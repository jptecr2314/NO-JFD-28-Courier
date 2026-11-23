using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using B=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Box;
using P=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Point;
using T=Japensou.NuclearOption.Superheavy.BomberFeedPlan.Triangle;

namespace Japensou.NuclearOption.Superheavy
{
    // Separate armament model. No MountedCargo, Retro key, vehicle preset or
    // transport save format is reused. Coordinates follow the organizer's
    // left/front rectangle convention; geometry uses aircraft-local metres.
    internal enum SpatialWeaponKind { Bomb, Missile, Gun }
    internal sealed class WeaponItemSpec
    {
        internal string Id,Label,MeasurementSourceHash;
        internal SpatialWeaponKind Kind;
        internal float Width,Length,Height,MassKg;
        internal bool AllowRotation90,HeightAdjustable=true;
        // All dimensions/volumes are measured in metres, after the native root
        // scale. This anchor is the full stowed union's bottom X/Z centre in
        // the authored mount frame, NOT the prefab's Transform origin.
        internal P NativeAnchorOffset;
        internal float DefaultY,EmptyMassKg,AmmoMassKg;
        internal int MountedRounds;
        internal B[] StowedVolumes=Array.Empty<B>(),FixedVolumes=Array.Empty<B>();
        internal WeaponRoundSpec[] Rounds=Array.Empty<WeaponRoundSpec>();
        internal WeaponMotionProfile MotionProfile;
    }
    internal sealed class WeaponRoundSpec
    {
        internal string NativeRailPath;
        // Offset from the ITEM's normalized anchor, before organizer rotation.
        internal P AnchorOffset;
        internal P NativeAnchorOffset;
        internal B[] Volumes=Array.Empty<B>();
        internal float MassKg;
    }
    internal readonly struct WeaponPlacement
    {
        internal readonly WeaponItemSpec Spec;
        internal readonly float X,Y,Z;
        internal readonly bool Rotation90;
        internal WeaponPlacement(WeaponItemSpec spec,float x,float z,bool rotation90=false)
        {Spec=spec;X=x;Y=0;Z=z;Rotation90=rotation90;}
        internal WeaponPlacement(WeaponItemSpec spec,float x,float z,float y,bool rotation90=false)
        {Spec=spec;X=x;Y=y;Z=z;Rotation90=rotation90;}
    }
    internal sealed class WeaponMotionProfile
    {
        internal string Id,MeasurementSourceHash;
        // Bounds are measured continuous swept volumes relative to the item's
        // floor-level X/Z centre. Not just endpoint boxes or display meshes.
        internal WeaponPathStep[] Steps=Array.Empty<WeaponPathStep>();
        internal B? AllowedAnchorZone;
        internal bool AllowsPartialExterior;
    }
    internal readonly struct WeaponPathStep
    {
        internal readonly B SweptBounds;
        internal readonly int ExitCell;
        internal readonly B[] Volumes;
        internal WeaponPathStep(B sweptBounds,int exitCell=0){SweptBounds=sweptBounds;Volumes=new[]{sweptBounds};ExitCell=exitCell;}
        internal WeaponPathStep(B[] volumes,int exitCell=0){Volumes=volumes;SweptBounds=SuperheavySpatialWeaponsPlan.Union(volumes);ExitCell=exitCell;}
    }
    internal readonly struct WeaponExitZone
    {
        internal readonly int Cell;
        internal readonly float Left,Right,Aft,Front;
        internal WeaponExitZone(int cell,float left,float right,float aft,float front)
        {Cell=cell;Left=left;Right=right;Aft=aft;Front=front;}
        internal bool ContainsXZ(B b,float e)=>b.Min.X>=Left-e&&b.Max.X<=Right+e&&b.Min.Z>=Aft-e&&b.Max.Z<=Front+e;
    }
    internal sealed class WeaponPlanGeometry
    {
        internal float Width=6.08f,Length=31.98f,Height=4.27f,LeftX=-3.04f,FrontZ=17.5f,FloorY=4.63f;
        internal float OuterClearY=2.70f,MaxMassKg=float.PositiveInfinity,WarningMassKg=float.PositiveInfinity,Clearance;
        internal float MinimumY=float.NaN,RoutingStep=.25f;
        internal string GeometrySourceHash,CatalogSourceHash;
        internal T[] Walls=Array.Empty<T>();
        // Only the actual compartment boundary, not nested fuselage/frames.
        // An upward odd/even test rejects a box wholly outside a curved wall,
        // where surface-intersection tests alone cannot detect the mistake.
        internal T[] ContainmentWalls=Array.Empty<T>();
        internal B[] DoorSweeps=Array.Empty<B>();
        internal B[] OpenDoorObstacles=Array.Empty<B>();
        internal bool DoorSweepMeasured;
        internal bool DirectDropOnly;
        internal B[] StaticBlockers=Array.Empty<B>();
        internal WeaponExitZone[] ExitZones=Array.Empty<WeaponExitZone>();
        internal WeaponNativeIntegration NativeIntegration;
        // Per-validation data only. UI callbacks must marshal their own state;
        // this pure solver never calls Unity or assumes a particular thread.
        internal CancellationToken ValidationCancellation;
        internal Action<int,int> ValidationProgress;
        internal B Envelope=>new B(new P(LeftX,float.IsNaN(MinimumY)?FloorY:MinimumY,FrontZ-Length),new P(LeftX+Width,FloorY+Height,FrontZ));
    }
    internal sealed class WeaponNativeIntegration
    {
        internal string Id,GeometrySourceHash,CatalogSourceHash,LatestNativeEvidence;
        internal bool SpawnSaveRestoreValidated,ContinuousDeployValidated,ArbitraryReleaseValidated;
        internal bool LayeredPlacementSupported,CompoundPlacementSupported,PerRoundReleaseSupported;
    }
    internal sealed class WeaponRoute
    {
        internal P[] Waypoints=Array.Empty<P>();
        internal WeaponPathStep[] SweptVolumes=Array.Empty<WeaponPathStep>();
        internal int ExitCell,RoundIndex=-1;
    }
    internal sealed class WeaponPlanValidation
    {
        internal bool PlacementValid,DeploymentProven,ReleasePathPending,CanApplyNative;
        internal string LayoutFingerprint;
        internal double TotalMassKg;
        internal bool OverNominalMass;
        internal readonly List<string> Warnings=new List<string>();
        internal readonly List<string> Issues=new List<string>();
    }
    internal sealed class WeaponPlanResolveResult
    {
        // Failed/budget-limited attempts return the original complete draft.
        // Resolving never discards stock. Direct-drop release order is rebuilt
        // from the physical lower layers before the upper layers.
        internal bool Success,BudgetExhausted;
        internal WeaponPlacement[] Items=Array.Empty<WeaponPlacement>();
        internal WeaponPlanValidation Validation;
        internal string Reason;
        internal int CandidatesTested,ItemsMoved,ItemsAdded;
    }
    internal static class SuperheavySpatialWeaponsPlan
    {
        private const float E=.0001f;
        internal static int[] InstanceDeploymentOrders(IReadOnlyList<int> authored,IReadOnlyList<string> instanceKeys)
        {
            if(authored==null||instanceKeys==null||authored.Count!=instanceKeys.Count||instanceKeys.Any(string.IsNullOrEmpty)||
                instanceKeys.Distinct(StringComparer.Ordinal).Count()!=instanceKeys.Count)throw new ArgumentException("Each real cradle needs a unique hierarchy identity.");
            var ranks=new int[authored.Count];var indices=Enumerable.Range(0,authored.Count)
                .OrderBy(i=>authored[i]).ThenBy(i=>instanceKeys[i],StringComparer.Ordinal).ToArray();
            for(int i=0;i<indices.Length;i++)ranks[indices[i]]=i;return ranks;
        }
        internal static bool RouteClearOfLiveBoxes(WeaponRoute route,IReadOnlyList<B> actual,float clearance)
        {
            if(route?.SweptVolumes==null||route.SweptVolumes.Length==0||actual==null)return false;
            return !route.SweptVolumes.SelectMany(s=>s.Volumes??new[]{s.SweptBounds})
                .Any(s=>actual.Any(b=>Pad(b,clearance).Intersects(s)));
        }
        internal static WeaponPlacement[] OrderedForRelease(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g)
        {
            if(!g.DirectDropOnly)return plan.ToArray();
            // Stable sorting preserves real gun deployment order and each
            // horizontal layer's selected order. MinY includes measured offsets.
            return plan.Select((p,i)=>new{P=p,I=i}).OrderBy(v=>v.P.Spec.Kind==SpatialWeaponKind.Gun?0:1)
                .ThenBy(v=>v.P.Spec.Kind==SpatialWeaponKind.Gun?0:StowedBounds(v.P,g).Min.Y).ThenBy(v=>v.I).Select(v=>v.P).ToArray();
        }
        private static void Cancel(WeaponPlanGeometry geometry)=>geometry?.ValidationCancellation.ThrowIfCancellationRequested();
        internal static WeaponPlanGeometry CloneGeometryForValidation(WeaponPlanGeometry g,CancellationToken token,Action<int,int> progress=null)
        {
            if(g==null)throw new ArgumentNullException(nameof(g));token.ThrowIfCancellationRequested();
            var native=g.NativeIntegration;
            return new WeaponPlanGeometry{Width=g.Width,Length=g.Length,Height=g.Height,LeftX=g.LeftX,FrontZ=g.FrontZ,FloorY=g.FloorY,
                MinimumY=g.MinimumY,OuterClearY=g.OuterClearY,MaxMassKg=g.MaxMassKg,WarningMassKg=g.WarningMassKg,Clearance=g.Clearance,
                RoutingStep=g.RoutingStep,GeometrySourceHash=g.GeometrySourceHash,CatalogSourceHash=g.CatalogSourceHash,
                Walls=g.Walls,ContainmentWalls=g.ContainmentWalls,StaticBlockers=g.StaticBlockers,DoorSweeps=g.DoorSweeps,
                OpenDoorObstacles=g.OpenDoorObstacles,ExitZones=g.ExitZones,DirectDropOnly=g.DirectDropOnly,DoorSweepMeasured=g.DoorSweepMeasured,
                ValidationCancellation=token,ValidationProgress=progress,
                NativeIntegration=native==null?null:new WeaponNativeIntegration{Id=native.Id,GeometrySourceHash=native.GeometrySourceHash,
                    CatalogSourceHash=native.CatalogSourceHash,LatestNativeEvidence=native.LatestNativeEvidence,
                    SpawnSaveRestoreValidated=native.SpawnSaveRestoreValidated,ContinuousDeployValidated=native.ContinuousDeployValidated,
                    ArbitraryReleaseValidated=native.ArbitraryReleaseValidated,LayeredPlacementSupported=native.LayeredPlacementSupported,
                    CompoundPlacementSupported=native.CompoundPlacementSupported,PerRoundReleaseSupported=native.PerRoundReleaseSupported}};
        }
        private static bool F(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        private static bool PointFinite(P p)=>F(p.X)&&F(p.Y)&&F(p.Z);
        private static bool BoxFinite(B b)=>PointFinite(b.Min)&&PointFinite(b.Max);
        private static bool BoxValid(B b)=>BoxFinite(b)&&
            b.Max.X>b.Min.X&&b.Max.Y>b.Min.Y&&b.Max.Z>b.Min.Z;
        private static bool Contains(B a,B b)=>b.Min.X>=a.Min.X-E&&b.Min.Y>=a.Min.Y-E&&b.Min.Z>=a.Min.Z-E&&
            b.Max.X<=a.Max.X+E&&b.Max.Y<=a.Max.Y+E&&b.Max.Z<=a.Max.Z+E;
        internal static B Pad(B a,float gap)=>new B(new P(a.Min.X-gap,a.Min.Y-gap,a.Min.Z-gap),new P(a.Max.X+gap,a.Max.Y+gap,a.Max.Z+gap));
        internal static B Union(IReadOnlyList<B> boxes)
        {
            if(boxes==null||boxes.Count==0)return default;
            var b=boxes[0];
            for(int i=1;i<boxes.Count;i++)b=new B(new P(Math.Min(b.Min.X,boxes[i].Min.X),Math.Min(b.Min.Y,boxes[i].Min.Y),Math.Min(b.Min.Z,boxes[i].Min.Z)),
                new P(Math.Max(b.Max.X,boxes[i].Max.X),Math.Max(b.Max.Y,boxes[i].Max.Y),Math.Max(b.Max.Z,boxes[i].Max.Z)));
            return b;
        }
        internal static P Anchor(WeaponPlacement p,WeaponPlanGeometry g)=>new P(g.LeftX+p.X+Width(p)*.5f,g.FloorY+p.Y,g.FrontZ-p.Z-Length(p)*.5f);
        internal static P Rotate(P q,bool rotation90)=>rotation90?new P(q.Z,q.Y,-q.X):q;
        private static B Shift(B b,P q)=>new B(new P(b.Min.X+q.X,b.Min.Y+q.Y,b.Min.Z+q.Z),new P(b.Max.X+q.X,b.Max.Y+q.Y,b.Max.Z+q.Z));
        private static B Rotate(B b,bool rotation90)=>rotation90?new B(new P(b.Min.Z,b.Min.Y,-b.Max.X),new P(b.Max.Z,b.Max.Y,-b.Min.X)):b;
        private static B[] RelativeVolumes(WeaponItemSpec s)=>s.StowedVolumes!=null&&s.StowedVolumes.Length>0?s.StowedVolumes:
            new[]{new B(new P(-s.Width*.5f,0,-s.Length*.5f),new P(s.Width*.5f,s.Height,s.Length*.5f))};
        internal static B[] StowedVolumes(WeaponPlacement p,WeaponPlanGeometry g)=>RelativeVolumes(p.Spec).Select(b=>TransformRelative(b,p,g)).ToArray();
        private sealed class GunReservationIdentity
        {
            internal B[] Parked;
            internal WeaponPathStep[] Steps;
            internal float Y,Width,Length,Height;
            internal B Relative;
            internal bool Ready;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WeaponItemSpec,GunReservationIdentity> GunReservations=
            new System.Runtime.CompilerServices.ConditionalWeakTable<WeaponItemSpec,GunReservationIdentity>();
        internal static B[] ReservedVolumes(WeaponPlacement p,WeaponPlanGeometry g)
        {
            Cancel(g);
            if(p.Spec.Kind!=SpatialWeaponKind.Gun)return StowedVolumes(p,g);
            // The installed capsule is solid keep-out, including the empty
            // space inside its housing. Reserve the moving lift's in-chamber
            // envelope as well, but not the exterior/door opening below it.
            // Catalog arrays are immutable after measurement. Cache this
            // relative prism rather than uniting 101 deployment poses again
            // for each mouse move or candidate in the packing worker.
            var s=p.Spec;var steps=s.MotionProfile?.Steps;var identity=GunReservations.GetValue(s,_=>new GunReservationIdentity());B relativeUnion;
            lock(identity)
            {
                if(!identity.Ready||!ReferenceEquals(identity.Parked,s.StowedVolumes)||!ReferenceEquals(identity.Steps,steps)||
                    identity.Y!=p.Y||identity.Width!=s.Width||identity.Length!=s.Length||identity.Height!=s.Height)
                {
                    var reserved=new List<B>(RelativeVolumes(s));
                    if(steps!=null)foreach(var step in steps)
                    {
                        Cancel(g);
                        foreach(var box in step.Volumes??new[]{step.SweptBounds})
                        {
                            if(box.Max.Y+p.Y<=E)continue;
                            reserved.Add(new B(new P(box.Min.X,Math.Max(-p.Y,box.Min.Y),box.Min.Z),box.Max));
                        }
                    }
                    identity.Relative=Union(reserved);identity.Y=p.Y;identity.Parked=s.StowedVolumes;identity.Steps=steps;
                    identity.Width=s.Width;identity.Length=s.Length;identity.Height=s.Height;identity.Ready=true;
                }
                relativeUnion=identity.Relative;
            }
            Cancel(g);return new[]{TransformRelative(relativeUnion,p,g)};
        }
        internal static bool ConflictsWithGun(IReadOnlyList<WeaponPlacement> plan,int replaceIndex,WeaponPlacement candidate,WeaponPlanGeometry g)
        {
            Cancel(g);if(plan==null||candidate.Spec==null)return false;
            var boxes=ReservedVolumes(candidate,g);
            for(int i=0;i<plan.Count;i++)
            {
                Cancel(g);if(i==replaceIndex||plan[i].Spec==null)continue;
                if(candidate.Spec.Kind!=SpatialWeaponKind.Gun&&plan[i].Spec.Kind!=SpatialWeaponKind.Gun)continue;
                if(ReservedVolumes(plan[i],g).Any(a=>boxes.Any(b=>Pad(a,g.Clearance).Intersects(b))))return true;
            }
            return false;
        }
        private static float Width(WeaponPlacement p)=>p.Rotation90?p.Spec.Length:p.Spec.Width;
        private static float Length(WeaponPlacement p)=>p.Rotation90?p.Spec.Width:p.Spec.Length;
        internal static B StowedBounds(WeaponPlacement p,WeaponPlanGeometry g)
        {
            return Union(StowedVolumes(p,g));
        }
        internal static B TransformRelative(B relative,WeaponPlacement p,WeaponPlanGeometry g)
        {
            return Shift(Rotate(relative,p.Rotation90),Anchor(p,g));
        }
        private sealed class WallIndex
        {
            internal readonly T[] Source;
            private readonly Dictionary<(int,int,int),List<int>> cells=new Dictionary<(int,int,int),List<int>>();
            private readonly List<int> large=new List<int>();
            private static int Cell(float v)=>(int)Math.Floor(v/2);
            internal WallIndex(T[] triangles,CancellationToken token)
            {
                Source=triangles;
                for(int i=0;i<triangles.Length;i++)
                {
                    if((i&63)==0)token.ThrowIfCancellationRequested();
                    var b=triangles[i].Bounds;int ax=Cell(b.Min.X),ay=Cell(b.Min.Y),az=Cell(b.Min.Z),bx=Cell(b.Max.X),by=Cell(b.Max.Y),bz=Cell(b.Max.Z);
                    if((long)(bx-ax+1)*(by-ay+1)*(bz-az+1)>4096){large.Add(i);continue;}
                    for(int x=ax;x<=bx;x++)for(int y=ay;y<=by;y++)for(int z=az;z<=bz;z++)
                    {var key=(x,y,z);if(!cells.TryGetValue(key,out var indices))cells.Add(key,indices=new List<int>());indices.Add(i);}
                }
            }
            internal bool Hits(B b,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();int work=0;
                foreach(int i in large){if((work++&63)==0)token.ThrowIfCancellationRequested();if(Source[i].Hits(b))return true;}
                var visited=new HashSet<int>();
                for(int x=Cell(b.Min.X);x<=Cell(b.Max.X);x++)for(int y=Cell(b.Min.Y);y<=Cell(b.Max.Y);y++)for(int z=Cell(b.Min.Z);z<=Cell(b.Max.Z);z++)
                    if(cells.TryGetValue((x,y,z),out var indices))foreach(int i in indices)
                    {if((work++&63)==0)token.ThrowIfCancellationRequested();if(visited.Add(i)&&Source[i].Hits(b))return true;}
                return false;
            }
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,WallIndex> WallIndices=new System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,WallIndex>();
        private sealed class BoundaryIndex
        {
            internal readonly T[] Source;
            private readonly Dictionary<(int,int),List<T>> cells=new Dictionary<(int,int),List<T>>();
            private static int Cell(float v)=>(int)Math.Floor(v/2);
            internal BoundaryIndex(T[] triangles,CancellationToken token)
            {
                Source=triangles;int work=0;
                foreach(var t in triangles)
                {
                    if((work++&63)==0)token.ThrowIfCancellationRequested();
                    for(int x=Cell(t.Bounds.Min.X);x<=Cell(t.Bounds.Max.X);x++)for(int z=Cell(t.Bounds.Min.Z);z<=Cell(t.Bounds.Max.Z);z++)
                    {if(!cells.TryGetValue((x,z),out var list))cells.Add((x,z),list=new List<T>());list.Add(t);}
                }
            }
            internal bool Contains(P p,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if(!cells.TryGetValue((Cell(p.X),Cell(p.Z)),out var triangles))return false;
                var crossings=new List<float>();
                int work=0;foreach(var t in triangles)
                {
                    if((work++&63)==0)token.ThrowIfCancellationRequested();
                    float ax=t.B.X-t.A.X,az=t.B.Z-t.A.Z,bx=t.C.X-t.A.X,bz=t.C.Z-t.A.Z;
                    float det=ax*bz-az*bx;if(Math.Abs(det)<1e-9f)continue;
                    float dx=p.X-t.A.X,dz=p.Z-t.A.Z,u=(dx*bz-dz*bx)/det,v=(ax*dz-az*dx)/det;
                    if(u<-.00001f||v<-.00001f||u+v>1.00001f)continue;
                    float y=t.A.Y+u*(t.B.Y-t.A.Y)+v*(t.C.Y-t.A.Y);
                    if(y>p.Y+.00001f&&!crossings.Any(hit=>Math.Abs(hit-y)<.00005f))crossings.Add(y);
                }
                return (crossings.Count&1)==1;
            }
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,BoundaryIndex> BoundaryIndices=new System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,BoundaryIndex>();
        internal static bool HitsWalls(B box,WeaponPlanGeometry g)
        {
            Cancel(g);
            if(g?.Walls==null||!BoxValid(box))return true;
            var index=WallIndices.GetValue(g,k=>new WallIndex(k.Walls,k.ValidationCancellation));
            if(!ReferenceEquals(index.Source,g.Walls)){WallIndices.Remove(g);index=WallIndices.GetValue(g,k=>new WallIndex(k.Walls,k.ValidationCancellation));}
            return index.Hits(box,g.ValidationCancellation);
        }
        private static bool ClearFixed(B box,WeaponPlanGeometry g,bool doorsOpen=false)
        {
            Cancel(g);
            var padded=Pad(box,g.Clearance);
            if(g.StaticBlockers.Any(b=>b.Intersects(padded))||(doorsOpen?g.OpenDoorObstacles:g.DoorSweeps).Any(b=>b.Intersects(padded)))return false;
            return !HitsWalls(padded,g);
        }
        private static bool InsideBoundary(B box,WeaponPlanGeometry g)
        {
            Cancel(g);
            if(g.ContainmentWalls==null||g.ContainmentWalls.Length==0)return true;
            var index=BoundaryIndices.GetValue(g,k=>new BoundaryIndex(k.ContainmentWalls,k.ValidationCancellation));
            if(!ReferenceEquals(index.Source,g.ContainmentWalls)){BoundaryIndices.Remove(g);index=BoundaryIndices.GetValue(g,k=>new BoundaryIndex(k.ContainmentWalls,k.ValidationCancellation));}
            for(int corner=0;corner<9;corner++)
            {
                var p=corner==8?new P((box.Min.X+box.Max.X)*.5f,(box.Min.Y+box.Max.Y)*.5f,(box.Min.Z+box.Max.Z)*.5f):
                    new P((corner&1)==0?box.Min.X:box.Max.X,(corner&2)==0?box.Min.Y:box.Max.Y,(corner&4)==0?box.Min.Z:box.Max.Z);
                if(!index.Contains(p,g.ValidationCancellation))return false;
            }
            return true;
        }
        // A proof accumulator holds the COMPLETE accepted draft. A candidate
        // must pass static geometry, every reserved capsule, serial gun motion,
        // its real round routes and all earlier retained release corridors.
        // Previous gun motion is immutable once its in-chamber solid keep-out
        // is reserved; adding ordinary stock cannot invalidate that proof.
        private sealed class PackingProof
        {
            private sealed class Entry
            {
                internal WeaponPlacement Placement;
                internal B[] Reserved;
                internal readonly List<WeaponRoute> Routes=new List<WeaponRoute>();
            }
            private readonly WeaponPlanGeometry geometry;
            private readonly List<Entry> entries=new List<Entry>();
            internal PackingProof(WeaponPlanGeometry g){geometry=g;}
            private List<Entry> Ordered(IEnumerable<Entry> source)=>!geometry.DirectDropOnly?source.ToList():source
                .Select((e,i)=>new{E=e,I=i}).OrderBy(v=>v.E.Placement.Spec.Kind==SpatialWeaponKind.Gun?0:1)
                .ThenBy(v=>v.E.Placement.Spec.Kind==SpatialWeaponKind.Gun?0:StowedBounds(v.E.Placement,geometry).Min.Y)
                .ThenBy(v=>v.I).Select(v=>v.E).ToList();
            internal WeaponPlacement[] Items=>Ordered(entries).Select(e=>e.Placement).ToArray();
            internal bool Add(WeaponPlacement candidate)
            {
                Cancel(geometry);
                var entry=new Entry{Placement=candidate,Reserved=ReservedVolumes(candidate,geometry)};
                if(entries.Any(e=>e.Reserved.Any(a=>entry.Reserved.Any(b=>Pad(a,geometry.Clearance).Intersects(b)))))return false;
                var check=TryValidate(new[]{candidate},geometry,false);
                if(!check.PlacementValid||entries.Sum(e=>(double)e.Placement.Spec.MassKg)+candidate.Spec.MassKg>geometry.MaxMassKg+E)return false;
                var order=Ordered(entries.Concat(new[]{entry}));var plan=order.Select(e=>e.Placement).ToArray();int index=order.IndexOf(entry);
                if(!geometry.DirectDropOnly)
                {
                    // Legacy translated conveyors may find a different route;
                    // never apply direct-drop monotonicity to those geometries.
                    if(!TryValidate(plan,geometry).CanApplyNative)return false;
                }
                else
                {
                    if(candidate.Spec.Kind==SpatialWeaponKind.Gun)
                    {
                        var parked=order.Select(e=>e.Reserved).ToArray();
                        for(int i=0;i<plan.Length;i++)if(plan[i].Spec.Kind==SpatialWeaponKind.Gun&&
                            !PathFits(plan,i,parked,geometry,new List<string>()))return false;
                    }
                    // The route proof for an earlier store remains valid only
                    // if new retained stock (or the new deployed gun) clears
                    // the already measured whole continuous downward sweep.
                    var obstacles=new List<B>(entry.Reserved);
                    if(candidate.Spec.Kind==SpatialWeaponKind.Gun)
                    {
                        var path=candidate.Spec.MotionProfile?.Steps;
                        if(path==null||path.Length<2)return false;
                        obstacles.AddRange((path[path.Length-1].Volumes??new[]{path[path.Length-1].SweptBounds})
                            .Select(b=>TransformRelative(b,candidate,geometry)));
                    }
                    for(int i=0;i<order.Count;i++)if(order[i]!=entry&&order[i].Routes.Count>0&&
                        (candidate.Spec.Kind==SpatialWeaponKind.Gun||i<index))
                        foreach(var path in order[i].Routes)if(!RouteClearOfLiveBoxes(path,obstacles,geometry.Clearance))return false;
                    if(candidate.Spec.Kind!=SpatialWeaponKind.Gun)
                    {
                        var released=new HashSet<int>();
                        for(int n=0;n<Math.Max(1,candidate.Spec.Rounds?.Length??0);n++)
                        {
                            if(!TryNextRoundRoute(plan,index,released,geometry,out var route,out _))return false;
                            released.Add(route.RoundIndex);entry.Routes.Add(route);
                        }
                    }
                }
                entries.Add(entry);return true;
            }
        }
        // Pure worker entry point; no Unity/native API or frame-thread work.
        // A completed fill may add fewer than requested, but it returns only a
        // strict, fully validated COMPLETE draft. Invalid baseline is untouched.
        internal static WeaponPlanResolveResult TryFillAvailable(IReadOnlyList<WeaponPlacement> plan,WeaponItemSpec spec,
            WeaponPlanGeometry g,int requested,int maxItems=1024,int maxMilliseconds=10000)
        {
            Cancel(g);var original=plan?.ToArray()??Array.Empty<WeaponPlacement>();
            var result=new WeaponPlanResolveResult{Items=original};
            if(g==null||plan==null||spec==null||requested<1||original.Length>=maxItems||maxMilliseconds<1)
            {result.Reason="Missing draft/item/geometry or no requested free item slots.";return result;}
            using(var budget=CancellationTokenSource.CreateLinkedTokenSource(g.ValidationCancellation))
            {
                budget.CancelAfter(maxMilliseconds);
                try
                {
                    var local=CloneGeometryForValidation(g,budget.Token);
                    var ordered=OrderedForRelease(original,local);var initial=TryValidate(ordered,local);
                    if(!initial.CanApplyNative){result.Validation=initial;result.Reason="Resolve the current draft's conflicts before adding items.";return result;}
                    var proof=new PackingProof(local);
                    foreach(var item in ordered)if(!proof.Add(item))throw new InvalidOperationException("Validated baseline lost its incremental packing proof.");
                    int target=Math.Min(requested,maxItems-original.Length);
                    var placed=new WeaponPlacement[maxItems];var assigned=new bool[maxItems];
                    for(int i=0;i<ordered.Length;i++){placed[i]=ordered[i];assigned[i]=true;}
                    var source=new WeaponPlacement(spec,0,0,spec.DefaultY);
                    foreach(var candidate in ResolveCandidates(source,placed,assigned,local,false))
                    {
                        Cancel(local);result.CandidatesTested++;
                        if(!proof.Add(candidate))continue;
                        int at=original.Length+result.ItemsAdded;placed[at]=candidate;assigned[at]=true;
                        result.ItemsAdded++;g.ValidationProgress?.Invoke(result.ItemsAdded,target);Cancel(g);Cancel(local);
                        if(result.ItemsAdded>=target)break;
                    }
                    var packed=proof.Items;var final=TryValidate(packed,local);
                    if(!final.CanApplyNative){result.ItemsAdded=0;result.Validation=final;result.Reason="No complete addition was proven; original draft retained.";return result;}
                    result.Success=true;result.Items=packed;result.Validation=final;
                    result.Reason="Added "+result.ItemsAdded+" item(s); complete capsule, deployment and lower-first release checks passed.";return result;
                }
                catch(OperationCanceledException) when(!g.ValidationCancellation.IsCancellationRequested&&budget.IsCancellationRequested)
                {result.ItemsAdded=0;result.BudgetExhausted=true;result.Reason="Packing time budget reached; original complete draft retained.";return result;}
            }
        }
        // Candidate/time limits terminate the search, never approve a partial
        // layout. Lower-first release sorting is explicit in direct-drop mode.
        internal static WeaponPlanResolveResult TryResolveConflicts(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g,
            int maxCandidates=25000,int maxMilliseconds=5000)
        {
            Cancel(g);var original=plan?.ToArray()??Array.Empty<WeaponPlacement>();
            var result=new WeaponPlanResolveResult{Items=original};
            if(g==null||plan==null||maxCandidates<1||maxMilliseconds<1||original.Any(p=>p.Spec==null))
            {result.Reason="Missing complete draft/geometry or invalid search budget.";return result;}
            var clock=System.Diagnostics.Stopwatch.StartNew();
            using(var budget=CancellationTokenSource.CreateLinkedTokenSource(g.ValidationCancellation))
            {
                budget.CancelAfter(maxMilliseconds);
                try
                {
                    bool Expired()=>result.CandidatesTested>=maxCandidates||clock.ElapsedMilliseconds>=maxMilliseconds;
                    var local=CloneGeometryForValidation(g,budget.Token);
                    var ordered=OrderedForRelease(original,local);
                    // Cheap placement first: do not spend the budget proving
                    // release routes for hundreds of overlapping original items.
                    var first=TryValidate(ordered,local,false);
                    if(first.PlacementValid)
                    {
                        first=TryValidate(ordered,local);
                        if(first.CanApplyNative){result.Success=true;result.Items=ordered;result.Validation=first;result.Reason="Complete layout valid; release layers ordered from the floor upward.";return result;}
                    }
                    if(original.Sum(p=>(double)p.Spec.MassKg)>g.MaxMassKg+E)
                    {result.Validation=first;result.Reason="Existing items exceed the configured physical mass capacity; no item removed.";return result;}
                    var indices=Enumerable.Range(0,original.Length).OrderBy(i=>original[i].Spec.Kind==SpatialWeaponKind.Gun?0:1)
                        .ThenByDescending(i=>original[i].Spec.Width*original[i].Spec.Length).ThenBy(i=>i).ToArray();
                    for(int attempt=0;attempt<2&&!Expired();attempt++)
                    {
                        var proof=new PackingProof(local);var placed=new WeaponPlacement[original.Length];var assigned=new bool[original.Length];bool complete=true;
                        var cursors=new Dictionary<WeaponItemSpec,IEnumerator<WeaponPlacement>>();
                        try
                        {
                            foreach(int index in indices)
                            {
                                Cancel(local);bool found=false;var spec=original[index].Spec;IEnumerator<WeaponPlacement> candidates;
                                if(!cursors.TryGetValue(spec,out candidates))cursors[spec]=candidates=ResolveCandidates(original[index],placed,assigned,local,false).GetEnumerator();
                                if(attempt==0&&!Expired())
                                {
                                    result.CandidatesTested++;
                                    var preserved=new WeaponPlacement(spec,original[index].X,original[index].Z,spec.HeightAdjustable?original[index].Y:spec.DefaultY,original[index].Rotation90);
                                    if(proof.Add(preserved)){placed[index]=preserved;assigned[index]=true;found=true;}
                                }
                                while(!found&&!Expired()&&candidates.MoveNext())
                                {
                                    Cancel(local);result.CandidatesTested++;
                                    var candidate=candidates.Current;
                                    if(!proof.Add(candidate))continue;
                                    placed[index]=candidate;assigned[index]=true;found=true;break;
                                }
                                if(!found){complete=false;break;}
                                g.ValidationProgress?.Invoke(assigned.Count(v=>v),original.Length);Cancel(g);Cancel(local);
                            }
                            if(complete)
                            {
                                var packed=proof.Items;var validation=TryValidate(packed,local);
                                if(validation.CanApplyNative)
                                {
                                    result.Success=true;result.Items=packed;result.Validation=validation;
                                    result.ItemsMoved=placed.Where((p,i)=>!SamePose(p,original[i])).Count();
                                    result.Reason="All items retained; complete geometry/deployment checks and lower-first release order passed.";return result;
                                }
                                result.Validation=validation;
                            }
                        }
                        finally{foreach(var cursor in cursors.Values)cursor.Dispose();}
                    }
                    result.Validation=result.Validation??first;result.BudgetExhausted=Expired();
                    result.Reason=result.BudgetExhausted?"Search budget reached; original complete draft retained.":"No complete arrangement proven; original draft retained. Move large modules manually.";return result;
                }
                catch(OperationCanceledException) when(!g.ValidationCancellation.IsCancellationRequested&&budget.IsCancellationRequested)
                {result.Items=original;result.BudgetExhausted=true;result.Reason="Search time budget reached; original complete draft retained.";return result;}
            }
        }
        private static bool SamePose(WeaponPlacement a,WeaponPlacement b)=>a.Spec==b.Spec&&Math.Abs(a.X-b.X)<E&&Math.Abs(a.Y-b.Y)<E&&Math.Abs(a.Z-b.Z)<E&&a.Rotation90==b.Rotation90;
        private static IEnumerable<WeaponPlacement> ResolveCandidates(WeaponPlacement source,WeaponPlacement[] placed,bool[] assigned,WeaponPlanGeometry g,bool preserve)
        {
            Cancel(g);var spec=source.Spec;
            if(preserve)yield return new WeaponPlacement(spec,source.X,source.Z,spec.HeightAdjustable?source.Y:spec.DefaultY,source.Rotation90);
            var rotations=spec.AllowRotation90?new[]{source.Rotation90,!source.Rotation90}:new[]{false};
            foreach(bool rotation in rotations)
            {
                var at=new WeaponPlacement(spec,0,0,spec.DefaultY,rotation);var bounds=Union(ReservedVolumes(at,g));
                float xOffset=bounds.Min.X-g.LeftX,zOffset=g.FrontZ-bounds.Max.Z;
                float width=bounds.Max.X-bounds.Min.X,length=bounds.Max.Z-bounds.Min.Z,height=bounds.Max.Y-bounds.Min.Y;
                if(!F(width)||!F(length)||width<=0||length<=0)continue;
                float margin=Math.Max(.002f,g.Clearance+.002f),minX=margin-xOffset,minZ=margin-zOffset;
                float maxX=g.Width-margin-width-xOffset,maxZ=g.Length-margin-length-zOffset;
                if(maxX<minX-E||maxZ<minZ-E)continue;
                var heights=new List<float>{spec.DefaultY};
                if(spec.HeightAdjustable)
                {
                    float increment=Math.Max(.1f,height+margin);
                    for(float y=spec.DefaultY+increment;y+height<=g.Height+E;y+=increment)heights.Add(y);
                    for(int i=0;i<placed.Length;i++)if(assigned[i])
                    {float y=Union(ReservedVolumes(placed[i],g)).Max.Y-g.FloorY+margin;if(y+height<=g.Height+E)heights.Add(y);}
                }
                var levels=heights.Distinct().OrderBy(v=>v).ToArray();
                // Exhaust the cheap module-sized lattice across ALL layers
                // before a fine-grid fallback. Previously a nearly full lower
                // layer caused tens of thousands of futile candidates while
                // the upper layers were still completely empty.
                foreach(float y in levels)
                for(float z=minZ;z<=maxZ+E;z+=Math.Max(.1f,length+margin))
                {
                    if(spec.Kind==SpatialWeaponKind.Gun)
                    {Cancel(g);yield return new WeaponPlacement(spec,(minX+maxX)*.5f,z,y,rotation);}
                    for(float x=minX;x<=maxX+E;x+=Math.Max(.1f,width+margin))
                    {Cancel(g);yield return new WeaponPlacement(spec,x,z,y,rotation);}
                }
                // Try contact edges without forming their quadratic Cartesian
                // product. Edges against a coarse row/column and actual object
                // corners cover the useful narrow gaps in a mixed layout.
                foreach(float y in levels)
                {
                    var xs=new List<float>{minX,maxX};var zs=new List<float>{minZ,maxZ};
                    for(int i=0;i<placed.Length;i++)if(assigned[i])
                    {
                        var other=Union(ReservedVolumes(placed[i],g));
                        xs.Add(other.Max.X-g.LeftX+margin-xOffset);xs.Add(other.Min.X-g.LeftX-width-margin-xOffset);
                        zs.Add(g.FrontZ-other.Min.Z+margin-zOffset);zs.Add(g.FrontZ-other.Max.Z-length-margin-zOffset);
                    }
                    var edgesX=xs.Where(v=>v>=minX-E&&v<=maxX+E).Select(v=>(float)(Math.Round(v*10000)/10000)).Distinct().ToArray();
                    var edgesZ=zs.Where(v=>v>=minZ-E&&v<=maxZ+E).Select(v=>(float)(Math.Round(v*10000)/10000)).Distinct().ToArray();
                    foreach(float x in edgesX)for(float z=minZ;z<=maxZ+E;z+=Math.Max(.1f,length+margin))
                    {Cancel(g);yield return new WeaponPlacement(spec,x,z,y,rotation);}
                    foreach(float z in edgesZ)for(float x=minX;x<=maxX+E;x+=Math.Max(.1f,width+margin))
                    {Cancel(g);yield return new WeaponPlacement(spec,x,z,y,rotation);}
                }
                // Arbitrary translated positions remain available as a bounded
                // fallback; no hardpoint-count limit or maximum-fit claim.
                foreach(float y in levels)
                for(float z=minZ;z<=maxZ+E;z+=.25f)for(float x=minX;x<=maxX+E;x+=.25f)
                {Cancel(g);yield return new WeaponPlacement(spec,x,z,y,rotation);}
            }
        }
        internal static WeaponPlanValidation TryValidate(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g,bool validateRoutes=true)
            =>ValidateCore(plan,g,validateRoutes,false);
        // Restore is deliberately not a release approval. Saved stock is
        // checked for physical placement and gun deployment, while every store
        // still needs a live per-round route before it can leave the aircraft.
        internal static WeaponPlanValidation TryValidateForRestore(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g)
            =>ValidateCore(plan,g,true,true);
        private static bool WallsFinite(T[] walls,WeaponPlanGeometry g)
        {
            for(int i=0;i<walls.Length;i++)
            {if((i&63)==0)Cancel(g);var t=walls[i];if(!PointFinite(t.A)||!PointFinite(t.B)||!PointFinite(t.C))return false;}
            return true;
        }
        private static WeaponPlanValidation ValidateCore(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g,bool validateRoutes,bool restoreOnly)
        {
            Cancel(g);
            var r=new WeaponPlanValidation();
            if(plan==null||g==null||!F(g.Width)||!F(g.Length)||!F(g.Height)||!F(g.FloorY)||!F(g.FrontZ)||!F(g.LeftX)||
                g.Width<=0||g.Length<=0||g.Height<=0||float.IsNaN(g.MaxMassKg)||g.MaxMassKg<=0||!F(g.Clearance)||g.Clearance<0||
                !F(g.OuterClearY)||g.OuterClearY>=g.FloorY||(!float.IsNaN(g.MinimumY)&&(!F(g.MinimumY)||g.MinimumY>g.FloorY))||
                g.Walls==null||g.StaticBlockers==null||g.ExitZones==null||g.DoorSweeps==null||g.OpenDoorObstacles==null||
                g.DoorSweeps.Any(b=>!BoxValid(b))||g.OpenDoorObstacles.Any(b=>!BoxValid(b))||
                g.StaticBlockers.Any(b=>!BoxValid(b))||!WallsFinite(g.Walls,g)||
                g.ExitZones.Any(z=>z.Cell<=0||!F(z.Left)||!F(z.Right)||!F(z.Aft)||!F(z.Front)||z.Right<=z.Left||z.Front<=z.Aft))
            {r.Issues.Add("placement: missing/invalid measured bay geometry or mass capacity");return r;}
            int progress=0,total=plan.Count*(validateRoutes?2:1);
            if(validateRoutes&&!restoreOnly)foreach(var p in plan)
            {Cancel(g);if(p.Spec!=null&&p.Spec.Kind!=SpatialWeaponKind.Gun)total+=Math.Max(1,p.Spec.Rounds?.Length??0);}
            g.ValidationProgress?.Invoke(0,total);Cancel(g);
            var boxes=new B[plan.Count][];bool valid=true;
            for(int i=0;i<plan.Count;i++)
            {
                Cancel(g);
                var p=plan[i];var s=p.Spec;
                if(s==null||string.IsNullOrEmpty(s.Id)||!F(s.Width)||!F(s.Length)||!F(s.Height)||!F(s.MassKg)||
                    s.Width<=0||s.Length<=0||s.Height<=0||s.MassKg<=0||!F(p.X)||!F(p.Y)||!F(p.Z)||!Enum.IsDefined(typeof(SpatialWeaponKind),s.Kind)||
                    (p.Rotation90&&!s.AllowRotation90))
                {r.Issues.Add("placement["+i+"]: invalid measured item or unsupported rotation");valid=false;g.ValidationProgress?.Invoke(++progress,total);continue;}
                if(!s.HeightAdjustable&&Math.Abs(p.Y-s.DefaultY)>.002f)
                {r.Issues.Add("placement["+i+"]: native gun stow height/active pivot must be preserved");valid=false;}
                boxes[i]=ReservedVolumes(p,g);r.TotalMassKg+=s.MassKg;
                if(boxes[i].Any(b=>!BoxValid(b)||!Contains(g.Envelope,b)||!InsideBoundary(b,g))){r.Issues.Add("placement["+i+"]: outside measured chamber including curved walls/height/bulkheads");valid=false;}
                if(boxes[i].Any(b=>!ClearFixed(b,g))){r.Issues.Add("placement["+i+"]: intersects actual wall/structure/fuel or door opening sweep");valid=false;}
                for(int j=0;j<i;j++)
                {
                    if((j&63)==0)Cancel(g);
                    if(boxes[j]!=null&&boxes[j].Any(a=>boxes[i].Any(b=>Pad(a,g.Clearance).Intersects(b))))
                    {r.Issues.Add("placement["+i+"]: overlaps complete item/array envelope["+j+"]");valid=false;}
                }
                g.ValidationProgress?.Invoke(++progress,total);Cancel(g);
            }
            if(r.TotalMassKg>g.MaxMassKg+E){r.Issues.Add("placement: full weapon/mechanism/ammunition mass exceeds bay capacity");valid=false;}
            r.OverNominalMass=r.TotalMassKg>g.WarningMassKg+E;
            if(r.OverNominalMass)r.Warnings.Add("mass: exceeds nominal native MTOW budget; native overweight behaviour is preserved");
            r.PlacementValid=valid;
            if(!valid)return r;
            r.LayoutFingerprint=Fingerprint(plan,g);
            if(!validateRoutes){r.ReleasePathPending=plan.Any(p=>p.Spec.Kind!=SpatialWeaponKind.Gun);return r;}
            var integration=g.NativeIntegration;
            // Source hashes identify measurements. They never approve a layout.
            bool native=integration!=null&&integration.SpawnSaveRestoreValidated&&integration.CompoundPlacementSupported&&
                (plan.All(p=>Math.Abs(p.Y)<E)||integration.LayeredPlacementSupported)&&g.DoorSweepMeasured;
            bool guns=true;bool release=false;
            for(int i=0;i<plan.Count;i++)
            {
                Cancel(g);
                bool route;
                if(plan[i].Spec.Kind==SpatialWeaponKind.Gun)route=PathFits(plan,i,boxes,g,r.Issues);
                else if(restoreOnly)
                {
                    release=true;g.ValidationProgress?.Invoke(++progress,total);Cancel(g);continue;
                }
                else
                {
                    route=true;var rounds=plan[i].Spec.Rounds;var released=new HashSet<int>();
                    for(int n=0;n<Math.Max(1,rounds?.Length??0);n++)
                    {
                        Cancel(g);
                        if(!TryNextRoundRoute(plan,i,released,g,out var path,out var reason)){r.Issues.Add("routing["+i+","+n+"]: "+reason);route=false;break;}
                        released.Add(path.RoundIndex);
                        g.ValidationProgress?.Invoke(++progress,total);Cancel(g);
                    }
                    if((rounds?.Length??0)>1&&!(integration?.PerRoundReleaseSupported??false))route=false;
                }
                if(plan[i].Spec.Kind==SpatialWeaponKind.Gun)guns&=route&&native&&integration.ContinuousDeployValidated;
                else release|=!route||!native||!integration.ArbitraryReleaseValidated;
                g.ValidationProgress?.Invoke(++progress,total);Cancel(g);
            }
            r.DeploymentProven=guns&&(!restoreOnly||native);r.ReleasePathPending=release;
            r.CanApplyNative=!restoreOnly&&native&&guns&&!release;
            if(!native)r.Issues.Add("native: required spawn/save/compound/layer/door-sweep capability is unavailable; preview only");
            else if(!restoreOnly&&!r.CanApplyNative)r.Issues.Add("native: required arbitrary release/deploy engine capability or complete physical routing is pending");
            return r;
        }
        private static bool PathFits(IReadOnlyList<WeaponPlacement> plan,int index,B[][] parked,WeaponPlanGeometry g,List<string> issues)
        {
            Cancel(g);
            var p=plan[index];var profile=p.Spec.MotionProfile;
            if(profile==null||string.IsNullOrEmpty(profile.Id)||string.IsNullOrEmpty(profile.MeasurementSourceHash)||
                string.IsNullOrEmpty(p.Spec.MeasurementSourceHash)||profile.Steps==null||profile.Steps.Length<2)
            {issues.Add("routing["+index+"]: measured continuous gun-deploy or store-release path is pending");return false;}
            // Deployment is translated/rotated with the actual placement. No
            // source anchor whitelist is used to bless or prohibit a layout.
            for(int n=0;n<profile.Steps.Length;n++)
            {
                Cancel(g);
                var step=profile.Steps[n];
                var volumes=step.Volumes??new[]{step.SweptBounds};
                if(volumes.Length==0||volumes.Any(b=>!BoxValid(b))){issues.Add("routing["+index+"]: invalid continuous swept volume");return false;}
                var boxes=volumes.Select(b=>TransformRelative(b,p,g)).ToArray();var box=Union(boxes);
                if(n==0&&boxes.Any(b=>!parked[index].Any(a=>Contains(Pad(a,E),b)))){issues.Add("routing["+index+"]: path starts outside reserved item compound");return false;}
                if(n>0&&!Pad(TransformRelative(profile.Steps[n-1].SweptBounds,p,g),E).Intersects(box))
                {issues.Add("routing["+index+"]: disconnected measured volumes would teleport across the hull");return false;}
                if(boxes.Any(b=>!ClearFixed(b,g,true))){issues.Add("routing["+index+"]: swept path hits structure/fuel/liner/open doors");return false;}
                for(int j=index+1;j<parked.Length;j++)if(parked[j].Any(a=>boxes.Any(b=>Pad(a,g.Clearance).Intersects(b))))
                {issues.Add("routing["+index+"]: cannot move through retained item/array["+j+"]");return false;}
                // Gear-up deploys the battery while all bombs/missiles may
                // still be aboard. Their list position is release order, not
                // evidence that they disappeared before a gun was lowered.
                if(p.Spec.Kind==SpatialWeaponKind.Gun)
                for(int j=0;j<index;j++)if(plan[j].Spec.Kind!=SpatialWeaponKind.Gun&&
                    parked[j].Any(a=>boxes.Any(b=>Pad(a,g.Clearance).Intersects(b))))
                {issues.Add("routing["+index+"]: gun lift crosses retained stock["+j+"]");return false;}
                for(int j=0;j<index;j++)if(plan[j].Spec.Kind!=SpatialWeaponKind.Gun&&
                    (plan[j].Spec.FixedVolumes??Array.Empty<B>()).Any(a=>boxes.Any(b=>Pad(TransformRelative(a,plan[j],g),g.Clearance).Intersects(b))))
                {issues.Add("routing["+index+"]: crosses retained launcher hardware["+j+"]");return false;}
                // Cradles deploy serially in placement order and retract in
                // reverse. Previously deployed guns remain real obstacles.
                if(p.Spec.Kind==SpatialWeaponKind.Gun)
                for(int j=0;j<index;j++)
                {
                    var previous=plan[j];var path=previous.Spec.MotionProfile;
                    if(previous.Spec.Kind!=SpatialWeaponKind.Gun||path?.Steps==null||path.Steps.Length==0)continue;
                    var last=path.Steps[path.Steps.Length-1];var deployed=(last.Volumes??new[]{last.SweptBounds}).Select(b=>TransformRelative(b,previous,g));
                    if(deployed.Concat(ReservedVolumes(previous,g)).Any(a=>boxes.Any(b=>Pad(a,g.Clearance).Intersects(b))))
                    {issues.Add("routing["+index+"]: crosses an earlier deployed gun["+j+"]");return false;}
                }
                foreach(var component in boxes)if(component.Min.Y>=g.Envelope.Min.Y-E)
                {
                    if(!Contains(g.Envelope,component)){issues.Add("routing["+index+"]: internal manoeuvre leaves chamber");return false;}
                }
                else if(component.Max.Y>g.OuterClearY+E&&!profile.AllowsPartialExterior)
                {
                    if(!g.ExitZones.Any(z=>z.Cell==step.ExitCell&&z.ContainsXZ(Pad(component,g.Clearance),E)))
                    {issues.Add("routing["+index+"]: gun/store crosses hull outside an aligned native bay throat");return false;}
                }
                // A final exterior endpoint is allowed only after the ENTIRE
                // moving envelope clears the hull, and still checks blockers.
            }
            var final=TransformRelative(profile.Steps[profile.Steps.Length-1].SweptBounds,p,g);
            if(!profile.AllowsPartialExterior&&final.Max.Y>g.OuterClearY+E){issues.Add("routing["+index+"]: no complete clearance below hull at release endpoint");return false;}
            return true;
        }
        internal static bool TryRoute(IReadOnlyList<WeaponPlacement> plan,int index,WeaponPlanGeometry g,out WeaponRoute route,out string reason)
        {
            Cancel(g);
            route=null;reason=null;
            if(plan==null||index<0||index>=plan.Count||g==null||plan[index].Spec==null){reason="missing placement/geometry";return false;}
            if(plan[index].Spec.Kind==SpatialWeaponKind.Gun)
            {
                var issues=new List<string>();var parked=plan.Select(p=>ReservedVolumes(p,g)).ToArray();
                if(!PathFits(plan,index,parked,g,issues)){reason=string.Join("; ",issues);return false;}
                route=new WeaponRoute{Waypoints=new[]{Anchor(plan[index],g)},SweptVolumes=plan[index].Spec.MotionProfile.Steps};return true;
            }
            if((plan[index].Spec.Rounds?.Length??0)>1){reason="rack requires individual real-rail release";return false;}
            return TryRouteRound(plan,index,0,new HashSet<int>(),g,out route,out reason);
        }
        internal static bool TryNextRoundRoute(IReadOnlyList<WeaponPlacement> plan,int index,ISet<int> released,WeaponPlanGeometry g,out WeaponRoute route,out string reason)
        {
            Cancel(g);
            route=null;reason="no retained round has a clear physical route";
            if(plan==null||index<0||index>=plan.Count||plan[index].Spec==null)return false;
            int count=Math.Max(1,plan[index].Spec.Rounds?.Length??0);
            // Prefer the rail nearest the usable throat; never move the entire
            // rack through a mouth narrower than its union envelope.
            var order=Enumerable.Range(0,count).Where(n=>released==null||!released.Contains(n)).OrderBy(n=>
                plan[index].Spec.Rounds.Length==0?0:Math.Abs(Rotate(plan[index].Spec.Rounds[n].AnchorOffset,plan[index].Rotation90).X));
            foreach(int n in order){Cancel(g);if(TryRouteRound(plan,index,n,released,g,out route,out reason))return true;}
            return false;
        }
        internal static bool TryRouteRound(IReadOnlyList<WeaponPlacement> plan,int index,int roundIndex,ISet<int> released,WeaponPlanGeometry g,out WeaponRoute route,out string reason)
        {
            Cancel(g);
            route=null;reason=null;
            if(plan==null||index<0||index>=plan.Count||g==null||plan[index].Spec==null||g.ExitZones==null||g.Walls==null||
                !F(g.RoutingStep)||g.RoutingStep<.05f){reason="missing measured routing geometry";return false;}
            var placement=plan[index];var spec=placement.Spec;
            if(spec.Kind==SpatialWeaponKind.Gun){reason="gun requires native compound deployment profile";return false;}
            var rounds=spec.Rounds??Array.Empty<WeaponRoundSpec>();
            if(roundIndex<0||roundIndex>=Math.Max(1,rounds.Length)||(released?.Contains(roundIndex)??false))
            {reason="round is absent/already released";return false;}
            var round=rounds.Length==0?null:rounds[roundIndex];
            var offset=round?.AnchorOffset??new P(0,0,0);var rotated=Rotate(offset,placement.Rotation90);var itemAnchor=Anchor(placement,g);
            var start=new P(itemAnchor.X+rotated.X,itemAnchor.Y+rotated.Y,itemAnchor.Z+rotated.Z);
            var moving=(round?.Volumes??RelativeVolumes(spec)).Select(b=>Rotate(Shift(b,new P(-offset.X,-offset.Y,-offset.Z)),placement.Rotation90)).ToArray();
            if(moving.Length==0||moving.Any(b=>!BoxValid(b))){reason="round's complete measured geometry is missing";return false;}
            var obstacles=new List<B>();
            for(int i=0;i<plan.Count;i++)
            {
                Cancel(g);
                var other=plan[i];if(other.Spec==null)continue;
                if(i==index)
                {
                    obstacles.AddRange((spec.FixedVolumes??Array.Empty<B>()).Select(b=>TransformRelative(b,placement,g)));
                    for(int n=0;n<rounds.Length;n++)if(n!=roundIndex&&!(released?.Contains(n)??false))
                        obstacles.AddRange(rounds[n].Volumes.Select(b=>TransformRelative(b,placement,g)));
                }
                else if(other.Spec.Kind==SpatialWeaponKind.Gun)
                {
                    var profile=other.Spec.MotionProfile;
                    if(profile?.Steps==null||profile.Steps.Length==0){reason="earlier gun has no measured deployed envelope";return false;}
                    obstacles.AddRange((profile.Steps[profile.Steps.Length-1].Volumes??new[]{profile.Steps[profile.Steps.Length-1].SweptBounds}).Select(b=>TransformRelative(b,other,g)));
                    obstacles.AddRange(ReservedVolumes(other,g));
                }
                else if(i>index)obstacles.AddRange(ReservedVolumes(other,g));
                else obstacles.AddRange((other.Spec.FixedVolumes??Array.Empty<B>()).Select(b=>TransformRelative(b,other,g)));
            }
            bool Clear(IEnumerable<B> boxes,bool internalOnly)
            {
                Cancel(g);
                foreach(var box in boxes)
                {Cancel(g);if((internalOnly&&(!Contains(g.Envelope,box)||!InsideBoundary(box,g)))||!ClearFixed(box,g,true)||obstacles.Any(b=>Pad(b,g.Clearance).Intersects(box)))return false;}
                return true;
            }
            B[] At(P p)=>moving.Select(b=>Shift(b,p)).ToArray();
            B[] Sweep(P a,P b)=>moving.Select(v=>Union(new[]{Shift(v,a),Shift(v,b)})).ToArray();
            var relativeUnion=Union(moving);
            if(!Clear(At(start),true)){reason="round starts intersecting retained stock/actual compartment/open doors";return false;}
            bool ExitFrom(P here,out P end,out int cell)
            {
                end=default;cell=0;var current=Union(At(here));
                foreach(var exit in g.ExitZones)
                {
                    if(!exit.ContainsXZ(Pad(current,g.Clearance),E))continue;
                    var below=new P(here.X,g.OuterClearY-relativeUnion.Max.Y-.02f,here.Z);
                    if(!Clear(Sweep(here,below),false))continue;
                    end=below;cell=exit.Cell;return true;
                }
                return false;
            }
            if(g.DirectDropOnly)
            {
                if(!ExitFrom(start,out var end,out int cell))
                {reason="direct vertical release is blocked by a retained item or the physical bay";return false;}
                route=new WeaponRoute{Waypoints=new[]{start,end},RoundIndex=roundIndex,ExitCell=cell,
                    SweptVolumes=new[]{new WeaponPathStep(Sweep(start,end),cell)}};return true;
            }
            var positions=new List<P>{start};var parents=new List<int>{-1};var queue=new Queue<int>();queue.Enqueue(0);
            var visited=new HashSet<(int,int,int)>{(0,0,0)};var keys=new List<(int,int,int)>{(0,0,0)};
            int found=-1,exitCell=0,expanded=0;P outside=default;float step=g.RoutingStep;
            // A finite three-dimensional graph. Every edge is tested as a full
            // continuous translated compound sweep, not just endpoint poses.
            while(queue.Count>0&&positions.Count<65000)
            {
                if((expanded++&63)==0)Cancel(g);
                int currentIndex=queue.Dequeue();var here=positions[currentIndex];
                if(ExitFrom(here,out outside,out exitCell)){found=currentIndex;break;}
                var key=keys[currentIndex];
                foreach(var d in new[]{(1,0,0),(-1,0,0),(0,-1,0),(0,0,1),(0,0,-1),(0,1,0)})
                {
                    var k=(key.Item1+d.Item1,key.Item2+d.Item2,key.Item3+d.Item3);
                    if(visited.Contains(k))continue;
                    var next=new P(start.X+k.Item1*step,start.Y+k.Item2*step,start.Z+k.Item3*step);
                    if(!Clear(Sweep(here,next),true))continue;
                    visited.Add(k);keys.Add(k);positions.Add(next);parents.Add(currentIndex);queue.Enqueue(positions.Count-1);
                }
            }
            if(found<0){reason=positions.Count>=65000?"physical route search budget exhausted; no path is approved":"no continuous route to native opening without crossing retained stock/walls/fuel/doors";return false;}
            var points=new List<P>();for(int n=found;n>=0;n=parents[n])points.Add(positions[n]);points.Reverse();points.Add(outside);
            // Collapse collinear graph edges only after their complete sweeps
            // pass again. Runtime receives absolute aircraft-local anchors.
            for(int n=points.Count-2;n>0;n--)
            {
                var a=points[n-1];var b=points[n];var c=points[n+1];
                bool straight=(Math.Abs(a.X-b.X)<E&&Math.Abs(b.X-c.X)<E&&Math.Abs(a.Y-b.Y)<E&&Math.Abs(b.Y-c.Y)<E)||
                    (Math.Abs(a.X-b.X)<E&&Math.Abs(b.X-c.X)<E&&Math.Abs(a.Z-b.Z)<E&&Math.Abs(b.Z-c.Z)<E)||
                    (Math.Abs(a.Y-b.Y)<E&&Math.Abs(b.Y-c.Y)<E&&Math.Abs(a.Z-b.Z)<E&&Math.Abs(b.Z-c.Z)<E);
                if(straight&&Clear(Sweep(a,c),n+1<points.Count-1))points.RemoveAt(n);
            }
            route=new WeaponRoute{Waypoints=points.ToArray(),RoundIndex=roundIndex,ExitCell=exitCell,
                SweptVolumes=Enumerable.Range(1,points.Count-1).Select(n=>new WeaponPathStep(Sweep(points[n-1],points[n]),exitCell)).ToArray()};
            return true;
        }
        private static string Fingerprint(IReadOnlyList<WeaponPlacement> plan,WeaponPlanGeometry g)
        {
            Cancel(g);
            using(var bytes=new MemoryStream())using(var w=new BinaryWriter(bytes))
            {
                w.Write("BG spatial armament measured capsule v4");w.Write(GeometryDigest(g));w.Write(g.CatalogSourceHash??"");w.Write(g.WarningMassKg);
                // Item order is also the serial cradle deployment order.
                foreach(var p in plan)
                {
                    Cancel(g);
                    var s=p.Spec;w.Write(s.Id);w.Write((int)s.Kind);w.Write(s.MeasurementSourceHash??"");
                    foreach(float f in new[]{s.Width,s.Length,s.Height,s.MassKg,p.X,p.Y,p.Z,s.DefaultY})w.Write(f==0?0:f);w.Write(p.Rotation90);w.Write(s.AllowRotation90);w.Write(s.HeightAdjustable);
                    foreach(var list in new[]{s.StowedVolumes,s.FixedVolumes}){w.Write(list?.Length??0);if(list!=null)foreach(var b in list)WriteBox(w,b);}
                    w.Write(s.Rounds?.Length??0);if(s.Rounds!=null)foreach(var round in s.Rounds){w.Write(round.NativeRailPath??"");foreach(float f in new[]{round.AnchorOffset.X,round.AnchorOffset.Y,round.AnchorOffset.Z})w.Write(f);foreach(var b in round.Volumes)WriteBox(w,b);}
                    var profile=s.MotionProfile;w.Write(profile?.Id??"");w.Write(profile?.MeasurementSourceHash??"");
                    w.Write(profile?.AllowedAnchorZone.HasValue??false);if(profile?.AllowedAnchorZone.HasValue==true)WriteBox(w,profile.AllowedAnchorZone.Value);
                    w.Write(profile?.Steps?.Length??0);
                    if(profile?.Steps!=null)foreach(var step in profile.Steps)
                    {Cancel(g);w.Write(step.ExitCell);var volumes=step.Volumes??new[]{step.SweptBounds};w.Write(volumes.Length);foreach(var b in volumes)WriteBox(w,b);}
                }
                w.Flush();using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-","");
            }
        }
        private sealed class GeometryIdentity
        {
            internal T[] Walls,Boundary;
            internal B[] Blockers,Sweeps,Open;
            internal WeaponExitZone[] Exits;
            internal string Source;
            internal float[] Values;
            internal byte[] Digest;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,GeometryIdentity> GeometryIdentities=new System.Runtime.CompilerServices.ConditionalWeakTable<WeaponPlanGeometry,GeometryIdentity>();
        private static byte[] GeometryDigest(WeaponPlanGeometry g)
        {
            Cancel(g);
            // Catalog geometry arrays are immutable after measurement. Replacing
            // an array, source hash or scalar invalidates this cache; callers
            // must never edit a triangle/box array in place. Fuel/MTOW warning
            // changes are deliberately written outside the cached geometry.
            var state=GeometryIdentities.GetValue(g,_=>new GeometryIdentity());
            var values=new[]{g.Width,g.Length,g.Height,g.LeftX,g.FrontZ,g.FloorY,g.MinimumY,g.OuterClearY,g.MaxMassKg,g.Clearance,g.DirectDropOnly?1f:0f};
            if(state.Digest!=null&&ReferenceEquals(state.Walls,g.Walls)&&ReferenceEquals(state.Boundary,g.ContainmentWalls)&&
                ReferenceEquals(state.Blockers,g.StaticBlockers)&&ReferenceEquals(state.Sweeps,g.DoorSweeps)&&ReferenceEquals(state.Open,g.OpenDoorObstacles)&&
                ReferenceEquals(state.Exits,g.ExitZones)&&state.Source==g.GeometrySourceHash&&state.Values.SequenceEqual(values))return state.Digest;
            using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream))
            {
                w.Write(g.GeometrySourceHash??"");foreach(float f in values)w.Write(f==0?0:f);
                foreach(var list in new[]{g.StaticBlockers,g.DoorSweeps,g.OpenDoorObstacles}){w.Write(list.Length);int n=0;foreach(var b in list){if((n++&63)==0)Cancel(g);WriteBox(w,b);}}
                w.Write(g.ExitZones.Length);foreach(var e in g.ExitZones){w.Write(e.Cell);w.Write(e.Left);w.Write(e.Right);w.Write(e.Aft);w.Write(e.Front);}
                foreach(var list in new[]{g.Walls,g.ContainmentWalls??Array.Empty<T>()})
                {w.Write(list.Length);int n=0;foreach(var t in list){if((n++&63)==0)Cancel(g);foreach(var p in new[]{t.A,t.B,t.C}){w.Write(p.X);w.Write(p.Y);w.Write(p.Z);}}}
                w.Flush();using(var sha=SHA256.Create())state.Digest=sha.ComputeHash(stream.ToArray());
            }
            state.Values=values;state.Walls=g.Walls;state.Boundary=g.ContainmentWalls;state.Blockers=g.StaticBlockers;state.Sweeps=g.DoorSweeps;state.Open=g.OpenDoorObstacles;
            state.Exits=g.ExitZones;state.Source=g.GeometrySourceHash;return state.Digest;
        }
        private static void WriteBox(BinaryWriter w,B box)
        {foreach(float f in new[]{box.Min.X,box.Min.Y,box.Min.Z,box.Max.X,box.Max.Y,box.Max.Z})w.Write(f==0?0:f);}
    }
}
