using System;
using System.Collections.Generic;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundHighLift : MonoBehaviour
    {
        private const string HingePrefix = "SHGT_Control_";
        private const string SkinPrefix = "SHGT_ControlMesh_";
        private const string FlapPrefix = HingePrefix + "Flap_";
        private const string SlatPrefix = HingePrefix + "Slat_";

        private sealed class Surface
        {
            internal Transform Hinge;
            internal Transform Skin;
            internal Quaternion HingeRestRotation;
            internal Vector3 SkinRestPosition;
            internal Quaternion SkinRestRotation;
            internal Vector3 Travel;
            internal float Deflection;
            internal float RotationStartsAt;
        }

        private Surface[] surfaces;
        private SuperheavyFlightDynamics dynamics;

        internal static void Attach(Aircraft aircraft)
        {
            if (aircraft.TryGetComponent<SuperheavyGroundHighLift>(out _))
                return;

            var surfaces = new List<Surface>(10);
            foreach (Transform hinge in SuperheavyNativeDamage.Components<Transform>(aircraft))
            {
                bool slat = hinge.name.StartsWith(SlatPrefix, StringComparison.Ordinal);
                if (!slat && !hinge.name.StartsWith(FlapPrefix, StringComparison.Ordinal))
                    continue;

                Transform skin = hinge.Find(SkinPrefix + hinge.name.Substring(HingePrefix.Length));
                if (skin == null)
                    continue;

                Vector3 travel = slat ? new Vector3(0f, -.11f, .46f) : new Vector3(0f, -.15f, -1.04f);
                surfaces.Add(new Surface
                {
                    Hinge = hinge,
                    Skin = skin,
                    HingeRestRotation = hinge.localRotation,
                    SkinRestPosition = skin.localPosition,
                    SkinRestRotation = skin.localRotation,
                    Travel = hinge.InverseTransformVector(aircraft.transform.TransformVector(travel)),
                    Deflection = slat ? 10f : 30f,
                    RotationStartsAt = slat ? .08f : .16f
                });
            }

            if (surfaces.Count > 0 && aircraft.TryGetComponent(out SuperheavyFlightDynamics dynamics))
            {
                var highLift = aircraft.gameObject.AddComponent<SuperheavyGroundHighLift>();
                highLift.surfaces = surfaces.ToArray();
                highLift.dynamics = dynamics;
            }
        }

        private void LateUpdate()
        {
            if (dynamics == null)
                return;

            float progress = dynamics.FlapPosition;
            foreach (Surface surface in surfaces)
            {
                if (surface.Skin == null || surface.Hinge == null)
                    continue;

                // Cancel the stock gear-driven hinge pose at the skin, then use
                // the same fraction as the lift model for both travel and rotation.
                Quaternion nativeDelta = Quaternion.Inverse(surface.HingeRestRotation) * surface.Hinge.localRotation;
                float rotationProgress = Mathf.InverseLerp(surface.RotationStartsAt, 1f, progress);
                Quaternion desired = Quaternion.AngleAxis(-surface.Deflection * Smooth(rotationProgress), Vector3.right);
                Quaternion skinOffset = Quaternion.Inverse(nativeDelta) * desired;

                surface.Skin.localPosition = skinOffset * surface.SkinRestPosition +
                    Quaternion.Inverse(nativeDelta) * (surface.Travel * Smooth(progress));
                surface.Skin.localRotation = skinOffset * surface.SkinRestRotation;
            }
        }

        private static float Smooth(float value) => value * value * (3f - 2f * value);
    }
}
