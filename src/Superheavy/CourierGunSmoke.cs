using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner request for 0.1.8 (23/11/2026): the 20 mm flank guns fire with no fire or smoke at the muzzle. Their prefab
    // (SuperheavyCIWS20, "Sky Sentry 20 mm CIWS") has no muzzle particles at all, while the Courier's own 35 mm CIWS has
    // a muzzle flash and a muzzle smoke that the native gun plays on every round (Gun.SpawnBullet plays every entry of
    // Gun.muzzleParticles). When a 20 mm gun is attached to a Courier, copies of the 35 mm flash and smoke, at 85 % of
    // their size, are placed on each of its muzzles and handed to the native gun. Only the Courier's own assets are used.
    internal static class CourierGunSmoke
    {
        private const float Scale = .85f;
        private static readonly AccessTools.FieldRef<Gun, ParticleSystem[]> Particles = AccessTools.FieldRefAccess<Gun, ParticleSystem[]>("muzzleParticles");
        private static readonly AccessTools.FieldRef<Gun, Transform[]> Muzzles = AccessTools.FieldRefAccess<Gun, Transform[]>("muzzles");
        private static Gun donor;

        internal static void Install(Harmony h)
        {
            var attach = AccessTools.Method(typeof(Gun), "AttachToUnit", new[] { typeof(Unit) });
            if (attach == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier 20 mm muzzle effects: Gun.AttachToUnit not found."); return; }
            h.Patch(attach, postfix: new HarmonyMethod(typeof(CourierGunSmoke), nameof(AfterAttach)));
        }

        private static void AfterAttach(Gun __instance, Unit __0)
        {
            try
            {
                if (!(__0 is Aircraft a) || a.definition == null || a.definition.jsonKey != "jp_superheavy_ground_test") return;
                var name = __instance.info != null ? __instance.info.weaponName : null;
                if (name == null || !name.Contains("20 mm")) return;
                var existing = Particles(__instance);
                if (existing != null && existing.Any(p => p != null)) return;
                var source = Donor();
                if (source == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier 20 mm muzzle effects: 35 mm CIWS effects not found."); return; }
                var muzzles = Muzzles(__instance);
                if (muzzles == null || muzzles.Length == 0) muzzles = new[] { __instance.transform };
                var made = new System.Collections.Generic.List<ParticleSystem>();
                foreach (var muzzle in muzzles.Where(m => m != null))
                    foreach (var fx in Particles(source).Where(p => p != null))
                    {
                        var copy = UnityEngine.Object.Instantiate(fx.gameObject, muzzle, false);
                        copy.name = "Courier20 " + fx.name;
                        copy.transform.localPosition = fx.transform.localPosition * Scale;
                        copy.transform.localRotation = fx.transform.localRotation;
                        copy.transform.localScale = fx.transform.localScale * Scale;
                        foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            var main = ps.main;
                            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                            main.playOnAwake = false;
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        }
                        made.Add(copy.GetComponent<ParticleSystem>());
                    }
                Particles(__instance) = made.Where(p => p != null).ToArray();
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier 20 mm muzzle effects: " + e.Message); }
        }

        // The Courier's 35 mm CIWS gun (its prefab, from the weapon catalogue).
        private static Gun Donor()
        {
            if (donor != null) return donor;
            Gun Pick(Gun g) => g != null && g.info != null && g.info.weaponName != null && g.info.weaponName.Contains("35 mm JFD-28") &&
                Particles(g) != null && Particles(g).Any(p => p != null) ? g : null;
            if (Encyclopedia.WeaponLookup != null)
                foreach (var mount in Encyclopedia.WeaponLookup.Values)
                {
                    if (mount == null || mount.prefab == null) continue;
                    foreach (var g in mount.prefab.GetComponentsInChildren<Gun>(true))
                        if (Pick(g) != null) return donor = g;
                }
            return donor = Resources.FindObjectsOfTypeAll<Gun>().FirstOrDefault(g => Pick(g) != null);
        }
    }
}
