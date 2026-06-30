using System;
using UnityEngine;

namespace Iris.Iml
{
    public interface IImlRenderer
    {
        string CurrentFilePath { get; }
        Action<string> LogDelegate { get; set; }

        void SetDataContext(object data);
        void RegisterHandler(string name, Action handler);
        void RegisterHandler(string name, Action<object> handler);
        void RegisterFunction(string name, Func<object[], object> func);
        void RegisterDrawHandler(string name, Action<Rect, RendererInternal.DrawArgs> handler);
        void SetHotReload(bool enabled);
        void SetLayout(IIrrLayout layout);
        void LoadFile(string filePath);
        void LoadContent(string imlContent, string basePath = "");
        void Render(string filePath);
        void OnGUI();
    }
}
