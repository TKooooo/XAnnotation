using System;
using System.Reflection;

namespace XAnnotation
{
    /// <summary>
    /// 产品与联系信息常量（关于框等 UI 共用，避免散落硬编码）。
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>产品显示名。</summary>
        public const string ProductName = "X-批注";

        /// <summary>设计者署名。</summary>
        public const string Designer = "影子";

        /// <summary>联系邮箱。留空表示不填 / 不展示。</summary>
        public const string Email = "";

        /// <summary>QQ 群号。留空表示不填 / 不展示。</summary>
        public const string QqGroup = "";

        /// <summary>
        /// 关于框最下方的来源声明（含原始作者署名，请勿删除）。
        /// </summary>
        public const string Credit =
            "本插件由“爱德华hehe”的“GM批注”修改而来，感谢“爱德华hehe”的无私奉献。";

        /// <summary>
        /// 从当前程序集读取版本号并格式化为带 V 前缀的显示文本。
        /// 修订号为 0 时显示三段（如 V0.5.2），否则显示四段（如 V0.5.2.1）。
        /// </summary>
        /// <returns>格式化后的版本字符串。</returns>
        public static string GetDisplayVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0, 0);
            if (version.Revision <= 0)
            {
                return $"V{version.Major}.{version.Minor}.{version.Build}";
            }

            return $"V{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }
}
