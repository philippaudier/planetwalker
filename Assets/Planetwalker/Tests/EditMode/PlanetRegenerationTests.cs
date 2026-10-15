using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    public class PlanetRegenerationTests
    {
        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
            }
        }

        // TEST G
        [Test]
        public void RepeatedRegeneration_DoesNotAccumulateObjectsOrMeshes()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var definition = new PlanetDefinition { resolution = 16 };

            visual.Render(new PlanetInstance(definition));
            int childCount = root.transform.childCount;
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var firstMeshes = root.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(7, visual.GeneratedObjectCount);
            Assert.AreEqual(7, childCount);

            for (int i = 0; i < 5; i++)
            {
                definition.seed = 100 + i;
                definition.seaLevel = 0.002f * i;
                definition.resolution = i % 2 == 0 ? 16 : 24;
                visual.Render(new PlanetInstance(definition));
            }

            Assert.AreEqual(childCount, root.transform.childCount);
            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(firstMeshes.Length, filters.Length);
            for (int i = 0; i < filters.Length; i++)
            {
                Assert.AreSame(firstMeshes[i].sharedMesh, filters[i].sharedMesh, "Mesh objects must be reused");
            }
        }

        [Test]
        public void DestroyingController_ReleasesGeneratedMeshes()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Render(new PlanetInstance(new PlanetDefinition { resolution = 16 }));
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>();
            var generated = new Mesh[filters.Length];
            for (int i = 0; i < filters.Length; i++)
            {
                generated[i] = filters[i].sharedMesh;
            }

            Object.DestroyImmediate(root);
            root = null;

            foreach (Mesh mesh in generated)
            {
                Assert.IsTrue(mesh == null, "Generated mesh leaked after destroy");
            }
        }

        // V0.1.1A TEST J
        [Test]
        public void RegenerationWithContinentalShaping_DoesNotLeakMeshesOrObjects()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var definition = new PlanetDefinition { resolution = 16 };
            visual.Render(new PlanetInstance(definition));
            int childCount = root.transform.childCount;
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;

            for (int i = 0; i < 6; i++)
            {
                definition.seed = 500 + i;
                definition.continentalWarpStrength = 0.1f * i;
                definition.continentalWarpFrequency = 0.5f + 0.5f * i;
                definition.coastalComplexity = 0.2f * i;
                visual.DebugView = i % 2 == 0 ? PlanetDebugView.None : PlanetDebugView.ContinentalMask;
                visual.Render(new PlanetInstance(definition));
            }

            Assert.AreEqual(childCount, root.transform.childCount);
            Assert.AreEqual(7, visual.GeneratedObjectCount);
            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
        }

        // V0.1.1B TEST J
        [Test]
        public void RegenerationWithMountainSystems_DoesNotLeakMeshesOrObjects()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var definition = new PlanetDefinition { resolution = 16 };
            visual.Render(new PlanetInstance(definition));
            int childCount = root.transform.childCount;
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;

            PlanetDebugView[] views =
            {
                PlanetDebugView.None, PlanetDebugView.MountainProvinces,
                PlanetDebugView.MountainRanges, PlanetDebugView.ContinentalMask,
            };
            for (int i = 0; i < 8; i++)
            {
                definition.seed = 900 + i;
                definition.mountainRangeCoverage = i / 7f;
                definition.mountainRangeLength = 0.5f + 0.2f * i;
                definition.mountainRuggedness = (i % 3) / 2f;
                visual.DebugView = views[i % views.Length];
                visual.Render(new PlanetInstance(definition));
            }

            Assert.AreEqual(childCount, root.transform.childCount);
            Assert.AreEqual(7, visual.GeneratedObjectCount);
            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
        }

        [Test]
        public void MountainDebugViews_AreGrayscaleOnLandAndKeepOceanHidden()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var planet = new PlanetInstance(new PlanetDefinition { resolution = 16, mountainRangeCoverage = 1f });
            visual.DebugView = PlanetDebugView.MountainProvinces;
            visual.Render(planet);
            Assert.IsFalse(root.transform.Find("Ocean").gameObject.activeSelf);
            visual.DebugView = PlanetDebugView.None;
            visual.Render(planet);
            Assert.IsTrue(root.transform.Find("Ocean").gameObject.activeSelf);
        }

        [Test]
        public void ContinentalMaskView_HidesOceanAndRestoresIt()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var planet = new PlanetInstance(new PlanetDefinition { resolution = 16 });
            visual.DebugView = PlanetDebugView.ContinentalMask;
            visual.Render(planet);
            Transform ocean = root.transform.Find("Ocean");
            Assert.IsFalse(ocean.gameObject.activeSelf);
            Color[] maskColors = root.transform.Find("Terrain +Y").GetComponent<MeshFilter>().sharedMesh.colors;
            foreach (Color c in maskColors)
            {
                Assert.AreEqual(c.r, c.g, 1e-5f, "Mask view is grayscale");
            }

            visual.DebugView = PlanetDebugView.None;
            visual.Render(planet);
            Assert.IsTrue(ocean.gameObject.activeSelf);
        }

        [Test]
        public void OceanRadius_FollowsSeaLevel()
        {
            root = new GameObject("Planet Test");
            var visual = root.AddComponent<PlanetVisualController>();
            var planet = new PlanetInstance(new PlanetDefinition { resolution = 16, radius = 50f, seaLevel = 0.01f });
            visual.Render(planet);
            Transform ocean = root.transform.Find("Ocean");
            Assert.IsNotNull(ocean);
            Assert.AreEqual(50f * 1.01f, ocean.localScale.x, 1e-4f);
            Assert.GreaterOrEqual(visual.BoundingRadius, planet.OceanRadius);
        }
    }
}
