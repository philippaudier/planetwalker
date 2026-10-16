using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Cloud presentation (V0.1.2C): cloud field cache and cube map, cloud shell, cloud shadows (uniforms
    /// on the terrain and ocean property blocks), simulation time, debug data, resource lifetime.
    /// Rendering only: never recomputes terrain, hydrology, climate, biomes or the slope field. The cloud
    /// field reads the climate only when it is already computed (never forces it).
    /// </summary>
    public sealed partial class PlanetVisualController
    {
        public const string CloudShaderName = "Planetwalker/PlanetClouds";
        /// <summary>Simulation time units per second of Play mode when Cloud Motion is on.</summary>
        public const float CloudMotionSpeed = 0.006f;
        const int CloudMeshResolution = 64;

        static readonly int CloudCubeId = Shader.PropertyToID("_CloudCube");
        static readonly int CloudParamsId = Shader.PropertyToID("_CloudParams");
        static readonly int CloudParams2Id = Shader.PropertyToID("_CloudParams2");
        static readonly int CloudParams3Id = Shader.PropertyToID("_CloudParams3");
        static readonly int CloudSeedId = Shader.PropertyToID("_CloudSeed");
        static readonly int CloudLocalScaleId = Shader.PropertyToID("_CloudLocalScale");

        [Tooltip("Procedural cloud systems (V0.1.2C). Rendering only.")]
        [SerializeField] CloudSettings clouds = new CloudSettings();

        [Tooltip("Cloud shell material (created at runtime if empty).")]
        [SerializeField] Material cloudMaterial;

        readonly CloudFieldCache cloudFieldCache = new CloudFieldCache();
        readonly Color[] cloudFacePixels = new Color[CloudField.Resolution * CloudField.Resolution];
        Cubemap cloudCube;
        CloudField builtCloudField;
        Mesh cloudMesh;
        Material ownedCloudMaterial;
        bool cloudShaderMissing;
        MaterialPropertyBlock cloudProperties;
        CloudField thresholdField;
        float thresholdCoverage = -1f, thresholdWeather = -1f, thresholdConvection = -1f, cloudThreshold;
        CloudSettings renderedCloudDebug;

        public CloudSettings Clouds => clouds;
        public CloudFieldCache CloudFieldCache => cloudFieldCache;
        /// <summary>Cloud field written to the cloud cube map, or null.</summary>
        public CloudField CurrentCloudField => builtCloudField;
        public Cubemap CloudCube => cloudCube;
        public Mesh CloudMesh => cloudMesh;
        /// <summary>Number of cloud cube map uploads.</summary>
        public int CloudUploadCount { get; private set; }
        /// <summary>True when the cloud shell (and its shadows) are drawn.</summary>
        public bool CloudsVisible { get; private set; }
        /// <summary>Cloud shell radius in local units.</summary>
        public float CloudRadius => Planet == null ? 0f : Planet.OceanRadius * (1f + clouds.altitude);
        /// <summary>Current coverage threshold of the cloud potential.</summary>
        public float CloudThreshold => cloudThreshold;
        public Material CloudMaterialInUse => cloudShaderMissing ? null : ownedCloudMaterial != null ? ownedCloudMaterial : cloudMaterial;

        /// <summary>
        /// Cloud simulation time (deterministic: the clouds are a pure function of it). Advanced in Play
        /// mode only while Cloud Motion is on; can be set explicitly (reproducible frames).
        /// </summary>
        public double CloudSimulationTime { get; set; }

        /// <summary>Assigns the cloud material asset (editor setup utility).</summary>
        public void SetCloudMaterial(Material material)
        {
            cloudMaterial = material;
            cloudShaderMissing = false;
        }

        /// <summary>
        /// Cloud field for the current planet: weather seed, climate (only if already computed for this
        /// planet: never forced), rotation axis. Uploaded to the cube map only when it changes.
        /// </summary>
        CloudField EnsureCloudField()
        {
            if (Planet == null)
            {
                return null;
            }
            ClimateResult climateResult = climateCache.IsCurrent(Planet, climate) ? climateCache.Current : null;
            CloudField field = cloudFieldCache.Get(clouds.EffectiveWeatherSeed(Planet.Definition.seed), climateResult, climate.rotationAxis);
            if (field != builtCloudField && cloudCube != null)
            {
                for (int face = 0; face < 6; face++)
                {
                    field.FillFace(face, cloudFacePixels);
                    cloudCube.SetPixels(cloudFacePixels, (CubemapFace)face);
                }
                cloudCube.Apply(false);
                builtCloudField = field;
                CloudUploadCount++;
            }
            return field;
        }

        float EnsureCloudThreshold(CloudField field)
        {
            if (field != thresholdField || clouds.coverage != thresholdCoverage || clouds.weatherStrength != thresholdWeather
                || clouds.convection != thresholdConvection)
            {
                cloudThreshold = field.Threshold(clouds);
                thresholdField = field;
                thresholdCoverage = clouds.coverage;
                thresholdWeather = clouds.weatherStrength;
                thresholdConvection = clouds.convection;
            }
            return cloudThreshold;
        }

        /// <summary>Cloud data for the cloud debug views (built on demand, never needs the climate).</summary>
        CloudDebugData CloudDebug()
        {
            clouds.Sanitize();
            EnsureCloudResources();
            CloudField field = EnsureCloudField();
            renderedCloudDebug = clouds.Clone();
            return field == null ? null : new CloudDebugData(field, clouds, EnsureCloudThreshold(field));
        }

        /// <summary>The cloud debug view no longer matches the settings that change its colors.</summary>
        bool CloudDebugStale()
        {
            return PlanetDebugViews.IsCloud(debugView) && (renderedCloudDebug == null || renderedCloudDebug.coverage != clouds.coverage
                || renderedCloudDebug.weatherStrength != clouds.weatherStrength || renderedCloudDebug.convection != clouds.convection
                || renderedCloudDebug.weatherSeed != clouds.weatherSeed);
        }

        /// <summary>
        /// Cloud uniforms (shell and shadows). Called from <see cref="ApplyOceanAtmosphere"/>. Cheap except when
        /// the cloud field itself changes (new planet / climate / weather seed).
        /// </summary>
        void ApplyClouds(bool defaultView)
        {
            clouds.Sanitize();
            CloudField field = defaultView && clouds.showClouds && clouds.coverage > 0f && Planet != null && ResolveCloudMaterial() != null
                ? EnsureCloudField() : null;
            CloudsVisible = field != null && builtCloudField == field;
            float threshold = CloudsVisible ? EnsureCloudThreshold(field) : 1e4f;
            cloudProperties ??= new MaterialPropertyBlock();
            WriteCloudUniforms(cloudProperties, 1f, threshold);
            if (CloudsVisible)
            {
                SetHaze(cloudProperties);
                cloudProperties.SetFloat(LightWrapId, oceanAtmosphere.LightWrap);
                WriteSolarShading(cloudProperties);
            }
        }

        /// <summary>Cloud uniforms on a property block; localScale = planet units per object unit of the renderer.</summary>
        void WriteCloudUniforms(MaterialPropertyBlock block, float localScale, float threshold)
        {
            float enabled = CloudsVisible ? 1f : 0f;
            int seed = clouds.EffectiveWeatherSeed(Planet != null ? Planet.Definition.seed : 0);
            if (cloudCube != null)
            {
                block.SetTexture(CloudCubeId, cloudCube);
            }
            block.SetVector(CloudParamsId, new Vector4(threshold, Mathf.Lerp(0.35f, 1f, clouds.density), Mathf.Lerp(0.04f, 0.3f, clouds.detail), enabled));
            block.SetVector(CloudParams2Id, new Vector4(clouds.weatherStrength, clouds.convection, CloudsVisible ? clouds.shadowStrength : 0f,
                Planet != null ? CloudRadius : 1f));
            Vector3 axis = climate.rotationAxis.sqrMagnitude > 0f ? climate.rotationAxis.normalized : Vector3.up;
            block.SetVector(CloudParams3Id, new Vector4(axis.x, axis.y, axis.z, (float)CloudSimulationTime));
            block.SetVector(CloudSeedId, new Vector4(
                (PlanetNoise.Hash01(seed, 9701) - 0.5f) * 100f, (PlanetNoise.Hash01(seed, 9702) - 0.5f) * 100f,
                (PlanetNoise.Hash01(seed, 9703) - 0.5f) * 100f, CloudField.Softness));
            block.SetFloat(CloudLocalScaleId, localScale);
        }

        /// <summary>Play mode, Cloud Motion on: advance the simulation time (time uniform only, no allocation).</summary>
        void AdvanceCloudTime()
        {
            if (!clouds.motion || !CloudsVisible)
            {
                return;
            }
            CloudSimulationTime += Time.deltaTime * CloudMotionSpeed;
            ApplyCloudTime();
        }

        /// <summary>Writes the simulation time to every cloud-dependent property block.</summary>
        public void ApplyCloudTime()
        {
            if (cloudProperties == null || terrainProperties == null || oceanProperties == null)
            {
                return;
            }
            Vector4 p = cloudProperties.GetVector(CloudParams3Id);
            p.w = (float)CloudSimulationTime;
            cloudProperties.SetVector(CloudParams3Id, p);
            terrainProperties.SetVector(CloudParams3Id, p);
            oceanProperties.SetVector(CloudParams3Id, p);
            if (debugView == PlanetDebugView.None)
            {
                foreach (MeshRenderer faceRenderer in faceRenderers)
                {
                    if (faceRenderer != null)
                    {
                        faceRenderer.SetPropertyBlock(terrainProperties);
                    }
                }
            }
            OceanRenderer?.SetPropertyBlock(oceanProperties);
        }

        void DrawClouds()
        {
            if (!CloudsVisible || cloudMesh == null || Planet == null)
            {
                return;
            }
            float radius = CloudRadius;
            var renderParams = new RenderParams(CloudMaterialInUse)
            {
                matProps = cloudProperties,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(transform.position, Vector3.one * (radius * 2.2f * transform.lossyScale.x)),
            };
            Graphics.RenderMesh(renderParams, cloudMesh, 0, transform.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * radius));
        }

        /// <summary>Cloud mesh, material and cube map: created once with the planet objects, then reused.</summary>
        void EnsureCloudResources()
        {
            if (cloudMesh == null)
            {
                cloudMesh = new Mesh { hideFlags = HideFlags.DontSave };
                PlanetMeshBuilder.BuildUnitSphere(cloudMesh, CloudMeshResolution);
                cloudMesh.name = "Planet Clouds";
            }
            if (cloudCube == null)
            {
                cloudCube = new Cubemap(CloudField.Resolution, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None)
                {
                    name = "Planet Cloud Field",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                builtCloudField = null;
            }
            ResolveCloudMaterial();
        }

        /// <summary>
        /// Cloud material: the assigned asset if compatible, else a runtime material of the cloud shader.
        /// Null (clouds disabled, warning once) when the shader is missing: never an opaque fallback.
        /// </summary>
        Material ResolveCloudMaterial()
        {
            if (cloudShaderMissing)
            {
                return null;
            }
            if (cloudMaterial != null)
            {
                if (cloudMaterial.HasProperty(CloudParamsId) && cloudMaterial.shader.name == CloudShaderName)
                {
                    return cloudMaterial;
                }
                Debug.LogWarning($"Planetwalker: cloud material '{cloudMaterial.name}' does not use {CloudShaderName}; clouds disabled.");
                cloudShaderMissing = true;
                return null;
            }
            if (ownedCloudMaterial == null)
            {
                Shader shader = Shader.Find(CloudShaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"Planetwalker: shader '{CloudShaderName}' not found; clouds disabled.");
                    cloudShaderMissing = true;
                    return null;
                }
                ownedCloudMaterial = new Material(shader) { name = CloudShaderName + " (Runtime)", hideFlags = HideFlags.DontSave };
            }
            return ownedCloudMaterial;
        }

        void DestroyClouds()
        {
            DestroySafe(cloudMesh);
            cloudMesh = null;
            DestroySafe(ownedCloudMaterial);
            ownedCloudMaterial = null;
            DestroySafe(cloudCube);
            cloudCube = null;
            builtCloudField = null;
            thresholdField = null;
            cloudFieldCache.Clear();
            CloudsVisible = false;
        }
    }
}
