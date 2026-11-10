using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif GSCAD
using Gssoft.Gscad.ApplicationServices;
using CadApplication = Gssoft.Gscad.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
#if ACAD_PRE2013
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif
#endif

namespace XAnnotation
{
    /// <summary>把「默认命令」改名成用户自己想要的名字（设置 → 命令）。
    ///
    /// <para><b>为什么是"别名"而不是改命令本身</b>：命令名是编译期用 <c>[CommandMethod]</c> 写死的，
    /// 运行时改不掉；而菜单、工具栏、Word 导出说明、已有用户的习惯全都引用默认名。
    /// 所以这里的做法是<b>额外注册一层别名</b>：默认命令始终可用，用户起的名字也能敲。</para>
    ///
    /// <para><b>实现方式：AutoLISP 的 <c>c:</c> 命令</b>。每个别名就是一条
    /// <c>(defun c:名字 () (command "X_PZ_XXX"))</c>，用 <c>SendStringToExecute</c> 静默送进文档
    /// （不激活窗口、不回显）。为什么不用 <c>Autodesk.AutoCAD.Internal.Utils.AddCommand</c>：
    /// 那是宿主内部 API，中望/浩辰没有；而 LISP 三个平台都原生支持，且**最坏情况只是别名没定义上**，
    /// 不会像改 CUI 那样把宿主启动搞坏（教训见 ToolbarInstaller.CleanupLegacyCui）。</para>
    ///
    /// <para><b>作用域是"每个文档"</b>：AutoLISP 的 <c>c:</c> 定义属于当前文档，所以每次
    /// <c>DocumentActivated</c> 都要重新定义一遍。已在 LISP 里留了 <c>*xzp-alias*</c> 记录上一批名字，
    /// 重新定义前先把它清掉 —— 这样"改了名/删了别名"后不会留下指向旧名字的僵尸命令，
    /// 插件中途重载（状态全丢）也能自愈。</para></summary>
    internal static class CommandAliases
    {
        /// <summary>别名最大长度（再长打字也累，且命令名过长在命令行里显示难看）。</summary>
        internal const int MaxLength = 24;

        /// <summary>LISP 里记录"本图已定义过哪些别名"的变量名，清僵尸命令要在定义前先读它。</summary>
        private const string LispBookkeepingVar = "*xzp-alias*";

        /// <summary>CAD 自带的命令与常用别名：起了同名就会把内置命令顶掉（例如别名填 L，
        /// 以后敲 L 就不画直线了），用户很难想到原因，所以在保存时直接拒掉。</summary>
        private static readonly string[] Reserved =
        {
            "L", "LINE", "C", "CIRCLE", "M", "MOVE", "CO", "CP", "COPY", "E", "ERASE", "TR", "TRIM",
            "EX", "EXTEND", "O", "OFFSET", "H", "BHATCH", "B", "BLOCK", "I", "INSERT", "X", "EXPLODE",
            "Z", "ZOOM", "P", "PAN", "D", "DIM", "A", "ARC", "R", "REDRAW", "S", "STRETCH", "F", "FILLET",
            "MI", "MIRROR", "AR", "ARRAY", "RO", "ROTATE", "SC", "SCALE", "MA", "MATCHPROP", "LA", "LAYER",
            "CH", "PROPERTIES", "MO", "DI", "DIST", "LI", "LIST", "UN", "UNDO", "U", "PL", "PLINE",
            "REC", "RECTANG", "PO", "POINT", "T", "MTEXT", "DT", "DTEXT", "ED", "DDEDIT", "PU", "PURGE",
            "REGEN", "SAVE", "OPEN", "NEW", "PLOT", "QSAVE", "WBLOCK", "PEDIT", "SPL", "SPLINE",
            "OSNAP", "ORTHO", "GRID", "SNAP", "PDMODE", "STYLE", "DIMSTYLE", "MATBALL"
        };

        /// <summary>上次算好的别名表 + settings.xml 的时间戳（避免每次切图都读盘）。</summary>
        private static string _cachedFingerprint = "";
        private static string _cachedLisp;
        private static string _cachedDescription = "";
        private static int _cachedCount;
        private static DateTime _cachedStamp = DateTime.MinValue;
        private static readonly object CacheLock = new object();

        /// <summary>每份文档最近一次生效的别名指纹（文档关闭自动回收，不会攒内存）。</summary>
        private static readonly ConditionalWeakTable<Document, string> Applied =
            new ConditionalWeakTable<Document, string>();

        /// <summary>校验并规范化用户填的命令名。
        /// <para><b>空值＝不要别名</b>（返回 true、<paramref name="name"/> 为空串），这是正常情况不是错误。</para></summary>
        /// <param name="raw">设置里填的原文。</param>
        /// <param name="name">规范化后的名字（大写、去空白）；无效时为原样上段的文本，便于回显。</param>
        /// <param name="error">无效原因（有效时为 null）。</param>
        internal static bool TryNormalize(string raw, out string name, out string error)
        {
            name = (raw ?? "").Trim();
            error = null;
            if (name.Length == 0) return true;                       // 留空 = 不改名

            if (name.Length > MaxLength)
            {
                error = "命令名最多 " + MaxLength + " 个字符。";
                return false;
            }

            // 只允许 ASCII：AutoLISP 的符号表对中文支持不稳（不同版本/不同宿主表现不一），
            // 中文命令名可能"看着定义成功、敲了没反应"，所以一律拒绝而不是留个坑。
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    error = "命令名只能用英文、数字、下划线（_）和短横线（-），不能用中文、空格或符号。";
                    return false;
                }
            }

            var first = name[0];
            if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
            {
                error = "命令名要以英文字母或下划线开头。";
                return false;
            }

            name = name.ToUpperInvariant();

            if (CommandCatalog.Find(name) != null)
            {
                error = name + " 是插件自带的命令名，本来就是可用的，不用再起一遍。";
                return false;
            }

            for (var i = 0; i < Reserved.Length; i++)
            {
                if (!string.Equals(Reserved[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                error = name + " 是 CAD 自带的命令/快捷键，用它会把内置命令顶掉，请换一个（例如加前缀：" + name + "_PZ）。";
                return false;
            }

            return true;
        }

        /// <summary>把设置里的别名表整理成「默认命令 → 别名」：丢掉无效项与重复别名（先到先得）。
        /// <para>设置窗口保存时也用这个做最后一道校验，保证落盘的一定是能用的。</para></summary>
        internal static Dictionary<string, string> Normalized(AnnotationSettings settings, out List<string> errors)
        {
            errors = new List<string>();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings == null || settings.CommandAliases == null) return map;

            var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in settings.CommandAliases)
            {
                var entry = CommandCatalog.Find(pair.Key);
                if (entry == null) continue;                        // 老配置里已下线的命令：忽略
                if (!TryNormalize(pair.Value, out var name, out var error))
                {
                    errors.Add(entry.Label + "：" + error);
                    continue;
                }
                if (name.Length == 0) continue;

                if (used.TryGetValue(name, out var owner))
                {
                    errors.Add(entry.Label + "：命令名 " + name + " 与「" + owner + "」重复，已忽略。");
                    continue;
                }
                used[name] = entry.Label;
                map[entry.Command] = name;
            }
            return map;
        }

        /// <summary>生成要送进文档的 LISP（单行：SendStringToExecute 里的换行＝回车，会把一句话拆成多次执行）。</summary>
        internal static string BuildLisp(AnnotationSettings settings, out int count, out string fingerprint)
        {
            var map = Normalized(settings, out _);
            count = map.Count;

            var names = new List<string>(map.Count);
            var body = new StringBuilder(512);
            body.Append("(progn");
            // 先清掉上一批别名：改名后旧名字不残留，插件重载后也能自愈。
            body.Append("(if (and (boundp '").Append(LispBookkeepingVar).Append(") (listp ").Append(LispBookkeepingVar).Append("))")
                .Append("(foreach x ").Append(LispBookkeepingVar).Append(" (set (read (strcat \"c:\" x)) nil)))");

            // 顺序固定（按命令清单），这样指纹是稳定的：同样的设置不会重复下发。
            foreach (var entry in CommandCatalog.All)
            {
                if (!map.TryGetValue(entry.Command, out var alias)) continue;
                names.Add(alias);
                // 需要保留预选的命令要一个局部变量，arglist 里声明成 local，免得往文档里留全局变量。
                body.Append("(defun c:").Append(alias).Append(entry.KeepPickSet ? " (/ xzp-ss) " : " () ");
                if (entry.KeepPickSet)
                {
                    // 原命令带 UsePickSet：经 LISP 转发时预选可能丢，调用前把"预选集"重新置为当前选择。
                    body.Append("(if (setq xzp-ss (ssget \"_I\")) (sssetfirst nil xzp-ss)) ");
                }
                body.Append("(command \"").Append(entry.Command).Append("\"))");
            }

            body.Append("(setq ").Append(LispBookkeepingVar).Append(" (list");
            for (var i = 0; i < names.Count; i++)
                body.Append(" \"").Append(names[i]).Append('"');
            body.Append(")))");

            fingerprint = string.Join("|", names.ToArray());
            return body.ToString();
        }

        /// <summary>把设置里的别名送进指定文档。同一份文档、同一套别名只送一次。</summary>
        internal static void Apply(Document doc)
        {
            try
            {
                if (doc == null) return;
                var lisp = GetLisp(out var count, out var fingerprint, out var description);

                if (Applied.TryGetValue(doc, out var last))
                {
                    if (string.Equals(last, fingerprint, StringComparison.Ordinal)) return;
                }
                else if (count == 0)
                {
                    // 从没定义过、现在也没有别名：什么都不用做（尤其不要往命令行送一个空操作）。
                    Applied.Add(doc, fingerprint);
                    return;
                }

                doc.SendStringToExecute(lisp, false, false, false);
                Applied.Remove(doc);
                Applied.Add(doc, fingerprint);

                if (count > 0)
                    PluginLog.Info("Alias", "已注册 " + count + " 个自定义命令：" + description);
                else
                    PluginLog.Info("Alias", "自定义命令已全部取消，旧的别名已清除。");
            }
            catch (Exception ex) { PluginLog.Warning("Alias.Apply", ex.GetType().Name + " - " + ex.Message); }
        }

        /// <summary>对当前文档立刻生效（保存设置后用，不必关图重开）。</summary>
        internal static void ApplyActive()
        {
            try { Apply(CadApplication.DocumentManager.MdiActiveDocument); }
            catch (Exception ex) { PluginLog.Warning("Alias.ApplyActive", ex.Message); }
        }

        /// <summary>日志用："PZ=X_PZ_DRAW、PZLIST=X_PZ_LIST"（按命令清单顺序排）。</summary>
        private static string DescribeAliases(AnnotationSettings settings)
        {
            var map = Normalized(settings, out _);
            var parts = new List<string>(map.Count);
            foreach (var entry in CommandCatalog.All)
                if (map.TryGetValue(entry.Command, out var alias)) parts.Add(alias + "=" + entry.Command);
            return string.Join("、", parts.ToArray());
        }

        /// <summary>settings.xml 没改就复用上次算好的 LISP：切图、开图都会调到这里，
        /// 没必要每次都解析一遍 XML 再拼字符串。</summary>
        private static string GetLisp(out int count, out string fingerprint, out string description)
        {
            lock (CacheLock)
            {
                var stamp = SettingsStamp();
                if (_cachedLisp == null || stamp != _cachedStamp)
                {
                    var settings = SettingsStore.Load();
                    _cachedLisp = BuildLisp(settings, out _cachedCount, out _cachedFingerprint);
                    _cachedDescription = DescribeAliases(settings);
                    _cachedStamp = stamp;
                }
                count = _cachedCount;
                fingerprint = _cachedFingerprint;
                description = _cachedDescription;
                return _cachedLisp;
            }
        }

        private static DateTime SettingsStamp()
        {
            try
            {
                var file = Path.Combine(AppPaths.DataFolder, "settings.xml");
                return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }
    }
}
