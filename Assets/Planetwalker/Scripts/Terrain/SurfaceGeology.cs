using System;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>Surface geology debug views (V0.2.0E; the shader's _GeoParams.y).</summary>
    public enum GeologyDebugView
    {
        None = 0,
        /// <summary>Material identity: province rock color where bedrock shows, sand-yellow sediment, green soil, strata in blue.</summary>
        Identity = 1,
        Bedrock = 2,
        Sediment = 3,
        Erosion = 4,
        ReferenceSlope = 5,
    }

    /// <summary>Surface geology at a point (V0.2.0E). Weights in [0, 1]; colors sRGB.</summary>
    public struct GeologySample
    {
        /// <summary>Exposed bedrock: steep, convex, high ground, thin cover.</summary>
        public float Bedrock;
        /// <summary>Loose material (debris, alluvium, beach): flat and concave ground, slope feet; never on bedrock.</summary>
        public float Sediment;
        /// <summary>Erosion character: gullies and streaks down the slopes (steep, arid, rugged ground).</summary>
        public float Erosion;
        /// <summary>Stratification tendency: layered (sedimentary) rock where it shows.</summary>
        public float Strata;
        /// <summary>Reference slope (m / m) of the terrain at the geology lattice scale.</summary>
        public float Slope;
        /// <summary>Concavity index: rise of the surroundings at the reference scale (+ hollows / valley floors, - ridges).</summary>
        public float Concavity;
        /// <summary>Unit downslope direction (planet space, tangent); zero on flat ground.</summary>
        public Vector3 Downslope;
        /// <summary>Rock color of the local geological province (sRGB).</summary>
        public Color RockTint;
        /// <summary>Climatic aridity (0 wet .. 1 arid), 0.5 without a climate.</summary>
        public float Aridity;
        /// <summary>Rock family: 0 crystalline (granite, basalt) .. 1 sedimentary (sandstone, limestone).</summary>
        public float Sedimentary;
        /// <summary>Family field (-1 .. 1): crystalline granite (-) / basalt (+); sedimentary sandstone (-) / limestone (+).</summary>
        public float Family;
    }

    /// <summary>
    /// Surface Geological Identity (V0.2.0E): a few continuous geological characteristics per surface point, derived
    /// from what the planet already decides (canonical elevation, mountain envelope, terrain character, climate) and
    /// from seeded province fields. Presentation data only: the elevation, the coastlines, the archives are untouched.
    /// </summary>
    /// <remarks>
    /// <para><b>Lattice.</b> The characteristics are computed at the nodes of a global cube-face lattice (per face,
    /// <see cref="ReferenceLatticeSize"/> = 2^16 cells, about 150 m at Earth scale, the planetary terrain's fine lattice)
    /// from a 5-point stencil of the elevation band-limited to the node spacing (slope, downslope direction, concavity),
    /// and interpolated bilinearly in between. Every patch level finer than the lattice reads the very same nodes: the
    /// geology at a planetary location is identical at every such level (no rock moving when the LOD changes).
    /// Coarser levels read their own vertex lattice (same rule, prefiltered: the geology of the relief they show, no
    /// aliasing). A node is a pure function of (planet, lattice, face, i, j): no camera, LOD order, thread or floating
    /// origin enters it. Nodes are cached per thread (direct-mapped).</para>
    /// <para><b>Fields.</b> Bedrock exposure grows with the reference slope and convexity (ridges, scarps), in mountain
    /// cores and arid land, and shrinks in hollows and on the coast. Sediment fills flat and concave ground and slope
    /// feet, never where bedrock is. Erosion character (gullies, streaks) follows slope, aridity and the terrain's
    /// rugged character. Stratification belongs to sedimentary provinces (seeded planet-scale field, favored by
    /// aridity, disfavored in crystalline mountain cores). Rock color: crystalline (grey / pink granite, basalt) and
    /// sedimentary (red sandstone, ochre limestone) families mixed by province fields, never one color per biome.</para>
    /// </remarks>
    public sealed class SurfaceGeology
    {
        /// <summary>Cells per face edge of the reference geology lattice (the planetary terrain's fine lattice).</summary>
        public const int ReferenceLatticeSize = PlanetHeightGenerator.FineLatticeSize;

        const int ProvinceStream = 9701;
        const int FamilyStream = 9702;
        const int LocalStream = 9703;

        static readonly Color Granite = new Color(0.52f, 0.51f, 0.49f);
        static readonly Color PinkGranite = new Color(0.6f, 0.5f, 0.45f);
        static readonly Color Basalt = new Color(0.3f, 0.3f, 0.31f);
        static readonly Color RedSandstone = new Color(0.62f, 0.38f, 0.26f);
        static readonly Color OchreLimestone = new Color(0.73f, 0.64f, 0.49f);

        /// <summary>
        /// Cells per face edge of the regional lattice (about 20 km at Earth scale): climate, terrain character and
        /// province fields vary over hundreds of kilometers, so they are evaluated there once and interpolated.
        /// </summary>
        public const int RegionalLatticeSize = 512;

        readonly PlanetHeightGenerator height;
        readonly TerrainDetail detail;
        readonly ClimateResult climate;
        readonly NaturalSurface natural;
        readonly float seaLevel;
        readonly int provinceSeed, familySeed, localSeed, coastSeed;
        readonly float radiusMeters;
        readonly float referenceSpacing;

        public SurfaceGeology(PlanetInstance planet, NaturalSurface natural)
        {
            Planet = planet ?? throw new ArgumentNullException(nameof(planet));
            height = planet.Height;
            detail = height.SurfaceDetail;
            this.natural = natural;
            climate = natural?.BiomeData?.Climate;
            seaLevel = planet.SeaLevel;
            provinceSeed = PlanetNoise.DeriveSeed(planet.Definition.seed, ProvinceStream);
            familySeed = PlanetNoise.DeriveSeed(planet.Definition.seed, FamilyStream);
            localSeed = PlanetNoise.DeriveSeed(planet.Definition.seed, LocalStream);
            coastSeed = PlanetNoise.DeriveSeed(planet.Definition.seed, LocalStream + 1);
            radiusMeters = (float)planet.Scale.RadiusMeters;
            referenceSpacing = Mathf.PI * 0.5f / ReferenceLatticeSize;
        }

        public PlanetInstance Planet { get; }

        /// <summary>True when the climate (aridity) is known (Natural Planet colors).</summary>
        public bool HasClimate => climate != null;

        /// <summary>Geology lattice for a vertex lattice: the vertex lattice itself while coarser than the reference.</summary>
        public static int LatticeFor(int vertexLattice) => Math.Min(vertexLattice, ReferenceLatticeSize);

        /// <summary>Width (cells) of the band along face edges where both faces' lattices are blended.</summary>
        public const double EdgeBlendCells = 3.0;

        /// <summary>
        /// Geology at lattice coordinates (x, y in [0, lattice] along the face's axes) of a face. Away from the face's
        /// edges: <see cref="SampleFace"/>. Within <see cref="EdgeBlendCells"/> of an edge, blended with the same point
        /// sampled on the neighboring face's lattice, half and half on the edge itself: the two faces' lattices are
        /// skewed against each other (strongly near cube corners) and each alone would leave a small seam.
        /// </summary>
        public void Sample(int face, double x, double y, int lattice, out GeologySample g)
        {
            SampleFace(face, x, y, lattice, out g);
            double west = x, east = lattice - x, south = y, north = lattice - y;
            double edge = Math.Min(Math.Min(west, east), Math.Min(south, north));
            if (edge >= EdgeBlendCells)
            {
                return;
            }
            // The face across the nearest edge: the one whose normal is that edge's outward axis.
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            Vector3 outward = edge == west ? -axisA : edge == east ? axisA : edge == south ? -axisB : axisB;
            int other = CubeSphereGenerator.DominantFace(new Vector3d(outward));
            Vector3d direction = FaceDirection(face, x * 2.0 / lattice - 1.0, y * 2.0 / lattice - 1.0);
            CubeSphereGenerator.FaceCoordinates(direction, other, 1.5, out double oa, out double ob);
            SampleFace(other, (oa + 1.0) * 0.5 * lattice, (ob + 1.0) * 0.5 * lattice, lattice, out GeologySample h);
            float w = (float)(0.5 * (1.0 - edge / EdgeBlendCells));
            g = Blend(g, h, w);
        }

        /// <summary>Direction of face coordinates (a, b), any real values (the lattice formula, continued).</summary>
        static Vector3d FaceDirection(int face, double a, double b)
        {
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            var p = new Vector3d(normal.x + axisA.x * a + axisB.x * b, normal.y + axisA.y * a + axisB.y * b, normal.z + axisA.z * a + axisB.z * b);
            double x2 = p.x * p.x, y2 = p.y * p.y, z2 = p.z * p.z;
            return new Vector3d(
                p.x * Math.Sqrt(Math.Max(0.0, 1.0 - y2 * 0.5 - z2 * 0.5 + y2 * z2 / 3.0)),
                p.y * Math.Sqrt(Math.Max(0.0, 1.0 - x2 * 0.5 - z2 * 0.5 + x2 * z2 / 3.0)),
                p.z * Math.Sqrt(Math.Max(0.0, 1.0 - x2 * 0.5 - y2 * 0.5 + x2 * y2 / 3.0))).Normalized;
        }

        static GeologySample Blend(in GeologySample a, in GeologySample b, float w)
        {
            float v = 1f - w;
            Vector3 flow = a.Downslope * v + b.Downslope * w;
            return new GeologySample
            {
                Bedrock = a.Bedrock * v + b.Bedrock * w,
                Sediment = a.Sediment * v + b.Sediment * w,
                Erosion = a.Erosion * v + b.Erosion * w,
                Strata = a.Strata * v + b.Strata * w,
                Slope = a.Slope * v + b.Slope * w,
                Concavity = a.Concavity * v + b.Concavity * w,
                Downslope = flow.sqrMagnitude > 1e-12f ? flow.normalized : Vector3.zero,
                RockTint = a.RockTint * v + b.RockTint * w,
                Aridity = a.Aridity * v + b.Aridity * w,
                Sedimentary = a.Sedimentary * v + b.Sedimentary * w,
                Family = a.Family * v + b.Family * w,
            };
        }

        /// <summary>
        /// Geology on one face's lattice: bilinear interpolation of the four surrounding nodes (the node itself at
        /// integer coordinates; coordinates past the edges continue the face's parametrization).
        /// </summary>
        public void SampleFace(int face, double x, double y, int lattice, out GeologySample g)
        {
            int i = (int)Math.Floor(x + 1e-9), j = (int)Math.Floor(y + 1e-9);
            double tx = x - i, ty = y - j;
            GeologyCache cache = GeologyCache.Current;
            if (tx < 1e-9 && ty < 1e-9)
            {
                g = cache.Geology(this, face, i, j, lattice);
                return;
            }
            GeologySample g00 = cache.Geology(this, face, i, j, lattice);
            GeologySample g10 = cache.Geology(this, face, i + 1, j, lattice);
            GeologySample g01 = cache.Geology(this, face, i, j + 1, lattice);
            GeologySample g11 = cache.Geology(this, face, i + 1, j + 1, lattice);
            float fx = (float)tx, fy = (float)ty;
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            g = new GeologySample
            {
                Bedrock = g00.Bedrock * w00 + g10.Bedrock * w10 + g01.Bedrock * w01 + g11.Bedrock * w11,
                Sediment = g00.Sediment * w00 + g10.Sediment * w10 + g01.Sediment * w01 + g11.Sediment * w11,
                Erosion = g00.Erosion * w00 + g10.Erosion * w10 + g01.Erosion * w01 + g11.Erosion * w11,
                Strata = g00.Strata * w00 + g10.Strata * w10 + g01.Strata * w01 + g11.Strata * w11,
                Slope = g00.Slope * w00 + g10.Slope * w10 + g01.Slope * w01 + g11.Slope * w11,
                Concavity = g00.Concavity * w00 + g10.Concavity * w10 + g01.Concavity * w01 + g11.Concavity * w11,
                RockTint = g00.RockTint * w00 + g10.RockTint * w10 + g01.RockTint * w01 + g11.RockTint * w11,
                Aridity = g00.Aridity * w00 + g10.Aridity * w10 + g01.Aridity * w01 + g11.Aridity * w11,
                Sedimentary = g00.Sedimentary * w00 + g10.Sedimentary * w10 + g01.Sedimentary * w01 + g11.Sedimentary * w11,
                Family = g00.Family * w00 + g10.Family * w10 + g01.Family * w01 + g11.Family * w11,
            };
            Vector3 flow = g00.Downslope * w00 + g10.Downslope * w10 + g01.Downslope * w01 + g11.Downslope * w11;
            g.Downslope = flow.sqrMagnitude > 1e-12f ? flow.normalized : Vector3.zero;
        }

        /// <summary>Geology at a direction on the reference lattice (or a coarser one).</summary>
        public void Sample(Vector3d direction, out GeologySample g, int lattice = ReferenceLatticeSize)
        {
            int face = CubeSphereGenerator.DirectionToFaceCoordinates(direction, out double a, out double b);
            Sample(face, (a + 1.0) * 0.5 * lattice, (b + 1.0) * 0.5 * lattice, lattice, out g);
        }

        // ------------------------------------------------------------------ Nodes

        internal struct ElevationNode
        {
            public float Elevation;
            public float Influence;
            public float Core;
            public Vector3d Direction;
        }

        internal ElevationNode ComputeElevationNode(int face, int i, int j, int lattice)
        {
            Vector3d direction = CubeSphereCoordinates.LatticeDirectionD(face, i, j, lattice);
            float spacing = Mathf.PI * 0.5f / lattice;
            float e = height.GetElevation(direction, spacing, out TerrainBaseSample context);
            return new ElevationNode { Elevation = e, Influence = context.Influence, Core = context.Core, Direction = direction };
        }

        internal GeologySample ComputeNode(GeologyCache cache, int face, int i, int j, int lattice)
        {
            ElevationNode c = cache.Elevation(this, face, i, j, lattice);
            // Local quadratic fit of the elevation on the 8 surrounding nodes, in the node's tangent plane: slope,
            // downslope direction and Laplacian that do not depend on the lattice's orientation or spacing. Near cube
            // corners the face lattice (continued past the face edges) is skewed and uneven; a fixed-axis stencil
            // would differ between two faces meeting there, a fit does not.
            Vector3d up = c.Direction;
            Vector3d reference = Math.Abs(up.y) < 0.9 ? Vector3d.Up : Vector3d.Right;
            Vector3d t = Vector3d.Cross(up, reference).Normalized;
            Vector3d b = Vector3d.Cross(up, t);
            Fit(cache, c, face, i, j, lattice, 1, up, t, b, out double gx, out double gy, out double laplacian);
            double gradientLength = Math.Sqrt(gx * gx + gy * gy);
            // Elevations are fractions of the radius and offsets radians: the gradient is a slope (m / m).
            float slope = (float)gradientLength;
            Vector3 downslope = gradientLength > 1e-6 ? ((t * gx + b * gy) * (-1.0 / gradientLength)).ToVector3() : Vector3.zero;
            // Concavity at the reference scale: Laplacian / 4 x reference spacing (a curvature times a fixed length:
            // comparable between the reference and the prefiltered lattices). Positive in hollows.
            float concavity = (float)(0.25 * laplacian * referenceSpacing);

            // Broad concavity (V0.2.0E): the same fit over nodes 4 cells away (about 600 m at the reference lattice), so
            // loose material fills valley floors and slope feet as continuous bodies, not every small hollow. On a
            // prefiltered (coarser) lattice the node spacing is already broad: the local value is used.
            float broad = concavity;
            if (lattice >= ReferenceLatticeSize)
            {
                Fit(cache, c, face, i, j, lattice, 4, up, t, b, out _, out _, out double broadLaplacian);
                broad = (float)(0.25 * broadLaplacian * referenceSpacing);
            }

            double toRegional = (double)RegionalLatticeSize / lattice;
            RegionalFields regional = cache.RegionalAt(this, face, i * toRegional, j * toRegional);
            return Evaluate(c.Direction.ToVector3(), c.Elevation, c.Influence, c.Core, slope, 0.5f * (concavity + broad), downslope, regional);
        }

        /// <summary>
        /// Least-squares fit of e - e(c) = gx x + gy y + a x^2 / 2 + h x y + d y^2 / 2 on the 8 nodes at a lattice distance
        /// <paramref name="step"/> around c, in tangent-plane coordinates (radians along t and b). Laplacian = a + d.
        /// </summary>
        void Fit(GeologyCache cache, in ElevationNode c, int face, int i, int j, int lattice, int step, Vector3d up, Vector3d t, Vector3d b,
            out double gx, out double gy, out double laplacian)
        {
            // Normal equations (5 x 5, symmetric), accumulated in place.
            Span<double> m = stackalloc double[25];
            Span<double> r = stackalloc double[5];
            Span<double> f = stackalloc double[5];
            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0)
                    {
                        continue;
                    }
                    ElevationNode n = cache.Elevation(this, face, i + di * step, j + dj * step, lattice);
                    // Gnomonic-like tangent coordinates (exact enough over a few hundred meters).
                    double k = Vector3d.Dot(n.Direction, up);
                    Vector3d w = n.Direction * (1.0 / Math.Max(k, 1e-9)) - up;
                    double x = Vector3d.Dot(w, t), y = Vector3d.Dot(w, b);
                    f[0] = x;
                    f[1] = y;
                    f[2] = 0.5 * x * x;
                    f[3] = x * y;
                    f[4] = 0.5 * y * y;
                    double z = n.Elevation - c.Elevation;
                    for (int p = 0; p < 5; p++)
                    {
                        r[p] += f[p] * z;
                        for (int q = 0; q < 5; q++)
                        {
                            m[p * 5 + q] += f[p] * f[q];
                        }
                    }
                }
            }
            // Gaussian elimination with partial pivoting.
            for (int col = 0; col < 5; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < 5; row++)
                {
                    if (Math.Abs(m[row * 5 + col]) > Math.Abs(m[pivot * 5 + col]))
                    {
                        pivot = row;
                    }
                }
                if (Math.Abs(m[pivot * 5 + col]) < 1e-300)
                {
                    gx = gy = laplacian = 0.0;
                    return;
                }
                if (pivot != col)
                {
                    for (int q = 0; q < 5; q++)
                    {
                        (m[col * 5 + q], m[pivot * 5 + q]) = (m[pivot * 5 + q], m[col * 5 + q]);
                    }
                    (r[col], r[pivot]) = (r[pivot], r[col]);
                }
                for (int row = col + 1; row < 5; row++)
                {
                    double factor = m[row * 5 + col] / m[col * 5 + col];
                    for (int q = col; q < 5; q++)
                    {
                        m[row * 5 + q] -= factor * m[col * 5 + q];
                    }
                    r[row] -= factor * r[col];
                }
            }
            Span<double> s = stackalloc double[5];
            for (int row = 4; row >= 0; row--)
            {
                double sum = r[row];
                for (int q = row + 1; q < 5; q++)
                {
                    sum -= m[row * 5 + q] * s[q];
                }
                s[row] = sum / m[row * 5 + row];
            }
            gx = s[0];
            gy = s[1];
            laplacian = s[2] + s[4];
        }

        /// <summary>Slowly varying inputs of the geology (climate, terrain character, province fields), on the regional lattice.</summary>
        internal struct RegionalFields
        {
            public float Aridity;
            public float Rugged;
            public float Smooth;
            public float Province;
            public float Family;
            public float Local;
        }

        internal RegionalFields ComputeRegional(Vector3 direction)
        {
            float aridity = 0.5f;
            if (climate != null)
            {
                float precipitation = climate.Sample(climate.Precipitation, direction);
                float temperature = climate.Sample(climate.Temperature, direction);
                aridity = 1f - SmoothStep(0.35f, 1.3f, precipitation);
                aridity *= SmoothStep(-12f, 2f, temperature) * 0.5f + 0.5f; // frozen land holds its cover differently
            }
            float character = detail != null ? detail.Character(direction) : 0f;
            return new RegionalFields
            {
                Aridity = aridity,
                Rugged = SmoothStep(-0.15f, -0.55f, character),
                Smooth = SmoothStep(0.15f, 0.55f, character),
                Province = PlanetNoise.Fractal(direction * 3.1f, provinceSeed, 2),
                Family = PlanetNoise.Fractal(direction * 7.3f, familySeed, 2),
                Local = PlanetNoise.Fractal(direction * 45f, localSeed, 2),
            };
        }

        internal RegionalFields ComputeRegionalNode(int face, int i, int j) =>
            ComputeRegional(CubeSphereCoordinates.LatticeDirectionD(face, i, j, RegionalLatticeSize).ToVector3());

        /// <summary>
        /// The geological characteristics from the node's terrain facts (the model; see the class remarks). Thresholds
        /// calibrated on the reference planets' pilot regions at the reference lattice: land slope p50 ~0.2, p90 ~0.5.
        /// </summary>
        internal GeologySample Evaluate(Vector3 direction, float elevation, float influence, float core, float slope, float concavity, Vector3 downslope, in RegionalFields r)
        {
            float above = elevation - seaLevel;
            float aboveMeters = above * radiusMeters;
            float aridity = r.Aridity;
            float steep = SmoothStep(0.22f, 0.55f, slope);
            // Concavity at the reference lattice (quadratic fit): p10 / p90 about -0.05 / +0.05 on the reference pilots.
            float convex = SmoothStep(0.005f, 0.06f, -concavity);
            float hollow = SmoothStep(0.005f, 0.05f, concavity);
            float moderate = SmoothStep(0.08f, 0.25f, slope);
            float sedimentary = Clamp01(0.3f + 0.9f * r.Province + 0.35f * aridity - 0.6f * core - 0.15f * influence);

            // Coast (V0.2.0F addendum), in meters above the sea: V0.2.0E used a band of 0.0003 radius (about 1.9 km of
            // exaggerated elevation), a uniform beige ring around every continent. Now a beach only where the shore is
            // gentle, its height varying along the coast (3 .. 35 m, a seeded field of ~50 km features) and wider on
            // sedimentary coasts; on steep shores the bedrock reaches the water instead.
            float alongShore = 0.5f + 0.5f * PlanetNoise.Fractal(direction * 800f, coastSeed, 2);
            float beachHeight = Mathf.Lerp(3f, 35f, alongShore) * Mathf.Lerp(0.6f, 1.4f, sedimentary);
            float gentle = 1f - SmoothStep(0.06f, 0.3f, slope);
            float shore = 1f - SmoothStep(0f, beachHeight, aboveMeters);
            float beach = shore * gentle;
            float rockyShore = (1f - SmoothStep(0f, 15f, aboveMeters)) * SmoothStep(0.2f, 0.5f, slope);

            float bedrock = Clamp01(steep * (0.8f + 0.2f * convex) + 0.35f * convex * moderate + 0.25f * core * moderate
                + 0.3f * aridity * moderate - 0.5f * hollow - 0.5f * beach + 0.3f * rockyShore);
            float flat = 1f - SmoothStep(0.05f, 0.2f, slope);
            float sediment = Clamp01(flat * (0.3f + 0.7f * hollow) + 0.7f * hollow * (1f - steep) + 0.6f * beach + 0.25f * aridity * flat) * (1f - bedrock);
            float erosion = Clamp01(SmoothStep(0.1f, 0.4f, slope) * Mathf.Lerp(0.4f, 1f, aridity) * Mathf.Lerp(0.75f, 1.25f, r.Rugged) * Mathf.Lerp(1f, 0.6f, r.Smooth));
            float strata = Clamp01(SmoothStep(0.35f, 0.75f, sedimentary) * Mathf.Lerp(0.5f, 1f, aridity) * (1f - 0.5f * r.Smooth));

            float family = r.Family;
            Color crystalline = family < 0f
                ? Color.Lerp(Granite, PinkGranite, SmoothStep(-0.1f, -0.5f, family))
                : Color.Lerp(Granite, Basalt, SmoothStep(0.1f, 0.55f, family));
            Color layered = Color.Lerp(RedSandstone, OchreLimestone, SmoothStep(-0.35f, 0.35f, family));
            Color tint = Color.Lerp(crystalline, layered, sedimentary) * (1f + 0.1f * r.Local);
            if (above <= 0f)
            {
                bedrock = 0f;
                sediment = 1f;
                erosion = 0f;
                strata = 0f;
            }
            return new GeologySample
            {
                Bedrock = bedrock,
                Sediment = sediment,
                Erosion = erosion,
                Strata = strata,
                Slope = slope,
                Concavity = concavity,
                Downslope = downslope,
                RockTint = new Color(Clamp01(tint.r), Clamp01(tint.g), Clamp01(tint.b), 1f),
                Aridity = aridity,
                Sedimentary = sedimentary,
                Family = Mathf.Clamp(family, -1f, 1f),
            };
        }

        // ------------------------------------------------------------------ Pilot region

        /// <summary>
        /// The pilot region of a planet (V0.2.0E validation): the land point under the cloud layer that best combines a
        /// mountain envelope, dry ground cover (the biome color itself: bare or sparse, not forest) and a sedimentary
        /// province (layered rock, gullies, debris: the richest identity). Deterministic (fixed sample set), main thread.
        /// </summary>
        public Vector3d FindPilot(int samples = 20000)
        {
            Vector3d best = Vector3d.Up;
            float bestScore = float.MinValue;
            for (int k = 0; k < samples; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / samples, rr = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3d(rr * Math.Cos(phi), y, rr * Math.Sin(phi));
                float e = height.GetElevation(d, 0.002f, out TerrainBaseSample context);
                float above = e - seaLevel;
                if (above < 0.002f || above > 0.006f)
                {
                    continue; // land under the cloud layer, not a summit
                }
                Vector3 dir = d.ToVector3();
                RegionalFields regional = ComputeRegional(dir);
                GeologySample g = Evaluate(dir, e, context.Influence, context.Core, 0.3f, 0f, Vector3.zero, regional);
                float dry = 0.5f;
                if (natural != null)
                {
                    Color ground = natural.Evaluate(dir, e);
                    dry = SmoothStep(-0.02f, 0.08f, ground.r - ground.g);
                }
                float score = context.Influence * 1.5f + 1.5f * dry + g.Sedimentary;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = d;
                }
            }
            return best;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    }

    /// <summary>Per-thread direct-mapped caches of geology lattice nodes (elevation stencil samples and characteristics).</summary>
    internal sealed class GeologyCache
    {
        const int Slots = 4096;

        [ThreadStatic] static GeologyCache current;

        public static GeologyCache Current => current ??= new GeologyCache();

        readonly SurfaceGeology[] elevationOwners = new SurfaceGeology[Slots];
        readonly long[] elevationKeys = new long[Slots];
        readonly SurfaceGeology.ElevationNode[] elevations = new SurfaceGeology.ElevationNode[Slots];
        readonly SurfaceGeology[] geologyOwners = new SurfaceGeology[Slots];
        readonly long[] geologyKeys = new long[Slots];
        readonly GeologySample[] geologies = new GeologySample[Slots];

        // face (3 bits) | lattice (17 bits, <= 2^16) | i + 8 (20 bits) | j + 8 (20 bits). Stencils reach a few nodes past
        // a face edge (negative indices): the offset keeps every field non-negative and in its own bits.
        static long Key(int face, int i, int j, int lattice) =>
            ((long)face << 57) | ((long)lattice << 40) | ((long)((i + 8) & 0xFFFFF) << 20) | (long)((j + 8) & 0xFFFFF);

        static int Slot(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 52) & (Slots - 1);

        public SurfaceGeology.ElevationNode Elevation(SurfaceGeology owner, int face, int i, int j, int lattice)
        {
            long key = Key(face, i, j, lattice);
            int slot = Slot(key);
            if (elevationOwners[slot] != owner || elevationKeys[slot] != key)
            {
                elevations[slot] = owner.ComputeElevationNode(face, i, j, lattice);
                elevationOwners[slot] = owner;
                elevationKeys[slot] = key;
            }
            return elevations[slot];
        }

        const int RegionalSlots = 1024;
        readonly SurfaceGeology[] regionalOwners = new SurfaceGeology[RegionalSlots];
        readonly long[] regionalKeys = new long[RegionalSlots];
        readonly SurfaceGeology.RegionalFields[] regionals = new SurfaceGeology.RegionalFields[RegionalSlots];

        SurfaceGeology.RegionalFields RegionalNode(SurfaceGeology owner, int face, int i, int j)
        {
            long key = Key(face, i, j, SurfaceGeology.RegionalLatticeSize);
            int slot = Slot(key) & (RegionalSlots - 1);
            if (regionalOwners[slot] != owner || regionalKeys[slot] != key)
            {
                regionals[slot] = owner.ComputeRegionalNode(face, i, j);
                regionalOwners[slot] = owner;
                regionalKeys[slot] = key;
            }
            return regionals[slot];
        }

        /// <summary>Regional fields at regional-lattice coordinates (bilinear between the four nodes).</summary>
        public SurfaceGeology.RegionalFields RegionalAt(SurfaceGeology owner, int face, double x, double y)
        {
            int i = (int)Math.Floor(x), j = (int)Math.Floor(y);
            float fx = (float)(x - i), fy = (float)(y - j);
            SurfaceGeology.RegionalFields a = RegionalNode(owner, face, i, j), b = RegionalNode(owner, face, i + 1, j),
                c = RegionalNode(owner, face, i, j + 1), d = RegionalNode(owner, face, i + 1, j + 1);
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            return new SurfaceGeology.RegionalFields
            {
                Aridity = a.Aridity * w00 + b.Aridity * w10 + c.Aridity * w01 + d.Aridity * w11,
                Rugged = a.Rugged * w00 + b.Rugged * w10 + c.Rugged * w01 + d.Rugged * w11,
                Smooth = a.Smooth * w00 + b.Smooth * w10 + c.Smooth * w01 + d.Smooth * w11,
                Province = a.Province * w00 + b.Province * w10 + c.Province * w01 + d.Province * w11,
                Family = a.Family * w00 + b.Family * w10 + c.Family * w01 + d.Family * w11,
                Local = a.Local * w00 + b.Local * w10 + c.Local * w01 + d.Local * w11,
            };
        }

        public GeologySample Geology(SurfaceGeology owner, int face, int i, int j, int lattice)
        {
            long key = Key(face, i, j, lattice);
            int slot = Slot(key);
            if (geologyOwners[slot] != owner || geologyKeys[slot] != key)
            {
                geologies[slot] = owner.ComputeNode(this, face, i, j, lattice);
                geologyOwners[slot] = owner;
                geologyKeys[slot] = key;
            }
            return geologies[slot];
        }
    }
}
