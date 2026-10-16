using System;
using System.Collections.Generic;
using System.Globalization;
using Planetwalker.Core;

namespace Planetwalker.Persistence
{
    /// <summary>Outcome of reading an archive: the archive (null when rejected) and the full report.</summary>
    public sealed class ArchiveReadResult
    {
        public PlanetArchive Archive;
        public ArchiveReport Report = new ArchiveReport();
        public GeneratorCompatibility Compatibility;
        /// <summary>Schema version found in the file (before migration).</summary>
        public int SourceSchemaVersion;

        public bool Ok => Archive != null && Report.Ok;
    }

    /// <summary>
    /// World archive JSON format, schema 1 (V0.1.3A). Treats files as untrusted input: strict JSON, explicit
    /// schema versioning with forward-only migrations, rejection of future schemas, type / finiteness / range
    /// validation of every field, no repair of malformed data. See the README (World Archive) for the layout.
    /// </summary>
    public static class PlanetArchiveSerializer
    {
        /// <summary>
        /// Generator versions whose archives the current generator reproduces exactly: the current one and earlier
        /// versions with identical procedural algorithms and defaults. V0.1.3B only added the World Library;
        /// V0.2.0C added the surface detail, which <see cref="ApplyCompatibilityDefaults"/> turns off for archives
        /// that predate it, so their terrain stays bit-identical.
        /// </summary>
        static readonly string[] ExactGeneratorVersions = { PlanetArchive.CurrentGeneratorVersion, "Genesis V0.1.3A", "Genesis V0.1.3B" };

        /// <summary>The terrain member whose absence marks an archive written before V0.2.0C.</summary>
        const string SurfaceDetailField = "terrain.surfaceDetail";

        /// <summary>
        /// Fields every archive before V0.2.0C lacks and whose defaults are a documented compatibility rule (the
        /// terrain is bit-identical with them): not worth a "defaults used" warning when a world is loaded.
        /// </summary>
        public static readonly string[] CompatibilityDefaultFields =
        {
            "terrain.radiusKilometers", SurfaceDetailField, "terrain.regionalDetail", "terrain.localDetail", "terrain.microDetail",
        };

        public static bool IsCompatibilityDefault(string field) => System.Array.IndexOf(CompatibilityDefaultFields, field) >= 0;

        const string GeneratorFamily = "Genesis V";
        const int MaxGeneratorVersionLength = 64;
        const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        static readonly string[] TimestampFormats = { TimestampFormat, "yyyy-MM-dd'T'HH:mm:ss'Z'" };

        static readonly string[] TerrainExcluded = { "seed" };
        static readonly string[] CloudExcluded = { "weatherSeed" };

        static readonly string[] TopLevelMembers =
        {
            "archiveSchemaVersion", "generatorVersion", "planetId", "displayName", "createdUtc", "modifiedUtc", "planetSeed",
            "weatherSeed", "terrain", "hydrology", "rivers", "lakes", "climate", "biomes", "surface", "oceanAtmosphere", "clouds",
            "lighting", "presentation", "metadata",
        };

        /// <summary>
        /// Schema migrations: entry n upgrades a document of schema n + 1 to schema n + 2 (entry 0: 1 -> 2).
        /// Schema 1 is the first and current schema, so there is none yet. A future step must only add
        /// members with documented defaults or rename / restructure existing ones, never drop data.
        /// </summary>
        static readonly Func<JsonValue, ArchiveReport, JsonValue>[] Migrations = { };

        // ------------------------------------------------------------------ Writing

        public static string Serialize(PlanetArchive archive)
        {
            WorldConfiguration c = archive.Configuration;
            JsonValue root = JsonValue.NewObject()
                .Add("archiveSchemaVersion", JsonValue.From(PlanetArchive.CurrentSchemaVersion))
                .Add("generatorVersion", JsonValue.From(archive.GeneratorVersion))
                .Add("planetId", JsonValue.From(archive.IdText))
                .Add("displayName", JsonValue.From(archive.DisplayName))
                .Add("createdUtc", JsonValue.From(FormatTimestamp(archive.CreatedUtc)))
                .Add("modifiedUtc", JsonValue.From(FormatTimestamp(archive.ModifiedUtc)))
                .Add("planetSeed", JsonValue.From(c.Terrain.seed))
                .Add("weatherSeed", JsonValue.From(c.Clouds.weatherSeed));
            foreach (KeyValuePair<string, object> group in c.Groups())
            {
                root.Add(group.Key, SettingsFields.Write(group.Value, Excluded(group.Key)));
            }
            PresentationState p = archive.Presentation;
            if (p != null && (p.Camera != null || p.Solar != null))
            {
                JsonValue presentation = JsonValue.NewObject();
                if (p.Camera != null)
                {
                    presentation.Add("camera", SettingsFields.Write(p.Camera));
                }
                if (p.Solar != null)
                {
                    presentation.Add("solar", SettingsFields.Write(p.Solar));
                }
                root.Add("presentation", presentation);
            }
            if (archive.Metadata != null && !archive.Metadata.IsEmpty)
            {
                // Optional member (V0.1.3B): same schema 1; V0.1.3A ignores it with a warning.
                root.Add("metadata", archive.Metadata.ToJson());
            }
            return root.ToJson();
        }

        public static string FormatTimestamp(DateTime utc)
        {
            return utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>UTC now, truncated to the millisecond (what the file can store: exact round trip).</summary>
        public static DateTime UtcNowTruncated()
        {
            DateTime now = DateTime.UtcNow;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        static ICollection<string> Excluded(string group)
        {
            return group == "terrain" ? TerrainExcluded : group == "clouds" ? CloudExcluded : null;
        }

        // ------------------------------------------------------------------ Reading

        /// <summary>Parses, migrates and validates an archive document. Never throws on bad input.</summary>
        public static ArchiveReadResult Read(string json)
        {
            var result = new ArchiveReadResult();
            ArchiveReport report = result.Report;
            JsonValue root;
            try
            {
                root = JsonReader.Parse(json);
            }
            catch (JsonFormatException e)
            {
                report.Error("Corrupt archive: " + e.Message);
                return result;
            }
            if (root.Kind != JsonKind.Object)
            {
                report.Error("Corrupt archive: the document is not a JSON object.");
                return result;
            }

            JsonValue schema = root.Find("archiveSchemaVersion");
            if (schema == null || !schema.TryGetInt(out int version))
            {
                report.Error("Missing or invalid archiveSchemaVersion: this is not a Planetwalker world archive.");
                return result;
            }
            result.SourceSchemaVersion = version;
            if (version < 1)
            {
                report.Error("Unsupported archive schema version " + version + ".");
                return result;
            }
            if (version > PlanetArchive.CurrentSchemaVersion)
            {
                report.Error("This world was saved by a newer version of Planetwalker (archive schema " + version
                    + "); this version reads schemas up to " + PlanetArchive.CurrentSchemaVersion + ".");
                return result;
            }
            for (int v = version; v < PlanetArchive.CurrentSchemaVersion; v++)
            {
                root = Migrations[v - 1](root, report);
                report.Warning("Migrated from archive schema " + v + " to " + (v + 1) + ".");
            }

            var archive = new PlanetArchive();
            WorldConfiguration c = archive.Configuration;

            // Identity and metadata (required).
            string generator = RequireString(root, "generatorVersion", report);
            if (generator != null)
            {
                if (generator.Length == 0 || generator.Length > MaxGeneratorVersionLength || HasControl(generator)
                    || !generator.StartsWith(GeneratorFamily, StringComparison.Ordinal))
                {
                    report.Error("generatorVersion: unsupported generator '" + Truncate(generator) + "'.");
                }
                archive.GeneratorVersion = generator;
            }
            string id = RequireString(root, "planetId", report);
            if (id != null)
            {
                if (PlanetArchive.TryParseId(id, out Guid guid))
                {
                    archive.PlanetId = guid;
                }
                else
                {
                    report.Error("planetId: not a valid planet ID.");
                }
            }
            string name = RequireString(root, "displayName", report);
            if (name != null)
            {
                string problem = PlanetArchive.ValidateName(name);
                if (problem != null)
                {
                    report.Error("displayName: " + problem + ".");
                }
                archive.DisplayName = name;
            }
            archive.CreatedUtc = RequireTimestamp(root, "createdUtc", report);
            archive.ModifiedUtc = RequireTimestamp(root, "modifiedUtc", report);

            // Seeds: the planet seed is required, the weather seed optional (0 = derived from the planet seed).
            JsonValue planetSeed = root.Find("planetSeed");
            if (planetSeed == null)
            {
                report.Error("planetSeed: missing.");
            }
            else if (planetSeed.TryGetInt(out int seed))
            {
                c.Terrain.seed = seed;
            }
            else
            {
                report.Error("planetSeed: expected a 32-bit integer.");
            }
            JsonValue weatherSeed = root.Find("weatherSeed");
            if (weatherSeed == null)
            {
                report.Defaulted("weatherSeed");
            }
            else if (weatherSeed.TryGetInt(out int weather))
            {
                c.Clouds.weatherSeed = weather;
            }
            else
            {
                report.Error("weatherSeed: expected a 32-bit integer.");
            }

            // Settings groups: terrain is required, the others default to the generator defaults when absent.
            foreach (KeyValuePair<string, object> group in c.Groups())
            {
                JsonValue obj = root.Find(group.Key);
                if (obj == null)
                {
                    if (group.Key == "terrain")
                    {
                        report.Error("terrain: missing settings group.");
                    }
                    else
                    {
                        report.Defaulted(group.Key);
                    }
                    continue;
                }
                SettingsFields.Read(obj, group.Value, group.Key, report, Excluded(group.Key));
            }
            ApplyCompatibilityDefaults(c, report);

            // Presentation (optional, and so are its parts).
            JsonValue presentation = root.Find("presentation");
            archive.Presentation = new PresentationState();
            if (presentation != null)
            {
                if (presentation.Kind != JsonKind.Object)
                {
                    report.Error("presentation: expected an object.");
                }
                else
                {
                    JsonValue camera = presentation.Find("camera");
                    if (camera != null)
                    {
                        archive.Presentation.Camera = new CameraPresentation();
                        SettingsFields.Read(camera, archive.Presentation.Camera, "presentation.camera", report);
                    }
                    JsonValue solar = presentation.Find("solar");
                    if (solar != null)
                    {
                        archive.Presentation.Solar = new SolarPresentation();
                        SettingsFields.Read(solar, archive.Presentation.Solar, "presentation.solar", report);
                    }
                    foreach (KeyValuePair<string, JsonValue> member in presentation.Members)
                    {
                        if (member.Key != "camera" && member.Key != "solar")
                        {
                            report.Warning("presentation." + member.Key + ": unknown field ignored.");
                        }
                    }
                }
            }

            JsonValue metadata = root.Find("metadata");
            if (metadata != null)
            {
                archive.Metadata = WorldMetadata.FromJson(metadata, report);
            }

            foreach (KeyValuePair<string, JsonValue> member in root.Members)
            {
                if (Array.IndexOf(TopLevelMembers, member.Key) < 0)
                {
                    report.Warning(member.Key + ": unknown field ignored.");
                }
            }

            if (report.Ok)
            {
                ValidateContent(archive, report);
            }
            if (!report.Ok)
            {
                return result;
            }
            result.Compatibility = CompatibilityOf(archive.GeneratorVersion);
            if (result.Compatibility == GeneratorCompatibility.BestEffort)
            {
                report.Warning("Saved by generator '" + archive.GeneratorVersion + "', current is '" + PlanetArchive.CurrentGeneratorVersion
                    + "': best-effort reconstruction (the same seeds and settings, but the planet may differ where algorithms changed).");
            }
            archive.SchemaVersion = PlanetArchive.CurrentSchemaVersion;
            result.Archive = archive;
            return result;
        }

        /// <summary>
        /// Compatibility rules for fields whose documented default differs from what an older archive means by
        /// its absence (V0.2.0C). Applied once the settings groups are read, keyed on presence only: a present
        /// field is always honored, whatever the generator version. Deliberately silent (no warning): the
        /// archive is reconstructed exactly, nothing was lost or guessed.
        /// <list type="bullet">
        /// <item><c>terrain.surfaceDetail</c> absent (every archive before V0.2.0C): surface detail off. New
        /// worlds start detailed (the C# default is true), but an older world must never silently gain detail;
        /// with it off the terrain is bit-identical to what V0.1.3A / V0.1.3B generated.</item>
        /// </list>
        /// </summary>
        public static void ApplyCompatibilityDefaults(WorldConfiguration configuration, ArchiveReport report)
        {
            foreach (string field in report.DefaultedFields)
            {
                if (field == SurfaceDetailField)
                {
                    configuration.Terrain.surfaceDetail = false;
                    return;
                }
            }
        }

        public static GeneratorCompatibility CompatibilityOf(string generatorVersion)
        {
            return Array.IndexOf(ExactGeneratorVersions, generatorVersion) >= 0 ? GeneratorCompatibility.Exact : GeneratorCompatibility.BestEffort;
        }

        /// <summary>Complete validation of an in-memory archive (also run before every save).</summary>
        public static ArchiveReport Validate(PlanetArchive archive)
        {
            var report = new ArchiveReport();
            if (archive.PlanetId == Guid.Empty)
            {
                report.Error("planetId: empty.");
            }
            string problem = PlanetArchive.ValidateName(archive.DisplayName);
            if (problem != null)
            {
                report.Error("displayName: " + problem + ".");
            }
            if (string.IsNullOrEmpty(archive.GeneratorVersion) || !archive.GeneratorVersion.StartsWith(GeneratorFamily, StringComparison.Ordinal))
            {
                report.Error("generatorVersion: unsupported generator.");
            }
            ValidateContent(archive, report);
            return report;
        }

        static void ValidateContent(PlanetArchive archive, ArchiveReport report)
        {
            foreach (KeyValuePair<string, object> group in archive.Configuration.Groups())
            {
                SettingsFields.Validate(group.Value, group.Key, report);
            }
            if (archive.Presentation?.Camera != null)
            {
                SettingsFields.Validate(archive.Presentation.Camera, "presentation.camera", report);
            }
            if (archive.Presentation?.Solar != null)
            {
                SettingsFields.Validate(archive.Presentation.Solar, "presentation.solar", report);
                if (archive.Presentation.Solar.solarTime >= 1f)
                {
                    report.Error("presentation.solar.solarTime: must be below 1.");
                }
            }
        }

        static string RequireString(JsonValue root, string name, ArchiveReport report)
        {
            JsonValue value = root.Find(name);
            if (value == null)
            {
                report.Error(name + ": missing.");
                return null;
            }
            if (value.Kind != JsonKind.String)
            {
                report.Error(name + ": expected a string.");
                return null;
            }
            return value.Text;
        }

        static DateTime RequireTimestamp(JsonValue root, string name, ArchiveReport report)
        {
            string text = RequireString(root, name, report);
            if (text == null)
            {
                return default;
            }
            if (DateTime.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime value))
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
            report.Error(name + ": expected a UTC timestamp like 2026-10-16T09:30:00.000Z.");
            return default;
        }

        static bool HasControl(string s)
        {
            foreach (char c in s)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }
            return false;
        }

        static string Truncate(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";
    }
}
