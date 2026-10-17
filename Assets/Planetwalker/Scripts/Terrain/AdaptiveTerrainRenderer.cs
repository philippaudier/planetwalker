using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// TERRAIN RENDERER: Adaptive (V0.2.0B). Displays the planet's terrain as adaptive, crack-free patches with the
    /// natural appearance of the legacy faces (same color rule, same material and lighting), built progressively.
    /// Enabled = adaptive; disabled = legacy (every resource released, the six faces shown). Lives on the planet
    /// visual's GameObject; never regenerates the planet and never touches the world's configuration.
    /// </summary>
    /// <remarks>
    /// Per frame (<see cref="Tick(Camera)"/>): detect a new terrain generation (planet, Natural Planet data, color
    /// ramp, color space, patch resolution: a new <see cref="AdaptiveSurfaceSnapshot"/>; older work is cancelled and
    /// its results discarded); re-run the LOD selection only when the camera moved noticeably; upload a bounded
    /// number of finished patches; advance the displayed <see cref="AdaptiveCovering"/> by atomic, balanced
    /// replacements (no hole, no crack); request the missing patches from the <see cref="PatchBuildScheduler"/>
    /// (worker threads, or a per-frame time budget); re-stitch the patches whose neighbors changed (index buffers
    /// only). The legacy faces stay visible until the adaptive covering of the current generation is complete and
    /// has caught up with the target (warm-up), so switching or regenerating never shows an empty planet.
    /// Only the default view is adaptive: scientific views use the legacy faces.
    /// Precision: patch vertices are relative to their patch origin and the draw matrix is computed in one place
    /// (<see cref="PatchMatrix"/>), so a future floating origin only changes that matrix.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Planetwalker/Adaptive Terrain Renderer")]
    public sealed class AdaptiveTerrainRenderer : MonoBehaviour
    {
        [SerializeField] AdaptiveTerrainSettings settings = new AdaptiveTerrainSettings();
        [Tooltip("Build patch geometry on worker threads (off: on the main thread within the construction budget).")]
        [SerializeField] bool backgroundConstruction = true;
        [Tooltip("Main-thread construction time per frame when background construction is off (at least one patch).")]
        [Range(0.5f, 50f)]
        [SerializeField] float constructionMilliseconds = 4f;
        [Tooltip("Finished patches uploaded to meshes per frame (main thread).")]
        [Range(1, 256)]
        [SerializeField] int maxUploadsPerFrame = 24;
        [Tooltip("Requests queued or building at once; older, unneeded requests are cancelled.")]
        [Range(4, 512)]
        [SerializeField] int maxPendingBuilds = 64;
        [Tooltip("Worker threads (0: automatic, processor count - 2, between 1 and 4).")]
        [Range(0, 8)]
        [SerializeField] int workerThreads;
        [Tooltip("Keep the current target covering while the camera moves.")]
        [SerializeField] bool freezeSelection;
        [Tooltip("Re-run the LOD selection when the camera moved by more than this fraction of its altitude.")]
        [Range(0f, 0.1f)]
        [SerializeField] float reselectAltitudeFraction = 0.002f;
        [Tooltip("Frames after which an incomplete warm-up still switches from the legacy faces (the covering is complete, only coarser).")]
        [Range(1, 600)]
        [SerializeField] int maxWarmupFrames = 120;
        [Tooltip("Close-range ground layer (V0.2.0D, shader only): soil patches, rock on locally steep ground, fine bump. "
            + "Shown with surface detail only; 0 = off.")]
        [Range(0f, 1f)]
        [SerializeField] float groundDetailStrength = 1f;

        [Tooltip("Surface geology appearance (V0.2.0E, shader only): bedrock, sediment and soil, rock families, strata, "
            + "erosion streaks, gravel near the ground. Off = the V0.2.0D look.")]
        [SerializeField] bool geologyEnabled = true;
        [Range(0f, 1f)]
        [SerializeField] float geologyStrength = 1f;
        [SerializeField] GeologyDebugView geologyDebugView = GeologyDebugView.None;
        [Tooltip("Surface materials (V0.2.0F): physically based CC0 scans distributed by the geology, or the V0.2.0E procedural look.")]
        [SerializeField] SurfaceMaterialMode materialMode = SurfaceMaterialMode.Pbr;
        [Range(0f, 1f)]
        [SerializeField] float pbrDetailStrength = 1f;
        [SerializeField] SurfaceMaterialDebugView materialDebugView = SurfaceMaterialDebugView.FinalLit;

        /// <summary>Period (meters) of the close-range ground noise; its origin is snapped to this lattice.</summary>
        public const double GroundNoisePeriodMeters = 1024.0;
        static readonly int GroundOriginId = Shader.PropertyToID("_GroundOrigin");
        static readonly int GroundParamsId = Shader.PropertyToID("_GroundParams");
        static readonly int GeoParamsId = Shader.PropertyToID("_GeoParams");
        static readonly int PatchStitchId = Shader.PropertyToID("_PatchStitch");
        static readonly int PatchMorphId = Shader.PropertyToID("_PatchMorph");
        static readonly int SurfParamsId = Shader.PropertyToID("_SurfParams");
        static readonly int SurfAlbedoId = Shader.PropertyToID("_SurfAlbedo");
        static readonly int SurfNormalId = Shader.PropertyToID("_SurfNormal");
        static readonly int SurfMaskId = Shader.PropertyToID("_SurfMask");
        static readonly int SurfMeanId = Shader.PropertyToID("_SurfMean");
        readonly Vector4[] surfaceMeans = new Vector4[SurfaceMaterials.Count];
        string statusSource, pbrStatus;
        TerrainVertex[] packed;

        PlanetVisualController visual;
        PlanetRenderFrame renderFrame;
        AdaptiveSurfaceSnapshot snapshot;
        int generation;
        TerrainLodSelector selector;
        readonly AdaptiveCovering covering = new AdaptiveCovering();
        PatchBuildScheduler scheduler;
        PatchMeshCache meshes;
        readonly List<PatchRequest> needed = new List<PatchRequest>();
        readonly HashSet<PlanetPatchId> neededSet = new HashSet<PlanetPatchId>();
        MaterialPropertyBlock block;
        Func<PlanetPatchId, bool> isReady, isProtected;
        Func<PlanetPatchId, float> priority;
        AdaptiveTerrainSettings appliedSettings;
        TerrainLodView lastView;
        bool hasView;
        bool warmed;
        bool facesHiddenByUs;
        int warmupFrames;
        int frame;
        int stitchedVersion = -1;

        /// <summary>The planet's adaptive renderer (created disabled, i.e. Legacy, on first use; never duplicated).</summary>
        public static AdaptiveTerrainRenderer GetOrCreate(PlanetVisualController visual)
        {
            if (visual == null)
            {
                throw new ArgumentNullException(nameof(visual));
            }
            var renderer = visual.GetComponent<AdaptiveTerrainRenderer>();
            if (renderer == null)
            {
                renderer = visual.gameObject.AddComponent<AdaptiveTerrainRenderer>();
                renderer.enabled = false;
            }
            return renderer;
        }

        /// <summary>LOD settings (runtime only, never archived). Changes apply on the next tick, without regeneration.</summary>
        public AdaptiveTerrainSettings Settings => settings;

        public bool BackgroundConstruction { get => backgroundConstruction; set => backgroundConstruction = value; }
        public float ConstructionMilliseconds { get => constructionMilliseconds; set => constructionMilliseconds = Mathf.Max(0.1f, value); }
        public int MaxUploadsPerFrame { get => maxUploadsPerFrame; set => maxUploadsPerFrame = Mathf.Max(1, value); }
        public int MaxPendingBuilds { get => maxPendingBuilds; set => maxPendingBuilds = Mathf.Max(1, value); }
        public bool FreezeSelection { get => freezeSelection; set => freezeSelection = value; }
        public int MaxWarmupFrames { get => maxWarmupFrames; set => maxWarmupFrames = Mathf.Max(1, value); }
        public float GroundDetailStrength { get => groundDetailStrength; set => groundDetailStrength = Mathf.Clamp01(value); }
        /// <summary>Surface geology appearance (V0.2.0E; presentation only, never archived, no rebuild).</summary>
        public bool GeologyEnabled { get => geologyEnabled; set => geologyEnabled = value; }
        public float GeologyStrength { get => geologyStrength; set => geologyStrength = Mathf.Clamp01(value); }
        public GeologyDebugView GeologyDebug { get => geologyDebugView; set => geologyDebugView = value; }
        /// <summary>Surface material mode (V0.2.0F; presentation only, never archived, no rebuild).</summary>
        public SurfaceMaterialMode MaterialMode { get => materialMode; set => materialMode = value; }
        public float PbrDetailStrength { get => pbrDetailStrength; set => pbrDetailStrength = Mathf.Clamp01(value); }
        public SurfaceMaterialDebugView MaterialDebug { get => materialDebugView; set => materialDebugView = value; }

        /// <summary>True when the PBR materials are drawn (mode PBR, geology on, the library's arrays assembled).</summary>
        public bool PbrActive { get; private set; }

        /// <summary>Why the PBR mode is or is not active (diagnostics).</summary>
        public string MaterialStatus { get; private set; } = "";

        /// <summary>Camera driving the selection and receiving the draws' priority (null: Camera.main).</summary>
        public Camera ViewCamera { get; set; }

        /// <summary>True while the adaptive patches are the displayed terrain (the legacy faces are hidden).</summary>
        public bool IsDisplaying => isActiveAndEnabled && warmed && snapshot != null && Suspended == false;

        /// <summary>True while a scientific view is shown (the legacy faces render it).</summary>
        public bool Suspended { get; private set; }

        public AdaptiveSurfaceSnapshot Snapshot => snapshot;
        public PlanetInstance SampledPlanet => snapshot?.Planet;
        public int Generation => generation;
        public TerrainLodSelection Target => selector?.Current;
        public IPatchCovering Displayed => covering;
        public AdaptiveCovering Covering => covering;
        public PatchBuildScheduler Scheduler => scheduler;
        public PatchMeshCache Meshes => meshes;

        public int SelectionCount { get; private set; }
        public double LastSelectionMilliseconds { get; private set; }
        public int UploadsLastTick { get; private set; }
        public double UploadMillisecondsLastTick { get; private set; }
        public int TotalUploads { get; private set; }
        public int Restitches { get; private set; }
        public int DrawnPatches { get; private set; }
        public int Invalidations { get; private set; }
        /// <summary>Uploads dropped because every mesh slot was protected (mesh budget).</summary>
        public int BlockedUploads { get; private set; }
        /// <summary>Displayed edges more than one level apart (must stay 0: unstitchable).</summary>
        public int InvalidEdges { get; private set; }
        public int TransitionEdges { get; private set; }
        public double TickMilliseconds { get; private set; }

        public int ResidentPatches => meshes?.ResidentCount ?? 0;
        public int MeshSlots => meshes?.MeshCount ?? 0;

        /// <summary>Estimated GPU memory of the patch meshes held (interleaved <see cref="TerrainVertex"/>, 16-bit indices).</summary>
        public long EstimatedMeshBytes
        {
            get
            {
                if (snapshot == null || meshes == null)
                {
                    return 0;
                }
                PatchTopology t = snapshot.Topology;
                return MeshSlots * ((long)t.VertexCount * TerrainVertex.Size + (long)t.Indices(0).Length * 2);
            }
        }

        public int EffectiveWorkerCount => !backgroundConstruction ? 0 : workerThreads > 0 ? workerThreads : Mathf.Clamp(Environment.ProcessorCount - 2, 1, 4);

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
                Draw(null);
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

        /// <summary>One step for a camera (frustum included).</summary>
        public void Tick(Camera camera)
        {
            if (camera == null)
            {
                return;
            }
            ResolveFrame();
            // Camera in planet space through the render frame (double; V0.2.0C) when there is one. The frustum stays
            // in the root's matrix: plane offsets of ~1e-5 units are irrelevant to culling.
            Vector3 cameraPosition = renderFrame != null ? renderFrame.RenderToPlanet(camera.transform.position).ToVector3()
                : transform.InverseTransformPoint(camera.transform.position);
            var view = new TerrainLodView(cameraPosition, camera.fieldOfView, Mathf.Max(1, camera.pixelHeight))
            {
                Frustum = GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix * transform.localToWorldMatrix),
            };
            Tick(view);
        }

        /// <summary>One step for a view in the planet's object space.</summary>
        public void Tick(in TerrainLodView view)
        {
            long start = Stopwatch.GetTimestamp();
            if (visual == null)
            {
                visual = GetComponent<PlanetVisualController>();
            }
            ResolveFrame();
            if (!isActiveAndEnabled || visual == null || visual.Planet == null)
            {
                return;
            }
            frame++;
            // Scientific views use the legacy faces; the surface detail views (V0.2.0C) are rendered adaptively too.
            Suspended = visual.DebugView != PlanetDebugView.None && !PlanetDebugViews.IsSurfaceDetail(visual.DebugView);
            if (Suspended)
            {
                ShowFaces();
                return;
            }
            if (!EnsureSnapshot())
            {
                ShowFaces();
                return;
            }
            bool settingsChanged = appliedSettings == null || !appliedSettings.SameAs(settings);
            if (settingsChanged)
            {
                selector ??= new TerrainLodSelector(settings);
                selector.Settings = settings;
                appliedSettings = settings.Clone();
            }
            meshes ??= new PatchMeshCache(settings.maxPatchMeshes);
            // Room for the displayed covering, the target's replacements and a margin (mid-transition both can be resident).
            meshes.SetCapacity(Math.Max(settings.maxPatchMeshes, 2 * settings.maxLeafPatches + 64), isProtected);
            scheduler ??= new PatchBuildScheduler(EffectiveWorkerCount, maxPendingBuilds);
            scheduler.WorkerCount = EffectiveWorkerCount;
            scheduler.MaxPending = maxPendingBuilds;

            // 1. Target covering (only when the view changed noticeably).
            if (selector.Current.Count == 0 || (!freezeSelection && (settingsChanged || !hasView || ViewChanged(view))))
            {
                long s = Stopwatch.GetTimestamp();
                selector.Select(snapshot.Sampler, view);
                LastSelectionMilliseconds = (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
                SelectionCount++;
                lastView = view;
                hasView = true;
            }

            // 2. Finished patches -> meshes (bounded).
            Upload();

            // 3. Displayed covering: atomic, balanced replacements; list what is missing.
            covering.Update(selector.Current, settings.EffectiveMinLevel, isReady, priority, needed);
            neededSet.Clear();
            foreach (PatchRequest r in needed)
            {
                neededSet.Add(r.Id);
            }

            // 4. Construction requests (bounded; unneeded ones cancelled).
            scheduler.Request(snapshot, needed, isReady);
            scheduler.RunSynchronous(constructionMilliseconds);

            // 5. Stitching of the displayed patches (index buffers only, when the covering changed).
            Restitch();

            // 6. Legacy faces until the adaptive covering is complete and has caught up (or the warm-up timed out).
            if (!warmed && covering.IsComplete)
            {
                warmupFrames++;
                warmed = covering.Converged || warmupFrames >= maxWarmupFrames;
            }
            if (warmed)
            {
                HideFaces();
            }
            else
            {
                ShowFaces();
            }
            TickMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        bool ViewChanged(in TerrainLodView view)
        {
            if (view.VerticalFieldOfView != lastView.VerticalFieldOfView || view.ViewportHeight != lastView.ViewportHeight)
            {
                return true;
            }
            // Altitude above the ground under the camera (not above the planet's lowest possible point, ~250 km under
            // a plain on an exaggerated Earth-scale planet: the selection then lagged ~0.5 km behind a low camera).
            float cameraDistance = view.CameraPosition.magnitude;
            Vector3 down = cameraDistance > 0f ? view.CameraPosition / cameraDistance : Vector3.up;
            TerrainElevationSampler s = snapshot.Sampler;
            float groundRadius = s.Radius * (1f + Mathf.Max(s.Elevation(down), s.Planet.SeaLevel));
            float altitude = Mathf.Max(cameraDistance - groundRadius, s.Radius * 1e-6f);
            if ((view.CameraPosition - lastView.CameraPosition).magnitude > reselectAltitudeFraction * altitude)
            {
                return true;
            }
            // Orientation (frustum weighting): re-select when the view direction turned noticeably.
            if (view.Frustum != null && lastView.Frustum != null && settings.offscreenErrorScale < 1f)
            {
                return Vector3.Dot(view.Frustum[4].normal, lastView.Frustum[4].normal) < 0.9995f;
            }
            return false;
        }

        /// <summary>Creates a new snapshot when the planet or its appearance changed. False when none can be made yet.</summary>
        bool EnsureSnapshot()
        {
            PlanetInstance planet = visual.Planet;
            PlanetDebugView debugView = PlanetDebugViews.IsSurfaceDetail(visual.DebugView) ? visual.DebugView : PlanetDebugView.None;
            NaturalSurface natural = debugView == PlanetDebugView.None && visual.NaturalPlanetShown ? visual.CurrentNaturalSurface : null;
            if (debugView == PlanetDebugView.None && visual.NaturalPlanetShown && natural == null)
            {
                return false; // the Natural Planet data is not computed yet
            }
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            int resolution = Math.Max(2, settings.patchResolution & ~1);
            if (snapshot != null && snapshot.Planet == planet && snapshot.Natural == natural && snapshot.LinearColors == linear
                && snapshot.Resolution == resolution && snapshot.DebugView == debugView && snapshot.Ramp.ContentEquals(visual.ColorRamp))
            {
                return true;
            }
            bool newPlanet = snapshot == null || snapshot.Planet != planet;
            if (snapshot != null)
            {
                Invalidations++;
            }
            generation++;
            snapshot = new AdaptiveSurfaceSnapshot(generation, planet, natural, visual.ColorRamp, linear, resolution, debugView);
            isReady ??= id => meshes != null && meshes.TryGet(id, out PatchMeshCache.Entry e) && e.Generation == snapshot.Generation;
            isProtected ??= id => covering.IsLeaf(id) || neededSet.Contains(id);
            priority ??= id => selector.ScreenError(id) + 1e-3f * (PlanetPatchId.MaxLevel - id.Level);
            scheduler?.CancelAll();
            meshes?.Invalidate();
            covering.Reset();
            stitchedVersion = -1;
            if (newPlanet)
            {
                selector?.Reset();
                hasView = false;
            }
            warmed = false;
            warmupFrames = 0;
            return true;
        }

        void Upload()
        {
            UploadsLastTick = 0;
            long start = Stopwatch.GetTimestamp();
            PatchTopology topology = snapshot.Topology;
            while (UploadsLastTick < maxUploadsPerFrame && scheduler.TryTakeResult(snapshot, out PatchGeometryData data))
            {
                PatchMeshCache.Entry entry = meshes.Acquire(data.Id, frame, isProtected);
                if (entry == null)
                {
                    BlockedUploads++;
                    scheduler.Recycle(data);
                    continue;
                }
                Mesh mesh = entry.Mesh;
                mesh.Clear();
                mesh.name = "Adaptive Terrain " + data.Id;
                mesh.indexFormat = topology.VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                // One interleaved stream (V0.2.0E): positions, normals and colors as before, plus the coarser level's
                // normal and the surface geology (TerrainVertex).
                int count = topology.VertexCount;
                if (packed == null || packed.Length != count)
                {
                    packed = new TerrainVertex[count];
                }
                TerrainVertex.Pack(data, packed, count);
                mesh.SetVertexBufferParams(count, TerrainVertex.Layout);
                mesh.SetVertexBufferData(packed, 0, 0, count, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                int mask = covering.IsLeaf(data.Id) ? covering.StitchMask(data.Id) : 0;
                mesh.SetIndices(topology.Indices(mask), MeshTopology.Triangles, 0, false);
                mesh.bounds = data.LocalBounds;
                entry.Mask = mask;
                entry.Generation = data.Generation;
                entry.Geometry = new PatchGeometry
                {
                    Id = data.Id,
                    Origin = data.Origin,
                    LocalBounds = data.LocalBounds,
                    MinElevation = data.MinElevation,
                    MaxElevation = data.MaxElevation,
                    Milliseconds = data.BuildMilliseconds,
                };
                scheduler.Recycle(data);
                UploadsLastTick++;
                TotalUploads++;
            }
            UploadMillisecondsLastTick = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        void Restitch()
        {
            if (covering.Version == stitchedVersion)
            {
                return;
            }
            stitchedVersion = covering.Version;
            PatchTopology topology = snapshot.Topology;
            int invalid = 0, transitions = 0;
            foreach (PlanetPatchId leaf in covering.Leaves)
            {
                int mask = 0;
                for (int d = 0; d < 4; d++)
                {
                    if (covering.TryGetCoveringLeaf(PatchNeighbors.Neighbor(leaf, (PatchDirection)d), out PlanetPatchId cover) && cover.Level < leaf.Level)
                    {
                        mask |= 1 << d;
                        transitions++;
                        if (cover.Level < leaf.Level - 1)
                        {
                            invalid++;
                        }
                    }
                }
                if (meshes.TryGet(leaf, out PatchMeshCache.Entry entry) && entry.Mask != mask)
                {
                    entry.Mesh.SetIndices(topology.Indices(mask), MeshTopology.Triangles, 0, false);
                    entry.Mask = mask;
                    Restitches++;
                }
            }
            InvalidEdges = invalid;
            TransitionEdges = transitions;
        }

        /// <summary>
        /// Object-to-world matrix of a patch: the planet's <see cref="PlanetRenderFrame"/> (floating origin, V0.2.0C;
        /// translation computed in double) when there is one, else the root matrix.
        /// </summary>
        public Matrix4x4 PatchMatrix(Vector3 origin) =>
            renderFrame != null ? renderFrame.PatchMatrix(origin) : transform.localToWorldMatrix * Matrix4x4.Translate(origin);

        /// <summary>The planet's render frame, cached once per tick (it may appear after this component).</summary>
        void ResolveFrame()
        {
            if (renderFrame == null)
            {
                renderFrame = GetComponent<PlanetRenderFrame>();
            }
        }

        /// <summary>Submits the displayed patches for one camera (null: every camera, like the face renderers).</summary>
        public void Draw(Camera camera)
        {
            DrawnPatches = 0;
            if (!IsDisplaying || visual == null)
            {
                return;
            }
            if (visual.Planet != snapshot.Planet)
            {
                // Regenerated after this frame's tick: the legacy faces (already rebuilt) cover the planet until the
                // next tick starts the new generation. Never the old planet, never an empty one.
                warmed = false;
                ShowFaces();
                return;
            }
            block ??= new MaterialPropertyBlock();
            visual.WriteAdaptiveTerrainProperties(block);
            WriteGroundDetail(block, camera != null ? camera : ViewCamera != null ? ViewCamera : Camera.main);
            var renderParams = new RenderParams(snapshot.DebugView == PlanetDebugView.None ? visual.TerrainMaterial : visual.DebugMaterial)
            {
                camera = camera,
                matProps = block,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            float scale = Mathf.Abs(transform.lossyScale.x);
            block.SetFloat(PatchMorphId, (float)snapshot.NormalMorphRows / snapshot.Resolution);
            foreach (PlanetPatchId leaf in covering.Leaves)
            {
                if (!meshes.TryGet(leaf, out PatchMeshCache.Entry entry))
                {
                    continue; // cannot happen for a displayed leaf (protected); kept defensive
                }
                entry.LastUsedFrame = frame;
                // Edges stitched to a coarser neighbor (V0.2.0E): the normals blend there towards the coarser level's,
                // which the neighbor shows on the shared vertices (no lighting step at LOD boundaries). The block's
                // values are copied by each draw call.
                int mask = entry.Mask;
                block.SetVector(PatchStitchId, new Vector4(mask & 1, (mask >> 1) & 1, (mask >> 2) & 1, (mask >> 3) & 1));
                Matrix4x4 matrix = PatchMatrix(entry.Geometry.Origin);
                Bounds local = entry.Geometry.LocalBounds;
                renderParams.worldBounds = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.one * (2f * local.extents.magnitude * scale));
                Graphics.RenderMesh(renderParams, entry.Mesh, 0, matrix);
                DrawnPatches++;
            }
        }

        /// <summary>
        /// Close-range ground layer uniforms (V0.2.0D): the planet-space camera position, in meters, snapped to the
        /// noise lattice in double and placed in render space through the floating origin (a small, precise float
        /// vector near the camera). The shader's noise is periodic over the lattice, so a new snapped origin changes
        /// nothing on screen. Off (enabled 0) without surface detail, or without a camera.
        /// </summary>
        public void WriteGroundDetail(MaterialPropertyBlock target, Camera viewer)
        {
            PlanetInstance planet = snapshot?.Planet;
            bool ground = planet != null && planet.Definition.surfaceDetail && groundDetailStrength > 0f;
            bool geology = planet != null && geologyEnabled && geologyStrength > 0f;
            target.SetVector(GeoParamsId, geology && viewer != null
                ? new Vector4(geologyStrength, (float)geologyDebugView, 1f, 0f)
                : new Vector4(0f, (float)geologyDebugView, 0f, 0f));
            if (viewer == null || (!ground && !geology))
            {
                target.SetVector(GroundParamsId, Vector4.zero);
                WriteSurfaceMaterials(target, false);
                return;
            }
            PlanetScale scale = planet.Scale;
            Vector3d camera = renderFrame != null
                ? renderFrame.RenderToPlanet(viewer.transform.position)
                : new Vector3d(transform.InverseTransformPoint(viewer.transform.position));
            double period = GroundNoisePeriodMeters;
            double metersPerUnit = scale.MetersPerUnit;
            var snapped = new Vector3d(
                Math.Round(camera.x * metersPerUnit / period) * period * scale.UnitsPerMeter,
                Math.Round(camera.y * metersPerUnit / period) * period * scale.UnitsPerMeter,
                Math.Round(camera.z * metersPerUnit / period) * period * scale.UnitsPerMeter);
            Vector3 origin = renderFrame != null ? renderFrame.PlanetToRender(snapped) : transform.TransformPoint(snapped.ToVector3());
            target.SetVector(GroundOriginId, new Vector4(origin.x, origin.y, origin.z, (float)metersPerUnit));
            // The origin and seed also anchor the geology's close-range noise (V0.2.0E); z: the V0.2.0D ground layer.
            target.SetVector(GroundParamsId, new Vector4(groundDetailStrength, planet.Definition.seed & 0xFFFF, ground ? 1f : 0f, 0f));
            WriteSurfaceMaterials(target, geology);
        }

        /// <summary>
        /// Physically based materials (V0.2.0F): the library's arrays and per-layer means / tile sizes on the property
        /// block, and the mode. PBR needs the geology (it distributes the materials) and the assembled arrays; otherwise
        /// the procedural V0.2.0E appearance is drawn (mode 0) and <see cref="MaterialStatus"/> says why.
        /// </summary>
        public void WriteSurfaceMaterials(MaterialPropertyBlock target, bool geology)
        {
            PbrActive = false;
            if (materialMode != SurfaceMaterialMode.Pbr)
            {
                MaterialStatus = "procedural (V0.2.0E)";
            }
            else if (!geology)
            {
                MaterialStatus = "procedural: PBR needs the geological appearance";
            }
            else
            {
                PlanetSurfaceLibrary library = PlanetSurfaceLibrary.Load();
                if (library == null)
                {
                    MaterialStatus = "procedural: no surface library (Planetwalker > Build Surface Material Library)";
                }
                else if (!library.EnsureArrays())
                {
                    if (!ReferenceEquals(statusSource, library.Status))
                    {
                        statusSource = library.Status;
                        pbrStatus = "procedural: " + statusSource;
                    }
                    MaterialStatus = pbrStatus;
                }
                else
                {
                    target.SetTexture(SurfAlbedoId, library.Albedo);
                    target.SetTexture(SurfNormalId, library.Normal);
                    target.SetTexture(SurfMaskId, library.Mask);
                    for (int k = 0; k < SurfaceMaterials.Count; k++)
                    {
                        Color m = library.meanAlbedo[k];
                        surfaceMeans[k] = new Vector4(m.r, m.g, m.b, library.tileMeters[k]);
                    }
                    target.SetVectorArray(SurfMeanId, surfaceMeans);
                    PbrActive = true;
                    if (!ReferenceEquals(statusSource, library.Status))
                    {
                        statusSource = library.Status; // the string is built once, not every frame
                        pbrStatus = "PBR: " + statusSource;
                    }
                    MaterialStatus = pbrStatus;
                }
            }
            target.SetVector(SurfParamsId, new Vector4(PbrActive ? 1f : 0f, pbrDetailStrength, PbrActive ? (float)materialDebugView : 0f, 0f));
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

        /// <summary>Releases everything (meshes, pending work, history) and shows the legacy faces again.</summary>
        public void Release()
        {
            ShowFaces();
            scheduler?.Dispose();
            scheduler = null;
            meshes?.Dispose();
            meshes = null;
            covering.Reset();
            selector = null;
            snapshot = null;
            appliedSettings = null;
            hasView = false;
            warmed = false;
            warmupFrames = 0;
            stitchedVersion = -1;
            DrawnPatches = 0;
            needed.Clear();
            neededSet.Clear();
        }
    }
}
