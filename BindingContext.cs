using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iris.Iml
{
    /// <summary>
    /// 数据上下文接口
    /// </summary>
    public interface IBindingContext
    {
        object GetValue(string propertyPath);
        void SetValue(string propertyPath, object value);
        event Action<string, object, object> PropertyChanged;
    }

    /// <summary>
    /// 默认数据上下文实现
    /// </summary>
    public class BindingContext : IBindingContext
    {
        private readonly Dictionary<string, PropertyAccessor> _accessors = new();
        private readonly object _data;

        public event Action<string, object, object> PropertyChanged;

        public BindingContext(object data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            InitializeAccessors();
        }

        private void InitializeAccessors()
        {
            var type = _data.GetType();
            var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            foreach (var prop in properties)
            {
                if (prop.CanRead)
                {
                    _accessors[prop.Name] = new PropertyAccessor
                    {
                        Property = prop,
                        Getter = prop.GetValue,
                        Setter = prop.CanWrite ? prop.SetValue : null
                    };
                }
            }

            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                _accessors[field.Name] = new PropertyAccessor
                {
                    Field = field,
                    Getter = field.GetValue,
                    Setter = (obj, val) => field.SetValue(obj, val)
                };
            }
        }

        public object GetValue(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
                return _data;

            var segments = propertyPath.Split('.');
            object current = _data;

            foreach (var segment in segments)
            {
                if (current == null) return null;

                // IDictionary<string, object> support: many call sites build the data
                // context dynamically (e.g. ImlWindow with variable button counts) and
                // Dictionary's keys are not exposed as CLR properties, so reflection-based
                // access alone cannot reach them.
                if (current is IDictionary<string, object> dict)
                {
                    if (dict.TryGetValue(segment, out var v)) current = v;
                    else return null;
                    continue;
                }

                if (_accessors.TryGetValue(segment, out var accessor))
                {
                    current = accessor.Getter(current);
                }
                else
                {
                    // Try dynamic access
                    var type = current.GetType();
                    var prop = type.GetProperty(segment);
                    if (prop != null)
                        current = prop.GetValue(current);
                    else
                    {
                        var field = type.GetField(segment);
                        if (field != null)
                            current = field.GetValue(current);
                        else
                            return null;
                    }
                }
            }

            return current;
        }

        public void SetValue(string propertyPath, object value)
        {
            if (string.IsNullOrEmpty(propertyPath))
                return;

            var segments = propertyPath.Split('.');
            if (segments.Length == 1)
            {
                if (_accessors.TryGetValue(segments[0], out var accessor) && accessor.Setter != null)
                {
                    var oldValue = accessor.Getter(_data);
                    accessor.Setter(_data, value);
                    PropertyChanged?.Invoke(propertyPath, oldValue, value);
                    return;
                }
            }

            // Navigate to parent
            object current = _data;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (current == null) return;

                // IDictionary<string, object> support (mirrors GetValue's path
                // so the two stay symmetric for Dictionary-based contexts).
                if (current is IDictionary<string, object> dict)
                {
                    if (dict.TryGetValue(segments[i], out var dv)) current = dv;
                    else return;
                    continue;
                }

                if (_accessors.TryGetValue(segments[i], out var accessor))
                {
                    current = accessor.Getter(current);
                }
                else
                {
                    // BUG FIX: GetValue's intermediate-navigation else branch
                    // falls back from GetProperty to GetField when a segment
                    // is a public field (e.g. Settings.judgeText). SetValue's
                    // branch did NOT — it `return`ed silently, so any binding
                    // path containing a public field in the middle silently
                    // no-op'd. Symptom: typing into a TextField bound to
                    // "settings.judgeText.tooEarly" appeared to swallow every
                    // keystroke because the data context was never updated,
                    // and the TextField's `content` parameter was re-read as
                    // the stale value on the next OnGUI call.
                    var type = current.GetType();
                    var prop = type.GetProperty(segments[i]);
                    if (prop != null)
                        current = prop.GetValue(current);
                    else
                    {
                        var field = type.GetField(segments[i]);
                        if (field != null)
                            current = field.GetValue(current);
                        else
                            return;
                    }
                }
            }

            // Set final property
            var finalSegment = segments[segments.Length - 1];
            var targetType = current.GetType();

            if (_accessors.TryGetValue(finalSegment, out var finalAccessor) && finalAccessor.Setter != null)
            {
                var oldValue = finalAccessor.Getter(current);
                finalAccessor.Setter(current, value);
                PropertyChanged?.Invoke(propertyPath, oldValue, value);
            }
            else
            {
                var prop = targetType.GetProperty(finalSegment);
                if (prop != null && prop.CanWrite)
                {
                    var oldValue = prop.GetValue(current);
                    prop.SetValue(current, value);
                    PropertyChanged?.Invoke(propertyPath, oldValue, value);
                }
            }
        }

        private class PropertyAccessor
        {
            public System.Reflection.PropertyInfo Property { get; set; }
            public System.Reflection.FieldInfo Field { get; set; }
            public Func<object, object> Getter { get; set; }
            public Action<object, object> Setter { get; set; }
        }
    }

    /// <summary>
    /// 简易RelayCommand实现
    /// </summary>
    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public event EventHandler CanExecuteChanged;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        {
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
