using System;
using System.Collections.Generic;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal static class SuperheavyAirdropZone
    {
        private const float SearchRadius = 450f;
        private const float MinGroundNormal = .978f;
        private const float FootprintMargin = 1.25f;
        private const int RoadShortlist = 12;
        private const int ForecastShortlist = 20;
        private static readonly int SurfaceMask = PhysicsLayers.StaticsMask | PhysicsLayers.WaterMask | PhysicsLayers.ShipsMask;
        private static readonly int ObstacleMask = SurfaceMask | PhysicsLayers.DefaultMask;
        private static readonly float[] RoadOffsets = { 0f, -32f, 32f };
        private static readonly float[] LandRings = { 0f, 35f, 75f, 150f, 275f, 425f };
        private static GlobalPosition roadCacheTarget;
        private static object roadCacheNetwork;
        private static List<RoadPatch> roadCache;

        private struct RoadPatch
        {
            internal GlobalPosition Point;
            internal Vector3 Direction;
            internal float DistanceSquared;
            internal float Before;
            internal float After;
            internal float Grade;
        }

        private struct LandingSite
        {
            internal GlobalPosition Point;
            internal Vector3 Heading;
            internal float Score;
        }

        internal static bool TryResolve(GlobalPosition forecast, Vector3 heading, float halfLength, float halfWidth,
            out GlobalPosition touchdown, out Vector3 touchdownHeading, Func<GlobalPosition, bool> reachable,
            GlobalPosition? occupied = null)
            => TryResolve(forecast,heading,halfLength,halfWidth,out touchdown,out touchdownHeading,
                (point,direction)=>reachable(point),occupied);

        internal static bool TryResolve(GlobalPosition forecast, Vector3 heading, float halfLength, float halfWidth,
            out GlobalPosition touchdown, out Vector3 touchdownHeading, Func<GlobalPosition, Vector3, bool> reachable,
            GlobalPosition? occupied = null)
        {
            touchdown = default;
            touchdownHeading = heading;
            if (!ValidFootprint(halfLength, halfWidth))
                return false;

            var candidates = new List<LandingSite>(ForecastShortlist);
            foreach (var road in NearestRoadPatches(forecast))
            {
                Vector3 side = Vector3.Cross(Vector3.up, road.Direction);
                foreach (float along in RoadOffsets)
                {
                    float distanceAlong = Mathf.Clamp(along, -road.Before, road.After);
                    if (along != 0f && Mathf.Abs(distanceAlong) < 6f)
                        continue;
                    var roadPoint = road.Point + (road.Direction + Vector3.up * road.Grade) * distanceAlong;
                    // A buried tunnel or an elevated deck may be represented in the road graph.
                    if (!TrySurface(roadPoint, out var roadHit, out _) ||
                        Mathf.Abs(roadHit.point.y - roadPoint.ToLocalPosition().y) > 4f)
                        continue;
                    for (int lane = -2; lane <= 2; lane++)
                    {
                        float offset = lane == 0 ? 0f : Mathf.Sign(lane) * (halfWidth + 5f + 9f * (Mathf.Abs(lane) - 1f));
                        var candidate = roadPoint + side * offset;
                        float distance = PlanarDistance(candidate, forecast);
                        float score = distance + (lane == 0 ? 25f : Mathf.Abs(offset) * .25f);
                        if (distance > SearchRadius || !WorthChecking(candidates, candidate, score, occupied) ||
                            !TryFootprint(candidate, road.Direction, halfLength, halfWidth, out var ground, out _))
                            continue;
                        if (Mathf.Abs(ground.ToLocalPosition().y - roadHit.point.y) > 2f ||
                            Physics.Linecast(ground.ToLocalPosition() + Vector3.up, roadHit.point + Vector3.up,
                                ObstacleMask, QueryTriggerInteraction.Ignore))
                            continue;
                        AddCandidate(candidates, ground, road.Direction, score);
                    }
                }
            }
            if (SelectReachable(candidates, reachable, out touchdown, out touchdownHeading))
                return true;

            // If no roadside site is reachable, prefer the nearest dry clearing.
            candidates.Clear();
            foreach (float radius in LandRings)
            {
                int steps = radius == 0f ? 1 : 12;
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * Mathf.PI * 2f / steps;
                    var candidate = forecast + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    if (WorthChecking(candidates, candidate, radius, occupied) &&
                        TryFootprint(candidate, heading, halfLength, halfWidth, out var ground, out _))
                        AddCandidate(candidates, ground, heading, radius);
                }
                if (SelectReachable(candidates, reachable, out touchdown, out touchdownHeading))
                    return true;
                candidates.Clear();
            }
            return false;
        }

        private static bool WorthChecking(List<LandingSite> sites, GlobalPosition candidate, float score, GlobalPosition? occupied) =>
            (!occupied.HasValue || PlanarDistance(candidate, occupied.Value) >= 26f) &&
            (sites.Count < ForecastShortlist || score < sites[sites.Count - 1].Score);

        private static void AddCandidate(List<LandingSite> sites, GlobalPosition point, Vector3 heading, float score)
        {
            if (sites.Exists(site => PlanarDistance(site.Point, point) < 3f))
                return;
            int index = sites.FindIndex(site => score < site.Score);
            if (index < 0)
                index = sites.Count;
            if (index >= ForecastShortlist)
                return;
            sites.Insert(index, new LandingSite { Point = point, Heading = heading, Score = score });
            if (sites.Count > ForecastShortlist)
                sites.RemoveAt(ForecastShortlist);
        }

        private static bool SelectReachable(List<LandingSite> sites, Func<GlobalPosition, Vector3, bool> reachable,
            out GlobalPosition point, out Vector3 heading)
        {
            foreach (var site in sites)
            {
                if (!reachable(site.Point,site.Heading))
                    continue;
                point = site.Point;
                heading = site.Heading;
                return true;
            }
            point = default;
            heading = Vector3.forward;
            return false;
        }

        private static List<RoadPatch> NearestRoadPatches(GlobalPosition target)
        {
            var network = LevelInfo.i?.roadNetwork;
            if (roadCache != null && ReferenceEquals(roadCacheNetwork, network) && PlanarDistance(target, roadCacheTarget) < .5f)
                return roadCache;
            var patches = new List<RoadPatch>(RoadShortlist);
            var roads = network?.roads;
            if (roads == null)
                return patches;
            foreach (var road in roads)
            {
                if (road == null || road.IsBridge() || road.points == null)
                    continue;
                for (int i = 1; i < road.points.Count; i++)
                {
                    var first = road.points[i - 1];
                    Vector3 segment = road.points[i] - first;
                    Vector3 flat = new Vector3(segment.x, 0f, segment.z);
                    if (flat.sqrMagnitude < 4f)
                        continue;
                    float t = Mathf.Clamp01(Vector3.Dot(target - first, flat) / flat.sqrMagnitude);
                    var point = first + segment * t;
                    float distance = PlanarDistance(point, target);
                    if (distance > SearchRadius)
                        continue;

                    int insert = patches.FindIndex(existing => distance * distance < existing.DistanceSquared);
                    if (insert < 0)
                        insert = patches.Count;
                    if (insert >= RoadShortlist)
                        continue;
                    float length = flat.magnitude;
                    patches.Insert(insert, new RoadPatch
                    {
                        Point = point,
                        Direction = flat / length,
                        DistanceSquared = distance * distance,
                        Before = t * length,
                        After = (1f - t) * length,
                        Grade = segment.y / length
                    });
                    if (patches.Count > RoadShortlist)
                        patches.RemoveAt(RoadShortlist);
                }
            }
            roadCacheNetwork = network;
            roadCacheTarget = target;
            return roadCache = patches;
        }

        internal static bool ValidateTouchdown(GlobalPosition touchdown, Vector3 forward,
            float halfLength, float halfWidth, out string reason)
        {
            return TryFootprint(touchdown, forward, halfLength, halfWidth, out _, out reason);
        }

        private static bool TryFootprint(GlobalPosition target, Vector3 forward, float halfLength, float halfWidth,
            out GlobalPosition ground, out string reason)
        {
            ground = target;
            reason = "Invalid cargo footprint.";
            if (!ValidFootprint(halfLength, halfWidth) || !TrySurface(target, out var center, out reason))
                return false;
            ground = center.point.ToGlobalPosition();
            forward.y = 0f;
            forward = forward.sqrMagnitude > .001f ? forward.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            float length = halfLength + FootprintMargin;
            float width = halfWidth + FootprintMargin;
            if (NearBridge(ground, Mathf.Sqrt(length * length + width * width) + 4f))
            {
                reason = "The touchdown footprint crosses a bridge.";
                return false;
            }
            float highest = center.point.y;
            for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && z == 0)
                    continue;
                Vector3 offset = side * (x * width) + forward * (z * length);
                if (!TrySurface(ground + offset, out var edge, out reason))
                    return false;
                float expectedHeight = center.point.y - Vector3.Dot(center.normal, offset) / center.normal.y;
                if (Mathf.Abs(edge.point.y - expectedHeight) > .65f)
                {
                    reason = "The touchdown footprint crosses uneven ground.";
                    return false;
                }
                highest = Mathf.Max(highest, edge.point.y);
            }

            Vector3 boxCenter = new Vector3(center.point.x, highest + 2.85f, center.point.z);
            if (Physics.CheckBox(boxCenter, new Vector3(width, 2.5f, length), Quaternion.LookRotation(forward),
                ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                reason = "The touchdown footprint is obstructed.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static bool NearBridge(GlobalPosition position, float radius)
        {
            var roads = LevelInfo.i?.roadNetwork?.roads;
            if (roads == null)
                return false;
            float radiusSquared = radius * radius;
            foreach (var road in roads)
            {
                if (road == null || !road.IsBridge() || road.points == null)
                    continue;
                for (int i = 1; i < road.points.Count; i++)
                {
                    Vector3 segment = road.points[i] - road.points[i - 1];
                    Vector3 offset = position - road.points[i - 1];
                    segment.y = offset.y = 0f;
                    float lengthSquared = segment.sqrMagnitude;
                    float along = lengthSquared > .001f ? Mathf.Clamp01(Vector3.Dot(offset, segment) / lengthSquared) : 0f;
                    if ((offset - segment * along).sqrMagnitude < radiusSquared)
                        return true;
                }
            }
            return false;
        }

        private static bool TrySurface(GlobalPosition position, out RaycastHit hit, out string reason)
        {
            Vector3 above = position.ToLocalPosition();
            above.y = Datum.LocalSeaY + 10000f;
            if (!Physics.Raycast(above, Vector3.down, out hit, 20000f, SurfaceMask, QueryTriggerInteraction.Ignore))
            {
                reason = "Terrain collision is unavailable at the touchdown.";
                return false;
            }
            int layer = hit.collider.gameObject.layer;
            if (layer == PhysicsLayers.Water || hit.point.y <= Datum.LocalSeaY + .45f)
            {
                reason = "The touchdown footprint reaches water.";
                return false;
            }
            if (layer == PhysicsLayers.Ships || hit.collider.GetComponentInParent<Building>() != null || hit.normal.y < MinGroundNormal)
            {
                reason = "The touchdown footprint is sloped or obstructed.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static bool ValidFootprint(float halfLength, float halfWidth) =>
            halfLength > 0f && halfWidth > 0f && !float.IsInfinity(halfLength) && !float.IsInfinity(halfWidth);

        private static float PlanarDistance(GlobalPosition a, GlobalPosition b)
        {
            Vector3 offset = a - b;
            return Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
        }
    }
}
