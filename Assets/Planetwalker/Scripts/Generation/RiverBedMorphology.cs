using System;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>What a channel node knows about itself (V0.2.0G.2E): the inputs of the bed morphology.</summary>
    public struct ChannelState
    {
        /// <summary>Drained area in hydrology grid cells (~5 200 km² each at Earth scale on the 128 grid): the discharge proxy.</summary>
        public float Cells;
        /// <summary>Channel gradient on the filled surface (rise over run), smoothed along the network.</summary>
        public float Gradient;
        /// <summary>Mountain influence of the node in [0, 1].</summary>
        public float Influence;
        /// <summary>Regional erodibility (0.75 .. 1.25).</summary>
        public float Erodibility;
        /// <summary>
        /// The node is the shore of a filled depression of the hydrology (it drains into one without lying in it): its
        /// floodplain never lowers the ground under the level a lake would stand at there, i.e. by more than
        /// <see cref="HeadroomMeters"/>. Inside a depression the ground is already under that level (a dry basin floor,
        /// or a lake bed under its water): no limit.
        /// </summary>
        public bool Shore;
        /// <summary>Height of a shore node above the filled surface of the depression it drains into (meters).</summary>
        public float HeadroomMeters;
    }

    /// <summary>The cross-section parameters of a channel node (meters; V0.2.0G.2E), three geometric scales.</summary>
    public struct ChannelSection
    {
        /// <summary>Development in [0, 1]: 0 a minor drainage path (nothing carved), 1 a developed channel.</summary>
        public float Development;
        /// <summary>Regime weights in [0, 1]: rocky torrent / low-gradient alluvial channel.</summary>
        public float Rocky, Alluvial;
        /// <summary>Active channel: half width and thalweg depth.</summary>
        public float HalfWidth, Depth;
        /// <summary>Bank width per side (before the curvature asymmetry).</summary>
        public float BankWidth;
        /// <summary>Floodplain: half width (from the axis) and lowering; 0 outside the alluvial regime.</summary>
        public float FloodplainHalfWidth, FloodplainDepth;
        /// <summary>How much the curvature asymmetry applies (0 .. 1).</summary>
        public float Asymmetry;
    }

    /// <summary>
    /// River bed morphology (V0.2.0G.2E): a pure, deterministic function from a node's <see cref="ChannelState"/> to its
    /// <see cref="ChannelSection"/>, and the C1 composite cross-section (active channel, banks, floodplain, margin)
    /// evaluated at a signed distance from the axis. All constants are an initial calibration (candidates, not
    /// geological laws), grouped here so a future discharge (precipitation, climate, dynamic water) replaces the
    /// drained-area proxy without touching the structure.
    /// </summary>
    public static class RiverBedMorphology
    {
        // ---- Development (section 4 of the strategy): a_0 = 1 cell (topological channel, nothing carved) to a_1 cells
        // (developed channel), earlier where the terrain is erodible. A calibration candidate, judged on the result.
        public const float DevelopedCells = 8f;
        public const float MinDevelopedCells = 3f, MaxDevelopedCells = 16f;

        // ---- Nominal dimensions at development 1, intermediate regime, erodibility 1 (Earth scale, cells of ~5 200 km²).
        public const float WidthMeters = 25f, WidthExponent = 0.5f, MinHalfWidthMeters = 8f, MaxHalfWidthMeters = 1200f;
        public const float DepthMeters = 4f, DepthExponent = 0.35f, MinDepthMeters = 1f, MaxDepthMeters = 40f;
        public const float BankShare = 1.2f, MinBankMeters = 10f, MaxBankMeters = 1500f;
        public const float FloodplainShare = 6f, MaxFloodplainHalfWidthMeters = 8000f;
        public const float FloodplainDepthShare = 0.4f, MaxFloodplainDepthMeters = 15f;
        public const float MarginShare = 0.5f;

        // ---- Regime factors (rocky / alluvial), applied as (1 + rocky * r + alluvial * a).
        const float WidthRocky = -0.4f, WidthAlluvial = 0.4f;
        const float DepthRocky = 0.3f, DepthAlluvial = -0.3f;
        const float BankRocky = -0.5f, BankAlluvial = 1f;
        const float RockyStart = 0.004f, RockyEnd = 0.02f;
        const float AlluvialEnd = 0.0005f, AlluvialStart = 0.004f;
        const float AlluvialCellsStart = 3f, AlluvialCellsEnd = 12f;
        const float BaseAsymmetry = 0.3f;

        // ---- Cross-section shape.
        /// <summary>Channel edge depth as a share of the thalweg depth (before the transverse tilt).</summary>
        public const float EdgeShare = 0.65f;
        /// <summary>Transverse tilt: the outer edge deeper, the inner edge shallower, by this share at full asymmetry.</summary>
        public const float EdgeTilt = 0.25f;
        /// <summary>Thalweg shift toward the outer bank, as a share of the half width at full asymmetry.</summary>
        public const float ThalwegShift = 0.3f;
        /// <summary>Outer bank shortened / inner bank lengthened by these shares at full asymmetry.</summary>
        public const float OuterBankShare = 0.5f, InnerBankShare = 1f;
        /// <summary>
        /// Curvature normalization: the carved axes (78 km drainage chords smoothed into curves, wound by a 25 km meander)
        /// bend with radii of a few kilometers at the tightest, far above any channel width, so the asymmetry responds to
        /// the bend relative to the network's own tightest bends: kappa_n = kappa (1/m) x this radius, clamped to [-1, 1].
        /// </summary>
        public const float CurvatureRadiusMeters = 4000f;

        /// <summary>Normalized curvature in [-1, 1] (see <see cref="CurvatureRadiusMeters"/>).</summary>
        public static float NormalizedCurvature(float curvature) => Mathf.Clamp(curvature * CurvatureRadiusMeters, -1f, 1f);
        /// <summary>Exponent of the p-norm union of overlapping contributions (two equal depths: +9 %).</summary>
        public const float UnionPower = 8f;
        /// <summary>Thalweg depth never above this share of the bank width (wall slopes stay gentle).</summary>
        const float MaxDepthPerBank = 0.5f;
        /// <summary>Floodplain lowering never above this share of the thalweg depth (the channel edge stays below the plain).</summary>
        const float MaxFloodplainPerDepth = 0.44f;

        /// <summary>Largest total bed depth (meters, strength 1): the LOD bound reference.</summary>
        public static float MaxTotalDepthMeters => MaxDepthMeters * (1f + DepthRocky) * 1.12f * 1.0905f + MaxFloodplainDepthMeters;

        /// <summary>The section parameters of a node (meters), from its state. Pure.</summary>
        public static ChannelSection Evaluate(in ChannelState s)
        {
            var c = new ChannelSection();
            float a = Mathf.Max(s.Cells, 1f);
            float k = Mathf.Clamp(s.Erodibility, 0.5f, 2f);
            float valleyRegion = SmoothStep(0.05f, 0.3f, s.Influence);
            float alluvial = (1f - SmoothStep(AlluvialEnd, AlluvialStart, s.Gradient)) * SmoothStep(AlluvialCellsStart, AlluvialCellsEnd, a) * (1f - valleyRegion);
            float rocky = SmoothStep(RockyStart, RockyEnd, s.Gradient) * (1f - 0.5f * alluvial);
            float developed = Mathf.Clamp(DevelopedCells / (k * k), MinDevelopedCells, MaxDevelopedCells);
            float development = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Log(a, 2f) / Mathf.Log(developed, 2f)));
            float sk = Mathf.Sqrt(k);
            float halfWidth = Mathf.Clamp(WidthMeters * Mathf.Pow(a, WidthExponent), MinHalfWidthMeters, MaxHalfWidthMeters) * (1f + WidthRocky * rocky + WidthAlluvial * alluvial) * sk;
            float depth = Mathf.Clamp(DepthMeters * Mathf.Pow(a, DepthExponent), MinDepthMeters, MaxDepthMeters) * (1f + DepthRocky * rocky + DepthAlluvial * alluvial) * sk;
            float bank = Mathf.Clamp(BankShare * halfWidth * (1f + BankRocky * rocky + BankAlluvial * alluvial), MinBankMeters, MaxBankMeters);
            float floodplain = Mathf.Min(FloodplainShare * halfWidth, MaxFloodplainHalfWidthMeters) * alluvial;
            float lowering = Mathf.Min(FloodplainDepthShare * depth, MaxFloodplainDepthMeters) * alluvial;
            // Lake shores: a floodplain never lowers the ground under the level a lake would stand at (the shore cells of a
            // filled depression keep their headroom; a cell without headroom keeps its channel but no plain).
            if (s.Shore)
            {
                lowering = Mathf.Min(lowering, Mathf.Max(0f, s.HeadroomMeters));
                if (lowering <= 0f) floodplain = 0f;
            }
            float d2 = development * development, d3 = d2 * development;
            c.Development = development;
            c.Rocky = rocky;
            c.Alluvial = alluvial;
            c.HalfWidth = halfWidth * development;
            c.Depth = depth * d2;
            c.BankWidth = bank * development;
            c.FloodplainHalfWidth = floodplain * d3;
            c.FloodplainDepth = lowering * d3;
            c.Asymmetry = Mathf.Clamp01(BaseAsymmetry + alluvial);
            Bound(ref c);
            return c;
        }

        /// <summary>Re-applies the shape bounds (after the downstream regularization mixes nodes).</summary>
        public static void Bound(ref ChannelSection c)
        {
            c.Depth = Mathf.Min(c.Depth, MaxDepthPerBank * c.BankWidth);
            c.FloodplainDepth = Mathf.Min(c.FloodplainDepth, MaxFloodplainPerDepth * c.Depth);
        }

        /// <summary>
        /// The composite cross-section at a point (pure). <paramref name="sAxis"/>: signed distance from the axis (meters),
        /// positive toward the outer bank; <paramref name="asymmetry"/>: the curvature asymmetry in [0, 1] (0: symmetric).
        /// Returns the depth below the floodplain level the channel and banks dig (<paramref name="channelExcess"/>, meters,
        /// 0 beyond the bank tops), the floodplain lowering there (<paramref name="plainDepth"/>, meters: the plain level
        /// times the plain weight, which is 1 on the plain and fades through the margin) and the channel mask (1 at the
        /// thalweg, 0 at the bank tops). Height = -(plain depth + channel excess).
        /// C1 at the thalweg, at the channel edge (same edge slope on both sides, clamped for a monotone bank), at the bank
        /// top (zero slope) and C2 through the margin.
        /// </summary>
        public static void CrossSection(in ChannelSection c, float sAxis, float asymmetry, out float channelExcess, out float plainDepth, out float plainWeight, out float channelMask)
        {
            channelExcess = 0f;
            channelMask = 0f;
            float xa = Math.Abs(sAxis);
            float wv = c.FloodplainHalfWidth, fv = MarginShare * wv;
            float wc = c.HalfWidth, dt = c.Depth;
            // One floodplain level for the plain term and the channel excess (the clamp below keeps the channel edge under it).
            float dv = Mathf.Min(c.FloodplainDepth, MaxFloodplainPerDepth * dt);
            if (wv > 0f && xa < wv + fv)
            {
                plainWeight = xa <= wv ? 1f : 1f - SmootherStep((xa - wv) / fv);
            }
            else
            {
                plainWeight = 0f;
            }
            // A floodplain narrower than the channel and its banks is no plain (an interpolated section where one begins):
            // its lowering fades in continuously as it grows past them (fully there at twice their width).
            float zone = wc + c.BankWidth;
            float plainScale = zone > 0f ? SmoothStep(1f, 2f, wv / zone) : 1f;
            plainDepth = dv * plainWeight * plainScale;
            if (wc <= 0f || dt <= 0f)
            {
                return;
            }
            float asym = Mathf.Clamp01(asymmetry);
            float shift = ThalwegShift * wc * asym;
            float u = sAxis - shift; // from the thalweg, positive toward the outer bank
            bool outer = u >= 0f;
            float x = Math.Abs(u);
            float wSide = outer ? wc - shift : wc + shift;
            float e = EdgeShare * dt * (outer ? 1f + EdgeTilt * asym : 1f - EdgeTilt * asym);
            float bSide = c.BankWidth * (outer ? 1f - OuterBankShare * asym : 1f + InnerBankShare * asym);
            if (x >= wSide + bSide || wSide <= 0f || bSide <= 0f)
            {
                return;
            }
            // Edge slope shared by the channel shape and the bank (C1), clamped so the Hermite bank stays monotone
            // (Fritsch-Carlson with a zero end tangent: start tangent at most three chords).
            float rise = dt - e;
            float edgeSlope = Math.Min(2f * rise / wSide, 3f * (e - dv) / bSide);
            float h;
            if (x <= wSide)
            {
                float v = x / wSide;
                float lambda = rise > 0f ? Mathf.Clamp01(1f - edgeSlope * wSide / (2f * rise)) : 0f;
                float psi = (1f - lambda) * v * v + lambda * (3f * v * v - 2f * v * v * v);
                h = -dt + rise * psi;
            }
            else
            {
                float t = (x - wSide) / bSide;
                float t2 = t * t, t3 = t2 * t;
                float h0 = -e, h1 = -dv, m0 = edgeSlope * bSide;
                h = h0 * (2f * t3 - 3f * t2 + 1f) + m0 * (t3 - 2f * t2 + t) + h1 * (-2f * t3 + 3f * t2);
            }
            channelExcess = Math.Max(0f, -h - dv);
            float span = dt - dv;
            channelMask = span > 0f ? Mathf.Clamp01(channelExcess / span) : 0f;
        }

        /// <summary>
        /// Bed depth (meters, strength 1) a sampling at a spacing (meters) cannot resolve: the deepest channel still
        /// narrower than four samples (width and depth both grow with the drained area), bounded by the deepest bed.
        /// </summary>
        public static float UnresolvedMeters(double spacingMeters)
        {
            if (spacingMeters <= 0.0)
            {
                return 0f;
            }
            double width = 4.0 * spacingMeters;
            double depthOfWidth = DepthMeters * Math.Pow(width / WidthMeters, DepthExponent / WidthExponent);
            double factor = (1.0 + DepthRocky) * Math.Sqrt(1.25) * 1.0905 * (1.0 + FloodplainDepthShare);
            return (float)Math.Min(MaxTotalDepthMeters, depthOfWidth * factor);
        }

        /// <summary>p-norm union of two non-negative contributions: exact for one, at most 2^(1/p) for two equal, C1.</summary>
        public static float Union(float a, float b)
        {
            if (a <= 0f) return b;
            if (b <= 0f) return a;
            float m = Math.Max(a, b), n = Math.Min(a, b) / m;
            return m * Mathf.Pow(1f + Mathf.Pow(n, UnionPower), 1f / UnionPower);
        }

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        static float SmootherStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }
    }
}
