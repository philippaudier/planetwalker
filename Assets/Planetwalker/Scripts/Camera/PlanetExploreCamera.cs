using System;
using Planetwalker.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Planetwalker.CameraControl
{
    /// <summary>Movement and look input of one exploration step (device independent, for the tests too).</summary>
    public struct ExploreInput
    {
        /// <summary>Forward / backward (-1 .. 1), along the local horizon.</summary>
        public double Forward;
        /// <summary>Right / left (-1 .. 1), along the local horizon.</summary>
        public double Right;
        /// <summary>Ascend / descend (-1 .. 1), along the local up.</summary>
        public double Up;
        /// <summary>Temporary speed boost.</summary>
        public bool Boost;
        /// <summary>Look rotation of this step (degrees): heading to the right, pitch upwards.</summary>
        public double LookYaw;
        public double LookPitch;

        public bool Moves => Forward != 0.0 || Right != 0.0 || Up != 0.0;
    }

    /// <summary>
    /// Free-flight surface exploration camera (V0.2.0D), on the same GameObject as the
    /// <see cref="PlanetOrbitCamera"/> (exactly one of the two is enabled).
    /// </summary>
    /// <remarks>
    /// State in double, in planet space (render units, planet-centered, the root's local space): the position and a
    /// unit forward vector kept tangent to the sphere. Local up is the radial direction; the forward vector is parallel
    /// transported (re-projected onto the new tangent plane) at every step, so there is no Euler angle, no reference
    /// axis and no singularity: crossing cube-face edges or the poles never rolls or flips the view. The pitch is a
    /// separate angle, clamped short of the vertical.
    /// <para>
    /// Movement: WASD along the local horizon, Space / Ctrl along the local up, Shift boost. Horizontal motion keeps the
    /// radial distance (it follows the sphere, not a plane). Speed follows the clearance above the ground (by default one
    /// clearance per second, clamped): comfortable from thousands of kilometers down to a few meters. Every step is a
    /// function of dt only (frame-rate independent). The camera never goes below the minimum clearance above the terrain
    /// or the sea surface, both from the canonical, deterministic elevation function (full detail, double direction:
    /// the function the adaptive terrain samples), never from a mesh.
    /// </para>
    /// <para>
    /// Placement goes through the planet's <see cref="PlanetRenderFrame"/> (floating origin: rebase first, then place
    /// relative to the new origin), like the orbit camera. Clip planes follow the clearance (near plane at a quarter of it,
    /// at least 0.1 m); Unity's reversed depth buffer (D3D, Vulkan, Metal) keeps the depth precision.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(-90)]
    public sealed class PlanetExploreCamera : MonoBehaviour
    {
        public const double MaxPitchDegrees = 89.0;

        [SerializeField] Transform target;

        [Header("Movement")]
        [Tooltip("Speed multiplier: 1 = one clearance (height above the ground) per second.")]
        [SerializeField, Range(0.05f, 10f)] float speedFactor = 1f;
        [Tooltip("Slowest horizontal speed (m/s), close to the ground.")]
        [SerializeField, Range(0.5f, 50f)] float minSpeedMetersPerSecond = 4f;
        [Tooltip("Fastest speed (m/s), far from the planet.")]
        [SerializeField] float maxSpeedMetersPerSecond = 3000000f;
        [Tooltip("Speed multiplier while Shift is held.")]
        [SerializeField, Range(1f, 20f)] float boostFactor = 5f;
        [Tooltip("Minimum clearance above the terrain or the sea surface (meters).")]
        [SerializeField, Range(0.5f, 100f)] float minClearanceMeters = 1.5f; // below eye height

        [Header("Look")]
        [Tooltip("Degrees per pixel of mouse movement.")]
        [SerializeField, Range(0.01f, 1f)] float lookSensitivity = 0.12f;
        [SerializeField, Range(30f, 100f)] float fieldOfView = 60f;

        [Header("Descent")]
        [Tooltip("Entering from higher than this (km) starts a controlled descent.")]
        [SerializeField] float autoDescentAboveKilometers = 150f;
        [Tooltip("Clearance a descent stops at (meters).")]
        [SerializeField] float descentTargetMeters = 2000f;
        [Tooltip("Descent rate: e-folding time of the clearance (seconds).")]
        [SerializeField, Range(0.2f, 5f)] float descentTimeConstant = 1.2f;

        Camera cachedCamera;
        PlanetRenderFrame frame;
        Vector3d position;      // planet space, render units
        Vector3d forward;       // unit, tangent at position
        double pitch;           // radians
        bool hasState;
        bool descending;
        double descentTarget;   // clearance (render units)

        /// <summary>Planet whose terrain bounds the camera (set by the host at every regeneration).</summary>
        public PlanetInstance Planet { get; set; }

        public Transform Target
        {
            get => target;
            set => target = value;
        }

        public float SpeedFactor { get => speedFactor; set => speedFactor = Mathf.Clamp(value, 0.05f, 10f); }
        public float MinClearanceMeters { get => minClearanceMeters; set => minClearanceMeters = Mathf.Clamp(value, 0.5f, 100f); }
        public float FieldOfView { get => fieldOfView; set => fieldOfView = Mathf.Clamp(value, 30f, 100f); }
        public float DescentTargetMeters { get => descentTargetMeters; set => descentTargetMeters = Mathf.Max(1f, value); }

        /// <summary>Planet-space position (render units).</summary>
        public Vector3d Position => position;
        /// <summary>Unit forward along the local horizon (planet space).</summary>
        public Vector3d Forward => forward;
        public Vector3d Up => position.Normalized;
        public double PitchDegrees => pitch * (180.0 / Math.PI);
        public bool Descending => descending;
        /// <summary>Speed of the last step (m/s).</summary>
        public double LastSpeedMetersPerSecond { get; private set; }
        public bool MouseCaptured => Cursor.lockState == CursorLockMode.Locked;

        // ------------------------------------------------------------------ Terrain queries

        PlanetScale Scale => Planet != null ? Planet.Scale : new PlanetScale(1.0, 1.0);

        /// <summary>Canonical elevation (fraction of the radius) under a direction: full detail, the adaptive terrain's function.</summary>
        double TerrainElevation(Vector3d direction) => Planet.Height.GetElevation(direction);

        /// <summary>Radius (render units) of the floor under a direction: terrain, or the sea surface above it.</summary>
        public double FloorRadius(Vector3d direction)
        {
            if (Planet == null)
            {
                return 0.0;
            }
            return Planet.Radius * (1.0 + Math.Max(TerrainElevation(direction.Normalized), Planet.SeaLevel));
        }

        /// <summary>Height above the terrain or the sea surface under the camera (meters).</summary>
        public double ClearanceMeters => Planet == null ? 0.0 : Scale.ToMeters(position.Magnitude - FloorRadius(position));

        /// <summary>Altitude above sea level (meters).</summary>
        public double AltitudeAboveSeaMeters => Planet == null ? 0.0 : Scale.ToMeters(position.Magnitude - Planet.Radius * (1.0 + Planet.SeaLevel));

        /// <summary>Altitude above the reference radius (meters).</summary>
        public double AltitudeAboveReferenceMeters => Planet == null ? 0.0 : Scale.ToMeters(position.Magnitude - Planet.Radius);

        // ------------------------------------------------------------------ State

        /// <summary>
        /// Takes over from a camera pose (render space, e.g. where the orbit camera is): same position and, as far as
        /// possible, the same view direction. Starts a controlled descent when entering from high above the planet.
        /// </summary>
        public void BeginFrom(Vector3 renderPosition, Vector3 renderForward, Vector3 renderUp)
        {
            PlanetRenderFrame renderFrame = ResolveFrame();
            Vector3d planetPosition = renderFrame != null ? renderFrame.RenderToPlanet(renderPosition) : (Vector3d)LocalPoint(renderPosition);
            Vector3d look = ToPlanetVector(renderForward).Normalized;
            Vector3d upHint = ToPlanetVector(renderUp).Normalized;
            SetPose(planetPosition, look, upHint);
            descending = Planet != null && AltitudeAboveSeaMeters > autoDescentAboveKilometers * 1000.0;
            descentTarget = Scale.ToUnits(descentTargetMeters);
            Place();
        }

        /// <summary>
        /// Sets the pose from a planet-space position and look direction. When the look is (nearly) vertical, the
        /// horizontal forward comes from <paramref name="upHint"/> (the previous camera's up: looking down at the planet,
        /// the screen's top becomes the direction of travel).
        /// </summary>
        public void SetPose(Vector3d planetPosition, Vector3d look, Vector3d upHint)
        {
            Vector3d up = planetPosition.Normalized;
            if (up.SqrMagnitude == 0.0)
            {
                up = Vector3d.Up;
                planetPosition = up * (Planet != null ? Planet.Radius * 2.0 : 1.0);
            }
            position = planetPosition;
            double vertical = Vector3d.Dot(look, up);
            Vector3d horizontal = look - up * vertical;
            if (horizontal.Magnitude < 1e-3)
            {
                horizontal = upHint - up * Vector3d.Dot(upHint, up);
            }
            if (horizontal.Magnitude < 1e-9)
            {
                horizontal = PlanetTangentFrame.At(up).North;
            }
            forward = horizontal.Normalized;
            pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, vertical)));
            ClampPitch();
            hasState = true;
            EnforceClearance();
        }

        /// <summary>Eye height of a standing person (meters): the last step of <see cref="NextDescentTarget"/>.</summary>
        public const double EyeHeightMeters = 1.7;

        /// <summary>
        /// Clearance the next press of Descend goes to (meters): down by a decade while above ~10 x the minimum clearance,
        /// then eye height (never below the minimum clearance). Equal to the current clearance when already there.
        /// </summary>
        public double NextDescentTarget()
        {
            double eye = Math.Max(EyeHeightMeters, minClearanceMeters);
            double clearance = ClearanceMeters;
            double decade = clearance * 0.1;
            if (decade >= Math.Max(5.0 * minClearanceMeters, 2.0 * eye))
            {
                return decade;
            }
            return Math.Min(eye, clearance);
        }

        /// <summary>Starts (or restarts) a controlled descent to a clearance (meters); any movement input cancels it.</summary>
        public void StartDescent(double targetClearanceMeters)
        {
            descentTarget = Scale.ToUnits(Math.Max(targetClearanceMeters, minClearanceMeters));
            descending = true;
        }

        public void StopDescent()
        {
            descending = false;
        }

        /// <summary>
        /// One step of dt seconds with the given input (the per-frame update uses it with the devices' input; the tests
        /// call it directly). A pure function of the state, the input and dt.
        /// </summary>
        public void Advance(double dt, in ExploreInput input)
        {
            if (!hasState || Planet == null || dt <= 0.0)
            {
                Place();
                return;
            }
            // Look: heading around the local up, pitch clamped.
            Vector3d up = position.Normalized;
            if (input.LookYaw != 0.0)
            {
                forward = RotateAround(forward, up, input.LookYaw * (Math.PI / 180.0));
            }
            pitch += input.LookPitch * (Math.PI / 180.0);
            ClampPitch();

            if (input.Moves)
            {
                descending = false;
            }
            double clearance = Math.Max(position.Magnitude - FloorRadius(position), 0.0);
            double speedMeters = Math.Max(minSpeedMetersPerSecond, Math.Min(maxSpeedMetersPerSecond, Scale.ToMeters(clearance) * speedFactor));
            if (input.Boost)
            {
                speedMeters *= boostFactor;
            }
            LastSpeedMetersPerSecond = input.Moves ? speedMeters : 0.0;
            double step = Scale.ToUnits(speedMeters) * dt;

            if (descending)
            {
                // Exponential approach of the clearance (log-uniform: smooth from orbit to the ground).
                double current = Math.Max(clearance, descentTarget * 1e-3);
                double next = descentTarget + (current - descentTarget) * Math.Exp(-dt / descentTimeConstant);
                position = up * (position.Magnitude - (current - next));
                if (Math.Abs(next - descentTarget) < descentTarget * 0.01)
                {
                    descending = false;
                }
            }

            // Horizontal motion along the sphere (keeps the radial distance), vertical motion along the local up.
            Vector3d right = Vector3d.Cross(up, forward).Normalized;
            Vector3d horizontal = forward * input.Forward + right * input.Right;
            double length = horizontal.Magnitude;
            if (length > 1.0)
            {
                horizontal /= length;
            }
            double radius = position.Magnitude + step * Math.Max(-1.0, Math.Min(1.0, input.Up));
            if (horizontal.SqrMagnitude > 0.0)
            {
                Vector3d moved = position + horizontal * step;
                Vector3d newUp = moved.Normalized;
                // Parallel transport of the forward vector to the new tangent plane.
                Vector3d transported = forward - newUp * Vector3d.Dot(forward, newUp);
                if (transported.SqrMagnitude > 1e-18)
                {
                    forward = transported.Normalized;
                }
                position = newUp * radius;
            }
            else
            {
                position = up * radius;
            }
            EnforceClearance();
            Place();
        }

        /// <summary>
        /// Distance (render units) from a position to the terrain or sea surface (V0.2.0F.3): the minimum over the
        /// authoritative surface under the position and on three rings around it (radii 1/4, 1/2 and 1 times the vertical
        /// clearance, 8 directions each). On flat ground it equals the vertical clearance; on a slope of angle a it tends to
        /// clearance x cos(a): 1.7 m above a 76-degree slope is only 0.41 m from it. Above 5 km the vertical clearance is
        /// returned (the relief is then far away compared with the clip planes). 25 samples of the canonical elevation.
        /// </summary>
        public double SurfaceDistanceUnits(Vector3d at)
        {
            if (Planet == null)
            {
                return 1.0;
            }
            Vector3d up = at.Normalized;
            double vertical = at.Magnitude - FloorRadius(up);
            if (vertical <= 0.0 || vertical > Scale.ToUnits(5000.0))
            {
                return Math.Max(vertical, 0.0);
            }
            Vector3d north = PlanetTangentFrame.At(up).North;
            Vector3d east = Vector3d.Cross(north, up).Normalized;
            double best = vertical;
            double radiusUnits = at.Magnitude;
            foreach (double ring in RingFractions)
            {
                double angle = vertical * ring / radiusUnits;
                for (int k = 0; k < 8; k++)
                {
                    double a = k * (Math.PI / 4.0);
                    Vector3d d = (up + (north * Math.Cos(a) + east * Math.Sin(a)) * angle).Normalized;
                    double distance = (at - d * FloorRadius(d)).Magnitude;
                    if (distance < best)
                    {
                        best = distance;
                    }
                }
            }
            return best;
        }

        static readonly double[] RingFractions = { 0.25, 0.5, 1.0 };

        /// <summary>Distance to the terrain or sea surface (meters; see <see cref="SurfaceDistanceUnits"/>).</summary>
        public double SurfaceDistanceMeters => Planet == null ? 0.0 : Scale.ToMeters(SurfaceDistanceUnits(position));

        void EnforceClearance()
        {
            if (Planet == null)
            {
                return;
            }
            double floor = FloorRadius(position) + Scale.ToUnits(minClearanceMeters);
            double r = position.Magnitude;
            if (r < floor)
            {
                position = position.Normalized * floor;
            }
            // V0.2.0F.3: the minimum clearance holds for the distance to the surface, not only vertically (on steep slopes
            // the surface in front is much closer than the ground below). Raised along the local up until it holds.
            double minimum = Scale.ToUnits(minClearanceMeters);
            for (int iteration = 0; iteration < 4; iteration++)
            {
                double distance = SurfaceDistanceUnits(position);
                if (distance >= minimum * 0.999)
                {
                    break;
                }
                double vertical = position.Magnitude - FloorRadius(position);
                double cosine = Math.Max(distance / Math.Max(vertical, 1e-12), 0.15);
                position = position.Normalized * (position.Magnitude + (minimum - distance) / cosine);
            }
            // Keep the forward vector exactly tangent (it drifts by rounding otherwise).
            Vector3d up = position.Normalized;
            Vector3d tangent = forward - up * Vector3d.Dot(forward, up);
            if (tangent.SqrMagnitude > 1e-18)
            {
                forward = tangent.Normalized;
            }
        }

        void ClampPitch()
        {
            double limit = MaxPitchDegrees * (Math.PI / 180.0);
            pitch = Math.Max(-limit, Math.Min(limit, pitch));
        }

        static Vector3d RotateAround(Vector3d v, Vector3d axis, double angle)
        {
            // Rodrigues (axis unit, v perpendicular or not).
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + Vector3d.Cross(axis, v) * s + axis * (Vector3d.Dot(axis, v) * (1.0 - c));
        }

        /// <summary>View direction (planet space): forward tilted by the pitch around the local right axis.</summary>
        public Vector3d LookDirection
        {
            get
            {
                Vector3d up = position.Normalized;
                return (forward * Math.Cos(pitch) + up * Math.Sin(pitch)).Normalized;
            }
        }

        // ------------------------------------------------------------------ Placement

        /// <summary>Places the camera (rebasing the floating origin first) and sets its clip planes and field of view.</summary>
        public void Place()
        {
            if (!hasState)
            {
                return;
            }
            if (cachedCamera == null)
            {
                cachedCamera = GetComponent<Camera>();
            }
            double clearanceUnits = Planet != null ? Math.Max(position.Magnitude - FloorRadius(position), 0.0) : 1.0;
            PlanetRenderFrame renderFrame = ResolveFrame();
            Vector3 renderPosition;
            if (renderFrame != null)
            {
                renderFrame.MaybeRebase(position, clearanceUnits);
                renderPosition = renderFrame.PlanetToRender(position);
            }
            else
            {
                renderPosition = target != null ? target.TransformPoint(position.ToVector3()) : position.ToVector3();
            }
            Vector3 look = ToRenderVector(LookDirection), upRender = ToRenderVector(position.Normalized);
            transform.SetPositionAndRotation(renderPosition, Quaternion.LookRotation(look, upRender));

            // Clip planes: the near plane at a quarter of the distance to the surface (V0.2.0F.3; the vertical clearance
            // before, which on steep slopes put the near plane beyond the slope in front: the mountain was cut open and
            // the far landscape seen through it), at least 5 cm; the far plane past the whole planet. Reversed-Z depth
            // (D3D / Vulkan / Metal) keeps the depth resolution at these ratios.
            float scale = target != null ? Mathf.Abs(target.lossyScale.x) : 1f;
            double minNear = Scale.ToUnits(0.05);
            double surfaceUnits = Planet != null ? SurfaceDistanceUnits(position) : clearanceUnits;
            cachedCamera.nearClipPlane = (float)Math.Max(surfaceUnits * 0.25, minNear) * scale;
            double planetRadius = Planet != null ? Planet.Radius : 1.0;
            cachedCamera.farClipPlane = (float)Math.Min(position.Magnitude + 2.5 * planetRadius, 1e6) * scale;
            cachedCamera.fieldOfView = fieldOfView;
        }

        Vector3 ToRenderVector(Vector3d planetVector)
        {
            PlanetRenderFrame renderFrame = ResolveFrame();
            if (renderFrame != null)
            {
                return renderFrame.PlanetVectorToRender(planetVector).ToVector3().normalized;
            }
            return target != null ? target.TransformDirection(planetVector.ToVector3()).normalized : planetVector.ToVector3().normalized;
        }

        Vector3d ToPlanetVector(Vector3 renderVector)
        {
            PlanetRenderFrame renderFrame = ResolveFrame();
            if (renderFrame != null)
            {
                return renderFrame.RenderVectorToPlanet(new Vector3d(renderVector));
            }
            return target != null ? new Vector3d(target.InverseTransformDirection(renderVector)) : new Vector3d(renderVector);
        }

        Vector3 LocalPoint(Vector3 renderPosition) => target != null ? target.InverseTransformPoint(renderPosition) : renderPosition;

        PlanetRenderFrame ResolveFrame()
        {
            if (target == null)
            {
                return frame = null;
            }
            if (frame == null || frame.transform != target)
            {
                frame = target.GetComponent<PlanetRenderFrame>();
            }
            return frame;
        }

        /// <summary>
        /// After a regeneration or a world load: keeps the camera where it is (same position and view), only pushed
        /// above the new terrain if it would now be inside it. Never a jump to an unrelated place.
        /// </summary>
        public void OnPlanetChanged(PlanetInstance planet)
        {
            Planet = planet;
            if (hasState)
            {
                EnforceClearance();
                Place();
            }
        }

        // ------------------------------------------------------------------ Devices

        void OnDisable()
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            descending = false;
        }

        void Update()
        {
            ExploreInput input = default;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool typing = IsTypingInUI();
            if (keyboard != null && !typing)
            {
                input.Forward = (keyboard.wKey.isPressed ? 1.0 : 0.0) - (keyboard.sKey.isPressed ? 1.0 : 0.0);
                input.Right = (keyboard.dKey.isPressed ? 1.0 : 0.0) - (keyboard.aKey.isPressed ? 1.0 : 0.0);
                input.Up = (keyboard.spaceKey.isPressed ? 1.0 : 0.0) - (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ? 1.0 : 0.0);
                input.Boost = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                if (keyboard.escapeKey.wasPressedThisFrame && MouseCaptured)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            if (mouse != null)
            {
                // Capture the mouse with a click on the view (not on the UI); Escape releases it.
                if (!MouseCaptured && mouse.leftButton.wasPressedThisFrame && !IsPointerOverUI())
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                // Look while captured, or with the right button held.
                if (MouseCaptured || (mouse.rightButton.isPressed && !IsPointerOverUI()))
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    input.LookYaw = delta.x * lookSensitivity;
                    input.LookPitch = delta.y * lookSensitivity;
                }
            }
            Advance(Time.unscaledDeltaTime, input);
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
            var inputField = eventSystem.currentSelectedGameObject.GetComponent<UnityEngine.UI.InputField>();
            return inputField != null && inputField.isFocused;
        }
    }
}
