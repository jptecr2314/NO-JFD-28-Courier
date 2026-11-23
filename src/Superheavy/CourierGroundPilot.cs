using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier ground and rotation handling for the game's own AI pilot (runtime 0.8.8; hangar exit, braked pivot and per-sortie state 0.9.0).
    // The native taxi/takeoff states are tuned for fighters and measured on the 400 t Courier (real game, TC3):
    //  * taxi: steers the aircraft ORIGIN at the next waypoint (popped within 20 m) through AutoAim, targets up to
    //    15 + sqrt(d) m/s and holds it with a bang-bang brake; the main gear sits 25 m behind the nose wheel, so the
    //    mains cut every corner and speed oscillates 14-35 km/h with throttle and brake (spoilers) fighting;
    //  * line-up: throttle 1 - 0.1 v together with brake 0.1 v (spoilers out) for ~45 s at 8 km/h;
    //  * takeoff run: aims 300 m ahead and 50 m up from wherever the aircraft is (no centreline), so after the
    //    family's 4 m rotation limit the nose went to 22.9 degrees at 251 km/h.
    // The states keep every decision (route, clearance, queues, obstacles, yielding, waits, state changes); only the
    // control inputs are replaced afterwards for the Courier: pure pursuit of the route from the main-gear axle,
    // a speed plan for the corners ahead with throttle OR brake, centreline pursuit for line-up and the run, and a
    // rate-limited attitude rotation (2.5 deg/s to 9 deg on the wheels, then a speed-protected climb attitude).
    internal static class CourierGroundPilot
    {
        // Native differential braking only acts between 3 and 30 m/s ground speed, so corners stay above 4 m/s.
        private const float TaxiSpeed = 7f, CornerSpeed = 4f, Deceleration = .45f, LineUpSpeed = 4f, SteerGain = 1.4f;
        private const float RotationRate = 3f, RotationAttitude = 9f, ClimbAttitude = 12f;
        // Braked pivot: differential braking needs >= 3 m/s, so the pivot holds ~3.8 m/s (measured north3: at
        // 2.3 m/s the Courier orbited a waypoint 16.6 m away on a 30 m radius until the native AI gave up).
        private const float PivotSpeed = 3.8f;

        private static readonly AccessTools.FieldRef<AIPilotTaxiState, PathfindingAgent> Pathfinder = AccessTools.FieldRefAccess<AIPilotTaxiState, PathfindingAgent>("pathfinder");
        private static readonly AccessTools.FieldRef<PathfindingAgent, List<GlobalPosition>> Waypoints = AccessTools.FieldRefAccess<PathfindingAgent, List<GlobalPosition>>("waypoints");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> Yielding = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("yielding");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> CrossingWait = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("waitingAtRunwayCrossing");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> ClearanceWait = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("waitingForTakeoffClearance");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> Disembarking = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("disembarking");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, float> BrakeUrgency = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("brakeUrgency");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, Vector3> AvoidVector = AccessTools.FieldRefAccess<AIPilotTaxiState, Vector3>("obstacleAvoidVector");
        // Native give-up: AIPilotTaxiState.IsStuck (below 1.5 m/s for 30 s, or the same yaw sign for 60 s) makes the
        // AI brake, eject the crew and park (Disembark); the ejection also clears the player HUD aircraft.
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, float> StuckSpeed = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerSpeed");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, float> StuckYaw = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("stuckTimerYaw");
        private static readonly AccessTools.FieldRef<AIPilotTakeoffState, Airbase.Runway.RunwayUsage?> Usage = AccessTools.FieldRefAccess<AIPilotTakeoffState, Airbase.Runway.RunwayUsage?>("runwayUsage");
        private static readonly AccessTools.FieldRef<AIPilotTakeoffState, bool> RunStarted = AccessTools.FieldRefAccess<AIPilotTakeoffState, bool>("startedTakeoffRun");
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> Owner = AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<LandingGear, bool> Steers = AccessTools.FieldRefAccess<LandingGear, bool>("steering");
        private static readonly AccessTools.FieldRef<LandingGear, float> SteeringLock = AccessTools.FieldRefAccess<LandingGear, float>("steeringLock");

        private sealed class State
        {
            internal bool Bound;
            internal float RearZ = -1.5f, Wheelbase = 25f, LockDegrees = 45f;
            internal GlobalPosition Next, Passed;
            internal bool HasNext, HasPassed;
            internal float Integral, PitchCommand, NextTrace;
            internal bool Rotating;
            internal GameObject JoinPoint;
            internal bool ExitInit, Exiting, ExitFailed, WaitingDoors;
            internal GlobalPosition ExitStart;
            internal Vector3 ExitDir;
            internal float ExitDistance, ExitCheckTime, ExitCheckDistance, ExitHoldSince = -1f;
            internal Transform HangarRoot;
            internal Transform[] DoorLeaves = Array.Empty<Transform>();
            internal float LastTaxi = -100f;
            internal string Phase = "";
        }

        // A new taxi (the first sortie, or a later one of the same aircraft) starts from a clean state.
        private static void ResetSortie(State s)
        {
            DropJoin(s, null);
            s.ExitInit = s.Exiting = s.ExitFailed = s.WaitingDoors = false;
            s.ExitHoldSince = -1f; s.HangarRoot = null; s.DoorLeaves = Array.Empty<Transform>();
            s.Rotating = false; s.PitchCommand = 0f; s.Integral = 0f; s.Phase = "";
        }

        private static void DropJoin(State s, string reason)
        {
            if (s.JoinPoint == null) return;
            UnityEngine.Object.Destroy(s.JoinPoint);
            s.JoinPoint = null;
            if (reason != null) CurrentAirframeDamagePlugin.Log.LogInfo("Courier ground pilot: runway join point removed (" + reason + ").");
        }

        private static readonly ConditionalWeakTable<Aircraft, State> States = new ConditionalWeakTable<Aircraft, State>();
        private static readonly bool Trace = Environment.GetEnvironmentVariable("COURIER_GROUND_TRACE") == "1";
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, Airbase> TaxiBase = AccessTools.FieldRefAccess<AIPilotTaxiState, Airbase>("airbase");
        private static readonly AccessTools.FieldRef<AIPilotTakeoffState, Airbase> TakeoffBase = AccessTools.FieldRefAccess<AIPilotTakeoffState, Airbase>("airbase");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, Transform> Destination = AccessTools.FieldRefAccess<AIPilotTaxiState, Transform>("destinationPoint");
        private static Airbase tracedBase;
        private static float nextTrack;
        private static int routeHash;

        // Bench-only map trace (COURIER_GROUND_TRACE=1): airbase geometry once, the route whenever it changes and
        // the aircraft track every 0.5 s, all in metres relative to the airbase centre (floating-origin safe).
        private static void Track(Aircraft a, Airbase airbase, string phase, Transform destination, List<GlobalPosition> route)
        {
            if (!Trace || airbase == null || airbase.center == null) return;
            var origin = airbase.center.position;
            string P(Vector3 w) => (w.x - origin.x).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," + (w.z - origin.z).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            var log = CurrentAirframeDamagePlugin.Log;
            if (tracedBase != airbase)
            {
                tracedBase = airbase;
                foreach (var r in airbase.runways ?? Array.Empty<Airbase.Runway>())
                {
                    if (r == null || r.Start == null || r.End == null) continue;
                    var entries = string.Join(" ", (r.entryPoints ?? Array.Empty<Transform>()).Where(t => t).Select(t => P(t.position) + "@" + t.eulerAngles.y.ToString("F0")));
                    var exits = string.Join(" ", (r.exitPoints ?? Array.Empty<Transform>()).Where(t => t).Select(t => P(t.position)));
                    log.LogInfo($"COURIER_MAP runway {r.GetName(false)} start {P(r.Start.position)} end {P(r.End.position)} width {r.GetWidth():F0} takeoff {r.Takeoff} entries {entries} exits {exits}");
                }
            }
            if (route != null && route.Count > 0)
            {
                var here = a.GlobalPosition(); var pos = a.transform.position;
                int hash = route.Count * 397 ^ Mathf.RoundToInt(FastMath.Distance(route[route.Count - 1], here));
                var first = pos + (route[0] - here);
                hash ^= Mathf.RoundToInt(first.x) * 31 + Mathf.RoundToInt(first.z);
                if (hash != routeHash)
                {
                    routeHash = hash;
                    log.LogInfo($"COURIER_MAP route dest {(destination ? P(destination.position) : "-")} pts " + string.Join(" ", route.Select(g => P(pos + (g - here)))));
                }
            }
            if (Time.time < nextTrack) return;
            nextTrack = Time.time + .5f;
            log.LogInfo($"COURIER_MAP track {Time.time:F1} {P(a.transform.position)} hdg {a.transform.eulerAngles.y:F0} v {a.speed * 3.6f:F0} {phase}");
        }

        internal static void Install(Harmony h)
        {
            h.CreateClassProcessor(typeof(TaxiControl)).Patch();
            h.CreateClassProcessor(typeof(TakeoffControl)).Patch();
        }

        private static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        private static State For(Aircraft aircraft)
        {
            var s = States.GetValue(aircraft, _ => new State());
            if (s.Bound) return s;
            s.Bound = true;
            try
            {
                var root = aircraft.transform;
                var gear = aircraft.GetComponentsInChildren<LandingGear>(true)
                    .Concat(aircraft.partLookup.Where(p => p).SelectMany(p => p.GetComponentsInChildren<LandingGear>(true)))
                    .Where(g => g).Distinct().ToArray();
                var nose = gear.FirstOrDefault(g => Steers(g));
                var mains = gear.Where(g => !Steers(g)).ToArray();
                if (nose != null && mains.Length > 0)
                {
                    float noseZ = root.InverseTransformPoint(nose.transform.position).z;
                    s.RearZ = mains.Average(g => root.InverseTransformPoint(g.transform.position).z);
                    s.Wheelbase = Mathf.Max(5f, noseZ - s.RearZ);
                    s.LockDegrees = Mathf.Max(5f, Mathf.Abs(SteeringLock(nose)));
                }
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: main-gear axle z={s.RearZ:F1} m, wheelbase {s.Wheelbase:F1} m, nose-wheel lock {s.LockDegrees:F0} deg ({mains.Length} main legs).");
            }
            catch (Exception e)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier ground pilot: gear geometry unavailable (" + e.Message + "); using defaults.");
            }
            return s;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        private static Vector3 Axle(Aircraft a, State s) => a.transform.TransformPoint(new Vector3(0f, 0f, s.RearZ));
        private static float Attitude(Aircraft a) => Mathf.Asin(Mathf.Clamp(a.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        private static float NoseUpRate(Aircraft a) => -Vector3.Dot(a.rb.angularVelocity, a.transform.right) * Mathf.Rad2Deg;

        // Pure pursuit for a nose-wheel-steered vehicle referenced at the main-gear axle.
        private static float PursuitYaw(Aircraft a, State s, Vector3 target, out float alphaDegrees)
        {
            var axle = Axle(a, s);
            var to = Flat(target - axle);
            float ld = Mathf.Max(6f, to.magnitude);
            alphaDegrees = Vector3.SignedAngle(Flat(a.transform.forward), to, Vector3.up);
            // A point well behind cannot be reached by pure pursuit (it orbits it): turn at full lock instead.
            if (Mathf.Abs(alphaDegrees) > 75f) return Mathf.Sign(alphaDegrees);
            // The 12 main legs scrub in a turn, so the measured radius is larger than the geometric one.
            float steer = Mathf.Atan2(2f * s.Wheelbase * Mathf.Sin(alphaDegrees * Mathf.Deg2Rad), ld) * Mathf.Rad2Deg;
            return Mathf.Clamp(SteerGain * steer / s.LockDegrees, -1f, 1f);
        }

        // Throttle OR brake (the Courier brake axis also raises the spoilers), PI on speed.
        private static void Speed(ControlInputs c, State s, float target, float speed, float maxThrottle)
        {
            float error = target - speed;
            float dt = Time.fixedDeltaTime;
            s.Integral = Mathf.Clamp(s.Integral + error * .035f * dt, -.08f, .22f);
            float throttle = Mathf.Clamp(.1f + .1f * error + s.Integral, 0f, maxThrottle);
            float brake = error < -1.2f ? Mathf.Clamp((-error - 1.2f) * .3f, 0f, .7f) : 0f;
            if (brake > 0f) { throttle = 0f; s.Integral = Mathf.Min(s.Integral, 0f); }
            c.throttle = Mathf.Max(throttle, .01f);
            c.brake = brake;
        }

        private static float TurnRadius(Aircraft a)
        {
            float rate = Mathf.Abs(a.rb.angularVelocity.y);
            return rate > .005f ? a.speed / rate : 9999f;
        }

        private static void Log(Aircraft a, State s, string phase, float target, float offset, float alpha)
        {
            if (s.Phase != phase && (Trace || !phase.StartsWith("taxi", StringComparison.Ordinal)))
            {
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: {phase} at {a.speed * 3.6f:F0} km/h.");
                s.Phase = phase;
            }
            if (!Trace || Time.time < s.NextTrace) return;
            s.NextTrace = Time.time + 1f;
            var c = a.GetInputs();
            CurrentAirframeDamagePlugin.Log.LogInfo($"COURIER_GROUND {phase} v={a.speed * 3.6f:F1} target={target * 3.6f:F1} offset={offset:F1} alpha={alpha:F1} " +
                $"yaw={c.yaw:F2} thr={c.throttle:F2} brk={c.brake:F2} pitch={c.pitch:F2} att={Attitude(a):F1} cmd={s.PitchCommand:F1} alt={a.radarAlt:F1} R={TurnRadius(a):F0}");
        }

        // Direct runway entry. The taxi network ends each runway with a fighter turn-around: the route runs past the
        // threshold onto the turn pad and comes back through a ~20 m hairpin so the aircraft arrives aligned. The
        // Courier cannot make that hairpin; measured in TC3 (map088a) it drove an 85 m circle, reached the threshold
        // heading 281 deg against a 64 deg runway and then pivoted 160 deg for ~40 s. When the aircraft is already on
        // the extended centreline behind the threshold and the rest of the route is that detour, the route is
        // replaced by a join point 50 m down the runway (one turn onto the runway, on pavement), and the native
        // destination moves there so the native state still queues, checks clearance and hands over to take-off.
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> ToRunway = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("toRunway");
        private static readonly AccessTools.FieldRef<AIPilotTaxiState, Airbase.Runway> TakeoffRunway = AccessTools.FieldRefAccess<AIPilotTaxiState, Airbase.Runway>("takeoffRunway");
        private static readonly System.Reflection.FieldInfo AgentNodes = AccessTools.Field(typeof(PathfindingAgent), "nodes");
        private static readonly System.Reflection.FieldInfo AgentNext = AccessTools.Field(typeof(PathfindingAgent), "nextWaypoints");
        private const float JoinDistance = 50f;

        private static void JoinRunway(AIPilotTaxiState state, Aircraft a, State s, PathfindingAgent agent, List<GlobalPosition> list, Func<GlobalPosition, Vector3> local)
        {
            if (s.JoinPoint != null || !ToRunway(state)) return;
            var runway = TakeoffRunway(state); var destination = Destination(state);
            if (runway == null || runway.Start == null || runway.End == null || destination == null) return;
            var threshold = Flat(destination.position);
            bool fromEnd = (Flat(runway.End.position) - threshold).sqrMagnitude < (Flat(runway.Start.position) - threshold).sqrMagnitude;
            var dir = (Flat(fromEnd ? runway.Start.position : runway.End.position) - Flat(fromEnd ? runway.End.position : runway.Start.position)).normalized;
            var p = Flat(a.transform.position);
            float along = Vector3.Dot(p - threshold, dir);
            float lateral = Vector3.Cross(dir, p - threshold).y;
            float halfWidth = Mathf.Max(15f, runway.GetWidth() * .5f);
            if (along > 5f || along < -120f || Mathf.Abs(lateral) > halfWidth + 5f) return;
            float route = 0f; var previous = p;
            foreach (var g in list) { var q = local(g); route += (q - previous).magnitude; previous = q; }
            float direct = (threshold - p).magnitude;
            if (route < direct * 1.5f + 40f) return; // already a direct entry
            var join = threshold + dir * JoinDistance;
            join.y = destination.position.y;
            var marker = new GameObject("CourierRunwayJoin").transform;
            marker.SetParent(destination.parent, true);
            marker.position = join;
            marker.rotation = Quaternion.LookRotation(dir, Vector3.up);
            s.JoinPoint = marker.gameObject;
            Destination(state) = marker;
            list.Clear();
            list.Add(join.ToGlobalPosition());
            list.Add((join + dir * 100f).ToGlobalPosition());
            (AgentNodes?.GetValue(agent) as System.Collections.IList)?.Clear();
            (AgentNext?.GetValue(agent) as System.Collections.IList)?.Clear();
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: direct entry onto {runway.GetName(false)} ({route:F0} m turn-around route replaced by {direct + JoinDistance:F0} m; {-along:F0} m behind the threshold, {lateral:F0} m off the centreline).");
        }

        // Route hairpins. Some taxi routes run past a junction node and come straight back (Heartland city base,
        // fx-h-city 23/11: ~150 deg reversal 32 m ahead); a fighter turns on the spot, the 400 t Courier drove a ~30 s
        // loop around the node. When the next point is close, lies behind a reversal of more than 120 deg and the
        // point after it is nearer than going via the hairpin, the hairpin point is dropped and the aircraft turns
        // straight for the next one (a braked pivot when needed). The last two points (runway entry) are never touched.
        private static void SkipHairpin(Aircraft a, List<GlobalPosition> list, Func<GlobalPosition, Vector3> local)
        {
            if (list == null || list.Count < 3) return;
            var pos = Flat(a.transform.position);
            var p0 = local(list[0]); var p1 = local(list[1]);
            var inDir = p0 - pos; var outDir = p1 - p0;
            if (inDir.magnitude > 45f || inDir.sqrMagnitude < 1f || outDir.sqrMagnitude < 1f) return;
            float reversal = Vector3.Angle(inDir, outDir);
            if (reversal < 120f || (p1 - pos).magnitude > outDir.magnitude + 5f) return;
            list.RemoveAt(0);
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: route hairpin skipped ({reversal:F0} deg reversal {inDir.magnitude:F0} m ahead; next point {(p1 - pos).magnitude:F0} m).");
        }

        // Straight hangar exit. The native taxi turns towards the first waypoint as soon as it rolls; from the heavy
        // hangar that swung a wingtip (Heartland naval base) or an engine (Ignus airbase_SE) into the hangar walls
        // and the AI gave up. A Courier that starts its taxi stationary inside a HeavyHangar first rolls straight
        // out, holding the spawn heading, until the wing (the part that struck) has passed the door plane. The door
        // plane is the outer face of the door leaves (door_*, rails at hangar z 39.4-40.6 m, ~41 m ahead of the
        // spawned origin); the hangar_body collider also holds the 50 m apron, which made the first estimate 89 m
        // and the exit 121 m, past the first route node.
        private const float WingClearance = 17f, DoorMin = 36f, DoorMax = 46f, ExitMin = 45f, ExitMax = 75f;
        private const float DoorHalfWidth = 42f, LeafOpenX = 47f, StallSeconds = 20f, HoldLimitSeconds = 40f;

        private static void StartHangarExit(Aircraft a, State s)
        {
            if (s.ExitInit) return;
            s.ExitInit = true;
            if (a.speed > 1f) return;
            var position = a.transform.position;
            s.ExitDir = Flat(a.transform.forward).normalized;
            Hangar nearest = null; float best = 90f;
            foreach (var h in UnityEngine.Object.FindObjectsOfType<Hangar>())
            {
                if (!h) continue;
                float d = Flat(h.transform.position - position).magnitude;
                if (d < best) { best = d; nearest = h; }
            }
            if (nearest == null) return;
            var root = nearest.attachedUnit != null ? nearest.attachedUnit.transform : nearest.transform;
            var leaves = root.GetComponentsInChildren<BoxCollider>(true)
                .Where(b => b && b.name.StartsWith("door_", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (leaves.Length == 0)
            {
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: no straight hangar exit ({root.name} has no door_* leaves).");
                return;
            }
            float door = float.MinValue, doorLocalZ = float.MinValue;
            foreach (var b in leaves)
                for (int m = 0; m < 8; m++)
                {
                    var corner = b.transform.TransformPoint(b.center + Vector3.Scale(b.size * .5f,
                        new Vector3((m & 1) == 0 ? -1f : 1f, (m & 2) == 0 ? -1f : 1f, (m & 4) == 0 ? -1f : 1f)));
                    door = Mathf.Max(door, Vector3.Dot(Flat(corner - position), s.ExitDir));
                    doorLocalZ = Mathf.Max(doorLocalZ, root.InverseTransformPoint(corner).z);
                }
            var local = root.InverseTransformPoint(position);
            if (Mathf.Abs(local.x) >= DoorHalfWidth || local.z >= doorLocalZ)
            {
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: no straight hangar exit (origin at hangar x={local.x:F1} z={local.z:F1} m is not inside {root.name}).");
                return;
            }
            if (door < DoorMin || door > DoorMax)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning($"Courier ground pilot: no straight hangar exit (door plane {door:F1} m ahead is outside {DoorMin:F0}-{DoorMax:F0} m; hangar z={local.z:F1} m).");
                return;
            }
            s.HangarRoot = root;
            s.DoorLeaves = leaves.Select(b => b.transform).ToArray();
            s.ExitStart = a.GlobalPosition();
            s.ExitDistance = Mathf.Clamp(door + WingClearance, ExitMin, ExitMax);
            s.ExitCheckTime = Time.time; s.ExitCheckDistance = 0f; s.ExitHoldSince = -1f;
            s.Exiting = true;
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: hangar exit straight ahead for {s.ExitDistance:F1} m (door plane {door:F1} m ahead; origin at hangar x={local.x:F1} z={local.z:F1} m).");
        }

        // All leaves slid into their pockets (closed at |x| 7/21/35 m, open at 49 m).
        private static bool DoorsOpen(State s)
        {
            if (s.HangarRoot == null) return true;
            foreach (var leaf in s.DoorLeaves)
                if (leaf && Mathf.Abs(s.HangarRoot.InverseTransformPoint(leaf.position).x) < LeafOpenX) return false;
            return true;
        }

        // Inside the hangar the AI never gets the controls back. Native waits and obstacles hold the aircraft
        // straight and braked. Closed leaves, or no progress for 20 s, hold it braked with the native give-up timers
        // reset for up to 40 s; after that the hold stays, the native give-up is allowed again and an error is logged.
        private static bool HangarExit(AIPilotTaxiState state, Aircraft a, State s, ControlInputs c, List<GlobalPosition> list, Func<GlobalPosition, Vector3> local, bool nativeHold)
        {
            if (!s.Exiting) return false;
            float travelled = Vector3.Dot(Flat(a.transform.position) - local(s.ExitStart), s.ExitDir);
            if (travelled >= s.ExitDistance)
            {
                s.Exiting = false;
                float next = list != null && list.Count > 0 ? Vector3.SignedAngle(Flat(a.transform.forward), local(list[0]) - Flat(a.transform.position), Vector3.up) : 0f;
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: hangar exit done after {travelled:F1} m at {a.speed * 3.6f:F0} km/h; next route point {next:F0} deg off the nose.");
                return false;
            }
            float error = Vector3.SignedAngle(s.ExitDir, Flat(a.transform.forward), Vector3.up);
            c.yaw = Mathf.Clamp(-error * .1f, -.3f, .3f);
            c.pitch = 0f;
            if (nativeHold)
            {
                s.ExitCheckTime = Time.time; s.ExitCheckDistance = travelled;
                c.throttle = .01f; c.brake = Mathf.Max(c.brake, .6f);
                Log(a, s, "hangar-exit-wait", 0f, s.ExitDistance - travelled, error);
                return true;
            }
            bool open = DoorsOpen(s);
            if (travelled - s.ExitCheckDistance >= 2f || (open && s.WaitingDoors))
            {
                s.ExitCheckDistance = travelled; s.ExitCheckTime = Time.time; s.WaitingDoors = false; s.ExitHoldSince = -1f;
            }
            if (!open || Time.time - s.ExitCheckTime > StallSeconds)
            {
                if (s.ExitHoldSince < 0f)
                {
                    s.ExitHoldSince = Time.time;
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ground pilot: hangar exit holding at {travelled:F1} m ({(open ? "no progress for 20 s" : "door leaves not open")}).");
                }
                if (!open) s.WaitingDoors = true;
                if (Time.time - s.ExitHoldSince <= HoldLimitSeconds) { StuckSpeed(state) = 0f; StuckYaw(state) = 0f; }
                else if (!s.ExitFailed)
                {
                    s.ExitFailed = true;
                    CurrentAirframeDamagePlugin.Log.LogError($"Courier ground pilot: hangar exit blocked for {HoldLimitSeconds:F0} s at {travelled:F1} m (doors open: {open}); holding in the hangar.");
                }
                c.yaw = 0f; c.throttle = .01f; c.brake = 1f;
                Log(a, s, "hangar-exit-hold", 0f, s.ExitDistance - travelled, error);
                return true;
            }
            Speed(c, s, 3f, a.speed, .4f);
            Log(a, s, "hangar-exit", 3f, s.ExitDistance - travelled, error);
            return true;
        }

        [HarmonyPatch(typeof(AIPilotTaxiState), "FixedUpdateState")]
        private static class TaxiControl
        {
            private static void Postfix(AIPilotTaxiState __instance)
            {
                try { Run(__instance); }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier ground pilot (taxi): " + e.Message); }
            }

            private static void Run(AIPilotTaxiState state)
            {
                var a = Owner(state);
                if (!IsCourier(a) || a.rb == null || a.disabled || !a.LocalSim) return;
                var s = For(a);
                if (Time.time - s.LastTaxi > 5f) ResetSortie(s);
                s.LastTaxi = Time.time;
                var agent = Pathfinder(state);
                var list = agent != null ? Waypoints(agent) : null;
                Track(a, TaxiBase(state), "taxi", Destination(state), list);
                if (s.JoinPoint != null && Destination(state) != s.JoinPoint.transform) DropJoin(s, "the native taxi chose another destination");
                StartHangarExit(a, s);
                if (Disembarking(state)) return;
                var c = a.GetInputs();
                var here = a.GlobalPosition();
                var origin = a.transform.position;
                Vector3 Local(GlobalPosition g) => Flat(origin + (g - here));
                // Waits, yielding, obstacles, clearance and the end of the route stay with the native state (inside
                // the hangar they only hold the aircraft straight and braked).
                bool nativeHold = list == null || list.Count == 0 || Yielding(state) || CrossingWait(state) || ClearanceWait(state) ||
                    BrakeUrgency(state) > .05f || AvoidVector(state).sqrMagnitude > .01f;
                if (HangarExit(state, a, s, c, list, Local, nativeHold)) return;
                if (nativeHold) return;
                if (c.throttle <= .011f && c.brake >= .5f && a.speed < 1f) return; // native hold
                JoinRunway(state, a, s, agent, list, Local);
                SkipHairpin(a, list, Local);
                // Steering stays native (it follows re-planned routes and turns around reliably); the Courier gets a
                // speed plan for the corners ahead instead of the 15-30 m/s target held with a bang-bang brake.
                float v = a.speed, distance = 0f, plan = TaxiSpeed;
                var previous = Flat(origin);
                var heading = Flat(a.transform.forward);
                for (int i = 0; i < list.Count && distance < 160f; i++)
                {
                    var point = Local(list[i]);
                    var leg = point - previous;
                    distance += leg.magnitude;
                    if (i + 1 < list.Count)
                    {
                        var outDir = Local(list[i + 1]) - point;
                        var inDir = leg.sqrMagnitude > 1f ? leg : heading;
                        if (outDir.sqrMagnitude > .01f)
                        {
                            float turn = Vector3.Angle(inDir, outDir);
                            float corner = Mathf.Lerp(TaxiSpeed, CornerSpeed, Mathf.InverseLerp(10f, 90f, turn));
                            plan = Mathf.Min(plan, Mathf.Sqrt(corner * corner + 2f * Deceleration * Mathf.Max(0f, distance - 15f)));
                        }
                    }
                    previous = point;
                }
                // End of the route (runway entry / hold) and any turn the nose still has to make now.
                plan = Mathf.Min(plan, Mathf.Sqrt(CornerSpeed * CornerSpeed + 2f * Deceleration * Mathf.Max(0f, distance - 15f)));
                float alpha = Vector3.SignedAngle(heading, Local(list[0]) - Flat(origin), Vector3.up);
                // Tight turns stay fully native: the 12 main legs only let the Courier pivot with the brakes on
                // (measured: without them it orbits a waypoint 20-30 m away at full lock), and the native state
                // keeps throttle above zero while braking, so the spoilers stay closed.
                if (Mathf.Abs(alpha) > 25f || Mathf.Abs(c.yaw) > .45f)
                {
                    s.Integral = 0f;
                    c.pitch = 0f; // as in the line-up pivot
                    // ...but not at the native corner speed (up to ~28 km/h measured, the 400 t aircraft then runs
                    // ~20 m wide of the taxiway): above 1.3x corner speed lift the throttle and brake firmly.
                    if (v > CornerSpeed * 1.3f) { c.throttle = .01f; c.brake = Mathf.Max(c.brake, Mathf.Clamp01((v - CornerSpeed) * .25f + .3f)); }
                    // A waypoint well off the nose (it fell inside the turning circle) makes the native state orbit
                    // it at full lock. Pivot instead: power against the brakes as in the native line-up, held at
                    // ~3.8 m/s so the differential braking (3-30 m/s) acts; the throttle stays above zero so the
                    // spoilers stay closed.
                    else if (Mathf.Abs(alpha) > 45f && Mathf.Abs(c.yaw) > .8f)
                    {
                        c.throttle = Mathf.Clamp(.8f + .4f * (PivotSpeed - v), .1f, 1f);
                        c.brake = Mathf.Clamp01(.1f * v);
                        Log(a, s, "taxi-pivot", PivotSpeed, distance, alpha);
                        return;
                    }
                    Log(a, s, "taxi-turn", CornerSpeed, distance, alpha);
                    return;
                }
                Speed(c, s, plan, v, .45f);
                Log(a, s, "taxi", plan, distance, alpha);
            }
        }

        [HarmonyPatch(typeof(AIPilotTakeoffState), "FixedUpdateState")]
        private static class TakeoffControl
        {
            private static void Postfix(AIPilotTakeoffState __instance)
            {
                try { Run(__instance); }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier ground pilot (takeoff): " + e.Message); }
            }

            private static void Run(AIPilotTakeoffState state)
            {
                var a = Owner(state);
                if (!IsCourier(a) || a.rb == null || a.disabled || !a.LocalSim) return;
                var usage = Usage(state);
                Track(a, TakeoffBase(state), RunStarted(state) ? "run" : "line-up", null, null);
                if (!usage.HasValue || usage.Value.Runway == null || a.radarAlt > 75f) return;
                var s = For(a);
                DropJoin(s, null);
                var c = a.GetInputs();
                var runway = usage.Value.Runway;
                var dir = Flat(runway.GetDirection(usage.Value.Reverse)).normalized;
                float v = a.speed;
                bool wheels = a.radarAlt < 1f;
                float offset = 0f, alpha = 0f;
                if (wheels)
                {
                    var axle = Axle(a, s);
                    // Lateral distance from the centreline (the nearest runway point is clamped to the threshold
                    // while the aircraft is still on the turn pad behind it).
                    var near = runway.GetNearestPoint(axle, false);
                    var along = Vector3.Dot(Flat(axle - near), dir);
                    var line = near + dir * along;
                    offset = Flat(axle - line).magnitude;
                    if (!RunStarted(state) && v < 20f)
                    {
                        // Line-up turn stays native (braked pivot onto the runway).
                        alpha = Vector3.SignedAngle(Flat(a.transform.forward), dir, Vector3.up);
                        s.Rotating = false; s.PitchCommand = Attitude(a);
                        c.pitch = 0f;
                        Log(a, s, "line-up", LineUpSpeed, offset, alpha);
                        return;
                    }
                    // On the runway, hold the centreline (pursuit from the main-gear axle, lookahead growing with
                    // speed); the native run only keeps the heading from wherever the run started.
                    if (offset < 25f && v > 8f)
                    {
                        float yaw = PursuitYaw(a, s, line + dir * Mathf.Clamp(40f + 3f * v, 60f, 300f), out alpha);
                        c.yaw = Mathf.Clamp(yaw, -.35f, .35f);
                    }
                }
                // Rotation and initial climb: attitude command rate-limited at 2.5 deg/s.
                float mass = a.rb.mass;
                foreach (var part in a.partLookup) if (part && part.rb && part.rb != a.rb && !part.rb.isKinematic && !part.IsDetached()) mass += part.rb.mass;
                float rotate = Mathf.Clamp(58f * Mathf.Sqrt(mass / 400000f), 40f, 66f);
                float safe = rotate * 1.15f;
                float attitude = Attitude(a);
                if (!s.Rotating && !RunStarted(state)) return;
                if (!s.Rotating)
                {
                    s.PitchCommand = attitude;
                    if (wheels && v < rotate) { c.pitch = 0f; Log(a, s, "takeoff-run", rotate, offset, alpha); return; }
                    s.Rotating = true;
                }
                float goal = wheels ? RotationAttitude : Mathf.Clamp(ClimbAttitude - 1.2f * Mathf.Max(0f, safe - v), 5f, ClimbAttitude);
                s.PitchCommand = Mathf.MoveTowards(s.PitchCommand, goal, RotationRate * Time.fixedDeltaTime);
                float noseUp = .2f * (s.PitchCommand - attitude) - .12f * NoseUpRate(a);
                c.pitch = -Mathf.Clamp(noseUp, -.6f, 1f); // negative native pitch raises the nose
                if (!wheels) c.yaw = Mathf.Clamp(c.yaw, -.3f, .3f);
                Log(a, s, wheels ? "rotation" : "initial-climb", goal, offset, alpha);
            }
        }
    }
}
