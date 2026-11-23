using System;
using System.Collections.Generic;
#if !FEED_PLANNER_ONLY
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
#endif

namespace Japensou.NuclearOption.Superheavy
{
    // This geometry-only planner is also compiled by the offline feeder audit.
    // A node is an authored physical store position, not an ammunition reserve.
    internal static class BomberFeedPlan
    {
        internal readonly struct Point
        {
            internal readonly float X, Y, Z;
            internal Point(float x, float y, float z) { X = x; Y = y; Z = z; }
            internal float Distance(Point p) => (float)Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z));
        }
        internal readonly struct Box
        {
            internal readonly Point Min, Max;
            internal Box(Point min, Point max) { Min = min; Max = max; }
            internal static Box Around(Point p, Point half) => new Box(
                new Point(p.X-half.X,p.Y-half.Y,p.Z-half.Z), new Point(p.X+half.X,p.Y+half.Y,p.Z+half.Z));
            internal bool Contains(Box b) => b.Min.X >= Min.X && b.Max.X <= Max.X &&
                b.Min.Y >= Min.Y && b.Max.Y <= Max.Y && b.Min.Z >= Min.Z && b.Max.Z <= Max.Z;
            internal bool Intersects(Box b) => Min.X < b.Max.X && Max.X > b.Min.X &&
                Min.Y < b.Max.Y && Max.Y > b.Min.Y && Min.Z < b.Max.Z && Max.Z > b.Min.Z;
        }
        internal readonly struct Exit
        {
            internal readonly Point Center;
            internal readonly float Width, Length, Drop;
            internal Exit(Point center, float width, float length, float drop)
                { Center = center; Width = width; Length = length; Drop = drop; }
            internal bool Fits(Point p) => Math.Abs(p.X-Center.X)+Half.X <= Width*.5f &&
                Math.Abs(p.Z-Center.Z)+Half.Z <= Length*.5f && Math.Abs(p.Y-Center.Y) < .025f;
        }
        internal readonly struct Triangle
        {
            internal readonly Point A,B,C;
            internal readonly Box Bounds;
            internal Triangle(Point a,Point b,Point c)
            {
                A=a;B=b;C=c;
                Bounds=new Box(new Point(Math.Min(a.X,Math.Min(b.X,c.X)),Math.Min(a.Y,Math.Min(b.Y,c.Y)),Math.Min(a.Z,Math.Min(b.Z,c.Z))),
                    new Point(Math.Max(a.X,Math.Max(b.X,c.X)),Math.Max(a.Y,Math.Max(b.Y,c.Y)),Math.Max(a.Z,Math.Max(b.Z,c.Z))));
            }
            private static Point Sub(Point a,Point b)=>new Point(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
            private static Point Cross(Point a,Point b)=>new Point(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
            private static float Dot(Point a,Point b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
            private static bool Separated(Point axis,Point a,Point b,Point c,Point h)
            {
                float pa=Dot(axis,a),pb=Dot(axis,b),pc=Dot(axis,c),radius=Math.Abs(axis.X)*h.X+Math.Abs(axis.Y)*h.Y+Math.Abs(axis.Z)*h.Z;
                return Math.Min(pa,Math.Min(pb,pc))>radius || Math.Max(pa,Math.Max(pb,pc)) < -radius;
            }
            internal bool Hits(Box box)
            {
                // Broad phase includes zero-thickness triangle bounds.
                if(Bounds.Min.X>box.Max.X||Bounds.Max.X<box.Min.X||Bounds.Min.Y>box.Max.Y||Bounds.Max.Y<box.Min.Y||Bounds.Min.Z>box.Max.Z||Bounds.Max.Z<box.Min.Z)return false;
                var center=new Point((box.Min.X+box.Max.X)*.5f,(box.Min.Y+box.Max.Y)*.5f,(box.Min.Z+box.Max.Z)*.5f);
                var half=new Point((box.Max.X-box.Min.X)*.5f,(box.Max.Y-box.Min.Y)*.5f,(box.Max.Z-box.Min.Z)*.5f);
                Point a=Sub(A,center),b=Sub(B,center),c=Sub(C,center),ab=Sub(b,a),bc=Sub(c,b),ca=Sub(a,c);
                if(Separated(new Point(1,0,0),a,b,c,half)||Separated(new Point(0,1,0),a,b,c,half)||Separated(new Point(0,0,1),a,b,c,half)||Separated(Cross(ab,bc),a,b,c,half))return false;
                foreach(var edge in new[]{ab,bc,ca})
                    if(Separated(new Point(0,edge.Z,-edge.Y),a,b,c,half)||Separated(new Point(-edge.Z,0,edge.X),a,b,c,half)||Separated(new Point(edge.Y,-edge.X,0),a,b,c,half))return false;
                return true;
            }
        }
        internal sealed class Geometry
        {
            internal readonly Box Envelope;
            internal readonly Exit[] Exits;
            internal readonly Triangle[] Walls;
            private readonly Dictionary<long,bool> clearEdges=new Dictionary<long,bool>();
            internal Geometry(Box envelope,Exit[] exits,Triangle[] walls)
                {Envelope=envelope;Exits=exits;Walls=walls??Array.Empty<Triangle>();}
            internal bool ClearWalls(Box sweep)
            {
                foreach(var wall in Walls)if(wall.Hits(sweep))return false;
                return true;
            }
            internal bool ClearEdge(int a,int b)
            {
                long key=(long)Math.Min(a,b)*Count+Math.Max(a,b);
                if(clearEdges.TryGetValue(key,out bool result))return result;
                var sweep=Sweep(Position(a),Position(b));
                result=Envelope.Contains(sweep)&&ClearWalls(sweep);
                clearEdges[key]=result;
                return result;
            }
        }
        internal sealed class Route
        {
            internal readonly int Start, ExitIndex;
            internal readonly int[] Nodes;
            internal readonly float Length;
            internal Route(int start, int exit, int[] nodes, float length)
                { Start=start; ExitIndex=exit; Nodes=nodes; Length=length; }
        }

        internal const int Columns=10, Rows=7, Levels=6, Count=Columns*Rows*Levels;
        internal const float Bottom=4.91f;
        // Must match BuildCourierFamily.StoreHeights. The vacant band keeps
        // the R10 curtain's real opening sweep clear of the outer stores.
        internal static readonly float[] StoreHeights={Bottom,5.44f,5.97f,6.50f,8.02f,8.55f};
        // Folded native GBM, with 10 mm clearance and 40 mm lateral allowance
        // for the slim moving side grips. No conveyor crosses below an outlet.
        internal static readonly Point Half=new Point(.44599736f*.5f+.005f,.50744426f*.5f+.01f,4.0679136f*.5f+.01f);
        internal static Point Position(int node)
        {
            int column=node%Columns, row=node/Columns%Rows, level=node/(Columns*Rows);
            return new Point((column-4.5f)*.475f,StoreHeights[level],14.3137408f-row*4.2679136f);
        }
        internal static int Nearest(Point p)
        {
            int c=(int)Math.Round(p.X/.475f+4.5f), r=(int)Math.Round((14.3137408f-p.Z)/4.2679136f),l=0;
            for(int i=1;i<StoreHeights.Length;i++)if(Math.Abs(p.Y-StoreHeights[i])<Math.Abs(p.Y-StoreHeights[l]))l=i;
            if(c<0||c>=Columns||r<0||r>=Rows||l<0||l>=Levels) return -1;
            int n=(l*Rows+r)*Columns+c;
            return Position(n).Distance(p)<=.035f?n:-1;
        }
        private static IEnumerable<int> Neighbors(int node)
        {
            int c=node%Columns,r=node/Columns%Rows,l=node/(Columns*Rows);
            if(c>0)yield return node-1; if(c+1<Columns)yield return node+1;
            if(r>0)yield return node-Columns; if(r+1<Rows)yield return node+Columns;
            if(l>0)yield return node-Columns*Rows; if(l+1<Levels)yield return node+Columns*Rows;
        }
        internal static Box Sweep(Point a,Point b) => new Box(
            new Point(Math.Min(a.X,b.X)-Half.X,Math.Min(a.Y,b.Y)-Half.Y,Math.Min(a.Z,b.Z)-Half.Z),
            new Point(Math.Max(a.X,b.X)+Half.X,Math.Max(a.Y,b.Y)+Half.Y,Math.Max(a.Z,b.Z)+Half.Z));
        internal static bool Clear(int from,int to,int moving,bool[] occupied,Box[] fixedObstacles,Geometry geometry)
        {
            if(!geometry.ClearEdge(from,to))return false;
            var sweep=Sweep(Position(from),Position(to));
            foreach(var obstacle in fixedObstacles)if(sweep.Intersects(obstacle))return false;
            for(int n=0;n<Count;n++)
                if(n!=moving&&occupied[n]&&sweep.Intersects(Box.Around(Position(n),Half)))return false;
            return true;
        }
        private static bool CanDrop(int node,int exit,bool[] occupied,Box[] obstacles,Geometry geometry)
        {
            Point p=Position(node);
            if(!geometry.Exits[exit].Fits(p))return false;
            var sweep=Sweep(p,new Point(p.X,p.Y-geometry.Exits[exit].Drop,p.Z));
            if(!geometry.ClearWalls(sweep))return false;
            foreach(var obstacle in obstacles)if(sweep.Intersects(obstacle))return false;
            for(int n=0;n<Count;n++)if(n!=node&&occupied[n]&&sweep.Intersects(Box.Around(Position(n),Half)))return false;
            return true;
        }
        internal static bool TryPlan(bool[] occupied,Box[] obstacles,Geometry geometry,out Route route,bool[] selectable=null)
        {
            route=null;
            if(occupied==null||occupied.Length!=Count||geometry?.Exits==null||geometry.Exits.Length==0||
                (selectable!=null&&selectable.Length!=Count))return false;
            obstacles=obstacles??Array.Empty<Box>();
            var parent=new int[Count];var exitFor=new int[Count];var distance=new float[Count];
            for(int n=0;n<Count;n++){parent[n]=-2;exitFor[n]=-1;}
            var queue=new Queue<int>();
            double best=double.MaxValue;Route chosenRoute=null;
            Action<int,int,int,float> consider=(candidate,empty,exit,length)=>
            {
                if(selectable!=null&&!selectable[candidate])return;
                // Prefer a lower physical level before introducing an upper store.
                double rank=candidate/(Columns*Rows)*100000d+length+Math.Abs(Position(candidate).X)*.001d+candidate*.0000001d;
                if(rank>=best)return;
                var nodes=new List<int>{candidate};
                for(int p=empty;p>=0;p=parent[p])nodes.Add(p);
                chosenRoute=new Route(candidate,exit,nodes.ToArray(),length);best=rank;
            };
            for(int n=0;n<Columns*Rows;n++)for(int e=0;e<geometry.Exits.Length;e++)
            {
                if(!geometry.Envelope.Contains(Box.Around(Position(n),Half))||!CanDrop(n,e,occupied,obstacles,geometry))continue;
                if(occupied[n])consider(n,-1,e,0);
                else if(parent[n]==-2){parent[n]=-1;exitFor[n]=e;queue.Enqueue(n);}
            }
            while(queue.Count>0)
            {
                int empty=queue.Dequeue();
                foreach(int next in Neighbors(empty))
                {
                    if(parent[next]!=-2||!Clear(next,empty,next,occupied,obstacles,geometry))continue;
                    float length=distance[empty]+Position(next).Distance(Position(empty));
                    if(occupied[next])consider(next,empty,exitFor[empty],length);
                    else{parent[next]=empty;exitFor[next]=exitFor[empty];distance[next]=length;queue.Enqueue(next);}
                }
            }
            route=chosenRoute;
            return route!=null;
        }
    }

    // Authored continuous-mouth profile only. The legacy two-cell asset keeps
    // its audited motion speeds. These are physical travel speeds, not virtual
    // inventory cadence: the next store waits for the native rail to clear.
    internal static class BomberFeedTiming
    {
        internal const float LegacyConveyorSpeed=1.5f,ContinuousConveyorSpeed=3f;
        internal const float ContinuousRailSpeed=4f,GatePhase=.995f,GateHoldSeconds=3f;
        internal static float ConveyorSpeed(bool continuous)=>continuous?ContinuousConveyorSpeed:LegacyConveyorSpeed;
    }

#if !FEED_PLANNER_ONLY
    public sealed class SuperheavyBomberFeed : MonoBehaviour
    {
        private const string StorePrefix="BG280_Store_L";
        private static readonly AccessTools.FieldRef<WeaponStation,int> WeaponIndex=AccessTools.FieldRefAccess<WeaponStation,int>("weaponIndex");
        private static readonly AccessTools.FieldRef<MountedMissile,BayDoor[]> RailDoors=AccessTools.FieldRefAccess<MountedMissile,BayDoor[]>("bayDoors");
        private static readonly AccessTools.FieldRef<MountedMissile,float> RailLength=AccessTools.FieldRefAccess<MountedMissile,float>("railLength");
        private static readonly AccessTools.FieldRef<MountedMissile,float> RailSpeed=AccessTools.FieldRefAccess<MountedMissile,float>("railSpeed");
        private static readonly System.Reflection.FieldInfo RailDirection=AccessTools.Field(typeof(MountedMissile),"railDirection");
        private static readonly AccessTools.FieldRef<BayDoor,float> DoorPhase=AccessTools.FieldRefAccess<BayDoor,float>("openAmount");
        private static ManualLogSource log;
        private Aircraft owner;
        private WeaponStation station;
        private MountedMissile moving;
        private Unit target;
        private GlobalPosition aimpoint;
        private BomberFeedPlan.Route route;
        private BomberFeedPlan.Box envelope;
        private BomberFeedPlan.Geometry geometry;
        private BomberFeedPlan.Exit[] exits;
        private BayDoor[] gates;
        private BayDoor[][] outletGates;
        private MeshFilter[] foldingDoorMeshes=Array.Empty<MeshFilter>();
        private readonly Dictionary<MountedMissile,Vector3> meshOffsets=new Dictionary<MountedMissile,Vector3>();
        private int waypoint;
        private bool nativeLaunch, handedOff,continuous;
        private float deadline,acceptedAt,handedOffAt;
        private string lastBlock;
        private GameObject carriage;
        internal float ConveyorMetersPerSecond=>BomberFeedTiming.ConveyorSpeed(continuous);
        internal bool Busy=>moving!=null;
        // GroundBombBay must not add a second door-opening delay to a round
        // whose real feed controller already waits for the observed gate pose.
        internal static bool ManagesRail(Aircraft aircraft,MountedMissile rail)=>IsBomber(aircraft)&&IsStore(rail);

        internal static void Install(Harmony harmony,ManualLogSource logger)
        {
            log=logger;
            harmony.CreateClassProcessor(typeof(LaunchPatch)).Patch();
            harmony.CreateClassProcessor(typeof(FireGuard)).Patch();
        }
        private static bool IsStore(MountedMissile rail)=>rail!=null&&rail.name.StartsWith(StorePrefix,StringComparison.Ordinal);
        private static bool IsBomber(Aircraft aircraft)=>aircraft!=null&&aircraft.definition?.jsonKey=="jp_bg280_bomber";

        [HarmonyPatch(typeof(WeaponStation),nameof(WeaponStation.LaunchMount))]
        private static class LaunchPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(WeaponStation __instance,Unit __0,Unit __1,GlobalPosition __2)
            {
                if(!(__0 is Aircraft aircraft)||!IsBomber(aircraft)||!__instance.Weapons.OfType<MountedMissile>().Any(IsStore))return true;
                var feed=aircraft.GetComponent<SuperheavyBomberFeed>()??aircraft.gameObject.AddComponent<SuperheavyBomberFeed>();
                if(feed.nativeLaunch&&feed.station==__instance)return true;
                feed.Request(aircraft,__instance,__1,__2);
                return false;
            }
        }
        [HarmonyPatch(typeof(MountedMissile),nameof(MountedMissile.Fire),new[]{typeof(Unit),typeof(Unit),typeof(Vector3),typeof(WeaponStation),typeof(GlobalPosition)})]
        private static class FireGuard
        {
            private static bool Prefix(MountedMissile __instance,Unit __0)
            {
                if(!IsStore(__instance)||!(__0 is Aircraft aircraft)||!IsBomber(aircraft))return true;
                var feed=aircraft.GetComponent<SuperheavyBomberFeed>();
                return feed!=null&&feed.nativeLaunch&&feed.moving==__instance;
            }
        }
        private void Block(string message)
        {
            if(lastBlock!=message)
            {
                lastBlock=message;log.LogWarning("BG-280 feeder: "+message);
                if(owner!=null&&GameManager.IsLocalAircraft(owner)&&SceneSingleton<AircraftActionsReport>.i!=null)
                    SceneSingleton<AircraftActionsReport>.i.ReportText("Bomb conveyor held: "+message,3f);
            }
        }
        private void PositionCarriage()
        {
            if(owner!=null&&owner.transform.Find("SHGT_BayCurtain_R9")!=null){if(carriage!=null)carriage.SetActive(false);return;}
            if(carriage==null)
            {
                carriage=new GameObject("SHGT_BomberFeed_MovingSideGrips");carriage.transform.SetParent(owner.transform,false);
                var material=owner.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r=>r.name=="SHGT_BombBayLiner")?.sharedMaterial;
                foreach(float side in new[]{-1f,1f})
                {
                    var grip=GameObject.CreatePrimitive(PrimitiveType.Cube);grip.name="Electromagnetic side grip";
                    grip.transform.SetParent(carriage.transform,false);grip.transform.localPosition=new Vector3(side*.244f,-.12f,0);
                    grip.transform.localScale=new Vector3(.025f,.08f,2.8f);grip.layer=owner.gameObject.layer;
                    var collider=grip.GetComponent<Collider>();collider.enabled=false;Destroy(collider);
                    if(material!=null)grip.GetComponent<MeshRenderer>().sharedMaterial=material;
                }
            }
            carriage.SetActive(true);carriage.transform.localPosition=Center(moving);carriage.transform.localRotation=Quaternion.identity;
        }
        private bool ReadContract(Aircraft aircraft)
        {
            if(geometry!=null)return true;
            var bounds=aircraft.transform.Find("SHGT_BomberFeedEnvelope");
            if(bounds==null){Block("validated compartment envelope marker is missing; release held.");return false;}
            var p=aircraft.transform.InverseTransformPoint(bounds.position);
            var size=bounds.localScale;
            if(size.x<=0||size.y<=0||size.z<=0)return false;
            envelope=BomberFeedPlan.Box.Around(P(p),P(size*.5f));
            var nativeGates=aircraft.GetComponentsInChildren<BayDoor>(true);
            var mouth=aircraft.transform.Find("SHGT_BomberFeedExit_Continuous");
            continuous=mouth!=null;
            exits=new BomberFeedPlan.Exit[continuous?1:2];
            outletGates=new BayDoor[exits.Length][];
            gates=new BayDoor[2];
            for(int i=0;i<2;i++)
            {
                gates[i]=nativeGates.FirstOrDefault(g=>g.name=="SHGT_BombBayGate_Cell"+(aircraft.transform.Find("SHGT_DirectDropBay")!=null?1:i+1));
                if(gates[i]==null){Block("native bay gate is missing; release held.");return false;}
            }
            for(int i=0;i<exits.Length;i++)
            {
                var marker=continuous?mouth:aircraft.transform.Find("SHGT_BomberFeedExit_Cell"+(i+1));
                if(marker==null||!ValidExit(marker,aircraft)){Block("outlet marker is missing or invalid; release held.");return false;}
                exits[i]=new BomberFeedPlan.Exit(P(aircraft.transform.InverseTransformPoint(marker.position)),marker.localScale.x,marker.localScale.z,2.65f);
                // The continuous mouth spans both authored folding-door cells.
                // Both leaves must be clear, even for a store near one end.
                outletGates[i]=continuous?gates:new[]{gates[i]};
            }
            foldingDoorMeshes=Enumerable.Range(1,2).Select(i=>aircraft.transform.Find("SHGT_BombBayMotion_Cell"+i))
                .Where(t=>t!=null).SelectMany(t=>t.GetComponentsInChildren<MeshFilter>(true)).Where(f=>f.sharedMesh!=null).ToArray();
            if(continuous&&foldingDoorMeshes.Length==0){Block("continuous folding-door geometry is missing; release held.");return false;}
            var walls=new List<BomberFeedPlan.Triangle>();
            foreach(var filter in aircraft.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.name=="SHGT_BombBayLiner"))
            {
                if(filter.sharedMesh==null||!filter.sharedMesh.isReadable){Block("compartment collision mesh is unreadable; release held.");return false;}
                var vertices=filter.sharedMesh.vertices;var triangles=filter.sharedMesh.triangles;
                for(int i=0;i<triangles.Length;i+=3)
                    walls.Add(new BomberFeedPlan.Triangle(P(aircraft.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[triangles[i]]))),
                        P(aircraft.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[triangles[i+1]]))),
                        P(aircraft.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[triangles[i+2]])))));
            }
            if(walls.Count==0){Block("compartment wall triangles are missing; release held.");return false;}
            geometry=new BomberFeedPlan.Geometry(envelope,exits,walls.ToArray());
            return true;
        }
        private static bool ValidExit(Transform marker,Aircraft aircraft)
        {
            var size=marker.localScale;var p=aircraft.transform.InverseTransformPoint(marker.position);
            return !(float.IsNaN(size.x)||float.IsNaN(size.z)||float.IsInfinity(size.x)||float.IsInfinity(size.z)||
                float.IsNaN(p.x)||float.IsNaN(p.y)||float.IsNaN(p.z)||float.IsInfinity(p.x)||float.IsInfinity(p.y)||float.IsInfinity(p.z))&&
                size.x>BomberFeedPlan.Half.X*2&&size.z>BomberFeedPlan.Half.Z*2&&Mathf.Abs(p.y-BomberFeedPlan.Bottom)<.025f&&
                Quaternion.Angle(Quaternion.identity,Quaternion.Inverse(aircraft.transform.rotation)*marker.rotation)<.1f;
        }
        private bool GatesClear=>outletGates[route.ExitIndex].All(g=>g!=null&&DoorPhase(g)>=(continuous?BomberFeedTiming.GatePhase:.98f));
        private void HoldRouteGates()
        {
            foreach(var gate in outletGates[route.ExitIndex])if(gate!=null)gate.OpenDoor(BomberFeedTiming.GateHoldSeconds);
        }
        private Vector3 Center(MountedMissile rail)=>owner.transform.InverseTransformPoint(rail.transform.TransformPoint(meshOffsets[rail]));
        private bool Capture(MountedMissile rail)
        {
            if(meshOffsets.ContainsKey(rail))return true;
            if(RailDirection==null||Convert.ToInt32(RailDirection.GetValue(rail))!=1||RailLength(rail)<2.65f-.001f||RailSpeed(rail)<=0)
            {Block("native ejection rail does not clear the audited outlet; release held.");return false;}
            bool first=true;Bounds bounds=default;
            foreach(var filter in rail.GetComponentsInChildren<MeshFilter>(true))
            {
                if(filter.sharedMesh==null)continue;
                var b=filter.sharedMesh.bounds;
                for(int corner=0;corner<8;corner++)
                {
                    var v=b.center+Vector3.Scale(b.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                    v=owner.transform.InverseTransformPoint(filter.transform.TransformPoint(v));
                    if(first){bounds=new Bounds(v,Vector3.zero);first=false;}else bounds.Encapsulate(v);
                }
            }
            var h=BomberFeedPlan.Half;
            if(first||bounds.extents.x>h.X||bounds.extents.y>h.Y||bounds.extents.z>h.Z)
            {Block("a store exceeds the audited folded GBM envelope; release held.");return false;}
            meshOffsets[rail]=rail.transform.InverseTransformPoint(owner.transform.TransformPoint(bounds.center));
            return true;
        }
        private BomberFeedPlan.Box[] Obstacles()
        {
            var result=new List<BomberFeedPlan.Box>();
            foreach(var t in owner.GetComponentsInChildren<Transform>(true))
            {
                if(!t.gameObject.activeInHierarchy||!t.name.StartsWith("SHGT_BomberFeedBlocker_",StringComparison.Ordinal))continue;
                bool first=true;Bounds box=default;
                for(int c=0;c<8;c++)
                {
                    // Marker scale encodes the full obstacle size; TransformPoint
                    // already applies that scale, so corners are unit half extents.
                    var p=owner.transform.InverseTransformPoint(t.TransformPoint(new Vector3((c&1)==0?-.5f:.5f,(c&2)==0?-.5f:.5f,(c&4)==0?-.5f:.5f)));
                    if(first){box=new Bounds(p,Vector3.zero);first=false;}else box.Encapsulate(p);
                }
                result.Add(new BomberFeedPlan.Box(P(box.min),P(box.max)));
            }
            foreach(var gun in owner.GetComponentsInChildren<Gun>(false).Where(g=>g.info!=null&&g.info.name.StartsWith("BG280G_",StringComparison.Ordinal)))
            {
                var turret=gun.GetComponentInParent<Turret>();
                if(turret==null)continue;
                foreach(var filter in turret.GetComponentsInChildren<MeshFilter>(false))
                {
                    if(filter.sharedMesh==null)continue;
                    bool first=true;Bounds box=default;var b=filter.sharedMesh.bounds;
                    for(int c=0;c<8;c++)
                    {
                        var p=b.center+Vector3.Scale(b.extents,new Vector3((c&1)==0?-1:1,(c&2)==0?-1:1,(c&4)==0?-1:1));
                        p=owner.transform.InverseTransformPoint(filter.transform.TransformPoint(p));
                        if(first){box=new Bounds(p,Vector3.zero);first=false;}else box.Encapsulate(p);
                    }
                    var measured=new BomberFeedPlan.Box(P(box.min),P(box.max));
                    if(measured.Intersects(envelope))result.Add(measured);
                }
            }
            return result.ToArray();
        }
        private bool Snapshot(out bool[] occupied,out Dictionary<int,MountedMissile> rails)
        {
            occupied=new bool[BomberFeedPlan.Count];rails=new Dictionary<int,MountedMissile>();
            foreach(var rail in owner.GetComponentsInChildren<MountedMissile>(false).Where(IsStore))
            {
                if(!Capture(rail))return false;
                if(rail==moving)continue;
                int node=BomberFeedPlan.Nearest(P(Center(rail)));
                if(node<0||occupied[node]){Block("store centers do not match the audited physical grid; release held.");return false;}
                occupied[node]=true;rails[node]=rail;
            }
            return true;
        }
        private void Request(Aircraft aircraft,WeaponStation requested,Unit selected,GlobalPosition point)
        {
            if(Busy)return; // Repeated trigger calls do not enqueue more drops.
            owner=aircraft;
            if(!requested.Ready()){Block("native weapon station is not ready.");return;}
            if(requested.SafetyIsOn(aircraft)){Block("native weapon safety is on.");return;}
            if(!ReadContract(aircraft)||!Snapshot(out var occupied,out var rails))return;
            // Guns/other selected weapon types cannot be borrowed as virtual GBMs.
            var eligible=new HashSet<MountedMissile>(requested.Weapons.OfType<MountedMissile>().Where(r=>IsStore(r)&&r.GetAmmoLoaded()>0));
            var selectable=new bool[BomberFeedPlan.Count];
            foreach(var pair in rails)selectable[pair.Key]=eligible.Contains(pair.Value);
            if(!BomberFeedPlan.TryPlan(occupied,Obstacles(),geometry,out var planned,selectable)||!rails.TryGetValue(planned.Start,out var chosen))
            {Block("no unobstructed physical path to an outlet for this station.");return;}
            station=requested;target=selected;aimpoint=point;route=planned;moving=chosen;waypoint=1;handedOff=false;
            acceptedAt=Time.time;deadline=acceptedAt+planned.Length/ConveyorMetersPerSecond+20f;lastBlock=null;
            PositionCarriage();
            HoldRouteGates();
            log.LogInfo($"BG-280 feeder accepted {moving.name}; path={planned.Length:F2}m nodes={planned.Nodes.Length} outlet={route.ExitIndex+1} continuous={continuous} conveyor={ConveyorMetersPerSecond:F1}m/s; ammunition remains mounted.");
        }
        private bool SegmentClear(Vector3 from,Vector3 to)
        {
            var swept=BomberFeedPlan.Sweep(P(from),P(to));
            if(!envelope.Contains(swept)||!geometry.ClearWalls(swept))return false;
            if(!ClearDoorMeshes(swept))return false;
            foreach(var obstacle in Obstacles())if(swept.Intersects(obstacle))return false;
            foreach(var rail in owner.GetComponentsInChildren<MountedMissile>(false).Where(IsStore))
            {
                if(rail==moving)continue;
                if(!Capture(rail)||swept.Intersects(BomberFeedPlan.Box.Around(P(Center(rail)),BomberFeedPlan.Half)))return false;
            }
            return true;
        }
        private bool ClearDoorMeshes(BomberFeedPlan.Box sweep)
        {
            foreach(var filter in foldingDoorMeshes)
            {
                if(filter==null||!filter.gameObject.activeInHierarchy||filter.sharedMesh==null)continue;
                var b=filter.sharedMesh.bounds;bool first=true;Bounds box=default;
                for(int c=0;c<8;c++)
                {
                    var p=b.center+Vector3.Scale(b.extents,new Vector3((c&1)==0?-1:1,(c&2)==0?-1:1,(c&4)==0?-1:1));
                    p=owner.transform.InverseTransformPoint(filter.transform.TransformPoint(p));
                    if(first){box=new Bounds(p,Vector3.zero);first=false;}else box.Encapsulate(p);
                }
                if(sweep.Intersects(new BomberFeedPlan.Box(P(box.min),P(box.max))))return false;
            }
            return true;
        }
        private bool DropClear()
        {
            var from=Center(moving);var to=from+Vector3.down*exits[route.ExitIndex].Drop;
            var sweep=BomberFeedPlan.Sweep(P(from),P(to));
            if(!geometry.ClearWalls(sweep)||!ClearDoorMeshes(sweep))return false;
            foreach(var obstacle in Obstacles())if(sweep.Intersects(obstacle))return false;
            foreach(var rail in owner.GetComponentsInChildren<MountedMissile>(false).Where(IsStore))
                if(rail!=moving&&(!Capture(rail)||sweep.Intersects(BomberFeedPlan.Box.Around(P(Center(rail)),BomberFeedPlan.Half))))return false;
            return true;
        }
        private void FixedUpdate()
        {
            if(moving==null){if(carriage!=null)carriage.SetActive(false);return;}
            if(owner==null||owner.disabled){if(carriage!=null)carriage.SetActive(false);moving=null;return;}
            // BayDoor.OpenDoor refreshes a timer; it never resets openAmount.
            // Hold throughout native RailLaunch as well as conveyor movement.
            HoldRouteGates();
            if(handedOff)
            {
                // Native RailLaunch still owns the visible downward travel and
                // projectile birth. Do not feed another store through it.
                if(!moving.gameObject.activeInHierarchy){log.LogInfo($"BG-280 feeder native release complete; commandSeconds={Time.time-acceptedAt:F3} railSeconds={Time.time-handedOffAt:F3}.");moving=null;}
                return;
            }
            if(Time.time>deadline){Block("movement timed out; store retained at its physical position. Rearm/reconfigure before another release.");return;}
            if(waypoint<route.Nodes.Length)
            {
                var from=Center(moving);var to=V(BomberFeedPlan.Position(route.Nodes[waypoint]));
                if(!SegmentClear(from,to)){Block("path became obstructed; conveyor stopped without launching.");return;}
                var next=Vector3.MoveTowards(from,to,ConveyorMetersPerSecond*Time.fixedDeltaTime);
                moving.transform.position+=owner.transform.TransformVector(next-from);
                PositionCarriage();
                if((next-to).sqrMagnitude<.000001f)waypoint++;
                return;
            }
            if(!GatesClear)return;
            if(!DropClear()){Block("open folding doors, a retained store or installed gun obstructs the native downward rail.");return;}
            if(!station.Ready()||station.SafetyIsOn(owner)){Block("native weapon readiness/safety is on; staged store retained.");return;}
            int index=station.Weapons.IndexOf(moving);
            if(index<0||!moving.IsAttached()||moving.GetAmmoLoaded()!=1){Block("selected physical rail is no longer attached.");moving=null;return;}
            RailDoors(moving)=outletGates[route.ExitIndex];
            if(continuous)RailSpeed(moving)=BomberFeedTiming.ContinuousRailSpeed;
            WeaponIndex(station)=index;
            if(carriage!=null)carriage.SetActive(false);
            nativeLaunch=true;
            try{station.LaunchMount(owner,target,aimpoint);}
            finally{nativeLaunch=false;}
            if(moving.GetAmmoLoaded()!=0){Block("native launch did not accept the staged store; no extra ammunition was created.");moving=null;return;}
            handedOff=true;handedOffAt=Time.time;
            log.LogInfo($"BG-280 feeder handed off {moving.name}; one native rail consumed at outlet {route.ExitIndex+1}.");
        }
        private static BomberFeedPlan.Point P(Vector3 v)=>new BomberFeedPlan.Point(v.x,v.y,v.z);
        private static Vector3 V(BomberFeedPlan.Point v)=>new Vector3(v.X,v.Y,v.Z);
    }
#endif
}
