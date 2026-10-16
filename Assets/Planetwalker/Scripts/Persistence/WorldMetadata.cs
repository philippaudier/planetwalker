using System;
using System.Globalization;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;

namespace Planetwalker.Persistence
{
    /// <summary>
    /// Planetary characteristics recorded with a saved world (V0.1.3B), for the World Library. A snapshot of
    /// values the application had already computed when the world was saved: nothing is regenerated to obtain
    /// or display them. Each value is optional (NaN / null = unavailable) and is only recorded when the live
    /// data provably belongs to the saved configuration. Derived, non-authoritative data: never part of the
    /// unsaved-changes comparison, never needed to reconstruct the planet.
    /// </summary>
    public sealed class WorldMetadata
    {
        public const float MinTemperature = -150f;
        public const float MaxTemperature = 150f;

        /// <summary>Fraction of the surface above sea level, in [0, 1] (8192 elevation samples, lakes count as land).</summary>
        public float LandFraction = float.NaN;

        /// <summary>Area-weighted annual mean sea-level-and-terrain temperature of the whole surface, degrees C.</summary>
        public float MeanTemperature = float.NaN;

        /// <summary>Biome covering the largest land area (null when unavailable).</summary>
        public Biome? DominantBiome;

        public bool HasLand => !float.IsNaN(LandFraction);
        public bool HasTemperature => !float.IsNaN(MeanTemperature);
        public bool IsEmpty => !HasLand && !HasTemperature && DominantBiome == null;

        public WorldMetadata Clone() => (WorldMetadata)MemberwiseClone();

        public bool ContentEquals(WorldMetadata other)
        {
            return other != null && LandFraction.Equals(other.LandFraction) && MeanTemperature.Equals(other.MeanTemperature)
                && DominantBiome == other.DominantBiome;
        }

        /// <summary>
        /// Reads the statistics the visual controller already holds, keeping only those computed for exactly the
        /// saved terrain / climate / biome settings. Never computes anything; returns null when nothing qualifies.
        /// </summary>
        public static WorldMetadata Capture(PlanetVisualController visual, WorldConfiguration saved)
        {
            PlanetInstance planet = visual != null ? visual.Planet : null;
            if (planet == null || !planet.Definition.TerrainEquals(saved.Terrain))
            {
                return null; // the displayed planet is not (yet) the saved one
            }
            var metadata = new WorldMetadata { LandFraction = planet.LandFraction };
            ClimateSettings climateSettings = saved.Climate.Clone();
            ClimateResult climate = visual.ClimateCache.IsCurrent(planet, climateSettings) ? visual.ClimateCache.Current : null;
            if (climate != null)
            {
                float mean = climate.Diagnostics.MeanTemperature;
                if (!float.IsNaN(mean) && !float.IsInfinity(mean))
                {
                    metadata.MeanTemperature = mean;
                }
                BiomeSettings biomeSettings = saved.Biomes.Clone();
                if (visual.BiomeCache.IsCurrent(climate, biomeSettings))
                {
                    metadata.DominantBiome = visual.BiomeCache.Current.Diagnostics.DominantBiome;
                }
            }
            return metadata;
        }

        // ------------------------------------------------------------------ JSON (archive member "metadata")

        public JsonValue ToJson()
        {
            JsonValue obj = JsonValue.NewObject();
            if (HasLand)
            {
                obj.Add("landFraction", JsonValue.From(LandFraction));
            }
            if (HasTemperature)
            {
                obj.Add("meanTemperature", JsonValue.From(MeanTemperature));
            }
            if (DominantBiome != null)
            {
                obj.Add("dominantBiome", JsonValue.From(DominantBiome.Value.ToString()));
            }
            return obj;
        }

        /// <summary>
        /// Parses the optional metadata object. Being derived data, an invalid snapshot never rejects the archive:
        /// it is dropped (null) with a warning, and the world shows its statistics as unavailable.
        /// </summary>
        public static WorldMetadata FromJson(JsonValue obj, ArchiveReport report)
        {
            if (obj.Kind != JsonKind.Object)
            {
                report.Warning("metadata: not an object; statistics unavailable.");
                return null;
            }
            var metadata = new WorldMetadata();
            foreach (var member in obj.Members)
            {
                switch (member.Key)
                {
                    case "landFraction":
                        if (!member.Value.TryGetFloat(out float land) || land < 0f || land > 1f)
                        {
                            return Invalid(report, "landFraction");
                        }
                        metadata.LandFraction = land;
                        break;
                    case "meanTemperature":
                        if (!member.Value.TryGetFloat(out float t) || t < MinTemperature || t > MaxTemperature)
                        {
                            return Invalid(report, "meanTemperature");
                        }
                        metadata.MeanTemperature = t;
                        break;
                    case "dominantBiome":
                        if (member.Value.Kind != JsonKind.String || Array.IndexOf(Enum.GetNames(typeof(Biome)), member.Value.Text) < 0)
                        {
                            return Invalid(report, "dominantBiome");
                        }
                        metadata.DominantBiome = (Biome)Enum.Parse(typeof(Biome), member.Value.Text);
                        break;
                    default:
                        report.Warning("metadata." + member.Key + ": unknown field ignored.");
                        break;
                }
            }
            return metadata;
        }

        static WorldMetadata Invalid(ArchiveReport report, string field)
        {
            report.Warning("metadata." + field + ": invalid value; statistics unavailable (the world itself is unaffected).");
            return null;
        }

        // ------------------------------------------------------------------ Display

        public static string FormatPercent(float fraction) =>
            (fraction * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        public static string FormatTemperature(float celsius) =>
            celsius.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
    }
}
