using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.Core;
using Planetwalker.Library;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.UI;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// World Library against a live planet (V0.1.3B): real thumbnail captures, when thumbnails are (not) updated,
    /// thumbnails following duplicate / rename / delete, recorded statistics, the library UI (unsaved-change
    /// protection, no regeneration or recomputation while browsing, bounded textures, 100+ worlds).
    /// Isolated temporary folders only.
    /// </summary>
    public class WorldLibrarySessionTests
    {
        readonly List<Object> created = new List<Object>();
        string root;
        WorldArchiveStore store;
        WorldThumbnailStore thumbnails;

        [SetUp]
        public void SetUp()
        {
            root = WorldArchiveTestUtil.CreateTempDirectory();
            store = new WorldArchiveStore(Path.Combine(root, "Worlds"));
            thumbnails = new WorldThumbnailStore(Path.Combine(root, "Thumbnails"));
            Directory.CreateDirectory(store.Directory); // tests may place archive files before the first save
            Assert.AreNotEqual(Path.GetFullPath(PlanetBootstrap.DefaultThumbnailsDirectory), thumbnails.Directory, "Never the user's thumbnails");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }
            created.Clear();
            WorldArchiveTestUtil.DeleteTempDirectory(root);
        }

        sealed class Rig
        {
            public WorldTestHost Host;
            public WorldSession Session;
            public WorldThumbnailUpdater Updater;
            public readonly List<string> Warnings = new List<string>();
        }

        static WorldConfiguration Config(int seed = 536452)
        {
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            return c;
        }

        Rig CreateRig(WorldConfiguration configuration = null, WorldThumbnailStore thumbnailStore = null, WorldArchiveStore archiveStore = null)
        {
            var host = new WorldTestHost(created);
            host.Apply(configuration ?? Config());
            host.Regenerate();
            var rig = new Rig { Host = host };
            rig.Session = new WorldSession(host, archiveStore ?? store, thumbnailStore ?? thumbnails);
            host.Session = rig.Session;
            rig.Session.ResetBaseline();
            rig.Updater = new WorldThumbnailUpdater(host.Visual, rig.Session, thumbnailStore ?? thumbnails);
            rig.Updater.Warning += rig.Warnings.Add;
            return rig;
        }

        /// <summary>Ticks the updater like Play-mode frames until it is idle.</summary>
        static void RunFrames(WorldThumbnailUpdater updater, int frames = 6)
        {
            for (int i = 0; i < frames; i++)
            {
                updater.Tick();
            }
        }

        WorldLibraryPanel CreateLibrary(Rig rig, List<WorldOperationResult> changes = null)
        {
            var canvasObject = new GameObject("Library Test Canvas", typeof(RectTransform));
            created.Add(canvasObject);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            WorldLibraryPanel library = WorldLibraryPanel.Create(canvasObject.transform, font);
            library.Bind(new WorldLibraryContext
            {
                Session = rig.Session,
                Thumbnails = rig.Session.Thumbnails,
                Updater = rig.Updater,
                CreateWorld = () =>
                {
                    rig.Host.Definition.seed += 1;
                    rig.Host.Regenerate();
                    rig.Session.BeginNewWorld();
                },
                AfterChange = r => changes?.Add(r),
            });
            return library;
        }

        static byte[] SmallPng(Color color)
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color32[64 * 64];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);
            return png;
        }

        static Color32 Pixel(Texture2D texture, float u, float v) =>
            texture.GetPixel(Mathf.RoundToInt(u * (texture.width - 1)), Mathf.RoundToInt(v * (texture.height - 1)));

        static float Difference(Color32 a, Color32 b) => (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b)) / 3f;

        Texture2D Decode(byte[] png)
        {
            var texture = new Texture2D(2, 2);
            created.Add(texture);
            Assert.IsTrue(texture.LoadImage(png));
            return texture;
        }

        // ------------------------------------------------------------ Capture

        [Test]
        public void Capture_RendersTheNaturalPlanetInIsolation_WithoutTouchingTheScene()
        {
            Rig rig = CreateRig();
            WorldTestHost host = rig.Host;
            Transform liveCamera = host.Camera.transform;
            Vector3 cameraPosition = liveCamera.position;
            Quaternion cameraRotation = liveCamera.rotation;
            int regenerations = host.Regenerations;
            PlanetInstance planet = host.Current;
            int objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            int textures = Resources.FindObjectsOfTypeAll<Texture>().Length;
            int cameras = Resources.FindObjectsOfTypeAll<Camera>().Length;
            Assert.IsTrue(host.Visual.CloudsVisible && host.Visual.AtmosphereVisible, "Natural Planet with clouds and atmosphere");

            var watch = Stopwatch.StartNew();
            byte[] png = WorldThumbnailRenderer.CapturePng(host.Visual);
            watch.Stop();
            ThumbnailCaptureStats stats = WorldThumbnailRenderer.LastStats;

            Assert.AreEqual(regenerations, host.Regenerations, "No regeneration");
            Assert.AreSame(planet, host.Visual.Planet);
            Assert.AreEqual(cameraPosition, liveCamera.position, "The user's camera is untouched");
            Assert.AreEqual(cameraRotation, liveCamera.rotation);
            Assert.AreEqual(objects, Resources.FindObjectsOfTypeAll<GameObject>().Length, "Temporary camera destroyed");
            Assert.AreEqual(cameras, Resources.FindObjectsOfTypeAll<Camera>().Length);
            Assert.AreEqual(textures, Resources.FindObjectsOfTypeAll<Texture>().Length, "Render texture and readback released");

            Texture2D image = Decode(png);
            Assert.AreEqual(WorldThumbnailRenderer.DefaultSize, image.width);
            Assert.AreEqual(WorldThumbnailRenderer.DefaultSize, image.height);
            Color32 background = WorldThumbnailRenderer.Background;
            Assert.Less(Difference(Pixel(image, 0.02f, 0.02f), background), 3f, "Corners: empty space (planet framed with a margin)");
            Assert.Less(Difference(Pixel(image, 0.98f, 0.98f), background), 3f);
            Assert.Greater(Difference(Pixel(image, 0.5f, 0.5f), background), 20f, "Center: the lit planet");

            // The parts drawn without GameObjects are in the picture (camera-targeted draws).
            host.Visual.Clouds.showClouds = false;
            host.Visual.ApplyOceanAtmosphere();
            byte[] noClouds = WorldThumbnailRenderer.CapturePng(host.Visual);
            host.Visual.OceanAtmosphere.atmosphere = false;
            host.Visual.ApplyOceanAtmosphere();
            byte[] noAtmosphere = WorldThumbnailRenderer.CapturePng(host.Visual);
            CollectionAssert.AreNotEqual(png, noClouds, "Clouds are captured");
            CollectionAssert.AreNotEqual(noClouds, noAtmosphere, "The atmosphere is captured");

            host.Visual.DebugView = PlanetDebugView.ClimateTemperature;
            Assert.Throws<InvalidOperationException>(() => WorldThumbnailRenderer.CapturePng(host.Visual), "Never a debug view");
            WorldArchiveTestUtil.Report(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Thumbnail capture 256 px (512 rendered): total {0:0.0} ms = render submit {1:0.0} + readback incl. GPU wait {2:0.0} + downsample & PNG {3:0.0}; {4} bytes",
                watch.Elapsed.TotalMilliseconds, stats.RenderMilliseconds, stats.ReadbackMilliseconds, stats.EncodeMilliseconds, stats.Bytes));
        }

        // ------------------------------------------------------------ Q, J, unsaved

        // Q
        [Test]
        public void Q_ThumbnailsAreWritten_OnlyAfterSaves_OncePerSave()
        {
            Rig rig = CreateRig();
            WorldTestHost host = rig.Host;
            RunFrames(rig.Updater, 20);
            host.Camera.SetView(40f, 10f, host.Camera.TargetDistance);
            host.Visual.CloudSimulationTime += 3.0;
            host.Visual.ApplyCloudTime();
            host.Visual.SetDayNightCycle(true);
            host.Now += 30.0;
            host.Visual.TickSolar();
            RunFrames(rig.Updater, 20);
            Assert.AreEqual(0, thumbnails.WriteCount, "Camera, clouds and the solar clock never capture");

            Assert.IsTrue(rig.Session.Save("First").Success);
            Guid first = rig.Session.PlanetId;
            Assert.IsTrue(rig.Updater.HasPending);
            rig.Updater.Tick();
            Assert.AreEqual(0, thumbnails.WriteCount, "Not in the frame of the save (the display catches up first)");
            RunFrames(rig.Updater);
            Assert.AreEqual(1, thumbnails.WriteCount);
            Assert.IsTrue(thumbnails.Exists(first));
            RunFrames(rig.Updater, 30);
            Assert.AreEqual(1, thumbnails.WriteCount, "Once per save");

            byte[] firstPng = File.ReadAllBytes(thumbnails.PathFor(first));
            Assert.IsTrue(rig.Session.SaveAsNew("Second").Success);
            RunFrames(rig.Updater);
            Assert.AreEqual(2, thumbnails.WriteCount);
            Assert.IsTrue(thumbnails.Exists(rig.Session.PlanetId), "Save As New gets its own thumbnail");
            CollectionAssert.AreEqual(firstPng, File.ReadAllBytes(thumbnails.PathFor(first)), "The first world's thumbnail is untouched");
            Assert.IsEmpty(rig.Warnings);
        }

        [Test]
        public void UnsavedChanges_NeverOverwriteTheSavedThumbnail()
        {
            Rig rig = CreateRig();
            Assert.IsTrue(rig.Session.Save("Saved look").Success);
            RunFrames(rig.Updater);
            byte[] saved = File.ReadAllBytes(thumbnails.PathFor(rig.Session.PlanetId));

            rig.Host.Visual.Surface.saturation = 0.6f; // the world changes...
            Assert.IsTrue(rig.Session.RefreshDirty());
            Assert.IsFalse(rig.Updater.RequestCurrent(), "...so a preview refresh is refused");
            rig.Updater.Request(rig.Session.PlanetId, rig.Session.SavedConfiguration); // even if one was pending
            Assert.AreEqual(ThumbnailCaptureOutcome.Cancelled, rig.Updater.TryCompletePending());
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(thumbnails.PathFor(rig.Session.PlanetId)));
            Assert.AreEqual(1, rig.Warnings.Count, "Reported");

            // A save still waiting for a regeneration (or shown in a scientific view) waits, then captures.
            rig.Host.Visual.Surface.saturation = 0.9f;
            rig.Host.Definition.seaLevel += 0.002f; // pending terrain change: not regenerated yet
            Assert.IsTrue(rig.Session.Save().Success);
            Assert.AreEqual(ThumbnailCaptureOutcome.Waiting, rig.Updater.TryCompletePending(), "The displayed planet is not the saved one yet");
            rig.Host.Regenerate();
            rig.Host.Visual.DebugView = PlanetDebugView.BiomeMap;
            Assert.AreEqual(ThumbnailCaptureOutcome.Waiting, rig.Updater.TryCompletePending(), "Scientific view: wait for the Natural Planet");
            rig.Host.Visual.DebugView = PlanetDebugView.None;
            rig.Host.Visual.RefreshColors();
            Assert.AreEqual(ThumbnailCaptureOutcome.Captured, rig.Updater.TryCompletePending());
        }

        sealed class ReadOnlyFileSystem : PhysicalArchiveFileSystem
        {
            public override void WriteNewFile(string path, byte[] bytes) => throw new UnauthorizedAccessException("Read-only storage.");
        }

        // J
        [Test]
        public void J_FailedThumbnails_NeverFailTheSave_AndKeepThePreviousOne()
        {
            Rig rig = CreateRig();
            Assert.IsTrue(rig.Session.Save("Robust").Success);
            RunFrames(rig.Updater);
            Guid id = rig.Session.PlanetId;
            byte[] previous = File.ReadAllBytes(thumbnails.PathFor(id));

            rig.Updater.Capture = (_, __) => throw new InvalidOperationException("GPU readback failed");
            rig.Host.Visual.Clouds.coverage = 0.3f;
            WorldOperationResult save = rig.Session.Save();
            Assert.IsTrue(save.Success, "The save stands");
            RunFrames(rig.Updater);
            Assert.AreEqual(0.3f, store.Load(id).Archive.Configuration.Clouds.coverage);
            CollectionAssert.AreEqual(previous, File.ReadAllBytes(thumbnails.PathFor(id)), "Previous thumbnail kept");
            Assert.IsTrue(rig.Warnings.Any(w => w.Contains("GPU readback failed")), "Non-fatal warning");

            // Read-only thumbnail folder: same outcome.
            var readOnly = new WorldThumbnailStore(Path.Combine(root, "ReadOnlyThumbnails"), new ReadOnlyFileSystem());
            Rig other = CreateRig(thumbnailStore: readOnly);
            Assert.IsTrue(other.Session.Save("No previews here").Success);
            RunFrames(other.Updater);
            Assert.IsTrue(store.Load(other.Session.PlanetId).Success);
            Assert.IsTrue(other.Warnings.Any(w => w.Contains("Read-only")));
        }

        // ------------------------------------------------------------ G, H, I

        // G, H, I
        [Test]
        public void GHI_ThumbnailsFollowDuplicateRenameAndDelete()
        {
            Rig rig = CreateRig();
            Assert.IsTrue(rig.Session.Save("Original").Success);
            RunFrames(rig.Updater);
            Guid original = rig.Session.PlanetId;
            byte[] png = File.ReadAllBytes(thumbnails.PathFor(original));

            WorldOperationResult copy = rig.Session.Duplicate(original);
            Assert.IsTrue(copy.Success);
            Assert.AreNotEqual(original, copy.PlanetId, "G: a distinct GUID");
            CollectionAssert.AreEqual(png, File.ReadAllBytes(thumbnails.PathFor(copy.PlanetId)), "G: the copy has the same picture, in its own file");

            Assert.IsTrue(rig.Session.Rename(original, "Renamed ✦").Success);
            CollectionAssert.AreEqual(png, File.ReadAllBytes(thumbnails.PathFor(original)), "H: same thumbnail, same identity");
            Assert.AreEqual(2, Directory.GetFiles(thumbnails.Directory).Length);

            Assert.IsTrue(rig.Session.Delete(copy.PlanetId).Success);
            Assert.IsFalse(thumbnails.Exists(copy.PlanetId), "I: removed with its archive");
            Assert.IsTrue(thumbnails.Exists(original), "I: only the selected world's");
            Assert.IsTrue(store.Load(original).Success);

            // I: the thumbnail is only removed once the archive deletion succeeded.
            var lockedStore = new WorldArchiveStore(store.Directory, new LockedDeleteFileSystem());
            var locked = new WorldSession(rig.Host, lockedStore, thumbnails);
            Assert.IsFalse(locked.Delete(original).Success);
            Assert.IsTrue(thumbnails.Exists(original), "Archive kept, so is its thumbnail");
        }

        sealed class LockedDeleteFileSystem : PhysicalArchiveFileSystem
        {
            public override void Delete(string path) => throw new IOException("The file is locked.");
        }

        // ------------------------------------------------------------ E, F

        // E, F
        [Test]
        public void EF_MissingOrCorruptThumbnails_ShowPlaceholders_AndWorldsStayLoadable()
        {
            Rig rig = CreateRig();
            Assert.IsTrue(rig.Session.Save("Missing").Success);
            Guid missing = rig.Session.PlanetId;
            Assert.IsTrue(rig.Session.SaveAsNew("Corrupt").Success);
            Guid corrupt = rig.Session.PlanetId;
            Assert.IsTrue(rig.Session.SaveAsNew("Fine").Success);
            Guid fine = rig.Session.PlanetId;
            RunFrames(rig.Updater);
            File.Delete(thumbnails.PathFor(missing));
            byte[] broken = File.ReadAllBytes(thumbnails.PathFor(fine)).Take(200).ToArray(); // a PNG cut short
            File.WriteAllBytes(thumbnails.PathFor(corrupt), broken);

            LogAssert.ignoreFailingMessages = true; // the engine may log the failed decode of the truncated PNG
            var cache = new WorldThumbnailCache(thumbnails);
            Assert.IsNull(cache.Get(missing), "E: no thumbnail");
            Assert.IsNull(cache.Get(corrupt), "F: corrupt thumbnail");
            Assert.IsNotNull(cache.Get(fine));
            Assert.AreEqual(1, cache.LoadedCount, "Failed decodes leave no texture");
            int decodes = cache.DecodeCount;
            cache.Get(corrupt);
            cache.Get(missing);
            Assert.AreEqual(decodes, cache.DecodeCount, "Remembered: no repeated disk reads");
            cache.Clear();
            LogAssert.ignoreFailingMessages = false;

            WorldLibraryPanel library = CreateLibrary(rig);
            library.Open();
            library.FlushThumbnails();
            library.Select(missing);
            Assert.AreSame(library.CardTexture(missing), library.PreviewTexture);
            Assert.AreNotEqual(library.CardTexture(fine), library.CardTexture(missing), "Placeholder, not another world's picture");
            library.Close();
            foreach (Guid id in new[] { missing, corrupt })
            {
                Assert.IsTrue(rig.Session.Load(id).Success, "The archive is fully usable");
            }
        }

        // ------------------------------------------------------------ Metadata

        [Test]
        public void Metadata_IsRecordedFromAlreadyComputedData_ExactlyAsDisplayed()
        {
            Rig rig = CreateRig();
            WorldTestHost host = rig.Host;
            int climateComputes = host.Visual.ClimateCache.ComputeCount, biomeComputes = host.Visual.BiomeCache.ComputeCount;
            Assert.IsTrue(rig.Session.Save("Measured").Success);
            Assert.AreEqual(climateComputes, host.Visual.ClimateCache.ComputeCount, "Saving computes nothing");
            Assert.AreEqual(biomeComputes, host.Visual.BiomeCache.ComputeCount);
            WorldMetadata m = store.Load(rig.Session.PlanetId).Archive.Metadata;
            Assert.AreEqual(host.Current.LandFraction, m.LandFraction);
            Assert.AreEqual(host.Visual.CurrentClimate.Diagnostics.MeanTemperature, m.MeanTemperature);
            Assert.AreEqual(host.Visual.CurrentBiomes.Diagnostics.DominantBiome, m.DominantBiome);

            // Values that do not belong to the saved configuration are not recorded.
            host.Definition.seaLevel += 0.003f; // not regenerated: the displayed statistics are stale
            Assert.IsTrue(rig.Session.Save().Success);
            Assert.IsNull(store.Load(rig.Session.PlanetId).Archive.Metadata, "Unavailable rather than wrong");
            host.Regenerate();
            host.Visual.Climate.axialTilt += 5f; // climate settings changed, climate not recomputed yet
            Assert.IsTrue(rig.Session.Save().Success);
            WorldMetadata partial = store.Load(rig.Session.PlanetId).Archive.Metadata;
            Assert.IsTrue(partial.HasLand);
            Assert.IsFalse(partial.HasTemperature);
            Assert.IsNull(partial.DominantBiome);
        }

        [Test]
        public void V013AWorlds_LoadExactly_AndGainStatisticsAndPreviewOnRequest()
        {
            File.WriteAllText(store.PathFor(WorldArchiveTestUtil.V013AArchiveId), WorldArchiveTestUtil.V013AArchiveJson);
            Rig rig = CreateRig();
            WorldOperationResult load = rig.Session.Load(WorldArchiveTestUtil.V013AArchiveId);
            Assert.IsTrue(load.Success, load.Message);
            Assert.AreEqual(GeneratorCompatibility.Exact, load.Compatibility);
            Assert.IsEmpty(load.Warnings);
            PlanetArchive before = store.Load(WorldArchiveTestUtil.V013AArchiveId).Archive;
            Assert.IsNull(before.Metadata);

            int regenerations = rig.Host.Regenerations;
            WorldOperationResult refresh = rig.Session.RefreshMetadata();
            Assert.IsTrue(refresh.Success, refresh.Message);
            Assert.IsTrue(rig.Updater.RequestCurrent());
            RunFrames(rig.Updater);
            Assert.AreEqual(regenerations, rig.Host.Regenerations, "No regeneration");
            PlanetArchive after = store.Load(WorldArchiveTestUtil.V013AArchiveId).Archive;
            Assert.IsNotNull(after.Metadata);
            Assert.IsTrue(after.Metadata.HasLand && after.Metadata.HasTemperature && after.Metadata.DominantBiome != null);
            Assert.AreEqual(before.ModifiedUtc, after.ModifiedUtc, "Statistics are not a modification of the world");
            Assert.AreEqual("Ærendel ✦", after.DisplayName);
            WorldArchiveTestUtil.AssertSameConfiguration(before.Configuration, after.Configuration);
            Assert.IsTrue(thumbnails.Exists(WorldArchiveTestUtil.V013AArchiveId));
            Assert.IsFalse(rig.Session.IsDirty);
        }

        // ------------------------------------------------------------ N, O, P, W (library UI)

        // N
        [Test]
        public void N_EnteringAnotherWorld_ProtectsUnsavedChanges()
        {
            Rig rig = CreateRig();
            Assert.IsTrue(rig.Session.Save("Current").Success);
            Guid current = rig.Session.PlanetId;
            rig.Host.Definition.seed = 1337;
            rig.Host.Regenerate();
            Assert.IsTrue(rig.Session.SaveAsNew("Other").Success);
            Guid other = rig.Session.PlanetId;
            Assert.IsTrue(rig.Session.Load(current).Success);
            rig.Host.Visual.Surface.rockExposure = 0.9f; // unsaved change

            var changes = new List<WorldOperationResult>();
            WorldLibraryPanel library = CreateLibrary(rig, changes);
            library.Open();
            Assert.AreEqual(current, library.SelectedId, "Opens on the current world");
            int regenerations = rig.Host.Regenerations;
            library.Select(other);
            library.EnterSelected();
            Assert.IsTrue(library.PromptOpen, "Confirmation required");
            Assert.AreEqual(current, rig.Session.PlanetId);
            Assert.AreEqual(regenerations, rig.Host.Regenerations);
            library.HidePrompt(); // Cancel
            Assert.AreEqual(current, rig.Session.PlanetId, "Cancel keeps the world and its changes");
            Assert.AreEqual(0.9f, rig.Host.Visual.Surface.rockExposure);
            Assert.IsTrue(library.IsOpen);

            library.EnterSelected();
            library.ConfirmPrompt();
            Assert.AreEqual(other, rig.Session.PlanetId);
            Assert.AreEqual(regenerations + 1, rig.Host.Regenerations, "One regeneration");
            Assert.IsFalse(library.IsOpen, "Closed after entering");
            Assert.AreEqual(1, changes.Count);

            // New World: same protection.
            rig.Host.Visual.Surface.rockExposure = 0.1f;
            library.Open();
            library.CreateWorld();
            Assert.IsTrue(library.PromptOpen);
            library.ConfirmPrompt();
            Assert.IsFalse(rig.Session.HasArchive, "A new, unsaved world");
            Assert.IsFalse(rig.Session.IsDirty);
            Assert.AreEqual(regenerations + 2, rig.Host.Regenerations);

            // The current, unmodified world: Enter simply returns to it.
            Assert.IsTrue(rig.Session.Load(other).Success);
            library.Open();
            int before = rig.Host.Regenerations;
            library.EnterSelected();
            Assert.IsFalse(library.PromptOpen);
            Assert.IsFalse(library.IsOpen);
            Assert.AreEqual(before, rig.Host.Regenerations, "Already there: no regeneration");
        }

        // O, P, W
        [Test]
        public void OPW_BrowsingTheLibrary_RegeneratesAndRecomputesNothing()
        {
            Rig rig = CreateRig();
            for (int i = 0; i < 15; i++)
            {
                rig.Host.Visual.Clouds.coverage = 0.3f + i * 0.01f;
                Assert.IsTrue(rig.Session.SaveAsNew("World " + i).Success);
            }
            RunFrames(rig.Updater);
            PlanetVisualController v = rig.Host.Visual;
            int regenerations = rig.Host.Regenerations;
            PlanetInstance planet = rig.Host.Current;
            int climate = v.ClimateCache.ComputeCount, biomes = v.BiomeCache.ComputeCount, hydrology = v.HydrologyCache.ComputeCount;
            int clouds = v.CloudFieldCache.ComputeCount, uploads = v.CloudUploadCount, natural = v.NaturalSurfaceBuildCount;
            int captures = thumbnails.WriteCount;

            WorldLibraryPanel library = CreateLibrary(rig);
            for (int round = 0; round < 3; round++)
            {
                library.Open();
                library.FlushThumbnails();
                foreach (WorldArchiveEntry e in library.Model.View.ToArray())
                {
                    library.Select(e.PlanetId);
                }
                library.ShowPage(1);
                library.FlushThumbnails();
                library.SetQuery("World 1");
                library.CycleSort();
                library.SetQuery("");
                library.Close();
            }
            Assert.AreEqual(regenerations, rig.Host.Regenerations, "O: no regeneration");
            Assert.AreSame(planet, v.Planet);
            Assert.AreEqual(climate, v.ClimateCache.ComputeCount, "P: no climate");
            Assert.AreEqual(biomes, v.BiomeCache.ComputeCount, "W: no biomes");
            Assert.AreEqual(hydrology, v.HydrologyCache.ComputeCount, "W: no hydrology");
            Assert.AreEqual(clouds, v.CloudFieldCache.ComputeCount, "W: no cloud field");
            Assert.AreEqual(uploads, v.CloudUploadCount);
            Assert.AreEqual(natural, v.NaturalSurfaceBuildCount);
            Assert.AreEqual(captures, thumbnails.WriteCount, "Q: browsing never captures");
        }

        // ------------------------------------------------------------ R, T, U

        // R, U
        [Test]
        public void RU_ThumbnailTextures_AreBoundedAndReleased()
        {
            Rig rig = CreateRig();
            var ids = new List<Guid>();
            for (int i = 0; i < 30; i++)
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive("Pictured " + i);
                Assert.IsTrue(store.Save(a).Success);
                Assert.IsNull(thumbnails.Write(a.PlanetId, SmallPng(new Color(i / 30f, 0.4f, 0.6f))));
                ids.Add(a.PlanetId);
            }
            WorldLibraryPanel library = CreateLibrary(rig);
            library.Open();
            library.Close();
            int baseline = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            for (int round = 0; round < 10; round++)
            {
                library.Open(); // reopens on the last selected world's page (round 0: page 1; then: the last page, 6 worlds)
                library.FlushThumbnails();
                int visible = library.VisibleEntries.Count();
                Assert.AreEqual(round == 0 ? WorldLibraryPanel.PageSize : 30 % WorldLibraryPanel.PageSize, visible);
                Assert.AreEqual(visible, library.ThumbnailCache.LoadedCount, "R: only the visible page is decoded");
                for (int p = 0; p < library.PageCount; p++)
                {
                    library.ShowPage(p);
                    library.FlushThumbnails();
                }
                Assert.LessOrEqual(library.ThumbnailCache.LoadedCount, library.ThumbnailCache.Capacity, "R: bounded");
                Assert.IsNotNull(library.CardTexture(library.VisibleEntries.First().PlanetId));
                library.Close();
                Assert.AreEqual(0, library.ThumbnailCache.LoadedCount, "R: released on close");
            }
            Assert.AreEqual(baseline, Resources.FindObjectsOfTypeAll<Texture2D>().Length, "U: no texture leak over 10 openings");
        }

        // T
        [Test]
        public void T_TheLibraryStaysFunctionalWith120Worlds()
        {
            Rig rig = CreateRig();
            for (int i = 0; i < 120; i++)
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive(i % 7 == 0 ? "Twin" : "Planet " + i.ToString("000"));
                a.Configuration.Terrain.seed = 5000 + i;
                a.Metadata = new WorldMetadata { LandFraction = 0.2f + i * 0.001f };
                Assert.IsTrue(store.Save(a).Success);
            }
            File.WriteAllText(store.PathFor(Guid.NewGuid()), "not json");
            WorldLibraryPanel library = CreateLibrary(rig);
            var watch = Stopwatch.StartNew();
            library.Open();
            double open = watch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(121, library.Model.All.Count);
            Assert.AreEqual(11, library.PageCount);
            Assert.AreEqual(WorldLibraryPanel.PageSize, library.VisibleEntries.Count());
            library.ShowPage(10);
            Assert.AreEqual(1, library.VisibleEntries.Count(), "The unreadable archive is listed last, visible");
            Assert.IsFalse(library.VisibleEntries.Single().IsValid);
            watch.Restart();
            library.SetQuery("twin");
            double search = watch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(18, library.Model.View.Count);
            library.SetSort(WorldSortMode.NameDescending);
            Assert.AreEqual(2, library.PageCount);
            Assert.IsTrue(library.VisibleEntries.All(e => e.DisplayName == "Twin"), "M: 18 worlds with the same name");
            library.SetQuery("");
            library.Select(library.Model.View[100].PlanetId);
            Assert.AreEqual(100 / WorldLibraryPanel.PageSize, library.Page, "Selecting shows the right page");
            WorldArchiveTestUtil.Report(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Library UI with 121 archives: open {0:0} ms (listing {1:0} ms), search {2:0.0} ms", open, library.Model.LastReloadMilliseconds, search));
            Assert.Less(library.Model.LastReloadMilliseconds, 1000.0);
            library.Close();
        }

        // ------------------------------------------------------------ Application wiring

        [Test]
        public void Bootstrap_PanelOpensTheLibrary_AndSavesScheduleAPreview()
        {
            var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
            var bootstrapObject = new GameObject("Library Bootstrap Test");
            var bootstrap = bootstrapObject.AddComponent<PlanetBootstrap>();
            bootstrap.EnsureSceneObjects();
            foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (!before.Contains(o) && o.transform.parent == null)
                {
                    created.Add(o);
                }
            }
            bootstrap.UseWorldStore(store, thumbnails);
            bootstrap.Definition.resolution = 16;
            bootstrap.Visual.Climate.resolution = ClimateSettings.LowResolution;
            bootstrap.Regenerate();
            Assert.IsNotNull(bootstrap.ThumbnailUpdater);
            Assert.IsTrue(bootstrap.Worlds.Save("Wired").Success);
            Assert.IsTrue(bootstrap.ThumbnailUpdater.HasPending, "Saves schedule a preview");
            RunFrames(bootstrap.ThumbnailUpdater);
            Assert.IsTrue(thumbnails.Exists(bootstrap.Worlds.PlanetId));

            PlanetGenerationPanel panel = Object.FindAnyObjectByType<PlanetGenerationPanel>();
            panel.OpenWorldLibrary();
            Assert.IsTrue(panel.WorldLibrary.IsOpen);
            Assert.AreEqual(bootstrap.Worlds.PlanetId, panel.WorldLibrary.SelectedId);
            panel.WorldLibrary.FlushThumbnails();
            Assert.AreEqual(1, panel.WorldLibrary.ThumbnailCache.LoadedCount, "The saved world's preview is shown");
            panel.SetContemplation(true);
            Assert.IsFalse(panel.WorldLibrary.IsOpen, "Contemplation closes the library");
            panel.SetContemplation(false);

            int count = bootstrap.GenerationCount;
            panel.OpenWorldLibrary();
            panel.WorldLibrary.CreateWorld();
            Assert.AreEqual(count + 1, bootstrap.GenerationCount, "New World: one regeneration");
            Assert.IsFalse(bootstrap.Worlds.HasArchive);
        }
    }
}
