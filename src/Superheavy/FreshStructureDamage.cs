using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object=UnityEngine.Object;
using K=Japensou.NuclearOption.CurrentDamage.FreshRuptureKernel;

namespace Japensou.NuclearOption.CurrentDamage
{
    public sealed class FreshStructureDamage:MonoBehaviour
    {
        private struct Context {internal bool Located;internal Vector3 Point,Axis;internal int Kind;}
        [ThreadStatic]private static Context current;
        private sealed class Hit {internal AeroPart Owner;internal Vector3 Point,Axis;internal float Loss;internal int Kind,Seed;}
        private sealed class Surface
        {internal NativePartitionSurface Meta;internal MeshFilter Filter;internal MeshRenderer Renderer;internal Mesh Original,Changed;internal List<K.Triangle> Faces;internal readonly List<GameObject> Lips=new List<GameObject>();internal readonly List<Operation> Operations=new List<Operation>();}
        private sealed class Operation {internal string SourceId;internal bool Health;internal Action<Surface> Replay;}
        private readonly Dictionary<string,int> frameStages=new Dictionary<string,int>();
        private CourierDamageVisuals courierVisuals;
        public int VisualPending=>courierVisuals?.Pending??pending.Count;
        public double VisualPeakStepMs=>courierVisuals?.PeakStepMs??0;
        public string VisualPeakStepPhase=>courierVisuals?.PeakStepPhase;
        public double VisualTotalStepMs=>courierVisuals?.TotalStepMs??0;
        public int VisualNoOpCuts=>courierVisuals?.NoOpCuts??0;
        public int VisualCommits=>courierVisuals?.Commits??0;
        public int VisualCancelled=>courierVisuals?.Cancelled??0;
        public int VisualLipObjects=>courierVisuals?.LipObjects??surfaces.Sum(s=>s.Lips.Count);
        public int VisualDeferredHidden=>courierVisuals?.DeferredHidden??0;
        public void SyncVisibility()=>courierVisuals?.SyncVisibility();
        internal void QualityChanged()=>courierVisuals?.QualityChanged();
        private NativePartitionController graph;private Aircraft aircraft;private readonly List<Surface> surfaces=new List<Surface>();private readonly Queue<Hit> pending=new Queue<Hit>();private readonly HashSet<string> exposed=new HashSet<string>();private readonly HashSet<string> separated=new HashSet<string>();private int sequence;
        public int LocatedHits,UnlocatedHits,ChangedSurfaces,SeamRuptures,GeneratedLipTriangles,AttachedContactCorrections;private int contactDiagnostics;
        internal static bool Converted(UnitPart p)=>p&&p.parentUnit&&p.GetComponent<NativePartitionPart>()&&p.parentUnit.GetComponent<NativePartitionController>();
        private static FreshStructureDamage Runtime(UnitPart p)=>Converted(p)?p.parentUnit.GetComponent<FreshStructureDamage>():null;
        internal static void Install(Harmony h)
        {
            h.CreateClassProcessor(typeof(Penetration)).Patch();h.CreateClassProcessor(typeof(Fragments)).Patch();h.CreateClassProcessor(typeof(Contact)).Patch();h.CreateClassProcessor(typeof(Receive)).Patch();h.CreateClassProcessor(typeof(Separate)).Patch();h.CreateClassProcessor(typeof(FragmentPolicy)).Patch();
            h.CreateClassProcessor(typeof(OwnJointAnchors)).Patch();
        }
        internal void Bind(NativePartitionController owner,Aircraft a)
        {
            graph=owner;aircraft=a;paintChannel=a.GetComponent<OwnedAirframePaint>()?.Channel??0;if(owner.CourierRefit){courierVisuals=new CourierDamageVisuals(owner,paintChannel);return;}foreach(var meta in owner.Parts.Values.SelectMany(p=>p.GetComponentsInChildren<NativePartitionSurface>(true)).Distinct())surfaces.Add(new Surface{Meta=meta,Filter=meta.GetComponent<MeshFilter>(),Renderer=meta.GetComponent<MeshRenderer>(),Original=meta.Original?meta.Original:meta.GetComponent<MeshFilter>().sharedMesh});
        }
        public bool Exposed(string id)=>exposed.Contains(id)||(courierVisuals?.Exposed(id)??false)||surfaces.Any(s=>s.Meta&&s.Meta.PartId==id&&s.Operations.Count>0);
        private FreshPartProfile Profile(AeroPart p)=>NativePartitionIntegration.Assets[graph.SourceKey].Profiles.ById[p.GetComponent<NativePartitionPart>().Id];
        private void ReceiveHit(AeroPart p,float loss,Context context)
        {
            if(loss<=0||float.IsNaN(loss)||float.IsInfinity(loss))return;
            exposed.Add(p.GetComponent<NativePartitionPart>().Id);
            UpdateFrameDamage(p);
            if(Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1"&&LocatedHits+UnlocatedHits<24)CurrentAirframeDamagePlugin.Log.LogInfo("OWN_DAMAGE_RECEIVED "+p.GetComponent<NativePartitionPart>().Id+" loss="+loss+" hp="+p.hitPoints+" located="+context.Located+" kind="+context.Kind);
            if(!context.Located){UnlocatedHits++;return;}LocatedHits++;var marker=p.GetComponent<NativePartitionPart>();exposed.Add(marker.Id);
            if(courierVisuals!=null){courierVisuals.Hit(p,context.Point,context.Axis,loss,context.Kind,++sequence,Profile(p));return;}
            pending.Enqueue(new Hit{Owner=p,Point=p.transform.InverseTransformPoint(context.Point),Axis=p.transform.InverseTransformDirection(context.Axis),Loss=loss,Kind=context.Kind,Seed=++sequence});
        }
        private void UpdateFrameDamage(AeroPart part)
        {
            if(courierVisuals!=null){courierVisuals.Health(part,Profile(part));return;}
            string id=part.GetComponent<NativePartitionPart>().Id;int stage=part.hitPoints>=95?0:part.hitPoints>=65?1:part.hitPoints>=35?2:3;
            if(frameStages.TryGetValue(id,out var prior)&&prior==stage)return;frameStages[id]=stage;var profile=Profile(part);
            foreach(var s in surfaces.Where(s=>s.Meta&&s.Meta.Fresh&&s.Meta.PartId==id))
            {
                s.Operations.RemoveAll(o=>o.Health);s.Faces=null;if(s.Changed){Object.Destroy(s.Changed);s.Changed=null;ChangedSurfaces--;}s.Filter.sharedMesh=s.Original;foreach(var lip in s.Lips)ReleaseLip(lip);s.Lips.Clear();
                foreach(var operation in s.Operations)operation.Replay(s);
                if(stage>0)
                {
                    Action<Surface> bend=target=>{Initialize(target);var b=target.Original.bounds;var size=b.size;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;int transverse=(axis+1)%3;float span=Mathf.Max(.001f,size[axis]);float amount=Mathf.Min(.22f,profile.seamRaggednessM*stage);
                        Func<K.Vertex,K.Vertex> warp=v=>{float u=Mathf.Clamp01((v.Position[axis]-b.min[axis])/span);var p=v.Position;p[transverse]+=Mathf.Sin(u*Mathf.PI)*amount;v.Position=p;return v;};target.Faces=target.Faces.Select(t=>new K.Triangle(warp(t.A),warp(t.B),warp(t.C))).ToList();Apply(target);};
                    bend(s);s.Operations.Add(new Operation{SourceId=id,Health=true,Replay=bend});
                }
                var block=new MaterialPropertyBlock();s.Renderer.GetPropertyBlock(block);block.SetColor("_BaseColor",Color.Lerp(Color.white,new Color(.28f,.23f,.18f),stage/3f));s.Renderer.SetPropertyBlock(block);
            }
        }
        private void LateUpdate(){if(courierVisuals!=null){courierVisuals.Step();ChangedSurfaces=courierVisuals.ChangedSurfaces;GeneratedLipTriangles=courierVisuals.GeneratedLipTriangles;return;}for(int i=0;i<3&&pending.Count>0;i++)Process(pending.Dequeue());}
        private void Process(Hit hit)
        {
            if(!hit.Owner)return;var profile=Profile(hit.Owner);var marker=hit.Owner.GetComponent<NativePartitionPart>();var point=hit.Owner.transform.TransformPoint(hit.Point);var axis=hit.Owner.transform.TransformDirection(hit.Axis).normalized;if(axis.sqrMagnitude<.01f)axis=hit.Owner.transform.forward;
            // Each role has its own bounded energy/section response. No old radius function is used.
            float radius=Mathf.Clamp(profile.tearMinRadiusM+profile.tearEnergyScale*(float)Math.Sqrt(hit.Loss)*(hit.Kind==1?1f:hit.Kind==2?2.4f:1.7f),profile.tearMinRadiusM,profile.tearMaxRadiusM);
            foreach(var s in surfaces.Where(s=>s.Meta&&s.Filter&&s.Meta.PartId==marker.Id&&!s.Meta.FixedReceiver))
            {
                var bounds=s.Renderer.bounds;bounds.Expand(radius*profile.tearStretch*2+profile.tearDepthM);if(!bounds.Contains(point))continue;Initialize(s);
                var center=s.Filter.transform.InverseTransformPoint(point);var localAxis=s.Filter.transform.InverseTransformDirection(axis);var longitudinal=s.Filter.transform.InverseTransformDirection(hit.Owner.transform.TransformDirection(NativePartitionIntegration.V(profile.seamNormal)));
                var wound=K.Prepare(center,localAxis,longitudinal,radius,profile.tearStretch,profile.tearDepthM+radius*.16f,profile.rimSides,hit.Seed*917+K.StableHash(marker.Id));
                if(Cut(s,wound,localAxis,profile,hit.Seed))s.Operations.Add(new Operation{SourceId=marker.Id,Replay=target=>Cut(target,wound,localAxis,profile,hit.Seed)});
            }
            graph.RefreshAfterDamage();
        }
        private bool Cut(Surface s,K.Wound wound,Vector3 axis,FreshPartProfile profile,int seed)
        {
            Initialize(s);var kept=new List<K.Triangle>(s.Faces.Count+64);var edges=new List<K.Edge>();float reach=profile.tearMaxRadiusM*(profile.tearStretch+1)+profile.tearDepthM;
            foreach(var t in s.Faces){var box=new Bounds(t.A.Position,Vector3.zero);box.Encapsulate(t.B.Position);box.Encapsulate(t.C.Position);if(box.SqrDistance(wound.Center)>reach*reach)kept.Add(t);else K.Subtract(t,wound,kept,edges);}
            // Bounded mesh growth is a visual budget, never an HP/damage limit.
            if(kept.Count>s.Original.triangles.Length/3*2+512)return false;s.Faces=kept;Apply(s);AddLips(s,edges,axis,profile,seed);return true;
        }
        private static void Initialize(Surface s)
        {
            if(s.Faces!=null)return;var m=s.Original;var positions=m.vertices;var normals=m.normals;var tangent=m.tangents;var uv=new[]{m.uv,m.uv2,m.uv3,m.uv4};var vertices=new K.Vertex[positions.Length];
            for(int i=0;i<vertices.Length;i++)vertices[i]=new K.Vertex{Position=positions[i],Normal=normals.Length>i?normals[i]:Vector3.up,Tangent=tangent.Length>i?tangent[i]:new Vector4(1,0,0,1),U0=uv[0].Length>i?uv[0][i]:Vector2.zero,U1=uv[1].Length>i?uv[1][i]:Vector2.zero,U2=uv[2].Length>i?uv[2][i]:Vector2.zero,U3=uv[3].Length>i?uv[3][i]:Vector2.zero};
            var ix=m.triangles;s.Faces=new List<K.Triangle>(ix.Length/3);for(int i=0;i<ix.Length;i+=3)s.Faces.Add(new K.Triangle(vertices[ix[i]],vertices[ix[i+1]],vertices[ix[i+2]]));
        }
        private static Mesh Build(List<K.Triangle> faces,string name)
        {
            int count=faces.Count*3;var p=new Vector3[count];var n=new Vector3[count];var ta=new Vector4[count];var uv=new[]{new Vector2[count],new Vector2[count],new Vector2[count],new Vector2[count]};var ix=new int[count];int at=0;
            foreach(var face in faces)foreach(var v in new[]{face.A,face.B,face.C}){p[at]=v.Position;n[at]=v.Normal.normalized;ta[at]=v.Tangent;uv[0][at]=v.U0;uv[1][at]=v.U1;uv[2][at]=v.U2;uv[3][at]=v.U3;ix[at]=at;at++;}
            var m=new Mesh{name=name,indexFormat=count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};m.vertices=p;m.normals=n;m.tangents=ta;for(int k=0;k<4;k++)m.SetUVs(k,uv[k]);m.triangles=ix;m.RecalculateBounds();return m;
        }
        private int paintChannel;
        private static void Layout(Mesh mesh,int channel){if(channel>0)mesh.SetUVs(0,channel==1?mesh.uv2:mesh.uv3);}
        public void ApplyPaint(int channel){paintChannel=channel;if(courierVisuals!=null){courierVisuals.Paint(channel);return;}foreach(var s in surfaces)if(s.Meta.Painted&&s.Faces!=null)Apply(s);}
        private void Apply(Surface s){if(s.Changed)Object.Destroy(s.Changed);else ChangedSurfaces++;s.Changed=Build(s.Faces,"OWN profile rupture "+s.Meta.PartId);if(s.Meta.Painted)Layout(s.Changed,paintChannel);s.Filter.sharedMesh=s.Changed;}
        private void AddLips(Surface s,List<K.Edge> edges,Vector3 axis,FreshPartProfile profile,int seed)
        {
            if(edges.Count==0)return;var faces=new List<K.Triangle>();int i=0;var seen=new HashSet<string>();
            foreach(var e in edges)
            {
                string a=Key(e.A.Position),b=Key(e.B.Position),key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;if(!seen.Add(key))continue;var c=e.A;var d=e.B;var middle=(c.Position+d.Position)*.5f;var inward=Vector3.Cross(axis,d.Position-c.Position).normalized;float bend=profile.lipWidthM*(.4f+K.Noise(seed,i++));c.Position+=inward*profile.lipWidthM-axis*bend;d.Position+=inward*profile.lipWidthM-axis*bend;faces.Add(new K.Triangle(e.A,e.B,d));faces.Add(new K.Triangle(e.A,d,c));
            }
            if(faces.Count==0)return;var go=new GameObject("OWN folded tear lip",typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(s.Filter.transform,false);go.layer=s.Filter.gameObject.layer;go.GetComponent<MeshFilter>().sharedMesh=Build(faces,"OWN tear lip");var material=new Material(CurrentAirframeDamagePlugin.Assets[graph.SourceKey].Plain);if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",new Color(.24f,.21f,.18f,1));if(material.HasProperty("_Color"))material.SetColor("_Color",new Color(.24f,.21f,.18f,1));material.SetFloat("_Smoothness",.14f);go.GetComponent<MeshRenderer>().sharedMaterial=material;s.Lips.Add(go);GeneratedLipTriangles+=faces.Count;
        }
        private static string Key(Vector3 p)=>Math.Round(p.x,5)+","+Math.Round(p.y,5)+","+Math.Round(p.z,5);
        private void SeparatePart(AeroPart part)
        {
            var marker=part.GetComponent<NativePartitionPart>();if(!marker||!separated.Add(marker.Id))return;graph.CollisionChanged();exposed.Add(marker.Id);var profile=Profile(part);var row=NativePartitionIntegration.Assets[graph.SourceKey].M.parts.Single(p=>p.id==marker.Id);
            Vector3 local=NativePartitionIntegration.V(profile.anchor)-(marker.Id=="Core"?Vector3.zero:NativePartitionIntegration.V(row.center));var world=part.transform.TransformPoint(local);var normal=part.transform.TransformDirection(NativePartitionIntegration.V(profile.seamNormal));Seam(part,world,normal,profile,1,marker.Id);
            if(marker.ParentId!=null&&graph.Parts.TryGetValue(marker.ParentId,out var parent)&&parent)Seam(parent,world,normal,profile,-1,marker.Id);SeamRuptures++;graph.RefreshAfterDamage();
        }
        private void Seam(AeroPart owner,Vector3 point,Vector3 axis,FreshPartProfile profile,int direction,string sourceId)
        {
            if(courierVisuals!=null){courierVisuals.Seam(owner,point,axis,profile,direction,sourceId);return;}
            string id=owner.GetComponent<NativePartitionPart>().Id;
            foreach(var s in surfaces.Where(s=>s.Meta&&s.Filter&&s.Meta.PartId==id&&!s.Meta.FixedReceiver))
            {
                var center=s.Filter.transform.InverseTransformPoint(point);var normal=s.Filter.transform.InverseTransformDirection(axis).normalized;int seed=++sequence;
                if(DeformSeam(s,center,normal,profile,direction,id,seed))s.Operations.Add(new Operation{SourceId=sourceId,Replay=target=>DeformSeam(target,center,normal,profile,direction,id,seed)});
            }
        }
        private bool DeformSeam(Surface s,Vector3 center,Vector3 normal,FreshPartProfile profile,int direction,string id,int seed)
        {
                Initialize(s);float band=Math.Max(.035f,profile.seamRaggednessM);var boundary=new Dictionary<string,(K.Vertex A,K.Vertex B,int Count)>();
                foreach(var t in s.Faces)foreach(var e in new[]{new K.Edge(t.A,t.B),new K.Edge(t.B,t.C),new K.Edge(t.C,t.A)})
                {if(Math.Abs(Vector3.Dot(e.A.Position-center,normal))>band||Math.Abs(Vector3.Dot(e.B.Position-center,normal))>band)continue;string a=Key(e.A.Position),b=Key(e.B.Position),key=string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;if(boundary.TryGetValue(key,out var old))boundary[key]=(old.A,old.B,old.Count+1);else boundary[key]=(e.A,e.B,1);}
                var open=boundary.Values.Where(e=>e.Count==1).ToArray();if(open.Length==0)return false;
                Func<K.Vertex,K.Vertex> shear=v=>{if(Math.Abs(Vector3.Dot(v.Position-center,normal))<=band)v.Position+=normal*(direction*profile.seamRaggednessM*(.2f+.8f*K.Noise(K.StableHash(id),K.StableHash(Key(v.Position)))));return v;};
                s.Faces=s.Faces.Select(t=>new K.Triangle(shear(t.A),shear(t.B),shear(t.C))).ToList();Apply(s);
                var edges=open.Select(e=>new K.Edge(shear(e.A),shear(e.B))).ToList();AddLips(s,edges,normal*direction,profile,seed);return true;
        }
        private void Catastrophic(AeroPart part)
        {
            var marker=part.GetComponent<NativePartitionPart>();if(marker.OwnFractured)return;marker.OwnFractured=true;exposed.Add(marker.Id);graph.RecordFailure(part,"integrity");
            // Native BreakAllJoints also erases the authored descriptors. Keep
            // those descriptors so finite-HP failures remain repairable.
            foreach(var joint in(PartJoint[])NativePartitionIntegration.Get(part,"joints"))if(joint.joint){Object.Destroy(joint.joint);joint.joint=null;}
            // Destroying a load-bearing section also releases its direct
            // attachments. Their own subassemblies can remain joined; their
            // descriptors and HP are preserved for native branch repair.
            foreach(var childPart in graph.Parts.Values.Where(p=>p&&p!=part))foreach(var joint in(PartJoint[])NativePartitionIntegration.Get(childPart,"joints"))if(joint.connectedPart==part&&joint.joint){Object.Destroy(joint.joint);joint.joint=null;}
            graph.CollisionChanged();
            // Unity serializes a null string as empty in clones. The root is
            // not a detachable child and must never enter AeroPart.Detach.
            bool child=!string.IsNullOrEmpty(marker.ParentId)&&marker.Id!="Core"&&NativePartitionIntegration.Get(part,"attachInfo")!=null;
            if(child&&!part.IsDetached()&&part.rb)part.parentUnit.DetachPart((byte)NativePartitionIntegration.Get(part,"id"),part.rb.velocity,part.transform.position-part.parentUnit.transform.position);
            if(child)SeparatePart(part);else graph.RefreshAfterDamage();
        }
        public void RepairPart(AeroPart owner)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;graph.CollisionChanged();exposed.Remove(id);separated.Remove(id);
            if(courierVisuals!=null){courierVisuals.Repair(id);GetComponent<OwnedAirframePaint>()?.Refresh();return;}
            frameStages.Remove(id);
            // Remove both faces of this section's seam, then replay unrelated
            // wounds/seams instead of healing their geometry or their HP.
            foreach(var s in surfaces.Where(s=>s.Meta&&(s.Meta.PartId==id||s.Operations.Any(o=>o.SourceId==id))))
            {s.Operations.RemoveAll(o=>o.SourceId==id);if(s.Filter)s.Filter.sharedMesh=s.Original;if(s.Changed){Object.Destroy(s.Changed);ChangedSurfaces--;}s.Changed=null;s.Faces=null;foreach(var lip in s.Lips)ReleaseLip(lip);s.Lips.Clear();foreach(var operation in s.Operations)operation.Replay(s);if(s.Meta.Fresh){var block=new MaterialPropertyBlock();s.Renderer.GetPropertyBlock(block);block.SetColor("_BaseColor",Color.white);s.Renderer.SetPropertyBlock(block);}}
            GetComponent<OwnedAirframePaint>()?.Refresh();
            int count=pending.Count;for(int i=0;i<count;i++){var h=pending.Dequeue();if(h.Owner!=owner)pending.Enqueue(h);}
        }
        private static void ReleaseLip(GameObject go){if(!go)return;var mesh=go.GetComponent<MeshFilter>().sharedMesh;var mat=go.GetComponent<MeshRenderer>().sharedMaterial;Object.Destroy(go);Object.Destroy(mesh);Object.Destroy(mat);}
        private void OnDestroy(){courierVisuals?.Dispose();foreach(var s in surfaces){if(s.Changed)Object.Destroy(s.Changed);foreach(var lip in s.Lips)ReleaseLip(lip);}}
        [HarmonyPatch(typeof(DamageEffects),"ArmorPenetrate")]
        private static class Penetration
        {private static void Prefix(Vector3 __0,Vector3 __1,out Context __state){__state=current;current=new Context{Located=true,Point=__0,Axis=__1,Kind=1};}private static void Finalizer(Context __state)=>current=__state;}
        [HarmonyPatch(typeof(DamageEffects),"FragTrace")]
        private static class Fragments
        {private static void Prefix(Vector3 __0,Vector3 __1,out Context __state){__state=current;current=new Context{Located=true,Point=__0,Axis=__1,Kind=2};}private static void Finalizer(Context __state)=>current=__state;}
        [HarmonyPatch]
        private static class Contact
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(AeroPart),"OnCollisionEnter");yield return AccessTools.Method(typeof(AeroPart),"OnCollisionStay");}
            private static bool Prefix(AeroPart __instance,Collision __0,out Context __state)
            {
                __state=current;if(__0!=null&&__0.contactCount>0)
                {
                    var c=__0.GetContact(0);current=new Context{Located=true,Point=c.point,Axis=c.normal,Kind=3};
                    var a=c.thisCollider.GetComponentInParent<UnitPart>();var b=c.otherCollider.GetComponentInParent<UnitPart>();
                    // A body/collider transition can reset Unity's pair filter.
                    // Attached members never damage each other; free debris still can.
                    if(Converted(__instance)&&a&&b&&a.parentUnit==b.parentUnit&&Runtime(__instance).graph.SameAssembly(a,b))
                    {
                        var runtime=Runtime(__instance);runtime.AttachedContactCorrections++;
                        if(Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1"&&runtime.AttachedContactCorrections<12)CurrentAirframeDamagePlugin.Log.LogInfo("OWN_ATTACHED_CONTACT t="+Time.time+" "+c.thisCollider.name+" vs "+c.otherCollider.name+" ignored="+Physics.GetIgnoreCollision(c.thisCollider,c.otherCollider)+" owners="+a.GetComponent<NativePartitionPart>()?.Id+"/"+b.GetComponent<NativePartitionPart>()?.Id+" separation="+__0.relativeVelocity.magnitude);
                        // A compound actor reports several shape pairs in one
                        // callback. Filtering only GetContact(0) leaves the
                        // remaining attached shapes in the solver indefinitely.
                        for(int i=0;i<__0.contactCount;i++)
                        {
                            var contact=__0.GetContact(i);var own=contact.thisCollider;var other=contact.otherCollider;
                            var pa=own.GetComponentInParent<UnitPart>();var pb=other.GetComponentInParent<UnitPart>();
                            if(pa&&pb&&pa.parentUnit==pb.parentUnit&&runtime.graph.SameAssembly(pa,pb))
                            {Physics.IgnoreCollision(own,other,true);runtime.graph.TrackAttachedContact(own,other);}
                        }
                        return false;
                    }
                    var r=Runtime(__instance);if(r&&Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1"&&r.contactDiagnostics++<16)CurrentAirframeDamagePlugin.Log.LogInfo("OWN_DEBRIS_CONTACT "+c.thisCollider.name+"("+(a?a.IsDetached().ToString():"noOwner")+") vs "+c.otherCollider.name+"("+(b?b.IsDetached().ToString():"noOwner")+") ignored="+Physics.GetIgnoreCollision(c.thisCollider,c.otherCollider)+" speed="+__0.relativeVelocity.magnitude);
                }
                return true;
            }
            private static void Finalizer(Context __state)=>current=__state;
        }
        [HarmonyPatch(typeof(UnitPart),nameof(UnitPart.ApplyDamage))]
        private static class Receive
        {private static void Prefix(UnitPart __instance,out float __state)=>__state=__instance.hitPoints;private static void Postfix(UnitPart __instance,float __state){var r=Runtime(__instance);if(r&&__instance is AeroPart p)r.ReceiveHit(p,Math.Max(0,__state-p.hitPoints),current);}}
        [HarmonyPatch(typeof(AeroPart),nameof(AeroPart.Detach))]
        private static class Separate
        {private static void Postfix(AeroPart __instance){var r=Runtime(__instance);if(r)r.SeparatePart(__instance);}}
        [HarmonyPatch(typeof(AeroPart),nameof(AeroPart.SpawnFragments))]
        private static class FragmentPolicy
        {private static bool Prefix(AeroPart __instance){if(!Converted(__instance))return true;var r=Runtime(__instance);if(r&&__instance.hitPoints<r.Profile(__instance).integrityThreshold)r.Catastrophic(__instance);return false;}}
        [HarmonyPatch(typeof(AeroPart),nameof(AeroPart.CreateJoints))]
        private static class OwnJointAnchors
        {
            private static void Postfix(AeroPart __instance)
            {
                if(!Converted(__instance))return;
                foreach(var p in(PartJoint[])NativePartitionIntegration.Get(__instance,"joints"))if(p.joint&&p.anchor&&p.connectedPart&&p.connectedPart.rb)
                {
                    var local=__instance.transform.InverseTransformPoint(p.anchor.position);
                    p.joint.enablePreprocessing=true;p.joint.autoConfigureConnectedAnchor=false;p.joint.anchor=local;
                    var world=__instance.rb.position+__instance.rb.rotation*local;
                    p.joint.connectedAnchor=Quaternion.Inverse(p.connectedPart.rb.rotation)*(world-p.connectedPart.rb.position);
                }
            }
        }
    }
}

