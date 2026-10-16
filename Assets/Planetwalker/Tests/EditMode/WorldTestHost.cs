using System;
using System.Collections.Generic;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>A minimal application: definition, visual controller, star, orbit camera, counted regenerations.</summary>
    sealed class WorldTestHost : IWorldHost
    {
        public readonly GameObject Root;
        public readonly PlanetOrbitCamera Camera;
        public WorldSession Session;
        public PlanetInstance Current;
        public int Regenerations;
        public int AppliedGroupsAtRegeneration = -1;
        public bool FailNextRegeneration;
        public double Now = 1000.0;

        public WorldTestHost(List<Object> created)
        {
            var star = new GameObject("Archive Test Star");
            created.Add(star);
            star.transform.rotation = Quaternion.Euler(20f, 50f, 0f);
            var light = star.AddComponent<Light>();
            light.type = LightType.Directional;
            Root = new GameObject("Archive Test Planet");
            created.Add(Root);
            Visual = Root.AddComponent<PlanetVisualController>();
            Visual.SolarRealTime = () => Now;
            Visual.SetSun(light);
            var cameraObject = new GameObject("Archive Test Camera");
            created.Add(cameraObject);
            Camera = cameraObject.AddComponent<PlanetOrbitCamera>();
            Camera.Target = Root.transform;
            Definition.resolution = 16;
            Visual.Climate.resolution = ClimateSettings.LowResolution;
            Visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
        }

        public PlanetDefinition Definition { get; } = new PlanetDefinition();
        public PlanetVisualController Visual { get; }

        public void Regenerate()
        {
            if (FailNextRegeneration)
            {
                FailNextRegeneration = false;
                throw new InvalidOperationException("Simulated generation failure");
            }
            Regenerations++;
            AppliedGroupsAtRegeneration = Session != null ? Session.LastApplyOrder.Count : -1;
            Definition.Sanitize();
            Current = new PlanetInstance(Definition);
            Visual.Render(Current);
            Camera.FramePlanet(Visual.BoundingRadius, Regenerations == 1);
        }

        public CameraPresentation CaptureCamera()
        {
            return new CameraPresentation
            {
                yaw = Mathf.Repeat(Camera.TargetYaw + 180f, 360f) - 180f,
                pitch = Camera.TargetPitch,
                distance = Camera.TargetDistance,
            };
        }

        public void RestoreCamera(CameraPresentation camera)
        {
            Camera.SetView(camera.yaw, camera.pitch, camera.distance);
        }

        public void Apply(WorldConfiguration c)
        {
            SettingsFields.Copy(c.Terrain, Definition);
            SettingsFields.Copy(c.Hydrology, Visual.Hydrology);
            SettingsFields.Copy(c.Rivers, Visual.Rivers);
            SettingsFields.Copy(c.Lakes, Visual.Lakes);
            SettingsFields.Copy(c.Climate, Visual.Climate);
            SettingsFields.Copy(c.Biomes, Visual.Biomes);
            SettingsFields.Copy(c.Surface, Visual.Surface);
            SettingsFields.Copy(c.OceanAtmosphere, Visual.OceanAtmosphere);
            SettingsFields.Copy(c.Clouds, Visual.Clouds);
            c.Lighting.ApplyTo(Visual.Solar);
        }
    }
}
