using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Floating render origin of a planet (V0.2.0C). Lives on the planet root (the planet visual's GameObject) and
    /// owns the mapping between planet space (double, planet-centered, render units) and render space (float, Unity
    /// world space). The planet root is placed so that the planet-space point <see cref="Origin"/> lies at the
    /// world origin; everything near the camera is then expressed in small float coordinates and keeps its
    /// precision, whatever the planet's size.
    /// </summary>
    /// <remarks>
    /// Definition: for a planet-space position P, its render position is <c>R * (S * (P - Origin))</c> with R and S
    /// the root's rotation and (lossy) scale, which are never touched here; the root's world position is therefore
    /// <c>R * (S * -Origin)</c>. The subtraction is done in double and rounded to float once
    /// (<see cref="PlanetToRender"/>, <see cref="PatchMatrix"/>).
    /// <para>
    /// Every render system of the planet derives its placement from the root transform: the legacy faces and the
    /// ocean are children; atmosphere, clouds, rivers, lakes, wind arrows and overlays are drawn with the root's
    /// <c>localToWorldMatrix</c>; the haze shell center is <c>transform.position</c>; thumbnails place their camera
    /// relative to it. Moving the root therefore moves the whole planet consistently, which is why automatic
    /// rebasing is on by default. The adaptive terrain patches are the one system that goes through
    /// <see cref="PatchMatrix"/> instead of the root matrix. To make both paths agree bit for bit, the origin is
    /// snapped: the root's float world position is the single source of truth and <see cref="Origin"/> is derived
    /// from it, so the origin is always exactly representable and never goes stale (no serialized state, nothing to
    /// lose over a domain reload).
    /// </para>
    /// <para>
    /// The frame only acts when asked (<see cref="Rebase"/>, <see cref="MaybeRebase"/>, <see cref="Reset"/>): it has no
    /// per-frame work of its own and never moves the root in edit mode on its own initiative. Disabling or
    /// destroying it puts the root back where it was authored (world zero) if it moved it, so leaving Play mode
    /// leaves the scene untouched. Independent of any camera class: positions in, positions out.
    /// </para>
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Planetwalker/Planet Render Frame")]
    public sealed class PlanetRenderFrame : MonoBehaviour
    {
        /// <summary>Camera render-space distance from the world origin, as a fraction of its altitude, that triggers a rebase.</summary>
        public const double RebaseAltitudeFraction = 0.25;

        /// <summary>Smallest rebase distance (render units), whatever the altitude.</summary>
        public const double MinRebaseDistance = 1e-3;

        [Tooltip("Move the planet root so that the camera stays near the world origin (floating origin). Safe: every planet "
            + "render system (faces, ocean, atmosphere, clouds, rivers, lakes, overlays, thumbnails) follows the root transform. "
            + "Off: no further rebase (the planet keeps its render position) and float precision degrades as the camera moves away.")]
        [SerializeField] bool autoRebase = true;

        [NonSerialized] int rebaseCount;
        [NonSerialized] bool movedRoot;

        /// <summary>The planet's render frame (created on first use; never duplicated).</summary>
        public static PlanetRenderFrame GetOrCreate(Transform planetRoot)
        {
            if (planetRoot == null)
            {
                throw new ArgumentNullException(nameof(planetRoot));
            }
            var frame = planetRoot.GetComponent<PlanetRenderFrame>();
            if (frame == null)
            {
                frame = planetRoot.gameObject.AddComponent<PlanetRenderFrame>();
            }
            return frame;
        }

        /// <summary>Raised after every rebase (the root moved).</summary>
        public event Action Rebased;

        /// <summary>Rebase automatically when the camera drifts away from the world origin (<see cref="MaybeRebase"/>).</summary>
        public bool AutoRebase
        {
            get => autoRebase;
            set => autoRebase = value;
        }

        /// <summary>
        /// Planet-space point currently mapped to the world origin (zero: the planet center is at world zero).
        /// Derived from the root's world position, hence always snapped.
        /// </summary>
        public Vector3d Origin => -RenderVectorToPlanet(transform.position);

        /// <summary>Distance (render units) between the planet center and the render origin.</summary>
        public double OriginOffsetUnits => Origin.Magnitude;

        /// <summary>World position of the planet root (<c>R * (S * -Origin)</c>, a float vector by construction).</summary>
        public Vector3 WorldRootPosition => transform.position;

        /// <summary>Rebases performed since the frame was created.</summary>
        public int RebaseCount => rebaseCount;

        /// <summary>Render (world) position of a planet-space position: subtracted in double, rounded once.</summary>
        public Vector3 PlanetToRender(Vector3d planetPosition) => PlanetVectorToRender(planetPosition - Origin).ToVector3();

        /// <summary>Planet-space position of a render (world) position.</summary>
        public Vector3d RenderToPlanet(Vector3 renderPosition) => RenderVectorToPlanet(renderPosition) + Origin;

        /// <summary>Render-space vector of a planet-space vector (rotation and scale of the root, no origin), in double.</summary>
        public Vector3d PlanetVectorToRender(Vector3d planetVector)
        {
            Vector3 s = transform.lossyScale;
            return Rotate(transform.rotation, new Vector3d(planetVector.x * s.x, planetVector.y * s.y, planetVector.z * s.z));
        }

        /// <summary>Planet-space vector of a render-space vector (inverse rotation and scale of the root, no origin), in double.</summary>
        public Vector3d RenderVectorToPlanet(Vector3d renderVector)
        {
            Vector3 s = transform.lossyScale;
            Vector3d v = RotateInverse(transform.rotation, renderVector);
            return new Vector3d(Divide(v.x, s.x), Divide(v.y, s.y), Divide(v.z, s.z));
        }

        /// <summary>
        /// Object-to-world matrix of an adaptive patch whose vertices are relative to <paramref name="patchOrigin"/>
        /// (planet space): the root's rotation and scale with a translation computed in double. The single draw
        /// matrix of the adaptive terrain and its diagnostics.
        /// </summary>
        public Matrix4x4 PatchMatrix(Vector3 patchOrigin) => Matrix4x4.TRS(PlanetToRender(patchOrigin), transform.rotation, transform.lossyScale);

        /// <summary>
        /// Rebases when the camera is further from the world origin, in render space, than a quarter of its
        /// altitude (at least <see cref="MinRebaseDistance"/>): rare in orbit, within meters near the surface.
        /// Does nothing when <see cref="AutoRebase"/> is off or the frame is disabled (its root is then back at zero).
        /// Returns true when it rebased.
        /// </summary>
        public bool MaybeRebase(Vector3d cameraPlanetPosition, double altitudeUnits)
        {
            if (!autoRebase || !isActiveAndEnabled)
            {
                return false;
            }
            double threshold = Math.Max(RebaseAltitudeFraction * altitudeUnits, MinRebaseDistance);
            if (PlanetVectorToRender(cameraPlanetPosition - Origin).Magnitude <= threshold)
            {
                return false;
            }
            Rebase(cameraPlanetPosition);
            return true;
        }

        /// <summary>Rebases now, whatever the threshold (diagnostic button).</summary>
        public void RebaseNow(Vector3d newOrigin) => Rebase(newOrigin);

        /// <summary>
        /// Maps <paramref name="newOrigin"/> (planet space) to the world origin: moves the planet root to
        /// <c>R * (S * -newOrigin)</c> rounded to float. That float position defines the effective origin (snapping),
        /// hence every system drawn through the root transform coincides exactly with the patches drawn through
        /// <see cref="PatchMatrix"/>.
        /// </summary>
        public void Rebase(Vector3d newOrigin)
        {
            transform.position = PlanetVectorToRender(-newOrigin).ToVector3();
            movedRoot = true;
            rebaseCount++;
            Rebased?.Invoke();
        }

        /// <summary>Puts the planet center back at the world origin (the authored state). Also Unity's Reset message: harmless.</summary>
        public void Reset()
        {
            Vector3 p = transform.position;
            if (p.x != 0f || p.y != 0f || p.z != 0f)
            {
                transform.position = Vector3.zero;
            }
            movedRoot = false;
        }

        void OnDisable()
        {
            // Only undo what this frame did: a root it never moved (edit mode, no rebase yet) stays as authored.
            if (movedRoot)
            {
                Reset();
            }
        }

        static double Divide(double value, float scale) => scale != 0f ? value / scale : 0.0;

        /// <summary>Rotates a double vector by a unit quaternion (v + w t + q x t with t = 2 q x v), in double.</summary>
        static Vector3d Rotate(Quaternion q, Vector3d v) => Rotate(q.x, q.y, q.z, q.w, v);

        static Vector3d RotateInverse(Quaternion q, Vector3d v) => Rotate(-q.x, -q.y, -q.z, q.w, v);

        /// <summary>Rotation by a quaternion in double, renormalized in double first: a float quaternion is unit only to
        /// ~1e-7, which would leave a 1e-5 residual on a 100-unit round trip (rotate, then rotate by the conjugate).</summary>
        static Vector3d Rotate(double x, double y, double z, double w, Vector3d v)
        {
            double norm = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (norm > 0 && norm != 1.0)
            {
                x /= norm;
                y /= norm;
                z /= norm;
                w /= norm;
            }
            var axis = new Vector3d(x, y, z);
            Vector3d t = Vector3d.Cross(axis, v) * 2.0;
            return v + t * w + Vector3d.Cross(axis, t);
        }
    }
}
