using System.Collections.Generic;
using System.IO;
using Planetwalker.Bootstrap;
using Planetwalker.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Planetwalker.Editor
{
    /// <summary>
    /// Planetwalker > Setup Genesis Scene: creates the material assets and a ready-to-play
    /// Genesis scene (bootstrap, planet, star, orbit camera, UI), then adds it to the
    /// build settings.
    /// </summary>
    public static class GenesisSceneSetup
    {
        public const string ScenePath = "Assets/Planetwalker/Scenes/Genesis.unity";
        const string MaterialsFolder = "Assets/Planetwalker/Materials";
        const string TerrainMaterialPath = MaterialsFolder + "/PlanetTerrain.mat";
        const string OceanMaterialPath = MaterialsFolder + "/PlanetOcean.mat";
        const string DebugMaterialPath = MaterialsFolder + "/PlanetDebugUnlit.mat";
        const string RiverMaterialPath = MaterialsFolder + "/PlanetRiver.mat";
        const string LakeMaterialPath = MaterialsFolder + "/PlanetLake.mat";
        const string AtmosphereMaterialPath = MaterialsFolder + "/PlanetAtmosphere.mat";
        const string CloudMaterialPath = MaterialsFolder + "/PlanetClouds.mat";

        [MenuItem("Planetwalker/Setup Genesis Scene")]
        public static void SetupGenesisScene()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog(
                    "Planetwalker",
                    ScenePath + " already exists.\n\nOverwrite it with a freshly generated Genesis scene?",
                    "Overwrite", "Cancel"))
            {
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Material terrain = GetOrCreateMaterial(TerrainMaterialPath, PlanetVisualController.TerrainShaderName);
            Material ocean = GetOrCreateMaterial(OceanMaterialPath, PlanetVisualController.OceanShaderName);
            // Referenced from the scene so the development shader is also included in builds.
            Material debug = GetOrCreateMaterial(DebugMaterialPath, PlanetVisualController.DebugShaderName);
            Material river = GetOrCreateMaterial(RiverMaterialPath, PlanetVisualController.RiverShaderName);
            Material lake = GetOrCreateMaterial(LakeMaterialPath, PlanetVisualController.LakeShaderName);
            Material atmosphere = GetOrCreateMaterial(AtmosphereMaterialPath, PlanetVisualController.AtmosphereShaderName);
            Material clouds = GetOrCreateMaterial(CloudMaterialPath, PlanetVisualController.CloudShaderName);
            if (terrain == null || ocean == null || debug == null || river == null || lake == null || atmosphere == null || clouds == null)
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bootstrap = new GameObject("Planetwalker").AddComponent<PlanetBootstrap>();
            bootstrap.EnsureSceneObjects();
            bootstrap.Visual.SetMaterials(terrain, ocean, debug, river, lake);
            bootstrap.Visual.SetAtmosphereMaterial(atmosphere);
            bootstrap.Visual.SetCloudMaterial(clouds);
            bootstrap.ApplySpaceEnvironment();

            EnsureFolder(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("Planetwalker: could not save " + ScenePath);
                return;
            }
            AddToBuildSettings(ScenePath);
            Selection.activeGameObject = bootstrap.gameObject;
            Debug.Log("Planetwalker: Genesis scene created at " + ScenePath + ". Press Play to generate the planet.");
        }

        static Material GetOrCreateMaterial(string path, string shaderName)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                return existing;
            }
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("Planetwalker: shader '" + shaderName + "' not found. Check the console for shader errors.");
                return null;
            }
            EnsureFolder(MaterialsFolder);
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static void AddToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (EditorBuildSettingsScene existing in scenes)
            {
                if (existing.path == path)
                {
                    return;
                }
            }
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
