using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>The cloud data shown by the cloud debug views (deterministic part, static: time 0).</summary>
    public sealed class CloudDebugData
    {
        public CloudDebugData(CloudField field, CloudSettings settings, float threshold)
        {
            Field = field;
            Settings = settings.Clone();
            Threshold = threshold;
        }

        public CloudField Field { get; }
        public CloudSettings Settings { get; }
        public float Threshold { get; }
    }

    /// <summary>Cloud debug views (V0.1.2C): the underlying cloud data on the terrain, unlit, ocean hidden.</summary>
    public static class CloudDebugColors
    {
        static readonly Color OceanBackground = new Color(0.04f, 0.07f, 0.16f);
        static readonly Color LandBackground = new Color(0.17f, 0.15f, 0.1f);
        static readonly Color SystemColor = new Color(1f, 0.55f, 0.15f);
        static readonly Color ConvectiveColor = new Color(0.2f, 0.85f, 0.75f);
        static readonly Color CenterColor = new Color(0.95f, 0.15f, 0.2f);

        public static Color Evaluate(CloudDebugData data, PlanetDebugView view, Vector3 direction, bool land)
        {
            if (data == null)
            {
                return Color.magenta;
            }
            CloudField field = data.Field;
            Color background = land ? LandBackground : OceanBackground;
            if (view == PlanetDebugView.CloudCoverage)
            {
                // Cloud alpha of the deterministic potential (before density and local detail).
                return Color.Lerp(background, Color.white, data.Field.Coverage(direction, data.Settings, data.Threshold));
            }
            // Structure: large-scale potential (gray), weather systems (orange), convection (teal), system centers (red).
            float potential = Mathf.Clamp01(field.Sample(field.Base, direction));
            float systems = field.Sample(field.Systems, direction) * field.Sample(field.Envelope, direction);
            float convective = field.Sample(field.Convective, direction);
            Color color = Color.Lerp(background, new Color(0.62f, 0.66f, 0.72f), potential * 0.8f);
            color = Color.Lerp(color, ConvectiveColor, convective * 0.7f);
            color = Color.Lerp(color, SystemColor, Mathf.Clamp01(systems) * 0.9f);
            foreach (WeatherSystem s in field.WeatherSystems)
            {
                if (Vector3.Angle(direction, s.Center) < Mathf.Max(0.8f, s.RadiusDegrees * 0.06f))
                {
                    return CenterColor;
                }
            }
            return color;
        }

        public static ClimateDebugColors.Legend GetLegend(PlanetDebugView view, CloudDebugData data)
        {
            var legend = new ClimateDebugColors.Legend { Gradient = new Color[128] };
            if (view == PlanetDebugView.CloudCoverage)
            {
                legend.Title = "Cloud cover (deterministic part, 0 .. 1)";
                for (int i = 0; i < legend.Gradient.Length; i++)
                {
                    legend.Gradient[i] = Color.Lerp(OceanBackground, Color.white, i / (legend.Gradient.Length - 1f));
                }
                legend.TickPositions = new[] { 0f, 0.5f, 1f };
                legend.TickLabels = new[] { "clear", "0.5", "overcast" };
                string source = data != null && data.Field.ClimateInformed ? "climate-informed" : "latitude-only (no climate yet)";
                legend.Note = data == null ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Before density and local detail; static (time 0). Prior: {0}. Weather seed {1}, {2} systems",
                    source, data.Field.Seed, data.Field.WeatherSystems.Count);
                return legend;
            }
            legend.Title = "Cloud structure";
            for (int i = 0; i < legend.Gradient.Length; i++)
            {
                legend.Gradient[i] = Color.Lerp(OceanBackground, new Color(0.62f, 0.66f, 0.72f), i / (legend.Gradient.Length - 1f) * 0.8f);
            }
            legend.TickPositions = new[] { 0f, 1f };
            legend.TickLabels = new[] { "low potential", "high" };
            legend.Note = "<color=#FF8C26>■</color> weather systems   <color=#33D9BF>■</color> tropical convection   "
                + "<color=#F2263A>■</color> system centers   gray: large-scale potential (climate prior + planetary / synoptic variability)";
            return legend;
        }
    }
}
