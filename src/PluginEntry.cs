using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.EditorInput;
using Gssoft.Gscad.Geometry;
using Gssoft.Gscad.Runtime;
using CadApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_PRE2013
// AutoCAD 2012 及更早：还没有 AcCoreMgd 拆分（2013 才有的 "The Big Split"），
// 只有 Autodesk.AutoCAD.ApplicationServices.Application 这一个入口。
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif
#endif

namespace XAnnotation
{
    /// <summary>插件入口：注册双击编辑、安装菜单、输出加载信息。</summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        // ===== 双击编辑 =====
        //
        // 为什么不能"在 BeginDoubleClick 里直接把编辑窗口开出来"：
        //   Application.BeginDoubleClick 事件**无法取消 CAD 随后的原生双击动作**。用户双击批注文字，
        //   AutoCAD 会接着执行 CUI 里配置的 MTEDIT（在位文字编辑器）；双击云线/引线则会拉起特性面板。
        //   原生命令一旦进入交互态，从 Idle 里排队的 X_PZ_EDIT 要么排在它后面、要么被当作编辑器输入吞掉
        //   —— 表现就是"双击没反应"。
        //
        // 官方（Autodesk 技术支持 Norman Yuan）给的解法是三段式，这里照此实现：
        //   1) BeginDoubleClick 里只做"识别 + 布防"，不自己执行命令；
        //   2) 原生命令即将取文档锁时，在 DocumentLockModeChanged 里 e.Veto() 把它拦下；
        //   3) 被拦下后会触发 DocumentLockModeChangeVetoed，此时命令上下文干净，再启动 X_PZ_EDIT。
        //
        // 识别"被双击的是哪个实体"改用双击点坐标做拾取框命中测试（官方推荐），
        // 因为 Editor.SelectImplied() 在编组内实体、带属性块上会返回 Error，还会返回过期的脏预选。

        /// <summary>双击编辑—待处理的文档引用（事件回调之后才执行命令，期间可能已切换文档）</summary>
        private static Document _pendingDocument;
        /// <summary>双击编辑—待处理的实体 ID</summary>
        private static ObjectId _pendingId = ObjectId.Null;

        /// <summary>双击路径已经决定要编辑的实体，交给排队的 X_PZ_EDIT 取走。
        /// <para><b>为什么不靠预选</b>：双击路径原本用 <c>SetImpliedSelection</c> 把实体塞给命令，
        /// 但这条预选经"事件 → Idle → SendStringToExecute → 命令"一圈之后<b>不保证还在</b>
        /// （AutoCAD 2020 上实测会丢）。预选一丢，X_PZ_EDIT 就退化成一串"选择要编辑的 X-批注:"
        /// 提示，命令行一闪而过，用户看到的就是"双击没进批注编辑"。</para></summary>
        private static ObjectId _pendingEditId = ObjectId.Null;
        private static DateTime _pendingEditUtc = DateTime.MinValue;
        /// <summary>待编辑 ID 的有效期（秒）：只用来接住紧接着的那条命令，过后一律作废。</summary>
        private const double PendingEditValidSeconds = 15.0;
        /// <summary>上一次双击要编辑的实体（看门狗重发命令时用）。</summary>
        private static ObjectId _lastEditId = ObjectId.Null;
        /// <summary>双击编辑—是否处于"已布防"：已命中批注，正等 CAD 发出原生双击命令好把它拦下来</summary>
        private static bool _doubleClickArmed;
        /// <summary>双击编辑—布防时刻（UTC），给拦截加一个时间窗</summary>
        private static DateTime _doubleClickArmedUtc;

        /// <summary>布防有效期（秒）。双击后 CAD 的原生双击命令在毫秒级就会发出来，
        /// 超出这个窗口还没等到就视为"该对象类型没有原生双击动作"，立即解除布防，绝不误拦用户随手敲的下一条命令。</summary>
        private const double ArmWindowSeconds = 3.0;

        /// <summary>兜底延迟（秒）。等这么久还没收到任何原生命令，就认为 CUI 里没有给这类对象配置双击动作，
        /// 此时命令上下文本来就是干净的，直接启动编辑（否则用户会看到"某些批注双击有反应、某些没反应"）。</summary>
        private const double FallbackDelaySeconds = 0.4;

        /// <summary>是否已注册 Idle 回调</summary>
        private static bool _idleAttached;
        private static Database _observedDatabase;
        private static bool _listRefreshPending;
        /// <summary>双击编辑期间临时保存 CAD 的快捷特性模式，避免与插件编辑窗口同时弹出。</summary>
        private static object _savedQuickPropertiesMode;
        private static bool _quickPropertiesSuppressed;
        /// <summary>双击编辑期间临时保存的 DBLCLKEDIT 原值（null = 没动过）。
        /// <para>改它是为了让 CAD 不产生原生双击命令，从根上免掉"Veto 之后命令行挂着待取消命令"。</para></summary>
        private static object _savedDoubleClickEdit;
        private static bool _doubleClickEditSuppressed;

        /// <summary>临时关掉 DBLCLKEDIT 之后的兜底启动延迟（秒）。
        /// <para>关掉之后正常情况下<b>不会</b>再有原生命令，但个别宿主可能不认"在事件里改系统变量"，
        /// 那时原生命令会在几毫秒内冒出来并被 Veto（立刻启动编辑，不用等这个时间）。这里取 0.25 秒，
        /// 既是"双击后马上出编辑框"，又给迟到的原生命令留出被拦下的窗口。</para></summary>
        private const double SuppressedFallbackSeconds = 0.25;

        /// <summary>排队发出的 X_PZ_EDIT 迟迟不开跑时的等待上限（秒），超过就发一次 Esc 清掉挂起状态。
        /// <para>这条兜底专治"命令排在一条已被打断的原生命令后面、必须手动按 Esc"的现场问题。</para></summary>
        private const double EditStartWatchdogSeconds = 1.0;

        /// <summary>本次双击编辑的 X_PZ_EDIT 是什么时候发出去的（<see cref="DateTime.MinValue"/> = 没在等）。</summary>
        private static DateTime _editLaunchUtc = DateTime.MinValue;

        /// <summary>看门狗是否已经发过 Esc（下一步该判断要不要重发命令）。</summary>
        private static bool _editEscapeSent;
        /// <summary>本次双击是否已经重发过一次命令（只重发一次，避免死循环）。</summary>
        private static bool _editResent;
        /// <summary>Esc 之后等多久还没开跑就重发命令（秒）。</summary>
        private const double EditResendSeconds = 0.6;

        /// <summary>那条 X_PZ_EDIT 是否已经真正跑起来（由命令入口回调置位）。</summary>
        private static bool _editCommandStarted;

        /// <summary>命令入口调用：标记"排队的 X_PZ_EDIT 已经开始执行"，看门狗不再发 Esc。</summary>
        internal static void NotifyEditCommandStarted()
        {
            _editCommandStarted = true;
            _editLaunchUtc = DateTime.MinValue;
            _editEscapeSent = false;
            _editResent = false;
        }

        /// <summary>命令入口调用：取走"双击时决定要编辑的实体"（取走即作废，超时也作废）。
        /// <para>这条通道专治"预选在排队期间丢失"：AutoCAD 2020 上双击批注时，命令确实起来了，
        /// 但 <c>SelectImplied</c> 是空的，于是弹出"选择要编辑的 X-批注:"，用户以为双击没反应。</para></summary>
        internal static ObjectId TakePendingEditId()
        {
            var id = _pendingEditId; var utc = _pendingEditUtc;
            _pendingEditId = ObjectId.Null; _pendingEditUtc = DateTime.MinValue;
            if (id.IsNull || id.IsErased) return ObjectId.Null;
            if ((DateTime.UtcNow - utc).TotalSeconds > PendingEditValidSeconds) return ObjectId.Null;
            return id;
        }

        /// <summary>依赖兜底解析：已在自己目录里找过的程序集名（防同名重入死循环）。</summary>
        private static readonly HashSet<string> ResolvingAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>加载失败提示只弹一次。</summary>
        private static bool _loadFailureReported;

        /// <summary>
        /// 静态构造：在 CAD 实例化本类、调用 <see cref="Initialize"/> <b>之前</b>就把依赖兜底挂上。
        ///
        /// <para>为什么需要：<c>System.ValueTuple.dll</c>（命名元组用的 BCL 补充包，.NET 4.0/4.5 都不含）
        /// 必须与主 DLL 放在同一个目录。换机器部署时最常见的失误就是"只拷了主 DLL"，
        /// 此时加载本身能过（JIT 是惰性的），直到执行到相关代码才抛 FileNotFoundException，
        /// 而 CAD 里往往看不到任何提示 —— 现场表现就是"插件好像加载了但没反应"。
        /// 这里改成主动去插件自己的目录找同名 DLL，把"漏拷文件"变成自愈 + 日志留痕。</para>
        /// </summary>
        static PluginEntry()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
            catch (System.Exception ex) { PluginLog.Error("AssemblyResolve.Hook", ex); }
        }

        /// <summary>缺哪个程序集就去插件 DLL 所在目录找同名文件：不递归子目录、不联网、找不到就交回 CLR 报错。</summary>
        private static System.Reflection.Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            string simpleName = null;
            try
            {
                simpleName = new System.Reflection.AssemblyName(e.Name).Name;
                if (string.IsNullOrEmpty(simpleName)) return null;
                lock (ResolvingAssemblies)
                {
                    // 同名只尝试一次：避免"加载→失败→再触发解析"的死循环。
                    if (!ResolvingAssemblies.Add(simpleName)) return null;
                }

                var folder = System.IO.Path.GetDirectoryName(typeof(PluginEntry).Assembly.Location);
                if (string.IsNullOrEmpty(folder)) return null;
                var candidate = System.IO.Path.Combine(folder, simpleName + ".dll");
                if (!System.IO.File.Exists(candidate)) return null;

                PluginLog.Warning("AssemblyResolve", "从插件目录补载依赖：" + candidate);
                return System.Reflection.Assembly.LoadFrom(candidate);
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("AssemblyResolve." + (simpleName ?? "?"), ex);
                return null;
            }
        }

        /// <summary>逐步初始化：某一步失败只记日志、不中断其余步骤，绝不整段无声失败。</summary>
        private static void Step(List<string> failedSteps, string name, Action action)
        {
            try { action(); }
            catch (System.Exception ex) { failedSteps.Add(name); PluginLog.Error("Initialize." + name, ex); }
        }

        /// <summary>
        /// 把"离线现场排查"要用的环境信息一次性写进日志：
        /// 宿主版本（ACADVER）、宿主托管程序集版本、CLR 版本、进程/系统位数、
        /// 已安装的 .NET Framework 版本、插件目标框架、依赖（System.ValueTuple）是否齐全。
        ///
        /// <para>典型用途：因"Win7 上加载后没反应"这类只有现场才能复现的问题，光看代码猜不出来 ——
        /// 有了这一组日志，一次运行就能区分"根本没加载" / "加载了但 .NET 版本不对" /
        /// "加载了但缺依赖" / "加载了但菜单没建出来"。</para>
        /// </summary>
        private static void LogEnvironment()
        {
            try
            {
                var asm = typeof(PluginEntry).Assembly;
                PluginLog.Info("Environment",
                    "OS=" + Environment.OSVersion
                    + "；64 位系统=" + Environment.Is64BitOperatingSystem
                    + "；64 位进程=" + Environment.Is64BitProcess
                    + "；CLR=" + Environment.Version
                    + "；DLL 目录=" + SafeFolder(asm));

                string acadVer = "?";
                try { acadVer = Convert.ToString(CadApplication.GetSystemVariable("ACADVER")); } catch { }
                PluginLog.Info("Environment", "宿主 ACADVER=" + acadVer
                    + "；宿主程序集=" + SafeAssemblyName(typeof(CadApplication))
                    + "；数据库程序集=" + SafeAssemblyName(typeof(Entity)));

                var targetFramework = "?";
                try
                {
                    foreach (var attribute in asm.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false))
                    {
                        targetFramework = ((System.Runtime.Versioning.TargetFrameworkAttribute)attribute).FrameworkName;
                        break;
                    }
                }
                catch (System.Exception ex) { PluginLog.Warning("Environment.TargetFramework", ex.Message); }
                PluginLog.Info("Environment", "插件目标框架=" + targetFramework + "；元数据运行时版本=" + asm.ImageRuntimeVersion);

                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    {
                        var release = key == null ? null : key.GetValue("Release");
                        var version = key == null ? null : key.GetValue("Version");
                        PluginLog.Info("Environment", "已装 .NET Framework 4.x：Version=" + (version ?? "(读不到)")
                            + "；Release=" + (release ?? "(读不到，可能只有 4.0 或注册表被策略限制)")
                            + "（3839?=无；378389=4.5；378758=4.5.1；379893=4.5.2；393295=4.6；394254=4.6.1；"
                            + "394802=4.6.2；460798=4.7；461808=4.7.2；528040=4.8）");
                    }
                }
                catch (System.Exception ex) { PluginLog.Warning("Environment.DotNet", ex.Message); }

                // 依赖自检：命名元组用的 System.ValueTuple 必须与主 DLL 同目录（或已在 GAC 中）。
                try
                {
                    var folder = SafeFolder(asm);
                    var dependency = string.IsNullOrEmpty(folder)
                        ? "System.ValueTuple.dll"
                        : System.IO.Path.Combine(folder, "System.ValueTuple.dll");
                    var sibling = System.IO.File.Exists(dependency);
                    var tuple = Type.GetType("System.ValueTuple`2, System.ValueTuple", false);
                    PluginLog.Info("Environment", "System.ValueTuple：" + (tuple == null ? "未解析到！" : "已解析 " + tuple.Assembly.FullName)
                        + "；同目录存在 System.ValueTuple.dll=" + sibling
                        + (tuple == null ? "（命名元组相关功能会失败，请把 System.ValueTuple.dll 与主 DLL 放在同一目录）" : string.Empty));
                }
                catch (System.Exception ex) { PluginLog.Warning("Environment.ValueTuple", ex.Message); }
            }
            catch (System.Exception ex) { PluginLog.Error("Environment", ex); }
        }

        /// <summary>插件 DLL 所在目录；取不到返回空串（不抛异常）。</summary>
        private static string SafeFolder(System.Reflection.Assembly assembly)
        {
            try { return System.IO.Path.GetDirectoryName(assembly.Location) ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>读程序集全名；失败返回错误说明而不是让日志流程中断。</summary>
        private static string SafeAssemblyName(Type type)
        {
            try { return type == null ? "(null)" : type.Assembly.FullName; }
            catch (System.Exception ex) { return "(读取失败:" + ex.Message + ")"; }
        }

        /// <summary>加载被意外打断时的"看得见"提示：日志里记全栈，CAD 里给一句人话 + 日志路径。</summary>
        private static void ReportLoadFailure(System.Exception ex)
        {
            if (_loadFailureReported) return;
            _loadFailureReported = true;

            var logPath = System.IO.Path.Combine(AppPaths.DataFolder, "Logs", "XAnnotation.log");
            var text = "X-批注加载失败：" + ex.GetType().Name + " - " + ex.Message
                + "\n\n详细信息见日志：\n" + logPath;

            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n" + text);
            }
            catch { }

            try
            {
                System.Windows.MessageBox.Show(text, "X-批注",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch { }
        }

        public void Initialize()
        {
            var asm = typeof(PluginEntry).Assembly;
            var failedSteps = new List<string>();
            try
            {
                // 启动即写日志：构建时间 + DLL 实际加载路径。排查"改了没生效"时，
                // 看日志最新一条的构建时间就知道 CAD 里跑的是哪个版本（NETLOAD 无法替换运行中的同名程序集）。
                var buildTime = System.IO.File.GetLastWriteTime(asm.Location);
                PluginLog.Info("Initialize",
                    "版本 " + AppInfo.GetDisplayVersion() + "，构建于 " + buildTime.ToString("yyyy-MM-dd HH:mm:ss") +
                    "，加载自 " + asm.Location);

                // 环境快照先落地：哪怕是"加载到一半炸了"，也已经留下可判断的现场信息。
                LogEnvironment();

                // 宿主 WPF Application 的守护必须排在所有建窗动作之前：把 ShutdownMode 改成
                // OnExplicitShutdown，否则本插件最后一个窗口关闭时 WPF 会自动关掉宿主的 Application，
                // 之后本次 CAD 会话内所有窗口都加载不出来（"应用程序对象正在关闭"）。详见 WpfHost。
                Step(failedSteps, "WpfHost", () => { WpfHost.Ensure(); });

                Step(failedSteps, "BeginDoubleClick", () => CadApplication.BeginDoubleClick += OnBeginDoubleClick);
                Step(failedSteps, "DocumentActivated", () => CadApplication.DocumentManager.DocumentActivated += OnDocumentActivated);
                // 双击编辑真正生效靠这两个事件：拦下原生命令 + 被拦之后执行自己的命令。见类顶部注释。
                Step(failedSteps, "DocumentLockModeChanged", () => CadApplication.DocumentManager.DocumentLockModeChanged += OnDocumentLockModeChanged);
                Step(failedSteps, "DocumentLockModeChangeVetoed", () => CadApplication.DocumentManager.DocumentLockModeChangeVetoed += OnDocumentLockModeChangeVetoed);

                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                var menuReady = false;
                var toolbarReady = false;
                try { menuReady = MenuInstaller.EnsureWithRetry(); }
                catch (System.Exception ex) { failedSteps.Add("Menu"); PluginLog.Error("Initialize.Menu", ex); }
                // 浮动快捷栏已取消：CAD 原生工具栏是单字按钮的唯一入口。加载时就建好并显示；
                // 建不出来会自动安排一次 Idle 重试，仍不行可用 X_PZ_TOOLBAR 手动建/显隐。
                try { toolbarReady = ToolbarInstaller.EnsureWithRetry(); }
                catch (System.Exception ex) { failedSteps.Add("Toolbar"); PluginLog.Error("Initialize.Toolbar", ex); }
                if (doc != null)
                {
                    try { AnnotationService.SyncNextNumber(doc); }
                    catch (System.Exception ex) { failedSteps.Add("SyncNextNumber"); PluginLog.Error("Initialize.SyncNextNumber", ex); }
                    try { DrawingScale.Ensure(doc); }
                    catch (System.Exception ex) { failedSteps.Add("DrawingScale"); PluginLog.Error("Initialize.DrawingScale", ex); }
                    try { ObserveDatabase(doc.Database); }
                    catch (System.Exception ex) { failedSteps.Add("ObserveDatabase"); PluginLog.Error("Initialize.ObserveDatabase", ex); }
                }

                // 双击编辑能不能用，取决于两处"参数"，远程排障时最常被问到；这里一次写全，
                // 用户只要把日志发过来就能判断是 CAD 的 DBLCLKEDIT 关了，还是插件自己的勾选项没开。
                try
                {
                    PluginLog.Info("DoubleClick.Config",
                        "DBLCLKEDIT=" + IntSysVar("DBLCLKEDIT", -1) +
                        "；插件设置「双击编辑」=" + SettingsStore.Load().DoubleClickEdit +
                        "；PICKSTYLE=" + IntSysVar("PICKSTYLE", -1) +
                        "；PICKFIRST=" + IntSysVar("PICKFIRST", -1));
                }
                catch (System.Exception ex) { PluginLog.Warning("DoubleClick.Config", ex.Message); }

                // "初始化完成"必须留痕：日志里只有"构建于…"而没有这一条，就说明加载真被打断了。
                PluginLog.Info("Initialize", "初始化完成。菜单=" + menuReady + "，工具栏=" + toolbarReady
                    + "，失败步骤=" + (failedSteps.Count == 0 ? "无" : string.Join("/", failedSteps.ToArray())));

                var message =
#if GSCAD
                    // 浩辰：菜单是直接写进主 CUI 文件（gcad.cuix）的，CUI 只在启动/重载时读取，
                    // 所以必须明确告诉用户"重启才显示" —— 否则又会收到"菜单栏不显示 X-批注"的反馈。
                    // 工具栏在浩辰上暂不创建（原因见 ToolbarInstaller.Ensure：会踩 MenuGroups 的访问冲突）。
                    "\nX-批注已加载。" +
                    (menuReady
                        ? " X-批注菜单已写入浩辰主菜单，重启浩辰CAD后显示（或执行 MENU 命令重载）。"
                        : " 菜单未安装，可直接用下面的命令名操作。") +
                    " 浩辰版暂不创建工具栏。" +
#else
                    "\nX-批注已加载。" + (menuReady ? " 菜单已就绪。" : " 菜单稍后自动挂上（或用 X_PZ_MENU）。") +
                    (toolbarReady ? " X-批注工具栏已就绪。" : " X-批注工具栏未就绪（将自动重试，或用 X_PZ_TOOLBAR）。") +
#endif
                    " 命令: X_PZ_DRAW / X_PZ_NOTE / X_PZ_EDIT / X_PZ_MOVE /" +
                    " X_PZ_DELETE / X_PZ_HIDE / X_PZ_SHOW / X_PZ_MERGE / X_PZ_FILTER / X_PZ_REFRESH / X_PZ_CLOUD / X_PZ_LIST /" +
                    " X_PZ_SUMMARY / X_PZ_LEGEND / X_PZ_WORD / X_PZ_HISTORY / X_PZ_KB /" +
                    " X_PZ_EXPORT / X_PZ_IMPORT / X_PZ_REPAIR /" +
                    " X_PZ_TOOLBAR / X_PZ_SETTINGS / X_PZ_INSTALL_AUTOLOAD / X_PZ_UNINSTALL_AUTOLOAD / X_PZ_AUTOLOAD / X_PZ_MENU / X_PZ_ABOUT";
                try
                {
                    var active = doc ?? CadApplication.DocumentManager.MdiActiveDocument;
                    active?.Editor.WriteMessage(message);
                }
                catch (System.Exception ex) { PluginLog.Error("Initialize.Message", ex); }
            }
            catch (System.Exception ex)
            {
                // 整个加载被意外打断：既写日志，也在 CAD 里显式提示。
                // 没有这一层时，用户看到的就只有"加载后没反应"，日志里只剩一行"构建于…"，无从下手。
                PluginLog.Error("Initialize", ex);
                ReportLoadFailure(ex);
            }
        }

        public void Terminate()
        {
            CadApplication.BeginDoubleClick -= OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            CadApplication.DocumentManager.DocumentLockModeChanged -= OnDocumentLockModeChanged;
            CadApplication.DocumentManager.DocumentLockModeChangeVetoed -= OnDocumentLockModeChangeVetoed;
            ObserveDatabase(null);
            DetachIdle();
            DisarmDoubleClick();
            MenuInstaller.Detach();
            ToolbarInstaller.Detach();
            // 关掉批注面板并摘掉它的 Esc 键盘钩子：钩子回调是托管委托，
            // 程序集被卸载后还挂着钩子的话，下一次按键会走进已卸载的代码（进程级崩溃）。
            try { XAnnotation.Views.AnnotationPanel.ShutdownForUnload(); }
            catch (System.Exception ex) { PluginLog.Error("Terminate.AnnotationPanel", ex); }
            RestoreQuickPropertiesMode();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                // 布防途中切了图纸：作废本次双击，并把临时改掉的 QPMODE 还回去。
                if (_doubleClickArmed) { DisarmDoubleClick(); RestoreQuickPropertiesMode(); }
                AnnotationService.SyncNextNumber(e.Document);
                // 换图后必须让图面单位重新识别：缓存是按"当前图"成立的一次性结果，
                // 不清就会把上一张图的比例套到这一张上。
                DrawingScale.Forget();
                DrawingScale.Ensure(e.Document);
                ObserveDatabase(e.Document?.Database);
                XAnnotation.Views.AnnotationPanel.HandleDocumentActivated(e.Document);
                XAnnotation.Views.AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { PluginLog.Error("Document.Activated", ex); }
        }

        private static void ObserveDatabase(Database database)
        {
            if(_observedDatabase==database)return;
            if(_observedDatabase!=null)_observedDatabase.ObjectErased-=OnObjectErased;
            _observedDatabase=database;
            if(_observedDatabase!=null)_observedDatabase.ObjectErased+=OnObjectErased;
        }

        private static void OnObjectErased(object sender,ObjectErasedEventArgs e)
        {
            try
            {
                if(!(e.DBObject is Entity entity)||entity.GetXDataForApplication(AnnotationCodec.AppName)==null)return;
                _listRefreshPending=true;
                AttachIdle();
            }
            catch(System.Exception ex){PluginLog.Error("AnnotationList.ObjectErased",ex);}
        }

        // ===== 双击编辑：第 1 段 —— 识别 + 布防 =====

        /// <summary>双击事件：判断双击点下是不是 X-批注；是则布防，等 CAD 发出原生双击命令后把它拦下来。</summary>
        private static void OnBeginDoubleClick(object sender, BeginDoubleClickEventArgs e)
        {
            try
            {
                DisarmDoubleClick();                                   // 上一次没走完的布防一律作废
                var doubleClickEditOn = SettingsStore.Load().DoubleClickEdit;
                // 每次双击都留一条：远程判断"事件到底有没有来"只靠这一行
                // （没有这行 = 宿主没发事件；有这行但没有 Hit = 命中测试没认出批注）。
                PluginLog.Info("DoubleClick.Event",
                    "收到双击事件 @(" + e.Location.X.ToString("0.###") + "," + e.Location.Y.ToString("0.###") +
                    ")，插件设置「双击编辑」=" + doubleClickEditOn);
                if (!doubleClickEditOn) return;
                var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;

                var id = ResolveDoubleClickedAnnotation(doc, e.Location);
                if (id.IsNull) return;                                 // 双击的不是批注：完全不干预，走 CAD 原生行为

                _pendingDocument = doc; _pendingId = id;
                _doubleClickArmed = true; _doubleClickArmedUtc = DateTime.UtcNow;
                SuppressQuickPropertiesMode();

                // DBLCLKEDIT=0：CAD 根本不会执行任何原生双击动作，没有可拦的目标，直接启动编辑。
                if (IntSysVar("DBLCLKEDIT", 1) == 0)
                {
                    LaunchPendingEdit("DBLCLKEDIT=0，无原生动作");
                    return;
                }

                // ★ 关键：自己把 DBLCLKEDIT 临时关掉，让 CAD **根本不产生**原生命令。
                //   以前只靠 DocumentLockModeChanged 里 Veto 把原生命令拦下来，代价是那次取锁被否掉之后，
                //   CAD 会把这条命令留在"正在执行"状态，命令行挂着"按回车取消或[重试]" ——
                //   用户必须先按一次 Esc 或回车，插件排队的 X_PZ_EDIT 才轮得到执行（现场就是"双击要按 Esc"）。
                //   关掉 DBLCLKEDIT 之后没有原生命令可拦，也就不存在这条挂起的提示；Veto 只留作兜底
                //   （个别宿主在事件里改系统变量不生效时才会走到）。
                SuppressDoubleClickEdit();

                // 其余情况挂 Idle 兜底：若 CAD 没有为该对象类型配置双击动作，就不会有任何命令可拦，
                // 到点后由 OnIdle 直接启动编辑。
                AttachIdle();
                PluginLog.Info("DoubleClick", "已命中批注，布防等待原生双击命令。");
            }
            catch (System.Exception ex) { DisarmDoubleClick(); RestoreQuickPropertiesMode(); PluginLog.Error("DoubleClick", ex); }
        }

        /// <summary>确定"被双击的到底是哪条批注"。
        /// 优先用双击点 + PICKBOX 做拾取框命中测试（Autodesk 官方推荐：编组内实体、带属性块、脏预选全部免疫）；
        /// 命中不到再退回预选集，保证任何情况下都不会比原来的实现更差。</summary>
        private static ObjectId ResolveDoubleClickedAnnotation(Document doc, Point3d location)
        {
            var id = HitTestAtPickBox(doc, location);
            if (!id.IsNull) { PluginLog.Info("DoubleClick.Hit", "拾取框命中批注实体 " + id); return id; }

            try
            {
                var implied = doc.Editor.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count == 1)
                {
                    var impliedId = implied.Value.GetObjectIds()[0];
                    if (IsAnnotation(doc, impliedId)) { PluginLog.Info("DoubleClick.Hit", "预选集命中批注实体 " + impliedId); return impliedId; }
                }
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.Implied", ex); }
            return ObjectId.Null;
        }

        /// <summary>拾取框：四角（喂给选择集）+ 图纸单位下的半边长（近邻兜底用的容差）。</summary>
        private struct PickBox
        {
            /// <summary>交叉选择用的四角（WCS）。</summary>
            public Point3dCollection Corners;
            /// <summary>拾取框在图纸单位下的半边长，恒大于 0。</summary>
            public double WorldHalf;
        }

        /// <summary>用双击点 + 系统变量 PICKBOX 反算光标拾取框，判断双击点下落着哪条批注。
        /// <para><b>两级策略</b>：先做交叉选择（首选，快且准），选不到再走"近邻遍历"兜底。</para>
        /// <para><b>为什么必须有两级</b>：识别"被双击的是哪个实体"原本只靠屏幕像素拾取框，
        /// 但中望 ZWCAD 的 <c>Editor.PointToScreen</c> 在 <c>BeginDoubleClick</c> 回调里必抛
        /// <c>InvalidOperationException: 对象的当前状态使该操作无效</c>（2026-10-13 实机日志确证）。
        /// 原实现"一次失败就整体放弃"，于是中望上整条双击编辑链路失效 ——
        /// 现场表现就是"中望里双击批注毫无反应、只能用菜单的编辑批注"。</para></summary>
        private static ObjectId HitTestAtPickBox(Document doc, Point3d location)
        {
            try
            {
                var box = BuildPickBox(doc.Editor, location);

                // 第一步：交叉选择。
                var hit = HitTestBySelection(doc, box.Corners);
                if (!hit.IsNull) return hit;

                // 第二步：近邻遍历兜底。不碰任何选择集 / 屏幕坐标 API，
                // 因此即便宿主的这两种能力都不可用，双击编辑照样成立。
                return HitTestByProximity(doc, location, box.WorldHalf);
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.HitTest", ex); return ObjectId.Null; }
        }

        /// <summary>按拾取框交叉选择，再从命中的实体里挑出批注。
        /// <para>两条选择途径依次尝试：<c>SelectCrossingPolygon</c> → <c>SelectCrossingWindow</c>
        /// （两者语义等价，某些宿主上只实现了后者）。都不可用时返回 <c>ObjectId.Null</c>，由近邻兜底接手。</para></summary>
        private static ObjectId HitTestBySelection(Document doc, Point3dCollection corners)
        {
            PromptSelectionResult result = null;
            try
            {
                result = doc.Editor.SelectCrossingPolygon(corners);
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("DoubleClick.HitTest",
                    "SelectCrossingPolygon 不可用：" + ex.GetType().Name + " - " + ex.Message);
            }

            // 多边形选择失败时退到等价的窗口选择（对角两点）。
            if ((result == null || result.Status != PromptStatus.OK) && corners != null && corners.Count >= 3)
            {
                try
                {
                    result = doc.Editor.SelectCrossingWindow(corners[0], corners[2]);
                }
                catch (System.Exception ex)
                {
                    PluginLog.Warning("DoubleClick.HitTest",
                        "SelectCrossingWindow 不可用：" + ex.GetType().Name + " - " + ex.Message);
                }
            }

            if (result == null || result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
            {
                return ObjectId.Null;
            }

            try
            {
                // 一条批注由云线/文字/边框/引线多个实体组成，它们携带的是同一个批注 Id（data.Id）；
                // 重叠处可能同时命中两条不同批注，此时取先选到的并记日志，便于按现场反馈微调。
                ObjectId hit = ObjectId.Null; string hitAnnotationId = null; var distinct = 0;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var candidate in result.Value.GetObjectIds())
                    {
                        var entity = tr.GetObject(candidate, OpenMode.ForRead, false) as Entity;
                        if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out var data)) continue;
                        if (hit.IsNull) { hit = candidate; hitAnnotationId = data.Id; distinct = 1; }
                        else if (!string.Equals(data.Id, hitAnnotationId, StringComparison.Ordinal))
                        {
                            distinct++;
                            PluginLog.Warning("DoubleClick.Hit", "拾取框同时命中 " + distinct + " 条批注，已取第一条。");
                        }
                    }
                }
                return hit;
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.HitTest.Read", ex); return ObjectId.Null; }
        }

        /// <summary>最后一道兜底：在当前空间里找"离双击点最近、且在容差内的批注实体"。
        /// <para>完全不依赖选择集与屏幕坐标 API —— 中望上这两样都不可靠，而这里只要有事务和实体几何就能工作。
        /// 先按 XData 过滤出批注实体再算距离，因此额外开销只是对当前空间做一次全表扫描；
        /// 双击是低频操作，这点代价换来"三个宿主上双击编辑都成立"是划算的。</para></summary>
        private static ObjectId HitTestByProximity(Document doc, Point3d location, double tolerance)
        {
            try
            {
                if (tolerance <= 0) tolerance = 1.0;   // 容差为 0 会永远选不到，兜一个最小正数

                ObjectId best = ObjectId.Null;
                var bestDistance = double.MaxValue;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var space = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    if (space == null) return ObjectId.Null;

                    foreach (ObjectId candidate in space)
                    {
                        var entity = tr.GetObject(candidate, OpenMode.ForRead, false) as Entity;
                        if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out var ignored)) continue;
                        var distance = DistanceToPoint(entity, location);
                        if (distance < bestDistance) { bestDistance = distance; best = candidate; }
                    }
                }

                if (best.IsNull || bestDistance > tolerance) return ObjectId.Null;
                PluginLog.Info("DoubleClick.Hit",
                    "交叉选择未命中，按近邻兜底命中批注实体 " + best
                    + "（距双击点 " + bestDistance.ToString("0.###") + "，容差 " + tolerance.ToString("0.###") + "）。");
                return best;
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("DoubleClick.HitTest.Proximity", ex);
                return ObjectId.Null;
            }
        }

        /// <summary>实体到某点的距离，用于近邻兜底的排序。
        /// <para>用<b>包围盒</b>而不是"最近点"来算：<c>GetClosestPointTo</c> 只存在于 <c>Curve</c> 上
        /// （<c>Entity</c> 没有这个方法，四个宿主上都编译不过），而 <c>GeometricExtents</c> 是每种实体都有的基础能力。
        /// 包围盒对云线这类不规则轮廓会偏大，但"双击点落在批注轮廓附近"本来就是要判的情形，够用。</para>
        /// <para>点在盒内时再按"到盒中心的距离"排序（乘 1e-6 压到远小于任何盒外距离），
        /// 避免两条批注包围盒重叠时永远取到数据库里先出现的那一条。</para>
        /// <para>算不出来（空实体、退化的范围）返回 <see cref="double.MaxValue"/>，等价于"无视它"。</para></summary>
        private static double DistanceToPoint(Entity entity, Point3d point)
        {
            try
            {
                var extents = entity.GeometricExtents;
                var min = extents.MinPoint;
                var max = extents.MaxPoint;

                // 到轴对齐包围盒的最短距离（点在盒内时为 0）。
                var dx = Math.Max(0.0, Math.Max(min.X - point.X, point.X - max.X));
                var dy = Math.Max(0.0, Math.Max(min.Y - point.Y, point.Y - max.Y));
                var outside = Math.Sqrt(dx * dx + dy * dy);
                if (outside > 0) return outside;

                var centerX = (min.X + max.X) * 0.5;
                var centerY = (min.Y + max.Y) * 0.5;
                var centerDistance = Math.Sqrt((point.X - centerX) * (point.X - centerX)
                                             + (point.Y - centerY) * (point.Y - centerY));
                return centerDistance * 1e-6;
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("DoubleClick.Distance", ex.Message);
                return double.MaxValue;
            }
        }

        /// <summary>构造"光标拾取框"，供 <see cref="HitTestBySelection"/> 做交叉选择。
        /// <para><b>三条路径逐级降级</b>，任意一条成功即返回：</para>
        /// <list type="number">
        /// <item>屏幕像素拾取框，视口号 1（Autodesk 官方示例口径）；</item>
        /// <item>同上，视口号 0（某些宿主只在这个视口号下可用）；</item>
        /// <item><b>世界坐标小方框</b>：不依赖任何屏幕 API，按"视口高度 × 0.5%"折算等价的图纸单位尺度
        /// （800 px 高的视口上约等于 PICKBOX 默认的 4 px）。中望走的就是这一条。</item>
        /// </list>
        /// <para>为什么必须降级而不是"失败就算了"：<c>BeginDoubleClick</c> 里根本拿不到预选集
        /// （双击不产生预选），屏幕拾取框是唯一可靠入口。中望上它不可用，
        /// 若不降级就会让双击编辑在整个中望宿主里完全失效。</para>
        /// <para>返回值恒为 WCS 坐标：<c>BeginDoubleClickEventArgs.Location</c> 是 WCS，
        /// <c>Editor.SelectCrossingPolygon</c> 也接收 WCS，因此不需要做 UCS 变换。</para>
        /// <para>顺带算出框在图纸单位下的半边长，作为近邻兜底的容差 —— 二者必须是同一个尺度，
        /// 否则会出现"选择集用像素框、兜底却用另一个尺度"的错配。</para></summary>
        private static PickBox BuildPickBox(Editor ed, Point3d location)
        {
            // PICKBOX 是拾取框的"半个边长"（像素）；为 0 时拾取框退化成一个点，兜一个最小框，否则什么都选不到。
            var half = (double)IntSysVar("PICKBOX", 4);
            if (half <= 0) half = 4;

            // 路径 1 / 2：屏幕坐标（返回类型是 System.Windows.Point，X/Y 为 double，**不是** System.Drawing.Point）。
            foreach (var viewport in new[] { 1, 0 })
            {
                try
                {
                    var screen = ed.PointToScreen(location, viewport);
                    var corners = new Point3dCollection();
                    corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X - half, screen.Y - half), viewport));
                    corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X + half, screen.Y - half), viewport));
                    corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X + half, screen.Y + half), viewport));
                    corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X - half, screen.Y + half), viewport));
                    return new PickBox { Corners = corners, WorldHalf = WorldHalfOf(corners) };
                }
                catch (System.Exception ex)
                {
                    // 逐级降级属正常路径，只记 Warning 不记 Error：中望上这一条必然会失败。
                    PluginLog.Warning("DoubleClick.HitTest",
                        "PointToScreen(viewport=" + viewport + ") 不可用：" + ex.GetType().Name + " - " + ex.Message);
                }
            }

            // 路径 3：世界坐标小方框。
            var world = 0.0;
            try
            {
                using (var view = ed.GetCurrentView())
                {
                    world = Math.Abs(view.Height) * 0.005;
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("DoubleClick.PickBox", "读取当前视图失败，改用固定尺度：" + ex.Message);
            }
            // 视口高度读不到（0 / 异常值）时兜一个不至于大得离谱的值，保证仍能选到点附近的实体。
            if (!(world > 0) || double.IsInfinity(world)) world = 1.0;

            var box = new Point3dCollection();
            box.Add(new Point3d(location.X - world, location.Y - world, location.Z));
            box.Add(new Point3d(location.X + world, location.Y - world, location.Z));
            box.Add(new Point3d(location.X + world, location.Y + world, location.Z));
            box.Add(new Point3d(location.X - world, location.Y + world, location.Z));
            PluginLog.Info("DoubleClick.HitTest",
                "已退回世界坐标拾取框（半边长 " + world.ToString("0.####") + "）。");
            return new PickBox { Corners = box, WorldHalf = world };
        }

        /// <summary>由拾取框相邻两角反推世界坐标下的半边长；算不出来返回 1.0（图纸单位）。</summary>
        private static double WorldHalfOf(Point3dCollection corners)
        {
            try
            {
                var value = corners[1].DistanceTo(corners[0]) * 0.5;
                if (value > 0 && !double.IsInfinity(value)) return value;
            }
            catch (System.Exception ex) { PluginLog.Warning("DoubleClick.PickBox.Size", ex.Message); }
            return 1.0;
        }

        /// <summary>实体是不是本插件的批注（走 XData / 编组链路识别）。</summary>
        private static bool IsAnnotation(Document doc, ObjectId id)
        {
            if (id.IsNull || !id.IsValid || id.IsErased) return false;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    return entity != null && AnnotationService.TryReadFromEntity(tr, entity, out var ignored);
                }
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.IsAnnotation", ex); return false; }
        }

        // ===== 双击编辑：第 2 段 —— 拦下 CAD 的原生双击动作 =====

        /// <summary>原生命令即将取文档锁时把它拦下来。这是唯一能取消 CAD 原生双击动作的入口。</summary>
        private static void OnDocumentLockModeChanged(object sender, DocumentLockModeChangedEventArgs e)
        {
            if (!_doubleClickArmed) return;

            // 超窗：宁可漏拦，也绝不能把用户随手敲的下一条命令吃掉。
            if ((DateTime.UtcNow - _doubleClickArmedUtc).TotalSeconds > ArmWindowSeconds)
            {
                DisarmDoubleClick();
                RestoreQuickPropertiesMode();
                PluginLog.Warning("DoubleClick.Veto", "超时未等到原生命令，已解除布防。");
                return;
            }

            var command = e.GlobalCommandName;
            if (string.IsNullOrEmpty(command)) return;
            // 放行自己发出的命令（正常流程里布防已提前清掉，这里是双保险）。
            if (string.Equals(command, "X_PZ_EDIT", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                PluginLog.Info("DoubleClick.Veto", "拦下原生双击命令：" + command);
                e.Veto();
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.Veto", ex); }
        }

        // ===== 双击编辑：第 3 段 —— 原生命令已被拦下，启动自己的编辑 =====

        private static void OnDocumentLockModeChangeVetoed(object sender, DocumentLockModeChangeVetoedEventArgs e)
        {
            if (!_doubleClickArmed) return;
            LaunchPendingEdit("原生双击命令已拦下");
        }

        /// <summary>Idle 回调：兜底启动编辑 + 批注列表刷新。</summary>
        private static void OnIdle(object sender, EventArgs e)
        {
            if (_listRefreshPending)
            {
                _listRefreshPending = false;
                try { XAnnotation.Views.AnnotationListPanel.RefreshIfOpen(); }
                catch (System.Exception ex) { PluginLog.Error("AnnotationList.Refresh", ex); }
            }

            var idleStillNeeded = false;
            if (_doubleClickArmed)
            {
                var elapsed = (DateTime.UtcNow - _doubleClickArmedUtc).TotalSeconds;
                // 已经把 DBLCLKEDIT 关掉时不可能再有原生命令：用更短的兜底，双击手感不变慢。
                var deadline = _doubleClickEditSuppressed ? SuppressedFallbackSeconds : FallbackDelaySeconds;
                if (elapsed > deadline) LaunchPendingEdit("未收到原生双击命令");
                else idleStillNeeded = true;
            }

            // 已排队 X_PZ_EDIT 的看门狗（两级）：
            //   ① 超过 1 秒还没开跑 → 多半卡在"文档正在执行命令"（被 Veto 掉的原生双击命令），替用户发一次 Esc；
            //   ② Esc 之后再等 0.6 秒仍没开跑 → 说明那条命令被宿主直接丢了，带着待编辑 ID 重发一次（只重发一次）。
            if (_editLaunchUtc != DateTime.MinValue && !_editCommandStarted)
            {
                var waited = (DateTime.UtcNow - _editLaunchUtc).TotalSeconds;
                if (!_editEscapeSent && !_editResent && waited > EditStartWatchdogSeconds)
                {
                    _editEscapeSent = true;
                    _editLaunchUtc = DateTime.UtcNow;          // 重新计时：给"重发"这一步留时间
                    try
                    {
                        var doc = CadApplication.DocumentManager.MdiActiveDocument;
                        PluginLog.Warning("DoubleClick.Watchdog",
                            "X_PZ_EDIT 在 " + EditStartWatchdogSeconds.ToString("0.#") + " 秒内没有启动：发一次 Esc 解除 CAD 的挂起状态。");
                        doc?.SendStringToExecute("\x1b", true, false, false);
                    }
                    catch (System.Exception ex) { PluginLog.Error("DoubleClick.Watchdog", ex); }
                    idleStillNeeded = true;
                }
                else if (_editEscapeSent && waited > EditResendSeconds)
                {
                    _editEscapeSent = false; _editLaunchUtc = DateTime.MinValue;
                    _editResent = true;
                    RelaunchPendingEdit();
                    if (_editLaunchUtc != DateTime.MinValue) idleStillNeeded = true;
                }
                else if (_editResent && waited > EditStartWatchdogSeconds)
                {
                    // 重发也没起来：不再死磕。把临时改过的系统变量还原，别让用户的 CAD 留在 DBLCLKEDIT=0。
                    _editLaunchUtc = DateTime.MinValue; _editResent = false;
                    RestoreDoubleClickEdit(); RestoreQuickPropertiesMode();
                    PluginLog.Warning("DoubleClick.Watchdog", "两次都没能启动 X_PZ_EDIT，已放弃，并还原 DBLCLKEDIT/QPMODE。");
                }
                else idleStillNeeded = true;
            }

            if (!idleStillNeeded) DetachIdle();
        }

        /// <summary>看门狗重发：Esc 之后队列已干净，带"待编辑 ID"再排一次 X_PZ_EDIT。</summary>
        private static void RelaunchPendingEdit()
        {
            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                var id = _lastEditId;
                if (doc == null || id.IsNull || id.IsErased) return;
                PluginLog.Warning("DoubleClick.Relaunch", "X_PZ_EDIT 被宿主丢弃，重发一次（实体 " + id + "）。");
                _pendingEditId = id; _pendingEditUtc = DateTime.UtcNow;
                doc.Editor.SetImpliedSelection(new[] { id });
                doc.SendStringToExecute("X_PZ_EDIT ", true, false, false);
                _editCommandStarted = false;
                _editLaunchUtc = DateTime.UtcNow;
                AttachIdle();
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.Relaunch", ex); }
        }

        /// <summary>启动编辑：先解除布防，再重建预选并排入 X_PZ_EDIT。
        /// 顺序很关键——解除布防必须在发命令之前，否则自己这条 X_PZ_EDIT 会被当成原生双击命令拦下来。</summary>
        private static void LaunchPendingEdit(string reason)
        {
            var doc = _pendingDocument; var id = _pendingId;
            DisarmDoubleClick();
            if (doc == null || id.IsNull || id.IsErased || doc.IsDisposed ||
                CadApplication.DocumentManager.MdiActiveDocument != doc)
            {
                RestoreQuickPropertiesMode();
                PluginLog.Warning("DoubleClick.Launch", "放弃启动编辑（" + reason + "）：图纸已切换或已关闭。");
                return;
            }
            try
            {
                PluginLog.Info("DoubleClick.Launch", "启动 X_PZ_EDIT（" + reason + "）。");
                _lastEditId = id;
                // 两条通道都给：预选（常规路径）+ 待编辑 ID（预选丢了也能接上，见 _pendingEditId）。
                _pendingEditId = id; _pendingEditUtc = DateTime.UtcNow;
                doc.Editor.SetImpliedSelection(new[] { id });
                doc.SendStringToExecute("X_PZ_EDIT ", true, false, false);
                // 看门狗：个别宿主会把排队命令压在"文档正在执行命令"的状态后面（现场表现就是
                // "双击要按一下 Esc 才弹出编辑框"）。1 秒内没见到命令真正开跑，就替用户按一次 Esc
                // 把挂起状态清掉 —— 已经排队的那条 X_PZ_EDIT 随后就会执行，不需要重发。
                _editCommandStarted = false;
                _editLaunchUtc = DateTime.UtcNow;
                AttachIdle();
            }
            catch (System.Exception ex)
            {
                RestoreQuickPropertiesMode();
                PluginLog.Error("DoubleClick.Launch", ex);
            }
        }

        private static void DisarmDoubleClick()
        {
            _doubleClickArmed = false;
            _pendingDocument = null;
            _pendingId = ObjectId.Null;
        }

        private static void AttachIdle()
        {
            if (_idleAttached) return;
            CadApplication.Idle += OnIdle; _idleAttached = true;
        }

        private static void DetachIdle()
        {
            if (!_idleAttached) return;
            CadApplication.Idle -= OnIdle; _idleAttached = false;
        }

        /// <summary>读整型系统变量；读不到就返回默认值，绝不让双击流程因为读变量失败而中断。</summary>
        private static int IntSysVar(string name, int fallback)
        {
            try { return Convert.ToInt32(CadApplication.GetSystemVariable(name)); }
            catch (System.Exception ex) { PluginLog.Error("SysVar." + name, ex); return fallback; }
        }

        /// <summary>双击期间把 DBLCLKEDIT 临时改成 0：CAD 判定"双击不做任何原生动作"，
        /// 于是不会产生 MTEDIT / QUICKPROPERTIES 这类原生命令。
        /// <para><b>为什么这能治"双击要按 Esc"</b>：原来的办法是在 <c>DocumentLockModeChanged</c> 里
        /// <c>Veto()</c> 掉原生命令。取锁被否掉之后，CAD 仍把那条命令留在"正在执行"状态，命令行挂着
        /// "文档正在执行命令，按回车取消或[重试]"；插件随即排队的 <c>X_PZ_EDIT</c> 只能排队等它结束 ——
        /// 用户看到的就是"双击没弹出编辑框，按一下 Esc 才出来"。没有原生命令，就没有这条挂起提示。</para>
        /// <para>原值在 <see cref="RestoreQuickPropertiesMode"/> 里还原（编辑命令结束、取消、卸载等所有出口都会走）。</para></summary>
        private static void SuppressDoubleClickEdit()
        {
            if (_doubleClickEditSuppressed) return;
            try
            {
                var value = CadApplication.GetSystemVariable("DBLCLKEDIT");
                if (Convert.ToInt32(value) == 0) return;      // 本来就是 0：不需要改，也没有要还原的
                _savedDoubleClickEdit = value;
                CadApplication.SetSystemVariable("DBLCLKEDIT", 0);
                _doubleClickEditSuppressed = true;
                PluginLog.Info("DoubleClick.SuppressDBLCLKEDIT", "双击期间临时把 DBLCLKEDIT 置 0（原值 " + value + "）。");
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.SuppressDBLCLKEDIT", ex); }
        }

        /// <summary>还原双击期间临时关掉的 DBLCLKEDIT。失败也不能影响后续流程。
        /// <para>编辑命令一进来就调用一次（见 <c>X_PZ_EDIT</c>）：<b>早还原</b>才不会因为编辑窗被人为留住、
        /// 命令异常退出而让用户的"双击编辑"能力长期失效。QPMODE 不能同时还原（预选还在，会弹快捷特性），
        /// 所以这两件事不绑在一起。</para></summary>
        internal static void RestoreDoubleClickEdit()
        {
            if (!_doubleClickEditSuppressed) return;
            var value = _savedDoubleClickEdit;
            _savedDoubleClickEdit = null;
            _doubleClickEditSuppressed = false;
            try { CadApplication.SetSystemVariable("DBLCLKEDIT", value ?? 1); }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreDBLCLKEDIT", ex); }
        }

        private static void SuppressQuickPropertiesMode()
        {
            if (_quickPropertiesSuppressed) return;
            try
            {
                var mode = CadApplication.GetSystemVariable("QPMODE");
                if (Convert.ToInt32(mode) == 0) return;
                _savedQuickPropertiesMode = mode;
                CadApplication.SetSystemVariable("QPMODE", 0);
                _quickPropertiesSuppressed = true;
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.SuppressQuickProperties", ex); }
        }

        /// <summary>编辑命令取得实体后恢复用户原有的 QPMODE，不永久改写 CAD 偏好。
        /// <para>双击期间临时改过的 <c>DBLCLKEDIT</c> 在这里一并还原：凡是"这一轮双击结束了"的出口
        /// （命令执行完、用户取消、切图、卸载）都会走到本方法，两个变量始终成对恢复。</para></summary>
        internal static void RestoreQuickPropertiesMode()
        {
            RestoreDoubleClickEdit();
            if (!_quickPropertiesSuppressed) return;
            var mode = _savedQuickPropertiesMode;
            _savedQuickPropertiesMode = null;
            _quickPropertiesSuppressed = false;
            try { CadApplication.SetSystemVariable("QPMODE", mode); }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreQuickProperties", ex); }
        }
    }
}
