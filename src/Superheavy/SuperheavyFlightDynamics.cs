using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    [DefaultExecutionOrder(-100)]
    internal sealed class SuperheavyFlightDynamics : MonoBehaviour
    {
        // Gross wing planform 855.14 m² and quarter MAC at local Z +1.80 m.
        // The station areas and span centroids come from the measured wing sections.
        private const float WingArea = 855.14f;
        private const float WingCleanLift = .18f;
        private const float FlapLiftIncrement = .52f;
        private const float WingLiftSlope = .095f;
        private const float WingInducedDrag = .068f;
        private const float TailArea = 133.94f;
        private const float FinArea = 47.58f;
        private const float TailLiftSlope = .050f;
        private const float TailProfileDrag = .013f;
        private const float MaxTakeoffFlap = .74f;
        private const float SpoilerLiftLoss = .23f;
        private const float SpoilerDrag = .052f;
        private const float SeaLevelDensity = 1.225f;
        private static readonly Vector3 HorizontalTail = new Vector3(0f, 11.3f, -28.25f);
        private static readonly Vector3 VerticalTail = new Vector3(0f, 11.7f, -29.61f);
        // Diagonal dry inertia at the prototype's 200 t, scaled from the component ledger.
        private static readonly Vector3 DryInertia = new Vector3(36.6e6f, 69.7e6f, 36.3e6f);
        private static readonly AccessTools.FieldRef<AeroPart, float> NativeWingEffectiveness =
            AccessTools.FieldRefAccess<AeroPart, float>("wingEffectiveness");

        private sealed class WingPanel
        {
            internal Vector3 Station;
            internal float Area;
            internal float AileronGain;
            internal UnitPart Owner;
            internal UnitPart Root;
        }

        private Aircraft aircraft;
        private Rigidbody body;
        private WingPanel[] wings;
        private Vector3 wingMeanStation;
        private UnitPart stabilizer;
        private UnitPart portFin;
        private UnitPart starboardFin;
        private UnitPart tailRoot;
        private SuperheavyGroundFlightControls flightControls;
        private ManualLogSource log;
        private bool departure;
        private float nextFlapLog;
        private float reportedFlap;
        private LandingGear.GearState reportedGear;
        private float inertiaMass;

        internal float FlapPosition { get; private set; }

        internal static void Register(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(ReplaceDonorAero)).Patch();
            harmony.CreateClassProcessor(typeof(RepairNativeEffectiveness)).Patch();
        }

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.rb == null || aircraft.TryGetComponent<SuperheavyFlightDynamics>(out _))
                return;

            var dynamics = aircraft.gameObject.AddComponent<SuperheavyFlightDynamics>();
            dynamics.aircraft = aircraft;
            dynamics.body = aircraft.rb;
            dynamics.log = log;
            var parts = SuperheavyNativeDamage.Components<UnitPart>(aircraft);
            dynamics.wings = new[]
            {
                Panel(parts, "wing1a_L", -7.34f, 10.1f, 4.01f, 277.50f, .06f),
                Panel(parts, "wing2_L", -21.63f, 10.5f, -1.54f, 124f, .31f),
                Panel(parts, "wingtip_L", -31.68f, 10.9f, -5.79f, 26.07f, .34f),
                Panel(parts, "wing1a_R", 7.34f, 10.1f, 4.01f, 277.50f, -.06f),
                Panel(parts, "wing2_R", 21.63f, 10.5f, -1.54f, 124f, -.31f),
                Panel(parts, "wingtip_R", 31.68f, 10.9f, -5.79f, 26.07f, -.34f)
            };
            foreach (WingPanel wing in dynamics.wings)
                dynamics.wingMeanStation += wing.Station * (wing.Area / WingArea);
            dynamics.stabilizer = Part(parts, "hstab");
            dynamics.portFin = Part(parts, "vstab1_L");
            dynamics.starboardFin = Part(parts, "vstab1_R");
            dynamics.tailRoot = Part(parts, "tail");
            dynamics.departure = SuperheavyGroundPhysicsPlugin.SurfaceClearance(aircraft) < .75f;
            dynamics.FlapPosition = dynamics.departure && GearDownSelected(aircraft) ? MaxTakeoffFlap : 0f;
            dynamics.reportedFlap = dynamics.FlapPosition;
            dynamics.reportedGear = aircraft.gearState;
            dynamics.RefreshInertia();
            log.LogInfo("Superheavy flight model active: 855 m² wing, automatic high-lift schedule, pitch trim and spoiler lift dump.");
        }

        private void Start() => TryGetComponent(out flightControls);

        private static WingPanel Panel(UnitPart[] parts, string owner, float x, float y, float z, float area, float aileron) =>
            new WingPanel { Station = new Vector3(x, y, z), Area = area, AileronGain = aileron, Owner = Part(parts, owner),
                Root = Part(parts, x < 0f ? "wing1a_L" : "wing1a_R") };

        private static UnitPart Part(UnitPart[] parts, string name) => parts.FirstOrDefault(part => part.name == name);

        private static bool Available(UnitPart part) => ReferenceEquals(part, null) || (part != null && !part.IsDetached());

        // The game already updates this value when native AeroPart damage is
        // applied. Read it rather than creating a second health/damage curve.
        // A root and child describe the same surviving surface, so take the
        // limiting value instead of multiplying their damage twice.
        internal static float SurfaceEffectiveness(UnitPart root, UnitPart owner) =>
            Mathf.Min(PartEffectiveness(root), PartEffectiveness(owner));

        private static float PartEffectiveness(UnitPart part)
        {
            if (!Available(part))
                return 0f;
            return part is AeroPart aero ? Mathf.Clamp01(NativeWingEffectiveness(aero)) : 1f;
        }

        private static bool GearDownSelected(Aircraft aircraft) => aircraft.gearState == LandingGear.GearState.LockedExtended ||
            aircraft.gearState == LandingGear.GearState.Extending ||
            (aircraft.gearState == LandingGear.GearState.Uninitialized && aircraft.gearDeployed);

        internal static float ScheduledFlap(float equivalentAirspeed, float liftDemand, bool gearDown, bool departure)
        {
            if (!gearDown)
                return 0f;

            // A hard structural speed envelope also protects the heaviest loadout.
            float speedAllowance = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(92f, 122f, equivalentAirspeed));
            if (departure)
                return Mathf.Min(MaxTakeoffFlap, speedAllowance);
            // The light aircraft also needs flap lift before the flare. Build it gradually from the wing's actual load.
            float demand = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.42f, 1.22f, liftDemand));
            return demand * speedAllowance;
        }

        private void RefreshInertia()
        {
            // The native body graph owns each part's inertia. Applying the
            // former whole-aircraft tensor to its root would count the wings
            // and engines twice and make the airframe excessively sluggish.
            if (SuperheavyNativeDamage.HasComplex(aircraft))
                return;
            float mass = body.mass;
            if (Mathf.Abs(mass - inertiaMass) < 100f)
                return;

            // Additional fuel/cargo is predominantly inboard. The pitch and yaw
            // terms include its fore/aft distribution without scaling roll like the wings.
            float extra = Mathf.Max(0f, mass - 200000f);
            body.inertiaTensorRotation = Quaternion.identity;
            body.inertiaTensor = DryInertia + extra * new Vector3(100f, 108f, 16f);
            inertiaMass = mass;
        }

        private void FixedUpdate()
        {
            if (aircraft == null || !aircraft.LocalSim || aircraft.disabled || body == null || body.isKinematic)
                return;

            RefreshInertia();
            float density = aircraft.airDensity;
            Vector3 wind = aircraft.GetWindVelocity();
            Vector3 relative = body.velocity - wind;
            if (!(density > .01f))
                return;

            float pressure = .5f * density * relative.sqrMagnitude;
            float equivalentAirspeed = Mathf.Sqrt(2f * pressure / SeaLevelDensity);
            float liftDemand = SuperheavyNativeDamage.AttachedMass(aircraft) * Physics.gravity.magnitude / (WingArea * Mathf.Max(1f, pressure));
            float clearance = SuperheavyGroundPhysicsPlugin.SurfaceClearance(aircraft);
            bool gearDown = GearDownSelected(aircraft);
            if (clearance > 55f || (!gearDown && clearance > 1f))
                departure = false;
            float targetFlap = ScheduledFlap(equivalentAirspeed, liftDemand, gearDown, departure);
            float flapRate = targetFlap > FlapPosition ? .19f : .34f;
            FlapPosition = Mathf.MoveTowards(FlapPosition, targetFlap, flapRate * Time.fixedDeltaTime);
            float gearDrag = SuperheavyGearPhysics.Extension(aircraft);
            ReportApproach(equivalentAirspeed, targetFlap, clearance);
            if (Vector3.Dot(relative, transform.forward) < 4f)
                return;

            // Retain aerodynamic stiffness at speed while tapering command gain as q rises.
            float controlScale = Mathf.Min(1f, 12500f / pressure);
            var inputs = aircraft.GetInputs();
            float approach = departure ? 0f : gearDrag;
            float pitchCommand = Mathf.Clamp(inputs.pitch, -1f, 1f);
            // Do not attenuate small flare inputs a second time after native FBW.
            // The former .58 + .42*abs(input) curve made a 10% correction only
            // 6.2% with the gear down, precisely when a heavy transport needs it.
            float pitch = LimitRotation(pitchCommand) * controlScale;
            float roll = Mathf.Clamp(inputs.roll, -1f, 1f) * controlScale;
            float yaw = Mathf.Clamp(inputs.yaw, -1f, 1f) * controlScale;
            float flap = FlapPosition;
            float flightTrim = departure ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.7f, 7f, clearance)) : 1f;
            float spoilers = flightControls != null ? flightControls.SymmetricSpoilerOpening : 0f;
            float liftRetention = 1f - spoilers * SpoilerLiftLoss;
            float waveDrag = .09f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(205f, 275f, relative.magnitude));
            float parasiteDrag = .029f + .064f * flap + waveDrag;
            Vector3 aircraftCenter = SuperheavyNativeDamage.AttachedCenterOfMass(aircraft);
            float tailArm = aircraftCenter.z - HorizontalTail.z;
            Vector3 wingArm = wingMeanStation - aircraftCenter;
            // Account for the tail's upward trim load, including the moment
            // generated by drag acting above the aircraft's center of mass.
            float trimDrag = parasiteDrag + WingInducedDrag * Mathf.Pow(Mathf.Min(liftDemand, 1.6f), 2f);
            float trimWingLift = (liftDemand * tailArm - trimDrag * wingArm.y) / (tailArm + wingArm.z);
            float cleanLift = Mathf.Lerp(WingCleanLift, Mathf.Clamp(trimWingLift, .02f, .39f),
                flightTrim * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(108f, 163f, equivalentAirspeed)));
            float flapLift = flap * FlapLiftIncrement;
            float trimAlpha = Mathf.Clamp((trimWingLift - cleanLift - flapLift) / WingLiftSlope, 0f, 4.5f);
            float wingTrimMoment = 0f;
            float referenceLift = (cleanLift + flapLift + WingLiftSlope * trimAlpha) * liftRetention;
            float referenceInducedDrag = WingInducedDrag * referenceLift * referenceLift;
            float trimAngle = trimAlpha * Mathf.Deg2Rad;
            var trimFlow = new Vector3(0f, -Mathf.Sin(trimAngle), Mathf.Cos(trimAngle));
            Vector3 referenceLiftForce = Vector3.ProjectOnPlane(Vector3.up, trimFlow).normalized * referenceLift;

            // The stock airbrake still provides its own drag. Add the lift loss from
            // deploying panels over the wing and the associated additional drag.
            float approachDrag = .026f * approach * (1f - Mathf.InverseLerp(.08f, .32f, inputs.throttle)) *
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f, 86f, equivalentAirspeed));
            body.AddForce(-relative.normalized * (pressure * WingArea *
                (.018f * gearDrag + SpoilerDrag * spoilers + approachDrag)));

            foreach (WingPanel panel in wings)
            {
                float effectiveness = SurfaceEffectiveness(panel.Root, panel.Owner);
                if (effectiveness <= 0f ||
                    !SampleFlow(panel.Station, wind, density, out var station, out var direction, out var flow, out float q))
                    continue;

                float alpha = AngleOfAttack(flow);
                float stall = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(14f, 33f, Mathf.Abs(alpha)));
                float linear = cleanLift + flapLift + WingLiftSlope * alpha + panel.AileronGain * roll;
                float lift = Mathf.Lerp(Mathf.Clamp(linear, -1.05f, 1.56f + .38f * flap), .28f * Mathf.Sign(alpha), stall);
                if (lift > 0f)
                    lift *= liftRetention;
                lift *= effectiveness;
                float drag = parasiteDrag + WingInducedDrag * lift * lift + .27f * stall;
                ApplySurface(station, direction, transform.up, panel.Area * q, lift, drag, panel.Owner);
                Vector3 referenceForce = referenceLiftForce * effectiveness -
                    trimFlow * (parasiteDrag + referenceInducedDrag * effectiveness * effectiveness);
                wingTrimMoment += Vector3.Cross(panel.Station - aircraftCenter, referenceForce).x * panel.Area;
            }

            float tailEffectiveness = SurfaceEffectiveness(tailRoot, stabilizer);
            if (tailEffectiveness > 0f && SampleFlow(HorizontalTail, wind, density, out var tail, out var tailDirection, out var tailFlow, out float tailQ))
            {
                // Offset the wing's predicted pitch moment as the flaps/spoilers
                // move, while preserving the tail's restoring force around trim.
                float tailDragMoment = -TailProfileDrag * TailArea * (HorizontalTail.y - aircraftCenter.y);
                float feedForward = Mathf.Clamp(-(wingTrimMoment + tailDragMoment) /
                    (TailArea * tailArm * tailEffectiveness), -.23f, .23f);
                float incidence = Mathf.Lerp(-.03f, feedForward - TailLiftSlope * trimAlpha, flightTrim);
                float pitchRate = Vector3.Dot(body.angularVelocity, transform.right) * Mathf.Rad2Deg;
                float pitchDamping = approach * Mathf.Clamp(.014f * pitchRate, -.13f, .13f);
                // Positive native pitch lowers the nose, so the tail generates upward force.
                float lift = Mathf.Clamp(TailLiftSlope * AngleOfAttack(tailFlow) + incidence + .77f * pitch - pitchDamping, -.91f, .91f) * tailEffectiveness;
                ApplySurface(tail, tailDirection, transform.up, TailArea * tailQ, lift, TailProfileDrag + .105f * lift * lift, stabilizer);
            }

            float liveFinShare = Available(tailRoot) ?
                (Available(portFin) ? .5f : 0f) + (Available(starboardFin) ? .5f : 0f) : 0f;
            float finEffectiveness = .5f * (SurfaceEffectiveness(tailRoot, portFin) + SurfaceEffectiveness(tailRoot, starboardFin));
            if (liveFinShare > 0f && SampleFlow(VerticalTail, wind, density, out var fin, out var finDirection, out var finFlow, out float finQ))
            {
                float slip = Mathf.Atan2(finFlow.x, finFlow.z);
                float sideForce = Mathf.Clamp(-3.0f * slip - .66f * yaw, -.95f, .95f) *
                    (finEffectiveness / liveFinShare);
                // Each physical fin receives its own half of the load, so its
                // native joint sees that load and a lost fin stops contributing.
                foreach (var owner in new[] { portFin, starboardFin })
                {
                    if (!Available(owner) || owner == null) continue;
                    float share = SurfaceEffectiveness(tailRoot, owner);
                    var finBody = SuperheavyNativeDamage.BodyFor(owner);
                    Vector3 point = finBody != null ? finBody.worldCenterOfMass : fin;
                    ApplySurface(point, finDirection, transform.right, FinArea * .5f * share * finQ,
                        sideForce / Mathf.Max(.01f, finEffectiveness / liveFinShare),
                        .015f + .14f * sideForce * sideForce, owner);
                }
            }
        }

        private void ReportApproach(float equivalentAirspeed, float targetFlap, float clearance)
        {
            bool gearChanged = reportedGear != aircraft.gearState;
            bool flapChanged = Mathf.Abs(FlapPosition - reportedFlap) >= .12f ||
                (FlapPosition == targetFlap && Mathf.Abs(FlapPosition - reportedFlap) >= .03f);
            if (!GameManager.IsLocalAircraft(aircraft) || (!gearChanged && (!flapChanged || Time.fixedTime < nextFlapLog)))
                return;

            reportedGear = aircraft.gearState;
            reportedFlap = FlapPosition;
            nextFlapLog = Time.fixedTime + 1.5f;
            float attitude = Mathf.Asin(Mathf.Clamp(transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            log.LogInfo($"Superheavy high-lift: gear={reportedGear}, flaps={FlapPosition:P0}, target={targetFlap:P0}, " +
                $"EAS={equivalentAirspeed * 3.6f:F0} km/h, pitch={attitude:F1}°, vertical={body.velocity.y:F1} m/s, " +
                $"throttle={aircraft.GetInputs().throttle:P0}, AGL={clearance:F1} m, gross={SuperheavyNativeDamage.AttachedMass(aircraft) / 1000f:F1} t.");
        }

        private float LimitRotation(float pitchCommand)
        {
            if (pitchCommand >= 0f || !aircraft.gearDeployed)
                return pitchCommand;

            float nearRunway = 1f - Mathf.InverseLerp(.5f, 4f, SuperheavyGroundPhysicsPlugin.SurfaceClearance(aircraft));
            float noseUp = Mathf.Asin(Mathf.Clamp(transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float noseUpRate = -Vector3.Dot(body.angularVelocity, transform.right) * Mathf.Rad2Deg;
            float predicted = noseUp + Mathf.Max(0f, noseUpRate) * .35f;
            return pitchCommand * (1f - nearRunway * Mathf.InverseLerp(7f, 10f, predicted));
        }

        private bool SampleFlow(Vector3 localStation, Vector3 wind, float density, out Vector3 station,
            out Vector3 direction, out Vector3 flow, out float pressure)
        {
            station = transform.TransformPoint(localStation);
            Vector3 velocity = body.GetPointVelocity(station) - wind;
            flow = transform.InverseTransformDirection(velocity);
            direction = velocity.normalized;
            pressure = .5f * density * velocity.sqrMagnitude;
            return flow.z > 3f;
        }

        private static float AngleOfAttack(Vector3 flow) => Mathf.Atan2(-flow.y, flow.z) * Mathf.Rad2Deg;

        private void ApplySurface(Vector3 station, Vector3 direction, Vector3 normal, float load, float lift, float drag, UnitPart owner = null)
        {
            Vector3 liftDirection = Vector3.ProjectOnPlane(normal, direction).normalized;
            var target = owner != null ? SuperheavyNativeDamage.BodyFor(owner) : body;
            if (target != null && !target.isKinematic)
                target.AddForceAtPosition((liftDirection * lift - direction * drag) * load, station);
        }

        [HarmonyPatch(typeof(AeroPart), nameof(AeroPart.Repair))]
        private static class RepairNativeEffectiveness
        {
            private static void Postfix(AeroPart __instance)
            {
                // Native Repair restores HP/attachment pose but leaves the
                // ApplyDamage effectiveness cache at its pre-repair value.
                // Repair only this family's cache after a real full HP reset;
                // do not synthesize damage events or change other aircraft.
                if (__instance.hitPoints >= 100f && __instance.parentUnit is Aircraft aircraft &&
                    SuperheavyGroundPhysicsPlugin.IsFamily(aircraft.definition))
                    NativeWingEffectiveness(__instance) = 1f;
            }
        }

        [HarmonyPatch(typeof(AeroPart), "UpdateJobFields")]
        private static class ReplaceDonorAero
        {
            private struct NativeArea
            {
                internal bool Replaced;
                internal float Wing;
                internal float Drag;
            }

            private static void Prefix(AeroPart __instance, out NativeArea __state)
            {
                __state = default;
                if (!(__instance.parentUnit is Aircraft aircraft) || aircraft.definition == null ||
                    !SuperheavyGroundPhysicsPlugin.IsFamily(aircraft.definition) || __instance.IsDetached() ||
                    !aircraft.TryGetComponent<SuperheavyFlightDynamics>(out _))
                    return;

                __state = new NativeArea { Replaced = true, Wing = __instance.GetWingArea(), Drag = __instance.dragArea };
                __instance.SetWingArea(0f);
                __instance.dragArea = 0f;
            }

            private static Exception Finalizer(AeroPart __instance, NativeArea __state, Exception __exception)
            {
                // The job snapshots the fields synchronously. Preserve all native part state.
                if (__state.Replaced)
                {
                    __instance.SetWingArea(__state.Wing);
                    __instance.dragArea = __state.Drag;
                }
                return __exception;
            }
        }
    }
}
