using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Data-reading colors and color-bar legends of the climate debug views (rendered unlit, sRGB).
    /// Palettes only change how data is shown, never the data. Land and sea come from the render
    /// vertex itself; the ocean is slightly darker so coasts stay readable.
    /// </summary>
    public static class ClimateDebugColors
    {
        /// <summary>Color bar of a view: a gradient, labelled ticks at positions in [0, 1], a title and a note.</summary>
        public struct Legend
        {
            public string Title;
            public Color[] Gradient;
            public float[] TickPositions;
            public string[] TickLabels;
            public string Note;
        }

        // Annual mean temperature climatology scale (-50 .. +30 C), slightly desaturated.
        static readonly Color[] TemperatureStops =
        {
            new Color(0.95f, 0.91f, 0.95f), // -50 white-pink
            new Color(0.8f, 0.48f, 0.8f), // -40 pink-magenta
            new Color(0.5f, 0.16f, 0.52f), // -30 purple
            new Color(0.2f, 0.12f, 0.5f), // -20 dark blue-violet
            new Color(0.16f, 0.38f, 0.88f), // -10 blue
            new Color(0.38f, 0.84f, 0.9f), // 0 cyan
            new Color(0.95f, 0.9f, 0.32f), // 10 yellow
            new Color(0.9f, 0.36f, 0.14f), // 20 red-orange
            new Color(0.48f, 0.06f, 0.06f), // 30 dark red
        };
        static readonly float[] TemperatureKeys = { -50f, -40f, -30f, -20f, -10f, 0f, 10f, 20f, 30f };

        // Precipitation climatology scale (cream arid .. greens .. teal .. navy very wet), on log2 of the index.
        static readonly Color[] PrecipitationStops =
        {
            new Color(0.98f, 0.96f, 0.84f), // x0.1
            new Color(0.93f, 0.93f, 0.68f), // x0.3
            new Color(0.76f, 0.87f, 0.56f), // x0.6
            new Color(0.52f, 0.76f, 0.45f), // x1
            new Color(0.27f, 0.6f, 0.5f), // x2
            new Color(0.12f, 0.4f, 0.6f), // x4
            new Color(0.03f, 0.13f, 0.32f), // x8
        };
        static readonly float[] PrecipitationKeys = { -3.32f, -1.74f, -0.74f, 0f, 1f, 2f, 3f };

        // Moisture (absolute humidity index): brown dry .. tan .. green .. teal .. blue humid.
        static readonly Color[] MoistureStops =
        {
            new Color(0.62f, 0.45f, 0.25f),
            new Color(0.85f, 0.78f, 0.45f),
            new Color(0.45f, 0.75f, 0.35f),
            new Color(0.15f, 0.6f, 0.65f),
            new Color(0.08f, 0.25f, 0.7f),
        };
        static readonly float[] MoistureKeys = { 0f, 0.15f, 0.35f, 0.6f, 1f };

        static readonly Color ContinentalityCoast = new Color(0.98f, 0.92f, 0.8f);
        static readonly Color ContinentalityInterior = new Color(0.45f, 0.05f, 0.05f);
        static readonly Color OceanNavy = new Color(0.05f, 0.1f, 0.3f);

        public static Color Temperature(float celsius) => Ramp(TemperatureStops, TemperatureKeys, celsius);

        public static Color Precipitation(float index) => Ramp(PrecipitationStops, PrecipitationKeys, Mathf.Log(Mathf.Max(index, 1e-3f), 2f));

        public static Color Moisture(float index) => Ramp(MoistureStops, MoistureKeys, index);

        /// <summary>Winds background: neutral slate, brighter where the wind is faster (arrows carry the direction).</summary>
        public static Color WindBackground(float speed, bool ocean)
        {
            float v = Mathf.Lerp(0.2f, 0.42f, Mathf.Clamp01(speed / 1.2f));
            return ocean ? new Color(v * 0.8f, v * 0.85f, v) : new Color(v, v * 0.98f, v * 0.94f);
        }

        public static Color Continentality(float distanceKm, float influenceDistance)
        {
            return Color.Lerp(ContinentalityInterior, ContinentalityCoast, Mathf.Exp(-distanceKm / influenceDistance));
        }

        /// <summary>
        /// Color of a render vertex. Land and sea come from the vertex's own <paramref name="elevation"/>
        /// (so coastlines follow the displayed terrain, not the coarser climate cells); the climate
        /// fields are sampled smoothly from the climate grid.
        /// </summary>
        public static Color Evaluate(ClimateResult climate, PlanetDebugView view, Vector3 direction, float elevation, bool windDirectionColors = false)
        {
            bool ocean = !(elevation > climate.SeaLevel);
            Color color;
            switch (view)
            {
                case PlanetDebugView.ClimateTemperature:
                    color = Temperature(climate.Sample(climate.Temperature, direction));
                    break;
                case PlanetDebugView.ClimateMoisture:
                {
                    float m = climate.Sample(climate.Moisture, direction);
                    color = Band(Moisture(m), m / 0.1f, 0.9f);
                    break;
                }
                case PlanetDebugView.ClimatePrecipitation:
                    color = Precipitation(climate.Sample(climate.Precipitation, direction));
                    break;
                case PlanetDebugView.ClimateWinds:
                {
                    Vector3 wind = climate.SampleWind(direction);
                    return windDirectionColors ? Compass(climate, direction, wind) : WindBackground(wind.magnitude, ocean);
                }
                case PlanetDebugView.ClimateContinentality:
                {
                    if (ocean)
                    {
                        return OceanNavy;
                    }
                    float distance = climate.Sample(climate.OceanDistanceKm, direction);
                    return Band(Continentality(distance, climate.Settings.oceanInfluenceDistance), distance / 500f, 0.9f);
                }
                default:
                    return Color.magenta;
            }
            return ocean ? color * 0.86f : color;
        }

        /// <summary>Color-bar legend of a climate view.</summary>
        public static Legend GetLegend(PlanetDebugView view, ClimateSettings settings)
        {
            const int samples = 128;
            var legend = new Legend { Gradient = new Color[samples] };
            switch (view)
            {
                case PlanetDebugView.ClimateTemperature:
                    legend.Title = "Annual mean temperature (C)";
                    Fill(legend.Gradient, t => Temperature(Mathf.Lerp(-50f, 30f, t)));
                    Ticks(ref legend, new[] { -40f, -20f, 0f, 10f, 20f, 30f }, v => (v + 50f) / 80f, v => v.ToString("0"));
                    legend.Note = "Ocean slightly darker";
                    break;
                case PlanetDebugView.ClimateMoisture:
                    legend.Title = "Atmospheric moisture: absolute humidity index";
                    Fill(legend.Gradient, t => Moisture(t * 1.2f));
                    Ticks(ref legend, new[] { 0f, 0.15f, 0.35f, 0.6f, 1f }, v => v / 1.2f, v => v.ToString("0.##"));
                    legend.Note = "1 = saturated air at 25 C (not relative humidity: cold air holds little water). Bands every 0.1";
                    break;
                case PlanetDebugView.ClimatePrecipitation:
                    legend.Title = "Precipitation (x planet mean, log scale)";
                    Fill(legend.Gradient, t => Precipitation(Mathf.Pow(2f, Mathf.Lerp(-3.32f, 3f, t))));
                    Ticks(ref legend, new[] { 0.1f, 0.3f, 1f, 2f, 4f, 8f }, v => Mathf.InverseLerp(-3.32f, 3f, Mathf.Log(v, 2f)), v => "x" + v.ToString("0.#"));
                    legend.Note = "Relative index, not mm/year";
                    break;
                case PlanetDebugView.ClimateWinds:
                    legend.Title = "Prevailing wind speed (arrows = direction)";
                    Fill(legend.Gradient, t => WindBackground(t * 1.2f, false));
                    Ticks(ref legend, new[] { 0f, 0.6f, 1.2f }, v => v / 1.2f, v => v == 0f ? "calm" : v < 1f ? "moderate" : "strong");
                    legend.Note = "Direction colors: Planet > Climate Wind Direction Colors (east red, north green, west cyan, south violet)";
                    break;
                case PlanetDebugView.ClimateContinentality:
                    legend.Title = "Distance from the sea (km)";
                    Fill(legend.Gradient, t => Continentality(t * 2500f, settings.oceanInfluenceDistance));
                    Ticks(ref legend, new[] { 0f, 500f, 1000f, 1500f, 2000f, 2500f }, v => v / 2500f, v => v.ToString("0"));
                    legend.Note = "Ocean navy · bands every 500 km";
                    break;
                default:
                    legend.Title = "";
                    legend.Note = "";
                    legend.TickPositions = new float[0];
                    legend.TickLabels = new string[0];
                    break;
            }
            return legend;
        }

        static void Fill(Color[] gradient, System.Func<float, Color> color)
        {
            for (int i = 0; i < gradient.Length; i++)
            {
                gradient[i] = color(i / (gradient.Length - 1f));
            }
        }

        static void Ticks(ref Legend legend, float[] values, System.Func<float, float> position, System.Func<float, string> label)
        {
            legend.TickPositions = new float[values.Length];
            legend.TickLabels = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                legend.TickPositions[i] = Mathf.Clamp01(position(values[i]));
                legend.TickLabels[i] = label(values[i]);
            }
        }

        /// <summary>Compass color of a tangent wind (east red, north green, west cyan, south violet), muted.</summary>
        static Color Compass(ClimateResult climate, Vector3 direction, Vector3 wind)
        {
            Vector3 axis = climate.Settings.rotationAxis.normalized;
            Vector3 east = Vector3.Cross(axis, direction);
            if (east.sqrMagnitude < 1e-10f || wind.sqrMagnitude < 1e-10f)
            {
                return new Color(0.2f, 0.2f, 0.2f);
            }
            east.Normalize();
            Vector3 north = Vector3.Cross(direction, east);
            float angle = Mathf.Atan2(Vector3.Dot(wind, north), Vector3.Dot(wind, east));
            float hue = Mathf.Repeat(angle / (2f * Mathf.PI), 1f);
            float speed = Mathf.Clamp01(wind.magnitude / 1.2f);
            return Color.HSVToRGB(hue, 0.45f, 0.3f + 0.25f * speed);
        }

        /// <summary>Darkens a thin band at every integer of <paramref name="value"/> (iso-lines).</summary>
        static Color Band(Color color, float value, float strength)
        {
            float f = value - Mathf.Floor(value);
            return f < 0.05f ? color * strength : color;
        }

        static Color Ramp(Color[] stops, float[] keys, float value)
        {
            if (value <= keys[0])
            {
                return stops[0];
            }
            for (int i = 1; i < keys.Length; i++)
            {
                if (value <= keys[i])
                {
                    return Color.Lerp(stops[i - 1], stops[i], (value - keys[i - 1]) / (keys[i] - keys[i - 1]));
                }
            }
            return stops[stops.Length - 1];
        }
    }
}
