using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace XAnnotation.Views
{
    internal partial class SettingsWindow : Window
    {
        private readonly AnnotationSettings _s;
        /// <summary>程序性赋值比例（初始化）期间为 true，避免被误当成"用户改了比例"。</summary>
        private bool _scaleChanging;
        /// <summary>用户在本次打开设置窗口期间是否真的动过比例下拉。</summary>
        private bool _scaleTouchedByUser;
        public AnnotationSettings Value => _s;

        /// <summary>当前打开着的设置窗口（单例）。非模态，与批注面板各自独立。</summary>
        private static SettingsWindow _instance;
        /// <summary>是否已经有一个设置窗口开着。</summary>
        internal static bool IsOpen => _instance != null && _instance.IsLoaded;

        /// <summary>设置窗口刚保存过（内容已写进设置对象并落盘）。面板据此刷新自己的控件。</summary>
        internal event Action Saved;

        /// <summary>当前应当共用的设置对象：设置窗口开着就沿用<b>它那一份</b>（同一个对象），
        /// 否则从设置文件读一份新的。
        /// <para>必须是同一个对象：两边各持一份的话，谁的"保存/落盘"都会把对方刚改的值覆盖回去
        /// （例如在设置里把云线外形改成椭圆，随后从面板开始画一轮，面板落盘时又把它写回旧值）。</para></summary>
        internal static AnnotationSettings SharedSettings()
            => IsOpen ? _instance.Value : SettingsStore.Load();

        /// <summary>显示或激活设置窗口（单例、非模态）。
        /// <para><b>非模态</b>是刻意的：模态（原先走的 <c>ShowModalWindow</c>）会禁用其它顶层窗口，
        /// 于是"设置窗口一开，批注面板既点不动、也不能单独关、必须先退出设置窗口"。</para>
        /// <para>anchor 给出时，新窗口会摆在它的右侧（面板固定在屏幕左上角，这样两个窗口同时可见）。</para></summary>
        internal static SettingsWindow ShowOrActivate(AnnotationSettings settings, IEnumerable<string> textStyles = null, Window anchor = null)
        {
            var existing = _instance;
            if (existing != null && existing.IsLoaded)
            {
                if (ReferenceEquals(existing.Value, settings))
                {
                    existing.Activate();
                    WpfWindowUtil.BringToTop(existing);
                    return existing;
                }

                // 开着的那一个绑的是别的设置对象（面板关掉又重开后换了新对象之类）：关掉重开，
                // 免得两边各改各的对象、"保存"后面板看到的还是旧值。
                PluginLog.Warning("SettingsWindow.ShowOrActivate", "已打开的设置窗口绑的是另一份设置对象，先关闭它以重建。");
                try { existing.Close(); }
                catch (Exception ex) { PluginLog.Warning("SettingsWindow.ShowOrActivate.Close", ex.Message); }
            }

            var window = new SettingsWindow(settings, textStyles);
            if (anchor != null) PlaceBeside(window, anchor);
            window.Closed += (_, __) => { if (ReferenceEquals(_instance, window)) _instance = null; };
            _instance = window;
            CadDialog.ShowModeless(window);
            return window;
        }

        /// <summary>把窗口摆到参照窗口右侧（越界时贴到可用区域右边缘）。</summary>
        private static void PlaceBeside(Window window, Window anchor)
        {
            try
            {
                var area = SystemParameters.WorkArea;
                var width = window.Width > 0 ? window.Width : 640;
                var height = window.Height > 0 ? window.Height : 540;
                var left = anchor.Left + anchor.ActualWidth + 12;
                if (left + width > area.Right - 8) left = Math.Max(area.Left + 8, area.Right - 8 - width);
                var top = Math.Min(Math.Max(area.Top + 8, anchor.Top), Math.Max(area.Top + 8, area.Bottom - 8 - height));
                // Manual 必须在 Show 之前设：窗口默认会被挂到 CAD 主窗口后"居中于主窗口"。
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left;
                window.Top = top;
            }
            catch (Exception ex) { PluginLog.Warning("SettingsWindow.PlaceBeside", ex.Message); }
        }

        /// <summary>构造设置窗口。textStyles 为当前 DWG 中可用的文字样式名列表。</summary>
        public SettingsWindow(AnnotationSettings value, IEnumerable<string> textStyles = null)
        {
            WpfHost.Ensure();
            InitializeComponent();
            // 登记为"置顶漂浮窗口"：弹模态窗口（批注内容窗等）时会被临时降为普通窗口，否则会把它盖住。
            CadDialog.RegisterFloating(this);
            _s = value;
            ShapeCombo.ItemsSource = new[] { "矩形", "菱形", "椭圆" };
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            DefaultRoleCombo.ItemsSource = AnnotationOptions.Roles;
            ConnectorCombo.ItemsSource = new[] { "-", "_", "·", "无" };
            ScaleRatioCombo.ItemsSource = AnnotationOptions.PlotScales;
            // 所有颜色下拉菜单共享 ACI 255 色列表
            foreach (var combo in new[] { ColorIndexCombo, CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, ScreenshotBackgroundColorCombo, PassColorCombo, CheckColorCombo })
                combo.ItemsSource = AciColors.All;
            // 文字样式下拉菜单：合并预设 + DWG 中已有样式
            var styles = new List<string> { "Standard" };
            if (textStyles != null) styles.AddRange(textStyles.Where(s => !string.Equals(s, "Standard", StringComparison.OrdinalIgnoreCase)));
            TextStyleCombo.ItemsSource = styles;
            LoadValues();
            Loaded += (s, e) => { WindowSizing.FitToWorkArea(this, 0.9, 0.86); RefreshDependencies(); };
            // 点谁谁在前：设置窗口与批注面板都是置顶窗口，被激活时主动顶到最上层，
            // 不用先关掉另一个才能操作这一个。
            Activated += (s, e) => WpfWindowUtil.BringToTop(this);
        }

        private void LoadValues()
        {
            ShapeCombo.Text = _s.Shape; CloudStyleCombo.Text = _s.CloudStyle;
            Set(CloudRadiusText, _s.CloudRadius); Set(LineWidthText, _s.LineWidth);
            CloudAutoFitCheck.IsChecked = _s.CloudAutoFit; FontAutoFitCheck.IsChecked = _s.FontAutoFit;
            Set(AutoTextViewPercentText, _s.AutoTextViewPercent);
            // 旧配置里存的是"全局比例"数字；这里统一按"1:N 出图比例"显示。
            _scaleChanging = true;
            ScaleRatioCombo.Text = FormatScale(_s.ScaleRatio);
            _scaleChanging = false;
            AutoCloseOrthoCheck.IsChecked = _s.AutoCloseOrtho; AutoCloseSnapCheck.IsChecked = _s.AutoCloseSnap;
            DoubleClickEditCheck.IsChecked = _s.DoubleClickEdit; ViewTopIsNorthCheck.IsChecked = _s.ViewTopIsNorth;
            Set(HeaderHeightText, _s.HeaderHeight); Set(SecondLineHeightText, _s.SecondLineHeight); Set(TextHeightText, _s.TextHeight);
            DefaultAuthorText.Text = _s.DefaultAuthor; DefaultRoleCombo.Text = _s.DefaultRole; DefaultDisciplineText.Text = _s.DefaultDiscipline;
            FixedWidthCheck.IsChecked = _s.FixedWidth; Set(FixedWidthValueText, _s.FixedWidthValue);
            AutoNumberCheck.IsChecked = _s.AutoNumber;
            LayerNameText.Text = _s.LayerName; TextStyleCombo.Text = _s.TextStyleName;
            SameColorsCheck.IsChecked = _s.SameColors;
            SelectColor(ColorIndexCombo, _s.ColorIndex); SelectColor(CloudColorCombo, _s.CloudColor);
            SelectColor(LeaderColorCombo, _s.LeaderColor); SelectColor(TextColorCombo, _s.TextColor);
            SelectColor(BoxColorCombo, _s.BoxColor); SelectColor(ReplyColorCombo, _s.ReplyColor);
            ScreenshotBackgroundOnceReplyCheck.IsChecked = _s.ScreenshotBackgroundOnceReply;
            SelectColor(ScreenshotBackgroundColorCombo, _s.ScreenshotBackgroundColor);
            SelectColor(PassColorCombo, _s.PassColor); SelectColor(CheckColorCombo, _s.CheckColor);
            StatusColorizeCheck.IsChecked = _s.StatusColorize;
            Set(CheckHeightText, _s.CheckHeight);
            LayerAppendDateCheck.IsChecked = _s.LayerAppendDate; LayerAppendNameCheck.IsChecked = _s.LayerAppendName;
            DateBeforeNameCheck.IsChecked = _s.DateBeforeName; ConnectorCombo.Text = _s.Connector;
            PlottableCheck.IsChecked = _s.Plottable;
            ShowDisciplineCheck.IsChecked = _s.ShowDiscipline;
            ShowAuthorCheck.IsChecked = _s.ShowAuthor; ShowRoleCheck.IsChecked = _s.ShowRole;
            ShowDateCheck.IsChecked = _s.ShowDate; ShowStatusCheck.IsChecked = _s.ShowStatus;
            ShowDrawingNoCheck.IsChecked = _s.ShowDrawingNo;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _s.Shape = ShapeCombo.Text.Trim(); _s.CloudStyle = CloudStyleCombo.Text.Trim();
                _s.CloudRadius = N(CloudRadiusText, "云线半径", 0.001); _s.LineWidth = N(LineWidthText, "云线线宽", 0);
                _s.CloudAutoFit = On(CloudAutoFitCheck); _s.FontAutoFit = On(FontAutoFitCheck);
                _s.AutoTextViewPercent = N(AutoTextViewPercentText, "自适应百分比", 0.1, 10);
                _s.ScaleRatio = ParseScale(ScaleRatioCombo.Text);
                // 比例与自适应是两套算法：选了比例却还勾着自适应时，字高仍按云线尺寸算，比例看不出效果。
                // 这里只在"本次确实改过比例"时问一句，避免每次保存都弹框；不问就保持自适应勾选不动。
                if (_scaleTouchedByUser && _s.ScaleRatio > 1.0 + 1e-9 && (_s.FontAutoFit || _s.CloudAutoFit))
                {
                    var autoNames = _s.FontAutoFit && _s.CloudAutoFit ? "字体/云线自适应" : _s.FontAutoFit ? "字体自适应" : "云线自适应";
                    var answer = MessageBox.Show(this,
                        "已选比例 1:" + FormatDenominator(_s.ScaleRatio) + "，但仍勾选了" + autoNames + "。\n\n" +
                        "自适应按云线尺寸计算，比例不参与，图上尺寸不会按比例放大。\n" +
                        "是否改为按比例换算（取消自适应勾选）？",
                        "X-批注", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes) { _s.FontAutoFit = false; _s.CloudAutoFit = false; }
                }
                _s.AutoCloseOrtho = On(AutoCloseOrthoCheck); _s.AutoCloseSnap = On(AutoCloseSnapCheck);
                _s.DoubleClickEdit = On(DoubleClickEditCheck); _s.ViewTopIsNorth = On(ViewTopIsNorthCheck);
                _s.HeaderHeight = N(HeaderHeightText, "首行字高", 0.01); _s.SecondLineHeight = N(SecondLineHeightText, "次行字高", 0.01);
                _s.TextHeight = N(TextHeightText, "正文字高", 0.01);
                _s.DefaultAuthor = DefaultAuthorText.Text.Trim(); _s.DefaultRole = DefaultRoleCombo.Text.Trim();
                _s.DefaultDiscipline = DefaultDisciplineText.Text.Trim();
                _s.FixedWidth = On(FixedWidthCheck); _s.FixedWidthValue = N(FixedWidthValueText, "固定宽度", 1);
                _s.AutoNumber = On(AutoNumberCheck);
                _s.LayerName = LayerNameText.Text.Trim();
                if (_s.LayerName.Length == 0) throw new InvalidOperationException("图层名称不能为空。");
                _s.TextStyleName = TextStyleCombo.Text.Trim();
                _s.SameColors = On(SameColorsCheck);
                _s.ColorIndex = SelectedColor(ColorIndexCombo); _s.CloudColor = SelectedColor(CloudColorCombo);
                _s.LeaderColor = SelectedColor(LeaderColorCombo); _s.TextColor = SelectedColor(TextColorCombo);
                _s.BoxColor = SelectedColor(BoxColorCombo); _s.ReplyColor = SelectedColor(ReplyColorCombo);
                _s.ScreenshotBackgroundOnceReply = On(ScreenshotBackgroundOnceReplyCheck);
                _s.ScreenshotBackgroundColor = SelectedColor(ScreenshotBackgroundColorCombo);
                _s.PassColor = SelectedColor(PassColorCombo); _s.CheckColor = SelectedColor(CheckColorCombo);
                // 按状态整条着色（已回复 / 已完成）：作用域是整条批注，与"统一颜色"互不相干。
                _s.StatusColorize = On(StatusColorizeCheck);
                _s.CheckHeight = N(CheckHeightText, "对勾高度", 0.01);
                _s.LayerAppendDate = On(LayerAppendDateCheck); _s.LayerAppendName = On(LayerAppendNameCheck);
                _s.DateBeforeName = On(DateBeforeNameCheck); _s.Connector = ConnectorCombo.Text.Trim();
                _s.Plottable = On(PlottableCheck);
                _s.ShowDiscipline = On(ShowDisciplineCheck);
                _s.ShowAuthor = On(ShowAuthorCheck); _s.ShowRole = On(ShowRoleCheck);
                _s.ShowDate = On(ShowDateCheck); _s.ShowStatus = On(ShowStatusCheck);
                _s.ShowDrawingNo = On(ShowDrawingNoCheck);
                // 统一颜色模式：所有"分项颜色"同步为统一颜色。
                // 注意**不要把 ReplyColor / PassColor 卷进来**：它们是"整条批注的状态色"，
                // 不属于分项着色，被覆盖的话用户在「状态颜色」里挑的已回复/已完成颜色保存后就被冲成统一色了。
                if (_s.SameColors) _s.CloudColor = _s.LeaderColor = _s.TextColor = _s.BoxColor = _s.CheckColor = _s.ColorIndex;
                SettingsStore.Save(_s);
                // 非模态窗口：不能给 DialogResult 赋值（非对话框窗口赋值会抛 InvalidOperationException），直接关。
                try { Saved?.Invoke(); } catch (Exception ex) { PluginLog.Warning("SettingsWindow.Saved", ex.Message); }
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message, "X-批注", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { Close(); }

        /// <summary>非模态窗口自己收 Esc / 回车键（按钮上的 IsCancel / IsDefault 已去掉，见 XAML 注释）。
        /// <list type="bullet">
        /// <item><b>Esc</b>＝取消并关闭（不保存）。下拉正展开时第一次 Esc 只收下拉，与常规对话框一致。</item>
        /// <item><b>回车</b>＝按「保存设置」。正在文本框 / 可编辑下拉里敲回车是在编辑内容，不动。</item>
        /// </list></summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                try
                {
                    var combo = WpfWindowUtil.FindAncestorComboBox(e.OriginalSource as DependencyObject);
                    if (combo != null && combo.IsDropDownOpen) return;
                }
                catch (Exception ex) { PluginLog.Warning("SettingsWindow.Window_PreviewKeyDown", ex.Message); }

                e.Handled = true;
                Close();
                return;
            }

            if (e.Key != Key.Enter) return;
            // 焦点在文本框/可编辑下拉里 = 在编辑内容；焦点在按钮/勾选框上 = 那是"按这个控件"，
            // 都不能当成"保存"。其余情况回车＝保存设置（与原先 IsDefault 的手感一致）。
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ButtonBase) return;
            e.Handled = true;
            Save_Click(this, null);
        }

        private void Dependency_Changed(object sender, RoutedEventArgs e) { RefreshDependencies(); }

        /// <summary>用户从下拉里选比例。<b>不再自动取消自适应勾选</b>——自适应默认是勾上的，
        /// 静默取消会让用户以为"默认没勾"；改为在下方提示里说明两者关系，是否取消由用户自己决定
        /// （保存时也只在本窗口内确实改过比例的前提下才问一次）。</summary>
        private void ScaleRatio_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_scaleChanging || !IsInitialized) return;
            _scaleTouchedByUser = true;
            RefreshDependencies();
        }

        /// <summary>把当前比例与打印尺寸当场换算成图面尺寸显示，避免"选了比例不知道算成多大"。</summary>
        private void RefreshScaleHint()
        {
            if (!IsInitialized) return;
            try
            {
                var n = TryParseScale(ScaleRatioCombo.Text) ?? 1.0;
                var textMm = TryNumber(TextHeightText, 3.0);
                var radiusMm = TryNumber(CloudRadiusText, 2.0);
                var denominator = FormatDenominator(n);
                var hint = "比例 1:" + denominator +
                           " → 图面字高 = " + Num(textMm) + "mm × " + denominator + " = " + Num(textMm * n) +
                           "；图面云线半径 = " + Num(radiusMm) + "mm × " + denominator + " = " + Num(radiusMm * n) + "。";
                if (On(FontAutoFitCheck) && On(CloudAutoFitCheck))
                    hint += " 当前勾选了字体/云线自适应：字高按云线尺寸计算；云线弧瓣仍按上面的比例换算，框选范围只决定瓣数。";
                else if (On(FontAutoFitCheck))
                    hint += " 当前勾选了字体自适应：字高按云线尺寸计算、比例不参与字高换算。";
                else if (On(CloudAutoFitCheck))
                    hint += " 当前勾选了云线自适应：弧瓣按上面的比例换算，框选范围只决定瓣数。";
                ScaleHintText.Text = hint;
            }
            catch (Exception ex)
            {
                ScaleHintText.Text = "";
                PluginLog.Warning("Settings.ScaleHint", ex.Message);
            }
        }

        private void RefreshDependencies()
        {
            if (!IsInitialized) return;
            FixedWidthValueText.IsEnabled = On(FixedWidthCheck);
            // 云线自适应现在只让"瓣数"随框大小变，弧瓣半径/线宽仍按这里的打印尺寸 × 图面比例换算，
            // 所以这两个输入框始终保持可用。
            CloudRadiusText.IsEnabled = true;
            LineWidthText.IsEnabled = true;
            var fontAuto = On(FontAutoFitCheck);
            AutoTextViewPercentText.IsEnabled = fontAuto;
            HeaderHeightText.IsEnabled = !fontAuto;
            SecondLineHeightText.IsEnabled = !fontAuto;
            TextHeightText.IsEnabled = !fontAuto;
            var same = On(SameColorsCheck);
            ColorIndexCombo.IsEnabled = same;
            // 分项颜色：只在"不统一"时可用。ReplyColor/PassColor 不在此列——它们是整条批注的状态色。
            foreach (var x in new[] { CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, CheckColorCombo })
                x.IsEnabled = !same;
            // 状态颜色：独立开关控制，与"统一颜色"无关
            var statusColorize = On(StatusColorizeCheck);
            ReplyColorCombo.IsEnabled = statusColorize;
            PassColorCombo.IsEnabled = statusColorize;
            RefreshScaleHint();
        }

        // ---- 比例（1:N 分母）解析与格式化 ----
        /// <summary>把比例分母写成 "1:N"；旧配置里存的就是 1，显示为 1:1。</summary>
        private static string FormatScale(double denominator) => "1:" + FormatDenominator(denominator);

        private static string FormatDenominator(double value)
        {
            var n = value > 0 ? value : 1.0;
            return Math.Abs(n - Math.Round(n)) < 1e-9
                ? ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture)
                : n.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>兼容 "1:100" 与 "100" 两种写法，解析出比例分母；解析不了返回 null。</summary>
        private static double? TryParseScale(string text)
        {
            var raw = (text ?? "").Trim();
            var colon = raw.IndexOf(':');
            if (colon >= 0) raw = raw.Substring(colon + 1).Trim();
            if (raw.Length == 0) return null;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.TryParse(raw, out n)) return null;
            return n > 0 ? n : (double?)null;
        }

        private static double ParseScale(string text)
        {
            var n = TryParseScale(text);
            if (!n.HasValue || n.Value < 0.01) throw new InvalidOperationException("比例必须是形如 1:100 的出图比例。");
            return n.Value;
        }

        private static double TryNumber(TextBox box, double fallback)
            => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.TryParse(box.Text, out n) ? n : fallback;

        private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        // ---- 辅助方法 ----
        private static bool On(CheckBox x) => x.IsChecked == true;
        private static void Set(TextBox x, double v) => x.Text = v.ToString("0.###", CultureInfo.InvariantCulture);
        private static double N(TextBox x, string name, double min, double max = double.MaxValue)
        {
            if (!double.TryParse(x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.TryParse(x.Text, out n))
                throw new InvalidOperationException(name + "必须是数字。");
            if (n < min || n > max) throw new InvalidOperationException(name + "超出允许范围。");
            return n;
        }
        /// <summary>在颜色 ComboBox 中按 ACI 索引选中对应项。</summary>
        private static void SelectColor(ComboBox combo, short index) => combo.SelectedItem = AciColors.Find(index);
        /// <summary>获取颜色 ComboBox 当前选中项的 ACI 索引，未选中时返回 7（白色）。</summary>
        private static short SelectedColor(ComboBox combo) => (combo.SelectedItem as AciColorItem)?.Index ?? 7;
    }
}
