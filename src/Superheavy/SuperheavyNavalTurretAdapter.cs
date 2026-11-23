using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Japensou.NuclearOption.Superheavy
{
    // Optional native-dependency adapter. No Naval mesh, texture or prefab is
    // redistributed: clone the exact registered installed donor at runtime.
    internal static class SuperheavyNavalTurretAdapter
    {
        internal const string MountKey="jp_bg280g_203n",Marker="SHGT_RuntimeNaval203";
        internal const string DonorKey="Aryx_StrikeCarrier1";
        internal const string DonorPath="Aryx_StrikeCarrier1_Deck_Fore/Aryx_StrikeCarrier1_Hull_Launchers/Aryx_StrikeCarrier1_Bow/Aryx_StrikeCarrier1_DeckGun_01";
        private static readonly AccessTools.FieldRef<Turret,Unit> TurretOwner=AccessTools.FieldRefAccess<Turret,Unit>("attachedUnit");
        private static readonly AccessTools.FieldRef<Turret,UnitPart[]> CriticalParts=AccessTools.FieldRefAccess<Turret,UnitPart[]>("criticalParts");
        private static readonly AccessTools.FieldRef<Turret,List<TargetDetector>> Detectors=AccessTools.FieldRefAccess<Turret,List<TargetDetector>>("targetDetectors");
        private static readonly AccessTools.FieldRef<Turret,WeaponStation[]> Stations=AccessTools.FieldRefAccess<Turret,WeaponStation[]>("weaponStations");
        private static readonly AccessTools.FieldRef<Turret,Weapon> AimWeapon=AccessTools.FieldRefAccess<Turret,Weapon>("aimSafetyWeapon");
        private static readonly AccessTools.FieldRef<Turret,bool> Manual=AccessTools.FieldRefAccess<Turret,bool>("manual");
        private static readonly AccessTools.FieldRef<Turret,float> Traverse=AccessTools.FieldRefAccess<Turret,float>("traverseRange");
        private static readonly AccessTools.FieldRef<Turret,float> MinimumElevation=AccessTools.FieldRefAccess<Turret,float>("minElevation");
        private static readonly AccessTools.FieldRef<Turret,float> MaximumElevation=AccessTools.FieldRefAccess<Turret,float>("maxElevation");
        private static readonly AccessTools.FieldRef<Gun,MissileDefinition> Guided=AccessTools.FieldRefAccess<Gun,MissileDefinition>("guidedProjectile");
        private static readonly AccessTools.FieldRef<Gun,int> Capacity=AccessTools.FieldRefAccess<Gun,int>("magazineCapacity");
        private static readonly AccessTools.FieldRef<Gun,int> Reserves=AccessTools.FieldRefAccess<Gun,int>("magazines");
        private static readonly AccessTools.FieldRef<Gun,bool> StartLoaded=AccessTools.FieldRefAccess<Gun,bool>("startLoaded");
        private static readonly AccessTools.FieldRef<Gun,float> FireRate=AccessTools.FieldRefAccess<Gun,float>("fireRate");
        private static readonly AccessTools.FieldRef<Gun,float> ReloadTime=AccessTools.FieldRefAccess<Gun,float>("reloadTime");
        private static readonly AccessTools.FieldRef<Missile,float> Yield=AccessTools.FieldRefAccess<Missile,float>("blastYield");
        private static readonly FieldInfo MissileDefinitionField=AccessTools.Field(typeof(Missile),"definition");
        private static ManualLogSource log;
        private static bool IsBg(Aircraft a)=>a!=null&&a.definition?.jsonKey=="jp_bg280_bomber";
        private static bool IsMount(WeaponMount mount)=>mount!=null&&mount.jsonKey==MountKey;

        internal static void Install(Harmony harmony,ManualLogSource logger)
        {
            log=logger;
            harmony.CreateClassProcessor(typeof(SpawnPatch)).Patch();
            harmony.CreateClassProcessor(typeof(OptionalMountPolicy)).Patch();
            harmony.CreateClassProcessor(typeof(NuclearFireGuard)).Patch();
        }

        // The loadout/catalog compatibility layer calls this before exposing the
        // optional mount. Missing Naval never falls back to another gun model.
        internal static bool CanOffer(WeaponMount mount,out string reason)
        {
            if(!IsMount(mount)){reason="not the optional203mm mount";return false;}
            if(mount.prefab==null||mount.info==null||mount.info.name!="BG280G_203N_Info"||!mount.info.nuclear||
                mount.ammo!=6||mount.turret||mount.emptyMass<=0)
            {reason="203mm runtime placeholder/info/ammunition contract is incomplete";return false;}
            var hosts=mount.prefab.GetComponentsInChildren<Transform>(true).Where(t=>t.name==Marker).ToArray();
            if(hosts.Length!=1||
                mount.prefab.GetComponentsInChildren<Gun>(true).Length!=0||mount.prefab.GetComponentsInChildren<Turret>(true).Length!=0)
            {reason="203mm requires exactly one empty authored runtime host, without a surrogate gun";return false;}
            if(Vector3.Distance(hosts[0].lossyScale,Vector3.one)>.001f)
            {reason="203mm authored host must be unit scale; native2.589334965 scale is retained by the donor clone";return false;}
            if(!TryArc(hosts[0],out _,out reason))return false;
            if(!TryDonor(out _,out _,out reason))return false;
            return TryShell(mount.info,out _,out reason);
        }

        private static bool TryDonor(out Turret turret,out Gun gun,out string reason)
        {
            turret=null;gun=null;
            if(Encyclopedia.i==null||Encyclopedia.Lookup==null||
                !Encyclopedia.Lookup.TryGetValue(DonorKey,out var definition)||definition?.unitPrefab==null)
            {reason="Aryx Naval Expansion Andromeda donor is not installed/registered";return false;}
            var root=definition.unitPrefab.transform.Find(DonorPath);
            if(root==null||(turret=root.GetComponent<Turret>())==null||
                (gun=root.Find("Deckgun_203")?.GetComponent<Gun>())==null||gun.info?.name!="Aryx_NavalGun_203mm")
            {reason="installed Andromeda203mm hierarchy differs from the measured donor";return false;}
            var guns=root.GetComponentsInChildren<Gun>(true);var turrets=root.GetComponentsInChildren<Turret>(true);
            if(guns.Length!=1||turrets.Length!=1||root.Find("Deckgun_203/Muzzle")==null)
            {reason="Andromeda203mm subtree is not a single closed gun/turret assembly";return false;}
            if(Vector3.Distance(root.localScale,Vector3.one*2.589334965f)>.001f)
            {reason="Andromeda203mm donor scale changed; repeat the physical fit audit";return false;}
            reason=null;return true;
        }
        private static bool TryArc(Transform host,out Vector3 arc,out string reason)
        {
            var marker=host.Find("SHGT_Naval203Arc");arc=marker==null?Vector3.zero:marker.localScale;
            if(marker==null||float.IsNaN(arc.x)||float.IsNaN(arc.y)||float.IsNaN(arc.z)||
                float.IsInfinity(arc.x)||float.IsInfinity(arc.y)||float.IsInfinity(arc.z)||
                arc.x<=0||arc.x>180||arc.y<0||arc.z>90||arc.y>arc.z)
            {reason="203mm requires measured traverse/minElevation/maxElevation degrees in SHGT_Naval203Arc.localScale";return false;}
            reason=null;return true;
        }

        private static bool TryShell(WeaponInfo info,out MissileDefinition definition,out string reason)
        {
            definition=null;var shell=info?.weaponPrefab==null?null:info.weaponPrefab.GetComponent<Missile>();
            if(shell==null||MissileDefinitionField==null||Mathf.Abs(Yield(shell)-500000f)>1f||
                (definition=MissileDefinitionField.GetValue(shell) as MissileDefinition)==null)
            {reason="own registered0.5kt shell prefab/definition is missing or has a different yield";return false;}
            // Do not invent a network definition or cache index from a cloned
            // ScriptableObject. The mod's builder/Blueprinter owns registration.
            if(Encyclopedia.i==null||!Encyclopedia.Lookup.TryGetValue(definition.jsonKey,out var registered)||registered!=definition)
            {reason="own203mm shell definition is not registered by the native encyclopedia";return false;}
            reason=null;return true;
        }

        [HarmonyPatch(typeof(WeaponMount),nameof(WeaponMount.NotAllowed),new[]{typeof(bool)})]
        private static class OptionalMountPolicy
        {
            // Preserve native rank/stock/escalation rules. This adds only the
            // optional donor/profile availability restriction, including saves.
            private static void Postfix(WeaponMount __instance,ref bool __result)
            {if(!__result&&IsMount(__instance)&&!CanOffer(__instance,out _))__result=true;}
        }

        [HarmonyPatch(typeof(Hardpoint),nameof(Hardpoint.SpawnMount))]
        private static class SpawnPatch
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Aircraft __0,WeaponMount __1,ref GameObject __result)
            {
                if(!IsBg(__0)||!IsMount(__1))return true;
                if(CanOffer(__1,out var reason))return true;
                log?.LogWarning("BG-280203mm retained/unavailable: "+reason);__result=null;return false;
            }
            [HarmonyPriority(Priority.First+200)]
            private static void Postfix(Hardpoint __instance,Aircraft __0,WeaponMount __1,GameObject __result)
            {
                if(!IsBg(__0)||!IsMount(__1)||__result==null)return;
                var host=__result.GetComponentsInChildren<Transform>(true).SingleOrDefault(t=>t.name==Marker);
                if(host==null)return;
                var previous=host.GetComponent<SuperheavyNaval203Binding>();
                if(previous!=null)return; // Never add ammunition or native controllers twice.
                var binding=host.gameObject.AddComponent<SuperheavyNaval203Binding>();
                GameObject staging=null;
                try
                {
                    if(Vector3.Distance(host.lossyScale,Vector3.one)>.001f)throw new InvalidOperationException("203mm live host scale differs from the measured unit-scale adapter");
                    if(!TryDonor(out var donor,out _,out var reason)||!TryShell(__1.info,out var shell,out reason))
                        throw new InvalidOperationException(reason);
                    if(!TryArc(host,out var arc,out reason))throw new InvalidOperationException(reason);
                    staging=new GameObject("203mm inactive dependency preparation");staging.SetActive(false);staging.transform.SetParent(host,false);
                    var clone=Object.Instantiate(donor.gameObject,staging.transform,false);
                    clone.name="BG280G_203N_OriginalAndromedaTurret";
                    // Unity preserves original Mesh/Material/UV references and
                    // remaps all local child references during this clone.
                    clone.transform.localPosition=Vector3.zero;clone.transform.localRotation=Quaternion.identity;
                    var gun=clone.GetComponentsInChildren<Gun>(true).Single();var turret=clone.GetComponentsInChildren<Turret>(true).Single();
                    binding.Gun=gun;binding.Turret=turret;
                    foreach(var part in clone.GetComponentsInChildren<UnitPart>(true))Object.DestroyImmediate(part);
                    foreach(var joint in clone.GetComponentsInChildren<Joint>(true))Object.DestroyImmediate(joint);
                    foreach(var collider in clone.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(collider);
                    foreach(var body in clone.GetComponentsInChildren<Rigidbody>(true))Object.DestroyImmediate(body);
                    TurretOwner(turret)=__0;CriticalParts(turret)=new[]{__instance.part};Detectors(turret)=new List<TargetDetector>();
                    Traverse(turret)=arc.x;MinimumElevation(turret)=arc.y;MaximumElevation(turret)=arc.z;
                    gun.attachedUnit=__0;gun.velocityInherit=__0.rb;gun.info=__1.info;gun.Safety=true;
                    Capacity(gun)=1;Reserves(gun)=5;StartLoaded(gun)=true;Guided(gun)=shell;
                    // The nuclear mount's own interval is the gameplay cadence;
                    // its cloned native Gun must not retain the conventional
                    // donor's faster manual rate/reload behind that same label.
                    float interval=Mathf.Max(.1f,__1.info.fireInterval);
                    FireRate(gun)=60f/interval;ReloadTime(gun)=interval;
                    // Same source ballistics and shell mass; only this mod-owned
                    // nuclear info changes. Never mutate Aryx's original info.
                    var nativeInfo=donor.GetComponentsInChildren<Gun>(true).Single().info;
                    __1.info.muzzleVelocity=nativeInfo.muzzleVelocity;__1.info.dragCoef=nativeInfo.dragCoef;
                    __1.info.gravMult=nativeInfo.gravMult;__1.info.massPerRound=nativeInfo.massPerRound;
                    __1.mass=__1.emptyMass+__1.ammo*__1.info.massPerRound;
                    Stations(turret)=new[]{new WeaponStation(__0,false,false,false,false){Weapons=new List<Weapon>{gun},Turrets=new List<Turret>(),WeaponInfo=gun.info}};
                    AimWeapon(turret)=gun;
                    CloseReferences(clone,__0,__instance.part);
                    // Activate only after all ship ownership/part references
                    // have been removed; native Awake sees the aircraft owner.
                    staging.SetActive(true);
                    if(gun.GetAmmoLoaded()!=1||gun.ammo!=6)throw new InvalidOperationException("203mm native Awake did not create exactly six finite rounds");
                    if(__0.weaponStations.Any(s=>s.Weapons.Contains(gun)))throw new InvalidOperationException("203mm was unexpectedly registered before adapter handoff");
                    __0.weaponManager.RegisterWeapon(gun,__1,__instance);
                    binding.Registered=true;
                    turret.AttachToWeaponManager(__0);
                    binding.Ready=true;
                    SuperheavyRetractableBattery.Refresh(__0);
                    log?.LogInfo("BG-280203mm: original Andromeda subtree cloned/closed before Awake; one native Gun/controller, six rounds, original meshes/UVs, no second empty-mass application.");
                }
                catch(Exception error)
                {
                    binding.Ready=false;
                    if(binding.Gun!=null)binding.Gun.Safety=true;
                    if(binding.Turret!=null)binding.Turret.enabled=false;
                    if(!binding.Registered&&staging!=null)Object.Destroy(staging);
                    log?.LogError("BG-280203mm adapter failed closed; reconfigure before use: "+error);
                }
            }
        }

        private static void CloseReferences(GameObject root,Aircraft owner,UnitPart hardpointPart)
        {
            foreach(var component in root.GetComponentsInChildren<Component>(true))
            {
                if(component==null)continue;
                CloseFields(component,typeof(Component),root,owner,hardpointPart);
            }
            if(root.GetComponentsInChildren<UnitPart>(true).Length!=0||root.GetComponentsInChildren<Unit>(true).Length!=0)
                throw new InvalidOperationException("203mm clone still contains a standalone naval unit/part");
        }
        private static void CloseFields(object instance,Type stopAt,GameObject root,Aircraft owner,UnitPart hardpointPart)
        {
                for(var type=instance.GetType();type!=null&&type!=stopAt&&type!=typeof(Object);type=type.BaseType)
                foreach(var field in type.GetFields(BindingFlags.DeclaredOnly|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                {
                    if(field.IsInitOnly||field.IsLiteral)continue;
                    if(typeof(Object).IsAssignableFrom(field.FieldType))
                    {
                        var value=field.GetValue(instance) as Object;
                        if(IsForeignComponent(value,root,owner,hardpointPart))field.SetValue(instance,null);
                    }
                    else if(field.FieldType.IsArray&&typeof(Object).IsAssignableFrom(field.FieldType.GetElementType()))
                    {
                        var values=field.GetValue(instance) as Array;if(values==null)continue;
                        for(int i=0;i<values.Length;i++)if(IsForeignComponent(values.GetValue(i) as Object,root,owner,hardpointPart))values.SetValue(null,i);
                    }
                    else if(field.FieldType==typeof(AimSolver))
                    {
                        // AimSolver is a serialized managed class, not a Unity
                        // Component. Its cached owner/target may otherwise keep
                        // a ship reference after the visible hierarchy is closed.
                        var solver=field.GetValue(instance);
                        if(solver!=null)CloseFields(solver,typeof(object),root,owner,hardpointPart);
                    }
                }
        }
        private static bool IsForeignComponent(Object value,GameObject root,Aircraft owner,UnitPart part)
        {
            if(value==null||value==owner||value==owner.rb||value==part)return false;
            var frame=value is Component c?c.transform:null;
            // Meshes, materials, audio clips and effect prefabs are retained as
            // native shared assets. Foreign scene Component references are not.
            return frame!=null&&frame!=root.transform&&!frame.IsChildOf(root.transform);
        }

        [HarmonyPatch(typeof(Gun),nameof(Gun.Fire),new[]{typeof(Unit),typeof(Unit),typeof(Vector3),typeof(WeaponStation),typeof(GlobalPosition)})]
        private static class NuclearFireGuard
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Gun __instance,Unit __0,Unit __1)
            {
                if(__instance.info?.name!="BG280G_203N_Info"||!(__0 is Aircraft aircraft)||!IsBg(aircraft))return true;
                var binding=__instance.GetComponentInParent<SuperheavyNaval203Binding>();
                if(binding==null||!binding.Ready||binding.Gun!=__instance||binding.Turret==null||!Manual(binding.Turret)||
                    __1==null||__1.disabled||!SuperheavyRetractableBattery.CanFire(__instance))return false;
                __instance.SetTarget(__1);return true;
            }
        }
    }
    internal sealed class SuperheavyNaval203Binding:MonoBehaviour
    {
        internal Gun Gun;internal Turret Turret;internal bool Registered,Ready;
    }
}
