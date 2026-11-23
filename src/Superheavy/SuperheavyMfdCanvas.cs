using System;
using System.Collections.Generic;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyMfdCanvas
    {
        private const int Width = 320;
        private const int Height = 192;
        internal const int ResolutionScale = 3;
        internal const int TextureWidth = Width * ResolutionScale;
        internal const int TextureHeight = Height * ResolutionScale;
        private const int ScopeX = 158;
        private const int ScopeY = 90;
        private const int ScopeRadius = 60;

        internal static readonly Color32 Ink = new Color32(207, 224, 220, 255);
        internal static readonly Color32 Muted = new Color32(113, 145, 149, 255);
        internal static readonly Color32 Amber = new Color32(245, 182, 81, 255);
        private static readonly Color32 Background = new Color32(9, 14, 17, 255);
        private static readonly Color32 Panel = new Color32(21, 31, 35, 255);
        private static readonly Color32 Sky = new Color32(33, 80, 118, 255);
        private static readonly Color32 Ground = new Color32(105, 73, 47, 255);
        private static readonly Color32[] face = BuildFace();
        private readonly Color32[] pixels = new Color32[TextureWidth * TextureHeight];
        internal Texture2D Texture { get; }

        internal SuperheavyMfdCanvas()
        {
            Texture = new Texture2D(TextureWidth, TextureHeight, TextureFormat.RGBA32, true)
            {
                name = "Superheavy flight display",
                filterMode = FilterMode.Trilinear,
                anisoLevel = 2,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        internal void Begin(string title)
        {
            Array.Copy(face, pixels, face.Length);
            Label(10, 178, title, Ink);
        }

        private static Color32[] BuildFace()
        {
            var data = new Color32[TextureWidth * TextureHeight];
            for (int y = 0; y < TextureHeight; y++)
            {
                int row = y / ResolutionScale;
                Color32 color = row == 167 ? Muted : row >= 168 || row == 29 ? Panel : Background;
                for (int x = 0; x < TextureWidth; x++)
                    data[y * TextureWidth + x] = color;
            }
            return data;
        }

        internal void Tape(int x, string label, string number, string unit, bool scaleAtRight)
        {
            int edge = scaleAtRight ? x + 59 : x;
            int direction = scaleAtRight ? -1 : 1;
            Line(edge, 48, edge, 149, Muted);
            for (int y = 56; y < 150; y += 11)
                Line(edge, y, edge + direction * (y % 2 == 0 ? 6 : 3), y, Muted);
            Fill(x, 88, 60, 24, Panel);
            Fill(x, 88, 60, 1, Ink);
            Fill(x, 111, 60, 1, Ink);
            int textScale = number.Length <= 4 ? 2 : 1;
            Label(x + (60 - number.Length * 6 * textScale) / 2, textScale == 2 ? 93 : 97, number, Ink, textScale);
            Label(x + (60 - label.Length * 6) / 2, 154, label, Muted);
            Label(x + (60 - unit.Length * 6) / 2, 38, unit, Muted);
        }

        internal void Commit()
        {
            Texture.SetPixels32(pixels);
            Texture.Apply(true);
        }

        internal void Horizon(float pitch, float roll)
        {
            const int left = 67, right = 232, bottom = 38, top = 152, cx = 149, cy = 95;
            float radians = roll * Mathf.Deg2Rad;
            float sine = Mathf.Sin(radians);
            float cosine = Mathf.Cos(radians);
            float offset = -pitch * 1.8f;
            const float sample = 1f / ResolutionScale;
            for (int y = bottom * ResolutionScale; y < top * ResolutionScale; y++)
            {
                float dy = (y + .5f) * sample - cy;
                float dx = left + .5f * sample - cx;
                float across = dx * cosine - dy * sine;
                float vertical = dy * cosine + dx * sine + offset;
                for (int x = left * ResolutionScale; x < right * ResolutionScale; x++)
                {
                    Color32 color = vertical > 0 ? Sky : Ground;
                    float ink = Mathf.Clamp01((1.2f - Mathf.Abs(vertical)) / sample + .5f);
                    if (ink < 1f && Mathf.Abs(across) < 27f)
                    {
                        float distance = Mathf.Abs(vertical - Mathf.Round(vertical / 18f) * 18f);
                        ink = Mathf.Max(ink, Mathf.Clamp01((.55f - distance) / sample + .5f));
                    }
                    pixels[y * TextureWidth + x] = ink <= 0f ? color : Color32.Lerp(color, Ink, ink);
                    across += cosine * sample;
                    vertical += sine * sample;
                }
            }
            Fill(cx - 28, cy - 1, 21, 2, Ink);
            Fill(cx + 8, cy - 1, 21, 2, Ink);
            Fill(cx - 2, cy - 2, 4, 4, Ink);
        }

        internal void Scope()
        {
            Circle(ScopeRadius);
            Circle(ScopeRadius / 2);
            Line(ScopeX, ScopeY - ScopeRadius, ScopeX, ScopeY + ScopeRadius, Muted);
            Line(ScopeX - ScopeRadius, ScopeY, ScopeX + ScopeRadius, ScopeY, Muted);
            Fill(ScopeX - 2, ScopeY - 2, 5, 5, Ink);
        }

        internal void Plot(float angle, float distance, Color32? color = null)
        {
            if (distance > 1f || float.IsNaN(distance))
                return;
            float x = ScopeX + Mathf.Sin(angle) * distance * ScopeRadius;
            float y = ScopeY + Mathf.Cos(angle) * distance * ScopeRadius;
            var tint = color ?? Ink;
            Line(x - 2f, y, x, y + 2f, tint);
            Line(x, y + 2f, x + 2f, y, tint);
            Line(x + 2f, y, x, y - 2f, tint);
            Line(x, y - 2f, x - 2f, y, tint);
        }

        internal void Bar(int x, int y, int width, float value)
        {
            if (float.IsNaN(value))
                value = 0f;
            Fill(x, y, width, 7, Panel);
            Fill(x, y, Mathf.RoundToInt(Mathf.Clamp01(value) * width), 7, Ink);
        }

        internal void Label(int x, int y, string text, Color32? color = null, int scale = 1)
        {
            foreach (char letter in text)
            {
                SuperheavyMfdFont.Draw(pixels, letter, x, y, scale, color ?? Ink);
                x += 6 * scale;
            }
        }

        private void Fill(int x, int y, int width, int height, Color32 color)
        {
            int endX = Mathf.Min(TextureWidth, (x + width) * ResolutionScale);
            int endY = Mathf.Min(TextureHeight, (y + height) * ResolutionScale);
            for (int py = Mathf.Max(0, y * ResolutionScale); py < endY; py++)
            for (int px = Mathf.Max(0, x * ResolutionScale); px < endX; px++)
                pixels[py * TextureWidth + px] = color;
        }

        private void Circle(int radius)
        {
            float previousX = ScopeX + radius, previousY = ScopeY;
            for (int i = 1; i <= 96; i++)
            {
                float angle = i * (Mathf.PI / 48f);
                float x = ScopeX + Mathf.Cos(angle) * radius;
                float y = ScopeY + Mathf.Sin(angle) * radius;
                Line(previousX, previousY, x, y, Muted);
                previousX = x;
                previousY = y;
            }
        }

        private void Line(float x, float y, float endX, float endY, Color32 color)
        {
            SuperheavyMfdFont.Line(pixels, new Vector2(x, y) * ResolutionScale,
                new Vector2(endX, endY) * ResolutionScale, color);
        }

    }

    internal static class SuperheavyMfdFont
    {
        private const int ScreenWidth = SuperheavyMfdCanvas.TextureWidth;
        private const int ScreenHeight = SuperheavyMfdCanvas.TextureHeight;
        private const int ScreenScale = SuperheavyMfdCanvas.ResolutionScale;
        private static readonly Dictionary<int, byte[]> masks = new Dictionary<int, byte[]>();

        // Each path uses pairs of grid coordinates, with Y measured from the baseline.
        private static readonly Dictionary<char, string> Paths = new Dictionary<char, string>
        {
            ['A'] = "00 04 26 44 40|02 42", ['B'] = "00 06 36 45 44 33 03|33 42 41 30 00",
            ['C'] = "45 36 16 05 01 10 30 41", ['D'] = "00 06 26 45 41 20 00",
            ['E'] = "46 06 00 40|03 33", ['F'] = "00 06 46|03 33",
            ['G'] = "45 36 16 05 01 10 30 41 43 23", ['H'] = "00 06|40 46|03 43",
            ['I'] = "06 46|26 20|00 40", ['J'] = "46 41 30 10 01|26 46",
            ['K'] = "00 06|46 03 40", ['L'] = "06 00 40",
            ['M'] = "00 06 23 46 40", ['N'] = "00 06 40 46",
            ['O'] = "16 36 45 41 30 10 01 05 16", ['P'] = "00 06 36 45 44 33 03",
            ['Q'] = "16 36 45 41 30 10 01 05 16|22 40", ['R'] = "00 06 36 45 44 33 03|23 40",
            ['S'] = "45 36 16 05 04 13 33 42 41 30 10 01", ['T'] = "06 46|26 20",
            ['U'] = "06 01 10 30 41 46", ['V'] = "06 20 46",
            ['W'] = "06 10 23 30 46", ['X'] = "06 40|46 00", ['Y'] = "06 23 46|23 20", ['Z'] = "06 46 00 40",
            ['0'] = "16 36 45 41 30 10 01 05 16|11 35", ['1'] = "15 26 20|10 30",
            ['2'] = "05 16 36 45 44 01 00 40", ['3'] = "06 46 23 33 42 41 30 10 01",
            ['4'] = "35 02 42|36 30", ['5'] = "46 06 03 33 42 41 30 10 01",
            ['6'] = "45 36 16 05 01 10 30 41 42 33 03", ['7'] = "06 46 10",
            ['8'] = "16 36 45 44 33 13 04 05 16|13 02 01 10 30 41 42 33",
            ['9'] = "41 30 10 01|43 13 04 05 16 36 45 41",
            ['%'] = "00 46|05 06 16 15 05|30 31 41 40 30", ['/'] = "00 46", ['-'] = "03 43",
            ['+'] = "03 43|21 25", ['.'] = "20", [','] = "21 10",
        };

        private readonly struct Stroke
        {
            private readonly Vector2 start;
            private readonly Vector2 delta;
            private readonly float inverseLengthSquared;

            internal Stroke(Vector2 start, Vector2 end)
            {
                this.start = start;
                delta = end - start;
                inverseLengthSquared = delta.sqrMagnitude > 0f ? 1f / delta.sqrMagnitude : 0f;
            }

            internal float Coverage(float x, float y, float radius)
            {
                var offset = new Vector2(x + .5f, y + .5f) - start;
                float t = Mathf.Clamp01(Vector2.Dot(offset, delta) * inverseLengthSquared);
                return Mathf.Clamp01(radius + .5f - (offset - t * delta).magnitude);
            }
        }

        internal static void Draw(Color32[] target, char letter, int x, int y, int size, Color32 color)
        {
            int key = letter * 8 + size;
            int unit = size * ScreenScale;
            int width = 5 * unit, height = 7 * unit;
            if (!masks.TryGetValue(key, out var coverage))
            {
                if (!Paths.TryGetValue(letter, out var paths))
                    return;
                coverage = MakeMask(paths, unit, width, height);
                masks.Add(key, coverage);
            }

            x *= ScreenScale;
            y *= ScreenScale;
            for (int row = Mathf.Max(0, -y); row < Mathf.Min(height, ScreenHeight - y); row++)
            for (int column = Mathf.Max(0, -x); column < Mathf.Min(width, ScreenWidth - x); column++)
            {
                int alpha = coverage[row * width + column];
                if (alpha == 0)
                    continue;
                int index = (y + row) * ScreenWidth + x + column;
                target[index] = alpha == 255 ? color : Color32.Lerp(target[index], color, alpha / 255f);
            }
        }

        private static byte[] MakeMask(string paths, int unit, int width, int height)
        {
            var mask = new byte[width * height];
            foreach (string path in paths.Split('|'))
            {
                string[] points = path.Split(' ');
                Vector2 start = Point(points[0], unit);
                for (int i = 0; i < Math.Max(1, points.Length - 1); i++)
                {
                    Vector2 end = points.Length == 1 ? start : Point(points[i + 1], unit);
                    var stroke = new Stroke(start, end);
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        int coverage = Mathf.RoundToInt(255f * stroke.Coverage(x, y, .38f * unit));
                        if (coverage > mask[index])
                            mask[index] = (byte)coverage;
                    }
                    start = end;
                }
            }
            return mask;
        }

        private static Vector2 Point(string pair, int unit) =>
            new Vector2(pair[0] - '0' + .5f, pair[1] - '0' + .5f) * unit;

        internal static void Line(Color32[] target, Vector2 start, Vector2 end, Color32 color)
        {
            var stroke = new Stroke(start, end);
            const float radius = .37f * ScreenScale;
            int left = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.x, end.x) - radius - .5f));
            int right = Mathf.Min(ScreenWidth, Mathf.CeilToInt(Mathf.Max(start.x, end.x) + radius + .5f));
            int bottom = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.y, end.y) - radius - .5f));
            int top = Mathf.Min(ScreenHeight, Mathf.CeilToInt(Mathf.Max(start.y, end.y) + radius + .5f));
            for (int y = bottom; y < top; y++)
            for (int x = left; x < right; x++)
            {
                float coverage = stroke.Coverage(x, y, radius);
                if (coverage <= 0f)
                    continue;
                int index = y * ScreenWidth + x;
                target[index] = Color32.Lerp(target[index], color, coverage);
            }
        }
    }
}
