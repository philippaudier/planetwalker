using System;
using System.Collections.Generic;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>Result of <see cref="PatchCoverage.Validate"/>.</summary>
    public struct CoverageReport
    {
        public bool Complete;
        public bool NonOverlapping;
        public bool Balanced;
        /// <summary>Edges of leaves facing a coarser neighbor (T-junction edges: the crack-resolution work of V0.2.0B).</summary>
        public int TransitionEdges;
        public string Problem;

        public bool Valid => Complete && NonOverlapping && Balanced;
    }

    /// <summary>
    /// Checks of a patch covering (V0.2.0A): complete (the leaves' areas add up to the six faces), non-overlapping
    /// (no leaf inside another), 2:1 balanced (edge neighbors at most one level apart); and the crack contract's
    /// measurements on edges between levels.
    /// </summary>
    public static class PatchCoverage
    {
        public static CoverageReport Validate(IReadOnlyCollection<PlanetPatchId> leaves)
        {
            var set = new HashSet<PlanetPatchId>(leaves);
            var report = new CoverageReport { Complete = true, NonOverlapping = true, Balanced = true };
            if (set.Count != leaves.Count)
            {
                report.NonOverlapping = false;
                report.Problem = "duplicate leaves";
            }
            int deepest = 0;
            foreach (PlanetPatchId id in set)
            {
                deepest = Math.Max(deepest, id.Level);
            }
            // Areas in units of the deepest level's patches: 4^(deepest - level) each; the planet is 6 * 4^deepest.
            ulong area = 0;
            foreach (PlanetPatchId id in set)
            {
                area += 1UL << (2 * (deepest - id.Level));
                for (PlanetPatchId p = id; p.HasParent;)
                {
                    p = p.Parent;
                    if (set.Contains(p))
                    {
                        report.NonOverlapping = false;
                        report.Problem = id + " overlaps its ancestor " + p;
                        break;
                    }
                }
            }
            if (area != 6UL << (2 * deepest))
            {
                report.Complete = false;
                report.Problem = report.Problem ?? "covered area " + area + " of " + (6UL << (2 * deepest));
            }
            foreach (PlanetPatchId id in set)
            {
                for (int d = 0; d < 4; d++)
                {
                    PlanetPatchId neighbor = PatchNeighbors.Neighbor(id, (PatchDirection)d);
                    for (PlanetPatchId p = neighbor; ; p = p.Parent)
                    {
                        if (set.Contains(p))
                        {
                            if (id.Level - p.Level > 1)
                            {
                                report.Balanced = false;
                                report.Problem = report.Problem ?? id + " is " + (id.Level - p.Level) + " levels finer than its neighbor " + p;
                            }
                            if (p.Level < id.Level)
                            {
                                report.TransitionEdges++;
                            }
                            break;
                        }
                        if (!p.HasParent)
                        {
                            break;
                        }
                    }
                }
            }
            return report;
        }

        /// <summary>
        /// Largest gap (rendering units) that a coarser neighbor's straight edge leaves at this patch's odd edge
        /// vertices (T-junctions): the crack size V0.2.0B must close. Uses the true surface points of both sides.
        /// </summary>
        public static float TJunctionGap(PlanetPatchId fine, PatchDirection direction, int resolution, TerrainElevationSampler sampler)
        {
            int lattice = resolution << fine.Level;
            int i0 = fine.X * resolution, j0 = fine.Y * resolution;
            float worst = 0f;
            for (int k = 1; k < resolution; k += 2)
            {
                PatchNeighbors.EdgeVertex(direction, k - 1, resolution, out int ia, out int ja);
                PatchNeighbors.EdgeVertex(direction, k, resolution, out int im, out int jm);
                PatchNeighbors.EdgeVertex(direction, k + 1, resolution, out int ib, out int jb);
                Vector3 a = sampler.SurfacePoint(CubeSphereCoordinates.LatticeDirection(fine.Face, i0 + ia, j0 + ja, lattice));
                Vector3 m = sampler.SurfacePoint(CubeSphereCoordinates.LatticeDirection(fine.Face, i0 + im, j0 + jm, lattice));
                Vector3 b = sampler.SurfacePoint(CubeSphereCoordinates.LatticeDirection(fine.Face, i0 + ib, j0 + jb, lattice));
                worst = Mathf.Max(worst, (m - 0.5f * (a + b)).magnitude);
            }
            return worst;
        }
    }
}
