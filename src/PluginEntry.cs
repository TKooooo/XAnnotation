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
                PluginLog.Info("Initialize", "构建于 " + buildTime.ToString("yyyy-MM-dd HH:mm:ss") + "，加载自 " + asm.Location);

                // 环境快照先落地：哪怕是"加载到一半炸了"，也已经留下可判断的现场信息。
                LogEnvironment();

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
                    try { ObserveDatabase(doc.Database); }
                    catch (System.Exception ex) { failedSteps.Add("ObserveDatabase"); PluginLog.Error("Initialize.ObserveDatabase", ex); }
                }

                // "初始化完成"必须留痕：日志里只有"构建于…"而没有这一条，就说明加载真被打断了。
                PluginLog.Info("Initialize", "初始化完成。菜单=" + menuReady + "，工具栏=" + toolbarReady
                    + "，失败步骤=" + (failedSteps.Count == 0 ? "无" : string.Join("/", failedSteps.ToArray())));

                var message =
                    "\nX-批注已加载。" + (menuReady ? " 菜单已就绪。" : " 菜单稍后自动挂上（或用 X_PZ_MENU）。") +
                    (toolbarReady ? " X-批注工具栏已就绪。" : " X-批注工具栏未就绪（将自动重试，或用 X_PZ_TOOLBAR）。") +
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
            RestoreQuickPropertiesMode();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                // 布防途中切了图纸：作废本次双击，并把临时改掉的 QPMODE 还回去。
                if (_doubleClickArmed) { DisarmDoubleClick(); RestoreQuickPropertiesMode(); }
                AnnotationService.SyncNextNumber(e.Document);
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
                if (!SettingsStore.Load().DoubleClickEdit) return;
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

        /// <summary>用双击点 + 系统变量 PICKBOX 反算光标拾取框的四个角点，再做一次交叉多边形选择。
        /// 这是在 BeginDoubleClick 里判断"到底点中了什么"最可靠的做法：不依赖预选集状态。</summary>
        private static ObjectId HitTestAtPickBox(Document doc, Point3d location)
        {
            try
            {
                var ed = doc.Editor;
                // 注意返回类型是 System.Windows.Point（X/Y 为 double），**不是** System.Drawing.Point；
                // 其逆变换 PointToWorld 也接收 System.Windows.Point。视口号取 1（Autodesk 官方示例同此）。
                var screen = ed.PointToScreen(location, 1);
                var half = (double)IntSysVar("PICKBOX", 4);   // PICKBOX 是拾取框的"半个边长"（像素）
                if (half <= 0) half = 4;                      // PICKBOX=0 时拾取框退化成一个点，兜一个最小框，否则什么都选不到

                var corners = new Point3dCollection();
                corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X - half, screen.Y - half), 1));
                corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X + half, screen.Y - half), 1));
                corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X + half, screen.Y + half), 1));
                corners.Add(ed.PointToWorld(new System.Windows.Point(screen.X - half, screen.Y + half), 1));

                var result = ed.SelectCrossingPolygon(corners);
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0) return ObjectId.Null;

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
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.HitTest", ex); return ObjectId.Null; }
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
                if (elapsed > FallbackDelaySeconds) LaunchPendingEdit("未收到原生双击命令");
                else idleStillNeeded = true;
            }

            if (!idleStillNeeded) DetachIdle();
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
                doc.Editor.SetImpliedSelection(new[] { id });
                doc.SendStringToExecute("X_PZ_EDIT ", true, false, false);
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

        /// <summary>编辑命令取得实体后恢复用户原有的 QPMODE，不永久改写 CAD 偏好。</summary>
        internal static void RestoreQuickPropertiesMode()
        {
            if (!_quickPropertiesSuppressed) return;
            var mode = _savedQuickPropertiesMode;
            _savedQuickPropertiesMode = null;
            _quickPropertiesSuppressed = false;
            try { CadApplication.SetSystemVariable("QPMODE", mode); }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreQuickProperties", ex); }
        }
    }
}
