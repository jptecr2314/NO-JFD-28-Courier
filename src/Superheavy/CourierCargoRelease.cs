using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // JFD-28 Courier cargo on destruction (owner report on 0.1.7, 23/11/2026: the cargo does not come off when the
    // aircraft is destroyed). Native MountedCargo releases its load only when the part holding its hardpoint
    // detaches (MountedCargo_OnPartDetached: hides the mounted model and, on the server, spawns the cargo unit at
    // its current pose and point velocity as a destroyed vehicle). The Courier's cargo stations sit on the core
    // fuselage, which never detaches, so a destroyed Courier kept its cargo inside the wreck. When a Courier is
    // disabled, the same native release runs for every cargo still mounted; the native code keeps its own
    // server/client split. Cargo already dropped or released is skipped. Other aircraft are untouched.
    internal static class CourierCargoRelease
    {
        private static readonly AccessTools.FieldRef<MountedCargo, bool> Detached = AccessTools.FieldRefAccess<MountedCargo, bool>("detached");
        private static readonly AccessTools.FieldRef<MountedCargo, bool> Fired = AccessTools.FieldRefAccess<MountedCargo, bool>("fired");
        private static readonly AccessTools.FieldRef<MountedCargo, UnitPart> Part = AccessTools.FieldRefAccess<MountedCargo, UnitPart>("attachedPart");
        private static readonly Action<MountedCargo, UnitPart> Release =
            AccessTools.MethodDelegate<Action<MountedCargo, UnitPart>>(AccessTools.Method(typeof(MountedCargo), "MountedCargo_OnPartDetached"));

        internal static void Install(Harmony h)
        {
            var disabled = AccessTools.Method(typeof(Aircraft), "UnitDisabled", new[] {typeof(bool), typeof(bool)});
            if (disabled == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cargo release: Aircraft.UnitDisabled not found."); return; }
            h.Patch(disabled, postfix: new HarmonyMethod(typeof(CourierCargoRelease), nameof(AfterDisabled)));
        }

        private static void AfterDisabled(Aircraft __instance)
        {
            try
            {
                if (__instance == null || __instance.definition == null || __instance.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                var cargo = new HashSet<MountedCargo>(__instance.GetComponentsInChildren<MountedCargo>(true));
                if (__instance.partLookup != null)
                    foreach (var part in __instance.partLookup)
                        if (part != null) cargo.UnionWith(part.GetComponentsInChildren<MountedCargo>(true));
                int released = 0;
                foreach (var item in cargo.Where(c => c != null && c.gameObject.activeSelf && !Fired(c) && !Detached(c)))
                {
                    var part = Part(item);
                    if (part == null || part.rb == null) continue;
                    Release(item, part);
                    released++;
                }
                if (released > 0)
                    CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cargo release: {released} mounted cargo released from the destroyed aircraft.");
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cargo release: " + e.Message); }
        }
    }
}
