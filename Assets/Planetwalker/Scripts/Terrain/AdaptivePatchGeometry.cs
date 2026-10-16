using System;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Everything a patch build reads (V0.2.0B), frozen for one terrain generation: the planet (immutable), the
    /// Natural Planet surface (immutable data; evaluated with per-thread buffers), a copy of the color ramp and the
    /// rendering constants. Worker threads only ever see a snapshot, never the live, editable settings; a new
    /// snapshot (new <see cref="Generation"/>) is made whenever the planet or its appearance changes, and results of
    /// older generations are discarded.
    /// </summary>
    public sealed class AdaptiveSurfaceSnapshot
    {
        /// <summary>Normal sample spacing: a quarter face / this, the same at every level (seamless lighting).</summary>
        public const int DefaultNormalSampleResolution = 256;

        public AdaptiveSurfaceSnapshot(int generation, PlanetInstance planet, NaturalSurface natural, PlanetColorRamp ramp, bool linearColors,
            int resolution, int normalSampleResolution = DefaultNormalSampleResolution)
        {
            if ((resolution & 1) != 0 || resolution < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), "Adaptive patches need an even resolution.");
            }
            Generation = generation;
            Planet = planet ?? throw new ArgumentNullException(nameof(planet));
            Sampler = new TerrainElevationSampler(planet);
            Natural = natural;
            Ramp = (ramp ?? PlanetColorRamp.CreateDefault()).Clone();
            LinearColors = linearColors;
            Resolution = resolution;
            Topology = PatchTopology.For(resolution);
            NormalSampleAngle = Mathf.PI * 0.5f / Mathf.Max(1, normalSampleResolution);
        }

        public int Generation { get; }
        public PlanetInstance Planet { get; }
        public TerrainElevationSampler Sampler { get; }
        /// <summary>Natural Planet colors, or null for the classic elevation palette.</summary>
        public NaturalSurface Natural { get; }
        public PlanetColorRamp Ramp { get; }
        public bool LinearColors { get; }
        public int Resolution { get; }
        public PatchTopology Topology { get; }

        /// <summary>
        /// Angular spacing of the finite-difference normals. Independent of the patch level, so a vertex shared by
        /// two levels gets the same normal on both sides (no lighting seam); finer than any feature of the current
        /// elevation field (about R / 30), so refinement never loses relief shading.
        /// </summary>
        public float NormalSampleAngle { get; }

        /// <summary>Vertex spacing of a level (rendering units, face-center value).</summary>
        public float Spacing(int level) => Sampler.Radius * Mathf.PI * 0.5f / ((float)Resolution * (1 << level));

        /// <summary>Skirt depth of a level: well above float32 rounding at this radius, far below any visible size.</summary>
        public float SkirtDepth(int level) => Sampler.Radius * 2e-5f + 0.02f * Spacing(level);
    }

    /// <summary>CPU geometry of one patch, ready for upload (owned by the build pipeline, pooled).</summary>
    public sealed class PatchGeometryData
    {
        public PatchGeometryData(int vertexCount)
        {
            Vertices = new Vector3[vertexCount];
            Normals = new Vector3[vertexCount];
            Colors = new Color[vertexCount];
        }

        public PlanetPatchId Id;
        public int Generation;
        /// <summary>Planet-space origin of the vertices (patch center on the reference sphere).</summary>
        public Vector3 Origin;
        public Bounds LocalBounds;
        public float MinElevation;
        public float MaxElevation;
        public double BuildMilliseconds;
        public readonly Vector3[] Vertices;
        public readonly Vector3[] Normals;
        public readonly Color[] Colors;
    }

    /// <summary>
    /// Builds patch geometry from a snapshot (V0.2.0B). One instance per thread (it owns scratch buffers); no
    /// UnityEngine engine call, so it runs on worker threads. Positions: the existing elevation field on the
    /// patch lattice (<see cref="CubeSphereCoordinates"/>), relative to the patch origin (subtracted in double:
    /// exact up to the final rounding). Colors: <see cref="PlanetMeshBuilder.TerrainColor"/>, the legacy rule.
    /// </summary>
    public sealed class AdaptivePatchGeometryBuilder
    {
        readonly float[] scores = new float[Biomes.Count];
        readonly float[] pointScores = new float[Biomes.Count];
        Vector3[] planetPositions = Array.Empty<Vector3>();
        Vector3[] directions = Array.Empty<Vector3>();

        public void Build(AdaptiveSurfaceSnapshot snapshot, PlanetPatchId id, PatchGeometryData target)
        {
            long start = Stopwatch.GetTimestamp();
            PatchTopology topology = snapshot.Topology;
            int r = snapshot.Resolution;
            if ((long)r << id.Level > CubeSphereCoordinates.MaxLatticeSize)
            {
                throw new ArgumentOutOfRangeException(nameof(id), "Level " + id.Level + " is too deep for exact coordinates at resolution " + r + ".");
            }
            if (target.Vertices.Length != topology.VertexCount)
            {
                throw new ArgumentException("Geometry buffer sized for another resolution.", nameof(target));
            }
            int gridCount = topology.GridVertexCount;
            if (directions.Length != gridCount)
            {
                directions = new Vector3[gridCount];
                planetPositions = new Vector3[gridCount];
            }
            TerrainElevationSampler sampler = snapshot.Sampler;
            PlanetHeightGenerator height = snapshot.Planet.Height;
            float seaLevel = snapshot.Planet.SeaLevel;
            int lattice = r << id.Level;
            int i0 = id.X * r, j0 = id.Y * r;
            Vector3 origin = CubeSphereCoordinates.CenterDirection(id) * sampler.Radius;
            double ox = origin.x, oy = origin.y, oz = origin.z;
            float minE = float.MaxValue, maxE = float.MinValue;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            int edge = r + 1;
            for (int j = 0; j <= r; j++)
            {
                for (int i = 0; i <= r; i++)
                {
                    int v = i + j * edge;
                    Vector3 direction = CubeSphereCoordinates.LatticeDirection(id.Face, i0 + i, j0 + j, lattice);
                    float elevation = sampler.Elevation(direction);
                    Vector3 position = sampler.SurfacePoint(direction, elevation);
                    Vector3 normal = height.GetSurfaceNormal(direction, elevation, snapshot.NormalSampleAngle);
                    Color color = PlanetMeshBuilder.TerrainColor(snapshot.Natural, snapshot.Ramp, seaLevel, direction, elevation, normal, scores, pointScores);
                    directions[v] = direction;
                    planetPositions[v] = position;
                    Vector3 local = Relative(position, ox, oy, oz);
                    target.Vertices[v] = local;
                    target.Normals[v] = normal;
                    target.Colors[v] = snapshot.LinearColors ? ToLinear(color) : color;
                    minE = Math.Min(minE, elevation);
                    maxE = Math.Max(maxE, elevation);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }
            }
            float skirt = snapshot.SkirtDepth(id.Level);
            for (int d = 0; d < 4; d++)
            {
                for (int k = 0; k <= r; k++)
                {
                    PatchNeighbors.EdgeVertex((PatchDirection)d, k, r, out int i, out int j);
                    int g = i + j * edge;
                    int s = topology.SkirtIndex((PatchDirection)d, k);
                    Vector3 local = Relative(planetPositions[g] - directions[g] * skirt, ox, oy, oz);
                    target.Vertices[s] = local;
                    target.Normals[s] = target.Normals[g];
                    target.Colors[s] = target.Colors[g];
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            target.Id = id;
            target.Generation = snapshot.Generation;
            target.Origin = origin;
            target.LocalBounds = bounds;
            target.MinElevation = minE;
            target.MaxElevation = maxE;
            target.BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        static Vector3 Relative(Vector3 p, double ox, double oy, double oz)
        {
            return new Vector3((float)(p.x - ox), (float)(p.y - oy), (float)(p.z - oz));
        }

        /// <summary>sRGB to linear (the standard curve; managed, unlike Color.linear, so usable off the main thread).</summary>
        public static Color ToLinear(Color c)
        {
            return new Color(ToLinear(c.r), ToLinear(c.g), ToLinear(c.b), c.a);
        }

        static float ToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
