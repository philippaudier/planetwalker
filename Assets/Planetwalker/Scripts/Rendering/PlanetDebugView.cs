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

        /// <summary>Lake shoreline validation (V0.1.1D-B.2 geometry): water vertices blue (detached
        /// parts teal), shoreline edges white, basin-boundary cuts orange, low terrain left dry outside
        /// the lake region purple, dropped one-vertex puddles pink, problem triangles yellow, lake
        /// footprint boundary cells dark cyan; river inlets green, outlets magenta, unresolved
        /// transitions red, exposed transition ends orange-red. Water is hidden, ribbons drawn.</summary>
        LakeShoreline,

        /// <summary>Climate: annual-mean surface temperature on the classic climatology scale (white -50 C,
        /// magenta, purple, blue, cyan 0 C, yellow 10 C, red 20 C, dark red 30 C); ocean slightly darker.</summary>
        ClimateTemperature,

        /// <summary>Climate: atmospheric moisture index, dry (brown) to humid (blue).</summary>
        ClimateMoisture,

        /// <summary>Climate: precipitation index relative to the planet mean, arid (tan) to very wet (deep blue).</summary>
        ClimatePrecipitation,

        /// <summary>Climate: prevailing winds, compass color of the heading (brightness = speed) plus arrows.</summary>
        ClimateWinds,

        /// <summary>Climate: ocean influence / continentality, coasts pale, interiors dark red, 500 km bands.</summary>
        ClimateContinentality,

        /// <summary>Biomes: dominant terrestrial biome (discrete palette); ocean neutral navy.</summary>
        BiomeMap,

        /// <summary>Biomes: suitability of the dominant biome (0.2 .. 1, dark = weak).</summary>
        BiomeSuitability,

        /// <summary>Biomes: ecotones, where the two best biomes are close (bright orange), over dimmed biome colors.</summary>
        BiomeTransitions,

        /// <summary>Biomes: relative aridity index P / evaporative demand (hyper-arid .. per-humid, log scale).</summary>
        BiomeAridity,

        /// <summary>Clouds (V0.1.2C): cloud cover of the deterministic cloud field (0 clear .. 1 overcast) over
        /// dark land / ocean backgrounds; before density and local detail, static (time 0).</summary>
        CloudCoverage,

        /// <summary>Clouds: structure of the cloud field: large-scale potential (gray), weather systems (orange),
        /// tropical convection (teal), system centers (red).</summary>
        CloudStructure,

        /// <summary>Surface detail (V0.2.0C): signed displacement of the regional band (blue down, red up, dark = none)
        /// over the dimmed planetary relief; ocean navy. Which layer made this landform?</summary>
        SurfaceDetailRegional,

        /// <summary>Surface detail: the local band (hills, ravines).</summary>
        SurfaceDetailLocal,

        /// <summary>Surface detail: the near-surface band.</summary>
        SurfaceDetailMicro,

        /// <summary>Surface detail: the three bands combined.</summary>
        SurfaceDetailCombined,

        /// <summary>
        /// Regional landforms (V0.2.0G.1): influence weights as colors, mountain valley region red (valley corridors
        /// brighter), plateau green, coastal plain blue; land without a landform dark gray.
        /// </summary>
        LandformInfluence,

        /// <summary>Regional landforms (V0.2.0G.1): their height change, blue lowered, red raised (diverging, +-2.5 km).</summary>
        LandformDisplacement,

        /// <summary>
        /// Uniform clay over land (V0.2.0G.1): one flat diagnostic color, so the terrain is read from its shape and
        /// lighting alone (no material, biome or detail coloring).
        /// </summary>
        LandformRelief,
    }

    public static class PlanetDebugViews
    {
        /// <summary>The surface detail views (V0.2.0C): terrain only, no analysis; the adaptive terrain shows them too.</summary>
        public static bool IsSurfaceDetail(PlanetDebugView view)
        {
            return view == PlanetDebugView.SurfaceDetailRegional || view == PlanetDebugView.SurfaceDetailLocal
                || view == PlanetDebugView.SurfaceDetailMicro || view == PlanetDebugView.SurfaceDetailCombined
                || IsLandform(view);
        }

        /// <summary>
        /// Views drawn with the lit terrain material (the others are unlit): the default view and the landform relief
        /// clay (V0.2.0G.1), whose uniform color is shaded by the sun so the terrain reads from its shape alone.
        /// </summary>
        public static bool IsLit(PlanetDebugView view)
        {
            return view == PlanetDebugView.None || view == PlanetDebugView.LandformRelief;
        }

        /// <summary>The regional landform views (V0.2.0G.1), drawn like the surface detail views.</summary>
        public static bool IsLandform(PlanetDebugView view)
        {
            return view == PlanetDebugView.LandformInfluence || view == PlanetDebugView.LandformDisplacement
                || view == PlanetDebugView.LandformRelief;
        }

        /// <summary>Views that need a hydrology analysis (hydrology, river and lake views).</summary>
        public static bool NeedsHydrology(PlanetDebugView view)
        {
            return IsHydrology(view) || view == PlanetDebugView.RiverNetwork || IsLake(view);
        }

        /// <summary>The lake views (they need the lake analysis; connectivity and shoreline also the rivers).</summary>
        public static bool IsLake(PlanetDebugView view)
        {
            return view == PlanetDebugView.LakeCandidates || view == PlanetDebugView.LakeDepth
                || view == PlanetDebugView.LakeClassification || view == PlanetDebugView.LakeConnectivity
                || view == PlanetDebugView.LakeShoreline;
        }

        /// <summary>The climate views (they need the climate analysis only).</summary>
        public static bool IsClimate(PlanetDebugView view)
        {
            return view >= PlanetDebugView.ClimateTemperature && view <= PlanetDebugView.ClimateContinentality;
        }

        /// <summary>The biome views (they need the climate and the biome classification).</summary>
        public static bool IsBiome(PlanetDebugView view)
        {
            return view >= PlanetDebugView.BiomeMap && view <= PlanetDebugView.BiomeAridity;
        }

        /// <summary>The cloud views (they need the cloud field only; never the climate).</summary>
        public static bool IsCloud(PlanetDebugView view)
        {
            return view == PlanetDebugView.CloudCoverage || view == PlanetDebugView.CloudStructure;
        }

        public static bool IsHydrology(PlanetDebugView view)
        {
            return view == PlanetDebugView.DrainageBasins || view == PlanetDebugView.FlowAccumulation
                || view == PlanetDebugView.FlowDirection || view == PlanetDebugView.RiverCandidates;
        }
    }
}
