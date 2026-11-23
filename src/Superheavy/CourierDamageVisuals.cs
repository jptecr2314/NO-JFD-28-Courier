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
            internal Vector3 Center,Axis;internal K.Wound Wound;internal FreshPartProfile Profile;
        }
        private sealed class Surface
        {
            internal NativePartitionSurface Meta;internal MeshFilter Filter;internal MeshRenderer Renderer;
            internal Mesh Original,Changed,LipMesh;internal Vector2[] BaseUv;internal GameObject LipObject;
            internal Bounds HealthBounds;internal int TriangleCount;
            internal TriangleBuffer Faces,Lips;internal readonly List<Operation> Operations=new List<Operation>();
            internal int Epoch,Applied;internal bool Rebuild;
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
        internal double PeakStepMs,TotalStepMs;internal string PeakStepPhase;internal int Pending=>queue.Count+(active!=null?1:0);
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
        internal void Health(AeroPart owner,FreshPartProfile profile)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;
            int stage=owner.hitPoints>=95?0:owner.hitPoints>=65?1:owner.hitPoints>=35?2:3;
            if(stages.TryGetValue(id,out var old)&&old==stage)return;stages[id]=stage;
            graph.Presentation?.Health(id,stage);
            if(!owners.TryGetValue(id,out var group))return;
            foreach(var s in group)if(s.Meta.Fresh)
            {
                if(s.Meta.BakedInterior){Tint(s,stage);continue;}
                s.Operations.RemoveAll(o=>o.Kind==2);
                if(stage>0)s.Operations.Add(new Operation{SourceId=id,Kind=2,Stage=stage,Profile=profile});
                Reset(s);Tint(s,stage);
            }
        }
        internal void Hit(AeroPart owner,Vector3 point,Vector3 axis,float loss,int kind,int seed,FreshPartProfile profile)
        {
            string id=owner.GetComponent<NativePartitionPart>().Id;if(!owners.TryGetValue(id,out var group))return;
            if(axis.sqrMagnitude<.01f)axis=owner.transform.forward;axis.Normalize();
            float radius=Mathf.Clamp(profile.tearMinRadiusM+profile.tearEnergyScale*(float)Math.Sqrt(loss)*(kind==1?1f:kind==2?2.4f:1.7f),profile.tearMinRadiusM,profile.tearMaxRadiusM);
            foreach(var s in group)if(s.Meta&&s.Filter&&!s.Meta.FixedReceiver&&!s.Meta.BakedInterior)
            {
                var bounds=s.Renderer.bounds;bounds.Expand(radius*profile.tearStretch*2+profile.tearDepthM);if(!bounds.Contains(point))continue;
                var center=s.Filter.transform.InverseTransformPoint(point);var localAxis=s.Filter.transform.InverseTransformDirection(axis);
                var longitudinal=s.Filter.transform.InverseTransformDirection(owner.transform.TransformDirection(NativePartitionIntegration.V(profile.seamNormal)));
                var wound=K.Prepare(center,localAxis,longitudinal,radius,profile.tearStretch,profile.tearDepthM+radius*.16f,profile.rimSides,seed*917+K.StableHash(id));
                s.Operations.Add(new Operation{SourceId=id,Kind=0,Center=center,Axis=localAxis,Wound=wound,Profile=profile,Seed=seed});Schedule(s);
            }
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
                s.Operations.RemoveAll(o=>o.SourceId==id);Reset(s);
                if(s.Meta.Fresh){int stage;stages.TryGetValue(s.Meta.PartId,out stage);Tint(s,stage);}
            }
        }
        private static void Tint(Surface s,int stage)
        {
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
            }
        }
        private static bool Current(Surface s,int epoch)=>s.Filter&&s.Meta&&s.Epoch==epoch;
        private IEnumerator Process(Surface s)
        {
            int batch=graph.Presentation&&graph.Presentation.Available?graph.Presentation.WorkBatch:Batch;
            int epoch=s.Epoch;bool rebuild=s.Rebuild||s.Faces==null;int start=rebuild?0:s.Applied;
            var ops=s.Operations.Take(Math.Min(s.Operations.Count,start+4)).ToArray();if(!rebuild&&start>=ops.Length)yield break;
            if(ops.Length==0)
            {
                s.Filter.sharedMesh=BaseMesh(s);ReleaseChanged(s);ReleaseLips(s);s.Faces=null;s.Lips=null;s.Applied=0;s.Rebuild=false;Commits++;yield break;
            }
            var faces=rebuild?null:s.Faces;var lips=new TriangleBuffer();if(!rebuild&&s.Lips!=null)for(int i=0;i<s.Lips.Count;i++){lips.Add(s.Lips[i]);if(i%batch==0)yield return null;}
            if(faces==null)
            {
                var m=s.Original;phase="read positions";var positions=m.vertices;yield return null;phase="read normals";var normals=m.normals;yield return null;phase="read tangents";var tangents=m.tangents;yield return null;
                var uv=new Vector2[4][];for(int channel=0;channel<4;channel++){phase="read UV "+channel;uv[channel]=channel==0?m.uv:channel==1?m.uv2:channel==2?m.uv3:m.uv4;yield return null;}
                phase="read indices";var indices=m.triangles;yield return null;faces=new TriangleBuffer();phase="decode source faces";
                for(int i=0;i<indices.Length;i+=3)
                {
                    faces.Add(new K.Triangle(Read(indices[i],positions,normals,tangents,uv),Read(indices[i+1],positions,normals,tangents,uv),Read(indices[i+2],positions,normals,tangents,uv)));
                    if(i%(batch*3)==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                }
            }
            bool changed=rebuild&&s.Changed;
            for(int oi=start;oi<ops.Length;oi++)
            {
                if(!Current(s,epoch)){Cancelled++;yield break;}var op=ops[oi];var kept=new TriangleBuffer();var edges=new List<K.Edge>();bool modified=false;phase="clip / seam faces";
                if(op.Kind==0)
                {
                    float reach=op.Profile.tearMaxRadiusM*(op.Profile.tearStretch+1)+op.Profile.tearDepthM;
                    for(int i=0;i<faces.Count;i++)
                    {
                        var t=faces[i];var box=new Bounds(t.A.Position,Vector3.zero);box.Encapsulate(t.B.Position);box.Encapsulate(t.C.Position);
                        if(box.SqrDistance(op.Center)>reach*reach)kept.Add(t);else modified|=K.SubtractIfRemoved(t,op.Wound,kept,edges);
                        if(i%batch==0){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                    }
                    if(!modified)NoOpCuts++;
                    if(kept.Count>s.TriangleCount*2+512){RejectedGrowth++;modified=false;}
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
                if(modified){faces=kept;changed=true;var rim=AddLips(lips,edges,op,graph.Presentation&&graph.Presentation.Available?graph.Presentation.RimBudget:LipBudget,batch);while(rim.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}}
            }
            if(!Current(s,epoch)){Cancelled++;yield break;}
            if(changed)
            {
                // OwnedAirframePaint retains generated damage meshes by this
                // established prefix when refreshing every painted section.
                phase="build damage mesh";Mesh mesh=null;var build=Build(faces,"OWN Courier bounded rupture "+s.Meta.PartId,m=>stagedMesh=mesh=m,batch);
                while(build.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}
                Mesh lip=null;
                if(lips.Count>0){build=Build(lips,"Courier bounded tear rim",m=>stagedLip=lip=m,batch);while(build.MoveNext()){yield return null;if(!Current(s,epoch)){Cancelled++;yield break;}}}
                if(!Current(s,epoch)){Cancelled++;yield break;}
                phase="publish damage mesh";s.BaseUv=mesh.uv;if(s.Meta.Painted)Layout(mesh,channel,s.BaseUv);
                ReleaseChanged(s);s.Changed=mesh;s.Filter.sharedMesh=mesh;ChangedSurfaces++;
                if(lip)
                {
                    if(!s.LipObject){s.LipObject=new GameObject("Courier section tear rims",typeof(MeshFilter),typeof(MeshRenderer));s.LipObject.transform.SetParent(s.Filter.transform,false);var r=s.LipObject.GetComponent<MeshRenderer>();r.sharedMaterial=lipMaterial;r.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;}
                    if(s.LipMesh){GeneratedLipTriangles-=(int)s.LipMesh.GetIndexCount(0)/3;Object.Destroy(s.LipMesh);}s.LipMesh=lip;s.LipObject.GetComponent<MeshFilter>().sharedMesh=lip;GeneratedLipTriangles+=lips.Count;
                }
                else ReleaseLips(s);
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
        private static IEnumerator Build(TriangleBuffer faces,string name,Action<Mesh> result,int batch)
        {
            var lookup=new Dictionary<K.Vertex,int>(new VertexComparer());var vertices=new List<K.Vertex>();var indices=new List<int>(faces.Count*3);
            for(int i=0;i<faces.Count;i++)
            {
                var t=faces[i];Index(t.A,lookup,vertices,indices);Index(t.B,lookup,vertices,indices);Index(t.C,lookup,vertices,indices);if(i%batch==0)yield return null;
            }
            int count=vertices.Count;var p=new Vector3[count];var n=new Vector3[count];var ta=new Vector4[count];var uv=new[]{new Vector2[count],new Vector2[count],new Vector2[count],new Vector2[count]};
            for(int i=0;i<count;i++){var v=vertices[i];p[i]=v.Position;n[i]=v.Normal.normalized;ta[i]=v.Tangent;uv[0][i]=v.U0;uv[1][i]=v.U1;uv[2][i]=v.U2;uv[3][i]=v.U3;if(i%batch==0)yield return null;}
            var mesh=new Mesh{name=name,indexFormat=count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};
            // Cancellation owns the staged resource from its first yield.
            result(mesh);yield return null;mesh.vertices=p;yield return null;mesh.normals=n;yield return null;mesh.tangents=ta;yield return null;
            for(int k=0;k<4;k++){mesh.SetUVs(k,uv[k]);yield return null;}mesh.SetTriangles(indices,0);yield return null;mesh.RecalculateBounds();
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
        private void ReleaseChanged(Surface s){if(!s.Changed)return;Object.Destroy(s.Changed);s.Changed=null;ChangedSurfaces--;}
        private void ReleaseLips(Surface s){if(s.LipMesh){GeneratedLipTriangles-=(int)s.LipMesh.GetIndexCount(0)/3;Object.Destroy(s.LipMesh);}if(s.LipObject)Object.Destroy(s.LipObject);s.LipMesh=null;s.LipObject=null;}
        private void ReleaseStaged(){if(stagedMesh)Object.Destroy(stagedMesh);if(stagedLip)Object.Destroy(stagedLip);stagedMesh=stagedLip=null;}
        public void Dispose(){disposed=true;(active as IDisposable)?.Dispose();active=null;ReleaseStaged();queue.Clear();queued.Clear();foreach(var s in surfaces){ReleaseChanged(s);ReleaseLips(s);}if(lipMaterial)Object.Destroy(lipMaterial);}
    }
}

