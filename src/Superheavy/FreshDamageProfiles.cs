using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Data in this file is owned by the new model; no legacy AeroPart is an input.
    [Serializable] public sealed class FreshArmor
    {
        public float pierceArmor,pierceTolerance,blastArmor,blastTolerance,fireArmor,fireTolerance,overpressureLimit;
        internal ArmorProperties Native(float scale)=>new ArmorProperties{pierceArmor=pierceArmor,pierceTolerance=pierceTolerance*scale,blastArmor=blastArmor,blastTolerance=blastTolerance*scale,fireArmor=fireArmor,fireTolerance=fireTolerance*scale,overpressureLimit=overpressureLimit};
    }
    [Serializable] public sealed class FreshPartProfile
    {
        public string id,category,material,anchorSource;
        public float massKg,structuralThreshold,integrityThreshold,impactThreshold,impactMultiplier,jointForceN,jointTorqueNm;
        public float engineDamageThreshold,engineCriticalThreshold;
        public float tearMinRadiusM,tearMaxRadiusM,tearEnergyScale,tearStretch,tearDepthM,lipWidthM,seamRaggednessM;
        public bool critical;public int rimSides,supportSamples;public float[] anchor,seamNormal;public FreshArmor armor;
        public float[] authoredInertiaKgM2;
        internal void Initialize(AeroPart part,Aircraft aircraft)
        {
            NativePartitionIntegration.Set(part,"id",(byte)0);NativePartitionIntegration.Set(part,"parentUnit",aircraft);
            NativePartitionIntegration.Set(part,"mass",massKg);NativePartitionIntegration.Set(part,"baseMass",massKg);NativePartitionIntegration.Set(part,"rb",null);
            NativePartitionIntegration.Set(part,"hitPoints",100f);NativePartitionIntegration.Set(part,"criticalPart",critical);
            NativePartitionIntegration.Set(part,"armorProperties",armor.Native(1));NativePartitionIntegration.Set(part,"structuralThreshold",structuralThreshold);NativePartitionIntegration.Set(part,"integrityThreshold",integrityThreshold);
            NativePartitionIntegration.Set(part,"impactDamage",new ImpactDamage{threshold=impactThreshold,multiplier=impactMultiplier});
            NativePartitionIntegration.Set(part,"hitSound",null);NativePartitionIntegration.Set(part,"damageMaterial",new DamageMaterial());
            NativePartitionIntegration.Set(part,"damageEffects",Activator.CreateInstance(HarmonyLib.AccessTools.Field(typeof(UnitPart),"damageEffects").FieldType));
            NativePartitionIntegration.Set(part,"disintegrationEffects",new GameObject[0]);NativePartitionIntegration.Set(part,"disintegrateObjects",new GameObject[0]);
            NativePartitionIntegration.Set(part,"fragmented",false);NativePartitionIntegration.Set(part,"detachedFromUnit",false);
        }
    }
    internal sealed class FreshProfileSet
    {
        public int schema;public string recipe,aircraftKey,sourceSha256,geometryBinarySha256;public float dryMassBudgetKg;public FreshPartProfile[] profiles;
        public int courierRefitVersion;public string inertiaRecipe;
        internal Dictionary<string,FreshPartProfile> ById;
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        internal static FreshProfileSet Load(string file,NativePartitionIntegration.Manifest geometry)
        {
            return LoadText(File.ReadAllText(file),geometry);
        }
        internal static FreshProfileSet LoadText(string json,NativePartitionIntegration.Manifest geometry)
        {
            var s=JsonConvert.DeserializeObject<FreshProfileSet>(json);
            if(s.schema!=1||s.recipe!="fresh-structure-owned-v1"||s.aircraftKey!=geometry.key||s.sourceSha256!=geometry.sourceSha256||s.geometryBinarySha256!=geometry.binarySha256||s.profiles.Length!=geometry.parts.Length)throw new InvalidDataException("Owned profile provenance/coverage mismatch.");
            s.ById=s.profiles.ToDictionary(p=>p.id,StringComparer.Ordinal);
            if(s.courierRefitVersion!=0&&(s.aircraftKey!="jp_superheavy_ground_test"||s.courierRefitVersion!=1||s.inertiaRecipe!="owned-section-box-v1"||s.profiles.Any(p=>p.authoredInertiaKgM2==null||p.authoredInertiaKgM2.Length!=3||p.authoredInertiaKgM2.Any(v=>!Finite(v)||v<=0))))throw new InvalidDataException("Invalid Courier refit mass properties.");
            if(!geometry.parts.All(p=>s.ById.ContainsKey(p.id))||Math.Abs(s.profiles.Sum(p=>p.massKg)-s.dryMassBudgetKg)>.2)throw new InvalidDataException("Owned mass/profile coverage invalid.");
            foreach(var p in s.profiles)if(p.massKg<=0||p.integrityThreshold<=-1000||p.structuralThreshold<0||p.structuralThreshold>=100||p.armor==null||p.armor.pierceTolerance<=0||p.armor.blastTolerance<=0||p.armor.fireTolerance<=0||p.rimSides<5||p.rimSides>16)throw new InvalidDataException("Invalid independent damage profile: "+p.id);
            foreach(var p in s.profiles)
            {
                var values=typeof(FreshPartProfile).GetFields().Where(f=>f.FieldType==typeof(float)).Select(f=>(float)f.GetValue(p)).Concat(typeof(FreshArmor).GetFields().Select(f=>(float)f.GetValue(p.armor)));
                if(!values.All(Finite)||p.anchor==null||p.anchor.Length!=3||!p.anchor.All(Finite)||p.seamNormal==null||p.seamNormal.Length!=3||!p.seamNormal.All(Finite)||NativePartitionIntegration.V(p.seamNormal).sqrMagnitude<.9f||p.integrityThreshold>=p.structuralThreshold||p.tearMinRadiusM<=0||p.tearMaxRadiusM<p.tearMinRadiusM||p.tearDepthM<=0||p.tearStretch<=0||p.lipWidthM<=0||(p.id!="Core"&&(p.jointForceN<=0||p.jointTorqueNm<=0)))throw new InvalidDataException("Non-finite or inconsistent owned profile: "+p.id);
            }
            return s;
        }
    }
}
