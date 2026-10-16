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
        const float PanelMargin = 24f;
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
        [SerializeField] Toggle climateToggle;
        [SerializeField] Button climateViewButton;
        [SerializeField] Text climateViewLabel;
        [SerializeField] Slider climateResolutionSlider;
        [SerializeField] Slider axialTiltSlider;
        [SerializeField] Slider climateElevationSlider;
        [SerializeField] Text climateResolutionValue;
        [SerializeField] Text axialTiltValue;
        [SerializeField] Text climateElevationValue;
        [SerializeField] Text climateLegendText;
        [SerializeField] RawImage climateLegendBar;
        [SerializeField] RectTransform climateLegendTicks;
        [SerializeField] Text climateLegendNote;
        [SerializeField] Text climateStatsText;
        [SerializeField] Text displayModeText;
        [SerializeField] Toggle naturalToggle;
        [SerializeField] Slider surfaceBlendSlider;
        [SerializeField] Slider surfaceVariationSlider;
        [SerializeField] Slider surfaceRockSlider;
        [SerializeField] Slider surfaceSaturationSlider;
        [SerializeField] Text surfaceBlendValue;
        [SerializeField] Text surfaceVariationValue;
        [SerializeField] Text surfaceRockValue;
        [SerializeField] Text surfaceSaturationValue;
        [SerializeField] Toggle oceanShadingToggle;
        [SerializeField] Toggle atmosphereToggle;
        [SerializeField] Slider oceanContrastSlider;
        [SerializeField] Slider oceanShelfSlider;
        [SerializeField] Slider oceanRoughnessSlider;
        [SerializeField] Slider oceanSpecularSlider;
        [SerializeField] Slider atmosphereStrengthSlider;
        [SerializeField] Slider atmosphereThicknessSlider;
        [SerializeField] Slider terminatorSlider;
        [SerializeField] Text oceanContrastValue;
        [SerializeField] Text oceanShelfValue;
        [SerializeField] Text oceanRoughnessValue;
        [SerializeField] Text oceanSpecularValue;
        [SerializeField] Text atmosphereStrengthValue;
        [SerializeField] Text atmosphereThicknessValue;
        [SerializeField] Text terminatorValue;
        [SerializeField] Toggle cloudToggle;
        [SerializeField] Toggle cloudMotionToggle;
        [SerializeField] Slider cloudCoverageSlider;
        [SerializeField] Slider cloudDensitySlider;
        [SerializeField] Slider cloudDetailSlider;
        [SerializeField] Slider cloudWeatherSlider;
        [SerializeField] Slider cloudConvectionSlider;
        [SerializeField] Slider cloudShadowSlider;
        [SerializeField] Slider cloudAltitudeSlider;
        [SerializeField] Text cloudCoverageValue;
        [SerializeField] Text cloudDensityValue;
        [SerializeField] Text cloudDetailValue;
        [SerializeField] Text cloudWeatherValue;
        [SerializeField] Text cloudConvectionValue;
        [SerializeField] Text cloudShadowValue;
        [SerializeField] Text cloudAltitudeValue;
        [SerializeField] Button cloudSkyButton;
        [SerializeField] Button cloudAutoSkyButton;
        [SerializeField] Text cloudSkyLabel;
        [SerializeField] Toggle cloudDebugToggle;
        [SerializeField] Button cloudViewButton;
        [SerializeField] Text cloudViewLabel;
        [SerializeField] Text cloudLegendText;
        [SerializeField] RawImage cloudLegendBar;
        [SerializeField] RectTransform cloudLegendTicks;
        [SerializeField] Text cloudLegendNote;
        [SerializeField] Toggle solarCycleToggle;
        [SerializeField] Button solarPauseButton;
        [SerializeField] Text solarPauseLabel;
        [SerializeField] Slider solarTimeSlider;
        [SerializeField] Text solarTimeValue;
        [SerializeField] Slider solarDurationSlider;
        [SerializeField] Text solarDurationValue;
        [SerializeField] Slider twilightSlider;
        [SerializeField] Text twilightValue;
        [SerializeField] Button solarResetButton;
        [SerializeField] Button contemplateButton;
        [SerializeField] Button showControlsButton;
        [SerializeField] Text contemplationHint;
        [SerializeField] Toggle biomeToggle;
        [SerializeField] Button biomeViewButton;
        [SerializeField] Text biomeViewLabel;
        [SerializeField] Slider biomeAriditySlider;
        [SerializeField] Slider biomeSharpnessSlider;
        [SerializeField] Slider biomeAlpineSlider;
        [SerializeField] Text biomeAridityValue;
        [SerializeField] Text biomeSharpnessValue;
        [SerializeField] Text biomeAlpineValue;
        [SerializeField] Text biomeLegendText;
        [SerializeField] RawImage biomeLegendBar;
        [SerializeField] RectTransform biomeLegendTicks;
        [SerializeField] Text biomeLegendNote;
        [SerializeField] Text biomeStatsText;
        [SerializeField] RectTransform panelFrame;
        [SerializeField] RectTransform panelContent;
        [SerializeField] ScrollRect panelScroll;

        static readonly PlanetDebugView[] HydrologyViews =
        {
            PlanetDebugView.RiverCandidates, PlanetDebugView.DrainageBasins,
            PlanetDebugView.FlowAccumulation, PlanetDebugView.FlowDirection,
        };

        static readonly string[] HydrologyViewNames = { "River Candidates", "Drainage Basins", "Flow Accumulation", "Flow Direction" };
        static readonly PlanetDebugView[] LakeViews =
        {
            PlanetDebugView.None, PlanetDebugView.LakeCandidates, PlanetDebugView.LakeDepth,
            PlanetDebugView.LakeClassification, PlanetDebugView.LakeConnectivity, PlanetDebugView.LakeShoreline,
        };

        // One name per entry of LakeViews (same order).
        static readonly string[] LakeViewNames =
        {
            "Debug: Off", "Debug: Candidates", "Debug: Depth", "Debug: Classes", "Debug: Connectivity", "Debug: Shoreline",
        };

        static readonly PlanetDebugView[] ClimateViews =
        {
            PlanetDebugView.ClimateTemperature, PlanetDebugView.ClimateMoisture, PlanetDebugView.ClimatePrecipitation,
            PlanetDebugView.ClimateWinds, PlanetDebugView.ClimateContinentality,
        };

        // One name per entry of ClimateViews (same order).
        static readonly string[] ClimateViewNames = { "View: Temperature", "View: Moisture", "View: Precipitation", "View: Winds", "View: Continentality" };

        static readonly PlanetDebugView[] CloudViews = { PlanetDebugView.CloudCoverage, PlanetDebugView.CloudStructure };
        static readonly string[] CloudViewNames = { "View: Coverage", "View: Structure" };
        int selectedCloudView;
        Texture2D cloudLegendTexture;

        static readonly PlanetDebugView[] BiomeViews =
        {
            PlanetDebugView.BiomeMap, PlanetDebugView.BiomeSuitability, PlanetDebugView.BiomeTransitions, PlanetDebugView.BiomeAridity,
        };

        // One name per entry of BiomeViews (same order).
        static readonly string[] BiomeViewNames = { "View: Biome Map", "View: Suitability", "View: Transitions", "View: Aridity" };

        static readonly int[] ClimateResolutions = { ClimateSettings.LowResolution, ClimateSettings.DefaultResolution, ClimateSettings.HighResolution };

        static readonly int[] HydrologyResolutions =
        {
            HydrologySettings.PreviewResolution, HydrologySettings.StandardResolution, HydrologySettings.HighResolution,
        };

        [Tooltip("Delay after the last slider change before the planet is regenerated (seconds).")]
        [SerializeField, Range(0f, 2f)] float sliderRegenerateDelay = 0.35f;

        float pendingRegenerateTime = -1f;
        Texture2D legendTexture;
        bool syncing;
        int selectedHydrologyView;
        int selectedLakeView;
        int selectedClimateView;
        float pendingClimateTime = -1f;
        int selectedBiomeView;
        float pendingBiomeTime = -1f;
        float pendingSurfaceTime = -1f;
        Texture2D biomeLegendTexture;
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
                && lakeToggle != null && lakeMinAreaSlider != null && lakeStatsText != null
                && climateToggle != null && climateElevationSlider != null && climateStatsText != null
                && panelScroll != null && panelContent != null && climateLegendBar != null && climateLegendTicks != null
                && biomeToggle != null && biomeAlpineSlider != null && biomeStatsText != null && biomeLegendBar != null
                && displayModeText != null && naturalToggle != null && surfaceSaturationSlider != null
                && oceanShadingToggle != null && atmosphereToggle != null && terminatorSlider != null
                && cloudToggle != null && cloudAltitudeSlider != null && cloudLegendBar != null && cloudSkyLabel != null
                && solarTimeSlider != null && contemplateButton != null && showControlsButton != null && contemplationHint != null)
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
            climateToggle.onValueChanged.AddListener(OnClimateToggled);
            climateViewButton.onClick.AddListener(OnClimateViewClicked);
            climateResolutionSlider.onValueChanged.AddListener(OnClimateSettingChanged);
            axialTiltSlider.onValueChanged.AddListener(OnClimateSettingChanged);
            climateElevationSlider.onValueChanged.AddListener(OnClimateSettingChanged);
            naturalToggle.onValueChanged.AddListener(OnNaturalToggled);
            surfaceBlendSlider.onValueChanged.AddListener(OnSurfaceSettingChanged);
            surfaceVariationSlider.onValueChanged.AddListener(OnSurfaceSettingChanged);
            surfaceRockSlider.onValueChanged.AddListener(OnSurfaceSettingChanged);
            surfaceSaturationSlider.onValueChanged.AddListener(OnSurfaceSettingChanged);
            oceanShadingToggle.onValueChanged.AddListener(OnOceanAtmosphereToggled);
            atmosphereToggle.onValueChanged.AddListener(OnOceanAtmosphereToggled);
            cloudToggle.onValueChanged.AddListener(OnCloudToggled);
            cloudMotionToggle.onValueChanged.AddListener(OnCloudToggled);
            foreach (Slider slider in CloudSliders())
            {
                slider.onValueChanged.AddListener(OnCloudSettingChanged);
            }
            cloudSkyButton.onClick.AddListener(OnNewSkyClicked);
            cloudAutoSkyButton.onClick.AddListener(OnAutoSkyClicked);
            cloudDebugToggle.onValueChanged.AddListener(OnCloudDebugToggled);
            cloudViewButton.onClick.AddListener(OnCloudViewClicked);
            solarCycleToggle.onValueChanged.AddListener(OnSolarCycleToggled);
            solarPauseButton.onClick.AddListener(OnSolarPauseClicked);
            solarTimeSlider.onValueChanged.AddListener(OnSolarTimeChanged);
            solarDurationSlider.onValueChanged.AddListener(OnSolarDurationChanged);
            twilightSlider.onValueChanged.AddListener(OnTwilightChanged);
            solarResetButton.onClick.AddListener(OnSolarResetClicked);
            contemplateButton.onClick.AddListener(OnContemplateClicked);
            showControlsButton.onClick.AddListener(OnShowControlsClicked);
            foreach (Slider slider in OceanAtmosphereSliders())
            {
                slider.onValueChanged.AddListener(OnOceanAtmosphereChanged);
            }
            biomeToggle.onValueChanged.AddListener(OnBiomeToggled);
            biomeViewButton.onClick.AddListener(OnBiomeViewClicked);
            biomeAriditySlider.onValueChanged.AddListener(OnBiomeSettingChanged);
            biomeSharpnessSlider.onValueChanged.AddListener(OnBiomeSettingChanged);
            biomeAlpineSlider.onValueChanged.AddListener(OnBiomeSettingChanged);
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
            climateToggle.onValueChanged.RemoveListener(OnClimateToggled);
            climateViewButton.onClick.RemoveListener(OnClimateViewClicked);
            climateResolutionSlider.onValueChanged.RemoveListener(OnClimateSettingChanged);
            axialTiltSlider.onValueChanged.RemoveListener(OnClimateSettingChanged);
            climateElevationSlider.onValueChanged.RemoveListener(OnClimateSettingChanged);
            naturalToggle.onValueChanged.RemoveListener(OnNaturalToggled);
            surfaceBlendSlider.onValueChanged.RemoveListener(OnSurfaceSettingChanged);
            surfaceVariationSlider.onValueChanged.RemoveListener(OnSurfaceSettingChanged);
            surfaceRockSlider.onValueChanged.RemoveListener(OnSurfaceSettingChanged);
            surfaceSaturationSlider.onValueChanged.RemoveListener(OnSurfaceSettingChanged);
            oceanShadingToggle.onValueChanged.RemoveListener(OnOceanAtmosphereToggled);
            atmosphereToggle.onValueChanged.RemoveListener(OnOceanAtmosphereToggled);
            cloudToggle.onValueChanged.RemoveListener(OnCloudToggled);
            cloudMotionToggle.onValueChanged.RemoveListener(OnCloudToggled);
            foreach (Slider slider in CloudSliders())
            {
                slider.onValueChanged.RemoveListener(OnCloudSettingChanged);
            }
            cloudSkyButton.onClick.RemoveListener(OnNewSkyClicked);
            cloudAutoSkyButton.onClick.RemoveListener(OnAutoSkyClicked);
            cloudDebugToggle.onValueChanged.RemoveListener(OnCloudDebugToggled);
            cloudViewButton.onClick.RemoveListener(OnCloudViewClicked);
            solarCycleToggle.onValueChanged.RemoveListener(OnSolarCycleToggled);
            solarPauseButton.onClick.RemoveListener(OnSolarPauseClicked);
            solarTimeSlider.onValueChanged.RemoveListener(OnSolarTimeChanged);
            solarDurationSlider.onValueChanged.RemoveListener(OnSolarDurationChanged);
            twilightSlider.onValueChanged.RemoveListener(OnTwilightChanged);
            solarResetButton.onClick.RemoveListener(OnSolarResetClicked);
            contemplateButton.onClick.RemoveListener(OnContemplateClicked);
            showControlsButton.onClick.RemoveListener(OnShowControlsClicked);
            foreach (Slider slider in OceanAtmosphereSliders())
            {
                slider.onValueChanged.RemoveListener(OnOceanAtmosphereChanged);
            }
            biomeToggle.onValueChanged.RemoveListener(OnBiomeToggled);
            biomeViewButton.onClick.RemoveListener(OnBiomeViewClicked);
            biomeAriditySlider.onValueChanged.RemoveListener(OnBiomeSettingChanged);
            biomeSharpnessSlider.onValueChanged.RemoveListener(OnBiomeSettingChanged);
            biomeAlpineSlider.onValueChanged.RemoveListener(OnBiomeSettingChanged);
            if (bootstrap != null)
            {
                bootstrap.PlanetGenerated -= OnPlanetGenerated;
            }
            if (subscribedVisual != null)
            {
                subscribedVisual.HydrologyUpdated -= OnHydrologyUpdated;
                subscribedVisual.RiversUpdated -= OnRiversUpdated;
                subscribedVisual.LakesUpdated -= OnLakesUpdated;
                subscribedVisual.ClimateUpdated -= OnClimateUpdated;
                subscribedVisual.BiomesUpdated -= OnBiomesUpdated;
                subscribedVisual = null;
            }
        }

        void Update()
        {
            UpdateContemplation();
            UpdateSolarDisplay();
            if (pendingRegenerateTime >= 0f && Time.unscaledTime >= pendingRegenerateTime)
            {
                pendingRegenerateTime = -1f;
                ApplyAndRegenerate();
            }
            if (pendingClimateTime >= 0f && Time.unscaledTime >= pendingClimateTime)
            {
                pendingClimateTime = -1f;
                ApplyClimateSettings();
            }
            if (pendingBiomeTime >= 0f && Time.unscaledTime >= pendingBiomeTime)
            {
                pendingBiomeTime = -1f;
                ApplyBiomeSettings();
            }
            if (pendingSurfaceTime >= 0f && Time.unscaledTime >= pendingSurfaceTime)
            {
                pendingSurfaceTime = -1f;
                ApplySurfaceSettings();
            }
            SyncHydrologyToggle();
        }

        void LateUpdate()
        {
            FitPanelToScreen();
        }

        void OnDestroy()
        {
            if (cloudLegendTexture != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(cloudLegendTexture);
                }
                else
                {
                    DestroyImmediate(cloudLegendTexture);
                }
                cloudLegendTexture = null;
            }
            if (biomeLegendTexture != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(biomeLegendTexture);
                }
                else
                {
                    DestroyImmediate(biomeLegendTexture);
                }
                biomeLegendTexture = null;
            }
            if (legendTexture != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(legendTexture);
                }
                else
                {
                    DestroyImmediate(legendTexture);
                }
                legendTexture = null;
            }
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
                subscribedVisual.ClimateUpdated -= OnClimateUpdated;
                subscribedVisual.BiomesUpdated -= OnBiomesUpdated;
            }
            subscribedVisual = visual;
            if (visual != null)
            {
                visual.HydrologyUpdated += OnHydrologyUpdated;
                visual.RiversUpdated += OnRiversUpdated;
                visual.LakesUpdated += OnLakesUpdated;
                visual.ClimateUpdated += OnClimateUpdated;
                visual.BiomesUpdated += OnBiomesUpdated;
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
                if (d.LakeInflows + d.LakeOutflows > 0)
                {
                    riverStatsText.text += string.Format(CultureInfo.InvariantCulture,
                        "\nLakes: shoreline transitions in {0} / out {1} · unresolved {2} · exposed ends {3} · {4:0} ms",
                        d.RiverToLakeTransitions, d.LakeToRiverTransitions, d.UnresolvedShorelineIntersections, d.ExposedTransitionEnds,
                        d.TransitionMilliseconds);
                }
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
            if (visual != null && visual.LakesVisible)
            {
                // Measured during the last water geometry / ribbon builds (no per-frame work).
                LakeMeshDiagnostics d = visual.LakeMeshes.LastDiagnostics;
                lakeStatsText.text += string.Format(CultureInfo.InvariantCulture,
                    "\nShore: {0:0.0} u in {1} segments · basin cuts {2} · not shown {3} · detached parts {4} · puddles dropped {5} · "
                    + "degenerate {6} · warnings {7} · cracks {8} (seams {9}) · level dev {10:0.0e0}",
                    d.ShorelineLength, d.ShorelineSegments, d.BasinCutSegments, d.UnrepresentedLakes, d.DetachedFragments,
                    d.DroppedPuddles, d.DegenerateTrianglesSkipped, d.TerrainIntersectionWarnings, d.CrackEdges, d.SeamMismatches,
                    d.MaxLevelDeviation);
            }
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

        // ------------------------------------------------------------------ Climate

        void OnClimateToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // The climate is computed the first time a climate view is shown, then cached.
                visual.DebugView = show ? ClimateViews[selectedClimateView] : PlanetDebugView.None;
            }
            UpdateClimateLegend();
        }

        void OnClimateViewClicked()
        {
            selectedClimateView = (selectedClimateView + 1) % ClimateViews.Length;
            climateViewLabel.text = ClimateViewNames[selectedClimateView];
            if (climateToggle.isOn)
            {
                // Views only recolor: the climate is not recomputed.
                OnClimateToggled(true);
            }
            UpdateClimateLegend();
        }

        void OnClimateSettingChanged(float _)
        {
            if (syncing)
            {
                return;
            }
            UpdateClimateLabels();
            // Applied after a short delay (a recomputation per slider step would stall dragging).
            pendingClimateTime = Time.unscaledTime + sliderRegenerateDelay;
        }

        void ApplyClimateSettings()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            visual.Climate.resolution = ClimateResolutions[Mathf.RoundToInt(climateResolutionSlider.value)];
            visual.Climate.axialTilt = axialTiltSlider.value;
            visual.Climate.elevationScale = climateElevationSlider.value;
        }

        void OnClimateUpdated(ClimateResult result)
        {
            ClimateDiagnostics d = result.Diagnostics;
            climateStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "T {0:0} / {1:0.0} / {2:0} C (min / mean / max) · land {3:0.0} · ocean {4:0.0} · below 0 C {5:0}% of area\n"
                + "Rain land x{6:0.00} / ocean x{7:0.00} · {8} iterations{9} · balance err {10:0.0e0} · {11:0} ms (T {12:0}, moisture {13:0})",
                d.MinTemperature, d.MeanTemperature, d.MaxTemperature, d.LandMeanTemperature, d.OceanMeanTemperature, d.FrozenFraction * 100f,
                d.LandMeanPrecipitation, d.OceanMeanPrecipitation, d.Iterations, d.Converged ? "" : " (not converged)", d.MassBalanceError,
                d.TotalMilliseconds, d.TemperatureMilliseconds, d.MoistureMilliseconds);
        }

        void UpdateClimateLegend()
        {
            PlanetVisualController visual = Visual;
            bool shown = visual != null && PlanetDebugViews.IsClimate(visual.DebugView);
            if (!shown)
            {
                HideLegend(climateLegendText, "Climate: computed when shown", climateLegendBar, climateLegendTicks, climateLegendNote);
                return;
            }
            ApplyLegend(ClimateDebugColors.GetLegend(visual.DebugView, visual.Climate), climateLegendText, climateLegendBar, climateLegendTicks,
                climateLegendNote, ref legendTexture);
        }

        /// <summary>Shows a color-bar legend: title, gradient, ticks at their data positions, note.</summary>
        static void ApplyLegend(ClimateDebugColors.Legend legend, Text title, RawImage bar, RectTransform ticks, Text note, ref Texture2D texture)
        {
            title.text = legend.Title;
            note.text = legend.Note;
            if (texture == null)
            {
                texture = new Texture2D(legend.Gradient.Length, 1, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                    name = "Legend",
                };
            }
            texture.SetPixels(legend.Gradient);
            texture.Apply(false);
            bar.texture = texture;
            bar.gameObject.SetActive(true);
            ticks.gameObject.SetActive(true);

            // Tick labels placed at their data position along the bar (log scales included).
            Font font = title.font;
            for (int i = 0; i < legend.TickLabels.Length; i++)
            {
                Text label;
                if (i < ticks.childCount)
                {
                    label = ticks.GetChild(i).GetComponent<Text>();
                }
                else
                {
                    label = CreateRect("Tick", ticks).gameObject.AddComponent<Text>();
                    label.font = font;
                    label.fontSize = 10;
                    label.color = MutedTextColor;
                    label.raycastTarget = false;
                    label.horizontalOverflow = HorizontalWrapMode.Overflow;
                }
                label.gameObject.SetActive(true);
                label.text = legend.TickLabels[i];
                float x = legend.TickPositions[i];
                var rect = (RectTransform)label.transform;
                rect.anchorMin = new Vector2(x, 0f);
                rect.anchorMax = new Vector2(x, 1f);
                float pivot = x < 0.02f ? 0f : x > 0.98f ? 1f : 0.5f;
                rect.pivot = new Vector2(pivot, 0.5f);
                rect.sizeDelta = new Vector2(60f, 0f);
                rect.anchoredPosition = Vector2.zero;
                label.alignment = pivot == 0f ? TextAnchor.MiddleLeft : pivot == 1f ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
            }
            for (int i = legend.TickLabels.Length; i < ticks.childCount; i++)
            {
                ticks.GetChild(i).gameObject.SetActive(false);
            }
        }

        static void HideLegend(Text title, string placeholder, RawImage bar, RectTransform ticks, Text note)
        {
            title.text = placeholder;
            bar.gameObject.SetActive(false);
            ticks.gameObject.SetActive(false);
            note.text = "";
        }

        // ------------------------------------------------------------------ Surface (Natural Planet)

        void OnNaturalToggled(bool natural)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // Artistic only: recolors the default view (Natural Planet or classic elevation palette).
                visual.Surface.enabled = natural;
            }
        }

        void OnSurfaceSettingChanged(float _)
        {
            if (syncing)
            {
                return;
            }
            UpdateSurfaceLabels();
            pendingSurfaceTime = Time.unscaledTime + sliderRegenerateDelay;
        }

        void ApplySurfaceSettings()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            visual.Surface.biomeBlend = surfaceBlendSlider.value;
            visual.Surface.regionalVariation = surfaceVariationSlider.value;
            visual.Surface.rockExposure = surfaceRockSlider.value;
            visual.Surface.saturation = surfaceSaturationSlider.value;
        }

        void SyncSurfaceControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Surface.Sanitize();
            naturalToggle.SetIsOnWithoutNotify(visual.Surface.enabled);
            surfaceBlendSlider.SetValueWithoutNotify(visual.Surface.biomeBlend);
            surfaceVariationSlider.SetValueWithoutNotify(visual.Surface.regionalVariation);
            surfaceRockSlider.SetValueWithoutNotify(visual.Surface.rockExposure);
            surfaceSaturationSlider.SetValueWithoutNotify(visual.Surface.saturation);
            UpdateSurfaceLabels();
            syncing = false;
        }

        void UpdateSurfaceLabels()
        {
            surfaceBlendValue.text = (surfaceBlendSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            surfaceVariationValue.text = (surfaceVariationSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            surfaceRockValue.text = (surfaceRockSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            surfaceSaturationValue.text = surfaceSaturationSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }

        // ------------------------------------------------------------------ Oceans & atmosphere

        Slider[] OceanAtmosphereSliders()
        {
            return new[] { oceanContrastSlider, oceanShelfSlider, oceanRoughnessSlider, oceanSpecularSlider,
                atmosphereStrengthSlider, atmosphereThicknessSlider, terminatorSlider };
        }

        void OnOceanAtmosphereToggled(bool _)
        {
            OnOceanAtmosphereChanged(0f);
        }

        /// <summary>Rendering only and cheap (material properties): applied immediately, no regeneration.</summary>
        void OnOceanAtmosphereChanged(float _)
        {
            PlanetVisualController visual = Visual;
            if (syncing || visual == null)
            {
                return;
            }
            OceanAtmosphereSettings s = visual.OceanAtmosphere;
            s.oceanShading = oceanShadingToggle.isOn;
            s.atmosphere = atmosphereToggle.isOn;
            s.depthContrast = oceanContrastSlider.value;
            s.shelfVisibility = oceanShelfSlider.value;
            s.oceanRoughness = oceanRoughnessSlider.value;
            s.specularIntensity = oceanSpecularSlider.value;
            s.atmosphereStrength = atmosphereStrengthSlider.value;
            s.atmosphereThickness = atmosphereThicknessSlider.value;
            s.terminatorSoftness = terminatorSlider.value;
            UpdateOceanAtmosphereLabels();
        }

        void SyncOceanAtmosphereControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            OceanAtmosphereSettings s = visual.OceanAtmosphere;
            s.Sanitize();
            oceanShadingToggle.SetIsOnWithoutNotify(s.oceanShading);
            atmosphereToggle.SetIsOnWithoutNotify(s.atmosphere);
            oceanContrastSlider.SetValueWithoutNotify(s.depthContrast);
            oceanShelfSlider.SetValueWithoutNotify(s.shelfVisibility);
            oceanRoughnessSlider.SetValueWithoutNotify(s.oceanRoughness);
            oceanSpecularSlider.SetValueWithoutNotify(s.specularIntensity);
            atmosphereStrengthSlider.SetValueWithoutNotify(s.atmosphereStrength);
            atmosphereThicknessSlider.SetValueWithoutNotify(s.atmosphereThickness);
            terminatorSlider.SetValueWithoutNotify(s.terminatorSoftness);
            UpdateOceanAtmosphereLabels();
            syncing = false;
        }

        void UpdateOceanAtmosphereLabels()
        {
            oceanContrastValue.text = (oceanContrastSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            oceanShelfValue.text = (oceanShelfSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            oceanRoughnessValue.text = oceanRoughnessSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
            oceanSpecularValue.text = (oceanSpecularSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            atmosphereStrengthValue.text = atmosphereStrengthSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            atmosphereThicknessValue.text = (atmosphereThicknessSlider.value * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "% R";
            terminatorValue.text = (terminatorSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // ------------------------------------------------------------------ Clouds

        Slider[] CloudSliders()
        {
            return new[] { cloudCoverageSlider, cloudDensitySlider, cloudDetailSlider, cloudWeatherSlider, cloudConvectionSlider,
                cloudShadowSlider, cloudAltitudeSlider };
        }

        void OnCloudToggled(bool _)
        {
            OnCloudSettingChanged(0f);
        }

        /// <summary>Rendering only (material properties; the coverage threshold is a cheap histogram): applied immediately.</summary>
        void OnCloudSettingChanged(float _)
        {
            PlanetVisualController visual = Visual;
            if (syncing || visual == null)
            {
                return;
            }
            CloudSettings s = visual.Clouds;
            s.showClouds = cloudToggle.isOn;
            s.motion = cloudMotionToggle.isOn;
            s.coverage = cloudCoverageSlider.value;
            s.density = cloudDensitySlider.value;
            s.detail = cloudDetailSlider.value;
            s.weatherStrength = cloudWeatherSlider.value;
            s.convection = cloudConvectionSlider.value;
            s.shadowStrength = cloudShadowSlider.value;
            s.altitude = cloudAltitudeSlider.value;
            UpdateCloudLabels();
        }

        /// <summary>Another sky over the same geography: a new weather seed (clouds only).</summary>
        void OnNewSkyClicked()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            int seed;
            do
            {
                seed = UnityEngine.Random.Range(1, 1000000);
            }
            while (seed == visual.Clouds.weatherSeed);
            visual.Clouds.weatherSeed = seed;
            UpdateCloudLabels();
            UpdateCloudLegend();
        }

        void OnAutoSkyClicked()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            visual.Clouds.weatherSeed = 0;
            UpdateCloudLabels();
            UpdateCloudLegend();
        }

        void OnCloudDebugToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // Cloud data on the terrain (unlit, ocean, clouds and atmosphere hidden); never computes the climate.
                visual.DebugView = show ? CloudViews[selectedCloudView] : PlanetDebugView.None;
            }
            UpdateCloudLegend();
        }

        void OnCloudViewClicked()
        {
            selectedCloudView = (selectedCloudView + 1) % CloudViews.Length;
            cloudViewLabel.text = CloudViewNames[selectedCloudView];
            if (cloudDebugToggle.isOn)
            {
                OnCloudDebugToggled(true);
            }
            UpdateCloudLegend();
        }

        void UpdateCloudLegend()
        {
            PlanetVisualController visual = Visual;
            if (visual == null || !PlanetDebugViews.IsCloud(visual.DebugView))
            {
                HideLegend(cloudLegendText, "Cloud data: shown on demand", cloudLegendBar, cloudLegendTicks, cloudLegendNote);
                return;
            }
            CloudField field = visual.CurrentCloudField;
            CloudDebugData data = field == null ? null : new CloudDebugData(field, visual.Clouds, visual.CloudThreshold);
            ApplyLegend(CloudDebugColors.GetLegend(visual.DebugView, data), cloudLegendText, cloudLegendBar, cloudLegendTicks, cloudLegendNote,
                ref cloudLegendTexture);
        }

        void SyncCloudControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            CloudSettings s = visual.Clouds;
            s.Sanitize();
            cloudToggle.SetIsOnWithoutNotify(s.showClouds);
            cloudMotionToggle.SetIsOnWithoutNotify(s.motion);
            cloudCoverageSlider.SetValueWithoutNotify(s.coverage);
            cloudDensitySlider.SetValueWithoutNotify(s.density);
            cloudDetailSlider.SetValueWithoutNotify(s.detail);
            cloudWeatherSlider.SetValueWithoutNotify(s.weatherStrength);
            cloudConvectionSlider.SetValueWithoutNotify(s.convection);
            cloudShadowSlider.SetValueWithoutNotify(s.shadowStrength);
            cloudAltitudeSlider.SetValueWithoutNotify(s.altitude);
            cloudViewLabel.text = CloudViewNames[selectedCloudView];
            UpdateCloudLabels();
            syncing = false;
        }

        void UpdateCloudLabels()
        {
            cloudCoverageValue.text = (cloudCoverageSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudDensityValue.text = (cloudDensitySlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudDetailValue.text = (cloudDetailSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudWeatherValue.text = (cloudWeatherSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudConvectionValue.text = (cloudConvectionSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudShadowValue.text = (cloudShadowSlider.value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            cloudAltitudeValue.text = (cloudAltitudeSlider.value * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "% R";
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                cloudSkyLabel.text = visual.Clouds.weatherSeed == 0
                    ? "Weather seed: from planet"
                    : "Weather seed: " + visual.Clouds.weatherSeed.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------------ Lighting & time

        void OnSolarCycleToggled(bool on)
        {
            if (!syncing && Visual != null)
            {
                Visual.SetDayNightCycle(on);
                UpdateSolarLabels();
            }
        }

        void OnSolarPauseClicked()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            visual.SetSolarPaused(!visual.Solar.paused);
            UpdateSolarLabels();
        }

        /// <summary>Scrubber: light orientation only (immediate, nothing regenerated).</summary>
        void OnSolarTimeChanged(float value)
        {
            if (!syncing && Visual != null)
            {
                Visual.SetSolarTime(value);
                UpdateSolarLabels();
            }
        }

        void OnSolarDurationChanged(float value)
        {
            if (!syncing && Visual != null)
            {
                Visual.SetCycleDuration(value);
                UpdateSolarLabels();
            }
        }

        void OnTwilightChanged(float value)
        {
            if (!syncing && Visual != null)
            {
                Visual.Solar.twilightWarmth = value;
                UpdateSolarLabels();
            }
        }

        void OnSolarResetClicked()
        {
            if (Visual != null)
            {
                Visual.ResetSun();
                SyncSolarControls();
            }
        }

        void SyncSolarControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            SolarSettings s = visual.Solar;
            s.Sanitize();
            solarCycleToggle.SetIsOnWithoutNotify(s.dayNightCycle);
            solarTimeSlider.SetValueWithoutNotify(s.solarTime);
            solarDurationSlider.SetValueWithoutNotify(s.cycleDuration);
            twilightSlider.SetValueWithoutNotify(s.twilightWarmth);
            UpdateSolarLabels();
            syncing = false;
        }

        int shownSolarMinute = -1;

        /// <summary>Follows a running solar clock (slider + label; the label string only changes once per minute of solar time).</summary>
        void UpdateSolarDisplay()
        {
            PlanetVisualController visual = Visual;
            if (visual == null || solarTimeSlider == null || !visual.Solar.dayNightCycle || visual.Solar.paused)
            {
                return;
            }
            solarTimeSlider.SetValueWithoutNotify(visual.Solar.solarTime);
            int minute = Mathf.FloorToInt(visual.Solar.solarTime * 1440f);
            if (minute != shownSolarMinute)
            {
                UpdateSolarLabels();
            }
        }

        void UpdateSolarLabels()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            SolarSettings s = visual.Solar;
            shownSolarMinute = Mathf.FloorToInt(s.solarTime * 1440f);
            solarTimeValue.text = SolarSettings.Format(s.solarTime);
            solarDurationValue.text = s.cycleDuration >= 60f
                ? (s.cycleDuration / 60f).ToString("0.0", CultureInfo.InvariantCulture) + " min"
                : s.cycleDuration.ToString("0", CultureInfo.InvariantCulture) + " s";
            twilightValue.text = (s.twilightWarmth * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            solarPauseLabel.text = s.paused ? "Resume" : "Pause";
            solarPauseButton.interactable = s.dayNightCycle;
        }

        // ------------------------------------------------------------------ Contemplation mode

        const float ContemplationHintSeconds = 5f;
        float contemplationHintUntil;

        /// <summary>True while the controls are hidden (contemplation mode).</summary>
        public bool Contemplating { get; private set; }

        /// <summary>
        /// Hides (or restores) the controls. Nothing is reset or regenerated; the planet, the camera, the
        /// clouds and the solar clock keep running. A small button, H and Esc always restore the controls.
        /// </summary>
        public void SetContemplation(bool on)
        {
            Contemplating = on;
            if (panelFrame != null)
            {
                panelFrame.gameObject.SetActive(!on);
            }
            if (showControlsButton != null)
            {
                showControlsButton.gameObject.SetActive(on);
            }
            if (contemplationHint != null)
            {
                contemplationHint.gameObject.SetActive(on);
                contemplationHint.canvasRenderer.SetAlpha(1f);
            }
            contemplationHintUntil = on ? Time.unscaledTime + ContemplationHintSeconds : 0f;
        }

        void OnContemplateClicked()
        {
            SetContemplation(true);
        }

        void OnShowControlsClicked()
        {
            SetContemplation(false);
        }

        void UpdateContemplation()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && !(seedField != null && seedField.isFocused))
            {
                if (keyboard.hKey.wasPressedThisFrame)
                {
                    SetContemplation(!Contemplating);
                }
                else if (Contemplating && keyboard.escapeKey.wasPressedThisFrame)
                {
                    SetContemplation(false);
                }
            }
            if (Contemplating && contemplationHint != null && contemplationHint.gameObject.activeSelf)
            {
                float remaining = contemplationHintUntil - Time.unscaledTime;
                contemplationHint.canvasRenderer.SetAlpha(Mathf.Clamp01(remaining));
                if (remaining <= 0f)
                {
                    contemplationHint.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Exit hint and the always-available "show controls" button (outside the panel frame).</summary>
        void BuildContemplationOverlay(Font font)
        {
            Transform old = transform.Find("Contemplation Overlay");
            if (old != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(old.gameObject);
                }
                else
                {
                    DestroyImmediate(old.gameObject);
                }
            }
            RectTransform overlay = CreateRect("Contemplation Overlay", transform);
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.offsetMin = overlay.offsetMax = Vector2.zero;

            contemplationHint = CreateLabel(overlay, font, "Contemplation mode - press H or Esc (or the button) to show the controls",
                14, FontStyle.Normal, TextColor, 24f);
            RectTransform hintRect = contemplationHint.rectTransform;
            hintRect.anchorMin = new Vector2(0.5f, 0f);
            hintRect.anchorMax = new Vector2(0.5f, 0f);
            hintRect.pivot = new Vector2(0.5f, 0f);
            hintRect.anchoredPosition = new Vector2(0f, 24f);
            hintRect.sizeDelta = new Vector2(620f, 24f);
            contemplationHint.alignment = TextAnchor.MiddleCenter;
            contemplationHint.raycastTarget = false;
            contemplationHint.gameObject.SetActive(false);

            showControlsButton = CreateButton(overlay, font, "Show controls (H)");
            var buttonRect = (RectTransform)showControlsButton.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0f, 1f);
            buttonRect.anchoredPosition = new Vector2(PanelMargin, -PanelMargin);
            buttonRect.sizeDelta = new Vector2(150f, 26f);
            var group = showControlsButton.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0.45f;
            showControlsButton.gameObject.SetActive(false);
        }

        /// <summary>The current rendering mode, always visible at the top of the panel.</summary>
        void UpdateDisplayMode(PlanetVisualController visual)
        {
            string mode = visual.DebugView != PlanetDebugView.None
                ? "Display: Scientific / Debug — " + visual.DebugView
                : visual.Surface.enabled ? "Display: Natural Planet" : "Display: Classic elevation colors";
            if (displayModeText.text != mode)
            {
                displayModeText.text = mode;
            }
            if (naturalToggle.isOn != visual.Surface.enabled)
            {
                naturalToggle.SetIsOnWithoutNotify(visual.Surface.enabled);
            }
        }

        // ------------------------------------------------------------------ Biomes

        void OnBiomeToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                // Biomes need the climate: both are computed the first time a biome view is shown, then cached.
                visual.DebugView = show ? BiomeViews[selectedBiomeView] : PlanetDebugView.None;
            }
            UpdateBiomeLegend();
        }

        void OnBiomeViewClicked()
        {
            selectedBiomeView = (selectedBiomeView + 1) % BiomeViews.Length;
            biomeViewLabel.text = BiomeViewNames[selectedBiomeView];
            if (biomeToggle.isOn)
            {
                OnBiomeToggled(true); // recolor only
            }
            UpdateBiomeLegend();
        }

        void OnBiomeSettingChanged(float _)
        {
            if (syncing)
            {
                return;
            }
            UpdateBiomeLabels();
            pendingBiomeTime = Time.unscaledTime + sliderRegenerateDelay;
        }

        void ApplyBiomeSettings()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            visual.Biomes.aridity = biomeAriditySlider.value;
            visual.Biomes.ecotoneSharpness = biomeSharpnessSlider.value;
            visual.Biomes.alpineSensitivity = biomeAlpineSlider.value;
        }

        void OnBiomesUpdated(BiomeResult result)
        {
            BiomeDiagnostics d = result.Diagnostics;
            // Top biomes by land area (compact; the full distribution is in the Planet inspector / logs).
            var order = new System.Collections.Generic.List<int>();
            for (int b = 1; b < Biomes.Count; b++)
            {
                order.Add(b);
            }
            order.Sort((x, y) => result.TerrestrialFraction[y].CompareTo(result.TerrestrialFraction[x]));
            var top = new System.Text.StringBuilder();
            for (int i = 0; i < 4 && i < order.Count && result.TerrestrialFraction[order[i]] > 0f; i++)
            {
                top.Append(i == 0 ? "" : " · ").Append(Biomes.Name((Biome)order[i])).Append(' ')
                    .Append((result.TerrestrialFraction[order[i]] * 100f).ToString("0", CultureInfo.InvariantCulture)).Append('%');
            }
            biomeStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "{0}\n{1} biomes · arid {2:0}% · drylands {3:0}% · forest {4:0}% · ecotones {5:0}% of land · {6} regions · {7:0} ms",
                top, d.BiomesRepresented, d.AridFraction * 100f, d.DrylandFraction * 100f, d.ForestFraction * 100f,
                d.EcotoneFraction * 100f, d.Regions, d.ClassificationMilliseconds);
        }

        void UpdateBiomeLegend()
        {
            PlanetVisualController visual = Visual;
            if (visual == null || !PlanetDebugViews.IsBiome(visual.DebugView))
            {
                HideLegend(biomeLegendText, "Biomes: computed when shown", biomeLegendBar, biomeLegendTicks, biomeLegendNote);
                return;
            }
            if (visual.DebugView == PlanetDebugView.BiomeMap)
            {
                HideLegend(biomeLegendText, "Dominant biome (Whittaker-style, annual climate)", biomeLegendBar, biomeLegendTicks, biomeLegendNote);
                biomeLegendNote.text = BiomeDebugColors.CategoryLegend();
                return;
            }
            ApplyLegend(BiomeDebugColors.GetLegend(visual.DebugView), biomeLegendText, biomeLegendBar, biomeLegendTicks, biomeLegendNote,
                ref biomeLegendTexture);
        }

        void SyncBiomeControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Biomes.Sanitize();
            biomeAriditySlider.SetValueWithoutNotify(visual.Biomes.aridity);
            biomeSharpnessSlider.SetValueWithoutNotify(visual.Biomes.ecotoneSharpness);
            biomeAlpineSlider.SetValueWithoutNotify(visual.Biomes.alpineSensitivity);
            biomeViewLabel.text = BiomeViewNames[selectedBiomeView];
            UpdateBiomeLabels();
            syncing = false;
        }

        void UpdateBiomeLabels()
        {
            biomeAridityValue.text = biomeAriditySlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            biomeSharpnessValue.text = biomeSharpnessSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            biomeAlpineValue.text = biomeAlpineSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }
        void SyncClimateControls()
        {
            PlanetVisualController visual = Visual;
            if (visual == null)
            {
                return;
            }
            syncing = true;
            visual.Climate.Sanitize();
            climateResolutionSlider.SetValueWithoutNotify(System.Array.IndexOf(ClimateResolutions, visual.Climate.resolution));
            axialTiltSlider.SetValueWithoutNotify(visual.Climate.axialTilt);
            climateElevationSlider.SetValueWithoutNotify(visual.Climate.elevationScale);
            climateViewLabel.text = ClimateViewNames[selectedClimateView];
            UpdateClimateLabels();
            syncing = false;
        }

        void UpdateClimateLabels()
        {
            climateResolutionValue.text = ClimateResolutions[Mathf.RoundToInt(climateResolutionSlider.value)].ToString(CultureInfo.InvariantCulture);
            axialTiltValue.text = axialTiltSlider.value.ToString("0.0", CultureInfo.InvariantCulture) + " deg";
            climateElevationValue.text = climateElevationSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + " km/%";
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
            bool climateShown = PlanetDebugViews.IsClimate(visual.DebugView);
            if (climateToggle.isOn != climateShown)
            {
                climateToggle.SetIsOnWithoutNotify(climateShown);
                UpdateClimateLegend();
            }
            UpdateDisplayMode(visual);
            bool cloudShown = PlanetDebugViews.IsCloud(visual.DebugView);
            if (cloudDebugToggle.isOn != cloudShown)
            {
                cloudDebugToggle.SetIsOnWithoutNotify(cloudShown);
                UpdateCloudLegend();
            }
            bool biomeShown = PlanetDebugViews.IsBiome(visual.DebugView);
            if (biomeToggle.isOn != biomeShown)
            {
                biomeToggle.SetIsOnWithoutNotify(biomeShown);
                UpdateBiomeLegend();
            }
            int biomeView = System.Array.IndexOf(BiomeViews, visual.DebugView);
            if (biomeView >= 0 && biomeView != selectedBiomeView)
            {
                selectedBiomeView = biomeView;
                biomeViewLabel.text = BiomeViewNames[selectedBiomeView];
                UpdateBiomeLegend();
            }
            int climateView = System.Array.IndexOf(ClimateViews, visual.DebugView);
            if (climateView >= 0 && climateView != selectedClimateView)
            {
                selectedClimateView = climateView;
                climateViewLabel.text = ClimateViewNames[selectedClimateView];
                UpdateClimateLegend();
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
            SyncClimateControls();
            SyncBiomeControls();
            SyncSurfaceControls();
            SyncOceanAtmosphereControls();
            SyncCloudControls();
            SyncSolarControls();
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
            BuildContemplationOverlay(font);
            // Frame: as tall as the content, at most the screen height (see FitPanelToScreen); the
            // content scrolls inside it (mouse wheel, drag or the scrollbar).
            panelFrame = CreateRect(PanelObjectName, transform);
            panelFrame.anchorMin = panelFrame.anchorMax = panelFrame.pivot = new Vector2(0f, 1f);
            panelFrame.anchoredPosition = new Vector2(PanelMargin, -PanelMargin);
            panelFrame.sizeDelta = new Vector2(PanelWidth, 400f);
            panelFrame.gameObject.AddComponent<Image>().color = PanelColor;
            panelFrame.gameObject.AddComponent<RectMask2D>();

            var panel = CreateRect("Content", panelFrame);
            panel.anchorMin = new Vector2(0f, 1f);
            panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = Vector2.zero;
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 22, 14, 14);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            panelContent = panel;

            panelScroll = panelFrame.gameObject.AddComponent<ScrollRect>();
            panelScroll.content = panel;
            panelScroll.viewport = panelFrame;
            panelScroll.horizontal = false;
            panelScroll.vertical = true;
            panelScroll.movementType = ScrollRect.MovementType.Clamped;
            panelScroll.scrollSensitivity = 30f;
            panelScroll.verticalScrollbar = CreateScrollbar(panelFrame);
            panelScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            CreateLabel(panel, font, "PLANETWALKER", 26, FontStyle.Bold, TextColor, 32f);
            CreateLabel(panel, font, "GENESIS V0.1.2D", 14, FontStyle.Normal, AccentColor, 20f);
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
            CreateLabel(panel, font, "SURFACE", 13, FontStyle.Bold, AccentColor, 18f);
            displayModeText = CreateLabel(panel, font, "Display: Natural Planet", 13, FontStyle.Bold, TextColor, 18f);
            var naturalRow = CreateRow(panel, "Natural Row");
            naturalToggle = CreateToggle(naturalRow, font, "Natural Surface");
            naturalToggle.isOn = true;
            surfaceBlendSlider = CreateSliderRow(panel, font, "Biome Blend", 0f, 1f, false, out surfaceBlendValue);
            surfaceVariationSlider = CreateSliderRow(panel, font, "Regional Variation", 0f, 1f, false, out surfaceVariationValue);
            surfaceRockSlider = CreateSliderRow(panel, font, "Rock Exposure", 0f, 1f, false, out surfaceRockValue);
            surfaceSaturationSlider = CreateSliderRow(panel, font, "Color Saturation", 0.5f, 1.3f, false, out surfaceSaturationValue);

            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "OCEANS & ATMOSPHERE", 13, FontStyle.Bold, AccentColor, 18f);
            var oceanRow = CreateRow(panel, "Ocean Atmosphere Row");
            oceanShadingToggle = CreateToggle(oceanRow, font, "Ocean Shading");
            oceanShadingToggle.isOn = true;
            atmosphereToggle = CreateToggle(oceanRow, font, "Atmosphere");
            atmosphereToggle.isOn = true;
            oceanContrastSlider = CreateSliderRow(panel, font, "Ocean Depth Contrast", 0f, 1f, false, out oceanContrastValue);
            oceanShelfSlider = CreateSliderRow(panel, font, "Coastal Shelf Visibility", 0f, 1f, false, out oceanShelfValue);
            oceanRoughnessSlider = CreateSliderRow(panel, font, "Ocean Roughness", OceanAtmosphereSettings.MinRoughness,
                OceanAtmosphereSettings.MaxRoughness, false, out oceanRoughnessValue);
            oceanSpecularSlider = CreateSliderRow(panel, font, "Specular Intensity", 0f, 1f, false, out oceanSpecularValue);
            atmosphereStrengthSlider = CreateSliderRow(panel, font, "Atmosphere Strength", 0f, 2f, false, out atmosphereStrengthValue);
            atmosphereThicknessSlider = CreateSliderRow(panel, font, "Atmosphere Thickness", OceanAtmosphereSettings.MinThickness,
                OceanAtmosphereSettings.MaxThickness, false, out atmosphereThicknessValue);
            terminatorSlider = CreateSliderRow(panel, font, "Terminator Softness", 0f, 1f, false, out terminatorValue);

            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "LIGHTING & TIME", 13, FontStyle.Bold, AccentColor, 18f);
            var solarRow = CreateRow(panel, "Solar Row");
            solarCycleToggle = CreateToggle(solarRow, font, "Day/Night Cycle");
            solarCycleToggle.isOn = false;
            solarPauseButton = CreateButton(solarRow, font, "Pause");
            solarPauseLabel = solarPauseButton.GetComponentInChildren<Text>();
            solarTimeSlider = CreateSliderRow(panel, font, "Solar Time", 0f, 0.999f, false, out solarTimeValue);
            solarDurationSlider = CreateSliderRow(panel, font, "Cycle Duration", SolarSettings.MinCycleDuration, SolarSettings.MaxCycleDuration, false, out solarDurationValue);
            twilightSlider = CreateSliderRow(panel, font, "Twilight Warmth", 0f, 1f, false, out twilightValue);
            var solarButtons = CreateRow(panel, "Solar Buttons");
            solarResetButton = CreateButton(solarButtons, font, "Reset Sun");
            contemplateButton = CreateButton(solarButtons, font, "Contemplate (H)");

            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "CLOUDS", 13, FontStyle.Bold, AccentColor, 18f);
            var cloudRow = CreateRow(panel, "Cloud Row");
            cloudToggle = CreateToggle(cloudRow, font, "Show Clouds");
            cloudToggle.isOn = true;
            cloudMotionToggle = CreateToggle(cloudRow, font, "Cloud Motion");
            cloudMotionToggle.isOn = false;
            var skyRow = CreateRow(panel, "Sky Row");
            cloudSkyButton = CreateButton(skyRow, font, "New Sky");
            cloudAutoSkyButton = CreateButton(skyRow, font, "Planet Sky");
            cloudSkyLabel = CreateLabel(panel, font, "Weather seed: from planet", 11, FontStyle.Normal, MutedTextColor, 16f);
            cloudCoverageSlider = CreateSliderRow(panel, font, "Cloud Coverage", 0f, 1f, false, out cloudCoverageValue);
            cloudDensitySlider = CreateSliderRow(panel, font, "Cloud Density", 0f, 1f, false, out cloudDensityValue);
            cloudDetailSlider = CreateSliderRow(panel, font, "Cloud Detail", 0f, 1f, false, out cloudDetailValue);
            cloudWeatherSlider = CreateSliderRow(panel, font, "Weather Systems", 0f, 1f, false, out cloudWeatherValue);
            cloudConvectionSlider = CreateSliderRow(panel, font, "Convection", 0f, 1f, false, out cloudConvectionValue);
            cloudShadowSlider = CreateSliderRow(panel, font, "Cloud Shadows", 0f, 1f, false, out cloudShadowValue);
            cloudAltitudeSlider = CreateSliderRow(panel, font, "Cloud Altitude", CloudSettings.MinAltitude, CloudSettings.MaxAltitude, false, out cloudAltitudeValue);
            var cloudDebugRow = CreateRow(panel, "Cloud Debug Row");
            cloudDebugToggle = CreateToggle(cloudDebugRow, font, "Show Cloud Data");
            cloudViewButton = CreateButton(cloudDebugRow, font, CloudViewNames[0]);
            cloudViewLabel = cloudViewButton.GetComponentInChildren<Text>();
            cloudViewLabel.fontSize = FontSize - 3;
            cloudLegendText = CreateLabel(panel, font, "Cloud data: shown on demand", 12, FontStyle.Bold, TextColor, 16f);
            var cloudBar = CreateRect("Cloud Legend Bar", panel);
            cloudLegendBar = cloudBar.gameObject.AddComponent<RawImage>();
            cloudLegendBar.raycastTarget = false;
            cloudBar.gameObject.AddComponent<LayoutElement>().preferredHeight = 10f;
            cloudLegendTicks = CreateRect("Cloud Legend Ticks", panel);
            cloudLegendTicks.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;
            cloudLegendNote = CreateLabel(panel, font, "", 10, FontStyle.Normal, MutedTextColor, 40f);
            cloudLegendNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            cloudLegendNote.supportRichText = true;
            cloudBar.gameObject.SetActive(false);
            cloudLegendTicks.gameObject.SetActive(false);

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

            CreateLabel(panel, font, "CLIMATE", 13, FontStyle.Bold, AccentColor, 18f);
            var climateRow = CreateRow(panel, "Climate Row");
            climateToggle = CreateToggle(climateRow, font, "Show Climate");
            climateViewButton = CreateButton(climateRow, font, ClimateViewNames[0]);
            climateViewLabel = climateViewButton.GetComponentInChildren<Text>();
            climateViewLabel.fontSize = FontSize - 3;
            climateResolutionSlider = CreateSliderRow(panel, font, "Climate Resolution", 0f, ClimateResolutions.Length - 1, true, out climateResolutionValue);
            axialTiltSlider = CreateSliderRow(panel, font, "Axial Tilt", 0f, ClimateSettings.MaxAxialTilt, false, out axialTiltValue);
            climateElevationSlider = CreateSliderRow(panel, font, "Climate Elevation Scale", ClimateSettings.MinElevationScale,
                ClimateSettings.MaxElevationScale, false, out climateElevationValue);
            climateLegendText = CreateLabel(panel, font, "Climate: computed when shown", 12, FontStyle.Bold, TextColor, 16f);
            var bar = CreateRect("Climate Legend Bar", panel);
            climateLegendBar = bar.gameObject.AddComponent<RawImage>();
            climateLegendBar.raycastTarget = false;
            bar.gameObject.AddComponent<LayoutElement>().preferredHeight = 10f;
            climateLegendTicks = CreateRect("Climate Legend Ticks", panel);
            climateLegendTicks.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;
            climateLegendNote = CreateLabel(panel, font, "", 10, FontStyle.Italic, MutedTextColor, 26f);
            climateLegendNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            bar.gameObject.SetActive(false);
            climateLegendTicks.gameObject.SetActive(false);
            climateStatsText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 30f);

            CreateLabel(panel, font, "BIOMES", 13, FontStyle.Bold, AccentColor, 18f);
            var biomeRow = CreateRow(panel, "Biome Row");
            biomeToggle = CreateToggle(biomeRow, font, "Show Biomes");
            biomeViewButton = CreateButton(biomeRow, font, BiomeViewNames[0]);
            biomeViewLabel = biomeViewButton.GetComponentInChildren<Text>();
            biomeViewLabel.fontSize = FontSize - 3;
            biomeAriditySlider = CreateSliderRow(panel, font, "Aridity Sensitivity", 0.5f, 2f, false, out biomeAridityValue);
            biomeSharpnessSlider = CreateSliderRow(panel, font, "Ecotone Sharpness", 0.5f, 2f, false, out biomeSharpnessValue);
            biomeAlpineSlider = CreateSliderRow(panel, font, "Alpine Sensitivity", 0.5f, 2f, false, out biomeAlpineValue);
            biomeLegendText = CreateLabel(panel, font, "Biomes: computed when shown", 12, FontStyle.Bold, TextColor, 16f);
            var biomeBar = CreateRect("Biome Legend Bar", panel);
            biomeLegendBar = biomeBar.gameObject.AddComponent<RawImage>();
            biomeLegendBar.raycastTarget = false;
            biomeBar.gameObject.AddComponent<LayoutElement>().preferredHeight = 10f;
            biomeLegendTicks = CreateRect("Biome Legend Ticks", panel);
            biomeLegendTicks.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;
            biomeLegendNote = CreateLabel(panel, font, "", 10, FontStyle.Normal, MutedTextColor, 52f);
            biomeLegendNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            biomeLegendNote.supportRichText = true;
            biomeBar.gameObject.SetActive(false);
            biomeLegendTicks.gameObject.SetActive(false);
            biomeStatsText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 30f);

            CreateSpacer(panel, 4f);
            statsText = CreateLabel(panel, font, "", 13, FontStyle.Normal, MutedTextColor, 54f);
            CreateLabel(panel, font, "LMB drag: rotate   ·   Wheel: zoom   ·   R: reset", 12, FontStyle.Italic, MutedTextColor, 18f);
        }

        /// <summary>Thin vertical scrollbar along the right edge of the panel frame.</summary>
        static Scrollbar CreateScrollbar(RectTransform frame)
        {
            var rect = CreateRect("Scrollbar", frame);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(6f, -12f);
            rect.anchoredPosition = new Vector2(-4f, 0f);
            rect.gameObject.AddComponent<Image>().color = ControlColor;
            var area = CreateRect("Sliding Area", rect);
            Stretch(area, 0f, 0f);
            var handle = CreateRect("Handle", area);
            Stretch(handle, 0f, 0f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = AccentColor;
            var scrollbar = rect.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            return scrollbar;
        }

        /// <summary>Panel frame height = content height, at most the canvas height minus the margins.</summary>
        void FitPanelToScreen()
        {
            if (panelFrame == null || panelContent == null)
            {
                return;
            }
            var canvas = (RectTransform)transform;
            float available = Mathf.Max(120f, canvas.rect.height - 2f * PanelMargin);
            float height = Mathf.Min(panelContent.rect.height, available);
            if (!Mathf.Approximately(panelFrame.sizeDelta.y, height))
            {
                panelFrame.sizeDelta = new Vector2(PanelWidth, height);
            }
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
