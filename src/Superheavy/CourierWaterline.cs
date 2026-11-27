using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner, 27/11/2026 (video of a 805 km/h pass a few metres over the sea): "antes mesmo de tocar na água ele já foi
    // danificado por ela", "algumas peças do cockpit já se deslocam".
    // The native aero job counts a part as in the water when its lift point (liftNormal) is less than collisionSize.y
    // above the sea; above 83 m/s such a part loses its joint strength and takes water drag. Several Courier parts have
    // lift points far below their own hull (the fins and the nose side shells were "wet" 10-14 m above the sea), so with the
    // HUD below about 2.8 m the fins and the nose sides broke loose while the belly was about 6 m above the water
    // (flight-waterpass-20261127-073605, flight-waterpass-wet-20261127-081114).
    // Measured (flight-waterline-lift-20261127-081305): the fin lift points sit at the parked wheel line, 9 m below the
    // fins (wet below HUD 2.80 m); the nose side shells and frames use the cockpit lift point, 0.9 m below the wheel line
    // (wet below 2.74 m); the root has no collider and is wet below 2.73 m.
    // Where the native depth would fire before the part's own hull reaches the sea, the job's collisionSize.y (read only
    // by the job's water depth and submerged fraction) is lowered every step to the height of the lift point above the
    // part's lowest collider, negative when the lift point is below the hull, so the part counts as wet exactly when its
    // hull does. A first attempt with a 5 cm floor only moved the breakup from HUD 2.8 m to 0.8 m, the belly still about
    // 4 m above the sea (flight-waterpass-022-contact-20261127-081525). The root has no collider of its own; with a 5 cm
    // depth it splashed (and released every part joined to it) with the wheel line at the sea and the belly still above it
    // (flight-waterpass-022c-contact-20261127-081833), so its hull is the lowest collider of the parts still attached (walked through partLookup: the parts are not
    // children of the root at runtime, so a hierarchy search found none, flight-waterline-root-20261127-082640).
    // The part's own collisionSize (dust, ground-contact size) and the lift point (aerodynamics) are untouched.
    internal static class CourierWaterline
    {
        internal const float MinimumDepth = .05f;
        private static readonly AccessTools.FieldRef<AeroPart, Transform> LiftNormal = AccessTools.FieldRefAccess<AeroPart, Transform>("liftNormal");
        private static readonly Dictionary<AeroPart, Collider[]> Hulls = new Dictionary<AeroPart, Collider[]>();

        internal static void Install(Harmony h)
        {
            // Last: NativePartitionIntegration.OwnedJobBounds writes the owned collision extents in the same postfix slot.
            h.Patch(AccessTools.Method(typeof(AeroPart), "UpdateJobFields"),
                postfix: new HarmonyMethod(typeof(CourierWaterline), nameof(AfterUpdateJobFields)) { priority = Priority.Last });
        }

        private static void AfterUpdateJobFields(AeroPart __instance, ref PtrAllocation<AeroPartFields> ___JobFields)
        {
            if (!___JobFields.IsCreated || !(__instance.parentUnit is Aircraft a) || a.definition == null ||
                a.definition.jsonKey != "jp_superheavy_ground_test")
                return;
            var lift = LiftNormal(__instance);
            if (lift == null) lift = __instance.transform;
            bool root = __instance.transform == a.transform;
            float lowest = float.PositiveInfinity;
            if (root)
            {
                // The parts are not children of the root at runtime; walk the aircraft's part list.
                foreach (var part in a.partLookup)
                    if (part is AeroPart p && p != null && !p.IsDetached())
                        lowest = Mathf.Min(lowest, Lowest(Hull(p)));
            }
            else
                lowest = Lowest(Hull(__instance));
            if (float.IsInfinity(lowest) && !root)
                return;
            // Once the airframe's hull is in the sea, the root keeps the native depth, so its buoyancy after a ditching is the
            // native one (with the lowered depth an intact airframe sank at 2.4 m/s against 1.9 m/s on 0.2.1,
            // flight-ditch4-022-20261127-094152 / flight-ditch4-021-20261127-093722).
            if (root && lowest < Datum.LocalSeaY)
                return;
            // Only lower the native depth where it fires before the hull is in the water; a lift point above the hull keeps
            // the native extents (and buoyancy fraction) unchanged.
            // Negative when the lift point lies below the hull: the job's depth (extY - lift height) is then exactly the
            // depth of the hull's lowest point; its submerged fraction clamps to 0, so such a part takes no water force of
            // its own but still splashes (and, above 83 m/s, breaks) on real contact.
            float real = float.IsInfinity(lowest) ? MinimumDepth : lift.position.y - lowest;
            if (real >= 0f) real = Mathf.Max(MinimumDepth, real);
            else if (real > -MinimumDepth) real = -MinimumDepth;
            ref AeroPartFields fields = ref ___JobFields.Ref();
            if (fields.collisionSize.y > real)
                fields.collisionSize.y = real;
        }

        private static float Lowest(Collider[] hull)
        {
            float lowest = float.PositiveInfinity;
            foreach (var c in hull)
                if (c != null && c.enabled && c.gameObject.activeInHierarchy)
                    lowest = Mathf.Min(lowest, c.bounds.min.y);
            return lowest;
        }

        private static Collider[] Hull(AeroPart part)
        {
            if (Hulls.TryGetValue(part, out var hull) && hull != null)
                return hull;
            var own = new List<Collider>();
            foreach (var c in part.GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger && (c.attachedRigidbody == null || c.attachedRigidbody == part.rb))
                    own.Add(c);
            if (Hulls.Count > 2048)
                Hulls.Clear();
            return Hulls[part] = own.ToArray();
        }
    }
}
