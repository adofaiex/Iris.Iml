using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Iris.Iml
{
    public class ExpressionEvaluator
    {
        private static readonly HashSet<string> AllowedOperators = new()
        {
            "+", "-", "*", "/", "%", "==", "!=", ">", "<", ">=", "<=", "&&", "||", "!", "?"
        };

        private static readonly Regex IdentifierPattern = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex NumberPattern = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);
        private static readonly Regex PropertyAccessPattern = new(@"^([a-zA-Z_][a-zA-Z0-9_]*)(\.[a-zA-Z_][a-zA-Z0-9_]*|\[[^\]]+\])*$", RegexOptions.Compiled);
        private static readonly Regex FunctionCallPattern = new(@"^([a-zA-Z_][a-zA-Z0-9_]*)\s*\((.*)\)$", RegexOptions.Compiled);
        private static readonly Regex TernaryPattern = new(@"^(.+?)\s*\?\s*(.+?)\s*:\s*(.+)$", RegexOptions.Compiled);

        // Cached regexes for CheckSecurity - previously created per-call with RegexOptions.Compiled,
        // causing expensive dynamic assembly generation on Mono every frame
        private static readonly Regex SecurityMethodCallPattern = new(@"([a-zA-Z_][a-zA-Z0-9_]*)\s*\(", RegexOptions.Compiled);
        private static readonly Regex SecurityNewPattern = new(@"\bnew\s+", RegexOptions.Compiled);
        private static readonly Regex SecurityTypeInspectionPattern = new(@"\b(typeof|GetType)\b", RegexOptions.Compiled);
        private static readonly Regex SecurityAssignmentPattern = new(@"(?<![=!<>])=(?!=)", RegexOptions.Compiled);

        private readonly BindingContext _context;
        private readonly Dictionary<string, object> _localVariables = new();
        private readonly Dictionary<string, Func<object[], object>> _allowedFunctions = new();

        public ExpressionEvaluator(BindingContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void RegisterFunction(string name, Func<object[], object> func)
        {
            _allowedFunctions[name] = func;
        }

        public void SetVariable(string name, object value)
        {
            _localVariables[name] = value;
            _context?.SetValue(name, value);
        }

        public void ClearVariables()
        {
            _localVariables.Clear();
        }

        public object Evaluate(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return null;

            expression = expression.Trim();

            var sw = System.Diagnostics.Stopwatch.StartNew();

            CheckSecurity(expression);

            try
            {
                var result = ParseAndEvaluate(expression);
                if (sw.ElapsedMilliseconds > 5)
                    Debug.Log($"[Iris.Iml] Slow eval ({sw.ElapsedMilliseconds}ms): {expression}");
                return result;
            }
            catch (Exception ex)
            {
                throw new ExpressionEvaluationException($"Failed to evaluate expression: {expression}", ex);
            }
        }

        public bool EvaluateBoolean(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return false;

            try
            {
                var result = Evaluate(expression);
                if (result is bool b) return b;
                if (result is string s) return !string.IsNullOrEmpty(s);
                if (result is int i) return i != 0;
                if (result is float f) return Math.Abs(f) > 0.0001f;
                if (result is double d) return Math.Abs(d) > 0.0001;
                return result != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iris.Iml] Boolean evaluation failed for '{expression}': {ex.Message}");
                return false;
            }
        }

        private void CheckSecurity(string expression)
        {
            foreach (Match match in SecurityMethodCallPattern.Matches(expression))
            {
                var funcName = match.Groups[1].Value;
                if (!_allowedFunctions.ContainsKey(funcName))
                    throw new SecurityException($"Method calls are not allowed: {funcName}");
            }

            if (SecurityNewPattern.IsMatch(expression))
                throw new SecurityException("Object instantiation is not allowed in expressions");

            if (SecurityTypeInspectionPattern.IsMatch(expression))
                throw new SecurityException("Type inspection is not allowed in expressions");

            if (expression.Contains("=>"))
                throw new SecurityException("Lambda expressions are not allowed in expressions");

            if (SecurityAssignmentPattern.IsMatch(expression))
                throw new SecurityException("Assignment operators are not allowed in expressions");
        }

        private object ParseAndEvaluate(string expression)
        {
            var ternaryMatch = TernaryPattern.Match(expression);
            if (ternaryMatch.Success)
            {
                var condition = EvaluateBoolean(ternaryMatch.Groups[1].Value);
                return condition
                    ? Evaluate(ternaryMatch.Groups[2].Value)
                    : Evaluate(ternaryMatch.Groups[3].Value);
            }

            if (expression.Contains("||"))
            {
                var parts = SplitByOperator(expression, "||");
                foreach (var part in parts)
                {
                    if (EvaluateBoolean(part))
                        return true;
                }
                return false;
            }

            if (expression.Contains("&&"))
            {
                var parts = SplitByOperator(expression, "&&");
                foreach (var part in parts)
                {
                    if (!EvaluateBoolean(part))
                        return false;
                }
                return true;
            }

            foreach (var op in new[] { "==", "!=", ">=", "<=", ">", "<" })
            {
                if (expression.Contains(op))
                {
                    var parts = expression.Split(new[] { op }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        var left = Evaluate(parts[0].Trim());
                        var right = Evaluate(parts[1].Trim());
                        return Compare(left, right, op);
                    }
                }
            }

            return EvaluateArithmetic(expression);
        }

        private object EvaluateArithmetic(string expression)
        {
            expression = expression.Trim();

            foreach (var op in new[] { "+", "-" })
            {
                var depth = 0;
                var lastOpIndex = -1;
                for (int i = expression.Length - 1; i >= 0; i--)
                {
                    var c = expression[i];
                    if (c == ')') depth++;
                    else if (c == '(') depth--;
                    else if (depth == 0 && expression[i] == op[0] && (i == 0 || expression[i - 1] != op[0]))
                    {
                        lastOpIndex = i;
                        break;
                    }
                }

                if (lastOpIndex > 0)
                {
                    var left = Evaluate(expression.Substring(0, lastOpIndex));
                    var right = Evaluate(expression.Substring(lastOpIndex + 1));
                    return ApplyArithmetic(left, right, op);
                }
            }

            foreach (var op in new[] { "*", "/" })
            {
                var depth = 0;
                for (int i = expression.Length - 1; i >= 0; i--)
                {
                    var c = expression[i];
                    if (c == ')') depth++;
                    else if (c == '(') depth--;
                    else if (depth == 0 && expression[i] == op[0])
                    {
                        var left = Evaluate(expression.Substring(0, i));
                        var right = Evaluate(expression.Substring(i + 1));
                        return ApplyArithmetic(left, right, op);
                    }
                }
            }

            if (expression.Contains("%"))
            {
                for (int i = expression.Length - 1; i >= 0; i--)
                {
                    if (expression[i] == '%')
                    {
                        var left = Evaluate(expression.Substring(0, i));
                        var right = Evaluate(expression.Substring(i + 1));
                        return ApplyArithmetic(left, right, "%");
                    }
                }
            }

            if (expression.StartsWith("(") && expression.EndsWith(")"))
            {
                return Evaluate(expression.Substring(1, expression.Length - 2));
            }

            if (expression.StartsWith("!"))
            {
                return !EvaluateBoolean(expression.Substring(1));
            }

            if (NumberPattern.IsMatch(expression))
            {
                if (expression.Contains("."))
                    return double.Parse(expression);
                return int.Parse(expression);
            }

            if ((expression.StartsWith("\"") && expression.EndsWith("\"")) ||
                (expression.StartsWith("'") && expression.EndsWith("'")))
            {
                return expression.Substring(1, expression.Length - 2);
            }

            if (expression == "true") return true;
            if (expression == "false") return false;
            if (expression == "null") return null;

            var funcMatch = FunctionCallPattern.Match(expression);
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                var argsStr = funcMatch.Groups[2].Value;
                if (_allowedFunctions.TryGetValue(funcName, out var func))
                {
                    var args = ParseArguments(argsStr);
                    return func(args);
                }
            }

            return ResolveValue(expression);
        }

        private object[] ParseArguments(string argsStr)
        {
            var args = new List<object>();
            var current = "";
            var depth = 0;
            bool inString = false;
            char quoteChar = '\0';

            foreach (var c in argsStr)
            {
                if (c == '"' || c == '\'')
                {
                    if (!inString)
                    {
                        inString = true;
                        quoteChar = c;
                        current += c;
                    }
                    else if (c == quoteChar)
                    {
                        inString = false;
                        quoteChar = '\0';
                        current += c;
                    }
                    else
                    {
                        current += c;
                    }
                }
                else if (c == '(')
                {
                    depth++;
                    current += c;
                }
                else if (c == ')')
                {
                    depth--;
                    current += c;
                }
                else if (c == ',' && depth == 0 && !inString)
                {
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        args.Add(Evaluate(current.Trim()));
                    }
                    current = "";
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                args.Add(Evaluate(current.Trim()));
            }

            return args.ToArray();
        }

        private object ResolveValue(string path)
        {
            path = path.Trim();

            if (_localVariables.TryGetValue(path, out var value))
                return value;

            var segments = ParsePropertyPath(path);
            if (segments == null)
                throw new ExpressionEvaluationException($"Invalid property path: {path}");

            object result = null;

            if (segments.Length > 0)
            {
                result = _context.GetValue(segments[0]);

                for (int i = 1; i < segments.Length; i++)
                {
                    if (result == null) return null;

                    var segment = segments[i];
                    if (segment.StartsWith("[") && segment.EndsWith("]"))
                    {
                        var indexStr = segment.Substring(1, segment.Length - 2).Trim('"', '\'');
                        result = AccessIndex(result, indexStr);
                    }
                    else
                    {
                        result = AccessProperty(result, segment);
                    }
                }
            }

            return result;
        }

        private string[] ParsePropertyPath(string path)
        {
            var segments = new List<string>();
            var current = "";
            var depth = 0;

            foreach (var c in path)
            {
                if (c == '.' && depth == 0)
                {
                    if (string.IsNullOrEmpty(current))
                        return null;
                    segments.Add(current);
                    current = "";
                }
                else if (c == '[')
                {
                    depth++;
                    current += c;
                }
                else if (c == ']')
                {
                    depth--;
                    current += c;
                }
                else
                {
                    current += c;
                }
            }

            if (!string.IsNullOrEmpty(current))
                segments.Add(current);

            return segments.ToArray();
        }

        private object AccessIndex(object target, string index)
        {
            if (target is Array arr)
            {
                if (int.TryParse(index, out var i))
                    return arr.GetValue(i);
            }
            else if (target is IList list)
            {
                if (int.TryParse(index, out var i))
                    return list[i];
            }
            else if (target is IDictionary dict)
            {
                return dict[index];
            }

            return AccessProperty(target, index.Trim('[', ']'));
        }

        private object AccessProperty(object target, string propertyName)
        {
            var type = target.GetType();

            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (prop != null)
                return prop.GetValue(target);

            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (field != null)
                return field.GetValue(target);

            prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (prop != null)
                return prop.GetValue(target);

            field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (field != null)
                return field.GetValue(target);

            throw new ExpressionEvaluationException($"Property '{propertyName}' not found on type '{type.Name}'");
        }

        private string[] SplitByOperator(string expression, string op)
        {
            var parts = new List<string>();
            var depth = 0;
            var lastIndex = 0;

            for (int i = 0; i < expression.Length; i++)
            {
                if (expression[i] == '(') depth++;
                else if (expression[i] == ')') depth--;
                else if (depth == 0 && expression.Substring(i).StartsWith(op))
                {
                    parts.Add(expression.Substring(lastIndex, i - lastIndex));
                    lastIndex = i + op.Length;
                    i += op.Length - 1;
                }
            }

            parts.Add(expression.Substring(lastIndex));
            return parts.ToArray();
        }

        private object ApplyArithmetic(object left, object right, string op)
        {
            double l, r;

            if (left == null && right == null)
            {
                l = r = 0;
            }
            else if (left == null)
            {
                l = 0;
                r = Convert.ToDouble(right);
            }
            else if (right == null)
            {
                l = Convert.ToDouble(left);
                r = 0;
            }
            else
            {
                l = Convert.ToDouble(left);
                r = Convert.ToDouble(right);
            }

            return op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => r != 0 ? l / r : 0,
                "%" => r != 0 ? l % r : 0,
                _ => throw new ArgumentException($"Unknown operator: {op}")
            };
        }

        private bool Compare(object left, object right, string op)
        {
            if (left == null && right == null)
            {
                return op == "==" || op == ">=" || op == "<=";
            }
            if (left == null || right == null)
            {
                // null is only equal to null; null != anything else
                return op == "!=";
            }

            if (left is string ls && right is string rs)
            {
                return op switch
                {
                    "==" => ls == rs,
                    "!=" => ls != rs,
                    ">" => string.Compare(ls, rs, StringComparison.Ordinal) > 0,
                    "<" => string.Compare(ls, rs, StringComparison.Ordinal) < 0,
                    ">=" => string.Compare(ls, rs, StringComparison.Ordinal) >= 0,
                    "<=" => string.Compare(ls, rs, StringComparison.Ordinal) <= 0,
                    _ => false
                };
            }

            var l = Convert.ToDouble(left);
            var r = Convert.ToDouble(right);

            return op switch
            {
                "==" => Math.Abs(l - r) < 0.0001,
                "!=" => Math.Abs(l - r) >= 0.0001,
                ">" => l > r,
                "<" => l < r,
                ">=" => l >= r,
                "<=" => l <= r,
                _ => false
            };
        }

        private bool IsNumeric(object obj)
        {
            return obj is int || obj is float || obj is double || obj is long || obj is short || obj is byte;
        }
    }

    public class ExpressionEvaluationException : Exception
    {
        public ExpressionEvaluationException(string message) : base(message) { }
        public ExpressionEvaluationException(string message, Exception inner) : base(message, inner) { }
    }

    public class SecurityException : Exception
    {
        public SecurityException(string message) : base($"[SECURITY] {message}") { }
    }
}
