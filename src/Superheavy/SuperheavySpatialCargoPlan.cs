using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SpatialCargoLoad
    {
        internal readonly WeaponMount Mount;
        internal readonly CargoItem Item;
        internal readonly float X;
        internal readonly float Y;

        internal SpatialCargoLoad(WeaponMount mount, CargoItem item, float x, float y)
        {
            Mount = mount;
            Item = item;
            X = x;
            Y = y;
        }
    }

    internal readonly struct SpatialCargoRoute
    {
        internal const float TurnPadding = .25f;
        internal readonly float Shift;
        internal readonly float Start;
        internal readonly float Distance;
        private readonly float approachShift;
        private readonly float approachDistance;

        internal SpatialCargoRoute(float shift, float start, float distance,
            float approachShift = 0f, float approachDistance = 0f)
        {
            Shift = shift;
            Start = start;
            Distance = distance;
            this.approachShift = approachShift;
            this.approachDistance = approachDistance;
        }

        internal float Sample(float travel, out float slope, out float lateralRoom)
        {
            bool approaching = approachDistance > 0f && travel < Start;
            float distance = approaching ? approachDistance : Distance;
            float shift = approaching ? approachShift : Shift - approachShift;
            float progress = approaching ? travel : travel - Start;
            float t = distance > 0f ? Mathf.Clamp01(progress / distance) : 0f;
            slope = distance > 0f ? shift * 30f * t * t * (1f - t) * (1f - t) / distance : 0f;
            float offset = shift * Mathf.Clamp01(t * t * t * (10f + t * (-15f + 6f * t)));
            lateralRoom = Mathf.Min(Mathf.Abs(offset), Mathf.Abs(shift - offset));
            return approaching ? offset : approachShift + offset;
        }
    }

    internal static class SuperheavySpatialCargoPlan
    {
        internal const int Capacity = 12;
        private const float DeckFrontZ = 17.50f;
        private const float DeckHeight = 4.44f;
        // The ramp extension narrows to 5.10 m. Leave 5 cm per side at its toe.
        private const float StraightLaneHalfWidth = 2.50f;
        private const float FullLaneHeight = 4.10f;
        private const float SweptLength = 10.60f;
        private const float DeckGap = .03f;
        private const float CenterlineTolerance = .02f;
        private const float Tolerance = .0001f;
        private const float HoldWidth = SuperheavyCargoPacking.HoldWidth;
        private const float HoldLength = SuperheavyCargoPacking.HoldLength;
        private const float EndMargin = SuperheavyCargoPacking.EndClearance;
        private const float WidthGap = SuperheavyCargoPacking.LateralClearance;
        private const float LengthGap = SuperheavyCargoPacking.LongitudinalClearance;
        internal const float LaneLeft = HoldWidth * .5f - StraightLaneHalfWidth;
        internal const float LaneRight = HoldWidth * .5f + StraightLaneHalfWidth;
        internal const float StowLeft = .05f;
        internal const float StowRight = HoldWidth - StowLeft;
        private const float RampApproach = 1f;
        // Distances from the nose-side edge of the hold. The HLT transport
        // pose was swept through the 5.20 m corridor over this part of the ramp.
        private const float UpperRampEntry = DeckFrontZ + 15.20f;
        private const float UpperRampLimit = DeckFrontZ + 21f;
        private const float UpperRampHalfWidth = 2.60f;

        private sealed class Measurement
        {
            internal CargoItem Item;
            internal Bounds Stowed;
            internal Bounds Release;
        }

        private static readonly ConditionalWeakTable<WeaponMount, Measurement> Measurements =
            new ConditionalWeakTable<WeaponMount, Measurement>();

        // A Retro Kit is the same vehicle with additional measured geometry.
        // Keep its distinct catalog/save key, but inherit the verified vehicle's
        // transport-pose policies. The dimensional limits still apply to the kit.
        internal static string SourceCargoKey(string key) =>
            key != null && key.StartsWith("JP_RDK_", StringComparison.Ordinal) ? key.Substring(7) : key;

        internal static bool TryMeasure(WeaponMount mount, out CargoItem item)
        {
            item = mount != null ? Measurements.GetValue(mount, Measure).Item : null;
            return item != null;
        }

        private static Measurement Measure(WeaponMount mount)
        {
            var measurement = new Measurement();
            if (!mount.Cargo || mount.ammo != 1 || mount.prefab == null)
                return measurement;

            var prefab = mount.prefab.transform;
            var mounted = mount.prefab.GetComponentInChildren<MountedCargo>(true);
            if (mounted == null || mounted.cargo == null || mounted.cargo.unitPrefab == null)
                return measurement;
            var collider = mounted.cargo.unitPrefab.GetComponent<BoxCollider>();
            if (collider == null)
                return measurement;

            // SpawnMount instantiates the prefab as a child and keeps its authored local transform.
            var prefabToAnchor = Matrix4x4.TRS(prefab.localPosition, prefab.localRotation, prefab.localScale);
            var worldToAnchor = prefabToAnchor * prefab.worldToLocalMatrix;
            var bounds = new Bounds();
            bool hasBounds = false;
            var lowerLods = new HashSet<Renderer>();
            var primaryLods = new HashSet<Renderer>();
            foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                if (lods.Length == 0)
                    continue;
                primaryLods.UnionWith(lods[0].renderers);
                for (int i = 1; i < lods.Length; i++)
                    lowerLods.UnionWith(lods[i].renderers);
            }
            lowerLods.ExceptWith(primaryLods);

            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                var mesh = filter.sharedMesh;
                if (renderer == null || !renderer.enabled || mesh == null || lowerLods.Contains(renderer) ||
                    !SuperheavyQolStores.IsVisibleInTemplate(filter.transform, prefab))
                    continue;
                var matrix = worldToAnchor * filter.transform.localToWorldMatrix;
                if (mesh.isReadable)
                {
                    foreach (var vertex in mesh.vertices)
                        Include(matrix.MultiplyPoint3x4(vertex), ref bounds, ref hasBounds);
                }
                else
                    Include(mesh.bounds, matrix, ref bounds, ref hasBounds);
            }
            if (!hasBounds)
                return measurement;

            float height = bounds.size.y;
            if (!(bounds.size.x > 0f && bounds.size.z > 0f && height > 0f))
                return measurement;

            measurement.Stowed = bounds;
            // The native collision hull appears at handoff, after the mounted
            // cargo has moved through the hold and aligned with the ramp.
            Include(new Bounds(collider.center, collider.size), worldToAnchor * mounted.transform.localToWorldMatrix,
                ref bounds, ref hasBounds);
            measurement.Release = bounds;
            bounds = measurement.Stowed;
            measurement.Item = new CargoItem(mount.jsonKey, mount.mountName, bounds.size.z, bounds.size.x,
                height, mounted.cargo.mass * .001f);
            return measurement;
        }

        private static void Include(Bounds source, Matrix4x4 matrix, ref Bounds output, ref bool hasBounds)
        {
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? source.min.x : source.max.x,
                    (i & 2) == 0 ? source.min.y : source.max.y,
                    (i & 4) == 0 ? source.min.z : source.max.z);
                Include(matrix.MultiplyPoint3x4(corner), ref output, ref hasBounds);
            }
        }

        private static void Include(Vector3 point, ref Bounds output, ref bool hasBounds)
        {
            if (hasBounds)
                output.Encapsulate(point);
            else
            {
                output = new Bounds(point, Vector3.zero);
                hasBounds = true;
            }
        }

        internal static Vector3 GetAnchor(SpatialCargoLoad load)
        {
            var measured = Measurements.GetValue(load.Mount, Measure);
            var bounds = measured.Stowed;
            // Unity +X is starboard and +Z is toward the nose.
            return new Vector3(load.X - HoldWidth * .5f - bounds.min.x,
                DeckHeight + DeckGap - measured.Release.min.y, DeckFrontZ - load.Y - bounds.max.z);
        }

        internal static Vector3 GetEnvelopeCenter(SpatialCargoLoad load) =>
            GetAnchor(load) + Measurements.GetValue(load.Mount, Measure).Stowed.center;

        internal static Bounds GetReleaseBounds(SpatialCargoLoad load)
        {
            var bounds = Measurements.GetValue(load.Mount, Measure).Release;
            bounds.center += GetAnchor(load);
            return bounds;
        }

        internal static Vector2 MigrateLegacyPosition(WeaponMount mount, float x, float y)
        {
            var measured = Measurements.GetValue(mount, Measure);
            return new Vector2(x + (measured.Stowed.min.x - measured.Release.min.x),
                y + (measured.Release.max.z - measured.Stowed.max.z));
        }

        internal static bool TryValidate(IReadOnlyList<SpatialCargoLoad> loads, out string reason)
        {
            if (loads == null || loads.Count > Capacity)
            {
                reason = $"The hangar can carry up to {Capacity} separate loads.";
                return false;
            }

            for (int i = 0; i < loads.Count; i++)
            {
                var load = loads[i];
                if (load == null || !TryMeasure(load.Mount, out var measured) || measured != load.Item)
                {
                    reason = "A load is missing its measured dimensions.";
                    return false;
                }
                if (!HasHeightClearance(load))
                {
                    reason = RequiresCenterline(load.Mount, load.Item)
                        ? $"{load.Item.Label} must stay centered to clear the roof over the ramp."
                        : $"{load.Item.Label} has no verified overhead clearance through the ramp.";
                    return false;
                }
                if (!(load.X >= StowLeft - Tolerance && load.X + load.Item.Width <= StowRight + Tolerance &&
                    Measurements.GetValue(load.Mount, Measure).Release.size.x <= LaneRight - LaneLeft + Tolerance))
                {
                    reason = $"{load.Item.Label} exceeds the deck or ramp width.";
                    return false;
                }
                if (!(load.Y >= EndMargin - Tolerance && load.Y + load.Item.Length <= HoldLength - EndMargin + Tolerance))
                {
                    reason = $"{load.Item.Label} extends beyond the usable cargo floor.";
                    return false;
                }
                for (int j = 0; j < i; j++)
                {
                    if (Separated(load, loads[j]))
                        continue;
                    reason = $"{load.Item.Label} overlaps {loads[j].Item.Label} or leaves too little clearance.";
                    return false;
                }
            }

            if (loads.Any(load => Mathf.Abs(RampShift(load)) > Tolerance))
            {
                var remaining = InReleaseOrder(loads).ToList();
                while (remaining.Count > 0)
                {
                    var next = remaining[0];
                    remaining.RemoveAt(0);
                    if (TryRoute(next, remaining, out _))
                        continue;
                    reason = $"{next.Item.Label} needs clear deck space to align before the rear ramp.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        internal static bool TryFindSpot(IReadOnlyList<SpatialCargoLoad> loads, WeaponMount mount, CargoItem item,
            out SpatialCargoLoad result)
        {
            result = null;
            if (loads.Count >= Capacity || item == null)
                return false;

            var measured = Measurements.GetValue(mount, Measure);
            var xs = new List<float> { LaneLeft + measured.Stowed.min.x - measured.Release.min.x,
                LaneRight + measured.Stowed.min.x - measured.Release.max.x, CenterlineX(mount),
                StowLeft, StowRight - item.Width };
            // A compact transport model can use the edge of the hold and leave
            // room for a second load, even when its release hull is wider.
            if (measured.Release.size.x - item.Width > WidthGap)
                xs.Insert(0, StowLeft);
            var ys = new List<float> { EndMargin, HoldLength - EndMargin - item.Length };
            var trial = new List<SpatialCargoLoad>(loads) { null };
            foreach (var other in loads)
            {
                xs.Add(other.X + other.Item.Width + WidthGap);
                xs.Add(other.X - item.Width - WidthGap);
                ys.Add(other.Y + other.Item.Length + LengthGap);
                ys.Add(other.Y - item.Length - LengthGap);
            }
            ys.Sort();

            // Preserve every existing position. Find a new space against an edge or beside an existing load.
            foreach (float y in ys)
            foreach (float x in xs)
            {
                var candidate = new SpatialCargoLoad(mount, item, x, y);
                trial[loads.Count] = candidate;
                if (!TryValidate(trial, out _))
                    continue;
                result = candidate;
                return true;
            }
            return false;
        }

        internal static IOrderedEnumerable<SpatialCargoLoad> InReleaseOrder(IEnumerable<SpatialCargoLoad> loads) =>
            loads.OrderByDescending(load => load.Y + load.Item.Length).ThenBy(load => load.X);

        private static float RampShift(SpatialCargoLoad load)
        {
            var measured = Measurements.GetValue(load.Mount, Measure);
            float min = LaneLeft + measured.Stowed.min.x - measured.Release.min.x;
            float max = LaneRight + measured.Stowed.min.x - measured.Release.max.x;
            return Mathf.Clamp(load.X, min, Mathf.Max(min, max)) - load.X;
        }

        internal static bool TryRoute(SpatialCargoLoad load, IReadOnlyList<SpatialCargoLoad> parked,
            out SpatialCargoRoute route)
        {
            route = default;
            float shift = RampShift(load);
            if (Mathf.Abs(shift) <= Tolerance)
                return !Blocked(load, parked, load.X, load.X, 0f, HoldLength, 0f);

            var measured = Measurements.GetValue(load.Mount, Measure);
            float rearReach = measured.Stowed.max.z - measured.Release.min.z;
            float finalX = load.X + shift;
            float end = load.Y + rearReach;
            if (TryFinishTurn(load, parked, load.X, finalX, 0f, HoldLength - RampApproach - end,
                out float start, out float distance))
            {
                route = new SpatialCargoRoute(shift, start, distance);
                return true;
            }

            // Enter the upper ramp on the narrower transport footprint. Move
            // farther inward only after the neighboring load is clear.
            string sourceKey = SourceCargoKey(load.Item.Id);
            if (sourceKey != "jp_superheavy_cargo_hlt-mart" &&
                !(sourceKey == "Aryx_MC260_HLT-M" && measured.Release.size.y + DeckGap <= 3.80f &&
                    measured.Release.size.z <= SweptLength))
                return false;

            float bridgeLeft = HoldWidth * .5f - UpperRampHalfWidth;
            float bridgeRight = HoldWidth * .5f + UpperRampHalfWidth - load.Item.Width;
            float approachX = Mathf.Clamp(load.X, bridgeLeft, bridgeRight);
            float approachShift = approachX - load.X;
            float approachDistance = Mathf.Abs(approachShift) > Tolerance ? TurnDistance(approachShift) : 0f;
            if (approachDistance > 0f &&
                (approachDistance + end + SpatialCargoRoute.TurnPadding > UpperRampEntry ||
                    Blocked(load, parked, Mathf.Min(load.X, approachX), Mathf.Max(load.X, approachX),
                        0f, approachDistance, SpatialCargoRoute.TurnPadding)))
                return false;

            if (!TryFinishTurn(load, parked, approachX, finalX, approachDistance,
                UpperRampLimit - end - SpatialCargoRoute.TurnPadding, out start, out distance))
                return false;
            route = new SpatialCargoRoute(shift, start, distance, approachShift, approachDistance);
            return true;
        }

        private static float TurnDistance(float shift) => Mathf.Max(4f, Mathf.Abs(shift) * 9f);

        private static bool TryFinishTurn(SpatialCargoLoad load, IReadOnlyList<SpatialCargoLoad> parked,
            float fromX, float finalX, float earliest, float deadline, out float start, out float distance)
        {
            distance = TurnDistance(finalX - fromX);
            start = 0f;
            float left = Mathf.Min(fromX, finalX);
            float right = Mathf.Max(fromX, finalX);
            // Try turning now, then after the rear edge of each parked load.
            // Waiting lets the first of two side-by-side loads back out before turning.
            for (int i = -1; i < parked.Count; i++)
            {
                float candidate = i < 0 ? earliest :
                    parked[i].Y + parked[i].Item.Length - load.Y + SpatialCargoRoute.TurnPadding;
                if (candidate < earliest || candidate + distance > deadline + Tolerance ||
                    Blocked(load, parked, fromX, fromX, earliest, candidate, 0f) ||
                    Blocked(load, parked, left, right, candidate, candidate + distance, SpatialCargoRoute.TurnPadding) ||
                    Blocked(load, parked, finalX, finalX, candidate + distance, HoldLength, 0f))
                    continue;
                start = candidate;
                return true;
            }
            return false;
        }

        private static bool Blocked(SpatialCargoLoad load, IReadOnlyList<SpatialCargoLoad> parked,
            float left, float right, float from, float to, float padding) =>
            parked.Any(other => other.X < right + load.Item.Width - Tolerance &&
                left < other.X + other.Item.Width - Tolerance &&
                other.Y < load.Y + load.Item.Length + to + padding - Tolerance &&
                other.Y + other.Item.Length > load.Y + from - padding + Tolerance);

        private static bool Separated(SpatialCargoLoad a, SpatialCargoLoad b) =>
            a.X + a.Item.Width + WidthGap <= b.X + Tolerance ||
            b.X + b.Item.Width + WidthGap <= a.X + Tolerance ||
            a.Y + a.Item.Length + LengthGap <= b.Y + Tolerance ||
            b.Y + b.Item.Length + LengthGap <= a.Y + Tolerance;

        internal static float CenterlineX(WeaponMount mount)
        {
            var measured = Measurements.GetValue(mount, Measure);
            return HoldWidth * .5f + measured.Stowed.min.x - measured.Release.center.x;
        }

        internal static bool RequiresCenterline(WeaponMount mount, CargoItem item)
        {
            Vector3 size = Measurements.GetValue(mount, Measure).Release.size;
            return !HasStandardRampClearance(size) && HasSpecialCenterClearance(item, size);
        }

        private static bool HasHeightClearance(SpatialCargoLoad load)
        {
            Vector3 size = Measurements.GetValue(load.Mount, Measure).Release.size;
            return HasStandardRampClearance(size) ||
                (Mathf.Abs(load.X - CenterlineX(load.Mount)) <= CenterlineTolerance &&
                    HasSpecialCenterClearance(load.Item, size));
        }

        private static bool HasStandardRampClearance(Vector3 size) =>
            // The 5.00 m-wide box passed the ramp sweep at 4.10 m high.
            size.z <= SweptLength + Tolerance && size.y + DeckGap <= FullLaneHeight + Tolerance;

        private static bool HasSpecialCenterClearance(CargoItem item, Vector3 size)
        {
            float height = size.y + DeckGap;
            string sourceKey = SourceCargoKey(item.Id);
            // Measured in the centered, open-ramp sweep of the Aryx assets.
            if (sourceKey == "Aryx_FRCV_Artillery_x1")
                return size.z <= 12.30f && size.x <= 3.90f && height <= 3.27f;
            if (sourceKey == "Aryx_MC260_FireControl")
                return size.z <= 6.80f && size.x <= 2.70f && height <= 4.70f;

            if (size.z > SweptLength)
                return false;

            switch (sourceKey)
            {
                case "jp_superheavy_cargo_linebreaker_apc":
                case "jp_superheavy_cargo_linebreaker_sam":
                case "jp_superheavy_cargo_hlt-m":
                    return size.x <= 4.12f && height <= 4.23f;
                // These vehicles passed with individual body and turret bounds
                // centered on the ramp. Their full rectangular envelopes did not.
                case "jp_superheavy_cargo_linebreaker_ifv":
                    return size.z <= 8f && size.x <= 4f && height <= 4.34f;
                case "jp_superheavy_cargo_spaag2": // FGA-57 Anvil
                    return size.z <= 8f && size.x <= 4f && height <= 4.31f;
                default:
                    return false;
            }
        }
    }
}
