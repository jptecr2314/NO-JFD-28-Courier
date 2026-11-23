using System;
using System.Collections.Generic;

namespace Japensou.NuclearOption.Superheavy
{
    public sealed class CargoItem
    {
        public readonly string Id;
        public readonly string Label;
        public readonly float Length;
        public readonly float Width;
        public readonly float Height;
        public readonly float Tonnes;

        public CargoItem(string id, string label, float length, float width, float height, float tonnes)
        {
            Id = id;
            Label = label;
            Length = length;
            Width = width;
            Height = height;
            Tonnes = tonnes;
        }
    }

    public struct CargoPlacement
    {
        public readonly CargoItem Item;
        public readonly int ItemIndex;
        public readonly float X;
        public readonly float Y;

        public CargoPlacement(CargoItem item, float x, float y, int itemIndex)
        {
            Item = item;
            X = x;
            Y = y;
            ItemIndex = itemIndex;
        }
    }

    public static class SuperheavyCargoPacking
    {
        public const float HoldLength = 31.98f;
        public const float HoldWidth = 6.10f;
        public const float EndClearance = 0.50f;
        public const float LongitudinalClearance = 0.50f;
        public const float SideClearance = 0.10f;
        public const float LateralClearance = 0.25f;

        private const float Epsilon = 0.0001f;
        private const int AttemptsPerOrder = 20000;

        // Coordinates are measured from the port side and front of the usable deck.
        // A failed bounded search means no layout was found for the configured clearances.
        public static bool TryPack(IReadOnlyList<CargoItem> items, out CargoPlacement[] placements)
        {
            placements = Array.Empty<CargoPlacement>();
            if (items == null) return false;
            if (items.Count == 0) return true;

            var order = new int[items.Count];
            float usableWidth = HoldWidth - 2 * SideClearance;
            float usableLength = HoldLength - 2 * EndClearance;
            float requiredArea = 0;
            for (int i = 0; i < items.Count; i++)
            {
                CargoItem item = items[i];
                if (item == null || !(item.Width > 0 && item.Width <= usableWidth + Epsilon) ||
                    !(item.Length > 0 && item.Length <= usableLength + Epsilon)) return false;

                order[i] = i;
                requiredArea += (item.Width + LateralClearance) * (item.Length + LongitudinalClearance);
            }

            // Expand each rectangle and the usable area by the same trailing clearances.
            if (requiredArea > (usableWidth + LateralClearance) * (usableLength + LongitudinalClearance) + Epsilon)
                return false;

            var arranged = new CargoPlacement[items.Count];
            var priorities = new Func<CargoItem, float>[]
            {
                item => item.Width,
                item => item.Width * item.Length,
                item => item.Length,
                item => -item.Width,
                item => 0f
            };
            foreach (var priority in priorities)
            {
                Array.Sort(order, (a, b) =>
                {
                    int rank = priority(items[b]).CompareTo(priority(items[a]));
                    return rank == 0 ? a.CompareTo(b) : rank;
                });
                int attempts = AttemptsPerOrder;
                if (!Place(items, order, arranged, 0, ref attempts)) continue;

                float left = HoldWidth;
                float right = 0;
                foreach (CargoPlacement placement in arranged)
                {
                    left = Math.Min(left, placement.X);
                    right = Math.Max(right, placement.X + placement.Item.Width);
                }

                float lateralOffset = (HoldWidth - left - right) / 2;
                placements = new CargoPlacement[arranged.Length];
                foreach (CargoPlacement placement in arranged)
                    placements[placement.ItemIndex] = new CargoPlacement(placement.Item, placement.X + lateralOffset,
                        placement.Y, placement.ItemIndex);
                return true;
            }
            return false;
        }

        private static bool Place(IReadOnlyList<CargoItem> items, int[] order, CargoPlacement[] arranged,
            int depth, ref int attempts)
        {
            if (depth == order.Length) return true;
            if (attempts <= 0) return false;

            int itemIndex = order[depth];
            CargoItem item = items[itemIndex];
            float right = Math.Max(SideClearance, HoldWidth - SideClearance - item.Width);
            float rear = Math.Max(EndClearance, HoldLength - EndClearance - item.Length);
            var xs = new SortedSet<float> { SideClearance, right };
            var ys = new SortedSet<float> { EndClearance, rear };
            for (int i = 0; i < depth; i++)
            {
                CargoPlacement other = arranged[i];
                AddAnchor(xs, other.X + other.Item.Width + LateralClearance, SideClearance, right);
                AddAnchor(xs, other.X - item.Width - LateralClearance, SideClearance, right);
                AddAnchor(ys, other.Y + other.Item.Length + LongitudinalClearance, EndClearance, rear);
                AddAnchor(ys, other.Y - item.Length - LongitudinalClearance, EndClearance, rear);
            }

            foreach (float y in ys)
            foreach (float x in xs)
            {
                if (attempts-- <= 0) return false;
                if (!HasClearance(item, x, y, arranged, depth)) continue;

                arranged[depth] = new CargoPlacement(item, x, y, itemIndex);
                if (Place(items, order, arranged, depth + 1, ref attempts)) return true;
            }
            return false;
        }

        private static void AddAnchor(SortedSet<float> anchors, float candidate, float minimum, float maximum)
        {
            if (candidate >= minimum - Epsilon && candidate <= maximum + Epsilon)
                anchors.Add(Math.Max(minimum, Math.Min(maximum, candidate)));
        }

        private static bool HasClearance(CargoItem item, float x, float y, CargoPlacement[] arranged, int depth)
        {
            for (int i = 0; i < depth; i++)
            {
                CargoPlacement other = arranged[i];
                bool separatedAcross = x + item.Width + LateralClearance <= other.X + Epsilon ||
                    other.X + other.Item.Width + LateralClearance <= x + Epsilon;
                bool separatedAlong = y + item.Length + LongitudinalClearance <= other.Y + Epsilon ||
                    other.Y + other.Item.Length + LongitudinalClearance <= y + Epsilon;
                if (!separatedAcross && !separatedAlong) return false;
            }
            return true;
        }
    }
}
