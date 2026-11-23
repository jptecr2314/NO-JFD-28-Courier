using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    // Keep the 0.32 identity and positional saves readable without registering
    // another aircraft. Lookup aliases never enter the native aircraft/index lists.
    internal sealed class SuperheavyFamilyCompatibility : MonoBehaviour
    {
        internal const string BomberKey = "jp_bg280_bomber";
        internal const string LegacyGunshipKey = "jp_bg280_gunship";
        private const string LegacyRackKey = "jp_bg280_gbm32";
        private const string AftLightRackKey = "jp_bg280_gbm_magazine3_32";
        private static ManualLogSource log;
        private float nextCheck;
        private static readonly System.Reflection.FieldInfo SelectedAircraft = AccessTools.Field(typeof(LoadoutSelector), "aircraft");
        private static readonly AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> Controls =
            AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");

        internal static void Install(GameObject host, Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            host.AddComponent<SuperheavyFamilyCompatibility>();
            harmony.CreateClassProcessor(typeof(LegacyPrefabPatch)).Patch();
            harmony.CreateClassProcessor(typeof(SavedSelectionPatch)).Patch();
            harmony.CreateClassProcessor(typeof(DefaultSelectionPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PhysicalSelectionPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PhysicalSpawnPatch)).Patch();
        }

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            EnsureAlias();
        }

        internal static void EnsureAlias()
        {
            var lookup = Encyclopedia.Lookup;
            if (lookup == null || !lookup.TryGetValue(BomberKey, out var canonical) || !(canonical is AircraftDefinition)) return;
            if (lookup.TryGetValue(LegacyGunshipKey, out var previous) && previous == canonical) return;
            lookup[LegacyGunshipKey] = canonical;
            log?.LogInfo("BG-280 legacy gunship key now resolves to the unified aircraft; no extra catalogue entry.");
        }

        [HarmonyPatch(typeof(Encyclopedia), nameof(Encyclopedia.TryGetPrefab))]
        private static class LegacyPrefabPatch
        {
            private static void Prefix(string __0)
            {
                // Saved missions can ask for the old key in the same frame that
                // Blueprinter finishes, before this component's next Update.
                if (__0 == LegacyGunshipKey) EnsureAlias();
            }
        }

        private static bool IsBomber(WeaponManager manager)
        {
            if (manager == null) return false;
            var owner = SuperheavyNativeDamage.Owner(manager);
            if (owner?.definition?.jsonKey == BomberKey) return true;
            return Encyclopedia.Lookup != null && Encyclopedia.Lookup.TryGetValue(BomberKey, out var definition) &&
                definition.unitPrefab?.GetComponent<Aircraft>()?.weaponManager == manager;
        }

        private static List<string> NormalizeKeys(IList<string> keys, WeaponManager manager)
        {
            int count = manager.hardpointSets.Length;
            var result = Enumerable.Repeat<string>(null, count).ToList();
            bool oldBomber = keys.Count == 8 && count >= 9;
            if (oldBomber)
            {
                // Preserve the old 64-round mission rather than silently loading
                // 420 rounds and adding 178 tonnes. The middle magazine stays empty.
                result[0] = keys[0];
                result[2] = keys[1] == LegacyRackKey ? AftLightRackKey : keys[1];
                for (int i = 2; i < keys.Count; i++) result[i + 1] = keys[i];
            }
            else
                for (int i = 0; i < Math.Min(keys.Count, count); i++) result[i] = keys[i];
            if (result.Count > 2 && IsHeavy(result[2]) && result[1] == "jp_bg280_gbm_magazine2")
                result[1] = null;
            return result;
        }

        private static bool IsHeavy(string key) => key == "jp_bg280g_155" || key == "jp_bg280g_155n" || key == "jp_bg280g_203n";

        [HarmonyPatch(typeof(WeaponSelector), nameof(WeaponSelector.DropdownChanged), new[] { typeof(int) })]
        private static class PhysicalSelectionPatch
        {
            private static void Prefix(WeaponSelector __instance, Aircraft ___aircraft)
            {
                if (___aircraft?.definition?.jsonKey != BomberKey) return;
                var selector = UnityEngine.Object.FindObjectsOfType<LoadoutSelector>(true)
                    .FirstOrDefault(s => (SelectedAircraft.GetValue(s) as Aircraft) == ___aircraft && Controls(s)?.Contains(__instance) == true);
                if (selector == null) return;
                var controls = Controls(selector);
                if (controls.Count < 3) return;
                int changed = controls.IndexOf(__instance);
                string chosen = __instance.GetValue()?.jsonKey;
                int clear = changed == 2 && IsHeavy(chosen) && controls[1].GetValue()?.jsonKey == "jp_bg280_gbm_magazine2" ? 1 :
                    changed == 1 && chosen == "jp_bg280_gbm_magazine2" && IsHeavy(controls[2].GetValue()?.jsonKey) ? 2 : -1;
                if (clear < 0) return;
                // Native SetValue uses SetValueWithoutNotify: the original
                // DropdownChanged then performs one consistent UpdateWeapons.
                controls[clear].SetValue(null);
                log?.LogInfo("BG-280 loadout: previous overlapping " + (clear == 1 ? "centre bomb rack" : "heavy gun") +
                    " cleared for the selected equipment; both occupy the same internal volume.");
            }
        }

        [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
        private static class PhysicalSpawnPatch
        {
            private static void Prefix(WeaponManager __instance)
            {
                var aircraft = SuperheavyNativeDamage.Owner(__instance);
                var weapons = aircraft?.loadout?.weapons;
                if (aircraft?.definition?.jsonKey != BomberKey || weapons == null || weapons.Count < 3 ||
                    !IsHeavy(weapons[2]?.jsonKey) || weapons[1]?.jsonKey != "jp_bg280_gbm_magazine2") return;
                weapons[1] = null;
                log?.LogWarning("BG-280 legacy loadout corrected before spawn: centre rack overlaps the stowed heavy gun.");
            }
        }

        internal static bool NormalizeSaved(SavedLoadout saved, WeaponManager manager)
        {
            if (!IsBomber(manager) || saved?.Selected == null) return false;
            var original = saved.Selected.Select(m => m.Key).ToList();
            var keys = NormalizeKeys(original, manager);
            if (original.SequenceEqual(keys)) return false;
            saved.Selected = keys.Select(key => new SavedLoadout.SelectedMount { Key = key }).ToList();
            return true;
        }

        private static Loadout Resolve(SavedLoadout saved, WeaponManager manager)
        {
            var resolved = new Loadout();
            for (int i = 0; i < manager.hardpointSets.Length; i++)
            {
                string key = i < saved.Selected.Count ? saved.Selected[i].Key : null;
                var mount = string.IsNullOrEmpty(key) ? null : manager.hardpointSets[i].weaponOptions
                    .FirstOrDefault(m => m != null && m.jsonKey == key);
                if (mount != null && mount.NotAllowed(MissionManager.AllowEventContent)) mount = null;
                resolved.weapons.Add(mount);
            }
            return resolved;
        }

        [HarmonyPatch(typeof(SavedLoadout), nameof(SavedLoadout.CreateLoadout), new[] { typeof(WeaponManager) })]
        private static class SavedSelectionPatch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(SavedLoadout __instance, WeaponManager __0)
            {
                EnsureAlias();
                NormalizeSaved(__instance, __0);
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(SavedLoadout __instance, WeaponManager __0, ref Loadout __result)
            {
                if (!IsBomber(__0) || __instance?.Selected == null) return;
                // QoL may have returned an index-based result before another
                // prefix ran. Resolve the actual final keys against each new slot.
                NormalizeSaved(__instance, __0);
                __result = Resolve(__instance, __0);
            }
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.LoadDefaults))]
        private static class DefaultSelectionPatch
        {
            private static void Prefix(LoadoutSelector __instance)
            {
                var aircraft = SelectedAircraft?.GetValue(__instance) as Aircraft;
                if (aircraft?.definition?.jsonKey != BomberKey ||
                    !GameManager.aircraftCustomization.TryGetValue(aircraft.definition, out var custom) || custom.loadout?.weapons == null) return;
                var saved = new SavedLoadout();
                saved.Selected = custom.loadout.weapons.Select(m => new SavedLoadout.SelectedMount { Key = m?.jsonKey }).ToList();
                if (NormalizeSaved(saved, aircraft.weaponManager)) custom.loadout = Resolve(saved, aircraft.weaponManager);
            }
        }
    }
}
