using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report 24/11/2026: the back of the inner engines looks different from the outer ones ("one has a burnt part,
    // the other is very white"). Measured in the fixture (flight-eng1-20261124-011238): the four inner GE-87s
    // (01, 02, 05, 06) carry the 20 %-longer study nacelle (10.08 m, lengthen_ge873_inner_pods.py), whose closed painted
    // cowl hides the dark fan/core exhaust that the four outer engines (8.31 m, the original GE-87-3-A shape) show.
    // Same diameter, materials and fan. The inner engines now use the outer engine mesh of the same wing for every LOD;
    // the meshes are baked in airframe space, so each copy is moved across to the inner axis with the inlet lip aligned
    // (flight-eng3-20261124-012318). The fan rotors, pylons, pod waist and the damage parts are unchanged.
    internal static class CourierEngineUniform
    {
        private static readonly (string inner, string outer)[] Pairs = { ("01", "03"), ("02", "04"), ("05", "07"), ("06", "08") };

        internal static void Apply(Transform aircraft, ICollection<UnitPart> parts)
        {
            var renderers = aircraft.GetComponentsInChildren<MeshRenderer>(true)
                .Concat(parts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<MeshRenderer>(true)))
                .Where(r => r != null && r.name.StartsWith("GE87 Engine 0", System.StringComparison.Ordinal))
                .Distinct().ToDictionary(r => r.name, r => r);
            int swapped = 0;
            foreach (var (inner, outer) in Pairs)
                foreach (var lod in new[] { "LOD0", "LOD1", "LOD2" })
                {
                    if (!renderers.TryGetValue("GE87 Engine " + inner + " " + lod, out var dst) || !renderers.TryGetValue("GE87 Engine " + outer + " " + lod, out var src))
                        continue;
                    var df = dst.GetComponent<MeshFilter>(); var sf = src.GetComponent<MeshFilter>();
                    if (df == null || sf == null || df.sharedMesh == null || sf.sharedMesh == null || df.sharedMesh == sf.sharedMesh)
                        continue;
                    Bounds db = df.sharedMesh.bounds, sb = sf.sharedMesh.bounds;
                    df.sharedMesh = sf.sharedMesh;
                    dst.transform.localPosition += new Vector3(db.center.x - sb.center.x, db.center.y - sb.center.y, db.max.z - sb.max.z);
                    swapped++;
                }
            int sealedTris = 0;
            foreach (var r in renderers.Values.Where(r => r.name.EndsWith(" LOD0", System.StringComparison.Ordinal) || r.name.EndsWith(" LOD1", System.StringComparison.Ordinal)))
                sealedTris += SealAftDuct(r);
            foreach (var group in renderers.Values.Select(r => r.GetComponentInParent<LODGroup>()).Where(g => g != null).Distinct())
                group.RecalculateBounds();
            if (swapped > 0)
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier engines: {swapped} inner engine meshes now match the outer GE-87 nacelles.");
            if (sealedTris > 0)
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier engines: aft bypass duct closed from outside ({sealedTris} faces in the shared engine meshes); weathered exhaust finish applied.");
        }

        // Owner report 24/11/2026 ("part of the engine goes invisible"). Measured on the exported GE-87 mesh
        // (paint1/paint2 probes): the painted fan cowl ends at its chevrons about 1.8 m ahead of the nozzle; aft of
        // them the proxy bypass duct (exhaust material, r 1.34-1.47 m) only has inward faces, so from outside the
        // duct is culled and the nacelle reads as see-through down to the core. Every inward duct face gets an
        // outward twin (same material, 2 % smaller radius): aft of the cowl it closes the nacelle as a metal sleeve,
        // between the chevrons it fills the gaps, and under the cowl (r >= 1.44 m vs <= 1.44 m) it stays hidden
        // (paint3, paint4 probes). One patched copy per shared mesh.
        //
        // Owner, after the 0.1.9 release ("the heat marks are too perfect"): the exhaust used a 256x64 proxy texture
        // repeated 8 times around and about 4 times along the duct (UV = profile length / 1.5 m), so its straw-to-blue
        // gradient read as identical rings and its streaks as a regular comb. The exhaust faces now get their own
        // vertices with one wrap around the axis (u = angle) and one along it (v = 0 at the duct front, 1 at the
        // nozzle), and a generated weathered texture: an irregular heat-tint front, soot streaks, blotches and panel
        // seams. Each engine turns the pattern by its own angle, so no two pods match.
        private static readonly Dictionary<Mesh, Mesh> Sealed = new Dictionary<Mesh, Mesh>();
        private const float Shrink = .98f;

        private static int SealAftDuct(MeshRenderer renderer)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var src = filter != null ? filter.sharedMesh : null;
            if (src == null)
                return 0;
            int exhaust = -1; var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length && i < src.subMeshCount; i++)
                if (mats[i] != null && (mats[i] == WeatheredExhaust || mats[i].name.StartsWith("GE87_9", System.StringComparison.Ordinal))) exhaust = i;
            if (Sealed.TryGetValue(src, out var done))
            {
                if (done != null) { filter.sharedMesh = done; Weather(renderer, exhaust); }
                return 0;
            }
            Sealed[src] = null;
            if (!src.isReadable || exhaust < 0 || !mats.Any(m => m != null && m.name.Contains("LiveryPaint")))
                return 0;
            var v = src.vertices; var n = src.normals; var tan = src.tangents; var col = src.colors;
            var c = src.bounds.center;
            var tris = src.GetTriangles(exhaust);
            float zFront = float.NegativeInfinity, zBack = float.PositiveInfinity;
            foreach (int k in tris) { zFront = Mathf.Max(zFront, v[k].z); zBack = Mathf.Min(zBack, v[k].z); }
            float length = Mathf.Max(.1f, zFront - zBack);
            var addV = new List<Vector3>(); var addN = new List<Vector3>(); var addUv = new List<Vector2>(); var origin = new List<int>();
            var exhaustT = new List<int>(); int outward = 0;
            var uvs = new Vector2[3];
            void Emit(int i0, int i1, int i2, bool twin)
            {
                var ids = twin ? new[] { i0, i2, i1 } : new[] { i0, i1, i2 };
                for (int j = 0; j < 3; j++)
                {
                    var p = v[ids[j]];
                    uvs[j] = new Vector2((Mathf.Atan2(p.y - c.y, p.x - c.x) + Mathf.PI) / (2f * Mathf.PI), (zFront - p.z) / length);
                }
                float lo = Mathf.Min(uvs[0].x, Mathf.Min(uvs[1].x, uvs[2].x)), hi = Mathf.Max(uvs[0].x, Mathf.Max(uvs[1].x, uvs[2].x));
                if (hi - lo > .5f) for (int j = 0; j < 3; j++) if (uvs[j].x < .5f) uvs[j].x += 1f; // across the seam (wrap mode repeat)
                for (int j = 0; j < 3; j++)
                {
                    int k = ids[j]; var p = v[k];
                    exhaustT.Add(v.Length + addV.Count);
                    addV.Add(twin ? new Vector3(c.x + (p.x - c.x) * Shrink, c.y + (p.y - c.y) * Shrink, p.z) : p);
                    addN.Add(n.Length > k ? (twin ? -n[k] : n[k]) : Vector3.forward);
                    addUv.Add(uvs[j]); origin.Add(k);
                }
            }
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                Vector3 a = v[i0], b = v[i1], d = v[i2], m = (a + b + d) / 3f;
                Emit(i0, i1, i2, false);
                var radial = new Vector3(m.x - c.x, m.y - c.y, 0f);
                if (radial.magnitude >= 1.25f && Vector3.Dot(Vector3.Cross(b - a, d - a), radial) < 0f) { Emit(i0, i1, i2, true); outward++; }
            }
            var mesh = Object.Instantiate(src);
            mesh.name = src.name + " sealed";
            if (v.Length + addV.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            var allV = new List<Vector3>(v); allV.AddRange(addV); mesh.SetVertices(allV);
            if (n.Length == v.Length) { var allN = new List<Vector3>(n); allN.AddRange(addN); mesh.SetNormals(allN); }
            if (tan.Length == v.Length) { var allTan = new List<Vector4>(tan); foreach (int k in origin) allTan.Add(tan[k]); mesh.SetTangents(allTan); }
            if (col.Length == v.Length) { var allC = new List<Color>(col); foreach (int k in origin) allC.Add(col[k]); mesh.SetColors(allC); }
            for (int ch = 0; ch < 4; ch++)
            {
                var uv = new List<Vector2>(); src.GetUVs(ch, uv);
                if (uv.Count != v.Length) continue;
                if (ch == 0) uv.AddRange(addUv); else foreach (int k in origin) uv.Add(uv[k]);
                mesh.SetUVs(ch, uv);
            }
            for (int sub = 0; sub < src.subMeshCount; sub++)
                mesh.SetTriangles(sub == exhaust ? exhaustT : new List<int>(src.GetTriangles(sub)), sub, false);
            mesh.RecalculateBounds();
            Sealed[src] = mesh;
            filter.sharedMesh = mesh;
            Weather(renderer, exhaust);
            return outward;
        }

        private static Material WeatheredExhaust;
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        private static void Weather(MeshRenderer renderer, int slot)
        {
            var mats = renderer.sharedMaterials;
            if (slot < 0 || slot >= mats.Length || mats[slot] == null)
                return;
            if (WeatheredExhaust == null)
            {
                WeatheredExhaust = new Material(mats[slot]) { name = "GE87 weathered exhaust" };
                var tex = ExhaustTexture();
                if (WeatheredExhaust.HasProperty("_BaseMap")) { WeatheredExhaust.SetTexture("_BaseMap", tex); WeatheredExhaust.SetTextureScale("_BaseMap", Vector2.one); WeatheredExhaust.SetTextureOffset("_BaseMap", Vector2.zero); }
                if (WeatheredExhaust.HasProperty("_MainTex")) WeatheredExhaust.SetTexture("_MainTex", tex);
                if (WeatheredExhaust.HasProperty("_BaseColor")) WeatheredExhaust.SetColor("_BaseColor", Color.white);
                if (WeatheredExhaust.HasProperty("_Smoothness")) WeatheredExhaust.SetFloat("_Smoothness", .32f);
            }
            mats[slot] = WeatheredExhaust;
            renderer.sharedMaterials = mats;
            // each engine turns the pattern by its own angle (engine number from "GE87 Engine 0N LODk")
            int engine = 0; var name = renderer.name;
            if (name.Length >= 14) int.TryParse(name.Substring(12, 2), out engine);
            renderer.GetPropertyBlock(Block, slot);
            Block.SetVector("_BaseMap_ST", new Vector4(1f, 1f, Mathf.Repeat(engine * .382f + .17f, 1f), 0f));
            renderer.SetPropertyBlock(Block, slot);
        }

        // The pixels are pure math: they are computed on a worker thread started when the plugin loads, so the first
        // Courier spawn no longer stalls ~650 ms (that hitch also let native reviews read the aircraft before its
        // spawn livery landed, native-Regression-20261124-033018). Only the Texture2D upload stays on the main thread.
        private const int W = 512, H = 256;
        private static System.Threading.Tasks.Task<Color32[]> pixels;
        internal static void Warm() { if (pixels == null) pixels = System.Threading.Tasks.Task.Run(() => ExhaustPixels()); }

        private static Texture2D ExhaustTexture()
        {
            Warm();
            var px = pixels.Result;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "GE87 weathered exhaust", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, anisoLevel = 4 };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        private static Color32[] ExhaustPixels()
        {
            var px = new Color32[W * H];
            var titanium = new Color(.47f, .47f, .48f);
            var straw = new Color(.55f, .49f, .36f);
            var bronze = new Color(.47f, .37f, .29f);
            var blue = new Color(.31f, .32f, .42f);
            var dark = new Color(.19f, .18f, .18f);
            var soot = new Color(.07f, .065f, .06f);
            for (int y = 0; y < H; y++)
            {
                float v = (y + .5f) / H; // row 0 is v = 0: the front of the duct
                for (int x = 0; x < W; x++)
                {
                    float u = (x + .5f) / W;
                    float blotch = Fbm(u, v * 2f, 5, 4);
                    float front = v + (blotch - .5f) * .32f + (Noise(u * 3f, v * 1.5f + 7f, 3) - .5f) * .12f;
                    Color col;
                    if (front < .28f) col = titanium;
                    else if (front < .46f) col = Color.Lerp(titanium, straw, Smooth(.28f, .46f, front));
                    else if (front < .62f) col = Color.Lerp(straw, bronze, Smooth(.46f, .62f, front));
                    else if (front < .78f) col = Color.Lerp(bronze, blue, Smooth(.62f, .78f, front));
                    else col = Color.Lerp(blue, dark, Smooth(.78f, .96f, front));
                    col = Color.Lerp(titanium, col, .55f);
                    // soot streaks running aft, stronger toward the nozzle and patchy around the pod
                    float streak = .55f * Noise(u * 90f, v * 1.4f, 90) + .45f * Noise(u * 37f, v * .7f + 3f, 37);
                    float sootAmount = Smooth(.45f, 1.05f, front) * Smooth(.35f, .85f, streak) * (.45f + .55f * Fbm(u + .3f, v, 3, 3));
                    col = Color.Lerp(col, soot, Mathf.Clamp01(sootAmount) * .8f);
                    // lighter and darker oxidation blotches
                    float spot = Fbm(u + .61f, v * 3f + 1.7f, 14, 3);
                    col *= 1f + Smooth(.6f, .75f, spot) * .05f - (1f - Smooth(.12f, .25f, spot)) * .07f;
                    // panel seams: two circumferential joints and eight slightly wandering axial ones
                    float seamV = Mathf.Min(Mathf.Abs(v - .24f), Mathf.Abs(v - .53f)) * H;
                    float axial = u * 8f + (Noise(v * 2f, 5f, 64) - .5f) * .08f;
                    float seamU = Mathf.Abs(axial - Mathf.Round(axial)) / 8f * W;
                    if (seamV < 1.2f || seamU < .9f) col *= .88f;
                    else if (seamV < 2.4f || seamU < 1.8f) col *= 1.03f;
                    col *= 1f + (Hash(x, y) - .5f) * .07f;
                    px[y * W + x] = new Color32((byte)(Mathf.Clamp01(col.r) * 255f), (byte)(Mathf.Clamp01(col.g) * 255f), (byte)(Mathf.Clamp01(col.b) * 255f), 255);
                }
            }
            return px;
        }

        private static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        private static float Hash(int x, int y)
        {
            unchecked { uint h = (uint)(x * 374761393 + y * 668265263); h = (h ^ (h >> 13)) * 1274126177u; return (h ^ (h >> 16)) / 4294967295f; }
        }

        // value noise; x wraps with the given period so the pattern closes around the pod
        private static float Noise(float x, float y, int period)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            int xa = ((x0 % period) + period) % period, xb = (xa + 1) % period;
            return Mathf.Lerp(Mathf.Lerp(Hash(xa, y0), Hash(xb, y0), fx), Mathf.Lerp(Hash(xa, y0 + 1), Hash(xb, y0 + 1), fx), fy);
        }

        private static float Fbm(float u, float v, int baseFrequency, int octaves)
        {
            float sum = 0, amp = .5f, norm = 0; int f = baseFrequency;
            for (int o = 0; o < octaves; o++) { sum += amp * Noise(u * f, v * f * .5f + o * 13.1f, f); norm += amp; amp *= .5f; f *= 2; }
            return sum / norm;
        }
    }
}
