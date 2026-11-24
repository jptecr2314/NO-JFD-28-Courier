using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report for 0.1.8 (23/11/2026): "part of the ramp should move with the other and does not". The Courier ramp
    // folds: the ramp body (SHGT_CargoMesh_012, with the physical NATIVE_CargoRamp part) goes down 40.9 deg, then the
    // toe (SHGT_CargoMesh_011) flips 180 deg over the ramp end and becomes the extension to the ground. While folded,
    // the toe's upper face is the slatted panel drawn by the native ramp surfaces 184-187, but those are children of the
    // physical ramp part, so after the toe flips they stayed lying on the ramp (the circled panel). Here they follow the
    // toe instead: their pose relative to the toe is taken with the ramp closed (toe folded) and applied every frame,
    // so after the flip the slatted face is under the extension, as on a real folding ramp. They stay children of the
    // ramp part (partition visibility and damage keep working); once the ramp part is detached they are left alone.
    [DefaultExecutionOrder(10000)]
    internal sealed class CourierRampToe : MonoBehaviour
    {
        private static readonly string[] FaceNames = { "CURRENT_NATIVE_SURFACE_184", "CURRENT_NATIVE_SURFACE_185", "CURRENT_NATIVE_SURFACE_186", "CURRENT_NATIVE_SURFACE_187" };
        private UnitPart rampPart;
        private Transform toe;
        private CargoRamp gate;
        private Transform[] faces;
        private Vector3[] relPosition;
        private Quaternion[] relRotation;
        private bool captured;

        // Called once from the Courier presentation setup, after the native partition has built its parts.
        internal static void Attach(Transform aircraft, ICollection<UnitPart> parts, IEnumerable<Renderer> renderers)
        {
            var part = parts.FirstOrDefault(p => p != null && p.name == "NATIVE_CargoRamp");
            var list = renderers.Where(r => r != null).ToArray();
            var toe = list.FirstOrDefault(r => r.name == "SHGT_CargoMesh_011")?.transform;
            if (part == null || toe == null || aircraft.GetComponent<CourierRampToe>() != null)
                return;
            var faces = list.Where(r => FaceNames.Contains(r.name) && r.transform.IsChildOf(part.transform) && r.GetComponent<Collider>() == null)
                .Select(r => r.transform).Distinct().ToArray();
            if (faces.Length == 0)
                return;
            var c = aircraft.gameObject.AddComponent<CourierRampToe>();
            c.rampPart = part;
            c.toe = toe;
            c.faces = faces;
            c.gate = aircraft.Find("SHGT_CargoReleaseGate")?.GetComponent<CargoRamp>();
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier ramp: {faces.Length} slatted toe faces follow the folding toe.");
        }

        private void LateUpdate()
        {
            if (toe == null || rampPart == null || faces == null)
                return;
            if (!captured)
            {
                // folded toe: lies along the ramp body, as authored with the ramp closed
                if (gate != null && gate.IsOpen())
                    return;
                relPosition = new Vector3[faces.Length];
                relRotation = new Quaternion[faces.Length];
                for (int i = 0; i < faces.Length; i++)
                {
                    if (faces[i] == null) continue;
                    relPosition[i] = toe.InverseTransformPoint(faces[i].position);
                    relRotation[i] = Quaternion.Inverse(toe.rotation) * faces[i].rotation;
                }
                captured = true;
                return;
            }
            if (rampPart.IsDetached())
            {
                enabled = false;
                return;
            }
            for (int i = 0; i < faces.Length; i++)
            {
                if (faces[i] == null) continue;
                faces[i].SetPositionAndRotation(toe.TransformPoint(relPosition[i]), toe.rotation * relRotation[i]);
            }
        }
    }
}
