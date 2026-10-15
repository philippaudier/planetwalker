namespace Planetwalker.Rendering
{
    /// <summary>Development-only terrain visualizations.</summary>
    public enum PlanetDebugView
    {
        /// <summary>Normal elevation-based colors.</summary>
        None,

        /// <summary>
        /// Continental field as grayscale (ocean hidden): dark gray abyssal basins,
        /// gradients across shelves and coasts, white continental interiors.
        /// </summary>
        ContinentalMask,

        /// <summary>
        /// Mountain influence as grayscale (ocean hidden): black = none, gray = foothills
        /// and province highlands, white = range cores. Oceans are tinted dark blue.
        /// </summary>
        MountainProvinces,

        /// <summary>
        /// Mountain range centerlines in red over the dimmed mountain influence. Shows the
        /// full range geometry, including parts suppressed over the ocean.
        /// </summary>
        MountainRanges,

        /// <summary>Hydrology: each drainage basin in a distinct color (neighbours differ).</summary>
        DrainageBasins,

        /// <summary>Hydrology: drained area on a logarithmic scale (dark = small, white = large).</summary>
        FlowAccumulation,

        /// <summary>Hydrology: flow direction as a compass color wheel (east red, north
        /// yellow-green, west cyan, south violet).</summary>
        FlowDirection,

        /// <summary>Hydrology: river candidates (drained area above the threshold) in light
        /// blue over desaturated terrain.</summary>
        RiverCandidates,

        /// <summary>River network topology: rivers by Strahler order (blue to white, main stems
        /// brighter), sources green, confluences yellow, mouths magenta, sinks red.</summary>
        RiverNetwork,

        /// <summary>Lake candidates: accepted lakes cyan, rejected depressions by reason (orange below
        /// the abundance threshold, brown too small, red too large, violet nested, yellow too close).</summary>
        LakeCandidates,

        /// <summary>Lake water depth from pale cyan (shallow) to deep blue.</summary>
        LakeDepth,

        /// <summary>Open lakes blue, closed lakes orange (dry basin floor brown), land gray, ocean navy.</summary>
        LakeClassification,

        /// <summary>Lake connectivity: outlets magenta, inflows green, outflow rivers yellow, rivers
        /// gray, captured channels dark red, closed-basin connectors cyan.</summary>
        LakeConnectivity,
    }

    public static class PlanetDebugViews
    {
        /// <summary>Views that need a hydrology analysis (hydrology, river and lake views).</summary>
        public static bool NeedsHydrology(PlanetDebugView view)
        {
            return IsHydrology(view) || view == PlanetDebugView.RiverNetwork || IsLake(view);
        }

        /// <summary>The four lake views (they need the lake analysis; connectivity also the rivers).</summary>
        public static bool IsLake(PlanetDebugView view)
        {
            return view == PlanetDebugView.LakeCandidates || view == PlanetDebugView.LakeDepth
                || view == PlanetDebugView.LakeClassification || view == PlanetDebugView.LakeConnectivity;
        }

        public static bool IsHydrology(PlanetDebugView view)
        {
            return view == PlanetDebugView.DrainageBasins || view == PlanetDebugView.FlowAccumulation
                || view == PlanetDebugView.FlowDirection || view == PlanetDebugView.RiverCandidates;
        }
    }
}
