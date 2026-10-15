using System.Globalization;
using Planetwalker.Bootstrap;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// Minimal runtime generation panel (uGUI): seed, regenerate, randomize, and a few
    /// live parameters. The layout is built in code so the scene needs no manual wiring.
    /// </summary>
    public sealed class PlanetGenerationPanel : MonoBehaviour
    {
        const float PanelWidth = 410f;
        const float SliderLabelWidth = 165f;
        const string PanelObjectName = "Panel";
        const float RowHeight = 27f;
        const int FontSize = 16;

        static readonly Color PanelColor = new Color(0.04f, 0.05f, 0.08f, 0.82f);
        static readonly Color ControlColor = new Color(0.16f, 0.19f, 0.25f, 1f);
        static readonly Color AccentColor = new Color(0.45f, 0.68f, 0.86f, 1f);
        static readonly Color TextColor = new Color(0.88f, 0.90f, 0.93f, 1f);
        static readonly Color MutedTextColor = new Color(0.60f, 0.64f, 0.70f, 1f);

        [SerializeField] PlanetBootstrap bootstrap;
        [SerializeField] InputField seedField;
        [SerializeField] Button regenerateButton;
        [SerializeField] Button randomizeButton;
        [SerializeField] Slider continentSlider;
        [SerializeField] Slider distortionSlider;
        [SerializeField] Slider coastSlider;
        [SerializeField] Slider mountainSlider;
        [SerializeField] Slider rangeCoverageSlider;
        [SerializeField] Slider ruggednessSlider;
        [SerializeField] Slider seaLevelSlider;
        [SerializeField] Slider resolutionSlider;
        [SerializeField] Text continentValue;
        [SerializeField] Text distortionValue;
        [SerializeField] Text coastValue;
        [SerializeField] Text mountainValue;
        [SerializeField] Text rangeCoverageValue;
        [SerializeField] Text ruggednessValue;
        [SerializeField] Text seaLevelValue;
        [SerializeField] Text resolutionValue;
        [SerializeField] Text statsText;
        [SerializeField] Toggle hydrologyToggle;
        [SerializeField] Button hydrologyViewButton;
        [SerializeField] Text hydrologyViewLabel;
        [SerializeField] Slider hydrologyResolutionSlider;
        [SerializeField] Text hydrologyResolutionValue;
        [SerializeField] Text hydrologyStatsText;
        [SerializeField] Toggle riverToggle;
        [SerializeField] Toggle riverDebugToggle;
        [SerializeField] Slider riverDensitySlider;
        [SerializeField] Slider riverWidthSlider;
        [SerializeField] Slider riverSmoothingSlider;
        [SerializeField] Text riverDensityValue;
        [SerializeField] Text riverWidthValue;
        [SerializeField] Text riverSmoothingValue;
        [SerializeField] Text riverStatsText;
        [SerializeField] Toggle lakeToggle;
        [SerializeField] Button lakeViewButton;
        [SerializeField] Text lakeViewLabel;
        [SerializeField] Slider lakeAbundanceSlider;
        [SerializeField] Slider lakeMinAreaSlider;
        [SerializeField] Slider lakeLevelSlider;
        [SerializeField] Text lakeAbundanceValue;
        [SerializeField] Text lakeMinAreaValue;
        [SerializeField] Text lakeLevelValue;
        [SerializeField] Text lakeStatsText;

        static readonly PlanetDebugView[] HydrologyViews =
        {
            PlanetDebugView.RiverCandidates, PlanetDebugView.DrainageBasins,
            PlanetDebugView.FlowAccumulation, PlanetDebugView.FlowDirection,
        };

        static readonly string[] HydrologyViewNames = { "River Candidates", "Drainage Basins", "Flow Accumulation", "Flow Direction" };
        static readonly PlanetDebugView[] LakeViews =
        {
            PlanetDebugView.None, PlanetDebugView.LakeCandidates, PlanetDebugView.LakeDepth,
            PlanetDebugView.LakeClassification, PlanetDebugView.LakeConnectivity,
        };

        static readonly string[] LakeViewNames = { "Debug: Off", "Debug: Candidates", "Debug: Depth", "Debug: Classes", "Debug: Connectivity" };

        static readonly int[] HydrologyResolutions =
        {
            HydrologySettings.PreviewResolution, HydrologySettings.StandardResolution, HydrologySettings.HighResolution,
        };

        [Tooltip("Delay after the last slider change before the planet is regenerated (seconds).")]
        [SerializeField, Range(0f, 2f)] float sliderRegenerateDelay = 0.35f;

        float pendingRegenerateTime = -1f;
        bool syncing;
        int selectedHydrologyView;
        int selectedLakeView;
        PlanetVisualController subscribedVisual;

        /// <summary>Creates a screen-space canvas with the panel and, if needed, an EventSystem.</summary>
        public static PlanetGenerationPanel Create(PlanetBootstrap owner)
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Generation UI", typeof(RectTransform));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();

            // In play mode Awake builds the layout during AddComponent; in edit mode it does not.
            var panel = canvasObject.AddComponent<PlanetGenerationPanel>();
            panel.EnsureLayout();
            panel.SetBootstrap(owner);
            return panel;
        }

        public static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
            var module = eventSystemObject.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }

        public void SetBootstrap(PlanetBootstrap owner)
        {
            EnsureLayout();
            if (isActiveAndEnabled && bootstrap != null)
            {
                bootstrap.PlanetGenerated -= OnPlanetGenerated;
            }
            bootstrap = owner;
            if (isActiveAndEnabled && bootstrap != null)
            {
                bootstrap.PlanetGenerated -= OnPlanetGenerated;
                bootstrap.PlanetGenerated += OnPlanetGenerated;
                SyncFromDefinition();
                SubscribeVisual();
            }
        }

        void Awake()
        {
            EnsureLayout();
        }

        /// <summary>
        /// Builds the layout if missing. A panel serialized by an older version (missing
        /// newer controls) is rebuilt, so existing scenes keep working without re-running
        /// the setup utility.
        /// </summary>
        void EnsureLayout()
        {
            if (seedField != null && distortionSlider != null && coastSlider != null
                && rangeCoverageSlider != null && ruggednessSlider != null
                && hydrologyToggle != null && hydrologyStatsText != null
                && riverToggle != null && riverDensitySlider != null && riverStatsText != null
                && lakeToggle != null && lakeMinAreaSlider != null && lakeStatsText != null)
            {
                return;
            }
            Transform obsolete = transform.Find(PanelObjectName);
            if (obsolete != null)
            {
                obsolete.name = PanelObjectName + " (obsolete)";
                obsolete.gameObject.SetActive(false);
                if (Application.isPlaying)
                {
                    Destroy(obsolete.gameObject);
                }
                else
                {
                    DestroyImmediate(obsolete.gameObject);
                }
            }
            BuildLayout();
        }

        void OnEnable()
        {
            seedField.onEndEdit.AddListener(OnSeedEdited);
            regenerateButton.onClick.AddListener(OnRegenerateClicked);
            randomizeButton.onClick.AddListener(OnRandomizeClicked);
            continentSlider.onValueChanged.AddListener(OnSliderChanged);
            distortionSlider.onValueChanged.AddListener(OnSliderChanged);
            coastSlider.onValueChanged.AddListener(OnSliderChanged);
            mountainSlider.onValueChanged.AddListener(OnSliderChanged);
            rangeCoverageSlider.onValueChanged.AddListener(OnSliderChanged);
            ruggednessSlider.onValueChanged.AddListener(OnSliderChanged);
            seaLevelSlider.onValueChanged.AddListener(OnSliderChanged);
            resolutionSlider.onValueChanged.AddListener(OnSliderChanged);
            hydrologyToggle.onValueChanged.AddListener(OnHydrologyToggled);
            hydrologyViewButton.onClick.AddListener(OnHydrologyViewClicked);
            hydrologyResolutionSlider.onValueChanged.AddListener(OnHydrologySettingChanged);
            riverToggle.onValueChanged.AddListener(OnRiverToggled);
            riverDebugToggle.onValueChanged.AddListener(OnRiverDebugToggled);
            riverDensitySlider.onValueChanged.AddListener(OnRiverSettingChanged);
            riverWidthSlider.onValueChanged.AddListener(OnRiverSettingChanged);
            riverSmoothingSlider.onValueChanged.AddListener(OnRiverSettingChanged);
            lakeToggle.onValueChanged.AddListener(OnLakeToggled);
            lakeViewButton.onClick.AddListener(OnLakeViewClicked);
            lakeAbundanceSlider.onValueChanged.AddListener(OnLakeSettingChanged);
            lakeMinAreaSlider.onValueChanged.AddListener(OnLakeSettingChanged);
            lakeLevelSlider.onValueChanged.AddListener(OnLakeSettingChanged);
            if (bootstrap != null)
            {
                bootstrap.PlanetGenerated -= OnPlanetGenerated;
                bootstrap.PlanetGenerated += OnPlanetGenerated;
                SyncFromDefinition();
                SubscribeVisual();
                if (bootstrap.Current != null)
                {
                    OnPlanetGenerated(bootstrap.Current);
                }
            }
        }

        void OnDisable()
        {
            seedField.onEndEdit.RemoveListener(OnSeedEdited);
            regenerateButton.onClick.RemoveListener(OnRegenerateClicked);
            randomizeButton.onClick.RemoveListener(OnRandomizeClicked);
            continentSlider.onValueChanged.RemoveListener(OnSliderChanged);
            distortionSlider.onValueChanged.RemoveListener(OnSliderChanged);
            coastSlider.onValueChanged.RemoveListener(OnSliderChanged);
            mountainSlider.onValueChanged.RemoveListener(OnSliderChanged);
            rangeCoverageSlider.onValueChanged.RemoveListener(OnSliderChanged);
            ruggednessSlider.onValueChanged.RemoveListener(OnSliderChanged);
            seaLevelSlider.onValueChanged.RemoveListener(OnSliderChanged);
            resolutionSlider.onValueChanged.RemoveListener(OnSliderChanged);
            hydrologyToggle.onValueChanged.RemoveListener(OnHydrologyToggled);
            hydrologyViewButton.onClick.RemoveListener(OnHydrologyViewClicked);
            hydrologyResolutionSlider.onValueChanged.RemoveListener(OnHydrologySettingChanged);
            riverToggle.onValueChanged.RemoveListener(OnRiverToggled);
            riverDebugToggle.onValueChanged.RemoveListener(OnRiverDebugToggled);
            riverDensitySlider.onValueChanged.RemoveListener(OnRiverSettingChanged);
            riverWidthSlider.onValueChanged.RemoveListener(OnRiverSettingChanged);
            riverSmoothingSlider.onValueChanged.RemoveListener(OnRiverSettingChanged);
            lakeToggle.onValueChanged.RemoveListener(OnLakeToggled);
            lakeViewButton.onClick.RemoveListener(OnLakeViewClicked);
            lakeAbundanceSlider.onValueChanged.RemoveListener(OnLakeSettingChanged);
            lakeMinAreaSlider.onValueChanged.RemoveListener(OnLakeSettingChanged);
            lakeLevelSlider.onValueChanged.RemoveListener(OnLakeSettingChanged);
            if (bootstrap != null)
            {
                bootstrap.PlanetGenerated -= OnPlanetGenerated;
            }
            if (subscribedVisual != null)
            {
                subscribedVisual.HydrologyUpdated -= OnHydrologyUpdated;
                subscribedVisual.RiversUpdated -= OnRiversUpdated;
                subscribedVisual.LakesUpdated -= OnLakesUpdated;
                subscribedVisual = null;
            }
        }

        void Update()
        {
            if (pendingRegenerateTime >= 0f && Time.unscaledTime >= pendingRegenerateTime)
            {
                pendingRegenerateTime = -1f;
                ApplyAndRegenerate();
            }
            SyncHydrologyToggle();
        }

        // ------------------------------------------------------------------ Hydrology

        PlanetVisualController Visual => bootstrap != null ? bootstrap.Visual : null;

        void SubscribeVisual()
        {
            PlanetVisualController visual = Visual;
            if (visual == subscribedVisual)
            {
                return;
            }
            if (subscribedVisual != null)
            {
                subscribedVisual.HydrologyUpdated -= OnHydrologyUpdated;
                subscribedVisual.RiversUpdated -= OnRiversUpdated;
                subscribedVisual.LakesUpdated -= OnLakesUpdated;
            }
            subscribedVisual = visual;
            if (visual != null)
            {
                visual.HydrologyUpdated += OnHydrologyUpdated;
                visual.RiversUpdated += OnRiversUpdated;
                visual.LakesUpdated += OnLakesUpdated;
                SyncHydrologyControls();
            }
        }

        void OnHydrologyToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // Applied by the visual controller on its next Update (hydrology is computed
                // on demand the first time, then cached).
                visual.DebugView = show ? HydrologyViews[selectedHydrologyView] : PlanetDebugView.None;
            }
        }

        void OnHydrologyViewClicked()
        {
            selectedHydrologyView = (selectedHydrologyView + 1) % HydrologyViews.Length;
            hydrologyViewLabel.text = "View: " + HydrologyViewNames[selectedHydrologyView];
            if (hydrologyToggle.isOn)
            {
                OnHydrologyToggled(true);
            }
        }

        void OnHydrologySettingChanged(float _)
        {
            PlanetVisualController visual = Visual;
            if (syncing || visual == null)
            {
                return;
            }
            // Triggers a new analysis only while hydrology or rivers are shown.
            visual.Hydrology.resolution = HydrologyResolutions[Mathf.RoundToInt(hydrologyResolutionSlider.value)];
            UpdateHydrologyLabels();
        }

        // ------------------------------------------------------------------ Rivers

        void OnRiverToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                visual.Rivers.showRivers = show;
            }
        }

        void OnRiverDebugToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                visual.DebugView = show ? PlanetDebugView.RiverNetwork : PlanetDebugView.None;
            }
        }

        void OnRiverSettingChanged(float _)
        {
            PlanetVisualController visual = Visual;
            if (syncing || visual == null)
            {
                return;
            }
            // Density -> network rebuild (hydrology kept); width -> ribbons only;
            // smoothing -> trajectories + ribbons (topology kept). Applied by the visual
            // controller on its next Update.
            visual.Hydrology.riverNetworkThreshold = ThresholdFromDensity(riverDensitySlider.value);
            visual.Rivers.widthMultiplier = riverWidthSlider.value;
            visual.Rivers.smoothing = riverSmoothingSlider.value;
            UpdateRiverLabels();
        }

        void OnRiversUpdated(RiverNetwork network)
        {
            PlanetVisualController visual = Visual;
            RiverRibbonBuilder ribbons = visual != null ? visual.RiverRibbons : null;
            float radiusLengths = (float)network.TotalLength;
            riverStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} rivers ({1:N0} drawn) · {2:N0} sources · {3:N0} confluences · {4:N0} mouths\n"
                + "Strahler max {5} · length {6:0.0} R · network {7:0.0} ms · trajectories {8:0.0} ms · ribbons {9:0} ms",
                network.RiverCount, visual != null && visual.RiversVisible ? ribbons.LastDisplayedRiverCount : 0,
                network.SourceCount, network.ConfluenceCount, network.MouthCount + network.SinkCount,
                network.MaxStrahler, radiusLengths,
                network.ExtractionMilliseconds + network.TopologyMilliseconds + network.ClassificationMilliseconds,
                network.SmoothingMilliseconds, ribbons != null ? ribbons.LastMilliseconds : 0f);
            if (visual != null && visual.RiversVisible)
            {
                // Measured during the last ribbon build (see RiverGeometryDiagnostics).
                RiverGeometryDiagnostics d = ribbons.LastDiagnostics;
                riverStatsText.text += string.Format(CultureInfo.InvariantCulture,
                    "\nGeometry: mouths extended {0}, gaps {1} · junction defects {2} · crossings D8 {3} / smoothing {4} (fixed {5}) · overlaps {6} · bad triangles removed {7}",
                    d.MouthsExtended, d.MouthGaps, d.JunctionDefects, network.GraphCrossingCount, network.SmoothingCrossingCount,
                    network.CorrectedPieceCount, d.WidthOverlaps, d.InvalidTrianglesRemoved);
            }
        }

        void SyncRiverControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Rivers.Sanitize();
            riverDensitySlider.SetValueWithoutNotify(DensityFromThreshold(visual.Hydrology.riverNetworkThreshold));
            riverWidthSlider.SetValueWithoutNotify(visual.Rivers.widthMultiplier);
            riverSmoothingSlider.SetValueWithoutNotify(visual.Rivers.smoothing);
            riverToggle.SetIsOnWithoutNotify(visual.Rivers.showRivers);
            UpdateRiverLabels();
            syncing = false;
        }

        void UpdateRiverLabels()
        {
            riverDensityValue.text = (riverDensitySlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            riverWidthValue.text = riverWidthSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            int passes = Mathf.Clamp(Mathf.RoundToInt(riverSmoothingSlider.value * RiverSettings.MaxSmoothingIterations), 0, RiverSettings.MaxSmoothingIterations);
            riverSmoothingValue.text = passes + (passes == 1 ? " pass" : " passes");
        }

        // ------------------------------------------------------------------ Lakes

        void OnLakeToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // Display only: the lake analysis is cached and reused.
                visual.Lakes.showLakes = show;
            }
        }

        void OnLakeViewClicked()
        {
            selectedLakeView = (selectedLakeView + 1) % LakeViews.Length;
            lakeViewLabel.text = LakeViewNames[selectedLakeView];
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                visual.DebugView = LakeViews[selectedLakeView];
            }
        }

        void OnLakeSettingChanged(float _)
        {
            PlanetVisualController visual = Visual;
            if (syncing || visual == null)
            {
                return;
            }
            // Selection only: the depression analysis and the hydrology are reused. Applied by the
            // visual controller on its next Update.
            visual.Lakes.abundance = lakeAbundanceSlider.value;
            visual.Lakes.minimumArea = MinimumAreaFromSlider(lakeMinAreaSlider.value);
            visual.Lakes.fillingFactor = lakeLevelSlider.value;
            UpdateLakeLabels();
        }

        void OnLakesUpdated(LakeSet set)
        {
            PlanetVisualController visual = Visual;
            double surface = set.Hydrology.Grid.TotalArea;
            double land = System.Math.Max(set.Hydrology.LandArea, 1e-12);
            double largest = set.LargestLake >= 0 ? set.GetLake(set.LargestLake).Area : 0.0;
            float mesh = visual != null && visual.LakesVisible ? visual.LakeMeshes.LastMilliseconds : 0f;
            float layer = visual != null && visual.CurrentLakeLayer != null ? visual.CurrentLakeLayer.Milliseconds : 0f;
            lakeStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} candidates · {1} lakes ({2} open, {3} closed) · {4:0.00}% of land\n"
                + "Largest {5:0.00}% of land ({6:0.000}% of surface) · {7:0} ms (analysis {8:0}, selection {9:0}, rivers {10:0}, mesh {11:0})",
                set.CandidateCount, set.Count, set.OpenCount, set.ClosedCount, set.TotalArea / land * 100.0,
                largest / land * 100.0, largest / surface * 100.0,
                set.Depressions.Milliseconds + set.SelectionMilliseconds + layer + mesh,
                set.Depressions.Milliseconds, set.SelectionMilliseconds, layer, mesh);
        }

        void SyncLakeControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Lakes.Sanitize();
            lakeAbundanceSlider.SetValueWithoutNotify(visual.Lakes.abundance);
            lakeMinAreaSlider.SetValueWithoutNotify(SliderFromMinimumArea(visual.Lakes.minimumArea));
            lakeLevelSlider.SetValueWithoutNotify(visual.Lakes.fillingFactor);
            lakeToggle.SetIsOnWithoutNotify(visual.Lakes.showLakes);
            UpdateLakeLabels();
            syncing = false;
        }

        void UpdateLakeLabels()
        {
            lakeAbundanceValue.text = (lakeAbundanceSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            // Fraction of the planet surface, shown as a percentage.
            lakeMinAreaValue.text = (MinimumAreaFromSlider(lakeMinAreaSlider.value) * 100f).ToString("0.000", CultureInfo.InvariantCulture) + "%";
            lakeLevelValue.text = (lakeLevelSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // Lake Minimum Area slider: logarithmic between the documented bounds.
        static float MinimumAreaFromSlider(float value)
        {
            return Mathf.Pow(10f, Mathf.Lerp(Mathf.Log10(LakeSettings.MinMinimumArea), Mathf.Log10(LakeSettings.MaxMinimumArea), value));
        }

        static float SliderFromMinimumArea(float area)
        {
            return Mathf.InverseLerp(Mathf.Log10(LakeSettings.MinMinimumArea), Mathf.Log10(LakeSettings.MaxMinimumArea), Mathf.Log10(area));
        }

        void OnHydrologyUpdated(HydrologyResult result)
        {
            double landArea = System.Math.Max(result.LandArea, 1e-12);
            hydrologyStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "Hydrology {0}: {1:N0} basins · largest {2:0.0}% of land\n{3:N0} depressions filled · max drained {4:0.00}% of surface · {5:0} ms",
                result.Grid.Resolution, result.BasinCount, result.LargestBasinArea / landArea * 100.0,
                result.DepressionCount, result.MaxAccumulation / result.Grid.TotalArea * 100.0, result.ComputeMilliseconds);
        }

        /// <summary>Keeps the toggle in sync when the view is changed from the inspector.</summary>
        void SyncHydrologyToggle()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            bool shown = PlanetDebugViews.IsHydrology(visual.DebugView);
            if (hydrologyToggle.isOn != shown)
            {
                hydrologyToggle.SetIsOnWithoutNotify(shown);
            }
            bool networkShown = visual.DebugView == PlanetDebugView.RiverNetwork;
            if (riverDebugToggle.isOn != networkShown)
            {
                riverDebugToggle.SetIsOnWithoutNotify(networkShown);
            }
            if (riverToggle.isOn != visual.Rivers.showRivers)
            {
                riverToggle.SetIsOnWithoutNotify(visual.Rivers.showRivers);
            }
            if (lakeToggle.isOn != visual.Lakes.showLakes)
            {
                lakeToggle.SetIsOnWithoutNotify(visual.Lakes.showLakes);
            }
            int lakeView = System.Array.IndexOf(LakeViews, visual.DebugView);
            lakeView = lakeView < 0 ? 0 : lakeView;
            if (lakeView != selectedLakeView)
            {
                selectedLakeView = lakeView;
                lakeViewLabel.text = LakeViewNames[selectedLakeView];
            }
            int index = System.Array.IndexOf(HydrologyViews, visual.DebugView);
            if (shown && index >= 0 && index != selectedHydrologyView)
            {
                selectedHydrologyView = index;
                hydrologyViewLabel.text = "View: " + HydrologyViewNames[selectedHydrologyView];
            }
        }

        void SyncHydrologyControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Hydrology.Sanitize();
            hydrologyResolutionSlider.SetValueWithoutNotify(System.Array.IndexOf(HydrologyResolutions, visual.Hydrology.resolution));
            hydrologyViewLabel.text = "View: " + HydrologyViewNames[selectedHydrologyView];
            UpdateHydrologyLabels();
            syncing = false;
            SyncRiverControls();
            SyncLakeControls();
            SyncHydrologyToggle();
        }

        void UpdateHydrologyLabels()
        {
            hydrologyResolutionValue.text = HydrologyResolutions[Mathf.RoundToInt(hydrologyResolutionSlider.value)].ToString(CultureInfo.InvariantCulture);
        }

        // River Density is the inverse, logarithmic view of the single river threshold:
        // 0% = largest threshold (few big rivers), 100% = smallest (dense network).
        static float ThresholdFromDensity(float density)
        {
            float logMin = Mathf.Log10(HydrologySettings.MinRiverNetworkThreshold);
            float logMax = Mathf.Log10(HydrologySettings.MaxRiverNetworkThreshold);
            return Mathf.Pow(10f, Mathf.Lerp(logMax, logMin, density));
        }

        static float DensityFromThreshold(float threshold)
        {
            float logMin = Mathf.Log10(HydrologySettings.MinRiverNetworkThreshold);
            float logMax = Mathf.Log10(HydrologySettings.MaxRiverNetworkThreshold);
            return Mathf.InverseLerp(logMax, logMin, Mathf.Log10(threshold));
        }

        void OnSeedEdited(string text)
        {
            // Enter in the seed field regenerates immediately. Losing focus (e.g. by
            // clicking Regenerate) does not, to avoid generating twice.
            Keyboard keyboard = Keyboard.current;
            bool submitted = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
            if (!syncing && submitted && TryParseSeed(text, out _))
            {
                ApplyAndRegenerate();
            }
        }

        void OnRegenerateClicked()
        {
            ApplyAndRegenerate();
        }

        void OnRandomizeClicked()
        {
            if (bootstrap == null)
            {
                return;
            }
            ApplyControlsToDefinition();
            bootstrap.RandomizeSeed();
            SyncFromDefinition();
            pendingRegenerateTime = -1f;
            bootstrap.Regenerate();
        }

        void OnSliderChanged(float _)
        {
            if (syncing)
            {
                return;
            }
            UpdateValueLabels();
            pendingRegenerateTime = Time.unscaledTime + sliderRegenerateDelay;
        }

        void ApplyAndRegenerate()
        {
            if (bootstrap == null)
            {
                return;
            }
            pendingRegenerateTime = -1f;
            ApplyControlsToDefinition();
            bootstrap.Regenerate();
            SyncFromDefinition();
        }

        void ApplyControlsToDefinition()
        {
            PlanetDefinition definition = bootstrap.Definition;
            if (TryParseSeed(seedField.text, out int seed))
            {
                definition.seed = seed;
            }
            definition.continentStrength = continentSlider.value;
            definition.continentalWarpStrength = distortionSlider.value;
            definition.coastalComplexity = coastSlider.value;
            definition.mountainStrength = mountainSlider.value;
            definition.mountainRangeCoverage = rangeCoverageSlider.value;
            definition.mountainRuggedness = ruggednessSlider.value;
            definition.seaLevel = seaLevelSlider.value;
            definition.resolution = Mathf.RoundToInt(resolutionSlider.value) * 16;
            definition.Sanitize();
        }

        void SyncFromDefinition()
        {
            if (bootstrap == null)
            {
                return;
            }
            syncing = true;
            PlanetDefinition definition = bootstrap.Definition;
            seedField.text = definition.seed.ToString(CultureInfo.InvariantCulture);
            continentSlider.SetValueWithoutNotify(definition.continentStrength);
            distortionSlider.SetValueWithoutNotify(definition.continentalWarpStrength);
            coastSlider.SetValueWithoutNotify(definition.coastalComplexity);
            mountainSlider.SetValueWithoutNotify(definition.mountainStrength);
            rangeCoverageSlider.SetValueWithoutNotify(definition.mountainRangeCoverage);
            ruggednessSlider.SetValueWithoutNotify(definition.mountainRuggedness);
            seaLevelSlider.SetValueWithoutNotify(definition.seaLevel);
            resolutionSlider.SetValueWithoutNotify(definition.resolution / 16f);
            UpdateValueLabels();
            syncing = false;
        }

        void OnPlanetGenerated(PlanetInstance planet)
        {
            if (statsText == null)
            {
                return;
            }
            PlanetDefinition d = planet.Definition;
            int vertexCount = 6 * (d.resolution + 1) * (d.resolution + 1);
            statsText.text = string.Format(CultureInfo.InvariantCulture,
                "Seed {0}   ·   Land {1:0}%\nRelief {2:+0.0;-0.0}% / {3:+0.0;-0.0}% of radius\n{4:N0} vertices   ·   {5:0} ms",
                d.seed, planet.LandFraction * 100f, planet.MinElevation * 100f, planet.MaxElevation * 100f,
                vertexCount, bootstrap != null ? bootstrap.LastGenerationMilliseconds : 0f);
        }

        void UpdateValueLabels()
        {
            continentValue.text = (continentSlider.value * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            distortionValue.text = distortionSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
            coastValue.text = (coastSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            mountainValue.text = (mountainSlider.value * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            rangeCoverageValue.text = (rangeCoverageSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            ruggednessValue.text = (ruggednessSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            seaLevelValue.text = (seaLevelSlider.value * 100f).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
            resolutionValue.text = (Mathf.RoundToInt(resolutionSlider.value) * 16).ToString(CultureInfo.InvariantCulture);
        }

        static bool TryParseSeed(string text, out int seed)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
        }

        // ------------------------------------------------------------------ Layout

        /// <summary>Builds the panel hierarchy under this object's canvas.</summary>
        public void BuildLayout()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panel = CreateRect(PanelObjectName, transform);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = new Vector2(24f, -24f);
            panel.sizeDelta = new Vector2(PanelWidth, 0f);
            panel.gameObject.AddComponent<Image>().color = PanelColor;
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 14, 14);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CreateLabel(panel, font, "PLANETWALKER", 26, FontStyle.Bold, TextColor, 32f);
            CreateLabel(panel, font, "GENESIS V0.1.1D-B.1", 14, FontStyle.Normal, AccentColor, 20f);
            CreateSpacer(panel, 6f);

            var seedRow = CreateRow(panel, "Seed Row");
            CreateLabel(seedRow, font, "Seed", FontSize, FontStyle.Normal, MutedTextColor, RowHeight, 70f);
            seedField = CreateInputField(seedRow, font);

            var buttonRow = CreateRow(panel, "Button Row");
            regenerateButton = CreateButton(buttonRow, font, "Regenerate");
            randomizeButton = CreateButton(buttonRow, font, "Randomize");
            CreateSpacer(panel, 4f);

            continentSlider = CreateSliderRow(panel, font, "Continents", 0f, PlanetDefinition.MaxContinentStrength, false, out continentValue);
            distortionSlider = CreateSliderRow(panel, font, "Continental Distortion", 0f, PlanetDefinition.MaxContinentalWarpStrength, false, out distortionValue);
            coastSlider = CreateSliderRow(panel, font, "Coastal Complexity", 0f, 1f, false, out coastValue);
            mountainSlider = CreateSliderRow(panel, font, "Mountains", 0f, PlanetDefinition.MaxMountainStrength, false, out mountainValue);
            rangeCoverageSlider = CreateSliderRow(panel, font, "Range Coverage", 0f, 1f, false, out rangeCoverageValue);
            ruggednessSlider = CreateSliderRow(panel, font, "Ruggedness", 0f, 1f, false, out ruggednessValue);
            seaLevelSlider = CreateSliderRow(panel, font, "Sea level", PlanetDefinition.MinSeaLevel, PlanetDefinition.MaxSeaLevel, false, out seaLevelValue);
            resolutionSlider = CreateSliderRow(panel, font, "Resolution",
                Mathf.CeilToInt(PlanetDefinition.MinResolution / 16f), PlanetDefinition.MaxResolution / 16, true, out resolutionValue);

            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "HYDROLOGY", 13, FontStyle.Bold, AccentColor, 18f);
            var hydrologyRow = CreateRow(panel, "Hydrology Row");
            hydrologyToggle = CreateToggle(hydrologyRow, font, "Show Hydrology");
            hydrologyViewButton = CreateButton(hydrologyRow, font, "View: River Candidates");
            hydrologyViewLabel = hydrologyViewButton.GetComponentInChildren<Text>();
            hydrologyViewLabel.fontSize = FontSize - 3;
            hydrologyResolutionSlider = CreateSliderRow(panel, font, "Hydrology Resolution", 0f, HydrologyResolutions.Length - 1, true,
                out hydrologyResolutionValue);
            hydrologyStatsText = CreateLabel(panel, font, "Hydrology: computed when shown", 12, FontStyle.Normal, MutedTextColor, 34f);

            CreateLabel(panel, font, "RIVERS", 13, FontStyle.Bold, AccentColor, 18f);
            var riverRow = CreateRow(panel, "River Row");
            riverToggle = CreateToggle(riverRow, font, "Show Rivers");
            riverDebugToggle = CreateToggle(riverRow, font, "Debug River Network");
            riverDensitySlider = CreateSliderRow(panel, font, "River Density", 0f, 1f, false, out riverDensityValue);
            riverWidthSlider = CreateSliderRow(panel, font, "River Width", 0.25f, 4f, false, out riverWidthValue);
            riverSmoothingSlider = CreateSliderRow(panel, font, "River Smoothing", 0f, 1f, false, out riverSmoothingValue);
            riverStatsText = CreateLabel(panel, font, "Rivers: computed when shown", 11, FontStyle.Normal, MutedTextColor, 46f);

            CreateLabel(panel, font, "LAKES", 13, FontStyle.Bold, AccentColor, 18f);
            var lakeRow = CreateRow(panel, "Lake Row");
            lakeToggle = CreateToggle(lakeRow, font, "Show Lakes");
            lakeViewButton = CreateButton(lakeRow, font, LakeViewNames[0]);
            lakeViewLabel = lakeViewButton.GetComponentInChildren<Text>();
            lakeViewLabel.fontSize = FontSize - 3;
            lakeAbundanceSlider = CreateSliderRow(panel, font, "Lake Abundance", 0f, 1f, false, out lakeAbundanceValue);
            lakeMinAreaSlider = CreateSliderRow(panel, font, "Lake Minimum Area", 0f, 1f, false, out lakeMinAreaValue);
            lakeLevelSlider = CreateSliderRow(panel, font, "Closed Lake Level", 0f, 1f, false, out lakeLevelValue);
            lakeStatsText = CreateLabel(panel, font, "Lakes: computed when shown", 11, FontStyle.Normal, MutedTextColor, 32f);

            CreateSpacer(panel, 4f);
            statsText = CreateLabel(panel, font, "", 13, FontStyle.Normal, MutedTextColor, 54f);
            CreateLabel(panel, font, "LMB drag: rotate   ·   Wheel: zoom   ·   R: reset", 12, FontStyle.Italic, MutedTextColor, 18f);
        }

        static RectTransform CreateRect(string objectName, Transform parent)
        {
            var go = new GameObject(objectName, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        static RectTransform CreateRow(RectTransform parent, string objectName)
        {
            var row = CreateRect(objectName, parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
            return row;
        }

        static void CreateSpacer(RectTransform parent, float height)
        {
            CreateRect("Spacer", parent).gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        }

        static Text CreateLabel(RectTransform parent, Font font, string text, int size, FontStyle style, Color color,
            float height, float width = -1f)
        {
            var rect = CreateRect("Label", parent);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            if (width > 0f)
            {
                element.preferredWidth = width;
                element.minWidth = width;
            }
            return label;
        }

        static InputField CreateInputField(RectTransform parent, Font font)
        {
            var rect = CreateRect("Seed Input", parent);
            var background = rect.gameObject.AddComponent<Image>();
            background.color = ControlColor;
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var textRect = CreateRect("Text", rect);
            Stretch(textRect, 10f, 4f);
            var text = textRect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = FontSize;
            text.color = TextColor;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            var placeholderRect = CreateRect("Placeholder", rect);
            Stretch(placeholderRect, 10f, 4f);
            var placeholder = placeholderRect.gameObject.AddComponent<Text>();
            placeholder.font = font;
            placeholder.fontSize = FontSize;
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = MutedTextColor;
            placeholder.alignment = TextAnchor.MiddleLeft;
            placeholder.text = "Integer seed";

            var field = rect.gameObject.AddComponent<InputField>();
            field.targetGraphic = background;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.contentType = InputField.ContentType.IntegerNumber;
            field.characterLimit = 11;
            return field;
        }

        static Toggle CreateToggle(RectTransform parent, Font font, string caption)
        {
            var rect = CreateRect(caption + " Toggle", parent);
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var box = CreateRect("Background", rect);
            box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
            box.pivot = new Vector2(0f, 0.5f);
            box.sizeDelta = new Vector2(20f, 20f);
            var boxImage = box.gameObject.AddComponent<Image>();
            boxImage.color = ControlColor;

            var check = CreateRect("Checkmark", box);
            Stretch(check, 4f, 4f);
            var checkImage = check.gameObject.AddComponent<Image>();
            checkImage.color = AccentColor;

            var labelRect = CreateRect("Label", rect);
            Stretch(labelRect, 0f, 0f);
            labelRect.offsetMin = new Vector2(28f, 0f);
            var label = labelRect.gameObject.AddComponent<Text>();
            label.font = font;
            label.text = caption;
            label.fontSize = FontSize - 1;
            label.color = TextColor;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;

            var toggle = rect.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = checkImage;
            toggle.isOn = false;
            return toggle;
        }

        static Button CreateButton(RectTransform parent, Font font, string caption)
        {
            var rect = CreateRect(caption + " Button", parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ControlColor;
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.3f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.85f, 1f);
            button.colors = colors;

            var labelRect = CreateRect("Text", rect);
            Stretch(labelRect, 0f, 0f);
            var label = labelRect.gameObject.AddComponent<Text>();
            label.font = font;
            label.text = caption;
            label.fontSize = FontSize;
            label.color = TextColor;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            return button;
        }

        static Slider CreateSliderRow(RectTransform parent, Font font, string caption, float min, float max, bool wholeNumbers,
            out Text valueLabel)
        {
            var row = CreateRow(parent, caption + " Row");
            CreateLabel(row, font, caption, FontSize - 1, FontStyle.Normal, MutedTextColor, RowHeight, SliderLabelWidth);

            var sliderRect = CreateRect(caption + " Slider", row);
            sliderRect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var track = CreateRect("Background", sliderRect);
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.sizeDelta = new Vector2(0f, 6f);
            track.gameObject.AddComponent<Image>().color = ControlColor;

            var fillArea = CreateRect("Fill Area", sliderRect);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(0f, 6f);
            var fill = CreateRect("Fill", fillArea);
            Stretch(fill, 0f, 0f);
            fill.gameObject.AddComponent<Image>().color = AccentColor;

            var handleArea = CreateRect("Handle Slide Area", sliderRect);
            Stretch(handleArea, 0f, 0f);
            var handle = CreateRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(12f, 0f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = TextColor;

            var slider = sliderRect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;

            valueLabel = CreateLabel(row, font, "", FontSize - 2, FontStyle.Normal, TextColor, RowHeight, 58f);
            valueLabel.alignment = TextAnchor.MiddleRight;
            return slider;
        }

        static void Stretch(RectTransform rect, float horizontalPadding, float verticalPadding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontalPadding, verticalPadding);
            rect.offsetMax = new Vector2(-horizontalPadding, -verticalPadding);
        }
    }
}
