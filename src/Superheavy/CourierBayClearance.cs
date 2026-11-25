using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner report 24/11/2026: "these two bars in the bomb bay take the space the weapons pass through".
    // Measured in the fixture (flight-barpick2/3-20261124): hiding each bay renderer in turn, the two light bars
    // across the open bay belong to "GE87 Interior | CPS 03_CentreBody | NATIVE_Shell_Core_Belly". They are the lower
    // arcs of two interior frame rings at aircraft-local z = -7.0 m and -5.1 m (|x| < 3.5 m, y 2.94-4.57 m), left
    // closed across the extended unified bay. Every triangle of that mesh lying entirely inside the drop opening box
    // is removed at spawn; the rest of the rings, the bay lining and the hull are unchanged.
    internal static class CourierBayClearance
    {
        private const string Belly = "GE87 Interior | CPS 03_CentreBody | NATIVE_Shell_Core_Belly";
        private static readonly Bounds Opening = new Bounds(new Vector3(.35f, 3.8f, -6.05f), new Vector3(7.3f, 1.9f, 2.5f));
        // The same two rings continue into the left/right bay-frame interior meshes (thin sliver left in
        // flight-barclear1-20261124-031448). Those meshes also hold the bay walls, so only two thin slices at the
        // ring stations and inside the bay width are cleared there.
        private static readonly Bounds[] RingSlices = { new Bounds(new Vector3(0f, 3.8f, -7.0f), new Vector3(4.7f, 1.9f, .5f)), new Bounds(new Vector3(0f, 3.8f, -5.1f), new Vector3(4.7f, 1.9f, .5f)) };
        private static readonly Dictionary<Mesh, Mesh> Cleared = new Dictionary<Mesh, Mesh>();

        internal static int Apply(Transform aircraft, IEnumerable<Renderer> renderers)
        {
            int removed = 0;
            foreach (var r in renderers.Where(r => r != null && (r.name == Belly || r.name.StartsWith("GE87 Interior | CPS 03_CentreBody | NATIVE_BayFrame_Core_", System.StringComparison.Ordinal))))
            {
                var mf = r.GetComponent<MeshFilter>(); var src = mf ? mf.sharedMesh : null;
                if (src == null || !src.isReadable) continue;
                if (!Cleared.TryGetValue(src, out var dst))
                {
                    var toAircraft = aircraft.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    var v = src.vertices;
                    var keep = new List<int>[src.subMeshCount];
                    int cut = 0;
                    for (int s = 0; s < src.subMeshCount; s++)
                    {
                        var ix = src.GetTriangles(s); keep[s] = new List<int>(ix.Length);
                        for (int t = 0; t < ix.Length; t += 3)
                        {
                            Vector3 a = toAircraft.MultiplyPoint3x4(v[ix[t]]), b = toAircraft.MultiplyPoint3x4(v[ix[t + 1]]), c = toAircraft.MultiplyPoint3x4(v[ix[t + 2]]);
                            bool inside = r.name == Belly ? Opening.Contains(a) && Opening.Contains(b) && Opening.Contains(c) : RingSlices.Any(z => z.Contains(a) && z.Contains(b) && z.Contains(c));
                            if (inside) { cut++; continue; }
                            keep[s].Add(ix[t]); keep[s].Add(ix[t + 1]); keep[s].Add(ix[t + 2]);
                        }
                    }
                    if (cut == 0) { Cleared[src] = src; continue; }
                    dst = Object.Instantiate(src); dst.name = src.name + " bay clear";
                    for (int s = 0; s < src.subMeshCount; s++) dst.SetTriangles(keep[s], s, false);
                    dst.RecalculateBounds();
                    Cleared[src] = dst; removed += cut;
                }
                if (dst == src) continue;
                mf.sharedMesh = dst;
                var meta = r.GetComponent<NativePartitionSurface>();
                if (meta != null && meta.Original == src) meta.Original = dst;
            }
            return removed;
        }
    }
}
