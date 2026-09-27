using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Iris.Iml
{
    /// <summary>
    /// UGUI backend. On <see cref="Rebuild"/> it builds the shared
    /// <see cref="ImlNode"/> tree, runs the shared <see cref="LayoutEngine"/>,
    /// then creates one GameObject per node positioned at the produced rects
    /// (top-left anchored, y-down converted). No LayoutGroups — every rect
    /// comes from the shared layout engine, so IMGUI and UGUI share one
    /// implementation of style + layout.
    /// </summary>
    public class IrisGoRenderer : IImlRenderer
    {
        private readonly ImlRuntime _rt = new() { DeferEffects = false };

        private GameObject _rootObject;
        private GameObject _wrapper;
        private readonly Dictionary<GameObject, ImlNode> _interactive = new();
        private readonly List<RaycastResult> _rayResults = new();
        private ImlNode _hovered;
        private ImlNode _pressed;
        private Text _measurer;
        private Font _defaultFont;

        private static Sprite _flatSprite;
        private static readonly Dictionary<int, Sprite> _roundedSpriteCache = new();
        private static readonly Dictionary<string, Sprite> _bakedSpriteCache = new();

        // ── IImlRenderer passthrough ────────────────────────────────────────

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
            Rebuild();
        }

        public void OnGUI() { } // IMGUI-only interface member

        // ── UGUI surface ────────────────────────────────────────────────────

        public GameObject RootObject
        {
            get => _rootObject;
            set => _rootObject = value;
        }

        public Transform ParentTransform { get; set; }

        /// <summary>
        /// Optional explicit root size. When set, the layout root is forced to
        /// exactly this size and the wrapper is sized to match — consumers can
        /// then pin the wrapper wherever they want (e.g. a 400×50 status bar).
        /// </summary>
        public Vector2? RootSizeOverride { get; set; }

        public void Rebuild()
        {
            _rt.Dirty = false;
            _hovered = null;
            _pressed = null;

            if (_rt.Document?.Root == null || _rt.DataContext == null)
            {
                Log("Rebuild skipped: document or dataContext null");
                return;
            }

            ImlNode root;
            try { root = _rt.BuildTree(); }
            catch (Exception ex) { Debug.LogError($"[Iris.Iml] Build failed: {ex}"); return; }
            if (root == null) return;

            try
            {
                LayoutEngine.Layout(root, new LayoutConstraints
                {
                    AvailWidth = RootSizeOverride?.x ?? Screen.width,
                    AvailHeight = 0f,
                    RootStretchWidth = false,                 // content-sized root
                    ApplyScroll = false,                      // ScrollRect moves content
                    MeasureText = MeasureUGUIText,
                    RootWidth = RootSizeOverride?.x ?? -1f,
                    RootHeight = RootSizeOverride?.y ?? -1f,
                });
            }
            catch (Exception ex) { Debug.LogError($"[Iris.Iml] Layout failed: {ex}"); return; }

            var rootTf = EnsureRoot();
            var rootSize = new Vector2(root.Rect.width, root.Rect.height);

            // Destroy old children, keep index 0 (overlay / consumer placeholder)
            for (int i = rootTf.childCount - 1; i >= 1; i--)
                UnityEngine.Object.Destroy(rootTf.GetChild(i).gameObject);

            _wrapper = new GameObject("DialogWrapper", typeof(RectTransform));
            var wRect = (RectTransform)_wrapper.transform;
            wRect.anchorMin = new Vector2(0.5f, 0.5f);
            wRect.anchorMax = new Vector2(0.5f, 0.5f);
            wRect.pivot = new Vector2(0.5f, 0.5f);
            wRect.anchoredPosition = Vector2.zero;
            wRect.sizeDelta = rootSize;
            _wrapper.transform.SetParent(rootTf, false);

            _interactive.Clear();
            BuildChildren(root, wRect, root.Rect);

            // interaction driver lives on the canvas so it survives rebuilds
            if (_rootObject.GetComponent<ImlDriver>() == null)
            {
                var d = _rootObject.AddComponent<ImlDriver>();
                d.Owner = this;
            }
        }

        /// <summary>Called by the driver each frame: rebuild on dirty + hover tracking.</summary>
        internal void Tick()
        {
            if (_rt.Dirty) Rebuild();
            UpdatePointerState();
        }

        private Transform EnsureRoot()
        {
            if (_rootObject == null)
            {
                _rootObject = new GameObject("IrisCanvas");
                if (ParentTransform != null)
                    _rootObject.transform.SetParent(ParentTransform, false);
                else
                    UnityEngine.Object.DontDestroyOnLoad(_rootObject);

                var canvas = _rootObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32767;
                _rootObject.AddComponent<CanvasScaler>();
                _rootObject.AddComponent<GraphicRaycaster>();

                // Full-screen modal overlay (renderer-created canvases only).
                var bgGo = new GameObject("OverlayBG", typeof(RectTransform));
                var bgImg = bgGo.GetComponent<Image>();
                bgImg.color = new Color(0f, 0f, 0f, 0.5f);
                bgImg.raycastTarget = true;
                bgImg.rectTransform.anchorMin = Vector2.zero;
                bgImg.rectTransform.anchorMax = Vector2.one;
                bgImg.rectTransform.sizeDelta = Vector2.zero;
                bgGo.transform.SetParent(_rootObject.transform, false);
            }
            else
            {
                // Consumer-provided canvas: make sure it can receive events.
                if (_rootObject.GetComponent<Canvas>() == null)
                {
                    var canvas = _rootObject.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                }
                if (_rootObject.GetComponent<GraphicRaycaster>() == null)
                    _rootObject.AddComponent<GraphicRaycaster>();
            }
            return _rootObject.transform;
        }

        // ── build helpers ───────────────────────────────────────────────────

        private static string Name(ImlNode node)
            => string.IsNullOrEmpty(node.Tag) ? node.Kind.ToString() : node.Tag;

        /// <summary>Top-left anchored placement: layout (x, y-down) → UGUI (y-up).</summary>
        private static void PlaceChild(RectTransform rt, Rect rect, Rect parentRect)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(rect.width, rect.height);
            rt.anchoredPosition = new Vector2(
                rect.x - parentRect.x,
                -(rect.y - parentRect.y));
        }

        private RectTransform CreateGO(ImlNode node, Transform parent, Rect parentRect)
        {
            var go = new GameObject(Name(node), typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            PlaceChild(rt, node.Rect, parentRect);
            node.Payload = new UiRefs { Root = go };
            return rt;
        }

        private void BuildChildren(ImlNode parent, Transform tf, Rect parentRect)
        {
            foreach (var c in parent.BgChildren) BuildNode(c, tf, parentRect);
            foreach (var c in parent.Children) BuildNode(c, tf, parentRect);
            foreach (var c in parent.FgChildren) BuildNode(c, tf, parentRect);
        }

        private void BuildNode(ImlNode node, Transform parent, Rect parentRect)
        {
            if (node == null) return;
            switch (node.Kind)
            {
                case ImlNodeKind.Box:
                case ImlNodeKind.Root:
                    BuildBox(node, parent, parentRect);
                    break;
                case ImlNodeKind.ScrollView:
                    BuildScrollView(node, parent, parentRect);
                    break;
                case ImlNodeKind.Text:
                    BuildText(node, parent, parentRect);
                    break;
                case ImlNodeKind.Link:
                    BuildLink(node, parent, parentRect);
                    break;
                case ImlNodeKind.Image:
                    BuildImage(node, parent, parentRect);
                    break;
                case ImlNodeKind.Button:
                case ImlNodeKind.SelectorOption:
                    BuildButton(node, parent, parentRect);
                    break;
                case ImlNodeKind.Switch:
                case ImlNodeKind.Checkbox:
                    BuildToggle(node, parent, parentRect);
                    break;
                case ImlNodeKind.Slider:
                    BuildSlider(node, parent, parentRect);
                    break;
                case ImlNodeKind.TextField:
                case ImlNodeKind.TextArea:
                    BuildInputField(node, parent, parentRect, multiline: node.Kind == ImlNodeKind.TextArea);
                    break;
                case ImlNodeKind.Separator:
                    BuildSeparator(node, parent, parentRect);
                    break;
                case ImlNodeKind.Icon:
                    BuildIcon(node, parent, parentRect);
                    break;
                case ImlNodeKind.ArrowButton:
                    BuildArrowButton(node, parent, parentRect);
                    break;
                // Fill / Spacer / CustomCanvas: pure layout / IMGUI-only → no GO
            }
        }

        private void BuildBox(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;

            if (node.Style.Setters.ContainsKey("background"))
            {
                refs.Background = rt.gameObject.AddComponent<Image>();
                refs.Background.raycastTarget = false;
            }
            if (string.Equals(StyleValues.GetStr(node.Style, "overflow", ""), "hidden", StringComparison.OrdinalIgnoreCase))
                rt.gameObject.AddComponent<RectMask2D>();

            ApplyVisual(node, refs, node.Style, node.BuildState);
            BuildChildren(node, rt, node.Rect);
        }

        private void BuildText(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            refs.Label = rt.gameObject.AddComponent<Text>();
            refs.Label.raycastTarget = false;
            refs.Label.text = node.Text ?? "";
            refs.Label.font = DefaultFont;
            refs.Label.supportRichText = node.Element != null &&
                string.Equals(node.Element.GetString("richText"), "true", StringComparison.OrdinalIgnoreCase);
            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildLink(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;

            refs.Background = rt.gameObject.AddComponent<Image>();   // transparent raycast surface
            refs.Background.color = new Color(0f, 0f, 0f, 0f);
            refs.Background.raycastTarget = true;
            refs.Background.sprite = _flatSprite;

            refs.Label = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
            refs.Label.transform.SetParent(rt, false);
            StretchFull((RectTransform)refs.Label.transform);
            refs.Label.raycastTarget = false;
            refs.Label.text = node.Text ?? "";
            refs.Label.font = DefaultFont;

            // 1px underline strip at the bottom of the text
            var underline = new GameObject("Underline", typeof(RectTransform));
            var underlineRT = (RectTransform)underline.transform;
            underlineRT.SetParent(rt, false);
            underlineRT.anchorMin = new Vector2(0f, 1f);
            underlineRT.anchorMax = new Vector2(0f, 1f);
            underlineRT.pivot = new Vector2(0f, 1f);
            float textW = MeasureUGUIText(node.Text ?? "",
                Mathf.RoundToInt(StyleValues.GetFloat(node.Style, "fontSize", 13f)), 0f).x;
            underlineRT.sizeDelta = new Vector2(Mathf.Min(textW, node.Rect.width), 1f);
            underlineRT.anchoredPosition = new Vector2(0f, -node.Rect.height + 1f);
            refs.Symbol = underline.AddComponent<Image>();
            refs.Symbol.raycastTarget = false;
            refs.Symbol.sprite = _flatSprite;

            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = refs.Background;
            btn.onClick.AddListener(() =>
            {
                if (!string.IsNullOrEmpty(node.Url))
                    Application.OpenURL(node.Url);
                _rt.HandleElementEvents(node.Element);
            });

            _interactive[rt.gameObject] = node;
            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildImage(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var tex = _rt.LoadTexture(node.Source);
            if (tex != null)
            {
                var raw = rt.gameObject.AddComponent<RawImage>();
                raw.texture = tex;
                raw.raycastTarget = false;
            }
        }

        private void BuildButton(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;

            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = true;

            refs.Label = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
            refs.Label.transform.SetParent(rt, false);
            StretchFull((RectTransform)refs.Label.transform);
            refs.Label.raycastTarget = false;
            refs.Label.text = node.Text ?? "";
            refs.Label.font = DefaultFont;
            refs.Label.alignment = TextAnchor.MiddleCenter;
            refs.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
            refs.Label.verticalOverflow = VerticalWrapMode.Overflow;

            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = refs.Background;
            var captured = node;
            btn.onClick.AddListener(() =>
            {
                if (captured.Kind == ImlNodeKind.SelectorOption)
                {
                    _rt.SetContextValue(captured.ValueBinding, captured.OptionKey);
                    _rt.ScheduleEffect(() => _rt.InvokeElementEvent(captured.Element, "on-changed", captured.OptionKey));
                }
                else
                {
                    var cmd = _rt.ResolveAttributeValue(captured.Element, "command");
                    if (!string.IsNullOrEmpty(cmd)) _rt.InvokeCommand(cmd);
                    _rt.HandleElementEvents(captured.Element);
                }
            });

            _interactive[rt.gameObject] = node;
            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildToggle(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = true;

            var captured = node;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = refs.Background;
            btn.onClick.AddListener(() => OnToggleClick(captured));

            if (node.Kind == ImlNodeKind.Switch)
            {
                refs.Knob = new GameObject("Knob", typeof(RectTransform)).AddComponent<Image>();
                var knobRT = (RectTransform)refs.Knob.transform;
                knobRT.SetParent(rt, false);
                knobRT.anchorMin = new Vector2(0f, 0.5f);
                knobRT.anchorMax = new Vector2(0f, 0.5f);
                knobRT.pivot = new Vector2(0.5f, 0.5f);
                refs.Knob.raycastTarget = false;
                refs.Knob.sprite = GetRoundedSprite(Mathf.Max(4, Mathf.RoundToInt(LayoutEngine.SwitchKnobSize)));
                refs.Knob.type = Image.Type.Simple;
            }
            else
            {
                refs.Symbol = new GameObject("Check", typeof(RectTransform)).AddComponent<Image>();
                var chkRT = (RectTransform)refs.Symbol.transform;
                chkRT.SetParent(rt, false);
                StretchFull(chkRT);
                refs.Symbol.raycastTarget = false;
                refs.Symbol.sprite = GetTextureSprite(GuiTextureFactory.GetCheckmark(
                    Mathf.RoundToInt(LayoutEngine.CheckboxSize), Color.white), 0);
            }

            refs.Overlay = new GameObject("Overlay", typeof(RectTransform)).AddComponent<Image>();
            var ovRT = (RectTransform)refs.Overlay.transform;
            ovRT.SetParent(rt, false);
            StretchFull(ovRT);
            refs.Overlay.raycastTarget = false;
            refs.Overlay.color = Color.clear;

            _interactive[rt.gameObject] = node;
            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void OnToggleClick(ImlNode node)
        {
            var newVal = !node.Checked;
            if (!string.IsNullOrEmpty(node.ValueBinding))
            {
                _rt.SetContextValue(node.ValueBinding, newVal);
                node.Checked = newVal;
                if (newVal) node.BuildState |= ImlStateFlags.Checked;
                else node.BuildState &= ~ImlStateFlags.Checked;
                ApplyState(node);
            }
            _rt.ScheduleEffect(() => _rt.InvokeElementEvent(node.Element, "on-changed", newVal));
        }

        private void BuildSlider(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            refs.TrackW = Mathf.Max(10f, node.Rect.width -
                (node.ShowValue ? LayoutEngine.SliderValueBox : 0f));

            // drag surface under everything else
            var drag = new GameObject("DragArea", typeof(RectTransform));
            var dragRT = (RectTransform)drag.transform;
            dragRT.SetParent(rt, false);
            StretchFull(dragRT);
            var dragImg = drag.AddComponent<Image>();
            dragImg.color = new Color(0f, 0f, 0f, 0f);
            dragImg.raycastTarget = true;
            dragImg.sprite = _flatSprite;
            var dragHandler = drag.AddComponent<ImlSliderDrag>();
            dragHandler.Owner = this;
            dragHandler.Node = node;
            dragHandler.Track = null; // assigned below once the track exists
            _interactive[drag] = node;

            var trackGo = new GameObject("Track", typeof(RectTransform));
            var trackRT = (RectTransform)trackGo.transform;
            trackRT.SetParent(rt, false);
            trackRT.anchorMin = new Vector2(0f, 1f);
            trackRT.anchorMax = new Vector2(0f, 1f);
            trackRT.pivot = new Vector2(0f, 1f);
            float trackH = LayoutEngine.SliderTrackHeight;
            trackRT.sizeDelta = new Vector2(refs.TrackW, trackH);
            trackRT.anchoredPosition = new Vector2(0f, -(node.Rect.height - trackH) / 2f);
            refs.Track = trackRT;

            refs.Background = trackGo.AddComponent<Image>();      // track capsule
            refs.Background.raycastTarget = false;

            refs.Fill = new GameObject("Fill", typeof(RectTransform)).AddComponent<Image>();
            var fillRT = (RectTransform)refs.Fill.transform;
            fillRT.SetParent(trackRT, false);
            fillRT.anchorMin = new Vector2(0f, 1f);
            fillRT.anchorMax = new Vector2(0f, 1f);
            fillRT.pivot = new Vector2(0f, 1f);
            fillRT.anchoredPosition = Vector2.zero;
            fillRT.sizeDelta = new Vector2(0f, trackH);
            refs.Fill.raycastTarget = false;

            refs.Symbol = new GameObject("Handle", typeof(RectTransform)).AddComponent<Image>();
            var handleRT = (RectTransform)refs.Symbol.transform;
            handleRT.SetParent(trackRT, false);
            handleRT.anchorMin = new Vector2(0f, 0.5f);
            handleRT.anchorMax = new Vector2(0f, 0.5f);
            handleRT.pivot = new Vector2(0.5f, 0.5f);
            handleRT.sizeDelta = new Vector2(LayoutEngine.SliderThumbSize, LayoutEngine.SliderThumbSize);
            refs.Symbol.raycastTarget = false;
            refs.Symbol.sprite = GetRoundedSprite(Mathf.Max(4, Mathf.RoundToInt(LayoutEngine.SliderThumbSize / 2f)));
            refs.Symbol.type = Image.Type.Simple;

            dragHandler.Track = trackRT;

            if (node.ShowValue)
                BuildSliderValueField(node, rt, refs);

            ApplyVisual(node, refs, node.Style, node.BuildState);
            UpdateSliderVisual(node, refs);
        }

        private void BuildSliderValueField(ImlNode node, RectTransform rt, UiRefs refs)
        {
            var go = new GameObject("Value", typeof(RectTransform));
            var fieldRT = (RectTransform)go.transform;
            fieldRT.SetParent(rt, false);
            fieldRT.anchorMin = new Vector2(0f, 1f);
            fieldRT.anchorMax = new Vector2(0f, 1f);
            fieldRT.pivot = new Vector2(0f, 1f);
            float h = LayoutEngine.TextFieldHeight;
            fieldRT.sizeDelta = new Vector2(LayoutEngine.SliderValueBoxWidth, h);
            fieldRT.anchoredPosition = new Vector2(
                node.Rect.width - LayoutEngine.SliderValueBoxWidth,
                -(node.Rect.height - h) / 2f);

            var img = go.AddComponent<Image>();
            img.sprite = GetBakedSprite(8, ParseHex("#151719"), ParseHex("#222326"), 1);
            img.type = Image.Type.Sliced;
            img.raycastTarget = true;

            var input = go.AddComponent<InputField>();
            input.targetGraphic = img;

            var txtGo = new GameObject("Text", typeof(RectTransform));
            var txtRT = (RectTransform)txtGo.transform;
            txtRT.SetParent(fieldRT, false);
            StretchFull(txtRT);
            txtRT.offsetMin = new Vector2(6f, 3f);
            txtRT.offsetMax = new Vector2(-6f, -3f);
            var txt = txtGo.AddComponent<Text>();
            txt.font = DefaultFont;
            txt.fontSize = 12;
            txt.color = ParseHex("#E9ECEF");
            txt.alignment = TextAnchor.MiddleLeft;
            txt.raycastTarget = false;
            input.textComponent = txt;

            var captured = node;
            input.text = FormatSliderValue(node.FloatValue, node.Max);
            input.onEndEdit.AddListener(v =>
            {
                if (float.TryParse(v, out var parsed))
                {
                    parsed = Mathf.Clamp(parsed, captured.Min, captured.Max);
                    if (!Mathf.Approximately(parsed, captured.FloatValue))
                        OnSliderChanged(captured, parsed);
                }
            });
        }

        internal void OnSliderChanged(ImlNode node, float value)
        {
            if (Mathf.Approximately(node.FloatValue, value)) return;
            node.FloatValue = value;
            if (!string.IsNullOrEmpty(node.ValueBinding))
                _rt.SetContextValue(node.ValueBinding, value);
            if (node.Payload is UiRefs refs) UpdateSliderVisual(node, refs);
            _rt.ScheduleEffect(() => _rt.InvokeElementEvent(node.Element, "on-changed", value));
        }

        private void UpdateSliderVisual(ImlNode node, UiRefs refs)
        {
            if (refs.Track == null) return;
            float range = Mathf.Max(0.0001f, node.Max - node.Min);
            float t = Mathf.Clamp01((node.FloatValue - node.Min) / range);
            if (node.Step > 0f && node.Step < node.Max - node.Min)
            {
                // quantize display position too so the thumb snaps with the value
                float snapped = node.Min + Mathf.Round((node.FloatValue - node.Min) / node.Step) * node.Step;
                t = Mathf.Clamp01((Mathf.Clamp(snapped, node.Min, node.Max) - node.Min) / range);
            }

            if (refs.Fill != null)
                ((RectTransform)refs.Fill.transform).sizeDelta = new Vector2(t * refs.TrackW, LayoutEngine.SliderTrackHeight);

            if (refs.Symbol != null)
            {
                float d = LayoutEngine.SliderThumbSize;
                float cx = Mathf.Clamp(t * refs.TrackW, d / 2f, refs.TrackW - d / 2f);
                ((RectTransform)refs.Symbol.transform).anchoredPosition = new Vector2(cx, 0f);
            }

            if (node.ShowValue)
            {
                var valueGo = refs.Root.transform.Find("Value");
                var input = valueGo != null ? valueGo.GetComponent<InputField>() : null;
                if (input != null && input.textComponent != null)
                    input.text = FormatSliderValue(node.FloatValue, node.Max);
            }
        }

        private static string FormatSliderValue(float value, float max)
        {
            bool isInt = value == Mathf.Round(value) && max > 1f;
            return value.ToString(isInt ? "F0" : "F2");
        }

        private void BuildInputField(ImlNode node, Transform parent, Rect parentRect, bool multiline)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;

            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = true;

            var input = rt.gameObject.AddComponent<InputField>();
            input.targetGraphic = refs.Background;
            input.lineType = multiline
                ? InputField.LineType.MultiLineNewline
                : InputField.LineType.SingleLine;

            refs.Label = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
            refs.Label.transform.SetParent(rt, false);
            StretchFull((RectTransform)refs.Label.transform);
            ((RectTransform)refs.Label.transform).offsetMin = new Vector2(6f, 3f);
            ((RectTransform)refs.Label.transform).offsetMax = new Vector2(-6f, -3f);
            refs.Label.raycastTarget = false;
            refs.Label.text = node.Text ?? "";
            refs.Label.font = DefaultFont;
            refs.Label.alignment = TextAnchor.MiddleLeft;
            refs.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
            refs.Label.verticalOverflow = VerticalWrapMode.Overflow;
            input.textComponent = refs.Label;
            input.text = node.Text ?? "";

            var fx = rt.gameObject.AddComponent<ImlSelectFx>();
            fx.Target = refs.Background;

            var captured = node;
            input.onValueChanged.AddListener(v =>
            {
                captured.Text = v;
                if (!string.IsNullOrEmpty(captured.ValueBinding))
                    _rt.SetContextValue(captured.ValueBinding, v);
                if (!string.IsNullOrEmpty(captured.OnChange))
                    _rt.ScheduleEffect(() => _rt.InvokeElementEvent(captured.Element, "on-changed", v));
            });
            input.onEndEdit.AddListener(v =>
            {
                captured.Text = v;
                if (!string.IsNullOrEmpty(captured.ValueBinding))
                    _rt.SetContextValue(captured.ValueBinding, v);
                if (!string.IsNullOrEmpty(captured.OnSubmit))
                    _rt.ScheduleEffect(() => _rt.InvokeElementEvent(captured.Element, "on-text-submit", v));
            });

            ApplyVisual(node, refs, node.Style, node.BuildState);
            refs.NormalSprite = refs.Background.sprite;
        }

        private void BuildSeparator(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = false;
            refs.Background.sprite = _flatSprite;
            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildIcon(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            int sz = Mathf.RoundToInt(node.Rect.width);
            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = false;

            refs.Symbol = new GameObject("Symbol", typeof(RectTransform)).AddComponent<Image>();
            var symRT = (RectTransform)refs.Symbol.transform;
            symRT.SetParent(rt, false);
            StretchFull(symRT);
            refs.Symbol.raycastTarget = false;

            refs.Overlay = new GameObject("Overlay", typeof(RectTransform)).AddComponent<Image>();
            var ovRT = (RectTransform)refs.Overlay.transform;
            ovRT.SetParent(rt, false);
            StretchFull(ovRT);
            refs.Overlay.raycastTarget = false;
            refs.Overlay.sprite = GetRoundedSprite(sz / 2);
            refs.Overlay.type = Image.Type.Sliced;
            refs.Overlay.color = Color.clear;

            if (HasEvents(node.Element))
            {
                var captured = node;
                var btn = rt.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = refs.Background;
                btn.onClick.AddListener(() => _rt.HandleElementEvents(captured.Element));
                _interactive[rt.gameObject] = node;
            }

            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildArrowButton(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;
            refs.Background = rt.gameObject.AddComponent<Image>();
            refs.Background.raycastTarget = true;

            var captured = node;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = refs.Background;
            btn.onClick.AddListener(() => _rt.HandleElementEvents(captured.Element));
            _interactive[rt.gameObject] = node;

            ApplyVisual(node, refs, node.Style, node.BuildState);
        }

        private void BuildScrollView(ImlNode node, Transform parent, Rect parentRect)
        {
            var rt = CreateGO(node, parent, parentRect);
            var refs = (UiRefs)node.Payload;

            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _flatSprite;
            img.color = new Color(0f, 0f, 0f, 0f);   // transparent raycast surface for wheel input
            img.raycastTarget = true;
            refs.Background = img;
            ApplyVisual(node, refs, node.Style, node.BuildState);
            if (node.Style.Setters.ContainsKey("background")) img.raycastTarget = true;

            rt.gameObject.AddComponent<RectMask2D>();

            var scroll = rt.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.inertia = true;
            scroll.scrollSensitivity = 30f;
            scroll.viewport = rt;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            var content = (RectTransform)contentGo.transform;
            content.SetParent(rt, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;
            // padding is baked into the children positions, so include it here
            float padT = StyleValues.GetFloat(node.Style, "paddingTop", 0f);
            float padB = StyleValues.GetFloat(node.Style, "paddingBottom", 0f);
            float totalH = padT + node.ContentSize.y + padB;
            content.sizeDelta = new Vector2(node.Rect.width, totalH);
            scroll.content = content;

            foreach (var c in node.BgChildren) BuildNode(c, content, node.Rect);
            foreach (var c in node.Children) BuildNode(c, content, node.Rect);
            foreach (var c in node.FgChildren) BuildNode(c, content, node.Rect);

            float viewH = node.Rect.height;
            float maxScroll = Mathf.Max(0f, totalH - viewH);
            float saved = Mathf.Clamp(_rt.GetScrollPos(node.ScrollKey), 0f, maxScroll);
            scroll.verticalNormalizedPosition = maxScroll > 0f ? 1f - saved / maxScroll : 1f;
            scroll.onValueChanged.AddListener(v =>
                _rt.SetScrollPos(node.ScrollKey, (1f - v.y) * maxScroll));
        }

        // ── visual application (build + state refresh share this) ───────────

        private sealed class UiRefs
        {
            public GameObject Root;
            public Image Background;   // box bg / button bg / pill / track / field bg
            public Text Label;         // text / button label / input text
            public Image Symbol;       // icon symbol / arrow / checkmark / slider handle
            public Image Knob;         // switch knob
            public Image Overlay;      // hover/press tint overlay
            public RectTransform Track; // slider track
            public Image Fill;          // slider fill
            public float TrackW;
            public Sprite NormalSprite; // text field normal bg
        }

        private void ApplyState(ImlNode node)
        {
            if (node?.Element == null || !(node.Payload is UiRefs refs)) return;
            var state = node.BuildState;
            if (node == _hovered) state |= ImlStateFlags.Hover;
            if (node == _pressed) state |= ImlStateFlags.Press;
            ImlStyle style;
            if (state == node.BuildState)
            {
                style = node.Style;
            }
            else
            {
                style = _rt.Styles.Resolve(node.Element, node.Parent?.Style, state,
                    _rt.ResolveAttributeValue);
                if (node.Kind == ImlNodeKind.SelectorOption)
                    style = _rt.ResolveSelectorOptionStyle(style, node.OptionSelected);
            }
            ApplyVisual(node, refs, style, state);
        }

        private void ApplyVisual(ImlNode node, UiRefs refs, ImlStyle style, ImlStateFlags state)
        {
            bool hover = (state & ImlStateFlags.Hover) != 0;
            bool press = (state & ImlStateFlags.Press) != 0;
            float opacity = StyleValues.GetFloat(style, "opacity", 1f);
            if (opacity <= 0f && refs.Root != null && node.Kind != ImlNodeKind.ScrollView)
            {
                // treat opacity 0 as invisible but keep layout
                if (refs.Background != null) refs.Background.color = Color.clear;
                if (refs.Label != null) refs.Label.color = Color.clear;
                return;
            }

            switch (node.Kind)
            {
                case ImlNodeKind.Box:
                case ImlNodeKind.Root:
                    if (refs.Background != null) ApplyBoxImage(refs.Background, style, opacity, 1f);
                    break;

                case ImlNodeKind.ScrollView:
                    if (refs.Background != null)
                    {
                        if (node.Style.Setters.ContainsKey("background"))
                            ApplyBoxImage(refs.Background, style, opacity, 1f);
                    }
                    break;

                case ImlNodeKind.Text:
                    ApplyLabelStyle(refs.Label, style, opacity);
                    break;

                case ImlNodeKind.Link:
                {
                    var color = StyleValues.GetColor(style, "color", ParseHex("#D973A5"));
                    if (hover) color = Color.white;
                    color.a *= opacity;
                    if (refs.Label != null) refs.Label.color = color;
                    if (refs.Symbol != null) refs.Symbol.color = color;
                    break;
                }

                case ImlNodeKind.Button:
                case ImlNodeKind.SelectorOption:
                    if (refs.Background != null)
                        ApplyBoxImage(refs.Background, style, opacity, hover ? 1.35f : press ? 0.7f : 1f);
                    ApplyLabelStyle(refs.Label, style, opacity, center: true);
                    break;

                case ImlNodeKind.Switch:
                {
                    bool on = node.Checked;
                    var fill = StyleValues.GetColor(style, on ? "switchOn" : "switchOff",
                        on ? ParseHex("#D973A5") : ParseHex("#313338"));
                    if (hover && !press) fill = Multiply(fill, 1.15f);
                    fill.a *= opacity;
                    if (refs.Background != null) refs.Background.color = fill;
                    if (refs.Knob != null)
                    {
                        var knob = StyleValues.GetColor(style, "knobColor", Color.white);
                        knob.a *= opacity;
                        refs.Knob.color = knob;
                        float d = LayoutEngine.SwitchKnobSize;
                        float x = on ? LayoutEngine.SwitchW - 2f - d / 2f : 2f + d / 2f;
                        ((RectTransform)refs.Knob.transform).anchoredPosition = new Vector2(x, 0f);
                    }
                    if (refs.Overlay != null)
                        refs.Overlay.color = new Color(1f, 1f, 1f, (hover ? 0.07f : 0f) + (press ? 0.08f : 0f));
                    break;
                }

                case ImlNodeKind.Checkbox:
                {
                    bool on = node.Checked;
                    var bg = StyleValues.GetColor(style, on ? "checkBg" : "background",
                        on ? ParseHex("#D973A5") : ParseHex("#313338"));
                    if (hover && !press) bg = Multiply(bg, 1.15f);
                    Color? border = null;
                    if (!on)
                    {
                        var b = StyleValues.GetColor(style, "borderColor", ParseHex("#494F5C"));
                        b.a *= opacity;
                        border = b;
                    }
                    if (refs.Background != null)
                    {
                        int radius = StyleValues.GetInt(style, "radius", 4);
                        if (border.HasValue)
                            refs.Background.sprite = GetBakedSprite(radius, bg, border.Value, 1);
                        else
                        {
                            refs.Background.sprite = GetRoundedSprite(radius);
                            bg.a *= opacity;
                            refs.Background.color = bg;
                        }
                        refs.Background.type = Image.Type.Sliced;
                    }
                    if (refs.Symbol != null)
                    {
                        var check = StyleValues.GetColor(style, "checkColor", Color.white);
                        refs.Symbol.sprite = GetTextureSprite(GuiTextureFactory.GetCheckmark(
                            Mathf.RoundToInt(LayoutEngine.CheckboxSize), check), 0);
                        refs.Symbol.gameObject.SetActive(on);
                    }
                    if (refs.Overlay != null)
                        refs.Overlay.color = new Color(1f, 1f, 1f, (hover ? 0.07f : 0f) + (press ? 0.08f : 0f));
                    break;
                }

                case ImlNodeKind.Icon:
                {
                    var circle = StyleValues.GetColor(style, "background", ParseHex("#494F5C"));
                    circle.a *= opacity;
                    if (refs.Background != null)
                    {
                        var borderC = StyleValues.GetColor(style, "borderColor", ParseHex("#313338"));
                        borderC.a *= opacity;
                        refs.Background.sprite = GetTextureSprite(GuiTextureFactory.GetCircle(
                            Mathf.RoundToInt(node.Rect.width), circle, borderC, 2), 0);
                        refs.Background.type = Image.Type.Simple;
                    }
                    if (refs.Symbol != null)
                    {
                        var sym = StyleValues.GetColor(style, "color", Color.white);
                        sym.a *= opacity;
                        refs.Symbol.sprite = GetTextureSprite(GuiTextureFactory.GetIconSymbol(
                            Mathf.RoundToInt(node.Rect.width), MapIcon(node.IconType), sym), 0);
                        refs.Symbol.type = Image.Type.Simple;
                    }
                    if (refs.Overlay != null)
                        refs.Overlay.color = new Color(1f, 1f, 1f, (hover ? 0.07f : 0f) + (press ? 0.08f : 0f));
                    break;
                }

                case ImlNodeKind.ArrowButton:
                {
                    var dir = MapArrow(node.Direction);
                    var bg = StyleValues.GetColor(style, "background", ParseHex("#313338"));
                    if (hover && !press) bg = Multiply(bg, 1.35f);
                    bg.a *= opacity;
                    var borderC = StyleValues.GetColor(style, "borderColor", ParseHex("#494F5C"));
                    borderC.a *= opacity;
                    if (refs.Background != null)
                    {
                        int radius = StyleValues.GetInt(style, "radius", 4);
                        Texture2D composed = null;
                        if (GuiTextureFactory.TryGetExternalArrowButton(
                                Mathf.RoundToInt(node.Rect.width), dir, bg, borderC, 1, radius,
                                StyleValues.GetColor(style, "color", Color.white), out composed))
                        {
                            refs.Background.sprite = GetTextureSprite(composed, radius);
                        }
                        else
                        {
                            refs.Background.sprite = GetBakedSprite(radius, bg, borderC, 1);
                        }
                        refs.Background.color = Color.white;
                        refs.Background.type = Image.Type.Sliced;
                    }
                    bool external = GuiTextureFactory.ExternalArrowButtonRenderer != null;
                    if (refs.Symbol == null && !external)
                    {
                        // builtin path: draw the arrow ourselves
                        refs.Symbol = new GameObject("Arrow", typeof(RectTransform)).AddComponent<Image>();
                        var arrRT = (RectTransform)refs.Symbol.transform;
                        arrRT.SetParent(refs.Root.transform, false);
                        StretchFull(arrRT);
                        arrRT.SetAsLastSibling();
                        arrRT.offsetMin = new Vector2(2f, 2f);
                        arrRT.offsetMax = new Vector2(-2f, -2f);
                        refs.Symbol.raycastTarget = false;
                    }
                    if (refs.Symbol != null && !external)
                    {
                        var arrow = StyleValues.GetColor(style, "color", Color.white);
                        arrow.a *= opacity;
                        refs.Symbol.sprite = GetTextureSprite(GuiTextureFactory.GetArrow(
                            Mathf.RoundToInt(node.Rect.width), dir, arrow), 0);
                        refs.Symbol.type = Image.Type.Simple;
                    }
                    if (refs.Overlay != null)
                        refs.Overlay.color = new Color(1f, 1f, 1f, (hover ? 0.07f : 0f) + (press ? 0.08f : 0f));
                    break;
                }

                case ImlNodeKind.Slider:
                {
                    var trackC = StyleValues.GetColor(style, "sliderTrack", ParseHex("#313338"));
                    trackC.a *= opacity;
                    var fillC = StyleValues.GetColor(style, "sliderFill", ParseHex("#D973A5"));
                    fillC.a *= opacity;
                    var thumbC = StyleValues.GetColor(style, "sliderThumb", Color.white);
                    if (refs.Background != null)
                    {
                        refs.Background.sprite = GetRoundedSprite(Mathf.RoundToInt(LayoutEngine.SliderTrackHeight / 2f));
                        refs.Background.color = trackC;
                        refs.Background.type = Image.Type.Sliced;
                    }
                    if (refs.Fill != null)
                    {
                        refs.Fill.sprite = GetRoundedSprite(Mathf.RoundToInt(LayoutEngine.SliderTrackHeight / 2f));
                        refs.Fill.color = fillC;
                        refs.Fill.type = Image.Type.Sliced;
                    }
                    if (refs.Symbol != null)
                    {
                        refs.Symbol.sprite = GetRoundedSprite(Mathf.RoundToInt(LayoutEngine.SliderThumbSize / 2f));
                        refs.Symbol.color = thumbC;
                        refs.Symbol.type = Image.Type.Simple;
                    }
                    break;
                }

                case ImlNodeKind.TextField:
                case ImlNodeKind.TextArea:
                {
                    var bg = StyleValues.GetColor(style, "background", ParseHex("#151719"));
                    bg.a *= opacity;
                    var borderC = StyleValues.GetColor(style, "borderColor", ParseHex("#222326"));
                    borderC.a *= opacity;
                    int radius = StyleValues.GetInt(style, "radius", 8);
                    int bw = StyleValues.GetInt(style, "borderWidth", 1);
                    if (refs.Background != null)
                    {
                        refs.Background.sprite = GetBakedSprite(radius, bg, borderC, bw);
                        refs.Background.type = Image.Type.Sliced;
                        refs.Background.color = Color.white;
                    }
                    refs.NormalSprite = refs.Background != null ? refs.Background.sprite : null;
                    var focus = StyleValues.GetColor(style, "focusBorder", ParseHex("#D973A5"));
                    focus.a *= opacity;
                    var fx = refs.Root != null ? refs.Root.GetComponent<ImlSelectFx>() : null;
                    if (fx != null)
                    {
                        fx.Normal = refs.Background != null ? refs.Background.sprite : null;
                        fx.Focused = GetBakedSprite(radius, bg, focus, bw);
                    }
                    ApplyLabelStyle(refs.Label, style, opacity);
                    break;
                }

                case ImlNodeKind.Separator:
                {
                    var c = StyleValues.GetColor(style, "background", ParseHex("#20FFFFFF"));
                    c.a *= opacity;
                    if (refs.Background != null) refs.Background.color = c;
                    break;
                }
            }
        }

        private static void ApplyLabelStyle(Text label, ImlStyle style, float opacity, bool center = false)
        {
            if (label == null) return;
            label.fontSize = Mathf.RoundToInt(StyleValues.GetFloat(style, "fontSize", 13f));
            var color = StyleValues.GetColor(style, "color", ParseHex("#E9ECEF"));
            color.a *= opacity;
            label.color = color;
            label.fontStyle = IsBold(style) ? FontStyle.Bold : FontStyle.Normal;
            bool nowrap = string.Equals(StyleValues.GetStr(style, "whiteSpace", ""), "nowrap", StringComparison.OrdinalIgnoreCase);
            label.horizontalOverflow = nowrap ? HorizontalWrapMode.Overflow : HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            if (center) { label.alignment = TextAnchor.MiddleCenter; return; }
            switch (StyleValues.GetStr(style, "textAlign", "left").ToLowerInvariant())
            {
                case "center": label.alignment = TextAnchor.MiddleCenter; break;
                case "right": label.alignment = TextAnchor.MiddleRight; break;
                default: label.alignment = TextAnchor.MiddleLeft; break;
            }
        }

        private void ApplyBoxImage(Image img, ImlStyle style, float opacity, float brightness)
        {
            var bg = StyleValues.GetColor(style, "background", Color.white);
            if (brightness != 1f) bg = Multiply(bg, brightness);
            bg.a *= opacity;
            int radius = StyleValues.GetInt(style, "radius", 0);
            float bw = StyleValues.GetFloat(style, "borderWidth", 0f);
            if (bw > 0f && StyleValues.TryParseColor(StyleValues.GetStr(style, "borderColor", ""), out var borderC))
            {
                borderC.a *= opacity;
                img.sprite = GetBakedSprite(radius, bg, borderC, Mathf.RoundToInt(bw));
                img.color = Color.white;
            }
            else if (radius > 0)
            {
                img.sprite = GetRoundedSprite(radius);
                img.color = bg;
            }
            else
            {
                img.sprite = _flatSprite;
                img.color = bg;
            }
            img.type = radius > 0 || bw > 0f ? Image.Type.Sliced : Image.Type.Simple;
        }

        private static bool IsBold(ImlStyle style)
        {
            var fw = StyleValues.GetStr(style, "fontWeight", "").ToLowerInvariant();
            if (fw.Contains("bold") || fw == "700" || fw == "800" || fw == "900") return true;
            var fs = StyleValues.GetStr(style, "fontStyle", "").ToLowerInvariant();
            return fs.Contains("bold");
        }

        private static Color Multiply(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f, c.a);

        private static Color ParseHex(string hex)
            => StyleValues.TryParseColor(hex, out var c) ? c : Color.white;

        private static GuiTextureFactory.IconSymbol MapIcon(string kind) => kind switch
        {
            "success" => GuiTextureFactory.IconSymbol.Success,
            "warning" => GuiTextureFactory.IconSymbol.Warning,
            "error" => GuiTextureFactory.IconSymbol.Error,
            "stop" => GuiTextureFactory.IconSymbol.Stop,
            _ => GuiTextureFactory.IconSymbol.Information,
        };

        private static GuiTextureFactory.ArrowDir MapArrow(string dir) => dir switch
        {
            "down" => GuiTextureFactory.ArrowDir.Down,
            "left" => GuiTextureFactory.ArrowDir.Left,
            "up" => GuiTextureFactory.ArrowDir.Up,
            _ => GuiTextureFactory.ArrowDir.Right,
        };

        private static bool HasEvents(ImlElement el)
        {
            if (el == null) return false;
            foreach (var kv in el.Attributes)
                if (kv.Key.StartsWith("on-") && !kv.Key.StartsWith("data-on-"))
                    return true;
            return false;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ── pointer state (hover/press pseudo classes) ──────────────────────

        private void UpdatePointerState()
        {
            ImlNode hovered = null;
            var es = EventSystem.current;
            if (es != null && _interactive.Count > 0)
            {
                _rayResults.Clear();
                es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, _rayResults);
                foreach (var res in _rayResults)
                {
                    if (res.gameObject != null && _interactive.TryGetValue(res.gameObject, out var n))
                    {
                        hovered = n;
                        break;
                    }
                }
            }

            var prevH = _hovered;
            var prevP = _pressed;
            _hovered = hovered;
            _pressed = hovered != null && Input.GetMouseButton(0) ? hovered : null;
            if (prevH == _hovered && prevP == _pressed) return;

            if (prevH != null && prevH != _hovered && prevH != _pressed) ApplyState(prevH);
            if (prevP != null && prevP != _pressed && prevP != _hovered) ApplyState(prevP);
            if (_hovered != null && _hovered != prevH) ApplyState(_hovered);
            if (_pressed != null && _pressed != prevP && _pressed != _hovered) ApplyState(_pressed);
        }

        // ── fonts & text measurement ────────────────────────────────────────

        private Font DefaultFont
        {
            get
            {
                if (_defaultFont == null)
                {
                    _defaultFont = TryLoadCjkFont();
                    if (_defaultFont == null)
                        _defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                return _defaultFont;
            }
        }

        /// <summary>
        /// Built-in fonts (Arial) have no CJK glyphs, so Chinese/Japanese/Korean
        /// text renders blank. Prefer a system CJK font when one is available.
        /// </summary>
        private static Font TryLoadCjkFont()
        {
            string[] candidates = {
                "Noto Sans CJK SC", "Noto Sans CJK TC", "Noto Sans CJK JP", "Noto Sans CJK KR",
                "WenQuanYi Micro Hei", "WenQuanYi Zen Hei", "Droid Sans Fallback",
                "Microsoft YaHei", "PingFang SC", "Source Han Sans SC", "SimHei"
            };
            foreach (var name in candidates)
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 14);
                    if (f != null) return f;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Deterministic text measurement via per-character font advances
        /// (independent of TextGenerator state). Line height uses a 1.35×
        /// factor, slightly generous so wrapping never under-allocates height.
        /// </summary>
        private Vector2 MeasureUGUIText(string text, int fontSize, float wrapWidth)
        {
            if (string.IsNullOrEmpty(text)) return new Vector2(0f, fontSize * 1.3f);
            var font = DefaultFont;
            if (font == null)
                return new Vector2(text.Length * fontSize * 0.55f, fontSize * 1.35f);

            try { font.RequestCharactersInTexture(text, fontSize, FontStyle.Normal); }
            catch { /* non-dynamic font: fall through to per-char lookup */ }

            const float lineHFactor = 1.35f;
            float lineH = fontSize * lineHFactor;
            float maxLineW = 0f, lineW = 0f;
            int lines = 1;
            foreach (var ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    maxLineW = Mathf.Max(maxLineW, lineW);
                    lineW = 0f;
                    lines++;
                    continue;
                }
                float adv;
                if (font.GetCharacterInfo(ch, out var ci, fontSize, FontStyle.Normal))
                    adv = ci.advance;
                else
                    adv = ch >= 0x2E80 ? fontSize : fontSize * 0.55f;  // CJK vs latin estimate
                if (wrapWidth > 0f && lineW > 0f && lineW + adv > wrapWidth)
                {
                    maxLineW = Mathf.Max(maxLineW, lineW);
                    lineW = 0f;
                    lines++;
                }
                lineW += adv;
            }
            maxLineW = Mathf.Max(maxLineW, lineW);
            float width = wrapWidth > 0f && lines > 1 ? Mathf.Min(maxLineW, wrapWidth) : maxLineW;
            return new Vector2(width, lines * lineH);
        }

        // ── sprite cache ────────────────────────────────────────────────────

        private static Sprite FlatSprite
        {
            get
            {
                if (_flatSprite != null) return _flatSprite;
                var tex = Texture2D.whiteTexture;
                _flatSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                _flatSprite.name = "IrisFlat";
                _flatSprite.hideFlags = HideFlags.HideAndDontSave;
                return _flatSprite;
            }
        }

        /// <summary>White rounded-corner 9-slice sprite (tint via Image.color).</summary>
        private static Sprite GetRoundedSprite(int radius)
        {
            if (radius <= 0) return FlatSprite;
            if (_roundedSpriteCache.TryGetValue(radius, out var cached) && cached != null) return cached;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"IrisRounded_{radius}",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            float r = radius;
            float r2 = r * r;
            float innerR2 = (r - 1.5f) * (r - 1.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int dx = 0, dy = 0;
                    if (x < radius) dx = radius - x;
                    else if (x >= size - radius) dx = x - (size - radius - 1);
                    if (y < radius) dy = radius - y;
                    else if (y >= size - radius) dy = y - (size - radius - 1);
                    float distSq = dx * dx + dy * dy;
                    byte alpha;
                    if (distSq <= innerR2) alpha = 255;
                    else if (distSq < r2)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        alpha = (byte)Mathf.Clamp((r - dist + 0.5f) * 255f, 0f, 255f);
                    }
                    else alpha = 0;
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sprite.name = $"IrisRounded_{radius}";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _roundedSpriteCache[radius] = sprite;
            return sprite;
        }

        /// <summary>Sprite with baked fill + border colors (for bordered controls).</summary>
        private static Sprite GetBakedSprite(int radius, Color fill, Color border, int borderWidth)
        {
            string key = $"{radius}|{fill.r:F3},{fill.g:F3},{fill.b:F3},{fill.a:F3}" +
                         $"|{border.r:F3},{border.g:F3},{border.b:F3},{border.a:F3}|{borderWidth}";
            if (_bakedSpriteCache.TryGetValue(key, out var cached) && cached != null) return cached;

            radius = Mathf.Max(radius, 0);
            int size = Mathf.Max(radius * 2 + 8, 16);
            var tex = GuiTextureFactory.GetRoundedRect(size, size, radius, fill, border, Mathf.Max(0, borderWidth));
            var border4 = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border4);
            sprite.name = "IrisBaked";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _bakedSpriteCache[key] = sprite;
            return sprite;
        }

        private static Sprite GetTextureSprite(Texture2D tex, int radius)
        {
            string key = $"tex|{tex.GetInstanceID()}|{radius}";
            if (_bakedSpriteCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sprite.name = "IrisTextureSprite";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _bakedSpriteCache[key] = sprite;
            return sprite;
        }

        private void Log(string message)
        {
            if (_rt.LogDelegate != null) _rt.LogDelegate(message);
            else Debug.Log($"[Iris.Iml] {message}");
        }
    }

    // ── helper MonoBehaviours ───────────────────────────────────────────────

    /// <summary>Per-frame tick: dirty rebuilds + hover/press state tracking.</summary>
    internal sealed class ImlDriver : MonoBehaviour
    {
        public IrisGoRenderer Owner;

        private void Update()
        {
            if (Owner != null) Owner.Tick();
        }
    }

    /// <summary>Custom slider drag handling (independent of Unity's Slider layout).</summary>
    internal sealed class ImlSliderDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public IrisGoRenderer Owner;
        public ImlNode Node;
        public RectTransform Track;

        public void OnPointerDown(PointerEventData eventData) => Apply(eventData);
        public void OnDrag(PointerEventData eventData) => Apply(eventData);

        private void Apply(PointerEventData eventData)
        {
            if (Owner == null || Node == null) return;
            var rt = Track != null ? Track : (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rt, eventData.position, eventData.pressEventCamera, out var local))
                return;
            float w = rt.rect.width;
            if (w <= 0f) return;
            float t = Mathf.Clamp01(local.x / w);
            float value = Node.Min + t * (Node.Max - Node.Min);
            if (Node.Step > 0f)
                value = Node.Min + Mathf.Round((value - Node.Min) / Node.Step) * Node.Step;
            Owner.OnSliderChanged(Node, Mathf.Clamp(value, Node.Min, Node.Max));
        }
    }

    /// <summary>Swaps a text field's background sprite while focused.</summary>
    internal sealed class ImlSelectFx : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        public Image Target;
        public Sprite Normal;
        public Sprite Focused;

        public void OnSelect(BaseEventData eventData)
        {
            if (Target != null && Focused != null) Target.sprite = Focused;
        }

        public void OnDeselect(BaseEventData eventData)
        {
            if (Target != null && Normal != null) Target.sprite = Normal;
        }
    }
}
