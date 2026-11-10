using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    /// <summary>批注清单：表格 + 状态筛选 + 搜索 + 详情，双击行在图纸中定位。
    /// <para>数据源统一走 <see cref="AnnotationService.GetAllAnnotations"/>（与 CSV/Word 导出同源），
    /// 本窗口只做筛选、展示与转发命令，不改图纸内容。</para></summary>
    internal partial class AnnotationListPanel : Window
    {
        private static AnnotationListPanel _instance;

        /// <summary>表格里除"意见摘要"之外的固定列宽之和（见 XAML 顶部注释）。
        /// 摘要列宽 = 列表实际宽度 - 这个值 - 滚动条余量。</summary>
        private const double FixedColumnsWidth = 52 + 76 + 78 + 100 + 76 + 106 + 88;

        /// <summary>全量批注（未筛选）。每次刷新整体重建，列表项是不可变快照。</summary>
        private List<AnnotationService.AnnotationInfo> _all = new List<AnnotationService.AnnotationInfo>();

        /// <summary>当前状态筛选；空串 = 全部。</summary>
        private string _statusFilter = "";

        /// <summary>当前搜索关键字（空 = 不过滤）。</summary>
        private string _keyword = "";

        /// <summary>"意见摘要"这一列（唯一弹性列），窗口尺寸变化时重算宽度。</summary>
        private GridViewColumn _summaryColumn;

        private AnnotationListPanel()
        {
            WpfHost.Ensure();
            InitializeComponent();
            CadDialog.RegisterFloating(this);

            Loaded += OnPanelLoaded;
            SizeChanged += (_, __) => ResizeSummaryColumn();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += (_, __) => _instance = null;
        }

        /// <summary>显示或激活批注清单（单例），并刷新数据。</summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded)
            {
                _instance.Activate();
                _instance.LoadAnnotations();
                return;
            }
            _instance = new AnnotationListPanel();
            // 非模态：走 CadDialog 才能挂到 CAD 主窗口上，并按 XAML 里的 CenterScreen 摆到 CAD 所在显示器中间。
            CadDialog.ShowModeless(_instance);
            _instance.LoadAnnotations();
        }

        /// <summary>若清单已打开则刷新数据。</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && _instance.IsLoaded) _instance.LoadAnnotations();
        }

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 小屏幕（1366×768）上 700 高的窗口会压到任务栏，按工作区夹一次。
                var work = SystemParameters.WorkArea;
                MaxWidth = Math.Max(820, work.Width - 40);
                MaxHeight = Math.Max(540, work.Height - 40);
                if (Width > MaxWidth) Width = MaxWidth;
                if (Height > MaxHeight) Height = MaxHeight;
            }
            catch (Exception ex) { PluginLog.Warning("AnnotationListPanel.OnPanelLoaded", ex.Message); }

            _summaryColumn = ResolveSummaryColumn();
            ResizeSummaryColumn();
            LoadAnnotations();
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
                ApplyFilter();
                DetailContent.Text = "当前没有打开的图纸。";
                return;
            }
            _all = AnnotationService.GetAllAnnotations(doc);
            ApplyFilter();
        }

        /// <summary>按状态筛选 + 关键字过滤后刷新表格，并尽量保住原来的选中行。</summary>
        private void ApplyFilter()
        {
            var keepId = (AnnotationList.SelectedItem as AnnotationService.AnnotationInfo)?.Id;

            IEnumerable<AnnotationService.AnnotationInfo> query = _all;
            if (!string.IsNullOrEmpty(_statusFilter)) query = query.Where(item => item.IsStatus(_statusFilter));
            var keyword = (_keyword ?? "").Trim();
            if (keyword.Length > 0) query = query.Where(item => Matches(item, keyword));

            var list = query.ToList();
            AnnotationList.ItemsSource = list;

            AnnotationService.AnnotationInfo keep = null;
            if (!string.IsNullOrEmpty(keepId)) keep = list.FirstOrDefault(item => item.Id == keepId);
            if (keep == null && list.Count > 0) keep = list[0];
            AnnotationList.SelectedItem = keep;

            UpdateCounters();
            if (keep == null) ShowDetail(null, list.Count == 0 && _all.Count > 0);
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
            ShowDetail(AnnotationList.SelectedItem as AnnotationService.AnnotationInfo, false);
        }

        private void FilterChip_Checked(object sender, RoutedEventArgs e)
        {
            var chip = sender as RadioButton;
            // 初始化阶段控件还没建全，Tag 可能读不到：此时按"全部"处理，真正加载后 ApplyFilter 会再算一遍。
            _statusFilter = chip == null ? "" : (chip.Tag as string ?? "");
            if (!IsLoaded) return;
            ApplyFilter();
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _keyword = SearchBox.Text ?? "";
            if (!IsLoaded) return;
            ApplyFilter();
        }

        /// <summary>双击行：缩放到对应批注并高亮选中。</summary>
        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var info = ResolveClickedInfo(e.OriginalSource as DependencyObject)
                ?? AnnotationList.SelectedItem as AnnotationService.AnnotationInfo;
            if (info == null) return;
            Locate(info);
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

        private void Locate_Click(object sender, RoutedEventArgs e) => Locate(SelectedInfo);

        private void Locate(AnnotationService.AnnotationInfo info)
        {
            if (info == null) { Notify("请先在列表里选中一条批注。"); return; }
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }
            if (info.FirstEntityId.IsNull || !info.FirstEntityId.IsValid || info.FirstEntityId.IsErased)
            {
                Notify("定位失败：批注实体已不存在，请刷新列表。");
                return;
            }
            if (!AnnotationService.ZoomToAnnotation(doc, info.FirstEntityId))
            {
                Notify("定位批注 " + info.Number + " 失败。");
            }
        }

        private void Mark_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedInfo == null) { Notify("请先在列表里选中一条批注。"); return; }
            OpenMenu(sender);
        }

        private void MarkPending_Click(object sender, RoutedEventArgs e) => ApplyStatus("待处理");
        private void MarkReplied_Click(object sender, RoutedEventArgs e) => ApplyStatus("已回复");
        private void MarkDone_Click(object sender, RoutedEventArgs e) => ApplyStatus("已完成");

        private void ApplyStatus(string status)
        {
            var info = SelectedInfo;
            if (info == null) { Notify("请先在列表里选中一条批注。"); return; }
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }
            if (string.Equals(info.Status, status, StringComparison.Ordinal)) return;

            var changed = AnnotationService.SetStatus(doc, info.FirstEntityId, status);
            if (!changed)
            {
                Notify("改状态失败：批注实体可能已不存在，请刷新列表。");
                return;
            }
            doc.Editor.WriteMessage("\nX-批注 " + info.Number + " 已标记为「" + status + "」。");
            LoadAnnotations();
        }

        private void NewAnnotation_Click(object sender, RoutedEventArgs e) => RunCommand("X_PZ_DRAW");

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (OpenMenu(sender)) return;
            RunCommand("X_PZ_EXPORT");
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e) => RunCommand("X_PZ_EXPORT");

        private void ExportWord_Click(object sender, RoutedEventArgs e) => RunCommand("X_PZ_WORD");

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadAnnotations();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Delete_Click(object sender, RoutedEventArgs e)
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
        }

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
                Close();
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
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
            return true;
        }

        /// <summary>
        /// 把命令交给 CAD 执行（不阻塞本窗口）。
        /// <para>发命令前先摘掉置顶：CAD 的"另存为/打开"这类原生对话框不认我们的 Owner，
        /// 本窗口置顶时会把它们压在后面（用户会以为按钮没反应）。1.2 秒后自动恢复置顶。</para>
        /// </summary>
        private void RunCommand(string command)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Notify("当前没有打开的图纸。"); return; }

            var wasTopmost = Topmost;
            Topmost = false;
            try { doc.SendStringToExecute(command + " ", true, false, false); }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationListPanel.RunCommand", ex);
                Notify("执行 " + command + " 失败: " + ex.Message);
                if (wasTopmost) Topmost = true;
                return;
            }

            if (!wasTopmost) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                try { if (IsLoaded) Topmost = true; } catch { }
            };
            timer.Start();
        }

        private void Notify(string message)
        {
            MessageBox.Show(this, message, "批注清单", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
