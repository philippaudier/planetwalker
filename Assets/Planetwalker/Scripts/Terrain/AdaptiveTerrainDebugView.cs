using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    public enum AdaptiveTerrainDebugMode
    {
        /// <summary>Patch outlines draped over the existing planet (default, lightweight).</summary>
        Overlay = 0,
        /// <summary>Opt-in: the selected patches as meshes, in place of the six face meshes (hidden meanwhile).</summary>
        MeshPreview = 1,
    }

    /// <summary>
    /// ADAPTIVE TERRAIN DEBUG (V0.2.0A): runs the LOD selection for the orbital camera and shows it, either as an
    /// outline overlay on the existing planet or as an opt-in patch-mesh preview. Purely diagnostic: lives on the
    /// planet visual's GameObject, disabled by default, never regenerates the planet, never touches the world's
    /// definition or archive. Owns every resource it creates (Mesh objects through <see cref="PatchMeshCache"/>
    /// and <see cref="AdaptiveTerrainOverlay"/>), releases them when disabled or destroyed, and drops stale patch
    /// data whenever the displayed planet changes (regeneration, world load).
    /// </summary>
    /// <remarks>
    /// Bounded work per frame: the selection reruns only when the camera, viewport or settings changed; the preview
    /// builds at most <see cref="previewBuildMilliseconds"/> of patch meshes per frame (at least one), nearest first,
    /// and keeps showing the last complete covering until the new one is fully built (no holes), unless the mesh
    /// budget cannot hold both. Not a streaming system: meshes are built synchronously on the main thread.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Planetwalker/Adaptive Terrain Debug")]
    public sealed class AdaptiveTerrainDebugView : MonoBehaviour
    {
        [SerializeField] AdaptiveTerrainSettings settings = new AdaptiveTerrainSettings();
        [SerializeField] AdaptiveTerrainDebugMode mode = AdaptiveTerrainDebugMode.Overlay;
        [Tooltip("Keep the current covering while the camera moves (inspect the selection from elsewhere).")]
        [SerializeField] bool freezeSelection;
        [Tooltip("Mesh preview: draw each patch's vertex grid (shows the triangle density chosen by the LOD).")]
        [SerializeField] bool showVertexGrid = true;
        [Tooltip("Patch mesh construction time per frame in the preview (at least one patch per frame).")]
        [Range(0.5f, 50f)]
        [SerializeField] float previewBuildMilliseconds = 6f;

        PlanetVisualController visual;
        TerrainElevationSampler sampler;
        TerrainLodSelector selector;
        AdaptiveTerrainSettings appliedSettings;
        PatchMeshBuilder builder;
        PatchMeshCache meshes;
        AdaptiveTerrainOverlay overlay;
        bool facesHiddenByUs;
        bool hasView;
        TerrainLodView lastView;
        int frame;

        readonly HashSet<PlanetPatchId> displayedSet = new HashSet<PlanetPatchId>();
        readonly List<PlanetPatchId> displayed = new List<PlanetPatchId>();
        readonly List<PlanetPatchId> missing = new List<PlanetPatchId>();
        readonly List<float> missingDistance = new List<float>();
        Func<PlanetPatchId, bool> isProtected;
        MaterialPropertyBlock noProperties;

        /// <summary>The debug view of a planet visual (created disabled on first use; never duplicated).</summary>
        public static AdaptiveTerrainDebugView GetOrCreate(PlanetVisualController visual)
        {
            if (visual == null)
            {
                throw new ArgumentNullException(nameof(visual));
            }
            var view = visual.GetComponent<AdaptiveTerrainDebugView>();
            if (view == null)
            {
                view = visual.gameObject.AddComponent<AdaptiveTerrainDebugView>();
                view.enabled = false;
            }
            return view;
        }

        /// <summary>LOD settings (diagnostic, runtime only). Changes apply on the next tick, without regeneration.</summary>
        public AdaptiveTerrainSettings Settings => settings;

        public AdaptiveTerrainDebugMode Mode
        {
            get => mode;
            set => mode = value;
        }

        public bool FreezeSelection
        {
            get => freezeSelection;
            set => freezeSelection = value;
        }

        public bool ShowVertexGrid
        {
            get => showVertexGrid;
            set => showVertexGrid = value;
        }

        public float PreviewBuildMilliseconds
        {
            get => previewBuildMilliseconds;
            set => previewBuildMilliseconds = Mathf.Max(0f, value);
        }

        /// <summary>Camera driving the selection (null: Camera.main).</summary>
        public Camera ViewCamera { get; set; }

        /// <summary>The planet whose patches are currently held (null when released).</summary>
        public PlanetInstance SampledPlanet => sampler?.Planet;

        public TerrainLodSelection Selection => selector?.Current;

        /// <summary>Number of times patch data was dropped because the displayed planet changed.</summary>
        public int Invalidations { get; private set; }

        /// <summary>Selections actually computed (the selection is skipped while nothing changed).</summary>
        public int SelectionCount { get; private set; }

        public int ResidentMeshes => meshes?.ResidentCount ?? 0;
        public int MeshObjects => meshes?.MeshCount ?? 0;
        public int MeshCapacity => meshes?.Capacity ?? settings.maxPatchMeshes;
        public int PendingPatches { get; private set; }
        public int BuiltLastTick { get; private set; }
        public double BuildMillisecondsLastTick { get; private set; }
        /// <summary>True when the mesh budget could not hold the covering being built.</summary>
        public bool MeshBudgetExhausted { get; private set; }
        /// <summary>True while the preview shows a complete covering (the current one or the previous one).</summary>
        public bool PreviewComplete { get; private set; }
        public int DrawnPatches { get; private set; }
        public int VerticesPerPatch => builder?.VertexCount ?? (settings.patchResolution + 1) * (settings.patchResolution + 1);

        /// <summary>Vertices of the selected covering if it were meshed (selection count x vertices per patch).</summary>
        public long SelectedVertices => (Selection?.Count ?? 0) * (long)VerticesPerPatch;

        /// <summary>Estimated memory of the meshes held: patch surfaces and vertex grids (slots alive) plus the overlay.</summary>
        public long EstimatedMeshBytes => (builder != null ? MeshObjects * (builder.EstimatedMeshBytes + builder.EstimatedGridBytes) : 0)
            + (overlay?.EstimatedBytes ?? 0);

        public AdaptiveTerrainOverlay Overlay => overlay;

        /// <summary>The patch mesh cache (preview), or null.</summary>
        public PatchMeshCache Meshes => meshes;

        /// <summary>
        /// True while the adaptive terrain renderer (V0.2.0B) is on: the overlay then outlines its displayed covering
        /// (what is on screen, stitched transitions included) and the diagnostic mesh preview is unavailable.
        /// </summary>
        public bool FollowingRenderer { get; private set; }

        /// <summary>Patches currently drawn by the preview.</summary>
        public IReadOnlyList<PlanetPatchId> DisplayedPatches => displayed;

        void Update()
        {
            if (Application.isPlaying)
            {
                Tick(ViewCamera != null ? ViewCamera : Camera.main);
            }
        }

        void LateUpdate()
        {
            if (Application.isPlaying)
            {
                Draw(ViewCamera != null ? ViewCamera : Camera.main);
            }
        }

        void OnDisable()
        {
            Release();
        }

        void OnDestroy()
        {
            Release();
        }

        /// <summary>One diagnostic step for a camera: invalidation, selection, overlay or preview meshes.</summary>
        public void Tick(Camera camera)
        {
            if (camera == null)
            {
                return;
            }
            Tick(new TerrainLodView(transform.InverseTransformPoint(camera.transform.position), camera.fieldOfView, Mathf.Max(1, camera.pixelHeight)));
        }

        /// <summary>One diagnostic step for a view in the planet's object space.</summary>
        public void Tick(in TerrainLodView view)
        {
            if (visual == null)
            {
                visual = GetComponent<PlanetVisualController>();
            }
            if (!isActiveAndEnabled || visual == null || visual.Planet == null)
            {
                return;
            }
            frame++;
            AdaptiveTerrainRenderer adaptive = GetComponent<AdaptiveTerrainRenderer>();
            FollowingRenderer = adaptive != null && adaptive.isActiveAndEnabled && adaptive.Snapshot != null;
            if (FollowingRenderer)
            {
                ShowFaces();
                ReleasePreview();
                overlay ??= new AdaptiveTerrainOverlay();
                if (adaptive.Displayed.Count > 0)
                {
                    overlay.Update(adaptive.Displayed, adaptive.Snapshot.Sampler);
                }
                return;
            }
            bool settingsChanged = appliedSettings == null || !appliedSettings.SameAs(settings);
            if (sampler == null || sampler.Planet != visual.Planet)
            {
                Invalidate(visual.Planet);
                settingsChanged = true;
            }
            if (settingsChanged)
            {
                if (builder == null || builder.Resolution != settings.patchResolution)
                {
                    builder = new PatchMeshBuilder(settings.patchResolution);
                    meshes?.Invalidate();
                    displayed.Clear();
                    displayedSet.Clear();
                }
                selector ??= new TerrainLodSelector(settings);
                selector.Settings = settings;
                meshes?.SetCapacity(settings.maxPatchMeshes, isProtected);
                appliedSettings = settings.Clone();
            }
            if ((!freezeSelection || selector.Current.Count == 0) && (settingsChanged || !hasView || !SameView(view, lastView)))
            {
                selector.Select(sampler, view);
                SelectionCount++;
                lastView = view;
                hasView = true;
            }

            if (mode == AdaptiveTerrainDebugMode.Overlay)
            {
                ShowFaces();
                overlay ??= new AdaptiveTerrainOverlay();
                overlay.Update(selector.Current, sampler);
                ReleasePreview();
            }
            else
            {
                HideFaces();
                TickPreview(view);
            }
        }

        static bool SameView(in TerrainLodView a, in TerrainLodView b)
        {
            return a.CameraPosition == b.CameraPosition && a.VerticalFieldOfView == b.VerticalFieldOfView && a.ViewportHeight == b.ViewportHeight;
        }

        void Invalidate(PlanetInstance planet)
        {
            if (sampler != null)
            {
                Invalidations++;
            }
            sampler = new TerrainElevationSampler(planet);
            selector?.Reset();
            meshes?.Invalidate();
            overlay?.Invalidate();
            displayed.Clear();
            displayedSet.Clear();
            hasView = false;
        }

        void TickPreview(in TerrainLodView view)
        {
            if (meshes == null)
            {
                meshes = new PatchMeshCache(settings.maxPatchMeshes);
                isProtected = id => displayedSet.Contains(id) || (selector != null && selector.Current.IsLeaf(id));
            }
            TerrainLodSelection selection = selector.Current;
            missing.Clear();
            missingDistance.Clear();
            foreach (PlanetPatchId leaf in selection.Leaves)
            {
                if (meshes.TryGet(leaf, out PatchMeshCache.Entry entry))
                {
                    entry.LastUsedFrame = frame;
                }
                else
                {
                    missing.Add(leaf);
                    missingDistance.Add((CubeSphereCoordinates.CenterDirection(leaf) * sampler.Radius - view.CameraPosition).sqrMagnitude);
                }
            }
            foreach (PlanetPatchId id in displayed)
            {
                if (meshes.TryGet(id, out PatchMeshCache.Entry entry))
                {
                    entry.LastUsedFrame = frame;
                }
            }

            BuiltLastTick = 0;
            MeshBudgetExhausted = false;
            long start = Stopwatch.GetTimestamp();
            double budgetTicks = previewBuildMilliseconds * Stopwatch.Frequency / 1000.0;
            while (missing.Count > 0)
            {
                // Nearest missing patch first (deterministic: ties by key).
                int best = 0;
                for (int i = 1; i < missing.Count; i++)
                {
                    if (missingDistance[i] < missingDistance[best] || (missingDistance[i] == missingDistance[best] && missing[i].Key < missing[best].Key))
                    {
                        best = i;
                    }
                }
                PlanetPatchId id = missing[best];
                Color tint = AdaptiveTerrainOverlay.LevelColor(id.Level);
                if (meshes.Build(id, builder, sampler, tint, tint * 0.3f, GridLift(id), frame, isProtected) == null)
                {
                    MeshBudgetExhausted = true;
                    if (displayed.Count == 0)
                    {
                        break; // the covering itself exceeds the mesh budget: it stays partial (reported)
                    }
                    // Cannot hold the previous covering and the new one: drop the previous one (temporary holes).
                    displayed.Clear();
                    displayedSet.Clear();
                    continue;
                }
                BuiltLastTick++;
                int last = missing.Count - 1;
                missing[best] = missing[last];
                missingDistance[best] = missingDistance[last];
                missing.RemoveAt(last);
                missingDistance.RemoveAt(last);
                if (Stopwatch.GetTimestamp() - start >= budgetTicks)
                {
                    break;
                }
            }
            BuildMillisecondsLastTick = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            PendingPatches = missing.Count;
            if (missing.Count == 0)
            {
                displayed.Clear();
                displayedSet.Clear();
                foreach (PlanetPatchId leaf in selection.Leaves)
                {
                    displayed.Add(leaf);
                    displayedSet.Add(leaf);
                }
            }
            PreviewComplete = displayed.Count > 0;
        }

        /// <summary>Radial lift of a patch's vertex grid: above its own triangles at orbital depth precision.</summary>
        float GridLift(PlanetPatchId id)
        {
            float spacing = sampler.Radius * Mathf.PI * 0.5f / ((float)builder.Resolution * id.GridSize);
            return Mathf.Max(0.05f * spacing, sampler.Radius * 2e-5f);
        }

        /// <summary>Submits this frame's diagnostic draws for one camera (thumbnail cameras never see them).</summary>
        public void Draw(Camera camera)
        {
            DrawnPatches = 0;
            if (!isActiveAndEnabled || visual == null)
            {
                return;
            }
            bool overlayOnly = FollowingRenderer || mode == AdaptiveTerrainDebugMode.Overlay;
            if (!overlayOnly && (sampler == null || visual.Planet != sampler.Planet))
            {
                return;
            }
            Material material = visual.DebugMaterial;
            float scale = Mathf.Abs(transform.lossyScale.x);
            var renderParams = new RenderParams(material)
            {
                camera = camera,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (visual.BoundingRadius * 3f * scale)),
            };
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            if (overlayOnly)
            {
                if (overlay?.Mesh != null && overlay.Mesh.vertexCount > 0)
                {
                    Graphics.RenderMesh(renderParams, overlay.Mesh, 0, localToWorld);
                }
                return;
            }
            if (meshes == null || selector == null)
            {
                return;
            }
            IReadOnlyList<PlanetPatchId> patches = PreviewComplete ? displayed : selector.Current.Leaves;
            // Same draw matrix as the adaptive renderer: the render frame (floating origin, V0.2.0C) when there is one.
            PlanetRenderFrame frame = GetComponent<PlanetRenderFrame>();
            foreach (PlanetPatchId id in patches)
            {
                if (meshes.TryGet(id, out PatchMeshCache.Entry entry))
                {
                    Matrix4x4 matrix = frame != null ? frame.PatchMatrix(entry.Geometry.Origin) : localToWorld * Matrix4x4.Translate(entry.Geometry.Origin);
                    Graphics.RenderMesh(renderParams, entry.Mesh, 0, matrix);
                    if (showVertexGrid)
                    {
                        Graphics.RenderMesh(renderParams, entry.Grid, 0, matrix);
                    }
                    DrawnPatches++;
                }
            }
        }

        void HideFaces()
        {
            if (!facesHiddenByUs && visual != null)
            {
                visual.SetTerrainFacesHidden(this, true);
                facesHiddenByUs = true;
            }
        }

        void ShowFaces()
        {
            if (facesHiddenByUs && visual != null)
            {
                visual.SetTerrainFacesHidden(this, false);
            }
            facesHiddenByUs = false;
        }

        void ReleasePreview()
        {
            meshes?.Dispose();
            meshes = null;
            displayed.Clear();
            displayedSet.Clear();
            PendingPatches = 0;
            PreviewComplete = false;
            MeshBudgetExhausted = false;
        }

        /// <summary>Releases every resource (meshes, overlay, selection history) and restores the planet's faces.</summary>
        public void Release()
        {
            ShowFaces();
            ReleasePreview();
            overlay?.Dispose();
            overlay = null;
            selector = null;
            sampler = null;
            builder = null;
            appliedSettings = null;
            hasView = false;
            DrawnPatches = 0;
        }
    }
}
