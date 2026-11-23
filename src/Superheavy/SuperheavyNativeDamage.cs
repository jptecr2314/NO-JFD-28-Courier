using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    // Native complex physics unparents every AeroPart. Keep an instance-local
    // index, so visual/weapon code can still find the detached scene hierarchy.
    internal sealed class SuperheavyNativeDamage : MonoBehaviour
    {
        private Aircraft aircraft;
        private Component[] authored;
        private readonly List<CollisionPair> ignored = new List<CollisionPair>();
        private float nextPoll;
        private Rigidbody[] completedBodies;
        private Collider[] completedContacts;
        private static readonly AccessTools.FieldRef<WeaponManager,Aircraft> ManagerAircraft =
            AccessTools.FieldRefAccess<WeaponManager,Aircraft>("aircraft");
        // Native complex physics unparents part roots. Ancestry remains a
        // template fallback; live ownership comes from native unit references.
        internal static Aircraft Owner(Component component)
        {
            if(component==null)return null;
            if(component is Aircraft aircraft)return aircraft;
            if(component is WeaponManager manager && ManagerAircraft(manager)!=null)
                return ManagerAircraft(manager);
            return component.GetComponentInParent<UnitPart>(true)?.parentUnit as Aircraft
                ?? component.GetComponentInParent<Aircraft>(true);
        }
        private struct CollisionPair { internal Collider A,B; internal UnitPart PartA,PartB; }
        private sealed class RepairState
        {
            internal Vector3 Position,Velocity,AngularVelocity;
            internal Quaternion Rotation;
            internal bool Kinematic,DetectCollisions,Fragmented;
            internal CollisionDetectionMode CollisionMode;
        }
        private struct TemporaryCollisionPair {internal Collider A,B;internal bool Ignored;}
        internal static void Install(Harmony harmony)
        {
#if !BAKED_AIRFRAMES
            harmony.CreateClassProcessor(typeof(RepairPatch)).Patch();
#endif
            harmony.CreateClassProcessor(typeof(RootStatusBindingPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ContactsAfterWeaponsPatch)).Patch();
        }
        [HarmonyPatch(typeof(WeaponManager),nameof(WeaponManager.SpawnWeapons))]
        private static class ContactsAfterWeaponsPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(WeaponManager __instance)
            {
                var owner=Owner(__instance);
                if(owner!=null&&owner.networked&&owner.LocalSim&&HasComplex(owner)&&
                    SuperheavyGroundPhysicsPlugin.IsFamily(owner.definition))Complete(owner,null);
            }
        }
        // Prefab saving and native spawning may rename the root GameObject.
        // Its HUD mask keeps the stable definition key; the remaining regions
        // retain the native name-based lookup without any global substitution.
        [HarmonyPatch]
        private static class RootStatusBindingPatch
        {
            private static System.Reflection.MethodBase TargetMethod()=>
                AccessTools.Method(typeof(StatusDisplay).Assembly.GetType("PartStatusDisplay",true),"FindPart");
            private static bool Prefix(List<UnitPart> __0,UnityEngine.UI.Image ___partImage,ref UnitPart __result)
            {
                if(__0==null||___partImage==null)return true;
                foreach(var part in __0)
                    if(part!=null&&part.parentUnit is Aircraft owner&&part.gameObject==owner.gameObject&&
                        SuperheavyGroundPhysicsPlugin.IsFamily(owner.definition)&&___partImage.name==owner.definition.jsonKey)
                    {__result=part;return false;}
                return true;
            }
        }
#if !BAKED_AIRFRAMES
        [HarmonyPatch(typeof(AeroPart),nameof(AeroPart.Repair))]
        private static class RepairPatch
        {
            private static void Prefix(AeroPart __instance,out RepairState __state)
            {
                __state=null;
                if(__instance==null||!(__instance.parentUnit is Aircraft aircraft)||
                    !SuperheavyGroundPhysicsPlugin.IsFamily(aircraft.definition)||!HasComplex(aircraft)||
                    !aircraft.networked||!aircraft.LocalSim||!__instance.IsDetached()||__instance.rb==null)return;
                var body=__instance.rb;
                __state=new RepairState{Position=body.position,Rotation=body.rotation,Velocity=body.velocity,AngularVelocity=body.angularVelocity,
                    Kinematic=body.isKinematic,DetectCollisions=body.detectCollisions,CollisionMode=body.collisionDetectionMode,
                    Fragmented=(bool)AccessTools.Field(typeof(UnitPart),"fragmented").GetValue(__instance)};
            }
            private static void Postfix(AeroPart __instance,RepairState __state)
            {
                if(__state==null||__instance==null||!(__instance.parentUnit is Aircraft aircraft))return;
                if(aircraft.TryGetComponent<SuperheavyNativeDamage>(out var index))index.StartCoroutine(index.Rejoin(__instance,__state));
            }
        }
        private IEnumerator Rejoin(AeroPart part,RepairState previous)
        {
            // Native AeroPart.Repair restores HP/rest pose but not destroyed
            // joints. Never allow a physics step between that teleport and
            // restoring host collision filtering, COM velocity and constraints.
            object info=AccessTools.Field(typeof(UnitPart),"attachInfo").GetValue(part);
            if(info==null)yield break;
            var parent=(UnitPart)AccessTools.Field(info.GetType(),"parentPart").GetValue(info);
            if(parent==null||parent.IsDetached()||parent.rb==null)
            {
                RestoreDebris(part,info,previous);
                Debug.LogWarning("Courier native repair deferred: "+part.name+" has no intact physical parent; no parent was healed.");
                yield break;
            }
            var body=part.rb;
            var temporary=IgnoreHostCollisions(part);
            body.detectCollisions=false;
            var oldJoints=part.GetComponents<Joint>();
            Debug.Log($"Courier native repair begin: part={part.name}, parent={parent.name}, oldJoints={oldJoints.Length}, hostPairs={temporary.Count}.");
            if(oldJoints.Length>0)
            {
                // Only a still-pending Unity constraint destruction needs a
                // frame boundary. Freeze the repaired body during that wait.
                body.collisionDetectionMode=CollisionDetectionMode.Discrete;
                body.isKinematic=true;
                AccessTools.Field(typeof(UnitPart),"detachedFromUnit").SetValue(part,true);
                foreach(var joint in oldJoints)Destroy(joint);
                yield return null;
            }
            if(part==null||body==null||parent==null||parent.rb==null||parent.IsDetached())
            {
                if(part!=null&&body!=null)RestoreDebris(part,info,previous);
                RestoreTemporaryPairs(temporary);yield break;
            }
            // Re-evaluate native rest pose against the CURRENT moving parent,
            // not the location of its frame before a possible wait.
            var local=(Vector3)AccessTools.Field(info.GetType(),"localPosition").GetValue(info);
            var rotation=(Quaternion)AccessTools.Field(info.GetType(),"localRotation").GetValue(info);
            body.position=parent.transform.TransformPoint(local);
            body.rotation=parent.transform.rotation*rotation;
            part.transform.SetPositionAndRotation(body.position,body.rotation);
            body.isKinematic=previous.Kinematic;
            body.velocity=parent.rb.GetPointVelocity(body.worldCenterOfMass);
            body.angularVelocity=parent.rb.angularVelocity;
            body.drag=0;body.mass=part.GetMass();
            AccessTools.Field(info.GetType(),"detachedFromParentPart").SetValue(info,false);
            AccessTools.Field(typeof(UnitPart),"detachedFromUnit").SetValue(part,false);
            AccessTools.Field(typeof(UnitPart),"fragmented").SetValue(part,false);
            var callback=(Action<UnitPart>)Delegate.CreateDelegate(typeof(Action<UnitPart>),part,
                AccessTools.Method(part.GetType(),"UnitPart_OnParentDetached"));
            parent.onParentDetached-=callback;parent.onParentDetached+=callback;
            part.CreateJoints();part.ApplyDamage(0,0,0,0);
            body.isKinematic=previous.Kinematic;
            body.collisionDetectionMode=previous.CollisionMode;
            body.detectCollisions=previous.DetectCollisions;
            // Enabling the native physics actor may recreate its shapes and
            // clear pair filtering. Restore the final body state FIRST, then
            // synchronize poses and install the attached-part filters before
            // another solver step can observe the repaired overlapping seam.
            Physics.SyncTransforms();
            completedBodies=null;completedContacts=null;
            Complete(aircraft,null);
            body.WakeUp();
            Debug.Log($"Courier native repair restored: part={part.name}, parent={parent.name}, detached={part.IsDetached()}, joints={part.GetComponents<Joint>().Length}.");
        }
        private List<TemporaryCollisionPair> IgnoreHostCollisions(UnitPart part)
        {
            var result=new List<TemporaryCollisionPair>();
            var colliders=Components<Collider>(aircraft).Where(c=>c!=null&&c.enabled&&!c.isTrigger).ToArray();
            var own=colliders.Where(c=>c.GetComponentInParent<UnitPart>()==part).ToArray();
            foreach(var a in own)foreach(var b in colliders)
            {
                var other=b.GetComponentInParent<UnitPart>();
                if(other==null||other==part||other.parentUnit!=aircraft||other.IsDetached())continue;
                result.Add(new TemporaryCollisionPair{A=a,B=b,Ignored=Physics.GetIgnoreCollision(a,b)});
                Physics.IgnoreCollision(a,b,true);
            }
            return result;
        }
        private static void RestoreTemporaryPairs(IEnumerable<TemporaryCollisionPair> pairs)
        {foreach(var pair in pairs)if(pair.A!=null&&pair.B!=null)Physics.IgnoreCollision(pair.A,pair.B,pair.Ignored);}
        private static void RestoreDebris(AeroPart part,object info,RepairState state)
        {
            AccessTools.Field(info.GetType(),"detachedFromParentPart").SetValue(info,true);
            AccessTools.Field(typeof(UnitPart),"detachedFromUnit").SetValue(part,true);
            AccessTools.Field(typeof(UnitPart),"fragmented").SetValue(part,state.Fragmented);
            var body=part.rb;if(body==null)return;
            body.position=state.Position;body.rotation=state.Rotation;
            body.isKinematic=state.Kinematic;body.collisionDetectionMode=state.CollisionMode;
            body.detectCollisions=state.DetectCollisions;
            body.velocity=state.Velocity;body.angularVelocity=state.AngularVelocity;
        }

#endif
        internal static bool HasComplex(Aircraft aircraft) => aircraft != null && !aircraft.simplePhysics;
        internal static Rigidbody BodyFor(UnitPart part) => part != null && !part.IsDetached() ? part.rb : null;
        internal static Vector3 AttachedCenterOfMass(Aircraft aircraft)
        {
            if(aircraft==null)return Vector3.zero;
            Vector3 weighted=Vector3.zero;float mass=0;
            if(aircraft.partLookup!=null)foreach(var p in aircraft.partLookup)
            {
                if(p==null||p.IsDetached()||p.rb==null)continue;
                float m=p.GetMass();
                weighted+=p.rb.worldCenterOfMass*m;mass+=m;
            }
            return mass>0?aircraft.transform.InverseTransformPoint(weighted/mass):Vector3.zero;
        }
        internal static float AttachedMass(Aircraft aircraft)
        {
            if(aircraft==null)return 0f;
            if(aircraft.partLookup==null||aircraft.partLookup.Count==0)return aircraft.rb!=null?aircraft.rb.mass:0;
            float sum=0;foreach(var p in aircraft.partLookup)if(p!=null&&!p.IsDetached())sum+=p.GetMass();
            return Mathf.Max(1f,sum);
        }
        internal static T[] Components<T>(Aircraft aircraft,bool includeInactive=true) where T:Component
        {
            if(aircraft==null)return Array.Empty<T>();
            var result=new HashSet<T>();
            if(aircraft.TryGetComponent<SuperheavyNativeDamage>(out var index)&&index.authored!=null)
                foreach(var item in index.authored)if(item is T t && t != null &&(includeInactive||t.gameObject.activeInHierarchy))result.Add(t);
            foreach(var t in aircraft.GetComponentsInChildren<T>(includeInactive))result.Add(t);
            // Runtime-installed stores are not in the authored snapshot.
            if(aircraft.partLookup!=null)foreach(var p in aircraft.partLookup)
                if(p!=null)foreach(var t in p.GetComponentsInChildren<T>(includeInactive))result.Add(t);
            return result.ToArray();
        }
        internal static void Capture(Aircraft aircraft)
        {
            if(!aircraft.TryGetComponent<SuperheavyNativeDamage>(out var index))index=aircraft.gameObject.AddComponent<SuperheavyNativeDamage>();
            index.aircraft=aircraft;
            var previous=index.authored??Array.Empty<Component>();
            index.authored=previous.Concat(aircraft.GetComponentsInChildren<Component>(true)).Where(c=>c!=null).Distinct().ToArray();
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void Complete(Aircraft aircraft,ManualLogSource log)
        {
#if !BAKED_AIRFRAMES
            if(!aircraft.TryGetComponent<SuperheavyNativeDamage>(out var index))return;
            var currentBodies=aircraft.partLookup.Where(p=>p!=null&&p.rb!=null).Select(p=>p.rb).Distinct().OrderBy(b=>b.GetInstanceID()).ToArray();
            // Stores and moving bay armour can create colliders after CreateRB.
            // An unchanged body graph does not imply an unchanged contact set.
            var colliders=Components<Collider>(aircraft).Where(c=>c!=null&&c.enabled&&!c.isTrigger)
                .OrderBy(c=>c.GetInstanceID()).ToArray();
            if(index.completedBodies!=null&&index.completedBodies.SequenceEqual(currentBodies)&&
                index.completedContacts!=null&&index.completedContacts.SequenceEqual(colliders))return;
            index.completedBodies=currentBodies;
            index.completedContacts=colliders;
            foreach(var pair in index.ignored)if(pair.A!=null&&pair.B!=null)Physics.IgnoreCollision(pair.A,pair.B,false);
            index.ignored.Clear();
            int bodies=0,joints=0;
            foreach(var part in aircraft.partLookup)
            {
                if(part==null||part.rb==null)continue;
                bodies++;
                var authoredCenter=(Transform)HarmonyLib.AccessTools.Field(typeof(UnitPart),"centerOfMass").GetValue(part);
                if(authoredCenter!=null)part.rb.centerOfMass=part.rb.transform.InverseTransformPoint(authoredCenter.position);
                part.rb.ResetInertiaTensor();
                part.rb.solverIterations=16;part.rb.solverVelocityIterations=8;
                if(!part.rb.isKinematic)part.rb.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
                joints+=part.GetComponents<Joint>().Length;
            }
            // Adjacent boxes can overlap a few mm at an authored seam; they
            // cannot fight one another while structurally attached. Restore
            // collisions after native detachment, so debris is physical again.
            var contactOwners=colliders.ToDictionary(c=>c,c=>c.GetComponentInParent<UnitPart>());
            for(int a=0;a<colliders.Length;a++)for(int b=a+1;b<colliders.Length;b++)
            {
                var ca=colliders[a];var cb=colliders[b];
                var pa=contactOwners[ca];var pb=contactOwners[cb];
                if(pa==null||pb==null||pa==pb||pa.parentUnit!=aircraft||pb.parentUnit!=aircraft)continue;
                if(pa.IsDetached()||pb.IsDetached())continue;
                Physics.IgnoreCollision(ca,cb,true);
                index.ignored.Add(new CollisionPair{A=ca,B=cb,PartA=pa,PartB=pb});
            }
            log?.LogInfo($"Courier native complex physics: {bodies} part bodies, {joints} native joints, attached mass={AttachedMass(aircraft):F0} kg. Body counts are not an observed fracture test.");
        #endif
        }
#if !BAKED_AIRFRAMES
        private void LateUpdate()
        {
            if(Time.unscaledTime<nextPoll)return;nextPoll=Time.unscaledTime+.2f;
            for(int i=ignored.Count-1;i>=0;i--)
            {
                var p=ignored[i];if(p.A==null||p.B==null){ignored.RemoveAt(i);continue;}
                if(p.PartA==null||p.PartB==null||p.PartA.IsDetached()||p.PartB.IsDetached())
                {Physics.IgnoreCollision(p.A,p.B,false);ignored.RemoveAt(i);}
            }
        }
        private static bool Finite(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsNaN(v.y)&&!float.IsNaN(v.z)&&!float.IsInfinity(v.x)&&!float.IsInfinity(v.y)&&!float.IsInfinity(v.z);
#endif
    }
}
