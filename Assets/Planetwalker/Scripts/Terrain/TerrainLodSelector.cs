using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Generation;
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
        public int maxLeafPatches = 1024; // V0.2.0F.3: 512 before (more detail near the ground)

        [Tooltip("Most patch meshes kept by the diagnostic preview (least recently used are destroyed).")]
        [Range(6, 8192)]
        public int maxPatchMeshes = 2112; // room for two coverings at the default budget (2 x 1024 + 64)

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
    /// distance from the camera to the patch's bounding sphere. That sphere contains the patch's own relief
    /// (V0.2.0C fix): radial bounds sampled on a 3 x 3 grid of the patch, widened by what the samples can miss
    /// (the error bound at the sample spacing), clamped to the planet's range. With the planet-wide range instead
    /// (+-370 km on an exaggerated Earth-scale planet), every patch within a few hundred kilometers contained the
    /// camera once it flew below the highest possible summit: their distances all clamped to zero, their errors
    /// saturated to the same value, and the budget spread evenly over that whole disk instead of under the camera
    /// (zooming in lost the finest levels). Patches entirely
    /// beyond the horizon (also accounting for the highest possible terrain) have no visible error and are never
    /// refined: no wasted budget behind the planet or at the horizon. No frustum culling: when the view carries a
    /// frustum (V0.2.0B), patches entirely outside it only have their error scaled by
    /// <see cref="AdaptiveTerrainSettings.offscreenErrorScale"/> — they refine later, so the budget goes to what is
    /// seen first, and the covering stays complete (turning the camera never reveals a hole).
    /// Patch center directions and angular radii (camera independent) are cached.
    /// Refinement: the leaf with the largest projected error is split first (ties by patch key), until no leaf
    /// exceeds the threshold, the depth limit is reached, or the budget is exhausted. A split first splits any
    /// coarser edge neighbor (2:1 balance, so edges only ever meet one level apart); a split that would exceed the
    /// budget is skipped and refinement continues with the candidates that still fit (stops when not even a single
    /// split fits): the covering stays complete and coarser. Hysteresis: a patch that
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
        float cameraAltitude;
        int maxLevel;
        Plane[] frustum;

        // Camera-independent patch bounds (center direction, angular radius, radial relief range): the costly part
        // of an error estimate, cached per planet (sampler). Bounded: cleared when it outgrows the limit (identical
        // values are recomputed).
        const int SphereCacheLimit = 32768;
        /// <summary>Samples per patch edge of the relief bounds (3 x 3 grid: corners, edge midpoints, center).</summary>
        public const int BoundSamples = 3;

        struct PatchBound
        {
            public Vector3 Direction;
            public float AngularRadius;
            public float LowRadius;
            public float HighRadius;
        }

        readonly Dictionary<ulong, PatchBound> sphereCache = new Dictionary<ulong, PatchBound>(4096);
        TerrainElevationSampler boundsSampler;

        /// <summary>Number of patch relief bounds computed (sampled) since creation (diagnostics, tests).</summary>
        public int BoundComputeCount { get; private set; }

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

        /// <summary>
        /// Geometric error bound (rendering units) of a patch at a level, for the current planet. With surface
        /// detail (V0.2.0C) two terms are added, from the detail profile's frequency bands: the slope the
        /// octaves resolved at this level's spacing can add (typical, not peak: a peak bound over-refines
        /// everywhere by a constant factor without changing which patches win the budget), and the displacement
        /// of the octaves it cannot resolve (the aliasing error of its vertices).
        /// </summary>
        public static float GeometricError(TerrainElevationSampler planet, AdaptiveTerrainSettings settings, int level)
        {
            float angle = Mathf.PI * 0.5f / ((float)settings.patchResolution * (1 << level));
            float spacing = 1.25f * planet.Radius * angle;
            float relief = planet.MaxRadius - planet.MinRadius;
            float slope = settings.assumedMaxSlope;
            float unresolved = 0f;
            TerrainDetail detail = planet.Planet.Height.SurfaceDetail;
            if (detail != null)
            {
                slope += detail.Profile.TypicalSlope(angle);
                unresolved = detail.UnresolvedAmplitude(angle) * planet.Radius; // octaves and landform valleys (V0.2.0G.1)
            }
            return Mathf.Min(relief, slope * spacing + unresolved) + spacing * spacing / (8f * planet.Radius);
        }

        /// <summary>Selects the covering for a camera. Reuses its buffers: no steady-state allocation.</summary>
        public TerrainLodSelection Select(TerrainElevationSampler planet, in TerrainLodView view)
        {
            long start = Stopwatch.GetTimestamp(); // (a Stopwatch instance would allocate)
            sampler = planet ?? throw new ArgumentNullException(nameof(planet));
            settings = Settings;
            if (sampler != boundsSampler)
            {
                sphereCache.Clear(); // relief bounds belong to one planet
                boundsSampler = sampler;
            }
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
            // Height above the ground (or the sea) under the camera: the floor of every patch distance (see ScreenError).
            float groundUnder = planet.Radius * (1f + Mathf.Max(planet.Elevation(cameraDirection), planet.Planet.SeaLevel));
            cameraAltitude = Mathf.Max(cameraDistance - groundUnder, planet.Radius * 1e-7f);
            if (!cameraInsideRelief)
            {
                // A point of the terrain can be seen when its angle from the camera direction is at most
                // acos(Rmin / D) + acos(Rmin / Rmax) (horizon of the lowest ground, plus the highest summit).
                float horizon = Mathf.Acos(Mathf.Clamp(planet.MinRadius / cameraDistance, -1f, 1f))
                    + Mathf.Acos(Mathf.Clamp(planet.MinRadius / planet.MaxRadius, -1f, 1f));
                horizonCos = Mathf.Cos(Mathf.Min(horizon, Mathf.PI));
            }

            int budget = Math.Max(settings.maxLeafPatches, 6);
            bool budgetLimited = Refine(minLevel, budget, out bool depthLimited, out int splits, out int balanceSplits);

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

        /// <summary>
        /// The refinement: from the uniform coarsest covering, splits (largest relative error first) every patch whose
        /// error exceeds the threshold, within the budget. True when the budget stopped a split that the error asked for.
        /// </summary>
        bool Refine(int minLevel, int budget, out bool depthLimited, out int splits, out int balanceSplits)
        {
            selection.LeafSet.Clear();
            splitPatches.Clear();
            heapCount = 0;
            bool budgetLimited = false;
            depthLimited = false;
            splits = 0;
            balanceSplits = 0;

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
                    // This split (with its balance splits) does not fit: skip it, cheaper ones may still fit. Stopping
                    // at the first one that does not fit made the final leaf count, and so the last splits under the
                    // camera, depend on which candidate happened to be the expensive one (flicker while moving).
                    budgetLimited = true;
                    if (selection.LeafSet.Count + 3 > budget)
                    {
                        break; // not even a single split fits
                    }
                    continue;
                }
                foreach (PlanetPatchId f in forced)
                {
                    Split(f, ref depthLimited);
                    balanceSplits++;
                }
                Split(top.Id, ref depthLimited);
                splits++;
            }

            return budgetLimited;
        }

        /// <summary>Projected error in pixels of a patch for the current camera (0 when entirely beyond the horizon).</summary>
        public float ScreenError(PlanetPatchId id)
        {
            PatchBound bound = Bound(id);
            Vector3 center = bound.Direction;
            float angularRadius = bound.AngularRadius;
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
            float rMin = bound.LowRadius, rMax = bound.HighRadius;
            float midRadius = 0.5f * (rMin + rMax);
            Vector3 c = center * midRadius;
            float cosA = Mathf.Cos(angularRadius);
            // Farthest patch point from c: a boundary direction at the low or high radius.
            float boundSq = Mathf.Max(rMin * rMin + midRadius * midRadius - 2f * rMin * midRadius * cosA,
                rMax * rMax + midRadius * midRadius - 2f * rMax * midRadius * cosA);
            float boundingRadius = Mathf.Sqrt(Mathf.Max(boundSq, 0f));
            // Distance to the patch: to its bounding sphere, but never less than lower bounds that stay informative
            // when the camera is inside that sphere (low over the ground, the spheres of the neighboring patches
            // contain it too): the height above the patch's highest point, the horizontal gap to its footprint, and
            // the camera's height above the ground under it. Without them those errors saturated (a near-zero
            // distance): ties were broken by patch key (the refinement drifted to one side of the camera), and
            // whole chains of patches were split down to the deepest level whenever the camera crossed into a
            // bounding volume, then merged again (budget-limited flicker while moving).
            float sphereDistance = (camera - c).magnitude - boundingRadius;
            float heightAboveTop = cameraDistance - rMax;
            // atan2(|a x b|, a . b): precise at small angles (acos of a float dot near 1 has a ~0.35 mrad noise floor,
            // about 2 km on the ground: far larger than the finest patches, whose errors then flickered).
            float angleToCenter = Mathf.Atan2(Vector3.Cross(center, cameraDirection).magnitude, Vector3.Dot(center, cameraDirection));
            float horizontalGap = (angleToCenter - angularRadius) * rMin;
            // Horizontal gap and camera altitude combined (sqrt(gap^2 + altitude^2)): the patch under the camera is always
            // strictly the closest of its neighbors (with a plain max they tied, and ties went to the lower patch key).
            float gap = Mathf.Max(horizontalGap, 0f);
            float floor = Mathf.Sqrt(gap * gap + cameraAltitude * cameraAltitude);
            float distance = Mathf.Max(Mathf.Max(sphereDistance, heightAboveTop), floor);
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

        /// <summary>Radial relief range of a patch (rendering units), for the current planet: see the remarks.</summary>
        public Vector2 ReliefBounds(PlanetPatchId id)
        {
            PatchBound b = Bound(id);
            return new Vector2(b.LowRadius, b.HighRadius);
        }

        PatchBound Bound(PlanetPatchId id)
        {
            if (sphereCache.TryGetValue(id.Key, out PatchBound bound))
            {
                return bound;
            }
            if (sphereCache.Count >= SphereCacheLimit)
            {
                sphereCache.Clear();
            }
            bound.Direction = CubeSphereCoordinates.CenterDirection(id);
            bound.AngularRadius = CubeSphereCoordinates.AngularRadius(id);
            // Sampled elevations, band-limited to the sample spacing (octaves the grid cannot resolve are covered by
            // the margin below, not aliased into the samples).
            CubeSphereCoordinates.FaceBounds(id, out float aMin, out float bMin, out float aMax, out float bMax);
            float patchAngle = Mathf.PI * 0.5f / (1 << id.Level);
            float sampleAngle = patchAngle / (BoundSamples - 1);
            float low = float.MaxValue, high = float.MinValue;
            for (int j = 0; j < BoundSamples; j++)
            {
                for (int i = 0; i < BoundSamples; i++)
                {
                    float a = Mathf.Lerp(aMin, aMax, i / (BoundSamples - 1f)), b = Mathf.Lerp(bMin, bMax, j / (BoundSamples - 1f));
                    // Octaves band-limited, landforms complete (V0.2.0G.1: the patches sample them whatever their width).
                    float e = sampler.Planet.Height.GetBoundsElevation(CubeSphereCoordinates.FaceToDirection(id.Face, a, b), sampleAngle);
                    low = Mathf.Min(low, e);
                    high = Mathf.Max(high, e);
                }
            }
            // What the samples can miss between them: the error bound at the sample spacing (half of it on either side
            // is enough for a point between two samples; the full bound keeps a safety factor). Sea level is included:
            // the ocean surface is part of what the patch draws over.
            float margin = ErrorAtSpacing(sampler, settings, sampleAngle) / sampler.Radius;
            float seaLevel = sampler.Planet.SeaLevel;
            low = Mathf.Min(low, seaLevel) - margin;
            high = Mathf.Max(high, seaLevel) + margin;
            bound.LowRadius = Mathf.Max(sampler.MinRadius, sampler.Radius * (1f + low));
            bound.HighRadius = Mathf.Min(sampler.MaxRadius, sampler.Radius * (1f + high));
            if (bound.HighRadius < bound.LowRadius)
            {
                bound.HighRadius = bound.LowRadius;
            }
            sphereCache.Add(id.Key, bound);
            BoundComputeCount++;
            return bound;
        }

        /// <summary>Displacement bound between samples at an angular spacing (same model as <see cref="GeometricError"/>).</summary>
        static float ErrorAtSpacing(TerrainElevationSampler planet, AdaptiveTerrainSettings settings, float angle)
        {
            float spacing = 1.25f * planet.Radius * angle;
            float slope = settings.assumedMaxSlope;
            float unresolved = 0f;
            TerrainDetail detail = planet.Planet.Height.SurfaceDetail;
            if (detail != null)
            {
                slope += detail.Profile.TypicalSlope(angle);
                unresolved = detail.UnresolvedAmplitude(angle) * planet.Radius; // octaves and landform valleys (V0.2.0G.1)
            }
            return Mathf.Min(planet.MaxRadius - planet.MinRadius, slope * spacing + unresolved);
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
            // Priority = error relative to the patch's own threshold: previously split patches rank higher.
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
