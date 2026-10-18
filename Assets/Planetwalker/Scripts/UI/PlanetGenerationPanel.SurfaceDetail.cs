using System.Globalization;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// SURFACE DETAIL section (V0.2.0C): the multi-scale terrain detail of the planet definition (a terrain setting,
    /// archived with the world, applied by regeneration like the GENERATION sliders) and its diagnostic views
    /// (which layer made this landform; a display setting like the other scientific views).
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        static readonly PlanetDebugView[] SurfaceDetailViews =
        {
            PlanetDebugView.SurfaceDetailCombined, PlanetDebugView.SurfaceDetailRegional, PlanetDebugView.SurfaceDetailLocal,
            PlanetDebugView.SurfaceDetailMicro, PlanetDebugView.LandformInfluence, PlanetDebugView.LandformDisplacement,
            PlanetDebugView.LandformRelief,
        };
        static readonly string[] SurfaceDetailViewNames =
        {
            "View: Combined", "View: Regional", "View: Local", "View: Micro", "View: Landforms", "View: Landform Shift", "View: Relief (clay)",
        };

        [SerializeField] Toggle surfaceDetailToggle;
        [SerializeField] Slider regionalDetailSlider;
        [SerializeField] Text regionalDetailValue;
        [SerializeField] Slider localDetailSlider;
        [SerializeField] Text localDetailValue;
        [SerializeField] Slider microDetailSlider;
        [SerializeField] Text microDetailValue;
        [SerializeField] Slider landformSlider;
        [SerializeField] Text landformValue;
        [SerializeField] Toggle surfaceDetailViewToggle;
        [SerializeField] Button surfaceDetailViewButton;
        [SerializeField] Text surfaceDetailViewLabel;
        [SerializeField] Text surfaceDetailStatsText;

        int selectedSurfaceDetailView;

        bool SurfaceDetailLayoutComplete()
        {
            return surfaceDetailToggle != null && regionalDetailSlider != null && localDetailSlider != null && microDetailSlider != null
                && landformSlider != null && surfaceDetailViewToggle != null && surfaceDetailViewButton != null && surfaceDetailStatsText != null;
        }

        void BuildSurfaceDetailSection(RectTransform panel, Font font)
        {
            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "SURFACE DETAIL", 13, FontStyle.Bold, AccentColor, 18f);
            var row = CreateRow(panel, "Surface Detail Row");
            surfaceDetailToggle = CreateToggle(row, font, "Enable Surface Detail");
            surfaceDetailToggle.isOn = true;
            regionalDetailSlider = CreateSliderRow(panel, font, "Regional Detail", 0f, PlanetDefinition.MaxSurfaceDetailLevel, false, out regionalDetailValue);
            localDetailSlider = CreateSliderRow(panel, font, "Local Detail", 0f, PlanetDefinition.MaxSurfaceDetailLevel, false, out localDetailValue);
            microDetailSlider = CreateSliderRow(panel, font, "Micro Detail", 0f, PlanetDefinition.MaxSurfaceDetailLevel, false, out microDetailValue);
            landformSlider = CreateSliderRow(panel, font, "Landforms", 0f, PlanetDefinition.MaxSurfaceDetailLevel, false, out landformValue);
            regionalDetailSlider.SetValueWithoutNotify(1f);
            localDetailSlider.SetValueWithoutNotify(1f);
            microDetailSlider.SetValueWithoutNotify(1f);
            landformSlider.SetValueWithoutNotify(1f);
            var viewRow = CreateRow(panel, "Surface Detail View Row");
            surfaceDetailViewToggle = CreateToggle(viewRow, font, "Show Detail Layers");
            surfaceDetailViewButton = CreateButton(viewRow, font, SurfaceDetailViewNames[0]);
            surfaceDetailViewLabel = surfaceDetailViewButton.GetComponentInChildren<Text>();
            surfaceDetailViewLabel.fontSize = FontSize - 3;
            surfaceDetailStatsText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 44f);
            surfaceDetailStatsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            UpdateSurfaceDetailValueLabels();
        }

        void EnableSurfaceDetail()
        {
            surfaceDetailToggle.onValueChanged.AddListener(OnSurfaceDetailToggled);
            regionalDetailSlider.onValueChanged.AddListener(OnSliderChanged);
            localDetailSlider.onValueChanged.AddListener(OnSliderChanged);
            microDetailSlider.onValueChanged.AddListener(OnSliderChanged);
            landformSlider.onValueChanged.AddListener(OnSliderChanged);
            surfaceDetailViewToggle.onValueChanged.AddListener(OnSurfaceDetailViewToggled);
            surfaceDetailViewButton.onClick.AddListener(OnSurfaceDetailViewClicked);
        }

        void DisableSurfaceDetail()
        {
            surfaceDetailToggle.onValueChanged.RemoveListener(OnSurfaceDetailToggled);
            regionalDetailSlider.onValueChanged.RemoveListener(OnSliderChanged);
            localDetailSlider.onValueChanged.RemoveListener(OnSliderChanged);
            microDetailSlider.onValueChanged.RemoveListener(OnSliderChanged);
            landformSlider.onValueChanged.RemoveListener(OnSliderChanged);
            surfaceDetailViewToggle.onValueChanged.RemoveListener(OnSurfaceDetailViewToggled);
            surfaceDetailViewButton.onClick.RemoveListener(OnSurfaceDetailViewClicked);
        }

        /// <summary>A terrain setting: regenerates (debounced like the sliders).</summary>
        void OnSurfaceDetailToggled(bool _)
        {
            if (syncing)
            {
                return;
            }
            pendingRegenerateTime = Time.unscaledTime + sliderRegenerateDelay;
        }

        void ApplySurfaceDetailControlsToDefinition(PlanetDefinition definition)
        {
            definition.surfaceDetail = surfaceDetailToggle.isOn;
            definition.regionalDetail = regionalDetailSlider.value;
            definition.localDetail = localDetailSlider.value;
            definition.microDetail = microDetailSlider.value;
            definition.regionalLandforms = landformSlider.value;
        }

        void SyncSurfaceDetailFromDefinition(PlanetDefinition definition)
        {
            surfaceDetailToggle.SetIsOnWithoutNotify(definition.surfaceDetail);
            regionalDetailSlider.SetValueWithoutNotify(definition.regionalDetail);
            localDetailSlider.SetValueWithoutNotify(definition.localDetail);
            microDetailSlider.SetValueWithoutNotify(definition.microDetail);
            landformSlider.SetValueWithoutNotify(definition.regionalLandforms);
            UpdateSurfaceDetailValueLabels();
        }

        void UpdateSurfaceDetailValueLabels()
        {
            regionalDetailValue.text = regionalDetailSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            localDetailValue.text = localDetailSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            microDetailValue.text = microDetailSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            landformValue.text = landformSlider.value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }

        void OnSurfaceDetailViewToggled(bool show)
        {
            PlanetVisualController visual = Visual;
            if (visual != null)
            {
                visual.DebugView = show ? SurfaceDetailViews[selectedSurfaceDetailView] : PlanetDebugView.None;
            }
        }

        void OnSurfaceDetailViewClicked()
        {
            selectedSurfaceDetailView = (selectedSurfaceDetailView + 1) % SurfaceDetailViews.Length;
            surfaceDetailViewLabel.text = SurfaceDetailViewNames[selectedSurfaceDetailView];
            if (surfaceDetailViewToggle.isOn)
            {
                OnSurfaceDetailViewToggled(true);
            }
        }

        /// <summary>Keeps the view toggle in step with the visual's view (other sections switch views too).</summary>
        void SyncSurfaceDetailToggle(PlanetVisualController visual)
        {
            bool shown = PlanetDebugViews.IsSurfaceDetail(visual.DebugView);
            if (surfaceDetailViewToggle.isOn != shown)
            {
                surfaceDetailViewToggle.SetIsOnWithoutNotify(shown);
            }
            int view = System.Array.IndexOf(SurfaceDetailViews, visual.DebugView);
            if (view >= 0 && view != selectedSurfaceDetailView)
            {
                selectedSurfaceDetailView = view;
                surfaceDetailViewLabel.text = SurfaceDetailViewNames[view];
            }
        }

        /// <summary>Stats of the generated planet's detail: bands, octaves, physical sizes.</summary>
        void UpdateSurfaceDetailStats(PlanetInstance planet)
        {
            if (surfaceDetailStatsText == null || planet == null)
            {
                return;
            }
            TerrainDetail detail = planet.Height.SurfaceDetail;
            if (detail == null)
            {
                surfaceDetailStatsText.text = "Surface detail off: planetary terrain only (V0.2.0B)";
                return;
            }
            TerrainDetailProfile p = detail.Profile;
            PlanetScale scale = planet.Scale;
            int regional = 0, local = 0, micro = 0;
            for (int k = 0; k < p.OctaveCount; k++)
            {
                switch (p.Bands[k])
                {
                    case TerrainDetailBand.Regional: regional++; break;
                    case TerrainDetailBand.Local: local++; break;
                    default: micro++; break;
                }
            }
            surfaceDetailStatsText.text = string.Format(CultureInfo.InvariantCulture,
                "Radius {0} · {1} octaves, {2} to {3}\nRegional {4} oct. ±{5} · Local {6} oct. ±{7} · Micro {8} oct. ±{9}",
                PlanetScale.FormatDistance(scale.RadiusMeters), p.OctaveCount,
                PlanetScale.FormatDistance(p.OctaveCount > 0 ? p.WavelengthMeters(0) : 0), PlanetScale.FormatDistance(p.OctaveCount > 0 ? p.WavelengthMeters(p.OctaveCount - 1) : 0),
                regional, PlanetScale.FormatDistance(scale.ElevationToMeters(p.BandAmplitude[0])),
                local, PlanetScale.FormatDistance(scale.ElevationToMeters(p.BandAmplitude[1])),
                micro, PlanetScale.FormatDistance(scale.ElevationToMeters(p.BandAmplitude[2])))
                + (detail.Landforms != null
                    ? string.Format(CultureInfo.InvariantCulture, "\nLandforms {0:0.00}x: valleys to {1}, plateaus, coastal plains",
                        detail.Landforms.Strength, PlanetScale.FormatDistance(scale.ElevationToMeters(detail.Landforms.MaxValleyDepth)))
                    : "\nLandforms off (V0.2.0F terrain)");
        }
    }
}
