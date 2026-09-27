using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>Inputs for one layout pass (provided by the backend).</summary>
    public sealed class LayoutConstraints
    {
        /// <summary>Available width for the root (ambient width / screen width).</summary>
        public float AvailWidth = 800f;

        /// <summary>Available height for the root; 0 = unbounded.</summary>
        public float AvailHeight = 0f;

        /// <summary>true → root takes the full available width (IMGUI ambient flow);
        /// false → root sizes to its content (UGUI dialogs).</summary>
        public bool RootStretchWidth = true;

        /// <summary>Apply scroll offsets to descendant rects (IMGUI draws in
        /// visible space). UGUI keeps unscrolled content-space rects and lets a
        /// ScrollRect move the content container.</summary>
        public bool ApplyScroll = true;

        /// <summary>Text measurer: (text, fontSize, wrapWidth) → size.
        /// wrapWidth &lt;= 0 means "measure as a single unwrapped line".</summary>
        public Func<string, int, float, Vector2> MeasureText;

        /// <summary>&gt;= 0 forces the root size (UGUI wrapper pinning);
        /// negative keeps the normal width/height/content rules.</summary>
        public float RootWidth = -1f;
        public float RootHeight = -1f;
    }

    /// <summary>
    /// The shared flex-like layout engine. Produces canvas-space rects
    /// (origin top-left, y down) for every node in the tree; both backends
    /// only consume the rects.
    ///
    /// Supported: direction (row/column), gap, padding, margin,
    /// justify-content (start/center/end/space-between/space-around/space-evenly),
    /// align-items / align-self (start/center/end/stretch), flexGrow,
    /// lengths px/%/auto, position:absolute (top/left/right/bottom/x/y/anchor),
    /// display:none (via visible), overflow:hidden, ScrollView with scroll
    /// offsets. No shrink: items overflow rather than compress.
    /// </summary>
    public static class LayoutEngine
    {
        // ── intrinsic control metrics ──────────────────────────────────────
        public const float ButtonMinHeight = 24f;
        public const float ControlHeight = 26f;
        public const float SliderBasis = 120f;
        public const float SliderValueBox = 55f;   // 50 value field + 5 gap
        public const float TextFieldBasis = 120f;
        public const float TextAreaBasis = 200f;
        public const float SwitchW = 40f, SwitchH = 22f;
        public const float SwitchKnobSize = 18f;      // SwitchH - 4
        public const float CheckboxSize = 22f;
        public const float IconSize = 24f;
        public const float ArrowButtonSize = 22f;
        public const float SliderTrackHeight = 6f;
        public const float SliderThumbSize = 14f;
        public const float SliderValueBoxWidth = 50f; // SliderValueBox = this + 5 gap
        public const float TextFieldHeight = ControlHeight;

        private static readonly Rect InfiniteClip = new Rect(-100000f, -100000f, 300000f, 300000f);

        // ── entry point ────────────────────────────────────────────────────

        public static void Layout(ImlNode root, LayoutConstraints c)
        {
            if (root == null || c == null) return;

            Measure(root, c.AvailWidth, c.AvailHeight, c);

            var st = root.Style;
            var wLen = StyleValues.GetLength(st, "width");
            var hLen = StyleValues.GetLength(st, "height");

            float w = c.RootWidth >= 0f
                ? c.RootWidth
                : wLen.Valid && !wLen.Auto
                    ? wLen.Resolve(c.AvailWidth, root.Desired.x)
                    : c.RootStretchWidth
                        ? c.AvailWidth
                        : Mathf.Min(root.Desired.x, c.AvailWidth);
            float h = c.RootHeight >= 0f
                ? c.RootHeight
                : hLen.Valid && !hLen.Auto
                    ? hLen.Resolve(c.AvailHeight, root.Desired.y)
                    : root.Desired.y;

            w = ClampAxis(st, "minWidth", "maxWidth", w, c.AvailWidth);
            h = ClampAxis(st, "minHeight", "maxHeight", h, c.AvailHeight);

            Arrange(root, 0f, 0f, w, h, InfiniteClip, c);
        }

        private static float ClampAxis(ImlStyle st, string minKey, string maxKey, float v, float basis)
        {
            var min = StyleValues.GetLengthValue(st, minKey, basis, 0f);
            var max = StyleValues.GetLengthValue(st, maxKey, basis, float.MaxValue);
            if (max <= 0f || max == float.MaxValue) return Mathf.Max(v, min);
            return Mathf.Clamp(v, min, max);
        }

        // ── helpers ────────────────────────────────────────────────────────

        private static bool IsRow(ImlNode node)
        {
            var dir = StyleValues.GetStr(node.Style, "direction", "");
            if (string.Equals(dir, "row", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(dir, "column", StringComparison.OrdinalIgnoreCase)) return false;
            return node.Tag == "HBox";
        }

        private static bool DisplayNone(ImlNode node)
            => string.Equals(StyleValues.GetStr(node.Style, "display", ""),
                "none", StringComparison.OrdinalIgnoreCase);

        private static void GetPadding(ImlNode node, out float l, out float r, out float t, out float b)
        {
            var st = node.Style;
            l = StyleValues.GetFloat(st, "paddingLeft", 0f);
            r = StyleValues.GetFloat(st, "paddingRight", 0f);
            t = StyleValues.GetFloat(st, "paddingTop", 0f);
            b = StyleValues.GetFloat(st, "paddingBottom", 0f);
        }

        private static void GetMargin(ImlNode node, out float l, out float r, out float t, out float b)
        {
            var st = node.Style;
            l = StyleValues.GetFloat(st, "marginLeft", 0f);
            r = StyleValues.GetFloat(st, "marginRight", 0f);
            t = StyleValues.GetFloat(st, "marginTop", 0f);
            b = StyleValues.GetFloat(st, "marginBottom", 0f);
        }

        private static bool IsAbsolute(ImlNode node)
        {
            var pos = StyleValues.GetStr(node.Style, "position", "");
            return string.Equals(pos, "absolute", StringComparison.OrdinalIgnoreCase);
        }

        private static float GetGap(ImlNode node)
            => StyleValues.GetFloat(node.Style, "gap", 0f);

        private static float GetFlexGrow(ImlNode node, bool row)
        {
            if (node.Kind == ImlNodeKind.Fill) return 1f;
            if (StyleValues.TryParseNumber(StyleValues.GetStr(node.Style, "flexGrow", ""), out var g))
                return g;
            if (row && (node.Kind == ImlNodeKind.TextField || node.Kind == ImlNodeKind.TextArea ||
                        node.Kind == ImlNodeKind.Slider))
                return 1f;
            return 0f;
        }

        /// <summary>Fluid items fill the cross axis under align-items:stretch;
        /// intrinsic controls (buttons, icons, ...) keep their natural size.</summary>
        private static bool IsFluid(ImlNode node)
            => node.Kind == ImlNodeKind.Root || node.Kind == ImlNodeKind.Box ||
               node.Kind == ImlNodeKind.ScrollView || node.Kind == ImlNodeKind.Fill ||
               node.Kind == ImlNodeKind.Separator || node.Kind == ImlNodeKind.Spacer ||
               node.Kind == ImlNodeKind.Text || node.Kind == ImlNodeKind.Link ||
               node.Kind == ImlNodeKind.Image || node.Kind == ImlNodeKind.CustomCanvas;

        private static IEnumerable<ImlNode> FlowChildren(ImlNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsAbsolute || DisplayNone(child)) continue;
                yield return child;
            }
        }

        private static Vector2 MeasureText(LayoutConstraints c, string text, int fontSize, float wrapWidth)
        {
            if (string.IsNullOrEmpty(text)) return new Vector2(0f, fontSize * 1.3f);
            if (c.MeasureText != null) return c.MeasureText(text, fontSize, wrapWidth);
            // Fallback estimate when no measurer is available.
            float w = text.Length * fontSize * 0.62f;
            float h = fontSize * 1.3f;
            if (wrapWidth > 0f && w > wrapWidth)
            {
                int lines = Mathf.CeilToInt(w / wrapWidth);
                return new Vector2(wrapWidth, lines * h);
            }
            return new Vector2(w, h);
        }

        /// <summary>Resolve the cross-axis alignment of one child.</summary>
        private static void ResolveCross(ImlNode child, ImlNode parent, float innerCross,
            bool row, out float size, out float offset, out float margin)
        {
            string align = StyleValues.GetStr(child.Style, "alignSelf", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(align) || align == "inherit" || align == "auto")
                align = StyleValues.GetStr(parent.Style, "alignItems", "stretch").ToLowerInvariant();

            float ml, mr, mt, mb;
            GetMargin(child, out ml, out mr, out mt, out mb);
            margin = row ? mt + mb : ml + mr;

            // definite cross size (height in a row / width in a column)
            float fixedLen = row
                ? StyleValues.GetLengthValue(child.Style, "height", innerCross, -1f)
                : StyleValues.GetLengthValue(child.Style, "width", innerCross, -1f);

            bool fluid = IsFluid(child);
            if (fixedLen >= 0f)
            {
                size = fixedLen;
            }
            else if (align == "stretch" && fluid)
            {
                size = Mathf.Max(0f, innerCross - margin);
                offset = 0f;
                return;
            }
            else
            {
                size = row ? child.Desired.y : child.Desired.x;
            }

            switch (align)
            {
                case "center":
                case "stretch":   // intrinsic item under stretch → center it
                    offset = (innerCross - (size + margin)) / 2f;
                    break;
                case "end":
                    offset = innerCross - (size + margin);
                    break;
                default:
                    offset = 0f;
                    break;
            }
        }

        // ── measure (desired size) ─────────────────────────────────────────

        private static void Measure(ImlNode node, float availW, float availH, LayoutConstraints c)
        {
            if (node == null) return;
            if (DisplayNone(node)) { node.Desired = Vector2.zero; return; }

            var st = node.Style;
            GetPadding(node, out var padL, out var padR, out var padT, out var padB);
            GetMargin(node, out var marL, out var marR, out var marT, out var marB);
            float availContentW = Mathf.Max(0f, availW - marL - marR - padL - padR);
            float availContentH = availH > 0f
                ? Mathf.Max(0f, availH - marT - marB - padT - padB) : 0f;

            Vector2 desired;
            int fontSize = Mathf.RoundToInt(StyleValues.GetFloat(st, "fontSize", 13f));

            switch (node.Kind)
            {
                case ImlNodeKind.Root:
                case ImlNodeKind.Box:
                case ImlNodeKind.ScrollView:
                {
                    bool row = IsRow(node);
                    float gap = GetGap(node);
                    int n = 0;
                    float main = 0f, cross = 0f;
                    foreach (var child in FlowChildren(node))
                    {
                        Measure(child, availContentW, availContentH, c);
                        GetMargin(child, out var cl, out var cr, out var ct, out var cb);
                        float ox = child.Desired.x + cl + cr;
                        float oy = child.Desired.y + ct + cb;
                        if (row) { main += ox; cross = Mathf.Max(cross, oy); }
                        else { main += oy; cross = Mathf.Max(cross, ox); }
                        n++;
                    }
                    if (n > 1) main += gap * (n - 1);
                    desired = row
                        ? new Vector2(main + padL + padR, cross + padT + padB)
                        : new Vector2(cross + padL + padR, main + padT + padB);
                    MeasureAbsolutes(node, availContentW, availContentH, c);
                    break;
                }

                case ImlNodeKind.Text:
                case ImlNodeKind.Link:
                {
                    var whiteSpace = StyleValues.GetStr(st, "whiteSpace", "normal");
                    bool nowrap = string.Equals(whiteSpace, "nowrap", StringComparison.OrdinalIgnoreCase);
                    float wrapWidth = !nowrap && availContentW > 0f ? availContentW : 0f;
                    var size = MeasureText(c, node.Text, fontSize, wrapWidth);
                    desired = new Vector2(size.x + padL + padR, size.y + padT + padB);
                    break;
                }

                case ImlNodeKind.Button:
                {
                    var size = MeasureText(c, node.Text, fontSize, 0f);
                    desired = new Vector2(
                        size.x + padL + padR,
                        Mathf.Max(ButtonMinHeight, size.y + padT + padB));
                    break;
                }

                case ImlNodeKind.TextField:
                    desired = new Vector2(
                        TextFieldBasis + padL + padR,
                        Mathf.Max(ControlHeight, fontSize + 12f + padT + padB));
                    break;

                case ImlNodeKind.TextArea:
                    desired = new Vector2(
                        TextAreaBasis + padL + padR,
                        node.Lines * fontSize * 1.4f + padT + padB + 4f);
                    break;

                case ImlNodeKind.Slider:
                    desired = new Vector2(
                        SliderBasis + (node.ShowValue ? SliderValueBox : 0f) + padL + padR,
                        ControlHeight + padT + padB);
                    break;

                case ImlNodeKind.Switch:
                    desired = new Vector2(SwitchW + padL + padR, SwitchH + padT + padB);
                    break;

                case ImlNodeKind.Checkbox:
                    desired = new Vector2(CheckboxSize + padL + padR, CheckboxSize + padT + padB);
                    break;

                case ImlNodeKind.Icon:
                    desired = new Vector2(IconSize + padL + padR, IconSize + padT + padB);
                    break;

                case ImlNodeKind.ArrowButton:
                    desired = new Vector2(ArrowButtonSize + padL + padR, ArrowButtonSize + padT + padB);
                    break;

                case ImlNodeKind.Separator:
                    desired = new Vector2(0f, 1f);
                    break;

                case ImlNodeKind.Fill:
                case ImlNodeKind.Spacer:
                case ImlNodeKind.Image:
                case ImlNodeKind.CustomCanvas:
                    // intrinsic size comes from width/height style props
                    desired = Vector2.zero;
                    break;

                default:
                    desired = Vector2.zero;
                    break;
            }

            // Fixed size overrides (width/height attributes or style props)
            var wLen = StyleValues.GetLength(st, "width");
            if (wLen.Valid && !wLen.Auto)
                desired.x = wLen.Resolve(availW, desired.x);
            var hLen = StyleValues.GetLength(st, "height");
            if (hLen.Valid && !hLen.Auto)
                desired.y = hLen.Resolve(availH > 0f ? availH : availW, desired.y);

            desired.x = ClampAxis(st, "minWidth", "maxWidth", desired.x, availW);
            desired.y = ClampAxis(st, "minHeight", "maxHeight", desired.y, availH > 0f ? availH : availW);

            node.Desired = desired;
        }

        private static void MeasureAbsolutes(ImlNode node, float availW, float availH, LayoutConstraints c)
        {
            foreach (var child in node.Children)
            {
                if (!child.IsAbsolute || DisplayNone(child)) continue;
                Measure(child, availW, availH, c);
            }
        }

        // ── arrange (rect assignment) ──────────────────────────────────────

        private static void Arrange(ImlNode node, float x, float y, float w, float h,
            Rect parentClip, LayoutConstraints c)
        {
            node.Rect = new Rect(x, y, w, h);

            var st = node.Style;
            GetPadding(node, out var padL, out var padR, out var padT, out var padB);
            bool overflowHidden = string.Equals(
                StyleValues.GetStr(st, "overflow", ""), "hidden", StringComparison.OrdinalIgnoreCase);

            var inner = new Rect(x + padL, y + padT,
                Mathf.Max(0f, w - padL - padR), Mathf.Max(0f, h - padT - padB));

            Rect childClip = overflowHidden
                ? Rect.MinMaxRect(
                    Mathf.Max(parentClip.xMin, inner.xMin), Mathf.Max(parentClip.yMin, inner.yMin),
                    Mathf.Min(parentClip.xMax, inner.xMax), Mathf.Min(parentClip.yMax, inner.yMax))
                : parentClip;
            node.Clip = childClip;

            if (node.Kind == ImlNodeKind.ScrollView)
            {
                ArrangeScrollView(node, inner, childClip, c);
                return;
            }

            bool row = IsRow(node);
            float gap = GetGap(node);

            // ── flex flow ──────────────────────────────────────────────────
            var flow = new List<ImlNode>();
            foreach (var child in FlowChildren(node)) flow.Add(child);

            if (flow.Count > 0)
            {
                float innerMain = row ? inner.width : inner.height;
                float innerCross = row ? inner.height : inner.width;

                var outer = new Vector2[flow.Count];
                float sumMain = 0f;
                for (int i = 0; i < flow.Count; i++)
                {
                    GetMargin(flow[i], out var ml, out var mr, out var mt, out var mb);
                    outer[i] = new Vector2(
                        flow[i].Desired.x + ml + mr, flow[i].Desired.y + mt + mb);
                    sumMain += row ? outer[i].x : outer[i].y;
                }
                float totalGap = gap * Mathf.Max(0, flow.Count - 1);
                float free = innerMain - sumMain - totalGap;

                // flexGrow distribution
                if (free > 0f)
                {
                    float totalGrow = 0f;
                    for (int i = 0; i < flow.Count; i++)
                        totalGrow += GetFlexGrow(flow[i], row);
                    if (totalGrow > 0f)
                    {
                        for (int i = 0; i < flow.Count; i++)
                        {
                            float grow = GetFlexGrow(flow[i], row);
                            if (grow <= 0f) continue;
                            float extra = free * (grow / totalGrow);
                            if (row) outer[i].x += extra; else outer[i].y += extra;
                        }
                        free = 0f;
                    }
                }

                // justify-content (leftover space only)
                float cursor = 0f;
                float usedGap = gap;
                string justify = StyleValues.GetStr(st, "justifyContent", "start")
                    .ToLowerInvariant();
                if (free > 0f)
                {
                    switch (justify)
                    {
                        case "center": cursor = free / 2f; break;
                        case "end": cursor = free; break;
                        case "space-between":
                            usedGap = gap + free / Mathf.Max(1, flow.Count - 1);
                            break;
                        case "space-around":
                            usedGap = gap + free / flow.Count;
                            cursor = usedGap / 2f - gap / 2f;
                            break;
                        case "space-evenly":
                            usedGap = gap + free / (flow.Count + 1);
                            cursor = usedGap / 2f - gap / 2f;
                            break;
                    }
                }

                for (int i = 0; i < flow.Count; i++)
                {
                    var child = flow[i];
                    GetMargin(child, out var ml, out var mr, out var mt, out var mb);

                    ResolveCross(child, node, innerCross, row,
                        out var crossSize, out var crossOffset, out var crossMargin);

                    float cx, cy, cw, ch;
                    if (row)
                    {
                        cx = inner.x + cursor + ml;
                        cy = inner.y + crossOffset + mt;
                        cw = row ? outer[i].x - ml - mr : crossSize;
                        ch = crossSize;
                        cursor += outer[i].x + usedGap;
                    }
                    else
                    {
                        cx = inner.x + crossOffset + ml;
                        cy = inner.y + cursor + mt;
                        cw = crossSize;
                        ch = outer[i].y - mt - mb;
                        cursor += outer[i].y + usedGap;
                    }

                    Arrange(child, cx, cy, cw, ch, childClip, c);
                }
            }

            // ── absolute children ──────────────────────────────────────────
            foreach (var child in node.Children)
            {
                if (!child.IsAbsolute || DisplayNone(child)) continue;
                ArrangeAbsolute(child, inner, childClip, c);
            }
        }

        private static void ArrangeAbsolute(ImlNode node, Rect inner, Rect clip, LayoutConstraints c)
        {
            var st = node.Style;
            float aw = node.Desired.x, ah = node.Desired.y;

            var wLen = StyleValues.GetLength(st, "width");
            var hLen = StyleValues.GetLength(st, "height");
            bool left = StyleValues.TryParseNumber(StyleValues.GetStr(st, "left", ""), out var lv);
            bool right = StyleValues.TryParseNumber(StyleValues.GetStr(st, "right", ""), out var rv);
            bool top = StyleValues.TryParseNumber(StyleValues.GetStr(st, "top", ""), out var tv);
            bool bottom = StyleValues.TryParseNumber(StyleValues.GetStr(st, "bottom", ""), out var bv);

            if (wLen.Valid && !wLen.Auto) aw = wLen.Resolve(inner.width, aw);
            else if (left && right) aw = Mathf.Max(0f, inner.width - lv - rv);
            if (hLen.Valid && !hLen.Auto) ah = hLen.Resolve(inner.height, ah);
            else if (top && bottom) ah = Mathf.Max(0f, inner.height - tv - bv);

            // anchor-based base position (default top-left)
            float px = 0f, py = 0f;
            var anchor = StyleValues.GetStr(st, "anchor", "")
                .ToLowerInvariant().Replace("-", "").Replace("_", "");
            if (!string.IsNullOrEmpty(anchor))
            {
                float ax = 0f, ay = 0f;
                switch (anchor)
                {
                    case "center": ax = 0.5f; ay = 0.5f; break;
                    case "top": ax = 0.5f; ay = 0f; break;
                    case "bottom": ax = 0.5f; ay = 1f; break;
                    case "left": ax = 0f; ay = 0.5f; break;
                    case "right": ax = 1f; ay = 0.5f; break;
                    case "topright": ax = 1f; ay = 0f; break;
                    case "bottomleft": ax = 0f; ay = 1f; break;
                    case "bottomright": ax = 1f; ay = 1f; break;
                    default: break;
                }
                px = ax * Mathf.Max(0f, inner.width - aw);
                py = ay * Mathf.Max(0f, inner.height - ah);
            }

            if (left) px = lv;
            else if (right) px = inner.width - aw - rv;
            if (top) py = tv;
            else if (bottom) py = inner.height - ah - bv;

            px += StyleValues.GetFloat(st, "x", 0f);
            py += StyleValues.GetFloat(st, "y", 0f);

            Arrange(node, inner.x + px, inner.y + py, aw, ah, clip, c);
        }

        private static void ArrangeScrollView(ImlNode node, Rect inner, Rect clip, LayoutConstraints c)
        {
            var st = node.Style;
            bool row = IsRow(node);
            float gap = GetGap(node);

            // content size from desired sizes of children
            int n = 0;
            float main = 0f, cross = 0f;
            foreach (var child in FlowChildren(node))
            {
                GetMargin(child, out var cl, out var cr, out var ct, out var cb);
                float ox = child.Desired.x + cl + cr;
                float oy = child.Desired.y + ct + cb;
                if (row) { main += ox; cross = Mathf.Max(cross, oy); }
                else { main += oy; cross = Mathf.Max(cross, ox); }
                n++;
            }
            if (n > 1) main += gap * (n - 1);

            node.ContentSize = row
                ? new Vector2(main, Mathf.Max(cross, inner.height))
                : new Vector2(cross, main);
            node.ViewportInner = inner;

            float viewMain = row ? inner.width : inner.height;
            float contentMain = row ? node.ContentSize.x : node.ContentSize.y;
            float maxScroll = Mathf.Max(0f, contentMain - viewMain);
            float scroll = Mathf.Clamp(c.ApplyScroll ? node.ScrollPos : 0f, 0f, maxScroll);
            node.ScrollPos = scroll;   // clamped — backends read it back

            float originX = inner.x - (row ? scroll : 0f);
            float originY = inner.y - (row ? 0f : scroll);
            float innerCross = row ? inner.height : inner.width;

            float cursor = 0f;
            foreach (var child in FlowChildren(node))
            {
                GetMargin(child, out var ml, out var mr, out var mt, out var mb);
                ResolveCross(child, node, innerCross, row,
                    out var crossSize, out var crossOffset, out _);

                float cx, cy, cw, ch;
                if (row)
                {
                    cx = originX + cursor + ml;
                    cy = originY + crossOffset + mt;
                    cw = child.Desired.x;
                    ch = crossSize;
                    cursor += child.Desired.x + ml + mr + gap;
                }
                else
                {
                    cx = originX + crossOffset + ml;
                    cy = originY + cursor + mt;
                    cw = crossSize;
                    ch = child.Desired.y;
                    cursor += child.Desired.y + mt + mb + gap;
                }

                Arrange(child, cx, cy, cw, ch, clip, c);
            }
        }
    }
}
