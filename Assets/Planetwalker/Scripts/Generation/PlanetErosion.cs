using System;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Erosion contribution at one direction.</summary>
    public struct ErosionSample
    {
        /// <summary>Height change of the pre-erosion terrain (fraction of the radius): never positive (incision only).</summary>
        public float Displacement;
        /// <summary>How much of a channel the point is on, in [0, 1] (1 on an incised axis), for diagnostics.</summary>
        public float Channel;
        /// <summary>River bed (V0.2.0G.2E): active channel weight (1 at a thalweg, 0 at the bank tops), band-limited.</summary>
        public float Bed;
        /// <summary>River bed (V0.2.0G.2E): floor weight (channel, banks and floodplain; 0 beyond the margin), band-limited: calms the octaves.</summary>
        public float Floor;
    }

    /// <summary>Which erosion model a planet uses (archived with it: an older world never changes model silently).</summary>
    public enum ErosionModel
    {
        /// <summary>V0.2.0G.2A: the stage with zero displacement.</summary>
        None = 0,
        /// <summary>V0.2.0G.2B: drainage-guided fluvial incision.</summary>
        Fluvial = 1,
    }

    /// <summary>
    /// Planet-level information the erosion stage reads (V0.2.0G.2A): immutable after construction, shared by every
    /// sampler, thread and patch. References the existing data (no copy): the planetary terrain e0, the regional
    /// landforms and their valley drainage network with its hydrology (drainage paths, receivers, drained areas, filled
    /// elevations), the physical scale and the seed.
    /// </summary>
    public sealed class ErosionContext
    {
        public ErosionContext(PlanetScale scale, int seed, float seaLevel, float strength, RegionalLandforms landforms, ErosionModel model = ErosionModel.None)
        {
            long start = Stopwatch.GetTimestamp();
            Scale = scale;
            Seed = seed;
            SeaLevel = seaLevel;
            Strength = strength;
            Model = model;
            Landforms = landforms;
            Valleys = landforms?.Valleys;
            Hydrology = Valleys?.Hydrology;
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        public PlanetScale Scale { get; }
        public int Seed { get; }
        public float SeaLevel { get; }

        /// <summary>User strength (0 = the stage is not constructed; up to 2): the incision depth scales linearly with it.</summary>
        public float Strength { get; }

        /// <summary>The erosion model (V0.2.0G.2B: fluvial incision; None: zero displacement, the V0.2.0G.2A stage).</summary>
        public ErosionModel Model { get; }

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

        // Fluvial incision bounds (V0.2.0G.2B): never below this share of the height above the sea (the valley carve
        // already takes up to 45 %: a floor keeps at least 20 % of its height), faded within the first 2 km above the sea
        // (the coastline of e0 is kept exactly), inside the mountain ranges only (the landforms' valley region), not on
        // plateaus.
        const float MaxHeightShare = 0.35f;
        const float FadeMeters = 2000f;

        // River bed (V0.2.0G.2E): in the deepest gorges the bed tapers out (the incision's own floor is the channel there,
        // and the incision bound stays what G.2B tested).
        const float BedTaperStart = 0.6f, BedTaperEnd = 1f;

        /// <summary>True when the stage carves a river bed along the whole network (V0.2.0G.2E).</summary>
        public bool HasRiverBeds => Context.Valleys != null && Context.Valleys.WholeNetwork;

        /// <summary>Displacement (fraction of the radius) a sample spacing cannot resolve: the incision and bed narrower than it.</summary>
        public float UnresolvedAmplitude(float spacing)
        {
            if (Context.Model == ErosionModel.None || Context.Valleys == null)
            {
                return 0f;
            }
            float strength = Math.Min(Context.Strength, 2f);
            return (float)((Context.Valleys.UnresolvedIncisionMeters(spacing) + Context.Valleys.UnresolvedBedMeters(spacing)) * strength / Context.Scale.RadiusMeters);
        }

        /// <summary>
        /// The erosion at a direction. <paramref name="precise"/> is the planet-space direction (double), <paramref name="direction"/>
        /// its float rounding, <paramref name="context"/> the planetary terrain at that direction, <paramref name="landforms"/>
        /// the regional landforms there, <paramref name="spacing"/> the sample spacing (radians, 0 = full).
        /// </summary>
        public void Evaluate(Vector3d precise, Vector3 direction, in TerrainBaseSample context, in LandformSample landforms, float spacing, out ErosionSample sample)
        {
            sample = default;
            ErosionContext c = Context;
            if (c.Model == ErosionModel.None || c.Valleys == null)
            {
                return; // V0.2.0G.2A stage: zero by contract
            }
            // The valley region of the landforms (0 outside the ranges), not on plateau blocks (exactly 0 from 80 % plateau).
            float region = landforms.Valley * (1f - SmoothStep(0.3f, 0.8f, landforms.Plateau));
            bool beds = c.Valleys.WholeNetwork;
            if (region <= 0f && !beds)
            {
                return;
            }
            double radius = c.Scale.RadiusMeters;
            double heightMeters = (context.Elevation - c.SeaLevel + landforms.Displacement) * radius;
            if (heightMeters <= 0.0)
            {
                return;
            }
            // Fluvial incision (V0.2.0G.2B), inside the ranges only.
            float incision = 0f, channel = 0f, raw = 0f;
            if (region > 0f)
            {
                raw = c.Valleys.Incision(precise, spacing, out channel);
                if (raw > 0f)
                {
                    float fade = SmoothStep(0f, FadeMeters, (float)heightMeters);
                    incision = raw * (c.Strength * region * fade); // the G.2B operation order: bit-identical terrain
                }
            }
            // River bed (V0.2.0G.2E), everywhere on land: the floodplain yields to the mountain valleys (which carve the
            // valley floor there), the whole bed tapers out in the deepest gorges, nothing fades with the height: the
            // height bound below extinguishes it at the coast, continuously (the coastline of e0 is kept exactly).
            float bed = 0f, bedChannel = 0f, floor = 0f;
            if (beds)
            {
                bed = c.Valleys.Bed(precise, spacing, 1f - landforms.Valley, out bedChannel, out floor);
                if (bed > 0f)
                {
                    float taper = 1f - SmoothStep(BedTaperStart, BedTaperEnd, raw * region / ValleyNetwork.MaxIncisionMeters);
                    bed *= c.Strength * taper;
                    bedChannel *= taper;
                    floor *= taper;
                }
            }
            float total = incision + bed;
            if (total <= 0f)
            {
                return;
            }
            total = Mathf.Min(total, (float)heightMeters * MaxHeightShare);
            sample.Displacement = (float)(-total / radius);
            sample.Channel = channel * region;
            sample.Bed = bedChannel;
            sample.Floor = floor;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
