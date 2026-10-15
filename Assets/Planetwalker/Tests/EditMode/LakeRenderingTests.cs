using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1D-B.1 lakes, rendering side: caching, visibility, debug views, leaks (Unity objects).</summary>
    public class LakeRenderingTests
    {
        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("Lake Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            return visual;
        }

        // 22, 27 (rendering side)
        [Test]
        public void RiverWidth_RebuildsRibbonsOnly_AndLakeSettingsNeverTouchTheTerrain()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { seed = 609538, resolution = 32 };
            visual.Render(new PlanetInstance(definition));
            Assert.IsTrue(visual.LakesVisible);
            Assert.IsNotNull(visual.CurrentLakes);
            Assert.Greater(visual.LakeMesh.vertexCount, 0);
            int analyses = visual.LakeCache.AnalysisCount;
            int selections = visual.LakeCache.SelectionCount;
            int lakeMeshes = visual.LakeMeshBuildCount;
            int hydrology = visual.HydrologyCache.ComputeCount;
            int ribbons = visual.RiverRibbonBuildCount;
            Mesh face = root.transform.Find("Terrain +Y").GetComponent<MeshFilter>().sharedMesh;
            Vector3[] terrain = face.vertices;

            // River width: ribbons only.
            visual.Rivers.widthMultiplier = 2f;
            visual.RefreshColors();
            Assert.AreEqual(ribbons + 1, visual.RiverRibbonBuildCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);
            Assert.AreEqual(lakeMeshes, visual.LakeMeshBuildCount);

            // River density: network + layer + ribbons, never the lake candidates.
            visual.Hydrology.riverNetworkThreshold *= 2f;
            visual.RefreshColors();
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);

            // Lake selection: new selection + water mesh + ribbons, same candidates, same terrain.
            int ribbonsBefore = visual.RiverRibbonBuildCount;
            visual.Lakes.abundance = 0.9f;
            visual.RefreshColors();
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount, "Candidates reused");
            Assert.AreEqual(selections + 1, visual.LakeCache.SelectionCount);
            Assert.AreEqual(lakeMeshes + 1, visual.LakeMeshBuildCount);
            Assert.AreEqual(ribbonsBefore + 1, visual.RiverRibbonBuildCount, "Affected river geometry updated");
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            CollectionAssert.AreEqual(terrain, face.vertices, "27: the terrain mesh is untouched");

            // Lake color: nothing rebuilt (material property at draw time).
            visual.Lakes.color = Color.red;
            visual.RefreshColors();
            Assert.AreEqual(selections + 1, visual.LakeCache.SelectionCount);
            Assert.AreEqual(lakeMeshes + 1, visual.LakeMeshBuildCount);

            // Hydrology resolution: candidates recomputed.
            visual.Hydrology.resolution = HydrologySettings.StandardResolution;
            visual.RefreshColors();
            Assert.AreEqual(analyses + 1, visual.LakeCache.AnalysisCount);
        }

        // 23
        [Test]
        public void LakeVisibility_DoesNotRecomputeHydrologyOrLakes()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 280695, resolution = 32 }));
            int hydrology = visual.HydrologyCache.ComputeCount;
            int analyses = visual.LakeCache.AnalysisCount;
            int selections = visual.LakeCache.SelectionCount;
            int lakeMeshes = visual.LakeMeshBuildCount;

            visual.Lakes.showLakes = false;
            visual.RefreshColors();
            Assert.IsFalse(visual.LakesVisible);
            visual.Lakes.showLakes = true;
            visual.RefreshColors();
            Assert.IsTrue(visual.LakesVisible);

            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);
            Assert.AreEqual(lakeMeshes, visual.LakeMeshBuildCount, "The water mesh is kept while hidden");
            Assert.AreEqual(7, visual.GeneratedObjectCount, "Lakes add no GameObject");

            // Lakes off and no hydrology view: no lake work at all on a new planet.
            visual.Lakes.showLakes = false;
            visual.Rivers.showRivers = false;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1, resolution = 32 }));
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
        }

        // 24
        [Test]
        public void LakeDebugViews_AreConsistentAcrossCubeFaces()
        {
            PlanetVisualController visual = CreateVisual();
            var planet = new PlanetInstance(new PlanetDefinition { seed = 868497, resolution = 32 });
            PlanetDebugView[] views =
            {
                PlanetDebugView.LakeCandidates, PlanetDebugView.LakeDepth,
                PlanetDebugView.LakeClassification, PlanetDebugView.LakeConnectivity,
            };
            foreach (PlanetDebugView view in views)
            {
                visual.DebugView = view;
                visual.Render(planet);
                Assert.IsFalse(visual.LakesVisible, "Water is hidden in development views");
                var seen = new Dictionary<Vector3, Color>();
                int shared = 0;
                for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                {
                    Mesh mesh = root.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(face)).GetComponent<MeshFilter>().sharedMesh;
                    Vector3[] vertices = mesh.vertices;
                    Color[] colors = mesh.colors;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Assert.AreNotEqual(Color.magenta, colors[i], view + ": missing lake data");
                        if (seen.TryGetValue(vertices[i], out Color other))
                        {
                            Assert.AreEqual(other, colors[i], view + ": seam color mismatch at " + vertices[i]);
                            shared++;
                        }
                        else
                        {
                            seen.Add(vertices[i], colors[i]);
                        }
                    }
                }
                Assert.Greater(shared, 0, "Seam vertices were compared");
            }
        }

        // 25
        [Test]
        public void RepeatedRegeneration_WithLakes_DoesNotLeak()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { resolution = 16 };
            // Warm-up: debug material created once.
            visual.DebugView = PlanetDebugView.LakeClassification;
            visual.Render(new PlanetInstance(definition));
            visual.DebugView = PlanetDebugView.None;
            visual.Render(new PlanetInstance(definition));
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            int materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;

            PlanetDebugView[] views =
            {
                PlanetDebugView.None, PlanetDebugView.LakeCandidates, PlanetDebugView.None,
                PlanetDebugView.LakeConnectivity, PlanetDebugView.LakeDepth, PlanetDebugView.None,
            };
            for (int i = 0; i < views.Length; i++)
            {
                definition.seed = 900 + i;
                definition.resolution = i % 2 == 0 ? 16 : 24;
                visual.Lakes.abundance = i / 5f;
                visual.Lakes.fillingFactor = 1f - i / 5f;
                visual.Lakes.showLakes = i != 2;
                visual.DebugView = views[i];
                visual.Render(new PlanetInstance(definition));
                visual.RefreshColors();
            }

            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
            Assert.AreEqual(materialCount, Resources.FindObjectsOfTypeAll<Material>().Length);

            Mesh lakes = visual.LakeMesh;
            Assert.IsNotNull(lakes);
            Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(lakes == null, "Lake mesh is released with the planet");
        }
    }
}
