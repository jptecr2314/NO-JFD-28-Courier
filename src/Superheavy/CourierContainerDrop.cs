using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner request for 0.1.9 (23/11/2026): the Courier package brings the Cargo Retro Kit, including for the supply
    // containers, and a container released in the air must steer to the nearest friendly unit it can sensibly reach,
    // judged by distance and by the release height, never to a unit kilometres away.
    // The family air release refuses anything that is not a GroundVehicle (TryMeasureAirRelease) and its planner hands a
    // released load to the Retro Kit only for vehicles. For a container with the Retro Kit (Retro Kit 0.4 marks them):
    //  - the air release measure accepts native Container units;
    //  - the release is planned here: reach radius R = 150 m + 0.6 x release height above ground, 200-1200 m, around the
    //    natural impact point; friendly ground vehicles (a friendly ship for the naval container) inside R are ranked by
    //    horizontal distance plus twice the height difference, and the first whose landing spot (25 m beside the unit,
    //    on the side facing the drop; 50 m off a ship, on the water) passes the Retro Kit's own guided prediction is the
    //    target; with none in reach, the nearest clear spot to the natural impact point is used, as for vehicles;
    //  - at separation the plan is checked again from the real state and the Retro Kit is armed on the container.
    // Containers without the kit, ground unloading and vehicles are unchanged.
    internal static class CourierContainerDrop
    {
        private const float StandoffVehicle = 25f, StandoffShip = 50f, MaxCandidates = 6, BesideRadius = 120f;
        private sealed class Pending
        {
            internal MountedCargo Cargo;
            internal UnitDefinition Definition;
            internal GlobalPosition Target;
            internal Vector3 Heading;
            internal string Friend;
            internal float Until;
            internal bool Natural;
        }

        private static readonly Dictionary<Aircraft, Pending> Plans = new Dictionary<Aircraft, Pending>();
        private static MethodInfo isRetro, predict, predictGuided, armUnit, armNatural, resolve, report;
        private static FieldInfo waterLanding, markerContainer;
        private static Type retroApi, markerType;

        internal static void Install(Harmony h)
        {
            var rail = CurrentAirframeCompatibility.Family("SuperheavyGroundCargo");
            var drop = CurrentAirframeCompatibility.Family("SuperheavyAirdrop");
            var zone = CurrentAirframeCompatibility.Family("SuperheavyAirdropZone");
            var measure = rail == null ? null : AccessTools.Method(rail, "TryMeasureAirRelease");
            var arm = drop == null ? null : AccessTools.Method(drop, "TryArm");
            report = drop == null ? null : AccessTools.Method(drop, "Report", new[] { typeof(Aircraft), typeof(string) });
            resolve = zone == null ? null : zone.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "TryResolve" && m.GetParameters().Length == 8 &&
                    m.GetParameters()[6].ParameterType == typeof(Func<GlobalPosition, Vector3, bool>));
            if (measure == null || arm == null || resolve == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier container airdrop: family release path not found; containers stay ground-only.");
                return;
            }
            h.Patch(measure, transpiler: new HarmonyMethod(typeof(CourierContainerDrop), nameof(AcceptContainers)));
            h.Patch(arm, prefix: new HarmonyMethod(typeof(CourierContainerDrop), nameof(BeforeArm)) { priority = Priority.High });
            h.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnUnit)),
                postfix: new HarmonyMethod(typeof(CourierContainerDrop), nameof(AfterSpawn)) { priority = Priority.Low });
        }

        // The family measure asks unitPrefab.GetComponent<GroundVehicle>() != null; a native Container also qualifies.
        private static IEnumerable<CodeInstruction> AcceptContainers(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(CourierContainerDrop), nameof(AirReleaseUnit));
            bool done = false;
            foreach (var ci in instructions)
            {
                if (!done && ci.operand is MethodInfo m && m.Name == "GetComponent" && m.IsGenericMethod &&
                    m.GetGenericArguments()[0] == typeof(GroundVehicle) && m.DeclaringType == typeof(GameObject))
                {
                    done = true;
                    yield return new CodeInstruction(OpCodes.Call, helper).WithLabels(ci.labels);
                    continue;
                }
                yield return ci;
            }
            if (!done)
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier container airdrop: air release measure changed; containers stay ground-only.");
        }

        private static Component AirReleaseUnit(GameObject prefab)
        {
            if (prefab == null) return null;
            var vehicle = prefab.GetComponent<GroundVehicle>();
            return vehicle != null ? (Component)vehicle : prefab.GetComponent<Container>();
        }

        private static bool Bind()
        {
            if (retroApi != null) return isRetro != null && predict != null && predictGuided != null && armUnit != null;
            retroApi = AccessTools.TypeByName("Japensou.NuclearOption.Type12Retro.RetroKitApi");
            markerType = AccessTools.TypeByName("Japensou.NuclearOption.Type12Retro.RetroCargoMarker");
            if (retroApi == null || markerType == null) return false;
            isRetro = AccessTools.Method(retroApi, "IsRetroCargo");
            predict = AccessTools.Method(retroApi, "Predict");
            predictGuided = AccessTools.Method(retroApi, "PredictGuided");
            armUnit = AccessTools.Method(retroApi, "ArmGuidedUnit");
            armNatural = AccessTools.Method(retroApi, "ArmNaturalUnit");
            waterLanding = AccessTools.Field(markerType, "WaterLanding");
            markerContainer = AccessTools.Field(markerType, "IsContainer");
            return isRetro != null && predict != null && predictGuided != null && armUnit != null;
        }

        private static bool IsRetroContainer(MountedCargo cargo)
        {
            if (cargo == null || cargo.cargo == null || cargo.cargo.unitPrefab == null || cargo.cargo.unitPrefab.GetComponent<Container>() == null)
                return false;
            if (!Bind() || markerContainer == null) return false;
            var marker = cargo.GetComponent(markerType);
            return marker != null && (bool)markerContainer.GetValue(marker);
        }

        private static bool BeforeArm(Aircraft __0, MountedCargo __1, Vector3 __2, Vector3 __3, float __4, ref string __5, ref bool __result)
        {
            if (!IsRetroContainer(__1))
                return true;
            try
            {
                __result = false;
                if (__0 == null || __0.rb == null || float.IsNaN(__4) || __4 < 0f)
                {
                    __5 = "AIRDROP — invalid trajectory data.";
                    return false;
                }
                Vector3 start = __2 + __0.rb.GetPointVelocity(__2) * __4;
                if (!TryPlan(__0, __1, start, __3, __1.transform.rotation, out var plan, out __5))
                    return false;
                plan.Until = Time.time + __4 + 15f;
                Plans[__0] = plan;
                __result = true;
                __5 = null;
                Notify(__0, plan.Friend != null ? $"Container release accepted: guided to {plan.Friend}."
                    : plan.Natural ? "Container release accepted: no friendly unit in reach, braking straight down onto clear ground."
                    : "Container release accepted: no friendly unit in reach, landing in the nearest clear spot.");
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier container airdrop planned: {__1.cargo.unitName} -> {(plan.Friend ?? (plan.Natural ? "own fall, braking only" : "clear spot"))} at {plan.Target}.");
            }
            catch (Exception e)
            {
                __result = false;
                __5 = "AIRDROP — container guidance unavailable.";
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier container airdrop: " + e);
            }
            return false;
        }

        private static void AfterSpawn(Unit __result, Unit __4)
        {
            if (!(__4 is Aircraft aircraft) || !(__result is Container box) || !Plans.TryGetValue(aircraft, out var plan))
                return;
            if (Time.time > plan.Until || plan.Definition != __result.definition)
            {
                if (Time.time > plan.Until) Plans.Remove(aircraft);
                return;
            }
            Plans.Remove(aircraft);
            try
            {
                var body = box.rb;
                if (body == null) return;
                Vector3 start = body.position, velocity = body.velocity;
                if (plan.Natural ? !NaturalSafe(plan.Cargo, start, velocity) : !Guided(plan.Cargo, start, velocity, body.rotation, plan.Target, plan.Heading))
                {
                    if (TryPlan(aircraft, plan.Cargo, start, velocity, body.rotation, out var again, out _))
                        plan = again;
                    else
                        Notify(aircraft, "AIRDROP — trajectory changed; container retros in emergency recovery.");
                }
                bool armed = plan.Natural
                    ? (bool)armNatural.Invoke(null, new object[] { box, plan.Cargo })
                    : (bool)armUnit.Invoke(null, new object[] { box, plan.Cargo, plan.Target, plan.Heading });
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier container retro armed={armed}: {box.definition.unitName} -> {(plan.Friend ?? (plan.Natural ? "own fall, braking only" : "clear spot"))} at {plan.Target}, release {start}, velocity {velocity}.");
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier container airdrop at separation: " + e); }
        }

        internal static float ReachRadius(float releaseHeight) => Mathf.Clamp(150f + .6f * releaseHeight, 200f, 1200f);

        private static bool TryPlan(Aircraft aircraft, MountedCargo cargo, Vector3 start, Vector3 velocity, Quaternion rotation, out Pending plan, out string reason)
        {
            plan = null;
            reason = "AIRDROP — no safe landing in reach for the Retro Kit.";
            var args = new object[] { cargo, start, velocity, null, null, null, null };
            if (!(bool)predict.Invoke(null, args))
                return false;
            Vector3 natural = (Vector3)args[3];
            bool naval = waterLanding != null && (bool)waterLanding.GetValue(cargo.GetComponent(markerType));
            float height = start.y - Ground(start, out _);
            float reach = ReachRadius(height);
            Vector3 heading = Vector3.ProjectOnPlane(velocity, Vector3.up).normalized;
            if (heading.sqrMagnitude < .01f) heading = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up).normalized;
            if (heading.sqrMagnitude < .01f) heading = Vector3.forward;
            float halfLength = Mathf.Max(1.1f, cargo.cargo.length * .5f), halfWidth = Mathf.Max(.7f, cargo.cargo.width * .5f);
            var hq = aircraft.NetworkHQ;

            var friends = (UnitRegistry.allUnits ?? new List<Unit>()).Where(u =>
                    u != null && !u.disabled && hq != null && u.NetworkHQ == hq && u != aircraft &&
                    (naval ? u is Ship : u is GroundVehicle) && Flat(u.transform.position - natural) <= reach)
                .Select(u => (unit: u, cost: Flat(u.transform.position - natural) + 2f * Mathf.Abs(u.transform.position.y - natural.y)))
                .OrderBy(x => x.cost).Take((int)MaxCandidates).ToList();
            var all = (UnitRegistry.allUnits ?? new List<Unit>()).Where(u => u != null && (naval ? u is Ship : u is GroundVehicle)).ToList();
            var closest = all.OrderBy(u => Flat(u.transform.position - natural)).FirstOrDefault();
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier container airdrop: natural safe={args[5]} impact={args[4]}; registry {all.Count} ({all.Count(u => u.NetworkHQ == hq)} same HQ); closest " +
                (closest != null ? $"{closest.unitName} {Flat(closest.transform.position - natural):F0} m hq={(closest.NetworkHQ == hq)} disabled={closest.disabled}" : "none"));
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier container airdrop: release height {height:F0} m, natural impact {Flat(natural - start):F0} m ahead, reach {reach:F0} m, friendly candidates {friends.Count}" +
                string.Concat(friends.Select(f => $" [{f.unit.unitName} {Flat(f.unit.transform.position - natural):F0} m, dy {f.unit.transform.position.y - natural.y:F0}]")));

            foreach (var entry in friends)
            {
                Unit unit = entry.unit;
                Vector3 at = unit.transform.position;
                Vector3 away = Vector3.ProjectOnPlane(natural - at, Vector3.up);
                if (away.sqrMagnitude < 1f) away = -heading;
                away.Normalize();
                float size = unit.definition != null ? unit.definition.length * .5f : 5f;
                Vector3 desired = at + away * (size + (naval ? StandoffShip : StandoffVehicle));
                if (naval)
                {
                    desired.y = Datum.LocalSeaY;
                    if (Ground(desired + Vector3.up * 50f, out bool water) > Datum.LocalSeaY + .5f || !water) continue;
                    var target = desired.ToGlobalPosition();
                    if (Guided(cargo, start, velocity, rotation, target, heading))
                    {
                        plan = new Pending { Cargo = cargo, Definition = cargo.cargo, Target = target, Heading = heading, Friend = unit.unitName };
                        return true;
                    }
                    continue;
                }
                desired.y = Ground(desired + Vector3.up * 200f, out _);
                Vector3 unitAt = at;
                int tried = 0, tooFar = 0; float nearestTried = float.MaxValue;
                if (Resolve(desired.ToGlobalPosition(), heading, halfLength, halfWidth,
                        (point, road) =>
                        {
                            tried++; float d = Flat(point.ToLocalPosition() - unitAt); nearestTried = Mathf.Min(nearestTried, d);
                            if (d > size + BesideRadius) { tooFar++; return false; }
                            return Guided(cargo, start, velocity, rotation, point, Vector3.Dot(road, heading) < 0f ? -road : road);
                        },
                        out var spot, out var direction))
                {
                    plan = new Pending { Cargo = cargo, Definition = cargo.cargo, Target = spot, Heading = Vector3.Dot(direction, heading) < 0f ? -direction : direction, Friend = unit.unitName };
                    return true;
                }
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier container airdrop: no safe retro landing beside {unit.unitName}: spots tried {tried}, too far {tooFar}, nearest {nearestTried:F0} m.");
            }

            // no friendly unit in reach: the nearest clear spot to the natural impact point (on the water for the naval container)
            if (naval && Ground(natural + Vector3.up * 50f, out bool sea) <= Datum.LocalSeaY + .5f && sea)
            {
                var target = new Vector3(natural.x, Datum.LocalSeaY, natural.z).ToGlobalPosition();
                if (Guided(cargo, start, velocity, rotation, target, heading))
                {
                    plan = new Pending { Cargo = cargo, Definition = cargo.cargo, Target = target, Heading = heading };
                    return true;
                }
            }
            // a container needs no road: when its own unsteered fall is already a safe landing, keep it (light containers have
            // little cross-range against their drag; Courier 0.1.9 test: a fuel container missed clearings 250-600 m away)
            if ((bool)args[5])
            {
                var own = new Vector3(natural.x, Ground(natural + Vector3.up * 50f, out _), natural.z).ToGlobalPosition();
                bool steer = Guided(cargo, start, velocity, rotation, own, heading);
                // a light container that cannot even steer onto its own fall point brakes straight down instead
                plan = new Pending { Cargo = cargo, Definition = cargo.cargo, Target = own, Heading = heading, Natural = !steer && armNatural != null };
                if (steer || plan.Natural)
                    return true;
                plan = null;
            }
            if (Resolve(natural.ToGlobalPosition(), heading, halfLength, halfWidth,
                    (point, road) => Guided(cargo, start, velocity, rotation, point, Vector3.Dot(road, heading) < 0f ? -road : road),
                    out var clear, out var clearHeading))
            {
                plan = new Pending { Cargo = cargo, Definition = cargo.cargo, Target = clear, Heading = Vector3.Dot(clearHeading, heading) < 0f ? -clearHeading : clearHeading };
                return true;
            }
            CurrentAirframeDamagePlugin.Log.LogInfo("Courier container airdrop: no safe clear spot; last retro prediction: " +
                (AccessTools.Field(retroApi, "LastPrediction")?.GetValue(null) ?? "n/a"));
            return false;
        }

        private static bool Resolve(GlobalPosition near, Vector3 heading, float halfLength, float halfWidth, Func<GlobalPosition, Vector3, bool> ok,
            out GlobalPosition spot, out Vector3 direction)
        {
            var args = new object[] { near, heading, halfLength, halfWidth, null, null, ok, null };
            bool found = (bool)resolve.Invoke(null, args);
            spot = found ? (GlobalPosition)args[4] : default;
            direction = found ? (Vector3)args[5] : heading;
            return found;
        }

        private static bool NaturalSafe(MountedCargo cargo, Vector3 start, Vector3 velocity)
        {
            var args = new object[] { cargo, start, velocity, null, null, null, null };
            return (bool)predict.Invoke(null, args) && (bool)args[5];
        }

        private static bool Guided(MountedCargo cargo, Vector3 start, Vector3 velocity, Quaternion rotation, GlobalPosition target, Vector3 heading)
        {
            var args = new object[] { cargo, start, velocity, rotation, target, heading, null, null, null };
            return (bool)predictGuided.Invoke(null, args) && (bool)args[7];
        }

        private static float Ground(Vector3 from, out bool water)
        {
            bool hit = Physics.Raycast(from + Vector3.up * .1f, Vector3.down, out var info, 20000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
            water = !hit || info.point.y <= Datum.LocalSeaY + .2f;
            return hit ? Mathf.Max(info.point.y, Datum.LocalSeaY) : Datum.LocalSeaY;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static void Notify(Aircraft aircraft, string message)
        {
            try { report?.Invoke(null, new object[] { aircraft, message }); }
            catch (Exception) { }
        }
    }
}
