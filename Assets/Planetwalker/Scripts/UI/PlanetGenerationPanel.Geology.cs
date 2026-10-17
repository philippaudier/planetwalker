using System.Globalization;
using Planetwalker.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// SURFACE GEOLOGY section (V0.2.0E): geological appearance on / off, its strength and a debug view cycled by a
    /// button (Off by default). Presentation settings of the adaptive terrain: never archived, no rebuild, no
    /// regeneration. Without an adaptive renderer (Legacy) the controls have nothing to drive.
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        static readonly string[] GeologyViewNames = { "Off", "Identity", "Bedrock", "Sediment", "Erosion", "Reference Slope" };

        [SerializeField] Toggle geologyToggle;
        [SerializeField] Slider geologyStrengthSlider;
        [SerializeField] Text geologyStrengthValue;
        [SerializeField] Button geologyViewButton;
        [SerializeField] Text geologyViewLabel;

        bool GeologyLayoutComplete()
        {
            return geologyToggle != null && geologyStrengthSlider != null && geologyViewButton != null && geologyViewLabel != null;
        }

        void BuildGeologySection(RectTransform panel, Font font)
        {
            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "SURFACE GEOLOGY", 13, FontStyle.Bold, AccentColor, 18f);
            geologyToggle = CreateToggle(panel, font, "Geological Appearance");
            geologyToggle.SetIsOnWithoutNotify(true);
            geologyStrengthSlider = CreateSliderRow(panel, font, "Geological Detail", 0f, 1f, false, out geologyStrengthValue);
            geologyStrengthSlider.SetValueWithoutNotify(1f);
            geologyViewButton = CreateButton(panel, font, "Geology View: Off");
            geologyViewLabel = geologyViewButton.GetComponentInChildren<Text>();
            geologyViewLabel.fontSize = FontSize - 3;
            UpdateGeologyLabels();
        }

        void EnableGeology()
        {
            geologyToggle.onValueChanged.AddListener(OnGeologyToggle);
            geologyStrengthSlider.onValueChanged.AddListener(OnGeologyStrength);
            geologyViewButton.onClick.AddListener(OnGeologyView);
            SyncGeologyControls();
        }

        void DisableGeology()
        {
            geologyToggle.onValueChanged.RemoveListener(OnGeologyToggle);
            geologyStrengthSlider.onValueChanged.RemoveListener(OnGeologyStrength);
            geologyViewButton.onClick.RemoveListener(OnGeologyView);
        }

        void OnGeologyToggle(bool on)
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.GeologyEnabled = on;
            }
            UpdateGeologyLabels();
        }

        void OnGeologyStrength(float value)
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.GeologyStrength = value;
            }
            UpdateGeologyLabels();
        }

        void OnGeologyView()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.GeologyDebug = (GeologyDebugView)(((int)terrain.GeologyDebug + 1) % GeologyViewNames.Length);
            }
            UpdateGeologyLabels();
        }

        /// <summary>Controls from the renderer's current settings (it may have been changed in the inspector).</summary>
        void SyncGeologyControls()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain == null || geologyToggle == null)
            {
                return;
            }
            geologyToggle.SetIsOnWithoutNotify(terrain.GeologyEnabled);
            geologyStrengthSlider.SetValueWithoutNotify(terrain.GeologyStrength);
            UpdateGeologyLabels();
        }

        void UpdateGeologyLabels()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            int view = terrain != null ? (int)terrain.GeologyDebug : 0;
            geologyStrengthValue.text = geologyStrengthSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
            geologyViewLabel.text = "Geology View: " + GeologyViewNames[Mathf.Clamp(view, 0, GeologyViewNames.Length - 1)];
        }
    }
}
