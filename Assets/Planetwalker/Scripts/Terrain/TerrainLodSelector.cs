using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Adaptive terrain limits and LOD tuning (V0.2.0A). Diagnostic, runtime-only settings: never archived, never
    /// part of a world's identity, never trigger a regeneration.
    /// </summary>
    [Serializable]
    public sealed class AdaptiveTerrainSettings
    {
        [Tooltip("Quads per patch edge (fixed, independent of the quadtree depth). Range [2, 128].")]
        [Range(PatchMeshBuilder.MinResolution, PatchMeshBuilder.MaxResolution)]
        public int patchResolution = 16;

        [Tooltip("Coarsest level of the covering (0 = the six cube faces).")]
        [Range(0, 6)]
        public int minLevel = 0;

        [Tooltip("Deepest level; also limited by exact lattice coordinates (20 at 16 quads per patch).")]
        [Range(0, PlanetPatchId.MaxLevel)]
        public int maxLevel = 16;

        [Tooltip("Refine while the projected geometric error exceeds this many pixels.")]
        [Range(0.5f, 64f)]
        public float maxScreenError = 4f;

        [Tooltip("Fraction of the threshold below which an already refined patch is merged again (oscillation guard).")]
        [Range(0f, 0.9f)]
        public float hysteresis = 0.25f;

        [Tooltip("Most leaf patches in a selection; above it the covering stays coarser (never incomplete).")]
        [Range(6, 8192)]
        public int maxLeafPatches = 512;

        [Tooltip("Most patch meshes kept by the diagnostic preview (least recently used are destroyed).")]
        [Range(6, 8192)]
        public int maxPatchMeshes = 768;

        [Tooltip("Assumed steepest terrain slope (rise / run) for the displacement error bound. 1 = 45 degrees.")]
        [Range(0.1f, 10f)]
        public float assumedMaxSlope = 1f;

        [Tooltip("Error multiplier of patches entirely outside the view frustum (when the view has one). 1 = no frustum weighting. They still refine, only later: never a hole.")]
        [Range(0.05f, 1f)]
        public float offscreenErrorScale = 0.25f;

        /// <summary>Deepest level actually used: the setting, capped by exact lattice coordinates.</summary>
        public int EffectiveMaxLevel => Mathf.Clamp(maxLevel, EffectiveMinLevel, CubeSphereCoordinates.MaxLevelForResolution(patchResolution));

        /// <summary>Coarsest level actually used: the setting, lowered until its full covering fits the budget.</summary>
        public int EffectiveMinLevel
        {
            get
            {
                int level = Mathf.Clamp(minLevel, 0, CubeSphereCoordinates.MaxLevelForResolution(patchResolution));
                while (level > 0 && 6L << (2 * level) > maxLeafPatches)
                {
                    level--;
                }
                return level;
            }
        }

        public AdaptiveTerrainSettings Clone() => (AdaptiveTerrainSettings)MemberwiseClone();

        public bool SameAs(AdaptiveTerrainSettings other)
        {
            return other != null && patchResolution == other.patchResolution && minLevel == other.minLevel && maxLevel == other.maxLevel
                && maxScreenError == other.maxScreenError && hysteresis == other.hysteresis && maxLeafPatches == other.maxLeafPatches
                && maxPatchMeshes == other.maxPatchMeshes && assumedMaxSlope == other.assumedMaxSlope
                && offscreenErrorScale == other.offscreenErrorScale;
        }
    }

    /// <summary>Camera inputs of a LOD selection, in the planet's object space (rendering units).</summary>
    public struct TerrainLodView
    {
        public Vector3 CameraPosition;
        public float VerticalFieldOfView;
        public int ViewportHeight;
        /// <summary>Optional view frustum in the planet's object space (6 planes, normals inward), or null.</summary>
        public Plane[] Frustum;

        public TerrainLodView(Vector3 cameraPosition, float verticalFieldOfView, int viewportHeight)
        {
            CameraPosition = cameraPosition;
            VerticalFieldOfView = verticalFieldOfView;
            ViewportHeight = viewportHeight;
            Frustum = null;
        }

        /// <summary>Pixels per unit of size at unit distance.</summary>
        public float PixelsPerRadian => ViewportHeight / (2f * Mathf.Tan(Mathf.Clamp(VerticalFieldOfView, 1f, 179f) * 0.5f * Mathf.Deg2Rad));
    }

    /// <summary>
    /// A complete, non-overlapping, 2:1-balanced covering of the planet by leaf patches (V0.2.0A), with its
    /// statistics. Owned and reused by its <see cref="TerrainLodSelector"/>: valid until the next selection.
    /// </summary>
    public sealed class TerrainLodSelection : IPatchCovering
    {
        internal readonly HashSet<PlanetPatchId> LeafSet = new HashSet<PlanetPatchId>();
        internal readonly List<PlanetPatchId> LeafList = new List<PlanetPatchId>();

        /// <summary>Leaves sorted by key (deterministic order).</summary>
        public IReadOnlyList<PlanetPatchId> Leaves => LeafList;
        public int Count => LeafList.Count;
        public bool IsLeaf(PlanetPatchId id) => LeafSet.Contains(id);

        /// <summary>The leaf containing a patch region (itself or an ancestor), or false when that region is subdivided.</summary>
        public bool TryGetCoveringLeaf(PlanetPatchId id, out PlanetPatchId leaf)
        {
            for (PlanetPatchId p = id; ; p = p.Parent)
            {
                if (LeafSet.Contains(p))
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

        /// <summary>What lies across an edge of a leaf (balanced covering: equal, one coarser, or finer leaves).</summary>
        public EdgeTransition Transition(PlanetPatchId leaf, PatchDirection direction)
        {
            PlanetPatchId neighbor = PatchNeighbors.Neighbor(leaf, direction);
            if (TryGetCoveringLeaf(neighbor, out PlanetPatchId cover))
            {
                return cover.Level == leaf.Level ? EdgeTransition.Equal : EdgeTransition.Coarser;
            }
            return EdgeTransition.Finer;
        }

        public int MaxLevel { get; internal set; }
        public int MinLevel { get; internal set; }
        /// <summary>True when the leaf budget stopped a refinement that the error asked for.</summary>
        public bool BudgetLimited { get; internal set; }
        /// <summary>True when the maximum level stopped a refinement that the error asked for.</summary>
        public bool DepthLimited { get; internal set; }
        public int Splits { get; internal set; }
        /// <summary>Splits made only to keep neighbors within one level (2:1 balance).</summary>
        public int BalanceSplits { get; internal set; }
        public int Budget { get; internal set; }
        public double Milliseconds { get; internal set; }
        /// <summary>Incremented whenever the leaf set changes.</summary>
        public int Version { get; internal set; }
    }

    /// <summary>
    /// Deterministic screen-space-error quadtree selection over the six cube faces (V0.2.0A).
    /// </summary>
    /// <remarks>
    /// Error model (documented approximation): a patch at level L with r quads per edge has a vertex spacing of at
    /// most s = 1.25 R (pi / 2) / (r 2^L) (the 1.25 bounds the cube-sphere spacing variation). Its geometric error,
    /// the largest distance between its triangles and the true surface, is bounded by
    /// e = min(relief, slope * s) + s^2 / (8 R): terrain displacement between vertices (at most the assumed
    /// steepest slope times the spacing, never more than the planet's whole relief range) plus the sphere's own
    /// curvature sagitta. The projected error is e * K / d, with K = viewport height / (2 tan(fov / 2)) and d the
    /// distance from the camera to the patch's bounding sphere (which contains its whole relief). Patches entirely
    /// beyond the horizon (also accounting for the highest possible terrain) have no visible error and are never
    /// refined: no wasted budget behind the planet or at the horizon. No frustum culling: when the view carries a
    /// frustum (V0.2.0B), patches entirely outside it only have their error scaled by
    /// <see cref="AdaptiveTerrainSettings.offscreenErrorScale"/> — they refine later, so the budget goes to what is
    /// seen first, and the covering stays complete (turning the camera never reveals a hole).
    /// Patch center directions and angular radii (camera independent) are cached.
    /// Refinement: the leaf with the largest projected error is split first (ties by patch key), until no leaf
    /// exceeds the threshold, the depth limit is reached, or the budget is exhausted. A split first splits any
    /// coarser edge neighbor (2:1 balance, so edges only ever meet one level apart); a split that would exceed the
    /// budget is not made and refinement stops: the covering stays complete and coarser. Hysteresis: a patch that
    /// was split in the previous selection stays split while its error exceeds threshold * (1 - hysteresis), and
    /// candidates are ranked by error relative to their own threshold, so previously split patches also win when
    /// the budget runs out (no churn of the last splits under tiny camera moves).
    /// Same inputs and same previous selection: same output (no time or randomness involved).
    /// </remarks>
    public sealed class TerrainLodSelector
    {
        struct Candidate
        {
            public float Error;
            public PlanetPatchId Id;
        }

        readonly TerrainLodSelection selection = new TerrainLodSelection();
        readonly HashSet<PlanetPatchId> splitPatches = new HashSet<PlanetPatchId>();
        readonly HashSet<PlanetPatchId> previousSplit = new HashSet<PlanetPatchId>();
        readonly HashSet<PlanetPatchId> planned = new HashSet<PlanetPatchId>();
        readonly List<PlanetPatchId> forced = new List<PlanetPatchId>();
        readonly List<PlanetPatchId> previousLeaves = new List<PlanetPatchId>();
        Candidate[] heap = new Candidate[256];
        int heapCount;
        readonly float[] levelError = new float[PlanetPatchId.MaxLevel + 1];

        // Per-selection inputs.
        TerrainElevationSampler sampler;
        AdaptiveTerrainSettings settings;
        Vector3 camera;
        float cameraDistance;
        Vector3 cameraDirection;
        float pixelsPerRadian;
        float horizonCos;
        bool cameraInsideRelief;
        int maxLevel;
        Plane[] frustum;

        // Camera-independent patch spheres (center direction, angular radius): the costly part of an error
        // estimate, cached. Bounded: cleared when it outgrows the limit (identical values are recomputed).
        const int SphereCacheLimit = 32768;
        readonly Dictionary<ulong, Vector4> sphereCache = new Dictionary<ulong, Vector4>(4096);

        public TerrainLodSelector(AdaptiveTerrainSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public AdaptiveTerrainSettings Settings { get; set; }

        /// <summary>The last selection (empty before the first).</summary>
        public TerrainLodSelection Current => selection;

        /// <summary>Forgets the previous selection (hysteresis history), e.g. for a new planet.</summary>
        public void Reset()
        {
            previousSplit.Clear();
            previousLeaves.Clear();
            selection.LeafSet.Clear();
            selection.LeafList.Clear();
            selection.Version++;
        }

        /// <summary>Geometric error bound (rendering units) of a patch at a level, for the current planet.</summary>
        public static float GeometricError(TerrainElevationSampler planet, AdaptiveTerrainSettings settings, int level)
        {
            float spacing = 1.25f * planet.Radius * Mathf.PI * 0.5f / ((float)settings.patchResolution * (1 << level));
            float relief = planet.MaxRadius - planet.MinRadius;
            return Mathf.Min(relief, settings.assumedMaxSlope * spacing) + spacing * spacing / (8f * planet.Radius);
        }

        /// <summary>Selects the covering for a camera. Reuses its buffers: no steady-state allocation.</summary>
        public TerrainLodSelection Select(TerrainElevationSampler planet, in TerrainLodView view)
        {
            long start = Stopwatch.GetTimestamp(); // (a Stopwatch instance would allocate)
            sampler = planet ?? throw new ArgumentNullException(nameof(planet));
            settings = Settings;
            int minLevel = settings.EffectiveMinLevel;
            maxLevel = settings.EffectiveMaxLevel;
            for (int level = 0; level <= maxLevel; level++)
            {
                levelError[level] = GeometricError(planet, settings, level);
            }
            camera = view.CameraPosition;
            cameraDistance = camera.magnitude;
            cameraDirection = cameraDistance > 1e-9f ? camera / cameraDistance : Vector3.up;
            pixelsPerRadian = view.PixelsPerRadian;
            frustum = view.Frustum != null && view.Frustum.Length == 6 && settings.offscreenErrorScale < 1f ? view.Frustum : null;
            cameraInsideRelief = cameraDistance <= planet.MaxRadius;
            if (!cameraInsideRelief)
            {
                // A point of the terrain can be seen when its angle from the camera direction is at most
                // acos(Rmin / D) + acos(Rmin / Rmax) (horizon of the lowest ground, plus the highest summit).
                float horizon = Mathf.Acos(Mathf.Clamp(planet.MinRadius / cameraDistance, -1f, 1f))
                    + Mathf.Acos(Mathf.Clamp(planet.MinRadius / planet.MaxRadius, -1f, 1f));
                horizonCos = Mathf.Cos(Mathf.Min(horizon, Mathf.PI));
            }

            selection.LeafSet.Clear();
            splitPatches.Clear();
            heapCount = 0;
            int budget = Math.Max(settings.maxLeafPatches, 6);
            bool budgetLimited = false, depthLimited = false;
            int splits = 0, balanceSplits = 0;

            // Uniform start at the coarsest level (fits the budget by construction).
            int size = 1 << minLevel;
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var id = new PlanetPatchId(face, minLevel, x, y);
                        selection.LeafSet.Add(id);
                        Consider(id, ref depthLimited);
                    }
                }
            }
            while (heapCount > 0)
            {
                Candidate top = Pop();
                if (!selection.LeafSet.Contains(top.Id))
                {
                    continue; // already split as a balance requirement
                }
                forced.Clear();
                planned.Clear();
                CollectForced(top.Id);
                int cost = 3 * (forced.Count + 1);
                if (selection.LeafSet.Count + cost > budget)
                {
                    budgetLimited = true;
                    break; // stay coarser everywhere from here on: complete covering, budget respected
                }
                foreach (PlanetPatchId f in forced)
                {
                    Split(f, ref depthLimited);
                    balanceSplits++;
                }
                Split(top.Id, ref depthLimited);
                splits++;
            }

            // Publish (sorted, deterministic) and remember which patches were split (hysteresis).
            selection.LeafList.Clear();
            foreach (PlanetPatchId leaf in selection.LeafSet)
            {
                selection.LeafList.Add(leaf); // (AddRange would allocate a temporary array on Mono)
            }
            selection.LeafList.Sort();
            bool changed = selection.LeafList.Count != previousLeaves.Count;
            for (int i = 0; !changed && i < previousLeaves.Count; i++)
            {
                changed = previousLeaves[i] != selection.LeafList[i];
            }
            if (changed)
            {
                selection.Version++;
                previousLeaves.Clear();
                previousLeaves.AddRange(selection.LeafList);
            }
            previousSplit.Clear();
            foreach (PlanetPatchId id in splitPatches)
            {
                previousSplit.Add(id); // (UnionWith would box an enumerator)
            }

            int max = 0, min = int.MaxValue;
            foreach (PlanetPatchId id in selection.LeafList)
            {
                max = Math.Max(max, id.Level);
                min = Math.Min(min, id.Level);
            }
            selection.MaxLevel = max;
            selection.MinLevel = min;
            selection.BudgetLimited = budgetLimited;
            selection.DepthLimited = depthLimited;
            selection.Splits = splits;
            selection.BalanceSplits = balanceSplits;
            selection.Budget = budget;
            selection.Milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return selection;
        }

        /// <summary>Projected error in pixels of a patch for the current camera (0 when entirely beyond the horizon).</summary>
        public float ScreenError(PlanetPatchId id)
        {
            if (!sphereCache.TryGetValue(id.Key, out Vector4 sphere))
            {
                if (sphereCache.Count >= SphereCacheLimit)
                {
                    sphereCache.Clear();
                }
                Vector3 direction = CubeSphereCoordinates.CenterDirection(id);
                sphere = new Vector4(direction.x, direction.y, direction.z, CubeSphereCoordinates.AngularRadius(id));
                sphereCache.Add(id.Key, sphere);
            }
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float angularRadius = sphere.w;
            if (!cameraInsideRelief)
            {
                float cosToCamera = Vector3.Dot(center, cameraDirection);
                // Visible region: angle(center, camera) - angularRadius <= horizon.
                float angle = Mathf.Acos(Mathf.Clamp(cosToCamera, -1f, 1f)) - angularRadius;
                if (angle > 0f && Mathf.Cos(angle) < horizonCos)
                {
                    return 0f;
                }
            }
            float midRadius = 0.5f * (sampler.MinRadius + sampler.MaxRadius);
            Vector3 c = center * midRadius;
            float cosA = Mathf.Cos(angularRadius);
            float rMin = sampler.MinRadius, rMax = sampler.MaxRadius;
            // Farthest patch point from c: a boundary direction at the low or high radius.
            float boundSq = Mathf.Max(rMin * rMin + midRadius * midRadius - 2f * rMin * midRadius * cosA,
                rMax * rMax + midRadius * midRadius - 2f * rMax * midRadius * cosA);
            float boundingRadius = Mathf.Sqrt(Mathf.Max(boundSq, 0f));
            float distance = Mathf.Max((camera - c).magnitude - boundingRadius, sampler.Radius * 1e-7f);
            float error = levelError[id.Level] * pixelsPerRadian / distance;
            if (frustum != null)
            {
                for (int p = 0; p < 6; p++)
                {
                    if (frustum[p].GetDistanceToPoint(c) < -boundingRadius)
                    {
                        return error * settings.offscreenErrorScale; // entirely outside the view: refined later
                    }
                }
            }
            return error;
        }

        void Consider(PlanetPatchId id, ref bool depthLimited)
        {
            float error = ScreenError(id);
            float threshold = settings.maxScreenError * (previousSplit.Contains(id) ? 1f - settings.hysteresis : 1f);
            if (error <= threshold)
            {
                return;
            }
            if (id.Level >= maxLevel)
            {
                depthLimited = true;
                return;
            }
            // Priority = error relative to the patch's own threshold: previously split patches rank higher, so the
            // hysteresis also keeps a budget-limited covering stable.
            Push(new Candidate { Error = error / Mathf.Max(threshold, 1e-6f), Id = id });
        }

        void Split(PlanetPatchId id, ref bool depthLimited)
        {
            selection.LeafSet.Remove(id);
            splitPatches.Add(id);
            for (int c = 0; c < 4; c++)
            {
                PlanetPatchId child = id.Child(c);
                selection.LeafSet.Add(child);
                Consider(child, ref depthLimited);
            }
        }

        /// <summary>Coarser edge neighbors that must be split before <paramref name="id"/> (recursively, dependencies first).</summary>
        void CollectForced(PlanetPatchId id)
        {
            for (int d = 0; d < 4; d++)
            {
                PlanetPatchId neighbor = PatchNeighbors.Neighbor(id, (PatchDirection)d);
                if (!selection.TryGetCoveringLeaf(neighbor, out PlanetPatchId cover) || cover.Level >= id.Level || planned.Contains(cover))
                {
                    continue;
                }
                planned.Add(cover);
                CollectForced(cover);
                forced.Add(cover);
            }
        }

        // ------------------------------------------------------------------ Max-heap (error desc, key asc)

        static bool Before(in Candidate a, in Candidate b) => a.Error > b.Error || (a.Error == b.Error && a.Id.Key < b.Id.Key);

        void Push(Candidate candidate)
        {
            if (heapCount == heap.Length)
            {
                Array.Resize(ref heap, heap.Length * 2);
            }
            int i = heapCount++;
            heap[i] = candidate;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Before(heap[i], heap[parent]))
                {
                    break;
                }
                (heap[i], heap[parent]) = (heap[parent], heap[i]);
                i = parent;
            }
        }

        Candidate Pop()
        {
            Candidate top = heap[0];
            heap[0] = heap[--heapCount];
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, best = i;
                if (left < heapCount && Before(heap[left], heap[best])) best = left;
                if (right < heapCount && Before(heap[right], heap[best])) best = right;
                if (best == i) break;
                (heap[i], heap[best]) = (heap[best], heap[i]);
                i = best;
            }
            return top;
        }
    }
}
