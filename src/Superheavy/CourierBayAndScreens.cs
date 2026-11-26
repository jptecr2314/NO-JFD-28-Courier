using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner, 26/11/2026:
    //  1. "uns 4 segundos pra abrir" the bomb bay. The bundle's SHGT_BombBayGate has openSpeed 0.12 (native BayDoor
    //     opens at openSpeed per second: 8.3 s, measured in flight-cockpit-20261126-024318). Raised to 0.25 (4.0 s) on
    //     Awake, before any mount spawns, so the family rail delay (1/openSpeed + 0.1 s, read at SpawnMount) and the
    //     bomber feed, which waits for the native door phase, follow the new speed. Closing (1 s) is unchanged.
    //  2. Flank camera screens (0.2.2, after 0.2.1 shipped): "Eu quero que essas duas telas [acima do para-brisa] fiquem
    //     permanentemente desligadas, as que eu estava falando dos flancos são aquelas que ficam perto da mesa central,
    //     apoiadas por duas molduras". SHGT_MFD_PortCamera and SHGT_MFD_StarboardCamera are each one renderer with two
    //     submeshes: a screen on the ceiling above the windscreen and a framed screen beside the instrument hood
    //     (flight-console-20261126-040408). The ceiling submesh is dark for good (per-material block); the framed one
    //     shows the flank feed with the gear down and goes dark with the gear up, when the flank feeds also leave the
    //     frozen family's camera rotation (the cargo camera keeps it).
    //  3. "arruma isso daqui tbm" (jagged strip at the top of the windscreen): the inward cockpit coating of the nose
    //     (Seam_12..14) has holes and sliver triangles there, so the outer skin, painted on both faces, shows through as
    //     white teeth. Moving the coating or drawing it double-sided changed nothing (flight-console-20261126-041113,
    //     -041207). A dark backing built from the nose skin triangles within 4 m of the pilot's eye, 4 cm inside the skin,
    //     with the coating material, closes it (flight-console-20261126-041409); the exterior is unchanged.
    internal static class CourierBayAndScreens
    {
        internal const float BayOpenSeconds = 4f;
        private static readonly string[] FlankScreens = { "PortCamera", "StarboardCamera" };
        private static readonly AccessTools.FieldRef<BayDoor, float> OpenSpeed = AccessTools.FieldRefAccess<BayDoor, float>("openSpeed");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly Vector3 PilotEye = new Vector3(0f, 8.92f, 27.2f); // aircraft-local, measured in flight-console-20261126-040408

        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(BayDoor), "Awake"), postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterDoorAwake)));
            var mfds = AccessTools.TypeByName("Japensou.NuclearOption.Superheavy.SuperheavyGroundMfds");
            var attach = mfds == null ? null : AccessTools.Method(mfds, "Attach");
            if (attach == null) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier screens: family MFD binding not found."); return; }
            h.Patch(attach, postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterMfdAttach)));
            h.Patch(AccessTools.Method(mfds, "LateUpdate"), postfix: new HarmonyMethod(typeof(CourierBayAndScreens), nameof(AfterMfdLateUpdate)));
        }

        private static bool IsCourier(Aircraft a) => a != null && a.definition != null && a.definition.jsonKey == "jp_superheavy_ground_test";

        private static bool Courier(Component c) => c != null && IsCourier(c.GetComponentInParent<Aircraft>(true));

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

        private static void AfterMfdAttach(Aircraft __0)
        {
            if (!IsCourier(__0)) return;
            CurrentAirframeDamagePlugin.Log.LogInfo("Courier screens: ceiling camera screens off; framed flank screens follow the landing gear.");
            try { CockpitBacking.Build(__0); }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier cockpit backing: " + e.Message); }
        }

        // Per MFD component: the flank feeds taken out of the family rotation while the gear is up, and which submesh of
        // each flank screen is the ceiling one.
        private sealed class FlankState
        {
            internal readonly List<object> Parked = new List<object>();
            internal readonly Dictionary<string, int> CeilingIndex = new Dictionary<string, int>();
            internal bool Up, Powered, Logged, Started;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Component, FlankState> States =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Component, FlankState>();
        private static Type mfdType;
        private static System.Reflection.FieldInfo displaysField, feedsField, poweredField, aircraftField, indexField, feedName, feedCamera, feedTexture;
        private static MaterialPropertyBlock dark;

        private static void Bind(Type t)
        {
            mfdType = t;
            displaysField = AccessTools.Field(t, "displays"); feedsField = AccessTools.Field(t, "feeds");
            poweredField = AccessTools.Field(t, "powered"); aircraftField = AccessTools.Field(t, "aircraft"); indexField = AccessTools.Field(t, "cameraIndex");
            var feed = AccessTools.Inner(t, "Feed");
            feedName = AccessTools.Field(feed, "Name"); feedCamera = AccessTools.Field(feed, "Camera"); feedTexture = AccessTools.Field(feed, "Texture");
        }

        private static MaterialPropertyBlock Dark()
        {
            if (dark != null) return dark;
            dark = new MaterialPropertyBlock();
            dark.SetTexture(BaseMap, Texture2D.blackTexture);
            dark.SetColor(BaseColor, new Color(.015f, .016f, .018f, 1f));
            dark.SetColor(EmissionColor, Color.black);
            return dark;
        }

        private static void Black(MeshRenderer screen) { if (screen != null) screen.SetPropertyBlock(Dark()); }

        private static void Live(MeshRenderer screen, Texture texture)
        {
            if (screen == null) return;
            var block = new MaterialPropertyBlock();
            if (texture != null) { block.SetTexture(BaseMap, texture); block.SetColor(BaseColor, Color.white); }
            screen.SetPropertyBlock(block);
        }

        // The submesh whose vertices sit highest in the aircraft is the ceiling screen; -1 when the mesh has one submesh.
        private static int FindCeiling(MeshRenderer screen, Transform root)
        {
            var mesh = screen.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || mesh.subMeshCount < 2 || !mesh.isReadable) return -1;
            var vertices = mesh.vertices; int best = -1; float bestY = float.MinValue;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndices(s); if (indices.Length == 0) continue;
                float y = 0f; foreach (var i in indices) y += root.InverseTransformPoint(screen.transform.TransformPoint(vertices[i])).y;
                y /= indices.Length;
                if (y > bestY) { bestY = y; best = s; }
            }
            return best;
        }

        private static void AfterMfdLateUpdate(Component __instance)
        {
            try
            {
                if (__instance == null) return;
                if (mfdType != __instance.GetType()) Bind(__instance.GetType());
                var aircraft = aircraftField.GetValue(__instance) as Aircraft;
                if (!IsCourier(aircraft)) return;
                var displays = displaysField.GetValue(__instance) as Dictionary<string, MeshRenderer>;
                var feeds = feedsField.GetValue(__instance) as System.Collections.IList;
                if (displays == null || feeds == null) return;
                var state = States.GetOrCreateValue(__instance);
                var gear = aircraft.gearState;
                bool up = gear == LandingGear.GearState.Retracting || gear == LandingGear.GearState.LockedRetracted ||
                          (gear == LandingGear.GearState.Uninitialized && !aircraft.gearDeployed);
                bool powered = (bool)poweredField.GetValue(__instance);

                // ceiling screens: dark for good; a per-material block survives the family's renderer-wide repaints
                foreach (var name in FlankScreens)
                {
                    if (!displays.TryGetValue(name, out var s) || s == null) continue;
                    if (!state.CeilingIndex.TryGetValue(name, out var ceiling))
                    {
                        state.CeilingIndex[name] = ceiling = FindCeiling(s, aircraft.transform);
                        CurrentAirframeDamagePlugin.Log.LogInfo($"Courier screens: {name} ceiling submesh {ceiling}.");
                    }
                    if (ceiling >= 0) s.SetPropertyBlock(Dark(), ceiling);
                }

                if (up)
                {
                    // every frame: the family creates its feeds on the first powered frame, possibly after the gear is up
                    bool parked = false;
                    for (int i = feeds.Count - 1; i >= 0; i--)
                    {
                        var feed = feeds[i];
                        if (Array.IndexOf(FlankScreens, (string)feedName.GetValue(feed)) < 0) continue;
                        if (feedCamera.GetValue(feed) is Camera cam) cam.enabled = false;
                        feeds.RemoveAt(i); state.Parked.Add(feed); parked = true;
                    }
                    if (parked || !state.Up || !state.Started || powered != state.Powered)
                        // the family repaints every screen when the power state changes
                        foreach (var name in FlankScreens) if (displays.TryGetValue(name, out var s)) Black(s);
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
                state.Up = up; state.Powered = powered; state.Started = true;
            }
            catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning("Courier screens: " + e.Message); }
        }

        // Dark backing behind the nose's inward cockpit coating (item 3 above).
        private static class CockpitBacking
        {
            private const float Reach = 4f, Inset = .04f;
            private static readonly Dictionary<Mesh, Mesh> Backings = new Dictionary<Mesh, Mesh>();
            private static readonly Dictionary<Mesh, Mesh> Coatings = new Dictionary<Mesh, Mesh>();
            private static Material material;

            internal static void Build(Aircraft aircraft)
            {
                var root = aircraft.transform;
                var renderers = new List<MeshRenderer>(aircraft.GetComponentsInChildren<MeshRenderer>(true));
                if (aircraft.partLookup != null)
                    foreach (var part in aircraft.partLookup) if (part != null) renderers.AddRange(part.GetComponentsInChildren<MeshRenderer>(true));
                MeshRenderer coating = null; var skins = new List<MeshRenderer>(); var coatings = new List<MeshRenderer>();
                foreach (var r in renderers)
                {
                    if (r == null || r.transform.parent == null || !r.transform.parent.name.StartsWith("NATIVE_Shell_Nose", StringComparison.Ordinal)) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        if (m.name.StartsWith("Owned inward cockpit coating", StringComparison.Ordinal)) { if (!coatings.Contains(r)) coatings.Add(r); coating = coating ?? r; }
                        else if (m.name.StartsWith("Owned livery paint", StringComparison.Ordinal) && !skins.Contains(r)) skins.Add(r);
                    }
                }
                if (coating == null || skins.Count == 0) { CurrentAirframeDamagePlugin.Log.LogWarning($"Courier cockpit backing: coating={coating != null} skins={skins.Count}; not built."); return; }
                if (material == null) { material = new Material(coating.sharedMaterial) { name = "Courier cockpit backing" }; material.SetFloat("_Cull", 0f); }

                int built = 0, triangles = 0;
                foreach (var skin in skins)
                {
                    if (skin.transform.Find("Courier cockpit backing") != null) continue;
                    var source = skin.GetComponent<MeshFilter>()?.sharedMesh;
                    if (source == null || !source.isReadable) continue;
                    if (!Backings.TryGetValue(source, out var mesh)) Backings[source] = mesh = Backing(source, skin.transform.InverseTransformPoint(root.TransformPoint(PilotEye)));
                    if (mesh == null) continue;
                    var go = new GameObject("Courier cockpit backing") { layer = skin.gameObject.layer };
                    go.transform.SetParent(skin.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = material;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = coating.receiveShadows;
                    go.AddComponent<Follow>().Init(skin, coating);
                    built++; triangles += (int)mesh.GetIndexCount(0) / 3;
                }
                // the coating's own normals point every which way near the windscreen top; face them into the cabin
                foreach (var c in coatings)
                {
                    var filter = c.GetComponent<MeshFilter>(); var source = filter?.sharedMesh;
                    if (source == null || !source.isReadable || Coatings.ContainsValue(source)) continue;
                    if (!Coatings.TryGetValue(source, out var turned)) Coatings[source] = turned = Inward(source, c.transform.InverseTransformPoint(root.TransformPoint(PilotEye)));
                    filter.sharedMesh = turned;
                }
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier cockpit backing: {built} skins, {triangles} triangles; {coatings.Count} coatings face the cabin.");
            }

            private static Mesh Backing(Mesh source, Vector3 eye)
            {
                var v = source.vertices; var tri = source.triangles; var keep = new List<int>();
                for (int i = 0; i < tri.Length; i += 3)
                    if (((v[tri[i]] + v[tri[i + 1]] + v[tri[i + 2]]) / 3f - eye).sqrMagnitude < Reach * Reach) { keep.Add(tri[i]); keep.Add(tri[i + 1]); keep.Add(tri[i + 2]); }
                if (keep.Count == 0) return null;
                var nv = new Vector3[v.Length]; var nn = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) { var d = (eye - v[i]).normalized; nv[i] = v[i] + d * Inset; nn[i] = d; }
                var mesh = new Mesh { name = "Courier cockpit backing " + source.name, indexFormat = v.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.vertices = nv; mesh.normals = nn; if (source.uv != null && source.uv.Length == v.Length) mesh.uv = source.uv;
                mesh.triangles = keep.ToArray(); mesh.RecalculateBounds(); mesh.UploadMeshData(true);
                return mesh;
            }

            private static Mesh Inward(Mesh source, Vector3 eye)
            {
                var mesh = UnityEngine.Object.Instantiate(source); mesh.name = source.name + " (cabin normals)";
                var v = source.vertices; var n = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) n[i] = (eye - v[i]).normalized;
                mesh.normals = n;
                return mesh;
            }

            // Hidden whenever the skin or the coating is (damage, distance culling, cockpit detail off).
            private sealed class Follow : MonoBehaviour
            {
                private Renderer skin, coating, self;
                internal void Init(Renderer skin, Renderer coating) { this.skin = skin; this.coating = coating; self = GetComponent<Renderer>(); }
                private void LateUpdate()
                {
                    bool on = skin != null && coating != null && skin.enabled && !skin.forceRenderingOff && coating.enabled && !coating.forceRenderingOff && coating.gameObject.activeInHierarchy;
                    if (self.enabled != on) self.enabled = on;
                }
            }
        }
    }
}
