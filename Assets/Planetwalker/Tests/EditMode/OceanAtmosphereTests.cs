using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.2B oceans and atmosphere. Letters refer to the milestone's test list (A - L).</summary>
    public class OceanAtmosphereTests
    {
        static HydrologyGrid grid;

        static HydrologyGrid Grid => grid ??= new HydrologyGrid(BathymetryField.Resolution);

        GameObject root;
        readonly List<Object> extra = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
            foreach (Object o in extra)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            extra.Clear();
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("Ocean Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            return visual;
        }

        static Vector3 Direction(float latitude, float longitude)
        {
            float la = latitude * Mathf.Deg2Rad, lo = longitude * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(la) * Mathf.Cos(lo), Mathf.Sin(la), Mathf.Cos(la) * Mathf.Sin(lo));
        }

        // ------------------------------------------------------------ A, B (data)

        // A
        [Test]
        public void A_Bathymetry_IsDeterministic()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 1337 });
            var a = new BathymetryField(Grid, planet.Height.GetElevation);
            var b = new BathymetryField(new HydrologyGrid(BathymetryField.Resolution), new PlanetInstance(new PlanetDefinition { seed = 1337 }).Height.GetElevation);
            CollectionAssert.AreEqual(a.Elevation, b.Elevation);
            var la = new OceanDepthLayer(a, planet.SeaLevel, planet.Definition.continentStrength);
            var lb = new OceanDepthLayer(b, planet.SeaLevel, planet.Definition.continentStrength);
            CollectionAssert.AreEqual(la.Shelf, lb.Shelf);
            var directions = new Vector3[2000];
            for (int i = 0; i < directions.Length; i++)
            {
                directions[i] = Direction(UnityEngine.Random.Range(-89f, 89f), UnityEngine.Random.Range(-180f, 180f));
            }
            List<Vector2> da = new List<Vector2>(), db = new List<Vector2>();
            la.WriteVertexData(directions, da);
            lb.WriteVertexData(directions, db);
            CollectionAssert.AreEqual(da, db);
            var other = new BathymetryField(Grid, new PlanetInstance(new PlanetDefinition { seed = 536452 }).Height.GetElevation);
            CollectionAssert.AreNotEqual(a.Elevation, other.Elevation, "Different seeds, different bathymetry");
        }

        // B
        [Test]
        public void B_Depth_IsMonotonicAndClampedAtSeaLevel()
        {
            // Synthetic basin: elevation falls linearly with the angle from a coast pole.
            Vector3 pole = Vector3.up;
            Func<Vector3, float> basin = d => 0.01f - 0.0008f * Vector3.Angle(d, pole);
            var field = new BathymetryField(Grid, basin);
            var layer = new OceanDepthLayer(field, 0f, 0.025f);
            float previous = -1f;
            for (float angle = 0f; angle <= 60f; angle += 0.5f)
            {
                Vector3 d = Direction(90f - angle, 37f);
                float depth = layer.NormalizedDepth(d);
                Assert.GreaterOrEqual(depth, 0f);
                if (basin(d) >= 0.0015f)
                {
                    Assert.AreEqual(0f, depth, "Land (well above sea level): depth clamped to 0");
                }
                Assert.GreaterOrEqual(depth, previous - 1e-5f, "Depth never decreases going down the basin");
                previous = depth;
            }
            // Normalization: depth / basin scale (continentStrength), dimensionless.
            Vector3 deep = Direction(90f - 50f, 37f);
            Assert.AreEqual(-basin(deep) / 0.025f, layer.NormalizedDepth(deep), 0.01f);
            Assert.That(layer.OceanFraction, Is.InRange(0.5f, 1f));

            // Shelves: a broad shallow shelf keeps a small shelf depth; a thin shallow sliver along a steep
            // coast does not (no bright outline along every coastline).
            Vector3 shelfCenter = Direction(0f, 0f), cliffCoast = Direction(0f, 180f);
            Func<Vector3, float> terrain = d =>
            {
                float toShelf = Vector3.Angle(d, shelfCenter), toCliff = Vector3.Angle(d, cliffCoast);
                if (toShelf < 20f) return toShelf < 8f ? 0.005f : -0.001f;              // island on a 12-degree shelf
                if (toCliff < 10f) return toCliff < 8f ? 0.005f : -0.001f - 0.02f * (toCliff - 8f); // island, cliff to deep water
                return -0.022f;
            };
            var layer2 = new OceanDepthLayer(new BathymetryField(Grid, terrain), 0f, 0.025f);
            float shelf = layer2.NormalizedShelfDepth(Direction(0f, 14f));
            float sliver = layer2.NormalizedShelfDepth(Direction(0f, 180f - 8.4f));
            TestContext.WriteLine($"shelf depth: broad shelf {shelf:0.000}, sliver by a steep coast {sliver:0.000}");
            Assert.Less(shelf, 0.08f, "Broad shelf reads as a shelf");
            Assert.Greater(sliver, 0.3f, "Sliver next to deep water does not");
        }

        // ------------------------------------------------------------ C, D (controller data)

        // C
        [Test]
        public void C_OceanDepths_AreConsistentAcrossCubeSeams()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 32 }));
            Assert.IsTrue(visual.OceanShadingActive);
            Mesh mesh = visual.OceanMesh;
            Vector3[] vertices = mesh.vertices;
            var uv = new List<Vector2>();
            mesh.GetUVs(1, uv);
            Assert.AreEqual(vertices.Length, uv.Count);
            var seen = new Dictionary<Vector3, Vector2>();
            int shared = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (seen.TryGetValue(vertices[i], out Vector2 other))
                {
                    Assert.AreEqual(other, uv[i], "Same direction, same depth on both faces");
                    shared++;
                }
                else
                {
                    seen.Add(vertices[i], uv[i]);
                }
                Assert.IsFalse(float.IsNaN(uv[i].x) || float.IsNaN(uv[i].y));
            }
            Assert.Greater(shared, 12 * visual.OceanMeshResolution, "Every cube edge is shared by two faces");
            // Continuity across a cube edge (x = z): a jump would stay however close the two points are;
            // a steep but continuous depth change shrinks with their distance.
            OceanDepthLayer layer = visual.CurrentOceanDepth;
            float far = 0f, near = 0f;
            for (float y = -0.6f; y <= 0.6f; y += 0.005f)
            {
                Vector3 a = new Vector3(1f, y, 0.999f).normalized, b = new Vector3(0.999f, y, 1f).normalized;
                far = Mathf.Max(far, Mathf.Abs(layer.NormalizedDepth(a) - layer.NormalizedDepth(b)));
                a = new Vector3(1f, y, 0.99999f).normalized;
                b = new Vector3(0.99999f, y, 1f).normalized;
                near = Mathf.Max(near, Mathf.Abs(layer.NormalizedDepth(a) - layer.NormalizedDepth(b)));
            }
            TestContext.WriteLine($"cube edge: largest depth difference 0.04 degree apart {far:0.0000}, 0.0004 degree apart {near:0.00000}");
            Assert.Less(near, 0.002f, "No jump across the cube edge");
            Assert.Less(near, 0.1f * far + 1e-4f, "Differences shrink with the distance (continuous)");
        }

        // D
        [Test]
        public void D_SeaLevelChange_ReusesTheElevationGrid()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 536452, resolution = 24, seaLevel = 0f }));
            OceanDepthLayer low = visual.CurrentOceanDepth;
            int fields = visual.BathymetryCache.FieldComputeCount, layers = visual.BathymetryCache.LayerComputeCount;
            int uploads = visual.OceanDepthUploadCount;
            var higher = new PlanetInstance(new PlanetDefinition { seed = 536452, resolution = 24, seaLevel = 0.01f });
            visual.Render(higher);
            OceanDepthLayer high = visual.CurrentOceanDepth;
            Assert.AreEqual(fields, visual.BathymetryCache.FieldComputeCount, "Elevations do not depend on sea level");
            Assert.AreEqual(layers + 1, visual.BathymetryCache.LayerComputeCount);
            Assert.AreEqual(uploads + 1, visual.OceanDepthUploadCount);
            Assert.AreEqual(0.01f, high.SeaLevel);
            Assert.Greater(high.OceanFraction, low.OceanFraction);
            Assert.AreEqual(higher.OceanRadius * (1f + visual.OceanAtmosphere.atmosphereThickness), visual.AtmosphereRadius, 1e-4f);
            // Deep water: exactly 0.01 deeper (normalized by the basin scale).
            Vector3 d = Vector3.zero;
            for (int c = 0; c < low.Field.Grid.CellCount; c++)
            {
                if (low.Field.Elevation[c] < -0.015f) { d = low.Field.Grid.GetCenter(c); break; }
            }
            Assert.AreNotEqual(Vector3.zero, d);
            Assert.AreEqual(0.01f / high.BasinDepth, high.NormalizedDepth(d) - low.NormalizedDepth(d), 1e-3f);
            // Same planet again: nothing recomputed or re-uploaded.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 536452, resolution = 24, seaLevel = 0.01f }));
            Assert.AreEqual(layers + 1, visual.BathymetryCache.LayerComputeCount);
            Assert.AreEqual(uploads + 1, visual.OceanDepthUploadCount);
            // Terrain change: new grid.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 536453, resolution = 24, seaLevel = 0.01f }));
            Assert.AreEqual(fields + 1, visual.BathymetryCache.FieldComputeCount);
        }

        // ------------------------------------------------------------ E, F, G (settings and modes)

        static void ChangeEverything(OceanAtmosphereSettings s)
        {
            s.depthContrast = 0.9f;
            s.shelfVisibility = 1f;
            s.oceanRoughness = 0.7f;
            s.specularIntensity = 0.1f;
            s.atmosphereStrength = 1.7f;
            s.atmosphereThickness = 0.06f;
            s.terminatorSoftness = 0.8f;
        }

        // E, F
        [Test]
        public void EF_VisualSettings_TouchNoDataAndNoCache()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            var planet = new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 24 });
            visual.Render(planet);
            visual.RefreshColors(); // colors compared below come from the same recolor path
            int climate = visual.ClimateCache.ComputeCount, biomes = visual.BiomeCache.ComputeCount, slope = visual.SlopeCache.ComputeCount;
            int hydrology = visual.HydrologyCache.ComputeCount, lakes = visual.LakeCache.SelectionCount, natural = visual.NaturalSurfaceBuildCount;
            int fields = visual.BathymetryCache.FieldComputeCount, layers = visual.BathymetryCache.LayerComputeCount, uploads = visual.OceanDepthUploadCount;
            float[] scores = (float[])visual.CurrentBiomes.Scores.Clone();
            float[] temperature = (float[])visual.CurrentClimate.Temperature.Clone();
            float[] elevation = (float[])visual.CurrentOceanDepth.Field.Elevation.Clone();
            Mesh face = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(0)).GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = face.vertices;
            Color[] colors = face.colors;
            float bounding = visual.BoundingRadius;

            ChangeEverything(visual.OceanAtmosphere);
            visual.ApplyOceanAtmosphere();
            visual.OceanAtmosphere.oceanShading = false;
            visual.OceanAtmosphere.atmosphere = false;
            visual.RefreshColors();
            visual.OceanAtmosphere.oceanShading = true;
            visual.OceanAtmosphere.atmosphere = true;
            visual.RefreshColors();

            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount);
            Assert.AreEqual(slope, visual.SlopeCache.ComputeCount);
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(lakes, visual.LakeCache.SelectionCount);
            Assert.AreEqual(natural, visual.NaturalSurfaceBuildCount);
            Assert.AreEqual(fields, visual.BathymetryCache.FieldComputeCount, "Bathymetry reused");
            Assert.AreEqual(layers, visual.BathymetryCache.LayerComputeCount);
            Assert.AreEqual(uploads, visual.OceanDepthUploadCount, "Ocean depths not re-uploaded");
            CollectionAssert.AreEqual(scores, visual.CurrentBiomes.Scores);
            CollectionAssert.AreEqual(temperature, visual.CurrentClimate.Temperature);
            CollectionAssert.AreEqual(elevation, visual.CurrentOceanDepth.Field.Elevation);
            CollectionAssert.AreEqual(vertices, face.vertices, "Terrain geometry untouched");
            CollectionAssert.AreEqual(colors, face.colors, "Natural Planet colors untouched");
            Assert.AreEqual(bounding, visual.BoundingRadius, "Thickness is not planet geometry");
            Assert.AreEqual(planet.OceanRadius, visual.transform.Find("Ocean").localScale.x, 1e-5f, "Ocean stays at sea level");
        }

        static float BlockFloat(Renderer renderer, string name)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetFloat(name);
        }

        static Vector4 BlockVector(Renderer renderer, string name)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetVector(name);
        }

        // G
        [Test]
        public void G_DebugViews_SuppressOceanAndAtmosphere_AndTogglesWork()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 868497, resolution = 24 }));
            MeshRenderer ocean = visual.OceanRenderer;
            MeshRenderer terrain = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(1)).GetComponent<MeshRenderer>();
            Assert.IsTrue(visual.OceanShadingActive);
            Assert.IsTrue(visual.AtmosphereVisible);
            Assert.AreEqual(1f, BlockFloat(ocean, "_OceanShading"));
            Assert.AreEqual(visual.OceanAtmosphere.LightWrap, BlockFloat(terrain, "_LightWrap"), 1e-6f);
            Assert.AreEqual(0.08f, visual.OceanAtmosphere.LightWrap, 1e-6f, "Default terminator = the previous terrain wrap");
            // Haze between camera and surface: same sphere as the shell, strength from the settings.
            Vector4 shell = BlockVector(terrain, "_AtmoShell"), haze = BlockVector(terrain, "_AtmoParams");
            Assert.AreEqual(visual.AtmosphereRadius, shell.w, 1e-3f);
            Assert.AreEqual(visual.Planet.OceanRadius, haze.x, 1e-3f);
            Assert.AreEqual(visual.OceanAtmosphere.atmosphereStrength, haze.w, 1e-6f);
            Assert.Greater(haze.y, 0f, "Density copied from the atmosphere material");
            Assert.AreEqual(haze, BlockVector(ocean, "_AtmoParams"), "Terrain and ocean share the parameters");

            foreach (PlanetDebugView view in new[] { PlanetDebugView.BiomeMap, PlanetDebugView.ClimateTemperature, PlanetDebugView.FlowAccumulation })
            {
                visual.DebugView = view;
                visual.RefreshColors();
                Assert.IsFalse(visual.OceanShadingActive, view + ": no ocean tint");
                Assert.IsFalse(visual.AtmosphereVisible, view + ": no atmosphere overlay");
                Assert.IsFalse(ocean.gameObject.activeSelf, view + ": ocean hidden");
                Assert.IsFalse(terrain.HasPropertyBlock(), view + ": debug material untouched");
            }
            visual.DebugView = PlanetDebugView.None;
            visual.RefreshColors();
            Assert.IsTrue(visual.OceanShadingActive);
            Assert.IsTrue(visual.AtmosphereVisible);

            // Before / after toggles.
            visual.OceanAtmosphere.oceanShading = false;
            visual.ApplyOceanAtmosphere();
            Assert.IsFalse(visual.OceanShadingActive);
            Assert.AreEqual(0f, BlockFloat(ocean, "_OceanShading"), "Previous flat ocean");
            visual.OceanAtmosphere.atmosphere = false;
            visual.ApplyOceanAtmosphere();
            Assert.IsFalse(visual.AtmosphereVisible);
            Assert.AreEqual(0f, BlockVector(terrain, "_AtmoParams").w, "No haze without the atmosphere");
            Assert.AreEqual(0f, BlockVector(ocean, "_AtmoParams").w);
            visual.OceanAtmosphere.oceanShading = true;
            visual.OceanAtmosphere.atmosphere = true;
            visual.ApplyOceanAtmosphere();
            Assert.IsTrue(visual.OceanShadingActive && visual.AtmosphereVisible);
            // Terminator softness drives the same wrap on terrain and ocean.
            visual.OceanAtmosphere.terminatorSoftness = 0.75f;
            visual.ApplyOceanAtmosphere();
            Assert.AreEqual(0.3f, BlockFloat(terrain, "_LightWrap"), 1e-6f);
            Assert.AreEqual(0.3f, BlockFloat(ocean, "_LightWrap"), 1e-6f);
        }

        // ------------------------------------------------------------ H, I, J, K (lifecycle and shaders)

        // H, I
        [Test]
        public void HI_AtmosphereFollowsThePlanet_AndResourcesAreReused()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 24, radius = 100f }));
            Mesh atmosphere = visual.AtmosphereMesh, ocean = visual.OceanMesh;
            Material material = visual.AtmosphereMaterialInUse;
            Assert.IsNotNull(atmosphere);
            Assert.IsNotNull(material);
            Assert.AreEqual(100f * 1.025f, visual.AtmosphereRadius, 1e-3f);
            Assert.Greater(visual.AtmosphereRadius, visual.Planet.OceanRadius, "Shell above the ocean");

            var regenerated = new PlanetInstance(new PlanetDefinition { seed = 280695, resolution = 32, radius = 250f, seaLevel = -0.01f });
            visual.Render(regenerated);
            visual.OceanAtmosphere.atmosphereThickness = 0.04f;
            for (int i = 0; i < 20; i++)
            {
                visual.OceanAtmosphere.atmosphereStrength = i * 0.1f;
                visual.ApplyOceanAtmosphere();
            }
            Assert.AreEqual(regenerated.OceanRadius * 1.04f, visual.AtmosphereRadius, 1e-3f, "Anchored to the regenerated ocean radius");
            Assert.AreSame(atmosphere, visual.AtmosphereMesh, "Atmosphere mesh reused");
            Assert.AreSame(ocean, visual.OceanMesh, "Ocean mesh reused");
            Assert.AreSame(material, visual.AtmosphereMaterialInUse, "No material per change");
            Assert.AreEqual(7, visual.GeneratedObjectCount, "The atmosphere adds no GameObject");

            Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(atmosphere == null, "Atmosphere mesh destroyed");
            Assert.IsTrue(ocean == null, "Ocean mesh destroyed");
            Assert.IsTrue(material == null, "Runtime atmosphere material destroyed");
        }

        // J
        [Test]
        public void J_MissingOrIncompatibleShaders_FallBackSafely()
        {
            PlanetVisualController visual = CreateVisual();
            var unlit = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            extra.Add(unlit);
            visual.SetAtmosphereMaterial(unlit);
            LogAssert.Expect(LogType.Warning, new Regex("atmosphere disabled"));
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 16 }));
            Assert.IsFalse(visual.AtmosphereVisible, "No opaque generic sphere over the planet");
            Assert.IsNull(visual.AtmosphereMaterialInUse);
            Assert.IsTrue(visual.OceanShadingActive, "The ocean does not depend on the atmosphere");

            // An ocean material without the bathymetry properties: previous ocean, no bathymetry work.
            TearDown();
            visual = CreateVisual();
            var plainOcean = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            extra.Add(plainOcean);
            visual.SetMaterials(null, plainOcean);
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 16 }));
            Assert.IsFalse(visual.OceanShadingActive);
            Assert.AreEqual(0, visual.BathymetryCache.FieldComputeCount);
            Assert.IsTrue(visual.AtmosphereVisible);
        }

        // K
        [Test]
        public void K_Shaders_CompileAndExposeTheirProperties()
        {
            Shader ocean = Shader.Find(PlanetVisualController.OceanShaderName);
            Shader atmosphere = Shader.Find(PlanetVisualController.AtmosphereShaderName);
            Assert.IsNotNull(ocean);
            Assert.IsNotNull(atmosphere);
            foreach (Shader shader in new[] { ocean, atmosphere })
            {
                Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(shader), shader.name + " compiles");
                Assert.IsTrue(shader.isSupported, shader.name + " supported");
            }
            foreach (string p in new[] { "_OceanShading", "_DeepColor", "_ShallowColor", "_ShelfColor", "_DepthContrast", "_ShelfVisibility",
                         "_Roughness", "_SpecularIntensity", "_LightWrap", "_Color", "_RimColor", "_Opacity" })
            {
                Assert.GreaterOrEqual(ocean.FindPropertyIndex(p), 0, "Ocean " + p);
            }
            Shader terrainShader = Shader.Find(PlanetVisualController.TerrainShaderName);
            Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(terrainShader), "Terrain shader compiles");
            var plainTerrain = new Material(terrainShader);
            extra.Add(plainTerrain);
            Assert.AreEqual(0f, plainTerrain.GetVector("_AtmoParams").w, "Without the controller: no haze (previous terrain look)");
            foreach (string p in new[] { "_AtmosphereColor", "_WarmColor", "_Strength", "_Density", "_Falloff", "_PlanetRatio", "_NightLevel", "_SurfaceOpticalDepth" })
            {
                Assert.GreaterOrEqual(atmosphere.FindPropertyIndex(p), 0, "Atmosphere " + p);
            }
            var material = new Material(ocean);
            extra.Add(material);
            Assert.AreEqual(0f, material.GetFloat("_OceanShading"), "Without the controller: the previous flat ocean");
            Assert.AreEqual(3000, material.renderQueue);
            var shell = new Material(atmosphere);
            extra.Add(shell);
            Assert.AreEqual(3100, shell.renderQueue, "Atmosphere after the ocean, lakes and rivers");
            // The shipped ocean asset keeps its previous values.
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Planetwalker/Materials/PlanetOcean.mat");
            Assert.IsNotNull(asset);
            Assert.AreEqual(0.74f, asset.GetFloat("_Opacity"), 1e-4f);
            Assert.AreEqual(0.35f, asset.GetFloat("_SpecularStrength"), 1e-4f);
            var terrain = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Planetwalker/Materials/PlanetTerrain.mat");
            Assert.AreEqual(terrain.GetFloat("_LightWrap"), new OceanAtmosphereSettings().LightWrap, 1e-6f, "Default terminator = terrain asset wrap");
        }

        // ------------------------------------------------------------ L (performance)

        [Test]
        public void L_Performance_AndAllocations_Report()
        {
            foreach (int seed in new[] { 1337, 536452, 868497, 926747 })
            {
                var planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                var field = new BathymetryField(Grid, planet.Height.GetElevation);
                var layer = new OceanDepthLayer(field, planet.SeaLevel, planet.Definition.continentStrength);
                int shelfCells = 0, ocean = 0;
                for (int c = 0; c < layer.Depth.Length; c++)
                {
                    if (field.Elevation[c] >= planet.SeaLevel) continue;
                    ocean++;
                    shelfCells += layer.Shelf[c] / layer.BasinDepth < 0.3f && layer.Depth[c] / layer.BasinDepth < 0.4f ? 1 : 0;
                }
                TestContext.WriteLine($"seed {seed}: ocean {layer.OceanFraction * 100f:0}% of surface, mean depth {layer.MeanDepth / layer.BasinDepth:0.00} x basin scale, "
                    + $"shelf-tinted cells {100f * shelfCells / Mathf.Max(1, ocean):0.0}% of ocean; grid {field.Milliseconds:0} ms, layer {layer.Milliseconds:0} ms");
            }

            PlanetVisualController visual = CreateVisual();
            visual.Climate.resolution = ClimateSettings.DefaultResolution;
            var definition = new PlanetDefinition { seed = 868497, resolution = 256 };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            visual.Render(new PlanetInstance(definition));
            double first = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            visual.Render(new PlanetInstance(definition));
            double warm = watch.Elapsed.TotalMilliseconds;
            visual.OceanAtmosphere.oceanShading = false;
            visual.OceanAtmosphere.atmosphere = false;
            visual.RefreshColors();
            watch.Restart();
            visual.Render(new PlanetInstance(definition));
            double warmOff = watch.Elapsed.TotalMilliseconds;
            visual.OceanAtmosphere.oceanShading = true;
            visual.OceanAtmosphere.atmosphere = true;
            visual.ApplyOceanAtmosphere();
            var sea = new PlanetDefinition { seed = 868497, resolution = 256, seaLevel = 0.005f };
            watch.Restart();
            visual.Render(new PlanetInstance(sea));
            double seaLevel = watch.Elapsed.TotalMilliseconds;

            visual.ApplyOceanAtmosphere();
            long before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            for (int i = 0; i < 100; i++)
            {
                visual.OceanAtmosphere.oceanRoughness = 0.1f + i * 0.005f;
                visual.ApplyOceanAtmosphere();
            }
            double slider = watch.Elapsed.TotalMilliseconds / 100.0;
            long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
            TestContext.WriteLine($"Terrain 256 / climate 64: first render {first:0} ms, warm re-render {warm:0} ms (oceans & atmosphere off: {warmOff:0} ms), "
                + $"sea-level change {seaLevel:0} ms; visual slider change {slider:0.000} ms, {allocated} bytes allocated per change");
            Assert.Less(slider, 2.0, "Slider changes are cheap");
            Assert.Less(allocated, 2048, "No per-change mesh / material / texture allocation");
        }
    }
}
