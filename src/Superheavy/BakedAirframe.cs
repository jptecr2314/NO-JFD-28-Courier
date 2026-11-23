using System;
using UnityEngine;

namespace Japensou.NuclearOption.AirframeData
{
    // Optional Courier-only presentation assets. These native Unity arrays
    // never define physics, HP, fuel quantities, loadouts or joint ownership.
    public sealed class CourierPresentationData:MonoBehaviour
    {
        public MeshFilter[] Filters;
        public Mesh[] Detailed,Balanced,Performance;
        public Mesh[] PlanningSources;
        public Mesh[] BalancedPaint1,BalancedPaint2,PerformancePaint1,PerformancePaint2;
        public Mesh[] DetailedDamage1,DetailedDamage2,BalancedDamage1,BalancedDamage2,PerformanceDamage1,PerformanceDamage2;
        public string[] Owners;
        public bool[] Interior,BakedDamage;
        public int[] ShadowModes;
        // Authored ownership for animated assemblies; clips live on the
        // supporting section rather than the aircraft's central transform.
        public Component[] SectionAnimations,GearContacts,GearSupports;
        public string[] SupportedPartIds,SupportOwnerIds;
    }
    // Serialized authoring data only. The assembly has no game or BepInEx dependency.
    [Serializable] public sealed class BakedPart
    {
        public Component Part; public string Id,ParentId;
        public Bounds CollisionBounds; public bool HasCollisionBounds;
    }
    [Serializable] public sealed class BakedSurface
    {
        public MeshFilter Filter; public string PartId,SourceOriginal;
        public bool Fresh,FixedReceiver,Painted; public Mesh Original,Paint1,Paint2;
    }
    [Serializable] public sealed class BakedMotion
    {
        public Component Part,Gate; public Transform Proxy,Reference;
        public Matrix4x4 RestProxy,RestBody; public Vector3 Travel; public string Kind;
    }
    [DefaultExecutionOrder(-20000)] public sealed class BakedAirframe:MonoBehaviour
    {
        public const int CurrentSchema=2;
        public int Schema=CurrentSchema;
        public string SourceKey,SourceSha256;
        public TextAsset Manifest,Profiles;
        public Material InternalPaint,ReceiverMetal;
        public BakedPart[] Parts; public BakedSurface[] Surfaces; public BakedMotion[] Motions;
        // Runtime-loaded mod assemblies do not reliably deserialize inline
        // custom-class arrays. Store their bindings in native Unity arrays.
        // The graph/meshes themselves remain fully authored prefab objects.
        public Component[] PartObjects,MotionParts,MotionGates;
        public Component[] ContactObjects;
        public string[] PartIds,PartParents,SurfaceParts,SurfaceSources,MotionKinds;
        public Bounds[] PartBounds;public bool[] PartHasBounds;
        public MeshFilter[] SurfaceFilters;
        public Mesh[] SurfaceOriginals,SurfacePaint1,SurfacePaint2;
        public bool[] SurfaceFresh,SurfaceFixed,SurfacePainted;
        public Transform[] MotionProxies,MotionReferences;
        public Matrix4x4[] MotionRestProxies,MotionRestBodies;
        public Vector3[] MotionTravel;
        // Optional paint-only bindings. Retained native animated/ejection
        // objects must not join the structural visibility/damage surface list.
        public MeshFilter[] DetailPaintFilters;
        public Mesh[] DetailPaintOriginals,DetailPaint1,DetailPaint2;
        public int[] DetailPaintSlotMasks;
        public float DryMass;
        public static Action<BakedAirframe> OnInstanceAwake;
        public void EncodeBindings()
        {
            if(Parts==null||Surfaces==null||Motions==null)throw new InvalidOperationException("Authoring bindings missing.");
            int n=Parts.Length;PartObjects=new Component[n];PartIds=new string[n];PartParents=new string[n];PartBounds=new Bounds[n];PartHasBounds=new bool[n];
            for(int i=0;i<n;i++){var p=Parts[i];PartObjects[i]=p.Part;PartIds[i]=p.Id;PartParents[i]=p.ParentId;PartBounds[i]=p.CollisionBounds;PartHasBounds[i]=p.HasCollisionBounds;}
            n=Surfaces.Length;SurfaceFilters=new MeshFilter[n];SurfaceParts=new string[n];SurfaceSources=new string[n];SurfaceOriginals=new Mesh[n];SurfacePaint1=new Mesh[n];SurfacePaint2=new Mesh[n];SurfaceFresh=new bool[n];SurfaceFixed=new bool[n];SurfacePainted=new bool[n];
            for(int i=0;i<n;i++){var s=Surfaces[i];SurfaceFilters[i]=s.Filter;SurfaceParts[i]=s.PartId;SurfaceSources[i]=s.SourceOriginal;SurfaceOriginals[i]=s.Original;SurfacePaint1[i]=s.Paint1;SurfacePaint2[i]=s.Paint2;SurfaceFresh[i]=s.Fresh;SurfaceFixed[i]=s.FixedReceiver;SurfacePainted[i]=s.Painted;}
            n=Motions.Length;MotionParts=new Component[n];MotionGates=new Component[n];MotionKinds=new string[n];MotionProxies=new Transform[n];MotionReferences=new Transform[n];MotionRestProxies=new Matrix4x4[n];MotionRestBodies=new Matrix4x4[n];MotionTravel=new Vector3[n];
            for(int i=0;i<n;i++){var m=Motions[i];MotionParts[i]=m.Part;MotionGates[i]=m.Gate;MotionKinds[i]=m.Kind;MotionProxies[i]=m.Proxy;MotionReferences[i]=m.Reference;MotionRestProxies[i]=m.RestProxy;MotionRestBodies[i]=m.RestBody;MotionTravel[i]=m.Travel;}
            Schema=CurrentSchema;
        }
        public void RestoreBindings()
        {
            if(Schema!=CurrentSchema||PartObjects==null||SurfaceFilters==null||MotionParts==null)throw new InvalidOperationException("Baked Unity binding arrays missing; package schema mismatch.");
            int n=PartObjects.Length;
            if(PartIds==null||PartParents==null||PartBounds==null||PartHasBounds==null||PartIds.Length!=n||PartParents.Length!=n||PartBounds.Length!=n||PartHasBounds.Length!=n)throw new InvalidOperationException("Baked part binding arrays differ.");
            Parts=new BakedPart[n];for(int i=0;i<n;i++)Parts[i]=new BakedPart{Part=PartObjects[i],Id=PartIds[i],ParentId=PartParents[i],CollisionBounds=PartBounds[i],HasCollisionBounds=PartHasBounds[i]};
            n=SurfaceFilters.Length;
            if(SurfaceParts==null||SurfaceSources==null||SurfaceOriginals==null||SurfacePaint1==null||SurfacePaint2==null||SurfaceFresh==null||SurfaceFixed==null||SurfacePainted==null||SurfaceParts.Length!=n||SurfaceSources.Length!=n||SurfaceOriginals.Length!=n||SurfacePaint1.Length!=n||SurfacePaint2.Length!=n||SurfaceFresh.Length!=n||SurfaceFixed.Length!=n||SurfacePainted.Length!=n)throw new InvalidOperationException("Baked surface binding arrays differ.");
            Surfaces=new BakedSurface[n];for(int i=0;i<n;i++)Surfaces[i]=new BakedSurface{Filter=SurfaceFilters[i],PartId=SurfaceParts[i],SourceOriginal=SurfaceSources[i],Original=SurfaceOriginals[i],Paint1=SurfacePaint1[i],Paint2=SurfacePaint2[i],Fresh=SurfaceFresh[i],FixedReceiver=SurfaceFixed[i],Painted=SurfacePainted[i]};
            n=MotionParts.Length;
            if(MotionGates==null||MotionKinds==null||MotionProxies==null||MotionReferences==null||MotionRestProxies==null||MotionRestBodies==null||MotionTravel==null||MotionGates.Length!=n||MotionKinds.Length!=n||MotionProxies.Length!=n||MotionReferences.Length!=n||MotionRestProxies.Length!=n||MotionRestBodies.Length!=n||MotionTravel.Length!=n)throw new InvalidOperationException("Baked motion binding arrays differ.");
            Motions=new BakedMotion[n];for(int i=0;i<n;i++)Motions[i]=new BakedMotion{Part=MotionParts[i],Gate=MotionGates[i],Kind=MotionKinds[i],Proxy=MotionProxies[i],Reference=MotionReferences[i],RestProxy=MotionRestProxies[i],RestBody=MotionRestBodies[i],Travel=MotionTravel[i]};
        }
        private void Awake(){OnInstanceAwake?.Invoke(this);}
    }
}
