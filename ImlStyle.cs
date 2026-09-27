using System.Collections.Generic;

namespace Iris.Iml
{
    /// <summary>
    /// A style definition. Named styles (<c>style="primary"</c>), selector styles
    /// (<c>on=".primary"</c>) and resolved/computed styles all share this shape:
    /// a flat property dictionary. Computed styles additionally carry
    /// pseudo-class state applied for the current frame.
    /// </summary>
    public class ImlStyle
    {
        public string Name { get; set; }
        public string Extends { get; set; }
        public StyleSelector Selector { get; set; }

        /// <summary>Registration order; used to break specificity ties deterministically.</summary>
        public int Order { get; set; }

        public Dictionary<string, string> Setters { get; set; } = new();
    }

    /// <summary>
    /// Interaction state of an element for one resolve pass. Selector pseudo
    /// classes (<c>Button.primary:hover</c>) only match when their required
    /// flags are all present in the state.
    /// </summary>
    [System.Flags]
    public enum ImlStateFlags
    {
        None = 0,
        Hover = 1,
        Press = 2,
        Disabled = 4,
        Checked = 8,
    }

    namespace RendererInternal
    {
        public class DrawArgs
        {
            public object Context { get; set; }
        }
    }
}
