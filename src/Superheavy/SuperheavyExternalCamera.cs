using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyExternalCamera
    {
        private static readonly FieldInfo CameraPivot = AccessTools.Field(typeof(CameraStateManager), nameof(CameraStateManager.cameraPivot));
        private static readonly FieldInfo FollowBody = AccessTools.Field(typeof(CameraOrbitState), "follow1");
        private static readonly FieldInfo TransitionBody = AccessTools.Field(typeof(CameraOrbitState), "follow2");
        private static readonly MethodInfo TransformGetter = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo Zero = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.zero));
        private static readonly MethodInfo WorldPosition = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
        private static readonly MethodInfo SetLocalPosition = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localPosition));
        private static readonly MethodInfo LocalCenter = AccessTools.Method(typeof(SuperheavyExternalCamera), nameof(GetLocalCenter));
        private static readonly MethodInfo WorldCenter = AccessTools.Method(typeof(SuperheavyExternalCamera), nameof(GetWorldCenter));
        private static Aircraft cachedAircraft;
        private static Vector3 cachedCenter;
        private static ManualLogSource log;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(ExternalPivotPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ChaseEntryPatch)).Patch();
        }

        private static bool IsFollowedCourier(Transform body, CameraStateManager camera)
        {
            return camera.followingUnit is Aircraft aircraft && aircraft != null && aircraft.definition != null &&
                SuperheavyGroundPhysicsPlugin.IsFamily(aircraft.definition) && body == aircraft.transform;
        }

        private static Vector3 GetAirframeCenter(Aircraft aircraft)
        {
            if (aircraft != cachedAircraft)
            {
                cachedAircraft = aircraft;
                // The FBX root is at runway height. Use the fixed shell and leave moving gear out of the frame.
                var hull = aircraft.transform.Find("SHGT_OriginalVisuals/SHGT_Fuselage");
                var mesh = hull == null ? null : hull.GetComponent<MeshFilter>()?.sharedMesh;
                cachedCenter = mesh == null ? new Vector3(0f, 7.3f, 0f) :
                    aircraft.transform.InverseTransformPoint(hull.TransformPoint(mesh.bounds.center));
            }

            return cachedCenter;
        }

        private static Vector3 GetLocalCenter(Transform pivot, CameraStateManager camera)
        {
            return IsFollowedCourier(pivot.parent, camera) ? GetAirframeCenter((Aircraft)camera.followingUnit) : Vector3.zero;
        }

        private static Vector3 GetWorldCenter(Transform body, CameraStateManager camera)
        {
            return IsFollowedCourier(body, camera) ? body.TransformPoint(GetAirframeCenter((Aircraft)camera.followingUnit)) : body.position;
        }

        [HarmonyPatch]
        private static class ExternalPivotPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(CameraOrbitState), nameof(CameraOrbitState.EnterState));
                yield return AccessTools.Method(typeof(CameraOrbitState), "CameraMotion");
                yield return AccessTools.Method(typeof(CameraChaseState), nameof(CameraChaseState.EnterState));
                yield return AccessTools.Method(typeof(CameraChaseState), nameof(CameraChaseState.UpdateState));
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                var code = instructions.ToList();
                bool orbitMotion = original.Name == "CameraMotion";
                bool chaseUpdate = original.DeclaringType == typeof(CameraChaseState) && original.Name == nameof(CameraChaseState.UpdateState);
                var pivotResets = new List<int>();
                var bodyPositions = new List<int>();

                for (int i = 3; i < code.Count - 1; i++)
                {
                    if (code[i].Calls(Zero) && code[i + 1].Calls(SetLocalPosition) &&
                        (Equals(code[i - 1].operand, CameraPivot) ||
                         code[i - 1].Calls(TransformGetter) && Equals(code[i - 2].operand, CameraPivot)))
                        pivotResets.Add(i);

                    if (code[i].Calls(WorldPosition) && code[i - 1].Calls(TransformGetter) &&
                        (orbitMotion && (Equals(code[i - 2].operand, FollowBody) || Equals(code[i - 2].operand, TransitionBody)) ||
                         chaseUpdate && Equals(code[i - 3].operand, CameraPivot)))
                        bodyPositions.Add(i);
                }

                if (pivotResets.Count != (orbitMotion ? 1 : 2) || bodyPositions.Count != (orbitMotion ? 6 : chaseUpdate ? 1 : 0))
                {
                    log.LogWarning($"Courier external camera could not locate the native pivot in {original.DeclaringType.Name}.{original.Name}.");
                    return code;
                }

                // Keep the native smoothing, view controls, and collision casts around the new pivot.
                foreach (int i in pivotResets.Concat(bodyPositions).OrderByDescending(i => i))
                {
                    bool isPivot = pivotResets.Contains(i);
                    int insertAt = i + 1;
                    code[i].opcode = isPivot ? OpCodes.Dup : OpCodes.Ldarg_1;
                    code[i].operand = null;
                    if (isPivot)
                        code.Insert(insertAt++, new CodeInstruction(OpCodes.Ldarg_1));
                    code.Insert(insertAt, new CodeInstruction(OpCodes.Call, isPivot ? LocalCenter : WorldCenter));
                }

                return code;
            }
        }

        [HarmonyPatch(typeof(CameraChaseState), nameof(CameraChaseState.EnterState))]
        private static class ChaseEntryPatch
        {
            private static void Postfix(CameraStateManager __0, float ___orbitDist, ref Vector3 ___posVector, ref Vector3 ___targetVector)
            {
                if (__0.cameraPivot == null || !IsFollowedCourier(__0.cameraPivot.parent, __0))
                    return;

                // The native custom-aircraft fallback feeds world heading into these body-local vectors.
                ___posVector = new Vector3(0f, .1f * ___orbitDist, -___orbitDist);
                ___targetVector = new Vector3(0f, -.1f, 1f);
            }
        }
    }
}
