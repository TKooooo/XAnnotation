# 评估报告：下探支持 AutoCAD 2010 + 修复双击编辑失效

> 评估对象：`XAnnotation-1.0.1`（产品名 X-批注 v1.0.0）
> 评估方式：全量源码静态审查 + 本机环境实测 + Autodesk 官方文档/论坛交叉验证
> 评估日期：2026-09-29

> **决策更新（2026-09-29）**：已定档 **从 AutoCAD 2012 起支持**。据此，本报告第 2 节中
> "必须新增 net35 目标 / WPF 3.5 要重写 DataGrid 与 DatePicker" 的结论**作废**——2012/2013/2014
> 都跑在 .NET 4.0 上，改为新增 **net40** 目标即可，UI 层无需降级，改造量从"数天"降到"骨架 + 机械替换"。
>
> 但随之暴露出一个更硬、且原报告未覆盖的约束：**AutoCAD 2013 的 "The Big Split" 是 .NET API
> 的二进制兼容断点**——针对 2013+ 编译的程序集在 2012 上加载不了，且
> `Autodesk.AutoCAD.ApplicationServices.Core` 命名空间 2013 才存在。详见文末"第 6 节 实施记录"。

---

## 0. 结论摘要（先看这三条）

| # | 事项 | 结论 |
|---|---|---|
| 1 | **双击编辑失效** | 不是"配置没开"，是**实现方式本身在 AutoCAD 上的固有缺陷**。主因是：双击时 CAD 原生的双击动作（双击文字块会拉起 `MTEDIT`、双击多段线会拉起特性面板）**无法从事件里取消**，它抢占了命令上下文，插件排队的 `X_PZ_EDIT` 被吞掉；次因是取实体用的是 `Editor.SelectImplied()`，它在**编组内实体**上会返回 `PromptStatus.Error`。**两处都要改**，改法有官方权威来源。 |
| 2 | **支持 AutoCAD 2010** | **技术上可行，但不能靠"改一个数字"实现。** AutoCAD 2010 跑在 **.NET 3.5 / CLR 2.0** 上，任何 .NET 4.x 程序集都加载不进去，**必须新增第三个 AutoCAD 目标（net35）**。随之而来的是 UI 层要降级到 **WPF 3.5**：`DataGrid`、`DatePicker` 在 WPF 3.5 里不存在，必须重写（涉及历史记录窗、知识库窗、批注窗 3 个窗口）。 |
| 3 | **本机能否立即动手** | **不能，缺两样硬条件**：本机**没有 .NET SDK**（只有 3.1 运行时，编不出任何工程）；本机**没有 AutoCAD 2010**（拿不到 `acdbmgd.dll/acmgd.dll` 编译引用）。这两样必须先备齐。 |

**建议的落地顺序：先修双击（小改动、当天可验证、直接影响你现在的 2014 生产使用），再做 2010 支持（大工程、需要新环境）。**

---

## 1. 问题一：AutoCAD 2014 下双击无法编辑已有批注

### 1.1 现有实现链路（`src/PluginEntry.cs`）

```
用户双击实体
   └─ Application.BeginDoubleClick 触发            PluginEntry.cs:40 / :102
        ├─ 读设置 DoubleClickEdit（默认 true）       :106
        ├─ Editor.SelectImplied() 取"被双击的实体"   :108   ← 失效点 A
        ├─ 用事务读该实体，确认是本插件批注          :110-114
        ├─ 临时关掉快捷特性 QPMODE                   :117 / :152-164
        ├─ 清空预选、记下待处理实体                  :118-119
        └─ 挂 Application.Idle 回调                  :120
                └─ Idle 触发后：重建预选 → SendStringToExecute("X_PZ_EDIT ")   :125-150   ← 失效点 B
                        └─ X_PZ_EDIT 再取一次预选 → 弹出编辑窗口              Commands.cs:311-334
```

### 1.2 根因（按确定性排序）

#### 主因 B：CAD 原生的双击动作无法取消，抢走了命令上下文

这是**设计层面的硬伤**，不是偶发 bug。AutoCAD 官方文档与 Autodesk .NET 论坛（Norman Yuan，Autodesk 官方技术支持）明确说明：

> `BeginDoubleClick` 事件**无法取消 CAD 的原生后续处理**。你只能通过 `DocumentLockModeChanged` 事件拿到即将执行的命令名，调用 `e.Veto()` 把它拦下来，再在 `DocumentLockModeChangeVetoed` 事件里执行自己的命令。

落到本插件（批注 = 云线多段线 + 文字 MText + 边框 + 引线 + 编组）：

| 用户双击到的东西 | CAD 原生动作 | 后果 |
|---|---|---|
| 批注文字（MText） | 拉起 **`MTEDIT`** 在位文字编辑器 | 编辑器进入交互态，插件排队的 `X_PZ_EDIT` 要么排在它后面、要么被当成编辑器输入吞掉 → **看不到编辑窗口** |
| 批注云线/边框/引线（Polyline/Line） | 拉起**特性 / 快捷特性**面板 | 面板抢占前台（代码已试图用 QPMODE=0 压制，但只有一半效果），`X_PZ_EDIT` 仍不稳 |
| 编组任一成员 | 整组被选中，再走组的双击动作 | 取到的 ID 可能不是你想要的那条 |

关键点：代码里的 `SuppressQuickPropertiesMode()`（关 `QPMODE`）**只能压住快捷特性面板，压不住 `MTEDIT` 这类真正的双击命令**。而"双击文字 → 打开文字编辑器"正是用户唯一想要的双击目标。

#### 次因 A：`SelectImplied()` 在编组 / 带属性块上返回错误

同一批官方资料里记录的已知行为：

> `Editor.SelectImplied()` 在 `BeginDoubleClick` 事件里，对**普通实体**（线、圆、弧、无属性块）返回 `PromptStatus.OK`；但对**带属性的块参照**返回 `PromptStatus.Error`——Autodesk 论坛普遍认为是 managed API 的 bug，且 2012/2013 版本上都存在。

本插件的批注实体**全部在一个编组里**，双击编组成员时的预选行为与普通实体不同；再加上还有"用户先点了别的实体、又在空白处双击"这种脏预选场景（`SelectImplied()` 依然会返回那份**过期的**预选集），`PluginEntry.cs:108` 的 `return` 就会静默发生——**日志里什么都没有**（只有 `catch` 才写日志）。

这正是排障困难的原因：**失败路径完全静默**。

#### 第三点：`Idle` + `SendStringToExecute` 的时序不可靠

`Application.Idle` 在 CAD 消息循环空闲时触发，时机上**可能落在原生双击命令仍在运行期间**。此时 `SendStringToExecute` 排入的命令会排在原生命令之后，原生命令若进入交互态（MTEDIT）则永远轮不到。

### 1.3 修复方案（方案 A，推荐）

**核心思路**：不再"绕过"原生动作，而是**正面拦下它**——官方标准三段式。

1. **取实体改为命中测试**（保留 `SelectImplied` 作为快路径，失败再兜底）：
   `BeginDoubleClickEventArgs` 提供**双击点的坐标 `Point3d Location`**，用它 + 系统变量 `PICKBOX` 反算出拾取框的 4 个角点，做一次 `Editor.SelectCrossingPolygon` —— 这就是"真正被双击的那个实体"，编组、带属性块、脏预选全部失效。
2. **拦下原生命令**：`DocumentManager.DocumentLockModeChanged` 中判断命令名，命中双击命令白名单则 `e.Veto()`。
3. **在 `DocumentLockModeChangeVetoed` 里执行编辑**：此时原生命令已被取消，命令上下文是干净的，`SendStringToExecute("X_PZ_EDIT ")` 必然生效。

新增代码（放在 `PluginEntry.cs`，示意，需按 18.0/20.0 头文件编译校验）：

```csharp
// ===== 1) 拾取框命中测试：替代不可靠的 SelectImplied =====
private static ObjectId HitTestAtPickBox(Document doc, Point3d location)
{
    var ed = doc.Editor;
    var screen = ed.PointToScreen(location, 0);          // 若映射不对，把 0 改成 1（各版本视口号语义略有差异）
    var half = Convert.ToInt32(CadApplication.GetSystemVariable("PICKBOX")); // PICKBOX 系统值是半宽
    if (half <= 0) half = 4;                             // PICKBOX=0 时兜一个最小框，避免选不到

    var pts = new Point3dCollection();
    pts.Add(ed.PointToWorld(new System.Drawing.Point(screen.X - half, screen.Y - half), 0));
    pts.Add(ed.PointToWorld(new System.Drawing.Point(screen.X + half, screen.Y - half), 0));
    pts.Add(ed.PointToWorld(new System.Drawing.Point(screen.X + half, screen.Y + half), 0));
    pts.Add(ed.PointToWorld(new System.Drawing.Point(screen.X - half, screen.Y + half), 0));

    var res = ed.SelectCrossingPolygon(pts);
    if (res.Status != PromptStatus.OK || res.Value == null || res.Value.Count == 0) return ObjectId.Null;

    using (var tr = doc.Database.TransactionManager.StartTransaction())
    {
        // 命中多个（编组/重叠）时，只认带本插件 XData 的那个
        foreach (var id in res.Value.GetObjectIds())
        {
            var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (ent != null && AnnotationService.TryReadFromEntity(tr, ent, out var _)) return id;
        }
    }
    return ObjectId.Null;
}

// ===== 2) 双击事件：只做"识别 + 布防"，不再自己排队执行 =====
private static readonly string[] NativeDoubleClickCommands =
{
    "PROPERTIES", "QUICKPROPERTIES", "MTEDIT", "DDEDIT", "TEXTEDIT", "EATTEDIT", "ATTIPEDIT",
    "HATCHEDIT", "BEDIT", "REFEDIT", "IMAGEADJUST", "OLEPROPERTIES", "GROUPEDITOR", "VPMAX"
};
private static bool _armed;
private static DateTime _armedAtUtc;

private static void OnBeginDoubleClick(object sender, BeginDoubleClickEventArgs e)
{
    try
    {
        if (!SettingsStore.Load().DoubleClickEdit) return;
        var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;

        // 快路径：预选集能用就用；不可用（编组 / 带属性块 / 无预选）才做拾取框命中测试
        ObjectId id = ObjectId.Null;
        var implied = doc.Editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count == 1)
            id = implied.Value.GetObjectIds()[0];
        if (id.IsNull) id = HitTestAtPickBox(doc, e.Location);
        if (id.IsNull) return;

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (ent == null || !AnnotationService.TryReadFromEntity(tr, ent, out var _)) return;
        }

        _pendingDocument = doc; _pendingId = id;
        _armed = true; _armedAtUtc = DateTime.UtcNow;
        SuppressQuickPropertiesMode();

        // DBLCLKEDIT=0 时 CAD 不会执行任何原生双击命令，没有可拦的东西 → 直接执行
        var dblclk = Convert.ToInt32(CadApplication.GetSystemVariable("DBLCLKEDIT"));
        if (dblclk == 0) { LaunchPendingEdit(); return; }
        // 否则等 DocumentLockModeChanged 把原生命令 Veto 掉，再在 Vetoed 里执行
    }
    catch (System.Exception ex) { PluginLog.Error("DoubleClick", ex); }
}

// ===== 3) 拦下 CAD 原生双击动作 =====
private static void OnDocumentLockModeChanged(object sender, DocumentLockModeChangedEventArgs e)
{
    if (!_armed) return;
    if ((DateTime.UtcNow - _armedAtUtc).TotalSeconds > 2.0) { Disarm(); return; }   // 防止误伤后续命令
    if (Array.IndexOf(NativeDoubleClickCommands, e.GlobalCommandName.ToUpperInvariant()) < 0)
    {
        PluginLog.Warning("DoubleClick.Native", "未在双击命令白名单内，未拦截：" + e.GlobalCommandName);
        return;
    }
    e.Veto();                                                                       // 关键：取消原生双击动作
}

private static void OnDocumentLockModeChangeVetoed(object sender, DocumentLockModeChangeVetoedEventArgs e)
{
    if (!_armed) return;
    LaunchPendingEdit();
}

private static void LaunchPendingEdit()
{
    var doc = _pendingDocument; var id = _pendingId;
    Disarm();
    if (doc == null || id.IsNull || doc.IsDisposed) { RestoreQuickPropertiesMode(); return; }
    try
    {
        doc.Editor.SetImpliedSelection(new[] { id });
        doc.SendStringToExecute("X_PZ_EDIT ", true, false, false);
    }
    catch (System.Exception ex) { RestoreQuickPropertiesMode(); PluginLog.Error("DoubleClick.Launch", ex); }
}

private static void Disarm() { _armed = false; _pendingDocument = null; _pendingId = ObjectId.Null; }
```

注册/注销（`Initialize` / `Terminate`）：

```csharp
CadApplication.BeginDoubleClick += OnBeginDoubleClick;
CadApplication.DocumentManager.DocumentLockModeChanged += OnDocumentLockModeChanged;
CadApplication.DocumentManager.DocumentLockModeChangeVetoed += OnDocumentLockModeChangeVetoed;
```

配套清理：
- `PluginEntry.cs:125-150` 的 `OnIdle` 里那段"重建预选 + SendStringToExecute"应当删除（改由 `LaunchPendingEdit` 负责），`OnIdle` 只保留批注列表刷新逻辑。
- `X_PZ_EDIT`（`Commands.cs:311-334`）本身不用改，它拿到预选就编辑、拿不到就让用户点选，行为已经正确。
- `RestoreQuickPropertiesMode()` 的恢复时机要从"`X_PZ_EDIT` 的 finally"扩展到"未被执行的路径"（本方案里 `Disarm()` 的各分支都补了调用）。

### 1.4 备选方案（方案 B，更彻底但更重）

放弃 `Application.BeginDoubleClick`，改用 **CUI 双击动作接管**：

1. 在插件的菜单安装流程里额外写一份 partial CUIX，覆盖 `Double Click Actions` 中 `MTEXT / LINE / LWPOLYLINE` 三项，指向插件的分发命令（例如 `X_PZ_DBLCLK`）；
2. `X_PZ_DBLCLK` 内部：判断当前预选实体是否为 X-批注 → 是则走编辑流程；不是则**转发给该项目类型原本的原生命令**（`MTEDIT` / `PROPERTIES`），保证不影响普通图纸操作；
3. 前置条件：`DBLCLKEDIT` 必须为 1；改 CUIX 在受管控的制图环境里可能不被允许。

**取舍**：方案 B 不依赖 COM 事件、无定时/时序问题，理论上最稳，但需要维护一份 CUIX 资源并处理"转发原生命令"的分支（一旦漏掉某类型实体，用户会觉得双击功能坏了）。**建议先上方案 A**，它改动集中在 `PluginEntry.cs` 一个文件；若实测仍有宿主差异，再考虑 B 兜底。

### 1.5 如何在你的 2014 上 5 分钟确证根因

在 `OnBeginDoubleClick` 开头补一句诊断日志（临时），然后双击一次批注、看日志：

```csharp
PluginLog.Info("DoubleClick.Diag",
    $"fire=1, dblclkedit={CadApplication.GetSystemVariable("DBLCLKEDIT")}, implied={doc.Editor.SelectImplied().Status}");
```

日志位置：`%APPDATA%\XAnnotation\Logs\XAnnotation.log`

| 看到什么 | 说明什么 |
|---|---|
| 没有 `DoubleClick.Diag` 这一行 | 事件根本没触发（`BeginDoubleClick` 订阅失败或设置里「双击编辑」被关了）→ 先查设置窗口那个勾选框 |
| 有日志，`implied=Error` | 坐实**失效点 A**（`SelectImplied` 取不到）→ 上命中测试 |
| 有日志，`implied=OK` 但编辑窗口不出现 | 坐实**失效点 B**（原生 `MTEDIT`/特性面板抢占）→ 上 Veto 方案 |

---

## 2. 问题二：支持范围下探到 AutoCAD 2010

### 2.1 硬约束：版本 ↔ 运行时 ↔ API 对照表

| AutoCAD | ObjectARX | 需要的托管程序集 | 运行时 | 本插件现状 |
|---|---|---|---|---|
| **2010** | R18.0 | AcDbMgd + AcMgd | **.NET 3.5 (CLR 2.0)** | ❌ **不支持** |
| **2011** | R18.1 | AcDbMgd + AcMgd | **.NET 3.5** | ❌ 不支持 |
| 2012 | R18.2 | AcDbMgd + AcMgd | .NET 4.0（可用 3.5 编） | ❌ 不支持 |
| 2013 / 2014 | R19.0 | **AcCoreMgd** + AcDbMgd + AcMgd | .NET 4.0 | ⚠️ 未声明，但实测可用（见下） |
| 2015~2024 | R20.0~ | 同上 | .NET 4.5 | ✅ net45 目标 |
| 2025~2027 | R25.0~ | 同上 | .NET 8 | ✅ net8.0-windows 目标 |

两条关键推论：

1. **要覆盖 2010/2011/2012，只能新增 `net35` 目标**。.NET 4.x 程序集带 `TargetFramework v4.0` 特性，在 CLR 2.0 上会直接拒绝加载——不是"可能有问题"，是加载不了。
2. **2013/2014 不需要单独目标**：.NET 4.5 是 4.0 的原位升级，你实测"2014 能加载 `XAnnotation.AutoCAD.dll`（net45）并正常标注"与此吻合。所以 2013/2014 可以继续用现有 net45 产物——**但这是"能用"而非"已适配"**，建议在 README 的支持矩阵里把 2013/2014 明确标注为"兼容可用（按 2015 API 编译）"，避免用户以为是官方支持。

### 2.2 阻塞项清单（按层次，全部已逐条核对源码）

#### A. 构建/依赖层

| 项 | 现状 | 结论 |
|---|---|---|
| NuGet 有没有 2010 的 API 包 | `AutoCAD.NET` 包**最老只到 20.0.0（=2015）**，没有 18.0/19.0 | 2010 目标**必须**像 ZWCAD 工程一样走 `HintPath` 指向本机 AutoCAD 2010 安装目录 |
| WPF 3.5 能否用 SDK 风格工程 | `Microsoft.NET.Sdk.WindowsDesktop` 官方支持 **.NET Framework 3.0+**，并会为 net35 自动引入 `WindowsBase/PresentationCore/PresentationFramework` | ✅ 可行，但工程里现有 `<Reference Include="System.Xaml" />` **必须删掉**（.NET 3.5 没有这个程序集） |
| `Microsoft.NETFramework.ReferenceAssemblies` | 已用 1.0.3，含 net35 参考程序集 | ✅ 无需 Visual Studio |
| `System.ValueTuple 4.5.0` | 两个工程都引了，但**全量检索确认代码里没有任何元组语法** | 属于**残留死依赖**，net35 目标直接不要它（顺带可清理另外两个工程） |

#### B. WPF 层（**本方案的主要工作量**）

.NET 3.5 的 WPF **没有** `DataGrid` 和 `DatePicker`（二者都是 .NET 4.0 才进 BCL）。命中情况：

| 文件 | 用到的 WPF 4.0+ 特性 | 改造方式 |
|---|---|---|
| `Views/HistoryWindow.xaml` | `DataGrid` + 7 个 `DataGridTextColumn` | 改 `ListView + GridView`（`FilterWindow.xaml` 已有同款写法可直接照抄） |
| `Views/KnowledgeWindow.xaml` | `DataGrid` + 17 个 `DataGridTextColumn`（**条目表可直接编辑、内容列支持换行**） | 改 `ListView + GridView` + 每格 `TextBox`（`UpdateSourceTrigger=PropertyChanged`），保留原有"改完自动保存"逻辑 |
| `Views/AnnotationWindow.xaml` | `DatePicker`（4 处） | 改 `TextBox`（沿用既有 `yyyy-MM-dd` 格式校验）或三个 `ComboBox` |
| `Themes/XTheme.xaml:35` | `TextOptions.TextFormattingMode` | 用 `#if NET35` 包掉，或改 `RenderOptions` |

其余控件（`ListView/GridView/TabControl/Popup/UniformGrid/ScrollViewer/ContextMenu/DataTrigger` 等）**WPF 3.5 全部支持**，无需改动——这一层的好消息是改造面很收敛。

另外 `ToolbarInstaller.cs` 里 `FormattedText` 的 6 参重载分支已由 `WPF_NO_PIXELSPERDIP` 符号处理，net35 目标继续定义该符号即可。

#### C. BCL 层（.NET 4.0+ 才有的 API，已逐条定位）

| API | 出现次数 | 处理方式 |
|---|---|---|
| `String.IsNullOrWhiteSpace` | **38 处** | 加一个 `TextKit.IsBlank(string)` polyfill（`v == null \|\| v.Trim().Length == 0`），全量替换 |
| `File.ReadLines` | 2 处 | 改 `File.ReadAllLines` |
| `IReadOnlyList<T>` | 1 处 | 改 `IList<T>` 或 `List<T>` |
| `string.Join(string, IEnumerable<string>)` | 4 处 | 补 `.ToArray()`（net35 只有数组重载） |
| `System.ValueTuple` | 0 处（仅包引用） | 删引用即可 |

**没有** 的问题（好消息）：无 `async/await`、无 `Task`、无 `Span/Memory`、无 `Index/Range`、无 `dynamic`、无 `HashSet` 之外的新集合类型、无 `Enum.TryParse/Enum.HasFlag/Enumerable.Zip/StringBuilder.Clear`。也就是说 C 层是一次机械替换，不涉及逻辑重写。

#### D. CAD API 层（需编译驱动发现）

代码里出现的、需要按 18.0 头文件逐一核对的成员：

| API | 用途 | 风险 |
|---|---|---|
| `Editor.CurrentUserCoordinateSystem`（`AnnotationService.cs:3084`） | UCS→WCS 变换 | ⚠️ 中等，2010 可能没有该属性；缺失时需退回"读 UCS 表 + `UcsTableRecord`"自己算变换矩阵 |
| `Document.CapturePreviewImage`（`WordExporter.cs`，3 处） | Word 导出的云线截图 | ⚠️ 偏高，缺失则需整条走 `CaptureCadWindow` 后备路径（代码已有后备，降级成本低） |
| `Editor.Regen()`（`WordExporter.cs:125`） | 截图前重绘 | ⚠️ 中等 |
| `Application.ShowModalWindow`（7 处） | 模态显示 WPF 窗口 | ⚠️ 中等；缺失时需退回 `ShowDialog()` 路径（`CadDialog.cs` 已有回退分支） |
| `Editor.PointToScreen/PointToWorld` | **上面双击修复方案 A 要用** | ⚠️ 中等；若 18.0 缺失，需改成"按当前视图高度换算一个 WCS 交叉窗口"的退化实现 |
| `AcCoreMgd` 类型 | — | ✅ 低风险：代码已用 `#if ACAD_CORE` 隔离，2010 目标不定义该符号即可 |
| 其余（`Polyline/MText/Group/Xrecord/ResultBuffer/PromptSelectionResult` 等） | — | ✅ 都是 R17 时代就有的老 API |

这一段**无法在纸面穷举**，正确做法是：建好工程 → 对着 AutoCAD 2010 的 `acdbmgd.dll/acmgd.dll` 编译 → **让编译器把所有缺失成员列出来** → 逐个加 `#if ACAD2010` 分支。这是可控的迭代，不是未知风险。

#### E. 平台位数（容易被忽略，但对 2010 场景很关键）

现有三个工程都是 `<PlatformTarget>x64</PlatformTarget>`。**AutoCAD 2010 有 32 位版本**（当年大量装在 32 位 Windows XP/7 上，国内设计院同理），而 x64 目标程序集在 32 位进程里加载不了。

**结论：2010 目标必须用 `AnyCPU`**（或额外产出 x86 变体）。

#### F. 打包与文档

- 目录：新增 `bin-acad2010/`（与 `bin-acad/`、`bin-acad2025-2027/`、`bin/` 并列）
- DLL 命名：`XAnnotation.AutoCAD2010.dll`（与现有三个并列，README 的"三个 DLL 不可混用"要改成四个）
- `build.cmd` 增加 `2010` 模式与 `all/release` 的覆盖；`README.md` 的支持矩阵、安装表、"已知限制"（目标机需装 .NET 3.5）同步更新

### 2.3 落地方案：新增 `src/XAnnotation.AutoCAD2010.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">
  <!-- AutoCAD 2010~2012（.NET 3.5 / CLR 2.0）构建：与其它目标共用全部源码，仅宿主引用与 WPF 版本不同。 -->
  <PropertyGroup>
    <TargetFramework>net35</TargetFramework>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <OutputPath>$(RepoRoot)bin-acad2010\</OutputPath>
    <OutputType>Library</OutputType>
    <RootNamespace>XAnnotation</RootNamespace>
    <AssemblyName>XAnnotation.AutoCAD2010</AssemblyName>
    <!-- AutoCAD 2010 同时存在 32/64 位版本，库必须 AnyCPU 才能在两边都加载 -->
    <PlatformTarget>AnyCPU</PlatformTarget>
    <!-- net35 上没有 C# 8/9 需要的运行时类型支撑，语言版本钉在 7.3 -->
    <LangVersion>7.3</LangVersion>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <UseWPF>true</UseWPF>
    <!-- ACAD2010：隔离 2010 缺失的 API（CurrentUserCoordinateSystem / CapturePreviewImage / ShowModalWindow 等）
         NET35   ：隔离 WPF 3.5 与 BCL 差异（DataGrid / DatePicker / IsNullOrWhiteSpace …）
         WPF_NO_PIXELSPERDIP：net35 的 FormattedText 没有 pixelsPerDip 重载 -->
    <DefineConstants>AUTOCAD;ACAD2010;NET35;WPF_NO_PIXELSPERDIP</DefineConstants>
    <Acad2010Dir Condition="'$(Acad2010Dir)' == ''">C:\Program Files\Autodesk\AutoCAD 2010</Acad2010Dir>
  </PropertyGroup>
  <ItemGroup>
    <!-- 18.0 没有 NuGet 包，与 ZWCAD 工程同样走本机安装目录引用（Private=false：运行时用宿主自带的） -->
    <Reference Include="acdbmgd"><HintPath>$(Acad2010Dir)\acdbmgd.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="acmgd"><HintPath>$(Acad2010Dir)\acmgd.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="AcWindows"><HintPath>$(Acad2010Dir)\AcWindows.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="AdWindows"><HintPath>$(Acad2010Dir)\AdWindows.dll</HintPath><Private>false</Private></Reference>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
    <!-- 注意：不要 System.Xaml（.NET 3.5 没有）、不要 System.ValueTuple（代码未用元组） -->
    <Reference Include="System.Xml.Linq" />
    <Reference Include="System.Drawing" />
    <Reference Include="Microsoft.CSharp" />
    <!-- 缺 DLL 时给人话，不要留一堆 CS0246 -->
    <Error Condition="!Exists('$(Acad2010Dir)\acdbmgd.dll')"
           Text="未找到 AutoCAD 2010 托管程序集。请安装 AutoCAD 2010，或用 -p:Acad2010Dir=&quot;D:\AutoCAD 2010&quot; 指定安装目录。" />
  </ItemGroup>
</Project>
```

> 说明：`UseWPF=true` 在 net35 下由 WindowsDesktop SDK 隐式引入 `WindowsBase/PresentationCore/PresentationFramework`，**不需要**再手写这三个 `<Reference>`；而 `System.Xaml` 在 .NET 3.5 不存在，写了会编译失败。

### 2.4 工作量与风险排序

| 工作块 | 量级 | 风险 | 说明 |
|---|---|---|---|
| 新增 net35 工程 + build.cmd + 打包 | 小（半天） | 低 | 骨架照抄现有工程 |
| BCL 层替换（38+2+4+1 处） | 小（半天） | 低 | 机械替换，可脚本化 |
| WPF 表格/日期控件重写 | **大（2~4 天）** | 中 | 主要是知识库可编辑表格；功能不能退化 |
| CAD API 差异 `#if` 适配 | 中（1~3 天） | **中高** | 必须以编译迭代为准，纸面无法穷举 |
| 32/64 位双环境验证 | 中 | 中 | 需要 32 位 CAD 2010 环境，本机没有 |

### 2.5 备选方案（如果 2010 不是"硬需求"）

按性价比从高到低：

1. **只做到 2012**：如果现场其实只到 2012，而 2012 是 .NET 4.0 → **可以用现有 net45 产物兼容**（同 2013/2014 的情况），**不需要 net35 工程**，成本几乎为零。
2. **只做到 2013/2014**：同上，直接复用 net45 产物，只需补文档与冒烟测试。
3. **"精简版"**：net35 目标里**裁掉知识库窗与历史窗**（这两个窗口占 WPF 改造工作量的绝大部分），保留"画/改/删/导入导出/汇总清单"核心能力。
4. **完整版**：按 2.3 全量实施。

**我的建议**：先确认真实现场的最低版本。国内设计院确实存在 2010/2012 存量机器，但如果现场实际最低是 2012，方案 1 能用极低成本达成目标；若确实有 2010/2011（真 .NET 3.5），再走完整版或精简版。

---

## 3. 落地顺序建议（Roadmap）

| 阶段 | 内容 | 依赖 | 可验证性 |
|---|---|---|---|
| **P0** | 修双击编辑（方案 A）+ 补诊断日志 | 无（用现有 2014 即可验证） | ★★★ 当天可验证 |
| **P1** | 确认现场最低 CAD 版本；据此在"方案 1 / 精简版 / 完整版"里定档 | 需你提供现场信息 | — |
| **P2** | 若需 net35：先把 UI 降级（DataGrid/DatePicker）在现有 net45 工程里完成并用 2014 回归 | .NET SDK | ★★★ 可在 2014 上验证，不必先有 2010 |
| **P3** | 新增 net35 工程，对 18.0 编译，逐个 `#if ACAD2010` 修 API 差异 | AutoCAD 2010 | ★★ 需 2010 环境 |
| **P4** | 32 位 + 64 位双环境冒烟；更新 README/build.cmd/打包 | 同上 | ★ |

**P2 的妙处**：把 WPF 降级先做在 net45 工程上，能在你现有的 2014 里完整回归功能，把"UI 改造"和"CAD API 差异"两个风险拆开——不要同时踩。

---

## 4. 环境准备清单（本机实测结果）

| 需求 | 本机现状 | 要做的事 |
|---|---|---|
| .NET SDK（编译必需） | ❌ **只有 .NET Core 3.1 运行时，没有任何 SDK**（`dotnet --list-sdks` 为空） | 装 **.NET SDK 8**：`winget install Microsoft.DotNet.SDK.8`（或从 dotnet.microsoft.com 下载）。**只有运行时编不出任何工程** |
| 编译用 CAD 引用 | ✅ 有 AutoCAD **2014 / 2022**（`acdbmgd.dll`、`acmgd.dll`、`accoremgd.dll` 齐备） | net45/net472 可直接编；**若要编 net35，必须再有 AutoCAD 2010** |
| AutoCAD 2010 | ❌ **未安装** | 安装 AutoCAD 2010，或从有该版本的机器拷 4 个 DLL（`acdbmgd/acmgd/AcWindows/AdWindows`）到任意目录，再用 `-p:Acad2010Dir=...` 指过去（与 ZWCAD 工程同一套做法） |
| NuGet 源 | 仅 `nuget.org`（本机网络会 302 到国内镜像） | restore 若报 NU1301，在仓库根加 `NuGet.Config` 指向可用镜像 |
| 32 位测试环境 | ❌ 无 | 方案 2.2-E 的决定性验证，需另行准备 |

> 另：`build.cmd` 的 SDK 检测有缺陷（`build.cmd:145` 只跑 `dotnet --version` 却不检查退出码/`--list-sdks`），**在只有运行时的机器上会误报 `[OK] dotnet SDK`**，然后在三次 build 全挂。建议一并修掉。

---

## 5. 附：本次评估已验证的关键事实与来源

| 事实 | 来源 |
|---|---|
| AutoCAD 各版本 ↔ .NET Framework ↔ 必需托管程序集对照（2010/2011=3.5、2012/2013/2014=4.0、2015+=4.5） | Autodesk Community .NET 论坛（Gilles Chanteau 汇总表，被标记为 Accepted Solution） |
| `BeginDoubleClick` 无法取消 CAD 原生后续处理，必须用 `DocumentLockModeChanged` + `Veto()` + `DocumentLockModeChangeVetoed` 三段式 | Autodesk 官方博客 *Custom Double-Click Action Using `Application.BeginDoubleClick` Event* 及其 *Update* 篇（作者 Norman Yuan，Autodesk 技术支持） |
| `Editor.SelectImplied()` 在 `BeginDoubleClick` 内对**带属性块参照**返回 `PromptStatus.Error`（2012/2013 实测存在），且会返回**过期的预选集** | 同上 Update 篇 + Autodesk .NET 论坛原帖 |
| `BeginDoubleClickEventArgs.Location` 是 `Point3d`（双击点坐标），可用于拾取框命中测试 | Autodesk 论坛 Norman Yuan 示例代码（`Point3d pt = e.Location;`）+ 上述博客 `ed.SelectAtPickBox(e.Location)` |
| `Editor.SelectAtPickBox`（PICKBOX + `PointToScreen`/`PointToWorld` + `SelectCrossingPolygon`）是官方推荐的取实体替代实现 | 上述博客 Update 篇完整代码 |
| `Microsoft.NET.Sdk.WindowsDesktop` 官方支持 .NET Framework **3.0+**，并为 net35 隐式提供 `WindowsBase/PresentationCore/PresentationFramework` | dotnet/wpf 官方文档（SDK 目标框架支持与 .NET Framework 程序集引用表） |
| `AutoCAD.NET` NuGet 包最低版本为 **20.0.0**（无 18.0/19.0） | nuget.org flat-container 版本索引实测 |
| 本机无 .NET SDK、无 AutoCAD 2010、有 AutoCAD 2014/2022 | 本机命令实测 |

**本次评估未做**：未编译、未运行、未修改任何源码。所有 CAD API 层面的版本差异（D 节）必须在有 AutoCAD 2010 的环境里以编译结果为准。

---

## 6. 实施记录（2026-09-29：已落地的改动）

### 6.1 双击编辑修复（已改，可在现有 2014 上立即验证）

改动集中在 `src/PluginEntry.cs`，按 Autodesk 官方三段式重写：

| 段 | 事件 | 做什么 |
|---|---|---|
| 识别 | `BeginDoubleClick` | 用 `BeginDoubleClickEventArgs.Location` + 系统变量 `PICKBOX` 反算拾取框四角 → `Editor.SelectCrossingPolygon` 命中测试。**不再依赖 `SelectImplied()`**（降级为兜底） |
| 拦下 | `DocumentManager.DocumentLockModeChanged` | 布防窗口（3 秒）内对非 `X_PZ_EDIT` 的命令调 `e.Veto()`，取消 CAD 的原生双击动作（`MTEDIT` / 特性面板等） |
| 执行 | `DocumentManager.DocumentLockModeChangeVetoed` | 原生命令已被取消、命令上下文干净 → 重建预选并 `SendStringToExecute("X_PZ_EDIT ")` |

关键细节：

- `DBLCLKEDIT=0` 时没有原生命令可拦，直接执行；
- **0.4 秒兜底**：某些对象类型在 CUI 里没有配置双击动作，不会有任何命令可拦，到点由 Idle 直接执行——否则会出现"有的批注双击有反应、有的没反应"；
- **3 秒窗口 + 放行 `X_PZ_EDIT` 名称**：双重保证绝不误拦用户随手敲的下一条命令；
- 每次识别到批注、成功拦下、超时、切换图纸，都会把临时改掉的 `QPMODE` 还原；
- 全链路写日志（`DoubleClick.Hit` / `.Veto` / `.Launch`）：万一某个宿主仍不生效，日志会直接给出被拦下的命令名，便于精确定位。

`X_PZ_EDIT`（`Commands.cs`）语义不变，无需改动。

### 6.2 AutoCAD 2012 支持（已改，待对 2012 编译收尾）

| 改动 | 文件 |
|---|---|
| 新增 net40 目标：`AnyCPU`、引用 18.2 的 `acdbmgd.dll`/`acmgd.dll`、定义 `AUTOCAD;ACAD_PRE2013;WPF_NO_PIXELSPERDIP` | `src/XAnnotation.AutoCAD2012.csproj`（**新增**） |
| `ApplicationServices.Core.Application` → 2013 之前改用 `ApplicationServices.Application`（共 13 处） | `PluginEntry`、`Commands`、`AnnotationService`、`MenuInstaller`、`ToolbarInstaller`、`CadDialog`、`KnowledgeStore`、`AnnotationHistoryStore`、`MergePrompt`、`Views` 下 4 个窗口 |
| 去掉唯一的 .NET 4.5 BCL 依赖：`IReadOnlyList<string>` → `IList<string>`（否则只装 .NET 4.0 的机器加载失败） | `AutoloadManager.cs` |
| 增加 `2012` 构建模式（`all` 一并带上），含"缺 2012 程序集则自动跳过"与产物部署到 `net40/` | `build.cmd` |
| 支持矩阵改为从 2012 起，并补充已知限制 | `README.md` |

### 6.3 编译验证结果（已在隔离目录内实际完成，未改动系统环境）

本机原本没有 .NET SDK。已把 .NET SDK 8.0.404 **便携解压到隔离目录** `~/.workbuddy/binaries/dotnet`（不写系统、不改 PATH），用它做了真实编译：

| 验证项 | 命令 | 结果 |
|---|---|---|
| AutoCAD 2015~2024（net45） | `dotnet build src/XAnnotation.AutoCAD.csproj -c Release` | ✅ **0 错误 0 警告** |
| AutoCAD 2025~2027（net8） | `dotnet build src/XAnnotation.AutoCAD2025.csproj -c Release` | ✅ **0 错误 0 警告** |
| `ACAD_PRE2013` 分支（13 处别名 + 2 处 `#elif`） | 同一工程加 `-p:DefineConstants=AUTOCAD%3BACAD_PRE2013%3BWPF_NO_PIXELSPERDIP` | ✅ **0 错误 0 警告** |
| 缺 2012 程序集时的提示 | `dotnet build src\XAnnotation.AutoCAD2012.csproj`（不指定 `Acad2012Dir`） | ✅ 输出人话提示，而非一堆 `CS0246` |

**编译过程中抓到的真实错误（已修正）**：`Editor.PointToScreen` 的返回类型是 **`System.Windows.Point`**（`X`/`Y` 为 `double`），
**不是** Autodesk 2013 年那篇博客代码里的 `System.Drawing.Point`；对应的 `PointToWorld` 也接收 `System.Windows.Point`。
`PluginEntry.HitTestAtPickBox` 已按编译器给出的实际签名改正。

> 这条值得记住：该错误靠读博客/文档是发现不了的——**只有真编译才能暴露**。
> 另外 `dotnet build` 的 `-p:` 里若带 `;` 必须写成 `%3B`，否则会被 shell 拆成两个参数。

顺带产出：`bin-acad/XAnnotation.AutoCAD.dll`（约 309KB，需与同目录的 `System.ValueTuple.dll` 一起部署）与
`bin-acad2025-2027/XAnnotation.AutoCAD2025.dll` 均已重新生成，可直接 NETLOAD。

### 6.4 仍需在真机收尾的部分

2012 目标的唯一未决项是 **R18.2 的 API 面**（本机没有 AutoCAD 2012，无法对着 18.2 的 `acdbmgd.dll`/`acmgd.dll` 编译）。收尾只需一条命令 + 一次反馈：

```
build.cmd 2012
```

- 若提示 `[SKIP] AutoCAD 2012 assemblies not found`：装 AutoCAD 2012，或把它目录下的 `acdbmgd.dll` + `acmgd.dll` 拷到任意目录，再
  `dotnet build src\XAnnotation.AutoCAD2012.csproj -c Release -p:Acad2012Dir="该目录"`
- 编译若报错，把 `build.log` 发回即可。**预期暴露、需逐个加 `#if ACAD_PRE2013` 分支的成员**（即原报告 D 节，以编译结果为准）：

| 成员 | 位置 | 缺失时的退路 |
|---|---|---|
| `Editor.CurrentUserCoordinateSystem` | `AnnotationService.GetUcsMatrix` | 读 UCS 表 + `UcsTableRecord` 自行计算变换矩阵 |
| `Document.CapturePreviewImage` | `WordExporter`（截图，3 处） | 走已有的 `CaptureCadWindow` 后备路径 |
| `Editor.Regen()` | `WordExporter`（截图前重绘） | 忽略重绘 |
| `Application.ShowModalWindow` | 经 `typeof(UiApplication)` 反射调用 | 已有 `Window.ShowDialog()` 回退 |

> 另：`build.cmd` 的 SDK 检测只跑 `dotnet --version`、不判断是否真的装了 SDK，在"只有运行时"的机器上会误报 `[OK] dotnet SDK`，随后三次 build 全挂。属独立小缺陷，本轮未改。
