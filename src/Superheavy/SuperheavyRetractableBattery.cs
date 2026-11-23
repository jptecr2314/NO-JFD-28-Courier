using System;
#if !RETRACTABLE_STATE_ONLY
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
#endif

namespace Japensou.NuclearOption.Superheavy
{
    // Pure sequencing model: the Unity adapter supplies observed endpoints,
    // native door phases and trusted flight/gear state. No timer substitutes
    // for an observed mechanical endpoint.
    internal sealed class RetractableBatterySequence
    {
        internal enum Stage { Stowed, Opening, Deploying, Armed, Parking, Retracting, Closing, Fault }
        internal struct Input
        {
            internal bool Valid, WantDeploy, DoorsOpen, DoorsClosed, Parked, AtStowed, AtDeployed, ForeignBayBusy;
        }
        internal Stage State { get; private set; } = Stage.Stowed;
        private float elapsed;
        private float timeout = 35f;
        internal void ConfigureTimeout(float seconds) { if(!float.IsNaN(seconds)&&!float.IsInfinity(seconds)&&seconds>0)timeout=seconds; }
        internal static float GroundClearance(float globalY,bool solidHit,float solidClearance) =>
            float.IsNaN(globalY)||float.IsInfinity(globalY)?float.NaN:(solidHit?Math.Min(globalY,solidClearance):globalY);
        // Native Gun disables its update one second after the last shot when
        // heat is disabled. A 5/s outbound and 1/s return stroke needs 1.2 s,
        // leaving recoil frozen before a cradle can safely retract. Keep the
        // authored stroke/outbound motion, but complete that return in <=.7 s.
        internal static float ReturnRateBeforeNativeIdle(float outbound,float returning,bool heatEnabled) =>
            !heatEnabled&&!float.IsNaN(outbound)&&!float.IsInfinity(outbound)&&outbound>=5f&&
            !float.IsNaN(returning)&&!float.IsInfinity(returning)&&returning>=0f ? Math.Max(2f,returning) : returning;
        internal bool FireAllowed(Input i) => State == Stage.Armed && i.Valid && i.WantDeploy && i.DoorsOpen && i.AtDeployed && !i.ForeignBayBusy;
        internal bool GearAllowed(Input i) => State == Stage.Fault || (i.AtStowed && i.DoorsClosed && !i.ForeignBayBusy &&
            (State == Stage.Stowed || State == Stage.Closing));
        internal bool HoldDoors => State == Stage.Opening || State == Stage.Deploying || State == Stage.Armed ||
            State == Stage.Parking || State == Stage.Retracting;
        internal void Recover(bool atStowed) { State = atStowed ? Stage.Closing : Stage.Parking; elapsed = 0; }
        private void Set(Stage next) { if (State != next) { State = next; elapsed = 0; } }
        internal void Step(Input i, float seconds)
        {
            if (!i.Valid) { Set(Stage.Fault); return; }
            if (State == Stage.Fault) return; // Repair/rebind is an explicit recovery, not an endless retry.
            if (!float.IsNaN(seconds) && !float.IsInfinity(seconds) && seconds > 0) elapsed += Math.Min(seconds, 1f);
            if (State != Stage.Stowed && State != Stage.Armed && elapsed > timeout) { Set(Stage.Fault); return; }
            switch (State)
            {
                case Stage.Stowed:
                    if (i.WantDeploy && !i.ForeignBayBusy) Set(Stage.Opening);
                    break;
                case Stage.Opening:
                    if (!i.WantDeploy) Set(i.AtStowed ? Stage.Closing : Stage.Parking);
                    else if (i.DoorsOpen && i.Parked && !i.ForeignBayBusy) Set(Stage.Deploying);
                    break;
                case Stage.Deploying:
                    if (!i.WantDeploy || !i.DoorsOpen || i.ForeignBayBusy) Set(Stage.Parking);
                    else if (i.AtDeployed) Set(Stage.Armed);
                    break;
                case Stage.Armed:
                    if (!i.WantDeploy || !i.DoorsOpen || !i.AtDeployed || i.ForeignBayBusy) Set(Stage.Parking);
                    break;
                case Stage.Parking:
                    if (i.Parked && !i.ForeignBayBusy) Set(i.AtStowed ? Stage.Closing : Stage.Retracting);
                    break;
                case Stage.Retracting:
                    if (!i.DoorsOpen) Set(Stage.Parking);
                    else if (i.AtStowed) Set(Stage.Closing);
                    break;
                case Stage.Closing:
                    if (!i.AtStowed) Set(Stage.Parking);
                    else if (i.DoorsClosed && !i.ForeignBayBusy) Set(Stage.Stowed);
                    break;
            }
        }
    }

#if !RETRACTABLE_STATE_ONLY
    // Opt-in only. Existing .34 assets have none of these markers and retain
    // their current behavior. Root integration calls Install; authoring must
    // separately prove its physical poses, envelopes and magazine routing.
    // A two-pose inverted-base module is a separate explicit marker; no old
    // long-cradle geometry is shortened, rotated or silently enabled here.
    internal sealed class SuperheavyRetractableBattery : MonoBehaviour
    {
        internal const string Marker = "SHGT_RetractableBatteryModule";
        internal const string ShortMarker = "SHGT_RetractableBatteryShortModule";
        private const string StorePrefix = "BG280_Store_L";
        private static readonly string[] PoseNames = { "SHGT_BatteryStowedPose", "SHGT_BatteryDoorAlignedPose",
            "SHGT_BatteryClearancePose", "SHGT_BatteryDeployedPose" };
        private static readonly AccessTools.FieldRef<BayDoor,float> DoorPhase = AccessTools.FieldRefAccess<BayDoor,float>("openAmount");
        private static readonly AccessTools.FieldRef<Turret,Transform> ElevationFrame = AccessTools.FieldRefAccess<Turret,Transform>("elevationTransform");
        private static readonly AccessTools.FieldRef<Gun,float> Recoil = AccessTools.FieldRefAccess<Gun,float>("recoilPosition");
        private static readonly AccessTools.FieldRef<Gun,float> RecoilOutRate = AccessTools.FieldRefAccess<Gun,float>("recoilRate");
        private static readonly AccessTools.FieldRef<Gun,float> RecoilReturnRate = AccessTools.FieldRefAccess<Gun,float>("recoilReturnRate");
        private static readonly AccessTools.FieldRef<Gun,bool> RecoilHeat = AccessTools.FieldRefAccess<Gun,bool>("heatEnabled");
        private static readonly AccessTools.FieldRef<Turret,float> TraverseRate = AccessTools.FieldRefAccess<Turret,float>("traverseRate");
        private static readonly AccessTools.FieldRef<Turret,float> ElevationRate = AccessTools.FieldRefAccess<Turret,float>("elevationRate");
        private static readonly AccessTools.FieldRef<BayDoor,float> OpenRate = AccessTools.FieldRefAccess<BayDoor,float>("openSpeed");
        private static readonly AccessTools.FieldRef<BayDoor,float> CloseRate = AccessTools.FieldRefAccess<BayDoor,float>("closeSpeed");
        private static readonly AccessTools.FieldRef<WeaponManager,WeaponStation> ActiveStation =
            AccessTools.FieldRefAccess<WeaponManager,WeaponStation>("currentWeaponStation");
        private static ManualLogSource log;
        private readonly RetractableBatterySequence sequence = new RetractableBatterySequence();
        private readonly List<Module> modules = new List<Module>();
        private Aircraft owner;
        private BayDoor[] gates;
        private bool valid, gearRequested, releasingGear, replayingBomb, bombingHold;
        private PendingBomb pendingBomb;
        private float nextClearance, nextNotice;
        private bool clearanceReady;
        private string invalidReason;
        private sealed class PendingBomb { internal WeaponStation Station; internal Unit Target; internal GlobalPosition Point; }
        private sealed class Module
        {
            internal Transform Marker, Frame, Envelope;
            internal Transform[] Poses;
            internal Gun[] Guns;
            internal Turret[] Turrets;
            internal float Progress;
            internal int Order;
            internal bool Short;
            internal LiftLink[] LiftLinks = Array.Empty<LiftLink>();
            internal float End => Poses.Length-1;
            internal bool Matches(float progress)
            {
                if(Frame==null||Poses.Any(p=>p==null))return false;
                int segment=Mathf.Min(Poses.Length-2,Mathf.FloorToInt(progress));float t=progress-segment;
                return Vector3.Distance(Frame.localPosition,Vector3.Lerp(Poses[segment].localPosition,Poses[segment+1].localPosition,t))<.002f &&
                    Quaternion.Angle(Frame.localRotation,Quaternion.Slerp(Poses[segment].localRotation,Poses[segment+1].localRotation,t))<.25f &&
                    Vector3.Distance(Frame.localScale,Vector3.Lerp(Poses[segment].localScale,Poses[segment+1].localScale,t))<.001f;
            }
            internal bool AtStowed => Progress <= .0001f && Matches(0);
            internal bool AtDeployed => Progress >= End-.0001f && Matches(End);
        }
        private sealed class LiftLink
        {
            internal Transform Fixed, Moving, Rod;
        }

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(MountPatch)).Patch();
            harmony.CreateClassProcessor(typeof(PhysicsPatch)).Patch();
            harmony.CreateClassProcessor(typeof(FirePatch)).Patch();
            harmony.CreateClassProcessor(typeof(AimVectorPatch)).Patch();
            harmony.CreateClassProcessor(typeof(AimStationPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GearPatch)).Patch();
            harmony.CreateClassProcessor(typeof(GearStatePatch)).Patch();
            harmony.CreateClassProcessor(typeof(BombPatch)).Patch();
            harmony.CreateClassProcessor(typeof(RepairPatch)).Patch();
        }
        private static bool IsBg(Aircraft aircraft) => aircraft != null && aircraft.definition?.jsonKey == "jp_bg280_bomber";
        private static bool Simulated(Aircraft aircraft) => IsBg(aircraft) && aircraft.networked && aircraft.LocalSim &&
            GameManager.gameState == GameState.SinglePlayer;
        private static bool IsStore(WeaponStation station) => station != null && station.Weapons != null &&
            station.Weapons.OfType<MountedMissile>().Any(w => w != null && (w.name.StartsWith(StorePrefix,StringComparison.Ordinal)||SuperheavySpatialWeaponsRuntime.IsSpatialStore(w)));
        private static bool IsGunStation(WeaponStation station) => station != null && station.Weapons != null &&
            station.Weapons.OfType<Gun>().Any(g => g != null && g.info != null && g.info.name.StartsWith("BG280G_",StringComparison.Ordinal));
        private static void Attach(Aircraft aircraft)
        {
            if (!Simulated(aircraft)) return;
            var markers = SuperheavyNativeDamage.Components<Transform>(aircraft,false).Where(IsMarker).ToArray();
            if (markers.Length == 0) return;
            var controller = aircraft.GetComponent<SuperheavyRetractableBattery>() ?? aircraft.gameObject.AddComponent<SuperheavyRetractableBattery>();
            controller.owner = aircraft;
            controller.Bind(markers);
        }
        internal static void Refresh(Aircraft aircraft)=>Attach(aircraft);
        internal static bool CanFire(Gun gun)
        {
            var aircraft=gun==null?null:gun.attachedUnit as Aircraft;
            return aircraft!=null&&aircraft.TryGetComponent<SuperheavyRetractableBattery>(out var controller)&&controller.GunMayFire(gun);
        }
        private void Bind(Transform[] markers)
        {
            valid = false;
            var existing = modules.ToDictionary(m => m.Marker);
            modules.Clear();
            var required = new HashSet<string>();
            foreach (var marker in markers)
            {
                if (existing.TryGetValue(marker,out var previous)) { modules.Add(previous); }
                else
                {
                    var frame = marker.Find("SHGT_BatteryDeployFrame");
                    // A fixed-orientation inverted base may use two authored
                    // poses. Stroke is measured, not limited to an invented
                    // travel distance. Rotating paths need all four waypoints.
                    bool shortRig=marker.name==ShortMarker;
                    var poses = (shortRig?new[]{PoseNames[0],PoseNames[3]}:PoseNames).Select(marker.Find).ToArray();
                    var envelope = marker.Find("SHGT_BatterySweepEnvelope");
                    var orders=marker.Cast<Transform>().Where(t=>t.name.StartsWith("SHGT_BatteryOrder_",StringComparison.Ordinal)).ToArray();
                    int order=-1;
                    if(orders.Length!=1||!int.TryParse(orders[0].name.Substring("SHGT_BatteryOrder_".Length),out order)||order<0)
                    { Fail("each cradle requires one nonnegative authored deployment-order marker"); return; }
                    if (frame == null || envelope == null || poses.Any(p => p == null) ||
                        frame.parent != marker || envelope.parent != marker || poses.Any(p => p.parent != marker))
                    { Fail("missing or non-sibling authored deploy path/envelope"); return; }
                    if (!Finite(envelope.localPosition) || !Finite(envelope.localRotation) || !Finite(envelope.localScale) || envelope.localScale.x <= 0 || envelope.localScale.y <= 0 || envelope.localScale.z <= 0 ||
                        poses.Any(p => !Finite(p.localPosition) || !Finite(p.localRotation) || !Finite(p.localScale) || p.localScale.x <= 0 || p.localScale.y <= 0 || p.localScale.z <= 0))
                    { Fail("invalid authored path or swept-envelope units"); return; }
                    var guns = frame.GetComponentsInChildren<Gun>(true);
                    var turrets = frame.GetComponentsInChildren<Turret>(true);
                    if (guns.Length == 0 || turrets.Length != guns.Length || guns.Any(g => g.info == null || !g.info.name.StartsWith("BG280G_",StringComparison.Ordinal)))
                    { Fail("deploy frame must contain every real BG gun and its own turret"); return; }
                    if(shortRig && Quaternion.Angle(poses[0].rotation,poses[1].rotation)>.25f)
                    { Fail("two-pose inverted base requires fixed parent orientation; a changing orientation needs an authored multi-phase path");return; }
                    var module = new Module { Marker=marker, Frame=frame, Envelope=envelope, Poses=poses, Guns=guns, Turrets=turrets,Order=order,Short=shortRig };
                    var links = new List<LiftLink>();
                    for (int link = 0; link < 2; link++)
                    {
                        var fixedAnchor = marker.parent.Find("SHGT_LiftFixedAnchor_" + link);
                        var movingAnchor = frame.Find("SHGT_LiftMovingAnchor_" + link);
                        var rod = marker.parent.Find("SHGT_LiftRod_" + link);
                        if (fixedAnchor == null && movingAnchor == null && rod == null) continue;
                        if (fixedAnchor == null || movingAnchor == null || rod == null)
                        { Fail("incomplete fixed/moving lift-anchor assembly"); return; }
                        links.Add(new LiftLink { Fixed = fixedAnchor, Moving = movingAnchor, Rod = rod });
                    }
                    module.LiftLinks = links.ToArray();
                    modules.Add(module);
                    // Newly spawned authored rigs start stowed. Existing rigs
                    // keep progress on rebind; repair never teleports a cradle.
                    Pose(module,0f);
                }
                for (int cell=1;cell<=2;cell++)
                    if (marker.Find("SHGT_BatteryGate_Cell"+cell) != null) required.Add("SHGT_BombBayGate_Cell"+cell);
            }
            if (required.Count == 0) { Fail("no native bay gate references authored for the gun cradle"); return; }
            if(SuperheavyNativeDamage.Components<Transform>(owner).Any(t=>t.name=="SHGT_DirectDropBay")){required.Clear();required.Add("SHGT_BombBayGate_Cell1");}
            gates = SuperheavyNativeDamage.Components<BayDoor>(owner,true).Where(g => required.Contains(g.name)).ToArray();
            if (gates.Length != required.Count) { Fail("authored gun cradle references missing native bay gates"); return; }
            foreach(var gun in modules.SelectMany(m=>m.Guns).Where(g=>g!=null&&g.info!=null&&g.info.name.StartsWith("BG280G_",StringComparison.Ordinal)).Distinct())
            {
                float outbound=RecoilOutRate(gun),before=RecoilReturnRate(gun);
                float after=RetractableBatterySequence.ReturnRateBeforeNativeIdle(outbound,before,RecoilHeat(gun));
                if(after<=before)continue;
                RecoilReturnRate(gun)=after;
                log?.LogInfo($"BG native recoil return bound: gun={gun.GetInstanceID()}, info={gun.info.name}, outRate={outbound:F3}, returnRate={before:F3}->{after:F3}, cycle={(1f/outbound+1f/Mathf.Max(.0001f,before)):F3}->{(1f/outbound+1f/after):F3}s; native idle=1s, amplitude/outbound/fire guards unchanged.");
            }
            // Authored order belongs to a reusable prefab, not to the final
            // aircraft. Two copies of that prefab necessarily repeat it. Give
            // each real marker a dense instance order; keep the stable native
            // hierarchy as the tie breaker, including legacy paired loadouts.
            var instanceOrders=SuperheavySpatialWeaponsPlan.InstanceDeploymentOrders(modules.Select(m=>m.Order).ToArray(),modules.Select(m=>HierarchyKey(m.Marker)).ToArray());
            for(int i=0;i<modules.Count;i++)modules[i].Order=instanceOrders[i];
            if(gates.Any(g=>OpenRate(g)<=0||CloseRate(g)<=0)){Fail("native bay opening/closing rate is invalid");return;}
            float travelBudget=modules.Sum(m=>Enumerable.Range(0,m.Poses.Length-1).Sum(segment=>SegmentDuration(m,segment)));
            float parkBudget=modules.SelectMany(m=>m.Turrets).Max(t=>360f/Mathf.Max(.1f,TraverseRate(t))+180f/Mathf.Max(.1f,ElevationRate(t)));
            sequence.ConfigureTimeout(travelBudget+parkBudget+gates.Max(g=>1f/OpenRate(g)+1f/CloseRate(g))+5f);
            valid = true; invalidReason = null;
            sequence.Recover(modules.All(m => m.AtStowed));
            Park(true);
            log?.LogInfo("BG retractable battery rig bound: "+modules.Count+" authored modules; no legacy mounts changed.");
        }
        private static bool Finite(Vector3 v) => !(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)||
            float.IsInfinity(v.x)||float.IsInfinity(v.y)||float.IsInfinity(v.z));
        private static bool Finite(Quaternion q) => Finite(new Vector3(q.x,q.y,q.z)) && !float.IsNaN(q.w) && !float.IsInfinity(q.w) &&
            (double)q.x*q.x+(double)q.y*q.y+(double)q.z*q.z+(double)q.w*q.w > .5d;
        private static string HierarchyKey(Transform marker)
        {
            var parts=new Stack<string>();
            for(var t=marker;t!=null;t=t.parent)parts.Push(t.GetSiblingIndex().ToString("D6",System.Globalization.CultureInfo.InvariantCulture));
            return string.Join("/",parts);
        }
        private void Fail(string reason)
        {
            valid = false;
            if (invalidReason != reason) log?.LogError("BG retractable battery inhibited: "+reason+". Fire remains blocked; gear is not trapped.");
            invalidReason = reason;
        }
        private bool ForeignBusy => owner != null && ((owner.TryGetComponent<SuperheavyBomberFeed>(out var feed)&&feed.Busy)||
            (owner.TryGetComponent<SuperheavySpatialWeaponFeed>(out var spatial)&&spatial.Busy));
        private bool RigAlive => valid && owner != null && !owner.disabled && modules.Count > 0 &&
            modules.All(m => m.Marker != null && m.Frame != null && m.Envelope != null && m.Matches(m.Progress) && m.Guns.All(g => g != null &&
                g.GetComponentInParent<UnitPart>() is UnitPart p && p.parentUnit == owner && !p.IsDetached()) && m.Turrets.All(t => t != null)) &&
            gates != null && gates.All(g => g != null);
        private bool AtStowed => modules.Count > 0 && modules.All(m => m.AtStowed);
        private bool AtDeployed => modules.Count > 0 && modules.All(m => m.AtDeployed);
        // Native StowForward finishes with zero physical rotations but leaves
        // its cached angles below1degree. Observe the actual guard pose.
        private bool Parked => modules.All(m => m.Turrets.All(t => t == null || (ElevationFrame(t) != null &&
            Quaternion.Angle(t.transform.localRotation,Quaternion.identity) < .25f &&
            Quaternion.Angle(ElevationFrame(t).localRotation,Quaternion.identity) < .25f)) &&
            m.Guns.All(g => g == null || Mathf.Abs(Recoil(g)) < .005f));
        private bool DoorsOpen => gates != null && gates.Length > 0 && gates.All(g => g != null && DoorPhase(g) >= .995f);
        private bool DoorsClosed => gates != null && gates.Length > 0 && gates.All(g => g != null && DoorPhase(g) <= .005f);
        private RetractableBatterySequence.Input Observe()
        {
            var active = owner != null && owner.weaponManager != null ? ActiveStation(owner.weaponManager) : null;
            if (IsStore(active)) bombingHold = true;
            else if (IsGunStation(active) && pendingBomb == null && !ForeignBusy) bombingHold = false;
            bool flight = Simulated(owner) && owner.gearState == LandingGear.GearState.LockedRetracted &&
                SuperheavyGroundPhysicsPlugin.HasAirborneThrust(owner) && clearanceReady;
            return new RetractableBatterySequence.Input { Valid=RigAlive, WantDeploy=flight && !gearRequested && !bombingHold && pendingBomb==null && !ForeignBusy,
                DoorsOpen=DoorsOpen, DoorsClosed=DoorsClosed, Parked=Parked, AtStowed=AtStowed, AtDeployed=AtDeployed, ForeignBayBusy=ForeignBusy };
        }
        private void FixedUpdate()
        {
            if (!Simulated(owner)) { Park(true); return; }
            if (Time.fixedTime >= nextClearance)
            {
                nextClearance = Time.fixedTime + .12f;
                clearanceReady = CheckSweepGroundClearance();
            }
            var input = Observe(); sequence.Step(input,Time.fixedDeltaTime);
            bool armed = sequence.FireAllowed(input);
            Park(!armed);
            if ((sequence.HoldDoors || (sequence.State==RetractableBatterySequence.Stage.Fault&&!AtStowed)) && !ForeignBusy)
                foreach (var gate in gates ?? Array.Empty<BayDoor>()) if (gate != null) gate.OpenDoor(.5f);
            if (sequence.State == RetractableBatterySequence.Stage.Deploying)
            { var module=modules.OrderBy(m=>m.Order).FirstOrDefault(m=>!m.AtDeployed);if(module!=null)Move(module,true,Time.fixedDeltaTime); }
            if (sequence.State == RetractableBatterySequence.Stage.Retracting && input.Parked && input.DoorsOpen)
            { var module=modules.OrderByDescending(m=>m.Order).FirstOrDefault(m=>!m.AtStowed);if(module!=null)Move(module,false,Time.fixedDeltaTime); }
            if ((sequence.State == RetractableBatterySequence.Stage.Closing || sequence.State == RetractableBatterySequence.Stage.Stowed) && AtStowed && !ForeignBusy)
                foreach(var gate in gates ?? Array.Empty<BayDoor>()) if (gate != null) gate.OpenDoor(0f);
            input = Observe();
            if (gearRequested && sequence.GearAllowed(input))
            {
                gearRequested=false; releasingGear=true;
                try { owner.SetGear(true); } finally { releasingGear=false; }
            }
            if (pendingBomb != null && !gearRequested && AtStowed && DoorsClosed && !ForeignBusy && sequence.State != RetractableBatterySequence.Stage.Fault)
            {
                var command=pendingBomb;pendingBomb=null;
                if (owner.weaponStations != null && owner.weaponStations.Contains(command.Station) && IsStore(command.Station))
                {
                    replayingBomb=true;
                    try { command.Station.LaunchMount(owner,command.Target,command.Point); } finally { replayingBomb=false; }
                }
            }
        }
        private bool CheckSweepGroundClearance()
        {
            if (!RigAlive) return false;
            int mask=(int)PhysicsLayers.StaticsMask|(int)PhysicsLayers.ShipsMask;
            foreach(var module in modules)
            for(int corner=0;corner<9;corner++)
            {
                Vector3 local=corner==8?Vector3.zero:new Vector3((corner&1)==0?-.5f:.5f,(corner&2)==0?-.5f:.5f,(corner&4)==0?-.5f:.5f);
                var point=module.Envelope.TransformPoint(local);
                // The authored entire swept box, in its actual flight attitude,
                // must clear real terrain/decks. No fixed airspeed or altitude.
                bool solid=Physics.Raycast(point+Vector3.up*8f,Vector3.down,out var hit,10008f,mask,QueryTriggerInteraction.Ignore);
                // Native WaterJob_Math computes submergence from GlobalY=0.
                // An ocean surface need not expose a physics collider; a seabed
                // hit must never replace the higher sea surface either.
                float clearance=RetractableBatterySequence.GroundClearance(point.ToGlobalPosition().y,solid,solid?hit.distance-8f:0f);
                if(float.IsNaN(clearance)||clearance<1f)return false;
            }
            return true;
        }
        private void Park(bool stow)
        {
            foreach(var module in modules)
            {
                foreach(var gun in module.Guns) if (gun!=null && stow) gun.Safety=true;
                foreach(var turret in module.Turrets) if(turret!=null) turret.SetStowed(stow);
            }
        }
        private static void Pose(Module m,float progress)
        {
            m.Progress=progress;int segment=Mathf.Min(m.Poses.Length-2,Mathf.FloorToInt(progress));float blend=progress-segment;
            m.Frame.localPosition=Vector3.Lerp(m.Poses[segment].localPosition,m.Poses[segment+1].localPosition,blend);
            m.Frame.localRotation=Quaternion.Slerp(m.Poses[segment].localRotation,m.Poses[segment+1].localRotation,blend);
            m.Frame.localScale=Vector3.Lerp(m.Poses[segment].localScale,m.Poses[segment+1].localScale,blend);
            foreach (var link in m.LiftLinks)
            {
                if (link.Fixed == null || link.Moving == null || link.Rod == null) continue;
                var parent = link.Rod.parent;
                var from = parent.InverseTransformPoint(link.Fixed.position);
                var to = parent.InverseTransformPoint(link.Moving.position);
                var delta = to - from; float length = delta.magnitude;
                if (length < .0001f) continue;
                link.Rod.localPosition = (from + to) * .5f;
                link.Rod.localRotation = Quaternion.FromToRotation(Vector3.up, delta / length);
                var scale = link.Rod.localScale; scale.y = length; link.Rod.localScale = scale;
            }
        }
        private static void Move(Module m,bool deploy,float seconds)
        {
            int segment=deploy?Mathf.Min(m.Poses.Length-2,Mathf.FloorToInt(m.Progress)):Mathf.Max(0,Mathf.CeilToInt(m.Progress)-1);
            float duration=SegmentDuration(m,segment);
            Pose(m,Mathf.MoveTowards(m.Progress,deploy?segment+1:segment,seconds/duration));
        }
        private static float SegmentDuration(Module m,int segment)
        {
            var a=m.Poses[segment];var b=m.Poses[segment+1];
            return Mathf.Max(.1f,Mathf.Max(Vector3.Distance(a.position,b.position)/(m.Short?1.5f:.8f),
                Mathf.Max(Quaternion.Angle(a.localRotation,b.localRotation)/15f,Vector3.Distance(a.localScale,b.localScale)/.5f)));
        }
        private bool RequestGear()
        {
            var input=Observe();
            if (!valid || sequence.GearAllowed(input)) return true;
            gearRequested=true; pendingBomb=null;
            Park(true);Notice("Recolhendo berços dos canhões antes de baixar o trem");return false;
        }
        private bool RequestBomb(WeaponStation station,Unit target,GlobalPosition point)
        {
            if (replayingBomb) return true;
            bombingHold=true;
            if (!RigAlive || sequence.State==RetractableBatterySequence.Stage.Fault)
            {
                // A failed gun is not a failure of every missile on board.
                // SpatialWeaponFeed proves the selected real round's route
                // against CURRENT gun/capsule meshes before the native launch.
                // Legacy feeders have no such live proof and stay inhibited.
                if(SuperheavySpatialWeaponsRuntime.IsSpatialStation(station))return true;
                Notice("Berço indisponível: saída deste armamento não comprovada");return false;
            }
            if (AtStowed && !gearRequested) return true; // Native feeder owns its gate phase from here.
            if (!gearRequested && pendingBomb==null) pendingBomb=new PendingBomb { Station=station,Target=target,Point=point };
            Park(true);Notice("Recolhendo canhões para liberar a saída de bombas");return false;
        }
        private void Notice(string text)
        {
            if (Time.unscaledTime<nextNotice) return;nextNotice=Time.unscaledTime+2f;
            if (GameManager.IsLocalAircraft(owner)) SceneSingleton<AircraftActionsReport>.i?.ReportText(text,3f);
        }
        private bool GunMayFire(Gun gun) => RigAlive && modules.Any(m => m.Guns.Contains(gun)) && sequence.FireAllowed(Observe());
        private static bool IsMarker(Transform t)=>t.name==Marker||t.name==ShortMarker;
        private static bool MayAim(Turret turret)
        {
            var marker=turret.GetComponentsInParent<Transform>(true).FirstOrDefault(IsMarker);
            if (marker==null) return true;
            var aircraft=turret.GetAttachedUnit() as Aircraft ?? turret.GetComponentInParent<Aircraft>();
            if (!IsBg(aircraft)) return true;
            return Simulated(aircraft) && aircraft.TryGetComponent<SuperheavyRetractableBattery>(out var c) &&
                c.modules.Any(m=>m.Turrets.Contains(turret)) && c.sequence.FireAllowed(c.Observe());
        }
        private void OnDestroy() { Park(true);pendingBomb=null;gearRequested=false; }

        [HarmonyPatch(typeof(Hardpoint),nameof(Hardpoint.SpawnMount))]
        private static class MountPatch { private static void Postfix(Aircraft __0) => Attach(__0); }
        [HarmonyPatch(typeof(Aircraft),"SetComplexPhysics",new Type[0])]
        private static class PhysicsPatch { private static void Postfix(Aircraft __instance) => Attach(__instance); }
        [HarmonyPatch(typeof(Gun),nameof(Gun.Fire),new[]{typeof(Unit),typeof(Unit),typeof(Vector3),typeof(WeaponStation),typeof(GlobalPosition)})]
        private static class FirePatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(Gun __instance,Unit __0)
            {
                if (!(__0 is Aircraft aircraft) || !IsBg(aircraft)) return true;
                var marker=__instance.GetComponentsInParent<Transform>(true).FirstOrDefault(IsMarker);
                if (marker==null) return true;
                return aircraft.TryGetComponent<SuperheavyRetractableBattery>(out var c) && c.GunMayFire(__instance);
            }
        }
        [HarmonyPatch(typeof(Turret),"AimTurret",new[]{typeof(Vector3)})]
        private static class AimVectorPatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(Turret __instance) => MayAim(__instance);
        }
        [HarmonyPatch(typeof(Turret),"AimTurret",new[]{typeof(WeaponStation)})]
        private static class AimStationPatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(Turret __instance,ref bool __result)
            { if(MayAim(__instance))return true;__result=false;return false; }
        }
        [HarmonyPatch(typeof(Aircraft),nameof(Aircraft.SetGear),new[]{typeof(bool)})]
        private static class GearPatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(Aircraft __instance,bool __0) => !__0 || !Simulated(__instance) ||
                !__instance.TryGetComponent<SuperheavyRetractableBattery>(out var c) || c.releasingGear || c.RequestGear();
        }
        [HarmonyPatch(typeof(Aircraft),nameof(Aircraft.SetGear),new[]{typeof(LandingGear.GearState)})]
        private static class GearStatePatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(Aircraft __instance,LandingGear.GearState __0) => __0!=LandingGear.GearState.Extending || !Simulated(__instance) ||
                !__instance.TryGetComponent<SuperheavyRetractableBattery>(out var c) || c.releasingGear || c.RequestGear();
        }
        [HarmonyPatch(typeof(WeaponStation),nameof(WeaponStation.LaunchMount))]
        private static class BombPatch
        {
            [HarmonyPriority(Priority.First+100)]
            private static bool Prefix(WeaponStation __instance,Unit __0,Unit __1,GlobalPosition __2) => !(__0 is Aircraft aircraft) || !Simulated(aircraft) ||
                !IsStore(__instance) || !aircraft.TryGetComponent<SuperheavyRetractableBattery>(out var c) || c.RequestBomb(__instance,__1,__2);
        }
        [HarmonyPatch(typeof(UnitPart),nameof(UnitPart.Repair))]
        private static class RepairPatch
        {
            private static void Postfix(UnitPart __instance)
            {
                if (__instance.parentUnit is Aircraft a && Simulated(a) && a.TryGetComponent<SuperheavyRetractableBattery>(out var c))
                {c.pendingBomb=null;c.bombingHold=true;c.sequence.Recover(c.AtStowed);}
            }
        }
    }
#endif
}
