using System;
using Planetwalker.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Planetwalker.CameraControl
{
    /// <summary>
    /// Smooth orbital camera around a planet center.
    /// LMB drag: rotate. Mouse wheel: zoom. R: reset.
    /// </summary>
    /// <remarks>
    /// Orientation is yaw/pitch with pitch clamped short of the poles, so the camera can
    /// never flip. It always looks at the target. Rotation is measured in degrees per
    /// screen height, which makes it independent of the screen resolution.
    /// <para>
    /// Precision (V0.2.0C): the camera position is computed in double in planet space and placed through the
    /// target's <see cref="PlanetRenderFrame"/> when it has one (floating origin: the frame is asked to rebase
    /// first, then the camera is placed relative to the new origin, in the same call). Without a frame the former
    /// float path is used. The near and far clip planes follow the altitude. Ordering: input, smoothing, rebase and
    /// placement all happen in <c>Update</c> at execution order -90, i.e. after <c>PlanetBootstrap</c> (-100) and
    /// before every default-order <c>Update</c> (the adaptive terrain's LOD tick, the visual's haze-frame check) and
    /// every <c>LateUpdate</c> draw (atmosphere, clouds, lakes, rivers, adaptive patches, diagnostics): within a
    /// frame, everything sees one camera position and one planet root position.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(-90)]
    public sealed class PlanetOrbitCamera : MonoBehaviour
    {
        /// <summary>Near clip plane as a fraction of the altitude above the bounding sphere.</summary>
        const float NearPlaneAltitudeFraction = 0.05f;
        /// <summary>Near clip plane bounds (render units): the floor keeps the fraction down to the altitude floor.</summary>
        const float MinNearPlane = 5e-5f, MaxNearPlane = 0.3f;
        /// <summary>Far clip plane bounds (render units).</summary>
        const float MinFarPlane = 30f, MaxFarPlane = 5000f;

        [SerializeField] Transform target;

        [Header("Rotation")]
        [Tooltip("Degrees of rotation for a drag across the full screen height (at framing distance).")]
        [SerializeField, Range(10f, 720f)] float rotationSensitivity = 180f;
        [SerializeField, Range(0f, 89f)] float maxPitch = 85f;
        [Tooltip("Higher = snappier. Exponential smoothing rate, per second.")]
        [SerializeField, Range(1f, 50f)] float rotationSmoothing = 12f;

        [Header("Zoom")]
        [Tooltip("Fraction of the current altitude covered by one wheel notch.")]
        [SerializeField, Range(0.01f, 0.5f)] float zoomSensitivity = 0.15f;
        [SerializeField, Range(1f, 50f)] float zoomSmoothing = 10f;
        [Tooltip("Minimum camera altitude above the planet bounding sphere, render units (the near plane follows the altitude).")]
        [SerializeField, Range(0.0001f, 10f)] float minAltitude = 0.001f;
        [Tooltip("Maximum camera distance, as a multiple of the planet bounding radius.")]
        [SerializeField, Range(2f, 50f)] float maxDistanceFactor = 8f;

        [Header("Framing")]
        [Tooltip("Extra space around the planet when framing (1 = planet touches the screen edge).")]
        [SerializeField, Range(1f, 3f)] float framingMargin = 1.35f;
        [SerializeField] float defaultYaw = 0f;
        [SerializeField] float defaultPitch = 15f;

        Camera cachedCamera;
        PlanetRenderFrame frame;
        float planetRadius = 1f;
        // Radius of the terrain under the camera (V0.2.0C): altitude limits, zoom steps, rotation speed and clip
        // planes follow the ground, so a plain is as reachable as a summit. Falls back to the bounding radius.
        float groundRadius = 1f;
        float minDistance = 1.04f;
        float maxDistance = 8f;
        float framedDistance = 3f;

        float yaw;
        float pitch;
        float distance;
        float targetYaw;
        float targetPitch;
        float targetDistance;
        bool dragging;

        public float MinDistance => minDistance;
        public float MaxDistance => maxDistance;
        public float CurrentDistance => distance;

        /// <summary>Altitude above the planet bounding sphere the camera is heading to (render units).</summary>
        public float TargetAltitude => Mathf.Max(targetDistance - groundRadius, 0f);

        /// <summary>
        /// Terrain radius (render units) under a planet-space direction, set by the host (V0.2.0C: the canonical
        /// elevation). Null: the bounding radius is used everywhere.
        /// </summary>
        public Func<Vector3, float> SurfaceRadius { get; set; }

        /// <summary>Current altitude above the terrain under the camera (render units).</summary>
        public float Altitude => Mathf.Max(distance - groundRadius, 0f);

        /// <summary>Planet-space unit direction of the camera for the given angles (the orbit's own convention).</summary>
        static Vector3 OrbitDirection(float pitchDegrees, float yawDegrees)
        {
            double p = pitchDegrees * (Math.PI / 180.0), y = yawDegrees * (Math.PI / 180.0);
            return new Vector3((float)(-Math.Cos(p) * Math.Sin(y)), (float)Math.Sin(p), (float)(-Math.Cos(p) * Math.Cos(y)));
        }

        void UpdateGroundRadius()
        {
            float ground = planetRadius;
            if (SurfaceRadius != null)
            {
                PlanetRenderFrame renderFrame = ResolveFrame();
                Vector3 direction = OrbitDirection(targetPitch, targetYaw);
                if (renderFrame != null)
                {
                    direction = renderFrame.RenderVectorToPlanet(new Vector3d(direction)).ToVector3().normalized;
                }
                float sampled = SurfaceRadius(direction);
                if (sampled > 0f && !float.IsNaN(sampled))
                {
                    ground = Mathf.Min(sampled, planetRadius);
                }
            }
            groundRadius = ground;
            minDistance = groundRadius + Mathf.Max(minAltitude, 0.0001f);
        }

        /// <summary>The target's render frame (floating origin) when it has one; null: plain float placement.</summary>
        public PlanetRenderFrame Frame => ResolveFrame();

        public Transform Target
        {
            get => target;
            set => target = value;
        }

        void Awake()
        {
            cachedCamera = GetComponent<Camera>();
            targetYaw = yaw = defaultYaw;
            targetPitch = pitch = defaultPitch;
            targetDistance = distance = framedDistance;
        }

        /// <summary>
        /// Adapts limits to a planet of the given bounding radius. When
        /// <paramref name="resetView"/> is true the view is snapped to the default framing;
        /// otherwise the current orientation is kept and the distance is clamped.
        /// </summary>
        public void FramePlanet(float boundingRadius, bool resetView)
        {
            planetRadius = Mathf.Max(boundingRadius, 0.001f);
            groundRadius = planetRadius;
            if (cachedCamera == null)
            {
                cachedCamera = GetComponent<Camera>();
            }

            // The near plane follows the altitude (ApplyTransform), so the floor is the altitude floor itself.
            UpdateGroundRadius();
            maxDistance = Mathf.Max(planetRadius * maxDistanceFactor, minDistance * 1.5f);
            framedDistance = Mathf.Clamp(ComputeFramingDistance(), minDistance, maxDistance);

            if (resetView)
            {
                targetYaw = yaw = defaultYaw;
                targetPitch = pitch = defaultPitch;
                targetDistance = distance = framedDistance;
            }
            else
            {
                targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
                distance = Mathf.Clamp(distance, minDistance, maxDistance);
            }
            ApplyTransform();
        }

        /// <summary>Orientation the camera is heading to (degrees; yaw unwrapped).</summary>
        public float TargetYaw => targetYaw;
        public float TargetPitch => targetPitch;
        /// <summary>Distance the camera is heading to (rendering units).</summary>
        public float TargetDistance => targetDistance;
        public float MaxPitch => maxPitch;

        /// <summary>
        /// Jumps to a view (world archive restore, V0.1.3A): pitch and distance are clamped to the current
        /// limits (call after <see cref="FramePlanet"/>); input and smoothing keep working from there.
        /// </summary>
        public void SetView(float yawDegrees, float pitchDegrees, float distanceUnits)
        {
            targetYaw = yaw = yawDegrees;
            targetPitch = pitch = Mathf.Clamp(pitchDegrees, -maxPitch, maxPitch);
            targetDistance = distance = Mathf.Clamp(distanceUnits, minDistance, maxDistance);
            ApplyTransform();
        }

        /// <summary>Smoothly returns to the default orientation and framing distance.</summary>
        public void ResetView()
        {
            // Unwind full turns so the camera takes the short way back.
            yaw = Mathf.Repeat(yaw - defaultYaw + 180f, 360f) - 180f + defaultYaw;
            targetYaw = defaultYaw;
            targetPitch = defaultPitch;
            targetDistance = framedDistance;
        }

        void Update()
        {
            // Update, not LateUpdate: the rebase and the camera placement must precede this frame's LOD tick and
            // every LateUpdate draw (see the class remarks).
            HandleInput();

            float dt = Time.unscaledDeltaTime;
            float rotationBlend = 1f - Mathf.Exp(-rotationSmoothing * dt);
            float zoomBlend = 1f - Mathf.Exp(-zoomSmoothing * dt);
            yaw = Mathf.Lerp(yaw, targetYaw, rotationBlend);
            pitch = Mathf.Lerp(pitch, targetPitch, rotationBlend);
            distance = Mathf.Lerp(distance, targetDistance, zoomBlend);
            ApplyTransform();
        }

        void HandleInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    // A drag that starts on the UI belongs to the UI.
                    dragging = !IsPointerOverUI();
                }
                else if (!mouse.leftButton.isPressed)
                {
                    dragging = false;
                }

                if (dragging)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    // Slow down near the surface so close-up inspection stays controllable.
                    float altitudeScale = Mathf.Clamp((distance - groundRadius) / Mathf.Max(framedDistance - planetRadius, 0.001f), 0.005f, 1.5f);
                    float degreesPerPixel = rotationSensitivity / Mathf.Max(Screen.height, 1) * altitudeScale;
                    targetYaw += delta.x * degreesPerPixel;
                    targetPitch = Mathf.Clamp(targetPitch - delta.y * degreesPerPixel, -maxPitch, maxPitch);
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f && !IsPointerOverUI())
                {
                    // Some platforms report 120 per wheel notch, others 1.
                    float notches = Mathf.Abs(scroll) >= 10f ? scroll / 120f : scroll;
                    notches = Mathf.Clamp(notches, -5f, 5f);
                    UpdateGroundRadius();
                    float altitude = Mathf.Max(targetDistance - groundRadius, minAltitude);
                    altitude *= Mathf.Pow(1f - zoomSensitivity, notches);
                    targetDistance = Mathf.Clamp(groundRadius + altitude, minDistance, maxDistance);
                }
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.rKey.wasPressedThisFrame && !IsTypingInUI())
            {
                ResetView();
            }
        }

        void ApplyTransform()
        {
            // The ground moves under an orbiting camera: never let the smoothed distance dip into it.
            UpdateGroundRadius();
            if (distance < minDistance)
            {
                distance = minDistance;
                targetDistance = Mathf.Max(targetDistance, minDistance);
            }
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            PlanetRenderFrame renderFrame = ResolveFrame();
            if (renderFrame != null)
            {
                // World offset from the planet center, in double: Euler(pitch, yaw, 0) * (0, 0, -distance).
                double p = pitch * (Math.PI / 180.0), y = yaw * (Math.PI / 180.0);
                var offset = new Vector3d(-Math.Cos(p) * Math.Sin(y), Math.Sin(p), -Math.Cos(p) * Math.Cos(y)) * distance;
                // Planet space (the root's local space): general in the root's rotation and scale.
                Vector3d cameraPlanet = renderFrame.RenderVectorToPlanet(offset);
                renderFrame.MaybeRebase(cameraPlanet, Mathf.Max(distance - groundRadius, 0f));
                transform.SetPositionAndRotation(renderFrame.PlanetToRender(cameraPlanet), rotation);
            }
            else
            {
                Vector3 center = target != null ? target.position : Vector3.zero;
                transform.SetPositionAndRotation(center + rotation * new Vector3(0f, 0f, -distance), rotation);
            }
            ApplyClipPlanes();
        }

        /// <summary>
        /// Near and far clip planes follow the altitude above the bounding sphere (V0.2.0C): a fixed near plane of
        /// 0.3 units is 19 km at Earth scale and clips the ground at low altitude. Depth precision: on D3D, Vulkan
        /// and Metal Unity uses a reversed floating-point depth buffer and a tiny near plane costs nothing; OpenGL
        /// has no reversed Z and a 24-bit buffer, where the resolution at depth z is about z^2 / (near * 2^24): with
        /// near = 5e-5 units, 8 mm at 640 m and 76 m at 64 km. Acceptable for the terrain itself; a far-plane split
        /// would be the next step there.
        /// </summary>
        void ApplyClipPlanes()
        {
            if (cachedCamera == null)
            {
                cachedCamera = GetComponent<Camera>();
            }
            float altitude = Mathf.Max(distance - groundRadius, 0f);
            cachedCamera.nearClipPlane = Mathf.Clamp(altitude * NearPlaneAltitudeFraction, MinNearPlane, MaxNearPlane);
            cachedCamera.farClipPlane = Mathf.Clamp(distance + 2f * planetRadius, MinFarPlane, MaxFarPlane);
        }

        PlanetRenderFrame ResolveFrame()
        {
            if (target == null)
            {
                return frame = null;
            }
            // Re-resolved while absent (the frame may be added to the target after it was assigned) or when the target changed.
            if (frame == null || frame.transform != target)
            {
                frame = target.GetComponent<PlanetRenderFrame>();
            }
            return frame;
        }

        float ComputeFramingDistance()
        {
            // Fit the bounding sphere inside the narrowest field of view.
            float verticalHalfFov = cachedCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * cachedCamera.aspect);
            float halfFov = Mathf.Min(verticalHalfFov, horizontalHalfFov);
            return planetRadius * framingMargin / Mathf.Sin(halfFov);
        }

        static bool IsPointerOverUI()
        {
            EventSystem eventSystem = EventSystem.current;
            return eventSystem != null && eventSystem.IsPointerOverGameObject();
        }

        static bool IsTypingInUI()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null || eventSystem.currentSelectedGameObject == null)
            {
                return false;
            }
            var inputField = eventSystem.currentSelectedGameObject.GetComponent<InputField>();
            return inputField != null && inputField.isFocused;
        }
    }
}
