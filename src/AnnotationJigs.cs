using System;
using System.Collections.Generic;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
using CadPolyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.Colors;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.EditorInput;
using Gssoft.Gscad.Geometry;
using Gssoft.Gscad.GraphicsInterface;
using CadPolyline = Gssoft.Gscad.DatabaseServices.Polyline;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using CadPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#endif

namespace XAnnotation
{
    internal static class RegionPreviewDrawing
    {
        private static bool _traitsLogged;
        private static bool _opaqueFailedLogged;
        private static bool _fallbackLogged;

        /// <summary>
        /// 预览外观预处理：实心填充 + 不透明。**每个预览 Jig 的 WorldDraw 入口都要先调它。**
        /// <para><b>填充</b>：AutoCAD 调用 WorldDraw 之前会按"再生类型"重置填充类型，再生类型为 StandardDisplay 时
        /// 会被置成 <c>FillNever</c>——这正是 Jig 拖拽期间的状态。显式置 <c>FillAlways</c> 后，
        /// 由 <see cref="DrawWidePolyline"/> 显式绘制的实心带才不会被去掉。</para>
        /// <para><b>不透明</b>（"绘制过程中淡显"的真正原因）：拖拽管线会按 50% 透明度合成
        /// WorldDraw 的输出，所以预览里的云线/引线看起来像蒙了层纱，而落图后走实体渲染就恢复正常。
        /// 抓屏取像素可验证：预览是 (128,0,128)，落图是 (255,0,255)——正好一半；相邻梯形小块重叠处
        /// 还能叠出 192，可见是<b>逐图元</b>按透明度混合，而不是整帧统一滤镜。既然在 traits 这一层，
        /// 就能就地关掉：<c>SubEntityTraits.Transparency</c> 显式置为不透明。
        /// 每帧都要置——AutoCAD 在每次 WorldDraw 之前重新初始化 subentity traits。</para>
        /// </summary>
        internal static void PreparePreviewTraits(WorldDraw draw)
        {
            var traits = draw.SubEntityTraits;
            traits.FillType = FillType.FillAlways;

            // 记录宿主给的初始透明度，只留一次：这条日志是"淡显"诊断的直接证据。
            if (!_traitsLogged)
            {
                _traitsLogged = true;
                try
                {
                    var old = traits.Transparency;
                    PluginLog.Info(
                        "PreviewTraits",
                        "拖拽预览的初始透明度: ByLayer=" + old.IsByLayer +
                        " ByBlock=" + old.IsByBlock + " Alpha=" + old.Alpha);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning("PreviewTraits", "读预览初始透明度失败：" + ex.Message);
                }
            }

            try
            {
                traits.Transparency = new Transparency((byte)0);   // Alpha=0 即完全不透明
            }
            catch (Exception ex)
            {
                // 老版本/非 AutoCAD 平台可能没有这个属性：只留一次日志，不影响绘制本身。
                if (!_opaqueFailedLogged)
                {
                    _opaqueFailedLogged = true;
                    PluginLog.Warning("PreviewTraits", "把拖拽预览置为不透明失败（保持宿主默认）：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 云线每段弧的采样数。云线弧瓣尺度很小（半径 ≈ 2mm 级）、宽度更小，
        /// 3 段采样即可让外轮廓看不出折线感，同时把每帧的填充调用量压在
        /// 「顶点数 × ArcSamples」这一量级（自适应模式下顶点约 160 → 约 480 次/帧）。
        /// </summary>
        private const int ArcSamples = 3;

        /// <summary>
        /// 在 Jig 里绘制"宽带状"多段线（云线、引线）。
        /// <para><b>为什么不能只靠 Geometry.Draw(polyline)</b>：宽多段线的带宽靠实体的
        /// <c>ConstantWidth</c> / 顶点宽度表达，而 Jig 走的是"世界绘制"管线，世界绘制对多段线只按中心线出图，
        /// 不参与实体的宽度/填充渲染——于是拖拽预览里只剩一条细描边，看起来"淡显/半透明"，
        /// 而落图（走数据库实体渲染）宽窄正常。两者因此对不上。</para>
        /// <para><b>做法：逐段生成梯形填充。</b>沿中心线把多段线按顶点切段（弧段先采样），
        /// 每小段按其两端宽度沿局部法向偏移出左右边界，构成一个<b>小凸多边形</b>，
        /// 再用 <c>Geometry.Polygon</c> 填充。逐段而非整体成环，是因为云线是锯齿状的：
        /// 若把整条云线沿法向整体偏移成单一闭合环，弧瓣外凸会互相穿插、环严重自交，
        /// <c>Geometry.Polygon</c> 将拒绝填充 —— 表现就是"云线一根都不显示"。</para>
        /// <para><b>宽度口径</b>：等宽样式取 <paramref name="width"/>（= ConstantWidth）；
        /// 渐变样式取顶点宽度（逐段 0 → 云线线宽），由 <see cref="DrawTaperedPolyline"/> 处理。</para>
        /// </summary>
        internal static void DrawWidePolyline(WorldDraw draw, CadPolyline polyline, double width)
        {
            if (polyline == null) return;

            // 必须先把实体自身的颜色刷进 SubEntityTraits：
            // Geometry.Draw(entity) 会带实体颜色，而 Geometry.Polygon 是"裸"几何基元，
            // **只认 SubEntityTraits 的当前色**，不会继承实体颜色。不刷颜色，拖拽预览里的
            // 云线/引线就会画成当前色（通常近白/随层），与落图后的批注颜色对不上。
            ApplyColorToTraits(draw, polyline);

            try
            {
                DrawPolylineBand(draw, polyline, width);
            }
            catch (Exception ex)
            {
                // 填充路径出任何岔子，至少把中心线画出来，避免"整条云线凭空消失"。
                // 但降级后画面会变成一条细线，光看屏幕分不清是"没填充"还是"抛了异常"，故留一次日志。
                if (!_fallbackLogged)
                {
                    _fallbackLogged = true;
                    PluginLog.Warning("DrawWidePolyline", "实心带绘制失败，已退回中心线：" + ex.Message);
                }
                try { draw.Geometry.Draw(polyline); } catch { /* 预览对象，失败即放弃本帧 */ }
            }
        }

        /// <summary>把多段线自身的随层色（ByAci）写进世界绘制的 SubEntityTraits，保证预览与落图同色。</summary>
        private static void ApplyColorToTraits(WorldDraw draw, CadPolyline polyline)
        {
            try
            {
                var color = polyline.Color;
                if (color != null && color.ColorMethod == ColorMethod.ByAci)
                {
                    draw.SubEntityTraits.Color = color.ColorIndex;
                }
            }
            catch
            {
                // 颜色读不出来（实体已释放等）时保持当前色，不影响绘制本身。
            }
        }

        /// <summary>
        /// 安全读多段线的全局线宽。锥形（渐变）或逐段宽度不一致的多段线读
        /// <c>ConstantWidth</c> 会抛 eInvalidInput —— 与 AnnotationService.SafeConstantWidth 同一口径。
        /// </summary>
        internal static double SafePolylineWidth(CadPolyline polyline)
        {
            if (polyline == null) return 0;
            try { return polyline.ConstantWidth; } catch { return 0; }
        }

        /// <summary>逐段梯形填充：宽度在每段内由 startWidth 线性过渡到 endWidth。</summary>
        private static void DrawPolylineBand(WorldDraw draw, CadPolyline polyline, double width)
        {
            var n = polyline.NumberOfVertices;
            if (n < 2)
            {
                draw.Geometry.Draw(polyline);
                return;
            }

            var closed = polyline.Closed;
            var segments = closed ? n : n - 1;
            var tapered = width <= 1e-9 && HasVertexWidth(polyline);

            // 既无全局宽度、又无顶点宽度：纯中心线，WorldDraw 本来就能画对，直接走原路。
            if (width <= 1e-9 && !tapered)
            {
                draw.Geometry.Draw(polyline);
                return;
            }

            for (var i = 0; i < segments; i++)
            {
                var a = polyline.GetPoint3dAt(i);
                var b = polyline.GetPoint3dAt((i + 1) % n);
                var bulge = polyline.GetBulgeAt(i);

                double startWidth, endWidth;
                if (tapered)
                {
                    // 渐变：一段的"起/止宽度"是写在**该段起始顶点**上的一对值
                    // （DXF LWPOLYLINE 的 40/41，作用于从此顶点开始的那一段）。
                    // 云线每个顶点写的是 (0, 线宽)，于是逐瓣 起笔尖 → 收笔粗。
                    startWidth = StartWidthAt(polyline, i);
                    endWidth = EndWidthAt(polyline, i);
                    if (startWidth <= 1e-12 && endWidth <= 1e-12) continue;
                }
                else
                {
                    startWidth = width;
                    endWidth = width;
                }

                foreach (var run in BuildSegmentRuns(a, b, bulge, startWidth, endWidth))
                {
                    if (run.Count >= 3) draw.Geometry.Polygon(run);
                }
            }
        }

        /// <summary>渐变样式：带宽写在顶点宽度里（<c>GetStartWidthAt</c>/<c>GetEndWidthAt</c>），全局宽度为 0。</summary>
        private static bool HasVertexWidth(CadPolyline polyline)
        {
            var n = polyline.NumberOfVertices;
            for (var i = 0; i < n; i++)
            {
                if (StartWidthAt(polyline, i) > 1e-12 || EndWidthAt(polyline, i) > 1e-12) return true;
            }
            return false;
        }

        /// <summary>安全读某段的起始宽度（锥形多段线读宽度可能抛 eInvalidInput，失败按 0 处理）。</summary>
        private static double StartWidthAt(CadPolyline polyline, int index)
        {
            try { return polyline.GetStartWidthAt(index); }
            catch { return 0; }
        }

        /// <summary>安全读某段的结束宽度（锥形多段线读宽度可能抛 eInvalidInput，失败按 0 处理）。</summary>
        private static double EndWidthAt(CadPolyline polyline, int index)
        {
            try { return polyline.GetEndWidthAt(index); }
            catch { return 0; }
        }

        /// <summary>把一段（直线或弧）按两端宽度展开成若干小梯形多边形。</summary>
        private static IEnumerable<Point3dCollection> BuildSegmentRuns(
            Point3d a,
            Point3d b,
            double bulge,
            double startWidth,
            double endWidth)
        {
            var samples = SampleSegment(a, b, bulge, Math.Abs(bulge) > 1e-9 ? ArcSamples : 1);
            if (samples.Count < 2)
            {
                // 退化段（两端点重合）：用一个菱形补个点，避免尖端缺角。
                yield return BuildJointPolygon(a, Math.Max(startWidth, endWidth) / 2.0);
                yield break;
            }

            var last = samples.Count - 1;
            for (var i = 0; i < last; i++)
            {
                var t0 = i / (double)last;
                var t1 = (i + 1) / (double)last;
                var w0 = startWidth + (endWidth - startWidth) * t0;
                var w1 = startWidth + (endWidth - startWidth) * t1;

                var p0 = samples[i];
                var p1 = samples[i + 1];

                // 半宽；两端都为 0 时该小段没有面积，跳过（渐变尖端附近）。
                var h0 = w0 / 2.0;
                var h1 = w1 / 2.0;
                if (h0 <= 1e-12 && h1 <= 1e-12) continue;

                var dir = new Vector3d(p1.X - p0.X, p1.Y - p0.Y, 0);
                if (dir.Length < 1e-12) continue;
                dir = dir.GetNormal();
                var normal = new Vector3d(-dir.Y, dir.X, 0);

                var left0 = new Point3d(p0.X + normal.X * h0, p0.Y + normal.Y * h0, p0.Z);
                var left1 = new Point3d(p1.X + normal.X * h1, p1.Y + normal.Y * h1, p1.Z);
                var right1 = new Point3d(p1.X - normal.X * h1, p1.Y - normal.Y * h1, p1.Z);
                var right0 = new Point3d(p0.X - normal.X * h0, p0.Y - normal.Y * h0, p0.Z);

                var poly = new Point3dCollection();

                // 渐变（锥形）样式下，每段起笔宽度正好是 0 —— 此时 p0±0 是**同一个点**。
                // 把这个"退化成四边形"的三角形交给 Geometry.Polygon，宿主会直接抛
                // eInvalidInput（整条带都会因此退回细中心线），所以这里显式收敛成三边形。
                if (h0 <= 1e-12)
                {
                    poly.Add(p1);      // 尖端
                    poly.Add(left1);
                    poly.Add(right1);
                }
                else if (h1 <= 1e-12)
                {
                    poly.Add(p0);      // 尖端
                    poly.Add(left0);
                    poly.Add(right0);
                }
                else
                {
                    poly.Add(left0);
                    poly.Add(left1);
                    poly.Add(right1);
                    poly.Add(right0);
                }

                yield return poly;
            }
        }

        /// <summary>退化段的补点多边形（小十字/菱形），防止渐变尖端或零长段留洞。</summary>
        private static Point3dCollection BuildJointPolygon(Point3d center, double half)
        {
            var h = Math.Max(half, 1e-6);
            var poly = new Point3dCollection();
            poly.Add(new Point3d(center.X - h, center.Y, center.Z));
            poly.Add(new Point3d(center.X, center.Y - h, center.Z));
            poly.Add(new Point3d(center.X + h, center.Y, center.Z));
            poly.Add(new Point3d(center.X, center.Y + h, center.Z));
            return poly;
        }

        /// <summary>把一段中心线（含 bulge 弧）采样成点列，含首尾端点。</summary>
        private static List<Point3d> SampleSegment(Point3d a, Point3d b, double bulge, int steps)
        {
            var result = new List<Point3d> { a };
            if (steps <= 1) { result.Add(b); return result; }

            var chord = new Vector3d(b.X - a.X, b.Y - a.Y, 0);
            var chordLen = chord.Length;
            if (chordLen < 1e-12) return result;

            var theta = 4.0 * Math.Atan(Math.Abs(bulge));      // 圆心角
            var sign = Math.Sign(bulge);
            var radius = chordLen / (2.0 * Math.Sin(theta / 2.0));

            // 弦中点 + 法向偏移 = 圆心。
            var mid = new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, a.Z);
            var normal = new Vector3d(-chord.Y / chordLen, chord.X / chordLen, 0);
            var offset = radius * Math.Cos(theta / 2.0);
            var center = new Point3d(mid.X + normal.X * offset * sign, mid.Y + normal.Y * offset * sign, a.Z);

            var startAngle = Math.Atan2(a.Y - center.Y, a.X - center.X);
            var sweep = theta * sign;

            for (var i = 1; i < steps; i++)
            {
                var ang = startAngle + sweep * i / (double)steps;
                result.Add(new Point3d(
                    center.X + radius * Math.Cos(ang),
                    center.Y + radius * Math.Sin(ang),
                    a.Z));
            }

            result.Add(b);
            return result;
        }

        /// <summary>重绘"已经确认过的"云线范围（多对一/仅绘云线时给用户看进度）。
        /// <para><paramref name="doc"/> 只用来读 <see cref="DrawingScale"/> 的缓存值（纯静态字段，不碰数据库），
        /// 保证历史云线预览与最终落图用同一套图面比例。</para>
        /// <para><b>本方法在 WorldDraw 里调用，绝不能访问数据库或编辑器</b>：原先每画一条历史云线就调一次
        /// <c>OrientCloudBulgesForView</c>（内含 <c>Editor.GetCurrentView()</c>），多对一时就是"每帧 × 每条"
        /// 的编辑器访问量，AutoCAD 会直接丢弃这些帧——表现就是多对一拖拽时预览闪烁/卡顿。
        /// 现在弧瓣朝向由调用方在拖拽开始前问一次，通过 <paramref name="bulgeSign"/> 传进来。</para></summary>
        internal static void DrawHistory(
            WorldDraw draw,
            Document doc,
            double bulgeSign,
            Matrix3d ucsToWcs,
            Matrix3d wcsToUcs,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds)
        {
            for (var i = 0; i < firsts.Count && i < seconds.Count; i++)
            {
                var a = firsts[i].TransformBy(wcsToUcs);
                var b = seconds[i].TransformBy(wcsToUcs);
                var diagonal = Math.Sqrt(
                    Math.Pow(b.X - a.X, 2) +
                    Math.Pow(b.Y - a.Y, 2));
                // 对角线为 0 会让 ResolveEffectiveSettings 去问视图——拖拽期间不允许，直接跳过这一条。
                if (diagonal <= 0) continue;
                var settings = AnnotationService.ResolveEffectiveSettings(
                    doc,
                    source,
                    null,
                    diagonal);
                using (var cloud = AnnotationService.BuildCloud(
                    RegionPreviewJig.ToMin(a, b),
                    RegionPreviewJig.ToMax(a, b),
                    settings))
                {
                    AnnotationService.ApplyPreviewAppearance(
                        cloud,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    cloud.Elevation = a.Z;
                    cloud.TransformBy(ucsToWcs);
                    // 用预先算好的翻转量，WorldDraw 里不再问视图。
                    AnnotationService.OrientCloudBulges(cloud, bulgeSign);
                    // 世界绘制不带带宽，须显式展开成实心带（否则历史云线同拖拽预览一样淡显）。
                    DrawWidePolyline(draw, cloud, SafePolylineWidth(cloud));
                }
            }
        }
    }

    /// <summary>第一个角点的选点 Jig：只用十字光标取点，不画任何图形。
    /// <para><b>它的存在意义是"可中止"</b>：CAD 原生的 <c>Editor.GetPoint</c> 是个黑盒，面板改绘制选项时
    /// 没有任何办法让它退出；换成 Jig 之后，<see cref="InteractionAbort"/> 一置位就能在 Sampler 里干净取消，
    /// 本轮随即按新选项重开（面板"勾选即生效、不必再点外形"就靠这个）。</para>
    /// <para>坐标口径与 <see cref="RegionPreviewJig"/> 一致：<c>AcquirePoint</c> 拿到的点按 WCS 使用。</para></summary>
    internal sealed class FirstCornerPromptJig : DrawJig
    {
        private readonly string _prompt;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current { get { return _current; } }

        /// <summary>本轮是否因"面板改了绘制选项"被主动中止（区别于用户自己按 Esc / 回车）。</summary>
        public bool Aborted { get; private set; }

        public FirstCornerPromptJig(string prompt)
        {
            _prompt = prompt;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            if (InteractionAbort.Requested)
            {
                Aborted = true;
                return SamplerStatus.Cancel;
            }

            var options = new JigPromptPointOptions(_prompt)
            {
                Cursor = CursorType.Crosshair
            };
            var result = prompts.AcquirePoint(options);

            // 回车/空格与 Esc 同义：都是取消本轮（与原来的 Editor.GetPoint 行为一致）。
            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (!_hasSample)
            {
                // 首帧必须回 OK：回 NoChange 会让这一帧的 WorldDraw 不被调用。
                _hasSample = true;
                _current = result.Value;
                return SamplerStatus.OK;
            }

            if (result.Value.DistanceTo(_current) < 1e-9)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            // 只取点、不画东西：十字光标就够了。
            return true;
        }
    }

    /// <summary>多对一第一角点预览：显示全部已确认区域，并允许回车结束。</summary>
    internal sealed class RegionFirstPointPreviewJig : DrawJig
    {
        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs;
        private readonly Matrix3d _wcsToUcs;
        private readonly AnnotationSettings _source;
        private readonly List<Point3d> _firsts;
        private readonly List<Point3d> _seconds;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;
        public bool FinishRequested { get; private set; }
        /// <summary>本轮是否因"面板改了绘制选项"被主动中止（区别于用户回车结束）。</summary>
        public bool Aborted { get; private set; }

        /// <summary>云线弧瓣朝向的翻转量（+1 / -1）：拖拽开始前问一次视图就定死，
        /// 拖拽期间（WorldDraw）不再访问编辑器。见 <see cref="AnnotationService.CloudBulgeSign"/>。</summary>
        private readonly double _bulgeSign;

        public RegionFirstPointPreviewJig(
            Document doc,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds)
        {
            _doc = doc;
            _source = source;
            _firsts = firsts?.ToList() ?? new List<Point3d>();
            _seconds = seconds?.ToList() ?? new List<Point3d>();
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
            _bulgeSign = AnnotationService.CloudBulgeSign(doc, Vector3d.ZAxis.TransformBy(_ucsToWcs));
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            // 面板改了绘制选项 / 换了外形：本轮按新选项重开，这里立刻退出（见 InteractionAbort）。
            if (InteractionAbort.Requested)
            {
                Aborted = true;
                return SamplerStatus.Cancel;
            }

            var options = new JigPromptPointOptions(
                "\n指定下一个云线范围第一个角点，回车/空格结束: ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NullResponseAccepted
            };
            var result = prompts.AcquirePoint(options);

            if (result.Status == PromptStatus.None)
            {
                FinishRequested = true;
                return SamplerStatus.Cancel;
            }

            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            RegionPreviewDrawing.PreparePreviewTraits(draw);
            RegionPreviewDrawing.DrawHistory(
                draw,
                _doc,
                _bulgeSign,
                _ucsToWcs,
                _wcsToUcs,
                _source,
                _firsts,
                _seconds);
            return true;
        }
    }

    /// <summary>拖拽预览：按当前区域尺寸解析最终参数，在 UCS 中构造并变换到 WCS。</summary>
    internal sealed class RegionPreviewJig : DrawJig
    {
        private readonly Document _doc;private readonly Matrix3d _ucsToWcs,_wcsToUcs;private readonly Point3d _first;private readonly AnnotationSettings _source;private readonly List<Point3d> _historyFirsts,_historySeconds;private Point3d _current;private bool _hasSample;
        /// <summary>弧瓣朝向的翻转量（+1 / -1）：构造阶段问一次视图。
        /// <b>WorldDraw 期间不允许访问编辑器</b>（见 <see cref="AnnotationService.CloudBulgeSign"/>），
        /// 原先每帧调 <c>OrientCloudBulgesForView</c> 正是拖拽预览卡顿的来源。</summary>
        private readonly double _bulgeSign;
        public Point3d Current=>_current;
        /// <summary>本轮是否因"面板改了绘制选项"被主动中止（区别于用户自己按 Esc）。</summary>
        public bool Aborted{get;private set;}
        public RegionPreviewJig(Document doc,Point3d first,AnnotationSettings source,IList<Point3d> historyFirsts=null,IList<Point3d> historySeconds=null){_doc=doc;_ucsToWcs=AnnotationService.GetUcsMatrix(doc);_wcsToUcs=_ucsToWcs.Inverse();_first=first;_current=first;_source=source;_historyFirsts=historyFirsts?.ToList()??new List<Point3d>();_historySeconds=historySeconds?.ToList()??new List<Point3d>();_bulgeSign=AnnotationService.CloudBulgeSign(doc,Vector3d.ZAxis.TransformBy(_ucsToWcs));}
        // 保留 BasePoint 以支持相对坐标和对象捕捉，但使用普通十字光标，避免 CAD 额外画出角点对角虚线。
        // _source 是"正在跑的那一轮"的设置实例本身（不是副本）：面板改了外形/选项会原地写进它，
        // WorldDraw 每帧重新解析，所以拖拽预览能立刻跟着变——不会出现"预览是矩形、落图是菱形"。
        protected override SamplerStatus Sampler(JigPrompts prompts){if(InteractionAbort.Requested){Aborted=true;return SamplerStatus.Cancel;}var o=new JigPromptPointOptions("\n指定批注范围另一个角点: "){UseBasePoint=true,BasePoint=_first,Cursor=CursorType.Crosshair};var r=prompts.AcquirePoint(o);if(r.Status!=PromptStatus.OK)return SamplerStatus.Cancel;if(!_hasSample){_hasSample=true;_current=r.Value;return SamplerStatus.OK;}if(r.Value.DistanceTo(_current)<1e-8)return SamplerStatus.NoChange;_current=r.Value;return SamplerStatus.OK;}
        protected override bool WorldDraw(WorldDraw draw)
        {
            RegionPreviewDrawing.PreparePreviewTraits(draw);
            RegionPreviewDrawing.DrawHistory(draw,_doc,_bulgeSign,_ucsToWcs,_wcsToUcs,_source,_historyFirsts,_historySeconds);
            var first=_first.TransformBy(_wcsToUcs);var current=_current.TransformBy(_wcsToUcs);var w=Math.Abs(current.X-first.X);var h=Math.Abs(current.Y-first.Y);if(w<1e-8||h<1e-8)return true;
            var settings=AnnotationService.ResolveEffectiveSettings(_doc,_source,new AnnotationData(),Math.Sqrt(w*w+h*h));
            using(var cloud=AnnotationService.BuildCloud(ToMin(first,current),ToMax(first,current),settings)){AnnotationService.ApplyPreviewAppearance(cloud,settings,settings.CloudColor,"cloud");cloud.Elevation=first.Z;cloud.TransformBy(_ucsToWcs);AnnotationService.OrientCloudBulges(cloud,_bulgeSign);RegionPreviewDrawing.DrawWidePolyline(draw,cloud,RegionPreviewDrawing.SafePolylineWidth(cloud));
                // 同一份云线再交给瞬态层画一遍（不透明）：Jig 的拖拽管线会把上面那份按 50% 与背景合成，
                // 而瞬态图形不走那条管线，盖在上面就是全色。Jig 的那份保留作兜底。
                if(PreviewTransient.Enabled) PreviewTransient.Replace((CadPolyline)cloud.Clone());}return true;
        }
        internal static Point2d ToMin(Point3d a,Point3d b)=>new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));internal static Point2d ToMax(Point3d a,Point3d b)=>new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    }

    /// <summary>放置阶段的几何来源，决定预览里画什么云线、引线从哪儿起。
    /// <list type="bullet">
    /// <item><see cref="Region"/>：矩形/菱形/椭圆——按框选范围建云线，引线接在云线轮廓（或包围盒角）上。</item>
    /// <item><see cref="Polygon"/>：PL 折线——折点即轮廓，引线必须落在折线云线轮廓上。</item>
    /// <item><see cref="MultiRegion"/>：多对一——多条云线，各自按自身尺寸解析参数。</item>
    /// <item><see cref="BasePoint"/>：基点——不画云线，引线从用户指定的基点直连到文字框。</item>
    /// </list></summary>
    internal enum PlacementGeometryKind { Region, Polygon, MultiRegion, BasePoint }

    /// <summary>完整批注放置预览：真实云线 + 文字占位框 + 全部引线，批注框随光标移动。
    /// <para><b>为什么要预构建</b>：WorldDraw 是拖拽回调，期间不允许访问数据库或编辑器。
    /// 原实现是在 WorldDraw 里现算：MeasureTextBox 要开事务读文字样式表、OrientCloudBulgesForView
    /// 要问 Editor.GetCurrentView——在拖拽期间碰这些，AutoCAD 会把这一帧 WorldDraw 的结果整体丢弃，
    /// 表现就是"定位批注框时云线、批注框、引线一个都不显示"。
    /// 现在所有与光标无关的几何（含云线、参数解析、文字框尺寸）都在构造阶段一次算完，
    /// 拖拽期间只做纯绘制。</para>
    /// <para><b>首帧</b>：拖拽起点正落在上一个角点上，若首次采样按"位置没变"返回 NoChange，
    /// WorldDraw 不会被调用，进入本步骤后会先看到一片空白、要动一下鼠标才出现预览，故第一帧强制 OK。</para></summary>
    internal sealed class PlacementPreviewJig : DrawJig, IDisposable
    {
        /// <summary>一条云线的预览部件：已变换到 WCS 的云线，以及解析引线锚点所需的定位信息。</summary>
        private sealed class CloudPart
        {
            public CadPolyline Cloud;
            public AnnotationSettings Settings;
            public Point2d MinUcs, MaxUcs;   // 矩形/菱形/椭圆：锚点在 UCS 包围盒内判定
            public bool AnchorOnOutline;     // 椭圆/菱形：包围盒四角不在轮廓上，锚点须取轮廓最近点
        }

        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs, _wcsToUcs;
        private readonly AnnotationSettings _settings;
        private readonly List<CloudPart> _parts = new List<CloudPart>();
        /// <summary>「基点」模式：引线起点固定在它上面（UCS 局部坐标）；其它模式不用。</summary>
        private readonly Point2d _fixedAnchorUcs;
        /// <summary>「基点」模式：基点标识（圆 + 十字）的半径，与落图同一口径。</summary>
        private readonly double _markerRadius;
        private readonly PlacementGeometryKind _kind;
        private readonly double _boxWidth, _boxHeight;
        private readonly short _boxColor;
        private readonly MText _text;          // 真实批注内容的预览文字，生命周期归 PromptPlacement
        private readonly double _textMargin;   // 文字相对框左下角的内边距，与落图同源
        private Point3d _current;
        private bool _hasSample;
        private bool _drawFailureLogged;

        public Point3d Current => _current;

        /// <summary>本轮是否因"面板改了绘制选项"被主动中止（区别于用户自己按 Esc）。</summary>
        public bool Aborted { get; private set; }

        public PlacementPreviewJig(
            Document doc,
            AnnotationSettings settings,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds,
            IList<Point3d> polygon,
            Point3d initial,
            PlacementGeometryKind kind,
            AnnotationService.PreviewTextBox box,
            Point3d fixedAnchor = default(Point3d))
        {
            _doc = doc;
            _settings = settings;
            _boxWidth = box.Width;
            _boxHeight = box.Height;
            _boxColor = settings.SameColors ? settings.CloudColor : settings.BoxColor;
            _current = initial;
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
            _kind = kind;
            // 预览文字：模板在 UCS 原点量好后一次性转到 WCS（旋转与法向一并带过去），
            // 之后每帧只改 Location，不做累积变换，所以文字不会随鼠标位移漂移或转圈。
            _text = box.Text;
            _textMargin = box.Margin;
            if (_text != null) _text.TransformBy(_ucsToWcs);

            var src = source ?? settings;
            if (kind == PlacementGeometryKind.Polygon)
            {
                AddPolygonPart(polygon, settings);
            }
            else if (kind == PlacementGeometryKind.BasePoint)
            {
                // 基点模式没有云线：只记下引线起点，WorldDraw 里每帧从它画到最近的框角。
                var localAnchor = fixedAnchor.TransformBy(_wcsToUcs);
                _fixedAnchorUcs = new Point2d(localAnchor.X, localAnchor.Y);
                // 基点标识的半径与落图（CreateBasePoint）用同一口径，拖放时看到的尺寸就是最终尺寸。
                _markerRadius = AnnotationService.BasePointMarkerRadius(settings, _boxHeight);
            }
            else
            {
                for (var i = 0; i < (firsts?.Count ?? 0) && i < (seconds?.Count ?? 0); i++)
                {
                    AddRegionPart(src, settings, firsts[i], seconds[i], kind);
                }
            }
        }

        /// <summary>PL 折点云线：折点即轮廓，锚点必须落在轮廓上（不能退化成包围盒四角）。</summary>
        private void AddPolygonPart(IList<Point3d> polygon, AnnotationSettings settings)
        {
            if (polygon == null || polygon.Count < 3) return;
            var points = polygon.Select(p => p.TransformBy(_wcsToUcs)).ToList();
            var min = new Point2d(points.Min(p => p.X), points.Min(p => p.Y));
            var max = new Point2d(points.Max(p => p.X), points.Max(p => p.Y));
            var cloud = AnnotationService.BuildPolygonCloud(
                points.Select(p => new Point2d(p.X, p.Y)).ToList(),
                settings);
            cloud.Elevation = points[0].Z;
            AddPart(cloud, settings, min, max, true);
        }

        /// <summary>框选矩形/菱形/椭圆云线：按当前区域尺寸解析最终参数，在 UCS 中成型后再变换到 WCS。</summary>
        private void AddRegionPart(AnnotationSettings source, AnnotationSettings settings, Point3d first, Point3d second, PlacementGeometryKind kind)
        {
            var a = first.TransformBy(_wcsToUcs);
            var b = second.TransformBy(_wcsToUcs);
            var min = RegionPreviewJig.ToMin(a, b);
            var max = RegionPreviewJig.ToMax(a, b);
            // 多对一：每条云线按自身对角线重新解析字高/半径，与落图口径一致。
            var part = kind == PlacementGeometryKind.MultiRegion
                ? AnnotationService.SettingsForRegion(_doc, source, first, second)
                : settings;
            var cloud = AnnotationService.BuildCloud(min, max, part);
            cloud.Elevation = a.Z;
            AddPart(cloud, part, min, max, part.Shape == "椭圆" || part.Shape == "菱形");
        }

        /// <summary>成形 + 着色 + 转到 WCS + 按视图定向弧瓣：全部在拖拽开始前完成。</summary>
        private void AddPart(CadPolyline cloud, AnnotationSettings settings, Point2d minUcs, Point2d maxUcs, bool anchorOnOutline)
        {
            AnnotationService.ApplyPreviewAppearance(cloud, settings, settings.CloudColor, "cloud");
            cloud.TransformBy(_ucsToWcs);
            AnnotationService.OrientCloudBulgesForView(_doc, cloud);
            _parts.Add(new CloudPart
            {
                Cloud = cloud,
                Settings = settings,
                MinUcs = minUcs,
                MaxUcs = maxUcs,
                AnchorOnOutline = anchorOnOutline
            });
        }

        public void Dispose()
        {
            foreach (var part in _parts)
            {
                try { part.Cloud?.Dispose(); } catch { /* 预览对象，释放失败可忽略 */ }
            }
            _parts.Clear();
        }

        /// <summary>只记第一条失败：拖拽期间每帧都会重画，逐帧写日志会拖垮交互。</summary>
        private void LogDrawFailure(string role, System.Exception ex)
        {
            if (_drawFailureLogged) return;
            _drawFailureLogged = true;
            PluginLog.Warning("PlacementPreviewJig", role + ": " + ex.Message);
        }
        // 放置阶段由 WorldDraw 自绘云线/框/引线，故用十字光标；RubberBand 会额外拉出一条从原点的对角虚线。
        // 首帧必须回 OK：拖拽起点正落在上一个角点上，按"位置没变"回 NoChange 的话 WorldDraw 不会被调用，
        // 进入本步骤后会先看到一片空白、要动一下鼠标才出现预览。
        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            // 面板改了绘制选项 / 换了外形：本轮按新选项重开，这里立刻退出（见 InteractionAbort）。
            if (InteractionAbort.Requested)
            {
                Aborted = true;
                return SamplerStatus.Cancel;
            }

            var options = new JigPromptPointOptions("\n指定批注框位置: ") { Cursor = CursorType.Crosshair };
            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            if (!_hasSample)
            {
                _hasSample = true;
                _current = result.Value;
                return SamplerStatus.OK;
            }
            if (result.Value.DistanceTo(_current) < 1e-9) return SamplerStatus.NoChange;
            _current = result.Value;
            return SamplerStatus.OK;
        }

        /// <summary>拖拽期间唯一入口：只画，不碰数据库和编辑器。
        /// 云线在构造阶段就备好了；批注框、引线与文字每帧按光标位置重算，所以框始终跟着鼠标走。
        /// 每个部件各自 try：某一项画不出来也不会连累其它项，更不会让整帧预览一起消失。</summary>
        protected override bool WorldDraw(WorldDraw draw)
        {
            // 拖拽管线默认不填充、且按 50% 透明度合成（看起来就是"淡显"）——见 PreparePreviewTraits 的说明。
            RegionPreviewDrawing.PreparePreviewTraits(draw);

            // 每帧收集"要额外交给瞬态层画一份不透明副本"的实体：Jig 的 drag image 会被按 50% 合成，
            // 瞬态层不会，盖上去就恢复全色。Jig 自己那份照样画，作为兜底。
            var overlays = PreviewTransient.Enabled ? new List<Entity>() : null;

            var localText = _current.TransformBy(_wcsToUcs);
            var boxCorners = BuildBoxCorners(localText);

            foreach (var part in _parts)
            {
                try
                {
                    // 宽多段线在世界绘制里不出带宽，须显式展开成实心带，否则拖拽时看着是细线/淡显。
                    RegionPreviewDrawing.DrawWidePolyline(draw, part.Cloud, RegionPreviewDrawing.SafePolylineWidth(part.Cloud));
                    if (overlays != null) overlays.Add((CadPolyline)part.Cloud.Clone());
                }
                catch (System.Exception ex) { LogDrawFailure("cloud", ex); }

                try
                {
                    var anchor = ResolveAnchorUcs(part, localText);
                    using (var leader = new CadPolyline())
                    {
                        leader.AddVertexAt(0, anchor, 0, 0, 0);
                        leader.AddVertexAt(1, boxCorners.OrderBy(c => c.GetDistanceTo(anchor)).First(), 0, 0, 0);
                        leader.Elevation = localText.Z;
                        AnnotationService.ApplyPreviewAppearance(leader, part.Settings, part.Settings.LeaderColor, "leader");
                        leader.TransformBy(_ucsToWcs);
                        RegionPreviewDrawing.DrawWidePolyline(draw, leader, RegionPreviewDrawing.SafePolylineWidth(leader));
                        if (overlays != null) overlays.Add((CadPolyline)leader.Clone());
                    }
                }
                catch (System.Exception ex) { LogDrawFailure("leader", ex); }
            }

            // 「基点」模式：没有云线，只有一条从基点连到最近框角的直线——与落图（CreateBasePoint）同一口径。
            if (_kind == PlacementGeometryKind.BasePoint)
            {
                try
                {
                    using (var leader = new CadPolyline())
                    {
                        leader.AddVertexAt(0, _fixedAnchorUcs, 0, 0, 0);
                        leader.AddVertexAt(1, boxCorners.OrderBy(c => c.GetDistanceTo(_fixedAnchorUcs)).First(), 0, 0, 0);
                        leader.Elevation = localText.Z;
                        AnnotationService.ApplyPreviewAppearance(leader, _settings, _settings.LeaderColor, "leader");
                        leader.TransformBy(_ucsToWcs);
                        RegionPreviewDrawing.DrawWidePolyline(draw, leader, RegionPreviewDrawing.SafePolylineWidth(leader));
                        if (overlays != null) overlays.Add((CadPolyline)leader.Clone());
                    }
                }
                catch (System.Exception ex) { LogDrawFailure("baseLeader", ex); }

                // 基点标识（圆 + 十字目标符号）：与落图 CreateBasePoint 用同一个 BuildBasePointMarker，
                // 拖放文字框的时候就能看到"这条引线的起点到底在图上哪个点"。
                try
                {
                    foreach (var markerPart in AnnotationService.BuildBasePointMarker(_fixedAnchorUcs, localText.Z, _markerRadius))
                    {
                        using (markerPart)
                        {
                            var marker = markerPart as CadPolyline;
                            if (marker == null) continue;
                            AnnotationService.ApplyPreviewAppearance(marker, _settings, _settings.LeaderColor, "leader");
                            marker.TransformBy(_ucsToWcs);
                            RegionPreviewDrawing.DrawWidePolyline(draw, marker, RegionPreviewDrawing.SafePolylineWidth(marker));
                            if (overlays != null) overlays.Add((CadPolyline)marker.Clone());
                        }
                    }
                }
                catch (System.Exception ex) { LogDrawFailure("baseMarker", ex); }
            }

            try
            {
                using (var box = AnnotationService.BuildBox(localText, _boxWidth, _boxHeight))
                {
                    box.Elevation = localText.Z;
                    AnnotationService.ApplyPreviewAppearance(box, _settings, _boxColor, "box");
                    box.TransformBy(_ucsToWcs);
                    draw.Geometry.Draw(box);
                    if (overlays != null) overlays.Add((CadPolyline)box.Clone());
                }
            }
            catch (System.Exception ex) { LogDrawFailure("box", ex); }

            // 批注内容：位置取"框左下角 + 边距"，与 Create 里 text.Location 的算法完全一致
            // （Create 先设 UCS 位置再 TransformBy(ucsToWcs)，这里是同一变换作用于同一个点，等价）。
            try
            {
                if (_text != null)
                {
                    _text.Location = new Point3d(
                        localText.X + _textMargin,
                        localText.Y + _textMargin,
                        localText.Z).TransformBy(_ucsToWcs);
                    draw.Geometry.Draw(_text);
                }
            }
            catch (System.Exception ex) { LogDrawFailure("text", ex); }

            // 统一把这一帧的副本交给瞬态层（不透明）；上一帧的由 Replace 内部擦除释放。
            if (overlays != null) PreviewTransient.Replace(overlays.ToArray());

            return true;
        }

        /// <summary>占位框四角（UCS）：左下角跟随光标，与正式创建 BuildBox(localText, …) 的口径一致。</summary>
        private Point2d[] BuildBoxCorners(Point3d localText) => new[]
        {
            new Point2d(localText.X, localText.Y),
            new Point2d(localText.X + _boxWidth, localText.Y),
            new Point2d(localText.X + _boxWidth, localText.Y + _boxHeight),
            new Point2d(localText.X, localText.Y + _boxHeight)
        };

        /// <summary>引线锚点（UCS）：矩形取离光标最近的包围盒角；
        /// 椭圆/菱形这类四角不在轮廓上的外形取轮廓最近点，避免线头悬空、与云线不相连。</summary>
        private Point2d ResolveAnchorUcs(CloudPart part, Point3d localText)
        {
            if (!part.AnchorOnOutline)
            {
                return AnnotationService.ClosestCorner(part.MinUcs, part.MaxUcs, localText);
            }

            // 云线已在 WCS；刚体变换不改变距离，用 WCS 光标求轮廓最近点等价于在 UCS 里求。
            var closestWcs = part.Cloud.GetClosestPointTo(_current, false);
            var closestUcs = closestWcs.TransformBy(_wcsToUcs);
            return new Point2d(closestUcs.X, closestUcs.Y);
        }
    }

    internal sealed class PlinePointPreviewJig : DrawJig
    {
        private readonly Document _doc;
        private readonly Matrix3d _ucsToWcs;
        private readonly Matrix3d _wcsToUcs;
        private readonly IList<Point3d> _points;
        private readonly AnnotationSettings _source;
        /// <summary>云线弧瓣朝向的翻转量（+1 / -1），拖拽开始前问一次视图就定死。
        /// <para>WorldDraw 是拖拽回调，期间访问编辑器/数据库会让 AutoCAD 整体丢弃这一帧。
        /// 原先每帧调 <c>OrientCloudBulgesForView</c>（内含 <c>Editor.GetCurrentView()</c>），
        /// 这就是"PL 线拖拽时预览不顺滑"的主因。</para></summary>
        private readonly double _bulgeSign;
        /// <summary>视图高度：包围盒还没成型（只确认了 0~1 个点）时当"字号自适应"的参照，
        /// 免得退回 <c>ResolveEffectiveSettings</c> 内部去问 <c>GetCurrentView</c>。</summary>
        private readonly double _viewHeight;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;
        public bool FinishRequested { get; private set; }
        /// <summary>拖拽期间是否已经采到过至少一个点（诊断用）。</summary>
        public bool HasSample => _hasSample;
        /// <summary>本轮是否因"面板改了绘制选项"被主动中止（区别于用户回车结束 / 自己按 Esc）。</summary>
        public bool Aborted { get; private set; }

        public PlinePointPreviewJig(
            Document doc,
            IList<Point3d> points,
            AnnotationSettings source)
        {
            _doc = doc;
            _points = points;
            _source = source;
            _ucsToWcs = AnnotationService.GetUcsMatrix(doc);
            _wcsToUcs = _ucsToWcs.Inverse();
            // 这两项都要问数据库/编辑器，必须在拖拽开始前一次性取好（见 CloudBulgeSign 的说明）。
            _bulgeSign = AnnotationService.CloudBulgeSign(doc, Vector3d.ZAxis.TransformBy(_ucsToWcs));
            double viewHeight = 0;
            try { using (var view = doc.Editor.GetCurrentView()) viewHeight = view.Height; }
            catch { /* 视图不可用时留 0，由 ResolveEffectiveSettings 自行兜底一次 */ }
            _viewHeight = viewHeight;
            _current = points.Count > 0
                ? points[points.Count - 1]
                : Point3d.Origin;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            // 面板改了绘制选项 / 换了外形：本轮按新选项重开，这里立刻退出（见 InteractionAbort）。
            if (InteractionAbort.Requested)
            {
                Aborted = true;
                return SamplerStatus.Cancel;
            }

            string prompt;
            if (_points.Count == 0)
            {
                prompt = "\n指定 PL 云线起点: ";
            }
            else if (_points.Count < 3)
            {
                prompt = $"\n指定下一点（已输入 {_points.Count} 点，至少需要 3 点）: ";
            }
            else
            {
                prompt = $"\n指定下一点（已输入 {_points.Count} 点，回车/空格完成）: ";
            }

            var options = new JigPromptPointOptions(prompt)
            {
                Cursor = CursorType.RubberBand,
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NullResponseAccepted
            };
            if (_points.Count > 0)
            {
                options.UseBasePoint = true;
                options.BasePoint = _points[_points.Count - 1];
            }

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.None)
            {
                FinishRequested = true;
                return SamplerStatus.Cancel;
            }

            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            if (_points.Count == 0)
            {
                return true;
            }

            RegionPreviewDrawing.PreparePreviewTraits(draw);

            var local = _points
                .Concat(new[] { _current })
                .Select(point => point.TransformBy(_wcsToUcs))
                .ToList();
            var polygon = local
                .Select(point => new Point2d(point.X, point.Y))
                .ToList();
            var minX = local.Min(point => point.X);
            var maxX = local.Max(point => point.X);
            var minY = local.Min(point => point.Y);
            var maxY = local.Max(point => point.Y);
            var diagonal = Math.Sqrt(
                Math.Pow(maxX - minX, 2) +
                Math.Pow(maxY - minY, 2));
            // 折点还没铺开（对角线为 0）时用构造阶段缓存好的视图高度当参照，
            // 不要传 0 让 ResolveEffectiveSettings 内部再去问 GetCurrentView——那又是一次拖拽期间的数据库访问。
            if (diagonal <= 0) diagonal = _viewHeight;
            var settings = AnnotationService.ResolveEffectiveSettings(
                _doc,
                _source,
                null,
                diagonal);

            if (polygon.Count >= 3 &&
                AnnotationService.ValidateCloudPolygon(polygon, out var ignored))
            {
                using (var cloud = AnnotationService.BuildPolygonCloud(
                    polygon,
                    settings))
                {
                    cloud.Elevation = local[0].Z;
                    AnnotationService.ApplyPreviewAppearance(
                        cloud,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    cloud.TransformBy(_ucsToWcs);
                    // 弧瓣朝向用构造阶段算好的翻转量，WorldDraw 里不再问视图（拖拽期间禁止访问编辑器）。
                    AnnotationService.OrientCloudBulges(cloud, _bulgeSign);
                    RegionPreviewDrawing.DrawWidePolyline(draw, cloud, RegionPreviewDrawing.SafePolylineWidth(cloud));
                }
            }
            else
            {
                using (var line = new CadPolyline())
                {
                    for (var i = 0; i < polygon.Count; i++)
                    {
                        line.AddVertexAt(i, polygon[i], 0, 0, 0);
                    }

                    line.Elevation = local[0].Z;
                    AnnotationService.ApplyPreviewAppearance(
                        line,
                        settings,
                        settings.CloudColor,
                        "cloud");
                    line.TransformBy(_ucsToWcs);
                    RegionPreviewDrawing.DrawWidePolyline(draw, line, RegionPreviewDrawing.SafePolylineWidth(line));
                }
            }

            return true;
        }
    }

    internal sealed class MoveAnnotationPreviewJig : DrawJig
    {
        private readonly Point3d _basePoint;
        private readonly List<Entity> _movableEntities;
        private readonly List<CadPolyline> _leaders;
        private Point3d _current;
        private bool _hasSample;

        public Point3d Current => _current;

        public MoveAnnotationPreviewJig(
            Point3d basePoint,
            IEnumerable<Entity> movableEntities,
            IEnumerable<CadPolyline> leaders)
        {
            _basePoint = basePoint;
            _current = basePoint;
            _movableEntities = movableEntities.ToList();
            _leaders = leaders.ToList();
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            // 移动预览已自绘文字框与引出线，改用十字光标避免 CAD 再画一条橡皮筋虚线。
            var options = new JigPromptPointOptions(
                "\n指定批注文字框新位置: ")
            {
                UseBasePoint = true,
                BasePoint = _basePoint,
                Cursor = CursorType.Crosshair
            };
            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (_hasSample && result.Value.DistanceTo(_current) < 1e-8)
            {
                return SamplerStatus.NoChange;
            }

            _current = result.Value;
            _hasSample = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            // 被克隆的真实云线同样是宽多段线，需一并打开填充，否则拖动预览也会显示成半透明。
            RegionPreviewDrawing.PreparePreviewTraits(draw);

            var displacement = _current - _basePoint;
            var transform = Matrix3d.Displacement(displacement);

            foreach (var source in _movableEntities)
            {
                using (var preview = source.Clone() as Entity)
                {
                    if (preview == null)
                    {
                        continue;
                    }

                    preview.TransformBy(transform);
                    // 宽多段线在世界绘制里不出带宽，展开成实心带，避免移动预览同样淡显。
                    if (preview is CadPolyline wide)
                    {
                        RegionPreviewDrawing.DrawWidePolyline(draw, wide, RegionPreviewDrawing.SafePolylineWidth(wide));
                    }
                    else
                    {
                        draw.Geometry.Draw(preview);
                    }
                }
            }

            foreach (var source in _leaders)
            {
                using (var preview = source.Clone() as CadPolyline)
                {
                    if (preview == null)
                    {
                        continue;
                    }

                    var anchor = preview.GetPoint2dAt(0);
                    preview.TransformBy(transform);
                    preview.SetPointAt(0, anchor);
                    RegionPreviewDrawing.DrawWidePolyline(draw, preview, RegionPreviewDrawing.SafePolylineWidth(preview));
                }
            }

            return true;
        }
    }

}
