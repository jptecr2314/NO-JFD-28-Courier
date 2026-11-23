using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundFlightControls : MonoBehaviour
    {
        private const string SpoilerPrefix = "SHGT_Control_Airbrake_";
        private const string OutletPrefix = "SHGT_ThrustOutlet_";
        private const float PitchTravel = 12f;
        private const float YawTravel = 10f;
        private const float RollTravel = 11f;
        private const float VectorLimitSpeed = 210f;
        private const float IdleThrottle = .09f;
        private const float StowSpool = .28f;
        private const float ReverseThrust = .38f;
        private const float ReverseCutoutSpeed = 5f;
        private const float ReverseFullSpeed = 18f;
        private const float ReverseMinimumSpeed = 14f;
        private const float ReverseMaximumSpeed = 105f;
        private const float ReverserTravel = 24f;
        private const float MaxRollOpening = .38f;
        private static readonly AccessTools.FieldRef<Airbrake, float> NativeOpening =
            AccessTools.FieldRefAccess<Airbrake, float>("openAmount");
        private static readonly AccessTools.FieldRef<Airbrake, float> NativeTravel =
            AccessTools.FieldRefAccess<Airbrake, float>("maxAngle");
        private static readonly AccessTools.FieldRef<Airbrake, float> NativeDrag =
            AccessTools.FieldRefAccess<Airbrake, float>("dragAmount");
        private static readonly AccessTools.FieldRef<Turbofan, JetNozzle[]> NativeNozzles =
            AccessTools.FieldRefAccess<Turbofan, JetNozzle[]>("nozzles");
        private static readonly AccessTools.FieldRef<JetNozzle, Transform> NativeForcePoint =
            AccessTools.FieldRefAccess<JetNozzle, Transform>("thrustTransform");

        private enum ReverserState { Forward, Braking, Stowing }

        private sealed class Spoiler
        {
            internal Airbrake Actuator;
            internal UnitPart Owner;
            internal Quaternion RestRotation;
            internal float Side;
            internal float Travel;
            internal float Drag;

            internal float ExtraOpening(float assist, float symmetric) =>
                (1f - symmetric) * Mathf.Max(0f, assist * Side);
        }

        private Aircraft aircraft;
        private ManualLogSource log;
        private Spoiler[] spoilers;
        private readonly Dictionary<Turbofan, float> bankSides = new Dictionary<Turbofan, float>();
        private readonly List<LandingGear> portContacts = new List<LandingGear>();
        private readonly List<LandingGear> starboardContacts = new List<LandingGear>();
        private readonly List<Transform> upperNozzleFlaps = new List<Transform>();
        private readonly List<Transform> lowerNozzleFlaps = new List<Transform>();
        private float rollOpening;
        private ReverserState reverse;
        private float reverseOpening;
        private float reverseUpdatedAt = float.NegativeInfinity;

        internal string ReverseReadout
        {
            get
            {
                if (reverse == ReverserState.Braking)
                    return reverseOpening >= .99f ? "REV ACTIVE" : "REV DEPLOY";
                return reverse == ReverserState.Stowing ? "REV STOW" : "FWD";
            }
        }

        internal bool ReverseSelected => reverse != ReverserState.Forward;
        internal bool VectoringAvailable => bankSides.Count >= 2 && !ReverseSelected && aircraft != null && aircraft.rb != null &&
            aircraft.Ignition && aircraft.LocalSim && !aircraft.disabled &&
            SuperheavyGroundPhysicsPlugin.HasAirborneThrust(aircraft) && FlightAuthority(aircraft.speed) > 0f;

        internal float SymmetricSpoilerOpening
        {
            get
            {
                float opening = 0f;
                int livePanels = 0;
                foreach (Spoiler panel in spoilers)
                    if (panel.Actuator != null && panel.Actuator.isActiveAndEnabled &&
                        panel.Owner != null && !panel.Owner.IsDetached())
                    {
                        opening += NativeOpening(panel.Actuator);
                        livePanels++;
                    }
                return livePanels == 0 ? 0f : opening / livePanels;
            }
        }

        internal static void Register(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(VectoringEnvelopePatch)).Patch();
            harmony.CreateClassProcessor(typeof(NozzleForcePatch)).Patch();
        }

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.TryGetComponent<SuperheavyGroundFlightControls>(out _))
                return;

            var spoilers = SuperheavyNativeDamage.Components<Airbrake>(aircraft)
                .Where(item => item.name.StartsWith(SpoilerPrefix, StringComparison.Ordinal))
                .Select(item => new Spoiler
                {
                    Actuator = item,
                    Owner = item.GetComponentInParent<UnitPart>(),
                    RestRotation = item.transform.localRotation,
                    Side = Mathf.Sign(aircraft.transform.InverseTransformPoint(item.transform.position).x),
                    Travel = NativeTravel(item),
                    Drag = NativeDrag(item)
                }).ToArray();
            if (spoilers.Length != 8 || spoilers.Count(panel => panel.Side > 0f) != 4 || spoilers.Any(panel => panel.Drag <= 0f))
            {
                log.LogWarning("Superheavy flight controls: expected four physical spoiler panels per wing.");
                spoilers = Array.Empty<Spoiler>();
            }

            var controls = aircraft.gameObject.AddComponent<SuperheavyGroundFlightControls>();
            controls.aircraft = aircraft;
            controls.log = log;
            controls.spoilers = spoilers;
            controls.BindPropulsion();
            log.LogInfo($"Superheavy flight controls: {spoilers.Length} spoiler panels and {controls.bankSides.Count} " +
                $"vector banks with ground reverse; {controls.upperNozzleFlaps.Count + controls.lowerNozzleFlaps.Count} nozzle flap frames.");
        }

        private void BindPropulsion()
        {
            foreach (var bank in SuperheavyNativeDamage.Components<Turbofan>(aircraft))
            {
                var outlets = NativeNozzles(bank)?.Where(outlet => outlet != null &&
                    outlet.name.StartsWith(OutletPrefix, StringComparison.Ordinal) &&
                    NativeForcePoint(outlet) != null).ToArray();
                if (outlets == null || (outlets.Length != 1 && outlets.Length != 4))
                    continue;
                float center = outlets.Average(outlet =>
                    transform.InverseTransformPoint(NativeForcePoint(outlet).position).x);
                if (Mathf.Abs(center) > 2f)
                    bankSides.Add(bank, Mathf.Sign(center));
            }
            if (bankSides.Count < 2 || !bankSides.Values.Contains(-1f) || !bankSides.Values.Contains(1f))
            {
                log.LogWarning("Superheavy propulsion requires one native nozzle bank on each side.");
                bankSides.Clear();
            }
            for (int i = 0; i < 6; i++)
            {
                if (SuperheavyGearPhysics.TryGetNative(aircraft, $"Port_{i:00}", out var port))
                    portContacts.Add(port);
                if (SuperheavyGearPhysics.TryGetNative(aircraft, $"Starboard_{i:00}", out var starboard))
                    starboardContacts.Add(starboard);
            }
            foreach (var frame in SuperheavyNativeDamage.Components<Transform>(aircraft))
            {
                if (!frame.name.StartsWith("SHGT_Engine_", StringComparison.Ordinal))
                    continue;
                if (frame.name.EndsWith("_VectorUpper", StringComparison.Ordinal))
                    upperNozzleFlaps.Add(frame);
                else if (frame.name.EndsWith("_VectorLower", StringComparison.Ordinal))
                    lowerNozzleFlaps.Add(frame);
            }
        }

        private float ForwardGroundSpeed() => Vector3.Dot(aircraft.rb.velocity, GroundForward());

        private Vector3 GroundForward() => Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;

        private bool CanReverse(float forwardSpeed)
        {
            if (!NozzleForcePatch.Installed || !VectoringEnvelopePatch.ReverseThrottleInstalled ||
                aircraft.disabled || !aircraft.LocalSim || !aircraft.Ignition || aircraft.rb.isKinematic ||
                !GameManager.IsLocalAircraft(aircraft) || bankSides.Count < 2 ||
                aircraft.gearState != LandingGear.GearState.LockedExtended ||
                forwardSpeed < 0f || forwardSpeed > ReverseMaximumSpeed ||
                Vector3.Dot(transform.up, Vector3.up) < .96f)
                return false;
            return bankSides.Keys.All(bank => bank != null && bank.isActiveAndEnabled) &&
                HasMainContact(portContacts) && HasMainContact(starboardContacts);
        }

        private static bool HasMainContact(List<LandingGear> contacts)
        {
            foreach (var gear in contacts)
                if (gear != null && gear.enabled && gear.WeightOnWheel(.01f))
                    return true;
            return false;
        }

        private void RefreshReverse()
        {
            if (reverseUpdatedAt == Time.fixedTime)
                return;
            reverseUpdatedAt = Time.fixedTime;
            var inputs = aircraft.GetInputs();
            float speed = ForwardGroundSpeed();
            float minimumSpeed = reverse == ReverserState.Braking ? ReverseCutoutSpeed : ReverseMinimumSpeed;
            if (inputs.throttle <= IdleThrottle && BrakeDemand(inputs.brake) > 0f &&
                speed >= minimumSpeed && CanReverse(speed))
            {
                if (reverse != ReverserState.Braking)
                    log.LogInfo($"Superheavy reverse braking at {speed * 3.6f:F0} km/h, brake={inputs.brake:P0}.");
                reverse = ReverserState.Braking;
                reverseOpening = Mathf.MoveTowards(reverseOpening, 1f, Time.fixedDeltaTime * 1.35f);
                return;
            }
            if (reverse == ReverserState.Forward)
                return;

            reverseOpening = Mathf.MoveTowards(reverseOpening, 0f, Time.fixedDeltaTime * 1.8f);
            bool highSpool = inputs.throttle <= IdleThrottle &&
                bankSides.Keys.Any(bank => bank != null && bank.GetSpoolPercentage() > StowSpool);
            reverse = reverseOpening > 0f || highSpool ? ReverserState.Stowing : ReverserState.Forward;
        }

        private void FixedUpdate()
        {
            if (aircraft == null || aircraft.rb == null)
                return;

            RefreshReverse();
            bool airborne = !aircraft.disabled && aircraft.LocalSim && SuperheavyGroundPhysicsPlugin.HasAirborneThrust(aircraft);
            Vector3 airflow = aircraft.rb.velocity - aircraft.GetWindVelocity();
            float airspeed = airflow.magnitude;
            float authority = airborne ? Mathf.InverseLerp(32f, 55f, airspeed) : 0f;
            // Positive native roll commands right bank, so spoil the right wing's lift.
            float command = Mathf.Clamp(aircraft.GetInputs().roll, -1f, 1f);
            rollOpening = Mathf.MoveTowards(rollOpening, command * MaxRollOpening * authority, 1.8f * Time.fixedDeltaTime);
            if (!airborne || airspeed <= 1f || rollOpening == 0f)
                return;

            Vector3 flowDirection = airflow / airspeed;
            Vector3 liftDirection = Vector3.ProjectOnPlane(aircraft.transform.up, flowDirection).normalized;
            // Bound the supplemental roll moment. The stock symmetric drag is still handled by Airbrake.
            float flowLoad = aircraft.airDensity * Mathf.Min(airspeed * airspeed, 100f * 100f);
            foreach (Spoiler panel in spoilers)
            {
                if (panel.Actuator == null || !panel.Actuator.isActiveAndEnabled || panel.Owner == null || panel.Owner.IsDetached())
                    continue;
                float extra = panel.ExtraOpening(rollOpening, NativeOpening(panel.Actuator));
                if (extra <= 0f)
                    continue;
                Vector3 force = -(flowDirection + .85f * liftDirection) * (extra * panel.Drag * flowLoad);
                var target = SuperheavyNativeDamage.BodyFor(panel.Owner);
                if (target != null && !target.isKinematic)
                    target.AddForceAtPosition(force, panel.Actuator.transform.position);
            }
        }

        private void LateUpdate()
        {
            foreach (Spoiler panel in spoilers)
            {
                if (panel.Actuator == null || !panel.Actuator.isActiveAndEnabled)
                    continue;
                float symmetric = NativeOpening(panel.Actuator);
                float total = symmetric + panel.ExtraOpening(rollOpening, symmetric);
                panel.Actuator.transform.localRotation = panel.RestRotation * Quaternion.AngleAxis(panel.Travel * total, Vector3.right);
            }
            if (!ReverseSelected)
                return;
            Quaternion upper = Quaternion.AngleAxis(-ReverserTravel * reverseOpening, Vector3.right);
            Quaternion lower = Quaternion.AngleAxis(ReverserTravel * reverseOpening, Vector3.right);
            foreach (var flap in upperNozzleFlaps)
                if (flap != null)
                    flap.localRotation = upper;
            foreach (var flap in lowerNozzleFlaps)
                if (flap != null)
                    flap.localRotation = lower;
        }

        private static float FlightAuthority(float speed) =>
            Mathf.Clamp01(1f - .7f * Mathf.InverseLerp(85f, 150f, speed) -
                .3f * Mathf.InverseLerp(150f, VectorLimitSpeed, speed));

        private static float ReverseRatioAtSpeed(float speed, float opening) =>
            -ReverseThrust * Mathf.Clamp01(opening) * Mathf.InverseLerp(ReverseCutoutSpeed, ReverseFullSpeed, speed);

        private static float BrakeDemand(float brake) => Mathf.InverseLerp(.05f, 1f, brake);

        private Vector3 VectorEnvelope(float side)
        {
            if (ReverseSelected || !aircraft.Ignition || aircraft.disabled || !aircraft.LocalSim || aircraft.rb == null)
                return Vector3.zero;
            if (SuperheavyGroundPhysicsPlugin.HasAirborneThrust(aircraft))
            {
                float rise = Mathf.InverseLerp(12f, 30f, SuperheavyGroundPhysicsPlugin.SurfaceClearance(aircraft));
                float authority = FlightAuthority(aircraft.speed);
                float pitchTravel = Mathf.Lerp(3f, PitchTravel, rise) * authority;
                float rollTravel = Mathf.Min(RollTravel * rise * authority,
                    20f - pitchTravel * Mathf.Clamp01(Mathf.Abs(aircraft.GetInputs().pitch)));
                // Native Turbofan negates its roll input. Positive roll must push
                // the starboard outlets down and lift the port outlets.
                return new Vector3(pitchTravel, YawTravel * rise * authority, -side * rollTravel);
            }
            if (!aircraft.gearDeployed)
                return Vector3.zero;
            float speed = SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb);
            float runwayPitch = 3f * Mathf.InverseLerp(15f, 36f, speed) * FlightAuthority(speed);
            if (aircraft.GetInputs().pitch < 0f)
            {
                float noseUp = Mathf.Asin(Mathf.Clamp(transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                float noseUpRate = -Vector3.Dot(aircraft.rb.angularVelocity, transform.right) * Mathf.Rad2Deg;
                runwayPitch *= 1f - Mathf.InverseLerp(7f, 10f, noseUp + Mathf.Max(0f, noseUpRate) * .35f);
            }
            return new Vector3(runwayPitch, 0f, 0f);
        }

        [HarmonyPatch(typeof(Turbofan), "FixedUpdate")]
        private static class VectoringEnvelopePatch
        {
            private static readonly AccessTools.FieldRef<Turbofan, Aircraft> Owner =
                AccessTools.FieldRefAccess<Turbofan, Aircraft>("aircraft");

            internal static bool ReverseThrottleInstalled { get; private set; }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var throttle = AccessTools.Field(typeof(ControlInputs), nameof(ControlInputs.throttle));
                var reads = code.Where(op => op.LoadsField(throttle)).ToArray();
                ReverseThrottleInstalled = reads.Length == 2;
                if (!ReverseThrottleInstalled)
                {
                    Debug.LogWarning("Superheavy reverse disabled: native Turbofan throttle reads changed.");
                    return code;
                }

                var command = AccessTools.Method(typeof(VectoringEnvelopePatch), nameof(ThrottleCommand));
                foreach (var read in reads)
                    code.InsertRange(code.IndexOf(read) + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, command)
                    });
                return code;
            }

            private static float ThrottleCommand(float throttle, Turbofan bank)
            {
                var aircraft = Owner(bank);
                if (aircraft == null || !aircraft.TryGetComponent<SuperheavyGroundFlightControls>(out var controls) ||
                    !controls.bankSides.ContainsKey(bank) || throttle > IdleThrottle)
                    return throttle;

                controls.RefreshReverse();
                return controls.reverse == ReverserState.Braking ? BrakeDemand(aircraft.GetInputs().brake) : throttle;
            }

            private static void Prefix(Turbofan __instance, Aircraft ___aircraft, ref Vector3 ___thrustVectoring,
                ref float ___thrustVectoringMaxAirspeed)
            {
                if (___aircraft == null || !___aircraft.TryGetComponent<SuperheavyGroundFlightControls>(out var controls) ||
                    !controls.bankSides.TryGetValue(__instance, out float side))
                    return;

                ___thrustVectoringMaxAirspeed = VectorLimitSpeed;
                ___thrustVectoring = controls.VectorEnvelope(side);
            }
        }

        [HarmonyPatch(typeof(JetNozzle), nameof(JetNozzle.Thrust))]
        private static class NozzleForcePatch
        {
            private static readonly AccessTools.FieldRef<JetNozzle, Aircraft> Owner =
                AccessTools.FieldRefAccess<JetNozzle, Aircraft>("aircraft");
            private static bool installed;

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var apply = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition),
                    new[] { typeof(Vector3), typeof(Vector3) });
                var matches = code.Where(op => op.Calls(apply)).ToArray();
                if (matches.Length != 1)
                {
                    installed = false;
                    Debug.LogWarning("Superheavy reverse disabled: native JetNozzle force call changed.");
                    return code;
                }
                var original = matches[0];
                int index = code.IndexOf(original);
                var loadNozzle = new CodeInstruction(OpCodes.Ldarg_0);
                loadNozzle.labels.AddRange(original.labels);
                original.labels.Clear();
                original.opcode = OpCodes.Call;
                original.operand = AccessTools.Method(typeof(NozzleForcePatch), nameof(ApplyForce));
                code.Insert(index, loadNozzle);
                installed = true;
                return code;
            }

            internal static bool Installed => installed;

            private static void ApplyForce(Rigidbody body, Vector3 nativeForce, Vector3 station, JetNozzle nozzle)
            {
                if (nozzle.name.StartsWith(OutletPrefix, StringComparison.Ordinal))
                {
                    var aircraft = Owner(nozzle);
                    if (aircraft != null && aircraft.TryGetComponent<SuperheavyGroundFlightControls>(out var controls))
                    {
                        controls.RefreshReverse();
                        var inputs = aircraft.GetInputs();
                        if (controls.ReverseSelected && inputs.throttle <= IdleThrottle)
                            nativeForce = controls.reverse == ReverserState.Braking && BrakeDemand(inputs.brake) > 0f
                                ? controls.GroundForward() * (nativeForce.magnitude *
                                    ReverseRatioAtSpeed(controls.ForwardGroundSpeed(), controls.reverseOpening))
                                : Vector3.zero;
                    }
                }
                body.AddForceAtPosition(nativeForce, station);
            }
        }
    }
}
