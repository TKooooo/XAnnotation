using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    /// <summary>
    /// CAD 菜单安装器（对齐「批量打印」CadMenuInstaller）：
    /// COM 反射操作菜单；重复加载时复用同名菜单并原地重建菜单项；
    /// 已在菜单栏上的弹出菜单不重复 InsertInMenuBar。
    /// </summary>
    internal static class MenuInstaller
    {
        private const string MenuName = "X-批注";
#if ZWCAD
        private const string PreferredMenuGroupName = "ZWCAD";
#elif GSCAD
        // 浩辰主菜单组名取自其主 CUI（gcad.cuix）的 <MenuGroup Name="GCAD" DisplayName="GstarCAD">。
        // 若该组名匹配不到，Ensure() 会自动回退到菜单组集合的第 0 项，所以写错只会选不到首选组、不会失败。
        private const string PreferredMenuGroupName = "GCAD";
#else
        private const string PreferredMenuGroupName = "ACAD";
#endif


        private static bool _retryAttached;
        private static int _retryCount;
        private static DateTime _lastRetryUtc = DateTime.MinValue;

        /// <summary>两次重试之间至少间隔多久（毫秒）。Idle 在 CAD 空闲时触发得非常频繁，
        /// 不节流的话会不停去碰宿主的 COM 菜单接口。</summary>
        private const int RetryIntervalMs = 1000;

        /// <summary>最多重试多少次（每次受 <see cref="RetryIntervalMs"/> 节流）。</summary>
        private const int MaxRetryCount = 180;

        /// <summary>
        /// 加载时创建/刷新菜单；若宿主界面尚未就绪则挂 Idle 回调<b>持续</b>重试。
        /// NETLOAD / 启动自动加载时都会走这里，无需再手动执行 X_PZ_MENU。
        /// <para><b>为什么必须持续重试</b>：浩辰 GstarCAD 2026 随 CAD 启动自动加载时，
        /// 插件 Initialize 跑在 CAD 界面完全就绪之前 —— 那一刻读
        /// <c>Application.AcadApplication.MenuGroups.Count</c> 会抛
        /// <c>TargetInvocationException</c>（2026-10-13 实机日志 [Menu.Peek]）。
        /// 早先的实现只挂"一次"Idle 重试，而那个 Idle 紧接着 Initialize 就触发了、
        /// 界面依然没就绪，于是<b>一次失败之后再无任何尝试</b> → 浩辰菜单栏永远不出现 X-批注。</para>
        /// </summary>
        public static bool EnsureWithRetry()
        {
            if (Ensure(out var message))
            {
                PluginLog.Info("Menu", message);
                return true;
            }

            PluginLog.Warning("Menu", message ?? "菜单首次安装失败，将在宿主界面就绪后重试。");
            AttachRetry();
            return false;
        }

        /// <summary>卸载插件时摘掉 Idle 重试回调。</summary>
        public static void Detach()
        {
            DetachRetry();
        }

        /// <summary>挂上（幂等的）Idle 重试回调。</summary>
        private static void AttachRetry()
        {
            if (_retryAttached) return;
            try
            {
                _lastRetryUtc = DateTime.MinValue;
                CadApplication.Idle += OnRetryIdle;
                _retryAttached = true;
            }
            catch (Exception ex)
            {
                _retryAttached = false;
                PluginLog.Error("Menu.Retry", ex);
            }
        }

        private static void DetachRetry()
        {
            if (!_retryAttached) return;
            _retryAttached = false;
            try
            {
                CadApplication.Idle -= OnRetryIdle;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu.Detach", ex);
            }
        }

        private static void OnRetryIdle(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastRetryUtc).TotalMilliseconds < RetryIntervalMs) return;
            _lastRetryUtc = now;

            if (++_retryCount > MaxRetryCount)
            {
                DetachRetry();
                PluginLog.Warning("Menu.Retry",
                    "已重试 " + MaxRetryCount + " 次仍未取得菜单组，停止自动安装；需要时手动执行 X_PZ_MENU。");
                return;
            }

            if (Ensure(out var message))
            {
                DetachRetry();
                PluginLog.Info("Menu.Retry", message + "（第 " + _retryCount + " 次重试成功）");
                try
                {
                    var doc = CadApplication.DocumentManager.MdiActiveDocument;
                    doc?.Editor.WriteMessage("\n" + message);
                }
                catch
                {
                }

                return;
            }

            // 每次都写日志会把文件刷爆（Idle 一秒一次）：只记第 1 次和每 20 次一条。
            if (_retryCount == 1 || _retryCount % 20 == 0)
            {
                PluginLog.Warning("Menu.Retry", "第 " + _retryCount + " 次重试仍失败: " + (message ?? "(无消息)"));
            }
        }
        public static bool Ensure(out string message)
        {
            try
            {
                ShowMenuBar();

                object menuBar;
                object menuGroups;
#if ACAD_CORE || GSCAD
                // AutoCAD 2025+ / 浩辰：MenuBar / MenuGroups 走 COM 应用对象。
                // 浩辰特别注意：托管静态属性 Application.MenuGroups 返回的是内部 COM 后期绑定实例，
                // 界面完全就绪前调用会踩出 AccessViolationException（进程级内存损坏，之后一切行为变随机），
                // 必须改走官方 COM 接口 AcadApplication（与中望分支取工具栏用的是同一口径）。
                var acadApplication = GetAcadApplication();
                menuBar = acadApplication == null ? null : GetComProperty(acadApplication, "MenuBar");
                menuGroups = acadApplication == null ? null : GetComProperty(acadApplication, "MenuGroups");
#else
                var appType = typeof(UiApplication);
                menuBar = GetStatic(appType, "MenuBar");
                menuGroups = GetStatic(appType, "MenuGroups");
#endif
                if (menuBar == null || menuGroups == null)
                {
                    message = "当前 CAD 未公开菜单栏接口。";
                    return false;
                }

#if GSCAD
                // 取项之前先确认菜单组集合真的可用：读 Count 是最轻的探针，读不到或为 0 即"还没就绪"。
                // 少了这一步，界面未就绪时那次 Item 调用会直接踩出访问冲突。
                if (!IsMenuGroupsReady(menuGroups))
                {
                    message = "宿主菜单组尚未就绪（将在 Idle 时重试）。" + ProbeSummary(menuBar, menuGroups);
                    return false;
                }
#endif

                var menuGroup = InvokeItem(menuGroups, PreferredMenuGroupName)
                    ?? InvokeItem(menuGroups, 0);
                if (menuGroup == null)
                {
                    message = "未取得默认菜单组。";
                    return false;
                }

                var menus = GetComProperty(menuGroup, "Menus");
                if (menus == null)
                {
                    message = "未取得菜单集合。";
                    return false;
                }

                var menu = GetOrCreateMenu(menus);
                if (menu == null)
                {
                    message = "菜单创建失败。";
                    return false;
                }

                var oldCount = Convert.ToInt32(GetComProperty(menu, "Count") ?? 0);
                for (var i = 0; i < oldCount; i++)
                {
                    var first = TryInvoke(menu, "Item", 0);
                    if (first == null)
                    {
                        break;
                    }

                    TryInvoke(first, "Delete");
                }

                var index = 0;
                // 「批注绘制」弹出功能选择框：可选外形（矩形/菱形/椭圆）、连续绘制、多对一批注，并可跳转批注设置。
                AddCommandMenuItem(menu, index++, "批注绘制", "X_PZ_NOTE");
                // 「快速直绘」不加弹窗，按设置直接进入框选-定位流程（对应工具栏「快」键）。
                AddCommandMenuItem(menu, index++, "快速直绘", "X_PZ_DRAW");
                AddCommandMenuItem(menu, index++, "编辑批注", "X_PZ_EDIT");
                AddCommandMenuItem(menu, index++, "移动批注", "X_PZ_MOVE");
                AddCommandMenuItem(menu, index++, "删除批注", "X_PZ_DELETE");
                AddCommandMenuItem(menu, index++, "隐藏批注", "X_PZ_HIDE");
                AddCommandMenuItem(menu, index++, "显示批注", "X_PZ_SHOW");
                AddCommandMenuItem(menu, index++, "合并批注", "X_PZ_MERGE");
                AddCommandMenuItem(menu, index++, "过滤批注", "X_PZ_FILTER");
                AddCommandMenuItem(menu, index++, "格式刷", "X_PZ_FORMAT");
                AddCommandMenuItem(menu, index++, "刷新批注文字", "X_PZ_REFRESH");
                AddCommandMenuItem(menu, index++, "单绘云线", "X_PZ_CLOUD");
                AddCommandMenuItem(menu, index++, "增补云线", "X_PZ_ADDCLOUD");
                AddCommandMenuItem(menu, index++, "批注列表", "X_PZ_LIST");
                AddCommandMenuItem(menu, index++, "批注汇总", "X_PZ_SUMMARY");
                AddCommandMenuItem(menu, index++, "批注清单", "X_PZ_LEGEND");
                AddCommandMenuItem(menu, index++, "批注历史", "X_PZ_HISTORY");
                AddCommandMenuItem(menu, index++, "知识库", "X_PZ_KB");
                AddCommandMenuItem(menu, index++, "导出Word", "X_PZ_WORD");
                AddCommandMenuItem(menu, index++, "导出批注", "X_PZ_EXPORT");
                AddCommandMenuItem(menu, index++, "导入批注", "X_PZ_IMPORT");
                AddCommandMenuItem(menu, index++, "修复批注", "X_PZ_REPAIR");
                AddCommandMenuItem(menu, index++, "工具栏", "X_PZ_TOOLBAR");
                TryInvoke(menu, "AddSeparator", index++);
                AddCommandMenuItem(menu, index++, "批注设置", "X_PZ_SETTINGS");
                AddCommandMenuItem(menu, index++, "安装自动加载", "X_PZ_INSTALL_AUTOLOAD");
                AddCommandMenuItem(menu, index++, "卸载自动加载", "X_PZ_UNINSTALL_AUTOLOAD");
                AddCommandMenuItem(menu, index++, "重新加载菜单", "X_PZ_MENU");
                TryInvoke(menu, "AddSeparator", index++);
                AddCommandMenuItem(menu, index++, "关于", "X_PZ_ABOUT");

                if (!IsMenuOnMenuBar(menu))
                {
                    var menuBarCount = Convert.ToInt32(GetComProperty(menuBar, "Count") ?? 0);
                    TryInvoke(menu, "InsertInMenuBar", menuBarCount + 1);
                }

                if (!IsMenuOnMenuBar(menu))
                {
                    message = "X-批注菜单已创建，但未能插入菜单栏。";
                    return false;
                }

                message = "X-批注菜单已创建或刷新。";
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu", ex);
                message = "菜单创建失败: " + ex.Message;
                return false;
            }
        }

        private static void ShowMenuBar()
        {
            try
            {
                UiApplication.SetSystemVariable("MENUBAR", 1);
            }
            catch
            {
            }
        }

        private static object GetOrCreateMenu(object menus)
        {
            var menu = TryGetMenu(menus);
            if (menu != null)
            {
                return menu;
            }

            return TryInvoke(menus, "Add", MenuName);
        }

        private static object TryGetMenu(object menus)
        {
            try
            {
                return menus.GetType().InvokeMember(
                    "Item",
                    BindingFlags.InvokeMethod,
                    null,
                    menus,
                    new object[] { MenuName });
            }
            catch (TargetInvocationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsMenuOnMenuBar(object menu)
        {
            try
            {
                return Convert.ToBoolean(
                    menu.GetType().InvokeMember(
                        "OnMenuBar",
                        BindingFlags.GetProperty,
                        null,
                        menu,
                        null));
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void AddCommandMenuItem(object menu, int index, string displayName, string commandName)
        {
            TryInvoke(menu, "AddMenuItem", index, displayName, CreateMenuMacro(commandName));
        }

        internal static string CreateMenuMacro(string commandName)
        {
            return new string((char)3, 2) + "_" + commandName + " ";
        }

        internal static object GetStatic(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null, null);
        }

        /// <summary>探针：宿主的菜单组集合是否已经可用。
        /// <para>只读 <c>Count</c>（带 catch）：读不到或为 0 就认定"界面还没就绪"。
        /// 之所以必须先探再取，是因为浩辰的 MenuGroups 是 COM 后期绑定对象，
        /// 未就绪时任何 <c>Item(...)</c> 都会踩出 <c>AccessViolationException</c> ——
        /// 而访问冲突一旦发生，进程内存已损坏，之后插件的每个宿主调用都可能莫名其妙地失败。</para></summary>
        internal static bool IsMenuGroupsReady(object menuGroups)
        {
            if (menuGroups == null) return false;
            try
            {
                var count = menuGroups.GetType().InvokeMember(
                    "Count", BindingFlags.GetProperty, null, menuGroups, null);
                var value = Convert.ToInt32(count);
                if (value > 0) return true;
                PluginLog.Warning("Menu.Peek", "菜单组数量为 " + value + "，判定宿主界面尚未就绪。");
                return false;
            }
            catch (Exception ex)
            {
                // 内层异常一定要写出来：COM 后期绑定失败时外层永远只有一句
                // "调用的目标发生了异常"，真正的 HRESULT 与原因全在 InnerException 里。
                PluginLog.Warning("Menu.Peek", Describe(ex));
                return false;
            }
        }

        /// <summary>把异常连同内层链一起摊平成一行（COM 后期绑定的失败信息只在 InnerException 里）。
        /// <para>HRESULT 用 <see cref="Marshal.GetHRForException"/> 取：net40 的 <c>Exception.HResult</c>
        /// 还是 protected（4.5 才公开），直接读会编译不过。</para></summary>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            var sb = new StringBuilder();
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message)
              .Append(" (0x").Append(HResultOf(ex)).Append(')');
            var inner = ex.InnerException;
            var depth = 0;
            while (inner != null && depth++ < 4)
            {
                sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message)
                  .Append(" (0x").Append(HResultOf(inner)).Append(')');
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        private static string HResultOf(Exception ex)
        {
            try
            {
                return Marshal.GetHRForException(ex).ToString("X8");
            }
            catch
            {
                return "????????";
            }
        }

#if ACAD_CORE
        private static object GetAcadApplication()
        {
            var applicationTypeNames = new[]
            {
                "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
                "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
            };

            foreach (var typeName in applicationTypeNames)
            {
                var type = Type.GetType(typeName, throwOnError: false);
                if (type == null)
                {
                    continue;
                }

                var acadApplication = GetStatic(type, "AcadApplication");
                if (acadApplication != null)
                {
                    return acadApplication;
                }
            }

            return null;
        }
#elif GSCAD
        /// <summary>浩辰的 COM 应用对象。取 MenuBar / MenuGroups 一律走它，
        /// <b>不读托管静态属性</b> <c>Application.MenuGroups</c> —— 那个返回的是内部 COM 后期绑定实例，
        /// 在 CAD 界面就绪之前调用会踩出 <c>AccessViolationException</c>。
        /// <para>浩辰的 COM 属性名是 <c>AcadApplication</c>（与 AutoCAD 同名，不是中望的 ZcadApplication）。</para></summary>
        private static object GetAcadApplication()
        {
            try { return UiApplication.AcadApplication; }
            catch (Exception ex) { PluginLog.Error("Menu.AcadApplication", ex); return null; }
        }
#endif

#if GSCAD
        /// <summary>诊断用：把 MenuBar / MenuGroups 两层探针结果拼成一句话，便于下次从日志一眼看出卡在哪一层。
        /// <para>浩辰的 COM 菜单对象在界面就绪前调用会抛异常；如果重试之后依旧失败，
        /// 这条信息就是判断"只是没就绪"还是"API 形状不对"的唯一依据。</para></summary>
        private static string ProbeSummary(object menuBar, object menuGroups)
        {
            var sb = new StringBuilder(" [探测 MenuBar:");
            sb.Append(menuBar == null ? "null" : menuBar.GetType().Name);
            sb.Append(" Count=").Append(TryCount(menuBar));
            sb.Append(" | MenuGroups:");
            sb.Append(menuGroups == null ? "null" : menuGroups.GetType().Name);
            sb.Append(" Count=").Append(TryCount(menuGroups));
            sb.Append(']');
            return sb.ToString();
        }

        private static string TryCount(object target)
        {
            if (target == null) return "n/a";
            try
            {
                var count = target.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, target, null);
                return Convert.ToString(count) ?? "(null)";
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }
#endif

        /// <summary>取宿主的 COM 应用对象（AcadApplication）；托管 API 没有等价物的宿主返回 null。
        /// <para>取 MenuBar / MenuGroups 一律走它，而不要读托管静态属性：
        /// 后者在界面就绪前可能返回半初始化的 COM 实例，调用即进程级访问冲突。</para></summary>
        internal static object GetComApplication()
        {
#if ACAD_CORE || GSCAD
            return GetAcadApplication();
#else
            return null;
#endif
        }

        private static object GetComProperty(object target, string name)
        {
            try
            {
                return target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty,
                    null,
                    target,
                    null);
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeItem(object collection, object key)
        {
            return TryInvoke(collection, "Item", key);
        }

        private static object TryInvoke(object target, string method, params object[] args)
        {
            try
            {
                return target.GetType().InvokeMember(
                    method,
                    BindingFlags.InvokeMethod,
                    null,
                    target,
                    args);
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class ReflectionTool
    {
        /// <summary>读属性；目标为 null 或宿主侧调用失败时返回 null，绝不冒泡。
        /// <para>COM 后期绑定调用在宿主侧出问题时抛什么类型完全不可预期
        /// （<c>TargetInvocationException</c> / <c>ArgumentException</c> / <c>MissingMethodException</c>），
        /// 调用方一律按"拿不到"处理，因此这里统一吞掉并记日志。</para></summary>
        public static object GetProperty(this object obj, string key)
        {
            if (obj == null) return null;
            try
            {
                return obj.GetType().InvokeMember(key, BindingFlags.GetProperty, null, obj, null);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Reflection.GetProperty." + key, ex.Message);
                return null;
            }
        }

        /// <summary>调方法；目标为 null 或宿主侧调用失败时返回 null，绝不冒泡。
        /// <para><b>为什么一定要吞</b>：浩辰的菜单/工具栏对象是 COM 后期绑定实例，
        /// 在 CAD 界面完全就绪之前调用会踩出 <c>AccessViolationException</c>（访问冲突）。
        /// 它一旦冒泡，进程内存已被破坏，后续所有宿主调用都会变得随机 ——
        /// 2026-10-13 的实机日志里，同一份 DLL 连续三次加载，菜单一次"未取得默认菜单组"、
        /// 两次"已创建但未能插入菜单栏"，就是这种不确定性的直接表现。</para></summary>
        public static object InvokeMethod(this object obj, string method, params object[] objArray)
        {
            if (obj == null) return null;
            try
            {
                return obj.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, obj, objArray);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Reflection.Invoke." + method, ex.Message);
                return null;
            }
        }
    }
}