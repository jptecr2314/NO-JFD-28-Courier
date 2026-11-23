using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier externally blown flaps (0.45.0). The GE-87 turbofans have fixed nozzles, so the
    // family thrust-vectoring envelope is switched off and the exhaust of each engine instead acts on the
    // flap panel directly behind it whenever the high-lift schedule has the flaps down:
    //  * jet reaction: the impinging part of the measured nozzle force is turned downward by the flap
    //    (turning efficiency and loss as for lower-surface externally blown flaps);
    //  * supercirculation: Spence jet-flap lift for the blown chord, minus its own reaction term, faded in
    //    with airspeed and vanishing with thrust;
    //  * the FBW pitch trim carries the extra nose-down moment on the horizontal tail, within the same
    //    authority the family trim uses, so handling stays neutral while lift rises with power.
    // Everything reads native state (nozzle force, flap schedule, part attachment); no aircraft data changes.
    public sealed class CourierBlownFlaps : MonoBehaviour
    {
        private const float FlapTravel = 30f, TurnPerFlap = .62f, TurnRecovery = .88f, Impingement = .65f;
        private const float PlumeHalfWidth = 2.6f, BlownChord = 12f, SpanEfficiency = .72f;
        private const float TailArea = 133.94f, TrimAuthority = .23f;
        private static readonly Vector3 HorizontalTail = new Vector3(0f, 11.3f, -28.25f);
        private static readonly AccessTools.FieldRef<JetNozzle, float> NozzleForce = AccessTools.FieldRefAccess<JetNozzle, float>("totalThrust");
        private static readonly AccessTools.FieldRef<JetNozzle, Transform> NozzlePoint = AccessTools.FieldRefAccess<JetNozzle, Transform>("thrustTransform");
        private static readonly AccessTools.FieldRef<JetNozzle, UnitPart> NozzlePart = AccessTools.FieldRefAccess<JetNozzle, UnitPart>("part");
        private static readonly AccessTools.FieldRef<Turbofan, JetNozzle[]> BankNozzles = AccessTools.FieldRefAccess<Turbofan, JetNozzle[]>("nozzles");
        private static readonly AccessTools.FieldRef<ControlSurface, UnitPart> FlapSurface = AccessTools.FieldRefAccess<ControlSurface, UnitPart>("attachedSurface");
        private static readonly AccessTools.FieldRef<AeroPart, float> WingEffectiveness = AccessTools.FieldRefAccess<AeroPart, float>("wingEffectiveness");
        private static MethodInfo flapPosition, reverseSelected;

        private sealed class Flap {internal string Name;internal Transform Hinge;internal UnitPart Surface;internal Vector3 Center;internal float MinX,MaxX;}
        private sealed class Jet {internal JetNozzle Nozzle;internal UnitPart Part;internal Flap Flap;internal float Share;}

        private Aircraft aircraft;
        private Component dynamics, controls;
        private Jet[] jets = Array.Empty<Jet>();
        private UnitPart stabilizer;
        private bool bound;
        internal float BlownLift {get;private set;}
        internal float Deflection {get;private set;}
        // Diagnostics read by the real-game bench (N, N·m): force along the body axis, blown pitch moment about
        // the centre of mass before trim, trim force applied and its authority.
        internal float AxialForce {get;private set;}
        internal float CgMoment {get;private set;}
        internal float TrimForce {get;private set;}
        internal float TrimLimit {get;private set;}
        internal Vector3 LocalForce {get;private set;}private Vector3 total;
        // Bench-only A/B switches (never set by the mod): drop the blown forces / restore family vectoring.
        internal string BodyInfo {get;private set;}
        private string body0Name()=>aircraft.rb?aircraft.rb.name+":"+aircraft.rb.mass.ToString("F0"):"none";
        internal static bool BenchForcesOff, BenchVectoring, BenchRootBody;internal static int BenchLumped;private Vector3 lumped;
        private Vector3 WholeCenter(){Vector3 sum=Vector3.zero;float m=0;foreach(var r in aircraft.partLookup.Where(p=>p&&!p.IsDetached()&&p.rb&&!p.rb.isKinematic).Select(p=>p.rb).Distinct()){sum+=r.worldCenterOfMass*r.mass;m+=r.mass;}return m>0?sum/m:aircraft.rb.worldCenterOfMass;}
        private void Push(Rigidbody target,Vector3 force,Vector3 at){total+=force;if(BenchLumped>0)lumped+=force;else target.AddForceAtPosition(force,at);}

        internal static void Attach(Aircraft aircraft)
        {
            if(aircraft==null||aircraft.GetComponent<CourierBlownFlaps>())return;
            aircraft.gameObject.AddComponent<CourierBlownFlaps>().aircraft=aircraft;
        }

        private IEnumerable<T> All<T>() where T:Component =>
            aircraft.GetComponentsInChildren<T>(true).Concat(aircraft.partLookup.Where(p=>p).SelectMany(p=>p.GetComponentsInChildren<T>(true))).Distinct();

        private void Bind()
        {
            bound=true;
            var family=CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics");var flight=CurrentAirframeCompatibility.Family("SuperheavyGroundFlightControls");
            if(family==null||flight==null)return;
            flapPosition=flapPosition??AccessTools.PropertyGetter(family,"FlapPosition");reverseSelected=reverseSelected??AccessTools.PropertyGetter(flight,"ReverseSelected");
            dynamics=GetComponent(family);controls=GetComponent(flight);
            if(!dynamics||flapPosition==null)return;
            var root=aircraft.transform;var flaps=new List<Flap>();
            foreach(var surface in All<ControlSurface>())
            {
                var hinge=surface.transform;
                if(!hinge.name.StartsWith("SHGT_Control_Flap_",StringComparison.Ordinal))continue;
                var skin=hinge.Find("SHGT_ControlMesh_"+hinge.name.Substring("SHGT_Control_".Length));var filter=skin?skin.GetComponent<MeshFilter>():null;
                var f=new Flap{Name=hinge.name.Substring("SHGT_Control_".Length),Hinge=hinge,Surface=FlapSurface(surface)??hinge.GetComponentInParent<UnitPart>(),MinX=float.MaxValue,MaxX=float.MinValue};
                if(filter&&filter.sharedMesh)
                {
                    var b=filter.sharedMesh.bounds;var sum=Vector3.zero;
                    for(int m=0;m<8;m++){var p=root.InverseTransformPoint(skin.TransformPoint(new Vector3((m&1)==0?b.min.x:b.max.x,(m&2)==0?b.min.y:b.max.y,(m&4)==0?b.min.z:b.max.z)));f.MinX=Mathf.Min(f.MinX,p.x);f.MaxX=Mathf.Max(f.MaxX,p.x);sum+=p;}
                    f.Center=sum/8f;
                }
                else {f.Center=root.InverseTransformPoint(hinge.position);f.MinX=f.Center.x-4f;f.MaxX=f.Center.x+4f;}
                flaps.Add(f);
            }
            var found=new List<Jet>();var geometry=new List<string>();
            foreach(var bank in All<Turbofan>())foreach(var nozzle in BankNozzles(bank)??Array.Empty<JetNozzle>())
            {
                if(!nozzle||!NozzlePoint(nozzle)||!nozzle.name.StartsWith("SHGT_ThrustOutlet_",StringComparison.Ordinal))continue;
                var p=root.InverseTransformPoint(NozzlePoint(nozzle).position);
                // The plume blows the flap panel behind it; the share is the plume width over the flap span.
                Flap best=null;float share=0;
                foreach(var f in flaps)
                {
                    if(f.Center.z>p.z+2f||Mathf.Sign(f.Center.x)!=Mathf.Sign(p.x))continue;
                    float overlap=Mathf.Max(0f,Mathf.Min(p.x+PlumeHalfWidth,f.MaxX)-Mathf.Max(p.x-PlumeHalfWidth,f.MinX))/(2f*PlumeHalfWidth);
                    if(overlap>share){share=overlap;best=f;}
                }
                if(best!=null&&share>.05f)found.Add(new Jet{Nozzle=nozzle,Part=NozzlePart(nozzle),Flap=best,Share=share*Impingement});
                else geometry.Add(nozzle.name.Replace("SHGT_ThrustOutlet_","E")+"@"+p.ToString("F1"));
            }
            foreach(var f in flaps)geometry.Add(f.Name+" x["+f.MinX.ToString("F1")+","+f.MaxX.ToString("F1")+"] c"+f.Center.ToString("F1"));
            jets=found.ToArray();
            stabilizer=All<UnitPart>().FirstOrDefault(p=>p.name=="hstab");
            BodyInfo=string.Join("; ",jets.Select(j=>j.Nozzle.name.Replace("SHGT_ThrustOutlet_","E")+" fwd="+root.InverseTransformDirection(NozzlePoint(j.Nozzle).forward).ToString("F3")+" engine="+(j.Part&&j.Part.rb?j.Part.name+":"+j.Part.rb.mass.ToString("F0"):"root")+" wing="+(j.Flap.Surface&&j.Flap.Surface.rb?j.Flap.Surface.name+":"+j.Flap.Surface.rb.mass.ToString("F0"):"root")))+"; root="+body0Name();
            CurrentAirframeDamagePlugin.Log.LogInfo("Courier blown flaps: "+jets.Length+" GE-87 outlets over "+flaps.Count+" flap panels ("+string.Join(", ",jets.Select(j=>j.Nozzle.name.Replace("SHGT_ThrustOutlet_","E")+">"+j.Flap.Name+" "+j.Share.ToString("P0")))+"); thrust vectoring off. Unblown/flaps: "+string.Join("; ",geometry));
        }

        private static bool Attached(UnitPart part)=>part!=null&&!part.IsDetached();
        private static float Effectiveness(UnitPart part)=>!Attached(part)?0f:part is AeroPart aero?Mathf.Clamp01(WingEffectiveness(aero)):1f;

        private void FixedUpdate()
        {
            BlownLift=0;Deflection=0;AxialForce=0;CgMoment=0;TrimForce=0;TrimLimit=0;
            if(BenchForcesOff)return;
            if(!aircraft||!aircraft.LocalSim||aircraft.disabled||!aircraft.rb||aircraft.rb.isKinematic)return;
            if(!bound)Bind();
            if(jets.Length==0||!dynamics)return;
            float flap=(float)flapPosition.Invoke(dynamics,null);
            // Same rotation schedule as the visible flap skins (rotation starts after 16% of the track).
            float rotation=Mathf.InverseLerp(.16f,1f,flap);rotation=rotation*rotation*(3f-2f*rotation);
            if(rotation<=0f)return;
            if(controls&&reverseSelected!=null&&(bool)reverseSelected.Invoke(controls,null))return;
            var root=aircraft.transform;var body=aircraft.rb;var wind=aircraft.GetWindVelocity();float density=aircraft.airDensity;
            Vector3 center=body.worldCenterOfMass;float pitchMoment=0f;Vector3 right=root.right;
            Deflection=FlapTravel*rotation;lumped=Vector3.zero;total=Vector3.zero;
            // The turning loss of the deflected jet is applied once the wheels are off the runway. On the gear
            // the native strut/rolling model turns this small aft load (~45 kN) into ~400 kN of extra rolling
            // resistance and a nose-heavy porpoise (measured A/B, 22/11/2026), so the ground roll keeps only the
            // turned (vertical) part of the jet and the supercirculation lift.
            float axialShare=Mathf.InverseLerp(1f,4f,aircraft.radarAlt);
            foreach(var jet in jets)
            {
                var point=NozzlePoint(jet.Nozzle);float force=NozzleForce(jet.Nozzle);
                if(!point||force<=0f||!Attached(jet.Part))continue;
                float flapHealth=Effectiveness(jet.Flap.Surface);if(flapHealth<=0f)continue;
                float turn=Deflection*TurnPerFlap*flapHealth*Mathf.Deg2Rad;float blown=force*jet.Share;
                Vector3 axis=point.forward;Vector3 normal=Vector3.ProjectOnPlane(root.up,axis).normalized;
                // Reaction of the turned jet relative to the native straight thrust.
                Vector3 reaction=axis*(blown*(TurnRecovery*Mathf.Cos(turn)-1f)*axialShare)+normal*(blown*TurnRecovery*Mathf.Sin(turn));
                var engineBody=!BenchRootBody&&jet.Part.rb?jet.Part.rb:body;
                Push(engineBody,reaction,point.position);
                pitchMoment+=Vector3.Dot(Vector3.Cross(point.position-center,reaction),right);
                BlownLift+=Vector3.Dot(reaction,root.up);AxialForce+=Vector3.Dot(reaction,root.forward);
                // Supercirculation over the blown chord (Spence jet flap, reaction term removed).
                var station=root.TransformPoint(new Vector3(Mathf.Clamp(root.InverseTransformPoint(point.position).x,jet.Flap.MinX,jet.Flap.MaxX),jet.Flap.Center.y,jet.Flap.Center.z+BlownChord*.35f));
                var flow=body.GetPointVelocity(station)-wind;float speed=flow.magnitude;
                if(speed<8f||density<.01f)continue;
                float q=.5f*density*speed*speed;float area=2f*PlumeHalfWidth*BlownChord;
                float cmu=Mathf.Min(blown/(q*area),25f);
                float spence=Mathf.Sqrt(4f*Mathf.PI*cmu*(1f+.151f*Mathf.Sqrt(cmu)+.139f*cmu));
                float circulation=SpanEfficiency*turn*Mathf.Max(0f,spence-cmu)*Mathf.InverseLerp(8f,30f,speed);
                float lift=Mathf.Min(circulation*q*area,1.2f*blown);
                Vector3 liftForce=Vector3.ProjectOnPlane(root.up,flow/speed).normalized*lift;
                var wingBody=!BenchRootBody&&Attached(jet.Flap.Surface)&&jet.Flap.Surface.rb?jet.Flap.Surface.rb:body;
                Push(wingBody,liftForce,station);
                pitchMoment+=Vector3.Dot(Vector3.Cross(station-center,liftForce),right);
                BlownLift+=Vector3.Dot(liftForce,root.up);AxialForce+=Vector3.Dot(liftForce,root.forward);
            }
            CgMoment=pitchMoment;
            // Automatic pitch trim on the horizontal tail, bounded like the family trim feed-forward.
            if(Attached(stabilizer)&&pitchMoment!=0f)
            {
                var tail=root.TransformPoint(HorizontalTail);var tailFlow=body.GetPointVelocity(tail)-wind;
                float tailQ=.5f*density*tailFlow.sqrMagnitude;float unit=Vector3.Dot(Vector3.Cross(tail-center,root.up),right);
                if(Mathf.Abs(unit)>1f&&tailQ>1f)
                {
                    float limit=TrimAuthority*TailArea*tailQ*Effectiveness(stabilizer);
                    float trim=Mathf.Clamp(-pitchMoment/unit,-limit,limit);
                    Push(!BenchRootBody&&stabilizer.rb?stabilizer.rb:body,root.up*trim,tail);
                    BlownLift+=trim;TrimForce=trim;TrimLimit=limit;
                }
            }
            LocalForce=root.InverseTransformDirection(total);
            if(BenchLumped>0&&lumped!=Vector3.zero)body.AddForceAtPosition(BenchLumped==2?Vector3.Project(lumped,root.up):lumped,WholeCenter());
        }

        // ---- family hooks (the frozen family runtime stays byte-identical) ----
        internal static void Install(Harmony h)
        {
            h.CreateClassProcessor(typeof(NoVectoring)).Patch();
            h.CreateClassProcessor(typeof(AttachWithDynamics)).Patch();
            h.CreateClassProcessor(typeof(MfdContext)).Patch();
            h.CreateClassProcessor(typeof(MfdLabel)).Patch();
        }
        private static bool IsCourier(Aircraft a)=>a!=null&&a.definition!=null&&a.definition.jsonKey=="jp_superheavy_ground_test";

        [HarmonyPatch] private static class NoVectoring
        {
            private static readonly FieldInfo owner=AccessTools.Field(CurrentAirframeCompatibility.Family("SuperheavyGroundFlightControls"),"aircraft");
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyGroundFlightControls"),"VectorEnvelope");
            private static bool Prefix(object __instance,ref Vector3 __result)
            {
                if(BenchVectoring||!IsCourier(owner.GetValue(__instance) as Aircraft))return true;
                __result=Vector3.zero;return false;
            }
        }
        [HarmonyPatch] private static class AttachWithDynamics
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyFlightDynamics"),"Attach");
            private static void Postfix(Aircraft __0){if(IsCourier(__0)&&__0.networked)Attach(__0);}
        }
        [ThreadStatic] private static Aircraft drawing;
        [HarmonyPatch] private static class MfdContext
        {
            private static readonly FieldInfo owner=AccessTools.Field(CurrentAirframeCompatibility.Family("SuperheavyGroundMfds"),"aircraft");
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyGroundMfds"),"LateUpdate");
            private static void Prefix(object __instance,out Aircraft __state){__state=drawing;drawing=owner.GetValue(__instance) as Aircraft;}
            private static void Finalizer(Aircraft __state)=>drawing=__state;
        }
        [HarmonyPatch] private static class MfdLabel
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.Family("SuperheavyMfdCanvas"),"Label");
            private static void Prefix(ref string __2)
            {
                if(__2==null||!__2.StartsWith("TVC ",StringComparison.Ordinal)||!IsCourier(drawing))return;
                var blown=drawing.GetComponent<CourierBlownFlaps>();
                __2=blown&&blown.Deflection>.5f?"BLOWN FLAP "+Mathf.RoundToInt(blown.Deflection)+" DEG":"FLAPS CLEAN";
            }
        }
    }
}
