using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier flight assist limits (owner report on 0.1.7, 23/11/2026: full aft stick loops the aircraft
    // easily). The Courier inherits the donor's native fly-by-wire tuning: 4 g, 0.35 rad/s pitch rate and 3 rad/s
    // roll rate, which lets a 400 t transport pull through a loop. When the family attaches its flight model to a
    // Courier, the native limits are set to a heavy transport's: 2.5 g, 0.12 rad/s (about 7 deg/s) pitch rate,
    // 11 deg alpha limiter and 0.40 rad/s (about 23 deg/s) roll rate, plus the pull limiter below. Only the Courier prefab instance is changed;
    // the aerodynamics, other aircraft and flight-assist-off flying are untouched.
    internal static class CourierHandling
    {
        internal const float GLimit = 2.5f, PitchRate = .12f, RollRate = .50f, RollTightness = 4f, AlphaLimit = 11f;
        // Owner report on 0.1.8 (23/11/2026): at low speed the Courier is "almost a rock" in roll. Below the native corner
        // speed (100 m/s on the Courier) the native roll command is 15 % stick plus 85 % of (target rate - rate) x
        // rollTightness, the target being stick x maxRollAngularVel / 2. With the donor's rollTightness 0.25 and 0.40 rad/s,
        // full stick gave about 19 % aileron: 1 deg of bank in 2 s at 290 km/h (flight-rs4-20261123-234559). The target
        // rate stays a heavy transport's (0.25 rad/s, about 14 deg/s) but the gain is 4, so full stick from wings level is
        // full aileron and the command fades as the roll rate reaches the target.
        // Own pull limiter on the family pitch command (the native g limiter alone still let full aft stick loop:
        // 3.5 g, 10.5 deg/s, inverted). Nose-up commands fade out as load factor, pitch rate or attitude reach the
        // soft limits and become a gentle nose-down correction past the hard limits. Nose-down commands and an
        // unloaded stick are never changed.
        internal const float LoadSoft = 2.0f, LoadHard = 2.5f, RateSoft = 4f, RateHard = 7f, AttitudeSoft = 20f, AttitudeHard = 30f;
        private static Type filterType;
        // Courier flight-assist filters adjusted so far (read by the private flight probe).
        internal static readonly List<Component> Assists = new List<Component>();
        private static FieldInfo flyByWire, dynamicsAircraft, dynamicsBody;

        internal static void Install(Harmony h)
        {
            var dynamics = CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics");
            var attach = dynamics == null ? null : AccessTools.Method(dynamics, "Attach");
            filterType = AccessTools.TypeByName("ControlsFilter");
            flyByWire = filterType == null ? null : AccessTools.Field(filterType, "flyByWire");
            if (attach == null || flyByWire == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier handling: native flight assist not found; limits unchanged.");
                return;
            }
            h.Patch(attach, postfix: new HarmonyMethod(typeof(CourierHandling), nameof(AfterAttach)));
            InstallSpoilerTravel(h);
            var limit = AccessTools.Method(dynamics, "LimitRotation");
            dynamicsAircraft = AccessTools.Field(dynamics, "aircraft");
            dynamicsBody = AccessTools.Field(dynamics, "body");
            if (limit == null || limit.ReturnType != typeof(float) || dynamicsAircraft == null || dynamicsBody == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier handling: family pitch path not found; pull limiter off.");
                return;
            }
            h.Patch(limit, postfix: new HarmonyMethod(typeof(CourierHandling), nameof(AfterLimitRotation)));
        }

        private static void AfterLimitRotation(object __instance, ref float __result)
        {
            if (__result >= 0f) // positive native pitch is nose-down
                return;
            try
            {
                var aircraft = dynamicsAircraft.GetValue(__instance) as Aircraft;
                var body = dynamicsBody.GetValue(__instance) as Rigidbody;
                if (aircraft == null || body == null || aircraft.definition == null || aircraft.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                Transform t = ((Component)__instance).transform;
                float noseUpRate = -Vector3.Dot(body.angularVelocity, t.right); // rad/s
                float attitude = Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                float level = Mathf.Clamp01(Vector3.Dot(t.up, Vector3.up));
                float load = body.velocity.magnitude * noseUpRate / Physics.gravity.magnitude + level;
                float rate = noseUpRate * Mathf.Rad2Deg;
                float keep = Mathf.Min(1f - Mathf.InverseLerp(LoadSoft, LoadHard, load),
                             Mathf.Min(1f - Mathf.InverseLerp(RateSoft, RateHard, rate), 1f - Mathf.InverseLerp(AttitudeSoft, AttitudeHard, attitude)));
                float over = Mathf.Clamp01(Mathf.Max((load - LoadHard) / .5f, Mathf.Max((rate - RateHard) / 3f, (attitude - AttitudeHard) / 5f)));
                __result = __result * keep + .3f * over;
            }
            catch (Exception) { }
        }

        private static void AfterAttach(Aircraft __0)
        {
            try
            {
                if (__0 == null || __0.definition == null || __0.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                foreach (var filter in __0.GetComponentsInChildren(filterType, true))
                {
                    object fbw = flyByWire.GetValue(filter);
                    if (fbw == null) continue;
                    float before = Read(fbw, "gLimitPositive");
                    Write(fbw, "gLimitPositive", GLimit);
                    Write(fbw, "maxPitchAngularVel", PitchRate);
                    Write(fbw, "maxRollAngularVel", RollRate);
                    Write(fbw, "rollTightness", RollTightness);
                    Write(fbw, "alphaLimiter", AlphaLimit);
                    flyByWire.SetValue(filter, fbw); // also correct if FlyByWire is a struct
                    Assists.RemoveAll(x => x == null);
                    Assists.Add(filter);
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier handling: flight assist {before:F1} g -> {GLimit} g, pitch {PitchRate} rad/s, roll {RollRate} rad/s x {RollTightness}, alpha {AlphaLimit} deg.");
                }
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier handling: " + e.Message); }
        }

        // Owner report on 0.1.7: the wing spoilers open wrongly in roll. The visible spoiler panels are native parts
        // jointed to the wing root, and the GE-87 bake mapped slivers of the wing-root aft fairing to the innermost
        // panels (nearest painted triangle), so a strip of fixed fairing lifted and twisted with spoiler 01 on each
        // side. Each such fairing piece is moved, at its current world pose, onto the wing part its spoiler is
        // jointed to; the spoiler then moves alone and the fairing stays with the wing.
        // Owner report on 0.1.7: in roll the spoilers "rose like steps" instead of swinging on their hinges. Measured on
        // the real vertices, the hinge is right (leading edge still within 2 mm), but the family commands only
        // 0.38 x 50 deg = 19 deg in roll and the visible panels, servo-driven physical parts under air load, reached about
        // 11 deg. Owner report on 0.1.8 (video): the motion is still unrealistic; a full speed brake stood the panels
        // almost upright (50 deg in flight) and the servo-driven panels lagged and opened unevenly.
        // For the Courier the hinge command of every panel is now set here, as on a heavy transport:
        //  - speed brake: up to 40 deg in flight, 55 deg on the ground (ground spoilers);
        //  - roll: up to 35 deg on the wing going down, and the opposite wing's speed brake retracts by the same share;
        //  - the hinge moves at most 60 deg/s.
        // CourierSpoilerSkins poses the visible panels rigidly from this hinge. The family's symmetric drag and roll
        // force are as before (they do not depend on the angle).
        internal const float FlightBrakeAngle = 40f, GroundBrakeAngle = 55f, RollAngle = 35f, HingeRate = 60f, RollFull = .38f;
        private static FieldInfo controlSpoilers, controlRollOpening, spoilerActuator, spoilerRest, spoilerSide, spoilerTravel, controlAircraft;
        private static readonly AccessTools.FieldRef<Airbrake, float> AirbrakeOpening = AccessTools.FieldRefAccess<Airbrake, float>("openAmount");
        private static readonly Dictionary<Airbrake, float> HingeAngles = new Dictionary<Airbrake, float>();

        private static void InstallSpoilerTravel(Harmony h)
        {
            var controls = CurrentAirframeCompatibility.Family("SuperheavyGroundFlightControls");
            var late = controls == null ? null : AccessTools.Method(controls, "LateUpdate");
            var panel = controls == null ? null : AccessTools.Inner(controls, "Spoiler");
            controlSpoilers = controls == null ? null : AccessTools.Field(controls, "spoilers");
            controlRollOpening = controls == null ? null : AccessTools.Field(controls, "rollOpening");
            controlAircraft = controls == null ? null : AccessTools.Field(controls, "aircraft");
            if (panel != null)
            {
                spoilerActuator = AccessTools.Field(panel, "Actuator");
                spoilerRest = AccessTools.Field(panel, "RestRotation");
                spoilerSide = AccessTools.Field(panel, "Side");
                spoilerTravel = AccessTools.Field(panel, "Travel");
            }
            if (late == null || controlSpoilers == null || controlRollOpening == null || controlAircraft == null || spoilerActuator == null ||
                spoilerRest == null || spoilerSide == null || spoilerTravel == null)
            {
                CurrentAirframeDamagePlugin.Log.LogWarning("Courier handling: family spoiler fields not found; spoiler angles unchanged.");
                return;
            }
            h.Patch(late, postfix: new HarmonyMethod(typeof(CourierHandling), nameof(AfterSpoilerPose)));
        }

        private static void AfterSpoilerPose(object __instance)
        {
            try
            {
                var aircraft = controlAircraft.GetValue(__instance) as Aircraft;
                if (aircraft == null || aircraft.definition == null || aircraft.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                if (!(controlSpoilers.GetValue(__instance) is Array panels))
                    return;
                float roll = (float)controlRollOpening.GetValue(__instance);
                bool ground = aircraft.radarAlt < 4f;
                float brakeMax = ground ? GroundBrakeAngle : FlightBrakeAngle;
                float step = HingeRate * Time.deltaTime;
                if (HingeAngles.Count > 256)
                    foreach (var dead in HingeAngles.Keys.Where(k => k == null).ToList()) HingeAngles.Remove(dead);
                foreach (object panel in panels)
                {
                    var brake = spoilerActuator.GetValue(panel) as Airbrake;
                    if (brake == null || !brake.isActiveAndEnabled)
                        continue;
                    float side = (float)spoilerSide.GetValue(panel);
                    float rollShare = Mathf.Clamp(roll * side / RollFull, -1f, 1f);
                    float target = Mathf.Clamp(AirbrakeOpening(brake) * brakeMax + rollShare * RollAngle, 0f, brakeMax);
                    HingeAngles.TryGetValue(brake, out float angle);
                    angle = Mathf.MoveTowards(angle, target, step);
                    HingeAngles[brake] = angle;
                    float sign = Mathf.Sign((float)spoilerTravel.GetValue(panel));
                    brake.transform.localRotation = (Quaternion)spoilerRest.GetValue(panel) * Quaternion.AngleAxis(sign * angle, Vector3.right);
                }
            }
            catch (Exception) { }
        }

        // Called once from the Courier presentation setup, after the native partition has built its parts.
        internal static void RehostRootFairing(ICollection<UnitPart> parts)
        {
            int moved = 0;
            foreach (var part in parts.Where(p => p != null && p.name.StartsWith("NATIVE_Control_Airbrake_", StringComparison.Ordinal)).ToArray())
            {
                var pieces = part.transform.Cast<Transform>().Where(t => t.name.StartsWith("GE87 ", StringComparison.Ordinal)).ToArray();
                if (pieces.Length == 0) continue;
                Rigidbody own = part.rb;
                Transform wing = part.GetComponentsInChildren<Joint>(true).Concat(part.GetComponentsInParent<Joint>(true))
                    .Select(j => j.connectedBody).FirstOrDefault(b => b != null && b != own)?.transform;
                if (wing == null)
                {
                    string suffix = part.name.Substring(part.name.IndexOf("_Wing_", StringComparison.Ordinal) + 1);
                    wing = parts.FirstOrDefault(p => p != null && p != part && !p.name.Contains("Control_") && p.name.EndsWith(suffix, StringComparison.Ordinal))?.transform;
                }
                if (wing == null) continue;
                foreach (var piece in pieces) { piece.SetParent(wing, true); moved++; }
            }
            if (moved > 0)
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier handling: {moved} wing-root fairing pieces moved off the spoiler panels onto the wing.");
        }

        private static float Read(object o, string name) => AccessTools.Field(o.GetType(), name) is FieldInfo f ? Convert.ToSingle(f.GetValue(o)) : float.NaN;

        private static void Write(object o, string name, float value)
        {
            var f = AccessTools.Field(o.GetType(), name);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name);
            f.SetValue(o, value);
        }
    }
}
