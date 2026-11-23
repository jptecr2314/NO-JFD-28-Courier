using System;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyMountedStores
    {
        private const string AryxBombKey = "aryx_glidebomb400_single_internal";
        private const string AryxModel = "assets/blueprinter/mods/aryx_expanded_weapons/aryx_weapons3.fbx";
        private static readonly string[] MeshNames =
        {
            "Aryx_GlideBomb_400", "Aryx_GlideBomb_400_Wing_R", "Aryx_GlideBomb_400_Wing_L"
        };
        private static readonly int[] VertexCounts = { 452, 34, 34 };
        private static Mesh[] meshes;
        private static bool reported;

        internal static void RestoreOptionalBomb(WeaponMount mount, ManualLogSource log)
        {
            if (mount == null || mount.jsonKey != AryxBombKey || mount.prefab == null || mount.info == null)
                return;

            // Aryx 1.1.1's registered mounted bomb and projectile lose three
            // mesh references during loading. Reuse the exact source subassets
            // already installed by that addon; do not substitute another bomb
            // or ship its models with Courier.
            int restored = 0;
            foreach (var root in new[] { mount.prefab, mount.info.weaponPrefab }.Where(root => root != null).Distinct())
            {
                var filters = root.GetComponentsInChildren<MeshFilter>(true);
                foreach (var filter in filters.Where(filter => filter.sharedMesh == null))
                {
                    int index = Array.IndexOf(MeshNames, filter.name == "Aryx_GlideBomb400"
                        ? "Aryx_GlideBomb_400" : filter.name);
                    if (index < 0 || !TryLoadMeshes())
                        continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null || renderer.sharedMaterials.Length < meshes[index].subMeshCount ||
                        renderer.sharedMaterials.Any(material => material == null))
                        continue;
                    filter.sharedMesh = meshes[index];
                    restored++;
                }
            }
            if (restored > 0)
            {
                log.LogInfo($"Courier restored {restored} original PGM-400LR mounted/projectile mesh references from the installed Aryx model.");
                reported = false;
            }
            else if (!reported && mount.prefab.GetComponentsInChildren<MeshFilter>(true).Any(filter => filter.sharedMesh == null))
            {
                reported = true;
                log.LogWarning("Courier could not restore PGM-400LR visuals: the exact installed Aryx model or matching materials are unavailable.");
            }
        }

        private static bool TryLoadMeshes()
        {
            if (meshes != null && meshes.All(mesh => mesh != null))
                return true;
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (!bundle.Contains(AryxModel))
                    continue;
                var candidates = bundle.LoadAssetWithSubAssets<Mesh>(AryxModel);
                var found = MeshNames.Select((name, index) =>
                {
                    var matches = candidates.Where(mesh => mesh != null && mesh.name == name &&
                        mesh.vertexCount == VertexCounts[index]).ToArray();
                    return matches.Length == 1 ? matches[0] : null;
                }).ToArray();
                if (found.All(mesh => mesh != null))
                {
                    meshes = found;
                    return true;
                }
            }
            return false;
        }
    }
}
