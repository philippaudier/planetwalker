using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Surface detail debug views (V0.2.0C): the signed displacement of one band (or all) as a diverging color,
    /// blue below the planetary terrain, red above, dark gray where the band adds nothing, scaled by the band's
    /// amplitude bound; a faint elevation tint keeps the relief readable; ocean navy. Diagnostic only: reads the
    /// same canonical sampler as the terrain, never changes it.
    /// </summary>
    public static class SurfaceDetailDebugColors
    {
        static readonly Color Down = new Color(0.20f, 0.45f, 1.0f);
        static readonly Color Up = new Color(1.0f, 0.35f, 0.15f);
        static readonly Color Neutral = new Color(0.16f, 0.16f, 0.17f);
        static readonly Color Ocean = new Color(0.03f, 0.05f, 0.14f);
        static readonly Color NoDetail = new Color(0.30f, 0.28f, 0.26f);

        public static Color Evaluate(PlanetHeightGenerator height, PlanetDebugView view, Vector3 direction, bool land)
        {
            if (!land)
            {
                return Ocean;
            }
            TerrainDetail detail = height.SurfaceDetail;
            if (detail == null)
            {
                return NoDetail;
            }
            float baseElevation = height.SampleLayers(direction, out TerrainDetailSample sample);
            if (PlanetDebugViews.IsLandform(view))
            {
                return Landform(height, view, sample);
            }
            float value, scale;
            float[] bands = detail.Profile.BandAmplitude;
            switch (view)
            {
                case PlanetDebugView.SurfaceDetailRegional: value = sample.Regional; scale = bands[0]; break;
                case PlanetDebugView.SurfaceDetailLocal: value = sample.Local; scale = bands[1]; break;
                case PlanetDebugView.SurfaceDetailMicro: value = sample.Micro; scale = bands[2]; break;
                default: value = sample.Total; scale = bands[0] + bands[1] + bands[2]; break;
            }
            float t = scale > 0f ? Mathf.Clamp(value / (1.5f * scale), -1f, 1f) : 0f;
            Color color = Color.Lerp(Neutral, t < 0f ? Down : Up, Mathf.Sqrt(Mathf.Abs(t)));
            // Elevation tint: higher planetary terrain slightly brighter (relief stays readable).
            float tint = 0.85f + 0.3f * Mathf.Clamp01((baseElevation - height.SeaLevel) / 0.03f);
            return new Color(color.r * tint, color.g * tint, color.b * tint, 1f);
        }

        static readonly Color Clay = new Color(0.62f, 0.60f, 0.57f);
        static readonly Color NoLandform = new Color(0.22f, 0.22f, 0.23f);
        const float DisplacementScaleMeters = 2500f;
        const float ErosionScaleMeters = 1500f; // the deepest incision at strength 1 (V0.2.0G.2B); zero is neutral gray
        static readonly Color ErosionNeutral = new Color(0.45f, 0.45f, 0.46f);

        /// <summary>Regional landform views (V0.2.0G.1).</summary>
        static Color Landform(PlanetHeightGenerator height, PlanetDebugView view, in TerrainDetailSample sample)
        {
            if (view == PlanetDebugView.LandformRelief)
            {
                return Clay;
            }
            if (view == PlanetDebugView.ErosionDisplacement)
            {
                if (height.SurfaceDetail.Erosion == null)
                {
                    return NoLandform; // stage off
                }
                float meters = (float)(sample.Erosion * height.SurfaceDetail.Erosion.Context.Scale.RadiusMeters);
                // Linear in depth (no square root: a negligible incision must not look dramatic); blue = lowered.
                float t = Mathf.Clamp(meters / ErosionScaleMeters, -1f, 1f);
                return Color.Lerp(ErosionNeutral, t < 0f ? Down : Up, Mathf.Abs(t));
            }
            if (height.SurfaceDetail.Landforms == null)
            {
                return NoDetail;
            }
            LandformSample l = sample.Landforms;
            if (view == PlanetDebugView.LandformDisplacement)
            {
                float meters = (float)(l.Displacement * height.SurfaceDetail.Landforms.RadiusMeters);
                float t = Mathf.Clamp(meters / DisplacementScaleMeters, -1f, 1f);
                return Color.Lerp(Neutral, t < 0f ? Down : Up, Mathf.Sqrt(Mathf.Abs(t)));
            }
            // Influence: additive weights over a dark base; valley corridors lit inside the valley region.
            var color = new Color(
                0.25f * l.Valley + 0.75f * l.Corridor,
                0.85f * l.Plateau,
                0.95f * l.CoastalPlain);
            float total = Mathf.Clamp01(l.Valley + l.Plateau + l.CoastalPlain);
            return Color.Lerp(NoLandform, new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b)) + NoLandform * 0.4f, total);
        }
    }
}
