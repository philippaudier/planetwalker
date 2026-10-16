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
    /// </remarks>
    [RequireComponent(typeof(Camera))]
    public sealed class PlanetOrbitCamera : MonoBehaviour
    {
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
        [Tooltip("Minimum camera distance, as a multiple of the planet bounding radius.")]
        [SerializeField, Range(1.01f, 2f)] float minDistanceFactor = 1.04f;
        [Tooltip("Maximum camera distance, as a multiple of the planet bounding radius.")]
        [SerializeField, Range(2f, 50f)] float maxDistanceFactor = 8f;

        [Header("Framing")]
        [Tooltip("Extra space around the planet when framing (1 = planet touches the screen edge).")]
        [SerializeField, Range(1f, 3f)] float framingMargin = 1.35f;
        [SerializeField] float defaultYaw = 0f;
        [SerializeField] float defaultPitch = 15f;

        Camera cachedCamera;
        float planetRadius = 1f;
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
            if (cachedCamera == null)
            {
                cachedCamera = GetComponent<Camera>();
            }

            // Never let the near plane cut into the planet at minimum distance.
            minDistance = planetRadius * minDistanceFactor + cachedCamera.nearClipPlane * 2f;
            maxDistance = Mathf.Max(planetRadius * maxDistanceFactor, minDistance * 1.5f);
            framedDistance = Mathf.Clamp(ComputeFramingDistance(), minDistance, maxDistance);
            cachedCamera.farClipPlane = Mathf.Max(cachedCamera.farClipPlane, maxDistance + planetRadius * 2f);

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

        void LateUpdate()
        {
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
                    float altitudeScale = Mathf.Clamp((distance - planetRadius) / Mathf.Max(framedDistance - planetRadius, 0.001f), 0.05f, 1.5f);
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
                    float altitude = Mathf.Max(targetDistance - planetRadius, 0.001f);
                    altitude *= Mathf.Pow(1f - zoomSensitivity, notches);
                    targetDistance = Mathf.Clamp(planetRadius + altitude, minDistance, maxDistance);
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
            Vector3 center = target != null ? target.position : Vector3.zero;
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(center + rotation * new Vector3(0f, 0f, -distance), rotation);
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
