using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Japensou.NuclearOption.AirframeData;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Presentation only: no collider/body/HP/joint/fuel/control writes here.
    public sealed class CourierPresentation:MonoBehaviour
    {
        private static readonly HashSet<CourierPresentation> instances=new HashSet<CourierPresentation>();
        private sealed class Row {internal int Index,Triangles;internal MeshFilter Filter;internal Renderer Renderer;internal NativePartitionSurface Meta;internal Mesh Selected;internal UnityEngine.Rendering.ShadowCastingMode Shadow;}
        private readonly Dictionary<MeshFilter,Row> lookup=new Dictionary<MeshFilter,Row>();
        private readonly Dictionary<string,int> stages=new Dictionary<string,int>();
        private CourierPresentationData data;private Row[] rows;private NativePartitionController graph;private Camera camera;private float nextPoll;
        private int distanceLevel;private CourierQuality preset;
        private bool cameraInside,accessOpen;
        public int ActiveTriangles,QualityChanges;
#if COURIER_BALANCED_ONLY
        internal double WorkBudgetMs=>.65;
        internal int RimBudget=>512;
        internal int WorkBatch=>8;
        private int MeshLevel=>1;
#else
        internal double WorkBudgetMs=>preset==CourierQuality.Performance?.35:preset==CourierQuality.Balanced?.65:1.0;
        internal int RimBudget=>preset==CourierQuality.Performance?256:preset==CourierQuality.Balanced?512:1024;
        internal int WorkBatch=>preset==CourierQuality.Performance?4:preset==CourierQuality.Balanced?8:16;
        private int MeshLevel=>Math.Max(distanceLevel,preset==CourierQuality.Performance?2:preset==CourierQuality.Balanced?1:0);
#endif
        internal bool Available=>data&&rows!=null;
        internal void Bind(NativePartitionController owner)
        {
            graph=owner;data=GetComponent<CourierPresentationData>();if(!data)return;
            ActiveTriangles=0;QualityChanges=0;
            int n=data.Filters?.Length??0;
            if(n==0||data.Detailed.Length!=n||data.Balanced.Length!=n||data.Performance.Length!=n||data.Owners.Length!=n||data.Interior.Length!=n||data.BakedDamage.Length!=n)throw new InvalidOperationException("Courier presentation bindings differ.");
            rows=new Row[n];for(int i=0;i<n;i++)
            {
                var f=data.Filters[i];if(!f||!data.Detailed[i]||!data.Balanced[i]||!data.Performance[i])throw new InvalidOperationException("Courier presentation mesh missing.");
                var renderer=f.GetComponent<Renderer>();var meta=f.GetComponent<NativePartitionSurface>();if(meta){meta.Interior=data.Interior[i];meta.BakedInterior=data.BakedDamage[i];meta.InteriorFurniture=meta.SourceOriginal.StartsWith("SHGT_Cabin material ")||meta.SourceOriginal.StartsWith("SHGT_CrewCompartments material ");meta.PlanningOriginal=data.PlanningSources!=null&&data.PlanningSources.Length==n?data.PlanningSources[i]:null;}
                rows[i]=new Row{Index=i,Filter=f,Renderer=renderer,Meta=meta,Shadow=data.ShadowModes!=null&&data.ShadowModes.Length==n?(UnityEngine.Rendering.ShadowCastingMode)data.ShadowModes[i]:renderer.shadowCastingMode};
                lookup.Add(f,rows[i]);
            }
            instances.Add(this);Apply();PreviewVisibility();
        }
        internal static void ApplyToAll(){foreach(var instance in instances)if(instance&&instance.Available){instance.Apply();instance.graph.Damage?.QualityChanged();instance.graph.Refresh();}}
        private void Update()
        {
            if(!Available||Time.unscaledTime<nextPoll)return;nextPoll=Time.unscaledTime+.2f;
            camera=Camera.main;float distance=camera?(camera.transform.position-transform.position).magnitude:0;
            int level=distance>600?2:distance>250?1:0;
            if(level!=distanceLevel||preset!=CurrentAirframeDamagePlugin.SelectedQuality){distanceLevel=level;Apply();graph.Damage?.QualityChanged();}
            if(!graph.Ready)PreviewVisibility();
        }
        internal void Health(string id,int stage)
        {
            if(!Available)return;if(stages.TryGetValue(id,out var old)&&old==stage)return;stages[id]=stage;
            foreach(var row in rows)if(data.BakedDamage[row.Index]&&data.Owners[row.Index]==id)Apply(row);
        }
        private void Apply()
        {
            preset=CurrentAirframeDamagePlugin.SelectedQuality;
            foreach(var row in rows)Apply(row);QualityChanges++;
        }
        private void Apply(Row row)
        {
            // a surface whose part was destroyed by damage keeps its row; skip it (seen in a real TC3 run, 21/11/2026)
            if(!row.Filter||!row.Renderer)return;
            int i=row.Index,level=MeshLevel;stages.TryGetValue(data.Owners[i],out var stage);
            Mesh mesh=level==2?data.Performance[i]:level==1?data.Balanced[i]:data.Detailed[i];
            if(data.BakedDamage[i]&&stage>0)
            {
                var damaged=level==2?(stage==1?data.PerformanceDamage1:data.PerformanceDamage2):level==1?(stage==1?data.BalancedDamage1:data.BalancedDamage2):(stage==1?data.DetailedDamage1:data.DetailedDamage2);
                if(damaged!=null&&i<damaged.Length&&damaged[i])mesh=damaged[i];
            }
            row.Selected=mesh;
            if(row.Meta==null||row.Meta.BakedInterior||!graph.Damage)row.Filter.sharedMesh=mesh;
            // Exterior self-shadowing is part of the authored finish. Drop
            // only interior casting in the light preset, preserving nacelles.
            row.Renderer.shadowCastingMode=level==2&&data.Interior[i]||windowShadowsOff&&row.Meta&&row.Meta.InteriorFurniture?UnityEngine.Rendering.ShadowCastingMode.Off:row.Shadow;
            int count=0;for(int slot=0;slot<mesh.subMeshCount;slot++)count+=(int)mesh.GetIndexCount(slot)/3;
            ActiveTriangles+=count-row.Triangles;row.Triangles=count;
        }
        internal Mesh SourceMesh(MeshFilter filter,Mesh fallback)=>lookup.TryGetValue(filter,out var row)?row.Selected:fallback;
        internal Mesh PaintMesh(MeshFilter filter,int channel,Mesh fallback)
        {
            if(channel==0)return SourceMesh(filter,fallback);if(!lookup.TryGetValue(filter,out var row))return fallback;
            int level=MeshLevel;if(level==0)return fallback;
            var array=level==2?(channel==1?data.PerformancePaint1:data.PerformancePaint2):(channel==1?data.BalancedPaint1:data.BalancedPaint2);
            return array!=null&&row.Index<array.Length&&array[row.Index]?array[row.Index]:fallback;
        }
        internal void PrepareVisibility()
        {
            camera=Camera.main;if(!camera){cameraInside=true;accessOpen=false;windowView=false;return;}
            // The camera inside the fuselage must retain a complete cockpit
            // and cargo interior, including intact close-up detail.
            Vector3 p=transform.InverseTransformPoint(camera.transform.position);
            cameraInside=Mathf.Abs(p.x)<5.5f&&p.y>2&&p.y<12&&p.z>-26&&p.z<34;
            // Seen from outside through the windscreen and side windows (front half, same 220 m as the cockpit
            // detail): the flight deck and crew compartment must be there, or the windows show the inside of the
            // far fuselage skin (reported 23/11 from the front view).
            windowView=!cameraInside&&p.z>-5f&&p.sqrMagnitude<220f*220f;
            interiorRange=cameraInside||p.sqrMagnitude<InteriorRange*InteriorRange;
            // Open loading/bomb doors expose otherwise intact interiors.
            accessOpen=graph.InteriorAccessOpen;
        }
        internal bool InteriorShown=>cameraInside||accessOpen;
        internal bool CameraInside=>cameraInside;
        internal bool WindowView=>windowView;
        private bool windowView;
        // 23/11 (owner): the whole interior is drawn within 250 m, at any angle (see NativePartitionController.Refresh)
        internal const float InteriorRange=250f;
        private bool interiorRange=true;
        internal bool InteriorInRange=>interiorRange;
        internal bool InteriorVisible(NativePartitionSurface surface,bool exposed)=>exposed||interiorRange;
        private void PreviewVisibility(){if(graph.Ready)return;PrepareVisibility();foreach(var row in rows)if(row.Renderer&&data.Interior[row.Index])row.Renderer.enabled=interiorRange;}
        // Furniture shown only through the windows casts no shadow (it is lit through the glass only).
        private bool windowShadowsOff;
        internal void WindowShadows(bool off)
        {
            if(off==windowShadowsOff||rows==null)return;windowShadowsOff=off;
            foreach(var row in rows)if(row.Renderer&&row.Meta&&row.Meta.InteriorFurniture)
                row.Renderer.shadowCastingMode=off||MeshLevel==2&&data.Interior[row.Index]?UnityEngine.Rendering.ShadowCastingMode.Off:row.Shadow;
        }
        private void OnDestroy(){instances.Remove(this);}

        // 23/11: the overhead console support above the pilot was removed from the exported cabin (21/11), but the
        // camera monitors that hung from it stayed, floating at the top of the windscreen; from outside they read as
        // a strip with engraved labels. The bundle stays frozen: their triangles (only these materials, wholly inside
        // the box, aircraft space) are dropped from the GE87 cabin mesh once, shared by every Courier.
        // baked asset names (CourierGE87_55/Materials, ge87-bay-meta.json order, colours checked): 48 LeatherNavy,
        // 51 EngravedIvory, 52 DarkTrim, 53 Premium SatinAluminum
        private static readonly string[] MonitorMaterials={"GE87_Bay_48","GE87_Bay_51","GE87_Bay_52","GE87_Bay_53"};
        private static readonly Bounds MonitorBox=new Bounds(new Vector3(-1.225f,9.35f,27.885f),new Vector3(1.35f,.40f,.47f));
        private static readonly Dictionary<Mesh,Mesh> trimmed=new Dictionary<Mesh,Mesh>();
        // 23/11 (owner): the light greige cabin looked odd through the windows. Walls, seats and the ceiling liner
        // (SoftGreige/AcousticGreige, GE87_Bay_57/55 in the bundle) get a dark neutral grey; metals, displays, lenses
        // and trims keep their colours. Shared clones, one per source material.
        private static readonly Color CabinGrey=new Color(.18f,.19f,.20f,1f);
        private static readonly Dictionary<Material,Material> darkened=new Dictionary<Material,Material>();
        private static bool Greige(Material m){var n=m.name.Replace(" (Instance)","");return n=="GE87_Bay_55"||n=="GE87_Bay_57"||n.EndsWith("Premium_SoftGreige")||n.EndsWith("Premium_AcousticGreige");}
        internal static Material Darkened(Material m)
        {
            if(!m)return m;if(darkened.TryGetValue(m,out var d))return d;
            if(!Greige(m)){darkened[m]=m;return m;}
            d=new Material(m){name=m.name+" (cabin grey)"};
            if(d.HasProperty("_BaseColor"))d.SetColor("_BaseColor",CabinGrey);if(d.HasProperty("_Color"))d.SetColor("_Color",CabinGrey);
            return darkened[m]=d;
        }
        internal static int DarkenCabin(IEnumerable<Renderer> renderers)
        {
            int changed=0;
            foreach(var r in renderers)
            {
                if(!r)continue;var mats=r.sharedMaterials;bool any=false;
                for(int i=0;i<mats.Length;i++){var d=Darkened(mats[i]);if(d!=mats[i]){mats[i]=d;any=true;}}
                if(any){r.sharedMaterials=mats;changed++;}
            }
            return changed;
        }

        // 23/11 (owner): removing the overhead console support (21/11) left its opening in the ceiling liner
        // (x +-0.45 m, z 26.5-28.05, y 10.06-9.31); through the windscreen it read as a dark block. A two-sided panel
        // in the liner material closes it, 1.5 cm behind the liner, parented to the cabin renderer that holds it.
        private static readonly Vector3[] FillerCorners={new Vector3(-.48f,10.08f,26.48f),new Vector3(.48f,10.08f,26.48f),new Vector3(.48f,9.29f,28.09f),new Vector3(-.48f,9.29f,28.09f)};
        internal static Renderer CabinCeilingFiller(Renderer[] cabin,Transform root)
        {
            var centre=root.TransformPoint(new Vector3(0f,9.7f,27.3f));
            var host=cabin.Where(r=>r&&r.name.StartsWith("GE87 Interior | Cabin")).OrderBy(r=>r.bounds.SqrDistance(centre)).FirstOrDefault();
            if(!host)return null;
            var liner=host.sharedMaterials.FirstOrDefault(m=>m&&(m.name.StartsWith("GE87_Bay_55")||m.name.Contains("AcousticGreige")));
            if(!liner){CurrentAirframeDamagePlugin.Log.LogWarning("Courier cabin ceiling: liner material not found; opening left as is.");return null;}
            var normal=new Vector3(0f,1f,.49f).normalized;var toHost=host.transform.worldToLocalMatrix*root.localToWorldMatrix;
            var p=FillerCorners.Select(c=>toHost.MultiplyPoint3x4(c+normal*.015f)).ToArray();var n=toHost.MultiplyVector(normal).normalized;
            var mesh=new Mesh{name="GE87 cabin ceiling filler"};
            mesh.vertices=new[]{p[0],p[1],p[2],p[3],p[0],p[1],p[2],p[3]};
            mesh.normals=new[]{-n,-n,-n,-n,n,n,n,n};
            mesh.uv=new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1),new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1)};
            mesh.triangles=new[]{0,1,2,0,2,3,4,6,5,4,7,6};mesh.RecalculateBounds();mesh.RecalculateTangents();
            var go=new GameObject("GE87 Interior | Cabin | ceiling filler",typeof(MeshFilter),typeof(MeshRenderer));go.layer=host.gameObject.layer;
            go.transform.SetParent(host.transform,false);go.GetComponent<MeshFilter>().sharedMesh=mesh;
            var mr=go.GetComponent<MeshRenderer>();mr.sharedMaterial=Darkened(liner);mr.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;mr.enabled=host.enabled;
            CurrentAirframeDamagePlugin.Log.LogInfo("Courier cabin ceiling: opening of the removed overhead support closed under "+host.name+".");
            return mr;
        }

        internal static void TrimCabinMonitors(IEnumerable<Renderer> cabin,Transform root)
        {
            foreach(var r in cabin)
            {
                if(!r||!root)continue;var filter=r.GetComponent<MeshFilter>();var source=filter?filter.sharedMesh:null;if(!source)continue;
                if(trimmed.TryGetValue(source,out var done)){if(done)filter.sharedMesh=done;continue;}
                trimmed[source]=null;
                if(!source.isReadable){CurrentAirframeDamagePlugin.Log.LogWarning("Courier cabin monitors: "+source.name+" is not readable; left as is.");continue;}
                var mats=r.sharedMaterials;var v=source.vertices;var toRoot=root.worldToLocalMatrix*r.transform.localToWorldMatrix;
                var inside=new bool[v.Length];for(int i=0;i<v.Length;i++)inside[i]=MonitorBox.Contains(toRoot.MultiplyPoint3x4(v[i]));
                var copy=Instantiate(source);copy.name=source.name+" (monitors trimmed)";int dropped=0;
                for(int sub=0;sub<source.subMeshCount&&sub<mats.Length;sub++)
                {
                    if(!mats[sub]||Array.IndexOf(MonitorMaterials,mats[sub].name.Replace(" (Instance)",""))<0)continue;
                    var tris=source.GetTriangles(sub);var keep=new List<int>(tris.Length);
                    for(int t=0;t<tris.Length;t+=3){if(inside[tris[t]]&&inside[tris[t+1]]&&inside[tris[t+2]]){dropped++;continue;}keep.Add(tris[t]);keep.Add(tris[t+1]);keep.Add(tris[t+2]);}
                    copy.SetTriangles(keep,sub,false);
                }
                if(dropped==0){Destroy(copy);continue;}
                copy.RecalculateBounds();trimmed[source]=copy;filter.sharedMesh=copy;
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cabin monitors: {dropped} floating monitor triangles removed from {r.name}.");
            }
        }
    }
}
