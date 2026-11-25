using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report for 0.1.8 (23/11/2026, screenshots from players): vehicles unloaded on the ground hit the Courier,
    // and a release is refused until the ramp and the area behind it are clear. The player must be able to release
    // whenever they want, and the AI must leave ready for its objectives.
    //
    // Bench result with this file (flight-unload-fix4-20261123-215723): three IFVs out in sequence from 58 presses,
    // none touching the Courier, each 83-97 m clear. Without it (flight-unload-base-20261123-213820) only two left.
    //
    // Bench repro (UNLOAD probe, three AFV6 IFV on a runway, release pressed every 0.5 s): the family rail carries one
    // load at a time and refused 15 presses ("Wait for the current load to clear the ramp." and "Clear the area behind
    // the ramp before unloading." for the previous vehicle). The vehicles leave the ramp in reverse, facing the
    // aircraft; the family fallback then sends them 75 m along their own forward axis, which is back through the
    // Courier: one hit the ramp at 13 m/s, one climbed the tail at 20 m/s. The Courier is not a native obstacle, so
    // the native avoidance never steers around it.
    //
    // The family DLL stays byte-identical; this file changes only the Courier:
    // 1. Release queue. A ground release refused because the rail is busy, or because a vehicle is still
    //    behind the ramp, becomes a queued release with no warning. Each queued release runs by itself as soon as the
    //    rail is free and no vehicle stands in the exit lane, through the same native LaunchMount and family checks.
    //    Any other refusal (taxiing too fast, a building behind the ramp) clears the queue and
    //    shows its usual warning.
    // 2. Exit drive. A vehicle unloaded on the ground gets, as soon as it stands on its wheels (about 3 s), a native
    //    commanded destination about 85 m behind the ramp and 22 m to one side (alternating sides, so consecutive
    //    loads do not stack; the native driver treats anything within 50 m as already reached). The family
    //    fallback that pointed back through the aircraft is removed from these vehicles. When the vehicle is within 45 m
    //    of that point (or after 40 s) native objective navigation resumes, and from then on the Courier is a native
    //    obstacle for it, so its route to objectives goes around the aircraft. Airdropped vehicles are untouched.
    internal static class CourierGroundUnload
    {
        internal const string WaitMessage = "Wait for the current load to clear the ramp.";
        internal const string ClearAreaMessage = "Clear the area behind the ramp before unloading.";
        private static Type railType, aiType;
        private static Func<object, bool> isMoving, onGround;
        private static AccessTools.FieldRef<GroundVehicle, bool> navigate, commanded;
        private static readonly Dictionary<Aircraft, Queue> Queues = new Dictionary<Aircraft, Queue>();
        [ThreadStatic] private static bool unitBlocksExit;

        internal static void Install(Harmony h)
        {
            railType = CurrentAirframeCompatibility.Family("SuperheavyGroundCargo");
            aiType = CurrentAirframeCompatibility.Family("SuperheavyCargoGroundAI");
            var report = railType == null ? null : AccessTools.Method(railType, "ReportBlocked");
            var exit = railType == null ? null : AccessTools.Method(railType, "HasClearExit");
            var moving = railType == null ? null : AccessTools.Method(railType, "IsMoving");
            if (report == null || exit == null || moving == null || aiType == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier ground unload: family cargo rail not found; queue and exit drive off.");
                return;
            }
            isMoving = rail => (bool)moving.Invoke(rail, null);
            var wheels = AccessTools.Method(railType, "HasWeightOnWheels");
            if (wheels != null) onGround = rail => (bool)wheels.Invoke(rail, null);
            navigate = AccessTools.FieldRefAccess<GroundVehicle, bool>("navigateToObjectives");
            commanded = AccessTools.FieldRefAccess<GroundVehicle, bool>("commandedDestination");
            h.Patch(AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.LaunchMount)),
                prefix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(BeforeLaunch)) { priority = Priority.First });
            h.Patch(exit, postfix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(AfterClearExit)));
            h.Patch(report, prefix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(BeforeReport)));
            h.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnUnit)),
                postfix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(AfterSpawn)) { priority = Priority.Last });
            h.Patch(AccessTools.Method(typeof(GroundVehicle), "UpdateObstacles"),
                postfix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(AfterObstacles)));
            // 0.1.8 (owner, 23/11/2026): no separate Ctrl+F10 unload key; cargo is released only with the normal weapon
            // release (select Cargo, fire), on the ground or in the air. The family rail's Update only polls that key.
            var keyPoll = AccessTools.Method(railType, "Update");
            if (keyPoll != null)
                h.Patch(keyPoll, prefix: new HarmonyMethod(typeof(CourierGroundUnload), nameof(NoUnloadKey)));
        }

        private static bool NoUnloadKey() => false;

        internal static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        private static void BeforeLaunch(WeaponStation __instance, Unit __0)
        {
            if (__instance != null && __instance.Cargo && __0 is Aircraft aircraft && IsCourier(aircraft))
                For(aircraft).Station = __instance;
        }

        private static Queue For(Aircraft aircraft)
        {
            if (!Queues.TryGetValue(aircraft, out var queue) || queue == null)
            {
                queue = aircraft.GetComponent<Queue>() ?? aircraft.gameObject.AddComponent<Queue>();
                queue.Aircraft = aircraft;
                Queues[aircraft] = queue;
            }
            return queue;
        }

        // The family exit test also stops for vehicles that will drive away by themselves; remember when that is all.
        private static void AfterClearExit(Component __instance, ref bool __result, ref string obstruction, object[] __args)
        {
            unitBlocksExit = false;
            string name = obstruction;
            if (__result || string.IsNullOrEmpty(name)) return;
            var near = Physics.OverlapSphere(__instance.transform.position, 120f, ~0, QueryTriggerInteraction.Ignore);
            unitBlocksExit = near.Any(c => c != null && c.name == name && c.GetComponentInParent<GroundVehicle>() != null);
            if (unitBlocksExit) return;
            try
            {
                if (__instance.GetComponent<Aircraft>() is Aircraft aircraft && IsCourier(aircraft) &&
                    ClearAboveGround(__instance, __args[0] as MountedCargo, __args[1], out string lowName))
                {
                    __result = true;
                    obstruction = null;
                }
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier exit clearance: " + e.Message); }
        }

        // Owner request 25/11/2026 (Spearhead as cargo): with three Spearheads (213 t) the gear sits about 0.17 m lower
        // than with three IFVs, and the family exit box (bottom 0.35 m above the rail tip, in the aircraft frame) then
        // reaches into the runway itself (flight-unload3-20261125: "Collider=terrain_naval_island1_roads" on every press,
        // no vehicle out). The same family test is repeated here with the box bottom kept 0.30 m above the ground found
        // under it; the top, the width, the lane and the sweep are the family's. A wall, building, ship or vehicle
        // still blocks; only the ground surface the vehicle drives on is no longer counted as an obstruction.
        private static System.Reflection.FieldInfo railTipField, aircraftField, shiftField, pushField;
        private static System.Reflection.MethodInfo halfSpan, tryGetLoad, releaseBounds;
        private static bool ClearAboveGround(Component rail, MountedCargo cargo, object route, out string obstruction)
        {
            obstruction = null;
            if (cargo == null || route == null) return false;
            if (railTipField == null)
            {
                railTipField = AccessTools.Field(railType, "railTip");
                aircraftField = AccessTools.Field(railType, "aircraft");
                halfSpan = AccessTools.Method(railType, "SupportHalfSpan");
                shiftField = AccessTools.Field(route.GetType(), "Shift");
                pushField = AccessTools.Field(typeof(MountedCargo), "pushDistance");
                var runtime = CurrentAirframeCompatibility.Family("SuperheavySpatialCargoRuntime");
                var plan = CurrentAirframeCompatibility.Family("SuperheavySpatialCargoPlan");
                tryGetLoad = runtime == null ? null : AccessTools.Method(runtime, "TryGetLoad");
                releaseBounds = plan == null ? null : AccessTools.Method(plan, "GetReleaseBounds");
            }
            if (railTipField == null || halfSpan == null || shiftField == null || pushField == null) return false;
            var t = rail.transform;
            int mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask | (int)PhysicsLayers.DefaultMask;
            Vector3 rear = -Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
            Quaternion rotation = Quaternion.LookRotation(rear);
            Vector3 localExit = (Vector3)railTipField.GetValue(rail);
            var extents = new Vector3(2.8f, 2.25f, .3f);
            var args = new object[] { aircraftField?.GetValue(rail), cargo, null };
            if (tryGetLoad != null && releaseBounds != null && (bool)tryGetLoad.Invoke(null, args))
            {
                var release = (Bounds)releaseBounds.Invoke(null, new[] { args[2] });
                localExit.x = release.center.x + (float)shiftField.GetValue(route);
                extents.x = release.extents.x + .15f;
            }
            Vector3 center = t.TransformPoint(localExit) + Vector3.up * 2.6f;
            float top = center.y + extents.y;
            var below = Physics.RaycastAll(center, Vector3.down, 12f, (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
                .Where(h => !h.collider.transform.IsChildOf(t)).OrderBy(h => h.distance).ToArray();
            if (below.Length == 0) return false;
            float bottom = Mathf.Max(center.y - extents.y, below[0].point.y + .30f);
            if (top - bottom < 2.5f) return false; // no room for a vehicle: keep the family refusal
            extents.y = (top - bottom) * .5f;
            center.y = bottom + extents.y;
            var overlap = Physics.OverlapBox(center, extents, rotation, mask, QueryTriggerInteraction.Ignore)
                .FirstOrDefault(c => !c.transform.IsChildOf(t));
            if (overlap != null) { obstruction = overlap.name; return false; }
            float travel = (float)halfSpan.Invoke(null, new object[] { cargo }) + 1.2f + (float)pushField.GetValue(cargo) + 2f;
            foreach (var hit in Physics.BoxCastAll(center, extents, rear, rotation, travel, mask, QueryTriggerInteraction.Ignore))
                if (!hit.collider.transform.IsChildOf(t) && Vector3.Dot(hit.normal, Vector3.up) < .75f)
                { obstruction = hit.collider.name; return false; }
            return true;
        }

        private static bool BeforeReport(Component __instance, string reason)
        {
            try
            {
                if (!(__instance.GetComponent<Aircraft>() is Aircraft aircraft) || !IsCourier(aircraft)) return true;
                var queue = For(aircraft);
                // Owner request 25/11/2026 ("faz as coisas saírem mais rápido do avião"): in the air a press refused only
                // because the rail is busy is queued as well. The queued release replays the native LaunchMount when the
                // rail is free, so the family plans that airdrop from the aircraft's position at that moment, as for a
                // press made then. Any other airdrop refusal keeps the family warning and clears the queue.
                bool ground = onGround != null && onGround(__instance);
                bool queueable = ground ? (reason == WaitMessage || (reason == ClearAreaMessage && unitBlocksExit)) : reason == WaitMessage;
                unitBlocksExit = false;
                if (queueable)
                {
                    queue.Request();
                    return false;
                }
                queue.Drop(reason);
                return true;
            }
            catch (Exception) { return true; }
        }

        internal sealed class Queue : MonoBehaviour
        {
            internal Aircraft Aircraft;
            internal WeaponStation Station;
            internal int Pending, Unloaded;
            private float next, lastNotice = float.NegativeInfinity;
            private bool replaying;
            private Component rail;

            internal void Request()
            {
                // every cargo station counts: each spatial cargo position is its own station (flight-salvo1-20261125:
                // three presses on three Spearheads queued only one, as the pressed station had one load)
                int loaded = Aircraft.weaponStations.Where(s => s.Cargo).Sum(s => s.Ammo);
                // the load on the moving rail was already spent (ammo 0), so it is not subtracted again
                // (flight-salvo2-20261125: loaded - busy released only 2 of 3)
                if (!replaying)
                    Pending = Mathf.Clamp(Pending + 1, 0, Mathf.Max(0, loaded));
                if (!replaying && GameManager.IsLocalAircraft(Aircraft) && Time.unscaledTime - lastNotice > 1.5f && SceneSingleton<AircraftActionsReport>.i != null)
                {
                    lastNotice = Time.unscaledTime;
                    SceneSingleton<AircraftActionsReport>.i.ReportText(Pending > 0 ? $"Cargo queued: {Pending} more to unload." : "All cargo is already queued for unloading.", 2f);
                }
            }

            internal void Drop(string reason)
            {
                if (Pending > 0)
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier unload queue cleared ({Pending} pending): {reason}");
                Pending = 0;
            }

            private Component Rail() => rail != null ? rail : (rail = Aircraft != null ? Aircraft.GetComponent(railType) : null);

            private void Update()
            {
                if (Pending <= 0 || Time.time < next || Aircraft == null || Aircraft.disabled) return;
                next = Time.time + .25f;
                if (Rail() == null || isMoving(rail) || VehicleInExitLane()) return;
                var station = Station != null && Station.Ammo > 0 ? Station : Aircraft.weaponStations.FirstOrDefault(s => s.Cargo && s.Ammo > 0);
                if (station == null) { Pending = 0; return; }
                replaying = true;
                try { station.LaunchMount(Aircraft, null, default(GlobalPosition)); }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier unload queue: " + e.Message); Pending = 0; }
                finally { replaying = false; }
                if (Pending > 0 && isMoving(rail))
                {
                    Pending--;
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier unload queue: next load released, {Pending} still queued.");
                }
            }

            // The family pushes a vehicle about 6-15 m clear of the ramp tip; the next one waits until that lane is empty.
            private bool VehicleInExitLane()
            {
                var gate = Aircraft.transform.Find("SHGT_CargoReleaseGate");
                if (gate == null) return false;
                var rear = -Aircraft.transform.forward;
                var center = gate.position + rear * 9f + Aircraft.transform.up * 2f;
                var hits = Physics.OverlapBox(center, new Vector3(5f, 3f, 11f), Quaternion.LookRotation(rear, Aircraft.transform.up), ~0, QueryTriggerInteraction.Ignore);
                return hits.Any(c => c != null && c.GetComponentInParent<GroundVehicle>() is GroundVehicle v && !v.disabled);
            }
        }

        private static void AfterSpawn(Unit __result, Unit __4)
        {
            try
            {
                if (!(__result is GroundVehicle vehicle) || !(__4 is Aircraft aircraft) || !IsCourier(aircraft) || aircraft.disabled) return;
                var gate = aircraft.transform.Find("SHGT_CargoReleaseGate");
                if (gate == null || (vehicle.transform.position - gate.position).sqrMagnitude > 30f * 30f) return; // not a rail unload
                if (vehicle.GetComponent("SuperheavyAirdrop") != null) return;
                if (!Physics.Raycast(vehicle.transform.position + Vector3.up, Vector3.down, 6f, PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore))
                    return; // released in flight
                var drive = vehicle.gameObject.AddComponent<ExitDrive>();
                drive.Vehicle = vehicle; drive.Aircraft = aircraft; drive.Index = ++For(aircraft).Unloaded;
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier exit drive: " + e.Message); }
        }

        internal sealed class ExitDrive : MonoBehaviour
        {
            internal GroundVehicle Vehicle;
            internal Aircraft Aircraft;
            internal int Index;
            internal bool Clear;
            internal float Radius;
            private GlobalPosition exit;
            private Vector3 point;
            private float born, started = -1f, nextCheck;

            private void Start()
            {
                // The family fallback heads along the vehicle's own forward axis, which faces the aircraft after a
                // reversing exit; this vehicle gets its route here instead.
                var family = Vehicle.GetComponent(aiType);
                if (family != null) Destroy(family);
                var root = Aircraft.transform;
                var gate = root.Find("SHGT_CargoReleaseGate");
                var rear = Vector3.ProjectOnPlane(-root.forward, Vector3.up).normalized;
                var side = Vector3.Cross(Vector3.up, rear).normalized * (Index % 2 == 1 ? 1f : -1f);
                // The native driver treats any destination within 50 m as reached, so the exit point lies beyond that.
                point = gate.position + rear * (85f + 12f * ((Index - 1) / 2)) + side * 22f;
                exit = point.ToGlobalPosition();
                Radius = Mathf.Max(20f, Aircraft.maxRadius);
                born = Time.time;
            }

            // Same test as the family recovery: the native push-out is over and the vehicle stands on its wheels. Waking
            // the vehicle any earlier, while its hull still touches the ramp, lets the solver throw it clear of the aircraft.
            private bool Settled()
            {
                var body = Vehicle.rb;
                if (body == null || body.isKinematic || body.velocity.sqrMagnitude > 16f || Time.time - born < .5f) return false;
                if (!Physics.Raycast(Vehicle.transform.position + Vector3.up * .1f, Vector3.down, out var hit, 10f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)) return false;
                float clearance = Vehicle.transform.position.y - Vehicle.definition.spawnOffset.y - hit.point.y;
                return clearance >= -.25f && clearance <= .25f;
            }

            private void Update()
            {
                if (Clear || Vehicle == null || Vehicle.disabled || Time.time < nextCheck) return;
                nextCheck = Time.time + .25f;
                if (started < 0f)
                {
                    if (!Vehicle.LocalSim || !Settled())
                    {
                        if (Time.time - born > 30f) Clear = true; // never settled: leave it to the native logic
                        return;
                    }
                    started = Time.time;
                    Vehicle.SetWheelsLocked(false); Vehicle.StowTurrets(false); Vehicle.enabled = true;
                    // A commanded destination is the only one the native driver follows without objective navigation
                    // replacing it; it is released below as soon as the vehicle is clear.
                    Vehicle.SetHoldPosition(false); navigate(Vehicle) = true;
                    if (Vehicle.UnitCommand != null) Vehicle.UnitCommand.SetDestination(exit, true);
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier exit drive: {Vehicle.definition.unitName} #{Index} settled after {Time.time - born:F1} s, heading {(point - Vehicle.transform.position).magnitude:F0} m clear of the ramp.");
                    return;
                }
                float left = FastMath.Distance(Vehicle.GlobalPosition(), exit);
                bool away = Aircraft == null || (Vehicle.transform.position - Aircraft.transform.position).magnitude > Radius + 8f;
                if (left > 45f && !(away && Time.time - started > 40f) && Time.time - started < 60f) return;
                Clear = true;
                commanded(Vehicle) = false;
                Vehicle.SetHoldPosition(false); navigate(Vehicle) = true;
                AccessTools.Method(typeof(GroundVehicle), "CheckObstacles")?.Invoke(Vehicle, null);
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier exit drive: {Vehicle.definition.unitName} #{Index} clear of the aircraft after {Time.time - started:F0} s; native objectives resumed, destination {FastMath.Distance(Vehicle.GlobalPosition(), Vehicle.GetDestination()):F0} m away.");
            }
        }

        // Once clear, the native avoidance treats the Courier that carried this vehicle like any native obstacle.
        private static void AfterObstacles(GroundVehicle __instance, List<Obstacle> ___obstacles)
        {
            if (___obstacles == null || !(__instance.GetComponent<ExitDrive>() is ExitDrive drive) || !drive.Clear || drive.Aircraft == null || drive.Aircraft.disabled) return;
            ___obstacles.Add(new Obstacle(drive.Aircraft.transform, drive.Radius, 15f));
        }
    }
}
