using System.Collections.Generic;
using Planetwalker.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Oceans and atmosphere presentation (V0.1.2B): bathymetry data on the ocean sphere, ocean / terrain
    /// lighting properties and the atmosphere shell. Rendering only: nothing here changes or recomputes
    /// terrain, hydrology, climate, biomes or the Natural Planet data.
    /// </summary>
    public sealed partial class PlanetVisualController
    {
        static readonly int OceanShadingId = Shader.PropertyToID("_OceanShading");
        static readonly int DepthContrastId = Shader.PropertyToID("_DepthContrast");
        static readonly int ShelfVisibilityId = Shader.PropertyToID("_ShelfVisibility");
        static readonly int RoughnessId = Shader.PropertyToID("_Roughness");
        static readonly int SpecularIntensityId = Shader.PropertyToID("_SpecularIntensity");
        static readonly int LightWrapId = Shader.PropertyToID("_LightWrap");
        static readonly int ShoreParamsId = Shader.PropertyToID("_ShoreParams");

        /// <summary>Shallow water at the shore (V0.2.0F, presentation only: never archived). Applied on the next ApplyOceanAtmosphere.</summary>
        public bool ShallowWater { get; set; } = true;
        static readonly int StrengthId = Shader.PropertyToID("_Strength");
        static readonly int PlanetRatioId = Shader.PropertyToID("_PlanetRatio");
        static readonly int AtmoShellId = Shader.PropertyToID("_AtmoShell");
        static readonly int AtmoParamsId = Shader.PropertyToID("_AtmoParams");
        static readonly int AtmoParams2Id = Shader.PropertyToID("_AtmoParams2");
        static readonly int AtmoColorId = Shader.PropertyToID("_AtmoColor");
        static readonly int AtmoWarmColorId = Shader.PropertyToID("_AtmoWarmColor");
        static readonly int DensityId = Shader.PropertyToID("_Density");
        static readonly int FalloffId = Shader.PropertyToID("_Falloff");
        static readonly int NightLevelId = Shader.PropertyToID("_NightLevel");
        static readonly int WarmStrengthId = Shader.PropertyToID("_WarmStrength");
        static readonly int SurfaceCapId = Shader.PropertyToID("_SurfaceOpticalDepth");
        static readonly int AtmosphereColorId = Shader.PropertyToID("_AtmosphereColor");
        static readonly int WarmColorId = Shader.PropertyToID("_WarmColor");

        const int AtmosphereMeshResolution = 24;

        readonly OceanBathymetryCache bathymetryCache = new OceanBathymetryCache();
        readonly List<Vector2> oceanDepthData = new List<Vector2>();
        OceanDepthLayer builtOceanLayer;
        MaterialPropertyBlock oceanProperties;
        MaterialPropertyBlock terrainProperties;
        MaterialPropertyBlock atmosphereProperties;
        Mesh atmosphereMesh;
        Material ownedAtmosphereMaterial;
        bool atmosphereShaderMissing;
        OceanAtmosphereSettings appliedOceanAtmosphere;
        PlanetDebugView appliedOceanAtmosphereView = (PlanetDebugView)(-1);
        Vector4 appliedHazeFrame;
        CloudSettings appliedClouds;
        SolarSettings appliedSolarShading;

        /// <summary>Oceans and atmosphere settings (rendering only). Changes apply on the next frame (Play
        /// mode) or on <see cref="ApplyOceanAtmosphere"/> / <see cref="RefreshColors"/> / <see cref="Render"/>.</summary>
        public OceanAtmosphereSettings OceanAtmosphere => oceanAtmosphere;

        public OceanBathymetryCache BathymetryCache => bathymetryCache;

        /// <summary>Ocean depths written to the ocean sphere, or null (ocean shading not used yet).</summary>
        public OceanDepthLayer CurrentOceanDepth => builtOceanLayer;

        /// <summary>Ocean sphere subdivisions: at least the bathymetry grid resolution.</summary>
        public int OceanMeshResolution => Mathf.Max(oceanResolution, BathymetryField.Resolution);

        public Mesh OceanMesh => oceanMesh;

        /// <summary>Number of ocean depth uploads to the ocean mesh.</summary>
        public int OceanDepthUploadCount { get; private set; }

        /// <summary>Number of material property applications (cheap, rendering only).</summary>
        public int OceanAtmosphereApplyCount { get; private set; }

        /// <summary>True when the ocean uses the bathymetry shading (default view, setting on, shader supports it).</summary>
        public bool OceanShadingActive { get; private set; }

        /// <summary>True when the atmosphere shell is drawn (default view, setting on, shader available).</summary>
        public bool AtmosphereVisible { get; private set; }

        /// <summary>Atmosphere shell radius in local units (ocean radius x (1 + thickness)).</summary>
        public float AtmosphereRadius => Planet == null ? 0f : Planet.OceanRadius * (1f + oceanAtmosphere.atmosphereThickness);

        /// <summary>Atmosphere material in use (null when its shader is unavailable).</summary>
        public Material AtmosphereMaterialInUse => atmosphereShaderMissing ? null : ownedAtmosphereMaterial != null ? ownedAtmosphereMaterial : atmosphereMaterial;

        public Mesh AtmosphereMesh => atmosphereMesh;

        public MeshRenderer OceanRenderer => oceanTransform != null ? oceanTransform.GetComponent<MeshRenderer>() : null;

        /// <summary>Assigns the atmosphere material asset (editor setup utility).</summary>
        public void SetAtmosphereMaterial(Material material)
        {
            atmosphereMaterial = material;
            atmosphereShaderMissing = false;
        }

        /// <summary>The applied ocean / atmosphere properties no longer match the settings or the view.</summary>
        bool OceanAtmosphereStale()
        {
            oceanAtmosphere.Sanitize();
            clouds.Sanitize();
            return appliedOceanAtmosphere == null || !appliedOceanAtmosphere.AppearanceEquals(oceanAtmosphere)
                || appliedOceanAtmosphereView != debugView || appliedHazeFrame != HazeFrame()
                || appliedClouds == null || !appliedClouds.AppearanceEquals(clouds)
                || appliedSolarShading == null || !appliedSolarShading.ShadingEquals(solar);
        }

        /// <summary>
        /// Ocean depth data for the current planet: cached bathymetry (per elevation field) and depth layer
        /// (per sea level), uploaded to the ocean mesh only when the layer changes. Only while the ocean
        /// shading is used (default view, setting on).
        /// </summary>
        void UpdateOceanBathymetry()
        {
            if (Planet == null || oceanMesh == null || debugView != PlanetDebugView.None || !oceanAtmosphere.oceanShading
                || !OceanShaderSupportsShading())
            {
                return;
            }
            OceanDepthLayer layer = bathymetryCache.Get(Planet);
            if (layer == builtOceanLayer)
            {
                return;
            }
            layer.WriteVertexData(oceanMesh.vertices, oceanDepthData);
            oceanMesh.SetUVs(1, oceanDepthData);
            builtOceanLayer = layer;
            OceanDepthUploadCount++;
        }

        bool OceanShaderSupportsShading()
        {
            Material ocean = ResolveOceanMaterial();
            return ocean != null && ocean.HasProperty(OceanShadingId);
        }

        /// <summary>
        /// Applies the oceans and atmosphere settings as material properties (ocean, terrain light wrap,
        /// atmosphere). Cheap: no mesh, texture or material is created; no data is recomputed.
        /// In development views the ocean and atmosphere are hidden and the terrain uses the unlit
        /// material, so the scientific colors are untouched.
        /// </summary>
        public void ApplyOceanAtmosphere()
        {
            if (oceanTransform == null)
            {
                return;
            }
            oceanAtmosphere.Sanitize();
            bool defaultView = debugView == PlanetDebugView.None;
            UpdateOceanBathymetry();

            AtmosphereVisible = defaultView && oceanAtmosphere.atmosphere && Planet != null && ResolveAtmosphereMaterial() != null;
            OceanShadingActive = defaultView && oceanAtmosphere.oceanShading && OceanShaderSupportsShading() && builtOceanLayer != null;
            ApplyClouds(defaultView);
            oceanProperties ??= new MaterialPropertyBlock();
            oceanProperties.SetFloat(OceanShadingId, OceanShadingActive ? 1f : 0f);
            oceanProperties.SetFloat(DepthContrastId, oceanAtmosphere.depthContrast);
            oceanProperties.SetFloat(ShelfVisibilityId, oceanAtmosphere.shelfVisibility);
            oceanProperties.SetFloat(RoughnessId, oceanAtmosphere.oceanRoughness);
            oceanProperties.SetFloat(SpecularIntensityId, oceanAtmosphere.specularIntensity);
            oceanProperties.SetFloat(LightWrapId, oceanAtmosphere.LightWrap);
            SetHaze(oceanProperties);
            WriteCloudUniforms(oceanProperties, Planet != null ? Planet.OceanRadius : 1f, cloudThreshold);
            WriteSolarShading(oceanProperties);
            // Shallow water at the shore (V0.2.0F): meters per world unit (planet scale and the root's scale), clarity depth.
            float worldScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 1e-9f);
            oceanProperties.SetVector(ShoreParamsId, new Vector4(Planet != null ? (float)(Planet.Scale.MetersPerUnit / worldScale) : 1f, 3f,
                defaultView && ShallowWater ? 1f : 0f, 0f));
            OceanRenderer.SetPropertyBlock(oceanProperties);

            // Terminator softness: the terrain's existing main-light wrap (no second lighting system).
            terrainProperties ??= new MaterialPropertyBlock();
            terrainProperties.SetFloat(LightWrapId, oceanAtmosphere.LightWrap);
            SetHaze(terrainProperties);
            WriteCloudUniforms(terrainProperties, 1f, cloudThreshold);
            WriteSolarShading(terrainProperties);
            foreach (MeshRenderer faceRenderer in faceRenderers)
            {
                if (faceRenderer == null)
                {
                    continue;
                }
                if (defaultView)
                {
                    faceRenderer.SetPropertyBlock(terrainProperties);
                }
                else
                {
                    faceRenderer.SetPropertyBlock(null);
                }
            }

            if (AtmosphereVisible)
            {
                atmosphereProperties ??= new MaterialPropertyBlock();
                atmosphereProperties.SetFloat(StrengthId, oceanAtmosphere.atmosphereStrength);
                atmosphereProperties.SetFloat(PlanetRatioId, 1f / (1f + oceanAtmosphere.atmosphereThickness));
                atmosphereProperties.SetFloat(WarmStrengthId2, TwilightWarmStrength(AtmosphereMaterialInUse));
                EnsureAtmosphereResources();
            }
            appliedOceanAtmosphere = oceanAtmosphere.Clone();
            appliedOceanAtmosphereView = debugView;
            appliedHazeFrame = HazeFrame();
            appliedClouds = clouds.Clone();
            appliedSolarShading = solar.Clone();
            OceanAtmosphereApplyCount++;
        }

        static readonly int PlanetFromWorldId = Shader.PropertyToID("_PlanetFromWorld");
        static readonly int AdaptivePatchId = Shader.PropertyToID("_AdaptivePatch");

        /// <summary>
        /// Writes the default view's terrain properties (terminator wrap, haze, clouds and their current time, night
        /// and twilight) to the adaptive terrain's property block (V0.2.0B): the same writers as for the six face
        /// renderers, plus the planet frame its patch-relative vertices need. Called each frame before drawing.
        /// </summary>
        internal void WriteAdaptiveTerrainProperties(MaterialPropertyBlock block)
        {
            block.SetFloat(LightWrapId, oceanAtmosphere.LightWrap);
            SetHaze(block);
            WriteCloudUniforms(block, 1f, cloudThreshold);
            WriteSolarShading(block);
            if (terrainProperties != null)
            {
                block.SetVector(CloudParams3Id, terrainProperties.GetVector(CloudParams3Id)); // cloud time
            }
            block.SetMatrix(PlanetFromWorldId, transform.worldToLocalMatrix);
            block.SetFloat(AdaptivePatchId, 1f);
        }

        /// <summary>
        /// Haze between the camera and the terrain / ocean (same model and parameters as the shell, read
        /// from the atmosphere material): world-space sphere center and radii. Strength 0 when the
        /// atmosphere is not shown (debug views, toggle off, shader missing): no haze.
        /// </summary>
        void SetHaze(MaterialPropertyBlock block)
        {
            Material shell = AtmosphereMaterialInUse;
            float strength = AtmosphereVisible && shell != null ? oceanAtmosphere.atmosphereStrength : 0f;
            Vector4 frame = HazeFrame();
            block.SetVector(AtmoShellId, new Vector4(frame.x, frame.y, frame.z, frame.w * (1f + oceanAtmosphere.atmosphereThickness)));
            if (shell == null)
            {
                block.SetVector(AtmoParamsId, new Vector4(frame.w, 0f, 0f, 0f));
                return;
            }
            block.SetVector(AtmoParamsId, new Vector4(frame.w, shell.GetFloat(DensityId), shell.GetFloat(FalloffId), strength));
            block.SetVector(AtmoParams2Id, new Vector4(shell.GetFloat(NightLevelId), TwilightWarmStrength(shell), shell.GetFloat(SurfaceCapId), 0f));
            block.SetColor(AtmoColorId, shell.GetColor(AtmosphereColorId));
            block.SetColor(AtmoWarmColorId, shell.GetColor(WarmColorId));
        }

        /// <summary>Planet center (xyz) and ocean radius (w) in world space.</summary>
        Vector4 HazeFrame()
        {
            Vector3 center = transform.position;
            float ocean = Planet == null ? 0f : Planet.OceanRadius * transform.lossyScale.x;
            return new Vector4(center.x, center.y, center.z, ocean);
        }

        /// <summary>Atmosphere mesh and material: created once with the planet objects, then reused.</summary>
        void EnsureAtmosphereResources()
        {
            if (atmosphereMesh == null)
            {
                atmosphereMesh = new Mesh { hideFlags = HideFlags.DontSave };
                PlanetMeshBuilder.BuildUnitSphere(atmosphereMesh, AtmosphereMeshResolution);
                atmosphereMesh.name = "Planet Atmosphere";
            }
            ResolveAtmosphereMaterial();
        }

        void DrawAtmosphere(Camera target = null)
        {
            if (!AtmosphereVisible || atmosphereMesh == null || Planet == null)
            {
                return;
            }
            // Drawn without a GameObject (like rivers and lakes): the planet keeps its 6 faces + ocean structure.
            float radius = AtmosphereRadius;
            var renderParams = new RenderParams(AtmosphereMaterialInUse)
            {
                camera = target,
                matProps = atmosphereProperties,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (radius * 2.2f * transform.lossyScale.x)),
            };
            Graphics.RenderMesh(renderParams, atmosphereMesh, 0, transform.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * radius));
        }

        /// <summary>
        /// Atmosphere material: the assigned asset if it uses a compatible shader, else a runtime material
        /// of the atmosphere shader. Null (atmosphere disabled, warning once) when the shader is missing:
        /// never a generic opaque fallback that would hide the planet.
        /// </summary>
        Material ResolveAtmosphereMaterial()
        {
            if (atmosphereShaderMissing)
            {
                return null;
            }
            if (atmosphereMaterial != null)
            {
                if (atmosphereMaterial.HasProperty(PlanetRatioId))
                {
                    return atmosphereMaterial;
                }
                Debug.LogWarning($"Planetwalker: atmosphere material '{atmosphereMaterial.name}' does not use {AtmosphereShaderName}; atmosphere disabled.");
                atmosphereShaderMissing = true;
                return null;
            }
            if (ownedAtmosphereMaterial == null)
            {
                Shader shader = Shader.Find(AtmosphereShaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"Planetwalker: shader '{AtmosphereShaderName}' not found; atmosphere disabled.");
                    atmosphereShaderMissing = true;
                    return null;
                }
                ownedAtmosphereMaterial = new Material(shader) { name = AtmosphereShaderName + " (Runtime)", hideFlags = HideFlags.DontSave };
            }
            return ownedAtmosphereMaterial;
        }

        void DestroyOceanAtmosphere()
        {
            DestroySafe(atmosphereMesh);
            atmosphereMesh = null;
            DestroySafe(ownedAtmosphereMaterial);
            ownedAtmosphereMaterial = null;
            bathymetryCache.Clear();
            builtOceanLayer = null;
            oceanDepthData.Clear();
            appliedOceanAtmosphere = null;
            AtmosphereVisible = false;
            DestroyClouds();
            OceanShadingActive = false;
        }
    }
}
