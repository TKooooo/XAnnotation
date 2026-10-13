#if GSCAD
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XAnnotation
{
    /// <summary>浩辰 GstarCAD 的菜单安装 —— <b>直接往主 CUI 文件（gcad.cuix）里注入「X-批注」菜单</b>。
    ///
    /// <para><b>为什么不用宿主的菜单 API</b>：浩辰的 <c>Application.AcadApplication.MenuGroups</c>
    /// 在 .NET 8 宿主下读 <c>Count</c> 必踩 <c>AccessViolationException</c>（访问冲突＝进程级内存损坏，
    /// 之后插件的每个宿主调用都会变得随机）。2026-10-13 的实机日志把这一点钉死了 —— 每秒重试一次、
    /// 重试了 40 次全部失败，而同一时刻 <c>MenuBar</c> 读 <c>Count</c> 却是好的（=16）。
    /// 所以只要碰到 <c>MenuGroups</c>，菜单这条路就走不通。</para>
    ///
    /// <para><b>为什么改文件是"官方路线"</b>：浩辰自己也是这么装菜单的 —— 主 CUI
    /// <c>&lt;APPDATA%&gt;\Gstarsoft\GstarCAD\&lt;版本&gt;\&lt;语言&gt;\Support\gcad.cuix</c> 的
    /// <c>Header.cui</c> 里有一串 <c>&lt;PartialMenuFile&gt;</c>（BIM.cuix / express.cuix / …），
    /// 而每个 partial 的菜单在 <c>WorkspaceRoot.cui</c> 里都有一条
    /// <c>&lt;WSPop MenuGroup="EXPRESS" pUID="ID_MNEXPRESS"/&gt;</c> —— 也就是说
    /// <b>partial 的弹出菜单必须被某个工作空间的 &lt;WSPop&gt; 引用，才会出现在菜单栏上</b>。</para>
    ///
    /// <para><b>本类的做法</b>（只动主 CUI，不额外生成 partial 文件，避免 OPC 包装出错）：
    /// 在 <c>MenuGroup.cui</c> 里加一个 <c>&lt;MacroGroup Name="XPZ"&gt;</c>（菜单宏），
    /// 在 <c>PopMenuRoot.cui</c> 里加一个 <c>&lt;PopMenu UID="XPZ_PM"&gt;</c>（弹出菜单，用 MacroRef 指回宏），
    /// 在 <c>WorkspaceRoot.cui</c> 的每个 <c>&lt;/WSPopRoot&gt;</c> 前加一条
    /// <c>&lt;WSPop MenuGroup="GCAD" pUID="XPZ_PM"/&gt;</c>（三个工作空间都挂上）。
    /// 全部 UID 用 <c>XPZ_</c> 前缀，与浩辰自带的不冲突；幂等（已注入就跳过）；写前备份。</para>
    ///
    /// <para><b>生效时机</b>：改完主 CUI 后<b>需要重启浩辰 CAD</b>（或执行 <c>MENU</c> 命令重载主菜单）
    /// 才会显示。这一点没法绕过 —— CUI 只在启动/重载时读取。</para>
    /// </summary>
    internal static class GstarCadMenu
    {
        /// <summary>菜单栏上显示的菜单名（<c>&amp;</c> 后面是 Alt 快捷键）。</summary>
        private const string PopMenuTitle = "X-批注(&amp;X)";

        /// <summary>注入用的固定 UID 前缀：只要看到它就知道"已经装过了"。</summary>
        private const string PopUid = "XPZ_PM";
        private const string MacroGroupTag = "<MacroGroup Name=\"XPZ\">";

        /// <summary>菜单项：<c>显示名|命令</c>；<c>-|</c> 表示分隔线。</summary>
        private static readonly string[] Entries =
        {
            "批注绘制|X_PZ_NOTE",
            "快速直绘|X_PZ_DRAW",
            "编辑批注|X_PZ_EDIT",
            "移动批注|X_PZ_MOVE",
            "删除批注|X_PZ_DELETE",
            "隐藏批注|X_PZ_HIDE",
            "显示批注|X_PZ_SHOW",
            "合并批注|X_PZ_MERGE",
            "过滤批注|X_PZ_FILTER",
            "格式刷|X_PZ_FORMAT",
            "刷新批注文字|X_PZ_REFRESH",
            "单绘云线|X_PZ_CLOUD",
            "增补云线|X_PZ_ADDCLOUD",
            "批注列表|X_PZ_LIST",
            "批注汇总|X_PZ_SUMMARY",
            "批注清单|X_PZ_LEGEND",
            "批注历史|X_PZ_HISTORY",
            "知识库|X_PZ_KB",
            "导出Word|X_PZ_WORD",
            "导出批注|X_PZ_EXPORT",
            "导入批注|X_PZ_IMPORT",
            "修复批注|X_PZ_REPAIR",
            "工具栏|X_PZ_TOOLBAR",
            "-|",
            "批注设置|X_PZ_SETTINGS",
            "安装自动加载|X_PZ_INSTALL_AUTOLOAD",
            "卸载自动加载|X_PZ_UNINSTALL_AUTOLOAD",
            "重新加载菜单|X_PZ_MENU",
            "-|",
            "关于|X_PZ_ABOUT",
        };

        /// <summary>把「X-批注」菜单装进浩辰主 CUI。返回 false 时 <paramref name="message"/> 说明卡在哪一步。</summary>
        public static bool Ensure(out string message)
        {
            message = null;
            try
            {
                var mainCui = FindMainCui();
                if (mainCui == null)
                {
                    message = "未找到浩辰主菜单文件 gcad.cuix，跳过菜单注入。";
                    return false;
                }

                if (AlreadyInjected(mainCui))
                {
                    message = "X-批注菜单已在浩辰主菜单中（重启浩辰CAD后显示）。";
                    return true;
                }

                // 备份：主 CUI 是用户界面配置，改坏了必须能一键还原。
                // 只留一份最早的原件（.xpz-bak 已存在就不再覆盖），这样反复加载不会把"已改过的版本"当成原件备份。
                var backup = mainCui + ".xpz-bak";
                if (!File.Exists(backup))
                {
                    File.Copy(mainCui, backup);
                    PluginLog.Info("Menu.Cui", "已备份浩辰主菜单：" + backup);
                }

                InjectInto(mainCui);

                message = "已把 X-批注菜单写入浩辰主菜单文件，重启浩辰CAD后显示（也可执行 MENU 命令重载）。";
                PluginLog.Info("Menu.Cui", message + " 文件=" + mainCui);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu.Cui", ex);
                message = "浩辰菜单注入失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>把菜单从浩辰主 CUI 里摘掉（卸载用）。找不到或没装过都算成功。</summary>
        public static bool Remove(out string message)
        {
            message = null;
            try
            {
                var mainCui = FindMainCui();
                if (mainCui == null)
                {
                    message = "未找到浩辰主菜单文件。";
                    return false;
                }

                if (!AlreadyInjected(mainCui))
                {
                    message = "浩辰主菜单里没有 X-批注。";
                    return true;
                }

                RewriteCui(mainCui, (name, text) => RemoveFromEntry(name, text));
                message = "已从浩辰主菜单移除 X-批注（重启浩辰CAD后生效）。";
                PluginLog.Info("Menu.Cui", message);
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu.Cui.Remove", ex);
                message = "移除浩辰菜单失败: " + ex.Message;
                return false;
            }
        }

        // ══════════════════════ 定位 ══════════════════════

        /// <summary>主 CUI 的路径：<c>%APPDATA%\Gstarsoft\GstarCAD\&lt;版本&gt;\&lt;语言&gt;\Support\gcad.cuix</c>。
        /// <para>版本号（R26）与语言目录都不写死 —— 从 <c>%APPDATA%\Gstarsoft\GstarCAD</c> 往下找同名文件，
        /// 有多个时取最近修改的那个（正在用的那份）。</para></summary>
        private static string FindMainCui()
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Gstarsoft", "GstarCAD");
                if (!Directory.Exists(root)) return null;

                string best = null;
                var bestTime = DateTime.MinValue;
                foreach (var file in Directory.GetFiles(root, "gcad.cuix", SearchOption.AllDirectories))
                {
                    var time = File.GetLastWriteTimeUtc(file);
                    if (time > bestTime) { bestTime = time; best = file; }
                }
                return best;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Menu.Cui.Find", ex.Message);
                return null;
            }
        }

        /// <summary>主 CUI 里是否已经有我们的菜单（看 MenuGroup.cui 有没有那个 MacroGroup）。</summary>
        private static bool AlreadyInjected(string mainCui)
        {
            using (var zip = ZipFile.OpenRead(mainCui))
            {
                var text = ReadEntry(zip, "MenuGroup.cui");
                return text != null && text.IndexOf(MacroGroupTag, StringComparison.Ordinal) >= 0;
            }
        }

        // ══════════════════════ 注入 / 移除 ══════════════════════

        private static void InjectInto(string mainCui)
        {
            RewriteCui(mainCui, (name, text) => InjectIntoEntry(name, text));
        }

        /// <summary>按 entry 名决定怎么改；返回 null 表示"这一项原样保留"。</summary>
        private static string InjectIntoEntry(string entryName, string text)
        {
            if (text == null) return null;

            if (string.Equals(entryName, "MenuGroup.cui", StringComparison.OrdinalIgnoreCase))
            {
                if (text.IndexOf(MacroGroupTag, StringComparison.Ordinal) >= 0) return null;   // 已注入
                return InsertBeforeLast(text, "</MenuGroup>", BuildMacroGroupXml());
            }

            if (string.Equals(entryName, "PopMenuRoot.cui", StringComparison.OrdinalIgnoreCase))
            {
                if (text.IndexOf("UID=\"" + PopUid + "\"", StringComparison.Ordinal) >= 0) return null;
                return InsertBeforeLast(text, "</PopMenuRoot>", BuildPopMenuXml());
            }

            if (string.Equals(entryName, "WorkspaceRoot.cui", StringComparison.OrdinalIgnoreCase))
            {
                if (text.IndexOf("pUID=\"" + PopUid + "\"", StringComparison.Ordinal) >= 0) return null;
                return InsertBeforeEach(text, "</WSPopRoot>", BuildWSPopXml);
            }

            return null;
        }

        private static string RemoveFromEntry(string entryName, string text)
        {
            if (text == null) return null;
            if (string.Equals(entryName, "MenuGroup.cui", StringComparison.OrdinalIgnoreCase))
            {
                return CutBlock(text, MacroGroupTag, "</MacroGroup>");
            }
            if (string.Equals(entryName, "PopMenuRoot.cui", StringComparison.OrdinalIgnoreCase))
            {
                return CutBlock(text, "<PopMenu hasDiesel=\"false\" UID=\"" + PopUid + "\">", "</PopMenu>");
            }
            if (string.Equals(entryName, "WorkspaceRoot.cui", StringComparison.OrdinalIgnoreCase))
            {
                return CutAll(text, "<WSPop MenuGroup=\"GCAD\" pUID=\"" + PopUid + "\"", "</WSPop>");
            }
            return null;
        }

        // ══════════════════════ XML 片段 ══════════════════════

        /// <summary>菜单宏（放在 MenuGroup.cui）。Command 用 <c>^C^C_命令名 </c>：
        /// <c>^C^C</c> 是"连按两次 Ctrl+C"（取消当前命令再执行），必须写成**字面文本** ——
        /// CUI 的 XML 里不能用 0x03 控制字符，那会让整个 CUI 解析失败。</summary>
        private static string BuildMacroGroupXml()
        {
            var sb = new StringBuilder();
            sb.Append("    ").Append(MacroGroupTag).Append("\r\n");
            for (var i = 0; i < Entries.Length; i++)
            {
                var parts = Entries[i].Split('|');
                if (parts.Length < 2 || parts[1].Length == 0) continue;   // 分隔线没有宏

                var n = IndexOfCommand(i);
                sb.Append("        <MenuMacro UID=\"XPZ_MM").Append(D2(n)).Append("\">\r\n");
                sb.Append("            <Macro type=\"Any\">\r\n");
                sb.Append("                <Revision MajorVersion=\"0\" MinorVersion=\"0\" UserVersion=\"0\" />\r\n");
                sb.Append("                <ModifiedRev MajorVersion=\"0\" MinorVersion=\"0\" UserVersion=\"0\" />\r\n");
                sb.Append("                <Name xlate=\"true\" UID=\"XPZ_NM").Append(D2(n)).Append("\">")
                  .Append(XmlEscape(parts[0])).Append("</Name>\r\n");
                sb.Append("                <Command>^C^C_").Append(parts[1]).Append(" </Command>\r\n");
                sb.Append("                <HelpString xlate=\"true\" UID=\"XPZ_HS").Append(D2(n)).Append("\" />\r\n");
                sb.Append("            </Macro>\r\n");
                sb.Append("        </MenuMacro>\r\n");
            }
            sb.Append("    </MacroGroup>\r\n");
            return sb.ToString();
        }

        /// <summary>弹出菜单（放在 PopMenuRoot.cui）。每个菜单项通过 MacroRef 指回上面那个 MacroGroup 里的宏。</summary>
        private static string BuildPopMenuXml()
        {
            var sb = new StringBuilder();
            sb.Append("    <PopMenu hasDiesel=\"false\" UID=\"").Append(PopUid).Append("\">\r\n");
            sb.Append("        <ModifiedRev MajorVersion=\"19\" MinorVersion=\"0\" UserVersion=\"1\" />\r\n");
            sb.Append("        <Name xlate=\"true\" UID=\"XPZ_PMN\">").Append(PopMenuTitle).Append("</Name>\r\n");

            var sep = 0;
            for (var i = 0; i < Entries.Length; i++)
            {
                var parts = Entries[i].Split('|');
                if (parts.Length < 2 || parts[1].Length == 0)
                {
                    // 分隔线：UID 用 XPZ_PISxx
                    sep++;
                    sb.Append("        <PopMenuItem IsSeparator=\"true\" hasDiesel=\"false\" UID=\"XPZ_PIS")
                      .Append(D2(sep)).Append("\">\r\n");
                    sb.Append("            <ModifiedRev MajorVersion=\"16\" MinorVersion=\"2\" UserVersion=\"0\" />\r\n");
                    sb.Append("        </PopMenuItem>\r\n");
                    continue;
                }

                var n = IndexOfCommand(i);
                sb.Append("        <PopMenuItem IsSeparator=\"false\" hasDiesel=\"false\" UID=\"XPZ_PI")
                  .Append(D2(n)).Append("\">\r\n");
                sb.Append("            <ModifiedRev MajorVersion=\"16\" MinorVersion=\"2\" UserVersion=\"0\" />\r\n");
                sb.Append("            <NameRef UID=\"XPZ_RF").Append(D2(n)).Append("\" xlate=\"true\">")
                  .Append(XmlEscape(parts[0])).Append("</NameRef>\r\n");
                sb.Append("            <MenuItem>\r\n");
                sb.Append("                <MacroRef MenuMacroID=\"XPZ_MM").Append(D2(n)).Append("\" />\r\n");
                sb.Append("            </MenuItem>\r\n");
                sb.Append("        </PopMenuItem>\r\n");
            }

            sb.Append("    </PopMenu>\r\n");
            return sb.ToString();
        }

        /// <summary>工作空间里的菜单栏引用（放在 WorkspaceRoot.cui 的每个 &lt;WSPopRoot&gt; 末尾）。
        /// 三个工作空间各给一个不同的 UID —— 主 CUI 里同名菜单在不同工作空间的 WSPop UID 本来就不一样。</summary>
        private static string BuildWSPopXml(int ordinal)
        {
            var sb = new StringBuilder();
            sb.Append("        <WSPop MenuGroup=\"GCAD\" pUID=\"").Append(PopUid)
              .Append("\" Display=\"1\" UID=\"XPZ_WS").Append(D2(ordinal)).Append("\">\r\n");
            sb.Append("            <ModifiedRev MajorVersion=\"17\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
            sb.Append("        </WSPop>\r\n");
            return sb.ToString();
        }

        /// <summary>Entries 里第 <paramref name="index"/> 项是第几条"真命令"（从 1 开始，跳过分隔线）。</summary>
        private static int IndexOfCommand(int index)
        {
            var n = 0;
            for (var i = 0; i <= index && i < Entries.Length; i++)
            {
                var parts = Entries[i].Split('|');
                if (parts.Length >= 2 && parts[1].Length > 0) n++;
            }
            return n;
        }

        private static string D2(int value)
        {
            return value < 10 ? "0" + value : value.ToString();
        }

        private static string XmlEscape(string text)
        {
            return (text ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        // ══════════════════════ OPC(zip) 读写 ══════════════════════

        /// <summary>逐 entry 重写主 CUI。<paramref name="transform"/> 返回 null 表示该 entry 原样复制。
        /// <para>先写临时文件再替换：<c>ZipArchive</c> 打开的那个文件本身不能同时被重写，
        /// 而且就地写坏主 CUI 是灾难性的（用户整个界面配置）。</para></summary>
        private static void RewriteCui(string cuiPath, Func<string, string, string> transform)
        {
            var temp = cuiPath + ".xpz-new";
            try
            {
                using (var source = ZipFile.OpenRead(cuiPath))
                using (var target = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    foreach (var entry in source.Entries)
                    {
                        var newEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        if (entry.Length == 0)
                        {
                            // 目录项（例如 "_rels/"）
                            continue;
                        }

                        byte[] payload;
                        using (var input = entry.Open())
                        using (var buffer = new MemoryStream())
                        {
                            input.CopyTo(buffer);
                            payload = buffer.ToArray();
                        }

                        var bom = HasUtf8Bom(payload);
                        var text = DecodeUtf8(payload, bom);
                        var updated = transform(entry.FullName, text);
                        if (updated != null)
                        {
                            payload = EncodeUtf8(updated, bom);
                        }

                        using (var output = newEntry.Open())
                        {
                            output.Write(payload, 0, payload.Length);
                        }
                    }
                }

                // File.Replace 保留原文件的 ACL/时间戳语义，并自动留一份 .xpz-old
                if (File.Exists(cuiPath))
                {
                    File.Replace(temp, cuiPath, null);
                }
                else
                {
                    File.Move(temp, cuiPath);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string ReadEntry(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using (var stream = entry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                var payload = buffer.ToArray();
                return DecodeUtf8(payload, HasUtf8Bom(payload));
            }
        }

        private static bool HasUtf8Bom(byte[] data)
        {
            return data != null && data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        }

        private static string DecodeUtf8(byte[] data, bool bom)
        {
            if (data == null) return null;
            return Encoding.UTF8.GetString(data, bom ? 3 : 0, bom ? data.Length - 3 : data.Length);
        }

        private static byte[] EncodeUtf8(string text, bool bom)
        {
            var body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (!bom) return body;
            var payload = new byte[body.Length + 3];
            payload[0] = 0xEF; payload[1] = 0xBB; payload[2] = 0xBF;
            Buffer.BlockCopy(body, 0, payload, 3, body.Length);
            return payload;
        }

        // ══════════════════════ 文本插入 / 裁剪 ══════════════════════

        /// <summary>在最后一个 <paramref name="anchor"/> 之前插入。</summary>
        private static string InsertBeforeLast(string text, string anchor, string fragment)
        {
            var index = text.LastIndexOf(anchor, StringComparison.Ordinal);
            if (index < 0) throw new InvalidDataException("CUI 里找不到锚点 " + anchor);
            return text.Substring(0, index) + fragment + text.Substring(index);
        }

        /// <summary>在每个 <paramref name="anchor"/> 之前插入（<paramref name="fragmentFactory"/> 按序号生成）。
        /// 从后往前处理，避免前面的插入打乱后面的下标。</summary>
        private static string InsertBeforeEach(string text, string anchor, Func<int, string> fragmentFactory)
        {
            var positions = new List<int>();
            var cursor = 0;
            while (true)
            {
                var index = text.IndexOf(anchor, cursor, StringComparison.Ordinal);
                if (index < 0) break;
                positions.Add(index);
                cursor = index + anchor.Length;
            }
            if (positions.Count == 0) throw new InvalidDataException("CUI 里找不到锚点 " + anchor);

            var sb = new StringBuilder(text);
            for (var i = positions.Count - 1; i >= 0; i--)
            {
                sb.Insert(positions[i], fragmentFactory(i + 1));
            }
            return sb.ToString();
        }

        /// <summary>删掉从 <paramref name="start"/> 起到其后第一个 <paramref name="end"/> 为止的一整块（含一个完整标签）。</summary>
        private static string CutBlock(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return text;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            if (to < 0) return text;
            to += end.Length;
            // 连带后面的换行一起删掉，别留下空行
            while (to < text.Length && (text[to] == '\r' || text[to] == '\n')) to++;
            return text.Substring(0, from) + text.Substring(to);
        }

        /// <summary>删掉所有 <paramref name="start"/>…<paramref name="end"/> 块。</summary>
        private static string CutAll(string text, string start, string end)
        {
            var result = text;
            while (true)
            {
                var next = CutBlock(result, start, end);
                if (string.Equals(next, result, StringComparison.Ordinal)) return result;
                result = next;
            }
        }
    }
}
#endif
