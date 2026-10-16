using System;
using System.Diagnostics;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Planetwalker.Library
{
    /// <summary>Timings of the last thumbnail capture (milliseconds).</summary>
    public struct ThumbnailCaptureStats
    {
        /// <summary>Camera setup, draw submission and render request (CPU).</summary>
        public double RenderMilliseconds;
        /// <summary>GPU to CPU readback; includes waiting for the GPU to finish the render.</summary>
        public double ReadbackMilliseconds;
        /// <summary>2x2 downsampling and PNG encoding (CPU).</summary>
        public double EncodeMilliseconds;
        public int Bytes;
    }

    /// <summary>
    /// Renders the planet as it is displayed into a small PNG (V0.1.3B), with an isolated, temporary camera and
    /// render texture: the user's camera, the scene and the planet are never modified, nothing is regenerated.
    /// Captures the Natural Planet presentation only (never a debug view); UI (screen-space overlay) and gizmos
    /// are not part of a camera render.
    /// </summary>
    /// <remarks>
    /// Framing: the whole planet with its atmosphere and clouds, centered with a margin, seen from the lit side
    /// (the sun 35 degrees to one side, 18 degrees above the equator), so every thumbnail is daylit and comparable
    /// whatever the solar time. Rendered at twice the size and averaged 2x2 (anti-aliasing without MSAA targets).
    /// </remarks>
    public static class WorldThumbnailRenderer
    {
        public const int DefaultSize = 256;
        public const float FieldOfView = 30f;
        public const float Margin = 1.1f;
        const int Supersampling = 2;

        public static readonly Color Background = new Color(0.012f, 0.015f, 0.024f, 1f);

        public static ThumbnailCaptureStats LastStats { get; private set; }

        /// <summary>Captures the displayed planet. Throws InvalidOperationException when it cannot be captured faithfully.</summary>
        public static byte[] CapturePng(PlanetVisualController visual, int size = DefaultSize)
        {
            if (visual == null || visual.Planet == null)
            {
                throw new InvalidOperationException("No planet is displayed.");
            }
            if (visual.DebugView != PlanetDebugView.None)
            {
                throw new InvalidOperationException("A scientific view is shown; previews capture the Natural Planet only.");
            }
            size = Mathf.Clamp(size, 32, 1024);
            int renderSize = size * Supersampling;
            var stats = new ThumbnailCaptureStats();
            var watch = Stopwatch.StartNew();

            Transform planet = visual.transform;
            float scale = Mathf.Abs(planet.lossyScale.x);
            float radius = Mathf.Max(visual.BoundingRadius, Mathf.Max(visual.AtmosphereRadius, visual.CloudRadius)) * scale;
            Vector3 center = planet.position;
            Vector3 axis = planet.TransformDirection(visual.Climate.rotationAxis.sqrMagnitude > 0f ? visual.Climate.rotationAxis : Vector3.up).normalized;
            Vector3 view = ViewDirection(visual.SunDirection, axis);
            float distance = radius * Margin / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad);

            GameObject cameraObject = null;
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previousActive = RenderTexture.active;
            // The adaptive terrain (V0.2.0B) draws for the main camera only and may be mid-refinement: previews always
            // capture the complete legacy faces, shown just for this synchronous render.
            bool facesHidden = !visual.TerrainFacesVisible;
            try
            {
                if (facesHidden)
                {
                    visual.ForceTerrainFacesVisible(true);
                }
                cameraObject = new GameObject("Planetwalker Thumbnail Camera") { hideFlags = HideFlags.HideAndDontSave };
                cameraObject.transform.SetPositionAndRotation(center + view * distance, Quaternion.LookRotation(-view, axis));
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                camera.cullingMask = 1 << visual.gameObject.layer;
                camera.fieldOfView = FieldOfView;
                camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 1.5f);
                camera.farClipPlane = distance + radius * 1.5f;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;

                target = new RenderTexture(renderSize, renderSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "Planetwalker Thumbnail",
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 1,
                };
                target.Create();
                camera.targetTexture = target;

                // Atmosphere, clouds, lakes and rivers are drawn without GameObjects: submit them for this camera.
                visual.SubmitPresentationDraws(camera);
                var request = new RenderPipeline.StandardRequest { destination = target };
                if (RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                else
                {
                    camera.Render();
                }
                stats.RenderMilliseconds = watch.Elapsed.TotalMilliseconds;

                watch.Restart();
                readback = new Texture2D(renderSize, renderSize, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0, false);
                Color32[] pixels = readback.GetPixels32();
                stats.ReadbackMilliseconds = watch.Elapsed.TotalMilliseconds;

                watch.Restart();
                Color32[] small = Downsample(pixels, renderSize, Supersampling);
                readback.Reinitialize(size, size);
                readback.SetPixels32(small);
                readback.Apply(false);
                byte[] png = readback.EncodeToPNG();
                stats.EncodeMilliseconds = watch.Elapsed.TotalMilliseconds;
                if (png == null || png.Length == 0)
                {
                    throw new InvalidOperationException("PNG encoding failed.");
                }
                stats.Bytes = png.Length;
                LastStats = stats;
                return png;
            }
            finally
            {
                if (facesHidden)
                {
                    visual.ForceTerrainFacesVisible(false);
                }
                RenderTexture.active = previousActive;
                if (cameraObject != null)
                {
                    cameraObject.GetComponent<Camera>().targetTexture = null;
                }
                DestroySafe(cameraObject);
                if (target != null)
                {
                    target.Release();
                }
                DestroySafe(target);
                DestroySafe(readback);
            }
        }

        /// <summary>Direction from the planet center to the camera: on the lit side, the sun 35 degrees to one side, 18 degrees north.</summary>
        public static Vector3 ViewDirection(Vector3 toSun, Vector3 axis)
        {
            Vector3 equatorial = Vector3.ProjectOnPlane(toSun, axis);
            if (equatorial.sqrMagnitude < 1e-6f)
            {
                equatorial = Vector3.ProjectOnPlane(Vector3.forward, axis);
                if (equatorial.sqrMagnitude < 1e-6f)
                {
                    equatorial = Vector3.ProjectOnPlane(Vector3.right, axis);
                }
            }
            Vector3 view = Quaternion.AngleAxis(-35f, axis) * equatorial.normalized;
            float elevation = 18f * Mathf.Deg2Rad;
            return (view * Mathf.Cos(elevation) + axis * Mathf.Sin(elevation)).normalized;
        }

        static Color32[] Downsample(Color32[] source, int sourceSize, int factor)
        {
            int size = sourceSize / factor;
            var result = new Color32[size * size];
            int samples = factor * factor;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = (y * factor + dy) * sourceSize + x * factor;
                        for (int dx = 0; dx < factor; dx++)
                        {
                            Color32 c = source[row + dx];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                        }
                    }
                    result[y * size + x] = new Color32((byte)(r / samples), (byte)(g / samples), (byte)(b / samples), 255);
                }
            }
            return result;
        }

        static void DestroySafe(Object target)
        {
            if (target == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
