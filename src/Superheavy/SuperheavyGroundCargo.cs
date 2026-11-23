using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundCargo : MonoBehaviour
    {
        private const string GateName = "SHGT_CargoReleaseGate";
        private const float MaxUnloadSpeed = 3f;
        private const float RunoutTransition = 1.2f;
        private const float AirRampClearance = .6f;
        private const float AirDoorWait = 4.1f;
        private static readonly int GroundMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
        private static readonly AccessTools.FieldRef<MountedCargo, bool> Detached =
            AccessTools.FieldRefAccess<MountedCargo, bool>("detached");
        private static readonly AccessTools.FieldRef<MountedCargo, float> PushDistance =
            AccessTools.FieldRefAccess<MountedCargo, float>("pushDistance");
        private static readonly AccessTools.FieldRef<MountedCargo, float> RailSpeed =
            AccessTools.FieldRefAccess<MountedCargo, float>("railSpeed");
        private static readonly AccessTools.FieldRef<MountedCargo, float> RailDelay =
            AccessTools.FieldRefAccess<MountedCargo, float>("railDelay");
        private static readonly AccessTools.FieldRef<MountedCargo, Vector3> RailVector =
            AccessTools.FieldRefAccess<MountedCargo, Vector3>("railVector");
        private static readonly AccessTools.FieldRef<WeaponStation, int> WeaponIndex =
            AccessTools.FieldRefAccess<WeaponStation, int>("weaponIndex");
        private static readonly ConditionalWeakTable<MountedCargo, StowedPose> StowedPoses =
            new ConditionalWeakTable<MountedCargo, StowedPose>();
        private static readonly ConditionalWeakTable<Rigidbody, MountedCargo> AirSpawnedBodies =
            new ConditionalWeakTable<Rigidbody, MountedCargo>();
        private static ManualLogSource log;
        private static ConfigEntry<KeyboardShortcut> unloadShortcut;

        private Aircraft aircraft;
        private LandingGear[] gearContacts;
        private Transform railStart;
        private Transform railFold;
        private Transform railGate;
        private Vector3 railTip;
        private MountedCargo moving;
        private MountedCargo pendingCargo;
        private bool pendingAir;
        private SpatialCargoRoute pendingRoute;
        private SpatialCargoRoute travelRoute;
        private Vector3 stowedPosition;
        private Vector3 envelopeOffset;
        private float envelopeHalfLength;
        private float turnRadius;
        private bool airTravel;
        private float stowedHeight;
        private Quaternion stowedRotation;
        private float supportHalfSpan;
        private float lastBlockedMessage = float.NegativeInfinity;

        private sealed class StowedPose
        {
            internal Quaternion LocalRotation;
        }

        internal static void Install(Harmony harmony, ManualLogSource logger, ConfigFile config)
        {
            log = logger;
            unloadShortcut = config.Bind("Cargo", "Unload on ground", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Unload the next cargo at the rear ramp. You can also select Cargo and use the regular weapon release control.");
            harmony.CreateClassProcessor(typeof(CargoAttachPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CargoOrderPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CargoRearmPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GroundReleasePatch)).Patch();
            harmony.CreateClassProcessor(typeof(RailTrackingPatch)).Patch();
            harmony.CreateClassProcessor(typeof(NativeSpawnPatch)).Patch();
            harmony.CreateClassProcessor(typeof(AirPushPatch)).Patch();
        }

        private static bool IsPrototype(Aircraft aircraft) => GameManager.gameState == GameState.SinglePlayer &&
            aircraft != null && aircraft.definition != null && aircraft.definition.jsonKey == SuperheavyGroundPhysicsPlugin.AircraftKey;

        [HarmonyPatch(typeof(Aircraft), "OnStartClient")]
        private static class CargoAttachPatch
        {
            private static void Postfix(Aircraft __instance)
            {
                if (IsPrototype(__instance))
                    GetRail(__instance);
            }
        }

        [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.AttachToHardpoint))]
        private static class CargoOrderPatch
        {
            private static void Postfix(MountedCargo __instance, Aircraft __0, Hardpoint __1)
            {
                if (!IsPrototype(__0) || __1.GetCargoDoor()?.name != GateName)
                    return;

                __instance.priority = __1.HardpointIndex;
                StowedPoses.GetValue(__instance, _ => new StowedPose()).LocalRotation = __instance.transform.localRotation;
            }
        }

        [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.Rearm))]
        private static class CargoRearmPatch
        {
            private static void Postfix(MountedCargo __instance)
            {
                if (__instance.GetAmmoLoaded() > 0 && StowedPoses.TryGetValue(__instance, out var pose))
                    __instance.transform.localRotation = pose.LocalRotation;
            }
        }

        [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.LaunchMount))]
        private static class GroundReleasePatch
        {
            private static bool Prefix(WeaponStation __instance, Unit __0, out SuperheavyGroundCargo __state)
            {
                __state = null;
                if (!__instance.Cargo || !(__0 is Aircraft aircraft) || !IsPrototype(aircraft))
                    return true;

                var rail = GetRail(aircraft);
                if (rail == null)
                    return false;

                var cargo = CurrentCargo(__instance);
                if (cargo == null)
                    return false;
                if (rail.IsMoving())
                {
                    rail.ReportBlocked("Wait for the current load to clear the ramp.");
                    return false;
                }

                if (!rail.TryPlanStowedRoute(__instance, cargo, out var route))
                {
                    rail.ReportBlocked("Another load is blocking the path to the ramp.", air: !rail.HasWeightOnWheels());
                    return false;
                }

                if (aircraft.rb != null && !rail.HasWeightOnWheels())
                {
                    if (!GameManager.IsLocalAircraft(aircraft))
                        return false;

                    if (!rail.TryMeasureAirRelease(cargo, route, out var release, out var velocity, out float delay))
                    {
                        rail.ReportBlocked("This cargo has no supported air release.", air: true);
                        return false;
                    }
                    if (!SuperheavyAirdrop.TryArm(aircraft, cargo, release, velocity, delay, out string reason))
                    {
                        rail.ReportBlocked(reason ?? "Airdrop not available.", air: true);
                        return false;
                    }

                    rail.pendingCargo = cargo;
                    rail.pendingAir = true;
                    rail.pendingRoute = route;
                    __state = rail;
                    log.LogInfo($"Superheavy air cargo release accepted: {cargo.cargo.unitName}; rail ETA {delay:F1}s.");
                    return true;
                }

                if (aircraft.rb == null || Vector3.Dot(aircraft.transform.up, Vector3.up) < .98f ||
                    Mathf.Abs(aircraft.rb.velocity.y) > .75f ||
                    SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb) > MaxUnloadSpeed)
                {
                    rail.ReportBlocked("Stop or taxi below 11 km/h on level ground before unloading.");
                    return false;
                }

                if (!rail.HasClearExit(cargo, route, out var obstruction))
                {
                    rail.ReportBlocked("Clear the area behind the ramp before unloading.", obstruction);
                    return false;
                }

                rail.pendingCargo = cargo;
                rail.pendingAir = false;
                rail.pendingRoute = route;
                __state = rail;
                log.LogInfo($"Superheavy ground cargo release accepted: {cargo.cargo.unitName}; " +
                    $"speed={SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb) * 3.6f:F1} km/h.");
                return true;
            }

            private static void Finalizer(SuperheavyGroundCargo __state)
            {
                if (__state != null)
                    __state.pendingCargo = null;
            }
        }

        [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.Fire))]
        private static class RailTrackingPatch
        {
            private static void Postfix(MountedCargo __instance, Unit __0)
            {
                if (!(__0 is Aircraft aircraft) || !IsPrototype(aircraft) || __instance.GetAmmoLoaded() > 0 ||
                    !StowedPoses.TryGetValue(__instance, out _))
                    return;

                var rail = GetRail(aircraft);
                if (rail == null || rail.IsMoving())
                    return;

                bool planned = rail.pendingCargo == __instance;
                rail.BeginTravel(__instance, planned && rail.pendingAir, planned ? rail.pendingRoute : default);
                log.LogInfo($"Superheavy cargo moving toward rear ramp: {__instance.cargo.unitName}.");
            }
        }

        [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnUnit))]
        private static class NativeSpawnPatch
        {
            private static void Prefix(UnitDefinition __0, ref Vector3 __1, ref Quaternion __2, ref Vector3 __3,
                Unit __4, out SuperheavyGroundCargo __state)
            {
                __state = null;
                if (!(__4 is Aircraft aircraft) || !IsPrototype(aircraft) ||
                    !aircraft.TryGetComponent<SuperheavyGroundCargo>(out var rail) ||
                    rail.moving == null || rail.moving.gameObject.activeInHierarchy || Detached(rail.moving) || rail.moving.cargo != __0)
                    return;

                Vector3 local = rail.transform.InverseTransformPoint(__1);
                if (local.z > rail.railGate.localPosition.z + .75f)
                    return;
                if (rail.travelRoute.Distance > 0f)
                    local.x = rail.stowedPosition.x + rail.travelRoute.Shift;

                if (rail.airTravel)
                {
                    rail.SampleAirRailPose(local, rail.stowedHeight, rail.stowedRotation, rail.supportHalfSpan,
                        out __1, out __2);
                    rail.moving.transform.SetPositionAndRotation(__1, __2);
                    __3 = aircraft.rb.GetPointVelocity(__1) +
                        rail.moving.transform.TransformVector(RailVector(rail.moving)) * RailSpeed(rail.moving);
                    __state = rail;
                    return;
                }

                float rearwardSpeed = (__3 - aircraft.rb.GetPointVelocity(__1)).magnitude;
                local.z = Mathf.Min(local.z, rail.railGate.localPosition.z);
                var ground = rail.ReadRunoutGround(local.x);
                rail.SampleDeckPose(local, ground, out __1, out __2);
                __1 = KeepNativeColliderAboveGround(__0, __1, __2, ground);
                __3 = aircraft.rb.GetPointVelocity(__1) - (__2 * Vector3.forward) * rearwardSpeed;
                // PushCargoOut still uses this now-hidden mount's position and forward.
                rail.moving.transform.SetPositionAndRotation(__1, __2);
                __state = rail;
            }

            private static void Postfix(Unit __result, SuperheavyGroundCargo __state)
            {
                if (__state == null)
                    return;

                bool air = __state.airTravel;
                MountedCargo cargo = __state.moving;
                __state.ClearTravel();
                SuperheavyCargoGroundAI.Track(__result);
                if (air && __result is GroundVehicle vehicle)
                {
                    var body = vehicle.GetComponent<Rigidbody>();
                    if (body != null)
                        AirSpawnedBodies.GetValue(body, _ => cargo);
                    try
                    {
                        SuperheavyAirdrop.AcceptSpawn(__state.aircraft, cargo, vehicle);
                    }
                    catch (Exception error)
                    {
                        // The stock launch already consumed this mount. Keep the
                        // native vehicle handoff alive even if guidance fails.
                        log.LogError($"Superheavy airdrop guidance failed at separation: {error}");
                    }
                }
                if (__result != null)
                    log.LogInfo($"Superheavy cargo {(air ? "air released" : "unloaded")} as a native unit: {__result.definition.unitName}.");
            }
        }

        [HarmonyPatch(typeof(MountedCargo), "PushCargoOut")]
        private static class AirPushPatch
        {
            private static bool Prefix(MountedCargo __instance, Rigidbody __0)
            {
                if (__0 == null || !AirSpawnedBodies.TryGetValue(__0, out var mount) || mount != __instance)
                    return true;

                AirSpawnedBodies.Remove(__0);
                var vehicle = __0.GetComponent<GroundVehicle>();
                var collider = __0.GetComponent<BoxCollider>();
                if (vehicle == null || collider == null)
                    return true;

                // The whole vehicle is beyond the ramp. Hand it to the native
                // suspension and preserve the actual velocity at separation.
                __instance.ActivateCargoVehicle(vehicle, collider, collider.sharedMaterial);
                return false;
            }
        }

        private static MountedCargo CurrentCargo(WeaponStation station)
        {
            if (station == null || !station.Cargo)
                return null;
            int index = WeaponIndex(station);
            if (index < 0 || index >= station.Weapons.Count || !(station.Weapons[index] is MountedCargo cargo) ||
                cargo == null || cargo.GetAmmoLoaded() <= 0)
                return null;
            return cargo;
        }

        private static SuperheavyGroundCargo GetRail(Aircraft aircraft)
        {
            if (aircraft.TryGetComponent<SuperheavyGroundCargo>(out var existing))
                return existing;

            var start = aircraft.transform.Find("SHGT_CargoRailStart");
            var fold = aircraft.transform.Find("SHGT_CargoRailFold");
            var gate = aircraft.transform.Find(GateName);
            if (start == null || fold == null || gate == null)
            {
                log.LogWarning("Superheavy cargo rail frames are missing.");
                return null;
            }

            var rail = aircraft.gameObject.AddComponent<SuperheavyGroundCargo>();
            rail.aircraft = aircraft;
            rail.gearContacts = SuperheavyNativeDamage.Components<LandingGear>(aircraft);
            rail.railStart = start;
            rail.railFold = fold;
            rail.railGate = gate;
            rail.railTip = gate.localPosition;
            return rail;
        }

        private bool IsMoving() => moving != null && moving.gameObject.activeInHierarchy && !Detached(moving);

        private bool HasWeightOnWheels() =>
            gearContacts.Any(gear => gear != null && gear.enabled && gear.WeightOnWheel(.01f));

        private void BeginTravel(MountedCargo cargo, bool air, SpatialCargoRoute route)
        {
            moving = cargo;
            airTravel = air;
            travelRoute = route;
            stowedPosition = transform.InverseTransformPoint(cargo.transform.position);
            stowedHeight = stowedPosition.y;
            stowedRotation = Quaternion.Inverse(transform.rotation) * cargo.transform.rotation;
            supportHalfSpan = SupportHalfSpan(cargo);
            if (route.Distance > 0f && SuperheavySpatialCargoRuntime.TryGetLoad(aircraft, cargo, out var load))
            {
                envelopeOffset = SuperheavySpatialCargoPlan.GetEnvelopeCenter(load) - stowedPosition;
                envelopeHalfLength = load.Item.Length * .5f;
                turnRadius = Mathf.Abs(envelopeOffset.x) + Mathf.Abs(envelopeOffset.z) +
                    envelopeHalfLength + load.Item.Width * .5f;
            }
            if (airTravel)
            {
                var gate = AirGate(cargo, stowedHeight, stowedRotation);
                gate.x += route.Shift;
                railGate.localPosition = gate;
                return;
            }
            // The physical tip stays put. Continue the invisible rail until the front
            // support reaches the ground, then hand the vehicle to the stock spawner.
            float exitX = route.Distance > 0f ? stowedPosition.x + route.Shift : railTip.x;
            railGate.localPosition = new Vector3(exitX, railTip.y + stowedHeight - railStart.localPosition.y,
                railTip.z - supportHalfSpan - RunoutTransition);
        }

        private void ClearTravel()
        {
            moving = null;
            airTravel = false;
            travelRoute = default;
            supportHalfSpan = 0f;
            if (railGate != null)
                railGate.localPosition = railTip;
        }

        private void OnDisable() => ClearTravel();

        private static float SupportHalfSpan(MountedCargo cargo)
        {
            var body = cargo.cargo.unitPrefab.GetComponent<BoxCollider>();
            // Approximate the supports inside the native collision hull, excluding overhangs.
            return body != null ? Mathf.Clamp(body.size.z * .38f, .55f, 3.2f) : 1.2f;
        }

        private void Update()
        {
            if (unloadShortcut == null || !unloadShortcut.Value.IsDown() || !GameManager.IsLocalAircraft(aircraft))
                return;

            if (!HasWeightOnWheels())
            {
                ReportBlocked("Use the normal weapon release for an airdrop.", air: true);
                return;
            }

            var station = aircraft.weaponStations.FirstOrDefault(candidate => candidate.Cargo);
            if (station == null || station.Ammo <= 0)
            {
                ReportBlocked("No cargo loaded.");
                return;
            }
            if (!station.Ready() || station.SalvoInProgress)
            {
                ReportBlocked("Wait for the cargo station to finish its release cycle.");
                return;
            }

            station.LaunchMount(aircraft, null, default(GlobalPosition));
        }

        private void LateUpdate()
        {
            if (!IsMoving())
            {
                if (supportHalfSpan > 0f)
                    ClearTravel();
                return;
            }

            Vector3 local = transform.InverseTransformPoint(moving.transform.position);
            Quaternion turn = AlignOnDeck(ref local);
            if (airTravel)
            {
                SampleAirRailPose(local, stowedHeight, stowedRotation, supportHalfSpan,
                    out var airPosition, out var airRotation);
                moving.transform.SetPositionAndRotation(airPosition, turn * airRotation);
                return;
            }

            Plane ground = local.z - supportHalfSpan <= railTip.z + RunoutTransition
                ? ReadRunoutGround(local.x)
                : new Plane(Vector3.up, transform.TransformPoint(railTip));
            Vector3 exit = railGate.localPosition;
            exit.x = travelRoute.Distance > 0f ? stowedPosition.x + travelRoute.Shift : local.x;
            exit.y = LocalPlaneHeight(ground, exit.x, exit.z) + stowedHeight - railStart.localPosition.y;
            railGate.localPosition = exit;
            SampleDeckPose(local, ground, out var position, out var rotation);
            moving.transform.SetPositionAndRotation(position, turn * rotation);
        }

        private Quaternion AlignOnDeck(ref Vector3 local)
        {
            if (travelRoute.Distance <= 0f)
                return Quaternion.identity;

            float shift = travelRoute.Sample(stowedPosition.z - local.z, out float slope, out float lateralRoom);
            // Keep the rotated envelope inside the swept deck corridor, even
            // when parked against a wall. Do not overwrite native rail progress.
            float maxYaw = Mathf.Min(lateralRoom / envelopeHalfLength, SpatialCargoRoute.TurnPadding / turnRadius);
            float yaw = Mathf.Clamp(-Mathf.Atan(slope), -maxYaw, maxYaw) * Mathf.Rad2Deg;
            var localTurn = Quaternion.AngleAxis(yaw, Vector3.up);
            local.x = stowedPosition.x + shift + envelopeOffset.x - (localTurn * envelopeOffset).x;
            return Quaternion.AngleAxis(yaw, transform.up);
        }

        private bool TryMeasureAirRelease(MountedCargo cargo, SpatialCargoRoute route,
            out Vector3 release, out Vector3 velocity, out float delay)
        {
            release = velocity = default;
            delay = 0f;
            if (cargo.cargo == null || cargo.cargo.unitPrefab == null ||
                cargo.cargo.unitPrefab.GetComponent<GroundVehicle>() == null ||
                cargo.cargo.unitPrefab.GetComponent<BoxCollider>() == null ||
                !StowedPoses.TryGetValue(cargo, out _) || cargo.transform.parent == null ||
                railGate.GetComponent<CargoRamp>() is not CargoRamp ramp)
                return false;

            var local = transform.InverseTransformPoint(cargo.transform.position);
            var rotation = Quaternion.Inverse(transform.rotation) * cargo.transform.rotation;
            var gate = AirGate(cargo, local.y, rotation);
            var railVelocity = RailVector(cargo) * RailSpeed(cargo);
            var slide = transform.InverseTransformVector(cargo.transform.parent.TransformVector(railVelocity));
            if (slide.z > -.1f)
                return false;

            float travel = Mathf.Max(0f, local.z - gate.z) / -slide.z;
            delay = Mathf.Max(0f, RailDelay(cargo)) + (ramp.IsOpen() ? 0f : AirDoorWait) + travel;
            gate.x = local.x + (route.Distance > 0f ? route.Shift : slide.x * travel);
            SampleAirRailPose(gate, local.y, rotation, SupportHalfSpan(cargo), out release, out var releaseRotation);
            velocity = aircraft.rb.GetPointVelocity(release) +
                releaseRotation * Vector3.Scale(cargo.transform.lossyScale, railVelocity);
            return true;
        }

        private Vector3 AirGate(MountedCargo cargo, float height, Quaternion rotation)
        {
            // The game releases when the mount crosses this frame. Wait until the
            // whole front edge of the vehicle has passed the physical ramp tip.
            var collider = cargo.cargo.unitPrefab.GetComponent<BoxCollider>();
            float slope = (railFold.localPosition.y - railTip.y) / (railFold.localPosition.z - railTip.z);
            var rampAttitude = Quaternion.AngleAxis(-Mathf.Atan(slope) * Mathf.Rad2Deg, Vector3.right);
            Vector3 axis = Quaternion.Inverse(rampAttitude * rotation) * Vector3.forward;
            Vector3 extents = collider.size * .5f;
            float frontReach = Vector3.Dot(collider.center, axis) +
                Mathf.Abs(axis.x) * extents.x + Mathf.Abs(axis.y) * extents.y + Mathf.Abs(axis.z) * extents.z;
            var local = transform.InverseTransformPoint(cargo.transform.position);
            local.z = railTip.z - Mathf.Max(.55f, frontReach) - AirRampClearance;
            local.y = AirDeckHeight(local.z) + height - railStart.localPosition.y;
            return local;
        }

        private void SampleAirRailPose(Vector3 local, float height, Quaternion stowed, float halfSpan,
            out Vector3 position, out Quaternion rotation)
        {
            float front = AirDeckHeight(local.z + halfSpan);
            float rear = AirDeckHeight(local.z - halfSpan);
            float pitch = -Mathf.Atan2(front - rear, 2f * halfSpan) * Mathf.Rad2Deg;
            local.y = Mathf.Max(AirDeckHeight(local.z), (front + rear) * .5f) +
                height - railStart.localPosition.y;
            position = transform.TransformPoint(local);
            rotation = transform.rotation * Quaternion.AngleAxis(pitch, Vector3.right) * stowed;
        }

        private float AirDeckHeight(float z)
        {
            Vector3 start = railStart.localPosition;
            Vector3 fold = railFold.localPosition;
            if (z >= fold.z)
                return Mathf.Lerp(start.y, fold.y, Mathf.InverseLerp(start.z, fold.z, z));
            return Mathf.LerpUnclamped(fold.y, railTip.y, (z - fold.z) / (railTip.z - fold.z));
        }

        private Plane ReadRunoutGround(float localX)
        {
            Vector3 sample = transform.TransformPoint(new Vector3(localX, railTip.y,
                Mathf.Lerp(railTip.z, railGate.localPosition.z, .75f)));
            if (Physics.Raycast(sample + Vector3.up * 1.5f, Vector3.down, out var hit, 3f, GroundMask, QueryTriggerInteraction.Ignore)
                && hit.normal.y > .85f && !hit.collider.transform.IsChildOf(transform))
                return new Plane(hit.normal, hit.point);

            return new Plane(Vector3.up, transform.TransformPoint(new Vector3(localX, railTip.y, railTip.z)));
        }

        private void SampleDeckPose(Vector3 local, Plane ground, out Vector3 position, out Quaternion rotation)
        {
            Vector3 tip = transform.TransformPoint(new Vector3(local.x, railTip.y, railTip.z));
            var neutralRunout = new Plane(Vector3.up, tip);
            float frontZ = local.z + supportHalfSpan;
            float rearZ = local.z - supportHalfSpan;
            float front = DeckHeight(local.x, frontZ, ground, neutralRunout);
            float rear = DeckHeight(local.x, rearZ, ground, neutralRunout);
            float pitch = -Mathf.Atan2(front - rear, 2f * supportHalfSpan) * Mathf.Rad2Deg;
            Quaternion bodyOnRamp = transform.rotation * Quaternion.AngleAxis(pitch, Vector3.right) * stowedRotation;
            Vector3 heading = transform.rotation * stowedRotation * Vector3.forward;
            Quaternion bodyOnGround = Quaternion.LookRotation(Vector3.ProjectOnPlane(heading, ground.normal), ground.normal);
            float onRunout = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(railTip.z, railTip.z - RunoutTransition, frontZ));
            rotation = Quaternion.Slerp(bodyOnRamp, bodyOnGround, onRunout);

            // Preserve native longitudinal progress. Fit the two supports without
            // lowering the center through the crest or either contact into the deck.
            Vector3 forward = transform.InverseTransformDirection(rotation * Vector3.forward);
            float rise = supportHalfSpan * forward.y / Mathf.Max(.5f, forward.z);
            float floor = Mathf.Max(DeckHeight(local.x, local.z, ground, neutralRunout), Mathf.Max(front - rise, rear + rise));
            local.y = floor + stowedHeight - railStart.localPosition.y;
            position = transform.TransformPoint(local);
        }

        private float DeckHeight(float x, float z, Plane ground, Plane neutralRunout)
        {
            Vector3 start = railStart.localPosition;
            Vector3 fold = railFold.localPosition;
            if (z >= fold.z)
                return Mathf.Lerp(start.y, fold.y, Mathf.InverseLerp(start.z, fold.z, z));

            float groundY = LocalPlaneHeight(ground, x, z);
            if (z >= railTip.z)
            {
                float rampY = Mathf.Lerp(fold.y, railTip.y, Mathf.InverseLerp(fold.z, railTip.z, z));
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(railTip.z + RunoutTransition, railTip.z, z));
                return Mathf.Lerp(rampY, Mathf.Max(rampY, groundY), blend);
            }

            float toeY = LocalPlaneHeight(neutralRunout, x, z);
            float runout = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(railTip.z, railTip.z - RunoutTransition, z));
            return Mathf.Lerp(Mathf.Max(toeY, groundY), groundY, runout);
        }

        private float LocalPlaneHeight(Plane plane, float x, float z)
        {
            Vector3 localZero = transform.TransformPoint(new Vector3(x, 0f, z));
            return -plane.GetDistanceToPoint(localZero) / Vector3.Dot(plane.normal, transform.up);
        }

        private static Vector3 KeepNativeColliderAboveGround(UnitDefinition definition, Vector3 position, Quaternion rotation, Plane ground)
        {
            var collider = definition.unitPrefab.GetComponent<BoxCollider>();
            if (collider == null)
                return position;

            // The native push-out enables this collider immediately. Put its whole
            // support face clear of the runway before giving physics the vehicle.
            float lowest = ground.GetDistanceToPoint(position + rotation * (collider.center - Vector3.up * (collider.size.y * .5f)));
            return position + Vector3.up * (Mathf.Max(0f, .015f - lowest) / ground.normal.y);
        }

        private bool HasClearExit(MountedCargo cargo, SpatialCargoRoute route, out string obstruction)
        {
            int obstructionMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask | (int)PhysicsLayers.DefaultMask;
            Vector3 rear = -Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Quaternion rotation = Quaternion.LookRotation(rear);
            Vector3 localExit = railTip;
            var extents = new Vector3(2.8f, 2.25f, .3f);
            if (SuperheavySpatialCargoRuntime.TryGetLoad(aircraft, cargo, out var load))
            {
                var release = SuperheavySpatialCargoPlan.GetReleaseBounds(load);
                localExit.x = release.center.x + route.Shift;
                extents.x = release.extents.x + .15f;
            }
            Vector3 center = transform.TransformPoint(localExit) + Vector3.up * 2.6f;
            var overlap = Physics.OverlapBox(center, extents, rotation, obstructionMask, QueryTriggerInteraction.Ignore)
                .FirstOrDefault(collider => !collider.transform.IsChildOf(transform));
            if (overlap != null)
            {
                obstruction = overlap.name;
                return false;
            }

            float travel = SupportHalfSpan(cargo) + RunoutTransition + PushDistance(cargo) + 2f;
            foreach (var hit in Physics.BoxCastAll(center, extents, rear, rotation, travel,
                obstructionMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.collider.transform.IsChildOf(transform) && Vector3.Dot(hit.normal, Vector3.up) < .75f)
                {
                    obstruction = hit.collider.name;
                    return false;
                }
            }

            obstruction = null;
            return true;
        }

        private bool TryPlanStowedRoute(WeaponStation station, MountedCargo cargo, out SpatialCargoRoute route)
        {
            route = default;
            if (!SuperheavySpatialCargoRuntime.TryGetLoad(aircraft, cargo, out var movingLoad))
                return true;

            var parked = new System.Collections.Generic.List<SpatialCargoLoad>();
            foreach (var other in station.Weapons.OfType<MountedCargo>())
                if (other != null && other != cargo && other.GetAmmoLoaded() > 0 && !Detached(other) &&
                    SuperheavySpatialCargoRuntime.TryGetLoad(aircraft, other, out var load))
                    parked.Add(load);
            return SuperheavySpatialCargoPlan.TryRoute(movingLoad, parked, out route);
        }

        private void ReportBlocked(string reason, string detail = null, bool air = false)
        {
            if (!GameManager.IsLocalAircraft(aircraft) || Time.unscaledTime - lastBlockedMessage < 2f)
                return;

            lastBlockedMessage = Time.unscaledTime;
            log.LogInfo($"Superheavy {(air ? "air" : "ground")} cargo held: {reason}" +
                (detail == null ? string.Empty : $" Collider={detail}."));
            if (SceneSingleton<AircraftActionsReport>.i != null)
                SceneSingleton<AircraftActionsReport>.i.ReportText(reason, 3f);
        }
    }
}
