using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.Core;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Physically based surface materials (V0.2.0F) and the coastal pass: material selection (determinism, ranges,
    /// cube-face continuity, LOD independence, steep / flat cases), anchoring, texture import conventions, roughness to
    /// smoothness, texture arrays, shader and runtime wiring, procedural fallback, presentation-only settings, no
    /// geometry change, no material instance per patch; coast: beach vs rocky shore, continuity, determinism.
    /// </summary>
    public class SurfaceMaterialTests
    {
        const string SourceFolder = "Assets/Planetwalker/Materials/PlanetSurface";
        static readonly string[] Folders = { "Granite", "Sandstone", "Limestone", "Basalt", "DrySand", "Gravel", "Grass", "DryGrass", "Forest", "Soil", "Snow" };
        static readonly Dictionary<int, PlanetInstance> Planets = new Dictionary<int, PlanetInstance>();
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            created.Clear();
        }

        static PlanetInstance Planet(int seed = 310929)
        {
            if (!Planets.TryGetValue(seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                Planets[seed] = planet;
            }
            return planet;
        }

        /// <summary>
        /// The reference planet as V0.2.0F calibrated its shore statistics on (no regional landforms, V0.2.0G.1): the coast
        /// tests measure the geology's shore model on that very terrain; with landforms the coastal plains change which
        /// shores exist (LandformTests checks the coastline itself).
        /// </summary>
        static PlanetInstance CalibrationPlanet()
        {
            const int seed = 310929;
            if (!Planets.TryGetValue(-seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, regionalLandforms = 0f });
                Planets[-seed] = planet;
            }
            return planet;
        }

        static float[] Weights(in GeologySample g)
        {
            var w = new float[SurfaceMaterials.Count];
            SurfaceMaterials.Weights(g.Bedrock, g.Erosion, g.Slope, g.Sedimentary, g.Family, w);
            return w;
        }

        static IEnumerable<Vector3d> Fibonacci(int n)
        {
            for (int k = 0; k < n; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / n, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                yield return new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
            }
        }

        static GeologySample Sample(SurfaceGeology geology, Vector3d d)
        {
            geology.Sample(d, out GeologySample g);
            return g;
        }

        // ------------------------------------------------------------------ 1, 2, 15: selection

        [Test]
        public void M01_MaterialSelection_IsDeterministic_AcrossInstancesAndThreads()
        {
            PlanetInstance planet = Planet();
            Vector3d[] points = Fibonacci(400).ToArray();
            float[][] expected = points.Select(d => Weights(Sample(new SurfaceGeology(planet, null), d))).ToArray();
            var geology = new SurfaceGeology(planet, null);
            var parallel = new float[points.Length][];
            Parallel.For(0, points.Length, k => parallel[k] = Weights(Sample(geology, points[k])));
            for (int k = 0; k < points.Length; k++)
            {
                CollectionAssert.AreEqual(expected[k], parallel[k], "Point " + k);
            }
        }

        [Test]
        public void M02_Weights_AreFinite_InRange_AndSumToOne()
        {
            var geology = new SurfaceGeology(Planet(), null);
            foreach (Vector3d d in Fibonacci(3000))
            {
                float[] w = Weights(Sample(geology, d));
                Assert.IsTrue(w.All(x => float.IsFinite(x) && x >= 0f && x <= 1f));
                Assert.AreEqual(1f, w.Sum(), 1e-4f);
                SurfaceMaterials.TopTwo(w, out int first, out float a, out int second, out float b);
                Assert.AreNotEqual(first, second);
                Assert.AreEqual(1f, a + b, 1e-5f);
                Assert.GreaterOrEqual(a, b);
                Assert.IsTrue(w[first] >= w.Where((x, i) => i != first).Max());
            }
            // Rank swaps between the second and third material are continuous: the second's weight is zero there.
            var tie = new float[SurfaceMaterials.Count];
            tie[0] = 0.5f; tie[1] = 0.25f; tie[2] = 0.25f;
            SurfaceMaterials.TopTwo(tie, out _, out _, out _, out float tieSecond);
            Assert.AreEqual(0f, tieSecond, 1e-6f);
        }

        [Test]
        public void M15_SteepAndFlatTerrain_SelectPlausibleMaterials()
        {
            var w = new float[SurfaceMaterials.Count];
            void Top(float bedrock, float erosion, float slope, float sedimentary, float family, SurfaceMaterial expected, string why)
            {
                SurfaceMaterials.Weights(bedrock, erosion, slope, sedimentary, family, w);
                SurfaceMaterials.TopTwo(w, out int first, out _, out _, out _);
                Assert.AreEqual(expected, (SurfaceMaterial)first, why);
            }
            Top(1f, 0.5f, 1.2f, 0.9f, -0.6f, SurfaceMaterial.Sandstone, "Steep sedimentary cliff");
            Top(1f, 0.5f, 1.2f, 0.9f, 0.7f, SurfaceMaterial.Limestone, "Steep pale sedimentary rock");
            Top(1f, 0.3f, 1.0f, 0.1f, -0.5f, SurfaceMaterial.Granite, "Crystalline mountain");
            Top(1f, 0.3f, 1.0f, 0.1f, 0.8f, SurfaceMaterial.Basalt, "Dark volcanic rock");
            Top(0f, 0.0f, 0.02f, 0.6f, 0f, SurfaceMaterial.DrySand, "Flat desert floor");
            Top(0.1f, 0.8f, 0.4f, 0.2f, 0f, SurfaceMaterial.Gravel, "Eroded debris slope");

            // Biome ground cover (V0.2.0F.2).
            void Cover(float rockShare, float slope, Vector4 cover, float height, SurfaceMaterial expected, string why)
            {
                SurfaceMaterials.Weights(rockShare, 0.1f, slope, 0.5f, 0f, cover, height, w);
                Assert.AreEqual(1f, w.Sum(), 1e-4f, why);
                SurfaceMaterials.TopTwo(w, out int first, out _, out _, out _);
                Assert.AreEqual(expected, (SurfaceMaterial)first, why);
            }
            Cover(0f, 0.05f, new Vector4(0.85f, 0.1f, 0f, 0f), 300f, SurfaceMaterial.Grass, "Temperate grassland");
            Cover(0f, 0.05f, new Vector4(0.1f, 0.75f, 0f, 0f), 300f, SurfaceMaterial.DryGrass, "Steppe");
            Cover(0f, 0.05f, new Vector4(0.1f, 0f, 0.85f, 0f), 300f, SurfaceMaterial.ForestFloor, "Forest");
            Cover(0f, 0.05f, new Vector4(0f, 0f, 0f, 1f), 300f, SurfaceMaterial.Snow, "Ice cap");
            Cover(0f, 0.05f, new Vector4(0.85f, 0.1f, 0f, 0f), 2f, SurfaceMaterial.DrySand, "Beach in a green biome: sand, not grass");
            Cover(0f, 0.05f, new Vector4(0f, 0.05f, 0f, 0f), 300f, SurfaceMaterial.DrySand, "Desert floor");
            Cover(0f, 0.05f, new Vector4(0.25f, 0.1f, 0f, 0f), 300f, SurfaceMaterial.Soil, "Sparse cover on humid ground: soil between the grass");
            SurfaceMaterials.Weights(1f, 0.1f, 1.2f, 0.5f, 0f, new Vector4(0.8f, 0f, 0f, 0f), 300f, w);
            SurfaceMaterials.TopTwo(w, out int steepFirst, out _, out _, out _);
            Assert.Less(steepFirst, 4, "Steep rock stays rock in a green biome");
            Cover(1f, 0.3f, new Vector4(0f, 0f, 0f, 1f), 300f, SurfaceMaterial.Snow, "Snow covers gentle rock");
        }

        // ------------------------------------------------------------------ 3, 4, 5: continuity, LOD, origin

        [Test]
        public void M03_MaterialWeights_AreContinuousAcrossCubeFaceEdges_ByConvergence()
        {
            // As the geology (SurfaceGeologyTests.C): the weights' difference across an edge vanishes with the
            // separation (20 m, 2 m, 0.2 m). V0.2.0F compared against the local gradient; a steep gradient could hide a seam.
            var geology = new SurfaceGeology(Planet(), null);
            EdgeContinuity.Result r = EdgeContinuity.Probe(d => Weights(Sample(geology, d)));
            Debug.Log($"Material weights across cube edges ({r.Pairs} places): worst difference 20 m {r.Worst20m:0.0000}, 2 m {r.Worst2m:0.0000}, 0.2 m {r.Worst02m:0.0000} at {r.WorstAt}");
            Assert.Less(r.Worst02m, 0.01f, "No jump at the cube-face edges: worst at " + r.WorstAt);
        }

        static PatchGeometryData Build(AdaptiveSurfaceSnapshot snapshot, PlanetPatchId id)
        {
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            new AdaptivePatchGeometryBuilder().Build(snapshot, id, data);
            return data;
        }

        static float[] VertexWeights(PatchGeometryData data, int v)
        {
            var w = new float[SurfaceMaterials.Count];
            SurfaceMaterials.Weights(SurfaceMaterials.RockShare(data.Geology[v].x), data.Geology[v].z, data.Grid[v].w, data.RockTints[v].a, data.RockFamily[v],
                data.Cover[v], data.Grid[v].z, w);
            return w;
        }

        [Test]
        public void M04_MaterialInputs_AreIdenticalAtEveryPatchLevel()
        {
            var snapshot = new AdaptiveSurfaceSnapshot(1, Planet(), null, PlanetColorRamp.CreateDefault(), true, 16);
            int r = snapshot.Resolution;
            for (int level = 12; level <= 15; level++)
            {
                int n = 1 << level;
                var parent = new PlanetPatchId(2, level, (int)(n * 0.41), (int)(n * 0.57));
                var child = new PlanetPatchId(parent.Face, level + 1, parent.X * 2, parent.Y * 2);
                PatchGeometryData coarse = Build(snapshot, parent), fine = Build(snapshot, child);
                for (int j = 0; j <= r / 2; j++)
                {
                    for (int i = 0; i <= r / 2; i++)
                    {
                        int vc = i + j * (r + 1), vf = 2 * i + 2 * j * (r + 1);
                        CollectionAssert.AreEqual(VertexWeights(coarse, vc), VertexWeights(fine, vf), $"Level {level} vs {level + 1}, vertex {i},{j}");
                        Assert.AreEqual(coarse.RockFamily[vc], fine.RockFamily[vf]);
                        Assert.AreEqual(coarse.RockTints[vc].a, fine.RockTints[vf].a, "Sedimentary weight");
                    }
                }
            }
        }

        [Test]
        public void M05_TextureAnchoring_IsInvariantUnderTheFloatingOrigin()
        {
            PlanetSurfaceLibrary library = PlanetSurfaceLibrary.Load();
            Assert.IsNotNull(library, "Library in Resources");
            double period = AdaptiveTerrainRenderer.GroundNoisePeriodMeters;
            foreach (float tile in library.tileMeters)
            {
                // Every projection scale (tile, x8, x64) divides the anchoring period: a new snapped origin moves the
                // texture coordinates by whole tiles only (no swimming, no jump at a rebase).
                foreach (double scale in new[] { tile, tile * 8.0, tile * 64.0 })
                {
                    double tiles = period / scale;
                    Assert.AreEqual(Math.Round(tiles), tiles, 1e-9, $"{scale} m divides {period} m");
                }
            }
        }

        // ------------------------------------------------------------------ 6, 7, 8, 9: textures

        [Test]
        public void M06_M07_SourceTextures_HaveTheExpectedImportSettings()
        {
            for (int k = 0; k < Folders.Length; k++)
            {
                string folder = SourceFolder + "/" + Folders[k];
                string[] files = Directory.GetFiles(folder, "*.jpg").Select(f => f.Replace('\\', '/')).ToArray();
                Assert.AreEqual(5, files.Length, folder);
                foreach (string file in files)
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(file);
                    Assert.IsNotNull(importer, file);
                    Assert.IsTrue(importer.mipmapEnabled, file + ": mipmaps");
                    Assert.AreEqual(2048, importer.maxTextureSize, file);
                    Assert.AreNotEqual(TextureImporterCompression.Uncompressed, importer.textureCompression, file + ": compressed");
                    if (file.Contains("_diff_"))
                    {
                        Assert.IsTrue(importer.sRGBTexture, file + ": base color is sRGB");
                    }
                    else if (file.Contains("_nor_gl_"))
                    {
                        Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType, file);
                        Assert.IsFalse(importer.sRGBTexture, file + ": linear");
                        Assert.IsFalse(importer.flipGreenChannel, file + ": OpenGL source = Unity convention, no flip");
                    }
                    else
                    {
                        Assert.IsFalse(importer.sRGBTexture, file + ": data maps are linear");
                    }
                }
            }
        }

        [Test]
        public void M07_NormalMaps_AreOpenGLConvention_GreenUp()
        {
            // The convention, from each material's own height map: in OpenGL normals (+Y up, Unity's) the red channel
            // follows -dh/dx and the green channel -dh/dv with v up the image (rows grow downwards: green follows +dh/drow).
            // A DirectX map would correlate negatively in green. Z (blue) stays up.
            for (int k = 0; k < Folders.Length; k++)
            {
                string folder = SourceFolder + "/" + Folders[k];
                Color32[] n = LoadPixels(Directory.GetFiles(folder, "*_nor_gl_2k.jpg").Single(), out int size);
                Color32[] h = LoadPixels(Directory.GetFiles(folder, "*_disp_2k.jpg").Single(), out _);
                double red = 0, green = 0, blue = 0;
                int count = 0;
                for (int y = 4; y < size - 4; y += 7)
                {
                    for (int x = 4; x < size - 4; x += 7)
                    {
                        int i = x + y * size;
                        // Texture rows: GetPixels32 starts at the bottom row, so +1 in the array index is up the image.
                        double dhdx = h[i + 1].r - h[i - 1].r;
                        double dhdUp = h[i + size].r - h[i - size].r;
                        red += (n[i].r - 127.5) * -dhdx;
                        green += (n[i].g - 127.5) * -dhdUp;
                        blue += n[i].b;
                        count++;
                    }
                }
                Assert.Greater(red, 0.0, Folders[k] + ": red follows -dh/dx");
                Assert.Greater(green, 0.0, Folders[k] + ": green follows -dh/dv (OpenGL, +Y up); a DirectX map would be negative");
                Assert.Greater(blue / count, 180.0, Folders[k] + ": tangent-space Z up");
            }
        }

        Color32[] LoadPixels(string file, out int size)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            created.Add(image);
            Assert.IsTrue(image.LoadImage(File.ReadAllBytes(file)), file);
            size = image.width;
            return image.GetPixels32();
        }

        [Test]
        public void M08_MaskRed_IsOneMinusTheScanRoughness()
        {
            PlanetSurfaceLibrary library = PlanetSurfaceLibrary.Load();
            Assert.IsTrue(library.HasSources);
            for (int k = 0; k < Folders.Length; k++)
            {
                string roughFile = Directory.GetFiles(SourceFolder + "/" + Folders[k], "*_rough_2k.jpg").Single();
                var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                created.Add(image);
                image.LoadImage(File.ReadAllBytes(roughFile));
                double roughness = image.GetPixels32().Average(p => p.r / 255.0);
                // GPU read of the imported (compressed) mask's smallest mip: its mean.
                Texture2D mask = library.maskSources[k];
                var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(mask, rt);
                var read = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                created.Add(read);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
                double smoothness = read.GetPixel(0, 0).r;
                Assert.AreEqual(1.0 - roughness, smoothness, 0.08, $"{Folders[k]}: smoothness = 1 - roughness (Unity convention)");
            }
        }

        [Test]
        public void M09_TextureArrays_AreConsistent()
        {
            PlanetSurfaceLibrary library = PlanetSurfaceLibrary.Load();
            Assert.IsTrue(library.EnsureArrays(), library.Status);
            foreach (Texture2DArray array in new[] { library.Albedo, library.Normal, library.Mask })
            {
                Assert.AreEqual(SurfaceMaterials.Count, array.depth, array.name);
                Assert.AreEqual(2048, array.width);
                Assert.AreEqual(2048, array.height);
                Assert.AreEqual(12, array.mipmapCount, "Full mip chain");
                Assert.AreEqual(TextureWrapMode.Repeat, array.wrapMode);
            }
            Assert.IsTrue(GraphicsFormatUtility.IsSRGBFormat(library.Albedo.graphicsFormat), "Albedo sampled as sRGB");
            Assert.IsFalse(GraphicsFormatUtility.IsSRGBFormat(library.Normal.graphicsFormat), "Normal linear");
            Assert.IsFalse(GraphicsFormatUtility.IsSRGBFormat(library.Mask.graphicsFormat), "Mask linear");
            Assert.IsTrue(GraphicsFormatUtility.IsCompressedFormat(library.Albedo.graphicsFormat), "Compressed");
            CollectionAssert.AreEqual(Folders, library.names, "Layer order = SurfaceMaterial");
            Assert.IsTrue(library.meanAlbedo.All(c => c.r > 0f && c.r < 1f && c.g > 0f && c.b > 0f));
        }

        // ------------------------------------------------------------------ 10, 11, 12, 13, 14: runtime

        sealed class Rig
        {
            public WorldTestHost Host;
            public AdaptiveTerrainRenderer Terrain;
            public Camera Camera;
        }

        Rig CreateRig(bool detail = true)
        {
            var host = new WorldTestHost(created);
            host.Definition.surfaceDetail = detail;
            host.Regenerate();
            PlanetRenderFrame.GetOrCreate(host.Root.transform);
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(host.Visual);
            terrain.BackgroundConstruction = false;
            terrain.ConstructionMilliseconds = 100000f;
            terrain.MaxUploadsPerFrame = 4096;
            terrain.MaxPendingBuilds = 4096;
            terrain.enabled = true;
            Camera camera = host.Camera.GetComponent<Camera>();
            terrain.ViewCamera = camera;
            for (int t = 0; t < 200 && !terrain.IsDisplaying; t++) terrain.Tick(camera);
            return new Rig { Host = host, Terrain = terrain, Camera = camera };
        }

        [Test]
        public void M10_M11_ShaderPropertiesAndModes_AreWired_WithProceduralFallback()
        {
            var material = new Material(Shader.Find("Planetwalker/PlanetTerrain"));
            created.Add(material);
            foreach (string p in new[] { "_SurfParams", "_SurfAlbedo", "_SurfNormal", "_SurfMask" })
            {
                Assert.IsTrue(material.HasProperty(p), p);
            }
            var ocean = new Material(Shader.Find("Planetwalker/PlanetOcean"));
            created.Add(ocean);
            Assert.IsTrue(ocean.HasProperty("_ShoreParams") && ocean.HasProperty("_ShoreColor"), "Ocean shore properties");

            Rig rig = CreateRig();
            var block = new MaterialPropertyBlock();
            rig.Terrain.MaterialMode = SurfaceMaterialMode.Pbr;
            rig.Terrain.GeologyEnabled = true;
            rig.Terrain.WriteGroundDetail(block, rig.Camera);
            Assert.IsTrue(rig.Terrain.PbrActive, rig.Terrain.MaterialStatus);
            Assert.AreEqual(1f, block.GetVector("_SurfParams").x);
            Assert.AreSame(PlanetSurfaceLibrary.Load().Albedo, block.GetTexture("_SurfAlbedo"));
            Assert.AreEqual(2f, block.GetVectorArray("_SurfMean")[0].w, "Tile size");

            rig.Terrain.MaterialDebug = SurfaceMaterialDebugView.Roughness;
            rig.Terrain.WriteGroundDetail(block, rig.Camera);
            Assert.AreEqual((float)SurfaceMaterialDebugView.Roughness, block.GetVector("_SurfParams").z);
            rig.Terrain.MaterialDebug = SurfaceMaterialDebugView.FinalLit;

            rig.Terrain.MaterialMode = SurfaceMaterialMode.Procedural;
            rig.Terrain.WriteGroundDetail(block, rig.Camera);
            Assert.IsFalse(rig.Terrain.PbrActive);
            Assert.AreEqual(0f, block.GetVector("_SurfParams").x, "Procedural (V0.2.0E) mode");
            rig.Terrain.MaterialMode = SurfaceMaterialMode.Pbr;
            rig.Terrain.GeologyEnabled = false;
            rig.Terrain.WriteGroundDetail(block, rig.Camera);
            Assert.AreEqual(0f, block.GetVector("_SurfParams").x, "No geology: procedural fallback");
            StringAssert.Contains("geological", rig.Terrain.MaterialStatus);
            rig.Terrain.GeologyEnabled = true;
        }

        [Test]
        public void M12_MaterialAndShoreSettings_AreNotPartOfTheWorld()
        {
            string directory = WorldArchiveTestUtil.CreateTempDirectory();
            try
            {
                var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
                var bootstrap = new GameObject("Materials Bootstrap Test").AddComponent<PlanetBootstrap>();
                bootstrap.EnsureSceneObjects();
                foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                {
                    if (!before.Contains(o) && o.transform.parent == null) created.Add(o);
                }
                bootstrap.UseWorldStore(new WorldArchiveStore(directory));
                bootstrap.Definition.resolution = 16;
                bootstrap.Visual.Climate.resolution = ClimateSettings.LowResolution;
                bootstrap.Regenerate();
                bootstrap.Worlds.ResetBaseline();
                int generations = bootstrap.GenerationCount;
                AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(bootstrap.Visual);
                terrain.MaterialMode = SurfaceMaterialMode.Procedural;
                terrain.PbrDetailStrength = 0.4f;
                terrain.MaterialDebug = SurfaceMaterialDebugView.Normal;
                bootstrap.Visual.ShallowWater = false;
                bootstrap.Visual.ApplyOceanAtmosphere();
                Assert.IsFalse(bootstrap.Worlds.RefreshDirty(), "Presentation only: the world is unchanged");
                Assert.AreEqual(generations, bootstrap.GenerationCount, "No regeneration");
                terrain.MaterialMode = SurfaceMaterialMode.Pbr;
                terrain.PbrDetailStrength = 1f;
                terrain.MaterialDebug = SurfaceMaterialDebugView.FinalLit;
                bootstrap.Visual.ShallowWater = true;
            }
            finally
            {
                WorldArchiveTestUtil.DeleteTempDirectory(directory);
            }
        }

        [Test]
        public void M13_M14_MaterialsChangeNoGeometry_AndCreateNoMaterialPerPatch()
        {
            Rig rig = CreateRig();
            PlanetInstance planet = rig.Host.Current;
            Vector3d[] probes = Fibonacci(50).ToArray();
            float[] heights = probes.Select(d => planet.Height.GetElevation(d)).ToArray();
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            foreach (SurfaceMaterialMode mode in new[] { SurfaceMaterialMode.Pbr, SurfaceMaterialMode.Procedural, SurfaceMaterialMode.Pbr })
            {
                rig.Terrain.MaterialMode = mode;
                for (int i = 0; i < 5; i++)
                {
                    rig.Terrain.Tick(rig.Camera);
                    rig.Terrain.Draw(rig.Camera);
                }
            }
            Assert.Greater(rig.Terrain.DrawnPatches, 10);
            Assert.AreEqual(materials, Resources.FindObjectsOfTypeAll<Material>().Length, "One terrain material, one property block");
            for (int k = 0; k < probes.Length; k++)
            {
                Assert.AreEqual(heights[k], planet.Height.GetElevation(probes[k]), "Heights unchanged");
            }
        }

        // ------------------------------------------------------------------ V0.2.0F.1: scale transitions

        static IEnumerable<float> Footprints(int perOctave = 64)
        {
            // 1e-4 m (a few cm from the camera) to 1e3 m per pixel (orbit), log spaced.
            for (int k = 0; k <= 23 * perOctave; k++)
            {
                yield return 1e-4f * Mathf.Pow(2f, k / (float)perOctave);
            }
        }

        [Test]
        public void S01_S04_ScaleState_IsValidEverywhere()
        {
            foreach (float tile in new[] { 1f, 2f, 4f })
            {
                foreach (float footprint in Footprints())
                {
                    float s = SurfaceScales.Position(footprint, tile);
                    SurfaceScales.Blend(s, out int step, out float blend);
                    Assert.IsTrue(s >= 0f && s <= SurfaceScales.Steps - 1, $"Position {s}");
                    Assert.IsTrue(step >= 0 && step <= SurfaceScales.Steps - 2, $"Step {step}");
                    Assert.IsTrue(blend >= 0f && blend <= 1f && float.IsFinite(blend), $"Blend {blend}");
                    float strength = SurfaceScales.NormalStrength(s);
                    Assert.IsTrue(strength > 0f && strength <= 1f);
                }
            }
        }

        [Test]
        public void S01_S02_S03_ScaleTransitions_AreContinuous_AndBounded()
        {
            // The shown tile size (geometric blend of the two sampled scales) never jumps: between two footprints 1/64
            // octave apart it changes by a bounded factor, including across the step boundaries (s = 1 and 2).
            const float tile = 2f;
            float previous = -1f, previousS = -1f, worstRatio = 1f;
            foreach (float footprint in Footprints())
            {
                float s = SurfaceScales.Position(footprint, tile);
                float shown = SurfaceScales.EffectiveTile(s, tile);
                if (previous > 0f)
                {
                    float ratio = Mathf.Max(shown / previous, previous / shown);
                    worstRatio = Mathf.Max(worstRatio, ratio);
                    Assert.GreaterOrEqual(shown, previous * 0.9999f, "Monotonic: farther never shows a finer scale");
                    Assert.Less(Mathf.Abs(s - previousS), 0.01f, "Position continuous");
                }
                previous = shown;
                previousS = s;
            }
            // 1/64 octave of footprint: smoothstep over 40 % of a 3-octave step -> slope at most 1.5 / 1.2 octaves.
            Assert.Less(worstRatio, Mathf.Pow(2f, 1.5f * 3f / (1.2f * 64f) + 0.01f), "Bounded rate of change");
            // At the step boundaries both sides show the same scale.
            foreach (float boundary in new[] { 1f, 2f })
            {
                Assert.AreEqual(SurfaceScales.EffectiveTile(boundary - 1e-4f, tile), SurfaceScales.EffectiveTile(boundary + 1e-4f, tile), tile * 0.01f, $"Boundary {boundary}");
            }
            // Pure scales away from the bands (one sample set), blends only inside them.
            SurfaceScales.Blend(0.3f, out _, out float pure);
            Assert.AreEqual(0f, pure);
            SurfaceScales.Blend(0.8f, out _, out float mid);
            Assert.AreEqual(0.5f, mid, 1e-5f);
        }

        [Test]
        public void S03_StableFootprint_DependsOnDistanceOnly()
        {
            // No screen derivatives: the same distance gives the same footprint, whatever the triangle, the rotation or
            // the resolution (reference 1080 lines); proportional to the distance.
            float a = SurfaceScales.Footprint(100f, 60f), b = SurfaceScales.Footprint(200f, 60f);
            Assert.AreEqual(2f * a, b, a * 1e-5f);
            Assert.AreEqual(100f * 2f * Mathf.Tan(30f * Mathf.Deg2Rad) / 1080f, a, a * 1e-5f);
            // Small camera moves change the scale state by small amounts (no step-like response).
            float s0 = SurfaceScales.Position(SurfaceScales.Footprint(1000f, 60f), 2f);
            float s1 = SurfaceScales.Position(SurfaceScales.Footprint(1010f, 60f), 2f);
            Assert.Less(Mathf.Abs(s1 - s0), 0.01f);
        }

        [Test]
        public void S06_TriplanarNormal_IsTheSurfaceNormal_ForANeutralTexture()
        {
            // Mirror of SampleLayer's whiteout blend: a flat tangent normal (0, 0, 1) must return the geometric normal on
            // every projection, whatever the axis signs; a tilted one stays unit length after blending and renormalizing.
            var rng = new System.Random(7);
            for (int k = 0; k < 500; k++)
            {
                var n = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f).normalized;
                Vector3 blend = TriplanarBlend(n);
                Vector3 result = Triplanar(n, blend, new Vector3(0f, 0f, 1f));
                Assert.Less((result - n).magnitude, 1e-4f, $"Neutral texture keeps the normal {n}");
                Vector3 tilted = Triplanar(n, blend, new Vector3(0.3f, -0.2f, 0.93f).normalized);
                Assert.AreEqual(1f, tilted.magnitude, 1e-4f);
                Assert.Greater(Vector3.Dot(tilted, n), 0.8f, "Detail perturbs, never flips, the surface normal");
            }
        }

        static Vector3 TriplanarBlend(Vector3 n)
        {
            var b = new Vector3(Mathf.Pow(Mathf.Abs(n.x), 12f), Mathf.Pow(Mathf.Abs(n.y), 12f), Mathf.Pow(Mathf.Abs(n.z), 12f));
            b /= Mathf.Max(b.x + b.y + b.z, 1e-6f);
            b = new Vector3(Mathf.Clamp01((b.x - 0.06f) / 0.94f), Mathf.Clamp01((b.y - 0.06f) / 0.94f), Mathf.Clamp01((b.z - 0.06f) / 0.94f));
            return b / Mathf.Max(b.x + b.y + b.z, 1e-6f);
        }

        static Vector3 Triplanar(Vector3 n, Vector3 blend, Vector3 t)
        {
            float sx = n.x >= 0f ? 1f : -1f, sy = n.y >= 0f ? 1f : -1f, sz = n.z >= 0f ? 1f : -1f;
            Vector3 sum = Vector3.zero;
            var tx = new Vector3(t.x * sx + n.z, t.y + n.y, Mathf.Abs(t.z) * n.x);
            sum += new Vector3(tx.z, tx.y, tx.x) * blend.x;
            var ty = new Vector3(t.x * sy + n.x, t.y + n.z, Mathf.Abs(t.z) * n.y);
            sum += new Vector3(ty.x, ty.z, ty.y) * blend.y;
            var tz = new Vector3(-t.x * sz + n.x, t.y + n.y, Mathf.Abs(t.z) * n.z);
            sum += tz * blend.z;
            return sum.normalized;
        }

        [Test]
        public void S15_DrawingAStableView_AllocatesNothing()
        {
            Rig rig = CreateRig();
            for (int i = 0; i < 10; i++)
            {
                rig.Terrain.Tick(rig.Camera);
                rig.Terrain.Draw(rig.Camera);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++)
            {
                rig.Terrain.Draw(rig.Camera);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Less(allocated, 1024, $"Managed allocation while drawing a stable view: {allocated} bytes over 30 frames");
        }

        // ------------------------------------------------------------------ Coast

        static List<(Vector3d d, GeologySample g, double above)> Coastal(PlanetInstance planet, SurfaceGeology geology, double maxMeters, int n = 300000)
        {
            var list = new List<(Vector3d, GeologySample, double)>();
            double radiusM = planet.Scale.RadiusMeters;
            foreach (Vector3d d in Fibonacci(n))
            {
                double above = (planet.Height.GetElevation(d) - planet.SeaLevel) * radiusM;
                if (above > 0.0 && above < maxMeters)
                {
                    list.Add((d, Sample(geology, d), above));
                }
            }
            return list;
        }

        [Test]
        public void Coast_GentleShoresGetBeaches_SteepShoresStayRocky()
        {
            PlanetInstance planet = CalibrationPlanet();
            var geology = new SurfaceGeology(planet, null);
            var points = Coastal(planet, geology, 12.0);
            var gentle = points.Where(p => p.g.Slope < 0.08f).ToList();
            var steep = points.Where(p => p.g.Slope > 0.35f).ToList();
            Assert.Greater(gentle.Count, 2, "Gentle shore samples");
            Assert.Greater(steep.Count, 1, "Steep shore samples");
            double gentleSediment = gentle.Average(p => p.g.Sediment), steepSediment = steep.Average(p => p.g.Sediment);
            double gentleRock = gentle.Average(p => p.g.Bedrock), steepRock = steep.Average(p => p.g.Bedrock);
            Debug.Log($"Shore (0-12 m): gentle n={gentle.Count} sediment {gentleSediment:0.00} bedrock {gentleRock:0.00}; steep n={steep.Count} sediment {steepSediment:0.00} bedrock {steepRock:0.00}");
            Assert.Greater(gentleSediment, steepSediment + 0.3, "Beaches on gentle shores");
            Assert.Greater(steepRock, gentleRock + 0.3, "Rock reaches the water on steep shores");
        }

        [Test]
        public void Coast_NoUniformRing_HighFlatLandIsNotForcedToBeach()
        {
            // V0.2.0E forced sediment over ~1.9 km of elevation above the sea. Now the coastal term ends within tens of
            // meters: flat land a few hundred meters up is decided by its own slope and concavity only.
            PlanetInstance planet = CalibrationPlanet();
            var geology = new SurfaceGeology(planet, null);
            double radiusM = planet.Scale.RadiusMeters;
            int checkedPoints = 0;
            foreach (Vector3d d in Fibonacci(30000))
            {
                double above = (planet.Height.GetElevation(d) - planet.SeaLevel) * radiusM;
                if (above < 300.0 || above > 1500.0) continue;
                GeologySample g = Sample(geology, d);
                if (g.Slope > 0.3f && Mathf.Abs(g.Concavity) < 0.01f)
                {
                    // Steep, straight ground away from the shore: no beach term removing its rock.
                    Assert.Greater(g.Bedrock, 0.3f, $"Steep ground {above:0} m above the sea keeps its rock");
                    checkedPoints++;
                }
            }
            Assert.Greater(checkedPoints, 3);
        }

        [Test]
        public void Coast_TransitionInland_IsSmooth_AndDeterministic()
        {
            PlanetInstance planet = Planet();
            var geology = new SurfaceGeology(planet, null);
            var other = new SurfaceGeology(planet, null);
            var shores = Coastal(planet, geology, 8.0).Where(p => p.g.Downslope.sqrMagnitude > 0.5f).Take(8).ToList();
            Assert.Greater(shores.Count, 3);
            double stepUnits = planet.Scale.ToUnits(10.0) / planet.Radius;
            foreach ((Vector3d d, GeologySample g, double _) in shores)
            {
                Vector3d inland = new Vector3d(-g.Downslope);
                GeologySample previous = Sample(geology, d);
                for (int k = 1; k <= 60; k++)
                {
                    Vector3d p = (d + inland * (stepUnits * k)).Normalized;
                    GeologySample s = Sample(geology, p);
                    Assert.Less(Mathf.Abs(s.Sediment - previous.Sediment), 0.12f, $"Sediment step {k} inland");
                    Assert.Less(Mathf.Abs(s.Bedrock - previous.Bedrock), 0.12f, $"Bedrock step {k} inland");
                    GeologySample again = Sample(other, p);
                    Assert.AreEqual(s.Sediment, again.Sediment, "Deterministic");
                    Assert.AreEqual(s.Bedrock, again.Bedrock, "Deterministic");
                    previous = s;
                }
            }
        }

        [Test]
        public void Coast_SedimentVariesAlongTheShore_NotAUniformRing()
        {
            // Low gentle shore points all around the planet: their sediment (beach) differs from place to place, since
            // the beach band's height follows a field of its own and the slope; a uniform ring would give one value.
            PlanetInstance planet = Planet();
            var geology = new SurfaceGeology(planet, null);
            var shore = Coastal(planet, geology, 25.0).Where(p => p.g.Slope < 0.2f).Select(p => (double)p.g.Sediment).ToList();
            Assert.Greater(shore.Count, 10);
            double mean = shore.Average();
            double deviation = Math.Sqrt(shore.Average(x => (x - mean) * (x - mean)));
            Debug.Log($"Shore sediment (0-25 m, gentle): n={shore.Count}, mean {mean:0.00}, deviation {deviation:0.00}");
            Assert.Greater(deviation, 0.08, "Coastal material varies along the shore");
        }

        [Test]
        public void Coast_ShallowWaterParameters_DoNotDependOnTheFloatingOrigin()
        {
            var host = new WorldTestHost(created);
            host.Regenerate();
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            host.Visual.ApplyOceanAtmosphere();
            var block = new MaterialPropertyBlock();
            host.Visual.OceanRenderer.GetPropertyBlock(block);
            Vector4 before = block.GetVector("_ShoreParams");
            Assert.AreEqual(1f, before.z, "Shallow water on by default");
            Assert.AreEqual((float)host.Current.Scale.MetersPerUnit, before.x, before.x * 1e-5f, "Meters per world unit");
            frame.RebaseNow(new Vector3d(host.Current.Radius * 1.01, 0.4, -0.3));
            host.Visual.ApplyOceanAtmosphere();
            host.Visual.OceanRenderer.GetPropertyBlock(block);
            Assert.AreEqual(before, block.GetVector("_ShoreParams"), "Rebasing changes nothing");
        }
    }
}
