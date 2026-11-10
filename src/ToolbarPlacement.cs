using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace XAnnotation
{
    /// <summary>「X-批注」工具栏的摆放记录：停靠方向（+ 浮动时的行数）。
    /// <para><b>能记的只有"停在哪条边"</b>。列内的具体位置记不住 —— 这是宿主限制：<c>Dock(n)</c> 一律把
    /// 新建的工具栏插到该停靠区最前面，停靠状态下的 <c>Left/Top</c> 是只读的行列索引（写进去读回来还是 0）。
    /// 曾试过把工具栏做成 CUI 分部菜单、让 AutoCAD 自己记 <c>dockrow</c>：位置是记住了，
    /// 但宿主每次启动都要解析那个 cuix，把 CAD 卡成假死（2026-11-10 回退，见 ToolbarInstaller.CleanupLegacyCui）。
    /// <b>不要再走那条路。</b></para>
    /// <para><b>为什么不塞进 settings.xml</b>：那份文件是由
    /// <c>SettingsWindow.SharedSettings()</c> 的同一个对象<b>整份回写</b>的（关批注面板、保存设置都会写），
    /// 工具栏若另外拿一份读写，两边就会互相覆盖 —— 典型现象是"刚记住的摆放，关一下面板又变回去了"。
    /// 所以单独存一个小文件，互不干扰。</para>
    /// <para>文件：<c>%AppData%\XAnnotation\toolbar.xml</c>。读写失败一律静默（属增强项，不能影响插件运行）。</para></summary>
    internal sealed class ToolbarPlacement
    {
        /// <summary>停靠状态：0=顶部、1=底部、2=左侧、3=右侧、4=浮动（AutoCAD ActiveX 的
        /// <c>AcToolbarDockStatus</c>，已在本机 AutoCAD 2014 上实测：标准/图层=0 顶部、绘图=2 左侧、
        /// 修改=3 右侧、X-批注=1 底部）。<b>-1 表示没有记录</b>。</para></summary>
        public int Dock { get; set; } = -1;

        /// <summary>浮动时排成几行（默认 1 行横排）；停靠时无意义。</summary>
        public int Rows { get; set; } = 1;

        /// <summary>是否有有效记录（没记录时调用方走默认摆放）。</summary>
        public bool HasDock
        {
            get { return Dock >= 0; }
        }

        public static ToolbarPlacement Load()
        {
            try
            {
                var path = FilePath();
                if (!File.Exists(path)) return null;
                var root = XDocument.Load(path).Root;
                if (root == null) return null;

                var placement = new ToolbarPlacement
                {
                    Dock = ParseInt(Attribute(root, "Dock"), -1),
                    Rows = ParseInt(Attribute(root, "Rows"), 1)
                };
                if (placement.Rows <= 0) placement.Rows = 1;
                return placement.HasDock ? placement : null;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Placement.Load", ex.Message);
                return null;
            }
        }

        public static void Save(ToolbarPlacement placement)
        {
            if (placement == null || !placement.HasDock) return;
            try
            {
                var document = new XDocument(
                    new XElement("XAnnotationToolbar",
                        new XAttribute("Dock", placement.Dock),
                        new XAttribute("Rows", placement.Rows)));
                // 先写临时文件再替换：CAD 被强制结束时不会留下半截 XML（下次启动会当成"没记录"）。
                var path = FilePath();
                var temp = path + ".tmp";
                document.Save(temp);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("Toolbar.Placement.Save", ex.Message);
            }
        }

        /// <summary>两份记录是否等价：只比停靠方向与浮动行数（Left/Top 已不再参与还原，见类注释）。
        /// <para>比较跑在 CAD 的 Idle 回调里，任何抛出去的异常都会变成致命错误 ——
        /// 所以这里刻意只做两个整数的比较，不碰 <see cref="int.MinValue"/> 的减法/取绝对值。</para></summary>
        public bool SameAs(ToolbarPlacement other)
        {
            if (other == null) return false;
            return Dock == other.Dock && Rows == other.Rows;
        }

        public string Describe()
        {
            var names = new[] { "顶部横排", "底部横排", "左侧竖排", "右侧竖排", "浮动" };
            var name = Dock >= 0 && Dock < names.Length ? names[Dock] : "未记录";
            if (Dock == 4) name += "/" + Rows + " 行";
            return name;
        }

        private static string FilePath()
        {
            return Path.Combine(AppPaths.DataFolder, "toolbar.xml");
        }

        private static string Attribute(XElement root, string name)
        {
            var attribute = root.Attribute(name);
            return attribute == null ? null : attribute.Value;
        }

        private static int ParseInt(string text, int fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }
    }
}
