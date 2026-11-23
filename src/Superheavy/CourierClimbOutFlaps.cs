using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier climb-out flap schedule (runtime 0.8.8). The family high-lift schedule ties the flaps to the
    // gear lever: once the gear is selected up the target is 0 and the panels run in at 34%/s, so the 400 t
    // Courier lost ~0.38 of wing lift coefficient in about 2 s at 250 km/h and sank at -10 m/s (measured in the
    // real game, 0.8.5 and 0.8.7). Above 55 m with the gear still down the same schedule also switched to the
    // approach law and drove the flaps from 74% to 100% during the climb.
    // After a takeoff and until the flaps are clean, the Courier instead retracts them on lift demand and speed,
    // as a heavy transport flap schedule does: the target never rises above the current position, follows the
    // family's own load/speed law (capped at the takeoff setting), and retracts at 6%/s (faster inside the
    // structural flap speed band). Lowering the gear again while airborne, or reaching clean flaps, hands the
    // schedule back to the family. The ground roll and every other aircraft keep the family schedule unchanged.
    internal static class CourierClimbOutFlaps
    {
        private const float MaxTakeoffFlap = .74f, ClimbRetractRate = .06f, StructuralRetractRate = .34f;

        private sealed class State {internal bool ClimbOut, GearRaised, Logged;}
        private sealed class Context {internal State State;internal float Flap;internal float Clearance;internal bool Airborne;}

        private static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();
        [ThreadStatic] private static Context current;
        private static FieldInfo owner;
        private static MethodInfo flapPosition;
        private static Func<Aircraft, float> clearance;

        internal static void Install(Harmony h)
        {
            var family = CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics");
            var plugin = CurrentAirframeCompatibility.Family("SuperheavyGroundPhysicsPlugin");
            owner = AccessTools.Field(family, "aircraft");
            flapPosition = AccessTools.PropertyGetter(family, "FlapPosition");
            var surface = AccessTools.Method(plugin, "SurfaceClearance", new[] {typeof(Aircraft)});
            if (owner == null || flapPosition == null || surface == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier climb-out flaps: family schedule not found; keeping the family law.");
                return;
            }
            clearance = (Func<Aircraft, float>)Delegate.CreateDelegate(typeof(Func<Aircraft, float>), surface);
            h.CreateClassProcessor(typeof(TickContext)).Patch();
            h.CreateClassProcessor(typeof(Schedule)).Patch();
        }

        private static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        private static bool GearDown(Aircraft a) => a.gearState == LandingGear.GearState.LockedExtended ||
            a.gearState == LandingGear.GearState.Extending ||
            (a.gearState == LandingGear.GearState.Uninitialized && a.gearDeployed);

        [HarmonyPatch] private static class TickContext
        {
            private static MethodBase TargetMethod() => AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics"), "FixedUpdate");

            private static void Prefix(object __instance, out Context __state)
            {
                __state = current;
                current = null;
                var aircraft = owner.GetValue(__instance) as Aircraft;
                if (!IsCourier(aircraft) || !aircraft.LocalSim)
                    return;
                var state = States.GetValue(__instance, _ => new State());
                float height = clearance(aircraft);
                bool gearDown = GearDown(aircraft);
                float flap = (float)flapPosition.Invoke(__instance, null);
                bool airborne = height >= 1f;
                if (!airborne)
                {
                    // Every takeoff (including a touch-and-go) arms the climb-out schedule.
                    if (gearDown) {state.ClimbOut = true; state.GearRaised = false;}
                }
                else if (state.ClimbOut)
                {
                    if (!gearDown) state.GearRaised = true;
                    else if (state.GearRaised) state.ClimbOut = false; // gear lowered again: approach, family law
                    if (state.GearRaised && flap <= .001f) state.ClimbOut = false; // clean
                }
                current = new Context {State = state, Flap = flap, Clearance = height, Airborne = airborne};
            }

            private static Exception Finalizer(Context __state, Exception __exception)
            {
                current = __state;
                return __exception;
            }
        }

        [HarmonyPatch] private static class Schedule
        {
            private static MethodBase TargetMethod() => AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics"), "ScheduledFlap");

            private static void Postfix(float __0, float __1, ref float __result)
            {
                var c = current;
                if (c == null || !c.Airborne || !c.State.ClimbOut)
                    return;
                // Family load/speed law (approach branch), capped at the takeoff setting.
                float speedBand = Mathf.InverseLerp(92f, 122f, __0);
                float allowance = 1f - Mathf.SmoothStep(0f, 1f, speedBand);
                float demand = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.42f, 1.22f, __1));
                float wanted = Mathf.Min(MaxTakeoffFlap, demand * allowance);
                float rate = Mathf.Lerp(ClimbRetractRate, StructuralRetractRate, speedBand);
                float target = Mathf.Min(c.Flap, Mathf.Max(wanted, c.Flap - rate * Time.fixedDeltaTime));
                if (!c.State.Logged && (target < __result - .001f || __result > c.Flap + .001f))
                {
                    c.State.Logged = true;
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier climb-out flaps: holding {c.Flap:P0} (family target {__result:P0}, scheduled {wanted:P0}) at EAS {__0 * 3.6f:F0} km/h, lift demand {__1:F2}, AGL {c.Clearance:F0} m.");
                }
                __result = target;
            }
        }
    }
}
