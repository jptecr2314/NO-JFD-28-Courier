using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundVisuals : MonoBehaviour
    {
        private static readonly FieldInfo Nozzles = AccessTools.Field(typeof(Turbofan), "nozzles");
        private static readonly FieldInfo NativeHinge = AccessTools.Field(typeof(LandingGear), "gearHinge");

        // Only the low spool has a native RPM signal in the ground prototype.
        private const float CoreVisualSpeedRatio = 1.413f;

        private readonly struct FanVisual
        {
            internal readonly Transform FrontRotor;
            internal readonly Transform CoreRotor;
            internal readonly Turbofan Turbine;
            internal readonly Vector3 FrontAxis;
            internal readonly Vector3 CoreAxis;

            internal FanVisual(Transform frontRotor, Transform coreRotor, Turbofan turbine, Vector3 aircraftForward)
            {
                FrontRotor = frontRotor;
                CoreRotor = coreRotor;
                Turbine = turbine;
                FrontAxis = frontRotor.InverseTransformDirection(aircraftForward);
                CoreAxis = coreRotor != null ? coreRotor.InverseTransformDirection(aircraftForward) : Vector3.forward;
            }
        }

        private readonly struct WheelVisual
        {
            internal readonly Transform Pivot;
            internal readonly Quaternion Rest;
            internal readonly Vector3 Axis;

            internal WheelVisual(Transform pivot, Vector3 aircraftRight)
            {
                Pivot = pivot;
                Rest = pivot.localRotation;
                Axis = pivot.InverseTransformDirection(aircraftRight);
            }
        }

        private sealed class AxleVisual
        {
            internal string Key;
            internal Transform LegFrame;
            internal Transform Axle;
            internal Transform SteeringFork;
            internal Transform ActuatorRod;
            internal Vector3 Rest;
            internal Vector3 SuspensionAxis;
            internal Quaternion ForkRest;
            internal Vector3 ForkAxis;
            internal WheelVisual[] Wheels;
            internal LandingGear Contact;
            internal float SpinDegrees;
            internal bool Broken;
        }

        private readonly List<FanVisual> fans = new List<FanVisual>(8);
        private readonly List<AxleVisual> axles = new List<AxleVisual>(13);
        private Aircraft aircraft;
        private GameObject gearHost;
        private AnimationClip gearClip;
        private ManualLogSource log;
        private float lastGearPhase = -1f;

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.TryGetComponent<SuperheavyGroundVisuals>(out _))
                return;

            var frames = new Dictionary<string, Transform>();
            foreach (var item in SuperheavyNativeDamage.Components<Transform>(aircraft))
            {
                if (item.name.StartsWith("SHGT_", System.StringComparison.Ordinal))
                    frames[item.name] = item;
            }
            if (!frames.ContainsKey("SHGT_FanPivot_01") && !frames.ContainsKey("SHGT_GearMotion"))
                return;

            var visual = aircraft.gameObject.AddComponent<SuperheavyGroundVisuals>();
            visual.aircraft = aircraft;
            visual.log = log;
            visual.BindFans(aircraft, frames);
            visual.BindGear(frames);
            int coreCount = visual.fans.Count(fan => fan.CoreRotor != null);
            log.LogInfo($"Superheavy ground animation bound {visual.fans.Count}/8 fans, {coreCount}/8 core rotors and " +
                        $"{visual.axles.Count}/13 gear axles, {visual.axles.Sum(axle => axle.Wheels.Length)}/28 wheels.");
        }

        private void BindFans(Aircraft aircraft, Dictionary<string, Transform> frames)
        {
            if (Nozzles == null)
                return;

            foreach (var turbine in SuperheavyNativeDamage.Components<Turbofan>(aircraft))
            {
                var nozzles = (JetNozzle[])Nozzles.GetValue(turbine);
                if (nozzles == null)
                    continue;
                foreach (var nozzle in nozzles)
                {
                    const string prefix = "SHGT_ThrustOutlet_";
                    if (nozzle == null || !nozzle.name.StartsWith(prefix, System.StringComparison.Ordinal))
                        continue;
                    string engine = nozzle.name.Substring(prefix.Length);
                    if (!frames.TryGetValue("SHGT_FanPivot_" + engine, out var frontRotor))
                        continue;
                    frames.TryGetValue("SHGT_CorePivot_" + engine, out var coreRotor);
                    fans.Add(new FanVisual(frontRotor, coreRotor, turbine, aircraft.transform.forward));
                }
            }
        }

        private void BindGear(Dictionary<string, Transform> frames)
        {
            if (!frames.TryGetValue("SHGT_GearMotion", out var host) ||
                !host.TryGetComponent<Animation>(out var animation) || animation.clip == null)
            {
                log.LogWarning("Superheavy landing gear authored animation is missing.");
                return;
            }
            gearHost = host.gameObject;
            gearClip = animation.clip;

            const string prefix = "SHGT_GearAxle_";
            foreach (var item in frames.Where(item => item.Key.StartsWith(prefix, System.StringComparison.Ordinal) &&
                !item.Key.EndsWith("_Mesh", System.StringComparison.Ordinal)))
            {
                string key = item.Key.Substring(prefix.Length);
                Transform axle = item.Value;
                Transform fork = key == "Nose" ? axle.Find("SHGT_NoseForkPivot") : null;
                var wheels = (fork != null ? fork : axle).GetComponentsInChildren<Transform>(true)
                    .Where(wheel => wheel.parent == (fork != null ? fork : axle) &&
                        wheel.name.StartsWith("SHGT_GearSpin_", System.StringComparison.Ordinal))
                    .Select(wheel => new WheelVisual(wheel, transform.right)).ToArray();
                if (wheels.Length != (key == "Nose" ? 4 : 2) ||
                    !frames.TryGetValue("SHGT_GearLeg_" + key + "_Motion", out var leg) ||
                    !frames.TryGetValue("SHGT_GearActuatorRod_" + key, out var rod) ||
                    !SuperheavyGearPhysics.TryGetNative(aircraft, key, out var contact))
                {
                    log.LogWarning($"Superheavy gear could not bind axle {key}.");
                    continue;
                }
                axles.Add(new AxleVisual
                {
                    Key = key,
                    LegFrame = leg,
                    Axle = axle,
                    SteeringFork = fork,
                    ActuatorRod = rod,
                    Rest = axle.localPosition,
                    SuspensionAxis = axle.parent.InverseTransformVector(transform.up),
                    ForkRest = fork != null ? fork.localRotation : Quaternion.identity,
                    ForkAxis = fork != null ? fork.InverseTransformDirection(transform.up) : Vector3.up,
                    Wheels = wheels,
                    Contact = contact,
                });
                contact.onGearBreak += OnGearBreak;
            }
        }

        private void LateUpdate()
        {
            foreach (var fan in fans)
            {
                if (fan.FrontRotor == null || fan.Turbine == null)
                    continue;
                float rpm = fan.Turbine.GetRPM();
                if (float.IsNaN(rpm) || float.IsInfinity(rpm))
                    continue;
                float spin = rpm * 6f * Time.deltaTime;
                fan.FrontRotor.Rotate(fan.FrontAxis, spin, Space.Self);
                if (fan.CoreRotor != null)
                    fan.CoreRotor.Rotate(fan.CoreAxis, spin * CoreVisualSpeedRatio, Space.Self);
            }

            if (gearClip == null)
                return;

            float phase = 1f - SuperheavyGearPhysics.Extension(aircraft);
            if (phase != lastGearPhase)
            {
                gearClip.SampleAnimation(gearHost, gearClip.length * phase);
                if (phase == 0f || phase == 1f)
                    log.LogInfo($"Superheavy gear animation reached {(phase == 0f ? "deployed" : "stowed")} pose.");
                lastGearPhase = phase;
            }

            // Return the oleos and steering to their authored clearance before the
            // bay leaves finish opening, then carry that pose through the folded path.
            float service = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(phase / .18f));
            foreach (var axle in axles)
            {
                if (axle.Broken || !SuperheavyGearPhysics.TryRead(aircraft, axle.Key, out float compression,
                        out float wheelRadiansPerSecond, out float steeringDegrees))
                    continue;
                axle.Axle.localPosition = axle.Rest + axle.SuspensionAxis * (compression * service);
                if (axle.SteeringFork != null)
                    axle.SteeringFork.localRotation = axle.ForkRest *
                        Quaternion.AngleAxis(steeringDegrees * service, axle.ForkAxis);
                axle.SpinDegrees = Mathf.Repeat(axle.SpinDegrees +
                    wheelRadiansPerSecond * Mathf.Rad2Deg * Time.deltaTime, 360f);
                foreach (var wheel in axle.Wheels)
                    wheel.Pivot.localRotation = wheel.Rest * Quaternion.AngleAxis(axle.SpinDegrees, wheel.Axis);
            }
        }

        private void OnGearBreak(LandingGear gear)
        {
            var axle = axles.FirstOrDefault(candidate => candidate.Contact == gear);
            if (axle == null)
                return;
            axle.Broken = true;
            if (NativeHinge?.GetValue(gear) is Transform nativeHinge)
                axle.LegFrame.SetParent(nativeHinge, true);
            axle.ActuatorRod.gameObject.SetActive(false);
            gear.onGearBreak -= OnGearBreak;
        }

        private void OnDestroy()
        {
            foreach (var axle in axles)
                if (axle.Contact != null && !axle.Broken)
                    axle.Contact.onGearBreak -= OnGearBreak;
        }
    }
}
