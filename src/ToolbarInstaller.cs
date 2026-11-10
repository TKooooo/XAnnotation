using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using UiApplication = Gssoft.Gscad.ApplicationServices.Application;
using CadApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
#elif ACAD_CORE
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#elif ACAD_PRE2013
// AutoCAD 2012 及更早：还没有 AcCoreMgd 拆分，只有一个 ApplicationServices.Application。
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace XAnnotation
{
    /// <summary>「X-批注」CAD 原生工具栏：单字按钮直达常用命令（浮动快捷栏已取消，这里是唯一入口）。
    /// 与 <see cref="MenuInstaller"/> 同一套路——全部通过 COM 反射操作宿主菜单组，任何一步失败都只记日志并返回 false，
    /// 绝不因为"工具栏建不出来"影响插件其它功能。
    /// 按钮图标由 WPF 现场生成：白底深蓝单字（观感与原浮动快捷栏按钮一致），先在 64×64 高清画好再缩到 16×16。</summary>
    internal static class ToolbarInstaller
    {
        /// <summary>工具栏名称；重复加载时按此名复用，不重复创建（避免宿主报"已存在"）。</summary>
        internal const string ToolbarName = "X-批注";

        /// <summary>单个工具栏按钮：图标单字 / 按钮名 / 悬停提示 / 命令。</summary>
        private sealed class ToolbarButtonSpec
        {
            public string Glyph;
            public string Name;
            public string Help;
            public string Command;
        }

        /// <summary>按钮清单（快捷栏已取消，工具栏是唯一入口）。
        /// "批"指向 X_PZ_NOTE（弹出功能选择框：选外形/连续绘制/多对一/仅绘云线，并可跳转批注设置）；
        /// "快"指向 X_PZ_DRAW（快速直绘，不弹功能选择框，按当前设置直接框选绘制）。</summary>
        private static readonly ToolbarButtonSpec[] Buttons =
        {
            new ToolbarButtonSpec { Glyph = "批", Name = "批注", Help = "批注绘制：弹出功能选择框（选外形/连续绘制/多对一批注，可跳批注设置）", Command = "X_PZ_NOTE" },
            new ToolbarButtonSpec { Glyph = "快", Name = "快速直绘", Help = "快速直绘：不弹功能选择框，框选云线范围 → 放文字框 → 填写内容", Command = "X_PZ_DRAW" },
            new ToolbarButtonSpec { Glyph = "移", Name = "移动", Help = "移动批注文字框、文字和引线（云线不动）", Command = "X_PZ_MOVE" },
            new ToolbarButtonSpec { Glyph = "编", Name = "编辑", Help = "编辑批注（双击批注也可）", Command = "X_PZ_EDIT" },
            new ToolbarButtonSpec { Glyph = "删", Name = "删除", Help = "删除批注（支持 UNDO）", Command = "X_PZ_DELETE" },
            new ToolbarButtonSpec { Glyph = "隐", Name = "隐藏", Help = "隐藏批注（未预选时隐藏全图批注）", Command = "X_PZ_HIDE" },
            new ToolbarButtonSpec { Glyph = "显", Name = "显示", Help = "显示批注（未预选时显示全图批注）", Command = "X_PZ_SHOW" },
            new ToolbarButtonSpec { Glyph = "合", Name = "合并", Help = "合并批注：内容完全一致的批注并成一条（保留编号最小的一条，其余云线并入）", Command = "X_PZ_MERGE" },
            new ToolbarButtonSpec { Glyph = "滤", Name = "过滤", Help = "过滤批注：只显示内容完全一致的批注，其余隐藏（按内容分组选择）", Command = "X_PZ_FILTER" },
            new ToolbarButtonSpec { Glyph = "刷", Name = "格式刷", Help = "格式刷：选一条批注提取样式 → 调整对话框（字高/文字样式/云线样式/图层颜色）→ 连续刷到其他批注", Command = "X_PZ_FORMAT" },
            new ToolbarButtonSpec { Glyph = "汇", Name = "汇总", Help = "批注汇总", Command = "X_PZ_SUMMARY" },
            new ToolbarButtonSpec { Glyph = "例", Name = "清单", Help = "批注清单：把本图全部批注生成清单表画在图上（列与批注列表一致，不框选、不画引线）", Command = "X_PZ_LEGEND" },
            new ToolbarButtonSpec { Glyph = "出", Name = "导出", Help = "导出批注 (CSV)", Command = "X_PZ_EXPORT" },
            new ToolbarButtonSpec { Glyph = "入", Name = "导入", Help = "导入批注 (CSV)", Command = "X_PZ_IMPORT" },
            new ToolbarButtonSpec { Glyph = "历", Name = "历史", Help = "批注历史记录（留痕）", Command = "X_PZ_HISTORY" },
        };

        private static bool _retryAttached;
        private static int _retryCount;
        private static DateTime _lastRetryUtc = DateTime.MinValue;

        // ===== 摆放（停靠方向 / 浮动位置）=====

        /// <summary>停靠状态常量（AutoCAD ActiveX <c>AcToolbarDockStatus</c>，本机实测：
        /// 标准/图层=0 顶部、绘图=2 左侧、修改=3 右侧，X-批注原先是 1 底部）。</summary>
        private const int DockTop = 0;
        private const int DockBottom = 1;
        private const int DockLeft = 2;
        private const int DockRight = 3;
        private const int DockFloating = 4;

        /// <summary>首次安装时的默认摆放：<b>停在底部横排一排</b>（就是用户现在看到的样子）。
        /// 之后一律按用户自己拖出来的摆放走，见 <see cref="ApplySavedPlacement"/>。</summary>
        private const int DefaultDock = DockBottom;

        /// <summary>当前工具栏 COM 对象（摆放轮询用；GSCAD 上不存在，恒为 null）。</summary>
        private static object _toolbar;

        /// <summary>上次已知的摆放：没变就不重写文件（轮询每 1.2 秒最多读一次属性）。</summary>
        private static ToolbarPlacement _knownPlacement;

        /// <summary>本轮会话里是否已经摆好过位置：摆成功就不再碰它，用户怎么拖都随他。
        /// <para>初建时 Dock 可能失败（宿主界面还没就绪），Idle 重试会再调 <c>Ensure</c>，
        /// 只要没摆成功就继续试。</para></summary>
        private static bool _placementApplied;

        /// <summary>等落盘的候选摆放 + 已经连续读到的次数（见 <see cref="CapturePlacement"/> 的稳定判定）。</summary>
        private static ToolbarPlacement _pendingPlacement;
        private static int _pendingStableCount;

        private static bool _placementWatchAttached;
        private static DateTime _lastPlacementWatchUtc = DateTime.MinValue;
        private static int _placementWatchFailures;

        /// <summary>摆放轮询间隔（毫秒）。工具栏是 COM 对象、没有"被拖动"事件，只能轻量轮询。
        /// 1.2 秒：用户拖完到关 CAD 之间基本都能抓到；又比 3 秒短得多，不至于"刚拖完就重启"丢位置。</summary>
        private const int PlacementWatchIntervalMs = 1200;

        /// <summary>连续读不到 <c>DockStatus</c> 这么多次就停掉轮询（个别宿主 / 版本不公开该属性）。</summary>
        private const int PlacementWatchMaxFailures = 10;

        /// <summary>两次重试之间的最小间隔（毫秒）：Idle 在 CAD 空闲时触发极其频繁，必须节流。</summary>
        private const int RetryIntervalMs = 1000;

        /// <summary>最多重试多少次。</summary>
        private const int MaxRetryCount = 180;

        /// <summary>创建或刷新工具栏并显示。失败时挂 Idle 回调<b>持续</b>重试
        /// （NETLOAD / 随 CAD 启动自动加载时，宿主界面尚未就绪是常态 —— 只重试一次必然失败）。</summary>
        /// <returns>工具栏已就绪返回 <c>true</c>。</returns>
        public static bool EnsureWithRetry()
        {
            if (Ensure(out var message))
            {
                PluginLog.Info("Toolbar", message);
                return true;
            }

            PluginLog.Warning("Toolbar", message);
#if !GSCAD
            // 浩辰不挂重试：这条路上每次尝试都会去碰 MenuGroups，而它在 .NET 8 下必抛
            // AccessViolationException；重试 180 次 = 把进程状态反复踩坏 180 次。见 Ensure 的注释。
            AttachRetry();
#endif
            return false;
        }

        /// <summary>卸载插件时摘掉 Idle 重试回调（工具栏本身保留在 CAD 界面，不随卸载消失）。</summary>
        public static void Detach()
        {
            // 卸载前最后记一次摆放：用户拖完工具栏没过多久就卸载插件时，轮询可能还没轮到。
            CapturePlacement(true);
            DetachPlacementWatch();
            _toolbar = null;
            DetachRetry();
        }

        private static void AttachRetry()
        {
            if (_retryAttached) return;
            try
            {
                _lastRetryUtc = DateTime.MinValue;
                CadApplication.Idle += OnRetryIdle;
                _retryAttached = true;
            }
            catch (Exception ex) { _retryAttached = false; PluginLog.Error("Toolbar.Retry", ex); }
        }

        private static void DetachRetry()
        {
            if (!_retryAttached) return;
            _retryAttached = false;
            try { CadApplication.Idle -= OnRetryIdle; }
            catch (Exception ex) { PluginLog.Error("Toolbar.Detach", ex); }
        }

        private static void OnRetryIdle(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastRetryUtc).TotalMilliseconds < RetryIntervalMs) return;
            _lastRetryUtc = now;

            if (++_retryCount > MaxRetryCount)
            {
                DetachRetry();
                PluginLog.Warning("Toolbar.Retry",
                    "已重试 " + MaxRetryCount + " 次仍未建出工具栏，停止自动安装；需要时手动执行 X_PZ_TOOLBAR。");
                return;
            }

            if (Ensure(out var message))
            {
                DetachRetry();
                PluginLog.Info("Toolbar.Retry", message + "（第 " + _retryCount + " 次重试成功）");
                return;
            }

            if (_retryCount == 1 || _retryCount % 20 == 0)
            {
                PluginLog.Warning("Toolbar.Retry", "第 " + _retryCount + " 次重试仍失败: " + message);
            }
        }

        /// <summary>创建（或复用）「X-批注」工具栏，按当前代码重建按钮并显示。</summary>
        public static bool Ensure(out string message)
        {
#if GSCAD
            // ★ 浩辰：暂时不建工具栏。
            //   原因：工具栏只能从「菜单组的 Toolbars 集合」拿，而浩辰的
            //   Application.AcadApplication.MenuGroups 在 .NET 8 宿主下读 Count 必抛
            //   AccessViolationException（进程级内存损坏 —— 一旦踩到，插件之后所有宿主调用都会变随机）。
            //   2026-10-13 的实机日志里，这条路上重试了 40 次、40 次全崩。
            //   菜单改走 GstarCadMenu 的 CUI 文件注入后已经可用，命令也都能从菜单/命令行直接跑，
            //   所以这里**一次都不尝试**，宁可少一个工具栏，也不让进程状态被访问冲突毁掉。
            message = "浩辰暂不创建工具栏（菜单已可用；命令名 X_PZ_* 也可直接使用）。";
            return false;
#else
            try
            {
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) { message = "当前 CAD 未公开工具栏接口。"; return false; }

                var created = false;
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null)
                {
                    toolbars.InvokeMethod("Add", ToolbarName);
                    toolbar = TryGetToolbar(toolbars);
                    created = true;
                }
                if (toolbar == null) { message = "工具栏创建失败：宿主未返回工具栏对象。"; return false; }

                // 重复加载（或手动刷新）时先清掉旧按钮，再按当前代码重建，保证与版本一致。
                // 必须判 null 再删：宿主侧取项失败时 InvokeMethod 会返回 null，
                // 不判就会退化成"Count 一直 > 0、却又删不掉"的死循环。
                while (Convert.ToInt32(toolbar.GetProperty("Count")) > 0)
                {
                    var first = toolbar.InvokeMethod("Item", 0);
                    if (first == null)
                    {
                        PluginLog.Warning("Toolbar.Ensure", "清空旧按钮时取不到按钮对象，已放弃继续清理。");
                        break;
                    }
                    first.InvokeMethod("Delete");
                }

                for (var i = 0; i < Buttons.Length; i++) AddButton(toolbar, i, Buttons[i]);

                SetProperty(toolbar, "Visible", true);
                // 摆放：每个会话只在"工具栏刚建出来"时摆一次，摆成功就不再碰它；
                // 刷新（同一会话里重复加载 / 手动 X_PZ_TOOLBAR）时保持用户当前摆放不动 ——
                // 一刷新就 Dock 一次，等于每次加载都把用户拖到侧边的竖排拽回下边。
                _toolbar = toolbar;
                if (created) _placementApplied = false;
                if (!_placementApplied) _placementApplied = ApplySavedPlacement(toolbar);
                AttachPlacementWatch();

                message = "X-批注工具栏已" + (created ? "创建" : "刷新") + "（" + Buttons.Length + " 个按钮）。";
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Toolbar.Ensure", ex);
                message = "工具栏创建失败: " + ex.Message;
                return false;
            }
#endif
        }

        /// <summary>切换工具栏显示/隐藏；工具栏还不存在时直接创建并显示。</summary>
        public static bool ToggleVisible(out string message)
        {
            try
            {
                var toolbars = GetToolbarsCollection();
                if (toolbars == null) { message = "当前 CAD 未公开工具栏接口。"; return false; }
                var toolbar = TryGetToolbar(toolbars);
                if (toolbar == null) return Ensure(out message);

                var visible = Convert.ToBoolean(toolbar.GetProperty("Visible"));
                SetProperty(toolbar, "Visible", !visible);
                message = visible ? "X-批注工具栏已隐藏。" : "X-批注工具栏已显示。";
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Toolbar.Toggle", ex);
                message = "工具栏操作失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>取当前宿主菜单组下的 Toolbars 集合 COM 对象；宿主不支持时返回 <c>null</c>。</summary>
        private static object GetToolbarsCollection()
        {
#if GSCAD
            // 浩辰：**不碰 MenuGroups**。它在这个宿主 + .NET 8 下读 Count 必抛 AccessViolationException
            // （2026-10-13 实机日志里 Toolbars 那条路就是这么崩的）。工具栏因此暂时不可用，
            // 菜单与命令行命令 X_PZ_* 不受影响。
            return null;
#else
            var menuGroups = MenuInstaller.GetStatic(typeof(UiApplication), "MenuGroups");
            if (menuGroups == null) return null;
#if ZWCAD
            var group = menuGroups.InvokeMethod("Item", 0);
#else
            var group = menuGroups.InvokeMethod("Item", "Acad");
#endif
            return group?.GetProperty("Toolbars");
#endif
        }

        /// <summary>按名称找工具栏；不存在或宿主查询抛异常时返回 <c>null</c>（等价于"还没有"）。</summary>
        private static object TryGetToolbar(object toolbars)
        {
            try
            {
                return toolbars.InvokeMethod("Item", ToolbarName);
            }
            catch (TargetInvocationException)
            {
                // COM 反射把宿主内部异常包成 TargetInvocationException：对按名查询来说等价于"不存在"。
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>加一个按钮：宏前缀与菜单一致。图标用返回的按钮对象的 <c>SetBitmaps(小, 大)</c> 设置。
        /// <para><b>血泪教训：AddToolbarButton 的第 5 个参数是 Boolean（ShowImage）而不是位图路径。</b>
        /// ActiveX 签名是 <c>AddToolbarButton(Index, Name, HelpString, Macro [, ShowImage])</c>，
        /// 往第 5 个位置传字符串会被 COM 判为类型不匹配，抛 TargetInvocationException——
        /// 症状是 11 个按钮全部只有文字没有图标（每次加载 11 条 Toolbar.Button.Icon 日志），
        /// 而失败信息被 ex.Message 吞成一句"调用的目标发生了异常"，根本看不出是参数类型错。</para></summary>
        private static void AddButton(object toolbar, int index, ToolbarButtonSpec spec)
        {
            short buttonIndex = (short)index;
            var macro = MenuInstaller.CreateMenuMacro(spec.Command);
            object button;
            try
            {
                button = toolbar.InvokeMethod("AddToolbarButton", buttonIndex, spec.Name, spec.Help, macro, false);
            }
            catch (Exception ex)
            {
                // 退化路径：某些宿主不认第 5 个参数，用 4 参重试（此时按钮没有图标，但功能可用）。
                PluginLog.Warning("Toolbar.Button.Bool", Describe(ex));
                button = toolbar.InvokeMethod("AddToolbarButton", buttonIndex, spec.Name, spec.Help, macro);
            }
            TrySetIcon(button, spec.Glyph);
        }

        /// <summary>给刚建好的按钮设图标：SetBitmaps 只认**完整文件路径**（不同于 AddToolbarButton 的位图参数，
        /// 那个要的是"支持路径下的资源名"）。大图标位图缺失时用 16×16 顶替，避免因为大小图标不全都放弃设置。</summary>
        private static void TrySetIcon(object button, string glyph)
        {
            if (button == null) return;
            var small = TryCreateIcon(glyph, 16);
            if (string.IsNullOrEmpty(small)) return;
            var large = TryCreateIcon(glyph, 32);
            if (string.IsNullOrEmpty(large)) large = small;
            try
            {
                button.InvokeMethod("SetBitmaps", small, large);
                return;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Button.Icon", Describe(ex));
            }
            // 兜底：个别宿主只认"支持路径下的资源名"，这时把 icons 目录挂进 CAD 支持路径，再用裸文件名设一次。
            try
            {
                var folder = Path.GetDirectoryName(small);
                if (!EnsureSupportPath(folder)) return;
                button.InvokeMethod("SetBitmaps", Path.GetFileName(small), Path.GetFileName(large));
            }
            catch (Exception ex) { PluginLog.Warning("Toolbar.Button.Icon.Name", Describe(ex)); }
        }

        /// <summary>把图标目录加进 CAD 支持路径（Preferences → Files → SupportPath），已存在则不重复加。
        /// 只在"用完整路径设图标"失败后才会走到这里，属于兜底路径；改动会写进当前 CAD 配置，日志里有记录。</summary>

        private static object ResolveAcadApplication()
        {
#if ZWCAD
            try { return UiApplication.ZcadApplication; } catch { return null; }
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
            try { return UiApplication.AcadApplication; } catch { return null; }
#else
            var applicationTypeNames = new[]
            {
                "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
                "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
            };
            foreach (var typeName in applicationTypeNames)
            {
                var type = Type.GetType(typeName, throwOnError: false);
                if (type == null) continue;
                var prop = type.GetProperty("AcadApplication", BindingFlags.Public | BindingFlags.Static);
                var value = prop?.GetValue(null, null);
                if (value != null) return value;
            }
            return null;
#endif
        }
        private static bool EnsureSupportPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                var app = ResolveAcadApplication();
                if (app == null) return false;
                var files = app.GetProperty("Preferences").GetProperty("Files");
                var current = Convert.ToString(files.GetProperty("SupportPath")) ?? "";
                if (current.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                SetProperty(files, "SupportPath", current.TrimEnd(';') + ";" + folder);
                PluginLog.Warning("Toolbar.SupportPath", "已把图标目录加入 CAD 支持路径: " + folder);
                return true;
            }
            catch (Exception ex) { PluginLog.Warning("Toolbar.SupportPath", Describe(ex)); return false; }
        }

        // ============ 摆放：默认底部横排，之后跟着用户走 ============

        /// <summary>新建工具栏时摆一次位置：<b>有记录就按记录还原，没记录就用默认（底部横排一排）</b>。
        /// <para>用户的分工很清楚：有人喜欢下边横排、有人喜欢侧边竖排（拖到左右两侧会自动变竖排）。
        /// 工具栏是每个 CAD 会话现场重建的，宿主自己不记位置，所以位置必须由插件记、插件摆
        /// （见 <see cref="ToolbarPlacement"/>）。这里只"还原"，用户随便拖 —— 拖动结果由
        /// <see cref="CapturePlacement"/> 轮询记住，重启后照原样摆回。</para>
        /// <para><b>浮动状态还原不了</b>（实测 AutoCAD 2014）：COM 新建出来的工具栏本身就是浮动的，
        /// 但<b>不可见</b>，<c>Visible=true</c> 设不上去（读回来还是 False）、设 <c>Left/Top</c> 直接抛
        /// "工具栏不可见"；只有 <c>Dock(0..3)</c> 之后它才会真的显示出来。
        /// <c>Dock(4)</c> 更不行 —— 会报"参数 Side 无效"。所以记录是浮动时退回默认（底部横排），
        /// 形状仍是横排一排，不会莫名其妙变成侧边竖排。</para>
        /// <returns>摆成功返回 <c>true</c>；失败时下次 <c>Ensure</c>（Idle 重试）还会再试一次。</returns></summary>
        private static bool ApplySavedPlacement(object toolbar)
        {
            if (toolbar == null) return false;
            var saved = ToolbarPlacement.Load();
            _knownPlacement = saved;

            if (saved == null || !saved.HasDock)
            {
                // 第一次装（或记录文件被删）：维持老样子 —— 停在底部、横排一排。
                return TryDock(toolbar, DefaultDock);
            }

            if (saved.Dock == DockFloating)
            {
                PluginLog.Info("Toolbar.Placement",
                    "上次是浮动摆放（" + saved.Describe() + "），该状态无法还原，按默认底部横排显示。");
                return TryDock(toolbar, DefaultDock);
            }

            if (TryDock(toolbar, saved.Dock))
            {
                TryRestoreOffset(toolbar, saved);
                PluginLog.Info("Toolbar.Placement", "按上次摆放恢复：" + saved.Describe() + "。");
                return true;
            }

            // 记录里的方向在这个宿主上摆不动：退回默认，别留在半空。
            PluginLog.Warning("Toolbar.Placement", "按记录摆放失败，回到默认底部横排。");
            return TryDock(toolbar, DefaultDock);
        }

        /// <summary>停靠之后，再把"停靠区里的偏移"摆一次。
        /// <para><b>为什么需要这一步</b>：AutoCAD 对运行时建的工具栏是"重新插入"，<c>Dock(n)</c> 一律插到
        /// 该停靠区的<b>最前面（顶部）</b> —— 用户把工具条拖到左列中部，重启后就跑到最上面
        /// （用户 2026-11-10 反馈的现场：同一条左列，位置从 y≈575 变成 y≈50）。
        /// ActiveX 没有"停靠顺序 / 列内偏移"的接口，唯一可能有效的就是直接写 <c>Left/Top</c>：
        /// 这里写一次并把<b>读回值</b>记进日志 —— 宿主认就用上，不认也顶多还是顶部，不会更糟。</para>
        /// <para>只写与停靠方向一致的那一个分量（左/右列看 <c>Top</c>，上/下排看 <c>Left</c>），
        /// 免得两个一起写把工具条挪出停靠区。</para></summary>
        private static void TryRestoreOffset(object toolbar, ToolbarPlacement saved)
        {
            if (toolbar == null || saved == null) return;
            var vertical = saved.Dock == DockLeft || saved.Dock == DockRight;
            var name = vertical ? "Top" : "Left";
            var value = vertical ? saved.Top : saved.Left;
            if (value == int.MinValue) return;

            try
            {
                toolbar.GetType().InvokeMember(name, BindingFlags.SetProperty, null, toolbar, new object[] { value });
                TryReadInt(toolbar, name, out var applied);
                PluginLog.Info("Toolbar.Placement",
                    name + " 偏移还原：写入 " + value + "，读回 " + applied + "（读回不同＝宿主不认列内偏移）。");
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Placement.Offset", name + " 写入失败：" + Describe(ex));
            }
        }

        /// <summary>把工具栏停靠到指定方向；失败返回 <c>false</c>（不冒泡）。
        /// <para>用直接反射而不是 <c>InvokeMethod</c> 扩展：那个扩展会把异常吞成 <c>null</c>，
        /// 这里需要知道"到底摆成功了没有"。</para></summary>
        private static bool TryDock(object toolbar, int dock)
        {
            if (toolbar == null) return false;
            try
            {
                toolbar.GetType().InvokeMember("Dock", BindingFlags.InvokeMethod, null, toolbar, new object[] { dock });
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Dock", "Dock(" + dock + ") 失败：" + Describe(ex));
                return false;
            }
        }

        private static void AttachPlacementWatch()
        {
            if (_placementWatchAttached) return;
            try
            {
                _lastPlacementWatchUtc = DateTime.MinValue;
                _placementWatchFailures = 0;
                CadApplication.Idle += OnPlacementWatchIdle;
                _placementWatchAttached = true;
            }
            catch (Exception ex)
            {
                _placementWatchAttached = false;
                PluginLog.Warning("Toolbar.Placement.Watch", Describe(ex));
            }
        }

        private static void DetachPlacementWatch()
        {
            if (!_placementWatchAttached) return;
            _placementWatchAttached = false;
            try { CadApplication.Idle -= OnPlacementWatchIdle; }
            catch (Exception ex) { PluginLog.Warning("Toolbar.Placement.Watch", Describe(ex)); }
        }

        /// <summary>轮询工具栏摆放：用户把它拖到侧边（竖排）或拖成浮动时，记下来，下次启动照原样摆回。
        /// <para>Idle 在 CAD 空闲时触发极频繁，必须节流；读 2～4 个 COM 属性本身很轻，
        /// 但连续失败（宿主没公开 <c>DockStatus</c>）就停掉，免得每次空闲都去踩一遍异常。</para></summary>
        private static void OnPlacementWatchIdle(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPlacementWatchUtc).TotalMilliseconds < PlacementWatchIntervalMs) return;
            _lastPlacementWatchUtc = now;
            CapturePlacement(false);
        }

        /// <summary>读一次当前摆放并落盘（没变化就不写）。<paramref name="force"/> = 忽略"没变化"判定。
        /// <para>整个方法包一层 try/catch：它跑在 CAD 的 Idle 回调里，任何冒出去的异常都会变成
        /// "致命错误"（AutoCAD 进程内没有 WPF 兜底），而记住摆放只是增强项 —— 失败顶多位置记不住。</para></summary>
        private static void CapturePlacement(bool force)
        {
            try
            {
                var toolbar = _toolbar;
                if (toolbar == null || !_placementWatchAttached) return;

                if (!TryReadInt(toolbar, "DockStatus", out var dock) || dock < 0)
                {
                    _placementWatchFailures++;
                    if (_placementWatchFailures >= PlacementWatchMaxFailures)
                    {
                        DetachPlacementWatch();
                        PluginLog.Warning("Toolbar.Placement.Watch",
                            "当前宿主读不到工具栏停靠状态，已停止记住摆放（不影响工具栏本身）。");
                    }
                    return;
                }
                _placementWatchFailures = 0;

                var placement = new ToolbarPlacement { Dock = dock, Rows = 1 };
                // 停靠时也把 Left/Top 读下来：AutoCAD 对停靠中的工具条一般只回 0，但万一是
                // "列内偏移"就能拿来还原（见 TryRestoreOffset）；读不到就保持"未知"。
                if (TryReadInt(toolbar, "Left", out var left)) placement.Left = left;
                if (TryReadInt(toolbar, "Top", out var top)) placement.Top = top;
                if (dock == DockFloating && TryReadInt(toolbar, "FloatingRows", out var rows) && rows > 0)
                    placement.Rows = rows;

                if (!force && _knownPlacement != null && _knownPlacement.SameAs(placement))
                {
                    _pendingPlacement = null;
                    _pendingStableCount = 0;
                    return;
                }

                // 稳定判定：刚 Dock 完、或用户还在拖的时候读到的都是中间态，**连续两次读到同样的值**才落盘，
                // 否则会把"刚创建还没摆好"的位置记下来，下次启动就摆到那个错误的地方。
                if (!force)
                {
                    if (_pendingPlacement != null && _pendingPlacement.SameAs(placement))
                    {
                        _pendingStableCount++;
                    }
                    else
                    {
                        _pendingPlacement = placement;
                        _pendingStableCount = 0;
                    }
                    if (_pendingStableCount < 1) return;
                }

                _pendingPlacement = null;
                _pendingStableCount = 0;
                _knownPlacement = placement;
                ToolbarPlacement.Save(placement);
                PluginLog.Info("Toolbar.Placement", "已记住工具栏摆放：" + placement.Describe() + "。");
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Placement.Capture", Describe(ex));
            }
        }

        /// <summary>安静地读一个整型属性：失败返回 <c>false</c> 且不记日志（轮询路径不能刷日志）。</summary>
        private static bool TryReadInt(object target, string name, out int value)
        {
            value = 0;
            if (target == null) return false;
            try
            {
                var raw = target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
                if (raw == null) return false;
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static void SetProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        /// <summary>异常摘要：把内层异常链一并展开。COM 反射失败时外层永远只是
        /// "调用的目标发生了异常(TargetInvocationException)"，真正的原因（HRESULT / 类型不匹配）只在内层，
        /// 只记 ex.Message 等于什么都没记。</summary>
        private static string Describe(Exception ex)
        {
            var text = "";
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (text.Length > 0) text += " <- ";
                text += current.GetType().Name + ": " + current.Message;
            }
            return text;
        }

        // ============ 图标：单字 → 白底蓝字 16×16 BMP ============

        /// <summary>把单字画成工具栏图标（BMP 文件，缓存在数据目录 icons 下），size 传 16 或 32。
        /// 观感对齐原浮动快捷栏按钮：**白底 + 深藏青单字**，整张不透明（不用 192,192,192 掩膜灰，
        /// 那样浅色/深色主题下都是同一块白底，字永远看得清）。
        /// **缓存文件名带样式版本号（tb_v3_）且按"字的码位+尺寸"命名**：改画风只须升版本号即可让旧缓存全部失效；
        /// 码位命名保证按钮增删移位不会出现"例"按钮显示"出"字这类错位；小/大图标要分别有文件（SetBitmaps 两个参数各要一个路径）。</summary>
        private static string TryCreateIcon(string glyph, int size)
        {
            try
            {
                if (size != 16 && size != 32) size = 16;
                var folder = Path.Combine(AppPaths.DataFolder, "icons");
                Directory.CreateDirectory(folder);
                var code = string.IsNullOrEmpty(glyph) ? 0 : char.ConvertToUtf32(glyph, 0);
                var path = Path.Combine(folder, "tb_v3_" + code.ToString("X4", CultureInfo.InvariantCulture) + "_" + size.ToString(CultureInfo.InvariantCulture) + ".bmp");
                if (File.Exists(path)) return path;

                // 高清渲染再缩小：直接在 16×16 上画 11px 汉字会糊成一团。
                // 先在 64×64 上以约 46px 的字号画字，再用线性插值整体缩到目标尺寸，笔画干净得多。
                const int render = 64;
                var typeface = ResolveCjkTypeface(glyph);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, render, render));
                    var glyphBrush = new SolidColorBrush(Color.FromRgb(23, 42, 108)); // 深藏青，接近快捷栏按钮的字色
                    var fontSize = render * 0.72;
#if WPF_NO_PIXELSPERDIP
                    // net45（AutoCAD 2015~2024）：FormattedText 还没有 pixelsPerDip 重载（.NET Framework 4.6.2 才加入），
                    // 它的第 7 个参数是 NumberSubstitution，所以这里必须用 6 参构造（见 XAnnotation.AutoCAD.csproj 的符号定义）。
                    var text = new FormattedText(
                        glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, glyphBrush);
#else
                    // net472 / net8 必须显式给 pixelsPerDip，而且必须是 1.0：
                    // 它表示"每个 DIP 对应多少像素"，位图按 96 DPI 渲染时就是 1.0。
                    // 千万别顺手填 96 —— 那会把字号放大 96 倍，字画到画布之外，图标只剩背景色。
                    var text = new FormattedText(
                        glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, glyphBrush, 1.0);
#endif
                    dc.DrawText(text, new Point((render - text.Width) / 2.0, (render - text.Height) / 2.0));
                }

                // RenderTargetBitmap 只接受 Pbgra32，传 Bgra32 会抛
                // ArgumentException「此操作不支持"Bgra32"PixelFormat」——
                // 症状是工具栏所有按钮全部没有图标（异常被下面的 catch 吞成一条日志），
                // 极易被当成"图标画不出来"去查画法，其实是格式参数不对。
                // 本图背景是不透明矩形，alpha 恒为 255，预乘与直通一致，24 位 BMP 直接丢 alpha 即可。
                var hi = new RenderTargetBitmap(render, render, 96, 96, PixelFormats.Pbgra32);
                hi.Render(visual);
                var scaled = new TransformedBitmap(hi, new ScaleTransform((double)size / render, (double)size / render));
                var pixels = new byte[size * size * 4];
                scaled.CopyPixels(pixels, size * 4, 0);
                WriteBmp24bpp(path, pixels, size, size);
                return path;
            }
            catch (Exception ex)
            {
                // 图标属美化项：生成失败不影响工具栏本身（按钮只是没有图标）。
                PluginLog.Warning("Toolbar.Icon", Describe(ex));
                return null;
            }
        }

        /// <summary>选一个**真正含该汉字字形**的字体。CAD 宿主进程里按名解析字体可能失败，
        /// 落到 WPF 的默认回退字体后没有中文字形，汉字会被替换成"？"——图标全部变成问号块（已实际发生）。
        /// 所以不能只写一个 FontFamily 名：这里逐个候选字体校验"字形表里确实有这个字"，
        /// 全部失败再用字体文件兜底（黑体是各版本 Windows 中文环境都自带的 TTF）。</summary>
        private static Typeface ResolveCjkTypeface(string glyph)
        {
            var ch = string.IsNullOrEmpty(glyph) ? '\0' : glyph[0];
            var names = new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "NSimSun", "DengXian", "KaiTi" };
            foreach (var name in names)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                    if (HasGlyph(tf, ch)) return tf;
                }
                catch { /* 字体不存在等情况，换下一个候选 */ }
            }

            // 文件兜底：按文件 URI 构造 FontFamily（"./文件名" 是 WPF 支持的写法）。
            var files = new[] { "simhei.ttf", "msyh.ttc", "simsun.ttc" };
            foreach (var file in files)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(new Uri("file:///C:/Windows/Fonts/"), "./" + file), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                    if (HasGlyph(tf, ch)) return tf;
                }
                catch { /* 文件不存在等情况，换下一个 */ }
            }

            // 都不行就退回候选表第一个——至少行为与旧版一致，日志里能看到图标生成失败的原因。
            return new Typeface(new FontFamily(names[0]), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        }

        private static bool HasGlyph(Typeface typeface, char ch)
        {
            if (ch == 0) return true;
            try
            {
                return typeface.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap.ContainsKey(ch);
            }
            catch { return false; }
        }

        /// <summary>把 BGRA 像素（自上而下）写成 24 位 BMP（自下而上、每行 4 字节对齐）。不依赖 System.Drawing。</summary>
        private static void WriteBmp24bpp(string path, byte[] bgra, int width, int height)
        {
            var stride = (width * 3 + 3) / 4 * 4;
            var dataSize = stride * height;
            var buffer = new byte[54 + dataSize];

            buffer[0] = (byte)'B'; buffer[1] = (byte)'M';
            WriteInt(buffer, 2, 54 + dataSize);
            WriteInt(buffer, 6, 0);
            WriteInt(buffer, 10, 54);

            WriteInt(buffer, 14, 40);
            WriteInt(buffer, 18, width);
            WriteInt(buffer, 22, height);
            WriteShort(buffer, 26, 1);
            WriteShort(buffer, 28, 24);
            WriteInt(buffer, 30, 0);
            WriteInt(buffer, 34, dataSize);
            WriteInt(buffer, 38, 2835);
            WriteInt(buffer, 42, 2835);
            WriteInt(buffer, 46, 0);
            WriteInt(buffer, 50, 0);

            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width * 4;
                var targetRow = 54 + (height - 1 - y) * stride;
                for (var x = 0; x < width; x++)
                {
                    buffer[targetRow + x * 3 + 0] = bgra[sourceRow + x * 4 + 0];
                    buffer[targetRow + x * 3 + 1] = bgra[sourceRow + x * 4 + 1];
                    buffer[targetRow + x * 3 + 2] = bgra[sourceRow + x * 4 + 2];
                }
            }

            File.WriteAllBytes(path, buffer);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8 & 0xFF);
            buffer[offset + 2] = (byte)(value >> 16 & 0xFF);
            buffer[offset + 3] = (byte)(value >> 24 & 0xFF);
        }

        private static void WriteShort(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8 & 0xFF);
        }
    }
}
