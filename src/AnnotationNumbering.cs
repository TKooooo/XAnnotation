using System;
using System.Text;

namespace XAnnotation
{
    /// <summary>批注编号的生成规则：<b>前缀 + 三位序号</b>（默认 "X" → <c>X-001</c>、<c>X-002</c>）。
    /// <para><b>凡是生成编号的地方都必须走 <see cref="Format"/> / <see cref="ResolvePrefix"/>：</b>
    /// 命令、批注面板、CSV 导入补号三处各写过一遍 "X-" 字面量，改前缀时漏一处就会
    /// "设置里改了前缀，新建的批注还是 X-00x"。</para>
    /// <para>前缀允许自带分隔符（填 "X-"、"建施-"、"A_" 都行）：末尾已是 <c>- _ . #</c> 时不再补 "-"，
    /// 否则自动补一个 "-"，所以用户填 "X" 和填 "X-" 得到的都是 <c>X-001</c>。</para></summary>
    internal static class AnnotationNumbering
    {
        /// <summary>出厂前缀。</summary>
        public const string DefaultPrefix = "X";

        /// <summary>前缀最多保留几个字符（够长、又不至于把编号撑得过宽）。</summary>
        private const int MaxPrefixLength = 12;

        /// <summary>可以当作分隔符的结尾字符（用户自己填了就不用再补 "-"）。</summary>
        private const string Separators = "-_.#";

        /// <summary>把用户填的前缀规范化：去首尾空白、去掉会撑坏编号/图面文字的字符、限长；
        /// 空串一律回落到默认前缀 <c>X</c>（保存时不必再校验空值，调用方也不用到处判空）。</summary>
        public static string NormalizePrefix(string prefix)
        {
            var raw = (prefix ?? "").Trim();
            if (raw.Length == 0) return DefaultPrefix;

            var builder = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (char.IsControl(ch)) continue;
                // 编号要进图面文字与 CSV，这几种字符在文件名/表格里最容易出事，直接丢弃。
                if (ch == '<' || ch == '>' || ch == '/' || ch == '\\' || ch == '"' || ch == '|' ||
                    ch == '?' || ch == '*' || ch == ',') continue;
                builder.Append(ch);
                if (builder.Length >= MaxPrefixLength) break;
            }

            var value = builder.ToString().Trim();
            return value.Length == 0 ? DefaultPrefix : value;
        }

        /// <summary>取设置里的当前前缀（读 settings.xml）。取不到就用默认值，绝不抛。</summary>
        public static string ResolvePrefix(AnnotationSettings settings)
        {
            try { return NormalizePrefix(settings?.NumberPrefix); }
            catch (Exception ex)
            {
                PluginLog.Warning("Numbering.Prefix", ex.Message);
                return DefaultPrefix;
            }
        }

        /// <summary>前缀是否已经自带分隔符（"X-" 的结尾是 '-'）。</summary>
        private static bool EndsWithSeparator(string prefix)
            => prefix.Length > 0 && Separators.IndexOf(prefix[prefix.Length - 1]) >= 0;

        /// <summary>生成编号文本：<c>X</c> + 1 → <c>X-001</c>；<c>建施-</c> + 12 → <c>建施-012</c>。</summary>
        public static string Format(string prefix, int sequence)
        {
            var value = NormalizePrefix(prefix);
            var seq = sequence < 1 ? 1 : sequence;
            return value + (EndsWithSeparator(value) ? "" : "-") + seq.ToString("D3");
        }

        /// <summary>编号预览文本（设置窗口里显示"改完长什么样"用）。</summary>
        public static string Preview(string prefix, int sequence = 1) => Format(prefix, sequence);

        /// <summary>取编号<b>末尾</b>的连续数字："X-012" → 12、"J-3" → 3、"A1-7" → 7。
        /// <para>取末尾而不是"第一个数字段"：前缀本身可能带数字（"A1-7"），从头取会解析成 1，
        /// 之后新建的编号就会和已有的撞号。解析不出来（如 "说明-甲"）返回 -1。</para></summary>
        public static int TrailingNumber(string number)
        {
            if (string.IsNullOrEmpty(number)) return -1;
            var end = number.Length - 1;
            while (end >= 0 && char.IsWhiteSpace(number[end])) end--;
            if (end < 0 || !char.IsDigit(number[end])) return -1;

            var start = end;
            while (start >= 0 && char.IsDigit(number[start])) start--;
            var digits = number.Substring(start + 1, end - start);
            return int.TryParse(digits, out var value) ? value : -1;
        }
    }
}
