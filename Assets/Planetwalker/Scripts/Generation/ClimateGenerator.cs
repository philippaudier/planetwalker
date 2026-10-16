using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Deterministic annual-mean climate (V0.1.1E-A): a downstream analysis of the terrain that never
    /// modifies it (nor the hydrology, rivers or lakes).
    /// </summary>
    /// <remarks>
    /// Grid: the equi-angular cube-sphere cell graph of <see cref="HydrologyGrid"/> (shared class,
    /// separate resolution): exact cell areas and explicit neighbourhoods across cube faces, so every
    /// exchange crosses seams like any other edge. Elevation is sampled from the continuous elevation
    /// function at cell centers (independent of the terrain mesh resolution).
    ///
    /// 1. Forcing: latitude from the rotation axis, annual-mean insolation Q (fraction of S0) from
    ///    the axial tilt (<see cref="ClimateForcing"/>).
    /// 2. Temperature (C): linear energy-balance relation T = Teq + B (Q - Qeq), calibrated so that an
    ///    Earth-like tilt gives the configured equator / pole temperatures; continental amplification
    ///    of the departure from 10 C away from the ocean; lapse rate on the physical elevation
    ///    (elevation above sea level x 100 x elevationScale km); small deterministic noise.
    /// 3. Ocean influence: shortest-path distance to the ocean over the cell graph (great-circle
    ///    steps, km); influence = exp(-d / L).
    /// 4. Moisture (index, 1 = saturated air at 25 C): capacity C(T) = exp(0.0687 (T - 25))
    ///    (Clausius-Clapeyron, about 7%/K). Each iteration, all cells at once (double buffer, no
    ///    traversal-order bias): advection to the two neighbours bracketing the wind (donor cell,
    ///    barycentric weights so the mean displacement is exactly along the wind, fraction scaled so
    ///    the air travels 0.8 x wind speed cells per iteration whatever the grid orientation;
    ///    conservative in area-weighted amount), ocean evaporation relaxing towards C(T), then
    ///    precipitation = large-scale
    ///    rain-out (latitude tendency: wet ITCZ, dry subtropics, wet mid-latitude storm tracks) +
    ///    orographic rain-out (lifting along the wind over the upslope height, e-folding 1.2 km) +
    ///    condensation of any excess above C(T) (air cooled by ascent or by moving poleward). The
    ///    large-scale rain-out scales with relative humidity (dry air rains little). Over land 70% of
    ///    the rain returns to the air (evapotranspiration recycling: water moved, not created).
    ///    Iterated to a steady state (max change below 2e-5, or a bounded iteration count).
    /// 5. Precipitation: the steady-state rain-out per iteration, normalized by its area-weighted
    ///    planetary mean (index 1 = planet average).
    /// </remarks>
    public sealed class ClimateGenerator
    {
        /// <summary>Air travel per iteration at unit wind speed, in cells (outflow fraction stays below 0.95).</summary>
        public const float Courant = 0.8f;

        /// <summary>Lifting height raining out 63% of the moisture (km).</summary>
        public const float OrographicHeight = 1.2f;
        public const float ConvergenceTolerance = 2e-5f;

        /// <summary>Exponent of the insolation-temperature calibration (below 1: flatter tropics, steeper
        /// high latitudes, the signature of poleward heat transport).</summary>
        public const float HeatTransportExponent = 0.6f;
        public const int MaxIterations = 2000;

        static readonly Dictionary<float, float[]> InsolationTables = new Dictionary<float, float[]>();
        static float earthEquator = -1f, earthPole;

        /// <summary>Climate of a planet (continuous elevation field, its sea level and seed).</summary>
        public ClimateResult Compute(PlanetInstance planet, ClimateSettings settings, HydrologyGrid grid = null)
        {
            settings.Sanitize();
            grid ??= new HydrologyGrid(settings.resolution);
            float spacing = Mathf.PI * 0.5f / grid.Resolution; // band-limited to the grid and the legacy world (V0.2.0C)
            return Compute(grid, d => planet.GetLegacyElevation(d, spacing), planet.SeaLevel, planet.Definition.seed, settings);
        }

        /// <summary>
        /// Climate of any elevation function. <paramref name="windOverride"/> (tests, synthetic scenarios)
        /// replaces the prevailing wind; it must return tangent vectors (normalized speed about 1).
        /// </summary>
        public ClimateResult Compute(HydrologyGrid grid, Func<Vector3, float> elevation, float seaLevel, int seed, ClimateSettings settings,
            Func<Vector3, Vector3> windOverride = null)
        {
            var total = Stopwatch.StartNew();
            var watch = Stopwatch.StartNew();
            ClimateSettings s = settings.Clone();
            s.Sanitize();
            int count = grid.CellCount;
            var result = new ClimateResult(grid, s, seed, seaLevel);
            Vector3 axis = s.rotationAxis.normalized;
            float[] table = Table(s.axialTilt);
            CalibrateEarth(out float equatorQ, out float poleQ);

            float kmPerElevation = 100f * s.elevationScale;

            // Geography.
            for (int c = 0; c < count; c++)
            {
                Vector3 p = grid.GetCenter(c);
                float e = elevation(p);
                result.Latitude[c] = ClimateForcing.Latitude(p, axis);
                result.Elevation[c] = e;
                result.IsOcean[c] = !(e > seaLevel);
                result.HeightKm[c] = result.IsOcean[c] ? 0f : (e - seaLevel) * kmPerElevation;
                result.Insolation[c] = ClimateForcing.SampleTable(table, result.Latitude[c]);
            }
            OceanDistance(grid, result, s.planetRadiusKm);
            for (int c = 0; c < count; c++)
            {
                result.OceanInfluence[c] = result.IsOcean[c] ? 1f : Mathf.Exp(-result.OceanDistanceKm[c] / s.oceanInfluenceDistance);
            }

            // Temperature.
            int noiseSeed = PlanetNoise.DeriveSeed(seed, 9201);
            for (int c = 0; c < count; c++)
            {
                float seaLevelT = SeaLevelTemperature(result.Insolation[c], equatorQ, poleQ, s);
                seaLevelT += s.continentality * (1f - result.OceanInfluence[c]) * (seaLevelT - 10f);
                float noise = s.temperatureVariation > 0f
                    ? s.temperatureVariation * Mathf.Clamp(PlanetNoise.Fractal(grid.GetCenter(c) * 3f, noiseSeed, 3) * 1.5f, -1f, 1f) : 0f;
                result.Temperature[c] = seaLevelT - s.lapseRate * result.HeightKm[c] + noise;
            }
            result.Diagnostics.TemperatureMilliseconds = (float)watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            Transport(grid, result, elevation, seaLevel, kmPerElevation, seed, s, windOverride);
            result.Diagnostics.MoistureMilliseconds = (float)watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            Precipitation(grid, result);
            result.Diagnostics.PrecipitationMilliseconds = (float)watch.Elapsed.TotalMilliseconds;
            result.ComputeStatistics();
            result.Diagnostics.TotalMilliseconds = (float)total.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Sea-level annual-mean temperature from the annual-mean insolation: T = Tpole + (Teq - Tpole) f(x),
        /// x = (Q - Qpole) / (Qeq - Qpole) with Earth-tilt references, f(x) = x^0.6 for x >= 0 (concave:
        /// poleward heat transport) and linear below (insolation lower than an Earth pole, e.g. tilt 0).
        /// </summary>
        public static float SeaLevelTemperature(float insolation, float equatorQ, float poleQ, ClimateSettings s)
        {
            float x = (insolation - poleQ) / (equatorQ - poleQ);
            float f = x >= 0f ? Mathf.Pow(x, HeatTransportExponent) : x;
            return s.poleTemperature + (s.equatorTemperature - s.poleTemperature) * f;
        }

        static float[] Table(float tilt)
        {
            lock (InsolationTables)
            {
                if (!InsolationTables.TryGetValue(tilt, out float[] table))
                {
                    table = ClimateForcing.InsolationTable(tilt);
                    InsolationTables.Add(tilt, table);
                }
                return table;
            }
        }

        static void CalibrateEarth(out float equator, out float pole)
        {
            if (earthEquator < 0f)
            {
                double tilt = ClimateForcing.EarthTilt * Math.PI / 180.0;
                earthPole = (float)ClimateForcing.AnnualMeanInsolation(Math.PI / 2.0, tilt);
                earthEquator = (float)ClimateForcing.AnnualMeanInsolation(0.0, tilt);
            }
            equator = earthEquator;
            pole = earthPole;
        }

        /// <summary>Multi-source shortest path (Dijkstra) from the ocean over the cell graph, in km.</summary>
        static void OceanDistance(HydrologyGrid grid, ClimateResult result, float radiusKm)
        {
            int count = grid.CellCount;
            float[] distance = result.OceanDistanceKm;
            var heap = new MinHeap(count);
            for (int c = 0; c < count; c++)
            {
                distance[c] = result.IsOcean[c] ? 0f : float.PositiveInfinity;
                if (result.IsOcean[c])
                {
                    heap.Push(c, 0f);
                }
            }
            while (heap.TryPop(out int cell, out float d))
            {
                if (d > distance[cell])
                {
                    continue;
                }
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    float step = 2f * Mathf.Asin(Mathf.Min(1f, 0.5f * grid.GetNeighborDistance(k))) * radiusKm;
                    if (d + step < distance[other])
                    {
                        distance[other] = d + step;
                        heap.Push(other, distance[other]);
                    }
                }
            }
            for (int c = 0; c < count; c++)
            {
                if (float.IsInfinity(distance[c]))
                {
                    distance[c] = 2f * Mathf.PI * radiusKm; // no ocean at all
                }
            }
        }

        /// <summary>Large-scale precipitation tendency (dimensionless multiplier of the rain-out rate).</summary>
        public static float RainTendency(float latitude) => RainTendency(latitude, 1f);

        /// <summary>Same, with the storm-track strength of the settings.</summary>
        public static float RainTendency(float latitude, float stormTrackStrength)
        {
            float a = Mathf.Abs(latitude) * Mathf.Rad2Deg;
            float itcz = 1.2f * Mathf.Exp(-(a / 12f) * (a / 12f));
            float stormTracks = stormTrackStrength * Mathf.Exp(-((a - 50f) / 14f) * ((a - 50f) / 14f));
            float subsidence = 0.3f * Mathf.Exp(-((a - 27f) / 9f) * ((a - 27f) / 9f));
            return Mathf.Max(0.12f, 0.4f + itcz + stormTracks - subsidence);
        }

        /// <summary>Moisture capacity at a temperature (index, 1 at 25 C), bounded.</summary>
        public static float Capacity(float temperature)
        {
            return Mathf.Clamp(Mathf.Exp(0.0687f * (temperature - 25f)), 0.01f, 1.6f);
        }

        void Transport(HydrologyGrid grid, ClimateResult result, Func<Vector3, float> elevation, float seaLevel, float kmPerElevation, int seed,
            ClimateSettings s, Func<Vector3, Vector3> windOverride)
        {
            int count = grid.CellCount;
            float cellAngle = Mathf.PI * 0.5f / grid.Resolution;
            float cellKm = cellAngle * s.planetRadiusKm;
            float travel = Courant * cellKm; // km per iteration at unit wind speed
            Vector3 axis = s.rotationAxis.normalized;

            // Per-cell transport coefficients (static: computed once).
            var outflow = new float[count];
            var weight = new float[grid.GetNeighborEnd(count - 1)];
            var evaporation = new float[count];
            var backgroundKeep = new float[count];
            var orographicKeep = new float[count];
            var capacity = new float[count];
            for (int c = 0; c < count; c++)
            {
                Vector3 p = grid.GetCenter(c);
                Vector3 wind = windOverride != null ? windOverride(p) : ClimateForcing.PrevailingWind(p, axis, seed, s.windPerturbation);
                wind -= p * Vector3.Dot(wind, p);
                result.Wind[c] = wind;
                float speed = Mathf.Clamp(wind.magnitude, 0.05f, 1.5f);
                Vector3 heading = wind.sqrMagnitude > 1e-12f ? wind.normalized : Vector3.zero;

                // Downwind neighbours: the two whose directions bracket the wind, weighted so that the
                // mean displacement is exactly along the wind; the outflow fraction is scaled by that
                // displacement, so the air travels Courant x speed cells per iteration whatever the
                // orientation of the grid (cube-face seams and the polar faces rotate it).
                outflow[c] = heading != Vector3.zero ? BracketWind(grid, c, p, heading, weight) * Courant * speed * cellAngle : 0f;
                outflow[c] = Mathf.Min(0.95f, outflow[c]);

                // Upslope height gained by the air per iteration (km), along the wind, on the continuous field.
                float upslope = 0f;
                if (heading != Vector3.zero)
                {
                    float up = PhysicalHeight(elevation((p + heading * cellAngle).normalized), seaLevel, kmPerElevation);
                    float down = PhysicalHeight(elevation((p - heading * cellAngle).normalized), seaLevel, kmPerElevation);
                    float gradient = (up - down) / (2f * cellKm); // km per km
                    upslope = Mathf.Max(0f, gradient) * travel * speed;
                    result.Upslope[c] = gradient;
                }
                capacity[c] = Capacity(result.Temperature[c]);
                evaporation[c] = result.IsOcean[c] ? 1f - Mathf.Exp(-travel / s.evaporationLength) : 0f;
                backgroundKeep[c] = Mathf.Exp(-travel * RainTendency(result.Latitude[c], s.stormTracks) / s.rainLength);
                orographicKeep[c] = Mathf.Exp(-upslope / OrographicHeight);
            }

            // Gather form of the donor-cell advection (hot loop): each cell reads what it receives from
            // its upwind sources, coef = source area x outflow x weight / destination area. Identical to
            // scattering the outflow (conservative), but one pass per iteration, no buffer clearing.
            var area = new float[count];
            var stay = new float[count];
            var inCount = new int[count + 1];
            for (int c = 0; c < count; c++)
            {
                area[c] = grid.GetArea(c);
                stay[c] = 1f - outflow[c];
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    if (weight[k] > 0f && outflow[c] > 0f)
                    {
                        inCount[grid.GetNeighbor(k) + 1]++;
                    }
                }
            }
            for (int c = 0; c < count; c++)
            {
                inCount[c + 1] += inCount[c];
            }
            var inSource = new int[inCount[count]];
            var inCoef = new float[inCount[count]];
            var fill = new int[count];
            for (int c = 0; c < count; c++)
            {
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    if (weight[k] > 0f && outflow[c] > 0f)
                    {
                        int destination = grid.GetNeighbor(k);
                        int slot = inCount[destination] + fill[destination]++;
                        inSource[slot] = c;
                        inCoef[slot] = grid.GetArea(c) * outflow[c] * weight[k] / grid.GetArea(destination);
                    }
                }
            }
            var recycling = new float[count];
            var rainBase = new float[count];
            for (int c = 0; c < count; c++)
            {
                recycling[c] = result.IsOcean[c] ? 0f : s.landRecycling;
                rainBase[c] = 1f - backgroundKeep[c];
            }

            // Iterate to a steady state (Jacobi: every cell reads the previous state only; two buffers swapped).
            float[] moisture = result.Moisture;
            var next = new float[count];
            float[] rain = result.RawPrecipitation;
            float[] evaporated = result.Evaporation;
            for (int c = 0; c < count; c++)
            {
                moisture[c] = result.IsOcean[c] ? capacity[c] : 0f;
            }
            ref ClimateDiagnostics d = ref result.Diagnostics;
            float humidityExponent = s.humidityRainExponent;
            bool linearHumidity = humidityExponent == 1f;
            int iteration = 0;
            bool converged = false;
            double maxBalanceError = 0.0;
            for (; iteration < MaxIterations && !converged; iteration++)
            {
                double before = 0.0, afterTransport = 0.0, added = 0.0, removed = 0.0, after = 0.0, recycled = 0.0;
                float maxChange = 0f, maxValue = 0f;
                for (int c = 0; c < count; c++)
                {
                    float cellArea = area[c];
                    float old = moisture[c];
                    before += (double)old * cellArea;
                    // Advection.
                    float m = old * stay[c];
                    for (int l = inCount[c], lEnd = inCount[c + 1]; l < lEnd; l++)
                    {
                        m += moisture[inSource[l]] * inCoef[l];
                    }
                    afterTransport += (double)m * cellArea;
                    // Ocean evaporation towards the capacity.
                    float cap = capacity[c];
                    float e = cap > m ? evaporation[c] * (cap - m) : 0f;
                    m += e;
                    // Large-scale rain-out, scaled by relative humidity^k (dry air rains little), and
                    // forced orographic rain-out (independent of humidity).
                    float background = rainBase[c];
                    if (humidityExponent > 0f)
                    {
                        float relative = m >= cap ? 1f : m / cap;
                        background *= linearHumidity ? relative : Mathf.Pow(relative, humidityExponent);
                    }
                    float r = m * (1f - (1f - background) * orographicKeep[c]);
                    float excess = m - r - cap;
                    if (excess > 0f)
                    {
                        r += excess;
                    }
                    m -= r;
                    if (m < 0f)
                    {
                        r += m;
                        m = 0f;
                    }
                    // Land: part of the rain returns to the air (evapotranspiration), within capacity.
                    float room = cap > m ? cap - m : 0f;
                    float back = recycling[c] * r;
                    if (back > room)
                    {
                        back = room;
                    }
                    m += back;
                    added += (double)e * cellArea;
                    removed += (double)r * cellArea;
                    recycled += (double)back * cellArea;
                    after += (double)m * cellArea;
                    evaporated[c] = e;
                    rain[c] = r;
                    float change = m > old ? m - old : old - m;
                    if (change > maxChange)
                    {
                        maxChange = change;
                    }
                    if (m > maxValue)
                    {
                        maxValue = m;
                    }
                    next[c] = m;
                }
                // Swap buffers.
                (moisture, next) = (next, moisture);
                double scale = Math.Max(before, 1e-12);
                maxBalanceError = Math.Max(maxBalanceError, Math.Abs(afterTransport - before) / scale);
                maxBalanceError = Math.Max(maxBalanceError, Math.Abs(after - (afterTransport + added + recycled - removed)) / scale);
                d.Evaporation = added;
                d.Recycled = recycled;
                d.Precipitation = removed;
                d.StorageChange = after - before;
                d.AtmosphericMoisture = after;
                converged = maxChange <= ConvergenceTolerance * Mathf.Max(maxValue, 1e-6f);
            }
            if (moisture != result.Moisture)
            {
                Array.Copy(moisture, result.Moisture, count);
            }            d.Iterations = iteration;
            d.Converged = converged;
            d.MassBalanceError = maxBalanceError;
        }

        /// <summary>
        /// Sets the weights of a cell's neighbours (zero except the one or two whose directions bracket
        /// the wind heading, barycentric so that the weighted displacement is parallel to the wind) and
        /// returns 1 / |mean displacement| (unit sphere), or 0 when no neighbour lies downwind.
        /// </summary>
        static float BracketWind(HydrologyGrid grid, int cell, Vector3 p, Vector3 heading, float[] weight)
        {
            Vector3 side = Vector3.Cross(p, heading);
            int start = grid.GetNeighborStart(cell), end = grid.GetNeighborEnd(cell);
            int left = -1, right = -1;
            float leftAngle = float.MaxValue, rightAngle = float.MaxValue;
            for (int k = start; k < end; k++)
            {
                weight[k] = 0f;
                Vector3 offset = grid.GetCenter(grid.GetNeighbor(k)) - p;
                offset -= p * Vector3.Dot(offset, p);
                // Signed angle from the heading, positive to the left.
                float angle = Mathf.Atan2(Vector3.Dot(offset, side), Vector3.Dot(offset, heading));
                if (angle >= 0f && angle < leftAngle)
                {
                    leftAngle = angle;
                    left = k;
                }
                if (angle <= 0f && -angle < rightAngle)
                {
                    rightAngle = -angle;
                    right = k;
                }
            }
            if (left < 0 && right < 0)
            {
                return 0f;
            }
            if (left < 0 || right < 0 || left == right || leftAngle + rightAngle >= Mathf.PI * 0.75f)
            {
                // Single neighbour (exactly along the wind, or no usable bracket): the closest in angle.
                int k = left < 0 || (right >= 0 && rightAngle < leftAngle) ? right : left;
                Vector3 only = grid.GetCenter(grid.GetNeighbor(k)) - p;
                float along = Vector3.Dot(only, heading);
                if (along <= 0f)
                {
                    return 0f;
                }
                weight[k] = 1f;
                return 1f / along;
            }
            Vector3 a = grid.GetCenter(grid.GetNeighbor(left)) - p, b = grid.GetCenter(grid.GetNeighbor(right)) - p;
            // alpha a + beta b parallel to the heading: zero cross-wind component.
            float sa = Vector3.Dot(a, side), sb = Vector3.Dot(b, side);
            float alpha = -sb / (sa - sb), beta = 1f - alpha;
            weight[left] = alpha;
            weight[right] = beta;
            float displacement = alpha * Vector3.Dot(a, heading) + beta * Vector3.Dot(b, heading);
            return displacement > 0f ? 1f / displacement : 0f;
        }

        static float PhysicalHeight(float elevation, float seaLevel, float kmPerElevation)
        {
            return elevation > seaLevel ? (elevation - seaLevel) * kmPerElevation : 0f;
        }

        static void Precipitation(HydrologyGrid grid, ClimateResult result)
        {
            double sum = 0.0, area = 0.0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                sum += (double)result.RawPrecipitation[c] * grid.GetArea(c);
                area += grid.GetArea(c);
            }
            float mean = (float)(sum / Math.Max(area, 1e-12));
            for (int c = 0; c < grid.CellCount; c++)
            {
                result.Precipitation[c] = mean > 0f ? result.RawPrecipitation[c] / mean : 0f;
            }
            result.RawPrecipitationMean = mean;
        }

        /// <summary>Binary min-heap of (cell, key), deterministic (ties by cell index).</summary>
        sealed class MinHeap
        {
            int[] cells;
            float[] keys;
            int size;

            public MinHeap(int capacity)
            {
                cells = new int[Math.Max(16, capacity)];
                keys = new float[cells.Length];
            }

            public void Push(int cell, float key)
            {
                if (size == cells.Length)
                {
                    Array.Resize(ref cells, size * 2);
                    Array.Resize(ref keys, size * 2);
                }
                int i = size++;
                cells[i] = cell;
                keys[i] = key;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (!Less(i, parent))
                    {
                        break;
                    }
                    Swap(i, parent);
                    i = parent;
                }
            }

            public bool TryPop(out int cell, out float key)
            {
                if (size == 0)
                {
                    cell = -1;
                    key = 0f;
                    return false;
                }
                cell = cells[0];
                key = keys[0];
                size--;
                cells[0] = cells[size];
                keys[0] = keys[size];
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1, right = left + 1, smallest = i;
                    if (left < size && Less(left, smallest)) smallest = left;
                    if (right < size && Less(right, smallest)) smallest = right;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
                return true;
            }

            bool Less(int a, int b) => keys[a] < keys[b] || (keys[a] == keys[b] && cells[a] < cells[b]);

            void Swap(int a, int b)
            {
                (cells[a], cells[b]) = (cells[b], cells[a]);
                (keys[a], keys[b]) = (keys[b], keys[a]);
            }
        }
    }

    /// <summary>
    /// Climate fields on the climate grid (one value per cell) plus measured diagnostics. Units:
    /// temperature C; latitude radians; heights km; distances km; insolation fraction of S0;
    /// moisture index (1 = saturated air at 25 C); precipitation index (1 = planet area mean).
    /// </summary>
    public sealed class ClimateResult
    {
        public ClimateResult(HydrologyGrid grid, ClimateSettings settings, int seed, float seaLevel)
        {
            Grid = grid;
            Settings = settings;
            Seed = seed;
            SeaLevel = seaLevel;
            int n = grid.CellCount;
            Latitude = new float[n];
            Elevation = new float[n];
            HeightKm = new float[n];
            IsOcean = new bool[n];
            Insolation = new float[n];
            OceanDistanceKm = new float[n];
            OceanInfluence = new float[n];
            Temperature = new float[n];
            Wind = new Vector3[n];
            Upslope = new float[n];
            Moisture = new float[n];
            Evaporation = new float[n];
            RawPrecipitation = new float[n];
            Precipitation = new float[n];
        }

        public HydrologyGrid Grid { get; }
        /// <summary>Sanitized copy of the settings used.</summary>
        public ClimateSettings Settings { get; }
        public int Seed { get; }
        public float SeaLevel { get; }

        public readonly float[] Latitude;
        /// <summary>Procedural elevation (fraction of the radius), as sampled.</summary>
        public readonly float[] Elevation;
        /// <summary>Physical elevation above sea level used by the climate (km, 0 over the ocean).</summary>
        public readonly float[] HeightKm;
        public readonly bool[] IsOcean;
        /// <summary>Annual-mean insolation, fraction of the solar constant (global mean 0.25).</summary>
        public readonly float[] Insolation;
        public readonly float[] OceanDistanceKm;
        /// <summary>exp(-ocean distance / L), 1 over the ocean, towards 0 deep inland.</summary>
        public readonly float[] OceanInfluence;
        /// <summary>Annual-mean surface air temperature, degrees C.</summary>
        public readonly float[] Temperature;
        /// <summary>Prevailing wind, tangent vector (speed about 0.2..1.2).</summary>
        public readonly Vector3[] Wind;
        /// <summary>Terrain gradient along the wind (km per km, positive = rising downwind).</summary>
        public readonly float[] Upslope;
        /// <summary>Steady-state atmospheric moisture index (1 = saturated at 25 C).</summary>
        public readonly float[] Moisture;
        /// <summary>Ocean evaporation per iteration at the steady state (index units).</summary>
        public readonly float[] Evaporation;
        /// <summary>Rain-out per iteration at the steady state (index units).</summary>
        public readonly float[] RawPrecipitation;
        /// <summary>Precipitation index: rain-out / its planetary area mean (1 = average).</summary>
        public readonly float[] Precipitation;
        public float RawPrecipitationMean { get; internal set; }

        public ClimateDiagnostics Diagnostics;

        /// <summary>Area-weighted statistics (temperature, freezing fraction, precipitation by surface).</summary>
        internal void ComputeStatistics()
        {
            double area = 0, land = 0, ocean = 0, t = 0, tLand = 0, tOcean = 0, frozen = 0, pLand = 0, pOcean = 0;
            float min = float.MaxValue, max = float.MinValue;
            for (int c = 0; c < Grid.CellCount; c++)
            {
                double a = Grid.GetArea(c);
                float temperature = Temperature[c];
                area += a;
                t += temperature * a;
                min = Mathf.Min(min, temperature);
                max = Mathf.Max(max, temperature);
                frozen += temperature < 0f ? a : 0.0;
                if (IsOcean[c])
                {
                    ocean += a;
                    tOcean += temperature * a;
                    pOcean += Precipitation[c] * a;
                }
                else
                {
                    land += a;
                    tLand += temperature * a;
                    pLand += Precipitation[c] * a;
                }
            }
            Diagnostics.MinTemperature = min;
            Diagnostics.MaxTemperature = max;
            Diagnostics.MeanTemperature = (float)(t / area);
            Diagnostics.LandMeanTemperature = land > 0 ? (float)(tLand / land) : float.NaN;
            Diagnostics.OceanMeanTemperature = ocean > 0 ? (float)(tOcean / ocean) : float.NaN;
            Diagnostics.FrozenFraction = (float)(frozen / area);
            Diagnostics.LandFraction = (float)(land / area);
            Diagnostics.LandMeanPrecipitation = land > 0 ? (float)(pLand / land) : 0f;
            Diagnostics.OceanMeanPrecipitation = ocean > 0 ? (float)(pOcean / ocean) : 0f;
        }

        /// <summary>
        /// Smooth sample of a cell field at a direction (cell and neighbours, weights (1 - d^2)^2 over
        /// 1.5 cell sizes, smooth at cell boundaries): the climate grid is coarser than the terrain mesh.
        /// Display only; the fields themselves are not smoothed.
        /// </summary>
        public float Sample(float[] field, Vector3 direction)
        {
            int cell = Grid.CellFromDirection(direction);
            float radius = 1.5f * Mathf.PI * 0.5f / Grid.Resolution;
            float sum = 0f, weights = 0f;
            Accumulate(field, cell, direction, radius, ref sum, ref weights);
            for (int k = Grid.GetNeighborStart(cell); k < Grid.GetNeighborEnd(cell); k++)
            {
                Accumulate(field, Grid.GetNeighbor(k), direction, radius, ref sum, ref weights);
            }
            return weights > 0f ? sum / weights : field[cell];
        }

        void Accumulate(float[] field, int cell, Vector3 direction, float radius, ref float sum, ref float weights)
        {
            float distance = (Grid.GetCenter(cell) - direction).magnitude / radius;
            float w = Mathf.Max(0f, 1f - distance * distance);
            w *= w;
            sum += field[cell] * w;
            weights += w;
        }

        /// <summary>Smooth sample of the wind field (tangent at the direction).</summary>
        public Vector3 SampleWind(Vector3 direction)
        {
            int cell = Grid.CellFromDirection(direction);
            Vector3 sum = Wind[cell] * 1e-6f;
            float radius = 1.5f * Mathf.PI * 0.5f / Grid.Resolution;
            for (int k = Grid.GetNeighborStart(cell) - 1; k < Grid.GetNeighborEnd(cell); k++)
            {
                int other = k < Grid.GetNeighborStart(cell) ? cell : Grid.GetNeighbor(k);
                float distance = (Grid.GetCenter(other) - direction).magnitude / radius;
                sum += Wind[other] * Mathf.Max(0f, 1f - distance * distance);
            }
            return sum - direction * Vector3.Dot(sum, direction);
        }
    }

    /// <summary>Measured climate diagnostics (area-weighted statistics, transport balance, timings).</summary>
    public struct ClimateDiagnostics
    {
        public float MinTemperature, MaxTemperature, MeanTemperature, LandMeanTemperature, OceanMeanTemperature;
        /// <summary>Area fraction with an annual-mean temperature below 0 C.</summary>
        public float FrozenFraction;
        public float LandFraction;
        /// <summary>Area-weighted mean precipitation index over land / over the ocean.</summary>
        public float LandMeanPrecipitation, OceanMeanPrecipitation;
        /// <summary>Last iteration: moisture added by the ocean, removed by precipitation, net storage
        /// change and remaining atmospheric moisture (area-weighted amounts, steradian x index).</summary>
        public double Evaporation, Precipitation, StorageChange, AtmosphericMoisture;
        /// <summary>Last iteration: land rain returned to the air (evapotranspiration recycling).</summary>
        public double Recycled;
        /// <summary>Largest relative mass-balance error of an iteration (transport and sources / sinks).</summary>
        public double MassBalanceError;
        public int Iterations;
        public bool Converged;
        public float TemperatureMilliseconds, MoistureMilliseconds, PrecipitationMilliseconds, TotalMilliseconds;
    }

    /// <summary>
    /// Climate cache: recomputes only when the terrain (<see cref="PlanetDefinition.TerrainEquals"/>, so
    /// seed, terrain parameters and sea level) or the climate settings change. Mesh resolution, views,
    /// rivers and lakes never trigger a recomputation.
    /// </summary>
    public sealed class ClimateCache
    {
        readonly ClimateGenerator generator = new ClimateGenerator();
        readonly Dictionary<int, HydrologyGrid> grids = new Dictionary<int, HydrologyGrid>();
        PlanetDefinition cachedTerrain;
        ClimateSettings cachedSettings;
        ClimateResult cached;

        public int ComputeCount { get; private set; }

        public ClimateResult Current => cached;

        public bool IsCurrent(PlanetInstance planet, ClimateSettings settings)
        {
            settings.Sanitize();
            return cached != null && cachedTerrain.TerrainEquals(planet.Definition) && cachedSettings.ClimateEquals(settings);
        }

        public ClimateResult Get(PlanetInstance planet, ClimateSettings settings)
        {
            if (IsCurrent(planet, settings))
            {
                return cached;
            }
            if (!grids.TryGetValue(settings.resolution, out HydrologyGrid grid))
            {
                grid = new HydrologyGrid(settings.resolution);
                grids.Add(settings.resolution, grid);
            }
            cached = generator.Compute(planet, settings, grid);
            cachedTerrain = planet.Definition.Clone();
            cachedSettings = settings.Clone();
            ComputeCount++;
            return cached;
        }

        public void Clear()
        {
            grids.Clear();
            cached = null;
            cachedTerrain = null;
            cachedSettings = null;
        }
    }
}
