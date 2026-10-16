using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Persistence
{
    /// <summary>
    /// What the world session needs from the running application: the live, authoritative settings objects,
    /// one regeneration entry point and the orbit camera. Implemented by <c>PlanetBootstrap</c>.
    /// </summary>
    public interface IWorldHost
    {
        /// <summary>The editable planet definition (authoritative terrain settings and planet seed).</summary>
        PlanetDefinition Definition { get; }

        /// <summary>Owner of every other settings group (hydrology ... clouds, solar).</summary>
        PlanetVisualController Visual { get; }

        /// <summary>Generates the planet from the current settings (one full regeneration).</summary>
        void Regenerate();

        /// <summary>Current camera view, or null when there is no orbit camera.</summary>
        CameraPresentation CaptureCamera();

        /// <summary>Restores a camera view (clamped to the camera's limits), without disturbing its controller.</summary>
        void RestoreCamera(CameraPresentation camera);
    }

    /// <summary>Result of a session operation, with what the UI reports.</summary>
    public sealed class WorldOperationResult
    {
        public bool Success;
        public string Message;
        /// <summary>Archive the operation produced or affected (save, save as new, duplicate).</summary>
        public Guid PlanetId;
        public IReadOnlyList<string> Warnings = Array.Empty<string>();
        public long Bytes;
        public double Milliseconds;
        public double ReadMilliseconds;
        public double RegenerationMilliseconds;
        public int Regenerations;
        public GeneratorCompatibility Compatibility;

        public static WorldOperationResult Fail(string message) => new WorldOperationResult { Message = message };
    }

    /// <summary>
    /// The current world (V0.1.3A): its identity (planet ID, name, creation time; none until first saved),
    /// the configuration it was last saved or loaded with (the unsaved-changes baseline), and the save /
    /// load / rename / duplicate / delete actions. Only persistent configuration counts as a change: camera,
    /// solar time, cloud animation, debug views and contemplation mode never mark the world modified.
    /// </summary>
    public sealed class WorldSession
    {
        /// <summary>After a configuration touch, the dirty state is re-evaluated for this long (seconds),
        /// covering the panel's delayed slider application.</summary>
        public const float TouchWindow = 1.5f;
        /// <summary>Minimum interval between two re-evaluations while touched (seconds).</summary>
        public const float CheckInterval = 0.2f;

        readonly IWorldHost host;
        WorldConfiguration baseline;
        bool archiveLost;
        float touchedUntil = -1f;
        float nextCheck;

        public WorldSession(IWorldHost host, WorldArchiveStore store, WorldThumbnailStore thumbnails = null)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Thumbnails = thumbnails;
        }

        public WorldArchiveStore Store { get; }

        /// <summary>World thumbnails (V0.1.3B), kept in step with duplicate / delete; null when not used.</summary>
        public WorldThumbnailStore Thumbnails { get; }

        /// <summary>
        /// Raised after every successful save (planet ID, saved configuration): the thumbnail updater captures the
        /// world's picture once the display matches that configuration.
        /// </summary>
        public event Action<Guid, WorldConfiguration> Saved;

        /// <summary>A copy of the configuration last saved or loaded (the unsaved-changes baseline), or null.</summary>
        public WorldConfiguration SavedConfiguration => baseline?.Clone();

        /// <summary>Planet ID of the current world's archive, or empty when it was never saved.</summary>
        public Guid PlanetId { get; private set; }
        public bool HasArchive => PlanetId != Guid.Empty;
        public string DisplayName { get; private set; } = PlanetArchive.DefaultName;
        public DateTime CreatedUtc { get; private set; }
        public DateTime ModifiedUtc { get; private set; }
        public GeneratorCompatibility Compatibility { get; private set; } = GeneratorCompatibility.Exact;

        /// <summary>True when the persistent configuration differs from the baseline (last save / load / start).</summary>
        public bool IsDirty { get; private set; }

        /// <summary>Number of dirty re-evaluations (each compares every archived field once).</summary>
        public int DirtyCheckCount { get; private set; }

        /// <summary>Groups in the order the last load applied them (tests, diagnostics).</summary>
        public List<string> LastApplyOrder { get; } = new List<string>();

        /// <summary>Raised when the identity, the name or the dirty state changes.</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ Capture / apply

        /// <summary>A private, sanitized copy of the live persistent configuration (nothing is modified).</summary>
        public WorldConfiguration CaptureConfiguration()
        {
            PlanetVisualController visual = host.Visual;
            var c = new WorldConfiguration();
            SettingsFields.Copy(host.Definition, c.Terrain);
            SettingsFields.Copy(visual.Hydrology, c.Hydrology);
            SettingsFields.Copy(visual.Rivers, c.Rivers);
            SettingsFields.Copy(visual.Lakes, c.Lakes);
            SettingsFields.Copy(visual.Climate, c.Climate);
            SettingsFields.Copy(visual.Biomes, c.Biomes);
            SettingsFields.Copy(visual.Surface, c.Surface);
            SettingsFields.Copy(visual.OceanAtmosphere, c.OceanAtmosphere);
            SettingsFields.Copy(visual.Clouds, c.Clouds);
            c.Lighting = LightingSettings.From(visual.Solar);
            // The live objects are sanitized at their point of use; an archive always holds the effective values.
            c.Terrain.Sanitize();
            c.Hydrology.Sanitize();
            c.Rivers.Sanitize();
            c.Lakes.Sanitize();
            c.Climate.Sanitize();
            c.Biomes.Sanitize();
            c.Surface.Sanitize();
            c.OceanAtmosphere.Sanitize();
            c.Clouds.Sanitize();
            c.Lighting.Sanitize();
            return c;
        }

        /// <summary>The current camera view and solar clock state (read only).</summary>
        public PresentationState CapturePresentation()
        {
            return new PresentationState { Camera = host.CaptureCamera(), Solar = SolarPresentation.From(host.Visual.Solar) };
        }

        /// <summary>
        /// Copies a configuration into the live settings objects, group by group in a fixed order (terrain,
        /// analyses, appearance). Plain field writes: nothing regenerates until <see cref="IWorldHost.Regenerate"/>.
        /// </summary>
        void ApplyConfiguration(WorldConfiguration c)
        {
            PlanetVisualController visual = host.Visual;
            LastApplyOrder.Clear();
            Apply("terrain", c.Terrain, host.Definition);
            Apply("hydrology", c.Hydrology, visual.Hydrology);
            Apply("rivers", c.Rivers, visual.Rivers);
            Apply("lakes", c.Lakes, visual.Lakes);
            Apply("climate", c.Climate, visual.Climate);
            Apply("biomes", c.Biomes, visual.Biomes);
            Apply("surface", c.Surface, visual.Surface);
            Apply("oceanAtmosphere", c.OceanAtmosphere, visual.OceanAtmosphere);
            Apply("clouds", c.Clouds, visual.Clouds);
            c.Lighting.ApplyTo(visual.Solar);
            LastApplyOrder.Add("lighting");
        }

        void Apply(string group, object source, object live)
        {
            SettingsFields.Copy(source, live);
            LastApplyOrder.Add(group);
        }

        /// <summary>Restores camera and solar clock (after the regeneration). Absent parts are left as they are.</summary>
        void ApplyPresentation(PresentationState p)
        {
            if (p == null)
            {
                return;
            }
            PlanetVisualController visual = host.Visual;
            if (p.Solar != null)
            {
                // Stop first (keeps the anchor consistent), then set duration and time, then the run state.
                visual.SetDayNightCycle(false);
                visual.SetSolarPaused(false);
                visual.SetCycleDuration(p.Solar.cycleDuration);
                visual.SetSolarTime(p.Solar.solarTime);
                visual.SetSolarPaused(p.Solar.paused);
                visual.SetDayNightCycle(p.Solar.dayNightCycle);
            }
            if (p.Camera != null)
            {
                host.RestoreCamera(p.Camera);
            }
            LastApplyOrder.Add("presentation");
        }

        // ------------------------------------------------------------------ Dirty tracking

        /// <summary>Takes the current configuration as the unsaved-changes baseline (startup: a new, unsaved world).</summary>
        public void ResetBaseline()
        {
            baseline = CaptureConfiguration();
            archiveLost = false;
            SetDirty(false);
        }

        /// <summary>Compares the live configuration with the baseline now (one pass over every archived field).</summary>
        public bool RefreshDirty()
        {
            if (baseline == null)
            {
                ResetBaseline();
                return false;
            }
            DirtyCheckCount++;
            SetDirty(archiveLost || !baseline.ContentEquals(CaptureConfiguration()));
            return IsDirty;
        }

        /// <summary>Fields that differ from the baseline (diagnostics).</summary>
        public List<string> UnsavedFields()
        {
            return baseline == null ? new List<string>() : baseline.Differences(CaptureConfiguration());
        }

        /// <summary>
        /// Something that may change the persistent configuration happened (a control was used, the inspector
        /// was edited, the planet regenerated): re-evaluate the dirty state for a short while. Nothing is
        /// compared while untouched, so an animating planet costs nothing here.
        /// </summary>
        public void Touch(float now)
        {
            touchedUntil = now + TouchWindow;
        }

        /// <summary>Per frame (cheap): runs a throttled comparison only inside a touch window.</summary>
        public void Tick(float now)
        {
            if (now > touchedUntil || now < nextCheck)
            {
                return;
            }
            nextCheck = now + CheckInterval;
            RefreshDirty();
        }

        void SetDirty(bool dirty)
        {
            if (dirty != IsDirty)
            {
                IsDirty = dirty;
                Changed?.Invoke();
            }
        }

        // ------------------------------------------------------------------ Actions

        /// <summary>
        /// Saves the current world (its archive is replaced; a never-saved world gets a new ID). Captures the
        /// configuration and the current camera and solar time. Never regenerates, never resets the cloud
        /// animation or the solar clock.
        /// </summary>
        public WorldOperationResult Save(string name = null)
        {
            return SaveInternal(HasArchive ? PlanetId : Guid.NewGuid(), name ?? DisplayName, HasArchive ? CreatedUtc : default);
        }

        /// <summary>Saves the current world as a new archive with a new identity; the current world becomes that one.</summary>
        public WorldOperationResult SaveAsNew(string name)
        {
            return SaveInternal(Guid.NewGuid(), name, default);
        }

        WorldOperationResult SaveInternal(Guid id, string name, DateTime created)
        {
            var stopwatch = Stopwatch.StartNew();
            string cleanName = PlanetArchive.CleanName(name);
            DateTime now = PlanetArchiveSerializer.UtcNowTruncated();
            var archive = new PlanetArchive
            {
                PlanetId = id,
                DisplayName = cleanName,
                CreatedUtc = created == default ? now : created,
                ModifiedUtc = now,
                Configuration = CaptureConfiguration(),
                Presentation = CapturePresentation(),
            };
            // Statistics the application already computed for exactly this configuration (never recomputed).
            archive.Metadata = WorldMetadata.Capture(host.Visual, archive.Configuration);
            ArchiveOperationResult saved = Store.Save(archive);
            if (!saved.Success)
            {
                return WorldOperationResult.Fail(saved.Error);
            }
            PlanetId = id;
            DisplayName = cleanName;
            CreatedUtc = archive.CreatedUtc;
            ModifiedUtc = now;
            Compatibility = GeneratorCompatibility.Exact;
            baseline = archive.Configuration.Clone();
            archiveLost = false;
            IsDirty = false;
            Changed?.Invoke();
            stopwatch.Stop();
            Saved?.Invoke(id, archive.Configuration.Clone());
            return new WorldOperationResult
            {
                Success = true,
                Message = "Saved '" + cleanName + "'",
                PlanetId = id,
                Bytes = saved.Bytes,
                Milliseconds = stopwatch.Elapsed.TotalMilliseconds,
            };
        }

        /// <summary>
        /// Loads a world transactionally: the archive is read and fully validated before anything changes
        /// (a bad archive leaves the current planet untouched); then every settings group is applied in a fixed
        /// order, the planet is regenerated once, and the camera and solar time are restored. If the
        /// regeneration fails, the previous configuration is restored and regenerated.
        /// </summary>
        public WorldOperationResult Load(Guid planetId)
        {
            var stopwatch = Stopwatch.StartNew();
            ArchiveOperationResult read = Store.Load(planetId);
            if (!read.Success)
            {
                return WorldOperationResult.Fail("Not loaded: " + read.Error);
            }
            double readMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            PlanetArchive archive = read.Archive;

            WorldConfiguration previous = CaptureConfiguration();
            ApplyConfiguration(archive.Configuration);
            host.Visual.DebugView = PlanetDebugView.None; // a loaded world is presented, not inspected
            var regeneration = Stopwatch.StartNew();
            try
            {
                host.Regenerate();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                ApplyConfiguration(previous);
                try
                {
                    host.Regenerate();
                }
                catch (Exception again)
                {
                    UnityEngine.Debug.LogException(again);
                }
                return WorldOperationResult.Fail("Not loaded: the planet could not be generated (" + e.Message
                    + "). The previous configuration was restored.");
            }
            regeneration.Stop();
            ApplyPresentation(archive.Presentation);

            PlanetId = archive.PlanetId;
            DisplayName = archive.DisplayName;
            CreatedUtc = archive.CreatedUtc;
            ModifiedUtc = archive.ModifiedUtc;
            Compatibility = read.Read.Compatibility;
            baseline = CaptureConfiguration();
            archiveLost = false;
            IsDirty = false;
            Changed?.Invoke();
            stopwatch.Stop();

            var warnings = new List<string>(read.Read.Report.Warnings);
            // Documented compatibility defaults (V0.2.0C fields missing from older archives) are expected, not warned.
            var defaulted = new List<string>();
            foreach (string field in read.Read.Report.DefaultedFields)
            {
                if (!PlanetArchiveSerializer.IsCompatibilityDefault(field))
                {
                    defaulted.Add(field);
                }
            }
            if (defaulted.Count > 0)
            {
                warnings.Add("Defaults used for " + defaulted.Count + " missing optional field(s): " + string.Join(", ", defaulted) + ".");
            }
            return new WorldOperationResult
            {
                Success = true,
                Message = "Loaded '" + archive.DisplayName + "'"
                    + (Compatibility == GeneratorCompatibility.BestEffort ? " (best-effort: saved by " + archive.GeneratorVersion + ")" : ""),
                Warnings = warnings,
                Bytes = read.Bytes,
                Milliseconds = stopwatch.Elapsed.TotalMilliseconds,
                ReadMilliseconds = readMilliseconds,
                RegenerationMilliseconds = regeneration.Elapsed.TotalMilliseconds,
                Regenerations = 1,
                Compatibility = Compatibility,
            };
        }

        /// <summary>Renames an archive (identity kept, nothing regenerated). Renaming the current world updates its name only.</summary>
        public WorldOperationResult Rename(Guid planetId, string newName)
        {
            string cleanName = PlanetArchive.CleanName(newName);
            ArchiveOperationResult result = Store.Rename(planetId, cleanName);
            if (!result.Success)
            {
                return WorldOperationResult.Fail("Not renamed: " + result.Error);
            }
            if (planetId == PlanetId)
            {
                DisplayName = cleanName;
                ModifiedUtc = result.Archive.ModifiedUtc;
                Changed?.Invoke();
            }
            return new WorldOperationResult { Success = true, Message = "Renamed to '" + cleanName + "'", Milliseconds = result.Milliseconds };
        }

        /// <summary>Names the current world. A saved world is renamed in its archive; an unsaved one keeps the name for its first save.</summary>
        public WorldOperationResult RenameCurrent(string newName)
        {
            if (HasArchive)
            {
                return Rename(PlanetId, newName);
            }
            DisplayName = PlanetArchive.CleanName(newName);
            Changed?.Invoke();
            return new WorldOperationResult { Success = true, Message = "Name set (saved with the world)" };
        }

        /// <summary>Copies an archive under a new identity (nothing loaded or regenerated).</summary>
        public WorldOperationResult Duplicate(Guid planetId)
        {
            ArchiveOperationResult result = Store.Duplicate(planetId);
            if (!result.Success)
            {
                return WorldOperationResult.Fail("Not duplicated: " + result.Error);
            }
            var warnings = new List<string>();
            string thumbnailError = Thumbnails?.Copy(planetId, result.Archive.PlanetId);
            if (thumbnailError != null)
            {
                warnings.Add("The copy has no preview (" + thumbnailError + ").");
            }
            return new WorldOperationResult
            {
                Success = true,
                Message = "Duplicated as '" + result.Archive.DisplayName + "'",
                PlanetId = result.Archive.PlanetId,
                Warnings = warnings,
                Milliseconds = result.Milliseconds,
            };
        }

        /// <summary>Deletes one archive. Deleting the current world's archive keeps the planet on screen as an unsaved world.</summary>
        public WorldOperationResult Delete(Guid planetId)
        {
            ArchiveOperationResult result = Store.Delete(planetId);
            if (!result.Success)
            {
                return WorldOperationResult.Fail(result.Error);
            }
            // Only once the archive is gone: a failed delete keeps the world and its preview together.
            var warnings = new List<string>();
            string thumbnailError = Thumbnails?.Delete(planetId);
            if (thumbnailError != null)
            {
                warnings.Add("The preview file could not be removed (" + thumbnailError + ").");
            }
            if (planetId == PlanetId)
            {
                PlanetId = Guid.Empty;
                archiveLost = true;
                IsDirty = true;
                Changed?.Invoke();
            }
            return new WorldOperationResult { Success = true, Message = "Deleted", Warnings = warnings };
        }

        /// <summary>
        /// Starts a new, unsaved world from the planet now on screen (the caller has just generated it): no
        /// identity, the default name, and the current configuration as the unsaved-changes baseline.
        /// </summary>
        public void BeginNewWorld()
        {
            PlanetId = Guid.Empty;
            DisplayName = PlanetArchive.DefaultName;
            CreatedUtc = default;
            ModifiedUtc = default;
            Compatibility = GeneratorCompatibility.Exact;
            baseline = CaptureConfiguration();
            archiveLost = false;
            IsDirty = false;
            Changed?.Invoke();
        }

        /// <summary>
        /// Records the statistics of the current, saved, unmodified world in its archive (for archives saved
        /// before V0.1.3B or whose statistics were unavailable). Identity, name, timestamps and configuration are
        /// unchanged. Nothing is computed: only values already held for exactly the saved configuration are kept.
        /// </summary>
        public WorldOperationResult RefreshMetadata()
        {
            if (!HasArchive)
            {
                return WorldOperationResult.Fail("Save this world first.");
            }
            if (RefreshDirty())
            {
                return WorldOperationResult.Fail("This world has unsaved changes: save it to update its preview.");
            }
            WorldMetadata metadata = WorldMetadata.Capture(host.Visual, baseline);
            ArchiveOperationResult result = Store.UpdateMetadata(PlanetId, metadata);
            if (!result.Success)
            {
                return WorldOperationResult.Fail("Statistics not updated: " + result.Error);
            }
            return new WorldOperationResult { Success = true, Message = "Preview updated", PlanetId = PlanetId };
        }
    }
}
