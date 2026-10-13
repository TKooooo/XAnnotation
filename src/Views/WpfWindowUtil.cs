using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace XAnnotation.Views
{
    /// <summary>漂浮窗口共用的小工具：置顶层级 + 可视树查找。</summary>
    internal static class WpfWindowUtil
    {
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>把窗口顶到"置顶组"的最上层 —— 实现"两个漂浮窗口点谁谁在前"。
        /// <para>宿主里的漂浮窗口都必须是 Topmost（否则用户回图纸画一笔，窗口就沉到 CAD 后面看不见了），
        /// 但两个 Topmost 窗口相互之间不会自动让位：点了后面那个，它虽然拿到焦点，却仍可能被前面那个挡住。
        /// 于是在 <c>Activated</c> 时主动 <c>SetWindowPos(HWND_TOPMOST)</c> 把它顶上去，
        /// 两个窗口就能各拖各的、各关各的，互不干扰。</para>
        /// <para><b>为什么不用另外两种常见做法</b>：
        /// ① <c>Activated</c> 置 <c>Topmost=true</c> / <c>Deactivated</c> 置 <c>Topmost=false</c> ——
        /// 这会让窗口在用户回图纸画图时也沉下去，而本插件的窗口必须<b>始终</b>压在 CAD 之上；
        /// ② 让设置窗口走模态 —— 模态会禁用其它顶层窗口（正是"设置窗口一开、批注面板既点不动也关不掉"的根因）。</para></summary>
        public static void BringToTop(Window window)
        {
            if (window == null) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle; // 句柄还没创建时返回 IntPtr.Zero
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("WpfWindowUtil.BringToTop", ex.Message);
            }
        }

        /// <summary>沿可视树 / 逻辑树向上找最近的 <see cref="ComboBox"/>（用于"下拉展开时第一次 Esc 只收下拉"）。</summary>
        public static ComboBox FindAncestorComboBox(DependencyObject node)
        {
            while (node != null)
            {
                if (node is ComboBox combo) return combo;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }
    }
}
