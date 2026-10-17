using System;
using System.IO;
using Planetwalker.Terrain;
using UnityEditor;
using UnityEngine;

namespace Planetwalker.Editor
{
    /// <summary>
    /// Creates or updates the planet surface material library asset (V0.2.0F, Resources/PlanetSurface): references to
    /// the imported scans (base color, normal, packed mask = the roughness file's import) in <see cref="SurfaceMaterial"/>
    /// order, tile sizes and mean albedos (linear, from the source files). Small: the texture arrays are assembled on the
    /// GPU at runtime from these imports (no stored duplicate). Planetwalker > Build Surface Material Library, or
    /// -executeMethod Planetwalker.Editor.PlanetSurfaceLibraryBuilder.Build.
    /// </summary>
    public static class PlanetSurfaceLibraryBuilder
    {
        public const string SourceFolder = "Assets/Planetwalker/Materials/PlanetSurface";
        public const string OutputFolder = "Assets/Planetwalker/Resources/PlanetSurface";
        public const string LibraryPath = OutputFolder + "/PlanetSurfaceLibrary.asset";

        /// <summary>Layer order = <see cref="SurfaceMaterial"/>: folder, scan id, tile size (m, divides the 1024 m anchoring period).</summary>
        public static readonly (string folder, string id, float tile)[] Layers =
        {
            ("Granite", "rock_surface", 2f),
            ("Sandstone", "cliff_side", 2f),
            ("Limestone", "rock_2", 2f),
            ("Basalt", "dark_rock_02", 2f),
            ("DrySand", "gravelly_sand", 2f),
            ("Gravel", "rock_ground", 2f),
            ("Grass", "Grass004", 2f),
            ("DryGrass", "grass_ground", 2f),
            ("Forest", "forrest_ground_01", 2f),
            ("Soil", "brown_mud", 2f),
            ("Snow", "snow_02", 2f),
        };

        public static string MapPath(int layer, string map) =>
            $"{SourceFolder}/{Layers[layer].folder}/{Layers[layer].folder}_{Layers[layer].id}_{map}_2k.jpg";

        [MenuItem("Planetwalker/Build Surface Material Library")]
        public static void Build()
        {
            int count = Layers.Length;
            Directory.CreateDirectory(OutputFolder);
            var library = AssetDatabase.LoadAssetAtPath<PlanetSurfaceLibrary>(LibraryPath);
            bool create = library == null;
            if (create)
            {
                library = ScriptableObject.CreateInstance<PlanetSurfaceLibrary>();
            }
            library.albedoSources = new Texture2D[count];
            library.normalSources = new Texture2D[count];
            library.maskSources = new Texture2D[count];
            library.names = new string[count];
            library.tileMeters = new float[count];
            library.meanAlbedo = new Color[count];
            for (int layer = 0; layer < count; layer++)
            {
                library.albedoSources[layer] = Require(MapPath(layer, "diff"));
                library.normalSources[layer] = Require(MapPath(layer, "nor_gl"));
                library.maskSources[layer] = Require(MapPath(layer, "rough"));
                library.names[layer] = Layers[layer].folder;
                library.tileMeters[layer] = Layers[layer].tile;
                library.meanAlbedo[layer] = MeanLinear(MapPath(layer, "diff"));
            }
            if (create)
            {
                AssetDatabase.CreateAsset(library, LibraryPath);
            }
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log("Planet surface library: " + string.Join(", ", Array.ConvertAll(library.names, n => n))
                + " | albedo " + library.albedoSources[0].graphicsFormat + ", normal " + library.normalSources[0].graphicsFormat
                + ", mask " + library.maskSources[0].graphicsFormat + " | means "
                + string.Join(" ", Array.ConvertAll(library.meanAlbedo, m => ColorUtility.ToHtmlStringRGB(m))));
        }

        static Texture2D Require(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new FileNotFoundException("Planet surface scan not imported: " + path);
            }
            return texture;
        }

        static Color MeanLinear(string path)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            image.LoadImage(File.ReadAllBytes(path));
            Color32[] pixels = image.GetPixels32();
            UnityEngine.Object.DestroyImmediate(image);
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < pixels.Length; i += 7)
            {
                r += Mathf.GammaToLinearSpace(pixels[i].r / 255f);
                g += Mathf.GammaToLinearSpace(pixels[i].g / 255f);
                b += Mathf.GammaToLinearSpace(pixels[i].b / 255f);
            }
            int n = (pixels.Length + 6) / 7;
            return new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
        }
    }
}
