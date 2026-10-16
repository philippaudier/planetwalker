using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>Data-reading colors for the lake debug views (rendered unlit). Colors are sRGB.</summary>
    public static class LakeDebugColors
    {
        public static readonly Color AcceptedColor = new Color(0.25f, 0.85f, 1f);
        public static readonly Color OpenLakeColor = new Color(0.15f, 0.5f, 1f);
        public static readonly Color ClosedLakeColor = new Color(1f, 0.55f, 0.1f);
        static readonly Color ClosedBasinColor = new Color(0.55f, 0.4f, 0.25f);
        static readonly Color BasinFloorColor = new Color(0.45f, 0.6f, 0.65f);
        static readonly Color BelowThresholdColor = new Color(0.9f, 0.45f, 0.2f);
        static readonly Color TooSmallColor = new Color(0.5f, 0.35f, 0.3f);
        static readonly Color TooLargeColor = new Color(0.9f, 0.15f, 0.15f);
        static readonly Color NestedColor = new Color(0.65f, 0.35f, 0.85f);
        static readonly Color TooCloseColor = new Color(0.95f, 0.85f, 0.2f);
        static readonly Color ShallowColor = new Color(0.65f, 0.95f, 0.95f);
        static readonly Color DeepColor = new Color(0.02f, 0.08f, 0.45f);
        static readonly Color OutletColor = new Color(1f, 0.2f, 0.85f);
        static readonly Color InflowColor = new Color(0.2f, 1f, 0.3f);
        static readonly Color OutflowColor = new Color(1f, 0.9f, 0.15f);
        static readonly Color RiverColor = new Color(0.85f, 0.88f, 0.92f);
        static readonly Color InactiveRiverColor = new Color(0.55f, 0.12f, 0.12f);
        static readonly Color ConnectorColor = new Color(0.2f, 0.95f, 0.95f);

        static readonly Color ShoreWaterColor = new Color(0.15f, 0.4f, 0.95f);
        static readonly Color FragmentColor = new Color(0.1f, 0.75f, 0.7f);
        static readonly Color ShoreLineColor = new Color(1f, 1f, 1f);
        static readonly Color BasinCutColor = new Color(1f, 0.55f, 0.05f);
        static readonly Color RejectedColor = new Color(0.6f, 0.25f, 0.85f);
        static readonly Color DroppedColor = new Color(1f, 0.5f, 0.75f);
        static readonly Color ProblemColor = new Color(1f, 1f, 0.1f);
        static readonly Color FootprintBoundaryColor = new Color(0.05f, 0.45f, 0.5f);
        static readonly Color UnresolvedColor = new Color(1f, 0.1f, 0.1f);
        static readonly Color ExposedColor = new Color(1f, 0.35f, 0.2f);

        /// <summary>
        /// Shoreline view: measured flags of the lake surface build and of the ribbon transitions on a
        /// render vertex (markers first, then problems, cuts, shoreline, rejected / dropped water,
        /// water), else the lake footprint boundary cells, else gray land.
        /// </summary>
        public static Color Shoreline(LakeSet lakes, int cell, Color terrainColor, ShorelineFlags flags)
        {
            if ((flags & ShorelineFlags.Unresolved) != 0) return UnresolvedColor;
            if ((flags & ShorelineFlags.Inlet) != 0) return InflowColor;
            if ((flags & ShorelineFlags.Outlet) != 0) return OutletColor;
            if ((flags & ShorelineFlags.Exposed) != 0) return ExposedColor;
            if ((flags & ShorelineFlags.Problem) != 0) return ProblemColor;
            if ((flags & ShorelineFlags.BasinCut) != 0) return BasinCutColor;
            if ((flags & ShorelineFlags.Shore) != 0) return ShoreLineColor;
            if ((flags & ShorelineFlags.Rejected) != 0) return RejectedColor;
            if ((flags & ShorelineFlags.Dropped) != 0) return DroppedColor;
            if ((flags & ShorelineFlags.Fragment) != 0) return FragmentColor;
            if ((flags & ShorelineFlags.Water) != 0) return ShoreWaterColor;
            int lake = lakes.GetLakeOfCell(cell);
            if (lake >= 0)
            {
                HydrologyGrid grid = lakes.Hydrology.Grid;
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    if (lakes.GetLakeOfCell(grid.GetNeighbor(k)) != lake)
                    {
                        return FootprintBoundaryColor;
                    }
                }
            }
            return Land(terrainColor);
        }

        static Color Land(Color terrainColor)
        {
            float luminance = 0.3f * terrainColor.r + 0.59f * terrainColor.g + 0.11f * terrainColor.b;
            float gray = 0.12f + 0.45f * luminance;
            return new Color(gray, gray, gray);
        }

        /// <summary>
        /// Lake of the cell, or (when the render mesh is coarser than the grid) of a neighbour, so
        /// that small lakes stay visible between vertices.
        /// </summary>
        static int LakeNear(LakeSet lakes, int cell, bool dilate)
        {
            int lake = lakes.GetLakeOfCell(cell);
            if (lake >= 0 || !dilate)
            {
                return lake;
            }
            HydrologyGrid grid = lakes.Hydrology.Grid;
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                lake = lakes.GetLakeOfCell(grid.GetNeighbor(k));
                if (lake >= 0)
                {
                    return lake;
                }
            }
            return -1;
        }

        /// <summary>
        /// Candidates view: accepted lakes cyan, dry floors of closed basins gray-blue, rejected
        /// candidates (smallest depression containing the cell at its spill level) by reason:
        /// orange below the abundance threshold, brown too small, red too large, violet nested in /
        /// enclosing an accepted lake, yellow too close to another lake. Brightness alternates with
        /// the nesting level so nested depressions are distinguishable.
        /// </summary>
        public static Color Candidates(LakeSet lakes, int cell, Color terrainColor, bool dilate)
        {
            HydrologyResult hydrology = lakes.Hydrology;
            if (!hydrology.IsLand(cell))
            {
                return HydrologyDebugColors.OceanColor;
            }
            if (LakeNear(lakes, cell, dilate) >= 0)
            {
                return AcceptedColor;
            }
            if (lakes.GetBasinLakeOfCell(cell) >= 0)
            {
                return BasinFloorColor;
            }
            DepressionHierarchy depressions = lakes.Depressions;
            int node = depressions.GetDeepestNode(cell);
            if (node < 0 || !depressions.IsCandidate(node))
            {
                return Land(terrainColor);
            }
            Color color;
            switch (lakes.GetCandidateStatus(node))
            {
                case LakeCandidateStatus.BelowThreshold: color = BelowThresholdColor; break;
                case LakeCandidateStatus.TooLarge: color = TooLargeColor; break;
                case LakeCandidateStatus.Nested: color = NestedColor; break;
                case LakeCandidateStatus.TooClose: color = TooCloseColor; break;
                default: color = TooSmallColor; break;
            }
            return depressions.GetNode(node).NestingLevel % 2 == 0 ? color : color * 0.7f;
        }

        /// <summary>Depth view: water depth from pale cyan (shallow) to deep blue (half the depth of
        /// the deepest lake of the planet and more); land gray, ocean dark.</summary>
        public static Color Depth(LakeSet lakes, int cell, Color terrainColor, float maxDepth, bool dilate)
        {
            if (!lakes.Hydrology.IsLand(cell))
            {
                return HydrologyDebugColors.OceanColor;
            }
            int lake = lakes.GetLakeOfCell(cell);
            if (lake < 0 && LakeNear(lakes, cell, dilate) >= 0)
            {
                return ShallowColor;
            }
            if (lake < 0)
            {
                return Land(terrainColor);
            }
            // Square-root scale saturating at half the deepest lake: shallow lakes stay readable.
            float t = Mathf.Sqrt(Mathf.Clamp01(lakes.GetDepth(cell) / Mathf.Max(0.5f * maxDepth, 1e-9f)));
            return Color.Lerp(ShallowColor, DeepColor, t);
        }

        /// <summary>Classification view: open lakes blue, closed lakes orange (their dry basin
        /// floor brown), land gray, ocean dark navy.</summary>
        public static Color Classification(LakeSet lakes, int cell, Color terrainColor, bool dilate)
        {
            if (!lakes.Hydrology.IsLand(cell))
            {
                return HydrologyDebugColors.OceanColor;
            }
            int lake = LakeNear(lakes, cell, dilate);
            if (lake >= 0)
            {
                return lakes.GetLake(lake).Class == LakeClass.Open ? OpenLakeColor : ClosedLakeColor;
            }
            return lakes.GetBasinLakeOfCell(cell) >= 0 ? ClosedBasinColor : Land(terrainColor);
        }

        /// <summary>
        /// Connectivity view: lake footprints light blue, outlets magenta, inflow entry cells green,
        /// outflow reaches yellow, other visible rivers light gray, channels captured by a closed
        /// lake dark red, closed-basin connectors cyan (see <see cref="SurfaceWaterRole"/>).
        /// </summary>
        public static Color Connectivity(LakeRiverLayer layer, int cell, Color terrainColor, bool dilate)
        {
            LakeSet lakes = layer.Lakes;
            HydrologyResult hydrology = lakes.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            if (!hydrology.IsLand(cell))
            {
                return HydrologyDebugColors.OceanColor;
            }
            SurfaceWaterRole role = layer.GetCellRole(cell);
            if (dilate)
            {
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    SurfaceWaterRole other = layer.GetCellRole(grid.GetNeighbor(k));
                    role = other > role ? other : role;
                }
            }
            switch (role)
            {
                case SurfaceWaterRole.Outlet: return OutletColor;
                case SurfaceWaterRole.Inflow: return InflowColor;
                case SurfaceWaterRole.Connector: return ConnectorColor;
                case SurfaceWaterRole.OutflowRiver: return OutflowColor;
                case SurfaceWaterRole.River: return RiverColor;
                case SurfaceWaterRole.CapturedChannel: return InactiveRiverColor;
            }
            if (lakes.GetLakeOfCell(cell) >= 0)
            {
                return AcceptedColor * 0.8f;
            }
            return lakes.GetBasinLakeOfCell(cell) >= 0 ? ClosedBasinColor : Land(terrainColor) * 0.8f;
        }
    }
}
