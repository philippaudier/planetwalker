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
    public sealed partial class PlanetVisualController : MonoBehaviour
    {
        public const string TerrainShaderName = "Planetwalker/PlanetTerrain";
        public const string OceanShaderName = "Planetwalker/PlanetOcean";
        public const string DebugShaderName = "Planetwalker/PlanetDebugUnlit";
        public const string RiverShaderName = "Planetwalker/PlanetRiver";
        public const string LakeShaderName = "Planetwalker/PlanetLake";
        public const string AtmosphereShaderName = "Planetwalker/PlanetAtmosphere";

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

        [Tooltip("Climate analysis (V0.1.1E-A). Computed on demand only, when a climate view is shown.")]
        [SerializeField] ClimateSettings climate = new ClimateSettings();

        [Tooltip("Climate winds view: color the wind heading (compass) instead of a neutral speed background.")]
        [SerializeField] bool climateWindDirectionColors;

        [Tooltip("Biome classification (V0.1.1E-B.1). Computed on demand: biome views and the Natural Planet.")]
        [SerializeField] BiomeSettings biomes = new BiomeSettings();

        [Tooltip("Natural Planet appearance (V0.1.2A): the default view's colors. Artistic only.")]
        [SerializeField] NaturalSurfaceSettings surface = new NaturalSurfaceSettings();

        [Tooltip("Oceans and atmosphere appearance (V0.1.2B). Rendering only.")]
        [SerializeField] OceanAtmosphereSettings oceanAtmosphere = new OceanAtmosphereSettings();

        [Tooltip("Atmosphere shell material (created at runtime if empty).")]
        [SerializeField] Material atmosphereMaterial;

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
        LakeSet lakeGeometrySet;
        int lakeGeometryTerrainVersion = -1;
        (int, int, int) shorelineViewKey = (-1, -1, -1);
        LakeSet notifiedLakes;
        LakeSet renderedLakes;
        LakeRiverLayer renderedLakeLayer;
        PlanetInstance contextPlanet;

        // Climate: data (cache) and representation (vertex colors, wind arrows).
        readonly ClimateCache climateCache = new ClimateCache();
        readonly ClimateWindArrows windArrows = new ClimateWindArrows();
        Mesh windArrowMesh;
        ClimateResult builtArrowClimate;
        int builtArrowTerrainVersion = -1;
        ClimateResult notifiedClimate;
        bool renderedWindDirectionColors;

        // Biomes: data only (cache); shown through vertex colors.
        readonly BiomeCache biomeCache = new BiomeCache();
        BiomeResult notifiedBiomes;

        // Natural Planet: terrain slope field (per terrain) and the color sampler (per data + settings).
        readonly TerrainSlopeCache slopeCache = new TerrainSlopeCache();
        NaturalSurface naturalSurface;
        bool renderedNatural;
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

        /// <summary>Number of lake water geometry builds (water mesh and rendered shoreline used by the ribbons).</summary>
        public int LakeMeshBuildCount { get; private set; }

        /// <summary>True when lake water is drawn on the artistic view.</summary>
        public bool LakesVisible { get; private set; }

        public Mesh LakeMesh => lakeMesh;

        /// <summary>Raised when the lake set or the lake mesh changes.</summary>
        public event Action<LakeSet> LakesUpdated;

        /// <summary>Climate settings. Changes are applied on the next frame (Play mode) or the next
        /// <see cref="Render"/> / <see cref="RefreshColors"/>, and only while a climate view is shown.</summary>
        public ClimateSettings Climate => climate;

        /// <summary>Winds view: heading colors instead of the neutral background (display only).</summary>
        public bool ClimateWindDirectionColors
        {
            get => climateWindDirectionColors;
            set => climateWindDirectionColors = value;
        }

        /// <summary>Climate of the current planet, or null if no climate view was shown yet.</summary>
        public ClimateResult CurrentClimate { get; private set; }

        public ClimateCache ClimateCache => climateCache;

        /// <summary>Wind arrows of the winds view (null until first shown).</summary>
        public Mesh WindArrowMesh => windArrowMesh;

        /// <summary>Raised when a new climate result becomes current.</summary>
        public event Action<ClimateResult> ClimateUpdated;

        /// <summary>Biome settings. Changes are applied on the next frame (Play mode) or the next
        /// <see cref="Render"/> / <see cref="RefreshColors"/>, and only while a biome view is shown.</summary>
        public BiomeSettings Biomes => biomes;

        /// <summary>Biomes of the current climate, or null if no biome view was shown yet.</summary>
        public BiomeResult CurrentBiomes { get; private set; }

        public BiomeCache BiomeCache => biomeCache;

        /// <summary>Raised when a new biome result becomes current.</summary>
        public event Action<BiomeResult> BiomesUpdated;

        /// <summary>Natural Planet settings (artistic). Changes recolor the planet on the next frame (Play
        /// mode) or the next <see cref="Render"/> / <see cref="RefreshColors"/>; no data is recomputed.</summary>
        public NaturalSurfaceSettings Surface => surface;

        /// <summary>Color sampler of the current Natural Planet, or null.</summary>
        public NaturalSurface CurrentNaturalSurface => naturalSurface;

        public TerrainSlopeCache SlopeCache => slopeCache;

        /// <summary>Number of Natural Planet sampler builds (data or artistic settings changed).</summary>
        public int NaturalSurfaceBuildCount { get; private set; }

        /// <summary>True when the default view shows the Natural Planet (not a debug view, natural surface on).</summary>
        public bool NaturalPlanetShown => debugView == PlanetDebugView.None && surface.enabled;

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
            UpdateOceanBathymetry();
            ApplyViewState();
            UpdateLakes();
            UpdateRivers();
            UpdateClimateArrows();
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
            UpdateClimateArrows();
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
                    || hydrology.resolution != renderedHydrologyResolution))
                || ((PlanetDebugViews.IsClimate(debugView) || PlanetDebugViews.IsBiome(debugView)) && !climateCache.IsCurrent(Planet, climate))
                || (PlanetDebugViews.IsBiome(debugView) && !biomeCache.IsCurrent(climateCache.Current, biomes))
                || (debugView == PlanetDebugView.None && NaturalSurfaceStale())
                || CloudDebugStale()
                || (debugView == PlanetDebugView.ClimateWinds && climateWindDirectionColors != renderedWindDirectionColors))
            {
                RefreshColors();
            }
            else
            {
                // Rendering-only settings: material properties (and the ocean depth data when needed).
                if (OceanAtmosphereStale())
                {
                    UpdateOceanBathymetry();
                    ApplyOceanAtmosphere();
                }
                AdvanceCloudTime();
                // Cheap when nothing changed: only cache keys are compared.
                UpdateLakes();
                UpdateRivers();
            }
        }

        void LateUpdate()
        {
            DrawAtmosphere();
            DrawClouds();
            DrawLakes();
            DrawWindArrows();
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
            riverProperties.SetColor("_MouthColor", MouthColor());
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

        /// <summary>
        /// Color river mouths blend towards: the lake's shallow water tinted a little towards the deep
        /// lake color (both from the lake material / settings), so rivers and lakes share one tint.
        /// </summary>
        Color MouthColor()
        {
            Material lake = ResolveLakeMaterial();
            Color shallow = lake.HasProperty("_ShallowColor") ? lake.GetColor("_ShallowColor") : lakes.color;
            return Color.Lerp(shallow, lakes.color, 0.35f);
        }

        ClimateResult EnsureClimate()
        {
            ClimateResult result = climateCache.Get(Planet, climate);
            CurrentClimate = result;
            if (result != notifiedClimate)
            {
                notifiedClimate = result;
                ClimateUpdated?.Invoke(result);
            }
            return result;
        }

        /// <summary>The Natural Planet colors no longer match their settings or data (cheap checks).</summary>
        bool NaturalSurfaceStale()
        {
            if (surface.enabled != renderedNatural)
            {
                return true;
            }
            if (!surface.enabled)
            {
                return false;
            }
            surface.Sanitize();
            return naturalSurface == null || !naturalSurface.Settings.AppearanceEquals(surface) || !climateCache.IsCurrent(Planet, climate)
                || !biomeCache.IsCurrent(climateCache.Current, biomes);
        }

        /// <summary>
        /// Natural Planet sampler: climate and biomes (cached), the terrain slope field (cached per terrain),
        /// rebuilt only when one of them or the artistic settings change.
        /// </summary>
        NaturalSurface EnsureNaturalSurface()
        {
            BiomeResult result = EnsureBiomes();
            TerrainSlopeField slope = slopeCache.Get(Planet);
            surface.Sanitize();
            if (naturalSurface == null || naturalSurface.BiomeData != result || naturalSurface.SlopeField != slope
                || !naturalSurface.Settings.AppearanceEquals(surface) || naturalSurface.Seed != Planet.Definition.seed)
            {
                naturalSurface = new NaturalSurface(result, slope, surface, Planet.Definition.seed);
                NaturalSurfaceBuildCount++;
            }
            return naturalSurface;
        }

        BiomeResult EnsureBiomes()
        {
            BiomeResult result = biomeCache.Get(EnsureClimate(), biomes);
            CurrentBiomes = result;
            if (result != notifiedBiomes)
            {
                notifiedBiomes = result;
                BiomesUpdated?.Invoke(result);
            }
            return result;
        }

        /// <summary>Wind arrows: rebuilt only for a new climate result or terrain mesh, in the winds view.</summary>
        void UpdateClimateArrows()
        {
            if (debugView != PlanetDebugView.ClimateWinds || Planet == null || faceMeshes[0] == null)
            {
                return;
            }
            ClimateResult result = EnsureClimate();
            if (result == builtArrowClimate && builtArrowTerrainVersion == terrainVersion)
            {
                return;
            }
            EnsureSurfaceSampler();
            windArrowMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
            windArrows.Build(windArrowMesh, result, surfaceSampler, Planet.Radius);
            builtArrowClimate = result;
            builtArrowTerrainVersion = terrainVersion;
        }

        void DrawWindArrows()
        {
            if (debugView != PlanetDebugView.ClimateWinds || windArrowMesh == null || windArrowMesh.vertexCount == 0 || Planet == null)
            {
                return;
            }
            var renderParams = new RenderParams(ResolveDebugMaterial())
            {
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (BoundingRadius * 2.4f * transform.lossyScale.x)),
            };
            Graphics.RenderMesh(renderParams, windArrowMesh, 0, transform.localToWorldMatrix);
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
            || debugView == PlanetDebugView.LakeConnectivity || debugView == PlanetDebugView.LakeShoreline;

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
                EnsureLakeGeometry(set);
                lakeMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
                lakeMeshBuilder.Upload(lakeMesh, Planet.Radius);
                builtLakeSet = set;
                builtLakeTerrainVersion = terrainVersion;
                changed = true;
            }
            if (PlanetDebugViews.IsLake(debugView) && set != renderedLakes && debugView != PlanetDebugView.LakeConnectivity
                && debugView != PlanetDebugView.LakeShoreline)
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

        /// <summary>
        /// Lake water geometry on the current terrain mesh: rebuilt only for a new lake selection or a
        /// new terrain mesh, shared by the water mesh and the lake-aware ribbons (rendered shoreline).
        /// </summary>
        void EnsureLakeGeometry(LakeSet set)
        {
            if (set == lakeGeometrySet && lakeGeometryTerrainVersion == terrainVersion)
            {
                return;
            }
            EnsureSurfaceSampler();
            lakeMeshBuilder.BuildGeometry(set, Planet, surfaceSampler);
            lakeGeometrySet = set;
            lakeGeometryTerrainVersion = terrainVersion;
            LakeMeshBuildCount++;
        }

        /// <summary>
        /// Shoreline view: recolors the terrain from the lake surface flags and the ribbon transition
        /// markers, only when the water geometry or the ribbons changed (never per frame).
        /// </summary>
        void RefreshShorelineView(LakeSet set)
        {
            EnsureLakeGeometry(set);
            RiverRibbonBuilder ribbons = RiversVisible && builtRibbon.Lakes != null ? ribbonBuilder : null;
            var key = (LakeMeshBuildCount, ribbons != null ? RiverRibbonBuildCount : -1, terrainVersion);
            if (key == shorelineViewKey && renderedLakes == set)
            {
                return;
            }
            shorelineViewKey = key;
            lakeMeshBuilder.MarkTransitions(ribbons);
            RecolorAll();
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
                || debugView == PlanetDebugView.LakeConnectivity || debugView == PlanetDebugView.LakeShoreline);
            // Lake-aware ribbons whenever lakes are shown (or inspected in the connectivity view).
            LakeRiverLayer layer = null;
            if (lakes.showLakes || debugView == PlanetDebugView.LakeConnectivity || debugView == PlanetDebugView.LakeShoreline)
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
                    if (layer != null)
                    {
                        EnsureLakeGeometry(layer.Lakes);
                    }
                    riverMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
                    ribbonBuilder.Build(riverMesh, network, surfaceSampler, Planet, rivers, layer, layer != null ? lakeMeshBuilder : null);
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
            if (debugView == PlanetDebugView.LakeShoreline && layer != null)
            {
                RefreshShorelineView(layer.Lakes);
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
            else if (PlanetDebugViews.IsClimate(debugView))
            {
                EnsureClimate();
            }
            else if (PlanetDebugViews.IsBiome(debugView))
            {
                EnsureBiomes();
            }
            else if (debugView == PlanetDebugView.None && surface.enabled)
            {
                EnsureNaturalSurface();
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
            PlanetMeshBuilder.ColorContext context = new PlanetMeshBuilder.ColorContext(debugView, CurrentHydrology,
                hydrology.riverNetworkThreshold, network, RiversVisible ? ribbonBuilder : null).WithLakes(set, layer);
            if (debugView == PlanetDebugView.LakeShoreline && set != null && set == lakeGeometrySet && lakeGeometryTerrainVersion == terrainVersion)
            {
                context.LakeWater = lakeMeshBuilder;
            }
            if (PlanetDebugViews.IsClimate(debugView))
            {
                context.Climate = CurrentClimate;
                context.WindDirectionColors = climateWindDirectionColors;
            }
            renderedWindDirectionColors = climateWindDirectionColors;
            if (PlanetDebugViews.IsBiome(debugView))
            {
                context.Biomes = CurrentBiomes;
            }
            if (debugView == PlanetDebugView.None && surface.enabled)
            {
                context.Natural = naturalSurface;
            }
            if (PlanetDebugViews.IsCloud(debugView))
            {
                context.Clouds = CloudDebug();
            }
            renderedNatural = surface.enabled;
            return context;
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
            ApplyOceanAtmosphere();
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
                PlanetMeshBuilder.BuildUnitSphere(oceanMesh, OceanMeshResolution);
                builtOceanLayer = null;
                oceanTransform = CreateChild("Ocean", oceanMesh, ResolveOceanMaterial()).transform;
            }
            EnsureAtmosphereResources();
            EnsureCloudResources();
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
            DestroyOceanAtmosphere();
            DestroySafe(ownedTerrainMaterial);
            DestroySafe(ownedOceanMaterial);
            DestroySafe(ownedDebugMaterial);
            DestroySafe(ownedRiverMaterial);
            DestroySafe(riverMesh);
            riverMesh = null;
            DestroySafe(ownedLakeMaterial);
            DestroySafe(lakeMesh);
            lakeMesh = null;
            DestroySafe(windArrowMesh);
            windArrowMesh = null;
            builtArrowClimate = null;
            climateCache.Clear();
            CurrentClimate = null;
            notifiedClimate = null;
            biomeCache.Clear();
            CurrentBiomes = null;
            notifiedBiomes = null;
            slopeCache.Clear();
            naturalSurface = null;
            hydrologyCache.Clear();
            riverCache.Clear();
            lakeCache.Clear();
            CurrentHydrology = null;
            CurrentRivers = null;
            CurrentLakes = null;
            CurrentLakeLayer = null;
            builtLakeSet = null;
            lakeGeometrySet = null;
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
