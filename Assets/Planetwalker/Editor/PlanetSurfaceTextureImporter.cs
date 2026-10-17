using System.IO;
using UnityEditor;
using UnityEngine;

namespace Planetwalker.Editor
{
    /// <summary>
    /// Import settings of the planet surface scans (V0.2.0F), by map suffix, for every file under
    /// Materials/PlanetSurface. Base color: sRGB, BC1. Normal: normal map, high-quality compression (BC5: X, Y), OpenGL
    /// convention (+Y up, Unity's: no green flip). Roughness: the material's packed mask, linear: R = smoothness
    /// (1 - roughness, Unity's convention), G = ambient occlusion, B = height, read from the material's _ao / _disp files at
    /// import (declared dependencies: a change to either re-imports it); BC1. AO and height: linear single channel (sources
    /// of the mask, not sampled at runtime). All: mipmaps, 2048 maximum, anisotropic filtering, repeat.
    /// </summary>
    public sealed class PlanetSurfaceTextureImporter : AssetPostprocessor
    {
        public const string Folder = "Assets/Planetwalker/Materials/PlanetSurface/";

        public enum MapKind
        {
            Unknown,
            BaseColor,
            Normal,
            Roughness,
            AmbientOcclusion,
            Height,
        }

        public static MapKind KindOf(string path)
        {
            string p = path.Replace('\\', '/');
            if (!p.StartsWith(Folder) || !(p.EndsWith(".jpg") || p.EndsWith(".png")))
            {
                return MapKind.Unknown;
            }
            if (p.Contains("_diff_")) return MapKind.BaseColor;
            if (p.Contains("_nor_gl_")) return MapKind.Normal;
            if (p.Contains("_rough_")) return MapKind.Roughness;
            if (p.Contains("_ao_")) return MapKind.AmbientOcclusion;
            if (p.Contains("_disp_")) return MapKind.Height;
            return MapKind.Unknown;
        }

        /// <summary>Path of a sibling map of the same material (e.g. the _ao_ file of a _rough_ file).</summary>
        public static string Sibling(string path, string from, string to) => path.Replace('\\', '/').Replace("_" + from + "_", "_" + to + "_");

        void OnPreprocessTexture()
        {
            MapKind kind = KindOf(assetPath);
            if (kind == MapKind.Unknown)
            {
                return;
            }
            var importer = (TextureImporter)assetImporter;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 2048;
            importer.anisoLevel = 8;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Compressed;
            switch (kind)
            {
                case MapKind.BaseColor:
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = true;
                    importer.alphaSource = TextureImporterAlphaSource.None;
                    break;
                case MapKind.Normal:
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.sRGBTexture = false;
                    importer.convertToNormalmap = false;
                    importer.flipGreenChannel = false; // OpenGL (+Y) source, Unity's convention
                    importer.textureCompression = TextureImporterCompression.CompressedHQ; // BC5
                    break;
                case MapKind.Roughness:
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = false;
                    importer.alphaSource = TextureImporterAlphaSource.None;
                    context.DependsOnSourceAsset(Sibling(assetPath, "rough", "ao"));
                    context.DependsOnSourceAsset(Sibling(assetPath, "rough", "disp"));
                    break;
                default:
                    importer.textureType = TextureImporterType.SingleChannel;
                    importer.sRGBTexture = false;
                    importer.alphaSource = TextureImporterAlphaSource.None;
                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
                    importer.SetTextureSettings(settings);
                    break;
            }
        }

        /// <summary>Roughness map: replaced by the packed mask (smoothness, AO, height) before mipmapping and compression.</summary>
        void OnPostprocessTexture(Texture2D texture)
        {
            if (KindOf(assetPath) != MapKind.Roughness)
            {
                return;
            }
            Color32[] ao = LoadSibling("ao", texture.width, texture.height);
            Color32[] height = LoadSibling("disp", texture.width, texture.height);
            for (int mip = 0; mip < texture.mipmapCount; mip++)
            {
                Color32[] rough = texture.GetPixels32(mip);
                int w = Mathf.Max(1, texture.width >> mip), h = Mathf.Max(1, texture.height >> mip);
                int step = texture.width / w;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = x + y * w;
                        byte a = Average(ao, texture.width, x * step, y * step, step);
                        byte d = Average(height, texture.width, x * step, y * step, step);
                        rough[i] = new Color32((byte)(255 - rough[i].r), a, d, 255);
                    }
                }
                texture.SetPixels32(rough, mip);
            }
            texture.Apply(false);
        }

        Color32[] LoadSibling(string map, int width, int height)
        {
            string path = Sibling(assetPath, "rough", map);
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            Color32[] pixels;
            if (File.Exists(path) && image.LoadImage(File.ReadAllBytes(path)) && image.width >= width && image.height >= height)
            {
                if (image.width != width || image.height != height)
                {
                    image.Reinitialize(width, height); // never expected: sources share one resolution
                }
                pixels = image.GetPixels32();
            }
            else
            {
                Debug.LogWarning("Planet surface: missing or mismatched " + path + "; neutral values used.");
                pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color32(255, 255, 255, 255); // AO 1, height 1 (mid blending)
                }
            }
            Object.DestroyImmediate(image);
            return pixels;
        }

        static byte Average(Color32[] full, int width, int x0, int y0, int step)
        {
            if (step == 1)
            {
                return full[x0 + y0 * width].r;
            }
            int sum = 0;
            int n = 0;
            int stride = Mathf.Max(1, step / 4); // subsampled box: enough for AO / height mips
            for (int y = y0; y < y0 + step; y += stride)
            {
                for (int x = x0; x < x0 + step; x += stride)
                {
                    sum += full[x + y * width].r;
                    n++;
                }
            }
            return (byte)(sum / n);
        }
    }
}
