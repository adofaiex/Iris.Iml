using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Iris.Iml
{
    public class IrisGoRenderer : IImlRenderer
    {
        private readonly ImlParser _parser = new();
        private ImlDocument _document;
        private IBindingContext _dataContext;
        private ExpressionEvaluator _evaluator;
        private readonly Dictionary<string, Action> _handlers = new();
        private readonly Dictionary<string, Action<object>> _genericHandlers = new();
        private readonly Dictionary<string, Action<Rect, RendererInternal.DrawArgs>> _drawHandlers = new();
        private readonly Dictionary<string, Texture2D> _textureCache = new();
        private readonly Dictionary<string, ImlStyle> _styleCache = new();
        private readonly Dictionary<string, Func<object[], object>> _registeredFunctions = new();

        private IIrrLayout _layout;
        private GameObject _rootObject;
        private readonly Dictionary<string, GameObject> _elementMap = new();
        private bool _dirty = true;
        private Font _defaultFont;

        public string CurrentFilePath { get; private set; }
        public Action<string> LogDelegate { get; set; }

        /// <summary>Set the root transform to parent UI under (e.g. a Canvas).</summary>
        public GameObject RootObject { get => _rootObject; set => _rootObject = value; }

        public void SetLayout(IIrrLayout layout) => _layout = layout;

        /// <summary>Optional parent transform for the root Canvas. If set, Canvas will be parented here.</summary>
        public Transform ParentTransform { get; set; }

        private Font DefaultFont
        {
            get
            {
                if (_defaultFont == null)
                    _defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf") ?? DefaultFont;
                return _defaultFont;
            }
        }

        // ---- Sprite cache ----
        // UGUI Image requires a non-null sprite to render. AddComponent<Image>() defaults
        // sprite to null, which is why the dialog/button/icon backgrounds all came out
        // invisible. We cache a flat 1x1 white sprite (for solid color panels) and a
        // procedurally generated rounded-corner sprite per radius (for 9-slice corners).
        private static Sprite _flatSprite;
        private static readonly Dictionary<int, Sprite> _roundedSpriteCache = new();

        private static Sprite FlatSprite
        {
            get
            {
                if (_flatSprite != null) return _flatSprite;
                var tex = Texture2D.whiteTexture;
                _flatSprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f, 0, SpriteMeshType.FullRect);
                _flatSprite.name = "IrisFlat";
                _flatSprite.hideFlags = HideFlags.HideAndDontSave;
                return _flatSprite;
            }
        }

        private static Sprite GetRoundedSprite(int radius)
        {
            if (radius <= 0) return FlatSprite;
            if (_roundedSpriteCache.TryGetValue(radius, out var cached)) return cached;

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

                    float distSq = (float)(dx * dx + dy * dy);
                    byte alpha;
                    if (distSq <= innerR2)
                    {
                        alpha = 255;
                    }
                    else if (distSq < r2)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        alpha = (byte)Mathf.Clamp((r - dist + 0.5f) * 255f, 0f, 255f);
                    }
                    else
                    {
                        alpha = 0;
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            // 9-slice with border = radius so corners keep their shape at any size.
            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect,
                border);
            sprite.name = $"IrisRounded_{radius}";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _roundedSpriteCache[radius] = sprite;
            return sprite;
        }

        /// <summary>Apply background color + sprite to a freshly created Image, reading radius from the style.</summary>
        private static void ApplyBackgroundStyle(Image img, ImlStyle style)
        {
            if (style?.Setters == null) { img.sprite = FlatSprite; return; }

            if (style.Setters.TryGetValue("background", out var bg) &&
                !string.IsNullOrEmpty(bg) &&
                bg.StartsWith("#") &&
                ColorUtility.TryParseHtmlString(bg, out var bgColor))
            {
                img.color = bgColor;
            }
            img.raycastTarget = false;

            if (style.Setters.TryGetValue("radius", out var radStr) &&
                int.TryParse(radStr, out var rad) && rad > 0)
            {
                img.sprite = GetRoundedSprite(rad);
                img.type = Image.Type.Sliced;
            }
            else
            {
                img.sprite = FlatSprite;
            }
        }

        private void Log(string message)
        {
            if (LogDelegate != null) LogDelegate(message);
            else Debug.Log($"[Iris.Iml] {message}");
        }

        public void SetDataContext(object data)
        {
            _dataContext = new BindingContext(data);
            _evaluator = new ExpressionEvaluator(_dataContext as BindingContext ?? new BindingContext(data));
            _dataContext.PropertyChanged += OnDataContextPropertyChanged;
            foreach (var kv in _registeredFunctions)
                _evaluator.RegisterFunction(kv.Key, kv.Value);
            _dirty = true;
        }

        public void RegisterHandler(string name, Action handler) => _handlers[name] = handler;

        public void RegisterHandler(string name, Action<object> handler) => _genericHandlers[name] = handler;

        public void RegisterFunction(string name, Func<object[], object> func)
        {
            _registeredFunctions[name] = func;
            _evaluator?.RegisterFunction(name, func);
        }

        public void RegisterDrawHandler(string name, Action<Rect, RendererInternal.DrawArgs> handler)
        {
            _drawHandlers[name] = handler;
        }

        public void SetHotReload(bool enabled) { }

        public void LoadFile(string filePath)
        {
            CurrentFilePath = filePath;
            _document = _parser.Parse(filePath);
            ProcessResources();
            _dirty = true;
        }

        public void LoadContent(string imlContent, string basePath = "")
        {
            _document = _parser.ParseContent(imlContent, basePath);
            ProcessResources();
            _dirty = true;
        }

        public void Render(string filePath)
        {
            if (!string.Equals(CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                LoadFile(filePath);
            RebuildUI();
        }

        /// <summary>Rebuild the UI from the currently loaded document. Use after LoadContent
        /// to render an in-memory IML string (e.g. dynamically generated) without a file on disk.</summary>
        public void Rebuild() => RebuildUI();

        public void OnGUI() { } // No-op for GO renderer

        private void ProcessResources() 
        { 
            if (_document?.Root == null) return;
            // Clear before repopulating so a subsequent LoadFile doesn't carry over styles
            // from the previous IML file.
            _styleCache.Clear();
            foreach (var child in _document.Root.Children)
                if (child is ImlElement e && e.TagName == "Resources")
                    ProcessResourceElement(e);
        }

        private void ProcessResourceElement(ImlElement element)
        {
            foreach (var child in element.Children)
                if (child is ImlElement ce && ce.TagName == "Style")
                {
                    var style = ParseStyle(ce);
                    _styleCache[style.Name.ToLowerInvariant()] = style;
                }
        }

        private ImlStyle ParseStyle(ImlElement element)
        {
            var style = new ImlStyle { Name = element.GetString("name"), Extends = element.GetString("extends") };
            foreach (var child in element.Children)
                if (child is ImlElement ce)
                {
                    if (ce.TagName == "Setter")
                    {
                        var prop = ce.GetString("property");
                        if (!string.IsNullOrEmpty(prop)) style.Setters[prop] = ce.GetString("value") ?? "";
                    }
                    else
                    {
                        var val = ce.GetString("value");
                        if (!string.IsNullOrEmpty(val)) style.Setters[ce.TagName] = val;
                    }
                }
            return style;
        }

        private Transform EnsureRoot()
        {
            if (_rootObject == null)
            {
                _rootObject = new GameObject("IrisCanvas");
                _rootObject.transform.SetParent(ParentTransform, false);
                UnityEngine.Object.DontDestroyOnLoad(_rootObject);
                var canvas = _rootObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32767;
                _rootObject.AddComponent<CanvasScaler>();
                _rootObject.AddComponent<GraphicRaycaster>();

                // Full-screen overlay background
                var bgGo = new GameObject("OverlayBG");
                var bgImg = bgGo.AddComponent<Image>();
                bgImg.color = new Color(0, 0, 0, 0.5f);
                var bgRect = bgImg.rectTransform;
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.sizeDelta = Vector2.zero;
                bgGo.transform.SetParent(_rootObject.transform, false);
            }
            return _rootObject.transform;
        }

        private void RebuildUI()
        {
            if (_document?.Root == null || _dataContext == null)
            {
                Debug.LogWarning("[IrisGoRenderer] RebuildUI skipped: document or dataContext null");
                return;
            }

            var root = EnsureRoot();

            // Destroy old children (keep overlay BG at index 0)
            for (int i = root.childCount - 1; i >= 1; i--)
                UnityEngine.Object.Destroy(root.GetChild(i).gameObject);

            try
            {
                // DialogWrapper: anchored at screen center, sized by its content's preferred size.
                // No LayoutGroup on the wrapper — we rely on anchored centering + ContentSizeFitter,
                // which is the most reliable way to center a dialog in UGUI.
                var wrapperGo = new GameObject("DialogWrapper");
                var wrapperRect = wrapperGo.AddComponent<RectTransform>();
                wrapperRect.anchorMin = new Vector2(0.5f, 0.5f);
                wrapperRect.anchorMax = new Vector2(0.5f, 0.5f);
                wrapperRect.pivot = new Vector2(0.5f, 0.5f);
                wrapperRect.anchoredPosition = Vector2.zero;
                wrapperRect.sizeDelta = Vector2.zero;
                var wrapperCsf = wrapperGo.AddComponent<ContentSizeFitter>();
                wrapperCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                wrapperCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                wrapperGo.transform.SetParent(root, false);

                BuildElement(_document.Root, wrapperGo.transform);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IrisGoRenderer] RebuildUI error: {ex}");
            }
            _dirty = false;
        }

        private void BuildElement(ImlElement element, Transform parent)
        {
            if (element == null) return;

            // Handle If conditions
            if (element.TagName == "If")
            {
                var cond = element.GetExpression("condition");
                if (!string.IsNullOrEmpty(cond) && !_evaluator.EvaluateBoolean(cond))
                    return;
            }

            // Handle visible
            if (element.HasAttribute("visible"))
            {
                var vis = element.GetExpression("visible");
                if (!string.IsNullOrEmpty(vis) && !_evaluator.EvaluateBoolean(vis))
                    return;
            }

            switch (element.TagName)
            {
                case "Iris":
                case "If":
                    BuildChildren(element, parent);
                    break;

                case "HBox":
                case "VBox":
                case "View":
                    BuildContainer(element, parent);
                    break;

                case "Text":
                    BuildText(element, parent);
                    break;

                case "Button":
                    BuildButton(element, parent);
                    break;

                case "Switch":
                case "Checkbox":
                    BuildToggle(element, parent);
                    break;

                case "Slider":
                    BuildSlider(element, parent);
                    break;

                case "TextField":
                    BuildTextField(element, parent);
                    break;

                case "Separator":
                    BuildSeparator(element, parent);
                    break;

                case "Spacer":
                case "Box":
                    BuildSpacer(element, parent);
                    break;

                case "Icon":
                    BuildIcon(element, parent);
                    break;

                case "Image":
                    BuildImage(element, parent);
                    break;

                case "ScrollView":
                    BuildScrollView(element, parent);
                    break;

                case "Fill":
                    // Flexible space: add an expanding layout element.
                    // In a HorizontalLayoutGroup, only flexibleWidth matters;
                    // in a VerticalLayoutGroup, only flexibleHeight matters.
                    // Set both so <Fill /> works in either direction.
                    var fill = new GameObject("Fill", typeof(LayoutElement));
                    var leFill = fill.GetComponent<LayoutElement>();
                    var parentLg = parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
                    if (parentLg is HorizontalLayoutGroup)
                    {
                        leFill.flexibleWidth = 1;
                    }
                    else if (parentLg is VerticalLayoutGroup)
                    {
                        leFill.flexibleHeight = 1;
                    }
                    else
                    {
                        // Fallback: expand both axes
                        leFill.flexibleWidth = 1;
                        leFill.flexibleHeight = 1;
                    }
                    fill.transform.SetParent(parent, false);
                    break;

                case "Reference":
                    // References are rendered inline during OnGUI in GuiRenderer
                    // For GO renderer, skip for now
                    break;

                // Meta tags — skip
                case "Resources":
                case "Style":
                case "Template":
                case "StyleSelector":
                case "Case":
                case "Slot":
                case "References":
                    break;

                default:
                    Debug.LogWarning($"[Iris.Iml] Unknown element: {element.TagName}");
                    break;
            }
        }

        private void BuildChildren(ImlElement element, Transform parent)
        {
            foreach (var child in element.Children)
            {
                if (child is ImlElement e) BuildElement(e, parent);
                else if (child is string t && !string.IsNullOrWhiteSpace(t))
                {
                    var go = new GameObject("Text");
                    var txt = go.AddComponent<Text>();
                    txt.text = t;
                    txt.color = Color.white;
                    txt.font = DefaultFont;
                    txt.fontSize = 14;
                    go.transform.SetParent(parent, false);
                }
            }
        }

        private void BuildContainer(ImlElement element, Transform parent)
        {
            bool isHorizontal = element.TagName == "HBox";
            var go = new GameObject(element.TagName);

            if (isHorizontal)
            {
                var hlg = go.AddComponent<HorizontalLayoutGroup>();
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = true;
            }
            else
            {
                var vlg = go.AddComponent<VerticalLayoutGroup>();
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
            }

            var csf = go.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Handle gap
            var gapStr = element.GetString("gap");
            if (int.TryParse(gapStr, out var gap) && gap > 0)
            {
                var lg = go.GetComponent<HorizontalOrVerticalLayoutGroup>();
                if (lg != null) lg.spacing = gap;
            }

            // Handle padding — fall back to style-defined padding when no inline padding
            var paddingStr = element.GetString("padding");
            if (string.IsNullOrEmpty(paddingStr))
            {
                var styleForPad = GetEffectiveStyle(element);
                if (styleForPad?.Setters != null && styleForPad.Setters.TryGetValue("padding", out var sp))
                    paddingStr = sp;
            }
            if (!string.IsNullOrEmpty(paddingStr))
            {
                var parts = paddingStr.Split(',');
                if (parts.Length == 4 &&
                    int.TryParse(parts[0], out var pTop) &&
                    int.TryParse(parts[1], out var pRight) &&
                    int.TryParse(parts[2], out var pBottom) &&
                    int.TryParse(parts[3], out var pLeft))
                {
                    var lg = go.GetComponent<HorizontalOrVerticalLayoutGroup>();
                    if (lg != null) lg.padding = new RectOffset(pLeft, pRight, pTop, pBottom);
                }
            }

            // Apply minWidth
            var mwStr = element.GetString("minWidth");
            if (float.TryParse(mwStr, out var minW) && minW > 0)
            {
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.minWidth = minW;
            }

            // Apply minHeight from element or style
            var mhStr = element.GetString("minHeight");
            if (float.TryParse(mhStr, out var minH) && minH > 0)
            {
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.minHeight = minH;
            }

            // Apply background from style (now uses ApplyBackgroundStyle so the sprite is set)
            var style = GetEffectiveStyle(element);
            if (style?.Setters != null && style.Setters.ContainsKey("background"))
            {
                var img = go.AddComponent<Image>();
                ApplyBackgroundStyle(img, style);
            }

            go.transform.SetParent(parent, false);

            foreach (var child in element.Children)
            {
                if (child is ImlElement e) BuildElement(e, go.transform);
                else if (child is string t && !string.IsNullOrWhiteSpace(t))
                {
                    var txtGo = new GameObject("Text");
                    var txt = txtGo.AddComponent<Text>();
                    txt.text = t;
                    txt.color = Color.white;
                    txt.font = DefaultFont;
                    txtGo.transform.SetParent(go.transform, false);
                }
            }
        }

        private void BuildText(ImlElement element, Transform parent)
        {
            var text = ResolveAttributeValue(element, "text");
            var go = new GameObject("Text");
            var txt = go.AddComponent<Text>();
            txt.text = text;
            txt.color = Color.white;
            txt.font = DefaultFont;
            txt.supportRichText = element.GetString("richText") == "true";

            var style = GetEffectiveStyle(element);
            if (style.Setters.TryGetValue("fontSize", out var fs) && int.TryParse(fs, out var fontSize))
                txt.fontSize = fontSize;
            if (TryParseColor(style, out var color))
                txt.color = color;

            go.transform.SetParent(parent, false);
        }

        private static bool TryParseColor(ImlStyle style, out Color color)
        {
            color = Color.white;
            if (style.Setters.TryGetValue("color", out var cs) && !string.IsNullOrEmpty(cs))
            {
                if (cs.StartsWith("#") && ColorUtility.TryParseHtmlString(cs, out var c))
                {
                    color = c;
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseBackground(ImlStyle style, out Color color)
        {
            color = Color.white;
            if (style.Setters.TryGetValue("background", out var bg) && !string.IsNullOrEmpty(bg))
            {
                if (bg.StartsWith("#") && ColorUtility.TryParseHtmlString(bg, out var c))
                {
                    color = c;
                    return true;
                }
            }
            return false;
        }

        private void BuildButton(ImlElement element, Transform parent)
        {
            var text = ResolveAttributeValue(element, "text");
            var go = new GameObject("Button");

            // Image first (the Button component will set targetGraphic to this)
            var img = go.AddComponent<Image>();

            var btnStyle = GetEffectiveStyle(element);
            ApplyBackgroundStyle(img, btnStyle);
            // ApplyBackgroundStyle flips raycastTarget to false (it assumes background-only
            // panels). Re-enable it for buttons so clicks actually register on the targetGraphic.
            img.raycastTarget = true;

            // Force a minimum/preferred height so the button doesn't collapse
            var le = go.AddComponent<LayoutElement>();
            var widthStr = element.GetString("width");
            if (float.TryParse(widthStr, out var btnW) && btnW > 0)
            {
                le.preferredWidth = btnW;
                le.minWidth = btnW;
            }
            le.minHeight = 32;
            le.preferredHeight = 32;

            // Button component for click events
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => HandleElementEvents(element));

            // Text child — stretched to fill the button, centered alignment
            var txtGo = new GameObject("Text");
            var txtRect = txtGo.AddComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.text = text;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.font = DefaultFont;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;

            if (btnStyle.Setters.TryGetValue("fontSize", out var fs2) && int.TryParse(fs2, out var fontSize2))
                txt.fontSize = fontSize2;
            if (TryParseColor(btnStyle, out var color2))
                txt.color = color2;

            txtGo.transform.SetParent(go.transform, false);
            go.transform.SetParent(parent, false);
        }

        private void BuildToggle(ImlElement element, Transform parent)
        {
            var text = ResolveAttributeValue(element, "text");
            var valueBinding = element.GetExpression("value");
            var onChanged = element.GetString("on-changed");

            bool currentValue = false;
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val is bool b && b;
            }

            var go = new GameObject(element.TagName);
            var tog = go.AddComponent<Toggle>();
            tog.isOn = currentValue;

            if (!string.IsNullOrEmpty(text))
            {
                var labelGo = new GameObject("Label");
                var lbl = labelGo.AddComponent<Text>();
                lbl.text = text;
                lbl.color = Color.white;
                lbl.font = DefaultFont;
                labelGo.transform.SetParent(go.transform, false);
            }

            tog.onValueChanged.AddListener(val =>
            {
                if (!string.IsNullOrEmpty(onChanged))
                    ScheduleEffect(() => InvokeHandler(onChanged, val));
            });

            go.transform.SetParent(parent, false);
        }

        private void BuildSlider(ImlElement element, Transform parent)
        {
            var valueBinding = element.GetExpression("value");
            var minStr = element.GetString("min");
            var maxStr = element.GetString("max");
            var onChanged = element.GetString("on-changed");

            float min = float.TryParse(minStr, out var mn) ? mn : 0;
            float max = float.TryParse(maxStr, out var mx) ? mx : 100;

            float currentValue = min;
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = Convert.ToSingle(val);
            }

            var go = new GameObject("Slider");
            var sl = go.AddComponent<Slider>();
            sl.minValue = min;
            sl.maxValue = max;
            sl.value = currentValue;

            sl.onValueChanged.AddListener(val =>
            {
                if (!string.IsNullOrEmpty(onChanged))
                    ScheduleEffect(() => InvokeHandler(onChanged, val));
            });

            go.transform.SetParent(parent, false);
        }

        private void BuildTextField(ImlElement element, Transform parent)
        {
            var valueBinding = element.GetExpression("value");
            var onSubmit = element.GetString("on-text-submit");

            string currentValue = "";
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val?.ToString() ?? "";
            }

            var go = new GameObject("InputField");
            var input = go.AddComponent<InputField>();
            var txtGo = new GameObject("Text");
            var txt = txtGo.AddComponent<Text>();
            txt.text = currentValue;
            txt.font = DefaultFont;
            txtGo.transform.SetParent(go.transform, false);
            input.textComponent = txt;

            input.onEndEdit.AddListener(val =>
            {
                if (!string.IsNullOrEmpty(onSubmit))
                    ScheduleEffect(() => InvokeHandler(onSubmit, val));
            });

            go.transform.SetParent(parent, false);
        }

        private void BuildScrollView(ImlElement element, Transform parent)
        {
            var go = new GameObject("ScrollView", typeof(ScrollRect));
            var sr = go.GetComponent<ScrollRect>();

            var vpGo = new GameObject("Viewport");
            var vpMask = vpGo.AddComponent<Mask>();
            vpMask.showMaskGraphic = false;
            var vpImg = vpGo.AddComponent<Image>();
            vpImg.color = Color.white;
            var vpRect = vpImg.rectTransform;
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.sizeDelta = Vector2.zero;
            vpGo.transform.SetParent(go.transform, false);
            sr.viewport = vpRect;

            var contentGo = new GameObject("Content");
            var contentLayout = contentGo.AddComponent<VerticalLayoutGroup>();
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentGo.transform.SetParent(vpGo.transform, false);
            sr.content = contentGo.GetComponent<RectTransform>();

            // Add a background image to the scroll view
            var bgImg = go.AddComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.08f, 1);

            go.transform.SetParent(parent, false);

            foreach (var child in element.Children)
            {
                if (child is ImlElement e) BuildElement(e, contentGo.transform);
            }
        }

        private void BuildSpacer(ImlElement element, Transform parent)
        {
            var heightStr = element.GetString("height");
            var go = new GameObject("Spacer");
            var le = go.AddComponent<LayoutElement>();
            if (float.TryParse(heightStr, out var h) && h > 0)
                le.minHeight = h;
            else
                le.minHeight = 10;
            le.flexibleWidth = 1;
            go.transform.SetParent(parent, false);
        }

        private void BuildSeparator(ImlElement element, Transform parent)
        {
            var go = new GameObject("Separator");
            var img = go.AddComponent<Image>();
            img.color = new Color(1, 1, 1, 0.2f);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 1;
            le.flexibleWidth = 1;
            go.transform.SetParent(parent, false);
        }

        private void BuildIcon(ImlElement element, Transform parent)
        {
            // The IML spec allows attribute values like type="{iconType}" — these are
            // expressions, not strings, so GetString (which only reads StringValue) would
            // return "" and the icon would always fall through to the default (gray "?").
            // Use ResolveAttributeValue so expressions are evaluated against the data context.
            var typeAttr = ResolveAttributeValue(element, "type");

            // Color + symbol map matching IridiumLayout's icon set
            var (color, symbol) = typeAttr switch
            {
                "information" => (new Color(0.3f, 0.5f, 1f),       "i"),
                "success"      => (new Color(0.1f, 0.8f, 0.3f),     "✓"),
                "warning"      => (new Color(0.9f, 0.6f, 0.1f),     "!"),
                "error"        => (new Color(0.85f, 0.15f, 0.15f),  "✕"),
                "stop"         => (new Color(0.85f, 0.15f, 0.15f),  "■"),
                _              => (new Color(0.5f, 0.5f, 0.5f),     "?"),
            };

            const float iconSize = 24f;
            var go = new GameObject("Icon");
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            // Use a full-circle sprite (radius == half size) to match IridiumLayout's
            // circular icons rather than a rounded square.
            img.sprite = GetRoundedSprite((int)(iconSize / 2f));
            img.type = Image.Type.Sliced;

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = iconSize;
            le.preferredHeight = iconSize;
            le.minWidth = iconSize;
            le.minHeight = iconSize;

            // Symbol overlay — child Text stretched to fill the icon
            var txtGo = new GameObject("Symbol");
            var txtRect = txtGo.AddComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.text = symbol;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.font = DefaultFont;
            txt.fontSize = 14;
            txt.color = Color.white;
            txt.fontStyle = FontStyle.Bold;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txtGo.transform.SetParent(go.transform, false);

            go.transform.SetParent(parent, false);
        }

        private void BuildImage(ImlElement element, Transform parent)
        {
            var source = element.GetString("source");
            var go = new GameObject("Image");

            if (!string.IsNullOrEmpty(source))
            {
                var tex = LoadTexture(source);
                if (tex != null)
                {
                    var raw = go.AddComponent<RawImage>();
                    raw.texture = tex;
                }
            }

            go.transform.SetParent(parent, false);
        }

        private string ResolveAttributeValue(ImlElement element, string attrName)
        {
            if (!element.Attributes.TryGetValue(attrName, out var attr)) return "";
            switch (attr.Type)
            {
                case AttributeType.String: return attr.StringValue ?? "";
                case AttributeType.Expression:
                    try { return _evaluator.Evaluate(attr.Expression)?.ToString() ?? ""; }
                    catch { return ""; }
                case AttributeType.Template:
                    if (attr.Parts == null) return "";
                    var sb = new System.Text.StringBuilder();
                    foreach (var part in attr.Parts)
                    {
                        if (part.IsExpression)
                        {
                            try { sb.Append(_evaluator.Evaluate(part.Value)?.ToString() ?? ""); }
                            catch { }
                        }
                        else sb.Append(part.Value);
                    }
                    return sb.ToString();
                case AttributeType.Boolean: return attr.BoolValue ? "true" : "false";
                default: return attr.StringValue ?? "";
            }
        }

        private ImlStyle GetEffectiveStyle(ImlElement element)
        {
            var cn = element.GetString("class");
            var sn = ResolveAttributeValue(element, "style");
            if (!string.IsNullOrEmpty(sn) && _styleCache.TryGetValue(sn.ToLowerInvariant(), out var s)) return s;
            if (!string.IsNullOrEmpty(cn) && _styleCache.TryGetValue(cn.ToLowerInvariant(), out s)) return s;
            return new ImlStyle();
        }

        private void HandleElementEvents(ImlElement element)
        {
            foreach (var kv in element.Attributes)
            {
                if (kv.Key.StartsWith("on-") && !kv.Key.StartsWith("data-on-"))
                {
                    var spec = ResolveAttributeValue(element, kv.Key);
                    if (!string.IsNullOrEmpty(spec)) InvokeHandlerString(spec);
                }
            }
        }

        private void InvokeHandlerString(string spec)
        {
            string name = spec;
            string arg = null;
            var pi = spec.IndexOf('(');
            if (pi > 0 && spec.EndsWith(")"))
            {
                name = spec.Substring(0, pi).Trim();
                var a = spec.Substring(pi + 1, spec.Length - pi - 2).Trim();
                if ((a.StartsWith("'") && a.EndsWith("'")) || (a.StartsWith("\"") && a.EndsWith("\"")))
                    arg = a.Substring(1, a.Length - 2);
                else if (!string.IsNullOrEmpty(a))
                {
                    var ev = _evaluator.Evaluate(a);
                    arg = ev?.ToString();
                }
            }
            if (arg != null) InvokeHandler(name, arg);
            else InvokeHandler(name, null);
        }

        private void InvokeHandler(string name, object param)
        {
            if (_handlers.TryGetValue(name, out var h)) h();
            else if (_genericHandlers.TryGetValue(name, out var gh)) gh(param);
        }

        private void ScheduleEffect(Action effect) { try { effect(); } catch { } }

        private void OnDataContextPropertyChanged(string path, object oldVal, object newVal) => _dirty = true;

        private Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_textureCache.TryGetValue(path, out var cached)) return cached;
            try
            {
                var fullPath = _parser.ResolvePath(path);
                if (File.Exists(fullPath))
                {
                    var tex = new Texture2D(1, 1);
                    tex.LoadImage(File.ReadAllBytes(fullPath));
                    _textureCache[path] = tex;
                    return tex;
                }
            }
            catch { }
            return null;
        }
    }
}
