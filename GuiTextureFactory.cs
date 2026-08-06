using System.Collections.Generic;
using UnityEngine;

namespace Iris.Iml
{
    public static class GuiTextureFactory
    {
        private static readonly Dictionary<string, Texture2D> _cache = new();

        public static void ClearCache()
        {
            foreach (var tex in _cache.Values)
                if (tex != null)
                    Object.DestroyImmediate(tex);
            _cache.Clear();
        }

        private static string Key(string prefix, int w, int h, int r, Color c, Color? bc, int bw)
            => $"{prefix}_{w}_{h}_{r}_{c.ToHex()}_{bc?.ToHex() ?? "none"}_{bw}";

        public static Texture2D GetRoundedRect(int width, int height, int radius, Color fill, Color? border = null, int borderWidth = 0)
        {
            var key = Key("rr", width, height, radius, fill, border, borderWidth);
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            int w = Mathf.Max(width, 2 * radius + 2);
            int h = Mathf.Max(height, 2 * radius + 2);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            Color transparent = new(0, 0, 0, 0);
            bool hasBorder = borderWidth > 0 && border.HasValue;
            Color borderCol = border ?? fill;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color col = fill;
                    float alpha = 1f;

                    if (CornerDist(x, y, w, h, radius, out float dist))
                    {
                        alpha = Mathf.Clamp01(-dist + 0.5f);
                    }

                    if (hasBorder)
                    {
                        // Inner shape (smaller by borderWidth)
                        int innerR = Mathf.Max(radius - borderWidth, 0);
                        if (x >= borderWidth && x < w - borderWidth &&
                            y >= borderWidth && y < h - borderWidth)
                        {
                            if (CornerDist(x - borderWidth, y - borderWidth, w - 2 * borderWidth, h - 2 * borderWidth, innerR, out float innerDist))
                            {
                                float innerAlpha = Mathf.Clamp01(-innerDist + 0.5f);
                                if (innerAlpha > 0.5f)
                                {
                                    col = fill;
                                    alpha = 1f;
                                }
                                else if (alpha > 0.5f)
                                {
                                    col = borderCol;
                                    alpha = 1f;
                                }
                                else
                                {
                                    col = borderCol;
                                    alpha = Mathf.Clamp01(-dist + 0.5f);
                                }
                            }
                            else
                            {
                                col = fill;
                                alpha = 1f;
                            }
                        }
                        // else: keep border color
                    }

                    if (alpha < 0.003f)
                        tex.SetPixel(x, y, transparent);
                    else
                    {
                        col.a = alpha;
                        tex.SetPixel(x, y, col);
                    }
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        public static Texture2D GetPill(int width, int height, Color fill, Color? knobColor = null, float knobPos = 0f)
        {
            var key = Key("pill", width, height, 0, fill, knobColor, (int)(knobPos * 100));
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            int radius = height / 2;
            int knobR = Mathf.Max(radius - 3, 3);
            int knobX = (int)(knobPos * (width - 2 * knobR) + knobR);

            Color transparent = new(0, 0, 0, 0);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float alpha = 1f;

                    if (CornerDist(x, y, width, height, radius, out float dist))
                    {
                        alpha = Mathf.Clamp01(-dist + 0.5f);
                    }

                    if (alpha < 0.003f)
                    {
                        tex.SetPixel(x, y, transparent);
                        continue;
                    }

                    // Check knob area
                    float kdx = x - knobX;
                    float kdy = y - radius;
                    float kdist = Mathf.Sqrt(kdx * kdx + kdy * kdy);
                    if (kdist <= knobR)
                    {
                        Color kc = knobColor ?? Color.white;
                        float ka = Mathf.Clamp01(knobR - kdist + 0.5f);
                        kc.a = ka;
                        tex.SetPixel(x, y, kc);
                    }
                    else
                    {
                        Color c = fill;
                        c.a = alpha;
                        tex.SetPixel(x, y, c);
                    }
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        public static Texture2D GetCircle(int size, Color fill, Color? border = null, int borderWidth = 0)
        {
            var key = Key("circle", size, size, size / 2, fill, border, borderWidth);
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            int radius = size / 2;
            Color transparent = new(0, 0, 0, 0);
            bool hasBorder = borderWidth > 0 && border.HasValue;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(radius - dist + 0.5f);

                    if (alpha < 0.003f)
                    {
                        tex.SetPixel(x, y, transparent);
                        continue;
                    }

                    Color col = fill;
                    if (hasBorder && dist > radius - borderWidth)
                        col = border.Value;
                    col.a = alpha;
                    tex.SetPixel(x, y, col);
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        public static Texture2D GetCheckmark(int size, Color stroke)
        {
            var key = Key("chk", size, size, 0, stroke, null, 0);
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            Color transparent = new(0, 0, 0, 0);
            // control points for checkmark
            float s = size;
            Vector2 p1 = new(s * 0.2f, s * 0.5f);
            Vector2 p2 = new(s * 0.42f, s * 0.72f);
            Vector2 p3 = new(s * 0.78f, s * 0.28f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new(x + 0.5f, y + 0.5f);
                    float d1 = PointToLineDist(p, p1, p2);
                    float d2 = PointToLineDist(p, p2, p3);
                    float d = Mathf.Min(d1, d2);
                    float alpha = Mathf.Clamp01(-d + 1.5f);
                    if (alpha < 0.003f)
                        tex.SetPixel(x, y, transparent);
                    else
                    {
                        Color c = stroke;
                        c.a = alpha;
                        tex.SetPixel(x, y, c);
                    }
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        public static Texture2D GetArrow(int size, ArrowDir dir, Color stroke)
        {
            var key = Key("arr", size, size, (int)dir, stroke, null, 0);
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            Color transparent = new(0, 0, 0, 0);
            float s = size;
            float cx = s / 2, cy = s / 2;
            float arm = s * 0.3f;
            float halfW = s * 0.15f;

            Vector2[] arrowPts = dir switch
            {
                ArrowDir.Right => new[] { new Vector2(cx - arm, cy - halfW), new Vector2(cx + arm, cy), new Vector2(cx - arm, cy + halfW) },
                ArrowDir.Left => new[] { new Vector2(cx + arm, cy - halfW), new Vector2(cx - arm, cy), new Vector2(cx + arm, cy + halfW) },
                ArrowDir.Down => new[] { new Vector2(cx - halfW, cy - arm), new Vector2(cx, cy + arm), new Vector2(cx + halfW, cy - arm) },
                ArrowDir.Up => new[] { new Vector2(cx - halfW, cy + arm), new Vector2(cx, cy - arm), new Vector2(cx + halfW, cy + arm) },
                _ => null
            };

            if (arrowPts == null) return tex;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new(x + 0.5f, y + 0.5f);
                    float d = PointToLineDist(p, arrowPts[0], arrowPts[1]);
                    d = Mathf.Min(d, PointToLineDist(p, arrowPts[1], arrowPts[2]));
                    d = Mathf.Min(d, PointToLineDist(p, arrowPts[0], arrowPts[2]));
                    float alpha = Mathf.Clamp01(-d + 1.5f);
                    if (alpha < 0.003f)
                        tex.SetPixel(x, y, transparent);
                    else
                    {
                        Color c = stroke;
                        c.a = alpha;
                        tex.SetPixel(x, y, c);
                    }
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        public static Texture2D GetIconSymbol(int size, IconSymbol symbol, Color stroke)
        {
            var key = Key("sym", size, size, (int)symbol, stroke, null, 0);
            if (_cache.TryGetValue(key, out var tex) && tex != null)
                return tex;

            tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };

            Color transparent = new(0, 0, 0, 0);
            int half = size / 2;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new(x + 0.5f, y + 0.5f);
                    float d = symbol switch
                    {
                        IconSymbol.Information => InfoSymbolDist(p, size),
                        IconSymbol.Success => SuccessSymbolDist(p, size, half),
                        IconSymbol.Warning => WarnSymbolDist(p, size, half),
                        IconSymbol.Error => CircleSymbolDist(p, size, half, '!'),
                        IconSymbol.Stop => CircleSymbolDist(p, size, half, '='),
                        _ => float.MaxValue
                    };
                    float alpha = Mathf.Clamp01(-d + 1.5f);
                    if (alpha < 0.003f)
                        tex.SetPixel(x, y, transparent);
                    else
                    {
                        Color c = stroke;
                        c.a = alpha;
                        tex.SetPixel(x, y, c);
                    }
                }
            }

            tex.Apply();
            _cache[key] = tex;
            return tex;
        }

        // ── internal helpers ──────────────────────────────

        private static bool CornerDist(int x, int y, int w, int h, int r, out float dist)
        {
            dist = 0f;
            // corners centered at (r,r), (w-r,r), (r,h-r), (w-r,h-r)
            float cx = 0, cy = 0;
            bool inCorner = false;

            if (x < r && y < r) { cx = r; cy = r; inCorner = true; }
            else if (x >= w - r && y < r) { cx = w - r; cy = r; inCorner = true; }
            else if (x < r && y >= h - r) { cx = r; cy = h - r; inCorner = true; }
            else if (x >= w - r && y >= h - r) { cx = w - r; cy = h - r; inCorner = true; }

            if (!inCorner) return false;
            float dx = x + 0.5f - cx;
            float dy = y + 0.5f - cy;
            dist = Mathf.Sqrt(dx * dx + dy * dy) - r;
            return true;
        }

        private static float PointToLineDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            Vector2 ap = p - a;
            float t = Vector2.Dot(ap, ab) / Vector2.Dot(ab, ab);
            t = Mathf.Clamp01(t);
            Vector2 closest = a + t * ab;
            return (p - closest).magnitude;
        }

        // Symbol distance fields

        private static float InfoSymbolDist(Vector2 p, float s)
        {
            float cx = s / 2, cy = s / 2;
            float r = s * 0.08f;
            // dot at center
            float dotDist = (p - new Vector2(cx, cy)).magnitude - r;
            // vertical line
            float lineX = s * 0.48f, lineTop = s * 0.56f, lineBot = s * 0.85f;
            float lineDist = PointToLineDist(p, new(lineX, lineTop), new(lineX, lineBot));
            return Mathf.Min(dotDist, lineDist);
        }

        private static float SuccessSymbolDist(Vector2 p, float s, int half)
        {
            Vector2 p1 = new(s * 0.18f, s * 0.5f);
            Vector2 p2 = new(s * 0.38f, s * 0.7f);
            Vector2 p3 = new(s * 0.82f, s * 0.3f);
            float d1 = PointToLineDist(p, p1, p2);
            float d2 = PointToLineDist(p, p2, p3);
            return Mathf.Min(d1, d2);
        }

        private static float WarnSymbolDist(Vector2 p, float s, int half)
        {
            // triangle point up, exclamation in center
            float cx = s / 2, top = s * 0.15f, bot = s * 0.78f;
            float triHalfW = s * 0.3f;
            float dot = PointToLineDist(p, new(cx, top), new(cx - triHalfW, bot));
            dot = Mathf.Min(dot, PointToLineDist(p, new(cx - triHalfW, bot), new(cx + triHalfW, bot)));
            dot = Mathf.Min(dot, PointToLineDist(p, new(cx + triHalfW, bot), new(cx, top)));
            // ! stem
            float stem = PointToLineDist(p, new(cx, s * 0.30f), new(cx, s * 0.55f));
            return Mathf.Min(dot, stem);
        }

        private static float CircleSymbolDist(Vector2 p, float s, int half, char ch)
        {
            // just return the center distance - the circle is drawn separately
            return float.MaxValue;
        }

        public enum ArrowDir { Right, Down, Left, Up }
        public enum IconSymbol { Information, Success, Warning, Error, Stop }
    }

    internal static class ColorExtensions
    {
        public static string ToHex(this Color c)
        {
            return $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}{(int)(c.a * 255):X2}";
        }
    }
}
