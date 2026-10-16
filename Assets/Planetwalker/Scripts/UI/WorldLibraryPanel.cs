using System;
using System.Collections.Generic;
using System.Globalization;
using Planetwalker.Generation;
using Planetwalker.Library;
using Planetwalker.Persistence;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Planetwalker.UI
{
    /// <summary>What the World Library works with (the panel provides it; tests build their own).</summary>
    public sealed class WorldLibraryContext
    {
        public WorldSession Session;
        /// <summary>Thumbnail files (decoded by the library's own bounded cache). Null: placeholders only.</summary>
        public WorldThumbnailStore Thumbnails;
        /// <summary>Captures previews after saves; used by Refresh Preview. May be null.</summary>
        public WorldThumbnailUpdater Updater;
        /// <summary>Generates a new world (New World). Null hides the action.</summary>
        public Action CreateWorld;
        /// <summary>Called before a world is entered or created (drop pending panel edits).</summary>
        public Action BeforeChange;
        /// <summary>Called after a world was entered or created (sync controls, report the result).</summary>
        public Action<WorldOperationResult> AfterChange;
    }

    /// <summary>
    /// The World Library (V0.1.3B): a full-screen overlay listing the saved worlds as cards (thumbnail, name, seed,
    /// date), with search, sort, pages of <see cref="PageSize"/> cards, and a details panel (preview, identity,
    /// recorded characteristics, Enter World, Rename / Duplicate / Delete, Refresh Preview). Opening reads archive
    /// metadata once; search and sort work in memory; thumbnails are decoded lazily for the visible page only and
    /// released on close. Never regenerates a planet except when a world is entered or created.
    /// </summary>
    public sealed class WorldLibraryPanel : MonoBehaviour
    {
        public const int PageSize = 12;
        public const string ObjectName = "World Library";
        const float CardWidth = 196f;
        const float CardHeight = 262f;
        const float CardSpacing = 14f;
        const float ThumbSize = 172f;
        const float DetailsWidth = 440f;
        const float PreviewSize = 256f;
        const float DoubleClickSeconds = 0.35f;
        const int DecodesPerFrame = 3;

        static readonly Color BackdropColor = new Color(0.005f, 0.006f, 0.012f, 0.78f);
        static readonly Color WindowColor = new Color(0.030f, 0.036f, 0.052f, 0.98f);
        static readonly Color DetailsColor = new Color(0.050f, 0.060f, 0.084f, 1f);
        static readonly Color CardColor = new Color(0.065f, 0.077f, 0.105f, 1f);
        static readonly Color CardBorderColor = new Color(0.17f, 0.20f, 0.26f, 1f);
        static readonly Color PlaceholderColor = new Color(0.10f, 0.12f, 0.16f, 1f);
        static readonly Color WarningColor = new Color(0.96f, 0.76f, 0.38f, 1f);
        static readonly Color ErrorColor = new Color(0.96f, 0.52f, 0.46f, 1f);
        static readonly Color PrimaryTextColor = new Color(0.03f, 0.05f, 0.08f, 1f);

        sealed class Card
        {
            public RectTransform Root;
            public Image Border;
            public RawImage Thumb;
            public Text Placeholder;
            public Text Name;
            public Text Info;
            public Text Tag;
            public WorldArchiveEntry Entry;
            public int ViewIndex = -1;
        }

        readonly List<Card> cards = new List<Card>();
        readonly Queue<Guid> thumbnailQueue = new Queue<Guid>();
        readonly HashSet<Guid> queued = new HashSet<Guid>();
        readonly WorldLibraryModel model = new WorldLibraryModel();
        WorldLibraryContext context;
        WorldThumbnailCache cache;
        WorldThumbnailUpdater subscribedUpdater;
        Texture2D placeholderTexture;
        Action<string> promptAction;
        Guid selectedId;
        int page;
        Guid lastClickedId;
        float lastClickTime = -10f;

        [SerializeField] RectTransform window;
        [SerializeField] InputField searchField;
        [SerializeField] Button sortButton;
        [SerializeField] Text sortLabel;
        [SerializeField] Text countLabel;
        [SerializeField] Button newWorldButton;
        [SerializeField] Button closeButton;
        [SerializeField] RectTransform gridContent;
        [SerializeField] ScrollRect gridScroll;
        [SerializeField] Text emptyLabel;
        [SerializeField] Button previousButton;
        [SerializeField] Button nextButton;
        [SerializeField] Text pageLabel;
        [SerializeField] RawImage preview;
        [SerializeField] Text previewPlaceholder;
        [SerializeField] Text detailName;
        [SerializeField] Text detailType;
        [SerializeField] Text detailState;
        [SerializeField] Text seedValue;
        [SerializeField] Text createdValue;
        [SerializeField] Text modifiedValue;
        [SerializeField] Text idValue;
        [SerializeField] Text landValue;
        [SerializeField] Text oceanValue;
        [SerializeField] Text temperatureValue;
        [SerializeField] Text biomeValue;
        [SerializeField] Text statisticsNote;
        [SerializeField] RectTransform characteristics;
        [SerializeField] Text promptLabel;
        [SerializeField] RectTransform promptRow;
        [SerializeField] InputField promptField;
        [SerializeField] Button promptConfirm;
        [SerializeField] Button promptCancel;
        [SerializeField] Text messageLabel;
        [SerializeField] Button enterButton;
        [SerializeField] Button renameButton;
        [SerializeField] Button duplicateButton;
        [SerializeField] Button deleteButton;
        [SerializeField] Button refreshPreviewButton;

        public bool IsOpen => gameObject.activeSelf;
        public WorldLibraryModel Model => model;
        public WorldThumbnailCache ThumbnailCache => cache;
        public Guid SelectedId => selectedId;
        public WorldArchiveEntry Selected => selectedId == Guid.Empty ? null : model.Find(selectedId);
        public int Page => page;
        public int PageCount => Mathf.Max(1, (model.View.Count + PageSize - 1) / PageSize);
        public bool PromptOpen => promptAction != null;
        public string Message => messageLabel != null ? messageLabel.text : "";
        public bool AnyFieldFocused => (searchField != null && searchField.isFocused) || (promptField != null && promptField.isFocused);

        /// <summary>Entries shown on the current page (card order).</summary>
        public IEnumerable<WorldArchiveEntry> VisibleEntries
        {
            get
            {
                foreach (Card card in cards)
                {
                    if (card.Entry != null)
                    {
                        yield return card.Entry;
                    }
                }
            }
        }

        /// <summary>Texture shown on the card of a world, if that card is visible (tests).</summary>
        public Texture CardTexture(Guid planetId)
        {
            foreach (Card card in cards)
            {
                if (card.Entry != null && card.Entry.PlanetId == planetId)
                {
                    return card.Thumb.texture;
                }
            }
            return null;
        }

        public Texture PreviewTexture => preview.texture;

        // ------------------------------------------------------------------ Lifetime

        /// <summary>Builds the (closed) library under a screen-space canvas, replacing a previous one.</summary>
        public static WorldLibraryPanel Create(Transform canvas, Font font)
        {
            Transform old = canvas.Find(ObjectName);
            if (old != null)
            {
                old.gameObject.SetActive(false);
                DestroySafe(old.gameObject);
            }
            RectTransform root = PlanetGenerationPanel.CreateRect(ObjectName, canvas);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            root.gameObject.SetActive(false);
            var library = root.gameObject.AddComponent<WorldLibraryPanel>();
            library.Build(root, font);
            return library;
        }

        public void Bind(WorldLibraryContext libraryContext)
        {
            context = libraryContext;
            cache?.Clear();
            cache = new WorldThumbnailCache(context.Thumbnails, PageSize * 3);
            newWorldButton.gameObject.SetActive(context.CreateWorld != null);
        }

        /// <summary>Shows the library: one archive listing (metadata only), the page of the current world selected.</summary>
        public void Open()
        {
            if (context == null)
            {
                return;
            }
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            SubscribeUpdater();
            HidePrompt();
            SetMessage("", false);
            context.Session.RefreshDirty();
            model.Reload(context.Session.Store);
            Guid preferred = context.Session.HasArchive ? context.Session.PlanetId : selectedId;
            int index = model.IndexOf(preferred);
            selectedId = Guid.Empty;
            SelectIndex(index >= 0 ? index : 0, true);
            RefreshHeader();
        }

        /// <summary>Hides the library and releases every decoded thumbnail.</summary>
        public void Close()
        {
            HidePrompt();
            UnsubscribeUpdater();
            foreach (Card card in cards)
            {
                card.Thumb.texture = null;
            }
            if (preview != null)
            {
                preview.texture = null;
            }
            thumbnailQueue.Clear();
            queued.Clear();
            cache?.Clear();
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            UnsubscribeUpdater();
            cache?.Clear();
            DestroySafe(placeholderTexture);
        }

        void SubscribeUpdater()
        {
            if (context.Updater != subscribedUpdater)
            {
                UnsubscribeUpdater();
                subscribedUpdater = context.Updater;
                if (subscribedUpdater != null)
                {
                    subscribedUpdater.ThumbnailUpdated += OnThumbnailUpdated;
                }
            }
        }

        void UnsubscribeUpdater()
        {
            if (subscribedUpdater != null)
            {
                subscribedUpdater.ThumbnailUpdated -= OnThumbnailUpdated;
                subscribedUpdater = null;
            }
        }

        void OnThumbnailUpdated(Guid id)
        {
            cache?.Invalidate(id);
            ShowThumbnail(id, null);
            Enqueue(id);
        }

        // ------------------------------------------------------------------ Per frame

        void Update()
        {
            FlushThumbnails(DecodesPerFrame);
            HandleKeyboard();
        }

        /// <summary>Decodes up to <paramref name="max"/> queued thumbnails of the visible page (lazy, spread over frames).</summary>
        public void FlushThumbnails(int max = int.MaxValue)
        {
            for (int i = 0; i < max && thumbnailQueue.Count > 0; i++)
            {
                Guid id = thumbnailQueue.Dequeue();
                queued.Remove(id);
                ShowThumbnail(id, cache.Get(id));
            }
        }

        void HandleKeyboard()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (PromptOpen)
                {
                    HidePrompt();
                }
                else if (!AnyFieldFocused)
                {
                    Close();
                }
                return;
            }
            if (AnyFieldFocused)
            {
                return;
            }
            if (PromptOpen)
            {
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                {
                    ConfirmPrompt();
                }
                return;
            }
            int index = model.IndexOf(selectedId);
            int columns = Columns();
            if (keyboard.rightArrowKey.wasPressedThisFrame) SelectIndex(index + 1);
            else if (keyboard.leftArrowKey.wasPressedThisFrame) SelectIndex(index - 1);
            else if (keyboard.downArrowKey.wasPressedThisFrame) SelectIndex(index + columns);
            else if (keyboard.upArrowKey.wasPressedThisFrame) SelectIndex(index - columns);
            else if (keyboard.pageDownKey.wasPressedThisFrame) ShowPage(page + 1);
            else if (keyboard.pageUpKey.wasPressedThisFrame) ShowPage(page - 1);
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) EnterSelected();
        }

        int Columns()
        {
            float width = gridContent != null ? gridContent.rect.width : 0f;
            return Mathf.Max(1, Mathf.FloorToInt((width + CardSpacing) / (CardWidth + CardSpacing)));
        }

        // ------------------------------------------------------------------ Search, sort, pages, selection

        public void SetQuery(string query)
        {
            model.SetQuery(query);
            if (searchField != null && searchField.text != model.Query && !searchField.isFocused)
            {
                searchField.SetTextWithoutNotify(model.Query);
            }
            int index = model.IndexOf(selectedId);
            SelectIndex(index >= 0 ? index : 0, true);
            RefreshHeader();
        }

        public void SetSort(WorldSortMode sort)
        {
            model.SetSort(sort);
            sortLabel.text = "Sort: " + WorldLibraryModel.SortLabel(sort) + "  ▼";
            int index = model.IndexOf(selectedId);
            SelectIndex(index >= 0 ? index : 0, true);
        }

        public void CycleSort()
        {
            SetSort((WorldSortMode)(((int)model.Sort + 1) % Enum.GetValues(typeof(WorldSortMode)).Length));
        }

        /// <summary>Shows another page; the selection moves to its first world when it is not on that page.</summary>
        public void ShowPage(int newPage)
        {
            newPage = Mathf.Clamp(newPage, 0, PageCount - 1);
            if (newPage == page)
            {
                return;
            }
            int index = model.IndexOf(selectedId);
            SelectIndex(index >= 0 && index / PageSize == newPage ? index : newPage * PageSize, true);
        }

        /// <summary>Selects the world at a position of the filtered, sorted list (clamped); shows its page.</summary>
        public void SelectIndex(int index, bool forceBind = false)
        {
            if (model.View.Count == 0)
            {
                selectedId = Guid.Empty;
                page = 0;
                BindPage();
                RefreshDetails();
                return;
            }
            index = Mathf.Clamp(index, 0, model.View.Count - 1);
            int newPage = index / PageSize;
            bool rebind = forceBind || newPage != page;
            page = newPage;
            selectedId = model.View[index].PlanetId;
            if (rebind)
            {
                BindPage();
            }
            else
            {
                RefreshSelectionBorders();
            }
            RefreshDetails();
        }

        public void Select(Guid planetId)
        {
            int index = model.IndexOf(planetId);
            if (index >= 0)
            {
                SelectIndex(index);
            }
        }

        void OnCardClicked(Card card)
        {
            if (card.Entry == null)
            {
                return;
            }
            Guid id = card.Entry.PlanetId;
            bool doubleClick = id == lastClickedId && Time.unscaledTime - lastClickTime < DoubleClickSeconds;
            lastClickedId = id;
            lastClickTime = Time.unscaledTime;
            SelectIndex(card.ViewIndex);
            if (doubleClick && card.Entry.IsValid)
            {
                EnterSelected();
            }
        }

        void BindPage()
        {
            int first = page * PageSize;
            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                int index = first + i;
                bool shown = index < model.View.Count;
                card.Root.gameObject.SetActive(shown);
                card.ViewIndex = shown ? index : -1;
                card.Entry = shown ? model.View[index] : null;
                if (shown)
                {
                    BindCard(card);
                }
            }
            emptyLabel.gameObject.SetActive(model.View.Count == 0);
            emptyLabel.text = model.All.Count == 0
                ? "No saved worlds yet.\nSave the current planet (Save Planet) or discover a new one (New World)."
                : "No world matches \"" + model.Query + "\".";
            pageLabel.text = "Page " + (page + 1) + " / " + PageCount;
            previousButton.interactable = page > 0;
            nextButton.interactable = page + 1 < PageCount;
            if (gridScroll != null)
            {
                gridScroll.verticalNormalizedPosition = 1f;
            }
            RefreshSelectionBorders();
        }

        void BindCard(Card card)
        {
            WorldArchiveEntry e = card.Entry;
            bool current = IsCurrent(e);
            if (e.IsValid)
            {
                card.Name.text = e.DisplayName;
                card.Name.color = TextColor;
                card.Info.text = "Seed " + e.PlanetSeed.ToString(CultureInfo.InvariantCulture) + " · " + FormatDate(e.ModifiedUtc);
                string tag = current ? (context.Session.IsDirty ? "CURRENT · UNSAVED" : "CURRENT") : "";
                if (e.Metadata != null && e.Metadata.HasLand)
                {
                    tag = (tag.Length > 0 ? tag + " · " : "") + "Land " + WorldMetadata.FormatPercent(e.Metadata.LandFraction);
                }
                card.Tag.text = tag;
                card.Tag.color = current && context.Session.IsDirty ? WarningColor : current ? AccentColor : MutedTextColor;
                card.Placeholder.text = "NO PREVIEW";
            }
            else
            {
                card.Name.text = "Unreadable archive";
                card.Name.color = ErrorColor;
                card.Info.text = "id " + e.ShortId;
                card.Tag.text = "UNAVAILABLE";
                card.Tag.color = WarningColor;
                card.Placeholder.text = "UNAVAILABLE";
            }
            ShowCardThumbnail(card, null);
            if (e.IsValid && cache != null)
            {
                if (cache.IsResolved(e.PlanetId))
                {
                    ShowCardThumbnail(card, cache.Get(e.PlanetId));
                }
                else
                {
                    Enqueue(e.PlanetId);
                }
            }
        }

        void Enqueue(Guid id)
        {
            if (queued.Add(id))
            {
                thumbnailQueue.Enqueue(id);
            }
        }

        void ShowThumbnail(Guid id, Texture2D texture)
        {
            foreach (Card card in cards)
            {
                if (card.Entry != null && card.Entry.PlanetId == id)
                {
                    ShowCardThumbnail(card, texture);
                }
            }
            if (id == selectedId)
            {
                ShowPreview(texture);
            }
        }

        void ShowCardThumbnail(Card card, Texture2D texture)
        {
            card.Thumb.texture = texture != null ? texture : placeholderTexture;
            card.Thumb.color = texture != null ? Color.white : PlaceholderColor;
            card.Placeholder.gameObject.SetActive(texture == null);
        }

        void ShowPreview(Texture2D texture)
        {
            preview.texture = texture != null ? texture : placeholderTexture;
            preview.color = texture != null ? Color.white : PlaceholderColor;
            previewPlaceholder.gameObject.SetActive(texture == null);
        }

        void RefreshSelectionBorders()
        {
            foreach (Card card in cards)
            {
                bool selected = card.Entry != null && card.Entry.PlanetId == selectedId;
                card.Border.color = selected ? AccentColor : CardBorderColor;
                card.Root.GetComponent<Outline>().enabled = selected;
            }
        }

        bool IsCurrent(WorldArchiveEntry entry)
        {
            return entry != null && context.Session.HasArchive && entry.PlanetId == context.Session.PlanetId;
        }

        void RefreshHeader()
        {
            int total = model.All.Count;
            int shown = model.View.Count;
            countLabel.text = (shown == total ? total + (total == 1 ? " world" : " worlds") : shown + " of " + total + " worlds")
                + string.Format(CultureInfo.InvariantCulture, "  ·  listed in {0:0} ms", model.LastReloadMilliseconds);
        }

        // ------------------------------------------------------------------ Details

        void RefreshDetails()
        {
            WorldArchiveEntry e = Selected;
            bool valid = e != null && e.IsValid;
            bool current = IsCurrent(e);
            bool dirty = context.Session.IsDirty;
            if (e == null)
            {
                detailName.text = "No world selected";
                detailType.text = "";
                detailState.text = "";
                seedValue.text = createdValue.text = modifiedValue.text = idValue.text = "—";
                characteristics.gameObject.SetActive(false);
                statisticsNote.text = "";
                ShowPreview(null);
                previewPlaceholder.text = "NO WORLD";
            }
            else if (!valid)
            {
                detailName.text = "Unreadable archive";
                detailType.text = "UNAVAILABLE";
                detailState.text = e.Error;
                detailState.color = ErrorColor;
                seedValue.text = "—";
                createdValue.text = "—";
                modifiedValue.text = FormatDate(e.ModifiedUtc) + " (file)";
                idValue.text = PlanetArchive.FormatId(e.PlanetId);
                characteristics.gameObject.SetActive(false);
                statisticsNote.text = "This file cannot be loaded. It is kept so you can inspect or delete it.";
                ShowPreview(null);
                previewPlaceholder.text = "UNAVAILABLE";
            }
            else
            {
                detailName.text = e.DisplayName;
                detailType.text = "TERRESTRIAL WORLD  ·  GENESIS"
                    + (e.Compatibility == GeneratorCompatibility.BestEffort ? "  ·  OLDER GENERATOR (BEST-EFFORT)" : "");
                detailState.text = current ? (dirty ? "CURRENT WORLD  ·  UNSAVED CHANGES" : "CURRENT WORLD") : "";
                detailState.color = current && dirty ? WarningColor : AccentColor;
                seedValue.text = e.PlanetSeed.ToString(CultureInfo.InvariantCulture);
                createdValue.text = FormatDate(e.CreatedUtc);
                modifiedValue.text = FormatDate(e.ModifiedUtc);
                idValue.text = PlanetArchive.FormatId(e.PlanetId);
                characteristics.gameObject.SetActive(true);
                WorldMetadata m = e.Metadata;
                const string unavailable = "Unavailable";
                landValue.text = m != null && m.HasLand ? WorldMetadata.FormatPercent(m.LandFraction) : unavailable;
                oceanValue.text = m != null && m.HasLand ? WorldMetadata.FormatPercent(1f - m.LandFraction) : unavailable;
                temperatureValue.text = m != null && m.HasTemperature ? WorldMetadata.FormatTemperature(m.MeanTemperature) : unavailable;
                biomeValue.text = m != null && m.DominantBiome != null ? Biomes.Name(m.DominantBiome.Value) : unavailable;
                bool complete = m != null && m.HasLand && m.HasTemperature && m.DominantBiome != null;
                statisticsNote.text = complete
                    ? "Recorded when the world was saved. Mean temperature: annual mean over the whole surface."
                    : "Some characteristics were not recorded with this save. Enter the world, then use Refresh Preview (or save it).";
                previewPlaceholder.text = "NO PREVIEW";
                Texture2D texture = cache != null && cache.IsResolved(e.PlanetId) ? cache.Get(e.PlanetId) : null;
                ShowPreview(texture);
                if (cache != null && !cache.IsResolved(e.PlanetId))
                {
                    Enqueue(e.PlanetId);
                }
            }
            enterButton.interactable = valid;
            enterButton.GetComponentInChildren<Text>().text = current && !dirty ? "RETURN TO WORLD" : "ENTER WORLD";
            renameButton.interactable = valid;
            duplicateButton.interactable = valid;
            deleteButton.interactable = e != null;
            refreshPreviewButton.interactable = valid && current && !dirty && context.Updater != null;
        }

        static string FormatDate(DateTime utc)
        {
            return utc == DateTime.MinValue || utc == default ? "—"
                : utc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ Actions

        /// <summary>
        /// Enters the selected world: unsaved changes need an explicit confirmation; the current, unmodified world
        /// is simply returned to (no regeneration). A successful load closes the library.
        /// </summary>
        public void EnterSelected()
        {
            WorldArchiveEntry e = Selected;
            if (e == null || !e.IsValid)
            {
                return;
            }
            WorldSession session = context.Session;
            bool dirty = session.RefreshDirty();
            if (IsCurrent(e) && !dirty)
            {
                Close();
                return;
            }
            if (dirty)
            {
                ShowPrompt("Discard the unsaved changes of '" + session.DisplayName + "' and enter '" + e.DisplayName + "'?",
                    false, null, "Discard & Enter", _ => Load(e));
                return;
            }
            Load(e);
        }

        void Load(WorldArchiveEntry e)
        {
            context.BeforeChange?.Invoke();
            WorldOperationResult result = context.Session.Load(e.PlanetId);
            if (result.Success)
            {
                context.AfterChange?.Invoke(result);
                Close();
                return;
            }
            Report(result);
            Reload();
        }

        /// <summary>Discovers a new world (new seed, current settings), protecting unsaved changes.</summary>
        public void CreateWorld()
        {
            if (context.CreateWorld == null)
            {
                return;
            }
            WorldSession session = context.Session;
            if (session.RefreshDirty())
            {
                ShowPrompt("Discard the unsaved changes of '" + session.DisplayName + "' and discover a new world?", false, null,
                    "Discard & Create", _ => DoCreateWorld());
                return;
            }
            DoCreateWorld();
        }

        void DoCreateWorld()
        {
            context.BeforeChange?.Invoke();
            context.CreateWorld();
            context.AfterChange?.Invoke(new WorldOperationResult { Success = true, Message = "New world discovered (not saved yet)" });
            Close();
        }

        public void RenameSelected()
        {
            WorldArchiveEntry e = Selected;
            if (e == null || !e.IsValid)
            {
                return;
            }
            ShowPrompt("Rename '" + e.DisplayName + "' to:", true, e.DisplayName, "Rename", name =>
            {
                Report(context.Session.Rename(e.PlanetId, name));
                Reload();
            });
        }

        public void DuplicateSelected()
        {
            WorldArchiveEntry e = Selected;
            if (e == null || !e.IsValid)
            {
                return;
            }
            WorldOperationResult result = context.Session.Duplicate(e.PlanetId);
            Report(result);
            Reload();
            if (result.Success)
            {
                Select(result.PlanetId);
            }
        }

        public void DeleteSelected()
        {
            WorldArchiveEntry e = Selected;
            if (e == null)
            {
                return;
            }
            string name = e.IsValid ? "'" + e.DisplayName + "'" : "this unreadable archive";
            ShowPrompt("Delete " + name + " permanently? Its preview is removed too. This cannot be undone.", false, null, "Delete", _ =>
            {
                int index = model.IndexOf(e.PlanetId);
                WorldOperationResult result = context.Session.Delete(e.PlanetId);
                Report(result);
                if (result.Success)
                {
                    cache?.Invalidate(e.PlanetId);
                }
                Reload();
                SelectIndex(Mathf.Min(index, model.View.Count - 1), true);
            });
        }

        /// <summary>Re-records the statistics and recaptures the preview of the current, saved, unmodified world.</summary>
        public void RefreshPreview()
        {
            WorldArchiveEntry e = Selected;
            if (e == null || !IsCurrent(e) || context.Updater == null)
            {
                return;
            }
            WorldOperationResult result = context.Session.RefreshMetadata();
            if (result.Success && context.Updater.RequestCurrent())
            {
                result.Message = "Statistics recorded; the preview is captured in a moment.";
            }
            Report(result);
            Reload();
        }

        /// <summary>Reads the archive list again (after an operation), keeping the selection when possible.</summary>
        public void Reload()
        {
            Guid keep = selectedId;
            model.Reload(context.Session.Store);
            int index = model.IndexOf(keep);
            selectedId = Guid.Empty;
            SelectIndex(index >= 0 ? index : Mathf.Min(page * PageSize, model.View.Count - 1), true);
            RefreshHeader();
        }

        void Report(WorldOperationResult result)
        {
            string text = result.Message;
            if (result.Warnings.Count > 0)
            {
                text += "\n" + string.Join("\n", result.Warnings);
                foreach (string warning in result.Warnings)
                {
                    Debug.LogWarning("Planetwalker world library: " + warning);
                }
            }
            SetMessage(text, !result.Success || result.Warnings.Count > 0);
            if (!result.Success)
            {
                Debug.LogWarning("Planetwalker world library: " + result.Message);
            }
        }

        void SetMessage(string text, bool warning)
        {
            messageLabel.text = text;
            messageLabel.color = warning ? WarningColor : MutedTextColor;
        }

        // ------------------------------------------------------------------ Prompt

        void ShowPrompt(string message, bool withInput, string initial, string confirmCaption, Action<string> action)
        {
            promptAction = action;
            promptLabel.text = message;
            promptLabel.gameObject.SetActive(true);
            promptConfirm.GetComponentInChildren<Text>().text = confirmCaption;
            promptField.gameObject.SetActive(withInput);
            promptField.SetTextWithoutNotify(initial ?? "");
            promptRow.gameObject.SetActive(true);
            if (withInput && Application.isPlaying)
            {
                promptField.Select();
                promptField.ActivateInputField();
            }
        }

        public void ConfirmPrompt()
        {
            Action<string> action = promptAction;
            string text = promptField.text;
            HidePrompt();
            action?.Invoke(text);
        }

        public void HidePrompt()
        {
            promptAction = null;
            if (promptRow != null)
            {
                promptRow.gameObject.SetActive(false);
                promptLabel.gameObject.SetActive(false);
            }
        }

        /// <summary>Sets the prompt's text field (tests: typing a new name).</summary>
        public void SetPromptText(string text)
        {
            promptField.SetTextWithoutNotify(text);
        }

        void OnPromptEndEdit(string _)
        {
            Keyboard keyboard = Keyboard.current;
            if (promptAction != null && keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
            {
                ConfirmPrompt();
            }
        }

        // ------------------------------------------------------------------ Layout

        void Build(RectTransform root, Font font)
        {
            var backdrop = root.gameObject.AddComponent<Image>();
            backdrop.color = BackdropColor; // also blocks the orbit camera while the library is open

            window = Rect("Window", root);
            window.anchorMin = Vector2.zero;
            window.anchorMax = Vector2.one;
            window.offsetMin = new Vector2(48f, 40f);
            window.offsetMax = new Vector2(-48f, -40f);
            window.gameObject.AddComponent<Image>().color = WindowColor;
            RectTransform accent = Rect("Accent Line", window);
            Anchor(accent, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -2f), Vector2.zero);
            accent.gameObject.AddComponent<Image>().color = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.55f);

            // Header.
            Text brand = MakeText(window, font, "PLANETWALKER", 12, FontStyle.Bold, AccentColor);
            Anchor(brand.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(28f, -36f), new Vector2(0f, -16f));
            Text title = MakeText(window, font, "WORLD LIBRARY", 26, FontStyle.Bold, TextColor);
            Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(28f, -70f), new Vector2(0f, -36f));
            closeButton = MakeButton(window, font, "Close  (Esc)", 14);
            Anchor((RectTransform)closeButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-148f, -56f), new Vector2(-28f, -22f));
            newWorldButton = MakeButton(window, font, "New World", 14);
            Anchor((RectTransform)newWorldButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-292f, -56f), new Vector2(-160f, -22f));

            // Toolbar.
            searchField = PlanetGenerationPanel.CreateInputField(window, font);
            searchField.gameObject.name = "Search";
            searchField.contentType = InputField.ContentType.Standard;
            searchField.lineType = InputField.LineType.SingleLine;
            searchField.characterLimit = PlanetArchive.MaxNameLength;
            ((Text)searchField.placeholder).text = "Search worlds by name or seed...";
            RemoveComponent(searchField.GetComponent<LayoutElement>());
            Anchor((RectTransform)searchField.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -118f), new Vector2(428f, -84f));
            sortButton = MakeButton(window, font, "", 14);
            sortLabel = sortButton.GetComponentInChildren<Text>();
            sortLabel.text = "Sort: " + WorldLibraryModel.SortLabel(WorldSortMode.RecentlyModified) + "  ▼";
            Anchor((RectTransform)sortButton.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(440f, -118f), new Vector2(690f, -84f));
            countLabel = MakeText(window, font, "", 13, FontStyle.Normal, MutedTextColor);
            Anchor(countLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(706f, -118f), new Vector2(-(DetailsWidth + 52f), -84f));

            // Grid area (left).
            float gridRight = -(DetailsWidth + 28f + 24f);
            Text section = MakeText(window, font, "DISCOVERED WORLDS", 13, FontStyle.Bold, AccentColor);
            Anchor(section.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -150f), new Vector2(gridRight, -130f));
            RectTransform viewport = Rect("Grid Viewport", window);
            Anchor(viewport, Vector2.zero, Vector2.one, new Vector2(28f, 64f), new Vector2(gridRight, -158f));
            viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f); // scroll target
            viewport.gameObject.AddComponent<RectMask2D>();
            gridContent = Rect("Grid", viewport);
            gridContent.anchorMin = new Vector2(0f, 1f);
            gridContent.anchorMax = new Vector2(1f, 1f);
            gridContent.pivot = new Vector2(0.5f, 1f);
            gridContent.offsetMin = gridContent.offsetMax = Vector2.zero;
            var grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(CardWidth, CardHeight);
            grid.spacing = new Vector2(CardSpacing, CardSpacing);
            grid.padding = new RectOffset(2, 2, 2, 2);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;
            gridContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            gridScroll = viewport.gameObject.AddComponent<ScrollRect>();
            gridScroll.content = gridContent;
            gridScroll.viewport = viewport;
            gridScroll.horizontal = false;
            gridScroll.movementType = ScrollRect.MovementType.Clamped;
            gridScroll.scrollSensitivity = 30f;
            placeholderTexture = CreatePlaceholderTexture();
            for (int i = 0; i < PageSize; i++)
            {
                cards.Add(BuildCard(gridContent, font));
            }
            emptyLabel = MakeText(viewport, font, "", 15, FontStyle.Italic, MutedTextColor);
            emptyLabel.alignment = TextAnchor.MiddleCenter;
            Anchor(emptyLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // Pager.
            previousButton = MakeButton(window, font, "‹  Previous", 14);
            Anchor((RectTransform)previousButton.transform, Vector2.zero, Vector2.zero, new Vector2(28f, 18f), new Vector2(158f, 50f));
            pageLabel = MakeText(window, font, "Page 1 / 1", 13, FontStyle.Normal, MutedTextColor);
            pageLabel.alignment = TextAnchor.MiddleCenter;
            Anchor(pageLabel.rectTransform, Vector2.zero, Vector2.zero, new Vector2(166f, 18f), new Vector2(296f, 50f));
            nextButton = MakeButton(window, font, "Next  ›", 14);
            Anchor((RectTransform)nextButton.transform, Vector2.zero, Vector2.zero, new Vector2(304f, 18f), new Vector2(434f, 50f));
            Text hint = MakeText(window, font, "Click: select  ·  Double-click / Enter: enter world  ·  Arrows, Page Up/Down: navigate  ·  Esc: close",
                11, FontStyle.Italic, MutedTextColor);
            Anchor(hint.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(450f, 18f), new Vector2(gridRight, 50f));

            BuildDetails(font);

            searchField.onValueChanged.AddListener(SetQuery);
            sortButton.onClick.AddListener(CycleSort);
            closeButton.onClick.AddListener(Close);
            newWorldButton.onClick.AddListener(CreateWorld);
            previousButton.onClick.AddListener(() => ShowPage(page - 1));
            nextButton.onClick.AddListener(() => ShowPage(page + 1));
            enterButton.onClick.AddListener(EnterSelected);
            renameButton.onClick.AddListener(RenameSelected);
            duplicateButton.onClick.AddListener(DuplicateSelected);
            deleteButton.onClick.AddListener(DeleteSelected);
            refreshPreviewButton.onClick.AddListener(RefreshPreview);
            promptConfirm.onClick.AddListener(ConfirmPrompt);
            promptCancel.onClick.AddListener(HidePrompt);
            promptField.onEndEdit.AddListener(OnPromptEndEdit);
        }

        void BuildDetails(Font font)
        {
            RectTransform frame = Rect("Details", window);
            Anchor(frame, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-(DetailsWidth + 28f), 18f), new Vector2(-28f, -84f));
            frame.gameObject.AddComponent<Image>().color = DetailsColor;
            frame.gameObject.AddComponent<RectMask2D>();
            RectTransform content = Rect("Content", frame);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(22, 22, 20, 20);
            layout.spacing = 5f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = frame.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = frame;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            RectTransform previewBox = Rect("Preview Box", content);
            previewBox.gameObject.AddComponent<LayoutElement>().preferredHeight = PreviewSize + 8f;
            preview = Rect("Preview", previewBox).gameObject.AddComponent<RawImage>();
            Anchor(preview.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-PreviewSize / 2f, -PreviewSize), new Vector2(PreviewSize / 2f, 0f));
            preview.raycastTarget = false;
            previewPlaceholder = MakeText(preview.rectTransform, font, "NO PREVIEW", 13, FontStyle.Bold, MutedTextColor);
            previewPlaceholder.alignment = TextAnchor.MiddleCenter;
            Anchor(previewPlaceholder.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            detailName = Wrapped(PlanetGenerationPanel.CreateLabel(content, font, "", 22, FontStyle.Bold, TextColor, 28f), 28f);
            detailType = PlanetGenerationPanel.CreateLabel(content, font, "", 11, FontStyle.Bold, AccentColor, 16f);
            detailState = Wrapped(PlanetGenerationPanel.CreateLabel(content, font, "", 12, FontStyle.Bold, AccentColor, 16f), 16f);
            Spacer(content, 4f);
            seedValue = InfoRow(content, font, "SEED");
            createdValue = InfoRow(content, font, "CREATED");
            modifiedValue = InfoRow(content, font, "LAST MODIFIED");
            idValue = InfoRow(content, font, "WORLD ID");
            idValue.fontSize = 11;
            Spacer(content, 6f);
            characteristics = Rect("Characteristics", content);
            var block = characteristics.gameObject.AddComponent<VerticalLayoutGroup>();
            block.spacing = 5f;
            block.childControlWidth = true;
            block.childControlHeight = true;
            block.childForceExpandWidth = true;
            block.childForceExpandHeight = false;
            PlanetGenerationPanel.CreateLabel(characteristics, font, "PLANETARY CHARACTERISTICS", 12, FontStyle.Bold, AccentColor, 18f);
            landValue = InfoRow(characteristics, font, "LAND COVERAGE");
            oceanValue = InfoRow(characteristics, font, "OCEAN COVERAGE");
            temperatureValue = InfoRow(characteristics, font, "MEAN TEMPERATURE");
            biomeValue = InfoRow(characteristics, font, "DOMINANT BIOME");
            statisticsNote = Wrapped(PlanetGenerationPanel.CreateLabel(content, font, "", 11, FontStyle.Italic, MutedTextColor, 14f), 14f);
            Spacer(content, 6f);

            promptLabel = Wrapped(PlanetGenerationPanel.CreateLabel(content, font, "", 13, FontStyle.Bold, WarningColor, 18f), 18f);
            promptRow = PlanetGenerationPanel.CreateRow(content, "Prompt Row");
            promptField = PlanetGenerationPanel.CreateInputField(promptRow, font);
            promptField.gameObject.name = "Prompt Input";
            promptField.contentType = InputField.ContentType.Standard;
            promptField.lineType = InputField.LineType.SingleLine;
            promptField.characterLimit = PlanetArchive.MaxNameLength;
            ((Text)promptField.placeholder).text = "World name";
            promptConfirm = PlanetGenerationPanel.CreateButton(promptRow, font, "Confirm");
            promptCancel = PlanetGenerationPanel.CreateButton(promptRow, font, "Cancel");
            foreach (Button b in new[] { promptConfirm, promptCancel })
            {
                b.GetComponentInChildren<Text>().fontSize = 13;
                LayoutElement element = b.GetComponent<LayoutElement>();
                element.flexibleWidth = 0f;
                element.preferredWidth = b == promptConfirm ? 130f : 76f;
            }
            promptRow.gameObject.SetActive(false);
            promptLabel.gameObject.SetActive(false);

            enterButton = PlanetGenerationPanel.CreateButton(content, font, "ENTER WORLD");
            enterButton.GetComponent<LayoutElement>().preferredHeight = 44f;
            enterButton.GetComponent<Image>().color = AccentColor;
            Text enterText = enterButton.GetComponentInChildren<Text>();
            enterText.fontStyle = FontStyle.Bold;
            enterText.color = PrimaryTextColor;
            enterText.fontSize = 17;
            ColorBlock colors = enterButton.colors;
            colors.disabledColor = new Color(0.35f, 0.35f, 0.38f, 0.6f);
            enterButton.colors = colors;

            RectTransform secondary = PlanetGenerationPanel.CreateRow(content, "Secondary Actions");
            renameButton = PlanetGenerationPanel.CreateButton(secondary, font, "Rename");
            duplicateButton = PlanetGenerationPanel.CreateButton(secondary, font, "Duplicate");
            deleteButton = PlanetGenerationPanel.CreateButton(secondary, font, "Delete");
            refreshPreviewButton = PlanetGenerationPanel.CreateButton(content, font, "Refresh Preview");
            foreach (Button b in new[] { renameButton, duplicateButton, deleteButton, refreshPreviewButton })
            {
                Text t = b.GetComponentInChildren<Text>();
                t.fontSize = 13;
                t.color = MutedTextColor;
            }
            refreshPreviewButton.GetComponent<LayoutElement>().preferredHeight = 24f;
            messageLabel = Wrapped(PlanetGenerationPanel.CreateLabel(content, font, "", 12, FontStyle.Normal, MutedTextColor, 16f), 16f);
        }

        Card BuildCard(RectTransform parent, Font font)
        {
            var card = new Card();
            card.Root = Rect("World Card", parent);
            card.Border = card.Root.gameObject.AddComponent<Image>();
            card.Border.color = CardBorderColor;
            var outline = card.Root.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.45f);
            outline.effectDistance = new Vector2(2f, -2f);
            outline.enabled = false;
            var button = card.Root.gameObject.AddComponent<Button>();
            button.targetGraphic = card.Border;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.5f, 1.5f, 1.55f, 1f);
            colors.selectedColor = Color.white;
            colors.pressedColor = new Color(0.8f, 0.8f, 0.85f, 1f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => OnCardClicked(card));

            RectTransform inner = Rect("Inner", card.Root);
            Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1.5f, 1.5f), new Vector2(-1.5f, -1.5f));
            Image innerImage = inner.gameObject.AddComponent<Image>();
            innerImage.color = CardColor;
            innerImage.raycastTarget = false;

            card.Thumb = Rect("Thumbnail", inner).gameObject.AddComponent<RawImage>();
            card.Thumb.raycastTarget = false;
            Anchor(card.Thumb.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-ThumbSize / 2f, -12f - ThumbSize), new Vector2(ThumbSize / 2f, -12f));
            card.Placeholder = MakeText(card.Thumb.rectTransform, font, "NO PREVIEW", 11, FontStyle.Bold, MutedTextColor);
            card.Placeholder.alignment = TextAnchor.MiddleCenter;
            Anchor(card.Placeholder.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            card.Name = MakeText(inner, font, "", 15, FontStyle.Bold, TextColor);
            card.Name.horizontalOverflow = HorizontalWrapMode.Wrap;
            card.Name.verticalOverflow = VerticalWrapMode.Truncate;
            Anchor(card.Name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -212f), new Vector2(-10f, -192f));
            card.Info = MakeText(inner, font, "", 11, FontStyle.Normal, MutedTextColor);
            Anchor(card.Info.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -230f), new Vector2(-10f, -214f));
            card.Tag = MakeText(inner, font, "", 11, FontStyle.Bold, AccentColor);
            Anchor(card.Tag.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -248f), new Vector2(-10f, -232f));
            card.Root.gameObject.SetActive(false);
            return card;
        }

        static Text InfoRow(RectTransform parent, Font font, string caption)
        {
            RectTransform row = PlanetGenerationPanel.CreateRow(parent, caption + " Row");
            row.GetComponent<LayoutElement>().preferredHeight = 20f;
            PlanetGenerationPanel.CreateLabel(row, font, caption, 11, FontStyle.Bold, MutedTextColor, 20f, 150f);
            Text value = PlanetGenerationPanel.CreateLabel(row, font, "—", 14, FontStyle.Normal, TextColor, 20f);
            value.GetComponent<LayoutElement>().flexibleWidth = 1f;
            return value;
        }

        static Text Wrapped(Text label, float minHeight)
        {
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            LayoutElement element = label.GetComponent<LayoutElement>();
            element.preferredHeight = -1f;
            element.minHeight = minHeight;
            return label;
        }

        static void Spacer(RectTransform parent, float height)
        {
            PlanetGenerationPanel.CreateSpacer(parent, height);
        }

        static Button MakeButton(RectTransform parent, Font font, string caption, int size)
        {
            Button button = PlanetGenerationPanel.CreateButton(parent, font, caption);
            RemoveComponent(button.GetComponent<LayoutElement>());
            button.GetComponentInChildren<Text>().fontSize = size;
            return button;
        }

        static Text MakeText(RectTransform parent, Font font, string text, int size, FontStyle style, Color color)
        {
            var label = Rect("Text", parent).gameObject.AddComponent<Text>();
            label.font = font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            return label;
        }

        static RectTransform Rect(string name, Transform parent) => PlanetGenerationPanel.CreateRect(name, parent);

        static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        /// <summary>A soft planetary ring on a dark disc: the "no preview" image (built once, owned by the panel).</summary>
        static Texture2D CreatePlaceholderTexture()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "World Preview Placeholder",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float disc = Mathf.Clamp01((0.62f - r) * 60f) * 0.35f;
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.62f) * 70f) * 0.9f;
                    float v = Mathf.Max(disc, ring);
                    byte b = (byte)(255f * Mathf.Clamp01(0.55f + 0.45f * v));
                    pixels[y * size + x] = new Color32(b, b, b, (byte)(255f * Mathf.Clamp01(0.25f + v)));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        static Color AccentColor => PlanetGenerationPanel.AccentColor;
        static Color TextColor => PlanetGenerationPanel.TextColor;
        static Color MutedTextColor => PlanetGenerationPanel.MutedTextColor;

        static void DestroySafe(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        /// <summary>Removes a layout element added by the shared helpers (manually anchored controls), before any layout pass.</summary>
        static void RemoveComponent(Component component)
        {
            if (component != null)
            {
                DestroyImmediate(component);
            }
        }
    }
}
