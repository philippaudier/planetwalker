# Planet surface materials — third-party assets (V0.2.0F)

All six materials come from **Poly Haven** (https://polyhaven.com), published under **CC0 1.0** (public domain
dedication; license page: https://polyhaven.com/license — "CC0 means absolute freedom", verified 2026-10-17). No
attribution is required; it is given below anyway.

Downloaded **2026-10-17** through Poly Haven's public API (`https://api.polyhaven.com/files/<id>`), JPG maps at **2K**
(2048 x 2048), from `https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/<id>/<id>_<map>_2k.jpg`. Only the maps
used were downloaded (no bundle): base color (`diff`), normal in OpenGL convention (`nor_gl`), roughness (`rough`),
ambient occlusion (`ao`), height (`disp`).

Location in the project: `Assets/Planetwalker/Materials/PlanetSurface/<Folder>/<Folder>_<id>_<map>_2k.jpg`.

| Layer | Folder | Poly Haven asset | Source page | Authors | Published | Physical size of the scan | Max resolution available |
|---|---|---|---|---|---|---|---|
| 0 | Granite (crystalline bedrock) | Rock Surface (`rock_surface`) | https://polyhaven.com/a/rock_surface | Amal Kumar | 2023-05-04 | 2.0 m | 16K |
| 1 | Sandstone (layered sedimentary rock) | Cliff Side (`cliff_side`) | https://polyhaven.com/a/cliff_side | James Ray Cock, Jenelle van Heerden, Dario Barresi | 2024-10-08 | 1.83 m | 16K |
| 2 | Limestone (pale weathered rock) | Rock 2 (`rock_2`) | https://polyhaven.com/a/rock_2 | Rob Tuytel | 2018-08-13 | 1.5 m | 8K |
| 3 | Basalt (dark rock) | Dark Rock 02 (`dark_rock_02`) | https://polyhaven.com/a/dark_rock_02 | Amal Kumar | 2026-07-20 | 2.0 m | 8K |
| 4 | DrySand (desert floor, compacted sandy soil) | Gravelly Sand (`gravelly_sand`) | https://polyhaven.com/a/gravelly_sand | Dario Barresi | 2024-10-08 | 2.48 m | 16K |
| 5 | Gravel (scree, rocky debris) | Rock Ground (`rock_ground`) | https://polyhaven.com/a/rock_ground | Rob Tuytel | 2020-04-23 | 1.5 m | 8K |

### Ground cover materials (V0.2.0F.2, downloaded 2026-10-17)

| Layer | Folder | Asset | Source page | Authors | Published | Physical size | License |
|---|---|---|---|---|---|---|---|
| 6 | Grass (green grass) | ambientCG **Grass004** (`Grass004_2K-JPG.zip`, maps extracted and renamed: Color → diff, NormalGL → nor_gl, Roughness → rough, AmbientOcclusion → ao, Displacement → disp; the .blend/.usdc/.mtlx/.tres and NormalDX files were not kept) | https://ambientcg.com/view?id=Grass004 | ambientCG (Lennart Demes) | 2021-03-28 | 1.4 m | CC0 1.0 (https://docs.ambientcg.com/license/) |
| 7 | DryGrass (savanna, steppe) | Poly Haven Grass Ground (`grass_ground`) | https://polyhaven.com/a/grass_ground | Charlotte Baglioni | 2026-09-18 | 2.51 m | CC0 |
| 8 | Forest (forest floor) | Poly Haven Forest Ground 01 (`forrest_ground_01`) | https://polyhaven.com/a/forrest_ground_01 | Rob Tuytel | 2019-03-19 | 2.0 m | CC0 |
| 9 | Soil (bare humid ground) | Poly Haven Brown Mud (`brown_mud`) | https://polyhaven.com/a/brown_mud | Rob Tuytel | 2018-06-18 | 1.3 m | CC0 |
| 10 | Snow | Poly Haven Snow 02 (`snow_02`) | https://polyhaven.com/a/snow_02 | Rob Tuytel | 2019-01-30 | 2.0 m | CC0 |

All 2K (2048²), same import rules as below. The folder of layer 8 is `Forest` (not `ForestFloor`): the longer name
exceeded the Windows 260-character path limit in deep checkouts.

Notes on the choice: the catalog was filtered by category and tag, then judged on Poly Haven's thumbnails for natural
outdoor lighting, absence of recognizable repeating objects, and fit for close-range ground (no moss, leaves or
man-made elements). The "basalt" is a dark fractured rock (Poly Haven does not tag a basalt as such); the "limestone"
is a pale weathered rock; both are stand-ins chosen for their appearance.

## Unity import (`Editor/PlanetSurfaceTextureImporter`)

| Map | Type | Color space | Compression (PC) | Notes |
|---|---|---|---|---|
| `diff` | Default | sRGB | BC1 (DXT1) | Mipmaps, aniso 8, repeat, max 2048 |
| `nor_gl` | Normal map | linear | BC7 (High Quality) | OpenGL convention = Unity's (+Y up): **no green flip** |
| `rough` | Default | linear | BC1 | **Becomes the packed mask** at import: R = smoothness = 1 − roughness (Unity convention), G = AO, B = height (read from the sibling `ao` / `disp` files, declared import dependencies) |
| `ao`, `disp` | Single channel | linear | BC4 | Sources of the packed mask only; not sampled at runtime |

Conversions performed: roughness → smoothness (1 − r) and channel packing, at import (no extra files in the repository).
No resizing (sources are already 2048²), no green-channel flip. Height maps are used to blend materials only, never to
displace geometry.

## Runtime use

`Resources/PlanetSurface/PlanetSurfaceLibrary.asset` (built by Planetwalker > Build Surface Material Library) references
the imported textures, the tile size of each layer (2 m, about the scans' physical size, a divisor of the 1024 m
anchoring period) and their mean linear albedo. At first use the three texture arrays (albedo, normal, mask; 6 layers,
2048², 12 mips) are assembled on the GPU with `Graphics.CopyTexture`; in Play mode the source textures are then unloaded.
