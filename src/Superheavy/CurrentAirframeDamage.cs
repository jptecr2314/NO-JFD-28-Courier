using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
#if COURIER_BALANCED_ONLY
    public enum CourierQuality {Balanced}
    [BepInPlugin("br.japensou.nuclearoption.currentdamage", "JFD-28 Courier Structural Damage", "0.9.0")]
#else
    public enum CourierQuality {Performance,Balanced,Detailed}
    [BepInPlugin("br.japensou.nuclearoption.currentdamage", "Courier / Bastion Own Structural Damage", "0.7.1")]
#endif
    [BepInDependency("br.japensou.nuclearoption.superheavy.groundphysics")]
    public sealed class CurrentAirframeDamagePlugin:BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
#if COURIER_BALANCED_ONLY
        internal static CourierQuality SelectedQuality=>CourierQuality.Balanced;
#else
        internal static ConfigEntry<CourierQuality> Quality;
        internal static CourierQuality SelectedQuality=>Quality.Value;
#endif
        internal static ConfigEntry<bool> DetailedFailureDiagnostics;
        internal static ConfigEntry<float> BodyResistance,WingResistance,TailResistance,EngineResistance,JointStrength,Distance;
        internal sealed class PaintAsset {internal Material Painted,Plain;}
        internal static readonly Dictionary<string,PaintAsset> Assets=new Dictionary<string,PaintAsset>();
        private void Awake()
        {
            Log=Logger;
#if !COURIER_BALANCED_ONLY
            Quality=Config.Bind("Courier presentation","Quality preset",CourierQuality.Balanced,"Courier visual detail and damage-work budget. Performance, Balanced and Detailed use identical native damage, joints, fuel and physical debris. Changes apply to existing aircraft. Detailed retains the original close-up interior mesh.");
            Quality.SettingChanged+=(s,e)=>CourierPresentation.ApplyToAll();
#endif
            DetailedFailureDiagnostics=Config.Bind("Diagnostics","Detailed first failure",false,"Write a full JSON snapshot of the first structural failure. Diagnostic only; synchronous reporting can add first-impact latency. Leave off during normal gameplay.");
            Enabled=Config.Bind("Integration","Enabled",true,"Enable the new independent structural damage model in single player and the mission editor. When disabled these aircraft cannot spawn through this candidate; no old damage fallback is used.");
            var range=new AcceptableValueRange<float>(.25f,4f);
            BodyResistance=Config.Bind("Native resistance","Body tolerance scale",1f,new ConfigDescription("Multiplier on the new owned body profiles, not the old aircraft damage values.",range));
            WingResistance=Config.Bind("Native resistance","Wing tolerance scale",1f,new ConfigDescription("Multiplier on the new owned wing/control profiles.",range));
            TailResistance=Config.Bind("Native resistance","Tail tolerance scale",1f,new ConfigDescription("Multiplier on the new owned tail profiles.",range));
            EngineResistance=Config.Bind("Native resistance","Engine tolerance scale",1f,new ConfigDescription("Multiplier on the new owned engine-module profiles.",range));
            JointStrength=Config.Bind("Native resistance","Joint strength scale",1f,new ConfigDescription("Multiplier on new geometry/role-based parent-joint budgets. These are provisional gameplay values.",range));
            Distance=Config.Bind("Rendering","Maximum frame distance",800f,new ConfigDescription("Damage-frame rendering distance in metres.",new AcceptableValueRange<float>(100,10000)));
            foreach(var entry in new[]{BodyResistance,WingResistance,TailResistance,EngineResistance,JointStrength})entry.SettingChanged+=(s,e)=>{foreach(var c in UnityEngine.Object.FindObjectsOfType<NativePartitionController>())if(c.Ready)c.ApplyConfiguredResistance();};
            // This guard survives feature-setup failures and never substitutes the old aircraft.
            NativePartitionIntegration.InstallSpawnGuard(new Harmony(Info.Metadata.GUID+".spawnGuard"));
            var harmony=new Harmony(Info.Metadata.GUID);
            try
            {
                if(Hash(typeof(UnitPart).Assembly.Location)!=CurrentDamageContract.GameAssemblySha256||!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("br.japensou.nuclearoption.superheavy.groundphysics",out var family)||Hash(family.Location)!=CurrentDamageContract.FamilyRuntimeSha256)throw new InvalidOperationException("Game/interface runtime differs from this candidate's pinned ABI.");
                NativePartitionIntegration.Initialize(harmony);FreshStructureDamage.Install(harmony);
                NativePartitionIntegration.IntegrationReady=true;
                Log.LogInfo("Own structural damage ready: explicit fresh-structure profiles; coherent native repair transactions; no old skeleton, damage thresholds, effects or 23-part fallback.");
            }
            catch(Exception e){NativePartitionIntegration.IntegrationReady=false;harmony.UnpatchSelf();Log.LogError("Own structural damage setup failed. Converted aircraft spawning is blocked; no legacy fallback: "+e);}
        }
        internal static string Hash(string path){using(var h=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
    }
}

