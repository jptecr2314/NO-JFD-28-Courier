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
                CurrentAirframeDamagePlugin.Log.LogInfo($"Courier engines: aft bypass duct closed from outside ({sealedTris} faces in the shared engine meshes).");
        }

        // Owner report 24/11/2026 ("part of the engine goes invisible"). Measured on the exported GE-87 mesh
        // (paint1/paint2 probes): the painted fan cowl ends at its chevrons about 1.8 m ahead of the nozzle; aft of
        // them the proxy bypass duct (exhaust material, r 1.34-1.47 m) only has inward faces, so from outside the
        // duct is culled and the nacelle reads as see-through down to the core. Every inward duct face gets an
        // outward twin (same material, 2 % smaller radius): aft of the cowl it closes the nacelle as a metal sleeve,
        // between the chevrons it fills the gaps, and under the cowl (r >= 1.44 m vs <= 1.44 m) it stays hidden
        // (paint3, paint4 probes). One patched copy per shared mesh.
        private static readonly Dictionary<Mesh, Mesh> Sealed = new Dictionary<Mesh, Mesh>();
        private const float Shrink = .98f;

        private static int SealAftDuct(MeshRenderer renderer)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var src = filter != null ? filter.sharedMesh : null;
            if (src == null)
                return 0;
            if (Sealed.TryGetValue(src, out var done))
            {
                if (done != null) filter.sharedMesh = done;
                return 0;
            }
            Sealed[src] = null;
            if (!src.isReadable)
                return 0;
            var mats = renderer.sharedMaterials;
            int exhaust = -1; var paint = new List<int>();
            for (int i = 0; i < mats.Length && i < src.subMeshCount; i++)
            {
                if (mats[i] == null) continue;
                if (mats[i].name.StartsWith("GE87_9", System.StringComparison.Ordinal)) exhaust = i;
                else if (mats[i].name.Contains("LiveryPaint")) paint.Add(i);
            }
            if (exhaust < 0 || paint.Count == 0)
                return 0;
            var v = src.vertices; var n = src.normals;
            var c = src.bounds.center;
            var tris = new List<int>(src.GetTriangles(exhaust));
            var addV = new List<Vector3>(); var addN = new List<Vector3>(); var origin = new List<int>(); var addT = new List<int>();
            int count = tris.Count;
            for (int i = 0; i < count; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                Vector3 a = v[i0], b = v[i1], d = v[i2], m = (a + b + d) / 3f;
                var radial = new Vector3(m.x - c.x, m.y - c.y, 0f);
                if (radial.magnitude < 1.25f || Vector3.Dot(Vector3.Cross(b - a, d - a), radial) >= 0f) continue;
                int baseIndex = v.Length + addV.Count;
                foreach (int k in new[] { i0, i2, i1 })
                {
                    var p = v[k];
                    addV.Add(new Vector3(c.x + (p.x - c.x) * Shrink, c.y + (p.y - c.y) * Shrink, p.z));
                    addN.Add(n.Length > k ? -n[k] : -radial.normalized);
                    origin.Add(k);
                }
                addT.Add(baseIndex); addT.Add(baseIndex + 1); addT.Add(baseIndex + 2);
            }
            if (addT.Count == 0)
                return 0;
            var mesh = Object.Instantiate(src);
            mesh.name = src.name + " sealed";
            int total = v.Length + addV.Count;
            if (total > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            var allV = new List<Vector3>(v); allV.AddRange(addV); mesh.SetVertices(allV);
            if (n.Length == v.Length) { var allN = new List<Vector3>(n); allN.AddRange(addN); mesh.SetNormals(allN); }
            var tan = src.tangents;
            if (tan.Length == v.Length) { var allTan = new List<Vector4>(tan); foreach (int k in origin) allTan.Add(tan[k]); mesh.SetTangents(allTan); }
            var col = src.colors;
            if (col.Length == v.Length) { var allC = new List<Color>(col); foreach (int k in origin) allC.Add(col[k]); mesh.SetColors(allC); }
            for (int ch = 0; ch < 4; ch++)
            {
                var uv = new List<Vector2>(); src.GetUVs(ch, uv);
                if (uv.Count != v.Length) continue;
                foreach (int k in origin) uv.Add(uv[k]);
                mesh.SetUVs(ch, uv);
            }
            for (int sub = 0; sub < src.subMeshCount; sub++)
            {
                var t = new List<int>(src.GetTriangles(sub));
                if (sub == exhaust) t.AddRange(addT);
                mesh.SetTriangles(t, sub, false);
            }
            mesh.RecalculateBounds();
            Sealed[src] = mesh;
            filter.sharedMesh = mesh;
            return addT.Count / 3;
        }
    }
}
