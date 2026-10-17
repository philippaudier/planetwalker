using System;
using System.Collections.Generic;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Continuity probe across the cube-face edges (V0.2.0F.1). A field is continuous at an edge when the difference
    /// between two points on either side shrinks with their separation: here 20 m, 2 m and 0.2 m apart (Earth scale).
    /// A steep but continuous gradient gives about 1/100 of the 20 m difference at 0.2 m; a seam keeps its jump whatever
    /// the separation. Points along all twelve edges (corners excluded), stepping from one face into the other.
    /// </summary>
    static class EdgeContinuity
    {
        public struct Result
        {
            public int Pairs;
            public float Worst20m, Worst2m, Worst02m;
            public string WorstAt;
        }

        /// <summary>Probes a vector-valued field (all components compared, max absolute difference).</summary>
        public static Result Probe(Func<Vector3d, float[]> field, int samplesPerEdge = 40)
        {
            const double tenMeters = 1.57e-6; // radians at Earth scale
            var result = new Result();
            for (int t = 1; t < samplesPerEdge; t++)
            {
                double s = -1.0 + 2.0 * t / samplesPerEdge;
                foreach (Vector3d corner in Edges(s))
                {
                    Vector3d d = corner.Normalized;
                    var axes = new List<Vector3d>();
                    if (Math.Abs(corner.x) == 1.0) axes.Add(new Vector3d(corner.x, 0, 0));
                    if (Math.Abs(corner.y) == 1.0) axes.Add(new Vector3d(0, corner.y, 0));
                    if (Math.Abs(corner.z) == 1.0) axes.Add(new Vector3d(0, 0, corner.z));
                    Vector3d toward = axes[0] - axes[1];
                    Vector3d across = (toward - d * Vector3d.Dot(toward, d)).Normalized;
                    float j20 = Jump(field, d, across, tenMeters);
                    float j2 = Jump(field, d, across, tenMeters * 0.1);
                    float j02 = Jump(field, d, across, tenMeters * 0.01);
                    result.Pairs++;
                    result.Worst20m = Mathf.Max(result.Worst20m, j20);
                    result.Worst2m = Mathf.Max(result.Worst2m, j2);
                    if (j02 > result.Worst02m)
                    {
                        result.Worst02m = j02;
                        result.WorstAt = $"{d.ToVector3()} (20 m {j20:0.0000}, 2 m {j2:0.0000}, 0.2 m {j02:0.0000})";
                    }
                }
            }
            return result;
        }

        static float Jump(Func<Vector3d, float[]> field, Vector3d d, Vector3d across, double half)
        {
            float[] a = field((d + across * half).Normalized);
            float[] b = field((d - across * half).Normalized);
            float jump = 0f;
            for (int k = 0; k < a.Length; k++)
            {
                jump = Mathf.Max(jump, Mathf.Abs(a[k] - b[k]));
            }
            return jump;
        }

        static IEnumerable<Vector3d> Edges(double s)
        {
            yield return new Vector3d(1, 1, s);
            yield return new Vector3d(1, -1, s);
            yield return new Vector3d(-1, 1, s);
            yield return new Vector3d(-1, -1, s);
            yield return new Vector3d(1, s, 1);
            yield return new Vector3d(1, s, -1);
            yield return new Vector3d(-1, s, 1);
            yield return new Vector3d(-1, s, -1);
            yield return new Vector3d(s, 1, 1);
            yield return new Vector3d(s, 1, -1);
            yield return new Vector3d(s, -1, 1);
            yield return new Vector3d(s, -1, -1);
        }
    }
}
