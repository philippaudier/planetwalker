# Planetwalker — Genesis V0.2.0F (Physically Based Planetary Surfaces)

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
defaults (currently `Genesis V0.2.0C`). Future schemas are rejected; older schemas go through forward-only migration
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

## Adaptive Terrain Foundation (V0.2.0A)

Foundation for exploring every point of a planet: a quadtree of patches over the six cube faces, sampling the
existing elevation field. **Not** an explorable planet yet: the existing planet rendering stays the default and
unchanged; the adaptive terrain is only visible in the opt-in **ADAPTIVE TERRAIN DEBUG** section of the panel.

**Patch identity** (`Terrain/PlanetPatchId`): face (the existing numbering +Y, -Y, -X, +X, +Z, -Z), level, integer
x, y — packed into one 64-bit key (face 3 bits | level 5 | x 24 | y 24): no floating point, no GUID, no collisions,
splitmix64 hash. At level L a face is a 2^L x 2^L grid; x grows along the face's A axis ("east"), y along B ("north");
(0, 0) is the south-west corner (face coordinates (-1, -1)). Children: 0 = SW, 1 = SE, 2 = NW, 3 = NE. Levels 0-24
are addressable; geometry is exact down to level `MaxLevelForResolution(r)` (20 for 16 quads per patch).

**Coordinates** (`Terrain/CubeSphereCoordinates`): the existing projection only (`CubeSphereGenerator.GetFaceAxes`,
`CubeToSphere`, `GridCoordinate`). Every adaptive vertex lies on an integer face lattice of r 2^L cells and is
computed with the planet mesh's own formula, so shared vertices (same level, across levels at even positions, across
face edges) are bit-identical, and at lattice = mesh resolution the adaptive vertices equal the planet mesh vertices
bit for bit. The inverse (direction to face coordinates) runs Newton iterations in double on the same mapping
(round trip < 1e-6 on the unit sphere).

**Neighbors** (`Terrain/PatchNeighbors`): equal-level edge neighbors with integer arithmetic on the cube — the
neighbor center is stepped across the edge and folded onto the adjacent face, whose axes give its address; no
hand-written orientation table, so rotations / mirrorings between faces follow from the face frames. Reciprocal,
exactly one shared edge each, three patches per cube corner (pairwise adjacent), corner contact never counted as
adjacency. Cross-level: `NeighborLeaves` returns the coarser leaf, or every finer leaf along the edge (any depth).

**Sampling and meshes** (`Terrain/TerrainElevationSampler`, `Terrain/PatchMeshBuilder`): `Planet.Height.GetElevation`
unchanged (same units, no added noise, no mesh dependence); surface point = `dir * radius * (1 + elevation)` as the
planet mesh. A patch is a fixed (r + 1)^2 grid (default r = 16, independent of the depth), the planet faces' triangle
pattern and outward winding, positions relative to the patch center (precision at depth), reused buffers.

**LOD selection** (`Terrain/TerrainLodSelector`): screen-space error, deterministic, no time or randomness. Error bound
of a level-L patch: vertex spacing s = 1.25 R (pi/2) / (r 2^L); geometric error e = min(relief, slope x s) + s^2 / 8R
(terrain displacement between vertices with an assumed steepest slope, 1 = 45 degrees, capped by the planet's relief
range; plus the sphere's sagitta). Projected error = e x K / d, K = viewport height / (2 tan(fov/2)), d = distance to
the patch's bounding sphere (whole relief range included). Patches entirely beyond the horizon (accounting for the
highest terrain) get no error: no refinement behind the planet or past the horizon; no frustum culling (the covering
does not depend on where the camera looks). The leaf with the largest error (relative to its threshold) is split
first; each split first splits coarser edge neighbors (2:1 balance). A split that would exceed the leaf budget is not
made and refinement stops: the covering is always complete, non-overlapping and coarser rather than holed
(`BudgetLimited` is reported). Hysteresis: a patch split last time stays split while its error exceeds threshold x (1 -
hysteresis) (default 25%). Steady-state selection allocates nothing.

**Crack contract (V0.2.0B)**: edges at equal level are bit-identical (tolerance 0, tested on every case incl. face
edges). Coverings are 2:1 balanced, so an edge only ever meets one level coarser: the fine side's even edge vertices
coincide exactly with the coarse vertices; its odd vertices are T-junctions (`EdgeTransition.Coarser`, measured by
`PatchCoverage.TJunctionGap`). **V0.2.0A is not crack-free**; the debug overlay draws these edges red. V0.2.0B will
stitch them (fine-side edge vertices snapped to the coarse edge, or alternate index buffers per transition mask).

**Limits** (`AdaptiveTerrainSettings`, diagnostic and runtime only, never archived): patch resolution 16, levels 0-16
(capped by exact coordinates), screen error 4 px, hysteresis 25%, leaf budget 512 (provisional), patch meshes 768,
assumed slope 1. Mesh estimate: (r + 1)^2 x 28 B + 6 r^2 x 2 B = 11 KB per 16-quad patch; 512 patches = 5.4 MB.

**ADAPTIVE TERRAIN DEBUG** (panel; `Terrain/AdaptiveTerrainDebugView`, off by default): *Show Patch LOD* runs the
selection for the orbital camera. *Overlay* (default) drapes the patch outlines over the existing planet (one line
mesh; colors by level, white cube-face edges, red LOD transitions). *Mesh Preview* (opt-in) draws the selected patches
as meshes in level colors with a diagnostic hillshade (lit from the local north-west, independent of the sun) and
their vertex grid (*Vertex Grid*, shows the triangle density), and hides the six face meshes meanwhile (never two planets); it builds at most 6 ms of
patches per frame, nearest first, and keeps showing the last complete covering until the new one is built. *Freeze
LOD* keeps the covering while the camera moves. LOD min / max level, screen error and patch budget sliders apply
without regeneration. (The GENERATION *Resolution* slider is unrelated: it sets the existing orbital mesh, still the
default rendering, and is part of the archived world.)

**Finding — terrain detail limit**: the existing elevation field has no feature finer than about R / 30 (its highest
octaves: detail 24 x 2^3 and ridges 5 x 2^3 cycles per radian). The orbital mesh at resolution 64 (spacing R / 41)
already samples most of it, so patches finer than level ~3-4 add triangles but no new relief: the LOD adapts (vertex
grid), the terrain looks the same. Ground-level exploration will need finer deterministic detail, a generation change
deliberately left out of V0.2.0A.
Statistics: patches / budget, levels, selection time, transition edges, meshes, pending builds, estimated memory.
Ownership: the component lives on the planet object, creates its meshes (`PatchMeshCache`: bounded, recycled LRU,
destroyed explicitly) and releases everything when switched off or destroyed (leaving Play mode); regeneration or
loading a world invalidates its patch data on the next frame. (V0.2.0B: thumbnails capture the legacy faces, see below.)
Not a streaming system: patch meshes are built synchronously on the main thread.

## Seamless Adaptive Terrain (V0.2.0B)

Panel **ADAPTIVE TERRAIN** > **Renderer: Legacy / Adaptive** (`Terrain/AdaptiveTerrainRenderer`). Legacy (the six
face meshes) stays the default and the fallback. Adaptive draws the terrain as crack-free adaptive patches with the
same natural appearance, built progressively; switching never regenerates the planet, never changes the seed or any
world setting and never marks the world as modified. The legacy faces stay on screen until the adaptive covering of
the current planet is complete and has caught up with the LOD target (warm-up), so no switch, regeneration or world
load shows an empty planet; only one terrain is visible at a time (the faces' renderers are off while adaptive is
shown). Scientific views always use the legacy faces.

**Stitching** (`Terrain/PatchTopology`): coverings are 2:1 balanced (V0.2.0A selector), so an edge meets at most one
level coarser. Each patch is triangulated as 2 x 2 blocks, each a fan of 8 triangles around its center vertex; an edge
facing a coarser neighbor (stitch mask bit) skips its odd vertices (fans of 7, or 6 at a corner), so it consists of
exactly the coarse neighbor's segments between even vertices, which are bit-identical on both sides (shared lattice).
No T-junction, no degenerate or overlapping triangle, any combination of the four edges. One index buffer per mask
(16), shared per resolution; a neighbor change only swaps a patch's index buffer. Tested: the union of every patch of
mixed coverings (cube corner, face edge, 3-9 levels) is a closed, consistently oriented surface (each edge shared by
exactly two triangles of opposite orientation, V - E + F = 2). Small skirts (2e-5 R + 2% of the vertex spacing) hang
under each edge as a guard against sub-pixel gaps from float32 rounding of patch-relative positions only; they are
not the continuity mechanism.

**Normals and colors**: normals use one finite-difference spacing for every level (a quarter face / 256, finer than
any feature of the current elevation field), so a vertex shared by two levels or two faces has the same normal on
every side (no lighting seam). Colors use the legacy rule (`PlanetMeshBuilder.TerrainColor`: Natural Planet on land,
elevation palette on the ocean floor; the same `NaturalSurface` instance, evaluated with per-thread buffers) and the
standard sRGB curve. On the shared lattice the adaptive points and colors equal the legacy faces (tested to 2e-6 R and
2e-3). Stitched edges drop their odd vertices, so both sides interpolate the same endpoints.

**Displayed covering** (`Terrain/AdaptiveCovering`): follows the LOD target only through atomic replacements — a leaf
replaced by its four children once all four meshes are ready, four siblings replaced by their parent once it is
ready — and only when the covering stays 2:1 balanced (neighbor replacements the target also needs go first: splits
coarse to fine, merges fine to coarse). Complete, non-overlapping and balanced at every frame; a parent stays visible
until all its children can replace it; refinement proceeds a level at a time, nearest / largest error first.

**Construction** (`Terrain/PatchBuildScheduler`, `Terrain/AdaptivePatchGeometry`): each frame the renderer (1) re-runs
the selection only if the camera moved by more than 0.2% of its altitude or turned, (2) uploads at most N finished
patches (main thread: `Mesh` writes), (3) advances the displayed covering, (4) requests the missing patches in priority
order. At most *Max Pending* requests are queued or building; requests that leave the needed list are cancelled.
Geometry is built on worker threads (runners pulling the next job; default processor count - 2, between 1 and 4) or,
with *Background Build* off, on the main thread within the *Construction Budget* (ms per frame). Workers read only an
immutable `AdaptiveSurfaceSnapshot` (planet, Natural Planet data, copied color ramp, color space, resolution) and
write pooled CPU buffers; they never call Unity. Each snapshot has a generation number: regeneration, world loads and
appearance changes create a new one, cancel queued work, and every result of an older generation is discarded before
it could reach a mesh. Meshes (`PatchMeshCache`): bounded slots (max(*maxPatchMeshes*, 2 x budget + 64)), recycled
least recently used first (displayed and requested patches protected), destroyed explicitly when switching to Legacy,
on regeneration reuse, scene unload and Play-mode exit.

**Rendering**: the patches are drawn with the planet's terrain material and a property block filled by the same
writers as the face renderers (terminator wrap, haze, cloud uniforms and time, night / twilight) plus the planet frame
(`_PlanetFromWorld`, `_AdaptivePatch`: patch vertices are relative to their patch origin; the shader recovers the
planet-space position for cloud shadows; both are zero for the legacy faces, which are unchanged). Per-patch bounds
let Unity frustum-cull the draws. LOD selection weights patches entirely outside the view frustum by 0.25 (they
refine later; the covering stays complete, so turning the camera never reveals a hole). Patch-geometry spheres are
cached: selection about 3x cheaper than V0.2.0A.

**Compatibility**: ocean, bathymetry, atmosphere and clouds do not depend on the terrain mesh (unchanged). Rivers and
lakes are still built on the legacy face mesh (same geography, same data per generation); close up, the adaptive
terrain can sit slightly above or below them where the legacy triangles cut corners (river lift / lake shorelines:
a V0.2.0C topic). Thumbnails always capture the complete legacy faces, forced visible for that synchronous render only
(never a half-built adaptive state, never a wait). The diagnostic overlay outlines the displayed adaptive covering; the
diagnostic mesh preview is only available with the Legacy renderer.

**Precision and scale (for V0.2.0C)**: the planet radius is 100 units; positions are float32. Patch vertices are stored
relative to their patch origin (subtracted in double) and the draw matrix is computed in one place
(`AdaptiveTerrainRenderer.PatchMatrix`), so a floating origin (camera-relative matrices computed in double) only
changes that function. World space is still float32 around the planet center (about 1e-5 units, 1e-7 R, at the
surface): below that scale the world transform, not the patches, is the limit. Physical scale (units per meter) is
not defined yet.

**Manual check (Play mode)**: 1 seed 1337 in Legacy, capture; 2 Adaptive: compare coastlines, colors, ocean, atmosphere;
3 orbit: no hole, flicker or lighting seam; 4 slow approach: levels refine, no background visible; 5 rapid zoom in / out:
pending stays bounded, cancelled / discarded grow; 6 low patch budget: coarser, still complete; 7 Show Patch LOD:
red stitched transitions, white face edges; 8 regenerate while building: never the old planet; 9 save / load two worlds
in the World Library: previews and worlds correct; 10 switch Legacy / Adaptive repeatedly: no corruption, memory flat;
11 still camera: builds settle to 0 pending; 12 several minutes of exploration: no stall or leak.

## Planetary Scale & Multi-Scale Terrain (V0.2.0C)

**Physical scale** (`Core/PlanetScale`, one authoritative source, `PlanetInstance.Scale`): a planet has a render radius
(`radius`, 100 units, the visual size, unchanged) and a physical radius (`radiusKilometers`, 6371 km = Earth by
default, archived with the terrain group). Horizontal distances are physical (1 unit = 63.71 km at Earth scale);
vertical relief stays the artistic, dimensionless model of V0.1 ("Mountains 4.5%" = 4.5% of the radius, about 30 x the
real world). `PlanetScale` converts, formats and measures (`FloatPrecisionMeters`: a float32 coordinate of magnitude 100
units steps by 0.49 m at Earth scale); it never reinterprets a generation setting. `ClimateSettings.planetRadiusKm`
(6371, climate only) is a legacy parameter of the climate model, left untouched; unifying the two is future work.

**Coordinates** (`Core/Vector3d`, `Core/PlanetCoordinates`): planet space is the planet object's local space (center at
the origin, the cube-sphere axes, render units) in double precision. `PlanetTangentFrame.At(direction)` gives Up /
North / East (right handed, East = North x Up), with the generator's own pole rule (+Y reference, +X within ~8 degrees
of the Y poles): orthonormal and finite everywhere, deterministic, the switch explicit. `PlanetCoordinates` converts
direction + altitude to positions, steps along the surface in meters, measures angles stably.

**Floating origin** (`Core/PlanetRenderFrame`, on the planet root; automatic by default, *Floating Origin* toggle and
*Rebase Now* in ADAPTIVE TERRAIN): the render origin is a planet-space point mapped to the Unity world origin. There is
no stored origin: it is derived from the root's float world position, so it is always exactly representable and
`transform.localToWorldMatrix` users (legacy faces, ocean, atmosphere and cloud shells, rivers, lakes, overlays,
thumbnails, `_AtmoShell`) and the adaptive terrain (`PatchMatrix`: translation computed in double) agree bit for bit.
The orbit camera computes its position in double (yaw / pitch / distance), asks the frame to rebase when its render
distance from the world origin exceeds max(0.25 x altitude, 1e-3 units), then places itself; everything runs in one
phase before any draw (`PlanetOrbitCamera` at execution order -90, draws in LateUpdate). Near / far clip planes follow
the altitude above the terrain under the camera (near = 5% of the altitude, floor 5e-5 units; far = distance + 2 R).
Altitude, zoom steps and rotation speed also follow the terrain under the camera (`SurfaceRadius`: the canonical
elevation), so a plain is as reachable as a summit (minimum altitude 0.001 units = 64 m at Earth scale). Legacy faces
and the ocean sphere keep float32 vertices of magnitude 100 (0.5 m quantization near the surface): the legacy renderer
is the orbital reference; V0.2.0D should render water adaptively.

**Terrain detail hierarchy** (`Generation/TerrainDetail`): the canonical elevation is the planetary terrain e0 (L0:
continents, mountain systems, V0.1 detail; unchanged, `GetBaseElevation`) plus three deterministic bands of
continuous 3D noise on the sphere. The spectrum continues e0's own finest octave (`detailFrequency` x 8, amplitude
`detailStrength` x 0.067) with a gain of 2^-0.8 per octave (Hurst 0.8), each octave doubling the frequency, down to a
wavelength of 80 m at the planet's physical scale (11 octaves at Earth scale: 104 km .. 102 m; 730 m .. 2.9 m of
amplitude, x 3 inside range cores). Bands by physical wavelength: **Regional** >= 20 km (3 octaves: 104, 52, 26 km),
**Local** 1.5-20 km (4 octaves), **Micro** < 1.5 km (4 octaves). Physically plausible relief (hundreds of meters at
100 km, meters at 100 m) under the exaggerated planetary relief. Geographic conditioning, from upstream signals only:
*coasts* (each band fades to zero within 6 x its largest amplitude of elevation above sea level: no band can move the
shoreline or sink a point below the sea, nothing under water: the coastline of e0 is preserved exactly); *mountains*
(range core envelope and influence, from the same evaluation as e0: regional and local bands become ridged and are
stretched along the range direction, secondary crests parallel to the main ridges with broad valleys between, their
amplitude grows with the core like e0's detail; regional valley floors are flattened by a soft maximum, intermontane
basins); *relief* (`TerrainReliefField`: slope of e0 on a 128-cell gnomonic face grid built once per planet, 20-70 ms
in parallel: flanks and escarpments get the full dissection, flat ground, plateau tops and valley floors keep 35%);
*character* (a seeded planet-scale field: smoother / eroded regions vs rugged ones for the local and micro bands, a
terrain-style signal standing in for aridity). Dependency order: e0 -> relief field -> detail; climate and biomes are
downstream and never read (tested by reflection). Controls (SURFACE DETAIL): *Enable Surface Detail* (off reproduces
V0.2.0B bit for bit), *Regional / Local / Micro Detail* (0-2 x, each scales only its band), *Show Detail Layers* with
Combined / Regional / Local / Micro views (signed displacement, blue down / red up; the adaptive terrain shows them too).

**Determinism and band limiting**: the detail is a pure function of the direction (and the immutable per-planet
context): same for every patch, level, face, thread and frame; two octave seeds derive from the world seed. LOD controls
sampling density, not identity: patch positions always use the full function (spacing 0). `GetElevation(direction,
spacing)` band-limits the detail to a sample spacing (octaves below 2 x the spacing are gone, whole from 4 x): the
hydrology, climate, bathymetry and slope grids sample it at their own spacing (they see the relief they can resolve,
never aliased noise, so rivers follow the regional valleys they resolve), and normals (legacy faces at the mesh spacing,
adaptive patches at their level's spacing) shade the relief the mesh resolves: no aliasing grain, every octave shaded
once resolved. Vertices shared by two levels get normals one octave apart (bounded, the usual LOD shading step; the
Natural Planet colors read the slope field, so colors have no seam). The LOD error model adds the typical slope of the
resolved octaves and the amplitude of the unresolved ones (`TerrainDetailProfile.TypicalSlope / UnresolvedAmplitude`):
orbital levels change by < 60%, near-ground levels refine more; the 512 budget and the balanced stitching are unchanged.

**Precision**: the adaptive patch geometry runs in double (V0.2.0C): lattice directions (`LatticeDirectionD`, exact
rationals, bit-identical for shared vertices), the detail octaves (`PlanetNoise.Gradient(double...)`: fine octaves keep
sub-cell precision at coordinates of 1e5-1e6), positions (direction x radius in double, relative to the float-rounded
patch origin the draw matrix uses, rounded once), normals (double finite differences). The planetary terrain itself is
single precision and its evaluation carries a few centimeters of rounding noise from sample to sample, which a
sub-meter grid turns into visible grain; below the fine lattice spacing (`FineSpacing`, 2^16 cells per face = 150 m at
Earth scale) the canonical e0 is `FineBaseElevation`: a Catmull-Rom interpolation of e0 on that global lattice (exact
to the meter away from crest lines, which it rounds over one cell, up to ~20 m; vertices of coarser levels are lattice
points and get the sample itself). One canonical surface for every level; legacy faces (direct float e0) differ from it
by the float noise and the crest rounding (invisible from orbit). Remaining floor: float32 elevation values (0.6 cm at
Earth scale) and the float32 camera-space geometry near the origin (millimeters).

**Save format**: five new terrain fields (`radiusKilometers`, `surfaceDetail`, `regionalDetail`, `localDetail`,
`microDetail`), schema 1 unchanged. Generator version `Genesis V0.2.0C`; V0.1.3A and V0.1.3B archives are exact: an
archive without `terrain.surfaceDetail` (every archive before V0.2.0C) loads with surface detail off
(`PlanetArchiveSerializer.ApplyCompatibilityDefaults`, no warning), so its terrain is bit-identical to what it recorded;
new worlds start with it on. V0.2.0A / V0.2.0B generator strings load best-effort (they never named a committed world).

**Measured (offline .NET 8, Ryzen 5 PRO 8640HS, seed 1337 at Earth scale)**: canonical sample 2.2 us (planetary only
1.9 us; band-limited 1.9 us); planet construction 230 ms with the relief field (14-35 ms without; parallel); the
adaptive pipeline with 3 workers settles a 512-patch covering in 0.21-0.31 s from distant orbit to 30 m altitude
(V0.2.0B: 0.16-0.35 s), main thread 0.3-0.9 ms per frame. Look harness (hillshade of the canonical terrain, 320 x 320
samples at the view's spacing): mountain (seed 1337) at 100 km / 10 km / 1 km / 100 m windows: relief 44.7 km / 4.7 km /
1.1 km / 39 m, mean slope 0.72 / 0.97 / 1.18 / 0.47; plain: 10.5 km / 444 m / 85 m / 14 m, mean slope 0.08 / 0.10 /
0.19 / 0.18. Plains stay 5-10 x calmer than mountains at every scale.

**Manual check (Play mode)**: 1 seed 1337, surface detail off, Legacy vs Adaptive = the V0.2.0B reference; 2 detail on:
continents and coastlines unchanged from orbit; 3 descend on a mountain: massif structure (100 km) -> secondary ridges
and valleys (10 km) -> local relief (1 km) -> fine irregularities (100 m), no sudden change; 4 same descent over a
plain: visibly calmer; 5 approach a shoreline: no new islands, no water / terrain intersections; 6 both poles: no
instability; 7 rapid zoom orbit <-> ground: no hole, no stale patch, pending bounded; 8 save a world with custom detail,
leave / re-enter Play, load: same settings and geography; 9 load a V0.1.3B / V0.2.0B archive: terrain unchanged
(detail off); 10 floating origin: at 100 m altitude the origin offset line shows rebases and terrain, ocean, clouds,
atmosphere stay aligned; *Rebase Now* changes nothing visible.

## Surface Detail Foundation & Ground Exploration (V0.2.0D)

**Explore Surface camera** (`CameraControl/PlanetExploreCamera`, on the orbit camera's GameObject, exactly one of the
two enabled; *Explore Surface (F)* / *Orbital View (F)* in SURFACE EXPLORATION or the F key): free flight from orbit
down to a few meters. State in double, in planet space: position and a unit forward vector kept tangent to the sphere
and parallel-transported at every step (local up = radial direction): no Euler angle, no reference axis, nothing special
at cube-face edges or the poles; pitch is a separate angle clamped at +-89 degrees. Controls: click the view to capture
the mouse (Esc releases; right button held also looks), WASD along the local horizon (horizontal motion keeps the
radial distance), Space / Ctrl up / down, Shift x5. Speed = one clearance per second (x *Exploration Speed*), 4 m/s ..
3000 km/s, a pure function of dt (frame-rate independent). Entering above 150 km starts a controlled descent
(exponential approach of 2 km of clearance, cancelled by any movement); *Descend* goes down a decade per press, the last step at eye height (1.7 m).
Clearance is measured against the canonical elevation in double (`Height.GetElevation`, the function the adaptive
patches sample, never a mesh) or the sea surface, whichever is higher; the camera never goes below *Min Clearance*
(1.5 m by default, below eye height). Placement goes through the floating origin (rebase first, then place), near plane = a quarter of the
clearance (>= 0.1 m), far plane past the planet; reversed-Z keeps the depth precision at these ratios. Leaving the mode
hands the orbit camera the same direction and distance. A regeneration or a world load keeps the pose and only pushes
the camera above the new terrain. Archives still record the orbit view (no format change).

**Close-range appearance** (`PlanetTerrain.shader`, adaptive patches only, shown with surface detail; presentation only:
heights, sampler and meshes untouched): soil patches (64 / 16 m), paler patches, brightness grain (8-2 m), bare rock
where the ground is locally steep (the patch normal at that distance carries the 80 m band the coarse rock field cannot
see; snow and ice stay clean) and a fine bump (8-1 m) on the shading normal. The noise is a function of the planet-space
position in meters, from an origin snapped to a 1024 m lattice near the camera (`AdaptiveTerrainRenderer.WriteGroundDetail`,
in double) and periodic over that lattice: small precise float coordinates, no swimming, no pops when the origin moves,
independent of the floating origin and of the patch level. Every octave fades out with the pixel footprint (no aliasing,
nothing from altitude). *Ground Detail Strength* on the renderer (0 = off).

**Lighting from the ground**: the very dark areas seen up close were slopes facing away from the sun lit only by the
faint space ambient (normals correct). A day-side *skylight* (terrain and ocean: the atmosphere color, hemispherical,
fading through the twilight, zero at night) fills them; the night stays black. From inside the atmosphere the sky shell
adds the sky dome a camera inside the layer sees (`SkyFromInside`: blue zenith, paler horizon, warm low-sun tint, sun
glow; weighted by the depth in the layer, zero from orbit: the orbital look is unchanged). The cloud layer is drawn on
both faces with an analytic planet occlusion and is depth tested near the ground (exaggerated relief can rise above it);
from orbit it keeps ZTest Always. Close-range cloud detail (`PlanetCloudsCommon.hlsl` `CloudFine`, material *Close-Range Detail*): 8 octaves continuing the orbital detail from ~R/300 (50 km) down to ~200 m, billowy, moved by the same flow map, each faded out with the pixel footprint (pixel-identical from orbit); they break up thin edges, shape the opacity of the lobes and light them from the sun side (a lobe denser towards the sun is in its own shade). Seen from below up close, dense lobes are more opaque. Ground shadows keep the coarse field.

**Diagnostics** (SURFACE EXPLORATION, 4 updates per second): mode, altitude above sea level, clearance (terrain or sea),
speed, descent / mouse look state, adaptive level under the camera and its vertex spacing, surface detail on / off and
band values.

## Surface Identity & Geological Detail (V0.2.0E)

**Surface geology** (`Terrain/SurfaceGeology`, presentation data only: elevation, coastlines, hydrology, archives
untouched). Per surface point: *bedrock* exposure, *sediment* (loose material), *erosion* character, *stratification*,
reference slope, concavity, downslope direction, province rock color. Inputs, all upstream and deterministic: the
canonical elevation band-limited to the lattice spacing, the mountain envelope (core, influence) of the same evaluation
(`PlanetHeightGenerator.GetElevation(direction, spacing, out context)`, no extra cost), the terrain character field of
the surface detail, the climate (aridity from precipitation and temperature, Natural Planet only) and three seeded
province fields (sedimentary vs crystalline, rock family, local variation). Rules: bedrock on steep and convex ground,
in mountain cores and arid land, never in hollows or on the coast; sediment on flat and concave ground (local and
~600 m broad concavity, so valley floors and slope feet fill as continuous bodies), never on bedrock; erosion with
slope, aridity and rugged character; strata in sedimentary provinces, favored by aridity. Rock families: grey / pink
granite and basalt (crystalline), red sandstone and ochre limestone (sedimentary), mixed by the province fields: a desert
holds rock, gravel, sand and layered plateaus; no biome-to-material table.

**LOD and precision.** Characteristics live on the nodes of a global cube-face lattice (2^16 cells per face, ~150 m at
Earth scale, the fine lattice of the planetary terrain): a node is a least-squares quadratic fit of the elevation on its
8 neighbours in its tangent plane (slope, downslope, Laplacian independent of the lattice's skew near cube corners),
then the rules; patch vertices interpolate the nodes bilinearly. Within 3 cells of a face edge the sample is blended
with the same point on the neighbouring face's lattice (half and half on the edge: worst jump across edges 0.05, the
local gradient). Every patch level at or finer than the lattice reads
the same nodes (geology at a location identical across levels: rock does not move with the LOD); coarser levels use
their own vertex lattice (prefiltered). Regional inputs (climate, character, provinces) on a 512-cell lattice. Pure
functions of (planet, face, i, j), per-thread caches, worker-thread safe. In the shader every pattern is a function of
the planet-space position in meters from the V0.2.0D snapped origin (1024 m lattice, periodic noise): no swimming, no
dependence on the floating origin; each octave fades with the pixel footprint.

**Appearance** (`PlanetTerrainGeology.hlsl`, adaptive patches; per-vertex attributes in one interleaved stream,
`TerrainVertex`, 80 bytes): outcrop boundaries broken by noise elongated down the slope; rock body variation, strata at
their true altitude (the planet's height above sea level, gently warped; 18 m and 4.5 m beds, alternating warmer beds,
bedding partings up close), erosion streaks along the downslope direction, weathering (darker hollows); jointed blocks
(3D Voronoi fractures, 4 m blocks, discontinuous, below ~12 cm per pixel), mineral grain down to 1.5 cm; sediment as
debris derived from the local rock (alluvium on vegetated land) with stones at 0.5 m / 12 cm; arid soil takes some of
the rock's color; snow stays on top except on steep rock; material-dependent bump (rough rock, stones, soft soil).
No new geometry: the authoritative elevation, the meshes and the camera clearance are unchanged (the existing 80 m band
was judged sufficient for the pilot). *SURFACE GEOLOGY* section: *Geological Appearance* (off = the V0.2.0D look),
*Geological Detail*, *Geology View* (Off / Identity / Bedrock / Sediment / Erosion / Reference Slope).

**Fixes.** *LOD shading seam*: patches carry the next coarser level's normal near their edges; along an edge stitched
to a coarser neighbour the normal blends to it (band of 1/8 patch), so shared vertices are lit identically. *Rock moving
with the LOD*: rock comes from the LOD-independent geology instead of the displayed normal. *Blue / dark shadows*: the
skylight is half desaturated (sky dome, not the limb color) and a ground bounce (local ground color, follows the sun's
height, zero at night) lights slopes facing away from the sky; the night is unchanged. *Horizon band*: identified as the
cloud deck seen from a summit above it (exaggerated relief rises above the 38 km layer), a grazing view of an infinitely
thin shell whose ray ends on the ocean and so received almost no haze; inside the atmosphere the grazing deck now fades
to the horizon sky and loses opacity (orbit pixel-identical). Remaining: the deck's own geometric horizon stays a sharp
line (a thin shell has no thickness to fade through; needs volumetric clouds).

## Physically Based Planetary Surfaces (V0.2.0F)

**Materials.** Six CC0 scans from Poly Haven (granite, layered sandstone, pale limestone-like rock, dark basalt-like
rock, gravelly desert sand, rocky debris), 2K, base color / OpenGL normal / roughness / AO / height, in
`Materials/PlanetSurface` (provenance: `Docs/ThirdPartyAssets/SurfaceMaterials.md`). Import
(`Editor/PlanetSurfaceTextureImporter`): base color sRGB BC1; normal map BC7, no green flip; the roughness file is
imported as the packed mask (R smoothness = 1 - roughness, G AO, B height, read from its sibling files). The library
asset (`Resources/PlanetSurface/PlanetSurfaceLibrary`, Planetwalker > Build Surface Material Library) references them;
the three Texture2DArrays are assembled on the GPU at first use (no stored duplicate) and set on the adaptive
terrain's single property block (no material per patch or per material, no extra draw call).

**Distribution** (`PlanetTerrainMaterials.hlsl`; CPU mirror `SurfaceMaterials`): the V0.2.0E geology stays the
authority. Rock share = the bedrock mask with its breakup; rock split by province (sedimentary weight, family field:
granite / basalt / sandstone / limestone); loose ground split by slope and erosion (gravel / dry sand). Per pixel only
the two largest weights are sampled (each reduced by the third: continuous rank changes), blended by their height maps.
New per-vertex inputs: sedimentary weight and family (free channels of the V0.2.0E vertex, same 80 bytes).

**Projection.** Triplanar on the planet's axes in planet-space meters from the snapped origin (power-12 blend, grazing
projections dropped, whiteout normals with axis signs): floating-origin and snap invariant, no cube-face seam. The scale
follows the pixel footprint in steps of 8 (2 m, 16 m, 128 m: all divide the 1024 m anchoring period), two steps blended,
so a tile always spans ~300+ pixels (no visible repetition); the detail fades out above 4 m per pixel (orbit: the
procedural cost). Color: the scans' detail with their mean moved towards the geology's macro colors (province tint with
strata, streaks, weathering; biome ground color), more so on layered rock (the scans' own bedding follows the projection
axes; the geology's strata are horizontal). Lighting: GGX specular (F0 0.04, metallic 0) + Lambert with the existing
wrap, planet and cloud shadows, skylight, ground bounce, AO on the ambient; night unchanged. The V0.2.0E Voronoi
fractures, mineral grain and stone bump are not used in PBR mode (the scans carry real ones).

**Coast** (addendum). Geology: the V0.2.0E coastal term (sediment over ~1.9 km of elevation: a uniform ring) is replaced
by a metric beach band, 3-35 m above the sea along the shore (a seeded ~50 km field), wider on sedimentary coasts, only on
gentle slopes; on steep shores bedrock reaches the water. Shader: a wet band above the waterline (0.4-2.5 m, varying,
narrower on steep shores) and everything below sea level: darker (porous sand more than rock), smoother. Ocean:
water thickness along the view ray from the camera depth texture (URP PC asset), in meters: clear and turquoise over the
first meters, deep water unchanged, the waterline where the terrain meets the sea (cliffs stay sharp). No foam.

**V0.2.0F.1 — stable at every scale.** Cause of the popping (measured, fixed camera, LOD refined 384 -> 1024 patches):
the projection scale was chosen from screen derivatives of the position, constant per triangle, so it changed with the
mesh LOD (and the camera's rotation); a refinement changed the image by 20.3 (mean abs difference, valley at 10 m)
vs 7.5 in the procedural mode. The scale now follows a *stable footprint* (distance x pixel angle at a reference of
1080 lines: independent of triangles, rotation and resolution): 9.9 for the same change (the rest is geometry and
normals). Scale steps 2 / 16 / 128 / 1024 m: a pure scale over 60 % of each step, a smoothstep crossfade over the last
40 % (every property blended, normals renormalized; two scales sampled only inside the bands); normal strength fades
continuously with the scale (1 .. 0.29) and normals are not sampled when negligible; textures continue to 15-30 km
(distant mountains keep their rock texture). Coasts and broad sandy surfaces: landscape-scale brightness / warmth
patches on loose ground and a damp zone above the wet band (4-10 m, varying). Cube-face continuity verified by
convergence (difference across an edge at 20 / 2 / 0.2 m: 0.052 / 0.0052 / 0.0005 for the geology, 0.18 / 0.018 /
0.0018 for the weights: continuous, a steep gradient). Colored fringes: the Genesis scene's Global Volume profile
(SampleSceneProfile) enables Chromatic Aberration (0.2), the URP effect producing red / cyan edges; not a terrain
defect (manual check: disable that override). Flat-color audit: below sea level the vertex color is the elevation palette's beach / shallow-water tone (a band seen through the clear water): replaced there by the geology's debris tone; ground color transfer reduced to 0.3-0.6 (was 0.55-0.95, close to a flat fill); snow keeps the texture's grain; wet and damp edges irregular (planet-anchored noise); the slope-oriented patterns and the second material fade in instead of switching at a threshold (straight lines across triangles on slopes). *View: Scale* shows the projection scale, *View: Tint* the final albedo over the scans' own color (neutral grey = nothing painted over the texture).

**V0.2.0F.2 — biome ground materials.** Five more CC0 scans (green grass, dry grass, forest floor, soil, snow; 11 layers):
the six V0.2.0F materials covered rock, sand and gravel only, so every vegetated biome was a tinted sand. Per vertex, the
Natural Planet biome blend now also gives its ground cover (green grass, dry grass, forest floor, snow: per-biome tables in
`NaturalSurface`, TEXCOORD4, vertex 84 bytes). Weights: snow (also over gentle rock), vegetation thinned on steep and eroded
slopes and on beaches (3-20 m above the sea), bare ground as gravel on slopes, soil around vegetation, sand in deserts and on
beaches; snow keeps its own color. Altitude stability: the normal strength is nearly constant along the scales (V0.2.0F.1
faded it to 0.29: brighter as the camera climbed) and the scale crossfade preserves contrast (deviation from the mean scaled
by 1 / sqrt(f^2 + (1 - f)^2), color and normal tilt). Texture arrays: 11 x 2048^2 (GPU about 120 MB; editor profiler
reports 235 MB).

**V0.2.0F.3 — ground-level stability.** The "wall" seen at 1.7 m on mountains was the near clip plane: it was a quarter
of the *vertical* clearance, but above a steep slope the surface in front is much closer (1.7 m above a 76-degree slope is
0.41 m from it), so the near plane lay beyond the slope and cut it open. The explore camera now measures the *distance to
the surface* (authoritative height under the camera and on three rings: 25 samples) and keeps the minimum clearance for
that distance (raised along the local up, frame-rate independent); the near plane is a quarter of it (at least 5 cm). No
fog, no clipping trick, no new skirts. Default patch budget 1024 (was 512; mesh slots 2112). Budget differences measured
at a fixed camera (identical camera, identical authoritative heights): they come from the finer levels the larger budget
shows near the camera (valley at 10 m: 100 L16 patches at 1024 vs 8 at 384) — geometry and the level-band-limited vertex
normals, then the materials that follow them; no holes at any budget; mesh vs authoritative height in view at 1024: mean
0.2-0.35 m, max 1.3-2.3 m. Evidence: `Docs/Captures/V0.2.0F.3_Validation/`, report `Docs/Reports/V0.2.0F.3_Validation.md`.

**Controls** (SURFACE MATERIALS): *Materials: PBR / Procedural (V0.2.0E)*, *PBR Detail*, *View* (Final Lit, Material
ID, Albedo, Normal, Roughness, Triplanar), status line. Presentation only: never archived.

## Regional Landform Identity (V0.2.0G.1)

Three landform families shaped from the planet's own geography (`Generation/RegionalLandforms`), between the planetary
terrain e0 and the surface detail bands, so regions read from their shape alone (no texture, vegetation or prop):

- **Mountain valleys** (V0.2.0G.1.1, `Generation/ValleyNetwork`) — the drainage network of the ranges: e0 sampled on
  a 128-per-face cube grid (~78 km cells) and run through the existing `HydrologySystem` (Priority-Flood, steepest
  descent; one strictly lower receiver per cell, so a forest of open trees, never a loop), once per planet (~200 ms).
  Channels: range cells (influence 0.08 and up) draining at least 2 cells; the paths through the cell centers smoothed
  into curves through the edge midpoints (tributaries end where their trunk begins), gently meandering (3.5 km).
  Width grows with the drained area, 5 km tributaries (V) to 22 km trunk valleys (U); depth bounded by the width (walls
  under ~45 degrees), by 5 km and by 45% of the height above the sea; asymmetric walls; calm floors. Lookup per vertex:
  one hash cell, a few segments (no tracing). V0.2.0G.1 drew corridors on the zero lines of a noise: level sets of a
  smooth field on a sphere are closed curves, hence the rings and horseshoes it showed.
- **Plateaus** — inland (continental field above ~0.1), outside ranges, where a seeded 500 km regional field exceeds a
  threshold: uplift 1.2-2.6 km, the e0 undulations flattened by 45% (at most 3 km), lobate margins (90 km and 30 km
  edge noise), steep (~2 km) or gradual (~10 km) scarps by region, never steeper on average than ~45 degrees,
  dissected escarpment bands, calm tops.
- **Coastal plains** — within ~0.05-0.15 of the coastline in continental-field units (~100-250 km at default settings),
  outside ranges, on the coasts a seeded 900 km field selects: the land lowered by 90% of its height above the first
  150 m (the shore itself keeps e0's shape: beaches, cliffs, coastline) and by 3 km at most, a broad inland rise (fall
  line) where the weight fades, gentle swells of at most 60 m (12 km cells).

Guarantees: every change is proportional to the height above the sea (or faded within 2 km of it): **no land / sea
change**, the coastline of e0 is kept exactly; pure function of the direction (no cube face, patch, LOD or camera
input); weights are smooth (no hard region boundary). Scales: coastal plains are hundreds of kilometers wide and part of
the terrain at every spacing (the legacy world, hydrology, climate and biomes see them, identically at any resolution).
Valleys, swells and plateaus (blocks bounded by scarps of a few kilometers, which a 78-156 km analysis grid would alias)
are band-limited like the noise octaves, fading over two octaves below their width: absent from analysis grids of 30 km
and coarser (hydrology, lakes and rivers keep their V0.2.0F.3 basins there). The adaptive patches always sample the
full function; relief bounds sample the landforms complete, the LOD error counts unresolved valleys.

**Setting**: *Landforms* (SURFACE DETAIL, `PlanetDefinition.regionalLandforms`, 0-2, default 1; depths and uplift scale
with it, shapes saturate at 1). A terrain setting, archived. Archives written before V0.2.0G.1 have no such field: they
load with 0 (landforms off: their terrain is bit-identical to what they recorded); generator version `Genesis V0.2.0G.1`,
`Genesis V0.2.0C` stays exact. **Views** (SURFACE DETAIL > Show Detail Layers): *Landforms* (influence: red valley
region, bright corridors, green plateau, blue coastal plain), *Landform Shift* (blue lowered, red raised, +-2.5 km),
*Relief (clay)* (one flat color under the sun: lit terrain material, no geology, material or ground layer).
Before / after evidence (same seed, directions, headings and clearances): `Docs/Captures/V0.2.0G.1/`, report
`Docs/Reports/V0.2.0G.1_Validation.md`.

**Erosion foundation (V0.2.0G.2A, `Generation/PlanetErosion`)** — the erosion stage of the canonical terrain:
`e = e0 + regional landforms + erosion + detail bands`, evaluated once per sample in `TerrainDetail.Evaluate` right after
the landforms (the bands then read the eroded height). `ErosionContext` (immutable, shared: scale, seed, sea level, the
landforms, the valley drainage network and its hydrology, by reference) and `ErosionSampler` (a pure function of the
planet-space direction and the upstream samples: no camera, patch, LOD, floating-origin or thread input). **No visible
erosion yet: the displacement is exactly zero** in V0.2.0G.2A; fluvial incision, hillslope relaxation and deposition
come with V0.2.0G.2B and later, on this data flow. Setting `erosionStrength` (0-2, default 1, 0 = stage off; archived;
archives before G.2A load with 0, so a future erosion never reshapes an older world). View *Erosion Shift* (SURFACE
DETAIL): neutral gray everywhere (zero), dark where the stage is off; the status line reports the stage and its context
construction time. Report: `Docs/Reports/V0.2.0G.2A_Validation.md`.

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
| Terrain | `Terrain/PlanetPatchId`, `Terrain/CubeSphereCoordinates` | Patch addresses and hierarchy; cube-sphere coordinate math on the existing projection |
| Terrain | `Terrain/PatchNeighbors`, `Terrain/PatchCoverage` | Cross-face / cross-level neighbors; covering validation and T-junction measurement |
| Terrain | `Terrain/TerrainElevationSampler`, `Terrain/PatchMeshBuilder` | Adapter to the existing elevation field; fixed-grid patch meshes |
| Terrain | `Terrain/TerrainLodSelector` | Settings, screen-space-error selection, budget, hysteresis |
| Terrain | `Terrain/PatchMeshCache`, `Terrain/AdaptiveTerrainOverlay`, `Terrain/AdaptiveTerrainDebugView` | Bounded mesh ownership; outline overlay; ADAPTIVE TERRAIN DEBUG lifecycle and drawing |
| Terrain | `Terrain/PatchTopology` | Stitched index buffers (16 masks) and skirts |
| Terrain | `Terrain/AdaptivePatchGeometry` | Immutable surface snapshot, thread-safe patch geometry builder |
| Terrain | `Terrain/AdaptiveCovering` | Displayed covering: atomic, balanced split / merge transitions |
| Terrain | `Terrain/PatchBuildScheduler` | Bounded, generation-safe build pipeline (worker threads or main-thread budget) |
| Terrain | `Terrain/AdaptiveTerrainRenderer` | TERRAIN RENDERER: Adaptive — lifecycle, uploads, stitching, natural drawing |
| Presentation | `UI/PlanetGenerationPanel.AdaptiveTerrain` | ADAPTIVE TERRAIN section (renderer selector, LOD, construction, diagnostics, altitude / origin line) |
| Data | `Core/Vector3d`, `Core/PlanetScale`, `Core/PlanetCoordinates` | Double vectors; physical scale contract; tangent frames and planet-centered conventions |
| Rendering | `Core/PlanetRenderFrame` | Floating origin: render origin derived from the root, double conversions, rebasing |
| Generation | `Generation/TerrainDetail` | Detail profile (octaves, bounds), relief field, regional / local / micro detail evaluation |
| Rendering | `Rendering/SurfaceDetailDebugColors` | Surface detail debug views |
| Presentation | `UI/PlanetGenerationPanel.SurfaceDetail` | SURFACE DETAIL section |
| Generation | `Generation/RegionalLandforms` | Regional landforms (V0.2.0G.1): mountain valleys, plateaus, coastal plains; weights, shaping, band limits |
| Generation | `Generation/ValleyNetwork` | Valley drainage network of the ranges (V0.2.0G.1.1): e0 hydrology, smoothed open paths, segment hash, carve lookup |
| Generation | `Generation/PlanetErosion` | Erosion foundation (V0.2.0G.2A): `ErosionContext`, `ErosionSampler` (zero displacement) |

Elevation convention: dimensionless, fraction of the reference radius
(surface point = `dir * radius * (1 + elevation)`), so terrain shape is independent of the
rendering scale.

## Changelog

### V0.2.0G.2A — Erosion Foundation
- Erosion stage in the canonical terrain evaluation (after the regional landforms, before the detail bands), its context
  referencing the drainage network and hydrology; displacement exactly zero (no visible erosion); `erosionStrength`
  setting archived (older archives load with it off); *Erosion Shift* view; tests for zero displacement, baseline
  identity, determinism, LOD and floating-origin independence, archive compatibility and cost.

### V0.2.0G.1.1 — Valley Morphology Correction
- Mountain valleys follow the ranges' drainage network (open, converging, widening downstream) instead of the zero lines
  of a noise, whose closed level sets drew rings and horseshoes. Plateaus and coastal plains unchanged.

### V0.2.0G.1 — Regional Landform Identity
- Mountain valleys along the ranges, inland plateaus with escarpments, low coastal plains, derived from the planet's
  geography between the planetary terrain and the surface detail; coastlines, seeds and the legacy world (hydrology,
  climate, biomes) unchanged. *Landforms* setting (archived; older archives load with it off), landform debug views and
  a lit clay relief view.

### V0.2.0F — Physically Based Planetary Surfaces
- Six CC0 PBR scans (Poly Haven) distributed by the surface geology: texture arrays, footprint-scaled triplanar
  projection, height-blended top-two materials, GGX lighting; Procedural (V0.2.0E) mode kept for comparison.
- Coastal pass: metric beach band on gentle shores, rocky steep shores, wet shoreline, shallow water from the depth texture.
- SURFACE MATERIALS section with material debug views. Unchanged: geography, heights, seeds, archives.

### V0.2.0E — Surface Identity & Geological Detail
- Surface geology (bedrock, sediment, erosion, strata, rock families) on a global LOD-independent lattice; geological
  terrain appearance from 10 km to 1.7 m; SURFACE GEOLOGY section with debug views.
- LOD shading seam removed (coarse-normal blend on stitched edges); skylight desaturated, ground bounce; cloud deck
  horizon from summits attenuated; close-range cloud detail.
- Unchanged: elevation, coastlines, hydrology, seeds, archives (format and content), orbital look.

### V0.2.0D — Surface Detail Foundation & Ground Exploration
- Explore Surface camera (double state, parallel-transported heading, clearance from the canonical elevation, controlled
  descent, floating-origin placement, clearance-driven clip planes); Orbital View / Explore Surface switch (F).
- Close-range ground layer in the terrain shader (soil, rock on local slopes, grain, fine bump; planet-anchored).
- Day-side skylight, sky dome from inside the atmosphere, cloud layer seen from below and depth tested near the ground.
- Adaptive LOD centered under a low camera, no flicker under budget pressure (distance floor, stable small angles,
  unaffordable splits skipped).
- SURFACE EXPLORATION section. Unchanged: seeds, archives (format and content), coastlines, hydrology, orbit controls.

### V0.2.0C — Planetary Scale & Deterministic Multi-Scale Terrain
- Physical scale contract (`PlanetScale`, `radiusKilometers`), double-precision planet coordinates and tangent frames,
  automatic floating origin (`PlanetRenderFrame`), double orbit camera with altitude-following clip planes and
  terrain-following limits. Details: *Planetary Scale & Multi-Scale Terrain* above.
- Deterministic regional / local / micro surface detail conditioned by coasts, mountains, relief and character; band-
  limited sampling for analysis grids and normals; detail-aware LOD error model; SURFACE DETAIL section and debug views.
- Adaptive patch geometry in double precision; smooth fine base of the planetary terrain below 150 m spacing.
- Save format: five optional terrain fields; generator `Genesis V0.2.0C`; older archives load with detail off, exact.
- Unchanged: continents, coastlines, mountain placement, seed interpretation, ocean level, climate, biomes, archives.

### V0.2.0B — Seamless Adaptive Planet Terrain
- **Renderer: Legacy / Adaptive**: crack-free adaptive patches (2:1 balance + index stitching), progressive construction
  on worker threads with generation-safe cancellation, natural appearance shared with the legacy faces. Details:
  *Seamless Adaptive Terrain* above.
- Shared rules: `PlanetMeshBuilder.TerrainColor` (legacy colors unchanged), `NaturalSurface.Evaluate` with caller
  buffers, `PlanetColorRamp.Clone / ContentEquals`.
- `PlanetTerrain` shader: `_PlanetFromWorld` / `_AdaptivePatch` (zero for the legacy faces: unchanged output).
- `PlanetVisualController`: face visibility by owner (`SetTerrainFacesHidden`, `ForceTerrainFacesVisible`, replacing
  V0.2.0A's `SetTerrainFacesVisible`), `WriteAdaptiveTerrainProperties`. Thumbnails capture the legacy faces in every
  mode (V0.2.0A made them wait during the diagnostic preview).
- LOD selector: cached patch spheres, optional frustum weighting. Generation, archives and defaults untouched.

### V0.2.0A — Adaptive Planet Terrain Foundation
- New `Terrain/` layer: patch addresses, cube-sphere coordinates, cross-face neighbors, elevation adapter, patch
  meshes, screen-space-error LOD selection with budget and hysteresis, coverage validation. Details: *Adaptive
  Terrain Foundation* above.
- Opt-in ADAPTIVE TERRAIN DEBUG (overlay or mesh preview); the planet rendering is unchanged by default.
- `PlanetVisualController.SetTerrainFacesVisible` (used by the preview only; replaced in V0.2.0B).
- Generation, hydrology, climate, biomes, oceans, atmosphere, clouds, lighting, defaults and archives are untouched
  (generator version stays `Genesis V0.1.3B`: worlds reconstruct exactly).

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
Adaptive terrain: `AdaptiveTerrainTests` (engine independent, A - O and T: identifiers, hierarchy, coverage, coordinates
against the existing projection, neighbors, shared edges, elevation equivalence, meshes, selection, budget, hysteresis,
performance / allocation report on the reference seeds) and `AdaptiveTerrainRuntimeTests` (P - S: invalidation,
resource cleanup, no effect on world settings, archive compatibility with the debug view active).
Seamless terrain (V0.2.0B): `AdaptiveTerrainSeamTests` (engine independent: watertight mixed coverings, stitch
topology, displayed-covering transitions, pipeline bounds / cancellation / generations, sampling rules, performance)
and `AdaptiveTerrainRendererTests` (legacy equivalence, switching, leaks, destruction, regeneration and loads while
building, ocean / atmosphere / clouds / rivers / lakes, thumbnails, erratic camera, Unity tick costs).
Scale and terrain detail (V0.2.0C): `PlanetScaleAndFrameTests` (A-E engine independent: conversions, altitude, tangent
frames incl. poles, double round trips; F-H, X in Unity: frame snapping and consistency, rebasing vs terrain samples,
orbit camera, ocean / atmosphere alignment after a rebase), `TerrainDetailTests` (H-O, V: determinism across threads
and precision paths, seed independence, continuity across faces and corners, bounds, detail-off bit identity, band
controls, coastline preservation, no climate / biome dependency, band limiting, double-path smoothness, error model,
cost report), `WorldArchiveFormatTests` T / U (old archives exact with detail off, new fields round trip).
Ground exploration (V0.2.0D): `ExploreCameraTests` (K clearance vs the canonical terrain and the sea, J floating origin,
L flight across poles and cube edges, M frame-rate independence, N regeneration, P mode switching leaves the world
untouched, Q ground layer anchoring and off without detail); `TerrainLodReliefBoundsTests` (LOD centered under a low
camera, no flicker on a slow drag, reselection after a short move).
Surface geology (V0.2.0E): `SurfaceGeologyTests` (A determinism across instances and threads, B seed variation, C
cube-edge continuity, D LOD independence, E floating-origin invariance, F ranges, G smooth transitions, H geometry and
vertex colors unchanged, I vertices on the authoritative surface, J presentation only / no regeneration, K coarse
normals on patch edges, L shader properties, vertex layout and runtime switches; pilot determinism).
Surface materials (V0.2.0F): `SurfaceMaterialTests` (selection determinism, ranges, cube-edge continuity, LOD
independence, anchoring, import settings, OpenGL normals, smoothness = 1 - roughness, texture arrays, shader / runtime
wiring, procedural fallback, presentation only, no geometry change, no material per patch, steep / flat selection; coast:
beaches on gentle shores and rock on steep ones, no uniform ring, smooth inland transition, shallow water vs floating origin).
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
