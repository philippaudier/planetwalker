using System.Globalization;
using System.Text;
using Planetwalker.Core;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// ADAPTIVE TERRAIN section. V0.2.0B: the TERRAIN RENDERER selector (Legacy / Adaptive,
    /// <see cref="AdaptiveTerrainRenderer"/>), background construction and the construction budget, the LOD settings
    /// shared by the renderer and the diagnostics, and compact statistics. V0.2.0C: the scale line (camera altitude
    /// in meters, render-origin offset and rebases, finest vertex spacing in meters) and the floating origin
    /// controls (<see cref="PlanetRenderFrame"/>). Diagnostics (V0.2.0A,
    /// <see cref="AdaptiveTerrainDebugView"/>): patch overlay (outlining the displayed covering when the adaptive
    /// renderer is on), the opt-in diagnostic mesh preview (Legacy renderer only), freeze, vertex grid. Rendering
    /// and diagnostic settings only: nothing here regenerates the planet or changes the world's configuration.
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        const int AdaptiveBudgetMinExponent = 3; // 8 leaves
        const int AdaptiveBudgetMaxExponent = 12; // 4096 leaves

        [SerializeField] Button terrainRendererButton;
        [SerializeField] Text terrainRendererLabel;
        [SerializeField] Toggle adaptiveBackgroundToggle;
        [SerializeField] Slider adaptiveConstructionSlider;
        [SerializeField] Text adaptiveConstructionValue;
        [SerializeField] Text adaptiveRendererStatsText;
        [SerializeField] Text adaptiveScaleText;
        [SerializeField] Toggle floatingOriginToggle;
        [SerializeField] Button rebaseNowButton;
        [SerializeField] Toggle adaptiveToggle;
        [SerializeField] Button adaptiveModeButton;
        [SerializeField] Text adaptiveModeLabel;
        [SerializeField] Toggle adaptiveFreezeToggle;
        [SerializeField] Toggle adaptiveGridToggle;
        [SerializeField] Slider adaptiveMinLevelSlider;
        [SerializeField] Text adaptiveMinLevelValue;
        [SerializeField] Slider adaptiveMaxLevelSlider;
        [SerializeField] Text adaptiveMaxLevelValue;
        [SerializeField] Slider adaptiveErrorSlider;
        [SerializeField] Text adaptiveErrorValue;
        [SerializeField] Slider adaptiveBudgetSlider;
        [SerializeField] Text adaptiveBudgetValue;
        [SerializeField] Text adaptiveStatsText;
        [SerializeField] Text adaptiveLegendText;

        float nextAdaptiveStatsTime;
        readonly StringBuilder adaptiveStats = new StringBuilder(512);

        bool AdaptiveLayoutComplete()
        {
            return terrainRendererButton != null && adaptiveBackgroundToggle != null && adaptiveConstructionSlider != null
                && adaptiveRendererStatsText != null && adaptiveScaleText != null && floatingOriginToggle != null && rebaseNowButton != null
                && adaptiveToggle != null && adaptiveModeButton != null && adaptiveFreezeToggle != null && adaptiveGridToggle != null
                && adaptiveMinLevelSlider != null && adaptiveMaxLevelSlider != null && adaptiveErrorSlider != null
                && adaptiveBudgetSlider != null && adaptiveStatsText != null && adaptiveLegendText != null;
        }

        void BuildAdaptiveTerrainSection(RectTransform panel, Font font)
        {
            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "ADAPTIVE TERRAIN", 13, FontStyle.Bold, AccentColor, 18f);
            var rendererRow = CreateRow(panel, "Terrain Renderer Row");
            terrainRendererButton = CreateButton(rendererRow, font, "Renderer: Legacy");
            terrainRendererLabel = terrainRendererButton.GetComponentInChildren<Text>();
            terrainRendererLabel.fontSize = FontSize - 3;
            adaptiveBackgroundToggle = CreateToggle(rendererRow, font, "Background Build");
            adaptiveBackgroundToggle.isOn = true;
            int deepest = CubeSphereCoordinates.MaxLevelForResolution(new AdaptiveTerrainSettings().patchResolution);
            adaptiveErrorSlider = CreateSliderRow(panel, font, "LOD Screen Error (px)", 1f, 32f, false, out adaptiveErrorValue);
            adaptiveBudgetSlider = CreateSliderRow(panel, font, "LOD Patch Budget", AdaptiveBudgetMinExponent, AdaptiveBudgetMaxExponent, true,
                out adaptiveBudgetValue);
            adaptiveMinLevelSlider = CreateSliderRow(panel, font, "LOD Min Level", 0f, 6f, true, out adaptiveMinLevelValue);
            adaptiveMaxLevelSlider = CreateSliderRow(panel, font, "LOD Max Level", 0f, deepest, true, out adaptiveMaxLevelValue);
            adaptiveConstructionSlider = CreateSliderRow(panel, font, "Construction Budget (ms)", 1f, 32f, false, out adaptiveConstructionValue);
            var defaults = new AdaptiveTerrainSettings();
            adaptiveErrorSlider.SetValueWithoutNotify(defaults.maxScreenError);
            adaptiveBudgetSlider.SetValueWithoutNotify(Mathf.Round(Mathf.Log(defaults.maxLeafPatches, 2f)));
            adaptiveMinLevelSlider.SetValueWithoutNotify(defaults.minLevel);
            adaptiveMaxLevelSlider.SetValueWithoutNotify(defaults.maxLevel);
            adaptiveConstructionSlider.SetValueWithoutNotify(4f);
            adaptiveRendererStatsText = CreateLabel(panel, font, "Terrain: legacy faces", 11, FontStyle.Normal, MutedTextColor, 58f);
            adaptiveRendererStatsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            adaptiveRendererStatsText.supportRichText = true;
            adaptiveScaleText = CreateLabel(panel, font, "Altitude -", 11, FontStyle.Normal, MutedTextColor, 16f);
            adaptiveScaleText.horizontalOverflow = HorizontalWrapMode.Wrap;
            var originRow = CreateRow(panel, "Floating Origin Row");
            floatingOriginToggle = CreateToggle(originRow, font, "Floating Origin");
            floatingOriginToggle.isOn = true;
            rebaseNowButton = CreateButton(originRow, font, "Rebase Now");
            rebaseNowButton.GetComponentInChildren<Text>().fontSize = FontSize - 3;

            CreateLabel(panel, font, "Diagnostics", 11, FontStyle.Bold, MutedTextColor, 16f);
            var row = CreateRow(panel, "Adaptive Terrain Row");
            adaptiveToggle = CreateToggle(row, font, "Show Patch LOD");
            adaptiveModeButton = CreateButton(row, font, "Mode: Overlay");
            adaptiveModeLabel = adaptiveModeButton.GetComponentInChildren<Text>();
            adaptiveModeLabel.fontSize = FontSize - 3;
            var freezeRow = CreateRow(panel, "Adaptive Terrain Freeze Row");
            adaptiveFreezeToggle = CreateToggle(freezeRow, font, "Freeze LOD");
            adaptiveGridToggle = CreateToggle(freezeRow, font, "Vertex Grid");
            adaptiveGridToggle.isOn = true;
            adaptiveLegendText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 30f);
            adaptiveLegendText.supportRichText = true;
            adaptiveLegendText.horizontalOverflow = HorizontalWrapMode.Wrap;
            adaptiveLegendText.text = AdaptiveLegend();
            adaptiveStatsText = CreateLabel(panel, font, "Patch LOD: off", 11, FontStyle.Normal, MutedTextColor, 58f);
            adaptiveStatsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            UpdateAdaptiveValueLabels();
        }

        static string AdaptiveLegend()
        {
            var text = new StringBuilder("Level ");
            for (int level = 0; level < 8; level++)
            {
                text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(AdaptiveTerrainOverlay.LevelColor(level))).Append(">■</color>")
                    .Append(level).Append(level == 7 ? "+ " : " ");
            }
            text.Append("\n<color=#FFFFFF>■</color> cube-face edge   <color=#")
                .Append(ColorUtility.ToHtmlStringRGB(AdaptiveTerrainOverlay.TransitionEdgeColor)).Append(">■</color> LOD transition (stitched)");
            return text.ToString();
        }

        void EnableAdaptiveTerrain()
        {
            terrainRendererButton.onClick.AddListener(OnTerrainRendererClicked);
            adaptiveBackgroundToggle.onValueChanged.AddListener(OnAdaptiveFreezeToggled);
            adaptiveConstructionSlider.onValueChanged.AddListener(OnAdaptiveSettingChanged);
            floatingOriginToggle.onValueChanged.AddListener(OnFloatingOriginToggled);
            rebaseNowButton.onClick.AddListener(OnRebaseNowClicked);
            adaptiveToggle.onValueChanged.AddListener(OnAdaptiveToggled);
            adaptiveModeButton.onClick.AddListener(OnAdaptiveModeClicked);
            adaptiveFreezeToggle.onValueChanged.AddListener(OnAdaptiveFreezeToggled);
            adaptiveGridToggle.onValueChanged.AddListener(OnAdaptiveFreezeToggled);
            adaptiveMinLevelSlider.onValueChanged.AddListener(OnAdaptiveSettingChanged);
            adaptiveMaxLevelSlider.onValueChanged.AddListener(OnAdaptiveSettingChanged);
            adaptiveErrorSlider.onValueChanged.AddListener(OnAdaptiveSettingChanged);
            adaptiveBudgetSlider.onValueChanged.AddListener(OnAdaptiveSettingChanged);
        }

        void DisableAdaptiveTerrain()
        {
            terrainRendererButton.onClick.RemoveListener(OnTerrainRendererClicked);
            adaptiveBackgroundToggle.onValueChanged.RemoveListener(OnAdaptiveFreezeToggled);
            adaptiveConstructionSlider.onValueChanged.RemoveListener(OnAdaptiveSettingChanged);
            floatingOriginToggle.onValueChanged.RemoveListener(OnFloatingOriginToggled);
            rebaseNowButton.onClick.RemoveListener(OnRebaseNowClicked);
            adaptiveToggle.onValueChanged.RemoveListener(OnAdaptiveToggled);
            adaptiveModeButton.onClick.RemoveListener(OnAdaptiveModeClicked);
            adaptiveFreezeToggle.onValueChanged.RemoveListener(OnAdaptiveFreezeToggled);
            adaptiveGridToggle.onValueChanged.RemoveListener(OnAdaptiveFreezeToggled);
            adaptiveMinLevelSlider.onValueChanged.RemoveListener(OnAdaptiveSettingChanged);
            adaptiveMaxLevelSlider.onValueChanged.RemoveListener(OnAdaptiveSettingChanged);
            adaptiveErrorSlider.onValueChanged.RemoveListener(OnAdaptiveSettingChanged);
            adaptiveBudgetSlider.onValueChanged.RemoveListener(OnAdaptiveSettingChanged);
        }

        Camera OrbitViewCamera => bootstrap != null && bootstrap.OrbitCamera != null ? bootstrap.OrbitCamera.GetComponent<Camera>() : null;

        /// <summary>The planet's debug view when it exists (never created just to be read).</summary>
        AdaptiveTerrainDebugView ExistingAdaptiveView()
        {
            PlanetVisualController visual = Visual;
            return visual != null ? visual.GetComponent<AdaptiveTerrainDebugView>() : null;
        }

        AdaptiveTerrainRenderer ExistingTerrainRenderer()
        {
            PlanetVisualController visual = Visual;
            return visual != null ? visual.GetComponent<AdaptiveTerrainRenderer>() : null;
        }

        /// <summary>The planet's render frame (floating origin, V0.2.0C) when it exists (the bootstrap creates it).</summary>
        PlanetRenderFrame ExistingRenderFrame()
        {
            PlanetVisualController visual = Visual;
            return visual != null ? visual.GetComponent<PlanetRenderFrame>() : null;
        }

        /// <summary>Camera position in planet space (double through the frame, else the root's inverse transform). False without a camera.</summary>
        bool TryGetCameraPlanetPosition(out Vector3d cameraPlanet)
        {
            Camera camera = OrbitViewCamera;
            PlanetVisualController visual = Visual;
            if (camera == null || visual == null)
            {
                cameraPlanet = Vector3d.Zero;
                return false;
            }
            PlanetRenderFrame frame = ExistingRenderFrame();
            cameraPlanet = frame != null ? frame.RenderToPlanet(camera.transform.position) : (Vector3d)visual.transform.InverseTransformPoint(camera.transform.position);
            return true;
        }

        void OnFloatingOriginToggled(bool on)
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            // Off only stops further rebases (the planet keeps its render position): no root move outside the camera's phase.
            PlanetRenderFrame.GetOrCreate(visual.transform).AutoRebase = on;
            nextAdaptiveStatsTime = 0f;
        }

        void OnRebaseNowClicked()
        {
            PlanetVisualController visual = Visual;
            if (visual == null || !TryGetCameraPlanetPosition(out Vector3d cameraPlanet))
            {
                return;
            }
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(visual.transform);
            frame.RebaseNow(cameraPlanet);
            // The UI runs after the orbit camera this frame: re-place the camera at the same planet position now, so
            // this frame's draws do not see the planet jump.
            OrbitViewCamera.transform.position = frame.PlanetToRender(cameraPlanet);
            nextAdaptiveStatsTime = 0f;
        }

        AdaptiveTerrainDebugView AdaptiveView()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return null;
            }
            AdaptiveTerrainDebugView view = AdaptiveTerrainDebugView.GetOrCreate(visual);
            view.ViewCamera = OrbitViewCamera;
            return view;
        }

        /// <summary>Legacy / Adaptive. Adaptive keeps the legacy faces on screen until its covering is ready.</summary>
        void OnTerrainRendererClicked()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(visual);
            terrain.ViewCamera = OrbitViewCamera;
            ApplyAdaptiveSettings();
            terrain.enabled = !terrain.enabled; // disabling releases every adaptive resource and shows the legacy faces
            terrainRendererLabel.text = terrain.enabled ? "Renderer: Adaptive" : "Renderer: Legacy";
            nextAdaptiveStatsTime = 0f;
        }

        void OnAdaptiveToggled(bool on)
        {
            AdaptiveTerrainDebugView view = on ? AdaptiveView() : ExistingAdaptiveView();
            if (view == null)
            {
                return;
            }
            ApplyAdaptiveSettings();
            view.enabled = on; // disabling releases every mesh it holds
            nextAdaptiveStatsTime = 0f;
        }

        void OnAdaptiveModeClicked()
        {
            AdaptiveTerrainDebugView view = AdaptiveView();
            if (view == null)
            {
                return;
            }
            view.Mode = view.Mode == AdaptiveTerrainDebugMode.Overlay ? AdaptiveTerrainDebugMode.MeshPreview : AdaptiveTerrainDebugMode.Overlay;
            adaptiveModeLabel.text = view.Mode == AdaptiveTerrainDebugMode.Overlay ? "Mode: Overlay" : "Mode: Mesh Preview";
            nextAdaptiveStatsTime = 0f;
        }

        void OnAdaptiveFreezeToggled(bool _)
        {
            ApplyAdaptiveSettings();
        }

        void OnAdaptiveSettingChanged(float _)
        {
            UpdateAdaptiveValueLabels();
            ApplyAdaptiveSettings();
        }

        /// <summary>Copies the controls to whichever of the renderer and the debug view exist (same LOD settings).</summary>
        void ApplyAdaptiveSettings()
        {
            int minLevel = Mathf.RoundToInt(adaptiveMinLevelSlider.value);
            // Min level above max level: the max follows (the selector also clamps).
            int maxLevel = Mathf.Max(minLevel, Mathf.RoundToInt(adaptiveMaxLevelSlider.value));
            int budget = 1 << Mathf.RoundToInt(adaptiveBudgetSlider.value);
            AdaptiveTerrainDebugView view = ExistingAdaptiveView();
            if (view != null)
            {
                Apply(view.Settings);
                view.FreezeSelection = adaptiveFreezeToggle.isOn;
                view.ShowVertexGrid = adaptiveGridToggle.isOn;
            }
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                Apply(terrain.Settings);
                terrain.FreezeSelection = adaptiveFreezeToggle.isOn;
                terrain.BackgroundConstruction = adaptiveBackgroundToggle.isOn;
                terrain.ConstructionMilliseconds = adaptiveConstructionSlider.value;
                terrain.MaxUploadsPerFrame = Mathf.Max(4, Mathf.RoundToInt(adaptiveConstructionSlider.value * 6f));
            }

            void Apply(AdaptiveTerrainSettings settings)
            {
                settings.maxScreenError = adaptiveErrorSlider.value;
                settings.maxLeafPatches = budget;
                settings.maxPatchMeshes = Mathf.Max(settings.maxPatchMeshes, budget + budget / 2);
                settings.minLevel = minLevel;
                settings.maxLevel = maxLevel;
            }
        }

        void UpdateAdaptiveValueLabels()
        {
            adaptiveErrorValue.text = adaptiveErrorSlider.value.ToString("0.0", CultureInfo.InvariantCulture);
            adaptiveBudgetValue.text = (1 << Mathf.RoundToInt(adaptiveBudgetSlider.value)).ToString(CultureInfo.InvariantCulture);
            adaptiveMinLevelValue.text = Mathf.RoundToInt(adaptiveMinLevelSlider.value).ToString(CultureInfo.InvariantCulture);
            adaptiveMaxLevelValue.text = Mathf.RoundToInt(adaptiveMaxLevelSlider.value).ToString(CultureInfo.InvariantCulture);
            adaptiveConstructionValue.text = adaptiveConstructionSlider.value.ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Statistics, refreshed a few times per second.</summary>
        void UpdateAdaptiveStats()
        {
            if (adaptiveStatsText == null || adaptiveRendererStatsText == null || Time.unscaledTime < nextAdaptiveStatsTime)
            {
                return;
            }
            nextAdaptiveStatsTime = Time.unscaledTime + 0.25f;
            UpdateRendererStats();
            UpdateScaleStats();
            UpdateDiagnosticStats();
        }

        /// <summary>
        /// The scale line (V0.2.0C), shown in every renderer: camera altitude above the terrain under it (meters),
        /// render-origin offset and rebase count, finest displayed vertex spacing (meters) when known.
        /// </summary>
        void UpdateScaleStats()
        {
            if (adaptiveScaleText == null)
            {
                return;
            }
            PlanetVisualController visual = Visual;
            PlanetInstance planet = visual != null ? visual.Planet : null;
            if (planet == null || !TryGetCameraPlanetPosition(out Vector3d cameraPlanet))
            {
                adaptiveScaleText.text = "Altitude -";
                return;
            }
            PlanetScale scale = planet.Scale;
            Vector3d direction = PlanetCoordinates.Direction(cameraPlanet);
            double surfaceElevation = direction.SqrMagnitude > 0 ? planet.Height.GetElevation(direction.ToVector3()) : 0.0;
            adaptiveStats.Clear();
            adaptiveStats.Append("Altitude ").Append(PlanetScale.FormatDistance(scale.AltitudeMeters(cameraPlanet, surfaceElevation)));
            PlanetRenderFrame frame = ExistingRenderFrame();
            if (frame != null)
            {
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, " · origin offset {0:0.00} units ({1} rebase{2})",
                    frame.OriginOffsetUnits, frame.RebaseCount, frame.RebaseCount == 1 ? "" : "s");
                if (floatingOriginToggle != null && floatingOriginToggle.isOn != frame.AutoRebase)
                {
                    floatingOriginToggle.SetIsOnWithoutNotify(frame.AutoRebase);
                }
            }
            // Finest vertex spacing R (pi/2) / (r 2^L) of the displayed covering, else of the diagnostic selection.
            int finestLevel = -1, resolution = 0;
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            AdaptiveTerrainDebugView view = ExistingAdaptiveView();
            if (terrain != null && terrain.IsDisplaying && terrain.Displayed.Count > 0)
            {
                foreach (PlanetPatchId id in terrain.Displayed.Leaves)
                {
                    finestLevel = Mathf.Max(finestLevel, id.Level);
                }
                resolution = terrain.Snapshot.Resolution;
            }
            else if (view != null && view.isActiveAndEnabled && view.Selection != null && view.Selection.Count > 0)
            {
                finestLevel = view.Selection.MaxLevel;
                resolution = view.Settings.patchResolution;
            }
            if (finestLevel >= 0 && resolution > 0)
            {
                double spacing = scale.ToMeters(planet.Radius * Mathf.PI * 0.5 / ((double)resolution * (1 << finestLevel)));
                adaptiveStats.Append(" · finest spacing ").Append(PlanetScale.FormatDistance(spacing));
            }
            adaptiveScaleText.text = adaptiveStats.ToString();
        }

        void UpdateRendererStats()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain == null || !terrain.isActiveAndEnabled)
            {
                adaptiveRendererStatsText.text = "Terrain: legacy faces";
                return;
            }
            adaptiveStats.Clear();
            adaptiveStats.Append(terrain.Suspended ? "Terrain: legacy (scientific view)"
                : terrain.IsDisplaying ? "Terrain: <b>adaptive</b>" : "Terrain: legacy while the adaptive covering builds");
            TerrainLodSelection target = terrain.Target;
            PatchBuildScheduler builds = terrain.Scheduler;
            if (target != null && target.Count > 0 && builds != null)
            {
                int minLevel = int.MaxValue, maxLevel = 0;
                foreach (PlanetPatchId id in terrain.Displayed.Leaves)
                {
                    minLevel = Mathf.Min(minLevel, id.Level);
                    maxLevel = Mathf.Max(maxLevel, id.Level);
                }
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nTarget {0} / {1}{2}  ·  shown {3} (L{4}-{5}){6}  ·  select {7:0.00} ms",
                    target.Count, target.Budget, target.BudgetLimited ? " <color=#F5C26B>full</color>" : "", terrain.Displayed.Count,
                    terrain.Displayed.Count > 0 ? minLevel : 0, maxLevel, terrain.Covering.Converged ? "" : " →", terrain.LastSelectionMilliseconds);
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nBuilds: pending {0} · done {1} · cancelled {2} · discarded {3} · {4:0.0} ms/patch",
                    builds.Pending, builds.Completed, builds.Cancelled, builds.Discarded, builds.Completed > 0 ? builds.BuildMilliseconds / builds.Completed : 0.0);
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nUpload {0} ({1:0.0} ms) · resident {2} · stitched {3}{4} · {5:0.0} MB",
                    terrain.UploadsLastTick, terrain.UploadMillisecondsLastTick, terrain.ResidentPatches, terrain.TransitionEdges,
                    terrain.InvalidEdges > 0 ? " <color=#FF6050>invalid " + terrain.InvalidEdges + "</color>" : "", terrain.EstimatedMeshBytes / (1024.0 * 1024.0));
            }
            adaptiveRendererStatsText.text = adaptiveStats.ToString();
        }

        void UpdateDiagnosticStats()
        {
            AdaptiveTerrainDebugView view = ExistingAdaptiveView();
            if (view == null || !view.isActiveAndEnabled)
            {
                adaptiveStatsText.text = "Patch LOD: off";
                return;
            }
            adaptiveStats.Clear();
            if (view.FollowingRenderer)
            {
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "Overlay of the displayed adaptive covering · {0} transition edges",
                    view.Overlay != null ? view.Overlay.TransitionEdges : 0);
                adaptiveStatsText.text = adaptiveStats.ToString();
                return;
            }
            TerrainLodSelection selection = view.Selection;
            if (selection == null || selection.Count == 0)
            {
                adaptiveStatsText.text = "Patch LOD: waiting for the planet";
                return;
            }
            adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "Patches {0} / {1} ({2:0}%)   ·   levels {3}-{4}   ·   select {5:0.00} ms",
                selection.Count, selection.Budget, 100f * selection.Count / Mathf.Max(1, selection.Budget), selection.MinLevel, selection.MaxLevel,
                selection.Milliseconds);
            if (view.SampledPlanet != null)
            {
                // Vertex spacing of the finest selected patches: R (pi/2) / (r 2^L), at the face center.
                float spacing = view.SampledPlanet.Radius * Mathf.PI * 0.5f / ((float)view.Settings.patchResolution * (1 << selection.MaxLevel));
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nFinest vertex spacing {0:0.###} units (R / {1:N0})", spacing,
                    view.SampledPlanet.Radius / spacing);
            }
            if (selection.BudgetLimited)
            {
                adaptiveStats.Append("\n<color=#F5C26B>Budget limited refinement (covering kept complete, coarser)</color>");
            }
            if (view.Mode == AdaptiveTerrainDebugMode.Overlay)
            {
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nLOD transitions: {0} edges", view.Overlay != null ? view.Overlay.TransitionEdges : 0);
            }
            else
            {
                adaptiveStats.AppendFormat(CultureInfo.InvariantCulture, "\nMeshes {0} / {1}  ·  drawn {2}  ·  pending {3}  ·  {4:0.00} MB",
                    view.ResidentMeshes, view.MeshCapacity, view.DrawnPatches, view.PendingPatches, view.EstimatedMeshBytes / (1024.0 * 1024.0));
            }
            adaptiveStatsText.supportRichText = true;
            adaptiveStatsText.text = adaptiveStats.ToString();
        }
    }
}
