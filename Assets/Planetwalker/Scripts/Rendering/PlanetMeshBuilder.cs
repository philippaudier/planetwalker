using System.Collections.Generic;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Builds cube-sphere face meshes from a <see cref="PlanetInstance"/>. Vertex buffers
    /// are reused across faces and regenerations to avoid per-build allocations.
    /// </summary>
    public sealed class PlanetMeshBuilder
    {
        Vector3[] vertices;
        Vector3[] normals;
        Color[] colors;
        int[] triangles;
        int bufferResolution = -1;
        readonly List<Vector3> meshVertices = new List<Vector3>();
        readonly List<Vector3> meshNormals = new List<Vector3>();

        /// <summary>What vertex colors represent (terrain or a development view).</summary>
        public struct ColorContext
        {
            public PlanetDebugView View;
            /// <summary>Required by hydrology views.</summary>
            public HydrologyResult Hydrology;
            public float RiverThreshold;
            /// <summary>Required by the river network view.</summary>
            public RiverNetwork Rivers;
            /// <summary>Optional: ribbon diagnostics (unresolved mouths) for the river network view.</summary>
            public RiverRibbonBuilder Ribbons;
            /// <summary>Required by the lake views.</summary>
            public LakeSet Lakes;
            /// <summary>Required by the lake connectivity view.</summary>
            public LakeRiverLayer LakeLayer;
            /// <summary>Deepest lake of the planet (depth view scale).</summary>
            public float MaxLakeDepth;
            /// <summary>Lake surface built on this terrain mesh (shoreline view), or null.</summary>
            public LakeMeshBuilder LakeWater;
            /// <summary>Required by the climate views.</summary>
            public ClimateResult Climate;
            /// <summary>Winds view: color the heading instead of a neutral speed background.</summary>
            public bool WindDirectionColors;
            /// <summary>Required by the biome views.</summary>
            public BiomeResult Biomes;
            /// <summary>Natural Planet surface (artistic view), or null for the classic elevation palette.</summary>
            public NaturalSurface Natural;
            /// <summary>Required by the cloud views.</summary>
            public CloudDebugData Clouds;

            public ColorContext(PlanetDebugView view, HydrologyResult hydrology = null, float riverThreshold = 0f,
                RiverNetwork rivers = null, RiverRibbonBuilder ribbons = null)
            {
                View = view;
                Hydrology = hydrology;
                RiverThreshold = riverThreshold;
                Rivers = rivers;
                Ribbons = ribbons;
                Lakes = null;
                LakeLayer = null;
                MaxLakeDepth = 0f;
                LakeWater = null;
                Climate = null;
                WindDirectionColors = false;
                Biomes = null;
                Natural = null;
                Clouds = null;
            }

            public ColorContext WithLakes(LakeSet lakes, LakeRiverLayer layer)
            {
                ColorContext context = this;
                context.Lakes = lakes;
                context.LakeLayer = layer;
                context.MaxLakeDepth = 0f;
                for (int i = 0; lakes != null && i < lakes.Count; i++)
                {
                    context.MaxLakeDepth = Mathf.Max(context.MaxLakeDepth, lakes.GetLake(i).MaxDepth);
                }
                return context;
            }
        }

        /// <summary>Largest vertex distance from the planet center seen in the last build.</summary>
        public float LastMaxRadius { get; private set; }

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> as the displaced, colored face
        /// <paramref name="face"/> of the planet.
        /// </summary>
        public void BuildFace(Mesh mesh, int face, PlanetInstance planet, PlanetColorRamp ramp,
            PlanetDebugView debugView = PlanetDebugView.None)
        {
            BuildFace(mesh, face, planet, ramp, new ColorContext(debugView));
        }

        public void BuildFace(Mesh mesh, int face, PlanetInstance planet, PlanetColorRamp ramp, ColorContext context)
        {
            int resolution = planet.Definition.resolution;
            EnsureBuffers(resolution);

            float radius = planet.Radius;
            PlanetHeightGenerator height = planet.Height;
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

            // Normals are sampled at the angular vertex spacing so lighting matches the
            // resolved geometry.
            float sampleAngle = Mathf.PI * 0.5f / resolution;

            int edge = resolution + 1;
            float maxRadius = 0f;
            for (int y = 0; y <= resolution; y++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    int i = x + y * edge;
                    Vector3 direction = CubeSphereGenerator.GetSphereDirection(face, x, y, resolution);
                    float elevation = height.GetElevation(direction);
                    Vector3 normal = height.GetSurfaceNormal(direction, elevation, sampleAngle);
                    float vertexRadius = radius * (1f + elevation);

                    vertices[i] = direction * vertexRadius;
                    normals[i] = normal;
                    Color color = VertexColor(context, planet, ramp, direction, elevation, normal, resolution, face, x, y);
                    colors[i] = linear ? color.linear : color;
                    maxRadius = Mathf.Max(maxRadius, vertexRadius);
                }
            }
            LastMaxRadius = maxRadius;

            int vertexCount = CubeSphereGenerator.VerticesPerFace(resolution);
            mesh.Clear();
            mesh.name = "Planet Face " + CubeSphereGenerator.GetFaceName(face);
            mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices, 0, vertexCount);
            mesh.SetNormals(normals, 0, vertexCount);
            mesh.SetColors(colors, 0, vertexCount);
            mesh.SetTriangles(triangles, 0, CubeSphereGenerator.IndicesPerFace(resolution), 0, true);
        }

        /// <summary>
        /// Recomputes only the vertex colors of an already built face (debug view or river
        /// threshold change): positions and normals are read back from the mesh, so the
        /// terrain is neither resampled nor modified. Falls back to a full build if the mesh
        /// does not match the planet resolution.
        /// </summary>
        public void RecolorFace(Mesh mesh, int face, PlanetInstance planet, PlanetColorRamp ramp, ColorContext context)
        {
            int resolution = planet.Definition.resolution;
            int vertexCount = CubeSphereGenerator.VerticesPerFace(resolution);
            if (mesh.vertexCount != vertexCount)
            {
                BuildFace(mesh, face, planet, ramp, context);
                return;
            }
            EnsureBuffers(resolution);
            mesh.GetVertices(meshVertices);
            mesh.GetNormals(meshNormals);
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            float inverseRadius = 1f / planet.Radius;
            int edge = resolution + 1;
            for (int y = 0; y <= resolution; y++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    int i = x + y * edge;
                    Vector3 direction = CubeSphereGenerator.GetSphereDirection(face, x, y, resolution);
                    float elevation = meshVertices[i].magnitude * inverseRadius - 1f;
                    Color color = VertexColor(context, planet, ramp, direction, elevation, meshNormals[i], resolution, face, x, y);
                    colors[i] = linear ? color.linear : color;
                }
            }
            mesh.SetColors(colors, 0, vertexCount);
        }

        static readonly Color DebugOceanColor = new Color(0.03f, 0.05f, 0.14f);
        static readonly Color DebugCenterlineColor = new Color(0.95f, 0.2f, 0.12f);

        static Color VertexColor(ColorContext context, PlanetInstance planet, PlanetColorRamp ramp, Vector3 direction,
            float elevation, Vector3 normal, int meshResolution, int face, int x, int y)
        {
            float elevationAboveSea = elevation - planet.SeaLevel;
            PlanetHeightGenerator height = planet.Height;
            switch (context.View)
            {
                case PlanetDebugView.None:
                    return TerrainColor(context.Natural, ramp, planet.SeaLevel, direction, elevation, normal);
                case PlanetDebugView.ContinentalMask:
                {
                    float gray = Mathf.Lerp(0.12f, 1f, height.GetContinentalMask(direction));
                    return new Color(gray, gray, gray);
                }
                case PlanetDebugView.MountainProvinces:
                {
                    if (elevationAboveSea <= 0f)
                    {
                        return DebugOceanColor;
                    }
                    float gray = height.GetMountainInfluence(direction);
                    return new Color(gray, gray, gray);
                }
                case PlanetDebugView.MountainRanges:
                {
                    float gray = 0.08f + 0.5f * height.GetMountainInfluence(direction);
                    Color background = elevationAboveSea <= 0f ? DebugOceanColor : new Color(gray, gray, gray);
                    return Color.Lerp(background, DebugCenterlineColor, height.Mountains.GetCenterlineMask(direction));
                }
            }

            if (PlanetDebugViews.IsCloud(context.View))
            {
                return CloudDebugColors.Evaluate(context.Clouds, context.View, direction, elevationAboveSea > 0f);
            }
            if (PlanetDebugViews.IsBiome(context.View))
            {
                return context.Biomes != null ? BiomeDebugColors.Evaluate(context.Biomes, context.View, direction, elevation) : Color.magenta;
            }
            if (PlanetDebugViews.IsClimate(context.View))
            {
                return context.Climate != null ? ClimateDebugColors.Evaluate(context.Climate, context.View, direction, elevation, context.WindDirectionColors) : Color.magenta;
            }

            HydrologyResult hydrology = context.Hydrology;
            if (hydrology == null)
            {
                return Color.magenta;
            }
            int cell = hydrology.Grid.CellFromDirection(direction);
            // When grid cells are smaller than the vertex spacing, sample their neighbourhood
            // so one-cell wide drainage lines are not skipped between vertices.
            bool dilate = hydrology.Grid.Resolution > meshResolution;
            switch (context.View)
            {
                case PlanetDebugView.DrainageBasins:
                    return HydrologyDebugColors.Basin(hydrology, cell);
                case PlanetDebugView.FlowAccumulation:
                    return HydrologyDebugColors.Accumulation(hydrology, cell, dilate);
                case PlanetDebugView.FlowDirection:
                    return HydrologyDebugColors.FlowDirection(hydrology, cell);
                case PlanetDebugView.RiverCandidates:
                    return HydrologyDebugColors.RiverCandidates(hydrology, cell, context.RiverThreshold,
                        ramp.EvaluateElevation(elevationAboveSea), dilate);
                case PlanetDebugView.RiverNetwork:
                    return context.Rivers != null
                        ? HydrologyDebugColors.RiverNetwork(context.Rivers, cell, ramp.EvaluateElevation(elevationAboveSea), dilate, context.Ribbons)
                        : Color.magenta;
            }
            if (context.Lakes == null || context.Lakes.Hydrology != hydrology)
            {
                return Color.magenta;
            }
            Color terrain = ramp.EvaluateElevation(elevationAboveSea);
            switch (context.View)
            {
                case PlanetDebugView.LakeCandidates:
                    return LakeDebugColors.Candidates(context.Lakes, cell, terrain, dilate);
                case PlanetDebugView.LakeDepth:
                    return LakeDebugColors.Depth(context.Lakes, cell, terrain, context.MaxLakeDepth, dilate);
                case PlanetDebugView.LakeClassification:
                    return LakeDebugColors.Classification(context.Lakes, cell, terrain, dilate);
                case PlanetDebugView.LakeConnectivity:
                    return context.LakeLayer != null ? LakeDebugColors.Connectivity(context.LakeLayer, cell, terrain, dilate) : Color.magenta;
                case PlanetDebugView.LakeShoreline:
                {
                    // Flags of this render vertex in the lake surface built on this very mesh.
                    ShorelineFlags flags = context.LakeWater != null && context.LakeWater.BuiltResolution == meshResolution
                        ? context.LakeWater.GetVertexFlags(face, x, y) : ShorelineFlags.None;
                    return LakeDebugColors.Shoreline(context.Lakes, cell, terrain, flags);
                }
                default:
                    return Color.magenta;
            }
        }

        /// <summary>
        /// Terrain color of the default view (sRGB): Natural Planet on land, the elevation palette on the ocean floor
        /// (seen through the ocean) or everywhere when <paramref name="natural"/> is null. The single rule shared by
        /// the legacy faces and the adaptive patches (V0.2.0B); pass scratch buffers to call it off the main thread.
        /// </summary>
        public static Color TerrainColor(NaturalSurface natural, PlanetColorRamp ramp, float seaLevel, Vector3 direction, float elevation,
            Vector3 normal, float[] scores = null, float[] pointScores = null)
        {
            float elevationAboveSea = elevation - seaLevel;
            if (natural != null && elevationAboveSea > 0f)
            {
                return scores != null ? natural.Evaluate(direction, elevation, scores, pointScores) : natural.Evaluate(direction, elevation);
            }
            return ramp.Evaluate(elevationAboveSea, 1f - Vector3.Dot(normal, direction));
        }

        /// <summary>Builds an undisplaced unit cube-sphere (all six faces in one mesh).</summary>
        public static void BuildUnitSphere(Mesh mesh, int resolution)
        {
            int perFace = CubeSphereGenerator.VerticesPerFace(resolution);
            int indicesPerFace = CubeSphereGenerator.IndicesPerFace(resolution);
            var sphereVertices = new Vector3[perFace * CubeSphereGenerator.FaceCount];
            var sphereTriangles = new int[indicesPerFace * CubeSphereGenerator.FaceCount];
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                int offset = face * perFace;
                for (int y = 0; y <= resolution; y++)
                {
                    for (int x = 0; x <= resolution; x++)
                    {
                        sphereVertices[offset + x + y * (resolution + 1)] =
                            CubeSphereGenerator.GetSphereDirection(face, x, y, resolution);
                    }
                }
                CubeSphereGenerator.FillFaceTriangles(sphereTriangles, resolution, offset, face * indicesPerFace);
            }

            mesh.Clear();
            mesh.name = "Planet Ocean";
            mesh.indexFormat = sphereVertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = sphereVertices;
            // On a unit sphere the normal is the position.
            mesh.normals = sphereVertices;
            mesh.triangles = sphereTriangles;
            mesh.RecalculateBounds();
        }

        void EnsureBuffers(int resolution)
        {
            if (resolution == bufferResolution)
            {
                return;
            }
            int vertexCount = CubeSphereGenerator.VerticesPerFace(resolution);
            vertices = new Vector3[vertexCount];
            normals = new Vector3[vertexCount];
            colors = new Color[vertexCount];
            triangles = new int[CubeSphereGenerator.IndicesPerFace(resolution)];
            CubeSphereGenerator.FillFaceTriangles(triangles, resolution);
            bufferResolution = resolution;
        }
    }
}
