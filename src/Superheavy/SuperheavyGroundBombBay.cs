using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyGroundBombBay
    {
        private const string GateName = "SHGT_BombBayGate";
        private const string MotionName = "SHGT_BombBayMotion";
        private static readonly ConditionalWeakTable<BayDoor, Motion> Motions =
            new ConditionalWeakTable<BayDoor, Motion>();
        private static ManualLogSource log;

        private sealed class Motion
        {
            internal GameObject Host;
            internal AnimationClip Clip;
            internal float LastPhase = -1f;
        }

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(DoorPosePatch)).Patch();
            harmony.CreateClassProcessor(typeof(RailDelayPatch)).Patch();
        }

        [HarmonyPatch(typeof(Hardpoint), nameof(Hardpoint.SpawnMount))]
        private static class RailDelayPatch
        {
            private static readonly AccessTools.FieldRef<BayDoor, float> OpenSpeed =
                AccessTools.FieldRefAccess<BayDoor, float>("openSpeed");
            private static readonly AccessTools.FieldRef<MountedMissile, float> Delay =
                AccessTools.FieldRefAccess<MountedMissile, float>("railDelay");

            private static void Postfix(Hardpoint __instance, Aircraft __0, GameObject __result)
            {
                if (__result == null || !SuperheavyGroundPhysicsPlugin.IsFamily(__0?.definition) ||
                    __instance.bayDoors == null)
                    return;
                foreach (var gate in __instance.bayDoors)
                {
                    if (gate == null || !gate.name.StartsWith(GateName, System.StringComparison.Ordinal) || OpenSpeed(gate) <= 0f)
                        continue;
                    float openingDelay = 1f / OpenSpeed(gate) + .1f;
                    foreach (var rail in __result.GetComponentsInChildren<MountedMissile>(true))
                    {
                        // A BG physical conveyor stages a real store and waits
                        // for all relevant native door phases before LaunchMount.
                        // Adding this delay again serialized every round behind
                        // an already-open door. Courier auxiliary rails still
                        // need the original opening-time safety delay.
                        if(!SuperheavyBomberFeed.ManagesRail(__0,rail)&&!SuperheavySpatialWeaponFeed.ManagesRail(__0,rail))
                            Delay(rail) = Mathf.Max(Delay(rail), openingDelay);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(BayDoor), "Update")]
        private static class DoorPosePatch
        {
            private static void Postfix(BayDoor __instance, float ___openAmount)
            {
                if (!__instance.name.StartsWith(GateName, System.StringComparison.Ordinal))
                    return;
                if (!Motions.TryGetValue(__instance, out var motion))
                {
                    var owner = SuperheavyNativeDamage.Owner(__instance);
                    if (!SuperheavyGroundPhysicsPlugin.IsFamily(owner?.definition))
                        return;
                    var host = owner.transform.Find(MotionName + __instance.name.Substring(GateName.Length));
                    var animation = host == null ? null : host.GetComponent<Animation>();
                    motion = new Motion { Host = host == null ? null : host.gameObject, Clip = animation?.clip };
                    Motions.Add(__instance, motion);
                    if (motion.Clip == null)
                        log.LogError("Courier ventral bay folding-door animation is missing.");
                }
                if (motion.Clip == null || motion.Host == null)
                    return;

                // Native BayDoor remains the authority for the door phase and
                // hold time. Its phase drives the authored
                // unseat/fold sequence rather than rotating intersecting leaves.
                float phase = Mathf.Clamp01(___openAmount);
                if (phase == motion.LastPhase)
                    return;
                motion.Clip.SampleAnimation(motion.Host, phase * motion.Clip.length);
                motion.LastPhase = phase;
            }
        }
    }
}
