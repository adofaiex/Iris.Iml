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
            ProcessResources();
            _styleCache.Clear();
            _forEachCollections.Clear();
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
                    if (childElement.TagName == "Style")
                    {
                        var style = ParseStyle(childElement);
                        _styleCache[style.Name.ToLowerInvariant()] = style;
                    }
                }
            }
        }

        private ImlStyle ParseStyle(ImlElement element)
        {
            var style = new ImlStyle
            {
                Name = element.GetString("name"),
                Extends = element.GetString("extends")
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
                if (isHorizontal)
                    _layout.BeginHorizontal(containerStyle, options.ToArray());
                else
                    _layout.BeginVertical(containerStyle, options.ToArray());
            }
            else
            {
                if (isHorizontal)
                    GUILayout.BeginHorizontal(options.ToArray());
                else
                    GUILayout.BeginVertical(options.ToArray());
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
                    else if (children[i] is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        if (_layout != null)
                            _layout.Text(text, IrrTextStyle.Normal);
                        else
                            GUILayout.Label(text);
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
                // Mod root: go up from ui/ to Resources/ to frontline/ to Iridium/
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
            var useAttr = element.GetString("use");
            var richTextAttr = element.GetString("richText");
            bool richText = richTextAttr == "true";

            if (_layout != null)
            {
                // use/richText are for GameObject target; GUILayout ignores use, richText is handled by GUIStyle
                _layout.Text(text, GetTextStyle(element));
            }
            else
            {
                var style = GetEffectiveStyle(element);
                var guiStyle = new GUIStyle(GUI.skin.label);
                guiStyle.richText = richText;
                var prevBg = GUI.backgroundColor;
                var prevContent = GUI.contentColor;
                GUI.backgroundColor = Color.white;
                GUI.contentColor = GetColor(style.Setters.TryGetValue("color", out var colorVal) ? colorVal : "#FFFFFF");
                try { GUILayout.Label(text, guiStyle, GetStyleOptions(style)); }
                finally { GUI.contentColor = prevContent; GUI.backgroundColor = prevBg; }
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
            bool clicked = false;

            if (_layout != null)
                clicked = _layout.Button(text, GetButtonStyle(element));
            else
            {
                var style = GetEffectiveStyle(element);
                var prevBg = GUI.backgroundColor;
                var prevContent = GUI.contentColor;
                GUI.backgroundColor = GetColor(style.Setters.TryGetValue("background", out var bgVal) ? bgVal : "#333333");
                GUI.contentColor = GetColor(style.Setters.TryGetValue("color", out var colorVal) ? colorVal : "#FFFFFF");
                try { clicked = GUILayout.Button(text, GetStyleOptions(style)); }
                finally { GUI.backgroundColor = prevBg; GUI.contentColor = prevContent; }
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
                if (_layout != null)
                    _layout.Text(text, IrrTextStyle.Normal);
                else
                    GUILayout.Label(text);
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
                GUI.changed = false;
                bool newValue = GUILayout.Toggle(currentValue, "", GUILayout.Width(40), GUILayout.Height(20));
                result = GUI.changed ? newValue : (bool?)null;
            }

            // Two-way binding: write the new value back to the data context
            // BEFORE invoking the on-changed handler, so handlers can also
            // rely on the bound field being up-to-date (in addition to the
            // explicit `value = obj` they receive in their handler body).
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
                if (_layout != null)
                    _layout.Text(text, IrrTextStyle.Normal);
                else
                    GUILayout.Label(text);
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
                GUI.changed = false;
                bool newValue = GUILayout.Toggle(currentValue, "", GUILayout.Width(40), GUILayout.Height(20));
                result = GUI.changed ? newValue : (bool?)null;
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

            GUI.changed = false;
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true), GUILayout.MaxWidth(200));
            float newValue = GUILayout.HorizontalSlider(currentValue, min, max);
            if (showValue)
                GUILayout.Label(newValue.ToString("F2"), GUILayout.Width(50));
            GUILayout.EndHorizontal();

            // Two-way binding: write the new value back to the data context
            // BEFORE invoking the on-changed handler.
            if (GUI.changed && !string.IsNullOrEmpty(valueBinding))
                SetContextValue(valueBinding, newValue);
            if (GUI.changed && !string.IsNullOrEmpty(onChanged))
            {
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
                newValue = _layout.TextField(currentValue);
            }
            else
            {
                GUI.changed = false;
                newValue = GUILayout.TextField(currentValue, GUILayout.ExpandWidth(true));
                if (!GUI.changed)
                    newValue = null;
            }

            // Two-way binding: write the new value back to the data context
            // BEFORE invoking the user handler, so handlers like
            // Settings.OnJudgeTextChanged (which only call Save()) actually
            // have the new value to persist. Without this, the bound CLR
            // field stays at its old value. (Bug: "判定文本无法修改".)
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
            if (_layout != null)
                _layout.Separator();
            else
            {
                GUILayout.Space(1);
                var color = GetColor(element.GetString("color") ?? "#333333");
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = color;
                try { GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); }
                finally { GUI.backgroundColor = prevBg; }
                GUILayout.Space(1);
            }
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

        private void RenderIcon(ImlElement element)
        {
            if (_layout == null) return;
            var iconStyle = GetIconStyle(element);
            _layout.Icon(iconStyle);
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
            var className = element.GetString("class");
            var styleName = ResolveAttributeValue(element, "style");

            if (!string.IsNullOrEmpty(styleName) && _styleCache.TryGetValue(styleName.ToLowerInvariant(), out var style))
                return style;

            if (!string.IsNullOrEmpty(className) && _styleCache.TryGetValue(className.ToLowerInvariant(), out style))
                return style;

            return new ImlStyle();
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

            if (hex.StartsWith("#") && hex.Length == 7)
            {
                var r = Convert.ToByte(hex.Substring(1, 2), 16) / 255f;
                var g = Convert.ToByte(hex.Substring(3, 2), 16) / 255f;
                var b = Convert.ToByte(hex.Substring(5, 2), 16) / 255f;
                return new Color(r, g, b);
            }

            return Color.white;
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
        public Dictionary<string, string> Setters { get; } = new();
    }

    namespace RendererInternal
        {
            public class DrawArgs
            {
                public object Context { get; set; }
            }
        }
}
