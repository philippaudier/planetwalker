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

        public AdaptiveSurfaceSnapshot(int generation, PlanetInstance planet, NaturalSurface natural, PlanetColorRamp ramp, bool linearColors,
            int resolution, PlanetDebugView debugView = PlanetDebugView.None)
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
            DebugView = debugView;
            Resolution = resolution;
            Topology = PatchTopology.For(resolution);
            Geology = new SurfaceGeology(planet, natural);
        }

        /// <summary>Surface geology of the planet (V0.2.0E): per-vertex geological characteristics, cached per thread.</summary>
        public SurfaceGeology Geology { get; }

        /// <summary>
        /// Width, in vertex rows, of the band along a patch edge where the normal blends towards the coarser level's
        /// normal when that edge is stitched to a coarser neighbor (V0.2.0E LOD seam fix; the blend is done when drawing).
        /// </summary>
        public int NormalMorphRows => Math.Max(1, Resolution / 8);

        public int Generation { get; }
        public PlanetInstance Planet { get; }
        public TerrainElevationSampler Sampler { get; }
        /// <summary>Natural Planet colors, or null for the classic elevation palette.</summary>
        public NaturalSurface Natural { get; }
        public PlanetColorRamp Ramp { get; }
        public bool LinearColors { get; }
        /// <summary>None, or a surface detail debug view (V0.2.0C) coloring the patches by detail layer.</summary>
        public PlanetDebugView DebugView { get; }
        public int Resolution { get; }
        public PatchTopology Topology { get; }

        /// <summary>
        /// Angular spacing of the finite-difference normals of a level: the level's own vertex spacing, like the
        /// legacy faces (V0.2.0C). The samples are band-limited to it, so the normal is that of the relief the
        /// patch resolves: no aliasing grain at coarse levels, every detail octave shaded once it is resolved.
        /// Vertices shared by two levels get normals of two adjacent band limits, which differ by the one octave
        /// between them (a bounded, sub-threshold lighting difference; V0.2.0B's fixed spacing would hide every
        /// detail octave finer than R / 160).
        /// </summary>
        public float NormalAngle(int level) => Mathf.PI * 0.5f / ((float)Resolution * (1 << level));

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
            CoarseNormals = new Vector3[vertexCount];
            Grid = new Vector4[vertexCount];
            Geology = new Vector4[vertexCount];
            Flow = new Vector4[vertexCount];
            RockTints = new Color[vertexCount];
            RockFamily = new float[vertexCount];
            Cover = new Vector4[vertexCount];
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
        /// <summary>Normal of the next coarser level (band limit x 2) near the patch edges, the normal itself elsewhere (V0.2.0E).</summary>
        public readonly Vector3[] CoarseNormals;
        /// <summary>Grid position in the patch (i / r, j / r), height above sea level (m), reference slope (V0.2.0E).</summary>
        public readonly Vector4[] Grid;
        /// <summary>Bedrock, sediment, erosion, strata (V0.2.0E surface geology).</summary>
        public readonly Vector4[] Geology;
        /// <summary>Unit downslope direction (planet space) and concavity (V0.2.0E).</summary>
        public readonly Vector4[] Flow;
        /// <summary>Rock color of the geological province (vertex color space: linear when the colors are).</summary>
        public readonly Color[] RockTints;
        /// <summary>Rock family field (-1 .. 1; V0.2.0F material selection). The rock tints' alpha holds the sedimentary weight.</summary>
        public readonly float[] RockFamily;
        /// <summary>Ground cover (V0.2.0F.2): green grass, dry grass, forest floor, snow (0 .. 1).</summary>
        public readonly Vector4[] Cover;
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
        Vector3d[] planetPositions = Array.Empty<Vector3d>();
        Vector3d[] directions = Array.Empty<Vector3d>();

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
                directions = new Vector3d[gridCount];
                planetPositions = new Vector3d[gridCount];
            }
            TerrainElevationSampler sampler = snapshot.Sampler;
            PlanetHeightGenerator height = snapshot.Planet.Height;
            float seaLevel = snapshot.Planet.SeaLevel;
            int lattice = r << id.Level;
            int i0 = id.X * r, j0 = id.Y * r;
            double radius = sampler.Radius;
            // Positions in double (V0.2.0C): exact lattice direction x radius, relative to the float-rounded patch
            // origin the draw matrix will use, rounded to float once. Sub-meter precision at any depth.
            Vector3 origin = (CubeSphereCoordinates.CenterDirectionD(id) * radius).ToVector3();
            var originD = new Vector3d(origin);
            float normalAngle = snapshot.NormalAngle(id.Level);
            SurfaceGeology geology = snapshot.Geology;
            int geologyLattice = SurfaceGeology.LatticeFor(lattice);
            double toGeology = (double)geologyLattice / lattice;
            int morphRows = snapshot.NormalMorphRows;
            double metersPerElevation = snapshot.Planet.Scale.ElevationToMeters(1.0);
            float minE = float.MaxValue, maxE = float.MinValue;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            int edge = r + 1;
            for (int j = 0; j <= r; j++)
            {
                for (int i = 0; i <= r; i++)
                {
                    int v = i + j * edge;
                    Vector3d precise = CubeSphereCoordinates.LatticeDirectionD(id.Face, i0 + i, j0 + j, lattice);
                    Vector3 direction = precise.ToVector3();
                    float elevation = height.GetElevation(precise);
                    Vector3d position = precise * (radius * (1.0 + elevation));
                    Vector3 normal = height.GetSurfaceNormal(precise, normalAngle);
                    Color color;
                    Vector4 cover;
                    if (snapshot.DebugView == PlanetDebugView.None && snapshot.Natural != null && elevation - seaLevel > 0f)
                    {
                        color = snapshot.Natural.Evaluate(direction, elevation, scores, pointScores, out cover);
                    }
                    else
                    {
                        color = snapshot.DebugView == PlanetDebugView.None
                            ? PlanetMeshBuilder.TerrainColor(snapshot.Natural, snapshot.Ramp, seaLevel, direction, elevation, normal, scores, pointScores)
                            : SurfaceDetailDebugColors.Evaluate(height, snapshot.DebugView, direction, elevation - seaLevel > 0f);
                        cover = CoverFromColor(color, elevation - seaLevel > 0f);
                    }
                    target.Cover[v] = cover;
                    directions[v] = precise;
                    planetPositions[v] = position;
                    Vector3 local = (position - originD).ToVector3();
                    target.Vertices[v] = local;
                    target.Normals[v] = normal;
                    target.Colors[v] = snapshot.LinearColors ? ToLinear(color) : color;
                    // LOD seam (V0.2.0E): near the edges, also the normal of the next coarser level (its band limit is
                    // exactly twice this one), the normal a coarser neighbor shows on the shared vertices.
                    int edgeRows = Math.Min(Math.Min(i, r - i), Math.Min(j, r - j));
                    target.CoarseNormals[v] = id.Level > 0 && edgeRows < morphRows ? height.GetSurfaceNormal(precise, normalAngle * 2f) : normal;
                    // Surface geology (V0.2.0E) from the global geology lattice: LOD independent.
                    geology.Sample(id.Face, (i0 + i) * toGeology, (j0 + j) * toGeology, geologyLattice, out GeologySample g);
                    target.Grid[v] = new Vector4((float)i / r, (float)j / r, (float)((elevation - seaLevel) * metersPerElevation), g.Slope);
                    target.Geology[v] = new Vector4(g.Bedrock, g.Sediment, g.Erosion, g.Strata);
                    target.Flow[v] = new Vector4(g.Downslope.x, g.Downslope.y, g.Downslope.z, g.Concavity);
                    Color tint = snapshot.LinearColors ? ToLinear(g.RockTint) : g.RockTint;
                    tint.a = g.Sedimentary; // data, not a color: never converted
                    target.RockTints[v] = tint;
                    target.RockFamily[v] = g.Family;
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
                    Vector3 local = (planetPositions[g] - directions[g] * skirt - originD).ToVector3();
                    target.Vertices[s] = local;
                    target.Normals[s] = target.Normals[g];
                    target.Colors[s] = target.Colors[g];
                    target.CoarseNormals[s] = target.CoarseNormals[g];
                    target.Grid[s] = target.Grid[g];
                    target.Geology[s] = target.Geology[g];
                    target.Flow[s] = target.Flow[g];
                    target.RockTints[s] = target.RockTints[g];
                    target.RockFamily[s] = target.RockFamily[g];
                    target.Cover[s] = target.Cover[g];
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

        /// <summary>
        /// Ground cover without the Natural Planet biomes (classic palette, under the sea): green grass from a greenish
        /// color, snow from a near-white one; nothing under the sea (bare sediment).
        /// </summary>
        public static Vector4 CoverFromColor(Color c, bool land)
        {
            if (!land)
            {
                return Vector4.zero;
            }
            float green = Mathf.Clamp01((c.g - c.r) * 6f) * 0.8f;
            float white = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float snow = Mathf.Clamp01((white - 0.6f) / 0.2f);
            return new Vector4(green * (1f - snow), 0f, 0f, snow * snow * (3f - 2f * snow));
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
