using System;
using System.IO;

namespace XAnnotation
{
    /// <summary>
    /// 应用数据目录统一入口：%AppData%\XAnnotation（设置、历史库、日志、图号记忆都放这里）。
    /// 首次访问时自动从上一代「GM批注」的目录 %AppData%\GMAnnotation 迁移已有文件，
    /// 换品牌升级不丢数据（只复制、不删除、不覆盖原目录里的任何东西）。
    /// </summary>
    internal static class AppPaths
    {
        private const string FolderName = "XAnnotation";

        /// <summary>上一代品牌（GM批注）的数据目录名。**只读复制**，绝不改动原目录。</summary>
        private const string LegacyFolderName = "GMAnnotation";

        /// <summary>需要从旧目录继承的文件（存在才复制，且不覆盖新目录已有文件）。</summary>
        private static readonly string[] InheritFiles = { "settings.xml", "history.json", "drawingnos.json" };

        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        private static bool _ready;

        /// <summary>数据目录完整路径（访问时自动完成迁移与目录创建）。</summary>
        public static string DataFolder
        {
            get { EnsureReady(); return Folder; }
        }

        /// <summary>确保数据目录可用，并从旧版本目录一次性迁移历史文件。任何失败都静默忽略。</summary>
        public static void EnsureReady()
        {
            if (_ready) return;
            _ready = true;
            try
            {
                Directory.CreateDirectory(Folder);
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);
                if (!Directory.Exists(legacy)) return;
                foreach (var name in InheritFiles)
                {
                    var source = Path.Combine(legacy, name);
                    var target = Path.Combine(Folder, name);
                    if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target, false);
                }
            }
            catch
            {
                // 迁移属增强能力，失败不应影响插件运行。
            }
        }
    }
}
