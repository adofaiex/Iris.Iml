using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// IML文件解析器，负责解析 .iml 文件并生成 AST
    /// </summary>
    public class ImlParser
    {
        private string _filePath;
        private string _content;
        private int _position;

        public ImlDocument Parse(string filePath)
        {
            _filePath = filePath;
            _content = File.ReadAllText(filePath);
            _position = 0;

            var document = new ImlDocument { FilePath = filePath };
            ExpectRoot(document);
            return document;
        }

        public ImlDocument ParseContent(string content, string basePath = "")
        {
            _content = content;
            _position = 0;
            _filePath = basePath;

            var document = new ImlDocument { FilePath = basePath };
            ExpectRoot(document);
            return document;
        }

        private void ExpectRoot(ImlDocument document)
        {
            document.Root = ParseElement();
            if (document.Root.TagName != "Iris")
                throw new ImlParseException($"Root element must be <Iris>, got <{document.Root.TagName}>");
            SkipWhitespaceAndComments();
            if (_position < _content.Length)
                throw new ImlParseException($"Unexpected content after root element at position {_position}");
        }

        private ImlElement ParseElement()
        {
            SkipWhitespaceAndComments();
            if (Peek() != '<')
                throw new ImlParseException($"Expected '<' at position {_position}");

            var tagStart = _position;
            Advance(1); // skip '<'
            var tagName = ParseTagName();

            var element = new ImlElement { TagName = tagName, SourceLine = GetLineNumber(tagStart) };

            // Parse attributes
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '\0' || _position >= _content.Length)
                    break;
                if (Peek() == '/' && Peek(1) == '>')
                {
                    Advance(2);
                    return element;
                }
                if (Peek() == '>')
                {
                    Advance(1);
                    break;
                }

                var attrName = ParseAttributeName();
                SkipWhitespace();
                if (Peek() == '=')
                {
                    Advance(1);
                    var attrValue = ParseAttributeValue();
                    element.Attributes[attrName] = attrValue;
                }
                else
                {
                    // Boolean attribute (shorthand like "disabled")
                    element.Attributes[attrName] = new AttributeValue { Type = AttributeType.Boolean, BoolValue = true };
                }
            }

            // Parse child elements
            var children = new List<object>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_position >= _content.Length || Match("</" + tagName + ">"))
                    break;

                if (Peek() == '<')
                {
                    // Fragment: <>...</>
                    if (Peek(1) == '>')
                    {
                        Advance(2); // skip <>
                        var fragChildren = new List<object>();
                        SkipWhitespaceAndComments();
                        while (_position < _content.Length)
                        {
                            if (Match("</>")) break;
                            if (Peek() == '<')
                                fragChildren.Add(ParseElement());
                            else
                                fragChildren.Add(ParseTextOrExpression());
                            SkipWhitespaceAndComments();
                        }
                        foreach (var c in fragChildren)
                            if (c != null) children.Add(c);
                        continue;
                    }

                    children.Add(ParseElement());
                }
                else
                {
                    var child = ParseTextOrExpression();
                    if (child != null) children.Add(child);
                }
            }

            element.Children = children.ToArray();
            return element;
        }

        private string ParseTagName()
        {
            var start = _position;
            while (_position < _content.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '-' || Peek() == '_'))
                Advance(1);
            return _content.Substring(start, _position - start);
        }

        private string ParseAttributeName()
        {
            var start = _position;
            while (_position < _content.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '-' || Peek() == '_'))
                Advance(1);
            return _content.Substring(start, _position - start);
        }

        private AttributeValue ParseAttributeValue()
        {
            char quote = Peek();
            if (quote == '"' || quote == '\'')
            {
                Advance(1);
                var start = _position;
                while (Peek() != quote && _position < _content.Length)
                {
                    if (Peek() == '\\')
                        Advance(1); // Skip escape
                    Advance(1);
                }
                var rawValue = _content.Substring(start, _position - start);
                Advance(1); // Closing quote

                // Process escapes
                rawValue = rawValue.Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\");

                // Quoted values are always plain strings.
                // Use unquoted {expr} for expressions and {"text"+expr} for templates.
                return new AttributeValue { Type = AttributeType.String, StringValue = rawValue };
            }
            else if (Peek() == '{')
            {
                if (Peek(1) == '{')
                {
                    // Style object: {{ key: value, key2: "value2", key3: 42 }}
                    Advance(2); // skip {{
                    return new AttributeValue
                    {
                        Type = AttributeType.StyleObject,
                        StyleEntries = ParseStyleObjectValue()
                    };
                }

                // Expression
                Advance(1);
                var start = _position;
                int braceDepth = 1;
                while (braceDepth > 0 && _position < _content.Length)
                {
                    if (Peek() == '{') braceDepth++;
                    else if (Peek() == '}') braceDepth--;
                    Advance(1);
                }
                var expr = _content.Substring(start, _position - start - 1);
                return new AttributeValue { Type = AttributeType.Expression, Expression = expr };
            }
            else
            {
                // Boolean shorthand (just attribute name without value)
                return new AttributeValue { Type = AttributeType.Boolean, BoolValue = true };
            }
        }

        /// <summary>
        /// Parse the content inside <c>{{ }}</c> as a style dictionary.
        /// Accepts: <c>key: value</c>, <c>key: "string"</c>, <c>key: 123</c>.
        /// Commas between entries are optional.
        /// </summary>
        private StyleEntry[] ParseStyleObjectValue()
        {
            var entries = new List<StyleEntry>();

            while (_position < _content.Length)
            {
                SkipWhitespace();
                if (Peek() == '}' && Peek(1) == '}') { Advance(2); break; }
                if (Peek() == ',' || Peek() == ';') { Advance(1); continue; }

                var key = ParseAttributeName();
                SkipWhitespace();
                if (Peek() != ':') throw new ImlParseException($"Expected ':' after style property '{key}'");
                Advance(1); // skip ':'
                SkipWhitespace();

                string val;
                if (Peek() == '"' || Peek() == '\'')
                {
                    var quote = Peek(); Advance(1);
                    var vStart = _position;
                    while (Peek() != quote && _position < _content.Length) Advance(1);
                    val = _content.Substring(vStart, _position - vStart);
                    if (_position < _content.Length) Advance(1);
                }
                else if (Peek() == '{')
                {
                    // Nested object (e.g. for text-shadow or future features)
                    Advance(1); int d = 1;
                    var vStart = _position;
                    while (d > 0 && _position < _content.Length)
                    {
                        if (Peek() == '{') d++; else if (Peek() == '}') d--;
                        Advance(1);
                    }
                    val = "{" + _content.Substring(vStart, _position - vStart - 1) + "}";
                }
                else
                {
                    var vStart = _position;
                    while (_position < _content.Length && !char.IsWhiteSpace(Peek()) && Peek() != ',' && Peek() != '}' && Peek() != ';')
                        Advance(1);
                    val = _content.Substring(vStart, _position - vStart);
                }

                if (!string.IsNullOrEmpty(key))
                    entries.Add(new StyleEntry { Property = key, Value = val ?? "" });
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Parse text content or JSX expression blocks <c>{expr}</c>, <c>{/* comment */}</c>,
        /// or <c>{cond &amp;&amp; &lt;Element /&gt;}</c> (conditional element).
        /// Returns <see cref="string"/>, <see cref="ExpressionValue"/>, or <see cref="ImlElement"/> (If).
        /// </summary>
        private object ParseTextOrExpression()
        {
            var start = _position;

            while (_position < _content.Length)
            {
                if (Peek() == '<') break;

                if (Peek() == '{')
                {
                    // Return accumulated text before this brace (if any)
                    var text = _content.Substring(start, _position - start).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _position = start;
                        goto returnText;
                    }

                    return ParseInlineExpressionBlock();
                }

                Advance(1);
            }

        returnText:
            var result = _content.Substring(start, _position - start).Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        /// <summary>
        /// Parse a JSX expression block starting after the opening <c>{</c>.
        /// Handles: <c>{expr}</c>, <c>{/* comment */}</c>, <c>{cond &amp;&amp; &lt;Element /&gt;}</c>.
        /// Uses purely peek/advance — no regex, no sub-parser.
        /// </summary>
        private object ParseInlineExpressionBlock()
        {
            Advance(1); // skip {
            var start = _position;
            int depth = 1;
            int exprEnd = -1;

            while (depth > 0 && _position < _content.Length)
            {
                var c = Peek();

                if (c == '{')
                {
                    depth++;
                    Advance(1);
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { exprEnd = _position; Advance(1); break; }
                    Advance(1);
                }
                else if (c == '<' && depth == 1)
                {
                    // Check if the accumulated text before '<' ends with "&&" or is empty
                    var before = _content.Substring(start, _position - start).TrimEnd();
                    if (string.IsNullOrEmpty(before) || before.EndsWith("&&"))
                    {
                        // Before: the condition (or null if bare element)
                        string condition = null;
                        if (before.EndsWith("&&"))
                            condition = before.Substring(0, before.Length - 2).Trim();

                        // Parse the element inline using the current parser state
                        var element = ParseElement();

                        if (condition != null)
                        {
                            var ifElement = new ImlElement
                            {
                                TagName = "If",
                                SourceLine = GetLineNumber(start)
                            };
                            ifElement.Attributes["condition"] = new AttributeValue
                            {
                                Type = AttributeType.Expression,
                                Expression = condition
                            };
                            ifElement.Children = element != null ? new object[] { element } : Array.Empty<object>();
                            // Consume trailing whitespace and closing }
                            SkipWhitespace();
                            if (Peek() == '}') Advance(1);
                            return ifElement;
                        }

                        // Bare element in braces: {<Element />} — return element directly
                        SkipWhitespace();
                        if (Peek() == '}') Advance(1);
                        return element;
                    }

                    // Not a conditional — treat '<' as part of expression (e.g. comparison)
                    Advance(1);
                }
                else if (c == '/' && depth == 1 && Peek(1) == '*')
                {
                    // JSX comment {/* ... */}
                    // Skip ahead past */
                    Advance(2);
                    while (_position < _content.Length - 1)
                    {
                        if (Peek() == '*' && Peek(1) == '/') { Advance(2); break; }
                        Advance(1);
                    }
                    // Reset start so everything before the comment is discarded
                    // (or if nothing remains, return null)
                    start = _position;
                }
                else
                {
                    Advance(1);
                }
            }

            // Extract the expression string (if any)
            if (exprEnd < 0) return null;
            var expr = _content.Substring(start, exprEnd - start).Trim();
            if (string.IsNullOrWhiteSpace(expr)) return null;

            return new ExpressionValue { Expression = expr };
        }

        private void SkipWhitespace()
        {
            while (_position < _content.Length && char.IsWhiteSpace(Peek()))
                Advance(1);
        }

        private void SkipWhitespaceAndComments()
        {
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '<' && Peek(1) == '!' && Peek(2) == '-' && Peek(3) == '-')
                {
                    Advance(4); // <!--
                    var safetyLimit = _content.Length;
                    while (!(_position >= _content.Length - 2 || (Peek() == '-' && Peek(1) == '-' && Peek(2) == '>')))
                    {
                        Advance(1);
                        if (--safetyLimit <= 0)
                            throw new ImlParseException($"Unterminated comment at position {_position}");
                    }
                    if (_position < _content.Length - 2)
                        Advance(3); // -->
                }
                else if (Peek() == '<' && Peek(1) == '!')
                {
                    Advance(2);
                    var safetyLimit = _content.Length;
                    while (Peek() != '>' && _position < _content.Length)
                    {
                        Advance(1);
                        if (--safetyLimit <= 0)
                            throw new ImlParseException($"Unterminated declaration at position {_position}");
                    }
                    if (Peek() == '>')
                        Advance(1);
                }
                else break;
            }
        }

        private bool Match(string s)
        {
            if (_position + s.Length > _content.Length)
                return false;
            if (_content.Substring(_position, s.Length) == s)
            {
                Advance(s.Length);
                return true;
            }
            return false;
        }

        private char Peek(int offset = 0)
        {
            var idx = _position + offset;
            return idx < _content.Length ? _content[idx] : '\0';
        }

        private void Advance(int count = 1)
        {
            _position = Math.Min(_position + count, _content.Length);
        }

        private int GetLineNumber(int position)
        {
            return _content.Substring(0, position).Count(c => c == '\n') + 1;
        }

        public string GetBasePath() => Path.GetDirectoryName(_filePath) ?? "";

        public string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path.StartsWith("@/"))
                return Path.Combine(Path.GetDirectoryName(_filePath) ?? "", "..", "..", path.Substring(2));
            if (path.StartsWith("@"))
                return Path.Combine(Path.GetDirectoryName(_filePath) ?? "", path.Substring(1));
            return path;
        }
    }

    public class ImlParseException : Exception
    {
        public ImlParseException(string message) : base(message) { }
    }

    public class ImlDocument
    {
        public string FilePath { get; set; }
        public ImlElement Root { get; set; }
        public Dictionary<string, ImlElement> Resources { get; } = new();
        public List<ImlElement> References { get; } = new();
    }

    public class ImlElement
    {
        public string TagName { get; set; }
        public int SourceLine { get; set; }
        public Dictionary<string, AttributeValue> Attributes { get; } = new();
        public object[] Children { get; set; } = Array.Empty<object>();

        public string GetString(string attrName) => Attributes.TryGetValue(attrName, out var v) ? v.StringValue ?? "" : "";
        public string GetExpression(string attrName) => Attributes.TryGetValue(attrName, out var v) ? v.Expression ?? "" : "";
        public bool HasAttribute(string attrName) => Attributes.ContainsKey(attrName);
    }

    public class AttributeValue
    {
        public AttributeType Type { get; set; }
        public string StringValue { get; set; }
        public string Expression { get; set; }
        public bool BoolValue { get; set; }
        public TemplatePart[] Parts { get; set; }
        public StyleEntry[] StyleEntries { get; set; }
    }

    public enum AttributeType
    {
        String,
        Expression,
        Boolean,
        Template,
        StyleObject
    }

    public class StyleEntry
    {
        public string Property { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// A CSS-like selector parsed from <c>on="Button.primary#id"</c>.
    /// All non-null fields must match for the style to apply.
    /// Specificity: Tag=1, Class=2, Id=3 (summed).
    /// </summary>
    public class StyleSelector
    {
        public string Tag { get; set; }
        public string Class { get; set; }
        public string Id { get; set; }

        public int Specificity =>
            (Tag  != null ? 1 : 0) +
            (Class != null ? 2 : 0) +
            (Id   != null ? 3 : 0);

        public bool Matches(string elementTag, string elementClass, string elementId)
        {
            if (Tag != null && !string.Equals(Tag, elementTag, StringComparison.OrdinalIgnoreCase))
                return false;
            if (Class != null && !string.Equals(Class, elementClass, StringComparison.OrdinalIgnoreCase))
                return false;
            if (Id != null && !string.Equals(Id, elementId, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        public static StyleSelector Parse(string on)
        {
            if (string.IsNullOrWhiteSpace(on) || on == "*")
                return new StyleSelector();

            var sel = new StyleSelector();

            // Parse "Button.primary#myId"
            var i = 0;
            var segStart = 0;
            while (i <= on.Length)
            {
                if (i == on.Length || on[i] == '.' || on[i] == '#')
                {
                    var seg = on.Substring(segStart, i - segStart);
                    if (!string.IsNullOrEmpty(seg))
                    {
                        // Determine what came before this segment
                        if (segStart == 0)
                        {
                            // First segment with no prefix: could be tag or class
                            if (on[0] == '.') sel.Class = seg;
                            else if (on[0] == '#') sel.Id = seg;
                            else sel.Tag = seg; // no prefix → tag
                        }
                        else if (on[segStart - 1] == '.') sel.Class = seg;
                        else if (on[segStart - 1] == '#') sel.Id = seg;
                    }
                    segStart = i + 1;
                }
                i++;
            }

            return sel;
        }
    }

    public class TemplatePart
    {
        public bool IsExpression { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Represents an inline expression in text content, e.g. <c>{settings.title}</c>
    /// or a JSX conditional <c>{cond && &lt;Element /&gt;}</c>.
    /// The renderer evaluates <see cref="Expression"/> at draw time and renders the result.
    /// </summary>
    public class ExpressionValue
    {
        public string Expression { get; set; }
    }
}
