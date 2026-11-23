using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavySpatialCargoRuntime
    {
        private const int LegacyCargoCount = 3;
        private const int DefenseCount = 4;
        private const int CargoCount = SuperheavySpatialCargoPlan.Capacity;
        private const int FirstNewWeaponSet = CargoCount + DefenseCount;
        private const int BombBaySetIndex = FirstNewWeaponSet + 1;
        internal const int SetCount = FirstNewWeaponSet + 3;
        private const string AnchorPrefix = "SHGT_CargoMount_";
        private const string AryxBombKey = "aryx_glidebomb400_single_internal";
        private static readonly string[] AryxCargoKeys =
        {
            "Aryx_ADLT_01x1", "Aryx_FRCV_Artillery_x1", "Aryx_CRVL_1_Recon_x1", "Aryx_CRVL_1_Mortar_x1",
            "Aryx_MC260_AAGunContainerx1", "Aryx_MC260_R9SAMLauncherx1", "Aryx_IRSAM_Turret_x1",
            "Aryx_HeavyCEV_x1", "Aryx_MC260_MunitionsContainer_50T_x1", "Aryx_MC260_HLT-M",
            "Aryx_MC260_FireControl", "MC260_RadarContainerx1", "MC260_SAMTurret1x1",
            "Aryx_MC260_Type12_x1"
        };
        private static readonly CultureInfo StorageCulture = CultureInfo.InvariantCulture;
        private static readonly IReadOnlyList<SpatialCargoLoad> Empty = Array.Empty<SpatialCargoLoad>();
        private static readonly ConditionalWeakTable<Aircraft, Layout> AircraftLayouts =
            new ConditionalWeakTable<Aircraft, Layout>();
        private static readonly ConditionalWeakTable<Loadout, Layout> LoadoutLayouts =
            new ConditionalWeakTable<Loadout, Layout>();
        private static readonly ConditionalWeakTable<WeaponManager, AnchorSet> AnchorSets =
            new ConditionalWeakTable<WeaponManager, AnchorSet>();
        private static readonly AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> SelectorControls =
            AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");
        private static readonly AccessTools.FieldRef<LoadoutSelector, Aircraft> SelectorAircraft =
            AccessTools.FieldRefAccess<LoadoutSelector, Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<Weapon, Hardpoint> MountedHardpoint =
            AccessTools.FieldRefAccess<Weapon, Hardpoint>("hardpoint");
        private static readonly Action<LoadoutSelector, bool> UpdateWeapons =
            AccessTools.MethodDelegate<Action<LoadoutSelector, bool>>(
                AccessTools.Method(typeof(LoadoutSelector), "UpdateWeapons", new[] { typeof(bool) }));
        private static ConfigEntry<string> savedLayout;
        private static ManualLogSource log;
        private static SpawnRequest pending;

        private sealed class Layout
        {
            internal readonly IReadOnlyList<SpatialCargoLoad> Loads;

            internal Layout(IEnumerable<SpatialCargoLoad> loads)
            {
                // A mount whose lateral path intersects another mount is aft of it
                // only if its rear edge is farther aft. Load it first for ramp release.
                Loads = Array.AsReadOnly(SuperheavySpatialCargoPlan.InReleaseOrder(loads).ToArray());
            }

            internal WeaponMount MountAt(int ordinal) => ordinal < Loads.Count ? Loads[ordinal].Mount : null;
        }

        private sealed class AnchorSet
        {
            internal readonly Transform[] Anchors;
            internal readonly Vector3[] Defaults;

            internal AnchorSet(Transform[] anchors)
            {
                Anchors = anchors;
                Defaults = anchors.Select(anchor => anchor.localPosition).ToArray();
            }

            internal void Apply(Layout layout)
            {
                for (int i = 0; i < Anchors.Length; i++)
                    Anchors[i].localPosition = layout != null && i < layout.Loads.Count
                        ? SuperheavySpatialCargoPlan.GetAnchor(layout.Loads[i])
                        : Defaults[i];
            }
        }

        private sealed class SpawnRequest
        {
            internal Airbase Airbase;
            internal Player Player;
            internal WeaponMount[] Weapons;
            internal Layout Layout;
            internal float Expires;
        }

        internal static void Install(Harmony harmony, ManualLogSource logger, ConfigFile config)
        {
            log = logger;
            savedLayout = config.Bind("Cargo", "Spatial layout", "",
                "The latest committed Courier cargo layout from the Solo hangar.");
            harmony.CreateClassProcessor(typeof(SoloLoadoutCapacityPatch)).Patch();
            harmony.CreateClassProcessor(typeof(RestorePreviewPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PrepareBayForValidationPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PrepareBayForSavedLoadoutPatch)).Patch();
            harmony.CreateClassProcessor(typeof(StampLoadoutPatch)).Patch();
            harmony.CreateClassProcessor(typeof(CaptureSpawnRequestPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReceiveSpawnRequestPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ApplyAnchorsPatch)).Patch();
        }

        [HarmonyPatch]
        private static class SoloLoadoutCapacityPatch
        {
            private const string LoadoutType = "NuclearOption.SavedMission.Loadout";

            private static IEnumerable<MethodBase> TargetMethods()
            {
                Type codec = typeof(Loadout).Assembly.GetType("Mirage.GeneratedNetworkCode", true);
                yield return AccessTools.DeclaredMethod(codec, "_Write_" + LoadoutType);
                yield return AccessTools.DeclaredMethod(codec, "_Read_" + LoadoutType);
            }

            private static int Capacity() => GameManager.gameState == GameState.SinglePlayer
                ? Math.Max(SetCount, SuperheavySpatialWeaponsRuntime.ArmsSetCount) : FirstNewWeaponSet;

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                int index = code.FindIndex(instruction => instruction.opcode == OpCodes.Call &&
                    instruction.operand is MethodInfo method &&
                    method.Name.EndsWith("<WeaponMount>_WithLength", StringComparison.Ordinal));
                if (index < 1 || !code[index - 1].LoadsConstant(FirstNewWeaponSet))
                    throw new InvalidOperationException("The native loadout codec limit has changed.");

                // Solo uses the same patched codec at both ends of the local session.
                // The stock network format remains intact for remote sessions.
                code[index - 1].opcode = OpCodes.Call;
                code[index - 1].operand = AccessTools.DeclaredMethod(typeof(SoloLoadoutCapacityPatch), nameof(Capacity));
                return code;
            }
        }

        internal static IReadOnlyList<SpatialCargoLoad> GetPlan(Aircraft preview) =>
            preview != null && AircraftLayouts.TryGetValue(preview, out var layout) ? layout.Loads : Empty;

        internal static bool TryGetLoad(Aircraft aircraft, MountedCargo cargo, out SpatialCargoLoad load)
        {
            load = default;
            if (!AircraftLayouts.TryGetValue(aircraft, out var layout) || MountedHardpoint(cargo) is not Hardpoint point)
                return false;
            int index = point.HardpointIndex;
            if (index < 0 || index >= FirstNewWeaponSet ||
                (index >= LegacyCargoCount && index < LegacyCargoCount + DefenseCount))
                return false;
            int ordinal = index < LegacyCargoCount ? index : index - DefenseCount;
            if (ordinal >= layout.Loads.Count)
                return false;
            load = layout.Loads[ordinal];
            return true;
        }

        internal static bool TryApply(LoadoutSelector selector, Aircraft preview,
            IReadOnlyList<SpatialCargoLoad> loads, out string reason)
        {
            if (!SuperheavySpatialCargoPlan.TryValidate(loads, out reason))
                return false;
            return ApplyToPreview(selector, preview, new Layout(loads), true, out reason);
        }

        private static bool ApplyToPreview(LoadoutSelector selector, Aircraft preview, Layout layout,
            bool save, out string reason)
        {
            if (selector == null || !IsCourier(preview) || SelectorAircraft(selector) != preview ||
                !TryGetAnchors(preview.weaponManager, out var anchors) ||
                SelectorControls(selector).Count != SetCount)
            {
                reason = "This Courier does not have the spatial cargo mounts.";
                return false;
            }

            var controls = SelectorControls(selector);
            var previous = new WeaponMount[CargoCount];
            for (int i = 0; i < CargoCount; i++)
                previous[i] = controls[SetIndex(i)].GetValue();

            for (int i = 0; i < CargoCount; i++)
            {
                WeaponMount mount = layout.MountAt(i);
                var control = controls[SetIndex(i)];
                control.SetValue(mount);
                if (control.GetValue() == mount)
                    continue;

                for (int j = 0; j <= i; j++)
                    controls[SetIndex(j)].SetValue(previous[j]);
                reason = $"{mount?.mountName ?? "Cargo"} is unavailable from this airbase.";
                return false;
            }

            AircraftLayouts.TryGetValue(preview, out var oldLayout);
            Bind(preview, layout);
            try
            {
                UpdateWeapons(selector, true);
                if (save)
                {
                    selector.SaveDefaults();
                    savedLayout.Value = Encode(layout);
                }
            }
            catch (Exception error)
            {
                Bind(preview, oldLayout);
                anchors.Apply(oldLayout);
                for (int i = 0; i < CargoCount; i++)
                    controls[SetIndex(i)].SetValue(previous[i]);
                UpdateWeapons(selector, true);
                log.LogError(error);
                reason = "The game could not apply this cargo layout.";
                return false;
            }

            reason = null;
            return true;
        }

        private static int SetIndex(int ordinal) => ordinal < LegacyCargoCount ? ordinal : ordinal + DefenseCount;

        private static bool IsFrontOnly(WeaponMount mount)
        {
            if (mount == null)
                return false;
            switch (SuperheavySpatialCargoPlan.SourceCargoKey(mount.jsonKey))
            {
                case "jp_superheavy_cargo_hlt-m":
                case "jp_superheavy_cargo_hlt-ft":
                case "jp_superheavy_cargo_hlt-mart":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsCourier(Aircraft aircraft) =>
            GameManager.gameState == GameState.SinglePlayer && aircraft != null && IsCourier(aircraft.definition);

        private static bool IsCourier(AircraftDefinition definition) =>
            definition != null && definition.jsonKey == SuperheavyGroundPhysicsPlugin.AircraftKey;

        private static void AddAryxCargoOptions(WeaponManager manager, IDictionary<string, WeaponMount> mounts)
        {
            if (mounts == null || !TryGetAnchors(manager, out _))
                return;

            foreach (string key in AryxCargoKeys)
            {
                if (!mounts.TryGetValue(key, out var mount) ||
                    !SuperheavySpatialCargoPlan.TryMeasure(mount, out var item))
                    continue;

                var centered = new SpatialCargoLoad(mount, item, SuperheavySpatialCargoPlan.CenterlineX(mount),
                    SuperheavyCargoPacking.EndClearance);
                if (!SuperheavySpatialCargoPlan.TryValidate(new[] { centered }, out _))
                    continue;

                // Use the registered mount so Solo loadout serialization, the
                // native vehicle definition and the addon's scripts share its ID.
                for (int i = 0; i < CargoCount; i++)
                {
                    var options = manager.hardpointSets[SetIndex(i)].weaponOptions;
                    if (options != null && !options.Contains(mount))
                        options.Add(mount);
                }
            }
        }

        private static void AddAryxBombOption(WeaponManager manager)
        {
            if (manager == null || manager.hardpointSets?.Length != SetCount ||
                manager.hardpointSets[BombBaySetIndex] is not HardpointSet bay ||
                bay.hardpoints == null || bay.hardpoints.Count < 2 || bay.hardpoints[0]?.transform == null ||
                bay.hardpoints[0].transform.name != "SHGT_BombBayRail_1" ||
                bay.weaponOptions == null || Encyclopedia.WeaponLookup == null ||
                !Encyclopedia.WeaponLookup.TryGetValue(AryxBombKey, out var mount) || mount == null ||
                mount.prefab == null || mount.info == null || !mount.info.useWeaponDoors || mount.ammo != 1 ||
                bay.weaponOptions.Contains(mount))
                return;

            SuperheavyMountedStores.RestoreOptionalBomb(mount, log);
            bay.weaponOptions.Add(mount);
        }

        private static void PrepareOptionalStores(WeaponManager manager)
        {
            if (!TryGetAnchors(manager, out _))
                return;
            AddAryxBombOption(manager);
            SuperheavyQolStores.Populate(manager, BombBaySetIndex, SetIndex, CargoCount, log);
            SuperheavyCargoIcons.Populate(manager);
        }

        private static void Bind(Aircraft aircraft, Layout layout)
        {
            AircraftLayouts.Remove(aircraft);
            if (layout != null)
                AircraftLayouts.Add(aircraft, layout);
        }

        private static bool TryGetAnchors(WeaponManager manager, out AnchorSet anchors)
        {
            anchors = null;
            if (manager == null || manager.hardpointSets?.Length != SetCount)
                return false;
            if (AnchorSets.TryGetValue(manager, out anchors))
                return true;

            var transforms = new Transform[CargoCount];
            for (int i = 0; i < CargoCount; i++)
            {
                var points = manager.hardpointSets[SetIndex(i)]?.hardpoints;
                if (points == null || points.Count != 1 || points[0] == null || points[0].transform == null ||
                    points[0].transform.name != AnchorPrefix + i || points[0].HardpointIndex != SetIndex(i))
                    return false;
                transforms[i] = points[0].transform;
            }

            anchors = new AnchorSet(transforms);
            AnchorSets.Add(manager, anchors);
            return true;
        }

        private static bool Matches(Loadout loadout, Layout layout)
        {
            if (loadout?.weapons == null)
                return false;
            for (int i = 0; i < CargoCount; i++)
            {
                int index = SetIndex(i);
                var mount = index < loadout.weapons.Count ? loadout.weapons[index] : null;
                if (mount != layout.MountAt(i))
                    return false;
            }
            return true;
        }

        private static string Encode(Layout layout) => "2" + string.Concat(layout.Loads.Select(load =>
            "|" + Uri.EscapeDataString(load.Mount.jsonKey) + "," + load.X.ToString("R", StorageCulture) +
            "," + load.Y.ToString("R", StorageCulture)));

        private static bool TryRestore(Aircraft aircraft, out Layout layout)
        {
            layout = null;
            string[] data = savedLayout.Value.Split('|');
            bool legacy = data[0] == "1";
            if (!legacy && data[0] != "2")
                return false;
            if (data.Length > CargoCount + 1)
            {
                log.LogWarning($"The saved Courier layout contains {data.Length - 1} loads. " +
                    $"The Courier has {CargoCount} cargo positions. The saved layout was kept.");
                return false;
            }

            var loads = new List<SpatialCargoLoad>();
            var catalog = aircraft.weaponManager.hardpointSets[SetIndex(CargoCount - 1)].weaponOptions;
            for (int i = 1; i < data.Length; i++)
            {
                string[] fields = data[i].Split(',');
                if (fields.Length != 3 ||
                    !float.TryParse(fields[1], NumberStyles.Float, StorageCulture, out float x) ||
                    !float.TryParse(fields[2], NumberStyles.Float, StorageCulture, out float y))
                    return false;
                string key = Uri.UnescapeDataString(fields[0]);
                var mount = catalog.FirstOrDefault(candidate => candidate != null && candidate.jsonKey == key);
                // Older Courier layouts stored unmodified ground cargo. The
                // selectable catalog now prefers its Retro Kit counterpart.
                if (mount == null && !key.StartsWith("JP_RDK_", StringComparison.Ordinal))
                    mount = catalog.FirstOrDefault(candidate => candidate != null && candidate.jsonKey == "JP_RDK_" + key);
                if (mount == null || !SuperheavySpatialCargoPlan.TryMeasure(mount, out var item))
                    return false;
                var position = legacy ? SuperheavySpatialCargoPlan.MigrateLegacyPosition(mount, x, y) : new Vector2(x, y);
                loads.Add(new SpatialCargoLoad(mount, item, position.x, position.y));
            }

            if (!SuperheavySpatialCargoPlan.TryValidate(loads, out _))
                return false;
            layout = new Layout(loads);
            return true;
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.AssignAircraft))]
        private static class RestorePreviewPatch
        {
            private static void Prefix(Aircraft __0)
            {
                if (IsCourier(__0))
                {
                    AddAryxCargoOptions(__0.weaponManager, Encyclopedia.WeaponLookup);
                    PrepareOptionalStores(__0.weaponManager);
                }
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix(LoadoutSelector __instance, Aircraft __0)
            {
                if (__0 == null || !IsCourier(__0.definition) || !TryGetAnchors(__0.weaponManager, out _))
                    return;

                if (GameManager.gameState == GameState.SinglePlayer)
                {
                    if (TryRestore(__0, out var layout) &&
                        !ApplyToPreview(__instance, __0, layout, false, out string reason))
                        log.LogWarning($"Could not restore the saved Courier cargo layout: {reason}");
                    return;
                }

                var controls = SelectorControls(__instance);
                if (controls.Count != SetCount)
                    return;
                bool changed = false;
                for (int i = LegacyCargoCount; i < CargoCount; i++)
                {
                    var control = controls[SetIndex(i)];
                    control.gameObject.SetActive(false);
                    if (control.GetValue() == null)
                        continue;
                    control.SetValue(null);
                    changed = true;
                }
                if (changed)
                    UpdateWeapons(__instance, true);
            }
        }

        [HarmonyPatch(typeof(WeaponChecker), nameof(WeaponChecker.VetLoadout))]
        private static class PrepareBayForValidationPatch
        {
            private static void Prefix(AircraftDefinition __0)
            {
                if (GameManager.gameState == GameState.SinglePlayer && IsCourier(__0) &&
                    __0.unitPrefab != null && __0.unitPrefab.TryGetComponent<Aircraft>(out var aircraft))
                    PrepareOptionalStores(aircraft.weaponManager);
            }
        }

        [HarmonyPatch(typeof(SavedLoadout), nameof(SavedLoadout.CreateLoadout), new[] { typeof(WeaponManager) })]
        private static class PrepareBayForSavedLoadoutPatch
        {
            private static void Prefix(WeaponManager __0)
            {
                if (GameManager.gameState == GameState.SinglePlayer || GameManager.gameState == GameState.Editor)
                    PrepareOptionalStores(__0);
            }
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.GenerateLoadoutFromDropdowns))]
        private static class StampLoadoutPatch
        {
            private static void Postfix(Aircraft ___aircraft, Loadout __result)
            {
                if (IsCourier(___aircraft) && AircraftLayouts.TryGetValue(___aircraft, out var layout) &&
                    Matches(__result, layout))
                    LoadoutLayouts.GetValue(__result, _ => layout);
            }
        }

        [HarmonyPatch(typeof(Spawner), nameof(Spawner.RequestSpawnAtAirbase))]
        private static class CaptureSpawnRequestPatch
        {
            private static void Prefix(Airbase __0, AircraftDefinition __1, Loadout __3)
            {
                if (GameManager.gameState != GameState.SinglePlayer || !IsCourier(__1) || __3 == null ||
                    !LoadoutLayouts.TryGetValue(__3, out var layout) ||
                    !GameManager.GetLocalPlayer(out Player player))
                    return;

                pending = new SpawnRequest
                {
                    Airbase = __0,
                    Player = player,
                    Weapons = __3.weapons.ToArray(),
                    Layout = layout,
                    Expires = Time.realtimeSinceStartup + 30f
                };
            }
        }

        [HarmonyPatch(typeof(Spawner), "TrySpawnAircraft",
            new[] { typeof(Airbase), typeof(AircraftDefinition), typeof(LiveryKey), typeof(Loadout),
                typeof(float), typeof(INetworkPlayer) })]
        private static class ReceiveSpawnRequestPatch
        {
            private static void Prefix(Airbase __0, AircraftDefinition __1, Loadout __3, INetworkPlayer __5)
            {
                var request = pending;
                if (request == null || GameManager.gameState != GameState.SinglePlayer || !IsCourier(__1) ||
                    request.Airbase != __0 || Time.realtimeSinceStartup > request.Expires ||
                    !PlayerHelper.TryGetPlayer(__5, out Player player) || request.Player != player ||
                    __3?.weapons == null || !request.Weapons.SequenceEqual(__3.weapons))
                    return;

                // Mirage may deserialize a different Loadout object. The server-side
                // object is preserved through the native hangar door queue and spawn.
                LoadoutLayouts.GetValue(__3, _ => request.Layout);
                pending = null;
            }
        }

        [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.SpawnWeapons))]
        private static class ApplyAnchorsPatch
        {
            private static void Prefix(WeaponManager __instance, Aircraft ___aircraft)
            {
                if (___aircraft == null || !IsCourier(___aircraft.definition) || !TryGetAnchors(__instance, out var anchors))
                    return;

                Loadout nativeLoadout = ___aircraft.loadout;
                Layout layout = null;
                if (GameManager.gameState == GameState.SinglePlayer)
                {
                    if (!AircraftLayouts.TryGetValue(___aircraft, out layout) && nativeLoadout != null &&
                        LoadoutLayouts.TryGetValue(nativeLoadout, out layout) && Matches(nativeLoadout, layout))
                        Bind(___aircraft, layout);
                    if (layout != null && !Matches(nativeLoadout, layout))
                    {
                        Bind(___aircraft, null);
                        layout = null;
                    }
                }
                anchors.Apply(layout);
                if (nativeLoadout?.weapons != null)
                {
                    for (int i = 0; i < nativeLoadout.weapons.Count; i++)
                    {
                        var mount = nativeLoadout.weapons[i];
                        if (mount == null || !mount.Cargo)
                            continue;
                        string sourceKey = SuperheavySpatialCargoPlan.SourceCargoKey(mount.jsonKey);
                        bool legacyBay = i < LegacyCargoCount && !(i < 2 && IsFrontOnly(mount)) &&
                            !AryxCargoKeys.Contains(sourceKey) && !SuperheavyQolStores.IsOptionalCargo(sourceKey);
                        if (i < FirstNewWeaponSet && (layout != null || legacyBay))
                            continue;

                        // Reject cargo from retired virtual slots in older saves.
                        nativeLoadout.weapons[i] = null;
                        log.LogWarning("Skipped a Courier spatial cargo mount without a matching layout.");
                    }
                }
            }
        }
    }
}
