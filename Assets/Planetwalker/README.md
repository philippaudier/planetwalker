# Planetwalker — Genesis V0.1.1D-B.1 (Procedural Lakes)

## Launch

1. Open the project in Unity 6000.3 (URP).
2. Menu **Planetwalker > Setup Genesis Scene** (once). It creates
   `Materials/PlanetTerrain.mat`, `Materials/PlanetOcean.mat` and `Scenes/Genesis.unity`,
   and adds the scene to the build settings. It asks before overwriting an existing scene.
3. Open `Scenes/Genesis.unity` (opened automatically after setup) and press **Play**.

A scene that only contains a `PlanetBootstrap` component also works: missing objects
(planet, star, camera, UI) are created at startup.

Controls: **LMB drag** rotate · **mouse wheel** zoom · **R** reset view.

Development views: select the `Planet` object in Play mode and set **Debug View**:
- `ContinentalMask`: dark gray = ocean basins, white = continental interiors (ocean hidden).
- `MountainProvinces`: black = no mountains, gray = foothills/highlands, white = range cores.
- `MountainRanges`: range centerlines in red over the dimmed mountain influence.
- `DrainageBasins`, `FlowAccumulation` (log scale), `FlowDirection` (compass colors: east red,
  north yellow-green, west cyan, south violet) and `RiverCandidates` (light blue over gray).
  Also reachable from the panel: **Hydrology > Show Hydrology** and the **View** button.

- `RiverNetwork`: rivers by Strahler order (blue to white, main stems brighter), sources green,
  confluences yellow, mouths magenta, sinks red; geometry diagnostics: non-topological
  crossings orange, ocean mouths that could not reach the rendered water white. Ribbons are
  drawn on top when Show Rivers is on. Panel: **Rivers > Debug River Network**.

- Lake views (panel **Lakes > Debug** button cycles them): `LakeCandidates` (accepted lakes cyan;
  rejected depressions orange = below the abundance threshold, brown = too small, red = too large,
  violet = nested in / enclosing a lake, yellow = too close; brightness alternates with nesting),
  `LakeDepth` (pale cyan shallow to deep blue), `LakeClassification` (open blue, closed orange, dry
  floor of closed basins brown, land gray, ocean navy), `LakeConnectivity` (outlets magenta, inflows
  green, outflow rivers yellow, rivers gray, channels captured by closed lakes dark red, closed-basin
  connectors cyan; ribbons drawn on top when Show Rivers is on).

All development views are unlit (`PlanetDebugUnlit` shader) and hide the ocean sphere.

Rivers on the artistic view: panel **Rivers > Show Rivers** (off by default, like Show
Hydrology, because it adds the hydrology analysis to every regeneration).
Lakes: panel **Lakes > Show Lakes** (off by default for the same reason). Lake Abundance, Lake
Minimum Area (% of the planet surface) and Closed Lake Level only change the selection (the
depression analysis and the hydrology are reused).

## Architecture

| Layer | Files | Role |
|---|---|---|
| Data | `Core/PlanetDefinition` | Serializable configuration, documented ranges, no logic |
| Data | `Core/PlanetInstance` | Immutable generated planet: definition snapshot + height function + stats |
| Generation | `Generation/PlanetNoise` | Hash-based deterministic 3D gradient noise, fBm, ridged |
| Generation | `Generation/PlanetHeightGenerator` | `GetElevation(direction)` / `GetSurfaceNormal`, mesh independent |
| Generation | `Generation/MountainSystem` | Seeded mountain provinces and curved ranges (envelope) + anisotropic ridges |
| Generation | `Generation/HydrologyGrid`, `Generation/HydrologySystem` | Mesh-independent cell graph; Priority-Flood, flow, accumulation, basins, cache |
| Generation | `Generation/RiverNetworkSystem` | River network data (rivers, topology, Strahler, trajectories) + cache |
| Rendering | `Rendering/PlanetSurfaceSampler`, `Rendering/RiverRibbonBuilder` | Terrain-hugging river ribbons on the displayed mesh |
| Generation | `Generation/DepressionAnalysis` | Depression hierarchy (candidates: nested depressions, saddles, spill points) |
| Generation | `Generation/LakeSystem` | Lake selection / classification / footprints, lake-aware drained area, `LakeCache` |
| Generation | `Generation/LakeRiverLayer` | Lake-aware surface-water connectivity: river reaches, inflows, outflows |
| Rendering | `Rendering/LakeMeshBuilder`, `Rendering/LakeDebugColors` | Lake water surfaces (`PlanetLake` shader), lake debug views |
| Generation | `Generation/CubeSphereGenerator` | Cube-sphere topology, bit-exact shared face borders |
| Rendering | `Rendering/PlanetMeshBuilder` | Face meshes (positions, normals, vertex colors), reused buffers |
| Rendering | `Rendering/PlanetVisualController` | Owns face/ocean objects and meshes, reuses them on regeneration |
| Rendering | `Rendering/PlanetColorRamp` | Elevation (+ slope) palette |
| Presentation | `Camera/PlanetOrbitCamera`, `UI/PlanetGenerationPanel` | Input System orbit camera, uGUI panel |
| Bootstrap | `Bootstrap/PlanetBootstrap` | Regeneration entry point, scene auto-setup |

Elevation convention: dimensionless, fraction of the reference radius
(surface point = `dir * radius * (1 + elevation)`), so terrain shape is independent of the
rendering scale.

## Changelog

### V0.1.1D-B.1 — Procedural Lakes
- Terrain, base hydrology (filled surface, D8, accumulation) and the river network graph: unchanged
  (regression-tested). Three elevations stay separate: terrain, filled (analysis only), lake level.
- `DepressionHierarchy`: separate deterministic pass on the real terrain. Watershed by flooding
  from local minima and the ocean (minimax flood keys), lowest pass per pair of basins, Kruskal
  merge: top-level depressions spill to the ocean, depressions meeting at a saddle become children
  of a meta-depression, flat minima are absorbed. Per candidate: id, bottom, spill elevation and
  cell, receiving cell, nesting, area / volume / centroid at spill (exact cell areas), topographic
  catchment (steepest descent). Footprint at level w = cells of the depression with flood key < w
  (connected, never across land above w, islands kept).
- Selection (`LakeSystem`): volume significance x seeded factor [0.7, 1.4] against an abundance
  threshold (10^-6..10^-8 of surface x radius), min area / 3 cells / depth, max 0.3% of the planet
  and 60% of the catchment, antichain of the hierarchy (no nested lakes), lakes >= 3 cells apart,
  small quota of mountain lakes. Zero lakes is valid.
- Classification (provisional water budget, NOT an equilibrium): seeded planet aridity
  K in [2, 5.5]; OPEN (level = spill, outlet = base D8 exit through the saddle) when supply >=
  K x lake area at spill and the D8 outflow really crosses the saddle; otherwise CLOSED at
  bottom + phi (spill - bottom), phi = filling factor x (0.5 + 0.5 min(1, supply / K area)) in
  [0.1, 0.95]; below the merge height the lake is the pocket holding the deepest point.
- `LakeRiverLayer`: river nodes under a lake / on a closed basin floor / captured (lake-aware
  drained area below the threshold) / visible; visible reaches with explicit starts (source,
  open-lake outlet, confluence, reactivation) and ends (river end, lake inflow); steepest-descent
  connectors across dry closed-basin floors; one outlet per open lake. Base D8 never modified.
- Ribbons (with lakes shown): one ribbon per reach, cut just under the water of the lake entered
  / left (true terrain below the level), lake-aware width, submerged samples not lifted (shader).
- `LakeMeshBuilder` + `PlanetLake` shader: refined equi-angular grid (2-4 per hydrology cell),
  marching triangles on level - true elevation restricted to water connected to the footprint and
  to the basin's shore ring; flat interior; shoreline / outlet lip placed on the displayed mesh
  (no gap, no floating edge), interior raised onto a coarser display mesh where it pokes above the
  level (counted); seamless across faces; drawn with `Graphics.RenderMesh` (no GameObject),
  writes depth (the ocean never shows through).
- Caching: hydrology -> candidates -> selection -> (layer, water mesh, ribbons). Visibility and
  color rebuild nothing; river width rebuilds ribbons only; river density never touches candidates.
- UI: LAKES section (Show Lakes, Debug view cycle, Lake Abundance, Lake Minimum Area, Closed Lake
  Level, statistics and timings). Fix: depression sort keys handle negative elevations.

### V0.1.1D-A.1 — River Geometry Polish
- Hydrology, network topology, Strahler orders, terrain: unchanged (regression-tested).
- Mouths: clipping only in the final stretch of ocean-bound rivers (tributaries are never cut);
  ribbons ending on rendered land are extended along the final drainage direction, only across
  hydrology ocean cells, until submerged; otherwise reported. Shader: no lift under the ocean.
- Confluences: main stems pass through confluences with a continuous tangent (anchors stay
  fixed); tributaries arrive blended with the parent direction; corridor-constrained relaxation
  reduces the D8 staircase (max 0.35 cell from each cell center).
- Crossings: `RiverCrossingAnalyzer` (tangent-plane tests, spatial hash) classifies shared
  junctions, D8 graph crossings, smoothing-induced crossings (corrected by reverting the pieces
  to the raw path) and width overlaps (ribbons narrowed locally).
- Ribbons: fold prevention, degenerate / inverted triangles removed and counted.
- Diagnostics: `RiverRibbonBuilder.LastDiagnostics`, `RiverNetwork` crossing counts,
  `RiverDiagnostics` (D8 direction quantization, grid alignment of first-order rivers).
- Known D8 limits (topological, not fixable by rendering): direction quantization and the
  grid imprint of parallel first-order tributaries ("comb").

### V0.1.1D-A — River Network
- `RiverNetwork` is a geographic entity, independent of rendering: river cells (nodes) with
  downstream links, kinds (source, confluence, mouth, sink), Strahler orders; `River` entities
  (main stems continue on the larger branch at confluences; tributaries know their parent and
  junction); `RiverSegment`s; smoothed trajectories with drained area along them.
- Extraction uses the single threshold `HydrologySettings.riverNetworkThreshold` (now 7e-4 of
  the planet surface; UI: River Density). Continuity is guaranteed (accumulation never
  decreases downstream).
- Trajectories: spherical Chaikin corner cutting between fixed anchors (sources, confluences,
  junctions, mouths); points stay on the D8 corridor.
- `RiverSettings` (on `PlanetVisualController`): show, smoothing, width model
  (power law of drained area, bounded), min on-screen width, coastal-stub display filter, color.
- Ribbons are placed on the displayed mesh (`PlanetSurfaceSampler`) and drawn with
  `Graphics.RenderMesh` (no extra GameObject), new `PlanetRiver` shader. Artistic shaders unchanged.
- Separate invalidations: terrain -> hydrology -> network -> trajectories -> ribbons; width
  -> ribbons only; color -> nothing rebuilt; mesh resolution -> ribbons only.

### V0.1.1C — Hydrology Foundation
- `HydrologyGrid`: equi-angular cube-sphere cell graph (64/128/256 per face), unique ids,
  explicit symmetric 8-neighbourhoods across edges and corners, exact spherical cell areas.
- `HydrologySystem`: Priority-Flood + epsilon on a separate corrected surface (the terrain is
  never modified), steepest-descent flow, area accumulation (drained area, not discharge),
  basins, depression count. Planets without ocean drain to an endorheic outlet.
- `HydrologyCache`: recomputes only when terrain parameters (`PlanetDefinition.TerrainEquals`)
  or the grid resolution change; the river threshold is display-only.
- `HydrologySettings` (on `PlanetVisualController`): `resolution` (128), `riverNetworkThreshold`
  (2e-4 of the planet surface). Hydrology is computed on demand only.
- Debug views use a new unlit shader; artistic shaders unchanged. Terrain unchanged.

### V0.1.1B — Mountain Systems
- `MountainSystem` replaces the V0.1.0 region-mask mountains: 0–6 provinces placed on
  land, each with 1–3 roughly parallel curved ranges (great-circle arcs with bow, meander
  and variable width), optional branches, plus occasional isolated massifs. The envelope
  (plain → foothills → slopes → ridgelines) defines where mountains are; anisotropic
  ridged noise aligned with the range defines their shape; massifs alternate with passes.
- Mountains are suppressed near and below the coastline; the continental field is
  unchanged (regression-tested against V0.1.1A values).
- New fields: `mountainRangeCoverage` (0–1, default 0.5), `mountainRangeLength`
  (0.5–2, default 1, inspector only), `mountainRuggedness` (0–1, default 0.5).
  `mountainStrength` keeps its meaning: an artistic fraction of the radius, not km.
- UI: "Range Coverage" and "Ruggedness" sliders. Debug views `MountainProvinces`, `MountainRanges`.
- Terrain differs from V0.1.1A wherever mountains were (continents are identical).

### V0.1.1A — Continental Shapes
- Continental field rebuilt as: seed-derived planet character → tangent-space domain warp →
  macro (3 oct.) + planet-scale asymmetry + regional (2 oct.) layers → coastal detail
  restricted to a band around the coastline. Bimodal elevation profile, mountains and
  detail are unchanged (mountains/detail are not warped).
- New `PlanetDefinition` fields: `continentalWarpStrength` (0–0.5 rad, default 0.2),
  `continentalWarpFrequency` (0.5–4, default 1.1), `coastalComplexity` (0–1, default 0.5).
  Older serialized definitions load with these defaults.
- UI: "Continental Distortion" and "Coastal Complexity" sliders. Panels serialized by
  V0.1.0 scenes are rebuilt automatically at startup.
- **Terrain is not backward compatible:** V0.1.0 seeds produce different planets.

## Tests

Window > General > Test Runner > EditMode > run `Planetwalker.Tests.EditMode`.
Lake tests: `LakeSystemTests` (data, synthetic terrains + reference seeds) and `LakeRenderingTests`
(visual controller: caching, visibility, debug-view seams, leaks).
