using System;

namespace XAnnotation
{
    /// <summary>一个可以在设置里"自己起名字"的命令。
    /// <para>只描述<b>能改成什么</b>：默认命令名 + 显示给用户看的标签 + 是否需要保留预选对象。
    /// 真正的改名动作见 <see cref="CommandAliases"/>。</para></summary>
    internal sealed class CommandEntry
    {
        internal CommandEntry(string command, string label, string group, bool keepPickSet = false)
        {
            Command = command;
            Label = label;
            Group = group;
            KeepPickSet = keepPickSet;
        }

        /// <summary>默认命令名（插件内置，永远可用，菜单与工具栏引用的也是它）。</summary>
        internal string Command { get; }

        /// <summary>设置窗口里显示的功能名。</summary>
        internal string Label { get; }

        /// <summary>分组标题（设置窗口按它分段）。</summary>
        internal string Group { get; }

        /// <summary>原命令带了 <c>CommandFlags.UsePickSet</c>：别名调用前要把"预选对象"重新置为
        /// 当前选择，否则经 LISP 转发时可能丢掉预选、变成"请选择对象"。</summary>
        internal bool KeepPickSet { get; }
    }

    /// <summary>插件命令清单（设置里"自定义命令名"那张表的唯一数据源）。
    /// <para><b>新增命令时同步加在这里</b>，否则那个命令在设置里改不了名；反过来，
    /// 这里列的命令必须真实存在于 <see cref="Commands"/>，否则用户起的别名会指向不存在的命令。</para></summary>
    internal static class CommandCatalog
    {
        internal const string CommonGroup = "常用";
        internal const string ToolGroup = "工具与维护";

        internal static readonly CommandEntry[] All =
        {
            // ── 常用 ──
            new CommandEntry("X_PZ_DRAW",     "快速直绘（框选云线 → 填内容）", CommonGroup),
            new CommandEntry("X_PZ_NOTE",     "批注功能选择框",                 CommonGroup),
            new CommandEntry("X_PZ_EDIT",     "编辑批注",                       CommonGroup, true),
            new CommandEntry("X_PZ_MOVE",     "移动批注文字框",                 CommonGroup, true),
            new CommandEntry("X_PZ_DELETE",   "删除批注",                       CommonGroup, true),
            new CommandEntry("X_PZ_LIST",     "批注列表",                       CommonGroup),
            new CommandEntry("X_PZ_LEGEND",   "批注清单（画到图上）",           CommonGroup),
            new CommandEntry("X_PZ_SUMMARY",  "批注汇总",                       CommonGroup),
            new CommandEntry("X_PZ_WORD",     "导出 Word 回复单",               CommonGroup),
            new CommandEntry("X_PZ_CLOUD",    "单绘云线",                       CommonGroup),
            new CommandEntry("X_PZ_ADDCLOUD", "增补云线",                       CommonGroup, true),

            // ── 工具与维护 ──
            new CommandEntry("X_PZ_FILTER",   "过滤批注",                       ToolGroup, true),
            new CommandEntry("X_PZ_FORMAT",   "格式刷",                         ToolGroup, true),
            new CommandEntry("X_PZ_HIDE",     "隐藏批注",                       ToolGroup, true),
            new CommandEntry("X_PZ_SHOW",     "显示批注",                       ToolGroup, true),
            new CommandEntry("X_PZ_REFRESH",  "刷新批注文字",                   ToolGroup),
            new CommandEntry("X_PZ_REPAIR",   "修复复制/粘贴后的批注",          ToolGroup),
            new CommandEntry("X_PZ_EXPORT",   "导出 CSV",                       ToolGroup, true),
            new CommandEntry("X_PZ_IMPORT",   "导入 CSV",                       ToolGroup),
            new CommandEntry("X_PZ_HISTORY",  "批注历史记录",                   ToolGroup),
            new CommandEntry("X_PZ_KB",       "知识库",                         ToolGroup),
            new CommandEntry("X_PZ_SETTINGS", "设置",                           ToolGroup),
            new CommandEntry("X_PZ_TOOLBAR",  "显示/隐藏工具栏",                ToolGroup),
            new CommandEntry("X_PZ_MENU",     "重新加载菜单",                   ToolGroup),
            new CommandEntry("X_PZ_ABOUT",    "关于",                           ToolGroup)
        };

        /// <summary>按默认命令名查一条（不区分大小写、忽略首尾空白）；找不到返回 <c>null</c>。</summary>
        internal static CommandEntry Find(string command)
        {
            var key = Canonical(command);
            if (key.Length == 0) return null;
            for (var i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Command, key, StringComparison.OrdinalIgnoreCase)) return All[i];
            return null;
        }

        /// <summary>默认命令名的规范写法（大写、去空白）。</summary>
        internal static string Canonical(string command) => (command ?? "").Trim().ToUpperInvariant();
    }
}
