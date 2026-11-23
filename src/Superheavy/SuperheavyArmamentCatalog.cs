using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using UnityEngine;
using B=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Box;
using P=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Point;
using T=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Triangle;

namespace Japensou.NuclearOption.Superheavy
{
    // Read-only measurement. No prefab is instantiated, animated, initialized,
    // scaled or rewritten here. Dependency donors stay in their own addon.
    internal static class SuperheavyArmamentCatalog
    {
        // The optional baked-airframe adapter supplies authored semantics and
        // CPU meshes. The family assembly has no dependency on that adapter.
        internal static Func<MeshFilter,string[]> AuthoredNames;
        internal static Func<MeshFilter,Mesh> AuthoredMesh;
        private sealed class Cache
        {
            internal IReadOnlyList<WeaponItemSpec> Items;
            internal WeaponPlanGeometry Geometry;
            internal string GeometryFailure,ReportedGeometryFailure;
            internal readonly Dictionary<string,WeaponMount> Mounts=new Dictionary<string,WeaponMount>(StringComparer.Ordinal);
            internal readonly Dictionary<string,string> Failures=new Dictionary<string,string>(StringComparer.Ordinal);
        }
        private sealed class Piece
        {
            internal string Path;
            internal B Stowed;
            internal Transform Frame,Marker;
            internal Matrix4x4 ToAnchor,FrameToAnchor;
            internal MeshFilter LiftRod;
            internal Transform LiftFixed,LiftMoving;
        }
        private static readonly ConditionalWeakTable<Aircraft,Cache> Caches=new ConditionalWeakTable<Aircraft,Cache>();
        private static readonly Dictionary<string,Cache> DefinitionCatalogs=new Dictionary<string,Cache>(StringComparer.Ordinal);
        private static readonly Dictionary<string,WeakReference> LastMounts=new Dictionary<string,WeakReference>(StringComparer.Ordinal);
        private static bool IsItem(string id)=>id!=null&&(id.StartsWith("jp_bg280_spatial_",StringComparison.Ordinal)||id=="aryx_glidebomb400_single_internal"||
            new[]{"jp_bg280g_30","jp_bg280g_30x2","jp_bg280g_57","jp_bg280g_155","jp_bg280g_155n","jp_bg280g_203n"}.Contains(id));
        private static P Point(Vector3 v)=>new P(v.x,v.y,v.z);
        private static Vector3 Vector(P p)=>new Vector3(p.X,p.Y,p.Z);
        private static B Box(Bounds b)=>new B(Point(b.min),Point(b.max));
        private static B Shift(B b,P p)=>new B(new P(b.Min.X-p.X,b.Min.Y-p.Y,b.Min.Z-p.Z),new P(b.Max.X-p.X,b.Max.Y-p.Y,b.Max.Z-p.Z));
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        private static bool Valid(B b)=>Finite(b.Min.X)&&Finite(b.Min.Y)&&Finite(b.Min.Z)&&Finite(b.Max.X)&&Finite(b.Max.Y)&&Finite(b.Max.Z)&&b.Max.X>b.Min.X&&b.Max.Y>b.Min.Y&&b.Max.Z>b.Min.Z;
        private static string Path(Transform t,Transform root)
        {
            var names=new List<string>();for(var p=t;p!=null&&p!=root;p=p.parent)names.Add(p.name);names.Reverse();return string.Join("/",names);
        }
        private static bool Under(string piece,string parent)=>parent.Length==0||piece==parent||piece.StartsWith(parent+"/",StringComparison.Ordinal);
        private static Transform Frame(Transform t,Transform root)
        {for(var p=t;p!=null;p=p.parent){if(p.name=="SHGT_BatteryDeployFrame")return p;if(p==root)break;}return null;}
        private static Matrix4x4 Pose(Transform t)=>Matrix4x4.TRS(t.localPosition,t.localRotation,t.localScale);
        private static Matrix4x4 MountMatrix(Transform root)=>Pose(root)*root.worldToLocalMatrix;
        private static Matrix4x4 StowedFrame(Transform frame,Matrix4x4 worldToAnchor)
        {
            var marker=frame.parent;var stowed=marker.Find("SHGT_BatteryStowedPose");
            if(stowed==null)throw new InvalidOperationException("Missing authored stowed pose: "+marker.name);
            return worldToAnchor*marker.localToWorldMatrix*Pose(stowed);
        }
        private static B Measure(Mesh mesh,Matrix4x4 matrix,bool requireVolume=true)
        {
            bool first=true;Bounds b=default;
            Action<Vector3> add=p=>{var v=matrix.MultiplyPoint3x4(p);if(first){b=new Bounds(v,Vector3.zero);first=false;}else b.Encapsulate(v);};
            if(mesh.isReadable)foreach(var v in mesh.vertices)add(v);
            else
            {
                var s=mesh.bounds;
                for(int i=0;i<8;i++)add(new Vector3((i&1)==0?s.min.x:s.max.x,(i&2)==0?s.min.y:s.max.y,(i&4)==0?s.min.z:s.max.z));
            }
            if(first||!Finite(b.center.x)||!Finite(b.center.y)||!Finite(b.center.z)||!Finite(b.size.x)||!Finite(b.size.y)||!Finite(b.size.z)||b.size.sqrMagnitude==0||requireVolume&&!Valid(Box(b)))throw new InvalidOperationException("Missing/degenerate actual mesh: "+mesh.name);
            return Box(b);
        }
        private static B TransformBox(B b,Matrix4x4 matrix)
        {
            var boxes=new B[8];
            for(int i=0;i<8;i++){var p=Point(matrix.MultiplyPoint3x4(new Vector3((i&1)==0?b.Min.X:b.Max.X,(i&2)==0?b.Min.Y:b.Max.Y,(i&4)==0?b.Min.Z:b.Max.Z)));boxes[i]=new B(p,p);}
            return SuperheavySpatialWeaponsPlan.Union(boxes);
        }
        private static HashSet<Renderer> LowerLods(Transform root)
        {
            var primary=new HashSet<Renderer>();var lower=new HashSet<Renderer>();
            foreach(var group in root.GetComponentsInChildren<LODGroup>(true))
            {var levels=group.GetLODs();if(levels.Length>0)primary.UnionWith(levels[0].renderers);for(int i=1;i<levels.Length;i++)lower.UnionWith(levels[i].renderers);}
            lower.ExceptWith(primary);return lower;
        }
        private static bool Visible(Transform t,Transform root)
        {for(var p=t;p!=null&&p!=root;p=p.parent)if(!p.gameObject.activeSelf)return false;return true;}
        private static void AddPieces(Transform sourceRoot,Transform mountRoot,Matrix4x4 sourceToAnchor,Matrix4x4 worldToAnchor,List<Piece> pieces,Transform substituteFrame=null)
        {
            var lower=LowerLods(sourceRoot);
            foreach(var filter in sourceRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer=filter.GetComponent<MeshRenderer>();
                if(renderer==null||!renderer.enabled||!Visible(filter.transform,sourceRoot)||lower.Contains(renderer))continue;
                if(filter.sharedMesh==null)throw new InvalidOperationException("Actual mount has a missing mesh: "+filter.name);
                var frame=substituteFrame??Frame(filter.transform,mountRoot);
                Transform liftFixed=null,liftMoving=null;
                if(filter.name.StartsWith("SHGT_LiftRod_",StringComparison.Ordinal))
                {
                    string suffix=filter.name.Substring("SHGT_LiftRod_".Length);
                    var scope=filter.transform.parent;
                    liftFixed=scope.Find("SHGT_LiftFixedAnchor_"+suffix);
                    liftMoving=scope.GetComponentsInChildren<Transform>(true).SingleOrDefault(t=>t.name=="SHGT_LiftMovingAnchor_"+suffix);
                    if(liftFixed==null||liftMoving==null)throw new InvalidOperationException("Lift rod has incomplete endpoint anchors: "+filter.name);
                    frame=Frame(liftMoving,mountRoot);
                    if(frame==null)throw new InvalidOperationException("Lift moving anchor is outside its deploy frame.");
                }
                var original=sourceToAnchor*filter.transform.localToWorldMatrix;
                var matrix=frame==null||liftFixed!=null?original:StowedFrame(frame,worldToAnchor)*(worldToAnchor*frame.localToWorldMatrix).inverse*original;
                pieces.Add(new Piece{Path=Path(filter.transform,sourceRoot),Stowed=Measure(filter.sharedMesh,matrix),Frame=frame,Marker=frame?.parent,
                    ToAnchor=matrix,FrameToAnchor=frame==null?Matrix4x4.identity:StowedFrame(frame,worldToAnchor),
                    LiftRod=liftFixed==null?null:filter,LiftFixed=liftFixed,LiftMoving=liftMoving});
            }
        }
        internal static IReadOnlyList<WeaponItemSpec> GetCatalog(Aircraft aircraft)
        {
            if(aircraft==null)return Array.Empty<WeaponItemSpec>();
            var cache=Caches.GetValue(aircraft,_=>new Cache());
            string definitionKey=aircraft.definition?.jsonKey??"";
            if(cache.Items!=null)
            {
                var authoritative=DefinitionCatalogs.TryGetValue(definitionKey,out var current)?current:cache;
                RefreshOptionalPgm(aircraft,authoritative);
                if(!ReferenceEquals(cache,authoritative)&&!ReferenceEquals(cache.Items,authoritative.Items))CopyCatalog(authoritative,cache);
                return cache.Items;
            }
            bool hasLibrary=aircraft.weaponManager.hardpointSets.Any(s=>s.name=="SHGT_ArmamentCatalog");
            if(!hasLibrary&&DefinitionCatalogs.TryGetValue(definitionKey,out var shared)&&shared.Items!=null)
            {
                RefreshOptionalPgm(aircraft,shared);CopyCatalog(shared,cache);
                return cache.Items;
            }
            var mounts=new Dictionary<string,WeaponMount>(StringComparer.Ordinal);
            foreach(var set in aircraft.weaponManager.hardpointSets)
            {
                foreach(var mount in set.weaponOptions)if(mount!=null&&IsItem(mount.jsonKey))mounts[mount.jsonKey]=mount;
                if(set.weaponMount!=null&&IsItem(set.weaponMount.jsonKey))mounts[set.weaponMount.jsonKey]=set.weaponMount;
            }
            if(Encyclopedia.WeaponLookup!=null&&Encyclopedia.WeaponLookup.TryGetValue("aryx_glidebomb400_single_internal",out var pgm)&&pgm!=null)mounts[pgm.jsonKey]=pgm;
            var specs=new List<WeaponItemSpec>();
            foreach(var mount in mounts.Values.OrderBy(m=>m.jsonKey,StringComparer.Ordinal))
            {
                if(!TryMeasure(aircraft,mount,out var spec,out var reason)){cache.Failures[mount.jsonKey]=reason;continue;}
                specs.Add(spec);cache.Mounts[mount.jsonKey]=mount;LastMounts[mount.jsonKey]=new WeakReference(mount);
            }
            cache.Items=specs.AsReadOnly();if(hasLibrary||!DefinitionCatalogs.ContainsKey(definitionKey))DefinitionCatalogs[definitionKey]=cache;return cache.Items;
        }
        private static void CopyCatalog(Cache source,Cache target)
        {
            target.Items=source.Items;target.Mounts.Clear();foreach(var pair in source.Mounts)target.Mounts[pair.Key]=pair.Value;
            target.Failures.Clear();foreach(var pair in source.Failures)target.Failures[pair.Key]=pair.Value;
            if(target.Geometry!=null)target.Geometry.CatalogSourceHash=string.Join("/",target.Items.Select(s=>s.MeasurementSourceHash));
        }
        private static void RefreshOptionalPgm(Aircraft aircraft,Cache cache)
        {
            const string key="aryx_glidebomb400_single_internal";
            if(cache.Mounts.ContainsKey(key)||Encyclopedia.WeaponLookup==null||!Encyclopedia.WeaponLookup.TryGetValue(key,out var mount)||mount?.prefab==null)return;
            // The runtime owns the exact-source mesh restoration. A late addon
            // or restored PGM must not be excluded for the rest of the session,
            // and must not force a second extraction of the airframe geometry.
            var filters=mount.prefab.GetComponentsInChildren<MeshFilter>(true);
            if(filters.Length==0||filters.Any(f=>f.sharedMesh==null))return;
            if(!TryMeasure(aircraft,mount,out var spec,out var reason)){cache.Failures[key]=reason;return;}
            cache.Items=cache.Items.Concat(new[]{spec}).OrderBy(s=>s.Id,StringComparer.Ordinal).ToList().AsReadOnly();
            cache.Mounts[key]=mount;cache.Failures.Remove(key);LastMounts[key]=new WeakReference(mount);
            if(cache.Geometry!=null)cache.Geometry.CatalogSourceHash=string.Join("/",cache.Items.Select(s=>s.MeasurementSourceHash));
        }
        internal static WeaponMount ResolveMount(string id)
        {
            if(id==null)return null;
            if(LastMounts.TryGetValue(id,out var weak)&&weak.Target is WeaponMount measured&&measured!=null)return measured;
            return Encyclopedia.WeaponLookup!=null&&Encyclopedia.WeaponLookup.TryGetValue(id,out var native)&&IsItem(id)?native:null;
        }
        internal static IReadOnlyDictionary<string,string> GetMeasurementFailures(Aircraft aircraft)
        {
            if(aircraft==null)return new Dictionary<string,string>();GetCatalog(aircraft);return Caches.GetValue(aircraft,_=>new Cache()).Failures;
        }
        internal static bool TryResolveMount(Aircraft aircraft,string id,out WeaponMount mount,out string reason)
        {
            mount=null;reason=null;
            if(aircraft==null||id==null){reason="missing aircraft/item key";return false;}
            GetCatalog(aircraft);var cache=Caches.GetValue(aircraft,_=>new Cache());
            if(!cache.Mounts.TryGetValue(id,out mount)||mount==null){reason="actual mount/dependency/mesh was not measured in this aircraft catalog";return false;}
            return true;
        }
        internal static bool TryMeasure(Aircraft aircraft,WeaponMount mount,out WeaponItemSpec spec,out string reason)
        {
            spec=null;reason=null;
            try
            {
                if(mount==null||mount.prefab==null||mount.info==null||!IsItem(mount.jsonKey))throw new InvalidOperationException("missing real mount/prefab/info");
                bool gun=mount.jsonKey.StartsWith("jp_bg280g_",StringComparison.Ordinal);
                var root=mount.prefab.transform;var worldToAnchor=MountMatrix(root);var pieces=new List<Piece>();
                AddPieces(root,root,worldToAnchor,worldToAnchor,pieces);
                float roundMass=mount.info.massPerRound;
                if(mount.jsonKey==SuperheavyNavalTurretAdapter.MountKey)
                {
                    if(!SuperheavyNavalTurretAdapter.CanOffer(mount,out reason))return false;
                    var donor=Encyclopedia.Lookup[SuperheavyNavalTurretAdapter.DonorKey].unitPrefab.transform.Find(SuperheavyNavalTurretAdapter.DonorPath);
                    var host=root.GetComponentsInChildren<Transform>(true).Single(t=>t.name==SuperheavyNavalTurretAdapter.Marker);
                    var matrix=worldToAnchor*host.localToWorldMatrix*Matrix4x4.TRS(Vector3.zero,Quaternion.identity,donor.localScale)*donor.worldToLocalMatrix;
                    AddPieces(donor,root,matrix,worldToAnchor,pieces,Frame(host,root));
                    roundMass=donor.GetComponentInChildren<Gun>(true).info.massPerRound;
                }
                if(pieces.Count==0)throw new InvalidOperationException("complete actual visible geometry is unavailable");
                var all=SuperheavySpatialWeaponsPlan.Union(pieces.Select(p=>p.Stowed).ToArray());
                var anchor=new P((all.Min.X+all.Max.X)*.5f,all.Min.Y,(all.Min.Z+all.Max.Z)*.5f);
                var rails=root.GetComponentsInChildren<MountedMissile>(true);
                if(!gun&&(rails.Length==0||rails.Length!=mount.ammo))throw new InvalidOperationException("native real-rail count differs from finite mount ammunition");
                float ammoMass=mount.ammo*roundMass;
                float fullMass=mount.emptyMass+ammoMass;
                // WeaponMount.mass is the native authoritative all-up mass.
                // A different nonzero value is retained rather than dropping
                // launcher/rack mass or counting shell mass twice.
                if(mount.mass>0&&Finite(mount.mass))fullMass=Math.Max(fullMass,mount.mass);
                if(!Finite(fullMass)||fullMass<=0||!Finite(ammoMass)||ammoMass<0)throw new InvalidOperationException("native all-up ammunition/mechanism mass is unavailable");
                spec=new WeaponItemSpec{Id=mount.jsonKey,Label=mount.mountName,Kind=gun?SpatialWeaponKind.Gun:
                    (mount.jsonKey.IndexOf("gbm",StringComparison.OrdinalIgnoreCase)>=0||mount.jsonKey.IndexOf("pgm",StringComparison.OrdinalIgnoreCase)>=0||mount.jsonKey=="aryx_glidebomb400_single_internal"?SpatialWeaponKind.Bomb:SpatialWeaponKind.Missile),
                    Width=all.Max.X-all.Min.X,Height=all.Max.Y-all.Min.Y,Length=all.Max.Z-all.Min.Z,
                    NativeAnchorOffset=anchor,MassKg=fullMass,EmptyMassKg=Math.Max(0,fullMass-ammoMass),AmmoMassKg=ammoMass,
                    MountedRounds=mount.ammo,AllowRotation90=!gun,HeightAdjustable=!gun,
                    StowedVolumes=pieces.Select(p=>Shift(p.Stowed,anchor)).ToArray(),
                    FixedVolumes=pieces.Where(p=>gun&&p.Frame==null||!gun&&!rails.Any(r=>Under(p.Path,Path(r.transform,root)))).Select(p=>Shift(p.Stowed,anchor)).ToArray()};
                if(gun)
                {
                    if(pieces.All(p=>p.Frame==null))throw new InvalidOperationException("gun has no complete authored retractable frame");
                    float hardpointY=0;bool found=false;
                    foreach(var set in aircraft.weaponManager.hardpointSets.Where(s=>s.name!="SHGT_ArmamentCatalog"&&s.weaponOptions.Contains(mount)))
                    foreach(var point in set.hardpoints)if(point.transform!=null){hardpointY=aircraft.transform.InverseTransformPoint(point.transform.position).y;found=true;break;}
                    if(!found)throw new InvalidOperationException("native authored gun hardpoint height is unavailable");
                    spec.DefaultY=hardpointY+anchor.Y-4.63f;
                    spec.MotionProfile=MeasureMotion(pieces,anchor,worldToAnchor,mount.jsonKey);
                }
                else
                {
                    spec.DefaultY=.01f;var rounds=new List<WeaponRoundSpec>();
                    foreach(var rail in rails)
                    {
                        string path=Path(rail.transform,root);
                        var volumes=pieces.Where(p=>Under(p.Path,path)).Select(p=>p.Stowed).ToArray();
                        if(volumes.Length==0)throw new InvalidOperationException("native rail has no complete measured store mesh: "+path);
                        var b=SuperheavySpatialWeaponsPlan.Union(volumes);var a=new P((b.Min.X+b.Max.X)*.5f,b.Min.Y,(b.Min.Z+b.Max.Z)*.5f);
                        rounds.Add(new WeaponRoundSpec{NativeRailPath=path,NativeAnchorOffset=a,AnchorOffset=new P(a.X-anchor.X,a.Y-anchor.Y,a.Z-anchor.Z),MassKg=roundMass,
                            Volumes=volumes.Select(v=>Shift(v,anchor)).ToArray()});
                    }
                    spec.Rounds=rounds.ToArray();
                }
                spec.MeasurementSourceHash=MeasurementHash(spec);
                if(spec.MotionProfile!=null)spec.MotionProfile.MeasurementSourceHash=spec.MeasurementSourceHash;
                return true;
            }
            catch(Exception e){reason=e.Message;spec=null;return false;}
        }
        private static WeaponMotionProfile MeasureMotion(List<Piece> pieces,P anchor,Matrix4x4 worldToAnchor,string key)
        {
            var frames=pieces.Where(p=>p.Frame!=null).Select(p=>p.Frame).Distinct().OrderBy(f=>
                {var order=f.parent.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name.StartsWith("SHGT_BatteryOrder_",StringComparison.Ordinal));return order?.name??f.parent.name;},StringComparer.Ordinal).ToArray();
            var result=new List<WeaponPathStep>();B[] previous=null;
            for(int stage=0;stage<frames.Length;stage++)for(int n=stage==0?0:1;n<=100;n++)
            {
                float phase=n*.01f;var posed=new B[pieces.Count];
                for(int i=0;i<pieces.Count;i++)
                {
                    var piece=pieces[i];var box=piece.Stowed;
                    if(piece.Frame!=null)
                    {
                        int index=Array.IndexOf(frames,piece.Frame);float p=index<stage?1:index>stage?0:phase;
                        var a=piece.Marker.Find("SHGT_BatteryStowedPose");var b=piece.Marker.Find("SHGT_BatteryDeployedPose");
                        if(a==null||b==null)throw new InvalidOperationException("gun deployment pose is missing");
                        // Current short native cradles translate with constant
                        // orientation/scale. A changed rotating mechanism needs
                        // its actual arc sweep, never an endpoint approximation.
                        if(Quaternion.Angle(a.localRotation,b.localRotation)>.001f||Vector3.Distance(a.localScale,b.localScale)>.0001f)
                            throw new InvalidOperationException("rotating/scaling deployment needs a new continuous compound measurement");
                        var matrix=worldToAnchor*piece.Marker.localToWorldMatrix*Matrix4x4.TRS(Vector3.Lerp(a.localPosition,b.localPosition,p),a.localRotation,a.localScale);
                        if(piece.LiftRod!=null)
                        {
                            var parent=piece.LiftRod.transform.parent;
                            var scope=worldToAnchor*parent.localToWorldMatrix;
                            var from=parent.InverseTransformPoint(piece.LiftFixed.position);
                            var movingLocal=piece.Frame.InverseTransformPoint(piece.LiftMoving.position);
                            var to=scope.inverse.MultiplyPoint3x4(matrix.MultiplyPoint3x4(movingLocal));
                            var delta=to-from;float length=delta.magnitude;
                            if(length<.0001f)throw new InvalidOperationException("Degenerate lift rod at deployment phase.");
                            var scale=piece.LiftRod.transform.localScale;scale.y=length;
                            box=Measure(piece.LiftRod.sharedMesh,scope*Matrix4x4.TRS((from+to)*.5f,Quaternion.FromToRotation(Vector3.up,delta/length),scale));
                        }
                        else box=TransformBox(box,matrix*piece.FrameToAnchor.inverse);
                    }
                    posed[i]=Shift(box,anchor);
                }
                var swept=previous==null?posed:posed.Select((b,i)=>SuperheavySpatialWeaponsPlan.Union(new[]{b,previous[i]})).ToArray();
                result.Add(new WeaponPathStep(swept));previous=posed;
            }
            return new WeaponMotionProfile{Id=key+"/actual-short-cradles-101-samples",Steps=result.ToArray(),AllowsPartialExterior=true};
        }
        private static string MeasurementHash(WeaponItemSpec spec)
        {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                writer.Write(spec.Id);writer.Write(spec.MassKg);writer.Write(spec.MountedRounds);
                foreach(var b in spec.StowedVolumes)foreach(var f in new[]{b.Min.X,b.Min.Y,b.Min.Z,b.Max.X,b.Max.Y,b.Max.Z})writer.Write(f);
                if(spec.MotionProfile!=null)foreach(var step in spec.MotionProfile.Steps)foreach(var b in step.Volumes)foreach(var f in new[]{b.Min.X,b.Min.Y,b.Min.Z,b.Max.X,b.Max.Y,b.Max.Z})writer.Write(f);
                writer.Flush();using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
            }
        }
        internal static WeaponPlanGeometry GetGeometry(Aircraft aircraft)
        {
            if(aircraft==null)return null;var cache=Caches.GetValue(aircraft,_=>new Cache());
            if(cache.Geometry!=null){GetCatalog(aircraft);UpdateMassBudget(aircraft,cache.Geometry);return cache.Geometry;}
            WeaponPlanGeometry measured;
            try{measured=MeasureGeometry(aircraft,cache);}catch(Exception e){cache.GeometryFailure=e.GetType().Name+": "+e.Message;measured=null;}
            if(measured!=null)return measured;
            var definition=aircraft.definition;
            var canonical=definition?.unitPrefab==null?null:definition.unitPrefab.GetComponent<Aircraft>();
            string localFailure=cache.GeometryFailure??"unknown measured-geometry failure";
            // Spawn-time visual/damage/livery initialization can replace a live
            // mesh before its channels are ready. Only the exact definition's
            // intact registered prefab is an acceptable geometry source.
            if(canonical!=null&&canonical!=aircraft)
            {
                var source=GetGeometry(canonical);
                var marker=SuperheavyNativeDamage.Components<Transform>(aircraft).FirstOrDefault(t=>t.name=="SHGT_BomberFeedEnvelope");
                if(source!=null&&marker!=null)
                {
                    var actual=BoxMarker(marker,aircraft.transform);
                    var expected=new B(new P(source.LeftX,source.FloorY,source.FrontZ-source.Length),new P(source.LeftX+source.Width,source.FloorY+source.Height,source.FrontZ));
                    if(actual.Min.Distance(expected.Min)<.003f&&actual.Max.Distance(expected.Max)<.003f)
                    {
                        measured=CopyGeometry(source);measured.CatalogSourceHash=string.Join("/",GetCatalog(aircraft).Select(s=>s.MeasurementSourceHash));
                        UpdateMassBudget(aircraft,measured);cache.Geometry=measured;
                        ReportGeometry(aircraft,cache,"Live geometry unavailable ("+localFailure+"); using intact geometry from the exact registered definition prefab; source="+source.GeometrySourceHash+". Owner mass/capabilities remain separate.");
                        return measured;
                    }
                    cache.GeometryFailure=localFailure+"; exact-definition source rejected because aircraft-local envelope differs";
                }
                else cache.GeometryFailure=localFailure+"; canonical source unavailable: "+(Caches.GetValue(canonical,_=>new Cache()).GeometryFailure??"missing live envelope");
            }
            ReportGeometry(aircraft,cache,"Measured geometry unavailable: "+cache.GeometryFailure);return null;
        }
        internal static string GetGeometryFailure(Aircraft aircraft)=>aircraft==null?"missing aircraft":Caches.GetValue(aircraft,_=>new Cache()).GeometryFailure;
        private static void ReportGeometry(Aircraft aircraft,Cache cache,string message)
        {
            if(cache.ReportedGeometryFailure==message)return;cache.ReportedGeometryFailure=message;
            Debug.LogWarning("BG spatial geometry ["+aircraft.name+", "+(aircraft.definition?.jsonKey??"definition prefab")+"]: "+message);
        }
        private static WeaponPlanGeometry CopyGeometry(WeaponPlanGeometry source)=>new WeaponPlanGeometry{
            Width=source.Width,Length=source.Length,Height=source.Height,LeftX=source.LeftX,FrontZ=source.FrontZ,FloorY=source.FloorY,
            MinimumY=source.MinimumY,OuterClearY=source.OuterClearY,Clearance=source.Clearance,RoutingStep=source.RoutingStep,
            GeometrySourceHash=source.GeometrySourceHash,CatalogSourceHash=source.CatalogSourceHash,
            Walls=source.Walls,ContainmentWalls=source.ContainmentWalls,StaticBlockers=source.StaticBlockers,
            DoorSweeps=source.DoorSweeps,OpenDoorObstacles=source.OpenDoorObstacles,ExitZones=source.ExitZones,DirectDropOnly=source.DirectDropOnly,DoorSweepMeasured=source.DoorSweepMeasured};
        private static WeaponPlanGeometry MeasureGeometry(Aircraft aircraft,Cache cache)
        {
            cache.GeometryFailure=null;
            var marker=SuperheavyNativeDamage.Components<Transform>(aircraft).FirstOrDefault(t=>t.name=="SHGT_BomberFeedEnvelope");if(marker==null){cache.GeometryFailure="SHGT_BomberFeedEnvelope marker missing";return null;}
            var b=BoxMarker(marker,aircraft.transform);var geometry=new WeaponPlanGeometry{LeftX=b.Min.X,FrontZ=b.Max.Z,FloorY=b.Min.Y,
                Width=b.Max.X-b.Min.X,Length=b.Max.Z-b.Min.Z,Height=b.Max.Y-b.Min.Y,Clearance=.003f,RoutingStep=.25f};
            var all=SuperheavyNativeDamage.Components<MeshFilter>(aircraft);var transforms=SuperheavyNativeDamage.Components<Transform>(aircraft);var walls=new List<T>();var containment=new List<T>();var blocks=new List<B>();
            var region=new B(new P(b.Min.X-.5f,2.6f,b.Min.Z-.5f),new P(b.Max.X+.5f,b.Max.Y+.5f,b.Max.Z+.5f));
            bool liner=false;float minimum=b.Min.Y;
            // Complex native physics owns separate fracture partitions. The raw
            // seven-role renderers stay as empty identity anchors, not geometry.
            bool Outer(string n)=>n=="SHGT_Fuselage"||n.StartsWith("SHGT_Fuselage__",StringComparison.Ordinal);
            bool Inner(string n)=>n=="SHGT_FuselageInterior"||n.StartsWith("SHGT_FuselageInterior__",StringComparison.Ordinal);
            // Paint layouts deliberately upload GPU-only clones. Geometric
            // proofs use the retained authored mesh, including its UV4 policy,
            // rather than requiring a render clone to keep CPU vertex data.
            var paint=aircraft.GetComponent<SuperheavyGroundLiveries>();
            Mesh Authored(MeshFilter f)=>AuthoredMesh?.Invoke(f)??(paint!=null&&paint.TryGetOriginalMesh(f,out var original)?original:f.sharedMesh);
            string[] Names(MeshFilter f)=>AuthoredNames?.Invoke(f)??new[]{f.name};
            bool hasOuter=all.Any(f=>Names(f).Any(Outer)&&Authored(f)!=null&&Authored(f).vertexCount>0);
            foreach(var filter in all)
            {
                if(InsideGunCapsule(filter.transform,aircraft.transform))continue;
                var names=Names(filter);string name=names.FirstOrDefault(n=>Outer(n)||Inner(n)||n.StartsWith("SHGT_BombBay")||n.IndexOf("Bulkhead",StringComparison.OrdinalIgnoreCase)>=0)??names.FirstOrDefault()??filter.name;
                bool tank=name.StartsWith("SHGT_FuelGeometry__",StringComparison.Ordinal);
                bool wall=(name.StartsWith("SHGT_BombBay",StringComparison.Ordinal)&&!name.StartsWith("SHGT_BombBayLeaf_",StringComparison.Ordinal))||Outer(name)||Inner(name)||name.StartsWith("SHGT_Structure_",StringComparison.Ordinal)||
                    name.IndexOf("Bulkhead",StringComparison.OrdinalIgnoreCase)>=0||name=="SHGT_CrewCompartments"||name=="SHGT_Cabin"||name.StartsWith("SHGT_CabinCompartment",StringComparison.Ordinal);
                if(!wall&&!tank)continue;
                var mesh=Authored(filter);
                if(mesh==null&&all.Any(f=>f.name.StartsWith(name+"__",StringComparison.Ordinal)&&Authored(f)!=null&&Authored(f).vertexCount>0))continue;
                // Import/export preserves empty former sill and role anchors.
                // They contain no physical face to prove. Completeness below
                // still requires the real liner, pressure hull, exits and every
                // door leaf's actual swept volume; no bounds are invented here.
                if(mesh!=null&&mesh.vertexCount==0)continue;
                if(mesh==null||!mesh.isReadable){cache.GeometryFailure="required mesh missing/unreadable: "+name+" / "+(mesh==null?"null":mesh.name)+", readable="+(mesh!=null&&mesh.isReadable)+", vertices="+(mesh==null?0:mesh.vertexCount);return null;}
                var matrix=aircraft.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
                // Partitioned walls/floors can be exactly planar. Measure
                // their actual triangles without inventing a box thickness.
                var bounds=Measure(mesh,matrix,false);
                if(name=="SHGT_BombBayLiner"){liner=true;minimum=Math.Min(minimum,bounds.Min.Y);}
                if(!bounds.Intersects(region))continue;
                if(tank){blocks.Add(bounds);continue;}
                var vertices=mesh.vertices;var indices=mesh.triangles;
                var policy=new List<Vector2>();
                bool boundary=Outer(name)||(!hasOuter&&Inner(name));
                if(boundary)
                {
                    mesh.GetUVs(3,policy);
                    if(policy.Count!=vertices.Length){cache.GeometryFailure="outer skin UV4 policy unavailable: "+name+", mesh="+mesh.name+", policy="+policy.Count+", vertices="+vertices.Length;return null;}
                }
                for(int i=0;i<indices.Length;i+=3)
                {
                    var triangle=new T(Point(matrix.MultiplyPoint3x4(vertices[indices[i]])),Point(matrix.MultiplyPoint3x4(vertices[indices[i+1]])),Point(matrix.MultiplyPoint3x4(vertices[indices[i+2]])));
                    if(triangle.Bounds.Intersects(region))walls.Add(triangle);
                    if(boundary&&Math.Abs(policy[indices[i]].x-1f)<.01f&&Math.Abs(policy[indices[i+1]].x-1f)<.01f&&Math.Abs(policy[indices[i+2]].x-1f)<.01f)
                        containment.Add(triangle);
                }
            }
            if(!liner||walls.Count==0||containment.Count==0){cache.GeometryFailure="measured boundary incomplete: liner="+liner+", wallTriangles="+walls.Count+", outerSkinTriangles="+containment.Count;return null;}
            geometry.DirectDropOnly=transforms.Any(t=>t.name=="SHGT_DirectDropBay");
            geometry.MinimumY=minimum;geometry.Walls=walls.ToArray();geometry.ContainmentWalls=containment.ToArray();
            foreach(var obstacle in transforms.Where(t=>t.name.StartsWith("SHGT_BomberFeedBlocker_",StringComparison.Ordinal)&&!InsideGunCapsule(t,aircraft.transform)))blocks.Add(BoxMarker(obstacle,aircraft.transform));
            geometry.StaticBlockers=blocks.Where(Valid).ToArray();
            var continuous=transforms.FirstOrDefault(t=>t.name=="SHGT_BomberFeedExit_Continuous");
            var exits=continuous!=null?new[]{continuous}:Enumerable.Range(1,2).Select(n=>transforms.FirstOrDefault(t=>t.name=="SHGT_BomberFeedExit_Cell"+n)).Where(t=>t!=null).ToArray();
            geometry.ExitZones=exits.Select((t,i)=>{var v=BoxMarker(t,aircraft.transform);return new WeaponExitZone(i+1,v.Min.X,v.Max.X,v.Min.Z,v.Max.Z);}).ToArray();
            geometry.DoorSweeps=transforms.Where(t=>t.name.StartsWith("SHGT_ArmamentDoorSweep_",StringComparison.Ordinal)).Select(t=>BoxMarker(t,aircraft.transform)).ToArray();
            geometry.OpenDoorObstacles=transforms.Where(t=>t.name.StartsWith("SHGT_ArmamentDoorOpen_",StringComparison.Ordinal)).Select(t=>BoxMarker(t,aircraft.transform)).ToArray();
            // One complete, measured sweep per physical leaf. The authoring
            // marker records the exact imported curtain track count.
            var curtainMarker=transforms.FirstOrDefault(t=>t.name=="SHGT_BayCurtain_R9"||t.name=="SHGT_BayCurtain_R10");
            int expectedLeaves=curtainMarker!=null?Mathf.RoundToInt(curtainMarker.localScale.x):12;
            geometry.DoorSweepMeasured=expectedLeaves>=2&&expectedLeaves<=256&&geometry.OpenDoorObstacles.Length==expectedLeaves&&geometry.DoorSweeps.Length>=expectedLeaves*100;
            geometry.CatalogSourceHash=string.Join("/",GetCatalog(aircraft).Select(s=>s.MeasurementSourceHash));
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                foreach(var t in geometry.Walls)foreach(var p in new[]{t.A,t.B,t.C}){writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);}
                foreach(var v in geometry.StaticBlockers.Concat(geometry.DoorSweeps).Concat(geometry.OpenDoorObstacles))foreach(float f in new[]{v.Min.X,v.Min.Y,v.Min.Z,v.Max.X,v.Max.Y,v.Max.Z})writer.Write(f);
                writer.Flush();using(var sha=SHA256.Create())geometry.GeometrySourceHash=BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");
            }
            UpdateMassBudget(aircraft,geometry);cache.Geometry=geometry;return geometry;
        }
        private static bool InsideGunCapsule(Transform node,Transform aircraft)
        {
            // Each mount owns its keep-out through its placement/spec. Treating
            // the same capsule as an airframe obstacle makes it hit itself.
            for(var p=node;p!=null&&p!=aircraft;p=p.parent)
                if(p.Find("SHGT_GunCapsuleReservation")!=null||p.GetComponent<SuperheavySpatialWeaponItem>()!=null)return true;
            return false;
        }
        private static B BoxMarker(Transform marker,Transform aircraft)=>TransformBox(new B(new P(-.5f,-.5f,-.5f),new P(.5f,.5f,.5f)),aircraft.worldToLocalMatrix*marker.localToWorldMatrix);
        private static void UpdateMassBudget(Aircraft aircraft,WeaponPlanGeometry geometry)
        {
            // The player chooses the carried mass. Only physical placement,
            // deployment and native weapon-eligibility rules constrain this
            // organizer; actual loaded mass still reaches the flight physics.
            geometry.WarningMassKg=float.PositiveInfinity;
            geometry.MaxMassKg=float.PositiveInfinity;
        }
    }
}
