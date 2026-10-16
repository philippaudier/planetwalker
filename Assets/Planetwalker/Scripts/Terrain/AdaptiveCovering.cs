using System;
using System.Collections.Generic;

namespace Planetwalker.Terrain
{
    /// <summary>A complete covering of the planet by leaf patches (the LOD target, or what is displayed).</summary>
    public interface IPatchCovering
    {
        /// <summary>Leaves sorted by key (deterministic order).</summary>
        IReadOnlyList<PlanetPatchId> Leaves { get; }
        int Count { get; }
        /// <summary>Incremented whenever the leaf set changes.</summary>
        int Version { get; }
        bool IsLeaf(PlanetPatchId id);
        bool TryGetCoveringLeaf(PlanetPatchId id, out PlanetPatchId leaf);
        EdgeTransition Transition(PlanetPatchId leaf, PatchDirection direction);
    }

    /// <summary>A patch mesh the displayed covering is waiting for, with its priority (larger first).</summary>
    public struct PatchRequest
    {
        public PlanetPatchId Id;
        public float Priority;
    }

    /// <summary>
    /// The displayed covering of the adaptive terrain (V0.2.0B). It follows the LOD target only through atomic
    /// replacements of one region: a split (a leaf replaced by its four children) once the four child meshes are
    /// ready, a merge (four sibling leaves replaced by their parent) once the parent mesh is ready. A replacement
    /// is applied only if the covering stays 2:1 balanced; otherwise it waits for the neighbor replacements the
    /// (balanced) target also requires, which are processed first (splits coarse to fine, merges fine to coarse),
    /// so the covering always converges. Invariants at every moment: complete, non-overlapping, 2:1 balanced —
    /// hence never a hole, never more than one level across an edge (stitchable), and a parent stays visible until
    /// all of its children can replace it. The first covering is the uniform minimum level, shown only once all
    /// of its patches are ready.
    /// </summary>
    public sealed class AdaptiveCovering : IPatchCovering
    {
        readonly HashSet<PlanetPatchId> leafSet = new HashSet<PlanetPatchId>();
        readonly List<PlanetPatchId> leafList = new List<PlanetPatchId>();
        readonly List<PlanetPatchId> splits = new List<PlanetPatchId>();
        readonly List<PlanetPatchId> merges = new List<PlanetPatchId>();
        readonly HashSet<PlanetPatchId> requested = new HashSet<PlanetPatchId>();
        readonly List<PlanetPatchId> scratch = new List<PlanetPatchId>();
        readonly Func<PlanetPatchId, bool> isLeaf;
        bool listDirty;
        Comparison<PlanetPatchId> coarseFirst, fineFirst;

        public AdaptiveCovering()
        {
            isLeaf = leafSet.Contains;
            coarseFirst = (a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : a.Key.CompareTo(b.Key);
            fineFirst = (a, b) => a.Level != b.Level ? b.Level.CompareTo(a.Level) : a.Key.CompareTo(b.Key);
        }

        public IReadOnlyList<PlanetPatchId> Leaves
        {
            get
            {
                if (listDirty)
                {
                    leafList.Clear();
                    foreach (PlanetPatchId id in leafSet)
                    {
                        leafList.Add(id);
                    }
                    leafList.Sort();
                    listDirty = false;
                }
                return leafList;
            }
        }

        public int Count => leafSet.Count;
        public int Version { get; private set; }

        /// <summary>True once a covering is displayed (complete by construction).</summary>
        public bool IsComplete => leafSet.Count > 0;

        /// <summary>True when the displayed covering equals the target of the last update.</summary>
        public bool Converged { get; private set; }

        public int TotalSplits { get; private set; }
        public int TotalMerges { get; private set; }
        public int LastSplits { get; private set; }
        public int LastMerges { get; private set; }

        public bool IsLeaf(PlanetPatchId id) => leafSet.Contains(id);

        public bool TryGetCoveringLeaf(PlanetPatchId id, out PlanetPatchId leaf)
        {
            for (PlanetPatchId p = id; ; p = p.Parent)
            {
                if (leafSet.Contains(p))
                {
                    leaf = p;
                    return true;
                }
                if (!p.HasParent)
                {
                    leaf = default;
                    return false;
                }
            }
        }

        public EdgeTransition Transition(PlanetPatchId leaf, PatchDirection direction)
        {
            PlanetPatchId neighbor = PatchNeighbors.Neighbor(leaf, direction);
            if (TryGetCoveringLeaf(neighbor, out PlanetPatchId cover))
            {
                return cover.Level == leaf.Level ? EdgeTransition.Equal : EdgeTransition.Coarser;
            }
            return EdgeTransition.Finer;
        }

        /// <summary>Stitch mask of a displayed leaf: bit 1 &lt;&lt; direction for each edge facing a coarser neighbor.</summary>
        public int StitchMask(PlanetPatchId leaf)
        {
            int mask = 0;
            for (int d = 0; d < 4; d++)
            {
                if (Transition(leaf, (PatchDirection)d) == EdgeTransition.Coarser)
                {
                    mask |= 1 << d;
                }
            }
            return mask;
        }

        /// <summary>Forgets the displayed covering (new planet generation): nothing is displayed until rebuilt.</summary>
        public void Reset()
        {
            leafSet.Clear();
            listDirty = true;
            Converged = false;
            Version++;
        }

        /// <summary>
        /// One step towards <paramref name="target"/>: applies every ready, balance-preserving replacement and lists
        /// the patch meshes still missing (each once, sorted by priority) into <paramref name="needed"/>.
        /// </summary>
        public void Update(IPatchCovering target, int initialLevel, Func<PlanetPatchId, bool> isReady, Func<PlanetPatchId, float> priority,
            List<PatchRequest> needed)
        {
            needed.Clear();
            requested.Clear();
            LastSplits = 0;
            LastMerges = 0;
            if (leafSet.Count == 0)
            {
                InitialCovering(initialLevel, isReady, priority, needed);
                Converged = false;
                SortNeeded(needed);
                return;
            }

            // A few passes: a split can enable a split of its children in the same frame when their meshes are cached.
            for (int pass = 0; pass < 8; pass++)
            {
                CollectCandidates(target);
                int applied = 0;
                foreach (PlanetPatchId leaf in splits)
                {
                    if (leafSet.Contains(leaf) && TrySplit(leaf, isReady, priority, needed))
                    {
                        applied++;
                    }
                }
                foreach (PlanetPatchId parent in merges)
                {
                    if (TryMerge(parent, isReady, priority, needed))
                    {
                        applied++;
                    }
                }
                if (applied == 0)
                {
                    break;
                }
            }
            CollectCandidates(target);
            Converged = splits.Count == 0 && merges.Count == 0;
            SortNeeded(needed);
        }

        void InitialCovering(int level, Func<PlanetPatchId, bool> isReady, Func<PlanetPatchId, float> priority, List<PatchRequest> needed)
        {
            int size = 1 << level;
            bool ready = true;
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var id = new PlanetPatchId(face, level, x, y);
                        if (!isReady(id))
                        {
                            ready = false;
                            Need(id, priority, needed);
                        }
                    }
                }
            }
            if (!ready)
            {
                return;
            }
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        leafSet.Add(new PlanetPatchId(face, level, x, y));
                    }
                }
            }
            listDirty = true;
            Version++;
        }

        void CollectCandidates(IPatchCovering target)
        {
            splits.Clear();
            merges.Clear();
            foreach (PlanetPatchId leaf in leafSet)
            {
                if (!target.TryGetCoveringLeaf(leaf, out PlanetPatchId cover))
                {
                    splits.Add(leaf); // the target is finer inside this leaf
                }
                else if (cover.Level < leaf.Level && (leaf.ChildIndex == 0) && AllChildrenAreLeaves(leaf.Parent))
                {
                    merges.Add(leaf.Parent); // the target is coarser here; one candidate per family
                }
            }
            splits.Sort(coarseFirst);
            merges.Sort(fineFirst);
        }

        bool AllChildrenAreLeaves(PlanetPatchId parent)
        {
            for (int c = 0; c < 4; c++)
            {
                if (!leafSet.Contains(parent.Child(c)))
                {
                    return false;
                }
            }
            return true;
        }

        bool TrySplit(PlanetPatchId leaf, Func<PlanetPatchId, bool> isReady, Func<PlanetPatchId, float> priority, List<PatchRequest> needed)
        {
            if (!leaf.CanSubdivide)
            {
                return false;
            }
            bool ready = true;
            for (int c = 0; c < 4; c++)
            {
                PlanetPatchId child = leaf.Child(c);
                if (!isReady(child))
                {
                    ready = false;
                    Need(child, priority, needed);
                }
            }
            if (!ready)
            {
                return false;
            }
            // Balance: a neighbor one level coarser would end two levels away from the children.
            for (int d = 0; d < 4; d++)
            {
                if (TryGetCoveringLeaf(PatchNeighbors.Neighbor(leaf, (PatchDirection)d), out PlanetPatchId cover) && cover.Level < leaf.Level)
                {
                    return false;
                }
            }
            leafSet.Remove(leaf);
            for (int c = 0; c < 4; c++)
            {
                leafSet.Add(leaf.Child(c));
            }
            listDirty = true;
            Version++;
            TotalSplits++;
            LastSplits++;
            return true;
        }

        bool TryMerge(PlanetPatchId parent, Func<PlanetPatchId, bool> isReady, Func<PlanetPatchId, float> priority, List<PatchRequest> needed)
        {
            if (!AllChildrenAreLeaves(parent))
            {
                return false;
            }
            if (!isReady(parent))
            {
                Need(parent, priority, needed);
                return false;
            }
            // Balance: leaves along the parent's edges must be at most one level finer than the parent.
            for (int d = 0; d < 4; d++)
            {
                scratch.Clear();
                PatchNeighbors.NeighborLeaves(parent, (PatchDirection)d, isLeaf, scratch);
                foreach (PlanetPatchId n in scratch)
                {
                    if (n.Level > parent.Level + 1)
                    {
                        return false;
                    }
                }
            }
            for (int c = 0; c < 4; c++)
            {
                leafSet.Remove(parent.Child(c));
            }
            leafSet.Add(parent);
            listDirty = true;
            Version++;
            TotalMerges++;
            LastMerges++;
            return true;
        }

        void Need(PlanetPatchId id, Func<PlanetPatchId, float> priority, List<PatchRequest> needed)
        {
            if (requested.Add(id))
            {
                needed.Add(new PatchRequest { Id = id, Priority = priority != null ? priority(id) : 0f });
            }
        }

        static readonly Comparison<PatchRequest> ByPriority = (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Id.Key.CompareTo(b.Id.Key);

        static void SortNeeded(List<PatchRequest> needed)
        {
            needed.Sort(ByPriority);
        }
    }
}
