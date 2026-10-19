using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>A view of the river surface LOD, in the planet's object space.</summary>
    public struct RiverSurfaceView
    {
        public Vector3d CameraPosition;
        /// <summary>Camera height above the ground under it (object units).</summary>
        public float Altitude;
        public float PixelsPerRadian;
        /// <summary>Screen error tolerance (pixels), the adaptive terrain's.</summary>
        public float MaxScreenError;
        /// <summary>Error scale of pieces outside the view (coarser), the adaptive terrain's.</summary>
        public float OffscreenErrorScale;
        /// <summary>Optional frustum planes in object space (normals inward), or null.</summary>
        public Plane[] Frustum;

        public RiverSurfaceView(Vector3d cameraPosition, float altitude, float pixelsPerRadian, float maxScreenError = 4f, float offscreenErrorScale = 0.25f)
        {
            CameraPosition = cameraPosition;
            Altitude = altitude;
            PixelsPerRadian = pixelsPerRadian;
            MaxScreenError = maxScreenError;
            OffscreenErrorScale = offscreenErrorScale;
            Frustum = null;
        }
    }

    /// <summary>Measured river surface diagnostics (see <see cref="RiverSurface.Diagnostics"/>).</summary>
    public struct RiverSurfaceDiagnostics
    {
        public int Ribbons, SpineSamples, Pieces;
        /// <summary>Corridor correspondence (see <see cref="RiverCorridorMap"/>).</summary>
        public int TrajectoryNodes, ChannelNodes, MatchedLinks, MismatchedLinks;
        /// <summary>Spine samples placed on a carved axis (weight over 0.5) and their horizontal statistics (meters).</summary>
        public int SnappedSamples;
        public float MeanOffsetMeters, MaxOffsetMeters, MeanResidualMeters, MaxResidualMeters;
        /// <summary>Ocean-bound ribbons whose last sample lies above the ocean on the placed surface.</summary>
        public int CanonicalMouthGaps;
        /// <summary>CPU time of the spine prepass and of all piece builds (worker or synchronous), milliseconds.</summary>
        public double PrepassMilliseconds, PieceBuildMilliseconds;
        /// <summary>Main-thread time of the last tick: selection slice, assembly and mesh uploads.</summary>
        public double SelectionMilliseconds, AssembleMilliseconds;
        public int CachedPieces;
        public long CacheBytes;
        public int DisplayedSamples, DisplayedVertices;
        /// <summary>Cache bytes of the displayed levels (never evicted) and spine samples appended at carved confluences.</summary>
        public long DisplayedBytes;
        public int ConfluenceSamples;
        public int FarPieces, MidPieces, NearPieces;
        public int PendingBuilds, CompletedBuilds, DiscardedBuilds, FailedBuilds;
        public int Uploads, GroupRebuilds, Evictions, MaskUploads;
        /// <summary>Threshold scale applied to keep the displayed samples within the budget (1 = none).</summary>
        public float BudgetScale;
    }

    /// <summary>
    /// The canonical river surface (V0.2.0G.2D): river ribbons placed on the terrain the adaptive renderer draws, not on
    /// the legacy face mesh. The spine of the <see cref="RiverRibbonBuilder"/> (legacy trajectory, mouths, lakes, widths)
    /// is cut into PIECES, one per spine segment, and each piece is built at a LEVEL: 2^level sub-steps of the spine step,
    /// every sample snapped horizontally onto its carved corridor (<see cref="RiverCorridorMap"/>) and placed at the
    /// terrain elevation with the detail octaves band-limited to the level's own spacing and the landforms and incision
    /// complete (<see cref="PlanetHeightGenerator.GetSurfaceElevation"/>): what the adaptive patches draw at that LOD. Levels are chosen per piece from the same screen-error rule
    /// as the terrain (error at spacing / distance), under a sample budget. Pieces are built on worker threads from the
    /// immutable planet and networks, cached per (piece, level) within a byte budget, and assembled on the main thread
    /// into three groups: a far mesh (coarsest levels, planet-wide, object space), a mid mesh and near pieces (one mesh
    /// each, vertices relative to a local origin drawn through the floating-origin patch matrix). Shared spine samples
    /// make adjacent pieces meet exactly at any pair of levels; where levels differ, the finer piece morphs its end to
    /// the coarser level's height over its length (no crack, no step). A pure function of the inputs and the view:
    /// deterministic, no per-frame allocation once warm, no main-thread terrain evaluation while pieces are built in
    /// the background.
    /// </summary>
    public sealed class RiverSurface : IDisposable
    {
        public const int MaxLevel = 11;
        /// <summary>
        /// The far mesh holds every piece at level 0, built once per surface; pieces displayed finer are hidden in it by a
        /// per-piece mask the shader reads (<see cref="FarMask"/>). Levels 1 to <see cref="NearLevel"/> - 1 share the mid
        /// mesh (rebuilt when its pieces change); at <see cref="NearLevel"/> or finer each piece gets its own mesh.
        /// </summary>
        public const int FarLevel = 0;
        public const int NearLevel = 5;
        public const int MaskWidth = 512;
        public const int DefaultSampleBudget = 160000;
        public const long DefaultCacheBytes = 48L << 20;
        /// <summary>Inside a corridor the ribbon half width is at most this share of the carved floor half width...</summary>
        public const float FloorShare = 0.35f;
        /// <summary>...and at most this many times the carved depth (incision plus a quarter of the valley depth): a flat water
        /// surface in a shallow valley must not spread as a pond. Never narrower than the minimum.</summary>
        public const float DepthShare = 2f;
        public const float MinCorridorHalfWidthMeters = 60f;
        /// <summary>Snapping fades out this many spine samples before a lake transition sample (its shoreline is legacy geometry).</summary>
        const int LakeFadeSamples = 2;
        const int SelectionSlices = 8;
        const int PrepassChunk = 512;
        const int MaxUploadsPerFrame = 6;
        const int GroupRebuildInterval = 8;
        const int MaxQueued = 96;
        const float Hysteresis = 0.25f;
        const float BudgetUp = 1.25f, BudgetDown = 1.1f;

        // ---- Inputs.
        PlanetInstance planet;
        RiverNetwork network;
        RiverCorridorMap corridors;
        float radius, oceanRadius, minRadius;
        double radiusMeters;
        int workerCount;

        // ---- Spine (from the ribbon builder) and its prepass (snapped, sided, placed at level 0).
        int spineCount, ribbonCount;
        int[] ribbonStart = new int[0];
        Vector3[] spineLegacy = new Vector3[0];
        float[] spineHalfWidth = new float[0], spineWater = new float[0], spineMouth = new float[0], spineParam = new float[0], spineLakeScale = new float[0];
        int[] spineRiver = new int[0];
        Vector3d[] spineDir = new Vector3d[0];
        Vector3[] spineSide = new Vector3[0];
        float[] spineHw = new float[0], spineWeight = new float[0], spineOffset = new float[0], spineResidual = new float[0];
        float[] spineCenter0 = new float[0], spineLeft0 = new float[0], spineRight0 = new float[0];
        Vector3[] spineLeftPos0 = new Vector3[0], spineRightPos0 = new Vector3[0];
        int prepassChunks, prepassFinished;

        // ---- Pieces.
        int pieceCount;
        int[] pieceFirst = new int[0], pieceRibbon = new int[0];
        Vector3[] pieceMid = new Vector3[0];
        float[] pieceHalfAngle = new float[0], pieceError = new float[0];
        sbyte[] desired = new sbyte[0], displayed = new sbyte[0];
        // Where each displayed piece's vertices are (group 0 far mesh, 1 mid mesh, 2 its near mesh; -1 not assembled yet).
        int[] pieceVertexStart = new int[0];
        sbyte[] pieceGroup = new sbyte[0];
        byte[] queuedLevel = new byte[0];
        bool[] dirty = new bool[0];
        float[] levelStep = new float[MaxLevel + 1], levelError = new float[MaxLevel + 1];
        float spineStep;
        long desiredSamples;
        float budgetScale = 1f;

        // ---- Cache.
        /// <summary>A piece built at a level: per sample, its placed direction, side, band-limited elevations and edge positions.</summary>
        public sealed class PieceData
        {
            public int Piece, Level, Count;
            public Vector3d[] Dir;
            public Vector3[] Side;
            public float[] Center, Left, Right, HalfWidth, Water, Mouth, Diag;
            public Vector3[] LeftPos, RightPos;
            public int LastUsedFrame;
            public long Bytes;
            public double Milliseconds;
        }
        readonly Dictionary<int, PieceData> cache = new Dictionary<int, PieceData>();
        readonly Dictionary<long, Vector3> endHeights = new Dictionary<long, Vector3>();
        long cacheBytes;
        readonly List<int> evictionKeys = new List<int>();

        // ---- Background jobs.
        sealed class Job
        {
            public int Piece, Level, Generation;
            public float Priority;
            public volatile bool Cancelled;
            public PieceData Result;
            public Exception Error;
            public int Chunk = -1;
        }
        readonly List<Job> queued = new List<Job>();
        readonly Dictionary<int, Job> inFlight = new Dictionary<int, Job>();
        readonly ConcurrentQueue<Job> finished = new ConcurrentQueue<Job>();
        readonly WaitCallback runJob;
        int generation;
        int running;

        // ---- Meshes.
        Mesh farMesh, midMesh;
        bool farBuilt, maskDirty, midDirty;
        int midBuiltFrame = -1000;
        Texture2D farMask;
        byte[] maskData = new byte[0];
        int[] farVertexStart = new int[0];
        readonly List<Vector2> pieceIds = new List<Vector2>();
        bool farBuild;
        // Selection runs only while the view changes (a settled camera costs nothing per frame).
        Vector3d lastCameraPosition;
        float lastPixelsPerRadian, lastMaxScreenError;
        int settledCycles;
        sealed class NearEntry { public int Piece; public Mesh Mesh; public Vector3 Origin; public Bounds Local; public int Level; }
        readonly List<NearEntry> near = new List<NearEntry>();
        readonly Dictionary<int, NearEntry> nearOfPiece = new Dictionary<int, NearEntry>();
        readonly Stack<Mesh> meshPool = new Stack<Mesh>();
        readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
        readonly List<Vector4> sides = new List<Vector4>(), widths = new List<Vector4>();
        readonly List<int> triangles = new List<int>();
        int frame;
        RiverSurfaceDiagnostics diagnostics;
        Exception firstError;

        /// <summary>The first build or prepass exception (null when none): reported, never hidden.</summary>
        public Exception FirstError => firstError;

        public RiverSurface()
        {
            runJob = RunJob;
        }

        public int SampleBudget { get; set; } = DefaultSampleBudget;
        public long CacheBudgetBytes { get; set; } = DefaultCacheBytes;
        /// <summary>Most piece builds queued or running at once.</summary>
        public int MaxPending { get; set; } = 8;

        public bool HasSurface => planet != null && pieceCount > 0;
        /// <summary>True once every spine sample is snapped and placed (the far mesh exists from then on).</summary>
        public bool PrepassComplete => prepassChunks > 0 && prepassFinished >= prepassChunks;
        public int PieceCount => pieceCount;
        public int SpineSampleCount => spineCount;
        public Mesh FarMesh => farMesh;
        public Mesh MidMesh => midMesh;
        /// <summary>Per-piece visibility of the far mesh (R8, <see cref="MaskWidth"/> wide): 255 hides a piece drawn finer elsewhere.</summary>
        public Texture2D FarMask => farMask;
        public int NearCount => near.Count;
        public RiverSurfaceDiagnostics Diagnostics => diagnostics;
        public float SpineStep => spineStep;

        public void GetNear(int index, out Mesh mesh, out Vector3 origin, out Bounds localBounds)
        {
            NearEntry e = near[index];
            mesh = e.Mesh;
            origin = e.Origin;
            localBounds = e.Local;
        }

        public int GetDisplayedLevel(int piece) => displayed[piece];
        /// <summary>Spine samples of ribbon r are [GetRibbonStart(r), GetRibbonStart(r + 1)) (confluence samples included).</summary>
        public int GetRibbonStart(int ribbon) => ribbonStart[ribbon];
        public int RibbonCount => ribbonCount;
        public int GetSpineRiver(int sample) => spineRiver[sample];
        public int GetDesiredLevel(int piece) => desired[piece];
        public int GetPieceFirstSample(int piece) => pieceFirst[piece];
        public int GetPieceRibbon(int piece) => pieceRibbon[piece];
        public float GetLevelStep(int level) => levelStep[level];
        public float GetLevelError(int level) => levelError[level];
        /// <summary>Spine sample as placed by the prepass (direction snapped, width capped, level-0 elevation).</summary>
        public void GetSpineSample(int index, out Vector3d direction, out float halfWidth, out float weight, out float offsetMeters, out float elevation0)
        {
            direction = spineDir[index];
            halfWidth = spineHw[index];
            weight = spineWeight[index];
            offsetMeters = spineOffset[index];
            elevation0 = spineCenter0[index];
        }

        /// <summary>
        /// Where a piece's vertices are in the assembled meshes: group 0 the far mesh, 1 the mid mesh, 2 its own near
        /// mesh (relative to its origin); the first vertex and the vertex count (left / right pairs). False when the
        /// piece is not assembled.
        /// </summary>
        public bool GetPieceVertices(int piece, out int group, out int first, out int count)
        {
            group = pieceGroup[piece];
            first = pieceVertexStart[piece];
            count = displayed[piece] >= 0 ? 2 * ((1 << displayed[piece]) + 1) : 0;
            return group >= 0;
        }

        public Mesh GetGroupMesh(int group, int piece, out Vector3 origin)
        {
            origin = Vector3.zero;
            if (group == 0) return farMesh;
            if (group == 1) return midMesh;
            if (nearOfPiece.TryGetValue(piece, out NearEntry e))
            {
                origin = e.Origin;
                return e.Mesh;
            }
            return null;
        }

        /// <summary>Level-0 edge vertices of a spine sample (object space, the far mesh's), tests.</summary>
        public void GetSpineVertex0(int sample, out Vector3 left, out Vector3 right)
        {
            left = spineLeftPos0[sample];
            right = spineRightPos0[sample];
        }

        public int DisplayedCount(int level)
        {
            int n = 0;
            for (int p = 0; p < pieceCount; p++) n += displayed[p] == level ? 1 : 0;
            return n;
        }

        // ------------------------------------------------------------------ Rebuild

        /// <summary>
        /// Takes a new spine (the ribbon builder's last build, geometry excluded) for a planet and network. Cancels the
        /// pending builds, clears the cache and queues the prepass. <paramref name="assumedMaxSlope"/> is the terrain's
        /// LOD slope assumption (its error model is reused).
        /// </summary>
        public void Rebuild(PlanetInstance planet, RiverNetwork network, RiverRibbonBuilder spine, RiverCorridorMap corridors, float assumedMaxSlope, int workerCount)
        {
            Clear();
            this.planet = planet;
            this.network = network;
            this.corridors = corridors;
            this.workerCount = Math.Max(0, workerCount);
            radius = planet.Radius;
            oceanRadius = planet.OceanRadius;
            radiusMeters = planet.Scale.RadiusMeters;
            spineStep = spine.SpineStep;
            ribbonCount = spine.RibbonCount;
            // A tributary's legacy trajectory stops at its junction node; its carved valley meets the trunk's half a cell
            // further, at the end of its curve: one spine sample is appended there (V0.2.0G.2D confluence continuity).
            int confluences = 0;
            var confluenceDir = new Vector3d[ribbonCount];
            var hasConfluence = new bool[ribbonCount];
            for (int r = 0; r < ribbonCount; r++)
            {
                int a = spine.GetSpineStart(r), b = spine.GetSpineStart(r + 1);
                if (b - a < 2) continue;
                RiverSpineSample last = spine.GetSpineSample(b - 1);
                if (last.WaterRadius > 0f || last.ClampToLake > 0) continue;
                if (corridors.TryGetConfluence(last.River, out confluenceDir[r]))
                {
                    hasConfluence[r] = true;
                    confluences++;
                }
            }
            spineCount = spine.SpineSampleCount + confluences;
            Ensure(ref ribbonStart, ribbonCount + 1);
            Ensure(ref spineLegacy, spineCount); Ensure(ref spineHalfWidth, spineCount); Ensure(ref spineWater, spineCount); Ensure(ref spineMouth, spineCount);
            Ensure(ref spineParam, spineCount); Ensure(ref spineLakeScale, spineCount); Ensure(ref spineRiver, spineCount);
            Ensure(ref spineDir, spineCount); Ensure(ref spineSide, spineCount); Ensure(ref spineHw, spineCount); Ensure(ref spineWeight, spineCount);
            Ensure(ref spineOffset, spineCount); Ensure(ref spineResidual, spineCount); Ensure(ref spineCenter0, spineCount); Ensure(ref spineLeft0, spineCount);
            Ensure(ref spineRight0, spineCount); Ensure(ref spineLeftPos0, spineCount); Ensure(ref spineRightPos0, spineCount);
            int n = 0;
            for (int r = 0; r < ribbonCount; r++)
            {
                ribbonStart[r] = n;
                int a = spine.GetSpineStart(r), b = spine.GetSpineStart(r + 1);
                for (int i = a; i < b; i++, n++)
                {
                    RiverSpineSample s = spine.GetSpineSample(i);
                    spineLegacy[n] = s.Direction.normalized;
                    spineHalfWidth[n] = s.HalfWidth;
                    spineWater[n] = s.WaterRadius;
                    spineMouth[n] = Mathf.Clamp01((s.Flare - 1f) / RiverRibbonBuilder.MouthFlare);
                    spineParam[n] = s.Param;
                    spineRiver[n] = s.River;
                    spineLakeScale[n] = 1f;
                }
                if (hasConfluence[r])
                {
                    // Same river, width and flare as the junction sample; the parameter one node further keeps the
                    // corridor map on the junction cell's curve (its last point).
                    spineLegacy[n] = confluenceDir[r].ToVector3().normalized;
                    spineHalfWidth[n] = spineHalfWidth[n - 1];
                    spineWater[n] = 0f;
                    spineMouth[n] = spineMouth[n - 1];
                    spineParam[n] = spineParam[n - 1] + 1f;
                    spineRiver[n] = spineRiver[n - 1];
                    spineLakeScale[n] = 1f;
                    n++;
                }
            }
            ribbonStart[ribbonCount] = n;
            // Lake transitions are legacy geometry (rendered shoreline crossings): the corridor snap fades before them.
            for (int r = 0; r < ribbonCount; r++)
            {
                int a = spine.GetSpineStart(r), b = spine.GetSpineStart(r + 1), start = ribbonStart[r], end = ribbonStart[r + 1];
                for (int i = a; i < b; i++)
                {
                    RiverSpineSample s = spine.GetSpineSample(i);
                    if (s.WaterRadius > 0f || s.ClampToLake > 0)
                    {
                        int at = start + (i - a);
                        for (int k = Math.Max(start, at - LakeFadeSamples); k <= Math.Min(end - 1, at + LakeFadeSamples); k++) spineLakeScale[k] = 0f;
                    }
                }
            }
            // Pieces: one per spine segment.
            pieceCount = 0;
            for (int r = 0; r < ribbonCount; r++) pieceCount += Math.Max(0, ribbonStart[r + 1] - ribbonStart[r] - 1);
            Ensure(ref pieceFirst, pieceCount); Ensure(ref pieceRibbon, pieceCount); Ensure(ref pieceMid, pieceCount); Ensure(ref pieceHalfAngle, pieceCount);
            Ensure(ref pieceError, pieceCount); Ensure(ref desired, pieceCount); Ensure(ref displayed, pieceCount); Ensure(ref queuedLevel, pieceCount); Ensure(ref dirty, pieceCount);
            Ensure(ref pieceVertexStart, pieceCount); Ensure(ref pieceGroup, pieceCount); Ensure(ref farVertexStart, pieceCount);
            int maskHeight = Math.Max(1, (pieceCount + MaskWidth - 1) / MaskWidth);
            if (maskData.Length < MaskWidth * maskHeight) maskData = new byte[MaskWidth * maskHeight];
            Array.Clear(maskData, 0, maskData.Length);
            if (farMask == null || farMask.height != maskHeight)
            {
                DestroyTexture(farMask);
                farMask = new Texture2D(MaskWidth, maskHeight, TextureFormat.R8, false, true) { name = "River far mask", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            }
            farBuilt = false;
            maskDirty = true;
            settledCycles = 0;
            int p = 0;
            for (int r = 0; r < ribbonCount; r++)
            {
                for (int i = ribbonStart[r]; i + 1 < ribbonStart[r + 1]; i++, p++)
                {
                    pieceFirst[p] = i;
                    pieceRibbon[p] = r;
                    Vector3 a = spineLegacy[i], b = spineLegacy[i + 1];
                    pieceMid[p] = (a + b).normalized;
                    pieceHalfAngle[p] = 0.5f * Mathf.Atan2(Vector3.Cross(a, b).magnitude, Vector3.Dot(a, b)) + 2f * (float)(ValleyNetwork.MeanderAmplitudeMeters / radiusMeters);
                    desired[p] = 0;
                    displayed[p] = -1;
                    queuedLevel[p] = 0;
                    dirty[p] = true;
                    pieceGroup[p] = -1;
                }
            }
            // Level spacings and the terrain's error model at each (TerrainLodSelector.ErrorAtSpacing).
            TerrainDetail detail = planet.Height.SurfaceDetail;
            minRadius = radius * (1f + planet.MinElevation);
            float maxRadius = radius * (1f + planet.MaxElevation);
            for (int l = 0; l <= MaxLevel; l++)
            {
                float step = spineStep / (1 << l);
                levelStep[l] = step;
                // The terrain's error model, with the octaves only in the unresolved term: the landforms and the incision are
                // complete at every level of the surface (GetSurfaceElevation), as the patches draw them.
                float slope = assumedMaxSlope, unresolved = 0f;
                if (detail != null)
                {
                    slope += detail.Profile.TypicalSlope(step);
                    unresolved = detail.Profile.UnresolvedAmplitude(step) * radius;
                }
                levelError[l] = Mathf.Min(maxRadius - minRadius, slope * 1.25f * radius * step + unresolved);
            }
            diagnostics = default;
            diagnostics.Ribbons = ribbonCount;
            diagnostics.SpineSamples = spineCount;
            diagnostics.Pieces = pieceCount;
            diagnostics.ConfluenceSamples = confluences;
            diagnostics.TrajectoryNodes = corridors.NodeCount;
            diagnostics.ChannelNodes = corridors.ChannelNodes;
            diagnostics.MatchedLinks = corridors.MatchedLinks;
            diagnostics.MismatchedLinks = corridors.MismatchedLinks;
            diagnostics.BudgetScale = 1f;
            budgetScale = 1f;
            desiredSamples = pieceCount;
            prepassChunks = (spineCount + PrepassChunk - 1) / PrepassChunk;
            prepassFinished = 0;
            for (int c = 0; c < prepassChunks; c++)
            {
                queued.Add(new Job { Chunk = c, Generation = generation, Priority = float.MaxValue });
            }
            Dispatch();
        }

        /// <summary>Drops everything: pending builds (cancelled), cache, meshes.</summary>
        public void Clear()
        {
            generation++;
            foreach (Job job in queued) job.Cancelled = true;
            queued.Clear();
            foreach (Job job in inFlight.Values) job.Cancelled = true;
            inFlight.Clear();
            cache.Clear();
            endHeights.Clear();
            cacheBytes = 0;
            foreach (NearEntry e in near) ReleaseMesh(e.Mesh);
            near.Clear();
            nearOfPiece.Clear();
            if (farMesh != null) farMesh.Clear();
            if (midMesh != null) midMesh.Clear();
            farBuilt = false;
            maskDirty = midDirty = false;
            planet = null;
            network = null;
            corridors = null;
            pieceCount = 0;
            spineCount = 0;
            ribbonCount = 0;
            prepassChunks = prepassFinished = 0;
        }

        public void Dispose()
        {
            Clear();
            while (meshPool.Count > 0) DestroyMesh(meshPool.Pop());
            DestroyMesh(farMesh);
            DestroyMesh(midMesh);
            farMesh = midMesh = null;
            DestroyTexture(farMask);
            farMask = null;
        }

        static void DestroyTexture(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture); else UnityEngine.Object.DestroyImmediate(texture);
        }

        static void Ensure<T>(ref T[] array, int count)
        {
            if (array.Length < count) array = new T[Math.Max(count, array.Length * 2)];
        }

        // ------------------------------------------------------------------ Per frame

        /// <summary>One step: collects finished builds, re-selects a slice of the pieces, dispatches builds, assembles and uploads.</summary>
        public void Tick(in RiverSurfaceView view)
        {
            frame++;
            if (!HasSurface)
            {
                return;
            }
            CollectFinished();
            long start = Stopwatch.GetTimestamp();
            // A settled view (camera within 0.2 % of its altitude, same tolerance) is re-selected for one more full cycle
            // after it settles, then left alone until something changes: no per-frame cost while standing still.
            bool changed = (view.CameraPosition - lastCameraPosition).Magnitude > 0.002 * Math.Max(view.Altitude, radius * 1e-6f)
                || view.PixelsPerRadian != lastPixelsPerRadian || view.MaxScreenError != lastMaxScreenError;
            if (changed)
            {
                lastCameraPosition = view.CameraPosition;
                lastPixelsPerRadian = view.PixelsPerRadian;
                lastMaxScreenError = view.MaxScreenError;
                settledCycles = 0;
            }
            if (settledCycles < 2 || queued.Count > 0 || inFlight.Count > 0)
            {
                SelectSlice(view, frame % SelectionSlices, SelectionSlices);
                if (frame % SelectionSlices == SelectionSlices - 1)
                {
                    AdjustBudget();
                    settledCycles++;
                }
            }
            Dispatch();
            diagnostics.SelectionMilliseconds = Elapsed(start);
            start = Stopwatch.GetTimestamp();
            Assemble(false);
            diagnostics.AssembleMilliseconds = Elapsed(start);
            Evict();
            diagnostics.PendingBuilds = queued.Count + inFlight.Count;
            diagnostics.CachedPieces = cache.Count;
            diagnostics.CacheBytes = cacheBytes;
            diagnostics.BudgetScale = budgetScale;
        }

        /// <summary>
        /// Tests and capture harnesses: completes the prepass, selects every piece for the view, builds every desired
        /// level on the calling thread (in parallel) and assembles all meshes. Nothing is left pending.
        /// </summary>
        public void CompleteSynchronously(in RiverSurfaceView view)
        {
            if (!HasSurface)
            {
                return;
            }
            frame++;
            // Prepass: whatever is queued or running, done here (duplicates are harmless: pure functions).
            var chunks = new List<Job>();
            foreach (Job job in queued) if (job.Chunk >= 0) chunks.Add(job);
            queued.RemoveAll(j => j.Chunk >= 0);
            Parallel.For(0, chunks.Count, i => Guarded(chunks[i], RunPrepass));
            foreach (Job job in chunks) finished.Enqueue(job);
            CollectFinished();
            var wait = Stopwatch.StartNew();
            while (!PrepassComplete)
            {
                if (wait.Elapsed.TotalSeconds > 120.0) throw new TimeoutException("River surface prepass did not complete: " + (queued.Count + inFlight.Count) + " jobs pending");
                Thread.Sleep(1);
                CollectFinished();
            }
            if (firstError != null) throw new InvalidOperationException("River surface prepass failed", firstError);
            // Selection under the sample budget: the threshold scale climbs until the desired samples fit (what the
            // per-frame controller does over successive cycles).
            for (int attempt = 0; attempt < 48; attempt++)
            {
                for (int slice = 0; slice < SelectionSlices; slice++)
                {
                    SelectSlice(view, slice, SelectionSlices);
                }
                if (desiredSamples <= Math.Max(1000, SampleBudget) || budgetScale >= 256f) break;
                budgetScale = Mathf.Min(budgetScale * BudgetUp, 256f);
            }
            for (int round = 0; round < 4; round++)
            {
                var pending = new List<Job>(queued);
                queued.Clear();
                Parallel.For(0, pending.Count, i => Guarded(pending[i], RunPiece));
                foreach (Job job in pending) finished.Enqueue(job);
                var drain = Stopwatch.StartNew();
                while (true)
                {
                    CollectFinished();
                    if (inFlight.Count == 0) break;
                    if (drain.Elapsed.TotalSeconds > 120.0) throw new TimeoutException("River surface builds did not drain: " + inFlight.Count + " in flight");
                    Thread.Sleep(1);
                }
                if (firstError != null) throw new InvalidOperationException("River surface piece build failed", firstError);
                for (int slice = 0; slice < SelectionSlices; slice++)
                {
                    SelectSlice(view, slice, SelectionSlices);
                }
                if (queued.Count == 0) break;
            }
            Assemble(true);
            Evict();
            diagnostics.PendingBuilds = queued.Count + inFlight.Count;
            diagnostics.CachedPieces = cache.Count;
            diagnostics.CacheBytes = cacheBytes;
            diagnostics.BudgetScale = budgetScale;
        }

        static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        static void Guarded(Job job, Action<Job> run)
        {
            try
            {
                run(job);
            }
            catch (Exception e)
            {
                job.Error = e;
            }
        }

        // ------------------------------------------------------------------ Selection

        void SelectSlice(in RiverSurfaceView view, int slice, int slices)
        {
            if (!PrepassComplete)
            {
                return;
            }
            Vector3 camera = view.CameraPosition.ToVector3();
            float cameraDistance = camera.magnitude;
            Vector3 cameraDirection = cameraDistance > 0f ? camera / cameraDistance : Vector3.up;
            float horizonCos = cameraDistance > minRadius ? minRadius / cameraDistance : 1f;
            float threshold = view.MaxScreenError * budgetScale;
            float ppr = view.PixelsPerRadian;
            Plane[] frustum = view.Frustum != null && view.Frustum.Length == 6 ? view.Frustum : null;
            for (int p = slice; p < pieceCount; p += slices)
            {
                int i0 = pieceFirst[p];
                Vector3 a = spineDir[i0].ToVector3() * (radius * (1f + spineCenter0[i0]));
                Vector3 b = spineDir[i0 + 1].ToVector3() * (radius * (1f + spineCenter0[i0 + 1]));
                float boundingRadius = 0.5f * (a - b).magnitude + pieceHalfAngle[p] * radius * 0.5f;
                Vector3 c = 0.5f * (a + b);
                int level = 0;
                float error = 0f;
                float cosToCenter = Vector3.Dot(pieceMid[p], cameraDirection);
                float angleToCenter = Mathf.Atan2(Vector3.Cross(pieceMid[p], cameraDirection).magnitude, cosToCenter);
                bool beyondHorizon = angleToCenter - pieceHalfAngle[p] > 0f && Mathf.Cos(angleToCenter - pieceHalfAngle[p]) < horizonCos;
                if (!beyondHorizon)
                {
                    float sphereDistance = SegmentDistance(camera, a, b);
                    float gap = Mathf.Max(angleToCenter - pieceHalfAngle[p], 0f) * radius;
                    float floor = Mathf.Sqrt(gap * gap + view.Altitude * view.Altitude);
                    float distance = Mathf.Max(Mathf.Max(sphereDistance, floor), radius * 1e-7f);
                    float scale = ppr / distance;
                    if (frustum != null)
                    {
                        for (int k = 0; k < 6; k++)
                        {
                            if (frustum[k].GetDistanceToPoint(c) < -boundingRadius)
                            {
                                scale *= view.OffscreenErrorScale;
                                break;
                            }
                        }
                    }
                    // Coarsest level within the tolerance; the displayed level, when it is the next finer one, is kept
                    // while the coarser would be within the hysteresis band of the tolerance (no flicker at the boundary).
                    while (level < MaxLevel && levelError[level] * scale > threshold)
                    {
                        level++;
                    }
                    int shown = displayed[p];
                    if (shown == level + 1 && levelError[level] * scale > threshold * (1f - Hysteresis))
                    {
                        level = shown;
                    }
                    error = levelError[Math.Max(shown, 0)] * scale;
                }
                pieceError[p] = error;
                if (desired[p] != level)
                {
                    desiredSamples += (1L << level) - (1L << desired[p]);
                    desired[p] = (sbyte)level;
                }
                Resolve(p);
            }
        }

        static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-20f));
            return (a + ab * t - p).magnitude;
        }

        void AdjustBudget()
        {
            long budget = Math.Max(1000, SampleBudget);
            if (desiredSamples > budget)
            {
                budgetScale = Mathf.Min(budgetScale * BudgetUp, 256f);
            }
            else if (desiredSamples < budget * 0.6 && budgetScale > 1f)
            {
                budgetScale = Mathf.Max(1f, budgetScale / BudgetDown);
            }
        }

        /// <summary>Shows the best cached level for a piece's desired level and queues the missing build.</summary>
        void Resolve(int p)
        {
            int want = desired[p];
            int best;
            if (want == 0 || cache.ContainsKey(Key(p, want)))
            {
                best = want;
            }
            else
            {
                best = 0;
                for (int l = want - 1; l >= 1; l--)
                {
                    if (cache.ContainsKey(Key(p, l))) { best = l; break; }
                }
                if (best == 0)
                {
                    for (int l = want + 1; l <= MaxLevel; l++)
                    {
                        if (cache.ContainsKey(Key(p, l))) { best = l; break; }
                    }
                }
                if (queuedLevel[p] != want && !inFlight.ContainsKey(Key(p, want)))
                {
                    if (queuedLevel[p] != 0)
                    {
                        CancelQueued(p);
                    }
                    queued.Add(new Job { Piece = p, Level = want, Generation = generation, Priority = pieceError[p] });
                    queuedLevel[p] = (byte)want;
                }
            }
            if (displayed[p] != best)
            {
                SetDisplayed(p, best);
            }
        }

        void CancelQueued(int p)
        {
            for (int i = queued.Count - 1; i >= 0; i--)
            {
                if (queued[i].Chunk < 0 && queued[i].Piece == p)
                {
                    queued[i].Cancelled = true;
                    queued.RemoveAt(i);
                }
            }
            queuedLevel[p] = 0;
        }

        void SetDisplayed(int p, int level)
        {
            int old = displayed[p];
            displayed[p] = (sbyte)level;
            MarkDirty(p, old);
            MarkDirty(p, level);
            // Neighbours morph their ends to the coarser level: both re-assemble.
            if (p > 0 && pieceRibbon[p - 1] == pieceRibbon[p]) MarkDirty(p - 1, displayed[p - 1]);
            if (p + 1 < pieceCount && pieceRibbon[p + 1] == pieceRibbon[p]) MarkDirty(p + 1, displayed[p + 1]);
        }

        void MarkDirty(int p, int level)
        {
            dirty[p] = true;
            if (level < 0) return;
            if (level <= FarLevel) maskDirty = true;
            else if (level < NearLevel) midDirty = true;
        }

        static int Key(int piece, int level) => piece * 16 + level;
        static long EndKey(int sample, int level) => (long)sample * 16 + level;

        // ------------------------------------------------------------------ Jobs

        void Dispatch()
        {
            if (queued.Count == 0)
            {
                return;
            }
            queued.Sort((x, y) => y.Priority.CompareTo(x.Priority));
            // Only piece builds are bounded in the queue (the lowest priorities are dropped and re-requested by the
            // selection later); prepass chunks are never dropped, the surface needs every one of them.
            int pieces = 0;
            for (int i = 0; i < queued.Count; i++) pieces += queued[i].Chunk < 0 ? 1 : 0;
            for (int i = queued.Count - 1; i >= 0 && pieces > MaxQueued; i--)
            {
                if (queued[i].Chunk >= 0) continue;
                queuedLevel[queued[i].Piece] = 0;
                queued.RemoveAt(i);
                pieces--;
            }
            // Synchronous mode (no workers: tests, harnesses) runs everything queued at once.
            int limit = workerCount > 0 ? Math.Max(1, MaxPending) : int.MaxValue;
            while (queued.Count > 0 && inFlight.Count < limit)
            {
                Job job = queued[0];
                queued.RemoveAt(0);
                int key = job.Chunk >= 0 ? -1 - job.Chunk : Key(job.Piece, job.Level);
                inFlight[key] = job;
                if (workerCount > 0)
                {
                    Interlocked.Increment(ref running);
                    ThreadPool.QueueUserWorkItem(runJob, job);
                }
                else
                {
                    RunJob(job);
                }
            }
        }

        void RunJob(object state)
        {
            var job = (Job)state;
            try
            {
                if (!job.Cancelled)
                {
                    if (job.Chunk >= 0) RunPrepass(job); else RunPiece(job);
                }
            }
            catch (Exception e)
            {
                job.Error = e;
            }
            finally
            {
                finished.Enqueue(job);
                if (workerCount > 0) Interlocked.Decrement(ref running);
            }
        }

        void CollectFinished()
        {
            while (finished.TryDequeue(out Job job))
            {
                int key = job.Chunk >= 0 ? -1 - job.Chunk : Key(job.Piece, job.Level);
                inFlight.Remove(key);
                if (job.Error != null)
                {
                    diagnostics.FailedBuilds++;
                    firstError ??= job.Error;
                    if (diagnostics.FailedBuilds <= 3) UnityEngine.Debug.LogException(job.Error);
                    if (job.Chunk >= 0 && job.Generation == generation)
                    {
                        prepassFinished++; // a failed chunk never blocks the surface (its samples stay unplaced)
                        if (PrepassComplete) FinishPrepass();
                    }
                    else if (job.Chunk < 0 && queuedLevel[job.Piece] == job.Level)
                    {
                        queuedLevel[job.Piece] = 0;
                    }
                    continue;
                }
                if (job.Cancelled || job.Generation != generation)
                {
                    diagnostics.DiscardedBuilds++;
                    continue;
                }
                if (job.Chunk >= 0)
                {
                    prepassFinished++;
                    diagnostics.PrepassMilliseconds += job.Result != null ? job.Result.Milliseconds : 0.0;
                    if (PrepassComplete)
                    {
                        FinishPrepass();
                    }
                    continue;
                }
                diagnostics.CompletedBuilds++;
                diagnostics.PieceBuildMilliseconds += job.Result.Milliseconds;
                if (queuedLevel[job.Piece] == job.Level) queuedLevel[job.Piece] = 0;
                PieceData d = job.Result;
                d.LastUsedFrame = frame;
                cache[Key(d.Piece, d.Level)] = d;
                cacheBytes += d.Bytes;
                int i0 = pieceFirst[d.Piece];
                endHeights[EndKey(i0, d.Level)] = new Vector3(d.Center[0], d.Left[0], d.Right[0]);
                endHeights[EndKey(i0 + 1, d.Level)] = new Vector3(d.Center[d.Count - 1], d.Left[d.Count - 1], d.Right[d.Count - 1]);
                Resolve(d.Piece);
            }
        }

        void FinishPrepass()
        {
            // Snapping statistics and the mouths, on the placed spine.
            int snapped = 0; double sumOffset = 0, sumResidual = 0; float maxOffset = 0f, maxResidual = 0f;
            for (int i = 0; i < spineCount; i++)
            {
                if (spineWeight[i] > 0.5f)
                {
                    snapped++;
                    sumOffset += spineOffset[i]; sumResidual += spineResidual[i];
                    maxOffset = Mathf.Max(maxOffset, spineOffset[i]); maxResidual = Mathf.Max(maxResidual, spineResidual[i]);
                }
            }
            diagnostics.SnappedSamples = snapped;
            diagnostics.MeanOffsetMeters = snapped > 0 ? (float)(sumOffset / snapped) : 0f;
            diagnostics.MeanResidualMeters = snapped > 0 ? (float)(sumResidual / snapped) : 0f;
            diagnostics.MaxOffsetMeters = maxOffset;
            diagnostics.MaxResidualMeters = maxResidual;
            int gaps = 0;
            for (int r = 0; r < ribbonCount; r++)
            {
                int last = ribbonStart[r + 1] - 1;
                if (last < ribbonStart[r]) continue;
                River data = network.GetRiver(spineRiver[last]);
                if (data.MouthCell >= 0 && spineWater[last] <= 0f && radius * (1f + spineCenter0[last]) >= oceanRadius)
                {
                    gaps++;
                }
            }
            diagnostics.CanonicalMouthGaps = gaps;
            for (int p = 0; p < pieceCount; p++)
            {
                if (displayed[p] < 0) SetDisplayed(p, 0);
            }
        }

        /// <summary>Snaps, sides and places (level 0) a chunk of spine samples. Pure: reads the immutable inputs only.</summary>
        void RunPrepass(Job job)
        {
            long start = Stopwatch.GetTimestamp();
            int a = job.Chunk * PrepassChunk, b = Math.Min(spineCount, a + PrepassChunk);
            // Pass 1: snap and widths.
            for (int i = a; i < b; i++)
            {
                PlaceSample(spineLegacy[i], spineRiver[i], spineParam[i], spineLakeScale[i], spineHalfWidth[i], out spineDir[i], out spineHw[i], out spineWeight[i], out spineOffset[i], out spineResidual[i]);
            }
            // Pass 2: sides from the neighbours along the ribbon (a neighbour of another chunk is re-placed: pure), heights
            // at the level-0 spacing.
            float step = levelStep[0];
            for (int i = a; i < b; i++)
            {
                int r = RibbonOf(i);
                int prev = i > ribbonStart[r] ? i - 1 : i, next = i + 1 < ribbonStart[r + 1] ? i + 1 : i;
                Vector3d dPrev = prev == i ? spineDir[i] : (prev >= a && prev < b ? spineDir[prev] : Placed(prev));
                Vector3d dNext = next == i ? spineDir[i] : (next >= a && next < b ? spineDir[next] : Placed(next));
                spineSide[i] = SideOf(spineDir[i], dPrev, dNext, i > a ? spineSide[i - 1] : Vector3.zero);
                Heights(spineDir[i], spineSide[i], spineHw[i], spineWeight[i], step, out float c, out float l, out float rr);
                spineCenter0[i] = c; spineLeft0[i] = l; spineRight0[i] = rr;
                EdgePositions(spineDir[i], spineSide[i], spineHw[i], l, rr, out Vector3d left, out Vector3d right);
                spineLeftPos0[i] = left.ToVector3();
                spineRightPos0[i] = right.ToVector3();
            }
            job.Result = new PieceData { Milliseconds = Elapsed(start) };
        }

        Vector3d Placed(int i)
        {
            PlaceSample(spineLegacy[i], spineRiver[i], spineParam[i], spineLakeScale[i], spineHalfWidth[i], out Vector3d dir, out _, out _, out _, out _);
            return dir;
        }

        int RibbonOf(int sample)
        {
            int lo = 0, hi = ribbonCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (ribbonStart[mid] <= sample) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The placed direction, capped half width and corridor data of a trajectory point (pure).</summary>
        void PlaceSample(Vector3 legacy, int river, float param, float lakeScale, float halfWidth,
            out Vector3d direction, out float hw, out float weight, out float offset, out float residual)
        {
            direction = corridors.Snap(new Vector3d(legacy), river, param, lakeScale, out RiverCorridorMap.Projection proj);
            weight = proj.Weight;
            offset = proj.OffsetMeters;
            residual = proj.ResidualMeters;
            hw = halfWidth;
            if (weight > 0f)
            {
                float floor = proj.HalfWidthMeters;
                if (proj.IncisionMeters > 0f && proj.IncisionHalfWidthMeters > 0f) floor = Mathf.Min(floor, proj.IncisionHalfWidthMeters);
                float cap = Mathf.Min(FloorShare * floor, DepthShare * (proj.IncisionMeters + 0.25f * proj.DepthMeters));
                cap = Mathf.Max(cap, MinCorridorHalfWidthMeters);
                float capped = Mathf.Min(halfWidth, (float)(cap / radiusMeters));
                hw = halfWidth + (capped - halfWidth) * weight;
            }
        }

        static Vector3 SideOf(Vector3d at, Vector3d previous, Vector3d next, Vector3 fallback)
        {
            Vector3d forward = next - previous;
            Vector3d tangent = forward - at * Vector3d.Dot(forward, at);
            Vector3d side = Vector3d.Cross(at, tangent);
            if (side.SqrMagnitude < 1e-24)
            {
                if (fallback.sqrMagnitude > 0f) return fallback;
                side = Vector3d.Cross(at, Math.Abs(at.y) < 0.99 ? Vector3d.Up : Vector3d.Right);
            }
            return side.Normalized.ToVector3();
        }

        /// <summary>Band-limited elevations of a sample: its center, and its edges draped on the terrain outside corridors
        /// (flat water surface inside: the center's elevation).</summary>
        void Heights(Vector3d dir, Vector3 side, float hw, float weight, float spacing, out float center, out float left, out float right)
        {
            PlanetHeightGenerator height = planet.Height;
            center = height.GetSurfaceElevation(dir, spacing);
            if (weight >= 0.999f)
            {
                left = right = center;
                return;
            }
            var s = new Vector3d(side);
            float l = height.GetSurfaceElevation((dir + s * hw).Normalized, spacing);
            float r = height.GetSurfaceElevation((dir - s * hw).Normalized, spacing);
            left = l + (center - l) * weight;
            right = r + (center - r) * weight;
        }

        void EdgePositions(Vector3d dir, Vector3 side, float hw, float left, float right, out Vector3d leftPos, out Vector3d rightPos)
        {
            var s = new Vector3d(side);
            leftPos = (dir + s * hw).Normalized * (radius * (1.0 + left));
            rightPos = (dir - s * hw).Normalized * (radius * (1.0 + right));
        }

        void RunPiece(Job job)
        {
            long start = Stopwatch.GetTimestamp();
            PieceData d = BuildPiece(job.Piece, job.Level);
            d.Milliseconds = Elapsed(start);
            job.Result = d;
        }

        /// <summary>
        /// Builds a piece at a level (pure: the same inputs give the same data on any thread). The two end samples are
        /// the spine samples themselves (placed by the prepass), re-evaluated at this level's spacing; the interior
        /// samples interpolate the legacy trajectory and are snapped and placed like the prepass does.
        /// </summary>
        public PieceData BuildPiece(int piece, int level)
        {
            int n = 1 << level, count = n + 1;
            int i0 = pieceFirst[piece], i1 = i0 + 1;
            var d = new PieceData
            {
                Piece = piece, Level = level, Count = count,
                Dir = new Vector3d[count], Side = new Vector3[count], Center = new float[count], Left = new float[count], Right = new float[count],
                HalfWidth = new float[count], Water = new float[count], Mouth = new float[count], Diag = new float[count],
                LeftPos = new Vector3[count], RightPos = new Vector3[count],
            };
            d.Bytes = count * (24 + 12 + 4 * 7 + 24) + 160;
            float step = levelStep[level];
            Vector3 legacyA = spineLegacy[i0], legacyB = spineLegacy[i1];
            for (int k = 0; k < count; k++)
            {
                float t = k / (float)n;
                float weight, offset;
                if (k == 0 || k == n)
                {
                    int i = k == 0 ? i0 : i1;
                    d.Dir[k] = spineDir[i];
                    d.HalfWidth[k] = spineHw[i];
                    weight = spineWeight[i];
                    offset = spineOffset[i];
                }
                else
                {
                    Vector3 legacy = Vector3.Lerp(legacyA, legacyB, t).normalized;
                    float param = Mathf.Lerp(spineParam[i0], spineParam[i1], t);
                    float lakeScale = Mathf.Lerp(spineLakeScale[i0], spineLakeScale[i1], t);
                    float halfWidth = Mathf.Lerp(spineHalfWidth[i0], spineHalfWidth[i1], t);
                    PlaceSample(legacy, spineRiver[i0], param, lakeScale, halfWidth, out d.Dir[k], out d.HalfWidth[k], out weight, out offset, out _);
                }
                d.Water[k] = Mathf.Lerp(spineWater[i0], spineWater[i1], t);
                d.Mouth[k] = Mathf.Lerp(spineMouth[i0], spineMouth[i1], t);
                // Diagnostic value: 0 legacy placement, 1 + offset / 40 km on a corridor (the river offset view).
                d.Diag[k] = weight > 0.5f ? 1f + Mathf.Clamp01(offset / 40000f) : 0f;
                d.Center[k] = weight; // temporarily the weight (needed for the heights below)
            }
            for (int k = 0; k < count; k++)
            {
                if (k == 0) d.Side[k] = spineSide[i0];
                else if (k == n) d.Side[k] = spineSide[i1];
                else d.Side[k] = SideOf(d.Dir[k], d.Dir[k - 1], d.Dir[k + 1], d.Side[k - 1]);
            }
            for (int k = 0; k < count; k++)
            {
                float weight = d.Center[k];
                Heights(d.Dir[k], d.Side[k], d.HalfWidth[k], weight, step, out d.Center[k], out d.Left[k], out d.Right[k]);
                EdgePositions(d.Dir[k], d.Side[k], d.HalfWidth[k], d.Left[k], d.Right[k], out Vector3d left, out Vector3d right);
                d.LeftPos[k] = left.ToVector3();
                d.RightPos[k] = right.ToVector3();
            }
            return d;
        }

        // ------------------------------------------------------------------ Assembly

        void Assemble(bool everything)
        {
            if (!PrepassComplete)
            {
                return;
            }
            if (!farBuilt)
            {
                BuildFarMesh();
            }
            bool midNow = midDirty && (everything || frame - midBuiltFrame >= GroupRebuildInterval);
            int uploads = 0;
            for (int p = 0; p < pieceCount; p++)
            {
                if (!dirty[p])
                {
                    continue;
                }
                int level = displayed[p];
                if (level >= NearLevel)
                {
                    if (!everything && uploads >= MaxUploadsPerFrame)
                    {
                        continue;
                    }
                    UploadNear(p, level);
                    uploads++;
                    dirty[p] = false;
                }
                else
                {
                    if (nearOfPiece.TryGetValue(p, out NearEntry stale))
                    {
                        RemoveNear(stale);
                    }
                    // Level 0 lives in the static far mesh (unmasked); mid pieces are cleared by the group rebuild below.
                    if (level <= FarLevel)
                    {
                        pieceGroup[p] = 0;
                        pieceVertexStart[p] = farVertexStart[p];
                        dirty[p] = false;
                    }
                    else if (midNow)
                    {
                        dirty[p] = false;
                    }
                }
                // The far mask: hidden wherever the piece is drawn at a finer level.
                if (level >= 0)
                {
                    byte hidden = (byte)(level > FarLevel ? 255 : 0);
                    if (maskData[p] != hidden) { maskData[p] = hidden; maskDirty = true; }
                }
            }
            diagnostics.Uploads += uploads;
            if (maskDirty && farMask != null)
            {
                farMask.SetPixelData(maskData, 0);
                farMask.Apply(false, false);
                maskDirty = false;
                diagnostics.MaskUploads++;
            }
            if (midNow)
            {
                BuildGroup(false);
                midDirty = false;
                midBuiltFrame = frame;
                diagnostics.GroupRebuilds++;
            }
            int samples = 0, far = 0, mid = 0, nearCount = 0;
            for (int p = 0; p < pieceCount; p++)
            {
                int l = displayed[p];
                if (l < 0) continue;
                samples += 1 << l;
                if (l <= FarLevel) far++; else if (l < NearLevel) mid++; else nearCount++;
            }
            diagnostics.DisplayedSamples = samples;
            diagnostics.DisplayedVertices = 2 * (samples + pieceCount);
            long displayedBytes = 0;
            foreach (KeyValuePair<int, PieceData> pair in cache)
            {
                if (displayed[pair.Value.Piece] == pair.Value.Level) displayedBytes += pair.Value.Bytes;
            }
            diagnostics.DisplayedBytes = displayedBytes;
            diagnostics.FarPieces = far;
            diagnostics.MidPieces = mid;
            diagnostics.NearPieces = nearCount;
        }

        /// <summary>Every piece at level 0, once per surface (UV2.x: the piece index the mask is read with).</summary>
        void BuildFarMesh()
        {
            vertices.Clear(); normals.Clear(); sides.Clear(); widths.Clear(); triangles.Clear(); pieceIds.Clear();
            farBuild = true;
            for (int p = 0; p < pieceCount; p++)
            {
                farVertexStart[p] = vertices.Count;
                if (displayed[p] <= FarLevel) { pieceGroup[p] = 0; pieceVertexStart[p] = farVertexStart[p]; }
                AppendPiece(p, 0, Vector3d.Zero, false);
            }
            farBuild = false;
            farMesh ??= NewMesh("Planet Rivers (far)");
            Upload(farMesh, new Bounds(Vector3.zero, Vector3.one * (radius * 2.4f)));
            farMesh.SetUVs(2, pieceIds);
            farBuilt = true;
            maskDirty = true;
            diagnostics.GroupRebuilds++;
        }

        void BuildGroup(bool farGroup)
        {
            vertices.Clear(); normals.Clear(); sides.Clear(); widths.Clear(); triangles.Clear();
            for (int p = 0; p < pieceCount; p++)
            {
                int level = displayed[p];
                if (level <= FarLevel || level >= NearLevel)
                {
                    continue;
                }
                pieceGroup[p] = 1;
                pieceVertexStart[p] = vertices.Count;
                AppendPiece(p, level, Vector3d.Zero, false);
            }
            midMesh ??= NewMesh("Planet Rivers (mid)");
            Upload(midMesh, new Bounds(Vector3.zero, Vector3.one * (radius * 2.4f)));
        }

        void UploadNear(int p, int level)
        {
            if (!nearOfPiece.TryGetValue(p, out NearEntry entry))
            {
                entry = new NearEntry { Piece = p, Mesh = meshPool.Count > 0 ? meshPool.Pop() : NewMesh("Planet Rivers (near)") };
                near.Add(entry);
                nearOfPiece[p] = entry;
            }
            entry.Level = level;
            int i0 = pieceFirst[p];
            Vector3d origin = spineDir[i0] * (radius * (1.0 + spineCenter0[i0]));
            entry.Origin = origin.ToVector3();
            vertices.Clear(); normals.Clear(); sides.Clear(); widths.Clear(); triangles.Clear();
            pieceGroup[p] = 2;
            pieceVertexStart[p] = 0;
            AppendPiece(p, level, origin, true);
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < vertices.Count; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            entry.Local = vertices.Count > 0 ? new Bounds((min + max) * 0.5f, max - min + Vector3.one * (radius * 0.002f)) : new Bounds(Vector3.zero, Vector3.zero);
            Upload(entry.Mesh, entry.Local);
        }

        void RemoveNear(NearEntry entry)
        {
            near.Remove(entry);
            nearOfPiece.Remove(entry.Piece);
            if (pieceGroup[entry.Piece] == 2) pieceGroup[entry.Piece] = -1;
            entry.Mesh.Clear();
            meshPool.Push(entry.Mesh);
        }

        /// <summary>End morph: the correction (elevation fraction, center / left / right) of an end sample towards the
        /// coarser neighbouring level, or zero.</summary>
        Vector3 EndCorrection(int p, int level, bool start)
        {
            int neighbour = start ? p - 1 : p + 1;
            if (neighbour < 0 || neighbour >= pieceCount || pieceRibbon[neighbour] != pieceRibbon[p])
            {
                return Vector3.zero;
            }
            int other = displayed[neighbour];
            if (other < 0 || other >= level)
            {
                return Vector3.zero;
            }
            int sample = start ? pieceFirst[p] : pieceFirst[p] + 1;
            Vector3 coarse = EndHeight(sample, other), own = EndHeight(sample, level);
            return coarse - own;
        }

        Vector3 EndHeight(int sample, int level)
        {
            if (level == 0)
            {
                return new Vector3(spineCenter0[sample], spineLeft0[sample], spineRight0[sample]);
            }
            if (!endHeights.TryGetValue(EndKey(sample, level), out Vector3 h))
            {
                // The neighbour's data was evicted: evaluated here (rare, three evaluations).
                Heights(spineDir[sample], spineSide[sample], spineHw[sample], spineWeight[sample], levelStep[level], out float c, out float l, out float r);
                h = new Vector3(c, l, r);
                endHeights[EndKey(sample, level)] = h;
            }
            return h;
        }

        int currentPiece;

        void AppendPiece(int p, int level, Vector3d origin, bool precise)
        {
            currentPiece = p;
            Vector3 cA = EndCorrection(p, level, true), cB = EndCorrection(p, level, false);
            // Exact tests: the corrections are elevation fractions (a 20 m morph is 3e-6), far below Unity's approximate
            // Vector3 equality.
            bool corrected = cA.sqrMagnitude > 0f || cB.sqrMagnitude > 0f;
            int first = vertices.Count;
            int i0 = pieceFirst[p];
            if (level == 0)
            {
                for (int k = 0; k < 2; k++)
                {
                    int i = i0 + k;
                    AppendSample(spineDir[i], spineSide[i], spineHw[i], spineLeft0[i], spineRight0[i], spineLeftPos0[i], spineRightPos0[i],
                        spineWater[i], spineMouth[i], spineWeight[i] > 0.5f ? 1f + Mathf.Clamp01(spineOffset[i] / 40000f) : 0f, Vector3.zero, origin, precise);
                }
            }
            else
            {
                PieceData d = cache[Key(p, level)];
                d.LastUsedFrame = frame;
                int n = d.Count - 1;
                for (int k = 0; k <= n; k++)
                {
                    float t = k / (float)n;
                    Vector3 c = corrected ? cA * (1f - t) + cB * t : Vector3.zero;
                    AppendSample(d.Dir[k], d.Side[k], d.HalfWidth[k], d.Left[k], d.Right[k], d.LeftPos[k], d.RightPos[k], d.Water[k], d.Mouth[k], d.Diag[k], c, origin, precise);
                }
            }
            int count = (vertices.Count - first) / 2;
            for (int k = 1; k < count; k++)
            {
                int l0 = first + 2 * (k - 1), r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                triangles.Add(l0); triangles.Add(l1); triangles.Add(r0);
                triangles.Add(r0); triangles.Add(l1); triangles.Add(r1);
            }
        }

        void AppendSample(Vector3d dir, Vector3 side, float hw, float left, float right, Vector3 leftPos, Vector3 rightPos, float water, float mouth, float diag,
            Vector3 correction, Vector3d origin, bool precise)
        {
            Vector3 l, r;
            if (precise || correction.sqrMagnitude > 0f)
            {
                EdgePositions(dir, side, hw, left + correction.y, right + correction.z, out Vector3d lp, out Vector3d rp);
                l = (lp - origin).ToVector3();
                r = (rp - origin).ToVector3();
            }
            else
            {
                l = leftPos;
                r = rightPos;
            }
            Vector3 normal = dir.ToVector3();
            vertices.Add(l); vertices.Add(r);
            normals.Add(normal); normals.Add(normal);
            sides.Add(new Vector4(side.x, side.y, side.z, 1f)); sides.Add(new Vector4(side.x, side.y, side.z, -1f));
            var w = new Vector4(hw * radius, water, mouth, diag);
            widths.Add(w); widths.Add(w);
            if (farBuild)
            {
                var id = new Vector2(currentPiece, 0f);
                pieceIds.Add(id); pieceIds.Add(id);
            }
        }

        void Upload(Mesh mesh, Bounds bounds)
        {
            mesh.Clear();
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, sides);
            mesh.SetUVs(1, widths);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = bounds;
        }

        static Mesh NewMesh(string name) => new Mesh { name = name, hideFlags = HideFlags.DontSave };

        void ReleaseMesh(Mesh mesh)
        {
            if (mesh == null) return;
            mesh.Clear();
            meshPool.Push(mesh);
        }

        static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(mesh); else UnityEngine.Object.DestroyImmediate(mesh);
        }

        // ------------------------------------------------------------------ Cache

        void Evict()
        {
            if (cacheBytes <= CacheBudgetBytes)
            {
                return;
            }
            // Over budget: drop the least recently used quarter of what is not displayed.
            evictionKeys.Clear();
            foreach (KeyValuePair<int, PieceData> pair in cache)
            {
                if (displayed[pair.Value.Piece] != pair.Value.Level) evictionKeys.Add(pair.Key);
            }
            evictionKeys.Sort((x, y) => cache[x].LastUsedFrame.CompareTo(cache[y].LastUsedFrame));
            // Least recently used first, until the budget holds (what is displayed is never dropped).
            for (int i = 0; i < evictionKeys.Count && cacheBytes > CacheBudgetBytes; i++)
            {
                PieceData d = cache[evictionKeys[i]];
                cacheBytes -= d.Bytes;
                cache.Remove(evictionKeys[i]);
                diagnostics.Evictions++;
            }
        }
    }
}
