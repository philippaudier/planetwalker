using System;
using System.Diagnostics;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    /// <summary>Geometry facts of one built patch.</summary>
    public struct PatchGeometry
    {
        public PlanetPatchId Id;
        /// <summary>Object-space origin of the patch vertices (the patch center on the reference sphere).</summary>
        public Vector3 Origin;
        /// <summary>Bounds of the vertices relative to <see cref="Origin"/>.</summary>
        public Bounds LocalBounds;
        public float MinElevation;
        public float MaxElevation;
        public double Milliseconds;
    }

    /// <summary>
    /// Builds the mesh of one terrain patch (V0.2.0A): a fixed grid of <see cref="Resolution"/> x Resolution quads
    /// (independent of the quadtree depth) on the patch's lattice (<see cref="CubeSphereCoordinates"/>), each
    /// vertex sampled from the existing elevation field; same triangle pattern and outward winding as the existing
    /// face meshes; positions stored relative to the patch origin (precision at depth). No detail of its own.
    /// Buffers are reused across builds (no per-build allocation besides Unity's mesh upload).
    /// </summary>
    public sealed class PatchMeshBuilder
    {
        public const int MinResolution = 2;
        public const int MaxResolution = 128;

        readonly Vector3[] vertices;
        readonly Vector3[] normals;
        readonly Color32[] colors;
        readonly Vector3[] directions;
        readonly int[] triangles;
        readonly int[] gridLines;
        readonly Vector3[] wireVertices;
        readonly Color32[] wireColors;

        public PatchMeshBuilder(int resolution)
        {
            if (resolution < MinResolution || resolution > MaxResolution)
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Patch resolution must be in [" + MinResolution + ", " + MaxResolution + "].");
            }
            Resolution = resolution;
            int count = VertexCount;
            vertices = new Vector3[count];
            normals = new Vector3[count];
            colors = new Color32[count];
            directions = new Vector3[count];
            triangles = new int[IndexCount];
            CubeSphereGenerator.FillFaceTriangles(triangles, resolution);
            // Vertex grid (diagnostics): every row and column of quad edges, as line pairs.
            gridLines = new int[GridLineIndexCount];
            int k = 0, edge = resolution + 1;
            for (int a = 0; a <= resolution; a++)
            {
                for (int b = 0; b < resolution; b++)
                {
                    gridLines[k++] = b + a * edge;
                    gridLines[k++] = b + 1 + a * edge;
                    gridLines[k++] = a + b * edge;
                    gridLines[k++] = a + (b + 1) * edge;
                }
            }
            wireVertices = new Vector3[count];
            wireColors = new Color32[count];
        }

        public int Resolution { get; }
        public int VertexCount => (Resolution + 1) * (Resolution + 1);
        public int IndexCount => Resolution * Resolution * 6;

        /// <summary>GPU memory estimate of one patch mesh (positions, normals, colors, 16-bit indices), bytes.</summary>
        public long EstimatedMeshBytes => (long)VertexCount * (12 + 12 + 4) + (long)IndexCount * 2;

        public int GridLineIndexCount => 4 * Resolution * (Resolution + 1);

        /// <summary>GPU memory estimate of one vertex-grid line mesh (positions, colors, 16-bit indices), bytes.</summary>
        public long EstimatedGridBytes => (long)VertexCount * (12 + 4) + (long)GridLineIndexCount * 2;

        /// <summary>Vertices of the last build, relative to its origin (read-only view for tests and diagnostics).</summary>
        public Vector3[] Vertices => vertices;
        public Vector3[] Normals => normals;
        public int[] Triangles => triangles;
        /// <summary>Unit directions of the last build's vertices.</summary>
        public Vector3[] Directions => directions;

        /// <summary>Samples the patch into the internal buffers. <paramref name="tint"/> is a diagnostic vertex color.</summary>
        public PatchGeometry Build(PlanetPatchId id, TerrainElevationSampler sampler, Color tint)
        {
            var watch = Stopwatch.StartNew();
            int r = Resolution;
            int level = id.Level;
            if ((long)r << level > CubeSphereCoordinates.MaxLatticeSize)
            {
                throw new ArgumentOutOfRangeException(nameof(id), "Level " + level + " is too deep for exact coordinates at resolution " + r + ".");
            }
            int lattice = r << level;
            int i0 = id.X * r, j0 = id.Y * r;
            // Normals at the vertex spacing, like the existing mesh (angular spacing of a face grid of this lattice).
            float sampleAngle = Mathf.PI * 0.5f / lattice;
            Vector3 origin = CubeSphereCoordinates.CenterDirection(id) * sampler.Radius;
            float minE = float.MaxValue, maxE = float.MinValue;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            int edge = r + 1;
            for (int j = 0; j <= r; j++)
            {
                for (int i = 0; i <= r; i++)
                {
                    int v = i + j * edge;
                    Vector3 direction = CubeSphereCoordinates.LatticeDirection(id.Face, i0 + i, j0 + j, lattice);
                    float elevation = sampler.Elevation(direction);
                    Vector3 position = sampler.SurfacePoint(direction, elevation) - origin;
                    Vector3 normal = sampler.Normal(direction, elevation, sampleAngle);
                    directions[v] = direction;
                    vertices[v] = position;
                    normals[v] = normal;
                    float shade = Hillshade(direction, normal);
                    colors[v] = new Color(tint.r * shade, tint.g * shade, tint.b * shade, 1f);
                    minE = Mathf.Min(minE, elevation);
                    maxE = Mathf.Max(maxE, elevation);
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return new PatchGeometry
            {
                Id = id,
                Origin = origin,
                LocalBounds = bounds,
                MinElevation = minE,
                MaxElevation = maxE,
                Milliseconds = watch.Elapsed.TotalMilliseconds,
            };
        }

        /// <summary>
        /// Diagnostic relief shading, independent of the sun and of the position on the planet: a cartographic
        /// hillshade lit from the local north-west, 45 degrees up, exaggerated (planet-scale slopes are gentle). Flat
        /// ground reads 0.8; slopes facing the light are brighter, the others darker.
        /// </summary>
        public static float Hillshade(Vector3 up, Vector3 normal)
        {
            Vector3 reference = Mathf.Abs(up.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 north = (reference - up * Vector3.Dot(reference, up)).normalized;
            Vector3 west = Vector3.Cross(north, up);
            Vector3 light = (up + 0.7071f * (north + west)).normalized;
            float relief = Vector3.Dot(normal, light) - Vector3.Dot(up, light);
            return Mathf.Clamp(0.8f + 2.5f * relief, 0.3f, 1.2f);
        }

        /// <summary>
        /// Uploads the last build's vertex grid as lines, lifted slightly above the surface so it is not hidden by
        /// the patch's own triangles. Shows the triangle density of the patch.
        /// </summary>
        public void UploadGrid(Mesh mesh, PatchGeometry geometry, Color color, float lift)
        {
            Color32 c = color;
            for (int v = 0; v < VertexCount; v++)
            {
                wireVertices[v] = vertices[v] + directions[v] * lift;
                wireColors[v] = c;
            }
            mesh.Clear();
            mesh.name = "Adaptive Patch Grid " + geometry.Id;
            mesh.indexFormat = VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(wireVertices);
            mesh.SetColors(wireColors);
            mesh.SetIndices(gridLines, MeshTopology.Lines, 0, true);
        }

        /// <summary>Uploads the last build into a mesh (reused by the caller).</summary>
        public void Upload(Mesh mesh, PatchGeometry geometry)
        {
            mesh.Clear();
            mesh.name = "Terrain Patch " + geometry.Id;
            mesh.indexFormat = VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, true);
            mesh.bounds = geometry.LocalBounds;
        }
    }
}
