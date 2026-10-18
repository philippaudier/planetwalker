using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Persistence;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// World archive format and storage (V0.1.3A): JSON, schema, validation, identity and safe file operations.
    /// Engine-independent: every test uses an isolated temporary folder, never the user's archives.
    /// </summary>
    public class WorldArchiveFormatTests
    {
        string directory;
        WorldArchiveStore store;

        [SetUp]
        public void SetUp()
        {
            directory = WorldArchiveTestUtil.CreateTempDirectory();
            store = new WorldArchiveStore(directory);
        }

        [TearDown]
        public void TearDown()
        {
            WorldArchiveTestUtil.DeleteTempDirectory(directory);
        }

        string FileOf(PlanetArchive archive) => store.PathFor(archive.PlanetId);

        ArchiveReadResult ReadModified(Func<string, string> edit)
        {
            string json = PlanetArchiveSerializer.Serialize(WorldArchiveTestUtil.NewArchive("Edited"));
            return PlanetArchiveSerializer.Read(edit(json));
        }

        static string ReplaceMember(string json, string member, string newValueText)
        {
            int index = json.IndexOf("\"" + member + "\": ", StringComparison.Ordinal);
            Assert.GreaterOrEqual(index, 0, member + " present");
            int start = index + member.Length + 4;
            int end = start;
            int depth = 0;
            while (end < json.Length)
            {
                char c = json[end];
                if (c == '{' || c == '[') depth++;
                if (c == '}' || c == ']') { if (depth == 0) break; depth--; }
                if (c == ',' && depth == 0) break;
                end++;
            }
            return json.Substring(0, start) + newValueText + json.Substring(end);
        }

        static JsonValue Root(string json) => JsonReader.Parse(json);

        static string WithoutMember(string json, params string[] path)
        {
            JsonValue root = Root(json);
            JsonValue parent = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                parent = parent.Find(path[i]);
            }
            Assert.IsTrue(parent.Remove(path[path.Length - 1]), string.Join(".", path) + " removed");
            return root.ToJson();
        }

        // ------------------------------------------------------------ Coverage guard

        [Test]
        public void Guard_EveryArchivedFieldHasASupportedTypeAndIsExercisedByTheRoundTrip()
        {
            var defaults = new WorldConfiguration();
            WorldConfiguration tweaked = WorldArchiveTestUtil.Tweaked();
            int fields = 0;
            foreach (KeyValuePair<string, object> group in defaults.Groups())
            {
                foreach (FieldInfo field in SettingsFields.Of(group.Value.GetType()))
                {
                    Assert.IsTrue(SettingsFields.IsSupported(field.FieldType), group.Key + "." + field.Name + " (" + field.FieldType.Name
                        + ") must be archivable: add support instead of dropping it");
                    fields++;
                }
            }
            List<string> changed = defaults.Differences(tweaked);
            Assert.AreEqual(fields, changed.Count, "Tweaked() must change every archived field so the round trip covers it; unchanged: "
                + string.Join(", ", defaults.Groups().SelectMany(g => SettingsFields.Of(g.Value.GetType()).Select(f => g.Key + "." + f.Name))
                    .Except(changed)));
            Assert.Greater(fields, 75);
            WorldArchiveTestUtil.Report("Archived configuration fields: " + fields);
        }

        // ------------------------------------------------------------ A

        // A
        [Test]
        public void A_SerializationRoundTrip_IsExact()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Round Trip");
            string json = PlanetArchiveSerializer.Serialize(archive);
            ArchiveReadResult read = PlanetArchiveSerializer.Read(json);
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.IsEmpty(read.Report.Warnings);
            Assert.IsEmpty(read.Report.DefaultedFields);
            Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility);
            WorldArchiveTestUtil.AssertSameConfiguration(archive.Configuration, read.Archive.Configuration);
            Assert.AreEqual(archive.PlanetId, read.Archive.PlanetId);
            Assert.AreEqual(archive.DisplayName, read.Archive.DisplayName);
            Assert.AreEqual(archive.CreatedUtc, read.Archive.CreatedUtc);
            Assert.AreEqual(archive.ModifiedUtc, read.Archive.ModifiedUtc);
            Assert.AreEqual(DateTimeKind.Utc, read.Archive.CreatedUtc.Kind);
            Assert.IsTrue(SettingsFields.AreEqual(archive.Presentation.Camera, read.Archive.Presentation.Camera));
            Assert.IsTrue(SettingsFields.AreEqual(archive.Presentation.Solar, read.Archive.Presentation.Solar));
            Assert.AreEqual(json, PlanetArchiveSerializer.Serialize(read.Archive), "Stable text: re-serializing gives the same file");

            JsonValue root = Root(json);
            Assert.AreEqual(1, int.Parse(root.Find("archiveSchemaVersion").Text));
            Assert.AreEqual(PlanetArchive.CurrentGeneratorVersion, root.Find("generatorVersion").Text);
            Assert.AreEqual("536452", root.Find("planetSeed").Text);
            Assert.AreEqual("4242", root.Find("weatherSeed").Text);
            Assert.IsNull(root.Find("terrain").Find("seed"), "The seed is stored once (planetSeed)");
            Assert.IsNull(root.Find("clouds").Find("weatherSeed"), "The weather seed is stored once (weatherSeed)");
            Assert.AreEqual("0.002", root.Find("terrain").Find("seaLevel").Text, "Shortest round-trip float text");
            foreach (string forbidden in new[] { "mesh", "vertices", "grid", "biomeMap", "texture", "cloudTime", "Temperature" })
            {
                StringAssert.DoesNotContain("\"" + forbidden, json, "No generated data in the archive");
            }
            WorldArchiveTestUtil.Report(json);
        }

        [Test]
        public void A_FloatsSurviveTheTextRoundTripBitExactly()
        {
            var random = new System.Random(7);
            for (int i = 0; i < 20000; i++)
            {
                float value = (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 6)));
                JsonValue parsed = JsonReader.Parse(JsonValue.From(value).ToJson());
                Assert.IsTrue(parsed.TryGetFloat(out float back));
                Assert.AreEqual(BitConverter.ToInt32(BitConverter.GetBytes(value), 0), BitConverter.ToInt32(BitConverter.GetBytes(back), 0), "value " + value);
            }
        }

        // ------------------------------------------------------------ B

        // B
        [Test]
        public void B_UnicodeNames_AreStoredExactly_AndNeverUsedAsFileNames()
        {
            string[] names =
            {
                "Ærendel ✦ Ünïcødé", "星の世界", "Мир Гагарина", "عالم جديد", "🌍 Terra Nova 🪐", "Planète \"guillemets\" \\ slash / two",
            };
            foreach (string name in names)
            {
                Assert.IsNull(PlanetArchive.ValidateName(name), name);
                PlanetArchive archive = WorldArchiveTestUtil.NewArchive(name);
                Assert.IsTrue(store.Save(archive).Success);
                Assert.AreEqual(PlanetArchive.FormatId(archive.PlanetId) + ".json", Path.GetFileName(FileOf(archive)));
                ArchiveOperationResult loaded = store.Load(archive.PlanetId);
                Assert.IsTrue(loaded.Success, loaded.Error);
                Assert.AreEqual(name, loaded.Archive.DisplayName);
                string text = File.ReadAllText(FileOf(archive), new UTF8Encoding(false, true));
                Assert.IsTrue(text.Contains(name.Replace("\\", "\\\\").Replace("\"", "\\\"")), "Readable UTF-8 text, not escapes");
            }
            CollectionAssert.AreEquivalent(names, store.List().Select(e => e.DisplayName));
            Assert.AreEqual(names.Length, Directory.GetFiles(directory).Length);

            Assert.IsNotNull(PlanetArchive.ValidateName(""));
            Assert.IsNotNull(PlanetArchive.ValidateName("   "));
            Assert.IsNotNull(PlanetArchive.ValidateName("line\nbreak"));
            Assert.IsNotNull(PlanetArchive.ValidateName("bad \uD83C surrogate"));
            Assert.IsNotNull(PlanetArchive.ValidateName(new string('x', PlanetArchive.MaxNameLength + 1)));
            Assert.AreEqual("line break", PlanetArchive.CleanName("  line\n break\t"));
            Assert.AreEqual(PlanetArchive.DefaultName, PlanetArchive.CleanName(" \n "));
            string longName = new string('a', PlanetArchive.MaxNameLength - 1) + "🌍";
            string clean = PlanetArchive.CleanName(longName);
            Assert.IsNull(PlanetArchive.ValidateName(clean), "Cut never splits a surrogate pair");
        }

        // ------------------------------------------------------------ C

        // C
        [Test]
        public void C_PlanetIds_AreStableRandomGuids_NeverDerivedFromTheSeed()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Stable");
            Guid id = archive.PlanetId;
            Assert.IsTrue(store.Save(archive).Success);
            archive.Configuration.Terrain.seaLevel = 0.004f;
            archive.ModifiedUtc = archive.ModifiedUtc.AddSeconds(5);
            Assert.IsTrue(store.Save(archive).Success, "Saving again replaces the same archive");
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(id, store.Load(id).Archive.PlanetId);
            Assert.AreEqual(0.004f, store.Load(id).Archive.Configuration.Terrain.seaLevel);

            var ids = new HashSet<Guid>();
            for (int i = 0; i < 50; i++)
            {
                Assert.IsTrue(ids.Add(WorldArchiveTestUtil.NewArchive("Same seed").PlanetId), "Unique per world even for the same seed");
            }

            // Strict ID text: lower-case "D" GUIDs only, so a file name can never be a path.
            Assert.IsTrue(PlanetArchive.TryParseId(PlanetArchive.FormatId(id), out Guid parsed) && parsed == id);
            foreach (string bad in new[] { "../../etc/passwd", PlanetArchive.FormatId(id).ToUpperInvariant(), "{" + PlanetArchive.FormatId(id) + "}",
                PlanetArchive.FormatId(id).Replace("-", ""), Guid.Empty.ToString("D"), "", null })
            {
                Assert.IsFalse(PlanetArchive.TryParseId(bad, out _), bad ?? "null");
            }
        }

        // ------------------------------------------------------------ D, E

        // D
        [Test]
        public void D_Duplicate_CreatesANewIdentity_WithTheSameConfiguration()
        {
            PlanetArchive original = WorldArchiveTestUtil.NewArchive("Original");
            original.CreatedUtc = original.ModifiedUtc = original.CreatedUtc.AddDays(-3);
            Assert.IsTrue(store.Save(original).Success);
            byte[] before = File.ReadAllBytes(FileOf(original));

            ArchiveOperationResult copy = store.Duplicate(original.PlanetId);
            Assert.IsTrue(copy.Success, copy.Error);
            Assert.AreNotEqual(original.PlanetId, copy.Archive.PlanetId);
            Assert.AreEqual("Original (copy)", copy.Archive.DisplayName);
            Assert.Greater(copy.Archive.CreatedUtc, original.CreatedUtc);
            PlanetArchive reloaded = store.Load(copy.Archive.PlanetId).Archive;
            WorldArchiveTestUtil.AssertSameConfiguration(original.Configuration, reloaded.Configuration);
            Assert.IsTrue(SettingsFields.AreEqual(original.Presentation.Camera, reloaded.Presentation.Camera));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(FileOf(original)), "The original archive is untouched");
            Assert.AreEqual(2, store.List().Count);

            PlanetArchive longName = WorldArchiveTestUtil.NewArchive(new string('n', PlanetArchive.MaxNameLength));
            Assert.IsTrue(store.Save(longName).Success);
            ArchiveOperationResult longCopy = store.Duplicate(longName.PlanetId);
            Assert.IsTrue(longCopy.Success, longCopy.Error);
            Assert.LessOrEqual(longCopy.Archive.DisplayName.Length, PlanetArchive.MaxNameLength);
        }

        // E
        [Test]
        public void E_TheSameSeed_CanLiveInSeveralArchives()
        {
            PlanetArchive a = WorldArchiveTestUtil.NewArchive("Twin A");
            PlanetArchive b = WorldArchiveTestUtil.NewArchive("Twin B");
            b.Configuration.Terrain.seaLevel = -0.01f;
            b.Configuration.Clouds.coverage = 0.2f;
            Assert.AreEqual(a.Configuration.PlanetSeed, b.Configuration.PlanetSeed);
            Assert.IsTrue(store.Save(a).Success);
            Assert.IsTrue(store.Save(b).Success);
            List<WorldArchiveEntry> entries = store.List();
            Assert.AreEqual(2, entries.Count);
            Assert.IsTrue(entries.All(e => e.IsValid && e.PlanetSeed == a.Configuration.PlanetSeed));
            Assert.AreNotEqual(entries[0].PlanetId, entries[1].PlanetId);
            Assert.AreEqual(-0.01f, store.Load(b.PlanetId).Archive.Configuration.Terrain.seaLevel);
            Assert.AreEqual(0.002f, store.Load(a.PlanetId).Archive.Configuration.Terrain.seaLevel);
        }

        // ------------------------------------------------------------ G, H, I, J

        // G
        [Test]
        public void G_CorruptArchives_AreRejectedSafely()
        {
            string valid = PlanetArchiveSerializer.Serialize(WorldArchiveTestUtil.NewArchive("Valid"));
            var corrupt = new Dictionary<string, string>
            {
                { "empty", "" },
                { "truncated", valid.Substring(0, valid.Length / 2) },
                { "garbage", "\u0001\u0002 not json at all" },
                { "array root", "[1, 2, 3]" },
                { "trailing comma", valid.Replace("\n  }\n}", ",\n  }\n}") },
                { "duplicate key", valid.Replace("\"planetSeed\": 536452,", "\"planetSeed\": 536452,\n  \"planetSeed\": 1,") },
                { "NaN literal", ReplaceMember(valid, "seaLevel", "NaN") },
                { "comment", "// world\n" + valid },
                { "deep nesting", new string('[', 200) + new string(']', 200) },
                { "unterminated string", "{\"archiveSchemaVersion\": 1, \"planetId\": \"abc" },
            };
            foreach (KeyValuePair<string, string> c in corrupt)
            {
                ArchiveReadResult read = PlanetArchiveSerializer.Read(c.Value);
                Assert.IsFalse(read.Ok, c.Key + " must be rejected");
                Assert.IsNull(read.Archive, c.Key);
                Assert.IsNotEmpty(read.Report.Summary(), c.Key + ": a readable reason");
            }

            // On disk: invalid UTF-8 and oversized files; the listing shows them as unreadable, load refuses them.
            Guid utf8 = Guid.NewGuid(), big = Guid.NewGuid(), text = Guid.NewGuid();
            File.WriteAllBytes(store.PathFor(utf8), new byte[] { 0x7B, 0xC3, 0x28, 0xFF, 0x7D });
            File.WriteAllText(store.PathFor(big), new string(' ', WorldArchiveStore.MaxFileBytes + 10));
            File.WriteAllText(store.PathFor(text), corrupt["truncated"]);
            File.WriteAllText(Path.Combine(directory, "notes.json"), valid); // not an archive file name: ignored
            foreach (Guid id in new[] { utf8, big, text })
            {
                ArchiveOperationResult load = store.Load(id);
                Assert.IsFalse(load.Success);
                Assert.IsNotEmpty(load.Error);
            }
            List<WorldArchiveEntry> entries = store.List();
            Assert.AreEqual(3, entries.Count);
            Assert.IsTrue(entries.All(e => !e.IsValid && !string.IsNullOrEmpty(e.Error)));
            Assert.IsTrue(store.Delete(text).Success, "An unreadable archive can still be deleted");
        }

        // H
        [Test]
        public void H_UnsupportedSchemaVersions_AreRejected()
        {
            ArchiveReadResult future = ReadModified(j => ReplaceMember(j, "archiveSchemaVersion", "2"));
            Assert.IsFalse(future.Ok);
            StringAssert.Contains("newer version", future.Report.Summary());
            Assert.AreEqual(2, future.SourceSchemaVersion);
            Assert.IsFalse(ReadModified(j => ReplaceMember(j, "archiveSchemaVersion", "0")).Ok);
            Assert.IsFalse(ReadModified(j => ReplaceMember(j, "archiveSchemaVersion", "1.5")).Ok);
            Assert.IsFalse(ReadModified(j => ReplaceMember(j, "archiveSchemaVersion", "\"1\"")).Ok);
            Assert.IsFalse(ReadModified(j => WithoutMember(j, "archiveSchemaVersion")).Ok);
            Assert.IsTrue(ReadModified(j => j).Ok);
        }

        [Test]
        public void H_GeneratorVersions_ExactOrBestEffort_ForeignGeneratorsRejected()
        {
            ArchiveReadResult same = ReadModified(j => j);
            Assert.AreEqual(GeneratorCompatibility.Exact, same.Compatibility);
            ArchiveReadResult older = ReadModified(j => ReplaceMember(j, "generatorVersion", "\"Genesis V0.1.2D\""));
            Assert.IsTrue(older.Ok, older.Report.Summary());
            Assert.AreEqual(GeneratorCompatibility.BestEffort, older.Compatibility);
            Assert.IsTrue(older.Report.Warnings.Any(w => w.Contains("best-effort")), "Best-effort is reported, never silent");
            Assert.IsFalse(ReadModified(j => ReplaceMember(j, "generatorVersion", "\"SomeOtherTool 3.0\"")).Ok);
            Assert.IsFalse(ReadModified(j => ReplaceMember(j, "generatorVersion", "\"\"")).Ok);
        }

        // I
        [Test]
        public void I_MissingOptionalFields_ReceiveTheDocumentedDefaults()
        {
            ArchiveReadResult read = ReadModified(j =>
            {
                j = WithoutMember(j, "weatherSeed");
                j = WithoutMember(j, "clouds");
                j = WithoutMember(j, "presentation");
                j = WithoutMember(j, "lakes", "fillingFactor");
                return WithoutMember(j, "terrain", "mountainRuggedness");
            });
            Assert.IsTrue(read.Ok, read.Report.Summary());
            var defaults = new WorldConfiguration();
            Assert.AreEqual(0, read.Archive.Configuration.WeatherSeed, "weatherSeed: 0 = derived from the planet seed");
            Assert.IsTrue(SettingsFields.AreEqual(defaults.Clouds, read.Archive.Configuration.Clouds), "clouds: generator defaults");
            Assert.AreEqual(defaults.Lakes.fillingFactor, read.Archive.Configuration.Lakes.fillingFactor);
            Assert.AreEqual(defaults.Terrain.mountainRuggedness, read.Archive.Configuration.Terrain.mountainRuggedness);
            Assert.AreEqual(0.6f, read.Archive.Configuration.Lakes.abundance, "Present fields are read normally");
            Assert.IsNull(read.Archive.Presentation.Camera, "No camera: the current view is kept");
            Assert.IsNull(read.Archive.Presentation.Solar);
            CollectionAssert.IsSupersetOf(read.Report.DefaultedFields,
                new[] { "weatherSeed", "clouds", "lakes.fillingFactor", "terrain.mountainRuggedness" }, "Defaults are reported, never silent");

            // Required data is never defaulted.
            foreach (string[] required in new[]
            {
                new[] { "planetSeed" }, new[] { "terrain" }, new[] { "planetId" }, new[] { "displayName" }, new[] { "createdUtc" },
                new[] { "generatorVersion" },
            })
            {
                Assert.IsFalse(ReadModified(j => WithoutMember(j, required)).Ok, string.Join(".", required) + " is required");
            }

            ArchiveReadResult unknown = ReadModified(j => j.Replace("\"lighting\": {", "\"lighting\": {\n    \"futureGlow\": 3,"));
            Assert.IsTrue(unknown.Ok);
            Assert.IsTrue(unknown.Report.Warnings.Any(w => w.Contains("lighting.futureGlow")), "Unknown data is reported");
            ArchiveReadResult duplicateSeed = ReadModified(j => j.Replace("\"terrain\": {", "\"terrain\": {\n    \"seed\": 1,"));
            Assert.IsTrue(duplicateSeed.Ok);
            Assert.AreEqual(536452, duplicateSeed.Archive.Configuration.PlanetSeed, "planetSeed is authoritative");
            Assert.IsTrue(duplicateSeed.Report.Warnings.Any(w => w.Contains("terrain.seed")), "A conflicting copy is reported");
        }

        // J
        [Test]
        public void J_InvalidNumbers_AreRejected_NotClamped()
        {
            var invalid = new Dictionary<string, Func<string, string>>
            {
                { "overflow to Infinity", j => ReplaceMember(j, "seaLevel", "1e999") },
                { "sea level out of range", j => ReplaceMember(j, "seaLevel", "0.5") },
                { "negative radius", j => ReplaceMember(j, "radius", "-5") },
                { "hydrology resolution not allowed", j => j.Replace("\"resolution\": 64", "\"resolution\": 100") },
                { "color above 1", j => j.Replace("\"r\": 0.2,", "\"r\": 2,") },
                { "fractional seed", j => ReplaceMember(j, "planetSeed", "1.5") },
                { "seed beyond int", j => ReplaceMember(j, "planetSeed", "3000000000") },
                { "string instead of number", j => ReplaceMember(j, "coverage", "\"0.5\"") },
                { "null value", j => ReplaceMember(j, "density", "null") },
                { "solar time 1", j => ReplaceMember(j, "solarTime", "1") },
                { "camera pitch", j => ReplaceMember(j, "pitch", "120") },
                { "zero rotation axis", j => j.Replace("\"rotationAxis\": {\n      \"x\": 0,\n      \"y\": 1,\n      \"z\": 0.05\n    }",
                    "\"rotationAxis\": {\n      \"x\": 0,\n      \"y\": 0,\n      \"z\": 0\n    }") },
                { "bool as number", j => ReplaceMember(j, "motion", "1") },
                { "bad timestamp", j => ReplaceMember(j, "createdUtc", "\"yesterday\"") },
                { "bad planet id", j => ReplaceMember(j, "planetId", "\"../evil\"") },
                { "control char name", j => ReplaceMember(j, "displayName", "\"a\\u0007b\"") },
            };
            foreach (KeyValuePair<string, Func<string, string>> c in invalid)
            {
                string original = PlanetArchiveSerializer.Serialize(WorldArchiveTestUtil.NewArchive("Edited"));
                string edited = c.Value(original);
                Assert.AreNotEqual(original, edited, c.Key + ": the edit applies");
                ArchiveReadResult read = PlanetArchiveSerializer.Read(edited);
                Assert.IsFalse(read.Ok, c.Key + " must be rejected");
                WorldArchiveTestUtil.Report(c.Key + " -> " + read.Report.Summary());
            }

            var nan = WorldArchiveTestUtil.NewArchive("NaN");
            nan.Configuration.Climate.lapseRate = float.NaN;
            Assert.IsFalse(PlanetArchiveSerializer.Validate(nan).Ok);
            Assert.IsFalse(store.Save(nan).Success, "Never written");
            Assert.AreEqual(0, Directory.GetFiles(directory).Length);
        }

        // ------------------------------------------------------------ K, L

        sealed class FaultyFileSystem : PhysicalArchiveFileSystem
        {
            public Exception FailWrite;
            public Exception FailReplace;
            public bool CorruptWrite;
            public bool CrashBeforeReplace;

            public override void WriteNewFile(string path, byte[] bytes)
            {
                if (FailWrite != null)
                {
                    // A partial write, then the failure (disk full / permission).
                    File.WriteAllBytes(path, bytes.Take(bytes.Length / 3).ToArray());
                    throw FailWrite;
                }
                if (CorruptWrite)
                {
                    bytes = (byte[])bytes.Clone();
                    bytes[bytes.Length / 2] ^= 0x20;
                }
                base.WriteNewFile(path, bytes);
            }

            public override void Replace(string source, string destination)
            {
                if (CrashBeforeReplace)
                {
                    throw new SimulatedCrash();
                }
                if (FailReplace != null)
                {
                    throw FailReplace;
                }
                base.Replace(source, destination);
            }
        }

        sealed class SimulatedCrash : Exception { }

        // K
        [Test]
        public void K_FailedSaves_PreserveThePreviousArchive()
        {
            var faulty = new FaultyFileSystem();
            var faultyStore = new WorldArchiveStore(directory, faulty);
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Precious");
            Assert.IsTrue(faultyStore.Save(archive).Success);
            byte[] good = File.ReadAllBytes(FileOf(archive));
            archive.Configuration.Terrain.seaLevel = -0.02f;

            var failures = new (string Name, Action Arm, string Expected)[]
            {
                ("disk full", () => faulty.FailWrite = new IOException("No space left on device."), "No space left"),
                ("permission", () => faulty.FailWrite = new UnauthorizedAccessException("Access denied."), "permission denied"),
                ("replace failure", () => faulty.FailReplace = new IOException("The file is locked."), "locked"),
                ("corrupted write", () => faulty.CorruptWrite = true, "does not match"),
            };
            foreach (var failure in failures)
            {
                faulty.FailWrite = null;
                faulty.FailReplace = null;
                faulty.CorruptWrite = false;
                failure.Arm();
                ArchiveOperationResult result = faultyStore.Save(archive);
                Assert.IsFalse(result.Success, failure.Name);
                StringAssert.Contains(failure.Expected, result.Error, failure.Name);
                StringAssert.Contains("previous archive", result.Error);
                CollectionAssert.AreEqual(good, File.ReadAllBytes(FileOf(archive)), failure.Name + ": previous archive intact");
                Assert.AreEqual(1, Directory.GetFiles(directory).Length, failure.Name + ": temporary file cleaned up");
            }
            Assert.AreEqual(0.002f, store.Load(archive.PlanetId).Archive.Configuration.Terrain.seaLevel, "Still the last valid save");

            // Missing directory: created on demand.
            string nested = Path.Combine(directory, "missing", "worlds");
            Assert.IsTrue(new WorldArchiveStore(nested).Save(WorldArchiveTestUtil.NewArchive("Nested")).Success);
        }

        // L
        [Test]
        public void L_Replacement_IsAtomic_AndInterruptedWritesLeaveNoDamage()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Atomic");
            Assert.IsTrue(store.Save(archive).Success);
            byte[] first = File.ReadAllBytes(FileOf(archive));
            for (int i = 0; i < 10; i++)
            {
                archive.Configuration.Clouds.coverage = i / 10f;
                Assert.IsTrue(store.Save(archive).Success);
                Assert.AreEqual(1, Directory.GetFiles(directory).Length, "Exactly one file, no temporary leftovers");
                Assert.AreEqual(i / 10f, store.Load(archive.PlanetId).Archive.Configuration.Clouds.coverage);
            }

            // A crash between the temporary write and the rename: the archive keeps its last complete version.
            byte[] last = File.ReadAllBytes(FileOf(archive));
            var crashing = new WorldArchiveStore(directory, new FaultyFileSystem { CrashBeforeReplace = true });
            archive.Configuration.Clouds.coverage = 0.99f;
            Assert.Throws<SimulatedCrash>(() => crashing.Save(archive));
            CollectionAssert.AreEqual(last, File.ReadAllBytes(FileOf(archive)));
            string[] temps = Directory.GetFiles(directory, "*" + WorldArchiveStore.TempExtension);
            Assert.AreEqual(1, temps.Length, "The interrupted write left a temporary file");
            Assert.AreEqual(1, store.List().Count, "Temporary files are never listed");
            Assert.IsTrue(store.Load(archive.PlanetId).Success);
            File.SetLastWriteTimeUtc(temps[0], DateTime.UtcNow - WorldArchiveStore.StaleTempAge - TimeSpan.FromMinutes(1));
            store.List();
            Assert.AreEqual(0, Directory.GetFiles(directory, "*" + WorldArchiveStore.TempExtension).Length, "Stale temporary files are removed");
            CollectionAssert.AreNotEqual(first, last);
        }

        // ------------------------------------------------------------ M, N

        // M
        [Test]
        public void M_Rename_PreservesIdentityAndConfiguration()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Old Name");
            archive.CreatedUtc = archive.ModifiedUtc = archive.CreatedUtc.AddHours(-1);
            Assert.IsTrue(store.Save(archive).Success);
            ArchiveOperationResult renamed = store.Rename(archive.PlanetId, "Nouveau nom ✦");
            Assert.IsTrue(renamed.Success, renamed.Error);
            PlanetArchive reloaded = store.Load(archive.PlanetId).Archive;
            Assert.AreEqual("Nouveau nom ✦", reloaded.DisplayName);
            Assert.AreEqual(archive.PlanetId, reloaded.PlanetId);
            Assert.AreEqual(archive.CreatedUtc, reloaded.CreatedUtc);
            Assert.Greater(reloaded.ModifiedUtc, archive.ModifiedUtc);
            WorldArchiveTestUtil.AssertSameConfiguration(archive.Configuration, reloaded.Configuration);
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            Assert.IsFalse(store.Rename(archive.PlanetId, "  ").Success, "Invalid names are refused");
            Assert.IsFalse(store.Rename(Guid.NewGuid(), "Nobody").Success);
        }

        // N
        [Test]
        public void N_Delete_RemovesOnlyTheSelectedArchive()
        {
            PlanetArchive[] archives = { WorldArchiveTestUtil.NewArchive("Keep 1"), WorldArchiveTestUtil.NewArchive("Delete me"), WorldArchiveTestUtil.NewArchive("Keep 2") };
            foreach (PlanetArchive a in archives)
            {
                Assert.IsTrue(store.Save(a).Success);
            }
            byte[] keep1 = File.ReadAllBytes(FileOf(archives[0]));
            byte[] keep2 = File.ReadAllBytes(FileOf(archives[2]));
            Assert.IsTrue(store.Delete(archives[1].PlanetId).Success);
            Assert.IsFalse(File.Exists(FileOf(archives[1])));
            CollectionAssert.AreEqual(keep1, File.ReadAllBytes(FileOf(archives[0])));
            CollectionAssert.AreEqual(keep2, File.ReadAllBytes(FileOf(archives[2])));
            CollectionAssert.AreEquivalent(new[] { "Keep 1", "Keep 2" }, store.List().Select(e => e.DisplayName));
            Assert.IsFalse(store.Delete(archives[1].PlanetId).Success, "Already deleted");
            Assert.IsFalse(store.Delete(Guid.Empty).Success);
        }

        [Test]
        public void IdMismatch_CopiedFilesCannotImpersonateAnotherWorld()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Original");
            Assert.IsTrue(store.Save(archive).Success);
            Guid other = Guid.NewGuid();
            File.Copy(FileOf(archive), store.PathFor(other));
            Assert.IsFalse(store.Load(other).Success);
            WorldArchiveEntry copied = store.List().Single(e => e.PlanetId == other);
            Assert.IsFalse(copied.IsValid);
        }

        [Test]
        public void Listing_IsSortedByModificationAndReadsMetadataOnly()
        {
            DateTime now = PlanetArchiveSerializer.UtcNowTruncated();
            for (int i = 0; i < 5; i++)
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive("World " + i);
                a.ModifiedUtc = now.AddMinutes(i);
                Assert.IsTrue(store.Save(a).Success);
            }
            List<WorldArchiveEntry> entries = store.List();
            CollectionAssert.AreEqual(new[] { "World 4", "World 3", "World 2", "World 1", "World 0" }, entries.Select(e => e.DisplayName));
            Assert.IsTrue(entries.All(e => e.FileBytes > 1000 && e.FileBytes < 20000));
        }

        // ------------------------------------------------------------ T, U (V0.2.0C: physical scale and surface detail)

        static readonly string[] SurfaceDetailFields = { "surfaceDetail", "radiusKilometers", "regionalDetail", "localDetail", "microDetail" };

        // T1
        [Test]
        public void T_ArchivesBeforeV020C_LoadExactly_WithSurfaceDetailOff_AndNoWarning()
        {
            // The V0.1.3A fixture has no terrain.surfaceDetail: an older world never silently gains detail. With it
            // off the terrain is what V0.1.3A generated, so reconstruction is exact and nothing is worth a warning.
            ArchiveReadResult read = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility);
            Assert.IsEmpty(read.Report.Warnings, "A compatibility default is a documented rule, not a warning");
            PlanetDefinition terrain = read.Archive.Configuration.Terrain;
            Assert.IsFalse(terrain.surfaceDetail, "Archives written before V0.2.0C load with surface detail off");
            Assert.IsTrue(new PlanetDefinition().surfaceDetail, "...even though new worlds start detailed");
            Assert.AreEqual(6371f, terrain.radiusKilometers, "Physical radius: the documented default (Earth)");
            Assert.AreEqual(1f, terrain.regionalDetail);
            Assert.AreEqual(1f, terrain.localDetail);
            Assert.AreEqual(1f, terrain.microDetail);
            CollectionAssert.IsSupersetOf(read.Report.DefaultedFields, SurfaceDetailFields.Select(f => "terrain." + f),
                "The absent V0.2.0C fields are reported as defaulted, never silent");

            // The same rule stands alone and is keyed on the defaulted entry only.
            var fresh = new WorldConfiguration();
            var report = new ArchiveReport();
            PlanetArchiveSerializer.ApplyCompatibilityDefaults(fresh, report);
            Assert.IsTrue(fresh.Terrain.surfaceDetail, "Nothing defaulted: nothing changes");
            report.Defaulted("terrain.surfaceDetail");
            PlanetArchiveSerializer.ApplyCompatibilityDefaults(fresh, report);
            Assert.IsFalse(fresh.Terrain.surfaceDetail);
            Assert.IsEmpty(report.Warnings);
        }

        // T2
        [Test]
        public void T_SurfaceDetail_IsHonoredWhenPresent_WhateverItsValue()
        {
            foreach (bool detail in new[] { true, false })
            {
                PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Detail " + detail);
                archive.Configuration.Terrain.surfaceDetail = detail;
                string json = PlanetArchiveSerializer.Serialize(archive);
                ArchiveReadResult read = PlanetArchiveSerializer.Read(json);
                Assert.IsTrue(read.Ok, read.Report.Summary());
                Assert.AreEqual(detail, read.Archive.Configuration.Terrain.surfaceDetail, "Present: honored, " + detail);
                Assert.IsEmpty(read.Report.DefaultedFields);
                Assert.IsEmpty(read.Report.Warnings);

                // Presence is what counts, not the generator version: an older label with the field present keeps it.
                ArchiveReadResult relabelled = PlanetArchiveSerializer.Read(ReplaceMember(json, "generatorVersion", "\"Genesis V0.1.3B\""));
                Assert.IsTrue(relabelled.Ok, relabelled.Report.Summary());
                Assert.AreEqual(detail, relabelled.Archive.Configuration.Terrain.surfaceDetail);
            }

            // Removing only the flag from a current archive applies the rule; the other detail fields stay as written.
            ArchiveReadResult removed = ReadModified(j => WithoutMember(j, "terrain", "surfaceDetail"));
            Assert.IsTrue(removed.Ok, removed.Report.Summary());
            Assert.IsFalse(removed.Archive.Configuration.Terrain.surfaceDetail);
            Assert.AreEqual(3390f, removed.Archive.Configuration.Terrain.radiusKilometers);
            Assert.AreEqual(1.4f, removed.Archive.Configuration.Terrain.regionalDetail);
            CollectionAssert.Contains(removed.Report.DefaultedFields, "terrain.surfaceDetail");
            Assert.IsEmpty(removed.Report.Warnings);
        }

        // T3
        [Test]
        public void T_GeneratorVersions_V013A_V013B_AndCurrent_AreExact_OlderStayBestEffort()
        {
            Assert.AreEqual("Genesis V0.2.0G.2B", PlanetArchive.CurrentGeneratorVersion);
            foreach (string exact in new[] { "Genesis V0.1.3A", "Genesis V0.1.3B", "Genesis V0.2.0C", "Genesis V0.2.0G.1", PlanetArchive.CurrentGeneratorVersion })
            {
                ArchiveReadResult read = ReadModified(j => ReplaceMember(j, "generatorVersion", "\"" + exact + "\""));
                Assert.IsTrue(read.Ok, read.Report.Summary());
                Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility, exact);
                Assert.IsEmpty(read.Report.Warnings, exact);
                Assert.AreEqual(GeneratorCompatibility.Exact, PlanetArchiveSerializer.CompatibilityOf(exact));
            }
            foreach (string older in new[] { "Genesis V0.1.2D", "Genesis V0.2.0A", "Genesis V0.2.0B" })
            {
                ArchiveReadResult read = ReadModified(j => ReplaceMember(j, "generatorVersion", "\"" + older + "\""));
                Assert.IsTrue(read.Ok, read.Report.Summary());
                Assert.AreEqual(GeneratorCompatibility.BestEffort, read.Compatibility, older);
                Assert.IsTrue(read.Report.Warnings.Any(w => w.Contains("best-effort")), older);
            }
        }

        // T4 (V0.2.0G.1)
        [Test]
        public void T_ArchivesBeforeV020G1_LoadWithLandformsOff_PresentValuesAreHonored()
        {
            // Every archive written before V0.2.0G.1 lacks terrain.regionalLandforms: it loads with landforms off, so its
            // terrain is exactly what it recorded (V0.2.0C - V0.2.0F.3), silently (a documented rule, not a warning).
            Assert.AreEqual(1f, new PlanetDefinition().regionalLandforms, "New worlds start with landforms");
            ArchiveReadResult old = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.IsTrue(old.Ok, old.Report.Summary());
            Assert.AreEqual(0f, old.Archive.Configuration.Terrain.regionalLandforms);
            Assert.IsFalse(old.Archive.Configuration.Terrain.surfaceDetail, "Both rules apply to one archive");
            CollectionAssert.Contains(old.Report.DefaultedFields, "terrain.regionalLandforms");
            Assert.IsEmpty(old.Report.Warnings);

            ArchiveReadResult removed = ReadModified(j => WithoutMember(j, "terrain", "regionalLandforms"));
            Assert.IsTrue(removed.Ok, removed.Report.Summary());
            Assert.AreEqual(0f, removed.Archive.Configuration.Terrain.regionalLandforms, "A V0.2.0C - F.3 archive: landforms off");
            Assert.AreEqual(WorldArchiveTestUtil.NewArchive("Edited").Configuration.Terrain.surfaceDetail,
                removed.Archive.Configuration.Terrain.surfaceDetail, "...and its surface detail as written");
            Assert.IsEmpty(removed.Report.Warnings);

            foreach (float value in new[] { 0f, 0.5f, 1f, 2f })
            {
                PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Landforms " + value);
                archive.Configuration.Terrain.regionalLandforms = value;
                ArchiveReadResult read = PlanetArchiveSerializer.Read(PlanetArchiveSerializer.Serialize(archive));
                Assert.IsTrue(read.Ok, read.Report.Summary());
                Assert.AreEqual(value, read.Archive.Configuration.Terrain.regionalLandforms, "Present: honored");
                Assert.IsEmpty(read.Report.DefaultedFields);
            }
        }

        // U
        [Test]
        public void U_SurfaceDetailSettings_SurviveSaveAndLoad_InDeclarationOrder()
        {
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("Detailed", WorldArchiveTestUtil.Tweaked(flipFlags: false));
            PlanetDefinition t = archive.Configuration.Terrain;
            Assert.IsTrue(t.surfaceDetail && t.radiusKilometers == 3390f && t.regionalDetail == 1.4f && t.localDetail == 0.6f && t.microDetail == 1.8f,
                "Tweaked() sets every V0.2.0C field to a non-default value");
            Assert.IsTrue(store.Save(archive).Success);
            ArchiveOperationResult loaded = store.Load(archive.PlanetId);
            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(GeneratorCompatibility.Exact, loaded.Read.Compatibility);
            Assert.AreEqual(GeneratorCompatibility.Exact, store.List().Single().Compatibility);
            WorldArchiveTestUtil.AssertSameConfiguration(archive.Configuration, loaded.Archive.Configuration);
            Assert.IsTrue(t.SurfaceDetailEquals(loaded.Archive.Configuration.Terrain));
            string json = File.ReadAllText(FileOf(archive), new UTF8Encoding(false, true));
            Assert.AreEqual(json, PlanetArchiveSerializer.Serialize(loaded.Archive), "Stable text after a reload");

            // The new members sit in the terrain group, in declaration order, after the V0.1.3A fields.
            JsonValue terrain = Root(json).Find("terrain");
            List<string> members = terrain.Members.Select(m => m.Key).ToList();
            Assert.AreEqual(JsonKind.Bool, terrain.Find("surfaceDetail").Kind);
            Assert.IsTrue(terrain.Find("surfaceDetail").Bool);
            Assert.AreEqual("3390", terrain.Find("radiusKilometers").Text);
            Assert.AreEqual("1.4", terrain.Find("regionalDetail").Text);
            Assert.AreEqual("0.6", terrain.Find("localDetail").Text);
            Assert.AreEqual("1.8", terrain.Find("microDetail").Text);
            int ruggedness = members.IndexOf("mountainRuggedness");
            Assert.GreaterOrEqual(ruggedness, 0);
            CollectionAssert.AreEqual(new[] { "radiusKilometers", "surfaceDetail", "regionalDetail", "localDetail", "microDetail" },
                members.Skip(ruggedness + 1).Take(5), "Declaration order of the V0.2.0C fields");
            CollectionAssert.AreEqual(new[] { "regionalLandforms", "erosionStrength", "erosionModel" }, members.Skip(ruggedness + 6), "Then the V0.2.0G.1, V0.2.0G.2A and V0.2.0G.2B fields, last");
            WorldArchiveTestUtil.Report(terrain.ToJson());
        }
    }
}
