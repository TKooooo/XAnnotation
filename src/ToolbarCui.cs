#if AUTOCAD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace XAnnotation
{
    /// <summary>把「X-批注」工具栏做成 <b>CAD 正式的 CUI 工具栏</b>（partial CUIx），而不是用 COM 现场新建。
    ///
    /// <para><b>为什么必须这么做</b>（2026-11-10 用户两次反馈"拖到侧边、重启又跑回最上面"之后定案）：
    /// 用 <c>MenuGroups("Acad").Toolbars.Add()</c> 建出来的工具栏，宿主<b>不会</b>把它写进工作空间，
    /// 每个 CAD 会话都是"新建一个"，而 <c>Dock(n)</c> 的语义是"停到那条边、插到该停靠区<b>最前面</b>"，
    /// 于是用户拖到左列中部的位置每次启动都会跑到最上面。ActiveX 里既没有"停靠顺序"接口，
    /// 也没有"列内偏移"接口 —— 实测写 <c>Top</c> 读回来永远是 0（日志 <c>Top 偏移还原：写入 1，读回 0</c>），
    /// <c>Dock()</c> 对已在同一侧的工具栏还会直接报"工具栏已固定到同一边"。</para>
    ///
    /// <para><b>CUI 工具栏为什么就能记住</b>：它的位置由 AutoCAD 自己维护在主 CUI 的
    /// <c>WorkspaceRoot.cui</c> 里，每条记录形如
    /// <c>&lt;WSToolbar MenuGroup="XANNOTATION" pUID="TB_XANNOT" ToolbarOrient="left"
    /// dockrow="1" dockcolumn="1" /&gt;</c> —— <c>dockrow</c>/<c>dockcolumn</c> 就是停靠区内的行列位次。
    /// 这件事已经在实机上确认：<c>MenuGroups.Load(cuix)</c> 之后主 CUI 会<b>立刻</b>多出
    /// <c>Header.cui</c> 的 <c>&lt;PartialMenuFile&gt;</c> 与四个工作空间的 <c>WSToolbar</c> 记录，
    /// 以后每次启动由 AutoCAD 自动加载、自动摆回原位（天正那条 <c>tangent.cuix</c> 走的就是这套）。
    /// 所以插件<b>不再需要</b>自己轮询记位置（<see cref="ToolbarPlacement"/> 退化成"首次生成时的初值"）。</para>
    ///
    /// <para><b>容错</b>：生成/加载任何一步失败都返回 <c>false</c>，由 <see cref="ToolbarInstaller"/>
    /// 退回原来的 COM 建法（位置记不住，但按钮还在）。宁可有工具栏但位置记不住，也不能让工具栏消失。</para></summary>
    internal static class ToolbarCui
    {
        /// <summary>CUI 里的菜单组名。AutoCAD 用它和主 CUI 里的 <c>WSToolbar</c> 记录对号。</summary>
        internal const string MenuGroupName = "XANNOTATION";

        /// <summary>工具栏的 pUID。<b>跨版本必须稳定</b>：AutoCAD 的工作空间就是按它记位置的。</summary>
        internal const string ToolbarUid = "TB_XANNOT";

        /// <summary>工具栏别名（CUI 里 &lt;Alias&gt;，命令行/脚本引用用）。</summary>
        private const string ToolbarAlias = "XPZ_TOOLBAR";

        /// <summary>partial CUI 文件名（放在 CAD 的 Support 目录，和 tangent.cuix 那些同级）。</summary>
        private const string CuixFileName = "XAnnotation.cuix";

        /// <summary>生成标记文件：内容 = 生成版本 + 按钮数。与当前不一致就重新生成 cuix
        /// （改按钮/改图标/升版本后，重启一次即可生效；文件没变就不动，免得每次启动都重写）。</summary>
        private const string BuildStampFileName = "XAnnotation.cuix.build";

        // ================= 对外入口 =================

        /// <summary>确保「X-批注」CUI 工具栏已生成并加载到当前会话。
        /// <para>成功返回 <c>true</c>，此时<b>不应</b>再用 COM 建同名工具栏（会出现两条）。</para></summary>
        internal static bool Ensure(out string message)
        {
            try
            {
                var menuGroups = GetMenuGroups();
                if (menuGroups == null)
                {
                    message = "当前 CAD 未公开 MenuGroups 接口，CUI 工具栏不可用。";
                    return false;
                }

                // 已经加载过就直接成功：重复 Load 会抛"菜单组已存在"，也没必要每次刷新都重建。
                if (FindGroup(menuGroups) != null)
                {
                    message = "CUI 工具栏（" + ToolbarUid + "）已加载。";
                    return true;
                }

                var supportDir = ResolveSupportFolder();
                if (string.IsNullOrEmpty(supportDir))
                {
                    message = "取不到 CAD 的 Support 目录，CUI 工具栏跳过。";
                    return false;
                }

                var path = Path.Combine(supportDir, CuixFileName);
                var stamp = Path.Combine(supportDir, BuildStampFileName);
                if (!File.Exists(path) || !StampMatches(stamp))
                {
                    Build(path, stamp);
                    PluginLog.Info("Toolbar.Cui", "已生成 " + path + "（" + ToolbarInstaller.ButtonCount + " 个按钮）。");
                }

                // 用**裸文件名**交给宿主（文件就在 Support 目录，属支持路径）：AutoCAD 会把它以
                // 相对名登记进主 CUI 的 <PartialMenuFile>，之后每次启动自己按支持路径找。
                // 传完整路径也能加载，但登记下来的是绝对路径 —— 换版本（Support 目录变了）、
                // 或用户名含中文时都会变成死链。实测两种写法都能加载，相对名更稳。
                try
                {
                    menuGroups.InvokeMethod("Load", CuixFileName);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning("Toolbar.Cui.LoadName", "按文件名加载失败，改用完整路径：" + Describe(ex));
                    menuGroups.InvokeMethod("Load", path);
                }

                if (FindGroup(menuGroups) == null)
                {
                    message = "CUI 已交给宿主加载，但菜单组没出现（" + path + "）。";
                    return false;
                }
                message = "CUI 工具栏已加载：" + path;
                return true;
            }
            catch (Exception ex)
            {
                message = "CUI 工具栏加载失败：" + Describe(ex);
                return false;
            }
        }

        // ================= 取宿主对象 =================

        private static object GetMenuGroups()
        {
            return ToolbarInstaller.GetMenuGroupsStatic();
        }

        /// <summary>按名找菜单组；没加载过时返回 <c>null</c>（COM 报错也当作"没有"）。</summary>
        private static object FindGroup(object menuGroups)
        {
            try { return menuGroups.InvokeMethod("Item", MenuGroupName); }
            catch (TargetInvocationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>CAD 的 Support 目录：从「主菜单文件」（Preferences → 文件 → 主自定义文件）取，
        /// 各版本 / 各语言都不同，硬编码路径一定会错。取不到再退回读注册表那条老路。</summary>
        private static string ResolveSupportFolder()
        {
            try
            {
                var app = ToolbarInstaller.GetAcadApplicationObject();
                if (app == null) return null;
                var files = app.GetType().InvokeMember("Preferences", BindingFlags.GetProperty, null, app, null);
                files = files.GetType().InvokeMember("Files", BindingFlags.GetProperty, null, files, null);
                var menuFile = Convert.ToString(files.GetType().InvokeMember(
                    "MenuFile", BindingFlags.GetProperty, null, files, null));
                if (!string.IsNullOrWhiteSpace(menuFile))
                {
                    var folder = Path.GetDirectoryName(menuFile.Trim());
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Cui.SupportDir", Describe(ex));
            }
            return null;
        }

        // ================= 生成 cuix =================

        private static bool StampMatches(string stampPath)
        {
            try
            {
                if (!File.Exists(stampPath)) return false;
                return File.ReadAllText(stampPath).Trim() == CurrentStamp();
            }
            catch { return false; }
        }

        private static string CurrentStamp()
        {
            var version = "?";
            try { version = typeof(ToolbarCui).Assembly.GetName().Version.ToString(); }
            catch { }
            return version + "|" + ToolbarInstaller.ButtonCount + "|cui1";
        }

        /// <summary>宿主版本（用于 CUI 里的 <c>ModifiedRev MajorVersion/MinorVersion</c>）。
        /// 不匹配不会导致加载失败（实测 19.1 写的包在 2022 上照样能读），但按宿主版本写更规矩。</summary>
        private static void ResolveHostVersion(out int major, out int minor)
        {
            major = 19;
            minor = 1;
            try
            {
                var text = ToolbarInstaller.HostVersion ?? "";
                var digits = new StringBuilder();
                foreach (var ch in text)
                {
                    if (char.IsDigit(ch) || ch == '.') digits.Append(ch);
                    else if (digits.Length > 0) break;
                }
                var parts = digits.ToString().Split('.');
                if (parts.Length > 0 && int.TryParse(parts[0], out var m)) major = m;
                if (parts.Length > 1 && int.TryParse(parts[1], out var n)) minor = n;
            }
            catch { }
        }

        /// <summary>生成整个 partial CUI 包（OPC：zip + [Content_Types].xml + _rels/.rels + 各 .cui 部件）。
        /// 先写临时文件再替换，避免 CAD 恰好在这种时刻退出留下半个包。</summary>
        private static void Build(string path, string stampPath)
        {
            var buttons = ToolbarInstaller.GetButtons();
            ResolveHostVersion(out var major, out var minor);

            // 每个按钮的图标：复用工具栏那套"白底深蓝单字"BMP（16/32 各一张）。
            var icons = new List<Tuple<string, string>>();   // 包内名 → 本地路径
            var iconNames = new string[buttons.Length][];
            for (var i = 0; i < buttons.Length; i++)
            {
                var code = string.IsNullOrEmpty(buttons[i].Glyph) ? "0000"
                    : char.ConvertToUtf32(buttons[i].Glyph, 0).ToString("X4", CultureInfo.InvariantCulture);
                iconNames[i] = new[] { "xpz_" + code + "_16.bmp", "xpz_" + code + "_32.bmp" };
                for (var k = 0; k < 2; k++)
                {
                    var size = k == 0 ? 16 : 32;
                    var local = ToolbarInstaller.TryCreateIcon(buttons[i].Glyph, size);
                    if (!string.IsNullOrEmpty(local) && File.Exists(local))
                        icons.Add(Tuple.Create(iconNames[i][k], local));
                }
            }

            // 首次生成时的停靠方向：沿用用户以前拖出来的摆放（toolbar.xml），没有就底部横排。
            var orient = InitialOrient();

            var parts = new List<Tuple<string, byte[]>>();
            parts.Add(Tuple.Create("Header.cui", Utf8(BuildHeaderCui(major, minor))));
            parts.Add(Tuple.Create("MenuGroup.cui", Utf8(BuildMenuGroupCui(major, minor, buttons))));
            parts.Add(Tuple.Create("ToolbarRoot.cui", Utf8(BuildToolbarRootCui(major, minor, buttons, orient))));
            foreach (var icon in icons)
                parts.Add(Tuple.Create(icon.Item1, File.ReadAllBytes(icon.Item2)));

            // Menu_Package_Info 与 _rels 必须列出<b>包里实际存在的每一个部件</b>，漏一个宿主就可能拒绝加载。
            parts.Add(Tuple.Create("Menu_Package_Info.xml", Utf8(BuildPackageInfo(parts))));
            var rels = Utf8(BuildRels(parts));

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var zip = new StoreZipWriter(stream);
                zip.Add("[Content_Types].xml", Utf8(ContentTypes));
                zip.Add("_rels/.rels", rels);
                foreach (var part in parts) zip.Add(part.Item1, part.Item2);
                zip.Finish();
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            try { File.WriteAllText(stampPath, CurrentStamp()); }
            catch (Exception ex) { PluginLog.Warning("Toolbar.Cui.Stamp", ex.Message); }
        }

        /// <summary>首次生成时的停靠方向。旧版把用户拖出来的摆放记在 toolbar.xml，
        /// 升级到 CUI 工具栏时沿用它一次，用户不会觉得"位置忽然又变了"；之后交给 AutoCAD 记。</summary>
        private static string InitialOrient()
        {
            try
            {
                var saved = ToolbarPlacement.Load();
                if (saved != null && saved.HasDock)
                {
                    switch (saved.Dock)
                    {
                        case 0: return "top";
                        case 1: return "bottom";
                        case 2: return "left";
                        case 3: return "right";
                        case 4: return "floating";
                    }
                }
            }
            catch { }
            return "bottom";   // 默认：底部横排一排（就是这个插件一直在用的样子）
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        private static string XmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private const string ContentTypes =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"cui\" ContentType=\"text/xml\" />" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\" />" +
            "<Default Extension=\"bmp\" ContentType=\"image/bmp\" />" +
            "<Default Extension=\"xml\" ContentType=\"text/xml\" /></Types>";

        private static string BuildHeaderCui(int major, int minor)
        {
            return "<?xml version=\"1.0\"?>\r\n" +
                   "<CustSection xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n" +
                   "  <FileVersion MajorVersion=\"0\" MinorVersion=\"6\" IncrementalVersion=\"1\" UserVersion=\"0\" />\r\n" +
                   "  <Header>\r\n" +
                   "    <CommonConfiguration>\r\n" +
                   "      <CommonItems>\r\n" +
                   "        <ModifiedRev MajorVersion=\"" + major + "\" MinorVersion=\"" + minor + "\" UserVersion=\"0\" />\r\n" +
                   "      </CommonItems>\r\n" +
                   "    </CommonConfiguration>\r\n" +
                   "  </Header>\r\n" +
                   "</CustSection>\r\n";
        }

        private static string BuildMenuGroupCui(int major, int minor, ToolbarInstaller.ToolbarButtonSpec[] buttons)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\r\n");
            sb.Append("<MenuGroup xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
            sb.Append(" Name=\"").Append(MenuGroupName).Append("\" DisplayName=\"X-批注\">\r\n");
            sb.Append("  <MacroGroup Name=\"").Append(MenuGroupName).Append("Macros\" Citizen=\"A\">\r\n");
            for (var i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                var id = MacroId(i);
                sb.Append("    <MenuMacro UID=\"").Append(id).Append("\">\r\n");
                sb.Append("      <Macro type=\"Any\">\r\n");
                sb.Append("        <Revision MajorVersion=\"").Append(major).Append("\" MinorVersion=\"").Append(minor).Append("\" UserVersion=\"0\"/>\r\n");
                sb.Append("        <ModifiedRev MajorVersion=\"").Append(major).Append("\" MinorVersion=\"").Append(minor).Append("\" UserVersion=\"0\"/>\r\n");
                sb.Append("        <Name xlate=\"true\" UID=\"XLSN_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\">")
                  .Append(XmlEscape(b.Name)).Append("</Name>\r\n");
                // 宏结尾必须留一个空格：AutoCAD 用空格把命令名与后续输入隔开，少了会被当成命令名的一部分。
                sb.Append("        <Command>^C^C_").Append(b.Command).Append(" </Command>\r\n");
                sb.Append("        <HelpString xlate=\"true\" UID=\"XLSH_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\">")
                  .Append(XmlEscape(b.Help)).Append("</HelpString>\r\n");
                var code = string.IsNullOrEmpty(b.Glyph) ? "0000"
                    : char.ConvertToUtf32(b.Glyph, 0).ToString("X4", CultureInfo.InvariantCulture);
                sb.Append("        <SmallImage Name=\"xpz_").Append(code).Append("_16.bmp\"/>\r\n");
                sb.Append("        <LargeImage Name=\"xpz_").Append(code).Append("_32.bmp\"/>\r\n");
                sb.Append("        <CLICommand xlate=\"true\" UID=\"XLSC_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\">")
                  .Append(b.Command).Append("</CLICommand>\r\n");
                sb.Append("      </Macro>\r\n");
                sb.Append("    </MenuMacro>\r\n");
            }
            sb.Append("  </MacroGroup>\r\n");
            sb.Append("</MenuGroup>\r\n");
            return sb.ToString();
        }

        private static string BuildToolbarRootCui(int major, int minor,
            ToolbarInstaller.ToolbarButtonSpec[] buttons, string orient)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\r\n");
            sb.Append("<ToolbarRoot xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n");
            // ToolbarVisible="show"：加载后直接显示。否则得用户自己在工具栏右键菜单里勾，等于"装完没效果"。
            sb.Append("  <Toolbar ToolbarOrient=\"").Append(orient).Append("\" ToolbarVisible=\"show\" xval=\"0\" yval=\"0\" rows=\"1\"");
            sb.Append(" UID=\"").Append(ToolbarUid).Append("\">\r\n");
            sb.Append("    <ModifiedRev MajorVersion=\"").Append(major).Append("\" MinorVersion=\"").Append(minor).Append("\" UserVersion=\"0\"/>\r\n");
            sb.Append("    <Alias>").Append(ToolbarAlias).Append("</Alias>\r\n");
            sb.Append("    <Name xlate=\"true\" UID=\"XLS_XTB\">X-批注</Name>\r\n");
            for (var i = 0; i < buttons.Length; i++)
            {
                sb.Append("    <ToolbarButton IsSeparator=\"false\" UID=\"TBB_XPZ_").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\">\r\n");
                sb.Append("      <ModifiedRev MajorVersion=\"").Append(major).Append("\" MinorVersion=\"").Append(minor).Append("\" UserVersion=\"0\"/>\r\n");
                sb.Append("      <Name xlate=\"true\" UID=\"XLSX").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\">")
                  .Append(XmlEscape(buttons[i].Name)).Append("</Name>\r\n");
                sb.Append("      <MenuItem>\r\n");
                sb.Append("        <MacroRef MenuMacroID=\"").Append(MacroId(i)).Append("\"/>\r\n");
                sb.Append("      </MenuItem>\r\n");
                sb.Append("    </ToolbarButton>\r\n");
            }
            sb.Append("  </Toolbar>\r\n");
            sb.Append("</ToolbarRoot>\r\n");
            return sb.ToString();
        }

        /// <summary>宏的 UID。用命令名（<c>ID_XPZ_DRAW</c>）而不是序号：按钮顺序调整时，
        /// 用户在 CUI 编辑器里做的宏引用不会跟着错位。</summary>
        private static string MacroId(int index)
        {
            var command = ToolbarInstaller.GetButtons()[index].Command;
            return "ID_" + (string.IsNullOrEmpty(command) ? "XPZ_" + index : command);
        }

        private static string BuildPackageInfo(List<Tuple<string, byte[]>> parts)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<MenuPackageParts>\r\n");
            foreach (var part in parts)
            {
                sb.Append("  <PartData PartData_Name=\"/").Append(part.Item1)
                  .Append("\" PartData_Modified=\"2013-02-22T15:55:26.2240524+01:00\" />\r\n");
            }
            sb.Append("  <PartData PartData_Name=\"/Menu_Package_Info.xml\" PartData_Modified=\"2013-02-22T15:55:26.2240524+01:00\" />\r\n");
            sb.Append("</MenuPackageParts>\r\n");
            return sb.ToString();
        }

        private static string BuildRels(List<Tuple<string, byte[]>> parts)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < parts.Count; i++)
            {
                // 包清单（Menu_Package_Info.xml）本身不在关系表里 —— 它不是一个可加载的部件，
                // 列进去会被宿主当成又一个 CUI 去读（AutoCAD 自带的包也是这么处理的）。
                if (parts[i].Item1.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var type = parts[i].Item1.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? "Image" : "CUI";
                sb.Append("<Relationship Type=\"").Append(type).Append("\" Target=\"/").Append(parts[i].Item1)
                  .Append("\" Id=\"Rxpz").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\" />");
            }
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string Describe(Exception ex)
        {
            var text = "";
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (text.Length > 0) text += " <- ";
                text += current.GetType().Name + ": " + current.Message;
            }
            return text;
        }

        // ================= 极简 ZIP 写入（store 方式，不压缩） =================
        // 不引 System.IO.Compression：AutoCAD 2013-2024 那条线是 net40，那套 API 在 4.5 才有；
        // 而 cuix 只有几 KB，不压缩完全无所谓。格式按 PKZIP 规范手写，避免多一个程序集依赖。

        private sealed class StoreZipWriter
        {
            private const uint LocalSig = 0x04034b50;
            private const uint CentralSig = 0x02014b50;
            private const uint EndSig = 0x06054b50;

            private readonly Stream _stream;
            private readonly List<byte[]> _names = new List<byte[]>();
            private readonly List<uint> _crcs = new List<uint>();
            private readonly List<int> _sizes = new List<int>();
            private readonly List<long> _offsets = new List<long>();

            public StoreZipWriter(Stream stream) { _stream = stream; }

            public void Add(string name, byte[] data)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                var crc = Crc32(data);
                var offset = _stream.Position;

                WriteUInt32(LocalSig);
                WriteUInt16(20);                        // version needed
                WriteUInt16(0x0800);                    // flags: UTF-8 文件名
                WriteUInt16(0);                         // method: store
                WriteUInt16(0); WriteUInt16(0);         // DOS time / date（不参与校验）
                WriteUInt32(crc);
                WriteUInt32((uint)data.Length);
                WriteUInt32((uint)data.Length);
                WriteUInt16((ushort)nameBytes.Length);
                WriteUInt16(0);
                _stream.Write(nameBytes, 0, nameBytes.Length);
                _stream.Write(data, 0, data.Length);

                _names.Add(nameBytes);
                _crcs.Add(crc);
                _sizes.Add(data.Length);
                _offsets.Add(offset);
            }

            public void Finish()
            {
                var centralStart = _stream.Position;
                for (var i = 0; i < _names.Count; i++)
                {
                    WriteUInt32(CentralSig);
                    WriteUInt16(20); WriteUInt16(20);
                    WriteUInt16(0x0800);
                    WriteUInt16(0);
                    WriteUInt16(0); WriteUInt16(0);
                    WriteUInt32(_crcs[i]);
                    WriteUInt32((uint)_sizes[i]);
                    WriteUInt32((uint)_sizes[i]);
                    WriteUInt16((ushort)_names[i].Length);
                    WriteUInt16(0); WriteUInt16(0); WriteUInt16(0);
                    WriteUInt16(0); WriteUInt32(0);
                    WriteUInt32((uint)_offsets[i]);
                    _stream.Write(_names[i], 0, _names[i].Length);
                }
                var centralSize = _stream.Position - centralStart;

                WriteUInt32(EndSig);
                WriteUInt16(0); WriteUInt16(0);
                WriteUInt16((ushort)_names.Count); WriteUInt16((ushort)_names.Count);
                WriteUInt32((uint)centralSize);
                WriteUInt32((uint)centralStart);
                WriteUInt16(0);
                _stream.Flush();
            }

            private void WriteUInt16(int value)
            {
                _stream.WriteByte((byte)(value & 0xFF));
                _stream.WriteByte((byte)(value >> 8 & 0xFF));
            }

            private void WriteUInt32(uint value)
            {
                _stream.WriteByte((byte)(value & 0xFF));
                _stream.WriteByte((byte)(value >> 8 & 0xFF));
                _stream.WriteByte((byte)(value >> 16 & 0xFF));
                _stream.WriteByte((byte)(value >> 24 & 0xFF));
            }

            private static readonly uint[] CrcTable = BuildCrcTable();

            private static uint[] BuildCrcTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    var value = i;
                    for (var bit = 0; bit < 8; bit++)
                        value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                    table[i] = value;
                }
                return table;
            }

            private static uint Crc32(byte[] data)
            {
                var crc = 0xFFFFFFFFu;
                for (var i = 0; i < data.Length; i++)
                    crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xFFFFFFFFu;
            }
        }
    }
}
#endif
