using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Japensou.NuclearOption.CurrentDamage
{
    // 23/11/2026 (owner): when a cargo exists both as the normal load and as its Retro Kit variant (optional
    // Type12RetroKit mod, keys "JP_RDK_" + original key), the Courier menu offers only the Retro Kit one. The retro
    // kit already prefers its variants on the Courier, but loads added to the menu later (the family's optional mod
    // cargos, preview instances) could keep both. This runs last when the loadout menu opens, on the aircraft being
    // shown and on its prefab; other aircraft and the encyclopedia are untouched. Without the retro kit nothing changes.
    internal static class CourierRetroOnlyCargo
    {
        private const string RetroPrefix = "JP_RDK_";

        internal static void Install(Harmony h) => h.CreateClassProcessor(typeof(MenuOpen)).Patch();

        private static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        internal static int PreferRetro(WeaponManager manager)
        {
            if (manager == null || manager.hardpointSets == null || Encyclopedia.WeaponLookup == null) return 0;
            int changed = 0;
            foreach (var set in manager.hardpointSets)
            {
                var options = set?.weaponOptions;
                if (options == null) continue;
                for (int i = 0; i < options.Count; i++)
                {
                    var normal = options[i];
                    if (normal == null || string.IsNullOrEmpty(normal.jsonKey) || normal.jsonKey.StartsWith(RetroPrefix, StringComparison.Ordinal)) continue;
                    if (!Encyclopedia.WeaponLookup.TryGetValue(RetroPrefix + normal.jsonKey, out var retro) || retro == null) continue;
                    if (options.Contains(retro)) { options.RemoveAt(i); i--; }
                    else options[i] = retro;
                    changed++;
                }
            }
            return changed;
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.AssignAircraft))]
        private static class MenuOpen
        {
            [HarmonyPriority(Priority.Last - 1)]
            [HarmonyAfter("br.japensou.nuclearoption.type12retro")]
            private static void Prefix(Aircraft __0)
            {
                try
                {
                    if (!IsCourier(__0)) return;
                    int changed = PreferRetro(__0.weaponManager);
                    var prefab = __0.definition.unitPrefab != null ? __0.definition.unitPrefab.GetComponent<Aircraft>() : null;
                    if (prefab != null && prefab != __0) changed += PreferRetro(prefab.weaponManager);
                    if (changed > 0) CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cargo menu: {changed} normal loads replaced by their Retro Kit versions.");
                }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cargo menu (retro only): " + e.Message); }
            }
        }
    }
}
