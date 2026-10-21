using System;
using System.Windows;
using System.Windows.Threading;

namespace XAnnotation
{
    /// <summary>
    /// 宿主进程里 WPF <see cref="Application"/> 对象的守护：防止"插件把最后一个窗口一关，
    /// WPF 就把宿主的 Application 一起关掉，之后本插件再也弹不出任何窗口"。
    ///
    /// <para><b>现场症状</b>：AutoCAD 2014 里用着用着，批注突然画不出来了，命令行只有一句
    /// <c>绘制批注失败: 应用程序对象正在关闭。</c>（日志里是
    /// <c>System.InvalidOperationException: 应用程序对象正在关闭。</c>，栈顶为
    /// <c>System.Windows.Application.GetResourcePackage</c> → <c>GetResourceOrContentPart</c> →
    /// <c>LoadComponent</c> → 某个窗口的 <c>.ctor</c>）。此后<b>本次 CAD 会话内不可恢复</b>，
    /// 连批注面板都打不开，只能重启 CAD。</para>
    ///
    /// <para><b>根因</b>（WPF 的行为，不是本插件的 bug 逻辑）：
    /// <list type="number">
    /// <item>CAD 宿主进程里存在一个 <c>System.Windows.Application</c> 实例（AutoCAD 2014 实测有，
    /// 由宿主自己的 WPF 界面创建；本插件从不创建它，只在 <c>CadDialog</c> 里读 <c>Application.Current</c>）。</item>
    /// <item>该实例的 <c>ShutdownMode</c> 是默认值 <c>OnLastWindowClose</c>：
    /// <b>挂在它名下的最后一个 WPF 窗口一关，WPF 会自动调用 <c>Application.Shutdown()</c></b>。
    /// 在 CAD 这种原生宿主里，它名下的窗口全是本插件的窗口 —— 于是"关掉批注面板"就足以触发。</item>
    /// <item><c>Application.Shutdown()</c> 会卸载该 Application 关联的资源包
    /// （<c>pack://application:,,,/…</c>）。之后再加载任何 XAML：
    /// <c>GetResourcePackage</c> 拿不到 package，WPF 判定这是"正在关闭"，抛
    /// <c>InvalidOperationException(应用程序对象正在关闭)</c>。</item>
    /// <item>一个 AppDomain 只允许创建一个 <c>Application</c>（关掉了也建不出第二个），
    /// 所以这个坏状态<b>一直持续到进程结束</b>。</item>
    /// </list>
    /// 这也解释了为什么是"有一定概率"：只有"关掉的是最后一个还活着的插件窗口"那一次才会踩中，
    /// 踩中之后同一会话里所有窗口创建都必然失败；重启 CAD 就恢复正常（日志里三处现场都是这个规律）。</para>
    ///
    /// <para><b>解法</b>（微软 KB2691806 给的官方做法）：把宿主的 <c>ShutdownMode</c> 改成
    /// <c>OnExplicitShutdown</c> —— 只有显式调用 <c>Shutdown()</c> 才关，我们自己的窗口关光了也不影响。
    /// 时机上必须在<b>第一次关窗口之前</b>做，因此插件初始化时调一次，并在每个窗口<b>构造之前</b>再调一次
    /// （幂等；也覆盖"宿主的 Application 是在插件加载之后才创建"的情况）。</para>
    ///
    /// <para>注意：一旦那个 Application 已经处于关闭状态，<b>改 ShutdownMode 也救不回来</b>
    /// （属性会抛 <c>InvalidOperationException</c>），此时只能重启 CAD。所以
    /// <see cref="Describe(Exception)"/> 会把这种无解状态翻译成一句能照着做的提示。</para>
    /// </summary>
    internal static class WpfHost
    {
        /// <summary>已经处理过的 Application 实例。CAS 单线程无虞：全部调用都在 CAD 的 UI 线程上。</summary>
        private static Application _fixated;

        /// <summary>"宿主没有 Application"只记一次日志，避免每个窗口都刷一行。</summary>
        private static bool _absentLogged;

        /// <summary>命令行/对话框里用的提示语：干净、可照做。</summary>
        private const string RestartHint =
            "CAD 的 WPF 应用对象已被关闭，本次 CAD 会话内无法再弹出插件窗口（WPF 每个进程只允许一个 Application 实例）。"
            + "请重启 AutoCAD 后重试；如反复出现，请把日志 XAnnotation.log 反馈给开发者。";

        /// <summary>
        /// 确保宿主的 WPF Application 不会因"最后一个插件窗口关闭"而自动关掉。
        /// <para>幂等、可在任意时刻（UI 线程）调用；任何失败都只记日志，不抛出 ——
        /// 弹窗链路上的守护绝不能自己变成新的崩溃点。</para>
        /// </summary>
        /// <returns><c>true</c> 表示窗口可以安全创建；<c>false</c> 表示那个 Application 已经关了，本次会话弹不出窗口。</returns>
        public static bool Ensure()
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                {
                    // 宿主没有 Application：WPF 窗口照样能用，也不存在"关最后一个窗口就自动 Shutdown"这条规则，
                    // 因此什么都不用做（ZWCAD / 浩辰 / 某些 AutoCAD 版本就是这种情形）。
                    if (!_absentLogged)
                    {
                        _absentLogged = true;
                        PluginLog.Info("WpfHost", "宿主进程无 WPF Application：无需处理（窗口会自动存活到进程结束）。");
                    }
                    return true;
                }

                if (ReferenceEquals(app, _fixated)) return true;

                Dispatcher dispatcher = null;
                try { dispatcher = app.Dispatcher; } catch { }
                if (dispatcher != null && dispatcher.HasShutdownStarted)
                {
                    ReportShutdown();
                    return false;
                }

                var mode = app.ShutdownMode;
                if (mode == ShutdownMode.OnExplicitShutdown)
                {
                    _fixated = app;
                    PluginLog.Info("WpfHost", "宿主 WPF Application 已是 OnExplicitShutdown，无需改动。");
                    return true;
                }

                try
                {
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _fixated = app;
                    PluginLog.Info("WpfHost",
                        "宿主 WPF Application 的 ShutdownMode=" + mode + "，已改为 OnExplicitShutdown。"
                        + "否则本插件最后一个窗口关闭时 WPF 会自动关闭该 Application，导致本次 CAD 会话内再也弹不出窗口"
                        + "（InvalidOperationException: 应用程序对象正在关闭）。");
                    return true;
                }
                catch (Exception ex)
                {
                    // Application 已经进入关闭流程时改 ShutdownMode 会被拒（ShutdownModeWhenAppShutdown）；
                    // 也可能是宿主把 Application 建在别的线程上（跨线程写属性同样被拒）。
                    // 只有"调度器确实已经开始关闭"才判定为无解状态，否则仅留痕、不影响建窗。
                    PluginLog.Warning("WpfHost", "把 ShutdownMode 改为 OnExplicitShutdown 失败：" + ex.Message);
                    try { dispatcher = app.Dispatcher; } catch { dispatcher = null; }
                    if (dispatcher != null && dispatcher.HasShutdownStarted)
                    {
                        ReportShutdown();
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning("WpfHost.Ensure", ex.Message);
                return false;
            }
        }

        /// <summary>把异常翻译成给用户看的一句话：若是"WPF 应用对象已关闭"这个无解状态，就说清该怎么办。</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return string.Empty;
            try
            {
                var app = Application.Current;
                var dispatcher = app?.Dispatcher;
                if (dispatcher != null && dispatcher.HasShutdownStarted) return RestartHint;
            }
            catch
            {
                // 取状态本身失败就退回原始消息。
            }
            return ex.Message;
        }

        /// <summary>失败留痕只记一次（同一会话里每次开窗口都会走到这里，不必刷屏）。</summary>
        private static void ReportShutdown()
        {
            if (_shutdownReported) return;
            _shutdownReported = true;
            PluginLog.Warning("WpfHost", "宿主 WPF Application 已处于关闭状态：" + RestartHint);
        }

        private static bool _shutdownReported;
    }
}
