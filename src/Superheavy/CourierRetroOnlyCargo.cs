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

        internal static void Install(Harmony h)
        {
            h.CreateClassProcessor(typeof(MenuOpen)).Patch();
            var vet = AccessTools.Method(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout));
            var runtime = CurrentAirframeCompatibility.Family("SuperheavySpatialCargoRuntime");
            addOptional = runtime == null ? null : AccessTools.Method(runtime, "AddAryxCargoOptions");
            if (vet != null)
                h.Patch(vet, prefix: new HarmonyMethod(typeof(CourierRetroOnlyCargo), nameof(BeforeVet)) { priority = Priority.Last });
        }

        // Owner report for 0.1.8 (23/11/2026): with more than two loads, the rest did not appear (two HLT MArt in front,
        // seven CRV-L Serval Mortars [Retro Kit] behind: only the MArts spawned). The menu adds the optional mod cargos
        // (Aryx) to the aircraft being shown, and the Retro Kit swap then replaces them by their "JP_RDK_" versions,
        // but the spawn check (WeaponChecker.VetLoadout) compares the loadout against the Courier prefab, which never
        // got those options: Player.log showed "VetWeapon failed ... JP_RDK_Aryx_CRVL_1_Mortar_x1 ... Mount is not in
        // hardpointSet's options" seven times, and each refused load was dropped. Before that check the prefab gets
        // the same optional cargos and the same Retro Kit swap as the menu, and every cargo option the shown aircraft
        // offers in a set.
        private static System.Reflection.MethodInfo addOptional;
        private static readonly Dictionary<HardpointSet, HardpointSet> Shown = new Dictionary<HardpointSet, HardpointSet>();

        private static void BeforeVet(AircraftDefinition __0)
        {
            try
            {
                if (__0 == null || __0.jsonKey != "jp_superheavy_ground_test" || __0.unitPrefab == null) return;
                var prefab = __0.unitPrefab.GetComponent<Aircraft>();
                if (prefab == null || prefab.weaponManager == null) return;
                int added = Prepare(prefab.weaponManager);
                if (added > 0) CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cargo check: {added} menu cargo options added to the Courier prefab before the spawn check.");
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cargo check: " + e.Message); }
        }

        private static int Prepare(WeaponManager manager)
        {
            var sets = manager.hardpointSets;
            if (sets == null) return 0;
            int before = 0;
            foreach (var set in sets) before += set?.weaponOptions?.Count ?? 0;
            if (addOptional != null && Encyclopedia.WeaponLookup != null)
                addOptional.Invoke(null, new object[] { manager, Encyclopedia.WeaponLookup });
            PreferRetro(manager);
            int added = 0;
            foreach (var set in sets)
            {
                if (set?.weaponOptions == null || !Shown.TryGetValue(set, out var shown) || shown?.weaponOptions == null) continue;
                foreach (var mount in shown.weaponOptions)
                    if (mount != null && mount.Cargo && !set.weaponOptions.Contains(mount)) { set.weaponOptions.Add(mount); added++; }
            }
            int after = 0;
            foreach (var set in sets) after += set?.weaponOptions?.Count ?? 0;
            return Math.Max(added, after - before);
        }

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
                    if (prefab != null && prefab != __0)
                    {
                        changed += PreferRetro(prefab.weaponManager);
                        // remember which shown set belongs to which prefab set (same index) for the spawn check
                        var shown = __0.weaponManager?.hardpointSets; var own = prefab.weaponManager?.hardpointSets;
                        if (shown != null && own != null && shown.Length == own.Length)
                            for (int i = 0; i < own.Length; i++) if (own[i] != null && shown[i] != null && own[i] != shown[i]) Shown[own[i]] = shown[i];
                    }
                    if (changed > 0) CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cargo menu: {changed} normal loads replaced by their Retro Kit versions.");
                }
                catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cargo menu (retro only): " + e.Message); }
            }
        }
    }
}
