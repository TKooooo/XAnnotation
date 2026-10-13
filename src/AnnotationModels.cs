using System;

namespace XAnnotation
{
    /// <summary>单条批注的业务数据，序列化后存储在 DWG 编组 XRecord 中。</summary>
    internal sealed class AnnotationData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Number { get; set; } = "X-001";
        public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
        public string Discipline { get; set; } = "建筑";
        public string Author { get; set; } = Environment.UserName;
        public string Role { get; set; } = "批注人";
        public string Status { get; set; } = "待处理";
        public string Content { get; set; } = "";
        public string DrawingNo { get; set; } = ""; // 图号：入库留痕与知识库检索用
        public double RenderTextHeight { get; set; }
        public double RenderHeaderHeight { get; set; }
        public double RenderSecondLineHeight { get; set; }
        public double RenderCloudRadius { get; set; }
        public double RenderLineWidth { get; set; }

        // ===== 仅 CSV 导入用（不进 XRecord、不参与图内数据）：导出时记下的<b>原图坐标</b>（WCS）。
        // 导入到其他 DWG 时按这些坐标还原批注位置，而不是排布到用户指定的基点网格上。
        // 旧 CSV 没有这些列 → Placed 为 false，退回"指定基点 + 网格排布"的老行为。
        public bool Placed { get; set; }                 // 本行是否带完整的云线范围坐标
        public string PlacedLayout { get; set; }         // 原批注所在布局（"模型" 或 布局名）
        public double PlacedX1 { get; set; }             // 云线范围左下角 X
        public double PlacedY1 { get; set; }
        public double PlacedX2 { get; set; }             // 云线范围右上角 X
        public double PlacedY2 { get; set; }
        public bool PlacedText { get; set; }             // 是否带文字框锚点坐标
        public double PlacedTextX { get; set; }
        public double PlacedTextY { get; set; }
        public double PlacedZ { get; set; }              // 标高（云线/文字所在平面）
    }

    /// <summary>批注全局设置，持久化到本机 XML 文件，包含外形、文字、图层、颜色等全部可配置项。</summary>
    internal sealed class AnnotationSettings
    {
        public string Shape { get; set; } = "矩形";
        public string CloudStyle { get; set; } = "等宽";
        public string LayerName { get; set; } = "X-批注";
        public string TextStyleName { get; set; } = "Standard";
        public short ColorIndex { get; set; } = 6;
        public short CloudColor { get; set; } = 6;
        public short LeaderColor { get; set; } = 6;
        public short TextColor { get; set; } = 6;
        public short BoxColor { get; set; } = 6;
        /// <summary>「已回复」状态下整条批注的颜色。
        /// <para><b>这是"整条批注"的颜色，不是某个元素的颜色</b>：勾选了 <see cref="StatusColorize"/> 时，
        /// 状态为「已回复」的批注，其云线 / 文字 / 边框 / 引线 / 基点标识会<b>全部</b>改成这个颜色，
        /// 用于在图纸上一眼区分"哪些还没处理"。</para></summary>
        public short ReplyColor { get; set; } = 6;
        public short ScreenshotBackgroundColor { get; set; } = 7;
        /// <summary>「已完成」状态下整条批注的颜色（语义同 <see cref="ReplyColor"/>，作用于该状态）。</summary>
        public short PassColor { get; set; } = 6;
        public short CheckColor { get; set; } = 6;
        public bool ScreenshotBackgroundOnceReply { get; set; } = false;
        /// <summary>是否按状态给整条批注着色：状态为「已回复」/「已完成」时，把该批注<b>所有元素</b>
        /// （云线、文字、边框、引线、基点标识）统一改成 <see cref="ReplyColor"/> / <see cref="PassColor"/>；
        /// 状态回到「待处理」时再按各元素的基础色恢复回去。
        /// <para><b>与 <see cref="SameColors"/> 无关</b>：统一颜色管的是"各元素是否用同一个颜色"，
        /// 本开关管的是"整条批注要不要按状态换色"。所以设置窗口里那两个状态色下拉不受"统一颜色"勾选的影响。</para></summary>
        public bool StatusColorize { get; set; } = true;
        // 文字与云线尺寸：未启用自适应时按"打印尺寸(mm)"理解，图上实测值 = 打印值 × 比例分母（见 AnnotationService.ResolveEffectiveSettings）
        public double TextHeight { get; set; } = 3.0;
        public double HeaderHeight { get; set; } = 3.0;
        public double SecondLineHeight { get; set; } = 3.0;
        public double CloudRadius { get; set; } = 2.0;
        public double LineWidth { get; set; } = 0.2;
        /// <summary>比例选型：出图比例 1:N 的分母 N（设置窗口里从下拉选，也可手填 "1:150"）。
        /// 未启用自适应时，字高/云线半径按"打印尺寸(mm) × N"换算为图面单位；启用自适应时该值不参与字高换算。
        /// 字段名沿用旧版 XML 节点 &lt;ScaleRatio&gt;，老配置文件可直接升级。</summary>
        public double ScaleRatio { get; set; } = 1.0;
        public bool CloudAutoFit { get; set; } = true;
        public bool FontAutoFit { get; set; } = true;
        public bool FixedWidth { get; set; } = false;
        public double FixedWidthValue { get; set; } = 55.0;
        public string DefaultAuthor { get; set; } = Environment.UserName;
        public string DefaultDiscipline { get; set; } = "岩土";
        public string DefaultRole { get; set; } = "批注人";
        public bool AutoNumber { get; set; } = true;
        public int NextNumber { get; set; } = 1;
        public bool AutoCloseOrtho { get; set; } = true;
        public bool AutoCloseSnap { get; set; } = true;
        public bool ViewTopIsNorth { get; set; } = true;
        public bool DoubleClickEdit { get; set; } = true;
        /// <summary>一处批注完成后是否自动继续下一处。<b>这是"本轮绘制"的快照字段</b>：
        /// 功能选择框的值由 <see cref="PanelContinuous"/> 记住并在开始绘制时拷进它，
        /// <b>不写回本设置</b>，所以这里的默认值取 false（快速直绘等入口直接读到它时不会意外连续绘制）。</summary>
        public bool ContinuousAnnotation { get; set; } = false;
        public bool CloudOnly { get; set; } = false;

        // ===== 功能选择框（X_PZ_NOTE）"上次的选择"：面板重开时恢复，让用户不必每次重新点一遍 =====
        /// <summary>面板上次点的外形令牌：「矩形」/「PL线」/「基点」。
        /// <para><b>注意与 <see cref="Shape"/> 的区别</b>：<c>Shape</c> 是云线外形（矩形/菱形/椭圆），
        /// 由「批注设置」窗口维护；本字段是面板那三个按钮的选择。</para>
        /// <para><b>为什么单开一组字段、不复用 <see cref="CloudOnly"/> / <see cref="ContinuousAnnotation"/></b>：
        /// 那一组是"本轮绘制"用的快照，快速直绘（X_PZ_DRAW）读同一份 settings.xml——把面板的选择写进它们，
        /// 快速直绘就会跟着变成"仅绘云线/连续绘制"。所以面板的"上次选择"单独存这几个字段，只被面板读写。</para></summary>
        public string PanelShape { get; set; } = "矩形";
        /// <summary>面板上次是否勾选「连续绘制」。</summary>
        public bool PanelContinuous { get; set; } = false;
        /// <summary>面板上次是否勾选「多对一批注」。</summary>
        public bool PanelMultiMode { get; set; } = false;
        /// <summary>面板上次是否勾选「仅绘云线」。</summary>
        public bool PanelCloudOnly { get; set; } = false;
        public bool SameColors { get; set; } = true;
        public bool LayerAppendDate { get; set; } = true;
        public bool LayerAppendName { get; set; } = false;
        public bool DateBeforeName { get; set; } = true;
        public string Connector { get; set; } = "-";
        public bool Plottable { get; set; } = false;
        public double CheckHeight { get; set; } = 8.0;
        public double AutoTextViewPercent { get; set; } = 5.0; // 字高 ≈ 云线对角线 × 此百分比
        // 批注框内显示内容（批注内容必选，始终显示；其余可选，默认全勾选）
        // 编号不在此列：编号仅用于批注列表与导出/导入匹配，不显示在图内。
        public bool ShowDiscipline { get; set; } = true;
        public bool ShowAuthor { get; set; } = true;
        public bool ShowRole { get; set; } = true;
        public bool ShowDate { get; set; } = true;
        public bool ShowStatus { get; set; } = true;
        public bool ShowDrawingNo { get; set; } = true;
        public bool ArchiveOnCreate { get; set; } = true; // 立即入库：创建/修改批注时写入知识库（KnowledgeStore / knowledge.json）
        /// <summary>输入建议：批注内容输入框是否实时给出补全/续写候选（来源：批注历史记录 / 常用批注语 / 知识库条目）。</summary>
        public bool ContentSuggest { get; set; } = true;

        public AnnotationSettings Clone() => (AnnotationSettings)MemberwiseClone();
    }
}
