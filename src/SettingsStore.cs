using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace XAnnotation
{
    /// <summary>设置持久化：将 AnnotationSettings 读写到 %AppData%/XAnnotation/settings.xml。</summary>
    internal static class SettingsStore
    {
        private static readonly string Folder = AppPaths.DataFolder;
        private static readonly string PathName = Path.Combine(Folder, "settings.xml");

        /// <summary>settings.xml 结构版本，用于做一次性迁移。
        /// <para>1（及无该节点）→ 老格式；<b>2</b> → 「云线自适应 / 字体自适应」默认勾选。</para></summary>
        private const int CurrentSchemaVersion = 2;

        public static AnnotationSettings Load()
        {
            var s = new AnnotationSettings();
            try
            {
                if (!File.Exists(PathName)) return s;
                var x = XElement.Load(PathName);
                var schemaVersion = ParseInt(Get(x, "SchemaVersion", "0"), 0);
                s.Shape = Get(x, "Shape", s.Shape); s.CloudStyle = Get(x, "CloudStyle", s.CloudStyle);
                s.LayerName = Get(x, "LayerName", s.LayerName);
                // 旧品牌版本的默认图层名（早期 "LA-批注"、上一代 "GM-批注"）：
                // 升级/换品牌后统一按新默认名 "X-批注" 使用（用户自定义过的图层名不受影响）。
                if (string.Equals(s.LayerName, "LA-批注", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.LayerName, "GM-批注", StringComparison.OrdinalIgnoreCase))
                {
                    s.LayerName = "X-批注";
                }
                s.TextStyleName = Get(x, "TextStyleName", s.TextStyleName);
                s.ColorIndex = ParseShort(Get(x, "ColorIndex", s.ColorIndex.ToString()), s.ColorIndex);
                s.CloudColor = ParseShort(Get(x,"CloudColor","6"),6); s.LeaderColor=ParseShort(Get(x,"LeaderColor","6"),6); s.TextColor=ParseShort(Get(x,"TextColor","6"),6);s.BoxColor=ParseShort(Get(x,"BoxColor","6"),6); s.ReplyColor=ParseShort(Get(x,"ReplyColor","6"),6);s.ScreenshotBackgroundColor=ParseShort(Get(x,"ScreenshotBackgroundColor","7"),7);s.PassColor=ParseShort(Get(x,"PassColor","6"),6);s.CheckColor=ParseShort(Get(x,"CheckColor","6"),6);s.ScreenshotBackgroundOnceReply=ParseBool(Get(x,"ScreenshotBackgroundOnceReply","false"),false);
                s.TextHeight = ParseDouble(Get(x, "TextHeight", "3"), s.TextHeight);
                s.HeaderHeight=ParseDouble(Get(x,"HeaderHeight","3"),3);s.SecondLineHeight=ParseDouble(Get(x,"SecondLineHeight","3"),3);
                s.CloudRadius = ParseDouble(Get(x, "CloudRadius", "2"), s.CloudRadius);
                s.LineWidth=ParseDouble(Get(x,"LineWidth","0.2"),0.2);s.ScaleRatio=ParseDouble(Get(x,"ScaleRatio","1"),1);s.CloudAutoFit=ParseBool(Get(x,"CloudAutoFit","true"),true);s.FontAutoFit=ParseBool(Get(x,"FontAutoFit","true"),true);s.FixedWidth=ParseBool(Get(x,"FixedWidth","false"),false);s.FixedWidthValue=ParseDouble(Get(x,"FixedWidthValue","55"),55);
                s.DefaultAuthor = Get(x, "DefaultAuthor", s.DefaultAuthor);
                s.DefaultDiscipline = Get(x, "DefaultDiscipline", s.DefaultDiscipline);
                s.DefaultRole=Get(x,"DefaultRole",s.DefaultRole);
                s.AutoNumber = ParseBool(Get(x, "AutoNumber", "true"), true);
                s.NextNumber = ParseInt(Get(x, "NextNumber", "1"), 1);
                s.AutoCloseOrtho=ParseBool(Get(x,"AutoCloseOrtho","true"),true);s.AutoCloseSnap=ParseBool(Get(x,"AutoCloseSnap","true"),true);s.ViewTopIsNorth=ParseBool(Get(x,"ViewTopIsNorth","true"),true);s.DoubleClickEdit=ParseBool(Get(x,"DoubleClickEdit","true"),true);s.ContinuousAnnotation=ParseBool(Get(x,"ContinuousAnnotation","true"),true);s.CloudOnly=ParseBool(Get(x,"CloudOnly","false"),false);s.SameColors=ParseBool(Get(x,"SameColors","true"),true);s.LayerAppendDate=ParseBool(Get(x,"LayerAppendDate","true"),true);s.LayerAppendName=ParseBool(Get(x,"LayerAppendName","false"),false);s.DateBeforeName=ParseBool(Get(x,"DateBeforeName","true"),true);s.Connector=Get(x,"Connector","-");s.Plottable=ParseBool(Get(x,"Plottable","false"),false);s.CheckHeight=ParseDouble(Get(x,"CheckHeight","8"),8);
                s.AutoTextViewPercent=ParseDouble(Get(x,"AutoTextViewPercent","5"),5);
                s.ShowDiscipline=ParseBool(Get(x,"ShowDiscipline","true"),true);s.ShowAuthor=ParseBool(Get(x,"ShowAuthor","true"),true);s.ShowRole=ParseBool(Get(x,"ShowRole","true"),true);s.ShowDate=ParseBool(Get(x,"ShowDate","true"),true);s.ShowStatus=ParseBool(Get(x,"ShowStatus","true"),true);s.ShowDrawingNo=ParseBool(Get(x,"ShowDrawingNo","true"),true);
                s.ArchiveOnCreate=ParseBool(Get(x,"ArchiveOnCreate","true"),true);
                s.ContentSuggest=ParseBool(Get(x,"ContentSuggest","true"),true);
                // 功能选择框"上次的选择"（外形 + 三个绘制方式勾选项）：下次打开面板时恢复。
                s.PanelShape=Get(x,"PanelShape","矩形");
                s.PanelContinuous=ParseBool(Get(x,"PanelContinuous","false"),false);
                s.PanelMultiMode=ParseBool(Get(x,"PanelMultiMode","false"),false);
                s.PanelCloudOnly=ParseBool(Get(x,"PanelCloudOnly","false"),false);
                // 按状态给整条批注着色（已回复 / 已完成）。老配置没有这个节点 → 默认开启，
                // 因为这两个状态色的语义就是"整条批注换个颜色"，默认关掉等于功能不可见。
                s.StatusColorize=ParseBool(Get(x,"StatusColorize","true"),true);

                // ── 一次性迁移：自适应默认勾选 ──
                // 老配置文件里的 CloudAutoFit/FontAutoFit 多为 false：那是早期"选比例就自动关自适应"
                // 的副作用写下的，用户会误以为"默认没勾"。这里对无版本号（或低于 v2）的配置强制打开一次，
                // 之后任何一次保存都会写入 SchemaVersion=2，用户在设置里的取舍即被尊重。
                if (schemaVersion < 2)
                {
                    s.CloudAutoFit = true;
                    s.FontAutoFit = true;
                }
            }
            catch (Exception ex) { PluginLog.Error("Settings.Load",ex); }
            return s;
        }

        public static void Save(AnnotationSettings s)
        {
            Directory.CreateDirectory(Folder);
            new XElement("Settings",
                new XElement("SchemaVersion", CurrentSchemaVersion),
                new XElement("Shape",s.Shape),new XElement("CloudStyle",s.CloudStyle),
                new XElement("LayerName", s.LayerName), new XElement("ColorIndex", s.ColorIndex),
                new XElement("TextStyleName",s.TextStyleName),new XElement("CloudColor",s.CloudColor),new XElement("LeaderColor",s.LeaderColor),new XElement("TextColor",s.TextColor),new XElement("BoxColor",s.BoxColor),new XElement("ReplyColor",s.ReplyColor),new XElement("ScreenshotBackgroundColor",s.ScreenshotBackgroundColor),new XElement("PassColor",s.PassColor),new XElement("CheckColor",s.CheckColor),new XElement("ScreenshotBackgroundOnceReply",s.ScreenshotBackgroundOnceReply),
                new XElement("TextHeight", s.TextHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("HeaderHeight",s.HeaderHeight.ToString(CultureInfo.InvariantCulture)),new XElement("SecondLineHeight",s.SecondLineHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("CloudRadius", s.CloudRadius.ToString(CultureInfo.InvariantCulture)),
                new XElement("LineWidth",s.LineWidth.ToString(CultureInfo.InvariantCulture)),new XElement("ScaleRatio",s.ScaleRatio.ToString(CultureInfo.InvariantCulture)),new XElement("CloudAutoFit",s.CloudAutoFit),new XElement("FontAutoFit",s.FontAutoFit),new XElement("FixedWidth",s.FixedWidth),new XElement("FixedWidthValue",s.FixedWidthValue.ToString(CultureInfo.InvariantCulture)),
                new XElement("DefaultAuthor", s.DefaultAuthor), new XElement("DefaultDiscipline", s.DefaultDiscipline),
                new XElement("DefaultRole",s.DefaultRole),new XElement("AutoNumber", s.AutoNumber), new XElement("NextNumber", s.NextNumber),
                new XElement("AutoCloseOrtho",s.AutoCloseOrtho),new XElement("AutoCloseSnap",s.AutoCloseSnap),new XElement("ViewTopIsNorth",s.ViewTopIsNorth),new XElement("DoubleClickEdit",s.DoubleClickEdit),new XElement("ContinuousAnnotation",s.ContinuousAnnotation),new XElement("CloudOnly",s.CloudOnly),new XElement("SameColors",s.SameColors),new XElement("LayerAppendDate",s.LayerAppendDate),new XElement("LayerAppendName",s.LayerAppendName),new XElement("DateBeforeName",s.DateBeforeName),new XElement("Connector",s.Connector),new XElement("Plottable",s.Plottable),new XElement("CheckHeight",s.CheckHeight.ToString(CultureInfo.InvariantCulture)),new XElement("AutoTextViewPercent",s.AutoTextViewPercent.ToString(CultureInfo.InvariantCulture)),
                new XElement("ShowDiscipline",s.ShowDiscipline),new XElement("ShowAuthor",s.ShowAuthor),new XElement("ShowRole",s.ShowRole),new XElement("ShowDate",s.ShowDate),new XElement("ShowStatus",s.ShowStatus),new XElement("ShowDrawingNo",s.ShowDrawingNo),
                new XElement("ArchiveOnCreate",s.ArchiveOnCreate),new XElement("ContentSuggest",s.ContentSuggest),
                // 功能选择框的"上次选择"：Panel* 只被功能选择框读写，快速直绘不受影响。
                new XElement("PanelShape",string.IsNullOrWhiteSpace(s.PanelShape)?"矩形":s.PanelShape),
                new XElement("PanelContinuous",s.PanelContinuous),new XElement("PanelMultiMode",s.PanelMultiMode),new XElement("PanelCloudOnly",s.PanelCloudOnly),
                new XElement("StatusColorize",s.StatusColorize)).Save(PathName);
        }

        private static string Get(XElement x, string n, string f) => (string)x.Element(n) ?? f;
        private static double ParseDouble(string v, double f) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : f;
        private static short ParseShort(string v, short f) => short.TryParse(v, out var n) ? n : f;
        private static int ParseInt(string v, int f) => int.TryParse(v, out var n) ? n : f;
        private static bool ParseBool(string v, bool f) => bool.TryParse(v, out var n) ? n : f;
    }
}
