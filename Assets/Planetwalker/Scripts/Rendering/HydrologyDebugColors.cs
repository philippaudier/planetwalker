using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Data-reading colors for the hydrology debug views (rendered unlit). Colors are sRGB.
    /// </summary>
    public static class HydrologyDebugColors
    {
        public static readonly Color OceanColor = new Color(0.04f, 0.07f, 0.2f);
        static readonly Color EndorheicOutletColor = new Color(1f, 0.1f, 0.6f);
        static readonly Color RiverColor = new Color(0.55f, 0.88f, 1f);

        // Saturated, well separated hues; none is close to the ocean color.
        static readonly Color[] BasinPalette =
        {
            new Color(0.90f, 0.30f, 0.25f), new Color(0.95f, 0.65f, 0.20f), new Color(0.85f, 0.85f, 0.25f),
            new Color(0.45f, 0.80f, 0.30f), new Color(0.20f, 0.70f, 0.55f), new Color(0.30f, 0.80f, 0.90f),
            new Color(0.55f, 0.45f, 0.95f), new Color(0.85f, 0.40f, 0.85f), new Color(0.95f, 0.60f, 0.70f),
            new Color(0.70f, 0.55f, 0.35f),
        };

        // Log scale of the drained area (fraction of the planet surface): 1e-6 .. 1e-1.
        const float LogMin = -6f;
        const float LogMax = -1f;

        public static Color Basin(HydrologyResult hydrology, int cell)
        {
            if (!hydrology.IsLand(cell))
            {
                return OceanColor;
            }
            int basin = hydrology.GetBasin(cell);
            Color color = BasinPalette[hydrology.GetBasinColorIndex(basin) % BasinPalette.Length];
            // Slight per-basin brightness variation, deterministic.
            float shade = 0.85f + 0.15f * PlanetNoise.Hash01(basin, 3);
            return color * shade;
        }

        /// <param name="dilate">Use the maximum over the cell's neighbourhood, so that one-cell
        /// wide drainage lines stay visible when the render mesh is coarser than the grid.</param>
        public static Color Accumulation(HydrologyResult hydrology, int cell, bool dilate)
        {
            if (!hydrology.IsLand(cell))
            {
                return OceanColor;
            }
            double fraction = dilate ? MaxLandAccumulationFraction(hydrology, cell) : hydrology.GetAccumulationFraction(cell);
            float t = Mathf.Clamp01(((float)System.Math.Log10(System.Math.Max(fraction, 1e-12)) - LogMin) / (LogMax - LogMin));
            return AccumulationRamp(t);
        }

        /// <summary>Flow direction as a compass color wheel (east = red, north = yellow-green,
        /// west = cyan, south = violet). Outlets and the endorheic sink are magenta.</summary>
        public static Color FlowDirection(HydrologyResult hydrology, int cell)
        {
            if (!hydrology.IsLand(cell))
            {
                return OceanColor;
            }
            int down = hydrology.GetReceiver(cell);
            if (down < 0)
            {
                return EndorheicOutletColor;
            }
            Vector3 p = hydrology.Grid.GetCenter(cell);
            Vector3 flow = hydrology.Grid.GetCenter(down) - p;
            Vector3 east = Vector3.Cross(Vector3.up, p);
            if (east.sqrMagnitude < 1e-8f)
            {
                east = Vector3.Cross(Vector3.forward, p);
            }
            east.Normalize();
            Vector3 north = Vector3.Cross(p, east);
            float angle = Mathf.Atan2(Vector3.Dot(flow, north), Vector3.Dot(flow, east));
            float hue = angle / (2f * Mathf.PI);
            return Hsv(hue < 0f ? hue + 1f : hue, 0.75f, 0.9f);
        }

        /// <summary>River candidates in light blue over a desaturated terrain color.</summary>
        public static Color RiverCandidates(HydrologyResult hydrology, int cell, float threshold, Color terrainColor, bool dilate)
        {
            if (!hydrology.IsLand(cell))
            {
                return OceanColor;
            }
            float luminance = 0.3f * terrainColor.r + 0.59f * terrainColor.g + 0.11f * terrainColor.b;
            var background = new Color(0.15f + 0.6f * luminance, 0.15f + 0.6f * luminance, 0.15f + 0.6f * luminance);
            double fraction = dilate ? MaxLandAccumulationFraction(hydrology, cell) : hydrology.GetAccumulationFraction(cell);
            if (fraction < threshold)
            {
                return background;
            }
            // Brighter and whiter for larger drainage axes.
            float strength = Mathf.Clamp01((float)System.Math.Log10(fraction / threshold) / 2f);
            return Color.Lerp(RiverColor * 0.85f, Color.Lerp(RiverColor, Color.white, 0.5f), strength);
        }

        static readonly Color SourceColor = new Color(0.25f, 0.95f, 0.35f);
        static readonly Color ConfluenceColor = new Color(1f, 0.85f, 0.15f);
        static readonly Color MouthColor = new Color(1f, 0.25f, 0.85f);
        static readonly Color SinkColor = new Color(1f, 0.15f, 0.1f);
        static readonly Color CrossingColor = new Color(1f, 0.5f, 0.05f);
        static readonly Color UnresolvedMouthColor = Color.white;

        /// <summary>
        /// River network topology view (see <see cref="PlanetDebugView.RiverNetwork"/>), plus
        /// geometry diagnostics: non-topological crossings in orange, ocean mouths whose ribbon
        /// could not reach the rendered water in white (needs <paramref name="ribbons"/>).
        /// </summary>
        public static Color RiverNetwork(RiverNetwork network, int cell, Color terrainColor, bool dilate,
            RiverRibbonBuilder ribbons = null)
        {
            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            if (ribbons != null && NearMarker(grid, cell, dilate, ribbons.IsUnresolvedMouthCell))
            {
                return UnresolvedMouthColor;
            }
            if (!hydrology.IsLand(cell))
            {
                return OceanColor;
            }
            if (NearMarker(grid, cell, dilate, network.IsCrossingCell))
            {
                return CrossingColor;
            }

            // Node markers take priority, searched in the neighbourhood when the mesh is coarse.
            RiverNodeKind markers = MarkerKind(network, cell);
            int river = network.GetNodeOfCell(cell);
            if (dilate)
            {
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    markers |= MarkerKind(network, other);
                    int otherNode = network.GetNodeOfCell(other);
                    if (otherNode >= 0 && (river < 0 || network.GetStrahler(otherNode) > network.GetStrahler(river)))
                    {
                        river = otherNode;
                    }
                }
            }
            if ((markers & RiverNodeKind.Sink) != 0)
            {
                return SinkColor;
            }
            if ((markers & RiverNodeKind.Mouth) != 0)
            {
                return MouthColor;
            }
            if ((markers & RiverNodeKind.Confluence) != 0)
            {
                return ConfluenceColor;
            }
            if ((markers & RiverNodeKind.Source) != 0)
            {
                return SourceColor;
            }
            if (river < 0)
            {
                float luminance = 0.3f * terrainColor.r + 0.59f * terrainColor.g + 0.11f * terrainColor.b;
                float gray = 0.1f + 0.45f * luminance;
                return new Color(gray, gray, gray);
            }
            float t = Mathf.Clamp01((network.GetStrahler(river) - 1f) / Mathf.Max(1f, network.MaxStrahler - 1f));
            Color color = Color.Lerp(new Color(0.15f, 0.35f, 0.95f), new Color(0.75f, 0.95f, 1f), t);
            bool mainStem = network.GetRiver(network.GetRiverOfNode(river)).ParentRiver < 0;
            return mainStem ? Color.Lerp(color, Color.white, 0.25f) : color * 0.85f;
        }

        static bool NearMarker(HydrologyGrid grid, int cell, bool dilate, System.Func<int, bool> isMarked)
        {
            if (isMarked(cell))
            {
                return true;
            }
            if (dilate)
            {
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    if (isMarked(grid.GetNeighbor(k)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static RiverNodeKind MarkerKind(RiverNetwork network, int cell)
        {
            int node = network.GetNodeOfCell(cell);
            return node >= 0 ? network.GetNodeKind(node) : RiverNodeKind.None;
        }

        static double MaxLandAccumulationFraction(HydrologyResult hydrology, int cell)
        {
            HydrologyGrid grid = hydrology.Grid;
            double max = hydrology.GetAccumulationFraction(cell);
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int other = grid.GetNeighbor(k);
                if (hydrology.IsLand(other))
                {
                    max = System.Math.Max(max, hydrology.GetAccumulationFraction(other));
                }
            }
            return max;
        }

        static Color AccumulationRamp(float t)
        {
            var dark = new Color(0.06f, 0.06f, 0.09f);
            var blue = new Color(0.12f, 0.3f, 0.75f);
            var cyan = new Color(0.4f, 0.85f, 1f);
            if (t < 0.5f)
            {
                return Color.Lerp(dark, blue, t / 0.5f);
            }
            if (t < 0.8f)
            {
                return Color.Lerp(blue, cyan, (t - 0.5f) / 0.3f);
            }
            return Color.Lerp(cyan, Color.white, (t - 0.8f) / 0.2f);
        }

        static Color Hsv(float h, float s, float v)
        {
            float i = Mathf.Floor(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (((int)i % 6 + 6) % 6)
            {
                case 0: return new Color(v, t, p);
                case 1: return new Color(q, v, p);
                case 2: return new Color(p, v, t);
                case 3: return new Color(p, q, v);
                case 4: return new Color(t, p, v);
                default: return new Color(v, p, q);
            }
        }
    }
}
