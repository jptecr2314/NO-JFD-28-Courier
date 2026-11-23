using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundDefense : MonoBehaviour
    {
        private const string MountKey = "jp_superheavy_ciws35";
        private const string GunKey = "SuperheavyCIWS35_Info";
        private const string FlankMountKey = "jp_superheavy_ciws20";
        private const string FlankGunKey = "SuperheavyCIWS20_Info";
        private const float NearbyMissileRange = 1500f;
        private const string SkySentryKey = "MC260_AAGunContainer_35";
        private const string TurretPath = "Aryx_MC260_35mmGunContainer_Platform/Aryx_MC260_35mmGunContainer_Turret";
        private const string BarrelName = "Aryx_MC260_35mmGunContainer_Gun";
        private const string ArmorShaderName = "Superheavy/Turret Armor";
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        private static readonly int PaintMode = Shader.PropertyToID("_PaintMode");
        private static readonly int PaintChart = Shader.PropertyToID("_PaintChart");
        private static readonly int PaintOrigin = Shader.PropertyToID("_PaintOrigin");
        private static ManualLogSource log;
        private static bool fallbackReported;
        [ThreadStatic] private static Gun automaticGun;

        private enum DefensePriority
        {
            None,
            TrackOnly,
            EnemyAircraft,
            NearbyMissile,
            IncomingMissile
        }

        private readonly List<(MeshRenderer Renderer, int Slot, Vector3 Origin, Material Original)> painted =
            new List<(MeshRenderer Renderer, int Slot, Vector3 Origin, Material Original)>(2);
        private Aircraft aircraft;
        private Material armorMaterial;
        private LiveryData currentLivery;
        private int currentChannel;
        private Vector3 station;
        private float outwardSign;
        private bool flank;
        private bool usesNativeSkin;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(MountAmmunitionPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ReserveRearmPatch)).Patch();
            harmony.CreateClassProcessor(typeof(MountVisualPatch)).Patch();
            harmony.CreateClassProcessor(typeof(TargetPriorityPatch)).Patch();
            harmony.CreateClassProcessor(typeof(TurretTargetPatch)).Patch();
            harmony.CreateClassProcessor(typeof(TurretFireScopePatch)).Patch();
            harmony.CreateClassProcessor(typeof(AutomaticFireRangePatch)).Patch();
            harmony.CreateClassProcessor(typeof(AutomaticGunPatch)).Patch();
        }

        [HarmonyPatch(typeof(WeaponMount), nameof(WeaponMount.Initialize))]
        private static class MountAmmunitionPatch
        {
            private static readonly AccessTools.FieldRef<Gun, int> Capacity =
                AccessTools.FieldRefAccess<Gun, int>("magazineCapacity");
            private static readonly AccessTools.FieldRef<Gun, int> Reserves =
                AccessTools.FieldRefAccess<Gun, int>("magazines");

            private static void Postfix(WeaponMount __instance)
            {
                if ((__instance.jsonKey != MountKey && __instance.jsonKey != FlankMountKey) ||
                    __instance.prefab == null || __instance.info == null)
                    return;
                var gun = __instance.prefab.GetComponentInChildren<Gun>(true);
                if (gun == null)
                    return;

                // Prefab assets have not run Gun.Awake, which initializes the
                // nonserialized reserve limit used by native GetFullAmmo.
                __instance.ammo = Capacity(gun) * (1 + Reserves(gun));
                __instance.mass = __instance.emptyMass + __instance.info.massPerRound * __instance.ammo;
                __instance.mountName = $"{__instance.info.weaponName} ({__instance.ammo} rounds)";
            }
        }

        [HarmonyPatch(typeof(Gun), nameof(Gun.Rearm))]
        private static class ReserveRearmPatch
        {
            private static bool Applies(Gun gun) => gun.info != null &&
                (gun.info.name == GunKey || gun.info.name == FlankGunKey);

            private static void Prefix(Gun __instance, ref int __0, int ___magazineCapacity, int ___maxMagazines)
            {
                if (Applies(__instance))
                    __0 = Mathf.Clamp(__0, 0, ___magazineCapacity * (1 + ___maxMagazines) - __instance.ammo);
            }

            private static void Postfix(Gun __instance, int ___magazineCapacity, ref int ___bulletsLoaded,
                ref int ___magazines, ref float ___timeUntilReload)
            {
                if (!Applies(__instance))
                    return;

                // Native Rearm counts the loaded belt again as a reserve.
                // Represent a partial feed in the ready box and keep reserves
                // full, preserving every supplied round in the native total.
                int total = __instance.ammo;
                ___bulletsLoaded = total == 0 ? 0 : 1 + (total - 1) % ___magazineCapacity;
                ___magazines = (total - ___bulletsLoaded) / ___magazineCapacity;
                ___timeUntilReload = 0f;
                __instance.ReportReloading(false);
            }
        }

        private static Gun GetDefenseGun(Turret turret)
        {
            if (turret.GetAttachedUnit() is Aircraft owner && owner.definition != null &&
                SuperheavyGroundPhysicsPlugin.IsFamily(owner.definition) &&
                turret.GetWeapon() is Gun gun && gun.info != null &&
                (gun.info.name == GunKey || gun.info.name == FlankGunKey))
                return gun;
            return null;
        }

        private static DefensePriority TargetTier(Unit owner, Vector3 mountPosition, float weaponRange, Unit candidate)
        {
            if (candidate == null || candidate.disabled || candidate.NetworkHQ == null ||
                candidate.NetworkHQ == owner.NetworkHQ)
                return DefensePriority.None;

            float distanceSquared = (candidate.transform.position - mountPosition).sqrMagnitude;
            if (distanceSquared >= weaponRange * weaponRange)
                return DefensePriority.None;
            if (candidate is Missile missile)
            {
                if (missile.targetID == owner.persistentID)
                    return DefensePriority.IncomingMissile;
                return (missile.transform.position - owner.transform.position).sqrMagnitude <=
                    NearbyMissileRange * NearbyMissileRange ? DefensePriority.NearbyMissile : DefensePriority.TrackOnly;
            }
            return candidate is Aircraft ? DefensePriority.EnemyAircraft : DefensePriority.None;
        }

        private static Gun GetCoordinatedGun(Turret turret)
        {
            var defense=GetDefenseGun(turret);
            if(defense!=null)return defense;
            if(!(turret.GetAttachedUnit() is Aircraft owner)||owner.definition?.jsonKey!="jp_bg280_bomber")return null;
            var gun=turret.GetWeapon() as Gun;
            return gun?.info!=null&&!gun.info.nuclear&&gun.info.name.StartsWith("BG280G_",StringComparison.Ordinal)?gun:null;
        }
        private static DefensePriority CoordinationTier(Gun gun,Unit owner,Vector3 position,float range,Unit target)
        {
            if(gun.info.name==GunKey||gun.info.name==FlankGunKey)return TargetTier(owner,position,range,target);
            if(target==null||target.disabled||target.NetworkHQ==null||target.NetworkHQ==owner.NetworkHQ||
                (target.transform.position-position).sqrMagnitude>=range*range)return DefensePriority.None;
            // Preserve the native gunship target requirements and scoring,
            // including ground/naval targets. Only coverage changes priority.
            return DefensePriority.EnemyAircraft;
        }

        [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
        private static class TargetPriorityPatch
        {
            private static readonly AccessTools.FieldRef<Turret, bool> Manual =
                AccessTools.FieldRefAccess<Turret, bool>("manual");
            private static readonly AccessTools.FieldRef<Turret, bool> Stowed =
                AccessTools.FieldRefAccess<Turret, bool>("stowed");
            private static readonly AccessTools.FieldRef<Turret, bool> Disabled =
                AccessTools.FieldRefAccess<Turret, bool>("disabled");
            private static readonly AccessTools.FieldRef<Turret, FiringCone[]> FiringCones =
                AccessTools.FieldRefAccess<Turret, FiringCone[]>("firingCones");

            private static bool Prefix(Turret __instance, Unit __0, ref float __1, Unit ___target,
                bool ___manual, out float __state)
            {
                __state = 0f;
                Gun gun = GetCoordinatedGun(__instance);
                if (___manual || gun == null)
                    return true;
                if (gun.GetAmmoTotal() <= 0)
                    return false;

                Unit owner = __instance.GetAttachedUnit();
                Vector3 position = __instance.transform.position;
                WeaponStation station = __instance.GetWeaponStation();
                float range = station.WeaponInfo.targetRequirements.maxRange;
                DefensePriority candidateTier = CoordinationTier(gun,owner,position,range,__0);
                if (candidateTier == DefensePriority.None)
                    return false;

                DefensePriority chosenTier = CoordinationTier(gun,owner,position,range,___target);
                int candidateRank = AssignmentRank(__instance, owner, __0, candidateTier);
                int chosenRank = AssignmentRank(__instance, owner, ___target, chosenTier);
                if (candidateRank < chosenRank)
                    return false;

                // Let native scoring and feasibility choose between equally ranked targets.
                if (candidateRank > chosenRank)
                {
                    __state = __1;
                    __1 = 0f;
                }
                return true;
            }

            private static int AssignmentRank(Turret turret, Unit owner, Unit target, DefensePriority tier)
            {
                if (tier <= DefensePriority.TrackOnly)
                    return (int)tier;

                foreach (WeaponStation station in owner.weaponStations)
                foreach (Turret other in station.Turrets)
                {
                    if (other == null || other == turret || other.GetTarget() != target ||
                        !other.gameObject.activeInHierarchy || Manual(other) || Stowed(other) || Disabled(other))
                        continue;

                    Gun otherGun = GetCoordinatedGun(other);
                    if (otherGun == null || otherGun.GetAmmoTotal() <= 0)
                        continue;
                    if(otherGun.info.name.StartsWith("BG280G_",StringComparison.Ordinal)&&!SuperheavyRetractableBattery.CanFire(otherGun))
                        continue;

                    Vector3 toTarget = target.transform.position - otherGun.transform.position;
                    float distance = toTarget.magnitude;
                    if (distance >= otherGun.info.targetRequirements.maxRange ||
                        !FiringConeChecker.VectorWithinFiringCones(FiringCones(other), toTarget, out _))
                        continue;

                    // Count a partner only while it has a clear local path, like the native firing check.
                    float clearance = Mathf.Min(200f, distance - target.maxRadius - 50f);
                    if (clearance <= 0f || !Physics.Raycast(otherGun.transform.position, toTarget / distance,
                        clearance, ~PhysicsLayers.ExclusionZonesMask))
                        return (int)tier;
                }

                // Cover a fresh threat first, with missiles first in each group. A lone threat can get backup.
                return (int)tier + (int)DefensePriority.IncomingMissile;
            }

            private static void Postfix(ref float __1, float __state)
            {
                if (__1 == 0f)
                    __1 = __state;
            }
        }

        [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.Fire))]
        private static class AutomaticFireRangePatch
        {
            private static bool Prefix(WeaponStation __instance, Unit __0, Unit __1)
            {
                if (automaticGun == null)
                    return true;

                // Pre-align for tracked missiles while they close, without spending rounds yet.
                return TargetTier(__0, automaticGun.transform.position,
                    __instance.WeaponInfo.targetRequirements.maxRange, __1) > DefensePriority.TrackOnly;
            }
        }

        [HarmonyPatch(typeof(Turret), nameof(Turret.SetTarget))]
        private static class TurretTargetPatch
        {
            private static readonly AccessTools.FieldRef<AimSolver, Unit> BoundTarget =
                AccessTools.FieldRefAccess<AimSolver, Unit>("currentTarget");

            private static void Prefix(AimSolver ___aimSolver, out Unit __state)
            {
                __state = ___aimSolver == null ? null : BoundTarget(___aimSolver);
            }

            private static void Postfix(Turret __instance, AimSolver ___aimSolver, bool ___manual,
                ref float ___timeOnTarget, Unit __state)
            {
                Gun gun = GetDefenseGun(__instance);
                if (gun == null)
                    return;

                Unit target = __instance.GetTarget();
                // The grouped native station otherwise uses its first gun as every turret's aim origin.
                ___aimSolver.SetTarget(__instance.GetAttachedUnit(), target, gun.transform, gun.info);
                gun.SetTarget(target);
                if (!___manual && target != __state)
                {
                    ___timeOnTarget = 0f;
                    gun.Safety = true;
                }
            }
        }

        [HarmonyPatch(typeof(Turret), "FixedUpdate")]
        private static class TurretFireScopePatch
        {
            private static void Prefix(Turret __instance, bool ___manual, out Gun __state)
            {
                __state = automaticGun;
                automaticGun = ___manual ? null : GetDefenseGun(__instance);
            }

            private static void Finalizer(Gun __state)
            {
                automaticGun = __state;
            }
        }

        [HarmonyPatch(typeof(Gun), nameof(Gun.Fire))]
        private static class AutomaticGunPatch
        {
            private static bool Prefix(Gun __instance)
            {
                // The station loops over all guns even when only one mount was cleared to fire.
                return automaticGun == null || automaticGun == __instance;
            }
        }

        [HarmonyPatch(typeof(Hardpoint), nameof(Hardpoint.SpawnMount))]
        private static class MountVisualPatch
        {
            private static void Postfix(Aircraft __0, WeaponMount __1, GameObject __result)
            {
                if (__result == null || __1 == null || (__1.jsonKey != MountKey && __1.jsonKey != FlankMountKey) ||
                    __0 == null || __0.definition == null ||
                    !SuperheavyGroundPhysicsPlugin.IsFamily(__0.definition) ||
                    __result.GetComponent<SuperheavyGroundDefense>() != null)
                    return;

                var defense = __result.AddComponent<SuperheavyGroundDefense>();
                defense.aircraft = __0;
                defense.flank = __1.jsonKey == FlankMountKey;
            }
        }

        private IEnumerator Start()
        {
            station = aircraft.transform.InverseTransformPoint(transform.position);
            outwardSign = Vector3.Dot(transform.up, aircraft.transform.up) >= 0f ? 1f : -1f;
            var fallback = transform.Find("Turret Traverse/Armored Turret")?.GetComponent<MeshRenderer>();
            RegisterPaintSurfaces(fallback);

            if (aircraft.TryGetComponent<SuperheavyGroundLiveries>(out var airframePaint))
                ApplyLivery(airframePaint.CurrentLivery, airframePaint.CurrentChannel);
            else
                ApplyLivery(currentLivery, currentChannel);

            var retry = new WaitForSecondsRealtime(.5f);
            for (int attempt = 0; attempt < 11; attempt++)
            {
                if (Encyclopedia.Lookup != null &&
                    Encyclopedia.Lookup.TryGetValue(SkySentryKey, out UnitDefinition source) &&
                    source != null && source.unitPrefab != null && ApplySkin(source.unitPrefab))
                    yield break;

                yield return retry;
            }

            if (!fallbackReported)
            {
                fallbackReported = true;
                log.LogInfo("Courier is using its bundled turret skin. The optional Chimera Sky Sentry model is unavailable.");
            }
        }

        private void RegisterPaintSurfaces(MeshRenderer renderer)
        {
            if (renderer == null)
                return;
            var materials = renderer.sharedMaterials;
            Vector3 origin = transform.InverseTransformPoint(renderer.transform.position);
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null || material.shader.name != ArmorShaderName)
                    continue;
                if (armorMaterial == null)
                    armorMaterial = material;
                painted.Add((renderer, i, origin, material));
            }
        }

        private bool ApplySkin(GameObject source)
        {
            var sourceTurret = source.transform.Find(TurretPath);
            var sourceBarrel = sourceTurret?.Find(BarrelName);
            var sourceMuzzle = sourceBarrel?.Find("muzzle");
            var yaw = transform.Find("Turret Traverse");
            var pitch = yaw?.Find("Gun Elevation");
            var muzzle = pitch?.Find("Muzzle");
            var bearing = transform.Find("Circular Bearing");
            if (sourceTurret == null || yaw == null ||
                !sourceTurret.TryGetComponent(out MeshFilter bodyMesh) || bodyMesh.sharedMesh == null ||
                !sourceTurret.TryGetComponent(out MeshRenderer bodyMaterial) ||
                bodyMaterial.sharedMaterial == null || bodyMaterial.sharedMaterials.Any(material => material == null))
                return false;

            if (flank)
            {
                var customArmor = yaw.Find("Armored Turret")?.GetComponent<MeshRenderer>();
                var mantlet = pitch?.Find("Articulated Mantlet")?.GetComponent<MeshRenderer>();
                var rotor = pitch?.Find("Rotary Assembly");
                var fittedMesh = customArmor?.GetComponent<MeshFilter>()?.sharedMesh;
                if (customArmor == null || mantlet == null || rotor == null || muzzle == null || fittedMesh == null ||
                    fittedMesh.subMeshCount != bodyMaterial.sharedMaterials.Length)
                    return false;
                // Preserve the approved shell's open trunnion pocket. Only its
                // materials come from the installed turret: the unmodified native
                // mesh contains two plates through the moving mantlet's envelope.
                var nativeArmor = AddRenderer(yaw, "Sky Sentry body", fittedMesh, bodyMaterial);
                customArmor.enabled = false;
                mantlet.enabled = true;
                painted.Clear();
                usesNativeSkin = true;
                BindArmor(nativeArmor, 0, "HLT_skin", yaw.localPosition);
                ApplyLivery(currentLivery, currentChannel);
                log.LogInfo($"Courier 20 mm uses the fitted Sky Sentry shell, articulated mantlet and native materials at {transform.parent.name}.");
                return true;
            }

            if (sourceBarrel == null || sourceMuzzle == null || pitch == null || muzzle == null || bearing == null ||
                !sourceBarrel.TryGetComponent(out MeshFilter barrelMesh) || barrelMesh.sharedMesh == null ||
                !sourceBarrel.TryGetComponent(out MeshRenderer barrelMaterial) || barrelMaterial.sharedMaterial == null ||
                barrelMaterial.sharedMaterials.Any(material => material == null))
                return false;

            // Reproduce the turret-only Blender fit. Its .49 m bearing adaptor
            // seats the turret 75 mm outward, including on inverted mounts.
            yaw.localPosition = Vector3.up * (.075f - bodyMesh.sharedMesh.bounds.min.y);
            pitch.localPosition = sourceBarrel.localPosition;
            muzzle.localPosition = sourceMuzzle.localPosition;
            muzzle.localRotation = sourceMuzzle.localRotation;
            bearing.localScale = new Vector3(.49f / .93f, .70f, .49f / .93f);

            foreach (var fallback in yaw.GetComponentsInChildren<MeshRenderer>(true))
                fallback.enabled = false;

            var body = AddRenderer(yaw, "Sky Sentry body", bodyMesh.sharedMesh, bodyMaterial);
            var barrel = AddRenderer(pitch, "Sky Sentry barrel", barrelMesh.sharedMesh, barrelMaterial);
            painted.Clear();
            BindArmor(body, 0, "HLT_skin", yaw.localPosition);
            BindArmor(barrel, 1, "HLT_container", yaw.localPosition + pitch.localPosition);
            ApplyLivery(currentLivery, currentChannel);
            log.LogInfo($"Superheavy mounted the installed Chimera Sky Sentry visual at {transform.parent.name}.");
            return true;
        }

        private void BindArmor(MeshRenderer renderer, int slot, string donorMaterial, Vector3 origin)
        {
            var materials = renderer.sharedMaterials;
            if (armorMaterial == null || slot >= materials.Length || materials[slot] == null)
                return;

            string materialName = materials[slot].name;
            if (!string.Equals(materialName, donorMaterial, StringComparison.Ordinal) &&
                !string.Equals(materialName, donorMaterial + " (Instance)", StringComparison.Ordinal))
                return;

            painted.Add((renderer, slot, origin, materials[slot]));
        }

        internal void ApplyLivery(LiveryData livery, int channel)
        {
            currentLivery = livery;
            currentChannel = channel;
            if (painted.Count == 0)
                return;

            bool hasPaint = livery != null && livery.Texture != null;
            bool urban = hasPaint && livery.name == "SHGT_Livery_bdf-digital";
            bool planar = channel == 1 && !urban;
            Vector4 chart = ChartAtStation(planar, urban);
            var block = new MaterialPropertyBlock();
            foreach (var surface in painted)
            {
                if (surface.Renderer == null)
                    continue;

                // This renderer belongs to Superheavy. Other slots keep their
                // donor material references and use their original mesh UVs.
                var materials = surface.Renderer.sharedMaterials;
                Material desired = hasPaint ? armorMaterial : surface.Original;
                if (materials[surface.Slot] != desired)
                {
                    materials[surface.Slot] = desired;
                    surface.Renderer.sharedMaterials = materials;
                }
                if (!hasPaint)
                {
                    surface.Renderer.SetPropertyBlock(null, surface.Slot);
                    continue;
                }

                surface.Renderer.GetPropertyBlock(block, surface.Slot);
                block.SetFloat(PaintMode, (planar ? 1f : 2f) + (flank && !usesNativeSkin ? 2f : 0f));
                block.SetTexture(BaseMap, livery.Texture);
                block.SetFloat(Smoothness, livery.Glossiness);
                block.SetVector(PaintChart, chart);
                block.SetVector(PaintOrigin, new Vector4(surface.Origin.x, surface.Origin.y, surface.Origin.z, outwardSign));
                surface.Renderer.SetPropertyBlock(block, surface.Slot);
            }
            if (hasPaint)
                log.LogDebug($"Superheavy turret paint {livery.name}: {transform.parent.name}, {painted.Count} armor slots.");
        }

        private Vector4 ChartAtStation(bool planar, bool urban)
        {
            const float atlasWidth = 8192f;
            const float atlasHeight = 4096f;
            float lengthPhase = (34f - station.z) / 68f;
            if (flank)
            {
                if (urban || planar)
                {
                    bool portChart = station.x > 0f;
                    float side = portChart ? 24f : 782f;
                    if (portChart)
                        lengthPhase = 1f - lengthPhase;
                    return new Vector4((24f + 4048f * lengthPhase) / atlasWidth,
                        1f - (side + 710f * .55f) / atlasHeight,
                        4048f / (68f * atlasWidth), 710f / (15.5f * atlasHeight));
                }
                float sidePhase = station.x > 0f ? .26f : .76f;
                return new Vector4((24f + 4048f * lengthPhase) / atlasWidth,
                    1f - (24f + 1468f * (.065f + .87f * sidePhase)) / atlasHeight,
                    4048f / (68f * atlasWidth), 1468f * .87f / (2f * Mathf.PI * 4.6f * atlasHeight));
            }
            if (urban)
            {
                // Urban camo deliberately omits the dorsal center strip and
                // belly. Its fuselage flank carries the visible pixel band.
                float side = outwardSign > 0f ? 24f : 782f;
                return new Vector4((24f + 4048f * lengthPhase) / atlasWidth,
                    1f - (side + 710f * .53f) / atlasHeight,
                    4048f / (68f * atlasWidth), 710f / (15.5f * atlasHeight));
            }

            if (planar)
            {
                float side = outwardSign > 0f ? 24f : 2072f;
                float across = (37f - station.x) / 74f;
                return new Vector4((side + 2000f * across) / atlasWidth,
                    1f - (1540f + 2060f * lengthPhase) / atlasHeight,
                    2000f / (74f * atlasWidth), 2060f / (68f * atlasHeight));
            }

            // The barrel and armor hold the pattern as they rotate. Unwrap
            // a local patch using the adjacent hull's metres per texel.
            // Sample just off the belly seam for the inverted stations.
            float phase = outwardSign > 0f ? .50f : .045f;
            float radius = Mathf.Max(3f, Mathf.Abs(station.y - 7.15f));
            return new Vector4((24f + 4048f * lengthPhase) / atlasWidth,
                1f - (24f + 1468f * (.065f + .87f * phase)) / atlasHeight,
                4048f / (68f * atlasWidth), 1468f * .87f / (2f * Mathf.PI * radius * atlasHeight));
        }

        private static MeshRenderer AddRenderer(Transform frame, string name, Mesh mesh, MeshRenderer source)
        {
            // Reuse loaded render assets without instantiating the ground unit.
            var visual = new GameObject(name) { layer = frame.gameObject.layer };
            visual.transform.SetParent(frame, false);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            return renderer;
        }
    }
}
