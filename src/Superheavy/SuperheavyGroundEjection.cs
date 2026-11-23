using System.Collections;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyGroundEjection
    {
        private static ManualLogSource log;

        internal static void Install(Harmony harmony)
        {
            log = BepInEx.Logging.Logger.CreateLogSource("Superheavy Ejection");
            harmony.CreateClassProcessor(typeof(StationaryCrewExitPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GroundDismountPatch)).Patch();
            harmony.CreateClassProcessor(typeof(EjectionProgressPatch)).Patch();
            harmony.CreateClassProcessor(typeof(SeatHullClearancePatch)).Patch();
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
        private static class StationaryCrewExitPatch
        {
            private static bool Prefix(Aircraft __instance)
            {
                if (!IsSuperheavy(__instance) || !GameManager.IsLocalAircraft(__instance) ||
                    __instance.HasEjected() || !SuperheavyCrewAccess.StationaryOnGround(__instance) ||
                    !SuperheavyCrewAccess.TryGet(__instance, out var access))
                    return true;
                return access.RequestExit();
            }
        }

        private static bool IsSuperheavy(Aircraft aircraft)
        {
            return GameManager.gameState == GameState.SinglePlayer && aircraft != null && aircraft.networked &&
                   aircraft.definition != null && SuperheavyGroundPhysicsPlugin.IsFamily(aircraft.definition);
        }

        [HarmonyPatch(typeof(PilotDismounted), "FindDismountSpot")]
        private static class GroundDismountPatch
        {
            private const float EgressSide = 5.8f;

            private static bool Prefix(PilotDismounted __instance, Aircraft __0, ref Vector3 __result)
            {
                if (!IsSuperheavy(__0) || __instance.pilotNumber >= __0.pilots.Length)
                    return true;
                if (__0.TryGetComponent<SuperheavyCrewAccess>(out var access) && access.UsingCrewExit)
                {
                    __result = access.DismountRayStart(__instance.pilotNumber);
                    log.LogInfo($"Superheavy native crew exit {__instance.pilotNumber + 1} placed at the stair foot.");
                    return false;
                }

                Pilot pilot = __0.pilots[__instance.pilotNumber];
                if (pilot == null)
                    return true;

                // The inherited cockpit damage part sits below the runway datum.
                // Give the stock downward placement ray a start outside the nose
                // box and at the actual seat height instead.
                Vector3 position = __0.transform.InverseTransformPoint(pilot.transform.position);
                position.x = pilot.exitDirection == Pilot.ExitDirection.Left ? -EgressSide : EgressSide;
                __result = __0.transform.TransformPoint(position);
                return false;
            }
        }

        [HarmonyPatch(typeof(PilotDismounted), nameof(PilotDismounted.SetCollidable))]
        private static class SeatHullClearancePatch
        {
            private static readonly AccessTools.FieldRef<EjectionSeat, bool> Firing =
                AccessTools.FieldRefAccess<EjectionSeat, bool>("firing");
            private static readonly AccessTools.FieldRef<EjectionSeat, Collider> SeatCollider =
                AccessTools.FieldRefAccess<EjectionSeat, Collider>("seatCollider");

            private static void Postfix(PilotDismounted __instance, bool __0, EjectionSeat ___ejectionSeat,
                                        Collider ___pilotCollider)
            {
                if (!__0 || __instance.cockpitPart?.parentUnit is not Aircraft aircraft || !IsSuperheavy(aircraft) ||
                    ___ejectionSeat == null || !Firing(___ejectionSeat))
                    return;

                var bodyFrame = aircraft.transform.Find("SHGT_Fuselage");
                var hull = bodyFrame?.GetComponent<BoxCollider>();
                if (hull == null && bodyFrame != null)
                    foreach (var candidate in aircraft.GetComponents<BoxCollider>())
                        if (Vector3.Distance(candidate.transform.TransformPoint(candidate.center), bodyFrame.position) < .001f)
                        { hull = candidate; break; }
                if (hull == null)
                    return;

                // The native rail ends 1.5 m from the seat, while the taxi
                // fuselage proxy still encloses it. Only ignore this one pair
                // of aircraft/pilot collision surfaces during clearance.
                var colliders = new[] { ___pilotCollider, SeatCollider(___ejectionSeat) };
                foreach (Collider collider in colliders)
                {
                    if (collider != null && collider.enabled)
                        Physics.IgnoreCollision(collider, hull);
                }
                __instance.StartCoroutine(RestoreAfterClear(hull, colliders));
            }

            private static IEnumerator RestoreAfterClear(BoxCollider hull, Collider[] colliders)
            {
                float deadline = Time.time + 2f;
                var nextStep = new WaitForFixedUpdate();
                while (hull != null && Time.time < deadline)
                {
                    yield return nextStep;
                    if (hull == null)
                        yield break;
                    bool overlaps = false;
                    foreach (Collider collider in colliders)
                        overlaps |= collider != null && collider.enabled && collider.bounds.Intersects(hull.bounds);
                    if (!overlaps)
                        break;
                }

                if (hull == null)
                    yield break;
                foreach (Collider collider in colliders)
                {
                    if (collider != null && collider.enabled)
                        Physics.IgnoreCollision(collider, hull, false);
                }
            }
        }

        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SpawnEjectingPilot))]
        private static class EjectionProgressPatch
        {
            private static void Postfix(Aircraft __instance, int __0)
            {
                if (IsSuperheavy(__instance))
                    log.LogInfo($"Superheavy native escape actor {__0 + 1}/{__instance.pilots.Length} spawned.");
            }
        }
    }
}
