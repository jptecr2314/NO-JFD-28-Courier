using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundControls : MonoBehaviour
    {
        private Aircraft aircraft;
        private Pivot pilotStick;
        private Pivot copilotStick;
        private Pivot pilotThrottle;
        private Pivot copilotThrottle;

        private sealed class Pivot
        {
            private readonly Transform target;
            private readonly Quaternion neutralRotation;
            private readonly Quaternion aircraftToParent;
            private readonly Quaternion parentToAircraft;
            private readonly Vector3 neutralPosition;
            private readonly Vector3 forwardMetre;

            internal Pivot(Transform target, Transform aircraft)
            {
                this.target = target;
                neutralRotation = target.localRotation;
                neutralPosition = target.localPosition;
                aircraftToParent = Quaternion.Inverse(target.parent.rotation) * aircraft.rotation;
                parentToAircraft = Quaternion.Inverse(aircraftToParent);
                forwardMetre = target.parent.InverseTransformVector(aircraft.forward);
            }

            internal void Rotate(float pitch, float roll)
            {
                var deflection = Quaternion.Euler(pitch, 0f, roll);
                target.localRotation = aircraftToParent * deflection * parentToAircraft * neutralRotation;
            }

            internal void SlideForward(float distance)
            {
                target.localPosition = neutralPosition + forwardMetre * distance;
            }
        }

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            if (aircraft.GetComponent<SuperheavyGroundControls>() != null)
                return;

            var controls = aircraft.gameObject.AddComponent<SuperheavyGroundControls>();
            controls.aircraft = aircraft;
            var pivots = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var part in SuperheavyNativeDamage.Components<Transform>(aircraft))
            {
                if (part.name == "SHGT_PilotJoystick" ||
                    part.name.StartsWith("SHGT_CockpitControl_", StringComparison.Ordinal))
                    pivots[part.name] = part;
            }

            Pivot Bind(string name)
            {
                if (pivots.TryGetValue(name, out var target))
                    return new Pivot(target, aircraft.transform);
                log.LogWarning($"Superheavy cockpit control pivot {name} is missing.");
                return null;
            }

            controls.pilotStick = Bind("SHGT_PilotJoystick");
            controls.copilotStick = Bind("SHGT_CockpitControl_CopilotStick");
            controls.pilotThrottle = Bind("SHGT_CockpitControl_PilotThrottle");
            controls.copilotThrottle = Bind("SHGT_CockpitControl_CopilotThrottle");
            log.LogInfo($"Superheavy cockpit controls bound {pivots.Count}/4 pivots.");
        }

        private void LateUpdate()
        {
            if (aircraft == null || aircraft.disabled || !GameManager.GetLocalAircraft(out var local) || local != aircraft)
                return;

            var input = aircraft.GetInputs();
            float pitch = Mathf.Clamp(input.pitch, -1f, 1f);
            float roll = -Mathf.Clamp(input.roll, -1f, 1f);
            float throttle = Mathf.Clamp01(input.throttle);
            pilotStick?.Rotate(pitch * 12f, roll * 12f);
            copilotStick?.Rotate(pitch * 12f, roll * 12f);
            pilotThrottle?.Rotate(Mathf.Lerp(-20f, 24f, throttle), 0f);
            // The rear pair share the simulator's power axis and travel in straight console slots.
            copilotThrottle?.SlideForward(.050f * throttle);
        }
    }
}
