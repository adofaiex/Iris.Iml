using System.Collections.Generic;
using UnityEngine;

namespace Iris.Iml
{
    public enum ImlNodeKind
    {
        Root,
        Box,          // <View> <HBox> <VBox> (+ <Selector> synthesized row)
        Text,
        Link,
        Image,
        Button,
        Switch,
        Checkbox,
        Slider,
        TextField,
        TextArea,
        Fill,
        Spacer,       // <Spacer> <Box>
        Separator,
        Icon,
        ArrowButton,
        ScrollView,
        SelectorOption,
        CustomCanvas,
    }

    /// <summary>
    /// A built (state-resolved) element node. The tree is produced by
    /// <see cref="ImlRuntime.BuildTree"/> each frame for IMGUI and on demand
    /// for UGUI; the layout engine writes <see cref="Rect"/> and friends.
    /// All rects are in "canvas space": origin at the root's top-left,
    /// y grows downward, scroll offsets already applied. Backends convert to
    /// their own draw space via a translation vector.
    /// </summary>
    public sealed class ImlNode
    {
        public ImlNodeKind Kind;
        public ImlElement Element;          // null for bare text/expression nodes
        public string Tag = "";
        public ImlNode Parent;
        public List<ImlNode> Children = new();
        public List<ImlNode> BgChildren = new();   // slot="background"
        public List<ImlNode> FgChildren = new();   // slot="foreground"

        /// <summary>Computed style at build time (no hover/press state).</summary>
        public ImlStyle Style;

        public bool Visible = true;

        /// <summary>State flags baked in at build time (checked/disabled);
        /// backends OR hover/press on top when drawing.</summary>
        public ImlStateFlags BuildState;

        // ── resolved attributes (evaluated at build time) ───────────────
        public string Text = "";
        public string Url;                 // Link
        public string Source;              // Image
        public string IconType;            // Icon (normalized)
        public string Direction;           // ArrowButton
        public string ScrollKey;
        public string OnDraw;              // CustomCanvas draw handler name
        public string ValueBinding;        // value={path} two-way binding expression
        public string OnChange;            // on-changed handler spec
        public string OnSubmit;            // on-text-submit handler spec
        public float Min;
        public float Max = 100f;
        public float Step;
        public bool ShowValue;
        public int Lines = 3;
        public float FloatValue;
        public bool Checked;

        // Selector options (Kind == SelectorOption)
        public string OptionKey;
        public string OptionDisplay;
        public bool OptionSelected;

        // ── layout outputs ──────────────────────────────────────────────
        public Rect Rect;          // canvas-space border box (scroll applied)
        public Rect Clip;          // canvas-space clip rect for hit testing
        public Vector2 Desired;    // measured size (border box, without margins)
        public Vector2 ContentSize;// ScrollView: content size
        public Rect ViewportInner; // ScrollView: scrollable area (canvas space)
        public float ScrollPos;
        public bool IsAbsolute;    // position:absolute — excluded from flex flow
        public bool Hovered;
        public bool Pressed;

        /// <summary>Backend payload (GameObject refs, cached sprites, ...).</summary>
        public object Payload;

        public bool Interactive =>
            Element != null &&
            (Kind == ImlNodeKind.Button || Kind == ImlNodeKind.Switch ||
             Kind == ImlNodeKind.Checkbox || Kind == ImlNodeKind.Icon ||
             Kind == ImlNodeKind.ArrowButton || Kind == ImlNodeKind.Link ||
             Kind == ImlNodeKind.SelectorOption);

        public string Path =>
            (Parent == null || Parent.Kind == ImlNodeKind.Root ? "" : Parent.Path + "/") +
            Tag + "[" + (Element != null ? Element.SourceLine : 0) + "]";
    }
}
