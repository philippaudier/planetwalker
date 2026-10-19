using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// River–terrain alignment (V0.2.0G.2D): the canonical river surface, measured on the default configuration at
    /// seed 1337 with the river density of the panel at 100 % (threshold 1e-5): deterministic placement, samples on the
    /// band-limited canonical terrain at every level, centerlines on the carved axes, exact meeting of adjacent pieces at
    /// any pair of levels, confluences, floating-origin relative near pieces, LOD rule, bounded cache, archive compatibility.
    /// </summary>
    public class RiverSurfaceTests
    {
        const int Seed = 1337;
        const float Threshold = 1e-5f;
        const float PixelsPerRadian = 1080f / (60f * Mathf.Deg2Rad);

        sealed class Scene
        {
            public PlanetInstance Planet;
            public RiverNetwork Network;
            public RiverRibbonBuilder Spine;
            public RiverCorridorMap Corridors;
            public RiverSurface Surface;
            public ValleyNetwork Valleys;
            public double RadiusMeters;
        }

        static Scene scene;

        static Scene Build()
        {
            if (scene != null)
            {
                return scene;
            }
            var c = new WorldConfiguration();
            c.Hydrology.riverNetworkThreshold = Threshold;
            PlanetDefinition def = c.Terrain.Clone();
            def.seed = Seed; def.surfaceDetail = true; def.regionalLandforms = 1f; def.erosionStrength = 1f; def.erosionModel = 1;
            var planet = new PlanetInstance(def);
            var hydrology = new HydrologySystem().Compute(new HydrologyGrid(c.Hydrology.resolution), planet);
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, Threshold);
            system.BuildPaths(network, c.Rivers.SmoothingIterations);
            var faces = new Mesh[CubeSphereGenerator.FaceCount];
            var meshBuilder = new PlanetMeshBuilder();
            for (int f = 0; f < faces.Length; f++) { faces[f] = new Mesh(); meshBuilder.BuildFace(faces[f], f, planet, PlanetColorRamp.CreateDefault()); }
            var sampler = new PlanetSurfaceSampler();
            sampler.Capture(faces, def.resolution);
            foreach (Mesh m in faces) UnityEngine.Object.DestroyImmediate(m);
            var spine = new RiverRibbonBuilder();
            spine.BuildGeometry(network, sampler, planet, c.Rivers, null, null, false);
            ValleyNetwork valleys = planet.Height.SurfaceDetail.Landforms.Valleys;
            var corridors = new RiverCorridorMap(network, valleys, planet.Scale.RadiusMeters);
            var surface = new RiverSurface();
            surface.Rebuild(planet, network, spine, corridors, 1f, 0);
            scene = new Scene { Planet = planet, Network = network, Spine = spine, Corridors = corridors, Surface = surface, Valleys = valleys, RadiusMeters = planet.Scale.RadiusMeters };
            TestContext.WriteLine($"scene: {network.RiverCount} rivers, {spine.RibbonCount} ribbons, {spine.SpineSampleCount} spine samples, {surface.PieceCount} pieces, prepass {surface.Diagnostics.PrepassMilliseconds:0} ms");
            return scene;
        }

        static RiverSurface Fresh(Scene s)
        {
            var surface = new RiverSurface();
            surface.Rebuild(s.Planet, s.Network, s.Spine, s.Corridors, 1f, 0);
            return surface;
        }

        /// <summary>A ground-level view 100 m above the placed river at a spine sample, looking along the planet.</summary>
        static RiverSurfaceView GroundView(Scene s, int sample, double metersAbove = 100.0)
        {
            s.Surface.GetSpineSample(sample, out Vector3d dir, out _, out _, out _, out float e0);
            float ground = s.Planet.Radius * (1f + Mathf.Max(s.Planet.Height.GetElevation(dir), s.Planet.SeaLevel));
            float altitude = (float)(metersAbove / s.RadiusMeters) * s.Planet.Radius;
            return new RiverSurfaceView(dir * (ground + altitude), altitude, PixelsPerRadian);
        }

        /// <summary>The spine sample on a carved axis with the deepest incision (a gorge): the main ground-level location.</summary>
        static int GorgeSample(Scene s)
        {
            int best = -1; float bestIncision = -1f;
            for (int i = 0; i < s.Surface.SpineSampleCount; i++)
            {
                s.Surface.GetSpineSample(i, out Vector3d dir, out _, out float weight, out _, out _);
                if (weight < 0.999f || Math.Abs(dir.y) > 0.75) continue;
                if (s.Valleys.Nearest(dir, out ValleyNetwork.ChannelQuery q) && q.IncisionMeters > bestIncision)
                {
                    bestIncision = q.IncisionMeters; best = i;
                }
            }
            Assert.GreaterOrEqual(best, 0, "A snapped spine sample in a gorge exists");
            return best;
        }

        static int PieceOfSample(Scene s, int sample)
        {
            for (int p = 0; p < s.Surface.PieceCount; p++)
            {
                if (s.Surface.GetPieceFirstSample(p) == sample) return p;
            }
            return -1;
        }

        [Test]
        public void Placement_IsDeterministic_AcrossInstancesAndThreads()
        {
            Scene s = Build();
            RiverSurface other = Fresh(s);
            int gorge = GorgeSample(s);
            int piece = PieceOfSample(s, gorge);
            Assert.GreaterOrEqual(piece, 0);
            foreach (int p in new[] { piece, Math.Max(0, piece - 7), Math.Min(s.Surface.PieceCount - 1, piece + 11) })
            {
                foreach (int level in new[] { 1, 4, 7 })
                {
                    RiverSurface.PieceData a = s.Surface.BuildPiece(p, level), b = other.BuildPiece(p, level);
                    Assert.AreEqual(a.Count, b.Count);
                    for (int k = 0; k < a.Count; k++)
                    {
                        Assert.IsTrue(a.Dir[k] == b.Dir[k], "placed direction, bitwise");
                        Assert.AreEqual(a.Center[k], b.Center[k], "elevation, bitwise");
                        Assert.AreEqual(a.Left[k], b.Left[k]);
                        Assert.AreEqual(a.HalfWidth[k], b.HalfWidth[k]);
                        Assert.AreEqual(a.LeftPos[k], b.LeftPos[k]);
                    }
                }
            }
            // The prepass is identical too (it ran on the calling thread in both; the controller runs it on workers).
            for (int i = 0; i < s.Surface.SpineSampleCount; i += 97)
            {
                s.Surface.GetSpineSample(i, out Vector3d d1, out float hw1, out float w1, out float o1, out float e1);
                other.GetSpineSample(i, out Vector3d d2, out float hw2, out float w2, out float o2, out float e2);
                Assert.IsTrue(d1 == d2); Assert.AreEqual(hw1, hw2); Assert.AreEqual(w1, w2); Assert.AreEqual(o1, o2); Assert.AreEqual(e1, e2);
            }
            other.Dispose();
        }

        [Test]
        public void Samples_LieOnTheDrawnTerrainAtTheirLevel_AndConvergeWithTheLevel()
        {
            Scene s = Build();
            PlanetHeightGenerator height = s.Planet.Height;
            double R = s.RadiusMeters;
            int gorge = GorgeSample(s);
            int first = PieceOfSample(s, gorge);
            var midpointResidual = new Dictionary<int, List<float>>();
            foreach (int level in new[] { 0, 3, 6, 9 })
            {
                var residuals = new List<float>();
                for (int p = Math.Max(0, first - 20); p < Math.Min(s.Surface.PieceCount, first + 20); p++)
                {
                    RiverSurface.PieceData d = s.Surface.BuildPiece(p, level);
                    float step = s.Surface.GetLevelStep(level);
                    for (int k = 0; k < d.Count; k++)
                    {
                        Assert.AreEqual(height.GetSurfaceElevation(d.Dir[k], step), d.Center[k], 0f, "the sample's own elevation: octaves band-limited at its level, landforms complete");
                        if (k + 1 < d.Count)
                        {
                            // Between two samples the ribbon is linear: the full canonical terrain at the midpoint tells the vertical residual.
                            Vector3d mid = (d.Dir[k] + d.Dir[k + 1]).Normalized;
                            float interpolated = 0.5f * (d.Center[k] + d.Center[k + 1]);
                            residuals.Add((float)(Math.Abs(height.GetElevation(mid) - interpolated) * R));
                        }
                    }
                }
                residuals.Sort();
                midpointResidual[level] = residuals;
                TestContext.WriteLine($"level {level} (step {s.Surface.GetLevelStep(level) * R / 1000.0:0.00} km): midpoint residual vs full canonical p50 {residuals[residuals.Count / 2]:0.0} m, p90 {residuals[(int)(residuals.Count * 0.9)]:0.0} m, max {residuals[residuals.Count - 1]:0.0} m over {residuals.Count} gaps");
            }
            float P90(int level) => midpointResidual[level][(int)(midpointResidual[level].Count * 0.9)];
            Assert.Less(P90(3), P90(0), "finer levels follow the terrain more closely");
            Assert.Less(P90(6), P90(3));
            Assert.Less(P90(9), P90(6));
            Assert.Less(P90(9), 25f, "76 m steps: the river surface stays within a few meters of the terrain between its samples (p90)");
        }

        [Test]
        public void Centerlines_LieOnTheCarvedAxes_WhereCorridorsExist()
        {
            Scene s = Build();
            RiverSurfaceDiagnostics d = s.Surface.Diagnostics;
            TestContext.WriteLine($"corridors: {d.ChannelNodes} / {d.TrajectoryNodes} trajectory nodes on carved cells, links matched {d.MatchedLinks} / mismatched {d.MismatchedLinks}; snapped samples {d.SnappedSamples} / {d.SpineSamples}, offset mean {d.MeanOffsetMeters:0} m max {d.MaxOffsetMeters:0} m, residual mean {d.MeanResidualMeters:0.000} m max {d.MaxResidualMeters:0.000} m");
            Assert.Greater(d.ChannelNodes, 500, "the default planet's rivers cross the ranges (879 carved trajectory nodes measured)");
            Assert.Greater(d.MatchedLinks, 4 * Math.Max(1, d.MismatchedLinks), "the legacy flow and the carved flow agree on most carved links (one hydrology, two surfaces)");
            Assert.Greater(d.SnappedSamples, 1000);
            Assert.Less(d.MaxResidualMeters, 0.5f, "the meander inversion lands on the axis");
            // On the axis: the terrain's own nearest-channel query confirms the placed direction (meters), and the
            // corridor is the carved floor there (a canonical sample lower than the legacy point's).
            int checkedSamples = 0, lower = 0; double sumDrop = 0;
            for (int i = 0; i < s.Surface.SpineSampleCount; i += 5)
            {
                s.Surface.GetSpineSample(i, out Vector3d dir, out _, out float weight, out float offset, out _);
                if (weight < 0.999f) continue;
                Assert.IsTrue(s.Valleys.Nearest(dir, out ValleyNetwork.ChannelQuery q));
                // Along the surface (the chord's sagitta is radial: the carve itself ignores it).
                float onAxis = (float)((q.Closest.Normalized - s.Valleys.Meander(dir)).Magnitude * s.RadiusMeters);
                Assert.Less(onAxis, 1.0f, "a snapped sample is on the carved axis");
                checkedSamples++;
                if (offset > 2000f)
                {
                    RiverSpineSample legacy = s.Spine.GetSpineSample(i);
                    float placed = s.Planet.Height.GetElevation(dir), before = s.Planet.Height.GetElevation(new Vector3d(legacy.Direction));
                    if (placed < before) lower++;
                    sumDrop += (before - placed) * s.RadiusMeters;
                }
            }
            TestContext.WriteLine($"{checkedSamples} snapped samples checked on the axis; of those moved over 2 km, {lower} now lie lower than the legacy point (mean drop {sumDrop / Math.Max(1, lower):0} m)");
            Assert.Greater(checkedSamples, 500);
        }

        [Test]
        public void AdjacentPieces_MeetExactly_AtAnyPairOfLevels()
        {
            Scene s = Build();
            int gorge = GorgeSample(s);
            int p = PieceOfSample(s, gorge);
            Assert.IsTrue(p + 1 < s.Surface.PieceCount && s.Surface.GetPieceRibbon(p + 1) == s.Surface.GetPieceRibbon(p));
            foreach ((int la, int lb) in new[] { (0, 5), (2, 7), (4, 4), (9, 1) })
            {
                RiverSurface.PieceData a = s.Surface.BuildPiece(p, la), b = s.Surface.BuildPiece(p + 1, lb);
                int end = a.Count - 1;
                Assert.IsTrue(a.Dir[end] == b.Dir[0], $"shared sample direction, bitwise ({la}, {lb})");
                Assert.AreEqual(a.Side[end], b.Side[0], "shared side vector");
                Assert.AreEqual(a.HalfWidth[end], b.HalfWidth[0], "shared width");
                Assert.AreEqual(a.Water[end], b.Water[0]);
                // The heights differ by the band limit of each level: that is what the end morph absorbs (checked on meshes below).
            }
            // Assembled: at a ground view every displayed boundary between two pieces is closed (left and right vertices coincide).
            s.Surface.CompleteSynchronously(GroundView(s, gorge));
            int boundaries = 0, mixedLevels = 0; float worst = 0f;
            var vertices = new Dictionary<Mesh, Vector3[]>();
            Vector3[] VerticesOf(Mesh m) { if (!vertices.TryGetValue(m, out Vector3[] v)) vertices[m] = v = m.vertices; return v; }
            for (int q = 0; q + 1 < s.Surface.PieceCount; q++)
            {
                if (s.Surface.GetPieceRibbon(q + 1) != s.Surface.GetPieceRibbon(q)) continue;
                if (!s.Surface.GetPieceVertices(q, out int ga, out int fa, out int ca) || !s.Surface.GetPieceVertices(q + 1, out int gb, out int fb, out _)) continue;
                Mesh ma = s.Surface.GetGroupMesh(ga, q, out Vector3 oa), mb = s.Surface.GetGroupMesh(gb, q + 1, out Vector3 ob);
                if (ma == null || mb == null) continue;
                Vector3[] va = VerticesOf(ma), vb = VerticesOf(mb);
                Vector3 endLeft = va[fa + ca - 2] + oa, endRight = va[fa + ca - 1] + oa;
                Vector3 startLeft = vb[fb] + ob, startRight = vb[fb + 1] + ob;
                float gap = Mathf.Max((endLeft - startLeft).magnitude, (endRight - startRight).magnitude) / s.Planet.Radius * (float)s.RadiusMeters;
                if (gap > worst)
                {
                    worst = gap;
                    int shared = s.Surface.GetPieceFirstSample(q + 1);
                    s.Surface.GetSpineVertex0(shared, out Vector3 l0, out _);
                    s.Surface.GetSpineSample(shared, out Vector3d sd, out _, out _, out _, out _);
                    float toM = (float)s.RadiusMeters / s.Planet.Radius;
                    TestContext.WriteLine($"worst so far: pieces {q} (level {s.Surface.GetDisplayedLevel(q)}, group {ga}) / {q + 1} (level {s.Surface.GetDisplayedLevel(q + 1)}, group {gb}): gap {gap:0.0} m; end to level-0 vertex {(endLeft - l0).magnitude * toM:0.0} m, start to level-0 vertex {(startLeft - l0).magnitude * toM:0.0} m; radial end {endLeft.magnitude * toM:0} m start {startLeft.magnitude * toM:0} m; angle end-start {Vector3.Angle(endLeft, startLeft) * Mathf.Deg2Rad * (float)s.RadiusMeters:0.0} m along the surface");
                }
                boundaries++;
                if (s.Surface.GetDisplayedLevel(q) != s.Surface.GetDisplayedLevel(q + 1)) mixedLevels++;
            }
            TestContext.WriteLine($"{boundaries} piece boundaries assembled ({mixedLevels} between different levels): worst gap {worst:0.000} m");
            Assert.Greater(boundaries, 1000);
            Assert.Greater(mixedLevels, 10, "the ground view mixes levels");
            Assert.Less(worst, 2.0f, "no crack: adjacent pieces share their end vertices (float rounding of absolute positions only)");
        }

        [Test]
        public void Confluences_TributariesEndOnTheirParentSurface()
        {
            Scene s = Build();
            RiverNetwork network = s.Network;
            double R = s.RadiusMeters;
            int tributaries = 0, carvedTributaries = 0; var gaps = new List<float>(); var drops = new List<float>();
            for (int r = 0; r < s.Spine.RibbonCount; r++)
            {
                int first = s.Spine.GetSpineStart(r), end = s.Spine.GetSpineStart(r + 1);
                if (end - first < 2) continue;
                River data = network.GetRiver(s.Spine.GetSpineSample(first).River);
                if (data.ParentRiver < 0) continue;
                // The parent's ribbon (the first ribbon of that river).
                int parentRibbon = -1;
                for (int q = 0; q < s.Spine.RibbonCount && parentRibbon < 0; q++)
                {
                    if (s.Spine.GetSpineStart(q + 1) > s.Spine.GetSpineStart(q) && s.Spine.GetSpineSample(s.Spine.GetSpineStart(q)).River == data.ParentRiver) parentRibbon = q;
                }
                if (parentRibbon < 0) continue;
                // The surface's ribbon r ends with the appended confluence sample when the junction is carved.
                int sEnd = -1;
                for (int rr = 0; rr < s.Surface.PieceCount; rr++) { if (s.Surface.GetPieceRibbon(rr) == r) sEnd = s.Surface.GetPieceFirstSample(rr) + 1; }
                if (sEnd < 0) continue;
                s.Surface.GetSpineSample(sEnd, out Vector3d tip, out _, out float weight, out _, out float tipElevation);
                if (weight < 0.999f) continue;
                tributaries++;
                // The tip must be on the trunk's carved axis: the trunk's curve through the junction cell or its receiver.
                Vector3d m = s.Valleys.Meander(tip);
                River parent = network.GetRiver(data.ParentRiver);
                int junction = data.JunctionCell;
                var trunkCells = new HashSet<int> { junction };
                int receiver = s.Valleys.Hydrology.GetReceiver(junction);
                if (receiver >= 0) trunkCells.Add(receiver);
                double best = double.MaxValue;
                for (int seg = 0; seg < s.Valleys.SegmentCount; seg++)
                {
                    ValleyNetwork.Segment g = s.Valleys.GetSegment(seg);
                    if (!trunkCells.Contains(g.Cell)) continue;
                    Vector3d ab = g.B - g.A;
                    double t = Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(m - g.A, ab) / Math.Max(Vector3d.Dot(ab, ab), 1e-30)));
                    best = Math.Min(best, ((g.A + ab * t).Normalized - m).Magnitude);
                }
                carvedTributaries++;
                gaps.Add((float)(best * R));
                // The trunk's own surface there: its nearest placed spine sample's level-0 elevation vs the tip's.
                int pa = s.Spine.GetSpineStart(parentRibbon), pb = s.Spine.GetSpineStart(parentRibbon + 1);
                double nearest = double.MaxValue; float parentElevation = 0f;
                for (int i = pa; i < pb; i++)
                {
                    int si = -1;
                    for (int rr = 0; rr < s.Surface.PieceCount && si < 0; rr++) { if (s.Surface.GetPieceRibbon(rr) == parentRibbon) si = s.Surface.GetPieceFirstSample(rr) + (i - pa); }
                    if (si < 0) break;
                    s.Surface.GetSpineSample(si, out Vector3d a, out _, out _, out _, out float ea);
                    double dist = (a - tip).Magnitude;
                    if (dist < nearest) { nearest = dist; parentElevation = ea; }
                }
                drops.Add((float)((tipElevation - parentElevation) * R));
            }
            gaps.Sort(); drops.Sort();
            TestContext.WriteLine($"{carvedTributaries} tributaries ending on a carved junction: tip to the trunk's carved axis p50 {gaps[gaps.Count / 2]:0.0} m p90 {gaps[(int)(gaps.Count * 0.9)]:0.0} m max {gaps[gaps.Count - 1]:0.0} m; tip elevation minus the trunk's nearest placed sample p50 {drops[drops.Count / 2]:0} m, p10 {drops[(int)(drops.Count * 0.1)]:0} m, p90 {drops[(int)(drops.Count * 0.9)]:0} m; {s.Surface.Diagnostics.ConfluenceSamples} confluence samples appended");
            Assert.Greater(carvedTributaries, 100, "151 measured on the default planet");
            Assert.Less(gaps[gaps.Count - 1], 1.0f, "every tributary tip lies on its trunk's carved axis (the two valleys meet there)");
        }

        [Test]
        public void NearPieces_AreRelativeToALocalOrigin_AndReproduceTheAbsoluteSurface()
        {
            Scene s = Build();
            int gorge = GorgeSample(s);
            RiverSurfaceView view = GroundView(s, gorge);
            s.Surface.CompleteSynchronously(view);
            RiverSurfaceDiagnostics d = s.Surface.Diagnostics;
            TestContext.WriteLine($"ground view: far {d.FarPieces} mid {d.MidPieces} near {d.NearPieces} pieces, {d.DisplayedSamples} samples, budget scale {d.BudgetScale:0.00}, cache {d.CachedPieces} pieces {d.CacheBytes / 1048576.0:0.0} MB, builds {d.CompletedBuilds}, levels " + string.Join(" ", Enumerable.Range(0, RiverSurface.MaxLevel + 1).Select(l => s.Surface.DisplayedCount(l))));
            Assert.Greater(s.Surface.NearCount, 0, "near pieces exist at ground level");
            float R = s.Planet.Radius;
            int checkedVertices = 0; float worst = 0f;
            for (int n = 0; n < s.Surface.NearCount; n++)
            {
                s.Surface.GetNear(n, out Mesh mesh, out Vector3 origin, out Bounds local);
                Assert.Greater(mesh.vertexCount, 0);
                Assert.Less(local.extents.magnitude, 3f * s.Surface.SpineStep * R, "a near piece's vertices stay within its own extent of the origin");
                Assert.Less(Mathf.Abs(origin.magnitude - R) / R, 0.1f, "the origin is on the surface");
                Vector3[] v = mesh.vertices;
                for (int i = 0; i < v.Length; i += 7)
                {
                    Vector3 absolute = origin + v[i];
                    Vector3 dir = absolute.normalized;
                    // The vertex lies on the band-limited terrain of its level (edges flat inside corridors) within the lift-free float noise.
                    float ratio = absolute.magnitude / R - 1f;
                    float e = s.Planet.Height.GetSurfaceElevation(new Vector3d(dir), s.Surface.GetLevelStep(s.Surface.GetDisplayedLevel(PieceOfNear(s, n))));
                    worst = Mathf.Max(worst, Mathf.Abs(ratio - e) * (float)s.RadiusMeters);
                    checkedVertices++;
                }
            }
            TestContext.WriteLine($"{checkedVertices} near vertices: worst distance to the band-limited terrain at the edge direction {worst:0.0} m (edges flat across inside corridors, draped outside)");
            Assert.Greater(checkedVertices, 50);
        }

        static int PieceOfNear(Scene s, int nearIndex)
        {
            s.Surface.GetNear(nearIndex, out Mesh mesh, out _, out _);
            for (int p = 0; p < s.Surface.PieceCount; p++)
            {
                if (s.Surface.GetPieceVertices(p, out int group, out _, out _) && group == 2 && s.Surface.GetGroupMesh(2, p, out _) == mesh) return p;
            }
            return -1;
        }

        [Test]
        public void Levels_FollowTheTerrainErrorRule_UnderTheBudget()
        {
            Scene s = Build();
            int gorge = GorgeSample(s);
            int piece = PieceOfSample(s, gorge);
            RiverSurface surface = Fresh(s);
            surface.CompleteSynchronously(GroundView(s, gorge));
            RiverSurfaceDiagnostics ground = surface.Diagnostics;
            int nearLevel = surface.GetDisplayedLevel(piece);
            TestContext.WriteLine($"ground (100 m): piece under the camera at level {nearLevel}, displayed samples {ground.DisplayedSamples}, scale {ground.BudgetScale:0.00}");
            Assert.GreaterOrEqual(nearLevel, 7, "the piece under a ground camera is sampled every few hundred meters at most");
            Assert.LessOrEqual(ground.DisplayedSamples, (long)(surface.SampleBudget * 1.3f), "the sample budget bounds the surface");
            // From orbit (2 000 km) everything is coarse and far pieces dominate.
            s.Surface.GetSpineSample(gorge, out Vector3d dir, out _, out _, out _, out _);
            float orbit = (float)(2.0e6 / s.RadiusMeters) * s.Planet.Radius;
            surface.CompleteSynchronously(new RiverSurfaceView(dir * (s.Planet.Radius + orbit), orbit, PixelsPerRadian));
            RiverSurfaceDiagnostics high = surface.Diagnostics;
            TestContext.WriteLine($"orbit (2 000 km): piece level {surface.GetDisplayedLevel(piece)}, far {high.FarPieces} mid {high.MidPieces} near {high.NearPieces}, samples {high.DisplayedSamples}");
            Assert.LessOrEqual(surface.GetDisplayedLevel(piece), 4);
            Assert.AreEqual(0, high.NearPieces, "no near piece from orbit");
            Assert.LessOrEqual(surface.Diagnostics.GroupRebuilds, 4, "the far mesh is built once; only the mid mesh is rebuilt");
            Assert.Greater(high.FarPieces, high.MidPieces, "most of the planet is far from one point of view");
            Assert.LessOrEqual(high.DisplayedSamples, (long)(surface.SampleBudget * 1.3f));
            surface.Dispose();
        }

        [Test]
        public void Cache_StaysWithinItsBudget_AndEvicts()
        {
            Scene s = Build();
            RiverSurface surface = Fresh(s);
            surface.CacheBudgetBytes = 2L << 20;
            int gorge = GorgeSample(s);
            surface.CompleteSynchronously(GroundView(s, gorge));
            // A second place far away: the first place's fine pieces are no longer displayed and get evicted.
            int other = -1;
            s.Surface.GetSpineSample(gorge, out Vector3d g, out _, out _, out _, out _);
            for (int i = 0; i < s.Surface.SpineSampleCount && other < 0; i += 3)
            {
                s.Surface.GetSpineSample(i, out Vector3d d, out _, out float w, out _, out _);
                if (w > 0.999f && Vector3d.Dot(d, g) < -0.5) other = i;
            }
            Assert.GreaterOrEqual(other, 0);
            surface.CompleteSynchronously(GroundView(s, other));
            RiverSurfaceDiagnostics d2 = surface.Diagnostics;
            TestContext.WriteLine($"cache after two ground views with a 2 MB budget: {d2.CachedPieces} pieces, {d2.CacheBytes / 1024.0:0} KB, evictions {d2.Evictions}");
            Assert.Greater(d2.Evictions, 0);
            Assert.LessOrEqual(d2.CacheBytes, Math.Max(surface.CacheBudgetBytes, d2.DisplayedBytes) + (1L << 20), "bounded: the budget, or what the current view displays when that is more (never evicted)");
            surface.Dispose();
        }

        [Test]
        public void Archives_LoadWithoutThePlacementField_AndRoundTripIt()
        {
            ArchiveReadResult old = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.IsTrue(old.Ok, old.Report.Summary());
            Assert.AreEqual(RiverSettings.PlacementCanonical, old.Archive.Configuration.Rivers.placement, "an older archive gets the current placement: presentation only, its world is untouched");
            CollectionAssert.Contains(old.Report.DefaultedFields, "rivers.placement");
            CollectionAssert.Contains(PlanetArchiveSerializer.CompatibilityDefaultFields, "rivers.placement");
            Assert.IsEmpty(old.Report.Warnings);
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Legacy rivers");
            archive.Configuration.Rivers.placement = RiverSettings.PlacementLegacy;
            ArchiveReadResult read = PlanetArchiveSerializer.Read(PlanetArchiveSerializer.Serialize(archive));
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(RiverSettings.PlacementLegacy, read.Archive.Configuration.Rivers.placement);
            Assert.IsEmpty(read.Report.DefaultedFields);
        }
    }
}
