# Iris.Iml

独立的声明式 IMGUI/UGUI 双后端 UI 库：JSX 风格 `.iml` 文档 → 解析 → 绑定求值 →
样式引擎 → 共享布局引擎 → rect 树 → 后端绘制。**不依赖 IridiumLayout**（渲染路径
零 GPL 衍生代码），两个后端共享同一棵样式/布局结果，保证同一份 `.iml` 在 IMGUI
（设置面板）与 UGUI（对话框 / 通知条）上渲染结果近似一致。

## 渲染管线

```
LoadFile/LoadContent → ImlParser（JSX-like，peek/advance，无正则）
  → ImlDocument（元素树 + <Reference> 合并 + <Style> 注册）
  → StyleEngine.Prepare（extends 解析 / 特异性排序 / 文档级 --var 收集）
  → ImlRuntime.BuildTree（求值属性/表达式 → ImlNode 树）
  → LayoutEngine.Layout（flex 子集 → canvas-space Rect，y 向下）
  → 后端绘制（IrisGuiRenderer 每帧 / IrisGoRenderer Rebuild）
```

## 文件结构

| 文件 | 职责 |
|---|---|
| `ImlParser.cs` | `.iml` 解析：元素/属性（string/expression/template/style 对象）、选择器解析 |
| `ImlStyle.cs` | 样式数据模型（Setters + Selector + 优先级元数据） |
| `StyleEngine.cs` | 级联、`extends`、`--` 变量、`var()`、伪类状态、语义默认值 |
| `StyleValues.cs` | 数字/长度/颜色/简写解析工具（InvariantCulture 优先） |
| `ExpressionEvaluator.cs` | `{expr}` 表达式求值、`localize()` 等内建 |
| `BindingContext.cs` | 双向绑定上下文（SetContextValue / PropertyChanged） |
| `ImlNode.cs` | 布局后的节点树（Rect/Clip/Desired/状态标志） |
| `LayoutEngine.cs` | 共享 flex 布局 + 控件固有尺寸常量 |
| `ImlRuntime.cs` | 编排：Load/BuildTree/事件分发/effects/滚动位置/纹理加载 |
| `IrisGuiRenderer.cs` | IMGUI 后端 |
| `IrisGoRenderer.cs` | UGUI 后端 |
| `GuiTextureFactory.cs` | 程序化纹理（圆角/圆形/图标符号/箭头/对勾），带缓存 |

## 样式引擎语义

- **来源与顺序**：默认样式（语义名 + 标签选择器，注册序在前）→ 文档选择器样式
  （特异性升序，平局按注册序，后者胜）→ 元素 `style` 属性（具名样式或内联对象）。
- **kebab→camel**：`font-size` 与 `fontSize` 等价，解析时统一归一化。
- **简写**：`padding`/`margin`/`border` 展开（`"8"`、`"6,10"`（纵,横）、四值 CSV）。
- **`extends`**：子样式缺失的属性从父样式填充（按解析前的文档声明序，环检测）。
- **变量**：文档级 `--*` 收集进 `_variables`；元素/继承样式中的 `--*` 永远可继承；
  `var(--x, fallback)` 查找顺序：合并元素样式 → 继承 → 文档 `_variables` → fallback。
- **伪类状态**（`ImlStateFlags`）：`:checked` `:hover` `:press` `:disabled` 等；
  构建期固定 `Checked/Disabled`，IMGUI 在绘制空间实时 OR 入 `Hover/Press`，
  UGUI 由驱动器（`ImlDriver`）每帧射线检测后对交互节点重应用视觉。
- **默认值**：`.padding`（8/10）、`.background`（#0D0E0F）、`.bg-default`（#0D0E0F）、
  title 20 加粗 / subtitle 15 / label 14 / hint·secondary 12（#7D7E7F）、
  Button/TextField/TextArea/Switch/Checkbox/Slider/Icon/ArrowButton 的控件默认外观。

## 布局引擎

- flex 子集：`direction`（row/column）、`gap`、`padding`、`margin`、`justifyContent`、
  `alignItems` + `align` 属性、`position:absolute` + `anchor`、`display:none`、
  `opacity`、`overflow:hidden`（IMGUI `GUI.BeginGroup` / UGUI `RectMask2D`）。
- **可拉伸种类**（主轴 grow=1 且横截拉伸）：`Root, Box, Fill, Separator, Text,
  ScrollView`；其余控件取固有尺寸（常量在 `LayoutEngine` 顶部，两端共用：
  Switch 40×22、Checkbox 22、Icon 24、ArrowButton 22、Slider 轨 6/拇指 14 等）。
- 无显式 `align` 的拉伸行：非流式子项截向居中（老设置行观感）；`Text` 截向
  拉伸但垂直居中（两个后端统一 `MiddleLeft/Center/Right`）。
- `Spacer`：有显式尺寸 → grow 0；无 → grow 1（即 Fill 语义）。
- `ScrollView`：布局期 `ApplyScroll=false`（内容按未滚动坐标摆放），滚动偏移由
  后端持有：IMGUI 直接平移 + 裁剪；UGUI 交给 `ScrollRect`，值双向同步回 runtime。
- 根尺寸：默认 `min(期望宽, 可用宽)`；`RootStretchWidth=true`（IMGUI）拉伸到可用宽；
  `RootWidth/RootHeight` ≥ 0 时强制（UGUI `RootSizeOverride` 传给这两个字段）。

## 后端

### IrisGuiRenderer（IMGUI）

每帧：探测可用宽（`GUILayoutUtility.GetRect` 探针）→ BuildTree → Layout →
按 rect 绘制（绝对坐标，嵌套组裁剪）。事件在绘制空间解析（hover/press），
交互结束时 `FlushEffects`。文本测量用 GUI 样式真实字体度量。Link 下划线手工
绘制（IMGUI 富文本不支持 `<u>`）。

### IrisGoRenderer（UGUI）

`Rebuild()`：BuildTree → Layout → 逐节点建 GameObject（顶左锚 + y 翻转放置，
无 LayoutGroup/ContentSizeFitter）。

契约（消费者依赖，勿破坏）：

- 根容器即名为 **`DialogWrapper`** 的 RectTransform，且必须是 `RootObject` 的
  **最后一个子节点**（重建只销毁 index ≥ 1 的旧子节点，index 0 保留给遮罩/
  消费者占位）。
- 节点 GameObject 以标签命名（`HBox`/`Text`/`Button`/…），`wrapper.Find("HBox/Text")`
  可用；Root 节点本身不建 GO，其子节点直接挂在 wrapper 下。
- wrapper 默认居中锚 + `sizeDelta` = 布局根尺寸；消费者可随后自行改锚/钉位。
- `RootSizeOverride`：强制根布局尺寸（如 400×50 通知条），wrapper 同步此尺寸。

交互：Button/Switch/Checkbox/Icon(有事件)/ArrowButton/Link/SelectorOption 挂
UGUI `Button`（transition=None，视觉由样式驱动）；Slider 为自绘拖拽
（`ImlSliderDrag`）；TextField/TextArea 为 `InputField`（onValueChanged 绑定、
onEndEdit 提交、`ImlSelectFx` 聚焦换边框）。hover/press 伪类由 `ImlDriver`
每帧 `RaycastAll` 命中交互节点后重应用样式。文本测量用字体逐字符 advance +
1.35 倍行高（确定性，不依赖 TextGenerator 状态）。

## 兼容性与已知限制

- 面向 `v3/Resources/ui/` 现有 9 份 `.iml`（Settings/General/…/Dialog/VRAM…，
  含 `@Colors.iml` 引用），老语法全部兼容。
- UGUI 侧容器节点的 `:hover` 伪类不生效（容器不参与射线注册，仅交互控件生效）；
  SelectorOption 的 `:hover` 会被选中态烘焙覆盖。
- `CustomCanvas` 仅 IMGUI 后端支持（UGUI 跳过，与旧版行为一致）。
- `ImlRuntime.LoadFile` 会 `Reset()` 样式引擎，重复加载不会累积文档样式。
