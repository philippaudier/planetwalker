using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Terrain;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Adaptive terrain foundation (V0.2.0A), engine independent: patch addressing, hierarchy, coverage, the
    /// cube-sphere coordinates (against the existing projection), cross-face and cross-level neighbors, shared
    /// boundaries, elevation equivalence, patch meshes, LOD selection (determinism, proximity, hysteresis, budget,
    /// coverage) and performance / allocation reports.
    /// </summary>
    public class AdaptiveTerrainTests
    {
        static readonly Dictionary<int, PlanetInstance> Planets = new Dictionary<int, PlanetInstance>();

        static PlanetInstance Planet(int seed = 1337)
        {
            if (!Planets.TryGetValue(seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                Planets[seed] = planet;
            }
            return planet;
        }

        static IEnumerable<PlanetPatchId> AllPatches(int level)
        {
            int size = 1 << level;
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        yield return new PlanetPatchId(face, level, x, y);
                    }
                }
            }
        }

        static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        static bool SameBits(Vector3 a, Vector3 b) => Bits(a.x) == Bits(b.x) && Bits(a.y) == Bits(b.y) && Bits(a.z) == Bits(b.z);

        static TerrainLodView View(float altitudeRadii, PlanetInstance planet, Vector3 direction = default)
        {
            Vector3 d = direction == default ? new Vector3(0.3f, 0.5f, -0.81f).normalized : direction.normalized;
            float distance = planet.Radius * (1f + Mathf.Max(planet.MaxElevation, 0f)) + planet.Radius * altitudeRadii;
            return new TerrainLodView(d * distance, 60f, 1080);
        }

        // ------------------------------------------------------------ A, B

        // A
        [Test]
        public void A_PatchIds_AreValueTypes_WithUniqueKeysAndStableHashes()
        {
            var keys = new HashSet<ulong>();
            var hashes = new HashSet<int>();
            int count = 0;
            for (int level = 0; level <= 5; level++)
            {
                foreach (PlanetPatchId id in AllPatches(level))
                {
                    Assert.IsTrue(keys.Add(id.Key), "Unique key " + id);
                    hashes.Add(id.GetHashCode());
                    Assert.AreEqual(id, PlanetPatchId.FromKey(id.Key));
                    Assert.AreEqual(id, new PlanetPatchId(id.Face, id.Level, id.X, id.Y));
                    Assert.AreEqual(id.GetHashCode(), new PlanetPatchId(id.Face, id.Level, id.X, id.Y).GetHashCode());
                    count++;
                }
            }
            Assert.Greater(hashes.Count, count * 0.999, "Hashes spread");
            var deep = new PlanetPatchId(5, PlanetPatchId.MaxLevel, (1 << 24) - 1, (1 << 24) - 1);
            Assert.AreEqual(5, deep.Face);
            Assert.AreEqual(PlanetPatchId.MaxLevel, deep.Level);
            Assert.AreEqual((1 << 24) - 1, deep.X);
            Assert.AreEqual(deep, PlanetPatchId.FromKey(deep.Key));
            Assert.IsFalse(deep.CanSubdivide);
            Assert.Throws<InvalidOperationException>(() => deep.Child(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetPatchId(6, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetPatchId(0, 2, 4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetPatchId(0, PlanetPatchId.MaxLevel + 1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetPatchId(0, 1, -1, 0));
            Assert.Throws<InvalidOperationException>(() => _ = PlanetPatchId.Root(0).Parent);
            Assert.AreEqual("+X L3 (5, 2)", new PlanetPatchId(3, 3, 5, 2).ToString());
        }

        // B
        [Test]
        public void B_ParentChildHierarchy_IsConsistent()
        {
            foreach (PlanetPatchId id in AllPatches(3))
            {
                for (int c = 0; c < 4; c++)
                {
                    PlanetPatchId child = id.Child(c);
                    Assert.AreEqual(id, child.Parent);
                    Assert.AreEqual(c, child.ChildIndex);
                    Assert.IsTrue(id.Contains(child));
                    Assert.IsFalse(child.Contains(id));
                    Assert.AreEqual(id, child.Child(3).AncestorAt(3));
                }
                // Documented order: 0 SW, 1 SE, 2 NW, 3 NE (x east along A, y north along B).
                Assert.AreEqual((id.X * 2, id.Y * 2), (id.Child(0).X, id.Child(0).Y));
                Assert.AreEqual((id.X * 2 + 1, id.Y * 2), (id.Child(1).X, id.Child(1).Y));
                Assert.AreEqual((id.X * 2, id.Y * 2 + 1), (id.Child(2).X, id.Child(2).Y));
                Assert.AreEqual((id.X * 2 + 1, id.Y * 2 + 1), (id.Child(3).X, id.Child(3).Y));
            }
            Assert.IsFalse(PlanetPatchId.Root(0).Contains(PlanetPatchId.Root(1).Child(0)), "Other faces are never contained");
        }

        // ------------------------------------------------------------ C, D, E

        // C
        [Test]
        public void C_UniformAndMixedLevels_CoverEachFaceExactlyOnce()
        {
            for (int level = 0; level <= 4; level++)
            {
                CoverageReport report = PatchCoverage.Validate(AllPatches(level).ToList());
                Assert.IsTrue(report.Valid, "Level " + level + ": " + report.Problem);
                Assert.AreEqual(0, report.TransitionEdges);
            }
            var mixed = AllPatches(1).ToList();
            PlanetPatchId refined = mixed[5];
            mixed.Remove(refined);
            for (int c = 0; c < 4; c++)
            {
                mixed.Add(refined.Child(c));
            }
            CoverageReport m = PatchCoverage.Validate(mixed);
            Assert.IsTrue(m.Valid, m.Problem);
            Assert.AreEqual(8, m.TransitionEdges, "4 children x 2 outer edges each face a coarser neighbor");

            var hole = new List<PlanetPatchId>(mixed);
            hole.Remove(refined.Child(2));
            Assert.IsFalse(PatchCoverage.Validate(hole).Complete, "A missing patch is detected");
            var overlap = new List<PlanetPatchId>(mixed) { refined };
            Assert.IsFalse(PatchCoverage.Validate(overlap).NonOverlapping, "An overlap is detected");
        }

        // D
        [Test]
        public void D_FaceBounds_PartitionExactly_AndContainTheirPoints()
        {
            foreach (PlanetPatchId id in AllPatches(3))
            {
                CubeSphereCoordinates.FaceBounds(id, out float aMin, out float bMin, out float aMax, out float bMax);
                Assert.Less(aMin, aMax);
                CubeSphereCoordinates.FaceBounds(id.Child(0), out float c0aMin, out float c0bMin, out float c0aMax, out _);
                CubeSphereCoordinates.FaceBounds(id.Child(3), out float c3aMin, out _, out float c3aMax, out float c3bMax);
                Assert.AreEqual(Bits(aMin), Bits(c0aMin));
                Assert.AreEqual(Bits(bMin), Bits(c0bMin));
                Assert.AreEqual(Bits(aMax), Bits(c3aMax));
                Assert.AreEqual(Bits(bMax), Bits(c3bMax));
                Assert.AreEqual(Bits(c0aMax), Bits(c3aMin), "Children share their inner boundary exactly");
                Assert.AreEqual(id, CubeSphereCoordinates.PatchAt(CubeSphereCoordinates.CenterDirection(id), 3), "The center lies in its patch");
                int face = CubeSphereCoordinates.DirectionToFace(CubeSphereCoordinates.CenterDirection(id), out float a, out float b);
                Assert.AreEqual(id.Face, face);
                Assert.IsTrue(CubeSphereCoordinates.Contains(id, a, b));
            }
            var random = new System.Random(3);
            for (int i = 0; i < 2000; i++)
            {
                int level = random.Next(0, 19);
                int size = 1 << level;
                var id = new PlanetPatchId(random.Next(6), level, random.Next(size), random.Next(size));
                Assert.AreEqual(id, CubeSphereCoordinates.PatchAt(CubeSphereCoordinates.CenterDirection(id), level), id.ToString());
            }
        }

        // E
        [Test]
        public void E_Coordinates_ReuseTheExistingProjectionBitForBit()
        {
            foreach (int resolution in new[] { 16, 64, 256 })
            {
                for (int face = 0; face < 6; face++)
                {
                    for (int j = 0; j <= resolution; j += resolution / 8)
                    {
                        for (int i = 0; i <= resolution; i += resolution / 16)
                        {
                            Assert.IsTrue(SameBits(CubeSphereGenerator.GetSphereDirection(face, i, j, resolution),
                                CubeSphereCoordinates.LatticeDirection(face, i, j, resolution)), "Same direction as the planet mesh");
                        }
                    }
                }
            }
            var random = new System.Random(11);
            float worst = 0f;
            for (int i = 0; i < 20000; i++)
            {
                var d = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f);
                if (d.sqrMagnitude < 1e-4f)
                {
                    continue;
                }
                d.Normalize();
                int face = CubeSphereCoordinates.DirectionToFace(d, out float a, out float b);
                Assert.IsTrue(Mathf.Abs(a) <= 1f && Mathf.Abs(b) <= 1f);
                Vector3 back = CubeSphereCoordinates.FaceToDirection(face, a, b);
                worst = Mathf.Max(worst, (d - back).magnitude); // chord: Vector3.Angle's float acos cannot resolve below ~0.02 degrees
            }
            Assert.Less(worst, 1e-6f, "Inverse projection round trip (radians, chord on the unit sphere)");
            Assert.AreEqual(20, CubeSphereCoordinates.MaxLevelForResolution(16), "16 quads: exact lattice down to level 20");
            WorldArchiveTestUtil.Report("Inverse projection worst round-trip error: " + worst.ToString("0.0e0", CultureInfo.InvariantCulture) + " (unit-sphere chord)");
        }

        // ------------------------------------------------------------ F, G, H

        // F
        [Test]
        public void F_EqualLevelNeighbors_AreReciprocal_AndShareExactlyOneEdge()
        {
            for (int level = 0; level <= 5; level++)
            {
                int size = 1 << level;
                foreach (PlanetPatchId id in AllPatches(level))
                {
                    var corners = new HashSet<(int, int, int)>();
                    for (int c = 0; c < 4; c++)
                    {
                        Vector3 v = CubeSphereCoordinates.CornerDirection(id, c);
                        corners.Add((Bits(v.x), Bits(v.y), Bits(v.z)));
                    }
                    var neighbors = new HashSet<PlanetPatchId>();
                    for (int d = 0; d < 4; d++)
                    {
                        PlanetPatchId n = PatchNeighbors.Neighbor(id, (PatchDirection)d);
                        Assert.AreEqual(level, n.Level);
                        Assert.AreNotEqual(id, n);
                        Assert.IsTrue(neighbors.Add(n), id + ": four distinct neighbors");
                        PatchDirection back = PatchNeighbors.DirectionBack(id, n);
                        Assert.AreEqual(id, PatchNeighbors.Neighbor(n, back), "Reciprocal");
                        int shared = 0;
                        for (int c = 0; c < 4; c++)
                        {
                            Vector3 v = CubeSphereCoordinates.CornerDirection(n, c);
                            if (corners.Contains((Bits(v.x), Bits(v.y), Bits(v.z))))
                            {
                                shared++;
                            }
                        }
                        Assert.AreEqual(2, shared, id + " -> " + n + ": one shared edge, bit-identical corners");
                        if (n.Face == id.Face)
                        {
                            Assert.AreEqual(1, Math.Abs(n.X - id.X) + Math.Abs(n.Y - id.Y));
                        }
                    }
                    if (level == 0)
                    {
                        CollectionAssert.DoesNotContain(neighbors.Select(n => n.Face).ToList(), id.Face ^ 1, "The opposite face is never adjacent");
                    }
                    // Diagonal contact is not adjacency.
                    if (id.X + 1 < size && id.Y + 1 < size)
                    {
                        CollectionAssert.DoesNotContain(neighbors, new PlanetPatchId(id.Face, level, id.X + 1, id.Y + 1));
                    }
                }
            }

            // Cube corners: exactly three patches meet, pairwise adjacent.
            const int cornerLevel = 3;
            var byCorner = new Dictionary<(int, int, int), List<PlanetPatchId>>();
            foreach (PlanetPatchId id in AllPatches(cornerLevel))
            {
                for (int c = 0; c < 4; c++)
                {
                    Vector3 v = CubeSphereCoordinates.CornerDirection(id, c);
                    var key = (Bits(v.x), Bits(v.y), Bits(v.z));
                    if (!byCorner.TryGetValue(key, out List<PlanetPatchId> list))
                    {
                        byCorner[key] = list = new List<PlanetPatchId>();
                    }
                    list.Add(id);
                }
            }
            Assert.AreEqual(8, byCorner.Values.Count(l => l.Count == 3), "The cube's 8 corners: 3 patches each");
            Assert.IsTrue(byCorner.Values.All(l => l.Count == 3 || l.Count == 4), "Every other vertex: 4 patches");
            foreach (List<PlanetPatchId> trio in byCorner.Values.Where(l => l.Count == 3))
            {
                foreach (PlanetPatchId p in trio)
                {
                    var adjacent = Enumerable.Range(0, 4).Select(d => PatchNeighbors.Neighbor(p, (PatchDirection)d)).ToList();
                    Assert.AreEqual(2, trio.Count(q => adjacent.Contains(q)), "At a cube corner the three patches share edges pairwise");
                }
            }
        }

        // G
        [Test]
        public void G_CrossLevelNeighbors_ReturnTheLeavesAlongTheEdge_Reciprocally()
        {
            // A covering mixing levels 1..3 across face edges.
            var leaves = AllPatches(1).ToList();
            PlanetPatchId refined = new PlanetPatchId(0, 1, 1, 1); // +Y, touches +X and +Z
            leaves.Remove(refined);
            for (int c = 0; c < 4; c++)
            {
                leaves.Add(refined.Child(c));
            }
            PlanetPatchId deeper = refined.Child(3);
            leaves.Remove(deeper);
            for (int c = 0; c < 4; c++)
            {
                leaves.Add(deeper.Child(c));
            }
            var set = new HashSet<PlanetPatchId>(leaves);
            CoverageReport coverage = PatchCoverage.Validate(leaves);
            Assert.IsTrue(coverage.Complete && coverage.NonOverlapping, "A complete covering (deliberately not 2:1 balanced: neighbor search must not need it)");
            Assert.IsFalse(coverage.Balanced);
            var output = new List<PlanetPatchId>();
            foreach (PlanetPatchId leaf in leaves)
            {
                for (int d = 0; d < 4; d++)
                {
                    output.Clear();
                    PatchNeighbors.NeighborLeaves(leaf, (PatchDirection)d, set.Contains, output);
                    Assert.IsNotEmpty(output, leaf + " " + (PatchDirection)d + ": no missing neighbor");
                    Assert.IsTrue(output.All(set.Contains));
                    Assert.AreEqual(output.Count, output.Distinct().Count());
                    if (output.Count > 1)
                    {
                        Assert.IsTrue(output.All(n => n.Level > leaf.Level), "Several neighbors only when they are finer");
                    }
                    foreach (PlanetPatchId n in output)
                    {
                        bool back = false;
                        var reverse = new List<PlanetPatchId>();
                        for (int e = 0; e < 4 && !back; e++)
                        {
                            reverse.Clear();
                            PatchNeighbors.NeighborLeaves(n, (PatchDirection)e, set.Contains, reverse);
                            back = reverse.Contains(leaf);
                        }
                        Assert.IsTrue(back, n + " sees " + leaf + " back");
                    }
                }
            }
            // The +X face's level-1 patch next to the refined corner sees two finer leaves.
            PlanetPatchId east = PatchNeighbors.Neighbor(refined, PatchDirection.East);
            Assert.AreNotEqual(0, east.Face, "Across the face edge");
            output.Clear();
            PatchNeighbors.NeighborLeaves(east, PatchNeighbors.DirectionBack(refined, east), set.Contains, output);
            Assert.AreEqual(3, output.Count, "One level-2 child and two level-3 grandchildren along that edge");
        }

        // H
        [Test]
        public void H_SharedBoundaries_AreBitIdenticalAtEqualLevels_AndMeasuredAcrossLevels()
        {
            PlanetInstance planet = Planet(536452);
            var sampler = new TerrainElevationSampler(planet);
            const int r = 16;
            var builder = new PatchMeshBuilder(r);
            var cases = new List<PlanetPatchId>
            {
                new PlanetPatchId(0, 2, 3, 1), new PlanetPatchId(3, 4, 15, 15), new PlanetPatchId(4, 3, 0, 7), new PlanetPatchId(1, 6, 63, 0),
                new PlanetPatchId(2, 10, 511, 777),
            };
            foreach (PlanetPatchId id in cases)
            {
                for (int d = 0; d < 4; d++)
                {
                    PlanetPatchId n = PatchNeighbors.Neighbor(id, (PatchDirection)d);
                    PatchDirection back = PatchNeighbors.DirectionBack(id, n);
                    HashSet<(int, int, int)> mine = EdgePoints(builder, sampler, id, (PatchDirection)d, r);
                    HashSet<(int, int, int)> theirs = EdgePoints(builder, sampler, n, back, r);
                    Assert.IsTrue(mine.SetEquals(theirs), id + " / " + n + ": identical shared-edge positions (tolerance 0)");
                    Assert.AreEqual(r + 1, mine.Count);
                }
            }

            // Across levels: the fine edge's even vertices are the coarse edge's vertices; odd ones are T-junctions.
            PlanetPatchId coarse = new PlanetPatchId(4, 3, 2, 5);
            PlanetPatchId fineSide = PatchNeighbors.Neighbor(coarse, PatchDirection.North).Child(0);
            Assert.IsTrue(PatchNeighbors.TouchesAlongEdge(fineSide, coarse));
            PatchDirection toward = Enumerable.Range(0, 4).Select(e => (PatchDirection)e).First(e => coarse.Contains(PatchNeighbors.Neighbor(fineSide, e).Parent));
            HashSet<(int, int, int)> coarseEdge = EdgePoints(builder, sampler, coarse, PatchDirection.North, r);
            HashSet<(int, int, int)> fineEdge = EdgePoints(builder, sampler, fineSide, toward, r);
            int shared = fineEdge.Count(coarseEdge.Contains);
            Assert.AreEqual(r / 2 + 1, shared, "Every other fine vertex coincides exactly with a coarse vertex");
            float gap = PatchCoverage.TJunctionGap(fineSide, toward, r, sampler);
            Assert.Greater(gap, 0f, "Odd vertices leave a measurable gap: the documented crack contract for V0.2.0B");
            WorldArchiveTestUtil.Report("T-junction gap on a level-4 edge (seed 536452): " + gap.ToString("0.0000", CultureInfo.InvariantCulture)
                + " units (" + (gap / planet.Radius * 100f).ToString("0.000", CultureInfo.InvariantCulture) + "% of R)");
        }

        static HashSet<(int, int, int)> EdgePoints(PatchMeshBuilder builder, TerrainElevationSampler sampler, PlanetPatchId id, PatchDirection direction, int r)
        {
            builder.Build(id, sampler, Color.white);
            var points = new HashSet<(int, int, int)>();
            for (int k = 0; k <= r; k++)
            {
                PatchNeighbors.EdgeVertex(direction, k, r, out int i, out int j);
                Vector3 d = builder.Directions[i + j * (r + 1)];
                Vector3 p = sampler.SurfacePoint(d);
                points.Add((Bits(p.x), Bits(p.y), Bits(p.z)));
            }
            return points;
        }

        // ------------------------------------------------------------ I, J

        // I
        [Test]
        public void I_PatchSampling_IsTheExistingElevationField()
        {
            PlanetInstance planet = Planet(868497);
            var sampler = new TerrainElevationSampler(planet);
            int resolution = planet.Definition.resolution; // 64: level 2 patches of 16 quads land on the mesh lattice
            const int r = 16;
            var builder = new PatchMeshBuilder(r);
            foreach (PlanetPatchId id in AllPatches(2))
            {
                PatchGeometry g = builder.Build(id, sampler, Color.white);
                for (int j = 0; j <= r; j++)
                {
                    for (int i = 0; i <= r; i++)
                    {
                        Vector3 meshDirection = CubeSphereGenerator.GetSphereDirection(id.Face, id.X * r + i, id.Y * r + j, resolution);
                        Vector3 adaptive = builder.Directions[i + j * (r + 1)];
                        Assert.IsTrue(SameBits(meshDirection, adaptive));
                        float elevation = planet.Height.GetElevation(meshDirection);
                        Assert.AreEqual(Bits(elevation), Bits(sampler.Elevation(adaptive)), "Same elevation, bit for bit");
                        Vector3 meshVertex = meshDirection * (planet.Radius * (1f + elevation)); // PlanetMeshBuilder's expression
                        Assert.IsTrue(SameBits(meshVertex, sampler.SurfacePoint(adaptive)));
                        // Stored relative to the patch origin: within one float ulp of the radius.
                        Vector3 rebuilt = g.Origin + builder.Vertices[i + j * (r + 1)];
                        Assert.Less((rebuilt - meshVertex).magnitude, planet.Radius * 3e-7f);
                    }
                }
            }
        }

        // J
        [Test]
        public void J_PatchMeshes_HaveValidIndicesOutwardWindingAndBounds()
        {
            PlanetInstance planet = Planet(609538);
            var sampler = new TerrainElevationSampler(planet);
            foreach (int r in new[] { 2, 16, 33 })
            {
                var builder = new PatchMeshBuilder(r);
                Assert.AreEqual((r + 1) * (r + 1), builder.VertexCount);
                Assert.AreEqual(r * r * 6, builder.IndexCount);
                foreach (PlanetPatchId id in new[] { PlanetPatchId.Root(0), new PlanetPatchId(2, 4, 3, 9), new PlanetPatchId(5, 12, 4000, 17) })
                {
                    PatchGeometry g = builder.Build(id, sampler, Color.gray);
                    Assert.AreEqual(id, g.Id);
                    Vector3 min = g.LocalBounds.min, max = g.LocalBounds.max;
                    float slack = planet.Radius * 1e-6f;
                    for (int v = 0; v < builder.VertexCount; v++)
                    {
                        Vector3 p = builder.Vertices[v];
                        Assert.IsTrue(p.x >= min.x - slack && p.y >= min.y - slack && p.z >= min.z - slack
                            && p.x <= max.x + slack && p.y <= max.y + slack && p.z <= max.z + slack, "Bounds contain every vertex");
                    }
                    int[] t = builder.Triangles;
                    for (int k = 0; k < builder.IndexCount; k += 3)
                    {
                        Assert.IsTrue(t[k] >= 0 && t[k] < builder.VertexCount && t[k + 1] < builder.VertexCount && t[k + 2] < builder.VertexCount);
                        Vector3 a = builder.Vertices[t[k]], b = builder.Vertices[t[k + 1]], c = builder.Vertices[t[k + 2]];
                        // The planet faces' convention: Cross(axisA, axisB) == normal makes Cross(b - a, c - a) point outward.
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        Vector3 outward = builder.Directions[t[k]];
                        Assert.Greater(Vector3.Dot(n, outward), 0f, id + ": outward-facing with the planet mesh's winding");
                    }
                    Assert.IsTrue(g.MinElevation <= g.MaxElevation);
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new PatchMeshBuilder(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PatchMeshBuilder(16).Build(new PlanetPatchId(0, 21, 0, 0), sampler, Color.white),
                "Beyond the exact lattice depth");
        }

        // ------------------------------------------------------------ K - O

        static AdaptiveTerrainSettings Settings(int budget = 512, float error = 4f, float hysteresis = 0.25f) =>
            new AdaptiveTerrainSettings { maxLeafPatches = budget, maxScreenError = error, hysteresis = hysteresis };

        // K
        [Test]
        public void K_Selection_IsDeterministic()
        {
            PlanetInstance planet = Planet(1337);
            var sampler = new TerrainElevationSampler(planet);
            foreach (float altitude in new[] { 7f, 1f, 0.05f, 0.001f })
            {
                TerrainLodView view = View(altitude, planet);
                List<PlanetPatchId> a = new TerrainLodSelector(Settings()).Select(sampler, view).Leaves.ToList();
                List<PlanetPatchId> b = new TerrainLodSelector(Settings()).Select(sampler, view).Leaves.ToList();
                CollectionAssert.AreEqual(a, b, "Same inputs, same covering (altitude " + altitude + " R)");
                var selector = new TerrainLodSelector(Settings());
                selector.Select(sampler, view);
                CollectionAssert.AreEqual(a, selector.Select(sampler, view).Leaves.ToList(), "Stable when repeated");
                Assert.IsTrue(PatchCoverage.Validate(a).Valid);
            }
        }

        // L
        [Test]
        public void L_Refinement_FollowsCameraProximity_AndNeverReachesBehindThePlanet()
        {
            PlanetInstance planet = Planet(1337);
            var sampler = new TerrainElevationSampler(planet);
            int previousMax = -1, previousCount = 0;
            foreach (float altitude in new[] { 20f, 7f, 1f, 0.2f, 0.02f, 0.002f })
            {
                TerrainLodSelection s = new TerrainLodSelector(Settings(4096)).Select(sampler, View(altitude, planet));
                Assert.GreaterOrEqual(s.MaxLevel, previousMax, "Closer: at least as fine");
                Assert.GreaterOrEqual(s.Count, previousCount);
                previousMax = s.MaxLevel;
                previousCount = s.Count;
            }
            Assert.GreaterOrEqual(previousMax, 8, "Near the ground the covering is deep");

            TerrainLodView near = View(0.01f, planet, Vector3.up);
            TerrainLodSelection selection = new TerrainLodSelector(Settings(4096)).Select(sampler, near);
            PlanetPatchId under = CubeSphereCoordinates.PatchAt(Vector3.up, 0);
            selection.TryGetCoveringLeaf(CubeSphereCoordinates.PatchAt(Vector3.up, selection.MaxLevel), out PlanetPatchId belowCamera);
            Assert.AreEqual(selection.MaxLevel, belowCamera.Level, "Finest under the camera");
            selection.TryGetCoveringLeaf(CubeSphereCoordinates.PatchAt(Vector3.down, 6), out PlanetPatchId antipode);
            Assert.LessOrEqual(antipode.Level, 2, "Behind the planet stays coarse (only 2:1 balance reaches there)");
            Assert.AreEqual(0, under.Face);
        }

        // M
        [Test]
        public void M_Hysteresis_PreventsOscillationAtThresholds()
        {
            PlanetInstance planet = Planet(536452);
            var sampler = new TerrainElevationSampler(planet);
            // Free refinement (budget not reached) and budget-limited refinement.
            foreach ((float altitude, int budget) in new[] { (3f, 8192), (0.3f, 512) })
            {
                int flipsWith = CountChanges(sampler, planet, 0.25f, altitude, budget), flipsWithout = CountChanges(sampler, planet, 0f, altitude, budget);
                Assert.LessOrEqual(flipsWith, 1, "With hysteresis: settles after the first move (altitude " + altitude + " R, budget " + budget + ")");
                Assert.GreaterOrEqual(flipsWithout, flipsWith);
                WorldArchiveTestUtil.Report("Covering changes over 40 frames jittering 2% around " + altitude + " R (budget " + budget + "): "
                    + flipsWithout + " without hysteresis, " + flipsWith + " with 25%");
            }
        }

        static int CountChanges(TerrainElevationSampler sampler, PlanetInstance planet, float hysteresis, float altitude, int budget)
        {
            var selector = new TerrainLodSelector(Settings(budget, 4f, hysteresis));
            selector.Select(sampler, View(altitude, planet));
            int version = selector.Current.Version, changes = 0;
            for (int frame = 0; frame < 40; frame++)
            {
                selector.Select(sampler, View(altitude * (frame % 2 == 0 ? 1.02f : 0.98f), planet));
                if (selector.Current.Version != version)
                {
                    changes++;
                    version = selector.Current.Version;
                }
            }
            return changes;
        }

        // N, O
        [Test]
        public void NO_BudgetIsRespected_AndTheCoveringStaysCompleteAndBalanced()
        {
            PlanetInstance planet = Planet(868497);
            var sampler = new TerrainElevationSampler(planet);
            foreach (int budget in new[] { 6, 7, 9, 24, 64, 200, 512, 2048 })
            {
                foreach (float altitude in new[] { 3f, 0.1f, 0.0005f })
                {
                    TerrainLodSelection s = new TerrainLodSelector(Settings(budget)).Select(sampler, View(altitude, planet));
                    Assert.LessOrEqual(s.Count, budget, "N: budget " + budget);
                    CoverageReport report = PatchCoverage.Validate(s.Leaves.ToList());
                    Assert.IsTrue(report.Valid, "O: budget " + budget + ", altitude " + altitude + ": " + report.Problem);
                    if (budget <= 64 && altitude < 0.2f)
                    {
                        Assert.IsTrue(s.BudgetLimited, "Reported");
                    }
                }
            }
            // A minimum level that does not fit the budget is lowered, never overflowing.
            var tight = new AdaptiveTerrainSettings { minLevel = 4, maxLeafPatches = 100 };
            Assert.AreEqual(2, tight.EffectiveMinLevel, "6 x 16 = 96 patches fit, 6 x 64 do not");
            TerrainLodSelection t = new TerrainLodSelector(tight).Select(sampler, View(5f, planet));
            Assert.LessOrEqual(t.Count, 100);
            Assert.IsTrue(PatchCoverage.Validate(t.Leaves.ToList()).Valid);
        }

        // ------------------------------------------------------------ T

        // T
        [Test]
        public void T_PerformanceAndAllocations_ReferenceSeeds()
        {
            var lines = new List<string> { "seed | camera | leaves | max level | budget-limited | select ms | steady-state alloc bytes | build ms/patch | vertices | mesh MB" };
            var builder = new PatchMeshBuilder(16);
            foreach (int seed in WorldArchiveTestUtil.ReferenceSeeds)
            {
                PlanetInstance planet = Planet(seed);
                var sampler = new TerrainElevationSampler(planet);
                foreach ((string name, float altitude) in new[] { ("far 7R", 7f), ("medium 1R", 1f), ("near 0.0005R", 0.0005f) })
                {
                    var selector = new TerrainLodSelector(Settings());
                    TerrainLodView view = View(altitude, planet);
                    for (int warm = 0; warm < 5; warm++)
                    {
                        selector.Select(sampler, view); // warm-up: buffers grow once, the hysteresis history settles
                    }
                    const int runs = 20;
                    TerrainLodSelection s = null;
                    long start = Stopwatch.GetTimestamp();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < runs; i++)
                    {
                        s = selector.Select(sampler, view);
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    double selectMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / runs;
                    var watch = Stopwatch.StartNew();
                    int built = Math.Min(16, s.Count);
                    for (int i = 0; i < built; i++)
                    {
                        builder.Build(s.Leaves[i * s.Count / built], sampler, Color.white);
                    }
                    double buildMs = watch.Elapsed.TotalMilliseconds / built;
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3} | {4} | {5:0.00} | {6} | {7:0.00} | {8:N0} | {9:0.0}",
                        seed, name, s.Count, s.MaxLevel, s.BudgetLimited, selectMs, allocated / runs, buildMs,
                        s.Count * builder.VertexCount, s.Count * builder.EstimatedMeshBytes / (1024.0 * 1024.0)));
                    Assert.AreEqual(0, allocated, "No managed allocation in steady-state selection (seed " + seed + ", " + name + ")");
                    Assert.Less(selectMs, 50.0);
                }
            }
            WorldArchiveTestUtil.Report(string.Join("\n", lines));
        }
    }
}
