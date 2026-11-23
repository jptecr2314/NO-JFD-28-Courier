using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
#if COURIER_ONLY
    [BepInPlugin(PluginId, "JFD-28 Courier Flight Systems", "0.44.0")]
#else
    [BepInPlugin(PluginId, "JFD-28 Courier / BG-280 Bastion Flight Systems", "0.43.0")]
#endif
    public sealed class SuperheavyGroundPhysicsPlugin : BaseUnityPlugin
    {
        private const string PluginId = "br.japensou.nuclearoption.superheavy.groundphysics";
        internal const string AircraftKey = "jp_superheavy_ground_test";
#if COURIER_ONLY
        internal static bool IsFamily(AircraftDefinition definition)=>definition!=null&&definition.jsonKey==AircraftKey;
#else
        internal static bool IsFamily(AircraftDefinition definition) => definition != null &&
            (definition.jsonKey == AircraftKey || definition.jsonKey == "jp_bg280_bomber" ||
             definition.jsonKey == "jp_bg280_gunship");
#endif
        private static ManualLogSource log;

        private void Awake()
        {
            log = Logger;
            SuperheavyGroundMfds.Configure(Config);
            SuperheavyCrewAccess.Configure(Config, log);
            var harmony = new Harmony(PluginId);
            // Preserve Blueprinter's native prepared-cache ownership and lifetime.
            harmony.CreateClassProcessor(typeof(SingleBodyPatch)).Patch();
            harmony.CreateClassProcessor(typeof(DryThrustPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PartMassPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GroundHatchesPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GroundGearInterlock)).Patch();
            harmony.CreateClassProcessor(typeof(NativeAirborneAltitudePatch)).Patch();
            harmony.CreateClassProcessor(typeof(NativeSpawnAltitudePatch)).Patch();
            harmony.CreateClassProcessor(typeof(HudRadarAltitudePatch)).Patch();
            SuperheavyGearPhysics.Register(harmony);
            SuperheavyFlightDynamics.Register(harmony);
            SuperheavyGroundFlightControls.Register(harmony);
            SuperheavyGroundEjection.Install(harmony);
            SuperheavyGroundLiveries.Install(harmony, log);
            SuperheavyGroundCargoMotion.Install(harmony, log);
            SuperheavyGroundBombBay.Install(harmony, log);
#if !COURIER_ONLY
            SuperheavyBomberFeed.Install(harmony, log);
#endif
            SuperheavySpatialCargoRuntime.Install(harmony, log, Config);
#if !COURIER_ONLY
            SuperheavySpatialWeaponsRuntime.Install(harmony, log, Config);
            SuperheavySpatialWeaponFeed.Install(harmony, log);
            SuperheavyWeaponsOrganizerMenu.Install(harmony);
#endif
            SuperheavyCargoOrganizerMenu.Install(harmony);
            SuperheavyGroundCargo.Install(harmony, log, Config);
            SuperheavyAirdrop.Install(harmony, log);
            SuperheavyGroundDefense.Install(harmony, log);
            SuperheavyNativeDamage.Install(harmony);
            SuperheavyExternalCamera.Install(harmony, log);
#if !COURIER_ONLY
            SuperheavyFamilyMenu.Install(harmony);
            SuperheavyFamilyCompatibility.Install(gameObject, harmony, log);
            SuperheavyNavalTurretAdapter.Install(harmony, log);
            SuperheavyRetractableBattery.Install(harmony, log);
#endif
#if !BAKED_AIRFRAMES
            SuperheavyBayArmor.Install(harmony, log);
#endif
#if !COURIER_ONLY
            SuperheavyGunship.Install(harmony, log);
#endif
        }


        private static bool IsLocalPrototype(Aircraft aircraft)
        {
            return IsPrototype(aircraft) && aircraft.networked && aircraft.LocalSim;
        }

        private static bool IsPrototype(Aircraft aircraft)
        {
            return GameManager.gameState == GameState.SinglePlayer && aircraft != null &&
                   IsFamily(aircraft.definition);
        }

        internal static float HorizontalSpeed(Rigidbody body)
        {
            return Vector3.ProjectOnPlane(body.velocity, Vector3.up).magnitude;
        }

        internal static bool HasAirborneThrust(Aircraft aircraft)
        {
            return aircraft.TryGetComponent<GroundPhysicsProbe>(out var probe) && probe.HasAirborneClearance;
        }

        internal static float SurfaceClearance(Aircraft aircraft)
        {
            return aircraft.TryGetComponent<GroundPhysicsProbe>(out var probe) ? probe.SurfaceClearance : aircraft.radarAlt;
        }

        private static float ResolveNativeAltitude(Aircraft aircraft)
        {
            if (!IsPrototype(aircraft) || !aircraft.LocalSim)
                return aircraft.radarAlt;

            float clearance = aircraft.TryGetComponent<GroundPhysicsProbe>(out var probe)
                ? probe.NativeRadarClearance()
                : GroundPhysicsProbe.MeasureSurfaceClearance(aircraft);
            aircraft.radarAlt = clearance <= .35f ? 0f : clearance;
            return aircraft.radarAlt;
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.CheckRadarAlt))]
        private static class NativeAirborneAltitudePatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var radar = AccessTools.Field(typeof(Unit), nameof(Unit.radarAlt));
                var airborne = AccessTools.Field(typeof(Aircraft), "airborne");
                int index = Enumerable.Range(0, Math.Max(0, code.Count - 3)).FirstOrDefault(i =>
                    code[i].opcode == OpCodes.Ldfld && Equals(code[i].operand, radar) &&
                    code[i + 1].opcode == OpCodes.Ldc_R4 && Equals(code[i + 1].operand, .2f) &&
                    code[i + 2].opcode == OpCodes.Cgt && code[i + 3].opcode == OpCodes.Stfld &&
                    Equals(code[i + 3].operand, airborne));
                if (index == 0)
                {
                    log.LogWarning("Superheavy native flight state could not locate the radar altitude comparison.");
                    return code;
                }
                // Keep the native airborne transition, audio and touchdown event.
                code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(SuperheavyGroundPhysicsPlugin), nameof(ResolveNativeAltitude));
                return code;
            }
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SpawnedInPosition))]
        private static class NativeSpawnAltitudePatch
        {
            private static void CorrectAtSpawn(Aircraft aircraft)
            {
                if (!IsPrototype(aircraft) || !aircraft.LocalSim)
                    return;
                float original = aircraft.radarAlt;
                float corrected = ResolveNativeAltitude(aircraft);
                log.LogInfo($"Superheavy spawn altitude: native={original:F2} m, corrected={corrected:F2} m.");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var spawned = AccessTools.Field(typeof(Aircraft), "onSpawnedInPosition");
                int index = code.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, spawned)) - 1;
                if (index < 0 || code[index].opcode != OpCodes.Ldarg_0)
                {
                    log.LogWarning("Superheavy spawn state could not locate the native position event.");
                    return code;
                }
                // Both paths of the native raycast join here, before listeners and gear initialization.
                var start = new CodeInstruction(OpCodes.Ldarg_0);
                start.labels.AddRange(code[index].labels);
                code[index].labels.Clear();
                code.InsertRange(index, new[] { start, new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(NativeSpawnAltitudePatch), nameof(CorrectAtSpawn))) });
                return code;
            }
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SetGear), new[] { typeof(bool) })]
        private static class GroundGearInterlock
        {
            private static bool Prefix(Aircraft __instance, bool __0)
            {
                if (__0 || !IsLocalPrototype(__instance) || __instance.gearState == LandingGear.GearState.LockedRetracted ||
                    !__instance.TryGetComponent<GroundPhysicsProbe>(out var probe) || probe.CanRetract)
                    return true;
                if (GameManager.IsLocalAircraft(__instance))
                    SceneSingleton<AircraftActionsReport>.i?.ReportText("Gear retraction inhibited on ground", 3f);
                log.LogInfo("Superheavy gear retraction inhibited: weight on wheels or runway within 0.75 m.");
                return false;
            }
        }

        [HarmonyPatch(typeof(Altitude), nameof(Altitude.Refresh))]
        private static class HudRadarAltitudePatch
        {
            private static void Postfix(Aircraft ___aircraft, TextMeshProUGUI ___radarAlt)
            {
                if (___radarAlt != null && IsLocalPrototype(___aircraft))
                    ___radarAlt.text = "R[" + UnitConverter.AltitudeReading(SurfaceClearance(___aircraft)) + "]";
            }
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.OpenCanopies))]
        private static class GroundHatchesPatch
        {
            private static bool Prefix(Aircraft __instance, Canopy[] ___canopies)
            {
                if (!IsPrototype(__instance))
                    return true;
                if (!__instance.networked)
                    return false;
                if (!__instance.HasEjected())
                    return true;
                if (SuperheavyCrewAccess.StationaryOnGround(__instance) &&
                    SuperheavyCrewAccess.TryGet(__instance, out var access))
                {
                    access.OpenForNativeDismount();
                    log.LogInfo("Superheavy crew door and stairs open for native ground dismount.");
                    return false;
                }

                // An emergency exit still needs the entire seat envelope.
                foreach (Canopy hatch in ___canopies)
                {
                    if (hatch != null)
                        hatch.Eject();
                }

                log.LogInfo($"Superheavy ground ejection jettisoned {___canopies.Length} roof hatches.");
                return false;
            }
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SetComplexPhysics))]
        private static class SingleBodyPatch
        {
            private static bool Prefix(Aircraft __instance)
            {
                if (!IsPrototype(__instance) || (__instance.networked && !__instance.LocalSim))
                    return true;

                var body = __instance.rb;
                var center = __instance.transform.Find("SHGT_PhysicsCG");
                if (body == null || center == null)
                {
                    log.LogWarning("Superheavy ground physics could not find the root rigidbody or authored center of mass.");
                    return true;
                }

                if (!__instance.networked)
                {
                    // Only the hangar/selector clone is a static single body.
                    __instance.SetSimplePhysics();
                    ApplyTotalMass(__instance);
                    body.centerOfMass = center.localPosition;
                    body.isKinematic = true;
                    return false;
                }

                // Capture and bind before native CreateRB unparents physical
                // parts. Existing systems retain their real object references.
                SuperheavyNativeDamage.Capture(__instance);
                if (!__instance.TryGetComponent<GroundPhysicsProbe>(out _))
                {
                    __instance.gameObject.AddComponent<GroundPhysicsProbe>().Initialize(__instance, log);
                    SuperheavyFlightDynamics.Attach(__instance, log);
                    try
                    {
                        SuperheavyGearPhysics.Attach(__instance, log);
                        SuperheavyGroundVisuals.Attach(__instance, log);
                        SuperheavyGroundHighLift.Attach(__instance);
                        SuperheavyGroundFlightControls.Attach(__instance, log);
                        SuperheavyCrewAccess.TryGet(__instance, out _);
                        SuperheavyGroundApu.Attach(__instance, log);
                        SuperheavyGroundControls.Attach(__instance, log);
                        SuperheavyGroundMfds.Attach(__instance, log);
                    }
                    catch (Exception error) { log.LogWarning($"Superheavy native setup failed: {error.Message}"); }
                }
                // Native callers normally transition from simple once. A
                // repeated request must not add another set of FixedJoints.
                if (!__instance.simplePhysics && __instance.partLookup.Any(p =>
                    p != null && p.rb != null && p.rb != body))
                    return false;
                // The game creates each AeroPart Rigidbody, its mass and native
                // breakable joints. Never overwrite the root with gross mass.
                return true;
            }

            private static void Postfix(Aircraft __instance)
            {
                if (IsLocalPrototype(__instance) && !__instance.simplePhysics)
                    SuperheavyNativeDamage.Complete(__instance, log);
            }
        }

        [HarmonyPatch(typeof(UnitPart), nameof(UnitPart.ModifyMass))]
        private static class PartMassPatch
        {
            private static bool Prefix(UnitPart __instance, float amount)
            {
                var aircraft = __instance.parentUnit as Aircraft;
                if (!IsLocalPrototype(aircraft) || !aircraft.simplePhysics || aircraft.rb == null)
                    return true;

                // The native method would set the shared rigidbody to this one part's mass.
                __instance.mass += amount;
                ApplyTotalMass(aircraft);
                return false;
            }
        }

        private static void ApplyTotalMass(Aircraft aircraft)
        {
            float mass = 0f;
            foreach (var part in aircraft.partLookup)
                if (part != null && !part.IsDetached())
                    mass += part.GetMass();
            if (mass > 0f)
                aircraft.rb.mass = mass;
        }

        [HarmonyPatch(typeof(JetNozzle), nameof(JetNozzle.Thrust))]
        private static class DryThrustPatch
        {
            private static void Prefix(Aircraft ___aircraft, ref bool allowAfterburner)
            {
                if (IsLocalPrototype(___aircraft))
                    allowAfterburner = false;
            }
        }
    }

    internal sealed class GroundPhysicsProbe : MonoBehaviour
    {
        private static readonly System.Reflection.FieldInfo NozzleThrust = AccessTools.Field(typeof(JetNozzle), "totalThrust");
        private static readonly System.Reflection.FieldInfo NozzleProportion = AccessTools.Field(typeof(JetNozzle), "thrustProportion");
        private Aircraft aircraft;
        private LandingGear[] landingGear;
        private JetNozzle[] nozzles;
        private ManualLogSource log;
        private float peakSpeed;
        private bool takeoffLogged;
        private bool climbLogged;
        private float unloadedSince;
        private float lastWheelContact = float.NegativeInfinity;
        private float nextSurfaceSample;
        internal float SurfaceClearance { get; private set; }
        internal bool CanRetract => Time.fixedTime - unloadedSince >= .45f && SurfaceClearance > .75f;
        internal bool HasAirborneClearance => CanRetract && SurfaceClearance > 12f;

        internal void Initialize(Aircraft aircraft, ManualLogSource log)
        {
            this.aircraft = aircraft;
            this.log = log;
            landingGear = aircraft.GetComponentsInChildren<LandingGear>(true);
            unloadedSince = Time.fixedTime;
            SampleClearance();
            nozzles = aircraft.GetComponentsInChildren<JetNozzle>(true);
            foreach (var gear in landingGear)
                gear.onGearBreak += LogGearBreak;
            StartCoroutine(SampleGroundRun());
        }

        private void OnDestroy()
        {
            if (landingGear == null)
                return;
            foreach (var gear in landingGear)
            {
                if (gear != null)
                    gear.onGearBreak -= LogGearBreak;
            }
            log.LogInfo($"Superheavy ground run: peak horizontal speed={peakSpeed * 3.6f:F1} km/h.");
        }

        private void FixedUpdate()
        {
            if (aircraft == null || !aircraft.LocalSim || aircraft.rb == null)
                return;
            peakSpeed = Mathf.Max(peakSpeed, SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb));
            foreach (var gear in landingGear)
            {
                if (gear == null || !gear.enabled || !gear.WeightOnWheel(.01f))
                    continue;
                unloadedSince = Time.fixedTime;
                lastWheelContact = Time.fixedTime;
                SurfaceClearance = 0f;
                return;
            }
            if (Time.fixedTime >= nextSurfaceSample)
                SampleClearance();
            if (!takeoffLogged && CanRetract && SurfaceClearance > 4f && aircraft.rb.velocity.y > .5f)
            {
                takeoffLogged = true;
                LogFlightMilestone("liftoff");
            }
            if (takeoffLogged && !climbLogged && SurfaceClearance > 50f && aircraft.rb.velocity.y > .5f)
            {
                climbLogged = true;
                LogFlightMilestone("climb");
            }
        }

        private void LogFlightMilestone(string label) => log.LogInfo($"Superheavy {label}: " +
            $"height={SurfaceClearance:F1} m, speed={aircraft.speed * 3.6f:F1} km/h, " +
            $"vertical={aircraft.rb.velocity.y:F1} m/s, gross={aircraft.rb.mass / 1000f:F1} t.");

        private void SampleClearance()
        {
            nextSurfaceSample = Time.fixedTime + .12f;
            SurfaceClearance = MeasureSurfaceClearance(aircraft);
        }

        internal float NativeRadarClearance()
        {
            foreach (var gear in landingGear)
                if (gear != null && gear.enabled && gear.WeightOnWheel(.01f))
                    return 0f;
            float clearance = MeasureSurfaceClearance(aircraft);
            return Time.fixedTime - lastWheelContact < .25f && clearance < .9f ? 0f : clearance;
        }

        internal static float MeasureSurfaceClearance(Aircraft aircraft)
        {
            const float startAboveOrigin = 8f;
            Vector3 start = aircraft.transform.position + Vector3.up * startAboveOrigin;
            int surfaceLayers = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
            return Physics.Raycast(start, Vector3.down, out var hit, 10008f, surfaceLayers,
                QueryTriggerInteraction.Ignore)
                ? Mathf.Max(0f, hit.distance - startAboveOrigin)
                : Mathf.Max(0f, aircraft.radarAlt);
        }

        private void LogGearBreak(LandingGear gear)
        {
            log.LogWarning($"Superheavy landing gear broke: {gear.name}.");
        }

        private IEnumerator SampleGroundRun()
        {
            foreach (float interval in new[] { 0.5f, 2.5f, 9f, 12f, 12f })
            {
                yield return new WaitForSeconds(interval);
                if (!aircraft.LocalSim || aircraft.rb == null)
                    yield break;

                var inputs = aircraft.GetInputs();
                int contacts = landingGear.Count(gear => gear != null && gear.enabled && gear.WeightOnWheel(0.01f));
                float thrust = NozzleThrust != null && NozzleProportion != null
                    ? nozzles.Where(nozzle => nozzle != null && (float)NozzleProportion.GetValue(nozzle) > 0f)
                        .Sum(nozzle => (float)NozzleThrust.GetValue(nozzle))
                    : float.NaN;
                string suspension = SuperheavyGearPhysics.TryRead(aircraft, "Nose", out float noseTravel, out float noseSpin, out _) &&
                    SuperheavyGearPhysics.TryRead(aircraft, "Port_03", out float mainTravel, out float mainSpin, out _)
                    ? $", nose travel={noseTravel:+0.00;-0.00;0.00} m, main travel={mainTravel:+0.00;-0.00;0.00} m, " +
                      $"nose/main wheel={noseSpin * 60f / (2f * Mathf.PI):F0}/{mainSpin * 60f / (2f * Mathf.PI):F0} RPM"
                    : string.Empty;
                log.LogInfo($"Superheavy ground sample: wheels={contacts}/{landingGear.Length}, ignition={aircraft.Ignition}, " +
                    $"throttle={inputs.throttle:P0}, brake={inputs.brake:P0}, nozzle thrust={thrust / 1000f:F1} kN, " +
                    $"ground speed={SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb):F2} m/s, " +
                    $"peak={peakSpeed * 3.6f:F1} km/h{suspension}.");
            }
        }
    }
}
