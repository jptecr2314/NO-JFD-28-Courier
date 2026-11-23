using System;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // 23/11/2026 (owner): the 35 mm turret uses the Courier's own container-style art (bundle builder
    // BuildGroundTestDefense, art tools/build_courier_turret_skyshield.py). The frozen family runtime paints only
    // "Turret Traverse/Armored Turret" and, when the Chimera mod is installed, swaps the whole 35 mm visual for the
    // Chimera's. For turrets built with the new art (they carry "Gun Elevation/Gun Cradle"):
    //  - the gun cradle receives the same livery block as the armour, with its own paint origin;
    //  - the Chimera skin is not applied to the 35 mm (decision T1), so the turret looks the same with or without it.
    // The 20 mm flank turrets keep the family behaviour unchanged.
    internal static class CourierCiwsArt
    {
        private const string Armor = "Turret Traverse/Armored Turret", Cradle = "Turret Traverse/Gun Elevation/Gun Cradle";
        private const string ArmorShader = "Superheavy/Turret Armor";
        private static readonly int PaintOrigin = Shader.PropertyToID("_PaintOrigin");

        internal static void Install(Harmony h)
        {
            var defense = CurrentAirframeCompatibility.Family("SuperheavyGroundDefense");
            if (defense == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier CIWS art: family defense type not found"); return; }
            h.Patch(AccessTools.Method(defense, "ApplyLivery"), postfix: new HarmonyMethod(typeof(CourierCiwsArt), nameof(AfterLivery)));
            h.Patch(AccessTools.Method(defense, "ApplySkin"), prefix: new HarmonyMethod(typeof(CourierCiwsArt), nameof(BeforeSkin)));
        }

        private static bool OwnArt(Component defense) => defense != null && defense.transform.Find(Cradle) != null;

        private static bool BeforeSkin(Component __instance, ref bool __result)
        {
            if (!OwnArt(__instance)) return true;
            __result = true; // the family stops looking for the optional skin; the bundled art stays
            return false;
        }

        private static void AfterLivery(Component __instance)
        {
            try
            {
                if (!OwnArt(__instance)) return;
                var armor = __instance.transform.Find(Armor)?.GetComponent<MeshRenderer>();
                var cradle = __instance.transform.Find(Cradle)?.GetComponent<MeshRenderer>();
                if (armor == null || cradle == null) return;
                int source = Array.FindIndex(armor.sharedMaterials, m => m != null && m.shader.name == ArmorShader);
                if (source < 0) return;
                var block = new MaterialPropertyBlock();
                armor.GetPropertyBlock(block, source);
                Vector3 origin = __instance.transform.InverseTransformPoint(cradle.transform.position);
                var materials = cradle.sharedMaterials;
                bool changed = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (materials[slot] == null || materials[slot].shader.name != ArmorShader) continue;
                    if (materials[slot] != armor.sharedMaterials[source]) { materials[slot] = armor.sharedMaterials[source]; changed = true; }
                    if (block.isEmpty) { cradle.SetPropertyBlock(null, slot); continue; }
                    var own = new MaterialPropertyBlock();
                    armor.GetPropertyBlock(own, source);
                    Vector4 o = own.GetVector(PaintOrigin);
                    own.SetVector(PaintOrigin, new Vector4(origin.x, origin.y, origin.z, o.w));
                    cradle.SetPropertyBlock(own, slot);
                }
                if (changed) cradle.sharedMaterials = materials;
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier CIWS cradle paint: " + e.Message); }
        }
    }
}
