# V0.2.0F.3 validation captures (batch, EditMode rendering)

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
| B_valley_10m_budget384.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 384 | Pbr | FinalLit | 384 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.91 m max 5.22 m |
| B_valley_10m_budget512.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 512 | Pbr | FinalLit | 510 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.87 m max 5.22 m |
| B_valley_10m_budget1024.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | 1023 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.35 m max 2.29 m |
| B_valley_10m_diff_lit_384_vs_1024.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_valley_10m_diff_geometry_384_vs_1024.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_valley_10m_diff_lit_512_vs_1024.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_valley_10m_diff_geometry_512_vs_1024.png | B budget | 310929 | 10.0 m | (101.126438, -9.939374, -65.500117) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_valley_100m_budget384.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 384 | Pbr | FinalLit | 384 patches (L0-L15), holes 0 px, mesh vs authoritative height in view: mean 1.01 m max 5.72 m |
| B_valley_100m_budget512.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 512 | Pbr | FinalLit | 510 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.76 m max 4.18 m |
| B_valley_100m_budget1024.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | 1023 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.35 m max 2.29 m |
| B_valley_100m_diff_lit_384_vs_1024.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_valley_100m_diff_geometry_384_vs_1024.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_valley_100m_diff_lit_512_vs_1024.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_valley_100m_diff_geometry_512_vs_1024.png | B budget | 310929 | 100.0 m | (101.129103, -9.939636, -65.501843) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_mountain_10m_budget384.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 384 | Pbr | FinalLit | 384 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 1.18 m max 3.33 m |
| B_mountain_10m_budget512.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 512 | Pbr | FinalLit | 510 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.36 m max 1.49 m |
| B_mountain_10m_budget1024.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 1024 | Pbr | FinalLit | 1023 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.22 m max 1.34 m |
| B_mountain_10m_diff_lit_384_vs_1024.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_mountain_10m_diff_geometry_384_vs_1024.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_mountain_10m_diff_lit_512_vs_1024.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_mountain_10m_diff_geometry_512_vs_1024.png | B budget | 310929 | 10.0 m | (-5.567038, 51.918516, 109.080432) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_mountain_100m_budget384.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 384 | Pbr | FinalLit | 384 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 1.15 m max 3.33 m |
| B_mountain_100m_budget512.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 512 | Pbr | FinalLit | 510 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.36 m max 1.49 m |
| B_mountain_100m_budget1024.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 1024 | Pbr | FinalLit | 1023 patches (L0-L16), holes 0 px, mesh vs authoritative height in view: mean 0.22 m max 1.34 m |
| B_mountain_100m_diff_lit_384_vs_1024.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_mountain_100m_diff_geometry_384_vs_1024.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| B_mountain_100m_diff_lit_512_vs_1024.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 1024 | Pbr | FinalLit | PBR final lit (absolute difference x8) |
| B_mountain_100m_diff_geometry_512_vs_1024.png | B budget | 310929 | 100.0 m | (-5.567185, 51.919884, 109.083306) | 1024 | Pbr | FinalLit | geometry and vertex colors only (geology, materials off) (absolute difference x8) |
| P_mountain_1_7m.png | Presets | 310929 | 5.3 m | (-5.565697, 51.921794, 109.086330) | 1024 | Pbr | FinalLit | requested clearance 1.7 m; vertical clearance 5.32 m; distance to surface 1.50 m; nearest rendered mesh 1.38 m; near plane 0.375 m; holes 0 px |
| P_mountain_10m.png | Presets | 310929 | 10.0 m | (-5.566290, 51.920462, 109.083961) | 1024 | Pbr | FinalLit | requested clearance 10 m; vertical clearance 10.00 m; distance to surface 2.50 m; nearest rendered mesh 2.46 m; near plane 0.626 m; holes 0 px |
| P_mountain_100m.png | Presets | 310929 | 100.0 m | (-5.572758, 51.906423, 109.059129) | 1024 | Pbr | FinalLit | requested clearance 100 m; vertical clearance 100.00 m; distance to surface 25.06 m; nearest rendered mesh 24.49 m; near plane 6.264 m; holes 0 px |
| P_mountain_1km.png | Presets | 310929 | 1000.0 m | (-5.638372, 51.776496, 108.832680) | 1024 | Pbr | FinalLit | requested clearance 1000 m; vertical clearance 1000.00 m; distance to surface 332.74 m; nearest rendered mesh 287.40 m; near plane 83.184 m; holes 0 px |
| P_coast_10m.png | Presets | 310929 | 10.0 m | (12.444413, -89.569624, -79.247925) | 1024 | Pbr | FinalLit | requested clearance 10 m; vertical clearance 10.00 m; distance to surface 10.00 m; nearest rendered mesh 10.00 m; near plane 2.500 m; holes 0 px |
| P_coast_100m.png | Presets | 310929 | 100.0 m | (12.441065, -89.574329, -79.248032) | 1024 | Pbr | FinalLit | requested clearance 100 m; vertical clearance 100.00 m; distance to surface 100.00 m; nearest rendered mesh 100.01 m; near plane 25.000 m; holes 0 px |
| P_coast_1km.png | Presets | 310929 | 1000.0 m | (12.407547, -89.621154, -79.248888) | 1024 | Pbr | FinalLit | requested clearance 1000 m; vertical clearance 1000.00 m; distance to surface 1000.00 m; nearest rendered mesh 1000.49 m; near plane 250.000 m; holes 0 px |
| P_cubeface_10m.png | Presets | 310929 | 10.0 m | (-77.152876, 50.553271, -77.154857) | 1024 | Pbr | FinalLit | requested clearance 10 m; vertical clearance 10.00 m; distance to surface 10.00 m; nearest rendered mesh 9.93 m; near plane 2.500 m; holes 0 px |
| P_cubeface_5km.png | Presets | 310929 | 5000.0 m | (-77.494606, 50.777184, -76.918889) | 1024 | Pbr | FinalLit | requested clearance 5000 m; vertical clearance 5000.00 m; distance to surface 5000.00 m; nearest rendered mesh 5045.96 m; near plane 1250.000 m; holes 0 px |
