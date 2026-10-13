using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using WinRegistry = Microsoft.Win32.Registry;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace XAnnotation
{
    /// <summary>
    /// 自动加载管理器（对齐「批量打印」/ IFoxCAD）：
    /// 只写入当前正在运行的 CAD 的 <c>UserRegistryProductRootKey\Applications</c>，
    /// 不扫其它年份版本。Applications 不存在时 CreateSubKey 创建。
    /// 本产品是<b>独立品牌</b>：只管理自己的键 <c>XAnnotation</c>，
    /// 不去清理「GM批注」留下的键 —— 那是另一个插件，删了会把原版一起卸掉。
    /// </summary>
    internal static class AutoloadManager
    {
        private const string AppKeyName = "XAnnotation";

        /// <summary>
        /// 本产品自己更早用过的键名（用于改名后的双加载清理）。当前没有历史键，留 <c>null</c>。
        /// 注意**不要**填 "GMAnnotation"：那会把用户机器上原版「GM批注」的自动加载一并删掉。
        /// </summary>
        private const string LegacyAppKeyName = null;

        private const string AppDescription = "X-批注插件";

        public static string CurrentDllPath =>
            Assembly.GetExecutingAssembly().Location;

        public static bool IsInstalled(out string dllPath)
        {
            dllPath = string.Empty;
            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
            if (applicationsRoot == null)
            {
                return false;
            }

            using (var key = WinRegistry.CurrentUser.OpenSubKey(
                applicationsRoot + "\\" + AppKeyName))
            {
                var loader = key?.GetValue("LOADER")?.ToString();
                if (string.IsNullOrWhiteSpace(loader))
                {
                    return false;
                }

                dllPath = loader;
                return true;
            }
        }

        // 返回类型用 IList<string> 而不是 IReadOnlyList<string>：后者是 .NET 4.5 才有的 BCL 类型，
        // 而 AutoCAD 2012/2013/2014 跑在 .NET 4.0 上，程序集在只装了 4.0 的机器上会加载失败。
        public static IList<string> Install(string dllPath = null)
        {
            dllPath = string.IsNullOrWhiteSpace(dllPath)
                ? Path.GetFullPath(CurrentDllPath)
                : Path.GetFullPath(dllPath);
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException("当前插件 DLL 不存在，无法安装自动加载。", dllPath);
            }

            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: true)
                ?? throw new InvalidOperationException(
                    "未找到当前 CAD 的自动加载注册表位置。请先正常启动一次当前 CAD。");

            if (!WriteAutoloadKey(applicationsRoot, dllPath))
            {
                throw new InvalidOperationException("无法写入当前 CAD 的自动加载注册表项。");
            }

            // 清理本产品自己的历史键（当前没有历史键，判空跳过），避免改名后双加载。
            // 绝不触碰其它插件（含原版「GM批注」）的键。
            if (!string.IsNullOrEmpty(LegacyAppKeyName))
            {
                TryDeleteKey(applicationsRoot, LegacyAppKeyName);
            }

            return new[] { applicationsRoot };
        }

        public static int Uninstall()
        {
            var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
            if (applicationsRoot == null)
            {
                return 0;
            }

            var removed = 0;
            if (TryDeleteKey(applicationsRoot, AppKeyName))
            {
                removed++;
            }

            if (!string.IsNullOrEmpty(LegacyAppKeyName) && TryDeleteKey(applicationsRoot, LegacyAppKeyName))
            {
                removed++;
            }

            return removed;
        }

        private static bool TryDeleteKey(string applicationsRoot, string keyName)
        {
            using (var parent = WinRegistry.CurrentUser.OpenSubKey(
                applicationsRoot,
                writable: true))
            {
                if (parent == null)
                {
                    return false;
                }

                try
                {
                    if (!parent.GetSubKeyNames().Any(name =>
                        string.Equals(name, keyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                    return true;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Autoload.DeleteKey", ex);
                    return false;
                }
            }
        }

        private static bool WriteAutoloadKey(string applicationsRoot, string dllPath)
        {
            using (var key = WinRegistry.CurrentUser.CreateSubKey(
                applicationsRoot + "\\" + AppKeyName))
            {
                if (key == null)
                {
                    return false;
                }

                key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
                key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
                key.SetValue("LOADER", dllPath, RegistryValueKind.String);
                key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                return true;
            }
        }

        /// <summary>
        /// 当前正在运行的 CAD 的 Applications 路径（IFoxCAD GetAcAppKey 写法）。
        /// </summary>
        private static string GetCurrentCadApplicationsRoot(bool createIfMissing)
        {
            var productRoot = HostApplicationServices.Current?.UserRegistryProductRootKey;
            if (string.IsNullOrWhiteSpace(productRoot))
            {
                return null;
            }

            productRoot = NormalizeHkcuRelativePath(productRoot);
            if (string.IsNullOrWhiteSpace(productRoot))
            {
                return null;
            }

            var applicationsPath = productRoot + "\\Applications";
            if (createIfMissing)
            {
                using (var created = WinRegistry.CurrentUser.CreateSubKey(applicationsPath))
                {
                    return created != null ? applicationsPath : null;
                }
            }

            using (var existing = WinRegistry.CurrentUser.OpenSubKey(applicationsPath))
            {
                return existing != null ? applicationsPath : null;
            }
        }

        private static string NormalizeHkcuRelativePath(string path)
        {
            var normalized = path.Replace('/', '\\').Trim('\\');
            const string hkcuPrefix = @"HKEY_CURRENT_USER\";
            if (normalized.StartsWith(hkcuPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(hkcuPrefix.Length).Trim('\\');
            }

            return normalized;
        }
    }
}