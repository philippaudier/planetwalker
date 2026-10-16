using System;
using System.Collections.Generic;
using System.Globalization;
using Planetwalker.Bootstrap;
using Planetwalker.Library;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>
    /// World archive UI: the WORLD ARCHIVE section of the panel (V0.1.3A: current world name, unsaved-changes
    /// indicator, Save Planet / Save As New) and its entry to the World Library (V0.1.3B, <see cref="WorldLibraryPanel"/>),
    /// which replaced the V0.1.3A archive browser. Destructive or discarding actions go through an explicit
    /// confirmation. Nothing here regenerates the planet except entering or creating a world.
    /// </summary>
    public sealed partial class PlanetGenerationPanel
    {
        const float BrowserWidth = 450f;
        const string BrowserObjectName = "Archive Browser";

        static readonly Color UnsavedColor = new Color(0.96f, 0.76f, 0.38f, 1f);
        static readonly Color ErrorColor = new Color(0.96f, 0.52f, 0.46f, 1f);
        static readonly Color EntryColor = new Color(0.10f, 0.12f, 0.17f, 0.9f);

        [SerializeField] InputField worldNameField;
        [SerializeField] Text worldStatusText;
        [SerializeField] Text worldMessageText;
        [SerializeField] Button saveWorldButton;
        [SerializeField] Button saveAsNewButton;
        [SerializeField] Button openArchiveButton;
        [SerializeField] RectTransform worldPromptRow;
        [SerializeField] Text worldPromptLabel;
        [SerializeField] InputField worldPromptField;
        [SerializeField] Button worldPromptConfirm;
        [SerializeField] Button worldPromptCancel;
        [SerializeField] WorldLibraryPanel worldLibrary;

        WorldSession subscribedSession;
        PlanetBootstrap subscribedBootstrap;
        PlanetVisualController archiveVisual;
        UnityAction<float> touchFloat;
        UnityAction<bool> touchBool;
        UnityAction touchClick;
        Action<string> worldPromptAction;
        WorldThumbnailUpdater subscribedUpdater;
        bool worldStatusDirty = true;

        WorldSession Worlds => bootstrap != null ? bootstrap.Worlds : null;

        bool ArchiveLayoutComplete()
        {
            return worldNameField != null && worldStatusText != null && saveWorldButton != null && worldPromptRow != null
                && worldLibrary != null;
        }

        // ------------------------------------------------------------------ Lifetime

        void EnableArchive()
        {
            saveWorldButton.onClick.AddListener(OnSaveWorldClicked);
            saveAsNewButton.onClick.AddListener(OnSaveAsNewClicked);
            openArchiveButton.onClick.AddListener(OpenWorldLibrary);
            worldNameField.onEndEdit.AddListener(OnWorldNameEdited);
            worldPromptConfirm.onClick.AddListener(OnWorldPromptConfirmed);
            worldPromptCancel.onClick.AddListener(HideWorldPrompt);
            worldPromptField.onEndEdit.AddListener(OnWorldPromptSubmitted);

            // Any control of the panel may change the persistent configuration: re-evaluate the unsaved state for
            // a moment (see WorldSession.Touch). Camera, clock and clouds never touch it.
            touchFloat ??= _ => TouchWorld();
            touchBool ??= _ => TouchWorld();
            touchClick ??= TouchWorld;
            foreach (Slider slider in panelContent.GetComponentsInChildren<Slider>(true))
            {
                slider.onValueChanged.AddListener(touchFloat);
            }
            foreach (Toggle toggle in panelContent.GetComponentsInChildren<Toggle>(true))
            {
                toggle.onValueChanged.AddListener(touchBool);
            }
            foreach (Button button in panelContent.GetComponentsInChildren<Button>(true))
            {
                button.onClick.AddListener(touchClick);
            }
            seedField.onEndEdit.AddListener(OnSeedTouched);
            SubscribeArchive();
        }

        void DisableArchive()
        {
            saveWorldButton.onClick.RemoveListener(OnSaveWorldClicked);
            saveAsNewButton.onClick.RemoveListener(OnSaveAsNewClicked);
            openArchiveButton.onClick.RemoveListener(OpenWorldLibrary);
            worldNameField.onEndEdit.RemoveListener(OnWorldNameEdited);
            worldPromptConfirm.onClick.RemoveListener(OnWorldPromptConfirmed);
            worldPromptCancel.onClick.RemoveListener(HideWorldPrompt);
            worldPromptField.onEndEdit.RemoveListener(OnWorldPromptSubmitted);
            foreach (Slider slider in panelContent.GetComponentsInChildren<Slider>(true))
            {
                slider.onValueChanged.RemoveListener(touchFloat);
            }
            foreach (Toggle toggle in panelContent.GetComponentsInChildren<Toggle>(true))
            {
                toggle.onValueChanged.RemoveListener(touchBool);
            }
            foreach (Button button in panelContent.GetComponentsInChildren<Button>(true))
            {
                button.onClick.RemoveListener(touchClick);
            }
            seedField.onEndEdit.RemoveListener(OnSeedTouched);
            UnsubscribeArchive();
        }

        void SubscribeArchive()
        {
            if (bootstrap == null || !isActiveAndEnabled)
            {
                return;
            }
            WorldSession session = bootstrap.Worlds;
            if (session == subscribedSession && bootstrap == subscribedBootstrap && bootstrap.Visual == archiveVisual)
            {
                return;
            }
            UnsubscribeArchive();
            subscribedSession = session;
            subscribedBootstrap = bootstrap;
            archiveVisual = bootstrap.Visual;
            session.Changed += OnWorldChanged;
            bootstrap.PlanetGenerated += OnWorldRegenerated;
            bootstrap.SettingsEdited += TouchWorld;
            if (archiveVisual != null)
            {
                archiveVisual.SettingsEdited += TouchWorld;
            }
            subscribedUpdater = bootstrap.ThumbnailUpdater;
            if (subscribedUpdater != null)
            {
                subscribedUpdater.Warning += OnPreviewWarning;
            }
            worldStatusDirty = true;
        }

        void UnsubscribeArchive()
        {
            if (subscribedSession != null)
            {
                subscribedSession.Changed -= OnWorldChanged;
            }
            if (subscribedBootstrap != null)
            {
                subscribedBootstrap.PlanetGenerated -= OnWorldRegenerated;
                subscribedBootstrap.SettingsEdited -= TouchWorld;
            }
            if (archiveVisual != null)
            {
                archiveVisual.SettingsEdited -= TouchWorld;
            }
            if (subscribedUpdater != null)
            {
                subscribedUpdater.Warning -= OnPreviewWarning;
                subscribedUpdater = null;
            }
            subscribedSession = null;
            subscribedBootstrap = null;
            archiveVisual = null;
        }

        void OnWorldRegenerated(Planetwalker.Core.PlanetInstance _)
        {
            TouchWorld();
        }

        void OnSeedTouched(string _)
        {
            TouchWorld();
        }

        void TouchWorld()
        {
            Worlds?.Touch(Time.unscaledTime);
        }

        void OnWorldChanged()
        {
            worldStatusDirty = true;
        }

        /// <summary>Per frame: throttled unsaved-changes check (only after a touch), status text when it changed.</summary>
        void UpdateArchive()
        {
            WorldSession session = Worlds;
            if (session == null || worldStatusText == null)
            {
                return;
            }
            if (session != subscribedSession)
            {
                SubscribeArchive();
            }
            session.Tick(Time.unscaledTime);
            if (worldStatusDirty)
            {
                worldStatusDirty = false;
                RefreshWorldStatus();
            }
        }

        void RefreshWorldStatus()
        {
            WorldSession session = Worlds;
            if (session == null)
            {
                return;
            }
            if (!worldNameField.isFocused)
            {
                worldNameField.SetTextWithoutNotify(session.DisplayName);
            }
            string status;
            if (session.IsDirty)
            {
                status = session.HasArchive ? "UNSAVED CHANGES" : "UNSAVED CHANGES · not in the archive";
                worldStatusText.color = UnsavedColor;
                worldStatusText.fontStyle = FontStyle.Bold;
            }
            else
            {
                status = session.HasArchive
                    ? "Saved · " + FormatLocal(session.ModifiedUtc) + " · id " + PlanetArchive.FormatId(session.PlanetId).Substring(0, 8)
                        + (session.Compatibility == GeneratorCompatibility.BestEffort ? " · best-effort (older generator)" : "")
                    : "New world · not saved yet";
                worldStatusText.color = MutedTextColor;
                worldStatusText.fontStyle = FontStyle.Normal;
            }
            worldStatusText.text = status;
        }

        static string FormatLocal(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        bool AnyTextFieldFocused()
        {
            return (seedField != null && seedField.isFocused) || (worldNameField != null && worldNameField.isFocused)
                || (worldPromptField != null && worldPromptField.isFocused) || (worldLibrary != null && worldLibrary.AnyFieldFocused);
        }

        // ------------------------------------------------------------------ Save

        void OnSaveWorldClicked()
        {
            WorldSession session = Worlds;
            if (session == null)
            {
                return;
            }
            if (!session.HasArchive)
            {
                ShowWorldPrompt("Name this world", session.DisplayName, "Save", name => SaveWorld(name, false));
                return;
            }
            SaveWorld(session.DisplayName, false);
        }

        void OnSaveAsNewClicked()
        {
            WorldSession session = Worlds;
            if (session != null)
            {
                ShowWorldPrompt("Save as a new world", session.DisplayName, "Save new", name => SaveWorld(name, true));
            }
        }

        void SaveWorld(string name, bool asNew)
        {
            WorldSession session = Worlds;
            FlushPendingSettings();
            WorldOperationResult result = asNew ? session.SaveAsNew(name) : session.Save(name);
            ShowResult(worldMessageText, result, result.Success
                ? string.Format(CultureInfo.InvariantCulture, " · {0:0.0} KB · {1:0} ms", result.Bytes / 1024.0, result.Milliseconds)
                : "");
            worldStatusDirty = true;
        }

        /// <summary>
        /// Settings still waiting for the panel's slider delay are written to their settings objects, so a save
        /// records what the controls show. A pending terrain regeneration still happens on its own schedule:
        /// saving itself never regenerates.
        /// </summary>
        void FlushPendingSettings()
        {
            if (pendingClimateTime >= 0f)
            {
                pendingClimateTime = -1f;
                ApplyClimateSettings();
            }
            if (pendingBiomeTime >= 0f)
            {
                pendingBiomeTime = -1f;
                ApplyBiomeSettings();
            }
            if (pendingSurfaceTime >= 0f)
            {
                pendingSurfaceTime = -1f;
                ApplySurfaceSettings();
            }
            if (pendingRegenerateTime >= 0f && bootstrap != null)
            {
                ApplyControlsToDefinition();
            }
        }

        /// <summary>Edits waiting for the slider delay are dropped (a load replaces every setting).</summary>
        void CancelPendingSettings()
        {
            pendingRegenerateTime = -1f;
            pendingClimateTime = -1f;
            pendingBiomeTime = -1f;
            pendingSurfaceTime = -1f;
        }

        void OnWorldNameEdited(string text)
        {
            WorldSession session = Worlds;
            if (session == null)
            {
                return;
            }
            string clean = PlanetArchive.CleanName(text);
            if (clean == session.DisplayName)
            {
                worldNameField.SetTextWithoutNotify(clean);
                return;
            }
            WorldOperationResult result = session.RenameCurrent(clean);
            ShowResult(worldMessageText, result, "");
            worldStatusDirty = true;
        }

        void ShowWorldPrompt(string message, string initial, string confirmCaption, Action<string> action)
        {
            worldPromptAction = action;
            worldPromptLabel.text = message;
            worldPromptConfirm.GetComponentInChildren<Text>().text = confirmCaption;
            worldPromptField.SetTextWithoutNotify(initial);
            worldPromptRow.gameObject.SetActive(true);
            worldPromptLabel.gameObject.SetActive(true);
            worldPromptField.Select();
            worldPromptField.ActivateInputField();
        }

        void HideWorldPrompt()
        {
            worldPromptAction = null;
            worldPromptRow.gameObject.SetActive(false);
            worldPromptLabel.gameObject.SetActive(false);
        }

        void OnWorldPromptConfirmed()
        {
            Action<string> action = worldPromptAction;
            string text = worldPromptField.text;
            HideWorldPrompt();
            action?.Invoke(text);
        }

        /// <summary>Enter in a prompt field confirms it (losing focus does not).</summary>
        static bool EnterPressed()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
        }

        void OnWorldPromptSubmitted(string _)
        {
            if (worldPromptAction != null && EnterPressed())
            {
                OnWorldPromptConfirmed();
            }
        }

        static void ShowResult(Text label, WorldOperationResult result, string details)
        {
            label.text = result.Message + details + (result.Warnings.Count > 0 ? "\n" + result.Warnings.Count + " warning(s), see the Console." : "");
            label.color = result.Success ? MutedTextColor : ErrorColor;
            if (!result.Success)
            {
                Debug.LogWarning("Planetwalker world archive: " + result.Message);
            }
            foreach (string warning in result.Warnings)
            {
                Debug.LogWarning("Planetwalker world archive: " + warning);
            }
        }

        // ------------------------------------------------------------------ World Library

        /// <summary>Opens the World Library (V0.1.3B) over the planet; Enter World / New World close it.</summary>
        public void OpenWorldLibrary()
        {
            if (bootstrap == null || worldLibrary == null)
            {
                return;
            }
            FlushPendingSettings();
            worldLibrary.Bind(new WorldLibraryContext
            {
                Session = bootstrap.Worlds,
                Thumbnails = bootstrap.Worlds.Thumbnails,
                Updater = bootstrap.ThumbnailUpdater,
                CreateWorld = bootstrap.CreateNewWorld,
                BeforeChange = CancelPendingSettings,
                AfterChange = OnWorldEntered,
            });
            worldLibrary.Open();
        }

        /// <summary>Closes the World Library (also when entering contemplation mode).</summary>
        public void CloseWorldLibrary()
        {
            if (worldLibrary != null && worldLibrary.IsOpen)
            {
                worldLibrary.Close();
            }
        }

        public WorldLibraryPanel WorldLibrary => worldLibrary;

        /// <summary>A world was entered or created from the library: every control shows it (no event fires, no extra regeneration).</summary>
        void OnWorldEntered(WorldOperationResult result)
        {
            SyncFromDefinition();
            SyncHydrologyControls();
            UpdateClimateLegend();
            UpdateBiomeLegend();
            UpdateCloudLegend();
            ShowResult(worldMessageText, result, result.Regenerations > 0
                ? string.Format(CultureInfo.InvariantCulture, " · {0} regeneration · {1:0} ms (read {2:0} ms, generate {3:0} ms)",
                    result.Regenerations, result.Milliseconds, result.ReadMilliseconds, result.RegenerationMilliseconds)
                : "");
            worldStatusDirty = true;
        }

        void OnPreviewWarning(string warning)
        {
            worldMessageText.text = warning;
            worldMessageText.color = UnsavedColor;
            Debug.LogWarning("Planetwalker world library: " + warning);
        }

        // ------------------------------------------------------------------ Layout

        void BuildArchiveSection(RectTransform panel, Font font)
        {
            CreateLabel(panel, font, "WORLD ARCHIVE", 13, FontStyle.Bold, AccentColor, 18f);
            RectTransform nameRow = CreateRow(panel, "World Name Row");
            CreateLabel(nameRow, font, "World", FontSize, FontStyle.Normal, MutedTextColor, RowHeight, 70f);
            worldNameField = CreateTextField(nameRow, font, "World name");
            worldStatusText = CreateLabel(panel, font, "New world · not saved yet", 12, FontStyle.Normal, MutedTextColor, 16f);

            worldPromptLabel = CreateLabel(panel, font, "", 12, FontStyle.Bold, TextColor, 16f);
            worldPromptRow = CreateRow(panel, "World Prompt Row");
            worldPromptField = CreateTextField(worldPromptRow, font, "World name");
            worldPromptConfirm = CreateButton(worldPromptRow, font, "Save");
            worldPromptConfirm.GetComponent<LayoutElement>().flexibleWidth = 0f;
            worldPromptConfirm.GetComponent<LayoutElement>().preferredWidth = 92f;
            worldPromptCancel = CreateButton(worldPromptRow, font, "Cancel");
            worldPromptCancel.GetComponent<LayoutElement>().flexibleWidth = 0f;
            worldPromptCancel.GetComponent<LayoutElement>().preferredWidth = 70f;
            worldPromptRow.gameObject.SetActive(false);
            worldPromptLabel.gameObject.SetActive(false);

            RectTransform buttons = CreateRow(panel, "World Buttons");
            saveWorldButton = CreateButton(buttons, font, "Save Planet");
            saveAsNewButton = CreateButton(buttons, font, "Save As New");
            openArchiveButton = CreateButton(buttons, font, "World Library");
            foreach (Button b in new[] { saveWorldButton, saveAsNewButton, openArchiveButton })
            {
                b.GetComponentInChildren<Text>().fontSize = FontSize - 2;
            }
            worldMessageText = CreateLabel(panel, font, "", 11, FontStyle.Normal, MutedTextColor, 14f);
            Wrap(worldMessageText, 14f);
        }

        /// <summary>Wrapped label whose height follows its text (at least one line).</summary>
        static void Wrap(Text label, float minHeight)
        {
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            LayoutElement element = label.GetComponent<LayoutElement>();
            element.preferredHeight = -1f;
            element.minHeight = minHeight;
        }

        /// <summary>Single-line Unicode text field (world names).</summary>
        static InputField CreateTextField(RectTransform parent, Font font, string placeholder)
        {
            InputField field = CreateInputField(parent, font);
            field.gameObject.name = "Name Input";
            field.contentType = InputField.ContentType.Standard;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = PlanetArchive.MaxNameLength;
            ((Text)field.placeholder).text = placeholder;
            return field;
        }
    }
}
