using System.Text;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Colors and legends of the biome debug views (rendered unlit, sRGB). Analytical views, not a terrain
    /// shader. Land and sea come from the render vertex; biome ids are never interpolated (see
    /// <see cref="BiomeResult.SampleBiome"/>), continuous variables are.
    /// </summary>
    public static class BiomeDebugColors
    {
        static readonly Color[] Palette =
        {
            new Color(0.06f, 0.1f, 0.22f), // Ocean (neutral navy)
            new Color(0.02f, 0.4f, 0.22f), // Tropical rainforest: deep emerald
            new Color(0.5f, 0.65f, 0.2f), // Tropical seasonal forest / savanna: olive / yellow-green
            new Color(0.9f, 0.78f, 0.5f), // Hot desert: sand
            new Color(0.62f, 0.62f, 0.32f), // Dry woodland / shrubland: sage olive
            new Color(0.78f, 0.66f, 0.38f), // Steppe: muted golden brown
            new Color(0.75f, 0.7f, 0.62f), // Cold desert: grey ochre
            new Color(0.55f, 0.8f, 0.38f), // Temperate grassland: fresh green
            new Color(0.2f, 0.55f, 0.22f), // Temperate forest: medium forest green
            new Color(0.08f, 0.42f, 0.4f), // Temperate rainforest: deep blue-green
            new Color(0.12f, 0.32f, 0.26f), // Boreal forest: dark cool green
            new Color(0.58f, 0.64f, 0.55f), // Tundra: muted grey-green
            new Color(0.48f, 0.5f, 0.55f), // Alpine: slate
            new Color(0.88f, 0.94f, 0.98f), // Ice / snow potential: pale blue-white
        };

        static readonly Color[] AridityStops =
        {
            new Color(0.45f, 0.15f, 0.08f), // hyper-arid
            new Color(0.88f, 0.55f, 0.25f), // arid
            new Color(0.95f, 0.85f, 0.45f), // semi-arid
            new Color(0.72f, 0.85f, 0.45f), // sub-humid
            new Color(0.3f, 0.65f, 0.35f), // humid
            new Color(0.1f, 0.4f, 0.55f), // per-humid
        };
        // ln of the aridity index at each stop: 0.03, 0.12, 0.35, 0.6, 1.3, 3.
        static readonly float[] AridityKeys = { -3.5f, -2.12f, -1.05f, -0.51f, 0.26f, 1.1f };

        static readonly Color[] SuitabilityStops =
        {
            new Color(0.15f, 0.08f, 0.3f),
            new Color(0.25f, 0.45f, 0.6f),
            new Color(0.4f, 0.75f, 0.45f),
            new Color(0.95f, 0.9f, 0.35f),
        };
        static readonly float[] SuitabilityKeys = { 0.2f, 0.45f, 0.7f, 1f };

        static readonly Color EcotoneColor = new Color(1f, 0.5f, 0.1f);

        public static Color BiomeColor(Biome biome) => Palette[(int)biome];

        public static Color Aridity(float index) => Ramp(AridityStops, AridityKeys, Mathf.Log(Mathf.Max(index, 1e-3f)));

        public static Color Suitability(float score) => Ramp(SuitabilityStops, SuitabilityKeys, score);

        public static Color Evaluate(BiomeResult biomes, PlanetDebugView view, Vector3 direction, float elevation)
        {
            if (!(elevation > biomes.Climate.SeaLevel))
            {
                return Palette[0];
            }
            switch (view)
            {
                case PlanetDebugView.BiomeMap:
                    return BiomeColor(biomes.SampleBiome(direction));
                case PlanetDebugView.BiomeSuitability:
                    return Suitability(biomes.SampleLand(biomes.DominantScore, direction));
                case PlanetDebugView.BiomeTransitions:
                {
                    Color dimmed = Color.Lerp(BiomeColor(biomes.SampleBiome(direction)), new Color(0.35f, 0.35f, 0.35f), 0.6f);
                    float margin = biomes.SampleLand(biomes.Margin, direction);
                    return Color.Lerp(EcotoneColor, dimmed, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.3f, margin)));
                }
                case PlanetDebugView.BiomeAridity:
                    return Aridity(biomes.SampleLand(biomes.Aridity, direction));
                default:
                    return Color.magenta;
            }
        }

        /// <summary>Color-bar legend of a continuous biome view (the map view uses <see cref="CategoryLegend"/>).</summary>
        public static ClimateDebugColors.Legend GetLegend(PlanetDebugView view)
        {
            var legend = new ClimateDebugColors.Legend { Gradient = new Color[128] };
            switch (view)
            {
                case PlanetDebugView.BiomeSuitability:
                    legend.Title = "Suitability of the dominant biome";
                    Fill(legend.Gradient, t => Suitability(Mathf.Lerp(0.2f, 1f, t)));
                    Ticks(ref legend, new[] { 0.2f, 0.4f, 0.6f, 0.8f, 1f }, v => (v - 0.2f) / 0.8f, v => v.ToString("0.0"));
                    legend.Note = "Normalized: the biome's share of all suitabilities in the cell (1 = unambiguous)";
                    break;
                case PlanetDebugView.BiomeTransitions:
                    legend.Title = "Ecotones: margin between the two best biomes";
                    Fill(legend.Gradient, t => Color.Lerp(EcotoneColor, new Color(0.35f, 0.35f, 0.35f), Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.3f, t * 0.4f))));
                    Ticks(ref legend, new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f }, v => v / 0.4f, v => v.ToString("0.0"));
                    legend.Note = "Orange = ecotone (margin < 0.15), dimmed biome colors = confident";
                    break;
                case PlanetDebugView.BiomeAridity:
                    legend.Title = "Aridity index P / evaporative demand (log)";
                    Fill(legend.Gradient, t => Aridity(Mathf.Exp(Mathf.Lerp(-3.5f, 1.1f, t))));
                    Ticks(ref legend, new[] { 0.05f, 0.2f, 0.5f, 1f, 2f }, v => Mathf.InverseLerp(-3.5f, 1.1f, Mathf.Log(v)), v => v.ToString("0.##"));
                    legend.Note = "Hyper-arid < 0.05 < arid < 0.2 < semi-arid < 0.5 < sub-humid < 0.65 < humid (relative, not mm)";
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

        /// <summary>Rich-text legend of the biome palette (colored squares), one compact block.</summary>
        public static string CategoryLegend()
        {
            var text = new StringBuilder();
            for (int b = 1; b < Biomes.Count; b++)
            {
                text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Palette[b])).Append(">■</color> ")
                    .Append(Biomes.Name((Biome)b)).Append(b == Biomes.Count - 1 ? "" : "   ");
            }
            return text.ToString();
        }

        static void Fill(Color[] gradient, System.Func<float, Color> color)
        {
            for (int i = 0; i < gradient.Length; i++)
            {
                gradient[i] = color(i / (gradient.Length - 1f));
            }
        }

        static void Ticks(ref ClimateDebugColors.Legend legend, float[] values, System.Func<float, float> position, System.Func<float, string> label)
        {
            legend.TickPositions = new float[values.Length];
            legend.TickLabels = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                legend.TickPositions[i] = Mathf.Clamp01(position(values[i]));
                legend.TickLabels[i] = label(values[i]);
            }
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
