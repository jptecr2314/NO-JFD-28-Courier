using System;
using System.IO;
using Japensou.NuclearOption.AirframeData;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace Japensou.NuclearOption.CurrentDamage
{
    internal static class NativePartitionIntegration
    {
        [Serializable] internal sealed class Part {public string id,parent,motionKind,driver;public float[] center,size,travel;public float weight;}
        [Serializable] internal sealed class Surface {public string part,original,material,motion;public bool fresh,painted;[JsonProperty("fixed")]public bool Fixed;}
        [Serializable] internal sealed class Contact {public string part;}
        [Serializable] internal sealed class Manifest {public int version,renderTriangles;public string label,key,source,sourceSha256,binary,binarySha256;public Part[] parts;public Surface[] meshes;public Contact[] contacts;public int payloadModulesExported,legacyFramesExported;}
        internal sealed class Asset {internal Manifest M;internal FreshProfileSet Profiles;}
        internal static readonly Dictionary<string,Asset> Assets=new Dictionary<string,Asset>();
        internal static bool IntegrationReady;
        internal static void InstallSpawnGuard(Harmony guard)=>guard.CreateClassProcessor(typeof(Spawn)).Patch();
        internal static Vector3 V(float[] v)=>new Vector3(v[0],v[1],v[2]);
        internal static readonly BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static bool OwnKey(string key)=>key=="jp_superheavy_ground_test"
#if !COURIER_ONLY
            ||key=="jp_bg280_bomber"
#endif
            ;
        private static bool OwnFamily(UnitPart p)=>p.parentUnit is Aircraft a&&a.definition!=null&&OwnKey(a.definition.jsonKey);
        private static readonly Dictionary<(Type,string),FieldInfo> FieldCache=new Dictionary<(Type,string),FieldInfo>();
        private static FieldInfo Field(object o,string f){var key=(o.GetType(),f);if(!FieldCache.TryGetValue(key,out var field))FieldCache.Add(key,field=AccessTools.Field(key.Item1,f)??throw new MissingFieldException(key.Item1.FullName,f));return field;}
        internal static object Get(object o,string f)=>Field(o,f).GetValue(o);
        internal static void Set(object o,string f,object v)=>Field(o,f).SetValue(o,v);
        internal static void Initialize(Harmony harmony)
        {
            BakedAirframe.OnInstanceAwake=BindBakedInstance;
            harmony.CreateClassProcessor(typeof(Complex)).Patch();harmony.CreateClassProcessor(typeof(Repair)).Patch();harmony.CreateClassProcessor(typeof(Contacts)).Patch();harmony.CreateClassProcessor(typeof(MovingAttachment)).Patch();
            harmony.CreateClassProcessor(typeof(PreviewContacts)).Patch();
            harmony.CreateClassProcessor(typeof(CanopyContacts)).Patch();
            harmony.CreateClassProcessor(typeof(EscapeActorContacts)).Patch();
            harmony.CreateClassProcessor(typeof(TailEffectiveness)).Patch();harmony.CreateClassProcessor(typeof(TailLoads)).Patch();
            harmony.CreateClassProcessor(typeof(EditorPhysicsContext)).Patch();
            harmony.CreateClassProcessor(typeof(EditorLoadoutCapacity)).Patch();
            harmony.CreateClassProcessor(typeof(OwnedCollisionBounds)).Patch();harmony.CreateClassProcessor(typeof(OwnedJobBounds)).Patch();harmony.CreateClassProcessor(typeof(OwnedJobDiagnostics)).Patch();
            CurrentAirframeCompatibility.Install(harmony);
        }
        [HarmonyPatch(typeof(Spawner),nameof(Spawner.SpawnAircraft))]
        private static class Spawn
        {
            // BOTE replaces the whole original method in a prefix. Prepare
            // the argument before that prefix runs instead of instrumenting
            // only the original method's Instantiate instruction.
            [HarmonyPriority(Priority.First+400)]
            [HarmonyBefore("com.minec.bote")]
            private static void Prefix(ref GameObject __1){__1=PrepareSpawnPrefab(__1);}
            private static void Postfix(Aircraft __result,bool __runOriginal)
            {
                if(!__result)return;
                var graph=__result.GetComponent<NativePartitionController>();
                if(!graph)
                {
                    var key=__result.definition?.jsonKey;
                    if((GameManager.gameState==GameState.SinglePlayer||GameManager.gameState==GameState.Editor)&&OwnKey(key))throw new InvalidOperationException("Owned aircraft spawned without its new native graph. Spawn-path compatibility failed.");
                    return;
                }
                graph.SpawnPath=__runOriginal?"native":"replacement";
                int count=__result.partLookup.OfType<AeroPart>().Count();
                if(count!=Assets[graph.SourceKey].M.parts.Length)throw new InvalidOperationException("Owned spawn has an unexpected native part count: "+count);
                CurrentAirframeDamagePlugin.Log.LogInfo("NATIVE_PARTITION_SPAWN "+graph.SourceKey+" context="+GameManager.gameState+" parts="+count+" spawnPath="+graph.SpawnPath);
            }
        }
        private static void Register(BakedAirframe data)
        {
            if(!data||data.Schema!=BakedAirframe.CurrentSchema||!data.Manifest||!data.Profiles||!data.InternalPaint||!data.ReceiverMetal)throw new InvalidDataException("Baked aircraft metadata/materials missing.");
            data.RestoreBindings();
            if(Assets.TryGetValue(data.SourceKey,out var existing))
            {if(existing.M.sourceSha256!=data.SourceSha256)throw new InvalidDataException("Mixed baked aircraft sources.");return;}
            var manifest=JsonConvert.DeserializeObject<Manifest>(data.Manifest.text);
            if(manifest==null||manifest.parts==null||manifest.version!=2||manifest.key!=data.SourceKey||manifest.sourceSha256!=data.SourceSha256||manifest.parts.Length!=data.Parts.Length||manifest.payloadModulesExported!=0||manifest.legacyFramesExported!=0)throw new InvalidDataException("Baked aircraft provenance mismatch.");
            var profiles=FreshProfileSet.LoadText(data.Profiles.text,manifest);
            if(Math.Abs(data.DryMass-profiles.dryMassBudgetKg)>.2f)throw new InvalidDataException("Baked mass mismatch.");
            Assets.Add(data.SourceKey,new Asset{M=manifest,Profiles=profiles});
            CurrentAirframeDamagePlugin.Assets.Add(data.SourceKey,new CurrentAirframeDamagePlugin.PaintAsset{Painted=data.InternalPaint,Plain=data.ReceiverMetal});
        }
        private static void BindBakedInstance(BakedAirframe data)
        {
            if(!IntegrationReady||!OwnKey(data.SourceKey))return;
            Register(data);if(data.GetComponent<NativePartitionController>())return;
            // Only attach runtime behavior. All AeroParts, meshes, materials,
            // colliders, joint descriptors and system references already exist.
            foreach(var row in data.Parts)
            {
                var part=row.Part as AeroPart;if(!part)throw new InvalidDataException("Baked native part is missing: "+row.Id);
                var marker=part.gameObject.AddComponent<NativePartitionPart>();marker.Id=row.Id;marker.ParentId=row.ParentId;marker.SourceKey=data.SourceKey;marker.OwnProfileRecipe=Assets[data.SourceKey].Profiles.recipe;marker.CollisionBounds=row.CollisionBounds;marker.HasCollisionBounds=row.HasCollisionBounds;
            }
            foreach(var row in data.Surfaces)
            {
                var marker=row.Filter.gameObject.AddComponent<NativePartitionSurface>();marker.PartId=row.PartId;marker.SourceOriginal=row.SourceOriginal;marker.Fresh=row.Fresh;marker.FixedReceiver=row.FixedReceiver;marker.Painted=row.Painted;marker.Original=row.Original;marker.Paint1=row.Paint1;marker.Paint2=row.Paint2;
            }
            foreach(var row in data.Motions)
            {
                var actuator=row.Part.gameObject.AddComponent<NativePartitionActuator>();actuator.Proxy=row.Proxy;actuator.Reference=row.Reference;actuator.Gate=row.Gate as BayDoor;actuator.RestProxy=row.RestProxy;actuator.RestBody=row.RestBody;actuator.Travel=row.Travel;actuator.Kind=row.Kind;
            }
            var graph=data.gameObject.AddComponent<NativePartitionController>();graph.SourceKey=data.SourceKey;graph.SourceSha256=data.SourceSha256;graph.ExpectedDryMass=data.DryMass;
            if(graph.CourierRefit&&data.GetComponent<Japensou.NuclearOption.AirframeData.CourierPresentationData>()){graph.Presentation=data.GetComponent<CourierPresentation>()??data.gameObject.AddComponent<CourierPresentation>();graph.Presentation.Bind(graph);}
        }
        private static GameObject PrepareSpawnPrefab(GameObject original)
        {
            var definition=original.GetComponent<Aircraft>()?.definition;
            if(definition==null&&Encyclopedia.i!=null)definition=Encyclopedia.i.aircraft.FirstOrDefault(d=>d.unitPrefab==original);
            if(definition==null||!OwnKey(definition.jsonKey))return original;
            if(!CurrentAirframeDamagePlugin.Enabled.Value||!IntegrationReady)throw new InvalidOperationException("Baked aircraft runtime unavailable.");
            var data=original.GetComponent<BakedAirframe>();if(!data)throw new InvalidDataException("The old aircraft bundle is installed. This runtime requires the baked Blueprinter package.");
            // Bind behavior before native Instantiate/Awake, including BOTE's
            // replacement spawn. Do not depend on relative Awake ordering.
            Register(data);BindBakedInstance(data);return original;
        }

        [HarmonyPatch(typeof(Aircraft),nameof(Aircraft.SetComplexPhysics))]
        private static class Complex
        {
            [HarmonyPriority(Priority.First)]private static void Prefix(Aircraft __instance){if(__instance.networked)SetPreviewContacts(__instance,false);var c=__instance.GetComponent<NativePartitionController>();if(c&&!c.Ready)c.Bind(__instance);}
            [HarmonyPriority(Priority.Last)]private static void Postfix(Aircraft __instance){var c=__instance.GetComponent<NativePartitionController>();if(c&&!__instance.simplePhysics)c.AfterComplex();}
        }
        private static void SetPreviewContacts(Aircraft a,bool preview)
        {
            var data=a.GetComponent<BakedAirframe>();if(!data||data.PartObjects==null)return;
            if(data.ContactObjects!=null&&data.ContactObjects.Length>0){foreach(var contact in data.ContactObjects)if(contact is Collider collider)collider.enabled=!preview;}
            else foreach(var part in data.PartObjects)if(part)foreach(var collider in part.GetComponents<MeshCollider>())collider.enabled=!preview;
            var selection=a.transform.Find("CURRENT_EDITOR_SELECTION_CONTACT");if(selection)selection.GetComponent<BoxCollider>().enabled=preview;
        }
        [HarmonyPatch]
        private static class CanopyContacts
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Canopy),"Eject");yield return AccessTools.Method(typeof(Canopy),"EjectionSequence");}
            private static void Prefix(Canopy __instance)
            {
                var owner=Get(__instance,"attachedPart")as AeroPart;var aircraft=owner&&owner.parentUnit?owner.parentUnit as Aircraft:__instance.GetComponentInParent<Aircraft>();var graph=aircraft?aircraft.GetComponent<NativePartitionController>():null;
                var collider=Get(__instance,"ejectionCollider")as Collider;if(graph&&collider)graph.RegisterExternalDebris(collider,owner);
            }
        }
        [HarmonyPatch(typeof(PilotDismounted),nameof(PilotDismounted.SetCollidable))]
        private static class EscapeActorContacts
        {
            private static void Prefix(PilotDismounted __instance,bool __0)=>Register(__instance,__0);
            private static void Postfix(PilotDismounted __instance,bool __0)=>Register(__instance,__0);
            private static void Register(PilotDismounted pilot,bool active)
            {
                if(!active||pilot.cockpitPart?.parentUnit is not Aircraft aircraft)return;
                var graph=aircraft.GetComponent<NativePartitionController>();if(!graph)return;
                foreach(var c in pilot.GetComponentsInChildren<Collider>(true))graph.RegisterExternalDebris(c,pilot.cockpitPart as AeroPart);
            }
        }
        [HarmonyPatch(typeof(Aircraft),nameof(Aircraft.SetSimplePhysics))]
        private static class PreviewContacts
        {
            private static void Postfix(Aircraft __instance){if(!__instance.networked&&__instance.GetComponent<BakedAirframe>())SetPreviewContacts(__instance,true);__instance.GetComponent<NativePartitionController>()?.CollisionChanged();}
        }
        [HarmonyPatch(typeof(UnitPart),"Awake")]
        private static class OwnedCollisionBounds
        {private static void Postfix(UnitPart __instance){var m=__instance.GetComponent<NativePartitionPart>();if(m)m.RestoreBounds();}}
        [HarmonyPatch(typeof(AeroPart),"UpdateJobFields")]
        private static class OwnedJobBounds
        {
            private static void Postfix(AeroPart __instance,ref global::NuclearOption.Jobs.PtrAllocation<global::NuclearOption.Jobs.AeroPartFields> ___JobFields)
            {if(!OwnFamily(__instance))return;var m=__instance.GetComponent<NativePartitionPart>();if(!m||!m.HasCollisionBounds||!___JobFields.IsCreated)return;ref var fields=ref ___JobFields.Ref();fields.collisionSize=m.CollisionBounds.extents;}
        }
        [HarmonyPatch(typeof(AeroPart),"ApplyJobFields")]
        private static class OwnedJobDiagnostics
        {
            private static void Prefix(AeroPart __instance,ref global::NuclearOption.Jobs.PtrAllocation<global::NuclearOption.Jobs.AeroPartFields> ___JobFields)
            {if(!OwnFamily(__instance))return;var m=__instance.GetComponent<NativePartitionPart>();if(!m||!___JobFields.IsCreated)return;ref var fields=ref ___JobFields.Ref();m.LastAeroForce=fields.force;}
        }
        [HarmonyPatch(typeof(AeroPart),nameof(AeroPart.Repair))]
        private static class Repair
        {
            [HarmonyPriority(Priority.First)]private static bool Prefix(AeroPart __instance)
            {var c=__instance.parentUnit?__instance.parentUnit.GetComponent<NativePartitionController>():null;if(!c||!c.Ready||(__instance.parentUnit as Aircraft)?.simplePhysics==true)return true;c.RequestRepair(__instance);return false;}
        }
        [HarmonyPatch]
        private static class Contacts
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavyNativeDamage"),"Complete");
            private static bool Prefix(Aircraft __0){var c=__0?__0.GetComponent<NativePartitionController>():null;if(!c)return true;c.AfterComplex();return false;}
        }
        [HarmonyPatch]
        private static class EditorPhysicsContext
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavyGroundPhysicsPlugin"),"IsPrototype");
            private static void Postfix(Aircraft __0,ref bool __result)
            {
                // The retained flight/gear interface also gated its setup on
                // SinglePlayer. Converted editor instances need the same
                // contact filtering, suspension, CG and flight-system setup.
                if(GameManager.gameState==GameState.Editor&&__0&&__0.GetComponent<NativePartitionController>())__result=true;
            }
        }
        [HarmonyPatch]
        private static class EditorLoadoutCapacity
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavySpatialCargoRuntime").GetNestedType("SoloLoadoutCapacityPatch",BindingFlags.NonPublic),"Capacity");
            private static void Postfix(ref int __result)
            {
                if(GameManager.gameState==GameState.Editor)__result=Math.Max(__result,(int)AccessTools.Field(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavySpatialWeaponsRuntime"),"ArmsSetCount").GetValue(null));
            }
        }
        private static NativePartitionController TailGraph(UnitPart owner)
        {
            if(!owner||owner.GetComponent<NativePartitionPart>()?.Id!="Stabilizer_L")return null;
            return owner.parentUnit?owner.parentUnit.GetComponent<NativePartitionController>():null;
        }
        private static float LiveEffectiveness(AeroPart part)=>part&&!part.IsDetached()?Mathf.Clamp01((float)Get(part,"wingEffectiveness")):0;
        [HarmonyPatch]
        private static class TailEffectiveness
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavyFlightDynamics"),"SurfaceEffectiveness");
            private static bool Prefix(UnitPart __0,UnitPart __1,ref float __result)
            {
                var graph=TailGraph(__1);if(!graph||!graph.Ready)return true;
                __result=Mathf.Min(__0 is AeroPart root?LiveEffectiveness(root):1f,(LiveEffectiveness(graph.Parts["Stabilizer_L"])+LiveEffectiveness(graph.Parts["Stabilizer_R"]))*.5f);return false;
            }
        }
        [HarmonyPatch]
        private static class TailLoads
        {
            private static MethodBase TargetMethod()=>AccessTools.Method(CurrentAirframeCompatibility.FastType("Japensou.NuclearOption.Superheavy.SuperheavyFlightDynamics"),"ApplySurface");
            private static bool Prefix(Vector3 __0,Vector3 __1,Vector3 __2,float __3,float __4,float __5,UnitPart __6)
            {
                var graph=TailGraph(__6);if(!graph||!graph.Ready)return true;
                var left=graph.Parts["Stabilizer_L"];var right=graph.Parts["Stabilizer_R"];float le=LiveEffectiveness(left),re=LiveEffectiveness(right),total=le+re;if(total<=0)return false;
                // The retained flight interface has one full-span hstab
                // owner. The new graph has two independent half-span bodies.
                // Preserve its resultant force/moment when intact instead of
                // concentrating the full tail load on the left attachment.
                var aircraft=__6.parentUnit;var liftDirection=Vector3.ProjectOnPlane(__2,__1).normalized;
                foreach(var part in new[]{left,right})
                {
                    float effective=part==left?le:re;if(effective<=0||!part.rb||part.rb.isKinematic)continue;float share=effective/total;
                    float lateral=Vector3.Dot(part.rb.position-aircraft.rb.position,aircraft.transform.right);
                    part.rb.AddForceAtPosition((liftDirection*__4-__1*__5)*(__3*share),__0+aircraft.transform.right*lateral);
                    if(part==left)graph.LeftTailLoadApplications++;else graph.RightTailLoadApplications++;
                }
                return false;
            }
        }
        [HarmonyPatch(typeof(AeroPart),"CheckAttachment")]
        private static class MovingAttachment
        {
            private static bool Prefix(AeroPart __instance)
            {
                var marker=__instance.GetComponent<NativePartitionPart>();if(!marker)return true;
                var controller=__instance.parentUnit?__instance.parentUnit.GetComponent<NativePartitionController>():null;
                if(!controller||!controller.Ready||controller.IsRepairing(__instance)||!__instance.rb||(bool)Get(__instance,"simplePhysics"))return false;
                var constraints=(PartJoint[])Get(__instance,"joints");if(__instance.GetComponent<NativePartitionActuator>()&&constraints.Any(j=>j.joint))return false;
                var info=Get(__instance,"attachInfo");if(info==null||(bool)Get(info,"detachedFromParentPart"))return false;
                var parent=Get(info,"parentPart")as AeroPart;if(!parent||!parent.rb)return false;
                // Render transforms can be more than the native 0.5m break
                // tolerance behind their interpolated physics actors. Native
                // ownership/events must follow the actual simulation poses.
                var expected=parent.rb.position+parent.rb.rotation*(Vector3)Get(info,"localPosition");
                if(Vector3.Distance(__instance.rb.position,expected)<=.5f)return false;
                Set(info,"detachedFromParentPart",true);float streamlining=(float)Get(__instance,"streamlining");if(streamlining>0)parent.ModifyDrag(streamlining);
                __instance.parentUnit.DetachPart((byte)Get(__instance,"id"),__instance.rb.velocity,__instance.rb.position-__instance.parentUnit.rb.position);return false;
            }
        }
    }
    public sealed class NativePartitionPart:MonoBehaviour
    {
        public string Id,ParentId,SourceKey,OwnProfileRecipe;public bool OwnFractured;
        public Bounds CollisionBounds;public bool HasCollisionBounds;public Vector3 LastAeroForce;
        public void RestoreBounds(){if(!HasCollisionBounds)return;var size=CollisionBounds.extents;size=new Vector3(Mathf.Max(.0075f,size.x),Mathf.Max(.0075f,size.y),Mathf.Max(.0075f,size.z));CollisionBounds.extents=size;NativePartitionIntegration.Set(GetComponent<AeroPart>(),"bounds",CollisionBounds);NativePartitionIntegration.Set(GetComponent<AeroPart>(),"collisionSize",size);}
        public float PeakForce,PeakTorque,LastForce,LastTorque,ForceLimit,TorqueLimit;
        private void OnJointBreak(float force){var p=GetComponent<AeroPart>();if(p&&p.parentUnit){var graph=p.parentUnit.GetComponent<NativePartitionController>();if(graph){if(!string.IsNullOrEmpty(ParentId)&&graph.Parts.TryGetValue(ParentId,out var parent)&&parent&&!parent.IsDetached()&&!parent.GetComponent<NativePartitionPart>().OwnFractured)OwnFractured=true;graph.CollisionChanged();graph.RecordFailure(p,"joint",force);}}}
    }
    public sealed class NativePartitionSurface:MonoBehaviour {public string PartId,SourceOriginal;public bool Fresh,FixedReceiver,Painted,Interior,BakedInterior,InteriorFurniture;public Mesh Original,Paint1,Paint2,PlanningOriginal;public Renderer CachedRenderer;}
    public sealed class NativePartitionActuator:MonoBehaviour
    {
        public Transform Proxy,Reference;public BayDoor Gate;public Matrix4x4 RestProxy,RestBody;public Vector3 Travel;public string Kind;
        private AeroPart owner;private ConfigurableJoint joint;private BayDoor[] sharedGates;
        private static readonly FieldInfo Phase=AccessTools.Field(typeof(BayDoor),"openAmount");
        public Matrix4x4 DesiredLocal()
        {
            if(Gate){float phase=Mathf.Clamp01(Kind=="courier_bay"&&sharedGates!=null?sharedGates.Where(g=>g).Max(g=>(float)Phase.GetValue(g)):(float)Phase.GetValue(Gate));float split=Kind=="courier_bay" ? .18f : .16f;float lift=Mathf.SmoothStep(0,1,Mathf.Clamp01(phase/split));float slide=Mathf.SmoothStep(0,1,Mathf.Clamp01((phase-split)/(1-split)));return Matrix4x4.Translate(new Vector3(0,Travel.y*lift,Travel.z*slide))*RestBody;}
            return Proxy?Reference.worldToLocalMatrix*Proxy.localToWorldMatrix*RestProxy.inverse*RestBody:RestBody;
        }
        public void Initialize()
        {
            owner=GetComponent<AeroPart>();if(Kind=="courier_bay")sharedGates=owner.parentUnit.GetComponentsInChildren<BayDoor>(true).Where(g=>g.name.StartsWith("SHGT_BombBayGate")).ToArray();var native=((PartJoint[])NativePartitionIntegration.Get(owner,"joints"))[0];if(native.joint is ConfigurableJoint existing){joint=existing;return;}
            if(native.joint)Object.DestroyImmediate(native.joint);joint=gameObject.AddComponent<ConfigurableJoint>();joint.connectedBody=native.connectedPart.rb;joint.autoConfigureConnectedAnchor=false;joint.anchor=Vector3.zero;joint.connectedAnchor=Reference.InverseTransformPoint(transform.position);
            joint.xMotion=joint.yMotion=joint.zMotion=ConfigurableJointMotion.Locked;joint.angularXMotion=joint.angularYMotion=joint.angularZMotion=ConfigurableJointMotion.Free;joint.rotationDriveMode=RotationDriveMode.Slerp;
            // A 50-Hz servo spring at the physics Nyquist limit destabilized
            // the complete wing/body chain. Use a damped ~5-Hz response and
            // keep commanded torque below the physical attachment limit.
            joint.slerpDrive=new JointDrive{positionSpring=owner.rb.mass*900,positionDamper=owner.rb.mass*60,maximumForce=Math.Min(owner.rb.mass*1200,native.breakTorque*4)};
            var graph=owner.parentUnit.GetComponent<NativePartitionController>();
            if(graph&&graph.CourierRefit)
            {
                // Angular response depends on rotational inertia, not dry mass.
                // Bound the stiffest principal-axis frequency to4Hz; damp the
                // slowest axis and keep servo torque below attachment strength.
                var t=owner.rb.inertiaTensor;float small=Mathf.Min(t.x,Mathf.Min(t.y,t.z)),large=Mathf.Max(t.x,Mathf.Max(t.y,t.z));float spring=small*Mathf.Pow(2*Mathf.PI*4,2);
                joint.slerpDrive=new JointDrive{positionSpring=spring,positionDamper=2*.9f*Mathf.Sqrt(spring*large),maximumForce=native.breakTorque*2.5f};
            }
            joint.projectionMode=JointProjectionMode.PositionAndRotation;joint.projectionDistance=.04f;joint.projectionAngle=3f;joint.enablePreprocessing=false;
            joint.breakForce=native.breakForce*10;joint.breakTorque=native.breakTorque*10;joint.enableCollision=false;native.joint=joint;ApplyTarget();
        }
        private void FixedUpdate(){if(owner&&joint&&Reference&&!owner.IsDetached())ApplyTarget();}
        private void ApplyTarget(){var target=DesiredLocal();joint.connectedAnchor=target.GetColumn(3);joint.targetRotation=Quaternion.Inverse(target.rotation);}
    }
    [DefaultExecutionOrder(11000)]public sealed class NativePartitionController:MonoBehaviour
    {
        public string SourceKey,SourceSha256,SpawnPath;public int RemovedLegacyRenderers,RemovedLegacyColliders;public float ExpectedDryMass;public bool Ready;public int RepairTransactions,DeferredRepairs,LeftTailLoadApplications,RightTailLoadApplications;public float LastRepairPoseError;
        public readonly Dictionary<string,AeroPart> Parts=new Dictionary<string,AeroPart>();
        internal bool CourierRefit=>SourceKey=="jp_superheavy_ground_test"&&NativePartitionIntegration.Assets.TryGetValue(SourceKey,out var asset)&&asset.Profiles.courierRefitVersion==1;
        internal CourierPresentation Presentation;private CargoRamp[] cargoAccess;private BayDoor[] bayAccess;
        private static readonly FieldInfo DoorPhase=AccessTools.Field(typeof(BayDoor),"openAmount");
        internal bool InteriorAccessOpen=>cargoAccess!=null&&cargoAccess.Any(r=>r&&r.IsOpen())||bayAccess!=null&&bayAccess.Any(r=>r&&(float)DoorPhase.GetValue(r)>.05f);
        internal void RefreshAfterDamage(){if(Presentation)nextRefresh=0;else Refresh();}
        internal bool IsRepairing(AeroPart p)=>repairing.Contains(p);
        private Aircraft aircraft;internal FreshStructureDamage Damage;private Collider[] contacts;private Rigidbody[] boundBodies;private readonly List<Pair> pairs=new List<Pair>();private readonly HashSet<AeroPart> repairing=new HashSet<AeroPart>();private readonly Queue<AeroPart> pendingRepairs=new Queue<AeroPart>();private float nextRefresh;
        private sealed class Profile {internal AeroPart Part;internal FreshPartProfile Owned;internal string Category;}
        private readonly List<Profile> profiles=new List<Profile>();private int filterSettlingSteps;
        private bool collisionDirty=true,contactGrace;private float nextContactPolicy;private bool failureRecorded;
        private readonly Dictionary<string,NativePartitionSurface[]> surfaceOwners=new Dictionary<string,NativePartitionSurface[]>();
        private readonly Dictionary<string,string[]> exposureNeighbors=new Dictionary<string,string[]>();
        public void CollisionChanged()=>collisionDirty=true;
        public void RecordFailure(AeroPart part,string reason,float measured=0)
        {
            if(failureRecorded||repairing.Count>0)return;failureRecorded=true;var marker=part.GetComponent<NativePartitionPart>();
            if(CourierRefit&&!CurrentAirframeDamagePlugin.DetailedFailureDiagnostics.Value){CurrentAirframeDamagePlugin.Log.LogDebug("First structural failure: "+marker.Id+" ("+reason+")");return;}
            CurrentAirframeDamagePlugin.Log.LogWarning("OWN_FIRST_FAILURE "+JsonConvert.SerializeObject(new{aircraft=SourceKey,part=marker.Id,reason,time=Time.time,speed=aircraft.speed,radarAltitude=aircraft.radarAlt,velocity=aircraft.rb.velocity.ToString(),angularVelocity=aircraft.rb.angularVelocity.ToString(),hp=part.hitPoints,measured,marker.LastForce,marker.LastTorque,marker.ForceLimit,marker.TorqueLimit,mass=part.rb.mass,fieldMass=NativePartitionIntegration.Get(part,"mass"),inertia=part.rb.inertiaTensor.ToString(),position=part.rb.position.ToString(),partVelocity=part.rb.velocity.ToString(),aeroForce=marker.LastAeroForce.ToString(),collisionSize=marker.CollisionBounds.extents.ToString(),parent=part.transform.parent?part.transform.parent.name:null,inputs=aircraft.GetInputs()}));
        }
        private sealed class Pair {internal Collider A,B;internal UnitPart PA,PB;internal bool Ignored,Released,Waiting,ExternalA,ExternalB;internal float Grace;}
        private sealed class ContactGroup {internal UnitPart A,B;internal Collider ExternalA,ExternalB;internal Pair[] Pairs;internal bool Initialized,Attached,Waiting;}
        private ContactGroup[] contactGroups;private int groupedPairCount;
        private Dictionary<ulong,Pair> pairIndex;private int indexedPairCount;
        private Dictionary<ulong,Pair> PairIndex()
        {if(pairIndex==null||indexedPairCount!=pairs.Count){pairIndex=new Dictionary<ulong,Pair>();foreach(var p in pairs)if(p.A&&p.B)pairIndex[PairKey(p.A,p.B)]=p;indexedPairCount=pairs.Count;}return pairIndex;}
        private readonly Dictionary<Collider,AeroPart> externalDebris=new Dictionary<Collider,AeroPart>();
        private readonly Dictionary<Collider,Collider[]> externalClearance=new Dictionary<Collider,Collider[]>();
        private readonly Dictionary<Collider,HashSet<Collider>> gearNeighborhoods=new Dictionary<Collider,HashSet<Collider>>();
        public int PendingContactReleases,RestoredContactPairs;
        public float MaxContactReleaseDelay;
        private readonly Dictionary<UnitPart,UnitPart> islands=new Dictionary<UnitPart,UnitPart>();
        private UnitPart Island(UnitPart p)
        {
            if(!p||!islands.TryGetValue(p,out var parent))return p;
            var root=parent;while(true){var next=islands[root];if(next==root)break;root=next;}
            // path compression (21/11/2026): the root is unchanged, later lookups are O(1)
            var walk=p;while(walk!=root){var next=islands[walk];islands[walk]=root;walk=next;}
            return root;
        }
        internal bool SameAssembly(UnitPart a,UnitPart b)=>a&&b&&Island(a)==Island(b);
        private void RebuildIslands()
        {
            islands.Clear();foreach(var p in Parts.Values)if(p)islands[p]=p;
            // Native simple physics merges intact AeroParts into one actor.
            // Shared actors remain one island even after their welds are removed.
            islandActors.Clear();var actors=islandActors;
            foreach(var p in Parts.Values){if(!p||!p.rb)continue;
            if(actors.TryGetValue(p.rb,out var first)){var a=Island(p);var b=Island(first);if(a!=b)islands[a]=b;}else actors[p.rb]=p;}
            foreach(var p in Parts.Values){if(!p)continue;foreach(var descriptor in(PartJoint[])NativePartitionIntegration.Get(p,"joints")){var j=descriptor.joint;if(j&&j.connectedBody){var other=j.connectedBody.GetComponent<UnitPart>();if(other&&islands.ContainsKey(other)){var a=Island(p);var b=Island(other);if(a!=b)islands[a]=b;}}}}
        }
        private readonly Dictionary<Rigidbody,UnitPart> islandActors=new Dictionary<Rigidbody,UnitPart>();
        private sealed class State {internal AeroPart P;internal bool Kinematic,Detect,HadJoint;internal CollisionDetectionMode Mode;internal Vector3 Velocity,AngularVelocity;}
        private sealed class PhysicsPoseScope:IDisposable
        {
            private readonly List<(Rigidbody Body,RigidbodyInterpolation Interpolation)> modes=new List<(Rigidbody,RigidbodyInterpolation)>();
            internal PhysicsPoseScope(IEnumerable<AeroPart> parts)
            {
                // PhysX collider/joint setters may synchronize the whole
                // nested hierarchy. Publish a coherent physics pose for the
                // entire island before changing a child actor, then suppress
                // interpolation only for the short repair transaction.
                var poses=parts.Where(p=>p&&p.rb).OrderBy(p=>p.GetComponentsInParent<Transform>(true).Length).Select(p=>new{Body=p.rb,Position=p.rb.position,Rotation=p.rb.rotation}).ToArray();
                foreach(var p in poses){modes.Add((p.Body,p.Body.interpolation));p.Body.interpolation=RigidbodyInterpolation.None;}
                foreach(var p in poses){p.Body.transform.SetPositionAndRotation(p.Position,p.Rotation);p.Body.position=p.Position;p.Body.rotation=p.Rotation;}
                Physics.SyncTransforms();
            }
            public void Dispose(){foreach(var p in modes)if(p.Body)p.Body.interpolation=p.Interpolation;}
        }
        internal void Bind(Aircraft a)
        {
            aircraft=a;foreach(var p in a.partLookup.OfType<AeroPart>()){var marker=p.GetComponent<NativePartitionPart>();if(marker)Parts.Add(marker.Id,p);}
            if(Parts.Count!=NativePartitionIntegration.Assets[SourceKey].M.parts.Length)throw new InvalidOperationException("Expanded native parts did not register before complex physics.");
            var rows=NativePartitionIntegration.Assets[SourceKey].M.parts.ToDictionary(r=>r.id);
            foreach(var entry in Parts){var p=entry.Value;string category="Body";for(string id=entry.Key;id!=null;id=rows[id].parent){if(id.StartsWith("Engine_")){category="Engine";break;}if(id.StartsWith("Wing_")){category="Wing";break;}if(id.StartsWith("Fin_")||id.StartsWith("Stabilizer_")||id=="TailSpine"){category="Tail";break;}}
                profiles.Add(new Profile{Part=p,Owned=NativePartitionIntegration.Assets[SourceKey].Profiles.ById[entry.Key],Category=category});}
            if(CourierRefit&&GetComponent<Japensou.NuclearOption.AirframeData.CourierPresentationData>()){Presentation=GetComponent<CourierPresentation>()??gameObject.AddComponent<CourierPresentation>();if(!Presentation.Available)Presentation.Bind(this);cargoAccess=GetComponentsInChildren<CargoRamp>(true).Where(r=>r.name=="SHGT_CargoReleaseGate").ToArray();bayAccess=GetComponentsInChildren<BayDoor>(true);}
            Damage=gameObject.GetComponent<FreshStructureDamage>()??gameObject.AddComponent<FreshStructureDamage>();Damage.Bind(this,a);
            foreach(var group in Parts.Values.SelectMany(p=>p.GetComponentsInChildren<NativePartitionSurface>(true)).Distinct().GroupBy(s=>s.PartId))surfaceOwners[group.Key]=group.ToArray();
            foreach(string id in Parts.Keys){var neighbors=new List<string>{id};string skin=id.Replace("LocalFrame | ","Shell | ");if(skin!=id&&Parts.ContainsKey(skin))neighbors.Add(skin);if(!id.Contains(" | "))neighbors.AddRange(rows.Values.Where(r=>r.parent==id&&(r.id.StartsWith("Shell | ")||r.id.StartsWith("LocalFrame | ")||r.id.StartsWith("Bay | "))).Select(r=>r.id));exposureNeighbors[id]=neighbors.Distinct().ToArray();}
            if(CourierRefit&&GetComponent<Japensou.NuclearOption.AirframeData.CourierPresentationData>() is var ownershipData&&ownershipData&&ownershipData.SectionAnimations!=null)
                (GetComponent<CourierAssemblyOwnership>()??gameObject.AddComponent<CourierAssemblyOwnership>()).Bind(this);
            Ready=true;ApplyConfiguredResistance();
        }
        public void ApplyConfiguredResistance()
        {
            foreach(var profile in profiles)
            {
                if(!profile.Part)continue;float scale=profile.Category=="Wing"?CurrentAirframeDamagePlugin.WingResistance.Value:profile.Category=="Tail"?CurrentAirframeDamagePlugin.TailResistance.Value:profile.Category=="Engine"?CurrentAirframeDamagePlugin.EngineResistance.Value:CurrentAirframeDamagePlugin.BodyResistance.Value;
                NativePartitionIntegration.Set(profile.Part,"armorProperties",profile.Owned.armor.Native(scale));
                foreach(var joint in (PartJoint[])NativePartitionIntegration.Get(profile.Part,"joints")){joint.breakForce=profile.Owned.jointForceN*CurrentAirframeDamagePlugin.JointStrength.Value/10f;joint.breakTorque=profile.Owned.jointTorqueNm*CurrentAirframeDamagePlugin.JointStrength.Value/10f;}
                profile.Part.ApplyDamage(0,0,0,0);
            }
            foreach(var engine in Parts.Values.Where(p=>p).SelectMany(p=>p.GetComponentsInChildren<Turbofan>(true)).Distinct())
            {var owner=NativePartitionIntegration.Get(engine,"part")as AeroPart;if(owner){var profile=NativePartitionIntegration.Assets[SourceKey].Profiles.ById[owner.GetComponent<NativePartitionPart>().Id];NativePartitionIntegration.Set(engine,"armorProperties",profile.armor.Native(CurrentAirframeDamagePlugin.EngineResistance.Value));}}
        }
        internal void AfterComplex()
        {
            if(!Ready||aircraft.simplePhysics)return;var updated=Parts.Values.Where(p=>p).SelectMany(p=>p.GetComponentsInChildren<Collider>(true)).Where(c=>c&&c.enabled&&!c.isTrigger).Distinct().ToArray();var bodies=Parts.Values.Where(p=>p&&p.rb).Select(p=>p.rb).Distinct().OrderBy(p=>p.GetInstanceID()).ToArray();bool newBodies=boundBodies==null||!bodies.SequenceEqual(boundBodies);if(!newBodies&&contacts!=null&&contacts.Length==updated.Length&&contacts.All(updated.Contains))return;contacts=updated;boundBodies=bodies;if(newBodies)foreach(var pair in pairs)pair.Ignored=false;
            if(CourierRefit)foreach(var p in Parts.Values)if(p&&p.rb)ApplyInertia(p);
            foreach(var actuator in Parts.Values.Where(p=>p&&p.rb).Select(p=>p.GetComponent<NativePartitionActuator>()).Where(a=>a))actuator.Initialize();
            var previous=pairs.Where(p=>p.A&&p.B).GroupBy(p=>PairKey(p.A,p.B)).ToDictionary(g=>g.Key,g=>g.First());pairs.Clear();pairIndex=null;Physics.SyncTransforms();
            var owners=contacts.Select(c=>c.GetComponentInParent<UnitPart>()).ToArray();var envelopes=contacts.Select((c,i)=>Envelope(c,owners[i])).ToArray();
            var ordered=Enumerable.Range(0,contacts.Length).OrderBy(i=>envelopes[i].min.x).ToArray();
            for(int ai=0;ai<ordered.Length;ai++)for(int bi=ai+1;bi<ordered.Length;bi++)
            {
                int i=ordered[ai],j=ordered[bi];var expanded=envelopes[i];expanded.Expand(.05f);if(envelopes[j].min.x>expanded.max.x)break;
                var a=contacts[i];var b=contacts[j];if(a.attachedRigidbody==b.attachedRigidbody)continue;var pa=owners[i];var pb=owners[j];if(!pa||!pb||pa.parentUnit!=aircraft||pb.parentUnit!=aircraft)continue;var ba=envelopes[i];ba.Expand(.05f);if(!ba.Intersects(envelopes[j]))continue;previous.TryGetValue(PairKey(a,b),out var saved);pairs.Add(saved??new Pair{A=a,B=b,PA=pa,PB=pb});
            }
            foreach(var external in externalDebris.ToArray())if(external.Key&&external.Value)AddExternalPairs(external.Key,external.Value);
            foreach(var p in Parts.Values){if(!p||!p.rb)continue;p.rb.solverIterations=48;p.rb.solverVelocityIterations=12;p.rb.maxDepenetrationVelocity=6;ApplyInertia(p);}
            foreach(var pair in pairs)pair.Ignored=false;contactGroups=null;filterSettlingSteps=4;CollisionChanged();CollisionPolicy();CurrentAirframeDamagePlugin.Log.LogInfo("NATIVE_PARTITION_BOUND "+SourceKey+" bodies="+Parts.Count+" joints="+Parts.Values.Sum(p=>p.GetComponents<Joint>().Length)+" contacts="+contacts.Length+" filteredPairs="+pairs.Count);
        }
        private static ulong PairKey(Collider a,Collider b){uint x=(uint)a.GetInstanceID(),y=(uint)b.GetInstanceID();return x<y?((ulong)x<<32)|y:((ulong)y<<32)|x;}
        private static void ConditionInertia(Rigidbody body)
        {
            // Sampled slender convex cells can give one axis almost zero
            // inertia (observed 853/853/0.05), ill-conditioning welded actors.
            // Keep the geometry-derived principal frame and total mass; bound
            // only the tensor's numerical aspect ratio for the coarse module.
            var t=body.inertiaTensor;float largest=Mathf.Max(t.x,Mathf.Max(t.y,t.z));float floor=Mathf.Max(body.mass*.01f,largest/128f);
            body.inertiaTensor=new Vector3(Mathf.Max(t.x,floor),Mathf.Max(t.y,floor),Mathf.Max(t.z,floor));
        }
        private void ApplyInertia(AeroPart part)
        {
            var cg=(Transform)NativePartitionIntegration.Get(part,"centerOfMass");part.rb.centerOfMass=part.transform.InverseTransformPoint(cg.position);
            if(CourierRefit)
            {
                var profile=NativePartitionIntegration.Assets[SourceKey].Profiles.ById[part.GetComponent<NativePartitionPart>().Id];
                part.rb.inertiaTensorRotation=Quaternion.identity;
                part.rb.inertiaTensor=NativePartitionIntegration.V(profile.authoredInertiaKgM2)*(part.rb.mass/profile.massKg);
            }
            else{part.rb.ResetInertiaTensor();ConditionInertia(part.rb);}
        }
        private static Bounds Envelope(Collider c,UnitPart owner)
        {
            var b=c.bounds;var actuator=owner?owner.GetComponent<NativePartitionActuator>():null;if(!actuator)return b;
            if(actuator.Kind=="bay"||actuator.Kind=="courier_bay"){var other=new Bounds(b.center+actuator.Reference.TransformVector(actuator.Travel),b.size);b.Encapsulate(other);}else b.Expand(12f);return b;
        }
        private void LateUpdate(){if(Ready){CollisionPolicy();if(Time.unscaledTime>=nextRefresh){nextRefresh=Time.unscaledTime+.1f;Refresh();}if(CourierRefit)Damage?.SyncVisibility();}}
        private static readonly MethodInfo CheckAttachment=AccessTools.Method(typeof(AeroPart),"CheckAttachment");
        private void FixedUpdate()
        {
            if(!Ready||aircraft.simplePhysics)return;
            // A whole-aircraft service can request several detached sections
            // in one frame. Serialize changes to the shared physics island.
            if(repairing.Count==0&&pendingRepairs.Count>0)RequestRepair(pendingRepairs.Dequeue());
            if(filterSettlingSteps>0){foreach(var pair in pairs)pair.Ignored=false;if(contactGroups!=null)foreach(var group in contactGroups)group.Initialized=false;filterSettlingSteps--;CollisionChanged();}
            CollisionPolicy();
            // The game's aerodynamic-job callback does not poll zero-lift panel/frame bodies.
            // Forward actual lost constraints through its own native attachment check as well.
            foreach(var p in Parts.Values)if(p&&!p.IsDetached()&&!repairing.Contains(p))
            {
                var joints=(PartJoint[])NativePartitionIntegration.Get(p,"joints");
                foreach(var j in joints)if(j.joint){var m=p.GetComponent<NativePartitionPart>();m.LastForce=j.joint.currentForce.magnitude;m.LastTorque=j.joint.currentTorque.magnitude;m.PeakForce=Mathf.Max(m.PeakForce,m.LastForce);m.PeakTorque=Mathf.Max(m.PeakTorque,m.LastTorque);m.ForceLimit=j.joint.breakForce;m.TorqueLimit=j.joint.breakTorque;}
                if(joints.Length>0&&joints.All(j=>!j.joint))CheckAttachment.Invoke(p,null);
            }
        }
        private void CollisionPolicy()
        {
            if(!collisionDirty&&(!contactGrace||Time.unscaledTime<nextContactPolicy))return;
            collisionDirty=false;contactGrace=false;PendingContactReleases=0;nextContactPolicy=Time.unscaledTime+.033f;RebuildIslands();
            // evaluated on demand (21/11/2026): only waiting external pairs need it, each collider at most once per pass
            externalClearMemo.Clear();
            if(Parts.TryGetValue("Core",out var core))foreach(var part in Parts.Values.Where(p=>p&&p.rb&&!p.rb.isKinematic))
            {var mode=SameAssembly(part,core)?CollisionDetectionMode.Continuous:CollisionDetectionMode.ContinuousDynamic;if(part.rb.collisionDetectionMode!=mode)part.rb.collisionDetectionMode=mode;}
            if(contactGroups==null||groupedPairCount!=pairs.Count)
            {contactGroups=pairs.GroupBy(p=>(p.PA,p.PB,p.ExternalA?p.A:null,p.ExternalB?p.B:null)).Select(g=>new ContactGroup{A=g.Key.Item1,B=g.Key.Item2,ExternalA=g.Key.Item3,ExternalB=g.Key.Item4,Pairs=g.ToArray()}).ToArray();groupedPairCount=pairs.Count;}
            foreach(var group in contactGroups)
            {
                bool attached=SameAssembly(group.A,group.B)&&(!group.ExternalA||ExternalAttached(group.ExternalA))&&(!group.ExternalB||ExternalAttached(group.ExternalB));
                bool external=group.ExternalA||group.ExternalB;
                if(external)
                {
                    bool enabled=(!group.ExternalA||group.ExternalA.enabled)&&(!group.ExternalB||group.ExternalB.enabled);
                    if(!enabled){group.Initialized=false;contactGrace=true;continue;}
                    if(attached)contactGrace=true;
                    foreach(var c in new[]{group.ExternalA,group.ExternalB})if(c&&c.attachedRigidbody&&!c.attachedRigidbody.isKinematic&&!ExternalAttached(c))c.attachedRigidbody.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
                }
                // A topology change affects only a small set of owner pairs.
                // Preserve already-applied filters on all other shape pairs.
                if(group.Initialized&&group.Attached==attached&&!group.Waiting)continue;
                group.Initialized=true;group.Attached=attached;group.Waiting=false;
                foreach(var p in group.Pairs)
                {
                if(!p.A||!p.B||!p.PA||!p.PB)continue;
                // Only origin-neighbor pairs wait for actual geometric clearance.
                // Once restored, re-contact is an ordinary physical collision.
                // World contacts and other aircraft never enter this table.
                if(attached){p.Released=false;p.Waiting=false;p.Grace=0;}
                else if(!p.Released){p.Released=true;p.Waiting=true;p.Grace=Time.time;}
                if(!attached&&p.Waiting)
                {
                    bool pathClear=(!p.ExternalA||ExternalClearOnce(p.A))&&(!p.ExternalB||ExternalClearOnce(p.B));
                    if(pathClear&&!Penetrates(p.A,p.B))
                    {p.Waiting=false;RestoredContactPairs++;MaxContactReleaseDelay=Mathf.Max(MaxContactReleaseDelay,Time.time-p.Grace);}
                    if(p.Waiting){contactGrace=true;group.Waiting=true;PendingContactReleases++;}
                }
                bool ignore=attached||p.Waiting;
                if(ignore!=p.Ignored||(p.ExternalA||p.ExternalB)&&Physics.GetIgnoreCollision(p.A,p.B)!=ignore){Physics.IgnoreCollision(p.A,p.B,ignore);p.Ignored=ignore;}
                }
            }
        }
        private readonly Dictionary<Collider,bool> externalClearMemo=new Dictionary<Collider,bool>();
        // same value the eager table gave: only registered, live debris owned by a live part can be clear
        private bool ExternalClearOnce(Collider c)
        {
            if(externalClearMemo.TryGetValue(c,out var v))return v;
            v=c&&externalDebris.TryGetValue(c,out var owner)&&owner&&ExternalClear(c);externalClearMemo[c]=v;return v;
        }
        private bool ExternalAttached(Collider c)=>!c.attachedRigidbody||boundBodies!=null&&boundBodies.Contains(c.attachedRigidbody);
        private bool ExternalClear(Collider c)
        {
            if(!c.enabled)return false;
            if(!externalClearance.TryGetValue(c,out var cells)||cells.Length==0)return true;
            var ext=ContactBounds(c);var bounds=ContactBounds(cells[0]);foreach(var cell in cells)if(cell)bounds.Encapsulate(ContactBounds(cell));bounds.Expand(.04f);
            return !bounds.Intersects(ext);
        }
        private static Bounds ContactBounds(Collider c)
        {
            if(!(c is BoxCollider box))return c.bounds;PhysicsPose(c,out var p,out var r);var scale=c.transform.lossyScale;var size=Vector3.Scale(box.size,scale)*.5f;
            var x=r*Vector3.right*size.x;var y=r*Vector3.up*size.y;var z=r*Vector3.forward*size.z;
            return new Bounds(p+r*Vector3.Scale(box.center,scale),new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y),Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z))*2);
        }
        public void RegisterExternalDebris(Collider collider,AeroPart owner)=>RegisterExternalDebris(collider,owner,false);
        internal void RegisterGearDebris(Collider collider,AeroPart support)
        {
            // Only adjacent origin shapes need a separation grace period.
            // A global Core branch made every gear wait on an expanding
            // whole-aircraft AABB and maintained ~4,000 unnecessary pairs.
            if(!gearNeighborhoods.ContainsKey(collider)&&contacts!=null)
            {var b=ContactBounds(collider);b.Expand(.2f);gearNeighborhoods[collider]=new HashSet<Collider>(contacts.Where(c=>c&&b.Intersects(ContactBounds(c))));}
            RegisterExternalDebris(collider,support,true);
        }
        public void RegisterExternalDebris(Collider collider,AeroPart owner,bool deferPolicy)
        {if(!owner)return;if(!externalDebris.ContainsKey(collider)){externalDebris[collider]=owner;if(contacts!=null)AddExternalPairs(collider,owner);if(Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1")CurrentAirframeDamagePlugin.Log.LogInfo("OWN_EXTERNAL_DEBRIS_REGISTER "+collider.name+" owner="+owner.name+" ready="+Ready+" pairs="+pairs.Count);}if(contactGroups!=null)foreach(var group in contactGroups)if(group.ExternalA==collider||group.ExternalB==collider)group.Initialized=false;CollisionChanged();if(Ready&&!deferPolicy)CollisionPolicy();}
        private void AddExternalPairs(Collider collider,AeroPart owner)
        {
            var ownId=owner.GetComponent<NativePartitionPart>()?.Id;var branch=new HashSet<UnitPart>();
            foreach(var p in Parts.Values){for(string id=p.GetComponent<NativePartitionPart>()?.Id;!string.IsNullOrEmpty(id)&&Parts.TryGetValue(id,out var current);id=current.GetComponent<NativePartitionPart>().ParentId)if(id==ownId){branch.Add(p);break;}}
            externalClearance[collider]=contacts.Where(c=>c&&branch.Contains(c.GetComponentInParent<UnitPart>())).ToArray();
            var previous=pairs.Where(p=>p.A==collider||p.B==collider).GroupBy(p=>p.A==collider?p.B:p.A).ToDictionary(g=>g.Key,g=>g.First());
            foreach(var other in contacts)
            {
                if(!other||other==collider)continue;var part=other.GetComponentInParent<AeroPart>();if(!part||!Parts.Values.Contains(part))continue;
                if(gearNeighborhoods.TryGetValue(collider,out var neighborhood)&&!neighborhood.Contains(other))continue;
                previous.TryGetValue(other,out var old);
                if(old!=null){if(old.A==collider){old.ExternalA=true;old.PA=owner;}else{old.ExternalB=true;old.PB=owner;}continue;}
                pairs.Add(new Pair{A=collider,B=other,PA=owner,PB=part,ExternalA=true});
            }
        }
        private static void PhysicsPose(Collider c,out Vector3 position,out Quaternion rotation)
        {
            var body=c.attachedRigidbody;if(!body){position=c.transform.position;rotation=c.transform.rotation;return;}
            if(c.transform==body.transform){position=body.position;rotation=body.rotation;return;}
            var local=body.transform.worldToLocalMatrix*c.transform.localToWorldMatrix;
            position=body.position+body.rotation*(Vector3)local.GetColumn(3);rotation=body.rotation*local.rotation;
        }
        private static bool Penetrates(Collider a,Collider b)
        {
            PhysicsPose(a,out var ap,out var ar);PhysicsPose(b,out var bp,out var br);
            return Physics.ComputePenetration(a,ap,ar,b,bp,br,out var direction,out float distance)&&distance>.0001f;
        }
        internal void TrackAttachedContact(Collider a,Collider b)
        {
            var index=PairIndex();var key=PairKey(a,b);if(index.TryGetValue(key,out var existing)){existing.Ignored=true;return;}
            var added=new Pair{A=a,B=b,PA=a.GetComponentInParent<UnitPart>(),PB=b.GetComponentInParent<UnitPart>(),Ignored=true};pairs.Add(added);index[key]=added;indexedPairCount=pairs.Count;
            CollisionChanged();
        }
        public void Refresh()
        {
            bool near=!Camera.main||(Camera.main.transform.position-transform.position).sqrMagnitude<CurrentAirframeDamagePlugin.Distance.Value*CurrentAirframeDamagePlugin.Distance.Value;
            if(Presentation)Presentation.PrepareVisibility();
            foreach(var p in Parts)
            {
                if(!p.Value)continue;
                bool fragmented=(bool)NativePartitionIntegration.Get(p.Value,"fragmented");
                bool exposed=false;foreach(string id in exposureNeighbors[p.Key])if(Parts[id].IsDetached()||(Damage&&Damage.Exposed(id))){exposed=true;break;}
                if(surfaceOwners.TryGetValue(p.Key,out var ownedSurfaces))foreach(var meta in ownedSurfaces)if(meta)
                {
                    if(!meta.CachedRenderer)meta.CachedRenderer=meta.GetComponent<Renderer>();
                    bool visible=meta.Interior&&Presentation?!fragmented&&Presentation.InteriorVisible(meta,exposed):meta.Fresh?near&&exposed:!fragmented;
                    if(meta.CachedRenderer.enabled!=visible)meta.CachedRenderer.enabled=visible;
                }
            }
            // GE-87 bay/access interior pieces (baked, outside the presentation tables): same rule as the
            // native interior furniture, plus visible once any part has come off (the cavity is open).
            if(addedInterior==null)
            {
                var all=GetComponentsInChildren<Renderer>(true).Concat(Parts.Values.Where(x=>x).SelectMany(x=>x.GetComponentsInChildren<Renderer>(true))).Where(r=>r).Distinct().ToArray();
                // flight deck/crew cabin: also seen through the windscreen and side windows (23/11: not the upper
                // access stairs, which showed outside the hull at the side)
                bool Window(Renderer r)=>r.name.StartsWith("GE87 Interior | Cabin");
                addedInterior=all.Where(r=>r.name.StartsWith("GE87 Interior |")&&!Window(r)).ToArray();
                windowInterior=all.Where(r=>r.name.StartsWith("GE87 Interior |")&&Window(r)).ToArray();
                windowShadow=windowInterior.Select(r=>r.shadowCastingMode).ToArray();
                CourierPresentation.TrimCabinMonitors(windowInterior,transform);
                var filler=CourierPresentation.CabinCeilingFiller(windowInterior,transform);
                if(filler){windowInterior=windowInterior.Concat(new[]{filler}).ToArray();windowShadow=windowShadow.Concat(new[]{UnityEngine.Rendering.ShadowCastingMode.Off}).ToArray();}
                CurrentAirframeDamagePlugin.Log.LogInfo("Courier cabin grey: "+CourierPresentation.DarkenCabin(all)+" renderers.");
                // folding crew stairs (segments 01-09; 00 is the door): stowed, they stick out of the GE87 nose skin
                crewStairs=all.Where(r=>r.name.Length==23&&r.name.StartsWith("SHGT_CrewAccess_Mesh_0")&&r.name!="SHGT_CrewAccess_Mesh_00").ToArray();
                // ramp/cassette mechanisms inside the hold (the ramp skin 011 and clamshell doors 021/022 are exterior)
                cargoInterior=all.Where(r=>r.name.StartsWith("SHGT_CargoMesh_")&&r.name!="SHGT_CargoMesh_011"&&r.name!="SHGT_CargoMesh_021"&&r.name!="SHGT_CargoMesh_022").ToArray();
                // seats, controls and displays behind the windscreen: invisible detail from a distance
                cockpitDetail=all.Where(r=>r.name.StartsWith("SHGT_EjectionSeat_")||r.name.StartsWith("SHGT_CockpitControlMesh_")||r.name.StartsWith("SHGT_MFD_")||r.name.StartsWith("SHGT_CrewAccess_Mesh_FlightDeck")).ToArray();
            }
            {
                // 23/11 (owner): the whole interior is drawn within 250 m of the camera, at any angle: cabin, bay, CPS,
                // accesses, ramp/cassette mechanisms and the cockpit detail. Measured with only the Courier and its
                // dependencies (Free Flight Heartland): ~+0.4 ms GPU and ~+1,100 draw calls with the SRP Batcher,
                // 60 fps held; at 600 m it still cost ~0.5 ms with nothing visible, hence the range. Interior shadows
                // made no measurable difference, so the authored modes are kept.
                bool on=!Presentation||Presentation.InteriorInRange;
                foreach(var r in addedInterior)if(r&&r.enabled!=on)r.enabled=on;
                for(int i=0;i<windowInterior.Length;i++)
                {
                    var r=windowInterior[i];if(!r)continue;
                    if(r.enabled!=on)r.enabled=on;
                    if(r.shadowCastingMode!=windowShadow[i])r.shadowCastingMode=windowShadow[i];
                }
                if(Presentation)Presentation.WindowShadows(false);
                foreach(var r in cargoInterior)if(r&&r.forceRenderingOff==on)r.forceRenderingOff=!on;
                // stowed crew stairs stick out of the GE87 nose skin: hidden until the crew access starts opening
                bool stairs=CrewStairsOut();
                foreach(var r in crewStairs)if(r&&r.forceRenderingOff==stairs)r.forceRenderingOff=!stairs;
                foreach(var r in cockpitDetail)if(r&&r.forceRenderingOff==on)r.forceRenderingOff=!on;
            }
        }
        private Renderer[] crewStairs=new Renderer[0];
        private Component crewAccess;private static readonly System.Type CrewAccessType=AccessTools.TypeByName("Japensou.NuclearOption.Superheavy.SuperheavyCrewAccess");
        private static readonly System.Reflection.FieldInfo CrewAccessProgress=CrewAccessType!=null?AccessTools.Field(CrewAccessType,"progress"):null;
        private bool CrewStairsOut()
        {
            if(CrewAccessType==null||CrewAccessProgress==null)return true; // unknown family runtime: leave them as authored
            if(!crewAccess)crewAccess=GetComponent(CrewAccessType);
            return crewAccess&&(float)CrewAccessProgress.GetValue(crewAccess)>0f;
        }
        private Renderer[] addedInterior,windowInterior=new Renderer[0],cargoInterior=new Renderer[0],cockpitDetail=new Renderer[0];
        private UnityEngine.Rendering.ShadowCastingMode[] windowShadow=new UnityEngine.Rendering.ShadowCastingMode[0];
        public void RequestRepair(AeroPart part)
        {
            if(!part||!part.rb||repairing.Contains(part))return;
            if(repairing.Count>0){if(!pendingRepairs.Contains(part))pendingRepairs.Enqueue(part);return;}
            var constraints=(PartJoint[])NativePartitionIntegration.Get(part,"joints");
            if(!part.IsDetached()&&(constraints.Length==0||constraints.Any(j=>j.joint))){Damage?.RepairPart(part);part.GetComponent<NativePartitionPart>().OwnFractured=false;part.hitPoints=100;part.ApplyDamage(0,0,0,0);Refresh();return;}
            var info=NativePartitionIntegration.Get(part,"attachInfo");var parent=info==null?null:NativePartitionIntegration.Get(info,"parentPart")as AeroPart;
            if(!parent||parent.IsDetached()){DeferredRepairs++;return;}
            var assembly=new List<AeroPart>{part};for(int i=0;i<assembly.Count;i++)foreach(var child in Parts.Values.Where(p=>p).Except(assembly).ToArray())
            {var childInfo=NativePartitionIntegration.Get(child,"attachInfo");var marker=child.GetComponent<NativePartitionPart>();if(childInfo!=null&&(NativePartitionIntegration.Get(childInfo,"parentPart")as AeroPart)==assembly[i]&&(child.GetComponents<Joint>().Any(j=>j&&j.connectedBody==assembly[i].rb)||marker&&!marker.OwnFractured))assembly.Add(child);}
            foreach(var p in assembly)repairing.Add(p);StartCoroutine(Rejoin(assembly,parent));
        }
        private IEnumerator Rejoin(List<AeroPart> assembly,AeroPart parent)
        {
            using(var physicsPose=new PhysicsPoseScope(Parts.Values))
            {
            var states=assembly.Select(p=>new State{P=p,Kinematic=p.rb.isKinematic,Detect=p.rb.detectCollisions,Mode=p.rb.collisionDetectionMode,Velocity=p.rb.velocity,AngularVelocity=p.rb.angularVelocity,HadJoint=p.GetComponents<Joint>().Any(j=>j)}).ToArray();
            foreach(var s in states){s.P.rb.detectCollisions=false;s.P.rb.collisionDetectionMode=CollisionDetectionMode.Discrete;s.P.rb.isKinematic=true;foreach(var j in s.P.GetComponents<Joint>())Object.Destroy(j);}
            yield return null;
            if(!parent||parent.IsDetached())
            {foreach(var s in states)if(s.P){s.P.rb.isKinematic=s.Kinematic;if(!s.Kinematic){s.P.rb.velocity=s.Velocity;s.P.rb.angularVelocity=s.AngularVelocity;}if(s.HadJoint){s.P.CreateJoints();s.P.GetComponent<NativePartitionActuator>()?.Initialize();}s.P.rb.collisionDetectionMode=s.Mode;s.P.rb.detectCollisions=s.Detect;repairing.Remove(s.P);}DeferredRepairs++;yield break;}
            var diagnostic=Parts.Values.Where(p=>p&&p.rb&&!assembly.Contains(p)).Select(p=>new{P=p,Position=p.rb.position,Rotation=p.rb.rotation,Velocity=p.rb.velocity}).ToArray();
            LastRepairPoseError=0;
            foreach(var s in states)
            {
                var p=s.P;var info=NativePartitionIntegration.Get(p,"attachInfo");var host=(AeroPart)NativePartitionIntegration.Get(info,"parentPart");
                var position=host.rb.position+host.rb.rotation*(Vector3)NativePartitionIntegration.Get(info,"localPosition");var rotation=host.rb.rotation*(Quaternion)NativePartitionIntegration.Get(info,"localRotation");
                var actuator=p.GetComponent<NativePartitionActuator>();if(actuator){var target=Matrix4x4.TRS(host.rb.position,host.rb.rotation,Vector3.one)*actuator.DesiredLocal();position=target.GetColumn(3);rotation=target.rotation;}
                if(Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1"&&p==assembly[0])CurrentAirframeDamagePlugin.Log.LogInfo("OWN_REPAIR_FRAME "+p.GetComponent<NativePartitionPart>().Id+" parentInterpolation="+host.rb.interpolation+" parentRenderLag="+Vector3.Distance(host.rb.position,host.transform.position)+" childInterpolation="+p.rb.interpolation+" scale="+host.transform.lossyScale);
                // PhysicsPoseScope has aligned the parent's render/physics
                // poses, so publishing this child pose cannot rewind Core.
                p.rb.position=position;p.rb.rotation=rotation;p.transform.SetPositionAndRotation(position,rotation);
                NativePartitionIntegration.Set(info,"detachedFromParentPart",false);NativePartitionIntegration.Set(p,"detachedFromUnit",false);p.enabled=true;if(p==assembly[0]){Damage?.RepairPart(p);p.GetComponent<NativePartitionPart>().OwnFractured=false;p.hitPoints=100;NativePartitionIntegration.Set(p,"fragmented",false);}
                var cb=(Action<UnitPart>)Delegate.CreateDelegate(typeof(Action<UnitPart>),p,AccessTools.Method(p.GetType(),"UnitPart_OnParentDetached"));host.onParentDetached-=cb;host.onParentDetached+=cb;
                LastRepairPoseError=Mathf.Max(LastRepairPoseError,Vector3.Distance(p.rb.position,position));
            }
            // Joint frames must be created from the repaired physics poses,
            // not the pre-teleport solver poses of the detached bodies.
            Physics.SyncTransforms();
            // Preserve the native actor/job identity while restoring its owned
            // mass properties. Replacing the actor leaves native jobs with a
            // different physics handle than their registered body.
            foreach(var s in states)
            {
                var p=s.P;ApplyInertia(p);
            }
            foreach(var s in states){s.P.rb.isKinematic=s.Kinematic;s.P.rb.velocity=parent.rb.GetPointVelocity(s.P.rb.worldCenterOfMass);s.P.rb.angularVelocity=parent.rb.angularVelocity;s.P.rb.drag=0;NativePartitionIntegration.Set(s.P,"wingEffectiveness",1f);s.P.CreateJoints();s.P.GetComponent<NativePartitionActuator>()?.Initialize();s.P.ApplyDamage(0,0,0,0);s.P.rb.collisionDetectionMode=s.Mode;s.P.rb.detectCollisions=s.Detect;}
            Physics.SyncTransforms();
            AfterComplex();
            foreach(var own in contacts.Where(c=>c&&assembly.Contains(c.GetComponentInParent<AeroPart>())))foreach(var other in contacts)
            {
                if(!other||own==other||own.attachedRigidbody==other.attachedRigidbody||!own.bounds.Intersects(other.bounds))continue;
                if(PairIndex().ContainsKey(PairKey(own,other)))continue;
                pairs.Add(new Pair{A=own,B=other,PA=own.GetComponentInParent<UnitPart>(),PB=other.GetComponentInParent<UnitPart>(),Grace=Time.time+.4f});
            }
            foreach(var pair in pairs)if(assembly.Contains(pair.PA as AeroPart)||assembly.Contains(pair.PB as AeroPart)){pair.Grace=Time.time+.4f;pair.Ignored=false;}
            // Enabling an actor can rebuild the whole PhysX island and clear
            // cached filters beyond the repaired actor's own contact pairs.
            foreach(var pair in pairs)pair.Ignored=false;filterSettlingSteps=4;
            CollisionPolicy();
            if(Environment.GetEnvironmentVariable("CURRENT_FRAME_NATIVE_AUDIT")=="1")CurrentAirframeDamagePlugin.Log.LogInfo("OWN_REPAIR_SIDE_EFFECT "+string.Join(";",diagnostic.OrderByDescending(s=>Vector3.Distance(s.Position,s.P.rb.position)).Take(4).Select(s=>s.P.GetComponent<NativePartitionPart>().Id+" moved="+Vector3.Distance(s.Position,s.P.rb.position)+" rotated="+Quaternion.Angle(s.Rotation,s.P.rb.rotation)+" velocityChange="+Vector3.Distance(s.Velocity,s.P.rb.velocity))));
            // Let newly constructed constraints settle for two solver steps;
            // this is a repair transaction, not permanent extra toughness.
            foreach(var p in assembly)foreach(var j in p.GetComponents<Joint>()){j.breakForce=float.PositiveInfinity;j.breakTorque=float.PositiveInfinity;}
            yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();
            foreach(var p in assembly.Take(1))CurrentAirframeDamagePlugin.Log.LogInfo("OWN_REPAIR_SETTLED "+p.GetComponent<NativePartitionPart>().Id+" "+string.Join(";",p.GetComponents<Joint>().Select(j=>"force="+j.currentForce.magnitude+" torque="+j.currentTorque.magnitude+" anchorDelta="+Vector3.Distance(p.transform.TransformPoint(j.anchor),j.connectedBody.transform.TransformPoint(j.connectedAnchor)))));
            foreach(var p in assembly)if(p){p.ApplyDamage(0,0,0,0);repairing.Remove(p);}
            RepairTransactions++;Refresh();CurrentAirframeDamagePlugin.Log.LogInfo("NATIVE_BRANCH_REPAIRED "+SourceKey+" members="+assembly.Count+" poseError="+LastRepairPoseError);
            }
        }
    }
}
