using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Object=UnityEngine.Object;
using K=Japensou.NuclearOption.CurrentDamage.FreshRuptureKernel;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Opt-in visual backend for the Courier refit. Native HP, forces, joints and
    // physical contacts remain synchronous. Only mesh work is scheduled here.
    internal sealed class CourierDamageVisuals:IDisposable
    {
        private const double FrameBudgetMs=2.0;
        private const int Batch=16,LipBudget=2048;
        private sealed class Operation
        {
            internal string SourceId;internal int Kind,Stage,Seed,Direction;
            internal Vector3 Center,Axis;internal K.Wound Wound;internal FreshPartProfile Profile;internal float Area,Radius;internal bool Distributed,Rejected;
        }
        private sealed class Surface
        {
            internal NativePartitionSurface Meta;internal MeshFilter Filter;internal MeshRenderer Renderer;
            internal Mesh Original,Changed,LipMesh;internal Vector2[] BaseUv;internal GameObject LipObject;
            internal Bounds HealthBounds;internal int TriangleCount;
            internal TriangleBuffer Faces,Lips;internal readonly List<Operation> Operations=new List<Operation>();
            internal int Epoch,Applied;internal bool Rebuild;
            internal float SkinArea=-1;internal float OpenArea=>Operations.Where(o=>o.Kind==0).Sum(o=>o.Area);
            internal float Target,TopUpHp;internal Matrix4x4 ToAircraft,FromAircraft;internal bool AxisShell;internal readonly List<Vector3> Hotspots=new List<Vector3>();internal string TopUpId;internal FreshPartProfile TopUpProfile;internal int TopUpIndex,DistributedCount;
            internal Mesh SampledMesh,SootMesh;internal float[] Cumulative;internal Vector3[] SampleVertices;internal int[] SampleTriangles;internal GameObject SootObject;
        }
        private readonly NativePartitionController graph;
        private readonly List<Surface> surfaces=new List<Surface>();
        private readonly Dictionary<string,Surface[]> owners=new Dictionary<string,Surface[]>();
        private readonly Dictionary<string,int> stages=new Dictionary<string,int>();
        private readonly Queue<Surface> queue=new Queue<Surface>();private readonly HashSet<Surface> queued=new HashSet<Surface>();
        private readonly Stopwatch watch=new Stopwatch();private IEnumerator active;private Material lipMaterial;
        private int channel,sequence;private bool disposed;private Mesh stagedMesh,stagedLip;
        private static int preparation;private static CourierDamageVisuals preparationOwner;private string phase="idle";
        internal int Commits,NoOpCuts,Cancelled,RejectedGrowth,ChangedSurfaces,GeneratedLipTriangles;
        internal double PeakStepMs,TotalStepMs;internal string PeakStepPhase;internal int Pending=>queue.Count+(active!=null?1:0)+topUp.Count;
        internal int LipObjects=>surfaces.Count(s=>s.LipObject);
        internal int DeferredHidden=>surfaces.Count(s=>s.Renderer&&!s.Renderer.enabled&&s.Operations.Count>0&&(s.Rebuild||s.Applied<s.Operations.Count));
        private static bool NeedsWork(Surface s)=>s.Rebuild||s.Applied<s.Operations.Count;

        internal CourierDamageVisuals(NativePartitionController owner,int paintChannel)
        {
            graph=owner;channel=paintChannel;
            foreach(var meta in owner.Parts.Values.SelectMany(p=>p.GetComponentsInChildren<NativePartitionSurface>(true)).Distinct())
                surfaces.Add(new Surface{Meta=meta,Filter=meta.GetComponent<MeshFilter>(),Renderer=meta.GetComponent<MeshRenderer>(),Original=meta.Original?meta.Original:meta.GetComponent<MeshFilter>().sharedMesh});
            if(graph.Presentation)foreach(var s in surfaces)if(!s.Meta.BakedInterior)s.Original=graph.Presentation.SourceMesh(s.Filter,s.Original);
            foreach(var group in surfaces.GroupBy(s=>s.Meta.PartId))owners[group.Key]=group.ToArray();
            foreach(var s in surfaces)
            {
                s.TriangleCount=(int)s.Original.GetIndexCount(0)/3;s.HealthBounds=s.Original.bounds;
                if(s.Meta.Fresh&&!s.Meta.BakedInterior)foreach(var other in owners[s.Meta.PartId].Where(o=>o.Meta.Fresh))
                {
                    var b=other.Original.bounds;
                    for(int mask=0;mask<8;mask++){var p=new Vector3((mask&1)==0?b.min.x:b.max.x,(mask&2)==0?b.min.y:b.max.y,(mask&4)==0?b.min.z:b.max.z);s.HealthBounds.Encapsulate(s.Filter.transform.InverseTransformPoint(other.Filter.transform.TransformPoint(p)));}
                }
            }
            lipMaterial=new Material(CurrentAirframeDamagePlugin.Assets[graph.SourceKey].Plain){name="Courier shared torn metal"};
            if(lipMaterial.HasProperty("_BaseColor"))lipMaterial.SetColor("_BaseColor",new Color(.24f,.21f,.18f,1));
            if(lipMaterial.HasProperty("_Color"))lipMaterial.SetColor("_Color",new Color(.24f,.21f,.18f,1));
            if(lipMaterial.HasProperty("_Smoothness"))lipMaterial.SetFloat("_Smoothness",.14f);
            if(graph.Presentation&&preparation==0){preparation=1;preparationOwner=this;graph.StartCoroutine(PrepareVisualPath());}
        }
        private IEnumerator PrepareVisualPath()
        {
            // Exercise only this visual backend on an invisible disposable
            // quad during aircraft loading. Never warm native HP or physics.
            var go=new GameObject("Courier visual preparation"){hideFlags=HideFlags.HideAndDontSave};go.SetActive(false);var filter=go.AddComponent<MeshFilter>();go.AddComponent<MeshRenderer>();var meta=go.AddComponent<NativePartitionSurface>();meta.PartId="visual preparation";
            var mesh=new Mesh();mesh.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};mesh.normals=Enumerable.Repeat(Vector3.forward,4).ToArray();mesh.tangents=Enumerable.Repeat(new Vector4(1,0,0,1),4).ToArray();mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();filter.sharedMesh=mesh;
            var s=new Surface{Meta=meta,Filter=filter,Renderer=go.GetComponent<MeshRenderer>(),Original=mesh,TriangleCount=2};var p=NativePartitionIntegration.Assets[graph.SourceKey].Profiles.ById.Values.First();
            var wound=K.Prepare(Vector3.zero,Vector3.forward,Vector3.right,.2f,1.1f,.05f,7,917);s.Operations.Add(new Operation{SourceId=meta.PartId,Kind=0,Center=Vector3.zero,Axis=Vector3.forward,Wound=wound,Profile=p,Seed=917});
            s.Operations.Add(new Operation{SourceId=meta.PartId,Kind=1,Center=Vector3.left,Axis=Vector3.right,Profile=p,Direction=1,Seed=918});
            bool finished=false;int beforeCommits=Commits,beforeNoOps=NoOpCuts;
            try{var work=Process(s);while(work.MoveNext())yield return null;finished=true;}
            finally{ReleaseChanged(s);ReleaseLips(s);ReleaseStaged();Object.Destroy(mesh);Object.Destroy(go);Commits=beforeCommits;NoOpCuts=beforeNoOps;if(preparationOwner==this){preparation=finished?2:0;preparationOwner=null;}}
        }
        internal bool Exposed(string id)=>owners.TryGetValue(id,out var group)&&group.Any(s=>s.Operations.Count>0);
        private void Schedule(Surface s){if(queued.Add(s))queue.Enqueue(s);}
        private void Reset(Surface s){s.Epoch++;s.Rebuild=true;s.Applied=0;Schedule(s);}
        // Owner references 24/11/2026 (Centurion / Darkreach at 0 % health): stock skins show health-driven damage spread
        // over the whole part - many small ragged holes revealing the ribs, soot streaking aft, panels flat and attached.
        // Measured on the stock AircraftSkin shader (flight-stockshader3-20261124): open skin 1 % at 80 HP, 6 % at 60,
        // 10 % at 40, 12 % at 20, 13 % at 0 and nothing more below 0. The old whole-panel sine warp and uniform darkening
        // are not part of that look and are retired for skins; skins now receive distributed ragged hole clusters
        // up to that curve, plus soot decals around every hole.
        private static readonly float[] CurveHp={100f,90f,80f,60f,40f,20f,0f},CurveOpen={0f,0f,.01f,.06f,.10f,.12f,.13f};
        private const int MaxClustersPerCall=24,MaxDistributedPerSurface=260;
        private static readonly string[] ExteriorCategories={"wing_root","wing_mid","wing_outer","wing_rear_box","stabilizer","fin_root","fin_upper","tail_spine","flap","slat","spoiler","control"};
        internal int DistributedHoles;
        internal static float OpenTarget(float hp)
        {
            if(hp>=CurveHp[0])return 0f;if(hp<=0f)return CurveOpen[CurveOpen.Length-1];
            for(int i=1;i<CurveHp.Length;i++)if(hp>=CurveHp[i]){float t=(hp-CurveHp[i])/(CurveHp[i-1]-CurveHp[i]);return Mathf.Lerp(CurveOpen[i],CurveOpen[i-1],t);}
            return CurveOpen[CurveOpen.Length-1];
        }
        private static bool Skin(Surface s)=>s.Meta&&s.Filter&&s.Renderer&&!s.Meta.FixedReceiver&&!s.Meta.BakedInterior&&!s.Meta.Interior&&!s.Meta.InteriorFurniture;
        internal void Health(AeroPart owner,FreshPartProfile profile)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;
            int stage=owner.hitPoints>=95?0:owner.hitPoints>=65?1:owner.hitPoints>=35?2:3;
            if(!owners.TryGetValue(id,out var group)){if(!stages.TryGetValue(id,out var o0)||o0!=stage){stages[id]=stage;graph.Presentation?.Health(id,stage);}return;}
            if(!stages.TryGetValue(id,out var old)||old!=stage)
            {
                stages[id]=stage;graph.Presentation?.Health(id,stage);
                foreach(var s in group)if(s.Meta.Fresh&&s.Meta.BakedInterior)Tint(s,stage);
            }
            if(profile==null||profile.category==null)return;
            // Exterior sections that show health damage on the stock aircraft: fuselage skins, wings, tail and the
            // movable surfaces (owner references: wings and tail are as damaged as the fuselage).
            bool shell=profile.category.StartsWith("skin_",StringComparison.Ordinal);
            if(!shell&&Array.IndexOf(ExteriorCategories,profile.category)<0)return;
            float target=OpenTarget(owner.hitPoints);if(target<=0f)return;
            int index=0;
            foreach(var s in group)
            {
                index++;if(!Skin(s))continue;
                if(target<=s.Target)continue;
                s.Target=target;s.AxisShell=shell;s.TopUpId=id;s.TopUpProfile=profile;s.TopUpHp=owner.hitPoints;s.TopUpIndex=index;topUp.Add(s);
            }
        }
        // Clusters are added a few at a time from Step() inside the frame budget, so a large health drop never
        // stalls one frame (flight-health1-20261124: 277-376 ms frames when every cluster was added in Health()).
        private readonly HashSet<Surface> topUp=new HashSet<Surface>();
        private bool TopUp(double budget)
        {
            if(topUp.Count==0)return false;var s=topUp.First();
            if(!s.Filter||!s.Meta){topUp.Remove(s);return true;}
            if(s.SkinArea<0){s.SkinArea=SurfaceArea(s.Original);return true;}
            if(!Sampler(s)){topUp.Remove(s);return true;}
            float goal=s.Target*s.SkinArea;int added=0;
            while(watch.Elapsed.TotalMilliseconds<budget&&added<MaxClustersPerCall)
            {
                if(s.OpenArea>=goal||s.DistributedCount>=MaxDistributedPerSurface||!AddCluster(s,s.TopUpId,s.TopUpProfile,s.TopUpHp,s.TopUpIndex)){topUp.Remove(s);break;}
                added++;
            }
            if(added>0)Schedule(s);return true;
        }
        private bool Sampler(Surface s)
        {
            if(s.SampledMesh==s.Original&&s.Cumulative!=null)return s.Cumulative.Length>0;
            s.SampledMesh=s.Original;s.Cumulative=new float[0];
            if(!s.Original||!s.Original.isReadable)return false;
            var v=s.Original.vertices;var t=s.Original.triangles;var cum=new float[t.Length/3];float total=0;
            for(int i=0;i<cum.Length;i++){total+=Vector3.Cross(v[t[i*3+1]]-v[t[i*3]],v[t[i*3+2]]-v[t[i*3]]).magnitude*.5f;cum[i]=total;}
            if(total<=1e-6f)return false;
            // Outward side per sample: the two-sided skins carry both windings (flight-health7-20261124: half the holes
            // had their cavity outside), so each sampled normal is flipped to point away from the fuselage axis
            // (aircraft-local x=0, y=4.2 m; all Courier skin sections are shells).
            s.ToAircraft=graph.transform.worldToLocalMatrix*s.Filter.transform.localToWorldMatrix;s.FromAircraft=s.ToAircraft.inverse;
            s.SampleVertices=v;s.SampleTriangles=t;s.Cumulative=cum;return true;
        }
        // One damage cluster: a main ragged hole plus 0-3 smaller overlapping satellites, so the union outline is
        // irregular like the stock masks. Positions are area-weighted and deterministic per part/surface/count.
        private bool AddCluster(Surface s,string id,FreshPartProfile profile,float hp,int surfaceIndex)
        {
            if(!Sampler(s))return false;
            int n=s.DistributedCount;int seed=unchecked(K.StableHash(id)*31+surfaceIndex*7919+n*104729);
            var cum=s.Cumulative;var v=s.SampleVertices;var t=s.SampleTriangles;
            // Stock damage is patchy, not uniform (owner references ref18/ref20): 60 % of new clusters gather near an
            // earlier one - best of six area-weighted samples - so damaged regions grow while others stay cleaner.
            bool gather=s.Hotspots.Count>0&&K.Noise(seed,8)<.6f;Vector3 hotspot=gather?s.Hotspots[(int)(K.Noise(seed,9)*s.Hotspots.Count)%s.Hotspots.Count]:Vector3.zero;
            Vector3 a=Vector3.zero,b=Vector3.zero,c=Vector3.zero,p=Vector3.zero;float best=float.MaxValue;
            for(int k=0;k<(gather?6:1);k++)
            {
                float pick=K.Noise(seed,100+k)*cum[cum.Length-1];int l=0,h=cum.Length-1;while(l<h){int mid=(l+h)/2;if(cum[mid]<pick)l=mid+1;else h=mid;}
                Vector3 ka=v[t[l*3]],kb=v[t[l*3+1]],kc=v[t[l*3+2]];float r1=(float)Math.Sqrt(K.Noise(seed,200+k)),r2=K.Noise(seed,300+k);var kp=ka*(1-r1)+kb*(r1*(1-r2))+kc*(r1*r2);
                float dist=gather?(kp-hotspot).sqrMagnitude:0f;if(dist<best){best=dist;a=ka;b=kb;c=kc;p=kp;}
            }
            var normal=Vector3.Cross(b-a,c-a);if(normal.sqrMagnitude<1e-12f)return false;normal.Normalize();
            Vector3 radial;
            if(s.AxisShell){var pa=s.ToAircraft.MultiplyPoint3x4(p);radial=s.FromAircraft.MultiplyVector(new Vector3(pa.x,pa.y-4.2f,0f));}
            else radial=p-s.HealthBounds.center; // wings, tail, movable surfaces: away from the section's own centre
            if(Vector3.Dot(normal,radial)<0f)normal=-normal;
            var tangent=Vector3.Cross(normal,Math.Abs(normal.y)<.8f?Vector3.up:Vector3.right).normalized;
            tangent=Quaternion.AngleAxis(K.Noise(seed,4)*360f,normal)*tangent;var bitangent=Vector3.Cross(normal,tangent);
            // The Courier is roughly twice the Darkreach's size; stock holes (~50 cm2) read as pinpricks on it
            // (flight-health1-20261124 hp60), so clusters are sized for the same on-screen look at the same framing.
            float grow=1f+.45f*Mathf.Clamp01((60f-hp)/60f);
            // heavy-tailed sizes: mostly small specks, some medium, a few large torn openings (stock masks)
            float q=K.Noise(seed,5);float radius=Math.Min((.024f+.035f*q+.24f*(float)Math.Pow(q,6))*grow,Math.Max(.03f,profile.tearMaxRadiusM));
            // Stock tears are jagged and often elongated, not round blobs (flight-health11-20261124 vs ref18): most
            // clusters stay near-round, a quarter stretch past 2x along a random direction.
            float sq=K.Noise(seed,6);float stretch=1f+.3f*sq+1.9f*sq*sq*sq;float depth=profile.tearDepthM+radius*.16f;
            Add(s,id,profile,p,normal,tangent,radius,stretch,depth,seed,Mathf.PI*radius*radius*stretch);
            int satellites=Math.Min(7,1+(int)(K.Noise(seed,7)*2.99f)+(int)(radius/.06f));if(!gather)s.Hotspots.Add(p);
            for(int k=0;k<satellites;k++)
            {
                // Satellites trail along the tear's long axis (ragged chains like the stock masks) and every other one
                // is a detached speck further out, so edges read as torn rather than as a rosette of discs.
                bool speck=(k&1)==1;float side=K.Noise(seed,60+k)<.5f?-1f:1f;
                float ang=(side<0f?Mathf.PI:0f)+(K.Noise(seed,10+k)-.5f)*(speck?2.4f:1.1f);
                float dist=radius*(speck?1.8f+2.2f*K.Noise(seed,20+k):(.9f+.8f*K.Noise(seed,20+k))*(float)Math.Sqrt(stretch));
                float sr=radius*(speck?.12f+.16f*K.Noise(seed,30+k):.3f+.3f*K.Noise(seed,30+k));
                var center=p+(tangent*(float)Math.Cos(ang)+bitangent*(float)Math.Sin(ang))*dist;
                var dir=Quaternion.AngleAxis((K.Noise(seed,40+k)-.5f)*(speck?360f:50f),normal)*tangent;
                Add(s,id,profile,center,normal,dir,sr,speck?1f+.8f*K.Noise(seed,50+k):1.2f+1.2f*K.Noise(seed,50+k),profile.tearDepthM+sr*.16f,seed*7+k+1,Mathf.PI*sr*sr*.6f);
            }
            s.DistributedCount++;DistributedHoles++;return true;
        }
        private static void Add(Surface s,string id,FreshPartProfile profile,Vector3 center,Vector3 axis,Vector3 longitudinal,float radius,float stretch,float depth,int seed,float area)
        {
            var wound=K.Prepare(center,axis,longitudinal,radius,stretch,depth,7,seed); // 7 sides keep the clipped mesh growth bounded
            s.Operations.Add(new Operation{SourceId=id,Kind=0,Center=center,Axis=axis,Wound=wound,Profile=profile,Seed=seed,Area=area,Radius=radius*(float)Math.Sqrt(stretch),Distributed=true});
        }
        internal void Hit(AeroPart owner,Vector3 point,Vector3 axis,float loss,int kind,int seed,FreshPartProfile profile)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;if(!owners.TryGetValue(id,out var group))return;
            if(axis.sqrMagnitude<.01f)axis=owner.transform.forward;axis.Normalize();
            // Owner report (0.1.9): a few .50 hits stripped whole panels down to
            // the frames. Native HP loss of a 12.7 mm penetration is ~24 on a
            // skin section, which the profile scale turned into a ~900 cm2 tear
            // (fragments x2.4, ~4400 cm2). Stock and MRB-100 airframes show
            // ~50 cm2 typical holes, ~5 holes/m2 and ~17-19 % open skin at 0 %
            // health. Ballistic holes are now small and round and grow only for
            // cannon-class losses; contact tears keep the profile formula, and
            // ballistic holes stop opening a surface past ~19 % of its area.
            float root=(float)Math.Sqrt(loss),radius,stretch=profile.tearStretch;
            if(kind==1||kind==2)
            {
                radius=(kind==1?.022f+.0045f*root:.018f+.0035f*root)+.02f*Math.Max(0f,root-8f);
                radius=Mathf.Clamp(radius,.015f,Math.Min(kind==1?.35f:.12f,profile.tearMaxRadiusM));stretch=Math.Min(stretch,1.25f);
            }
            else radius=Mathf.Clamp(profile.tearMinRadiusM+profile.tearEnergyScale*root*1.7f,profile.tearMinRadiusM,profile.tearMaxRadiusM);
            float area=Mathf.PI*radius*radius*stretch;
            foreach(var s in group)if(s.Meta&&s.Filter&&!s.Meta.FixedReceiver&&!s.Meta.BakedInterior)
            {
                var bounds=s.Renderer.bounds;bounds.Expand(radius*stretch*2+profile.tearDepthM);if(!bounds.Contains(point))continue;
                if(kind==1||kind==2){if(s.SkinArea<0)s.SkinArea=SurfaceArea(s.Original);if(s.OpenArea+area>OpenLimit*s.SkinArea){CappedHoles++;continue;}}
                var center=s.Filter.transform.InverseTransformPoint(point);var localAxis=s.Filter.transform.InverseTransformDirection(axis);
                var longitudinal=s.Filter.transform.InverseTransformDirection(owner.transform.TransformDirection(NativePartitionIntegration.V(profile.seamNormal)));
                var wound=K.Prepare(center,localAxis,longitudinal,radius,stretch,profile.tearDepthM+radius*.16f,profile.rimSides,seed*917+K.StableHash(id));
                s.Operations.Add(new Operation{SourceId=id,Kind=0,Center=center,Axis=localAxis,Wound=wound,Profile=profile,Seed=seed,Area=area,Radius=radius*(float)Math.Sqrt(stretch)});Schedule(s);
            }
        }
        private const float OpenLimit=.19f;internal int CappedHoles;
        private static float SurfaceArea(Mesh m)
        {
            if(!m||!m.isReadable)return 1f;var v=m.vertices;var t=m.triangles;double a=0;
            for(int i=0;i<t.Length;i+=3)a+=Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]).magnitude*.5;
            return Math.Max(.05f,(float)a);
        }
        internal void Seam(AeroPart owner,Vector3 point,Vector3 axis,FreshPartProfile profile,int direction,string sourceId)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;if(!owners.TryGetValue(id,out var group))return;
            if(id==sourceId)graph.Presentation?.Health(id,3);
            foreach(var s in group)if(s.Meta&&s.Filter&&!s.Meta.FixedReceiver&&!s.Meta.BakedInterior)
            {
                var center=s.Filter.transform.InverseTransformPoint(point);var normal=s.Filter.transform.InverseTransformDirection(axis).normalized;
                var bounds=s.Original.bounds;float distance=Vector3.Dot(bounds.center-center,normal),extent=Vector3.Dot(bounds.extents,new Vector3(Math.Abs(normal.x),Math.Abs(normal.y),Math.Abs(normal.z)));
                if(Math.Abs(distance)>extent+Math.Max(.035f,profile.seamRaggednessM))continue;
                s.Operations.Add(new Operation{SourceId=sourceId,Kind=1,Center=center,Axis=normal,Profile=profile,Direction=direction,Seed=++sequence});Schedule(s);
            }
        }
        internal void Repair(string id)
        {
            stages.Remove(id);
            graph.Presentation?.Health(id,0);
            foreach(var s in surfaces)if(s.Meta&&(s.Meta.PartId==id||s.Operations.Any(o=>o.SourceId==id)))
            {
                if(s.Meta.BakedInterior){Tint(s,0);continue;}
                s.Operations.RemoveAll(o=>o.SourceId==id);if(s.Meta.PartId==id){s.DistributedCount=0;s.Target=0;s.Hotspots.Clear();topUp.Remove(s);}Reset(s);
                if(s.Meta.Fresh){int stage;stages.TryGetValue(s.Meta.PartId,out stage);Tint(s,stage);}
            }
        }
        private static void Tint(Surface s,int stage)
        {
            if(!s.Meta.BakedInterior)stage=0; // skins: soot decals replace the old uniform darkening
            var block=new MaterialPropertyBlock();s.Renderer.GetPropertyBlock(block);
            var material=s.Renderer.sharedMaterial;var baseline=material&&material.HasProperty("_BaseColor")?material.GetColor("_BaseColor"):Color.white;
            block.SetColor("_BaseColor",Color.Lerp(baseline,new Color(baseline.r*.28f,baseline.g*.23f,baseline.b*.18f,baseline.a),stage/3f));s.Renderer.SetPropertyBlock(block);
        }
        internal void Paint(int paintChannel)
        {
            channel=paintChannel;
            // Layout changes do not rebuild geometry or discard wounds.
            foreach(var s in surfaces)if(s.Meta.Painted&&s.Changed)Layout(s.Changed,channel,s.BaseUv);
            foreach(var s in surfaces)if(!s.Changed&&!s.Meta.BakedInterior)s.Filter.sharedMesh=BaseMesh(s);
        }
        private Mesh BaseMesh(Surface s){var original=s.Meta.Painted&&channel==1&&s.Meta.Paint1?s.Meta.Paint1:s.Meta.Painted&&channel==2&&s.Meta.Paint2?s.Meta.Paint2:s.Original;return graph.Presentation?graph.Presentation.PaintMesh(s.Filter,channel,original):original;}
        internal void QualityChanged()
        {
            if(!graph.Presentation)return;
            foreach(var s in surfaces)if(!s.Meta.BakedInterior)
            {
                var mesh=graph.Presentation.SourceMesh(s.Filter,s.Meta.Original);if(mesh!=s.Original||s.Operations.Count>0){s.Original=mesh;s.TriangleCount=(int)mesh.GetIndexCount(0)/3;Reset(s);}
                if(s.Operations.Count==0)s.Filter.sharedMesh=BaseMesh(s);
            }
        }
        internal void Step()
        {
            if(preparation==1&&(preparationOwner==null||preparationOwner.disposed||!preparationOwner.graph)){preparation=0;preparationOwner=null;}
            if(disposed||preparation==1)return;watch.Restart();
            double budget=graph.Presentation&&graph.Presentation.Available?graph.Presentation.WorkBudgetMs:FrameBudgetMs;
            while(watch.Elapsed.TotalMilliseconds<budget)
            {
                if(active==null)
                {
                    if(topUp.Count>0&&watch.Elapsed.TotalMilliseconds<budget*.5&&TopUp(budget*.5))continue;
                    if(queue.Count==0)break;var s=queue.Dequeue();queued.Remove(s);
                    if(!s.Filter||!s.Meta)continue;
                    // Keep events for hidden/distant surfaces, but spend mesh
                    // time only when the owner can actually show the result.
                    // Empty repair transactions still restore their mesh now.
                    if(!s.Renderer.enabled&&s.Operations.Count>0)continue;
                    active=Process(s);
                }
                if(!active.MoveNext()){(active as IDisposable)?.Dispose();active=null;ReleaseStaged();}
            }
            watch.Stop();double elapsed=watch.Elapsed.TotalMilliseconds;if(elapsed>PeakStepMs){PeakStepMs=elapsed;PeakStepPhase=phase;}TotalStepMs+=elapsed;
        }
        internal void SyncVisibility()
        {
            foreach(var s in surfaces)
            {
                if(s.Renderer&&s.Renderer.enabled&&NeedsWork(s))Schedule(s);
                if(s.LipObject){var r=s.LipObject.GetComponent<MeshRenderer>();r.enabled=s.Renderer&&s.Renderer.enabled;r.gameObject.layer=s.Filter.gameObject.layer;}
                if(s.SootObject){var r=s.SootObject.GetComponent<MeshRenderer>();r.enabled=s.Renderer&&s.Renderer.enabled;r.gameObject.layer=s.Filter.gameObject.layer;}
            }
        }
        private static bool Current(Surface s,int epoch)=>s.Filter&&s.Meta&&s.Epoch==epoch;
        // Background work (24/11/2026): with the 0.35-1.0 ms/frame visual budget, a few hundred distributed health
        // clusters took minutes to appear when every face pass ran on the main thread (flight-health2-20261124).
        // Pure geometry - face decode, wound clipping, rims, vertex de-duplication and soot - now runs on a worker
        // thread; only Mesh API calls stay on the main thread, still spread over frames. Unity extern helpers
        // (Bounds, Quaternion) are not used inside the worker code.
        private static IEnumerator Wait(System.Threading.Tasks.Task task,string label)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsFaulted)CurrentAirframeDamagePlugin.Log.LogWarning("Courier damage visuals: "+label+" failed: "+task.Exception?.GetBaseException().Message);
        }
        private static float SqrDistance(K.Triangle t,Vector3 c)
        {
            float d=0f;
            float lo=Math.Min(t.A.Position.x,Math.Min(t.B.Position.x,t.C.Position.x)),hi=Math.Max(t.A.Position.x,Math.Max(t.B.Position.x,t.C.Position.x));if(c.x<lo)d+=(lo-c.x)*(lo-c.x);else if(c.x>hi)d+=(c.x-hi)*(c.x-hi);
            lo=Math.Min(t.A.Position.y,Math.Min(t.B.Position.y,t.C.Position.y));hi=Math.Max(t.A.Position.y,Math.Max(t.B.Position.y,t.C.Position.y));if(c.y<lo)d+=(lo-c.y)*(lo-c.y);else if(c.y>hi)d+=(c.y-hi)*(c.y-hi);
            lo=Math.Min(t.A.Position.z,Math.Min(t.B.Position.z,t.C.Position.z));hi=Math.Max(t.A.Position.z,Math.Max(t.B.Position.z,t.C.Position.z));if(c.z<lo)d+=(lo-c.z)*(lo-c.z);else if(c.z>hi)d+=(c.z-hi)*(c.z-hi);
            return d;
        }
        private IEnumerator Process(Surface s)
        {
            int batch=graph.Presentation&&graph.Presentation.Available?graph.Presentation.WorkBatch:Batch;
            int epoch=s.Epoch;bool rebuild=s.Rebuild||s.Faces==null;int start=rebuild?0:s.Applied;
            var ops=s.Operations.Take(Math.Min(s.Operations.Count,start+256)).ToArray();if(!rebuild&&start>=ops.Length)yield break;
            if(ops.Length==0)
            {
                s.Filter.sharedMesh=BaseMesh(s);ReleaseChanged(s);ReleaseLips(s);s.Faces=null;s.Lips=null;s.Applied=0;s.Rebuild=false;Commits++;yield break;
            }
            var faces=rebuild?null:s.Faces;var lips=new TriangleBuffer();if(!rebuild&&s.Lips!=null)for(int i=0;i<s.Lips.Count;i++){lips.Add(s.Lips[i]);if(i%batch==0)yield return null;}
            if(faces==null)
            {
                var m=s.Original;phase="read positions";var positions=m.vertices;yield return null;phase="read normals";var normals=m.normals;yield return null;phase="read tangents";var tangents=m.tangents;yield return null;
                var uv=new Vector2[4][];for(int channel=0;channel<4;channel++){phase="read UV "+channel;uv[channel]=channel==0?m.uv:channel==1?m.uv2:channel==2?m.uv3:m.uv4;yield return null;}
                phase="read indices";var indices=m.triangles;yield return null;var decoded=new TriangleBuffer();phase="decode source faces";
                var decode=System.Threading.Tasks.Task.Run(()=>{for(int i=0;i<indices.Length;i+=3)decoded.Add(new K.Triangle(Read(indices[i],positions,normals,tangents,uv),Read(indices[i+1],positions,normals,tangents,uv),Read(indices[i+2],positions,normals,tangents,uv)));});
                var w=Wait(decode,"decode");while(w.MoveNext())yield return null;
                if(decode.IsFaulted||!Current(s,epoch)){Cancelled++;yield break;}
                faces=decoded;
            }
            bool changed=rebuild&&s.Changed;
            int rimLimit=graph.Presentation&&graph.Presentation.Available?graph.Presentation.RimBudget:LipBudget;
            for(int oi=start;oi<ops.Length;oi++)
            {
                if(!Current(s,epoch)){Cancelled++;yield break;}var op=ops[oi];var kept=new TriangleBuffer();var edges=new List<K.Edge>();bool modified=false;phase="clip / seam faces";
                if(op.Kind==0)
                {
                    int end=oi;while(end<ops.Length&&ops[end].Kind==0)end++;
                    var run=ops.Skip(oi).Take(end-oi).ToArray();var current=faces;int noOps=0,rejected=0;bool any=false;
                    int growthLimit=s.TriangleCount*2+512,distributedLimit=s.TriangleCount*12+12288;bool distributedSurface=s.DistributedCount>0;
                    var clip=System.Threading.Tasks.Task.Run(()=>
                    {
                        foreach(var o in run)
                        {
                            var keep=new TriangleBuffer();var rimEdges=new List<K.Edge>();bool mod=false;
                            float reach=Math.Max(o.Profile.tearMaxRadiusM*(o.Profile.tearStretch+1),o.Radius*3f)+o.Profile.tearDepthM;
                            for(int i=0;i<current.Count;i++){var t=current[i];if(SqrDistance(t,o.Center)>reach*reach)keep.Add(t);else mod|=K.SubtractIfRemoved(t,o.Wound,keep,rimEdges);}
                            if(!mod)noOps++;
                            // distributed health damage needs room for a few hundred small clusters per skin surface
                            if(keep.Count>(o.Distributed||distributedSurface?distributedLimit:growthLimit)){rejected++;mod=false;o.Rejected=true;}
                            if(mod){current=keep;any=true;var rim=AddLips(lips,rimEdges,o,rimLimit,int.MaxValue);while(rim.MoveNext()){}}
                        }
                    });
                    var w=Wait(clip,"clip");while(w.MoveNext())yield return null;
                    if(clip.IsFaulted||!Current(s,epoch)){Cancelled++;yield break;}
                    NoOpCuts+=noOps;RejectedGrowth+=rejected;if(any){faces=current;changed=true;}
                    oi=end-1;continue;
                }
                else if(op.Kind==1)
                {
                    float band=Math.Max(.035f,op.Profile.seamRaggednessM);var boundary=new Dictionary<(PointKey,PointKey),(K.Vertex A,K.Vertex B,int Count)>();
                    for(int i=0;i<faces.Count;i++)
                    {
                        var t=faces[i];Edge(boundary,t.A,t.B,op,band);Edge(boundary,t.B,t.C,op,band);Edge(boundary,t.C,t.A,op,band);
                        if(i%batch==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                    }
                    int edgeIndex=0;foreach(var edge in boundary.Values){if(edge.Count==1)edges.Add(new K.Edge(Shear(edge.A,op,band),Shear(edge.B,op,band)));if(++edgeIndex%batch==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}}
                    modified=edges.Count>0;
                    if(modified)for(int i=0;i<faces.Count;i++)
                    {
                        var t=faces[i];kept.Add(new K.Triangle(Shear(t.A,op,band),Shear(t.B,op,band),Shear(t.C,op,band)));
                        if(i%batch==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                    }
                }
                else
                {
                    var b=s.HealthBounds;var size=b.size;int axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;float span=Mathf.Max(.001f,size[axis]);float amount=Mathf.Min(.22f,op.Profile.seamRaggednessM*op.Stage);
                    for(int i=0;i<faces.Count;i++)
                    {
                        var t=faces[i];kept.Add(new K.Triangle(Warp(t.A,b,axis,span,amount),Warp(t.B,b,axis,span,amount),Warp(t.C,b,axis,span,amount)));
                        if(i%batch==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                    }
                    modified=true;
                }
                if(modified){faces=kept;changed=true;var rim=AddLips(lips,edges,op,rimLimit,batch);while(rim.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}}
            }
            if(!Current(s,epoch)){Cancelled++;yield break;}
            if(changed)
            {
                // Soot only once this surface's queue drains, so long bursts do not rebuild it on every pass.
                bool drained=s.Operations.Count<=ops.Length;var aft=s.Filter.transform.InverseTransformDirection(-graph.transform.forward);
                MeshData faceData=null,lipData=null;SootData sootData=null;var finalFaces=faces;
                phase="prepare damage mesh";
                var prepare=System.Threading.Tasks.Task.Run(()=>{faceData=Arrays(finalFaces);if(lips.Count>0)lipData=Arrays(lips);if(drained)sootData=ComputeSoot(finalFaces,ops,aft);});
                var w=Wait(prepare,"prepare");while(w.MoveNext())yield return null;
                if(prepare.IsFaulted||!Current(s,epoch)){Cancelled++;yield break;}
                // OwnedAirframePaint retains generated damage meshes by this
                // established prefix when refreshing every painted section.
                phase="build damage mesh";Mesh mesh=null;var build=Assign(faceData,"OWN Courier bounded rupture "+s.Meta.PartId,m=>stagedMesh=mesh=m);
                while(build.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                Mesh lip=null;
                if(lipData!=null){build=Assign(lipData,"Courier bounded tear rim",m=>stagedLip=lip=m);while(build.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}}
                if(!Current(s,epoch)){Cancelled++;yield break;}
                phase="publish damage mesh";s.BaseUv=mesh.uv;if(s.Meta.Painted)Layout(mesh,channel,s.BaseUv);
                ReleaseChanged(s);s.Changed=mesh;s.Filter.sharedMesh=mesh;ChangedSurfaces++;
                var keepSoot=s.SootObject;var keepSootMesh=s.SootMesh;s.SootObject=null;s.SootMesh=null; // ReleaseLips must not drop soot here
                if(lip)
                {
                    if(!s.LipObject){s.LipObject=new GameObject("Courier section tear rims",typeof(MeshFilter),typeof(MeshRenderer));s.LipObject.transform.SetParent(s.Filter.transform,false);var r=s.LipObject.GetComponent<MeshRenderer>();r.sharedMaterial=lipMaterial;r.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;}
                    if(s.LipMesh){GeneratedLipTriangles-=(int)s.LipMesh.GetIndexCount(0)/3;Object.Destroy(s.LipMesh);}s.LipMesh=lip;s.LipObject.GetComponent<MeshFilter>().sharedMesh=lip;GeneratedLipTriangles+=lips.Count;
                }
                else ReleaseLips(s);
                s.SootObject=keepSoot;s.SootMesh=keepSootMesh;
                if(drained){phase="build soot";PublishSoot(s,sootData!=null?SootMesh(sootData,s.Meta.PartId):null);}
                stagedMesh=stagedLip=null;Commits++;
            }
            s.Faces=faces;s.Lips=lips;s.Applied=ops.Length;s.Rebuild=false;
            if(s.Operations.Count>s.Applied)Schedule(s);
        }
        private static K.Vertex Read(int i,Vector3[] p,Vector3[] n,Vector4[] t,Vector2[][] u)=>new K.Vertex{Position=p[i],Normal=n.Length>i?n[i]:Vector3.up,Tangent=t.Length>i?t[i]:new Vector4(1,0,0,1),U0=u[0].Length>i?u[0][i]:Vector2.zero,U1=u[1].Length>i?u[1][i]:Vector2.zero,U2=u[2].Length>i?u[2][i]:Vector2.zero,U3=u[3].Length>i?u[3][i]:Vector2.zero};
        private static K.Vertex Warp(K.Vertex v,Bounds b,int axis,float span,float amount){var p=v.Position;p[(axis+1)%3]+=Mathf.Sin(Mathf.Clamp01((p[axis]-b.min[axis])/span)*Mathf.PI)*amount;v.Position=p;return v;}
        private static K.Vertex Shear(K.Vertex v,Operation op,float band){if(Math.Abs(Vector3.Dot(v.Position-op.Center,op.Axis))<=band)v.Position+=op.Axis*(op.Direction*op.Profile.seamRaggednessM*(.2f+.8f*K.Noise(K.StableHash(op.SourceId),Key(v.Position).GetHashCode())));return v;}
        private readonly struct PointKey:IEquatable<PointKey>,IComparable<PointKey>
        {
            private readonly long x,y,z;
            internal PointKey(Vector3 p){x=(long)Math.Round(p.x*100000d);y=(long)Math.Round(p.y*100000d);z=(long)Math.Round(p.z*100000d);}
            public bool Equals(PointKey other)=>x==other.x&&y==other.y&&z==other.z;
            public override bool Equals(object other)=>other is PointKey key&&Equals(key);
            public override int GetHashCode(){unchecked{int h=x.GetHashCode();h=h*31+y.GetHashCode();return h*31+z.GetHashCode();}}
            public int CompareTo(PointKey other){int c=x.CompareTo(other.x);if(c!=0)return c;c=y.CompareTo(other.y);return c!=0?c:z.CompareTo(other.z);}
        }
        private static PointKey Key(Vector3 p)=>new PointKey(p);
        private static (PointKey,PointKey) EdgeKey(Vector3 a,Vector3 b){var ka=Key(a);var kb=Key(b);return ka.CompareTo(kb)<0?(ka,kb):(kb,ka);}
        private static void Edge(Dictionary<(PointKey,PointKey),(K.Vertex A,K.Vertex B,int Count)> edges,K.Vertex a,K.Vertex b,Operation op,float band)
        {
            if(Math.Abs(Vector3.Dot(a.Position-op.Center,op.Axis))>band||Math.Abs(Vector3.Dot(b.Position-op.Center,op.Axis))>band)return;
            var key=EdgeKey(a.Position,b.Position);
            if(edges.TryGetValue(key,out var prior))edges[key]=(prior.A,prior.B,prior.Count+1);else edges[key]=(a,b,1);
        }
        private static IEnumerator AddLips(TriangleBuffer faces,List<K.Edge> edges,Operation op,int limit,int batch)
        {
            int i=0;var seen=new HashSet<(PointKey,PointKey)>();var axis=op.Axis*(op.Kind==1?op.Direction:1);
            foreach(var e in edges)
            {
                if(faces.Count+2>limit)break;if(!seen.Add(EdgeKey(e.A.Position,e.B.Position)))continue;
                var c=e.A;var d=e.B;var inward=Vector3.Cross(axis,d.Position-c.Position).normalized;float bend=op.Profile.lipWidthM*(.4f+K.Noise(op.Seed,i++));c.Position+=inward*op.Profile.lipWidthM-axis*bend;d.Position+=inward*op.Profile.lipWidthM-axis*bend;faces.Add(new K.Triangle(e.A,e.B,d));faces.Add(new K.Triangle(e.A,d,c));
                if(i%batch==0)yield return null;
            }
        }
        private sealed class VertexComparer:IEqualityComparer<K.Vertex>
        {
            public bool Equals(K.Vertex a,K.Vertex b)=>a.Position.Equals(b.Position)&&a.Normal.Equals(b.Normal)&&a.Tangent.Equals(b.Tangent)&&a.U0.Equals(b.U0)&&a.U1.Equals(b.U1)&&a.U2.Equals(b.U2)&&a.U3.Equals(b.U3);
            public int GetHashCode(K.Vertex v){unchecked{int h=v.Position.GetHashCode();h=h*31+v.Normal.GetHashCode();h=h*31+v.Tangent.GetHashCode();h=h*31+v.U0.GetHashCode();h=h*31+v.U1.GetHashCode();h=h*31+v.U2.GetHashCode();return h*31+v.U3.GetHashCode();}}
        }
        private sealed class MeshData {internal Vector3[] P,N;internal Vector4[] T;internal Vector2[][] UV;internal List<int> I;}
        private static MeshData Arrays(TriangleBuffer faces)
        {
            var lookup=new Dictionary<K.Vertex,int>(new VertexComparer());var vertices=new List<K.Vertex>();var indices=new List<int>(faces.Count*3);
            for(int i=0;i<faces.Count;i++){var t=faces[i];Index(t.A,lookup,vertices,indices);Index(t.B,lookup,vertices,indices);Index(t.C,lookup,vertices,indices);}
            int count=vertices.Count;var d=new MeshData{P=new Vector3[count],N=new Vector3[count],T=new Vector4[count],UV=new[]{new Vector2[count],new Vector2[count],new Vector2[count],new Vector2[count]},I=indices};
            for(int i=0;i<count;i++){var v=vertices[i];d.P[i]=v.Position;var n=v.Normal;float l=n.magnitude;d.N[i]=l>1e-8f?n/l:Vector3.up;d.T[i]=v.Tangent;d.UV[0][i]=v.U0;d.UV[1][i]=v.U1;d.UV[2][i]=v.U2;d.UV[3][i]=v.U3;}
            return d;
        }
        private static IEnumerator Assign(MeshData d,string name,Action<Mesh> result)
        {
            var mesh=new Mesh{name=name,indexFormat=d.P.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};
            // Cancellation owns the staged resource from its first yield.
            result(mesh);yield return null;mesh.vertices=d.P;yield return null;mesh.normals=d.N;yield return null;mesh.tangents=d.T;yield return null;
            for(int k=0;k<4;k++){mesh.SetUVs(k,d.UV[k]);yield return null;}mesh.SetTriangles(d.I,0);yield return null;mesh.RecalculateBounds();
        }
        private static void Index(K.Vertex v,Dictionary<K.Vertex,int> lookup,List<K.Vertex> vertices,List<int> indices){if(!lookup.TryGetValue(v,out int index)){index=vertices.Count;lookup.Add(v,index);vertices.Add(v);}indices.Add(index);}
        // A triangle stores three complete vertex records. Allocating an
        // entire section in one List<T> produced a measured 6.6 ms pause.
        // Grow by 128 triangles (~27 KiB), between the existing yields.
        private sealed class TriangleBuffer:IList<K.Triangle>
        {
            private const int Shift=7,Size=1<<Shift,Mask=Size-1;
            private readonly List<K.Triangle[]> chunks=new List<K.Triangle[]>();
            public int Count{get;private set;}public bool IsReadOnly=>false;
            public K.Triangle this[int index]{get=>chunks[index>>Shift][index&Mask];set=>chunks[index>>Shift][index&Mask]=value;}
            public void Add(K.Triangle value){int chunk=Count>>Shift;if(chunk==chunks.Count)chunks.Add(new K.Triangle[Size]);chunks[chunk][Count&Mask]=value;Count++;}
            public void Clear(){Count=0;chunks.Clear();}
            public void RemoveAt(int index){if(index!=Count-1)throw new NotSupportedException("Only tail truncation is used by the rupture kernel.");Count--;chunks[index>>Shift][index&Mask]=default(K.Triangle);}
            public int IndexOf(K.Triangle value){for(int i=0;i<Count;i++)if(this[i].Equals(value))return i;return -1;}
            public bool Contains(K.Triangle value)=>IndexOf(value)>=0;
            public bool Remove(K.Triangle value){int i=IndexOf(value);if(i<0)return false;RemoveAt(i);return true;}
            public void Insert(int index,K.Triangle value){if(index!=Count)throw new NotSupportedException();Add(value);}
            public void CopyTo(K.Triangle[] array,int index){for(int i=0;i<Count;i++)array[index+i]=this[i];}
            public IEnumerator<K.Triangle> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
            IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        private static void Layout(Mesh mesh,int paintChannel,Vector2[] baseUv)
        {
            mesh.SetUVs(0,paintChannel==1?mesh.uv2:paintChannel==2?mesh.uv3:baseUv);
        }
        // Soot: for every applied hole, the remaining skin triangles around it are copied 3 mm out along the normal with a
        // planar projection of a shared soot blot (dark core round the hole, streaks trailing aft with the airflow).
        // Built from the already-cut faces, so soot never floats over open holes. Transparent particle-unlit material
        // cloned from the aircraft's own spark material, so the shader variant is known to exist in the game build.
        private const int SootTriangleLimit=2500;private const float SootFront=1.4f,SootBack=5.0f,SootHalfWidth=1.8f,SootLift=.003f;
        internal int SootTriangles;
        private static Material sootMaterial;private static Texture2D sootTexture;
        private static Material SootMaterial()
        {
            if(sootMaterial)return sootMaterial;
            // First attempt (particle-unlit transparent, flight-health1-20261124) produced soot meshes that never showed.
            // Use the aircraft's own alpha-tested URP Lit material instead (jet_mechanical: _ALPHATEST_ON variant is in
            // the build): lit, shadow-receiving, hard but noisy edges that read as streaky soot like the stock masks.
            var template=Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m=>m&&m.name=="jet_mechanical"&&m.shader&&m.shader.name=="Universal Render Pipeline/Lit"&&m.IsKeywordEnabled("_ALPHATEST_ON"));
            if(!template)return null;
            sootMaterial=new Material(template){name="Courier soot",hideFlags=HideFlags.DontUnloadUnusedAsset};
            sootMaterial.SetTexture("_BaseMap",SootTexture());if(sootMaterial.HasProperty("_MainTex"))sootMaterial.SetTexture("_MainTex",SootTexture());
            sootMaterial.SetTextureScale("_BaseMap",Vector2.one);sootMaterial.SetTextureOffset("_BaseMap",Vector2.zero);
            var grime=new Color(.15f,.14f,.13f,1f);sootMaterial.SetColor("_BaseColor",grime);if(sootMaterial.HasProperty("_Color"))sootMaterial.SetColor("_Color",grime);
            if(sootMaterial.HasProperty("_MetallicGlossMap"))sootMaterial.SetTexture("_MetallicGlossMap",Texture2D.blackTexture);
            if(sootMaterial.HasProperty("_OcclusionMap"))sootMaterial.SetTexture("_OcclusionMap",Texture2D.whiteTexture);
            if(sootMaterial.HasProperty("_BumpMap"))sootMaterial.SetTexture("_BumpMap",null);
            if(sootMaterial.HasProperty("_Smoothness"))sootMaterial.SetFloat("_Smoothness",.05f);if(sootMaterial.HasProperty("_GlossMapScale"))sootMaterial.SetFloat("_GlossMapScale",.05f);
            if(sootMaterial.HasProperty("_Cutoff"))sootMaterial.SetFloat("_Cutoff",.42f);
            if(sootMaterial.HasProperty("_Cull"))sootMaterial.SetFloat("_Cull",0f);
            return sootMaterial;
        }
        private static Texture2D SootTexture()
        {
            if(sootTexture)return sootTexture;
            const int W=192,H=96;var px=new Color32[W*H];float u0=SootFront/(SootFront+SootBack);
            for(int y=0;y<H;y++)for(int x=0;x<W;x++)
            {
                float u=(x+.5f)/W,v=(y+.5f)/H;float du=(u-u0)*(SootFront+SootBack),dy=(v-.5f)*2f*SootHalfWidth;
                float rho=(float)Math.Sqrt(du*du+dy*dy);
                float core=Mathf.Clamp01((2.7f-rho)/1.7f);core*=core*(3f-2f*core);
                float streak=0f;
                if(du>0f){float w=.9f+.28f*du;float across=Mathf.Clamp01(1f-Math.Abs(dy)/w);
                    float bands=.5f+.5f*(float)Math.Sin(dy*7.3f+(float)Math.Sin(dy*2.1f)*2f)*(.6f+.4f*(float)Math.Sin(dy*17f));
                    streak=(float)Math.Exp(-du/3.2f)*across*(.45f+.55f*bands);}
                float grain=.5f+.5f*K.Noise(4242,(y/2)*W+x/2);
                float a=Math.Max(core*.95f,streak*1.15f)*grain;
                float edge=Mathf.Clamp01(Math.Min(Math.Min(x,W-1-x),Math.Min(y,H-1-y))/4f);a*=edge;
                px[y*W+x]=new Color32(255,255,255,(byte)Mathf.RoundToInt(Mathf.Clamp01(a)*255f));
            }
            sootTexture=new Texture2D(W,H,TextureFormat.RGBA32,false){name="Courier soot blot",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear,hideFlags=HideFlags.DontUnloadUnusedAsset};
            sootTexture.SetPixels32(px);sootTexture.Apply(false,true);return sootTexture;
        }
        private sealed class SootData {internal List<Vector3> P=new List<Vector3>(),Cavity=new List<Vector3>();internal List<Vector2> UV=new List<Vector2>();}
        private static Vector3 Rotate(Vector3 v,Vector3 n,float degrees){float r=degrees*Mathf.Deg2Rad,c=(float)Math.Cos(r),s=(float)Math.Sin(r);return v*c+Vector3.Cross(n,v)*s+n*(Vector3.Dot(n,v)*(1-c));}
        private static SootData ComputeSoot(TriangleBuffer faces,Operation[] ops,Vector3 aft)
        {
            var holes=ops.Where(o=>o.Kind==0&&o.Radius>0f&&!o.Rejected).ToArray();if(holes.Length==0||faces==null)return null;var d=new SootData();
            // Dark cavity behind every hole: stock holes read as dark voids, ours showed the lit interior as white
            // blobs (flight-health6-20261124). A 10-sided disc 8 mm inside the skin, facing out, 1.2x the hole radius.
            foreach(var h in holes)
            {
                var outN=(h.Distributed?h.Axis:-h.Axis).normalized;var u0=Vector3.Cross(outN,Math.Abs(outN.y)<.8f?Vector3.up:Vector3.right).normalized;var v0=Vector3.Cross(outN,u0);
                var c=h.Center-outN*.008f;float r=h.Radius*1.2f; // 8 mm: the light internal liner sits a few cm inside and hid a deeper disc (health8)
                for(int k=0;k<10;k++){float a0=k*.6283185f,a1=(k+1)*.6283185f;d.Cavity.Add(c);d.Cavity.Add(c+(u0*(float)Math.Cos(a1)+v0*(float)Math.Sin(a1))*r);d.Cavity.Add(c+(u0*(float)Math.Cos(a0)+v0*(float)Math.Sin(a0))*r);}
            }
            foreach(var h in holes)
            {
                var n=(h.Distributed?h.Axis:-h.Axis).normalized;var along=aft-n*Vector3.Dot(aft,n);if(along.sqrMagnitude<1e-6f)along=Vector3.Cross(n,Math.Abs(n.y)<.8f?Vector3.up:Vector3.right);
                along=Rotate(along.normalized,n,(K.Noise(h.Seed,61)-.5f)*30f);var side=Vector3.Cross(n,along);
                float R=h.Radius*(.85f+.5f*K.Noise(h.Seed,62)),front=SootFront*R,length=(SootFront+SootBack)*R,half=SootHalfWidth*R,reach=Math.Max(length,2*half)+.05f;
                for(int i=0;i<faces.Count;i++)
                {
                    var t=faces[i];
                    if((t.A.Position-h.Center).sqrMagnitude>reach*reach&&(t.B.Position-h.Center).sqrMagnitude>reach*reach&&(t.C.Position-h.Center).sqrMagnitude>reach*reach)continue;
                    Vector2 a=SootUv(t.A.Position,h.Center,along,side,front,length,half),b=SootUv(t.B.Position,h.Center,along,side,front,length,half),c=SootUv(t.C.Position,h.Center,along,side,front,length,half);
                    if(a.x<0&&b.x<0&&c.x<0||a.x>1&&b.x>1&&c.x>1||a.y<0&&b.y<0&&c.y<0||a.y>1&&b.y>1&&c.y>1)continue;
                    var fn=Vector3.Cross(t.B.Position-t.A.Position,t.C.Position-t.A.Position);float fl=fn.magnitude;if(fl<1e-7f)continue;fn/=fl;
                    float facing=Vector3.Dot(fn,n);if(Math.Abs(facing)<.35f)continue; // rims and faces turned edge-on
                    var lift=(facing>=0f?fn:-fn)*SootLift;
                    d.P.Add(t.A.Position+lift);d.P.Add(t.B.Position+lift);d.P.Add(t.C.Position+lift);d.UV.Add(a);d.UV.Add(b);d.UV.Add(c);
                    if(d.P.Count/3>=SootTriangleLimit)return d;
                }
            }
            return d.P.Count>0||d.Cavity.Count>0?d:null;
        }
        private static Vector2 SootUv(Vector3 p,Vector3 center,Vector3 along,Vector3 side,float front,float length,float half){var d=p-center;return new Vector2((Vector3.Dot(d,along)+front)/length,Vector3.Dot(d,side)/(2*half)+.5f);}
        private static Mesh SootMesh(SootData d,string id)
        {
            int total=d.P.Count+d.Cavity.Count;
            var mesh=new Mesh{name="Courier soot "+id,indexFormat=total>65000?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16,subMeshCount=2};
            var vertices=new List<Vector3>(total);vertices.AddRange(d.P);vertices.AddRange(d.Cavity);
            var normals=new List<Vector3>(total);for(int i=0;i<vertices.Count;i+=3){var n=Vector3.Cross(vertices[i+1]-vertices[i],vertices[i+2]-vertices[i]).normalized;normals.Add(n);normals.Add(n);normals.Add(n);}
            var uv=new List<Vector2>(total);uv.AddRange(d.UV);for(int i=0;i<d.Cavity.Count;i++)uv.Add(new Vector2(.5f,.5f));
            mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv);
            mesh.SetTriangles(Enumerable.Range(0,d.P.Count).ToArray(),0,false);mesh.SetTriangles(Enumerable.Range(d.P.Count,d.Cavity.Count).ToArray(),1,false);mesh.RecalculateBounds();return mesh;
        }
        private Material cavityMaterial;
        private Material CavityMaterial()
        {
            if(cavityMaterial)return cavityMaterial;
            cavityMaterial=new Material(lipMaterial){name="Courier hole cavity"};
            var dark=new Color(.028f,.025f,.023f,1f);if(cavityMaterial.HasProperty("_BaseColor"))cavityMaterial.SetColor("_BaseColor",dark);if(cavityMaterial.HasProperty("_Color"))cavityMaterial.SetColor("_Color",dark);
            if(cavityMaterial.HasProperty("_Smoothness"))cavityMaterial.SetFloat("_Smoothness",0f);foreach(var tex in new[]{"_BaseMap","_MainTex","_MetallicGlossMap","_SpecGlossMap","_OcclusionMap","_EmissionMap"})if(cavityMaterial.HasProperty(tex))cavityMaterial.SetTexture(tex,null);if(cavityMaterial.HasProperty("_Metallic"))cavityMaterial.SetFloat("_Metallic",0f);if(cavityMaterial.HasProperty("_GlossMapScale"))cavityMaterial.SetFloat("_GlossMapScale",0f);if(cavityMaterial.HasProperty("_EnvironmentReflections"))cavityMaterial.SetFloat("_EnvironmentReflections",0f);if(cavityMaterial.HasProperty("_SpecularHighlights"))cavityMaterial.SetFloat("_SpecularHighlights",0f);cavityMaterial.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");cavityMaterial.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");if(cavityMaterial.HasProperty("_Cull"))cavityMaterial.SetFloat("_Cull",0f);
            return cavityMaterial;
        }
        private void PublishSoot(Surface s,Mesh soot)
        {
            ReleaseSoot(s);if(!soot)return;var material=SootMaterial();if(!material){Object.Destroy(soot);return;}
            s.SootObject=new GameObject("Courier section soot",typeof(MeshFilter),typeof(MeshRenderer));s.SootObject.transform.SetParent(s.Filter.transform,false);s.SootObject.layer=s.Filter.gameObject.layer;
            var r=s.SootObject.GetComponent<MeshRenderer>();r.sharedMaterials=new[]{material,CavityMaterial()};r.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;r.receiveShadows=true;r.enabled=s.Renderer&&s.Renderer.enabled;
            s.SootMesh=soot;s.SootObject.GetComponent<MeshFilter>().sharedMesh=soot;SootTriangles+=(int)soot.GetIndexCount(0)/3;
        }
        private void ReleaseSoot(Surface s){if(s.SootMesh){SootTriangles-=(int)s.SootMesh.GetIndexCount(0)/3;Object.Destroy(s.SootMesh);}if(s.SootObject)Object.Destroy(s.SootObject);s.SootMesh=null;s.SootObject=null;}
        private void ReleaseChanged(Surface s){if(!s.Changed)return;Object.Destroy(s.Changed);s.Changed=null;ChangedSurfaces--;}
        private void ReleaseLips(Surface s){ReleaseSoot(s);if(s.LipMesh){GeneratedLipTriangles-=(int)s.LipMesh.GetIndexCount(0)/3;Object.Destroy(s.LipMesh);}if(s.LipObject)Object.Destroy(s.LipObject);s.LipMesh=null;s.LipObject=null;}
        private void ReleaseStaged(){if(stagedMesh)Object.Destroy(stagedMesh);if(stagedLip)Object.Destroy(stagedLip);stagedMesh=stagedLip=null;}
        public void Dispose(){disposed=true;(active as IDisposable)?.Dispose();active=null;ReleaseStaged();queue.Clear();queued.Clear();foreach(var s in surfaces){ReleaseChanged(s);ReleaseLips(s);}if(lipMaterial)Object.Destroy(lipMaterial);if(cavityMaterial)Object.Destroy(cavityMaterial);topUp.Clear();}
    }
}

