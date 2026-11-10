using System;
using System.Windows;
using System.Windows.Input;

namespace XAnnotation.Views
{
    /// <summary>
    /// 「关于 X-批注」对话框：展示产品名、程序集版本与固定联系信息。
    /// </summary>
    internal partial class AboutWindow : Window
    {
        /// <summary>
        /// 初始化关于框，填充产品名、版本与联系信息。
        /// </summary>
        public AboutWindow()
        {
            WpfHost.Ensure();
            InitializeComponent();
            Title = "关于 " + AppInfo.ProductName;
            ProductNameText.Text = AppInfo.ProductName;
            VersionText.Text = AppInfo.GetDisplayVersion();
            DesignerText.Text = AppInfo.Designer;
            EmailText.Text = AppInfo.Email;
            QqGroupText.Text = AppInfo.QqGroup;
            CreditText.Text = AppInfo.Credit;

            // 邮箱 / QQ 群留空：把「复制 QQ 群」按钮收起（复制空文本既没意义，也和界面不符）。
            if (string.IsNullOrWhiteSpace(AppInfo.QqGroup))
            {
                CopyQqButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 关闭对话框。
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        /// <summary>Esc 关闭：关于框只有「确定」一个出口，用户按 Esc 的预期就是关掉它。
        /// <para>放在 Preview 阶段做，与本窗口里哪个控件拿着焦点无关（不依赖按钮的 IsCancel）。
        /// 宿主那边面板挂的全局 Esc 钩子只是借这一下做收尾判断、从不吞键，所以这里能拿到完整的 Esc。</para></summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>
        /// 将 QQ 群号复制到剪贴板；失败时弹窗直接显示群号。
        /// </summary>
        private void CopyQq_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(AppInfo.QqGroup);
                MessageBox.Show(
                    this,
                    "QQ群号 " + AppInfo.QqGroup + " 已复制到剪贴板。",
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception)
            {
                MessageBox.Show(
                    this,
                    "复制失败，请手动记录 QQ 群号：\n" + AppInfo.QqGroup,
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
