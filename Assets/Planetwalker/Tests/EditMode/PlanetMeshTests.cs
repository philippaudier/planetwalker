using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Tests
{
    public class PlanetMeshTests
    {
        const int Resolution = 24;

        readonly List<Mesh> meshes = new List<Mesh>();

        [TearDown]
        public void TearDown()
        {
            foreach (Mesh mesh in meshes)
            {
                Object.DestroyImmediate(mesh);
            }
            meshes.Clear();
        }

        Mesh[] BuildAllFaces(PlanetInstance planet)
        {
            var builder = new PlanetMeshBuilder();
            var faces = new Mesh[CubeSphereGenerator.FaceCount];
            for (int face = 0; face < faces.Length; face++)
            {
                faces[face] = new Mesh();
                meshes.Add(faces[face]);
                builder.BuildFace(faces[face], face, planet, PlanetColorRamp.CreateDefault());
            }
            return faces;
        }

        static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        [Test]
        public void ResolutionSemantics_SubdivisionsPlusOneVerticesPerEdge()
        {
            Assert.AreEqual(65, CubeSphereGenerator.VerticesPerEdge(64));
            Assert.AreEqual(65 * 65, CubeSphereGenerator.VerticesPerFace(64));
            Assert.AreEqual(64 * 64 * 6, CubeSphereGenerator.IndicesPerFace(64));
            Assert.AreEqual(-1f, CubeSphereGenerator.GridCoordinate(0, 64));
            Assert.AreEqual(1f, CubeSphereGenerator.GridCoordinate(64, 64));
        }

        // TEST E
        [Test]
        public void AllSixFaces_GenerateValidOutwardFacingMeshes()
        {
            var planet = new PlanetInstance(new PlanetDefinition { resolution = Resolution });
            Mesh[] faces = BuildAllFaces(planet);
            int expectedVertices = CubeSphereGenerator.VerticesPerFace(Resolution);
            int expectedIndices = CubeSphereGenerator.IndicesPerFace(Resolution);

            foreach (Mesh mesh in faces)
            {
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Assert.AreEqual(expectedVertices, vertices.Length, mesh.name);
                Assert.AreEqual(expectedVertices, mesh.normals.Length, mesh.name);
                Assert.AreEqual(expectedVertices, mesh.colors.Length, mesh.name);
                Assert.AreEqual(expectedIndices, triangles.Length, mesh.name);

                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    Assert.IsTrue(a >= 0 && a < vertices.Length && b >= 0 && b < vertices.Length && c >= 0 && c < vertices.Length);
                    Assert.IsTrue(a != b && b != c && a != c, "Degenerate triangle in " + mesh.name);
                    // Unity front faces: Cross(b - a, c - a) points toward the viewer (outward).
                    Vector3 faceNormal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    Vector3 centroid = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                    Assert.Greater(Vector3.Dot(faceNormal, centroid), 0f, "Inverted triangle in " + mesh.name);
                }
                Assert.Greater(mesh.bounds.size.magnitude, 0f);
            }
        }

        // TEST F
        [Test]
        public void GeneratedVertices_ContainNoNaNOrInfinity()
        {
            var planet = new PlanetInstance(new PlanetDefinition { resolution = Resolution, seed = -987654 });
            foreach (Mesh mesh in BuildAllFaces(planet))
            {
                foreach (Vector3 v in mesh.vertices)
                {
                    Assert.IsTrue(IsFinite(v), mesh.name);
                }
                foreach (Vector3 n in mesh.normals)
                {
                    Assert.IsTrue(IsFinite(n), mesh.name);
                    Assert.AreEqual(1f, n.magnitude, 1e-3f, mesh.name);
                }
            }
        }

        [Test]
        public void Vertices_MatchElevationFunction()
        {
            // The planetary terrain with its detail bands (no V0.2.0G.1 landforms): within the band-limit tolerance of the
            // canonical function.
            var planet = new PlanetInstance(new PlanetDefinition { resolution = Resolution, regionalLandforms = 0f });
            Mesh[] faces = BuildAllFaces(planet);
            for (int face = 0; face < faces.Length; face++)
            {
                Vector3[] vertices = faces[face].vertices;
                for (int i = 0; i < vertices.Length; i += 37)
                {
                    Vector3 direction = vertices[i].normalized;
                    Assert.AreEqual(planet.GetSurfaceRadius(direction), vertices[i].magnitude, planet.Radius * 1e-4f);
                }
            }
            // With landforms (the default): plateaus are band-limited away at this coarse spacing, so the face mesh is the
            // legacy elevation function it samples, exactly.
            var landforms = new PlanetInstance(new PlanetDefinition { resolution = Resolution });
            Mesh[] withLandforms = BuildAllFaces(landforms);
            for (int face = 0; face < withLandforms.Length; face++)
            {
                Vector3[] vertices = withLandforms[face].vertices;
                for (int i = 0; i < vertices.Length; i += 37)
                {
                    Vector3 direction = vertices[i].normalized;
                    Assert.AreEqual(landforms.Radius * (1f + landforms.GetLegacyElevation(direction)), vertices[i].magnitude, landforms.Radius * 1e-5f);
                }
            }
        }

        [Test]
        public void FaceEdges_ShareIdenticalPositionsAndNormals()
        {
            var planet = new PlanetInstance(new PlanetDefinition { resolution = Resolution });
            Mesh[] faces = BuildAllFaces(planet);
            var seen = new Dictionary<Vector3, Vector3>();
            var occurrences = new Dictionary<Vector3, int>();
            int edge = Resolution + 1;

            for (int face = 0; face < faces.Length; face++)
            {
                Vector3[] vertices = faces[face].vertices;
                Vector3[] normals = faces[face].normals;
                for (int y = 0; y < edge; y++)
                {
                    for (int x = 0; x < edge; x++)
                    {
                        if (x != 0 && y != 0 && x != Resolution && y != Resolution)
                        {
                            continue;
                        }
                        int i = x + y * edge;
                        if (seen.TryGetValue(vertices[i], out Vector3 normal))
                        {
                            // Exact equality: shared border vertices are bit-identical.
                            Assert.AreEqual(normal.x, normals[i].x, 0f);
                            Assert.AreEqual(normal.y, normals[i].y, 0f);
                            Assert.AreEqual(normal.z, normals[i].z, 0f);
                            occurrences[vertices[i]]++;
                        }
                        else
                        {
                            seen.Add(vertices[i], normals[i]);
                            occurrences.Add(vertices[i], 1);
                        }
                    }
                }
            }

            // Every border vertex must be shared by 2 faces (3 at cube corners): no gaps.
            foreach (KeyValuePair<Vector3, int> pair in occurrences)
            {
                Assert.GreaterOrEqual(pair.Value, 2, "Unshared border vertex " + pair.Key);
            }
            int expectedBorderVertices = 12 * (Resolution - 1) + 8;
            Assert.AreEqual(expectedBorderVertices, occurrences.Count);
        }

        [Test]
        public void HighResolution_Uses32BitIndices()
        {
            var planet = new PlanetInstance(new PlanetDefinition { resolution = 256, mountainStrength = 0f, detailStrength = 0f });
            var mesh = new Mesh();
            meshes.Add(mesh);
            new PlanetMeshBuilder().BuildFace(mesh, 0, planet, PlanetColorRamp.CreateDefault());
            Assert.AreEqual(IndexFormat.UInt32, mesh.indexFormat);
            Assert.AreEqual(257 * 257, mesh.vertexCount);
        }
    }
}
