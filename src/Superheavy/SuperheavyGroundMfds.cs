using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundMfds : MonoBehaviour
    {
        private const string Prefix = "SHGT_MFD_";
        private const string GearLampPrefix = "SHGT_GearLamp_";
        private const float CameraRefreshInterval = 1f / 60f;
        private const float PageRefreshInterval = 1f / 48f;
        private static readonly string[] InstrumentPages = { "PFD", "NAV", "SYS", "ESM", "CARGO", "AUX" };
        private static readonly Vector3 TaxiCameraPosition = new Vector3(0f, 3.06f, 29.08f);
        private static readonly Vector3 TaxiCameraDirection = new Vector3(0f, -.45f, 1f);
        private static readonly Vector3 RearTargetCameraPosition = new Vector3(0f, 3.8f, -31.6f);
        private static readonly int BaseMapScaleOffset = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly AccessTools.FieldRef<LandingGear, float> GearFold =
            AccessTools.FieldRefAccess<LandingGear, float>("foldAmount");
        private static readonly AccessTools.FieldRef<Turbofan, string> EngineFailureMessage =
            AccessTools.FieldRefAccess<Turbofan, string>("failureMessage");
        private static readonly AccessTools.FieldRef<TargetCam, Camera> TargetCamera =
            AccessTools.FieldRefAccess<TargetCam, Camera>("cam");
        private static readonly AccessTools.FieldRef<TargetCam, TargetCam.CamMode> TargetMode =
            AccessTools.FieldRefAccess<TargetCam, TargetCam.CamMode>("currentMode");
        private static readonly AccessTools.FieldRef<TargetCam, Transform> ForwardTargetMount =
            AccessTools.FieldRefAccess<TargetCam, Transform>("camMountForward");
        private static readonly AccessTools.FieldRef<TargetCam, Transform> RearTargetMount =
            AccessTools.FieldRefAccess<TargetCam, Transform>("camMountRear");
        private static readonly Color GearDark = new Color(.015f, .031f, .026f);
        private static readonly Color GearLocked = new Color(.13f, .92f, .40f);
        private static readonly Color GearTransit = new Color(1f, .54f, .09f);
        private static readonly Color GearFailed = new Color(1f, .13f, .07f);
        private static ConfigEntry<KeyboardShortcut> cameraShortcut;

        private readonly Dictionary<string, MeshRenderer> displays = new Dictionary<string, MeshRenderer>();
        private readonly Dictionary<string, SuperheavyMfdCanvas> pages = new Dictionary<string, SuperheavyMfdCanvas>();
        private readonly List<Feed> feeds = new List<Feed>();
        private Aircraft aircraft;
        private MeshRenderer interiorGlass;
        private CargoRamp ramp;
        private readonly MeshRenderer[] gearLamps = new MeshRenderer[3];
        private LandingGear[] gearContacts;
        private readonly MaterialPropertyBlock lampProperties = new MaterialPropertyBlock();
        private IEngine[] engines;
        private bool started;
        private bool gearUp;
        private bool cargoCamera;
        private SuperheavyMfdCanvas targetStandby;
        private Texture adaptOutput;
        private int standbyTargetCount = -1;
        private string standbyMessage;
        private bool powered;
        private float nextRefresh;
        private float nextGearRefresh;
        private int pageIndex;
        private float nextCameraFrame;
        private int cameraIndex;
        private float lastWarning = float.NegativeInfinity;
        private float warningBearing;
        private bool missileLock;

        private enum AdaptMode { Taxi, Target, Cargo }
        private AdaptMode adaptMode;

        private sealed class Feed
        {
            internal string Name;
            internal Camera Camera;
            internal RenderTexture Texture;
        }

        internal static void Configure(ConfigFile config)
        {
            cameraShortcut = config.Bind("Cockpit", "Taxi/Cargo Camera",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Toggle the cargo camera on ADAPT. Moving the gear returns to the automatic taxi/target display.");
        }

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.GetComponent<SuperheavyGroundMfds>() != null)
                return;

            var screens = SuperheavyNativeDamage.Components<MeshRenderer>(aircraft);
            var mfds = aircraft.gameObject.AddComponent<SuperheavyGroundMfds>();
            mfds.aircraft = aircraft;
            foreach (var screen in screens)
            {
                if (screen.name.StartsWith(Prefix, StringComparison.Ordinal))
                    mfds.displays.Add(screen.name.Substring(Prefix.Length), screen);
                if (screen.name.StartsWith(GearLampPrefix, StringComparison.Ordinal) &&
                    int.TryParse(screen.name.Substring(GearLampPrefix.Length), out int lamp) && lamp >= 1 && lamp <= 3)
                    mfds.gearLamps[lamp - 1] = screen;
                if (screen.name == "SHGT_GlazingInterior")
                    mfds.interiorGlass = screen;
            }
            mfds.engines = SuperheavyNativeDamage.Components<Turbofan>(aircraft)
                .OrderBy(engine => EngineFailureMessage(engine), StringComparer.Ordinal).Cast<IEngine>().ToArray();
            mfds.ramp = SuperheavyNativeDamage.Components<CargoRamp>(aircraft)
                .FirstOrDefault(candidate => candidate.name == "SHGT_CargoReleaseGate");
            var landingGear = SuperheavyNativeDamage.Components<LandingGear>(aircraft);
            // The authored lenses are marked L, N and R, from left to right.
            mfds.gearContacts = new[] { "gear_L", "gear_F", "gear_R" }
                .Select(name => landingGear.FirstOrDefault(gear => gear.name == name)).ToArray();
            aircraft.onRadarWarning += mfds.OnRadarWarning;
            log.LogInfo($"Superheavy MFDs bound {mfds.displays.Count} display groups, " +
                $"{SuperheavyMfdCanvas.TextureWidth}x{SuperheavyMfdCanvas.TextureHeight} instrument pages, {mfds.engines.Length} engine banks, " +
                $"{mfds.gearLamps.Count(lamp => lamp != null)}/3 gear lights and {mfds.gearContacts.Count(gear => gear != null)}/3 contacts.");
        }

        private void OnRadarWarning(Aircraft.OnRadarWarning warning)
        {
            if (warning.emitter == null)
                return;
            var relative = warning.emitter.transform.position - transform.position;
            warningBearing = Mathf.Atan2(relative.x, relative.z) * Mathf.Rad2Deg;
            lastWarning = Time.time;
            missileLock = warning.isTarget;
        }

        private void LateUpdate()
        {
            foreach (var feed in feeds)
                feed.Camera.enabled = false;

            if (aircraft == null || aircraft.disabled || aircraft.rb == null ||
                !GameManager.GetLocalAircraft(out var local) || local != aircraft)
                return;

            if (!started)
                StartDisplays();

            bool hasPower = aircraft.Ignition || (aircraft.GetPowerSupply()?.GetChargeKJ() ?? 0f) > 1f;
            if (hasPower != powered)
            {
                powered = hasPower;
                adaptOutput = null;
                foreach (var pair in displays)
                {
                    Texture output = pair.Key == "ADAPT" ? null :
                        pages.TryGetValue(pair.Key, out var page) ? page.Texture : FeedTexture(pair.Key);
                    Show(pair.Value, powered ? output : null);
                }
                UpdateGearLamps();
            }
            if (!powered)
                return;

            float now = Time.unscaledTime;
            if (now >= nextGearRefresh)
            {
                nextGearRefresh = now + .12f;
                UpdateGearLamps();
            }
            if (interiorGlass == null || !interiorGlass.enabled)
                return;
            if (now >= nextRefresh)
            {
                // Upload one instrument per frame to distribute the higher-resolution work.
                nextRefresh = Mathf.Max(now, nextRefresh + PageRefreshInterval);
                DrawPage(InstrumentPages[pageIndex]);
                pageIndex = (pageIndex + 1) % InstrumentPages.Length;
            }
            UpdateAdapt();
            if (feeds.Count != 0 && now >= nextCameraFrame)
            {
                // Stagger the views without rounding their cadence down to
                // every other rendered frame at common frame rates.
                nextCameraFrame = Mathf.Max(now, nextCameraFrame + CameraRefreshInterval);
                for (int i = 0; i < feeds.Count; i++)
                {
                    var feed = feeds[cameraIndex];
                    cameraIndex = (cameraIndex + 1) % feeds.Count;
                    if ((feed.Name == "ADAPT" && adaptMode != AdaptMode.Taxi) ||
                        (feed.Name == "CargoCamera" && adaptMode != AdaptMode.Cargo && !displays.ContainsKey("CargoCamera")))
                        continue;
                    feed.Camera.enabled = true;
                    break;
                }
            }
        }

        private void StartDisplays()
        {
            started = true;
            foreach (string name in InstrumentPages)
                if (displays.ContainsKey(name))
                    pages.Add(name, new SuperheavyMfdCanvas());

            if (displays.ContainsKey("ADAPT"))
            {
                targetStandby = new SuperheavyMfdCanvas();
                var target = aircraft.targetCam;
                if (target != null)
                {
                    // The stock camera still uses the donor's mounts, inside the Courier's hull.
                    var front = ForwardTargetMount(target);
                    var rear = RearTargetMount(target);
                    if (front != null)
                        front.position = transform.TransformPoint(TaxiCameraPosition);
                    if (rear != null)
                        rear.position = transform.TransformPoint(RearTargetCameraPosition);
                }
            }

            // The curved ADAPT panel is nearly 6:5; the two header screens are 16:9.
            MakeFeed("ADAPT", 1152, 960, TaxiCameraPosition, TaxiCameraDirection);
            MakeFeed("CargoCamera", 1152, 960, new Vector3(0f, 7.4f, 12f), new Vector3(0f, -.12f, -1f));
            MakeFeed("PortCamera", 1024, 576, new Vector3(-5.6f, 6.6f, 18f), new Vector3(-.85f, -.38f, -.5f));
            MakeFeed("StarboardCamera", 1024, 576, new Vector3(5.6f, 6.6f, 18f), new Vector3(.85f, -.38f, -.5f));
        }

        private void MakeFeed(string name, int width, int height, Vector3 position, Vector3 direction)
        {
            if (!displays.ContainsKey(name) && (name != "CargoCamera" || !displays.ContainsKey("ADAPT")))
                return;
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = 2,
                useMipMap = true,
                autoGenerateMips = true,
                useDynamicScale = false
            };
            descriptor.msaaSamples = Mathf.Max(1, SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor));
            var target = new RenderTexture(descriptor)
            {
                name = "Superheavy " + name,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4
            };
            if (!target.Create())
            {
                target.antiAliasing = 1;
                if (!target.Create())
                {
                    Debug.LogWarning($"Superheavy could not allocate the {name} camera texture.");
                    Destroy(target);
                    return;
                }
            }
            var camera = new GameObject("SHGT_Feed_" + name).AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetParent(transform, false);
            camera.transform.localPosition = position;
            camera.transform.localRotation = Quaternion.LookRotation(direction);
            camera.nearClipPlane = .15f;
            camera.farClipPlane = 1300f;
            camera.fieldOfView = 64f;
            camera.aspect = (float)width / height;
            camera.depth = -20;
            camera.allowHDR = false;
            camera.allowMSAA = target.antiAliasing > 1;
            camera.allowDynamicResolution = false;
            // Match the native world camera's geometry and omit its UI/editor layers.
            // The default all-layers mask also exposes grass-blocker proxy meshes.
            var mainCamera = Camera.main;
            camera.cullingMask = (mainCamera != null ? mainCamera.cullingMask : ~0) &
                ~LayerMask.GetMask("UI", "HUD", "PP", "TargetCamPP", "PreviewRender",
                    "Editor SelectOnly", "GrassBlockerProxy");
            camera.targetTexture = target;
            feeds.Add(new Feed { Name = name, Camera = camera, Texture = target });
        }

        private RenderTexture FeedTexture(string display)
        {
            return feeds.FirstOrDefault(feed => feed.Name == display)?.Texture;
        }

        private void UpdateAdapt()
        {
            if (!displays.TryGetValue("ADAPT", out var display))
                return;

            var gear = aircraft.gearState;
            bool selectTarget = gear == LandingGear.GearState.Retracting ||
                gear == LandingGear.GearState.LockedRetracted ||
                (gear == LandingGear.GearState.Uninitialized && !aircraft.gearDeployed);
            if (selectTarget != gearUp)
            {
                gearUp = selectTarget;
                cargoCamera = false;
            }
            if (cameraShortcut != null && cameraShortcut.Value.IsDown() && FeedTexture("CargoCamera") != null)
                cargoCamera = !cargoCamera;

            var mode = cargoCamera ? AdaptMode.Cargo : gearUp ? AdaptMode.Target : AdaptMode.Taxi;
            string feedName = mode == AdaptMode.Cargo ? "CargoCamera" : "ADAPT";
            if (mode != adaptMode)
            {
                adaptMode = mode;
                int index = feeds.FindIndex(feed => feed.Name == feedName);
                if (mode != AdaptMode.Target && index >= 0)
                {
                    cameraIndex = index;
                    nextCameraFrame = 0f;
                }
            }

            var output = mode == AdaptMode.Target ? TargetTexture() : FeedTexture(feedName);
            if (output != adaptOutput)
            {
                adaptOutput = output;
                Show(display, output);
            }
        }

        private Texture TargetTexture()
        {
            int count = aircraft.weaponManager?.GetTargetList()?.Count ?? 0;
            var nativeTarget = aircraft.targetCam;
            var camera = nativeTarget != null ? TargetCamera(nativeTarget) : null;
            // The native render target already contains the target video and the stock
            // overlay (identification for one selection, count and boxes for several).
            if (count > 0 && camera != null && camera.enabled &&
                TargetMode(nativeTarget) != TargetCam.CamMode.landingMode && camera.targetTexture != null)
                return camera.targetTexture;

            // Never display the previous frame after the selection is cleared.
            string message = count == 0 ? "NO TARGET SELECTED" :
                camera == null ? "TARGET CAMERA UNAVAILABLE" : "WAITING FOR TARGET VIEW";
            if (count != standbyTargetCount || message != standbyMessage)
            {
                standbyTargetCount = count;
                standbyMessage = message;
                targetStandby.Begin("TARGET VIEW");
                targetStandby.Label(10, 126, $"{count} SELECTED", scale: 2);
                targetStandby.Label(10, 91, message, SuperheavyMfdCanvas.Muted);
                targetStandby.Commit();
            }
            return targetStandby.Texture;
        }

        private static void Show(MeshRenderer renderer, Texture texture)
        {
            if (renderer == null)
                return;
            var block = new MaterialPropertyBlock();
            if (texture != null)
            {
                block.SetTexture(BaseMap, texture);
                block.SetColor(BaseColor, Color.white);
                if (renderer.name == Prefix + "CargoCamera")
                {
                    // The wider lower monitor uses a centered crop of the same feed.
                    const float cargoMonitorAspect = .264f / .153f;
                    float scale = (float)texture.width / texture.height / cargoMonitorAspect;
                    block.SetVector(BaseMapScaleOffset, new Vector4(1f, scale, 0f, (1f - scale) * .5f));
                }
            }
            renderer.SetPropertyBlock(block);
        }

        private void DrawPage(string name)
        {
            var velocity = aircraft.rb.velocity;
            var angles = transform.eulerAngles;
            float heading = angles.y;
            float groundSpeed = SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb);
            float clearance = SuperheavyGroundPhysicsPlugin.SurfaceClearance(aircraft);
            bool metric = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Metric;
            string speedUnit = metric ? "KM/H" : "KT";
            string altitudeUnit = metric ? "M" : "FT";
            if (name == "PFD" && pages.TryGetValue(name, out var pfd))
            {
                pfd.Begin("PFD");
                pfd.Label(249, 178, "HDG " + Heading(heading));
                pfd.Horizon(Mathf.DeltaAngle(0f, angles.x), Mathf.DeltaAngle(0f, angles.z));
                pfd.Tape(5, "SPD", WithoutUnit(UnitConverter.SpeedReading(aircraft.speed), speedUnit), speedUnit, true);
                pfd.Tape(248, "RAD ALT", WithoutUnit(UnitConverter.AltitudeReading(clearance), altitudeUnit), altitudeUnit, false);
                pfd.Label(10, 13, "GS " + Reading(UnitConverter.SpeedReadingGround(groundSpeed)));
                pfd.Label(169, 13, "V/S " + Reading(UnitConverter.ClimbRateReading(velocity.y)));
                pfd.Commit();
            }
            if (name == "NAV" && pages.TryGetValue(name, out var nav))
            {
                nav.Begin("NAV");
                nav.Scope();
                var sensor = aircraft.radar;
                bool radarActive = sensor != null && sensor.activated && sensor.IsOperational();
                var contacts = radarActive ? sensor.detectedTargets : aircraft.EOTS?.detectedTargets;
                float scopeRange = radarActive ? Mathf.Max(1000f, sensor.GetRadarRange()) : 10000f;
                if (contacts != null)
                {
                    foreach (var contact in contacts)
                    {
                        if (contact == null || contact == aircraft)
                            continue;
                        Vector3 delta = contact.transform.position - transform.position;
                        float distance = new Vector2(delta.x, delta.z).magnitude;
                        if (distance > scopeRange)
                            continue;
                        float course = (Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg - heading) * Mathf.Deg2Rad;
                        nav.Plot(course, distance / scopeRange);
                    }
                }
                nav.Label(10, 143, "HDG");
                nav.Label(10, 124, Heading(heading), scale: 2);
                nav.Label(224, 144, radarActive ? "RADAR" : aircraft.EOTS != null ? "OPTICAL" : "NO SENSOR");
                nav.Label(224, 124, Reading(UnitConverter.DistanceReading(scopeRange)), SuperheavyMfdCanvas.Muted);
                nav.Label(10, 13, "GS " + Reading(UnitConverter.SpeedReadingGround(groundSpeed)));
                nav.Commit();
            }
            if (name == "SYS" && pages.TryGetValue(name, out var sys))
            {
                sys.Begin("ENGINE / ELEC");
                for (int i = 0; i < Math.Min(engines.Length, 8); i++)
                {
                    // Four rows per side keep all eight engines readable without
                    // covering the fuel, capacitor and flight-control readouts.
                    int row = 143 - (i % 4) * 15;
                    int x = i < 4 ? 10 : 160;
                    sys.Label(x, row, "E" + (i + 1).ToString("00"));
                    sys.Bar(x + 32, row, 65, engines[i].GetRPMRatio());
                    sys.Label(x + 103, row, Readout(engines[i].GetRPMRatio() * 100f));
                }
                sys.Label(10, 78, "FUEL " + Reading(UnitConverter.WeightReading(aircraft.GetFuelQuantity())));
                if (aircraft.TryGetComponent<SuperheavyGroundApu>(out var apu))
                    sys.Label(203, 78, apu.Running ? "APU ON" : "APU OFF",
                        apu.Running ? SuperheavyMfdCanvas.Ink : SuperheavyMfdCanvas.Muted);
                var power = aircraft.GetPowerSupply();
                sys.Label(10, 55, "CAP " + Readout(power?.GetChargeKJ() ?? 0f) + " KJ");
                sys.Bar(157, 55, 126, power?.GetCharge() ?? 0f);
                if (aircraft.TryGetComponent<SuperheavyGroundFlightControls>(out var flight))
                {
                    sys.Label(10, 34, flight.ReverseReadout,
                        flight.ReverseSelected ? SuperheavyMfdCanvas.Amber : SuperheavyMfdCanvas.Ink);
                    sys.Label(155, 34, flight.VectoringAvailable ? "TVC P / Y / R" : "TVC LIMITED",
                        flight.VectoringAvailable ? SuperheavyMfdCanvas.Ink : SuperheavyMfdCanvas.Muted);
                }
                sys.Label(10, 13, "THR " + Readout(aircraft.GetInputs().throttle * 100f) + "%");
                sys.Label(155, 13, "GEAR " + GearLabel());
                sys.Commit();
            }
            if (name == "ESM" && pages.TryGetValue(name, out var esm))
            {
                esm.Begin("DEFENCE");
                esm.Scope();
                bool recent = Time.time - lastWarning < 8f;
                if (recent)
                    esm.Plot((warningBearing - heading) * Mathf.Deg2Rad, .78f,
                        missileLock ? SuperheavyMfdCanvas.Amber : SuperheavyMfdCanvas.Ink);
                int missiles = aircraft.GetMissileWarningSystem()?.knownMissiles.Count ?? 0;
                esm.Label(10, 145, "RWR");
                esm.Label(10, 125, recent ? (missileLock ? "LOCK" : "PING") : "CLEAR",
                    missileLock && recent ? SuperheavyMfdCanvas.Amber : SuperheavyMfdCanvas.Ink);
                esm.Label(208, 145, "MISSILES");
                esm.Label(221, 125, missiles.ToString(CultureInfo.InvariantCulture), scale: 2);
                esm.Label(10, 15, "CAP " + Readout(aircraft.GetPowerSupply()?.GetChargeKJ() ?? 0f) + " KJ");
                esm.Commit();
            }
            if (name == "CARGO" && pages.TryGetValue(name, out var cargo))
            {
                cargo.Begin("CARGO");
                int loads = aircraft.weaponStations.Where(station => station != null && station.Cargo).Sum(station => station.Ammo);
                cargo.Label(10, 146, "LOADS ON BOARD");
                cargo.Label(10, 112, loads.ToString(CultureInfo.InvariantCulture), scale: 3);
                cargo.Label(10, 86, "RAMP " + (ramp != null && ramp.IsOpen() ? "OPEN" : "NOT OPEN"));
                cargo.Label(10, 61, "RAD ALT " + Reading(UnitConverter.AltitudeReading(clearance)));
                cargo.Label(10, 15, aircraft.weaponManager?.currentWeaponStation?.Cargo == true ?
                    "CARGO SELECTED" : "SELECT CARGO STATION", SuperheavyMfdCanvas.Muted);
                cargo.Commit();
            }
            if (name == "AUX" && pages.TryGetValue(name, out var aux))
            {
                aux.Begin("FLIGHT REPEATER");
                aux.Label(12, 128, "HDG " + Heading(heading), scale: 2);
                aux.Label(156, 128, "GS " + Reading(UnitConverter.SpeedReadingGround(groundSpeed)));
                aux.Label(12, 82, "RAD ALT " + Reading(UnitConverter.AltitudeReading(clearance)), scale: 2);
                aux.Label(12, 18, "GEAR " + GearLabel());
                aux.Commit();
            }
        }

        private string GearLabel()
        {
            if (gearContacts.Any(gear => gear == null))
                return "CHECK";
            if (aircraft.gearState == LandingGear.GearState.LockedRetracted)
                return "UP";
            if (aircraft.gearState == LandingGear.GearState.LockedExtended)
                return gearContacts.Any(gear => gear.enabled && gear.WeightOnWheel(.01f)) ? "ON GROUND" : "DOWN";
            return "TRANSIT";
        }

        private void UpdateGearLamps()
        {
            for (int i = 0; i < gearLamps.Length; i++)
            {
                var lamp = gearLamps[i];
                if (lamp == null)
                    continue;
                var gear = gearContacts[i];
                Color color = GearDark;
                if (powered)
                {
                    if (gear == null)
                        color = GearFailed;
                    else if (aircraft.gearState == LandingGear.GearState.Extending ||
                        aircraft.gearState == LandingGear.GearState.Retracting)
                        color = GearTransit;
                    else if (aircraft.gearState == LandingGear.GearState.LockedExtended)
                        color = gear.enabled && GearFold(gear) <= .01f ? GearLocked : GearFailed;
                }
                lampProperties.SetColor(BaseColor, color);
                lamp.SetPropertyBlock(lampProperties);
            }
        }

        private static string Readout(float value, string format = "0")
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "---" :
                value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string Heading(float degrees) => Readout(Mathf.RoundToInt(degrees) % 360, "000");

        private static string Reading(string nativeReading) => nativeReading.ToUpperInvariant();

        private static string WithoutUnit(string nativeReading, string unit) =>
            nativeReading.Substring(0, nativeReading.Length - unit.Length).Trim();

        private void OnDestroy()
        {
            if (aircraft != null)
                aircraft.onRadarWarning -= OnRadarWarning;
            foreach (var page in pages.Values)
                Destroy(page.Texture);
            if (targetStandby != null)
                Destroy(targetStandby.Texture);
            foreach (var feed in feeds)
            {
                if (feed.Camera != null)
                    feed.Camera.targetTexture = null;
                feed.Texture.Release();
                Destroy(feed.Texture);
            }
        }
    }
}
