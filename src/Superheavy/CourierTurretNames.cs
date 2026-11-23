using System;
using HarmonyLib;

namespace Japensou.NuclearOption.CurrentDamage
{
    // 23/11/2026 (owner): the flank 20 mm turrets were listed in the loadout selector as "Sky Sentry 20 mm" (the
    // optional Chimera model the visual can borrow), unlike the 35 mm "JFD-28 CIWS" turrets. Runtime renames, bundle
    // frozen: hardpoint set "Flank Defense", mount "2 x 20 mm JFD-28 CIWS (7200 rounds each)", weapon
    // "20 mm JFD-28 CIWS". Applied when the loadout menu opens and when the Courier spawns its weapons.
    internal static class CourierTurretNames
    {
        private const string OldSet = "Sky Sentry 20 mm Flanks", NewSet = "Flank Defense";
        private const string MountKey = "jp_superheavy_ciws20";
        private const string MountName = "2 × 20 mm JFD-28 CIWS (7200 rounds each)", WeaponName = "20 mm JFD-28 CIWS";

        internal static void Install(Harmony h)
        {
            h.CreateClassProcessor(typeof(MenuOpen)).Patch();
            h.CreateClassProcessor(typeof(WeaponsSpawned)).Patch();
        }

        private static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        private static void Rename(WeaponManager manager)
        {
            if (manager?.hardpointSets == null) return;
            foreach (var set in manager.hardpointSets)
                if (set != null && set.name == OldSet) set.name = NewSet;
        }

        internal static void Apply(Aircraft a)
        {
            if (!IsCourier(a)) return;
            Rename(a.weaponManager);
            var prefab = a.definition.unitPrefab != null ? a.definition.unitPrefab.GetComponent<Aircraft>() : null;
            if (prefab != null && prefab != a) Rename(prefab.weaponManager);
            if (Encyclopedia.WeaponLookup != null && Encyclopedia.WeaponLookup.TryGetValue(MountKey, out var mount) && mount != null)
            {
                // the family's WeaponMount.Initialize postfix rebuilds the name as "<weapon> (<ammo> rounds)": keep its
                // wording and ammunition, only the product name changes
                mount.mountName = string.IsNullOrEmpty(mount.mountName) ? MountName
                    : mount.mountName.Replace("Sky Sentry 20 mm CIWS", WeaponName).Replace("Sky Sentry 20 mm", WeaponName);
                if (mount.info != null) mount.info.weaponName = WeaponName;
            }
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.AssignAircraft))]
        private static class MenuOpen
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Aircraft __0)
            {
                try { Apply(__0); }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier turret names (menu): " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
        private static class WeaponsSpawned
        {
            private static void Prefix(WeaponManager __instance)
            {
                try { Apply(__instance != null ? __instance.GetComponentInParent<Aircraft>() : null); }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier turret names (spawn): " + e.Message); }
            }
        }
    }
}
