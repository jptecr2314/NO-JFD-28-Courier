using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier CIWS coverage (0.45.0).
    //  * The dorsal and ventral mounts each covered only their own hemisphere above +5 degrees, so a threat
    //    within +/-5 degrees of the aircraft's plane (typically a head-on, co-altitude missile) was visible to
    //    no mount. Both now reach 6 degrees past their mounting plane; the native 200 m hull linecast in
    //    Turret.FixedUpdate still withholds fire whenever the airframe is in the line of fire.
    //  * Datalink acquisition only sees what the faction already tracks. The aircraft's own missile warning
    //    receiver now also cues the mounts with the missiles it has detected, within range and firing cone.
    internal static class CourierDefenseCoverage
    {
        private const float MinElevation = -6f, ConeAngle = 96f;
        private static readonly AccessTools.FieldRef<Turret, float> Elevation = AccessTools.FieldRefAccess<Turret, float>("minElevation");
        private static readonly AccessTools.FieldRef<FiringCone, float> Cone = AccessTools.FieldRefAccess<FiringCone, float>("coneAngle");
        private static readonly AccessTools.FieldRef<FiringCone, bool> Exclusion = AccessTools.FieldRefAccess<FiringCone, bool>("exclusion");
        private static readonly MethodInfo choose = AccessTools.Method(typeof(Turret), "ChooseTarget");
        internal static int Cued;

        internal static void Install(Harmony h) => h.CreateClassProcessor(typeof(Search)).Patch();

        private static bool Defense(Turret turret, out Aircraft aircraft)
        {
            aircraft = turret.GetAttachedUnit() as Aircraft;
            if (aircraft == null || aircraft.definition == null || aircraft.definition.jsonKey != "jp_superheavy_ground_test") return false;
            var gun = turret.GetWeapon() as Gun;
            return gun != null && gun.info != null && (gun.info.name == "SuperheavyCIWS35_Info" || gun.info.name == "SuperheavyCIWS20_Info");
        }

        [HarmonyPatch(typeof(Turret), "DatalinkTargetSearch")]
        private static class Search
        {
            private static void Prefix(Turret __instance, FiringCone[] ___firingCones)
            {
                if (Elevation(__instance) <= MinElevation || !Defense(__instance, out _)) return;
                Elevation(__instance) = MinElevation;
                if (___firingCones != null) foreach (var cone in ___firingCones) if (cone != null && !Exclusion(cone) && Cone(cone) < ConeAngle) Cone(cone) = ConeAngle;
            }

            private static void Postfix(Turret __instance, bool ___manual, bool ___stowed, WeaponStation ___currentWeaponStation,
                List<Unit> ___potentialTargets, FiringCone[] ___firingCones, Transform ___elevationTransform)
            {
                if (___manual || ___stowed || ___currentWeaponStation == null || ___currentWeaponStation.Ammo <= 0 ||
                    ___potentialTargets == null || ___elevationTransform == null || !Defense(__instance, out var aircraft)) return;
                var warning = aircraft.GetMissileWarningSystem();
                if (warning == null || warning.knownMissiles == null || warning.knownMissiles.Count == 0) return;
                float range = ___currentWeaponStation.WeaponInfo.targetRequirements.maxRange;
                bool added = false;
                foreach (var missile in warning.knownMissiles)
                {
                    if (missile == null || missile.disabled || ___potentialTargets.Contains(missile)) continue;
                    var direction = missile.transform.position - ___elevationTransform.position;
                    if (direction.sqrMagnitude > range * range || !FiringConeChecker.VectorWithinFiringCones(___firingCones, direction, out _)) continue;
                    ___potentialTargets.Add(missile); added = true; Cued++;
                }
                if (added) choose.Invoke(__instance, new object[] { true });
            }
        }
    }
}
