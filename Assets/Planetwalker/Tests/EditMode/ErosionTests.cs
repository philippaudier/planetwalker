using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Erosion foundation (V0.2.0G.2A, model None; since V0.2.0G.2B the model a G.2A world archived, and the fluvial model is
    /// FluvialIncisionTests): the stage is in the canonical evaluation path, its displacement is exactly zero,
    /// the G.1.1 terrain is bit-identical with it on or off, it is deterministic and independent of LOD paths and of the
    /// floating origin, archives before G.2A load with it off, and its cost is measured.
    /// </summary>
    public class ErosionTests
    {
        const int Seed = 1337;
        static readonly Dictionary<(int, float), PlanetInstance> Planets = new Dictionary<(int, float), PlanetInstance>();
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created) if (o != null) Object.DestroyImmediate(o);
            created.Clear();
        }

        static PlanetInstance Planet(float erosion = 1f, int seed = Seed)
        {
            if (!Planets.TryGetValue((seed, erosion), out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, erosionStrength = erosion, erosionModel = (int)ErosionModel.None });
                Planets[(seed, erosion)] = planet;
            }
            return planet;
        }

        static IEnumerable<Vector3d> Spiral(int count)
        {
            for (int k = 0; k < count; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                yield return new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
            }
        }

        /// <summary>Representative directions: peaks, slopes, valley floors, plateaus, escarpments, coastal plains, shores, face edges.</summary>
        static List<(string kind, Vector3d d)> Representative(PlanetInstance planet)
        {
            var best = new Dictionary<string, (float score, Vector3d d)>();
            void Consider(string kind, float score, Vector3d d)
            {
                if (!best.TryGetValue(kind, out var b) || score > b.score) best[kind] = (score, d);
            }
            double R = planet.Scale.RadiusMeters;
            foreach (Vector3d d in Spiral(40000))
            {
                float e0 = planet.Height.SampleBase(d.ToVector3(), out TerrainBaseSample ctx);
                float above = e0 - planet.SeaLevel;
                if (above <= 0f) continue;
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                LandformSample l = s.Landforms;
                Consider("peak", ctx.Core * (float)above, d);
                Consider("slope", ctx.Influence * planet.Height.SurfaceDetail.Relief.Sample(d.ToVector3()), d);
                Consider("valley floor", l.Corridor, d);
                Consider("plateau", l.Plateau * (1f - l.Edge), d);
                Consider("escarpment", l.Edge * l.Plateau, d);
                Consider("coastal plain", l.CoastalPlain * (float)(above * R < 300.0 ? 1.0 : 0.0), d);
                Consider("shore", (float)(1.0 / (1.0 + above * R)), d);
            }
            var list = new List<(string, Vector3d)>();
            foreach (var pair in best) list.Add((pair.Key, pair.Value.d));
            // Cube-face edges and a corner.
            list.Add(("face edge", new Vector3d(1, 1, 0.02).Normalized));
            list.Add(("face edge", new Vector3d(0.3, -1, -1).Normalized));
            list.Add(("face corner", new Vector3d(1, 1, 1).Normalized));
            return list;
        }

        // A
        [Test]
        public void A_TheErosionDisplacement_IsExactlyZero_Everywhere()
        {
            PlanetInstance planet = Planet();
            ErosionSampler erosion = planet.Height.SurfaceDetail.Erosion;
            Assert.IsNotNull(erosion, "The stage is constructed at the default strength");
            Assert.AreEqual(1f, erosion.Context.Strength);
            foreach (Vector3d d in Spiral(20000))
            {
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(0f, s.Erosion);
                planet.Height.GetElevation(d, 0f, out TerrainBaseSample ctx);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 0f, out ErosionSample e);
                Assert.AreEqual(0f, e.Displacement);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 1e-3f, out e);
                Assert.AreEqual(0f, e.Displacement, "Band-limited too");
            }
            foreach (var (kind, d) in Representative(planet))
            {
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(0f, s.Erosion, kind);
            }
            // Strength 2 as well: nothing scales a zero.
            PlanetInstance strong = Planet(2f);
            foreach (Vector3d d in Spiral(2000))
            {
                strong.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(0f, s.Erosion);
            }
        }

        // B, G
        [Test]
        public void BG_TheTerrain_IsBitIdentical_WithTheStageOnOrOff_AtEveryRepresentativeLocation()
        {
            PlanetInstance on = Planet(1f), off = Planet(0f), strong = Planet(2f);
            Assert.IsNull(off.Height.SurfaceDetail.Erosion, "Strength 0: no stage (the G.1.1 evaluation path exactly)");
            int checkedPoints = 0;
            foreach (var (kind, d) in Representative(on))
            {
                foreach (float spacing in new[] { 0f, 1e-6f, 1e-4f, 1e-2f })
                {
                    Assert.AreEqual(off.Height.GetElevation(d, spacing), on.Height.GetElevation(d, spacing), $"{kind} at spacing {spacing}: bit-identical");
                    Assert.AreEqual(off.Height.GetElevation(d, spacing), strong.Height.GetElevation(d, spacing), $"{kind} at spacing {spacing}, strength 2");
                }
                Assert.AreEqual(off.Height.GetElevation(d.ToVector3()), on.Height.GetElevation(d.ToVector3()), kind + " (float path)");
                Assert.AreEqual(off.Height.GetSurfaceNormal(d, 1e-4f), on.Height.GetSurfaceNormal(d, 1e-4f), kind + " normal");
                checkedPoints++;
            }
            Assert.GreaterOrEqual(checkedPoints, 10);
            foreach (Vector3d d in Spiral(20000))
            {
                Assert.AreEqual(off.Height.GetElevation(d), on.Height.GetElevation(d));
            }
            Assert.AreEqual(off.MinElevation, on.MinElevation);
            Assert.AreEqual(off.MaxElevation, on.MaxElevation);
            Assert.AreEqual(off.LandFraction, on.LandFraction);
            Assert.IsTrue(new PlanetDefinition().erosionStrength == 1f, "New worlds carry the stage");
            Assert.AreEqual((int)ErosionModel.Fluvial, new PlanetDefinition().erosionModel, "...with the fluvial model (V0.2.0G.2B)");
            Assert.IsFalse(new PlanetDefinition { erosionStrength = 0f }.TerrainEquals(new PlanetDefinition()), "A terrain setting (regeneration follows it)");
        }

        // C
        [Test]
        public void C_Deterministic_AcrossRepeatedEvaluations_AndFreshContexts()
        {
            var a = new PlanetInstance(new PlanetDefinition { seed = Seed, erosionModel = 0 });
            var b = new PlanetInstance(new PlanetDefinition { seed = Seed, erosionModel = 0 });
            Assert.AreNotSame(a.Height.SurfaceDetail.Erosion.Context, b.Height.SurfaceDetail.Erosion.Context);
            Assert.AreEqual(a.Height.SurfaceDetail.Erosion.Context.Seed, b.Height.SurfaceDetail.Erosion.Context.Seed);
            var list = new List<Vector3d>(Spiral(3000));
            var first = new List<float>();
            foreach (Vector3d d in list)
            {
                a.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                first.Add(s.Erosion);
                Assert.AreEqual(a.Height.GetElevation(d), b.Height.GetElevation(d));
            }
            for (int i = list.Count - 1; i >= 0; i--) // reverse order, the other context
            {
                b.Height.SampleLayers(list[i].ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(first[i], s.Erosion);
            }
            for (int rep = 0; rep < 3; rep++)
            {
                a.Height.SampleLayers(list[7].ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(first[7], s.Erosion, "Repeated evaluation, no state");
            }
        }

        // D
        [Test]
        public void D_LodPaths_SampleTheSameTerrain()
        {
            PlanetInstance planet = Planet();
            var snapshot = new AdaptiveSurfaceSnapshot(1, planet, null, PlanetColorRamp.CreateDefault(), true, 16);
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            int shared = 0;
            foreach (var (kind, d) in Representative(planet))
            {
                PlanetPatchId fine = CubeSphereCoordinates.PatchAt(d.ToVector3(), 14);
                builder.Build(snapshot, fine, data);
                Vector3 reference = data.Origin + data.Vertices[0];
                for (int level = 10; level <= 13; level++)
                {
                    int shift = 14 - level;
                    if ((fine.X & ((1 << shift) - 1)) != 0 || (fine.Y & ((1 << shift) - 1)) != 0) continue;
                    builder.Build(snapshot, new PlanetPatchId(fine.Face, level, fine.X >> shift, fine.Y >> shift), data);
                    Assert.Less(((data.Origin + data.Vertices[0]) - reference).magnitude, planet.Radius * 2e-7f, $"{kind}: level {level} vs 14");
                    shared++;
                }
                // Bounds elevation (landforms complete, octaves band-limited) and the canonical function agree on the stage: zero.
                float bounds = planet.Height.GetBoundsElevation(d.ToVector3(), 1e-3f);
                Assert.IsTrue(float.IsFinite(bounds));
            }
            Assert.Greater(shared, 0);
            // Patch build order / background scheduling cannot matter: the same patch twice, from two builders.
            var other = new AdaptivePatchGeometryBuilder();
            var data2 = new PatchGeometryData(snapshot.Topology.VertexCount);
            PlanetPatchId id = CubeSphereCoordinates.PatchAt(Representative(planet)[0].d.ToVector3(), 12);
            builder.Build(snapshot, id, data);
            other.Build(snapshot, id, data2);
            for (int v = 0; v < data.Vertices.Length; v++) Assert.AreEqual(data.Vertices[v], data2.Vertices[v]);
        }

        // E
        [Test]
        public void E_FloatingOriginRebase_DoesNotChangeTheErosionOrTheTerrain()
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(Seed, flipFlags: false);
            c.Terrain.resolution = 16;
            c.Terrain.erosionStrength = 1f;
            c.Terrain.erosionModel = 0;
            host.Apply(c);
            host.Definition.seed = Seed;
            host.Regenerate();
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            PlanetInstance planet = host.Current;
            Assert.IsNotNull(planet.Height.SurfaceDetail.Erosion);
            var probes = new List<Vector3d>(Spiral(500));
            var before = probes.ConvertAll(d => planet.Height.GetElevation(d));
            foreach (Vector3d origin in new[] { probes[3] * planet.Radius * 1.001, probes[200] * planet.Radius * 1.0001, Vector3d.Zero })
            {
                frame.RebaseNow(origin);
                for (int i = 0; i < probes.Count; i++)
                {
                    Assert.AreEqual(before[i], planet.Height.GetElevation(probes[i]), "Planet-space terrain is untouched by a rebase");
                    planet.Height.SampleLayers(probes[i].ToVector3(), out TerrainDetailSample s);
                    Assert.AreEqual(0f, s.Erosion);
                }
            }
            Assert.Greater(frame.RebaseCount, 0);
        }

        // F
        [Test]
        public void F_ArchivesBeforeG2A_LoadWithTheStageOff_AndTheirTerrainUnchanged()
        {
            Assert.AreEqual(1f, new PlanetDefinition().erosionStrength, "New worlds start with the stage");
            ArchiveReadResult old = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.IsTrue(old.Ok, old.Report.Summary());
            Assert.AreEqual(0f, old.Archive.Configuration.Terrain.erosionStrength, "Before G.2A: stage off");
            CollectionAssert.Contains(old.Report.DefaultedFields, "terrain.erosionStrength");
            Assert.IsEmpty(old.Report.Warnings, "A documented compatibility rule, not a warning");
            Assert.AreEqual(GeneratorCompatibility.Exact, old.Compatibility);

            // A G.1.1 archive (every field but erosionStrength): stage off, and its terrain is what it recorded.
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("G.1.1 world");
            JsonValue root = JsonReader.Parse(PlanetArchiveSerializer.Serialize(archive));
            Assert.IsTrue(root.Find("terrain").Remove("erosionStrength"), "The field is archived");
            string without = root.ToJson();
            ArchiveReadResult read = PlanetArchiveSerializer.Read(without);
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(0f, read.Archive.Configuration.Terrain.erosionStrength);
            Assert.AreEqual(0, read.Archive.Configuration.Terrain.erosionModel, "Nor the model: a G.1.1 world is bit-identical");
            PlanetDefinition recorded = archive.Configuration.Terrain.Clone();
            var loaded = new PlanetInstance(read.Archive.Configuration.Terrain);
            var written = new PlanetInstance(recorded);
            foreach (Vector3d d in Spiral(3000)) Assert.AreEqual(written.Height.GetElevation(d), loaded.Height.GetElevation(d), "The archived world's terrain is unchanged");

            foreach (float value in new[] { 0f, 0.5f, 1f, 2f })
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive("Erosion " + value);
                a.Configuration.Terrain.erosionStrength = value;
                ArchiveReadResult r = PlanetArchiveSerializer.Read(PlanetArchiveSerializer.Serialize(a));
                Assert.IsTrue(r.Ok, r.Report.Summary());
                Assert.AreEqual(value, r.Archive.Configuration.Terrain.erosionStrength, "Present: honored");
                Assert.IsEmpty(r.Report.DefaultedFields);
            }
        }

        // H
        [Test]
        public void H_TheStageCost_IsMeasured()
        {
            var probes = new List<Vector3d>(Spiral(100000));
            double Time(PlanetInstance p)
            {
                float sink = 0f;
                foreach (Vector3d d in probes) sink += p.Height.GetElevation(d);
                var sw = Stopwatch.StartNew();
                for (int rep = 0; rep < 3; rep++) foreach (Vector3d d in probes) sink += p.Height.GetElevation(d);
                double us = sw.Elapsed.TotalMilliseconds * 1000.0 / (3 * probes.Count);
                Assert.IsTrue(float.IsFinite(sink));
                return us;
            }
            PlanetInstance off = Planet(0f), on = Planet(1f);
            double offUs = Math.Min(Time(off), Time(off)), onUs = Math.Min(Time(on), Time(on));
            var sw = Stopwatch.StartNew();
            var context = new ErosionContext(on.Scale, Seed, on.SeaLevel, 1f, on.Height.SurfaceDetail.Landforms, ErosionModel.None);
            double contextMs = sw.Elapsed.TotalMilliseconds;
            TestContext.WriteLine($"canonical elevation: {offUs:0.00} us/sample without the stage, {onUs:0.00} us/sample with it ({(onUs - offUs) / offUs * 100.0:+0.0;-0.0} %); context construction {contextMs:0.000} ms (reported {context.BuildMilliseconds:0.000} ms)");
            Assert.IsNotNull(context.Valleys);
            Assert.IsNotNull(context.Hydrology);
            Assert.Less(contextMs, 50.0, "The context only references existing data");
            Assert.Less(onUs, offUs * 1.5 + 1.0, "A zero stage costs at most a call per sample (loose bound: not a workstation benchmark)");
        }
    }
}
