using System;
using System.Collections.Generic;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Crack-free triangulation of an adaptive patch (V0.2.0B): one index buffer per stitch mask, shared by every
    /// patch of a resolution.
    /// </summary>
    /// <remarks>
    /// Vertex layout: the (r + 1)^2 grid (i + j (r + 1), i east along the face's A axis, j north along B), then four
    /// skirt rows of r + 1 vertices in <see cref="PatchDirection"/> order (East, North, West, South), vertex k of a
    /// skirt row lying below grid vertex <see cref="PatchNeighbors.EdgeVertex"/>(direction, k).
    /// Triangles: the grid is cut into 2 x 2 blocks, each a fan of 8 triangles around its center vertex (odd, odd).
    /// When an edge faces a neighbor one level coarser (mask bit 1 &lt;&lt; direction), the blocks along it skip the
    /// edge's odd vertices (fan of 7, or 6 at a corner shared by two such edges): that edge then consists exactly
    /// of the coarse neighbor's segments between even vertices, which are bit-identical on both sides — no
    /// T-junction, no crack, no degenerate or overlapping triangle. Coverings are 2:1 balanced, so one level is
    /// the only difference to handle. Skirts hang below each edge (following the same even / all vertices): they
    /// are not the continuity mechanism, only a guard against sub-pixel gaps from float32 rounding of
    /// patch-relative positions (shared vertices agree exactly in planet space, not after each patch subtracts its
    /// own origin). Winding: counter-clockwise in (i, j), i.e. outward like the planet faces; skirts face outward
    /// of the patch.
    /// </remarks>
    public sealed class PatchTopology
    {
        public const int MaskCount = 16;

        static readonly Dictionary<int, PatchTopology> Cache = new Dictionary<int, PatchTopology>();
        static readonly object CacheLock = new object();

        readonly int[][] variants = new int[MaskCount][];

        PatchTopology(int resolution)
        {
            Resolution = resolution;
            for (int mask = 0; mask < MaskCount; mask++)
            {
                variants[mask] = BuildIndices(resolution, mask);
            }
        }

        /// <summary>Shared topology of a resolution (even, at least 2). Thread safe.</summary>
        public static PatchTopology For(int resolution)
        {
            if (resolution < 2 || (resolution & 1) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Stitched patches need an even resolution of at least 2.");
            }
            lock (CacheLock)
            {
                if (!Cache.TryGetValue(resolution, out PatchTopology topology))
                {
                    topology = new PatchTopology(resolution);
                    Cache.Add(resolution, topology);
                }
                return topology;
            }
        }

        public int Resolution { get; }

        public int GridVertexCount => (Resolution + 1) * (Resolution + 1);

        public int VertexCount => GridVertexCount + 4 * (Resolution + 1);

        /// <summary>Triangle indices for a stitch mask (bit 1 &lt;&lt; direction: that edge faces a coarser neighbor). Do not modify.</summary>
        public int[] Indices(int mask) => variants[mask & (MaskCount - 1)];

        public int GridIndex(int i, int j) => i + j * (Resolution + 1);

        public int SkirtIndex(PatchDirection direction, int k) => GridVertexCount + (int)direction * (Resolution + 1) + k;

        static int[] BuildIndices(int r, int mask)
        {
            var indices = new List<int>(r * r * 6 + 8 * r * 3);
            int edge = r + 1;
            int Grid(int i, int j) => i + j * edge;
            bool east = (mask & (1 << (int)PatchDirection.East)) != 0;
            bool north = (mask & (1 << (int)PatchDirection.North)) != 0;
            bool west = (mask & (1 << (int)PatchDirection.West)) != 0;
            bool south = (mask & (1 << (int)PatchDirection.South)) != 0;
            var ring = new List<int>(8);
            int blocks = r / 2;
            for (int by = 0; by < blocks; by++)
            {
                for (int bx = 0; bx < blocks; bx++)
                {
                    int x0 = 2 * bx, y0 = 2 * by;
                    ring.Clear();
                    // Counter-clockwise ring from the south-west corner; edge midpoints dropped where stitched.
                    ring.Add(Grid(x0, y0));
                    if (!(south && by == 0))
                    {
                        ring.Add(Grid(x0 + 1, y0));
                    }
                    ring.Add(Grid(x0 + 2, y0));
                    if (!(east && bx == blocks - 1))
                    {
                        ring.Add(Grid(x0 + 2, y0 + 1));
                    }
                    ring.Add(Grid(x0 + 2, y0 + 2));
                    if (!(north && by == blocks - 1))
                    {
                        ring.Add(Grid(x0 + 1, y0 + 2));
                    }
                    ring.Add(Grid(x0, y0 + 2));
                    if (!(west && bx == 0))
                    {
                        ring.Add(Grid(x0, y0 + 1));
                    }
                    int center = Grid(x0 + 1, y0 + 1);
                    for (int k = 0; k < ring.Count; k++)
                    {
                        indices.Add(center);
                        indices.Add(ring[k]);
                        indices.Add(ring[(k + 1) % ring.Count]);
                    }
                }
            }

            // Skirts: one quad per edge segment (every vertex, or even vertices only on a stitched edge).
            int skirtBase = edge * edge;
            for (int d = 0; d < 4; d++)
            {
                var direction = (PatchDirection)d;
                int step = (mask & (1 << d)) != 0 ? 2 : 1;
                bool reversed = direction == PatchDirection.North || direction == PatchDirection.West;
                for (int k = 0; k < r; k += step)
                {
                    PatchNeighbors.EdgeVertex(direction, k, r, out int i0, out int j0);
                    PatchNeighbors.EdgeVertex(direction, k + step, r, out int i1, out int j1);
                    int g0 = Grid(i0, j0), g1 = Grid(i1, j1);
                    int s0 = skirtBase + d * edge + k, s1 = skirtBase + d * edge + k + step;
                    if (reversed)
                    {
                        indices.Add(g0); indices.Add(g1); indices.Add(s0);
                        indices.Add(g1); indices.Add(s1); indices.Add(s0);
                    }
                    else
                    {
                        indices.Add(g0); indices.Add(s0); indices.Add(g1);
                        indices.Add(g1); indices.Add(s0); indices.Add(s1);
                    }
                }
            }
            return indices.ToArray();
        }
    }
}
