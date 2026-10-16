using System;
using System.Diagnostics;
using System.IO;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Library;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Bootstrap
{
    /// <summary>
    /// Entry point of the Genesis scene. Owns the editable <see cref="PlanetDefinition"/>,
    /// regenerates the planet on demand, and makes sure every required scene object
    /// exists (planet visual, star, orbit camera, UI) so a scene containing only this
    /// component is already playable. Hosts the world archive session (V0.1.3A).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class PlanetBootstrap : MonoBehaviour, IWorldHost
    {
        /// <summary>World archives folder, relative to <c>Application.persistentDataPath</c>.</summary>
        public const string WorldsFolder = "Planetwalker/Worlds";

        /// <summary>World thumbnails folder, relative to <c>Application.persistentDataPath</c> (V0.1.3B).</summary>
        public const string ThumbnailsFolder = "Planetwalker/Thumbnails";

        [SerializeField] PlanetDefinition definition = new PlanetDefinition();

        [Header("Scene References (created automatically when empty)")]
        [SerializeField] PlanetVisualController visual;
        [SerializeField] PlanetOrbitCamera orbitCamera;
        [SerializeField] PlanetGenerationPanel panel;
        [SerializeField] Light star;

        [Header("Environment")]
        [Tooltip("Configure a dark space background and a faint ambient light on start.")]
        [SerializeField] bool applySpaceEnvironment = true;
        [SerializeField] Color backgroundColor = new Color(0.010f, 0.012f, 0.020f);
        [SerializeField] Color ambientColor = new Color(0.10f, 0.11f, 0.14f);

        System.Random seedRandom;
        WorldSession worlds;
        WorldThumbnailUpdater thumbnailUpdater;

        /// <summary>Raised after every successful (re)generation.</summary>
        public event Action<PlanetInstance> PlanetGenerated;

        /// <summary>Editable configuration; call <see cref="Regenerate"/> to apply changes.</summary>
        public PlanetDefinition Definition => definition;

        public PlanetInstance Current { get; private set; }

        public PlanetVisualController Visual => visual;

        public PlanetOrbitCamera OrbitCamera => orbitCamera;

        public float LastGenerationMilliseconds { get; private set; }

        public int GenerationCount { get; private set; }

        /// <summary>Raised when settings are edited in the inspector (editor only): the world may have unsaved changes.</summary>
        public event Action SettingsEdited;

        /// <summary>
        /// The current world and its archive (created on first use; no file is touched until an archive action).
        /// Archives live in <see cref="DefaultWorldsDirectory"/>.
        /// </summary>
        public WorldSession Worlds => worlds ??= new WorldSession(this, new WorldArchiveStore(DefaultWorldsDirectory),
            new WorldThumbnailStore(DefaultThumbnailsDirectory));

        /// <summary>Captures world thumbnails after saves (V0.1.3B); ticked by <see cref="Update"/> in Play mode.</summary>
        public WorldThumbnailUpdater ThumbnailUpdater
        {
            get
            {
                if (thumbnailUpdater == null && visual != null && Worlds.Thumbnails != null)
                {
                    thumbnailUpdater = new WorldThumbnailUpdater(visual, Worlds, Worlds.Thumbnails);
                }
                return thumbnailUpdater;
            }
        }

        /// <summary>Absolute path of the world archives: persistentDataPath/Planetwalker/Worlds (Editor and builds).</summary>
        public static string DefaultWorldsDirectory => Path.Combine(Application.persistentDataPath, WorldsFolder);

        /// <summary>Absolute path of the world thumbnails: persistentDataPath/Planetwalker/Thumbnails.</summary>
        public static string DefaultThumbnailsDirectory => Path.Combine(Application.persistentDataPath, ThumbnailsFolder);

        /// <summary>Uses other archive / thumbnail stores (tests: isolated temporary folders). Resets the session.</summary>
        public void UseWorldStore(WorldArchiveStore store, WorldThumbnailStore thumbnails = null)
        {
            thumbnailUpdater?.Detach();
            thumbnailUpdater = null;
            worlds = new WorldSession(this, store, thumbnails);
        }

        /// <summary>
        /// Discovers a new world (World Library "New World"): a new random seed with the current settings, one
        /// regeneration, and a new unsaved identity. The caller protects unsaved changes first.
        /// </summary>
        public void CreateNewWorld()
        {
            RandomizeSeed();
            Regenerate();
            Worlds.BeginNewWorld();
        }

        void Update()
        {
            // Pending thumbnail capture only (free otherwise).
            if (Application.isPlaying && worlds != null)
            {
                ThumbnailUpdater?.Tick();
            }
        }

        CameraPresentation IWorldHost.CaptureCamera()
        {
            if (orbitCamera == null)
            {
                return null;
            }
            return new CameraPresentation
            {
                yaw = Mathf.Repeat(orbitCamera.TargetYaw + 180f, 360f) - 180f,
                pitch = Mathf.Clamp(orbitCamera.TargetPitch, -90f, 90f),
                distance = Mathf.Clamp(orbitCamera.TargetDistance, 0.001f, 1000000f),
            };
        }

        void IWorldHost.RestoreCamera(CameraPresentation camera)
        {
            if (orbitCamera != null && camera != null)
            {
                orbitCamera.SetView(camera.yaw, camera.pitch, camera.distance);
            }
        }

        void OnValidate()
        {
            SettingsEdited?.Invoke();
        }

        void Awake()
        {
            EnsureSceneObjects();
            if (applySpaceEnvironment)
            {
                ApplySpaceEnvironment();
            }
        }

        void Start()
        {
            Regenerate();
        }

        /// <summary>Generates the planet from the current definition and updates its visuals.</summary>
        public void Regenerate()
        {
            if (visual == null)
            {
                EnsureSceneObjects();
            }

            var stopwatch = Stopwatch.StartNew();
            definition.Sanitize();
            Current = new PlanetInstance(definition);
            visual.Render(Current);
            stopwatch.Stop();
            LastGenerationMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;

            bool first = GenerationCount == 0;
            GenerationCount++;
            if (orbitCamera != null)
            {
                // Altitude, zoom and clip planes follow the terrain under the camera (V0.2.0C: canonical elevation).
                orbitCamera.SurfaceRadius = SurfaceRadiusUnderCamera;
                // Frame the planet at startup; afterwards keep the user's view.
                orbitCamera.FramePlanet(visual.BoundingRadius, first);
            }
            if (first)
            {
                // The startup planet is a new, unsaved world: its configuration is the unsaved-changes baseline.
                Worlds.ResetBaseline();
                _ = ThumbnailUpdater; // listens to saves from the start
            }
            PlanetGenerated?.Invoke(Current);
        }

        /// <summary>Terrain radius (render units) under a planet-space direction: the canonical surface.</summary>
        float SurfaceRadiusUnderCamera(Vector3 direction)
        {
            return Current != null ? Current.GetSurfaceRadius(direction) : visual != null ? visual.BoundingRadius : 0f;
        }

        /// <summary>Picks a new random seed (does not regenerate).</summary>
        public int RandomizeSeed()
        {
            if (seedRandom == null)
            {
                seedRandom = new System.Random();
            }
            definition.seed = seedRandom.Next(0, 1000000);
            return definition.seed;
        }

        /// <summary>
        /// Finds or creates the planet visual, star light, orbit camera and UI. Used at
        /// runtime and by the editor setup utility.
        /// </summary>
        public void EnsureSceneObjects()
        {
            if (visual == null)
            {
                visual = FindAnyObjectByType<PlanetVisualController>();
            }
            if (visual == null)
            {
                visual = new GameObject("Planet").AddComponent<PlanetVisualController>();
            }

            if (star == null)
            {
                star = FindDirectionalLight();
            }
            if (star == null)
            {
                var starObject = new GameObject("Star");
                starObject.transform.rotation = Quaternion.Euler(20f, 50f, 0f);
                star = starObject.AddComponent<Light>();
                star.type = LightType.Directional;
                star.color = new Color(1f, 0.96f, 0.90f);
                star.intensity = 1.4f;
                star.shadows = LightShadows.None;
            }
            // The solar clock (V0.1.2D) orients this light: the single sun of every shader.
            visual.SetSun(star);
            // Floating origin (V0.2.0C): the orbit camera finds it through its target.
            PlanetRenderFrame.GetOrCreate(visual.transform);

            if (orbitCamera == null)
            {
                orbitCamera = FindAnyObjectByType<PlanetOrbitCamera>();
            }
            if (orbitCamera == null)
            {
                Camera sceneCamera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
                if (sceneCamera == null)
                {
                    var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
                    sceneCamera = cameraObject.AddComponent<Camera>();
                    cameraObject.AddComponent<AudioListener>();
                }
                sceneCamera.nearClipPlane = 0.3f;
                sceneCamera.farClipPlane = 5000f;
                orbitCamera = sceneCamera.gameObject.AddComponent<PlanetOrbitCamera>();
            }
            orbitCamera.Target = visual.transform;

            if (panel == null)
            {
                panel = FindAnyObjectByType<PlanetGenerationPanel>();
            }
            if (panel == null)
            {
                panel = PlanetGenerationPanel.Create(this);
            }
            else
            {
                PlanetGenerationPanel.EnsureEventSystem();
                panel.SetBootstrap(this);
            }
        }

        /// <summary>Dark background, faint flat ambient, star as the sun source.</summary>
        public void ApplySpaceEnvironment()
        {
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambientColor;
            // Set the probe explicitly so ambient is correct without a lighting bake.
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(QualitySettings.activeColorSpace == ColorSpace.Linear ? ambientColor.linear : ambientColor);
            RenderSettings.ambientProbe = probe;
            if (star != null)
            {
                RenderSettings.sun = star;
            }

            if (orbitCamera != null)
            {
                var sceneCamera = orbitCamera.GetComponent<Camera>();
                sceneCamera.clearFlags = CameraClearFlags.SolidColor;
                sceneCamera.backgroundColor = backgroundColor;
            }
        }

        static Light FindDirectionalLight()
        {
            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    return light;
                }
            }
            return null;
        }
    }
}
