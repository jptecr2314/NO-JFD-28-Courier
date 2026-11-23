using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyQolStores
    {
        private const string RestorationId = "br.japensou.nuclearoption.groundscoped";
        internal const string CargoPrefix = "jp_superheavy_qol_cargo_";
        private static readonly string[] Vehicles =
        {
            "UGV1_AT_P", "Linebreaker_SPG_P", "Linebreaker_EW_P", "AFV8_MLRS_P",
            "AFV8_SPG_P", "AFV8_EW_P", "AFV8_MAD_P", "SPAAG2_CFV_P", "P_LRAA1"
        };
        private static readonly string[] WingStores =
        {
            "P_KEM1_single", "ARM1_mini_single", "ARM1_cluster_single", "JammingPod1_mini_P",
            "P_AAM2_double_pod", "P_AGM_heavy_single_pod", "P_AAM4_double_pod",
            "P_AAM1_double_pod", "P_bomb_250_double_pod", "P_bomb_glide1_double_pod"
        };
        private static readonly AccessTools.FieldRef<MountedMissile, float> RailLength =
            AccessTools.FieldRefAccess<MountedMissile, float>("railLength");
        private static readonly AccessTools.FieldRef<MountedMissile, float> RailSpeed =
            AccessTools.FieldRefAccess<MountedMissile, float>("railSpeed");
        private static readonly AccessTools.FieldRef<MountedMissile, float> RailDelay =
            AccessTools.FieldRefAccess<MountedMissile, float>("railDelay");
        private static readonly System.Reflection.FieldInfo RailDirection =
            AccessTools.Field(typeof(MountedMissile), "railDirection");
        private static Transform templates;
        private static readonly HashSet<string> Reported = new HashSet<string>();

        internal static bool IsOptionalCargo(string key) =>
            key == "UGV1_AT_Px1" || key?.StartsWith(CargoPrefix, StringComparison.Ordinal) == true;

        internal static void Populate(WeaponManager manager, int bayIndex, Func<int, int> cargoIndex,
            int cargoCount, ManualLogSource log)
        {
            if (!Chainloader.PluginInfos.TryGetValue(RestorationId, out var plugin) ||
                plugin.Metadata.Version < new Version(0, 5, 1) || Encyclopedia.WeaponLookup == null ||
                Encyclopedia.Lookup == null || Encyclopedia.i?.IndexLookup == null)
                return;

            var sets = manager.hardpointSets;
            var deck = sets[cargoIndex(0)].weaponOptions;
            var donor = deck.FirstOrDefault(m => m != null && m.Cargo && m.prefab != null &&
                m.jsonKey == "jp_superheavy_cargo_ugv1_sam");
            foreach (string key in Vehicles)
            {
                if (!Encyclopedia.Lookup.TryGetValue(key, out var unit) || unit is not VehicleDefinition vehicle ||
                    !Indexed(vehicle) || vehicle.unitPrefab == null ||
                    vehicle.unitPrefab.GetComponent<BoxCollider>() == null ||
                    vehicle.unitPrefab.GetComponent<Rigidbody>() == null)
                    continue;

                string cargoKey = CargoPrefix + key.ToLowerInvariant();
                if (!Encyclopedia.WeaponLookup.TryGetValue(cargoKey, out var mount) &&
                    !(key == "UGV1_AT_P" && TryMount("UGV1_AT_Px1", out mount) && FitsCargo(mount, vehicle)))
                {
                    mount = donor == null ? null : BuildCargo(donor, vehicle, cargoKey);
                    if (mount != null)
                    {
                        var index = Encyclopedia.i.IndexLookup;
                        ((INetworkDefinition)mount).LookupIndex = index.Count;
                        index.Add(mount);
                        Encyclopedia.i.weaponMounts.Add(mount);
                        Encyclopedia.WeaponLookup.Add(cargoKey, mount);
                    }
                }
                if (mount == null || !FitsCargo(mount, vehicle))
                {
                    Report(log, key, false);
                    continue;
                }
                for (int i = 0; i < cargoCount; i++)
                    Add(sets[cargoIndex(i)], mount);
                Report(log, key, true);
            }

            var bay = sets[bayIndex];
            if (TryMount("P_GLB1_internal", out var bomb))
            {
                bool fits = FitsBay(bomb);
                if (fits) Add(bay, bomb);
                Report(log, bomb.jsonKey, fits);
            }

            var wing = sets[bayIndex + 1];
            if (!TryMount("AShM2_single", out var stock) || !TryBounds(stock.prefab, out var envelope))
                return;
            envelope.Encapsulate(new Vector3(.36f, .17f, 2.3f));
            envelope.Encapsulate(new Vector3(-.36f, -.85f, -2.3f));
            foreach (string key in WingStores)
            {
                if (!TryMount(key, out var mount))
                    continue;
                bool fits = !mount.Cargo && !mount.missileBay && TryBounds(mount.prefab, out var bounds) &&
                    envelope.Contains(bounds.min) && envelope.Contains(bounds.max);
                if (fits) Add(wing, mount);
                Report(log, key, fits);
            }
        }

        private static bool Indexed(INetworkDefinition item)
        {
            var index = Encyclopedia.i.IndexLookup;
            return item.LookupIndex is int slot && slot >= 0 && slot < index.Count &&
                ReferenceEquals(index[slot], item);
        }

        private static bool TryMount(string key, out WeaponMount mount) =>
            Encyclopedia.WeaponLookup.TryGetValue(key, out mount) && mount != null &&
            mount.prefab != null && mount.info != null && Indexed(mount);

        private static bool FitsCargo(WeaponMount mount, VehicleDefinition vehicle)
        {
            var cargo = mount.prefab == null ? null : mount.prefab.GetComponentInChildren<MountedCargo>(true);
            return cargo != null && cargo.cargo == vehicle &&
                SuperheavySpatialCargoPlan.TryMeasure(mount, out var item) &&
                SuperheavySpatialCargoPlan.TryFindSpot(Array.Empty<SpatialCargoLoad>(), mount, item, out _);
        }

        private static WeaponMount BuildCargo(WeaponMount donor, VehicleDefinition vehicle, string key)
        {
            if (templates == null)
            {
                var root = new GameObject("Courier QoL cargo templates");
                root.SetActive(false);
                Object.DontDestroyOnLoad(root);
                templates = root.transform;
            }
            var stowed = new GameObject(key);
            stowed.transform.SetParent(templates, false);
            var prefab = vehicle.unitPrefab.transform;
            foreach (var renderer in PrimaryRenderers(vehicle.unitPrefab))
            {
                var part = new GameObject(renderer.name);
                part.transform.SetParent(stowed.transform, false);
                part.transform.localPosition = prefab.InverseTransformPoint(renderer.transform.position);
                part.transform.localRotation = Quaternion.Inverse(prefab.rotation) * renderer.transform.rotation;
                var scale = renderer.transform.lossyScale;
                var parentScale = prefab.lossyScale;
                part.transform.localScale = new Vector3(scale.x / parentScale.x, scale.y / parentScale.y, scale.z / parentScale.z);
                part.AddComponent<MeshFilter>().sharedMesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            }
            if (!TryBounds(stowed, out var bounds))
            {
                Object.Destroy(stowed);
                return null;
            }
            stowed.transform.localPosition = new Vector3(-bounds.center.x, .03f - bounds.min.y, -bounds.center.z);
            var info = Object.Instantiate(donor.info);
            info.weaponName = vehicle.unitName;
            info.description = vehicle.description;
            info.weaponIcon = vehicle.friendlyIcon;
            info.massPerRound = vehicle.mass;
            info.costPerRound = vehicle.value;
            var cargo = stowed.AddComponent<MountedCargo>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(donor.prefab.GetComponentInChildren<MountedCargo>(true)), cargo);
            cargo.cargo = vehicle;
            cargo.info = info;
            var mount = Object.Instantiate(donor);
            mount.name = key;
            mount.jsonKey = key;
            mount.mountName = vehicle.unitName;
            mount.prefab = stowed;
            mount.info = info;
            mount.mass = vehicle.mass;
            if (FitsCargo(mount, vehicle))
                return mount;
            Object.Destroy(mount);
            Object.Destroy(info);
            Object.Destroy(stowed);
            return null;
        }

        private static bool FitsBay(WeaponMount mount)
        {
            var rails = mount.prefab.GetComponentsInChildren<MountedMissile>(true);
            if (mount.Cargo || mount.ammo != 1 || !mount.info.useWeaponDoors || !mount.GroundSafety || !mount.GearSafety ||
                rails.Length != 1 || rails[0].info != mount.info ||
                RailDirection == null || Convert.ToInt32(RailDirection.GetValue(rails[0])) != 1 ||
                !TryBounds(mount.prefab, out var bounds))
                return false;
            float speed = RailSpeed(rails[0]);
            float drop = RailLength(rails[0]);
            float release = RailDelay(rails[0]) + drop / speed;
            return speed > 0 && drop >= .5f && release >= .25f && release < 1.5f &&
                bounds.min.x >= -.29f && bounds.max.x <= .29f && bounds.max.y <= .16f &&
                bounds.min.z >= -4.8f && bounds.max.z <= 4.8f;
        }

        private static IEnumerable<MeshRenderer> PrimaryRenderers(GameObject root)
        {
            var lower = new HashSet<Renderer>();
            var primary = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                if (lods.Length == 0) continue;
                primary.UnionWith(lods[0].renderers);
                foreach (var lod in lods.Skip(1)) lower.UnionWith(lod.renderers);
            }
            lower.ExceptWith(primary);
            return root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled &&
                !lower.Contains(r) && IsVisibleInTemplate(r.transform, root.transform) &&
                r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null);
        }

        internal static bool IsVisibleInTemplate(Transform part, Transform root)
        {
            for (; part != root; part = part.parent)
                if (!part.gameObject.activeSelf) return false;
            return true;
        }

        private static bool TryBounds(GameObject root, out Bounds result)
        {
            result = default;
            bool any = false;
            var local = Matrix4x4.TRS(root.transform.localPosition, root.transform.localRotation, root.transform.localScale)
                * root.transform.worldToLocalMatrix;
            foreach (var renderer in PrimaryRenderers(root))
            {
                var bounds = renderer.GetComponent<MeshFilter>().sharedMesh.bounds;
                var matrix = local * renderer.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = matrix.MultiplyPoint3x4(new Vector3(
                        (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                        (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
                    if (any) result.Encapsulate(point);
                    else { result = new Bounds(point, Vector3.zero); any = true; }
                }
            }
            return any;
        }

        private static void Add(HardpointSet set, WeaponMount mount)
        {
            if (!set.weaponOptions.Contains(mount))
                set.weaponOptions.Add(mount);
        }

        private static void Report(ManualLogSource log, string key, bool fitted)
        {
            if (Reported.Add(key))
                log.LogInfo($"Courier QoL {key}: {(fitted ? "available" : "excluded by mount or clearance checks")}.");
        }
    }
}
