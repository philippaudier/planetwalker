using System;
using System.Collections.Generic;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Persistence
{
    /// <summary>
    /// A saved world (V0.1.3A): identity, metadata, the complete persistent configuration (seeds and every
    /// settings group) and an optional presentation snapshot. Never contains generated data (meshes, grids,
    /// climate, biomes, cloud textures) or Unity objects: the planet is reconstructed from it.
    /// </summary>
    public sealed class PlanetArchive
    {
        /// <summary>How the file is structured. Bumped (with a migration step) when the layout changes.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Which procedural algorithms and defaults produce the planet (see <see cref="GeneratorCompatibility"/>).</summary>
        public const string CurrentGeneratorVersion = "Genesis V0.2.0G.1";

        public const string DefaultName = "Unnamed World";
        public const int MaxNameLength = 80;

        public int SchemaVersion = CurrentSchemaVersion;
        public string GeneratorVersion = CurrentGeneratorVersion;

        /// <summary>Stable identity: a random GUID, never derived from the seed. Also the file name.</summary>
        public Guid PlanetId;
        public string DisplayName = DefaultName;
        public DateTime CreatedUtc;
        public DateTime ModifiedUtc;

        public WorldConfiguration Configuration = new WorldConfiguration();

        /// <summary>Camera and solar time; optional (null parts are simply not restored).</summary>
        public PresentationState Presentation = new PresentationState();

        /// <summary>Planetary statistics recorded at save time (V0.1.3B, optional, derived data), or null.</summary>
        public WorldMetadata Metadata;

        public PlanetArchive Clone()
        {
            return new PlanetArchive
            {
                SchemaVersion = SchemaVersion,
                GeneratorVersion = GeneratorVersion,
                PlanetId = PlanetId,
                DisplayName = DisplayName,
                CreatedUtc = CreatedUtc,
                ModifiedUtc = ModifiedUtc,
                Configuration = Configuration.Clone(),
                Presentation = Presentation?.Clone(),
                Metadata = Metadata?.Clone(),
            };
        }

        /// <summary>Lower-case "D" GUID text used in files and file names.</summary>
        public string IdText => FormatId(PlanetId);

        /// <summary>First 8 hex digits of the ID, for compact display.</summary>
        public string ShortId => IdText.Substring(0, 8);

        public static string FormatId(Guid id) => id.ToString("D");

        /// <summary>Strict ID parsing: exactly the lower-case "D" format, never empty (no path tricks possible).</summary>
        public static bool TryParseId(string text, out Guid id)
        {
            id = Guid.Empty;
            return text != null && text.Length == 36 && Guid.TryParseExact(text, "D", out id) && id != Guid.Empty
                && FormatId(id) == text;
        }

        /// <summary>
        /// Display name rules: 1 .. <see cref="MaxNameLength"/> UTF-16 units after trimming, any Unicode text
        /// (well-formed surrogate pairs), no control characters. Returns null when valid, else the reason.
        /// </summary>
        public static string ValidateName(string name)
        {
            if (name == null || name.Trim().Length == 0)
            {
                return "the name is empty";
            }
            if (name.Length > MaxNameLength)
            {
                return "the name is longer than " + MaxNameLength + " characters";
            }
            if (name != name.Trim())
            {
                return "the name has leading or trailing spaces";
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsControl(c))
                {
                    return "the name contains a control character";
                }
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                    {
                        return "the name contains an invalid Unicode sequence";
                    }
                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return "the name contains an invalid Unicode sequence";
                }
            }
            return null;
        }

        /// <summary>
        /// Turns user input into a valid name: control characters removed, spaces trimmed, cut to the maximum
        /// length (never inside a surrogate pair); empty input gives <see cref="DefaultName"/>. Used for names
        /// typed in the UI only; names read from files are validated, never repaired.
        /// </summary>
        public static string CleanName(string input)
        {
            if (input == null)
            {
                return DefaultName;
            }
            var builder = new System.Text.StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    builder.Append(c).Append(input[i + 1]);
                    i++;
                }
                else if (!char.IsControl(c) && !char.IsSurrogate(c))
                {
                    builder.Append(c);
                }
            }
            string name = builder.ToString().Trim();
            if (name.Length > MaxNameLength)
            {
                int cut = MaxNameLength;
                if (char.IsLowSurrogate(name[cut]) && char.IsHighSurrogate(name[cut - 1]))
                {
                    cut--;
                }
                name = name.Substring(0, cut).TrimEnd();
            }
            return name.Length == 0 ? DefaultName : name;
        }
    }

    /// <summary>
    /// The persistent configuration of a world: everything that defines it, nothing that is transient
    /// (camera, solar time, cloud animation phase, debug views and contemplation mode are excluded).
    /// Each group is a private copy of the project's own settings class (the authoritative definition of
    /// its fields, defaults and ranges).
    /// </summary>
    public sealed class WorldConfiguration
    {
        /// <summary>Terrain generation, including the planet seed (archived as top-level <c>planetSeed</c>).</summary>
        public PlanetDefinition Terrain = new PlanetDefinition();
        public HydrologySettings Hydrology = new HydrologySettings();
        public RiverSettings Rivers = new RiverSettings();
        public LakeSettings Lakes = new LakeSettings();
        public ClimateSettings Climate = new ClimateSettings();
        public BiomeSettings Biomes = new BiomeSettings();
        public NaturalSurfaceSettings Surface = new NaturalSurfaceSettings();
        public OceanAtmosphereSettings OceanAtmosphere = new OceanAtmosphereSettings();
        /// <summary>Clouds, including the weather seed (archived as top-level <c>weatherSeed</c>).</summary>
        public CloudSettings Clouds = new CloudSettings();
        public LightingSettings Lighting = new LightingSettings();

        public int PlanetSeed => Terrain.seed;
        public int WeatherSeed => Clouds.weatherSeed;

        /// <summary>Every group with its archive name, in the order settings are applied on load.</summary>
        public IEnumerable<KeyValuePair<string, object>> Groups()
        {
            yield return new KeyValuePair<string, object>("terrain", Terrain);
            yield return new KeyValuePair<string, object>("hydrology", Hydrology);
            yield return new KeyValuePair<string, object>("rivers", Rivers);
            yield return new KeyValuePair<string, object>("lakes", Lakes);
            yield return new KeyValuePair<string, object>("climate", Climate);
            yield return new KeyValuePair<string, object>("biomes", Biomes);
            yield return new KeyValuePair<string, object>("surface", Surface);
            yield return new KeyValuePair<string, object>("oceanAtmosphere", OceanAtmosphere);
            yield return new KeyValuePair<string, object>("clouds", Clouds);
            yield return new KeyValuePair<string, object>("lighting", Lighting);
        }

        public WorldConfiguration Clone()
        {
            var copy = new WorldConfiguration();
            CopyTo(copy);
            return copy;
        }

        public void CopyTo(WorldConfiguration destination)
        {
            using (IEnumerator<KeyValuePair<string, object>> target = destination.Groups().GetEnumerator())
            {
                foreach (KeyValuePair<string, object> group in Groups())
                {
                    target.MoveNext();
                    SettingsFields.Copy(group.Value, target.Current.Value);
                }
            }
        }

        /// <summary>Field-by-field equality of every group (the unsaved-changes test).</summary>
        public bool ContentEquals(WorldConfiguration other)
        {
            if (other == null)
            {
                return false;
            }
            using (IEnumerator<KeyValuePair<string, object>> theirs = other.Groups().GetEnumerator())
            {
                foreach (KeyValuePair<string, object> group in Groups())
                {
                    theirs.MoveNext();
                    if (!SettingsFields.AreEqual(group.Value, theirs.Current.Value))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Qualified names of the fields that differ ("terrain.seaLevel", ...).</summary>
        public List<string> Differences(WorldConfiguration other)
        {
            var result = new List<string>();
            using (IEnumerator<KeyValuePair<string, object>> theirs = other.Groups().GetEnumerator())
            {
                foreach (KeyValuePair<string, object> group in Groups())
                {
                    theirs.MoveNext();
                    result.AddRange(SettingsFields.Differences(group.Value, theirs.Current.Value, group.Key));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Persistent lighting look of a world: the shading part of <see cref="SolarSettings"/> (the time part is
    /// presentation, see <see cref="SolarPresentation"/>). Same fields, ranges and defaults.
    /// </summary>
    [Serializable]
    public sealed class LightingSettings
    {
        [Range(0f, 1f)]
        public float twilightWarmth = 0.5f;

        [Range(0f, 1f)]
        public float nightAmbient = 0.04f;

        public void Sanitize()
        {
            twilightWarmth = Mathf.Clamp01(twilightWarmth);
            nightAmbient = Mathf.Clamp01(nightAmbient);
        }

        public static LightingSettings From(SolarSettings solar)
        {
            return new LightingSettings { twilightWarmth = solar.twilightWarmth, nightAmbient = solar.nightAmbient };
        }

        public void ApplyTo(SolarSettings solar)
        {
            solar.twilightWarmth = twilightWarmth;
            solar.nightAmbient = nightAmbient;
        }
    }

    /// <summary>Saved view: orbit camera orientation and distance (rendering units). Restored clamped to the camera limits.</summary>
    [Serializable]
    public sealed class CameraPresentation
    {
        [Tooltip("Degrees, wrapped to [-180, 180).")]
        [Range(-180f, 180f)]
        public float yaw;

        [Range(-90f, 90f)]
        public float pitch = 15f;

        [Tooltip("Distance from the planet center, rendering units.")]
        [Range(0.001f, 1000000f)]
        public float distance = 300f;

        public CameraPresentation Clone() => SettingsFields.Clone(this);
    }

    /// <summary>Saved solar clock state (the time part of <see cref="SolarSettings"/>).</summary>
    [Serializable]
    public sealed class SolarPresentation
    {
        [Range(0f, 1f)]
        public float solarTime = SolarSettings.DefaultSolarTime;

        public bool dayNightCycle;

        public bool paused;

        [Range(SolarSettings.MinCycleDuration, SolarSettings.MaxCycleDuration)]
        public float cycleDuration = 240f;

        public void Sanitize()
        {
            solarTime = SolarClock.Wrap(solarTime);
            cycleDuration = Mathf.Clamp(cycleDuration, SolarSettings.MinCycleDuration, SolarSettings.MaxCycleDuration);
        }

        public static SolarPresentation From(SolarSettings solar)
        {
            return new SolarPresentation
            {
                solarTime = SolarClock.Wrap(solar.solarTime),
                dayNightCycle = solar.dayNightCycle,
                paused = solar.paused,
                cycleDuration = Mathf.Clamp(solar.cycleDuration, SolarSettings.MinCycleDuration, SolarSettings.MaxCycleDuration),
            };
        }

        public SolarPresentation Clone() => SettingsFields.Clone(this);
    }

    /// <summary>
    /// Optional presentation snapshot, captured on every explicit save and never tracked as an unsaved change.
    /// Cloud animation phase, debug views and contemplation mode are deliberately not part of it.
    /// </summary>
    public sealed class PresentationState
    {
        public CameraPresentation Camera;
        public SolarPresentation Solar;

        public PresentationState Clone()
        {
            return new PresentationState { Camera = Camera?.Clone(), Solar = Solar?.Clone() };
        }
    }

    public enum GeneratorCompatibility
    {
        /// <summary>Same generator: the planet is reconstructed exactly.</summary>
        Exact,
        /// <summary>
        /// Different generator version: the same seeds and settings are applied, but terrain, climate, biomes
        /// or clouds may differ wherever the algorithms or their defaults changed.
        /// </summary>
        BestEffort,
    }
}
