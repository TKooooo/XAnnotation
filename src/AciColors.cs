using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;

namespace XAnnotation
{
    /// <summary>ACI（AutoCAD Color Index）颜色项，用于下拉菜单展示。</summary>
    public sealed class AciColorItem
    {
        public short Index { get; }
        public string Name { get; }

        /// <summary>下拉里显示的文本：标准色带中文名（"1  红"），其余只显示索引号。</summary>
        public string Display => string.IsNullOrEmpty(Name) ? Index.ToString() : $"{Index}  {Name}";

        public SolidColorBrush Brush { get; }

        internal AciColorItem(short index, string name, byte r, byte g, byte b)
        {
            Index = index;
            Name = name;
            Brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        /// <summary>下拉框的键盘检索用的是 ToString()，重写后直接敲 "202" 就能定位到该颜色。</summary>
        public override string ToString() => Display;
    }

    /// <summary>ACI 255 色索引 → RGB 映射，供设置界面颜色下拉菜单使用。
    /// <para>色值逐条取自 AutoCAD 官方 ACI 色表，<b>不要改成公式推算</b>：10–249 是
    /// 24 组色相（每 15° 一组）× 10 档明度，用 HSL/HSV 公式生成会有若干组差 1～2 个色阶，
    /// 界面色块与实际落图颜色就对不上了。</para></summary>
    public static class AciColors
    {
        /// <summary>全部 255 种 ACI 颜色（索引 1–255；0=ByBlock 与 256=ByLayer 不是实色，不在表内）。</summary>
        public static ReadOnlyCollection<AciColorItem> All { get; }

        /// <summary>10–249 共 240 个工业色，按索引顺序排列，每个 6 位十六进制（RRGGBB）。
        /// <para>每行 10 个＝同一色相的一组，组内依次是：亮 / 亮淡 / 中亮 / 中亮淡 / 中 / 中淡 / 暗 / 暗淡 / 更暗 / 更暗淡。</para>
        /// <para>行尾注释是该组的索引区间与色相角（0° 起每 15° 一组，24 组正好凑满 240 色）。</para></summary>
        private const string Industrial =
            "FF0000FFAAAABD0000BD7E7E8100008156566800006845454F00004F3535" +   // 10–19  0°
            "FF3F00FFBFAABD2E00BD8D7E811F00816056681900684E454F13004F3B35" +   // 20–29  15°
            "FF7F00FFD4AABD5E00BD9D7E814000816B566834006856454F27004F4235" +   // 30–39  30°
            "FFBF00FFEAAABD8D00BDAD7E816000817656684E00685F454F3B004F4935" +   // 40–49  45°
            "FFFF00FFFFAABDBD00BDBD7E8181008181566868006868454F4F004F4F35" +   // 50–59  60°
            "BFFF00EAFFAA8DBD00ADBD7E6081007681564E68005F68453B4F00494F35" +   // 60–69  75°
            "7FFF00D4FFAA5EBD009DBD7E4081006B8156346800566845274F00424F35" +   // 70–79  90°
            "3FFF00BFFFAA2EBD008DBD7E1F81006081561968004E6845134F003B4F35" +   // 80–89  105°
            "00FF00AAFFAA00BD007EBD7E008100568156006800456845004F00354F35" +   // 90–99  120°
            "00FF3FAAFFBF00BD2E7EBD8D00811F56816000681945684E004F13354F3B" +   // 100–109  135°
            "00FF7FAAFFD400BD5E7EBD9D00814056816B006834456856004F27354F42" +   // 110–119  150°
            "00FFBFAAFFEA00BD8D7EBDAD00816056817600684E45685F004F3B354F49" +   // 120–129  165°
            "00FFFFAAFFFF00BDBD7EBDBD008181568181006868456868004F4F354F4F" +   // 130–139  180°
            "00BFFFAAEAFF008DBD7EADBD006081567681004E68455F68003B4F35494F" +   // 140–149  195°
            "007FFFAAD4FF005EBD7E9DBD004081566B8100346845566800274F35424F" +   // 150–159  210°
            "003FFFAABFFF002EBD7E8DBD001F81566081001968454E6800134F353B4F" +   // 160–169  225°
            "0000FFAAAAFF0000BD7E7EBD00008156568100006845456800004F35354F" +   // 170–179  240°
            "3F00FFBFAAFF2E00BD8D7EBD1F00816056811900684E456813004F3B354F" +   // 180–189  255°
            "7F00FFD4AAFF5E00BD9D7EBD4000816B568134006856456827004F42354F" +   // 190–199  270°
            "BF00FFEAAAFF8D00BDAD7EBD6000817656814E00685F45683B004F49354F" +   // 200–209  285°
            "FF00FFFFAAFFBD00BDBD7EBD8100818156816800686845684F004F4F354F" +   // 210–219  300°
            "FF00BFFFAAEABD008DBD7EAD81006081567668004E68455F4F003B4F3549" +   // 220–229  315°
            "FF007FFFAAD4BD005EBD7E9D81004081566B6800346845564F00274F3542" +   // 230–239  330°
            "FF003FFFAABFBD002EBD7E8D81001F81566068001968454E4F00134F353B";   // 240–249  345°

        static AciColors()
        {
            var list = new List<AciColorItem>(255);

            // 1–9: 标准色（保留中文名，色值按官方 ACI 表取，界面色块与落图颜色一致）
            list.Add(new AciColorItem(1, "红", 255, 0, 0));
            list.Add(new AciColorItem(2, "黄", 255, 255, 0));
            list.Add(new AciColorItem(3, "绿", 0, 255, 0));
            list.Add(new AciColorItem(4, "青", 0, 255, 255));
            list.Add(new AciColorItem(5, "蓝", 0, 0, 255));
            list.Add(new AciColorItem(6, "洋红", 255, 0, 255));
            list.Add(new AciColorItem(7, "白", 255, 255, 255));
            list.Add(new AciColorItem(8, "深灰", 65, 65, 65));
            list.Add(new AciColorItem(9, "浅灰", 128, 128, 128));

            // 10–249: 240 个工业色，索引紧接在 1–9 之后（10 + 序号）
            for (var i = 0; i < Industrial.Length; i += 6)
            {
                list.Add(new AciColorItem(
                    (short)(10 + i / 6),
                    string.Empty,
                    HexByte(Industrial, i),
                    HexByte(Industrial, i + 2),
                    HexByte(Industrial, i + 4)));
            }

            // 250–255: 六级灰度（官方取值 51 / 80 / 105 / 130 / 190 / 255）
            list.Add(new AciColorItem(250, "250", 51, 51, 51));
            list.Add(new AciColorItem(251, "251", 80, 80, 80));
            list.Add(new AciColorItem(252, "252", 105, 105, 105));
            list.Add(new AciColorItem(253, "253", 130, 130, 130));
            list.Add(new AciColorItem(254, "254", 190, 190, 190));
            list.Add(new AciColorItem(255, "255", 255, 255, 255));

            All = new ReadOnlyCollection<AciColorItem>(list);
        }

        /// <summary>根据 ACI 索引查找颜色项，找不到则返回索引 7（白色）。</summary>
        public static AciColorItem Find(short index)
        {
            // 列表下标 0 对应索引 1
            var i = index - 1;
            if (i >= 0 && i < All.Count) return All[i];
            return All[6]; // 默认白色
        }

        /// <summary>读表里第 offset 个字符起的两位十六进制。</summary>
        private static byte HexByte(string table, int offset) =>
            byte.Parse(table.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
