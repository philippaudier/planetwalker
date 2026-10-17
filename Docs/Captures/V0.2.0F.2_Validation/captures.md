# V0.2.0F.2 validation captures (batch, EditMode rendering)

| File | Scenario | Seed | Clearance | Camera (planet space, render units) | LOD budget | Mode | View | Note |
|---|---|---|---|---|---|---|---|---|
| A_start_1km.png | A altitude | 310929 | 1000.0 m | (101.155752, -9.942255, -65.519104) | 1024 | Pbr | FinalLit | frame 0, streaming (40 ms, 48 uploads per frame) |
| A_down_100m.png | A altitude | 310929 | 100.7 m | (101.129124, -9.939638, -65.501857) | 1024 | Pbr | FinalLit | frame 108, streaming (40 ms, 48 uploads per frame) |
| A_down_100m_scale.png | A altitude | 310929 | 100.7 m | (101.129124, -9.939638, -65.501857) | 1024 | Pbr | Scale | Scale view (2 m red, 16 m green, 128 m blue, 1024 m white) |
| A_down_10m.png | A altitude | 310929 | 9.9 m | (101.126436, -9.939374, -65.500116) | 1024 | Pbr | FinalLit | frame 217, streaming (40 ms, 48 uploads per frame) |
| A_down_10m_scale.png | A altitude | 310929 | 9.9 m | (101.126436, -9.939374, -65.500116) | 1024 | Pbr | Scale | Scale view (2 m red, 16 m green, 128 m blue, 1024 m white) |
| A_up_1_7m.png | A altitude | 310929 | 1.7 m | (101.126193, -9.939350, -65.499958) | 1024 | Pbr | FinalLit | frame 300, streaming (40 ms, 48 uploads per frame) |
| A_up_10m.png | A altitude | 310929 | 9.9 m | (101.126436, -9.939374, -65.500116) | 1024 | Pbr | FinalLit | frame 383, streaming (40 ms, 48 uploads per frame) |
| A_up_100m.png | A altitude | 310929 | 100.7 m | (101.129124, -9.939638, -65.501857) | 1024 | Pbr | FinalLit | frame 492, streaming (40 ms, 48 uploads per frame) |
| A_up_1km.png | A altitude | 310929 | 1000.0 m | (101.155752, -9.942255, -65.519104) | 1024 | Pbr | FinalLit | frame 600, streaming (40 ms, 48 uploads per frame) |
| B_10m_budget384.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 384 | Pbr | FinalLit | 384 patches displayed |
| B_10m_budget512.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 512 | Pbr | FinalLit | 510 patches displayed |
| B_10m_budget1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | 1023 patches displayed |
| B_10m_diff_lit_384_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_10m_diff_albedo_384_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR albedo view (unlit) (absolute difference x8) |
| B_10m_diff_procedural_384_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | Procedural mode (reference) (absolute difference x8) |
| B_10m_diff_lit_512_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_10m_diff_albedo_512_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR albedo view (unlit) (absolute difference x8) |
| B_10m_diff_procedural_512_vs_1024.png | B LOD | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | Procedural mode (reference) (absolute difference x8) |
| B_100m_budget384.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 384 | Pbr | FinalLit | 384 patches displayed |
| B_100m_budget512.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 512 | Pbr | FinalLit | 510 patches displayed |
| B_100m_budget1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | 1023 patches displayed |
| B_100m_diff_lit_384_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_100m_diff_albedo_384_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR albedo view (unlit) (absolute difference x8) |
| B_100m_diff_procedural_384_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | Procedural mode (reference) (absolute difference x8) |
| B_100m_diff_lit_512_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_100m_diff_albedo_512_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR albedo view (unlit) (absolute difference x8) |
| B_100m_diff_procedural_512_vs_1024.png | B LOD | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | Procedural mode (reference) (absolute difference x8) |
| C_before.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | stationary, 10 m |
| C_immediately_after.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | same frame, button sequence |
| C_diff_before_vs_immediately_after.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit |  (absolute difference x8) |
| C_stale_camera_one_frame.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | rebase by 0.37 m without re-placing the camera (diagnostic) |
| C_after_stabilization.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | 30 ticks later |
| C_diff_before_vs_stabilized.png | C rebase | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit |  (absolute difference x8) |
| D_grassland_1_7m.png | D biomes/coast | 310929 | 1.7 m | (-77.013731, -84.640163, 36.991575) | 1024 | Pbr | FinalLit | PBR |
| D_grassland_10m.png | D biomes/coast | 310929 | 10.0 m | (-77.013411, -84.640846, 36.991421) | 1024 | Pbr | FinalLit | PBR |
| D_grassland_10m_MaterialId.png | D biomes/coast | 310929 | 10.0 m | (-77.013411, -84.640846, 36.991421) | 1024 | Pbr | MaterialId | MaterialId |
| D_grassland_10m_Tint.png | D biomes/coast | 310929 | 10.0 m | (-77.013411, -84.640846, 36.991421) | 1024 | Pbr | Tint | Tint |
| D_grassland_10m_Scale.png | D biomes/coast | 310929 | 10.0 m | (-77.013411, -84.640846, 36.991421) | 1024 | Pbr | Scale | Scale |
| D_grassland_10m_procedural.png | D biomes/coast | 310929 | 10.0 m | (-77.013411, -84.640846, 36.991421) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_grassland_1km.png | D biomes/coast | 310929 | 1000.0 m | (-76.977993, -84.725549, 36.974409) | 1024 | Pbr | FinalLit | PBR |
| D_grassland_1km_MaterialId.png | D biomes/coast | 310929 | 1000.0 m | (-76.977993, -84.725549, 36.974409) | 1024 | Pbr | MaterialId | MaterialId |
| D_grassland_1km_Tint.png | D biomes/coast | 310929 | 1000.0 m | (-76.977993, -84.725549, 36.974409) | 1024 | Pbr | Tint | Tint |
| D_grassland_1km_Scale.png | D biomes/coast | 310929 | 1000.0 m | (-76.977993, -84.725549, 36.974409) | 1024 | Pbr | Scale | Scale |
| D_grassland_1km_procedural.png | D biomes/coast | 310929 | 1000.0 m | (-76.977993, -84.725549, 36.974409) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_grassland_300km.png | D biomes/coast | 310929 | 300000.0 m | (-83.814427, -92.113645, 40.258115) | 1024 | Pbr | FinalLit | PBR |
| D_mountain_1_7m.png | D biomes/coast | 310929 | 1.7 m | (-5.565692, 51.921739, 109.086214) | 1024 | Pbr | FinalLit | PBR |
| D_mountain_10m.png | D biomes/coast | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Pbr | FinalLit | PBR |
| D_mountain_10m_MaterialId.png | D biomes/coast | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Pbr | MaterialId | MaterialId |
| D_mountain_10m_Tint.png | D biomes/coast | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Pbr | Tint | Tint |
| D_mountain_10m_Scale.png | D biomes/coast | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Pbr | Scale | Scale |
| D_mountain_10m_procedural.png | D biomes/coast | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_mountain_1km.png | D biomes/coast | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Pbr | FinalLit | PBR |
| D_mountain_1km_MaterialId.png | D biomes/coast | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Pbr | MaterialId | MaterialId |
| D_mountain_1km_Tint.png | D biomes/coast | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Pbr | Tint | Tint |
| D_mountain_1km_Scale.png | D biomes/coast | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Pbr | Scale | Scale |
| D_mountain_1km_procedural.png | D biomes/coast | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_mountain_300km.png | D biomes/coast | 310929 | 300000.0 m | (-6.054021, 56.481715, 118.666328) | 1024 | Pbr | FinalLit | PBR |
| D_coast_1_7m.png | D biomes/coast | 310929 | 1.7 m | (12.444722, -89.569190, -79.247915) | 1024 | Pbr | FinalLit | PBR |
| D_coast_10m.png | D biomes/coast | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Pbr | FinalLit | PBR |
| D_coast_10m_MaterialId.png | D biomes/coast | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Pbr | MaterialId | MaterialId |
| D_coast_10m_Tint.png | D biomes/coast | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Pbr | Tint | Tint |
| D_coast_10m_Scale.png | D biomes/coast | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Pbr | Scale | Scale |
| D_coast_10m_procedural.png | D biomes/coast | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_coast_1km.png | D biomes/coast | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Pbr | FinalLit | PBR |
| D_coast_1km_MaterialId.png | D biomes/coast | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Pbr | MaterialId | MaterialId |
| D_coast_1km_Tint.png | D biomes/coast | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Pbr | Tint | Tint |
| D_coast_1km_Scale.png | D biomes/coast | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Pbr | Scale | Scale |
| D_coast_1km_procedural.png | D biomes/coast | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Procedural | FinalLit | Procedural (V0.2.0E) reference |
| D_coast_300km.png | D biomes/coast | 310929 | 300000.0 m | (13.544070, -97.479616, -86.247083) | 1024 | Pbr | FinalLit | PBR |
