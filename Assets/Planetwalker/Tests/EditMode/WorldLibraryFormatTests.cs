using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Generation;
using Planetwalker.Library;
using Planetwalker.Persistence;

namespace Planetwalker.Tests
{
    /// <summary>
    /// World Library data (V0.1.3B), engine independent: V0.1.3A compatibility, the optional metadata snapshot,
    /// thumbnail files, and the in-memory search / sort model. Isolated temporary folders only.
    /// </summary>
    public class WorldLibraryFormatTests
    {
        string root;
        WorldArchiveStore store;
        WorldThumbnailStore thumbnails;

        [SetUp]
        public void SetUp()
        {
            root = WorldArchiveTestUtil.CreateTempDirectory();
            store = new WorldArchiveStore(Path.Combine(root, "Worlds"));
            thumbnails = new WorldThumbnailStore(Path.Combine(root, "Thumbnails"));
            Directory.CreateDirectory(store.Directory);
        }

        [TearDown]
        public void TearDown()
        {
            WorldArchiveTestUtil.DeleteTempDirectory(root);
        }

        /// <summary>A small valid PNG header followed by filler (the store checks the signature only; decoding is the cache's job).</summary>
        static byte[] FakePng(byte fill)
        {
            var bytes = new byte[200];
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Array.Copy(signature, bytes, signature.Length);
            for (int i = signature.Length; i < bytes.Length; i++)
            {
                bytes[i] = fill;
            }
            return bytes;
        }

        static WorldMetadata SampleMetadata() => new WorldMetadata { LandFraction = 0.3125f, MeanTemperature = 13.75f, DominantBiome = Biome.TemperateForest };

        static WorldArchiveEntry Entry(string name, int seed, int minutes, bool valid = true)
        {
            DateTime t = new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);
            return valid
                ? new WorldArchiveEntry { PlanetId = Guid.NewGuid(), DisplayName = name, PlanetSeed = seed, ModifiedUtc = t, CreatedUtc = t.AddDays(-minutes) }
                : new WorldArchiveEntry { PlanetId = Guid.NewGuid(), Error = "Corrupt archive", ModifiedUtc = t };
        }

        // ------------------------------------------------------------ A, B, X (compatibility)

        // A
        [Test]
        public void A_V013AArchives_RemainReadable_Exactly_WithoutMetadata()
        {
            ArchiveReadResult read = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility, "V0.1.3B has the same algorithms: exact reconstruction");
            Assert.IsEmpty(read.Report.Warnings);
            Assert.IsNull(read.Archive.Metadata, "No statistics recorded: shown as unavailable, never invented");
            Assert.AreEqual(WorldArchiveTestUtil.V013AArchiveId, read.Archive.PlanetId);
            Assert.AreEqual("Ærendel ✦", read.Archive.DisplayName);
            Assert.AreEqual(868497, read.Archive.Configuration.PlanetSeed);
            Assert.AreEqual(552901, read.Archive.Configuration.WeatherSeed);

            File.WriteAllText(store.PathFor(WorldArchiveTestUtil.V013AArchiveId), WorldArchiveTestUtil.V013AArchiveJson);
            WorldArchiveEntry entry = store.List().Single();
            Assert.IsTrue(entry.IsValid);
            Assert.IsNull(entry.Metadata);
            Assert.AreEqual(GeneratorCompatibility.Exact, entry.Compatibility);
        }

        // B, X
        [Test]
        public void BX_GuidsNamesAndConfigurationsSurviveEveryLibraryOperation()
        {
            File.WriteAllText(store.PathFor(WorldArchiveTestUtil.V013AArchiveId), WorldArchiveTestUtil.V013AArchiveJson);
            PlanetArchive before = store.Load(WorldArchiveTestUtil.V013AArchiveId).Archive;

            Assert.IsTrue(store.UpdateMetadata(before.PlanetId, SampleMetadata()).Success);
            Assert.IsTrue(store.Rename(before.PlanetId, "Ærendel ✦ II").Success);
            PlanetArchive after = store.Load(before.PlanetId).Archive;
            Assert.AreEqual(WorldArchiveTestUtil.V013AArchiveId, after.PlanetId, "B: the GUID never changes");
            Assert.AreEqual(before.CreatedUtc, after.CreatedUtc);
            WorldArchiveTestUtil.AssertSameConfiguration(before.Configuration, after.Configuration);
            Assert.IsTrue(SettingsFields.AreEqual(before.Presentation.Camera, after.Presentation.Camera));
            Assert.IsTrue(SampleMetadata().ContentEquals(after.Metadata), "Rename keeps the statistics");
            Assert.AreEqual(1, Directory.GetFiles(store.Directory).Length);

            // X: the V0.1.3A operations behave as before (duplicate = new identity, delete = one file).
            ArchiveOperationResult copy = store.Duplicate(before.PlanetId);
            Assert.IsTrue(copy.Success);
            Assert.AreNotEqual(before.PlanetId, copy.Archive.PlanetId);
            Assert.IsTrue(SampleMetadata().ContentEquals(store.Load(copy.Archive.PlanetId).Archive.Metadata), "The copy keeps the statistics");
            Assert.IsTrue(store.Delete(copy.Archive.PlanetId).Success);
            Assert.IsTrue(store.Load(before.PlanetId).Success);
        }

        // ------------------------------------------------------------ C, D (metadata)

        // C
        [Test]
        public void C_MetadataSerialization_IsDeterministicAndExact()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Metadata");
            archive.Metadata = SampleMetadata();
            string a = PlanetArchiveSerializer.Serialize(archive);
            string b = PlanetArchiveSerializer.Serialize(archive.Clone());
            Assert.AreEqual(a, b, "Same archive, same text");
            StringAssert.Contains("\"metadata\": {\n    \"landFraction\": 0.3125,\n    \"meanTemperature\": 13.75,\n    \"dominantBiome\": \"TemperateForest\"\n  }", a);
            ArchiveReadResult read = PlanetArchiveSerializer.Read(a);
            Assert.IsTrue(read.Ok);
            Assert.IsEmpty(read.Report.Warnings);
            Assert.IsTrue(archive.Metadata.ContentEquals(read.Archive.Metadata));
            Assert.AreEqual(a, PlanetArchiveSerializer.Serialize(read.Archive), "Stable round trip");
            Assert.AreEqual(1, int.Parse(JsonReader.Parse(a).Find("archiveSchemaVersion").Text), "No schema change for optional data");

            // Partial snapshots keep only what was available; an empty snapshot is not written.
            archive.Metadata = new WorldMetadata { LandFraction = 0.5f };
            string partial = PlanetArchiveSerializer.Serialize(archive);
            WorldMetadata back = PlanetArchiveSerializer.Read(partial).Archive.Metadata;
            Assert.IsTrue(back.HasLand && !back.HasTemperature && back.DominantBiome == null);
            archive.Metadata = new WorldMetadata();
            StringAssert.DoesNotContain("\"metadata\"", PlanetArchiveSerializer.Serialize(archive));

            // Metadata is derived data: it never makes a world "modified".
            WorldConfiguration c = archive.Configuration.Clone();
            archive.Metadata = SampleMetadata();
            Assert.IsTrue(c.ContentEquals(archive.Configuration));
        }

        // D
        [Test]
        public void D_MissingOrInvalidMetadata_NeverRejectsTheWorld()
        {
            string json = PlanetArchiveSerializer.Serialize(WorldArchiveTestUtil.NewArchive("Odd statistics"));
            foreach (string metadata in new[]
            {
                "\"metadata\": {\"landFraction\": 1.5}", "\"metadata\": {\"meanTemperature\": 1e30}", "\"metadata\": {\"dominantBiome\": \"Lava\"}",
                "\"metadata\": [1, 2]", "\"metadata\": {\"landFraction\": \"most\"}",
            })
            {
                string edited = json.Replace("\"planetSeed\":", metadata + ",\n  \"planetSeed\":");
                ArchiveReadResult read = PlanetArchiveSerializer.Read(edited);
                Assert.IsTrue(read.Ok, metadata + ": the world stays loadable (" + read.Report.Summary() + ")");
                Assert.IsNull(read.Archive.Metadata, metadata + ": statistics unavailable");
                Assert.IsTrue(read.Report.Warnings.Any(w => w.StartsWith("metadata")), metadata + ": reported");
            }
            ArchiveReadResult unknown = PlanetArchiveSerializer.Read(json.Replace("\"planetSeed\":",
                "\"metadata\": {\"landFraction\": 0.25, \"rings\": 3},\n  \"planetSeed\":"));
            Assert.IsTrue(unknown.Ok);
            Assert.AreEqual(0.25f, unknown.Archive.Metadata.LandFraction);
        }

        // ------------------------------------------------------------ Thumbnail files

        [Test]
        public void ThumbnailStore_WritesAtomically_ReadsSafely_NeverUsesNames()
        {
            Guid id = Guid.NewGuid();
            Assert.IsFalse(thumbnails.TryRead(id, out _, out string missing));
            Assert.AreEqual("no thumbnail", missing);
            Assert.IsNull(thumbnails.Write(id, FakePng(1)));
            Assert.AreEqual(PlanetArchive.FormatId(id) + ".png", Path.GetFileName(thumbnails.PathFor(id)));
            Assert.IsNull(thumbnails.Write(id, FakePng(2)), "Replaced");
            Assert.IsTrue(thumbnails.TryRead(id, out byte[] png, out _));
            Assert.AreEqual(2, png[100]);
            Assert.AreEqual(1, Directory.GetFiles(thumbnails.Directory).Length, "No temporary leftovers");
            Assert.IsNotNull(thumbnails.Write(id, new byte[] { 1, 2, 3 }), "Not a PNG: refused");
            Assert.IsTrue(thumbnails.TryRead(id, out png, out _) && png[100] == 2, "Previous thumbnail kept");

            File.WriteAllText(thumbnails.PathFor(id), "garbage that is not an image at all, but long enough to look like a file .......");
            Assert.IsFalse(thumbnails.TryRead(id, out _, out string corrupt));
            Assert.AreEqual("corrupt thumbnail", corrupt);
            Assert.IsNull(thumbnails.Delete(id));
            Assert.IsNull(thumbnails.Delete(id), "Deleting a missing thumbnail is fine");
            Assert.IsNull(thumbnails.Copy(Guid.NewGuid(), Guid.NewGuid()), "Copying a missing thumbnail is a no-op");
        }

        // ------------------------------------------------------------ K, L, M, S, T (search, sort)

        // K
        [Test]
        public void K_Search_IsCaseInsensitive_AcrossUnicode_AndMatchesSeeds()
        {
            var model = new WorldLibraryModel();
            model.SetEntries(new[]
            {
                Entry("Ærendel ✦", 868497, 1), Entry("ÉLYSÉE", 1337, 2), Entry("星の世界", 536452, 3), Entry("Мир Гагарина", 609538, 4),
                Entry("Ocean World", 42, 5), Entry("", 0, 6, valid: false),
            });
            string[] Names() => model.View.Select(e => e.DisplayName).ToArray();
            model.SetQuery("ærendel");
            CollectionAssert.AreEqual(new[] { "Ærendel ✦" }, Names());
            model.SetQuery("élysée");
            CollectionAssert.AreEqual(new[] { "ÉLYSÉE" }, Names());
            model.SetQuery("世界");
            CollectionAssert.AreEqual(new[] { "星の世界" }, Names());
            model.SetQuery("МИР");
            CollectionAssert.AreEqual(new[] { "Мир Гагарина" }, Names());
            model.SetQuery("  1337 ");
            CollectionAssert.AreEqual(new[] { "ÉLYSÉE" }, Names(), "Seed search, trimmed");
            model.SetQuery("8497");
            CollectionAssert.AreEqual(new[] { "Ærendel ✦" }, Names());
            model.SetQuery("nothing like this");
            Assert.AreEqual(0, model.View.Count);
            model.SetQuery("");
            Assert.AreEqual(6, model.View.Count);
            Assert.IsFalse(model.View.Last().IsValid, "Unreadable archives stay listed (last)");
        }

        // L, M
        [Test]
        public void LM_Sorting_IsDeterministic_AndDuplicateNamesAreSupported()
        {
            var entries = new List<WorldArchiveEntry>
            {
                Entry("beta", 2, 10), Entry("Alpha", 1, 30), Entry("alpha", 3, 20), Entry("Gamma", 4, 40), Entry("Alpha", 5, 30),
                Entry(null, 0, 50, valid: false),
            };
            var model = new WorldLibraryModel();
            model.SetEntries(entries);
            string[] Order() => model.View.Select(e => e.IsValid ? e.DisplayName + "#" + e.PlanetSeed : "ERR").ToArray();

            model.SetSort(WorldSortMode.NameAscending);
            string[] ascending = Order();
            Assert.AreEqual("beta#2", ascending[3]);
            Assert.AreEqual("Gamma#4", ascending[4]);
            Assert.AreEqual("ERR", ascending[5], "Unreadable archives last");
            CollectionAssert.AreEquivalent(new[] { "Alpha#1", "alpha#3", "Alpha#5" }, ascending.Take(3), "M: same names coexist");
            model.SetSort(WorldSortMode.NameDescending);
            Assert.AreEqual("Gamma#4", Order()[0]);
            model.SetSort(WorldSortMode.RecentlyModified);
            Assert.AreEqual("Gamma#4", Order()[0]);
            Assert.AreEqual("beta#2", Order()[4]);
            model.SetSort(WorldSortMode.OldestModified);
            Assert.AreEqual("beta#2", Order()[0]);

            // L: any input order gives the same output order (ties broken by planet ID).
            foreach (WorldSortMode mode in Enum.GetValues(typeof(WorldSortMode)))
            {
                model.SetSort(mode);
                string[] reference = Order();
                var random = new System.Random(5);
                for (int i = 0; i < 10; i++)
                {
                    var shuffled = new WorldLibraryModel();
                    shuffled.SetEntries(entries.OrderBy(_ => random.Next()));
                    shuffled.SetSort(mode);
                    CollectionAssert.AreEqual(reference, shuffled.View.Select(e => e.IsValid ? e.DisplayName + "#" + e.PlanetSeed : "ERR").ToArray(),
                        mode + ": deterministic");
                }
            }
        }

        sealed class CountingFileSystem : PhysicalArchiveFileSystem
        {
            public int Reads;

            public override byte[] ReadAllBytes(string path, int maxBytes)
            {
                Reads++;
                return base.ReadAllBytes(path, maxBytes);
            }
        }

        // S, T
        [Test]
        public void ST_HundredsOfArchives_ListOnce_SearchAndSortInMemory_CorruptOnesStayVisible()
        {
            var counting = new CountingFileSystem();
            var countingStore = new WorldArchiveStore(store.Directory, counting);
            for (int i = 0; i < 150; i++)
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive(i % 10 == 0 ? "Twin" : "World " + i);
                a.Configuration.Terrain.seed = 1000 + i;
                a.Metadata = SampleMetadata();
                Assert.IsTrue(store.Save(a).Success);
            }
            Guid broken = Guid.NewGuid();
            File.WriteAllText(store.PathFor(broken), "{ \"archiveSchemaVersion\": 1, ");

            var model = new WorldLibraryModel();
            model.Reload(countingStore);
            int reads = counting.Reads;
            Assert.AreEqual(151, model.All.Count);
            Assert.AreEqual(151, reads, "One read per archive when the library opens");
            WorldArchiveEntry corrupt = model.All.Single(e => e.PlanetId == broken);
            Assert.IsFalse(corrupt.IsValid);
            StringAssert.Contains("Corrupt archive", corrupt.Error, "S: visible with an understandable error");

            var watch = Stopwatch.StartNew();
            foreach (string query in new[] { "t", "tw", "twi", "twin", "1", "10", "104", "" })
            {
                model.SetQuery(query);
            }
            foreach (WorldSortMode mode in Enum.GetValues(typeof(WorldSortMode)))
            {
                model.SetSort(mode);
            }
            watch.Stop();
            Assert.AreEqual(reads, counting.Reads, "Search and sort never touch the disk");
            model.SetQuery("twin");
            Assert.AreEqual(15, model.View.Count);
            WorldArchiveTestUtil.Report(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Library model: 151 archives listed in {0:0} ms; 8 searches + 5 sorts in {1:0.0} ms", model.LastReloadMilliseconds,
                watch.Elapsed.TotalMilliseconds));
            Assert.Less(watch.Elapsed.TotalMilliseconds, 200.0);
        }
    }
}
