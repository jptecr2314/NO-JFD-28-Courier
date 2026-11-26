using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner, 26/11/2026 (part of 0.2.2):
    //  1. "uns 4 segundos pra abrir" the bomb bay. The bundle's SHGT_BombBayGate has openSpeed 0.12 (native BayDoor
    //     opens at openSpeed per second: 8.3 s, measured in flight-cockpit-20261126-024318). Raised to 0.25 (4.0 s) on
    //     Awake, before any mount spawns, so the family rail delay (1/openSpeed + 0.1 s, read at SpawnMount) and the
    //     bomber feed, which waits for the native door phase, follow the new speed. Closing (1 s) is unchanged.
    //  2. "desliga as telas das câmeras que ficam acima do parabrisas", then "as câmeras dos flancos desliguem-se quando
    //     o avião recolher o trem de pouso, deixando só a do compartimento de cargas ligada". SHGT_MFD_PortCamera and
    //     SHGT_MFD_StarboardCamera hang at eye height in front of the windscreen top (y ~8.95, z ~27.8; pilot eye y 8.92,
    //     z 27.2) and show the flank/engine feeds. While the gear is up their feeds leave the frozen family's camera
    //     rotation (the cargo camera keeps the whole rotation) and the screens go dark; gear down puts them back.
    internal static class CourierBayAndScreens
    {
        internal const float BayOpenSeconds = 4f;
        private static readonly string[] OffScreens = { "PortCamera", "StarboardCamera" };
        private static readonly AccessTools.FieldRef<BayDoor, float> OpenSpeed = AccessTools.FieldRefAccess<BayDoor, float>("openSpeed");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(BayDoor), "Awake"), postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterDoorAwake)));
            var mfds = AccessTools.TypeByName("Japensou.NuclearOption.Superheavy.SuperheavyGroundMfds");
            var attach = mfds == null ? null : AccessTools.Method(mfds, "Attach");
            if (attach == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier screens: family MFD binding not found."); return; }
            h.Patch(attach, postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterMfdAttach)));
            h.Patch(AccessTools.Method(mfds, "LateUpdate"), postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterMfdLateUpdate)));
        }

        private static bool Courier(Component c)
        {
            var aircraft = c == null ? null : c.GetComponentInParent<Aircraft>(true);
            return aircraft != null && aircraft.definition != null && aircraft.definition.jsonKey == "jp_superheavy_ground_test";
        }

        private static void AfterDoorAwake(BayDoor __instance)
        {
            try
            {
                if (__instance == null || !__instance.name.StartsWith("SHGT_BombBayGate", StringComparison.Ordinal) || !Courier(__instance))
                    return;
                OpenSpeed(__instance) = Mathf.Max(OpenSpeed(__instance), 1f / BayOpenSeconds);
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier bay door speed: " + e.Message); }
        }

        // Per MFD component: the flank feeds taken out of the family rotation while the gear is up.
        private sealed class FlankState { internal readonly List<object> Parked = new List<object>(); internal bool Up, Powered, Logged; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Component, FlankState> States =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Component, FlankState>();
        private static Type mfdType;
        private static System.Reflection.FieldInfo displaysField, feedsField, poweredField, aircraftField, indexField, feedName, feedCamera, feedTexture;

        private static void Bind(Type t)
        {
            mfdType = t;
            displaysField = AccessTools.Field(t, "displays"); feedsField = AccessTools.Field(t, "feeds");
            poweredField = AccessTools.Field(t, "powered"); aircraftField = AccessTools.Field(t, "aircraft"); indexField = AccessTools.Field(t, "cameraIndex");
            var feed = AccessTools.Inner(t, "Feed");
            feedName = AccessTools.Field(feed, "Name"); feedCamera = AccessTools.Field(feed, "Camera"); feedTexture = AccessTools.Field(feed, "Texture");
        }

        private static void AfterMfdAttach(Aircraft __0)
        {
            // Owner, 26/11/2026 (second message): the flank cameras switch off when the gear retracts, leaving only the
            // cargo-compartment camera. They stay on with the gear down (taxi, ground loading).
            if (__0 != null && __0.definition != null && __0.definition.jsonKey == "jp_superheavy_ground_test")
                CurrentAirframeDamagePlugin.Log.LogInfo("Courier screens: flank cameras follow the landing gear (off when retracted).");
        }

        private static void Black(MeshRenderer screen)
        {
            if (screen == null) return;
            var block = new MaterialPropertyBlock();
            block.SetTexture(BaseMap, Texture2D.blackTexture);
            block.SetColor(BaseColor, new Color(.015f, .016f, .018f, 1f));
            block.SetColor(EmissionColor, Color.black);
            screen.SetPropertyBlock(block);
        }

        private static void Live(MeshRenderer screen, Texture texture)
        {
            if (screen == null) return;
            var block = new MaterialPropertyBlock();
            if (texture != null) { block.SetTexture(BaseMap, texture); block.SetColor(BaseColor, Color.white); }
            screen.SetPropertyBlock(block);
        }

        private static void AfterMfdLateUpdate(Component __instance)
        {
            try
            {
                if (__instance == null) return;
                if (mfdType != __instance.GetType()) Bind(__instance.GetType());
                var aircraft = aircraftField.GetValue(__instance) as Aircraft;
                if (aircraft == null || aircraft.definition == null || aircraft.definition.jsonKey != "jp_superheavy_ground_test")
                    return;
                var displays = displaysField.GetValue(__instance) as Dictionary<string, MeshRenderer>;
                var feeds = feedsField.GetValue(__instance) as System.Collections.IList;
                if (displays == null || feeds == null) return;
                var state = States.GetOrCreateValue(__instance);
                var gear = aircraft.gearState;
                bool up = gear == LandingGear.GearState.Retracting || gear == LandingGear.GearState.LockedRetracted ||
                          (gear == LandingGear.GearState.Uninitialized && !aircraft.gearDeployed);
                bool powered = (bool)poweredField.GetValue(__instance);

                if (up)
                {
                    // every frame: the family creates its feeds on the first powered frame, possibly after the gear is up
                    bool parked = false;
                    for (int i = feeds.Count - 1; i >= 0; i--)
                    {
                        var feed = feeds[i];
                        if (Array.IndexOf(OffScreens, (string)feedName.GetValue(feed)) < 0) continue;
                        if (feedCamera.GetValue(feed) is Camera cam) cam.enabled = false;
                        feeds.RemoveAt(i); state.Parked.Add(feed); parked = true;
                    }
                    if (parked || !state.Up || powered != state.Powered)
                        // the family repaints every screen when the power state changes
                        foreach (var name in OffScreens) if (displays.TryGetValue(name, out var s)) Black(s);
                    if (parked && !state.Logged) { CurrentAirframeDamagePlugin.Log.LogInfo($"Courier screens: gear up, {state.Parked.Count} flank cameras off."); state.Logged = true; }
                }
                else if (state.Up)
                {
                    foreach (var feed in state.Parked)
                    {
                        feeds.Add(feed);
                        if (displays.TryGetValue((string)feedName.GetValue(feed), out var s)) Live(s, powered ? feedTexture.GetValue(feed) as Texture : null);
                    }
                    state.Parked.Clear();
                }
                // the family indexes its rotation with cameraIndex; keep it inside the list it now has
                int index = (int)indexField.GetValue(__instance);
                if (index >= feeds.Count || index < 0) indexField.SetValue(__instance, 0);
                state.Up = up; state.Powered = powered;
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier screens: " + e.Message); }
        }
    }
}
