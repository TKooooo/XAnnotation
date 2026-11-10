using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
#if ZWCAD
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using CadApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
#else
#if ACAD_PRE2013
// AutoCAD 2012 及更早：还没有 AcCoreMgd 拆分，只有 ApplicationServices.Application 一个入口。
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif
#endif

namespace XAnnotation.Views
{
    /// <summary>批注列表：表格 + 状态筛选 + 搜索 + 详情，双击行在图纸中定位。
    /// <para>数据源统一走 <see cref="AnnotationService.GetAllAnnotations"/>（与 CSV/Word 导出同源），
    /// 本窗口只做筛选、展示与转发命令，不改图纸内容。</para>
    /// <para><b>窗口层级</b>：本窗口<b>不是</b> <c>Topmost</c> 窗口，而是挂在 CAD 主窗口上的
    /// <b>从属窗口</b>（Owner = CAD 主框架）。从属窗口天然压在属主之上（点 CAD 时一起抬起来），
    /// 但不会盖住其它程序——这是"启动批注列表后别的软件界面都被挡住"的修法，
    /// 详见下方「与 CAD 的窗口配合」一节。</para>
    /// <para><b>所有事件处理器都必须走 <see cref="Safe"/> 包一层</b>：宿主 AutoCAD 2014 里没有 WPF
    /// <c>Application</c> 对象（见 <see cref="WpfGuard"/> 的说明），任何漏出去的异常都会让 CAD 直接
    /// "致命错误"退出。以前筛选胶囊一点就崩，就是异常没人接。</para></summary>
    internal partial class AnnotationListPanel : Window
    {
        private static AnnotationListPanel _instance;

        /// <summary>表格里除"意见摘要"之外的固定列宽之和（见 XAML 顶部注释）。
        /// 摘要列宽 = 列表实际宽度 - 这个值 - 滚动条余量。</summary>
        private const double FixedColumnsWidth = 52 + 76 + 78 + 100 + 76 + 106 + 100;

        /// <summary>全量批注（未筛选）。每次刷新整体重建，列表项是不可变快照。</summary>
        private List<AnnotationService.AnnotationInfo> _all = new List<AnnotationService.AnnotationInfo>();

        /// <summary>列表的数据视图。筛选只改它的 Filter 再 Refresh，<b>不再整体替换 ItemsSource</b>：
        /// 换数据源会把选中行、滚动位置、单元格生成一次性全推倒，是"点筛选就出错"的高风险来源。</summary>
        private ICollectionView _view;

        /// <summary>当前状态筛选；空串 = 全部。</summary>
        private string _statusFilter = "";

        /// <summary>当前搜索关键字（空 = 不过滤）。</summary>
        private string _keyword = "";

        /// <summary>"意见摘要"这一列（唯一弹性列），窗口尺寸变化时重算宽度。</summary>
        private GridViewColumn _summaryColumn;

        /// <summary>"把窗口交给 CAD（最小化）→ 用户按 Esc 回来"这一轮是否还没结束。
        /// <para><b>只看这个标志，不再看 <c>WindowState</c></b>：以前要求"标志为真 且 当前确实最小化"才还原，
        /// 只要中间有任何一步让窗口状态对不上（用户从任务栏点回来、最小化被系统动作抢先），
        /// Esc 就会走进"静默返回分支"——什么都不做、只把钩子摘掉，现场就是
        /// <b>"按了 Esc 却回不到批注列表"</b>。现在只要这一轮没结束，Esc 一律还原（还原是幂等的）。</para></summary>
        private bool _locating;

        /// <summary>定位之后等用户按 Esc 就还原窗口的线程级键盘钩子（见 InstallEscReturnHook）。</summary>
        private IntPtr _escReturnHook = IntPtr.Zero;
        private EscReturnProc _escReturnProc;
        /// <summary>Esc 已经收到、正在（排队）还原窗口；防止连按 Esc 重复排队。</summary>
        private bool _escReturnPending;

        private AnnotationListPanel()
        {
            WpfGuard.Install();
            WpfHost.Ensure();
            InitializeComponent();
            CadDialog.RegisterFloating(this);

            Loaded += OnPanelLoaded;
            // 本窗口不是置顶窗口（见类注释）：被激活时自己抬一次到 CAD 之上，兜住"从最小化恢复时
            // Windows 偶尔把窗口留在属主后面"这种情况。
            Activated += (s, e) => SafeRun("Activated", RaiseAboveCad);
            SizeChanged += (_, __) => SafeRun("Resize", ResizeSummaryColumn);
            PreviewKeyDown += (s, e) => SafeRun("KeyDown", () => OnPreviewKeyDown(s, e));
            Closing += (_, __) =>
            {
                RemoveEscReturnHook();
                _locating = false;
                _instance = null;
            };
        }

        /// <summary>显示或激活批注列表（单例），并刷新数据。</summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded)
            {
                _instance.RestoreFromCad();
                _instance.LoadAnnotations();
                return;
            }
            _instance = new AnnotationListPanel();
            // 非模态：走 CadDialog 才能挂到 CAD 主窗口上，并按 XAML 里的 CenterScreen 摆到 CAD 所在显示器中间。
            CadDialog.ShowModeless(_instance);
            _instance.LoadAnnotations();
        }

        /// <summary>若列表已打开则刷新数据。</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && _instance.IsLoaded)
                _instance.SafeRun("Refresh", _instance.LoadAnnotations);
        }

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            SafeRun("Loaded", () =>
            {
                // 小屏幕（1366×768）上 700 高的窗口会压到任务栏，按工作区夹一次。
                var work = SystemParameters.WorkArea;
                MaxWidth = Math.Max(820, work.Width - 40);
                MaxHeight = Math.Max(540, work.Height - 40);
                if (Width > MaxWidth) Width = MaxWidth;
                if (Height > MaxHeight) Height = MaxHeight;

                _summaryColumn = ResolveSummaryColumn();
                ResizeSummaryColumn();
                LoadAnnotations();
            });
        }

        private GridViewColumn ResolveSummaryColumn()
        {
            var view = AnnotationList.View as GridView;
            if (view == null || view.Columns.Count < 5) return null;
            // 顺序：序号 / 编号 / 专业 / 图号 / 意见摘要 ……（见 XAML）
            return view.Columns[4];
        }

        private void ResizeSummaryColumn()
        {
            if (_summaryColumn == null) _summaryColumn = ResolveSummaryColumn();
            if (_summaryColumn == null) return;
            var available = AnnotationList.ActualWidth;
            if (available <= 0) available = ActualWidth - 32;
            _summaryColumn.Width = Math.Max(200, available - FixedColumnsWidth - SystemParameters.VerticalScrollBarWidth - 6);
        }

        // ===================== 数据 =====================

        private void LoadAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                _all = new List<AnnotationService.AnnotationInfo>();
                BindView();
                DetailContent.Text = "当前没有打开的图纸。";
                return;
            }
            _all = AnnotationService.GetAllAnnotations(doc);
            BindView();
        }

        /// <summary>把全量数据挂到表格上（只在刷新/首次加载时做，筛选不碰它）。</summary>
        private void BindView()
        {
            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = FilterPredicate;
            AnnotationList.ItemsSource = _view;
            RefreshView();
        }

        private bool FilterPredicate(object item)
        {
            var info = item as AnnotationService.AnnotationInfo;
            if (info == null) return false;
            if (!string.IsNullOrEmpty(_statusFilter) && !info.IsStatus(_statusFilter)) return false;
            var keyword = (_keyword ?? "").Trim();
            if (keyword.Length > 0 && !Matches(info, keyword)) return false;
            return true;
        }

        /// <summary>按当前状态筛选 + 关键字重排列表，并尽量保住原来的选中行。</summary>
        private void RefreshView()
        {
            if (_view == null) return;
            var keepId = SelectedInfo?.Id;

            _view.Filter = FilterPredicate;
            _view.Refresh();

            var visible = _view.Cast<AnnotationService.AnnotationInfo>().ToList();

            AnnotationService.AnnotationInfo keep = null;
            if (!string.IsNullOrEmpty(keepId)) keep = visible.FirstOrDefault(item => item.Id == keepId);
            if (keep == null && visible.Count > 0) keep = visible[0];
            AnnotationList.SelectedItem = keep;

            UpdateCounters();
            if (keep == null) ShowDetail(null, visible.Count == 0 && _all.Count > 0);
        }

        private static bool Matches(AnnotationService.AnnotationInfo info, string keyword)
        {
            return Contains(info.Number, keyword)
                || Contains(info.DrawingNo, keyword)
                || Contains(info.Discipline, keyword)
                || Contains(info.Content, keyword)
                || Contains(info.Author, keyword)
                || Contains(info.Layer, keyword)
                || Contains(info.Status, keyword)
                || Contains(info.Role, keyword);
        }

        private static bool Contains(string source, string keyword)
        {
            return !string.IsNullOrEmpty(source)
                && source.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void UpdateCounters()
        {
            var total = _all.Count;
            var done = _all.Count(item => string.Equals(item.Status, "已完成", StringComparison.Ordinal));
            var replied = _all.Count(item => string.Equals(item.Status, "已回复", StringComparison.Ordinal));
            var pending = total - done - replied;
            if (pending < 0) pending = 0;

            CountAll.Text = total.ToString();
            CountPending.Text = pending.ToString();
            CountReplied.Text = replied.ToString();
            CountDone.Text = done.ToString();
            SubTitle.Text = total == 0
                ? "当前图纸还没有批注"
                : "共 " + total + " 条批注 · 选中一行可在图纸中定位";
            FooterStats.Text = "共 " + total + " 条   ·   待处理 " + pending + " 条   ·   已回复 " + replied + " 条   ·   已完成 " + done + " 条";
        }

        // ===================== 详情 =====================

        private void ShowDetail(AnnotationService.AnnotationInfo info, bool emptyBecauseFiltered)
        {
            if (info == null)
            {
                DetailHead.Text = "";
                DetailContent.Text = emptyBecauseFiltered ? "当前筛选条件下没有批注。" : "在列表里选一条批注查看全文。";
                DetailElements.Text = "";
                DetailNumber.Text = DetailAuthor.Text = DetailDrawingNo.Text = "—";
                DetailLayer.Text = DetailLayout.Text = DetailFingerprint.Text = "—";
                return;
            }

            DetailHead.Text = "第 " + info.Number + " 条";
            var content = (info.Content ?? "").Replace("\\P", Environment.NewLine).Trim();
            DetailContent.Text = content.Length == 0 ? "（这条批注没有填写内容）" : content;
            DetailElements.Text = "组成：" + info.ElementText;

            DetailNumber.Text = string.IsNullOrEmpty(info.Number) ? "—" : info.Number;
            DetailAuthor.Text = BuildAuthorText(info);
            DetailDrawingNo.Text = info.DrawingNoDisplay;
            DetailLayer.Text = string.IsNullOrEmpty(info.Layer) ? "—" : info.Layer;
            DetailLayout.Text = BuildLayoutText(info);
            DetailFingerprint.Text = string.IsNullOrEmpty(info.Fingerprint) ? "—" : info.Fingerprint;
        }

        private static string BuildAuthorText(AnnotationService.AnnotationInfo info)
        {
            var author = string.IsNullOrEmpty(info.Author) ? "—" : info.Author;
            if (string.IsNullOrEmpty(info.Role) || info.Role == "批注人") return author;
            return author + "（" + info.Role + "）";
        }

        private static string BuildLayoutText(AnnotationService.AnnotationInfo info)
        {
            var layout = string.IsNullOrEmpty(info.Layout) ? "—" : info.Layout;
            var date = string.IsNullOrEmpty(info.Date) ? "" : "  ·  " + info.Date;
            return layout + date;
        }

        // ===================== 交互 =====================

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SafeRun("SelectionChanged", () => ShowDetail(SelectedInfo, false));
        }

        private void FilterChip_Checked(object sender, RoutedEventArgs e)
        {
            SafeRun("FilterChip", () =>
            {
                var chip = sender as RadioButton;
                // 初始化阶段控件还没建全，Tag 可能读不到：此时按"全部"处理，真正加载后 RefreshView 会再算一遍。
                _statusFilter = chip == null ? "" : (chip.Tag as string ?? "");
                if (!IsLoaded) return;
                RefreshView();
            });
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            SafeRun("Search", () =>
            {
                _keyword = SearchBox.Text ?? "";
                if (!IsLoaded) return;
                RefreshView();
            });
        }

        /// <summary>双击行：缩放到对应批注并高亮选中。</summary>
        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            SafeRun("DoubleClick", () =>
            {
                var info = ResolveClickedInfo(e.OriginalSource as DependencyObject)
                    ?? SelectedInfo;
                if (info == null) return;
                Locate(info);
            });
        }

        /// <summary>从双击命中的可视化树向上找到对应的列表项数据。</summary>
        private static AnnotationService.AnnotationInfo ResolveClickedInfo(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ListViewItem item) return item.DataContext as AnnotationService.AnnotationInfo;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private void Locate_Click(object sender, RoutedEventArgs e) => SafeRun("Locate", () => Locate(SelectedInfo, "按钮"));

        private void Locate(AnnotationService.AnnotationInfo info, string source = "双击")
        {
            if (info == null) { Notify("请先在列表里选中一条批注。"); return; }
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }
            if (info.FirstEntityId.IsNull || !info.FirstEntityId.IsValid || info.FirstEntityId.IsErased)
            {
                Notify("定位失败：批注实体已不存在，请刷新列表。");
                return;
            }

            // 顺序很重要（1.0.11 用户反馈"按了 Esc 回不来"后调整）：
            //   ① 先装 Esc 钩子 → ② 再最小化 → ③ 最后（延时）缩放。
            // 以前是"先最小化、装钩子、再缩放"，中间任何一步出错或被打断都会留下
            // "窗口已经收起、但没有任何机制把它叫回来"的状态。现在钩子一定先就位。
            _locating = true;
            InstallEscReturnHook();
            PluginLog.Info("AnnotationListPanel.Locate",
                "来源=" + source + " 编号=" + info.Number + " 窗口状态=" + WindowState + " 钩子=" + (_escReturnHook != IntPtr.Zero ? "OK" : "失败"));
            if (_escReturnHook == IntPtr.Zero)
            {
                // 钩子装不上就明说：这次定位后只能从任务栏 / X_PZ_LIST 找回列表，别让用户以为"按 Esc 没反应"。
                try { StatusText.Text = "定位后列表会最小化，可从任务栏或 X_PZ_LIST 找回（本次 Esc 返回不可用）。"; } catch { }
            }

            // 把列表收起来再定位：不收起来会一直挡在图纸前面，
            // 用户看到的就是"点了定位，可屏幕全被列表占着、什么也看不见"。
            MinimizeForCad(() =>
            {
                if (!AnnotationService.ZoomToAnnotation(doc, info.FirstEntityId))
                {
                    PluginLog.Warning("AnnotationListPanel.Locate", "缩放失败：" + info.Number);
                    RestoreFromCad();
                    Notify("定位批注 " + info.Number + " 失败。");
                }
            });
        }

        private void Mark_Click(object sender, RoutedEventArgs e)
        {
            SafeRun("Mark", () =>
            {
                if (SelectedInfo == null) { Notify("请先在列表里选中一条批注。"); return; }
                if (!OpenMenu(sender)) Notify("状态菜单没有打开，请用鼠标右键点击这一行。");
            });
        }

        private void MarkPending_Click(object sender, RoutedEventArgs e) => SafeRun("MarkPending", () => ApplyStatus("待处理"));
        private void MarkReplied_Click(object sender, RoutedEventArgs e) => SafeRun("MarkReplied", () => ApplyStatus("已回复"));
        private void MarkDone_Click(object sender, RoutedEventArgs e) => SafeRun("MarkDone", () => ApplyStatus("已完成"));

        private void ApplyStatus(string status)
        {
            var info = SelectedInfo;
            if (info == null) { Notify("请先在列表里选中一条批注。"); return; }
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }
            if (string.Equals(info.Status, status, StringComparison.Ordinal)) return;

            if (info.FirstEntityId.IsNull || !info.FirstEntityId.IsValid || info.FirstEntityId.IsErased)
            {
                Notify("改状态失败：批注实体已不存在，请按 F5 刷新列表。");
                return;
            }

            var reason = AnnotationService.SetStatus(doc, info.FirstEntityId, status, out var changed);
            if (!changed)
            {
                Notify("改状态失败" + (string.IsNullOrEmpty(reason) ? "。" : "：" + reason) + "\n可按 F5 刷新列表后重试。");
                return;
            }

            try { doc.Editor.WriteMessage("\nX-批注 " + info.Number + " 已标记为「" + status + "」。"); }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.WriteMessage", ex.Message); }

            LoadAnnotations();
            StatusText.Text = "已把 " + info.Number + " 标记为「" + status + "」";
        }

        private void NewAnnotation_Click(object sender, RoutedEventArgs e) => SafeRun("NewAnnotation", () =>
        {
            // 同上：列表挡在图纸前面时连"指定第一个角点"都点不到，先收起来再发绘制命令。
            MinimizeForCad(() => RunCommandDirect("X_PZ_DRAW"));
        });

        private void Export_Click(object sender, RoutedEventArgs e) => SafeRun("Export", () =>
        {
            if (OpenMenu(sender)) return;
            RunCommand("X_PZ_EXPORT");
        });

        private void ExportCsv_Click(object sender, RoutedEventArgs e) => SafeRun("ExportCsv", () => RunCommand("X_PZ_EXPORT"));

        private void ExportWord_Click(object sender, RoutedEventArgs e) => SafeRun("ExportWord", () => RunCommand("X_PZ_WORD"));

        private void Refresh_Click(object sender, RoutedEventArgs e) => SafeRun("Reload", LoadAnnotations);

        private void Close_Click(object sender, RoutedEventArgs e) => SafeRun("Close", Close);

        private void Delete_Click(object sender, RoutedEventArgs e) => SafeRun("Delete", () =>
        {
            var info = SelectedInfo;
            if (info == null) { Notify("请先在列表里选中一条批注。"); return; }
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }

            var result = MessageBox.Show(this, "确定要删除批注 " + info.Number + " 吗？", "X-批注",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (!AnnotationService.Delete(doc, info.FirstEntityId))
                    Notify("删除失败：批注实体已不存在。");
                else
                    doc.Editor.WriteMessage("\nX-批注 " + info.Number + " 已删除。");
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationListPanel.Delete", ex);
                Notify("删除批注失败: " + ex.Message);
            }
            LoadAnnotations();
        });

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5) { LoadAnnotations(); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                Locate(SelectedInfo);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && !SearchBox.IsKeyboardFocusWithin)
            {
                // 定位过程中（窗口刚还原、Esc 在窗口上抬起之类）不要顺手把列表关掉：
                // 用户想的是"回到列表"，关掉就变成"列表没了"，只能重新敲命令。
                if (_locating) ReturnFromLocate();
                else Close();
                e.Handled = true;
            }
        }

        private AnnotationService.AnnotationInfo SelectedInfo
        {
            get { return AnnotationList.SelectedItem as AnnotationService.AnnotationInfo; }
        }

        /// <summary>给按钮弹出它自己的下拉菜单；没有菜单时返回 false。</summary>
        private static bool OpenMenu(object sender)
        {
            var button = sender as Button;
            if (button == null || button.ContextMenu == null) return false;
            button.ContextMenu.PlacementTarget = button;
            // 菜单朝上弹：「标记状态」按钮在窗口最底行，往下弹会跑到屏幕外，
            // 以前点了没反应就是这个原因（菜单其实开了，只是看不见）。
            button.ContextMenu.Placement = PlacementMode.Top;
            button.ContextMenu.HorizontalOffset = 0;
            button.ContextMenu.VerticalOffset = -2;
            button.ContextMenu.IsOpen = true;
            return true;
        }

        // ===================== 与 CAD 的窗口配合 =====================

        // 层级规则（1.0.11 用户反馈后定稿）：
        //   · 本窗口**不是 Topmost 窗口**。以前为了"压在 CAD 之上"设了置顶，结果用户切到
        //     Word / 微信 / 资源管理器，屏幕上也一直挂着这个列表——现场就是
        //     "启动了批注列表后，别的软件界面都会被它盖住"。
        //   · 现在靠"从属窗口"关系：窗口的 Win32 Owner = CAD 主框架（CadDialog.ShowModeless →
        //     AttachToCadMainWindow），从属窗口天然压在属主之上、点 CAD 时跟着抬起来，
        //     但**不会**盖住别的程序（切走就是切走）。导出时弹的另存为对话框也因此排在列表之上，
        //     不需要再"临时摘置顶"（CadDialog.SuspendTopmost 对非置顶窗口是空操作）。
        //   · 要让位给图纸时（定位 / 新建批注）仍然最小化，Esc 或重新打开时还原。

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>把窗口抬到 CAD 之上（不抢焦点）。
        /// <para>从属窗口本来就压在属主之上，这里只是兜底：从最小化 / 隐藏恢复时，
        /// Windows 偶尔会把窗口留在属主后面，用户就会觉得"列表回来了却看不见"。</para></summary>
        private void RaiseAboveCad()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.RaiseAboveCad", ex.Message); }
        }

        /// <summary>把列表最小化，让出图纸；<paramref name="after"/> 在窗口状态落定后再执行。</summary>
        private void MinimizeForCad(Action after)
        {
            var scheduled = false;
            try
            {
                if (WindowState != WindowState.Minimized)
                {
                    WindowState = WindowState.Minimized;
                    // 窗口最小化后 Windows 要让出前台，CAD 才会真正露出来；
                    // 等约一拍（120ms）再发命令/做缩放，动作才不会和动画打架。
                    scheduled = after != null;
                }
            }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.MinimizeForCad", ex.Message); }

            if (after == null) return;
            if (!scheduled) { SafeRun("AfterMinimize", after); return; }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                SafeRun("AfterMinimize", after);
            };
            timer.Start();
        }

        /// <summary>把列表还原出来（命令再次打开列表、定位失败、按 Esc 返回都用它）。
        /// <para><b>幂等</b>：已经正常显示时再调一次只是重新抬到 CAD 之上，不会有副作用——
        /// 这正是"按了 Esc 一定回得来"的保证。</para></summary>
        private void RestoreFromCad()
        {
            try
            {
                _locating = false;
                RemoveEscReturnHook();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                if (!IsVisible) Show();
                RaiseAboveCad();
                Activate();
            }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.RestoreFromCad", ex.Message); }
        }

        // ===================== 定位后按 Esc 回到列表 =====================

        // 需求（2026-11-10）："双击意见或定位到图纸后，Esc 退出定位要返回到批注列表界面"。
        // 定位时列表被最小化、焦点回到 CAD，本窗口收不到任何键盘消息，所以装一个**线程级**键盘钩子：
        //   WH_KEYBOARD + 本线程 ID —— 只收本线程（＝ CAD 界面线程）的键盘输入，不需要 DLL、
        //   不跨进程；回调里一律 CallNextHookEx，**绝不吞键**，CAD 自己的 Esc 语义原样保留。
        // （与 Views/AnnotationPanel.xaml.cs 的 Esc 钩子同一套做法，已在 CAD 2014 上跑通。）

        private delegate IntPtr EscReturnProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD = 2;
        private const int VK_ESCAPE = 0x1B;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, EscReturnProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>装上"Esc 还原列表"钩子（幂等）。失败只记日志：列表仍可从任务栏点回来。</summary>
        private void InstallEscReturnHook()
        {
            if (_escReturnHook != IntPtr.Zero) return;
            try
            {
                _escReturnProc = OnEscWhileLocating;
                _escReturnHook = SetWindowsHookEx(WH_KEYBOARD, _escReturnProc, IntPtr.Zero, GetCurrentThreadId());
                if (_escReturnHook == IntPtr.Zero)
                    PluginLog.Warning("AnnotationListPanel.EscHook", "安装键盘钩子失败：定位后需从任务栏找回列表。");
            }
            catch (Exception ex)
            {
                _escReturnHook = IntPtr.Zero;
                _escReturnProc = null;
                PluginLog.Warning("AnnotationListPanel.EscHook", ex.Message);
            }
        }

        private void RemoveEscReturnHook()
        {
            _escReturnPending = false;
            if (_escReturnHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_escReturnHook); }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.EscHook(remove)", ex.Message); }
            _escReturnHook = IntPtr.Zero;
            _escReturnProc = null;
        }

        /// <summary>钩子回调：定位后按 Esc（键按下那一下）就把列表还原。
        /// <para>WH_KEYBOARD 的 <c>wParam</c> 是虚拟键码，<c>lParam</c> 的 bit31 = 1 表示"抬键"。</para>
        /// <para>整个回调必须吞掉所有异常：异常越过托管/原生边界会直接崩掉 CAD。</para></summary>
        private IntPtr OnEscWhileLocating(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var isKeyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                if (nCode >= 0 && isKeyDown && wParam.ToInt64() == VK_ESCAPE && !_escReturnPending)
                {
                    _escReturnPending = true;
                    // 不能在钩子回调里直接动窗口（钩子栈很浅，且此时 CAD 正在处理这次按键）。
                    Dispatcher.BeginInvoke(new Action(() => SafeRun("EscReturn", ReturnFromLocate)), DispatcherPriority.Normal);
                }
            }
            catch (Exception ex)
            {
                try { PluginLog.Warning("AnnotationListPanel.EscHook(tick)", ex.Message); } catch { }
            }

            // 永不吞键：CAD 自己的 Esc（结束缩放、取消命令、清除选择）必须原样收到。
            return CallNextHookEx(_escReturnHook, nCode, wParam, lParam);
        }

        /// <summary>用户按 Esc 结束定位：把列表摆回来。
        /// <para><b>只要这一轮定位还没结束（<see cref="_locating"/>）就还原</b>，不再要求"当前确实是最小化"：
        /// 以前那个双重条件一旦对不上（用户从任务栏点回来过、最小化被系统动作抢先），
        /// Esc 就走进"静默摘钩子"的分支，什么都不做——这正是"按了 Esc 回不到列表"的来源。
        /// 还原本身是幂等的，多摆一次没有副作用。</para></summary>
        private void ReturnFromLocate()
        {
            var locating = _locating;
            if (!locating)
            {
                // 已经还原过了（例如用户先点回列表、再按 Esc）：只把钩子摘掉。
                RemoveEscReturnHook();
                PluginLog.Info("AnnotationListPanel.EscHook", "收到 Esc，但这一轮定位已结束，不用再还原。");
                return;
            }
            PluginLog.Info("AnnotationListPanel.EscHook", "定位后收到 Esc，还原批注列表（窗口状态=" + WindowState + "）。");
            RestoreFromCad();
        }

        /// <summary>
        /// 把命令交给 CAD 执行（不阻塞本窗口）。
        /// <para><b>不再需要"摘置顶"</b>：列表本身不是 Topmost 窗口（见「与 CAD 的窗口配合」），
        /// 命令里弹的另存为/打开对话框、Word 之类的外部程序都自然排在它之上，
        /// 以前那套"发命令时摘置顶、等用户点回来再恢复"的逻辑已随置顶一起去掉。</para>
        /// </summary>
        private void RunCommand(string command)
        {
            RunCommandDirect(command);
        }

        /// <summary>直接发命令；发不出去时在界面上说清楚，不再"点了没反应"。</summary>
        private bool RunCommandDirect(string command)
        {
            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) { Notify("当前没有打开的图纸。"); return false; }
                doc.SendStringToExecute(command + " ", true, false, false);
                PluginLog.Info("AnnotationListPanel.RunCommand", "已发送命令 " + command);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationListPanel.RunCommand", ex);
                Notify("执行 " + command + " 失败: " + ex.Message);
                return false;
            }
        }

        private void Notify(string message)
        {
            try { MessageBox.Show(this, message, "批注列表", MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.Notify", ex.Message); }
        }

        // ===================== 异常防护 =====================

        /// <summary>跑一段界面逻辑；任何异常都记日志并吞掉，绝不让它冒到 CAD 里变成"致命错误"。</summary>
        private void SafeRun(string area, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationListPanel." + area, ex);
                try
                {
                    if (StatusText != null) StatusText.Text = "操作失败（已记录日志）：" + ex.Message;
                    var doc = CadApplication.DocumentManager.MdiActiveDocument;
                    if (doc != null) doc.Editor.WriteMessage("\nX-批注列表出错（已记录日志）：" + ex.Message);
                }
                catch { }
            }
        }
    }

    /// <summary>WPF 界面线程的兜底：把漏出来的异常记日志并吃掉，避免宿主 CAD 直接致命错误退出。
    /// <para><b>为什么必须有</b>：AutoCAD 2014 进程里<b>没有</b> WPF <c>Application</c> 对象
    /// （启动日志里的"宿主进程无 WPF Application"就是这个意思），于是 WPF 没有可挂
    /// <c>DispatcherUnhandledException</c> 的地方——任何 UI 线程异常都会按"未处理"直接终止进程，
    /// CAD 那边的表现就是一条"致命错误"。接住它之后最坏只是某个按钮失效，进程和图纸都还在。</para></summary>
    internal static class WpfGuard
    {
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
                {
                    PluginLog.Error("WPF.UnhandledException", e.Exception);
                    e.Handled = true;
                    try
                    {
                        var doc = CadApplication.DocumentManager.MdiActiveDocument;
                        if (doc != null)
                            doc.Editor.WriteMessage("\nX-批注界面出错（已记录日志）：" + e.Exception.Message);
                    }
                    catch (Exception) { }
                };
            }
            catch (Exception ex) { PluginLog.Warning("WpfGuard.Install", ex.Message); }
        }
    }
}
