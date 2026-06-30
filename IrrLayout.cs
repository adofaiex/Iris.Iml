using UnityEngine;

namespace Iris.Iml
{
    public enum IrrContStyle { None, Padding, Background }
    public enum IrrButStyle { Element, Primary }
    public enum IrrTextStyle { Normal, Subtitle, Title, Secondary }
    public enum IrrIconStyle { Information, Success, Warning, Error, Stop }

    public interface IIrrLayout
    {
        void BeginHorizontal(IrrContStyle style, GUILayoutOption[] options);
        void BeginVertical(IrrContStyle style, GUILayoutOption[] options);
        void End();
        bool Button(string text, IrrButStyle style);
        void Text(string text, IrrTextStyle style);
        bool? Switch(bool on);
        bool? Checkbox(bool on);
        void Separator();
        void Space(double size);
        string? TextField(string content);
        bool Icon(IrrIconStyle style);
    }
}
