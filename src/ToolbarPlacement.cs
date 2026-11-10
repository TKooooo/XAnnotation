using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace XAnnotation
{
    /// <summary>「X-批注」工具栏的摆放记录：停靠方向 + 浮动时的屏幕坐标 + 浮动行数。
    /// <para><b>从 1.0.11（2026-11-10 第 2 版）起，AutoCAD 平台不再靠它还原位置</b>：
    /// 工具栏改成了 CUI 正式工具栏（见 <see cref="ToolbarCui"/>），停靠方向与列内次序由 AutoCAD
    /// 自己记在主 CUI 的工作空间里（<c>WSToolbar</c> 的 <c>dockrow</c>/<c>dockcolumn</c>），重启精确还原。
    /// 这份文件仍保留两个用途：<b>①</b> 首次生成 cuix 时当作初始停靠方向（用户升级后位置不突变）；
    /// <b>②</b> CUI 方案失败、退回 COM 建法时的位置记忆（那时只能记住停在哪条边）。</para>
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

        /// <summary>浮动时的屏幕左上角（像素，AutoCAD 主窗口客户区坐标）；停靠时无意义。</summary>
        public int Left { get; set; } = int.MinValue;

        /// <summary>见 <see cref="Left"/>。</summary>
        public int Top { get; set; } = int.MinValue;

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
                    Left = ParseInt(Attribute(root, "Left"), int.MinValue),
                    Top = ParseInt(Attribute(root, "Top"), int.MinValue),
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
                        new XAttribute("Left", placement.Left),
                        new XAttribute("Top", placement.Top),
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

        /// <summary>两份记录是否等价。方向之外，坐标与行数也一起比 —— 用户在同一个停靠区里
        /// 上下拖动（列内偏移变了）也要能记下来。
        /// <para>坐标用**完全相等**判定而不是"差 1 像素也算一样"：值都是直接从 AutoCAD 读回来的整数，
        /// 不存在抖动；而 <c>Math.Abs(Left - other.Left)</c> 在 <see cref="int.MinValue"/>（没读到）参与运算时
        /// 会溢出抛异常 —— 这个比较跑在 CAD 的 Idle 回调里，抛出去就是致命错误。</para></summary>
        public bool SameAs(ToolbarPlacement other)
        {
            if (other == null) return false;
            if (Dock != other.Dock) return false;
            if (Left != other.Left || Top != other.Top) return false;
            return Rows == other.Rows;
        }

        public string Describe()
        {
            var names = new[] { "顶部横排", "底部横排", "左侧竖排", "右侧竖排", "浮动" };
            var name = Dock >= 0 && Dock < names.Length ? names[Dock] : "未记录";
            if (Left != int.MinValue || Top != int.MinValue)
            {
                name += "(x=" + (Left == int.MinValue ? "?" : Left.ToString(CultureInfo.InvariantCulture))
                     + ",y=" + (Top == int.MinValue ? "?" : Top.ToString(CultureInfo.InvariantCulture)) + ")";
            }
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
