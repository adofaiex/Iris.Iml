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
        private static readonly Regex ExpressionPattern = new(@"\{(.*?)\}", RegexOptions.Compiled);
        private static readonly Regex EscapePattern = new(@"\\([\\{}])", RegexOptions.Compiled);

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
                    children.Add(ParseElement());
                }
                else
                {
                    // Text content
                    children.Add(ParseTextContent());
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

                // Check for expressions
                var match = ExpressionPattern.Match(rawValue);
                if (match.Success && match.Index == 0 && match.Length == rawValue.Length)
                {
                    var expr = match.Groups[1].Value;
                    return new AttributeValue { Type = AttributeType.Expression, Expression = expr };
                }

                // Check for template string with interpolation
                if (rawValue.Contains("{") && rawValue.Contains("}"))
                {
                    return ParseTemplateString(rawValue);
                }

                return new AttributeValue { Type = AttributeType.String, StringValue = rawValue };
            }
            else if (Peek() == '{')
            {
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

        private AttributeValue ParseTemplateString(string rawValue)
        {
            var parts = new List<TemplatePart>();
            var lastIndex = 0;

            foreach (Match match in ExpressionPattern.Matches(rawValue))
            {
                if (match.Index > lastIndex)
                {
                    var textBefore = rawValue.Substring(lastIndex, match.Index - lastIndex);
                    textBefore = EscapePattern.Replace(textBefore, m => m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(textBefore))
                        parts.Add(new TemplatePart { IsExpression = false, Value = textBefore });
                }

                parts.Add(new TemplatePart { IsExpression = true, Value = match.Groups[1].Value });
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < rawValue.Length)
            {
                var textAfter = rawValue.Substring(lastIndex);
                textAfter = EscapePattern.Replace(textAfter, m => m.Groups[1].Value);
                if (!string.IsNullOrEmpty(textAfter))
                    parts.Add(new TemplatePart { IsExpression = false, Value = textAfter });
            }

            return new AttributeValue { Type = AttributeType.Template, Parts = parts.ToArray() };
        }

        private string ParseTextContent()
        {
            var start = _position;
            while (Peek() != '<' && _position < _content.Length)
                Advance(1);
            return _content.Substring(start, _position - start).Trim();
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
    }

    public enum AttributeType
    {
        String,
        Expression,
        Boolean,
        Template
    }

    public class TemplatePart
    {
        public bool IsExpression { get; set; }
        public string Value { get; set; }
    }
}
