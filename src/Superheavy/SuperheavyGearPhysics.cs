using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGearPhysics : MonoBehaviour
    {
        private const string ProbePrefix = "SHGT_NativeProbe_";
        private const float MotionDuration = 4f;
        // Must match the spring preload in BuildGroundTest; Blender frame one is the loaded stance.
        private const float NosePreload = .24f;
        private const float MainPreload = .22f;

        private static readonly AccessTools.FieldRef<LandingGear, Aircraft> AircraftField =
            AccessTools.FieldRefAccess<LandingGear, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<LandingGear, Transform> CastPoint =
            AccessTools.FieldRefAccess<LandingGear, Transform>("castPoint");
        private static readonly AccessTools.FieldRef<LandingGear, float> Compression =
            AccessTools.FieldRefAccess<LandingGear, float>("compressionDistance");
        private static readonly AccessTools.FieldRef<LandingGear, float> CompressionPrev =
            AccessTools.FieldRefAccess<LandingGear, float>("compressionDistancePrev");
        private static readonly AccessTools.FieldRef<LandingGear, float> WheelSpeed =
            AccessTools.FieldRefAccess<LandingGear, float>("wheelSpeed");
        private static readonly AccessTools.FieldRef<LandingGear, float> WheelRadius =
            AccessTools.FieldRefAccess<LandingGear, float>("wheelRadius");
        private static readonly AccessTools.FieldRef<LandingGear, float> SteeringAngle =
            AccessTools.FieldRefAccess<LandingGear, float>("steeringAngle");
        private static readonly AccessTools.FieldRef<LandingGear, AudioClip> FoldSound =
            AccessTools.FieldRefAccess<LandingGear, AudioClip>("foldSound");
        private static readonly AccessTools.FieldRef<LandingGear, AudioClip> LatchSound =
            AccessTools.FieldRefAccess<LandingGear, AudioClip>("latchSound");
        private static readonly AccessTools.FieldRef<LandingGear, float> FoldVolume =
            AccessTools.FieldRefAccess<LandingGear, float>("foldVolume");
        private static readonly AccessTools.FieldRef<LandingGear, float> LatchVolume =
            AccessTools.FieldRefAccess<LandingGear, float>("latchVolume");

        private Dictionary<string, LandingGear> contacts;
        private Aircraft aircraft;
        private AudioSource gearSound;
        private LandingGear nose;
        private float extension;
        private bool moving;
        private bool lowering;

        internal static void Register(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(NativeGearMotion)).Patch();
            harmony.CreateClassProcessor(typeof(ChangeGearDirection)).Patch();
        }

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.TryGetComponent<SuperheavyGearPhysics>(out _))
                return;

            var contacts = new Dictionary<string, LandingGear>(13);
            foreach (var gear in SuperheavyNativeDamage.Components<LandingGear>(aircraft))
            {
                var probe = CastPoint(gear);
                if (probe != null && probe.name.StartsWith(ProbePrefix, StringComparison.Ordinal))
                    contacts.Add(probe.name.Substring(ProbePrefix.Length), gear);
            }
            if (contacts.Count != 13 || !contacts.TryGetValue("Nose", out var nose))
            {
                log.LogWarning($"Superheavy native gear setup found {contacts.Count}/13 suspension probes.");
                return;
            }

            var controller = aircraft.gameObject.AddComponent<SuperheavyGearPhysics>();
            controller.aircraft = aircraft;
            controller.contacts = contacts;
            controller.nose = nose;
            controller.extension = NativeExtension(aircraft.gearState);
            controller.gearSound = aircraft.gameObject.AddComponent<AudioSource>();
            controller.gearSound.playOnAwake = false;
            controller.gearSound.spatialBlend = 1f;
            controller.gearSound.minDistance = 5f;
            controller.gearSound.maxDistance = 35f;
            controller.gearSound.dopplerLevel = 0f;
            aircraft.onSetGear += controller.OnSetGear;
            if (IsMoving(aircraft.gearState))
                controller.BeginMotion(aircraft.gearState == LandingGear.GearState.Extending);
            log.LogInfo("Superheavy native gear bound 13 independent spring/damper contacts and 4-second synchronized locks.");
        }

        internal static bool TryGetNative(Aircraft aircraft, string axleKey, out LandingGear gear)
        {
            gear = null;
            return aircraft.TryGetComponent<SuperheavyGearPhysics>(out var controller) &&
                   controller.contacts.TryGetValue(axleKey, out gear) && gear != null;
        }

        internal static bool TryRead(Aircraft aircraft, string axleKey, out float compressionFromAuthored,
            out float wheelRadiansPerSecond, out float steeringDegrees)
        {
            compressionFromAuthored = wheelRadiansPerSecond = steeringDegrees = 0f;
            if (!TryGetNative(aircraft, axleKey, out var gear))
                return false;

            bool nose = axleKey == "Nose";
            compressionFromAuthored = Compression(gear) - (nose ? NosePreload : MainPreload);
            wheelRadiansPerSecond = WheelSpeed(gear) / WheelRadius(gear);
            if (nose && gear.enabled)
                steeringDegrees = SteeringAngle(gear);
            return true;
        }

        internal static float Extension(Aircraft aircraft) =>
            aircraft.TryGetComponent<SuperheavyGearPhysics>(out var controller)
                ? controller.extension
                : NativeExtension(aircraft.gearState);

        private static float NativeExtension(LandingGear.GearState state) =>
            state == LandingGear.GearState.LockedRetracted || state == LandingGear.GearState.Extending ? 0f : 1f;

        private static bool IsMoving(LandingGear.GearState state) =>
            state == LandingGear.GearState.Extending || state == LandingGear.GearState.Retracting;

        private void OnSetGear(Aircraft.OnSetGear change)
        {
            if (IsMoving(change.gearState))
            {
                BeginMotion(change.gearState == LandingGear.GearState.Extending);
                return;
            }

            extension = NativeExtension(change.gearState);
            if (!moving)
                return;
            moving = false;
            gearSound.Stop();
            if (nose != null && LatchSound(nose) != null)
            {
                gearSound.volume = 1f;
                gearSound.PlayOneShot(LatchSound(nose), LatchVolume(nose));
            }
        }

        private void BeginMotion(bool lower)
        {
            moving = true;
            lowering = lower;
            if (nose == null || FoldSound(nose) == null)
                return;
            gearSound.Stop();
            gearSound.outputAudioMixerGroup = SoundManager.i != null ? SoundManager.i.EffectsMixer : null;
            gearSound.clip = FoldSound(nose);
            gearSound.volume = FoldVolume(nose);
            gearSound.Play();
        }

        private void Update()
        {
            if (!moving)
                return;

            foreach (var gear in contacts.Values)
            {
                if (gear == null)
                    continue;
                ref float speed = ref WheelSpeed(gear);
                speed = Mathf.MoveTowards(speed, 0f, (Mathf.Abs(speed) * .1f + 1f) * Time.deltaTime);
            }

            float endpoint = lowering ? 1f : 0f;
            extension = Mathf.MoveTowards(extension, endpoint, Time.deltaTime / MotionDuration);
            if (extension == endpoint)
                aircraft.SetGear(lowering ? LandingGear.GearState.LockedExtended : LandingGear.GearState.LockedRetracted);
        }

        private void OnDestroy()
        {
            if (aircraft != null)
                aircraft.onSetGear -= OnSetGear;
            if (gearSound != null)
                Destroy(gearSound);
        }

        [HarmonyPatch(typeof(LandingGear), "LandingGear_OnSetGear")]
        private static class NativeGearMotion
        {
            private static bool Prefix(LandingGear __instance, Aircraft.OnSetGear __0)
            {
                var aircraft = AircraftField(__instance);
                if (aircraft == null || !aircraft.TryGetComponent<SuperheavyGearPhysics>(out var controller) ||
                    !controller.contacts.ContainsValue(__instance))
                    return true;

                if (__0.gearState == LandingGear.GearState.LockedRetracted)
                    WheelSpeed(__instance) = 0f;
                if (!IsMoving(__0.gearState))
                    return true;

                // The native coroutine lets the first axle to finish lock the entire aircraft.
                // The synchronized controller locks all axles together after the shared door/leg sequence.
                __instance.enabled = false;
                Compression(__instance) = 0f;
                CompressionPrev(__instance) = 0f;
                return false;
            }
        }

        [HarmonyPatch(typeof(Aircraft), "GearStateChanged")]
        private static class ChangeGearDirection
        {
            private static bool Prefix(Aircraft __instance, bool __0)
            {
                if (!IsMoving(__instance.gearState) || !__instance.TryGetComponent<SuperheavyGearPhysics>(out _))
                    return true;

                // Resume from the current position if the command changes during the travel.
                var target = __0 ? LandingGear.GearState.Extending : LandingGear.GearState.Retracting;
                if (__instance.gearState != target)
                    __instance.SetGear(target);
                return false;
            }
        }
    }
}
