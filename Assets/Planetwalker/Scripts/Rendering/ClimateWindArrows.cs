using System.Collections.Generic;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Sparse wind arrows for the climate winds view: flat arrows on the displayed terrain at evenly
    /// spread points (Fibonacci sphere), oriented along the prevailing wind, length by speed. Built
    /// once per climate result / terrain mesh, drawn with the unlit debug material.
    /// </summary>
    public sealed class ClimateWindArrows
    {
        public const int ArrowCount = 1400;
        static readonly Color ArrowColor = new Color(0.93f, 0.95f, 0.98f);

        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> triangles = new List<int>();

        public int LastArrowCount { get; private set; }

        public void Build(Mesh mesh, ClimateResult climate, PlanetSurfaceSampler sampler, float radius)
        {
            vertices.Clear();
            colors.Clear();
            triangles.Clear();
            LastArrowCount = 0;
            const float goldenAngle = 2.39996323f;
            float spacing = Mathf.Sqrt(4f * Mathf.PI / ArrowCount);
            for (int i = 0; i < ArrowCount; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / ArrowCount;
                float r = Mathf.Sqrt(1f - y * y);
                float phi = i * goldenAngle;
                var p = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
                Vector3 wind = climate.SampleWind(p);
                float speed = wind.magnitude;
                if (speed < 0.05f)
                {
                    continue;
                }
                Vector3 forward = wind / speed;
                Vector3 side = Vector3.Cross(p, forward);
                float length = spacing * (0.35f + 0.35f * Mathf.Clamp01(speed / 1.2f));
                float shaft = spacing * 0.05f, head = spacing * 0.16f;
                Vector3 tail = p - forward * length * 0.5f, neck = p + forward * length * 0.15f, tip = p + forward * length * 0.5f;
                int start = vertices.Count;
                Add(sampler, radius, tail + side * shaft);
                Add(sampler, radius, tail - side * shaft);
                Add(sampler, radius, neck + side * shaft);
                Add(sampler, radius, neck - side * shaft);
                Add(sampler, radius, neck + side * head);
                Add(sampler, radius, neck - side * head);
                Add(sampler, radius, tip);
                Triangle(start, start + 1, start + 2);
                Triangle(start + 2, start + 1, start + 3);
                Triangle(start + 4, start + 5, start + 6);
                LastArrowCount++;
            }
            mesh.Clear();
            mesh.name = "Climate Wind Arrows";
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 2.4f));
        }

        void Add(PlanetSurfaceSampler sampler, float radius, Vector3 direction)
        {
            Vector3 d = direction.normalized;
            // Slightly above the displayed terrain (and never below the sea surface sphere of radius).
            float height = Mathf.Max(sampler.Sample(d).magnitude, radius);
            vertices.Add(d * (height + radius * 0.002f));
            colors.Add(QualitySettings.activeColorSpace == ColorSpace.Linear ? ArrowColor.linear : ArrowColor);
        }

        /// <summary>Adds a triangle facing outward (terrain winding convention, culled from below).</summary>
        void Triangle(int a, int b, int c)
        {
            Vector3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), pa + pb + pc) < 0f)
            {
                (b, c) = (c, b);
            }
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }
    }
}
