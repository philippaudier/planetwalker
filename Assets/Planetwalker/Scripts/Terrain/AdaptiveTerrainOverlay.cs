using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Lightweight ADAPTIVE TERRAIN DEBUG overlay (V0.2.0A): the outlines of the selected leaf patches as one line
    /// mesh draped over the existing planet (no second planet is drawn). Colors: the patch level (categorical
    /// palette, deliberately unlike the scientific ramps), white on cube-face boundaries, red on edges facing a
    /// coarser neighbor (the T-junctions V0.2.0B must stitch). Outline points are sampled from the existing
    /// elevation field once per patch and cached; the mesh is rebuilt only when the covering changes.
    /// </summary>
    public sealed class AdaptiveTerrainOverlay : IDisposable
    {
        /// <summary>Points per patch edge (segments).</summary>
        public const int EdgeSegments = 8;

        public static readonly Color FaceEdgeColor = new Color(1f, 1f, 1f, 1f);
        public static readonly Color TransitionEdgeColor = new Color(1f, 0.16f, 0.12f, 1f);

        static readonly Color[] LevelPalette =
        {
            new Color(0.30f, 0.45f, 1.00f), new Color(0.10f, 0.85f, 0.95f), new Color(0.20f, 0.95f, 0.45f),
            new Color(0.80f, 1.00f, 0.20f), new Color(1.00f, 0.85f, 0.10f), new Color(1.00f, 0.55f, 0.10f),
            new Color(1.00f, 0.35f, 0.75f), new Color(0.75f, 0.40f, 1.00f),
        };

        readonly Dictionary<PlanetPatchId, Vector3[]> outlines = new Dictionary<PlanetPatchId, Vector3[]>();
        readonly List<PlanetPatchId> stale = new List<PlanetPatchId>();
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Color32> colors = new List<Color32>();
        readonly List<int> indices = new List<int>();
        Mesh mesh;
        int builtVersion = -1;
        TerrainElevationSampler builtSampler;
        IPatchCovering builtCovering;

        /// <summary>Diagnostic color of a level (cycles every 8 levels).</summary>
        public static Color LevelColor(int level) => LevelPalette[level % LevelPalette.Length];

        public Mesh Mesh => mesh;

        /// <summary>Edges drawn red in the last build (fine side of a level transition).</summary>
        public int TransitionEdges { get; private set; }

        public int OutlineSamples { get; private set; }

        public long EstimatedBytes => mesh == null ? 0 : (long)mesh.vertexCount * 16 + (long)indices.Count * 4;

        /// <summary>Rebuilds the line mesh when the covering or the planet changed (cheap otherwise).</summary>
        public void Update(IPatchCovering selection, TerrainElevationSampler sampler)
        {
            if (builtSampler != sampler)
            {
                outlines.Clear();
                builtSampler = sampler;
                builtVersion = -1;
            }
            if (builtCovering != selection)
            {
                builtCovering = selection;
                builtVersion = -1;
            }
            if (selection.Version == builtVersion && mesh != null)
            {
                return;
            }
            builtVersion = selection.Version;
            if (mesh == null)
            {
                mesh = new Mesh { name = "Adaptive Terrain Overlay", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
            }
            // Bounded cache: forget outlines of patches that left a covering much smaller than the cache.
            if (outlines.Count > 2 * selection.Count + 64)
            {
                stale.Clear();
                foreach (PlanetPatchId id in outlines.Keys)
                {
                    if (!selection.IsLeaf(id))
                    {
                        stale.Add(id);
                    }
                }
                foreach (PlanetPatchId id in stale)
                {
                    outlines.Remove(id);
                }
            }

            vertices.Clear();
            colors.Clear();
            indices.Clear();
            int transitions = 0;
            foreach (PlanetPatchId leaf in selection.Leaves)
            {
                Vector3[] ring = Outline(leaf, sampler);
                for (int e = 0; e < 4; e++)
                {
                    var direction = (PatchDirection)e;
                    PlanetPatchId neighbor = PatchNeighbors.Neighbor(leaf, direction);
                    bool faceEdge = neighbor.Face != leaf.Face;
                    bool transition = selection.Transition(leaf, direction) == EdgeTransition.Coarser;
                    Color color = transition ? (faceEdge ? Color.Lerp(FaceEdgeColor, TransitionEdgeColor, 0.6f) : TransitionEdgeColor)
                        : faceEdge ? FaceEdgeColor : LevelColor(leaf.Level);
                    if (transition)
                    {
                        transitions++;
                    }
                    int start = vertices.Count;
                    int ringStart = RingStart(direction);
                    for (int k = 0; k <= EdgeSegments; k++)
                    {
                        vertices.Add(ring[(ringStart + k) % ring.Length]);
                        colors.Add(color);
                    }
                    for (int k = 0; k < EdgeSegments; k++)
                    {
                        indices.Add(start + k);
                        indices.Add(start + k + 1);
                    }
                }
            }
            TransitionEdges = transitions;
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetIndices(indices, MeshTopology.Lines, 0, true);
        }

        // Ring order: south edge west to east, east edge south to north, north edge east to west, west edge north to
        // south; EdgeSegments points per edge.
        static int RingStart(PatchDirection direction)
        {
            switch (direction)
            {
                case PatchDirection.South: return 0;
                case PatchDirection.East: return EdgeSegments;
                case PatchDirection.North: return 2 * EdgeSegments;
                default: return 3 * EdgeSegments;
            }
        }

        Vector3[] Outline(PlanetPatchId id, TerrainElevationSampler sampler)
        {
            if (outlines.TryGetValue(id, out Vector3[] ring))
            {
                return ring;
            }
            const int k = EdgeSegments;
            ring = new Vector3[4 * k];
            int lattice = k << id.Level;
            int i0 = id.X * k, j0 = id.Y * k;
            float radius = sampler.Radius;
            // Lift: above the ocean and the chord sagitta of one segment, so depth-tested lines stay visible.
            float spacing = 1.25f * radius * Mathf.PI * 0.5f / lattice;
            float lift = radius * 2e-4f + spacing * spacing / radius;
            float oceanRadius = sampler.Planet.OceanRadius;
            for (int p = 0; p < 4 * k; p++)
            {
                int i, j;
                if (p < k) { i = p; j = 0; }
                else if (p < 2 * k) { i = k; j = p - k; }
                else if (p < 3 * k) { i = 3 * k - p; j = k; }
                else { i = 0; j = 4 * k - p; }
                Vector3 direction = CubeSphereCoordinates.LatticeDirection(id.Face, i0 + i, j0 + j, lattice);
                float surface = Mathf.Max(sampler.SurfacePoint(direction).magnitude, oceanRadius);
                ring[p] = direction * (surface + lift);
                OutlineSamples++;
            }
            outlines.Add(id, ring);
            return ring;
        }

        /// <summary>Forces a rebuild on the next <see cref="Update"/> (new selector: versions restart).</summary>
        public void Invalidate()
        {
            builtVersion = -1;
        }

        public void Dispose()
        {
            if (mesh != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                mesh = null;
            }
            outlines.Clear();
            builtSampler = null;
            builtCovering = null;
            builtVersion = -1;
        }
    }
}
