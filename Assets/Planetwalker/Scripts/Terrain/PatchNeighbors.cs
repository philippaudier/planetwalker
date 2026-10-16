using System;
using System.Collections.Generic;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>Relation between a leaf patch and what lies across one of its edges (in a balanced covering).</summary>
    public enum EdgeTransition
    {
        /// <summary>One neighbor at the same level: identical edge vertices.</summary>
        Equal,
        /// <summary>One neighbor one level coarser: this patch's odd edge vertices are T-junctions (crack risk).</summary>
        Coarser,
        /// <summary>Two neighbors one level finer: they hold the T-junctions on their side.</summary>
        Finer,
    }

    /// <summary>
    /// Edge adjacency of terrain patches (V0.2.0A), across cube faces and levels. Equal-level neighbors are computed
    /// exactly with integer arithmetic: the neighbor's center is stepped across the edge on an integer cube and
    /// folded onto the adjacent face, whose own axes then give its address. No orientation table is written by
    /// hand: the existing face frames (<see cref="CubeSphereGenerator.GetFaceAxes"/>) are the only source, so
    /// rotations and mirrorings between faces are handled by construction. Corner contact is not adjacency: only
    /// the four edge neighbors are returned.
    /// </summary>
    public static class PatchNeighbors
    {
        static readonly Vector3Int[] Normals = new Vector3Int[CubeSphereGenerator.FaceCount];
        static readonly Vector3Int[] AxesA = new Vector3Int[CubeSphereGenerator.FaceCount];
        static readonly Vector3Int[] AxesB = new Vector3Int[CubeSphereGenerator.FaceCount];

        static PatchNeighbors()
        {
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
                Normals[face] = Vector3Int.RoundToInt(normal);
                AxesA[face] = Vector3Int.RoundToInt(axisA);
                AxesB[face] = Vector3Int.RoundToInt(axisB);
            }
        }

        static int Dot(Vector3Int a, Vector3Int b) => a.x * b.x + a.y * b.y + a.z * b.z;

        /// <summary>The equal-level patch across one edge (always exists: the cube surface has no border).</summary>
        public static PlanetPatchId Neighbor(PlanetPatchId id, PatchDirection direction)
        {
            int face = id.Face;
            int size = id.GridSize;
            // Patch center in half-cell units: odd values in (-size, size); the face plane is at +size.
            int ia = 2 * id.X + 1 - size;
            int ib = 2 * id.Y + 1 - size;
            switch (direction)
            {
                case PatchDirection.East: ia += 2; break;
                case PatchDirection.West: ia -= 2; break;
                case PatchDirection.North: ib += 2; break;
                default: ib -= 2; break;
            }
            if (ia > -size && ia < size && ib > -size && ib < size)
            {
                return new PlanetPatchId(face, id.Level, (ia + size - 1) >> 1, (ib + size - 1) >> 1);
            }

            // Stepped one half cell past the edge: fold that overshoot onto the adjacent face.
            Vector3Int n = Normals[face];
            Vector3Int edgeAxis;
            int overshoot;
            if (ia >= size || ia <= -size)
            {
                edgeAxis = AxesA[face] * Math.Sign(ia);
                overshoot = Math.Abs(ia) - size;
                ia = Math.Sign(ia) * size;
            }
            else
            {
                edgeAxis = AxesB[face] * Math.Sign(ib);
                overshoot = Math.Abs(ib) - size;
                ib = Math.Sign(ib) * size;
            }
            // On the integer cube of half-size "size": the point moves down the adjacent face by the overshoot.
            Vector3Int point = n * (size - overshoot) + AxesA[face] * ia + AxesB[face] * ib;
            if (Dot(point, edgeAxis) != size)
            {
                throw new InvalidOperationException("Neighbor folding failed for " + id + " " + direction);
            }
            int target = FaceOf(edgeAxis);
            int ta = Dot(point, AxesA[target]);
            int tb = Dot(point, AxesB[target]);
            return new PlanetPatchId(target, id.Level, (ta + size - 1) >> 1, (tb + size - 1) >> 1);
        }

        static int FaceOf(Vector3Int normal)
        {
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                if (Normals[face] == normal)
                {
                    return face;
                }
            }
            throw new InvalidOperationException("No face with normal " + normal);
        }

        /// <summary>The direction, seen from <paramref name="neighbor"/>, that leads back to <paramref name="id"/> (equal levels).</summary>
        public static PatchDirection DirectionBack(PlanetPatchId id, PlanetPatchId neighbor)
        {
            for (int d = 0; d < 4; d++)
            {
                if (Neighbor(neighbor, (PatchDirection)d) == id)
                {
                    return (PatchDirection)d;
                }
            }
            throw new ArgumentException(neighbor + " is not an edge neighbor of " + id);
        }

        /// <summary>
        /// True when a patch finer than (or equal to) <paramref name="region"/> touches it along an edge, i.e. one of
        /// its equal-level edge neighbors lies inside <paramref name="region"/>.
        /// </summary>
        public static bool TouchesAlongEdge(PlanetPatchId patch, PlanetPatchId region)
        {
            for (int d = 0; d < 4; d++)
            {
                if (region.Contains(Neighbor(patch, (PatchDirection)d)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Leaves of a covering that share the given edge of a leaf: one equal or coarser leaf, or the finer leaves
        /// along that edge (any depth), in a deterministic order. <paramref name="isLeaf"/> describes the covering.
        /// </summary>
        public static void NeighborLeaves(PlanetPatchId id, PatchDirection direction, Func<PlanetPatchId, bool> isLeaf, List<PlanetPatchId> output)
        {
            PlanetPatchId neighbor = Neighbor(id, direction);
            for (PlanetPatchId p = neighbor; ; p = p.Parent)
            {
                if (isLeaf(p))
                {
                    output.Add(p);
                    return;
                }
                if (!p.HasParent)
                {
                    break;
                }
            }
            CollectFinerAlongEdge(neighbor, id, isLeaf, output);
        }

        static void CollectFinerAlongEdge(PlanetPatchId region, PlanetPatchId target, Func<PlanetPatchId, bool> isLeaf, List<PlanetPatchId> output)
        {
            if (!region.CanSubdivide)
            {
                return;
            }
            for (int c = 0; c < 4; c++)
            {
                PlanetPatchId child = region.Child(c);
                if (!TouchesAlongEdge(child, target))
                {
                    continue;
                }
                if (isLeaf(child))
                {
                    output.Add(child);
                }
                else
                {
                    CollectFinerAlongEdge(child, target, isLeaf, output);
                }
            }
        }

        /// <summary>Lattice indices (patch-local, 0..resolution) of the vertices on one edge of a patch, in increasing order.</summary>
        public static void EdgeVertex(PatchDirection direction, int k, int resolution, out int i, out int j)
        {
            switch (direction)
            {
                case PatchDirection.East: i = resolution; j = k; break;
                case PatchDirection.West: i = 0; j = k; break;
                case PatchDirection.North: i = k; j = resolution; break;
                default: i = k; j = 0; break;
            }
        }
    }
}
