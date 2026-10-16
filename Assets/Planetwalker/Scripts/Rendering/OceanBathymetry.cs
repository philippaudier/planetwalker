using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Raw terrain elevation on a fixed spherical grid (V0.1.2B), for ocean shading only. Sampled once
    /// per cell center from the continuous elevation field (never from terrain mesh vertices), so it
    /// is independent of the terrain mesh and of cube-face UVs. Independent of sea level (elevations
    /// are absolute): a sea-level change reuses it. Cached per terrain.
    /// </summary>
    /// <remarks>
    /// Accuracy / cost: 128 cells per face edge (~0.7 degree, ~80 km on an Earth-sized planet), one
    /// elevation sample per cell (98,304 samples, about 0.25 s per terrain). Coastline detail finer than
    /// that comes from the terrain itself (the ocean sphere intersects the terrain mesh); the shading
    /// only needs the broad bathymetry (deep basins, slopes, shelves).
    /// </remarks>
    public sealed class BathymetryField
    {
        public const int Resolution = 128;

        public BathymetryField(HydrologyGrid grid, Func<Vector3, float> elevation)
        {
            var watch = Stopwatch.StartNew();
            Grid = grid;
            Elevation = new float[grid.CellCount];
            for (int c = 0; c < grid.CellCount; c++)
            {
                Elevation[c] = elevation(grid.GetCenter(c));
            }
            Milliseconds = (float)watch.Elapsed.TotalMilliseconds;
        }

        public HydrologyGrid Grid { get; }
        public readonly float[] Elevation;
        public float Milliseconds { get; }

        /// <summary>
        /// Smooth sample of a cell field at a direction (cell and 8 neighbours, weights (1 - d^2)^2 over
        /// 1.4 local cell spacings: continuous everywhere, also across cube edges).
        /// </summary>
        public float Sample(float[] field, Vector3 direction)
        {
            int cell = Grid.CellFromDirection(direction);
            float radius = 1.4f * Grid.LocalSpacing(direction);
            float sum = 0f, weights = 0f;
            for (int k = Grid.GetNeighborStart(cell) - 1; k < Grid.GetNeighborEnd(cell); k++)
            {
                int other = k < Grid.GetNeighborStart(cell) ? cell : Grid.GetNeighbor(k);
                float d = (Grid.GetCenter(other) - direction).magnitude / radius;
                float w = Mathf.Max(0f, 1f - d * d);
                w *= w;
                sum += field[other] * w;
                weights += w;
            }
            return weights > 0f ? sum / weights : field[cell];
        }
    }

    /// <summary>
    /// Ocean depths for one sea level (V0.1.2B): local depth and broad "shelf" depth per cell, and the
    /// normalized per-vertex values written to the ocean sphere.
    /// </summary>
    /// <remarks>
    /// Depth normalization (dimensionless, documented artistic choice): depth below sea level divided by
    /// the planet's own basin depth scale, <see cref="PlanetDefinition.continentStrength"/> (the
    /// "depth of ocean basins" setting, a fraction of the radius). Terrain units are exaggerated and are
    /// not kilometres; this keeps deep basins near 1 while a raised sea level reads deeper and a lowered
    /// one shallower, so planets do not all look the same.
    ///
    /// Shelf depth: mean depth of the ocean cells around each cell (two passes over the 8-neighbourhood,
    /// about two cells, ~1.5 degrees). A broad shallow shelf keeps a small value; a thin sliver of
    /// shallow water along a steep coast averages in the deep water beside it, so it is not tinted:
    /// no bright outline along every coastline, and no shallow area that the bathymetry does not support.
    /// </remarks>
    public sealed class OceanDepthLayer
    {
        public OceanDepthLayer(BathymetryField field, float seaLevel, float basinDepth)
        {
            var watch = Stopwatch.StartNew();
            Field = field;
            SeaLevel = seaLevel;
            BasinDepth = Mathf.Max(basinDepth, 1e-4f);
            HydrologyGrid grid = field.Grid;
            int count = grid.CellCount;
            Depth = new float[count];
            Shelf = new float[count];
            var pass = new float[count];
            var hasOcean = new bool[count];
            double oceanArea = 0, depthSum = 0;
            for (int c = 0; c < count; c++)
            {
                Depth[c] = Mathf.Max(0f, seaLevel - field.Elevation[c]);
                if (field.Elevation[c] < seaLevel)
                {
                    oceanArea += grid.GetArea(c);
                    depthSum += Depth[c] * grid.GetArea(c);
                }
            }
            OceanFraction = (float)(oceanArea / grid.TotalArea);
            MeanDepth = oceanArea > 0 ? (float)(depthSum / oceanArea) : 0f;
            // Pass 1: mean depth of the ocean cells in the neighbourhood.
            for (int c = 0; c < count; c++)
            {
                float sum = 0f;
                int n = 0;
                for (int k = grid.GetNeighborStart(c) - 1; k < grid.GetNeighborEnd(c); k++)
                {
                    int other = k < grid.GetNeighborStart(c) ? c : grid.GetNeighbor(k);
                    if (field.Elevation[other] < seaLevel)
                    {
                        sum += Depth[other];
                        n++;
                    }
                }
                hasOcean[c] = n > 0;
                pass[c] = n > 0 ? sum / n : 0f;
            }
            // Pass 2: same over the pass-1 values (ocean-adjacent cells only).
            for (int c = 0; c < count; c++)
            {
                float sum = 0f;
                int n = 0;
                for (int k = grid.GetNeighborStart(c) - 1; k < grid.GetNeighborEnd(c); k++)
                {
                    int other = k < grid.GetNeighborStart(c) ? c : grid.GetNeighbor(k);
                    if (hasOcean[other])
                    {
                        sum += pass[other];
                        n++;
                    }
                }
                Shelf[c] = n > 0 ? sum / n : 0f;
            }
            Milliseconds = (float)watch.Elapsed.TotalMilliseconds;
        }

        public BathymetryField Field { get; }
        public float SeaLevel { get; }
        /// <summary>Normalization depth (fraction of the radius).</summary>
        public float BasinDepth { get; }
        public float OceanFraction { get; }
        /// <summary>Area-weighted mean ocean depth (fraction of the radius).</summary>
        public float MeanDepth { get; }
        public readonly float[] Depth;
        public readonly float[] Shelf;
        public float Milliseconds { get; }

        /// <summary>Normalized local depth at a direction: 0 at and above sea level, ~1 in deep basins.</summary>
        public float NormalizedDepth(Vector3 direction)
        {
            return Mathf.Max(0f, SeaLevel - Field.Sample(Field.Elevation, direction)) / BasinDepth;
        }

        /// <summary>Normalized broad (shelf) depth at a direction.</summary>
        public float NormalizedShelfDepth(Vector3 direction)
        {
            return Field.Sample(Shelf, direction) / BasinDepth;
        }

        /// <summary>Per-vertex (normalized depth, normalized shelf depth) for unit-sphere vertices.</summary>
        public void WriteVertexData(Vector3[] directions, List<Vector2> data)
        {
            data.Clear();
            foreach (Vector3 v in directions)
            {
                Vector3 d = v.normalized;
                data.Add(new Vector2(NormalizedDepth(d), NormalizedShelfDepth(d)));
            }
        }
    }

    /// <summary>
    /// Bathymetry cache: the elevation grid is rebuilt only when the elevation field changes (sea level
    /// excluded), the depth layer only when the field, sea level or basin scale change. Rendering
    /// settings never invalidate either.
    /// </summary>
    public sealed class OceanBathymetryCache
    {
        HydrologyGrid grid;
        PlanetDefinition fieldTerrain;
        BathymetryField field;
        OceanDepthLayer layer;

        public int FieldComputeCount { get; private set; }
        public int LayerComputeCount { get; private set; }
        public BathymetryField CurrentField => field;
        public OceanDepthLayer Current => layer;

        public OceanDepthLayer Get(PlanetInstance planet)
        {
            if (field == null || !SameElevationField(fieldTerrain, planet.Definition))
            {
                grid ??= new HydrologyGrid(BathymetryField.Resolution);
                field = new BathymetryField(grid, planet.Height.GetElevation);
                fieldTerrain = planet.Definition.Clone();
                FieldComputeCount++;
                layer = null;
            }
            float basin = planet.Definition.continentStrength;
            if (layer == null || layer.SeaLevel != planet.SeaLevel || layer.BasinDepth != Mathf.Max(basin, 1e-4f))
            {
                layer = new OceanDepthLayer(field, planet.SeaLevel, basin);
                LayerComputeCount++;
            }
            return layer;
        }

        /// <summary>Same elevation field: <see cref="PlanetDefinition.TerrainEquals"/> ignoring the sea level
        /// (elevations do not depend on it).</summary>
        static bool SameElevationField(PlanetDefinition a, PlanetDefinition b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            PlanetDefinition other = b.Clone();
            other.seaLevel = a.seaLevel;
            return a.TerrainEquals(other);
        }

        public void Clear()
        {
            field = null;
            fieldTerrain = null;
            layer = null;
        }
    }
}
