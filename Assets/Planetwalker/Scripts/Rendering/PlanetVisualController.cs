using System;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Presents a <see cref="PlanetInstance"/>: six terrain face meshes plus an ocean
    /// sphere at sea level. Generated objects and meshes are created once and reused on
    /// every regeneration, then destroyed with this component. ExecuteAlways only ensures
    /// this cleanup also runs in edit mode (tests, tooling); nothing is generated in edit
    /// mode unless <see cref="Render"/> is called explicitly.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class PlanetVisualController : MonoBehaviour
    {
        public const string TerrainShaderName = "Planetwalker/PlanetTerrain";
        public const string OceanShaderName = "Planetwalker/PlanetOcean";
        public const string DebugShaderName = "Planetwalker/PlanetDebugUnlit";
        public const string RiverShaderName = "Planetwalker/PlanetRiver";
        public const string LakeShaderName = "Planetwalker/PlanetLake";

        [SerializeField] Material terrainMaterial;
        [SerializeField] Material oceanMaterial;
        [Tooltip("Unlit vertex-color material for development views (created at runtime if empty).")]
        [SerializeField] Material debugMaterial;
        [Tooltip("River ribbon material (created at runtime if empty).")]
        [SerializeField] Material riverMaterial;
        [Tooltip("Lake water material (created at runtime if empty).")]
        [SerializeField] Material lakeMaterial;
        [SerializeField] PlanetColorRamp colorRamp = PlanetColorRamp.CreateDefault();

        [Tooltip("Subdivisions per face of the ocean sphere.")]
        [SerializeField, Range(8, 128)] int oceanResolution = 64;

        [Header("Development")]
        [Tooltip("Development-only visualization. Changing it in Play mode re-renders the current planet.")]
        [SerializeField] PlanetDebugView debugView = PlanetDebugView.None;

        [Tooltip("Hydrology analysis used by the hydrology debug views. Computed on demand only.")]
        [SerializeField] HydrologySettings hydrology = new HydrologySettings();

        [Tooltip("River network trajectories and display. The selection threshold is "
            + "Hydrology > River Network Threshold (single source).")]
        [SerializeField] RiverSettings rivers = new RiverSettings();

        [Tooltip("Lake selection and display (V0.1.1D-B.1). Lakes are computed on demand only.")]
        [SerializeField] LakeSettings lakes = new LakeSettings();

        readonly MeshFilter[] faceFilters = new MeshFilter[CubeSphereGenerator.FaceCount];
        readonly Mesh[] faceMeshes = new Mesh[CubeSphereGenerator.FaceCount];
        readonly MeshRenderer[] faceRenderers = new MeshRenderer[CubeSphereGenerator.FaceCount];
        readonly HydrologyCache hydrologyCache = new HydrologyCache();
        readonly PlanetMeshBuilder meshBuilder = new PlanetMeshBuilder();
        Transform oceanTransform;
        Mesh oceanMesh;
        Material ownedTerrainMaterial;
        Material ownedOceanMaterial;
        Material ownedDebugMaterial;
        PlanetDebugView renderedDebugView;
        float renderedRiverThreshold;
        int renderedHydrologyResolution;
        int notifiedComputeCount;

        // Rivers: data (network cache) and representation (ribbon mesh) are kept separate.
        readonly RiverNetworkCache riverCache = new RiverNetworkCache();
        readonly RiverRibbonBuilder ribbonBuilder = new RiverRibbonBuilder();
        readonly PlanetSurfaceSampler surfaceSampler = new PlanetSurfaceSampler();
        Mesh riverMesh;
        Material ownedRiverMaterial;
        MaterialPropertyBlock riverProperties;
        int terrainVersion;
        int sampledTerrainVersion = -1;
        RibbonKey builtRibbon;
        RiverNetwork notifiedRivers;
        int notifiedRiverBuild = -1;
        int notifiedPathBuild = -1;

        // Lakes: data (lake cache, lake-aware river layer) and representation (water mesh).
        readonly LakeCache lakeCache = new LakeCache();
        readonly LakeMeshBuilder lakeMeshBuilder = new LakeMeshBuilder();
        Mesh lakeMesh;
        Material ownedLakeMaterial;
        MaterialPropertyBlock lakeProperties;
        LakeSet builtLakeSet;
        int builtLakeTerrainVersion = -1;
        LakeSet notifiedLakes;
        LakeSet renderedLakes;
        LakeRiverLayer renderedLakeLayer;
        PlanetInstance contextPlanet;
        Func<Vector3, float> mountainInfluence;

        /// <summary>Everything the ribbon geometry depends on (color / pixel width excluded).</summary>
        struct RibbonKey : IEquatable<RibbonKey>
        {
            public RiverNetwork Network;
            public int NetworkBuild;
            public int PathBuild;
            public int TerrainVersion;
            public float WidthMultiplier, ReferenceWidth, ReferenceArea, WidthExponent, MinWidth, MaxWidth, MinDisplayLength, TaperLength;
            /// <summary>Lake-aware layer used for the ribbons (null: lakes not shown).</summary>
            public LakeRiverLayer Lakes;

            public bool Equals(RibbonKey o)
            {
                return Network == o.Network && NetworkBuild == o.NetworkBuild && PathBuild == o.PathBuild
                    && TerrainVersion == o.TerrainVersion && Lakes == o.Lakes
                    && WidthMultiplier == o.WidthMultiplier && ReferenceWidth == o.ReferenceWidth
                    && ReferenceArea == o.ReferenceArea && WidthExponent == o.WidthExponent
                    && MinWidth == o.MinWidth && MaxWidth == o.MaxWidth
                    && MinDisplayLength == o.MinDisplayLength && TaperLength == o.TaperLength;
            }
        }

        /// <summary>Radius of a sphere enclosing all terrain and ocean, in local units.</summary>
        public float BoundingRadius { get; private set; }

        public PlanetInstance Planet { get; private set; }

        /// <summary>Number of generated child objects (terrain faces + ocean).</summary>
        public int GeneratedObjectCount
        {
            get
            {
                int count = oceanTransform != null ? 1 : 0;
                foreach (MeshFilter filter in faceFilters)
                {
                    if (filter != null)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public PlanetColorRamp ColorRamp => colorRamp;

        public PlanetDebugView DebugView
        {
            get => debugView;
            set => debugView = value;
        }

        /// <summary>Hydrology analysis/display settings. Changes are applied on the next frame
        /// (Play mode) or the next <see cref="Render"/> / <see cref="RefreshColors"/>.</summary>
        public HydrologySettings Hydrology => hydrology;

        /// <summary>Hydrology of the current planet, or null if no hydrology view was shown yet.</summary>
        public HydrologyResult CurrentHydrology { get; private set; }

        public HydrologyCache HydrologyCache => hydrologyCache;

        /// <summary>Raised when a new hydrology analysis becomes current.</summary>
        public event Action<HydrologyResult> HydrologyUpdated;

        public RiverSettings Rivers => rivers;

        /// <summary>River network of the current planet (geographic data), or null if not needed yet.</summary>
        public RiverNetwork CurrentRivers { get; private set; }

        public RiverNetworkCache RiverCache => riverCache;

        public RiverRibbonBuilder RiverRibbons => ribbonBuilder;

        /// <summary>Number of ribbon mesh builds (render-only changes increment this alone).</summary>
        public int RiverRibbonBuildCount { get; private set; }

        /// <summary>True when river ribbons are drawn on the artistic view.</summary>
        public bool RiversVisible { get; private set; }

        public Mesh RiverMesh => riverMesh;

        /// <summary>Raised when the river network or its ribbon geometry changes.</summary>
        public event Action<RiverNetwork> RiversUpdated;

        /// <summary>Lake selection and display settings. Changes are applied on the next frame (Play
        /// mode) or the next <see cref="Render"/> / <see cref="RefreshColors"/>.</summary>
        public LakeSettings Lakes => lakes;

        /// <summary>Lakes of the current planet (geographic data), or null if not needed yet.</summary>
        public LakeSet CurrentLakes { get; private set; }

        /// <summary>Lake-aware river layer used by the ribbons / connectivity view, or null.</summary>
        public LakeRiverLayer CurrentLakeLayer { get; private set; }

        public LakeCache LakeCache => lakeCache;

        public LakeMeshBuilder LakeMeshes => lakeMeshBuilder;

        /// <summary>Number of lake-aware river layer builds.</summary>
        public int LakeLayerBuildCount { get; private set; }

        /// <summary>Number of lake water mesh builds.</summary>
        public int LakeMeshBuildCount { get; private set; }

        /// <summary>True when lake water is drawn on the artistic view.</summary>
        public bool LakesVisible { get; private set; }

        public Mesh LakeMesh => lakeMesh;

        /// <summary>Raised when the lake set or the lake mesh changes.</summary>
        public event Action<LakeSet> LakesUpdated;

        /// <summary>Assigns shared material assets (used by the editor setup utility).</summary>
        public void SetMaterials(Material terrain, Material ocean, Material debug = null, Material river = null, Material lake = null)
        {
            if (lake != null)
            {
                lakeMaterial = lake;
            }
            terrainMaterial = terrain;
            oceanMaterial = ocean;
            if (debug != null)
            {
                debugMaterial = debug;
            }
            if (river != null)
            {
                riverMaterial = river;
            }
        }

        /// <summary>Rebuilds all terrain meshes and resizes the ocean for this planet.</summary>
        public void Render(PlanetInstance planet)
        {
            Planet = planet;
            EnsureObjects();
            PlanetMeshBuilder.ColorContext context = PrepareColorContext();

            float maxRadius = 0f;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                meshBuilder.BuildFace(faceMeshes[face], face, planet, colorRamp, context);
                maxRadius = Mathf.Max(maxRadius, meshBuilder.LastMaxRadius);
            }

            float oceanRadius = planet.OceanRadius;
            oceanTransform.localScale = Vector3.one * oceanRadius;
            BoundingRadius = Mathf.Max(maxRadius, oceanRadius);
            terrainVersion++;
            ApplyViewState();
            UpdateLakes();
            UpdateRivers();
        }

        /// <summary>
        /// Recolors the current planet for the current view without rebuilding geometry
        /// (and without recomputing hydrology unless the terrain or grid resolution changed).
        /// </summary>
        public void RefreshColors()
        {
            if (Planet == null || faceMeshes[0] == null)
            {
                return;
            }
            PlanetMeshBuilder.ColorContext context = PrepareColorContext();
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                meshBuilder.RecolorFace(faceMeshes[face], face, Planet, colorRamp, context);
            }
            ApplyViewState();
            UpdateLakes();
            UpdateRivers();
        }

        void Update()
        {
            // Lets views and hydrology display settings change during Play mode.
            if (!Application.isPlaying || Planet == null)
            {
                return;
            }
            bool hydrologyView = PlanetDebugViews.NeedsHydrology(debugView);
            if (debugView != renderedDebugView
                || (hydrologyView && (hydrology.riverNetworkThreshold != renderedRiverThreshold
                    || hydrology.resolution != renderedHydrologyResolution)))
            {
                RefreshColors();
            }
            else
            {
                // Cheap when nothing changed: only cache keys are compared.
                UpdateLakes();
                UpdateRivers();
            }
        }

        void LateUpdate()
        {
            DrawLakes();
            if (!RiversVisible || riverMesh == null || riverMesh.vertexCount == 0)
            {
                return;
            }
            // Drawn without a GameObject: the planet keeps its 6 faces + ocean structure.
            riverProperties ??= new MaterialPropertyBlock();
            riverProperties.SetColor("_Color", rivers.color);
            riverProperties.SetFloat("_MinPixelHalfWidth", rivers.minPixelHalfWidth);
            riverProperties.SetFloat("_MinLift", Planet.Radius * 0.0002f);
            riverProperties.SetFloat("_LiftPerDistance", 0.0004f);
            riverProperties.SetFloat("_OceanRadius", Planet.OceanRadius);
            var renderParams = new RenderParams(ResolveRiverMaterial())
            {
                matProps = riverProperties,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (BoundingRadius * 2.4f * transform.lossyScale.x)),
            };
            Graphics.RenderMesh(renderParams, riverMesh, 0, transform.localToWorldMatrix);
        }

        HydrologyResult EnsureHydrology()
        {
            HydrologyResult result = hydrologyCache.Get(Planet, hydrology.resolution);
            if (result != CurrentHydrology || hydrologyCache.ComputeCount != notifiedComputeCount)
            {
                CurrentHydrology = result;
                notifiedComputeCount = hydrologyCache.ComputeCount;
                HydrologyUpdated?.Invoke(result);
            }
            return result;
        }

        bool RiversNeeded => (debugView == PlanetDebugView.None && rivers.showRivers) || debugView == PlanetDebugView.RiverNetwork
            || debugView == PlanetDebugView.LakeConnectivity;

        /// <summary>Lake data is computed only for the lake views or when lakes are shown.</summary>
        bool LakesNeeded => PlanetDebugViews.IsLake(debugView)
            || (lakes.showLakes && (debugView == PlanetDebugView.None || debugView == PlanetDebugView.RiverNetwork));

        LakeSet EnsureLakes()
        {
            HydrologyResult hydrology = EnsureHydrology();
            if (contextPlanet != Planet)
            {
                contextPlanet = Planet;
                mountainInfluence = Planet.Height.GetMountainInfluence;
            }
            LakeSet set = lakeCache.Get(hydrology, lakes, new LakeContext(Planet.Definition.seed, mountainInfluence));
            CurrentLakes = set;
            return set;
        }

        /// <summary>Lake-aware river layer for the current network and lakes (rebuilt when either changes).</summary>
        LakeRiverLayer EnsureLakeLayer(RiverNetwork network, LakeSet set)
        {
            if (CurrentLakeLayer == null || CurrentLakeLayer.Network != network || CurrentLakeLayer.Lakes != set)
            {
                CurrentLakeLayer = LakeRiverIntegration.Build(network, set);
                LakeLayerBuildCount++;
            }
            return CurrentLakeLayer;
        }

        /// <summary>
        /// Brings lake data and the water mesh up to date. Invalidation chain: hydrology -> depression
        /// analysis -> selection (lake settings) -> water mesh (also rebuilt for a new terrain mesh);
        /// visibility and color never rebuild anything.
        /// </summary>
        void UpdateLakes()
        {
            if (Planet == null || faceMeshes[0] == null || !LakesNeeded)
            {
                LakesVisible = false;
                return;
            }
            hydrology.Sanitize();
            LakeSet set = EnsureLakes();
            LakesVisible = lakes.showLakes && debugView == PlanetDebugView.None;
            bool changed = false;
            if (LakesVisible && (set != builtLakeSet || builtLakeTerrainVersion != terrainVersion))
            {
                EnsureSurfaceSampler();
                lakeMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
                lakeMeshBuilder.Build(lakeMesh, set, Planet, surfaceSampler);
                builtLakeSet = set;
                builtLakeTerrainVersion = terrainVersion;
                LakeMeshBuildCount++;
                changed = true;
            }
            if (PlanetDebugViews.IsLake(debugView) && set != renderedLakes && debugView != PlanetDebugView.LakeConnectivity)
            {
                // Selection settings changed while a lake view is shown: recolor only.
                RecolorAll();
            }
            if (changed || set != notifiedLakes)
            {
                notifiedLakes = set;
                LakesUpdated?.Invoke(set);
            }
        }

        void EnsureSurfaceSampler()
        {
            if (sampledTerrainVersion != terrainVersion)
            {
                surfaceSampler.Capture(faceMeshes, Planet.Definition.resolution);
                sampledTerrainVersion = terrainVersion;
            }
        }

        void RecolorAll()
        {
            PlanetMeshBuilder.ColorContext context = PrepareColorContext();
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                meshBuilder.RecolorFace(faceMeshes[face], face, Planet, colorRamp, context);
            }
        }

        void DrawLakes()
        {
            if (!LakesVisible || lakeMesh == null || lakeMesh.vertexCount == 0 || Planet == null)
            {
                return;
            }
            // Drawn without a GameObject: the planet keeps its 6 faces + ocean structure.
            lakeProperties ??= new MaterialPropertyBlock();
            lakeProperties.SetColor("_Color", lakes.color);
            lakeProperties.SetFloat("_MinLift", Planet.Radius * 0.00005f);
            lakeProperties.SetFloat("_LiftPerDistance", 0.0002f);
            var renderParams = new RenderParams(ResolveLakeMaterial())
            {
                matProps = lakeProperties,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (BoundingRadius * 2.4f * transform.lossyScale.x)),
            };
            Graphics.RenderMesh(renderParams, lakeMesh, 0, transform.localToWorldMatrix);
        }

        RiverNetwork EnsureRiverNetwork()
        {
            rivers.Sanitize();
            RiverNetwork network = riverCache.Get(EnsureHydrology(), hydrology.riverNetworkThreshold, rivers.SmoothingIterations);
            CurrentRivers = network;
            return network;
        }

        /// <summary>
        /// Brings river data and ribbons up to date. Invalidation chain: terrain -> hydrology ->
        /// network -> trajectories -> ribbons; each stage rebuilds only when its inputs changed.
        /// </summary>
        void UpdateRivers()
        {
            if (Planet == null || faceMeshes[0] == null || !RiversNeeded)
            {
                RiversVisible = false;
                return;
            }
            hydrology.Sanitize();
            RiverNetwork network = EnsureRiverNetwork();
            // Ribbons are also drawn over the river network and lake connectivity debug views, so
            // that topology markers and the actual geometry can be checked together.
            RiversVisible = rivers.showRivers && (debugView == PlanetDebugView.None || debugView == PlanetDebugView.RiverNetwork
                || debugView == PlanetDebugView.LakeConnectivity);
            // Lake-aware ribbons whenever lakes are shown (or inspected in the connectivity view).
            LakeRiverLayer layer = null;
            if (lakes.showLakes || debugView == PlanetDebugView.LakeConnectivity)
            {
                layer = EnsureLakeLayer(network, EnsureLakes());
                if (debugView == PlanetDebugView.LakeConnectivity && layer != renderedLakeLayer)
                {
                    RecolorAll();
                }
            }
            bool changed = false;
            if (RiversVisible)
            {
                var key = new RibbonKey
                {
                    Network = network,
                    NetworkBuild = riverCache.BuildCount,
                    PathBuild = riverCache.PathBuildCount,
                    TerrainVersion = terrainVersion,
                    WidthMultiplier = rivers.widthMultiplier,
                    ReferenceWidth = rivers.referenceWidth,
                    ReferenceArea = rivers.referenceArea,
                    WidthExponent = rivers.widthExponent,
                    MinWidth = rivers.minWidth,
                    MaxWidth = rivers.maxWidth,
                    MinDisplayLength = rivers.minDisplayLength,
                    TaperLength = rivers.sourceTaperLength,
                    Lakes = layer,
                };
                if (!key.Equals(builtRibbon))
                {
                    EnsureSurfaceSampler();
                    riverMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
                    ribbonBuilder.Build(riverMesh, network, surfaceSampler, Planet, rivers, layer);
                    builtRibbon = key;
                    RiverRibbonBuildCount++;
                    changed = true;
                    if (debugView == PlanetDebugView.RiverNetwork)
                    {
                        // Mouth diagnostics come from the ribbon build: refresh the markers.
                        PlanetMeshBuilder.ColorContext context = new PlanetMeshBuilder.ColorContext(debugView, CurrentHydrology,
                            hydrology.riverNetworkThreshold, network, ribbonBuilder);
                        for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                        {
                            meshBuilder.RecolorFace(faceMeshes[face], face, Planet, colorRamp, context);
                        }
                    }
                }
            }
            if (changed || network != notifiedRivers || riverCache.BuildCount != notifiedRiverBuild || riverCache.PathBuildCount != notifiedPathBuild)
            {
                notifiedRivers = network;
                notifiedRiverBuild = riverCache.BuildCount;
                notifiedPathBuild = riverCache.PathBuildCount;
                RiversUpdated?.Invoke(network);
            }
        }

        PlanetMeshBuilder.ColorContext PrepareColorContext()
        {
            hydrology.Sanitize();
            RiverNetwork network = null;
            LakeSet set = null;
            LakeRiverLayer layer = null;
            if (debugView == PlanetDebugView.RiverNetwork)
            {
                network = EnsureRiverNetwork();
            }
            else if (PlanetDebugViews.IsHydrology(debugView))
            {
                EnsureHydrology();
            }
            else if (PlanetDebugViews.IsLake(debugView))
            {
                set = EnsureLakes();
                if (debugView == PlanetDebugView.LakeConnectivity)
                {
                    layer = EnsureLakeLayer(EnsureRiverNetwork(), set);
                }
            }
            renderedLakes = set;
            renderedLakeLayer = layer;
            return new PlanetMeshBuilder.ColorContext(debugView, CurrentHydrology, hydrology.riverNetworkThreshold, network,
                RiversVisible ? ribbonBuilder : null).WithLakes(set, layer);
        }

        void ApplyViewState()
        {
            // Development views are unlit and show the whole surface, so the ocean is hidden.
            Material material = debugView == PlanetDebugView.None ? ResolveTerrainMaterial() : ResolveDebugMaterial();
            foreach (MeshRenderer faceRenderer in faceRenderers)
            {
                if (faceRenderer != null && faceRenderer.sharedMaterial != material)
                {
                    faceRenderer.sharedMaterial = material;
                }
            }
            oceanTransform.gameObject.SetActive(debugView == PlanetDebugView.None);
            renderedDebugView = debugView;
            renderedRiverThreshold = hydrology.riverNetworkThreshold;
            renderedHydrologyResolution = hydrology.resolution;
        }

        void EnsureObjects()
        {
            Material terrain = ResolveTerrainMaterial();
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                if (faceFilters[face] == null)
                {
                    faceMeshes[face] = new Mesh { hideFlags = HideFlags.DontSave };
                    faceFilters[face] = CreateChild("Terrain " + CubeSphereGenerator.GetFaceName(face), faceMeshes[face], terrain);
                    faceRenderers[face] = faceFilters[face].GetComponent<MeshRenderer>();
                }
            }

            if (oceanTransform == null)
            {
                oceanMesh = new Mesh { hideFlags = HideFlags.DontSave };
                PlanetMeshBuilder.BuildUnitSphere(oceanMesh, oceanResolution);
                oceanTransform = CreateChild("Ocean", oceanMesh, ResolveOceanMaterial()).transform;
            }
        }

        MeshFilter CreateChild(string objectName, Mesh mesh, Material material)
        {
            // In edit mode (tests, tooling) generated objects must never be saved into a scene.
            var child = new GameObject(objectName)
            {
                hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave,
            };
            child.layer = gameObject.layer;
            child.transform.SetParent(transform, false);
            var filter = child.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return filter;
        }

        Material ResolveTerrainMaterial()
        {
            if (terrainMaterial != null)
            {
                return terrainMaterial;
            }
            if (ownedTerrainMaterial == null)
            {
                ownedTerrainMaterial = CreateFallbackMaterial(TerrainShaderName);
            }
            return ownedTerrainMaterial;
        }

        Material ResolveOceanMaterial()
        {
            if (oceanMaterial != null)
            {
                return oceanMaterial;
            }
            if (ownedOceanMaterial == null)
            {
                ownedOceanMaterial = CreateFallbackMaterial(OceanShaderName);
            }
            return ownedOceanMaterial;
        }

        Material ResolveRiverMaterial()
        {
            if (riverMaterial != null)
            {
                return riverMaterial;
            }
            if (ownedRiverMaterial == null)
            {
                ownedRiverMaterial = CreateFallbackMaterial(RiverShaderName);
            }
            return ownedRiverMaterial;
        }

        Material ResolveLakeMaterial()
        {
            if (lakeMaterial != null)
            {
                return lakeMaterial;
            }
            if (ownedLakeMaterial == null)
            {
                ownedLakeMaterial = CreateFallbackMaterial(LakeShaderName);
            }
            return ownedLakeMaterial;
        }

        Material ResolveDebugMaterial()
        {
            if (debugMaterial != null)
            {
                return debugMaterial;
            }
            if (ownedDebugMaterial == null)
            {
                ownedDebugMaterial = CreateFallbackMaterial(DebugShaderName);
            }
            return ownedDebugMaterial;
        }

        static Material CreateFallbackMaterial(string shaderName)
        {
            // Shader.Find only works in builds if the shader is referenced or always
            // included; the Genesis setup utility assigns material assets for that reason.
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"Planetwalker: shader '{shaderName}' not found. Run Planetwalker > Setup Genesis Scene.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            return new Material(shader) { name = shaderName + " (Runtime)", hideFlags = HideFlags.DontSave };
        }

        void OnDestroy()
        {
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                if (faceFilters[face] != null)
                {
                    DestroySafe(faceFilters[face].gameObject);
                }
                DestroySafe(faceMeshes[face]);
                faceFilters[face] = null;
                faceMeshes[face] = null;
                faceRenderers[face] = null;
            }
            if (oceanTransform != null)
            {
                DestroySafe(oceanTransform.gameObject);
            }
            DestroySafe(oceanMesh);
            DestroySafe(ownedTerrainMaterial);
            DestroySafe(ownedOceanMaterial);
            DestroySafe(ownedDebugMaterial);
            DestroySafe(ownedRiverMaterial);
            DestroySafe(riverMesh);
            riverMesh = null;
            DestroySafe(ownedLakeMaterial);
            DestroySafe(lakeMesh);
            lakeMesh = null;
            hydrologyCache.Clear();
            riverCache.Clear();
            lakeCache.Clear();
            CurrentHydrology = null;
            CurrentRivers = null;
            CurrentLakes = null;
            CurrentLakeLayer = null;
            builtLakeSet = null;
            RiversVisible = false;
            LakesVisible = false;
            oceanTransform = null;
            oceanMesh = null;
        }

        static void DestroySafe(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
