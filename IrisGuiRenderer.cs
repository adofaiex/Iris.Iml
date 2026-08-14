using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// IML渲染器主类
    /// </summary>
    public class IrisGuiRenderer : IImlRenderer
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
        private readonly List<ImlStyle> _selectorStyles = new();

        private bool _hotReloadEnabled = false;
        private FileSystemWatcher _fileWatcher;
        private float _lastReloadTime = 0f;
        private const float ReloadCooldown = 0.5f;

        private readonly List<Action> _pendingEffects = new();
        private bool _effectsScheduled = false;

        private readonly Dictionary<string, object> _loopItemContext = new();
        private readonly Dictionary<string, List<object>> _forEachCollections = new();

        public string CurrentFilePath { get; private set; }

        private IIrrLayout _layout;

        private readonly Dictionary<string, Func<object[], object>> _registeredFunctions = new();
        private readonly Dictionary<string, GUIStyle> _guiStyleCache = new();

        public void SetLayout(IIrrLayout layout) => _layout = layout;

        /// <summary>
        /// 日志输出委托，由调用方设置（如 Main.Logger.Log）
        /// </summary>
        public Action<string> LogDelegate { get; set; }

        private void Log(string message)
        {
            // 优先使用委托，否则使用 Unity Debug.Log（会同时输出到 Unity Console 和 UMM 日志）
            if (LogDelegate != null)
                LogDelegate(message);
            else
                UnityEngine.Debug.Log($"[Iris.Iml] {message}");
        }

        /// <summary>
        /// 设置数据上下文
        /// </summary>
        public void SetDataContext(object data)
        {
            _dataContext = new BindingContext(data);
            _evaluator = new ExpressionEvaluator(_dataContext as BindingContext ?? new BindingContext(data));
            _dataContext.PropertyChanged += OnDataContextPropertyChanged;

            // Re-register functions on the new evaluator
            foreach (var kv in _registeredFunctions)
                _evaluator.RegisterFunction(kv.Key, kv.Value);
        }

        /// <summary>
        /// Write a value back to the data context at the given property path.
        /// Used by input controls (TextField, TextArea) when the user submits a
        /// new value via the on-text-submit/on-changed handler chain. Without
        /// this, the bound CLR field (e.g. <c>Settings.judgeText.tooEarly</c>)
        /// stays at its old value, and downstream <c>Save()</c> writes the
        /// stale value to disk. (Bug: "判定文本无法修改".)
        /// </summary>
        public void SetContextValue(string propertyPath, object value)
        {
            _dataContext?.SetValue(propertyPath, value);
        }

        /// <summary>
        /// 注册事件处理程序
        /// </summary>
        public void RegisterHandler(string name, Action handler)
        {
            _handlers[name] = handler;
        }

        public void RegisterHandler<T>(string name, Action<T> handler)
        {
            _genericHandlers[name] = obj => handler(obj is T t ? t : default);
        }

        public void RegisterHandler(string name, Action<object> handler)
        {
            _genericHandlers[name] = handler;
        }

        public void RegisterFunction(string name, Func<object[], object> func)
        {
            _registeredFunctions[name] = func;
            _evaluator?.RegisterFunction(name, func);
        }

        /// <summary>
        /// 注册绘制回调
        /// </summary>
        public void RegisterDrawHandler(string name, Action<Rect, RendererInternal.DrawArgs> handler)
        {
            _drawHandlers[name] = handler;
        }

        /// <summary>
        /// 启用/禁用热重载
        /// </summary>
        public void SetHotReload(bool enabled)
        {
            _hotReloadEnabled = enabled;

            if (enabled && !string.IsNullOrEmpty(CurrentFilePath))
            {
                StartFileWatcher();
            }
            else
            {
                StopFileWatcher();
            }
        }

        private void StartFileWatcher()
        {
            StopFileWatcher();

            var directory = Path.GetDirectoryName(CurrentFilePath);
            var fileName = Path.GetFileName(CurrentFilePath);

            if (Directory.Exists(directory))
            {
                _fileWatcher = new FileSystemWatcher(directory, fileName);
                _fileWatcher.Changed += OnFileChanged;
                _fileWatcher.EnableRaisingEvents = true;
            }
        }

        private void StopFileWatcher()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Changed -= OnFileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Time.realtimeSinceStartup - _lastReloadTime > ReloadCooldown)
            {
                _lastReloadTime = Time.realtimeSinceStartup;
                LoadFile(CurrentFilePath);
            }
        }

        /// <summary>
        /// 加载IML文件
        /// </summary>
        public void LoadFile(string filePath)
        {
            CurrentFilePath = filePath;
            _document = _parser.Parse(filePath);
            ProcessResources();
            _styleCache.Clear();
            _selectorStyles.Clear();
            _forEachCollections.Clear();
            _referenceCache.Clear();

            if (_hotReloadEnabled)
                StartFileWatcher();
        }

        /// <summary>
        /// 加载IML内容
        /// </summary>
        public void LoadContent(string imlContent, string basePath = "")
        {
            _document = _parser.ParseContent(imlContent, basePath);
            CurrentFilePath = string.IsNullOrEmpty(basePath) ? CurrentFilePath : Path.Combine(basePath, "_generated.iml");
            ProcessResources();
            _styleCache.Clear();
            _selectorStyles.Clear();
            _forEachCollections.Clear();
            _referenceCache.Clear();
        }

        private void ProcessResources()
        {
            if (_document?.Root == null) return;

            // Process <Resources> section
            foreach (var child in _document.Root.Children)
            {
                if (child is ImlElement element && element.TagName == "Resources")
                {
                    ProcessResourceElement(element);
                }
            }
        }

        private void ProcessReferencedResources(ImlDocument doc)
        {
            if (doc?.Root == null) return;
            foreach (var child in doc.Root.Children)
            {
                if (child is ImlElement element && element.TagName == "Resources")
                {
                    ProcessResourceElement(element);
                }
            }
        }

        private void ProcessResourceElement(ImlElement element)
        {
            foreach (var child in element.Children)
            {
                if (child is ImlElement childElement)
                {
                    if (childElement.TagName == "Reference")
                    {
                        var path = childElement.GetString("path");
                        if (!string.IsNullOrEmpty(path))
                            ProcessReferencedFile(path);
                    }
                    else if (childElement.TagName == "Style")
                    {
                        var style = ParseStyle(childElement);
                        if (!string.IsNullOrEmpty(style.Name))
                            _styleCache[style.Name.ToLowerInvariant()] = style;
                        if (style.Selector != null)
                            _selectorStyles.Add(style);
                    }
                }
            }

            // Resolve style inheritance (extends)
            foreach (var kv in _styleCache)
            {
                ResolveExtends(kv.Value, new HashSet<string>());
            }

            // Sort selectors by specificity ascending so GetEffectiveStyle can
            // apply them in order (later = higher specificity = wins).
            _selectorStyles.Sort((a, b) => a.Selector.Specificity.CompareTo(b.Selector.Specificity));
        }

        private void ResolveExtends(ImlStyle style, HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(style.Extends)) return;
            if (!visited.Add(style.Name.ToLowerInvariant()))
            {
                Debug.LogWarning($"[Iris.Iml] Circular style inheritance detected for '{style.Name}'");
                return;
            }
            if (_styleCache.TryGetValue(style.Extends.ToLowerInvariant(), out var parent))
            {
                ResolveExtends(parent, visited);
                foreach (var kv in parent.Setters)
                    if (!style.Setters.ContainsKey(kv.Key))
                        style.Setters[kv.Key] = kv.Value;
            }
            style.Extends = null;
        }

        private void ProcessReferencedFile(string path)
        {
            try
            {
                var refPath = ResolveReferencePath(path);
                if (!File.Exists(refPath)) return;
                if (_referenceCache.ContainsKey(refPath)) return;
                var refDoc = _parser.Parse(refPath);
                _referenceCache[refPath] = refDoc;
                ProcessReferencedResources(refDoc);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] Failed to process referenced resources: {path} - {ex.Message}");
            }
        }

        private ImlStyle ParseStyle(ImlElement element)
        {
            var style = new ImlStyle
            {
                Name = element.GetString("name"),
                Extends = element.GetString("extends"),
                Selector = StyleSelector.Parse(element.GetString("on"))
            };

            foreach (var child in element.Children)
            {
                if (child is ImlElement childElement)
                {
                    if (childElement.TagName == "Setter")
                    {
                        var property = childElement.GetString("property");
                        var value = childElement.GetString("value");
                        if (!string.IsNullOrEmpty(property))
                            style.Setters[property] = value ?? "";
                    }
                    else
                    {
                        // Custom property tag: <tagName value="..." />
                        var value = childElement.GetString("value");
                        if (!string.IsNullOrEmpty(value))
                            style.Setters[childElement.TagName] = value;
                    }
                }
            }

            return style;
        }

        /// <summary>
        /// 在OnGUI中调用此方法渲染UI
        /// </summary>
        public void OnGUI()
        {
            if (_document?.Root == null || _dataContext == null)
                return;

            if (_hotReloadEnabled && UnityEngine.Input.GetKeyDown(KeyCode.R) && UnityEngine.Input.GetKey(KeyCode.LeftControl))
                LoadFile(CurrentFilePath);

            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            _elementCount = 0;

            try
            {
                RenderElement(_document.Root);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iris.Iml] Render error: {ex.Message}\n{ex.StackTrace}");
            }

            sw.Stop();

            if (_effectsScheduled)
            {
                ProcessPendingEffects();
                _effectsScheduled = false;
            }
        }

        private int _elementCount;

        /// <summary>
        /// 加载并渲染IML文件（简化接口）
        /// </summary>
        public void Render(string filePath)
        {
            if (!string.Equals(CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                LoadFile(filePath);
            OnGUI();
        }

        private void RenderElement(ImlElement element)
        {
            if (element == null) return;
            _elementCount++;

            // Check condition (If rendering)
            if (element.TagName == "If")
            {
                var condition = element.GetExpression("condition");
                if (!string.IsNullOrEmpty(condition) && !_evaluator.EvaluateBoolean(condition))
                    return;
            }

            // Check visible attribute
            if (element.HasAttribute("visible"))
            {
                var visible = element.GetExpression("visible");
                if (!string.IsNullOrEmpty(visible) && !_evaluator.EvaluateBoolean(visible))
                    return;
            }

            // Render based on tag type
            switch (element.TagName)
            {
                case "Iris":
                case "If":
                case "":
                    RenderChildren(element);
                    break;

                case "View":
                case "HBox":
                case "VBox":
                    RenderFlexContainer(element);
                    break;

                case "ScrollView":
                    RenderScrollView(element);
                    break;

                case "Text":
                    RenderText(element);
                    break;

                case "Image":
                    RenderImage(element);
                    break;

                case "Button":
                    RenderButton(element);
                    break;

                case "Switch":
                    RenderSwitch(element);
                    break;

                case "Checkbox":
                    RenderCheckbox(element);
                    break;

                case "Slider":
                    RenderSlider(element);
                    break;

                case "TextField":
                    RenderTextField(element);
                    break;

                case "TextArea":
                    RenderTextArea(element);
                    break;

                case "Fill":
                    if (_layout != null)
                        _layout.Fill();
                    else
                        GUILayout.FlexibleSpace();
                    break;

                case "Icon":
                    RenderIcon(element);
                    break;

                case "Separator":
                    RenderSeparator(element);
                    break;

                case "Reference":
                    RenderReference(element);
                    break;

                case "ForEach":
                    RenderForEach(element);
                    break;

                case "ArrowButton":
                    RenderArrowButton(element);
                    break;

                case "Selector":
                    RenderSelector(element);
                    break;

                case "CustomCanvas":
                    RenderCustomCanvas(element);
                    break;

                case "References":
                case "Resources":
                case "Style":
                case "Template":
                case "StyleSelector":
                case "Case":
                case "Slot":
                    // These are processed at load time
                    break;

                default:
                    Debug.LogWarning($"[Iris.Iml] Unknown element: {element.TagName}");
                    break;
            }
        }

        private void RenderChildren(ImlElement element)
        {
            foreach (var child in element.Children)
            {
                if (child is ImlElement childElement)
                    RenderElement(childElement);
                else if (child is ExpressionValue ev && !string.IsNullOrWhiteSpace(ev.Expression))
                {
                    var evaluated = _evaluator.Evaluate(ev.Expression);
                    var text = evaluated?.ToString() ?? "";
                    if (_layout != null)
                        _layout.Text(text, IrrTextStyle.Normal);
                    else
                        GUILayout.Label(text);
                }
                else if (child is string text && !string.IsNullOrWhiteSpace(text))
                {
                    if (_layout != null)
                        _layout.Text(text, IrrTextStyle.Normal);
                    else
                        GUILayout.Label(text);
                }
            }
        }

        private void RenderFlexContainer(ImlElement element)
        {
            bool isHorizontal = element.TagName == "HBox";
            var containerStyle = GetContainerStyle(element);
            var style = GetEffectiveStyle(element);

            var gapStr = element.GetString("gap");
            int gap = 0;
            if (!string.IsNullOrEmpty(gapStr) && int.TryParse(gapStr, out var g))
                gap = g;

            var options = new List<GUILayoutOption>();
            options.AddRange(GetStyleOptions(style));
            options.Add(GUILayout.ExpandWidth(true));
            if (!isHorizontal)
                options.Add(GUILayout.ExpandHeight(true));

            if (_layout != null)
            {
                var prevBg = GUI.backgroundColor;
                if (style.Setters.TryGetValue("background", out var bgHex))
                    GUI.backgroundColor = GetColor(bgHex);

                if (isHorizontal)
                    _layout.BeginHorizontal(containerStyle, options.ToArray());
                else
                    _layout.BeginVertical(containerStyle, options.ToArray());
                GUI.backgroundColor = prevBg;
            }
            else
            {
                var bgHex = GetStyleString(style, "background", "");
                var gs = !string.IsNullOrEmpty(bgHex) ? BuildGuiStyle(style, element) : null;

                if (isHorizontal)
                    GUILayout.BeginHorizontal(gs ?? GUI.skin.box, options.ToArray());
                else
                    GUILayout.BeginVertical(gs ?? GUI.skin.box, options.ToArray());
            }

            try
            {
                var children = element.Children;
                for (int i = 0; i < children.Length; i++)
                {
                    if (i > 0 && gap > 0)
                    {
                        if (_layout != null)
                            _layout.Space(gap);
                        else
                            GUILayout.Space(gap);
                    }

                    if (children[i] is ImlElement childElement)
                        RenderElement(childElement);
                    else
                    {
                        var text = GetFlexChildText(children[i]);
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (_layout != null)
                                _layout.Text(text, IrrTextStyle.Normal);
                            else
                                GUILayout.Label(text);
                        }
                    }
                }
            }
            finally
            {
                if (_layout != null)
                    _layout.End();
                else if (isHorizontal)
                    GUILayout.EndHorizontal();
                else
                    GUILayout.EndVertical();
            }
        }

        private void RenderScrollView(ImlElement element)
        {
            var scrollPositionKey = element.GetString("scrollPosition") ?? "_scrollPos";

            if (!_loopItemContext.TryGetValue(scrollPositionKey, out var posObj))
            {
                posObj = Vector2.zero;
                _loopItemContext[scrollPositionKey] = posObj;
            }

            Vector2 scrollPos = (Vector2)posObj;
            var heightStr = element.GetString("height");
            int height = 0;
            var options = new List<GUILayoutOption>();
            options.Add(GUILayout.ExpandWidth(true));
            options.Add(GUILayout.ExpandHeight(true));
            if (!string.IsNullOrEmpty(heightStr) && int.TryParse(heightStr, out height))
                options.Add(GUILayout.Height(height));
            scrollPos = GUILayout.BeginScrollView(scrollPos, options.ToArray());

            try
            {
                RenderChildren(element);
            }
            finally
            {
                GUILayout.EndScrollView();
            }

            _loopItemContext[scrollPositionKey] = scrollPos;
        }

        private readonly Dictionary<string, ImlDocument> _referenceCache = new();

        private void RenderReference(ImlElement element)
        {
            // Per spec: use "path" attribute with @ prefix
            var path = element.GetString("path");
            if (string.IsNullOrEmpty(path))
                path = element.GetString("src"); // backward compat
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var referencePath = ResolveReferencePath(path);

                if (!File.Exists(referencePath))
                {
                    Debug.LogWarning($"[Iris.Iml] Reference file not found: {referencePath}");
                    return;
                }

                // Cache parsed documents to avoid re-parsing every frame
                if (!_referenceCache.TryGetValue(referencePath, out var referenceDocument))
                {
                    referenceDocument = _parser.Parse(referencePath);
                    _referenceCache[referencePath] = referenceDocument;
                    // Process resources (styles, templates) from referenced files
                    ProcessReferencedResources(referenceDocument);
                }

                if (referenceDocument?.Root != null)
                {
                    RenderElement(referenceDocument.Root);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iris.Iml] Failed to render reference: {path} - {ex.Message}");
            }
        }

        private string ResolveReferencePath(string path)
        {
            // Per spec: @/ = Mod root, @ = current file dir
            var basePath = Path.GetDirectoryName(CurrentFilePath) ?? "";

            if (path.StartsWith("@/"))
            {
                // Mod root: go up from ui/ to Resources/ to v3/ to Iridium/
                // Actually, @/ resolves relative to the mod root (Main.ModPath)
                // For now, resolve relative to basePath + ../../
                return Path.GetFullPath(Path.Combine(basePath, "..", "..", path.Substring(2)));
            }
            if (path.StartsWith("@"))
            {
                return Path.GetFullPath(Path.Combine(basePath, path.Substring(1)));
            }
            // Bare path: relative to current file
            return Path.Combine(basePath, path);
        }

        private void RenderText(ImlElement element)
        {
            var text = ResolveAttributeValue(element, "text");
            var style = GetEffectiveStyle(element);

            if (_layout != null)
            {
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("color", out var colorVal))
                    GUI.contentColor = GetColor(colorVal);
                _layout.Text(text, GetTextStyle(element));
                GUI.contentColor = prevContent;
            }
            else
            {
                var gs = BuildTextStyle(style, element);
                GUILayout.Label(text, gs, GetStyleOptions(style));
            }
        }

        /// <summary>
        /// Resolve an attribute value regardless of its type (String, Expression, Template)
        /// </summary>
        private string ResolveAttributeValue(ImlElement element, string attrName)
        {
            if (!element.Attributes.TryGetValue(attrName, out var attr))
                return "";

            switch (attr.Type)
            {
                case AttributeType.String:
                    return attr.StringValue ?? "";
                case AttributeType.Expression:
                    try
                    {
                        var result = _evaluator.Evaluate(attr.Expression);
                        return result?.ToString() ?? "";
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Iris.Iml] Failed to evaluate expression '{attr.Expression}': {ex.Message}");
                        return "";
                    }
                case AttributeType.Template:
                    if (attr.Parts == null) return "";
                    var sb = new System.Text.StringBuilder();
                    foreach (var part in attr.Parts)
                    {
                        if (part.IsExpression)
                        {
                            try
                            {
                                var result = _evaluator.Evaluate(part.Value);
                                sb.Append(result?.ToString() ?? "");
                            }
                            catch
                            {
                                sb.Append("");
                            }
                        }
                        else
                        {
                            sb.Append(part.Value);
                        }
                    }
                    return sb.ToString();
                case AttributeType.Boolean:
                    return attr.BoolValue ? "true" : "false";
                case AttributeType.StyleObject:
                    return "";
                default:
                    return attr.StringValue ?? "";
            }
        }

        private void RenderImage(ImlElement element)
        {
            var source = element.GetString("source");
            var widthStr = element.GetString("width");
            var heightStr = element.GetString("height");

            int width = string.IsNullOrEmpty(widthStr) ? 100 : int.Parse(widthStr);
            int height = string.IsNullOrEmpty(heightStr) ? 100 : int.Parse(heightStr);

            var texture = LoadTexture(source);

            if (texture != null)
            {
                GUI.DrawTexture(new Rect(0, 0, width, height), texture);
            }
            else
            {
                GUI.Box(new Rect(0, 0, width, height), "Loading...");
            }
        }

        private void RenderButton(ImlElement element)
        {
            var text = ResolveAttributeValue(element, "text");
            var command = element.GetString("command");
            var style = GetEffectiveStyle(element);

            bool clicked;
            if (_layout != null)
            {
                var prevBg = GUI.backgroundColor;
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("background", out var bgVal))
                    GUI.backgroundColor = GetColor(bgVal);
                if (style.Setters.TryGetValue("color", out var colorVal))
                    GUI.contentColor = GetColor(colorVal);
                clicked = _layout.Button(text, GetButtonStyle(element));
                GUI.backgroundColor = prevBg;
                GUI.contentColor = prevContent;
            }
            else
            {
                var gs = BuildGuiStyle(style, element);
                clicked = GUILayout.Button(text, gs, GetStyleOptions(style));
            }

            if (clicked)
            {
                if (!string.IsNullOrEmpty(command))
                    InvokeCommand(command);
                HandleElementEvents(element);
            }
        }

        private void RenderSwitch(ImlElement element)
        {
            var valueBinding = element.GetExpression("value");
            var onChanged = element.GetString("on-changed");
            var text = ResolveAttributeValue(element, "text");

            if (!string.IsNullOrEmpty(text))
            {
                var style = GetEffectiveStyle(element);
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("color", out var colorVal))
                    GUI.contentColor = GetColor(colorVal);
                if (_layout != null)
                    _layout.Text(text, IrrTextStyle.Normal);
                else
                    GUILayout.Label(text);
                GUI.contentColor = prevContent;
            }

            bool currentValue = false;
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val is bool b && b;
            }

            bool? result;
            if (_layout != null)
                result = _layout.Switch(currentValue);
            else
            {
                var style = GetEffectiveStyle(element);
                var onHex = GetStyleString(style, "switchOn", "#D973A5");
                var offHex = GetStyleString(style, "switchOff", "#313338");
                var knobHex = GetStyleString(style, "knobColor", "#FFFFFF");
                Color onColor = GetColor(onHex);
                Color offColor = GetColor(offHex);
                Color knobColor = GetColor(knobHex);

                int w = 40, h = 22;
                var tex = GuiTextureFactory.GetPill(w, h,
                    currentValue ? onColor : offColor,
                    knobColor,
                    currentValue ? 1f : 0f);
                var gs = new GUIStyle();
                gs.normal.background = gs.hover.background = gs.active.background = tex;
                gs.border = new RectOffset(h / 2, h / 2, h / 2, h / 2);

                GUI.changed = false;
                bool newValue = GUILayout.Toggle(currentValue, "", gs, GUILayout.Width(w), GUILayout.Height(h));
                result = GUI.changed ? newValue : (bool?)null;
            }

            if (result.HasValue && !string.IsNullOrEmpty(valueBinding))
                SetContextValue(valueBinding, result.Value);
            if (result.HasValue && !string.IsNullOrEmpty(onChanged))
                ScheduleEffect(() => InvokeHandler(onChanged, result.Value));
        }

        private void RenderCheckbox(ImlElement element)
        {
            var valueBinding = element.GetExpression("value");
            var onChanged = element.GetString("on-changed");
            var text = ResolveAttributeValue(element, "text");

            if (!string.IsNullOrEmpty(text))
            {
                var style = GetEffectiveStyle(element);
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("color", out var colorVal))
                    GUI.contentColor = GetColor(colorVal);
                if (_layout != null)
                    _layout.Text(text, IrrTextStyle.Normal);
                else
                    GUILayout.Label(text);
                GUI.contentColor = prevContent;
            }

            bool currentValue = false;
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val is bool b && b;
            }

            bool? result;
            if (_layout != null)
                result = _layout.Checkbox(currentValue);
            else
            {
                var style = GetEffectiveStyle(element);
                var bgHex = GetStyleString(style, "background", "#313338");
                var borderHex = GetStyleString(style, "borderColor", "#494F5C");
                var checkHex = GetStyleString(style, "checkColor", "#FFFFFF");
                var onBgHex = GetStyleString(style, "checkBg", "#D973A5");
                Color bg = GetColor(bgHex);
                Color borderCol = GetColor(borderHex);
                Color check = GetColor(checkHex);
                int sz = 22, radius = 4;

                if (currentValue)
                {
                    var tex = GuiTextureFactory.GetRoundedRect(sz, sz, radius, GetColor(onBgHex), null, 0);
                    var gs = new GUIStyle();
                    gs.normal.background = gs.hover.background = gs.active.background = tex;
                    gs.border = new RectOffset(radius, radius, radius, radius);
                    GUI.changed = false;
                    bool newValue = GUILayout.Toggle(true, "", gs, GUILayout.Width(sz), GUILayout.Height(sz));
                    result = GUI.changed ? false : (bool?)null;

                    // Overlay checkmark
                    var chk = GuiTextureFactory.GetCheckmark(sz, check);
                    var rect = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(rect, chk);
                }
                else
                {
                    var tex = GuiTextureFactory.GetRoundedRect(sz, sz, radius, bg, borderCol, 1);
                    var gs = new GUIStyle();
                    gs.normal.background = gs.hover.background = gs.active.background = tex;
                    gs.border = new RectOffset(radius, radius, radius, radius);
                    GUI.changed = false;
                    bool newValue = GUILayout.Toggle(false, "", gs, GUILayout.Width(sz), GUILayout.Height(sz));
                    result = GUI.changed ? true : (bool?)null;
                }
            }

            if (result.HasValue && !string.IsNullOrEmpty(valueBinding))
                SetContextValue(valueBinding, result.Value);
            if (result.HasValue && !string.IsNullOrEmpty(onChanged))
                ScheduleEffect(() => InvokeHandler(onChanged, result.Value));
        }

		private void RenderSlider(ImlElement element)
		{
			var valueBinding = element.GetExpression("value");
			var minStr = element.GetString("min");
			var maxStr = element.GetString("max");
			var showValueStr = element.GetString("showValue");
			var onChanged = element.GetString("on-changed");

			float min = string.IsNullOrEmpty(minStr) ? 0 : float.Parse(minStr);
			float max = string.IsNullOrEmpty(maxStr) ? 100 : float.Parse(maxStr);
			bool showValue = showValueStr == "true";

			float currentValue = min;
			if (!string.IsNullOrEmpty(valueBinding))
			{
				var val = _evaluator.Evaluate(valueBinding);
				currentValue = Convert.ToSingle(val);
			}

			bool isInt = currentValue == Mathf.Round(currentValue) && max > 1;
			var fmt = isInt ? "F0" : "F2";

			GUI.changed = false;
			float newValue = GUILayout.HorizontalSlider(currentValue, min, max, GUILayout.MinWidth(80));
			if (showValue)
			{
				GUILayout.Space(5);
				var textValue = newValue.ToString(fmt);
				if (_layout != null)
				{
					var layoutResult = _layout.TextField(textValue);
					if (layoutResult != null && float.TryParse(layoutResult, out var parsed))
					{
						parsed = Mathf.Clamp(parsed, min, max);
						if (Math.Abs(parsed - newValue) > 0.001f)
						{
							newValue = parsed;
							GUI.changed = true;
						}
					}
				}
				else
				{
					var guiResult = GUILayout.TextField(textValue, GUILayout.Width(50));
					if (float.TryParse(guiResult, out var parsed))
					{
						parsed = Mathf.Clamp(parsed, min, max);
						if (Math.Abs(parsed - newValue) > 0.001f)
						{
							newValue = parsed;
							GUI.changed = true;
						}
					}
				}
			}

			if (GUI.changed)
			{
				if (!string.IsNullOrEmpty(valueBinding))
					SetContextValue(valueBinding, newValue);
				if (!string.IsNullOrEmpty(onChanged))
					ScheduleEffect(() => InvokeHandler(onChanged, newValue));
			}
		}

        private void RenderTextField(ImlElement element)
        {
            var valueBinding = element.GetExpression("value");
            var onSubmit = element.GetString("on-text-submit");

            string currentValue = "";
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val?.ToString() ?? "";
            }

            string newValue = null;
            if (_layout != null)
            {
                var style = GetEffectiveStyle(element);
                var prevBg = GUI.backgroundColor;
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("background", out var bgHex))
                    GUI.backgroundColor = GetColor(bgHex);
                if (style.Setters.TryGetValue("color", out var colorHex))
                    GUI.contentColor = GetColor(colorHex);
                newValue = _layout.TextField(currentValue);
                GUI.backgroundColor = prevBg;
                GUI.contentColor = prevContent;
            }
            else
            {
                var style = GetEffectiveStyle(element);
                var bgHex = GetStyleString(style, "background", "#151719");
                var borderHex = GetStyleString(style, "borderColor", "#222326");
                var focusHex = GetStyleString(style, "focusBorder", "#D973A5");
                var colorHex = GetStyleString(style, "color", "#E9ECEF");
                int radius = GetStyleInt(style, "radius", 8);
                int borderWidth = GetStyleInt(style, "borderWidth", 1);

                Color bg = GetColor(bgHex);
                Color borderCol = GetColor(borderHex);
                Color focusCol = GetColor(focusHex);
                int texSize = Mathf.Max(2 * radius + 2, 16);

                var normalTex = GuiTextureFactory.GetRoundedRect(texSize, texSize, radius, bg, borderCol, borderWidth);
                var focusTex = GuiTextureFactory.GetRoundedRect(texSize, texSize, radius, bg, focusCol, borderWidth);
                var gs = new GUIStyle(GUI.skin.textField);
                gs.normal.background = normalTex;
                gs.hover.background = normalTex;
                gs.active.background = focusTex;
                gs.focused.background = focusTex;
                gs.normal.textColor = gs.hover.textColor = gs.active.textColor = gs.focused.textColor = GetColor(colorHex);
                gs.border = new RectOffset(radius, radius, radius, radius);
                gs.padding = new RectOffset(6, 6, 4, 4);

                GUI.changed = false;
                newValue = GUILayout.TextField(currentValue, gs, GUILayout.ExpandWidth(true));
                if (!GUI.changed)
                    newValue = null;
            }

            if (newValue != null && !string.IsNullOrEmpty(valueBinding))
                SetContextValue(valueBinding, newValue);

            if (newValue != null && !string.IsNullOrEmpty(onSubmit))
                ScheduleEffect(() => InvokeHandler(onSubmit, newValue));

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
                ScheduleEffect(() => InvokeHandler(onSubmit, newValue ?? currentValue));
        }

        private void RenderTextArea(ImlElement element)
        {
            var valueBinding = element.GetExpression("value");
            var linesStr = element.GetString("lines");
            var lines = string.IsNullOrEmpty(linesStr) ? 3 : int.Parse(linesStr);

            string currentValue = "";
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentValue = val?.ToString() ?? "";
            }

            GUI.changed = false;
            string newValue = GUILayout.TextArea(currentValue, lines, GUILayout.ExpandWidth(true), GUILayout.Height(lines * 20));

            // Two-way binding: write the new value back to the data context
            // when the user edits. (Bug: "判定文本无法修改" — without this, the
            // bound CLR field never sees the new value.)
            if (GUI.changed && !string.IsNullOrEmpty(valueBinding))
                SetContextValue(valueBinding, newValue);
        }

        private void RenderSeparator(ImlElement element)
        {
            var style = GetEffectiveStyle(element);

            if (_layout != null)
            {
                var prevBg = GUI.backgroundColor;
                if (style.Setters.TryGetValue("background", out var bgHex))
                    GUI.backgroundColor = GetColor(bgHex);
                _layout.Separator();
                GUI.backgroundColor = prevBg;
                return;
            }

            var sepColor = GetStyleString(style, "background", "#20FFFFFF");
            Color c = GetColor(sepColor);
            int h = 1;
            var tex = new Texture2D(2, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            for (int x = 0; x < 2; x++)
                tex.SetPixel(x, 0, c);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Repeat;

            GUILayout.Space(2);
            GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(h));
            GUILayout.Space(2);
        }

        private void RenderForEach(ImlElement element)
        {
            var itemsBinding = element.GetExpression("items");
            var keyBinding = element.GetString("key");
            var template = element.GetString("template");

            if (string.IsNullOrEmpty(itemsBinding) || string.IsNullOrEmpty(template))
                return;

            var itemsObj = _evaluator.Evaluate(itemsBinding);
            if (itemsObj == null) return;

            IEnumerable items = null;
            if (itemsObj is IEnumerable)
                items = itemsObj as IEnumerable;
            else
                return;

            foreach (var item in items)
            {
                _evaluator.SetVariable("item", item);

                var templateElement = FindTemplate(template);
                if (templateElement != null)
                {
                    foreach (var child in templateElement.Children)
                    {
                        if (child is ImlElement childElement)
                            RenderElement(childElement);
                    }
                }
            }

            _evaluator.SetVariable("item", null);
        }

        private ImlElement FindTemplate(string name)
        {
            foreach (var child in _document.Root.Children)
            {
                if (child is ImlElement element && element.TagName == "Resources")
                {
                    foreach (var resource in element.Children)
                    {
                        if (resource is ImlElement res && res.TagName == "Template" && res.GetString("name") == name)
                            return res;
                    }
                }
            }
            return null;
        }

        private void RenderCustomCanvas(ImlElement element)
        {
            var onDraw = element.GetString("on-draw");
            var widthStr = element.GetString("width");
            var heightStr = element.GetString("height");

            int width = string.IsNullOrEmpty(widthStr) ? 100 : int.Parse(widthStr);
            int height = string.IsNullOrEmpty(heightStr) ? 100 : int.Parse(heightStr);

            if (_drawHandlers.TryGetValue(onDraw, out var handler))
            {
                var rect = GUILayoutUtility.GetRect(width, height);
                handler(rect, new RendererInternal.DrawArgs { Context = _dataContext });
            }
        }

        private string GetEffectiveStyleName(ImlElement element)
        {
            var styleVal = ResolveAttributeValue(element, "style");
            if (!string.IsNullOrEmpty(styleVal)) return styleVal;
            return ResolveAttributeValue(element, "class");
        }

        private IrrContStyle GetContainerStyle(ImlElement element)
        {
            var classVal = ResolveAttributeValue(element, "class")?.ToLowerInvariant();
            var styleVal = ResolveAttributeValue(element, "style")?.ToLowerInvariant();
            var key = classVal;
            if (styleVal == "padding" || styleVal == "background")
                key = styleVal;
            return key switch
            {
                "padding" => IrrContStyle.Padding,
                "background" => IrrContStyle.Background,
                _ => IrrContStyle.None
            };
        }

        private IrrTextStyle GetTextStyle(ImlElement element)
        {
            var key = GetEffectiveStyleName(element)?.ToLowerInvariant();
            return key switch
            {
                "title" => IrrTextStyle.Title,
                "subtitle" => IrrTextStyle.Subtitle,
                "secondary" => IrrTextStyle.Secondary,
                _ => IrrTextStyle.Normal
            };
        }

        private IrrButStyle GetButtonStyle(ImlElement element)
        {
            var key = GetEffectiveStyleName(element)?.ToLowerInvariant();
            return key switch
            {
                "primary" => IrrButStyle.Primary,
                _ => IrrButStyle.Element
            };
        }

        private string GetFlexChildText(object child)
        {
            if (child is string s) return s;
            if (child is ExpressionValue ev && !string.IsNullOrWhiteSpace(ev.Expression))
            {
                try { return _evaluator.Evaluate(ev.Expression)?.ToString() ?? ""; }
                catch { return ""; }
            }
            return "";
        }

        private void RenderIcon(ImlElement element)
        {
            var style = GetEffectiveStyle(element);

            if (_layout != null)
            {
                var prevBg = GUI.backgroundColor;
                var prevContent = GUI.contentColor;
                if (style.Setters.TryGetValue("background", out var bgHex))
                    GUI.backgroundColor = GetColor(bgHex);
                if (style.Setters.TryGetValue("color", out var colorHex))
                    GUI.contentColor = GetColor(colorHex);
                _layout.Icon(GetIconStyle(element));
                GUI.backgroundColor = prevBg;
                GUI.contentColor = prevContent;
                return;
            }

            var iconType = GetIconStyle(element);
            int sz = 22;
            int radius = sz / 2;
            Color circleColor = GetColor(GetStyleString(style, "background", "#494F5C"));
            Color borderColor = GetColor(GetStyleString(style, "borderColor", "#313338_Hovered"));
            Color symbolColor = GetColor(GetStyleString(style, "color", "#FFFFFF"));

            var circle = GuiTextureFactory.GetCircle(sz, circleColor, borderColor, 2);
            var gs = new GUIStyle();
            gs.normal.background = circle;
            gs.fixedWidth = sz;
            gs.fixedHeight = sz;

            GUILayout.Box("", gs);
            var rect = GUILayoutUtility.GetLastRect();

            var symbolKind = iconType switch
            {
                IrrIconStyle.Information => GuiTextureFactory.IconSymbol.Information,
                IrrIconStyle.Success => GuiTextureFactory.IconSymbol.Success,
                IrrIconStyle.Warning => GuiTextureFactory.IconSymbol.Warning,
                IrrIconStyle.Error => GuiTextureFactory.IconSymbol.Error,
                IrrIconStyle.Stop => GuiTextureFactory.IconSymbol.Stop,
                _ => GuiTextureFactory.IconSymbol.Information
            };
            var sym = GuiTextureFactory.GetIconSymbol(sz, symbolKind, symbolColor);
            GUI.DrawTexture(rect, sym);
        }

        private void RenderArrowButton(ImlElement element)
        {
            var dirStr = ResolveAttributeValue(element, "direction");
            if (string.IsNullOrEmpty(dirStr)) dirStr = "right";
            var dir = dirStr.ToLowerInvariant() switch
            {
                "down" => GuiTextureFactory.ArrowDir.Down,
                "left" => GuiTextureFactory.ArrowDir.Left,
                "up" => GuiTextureFactory.ArrowDir.Up,
                _ => GuiTextureFactory.ArrowDir.Right
            };

            var style = GetEffectiveStyle(element);
            var bgHex = GetStyleString(style, "background", "#313338");
            var borderHex = GetStyleString(style, "borderColor", "#494F5C");
            var arrowHex = GetStyleString(style, "color", "#FFFFFF");
            int sz = 22, radius = 4;

            var tex = GuiTextureFactory.GetRoundedRect(sz, sz, radius, GetColor(bgHex), GetColor(borderHex), 1);
            var gs = new GUIStyle();
            gs.normal.background = gs.hover.background = gs.active.background = tex;
            gs.border = new RectOffset(radius, radius, radius, radius);
            gs.fixedWidth = sz;
            gs.fixedHeight = sz;

            bool clicked = GUILayout.Button("", gs, GUILayout.Width(sz), GUILayout.Height(sz));
            var rect = GUILayoutUtility.GetLastRect();
            var arr = GuiTextureFactory.GetArrow(sz, dir, GetColor(arrowHex));
            GUI.DrawTexture(rect, arr);

            if (clicked)
                HandleElementEvents(element);
        }

        private void RenderSelector(ImlElement element)
        {
            var valueBinding = element.GetExpression("value");
            var itemsStr = element.GetExpression("items");
            var onChanged = element.GetString("on-changed");

            if (string.IsNullOrEmpty(itemsStr)) return;

            var itemsObj = _evaluator.Evaluate(itemsStr);
            if (itemsObj is not IList items) return;

            string currentStr = "";
            if (!string.IsNullOrEmpty(valueBinding))
            {
                var val = _evaluator.Evaluate(valueBinding);
                currentStr = val?.ToString() ?? "";
            }

            bool changed = false;
            string newValue = currentStr;

            var elementStyle = GetEffectiveStyle(element);
            var selectedBg = GetStyleString(elementStyle, "selectedBg", "#D973A5");
            var selectedColor = GetStyleString(elementStyle, "selectedColor", "#FFFFFF");
            var unselectedBg = GetStyleString(elementStyle, "background", "#313338");
            var unselectedColor = GetStyleString(elementStyle, "color", "#E9ECEF");

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string key = "";
                string display = "";

                if (item is string s)
                {
                    key = s;
                    display = s;
                }
                else if (item != null)
                {
                    var t = item.GetType();
                    var keyProp = t.GetProperty("key");
                    var displayProp = t.GetProperty("displayName");
                    key = keyProp?.GetValue(item)?.ToString() ?? item.ToString();
                    display = displayProp?.GetValue(item)?.ToString() ?? item.ToString();
                }

                bool isSelected = key == currentStr;
                var optStyle = new ImlStyle();
                optStyle.Setters["background"] = isSelected ? selectedBg : unselectedBg;
                optStyle.Setters["color"] = isSelected ? selectedColor : unselectedColor;
                optStyle.Setters["radius"] = GetStyleString(elementStyle, "radius", "8");

                var gs = BuildGuiStyle(optStyle, element);
                if (GUILayout.Button(display, gs))
                {
                    if (!isSelected)
                    {
                        changed = true;
                        newValue = key;
                    }
                }
            }

            if (changed)
            {
                if (!string.IsNullOrEmpty(valueBinding))
                    SetContextValue(valueBinding, newValue);
                if (!string.IsNullOrEmpty(onChanged))
                    ScheduleEffect(() => InvokeHandler(onChanged, newValue));
            }
        }

        private IrrIconStyle GetIconStyle(ImlElement element)
        {
            var typeAttr = element.GetString("type");
            return typeAttr?.ToLowerInvariant() switch
            {
                "information" => IrrIconStyle.Information,
                "success" => IrrIconStyle.Success,
                "warning" => IrrIconStyle.Warning,
                "error" => IrrIconStyle.Error,
                "stop" => IrrIconStyle.Stop,
                _ => IrrIconStyle.Information
            };
        }

        private void HandleElementEvents(ImlElement element)
        {
            foreach (var kv in element.Attributes)
            {
                if (kv.Key.StartsWith("on-") && !kv.Key.StartsWith("data-on-"))
                {
                    var handlerSpec = ResolveAttributeValue(element, kv.Key);
                    if (!string.IsNullOrEmpty(handlerSpec))
                        InvokeHandlerString(handlerSpec);
                }
            }
        }

        private void InvokeHandlerString(string handlerSpec)
        {
            string handlerName = handlerSpec;
            string stringArg = null;
            var parenIdx = handlerSpec.IndexOf('(');
            if (parenIdx > 0 && handlerSpec.EndsWith(")"))
            {
                handlerName = handlerSpec.Substring(0, parenIdx).Trim();
                var argStr = handlerSpec.Substring(parenIdx + 1, handlerSpec.Length - parenIdx - 2).Trim();
                if ((argStr.StartsWith("'") && argStr.EndsWith("'")) ||
                    (argStr.StartsWith("\"") && argStr.EndsWith("\"")))
                {
                    stringArg = argStr.Substring(1, argStr.Length - 2);
                }
                else if (!string.IsNullOrEmpty(argStr))
                {
                    var evalResult = _evaluator.Evaluate(argStr);
                    stringArg = evalResult?.ToString();
                }
            }
            if (stringArg != null)
                InvokeHandler(handlerName, stringArg);
            else
                InvokeHandler(handlerName, null);
        }

        private void InvokeCommand(string commandPath)
        {
            if (string.IsNullOrEmpty(commandPath))
                return;

            var command = _evaluator.Evaluate(commandPath);
            if (command is System.Windows.Input.ICommand cmd && cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }
        }

        private void InvokeHandler(string handlerName, object parameter)
        {
            if (string.IsNullOrEmpty(handlerName))
                return;

            if (_handlers.TryGetValue(handlerName, out var handler))
            {
                handler();
            }
            else if (_genericHandlers.TryGetValue(handlerName, out var genericHandler))
            {
                genericHandler(parameter);
            }
        }

        private void ScheduleEffect(Action effect)
        {
            if (!_pendingEffects.Contains(effect))
            {
                _pendingEffects.Add(effect);
                _effectsScheduled = true;
            }
        }

        private void ProcessPendingEffects()
        {
            foreach (var effect in _pendingEffects)
            {
                try
                {
                    effect();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Iris.Iml] Effect error: {ex.Message}");
                }
            }
            _pendingEffects.Clear();
        }

        private void OnDataContextPropertyChanged(string propertyPath, object oldValue, object newValue)
        {
            Debug.Log($"[Iris.Iml] Property changed: {propertyPath} = {newValue}");
        }

        private ImlStyle GetEffectiveStyle(ImlElement element)
        {
            var merged = new ImlStyle();
            var tag = element.TagName?.ToLowerInvariant();
            var cls = element.GetString("class")?.ToLowerInvariant();
            var id = element.GetString("id")?.ToLowerInvariant();

            // 1. Selector-based styles (sorted by specificity ascending)
            foreach (var ss in _selectorStyles)
            {
                if (ss.Selector != null && ss.Selector.Matches(tag, cls, id))
                    foreach (var kv in ss.Setters)
                        merged.Setters[kv.Key] = kv.Value;
            }

            // 2. Named style from cache (via style="name")
            if (element.Attributes.TryGetValue("style", out var styleAttr))
            {
                if (styleAttr.Type == AttributeType.String || styleAttr.Type == AttributeType.Expression)
                {
                    var styleName = ResolveAttributeValue(element, "style");
                    if (!string.IsNullOrEmpty(styleName) && _styleCache.TryGetValue(styleName.ToLowerInvariant(), out var namedStyle))
                        foreach (var kv in namedStyle.Setters)
                            merged.Setters[kv.Key] = kv.Value;
                }
                // 3. Inline StyleObject: style={{ key: value, ... }}
                else if (styleAttr.Type == AttributeType.StyleObject && styleAttr.StyleEntries != null)
                {
                    foreach (var entry in styleAttr.StyleEntries)
                        if (!string.IsNullOrEmpty(entry.Property))
                            merged.Setters[entry.Property] = entry.Value;
                }
            }

            return merged;
        }

        private GUILayoutOption[] GetStyleOptions(ImlStyle style)
        {
            var options = new List<GUILayoutOption>();

            if (style.Setters.TryGetValue("width", out var widthStr) && int.TryParse(widthStr, out var width))
                options.Add(GUILayout.Width(width));

            if (style.Setters.TryGetValue("height", out var heightStr) && int.TryParse(heightStr, out var height))
                options.Add(GUILayout.Height(height));

            if (style.Setters.TryGetValue("minWidth", out var minW) && int.TryParse(minW, out var minWidth))
                options.Add(GUILayout.MinWidth(minWidth));

            if (style.Setters.TryGetValue("maxWidth", out var maxW) && int.TryParse(maxW, out var maxWidth))
                options.Add(GUILayout.MaxWidth(maxWidth));

            if (style.Setters.TryGetValue("minHeight", out var minH) && int.TryParse(minH, out var minHeight))
                options.Add(GUILayout.MinHeight(minHeight));

            if (style.Setters.TryGetValue("maxHeight", out var maxH) && int.TryParse(maxH, out var maxHeight))
                options.Add(GUILayout.MaxHeight(maxHeight));

            options.Add(GUILayout.ExpandWidth(!style.Setters.ContainsKey("width")));
            options.Add(GUILayout.ExpandHeight(!style.Setters.ContainsKey("height")));

            return options.ToArray();
        }

        private Color GetColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return Color.white;

            if (hex.StartsWith("#"))
            {
                hex = hex.Substring(1);
                if (hex.Length >= 6)
                {
                    var r = Convert.ToByte(hex.Substring(0, 2), 16) / 255f;
                    var g = Convert.ToByte(hex.Substring(2, 2), 16) / 255f;
                    var b = Convert.ToByte(hex.Substring(4, 2), 16) / 255f;
                    float a = hex.Length >= 8 ? Convert.ToByte(hex.Substring(6, 2), 16) / 255f : 1f;
                    return new Color(r, g, b, a);
                }
            }

            return Color.white;
        }

        private static Color MultiplyColor(Color c, float factor)
        {
            return new Color(c.r * factor, c.g * factor, c.b * factor, c.a);
        }

        private string GetStyleString(ImlStyle style, string key, string fallback = "")
        {
            return style.Setters.TryGetValue(key, out var v) ? v : fallback;
        }

        private int GetStyleInt(ImlStyle style, string key, int fallback = 0)
        {
            if (style.Setters.TryGetValue(key, out var v) && int.TryParse(v, out var n))
                return n;
            return fallback;
        }

        private float GetStyleFloat(ImlStyle style, string key, float fallback = 0f)
        {
            if (style.Setters.TryGetValue(key, out var v) && float.TryParse(v, out var n))
                return n;
            return fallback;
        }

        private GUIStyle BuildGuiStyle(ImlStyle style, ImlElement element)
        {
            string tag = element?.TagName?.ToLowerInvariant() ?? "";
            string cls = element?.GetString("class")?.ToLowerInvariant() ?? "";

            string bgHex = GetStyleString(style, "background", "");
            string colorHex = GetStyleString(style, "color", "");
            string borderHex = GetStyleString(style, "borderColor", "");
            int radius = GetStyleInt(style, "radius");
            int borderWidth = GetStyleInt(style, "borderWidth");
            int fontSize = GetStyleInt(style, "fontSize");
            int marginVal = GetStyleInt(style, "margin");
            int paddingVal = GetStyleInt(style, "padding");

            string cacheKey = $"{tag}.{cls}.bg:{bgHex}.cl:{colorHex}.bd:{borderHex}.r:{radius}.bw:{borderWidth}.fs:{fontSize}.m:{marginVal}.p:{paddingVal}";
            if (_guiStyleCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var gs = new GUIStyle();
            gs.richText = true;
            gs.wordWrap = true;
            gs.clipping = TextClipping.Overflow;

            if (fontSize > 0) gs.fontSize = fontSize;
            if (!string.IsNullOrEmpty(colorHex)) gs.normal.textColor = GetColor(colorHex);

            if (marginVal > 0) gs.margin = new RectOffset(marginVal, marginVal, marginVal, marginVal);
            if (paddingVal > 0) gs.padding = new RectOffset(paddingVal, paddingVal, paddingVal, paddingVal);

            if (!string.IsNullOrEmpty(bgHex))
            {
                Color bg = GetColor(bgHex);
                int texSize = Mathf.Max(2 * radius + 2, 16);
                Color? borderCol = !string.IsNullOrEmpty(borderHex) ? GetColor(borderHex) : null;

                gs.normal.background = GuiTextureFactory.GetRoundedRect(texSize, texSize, radius, bg, borderCol, borderWidth);
                gs.hover.background = GuiTextureFactory.GetRoundedRect(texSize, texSize, radius, MultiplyColor(bg, 1.08f), borderCol, borderWidth);
                gs.active.background = GuiTextureFactory.GetRoundedRect(texSize, texSize, radius, MultiplyColor(bg, 0.88f), borderCol, borderWidth);

                if (radius > 0)
                    gs.border = new RectOffset(radius, radius, radius, radius);
            }

            _guiStyleCache[cacheKey] = gs;
            return gs;
        }

        private GUIStyle BuildTextStyle(ImlStyle style, ImlElement element)
        {
            string tag = element?.TagName?.ToLowerInvariant() ?? "";
            string cls = element?.GetString("class")?.ToLowerInvariant() ?? "";
            string colorHex = GetStyleString(style, "color", "");
            int fontSize = GetStyleInt(style, "fontSize");

            string cacheKey = $"txt.{cls}.cl:{colorHex}.fs:{fontSize}";
            if (_guiStyleCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var gs = new GUIStyle();
            gs.richText = true;
            gs.wordWrap = true;
            gs.clipping = TextClipping.Overflow;
            gs.alignment = TextAnchor.MiddleLeft;

            if (fontSize > 0) gs.fontSize = fontSize;
            if (!string.IsNullOrEmpty(colorHex)) gs.normal.textColor = GetColor(colorHex);

            _guiStyleCache[cacheKey] = gs;
            return gs;
        }

        private Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (_textureCache.TryGetValue(path, out var cached))
                return cached;

            try
            {
                Texture2D texture = null;

                if (path.StartsWith("@/") || path.StartsWith("@"))
                {
                    var fullPath = _parser.ResolvePath(path);
                    if (File.Exists(fullPath))
                    {
                        var bytes = File.ReadAllBytes(fullPath);
                        texture = new Texture2D(1, 1);
                        texture.LoadImage(bytes);
                    }
                }
                else if (path.StartsWith("bundle://"))
                {
                    var bundlePath = path.Substring(9);
                    // AssetBundle loading would go here
                }
                else if (path.StartsWith("addr://"))
                {
                    // Addressables loading would go here
                }

                if (texture != null)
                    _textureCache[path] = texture;

                return texture;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] Failed to load texture: {path} - {ex.Message}");
                return null;
            }
        }
    }

    public class ImlStyle
    {
        public string Name { get; set; }
        public string Extends { get; set; }
        public StyleSelector Selector { get; set; }
        public Dictionary<string, string> Setters { get; set; } = new();
    }

    namespace RendererInternal
        {
            public class DrawArgs
            {
                public object Context { get; set; }
            }
        }
}
