using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundLiveries : MonoBehaviour
    {
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        private static readonly Dictionary<Mesh, PaintMesh> SharedMeshes = new Dictionary<Mesh, PaintMesh>();
        private static ManualLogSource log;

        private sealed class PaintMesh
        {
            internal readonly Mesh Original;
            internal readonly Mesh[] Layouts = new Mesh[2];
            internal int Users;

            internal PaintMesh(Mesh mesh) => Original = mesh;

            internal Mesh WithLayout(int channel)
            {
                if (channel == 0)
                    return Original;

                int index = channel - 1;
                if (Layouts[index] != null)
                    return Layouts[index];

                var desired = new List<Vector2>();
                Original.GetUVs(channel, desired);
                if (desired.Count != Original.vertexCount)
                    throw new InvalidOperationException($"{Original.name} is missing paint UV channel {channel}.");

                var current = new List<Vector2>();
                Original.GetUVs(0, current);
                if (current.SequenceEqual(desired))
                    return Layouts[index] = Original;

                var mesh = Instantiate(Original);
                mesh.name = $"{Original.name} paint UV {channel}";
                mesh.SetUVs(0, desired);
                mesh.UploadMeshData(true);
                return Layouts[index] = mesh;
            }
        }

        private sealed class PaintRenderer
        {
            internal MeshRenderer Renderer;
            internal MeshFilter Filter;
            internal PaintMesh Mesh;
            internal int[] Slots;
        }

        private readonly List<PaintRenderer> renderers = new List<PaintRenderer>();

        internal LiveryData CurrentLivery { get; private set; }
        internal int CurrentChannel { get; private set; }

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            harmony.CreateClassProcessor(typeof(NativeLiveryPatch)).Patch();
        }

        [HarmonyPatch(typeof(LiveryBehaviour), "SetLivery")]
        private static class NativeLiveryPatch
        {
            private static void Postfix(Aircraft ___aircraft, LiveryData livery)
            {
                if (___aircraft == null || ___aircraft.definition == null ||
                    !SuperheavyGroundPhysicsPlugin.IsFamily(___aircraft.definition) ||
                    livery == null || livery.Texture == null)
                    return;

                try
                {
                    if (!___aircraft.TryGetComponent<SuperheavyGroundLiveries>(out var visual))
                    {
                        visual = ___aircraft.gameObject.AddComponent<SuperheavyGroundLiveries>();
                        visual.Bind(___aircraft);
                    }

                    visual.Apply(livery);
                }
                catch (Exception error)
                {
                    log.LogWarning($"Superheavy paint could not apply {livery.name}: {error.Message}");
                }
            }
        }

        private void Bind(Aircraft aircraft)
        {
            foreach (var renderer in SuperheavyNativeDamage.Components<MeshRenderer>(aircraft))
            {
                if (!renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null)
                    continue;

                var materials = renderer.sharedMaterials;
                var slots = new List<int>();
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null)
                        continue;
                    const string instanceSuffix = " (Instance)";
                    string name = material.name;
                    if (name.EndsWith(instanceSuffix, StringComparison.Ordinal))
                        name = name.Substring(0, name.Length - instanceSuffix.Length);
                    if (name == "SHGT_LiveryPaint" || name == "SHGT_LiveryPaintTwoSided")
                        slots.Add(slot);
                }

                if (slots.Count == 0)
                    continue;

                Mesh original = filter.sharedMesh;
                if (!SharedMeshes.TryGetValue(original, out var mesh))
                {
                    mesh = new PaintMesh(original);
                    SharedMeshes.Add(original, mesh);
                }

                mesh.Users++;
                renderers.Add(new PaintRenderer { Renderer = renderer, Filter = filter, Mesh = mesh, Slots = slots.ToArray() });
            }
        }

        internal bool TryGetOriginalMesh(MeshFilter filter, out Mesh mesh)
        {
            var entry = renderers.FirstOrDefault(r => r.Filter == filter);
            mesh = entry?.Mesh.Original;
            return mesh != null;
        }

        internal bool TryGetCurrentMesh(MeshFilter filter, out Mesh mesh)
        {
            var entry = renderers.FirstOrDefault(r => r.Filter == filter);
            mesh = entry?.Mesh.WithLayout(CurrentChannel);
            return mesh != null;
        }

        private void Apply(LiveryData livery)
        {
            if (renderers.Count == 0)
                return;

            int channel;
            switch (livery.name)
            {
                case "SHGT_Livery_bdf-matte":
                case "SHGT_Livery_bdf-digital":
                    channel = 1;
                    break;
                case "SHGT_Livery_bdf-forest":
                    channel = 2;
                    break;
                default:
                    channel = 0;
                    break;
            }

            // Resolve all mesh layouts before changing the displayed paint.
            foreach (var entry in renderers)
                if (entry.Filter != null)
                    entry.Mesh.WithLayout(channel);

            var block = new MaterialPropertyBlock();
            foreach (var entry in renderers)
            {
                if (entry.Renderer == null || entry.Filter == null)
                    continue;

                entry.Filter.sharedMesh = entry.Mesh.WithLayout(channel);
                foreach (int slot in entry.Slots)
                {
                    entry.Renderer.GetPropertyBlock(block, slot);
                    block.SetTexture(BaseMap, livery.Texture);
                    block.SetColor(BaseColor, Color.white);
                    block.SetFloat(Smoothness, livery.Glossiness);
                    entry.Renderer.SetPropertyBlock(block, slot);
                }
            }

            CurrentLivery = livery;
            CurrentChannel = channel;
            foreach (var turret in SuperheavyNativeDamage.Components<SuperheavyGroundDefense>(GetComponent<Aircraft>()))
                turret.ApplyLivery(livery, channel);

            log.LogInfo($"Superheavy paint applied {livery.name} (UV{channel}, {renderers.Count} renderers).");
        }

        private void OnDestroy()
        {
            foreach (var entry in renderers)
            {
                var mesh = entry.Mesh;
                if (--mesh.Users != 0)
                    continue;

                SharedMeshes.Remove(mesh.Original);
                foreach (var layout in mesh.Layouts)
                    if (layout != null && layout != mesh.Original)
                        Destroy(layout);
            }
        }
    }
}
