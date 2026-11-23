using System;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyRetroBridge
    {
        private delegate bool IsRetroDelegate(MountedCargo cargo);
        private delegate bool PredictDelegate(MountedCargo cargo,Vector3 start,Vector3 velocity,
            out Vector3 point,out float impact,out bool safe,out float seconds);
        private delegate bool GuidedDelegate(MountedCargo cargo,Vector3 start,Vector3 velocity,Quaternion rotation,
            GlobalPosition target,Vector3 heading,out Vector3 point,out bool safe,out float seconds);
        private delegate bool ArmDelegate(GroundVehicle unit,MountedCargo cargo,GlobalPosition target,Vector3 heading);
        private static IsRetroDelegate isRetro;
        private static PredictDelegate predict;
        private static GuidedDelegate guided;
        private static ArmDelegate arm;
        private static void Refresh()
        {
            var type=AccessTools.TypeByName("Japensou.NuclearOption.Type12Retro.RetroKitApi");
            if(type==null)return;
            if(isRetro==null)isRetro=AccessTools.MethodDelegate<IsRetroDelegate>(AccessTools.Method(type,"IsRetroCargo"));
            if(predict==null)predict=AccessTools.MethodDelegate<PredictDelegate>(AccessTools.Method(type,"Predict"));
            var method=AccessTools.Method(type,"PredictGuided");
            if(method!=null && guided==null)guided=AccessTools.MethodDelegate<GuidedDelegate>(method);
            method=AccessTools.Method(type,"ArmGuided");
            if(method!=null && arm==null)arm=AccessTools.MethodDelegate<ArmDelegate>(method);
        }
        internal static bool IsRetro(MountedCargo cargo){Refresh();return isRetro!=null&&isRetro(cargo);}
        internal static bool Ready {get{Refresh();return guided!=null&&arm!=null;}}
        internal static bool Natural(MountedCargo cargo,Vector3 start,Vector3 velocity,out Vector3 point)
        {
            point=start;Refresh();
            return predict!=null&&predict(cargo,start,velocity,out point,out _,out _,out _);
        }
        internal static bool Reachable(MountedCargo cargo,Vector3 start,Vector3 velocity,Quaternion rotation,
            GlobalPosition target,Vector3 heading)
        {
            Refresh();return guided!=null&&guided(cargo,start,velocity,rotation,target,heading,out var point,out bool safe,out _)&&safe&&
                SuperheavyAirdropZone.ValidateTouchdown(point.ToGlobalPosition(),heading,
                    Mathf.Max(1.1f,cargo.cargo.length*.5f),Mathf.Max(.7f,cargo.cargo.width*.5f),out _);
        }
        internal static bool Arm(GroundVehicle unit,MountedCargo cargo,GlobalPosition target,Vector3 heading)
        {Refresh();return arm!=null&&arm(unit,cargo,target,heading);}
    }
}
