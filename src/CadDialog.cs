using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
using CadDocument = ZwSoft.ZwCAD.ApplicationServices.Document;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using UiApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
using CadDocument = Gssoft.Gscad.ApplicationServices.Document;
#else
#if ACAD_PRE2013
// AutoCAD 2012 及更早：还没有 AcCoreMgd 拆分，只有 ApplicationServices.Application 一个入口。
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif
using CadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
#endif

namespace XAnnotation
{
    /// <summary>WPF 窗口适配层：模态走 CAD 原生 ShowModalWindow（失败回退 Window.ShowDialog），
    /// 非模态走 Window.Show + 挂 CAD 主窗口。</summary>
    internal static class CadDialog
    {
        public static bool? ShowModal(Window window)
        {
            // 兜底再确认一次宿主 WPF Application 的 ShutdownMode（构造期已确认过一次，见 WpfHost）：
            // 宿主的 Application 有可能是在本插件加载之后才建出来的，那时构造期的守护是空跑。
            WpfHost.Ensure();
            // 挂主窗口失败不应阻断弹窗：仅记日志，继续走后续流程（窗口会退回居中屏幕显示）。
            try { AttachToCadMainWindow(window); }
            catch (Exception ex) { PluginLog.Error("WPF.AttachToCadMainWindow", ex); }
            // 自己声明了"居中于屏幕"的窗口（批注内容窗、格式刷窗），由我们再摆一次：
            // 光靠 XAML 的 CenterScreen 落点不可靠，见 CenterOnMonitor 的说明。
            if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen) CenterOnMonitor(window);
            window.Loaded += BringToForeground;
            // 模态窗口弹出前先把其它置顶漂浮窗口（批注面板 / 批注设置…）降下来：
            // 我们的漂浮窗口都是 Topmost（要压在 CAD 之上），而两个 Topmost 窗口谁也不能盖住谁 ——
            // 不降的话，模态窗口会躲在它们后面（例如"设置窗口还开着时开始画批注，内容窗看不见"）。
            // 模态结束后原样恢复。
            var lowered = LowerTopmostWindows(window);
            try
            {
                try
                {
                    var method=typeof(UiApplication).GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="ShowModalWindow"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Window)));
                    if(method!=null){var result=method.Invoke(null,new object[]{window});if(result is bool b)return b;return window.DialogResult;}
                }
                catch(Exception ex){PluginLog.Error("WPF.ShowModalWindow",ex);}
                try { return window.ShowDialog(); }
                finally { window.Loaded -= BringToForeground; }
            }
            finally { RestoreTopmost(lowered); }
        }

        /// <summary>以<b>非模态</b>方式显示窗口（挂到 CAD 主窗口，保持压在 CAD 之上）。
        /// <para>批注设置窗口原先走 <see cref="ShowModal(Window)"/>（CAD 原生模态）。模态会禁用其它顶层窗口，
        /// 于是"设置窗口一开，批注面板既点不动也不能单独关、必须先退出设置窗口"；
        /// 改成非模态后，两个窗口各自独立：各拖各的、各关各的，点谁谁在前（见 WpfWindowUtil.BringToTop）。</para></summary>
        public static void ShowModeless(Window window)
        {
            if (window == null) return;
            WpfHost.Ensure();
            try { AttachToCadMainWindow(window); }
            catch (Exception ex) { PluginLog.Error("WPF.AttachToCadMainWindow", ex); }
            window.Show();
        }

        /// <summary>本插件自己开的置顶漂浮窗口（弱引用）。用于"弹模态窗口前把它们临时降下去"：
        /// 宿主里 <c>Application.Current</c> 不一定存在（CAD 不是 WPF 应用），光靠枚举它不保险。</summary>
        private static readonly List<WeakReference> FloatingWindows = new List<WeakReference>();

        /// <summary>登记一个置顶漂浮窗口（批注面板、批注设置…），供弹模态窗口时临时降级。
        /// 登记一次即可，重复登记也无害（会去重）。</summary>
        public static void RegisterFloating(Window window)
        {
            if (window == null) return;
            try
            {
                PurgeFloating();
                foreach (var reference in FloatingWindows)
                    if (ReferenceEquals(reference.Target, window)) return;
                FloatingWindows.Add(new WeakReference(window));
            }
            catch (Exception ex) { PluginLog.Warning("WPF.RegisterFloating", ex.Message); }
        }

        /// <summary>清掉登记表里已经销毁的窗口，免得越攒越多。</summary>
        private static void PurgeFloating()
        {
            for (var i = FloatingWindows.Count - 1; i >= 0; i--)
            {
                if (!(FloatingWindows[i].Target is Window)) FloatingWindows.RemoveAt(i);
            }
        }

        /// <summary>本插件当前活着的漂浮窗口（登记表 ∪ <c>Application.Current.Windows</c>，已去重）。
        /// <para>给"光标 / 前台窗口是不是落在我们自己的窗口上"这类判断用（见
        /// <c>AnnotationPanel.IsCursorOverOwnWindow</c> / <c>IsForegroundUsableForHoverStart</c>）：
        /// 宿主里 <c>Application.Current</c> 不一定存在（CAD 不是 WPF 应用），只靠它会把我们自己的
        /// 窗口误判成"CAD 主窗口"，于是鼠标划过批注设置/批注列表就可能擅自开始一轮。</para></summary>
        public static List<Window> GetFloatingWindows()
        {
            var result = new List<Window>();
            try
            {
                PurgeFloating();
                foreach (var reference in FloatingWindows)
                {
                    if (reference.Target is Window registered && !result.Contains(registered)) result.Add(registered);
                }
            }
            catch (Exception ex) { PluginLog.Warning("WPF.GetFloatingWindows.registry", ex.Message); }

            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    foreach (Window w in app.Windows)
                    {
                        if (w != null && !result.Contains(w)) result.Add(w);
                    }
                }
            }
            catch (Exception ex) { PluginLog.Warning("WPF.GetFloatingWindows.application", ex.Message); }

            return result;
        }

        /// <summary>把本插件其它<b>置顶</b>窗口临时降为普通窗口（跳过一个：即将显示的模态窗口本身）。
        /// 只动 <c>Topmost</c>，不动可见性 / 焦点，模态结束后由 <see cref="RestoreTopmost"/> 原样恢复。</summary>
        private static List<Window> LowerTopmostWindows(Window except)
        {
            var lowered = new List<Window>();
            var candidates = new List<Window>();
            try
            {
                PurgeFloating();
                foreach (var reference in FloatingWindows)
                {
                    if (reference.Target is Window registered) candidates.Add(registered);
                }
            }
            catch (Exception ex) { PluginLog.Warning("WPF.LowerTopmostWindows.registry", ex.Message); }

            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    foreach (Window w in app.Windows)
                    {
                        if (w != null && !candidates.Contains(w)) candidates.Add(w);
                    }
                }
            }
            catch (Exception ex) { PluginLog.Warning("WPF.LowerTopmostWindows.application", ex.Message); }

            foreach (var w in candidates)
            {
                try
                {
                    if (ReferenceEquals(w, except) || lowered.Contains(w) || !w.IsLoaded || !w.Topmost) continue;
                    w.Topmost = false;
                    lowered.Add(w);
                }
                catch (Exception ex) { PluginLog.Warning("WPF.LowerTopmostWindows", ex.Message); }
            }

            return lowered;
        }

        private static void RestoreTopmost(List<Window> windows)
        {
            if (windows == null) return;
            foreach (var w in windows)
            {
                try { if (w.IsLoaded) w.Topmost = true; }
                catch (Exception ex) { PluginLog.Warning("WPF.RestoreTopmost", ex.Message); }
            }
        }

        /// <summary>打开批注窗口，并处理"拾取图面文字"请求。
        /// 批注窗口是 CAD 模态窗口，显示期间无法与图形交互，所以"拾取文字"的流程是：
        /// 窗口把已填内容存回数据对象后关闭 → 在图上点选文字 → 重开窗口（已填内容原样带回）。</summary>
        public static bool ShowAnnotation(AnnotationData data, bool editing)
        {
            CadDocument doc = null;
            try { doc = UiApplication.DocumentManager.MdiActiveDocument; }
            catch (Exception ex) { PluginLog.Error("AnnotationDialog.Document", ex); }

            while (true)
            {
                var form = new Views.AnnotationWindow(data, editing);
                var result = ShowModal(form);
                if (form.AppendCloudRequested)
                {
                    // 「增补云线」：窗口已把已填内容存回 data 并关闭。编辑器现在可交互，
                    // 执行连续框选的增补会话（图层/颜色/线宽照抄原批注，**外形也照原批注**，云线样式/尺寸按当前设置），
                    // 随后重开窗口继续编辑。
                    if (doc == null || doc.IsDisposed)
                    {
                        PluginLog.Warning("AnnotationDialog.AppendCloud", "当前没有可用的图纸，已取消增补。");
                        return false;
                    }
                    try { Commands.AppendCloudSession(doc, data); }
                    catch (Exception ex)
                    {
                        PluginLog.Error("AnnotationDialog.AppendCloud", ex);
                        doc.Editor.WriteMessage("\n增补云线失败: " + ex.Message);
                    }
                    continue;
                }
                if (!form.PickTextRequested) return result == true;
                if (doc == null || doc.IsDisposed)
                {
                    PluginLog.Warning("AnnotationDialog.PickText", "当前没有可用的图纸，已取消拾取。");
                    return false;
                }
                try
                {
                    var picked = AnnotationService.PickText(doc);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        data.DrawingNo = picked;
                        doc.Editor.WriteMessage("\n已拾取图号: " + picked);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error("AnnotationDialog.PickText", ex);
                    doc.Editor.WriteMessage("\n拾取图号文字失败: " + ex.Message);
                }
            }
        }

        /// <summary>显式把 WPF 窗口挂到 CAD 主窗口（模态窗口挂不上时可能落到 CAD 后面）。
        /// <para>定位规则：<b>窗口自己表过态就不动它</b> —— <see cref="WindowStartupLocation.Manual"/>
        /// 是调用方已经摆好的位置（如设置窗口贴在面板旁边），<see cref="WindowStartupLocation.CenterScreen"/>
        /// 是窗口明确要求"居中于屏幕"（批注内容窗口，XAML 里写的）。
        /// 只有 <see cref="WindowStartupLocation.CenterOwner"/> 这类"居中于属主"的默认意图，
        /// 才按挂载结果决定居中于 CAD 主窗口还是屏幕。</para></summary>
        private static void AttachToCadMainWindow(Window window)
        {
            var handle=GetCadMainWindowHandle();
            if(handle!=IntPtr.Zero)
                new WindowInteropHelper(window).Owner=handle;

            var location=window.WindowStartupLocation;
            if(location==WindowStartupLocation.Manual||location==WindowStartupLocation.CenterScreen)return;
            window.WindowStartupLocation=handle!=IntPtr.Zero?WindowStartupLocation.CenterOwner:WindowStartupLocation.CenterScreen;
        }

        #region 居中到显示器

        private const uint MonitorDefaultToNearest = 2;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorInfo
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>把窗口摆到 CAD 所在显示器的中间（工作区内，避开任务栏）。
        /// <para><b>为什么不能只靠 XAML 的 <c>WindowStartupLocation="CenterScreen"</c>：</b>
        /// WPF 是在建窗阶段算位置的，那时窗口还没量出真实大小；我们又给窗口挂了一个宿主 HWND 作 Owner、
        /// 模态还可能被 CAD 的 <c>ShowModalWindow</c> 再包一层，最终落点经常偏出屏幕中心
        /// （批注内容窗的实测表现就是"没在显示器居中"）。</para>
        /// <para>做法：等 Loaded（布局完成、首帧渲染之前）拿窗口真实外框，按显示器<b>物理像素</b>工作区算中点，
        /// 再用 <c>SetWindowPos</c> 直接挪过去。全程物理像素，不掺 DIP/DPI 换算，多屏 + 不同缩放比例都不会算歪；
        /// 目标显示器取 <b>CAD 主窗口</b>所在那块（用户眼睛在那儿），取不到才退回窗口自己所在的显示器。</para></summary>
        public static void CenterOnMonitor(Window window)
        {
            if (window == null) return;
            window.Loaded += (s, e) =>
            {
                try { CenterNow(window); }
                catch (Exception ex) { PluginLog.Warning("WPF.CenterOnMonitor", ex.Message); }
            };
        }

        private static void CenterNow(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var monitor = MonitorFromWindow(GetCadMainWindowHandle(), MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return;

            var info = new NativeMonitorInfo { cbSize = Marshal.SizeOf(typeof(NativeMonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info)) return;

            NativeRect current;
            if (!GetWindowRect(hwnd, out current)) return;
            var width = current.Right - current.Left;
            var height = current.Bottom - current.Top;
            if (width <= 0 || height <= 0) return;

            var work = info.rcWork;
            var x = work.Left + ((work.Right - work.Left) - width) / 2;
            var y = work.Top + ((work.Bottom - work.Top) - height) / 2;
            if (x == current.Left && y == current.Top) return;

            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
            {
                PluginLog.Warning("WPF.CenterOnMonitor", "SetWindowPos 失败，窗口位置保持原样。");
                return;
            }
            PluginLog.Info("WPF.CenterOnMonitor",
                $"{window.GetType().Name} 从 ({current.Left},{current.Top}) 移到 ({x},{y})，工作区 {work.Left},{work.Top}-{work.Right},{work.Bottom}，窗口 {width}x{height}");
        }

        #endregion

        private static IntPtr GetCadMainWindowHandle()
        {
            try
            {
                var mainWindowProperty=typeof(UiApplication).GetProperty("MainWindow",BindingFlags.Public|BindingFlags.Static);
                var mainWindow=mainWindowProperty?.GetValue(null,null);
                var handleProperty=mainWindow?.GetType().GetProperty("Handle",BindingFlags.Public|BindingFlags.Instance);
                var value=handleProperty?.GetValue(mainWindow,null);
                if(value is IntPtr handle&&handle!=IntPtr.Zero)return handle;
            }
            catch(Exception ex){PluginLog.Error("WPF.MainWindowHandle",ex);}
            return Process.GetCurrentProcess().MainWindowHandle;
        }

        private static void BringToForeground(object sender,RoutedEventArgs e)
        {
            var window=(Window)sender;
            window.Activate();
            window.Focus();
        }
    }
}
