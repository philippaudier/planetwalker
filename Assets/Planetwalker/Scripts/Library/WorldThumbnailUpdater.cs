using System;
using Planetwalker.Core;
using Planetwalker.Persistence;
using Planetwalker.Rendering;

namespace Planetwalker.Library
{
    public enum ThumbnailCaptureOutcome
    {
        None,
        Waiting,
        Captured,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// Keeps world thumbnails in step with saves (V0.1.3B). After a save (or an explicit preview refresh) one
    /// capture is scheduled; it happens a couple of frames later, only once the displayed planet matches the
    /// saved configuration (terrain regenerated, settings applied, Natural Planet view). If the world changes
    /// before that, the capture is cancelled: an archive's thumbnail never shows unsaved changes. Failures are
    /// non-fatal warnings and keep the previous thumbnail. Nothing else ever triggers a capture (camera, clouds,
    /// solar clock, opening the library...).
    /// </summary>
    public sealed class WorldThumbnailUpdater
    {
        /// <summary>Frames between the request and the first capture attempt (the display catches up with the settings).</summary>
        public const int SettleFrames = 2;
        /// <summary>While waiting (debug view shown, regeneration pending), the state is re-checked every this many frames.</summary>
        public const int WaitCheckFrames = 15;

        readonly PlanetVisualController visual;
        readonly WorldSession session;
        readonly WorldThumbnailStore store;
        Guid pendingId;
        WorldConfiguration pendingConfiguration;
        int framesToWait;

        public WorldThumbnailUpdater(PlanetVisualController visual, WorldSession session, WorldThumbnailStore store)
        {
            this.visual = visual;
            this.session = session;
            this.store = store;
            session.Saved += Request;
        }

        /// <summary>Renders the PNG; replaceable (tests simulate failures).</summary>
        public Func<PlanetVisualController, int, byte[]> Capture { get; set; } = WorldThumbnailRenderer.CapturePng;

        public int Size { get; set; } = WorldThumbnailRenderer.DefaultSize;

        public bool HasPending => pendingId != Guid.Empty;
        public Guid PendingId => pendingId;

        /// <summary>Thumbnails written (one per completed capture).</summary>
        public int CaptureCount { get; private set; }

        public ThumbnailCaptureStats LastStats { get; private set; }

        /// <summary>Total wall time of the last capture including the file write (milliseconds).</summary>
        public double LastTotalMilliseconds { get; private set; }

        /// <summary>A thumbnail file changed (planet ID): cached textures of it are stale.</summary>
        public event Action<Guid> ThumbnailUpdated;

        /// <summary>A non-fatal problem (the save itself always stands).</summary>
        public event Action<string> Warning;

        /// <summary>Unsubscribes from the session (the updater is replaced or discarded).</summary>
        public void Detach()
        {
            session.Saved -= Request;
        }

        /// <summary>Schedules one capture of a world that has just been saved with this configuration.</summary>
        public void Request(Guid planetId, WorldConfiguration savedConfiguration)
        {
            pendingId = planetId;
            pendingConfiguration = savedConfiguration;
            framesToWait = SettleFrames;
        }

        /// <summary>Schedules a capture of the current, saved, unmodified world (Refresh Preview).</summary>
        public bool RequestCurrent()
        {
            WorldConfiguration saved = session.SavedConfiguration;
            if (!session.HasArchive || saved == null || session.RefreshDirty())
            {
                return false;
            }
            Request(session.PlanetId, saved);
            return true;
        }

        /// <summary>Per frame (Play mode). Free when nothing is pending.</summary>
        public void Tick()
        {
            if (pendingId == Guid.Empty)
            {
                return;
            }
            if (framesToWait > 0)
            {
                framesToWait--;
                return;
            }
            if (TryCompletePending() == ThumbnailCaptureOutcome.Waiting)
            {
                framesToWait = WaitCheckFrames;
            }
        }

        /// <summary>Attempts the pending capture now (tests, and Tick).</summary>
        public ThumbnailCaptureOutcome TryCompletePending()
        {
            if (pendingId == Guid.Empty)
            {
                return ThumbnailCaptureOutcome.None;
            }
            Guid id = pendingId;
            if (session.PlanetId != id || !session.CaptureConfiguration().ContentEquals(pendingConfiguration))
            {
                Clear();
                Warning?.Invoke("Preview not updated: the world changed before it could be captured (save again to refresh it).");
                return ThumbnailCaptureOutcome.Cancelled;
            }
            PlanetInstance planet = visual.Planet;
            if (visual.DebugView != PlanetDebugView.None || planet == null || !planet.Definition.TerrainEquals(pendingConfiguration.Terrain)
                || planet.Definition.resolution != pendingConfiguration.Terrain.resolution || planet.Definition.radius != pendingConfiguration.Terrain.radius)
            {
                return ThumbnailCaptureOutcome.Waiting; // a scientific view is shown or the regeneration is still pending
            }
            Clear();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] png;
            try
            {
                png = Capture(visual, Size);
            }
            catch (Exception e)
            {
                Warning?.Invoke("Preview not updated: capture failed (" + e.Message + "). The world is saved.");
                return ThumbnailCaptureOutcome.Failed;
            }
            string error = store.Write(id, png);
            if (error != null)
            {
                Warning?.Invoke("Preview not updated: " + error + ". The world is saved; the previous preview is kept.");
                return ThumbnailCaptureOutcome.Failed;
            }
            LastStats = WorldThumbnailRenderer.LastStats;
            LastTotalMilliseconds = watch.Elapsed.TotalMilliseconds;
            CaptureCount++;
            ThumbnailUpdated?.Invoke(id);
            return ThumbnailCaptureOutcome.Captured;
        }

        void Clear()
        {
            pendingId = Guid.Empty;
            pendingConfiguration = null;
        }
    }
}
