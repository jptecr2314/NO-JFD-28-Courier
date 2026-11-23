using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Japensou.NuclearOption.AirframeData;
using Object=UnityEngine.Object;

namespace Japensou.NuclearOption.CurrentDamage
{
    // No new bodies or contacts. Native LandingGear creates its own physical
    // wreck; authored section clips stop addressing a leg once it leaves.
    [DefaultExecutionOrder(12000)]
    public sealed class CourierAssemblyOwnership:MonoBehaviour
    {
        private sealed class Gear {internal LandingGear Contact;internal AeroPart Support;internal Renderer[] Wheels;internal bool Released;}
        private sealed class Mount {internal AeroPart Part;internal AeroPart[] Supports;}
        private Animation[] animations;private Gear[] gears;private Mount[] mounts;
        private Component gearController;private Type gearType;private FieldInfo extension;private float lastPhase=-1;
        private NativePartitionController graph;
        private static readonly MethodInfo BreakGear=AccessTools.Method(typeof(LandingGear),"BreakWheel");
        public int ReleasedGears,ReleasedMounts;public double PeakStepMs;
        internal AeroPart GearSupport(LandingGear contact)
        {foreach(var gear in gears)if(gear.Contact==contact)return gear.Support;return null;}
        internal void AlignGearContact(LandingGear contact,Collider collider)
        {
            if(!(collider is BoxCollider box))return;var gear=gears.FirstOrDefault(g=>g.Contact==contact);if(gear?.Wheels==null||gear.Wheels.Length==0)return;
            var wheels=gear.Wheels.Where(w=>w).ToArray();if(wheels.Length==0)return;var bounds=wheels[0].bounds;foreach(var wheel in wheels.Skip(1))bounds.Encapsulate(wheel.bounds);
            // The donor hinge follows a different folded path. At release,
            // place its existing wreck contact on the authored visible wheels.
            box.transform.SetPositionAndRotation(bounds.center,Quaternion.identity);box.center=Vector3.zero;
            var scale=box.transform.lossyScale;box.size=new Vector3(bounds.size.x/Mathf.Abs(scale.x),bounds.size.y/Mathf.Abs(scale.y),bounds.size.z/Mathf.Abs(scale.z));
        }
        internal void Bind(NativePartitionController owner)
        {
            graph=owner;var data=GetComponent<CourierPresentationData>();
            animations=(data.SectionAnimations??new Component[0]).OfType<Animation>().ToArray();
            gears=(data.GearContacts??new Component[0]).Select((g,i)=>new Gear{Contact=g as LandingGear,Support=data.GearSupports[i] as AeroPart}).ToArray();
            var gearFrames=animations.SelectMany(a=>a.GetComponentsInChildren<Transform>(true)).ToArray();
            foreach(var gear in gears){var probe=NativePartitionIntegration.Get(gear.Contact,"castPoint")as Transform;string key=probe.name.Substring("SHGT_NativeProbe_".Length);var leg=gearFrames.First(t=>t.name=="SHGT_GearLeg_"+key+"_Motion");gear.Wheels=leg.GetComponentsInChildren<Renderer>(true).Where(r=>r.name.StartsWith("SHGT_GearSpin_",StringComparison.Ordinal)).ToArray();}
            mounts=(data.SupportedPartIds??new string[0]).Select((id,i)=>new Mount{Part=owner.Parts[id],Supports=data.SupportOwnerIds[i].Split('\n').Select(s=>owner.Parts[s]).ToArray()}).ToArray();
            // Hardpoint.part controls the native mount's parent and damage
            // callbacks. Preserve all choices/stations, route only the known
            // modular support anchors to the same owner as their fairings.
            var aircraft=GetComponent<Aircraft>();
            foreach(var set in aircraft.weaponManager.hardpointSets)foreach(var point in set.hardpoints)
            {
                var anchor=point.transform;if(!anchor)continue;string id=null;
                if(anchor.name.StartsWith("SHGT_TurretMount_",StringComparison.Ordinal))id="LocalFrame | "+(anchor.name.EndsWith("Aft",StringComparison.Ordinal)?"RearFuselage":"ForwardFuselage")+" | "+(anchor.name.Contains("Dorsal")?"Crown":"Belly");
                else if(anchor.name.StartsWith("SHGT_Sky20_",StringComparison.Ordinal))id="LocalFrame | ForwardFuselage | "+(transform.InverseTransformPoint(anchor.position).x<0?"Left":"Right");
                else if(anchor.name.StartsWith("SHGT_StrikeRail_",StringComparison.Ordinal))id="Wing_"+(anchor.name.EndsWith("Starboard",StringComparison.Ordinal)?"R":"L")+"_Mid";
                if(id!=null){var part=owner.Parts[id];point.part=part;anchor.SetParent(part.transform,true);}
            }
            gearType=CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavyGearPhysics");
            if(gearType!=null){gearController=GetComponent(gearType);extension=AccessTools.Field(gearType,"extension");}
        }
        private static bool Failed(AeroPart part)=>!part||part.GetComponent<NativePartitionPart>().OwnFractured||
            part.IsDetached()&&((PartJoint[])NativePartitionIntegration.Get(part,"joints")).All(j=>!j.joint);
        private void FixedUpdate()
        {
            if(!graph||!graph.Ready)return;var start=System.Diagnostics.Stopwatch.GetTimestamp();
            foreach(var gear in gears)if(!gear.Released&&gear.Contact&&Failed(gear.Support))
            {gear.Released=true;var hinge=NativePartitionIntegration.Get(gear.Contact,"gearHinge")as Transform;if(hinge&&!hinge.GetComponent<Rigidbody>()){BreakGear.Invoke(gear.Contact,null);ReleasedGears++;graph.CollisionChanged();}}
            foreach(var mount in mounts)if(mount.Part&&!graph.IsRepairing(mount.Part)&&mount.Supports.All(Failed))
            {
                bool changed=false;foreach(var j in (PartJoint[])NativePartitionIntegration.Get(mount.Part,"joints"))if(j.joint){Object.Destroy(j.joint);j.joint=null;changed=true;}
                if(changed){ReleasedMounts++;graph.CollisionChanged();}
            }
            PeakStepMs=Math.Max(PeakStepMs,(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency);
        }
        private void LateUpdate()
        {
            // Graph binding precedes the family physics prefix that installs
            // its controller. Resolve that component after native creation.
            if(!gearController&&gearType!=null)gearController=GetComponent(gearType);
            if(!gearController||extension==null)return;float phase=1-(float)extension.GetValue(gearController);if(phase==lastPhase)return;
            foreach(var animation in animations)if(animation&&animation.clip)animation.clip.SampleAnimation(animation.gameObject,animation.clip.length*phase);
            lastPhase=phase;
        }
    }
}
