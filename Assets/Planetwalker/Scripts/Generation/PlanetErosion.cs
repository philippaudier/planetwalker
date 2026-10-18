using System;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Erosion contribution at one direction (V0.2.0G.2A: always zero).</summary>
    public struct ErosionSample
    {
        /// <summary>Height change of the pre-erosion terrain (fraction of the radius). Zero throughout V0.2.0G.2A.</summary>
        public float Displacement;
    }

    /// <summary>
    /// Planet-level information the erosion stage reads (V0.2.0G.2A): immutable after construction, shared by every
    /// sampler, thread and patch. References the existing data (no copy): the planetary terrain e0, the regional
    /// landforms and their valley drainage network with its hydrology (drainage paths, receivers, drained areas, filled
    /// elevations), the physical scale and the seed.
    /// </summary>
    public sealed class ErosionContext
    {
        public ErosionContext(PlanetScale scale, int seed, float seaLevel, float strength, RegionalLandforms landforms)
        {
            long start = Stopwatch.GetTimestamp();
            Scale = scale;
            Seed = seed;
            SeaLevel = seaLevel;
            Strength = strength;
            Landforms = landforms;
            Valleys = landforms?.Valleys;
            Hydrology = Valleys?.Hydrology;
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        public PlanetScale Scale { get; }
        public int Seed { get; }
        public float SeaLevel { get; }

        /// <summary>User strength (0 = the stage is not constructed; up to 2). Scales nothing yet: the displacement is zero.</summary>
        public float Strength { get; }

        /// <summary>The regional landforms the erosion acts on (may be null: no landforms).</summary>
        public RegionalLandforms Landforms { get; }

        /// <summary>The ranges' drainage network (V0.2.0G.1.1), the fluvial structure of a future incision (may be null).</summary>
        public ValleyNetwork Valleys { get; }

        /// <summary>The drainage analysis of e0 behind the network: receivers, drained areas, filled surface (may be null).</summary>
        public HydrologyResult Hydrology { get; }

        /// <summary>Wall time of this context's construction (references only: microseconds).</summary>
        public double BuildMilliseconds { get; }
    }

    /// <summary>
    /// Erosion stage of the canonical terrain (V0.2.0G.2A): <c>e = e0 + landforms + erosion + detail bands</c>, evaluated in
    /// <see cref="TerrainDetail.Evaluate(Vector3d, Vector3, in TerrainBaseSample, float, float, out TerrainDetailSample)"/>
    /// right after the regional landforms and before the fine surface detail, once per sample.
    /// </summary>
    /// <remarks>
    /// A pure function of the planet-space direction and the samples already computed upstream (e0 context, landform
    /// sample) and of the immutable <see cref="ErosionContext"/>: no camera, patch, LOD, floating-origin, frame or thread
    /// input, no mutable state, no allocation. The band-limit spacing is passed for the future (an erosion feature narrower
    /// than a sampling fades like the octaves and the valleys do). In V0.2.0G.2A the displacement is exactly zero: the
    /// foundation (data flow, diagnostics, archives, tests) without any visible erosion. The future scales:
    /// regional (basins, trunk valleys: the hydrology grid, ~78 km), local (valley profiles, tributaries, fans: the valley
    /// segments, 5-22 km) and micro (channels, weathering: analytic noise, no data), each sampled from the data of its scale.
    /// </remarks>
    public sealed class ErosionSampler
    {
        public ErosionSampler(ErosionContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public ErosionContext Context { get; }

        /// <summary>
        /// The erosion at a direction. <paramref name="precise"/> is the planet-space direction (double), <paramref name="direction"/>
        /// its float rounding, <paramref name="context"/> the planetary terrain at that direction, <paramref name="landforms"/>
        /// the regional landforms there, <paramref name="spacing"/> the sample spacing (radians, 0 = full).
        /// </summary>
        public void Evaluate(Vector3d precise, Vector3 direction, in TerrainBaseSample context, in LandformSample landforms, float spacing, out ErosionSample sample)
        {
            // V0.2.0G.2A: the foundation only. Every input is in place; the displacement is zero by contract.
            sample = default;
        }
    }
}
