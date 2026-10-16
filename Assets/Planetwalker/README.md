# Planetwalker — Genesis V0.1.3B (World Library)

## Launch

1. Open the project in Unity 6000.3 (URP).
2. Menu **Planetwalker > Setup Genesis Scene** (once). It creates
   `Materials/PlanetTerrain.mat`, `Materials/PlanetOcean.mat` and `Scenes/Genesis.unity`,
   and adds the scene to the build settings. It asks before overwriting an existing scene.
3. Open `Scenes/Genesis.unity` (opened automatically after setup) and press **Play**.

A scene that only contains a `PlanetBootstrap` component also works: missing objects
(planet, star, camera, UI) are created at startup.

Controls: **LMB drag** rotate · **mouse wheel** zoom · **R** reset view.

Default view: **Natural Planet** (panel **SURFACE**; the label above the controls shows the current
display: Natural Planet, Classic elevation colors or Scientific / Debug). Terrain colors come from
the biome suitabilities, multi-scale variation, steep-slope rock and a provisional ice / snow look.
Biome Blend, Regional Variation, Rock Exposure and Color Saturation are artistic only: they recolor
the terrain and never recompute climate, biomes or hydrology. Unticking Natural Planet restores the
classic elevation palette.

Oceans & atmosphere (panel **OCEANS & ATMOSPHERE**, rendering only, applied immediately): Ocean Shading
and Atmosphere toggles (before / after comparisons), Ocean Depth Contrast, Coastal Shelf Visibility, Ocean
Roughness, Specular Intensity, Atmosphere Strength, Atmosphere Thickness, Terminator Softness. Hidden in
all development views. Builds: re-run **Setup Genesis Scene** once so the atmosphere material asset is
referenced (the editor finds the shader by name).

Clouds (panel **CLOUDS**, rendering only): Show Clouds, Cloud Motion (off by default), **New Sky** (another
weather seed over the same geography) / **Planet Sky**, Cloud Coverage (area fraction), Density, Detail, Weather
Systems, Convection, Cloud Shadows, Cloud Altitude; **Show Cloud Data** + View: Coverage / Structure (legend).

Lighting & time (panel **LIGHTING & TIME**): Day/Night Cycle (off by default), Pause / Resume, Solar Time
scrubber (12:00 = the default sun), Cycle Duration, Twilight Warmth, Reset Sun. **Contemplate (H)** hides the
controls (orbit and zoom keep working); **H**, **Esc** or the small "Show controls" button bring them back.

World archive (panel **WORLD ARCHIVE**, top of the panel, V0.1.3A): the current world's name, its state
(**New world · not saved yet**, **Saved · date · id**, or **UNSAVED CHANGES**), **Save Planet** (asks for a name the
first time, then replaces the same archive), **Save As New** (a new identity) and **World Library** (V0.1.3B: every
saved world as a card with its picture; select to see its details, **Enter World**, **Rename**, **Duplicate**, **Delete**,
**Refresh Preview**, **New World**; search and sort). Deleting, and entering or creating a world over unsaved changes,
ask for confirmation. Editing the World name renames the current world (its archive too when it is saved). See
*World Archive* and *World Library* below.

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

- Climate views (panel **Climate > Show Climate** + **View** button cycles them; legend under
  the controls): `ClimateTemperature` (climatology scale: white -50 C .. purple ..
  blue .. cyan 0 C .. yellow 10 C .. red 20 C .. dark red 30 C), `ClimateMoisture` (absolute humidity index, brown dry .. blue humid, bands every 0.1),
  `ClimatePrecipitation` (x planet mean, log scale, cream arid .. green .. navy very wet),
  `ClimateWinds` (light arrows on a neutral background, brighter = faster; heading colors with
  the `Climate Wind Direction Colors` option), `ClimateContinentality` (coast pale .. interior
  dark red, bands every 500 km). Climate Resolution (32 / 64 / 128), Axial Tilt and Climate
  Elevation Scale recompute the climate (after a short delay); changing views never does.

All development views are unlit (`PlanetDebugUnlit` shader) and hide the ocean sphere.

Rivers on the artistic view: panel **Rivers > Show Rivers** (off by default, like Show
Hydrology, because it adds the hydrology analysis to every regeneration).
Lakes: panel **Lakes > Show Lakes** (off by default for the same reason). Lake Abundance, Lake
Minimum Area (% of the planet surface) and Closed Lake Level only change the selection (the
depression analysis and the hydrology are reused).

## World Archive (V0.1.3A)

A saved world is a small JSON file (about 3 KB) holding its identity, both seeds and every persistent setting. No
generated data is stored (no meshes, grids, climate, biome maps or cloud textures): loading regenerates the planet from
the seeds and settings, once.

**Location**: `Application.persistentDataPath/Planetwalker/Worlds/<planet-id>.json`, shown at the top of the archive
browser. With the default company / product names: Linux `~/.config/unity3d/DefaultCompany/planetwalker/Planetwalker/Worlds`,
Windows `%USERPROFILE%\AppData\LocalLow\DefaultCompany\planetwalker\Planetwalker\Worlds`, macOS
`~/Library/Application Support/DefaultCompany/planetwalker/Planetwalker/Worlds`. Same folder in the Editor and in
builds; no administrator rights, network or account. Files are named by planet ID only: display names (any Unicode,
80 characters max, no control characters) never reach the file system.

**Identity**: a random GUID created at the first save, never derived from the seed (several worlds can share a seed).
Rename keeps it; Duplicate and Save As New create a new one. Timestamps are UTC (`createdUtc`, `modifiedUtc`).

**Contents** (schema 1): `archiveSchemaVersion`, `generatorVersion`, `planetId`, `displayName`, `createdUtc`,
`modifiedUtc`, `planetSeed`, `weatherSeed` (0 = derived from the planet seed), then one object per settings group,
each holding the archived fields of the project's own settings class under their C# names: `terrain`
(`PlanetDefinition` without the seed), `hydrology`, `rivers`, `lakes`, `climate`, `biomes`, `surface`
(`NaturalSurfaceSettings`), `oceanAtmosphere`, `clouds` (without the weather seed), `lighting` (twilight warmth, night
ambient), and the optional `presentation` (`camera`: yaw / pitch in degrees, distance in rendering units; `solar`:
solar time, day / night cycle, paused, cycle duration). Archived fields are the public serialized fields of each
settings class (`SettingsFields`): a field added later is archived automatically, and a field of an unsupported type
fails the archive tests rather than being dropped.

**Persistent vs transient**: only the configuration above marks a world modified. The camera, the solar time and
clock state, the cloud animation phase, debug views, wind-direction colors and contemplation mode never do; the
camera and the clock are captured on each explicit save (the `presentation` block) and restored on load. The cloud
animation phase is never saved; contemplation mode and debug views are never restored (a loaded world is shown in the
Natural Planet view). The unsaved state is evaluated by comparing every archived field with the last saved or loaded
configuration, only during 1.5 s after a panel control, an inspector edit or a regeneration (throttled to 5 Hz;
nothing is compared while the planet just animates).

**Saving** captures the live settings (sanitized copies), validates them, serializes, checks that the text reads back
to the same configuration, writes a temporary file next to the archive, flushes it to disk, verifies its bytes, then
atomically renames it over the archive (`File.Replace` / POSIX rename). Any failure (disk full, permissions, locked or
corrupted file) leaves the previous archive untouched and is reported in the panel; orphan temporary files of an
interrupted save are never listed and are removed after 10 minutes. Saving never regenerates and never resets the
cloud motion or the solar clock. Settings still waiting for the panel's slider delay are written first, so the archive
matches the controls.

**Loading** is transactional: the file is read (1 MB max, strict UTF-8, strict JSON: no comments, trailing commas,
NaN / Infinity, duplicate keys; 32 levels max), migrated and fully validated before anything changes; a rejected file
leaves the current planet untouched. Then the groups are applied in a fixed order (terrain, hydrology, rivers, lakes,
climate, biomes, surface, oceans & atmosphere, clouds, lighting), the planet is regenerated once, and the camera
(clamped to the camera's limits) and solar clock are restored. If the regeneration throws, the previous configuration
is applied and regenerated again and the load is reported as failed.

**Validation** (archives are untrusted input): supported schema, known generator family, a valid planet ID matching
the file name, valid name, UTC timestamps, 32-bit integer seeds, finite numbers only, every value inside its
`[Range]` and unchanged by the class's own `Sanitize()` (so out-of-range values and disallowed grid resolutions are
rejected, never clamped), colors in [0, 1]. Missing required data (schema version, generator version, ID, name,
timestamps, planet seed, `terrain`) is an error. Missing optional data receives the generator defaults (the C# field
initializers) and is listed in the load warnings (Console); unknown members are ignored with a warning.

**Versioning**: `archiveSchemaVersion` is the file layout; `generatorVersion` is the set of procedural algorithms and
defaults (currently `Genesis V0.1.3A`). Future schemas are rejected; older schemas go through forward-only migration
steps (`PlanetArchiveSerializer.Migrations`, none yet: schema 1 is the first). Reconstruction is **exact** for archives
of the current generator (and of versions listed as algorithm-identical) and **best-effort** otherwise: the same seeds
and settings are applied, but terrain, climate, biomes or clouds may differ wherever an algorithm or default changed.
Best-effort loads are labelled in the panel and the browser. A seed is not a promise of identical terrain across
generator versions; keeping old generators runnable is future work.

## World Library (V0.1.3B)

An overlay inside the running application (no scene change): header (New World, Close), search (name, any Unicode
script, case-insensitive; or seed digits) and sort (recently / oldest modified, name A-Z / Z-A, recently created),
pages of 12 cards (thumbnail, name, seed, last modification, land coverage when recorded, CURRENT tag), and a details
panel: larger preview, name, identity (seed, created, last modified, world ID), recorded characteristics (land and
ocean coverage, mean temperature, dominant biome), **ENTER WORLD** (primary; "Return to World" for the current,
unmodified world, which is not regenerated), Rename / Duplicate / Delete and Refresh Preview. Keyboard: arrows, Page
Up / Down, Enter, Esc. Double-click a card to enter its world. Unreadable archives stay listed (last) with their
error and can only be deleted. A successful Enter World / New World closes the library.

**Thumbnails**: `persistentDataPath/Planetwalker/Thumbnails/<planet-id>.png` (256 x 256), separate from the archives
(a missing or broken picture never affects a world: the library shows a placeholder). Captured with an isolated,
temporary camera and render texture (`WorldThumbnailRenderer`): the user's camera and the planet are untouched,
nothing is regenerated; the parts drawn without GameObjects (atmosphere, clouds, lakes, rivers) are submitted for that
camera only. Natural Planet view only (never a debug view), no UI or gizmos. Framing: the whole planet, atmosphere and
clouds with a 10% margin, 30 degree field of view, seen from the lit side (sun 35 degrees aside, 18 degrees above the
equator) so every picture is daylit and comparable; rendered at 512 px and averaged 2x2. No post-processing (bloom /
tonemapping of the main camera are not applied). Captured only after a save (Save, Save As New) or Refresh Preview,
a couple of frames later, once the display matches the saved configuration (pending regeneration done, Natural Planet
view shown); if the world changes first, the capture is cancelled (an archive's picture never shows unsaved changes).
Duplicate copies the picture, Rename keeps it, Delete removes it after the archive is deleted. Failures (capture,
encoding, read-only folder) are non-fatal warnings; the previous picture is kept and the save stands.

**Characteristics**: an optional `metadata` member of the archive (same schema 1): `landFraction` (surface above sea
level, the planet's 8192-sample statistic), `meanTemperature` (area-weighted annual mean of the whole surface, deg C,
from the climate diagnostics) and `dominantBiome` (largest land area). Written at each save from values the application
had already computed, and only when they provably belong to the saved configuration (otherwise "Unavailable"); never
recomputed to open or browse the library; never part of the unsaved-changes comparison. Chosen over a sidecar file:
it is written atomically with the world, and rename / duplicate / delete need no extra file. V0.1.3A archives
(no metadata) load unchanged (exact reconstruction: V0.1.3A and V0.1.3B share the same algorithms) and show
"Unavailable" until the next save or Refresh Preview (which records the statistics and the picture of the current,
unmodified world without changing its modification time). A V0.1.3A build reads V0.1.3B archives and ignores the
metadata with a warning. An invalid metadata block is dropped with a warning; the world stays loadable.

**Resources**: opening reads the archive folder once (metadata only); search and sort run in memory. Thumbnails are
decoded lazily for the visible page (3 per frame) into a bounded cache (36 textures) that is released when the
library closes. No per-frame disk access, JSON parsing or decoding; idle display allocates nothing.

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
| Data | `Core/ClimateSettings` | Climate settings: resolution, rotation axis, axial tilt, elevation scale, calibration |
| Generation | `Generation/ClimateForcing` | Latitude, annual-mean insolation (orbit-integrated), three-cell prevailing winds |
| Generation | `Generation/ClimateGenerator` | Temperature, ocean influence, moisture transport, precipitation; `ClimateResult`, `ClimateCache` |
| Rendering | `Rendering/ClimateDebugColors`, `Rendering/ClimateWindArrows` | Climate debug views, legends, wind arrows |
| Data | `Core/BiomeSettings` | Biome interpretation settings: aridity, ecotone sharpness, alpine sensitivity |
| Generation | `Generation/BiomeClassifier` | Biome taxonomy, ecological variables, suitability scores, `BiomeResult`, `BiomeCache` |
| Rendering | `Rendering/BiomeDebugColors` | Biome debug views and legends |
| Data | `Core/NaturalSurfaceSettings` | Natural Planet appearance settings (artistic only) |
| Data | `Core/OceanAtmosphereSettings` | Oceans & atmosphere rendering settings |
| Data | `Core/CloudSettings` | Cloud rendering settings, weather seed |
| Rendering | `Rendering/CloudField`, `Rendering/CloudDebugColors`, `Rendering/PlanetVisualController.Clouds` | Cloud data (cube map texels), weather systems, coverage quantile, cache; debug views; cloud shell / shadows / motion presentation (`PlanetClouds` shader, `PlanetCloudsCommon.hlsl`) |
| Rendering | `Rendering/OceanBathymetry`, `Rendering/PlanetVisualController.OceanAtmosphere` | Bathymetry grid / ocean depth layer / cache; ocean, terminator and atmosphere presentation (`PlanetOcean`, `PlanetAtmosphere` shaders) |
| Rendering | `Rendering/NaturalSurface` | Natural Planet colors (`NaturalSurface`), mesh-independent slope field (`TerrainSlopeField`, `TerrainSlopeCache`) |
| Generation | `Generation/CubeSphereGenerator` | Cube-sphere topology, bit-exact shared face borders |
| Rendering | `Rendering/PlanetMeshBuilder` | Face meshes (positions, normals, vertex colors), reused buffers |
| Rendering | `Rendering/PlanetVisualController` | Owns face/ocean objects and meshes, reuses them on regeneration |
| Rendering | `Rendering/PlanetColorRamp` | Elevation (+ slope) palette |
| Presentation | `Camera/PlanetOrbitCamera`, `UI/PlanetGenerationPanel` | Input System orbit camera, uGUI panel |
| Bootstrap | `Bootstrap/PlanetBootstrap` | Regeneration entry point, scene auto-setup, world archive host (`IWorldHost`) |
| Persistence | `Persistence/PlanetArchive` | World archive model: identity, `WorldConfiguration` (settings groups), presentation, compatibility |
| Persistence | `Persistence/ArchiveJson`, `Persistence/SettingsFields` | Strict JSON reader / deterministic writer; field-level archive, compare, copy and range validation of settings classes |
| Persistence | `Persistence/PlanetArchiveSerializer` | Schema 1 layout, migrations, validation, generator compatibility |
| Persistence | `Persistence/WorldArchiveStore` | One file per world, atomic writes, listing, rename / duplicate / delete (`IArchiveFileSystem`) |
| Persistence | `Persistence/WorldSession` | Current world identity, unsaved-change tracking, transactional save / load |
| Presentation | `UI/PlanetGenerationPanel.Archive` | WORLD ARCHIVE section, entry to the World Library |
| Persistence | `Persistence/WorldMetadata` | Optional recorded characteristics (land, temperature, dominant biome) |
| Persistence | `Persistence/WorldThumbnailStore` | Thumbnail PNG files per planet ID (atomic writes, safe reads) |
| Library | `Library/WorldThumbnailRenderer` | Isolated-camera capture of the displayed planet to PNG |
| Library | `Library/WorldThumbnailUpdater` | When thumbnails are captured (after saves, only once the display matches) |
| Library | `Library/WorldThumbnailCache` | Lazy, bounded, released thumbnail textures |
| Library | `Library/WorldLibraryModel` | In-memory list, Unicode search, deterministic sorts |
| Presentation | `UI/WorldLibraryPanel` | World Library overlay: cards, pages, details, actions |

Elevation convention: dimensionless, fraction of the reference radius
(surface point = `dir * radius * (1 + elevation)`), so terrain shape is independent of the
rendering scale.

## Changelog

### V0.1.3B — World Library
- The V0.1.3A archive browser became the **World Library**: a full-screen overlay with planet cards, search, sort, pages,
  a details panel (preview, identity, characteristics), Enter World, New World, Rename / Duplicate / Delete, Refresh
  Preview, keyboard navigation and double-click. Details: *World Library* above.
- Thumbnails: isolated-camera captures of the Natural Planet after each save, stored per planet ID next to the
  archives; never block or fail a save.
- Optional `metadata` in archives (schema 1 unchanged; V0.1.3A archives load exactly, without statistics).
- Generator version `Genesis V0.1.3B` (algorithms unchanged; V0.1.3A archives remain exact).
- Rendering: the atmosphere / cloud / lake / river draws accept a target camera (`SubmitPresentationDraws`) for the
  thumbnail camera; what they draw is unchanged. Planet generation, climate, hydrology, biomes, clouds, oceans,
  atmosphere and lighting are untouched.

### V0.1.3A — Planet Workshop: The World Archive
- Save, name, rename, duplicate, delete and reload worlds (panel **WORLD ARCHIVE** + archive browser). One JSON file per
  world in `persistentDataPath/Planetwalker/Worlds`, named by a random planet ID. Details: *World Archive* above.
- Archives hold seeds and settings only; loading regenerates once. Reconstruction with the same generator is exact
  (tests compare terrain samples, mesh vertices and colors, climate and biome fields and the cloud field bit for bit
  on the four reference seeds).
- Safe persistence: validation before writing, read-back check, temporary file + flush + verify + atomic rename;
  failed or interrupted saves keep the previous archive. Strict validation on load; nothing changes until the archive
  is fully valid; rollback if the regeneration fails.
- Unsaved-changes indicator for persistent settings only (camera, solar clock, cloud animation and debug views never
  mark a world modified). Loading over unsaved changes asks for confirmation.
- Orbit camera: `SetView` / target accessors for restoring a saved view. Inspector edits of the bootstrap or the
  planet raise `SettingsEdited` (unsaved-change tracking).
- No change to generation, rendering or any existing default.

### V0.1.2D — Planetary Lighting & Presentation
- One authoritative sun: the scene's star light, oriented by a visual solar clock (`SolarSettings`,
  `SolarClock`, `PlanetVisualController.Solar`). Terrain, ocean (diffuse + GGX glint), atmosphere, haze,
  clouds and cloud shadows all read it through URP's main light; no shader has its own sun vector.
  Convention: fixed planet, sun turned around the climate rotation axis (westward with time, fixed
  declination); solar time 0.5 = the star's original orientation (validated default). A future orbital model
  only replaces `SolarDirection`.
- Clock: anchored (value, real time), never accumulated: frame-rate independent, no pause / resume drift,
  wraps in [0, 1). Off and at 0.5 by default (unchanged look). Independent of the planet seed, the weather
  seed and the cloud animation time; kept across regeneration; Reset Sun restores the default.
- Night: the sky ambient fades with the geocentric sun height (`_NightAmbient`, 4% kept) on terrain, ocean
  (incl. its Fresnel sky reflection) and clouds; a planet shadow cuts direct light (and the sea glint) where the
  sun has set, so wrapped relief no longer glows beyond the terminator (clouds keep it a little longer, being
  higher). Atmosphere night rim 2% -> 0.3%. Day side unchanged.
- Twilight: sunlight on the air near the terminator is reddened (blue lost first, low layers most), with the
  warm tint added on top (no blue / orange blend: no purple ring). Twilight Warmth scales the atmosphere and
  cloud-edge tints (0.5 = V0.1.2C).
- Contemplation mode (panel button, H): controls hidden, camera, clouds and clock untouched; H, Esc or a faint
  "Show controls" button restore them.
- Cost: solar tick 0.6 us, 0 bytes per frame; frame (1080p MSAA 4x, RTX 3070, batch) 2.16 ms paused vs 2.17 ms
  V0.1.2C with clouds and shadows; 3.18 ms running with cloud motion (3.19 ms before).
### V0.1.2C — Procedural Cloud Systems
- Terrain, hydrology, rivers, lakes, climate, biomes, Natural Surface: unchanged. Clouds off = the V0.1.2B
  look (shader branches disabled, no shell).
- Cloud data (`CloudField`, 128 x 128 x 6 cube map texels, RGBA half; layout verified on the GPU): R large-scale
  potential (climate prior 30%: precipitation index, relative humidity = moisture / capacity(T), latitude
  tendencies; planetary + domain-warped synoptic noise; east-west frontal bands in the midlatitudes),
  G weather-system pattern, B convective clusters (warm humid regions x cluster noise), A system envelope.
  5-8 midlatitude comma systems (38-62 deg) and 0-2 tropical cyclones (10-22 deg, clear eye), spaced, bounded
  (3 radii), hemisphere-correct rotation, noise-perturbed spirals. Inside its envelope a system governs the
  clouds (cloudy arms / head, clear dry slots). Without a computed climate: latitude-only prior (never forces
  the climate). ~110-135 ms per field (weather seed / climate change only).
- Coverage = area fraction (area-weighted quantile of the deterministic potential, fixed point with the systems):
  45% covers ~45% of the sphere. Reference seeds: tropics ~0.73, subtropics ~0.25, midlatitudes ~0.5, poles ~0.25;
  correlation with log precipitation 0.38-0.46.
- Rendering (`PlanetClouds`, `Graphics.RenderMesh`, unit sphere 64 x OceanRadius x (1 + altitude)): front faces,
  ZTest Always (the layer is above the exaggerated relief, no z-fighting, no cut along mountain silhouettes),
  alpha blend, queue 3050 (after the ocean, before the atmosphere glow). Per pixel: fBm detail (stretched
  east-west in the storm belts), billowy convective cells, mid-scale internal opacity (no flat white paint),
  thicker at grazing angles, main-light wrap (terminator softness), self-shadowing towards the sun, cool shaded
  side, restrained warm tint near the terminator, atmospheric haze in front.
- Shadows: terrain and ocean shaders sample the same function where the ray to the sun crosses the shell (at
  least just above the surface point); they dim the direct light only.
- Motion (off by default): flow map along the zonal three-cell winds, two samples cross-faded (period 0.25 time
  units, ~42 s): bounded displacement, no growing shear, a pure function of the simulation time; detail evolves
  slowly. Systems do not migrate around the globe (extension point).
- Debug views `CloudCoverage`, `CloudStructure` (vertex colors from the CPU field, legend).
- Cost: field 110-135 ms per weather seed / climate; coverage / weather / convection change ~23 ms (threshold);
  other sliders: material properties only; motion: 0 bytes per frame. Frame (1080p MSAA 4x, RTX 3070, batch):
  1.13 ms (clouds off), 1.63 (clouds), 2.15 (+ shadows), 3.28 ms (+ motion).
### V0.1.2B — Oceans & Atmosphere
- Terrain, hydrology, rivers, lakes, climate, biomes and Natural Surface: unchanged (their tests pass
  unmodified). Debug views: ocean and atmosphere hidden, terrain property blocks removed.
- Bathymetry: raw elevation on a fixed 128 spherical grid (`BathymetryField`, 98k samples of the continuous
  elevation field, ~0.28 s per terrain, independent of sea level and of the terrain mesh), then per sea level
  (`OceanDepthLayer`, ~10 ms) the local depth and a broad shelf depth (ocean-only neighbourhood mean, two
  passes). Normalization: depth / `continentStrength` (the planet's basin depth setting; terrain units are
  not km): deep basins ~0.9 on the reference seeds. Written per vertex (UV1) on the ocean sphere, now at
  least 128 subdivisions: seam-free (shared border vertices get identical values) and continuous.
- `PlanetOcean` shader: `_OceanShading` 0 = the previous flat ocean, unchanged (also the material default);
  1 = deep navy to lighter shallow blue (smooth curve, no thresholds), turquoise only on broad shelves
  (a thin shallow strip along a steep coast is not tinted), slightly more opaque first coastal strip (hides
  the pale sub-sea-level terrain palette), Fresnel sky reflection, GGX highlight (water F0 0.02) with soft
  saturation y / (1 + y): roughness changes size / sharpness, intensity the strength, never a white disc.
- Atmosphere: one model (`PlanetAtmosphereCommon.hlsl`: analytic ray / shell / ocean-sphere intersection,
  amount 1 - exp(-density tau^1.5), height falloff, lit at the segment midpoint: bright day rim, 2% night
  rim, warm tint near the terminator on long paths only), used twice:
  - the shell (`PlanetAtmosphere`, `Graphics.RenderMesh`, no GameObject): back faces, ZTest LEqual,
    additive, rays that miss the ocean sphere (limb, sky); relief in front of it hides it;
  - the terrain and ocean shaders: haze on the path camera -> surface point (soft cap 5 on tau), so a
    mountain rising into the atmosphere is hazed at its base (meeting the limb glow) and clear at its top.
    Parameters copied per renderer from the atmosphere material (property blocks); strength 0 = no haze
    (debug views, atmosphere off, materials used without the controller).
  No depth texture needed. Missing / incompatible shader: atmosphere disabled with a warning.
- Terminator softness: the terrain's existing `_LightWrap` (and the same wrap on the ocean), via property
  blocks; default 0.2 = the previous 0.08 wrap. No second lighting system.
- Cost: visual sliders 0.001 ms, 0 bytes allocated per change (material properties only); bathymetry grid
  ~0.28 s per terrain (cached), depth layer ~10 ms per sea level; frame time 0.98 ms vs 0.97 ms
  (1080p MSAA 4x, RTX 3070, batch capture).
### V0.1.2A — Procedural Natural Surface Appearance
- Terrain, hydrology, rivers, lakes, climate and biome classification: unchanged (outputs identical;
  the Natural Planet only reads them). Debug views unchanged.
- New default lit view **Natural Planet** (`NaturalSurface`, per land vertex from its direction and
  elevation, mesh independent): interpolated biome suitabilities (land cells only, 1.4 local cell
  spacings), top three biomes weighted (s^p - s4^p), p from 3 (Biome Blend 0, crisp) to 1.25 (Biome
  Blend 1, smooth); restrained sRGB palette; ids never interpolated, ocean never blended (underwater
  vertices keep the ocean-floor palette). Coasts / islets smaller than a climate cell fade to a
  pointwise classification of the interpolated climate.
- Variation: deterministic 3D noise on the unit sphere (seeded, no UV / face dependence): regional
  (brightness), warmth, landscape (dry soil patches through vegetated biomes), fine (slight brightness).
- Terrain: bare rock from steepness on a cached 128 slope field (least-squares gradient, sea-level
  floor so coasts are not cliffs); smooth thresholds calibrated on the reference planets (start about
  land p90, full about p99; 1.5 - 5 % of land with cover above 0.3 at default Rock Exposure); mild
  desaturation with elevation; rock partly covered under the ice / snow potential (pale, slightly cool,
  never pure white; provisional, no snow simulation). Rock reads at a regional scale (slope cell ~80 km).
- `HydrologyGrid.LocalSpacing`: continuous local cell spacing (equi-angular metric) so sampling kernels
  stay inside the 8-neighbourhood: removes a small color step at some cell boundaries.
- Cache: artistic settings, view changes and mesh resolution only recolor (the sampler is rebuilt when
  biomes, slope field, settings or seed change; the slope field only when the terrain changes). The
  default view now computes climate and biomes on terrain changes.
- Cost (terrain 256, climate 64): natural colors 2.9 us per land vertex (0.41 s for 142k land
  vertices), slope field 0.28 s per terrain; first render 4.4 s (climate + biomes + slope), warm
  re-render 3.3 s natural vs 2.9 s classic, switching back from a debug view 0.5 s (recolor).
### V0.1.1E-B.1 — Deterministic Planetary Biome Classification
- Terrain, hydrology, rivers, lakes and climate: unchanged. Biomes read the `ClimateResult` (same grid,
  no second climate model, independent of the terrain mesh) and never write anything back.
- Taxonomy (Whittaker's annual temperature / precipitation diagram, 13 terrestrial classes + Ocean):
  tropical rainforest, tropical seasonal forest / savanna (one class: no seasonality proxy), hot
  desert, dry woodland / shrubland (not asserted Mediterranean), steppe, cold desert, temperate
  grassland, temperate forest, temperate rainforest, boreal forest, tundra, alpine, ice / snow
  potential (provisional: annual mean below about -12 C). Deferred: Mediterranean, savanna vs seasonal
  forest, monsoon forests (need seasonal climate).
- Ecological variables: annual temperature; evaporative demand D(T) = max(0.12, 0.8 exp(0.05 (T - 15)))
  (Thornthwaite-like, relative to the planet-mean rain); aridity index P / (aridity x D) read like
  UNEP P / PET (arid < 0.2 < semi-arid < 0.5 < sub-humid < 0.65 < humid), relative, not mm; elevation
  cooling (lapse x climate elevation) separating alpine from tundra.
- Suitability: soft envelopes in (T, ln AI), normalized per land cell; dominant / secondary / margin
  (ties: lower id). Conservative regularization: continuous blend with land neighbours for low-margin
  cells only (never across the ocean), then isolated low-margin cells join their neighbourhood when it
  is their second-best biome.
- Views (panel BIOMES: Show Biomes, View, Aridity / Ecotone / Alpine sensitivity, legend, statistics):
  `BiomeMap` (discrete palette, boundaries from interpolated suitabilities, ids never interpolated),
  `BiomeSuitability`, `BiomeTransitions` (ecotones orange), `BiomeAridity` (log scale, UNEP classes).
- Cache: climate result + biome settings only; views, mesh resolution, rivers and lakes never
  recompute. Classification: 17 ms at climate resolution 64 (75 ms at 128).

### V0.1.1E-A.1 — Climate Calibration & Debug Polish
- Terrain, hydrology, rivers, lakes: unchanged. Temperature unchanged.
- Moisture transport: the donor-cell advection now sends moisture to the two neighbours bracketing
  the wind, with weights making the mean displacement exactly parallel to the wind and an outflow
  scaled by that displacement (travel independent of the grid orientation). The former cos^2
  8-neighbour weights imprinted the cube-face seams on the moisture field (aquaplanet: 5.4% deviation
  from the zonal mean near seams vs 1.5% inside faces; now 0.9% / 0.9%). Gather form, one pass per
  iteration: 0.71 s at resolution 64, 3.3 s at 128.
- Precipitation calibration (measured on the 7 reference seeds): land recycling 0.7 (Earth land
  evapotranspiration / precipitation ~0.6-0.7), storm tracks 2.5, rain length 2500 km, large-scale
  rain scaled by relative humidity. Temperate land x0.41 -> x0.68 of the planet mean, windward
  temperate coasts x0.96 -> x1.25, interiors x0.21 -> x0.44 on average (x0.12 .. x1.27 by planet),
  arid land below 60 deg 43% -> 37%. Deserts remain where the circulation makes them. The four
  calibration values are exposed in `ClimateSettings`.
- Debug views: precipitation on the classic climatology palette (cream, greens, teal, navy; no bands),
  muted temperature scale, winds on a neutral background with light arrows (heading colors as the
  `Climate Wind Direction Colors` option), softer iso-bands, smoother display sampling. Panel: color
  bar legends with ticks placed on the data scale (log for precipitation) and units. The moisture map
  is an absolute humidity index (1 = saturated air at 25 C), not relative humidity.
- Note for biomes (V0.1.1E-B): classify from temperature and precipitation, not from the moisture map.

### V0.1.1E-A — Climate Foundations
- Terrain, sea level, hydrology, rivers and lakes: unchanged (regression-tested). The climate is a
  downstream analysis: it samples the continuous elevation field at its cell centers (independent
  of the terrain mesh resolution) and never writes anything back.
- Grid: the `HydrologyGrid` cube-sphere cell graph (shared class, own resolution 32 / 64 / 128,
  default 64): exact cell areas, explicit neighbourhoods across faces (seams are ordinary edges).
- Forcing: latitude = asin(dot(direction, axis)); annual-mean insolation = daily-mean insolation
  averaged over 360 orbital positions (circular orbit, fixed luminosity); global mean S0/4 for
  any tilt. Temperature (C): Tpole + (Teq - Tpole) x^0.6 (x = insolation between the Earth-tilt
  pole and equator values; concave = poleward heat transport), continental amplification of the
  departure from 10 C (up to 15% deep inland), lapse rate 6.5 C/km on the climate elevation
  (1% of the radius above sea level = 1 km by default), small seeded noise (0.75 C).
- Winds: three-cell pattern (trades, westerlies, polar easterlies) blended with latitude, tangent,
  never zero at a cell, fading near the poles, smooth seeded rotation (20 deg max).
- Moisture (index, 1 = saturated air at 25 C): Jacobi iteration of donor-cell advection to the
  downwind neighbours, ocean evaporation towards the Clausius-Clapeyron capacity, large-scale
  rain-out (wet ITCZ, dry subtropics, mid-latitude storm tracks), orographic rain-out along the
  wind, condensation above capacity, 35% land recycling; iterated to a steady state (measured
  mass-balance error ~1e-9). Precipitation index = rain-out / planet area mean.
- UI: CLIMATE section (Show Climate, View, Climate Resolution, Axial Tilt, Climate Elevation
  Scale, legend, statistics). Climate computed only when a climate view is shown; cached on the
  terrain parameters, sea level and climate settings.
- Not modelled: seasons, ocean currents, clouds, biomes, feedback on rivers or lakes.

### V0.1.1D-B.3 — Lake Shoreline & River Transition Polish
- Hydrology, lakes (ids, classes, levels, footprints), drainage and river topology: unchanged
  (regression-tested, independent of the terrain resolution). Hydrology Resolution kept.
- Lake surface (`LakeMeshBuilder`): fallback seeds (quad corners) only for lakes without any
  displayed vertex in their footprint; disconnected single-vertex "puddles" (one sampled vertex
  below the level, away from the lake body) are not drawn and counted, larger disconnected parts
  are kept; wet-side shoreline snap (< 2% of the edge and < 1e-5 R above the terrain); quad split
  along the diagonal giving the fatter triangles; boundary vertices closer than 0.1% of a render
  edge are welded (no needles); basin cuts placed on the edge part where the step under the water
  edge is smallest. Measured: no degenerate triangle, slivers (altitude < 1% of the longest edge)
  below 0.5%.
- River transitions (`RiverRibbonBuilder`): past the shoreline crossing the ribbon edges are kept
  inside the rendered water (bisection, then the sampled vertex is checked), so no ribbon wedge
  lies on land beside an oblique shore; gentler flare (+30% over 6 half widths, at least two render
  quads, monotonic); reaches lying entirely under a lake are not emitted.
- Rivers tint towards the lake's shallow water color near a mouth (`_MouthColor`, `_MouthBlend`).
- Debug view `LakeShoreline` (panel Lakes > Debug cycle): water, shoreline, basin cuts, rejected
  low terrain, dropped puddles, detached parts, problem triangles, footprint boundary, inlets,
  outlets, unresolved and exposed transitions. Built from the last geometry, recolored only when it
  changes.
- Known limits: basin-cut steps where the hydrology grid is coarser than the terrain (up to
  ~0.003 R at hydrology 64 / terrain 256), rare unresolved inflows on coarse grids or meshes
  (reported, same as B.2), lakes not representable by a coarse mesh (reported).

### V0.1.1D-B.2 — Terrain-Conforming Lake Shorelines & Seamless River Transitions
- Hydrology, depression hierarchy, lake selection / classification / levels / ids, drainage and
  river topology: unchanged (regression-tested). Hydrology Resolution and terrain Resolution stay
  independent settings: the grid defines the lakes, the render mesh defines the visible shoreline.
- `LakeMeshBuilder` rewritten: the water is clipped out of the DISPLAYED terrain triangles. Wet
  render vertices (displayed height below the level) are flood-filled from the footprint across the
  render mesh edges, restricted to the lake region (footprint + basin-rim shore ring); every
  touched render triangle is clipped (marching triangles). Wet vertices are projected on the water
  sphere R (1 + level); shoreline vertices are the exact terrain-edge / water-sphere intersections
  (on the terrain AND at the level). Wet terrain outside the region is cut at the region boundary
  (bisection on cell membership), at the level ("basin cut", counted, max drop measured). The B.1
  fallback that raised interior water onto a coarse mesh and lowered the outlet lip is removed:
  all vertices are at the level (max deviation measured), and the water plane stays above the
  terrain plane in each triangle (checked per triangle). Canonical cube-coordinate vertex and edge
  keys make the surface continuous across cube faces, independent of the traversal order. Lakes
  the displayed mesh cannot show (no render vertex below the level) are not drawn and are counted.
- Rendered-water queries (`IsUnderWater`, `GetLakeUnderWater`) expose the clipped surface to the
  ribbons. River inflows / outflows: exact centerline crossing of the rendered shoreline
  (bisection) inserted as a sample, mouth flare (+50% over 4 half widths, at least one render quad),
  continuation under the water over 3 flared half widths (at least half a render quad) until both
  edges are submerged. The hydrological (true terrain) shoreline is only a counted fallback.
- Shaders: rivers drawn after the lakes (`Transparent-5`), lift clamped to the lake water radius
  (UV1.y), so the lake depth hides the submerged ribbon exactly along the rendered shoreline. Lakes
  use the exact radial normal per pixel (no triangle-dependent lighting); the glint is legitimate
  specular.
- Caching: lake geometry is shared by the water mesh and the ribbons, rebuilt only for a new lake
  selection or a new terrain mesh; visibility / colors rebuild nothing; river width rebuilds the
  ribbons and their transitions only.
- Diagnostics (panel, measured at build time): shoreline length / segments, basin cuts, lakes not
  shown, terrain intersection warnings, cracks and seam mismatches, level deviation, river
  transitions in / out, unresolved shoreline intersections, exposed transition ends, timings.

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
World archive: `WorldArchiveFormatTests` (format, schema, validation, identity, safe storage; engine independent) and
`WorldArchiveSessionTests` (live planet: unsaved changes, regeneration counts, deterministic reconstruction of the
reference seeds, presentation, leaks, save / list / load timings). Both use a temporary folder, never the user's archives.
World Library: `WorldLibraryFormatTests` (V0.1.3A compatibility, metadata, thumbnail files, search / sort; engine
independent) and `WorldLibrarySessionTests` (real thumbnail captures, capture timing, unsaved-change protection, no
recomputation while browsing, bounded textures, 120 worlds, application wiring).
Lake tests: `LakeSystemTests` (data, synthetic terrains + reference seeds) and `LakeRenderingTests`
(visual controller: caching, visibility, debug-view seams, leaks), and `LakeShorelineTests`
(V0.1.1D-B.2 shorelines and river transitions, tests A - O, resolution matrix), and
`LakeTransitionPolishTests` (V0.1.1D-B.3 regression list A - P, seeds 926747 / 204018). Biomes: `BiomeTests` (A - T, synthetic climate fixtures, seed report). Climate: `ClimateTests` (A - T,
synthetic barrier / aquaplanet / seam scenarios, reference planet and performance reports).
