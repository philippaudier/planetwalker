using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// The physically based surface materials of the planets (V0.2.0F): references to the imported CC0 scans
    /// (Materials/PlanetSurface; editor: PlanetSurfaceTextureImporter, PlanetSurfaceLibraryBuilder), one per
    /// <see cref="SurfaceMaterial"/>, assembled on the GPU into three texture arrays on first use (Graphics.CopyTexture,
    /// every mip, no CPU copy, no stored duplicate). The arrays go on the adaptive terrain's single property block: no
    /// material instance per patch or per material.
    /// </summary>
    /// <remarks>
    /// Albedo: BC1 sRGB. Normal: tangent-space X and Y (BC5; OpenGL convention, +Y up: Unity's; Z reconstructed).
    /// Mask (the roughness scan's import): R = smoothness (1 - roughness: Unity's convention), G = ambient occlusion,
    /// B = height (blends materials; never displaces geometry). 2048 x 2048, full mip chains. When the arrays cannot be
    /// assembled (formats or sizes differ, copy unsupported) the terrain stays procedural and <see cref="Status"/> says why.
    /// </remarks>
    [CreateAssetMenu(menuName = "Planetwalker/Planet Surface Library", fileName = "PlanetSurfaceLibrary")]
    public sealed class PlanetSurfaceLibrary : ScriptableObject
    {
        /// <summary>Resources path of the library asset.</summary>
        public const string ResourcePath = "PlanetSurface/PlanetSurfaceLibrary";

        public Texture2D[] albedoSources = Array.Empty<Texture2D>();
        public Texture2D[] normalSources = Array.Empty<Texture2D>();
        public Texture2D[] maskSources = Array.Empty<Texture2D>();
        /// <summary>Per layer: display name, tile size (meters; divides the 1024 m anchoring period), mean albedo (linear).</summary>
        public string[] names = Array.Empty<string>();
        public float[] tileMeters = Array.Empty<float>();
        public Color[] meanAlbedo = Array.Empty<Color>();

        [NonSerialized] Texture2DArray albedo, normal, mask;
        [NonSerialized] bool attempted;

        public Texture2DArray Albedo => albedo;
        public Texture2DArray Normal => normal;
        public Texture2DArray Mask => mask;
        public string Status { get; private set; } = "not assembled";

        /// <summary>The references are complete (one of each map per material).</summary>
        public bool HasSources => albedoSources.Length == SurfaceMaterials.Count && normalSources.Length == SurfaceMaterials.Count
            && maskSources.Length == SurfaceMaterials.Count && tileMeters.Length == SurfaceMaterials.Count && meanAlbedo.Length == SurfaceMaterials.Count
            && Array.TrueForAll(albedoSources, t => t != null) && Array.TrueForAll(normalSources, t => t != null) && Array.TrueForAll(maskSources, t => t != null);

        /// <summary>GPU memory of the assembled arrays (bytes; 0 when not assembled).</summary>
        public long ArrayBytes => Bytes(albedo) + Bytes(normal) + Bytes(mask);

        static long Bytes(Texture texture) => texture != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture) : 0;

        /// <summary>Assembles the arrays once (main thread). True when they are ready.</summary>
        public bool EnsureArrays()
        {
            if (albedo != null && normal != null && mask != null)
            {
                return true;
            }
            if (attempted)
            {
                return false;
            }
            attempted = true;
            if (!HasSources)
            {
                Status = "library incomplete (rebuild: Planetwalker > Build Surface Material Library)";
                return false;
            }
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0)
            {
                Status = "GPU texture copy into arrays unsupported";
                return false;
            }
            albedo = Assemble(albedoSources, "PlanetSurfaceAlbedo", out string a);
            normal = Assemble(normalSources, "PlanetSurfaceNormal", out string n);
            mask = Assemble(maskSources, "PlanetSurfaceMask", out string m);
            if (albedo == null || normal == null || mask == null)
            {
                Status = a ?? n ?? m;
                Release();
                return false;
            }
            Status = $"{albedo.depth} materials, {albedo.width}x{albedo.height}, {albedo.graphicsFormat} / {normal.graphicsFormat} / {mask.graphicsFormat}";
            if (Application.isPlaying)
            {
                // The arrays hold everything now: free the sources' GPU copies (reloaded on demand if ever needed).
                foreach (Texture2D[] set in new[] { albedoSources, normalSources, maskSources })
                {
                    foreach (Texture2D t in set)
                    {
                        Resources.UnloadAsset(t);
                    }
                }
            }
            return true;
        }

        static Texture2DArray Assemble(Texture2D[] sources, string name, out string error)
        {
            error = null;
            Texture2D first = sources[0];
            for (int k = 1; k < sources.Length; k++)
            {
                Texture2D s = sources[k];
                if (s.width != first.width || s.height != first.height || s.graphicsFormat != first.graphicsFormat || s.mipmapCount != first.mipmapCount)
                {
                    error = $"{name}: {s.name} is {s.width}x{s.height} {s.graphicsFormat} ({s.mipmapCount} mips), {first.name} is {first.width}x{first.height} {first.graphicsFormat} ({first.mipmapCount} mips)";
                    return null;
                }
            }
            var array = new Texture2DArray(first.width, first.height, sources.Length, first.graphicsFormat, TextureCreationFlags.MipChain, first.mipmapCount)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                hideFlags = HideFlags.DontSave,
            };
            for (int k = 0; k < sources.Length; k++)
            {
                for (int mip = 0; mip < first.mipmapCount; mip++)
                {
                    Graphics.CopyTexture(sources[k], 0, mip, array, k, mip);
                }
            }
            return array;
        }

        void Release()
        {
            DestroySafe(albedo);
            DestroySafe(normal);
            DestroySafe(mask);
            albedo = normal = mask = null;
        }

        void OnDisable()
        {
            Release();
            attempted = false;
        }

        static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        static PlanetSurfaceLibrary cached;

        /// <summary>The project's library (null when it has not been built: the terrain then stays procedural).</summary>
        public static PlanetSurfaceLibrary Load()
        {
            if (cached == null)
            {
                cached = Resources.Load<PlanetSurfaceLibrary>(ResourcePath);
            }
            return cached;
        }
    }

    /// <summary>Layer index of each surface material in the library's arrays.</summary>
    public enum SurfaceMaterial
    {
        Granite = 0,
        Sandstone = 1,
        Limestone = 2,
        Basalt = 3,
        DrySand = 4,
        Gravel = 5,
        // Ground cover (V0.2.0F.2): the biomes' own ground.
        Grass = 6,
        DryGrass = 7,
        ForestFloor = 8,
        Soil = 9,
        Snow = 10,
    }

    /// <summary>Material rendering mode of the terrain (V0.2.0F).</summary>
    public enum SurfaceMaterialMode
    {
        /// <summary>The V0.2.0E procedural geology appearance.</summary>
        Procedural = 0,
        /// <summary>Physically based scanned materials distributed by the geology.</summary>
        Pbr = 1,
    }

    /// <summary>Material debug views (V0.2.0F; the shader's _SurfParams.z).</summary>
    public enum SurfaceMaterialDebugView
    {
        FinalLit = 0,
        MaterialId = 1,
        Albedo = 2,
        Normal = 3,
        Roughness = 4,
        Triplanar = 5,
        /// <summary>Projection scale (V0.2.0F.1): 2 m red, 16 m green, 128 m blue, 1024 m white, mixed in transition bands.</summary>
        Scale = 6,
        /// <summary>Overlay check (V0.2.0F.1): final albedo / the scans' raw color x 0.5; neutral grey = no tint over the texture.</summary>
        Tint = 7,
    }

    /// <summary>
    /// Projection scale selection of the PBR materials (V0.2.0F.1), CPU mirror of PlanetTerrainMaterials.hlsl
    /// (ScalePosition, ScaleBlend, ScaleNormalStrength, StableFootprint): continuous along four scales in steps of 8.
    /// </summary>
    public static class SurfaceScales
    {
        public const int Steps = 4;
        public const float TilePixels = 320f;
        public const float ReferenceLines = 1080f;

        /// <summary>Continuous scale position (0 .. 3) of a footprint (meters per pixel) for a material tile (meters).</summary>
        public static float Position(float footprint, float tile) =>
            Mathf.Clamp(Mathf.Log(Mathf.Max(footprint * TilePixels / tile, 1e-6f), 2f) / 3f, 0f, Steps - 1);

        /// <summary>Lower step index (0 .. 2) and blend (0 .. 1) towards the next step.</summary>
        public static void Blend(float position, out int step, out float blend)
        {
            step = Mathf.Min(Mathf.FloorToInt(position), Steps - 2);
            float t = position - step;
            float x = Mathf.Clamp01((t - 0.6f) / 0.4f);
            blend = x * x * (3f - 2f * x);
        }

        /// <summary>Effective tile size (meters) shown at a scale position: geometric blend of the two sampled scales.</summary>
        public static float EffectiveTile(float position, float tile)
        {
            Blend(position, out int step, out float blend);
            return tile * Mathf.Pow(8f, step + blend);
        }

        public static float NormalStrength(float position) => Mathf.Pow(2f, -0.15f * position);

        /// <summary>Stable footprint: distance (m) times the pixel angle at the reference resolution for a vertical field of view.</summary>
        public static float Footprint(float distanceMeters, float verticalFovDegrees) =>
            distanceMeters * 2f * Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad) / ReferenceLines;
    }

    /// <summary>
    /// Material selection from the geology (V0.2.0F): the weights of the six materials at a surface point. The
    /// terrain shader evaluates the same formula per pixel (PlanetTerrainMaterials.hlsl, SurfaceWeights); this is its
    /// CPU mirror (tests, diagnostics). No biome table: rock materials come from the province fields (sedimentary
    /// weight, family), ground materials from slope, erosion and the province; the V0.2.0E bedrock mask decides rock
    /// vs ground.
    /// </summary>
    public static class SurfaceMaterials
    {
        public const int Count = 11;

        /// <summary>Rock share at a point, as the V0.2.0E appearance computes it (without the pixel-scale breakup).</summary>
        public static float RockShare(float bedrock) => SmoothStep(0.3f, 0.72f, bedrock);

        /// <summary>Weights of the six geological materials only (no ground cover: V0.2.0F behaviour).</summary>
        public static void Weights(float bedrock, float erosion, float slope, float sedimentary, float family, float[] weights)
        {
            Weights(RockShare(bedrock), erosion, slope, sedimentary, family, Vector4.zero, 1000f, weights);
        }

        /// <summary>
        /// Weights of the eleven materials (sum 1). Rock (the bedrock share, less where snow lies on gentle rock) splits
        /// by province; loose ground splits into snow, vegetation (green grass, dry grass, forest floor: the biomes'
        /// cover, thinned on steep and eroded slopes and on beaches) and bare ground: gravel on slopes and eroded ground,
        /// otherwise soil where the land is vegetated around and sand where it is not (deserts) and on beaches.
        /// cover = green grass, dry grass, forest floor, snow; heightMeters = height above sea level.
        /// </summary>
        public static void Weights(float rockShare, float erosion, float slope, float sedimentary, float family, Vector4 cover, float heightMeters, float[] weights)
        {
            float snowOnRock = Clamp01(cover.w) * (1f - SmoothStep(0.6f, 1.2f, slope));
            float rock = Clamp01(rockShare) * (1f - snowOnRock);
            float crystalline = 1f - Clamp01(sedimentary);
            float basalt = SmoothStep(0.1f, 0.55f, family);
            float limestone = SmoothStep(-0.35f, 0.35f, family);
            weights[(int)SurfaceMaterial.Granite] = rock * crystalline * (1f - basalt);
            weights[(int)SurfaceMaterial.Basalt] = rock * crystalline * basalt;
            weights[(int)SurfaceMaterial.Sandstone] = rock * (1f - crystalline) * (1f - limestone);
            weights[(int)SurfaceMaterial.Limestone] = rock * (1f - crystalline) * limestone;
            float loose = 1f - rock;
            float snow = Clamp01(cover.w);
            float green = Clamp01(cover.x), dry = Clamp01(cover.y), forest = Clamp01(cover.z);
            float plants = Clamp01(green + dry + forest);
            float beach = 1f - SmoothStep(3f, 20f, heightMeters);
            float vegetation = (1f - snow) * plants * (1f - SmoothStep(0.35f, 0.9f, slope)) * (1f - 0.6f * Clamp01(erosion)) * (1f - beach);
            float sum = green + dry + forest;
            float k = sum > 1e-5f ? vegetation / sum : 0f;
            weights[(int)SurfaceMaterial.Snow] = loose * snow;
            weights[(int)SurfaceMaterial.Grass] = loose * green * k;
            weights[(int)SurfaceMaterial.DryGrass] = loose * dry * k;
            weights[(int)SurfaceMaterial.ForestFloor] = loose * forest * k;
            float bare = loose * Mathf.Max(0f, 1f - snow - vegetation);
            float gravel = GravelShare(erosion, slope, sedimentary);
            float soil = Clamp01(plants * 1.5f) * (1f - beach);
            weights[(int)SurfaceMaterial.Gravel] = bare * gravel;
            weights[(int)SurfaceMaterial.Soil] = bare * (1f - gravel) * soil;
            weights[(int)SurfaceMaterial.DrySand] = bare * (1f - gravel) * (1f - soil);
        }

        /// <summary>Debris share of the loose ground: slopes and eroded ground, more under crystalline rock.</summary>
        public static float GravelShare(float erosion, float slope, float sedimentary) =>
            Clamp01(SmoothStep(0.08f, 0.35f, slope) * 0.75f + Clamp01(erosion) * 0.35f + (1f - Clamp01(sedimentary)) * 0.15f);

        /// <summary>
        /// The two materials a pixel samples (bounded cost): the two largest weights, each reduced by the third so that
        /// a rank change between the second and third is continuous, renormalized.
        /// </summary>
        public static void TopTwo(float[] weights, out int first, out float firstWeight, out int second, out float secondWeight)
        {
            // Ties keep the lower index (strict comparisons), as in the shader. Weights are >= 0.
            first = 0;
            second = 0;
            float v1 = -1f, v2 = -1f, v3 = -1f;
            for (int k = 0; k < Count; k++)
            {
                float v = weights[k];
                if (v > v1)
                {
                    v3 = v2;
                    v2 = v1;
                    second = first;
                    v1 = v;
                    first = k;
                }
                else if (v > v2)
                {
                    v3 = v2;
                    v2 = v;
                    second = k;
                }
                else if (v > v3)
                {
                    v3 = v;
                }
            }
            float w3 = Mathf.Max(v3, 0f);
            float a = Mathf.Max(v1 - w3, 1e-6f), b = Mathf.Max(v2 - w3, 0f);
            firstWeight = a / (a + b);
            secondWeight = b / (a + b);
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    }
}
