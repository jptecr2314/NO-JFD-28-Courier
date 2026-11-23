using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyGunship
    {
        private const string InfoPrefix = "BG280G_";
        private static readonly AccessTools.FieldRef<Turret, bool> Manual = AccessTools.FieldRefAccess<Turret, bool>("manual");
        private static readonly AccessTools.FieldRef<AimSolver, Unit> BoundTarget = AccessTools.FieldRefAccess<AimSolver, Unit>("currentTarget");
        private static ManualLogSource log;
        [ThreadStatic] private static Gun automaticGun;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(RegisterMountedTurrets)).Patch();
            harmony.CreateClassProcessor(typeof(OwnAimOrigin)).Patch();
            harmony.CreateClassProcessor(typeof(AutomaticTurretScope)).Patch();
            harmony.CreateClassProcessor(typeof(AutomaticGunAuthority)).Patch();
            harmony.CreateClassProcessor(typeof(ObservedBulletOwner)).Patch();
            harmony.CreateClassProcessor(typeof(ManualNuclearBattery)).Patch();
            harmony.CreateClassProcessor(typeof(Guided155Target)).Patch();
            harmony.CreateClassProcessor(typeof(NuclearTargetSelection)).Patch();
            harmony.CreateClassProcessor(typeof(MountAmmunition)).Patch();
            log.LogInfo("BG-280 ventral batteries active: independent turret authority; tactical 155/203 mm remain manual-only.");
        }

        private static bool IsBg(Aircraft owner) => owner != null && owner.definition?.jsonKey == "jp_bg280_bomber";

        [HarmonyPatch(typeof(Hardpoint), nameof(Hardpoint.SpawnMount))]
        private static class RegisterMountedTurrets
        {
            private static void Postfix(Aircraft __0, WeaponMount __1, GameObject __result)
            {
                if (!IsBg(__0) || __result == null || __1?.info == null ||
                    !__1.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal)) return;
                int added = 0;
                // Native SpawnMount already registers every Gun, including its
                // ammo mass. It only attaches the first Turret. Never register a
                // Gun twice to repair that separate turret-controller omission.
                foreach (var turret in __result.GetComponentsInChildren<Turret>(false))
                {
                    var gun = turret.GetWeapon() as Gun;
                    if (gun?.info == null || !gun.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal)) continue;
                    var station = __0.weaponStations.FirstOrDefault(s => s.Weapons.Contains(gun));
                    if (station == null)
                    {
                        log.LogError("BG-280 mounted gun has no native station: " + gun.name);
                        continue;
                    }
                    if (station.Turrets != null && station.Turrets.Contains(turret)) continue;
                    turret.AttachToWeaponManager(__0);
                    added++;
                }
                if (added > 0) log.LogInfo("BG-280 registered " + added + " additional native turret controller(s) for " + __1.jsonKey + ".");
            }
        }

        private static Gun Battery(Turret turret)
        {
            if (!(turret.GetAttachedUnit() is Aircraft owner) || !IsBg(owner)) return null;
            var gun = turret.GetWeapon() as Gun;
            return gun != null && gun.info != null && gun.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal) ? gun : null;
        }

        [HarmonyPatch(typeof(Turret), "FixedUpdate")]
        private static class AutomaticTurretScope
        {
            private static void Prefix(Turret __instance, bool ___manual, out Gun __state)
            {
                __state = automaticGun;
                automaticGun = ___manual ? null : Battery(__instance);
            }
            private static void Finalizer(Gun __state) => automaticGun = __state;
        }

        [HarmonyPatch(typeof(Gun), nameof(Gun.Fire), new[] { typeof(Unit), typeof(Unit), typeof(Vector3), typeof(WeaponStation), typeof(GlobalPosition) })]
        private static class AutomaticGunAuthority
        {
            private static bool Prefix(Gun __instance) => automaticGun == null || automaticGun == __instance;
        }

        [HarmonyPatch(typeof(BulletSim), nameof(BulletSim.Create))]
        private static class ObservedBulletOwner
        {
            private static void Prefix(Unit __0, Gun __1, ref Turret __2)
            {
                if (!(__0 is Aircraft owner) || !IsBg(owner) || __1?.info == null ||
                    !__1.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal)) return;
                // Native Gun.SpawnBullet passes station.GetTurret() (the first
                // controller). Each pair's second gun must report trajectory
                // observations to its own native AimSolver instead.
                var own = __1.GetComponentInParent<Turret>();
                if (own != null) __2 = own;
            }
        }

        [HarmonyPatch(typeof(Turret), nameof(Turret.SetTarget))]
        private static class OwnAimOrigin
        {
            private static void Prefix(AimSolver ___aimSolver, out Unit __state) =>
                __state = ___aimSolver == null ? null : BoundTarget(___aimSolver);

            private static void Postfix(Turret __instance, AimSolver ___aimSolver, bool ___manual,
                ref float ___timeOnTarget, Unit __state)
            {
                var gun = Battery(__instance);
                if (gun == null || ___aimSolver == null)
                    return;
                Unit target = __instance.GetTarget();
                ___aimSolver.SetTarget(__instance.GetAttachedUnit(), target, gun.transform, gun.info);
                gun.SetTarget(target);
                if (!___manual && target != __state)
                {
                    ___timeOnTarget = 0f;
                    gun.Safety = true;
                }
            }
        }

        [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
        private static class NuclearTargetSelection
        {
            private static bool Prefix(Turret __instance)
            {
                var gun = Battery(__instance);
                // Keep the optional tactical shell out of autonomous datalink
                // target selection. The selected native weapon station still aims it.
                return gun == null || !gun.info.nuclear || Manual(__instance);
            }
        }

        [HarmonyPatch(typeof(Gun), nameof(Gun.Fire), new[] { typeof(Unit), typeof(Unit), typeof(Vector3), typeof(WeaponStation), typeof(GlobalPosition) })]
        private static class ManualNuclearBattery
        {
            private static bool Prefix(Gun __instance)
            {
                if (__instance.info == null || !__instance.info.nuclear ||
                    !__instance.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal))
                    return true;
                var turret = __instance.GetComponentInParent<Turret>();
                return turret != null && Manual(turret);
            }
        }

        [HarmonyPatch(typeof(Gun), nameof(Gun.Fire), new[] { typeof(Unit), typeof(Unit), typeof(Vector3), typeof(WeaponStation), typeof(GlobalPosition) })]
        private static class Guided155Target
        {
            [HarmonyPriority(Priority.Last)]
            private static bool Prefix(Gun __instance, Unit __1)
            {
                if (__instance.info == null ||
                    (!__instance.info.name.StartsWith("BG280G_155", StringComparison.Ordinal) &&
                     !__instance.info.name.StartsWith("BG280G_203", StringComparison.Ordinal)))
                    return true;
                // Native WeaponManager permits gun fire with an empty target list,
                // while InertialSeekerShell dereferences its datalink target. Gun.Fire
                // itself ignores its target argument; SpawnBullet uses the separate
                // cache populated by Gun.SetTarget. Keep guided 155/203 variants on that
                // native target path, without restricting unguided 30/57 mm fire.
                if (__1 == null || __1.disabled)
                    return false;
                __instance.SetTarget(__1);
                return true;
            }
        }

        [HarmonyPatch(typeof(WeaponMount), nameof(WeaponMount.Initialize))]
        private static class MountAmmunition
        {
            private static readonly AccessTools.FieldRef<Gun, int> Capacity = AccessTools.FieldRefAccess<Gun, int>("magazineCapacity");
            private static readonly AccessTools.FieldRef<Gun, int> Reserves = AccessTools.FieldRefAccess<Gun, int>("magazines");

            private static void Postfix(WeaponMount __instance)
            {
                if (__instance.info == null || !__instance.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal) || __instance.prefab == null)
                    return;
                var guns = __instance.prefab.GetComponentsInChildren<Gun>(true)
                    .Where(g => g.info != null && g.info.name.StartsWith(InfoPrefix, StringComparison.Ordinal)).Distinct().ToArray();
                if (guns.Length == 0)
                    return;
                __instance.ammo = guns.Sum(g => Capacity(g) * (1 + Reserves(g)));
                __instance.mass = __instance.emptyMass + __instance.ammo * __instance.info.massPerRound;
                __instance.mountName = __instance.info.weaponName + (guns.Length > 1 ? " x" + guns.Length : "") +
                    " (" + __instance.ammo + " rounds)";
            }
        }
    }
}
