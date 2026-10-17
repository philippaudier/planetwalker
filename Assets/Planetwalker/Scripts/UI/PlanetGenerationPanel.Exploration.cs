using System.Globalization;
using System.Text;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// SURFACE EXPLORATION section (V0.2.0D): Orbital View / Explore Surface switch, controlled descent, exploration
    /// speed and minimum clearance (presentation settings, never archived), and compact diagnostics (mode, altitude above
    /// sea level, clearance above the terrain, speed, terrain LOD under the camera, finest vertex spacing, surface detail).
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        [SerializeField] Button exploreModeButton;
        [SerializeField] Text exploreModeLabel;
        [SerializeField] Button exploreDescendButton;
        [SerializeField] Slider exploreSpeedSlider;
        [SerializeField] Text exploreSpeedValue;
        [SerializeField] Slider exploreClearanceSlider;
        [SerializeField] Text exploreClearanceValue;
        [SerializeField] Text exploreStatsText;

        readonly StringBuilder exploreStats = new StringBuilder(256);
        float nextExploreStatsTime;

        bool ExplorationLayoutComplete()
        {
            return exploreModeButton != null && exploreDescendButton != null && exploreSpeedSlider != null && exploreClearanceSlider != null
                && exploreStatsText != null;
        }

        void BuildExplorationSection(RectTransform panel, Font font)
        {
            CreateSpacer(panel, 4f);
            CreateLabel(panel, font, "SURFACE EXPLORATION", 13, FontStyle.Bold, AccentColor, 18f);
            var row = CreateRow(panel, "Exploration Row");
            exploreModeButton = CreateButton(row, font, "Explore Surface (F)");
            exploreModeLabel = exploreModeButton.GetComponentInChildren<Text>();
            exploreModeLabel.fontSize = FontSize - 3;
            exploreDescendButton = CreateButton(row, font, "Descend");
            exploreDescendButton.GetComponentInChildren<Text>().fontSize = FontSize - 3;
            exploreSpeedSlider = CreateSliderRow(panel, font, "Exploration Speed", -2f, 2f, false, out exploreSpeedValue); // log2 of the factor
            exploreClearanceSlider = CreateSliderRow(panel, font, "Min Clearance (m)", 0.5f, 20f, false, out exploreClearanceValue);
            exploreSpeedSlider.SetValueWithoutNotify(0f);
            exploreClearanceSlider.SetValueWithoutNotify(1.5f);
            exploreStatsText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 58f);
            exploreStatsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            exploreStatsText.supportRichText = true;
            CreateLabel(panel, font, "Explore: click view = mouse look (Esc releases) · WASD move · Space/Ctrl up/down · Shift boost",
                10, FontStyle.Normal, MutedTextColor, 26f).horizontalOverflow = HorizontalWrapMode.Wrap;
            UpdateExplorationValueLabels();
        }

        void EnableExploration()
        {
            exploreModeButton.onClick.AddListener(OnExploreModeClicked);
            exploreDescendButton.onClick.AddListener(OnExploreDescendClicked);
            exploreSpeedSlider.onValueChanged.AddListener(OnExplorationSettingChanged);
            exploreClearanceSlider.onValueChanged.AddListener(OnExplorationSettingChanged);
        }

        void DisableExploration()
        {
            exploreModeButton.onClick.RemoveListener(OnExploreModeClicked);
            exploreDescendButton.onClick.RemoveListener(OnExploreDescendClicked);
            exploreSpeedSlider.onValueChanged.RemoveListener(OnExplorationSettingChanged);
            exploreClearanceSlider.onValueChanged.RemoveListener(OnExplorationSettingChanged);
        }

        void OnExploreModeClicked()
        {
            if (bootstrap != null)
            {
                bootstrap.SetExploring(!bootstrap.Exploring);
                nextExploreStatsTime = 0f;
            }
        }

        /// <summary>Controlled descent to the explorer's descent clearance (enters Explore Surface first if needed).</summary>
        void OnExploreDescendClicked()
        {
            if (bootstrap == null)
            {
                return;
            }
            if (!bootstrap.Exploring)
            {
                bootstrap.SetExploring(true);
            }
            PlanetExploreCamera explorer = bootstrap.ExploreCamera;
            if (explorer != null)
            {
                // Down by a decade per press (log-uniform), the last step at eye height.
                explorer.StartDescent(explorer.NextDescentTarget());
            }
        }

        void OnExplorationSettingChanged(float _)
        {
            PlanetExploreCamera explorer = bootstrap != null ? bootstrap.ExploreCamera : null;
            if (explorer != null)
            {
                explorer.SpeedFactor = Mathf.Pow(2f, exploreSpeedSlider.value);
                explorer.MinClearanceMeters = exploreClearanceSlider.value;
            }
            UpdateExplorationValueLabels();
        }

        void UpdateExplorationValueLabels()
        {
            exploreSpeedValue.text = Mathf.Pow(2f, exploreSpeedSlider.value).ToString("0.00", CultureInfo.InvariantCulture) + "x";
            exploreClearanceValue.text = exploreClearanceSlider.value.ToString("0.0", CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>Diagnostics (4 per second; one string per update, no per-frame allocation).</summary>
        void UpdateExplorationStats()
        {
            if (exploreStatsText == null || bootstrap == null || Time.unscaledTime < nextExploreStatsTime)
            {
                return;
            }
            nextExploreStatsTime = Time.unscaledTime + 0.25f;
            bool exploring = bootstrap.Exploring;
            string label = exploring ? "Orbital View (F)" : "Explore Surface (F)";
            if (exploreModeLabel.text != label)
            {
                exploreModeLabel.text = label;
            }
            PlanetInstance planet = bootstrap.Current;
            exploreStats.Clear();
            exploreStats.Append("Mode: <b>").Append(exploring ? "Explore Surface" : "Orbital View").Append("</b>");
            if (planet != null && TryGetCameraPlanetPosition(out Vector3d cameraPlanet))
            {
                PlanetScale scale = planet.Scale;
                Vector3d direction = cameraPlanet.Normalized;
                double terrain = planet.Height.GetElevation(direction);
                double sea = planet.SeaLevel;
                double radius = cameraPlanet.Magnitude;
                double aboveSea = scale.ToMeters(radius - planet.Radius * (1.0 + sea));
                double clearance = scale.ToMeters(radius - planet.Radius * (1.0 + System.Math.Max(terrain, sea)));
                exploreStats.Append(" · altitude ").Append(PlanetScale.FormatDistance(aboveSea)).Append(" ASL · clearance ")
                    .Append(PlanetScale.FormatDistance(clearance)).Append(terrain > sea ? " (terrain)" : " (sea)");
                if (exploring && bootstrap.ExploreCamera != null)
                {
                    PlanetExploreCamera explorer = bootstrap.ExploreCamera;
                    exploreStats.Append("\nSpeed ").Append(PlanetScale.FormatDistance(explorer.LastSpeedMetersPerSecond)).Append("/s")
                        .Append(explorer.Descending ? " · descending" : "").Append(explorer.MouseCaptured ? " · mouse look" : "");
                }
                AdaptiveTerrainRenderer terrainRenderer = ExistingTerrainRenderer();
                if (terrainRenderer != null && terrainRenderer.IsDisplaying && terrainRenderer.Displayed != null)
                {
                    // Level of the displayed patch under the camera and its vertex spacing.
                    int level = -1;
                    for (int l = 0; l <= PlanetPatchId.MaxLevel; l++)
                    {
                        if (terrainRenderer.Displayed.IsLeaf(CubeSphereCoordinates.PatchAt(direction.ToVector3(), l)))
                        {
                            level = l;
                            break;
                        }
                    }
                    exploreStats.Append("\nTerrain: adaptive · level under camera ").Append(level);
                    if (level >= 0)
                    {
                        double spacing = scale.ToMeters(planet.Radius * System.Math.PI * 0.5 / (terrainRenderer.Settings.patchResolution * (double)(1 << level)));
                        exploreStats.Append(" · vertex spacing ").Append(PlanetScale.FormatDistance(spacing));
                    }
                }
                else
                {
                    exploreStats.Append("\nTerrain: legacy faces (resolution ").Append(planet.Definition.resolution).Append(")");
                }
                PlanetDefinition d = planet.Definition;
                exploreStats.Append("\nSurface detail ").Append(d.surfaceDetail ? "on" : "off");
                if (d.surfaceDetail)
                {
                    exploreStats.Append(" · regional ").Append(d.regionalDetail.ToString("0.0", CultureInfo.InvariantCulture))
                        .Append(" / local ").Append(d.localDetail.ToString("0.0", CultureInfo.InvariantCulture))
                        .Append(" / micro ").Append(d.microDetail.ToString("0.0", CultureInfo.InvariantCulture));
                }
            }
            exploreStatsText.text = exploreStats.ToString();
        }
    }
}
