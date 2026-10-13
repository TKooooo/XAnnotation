using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.EditorInput;
using Gssoft.Gscad.Geometry;
using CadApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_PRE2013
// AutoCAD 2012 及更早：还没有 AcCoreMgd 拆分，只有 ApplicationServices.Application 一个入口。
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif
#endif

namespace XAnnotation.Views
{
    /// <summary>批注功能选择框（菜单「批注绘制」/ 工具栏「批」）。
    /// <para><b>交互模型：面板打开后"待命"，不立刻启动 CAD 命令</b>。原因是宿主限制：CAD 命令一旦挂起
    /// 取点提示（拖拽/选点），鼠标输入被 CAD 独占，面板上的勾选框和按钮就点不动了 —— 早先"打开即自动开始"
    /// 会把这个面板冻住，于是"打开后先勾多对一 / 仅绘云线没反应，必须先跑完一轮"。
    /// 现在面板保持可用，用户勾好选项、换好外形后，把鼠标移回图纸（或点图纸 / 按回车 / 点外形按钮）才启动一轮，
    /// 按"当前外形 + 当前选项"进入。</para>
    /// <para><b>三种外形</b>：<see cref="ShapeRect"/>＝按「批注设置」里的云线外形（矩形/菱形/椭圆）框选绘制；
    /// <see cref="ShapePline"/>＝在图纸上逐点画出折线，云线沿该自定义形状生成，引线接在该轮廓上；
    /// <see cref="ShapeBasePoint"/>＝只点一个基点，引线从基点直连到批注文本框，不画云线。</para>
    /// <para>**Esc 关闭面板**（正在画则连带取消这一轮交互）。</para>
    /// <para>外形刻意用 Button 而不是 RadioButton：AutoCAD 宿主下点 RadioButton 会进入
    /// System.Windows.Controls.RadioButton.UpdateRadioButtonGroup() 并抛致命错误（崩溃转储已确认），
    /// Button 没有这个问题。</para></summary>
    internal partial class AnnotationPanel : Window
    {
        /// <summary>面板打开时的默认外形：跟随「批注设置」里的云线外形。</summary>
        private const string ShapeRect = "矩形";
        /// <summary>沿用户自绘折线生成云线的外形。</summary>
        private const string ShapePline = "PL线";
        /// <summary>只画基点 + 引线 + 文本框的外形（无云线）。</summary>
        private const string ShapeBasePoint = "基点";

        /// <summary>面板打开时的默认外形。</summary>
        private const string DefaultShape = ShapeRect;

        /// <summary>光标离开面板后多久之内进入图纸算"接着去画"（毫秒）。超过这个时间说明用户只是让光标在图纸上停着，不启动。</summary>
        private const int PanelLeaveGraceMs = 2000;

        /// <summary>光标在图纸上连续停留多久才算"打算开始画"（毫秒），用来过滤只是路过。</summary>
        private const int DrawingDwellMs = 400;

        /// <summary>两次光标事件间隔超过它就当作"新的一次进入图纸"（毫秒）。</summary>
        private const int DrawingVisitGapMs = 700;

        /// <summary>触发启动后等一小会儿再进命令（毫秒）：让触发它的那次点击先处理完，
        /// 免得那一下被新一轮的取点提示当成第一个角点。
        /// <para>2026-10-13 第三轮反馈后由 200 收到 120：这条延时是"用户感知启动快慢"的主要一环，
        /// 而 120 ms 仍足够让触发它的那一下点击处理完（AutoCAD 一次点击处理 &lt; 50 ms）。</para></summary>
        private const int StartDelayMs = 120;

        /// <summary>「基点」外形下"字号自适应"的参照尺寸＝当前视图高度 × 本系数。
        /// <para>基点批注没有云线范围可作参照，取一个"用户在此视图下常框选的范围大小"当参照，
        /// 使算出来的字高与框选流程下的观感一致（若直接拿整个视图高度当参照，字会大到离谱）。</para></summary>
        private const double BasePointAutoFitViewFraction = 0.2;

        private static AnnotationPanel _instance;
        private AnnotationSettings _s;
        /// <summary>当前订阅着"已保存"通知的设置窗口（面板关闭时退订）。它<b>不受面板关闭影响</b>：
        /// 两个窗口各自独立，关面板不会把设置窗口一起关掉。</summary>
        private SettingsWindow _settingsWindow;
        private bool _isRunning;
        private bool _continuePending;
        private bool _startPending;
        private Document _queuedDocument;
        private AnnotationSettings _queuedSettings;
        private bool _queuedIsSingle;
        private string _queuedShape;
        private AnnotationSettings _runSettings;
        private bool _runIsSingle;
        private string _runShape;
        private DateTime _pendingSinceUtc;
        /// <summary>本次打开是否已经挂过"光标进图纸"的监听：Loaded 可能重复触发，只认第一次。</summary>
        private bool _autoStarted;
        /// <summary>当前生效的外形（蓝底那个）。改绘制选项、自动开始时都用它，不必再点外形按钮。</summary>
        private string _currentShape = DefaultShape;

        /// <summary>绘制选项被改过、等当前这一轮退出来后按新选项重新开始。</summary>
        private bool _restartPending;
        /// <summary>本次改动里是否含「多对一批注」：它决定的是"单区域"还是"连续多范围"整套流程，
        /// 本轮没法中途改，只能中止本轮按新选项重开。</summary>
        private bool _flowChanged;
        /// <summary>当前这一轮进行到哪一步。见 <see cref="RunStage"/>。</summary>
        private RunStage _stage = RunStage.Idle;
        /// <summary>初始化/刷新控件期间的程序性赋值不算"用户改了选项"。</summary>
        private bool _suppressOptionEvents;
        /// <summary>面板正在关闭：不再触发任何重开。</summary>
        private bool _closing;
        /// <summary>勾选绘制选项后的防抖计时器：连勾多项只合并成一次决策。</summary>
        private readonly DispatcherTimer _optionStartTimer;

        /// <summary>光标最近一次离开面板/面板获得焦点的时间。只有"刚从面板出来"才允许由光标触发启动，
        /// 否则用户把光标停在图纸上一动不动（比如一边看面板一边想）也会被当成"要开始画"。</summary>
        private DateTime _panelLeaveUtc = DateTime.MinValue;
        /// <summary>本轮"光标停在图纸上"的起点；配合 <see cref="DrawingDwellMs"/> 过滤路过。</summary>
        private DateTime _dwellStartUtc;
        private DateTime _lastMonitorUtc;
        /// <summary>是否已挂上"光标进图纸"的监听（仅 AutoCAD 宿主，见 HookPointMonitor）。</summary>
        private bool _pointMonitorHooked;
        /// <summary>监听挂在哪篇图纸上：摘的时候按它摘，避免切图后留下野回调。</summary>
        private Document _monitorDocument;
        /// <summary>启动前的一小段延时（见 <see cref="StartDelayMs"/>）。</summary>
        private readonly DispatcherTimer _startDelayTimer;
        /// <summary>排队的 X_PZ_RUN 的看门狗：宿主没消费那条命令时换一条入口再试（见 <see cref="OnRunWatchdogTick"/>）。</summary>
        private readonly DispatcherTimer _runWatchdog;

        // ═══ 「光标回到图纸即开始」的轮询触发（见 OnIdleWatchTick） ═══
        //
        // 面板打开后必须能"什么都不点就直接去图纸上画"。早先只有两条入口：
        //   ① AutoCAD 宿主的 Editor.PointMonitor（中望/浩辰是留空实现，等于没有）；
        //   ② 面板 Deactivated 后的"焦点是否回到 CAD 主窗口"判定。
        // 而 ② 只在**面板失去焦点的那一刻**到达一次 —— 中望实测面板刚打开 513 ms 就被宿主抢回焦点
        // （浩辰约 1 s），那次失活被宽限期挡掉之后，用户再点图纸就什么事件都没有了，
        // "回图纸即开始"这条路彻底死掉，只能去点外形按钮（用户反馈"必须要手动选一次外形才能批注"）。
        // 轮询不看宿主事件，四个宿主行为一致。

        /// <summary>空闲轮询计时器（仅面板待命时有效）。优先级必须 Normal：Background 会被 CAD 主循环饿死。</summary>
        private readonly DispatcherTimer _idleWatch;

        /// <summary>轮询间隔（毫秒）。
        /// <para>2026-10-13 第三轮反馈后由 200 收到 120：这段等待直接叠加在用户的"我已经把鼠标
        /// 移到图纸上了，怎么还不开始"里，是启动延迟里最容易被感知的一环。</para></summary>
        private const int IdleWatchIntervalMs = 120;

        /// <summary>光标连续满足"不在面板上、且落在 CAD 窗口里"这么多次（× <see cref="IdleWatchIntervalMs"/>）
        /// 才算用户要开始画。
        /// <para>2026-10-13 由 3 收到 2：原值（600 ms）让"打开面板 → 移鼠标到图纸"这条最自然的路径
        /// 慢到用户以为没反应 —— 实测用户会在这段空档里回去点一次外形按钮（反馈里的
        /// "都需要手动选择一次矩形批注才能批注"）。2 × 120 = 240 ms 足够滤掉面板刚 Show 出来时的抖动。</para></summary>
        private const int IdleWatchDwellTicks = 2;

        // 2026-10-13 第三轮反馈后**取消**了"光标必须动过（位移 ≥ 3 px）"这道判定。
        // 它当初是为了把"宿主把焦点抢回去（用户没碰鼠标）"和"用户真去图纸"分开，但代价是把
        // 最自然的那条路径也卡住了一拍：光标位置采样要等下一拍才有上一拍可比，白送一个轮询周期。
        // 而现在这种误判已经被两道更直接的判据挡住了：
        //   ① IsCursorOverPanel()：光标还在面板矩形里就一律不算"离开面板"；
        //   ② IdleWatchDwellTicks 连续 2 拍确认（240 ms），面板刚 Show 出来的抖动过不去。

        /// <summary>"光标在图纸上"已经连续满足了几次轮询。</summary>
        private int _idleDwellTicks;

        // ═══ Esc 退出批注（见 OnEscapeKey） ═══

        /// <summary>线程级键盘钩子句柄；<see cref="IntPtr.Zero"/> 表示没装上。</summary>
        private IntPtr _escapeHook;

        /// <summary>钩子回调的强引用。<b>必须存字段</b>：委托被 GC 掉以后 CAD 的键盘路径会直接崩。</summary>
        private LowLevelKeyboardProc _escapeHookProc;

        /// <summary>这一轮是不是"用户自己取消的"（CAD 提示取消 / 内容窗取消），而不是插件的重启逻辑（改选项/换外形）。
        /// <para>本轮结束时据此<b>退出批注</b>（关掉面板），对应需求"批注完成后或不需要批注时按 Esc 要能退出批注"。
        /// 插件自己的重启走 <see cref="InteractionAbort"/>，不会被误判成用户取消。</para></summary>
        private bool _userCancelled;

        /// <summary>本轮所处阶段。面板打开时是"待命"（<see cref="Idle"/>，没有命令在跑，控件都可点）；
        /// 用户回图纸/回车/点外形后才会进入下面三个阶段。
        /// <para>注意：命令一旦挂起取点提示，鼠标输入被 CAD 独占，面板上再改选项是点不到的 ——
        /// 所以下面这套"改动怎么落地"只在极少数还能收到事件的情况下起作用（例如已排队未开跑）。</para>
        /// <list type="bullet">
        /// <item><see cref="Picking"/>：正在选云线范围 —— 外形/仅绘云线/连续绘制都能被本轮直接读到，改动立即生效。</item>
        /// <item><see cref="Content"/>：批注内容对话框已弹出 —— 本轮的口径早已定型，改动留待下一轮，
        /// 不中止本轮，免得把用户刚填的内容丢掉。</item>
        /// <item><see cref="Elsewhere"/>：范围已定型（定位拖放/落图）—— 改动无法在本轮体现，按新选项重开。</item>
        /// </list></summary>
        private enum RunStage
        {
            Idle,
            Picking,
            Content,
            Elsewhere
        }

        /// <summary>连勾多项时合并成一次重开的等待时间。</summary>
        private static readonly TimeSpan OptionStartDebounce = TimeSpan.FromMilliseconds(500);

        /// <summary>排队的 X_PZ_RUN 超过这个时间仍未被宿主消费，就判定"命令被丢了"，改走兜底入口。
        /// <para>2026-10-13 由 600 收到 400：<c>SendStringToExecute</c> 在部分宿主上被静默丢弃时，
        /// 这 400 ms 就是"面板没反应"的时长；配合 <see cref="MaxRunRetry"/> 次重试，
        /// 最坏情况下 1.2 s 内必定落到 <c>Editor.Command</c> 或最后一次重发。</para></summary>
        private const int RunWatchdogMs = 400;

        /// <summary>看门狗最多重发几次 X_PZ_RUN（第一次重发前还会先试一次 <c>Editor.Command</c>）。
        /// <para>只重发、不重复执行：<c>X_PZ_RUN</c> 一进命令上下文就会 <see cref="ResetPendingStart"/>，
        /// 所以 <c>_startPending</c> 一旦被清就说明那一条已被消费，看门狗不会再动手。</para></summary>
        private const int MaxRunRetry = 4;

        /// <summary>本轮启动已经重发过几次（每次排队归零）。</summary>
        private int _runRetry;

        /// <summary>排队的 X_PZ_RUN 超过这个时间仍未被消费，视为状态已失效：下次触发时先复位再排队。
        /// <para>没有它，一旦宿主没执行那条命令，<c>_startPending</c> 会永久停在 true，
        /// 之后面板上每一次点击都会被"忽略…pending=True"挡掉 —— 2026-10-13 浩辰实测就是这个症状。</para></summary>
        private static readonly TimeSpan StalePending = TimeSpan.FromSeconds(2);

        private readonly List<Point3d> _multiCloudFirsts = new List<Point3d>();
        private readonly List<Point3d> _multiCloudSeconds = new List<Point3d>();

        private AnnotationPanel()
        {
            InitializeComponent();
            // 登记为"置顶漂浮窗口"：弹模态窗口（批注内容窗等）时会被临时降为普通窗口，
            // 否则模态窗会躲在本面板后面（两个 Topmost 窗口相互不能盖住）。
            CadDialog.RegisterFloating(this);
            // 与「批注设置」窗口共用**同一个**设置对象：设置窗口还开着（面板关掉又重开）时沿用它那一份，
            // 否则才从设置文件读一份新的。两边各持一份的话，谁的落盘都会把对方刚改的值覆盖回去。
            _s = SettingsWindow.SharedSettings();
            // 下拉数据源
            DisciplineCombo.ItemsSource = AnnotationOptions.Disciplines;
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            RoleCombo.ItemsSource = AnnotationOptions.Roles;
            // 加载当前设置值
            RefreshControls();
            // 选项变更的防抖：只有在"这一轮已经在跑/已排队"时才需要做点什么（见 OnOptionStartTick）；
            // 面板待命期间改选项只是更新状态，启动交给 StartByTrigger。
            _optionStartTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = OptionStartDebounce
            };
            _optionStartTimer.Tick += OnOptionStartTick;
            // 优先级必须是 Normal，不能是 Background：
            //   Background 的回调只在 Dispatcher 队列空闲时才跑，而 CAD 宿主的主循环长期有
            //   Render / Input 级别的消息，Background 的 Tick 可能被无限期饿死 ——
            //   症状就是"点了外形按钮，日志有触发记录、却再也等不到那 200ms 后的启动"。
            //   Normal 与输入同优先级，几个宿主上都能稳定到点。
            _startDelayTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(StartDelayMs)
            };
            _startDelayTimer.Tick += OnStartDelayTick;
            // X_PZ_RUN 的看门狗：SendStringToExecute 在个别宿主上会被静默丢弃（见 OnRunWatchdogTick）。
            // 同样必须是 Normal 优先级 —— 用 Background 会被 CAD 主循环饿死，看门狗就永远不出来救场。
            _runWatchdog = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(RunWatchdogMs)
            };
            _runWatchdog.Tick += OnRunWatchdogTick;
            // 「光标回到图纸即开始」的空闲轮询：中望/浩辰没有 PointMonitor，而 Deactivated 只在面板
            // 失去焦点的一瞬间到达一次（宿主抢焦点那次被宽限期挡掉后就再也没有事件了）。
            _idleWatch = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(IdleWatchIntervalMs)
            };
            _idleWatch.Tick += OnIdleWatchTick;
            // 面板修改只保留为待应用值；真正落盘发生在开始绘制时。
            Loaded += OnPanelLoaded;
            // 用户从别处回到面板（或刚打开）：视作"刚从面板离开"，这样他接着把光标移到图纸就算开始画。
            Activated += (_, __) =>
            {
                // 点谁谁在前：面板与「批注设置」都是置顶窗口，被激活时主动顶到最上层，
                // 不用先关掉另一个才能操作这一个（两个窗口各自独立：各拖各的、各关各的）。
                WpfWindowUtil.BringToTop(this);

                // 回到面板＝这一段时间的"光标在图纸上"计数作废：要重新从面板离开才算用户去图纸。
                _idleDwellTicks = 0;

                // 「批注设置」窗口开着时不算"离开面板去画图"：用户是在配置，不是在画。
                // 设置窗口压着的正好是图纸，这时候还留着光标触发的话，鼠标划过它就可能莫名开始一轮。
                if (SettingsWindow.IsOpen)
                {
                    _panelLeaveUtc = DateTime.MinValue;
                    UnhookPointMonitor();
                    return;
                }

                _panelLeaveUtc = DateTime.UtcNow;
                HookPointMonitor();
            };
            MouseLeave += (_, __) => _panelLeaveUtc = DateTime.UtcNow;
            // 焦点离开面板：如果接住焦点的是 CAD 自己，说明用户点了图纸 → 开始画（见 OnPanelDeactivated）。
            Deactivated += OnPanelDeactivated;
            Closing += (_, __) =>
            {
                _closing = true;
                _restartPending = false;
                _continuePending = false;
                _userCancelled = false;
                _optionStartTimer.Stop();
                _startDelayTimer.Stop();
                _runWatchdog.Stop();
                _idleWatch.Stop();
                RemoveEscapeHook();
                UnhookPointMonitor();
                // 退订设置窗口的"已保存"通知：面板关掉后不该再被它回调。
                // 注意 **不去关** 设置窗口：两个窗口各自独立关闭，关面板不影响它。
                if (_settingsWindow != null)
                {
                    try { _settingsWindow.Saved -= OnSettingsSaved; }
                    catch (Exception ex) { PluginLog.Warning("AnnotationPanel.Closing", ex.Message); }
                    _settingsWindow = null;
                }

                // 关面板时也把"上次的选择"存下来：用户可能只是挑好外形/勾好选项就关掉面板，
                // 下次打开仍应看到同一套选择。异常不能影响关闭本身。
                try { ApplyPanelSettings(); }
                catch (Exception ex) { PluginLog.Warning("AnnotationPanel.Closing", ex.Message); }
                _instance = null;
            };
        }

        /// <summary>面板里按 Esc：退出批注对话框（正在画则连带取消当前这一轮交互）。
        /// <para>回车：按当前外形与选项开始一轮（不想用鼠标时的入口）。</para>
        /// <para>下拉正展开时第一次 Esc 只收下拉，与常规对话框一致。</para></summary>
        private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // 正在文本框/可编辑下拉里敲回车是在编辑内容，焦点在按钮上则回车是"按这个按钮"，
                // 都不能当"开始绘制"用。
                if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is Button) return;
                e.Handled = true;
                StartByTrigger("回车");
                return;
            }

            if (e.Key != Key.Escape) return;
            try
            {
                var combo = WpfWindowUtil.FindAncestorComboBox(e.OriginalSource as DependencyObject);
                if (combo != null && combo.IsDropDownOpen) return;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.Panel_PreviewKeyDown", ex.Message);
            }

            e.Handled = true;
            ClosePanel();
        }

        /// <summary>关闭批注对话框：若这一轮还在交互中，先取消它，避免留下无主的范围选择提示。
        /// <para>双管齐下：置交互中止标志（我方 Jig 在 Sampler 里检查，鼠标一动就干净退出）＋发一次 Esc
        /// （正在跑原生提示时能立刻收手）。本轮结束后标志会自动清掉。</para></summary>
        private void ClosePanel()
        {
            if (_closing) return;   // 已经在关了：Esc 钩子与 WPF 按键可能各来一次，只认第一次
            try
            {
                if (_isRunning)
                {
                    InteractionAbort.Request();
                    CancelRunningAnnotation();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.ClosePanel(cancel)", ex);
            }

            try
            {
                Close();
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.ClosePanel", ex);
            }
        }

        /// <summary>从按键源元素往上找 ComboBox（可编辑下拉里 OriginalSource 是内部 TextBox）。
        /// 实现见 <see cref="WpfWindowUtil.FindAncestorComboBox"/>，与批注设置窗口共用。</summary>
        private static ComboBox FindAncestorComboBox(DependencyObject node)
            => WpfWindowUtil.FindAncestorComboBox(node);

        // ═══════════════ 待命 → 开始 ═══════════════
        //
        // 面板打开后**不**立刻启动 CAD 命令：命令一旦挂起取点提示，鼠标输入就被 CAD 独占，
        // 面板上的勾选框/按钮都点不动（这就是"打开后先勾多对一/仅绘云线没反应"的根因）。
        // 所以改成"待命"：面板保持可用，由下面几种"用户真的开始画了"的迹象触发启动。

        /// <summary>触发一次启动：延时一小会儿（见 <see cref="StartDelayMs"/>）再按当前外形与选项进入命令。
        /// <para>已经在跑/已排队的那一轮不重复启动；本轮待命状态被消费掉。</para>
        /// <para><b>提前返回一律记日志</b>：这条路径上任何一次静默 return 都会表现为"点了按钮毫无反应"，
        /// 而现场只看命令行是查不出来的（2026-10-13 浩辰上就遇到过）。</para></summary>
        private void StartByTrigger(string reason)
        {
            // 先自愈再判重。顺序不能反：复位逻辑若放在下面那道 _startPending 早退之后，就永远走不到 ——
            // 于是"宿主丢了一次 X_PZ_RUN"会变成"之后怎么点都被忽略"，正是 2026-10-13 浩辰实测的现场。
            ClearStalePending();
            if (_closing || !IsReadyToStart || _isRunning || _startPending)
            {
                PluginLog.Info("AnnotationPanel.Start",
                    "忽略「" + reason + "」触发：closing=" + _closing
                    + " IsLoaded=" + IsLoaded + " IsVisible=" + IsVisible
                    + " running=" + _isRunning + " pending=" + _startPending + "。");
                return;
            }
            _dwellStartUtc = default(DateTime);
            PluginLog.Info("AnnotationPanel.Start", "由「" + reason + "」触发启动。");
            _startDelayTimer.Stop();
            _startDelayTimer.Start();
        }

        /// <summary>排队的 X_PZ_RUN 迟迟没被宿主消费时复位，避免 <c>_startPending</c> 把面板永久锁死。
        /// <para>判定口径见 <see cref="StalePending"/>；复位只清"已排队"状态，不影响正在跑的那一轮。</para></summary>
        private void ClearStalePending()
        {
            if (!_startPending || _isRunning) return;
            if (_pendingSinceUtc == default(DateTime)) { ResetPendingStart(); return; }
            var age = DateTime.UtcNow - _pendingSinceUtc;
            if (age < StalePending) return;
            PluginLog.Warning("AnnotationPanel.StalePending",
                "排队的 X_PZ_RUN 已过 " + age.TotalSeconds.ToString("0.#") + " 秒仍未被执行，"
                + "判定宿主未消费该命令，复位后重试。");
            _runWatchdog.Stop();
            ResetPendingStart();
        }

        /// <summary>窗口是否已经能承载一次启动。
        /// <para>不直接用 <c>IsLoaded</c>：AutoCAD / 中望 / 浩辰三种宿主里 WPF 的生命周期触发时机并不一致，
        /// 在个别宿主上 <c>IsLoaded</c> 迟迟不为 true，会让"点外形按钮"整条入口静默失效。
        /// 只要窗口已经 Show 出来（<c>IsVisible</c>）就认为可用。</para></summary>
        private bool IsReadyToStart => IsLoaded || IsVisible;

        private void OnStartDelayTick(object sender, EventArgs e)
        {
            _startDelayTimer.Stop();
            if (_closing || !IsReadyToStart || _isRunning || _startPending)
            {
                PluginLog.Info("AnnotationPanel.StartDelay",
                    "延时到点但放弃启动：closing=" + _closing + " IsLoaded=" + IsLoaded
                    + " running=" + _isRunning + " pending=" + _startPending + "。");
                return;
            }
            StartWithShape(_currentShape);
        }

        /// <summary>面板失活：只把"光标进图纸"的宿主监听摘掉，**不再在这里启动**。
        /// <para><b>为什么不启动</b>：这一处只在面板失去焦点的一瞬间到达一次。若宿主自己在面板刚打开时
        /// 就把焦点抢回去（中望实测 513 ms、浩辰约 1 s），那次失活会被"刚打开"的宽限期挡掉；
        /// 用户随后点图纸<b>不会再有第二个 Deactivated</b>，整条入口就此死掉，
        /// 只剩"点外形按钮"能画（用户反馈的原话）。现在统一交给与宿主无关的
        /// <see cref="OnIdleWatchTick"/> 轮询判断，这条路径因此不再需要。</para></summary>
        private void OnPanelDeactivated(object sender, EventArgs e)
        {
            UnhookPointMonitor();
            _idleDwellTicks = 0;
        }

        /// <summary>空闲轮询：面板待命时，只要"光标已不在面板上、落在 CAD 窗口里、CAD 是前台窗口"
        /// 连续满足几次，就按当前外形与选项开始一轮。
        /// <para>与宿主事件无关，四个宿主（AutoCAD 2013-2024 / 2025+ / 中望 / 浩辰）行为一致。</para>
        /// <para><b>2026-10-13 第三轮反馈后的改动</b>：这里原本还要求"光标动过（位移 ≥ 3 px）"，
        /// 现在去掉了。去掉之后"打开面板 → 把鼠标移向图纸"就<b>直接开始</b>，
        /// 用户不需要在面板上点任何按钮 —— 这正是反馈里要的
        /// "进入之后不需要做任何选择就能矩形批注"。误判风险由两道判据兜着：
        /// 光标仍在面板矩形内一律不算离开（宿主抢焦点时鼠标通常还压在面板上），
        /// 以及连续 <see cref="IdleWatchDwellTicks"/> 拍确认（240 ms）。</para></summary>
        private void OnIdleWatchTick(object sender, EventArgs e)
        {
            try
            {
                if (_closing || !IsReadyToStart || _isRunning || _startPending)
                {
                    _idleDwellTicks = 0;
                    return;
                }

                // 「批注设置」窗口开着时不算"去图纸画"（用户是在配置）。
                if (SettingsWindow.IsOpen) { _idleDwellTicks = 0; return; }

                // 本轮已经被结束过一轮（用户按 Esc 取消 / 正常画完）而面板还开着：_panelLeaveUtc
                // 被清成 MinValue，含义是"用户还没重新表示要画"。这时按住轮询，直到他回到面板
                // （Activated / MouseLeave 会重新盖章）——否则取消一轮之后光标还停在图纸上，
                // 下一拍就会自己又开一轮，用户按 Esc 等于没按。
                if (_panelLeaveUtc == DateTime.MinValue) { _idleDwellTicks = 0; return; }

                if (IsCursorOverPanel()) { _idleDwellTicks = 0; return; }
                if (!IsCursorOverCadWindow()) { _idleDwellTicks = 0; return; }
                if (!IsCadMainWindowForeground()) { _idleDwellTicks = 0; return; }

                if (++_idleDwellTicks < IdleWatchDwellTicks) return;
                _idleDwellTicks = 0;
                StartByTrigger("光标停在图纸上");
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.IdleWatch", ex.Message);
            }
        }


        /// <summary>鼠标指针此刻是否落在 CAD 主窗口的矩形范围内（拿不到句柄时不拦，退回只判前景窗口）。</summary>
        private static bool IsCursorOverCadWindow()
        {
            try
            {
                var main = GetCadMainWindowHandle();
                if (main == IntPtr.Zero) return true;
                if (!GetCursorPos(out var point)) return false;
                if (!GetWindowRect(main, out var rect)) return false;
                return point.X >= rect.Left && point.X < rect.Right
                    && point.Y >= rect.Top && point.Y < rect.Bottom;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.IsCursorOverCadWindow", ex.Message);
                return true;
            }
        }

        // ═══════════════ Esc 退出批注 ═══════════════

        // 需求（2026-10-13）："批注完成后或者不需要批注的时候按 esc 要能退出批注"。
        // 面板自己有焦点时 Esc 由 Panel_PreviewKeyDown 处理，可是 CAD 命令一跑起来（或用户一点图纸）
        // 焦点就回到 CAD，那时候的 Esc 根本到不了 WPF。所以这里装一个**线程级**键盘钩子：
        //   WH_KEYBOARD + 本线程 ID —— 只收本线程（＝ CAD 界面线程）键盘输入，不需要 DLL、
        //   不影响其它进程；回调里一律 CallNextHookEx，**绝不吞键**，CAD 自己的 Esc 语义原样保留。

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD = 2;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>装上 Esc 钩子（幂等）。失败只记日志：没有它 Esc 仍然能在面板有焦点时退出。</summary>
        private void InstallEscapeHook()
        {
            if (_escapeHook != IntPtr.Zero) return;
            try
            {
                _escapeHookProc = OnEscapeKey;
                _escapeHook = SetWindowsHookEx(WH_KEYBOARD, _escapeHookProc, IntPtr.Zero, GetCurrentThreadId());
                if (_escapeHook == IntPtr.Zero)
                {
                    PluginLog.Warning("AnnotationPanel.EscapeHook", "安装键盘钩子失败，Esc 仅在面板有焦点时生效。");
                }
            }
            catch (Exception ex)
            {
                _escapeHook = IntPtr.Zero;
                PluginLog.Warning("AnnotationPanel.EscapeHook", ex.Message);
            }
        }

        private void RemoveEscapeHook()
        {
            if (_escapeHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_escapeHook); }
            catch (Exception ex) { PluginLog.Warning("AnnotationPanel.EscapeHook(remove)", ex.Message); }
            _escapeHook = IntPtr.Zero;
            _escapeHookProc = null;
        }

        /// <summary>键盘钩子回调：面板空闲时按 Esc ＝ 退出批注（关面板）。
        /// <para>WH_KEYBOARD 的 <c>wParam</c> 是虚拟键码，<c>lParam</c> 的 bit31 = 1 表示"抬键"。</para>
        /// <para>整个回调必须吞掉所有异常：异常越过托管/原生边界会直接崩掉 CAD。</para></summary>
        private IntPtr OnEscapeKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var isKeyDown = (lParam.ToInt64() & 0x80000000L) == 0;
                if (nCode >= 0 && isKeyDown && wParam.ToInt64() == VK_ESCAPE && ShouldExitOnEscape())
                {
                    PluginLog.Info("AnnotationPanel.EscapeHook", "面板待命中收到 Esc，退出批注。");
                    Dispatcher.BeginInvoke(new Action(ClosePanel), DispatcherPriority.Normal);
                }
            }
            catch (Exception ex)
            {
                try { PluginLog.Warning("AnnotationPanel.EscapeHook(tick)", ex.Message); } catch { }
            }

            // 永不吞键：CAD 自己的 Esc（取消命令、清除选择）必须原样收到。
            return CallNextHookEx(_escapeHook, nCode, wParam, lParam);
        }

        /// <summary>这一下 Esc 该由我们处理吗。
        /// <para>只处理"面板待命、没有交互在跑"的情形：正在跑的那一轮让 CAD 自己取消，
        /// 取消之后由 <c>StartAnnotation</c> 的收尾逻辑关闭面板，两条路都不重叠。</para></summary>
        private bool ShouldExitOnEscape()
        {
            if (_closing || !IsLoaded) return false;
            // 用户在配置：Esc 是关「批注设置」窗口。
            if (SettingsWindow.IsOpen) return false;
            if (_isRunning || _startPending) return false;
            return true;
        }

        /// <summary>当前前景窗口是不是 <b>CAD 主窗口</b>。
        /// <para>不能只判"同进程"：本插件自己的窗口（批注设置、批注列表…）也跑在 CAD 进程里，
        /// 点开「批注设置…」会让面板失活，只判进程就会擅自启动一轮批注。</para></summary>
        private static bool IsCadMainWindowForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            var main = GetCadMainWindowHandle();
            if (main != IntPtr.Zero) return hwnd == main;

            // 拿不到主窗口句柄时退回"同进程"判定，并排除掉我们自己开的 WPF 窗口。
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
            return !IsOurOwnWindow(hwnd);
        }

        /// <summary>CAD 主窗口句柄（即本进程的主窗口）。取不到时返回 <see cref="IntPtr.Zero"/>。</summary>
        private static IntPtr GetCadMainWindowHandle()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>给定句柄是不是本插件自己开的 WPF 窗口。</summary>
        private static bool IsOurOwnWindow(IntPtr hwnd)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return false;
                foreach (Window w in app.Windows)
                {
                    if (w == null) continue;
                    if (new System.Windows.Interop.WindowInteropHelper(w).Handle == hwnd) return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.IsOurOwnWindow", ex.Message);
            }

            return false;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        /// <summary>鼠标指针此刻是否还落在本面板窗口的矩形范围内。
        /// <para>用来区分"面板失活"的两种成因：用户点了图纸（光标已离开面板 → 该启动）与
        /// 宿主自己抢回焦点（光标仍在面板上 → 不该启动）。用 Win32 取坐标而不是 WPF 的
        /// <c>IsMouseOver</c>：窗口失活后 WPF 的鼠标状态会停止更新，拿到的可能是残留值。</para></summary>
        private bool IsCursorOverPanel()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return false;
                if (!GetCursorPos(out var point)) return false;
                if (!GetWindowRect(hwnd, out var rect)) return false;
                return point.X >= rect.Left && point.X < rect.Right
                    && point.Y >= rect.Top && point.Y < rect.Bottom;
            }
            catch (Exception ex)
            {
                // 取不到就按"不在面板上"处理（照旧走原来的判定），不因为一个探测失败堵死启动入口。
                PluginLog.Warning("AnnotationPanel.IsCursorOverPanel", ex.Message);
                return false;
            }
        }

#if ZWCAD
        // ZWCAD 宿主不挂"光标进图纸"的监听：宁可少一个入口，也不引入未经验证的宿主 API。
        // 那边靠「点图纸 / 回车 / 点外形按钮」三个入口启动，功能完整。
        private void HookPointMonitor() { }
        private void UnhookPointMonitor() { }
        private void RehookPointMonitor() { }
#elif GSCAD
        // 浩辰与中望同处理：不挂"光标进图纸"的监听——宁可少一个入口，也不引入未经验证的宿主 API。
        // 靠「点图纸 / 回车 / 点外形按钮」三个入口启动，功能完整。
        private void HookPointMonitor() { }
        private void UnhookPointMonitor() { }
        private void RehookPointMonitor() { }
#else
        /// <summary>挂上"光标在图纸上移动"的监听：光标刚从面板出来又在图纸上停住（见 <see cref="DrawingDwellMs"/>），
        /// 就当成"用户要开始画"，不用再点一下。
        /// <para>只在"面板拿着焦点、还没开始画"这段时间挂着：一开始画或面板失活就摘掉，
        /// 免得在 CAD 正常使用时一直占着这个每帧都触发的回调。</para></summary>
        private void HookPointMonitor()
        {
            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                if (ReferenceEquals(_monitorDocument, doc) && _pointMonitorHooked) return;
                UnhookPointMonitor();
                doc.Editor.PointMonitor += OnPointMonitor;
                _monitorDocument = doc;
                _pointMonitorHooked = true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.HookPointMonitor", ex.Message);
            }
        }

        /// <summary>摘掉监听。按挂载时记下的那篇图纸摘（期间可能已经切过图），避免留下野回调把面板对象拖住。</summary>
        private void UnhookPointMonitor()
        {
            var doc = _monitorDocument;
            _monitorDocument = null;
            _pointMonitorHooked = false;
            if (doc == null) return;
            try
            {
                if (!doc.IsDisposed) doc.Editor.PointMonitor -= OnPointMonitor;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.UnhookPointMonitor", ex.Message);
            }
        }

        /// <summary>切换图纸后把监听挪到新图纸上。</summary>
        private void RehookPointMonitor()
        {
            if (!_pointMonitorHooked) return;
            UnhookPointMonitor();
            HookPointMonitor();
        }

        private void OnPointMonitor(object sender, PointMonitorEventArgs e)
        {
            try
            {
                if (_closing || !IsLoaded || _isRunning || _startPending) return;

                var now = DateTime.UtcNow;
                var fromPanel = (now - _panelLeaveUtc).TotalMilliseconds <= PanelLeaveGraceMs;
                if (!fromPanel)
                {
                    // 光标不是刚从面板过来的：只是在图纸上停着，不启动。
                    _dwellStartUtc = default(DateTime);
                    return;
                }

                if (_dwellStartUtc == default(DateTime) ||
                    (now - _lastMonitorUtc).TotalMilliseconds > DrawingVisitGapMs)
                {
                    _dwellStartUtc = now;
                }

                _lastMonitorUtc = now;
                if ((now - _dwellStartUtc).TotalMilliseconds < DrawingDwellMs) return;

                StartByTrigger("光标进入图纸");
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.OnPointMonitor", ex.Message);
            }
        }
#endif

        /// <summary>勾选/取消任一绘制选项 → 只更新本轮的选项状态（启动由"回图纸/回车/点外形"负责）。
        /// <para>「仅绘云线」优先：勾上它就把「多对一批注」取消掉，两者同时勾选按仅绘云线处理。</para></summary>
        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressOptionEvents || _closing) return;

            if (ReferenceEquals(sender, CloudOnlyCheck) &&
                CloudOnlyCheck.IsChecked == true &&
                MultiModeCheck.IsChecked == true)
            {
                _suppressOptionEvents = true;
                try
                {
                    MultiModeCheck.IsChecked = false;
                }
                finally
                {
                    _suppressOptionEvents = false;
                }
            }

            // 「多对一批注」改的是整套流程（单区域 vs 连续多范围），正在跑的那一轮无法中途改，只能中止重开。
            NotifyOptionsChanged(ReferenceEquals(sender, MultiModeCheck));
        }

        /// <summary>面板里改了绘制选项或换了外形。
        /// <para>第一步：把改动原地写进"正在画的这一轮"的设置快照 —— 本轮读到的就是新值，
        /// 于是"勾选后不必再点一次外形"（取范围之前改的选项，本轮直接按新值继续）。</para>
        /// <para>第二步：防抖排一次决策，决定要不要让本轮重开（连勾多项只决策一次）。</para></summary>
        private void NotifyOptionsChanged(bool flowChanged)
        {
            if (_suppressOptionEvents || _closing) return;
            if (flowChanged) _flowChanged = true;
            ApplyLivePanelOptions();

            // _optionStartTimer 在构造函数里创建；控件构造早期若意外触发本事件，这里直接忽略。
            if (_optionStartTimer == null) return;
            _optionStartTimer.Stop();
            _optionStartTimer.Start();
        }

        /// <summary>把面板上"本次生效"的选项<b>原地</b>写进当前这一轮的设置快照。
        /// <para><b>为什么是原地</b>：拖拽预览 Jig 每帧都从这份实例重新解析参数，落图也用同一份，
        /// 所以改完立刻就能在预览里看到；换成副本就会出现"预览还是矩形、落图却是菱形"这类不一致。</para></summary>
        private void ApplyLivePanelOptions()
        {
            var running = _runSettings;
            if (running == null) return;
            // 「矩形」外形＝跟随批注设置里的云线外形（矩形/菱形/椭圆）；PL线/基点不用这个值。
            running.Shape = CloudShapeForPanel();
            running.CloudOnly = CloudOnlyCheck.IsChecked == true;
            running.ContinuousAnnotation = ContinuousCheck.IsChecked == true;
        }

        /// <summary>防抖地排一次决策：连勾多项只走一遍。</summary>
        private void ScheduleOptionStart()
        {
            if (_optionStartTimer == null || !IsLoaded || _closing) return;
            _optionStartTimer.Stop();
            _optionStartTimer.Start();
        }

        private void OnOptionStartTick(object sender, EventArgs e)
        {
            _optionStartTimer.Stop();
            if (_closing) return;

            var flowChanged = _flowChanged;
            _flowChanged = false;

            try
            {
                // 改动先落进本轮快照（若本轮已经在跑）。
                ApplyLivePanelOptions();

                // 已排队但还没跑起来：直接换掉排队里的设置快照，不必多启动一轮。
                if (_startPending)
                {
                    ApplyPanelSettings();
                    var fresh = _s.Clone();
                    fresh.Shape = CloudShapeForPanel();
                    ApplyTransientPanelSettings(fresh);
                    _queuedSettings = fresh;
                    _queuedShape = _currentShape;
                    _queuedIsSingle = CloudOnlyCheck.IsChecked == true || MultiModeCheck.IsChecked != true;
                    return;
                }

                if (_isRunning)
                {
                    // 还在选范围：上面已把新值写进本轮快照，本轮直接按新选项继续，无需重开。
                    // 例外是「多对一」这种换流程的改动。
                    if (!flowChanged && _stage == RunStage.Picking) return;

                    // 内容对话框阶段：本轮口径已定型，改动留待下一轮，别把用户刚填的内容丢掉。
                    if (_stage == RunStage.Content) return;

                    // 其余情形（范围已定型、正在定位批注框）：中止本轮，按新选项干净重开。
                    _restartPending = true;
                    InteractionAbort.Request();
                    return;
                }

                // 面板还开着但上一轮已经结束。这里分两种情况：
                // ① 刚被"改选项"中止过（_restartPending）：按新选项重开一轮；
                // ② 普通待命：**刻意不**主动开新一轮 —— 等用户回图纸 / 点外形 / 按回车再启动，
                //    否则用户刚勾上第一个选项就被抢进命令上下文，面板随之点不动，第二个选项就勾不上了。
                if (_restartPending)
                {
                    _restartPending = false;
                    StartWithShape(_currentShape);
                    return;
                }

                PluginLog.Info("AnnotationPanel.OnOptionStartTick", "面板待命中：选项已更新，等用户回图纸开始。");
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.OnOptionStartTick", ex);
            }
        }

        /// <summary>给 CAD 发一次 Esc（等价于用户在命令行按 Esc），取消当前这一轮交互。
        /// <para>只在关闭面板时用：此时不会再自动重开，所以不存在"残留 Esc 误杀新一轮"的问题。
        /// 改绘制选项要走 <see cref="InteractionAbort"/> —— 命令行注入的 Esc 在提示挂起时不保证及时生效。</para></summary>
        private void CancelRunningAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null || doc.IsDisposed) return;
            doc.SendStringToExecute("\x1b", true, false, false);
        }

        /// <summary>面板显示后：贴到屏幕左上角、按内容高度自适应，并进入<b>待命</b>状态 ——
        /// 不立刻启动 CAD 命令（命令一跑起来鼠标就被 CAD 独占，面板就点不动了），
        /// 等用户把光标移回图纸 / 点图纸 / 按回车 / 点外形按钮，才按当前外形与选项开始。
        /// <para>所以"打开后先勾多对一 / 仅绘云线"是完全有效的：勾好后回到图纸即按新选项开始。</para></summary>
        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            // 面板刚露面：把"光标在图纸上"的连续计数清零，重新数（见 OnIdleWatchTick）。
            _idleDwellTicks = 0;
            try
            {
                Left = SystemParameters.WorkArea.Left + 20;
                Top = SystemParameters.WorkArea.Top + 20;
                // 窗口高度按内容自适应（XAML 里 SizeToContent="Height"）；这里只限制不超出屏幕
                // 可用区域，超出时内部 ScrollViewer 会自动出现滚动条。
                var maxHeight = SystemParameters.WorkArea.Height - 48;
                if (maxHeight > 240) MaxHeight = maxHeight;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.OnPanelLoaded", ex.Message);
            }

            if (_autoStarted) return;
            _autoStarted = true;

            // 空闲轮询与 Esc 钩子跟"设置窗口是否开着"无关，任何时候都要挂：
            // 它们自己会在计时器/回调里判断当前能不能启动。
            try { _idleWatch.Start(); }
            catch (Exception ex) { PluginLog.Warning("AnnotationPanel.OnPanelLoaded(idleWatch)", ex.Message); }
            InstallEscapeHook();

            // 「批注设置」窗口开着时不挂"光标进图纸"这条入口：用户是在配置，不是在画
            // （设置窗口压着的正好是图纸，鼠标划过它就可能莫名开始一轮）。关掉设置窗口时会重新挂上。
            if (SettingsWindow.IsOpen)
            {
                _panelLeaveUtc = DateTime.MinValue;
                return;
            }

            // 一打开就算作"刚从面板离开"：用户若直接把光标移到图纸上就是"接着去画"，不必再点一下。
            _panelLeaveUtc = DateTime.UtcNow;
            HookPointMonitor();
        }

        private void RefreshControls()
        {
            // 这里是程序性赋值，不能被当成"用户改了绘制选项"而触发重开。
            _suppressOptionEvents = true;
            try
            {
                DisciplineCombo.Text = _s.DefaultDiscipline;
                AuthorText.Text = _s.DefaultAuthor;
                CloudStyleCombo.Text = _s.CloudStyle;
                RoleCombo.Text = _s.DefaultRole;
                AutoNumberCheck.IsChecked = _s.AutoNumber;
                DoubleClickEditCheck.IsChecked = _s.DoubleClickEdit;

                // 恢复"上次的选择"（面板专用字段 Panel*，与快速直绘读的 CloudOnly/ContinuousAnnotation
                // 互不干扰）：外形 + 三个绘制方式勾选项。用户不必每次重开面板都重新点一遍。
                ContinuousCheck.IsChecked = _s.PanelContinuous;
                CloudOnlyCheck.IsChecked = _s.PanelCloudOnly;
                MultiModeCheck.IsChecked = _s.PanelMultiMode;
                // 外形放最后：SelectShapeButton 会按外形纠正非法组合（PL线/基点自动取消「多对一」等），
                // 必须在三个勾选项都恢复完之后再跑。
                //
                // ★ 外形一律回到「矩形」，**刻意不沿用上次的选择**（用户 2026-10-13 明确要求
                //   "进入批注面板后默认就是矩形"）。早先会记忆上次外形，结果试过一次「基点」之后
                //   长期是基点：中望/浩辰上的表现就是"进面板不是矩形，得先手动点一次矩形才能画"。
                //   _s.PanelShape 仍然照写不误（只写不读），免得设置文件结构变来变去。
                SelectShapeButton(DefaultShape);
                // 纠正后的组合与配置里存的不一致时（例如曾记住「基点 + 多对一」）立刻回写，
                // 否则每次打开都要再纠正一遍，用户看到的勾选状态与配置文件长期对不上。
                PersistPanelSelectionIfChanged();
            }
            finally
            {
                _suppressOptionEvents = false;
            }
        }

        /// <summary>把面板当前选择抄进设置对象的 <c>Panel*</c> 字段（不落盘，由调用方决定何时 Save）。
        /// <para><b>刻意不碰 <c>CloudOnly</c> / <c>ContinuousAnnotation</c></b>：那两项是"本轮绘制"的快照，
        /// 快速直绘（X_PZ_DRAW）读同一份设置，写进去会把它一起改成仅绘云线/连续绘制。</para></summary>
        private void CapturePanelSelectionInto(AnnotationSettings settings)
        {
            settings.PanelShape = _currentShape;
            settings.PanelContinuous = ContinuousCheck.IsChecked == true;
            settings.PanelCloudOnly = CloudOnlyCheck.IsChecked == true;
            settings.PanelMultiMode = MultiModeCheck.IsChecked == true;
        }

        /// <summary>面板的选择变了就落盘（"上次选择"的记忆），没变则一个字都不写。</summary>
        private void PersistPanelSelectionIfChanged()
        {
            if (_s.PanelShape == _currentShape &&
                _s.PanelContinuous == (ContinuousCheck.IsChecked == true) &&
                _s.PanelCloudOnly == (CloudOnlyCheck.IsChecked == true) &&
                _s.PanelMultiMode == (MultiModeCheck.IsChecked == true))
            {
                return;
            }

            CapturePanelSelectionInto(_s);
            try { SettingsStore.Save(_s); }
            catch (Exception ex) { PluginLog.Warning("AnnotationPanel.PersistPanelSelection", ex.Message); }
        }

        /// <summary>把面板上"只影响本次批注"的开关写进设置快照（不落盘）。</summary>
        private void ApplyTransientPanelSettings(AnnotationSettings settings)
        {
            // 「仅绘云线」是本次开关：不写回 settings.xml，否则快速直绘（X_PZ_DRAW
            // 读同一份设置）也会跟着变成仅绘云线。
            settings.CloudOnly = CloudOnlyCheck.IsChecked == true;
            // 「连续绘制」同样是本次开关（每次打开默认不勾选），只进本次快照。
            settings.ContinuousAnnotation = ContinuousCheck.IsChecked == true;
        }

        /// <summary>点击开始时，将面板当前值一次性写回设置并持久化。
        /// <para>注意：<c>CloudOnly</c> 与 <c>ContinuousAnnotation</c>（多对一模式由 isSingle 决定）都是
        /// "本次开关"，**刻意不写入**，否则会污染快速直绘等其它读同一份设置的入口；
        /// 面板自己的"上次选择"另存到 <c>Panel*</c> 字段（见 <see cref="CapturePanelSelectionInto"/>）。</para></summary>
        private void ApplyPanelSettings()
        {
            _s.DefaultDiscipline = DisciplineCombo.Text.Trim();
            _s.DefaultAuthor = AuthorText.Text.Trim();
            _s.CloudStyle = CloudStyleCombo.Text.Trim();
            _s.DefaultRole = RoleCombo.Text.Trim();
            _s.AutoNumber = AutoNumberCheck.IsChecked == true;
            _s.DoubleClickEdit = DoubleClickEditCheck.IsChecked == true;
            // 落盘前显式清零"本次开关"，确保任何历史遗留值都不会被写进设置文件。
            _s.CloudOnly = false;
            _s.ContinuousAnnotation = false;
            // 记住面板这次的选择（外形 + 三个绘制方式勾选项），下次打开面板时恢复。
            CapturePanelSelectionInto(_s);
            SettingsStore.Save(_s);
        }

        /// <summary>插件卸载时调用：关掉面板并**确保摘掉 Esc 键盘钩子**。
        /// <para><b>为什么必须做</b>：Esc 钩子的回调是托管委托。若宿主把插件程序集卸掉
        /// （.NET 8 宿主用的 collectible AssemblyLoadContext）而钩子还挂在系统里，
        /// 下一次按键就会跳进已卸载的代码 —— 那是进程级崩溃，且现场完全看不出跟插件有关。</para></summary>
        internal static void ShutdownForUnload()
        {
            var panel = _instance;
            if (panel == null) return;
            try
            {
                if (panel.IsLoaded) panel.Close();
                else panel.RemoveEscapeHook();
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.ShutdownForUnload", ex.Message);
                try { panel.RemoveEscapeHook(); } catch { }
            }
            _instance = null;
        }

        /// <summary>显示或激活批注功能选择框（单例）。任何异常都记日志并提示，避免冒泡成 CAD 致命错误。</summary>
        public static void ShowOrActivate()
        {
            try
            {
                // 用 IsReadyToStart（IsLoaded || IsVisible）而不是裸 IsLoaded：
                // 个别宿主上 IsLoaded 迟迟不为 true，只判它会让每次 X_PZ_NOTE 都新建一个面板实例，
                // 旧实例的状态（排队、本轮快照）全丢，且界面上会叠出第二个框。
                if (_instance != null && _instance.IsReadyToStart) { _instance.Activate(); return; }
                PluginLog.Info("AnnotationPanel.ShowOrActivate", "新建批注功能选择框。");
                _instance = new AnnotationPanel();
                _instance.Show();
                // 明确激活：面板要拿着焦点，用户"点图纸 → 开始画"这条路径（面板失活）才成立。
                _instance.Activate();
            }
            catch (Exception ex)
            {
                _instance = null;
                PluginLog.Error("AnnotationPanel.ShowOrActivate", ex);
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n打开批注功能选择框失败: " + ex.Message);
            }
        }

        /// <summary>点任一外形按钮：先把它标成当前外形（蓝底），然后立刻按"当前外形 + 当前选项"开始一轮
        /// （不想把光标移回图纸、也懒得按回车时的入口）。
        /// <para>换外形＝<b>换整套流程</b>（矩形框选 / PL 折线 / 基点），因此按"流程改了"处理：
        /// 本轮还在跑时会先中止它，再按新外形干净重开，不会出现"按钮蓝底是 PL线、本轮却还是框选"。</para>
        /// <para><b>为什么不走 <see cref="NotifyOptionsChanged"/></b>：那条路是"防抖 500ms 后再决定要不要中止本轮"，
        /// 而本方法紧接着就用 200ms 的启动延时把新的一轮开起来了 —— 于是 500ms 时那次迟到的中止正好掐在
        /// <b>刚起来的这一轮</b>上。症状就是"点「PL线」后提示立刻被取消、命令行还报一次错"（本轮被中止时
        /// Jig 自行从 Sampler 返回 Cancel，AutoCAD 对这种情况不返回 PromptResult 而返回 null）。
        /// 所以这里直接同步做掉：清掉迟到的防抖，该中止就中止，否则立刻开。</para></summary>
        private void Shape_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var shape = (sender as Button)?.Tag as string;
                PluginLog.Info("AnnotationPanel.ShapeClick",
                    "点击外形按钮「" + (shape ?? "(空)") + "」：running=" + _isRunning
                    + " pending=" + _startPending + " closing=" + _closing
                    + " IsLoaded=" + IsLoaded + " IsVisible=" + IsVisible + "。");
                SelectShapeButton(shape);

                // 清掉"选项变更"的防抖与流程变更标志，避免 500ms 后再来一次中止。
                _flowChanged = false;
                if (_optionStartTimer != null) _optionStartTimer.Stop();
                // 外形写进本轮快照（若本轮已在跑，配合下面的中止一起生效）。
                ApplyLivePanelOptions();

                if (_isRunning)
                {
                    // 正在跑：中止它。StartAnnotation 的 finally 会按 _restartPending 用 _currentShape
                    // （已是新外形）重开一轮，不需要这里再排队。
                    PluginLog.Info("AnnotationPanel.ShapeClick", "本轮进行中，按新外形中止后重开。");
                    _restartPending = true;
                    InteractionAbort.Request();
                    return;
                }

                StartByTrigger("点外形按钮");
            }
            catch (Exception ex)
            {
                // 这里必须既写日志又写命令行：面板上的按钮点下去"没反应"时，
                // 只有日志能证明事件到底有没有进来、异常发生在哪一步。
                PluginLog.Error("AnnotationPanel.Shape_Click", ex);
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n启动批注失败: " + ex.Message);
            }
        }

        /// <summary>以指定外形开始一次批注；外形为空/非法时退回默认矩形。</summary>
        private void StartWithShape(string shape)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shape))
                {
                    shape = DefaultShape;
                }

                // 任何一次真正的启动都取消掉"等重开"的挂起请求，避免紧接着又开一轮；
                // 并且摘掉"光标进图纸"的监听（本轮已经起来，不再需要）。
                _restartPending = false;
                UnhookPointMonitor();
                SelectShapeButton(shape);
                QueueAnnotationCommand(shape);
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.StartWithShape", ex);
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n启动批注失败: " + ex.Message);
            }
        }

        /// <summary>把某个外形标成当前外形：被选中的按钮用蓝色（PrimaryButton），其余用浅色（SecondaryButton）。
        /// <para>顺带处理外形与绘制选项的既定组合（见 <see cref="EnforceShapeOptionConflicts"/>）。</para></summary>
        private void SelectShapeButton(string shape)
        {
            var current = ResolveShapeToken(shape);
            _currentShape = current;
            ApplyShapeButtonStyle(ShapeRectButton, current == ShapeRect);
            ApplyShapeButtonStyle(ShapePlineButton, current == ShapePline);
            ApplyShapeButtonStyle(ShapeBasePointButton, current == ShapeBasePoint);
            EnforceShapeOptionConflicts(current);
        }

        /// <summary>把按钮 Tag（或任何来源的字符串）归一到三个外形令牌之一，认不出来退回「矩形」。</summary>
        private static string ResolveShapeToken(string shape)
        {
            if (shape == ShapePline) return ShapePline;
            if (shape == ShapeBasePoint) return ShapeBasePoint;
            return ShapeRect;
        }

        /// <summary>外形与绘制选项的既定组合——不成立的组合直接取消勾选，让用户看得见，
        /// 而不是"勾了却不生效"：
        /// <list type="bullet">
        /// <item>「多对一批注」只适用于「矩形」外形（按批注设置的外形框选多个范围）；
        /// PL线 / 基点都是单区域绘制。</item>
        /// <item>「仅绘云线」在「基点」外形下没有云线可画（该外形只有基点+引线+文本框）。</item>
        /// </list></summary>
        private void EnforceShapeOptionConflicts(string shape)
        {
            var wasSuppressed = _suppressOptionEvents;
            _suppressOptionEvents = true;
            try
            {
                if (shape != ShapeRect && MultiModeCheck.IsChecked == true) MultiModeCheck.IsChecked = false;
                if (shape == ShapeBasePoint && CloudOnlyCheck.IsChecked == true) CloudOnlyCheck.IsChecked = false;
            }
            finally
            {
                _suppressOptionEvents = wasSuppressed;
            }
        }

        /// <summary>当前该用的云线外形：「矩形」外形＝跟随「批注设置」里选的云线外形（矩形/菱形/椭圆）。
        /// <para>设置里只会是这三个值；取不到时按矩形兜底。外形<b>不写回</b> settings.xml（只进本次快照），
        /// 免得污染快速直绘 <c>X_PZ_DRAW</c> 读的同一份设置。</para></summary>
        private string CloudShapeForPanel()
        {
            var shape = _s?.Shape;
            if (shape == "菱形" || shape == "椭圆") return shape;
            return "矩形";
        }

        /// <summary>切换外形按钮的底色。资源缺失时保持原样式，不影响功能。</summary>
        private void ApplyShapeButtonStyle(Button button, bool selected)
        {
            if (button == null) return;
            try
            {
                button.Style = (Style)FindResource(selected ? "PrimaryButton" : "SecondaryButton");
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.ShapeButtonStyle", ex.Message);
            }
        }

        /// <summary>由 CAD 内部命令（X_PZ_RUN）调用，确保所有 Editor 选点都运行在正式命令上下文。
        /// <para>每个"放弃执行"的分支都必须<b>先</b>调用 <see cref="ResetPendingStart"/> 再返回：
        /// <c>_startPending</c> 一旦留在 true，面板上后续所有入口（外形按钮 / 回车 / 点图纸）
        /// 都会被 <see cref="StartByTrigger"/> 静默挡掉 —— 现场表现就是"面板按钮点了完全没反应"。</para></summary>
        internal static void RunQueuedAnnotation()
        {
            var panel=_instance;
            if(panel==null){PluginLog.Warning("X_PZ_RUN","面板实例已不存在，忽略本次调用。");return;}
            if(!panel._startPending){PluginLog.Warning("X_PZ_RUN","面板没有待执行的启动请求，忽略本次调用。");return;}
            var queuedDocument = panel._queuedDocument;
            if (queuedDocument == null || queuedDocument.IsDisposed)
            {
                PluginLog.Warning("X_PZ_RUN","排队的图纸已关闭，放弃本次启动。");
                panel.ResetPendingStart();
                return;
            }

            // 旧图纸队列中的延迟命令不能消费另一张图纸的新请求。
            if (CadApplication.DocumentManager.MdiActiveDocument != queuedDocument)
            {
                PluginLog.Warning("X_PZ_RUN","当前图纸已不是排队时的那一篇，放弃本次启动。");
                panel.ResetPendingStart();
                return;
            }

            var settings = panel._queuedSettings;
            var isSingle = panel._queuedIsSingle;
            var shape = panel._queuedShape;
            panel.ResetPendingStart();

            if (settings == null)
            {
                PluginLog.Warning("X_PZ_RUN","排队参数缺失（settings 为空），放弃本次启动。");
                return;
            }

            PluginLog.Info("X_PZ_RUN", "开始执行排队的批注：外形=" + (shape ?? "(默认)")
                + " 单区域=" + isSingle + "。");
            panel.StartAnnotation(
                queuedDocument,
                settings,
                isSingle,
                shape);
        }

        /// <summary>切换图纸时把"光标进图纸"的监听挪到新图纸，并取消旧图纸尚未执行的面板请求。</summary>
        internal static void HandleDocumentActivated(Document activeDocument)
        {
            var panel = _instance;
            if (panel == null || !panel.IsLoaded) return;
            panel.RehookPointMonitor();
            if (panel._startPending && panel._queuedDocument != activeDocument) panel.ResetPendingStart();
        }

        private void ResetPendingStart()
        {
            _startPending = false;
            _queuedDocument = null;
            _queuedSettings = null;
            _queuedShape = null;
            _pendingSinceUtc = default(DateTime);
        }

        /// <summary>看门狗：排队的 X_PZ_RUN 若没被宿主执行，换一条入口再试一次。
        /// <para><b>为什么需要</b>：<c>Document.SendStringToExecute</c> 在浩辰 GstarCAD 2026 上会被静默丢弃 ——
        /// 2026-10-13 的实机日志里，中望与 AutoCAD 排完队都会在同一秒出现
        /// <c>[X_PZ_RUN] 命令上下文已进入。</c>，而浩辰那几次<b>一条都没有</b>；
        /// 于是面板停"已排队"状态，之后点矩形/PL线/基点全被"忽略…pending=True"挡掉
        /// （用户反馈的"浩辰控制面板里的矩形、PL线、基点都画不出来"）。</para>
        /// <para>兜底入口 <c>Editor.Command(object[])</c> 是 acedCommand 的托管版，在当前线程同步进入命令上下文：
        /// 浩辰、中望、AutoCAD 2015+ 都有（AutoCAD 2013/2014 没有，所以只能反射调用，
        /// 这也避免把 net4 目标绑死在 2024 才有的 API 上）。没有它时退回以 <c>\n</c> 结尾再发一次 —— 部分宿主只认换行作回车。</para></summary>
        private void OnRunWatchdogTick(object sender, EventArgs e)
        {
            _runWatchdog.Stop();
            if (_closing || !_startPending || _isRunning) return;   // 已被 X_PZ_RUN 消费 = 正常路径成功
            _runRetry++;

            var doc = _queuedDocument;
            if (doc == null || doc.IsDisposed || CadApplication.DocumentManager.MdiActiveDocument != doc)
            {
                PluginLog.Warning("AnnotationPanel.RunWatchdog", "排队的图纸已不可用，放弃本次启动。");
                ResetPendingStart();
                return;
            }

            try
            {
                if (_runRetry > MaxRunRetry)
                {
                    PluginLog.Warning("AnnotationPanel.RunWatchdog",
                        "已重发 " + MaxRunRetry + " 次宿主仍未消费 X_PZ_RUN，放弃本次启动"
                        + "（可直接用命令名 X_PZ_NOTE / X_PZ_DRAW）。");
                    ResetPendingStart();
                    return;
                }

                var age = _pendingSinceUtc == default(DateTime)
                    ? RunWatchdogMs / 1000.0
                    : (DateTime.UtcNow - _pendingSinceUtc).TotalSeconds;
                PluginLog.Warning("AnnotationPanel.RunWatchdog",
                    "排队的 X_PZ_RUN 在 " + age.ToString("0.#") + " 秒内未被宿主执行，"
                    + "第 " + _runRetry + "/" + MaxRunRetry + " 次兜底。");

                // 第一次兜底优先走 Editor.Command：它是 acedCommand 的托管版，在当前线程**同步**进入
                // 命令上下文，不经过宿主的命令行输入缓冲，是这条链上最可靠的一环。
                // 拿不到这个 API（AutoCAD 2013/2014）或它自身失败时，再退回重发。
                if (_runRetry == 1 && TryRunByEditorCommand(doc))
                {
                    // Editor.Command 是同步的：返回时这一轮要么已经跑完、要么已经抛异常，
                    // 无论如何 _startPending 的使命都结束了。
                    return;
                }

                doc.SendStringToExecute("X_PZ_RUN\n", true, false, false);
                _pendingSinceUtc = DateTime.UtcNow;
                // 继续盯：宿主如果还是不消费，下一拍再发一次（最多 MaxRunRetry 次）。
                _runWatchdog.Start();
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.RunWatchdog", ex);
                ResetPendingStart();
            }
        }

        /// <summary>用 <c>Editor.Command(object[])</c> 兜底执行 X_PZ_RUN；宿主没有这个 API 时返回 false。
        /// <para>走反射而不是直接调用：net4 目标是拿 AutoCAD 2013/2014 的托管程序集编的，
        /// 而那两个版本的 <c>Editor</c> 没有 <c>Command</c>（AutoCAD 2015+ 才有），
        /// 直接写就编不过；反射让同一份源码在四个宿主上都成立。</para>
        /// <para>失败时把 <b>InnerException</b> 一并写出来：COM / 宿主侧抛出的真实原因
        /// （例如 <c>eNotInCommand</c>）只在里层，外面永远只有一句"调用的目标发生了异常"。</para></summary>
        private static bool TryRunByEditorCommand(Document doc)
        {
            try
            {
                var editor = doc.Editor;
                var method = editor.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => string.Equals(m.Name, "Command", StringComparison.Ordinal)
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(object[]));
                if (method == null) return false;

                PluginLog.Info("AnnotationPanel.RunWatchdog", "用 Editor.Command 兜底执行 X_PZ_RUN。");
                method.Invoke(editor, new object[] { new object[] { "X_PZ_RUN" } });
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.RunWatchdog(Editor.Command)",
                    ex.GetType().Name + " - " + ex.Message
                    + (ex.InnerException != null
                        ? " <- " + ex.InnerException.GetType().Name + " - " + ex.InnerException.Message
                        : string.Empty));
                return false;
            }
        }

        /// <summary>排队一次批注：从面板读取外形与勾选项，写入设置后由 X_PZ_RUN 在命令上下文执行。</summary>
        private void QueueAnnotationCommand(string shape)
        {
            QueueAnnotationCommand(shape, false);
        }

        private void QueueAnnotationCommand(
            string shape,
            bool fromContinuous)
        {
            if (_isRunning && !fromContinuous)
            {
                // 正常路径下不会走到这里（面板改选项走 InteractionAbort 重开，不直接排队）。
                // 真到了这里说明状态对不上，记一条日志便于排查，别静默吞掉。
                PluginLog.Warning("AnnotationPanel.QueueAnnotationCommand", "已有绘制在进行中，本次排队被忽略。");
                return;
            }

            try
            {
                // 外形令牌（矩形 / PL线 / 基点）：本轮走哪条流程由它决定（见 StartAnnotation）。
                shape = ResolveShapeToken(shape);
                AnnotationSettings settings;

                if (fromContinuous)
                {
                    // 连续绘制复用上一次的设置快照与模式，不再重读面板。
                    settings = _runSettings?.Clone();
                    shape = _runShape;
                }
                else
                {
                    ApplyPanelSettings();
                    settings = _s.Clone();
                    // 云线外形：取「批注设置」里的值（面板上「矩形」按钮＝跟随设置）。
                    // 外形只进本次快照，**不写回 _s**：否则自动编号落图时 SettingsStore.Save(_s)
                    // 会把外形一并存进 settings.xml，快速直绘（X_PZ_DRAW 读同一份设置）也会跟着变形状。
                    settings.Shape = CloudShapeForPanel();
                    ApplyTransientPanelSettings(settings);
                }

                if (settings == null)
                {
                    return;
                }

                // 「仅绘云线」优先于「多对一」：两个都勾着时按仅绘云线走（属单区域流程），
                // 面板里勾选仅绘云线也会顺手把多对一取消掉，这里再兜一道，避免任何遗留状态绕过 UI。
                var isSingle = CloudOnlyCheck.IsChecked == true || MultiModeCheck.IsChecked != true;

                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    Close();
                    return;
                }

                // 自愈要在这里做一次：本方法也可能由"焦点回图纸"之外的路径直接调用（连续绘制重排队等）。
                ClearStalePending();
                if (_startPending && _queuedDocument == doc)
                {
                    PluginLog.Info("AnnotationPanel.Queue", "已有同一张图纸的排队请求未执行，忽略本次重复排队。");
                    return;
                }

                _queuedDocument = doc;
                _queuedSettings = settings;
                _queuedIsSingle = isSingle;
                _queuedShape = shape;
                _startPending = true;
                _pendingSinceUtc = DateTime.UtcNow;
                _runRetry = 0;
                // ★ 以换行结尾，不用尾随空格。
                //   2026-10-13 的实机日志把这条钉死了：排队的写法是 "X_PZ_RUN "（尾随空格），
                //   看门狗 0.6 s 后判定"未被宿主执行"；兜底改成 "X_PZ_RUN\n" 的那一次，
                //   同一秒就出现了 [X_PZ_RUN] 命令上下文已进入。空格与换行虽然都能提交命令名，
                //   但 SendStringToExecute 在 CAD 正忙（面板刚 Show / 焦点切换）时，
                //   结尾不是回车的那一条更容易被丢掉。
                try { doc.SendStringToExecute("X_PZ_RUN\n", true, false, false); }
                catch (Exception sendEx) { PluginLog.Error("AnnotationPanel.Queue(send)", sendEx); }
                PluginLog.Info("AnnotationPanel.Queue",
                    "已把 X_PZ_RUN 排入命令队列（外形=" + shape + " 单区域=" + isSingle + "）。");
                // 布好看门狗：宿主没执行这条命令就换一条入口再试（见 OnRunWatchdogTick）。
                _runWatchdog.Stop();
                _runWatchdog.Start();
            }
            catch (Exception ex)
            {
                ResetPendingStart();
                PluginLog.Error("AnnotationPanel.QueueAnnotationCommand", ex);
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n无法启动批注命令: " + ex.Message);
            }
        }

        /// <summary>启动批注流程：按"外形令牌 + 面板选项"分派到对应的绘制流程，进入 CAD 交互。
        /// <para>外形决定整套流程：「矩形」＝按批注设置的外形框选（可单区域 / 多对一）；
        /// 「PL线」＝逐点画折线后沿其生成云线；「基点」＝只点一个基点再连引线（无云线）。</para></summary>
        private void StartAnnotation(
            Document doc,
            AnnotationSettings settings,
            bool isSingle,
            string shape)
        {
            if (_isRunning)
            {
                PluginLog.Warning("AnnotationPanel.StartAnnotation", "已有绘制在进行中（_isRunning 未复位），忽略本次启动。");
                return;
            }

            PluginLog.Info("AnnotationPanel.StartAnnotation",
                "进入绘制流程：外形=" + (shape ?? "(默认)") + " 单区域=" + isSingle + "。");
            _isRunning = true;
            _runSettings = settings;
            _runIsSingle = isSingle;
            _runShape = ResolveShapeToken(shape);
            // 新一轮开始：先清掉上一轮可能残留的中止请求，否则这一轮刚起来就会被立刻取消。
            InteractionAbort.Clear();
            _userCancelled = false;
            _stage = RunStage.Picking;

            try
            {
                if (doc == null ||
                    doc.IsDisposed ||
                    CadApplication.DocumentManager.MdiActiveDocument != doc)
                {
                    return;
                }

                // 启动这一轮的那一下点击（面板失活触发）在"图纸上没有命令"时可能已经拉出过选择集，
                // 先清掉：否则批注画完了图上还留着一片高亮实体。
                try
                {
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                catch (Exception clearEx)
                {
                    PluginLog.Warning("AnnotationPanel.StartAnnotation(clearSelection)", clearEx.Message);
                }

                // 按外形分派：PL线 / 基点 是各自独立的流程；「矩形」＝按批注设置的外形框选，
                // 勾了「多对一」时走多区域流程，否则单区域。
                if (_runShape == ShapePline)
                {
                    DoPlineAnnotation(doc);
                }
                else if (_runShape == ShapeBasePoint)
                {
                    DoBasePointAnnotation(doc);
                }
                else if (isSingle)
                {
                    DoSingleAnnotation(doc);
                }
                else
                {
                    DoMultiAnnotation(doc);
                }
            }
            catch (Exception ex)
            {
                // 菱形/椭圆曾报"致命错误"：这里把真实异常落日志，便于定位，
                // 而不是让它冒泡成 CAD 的致命错误对话框。
                PluginLog.Error("AnnotationPanel.StartAnnotation(shape=" + shape + ")", ex);
                var activeDocument = CadApplication.DocumentManager.MdiActiveDocument;
                activeDocument?.Editor.WriteMessage("\n批注失败: " + ex.Message);
            }
            finally
            {
                _isRunning = false;
                // 被中止的这一轮到此为止：清掉中止标志，免得它残留下去误杀紧接着重开的新一轮。
                InteractionAbort.Clear();
                _stage = RunStage.Idle;

                // "用户自己取消的"只对本轮有效：读到本地变量后立刻清零（下面每条分支都可能 return）。
                var cancelledByUser = _userCancelled;
                _userCancelled = false;

                // 绘制期间用户改过绘制选项（如中途勾上"仅绘云线"）→ 按新选项重开，优先于连续绘制。
                // 注意：这里**不能**用 return，finally 里 return 会吞掉异常。
                if (_restartPending && IsLoaded && !_closing)
                {
                    // 保持 _restartPending 为 true：真正的重开在防抖到点后由 OnOptionStartTick 执行
                    // （给刚被中止的那一轮留一点时间把命令行输出落定）。
                    _continuePending = false;
                    ScheduleOptionStart();
                }
                else if (cancelledByUser && IsLoaded && !_closing)
                {
                    // 用户按 Esc / 点了取消：本轮不再继续（也不管连续绘制开没开），并且**退出批注**（关面板）。
                    // 这正是需求"批注完成后或者不需要批注的时候按 esc 要能退出批注" —— 只把本轮停掉不够，
                    // 面板还开着、连续绘制还记着，用户就会觉得"按了 Esc 没用"。
                    _continuePending = false;
                    PluginLog.Info("AnnotationPanel.StartAnnotation", "本轮由用户取消，退出批注并关闭面板。");
                    ClosePanel();
                }
                else if (_continuePending && IsLoaded && !_closing)
                {
                    _continuePending = false;
                    QueueAnnotationCommand(_runShape, true);
                }
                else
                {
                    _runSettings = null;
                    _runShape = null;
                    // 这一轮到此结束而面板还开着（例如用户按 Esc 取消了）：把"光标刚从面板离开"的时限作废，
                    // 否则用户一边看着图纸一边按 Esc 取消时，光标还停在图纸上，会被立刻判定成"又要开始"。
                    // 想再开始：回面板点一下（或点外形按钮 / 按回车）。
                    _panelLeaveUtc = DateTime.MinValue;
                    // 同理：本轮作废之后不允许空闲轮询再自己开一轮。
                    // 轮询的放行条件是 _panelLeaveUtc 有效（见 OnIdleWatchTick），
                    // 上面这一句把它清成 MinValue 就等于把轮询按住 —— 直到用户重新回到面板
                    // （Activated / MouseLeave 会重新盖章），这样"按 Esc 取消"不会被立刻重开覆盖。
                    _idleDwellTicks = 0;
                }
            }
        }

        /// <summary>打开「批注设置」。窗口是<b>非模态</b>的，与面板各自独立：可分别拖动、分别关闭，
        /// 点谁谁在前（<see cref="WpfWindowUtil.BringToTop"/>）。
        /// <para>原先走的是 CAD 原生模态 <c>ShowModalWindow</c>：模态会禁用其它顶层窗口，
        /// 于是设置窗口一开，面板既点不动、也不能单独关，必须先退出设置窗口 —— 正是要修掉的问题。</para>
        /// <para>新窗口摆在面板右侧（面板固定在屏幕左上角），两个窗口同时可见。</para></summary>
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                var styles = GetTextStyles(doc);
                var w = SettingsWindow.ShowOrActivate(_s, styles, this);
                if (w == null) return;

                // 订阅"保存后刷新面板"，同一窗口只订一次（窗口被复用时不重复挂钩子）。
                if (!ReferenceEquals(_settingsWindow, w))
                {
                    if (_settingsWindow != null) _settingsWindow.Saved -= OnSettingsSaved;
                    w.Saved -= OnSettingsSaved;
                    w.Saved += OnSettingsSaved;
                    w.Closed -= OnSettingsWindowClosed;
                    w.Closed += OnSettingsWindowClosed;
                    _settingsWindow = w;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("AnnotationPanel.Settings_Click", ex);
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n打开批注设置失败: " + ex.Message);
            }
        }

        /// <summary>设置窗口保存后：按新的设置刷新面板控件（批注人 / 云线样式 / 编号等）。</summary>
        private void OnSettingsSaved()
        {
            try
            {
                if (_settingsWindow != null) _s = _settingsWindow.Value;
                RefreshControls();
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.OnSettingsSaved", ex.Message);
            }
        }

        /// <summary>设置窗口关闭后：面板若还开着且没在跑，重新进入"刚从面板出来"——
        /// 用户接着把光标移回图纸即可开始（设置窗口开着期间这条入口是刻意关掉的）。</summary>
        private void OnSettingsWindowClosed(object sender, EventArgs e)
        {
            try
            {
                if (_closing || !IsLoaded || _isRunning) return;
                _panelLeaveUtc = DateTime.UtcNow;
                HookPointMonitor();
            }
            catch (Exception ex)
            {
                PluginLog.Warning("AnnotationPanel.OnSettingsWindowClosed", ex.Message);
            }
        }

        // ═══════════════ 单独批注 ═══════════════

        /// <summary>本轮批注的业务数据：专业/批注人/角色取自设置；开了自动编号时取下一个编号。</summary>
        private AnnotationData NewAnnotationData(Document doc)
        {
            var settings = _runSettings;
            var data = new AnnotationData
            {
                Author = settings.DefaultAuthor,
                Discipline = settings.DefaultDiscipline,
                Role = settings.DefaultRole
            };
            if (settings.AutoNumber)
            {
                settings.NextNumber = AnnotationService.GetNextNumber(doc);
                data.Number = "X-" + settings.NextNumber.ToString("D3");
            }

            return data;
        }

        /// <summary>「矩形」外形（＝按批注设置里的云线外形：矩形/菱形/椭圆）单区域流程。</summary>
        private void DoSingleAnnotation(Document doc)
        {
            // 云线外形由 settings.Shape 驱动（取自「批注设置」），单区域流程统一走 DoRegionAnnotation。
            DoRegionAnnotation(doc, NewAnnotationData(doc));
        }

        /// <summary>「PL线」外形：在图纸上逐点画出闭合折线，云线沿该自定义形状生成，引线接在该轮廓上。</summary>
        private void DoPlineAnnotation(Document doc)
        {
            DoPlineCloud(doc, NewAnnotationData(doc));
        }

        /// <summary>「基点」外形：点一个基点 → 填内容 → 拖放文字框；引线从基点直连到文本框，<b>不画云线</b>。</summary>
        private void DoBasePointAnnotation(Document doc)
        {
            var settings = _runSettings;
            var data = NewAnnotationData(doc);

            if (!AnnotationService.PromptBasePoint(doc, out var basePoint))
            {
                if (!InteractionAbort.Requested)
                {
                    doc.Editor.WriteMessage("\n已取消基点选择。");
                    MarkUserCancelled();
                }
                return;
            }

            // 基点批注没有云线范围可作"字号自适应"的参照，这里用"当前视图高度的 20%"当参照——
            // 相当于用户在这个视图下常见的一个框选范围大小，字号观感与框选流程一致；
            // 没开字体自适应时该参照不参与（仍按"打印字高 × 出图比例"算）。
            var refSize = 0.0;
            try
            {
                using (var view = doc.Editor.GetCurrentView()) refSize = view.Height * BasePointAutoFitViewFraction;
            }
            catch (Exception viewEx)
            {
                PluginLog.Warning("AnnotationPanel.DoBasePointAnnotation(view)", viewEx.Message);
            }

            // captureRenderSettings 必须是 true：把"图面实测值"（字高/首行字高/次行字高/云线半径/线宽）
            // 记进批注数据。漏了它，data.RenderTextHeight 会一直是 0，之后**任何一次编辑**
            // （双击 → X_PZ_EDIT → Update）都会走 SettingsForExisting 的兜底分支去读设置文件里的
            // **打印 mm**（如 3），FormatText 随即写出内联字高码 \H3;，正文立刻被压成原尺寸的
            // 1/出图比例分母（1:100 就是小 100 倍）——这正是"基点批注双击改完文字变得很小"的根因。
            // 矩形/PL 两条流程一直传 true（见 DoPlineCloud / 多对一），只有这里漏了。
            var effective = AnnotationService.ResolveEffectiveSettings(doc, settings, data, refSize, true);

            // 需求：点完基点要**先看见基点落在哪儿**，再弹内容窗口。
            // 这里先画一个临时标识（真实实体，画完即擦），用户在填内容时就能确认位置对不对；
            // 正式标识仍由 CreateBasePoint 生成，所以临时的那份绝不能留到最后（否则两个符号叠在一起）。
            var markerIds = AnnotationService.DrawTemporaryBasePointMarker(doc, basePoint, effective);

            bool contentOk;
            try
            {
                _stage = RunStage.Content;
                contentOk = CadDialog.ShowAnnotation(data, false);
            }
            catch (Exception dialogEx)
            {
                PluginLog.Error("AnnotationPanel.DoBasePointAnnotation.ShowAnnotation", dialogEx);
                contentOk = false;
            }
            finally
            {
                // 窗口一关就擦掉：接下来的拖放定位由放置预览 Jig 按最终尺寸重画同一个标识，中间不会出现空档。
                AnnotationService.EraseTemporaryEntities(doc, markerIds);
            }

            if (!contentOk)
            {
                doc.Editor.WriteMessage("\n已在填写内容阶段取消基点批注。");
                MarkUserCancelled();
                return;
            }

            _stage = RunStage.Elsewhere;
            var placementResult = AnnotationService.PromptPlacement(
                doc,
                effective,
                settings,
                null,
                null,
                null,
                basePoint,
                PlacementGeometryKind.BasePoint,
                data,
                basePoint);
            if (!AcceptInteraction(doc, placementResult)) return;

            AnnotationService.CreateBasePoint(doc, data, effective, basePoint, placementResult.Point);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        private void DoPlineCloud(Document doc, AnnotationData data)
        {
            var points = CollectPlinePoints(doc, _runSettings, out var cancelled);
            if (cancelled || points == null)
            {
                if (cancelled) MarkUserCancelled();
                return;
            }
            var ucsMatrix=AnnotationService.GetUcsMatrix(doc);var wcsToUcs=ucsMatrix.Inverse();var localPoints=points.Select(p=>p.TransformBy(wcsToUcs)).ToList();
            var polygonPoints=localPoints.Select(p=>new Point2d(p.X,p.Y)).ToList();
            if(!AnnotationService.ValidateCloudPolygon(polygonPoints,out var validationError)){doc.Editor.WriteMessage("\n"+validationError);return;}
            var xs=localPoints.Select(p=>p.X);var ys=localPoints.Select(p=>p.Y);
            var w = xs.Max() - xs.Min(); var h = ys.Max() - ys.Min();
            var diagonal = Math.Sqrt(w * w + h * h);
            var effective = AnnotationService.ResolveEffectiveSettings(doc, _runSettings, data, diagonal, true);

            // 仅绘云线模式：沿折线创建云线后即结束
            if (_runSettings.CloudOnly)
            {
                AnnotationService.CreatePlineCloudOnly(doc,effective,points);
                doc.Editor.WriteMessage("\nPL 云线已创建。");
                CheckContinuous(doc);
                return;
            }
            // 完整 PL 批注：先填写内容，再定位批注框（预览按真实内容显示文字与框体）并落图。
            if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线范围: {w:0.#}×{h:0.#}  字高: {effective.TextHeight:0.###}  云线半径: {effective.CloudRadius:0.###}");
            // 折线已定：本轮的外形与尺寸自此定型（后续步骤不再受面板改动影响，见 RunStage）。
            _stage = RunStage.Content;
            if (!CadDialog.ShowAnnotation(data, false)){doc.Editor.WriteMessage("\n已在填写内容阶段取消 PL 批注。");MarkUserCancelled();return;}
            _stage = RunStage.Elsewhere;
            var placementResult=AnnotationService.PromptPlacement(doc,effective,_runSettings,null,null,points,points[points.Count-1],PlacementGeometryKind.Polygon,data);if(!AcceptInteraction(doc,placementResult))return;var textPointWcs=placementResult.Point;
            AnnotationService.CreatePlineCloud(doc, data, effective, points, textPointWcs);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        private void DoRegionAnnotation(Document doc, AnnotationData data)
        {
            // 直接把"本轮设置实例"交给交互层（不传副本）：面板在选范围期间改了外形/选项时，
            // 拖拽预览每帧重新解析它就会立刻跟着变，落图也用同一份口径。
            var settings = _runSettings;

            // 先选范围、再判断是否"仅绘云线"：这样提示挂起时勾上「仅绘云线」也能立刻生效，
            // 不必退出重来（面板里勾选绘制选项因此不用再点一次外形）。
            if (!AnnotationService.PromptGeometry(doc, settings, out var f, out var s))
            {
                if (!InteractionAbort.Requested)
                {
                    doc.Editor.WriteMessage("\n已取消单区域范围选择。");
                    MarkUserCancelled();
                }
                return;
            }

            // 范围已定：本轮的外形与尺寸自此定型（后面统一用快照 beffective）。
            _stage = RunStage.Content;
            var (_,bw,bh)=AnnotationService.UcsAlignedExtents(doc,f,s);
            var bdiagonal = Math.Sqrt(bw * bw + bh * bh);
            var beffective = AnnotationService.ResolveEffectiveSettings(doc, settings, data, bdiagonal, true);

            // 仅绘云线：不填内容、不定位文字框，画完云线即结束。
            if (beffective.CloudOnly)
            {
                if (beffective.FontAutoFit) doc.Editor.WriteMessage($"\n云线对角线: {bdiagonal:0.#}  云线半径: {beffective.CloudRadius:0.###}");
                AnnotationService.CreateCloudOnly(doc, beffective, f, s);
                doc.Editor.WriteMessage("\n云线已创建。");
                CheckContinuous(doc);
                return;
            }

            // 完整批注：先填写内容，再定位批注框（预览按真实内容显示文字与框体）并落图。
            if (beffective.FontAutoFit) doc.Editor.WriteMessage($"\n云线对角线: {bdiagonal:0.#}  字高: {beffective.TextHeight:0.###}  云线半径: {beffective.CloudRadius:0.###}");
            if (!CadDialog.ShowAnnotation(data, false)){doc.Editor.WriteMessage("\n已在填写内容阶段取消单区域批注。");MarkUserCancelled();return;}
            _stage = RunStage.Elsewhere;
            var placementResult=AnnotationService.PromptPlacement(doc,beffective,settings,new[]{f},new[]{s},null,s,PlacementGeometryKind.Region,data);if(!AcceptInteraction(doc,placementResult))return;var textPt=placementResult.Point;
            AnnotationService.Create(doc, data, beffective, f, s, textPt);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        // ═══════════════ 多对一批注 ═══════════════

        private void DoMultiAnnotation(Document doc)
        {
            _multiCloudFirsts.Clear();
            _multiCloudSeconds.Clear();

            var data = NewAnnotationData(doc);

            // 传"本轮设置实例"本身（不是副本）：选范围期间在面板里改外形/选项能立刻反映到预览与落图口径上。
            var preview = _runSettings;
            doc.Editor.WriteMessage(
                "\n多对一批注：连续选择 2 个及以上云线范围；在下一个云线的第一个角点提示时按回车或空格结束范围选择。");
            try
            {
                while (true)
                {
                    var promptResult = AnnotationService.PromptCloudOrFinish(
                        doc,
                        preview,
                        _multiCloudFirsts,
                        _multiCloudSeconds,
                        out var first,
                        out var second);
                    if (promptResult == CloudPromptResult.Finished)
                    {
                        // 多对一至少需要两个范围才成立；不足时提示并继续选择，不直接退出。
                        if (_multiCloudFirsts.Count < 2)
                        {
                            doc.Editor.WriteMessage(
                                $"\n多对一批注至少需要 2 个云线范围（当前 {_multiCloudFirsts.Count} 个），请继续指定。");
                            continue;
                        }

                        break;
                    }

                    if (promptResult == CloudPromptResult.Cancelled)
                    {
                        MarkUserCancelled();
                        return;
                    }

                    _multiCloudFirsts.Add(first);
                    _multiCloudSeconds.Add(second);
                    doc.Editor.WriteMessage(
                        $"\n已添加第 {_multiCloudFirsts.Count} 个云线；" +
                        "继续指定下一条，或按回车/空格结束范围选择。");
                }

                if (_multiCloudFirsts.Count == 0)
                {
                    return;
                }

                var diagonals = _multiCloudFirsts
                    .Select((f, i) =>
                    {
                        var (_, cw, ch) =
                            AnnotationService.UcsAlignedExtents(
                                doc,
                                f,
                                _multiCloudSeconds[i]);
                        return Math.Sqrt(cw * cw + ch * ch);
                    })
                    .OrderBy(x => x)
                    .ToList();
                var middle = diagonals.Count / 2;
                var representativeDiagonal =
                    diagonals.Count % 2 == 1
                        ? diagonals[middle]
                        : (diagonals[middle - 1] +
                           diagonals[middle]) / 2.0;

                var effective = AnnotationService.ResolveEffectiveSettings(
                    doc,
                    _runSettings,
                    data,
                    representativeDiagonal,
                    true);
                if (effective.FontAutoFit || effective.CloudAutoFit)
                {
                    doc.Editor.WriteMessage(
                        $"\n代表云线尺寸: {representativeDiagonal:0.#}" +
                        $"  字高: {effective.TextHeight:0.###}" +
                        "  云线半径和线宽按各云线自身尺寸计算。");
                }

                // 范围已定：本轮的外形与尺寸自此定型。
                _stage = RunStage.Content;

                // 「仅绘云线」+ 多对一：只按各范围出云线，不填内容、不生成文字框与引线。
                if (_runSettings.CloudOnly)
                {
                    AnnotationService.CreateMultiCloud(
                        doc,
                        data,
                        effective,
                        _runSettings,
                        null,
                        _multiCloudFirsts,
                        _multiCloudSeconds,
                        _multiCloudSeconds[_multiCloudSeconds.Count - 1]);
                    doc.Editor.WriteMessage($"\n多对一云线已创建（共 {_multiCloudFirsts.Count} 条）。");
                    CheckContinuous(doc);
                    return;
                }

                // 先填内容、再定位：预览框内画出真实批注文字并按同一口径量框，便于按内容大小找位置。
                if (!CadDialog.ShowAnnotation(data, false))
                {
                    doc.Editor.WriteMessage(
                        "\n已在填写内容阶段取消多对一批注。");
                    MarkUserCancelled();
                    return;
                }

                _stage = RunStage.Elsewhere;
                var placementResult = AnnotationService.PromptPlacement(
                    doc,
                    effective,
                    _runSettings,
                    _multiCloudFirsts,
                    _multiCloudSeconds,
                    null,
                    _multiCloudSeconds[_multiCloudSeconds.Count - 1],
                    PlacementGeometryKind.MultiRegion,
                    data);
                if (!AcceptInteraction(doc, placementResult))
                {
                    return;
                }

                var textPointWcs = placementResult.Point;

                AnnotationService.CreateMultiCloud(
                    doc,
                    data,
                    effective,
                    _runSettings,
                    null,
                    _multiCloudFirsts,
                    _multiCloudSeconds,
                    textPointWcs);
                FinalizeAnnotation(doc, data);
                CheckContinuous(doc);
            }
            finally
            {
                _multiCloudFirsts.Clear();
                _multiCloudSeconds.Clear();
            }
        }

        // ═══════════════ 工具方法 ═══════════════

        private List<Point3d> CollectPlinePoints(
            Document doc,
            AnnotationSettings settings,
            out bool cancelled)
        {
            cancelled = false;
            var points = new List<Point3d>();
            var ed = doc.Editor;

            while (true)
            {
                var pointResult = AnnotationService.PromptPlinePoint(
                    doc,
                    points,
                    settings);

                if (pointResult.FinishRequested)
                {
                    if (points.Count >= 3)
                    {
                        break;
                    }

                    ed.WriteMessage("\nPL 云线至少需要三个不同的点。");
                    continue;
                }

                if (!AcceptInteraction(doc, pointResult))
                {
                    cancelled = true;
                    return null;
                }

                var pointWcs = pointResult.Point;
                if (points.Count > 0 &&
                    points[points.Count - 1].DistanceTo(pointWcs) <= 1e-8)
                {
                    ed.WriteMessage("\n该点与上一点重复，请重新指定。");
                    continue;
                }

                points.Add(pointWcs);
            }

            return points;
        }

        /// <summary>标记"这一轮是用户自己取消的"（CAD 提示被 Esc 取消 / 内容窗点了取消）。
        /// <para>本轮结束时据此退出批注（关面板）。插件自己为了换外形/改选项而中止的那条路走
        /// <see cref="InteractionAbort"/>，不经过这里，所以不会被误判。</para></summary>
        private void MarkUserCancelled()
        {
            if (InteractionAbort.Requested) return;
            _userCancelled = true;
        }

        private bool AcceptInteraction(Document doc,AnnotationService.InteractionResult result)
        {
            if(result.Status==AnnotationService.InteractionStatus.Accepted)return true;
            // 本轮是"面板改了绘制选项"被主动中止的：不必再报一次"已取消"，真正原因由中止方与重开逻辑说明。
            if(InteractionAbort.Requested)return false;
            var message=$"{result.Stage}：{result.PromptStatus}";
            if(result.Status==AnnotationService.InteractionStatus.Cancelled){doc.Editor.WriteMessage("\n已取消"+message+"。");MarkUserCancelled();return false;}
            doc.Editor.WriteMessage("\n交互失败："+message+"。");PluginLog.Warning("Interaction",message);return false;
        }

        private void FinalizeAnnotation(Document doc, AnnotationData data)
        {
            if (_runSettings.AutoNumber)
            {
                _runSettings.NextNumber++;
                _s.NextNumber = _runSettings.NextNumber;
                SettingsStore.Save(_s);
            }

            doc.Editor.WriteMessage("\nX-批注已创建: " + data.Number);
            AnnotationListPanel.RefreshIfOpen();
        }

        private void CheckContinuous(Document doc)
        {
            if (_runSettings.ContinuousAnnotation)
            {
                _continuePending = true;
                return;
            }

            Close();
        }

        private static IEnumerable<string> GetTextStyles(Document doc)
        {
            var styles = new List<string>();
            try
            {
                if (doc == null) return styles;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var table = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        if (id.IsValid && !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord r && !string.IsNullOrWhiteSpace(r.Name))
                            styles.Add(r.Name);
                    }
                }
            }
            catch { }
            return styles;
        }
    }
}
