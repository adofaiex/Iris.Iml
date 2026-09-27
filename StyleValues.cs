using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// Parsing helpers shared by the style engine, the layout engine and both
    /// render backends: numbers with optional units, lengths (px/%/auto),
    /// colors (hex / named) and shorthands (padding/margin/border/...).
    /// </summary>
    public static class StyleValues
    {
        // ── numbers ────────────────────────────────────────────────────────

        /// <summary>Parse "12", "12px", "12.5" (also with current-culture decimals).</summary>
        public static bool TryParseNumber(string s, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 2).Trim();
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        public static string GetStr(ImlStyle style, string key, string fallback = "")
            => style?.Setters != null && style.Setters.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v : fallback;

        public static float GetFloat(ImlStyle style, string key, float fallback = 0f)
            => style?.Setters != null && style.Setters.TryGetValue(key, out var v) && TryParseNumber(v, out var n)
                ? n : fallback;

        public static int GetInt(ImlStyle style, string key, int fallback = 0)
            => style?.Setters != null && style.Setters.TryGetValue(key, out var v) && TryParseNumber(v, out var n)
                ? Mathf.RoundToInt(n) : fallback;

        public static bool GetBool(ImlStyle style, string key, bool fallback = false)
        {
            if (style?.Setters == null || !style.Setters.TryGetValue(key, out var v)) return fallback;
            return ParseBool(v, fallback);
        }

        public static bool ParseBool(string s, bool fallback = false)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            if (bool.TryParse(s, out var b)) return b;
            if (s == "1" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "no" || s == "off") return false;
            return fallback;
        }

        // ── lengths ────────────────────────────────────────────────────────

        public readonly struct Length
        {
            public readonly bool Valid;
            public readonly bool Percent;
            public readonly bool Auto;
            public readonly float Value;

            public Length(bool valid, bool percent, bool auto, float value)
            {
                Valid = valid; Percent = percent; Auto = auto; Value = value;
            }

            public static Length None => default;
            public static Length From(float v) => new Length(true, false, false, v);

            /// <summary>Resolve to pixels against a basis (parent/available size).</summary>
            public float Resolve(float basis, float fallback)
            {
                if (!Valid || Auto) return fallback;
                return Percent ? basis * Value / 100f : Value;
            }
        }

        public static Length ParseLength(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Length.None;
            s = s.Trim();
            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return new Length(true, false, true, 0f);
            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                var num = s.Substring(0, s.Length - 1);
                if (TryParseNumber(num, out var p)) return new Length(true, true, false, p);
                return Length.None;
            }
            return TryParseNumber(s, out var v) ? Length.From(v) : Length.None;
        }

        public static Length GetLength(ImlStyle style, string key)
            => style?.Setters != null && style.Setters.TryGetValue(key, out var v)
                ? ParseLength(v) : Length.None;

        /// <summary>Read a length style property and resolve it; missing → fallback.</summary>
        public static float GetLengthValue(ImlStyle style, string key, float basis, float fallback)
        {
            var len = GetLength(style, key);
            return len.Valid ? len.Resolve(basis, fallback) : fallback;
        }

        // ── colors ─────────────────────────────────────────────────────────

        public static bool TryParseColor(string s, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                var hex = s.Substring(1);
                if (hex.Length == 3) // #RGB
                    hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
                if (hex.Length != 6 && hex.Length != 8) return false;
                if (!TryHex(hex, 0, out var r) || !TryHex(hex, 2, out var g) ||
                    !TryHex(hex, 4, out var b)) return false;
                float a = 1f;
                if (hex.Length == 8) TryHex(hex, 6, out a);
                color = new Color(r, g, b, a);
                return true;
            }

            switch (s.ToLowerInvariant())
            {
                case "white": color = Color.white; return true;
                case "black": color = Color.black; return true;
                case "transparent": color = new Color(0, 0, 0, 0); return true;
                case "red": color = new Color(1f, 0.35f, 0.35f); return true;
                case "green": color = new Color(0.4f, 0.85f, 0.5f); return true;
                case "blue": color = new Color(0.4f, 0.6f, 1f); return true;
                case "gray":
                case "grey": color = new Color(0.5f, 0.5f, 0.5f); return true;
                default: return false;
            }
        }

        private static bool TryHex(string hex, int index, out float value)
        {
            value = 0f;
            if (index + 1 >= hex.Length) return false;
            if (!byte.TryParse(hex.Substring(index, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var b)) return false;
            value = b / 255f;
            return true;
        }

        public static Color ParseColor(string s, Color fallback)
            => TryParseColor(s, out var c) ? c : fallback;

        public static Color GetColor(ImlStyle style, string key, Color fallback)
            => style?.Setters != null && style.Setters.TryGetValue(key, out var v) && TryParseColor(v, out var c)
                ? c : fallback;

        public static Color Multiply(Color c, float factor)
            => new Color(c.r * factor, c.g * factor, c.b * factor, c.a);

        // ── shorthands (applied once after var() substitution) ─────────────

        /// <summary>
        /// Expand shorthand properties in-place: padding/margin (1/2/4 values,
        /// space or comma separated), border ("2 #hex" / "2px solid #hex"),
        /// radius (uniform). Custom properties (<c>--x</c>) are left alone.
        /// </summary>
        public static void ExpandShorthands(Dictionary<string, string> setters)
        {
            if (setters.TryGetValue("padding", out var pad) && pad != null)
            {
                var vals = SplitValues(pad);
                if (vals.Count == 1) { SetAll(setters, "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", vals[0]); }
                else if (vals.Count == 2)
                {
                    setters["paddingTop"] = setters["paddingBottom"] = vals[0];
                    setters["paddingRight"] = setters["paddingLeft"] = vals[1];
                }
                else if (vals.Count >= 4)
                {
                    setters["paddingTop"] = vals[0];
                    setters["paddingRight"] = vals[1];
                    setters["paddingBottom"] = vals[2];
                    setters["paddingLeft"] = vals[3];
                }
            }

            if (setters.TryGetValue("margin", out var mar) && mar != null)
            {
                var vals = SplitValues(mar);
                if (vals.Count == 1) { SetAll(setters, "marginTop", "marginRight", "marginBottom", "marginLeft", vals[0]); }
                else if (vals.Count == 2)
                {
                    setters["marginTop"] = setters["marginBottom"] = vals[0];
                    setters["marginRight"] = setters["marginLeft"] = vals[1];
                }
                else if (vals.Count >= 4)
                {
                    setters["marginTop"] = vals[0];
                    setters["marginRight"] = vals[1];
                    setters["marginBottom"] = vals[2];
                    setters["marginLeft"] = vals[3];
                }
            }

            if (setters.TryGetValue("border", out var border) && border != null)
            {
                var vals = SplitValues(border);
                foreach (var v in vals)
                {
                    if (v.StartsWith("#", StringComparison.Ordinal) ||
                        (!v.Equals("solid", StringComparison.OrdinalIgnoreCase) &&
                         !v.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                         TryParseColor(v, out _) && !TryParseNumber(v, out _)))
                    {
                        setters["borderColor"] = v;
                    }
                    else if (TryParseNumber(v, out var w))
                    {
                        setters["borderWidth"] = v;
                    }
                }
            }

            if (setters.TryGetValue("radius", out var radius) && radius != null)
            {
                var vals = SplitValues(radius);
                if (vals.Count > 0) setters["radius"] = vals[0];
            }
        }

        private static void SetAll(Dictionary<string, string> d, string a, string b, string c, string e, string v)
        {
            d[a] = d[b] = d[c] = d[e] = v;
        }

        private static List<string> SplitValues(string value)
        {
            var result = new List<string>();
            foreach (var raw in value.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                result.Add(raw.Trim());
            return result;
        }
    }
}
