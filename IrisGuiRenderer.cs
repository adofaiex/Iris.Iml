using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// IMGUI backend. Each frame it builds the shared <see cref="ImlNode"/>
    /// tree via <see cref="ImlRuntime"/>, runs the shared <see cref="LayoutEngine"/>
    /// against the ambient available width, then draws at the produced rects.
    /// All interaction (hover/press/pseudo classes) is resolved in draw space.
    /// </summary>
    public class IrisGuiRenderer : IImlRenderer
    {
        private readonly ImlRuntime _rt = new() { DeferEffects = true };

        private readonly Dictionary<string, GUIStyle> _boxStyles = new();
        private readonly Dictionary<string, GUIStyle> _textStyles = new();
        private readonly Dictionary<string, GUIStyle> _measureStyles = new();
        private readonly Dictionary<string, GUIStyle> _buttonStyles = new();
        private readonly Dictionary<string, GUIStyle> _fieldStyles = new();

        private float _scrollGrabOffset;

        // ── IImlRenderer ────────────────────────────────────────────────────

        public string CurrentFilePath => _rt.CurrentFilePath;

        public Action<string> LogDelegate
        {
            get => _rt.LogDelegate;
            set => _rt.LogDelegate = value;
        }

        public void SetDataContext(object data) => _rt.SetDataContext(data);

        public void SetContextValue(string propertyPath, object value)
            => _rt.SetContextValue(propertyPath, value);

        public void RegisterHandler(string name, Action handler)
            => _rt.RegisterHandler(name, handler);

        public void RegisterHandler(string name, Action<object> handler)
            => _rt.RegisterHandler(name, handler);

        public void RegisterHandler<T>(string name, Action<T> handler)
            => _rt.RegisterHandler(name, handler);

        public void RegisterFunction(string name, Func<object[], object> func)
            => _rt.RegisterFunction(name, func);

        public void RegisterDrawHandler(string name, Action<Rect, RendererInternal.DrawArgs> handler)
            => _rt.RegisterDrawHandler(name, handler);

        public void SetHotReload(bool enabled) => _rt.SetHotReload(enabled);

        public void LoadFile(string filePath) => _rt.LoadFile(filePath);

        public void LoadContent(string imlContent, string basePath = "")
            => _rt.LoadContent(imlContent, basePath);

        public void Render(string filePath)
        {
            if (!string.Equals(_rt.CurrentFilePath, filePath, StringComparison.Ordinal))
                _rt.LoadFile(filePath);
            OnGUI();
        }

        // ── frame ───────────────────────────────────────────────────────────

        public void OnGUI()
        {
            if (_rt.Document?.Root == null || _rt.DataContext == null) return;

            var evt = Event.current;
            if (evt != null && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.R &&
                (evt.control || evt.command) && _rt.HotReloadEnabled)
            {
                _rt.RequestReload();
                evt.Use();
            }

            ImlNode root;
            try
            {
                root = _rt.BuildTree();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iris.Iml] Build failed: {ex}");
                return;
            }
            if (root == null) return;

            float avail = ProbeWidth();
            try
            {
                LayoutEngine.Layout(root, new LayoutConstraints
                {
                    AvailWidth = avail,
                    AvailHeight = 0f,
                    RootStretchWidth = true,
                    ApplyScroll = true,
                    MeasureText = MeasureText,
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iris.Iml] Layout failed: {ex}");
                return;
            }

            var area = GUILayoutUtility.GetRect(
                Mathf.Max(1f, root.Rect.width), Mathf.Max(1f, root.Rect.height));
            DrawNode(root, area.position - root.Rect.position);

            _rt.FlushEffects();
        }

        /// <summary>Stretch to whatever width the ambient layout (loader scroll
        /// view / window) offers this frame.</summary>
        private float ProbeWidth()
        {
            var rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            return Mathf.Max(50f, rect.width);
        }

        // ── text measurement (shared layout → IMGUI styles) ─────────────────

        private Vector2 MeasureText(string text, int fontSize, float wrapWidth)
        {
            if (string.IsNullOrEmpty(text)) return new Vector2(0f, fontSize * 1.3f);
            var content = new GUIContent(text);
            var plain = GetMeasureStyle(fontSize, false);
            var single = plain.CalcSize(content);
            if (wrapWidth <= 0f || single.x <= wrapWidth) return single;
            var wrap = GetMeasureStyle(fontSize, true);
            return new Vector2(wrapWidth, wrap.CalcHeight(content, wrapWidth));
        }

        private GUIStyle GetMeasureStyle(int fontSize, bool wrap)
        {
            var key = fontSize + "|" + (wrap ? 1 : 0);
            if (_measureStyles.TryGetValue(key, out var cached)) return cached;
            var gs = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = wrap,
                richText = false,
            };
            _measureStyles[key] = gs;
            return gs;
        }

        // ── draw ────────────────────────────────────────────────────────────

        private static Rect Offset(Rect r, Vector2 tr)
            => new Rect(r.x + tr.x, r.y + tr.y, r.width, r.height);

        private ImlStateFlags ComputeState(ImlNode node, Vector2 tr)
        {
            var state = node.BuildState;
            if (node.Element == null) return state;
            var e = Event.current;
            if (e == null) return state;
            var p = e.mousePosition - tr;
            if (node.Rect.Contains(p) && node.Clip.Contains(p))
            {
                state |= ImlStateFlags.Hover;
                if (Input.GetMouseButton(0)) state |= ImlStateFlags.Press;
            }
            return state;
        }

        private void DrawNode(ImlNode node, Vector2 tr)
        {
            if (node == null || IsHidden(node)) return;
            var r = Offset(node.Rect, tr);

            var state = ComputeState(node, tr);
            ImlStyle style = node.Style;
            if (node.Element != null && state != node.BuildState)
            {
                style = _rt.Styles.Resolve(node.Element, node.Parent?.Style, state,
                    _rt.ResolveAttributeValue);
                // selector options carry a baked selected/unselected look
                if (node.Kind == ImlNodeKind.SelectorOption)
                    style = _rt.ResolveSelectorOptionStyle(style, node.OptionSelected);
            }

            switch (node.Kind)
            {
                case ImlNodeKind.Root:
                case ImlNodeKind.Box:
                    DrawBoxChildren(node, tr, style);
                    break;

                case ImlNodeKind.ScrollView:
                    DrawScrollView(node, tr, style);
                    break;

                case ImlNodeKind.Text:
                    GUI.Label(r, node.Text ?? "", GetTextStyle(style));
                    break;

                case ImlNodeKind.Link:
                    DrawLink(node, r, style, state);
                    break;

                case ImlNodeKind.Image:
                {
                    var tex = _rt.LoadTexture(node.Source);
                    if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
                    else GUI.Box(r, "Loading...");
                    break;
                }

                case ImlNodeKind.Button:
                    if (GUI.Button(r, node.Text ?? "", GetButtonStyle(style)))
                    {
                        var cmd = _rt.ResolveAttributeValue(node.Element, "command");
                        if (!string.IsNullOrEmpty(cmd)) _rt.InvokeCommand(cmd);
                        _rt.HandleElementEvents(node.Element);
                    }
                    break;

                case ImlNodeKind.SelectorOption:
                    if (GUI.Button(r, node.Text ?? "", GetButtonStyle(style)))
                    {
                        _rt.SetContextValue(node.ValueBinding, node.OptionKey);
                        _rt.ScheduleEffect(() =>
                            _rt.InvokeElementEvent(node.Element, "on-changed", node.OptionKey));
                    }
                    break;

                case ImlNodeKind.Switch:
                    DrawSwitch(node, r, style, state);
                    break;

                case ImlNodeKind.Checkbox:
                    DrawCheckbox(node, r, style, state);
                    break;

                case ImlNodeKind.Slider:
                    DrawSlider(node, r, style, state);
                    break;

                case ImlNodeKind.TextField:
                    DrawTextField(node, r, style);
                    break;

                case ImlNodeKind.TextArea:
                    DrawTextArea(node, r, style);
                    break;

                case ImlNodeKind.Separator:
                    DrawSeparator(r, style);
                    break;

                case ImlNodeKind.Icon:
                    DrawIcon(node, r, style, state);
                    break;

                case ImlNodeKind.ArrowButton:
                    DrawArrowButton(node, r, style, state);
                    break;

                case ImlNodeKind.CustomCanvas:
                    if (!string.IsNullOrEmpty(node.OnDraw) &&
                        _rt.DrawHandlers.TryGetValue(node.OnDraw, out var drawHandler))
                    {
                        drawHandler(r, new RendererInternal.DrawArgs { Context = _rt.DataContext });
                    }
                    break;

                // Fill / Spacer: pure layout, nothing to draw
            }
        }

        private void DrawBoxChildren(ImlNode node, Vector2 tr, ImlStyle style)
        {
            var r = Offset(node.Rect, tr);
            DrawBackground(r, style);

            bool clip = string.Equals(StyleValues.GetStr(style, "overflow", ""),
                "hidden", StringComparison.OrdinalIgnoreCase);
            Vector2 trChild = tr;
            if (clip)
            {
                GUI.BeginGroup(r);
                trChild = -node.Rect.position;
            }

            foreach (var c in node.BgChildren) DrawNode(c, trChild);
            foreach (var c in node.Children) DrawNode(c, trChild);
            foreach (var c in node.FgChildren) DrawNode(c, trChild);

            if (clip) GUI.EndGroup();
        }

        // ── scroll view ─────────────────────────────────────────────────────

        private void DrawScrollView(ImlNode node, Vector2 tr, ImlStyle style)
        {
            var r = Offset(node.Rect, tr);
            DrawBackground(r, style);

            var vp = node.ViewportInner;
            var vpDraw = Offset(vp, tr);

            HandleScrollWheel(node, vpDraw);

            GUI.BeginGroup(vpDraw);
            Vector2 trChild = -vp.position;
            foreach (var c in node.BgChildren) DrawNode(c, trChild);
            foreach (var c in node.Children) DrawNode(c, trChild);
            foreach (var c in node.FgChildren) DrawNode(c, trChild);
            GUI.EndGroup();

            DrawScrollBar(node, vpDraw);
            _rt.SetScrollPos(node.ScrollKey, node.ScrollPos);
        }

        private void HandleScrollWheel(ImlNode node, Rect vpDraw)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.ScrollWheel || !vpDraw.Contains(e.mousePosition))
                return;
            float maxScroll = Mathf.Max(0f, node.ContentSize.y - node.ViewportInner.height);
            node.ScrollPos = Mathf.Clamp(node.ScrollPos + e.delta.y * 15f, 0f, maxScroll);
            _rt.SetScrollPos(node.ScrollKey, node.ScrollPos);
            e.Use();
        }

        private void DrawScrollBar(ImlNode node, Rect vpDraw)
        {
            float contentMain = node.ContentSize.y;
            float viewMain = vpDraw.height;
            if (contentMain <= viewMain + 0.5f) return;

            float maxScroll = contentMain - viewMain;
            float scroll = Mathf.Clamp(node.ScrollPos, 0f, maxScroll);

            var barRect = new Rect(vpDraw.xMax - 7f, vpDraw.y + 2f, 5f, vpDraw.height - 4f);
            DrawFlat(new Rect(barRect.x + 1.5f, barRect.y, 2f, barRect.height),
                new Color(0f, 0f, 0f, 0.25f));

            float thumbH = Mathf.Max(20f, barRect.height * viewMain / contentMain);
            float t = maxScroll > 0f ? scroll / maxScroll : 0f;
            float thumbY = barRect.y + t * (barRect.height - thumbH);
            var thumbRect = new Rect(barRect.x, thumbY, barRect.width, thumbH);
            DrawShape(thumbRect, 2, new Color(1f, 1f, 1f, 0.45f), null, 0);

            var e = Event.current;
            if (e == null) return;
            int id = GUIUtility.GetControlID("IrisScrollBar".GetHashCode(), FocusType.Passive, barRect);
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!barRect.Contains(e.mousePosition) || e.button != 0) break;
                    GUIUtility.hotControl = id;
                    if (e.mousePosition.y >= thumbRect.y && e.mousePosition.y <= thumbRect.yMax)
                    {
                        _scrollGrabOffset = e.mousePosition.y - thumbRect.y;
                    }
                    else
                    {
                        _scrollGrabOffset = thumbH / 2f;
                        float dir = e.mousePosition.y < thumbRect.y ? -1f : 1f;
                        scroll = Mathf.Clamp(scroll + dir * viewMain * 0.9f, 0f, maxScroll);
                        node.ScrollPos = scroll;
                        _rt.SetScrollPos(node.ScrollKey, scroll);
                    }
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    {
                        float travel = barRect.height - thumbH;
                        float pos = e.mousePosition.y - barRect.y - _scrollGrabOffset;
                        float ns = travel > 0f ? pos / travel * maxScroll : 0f;
                        node.ScrollPos = Mathf.Clamp(ns, 0f, maxScroll);
                        _rt.SetScrollPos(node.ScrollKey, node.ScrollPos);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }

        // ── controls ────────────────────────────────────────────────────────

        private void DrawSwitch(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            Color fill = node.Checked
                ? StyleValues.GetColor(style, "switchOn", Hex("#D973A5"))
                : StyleValues.GetColor(style, "switchOff", Hex("#313338"));
            Color knob = StyleValues.GetColor(style, "knobColor", Color.white);
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            fill.a *= opacity;
            knob.a *= opacity;

            int radius = Mathf.Max(1, Mathf.RoundToInt(r.height / 2f));
            DrawShape(r, radius, fill, null, 0);

            float d = r.height - 4f;
            float kx = node.Checked ? r.xMax - d - 2f : r.x + 2f;
            var knobTex = GuiTextureFactory.GetCircle(Mathf.RoundToInt(d), knob);
            GUI.DrawTexture(new Rect(kx, r.y + 2f, d, d), knobTex);

            DrawHoverOverlay(r, radius, state);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                Toggle(node);
        }

        private void DrawCheckbox(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            Color bg = node.Checked
                ? StyleValues.GetColor(style, "checkBg", Hex("#D973A5"))
                : StyleValues.GetColor(style, "background", Hex("#313338"));
            Color border = StyleValues.GetColor(style, "borderColor", Hex("#494F5C"));
            Color check = StyleValues.GetColor(style, "checkColor", Color.white);
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            bg.a *= opacity;
            border.a *= opacity;
            check.a *= opacity;

            int radius = 4;
            DrawShape(r, radius, bg, border, 1);
            if (node.Checked)
            {
                var chk = GuiTextureFactory.GetCheckmark(Mathf.RoundToInt(r.width), check);
                GUI.DrawTexture(r, chk);
            }

            DrawHoverOverlay(r, radius, state);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                Toggle(node);
        }

        private void Toggle(ImlNode node)
        {
            var newVal = !node.Checked;
            if (!string.IsNullOrEmpty(node.ValueBinding))
                _rt.SetContextValue(node.ValueBinding, newVal);
            _rt.ScheduleEffect(() => _rt.InvokeElementEvent(node.Element, "on-changed", newVal));
        }

        private void DrawSlider(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            float min = node.Min, max = node.Max;
            float value = node.FloatValue;
            float trackW = r.width - (node.ShowValue ? LayoutEngine.SliderValueBox : 0f);
            if (trackW < 10f) trackW = 10f;

            Color trackC = StyleValues.GetColor(style, "sliderTrack", Hex("#313338"));
            Color fillC = StyleValues.GetColor(style, "sliderFill", Hex("#D973A5"));
            Color thumbC = StyleValues.GetColor(style, "sliderThumb", Color.white);
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            trackC.a *= opacity; fillC.a *= opacity; thumbC.a *= opacity;

            const float trackH = 6f;
            var trackRect = new Rect(r.x, r.y + (r.height - trackH) / 2f, trackW, trackH);
            var hitRect = new Rect(r.x, r.y, trackW, r.height);

            float maxScroll = max - min;
            Func<float, float> posToValue = x =>
            {
                float t = maxScroll > 0f ? Mathf.Clamp01((x - trackRect.x) / trackRect.width) : 0f;
                float v = min + t * maxScroll;
                if (node.Step > 0f)
                    v = min + Mathf.Round((v - min) / node.Step) * node.Step;
                return Mathf.Clamp(v, min, max);
            };

            var e = Event.current;
            int id = GUIUtility.GetControlID("IrisSlider".GetHashCode(), FocusType.Passive, hitRect);
            if (e != null)
            {
                switch (e.type)
                {
                    case EventType.MouseDown:
                        if (e.button == 0 && hitRect.Contains(e.mousePosition))
                        {
                            GUIUtility.hotControl = id;
                            value = posToValue(e.mousePosition.x);
                            e.Use();
                        }
                        break;
                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == id)
                        {
                            value = posToValue(e.mousePosition.x);
                            e.Use();
                        }
                        break;
                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == id)
                        {
                            GUIUtility.hotControl = 0;
                            e.Use();
                        }
                        break;
                }
            }

            // value box
            if (node.ShowValue)
            {
                var vb = new Rect(r.xMax - 50f, r.y + (r.height - 24f) / 2f, 50f, 24f);
                bool isInt = Mathf.Approximately(value, Mathf.Round(value)) && max > 1f;
                var txt = GUI.TextField(vb, value.ToString(isInt ? "F0" : "F2"),
                    GetFieldStyle(style));
                if (StyleValues.TryParseNumber(txt, out var parsed))
                {
                    parsed = Mathf.Clamp(parsed, min, max);
                    if (Mathf.Abs(parsed - value) > 0.001f) value = parsed;
                }
            }

            // draw track + fill + thumb
            float t0 = maxScroll > 0f ? Mathf.Clamp01((value - min) / maxScroll) : 0f;
            DrawShape(trackRect, Mathf.RoundToInt(trackH / 2f), trackC, null, 0);
            if (t0 > 0f)
            {
                DrawShape(new Rect(trackRect.x, trackRect.y, trackRect.width * t0, trackH),
                    Mathf.RoundToInt(trackH / 2f), fillC, null, 0);
            }
            float thumbD = Mathf.Min(r.height, 14f);
            float thumbX = trackRect.x + trackRect.width * t0 - thumbD / 2f;
            var thumbRect = new Rect(thumbX, r.y + (r.height - thumbD) / 2f, thumbD, thumbD);
            GUI.DrawTexture(thumbRect,
                GuiTextureFactory.GetCircle(Mathf.RoundToInt(thumbD), thumbC));

            if ((state & ImlStateFlags.Hover) != 0 || GUIUtility.hotControl == id)
                DrawHoverOverlay(new Rect(hitRect.x, r.y, hitRect.width, r.height),
                    Mathf.RoundToInt(r.height / 2f), state);

            // apply changes
            if (!Mathf.Approximately(value, node.FloatValue))
            {
                node.FloatValue = value;
                if (!string.IsNullOrEmpty(node.ValueBinding))
                    _rt.SetContextValue(node.ValueBinding, value);
                _rt.ScheduleEffect(() => _rt.InvokeElementEvent(node.Element, "on-changed", value));
            }
        }

        private void DrawTextField(ImlNode node, Rect r, ImlStyle style)
        {
            var gs = GetFieldStyle(style);
            var e = Event.current;
            bool submitKey = e != null && e.type == EventType.KeyDown &&
                             (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);

            string newValue = GUI.TextField(r, node.Text ?? "", gs);
            bool changed = newValue != node.Text;
            if (changed)
            {
                node.Text = newValue;
                if (!string.IsNullOrEmpty(node.ValueBinding))
                    _rt.SetContextValue(node.ValueBinding, newValue);
                if (!string.IsNullOrEmpty(node.OnChange))
                    _rt.ScheduleEffect(() =>
                        _rt.InvokeElementEvent(node.Element, "on-changed", newValue));
            }

            // Old semantics: on-text-submit fires while typing and on Return.
            if (!string.IsNullOrEmpty(node.OnSubmit))
            {
                if (changed)
                    _rt.ScheduleEffect(() =>
                        _rt.InvokeElementEvent(node.Element, "on-text-submit", newValue));
                if (submitKey)
                {
                    var submitted = node.Text ?? "";
                    _rt.ScheduleEffect(() =>
                        _rt.InvokeElementEvent(node.Element, "on-text-submit", submitted));
                }
            }
        }

        private void DrawTextArea(ImlNode node, Rect r, ImlStyle style)
        {
            var gs = GetFieldStyle(style);
            string newValue = GUI.TextArea(r, node.Text ?? "", gs);
            if (newValue != node.Text)
            {
                node.Text = newValue;
                if (!string.IsNullOrEmpty(node.ValueBinding))
                    _rt.SetContextValue(node.ValueBinding, newValue);
                if (!string.IsNullOrEmpty(node.OnChange))
                    _rt.ScheduleEffect(() =>
                        _rt.InvokeElementEvent(node.Element, "on-changed", newValue));
            }
        }

        private void DrawSeparator(Rect r, ImlStyle style)
        {
            var c = StyleValues.GetColor(style, "background", Hex("#20FFFFFF"));
            c.a *= StyleValues.GetFloat(style, "opacity", 1f);
            DrawFlat(r, c);
        }

        private void DrawIcon(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            Color circle = StyleValues.GetColor(style, "background", Hex("#494F5C"));
            Color border = StyleValues.GetColor(style, "borderColor", Hex("#313338"));
            Color sym = StyleValues.GetColor(style, "color", Color.white);
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            circle.a *= opacity; border.a *= opacity; sym.a *= opacity;

            int sz = Mathf.Max(4, Mathf.RoundToInt(r.width));
            GUI.DrawTexture(r, GuiTextureFactory.GetCircle(sz, circle, border, 2));
            GUI.DrawTexture(r, GuiTextureFactory.GetIconSymbol(sz, MapIcon(node.IconType), sym));

            if (HasEvents(node.Element))
            {
                DrawHoverOverlay(r, sz / 2, state);
                if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                    _rt.HandleElementEvents(node.Element);
            }
        }

        private void DrawArrowButton(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            var dir = node.Direction switch
            {
                "down" => GuiTextureFactory.ArrowDir.Down,
                "left" => GuiTextureFactory.ArrowDir.Left,
                "up" => GuiTextureFactory.ArrowDir.Up,
                _ => GuiTextureFactory.ArrowDir.Right,
            };
            Color bg = StyleValues.GetColor(style, "background", Hex("#313338"));
            Color border = StyleValues.GetColor(style, "borderColor", Hex("#494F5C"));
            Color arrowC = StyleValues.GetColor(style, "color", Color.white);
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            bg.a *= opacity; border.a *= opacity; arrowC.a *= opacity;
            int radius = Mathf.Max(0, Mathf.RoundToInt(StyleValues.GetFloat(style, "radius", 4f)));
            int sz = Mathf.Max(4, Mathf.RoundToInt(r.width));

            bool clicked;
            if (GuiTextureFactory.TryGetExternalArrowButton(
                    sz, dir, bg, border, 1, radius, arrowC, out var composed))
            {
                var gs = new GUIStyle(GUI.skin.button)
                {
                    border = new RectOffset(radius, radius, radius, radius),
                };
                gs.normal.background = gs.hover.background = gs.active.background = composed;
                clicked = GUI.Button(r, GUIContent.none, gs);
                if ((state & ImlStateFlags.Hover) != 0)
                    DrawHoverOverlay(r, radius, state);
            }
            else
            {
                clicked = GUI.Button(r, GUIContent.none, GetButtonStyle(style));
                GUI.DrawTexture(r, GuiTextureFactory.GetArrow(sz, dir, arrowC));
            }

            if (clicked) _rt.HandleElementEvents(node.Element);
        }

        private void DrawLink(ImlNode node, Rect r, ImlStyle style, ImlStateFlags state)
        {
            bool hover = (state & ImlStateFlags.Hover) != 0;
            Color color = hover
                ? Color.white
                : StyleValues.GetColor(style, "color", Hex("#D973A5"));
            color.a *= StyleValues.GetFloat(style, "opacity", 1f);

            var gs = GetLinkStyle(style, color);
            GUI.Label(r, node.Text ?? "", gs);

            // underline (IMGUI rich text has no <u>)
            var size = gs.CalcSize(new GUIContent(node.Text ?? ""));
            float w = Mathf.Min(size.x, r.width);
            if (w > 1f)
                DrawFlat(new Rect(r.x, r.y + r.height - 2f, w, 1f), color);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none) &&
                !string.IsNullOrEmpty(node.Url))
            {
                Application.OpenURL(node.Url);
            }
        }

        private static GuiTextureFactory.IconSymbol MapIcon(string type)
            => type switch
            {
                "success" => GuiTextureFactory.IconSymbol.Success,
                "warning" => GuiTextureFactory.IconSymbol.Warning,
                "error" => GuiTextureFactory.IconSymbol.Error,
                "stop" => GuiTextureFactory.IconSymbol.Stop,
                _ => GuiTextureFactory.IconSymbol.Information,
            };

        private static bool HasEvents(ImlElement el)
        {
            if (el == null) return false;
            foreach (var kv in el.Attributes)
                if (kv.Key.StartsWith("on-", StringComparison.Ordinal) &&
                    !kv.Key.StartsWith("data-on-", StringComparison.Ordinal))
                    return true;
            return false;
        }

        // ── shape/style helpers ─────────────────────────────────────────────

        private static bool IsHidden(ImlNode node)
            => string.Equals(StyleValues.GetStr(node.Style, "display", ""),
                "none", StringComparison.OrdinalIgnoreCase);

        /// <summary>Draw the element's background color (+ border/radius) if set.</summary>
        private void DrawBackground(Rect r, ImlStyle style)
        {
            if (style == null) return;
            if (!style.Setters.TryGetValue("background", out var bg) || string.IsNullOrEmpty(bg)) return;
            if (!StyleValues.TryParseColor(bg, out var color)) return;
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            color.a *= opacity;
            int radius = Mathf.Max(0, Mathf.RoundToInt(StyleValues.GetFloat(style, "radius", 0f)));
            float bw = StyleValues.GetFloat(style, "borderWidth", 0f);
            Color? border = null;
            if (bw > 0f && StyleValues.TryParseColor(StyleValues.GetStr(style, "borderColor", ""), out var bc))
            {
                bc.a *= opacity;
                border = bc;
            }
            DrawShape(r, radius, color, border, Mathf.RoundToInt(bw));
        }

        private static void DrawFlat(Rect r, Color c)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private void DrawHoverOverlay(Rect r, int radius, ImlStateFlags state)
        {
            if ((state & ImlStateFlags.Hover) == 0) return;
            float a = (state & ImlStateFlags.Press) != 0 ? 0.14f : 0.07f;
            DrawShape(r, radius, new Color(1f, 1f, 1f, a), null, 0);
        }

        /// <summary>Draw a rounded rect via a 9-sliced GUI style (no corner
        /// distortion at any size, one cached texture per shape).</summary>
        private void DrawShape(Rect r, int radius, Color fill, Color? border, int borderWidth)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            if (radius <= 0)
            {
                DrawFlat(r, fill);
                return;
            }
            GUI.Box(r, GUIContent.none, GetBoxStyle(radius, fill, border, borderWidth));
        }

        private GUIStyle GetBoxStyle(int radius, Color fill, Color? border, int borderWidth)
        {
            var key = $"box|{radius}|{fill.r:F3},{fill.g:F3},{fill.b:F3},{fill.a:F3}|" +
                      (border.HasValue
                          ? $"{border.Value.r:F3},{border.Value.g:F3},{border.Value.b:F3},{border.Value.a:F3}"
                          : "-") + "|" + borderWidth;
            if (_boxStyles.TryGetValue(key, out var cached)) return cached;

            int size = Mathf.Max(radius * 2 + 8, 16);
            var tex = GuiTextureFactory.GetRoundedRect(size, size, radius, fill, border, borderWidth);
            var gs = new GUIStyle
            {
                normal = { background = tex },
                border = new RectOffset(radius, radius, radius, radius),
            };
            _boxStyles[key] = gs;
            return gs;
        }

        private GUIStyle GetTextStyle(ImlStyle style)
        {
            int fontSize = Mathf.RoundToInt(StyleValues.GetFloat(style, "fontSize", 13f));
            var align = StyleValues.GetStr(style, "textAlign", "left").ToLowerInvariant();
            bool wrap = !string.Equals(StyleValues.GetStr(style, "whiteSpace", "normal"),
                "nowrap", StringComparison.OrdinalIgnoreCase);
            bool bold = IsBold(style);
            var color = StyleValues.GetColor(style, "color", Hex("#E9ECEF"));
            color.a *= StyleValues.GetFloat(style, "opacity", 1f);

            var key = $"text|{fontSize}|{align}|{wrap}|{bold}|{color.r:F3},{color.g:F3},{color.b:F3},{color.a:F3}";
            if (_textStyles.TryGetValue(key, out var cached)) return cached;

            var gs = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = wrap,
                richText = false,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                alignment = align switch
                {
                    "center" => TextAnchor.MiddleCenter,
                    "right" => TextAnchor.MiddleRight,
                    _ => TextAnchor.MiddleLeft,
                },
            };
            gs.normal.textColor = gs.hover.textColor = gs.active.textColor = color;
            _textStyles[key] = gs;
            return gs;
        }

        private GUIStyle GetLinkStyle(ImlStyle style, Color color)
        {
            int fontSize = Mathf.RoundToInt(StyleValues.GetFloat(style, "fontSize", 13f));
            var key = $"link|{fontSize}|{color.r:F3},{color.g:F3},{color.b:F3},{color.a:F3}";
            if (_textStyles.TryGetValue(key, out var cached)) return cached;

            var gs = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = false,
                richText = false,
            };
            gs.normal.textColor = gs.hover.textColor = gs.active.textColor = color;
            _textStyles[key] = gs;
            return gs;
        }

        private GUIStyle GetButtonStyle(ImlStyle style)
        {
            int fontSize = Mathf.RoundToInt(StyleValues.GetFloat(style, "fontSize", 13f));
            bool bold = IsBold(style);
            var bg = StyleValues.GetColor(style, "background", Hex("#313338"));
            var text = StyleValues.GetColor(style, "color", Hex("#E9ECEF"));
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            bg.a *= opacity; text.a *= opacity;
            int radius = Mathf.Max(0, Mathf.RoundToInt(StyleValues.GetFloat(style, "radius", 8f)));
            float bw = StyleValues.GetFloat(style, "borderWidth", 0f);
            Color? border = null;
            if (bw > 0f && StyleValues.TryParseColor(StyleValues.GetStr(style, "borderColor", ""), out var bc))
            {
                bc.a *= opacity;
                border = bc;
            }
            int bwI = Mathf.RoundToInt(bw);
            int padL = Mathf.RoundToInt(StyleValues.GetFloat(style, "paddingLeft", 10f));
            int padR = Mathf.RoundToInt(StyleValues.GetFloat(style, "paddingRight", 10f));
            int padT = Mathf.RoundToInt(StyleValues.GetFloat(style, "paddingTop", 6f));
            int padB = Mathf.RoundToInt(StyleValues.GetFloat(style, "paddingBottom", 6f));

            var key = $"btn|{fontSize}|{bold}|{bg.r:F3},{bg.g:F3},{bg.b:F3},{bg.a:F3}|" +
                      $"{text.r:F3},{text.g:F3},{text.b:F3},{text.a:F3}|{radius}|{bwI}|" +
                      (border.HasValue
                          ? $"{border.Value.r:F3},{border.Value.g:F3},{border.Value.b:F3},{border.Value.a:F3}"
                          : "-") + $"|{padL},{padR},{padT},{padB}";
            if (_buttonStyles.TryGetValue(key, out var cached)) return cached;

            var gs = new GUIStyle(GUI.skin.button)
            {
                fontSize = fontSize,
                wordWrap = false,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(padL, padR, padT, padB),
            };
            if (radius > 0)
            {
                int size = Mathf.Max(radius * 2 + 8, 16);
                gs.normal.background = GuiTextureFactory.GetRoundedRect(size, size, radius, bg, border, bwI);
                gs.hover.background = GuiTextureFactory.GetRoundedRect(size, size, radius,
                    StyleValues.Multiply(bg, 1.35f), border, bwI);
                gs.active.background = GuiTextureFactory.GetRoundedRect(size, size, radius,
                    StyleValues.Multiply(bg, 0.7f), border, bwI);
                gs.border = new RectOffset(radius, radius, radius, radius);
            }
            gs.focused.background = gs.hover.background;
            gs.normal.textColor = gs.hover.textColor = gs.active.textColor = gs.focused.textColor = text;
            _buttonStyles[key] = gs;
            return gs;
        }

        private GUIStyle GetFieldStyle(ImlStyle style)
        {
            int fontSize = Mathf.RoundToInt(StyleValues.GetFloat(style, "fontSize", 13f));
            var bg = StyleValues.GetColor(style, "background", Hex("#151719"));
            var border = StyleValues.GetColor(style, "borderColor", Hex("#222326"));
            var focus = StyleValues.GetColor(style, "focusBorder", Hex("#D973A5"));
            var text = StyleValues.GetColor(style, "color", Hex("#E9ECEF"));
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            bg.a *= opacity; border.a *= opacity; focus.a *= opacity; text.a *= opacity;
            int radius = Mathf.Max(0, Mathf.RoundToInt(StyleValues.GetFloat(style, "radius", 8f)));
            int bw = Mathf.Max(0, Mathf.RoundToInt(StyleValues.GetFloat(style, "borderWidth", 1f)));

            var key = $"field|{fontSize}|{bg.r:F3},{bg.g:F3},{bg.b:F3},{bg.a:F3}|" +
                      $"{focus.r:F3},{focus.g:F3},{focus.b:F3},{focus.a:F3}|{radius}|{bw}|" +
                      $"{text.r:F3},{text.g:F3},{text.b:F3},{text.a:F3}";
            if (_fieldStyles.TryGetValue(key, out var cached)) return cached;

            var gs = new GUIStyle(GUI.skin.textField)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 4, 4),
                richText = false,
            };
            if (radius > 0)
            {
                int size = Mathf.Max(radius * 2 + 8, 16);
                var normalTex = GuiTextureFactory.GetRoundedRect(size, size, radius, bg, border, bw);
                var focusTex = GuiTextureFactory.GetRoundedRect(size, size, radius, bg, focus, bw);
                gs.normal.background = gs.hover.background = normalTex;
                gs.active.background = gs.focused.background = focusTex;
                gs.border = new RectOffset(radius, radius, radius, radius);
            }
            gs.normal.textColor = gs.hover.textColor = gs.active.textColor = gs.focused.textColor = text;
            _fieldStyles[key] = gs;
            return gs;
        }

        private static bool IsBold(ImlStyle style)
        {
            var weight = StyleValues.GetStr(style, "fontWeight", "");
            if (weight == "bold" || weight == "700" || weight == "800" || weight == "900")
                return true;
            var fs = StyleValues.GetStr(style, "fontStyle", "");
            return fs == "bold";
        }

        private static Color Hex(string s)
            => StyleValues.TryParseColor(s, out var c) ? c : Color.white;
    }
}
