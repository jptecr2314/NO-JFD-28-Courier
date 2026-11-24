using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report on 0.1.8 (23/11/2026, video): the wing spoilers still move unrealistically. Measured in the fixture
    // (flight-ab2-20261123-233422): the hinge itself is right (leading edge still within 1-7 mm), but the visible panels
    // are physical parts pulled toward the hinge command by a ~4 Hz servo spring with limited torque. Under air load they
    // lag, sag and open unevenly (same command: 2 deg on one wing, 10 deg on the other), and the command itself went to
    // 50 deg in flight, so a full speed brake stood the plates almost upright.
    // Here the visible skin of each spoiler panel is posed rigidly from its commanded hinge every frame (the physical
    // part, its colliders, damage and aerodynamics are unchanged), and CourierHandling sets realistic hinge angles.
    [DefaultExecutionOrder(10001)]
    internal sealed class CourierSpoilerSkins : MonoBehaviour
    {
        private sealed class Panel
        {
            internal UnitPart Part;
            internal Transform Hinge;
            internal Transform[] Skins;
            internal Matrix4x4[] Relative;
        }

        private Panel[] panels;

        internal static void Attach(Transform aircraft, ICollection<UnitPart> parts)
        {
            if (aircraft.GetComponent<CourierSpoilerSkins>() != null)
                return;
            var list = new List<Panel>();
            foreach (var part in parts.Where(p => p != null && p.name.StartsWith("NATIVE_Control_Airbrake_", System.StringComparison.Ordinal)))
            {
                var actuator = part.GetComponent<NativePartitionActuator>();
                var hinge = actuator != null ? actuator.Proxy : null;
                if (hinge == null)
                    continue;
                var skins = part.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r != null && r.GetComponent<Collider>() == null && r.transform != part.transform)
                    .Select(r => r.transform).Distinct().ToArray();
                // keep only the outermost skin transforms (children follow their parent)
                skins = skins.Where(t => !skins.Any(o => o != t && t.IsChildOf(o))).ToArray();
                if (skins.Length == 0)
                    continue;
                list.Add(new Panel
                {
                    Part = part,
                    Hinge = hinge,
                    Skins = skins,
                    Relative = skins.Select(t => hinge.worldToLocalMatrix * t.localToWorldMatrix).ToArray()
                });
            }
            if (list.Count == 0)
                return;
            var c = aircraft.gameObject.AddComponent<CourierSpoilerSkins>();
            c.panels = list.ToArray();
            CurrentAirframeDamagePlugin.Log.LogInfo($"Courier spoilers: {list.Count} panels follow their hinge rigidly ({list.Sum(p => p.Skins.Length)} skins).");
        }

        private void LateUpdate()
        {
            if (panels == null)
                return;
            foreach (var p in panels)
            {
                if (p.Part == null || p.Hinge == null || p.Part.IsDetached())
                    continue;
                Matrix4x4 hinge = p.Hinge.localToWorldMatrix;
                for (int i = 0; i < p.Skins.Length; i++)
                {
                    var t = p.Skins[i];
                    if (t == null) continue;
                    Matrix4x4 m = hinge * p.Relative[i];
                    t.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                }
            }
        }
    }
}
