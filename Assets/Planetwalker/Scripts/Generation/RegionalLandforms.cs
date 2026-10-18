using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>The regional landform families of V0.2.0G.1.</summary>
    public enum Landform
    {
        MountainValley = 0,
        Plateau = 1,
        CoastalPlain = 2,
    }

    /// <summary>Regional landform evaluation at one direction (V0.2.0G.1).</summary>
    public struct LandformSample
    {
        /// <summary>Influence weights in [0, 1]: mountain valley region, plateau, coastal plain (continuous, not exclusive).</summary>
        public float Valley, Plateau, CoastalPlain;
        /// <summary>How deep inside a valley corridor the point is, in [0, 1] (1 = valley floor axis).</summary>
        public float Corridor;
        /// <summary>Height change of the planetary terrain (fraction of the radius).</summary>
        public float Displacement;
        /// <summary>How much the regional and local dissection is calmed, in [0, 1] (valley floors, plateau tops).</summary>
        public float Calm;
        /// <summary>Escarpment band of plateau margins, in [0, 1] (extra local dissection).</summary>
        public float Edge;

        public float Weight(Landform landform) => landform == Landform.MountainValley ? Valley : landform == Landform.Plateau ? Plateau : CoastalPlain;
    }

    /// <summary>
    /// Regional landform identity (V0.2.0G.1): mountain valleys, plateaus and coastal plains shaped from the planet's own
    /// geography, between the planetary terrain e0 and the surface detail bands.
    /// </summary>
    /// <remarks>
    /// Signals (all upstream of the terrain, no climate or biome read): height of e0 above the sea, continental field
    /// (coastline at 0, a proxy of the distance to the coast: about 2 km of e0 per 0.025 at default settings), mountain
    /// influence, range core envelope and ridge anisotropy, the V0.1 undulation layer of e0, and seeded regional fields.
    /// Influence weights are smooth functions of those signals: no hard region boundary.
    /// <list type="bullet">
    /// <item>Mountain valleys (V0.2.0G.1.1): the drainage network of the planetary terrain in the ranges
    /// (<see cref="ValleyNetwork"/>): open paths that converge downhill to trunk valleys, tributaries narrow (~5 km),
    /// trunks broad (~22 km, U-shaped), depth bounded by the width (walls under ~45 degrees), by 5 km and by 45% of the
    /// height above the sea; gently meandering, asymmetric, calm floors. (V0.2.0G.1 used the zero lines of a noise:
    /// closed rings and horseshoes.)</item>
    /// <item>Plateaus: inland (continental field above ~0.1), outside mountain ranges, where a seeded regional field
    /// exceeds a threshold: an uplift of 1.2-2.6 km with the e0 undulations flattened by 45% (at most 3 km), margins
    /// lobate (edge noise) and steep (~2 km) or gradual (~10 km) by region, never steeper on average than ~45 degrees,
    /// the escarpment band dissected, calm tops.</item>
    /// <item>Coastal plains: within ~0.05-0.15 of the coastline (continental field), outside mountains, on the coasts a
    /// seeded field selects: the land lowered by 90% of its height above the first 150 m (the shore keeps e0's shape) and
    /// by 3 km at most, a broad inland rise (fall line) where the weight fades, gentle swells of at most 60 m.</item>
    /// </list>
    /// Coastline safety: every change is proportional to the height above the sea or faded to zero within 2 km of it,
    /// and the total never lowers a point by more than 60% (plus the plain lowering, which keeps the first 150 m): no land
    /// point goes under the sea, no sea point is touched, the coastline of e0 is preserved exactly. Pure function of the
    /// direction (no cube face, patch, camera or LOD input). Scales: coastal plains are part of the terrain at every
    /// sample spacing (hundreds of kilometers wide: the legacy world, hydrology and climate see them, identically at any
    /// resolution); valleys, swells and plateaus (blocks bounded by scarps of a few kilometers) are band-limited like noise
    /// octaves, fading over two octaves below their width, absent from analysis grids of 30 km and coarser. Strength 0 is
    /// never constructed (the terrain is then bit-identical to V0.2.0F.3).
    /// </remarks>
    public sealed class RegionalLandforms
    {
        const int PlateauStream = 40;
        const int PlateauEdgeStream = 41;
        const int PlateauCharacterStream = 42;
        const int PlainStream = 43;
        const int SwellStream = 44;
        // Valleys: the drainage network (ValleyNetwork; streams 45-47 were V0.2.0G.1's noise corridors, retired).
        public const float MaxValleyDepthMeters = 5000f;
        const float ValleyHeightShare = 0.45f;
        public static double MinValleyHalfWidthMeters => ValleyNetwork.MinHalfWidthMeters;

        // Plateaus.
        const double PlateauCellMeters = 500000.0;
        const double PlateauEdgeCellMeters = 90000.0;
        const double PlateauFineEdgeCellMeters = 30000.0;
        const double PlateauCharacterCellMeters = 400000.0;
        const float PlateauThreshold = 0.27f;
        const float MinUpliftMeters = 1200f, MaxUpliftMeters = 2600f;
        const float PlateauFlattening = 0.45f;
        const float MaxFlattenMeters = 3000f;

        const float PlateauFieldGradientPerMeter = 1e-5f; // the plateau field's typical slope (~0.01 per km)
        // A plateau is a block bounded by scarps of a few kilometers: a sampling too coarse for its scarps cannot represent
        // it (a 78-156 km analysis grid would alias the blocks), so it fades like a feature of that width. Absent at
        // spacings of 30 km and coarser: the legacy world, hydrology, lakes and climate never see plateaus, whatever the
        // mesh or grid resolution (up to 256).
        const double PlateauScarpWidthMeters = 60000.0;

        // Coastal plains.
        const double PlainCellMeters = 900000.0;
        const double SwellCellMeters = 12000.0;
        const float MaxSwellMeters = 60f;
        const float ShoreKeepMeters = 150f; // the shore itself keeps the shape of e0 (beaches, cliffs, coastline)
        const float MaxPlainLoweringMeters = 3000f; // a plain lowers the land by a few kilometers at most (no 10 km fall line)
        const float PlainCompression = 0.9f;

        const float FadeMeters = 2000f;
        const float MaxLowering = 0.6f;

        readonly int plateauSeed, plateauEdgeSeed, plateauCharacterSeed, plainSeed, swellSeed;
        readonly double radiusMeters;
        readonly ValleyNetwork valleys;
        readonly float fPlateau, fPlateauEdge, fPlateauFineEdge, fPlateauCharacter, fPlain, fSwell;
        readonly float shape;
        readonly float maxFlatten;

        /// <param name="valleys">The ranges' drainage network (null: no valleys).</param>
        public RegionalLandforms(PlanetScale scale, int seed, float strength, float seaLevel, ValleyNetwork valleys)
        {
            this.valleys = valleys;
            Strength = strength;
            SeaLevel = seaLevel;
            radiusMeters = scale.RadiusMeters;
            shape = Mathf.Min(strength, 1f);
            plateauSeed = PlanetNoise.DeriveSeed(seed, PlateauStream);
            plateauEdgeSeed = PlanetNoise.DeriveSeed(seed, PlateauEdgeStream);
            plateauCharacterSeed = PlanetNoise.DeriveSeed(seed, PlateauCharacterStream);
            plainSeed = PlanetNoise.DeriveSeed(seed, PlainStream);
            swellSeed = PlanetNoise.DeriveSeed(seed, SwellStream);
            fPlateau = Frequency(PlateauCellMeters);
            fPlateauEdge = Frequency(PlateauEdgeCellMeters);
            fPlateauFineEdge = Frequency(PlateauFineEdgeCellMeters);
            fPlateauCharacter = Frequency(PlateauCharacterCellMeters);
            fPlain = Frequency(PlainCellMeters);
            fSwell = Frequency(SwellCellMeters);
            MaxValleyDepth = (float)(MaxValleyDepthMeters * strength / radiusMeters);
            maxFlatten = (float)(MaxFlattenMeters / radiusMeters);
        }

        /// <summary>User strength (0 = off, 1 = default, up to 2): depths and uplift scale with it, shapes saturate at 1.</summary>
        public float Strength { get; }

        public float SeaLevel { get; }

        /// <summary>Physical radius of the planet (meters): the landforms' sizes are physical.</summary>
        public double RadiusMeters => radiusMeters;

        /// <summary>Deepest valley (fraction of the radius): the displacement a too coarse sampling can miss.</summary>
        public float MaxValleyDepth { get; }

        /// <summary>The valley drainage network (V0.2.0G.1.1), or null.</summary>
        public ValleyNetwork Valleys => valleys;

        /// <summary>Noise frequency (per unit direction) whose lattice cell is the given length on the surface.</summary>
        float Frequency(double cellMeters) => (float)(radiusMeters / cellMeters);

        /// <summary>
        /// Valley depth a sampling at a spacing (radians) cannot resolve (fraction of the radius): 0 while the narrowest
        /// corridor spans 8 samples, the deepest valley once it spans 2 or fewer.
        /// </summary>
        public float UnresolvedValleyDepth(float spacing)
        {
            return MaxValleyDepth * (1f - BandWeight(MinValleyHalfWidthMeters * 2.0, spacing));
        }

        /// <summary>
        /// Band-limit weight of a feature of a given width at a sample spacing (radians; 0 = full): 1 when the feature
        /// spans 8 samples or more, 0 at 2 or fewer, linear in octaves between (a landform fades over two octaves, so two
        /// adjacent LOD levels differ by half of it at most).
        /// </summary>
        float BandWeight(double widthMeters, float spacing)
        {
            if (spacing <= 0f)
            {
                return 1f;
            }
            double samples = widthMeters / (spacing * radiusMeters);
            return samples <= 2.0 ? 0f : Mathf.Clamp01((float)((Math.Log(samples, 2.0) - 1.0) * 0.5));
        }

        /// <summary>
        /// Landforms at a direction. <paramref name="precise"/> drives the corridor and swell noise (double precision at
        /// any scale), <paramref name="direction"/> the regional fields. <paramref name="spacing"/> band-limits the valley
        /// corridors and swells (radians, 0 = full).
        /// </summary>
        public void Evaluate(Vector3d precise, Vector3 direction, in TerrainBaseSample context, float spacing, out LandformSample sample)
        {
            sample = default;
            float h = context.Elevation - SeaLevel;
            if (h <= 0f)
            {
                return; // the sea and below-sea ground are never touched
            }
            double hMeters = h * radiusMeters;
            float fade = SmoothStep(0f, FadeMeters, (float)hMeters);
            float c = context.Continental;
            float m = context.Influence;
            float core = context.Core;
            float displacement = 0f;

            // ---- Mountain valleys (V0.2.0G.1.1): the drainage network of the ranges, open paths converging downhill.
            float valleyRegion = valleys != null ? SmoothStep(0.05f, 0.3f, m) : 0f;
            if (valleyRegion > 0f)
            {
                float carve = valleys.Carve(precise, spacing, out float corridor) * Strength * valleyRegion;
                // Never more than a share of the height above the sea (no lake below sea level, no coastline change).
                carve = Mathf.Min(carve, (float)hMeters * ValleyHeightShare);
                displacement -= (float)(carve / radiusMeters);
                sample.Valley = valleyRegion;
                sample.Corridor = corridor * valleyRegion;
                sample.Calm = Mathf.Max(sample.Calm, sample.Corridor);
            }

            // ---- Plateaus.
            float plateauSuitability = SmoothStep(0.10f, 0.22f, c) * (1f - SmoothStep(0.08f, 0.35f, m));
            float plateauBand = BandWeight(PlateauScarpWidthMeters, spacing);
            if (plateauSuitability > 0f && plateauBand > 0f)
            {
                float field = PlanetNoise.Gradient(direction.x * fPlateau, direction.y * fPlateau, direction.z * fPlateau, plateauSeed)
                    + 0.5f * PlanetNoise.Gradient(direction.x * fPlateau * 2f, direction.y * fPlateau * 2f, direction.z * fPlateau * 2f, plateauSeed + 1)
                    + 0.22f * PlanetNoise.Gradient(direction.x * fPlateauEdge, direction.y * fPlateauEdge, direction.z * fPlateauEdge, plateauEdgeSeed)
                    + 0.08f * PlanetNoise.Gradient(precise.x * fPlateauFineEdge, precise.y * fPlateauFineEdge, precise.z * fPlateauFineEdge, plateauEdgeSeed + 1);
                float character = PlanetNoise.Gradient(direction.x * fPlateauCharacter, direction.y * fPlateauCharacter, direction.z * fPlateauCharacter, plateauCharacterSeed);
                float uplift = Mathf.Lerp(MinUpliftMeters, MaxUpliftMeters, SmoothStep(-0.4f, 0.4f, character)) * Strength;
                // Flatten the e0 undulations (bounded: a plateau evens its surface, it does not fill whole basins).
                float flatten = Mathf.Clamp(-PlateauFlattening * shape * context.Undulation, -maxFlatten, maxFlatten);
                float shift = (float)(uplift / radiusMeters) + flatten;
                float sharpness = Mathf.Lerp(0.008f, 0.04f, SmoothStep(-0.3f, 0.3f, character)); // steep scarps (~2 km) .. gradual margins (~10 km)
                // The margin is at least as wide as its height (mean slope under ~45 degrees).
                sharpness = Mathf.Max(sharpness, PlateauFieldGradientPerMeter * (float)(Math.Abs(shift) * radiusMeters));
                float raw = SmoothStep(PlateauThreshold - sharpness, PlateauThreshold + sharpness, field);
                float mask = raw * plateauSuitability;
                if (mask > 0f)
                {
                    displacement += mask * fade * plateauBand * shift;
                    sample.Plateau = mask;
                    sample.Calm = Mathf.Max(sample.Calm, raw * raw * plateauSuitability * 0.75f);
                    sample.Edge = 4f * raw * (1f - raw) * plateauSuitability;
                }
            }

            // Lowering bound before the coastal compression (which is proportional to the remaining height).
            displacement = Mathf.Max(displacement, -MaxLowering * h);

            // ---- Coastal plains.
            float proximity = 1f - SmoothStep(0.05f, 0.15f, c);
            float notMountain = 1f - SmoothStep(0.03f, 0.25f, m);
            if (proximity > 0f && notMountain > 0f)
            {
                float plain = PlanetNoise.Gradient(direction.x * fPlain, direction.y * fPlain, direction.z * fPlain, plainSeed)
                    + 0.3f * PlanetNoise.Gradient(direction.x * fPlain * 3f, direction.y * fPlain * 3f, direction.z * fPlain * 3f, plainSeed + 1);
                float weight = proximity * notMountain * SmoothStep(0f, 0.3f, plain);
                if (weight > 0f)
                {
                    float h1 = h + displacement;
                    // The first meters above the sea keep e0's shape: the plain begins just inland of the shore.
                    float keep = Mathf.Min(h1, (float)(ShoreKeepMeters / radiusMeters));
                    float lowering = Mathf.Min((h1 - keep) * PlainCompression * shape, (float)(MaxPlainLoweringMeters / radiusMeters)) * weight;
                    float h2 = h1 - lowering;
                    // Gentle swells (low terraces, broad interfluves): at most 60 m, and at most 25% of the remaining height.
                    float swell = 0.72f * PlanetNoise.Gradient(precise.x * fSwell, precise.y * fSwell, precise.z * fSwell, swellSeed)
                        * BandWeight(SwellCellMeters * 2.0, spacing)
                        + 0.28f * PlanetNoise.Gradient(precise.x * fSwell * 3f, precise.y * fSwell * 3f, precise.z * fSwell * 3f, swellSeed + 1)
                        * BandWeight(SwellCellMeters * 2.0 / 3.0, spacing);
                    float swellAmplitude = Mathf.Min(0.25f * (h2 - keep), (float)(MaxSwellMeters / radiusMeters));
                    h2 += swellAmplitude * swell * weight;
                    displacement = h2 - h;
                    sample.CoastalPlain = weight;
                }
            }

            sample.Displacement = displacement;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
