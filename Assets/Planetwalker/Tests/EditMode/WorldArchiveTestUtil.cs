using System;
using System.IO;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Persistence;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>Shared fixtures of the world archive tests (V0.1.3A).</summary>
    static class WorldArchiveTestUtil
    {
        public static readonly int[] ReferenceSeeds = { 1337, 536452, 868497, 609538 };

        /// <summary>A world archive exactly as written by V0.1.3A (no metadata member): compatibility fixture.</summary>
        public const string V013AArchiveJson = @"{
  ""archiveSchemaVersion"": 1,
  ""generatorVersion"": ""Genesis V0.1.3A"",
  ""planetId"": ""3f2a1c9e-5b7d-4e21-9c0a-8d6f4b2e1a07"",
  ""displayName"": ""Ærendel ✦"",
  ""createdUtc"": ""2026-10-16T09:22:07.315Z"",
  ""modifiedUtc"": ""2026-10-16T09:42:07.315Z"",
  ""planetSeed"": 868497,
  ""weatherSeed"": 552901,
  ""terrain"": {
    ""radius"": 100,
    ""resolution"": 64,
    ""continentFrequency"": 1.1,
    ""continentStrength"": 0.025,
    ""mountainFrequency"": 5,
    ""mountainStrength"": 0.045,
    ""detailFrequency"": 24,
    ""detailStrength"": 0.003,
    ""seaLevel"": 0.0015,
    ""continentalWarpStrength"": 0.2,
    ""continentalWarpFrequency"": 1.1,
    ""coastalComplexity"": 0.7,
    ""mountainRangeCoverage"": 0.65,
    ""mountainRangeLength"": 1,
    ""mountainRuggedness"": 0.5
  },
  ""hydrology"": {
    ""resolution"": 128,
    ""riverNetworkThreshold"": 0.0007
  },
  ""rivers"": {
    ""showRivers"": false,
    ""smoothing"": 0.67,
    ""widthMultiplier"": 1,
    ""referenceWidth"": 0.0012,
    ""referenceArea"": 0.0005,
    ""widthExponent"": 0.5,
    ""minWidth"": 0.0005,
    ""maxWidth"": 0.006,
    ""minPixelHalfWidth"": 0.75,
    ""minDisplayLength"": 0.08,
    ""sourceTaperLength"": 0.03,
    ""color"": {
      ""r"": 0.16,
      ""g"": 0.36,
      ""b"": 0.62,
      ""a"": 1
    }
  },
  ""lakes"": {
    ""showLakes"": false,
    ""abundance"": 0.5,
    ""minimumArea"": 4E-05,
    ""fillingFactor"": 0.7,
    ""color"": {
      ""r"": 0.04,
      ""g"": 0.17,
      ""b"": 0.27,
      ""a"": 1
    }
  },
  ""climate"": {
    ""resolution"": 64,
    ""rotationAxis"": {
      ""x"": 0,
      ""y"": 1,
      ""z"": 0
    },
    ""axialTilt"": 23.5,
    ""elevationScale"": 1,
    ""lapseRate"": 6.5,
    ""planetRadiusKm"": 6371,
    ""equatorTemperature"": 27,
    ""poleTemperature"": -25,
    ""continentality"": 0.15,
    ""oceanInfluenceDistance"": 700,
    ""temperatureVariation"": 0.75,
    ""windPerturbation"": 20,
    ""landRecycling"": 0.7,
    ""stormTracks"": 2.5,
    ""rainLength"": 2500,
    ""evaporationLength"": 500,
    ""humidityRainExponent"": 1
  },
  ""biomes"": {
    ""aridity"": 1,
    ""ecotoneSharpness"": 1,
    ""alpineSensitivity"": 1
  },
  ""surface"": {
    ""enabled"": true,
    ""biomeBlend"": 0.5,
    ""regionalVariation"": 0.5,
    ""rockExposure"": 0.5,
    ""saturation"": 0.9
  },
  ""oceanAtmosphere"": {
    ""oceanShading"": true,
    ""depthContrast"": 0.5,
    ""shelfVisibility"": 0.5,
    ""oceanRoughness"": 0.35,
    ""specularIntensity"": 0.35,
    ""atmosphere"": true,
    ""atmosphereStrength"": 1.2,
    ""atmosphereThickness"": 0.025,
    ""terminatorSoftness"": 0.2
  },
  ""clouds"": {
    ""showClouds"": true,
    ""coverage"": 0.52,
    ""density"": 0.5,
    ""detail"": 0.5,
    ""weatherStrength"": 0.5,
    ""convection"": 0.5,
    ""shadowStrength"": 0.25,
    ""altitude"": 0.006,
    ""motion"": false
  },
  ""lighting"": {
    ""twilightWarmth"": 0.5,
    ""nightAmbient"": 0.04
  },
  ""presentation"": {
    ""camera"": {
      ""yaw"": -37.5,
      ""pitch"": 22,
      ""distance"": 268.4
    },
    ""solar"": {
      ""solarTime"": 0.7375,
      ""dayNightCycle"": false,
      ""paused"": false,
      ""cycleDuration"": 240
    }
  }
}
";

        public static readonly System.Guid V013AArchiveId = new System.Guid("3f2a1c9e-5b7d-4e21-9c0a-8d6f4b2e1a07");

        /// <summary>
        /// A fresh, isolated folder under the system temporary directory (never the user's archive folder).
        /// </summary>
        public static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "PlanetwalkerArchiveTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void DeleteTempDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.Contains("PlanetwalkerArchiveTests") && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        /// <summary>
        /// Every archived field set to a valid non-default value (flags flipped when <paramref name="flipFlags"/>,
        /// otherwise kept as the Natural Planet needs them), so a round trip checks every field.
        /// </summary>
        public static WorldConfiguration Tweaked(int seed = 536452, bool flipFlags = true)
        {
            var c = new WorldConfiguration();
            PlanetDefinition t = c.Terrain;
            t.seed = seed;
            t.radius = 120f;
            t.resolution = 32;
            t.continentFrequency = 1.3f;
            t.continentStrength = 0.03f;
            t.mountainFrequency = 6f;
            t.mountainStrength = 0.05f;
            t.detailFrequency = 30f;
            t.detailStrength = 0.004f;
            t.seaLevel = 0.002f;
            t.continentalWarpStrength = 0.25f;
            t.continentalWarpFrequency = 1.5f;
            t.coastalComplexity = 0.6f;
            t.mountainRangeCoverage = 0.7f;
            t.mountainRangeLength = 1.2f;
            t.mountainRuggedness = 0.4f;

            c.Hydrology.resolution = HydrologySettings.PreviewResolution;
            c.Hydrology.riverNetworkThreshold = 1e-3f;

            RiverSettings r = c.Rivers;
            r.showRivers = flipFlags;
            r.smoothing = 0.33f;
            r.widthMultiplier = 1.5f;
            r.referenceWidth = 0.0015f;
            r.referenceArea = 6e-4f;
            r.widthExponent = 0.6f;
            r.minWidth = 0.0004f;
            r.maxWidth = 0.007f;
            r.minPixelHalfWidth = 1f;
            r.minDisplayLength = 0.1f;
            r.sourceTaperLength = 0.04f;
            r.color = new Color(0.2f, 0.4f, 0.6f, 0.9f);

            LakeSettings l = c.Lakes;
            l.showLakes = flipFlags;
            l.abundance = 0.6f;
            l.minimumArea = 5e-5f;
            l.fillingFactor = 0.8f;
            l.color = new Color(0.05f, 0.2f, 0.3f, 0.95f);

            ClimateSettings k = c.Climate;
            k.resolution = ClimateSettings.LowResolution;
            k.rotationAxis = new Vector3(0f, 1f, 0.05f);
            k.axialTilt = 30f;
            k.elevationScale = 1.2f;
            k.lapseRate = 6f;
            k.planetRadiusKm = 6000f;
            k.equatorTemperature = 28f;
            k.poleTemperature = -24f;
            k.continentality = 0.2f;
            k.oceanInfluenceDistance = 800f;
            k.temperatureVariation = 1f;
            k.windPerturbation = 25f;
            k.landRecycling = 0.6f;
            k.stormTracks = 2f;
            k.rainLength = 2400f;
            k.evaporationLength = 600f;
            k.humidityRainExponent = 1.2f;

            c.Biomes.aridity = 1.2f;
            c.Biomes.ecotoneSharpness = 0.8f;
            c.Biomes.alpineSensitivity = 1.1f;

            NaturalSurfaceSettings s = c.Surface;
            s.enabled = !flipFlags;
            s.biomeBlend = 0.6f;
            s.regionalVariation = 0.4f;
            s.rockExposure = 0.55f;
            s.saturation = 1f;

            OceanAtmosphereSettings o = c.OceanAtmosphere;
            o.oceanShading = !flipFlags;
            o.depthContrast = 0.6f;
            o.shelfVisibility = 0.4f;
            o.oceanRoughness = 0.3f;
            o.specularIntensity = 0.4f;
            o.atmosphere = !flipFlags;
            o.atmosphereStrength = 1.2f;
            o.atmosphereThickness = 0.03f;
            o.terminatorSoftness = 0.3f;

            CloudSettings w = c.Clouds;
            w.showClouds = !flipFlags;
            w.coverage = 0.55f;
            w.density = 0.6f;
            w.detail = 0.4f;
            w.weatherStrength = 0.7f;
            w.convection = 0.3f;
            w.shadowStrength = 0.3f;
            w.altitude = 0.008f;
            w.motion = true;
            w.weatherSeed = 4242;

            c.Lighting.twilightWarmth = 0.7f;
            c.Lighting.nightAmbient = 0.06f;
            return c;
        }

        public static PlanetArchive NewArchive(string name, WorldConfiguration configuration = null)
        {
            DateTime now = PlanetArchiveSerializer.UtcNowTruncated();
            return new PlanetArchive
            {
                PlanetId = Guid.NewGuid(),
                DisplayName = name,
                CreatedUtc = now,
                ModifiedUtc = now,
                Configuration = configuration ?? Tweaked(),
                Presentation = new PresentationState
                {
                    Camera = new CameraPresentation { yaw = 37.5f, pitch = -22f, distance = 260f },
                    Solar = new SolarPresentation { solarTime = 0.83f, dayNightCycle = true, paused = true, cycleDuration = 600f },
                },
            };
        }

        /// <summary>Test report line: NUnit progress output and the Unity Console (when the engine is running).</summary>
        public static void Report(string line)
        {
            TestContext.Progress.WriteLine(line);
            try
            {
                Debug.Log("[World Archive] " + line);
            }
            catch (Exception)
            {
                // Outside the Unity runtime (offline runner): the progress output is enough.
            }
        }

        public static void AssertSameConfiguration(WorldConfiguration expected, WorldConfiguration actual, string message = null)
        {
            Assert.IsTrue(expected.ContentEquals(actual), (message ?? "Configuration differs") + ": "
                + string.Join(", ", expected.Differences(actual)));
        }
    }
}
