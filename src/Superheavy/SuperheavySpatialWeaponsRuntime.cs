using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using Object=UnityEngine.Object;
using P=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Point;

namespace Japensou.NuclearOption.Superheavy
{
    // Physical native mounts, independent of Courier transport cargo and of the
    // legacy420-node magazine. Slot0..8 keep their established identities.
    internal static class SuperheavySpatialWeaponsRuntime
    {
        internal const int LegacySetCount=9,MaximumItems=1024,ArmsSetCount=LegacySetCount+MaximumItems;
        internal const string CatalogSetName="SHGT_ArmamentCatalog",SlotPrefix="SHGT_ArmsSlot_",AnchorPrefix="SHGT_ArmsMount_",SavedPrefix="BGW36:";
        private static readonly ConditionalWeakTable<Aircraft,Layout> AircraftPlans=new ConditionalWeakTable<Aircraft,Layout>();
        private static readonly ConditionalWeakTable<Loadout,Layout> LoadoutPlans=new ConditionalWeakTable<Loadout,Layout>();
        private static readonly ConditionalWeakTable<WeaponManager,ManagerState> Managers=new ConditionalWeakTable<WeaponManager,ManagerState>();
        // Complex physics unparents native AeroParts, including the manager's
        // owning transform. The serialized native owner remains authoritative.
        private static readonly AccessTools.FieldRef<WeaponManager,Aircraft> ManagerAircraft=AccessTools.FieldRefAccess<WeaponManager,Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<LoadoutSelector,Aircraft> SelectorAircraft=AccessTools.FieldRefAccess<LoadoutSelector,Aircraft>("aircraft");
        private static readonly AccessTools.FieldRef<LoadoutSelector,List<WeaponSelector>> Controls=AccessTools.FieldRefAccess<LoadoutSelector,List<WeaponSelector>>("weaponSelectors");
        private static readonly Action<LoadoutSelector,bool> UpdateWeapons=AccessTools.MethodDelegate<Action<LoadoutSelector,bool>>(AccessTools.Method(typeof(LoadoutSelector),"UpdateWeapons",new[]{typeof(bool)}));
        private static readonly FieldInfo PylonOptions=AccessTools.Field(typeof(Hardpoint),"pylonOptions");
        private static readonly Array EmptyPylons=Array.CreateInstance(PylonOptions.FieldType.GetElementType(),0);
        private static ManualLogSource log;
        private static ConfigEntry<string> latest;
        private static SpawnRequest pending;
        private static bool installed;
        private static int mainThreadId;
        private const int MaximumValidationProofs=16;
        private static readonly Dictionary<string,ValidationProof> ApprovedValidations=new Dictionary<string,ValidationProof>(StringComparer.Ordinal);
        private static readonly Queue<string> ValidationOrder=new Queue<string>();
        private sealed class ValidationProof
        {
            internal readonly string Encoded,GeometryHash,CatalogHash,Fingerprint;
            internal ValidationProof(string encoded,WeaponPlanGeometry g,WeaponPlanValidation result)
            {Encoded=encoded;GeometryHash=g.GeometrySourceHash;CatalogHash=g.CatalogSourceHash;Fingerprint=result.LayoutFingerprint;}
        }
        internal sealed class Layout
        {
            internal readonly WeaponPlacement[] Items;
            internal readonly WeaponMount[] Mounts;
            internal readonly string Encoded;
            internal Layout(WeaponPlacement[] items,WeaponMount[] mounts){Items=items;Mounts=mounts;Encoded=Encode(items);}
        }
        private sealed class ManagerState
        {
            internal HardpointSet[] Base;
            internal Layout Prepared;
            internal readonly List<GameObject> Anchors=new List<GameObject>();
        }
        private sealed class SpawnRequest
        {
            internal Airbase Airbase;internal Player Player;internal WeaponMount[] Keys;internal Layout Plan;internal float Expires;
        }
        private sealed class SavedState { internal Layout Plan;internal List<SavedLoadout.SelectedMount> Original; }
        private sealed class DefaultsState { internal AircraftCustomization Custom;internal Loadout Original; }

        internal static void Install(Harmony harmony,ManualLogSource logger,ConfigFile config)
        {
            log=logger;mainThreadId=Thread.CurrentThread.ManagedThreadId;
            latest=config.Bind("Armament","Spatial layout","","Committed BG spatial armament layout; native mounts and positions, independent of Courier cargo.");
            harmony.CreateClassProcessor(typeof(SelectorScope)).Patch();
            harmony.CreateClassProcessor(typeof(ShowBaseSelectors)).Patch();
            harmony.CreateClassProcessor(typeof(GenerateLoadout)).Patch();
            harmony.CreateClassProcessor(typeof(RestoreDefaults)).Patch();
            harmony.CreateClassProcessor(typeof(PrepareDefaults)).Patch();
            harmony.CreateClassProcessor(typeof(SavedPlanPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PrepareNativeValidation)).Patch();
            harmony.CreateClassProcessor(typeof(CaptureRequest)).Patch();
            harmony.CreateClassProcessor(typeof(ReceiveRequest)).Patch();
            harmony.CreateClassProcessor(typeof(SpawnWeapons)).Patch();
            harmony.CreateClassProcessor(typeof(InitializeWeapons)).Patch();
            installed=true;
        }
        private static bool IsBg(Aircraft owner)=>owner!=null&&owner.definition?.jsonKey=="jp_bg280_bomber";
        private static Aircraft Owner(WeaponManager manager)=>manager==null?null:ManagerAircraft(manager)??manager.GetComponentInParent<Aircraft>(true);
        internal static bool CanManage(LoadoutSelector selector,Aircraft owner)=>installed&&GameManager.gameState==GameState.SinglePlayer&&
            IsBg(owner)&&selector!=null&&SelectorAircraft(selector)==owner&&GetCatalog(owner).Count>0;
        internal static IReadOnlyList<WeaponItemSpec> GetCatalog(Aircraft owner)
        {
            if(!IsBg(owner))return Array.Empty<WeaponItemSpec>();
            // BG can be opened before Courier. Repair the installed Aryx
            // original's lost subasset references before the first immutable
            // measurement cache, rather than depending on Courier's bay UI.
            if(Encyclopedia.WeaponLookup!=null&&Encyclopedia.WeaponLookup.TryGetValue("aryx_glidebomb400_single_internal",out var pgm)&&pgm!=null)
                SuperheavyMountedStores.RestoreOptionalBomb(pgm,log);
            return SuperheavyArmamentCatalog.GetCatalog(owner);
        }
        internal static WeaponPlanGeometry GetGeometry(Aircraft owner)
        {
            // Repair original optional meshes before any geometry/catalog cache
            // entry point, including native validation before the UI opens.
            GetCatalog(owner);
            var g=SuperheavyArmamentCatalog.GetGeometry(owner);
            if(g!=null)g.NativeIntegration=new WeaponNativeIntegration{Id="BG36 native physical mounts/free feeder/save bridge",
                GeometrySourceHash=g.GeometrySourceHash,CatalogSourceHash=g.CatalogSourceHash,
                LatestNativeEvidence="Integrated runtime0.36; release package requires its isolated native audit",
                SpawnSaveRestoreValidated=installed,ContinuousDeployValidated=installed,ArbitraryReleaseValidated=installed,
                CompoundPlacementSupported=installed,LayeredPlacementSupported=installed,PerRoundReleaseSupported=installed};
            return g;
        }
        internal static IReadOnlyList<WeaponPlacement> GetPlan(Aircraft owner)
        {
            if(!IsBg(owner))return Array.Empty<WeaponPlacement>();
            if(AircraftPlans.TryGetValue(owner,out var plan))return Array.AsReadOnly(plan.Items);
            // Reading the organizer never silently changes a native legacy
            // loadout. Its displayed conversion becomes active only on Apply.
            return InferLegacy(owner);
        }
        internal static WeaponPlanValidation Validate(Aircraft owner,IReadOnlyList<WeaponPlacement> plan)
        {
            if(!PrepareValidation(owner,plan,out var canonical,out var snapshot,out string reason))
            {var invalid=new WeaponPlanValidation();invalid.Issues.Add(reason);return invalid;}
            var result=SuperheavySpatialWeaponsPlan.TryValidate(canonical,snapshot);
            RecordValidation(owner,canonical,snapshot,result);return result;
        }
        // This is the main-thread capture boundary. The returned plan contains
        // measured canonical specs; the new geometry contains managed value
        // arrays only. A worker must call only the pure planner, never this
        // class, catalog measurement or any Unity object API.
        internal static bool PrepareValidation(Aircraft owner,IReadOnlyList<WeaponPlacement> plan,
            out WeaponPlacement[] canonical,out WeaponPlanGeometry snapshot,out string reason)
        {
            canonical=null;snapshot=null;
            if(!OnMainThread){reason="Armament validation capture must run on the game main thread.";return false;}
            if(!TryCanonical(owner,plan,out var layout,out reason))return false;
            var source=GetGeometry(owner);
            if(source==null){reason="Measured armament compartment geometry is unavailable.";return false;}
            canonical=layout.Items;
            snapshot=SuperheavySpatialWeaponsPlan.CloneGeometryForValidation(source,CancellationToken.None,null);
            reason=null;return true;
        }
        private static bool OnMainThread=>mainThreadId==0||Thread.CurrentThread.ManagedThreadId==mainThreadId;
        private static string ProofKey(string encoded,WeaponPlanGeometry geometry)=>encoded+"|"+(geometry.GeometrySourceHash??"")+"|"+(geometry.CatalogSourceHash??"");
        internal static bool RecordValidation(Aircraft owner,IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry snapshot,WeaponPlanValidation result)
        {
            if(!OnMainThread||!IsBg(owner)||snapshot==null||snapshot.ValidationCancellation.IsCancellationRequested)return false;
            var current=GetGeometry(owner);
            if(current==null||current.GeometrySourceHash!=snapshot.GeometrySourceHash||current.CatalogSourceHash!=snapshot.CatalogSourceHash)return false;
            return RecordValidation(plan,snapshot,result);
        }
        internal static bool RecordValidation(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry snapshot,WeaponPlanValidation result)
        {
            // Only the strict COMPLETE route result can become an Apply proof.
            // Source hashes select the matching measurements; they are never
            // themselves evidence that a route was checked successfully.
            if(!OnMainThread||plan==null||snapshot==null||snapshot.ValidationCancellation.IsCancellationRequested||result==null||
                !result.PlacementValid||!result.DeploymentProven||result.ReleasePathPending||!result.CanApplyNative||
                string.IsNullOrEmpty(result.LayoutFingerprint)||string.IsNullOrEmpty(snapshot.GeometrySourceHash)||string.IsNullOrEmpty(snapshot.CatalogSourceHash))return false;
            string encoded=Encode(plan),key=ProofKey(encoded,snapshot);
            if(!ApprovedValidations.ContainsKey(key))ValidationOrder.Enqueue(key);
            ApprovedValidations[key]=new ValidationProof(encoded,snapshot,result);
            while(ApprovedValidations.Count>MaximumValidationProofs&&ValidationOrder.Count>0)ApprovedValidations.Remove(ValidationOrder.Dequeue());
            return true;
        }
        internal static WeaponPlanValidation ValidatePlacement(Aircraft owner,IReadOnlyList<WeaponPlacement> plan)
        {
            if(!TryCanonical(owner,plan,out var canonical,out string reason))
            {var invalid=new WeaponPlanValidation();invalid.Issues.Add(reason);return invalid;}
            return SuperheavySpatialWeaponsPlan.TryValidate(canonical.Items,GetGeometry(owner),false);
        }
        private static bool TryCanonical(Aircraft owner,IReadOnlyList<WeaponPlacement> input,out Layout layout,out string reason)
        {
            layout=null;reason=null;
            if(!IsBg(owner)||input==null||input.Count>MaximumItems){reason="Invalid BG plan or native slot capacity exceeded.";return false;}
            var catalog=GetCatalog(owner).ToDictionary(s=>s.Id,StringComparer.Ordinal);
            var items=new WeaponPlacement[input.Count];var mounts=new WeaponMount[input.Count];
            for(int i=0;i<input.Count;i++)
            {
                var p=input[i];
                if(p.Spec==null||!catalog.TryGetValue(p.Spec.Id,out var spec)||
                    !SuperheavyArmamentCatalog.TryResolveMount(owner,spec.Id,out mounts[i],out reason))
                {reason="Installed native armament unavailable: "+(p.Spec?.Id??"missing item")+". "+reason;return false;}
                if(spec.Kind==SpatialWeaponKind.Gun&&Mathf.Abs(p.Y-spec.DefaultY)>.002f)
                {reason="Gun storage height is fixed to its measured rig; changing it would move the deployed base away from the skin.";return false;}
                items[i]=new WeaponPlacement(spec,p.X,p.Z,p.Y,p.Rotation90);
            }
            layout=new Layout(items,mounts);reason=null;return true;
        }
        internal static bool TryApply(LoadoutSelector selector,Aircraft owner,IReadOnlyList<WeaponPlacement> plan,out string reason)
        {
            if(!CanManage(selector,owner)){reason="BG spatial armament is available from the Solo native hangar.";return false;}
            if(!TryCanonical(owner,plan,out var candidate,out reason))return false;
            var geometry=GetGeometry(owner);
            if(geometry==null||!ApprovedValidations.TryGetValue(ProofKey(candidate.Encoded,geometry),out var proof)||
                proof.Encoded!=candidate.Encoded||proof.GeometryHash!=geometry.GeometrySourceHash||proof.CatalogHash!=geometry.CatalogSourceHash)
            {reason="Complete physical route validation is pending for this exact armament layout. Wait for the check before applying.";return false;}
            var controls=Controls(selector);
            if(controls==null||controls.Count!=LegacySetCount){reason="Native base selectors have not completed initialization.";return false;}
            var previous=controls.Select(c=>c.GetValue()).ToArray();AircraftPlans.TryGetValue(owner,out var old);
            try
            {
                for(int i=0;i<3;i++)controls[i].SetValue(null);
                Bind(owner,candidate);Prepare(owner.weaponManager,candidate);
                var requested=BuildLoadout(previous,candidate);
                if(!Allowed(selector,owner,requested,candidate,out reason))throw new InvalidOperationException(reason);
                UpdateWeapons(selector,true);
                if(!Matches(owner.loadout,candidate))throw new InvalidOperationException("Native preview did not retain all real spatial weapon mounts.");
                selector.SaveDefaults();latest.Value=candidate.Encoded;
                StampSavedUnit(owner,candidate);
                reason=null;return true;
            }
            catch(Exception error)
            {
                // Always preserve the original failure. A rollback error must
                // never replace the actual native apply exception in the log.
                log?.LogError("BG spatial native apply failed: "+error);
                try
                {
                    Bind(owner,old);Prepare(owner.weaponManager,old);
                    for(int i=0;i<LegacySetCount;i++)controls[i].SetValue(previous[i]);
                    UpdateWeapons(selector,true);
                }
                catch(Exception rollback){log?.LogError("BG spatial preview rollback also failed: "+rollback);}
                reason="The native game could not commit this physical armament layout: "+error.Message;
                log?.LogWarning(reason);return false;
            }
        }
        private static bool Allowed(LoadoutSelector selector,Aircraft owner,Loadout load,Layout plan,out string reason)
        {
            var hq=AccessTools.Field(typeof(LoadoutSelector),"hq").GetValue(selector) as FactionHQ;
            var airbase=AccessTools.Field(typeof(LoadoutSelector),"airbase").GetValue(selector) as Airbase;
            GameManager.GetLocalPlayer(out Player player);
            foreach(int i in Enumerable.Range(0,plan.Mounts.Length))
            {
                var mount=plan.Mounts[i];var set=owner.weaponManager.hardpointSets[LegacySetCount+i];
                if(mount.NotAllowed(MissionManager.AllowEventContent)||
                    !WeaponChecker.MountAllowedHQ(mount,hq)||!WeaponChecker.MountAllowedAirbase(mount,airbase)||
                    !WeaponChecker.MountAllowedNuclear(mount,set,airbase,player,hq))
                {reason="Native rules do not allow "+mount.mountName+(mount.info.nuclear?". Tactical nuclear equipment requires the appropriate rank, nuclear authorization and available warheads.":" at this faction/airbase.");return false;}
            }
            if(airbase!=null)
            {
                long warheads=0;for(int i=0;i<load.weapons.Count;i++)
                    if(load.weapons[i]?.info?.nuclear==true)warheads+=(long)load.weapons[i].ammo*owner.weaponManager.hardpointSets[i].hardpoints.Count;
                if(warheads>airbase.GetWarheads()){reason="The complete physical layout exceeds the airbase nuclear stock.";return false;}
            }
            reason=null;return true;
        }
        private static Loadout BuildLoadout(IEnumerable<WeaponMount> original,Layout plan)
        {
            var result=new Loadout();result.weapons=original.Take(LegacySetCount).ToList();
            while(result.weapons.Count<LegacySetCount)result.weapons.Add(null);
            if(plan!=null){for(int i=0;i<3;i++)result.weapons[i]=null;result.weapons.AddRange(plan.Mounts);Mark(result,plan);}
            return result;
        }
        private static void Bind(Aircraft owner,Layout plan)
        {
            AircraftPlans.Remove(owner);if(plan!=null)AircraftPlans.Add(owner,plan);
        }
        private static void Mark(Loadout load,Layout plan){LoadoutPlans.Remove(load);if(plan!=null)LoadoutPlans.Add(load,plan);}
        private static bool Matches(Loadout load,Layout plan)=>load?.weapons!=null&&load.weapons.Count==LegacySetCount+plan.Items.Length&&
            load.weapons.Take(3).All(m=>m==null)&&load.weapons.Skip(LegacySetCount).SequenceEqual(plan.Mounts);
        internal static bool TryGetPlan(Aircraft owner,out Layout plan)
        {plan=null;return owner!=null&&AircraftPlans.TryGetValue(owner,out plan);}

        private static ManagerState State(WeaponManager manager)
        {
            return Managers.GetValue(manager,m=>
            {
                // Cache the authoring-only asset library before removing it
                // from operational slots. Catalog owns the asset references.
                var owner=Owner(m);GetCatalog(owner);
                var source=m.hardpointSets.Where(s=>s.name!=CatalogSetName).Take(LegacySetCount).ToArray();
                if(source.Length!=LegacySetCount)throw new InvalidOperationException("BG native base9sets are missing.");
                return new ManagerState{Base=source};
            });
        }
        private static void Prepare(WeaponManager manager,Layout plan)
        {
            var state=State(manager);int count=plan?.Items.Length??0;
            if(state.Prepared!=null&&plan!=null&&state.Prepared.Encoded==plan.Encoded&&manager.hardpointSets.Length==LegacySetCount+count)return;
            foreach(var set in manager.hardpointSets.Skip(LegacySetCount).Where(s=>s.name.StartsWith(SlotPrefix,StringComparison.Ordinal)))set.RemoveMounts();
            foreach(var anchor in state.Anchors)if(anchor!=null)Object.Destroy(anchor);state.Anchors.Clear();
            var owner=Owner(manager);
            if(owner==null)throw new InvalidOperationException("BG spatial native WeaponManager has no serialized aircraft owner.");
            var rootPart=owner.GetComponent<AeroPart>();
            var gates=SuperheavyNativeDamage.Components<BayDoor>(owner,true).Where(g=>g.name.StartsWith("SHGT_BombBayGate_Cell",StringComparison.Ordinal)).ToArray();
            var sets=new List<HardpointSet>(state.Base);var g=GetGeometry(owner);
            for(int i=0;i<count;i++)
            {
                var item=plan.Items[i];var point=new Hardpoint();Transform frame;
                if(owner.gameObject.scene.IsValid())
                {
                    var go=new GameObject(AnchorPrefix+i.ToString("D4",CultureInfo.InvariantCulture));go.transform.SetParent(owner.transform,false);
                    go.layer=rootPart.gameObject.layer;frame=go.transform;state.Anchors.Add(go);
                    var rotation=Quaternion.Euler(0,item.Rotation90?90f:0f,0);
                    frame.localRotation=rotation;frame.localPosition=Anchor(item,g)-rotation*V(item.Spec.NativeAnchorOffset);
                }
                else frame=state.Base[0].hardpoints[0].transform; // Validation template only; rebuilt on the actual spawned aircraft.
                point.transform=frame;point.part=rootPart;point.HardpointIndex=LegacySetCount+i;point.bayDoors=gates;point.doorOpenDuration=3f;
                point.BuiltInWeapons=Array.Empty<Weapon>();point.BuiltInTurrets=Array.Empty<Turret>();
                // The native Hardpoint constructor leaves this array null.
                // SpawnMounts/RemoveMounts always call ShowPylon, whose loop
                // reads Length even when there is no external pylon renderer.
                PylonOptions.SetValue(point,EmptyPylons);
                sets.Add(new HardpointSet{name=SlotPrefix+i.ToString("D4",CultureInfo.InvariantCulture),hardpoints=new List<Hardpoint>{point},
                    weaponOptions=new List<WeaponMount>{null,plan.Mounts[i]},precludingHardpointSets=new List<byte>(),
                    SymmetryWithPrev=false,SymmetryName=""});
            }
            // Native legalWeaponsCache is a static scratch list, not a cache of
            // this manager's hardpoints. WeaponChecker clears/reuses it on each
            // query. Setting it to null breaks subsequent AI loadout selection
            // globally, including aircraft unrelated to the BG.
            manager.hardpointSets=sets.ToArray();state.Prepared=plan;
        }
        internal static Vector3 Anchor(WeaponPlacement p,WeaponPlanGeometry g)=>new Vector3(g.LeftX+p.X+(p.Rotation90?p.Spec.Length:p.Spec.Width)*.5f,
            g.FloorY+p.Y,g.FrontZ-p.Z-(p.Rotation90?p.Spec.Width:p.Spec.Length)*.5f);
        private static Vector3 V(P p)=>new Vector3(p.X,p.Y,p.Z);

        internal static bool IsSpatialStore(MountedMissile rail)=>rail!=null&&rail.GetComponentInParent<SuperheavySpatialWeaponItem>()!=null;
        internal static bool IsSpatialStation(WeaponStation station)=>station?.Weapons!=null&&station.Weapons.OfType<MountedMissile>().Any(IsSpatialStore);
        internal static bool TryGetItem(Weapon weapon,out SuperheavySpatialWeaponItem item)
        {item=weapon==null?null:weapon.GetComponentInParent<SuperheavySpatialWeaponItem>();return item!=null;}
        private static void BindSpawned(Aircraft owner,Layout plan)
        {
            for(int i=0;i<plan.Items.Length;i++)
            {
                var point=owner.weaponManager.hardpointSets[LegacySetCount+i].hardpoints[0];
                var root=AccessTools.Field(typeof(Hardpoint),"spawnedPrefab").GetValue(point) as GameObject;
                if(root==null)throw new InvalidOperationException("Native mount failed to spawn spatial item["+i+"]: "+plan.Mounts[i].jsonKey);
                var item=root.GetComponent<SuperheavySpatialWeaponItem>()??root.AddComponent<SuperheavySpatialWeaponItem>();
                item.Owner=owner;item.Index=i;item.Placement=plan.Items[i];item.Mount=plan.Mounts[i];item.Point=point;
                SuperheavySpatialWeaponFeed.Configure(item);
                int order=0;
                foreach(var marker in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("SHGT_BatteryOrder_",StringComparison.Ordinal)))
                    marker.name="SHGT_BatteryOrder_"+(i*16+order++).ToString(CultureInfo.InvariantCulture);
                foreach(var turret in root.GetComponentsInChildren<Turret>(false))
                {
                    var gun=turret.GetWeapon();
                    if(gun==null||!owner.weaponStations.Any(s=>s.Weapons.Contains(gun)))continue;
                    if(!owner.weaponStations.Any(s=>s.Turrets.Contains(turret)))turret.AttachToWeaponManager(owner);
                }
            }
            SuperheavyRetractableBattery.Refresh(owner);StampSavedUnit(owner,plan);
            var stamp=owner.GetComponent<SuperheavySpatialWeaponsStamp>()??owner.gameObject.AddComponent<SuperheavySpatialWeaponsStamp>();stamp.Owner=owner;
        }
        internal static void StampSavedUnit(Aircraft owner,Layout plan)
        {
            if(!(owner.SavedUnit is SavedAircraft saved))return;
            var data=new SavedLoadout{Selected=owner.loadout.weapons.Select(m=>new SavedLoadout.SelectedMount{Key=m?.jsonKey}).ToList()};
            data.Selected.Add(new SavedLoadout.SelectedMount{Key=plan.Encoded});saved.savedLoadout=data;
        }

        internal static bool HasSavedPlan(SavedLoadout saved)=>saved?.Selected?.Any(m=>m.Key?.StartsWith(SavedPrefix,StringComparison.Ordinal)==true)==true;
        internal static string Encode(IReadOnlyList<WeaponPlacement> plan)
        {
            using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream))
            {
                w.Write(1);w.Write(plan.Count);
                foreach(var p in plan){w.Write(p.Spec.Id);w.Write(p.X);w.Write(p.Z);w.Write(p.Y);w.Write(p.Rotation90);}
                w.Flush();return SavedPrefix+Convert.ToBase64String(stream.ToArray());
            }
        }
        private static bool Decode(Aircraft owner,string encoded,out Layout plan,out string reason)
        {
            plan=null;
            try
            {
                if(encoded==null||!encoded.StartsWith(SavedPrefix,StringComparison.Ordinal)||encoded.Length>250000)throw new InvalidDataException("Spatial weapon payload missing/oversized.");
                var catalog=GetCatalog(owner).ToDictionary(s=>s.Id,StringComparer.Ordinal);var list=new List<WeaponPlacement>();
                using(var stream=new MemoryStream(Convert.FromBase64String(encoded.Substring(SavedPrefix.Length))))using(var r=new BinaryReader(stream))
                {
                    if(r.ReadInt32()!=1)throw new InvalidDataException("Unsupported spatial weapon schema.");int n=r.ReadInt32();
                    if(n<0||n>MaximumItems)throw new InvalidDataException("Spatial weapon item count invalid.");
                    for(int i=0;i<n;i++)
                    {
                        string key=r.ReadString();float x=r.ReadSingle(),z=r.ReadSingle(),y=r.ReadSingle();bool turn=r.ReadBoolean();
                        if(!catalog.TryGetValue(key,out var spec))throw new InvalidDataException("Installed measured armament missing: "+key);
                        list.Add(new WeaponPlacement(spec,x,z,y,turn));
                    }
                    if(stream.Position!=stream.Length)throw new InvalidDataException("Unexpected spatial weapon payload tail.");
                }
                if(!TryCanonical(owner,list,out plan,out reason))return false;
                // Restore retains the existing native layout after complete
                // placement/gun-path checks. It does NOT approve a new Apply:
                // stores are re-routed against real retained stock by the live
                // feeder on each release; no all-store BFS runs during menu
                // assignment, configuration load or SavedLoadout restoration.
                var result=SuperheavySpatialWeaponsPlan.TryValidateForRestore(plan.Items,GetGeometry(owner));
                if(!result.PlacementValid||!result.DeploymentProven){reason=string.Join("; ",result.Issues);plan=null;return false;}
                reason=null;return true;
            }
            catch(Exception e){reason=e.Message;return false;}
        }
        private static IReadOnlyList<WeaponPlacement> InferLegacy(Aircraft owner)
        {
            var result=new List<WeaponPlacement>();var g=GetGeometry(owner);if(g==null)return result;
            var catalog=GetCatalog(owner);var manager=owner.weaponManager;if(manager?.hardpointSets==null)return result;
            foreach(var set in manager.hardpointSets.Take(3))
            {
                var mount=set.hardpoints.FirstOrDefault()?.GetMount();if(mount==null)continue;
                var spec=catalog.FirstOrDefault(s=>s.Id==mount.jsonKey);
                if(spec==null)
                {
                    // Existing420/32 native magazines stay visible in the
                    // organizer as their ACTUAL individual retained rounds.
                    // Reading does not replace/spawn them or invent a rack.
                    var unit=catalog.FirstOrDefault(s=>s.Kind!=SpatialWeaponKind.Gun&&s.MountedRounds==1&&
                        SuperheavyArmamentCatalog.ResolveMount(s.Id)?.info==mount.info);
                    var spawned=AccessTools.Field(typeof(Hardpoint),"spawnedPrefab").GetValue(set.hardpoints[0]) as GameObject;
                    if(unit==null||spawned==null)continue;
                    foreach(var rail in spawned.GetComponentsInChildren<MountedMissile>(false).Where(r=>r.GetAmmoLoaded()==1&&r.IsAttached()))
                    {
                        bool first=true;Bounds bounds=default;
                        foreach(var filter in rail.GetComponentsInChildren<MeshFilter>(false))
                        {
                            if(filter.sharedMesh==null)continue;var b=filter.sharedMesh.bounds;
                            for(int n=0;n<8;n++)
                            {
                                var q=owner.transform.InverseTransformPoint(filter.transform.TransformPoint(new Vector3((n&1)==0?b.min.x:b.max.x,(n&2)==0?b.min.y:b.max.y,(n&4)==0?b.min.z:b.max.z)));
                                if(first){bounds=new Bounds(q,Vector3.zero);first=false;}else bounds.Encapsulate(q);
                            }
                        }
                        if(!first)result.Add(new WeaponPlacement(unit,bounds.center.x-g.LeftX-unit.Width*.5f,
                            g.FrontZ-bounds.center.z-unit.Length*.5f,bounds.min.y-g.FloorY));
                    }
                    continue;
                }
                var hp=set.hardpoints[0];var anchor=owner.transform.InverseTransformPoint(hp.transform.TransformPoint(V(spec.NativeAnchorOffset)));
                result.Add(new WeaponPlacement(spec,anchor.x-g.LeftX-spec.Width*.5f,g.FrontZ-anchor.z-spec.Length*.5f,anchor.y-g.FloorY));
            }
            return result;
        }

        [HarmonyPatch(typeof(LoadoutSelector),nameof(LoadoutSelector.AssignAircraft))]
        private static class SelectorScope
        {
            private static void Prefix(Aircraft __0)
            {if(IsBg(__0)){AircraftPlans.TryGetValue(__0,out var plan);Prepare(__0.weaponManager,plan);}}
            private static void Postfix(LoadoutSelector __instance,Aircraft __0)
            {
                if(!IsBg(__0))return;
                if(GameManager.gameState==GameState.SinglePlayer&&latest!=null&&Decode(__0,latest.Value,out var plan,out _)&&
                    GameManager.aircraftCustomization.TryGetValue(__0.definition,out var saved)&&Matches(saved.loadout,plan))Bind(__0,plan);
                AircraftPlans.TryGetValue(__0,out var active);Prepare(__0.weaponManager,active);
                if(active!=null)UpdateWeapons(__instance,true);
            }
        }
        [HarmonyPatch]
        private static class ShowBaseSelectors
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(typeof(LoadoutSelector),"ShowHardpoints");
            private static void Prefix(Aircraft ___aircraft,out HardpointSet[] __state)
            {
                __state=null;if(!IsBg(___aircraft))return;
                __state=___aircraft.weaponManager.hardpointSets;
                ___aircraft.weaponManager.hardpointSets=State(___aircraft.weaponManager).Base;
            }
            private static Exception Finalizer(Exception __exception,Aircraft ___aircraft,HardpointSet[] __state)
            {if(__state!=null&&___aircraft!=null)___aircraft.weaponManager.hardpointSets=__state;return __exception;}
        }
        [HarmonyPatch(typeof(LoadoutSelector),nameof(LoadoutSelector.GenerateLoadoutFromDropdowns))]
        private static class GenerateLoadout
        {
            private static void Postfix(Aircraft ___aircraft,ref Loadout __result)
            {if(IsBg(___aircraft)&&AircraftPlans.TryGetValue(___aircraft,out var plan))__result=BuildLoadout(__result.weapons,plan);}
        }
        [HarmonyPatch(typeof(LoadoutSelector),nameof(LoadoutSelector.LoadDefaults))]
        private static class PrepareDefaults
        {
            [HarmonyPriority(Priority.First+200)]
            private static void Prefix(Aircraft ___aircraft)
            {
                if(!IsBg(___aircraft))return;
                State(___aircraft.weaponManager);
                if(!GameManager.aircraftCustomization.TryGetValue(___aircraft.definition,out var custom)||custom.loadout==null)return;
                if(LoadoutPlans.TryGetValue(custom.loadout,out var plan))Bind(___aircraft,plan);
                else if(GameManager.gameState==GameState.SinglePlayer&&latest!=null&&Decode(___aircraft,latest.Value,out plan,out _)&&Matches(custom.loadout,plan))
                {Mark(custom.loadout,plan);Bind(___aircraft,plan);}
                AircraftPlans.TryGetValue(___aircraft,out var active);Prepare(___aircraft.weaponManager,active);
            }
        }
        [HarmonyPatch(typeof(LoadoutSelector),nameof(LoadoutSelector.LoadDefaults))]
        private static class RestoreDefaults
        {
            [HarmonyPriority(Priority.Last-100)]
            private static void Prefix(Aircraft ___aircraft,out DefaultsState __state)
            {
                __state=null;if(!IsBg(___aircraft)||!GameManager.aircraftCustomization.TryGetValue(___aircraft.definition,out var custom)||custom.loadout==null)return;
                // Native LoadDefaults indexes one dropdown for every element
                // in its customization loadout. Only the nine legacy controls
                // exist; the organizer restores its physical mounts via the
                // GenerateLoadout hook while this loop sees those nine only.
                if(custom.loadout.weapons.Count>LegacySetCount)
                {
                    __state=new DefaultsState{Custom=custom,Original=custom.loadout};
                    custom.loadout=BuildLoadout(custom.loadout.weapons,null);
                }
            }
            private static Exception Finalizer(Exception __exception,DefaultsState __state)
            {if(__state!=null)__state.Custom.loadout=__state.Original;return __exception;}
        }
        [HarmonyPatch(typeof(SavedLoadout),nameof(SavedLoadout.CreateLoadout),new[]{typeof(WeaponManager)})]
        private static class SavedPlanPatch
        {
            [HarmonyPriority(Priority.First+200)]
            private static void Prefix(SavedLoadout __instance,WeaponManager __0,out SavedState __state)
            {
                __state=null;var owner=Owner(__0);if(!IsBg(owner))return;
                var state=new SavedState{Original=__instance.Selected};
                string metadata=__instance.Selected?.LastOrDefault(m=>m.Key?.StartsWith(SavedPrefix,StringComparison.Ordinal)==true).Key;
                string reason=null;
                if(metadata!=null&&Decode(owner,metadata,out var plan,out reason))state.Plan=plan;
                else if(metadata!=null)log?.LogWarning("BG spatial saved plan held: "+reason);
                Prepare(__0,state.Plan);
                if(metadata!=null){__state=state;__instance.Selected=BuildLoadout(state.Original.Where(m=>m.Key?.StartsWith(SavedPrefix,StringComparison.Ordinal)!=true)
                    .Take(LegacySetCount).Select(m=>Encyclopedia.WeaponLookup.TryGetValue(m.Key??"",out var value)?value:null),state.Plan).weapons
                    .Select(m=>new SavedLoadout.SelectedMount{Key=m?.jsonKey}).ToList();}
            }
            private static Exception Finalizer(Exception __exception,SavedLoadout __instance,Loadout __result,SavedState __state)
            {
                if(__state==null)return __exception;
                if(__exception==null&&__result!=null&&__state.Plan!=null)Mark(__result,__state.Plan);
                __instance.Selected=__state.Original;return __exception;
            }
        }
        [HarmonyPatch(typeof(WeaponChecker),nameof(WeaponChecker.VetLoadout))]
        private static class PrepareNativeValidation
        {
            private static void Prefix(AircraftDefinition __0,Loadout __1)
            {
                if(__0?.jsonKey!="jp_bg280_bomber"||__0.unitPrefab==null)return;
                var owner=__0.unitPrefab.GetComponent<Aircraft>();Layout plan=null;if(__1!=null)LoadoutPlans.TryGetValue(__1,out plan);Prepare(owner.weaponManager,plan);
            }
        }
        [HarmonyPatch(typeof(Spawner),nameof(Spawner.RequestSpawnAtAirbase))]
        private static class CaptureRequest
        {
            private static void Prefix(Airbase __0,AircraftDefinition __1,Loadout __3)
            {
                if(GameManager.gameState!=GameState.SinglePlayer||__1?.jsonKey!="jp_bg280_bomber"||__3==null||
                    !LoadoutPlans.TryGetValue(__3,out var plan)||!GameManager.GetLocalPlayer(out Player player))return;
                pending=new SpawnRequest{Airbase=__0,Player=player,Keys=__3.weapons.ToArray(),Plan=plan,Expires=Time.realtimeSinceStartup+30f};
            }
        }
        [HarmonyPatch(typeof(Spawner),"TrySpawnAircraft",new[]{typeof(Airbase),typeof(AircraftDefinition),typeof(LiveryKey),typeof(Loadout),typeof(float),typeof(INetworkPlayer)})]
        private static class ReceiveRequest
        {
            private static void Prefix(Airbase __0,AircraftDefinition __1,Loadout __3,INetworkPlayer __5)
            {
                var request=pending;
                if(request==null||GameManager.gameState!=GameState.SinglePlayer||__1?.jsonKey!="jp_bg280_bomber"||request.Airbase!=__0||
                    Time.realtimeSinceStartup>request.Expires||!PlayerHelper.TryGetPlayer(__5,out Player player)||player!=request.Player||
                    __3?.weapons==null||!request.Keys.SequenceEqual(__3.weapons))return;
                Mark(__3,request.Plan);pending=null;
            }
        }
        [HarmonyPatch(typeof(WeaponManager),nameof(WeaponManager.SpawnWeapons))]
        private static class SpawnWeapons
        {
            [HarmonyPriority(Priority.First+200)]
            private static void Prefix(WeaponManager __instance,Aircraft ___aircraft)
            {
                if(!IsBg(___aircraft))return;
                Layout plan=null;
                if(___aircraft.loadout!=null)LoadoutPlans.TryGetValue(___aircraft.loadout,out plan);
                if(plan==null&&AircraftPlans.TryGetValue(___aircraft,out var previous)&&Matches(___aircraft.loadout,previous))plan=previous;
                Bind(___aircraft,plan);Prepare(__instance,plan);
            }
            [HarmonyPriority(Priority.Last-100)]
            private static void Postfix(Aircraft ___aircraft)
            {if(IsBg(___aircraft)&&AircraftPlans.TryGetValue(___aircraft,out var plan))BindSpawned(___aircraft,plan);}
        }
        [HarmonyPatch(typeof(WeaponManager),nameof(WeaponManager.InitializeWeaponManager))]
        private static class InitializeWeapons
        {
            [HarmonyPriority(Priority.First+200)]
            private static void Prefix(WeaponManager __instance)
            {
                var owner=Owner(__instance);
                if(GameManager.gameState!=GameState.SinglePlayer||!IsBg(owner)||owner.loadout==null||
                    !LoadoutPlans.TryGetValue(owner.loadout,out var plan))return;
                // Direct native SpawnAircraft (including saved/AI entrypoints)
                // does not pass the airbase VetLoadout template preparation.
                // Native Initialize compares lengths before SpawnWeapons and
                // would replace this known11+/N layout with default9. Prepare
                // the actual clone before that guard; never infer a plan from
                // matching keys or mutate unknown/remote native loadouts.
                Bind(owner,plan);Prepare(__instance,plan);
            }
        }
    }
    internal sealed class SuperheavySpatialWeaponItem:MonoBehaviour
    {
        internal Aircraft Owner;internal int Index;internal WeaponPlacement Placement;internal WeaponMount Mount;internal Hardpoint Point;
    }
    internal sealed class SuperheavySpatialWeaponsStamp:MonoBehaviour
    {
        internal Aircraft Owner;private float next;
        private void Update()
        {if(Time.unscaledTime<next)return;next=Time.unscaledTime+.5f;if(Owner!=null&&SuperheavySpatialWeaponsRuntime.TryGetPlan(Owner,out var plan))SuperheavySpatialWeaponsRuntime.StampSavedUnit(Owner,plan);}
    }
}
