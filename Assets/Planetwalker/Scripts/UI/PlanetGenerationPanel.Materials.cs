using System.Globalization;
using Planetwalker.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// SURFACE MATERIALS section (V0.2.0F): Procedural (V0.2.0E) / PBR mode, PBR detail strength, a material debug
    /// view cycled by a button (Final Lit by default) and a status line. Presentation settings of the adaptive terrain:
    /// never archived, no rebuild, no regeneration.
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        static readonly string[] MaterialViewNames = { "Final Lit", "Material ID", "Albedo", "Normal", "Roughness", "Triplanar", "Scale", "Tint" };

        [SerializeField] Button materialModeButton;
        [SerializeField] Text materialModeLabel;
        [SerializeField] Slider pbrStrengthSlider;
        [SerializeField] Text pbrStrengthValue;
        [SerializeField] Button materialViewButton;
        [SerializeField] Text materialViewLabel;
        [SerializeField] Text materialStatusText;
        float nextMaterialStatusTime;

        bool MaterialsLayoutComplete()
        {
            return materialModeButton != null && pbrStrengthSlider != null && materialViewButton != null && materialStatusText != null;
        }

        void BuildMaterialsSection(RectTransform panel, Font font)
        {
            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "SURFACE MATERIALS", 13, FontStyle.Bold, AccentColor, 18f);
            var row = CreateRow(panel, "Materials Row");
            materialModeButton = CreateButton(row, font, "Materials: PBR");
            materialModeLabel = materialModeButton.GetComponentInChildren<Text>();
            materialModeLabel.fontSize = FontSize - 3;
            materialViewButton = CreateButton(row, font, "View: Final Lit");
            materialViewLabel = materialViewButton.GetComponentInChildren<Text>();
            materialViewLabel.fontSize = FontSize - 3;
            pbrStrengthSlider = CreateSliderRow(panel, font, "PBR Detail", 0f, 1f, false, out pbrStrengthValue);
            pbrStrengthSlider.SetValueWithoutNotify(1f);
            materialStatusText = CreateLabel(panel, font, "", 10, FontStyle.Normal, MutedTextColor, 26f);
            materialStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            UpdateMaterialLabels();
        }

        void EnableMaterials()
        {
            materialModeButton.onClick.AddListener(OnMaterialMode);
            materialViewButton.onClick.AddListener(OnMaterialView);
            pbrStrengthSlider.onValueChanged.AddListener(OnPbrStrength);
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                pbrStrengthSlider.SetValueWithoutNotify(terrain.PbrDetailStrength);
            }
            UpdateMaterialLabels();
        }

        void DisableMaterials()
        {
            materialModeButton.onClick.RemoveListener(OnMaterialMode);
            materialViewButton.onClick.RemoveListener(OnMaterialView);
            pbrStrengthSlider.onValueChanged.RemoveListener(OnPbrStrength);
        }

        void OnMaterialMode()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.MaterialMode = terrain.MaterialMode == SurfaceMaterialMode.Pbr ? SurfaceMaterialMode.Procedural : SurfaceMaterialMode.Pbr;
            }
            UpdateMaterialLabels();
        }

        void OnMaterialView()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.MaterialDebug = (SurfaceMaterialDebugView)(((int)terrain.MaterialDebug + 1) % MaterialViewNames.Length);
            }
            UpdateMaterialLabels();
        }

        void OnPbrStrength(float value)
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            if (terrain != null)
            {
                terrain.PbrDetailStrength = value;
            }
            UpdateMaterialLabels();
        }

        void UpdateMaterialLabels()
        {
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            bool pbr = terrain == null || terrain.MaterialMode == SurfaceMaterialMode.Pbr;
            int view = terrain != null ? (int)terrain.MaterialDebug : 0;
            materialModeLabel.text = pbr ? "Materials: PBR" : "Materials: Procedural (V0.2.0E)";
            materialViewLabel.text = "View: " + MaterialViewNames[Mathf.Clamp(view, 0, MaterialViewNames.Length - 1)];
            pbrStrengthValue.text = pbrStrengthSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>Status line (twice per second): what the terrain actually draws.</summary>
        void UpdateMaterialStatus()
        {
            if (materialStatusText == null || Time.unscaledTime < nextMaterialStatusTime)
            {
                return;
            }
            nextMaterialStatusTime = Time.unscaledTime + 0.5f;
            AdaptiveTerrainRenderer terrain = ExistingTerrainRenderer();
            string status = terrain == null ? "Materials: legacy terrain (no adaptive renderer)" : terrain.MaterialStatus;
            if (materialStatusText.text != status)
            {
                materialStatusText.text = status;
            }
        }
    }
}
