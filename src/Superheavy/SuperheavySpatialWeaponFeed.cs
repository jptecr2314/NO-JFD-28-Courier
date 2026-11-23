using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using B=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Box;
using P=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Point;

namespace Japensou.NuclearOption.Superheavy
{
    // The organizer owns real native mounts. This controller only translates
    // one retained MountedMissile at a time, then hands its final vertical
    // travel/projectile birth to the unmodified native LaunchMount/RailLaunch.
    // No reserve count, projectile prefab or virtual-ammo decrement exists here.
    public sealed class SuperheavySpatialWeaponFeed:MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<WeaponStation,int> WeaponIndex=AccessTools.FieldRefAccess<WeaponStation,int>("weaponIndex");
        private static readonly AccessTools.FieldRef<MountedMissile,BayDoor[]> RailDoors=AccessTools.FieldRefAccess<MountedMissile,BayDoor[]>("bayDoors");
        private static readonly AccessTools.FieldRef<MountedMissile,float> RailLength=AccessTools.FieldRefAccess<MountedMissile,float>("railLength");
        private static readonly AccessTools.FieldRef<MountedMissile,float> RailSpeed=AccessTools.FieldRefAccess<MountedMissile,float>("railSpeed");
        private static readonly AccessTools.FieldRef<MountedMissile,float> RailDelay=AccessTools.FieldRefAccess<MountedMissile,float>("railDelay");
        private static readonly System.Reflection.FieldInfo RailDirection=AccessTools.Field(typeof(MountedMissile),"railDirection");
        private static readonly AccessTools.FieldRef<BayDoor,float> DoorPhase=AccessTools.FieldRefAccess<BayDoor,float>("openAmount");
        private static ManualLogSource log;
        private Aircraft owner;
        private WeaponStation station;
        private MountedMissile moving;
        private Unit target;
        private GlobalPosition aimpoint;
        private SuperheavySpatialWeaponItem item;
        private WeaponRoute route;
        private WeaponPlanGeometry geometry;
        private BayDoor[] gates;
        private MeshFilter[] leaves;
        private B[] movingVolumes;
        private B[] retainedMeshBounds=Array.Empty<B>();
        private MeshFilter[] liveGunMeshes=Array.Empty<MeshFilter>();
        private Vector3 anchorInRail;
        private int waypoint;
        private bool launching,handedOff,faulted;
        private float deadline,acceptedAt;
        private string lastBlock;
        internal bool Busy=>moving!=null;
        internal static bool ManagesRail(Aircraft owner,MountedMissile rail)=>owner?.definition?.jsonKey=="jp_bg280_bomber"&&
            (SuperheavySpatialWeaponsRuntime.IsSpatialStore(rail)||rail?.GetComponentInParent<Transform>()?.GetComponentsInParent<Transform>(true)
                .Any(t=>t.name.StartsWith(SuperheavySpatialWeaponsRuntime.AnchorPrefix,StringComparison.Ordinal))==true);

        internal static void Install(Harmony harmony,ManualLogSource logger)
        {
            log=logger;harmony.CreateClassProcessor(typeof(LaunchPatch)).Patch();harmony.CreateClassProcessor(typeof(FireGuard)).Patch();
        }
        internal static MountedMissile[] Rails(SuperheavySpatialWeaponItem entry)
        {
            var specs=entry.Placement.Spec.Rounds??Array.Empty<WeaponRoundSpec>();var result=new MountedMissile[specs.Length];
            for(int n=0;n<specs.Length;n++)
            {
                var path=specs[n].NativeRailPath;
                var frame=string.IsNullOrEmpty(path)?entry.transform:entry.transform.Find(path);
                result[n]=frame==null?null:frame.GetComponent<MountedMissile>();
            }
            return result;
        }
        internal static void Configure(SuperheavySpatialWeaponItem entry)
        {
            if(entry.Placement.Spec.Kind==SpatialWeaponKind.Gun)return;
            var rails=Rails(entry);
            if(rails.Length!=entry.Placement.Spec.MountedRounds||rails.Any(r=>r==null))
                throw new InvalidOperationException("Native spatial rack paths do not match measured real rounds: "+entry.Mount.jsonKey);
            foreach(var rail in rails)
            {
                RailDelay(rail)=0f;RailSpeed(rail)=BomberFeedTiming.ContinuousRailSpeed;
                RailDirection.SetValue(rail,Enum.ToObject(RailDirection.FieldType,1)); // native Down, not a new projectile model.
            }
        }
        private static bool Simulated(Aircraft a)=>a!=null&&a.definition?.jsonKey=="jp_bg280_bomber"&&a.networked&&a.LocalSim&&GameManager.gameState==GameState.SinglePlayer;
        private static bool Retained(MountedMissile rail)=>rail!=null&&rail.gameObject.activeInHierarchy&&rail.IsAttached()&&rail.GetAmmoLoaded()==1;
        private void Block(string reason)
        {
            if(reason==lastBlock)return;lastBlock=reason;log?.LogWarning("BG spatial feeder: "+reason);
            if(owner!=null&&GameManager.IsLocalAircraft(owner))SceneSingleton<AircraftActionsReport>.i?.ReportText("Armament conveyor held: "+reason,3f);
        }
        private static WeaponItemSpec RetainedSpec(SuperheavySpatialWeaponItem e,MountedMissile[] rails)
        {
            var spec=e.Placement.Spec;
            if(spec.Kind==SpatialWeaponKind.Gun)return spec;
            var parts=new List<B>(spec.FixedVolumes??Array.Empty<B>());
            for(int n=0;n<spec.Rounds.Length;n++)if(Retained(rails[n]))parts.AddRange(spec.Rounds[n].Volumes);
            return new WeaponItemSpec{Id=spec.Id,Kind=spec.Kind,Width=spec.Width,Length=spec.Length,Height=spec.Height,StowedVolumes=parts.ToArray(),
                FixedVolumes=spec.FixedVolumes,AllowRotation90=spec.AllowRotation90,MassKg=spec.MassKg};
        }
        // A player may select weapon stations in any order. Put the requested
        // item first in this snapshot and retain ALL other actual stock; never
        // assume a lower organizer index has already left the aircraft.
        private bool SnapshotRoute(SuperheavySpatialWeaponItem chosen,B[] currentGuns,out WeaponRoute path,out MountedMissile rail)
        {
            path=null;rail=null;var selectedRails=Rails(chosen);
            if(selectedRails.Length!=chosen.Placement.Spec.Rounds.Length||selectedRails.Any(r=>r==null))return false;
            var released=new HashSet<int>(Enumerable.Range(0,selectedRails.Length).Where(n=>!Retained(selectedRails[n])));
            var plan=new List<WeaponPlacement>{chosen.Placement};
            foreach(var other in SuperheavyNativeDamage.Components<SuperheavySpatialWeaponItem>(owner,false).OrderBy(e=>e.Index))
            {
                if(other==chosen||other.Placement.Spec.Kind==SpatialWeaponKind.Gun)continue;
                var otherRails=Rails(other);var spec=RetainedSpec(other,otherRails);
                if(spec.StowedVolumes.Length==0)continue;
                plan.Add(new WeaponPlacement(spec,other.Placement.X,other.Placement.Z,other.Placement.Y,other.Placement.Rotation90));
            }
            // Release uses CURRENT real gun positions, not the catalogue's
            // assumed deployed endpoint. A stowed/faulted unrelated cradle
            // cannot block an empty exit just because its future pose differs.
            var live=SuperheavySpatialWeaponsPlan.CloneGeometryForValidation(geometry,default(System.Threading.CancellationToken));
            live.StaticBlockers=geometry.StaticBlockers.Concat(currentGuns).ToArray();
            if(!SuperheavySpatialWeaponsPlan.TryNextRoundRoute(plan,0,released,live,out path,out var reason))
            {Block(reason);return false;}
            rail=selectedRails[path.RoundIndex];return Retained(rail);
        }
        private bool ReadGates()
        {
            gates=SuperheavyNativeDamage.Components<BayDoor>(owner,true).Where(g=>g.name=="SHGT_BombBayGate_Cell1"||g.name=="SHGT_BombBayGate_Cell2").ToArray();
            leaves=SuperheavyNativeDamage.Components<Transform>(owner).Where(t=>t.name=="SHGT_BombBayMotion_Cell1"||t.name=="SHGT_BombBayMotion_Cell2")
                .SelectMany(t=>t.GetComponentsInChildren<MeshFilter>(true)).Where(f=>f.sharedMesh!=null).ToArray();
            if(gates.Length<1||leaves.Length==0){Block("native continuous gates/folding leaf geometry are missing");return false;}
            return true;
        }
        private void Request(Aircraft aircraft,WeaponStation requested,Unit selected,GlobalPosition point)
        {
            if(Busy)return;
            var requestWatch=System.Diagnostics.Stopwatch.StartNew();
            owner=aircraft;if(!Simulated(owner)||owner.disabled||!requested.Ready()||requested.SafetyIsOn(owner))return;
            geometry=SuperheavySpatialWeaponsRuntime.GetGeometry(owner);if(geometry==null||!ReadGates())return;
            liveGunMeshes=SuperheavyNativeDamage.Components<Transform>(owner,false)
                .Where(t=>t.name==SuperheavyRetractableBattery.Marker||t.name==SuperheavyRetractableBattery.ShortMarker)
                .SelectMany(t=>(t.parent??t).GetComponentsInChildren<MeshFilter>(false)).Distinct().Where(f=>f.sharedMesh!=null).ToArray();
            var currentGuns=liveGunMeshes.Select(MeshBox).ToArray();
            foreach(var candidate in requested.Weapons.OfType<MountedMissile>().Where(Retained)
                .Select(w=>w.GetComponentInParent<SuperheavySpatialWeaponItem>()).Where(e=>e!=null).Distinct().OrderBy(e=>e.Index))
            {
                if(!SnapshotRoute(candidate,currentGuns,out var path,out var chosen))continue;
                if(path.Waypoints.Length<2){Block("measured physical route has no native ejection segment");return;}
                var start=path.Waypoints[path.Waypoints.Length-2];var end=path.Waypoints[path.Waypoints.Length-1];
                if(Math.Abs(start.X-end.X)>.0001f||Math.Abs(start.Z-end.Z)>.0001f||end.Y>=start.Y)
                {Block("the final native rail must be aligned vertically downward");return;}
                station=requested;target=selected;aimpoint=point;item=candidate;route=path;moving=chosen;waypoint=1;handedOff=faulted=false;
                anchorInRail=moving.transform.InverseTransformPoint(owner.transform.TransformPoint(V(path.Waypoints[0])));
                var round=item.Placement.Spec.Rounds[path.RoundIndex];
                movingVolumes=round.Volumes.Select(b=>RelativeRound(b,round.AnchorOffset,item.Placement.Rotation90)).ToArray();
                // All other rounds and parked cradles are stationary relative
                // to this aircraft for the duration of the serialized feed.
                // Cache their measured boxes once, not thousands of mesh-bound
                // transforms on every physics tick. Live folding doors remain
                // sampled each step. Rearm/reconfigure builds a new snapshot.
                var retainedMeshes=geometry.DirectDropOnly?Enumerable.Empty<MeshFilter>():SuperheavyNativeDamage.Components<SuperheavySpatialWeaponItem>(owner,false)
                    .SelectMany(e=>e.GetComponentsInChildren<MeshFilter>(false));
                retainedMeshBounds=retainedMeshes.Distinct()
                    .Where(f=>f.sharedMesh!=null&&!f.transform.IsChildOf(moving.transform)).Select(MeshBox).ToArray();
                if(!SuperheavySpatialWeaponsPlan.RouteClearOfLiveBoxes(path,currentGuns.Concat(retainedMeshBounds).ToArray(),geometry.Clearance))
                {
                    moving=null;Block("the selected round's route intersects a current gun/capsule position; other clear routes remain available");continue;
                }
                acceptedAt=Time.time;float length=0;for(int n=1;n<path.Waypoints.Length;n++)length+=path.Waypoints[n-1].Distance(path.Waypoints[n]);
                deadline=acceptedAt+length/BomberFeedTiming.ContinuousConveyorSpeed+20f;lastBlock=null;HoldGates();
                log?.LogInfo($"BG spatial feed accepted item={item.Index} round={route.RoundIndex} realRail={moving.name} segments={route.Waypoints.Length-1} length={length:F3}m; native ammo retained.");
                log?.LogInfo("BG release request CPU ms="+requestWatch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" direct="+geometry.DirectDropOnly);
                return;
            }
            Block("no retained round in this station has a clear physical route");
        }
        private static B RelativeRound(B b,P offset,bool yaw90)
        {
            b=new B(new P(b.Min.X-offset.X,b.Min.Y-offset.Y,b.Min.Z-offset.Z),new P(b.Max.X-offset.X,b.Max.Y-offset.Y,b.Max.Z-offset.Z));
            return yaw90?new B(new P(b.Min.Z,b.Min.Y,-b.Max.X),new P(b.Max.Z,b.Max.Y,-b.Min.X)):b;
        }
        private Vector3 Position=>owner.transform.InverseTransformPoint(moving.transform.TransformPoint(anchorInRail));
        private void HoldGates(){foreach(var g in gates)if(g!=null)g.OpenDoor(BomberFeedTiming.GateHoldSeconds);}
        private bool GatesClear=>gates.All(g=>g!=null&&DoorPhase(g)>=BomberFeedTiming.GatePhase);
        private static B Shift(B b,Vector3 p)=>new B(new P(b.Min.X+p.x,b.Min.Y+p.y,b.Min.Z+p.z),new P(b.Max.X+p.x,b.Max.Y+p.y,b.Max.Z+p.z));
        private B[] Sweeps(Vector3 a,Vector3 b)=>movingVolumes.Select(v=>SuperheavySpatialWeaponsPlan.Union(new[]{Shift(v,a),Shift(v,b)})).ToArray();
        private B MeshBox(MeshFilter f)
        {
            var b=f.sharedMesh.bounds;var points=new B[8];for(int n=0;n<8;n++)
            {
                var p=owner.transform.InverseTransformPoint(f.transform.TransformPoint(new Vector3((n&1)==0?b.min.x:b.max.x,(n&2)==0?b.min.y:b.max.y,(n&4)==0?b.min.z:b.max.z)));
                points[n]=new B(Pt(p),Pt(p));
            }
            return SuperheavySpatialWeaponsPlan.Union(points);
        }
        private bool SegmentClear(Vector3 from,Vector3 to,bool internalOnly)
        {
            var sweeps=Sweeps(from,to);
            foreach(var box in sweeps)
            {
                if(internalOnly&&!geometry.Envelope.Contains(box))return false;
                if(geometry.StaticBlockers.Any(b=>b.Intersects(box))||SuperheavySpatialWeaponsPlan.HitsWalls(box,geometry))return false;
            }
            foreach(var box in retainedMeshBounds)if(sweeps.Any(b=>SuperheavySpatialWeaponsPlan.Pad(b,geometry.Clearance).Intersects(box)))return false;
            foreach(var filter in liveGunMeshes)
                if(filter!=null&&filter.sharedMesh!=null&&filter.gameObject.activeInHierarchy&&
                    sweeps.Any(b=>SuperheavySpatialWeaponsPlan.Pad(b,geometry.Clearance).Intersects(MeshBox(filter))))return false;
            foreach(var filter in leaves)
            {
                if(filter==null||filter.sharedMesh==null||!filter.gameObject.activeInHierarchy||filter.transform.IsChildOf(moving.transform))continue;
                if(sweeps.Any(b=>SuperheavySpatialWeaponsPlan.Pad(b,geometry.Clearance).Intersects(MeshBox(filter))))return false;
            }
            return true;
        }
        private void FixedUpdate()
        {
            if(moving==null)return;
            if(!Simulated(owner)||owner.disabled){moving=null;return;}
            HoldGates();
            if(handedOff)
            {
                if(!moving.gameObject.activeInHierarchy)
                {log?.LogInfo($"BG spatial native release complete item={item.Index} round={route.RoundIndex} commandSeconds={Time.time-acceptedAt:F3}; one physical round removed.");moving=null;}
                return;
            }
            if(faulted)return;
            if(Time.time>deadline){faulted=true;Block("movement timed out; retained physical store is stopped until rearm/reconfiguration");return;}
            if(waypoint<route.Waypoints.Length-1)
            {
                var from=Position;var to=V(route.Waypoints[waypoint]);
                if(!GatesClear)return;
                if(!SegmentClear(from,to,true)){faulted=true;Block("a live door/store/gun obstructed the conveyor; retained round stopped");return;}
                var next=Vector3.MoveTowards(from,to,BomberFeedTiming.ContinuousConveyorSpeed*Time.fixedDeltaTime);
                moving.transform.position+=owner.transform.TransformVector(next-from);
                if((next-to).sqrMagnitude<.0000001f)waypoint++;
                return;
            }
            if(!GatesClear||!station.Ready()||station.SafetyIsOn(owner))return;
            var endpoint=V(route.Waypoints[route.Waypoints.Length-1]);
            if(!SegmentClear(Position,endpoint,false)){faulted=true;Block("the complete native downward rail is physically obstructed");return;}
            int index=station.Weapons.IndexOf(moving);
            if(index<0||!Retained(moving)){faulted=true;Block("physical selected round is no longer attached to its native station");return;}
            RailLength(moving)=Position.y-endpoint.y;RailSpeed(moving)=BomberFeedTiming.ContinuousRailSpeed;RailDelay(moving)=0;RailDoors(moving)=gates;
            WeaponIndex(station)=index;launching=true;try{station.LaunchMount(owner,target,aimpoint);}finally{launching=false;}
            if(moving.GetAmmoLoaded()!=0){faulted=true;Block("native LaunchMount rejected the staged round; ammo retained");return;}
            handedOff=true;log?.LogInfo($"BG spatial native handoff item={item.Index} round={route.RoundIndex} rail={RailLength(moving):F3}m; one native ammunition consumed.");
        }
        private static Vector3 V(P p)=>new Vector3(p.X,p.Y,p.Z);
        private static P Pt(Vector3 p)=>new P(p.x,p.y,p.z);
        [HarmonyPatch(typeof(WeaponStation),nameof(WeaponStation.LaunchMount))]
        private static class LaunchPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(WeaponStation __instance,Unit __0,Unit __1,GlobalPosition __2)
            {
                if(!(__0 is Aircraft a)||!Simulated(a)||!__instance.Weapons.OfType<MountedMissile>().Any(w=>SuperheavySpatialWeaponsRuntime.IsSpatialStore(w)))return true;
                var feed=a.GetComponent<SuperheavySpatialWeaponFeed>()??a.gameObject.AddComponent<SuperheavySpatialWeaponFeed>();
                if(feed.launching&&feed.station==__instance)return true;
                feed.Request(a,__instance,__1,__2);return false;
            }
        }
        [HarmonyPatch(typeof(MountedMissile),nameof(MountedMissile.Fire),new[]{typeof(Unit),typeof(Unit),typeof(Vector3),typeof(WeaponStation),typeof(GlobalPosition)})]
        private static class FireGuard
        {
            private static bool Prefix(MountedMissile __instance,Unit __0)
            {
                if(!SuperheavySpatialWeaponsRuntime.IsSpatialStore(__instance)||!(__0 is Aircraft a)||!Simulated(a))return true;
                var feed=a.GetComponent<SuperheavySpatialWeaponFeed>();return feed!=null&&feed.launching&&feed.moving==__instance;
            }
        }
    }
}
