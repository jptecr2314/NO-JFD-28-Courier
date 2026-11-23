using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundCargoMotion : MonoBehaviour
    {
        private const string GateName = "SHGT_CargoReleaseGate";
        private const string MotionName = "SHGT_CargoMotion";
        private const float OpenSeconds = 4f;
        private const float CloseSeconds = 4f;
        private static ManualLogSource log;

        private GameObject animationRoot;
        private AnimationClip clip;
        private float phase;
        private float holdOpen;
        private bool loggedMissingClip;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(NativeJointLifecyclePatch)).Patch();
            harmony.CreateClassProcessor(typeof(OpenDoorPatch)).Patch();
            harmony.CreateClassProcessor(typeof(IsOpenPatch)).Patch();
        }

        private static bool IsAuthoredGate(CargoRamp ramp) => ramp.name == GateName;

        [HarmonyPatch]
        private static class NativeJointLifecyclePatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(CargoRamp), "Awake");
                yield return AccessTools.DeclaredMethod(typeof(CargoRamp), "Start");
                yield return AccessTools.DeclaredMethod(typeof(CargoRamp), "Update");
            }

            // The real native rail uses this gate for sequencing, while the authored
            // visual has no stock Darkreach ramp joint or latch.
            private static bool Prefix(CargoRamp __instance) => !IsAuthoredGate(__instance);
        }

        [HarmonyPatch(typeof(CargoRamp), nameof(CargoRamp.OpenDoor))]
        private static class OpenDoorPatch
        {
            private static bool Prefix(CargoRamp __instance, float __0)
            {
                if (!IsAuthoredGate(__instance))
                    return true;

                if (!__instance.TryGetComponent<SuperheavyGroundCargoMotion>(out var motion))
                    motion = __instance.gameObject.AddComponent<SuperheavyGroundCargoMotion>();
                motion.RequestOpen(__0);
                return false;
            }
        }

        [HarmonyPatch(typeof(CargoRamp), nameof(CargoRamp.IsOpen))]
        private static class IsOpenPatch
        {
            private static bool Prefix(CargoRamp __instance, ref bool __result)
            {
                if (!IsAuthoredGate(__instance))
                    return true;

                __result = __instance.TryGetComponent<SuperheavyGroundCargoMotion>(out var motion)
                    && motion.clip != null && motion.phase >= 1f;
                return false;
            }
        }

        private void RequestOpen(float duration)
        {
            if (clip == null)
            {
                var aircraft = SuperheavyNativeDamage.Owner(this);
                var host = aircraft != null ? aircraft.transform.Find(MotionName) : null;
                var animation = host != null ? host.GetComponent<Animation>() : null;
                if (animation == null || animation.clip == null)
                {
                    if (!loggedMissingClip)
                    {
                        log.LogError("Superheavy cargo release is waiting for the authored ramp clip, which is missing.");
                        loggedMissingClip = true;
                    }
                    return;
                }
                animationRoot = host.gameObject;
                clip = animation.clip;
            }

            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration < .1f)
                duration = .1f;
            if (phase == 0f && holdOpen <= 0f)
                log.LogInfo("Superheavy cargo doors opening on a native release request.");
            holdOpen = Mathf.Max(holdOpen, duration);
        }

        private void Update()
        {
            if (clip == null || animationRoot == null)
                return;
            float seconds = Time.deltaTime;
            if (holdOpen > 0f && phase >= 1f)
            {
                holdOpen -= seconds;
                return;
            }

            float next = holdOpen > 0f
                ? Mathf.MoveTowards(phase, 1f, seconds / OpenSeconds)
                : Mathf.MoveTowards(phase, 0f, seconds / CloseSeconds);
            if (next == phase)
                return;
            phase = next;
            clip.SampleAnimation(animationRoot, clip.length * phase);
            if (phase >= 1f)
                log.LogInfo("Superheavy cargo ramp fully open; native cargo rail clear.");
        }
    }
}
