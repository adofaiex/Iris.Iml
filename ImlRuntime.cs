using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// Renderer-agnostic runtime shared by the IMGUI and UGUI backends:
    /// document loading, style/resource processing, event dispatch, effects,
    /// texture loading, scroll state and the AST → <see cref="ImlNode"/> tree
    /// builder. Both backends only walk the produced tree and draw it.
    /// </summary>
    public class ImlRuntime
    {
        public readonly ImlParser Parser = new();
        public readonly StyleEngine Styles = new();
        public ImlDocument Document { get; private set; }
        public IBindingContext DataContext { get; private set; }
        public ExpressionEvaluator Evaluator { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Log sink; falls back to Unity Debug when null.</summary>
        public Action<string> LogDelegate { get; set; }

        /// <summary>When true, effects queue until <see cref="FlushEffects"/>
        /// (IMGUI); when false they run immediately (UGUI).</summary>
        public bool DeferEffects = true;

        /// <summary>Set whenever the document/context needs a rebuild (UGUI).</summary>
        public bool Dirty { get; set; }

        /// <summary>Raised after a hot reload re-parsed the document.</summary>
        public event Action Reloaded;

        private readonly Dictionary<string, Action> _handlers = new();
        private readonly Dictionary<string, Action<object>> _genericHandlers = new();
        private readonly Dictionary<string, Func<object[], object>> _registeredFunctions = new();
        private readonly Dictionary<string, Action<Rect, RendererInternal.DrawArgs>> _drawHandlers = new();
        private readonly Dictionary<string, ImlDocument> _referenceCache = new();
        private readonly Dictionary<string, Texture2D> _textureCache = new();
        private readonly Dictionary<string, float> _scrollPositions = new();
        private readonly List<Action> _pendingEffects = new();
        private bool _effectsScheduled;

        private bool _hotReloadEnabled;
        private FileSystemWatcher _fileWatcher;
        private float _lastReloadTime;

        public IReadOnlyDictionary<string, Action<Rect, RendererInternal.DrawArgs>> DrawHandlers => _drawHandlers;
        public bool HasDrawHandlers => _drawHandlers.Count > 0;

        // ── logging ────────────────────────────────────────────────────────

        protected void Log(string message)
        {
            if (LogDelegate != null) LogDelegate(message);
            else Debug.Log($"[Iris.Iml] {message}");
        }

        // ── context / registration ─────────────────────────────────────────

        public void SetDataContext(object data)
        {
            DataContext = new BindingContext(data);
            Evaluator = new ExpressionEvaluator((BindingContext)DataContext);
            foreach (var kv in _registeredFunctions)
                Evaluator.RegisterFunction(kv.Key, kv.Value);
            Dirty = true;
        }

        /// <summary>Write a value back to the bound property path (two-way binding).</summary>
        public void SetContextValue(string propertyPath, object value)
        {
            if (string.IsNullOrEmpty(propertyPath)) return;
            try
            {
                DataContext?.SetValue(propertyPath, value);
            }
            catch (Exception ex)
            {
                Log($"SetContextValue('{propertyPath}') failed: {ex.Message}");
            }
        }

        public void RegisterHandler(string name, Action handler) => _handlers[name] = handler;

        public void RegisterHandler(string name, Action<object> handler) => _genericHandlers[name] = handler;

        public void RegisterHandler<T>(string name, Action<T> handler)
            => _genericHandlers[name] = obj => handler(obj is T t ? t : default);

        public void RegisterFunction(string name, Func<object[], object> func)
        {
            _registeredFunctions[name] = func;
            Evaluator?.RegisterFunction(name, func);
        }

        public void RegisterDrawHandler(string name, Action<Rect, RendererInternal.DrawArgs> handler)
            => _drawHandlers[name] = handler;

        // ── loading ────────────────────────────────────────────────────────

        public void LoadFile(string filePath)
        {
            CurrentFilePath = filePath;
            Document = Parser.Parse(filePath);
            PrepareDocument();
        }

        public void LoadContent(string imlContent, string basePath = "")
        {
            Document = Parser.ParseContent(imlContent, basePath);
            if (!string.IsNullOrEmpty(basePath))
                CurrentFilePath = Path.Combine(basePath, "_generated.iml");
            PrepareDocument();
        }

        private void PrepareDocument()
        {
            Styles.Reset();
            _referenceCache.Clear();
            ProcessResources(Document);
            Styles.FinalizeRegistration();
            _scrollPositions.Clear();
            Dirty = true;
            if (_hotReloadEnabled) StartFileWatcher();
            Reloaded?.Invoke();
        }

        private void ProcessResources(ImlDocument doc)
        {
            if (doc?.Root == null) return;
            foreach (var child in doc.Root.Children)
                if (child is ImlElement el && el.TagName == "Resources")
                    ProcessResourceElement(el);
        }

        private void ProcessResourceElement(ImlElement element)
        {
            foreach (var child in element.Children)
            {
                if (child is not ImlElement childElement) continue;
                if (childElement.TagName == "Reference")
                {
                    var path = childElement.GetString("path");
                    if (!string.IsNullOrEmpty(path))
                        ProcessReferencedFile(path);
                }
                else if (childElement.TagName == "Style")
                {
                    Styles.AddStyle(ParseStyle(childElement));
                }
            }
            Styles.FinalizeRegistration();
        }

        private void ProcessReferencedFile(string path)
        {
            try
            {
                var refPath = ResolveReferencePath(path);
                if (!File.Exists(refPath)) return;
                if (_referenceCache.ContainsKey(refPath)) return;
                var refDoc = Parser.Parse(refPath);
                _referenceCache[refPath] = refDoc;
                ProcessResources(refDoc);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] Failed to process referenced resources: {path} - {ex.Message}");
            }
        }

        private static ImlStyle ParseStyle(ImlElement element)
        {
            var style = new ImlStyle
            {
                Name = element.GetString("name"),
                Extends = element.GetString("extends"),
                Selector = StyleSelector.Parse(element.GetString("on")),
            };

            foreach (var child in element.Children)
            {
                if (child is not ImlElement childElement) continue;
                if (childElement.TagName == "Setter")
                {
                    var property = childElement.GetString("property");
                    var value = childElement.GetString("value");
                    if (!string.IsNullOrEmpty(property))
                        style.Setters[StyleEngine.NormalizeKey(property)] = value ?? "";
                }
                else
                {
                    // Custom property tag: <fontSize value="14" />
                    var value = childElement.GetString("value");
                    if (!string.IsNullOrEmpty(value))
                        style.Setters[StyleEngine.NormalizeKey(childElement.TagName)] = value;
                }
            }

            return style;
        }

        public string ResolveReferencePath(string path)
        {
            var basePath = Path.GetDirectoryName(CurrentFilePath) ?? "";
            if (path.StartsWith("@/", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(basePath, "..", "..", path.Substring(2)));
            if (path.StartsWith("@", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(basePath, path.Substring(1)));
            return Path.Combine(basePath, path);
        }

        // ── hot reload ─────────────────────────────────────────────────────

        public void SetHotReload(bool enabled)
        {
            _hotReloadEnabled = enabled;
            if (enabled && !string.IsNullOrEmpty(CurrentFilePath)) StartFileWatcher();
            else StopFileWatcher();
        }

        private void StartFileWatcher()
        {
            StopFileWatcher();
            var directory = Path.GetDirectoryName(CurrentFilePath);
            var fileName = Path.GetFileName(CurrentFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            _fileWatcher = new FileSystemWatcher(directory, fileName);
            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
        }

        private void StopFileWatcher()
        {
            if (_fileWatcher == null) return;
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Changed -= OnFileChanged;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (Time.realtimeSinceStartup - _lastReloadTime <= 0.5f) return;
            _lastReloadTime = Time.realtimeSinceStartup;
            try { LoadFile(CurrentFilePath); }
            catch (Exception ex) { Log($"Hot reload failed: {ex.Message}"); }
        }

        public void RequestReload()
        {
            if (string.IsNullOrEmpty(CurrentFilePath)) return;
            try { LoadFile(CurrentFilePath); }
            catch (Exception ex) { Log($"Reload failed: {ex.Message}"); }
        }

        public bool HotReloadEnabled => _hotReloadEnabled;

        // ── attribute / event dispatch ─────────────────────────────────────

        /// <summary>Resolve an attribute value regardless of its type.</summary>
        public string ResolveAttributeValue(ImlElement element, string attrName)
        {
            if (element == null || !element.Attributes.TryGetValue(attrName, out var attr))
                return "";

            switch (attr.Type)
            {
                case AttributeType.String:
                    return attr.StringValue ?? "";
                case AttributeType.Expression:
                    try
                    {
                        return Evaluator?.Evaluate(attr.Expression)?.ToString() ?? "";
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
                            try { sb.Append(Evaluator?.Evaluate(part.Value)?.ToString() ?? ""); }
                            catch { /* keep empty */ }
                        }
                        else sb.Append(part.Value);
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

        /// <summary>Evaluate a boolean-ish attribute (visible, condition, ...). Missing → true.</summary>
        public bool EvalBoolAttr(ImlElement element, string attrName, bool fallback = true)
        {
            if (element == null || !element.Attributes.TryGetValue(attrName, out var attr))
                return fallback;
            switch (attr.Type)
            {
                case AttributeType.Boolean:
                    return attr.BoolValue;
                case AttributeType.Expression:
                    try { return Evaluator.EvaluateBoolean(attr.Expression); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Iris.Iml] Failed to evaluate {attrName} '{attr.Expression}': {ex.Message}");
                        return fallback;
                    }
                case AttributeType.String:
                    return StyleValues.ParseBool(attr.StringValue, fallback);
                default:
                    return fallback;
            }
        }

        /// <summary>Invoke every <c>on-*</c> attribute of the element (no argument).</summary>
        public void HandleElementEvents(ImlElement element)
        {
            if (element == null) return;
            foreach (var kv in element.Attributes)
            {
                if (!kv.Key.StartsWith("on-", StringComparison.Ordinal) ||
                    kv.Key.StartsWith("data-on-", StringComparison.Ordinal))
                    continue;
                var handlerSpec = ResolveAttributeValue(element, kv.Key);
                if (!string.IsNullOrEmpty(handlerSpec))
                    InvokeHandlerString(handlerSpec);
            }
        }

        /// <summary>Invoke one specific event attribute, passing <paramref name="param"/>.</summary>
        public void InvokeElementEvent(ImlElement element, string attrName, object param)
        {
            if (element == null) return;
            var spec = ResolveAttributeValue(element, attrName);
            if (string.IsNullOrEmpty(spec)) return;
            if (spec.IndexOf('(') > 0 && spec.EndsWith(")", StringComparison.Ordinal))
                InvokeHandlerString(spec);
            else
                InvokeHandler(spec, param);
        }

        public void InvokeHandlerString(string handlerSpec)
        {
            if (string.IsNullOrEmpty(handlerSpec)) return;
            string handlerName = handlerSpec;
            string stringArg = null;
            var parenIdx = handlerSpec.IndexOf('(');
            if (parenIdx > 0 && handlerSpec.EndsWith(")", StringComparison.Ordinal))
            {
                handlerName = handlerSpec.Substring(0, parenIdx).Trim();
                var argStr = handlerSpec.Substring(parenIdx + 1, handlerSpec.Length - parenIdx - 2).Trim();
                if ((argStr.StartsWith("'", StringComparison.Ordinal) && argStr.EndsWith("'", StringComparison.Ordinal)) ||
                    (argStr.StartsWith("\"", StringComparison.Ordinal) && argStr.EndsWith("\"", StringComparison.Ordinal)))
                {
                    stringArg = argStr.Substring(1, argStr.Length - 2);
                }
                else if (!string.IsNullOrEmpty(argStr))
                {
                    try { stringArg = Evaluator?.Evaluate(argStr)?.ToString(); }
                    catch (Exception ex) { Log($"Handler arg '{argStr}' failed: {ex.Message}"); }
                }
            }
            InvokeHandler(handlerName, stringArg);
        }

        public void InvokeCommand(string commandPath)
        {
            if (string.IsNullOrEmpty(commandPath) || Evaluator == null) return;
            try
            {
                if (Evaluator.Evaluate(commandPath) is System.Windows.Input.ICommand cmd && cmd.CanExecute(null))
                    cmd.Execute(null);
            }
            catch (Exception ex)
            {
                Log($"Command '{commandPath}' failed: {ex.Message}");
            }
        }

        public void InvokeHandler(string handlerName, object parameter)
        {
            if (string.IsNullOrEmpty(handlerName)) return;
            if (_handlers.TryGetValue(handlerName, out var handler))
            {
                try { handler(); }
                catch (Exception ex) { Log($"Handler '{handlerName}' error: {ex.Message}"); }
            }
            else if (_genericHandlers.TryGetValue(handlerName, out var genericHandler))
            {
                try { genericHandler(parameter); }
                catch (Exception ex) { Log($"Handler '{handlerName}' error: {ex.Message}"); }
            }
        }

        // ── effects ────────────────────────────────────────────────────────

        public void ScheduleEffect(Action effect)
        {
            if (!DeferEffects)
            {
                try { effect(); }
                catch (Exception ex) { Log($"Effect error: {ex.Message}"); }
                return;
            }
            if (_pendingEffects.Contains(effect)) return;
            _pendingEffects.Add(effect);
            _effectsScheduled = true;
        }

        public void FlushEffects()
        {
            if (!_effectsScheduled) return;
            _effectsScheduled = false;
            foreach (var effect in _pendingEffects)
            {
                try { effect(); }
                catch (Exception ex) { Log($"Effect error: {ex.Message}"); }
            }
            _pendingEffects.Clear();
        }

        // ── scroll state ───────────────────────────────────────────────────

        public float GetScrollPos(string key) =>
            !string.IsNullOrEmpty(key) && _scrollPositions.TryGetValue(key, out var v) ? v : 0f;

        public void SetScrollPos(string key, float value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _scrollPositions[key] = value;
        }

        // ── templates ──────────────────────────────────────────────────────

        public ImlElement FindTemplate(string name)
        {
            if (Document?.Root == null) return null;
            foreach (var child in Document.Root.Children)
            {
                if (child is ImlElement el && el.TagName == "Resources")
                {
                    foreach (var resource in el.Children)
                    {
                        if (resource is ImlElement res && res.TagName == "Template" &&
                            res.GetString("name") == name)
                            return res;
                    }
                }
            }
            return null;
        }

        // ── textures ───────────────────────────────────────────────────────

        public Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_textureCache.TryGetValue(path, out var cached)) return cached;

            try
            {
                Texture2D texture = null;
                if (path.StartsWith("@", StringComparison.Ordinal))
                {
                    var fullPath = Parser.ResolvePath(path);
                    if (File.Exists(fullPath))
                    {
                        var bytes = File.ReadAllBytes(fullPath);
                        texture = new Texture2D(1, 1);
                        texture.LoadImage(bytes);
                    }
                }
                // bundle:// and addr:// are placeholders for future asset loading.

                if (texture != null) _textureCache[path] = texture;
                return texture;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] Failed to load texture: {path} - {ex.Message}");
                return null;
            }
        }

        // ── tree building ──────────────────────────────────────────────────

        /// <summary>Build the node tree for the current document/context state.</summary>
        public ImlNode BuildTree()
        {
            if (Document?.Root == null || Evaluator == null) return null;
            var root = new ImlNode
            {
                Kind = ImlNodeKind.Root,
                Tag = "Iris",
                Style = Styles.Resolve(Document.Root, null, ImlStateFlags.None, ResolveAttributeValue),
            };
            BuildChildrenInto(Document.Root, root, root.Children);
            return root;
        }

        private void BuildChildrenInto(ImlElement container, ImlNode owner, List<ImlNode> dest)
        {
            foreach (var child in container.Children)
            {
                if (child is ImlElement el)
                {
                    var slot = el.GetString("slot")?.ToLowerInvariant();
                    var target = slot switch
                    {
                        "background" => owner.BgChildren,
                        "foreground" => owner.FgChildren,
                        _ => dest,
                    };
                    BuildElement(el, owner, target);
                }
                else if (child is ExpressionValue ev && !string.IsNullOrWhiteSpace(ev.Expression))
                {
                    object evaluated;
                    try { evaluated = Evaluator.Evaluate(ev.Expression); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Iris.Iml] Text expression '{ev.Expression}' failed: {ex.Message}");
                        continue;
                    }
                    if (evaluated is ImlElement nestedEl) { BuildElement(nestedEl, owner, dest); continue; }
                    var text = evaluated?.ToString();
                    if (!string.IsNullOrEmpty(text))
                        dest.Add(MakeText(null, owner, text));
                }
                else if (child is string text && !string.IsNullOrWhiteSpace(text))
                {
                    dest.Add(MakeText(null, owner, text));
                }
            }
        }

        private ImlNode MakeText(ImlElement el, ImlNode owner, string text)
        {
            return new ImlNode
            {
                Kind = ImlNodeKind.Text,
                Element = el,
                Tag = el?.TagName ?? "Text",
                Parent = owner,
                Text = text,
                Style = Styles.Resolve(el, owner.Style, ImlStateFlags.None, ResolveAttributeValue),
            };
        }

        private void BuildElement(ImlElement el, ImlNode owner, List<ImlNode> dest)
        {
            if (el == null) return;

            // visible gate (all tags)
            if (el.HasAttribute("visible") && !EvalBoolAttr(el, "visible"))
                return;

            switch (el.TagName)
            {
                case "If":
                    if (el.HasAttribute("condition") && !EvalBoolAttr(el, "condition"))
                        return;
                    BuildChildrenInto(el, owner, dest);
                    return;

                case "Iris":
                case "":
                    BuildChildrenInto(el, owner, dest);
                    return;

                case "Reference":
                    BuildReference(el, owner, dest);
                    return;

                case "ForEach":
                    BuildForEach(el, owner, dest);
                    return;

                case "Resources":
                case "Style":
                case "Template":
                case "StyleSelector":
                case "Case":
                case "Slot":
                case "References":
                    return;
            }

            var node = CreateNode(el, owner);

            switch (el.TagName)
            {
                case "View":
                case "HBox":
                case "VBox":
                    node.Kind = ImlNodeKind.Box;
                    break;

                case "Selector":
                    BuildSelector(el, owner, dest);
                    return;

                case "Fill":
                    node.Kind = ImlNodeKind.Fill;
                    break;

                case "Spacer":
                case "Box":
                    node.Kind = ImlNodeKind.Spacer;
                    break;

                default:
                    node.Kind = el.TagName switch
                    {
                        "Text" => ImlNodeKind.Text,
                        "Link" => ImlNodeKind.Link,
                        "Image" => ImlNodeKind.Image,
                        "Button" => ImlNodeKind.Button,
                        "Switch" => ImlNodeKind.Switch,
                        "Checkbox" => ImlNodeKind.Checkbox,
                        "Slider" => ImlNodeKind.Slider,
                        "TextField" => ImlNodeKind.TextField,
                        "TextArea" => ImlNodeKind.TextArea,
                        "Separator" => ImlNodeKind.Separator,
                        "Icon" => ImlNodeKind.Icon,
                        "ArrowButton" => ImlNodeKind.ArrowButton,
                        "ScrollView" => ImlNodeKind.ScrollView,
                        "CustomCanvas" => ImlNodeKind.CustomCanvas,
                        _ => (ImlNodeKind)(-1),
                    };
                    if ((int)node.Kind < 0)
                    {
                        Debug.LogWarning($"[Iris.Iml] Unknown element: {el.TagName}");
                        return;
                    }
                    break;
            }

            FillNodeData(node, el);

            // Switch/Checkbox inline label: old renderers drew the text as a
            // preceding flow item — keep that as a sibling Text node.
            if ((node.Kind == ImlNodeKind.Switch || node.Kind == ImlNodeKind.Checkbox) &&
                !string.IsNullOrEmpty(node.Text))
            {
                var labelStyle = new ImlStyle();
                foreach (var kv in node.Style.Setters) labelStyle.Setters[kv.Key] = kv.Value;
                labelStyle.Setters["marginRight"] = "4";   // breathing room before the control
                dest.Add(new ImlNode
                {
                    Kind = ImlNodeKind.Text,
                    Element = null,
                    Tag = "Text",
                    Parent = owner,
                    Text = node.Text,
                    Style = labelStyle,   // color/fontSize from the control style
                });
            }
            dest.Add(node);

            // Container children (skipped for leaf kinds that never have children)
            if (node.Kind == ImlNodeKind.Box || node.Kind == ImlNodeKind.ScrollView)
                BuildChildrenInto(el, node, node.Children);
        }

        private ImlNode CreateNode(ImlElement el, ImlNode owner)
        {
            // Checked/disabled state feeds pseudo classes at build time.
            var state = ImlStateFlags.None;
            var valueExpr = el.GetExpression("value");
            bool checkedValue = false;
            if (!string.IsNullOrEmpty(valueExpr) &&
                (el.TagName == "Switch" || el.TagName == "Checkbox"))
            {
                try { checkedValue = Evaluator.Evaluate(valueExpr) is bool b && b; }
                catch { checkedValue = false; }
                if (checkedValue) state |= ImlStateFlags.Checked;
            }
            if (el.HasAttribute("disabled") && EvalBoolAttr(el, "disabled", false))
                state |= ImlStateFlags.Disabled;

            var node = new ImlNode
            {
                Element = el,
                Tag = el.TagName,
                Parent = owner,
                Style = Styles.Resolve(el, owner.Style, state, ResolveAttributeValue),
            };
            node.Checked = checkedValue;
            node.BuildState = state;
            return node;
        }

        private void FillNodeData(ImlNode node, ImlElement el)
        {
            switch (node.Kind)
            {
                case ImlNodeKind.Text:
                    node.Text = ResolveAttributeValue(el, "text");
                    break;

                case ImlNodeKind.Link:
                    node.Text = ResolveAttributeValue(el, "text");
                    node.Url = el.GetString("url");
                    break;

                case ImlNodeKind.Image:
                    node.Source = ResolveAttributeValue(el, "source");
                    if (!node.Style.Setters.ContainsKey("width")) node.Style.Setters["width"] = "100";
                    if (!node.Style.Setters.ContainsKey("height")) node.Style.Setters["height"] = "100";
                    break;

                case ImlNodeKind.Button:
                    node.Text = ResolveAttributeValue(el, "text");
                    break;

                case ImlNodeKind.Switch:
                case ImlNodeKind.Checkbox:
                    node.ValueBinding = el.GetExpression("value");
                    node.OnChange = ResolveAttributeValue(el, "on-changed");
                    break;

                case ImlNodeKind.Slider:
                    node.ValueBinding = el.GetExpression("value");
                    node.OnChange = ResolveAttributeValue(el, "on-changed");
                    node.Min = StyleValues.TryParseNumber(el.GetString("min"), out var min) ? min : 0f;
                    node.Max = StyleValues.TryParseNumber(el.GetString("max"), out var max) ? max : 100f;
                    node.Step = StyleValues.TryParseNumber(el.GetString("step"), out var step) ? step : 0f;
                    node.ShowValue = StyleValues.ParseBool(el.GetString("showValue"), false);
                    try
                    {
                        var val = string.IsNullOrEmpty(node.ValueBinding)
                            ? node.Min
                            : Convert.ToSingle(Evaluator.Evaluate(node.ValueBinding));
                        node.FloatValue = Mathf.Clamp(val, node.Min, node.Max);
                    }
                    catch { node.FloatValue = node.Min; }
                    break;

                case ImlNodeKind.TextField:
                case ImlNodeKind.TextArea:
                    node.ValueBinding = el.GetExpression("value");
                    node.OnSubmit = ResolveAttributeValue(el, "on-text-submit");
                    node.OnChange = ResolveAttributeValue(el, "on-changed");
                    try
                    {
                        node.Text = string.IsNullOrEmpty(node.ValueBinding)
                            ? ""
                            : Evaluator.Evaluate(node.ValueBinding)?.ToString() ?? "";
                    }
                    catch { node.Text = ""; }
                    if (node.Kind == ImlNodeKind.TextArea)
                        node.Lines = StyleValues.TryParseNumber(el.GetString("lines"), out var lines)
                            ? Mathf.Max(1, Mathf.RoundToInt(lines)) : 3;
                    break;

                case ImlNodeKind.Separator:
                    if (!node.Style.Setters.ContainsKey("marginTop"))
                    {
                        node.Style.Setters["marginTop"] = "2";
                        node.Style.Setters["marginBottom"] = "2";
                    }
                    break;

                case ImlNodeKind.Spacer:
                    if (!node.Style.Setters.ContainsKey("width") &&
                        !node.Style.Setters.ContainsKey("height"))
                        node.Style.Setters["height"] = "10";
                    break;

                case ImlNodeKind.Icon:
                    node.IconType = NormalizeIconType(ResolveAttributeValue(el, "type"));
                    break;

                case ImlNodeKind.ArrowButton:
                    node.Direction = (ResolveAttributeValue(el, "direction") ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(node.Direction)) node.Direction = "right";
                    break;

                case ImlNodeKind.ScrollView:
                {
                    var key = ResolveAttributeValue(el, "scrollPosition");
                    if (string.IsNullOrEmpty(key)) key = "auto:" + node.Path;
                    node.ScrollKey = key;
                    node.ScrollPos = GetScrollPos(key);
                    break;
                }

                case ImlNodeKind.CustomCanvas:
                    node.OnDraw = el.GetString("on-draw");
                    if (!node.Style.Setters.ContainsKey("width")) node.Style.Setters["width"] = "100";
                    if (!node.Style.Setters.ContainsKey("height")) node.Style.Setters["height"] = "100";
                    break;
            }
        }

        public static string NormalizeIconType(string typeAttr)
        {
            return (typeAttr ?? "").Trim().ToLowerInvariant() switch
            {
                "success" => "success",
                "warning" => "warning",
                "error" => "error",
                "stop" => "stop",
                _ => "information",
            };
        }

        private void BuildReference(ImlElement el, ImlNode owner, List<ImlNode> dest)
        {
            var path = el.GetString("path");
            if (string.IsNullOrEmpty(path)) path = el.GetString("src");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var refPath = ResolveReferencePath(path);
                if (!File.Exists(refPath))
                {
                    Debug.LogWarning($"[Iris.Iml] Reference file not found: {refPath}");
                    return;
                }
                if (!_referenceCache.TryGetValue(refPath, out var refDoc))
                {
                    refDoc = Parser.Parse(refPath);
                    _referenceCache[refPath] = refDoc;
                    ProcessResources(refDoc);
                    Styles.FinalizeRegistration();
                }
                if (refDoc?.Root != null)
                    BuildChildrenInto(refDoc.Root, owner, dest);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iris.Iml] Failed to build reference: {path} - {ex.Message}");
            }
        }

        private void BuildForEach(ImlElement el, ImlNode owner, List<ImlNode> dest)
        {
            var itemsBinding = el.GetExpression("items");
            var templateName = el.GetString("template");
            if (string.IsNullOrEmpty(itemsBinding) || string.IsNullOrEmpty(templateName)) return;

            object itemsObj;
            try { itemsObj = Evaluator.Evaluate(itemsBinding); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] ForEach items '{itemsBinding}' failed: {ex.Message}");
                return;
            }
            if (itemsObj is not IEnumerable items || itemsObj is string) return;

            var templateElement = FindTemplate(templateName);
            if (templateElement == null)
            {
                Debug.LogWarning($"[Iris.Iml] ForEach template not found: {templateName}");
                return;
            }

            foreach (var item in items)
            {
                Evaluator.SetVariable("item", item);
                foreach (var child in templateElement.Children)
                    if (child is ImlElement childEl)
                        BuildElement(childEl, owner, dest);
            }
            Evaluator.SetVariable("item", null);
        }

        private void BuildSelector(ImlElement el, ImlNode owner, List<ImlNode> dest)
        {
            var itemsStr = el.GetExpression("items");
            if (string.IsNullOrEmpty(itemsStr)) return;

            object itemsObj;
            try { itemsObj = Evaluator.Evaluate(itemsStr); }
            catch { return; }
            if (itemsObj is not IList items) return;

            string currentStr = "";
            var valueBinding = el.GetExpression("value");
            if (!string.IsNullOrEmpty(valueBinding))
            {
                try { currentStr = Evaluator.Evaluate(valueBinding)?.ToString() ?? ""; }
                catch { currentStr = ""; }
            }

            // The selector itself becomes a row box; options are button-like nodes.
            var row = CreateNode(el, owner);
            row.Kind = ImlNodeKind.Box;
            if (!row.Style.Setters.ContainsKey("direction")) row.Style.Setters["direction"] = "row";
            if (!row.Style.Setters.ContainsKey("gap")) row.Style.Setters["gap"] = "4";
            row.ValueBinding = valueBinding;
            row.OnChange = ResolveAttributeValue(el, "on-changed");
            FillNodeData(row, el);
            dest.Add(row);

            foreach (var item in items)
            {
                string key = "", display = "";
                if (item is string s) { key = s; display = s; }
                else if (item != null)
                {
                    var t = item.GetType();
                    var keyProp = t.GetProperty("key");
                    var displayProp = t.GetProperty("displayName");
                    key = keyProp?.GetValue(item)?.ToString() ?? item.ToString();
                    display = displayProp?.GetValue(item)?.ToString() ?? item.ToString();
                }

                var option = new ImlNode
                {
                    Kind = ImlNodeKind.SelectorOption,
                    Element = el,           // events come from the Selector element
                    Tag = "Button",
                    Parent = row,
                    Text = display,
                    OptionKey = key,
                    OptionDisplay = display,
                    OptionSelected = key == currentStr,
                    Style = ResolveSelectorOptionStyle(row.Style, key == currentStr),
                };
                option.Checked = key == currentStr;
                option.BuildState = key == currentStr ? ImlStateFlags.Checked : ImlStateFlags.None;
                option.ValueBinding = valueBinding;
                option.OnChange = row.OnChange;
                row.Children.Add(option);
            }
        }

        public ImlStyle ResolveSelectorOptionStyle(ImlStyle elementStyle, bool selected)
        {
            var s = new ImlStyle();
            if (elementStyle?.Setters != null)
                foreach (var kv in elementStyle.Setters)
                    s.Setters[kv.Key] = kv.Value;

            var selectedBg = StyleValues.GetStr(s, "selectedBg", "#D973A5");
            var selectedColor = StyleValues.GetStr(s, "selectedColor", "#FFFFFF");
            var unselectedBg = StyleValues.GetStr(s, "background", "#313338");
            var unselectedColor = StyleValues.GetStr(s, "color", "#E9ECEF");
            var radius = StyleValues.GetStr(s, "radius", "8");

            s.Setters["background"] = selected ? selectedBg : unselectedBg;
            s.Setters["color"] = selected ? selectedColor : unselectedColor;
            s.Setters["radius"] = radius;
            return s;
        }
    }
}
