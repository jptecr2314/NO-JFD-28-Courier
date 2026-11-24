using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report for 0.1.8 (23/11/2026): the Courier's guns can shoot the Courier itself. Native bullets are a plain
    // linecast against every collider; native turrets only refuse to fire outside their prefab's firing cones, and the
    // Courier's turrets have none. This is a firing cut-out like a real turret's: before the native gun queues rounds,
    // a thin sweep (same layers as a native bullet) runs from each muzzle along its barrel. In the aircraft's frame the round leaves straight
    // along the barrel, so if the sweep meets any part of the same Courier (other than the gun's own turret) within
    // ReachMeters, the trigger counts as released for that physics step: no round, recoil, sound or ammunition use.
    // The gun fires again as soon as the barrel clears. Other aircraft and units are untouched.
    internal static class CourierFireInterlock
    {
        // Radius: a thin ray, just wider than the round, so dispersion alone cannot graze the hull next to the turret.
        internal const float ReachMeters = 120f, Radius = .12f;
        private static readonly AccessTools.FieldRef<Gun, Transform[]> Muzzles = AccessTools.FieldRefAccess<Gun, Transform[]>("muzzles");
        private static readonly AccessTools.FieldRef<Gun, int> TriggerTicks = AccessTools.FieldRefAccess<Gun, int>("ticksSinceTriggerPull");
        private static readonly AccessTools.FieldRef<Gun, float> Queued = AccessTools.FieldRefAccess<Gun, float>("queuedBullets");
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        private static readonly Dictionary<Gun, Transform> TurretRoots = new Dictionary<Gun, Transform>();
        private static readonly Dictionary<Gun, string> Reported = new Dictionary<Gun, string>();
        internal static int BlockedSteps;

        internal static void Install(Harmony h)
        {
            var step = AccessTools.Method(typeof(Gun), "FixedUpdate");
            if (step == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier fire interlock: Gun.FixedUpdate not found."); return; }
            h.Patch(step, prefix: new HarmonyMethod(typeof(CourierFireInterlock), nameof(BeforeGunStep)));
        }

        private static void BeforeGunStep(Gun __instance)
        {
            try
            {
                if (TriggerTicks(__instance) > 2 && Queued(__instance) <= 0f)
                    return; // trigger not held: nothing to cut out
                var unit = __instance.attachedUnit as Aircraft;
                if (unit == null || unit.definition == null || unit.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                if (!Blocked(__instance, unit, out var what))
                    return;
                TriggerTicks(__instance) = int.MaxValue / 2;
                Queued(__instance) = 0f;
                BlockedSteps++;
                string seen = what?.Split(' ')[0];
                if (!Reported.TryGetValue(__instance, out var last) || last != seen)
                {
                    Reported[__instance] = seen;
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier fire interlock: {Path(__instance.transform)} held, barrel points at the Courier ({what}).");
                }
            }
            catch (Exception) { }
        }

        private static string Path(Transform t) => t.parent == null ? t.name : (t.parent.parent == null ? t.parent.name : t.parent.parent.name + "/" + t.parent.name) + "/" + t.name;

        private static bool Blocked(Gun gun, Aircraft aircraft, out string what)
        {
            what = null;
            if (!TurretRoots.TryGetValue(gun, out var own) || own == null)
            {
                var turret = gun.GetComponentInParent<Turret>();
                TurretRoots[gun] = own = turret != null ? turret.transform : gun.transform;
            }
            var muzzles = Muzzles(gun);
            if (muzzles == null || muzzles.Length == 0)
                muzzles = new[] { gun.transform };
            foreach (var muzzle in muzzles)
            {
                if (muzzle == null) continue;
                int count = Physics.SphereCastNonAlloc(muzzle.position, Radius, muzzle.forward, Hits, ReachMeters, ~(int)PhysicsLayers.ExclusionZonesMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    var collider = Hits[i].collider;
                    // distance 0: the sweep starts inside that collider; a native bullet's linecast cannot hit it either.
                    if (collider == null || Hits[i].distance <= 0f || collider.transform.IsChildOf(own)) continue;
                    var part = collider.GetComponentInParent<UnitPart>();
                    if ((part != null && part.parentUnit == aircraft) || collider.GetComponentInParent<Aircraft>() == aircraft)
                    {
                        what = $"{collider.name} at {Hits[i].distance:F2} m, turret {(own.GetComponent<Turret>() is Turret tr ? tr.GetInstanceID() : 0)}";
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
