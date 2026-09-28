using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// The single style engine shared by both render backends.
    ///
    /// Resolve order for one element (later wins):
    ///   0. inherited properties from the parent's computed style
    ///      (color/fontSize/textAlign/whiteSpace/opacity + every <c>--custom</c> prop)
    ///   1. matching selector styles (<c>on="..."</c>), specificity ascending,
    ///      ties broken by registration order, pseudo classes matched against state
    ///   2. named styles from <c>style="a b c"</c> (space separated list)
    ///   3. inline style object <c>style={{ key: value }}</c>
    ///   4. layout attributes (width/height/gap/align/...) — attributes beat styles
    ///   5. <c>var(--name, fallback)</c> substitution
    ///   6. shorthand expansion (padding/margin/border/radius)
    /// </summary>
    public sealed class StyleEngine
    {
        private readonly Dictionary<string, ImlStyle> _named = new();
        private readonly List<ImlStyle> _selectors = new();
        private readonly Dictionary<string, string> _variables = new();
        private int _orderCounter;

        private static readonly string[] InheritableKeys =
        {
            "color", "fontSize", "textAlign", "whiteSpace", "opacity",
        };

        // Layout-ish element attributes that are folded into the computed style
        // so the layout engine only has to look at one place.
        private static readonly Dictionary<string, string> AttrToStyle = new(StringComparer.Ordinal)
        {
            ["width"] = "width",
            ["height"] = "height",
            ["min-width"] = "minWidth", ["minWidth"] = "minWidth",
            ["max-width"] = "maxWidth", ["maxWidth"] = "maxWidth",
            ["min-height"] = "minHeight", ["minHeight"] = "minHeight",
            ["max-height"] = "maxHeight", ["maxHeight"] = "maxHeight",
            ["gap"] = "gap",
            ["padding"] = "padding",
            ["margin"] = "margin",
            ["align"] = "alignItems",
            ["justify-content"] = "justifyContent", ["justifyContent"] = "justifyContent",
            ["direction"] = "direction",
            ["position"] = "position",
            ["top"] = "top", ["left"] = "left", ["right"] = "right", ["bottom"] = "bottom",
            ["x"] = "x", ["y"] = "y",
            ["anchor"] = "anchor",
            ["display"] = "display",
            ["overflow"] = "overflow",
            ["opacity"] = "opacity",
            ["flex-grow"] = "flexGrow", ["flexGrow"] = "flexGrow",
            ["flex-shrink"] = "flexShrink", ["flexShrink"] = "flexShrink",
            ["text-align"] = "textAlign", ["textAlign"] = "textAlign",
            ["white-space"] = "whiteSpace", ["whiteSpace"] = "whiteSpace",
        };

        // ── lifecycle ──────────────────────────────────────────────────────

        public void Reset()
        {
            _named.Clear();
            _selectors.Clear();
            _variables.Clear();
            _orderCounter = 0;
        }

        public StyleEngine() => Reset();

        /// <summary>Register one <c>&lt;Style&gt;</c> element.</summary>
        public void AddStyle(ImlStyle style)
        {
            style.Order = _orderCounter++;
            if (!string.IsNullOrEmpty(style.Name))
                _named[style.Name.ToLowerInvariant()] = style;
            if (style.Selector != null)
                _selectors.Add(style);
        }

        /// <summary>
        /// Finalize after all resources of a document set have been registered:
        /// resolve <c>extends</c>, sort selectors, collect document-level
        /// <c>--variables</c>.
        /// </summary>
        public void FinalizeRegistration()
        {
            foreach (var kv in _named) ResolveExtends(kv.Value, new HashSet<string>());
            foreach (var s in _selectors) ResolveExtends(s, new HashSet<string>());

            _selectors.Sort((a, b) =>
            {
                var c = a.Selector.Specificity.CompareTo(b.Selector.Specificity);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });

            _variables.Clear();
            foreach (var s in _named.Values)
                CollectVariables(s);
            foreach (var s in _selectors)
                CollectVariables(s);
        }

        private void CollectVariables(ImlStyle style)
        {
            foreach (var kv in style.Setters)
                if (kv.Key.StartsWith("--", StringComparison.Ordinal))
                    _variables[kv.Key] = kv.Value;
        }

        private void ResolveExtends(ImlStyle style, HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(style.Extends)) return;
            if (!visited.Add((style.Name ?? style.Extends).ToLowerInvariant()))
            {
                Debug.LogWarning($"[Iris.Iml] Circular style inheritance detected for '{style.Name}'");
                style.Extends = null;
                return;
            }
            if (_named.TryGetValue(style.Extends.ToLowerInvariant(), out var parent))
            {
                ResolveExtends(parent, visited);
                foreach (var kv in parent.Setters)
                    if (!style.Setters.ContainsKey(kv.Key))
                        style.Setters[kv.Key] = kv.Value;
            }
            style.Extends = null;
        }

        // ── resolve ────────────────────────────────────────────────────────

        /// <param name="element">Element being resolved (may be null for bare text nodes).</param>
        /// <param name="parentComputed">Parent's computed style (inheritance source).</param>
        /// <param name="state">Interaction state for pseudo classes.</param>
        /// <param name="resolveAttr">Callback evaluating an element attribute to a string.</param>
        public ImlStyle Resolve(ImlElement element, ImlStyle parentComputed, ImlStateFlags state,
            Func<ImlElement, string, string> resolveAttr)
        {
            var merged = new Dictionary<string, string>();

            // 0. inheritance
            if (parentComputed?.Setters != null)
            {
                foreach (var key in InheritableKeys)
                    if (parentComputed.Setters.TryGetValue(key, out var v))
                        merged[key] = v;
                foreach (var kv in parentComputed.Setters)
                    if (kv.Key.StartsWith("--", StringComparison.Ordinal))
                        merged[kv.Key] = kv.Value;
            }

            if (element != null)
            {
                var tag = element.TagName ?? "";
                var cls = resolveAttr != null ? resolveAttr(element, "class") : element.GetString("class");
                var id = resolveAttr != null ? resolveAttr(element, "id") : element.GetString("id");
                cls = cls?.ToLowerInvariant();
                id = id?.ToLowerInvariant();

                // 1. selector styles (pre-sorted by specificity/order)
                foreach (var ss in _selectors)
                    if (ss.Selector.Matches(tag, cls, id, state))
                        PutSetters(merged, ss.Setters);

                // 2. named styles from style="a b"
                var styleAttr = element.Attributes.TryGetValue("style", out var sa) ? sa : null;
                if (styleAttr != null &&
                    (styleAttr.Type == AttributeType.String || styleAttr.Type == AttributeType.Expression))
                {
                    var styleNames = resolveAttr != null ? resolveAttr(element, "style") : element.GetString("style");
                    if (!string.IsNullOrEmpty(styleNames))
                    {
                        foreach (var name in styleNames.Split(new[] { ' ', '\t', ',' },
                                     StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (_named.TryGetValue(name.ToLowerInvariant(), out var named))
                                PutSetters(merged, named.Setters);
                        }
                    }
                }

                // 3. inline style object
                if (styleAttr != null && styleAttr.Type == AttributeType.StyleObject && styleAttr.StyleEntries != null)
                {
                    foreach (var entry in styleAttr.StyleEntries)
                        if (!string.IsNullOrEmpty(entry.Property))
                            merged[NormalizeKey(entry.Property)] = entry.Value ?? "";
                }

                // 4. layout attributes (highest priority)
                if (resolveAttr != null)
                {
                    foreach (var kv in AttrToStyle)
                    {
                        if (!element.HasAttribute(kv.Key)) continue;
                        var value = resolveAttr(element, kv.Key);
                        if (!string.IsNullOrEmpty(value))
                            merged[kv.Value] = value;
                    }
                }
            }

            // 5. var() substitution
            if (NeedsVar(merged))
                SubstituteVars(merged);

            // 6. shorthands
            StyleValues.ExpandShorthands(merged);

            return new ImlStyle { Setters = merged };
        }

        private static void PutSetters(Dictionary<string, string> merged, Dictionary<string, string> setters)
        {
            if (setters == null) return;
            foreach (var kv in setters)
                merged[NormalizeKey(kv.Key)] = kv.Value;
        }

        /// <summary>Normalize a property name: <c>padding-top</c> → <c>paddingTop</c>,
        /// <c>background-color</c> → <c>background</c>. Custom properties (<c>--x</c>) pass through.</summary>
        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (key.StartsWith("--", StringComparison.Ordinal)) return key;
            if (key.IndexOf('-') < 0) return key;

            var parts = key.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return key;
            var result = parts[0];
            for (var i = 1; i < parts.Length; i++)
                result += char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);

            // aliases
            switch (result)
            {
                case "backgroundColor": return "background";
                case "textColor": return "color";
                default: return result;
            }
        }

        // ── var() ──────────────────────────────────────────────────────────

        private static readonly Regex VarRegex = new(
            @"var\(\s*(--[\w\-]+)\s*(?:,\s*([^)]*?))?\)", RegexOptions.Compiled);

        private static bool NeedsVar(Dictionary<string, string> setters)
        {
            foreach (var kv in setters)
                if (kv.Value != null && kv.Value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private void SubstituteVars(Dictionary<string, string> setters)
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var changed = false;
                foreach (var kv in new List<KeyValuePair<string, string>>(setters))
                {
                    if (kv.Value == null || kv.Value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var replaced = VarRegex.Replace(kv.Value, m =>
                    {
                        var name = m.Groups[1].Value;
                        if (setters.TryGetValue(name, out var v)) return v;
                        if (_variables.TryGetValue(name, out var dv)) return dv;
                        return m.Groups[2].Success ? m.Groups[2].Value : "";
                    });
                    if (replaced != kv.Value)
                    {
                        setters[kv.Key] = replaced;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }

    }
}
