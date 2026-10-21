using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.Colors;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.EditorInput;
using Gssoft.Gscad.Geometry;
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#endif

namespace XAnnotation
{
    internal enum CloudPromptResult
    {
        Completed,
        Finished,
        Cancelled
    }

    /// <summary>批注核心服务：几何交互、实体创建/读写/更新/删除。</summary>
    internal static class AnnotationService
    {
        /// <summary>CAD 编组名前缀，用于关联批注的所有子实体。</summary>
        private const string GroupPrefix = "LA_PZ_NOTE_";
        /// <summary>XRecord 数据键名，用于在编组扩展字典中存储批注数据。</summary>
        private const string DataKey = "LA_PZ_DATA";

        /// <summary>计算实际生效的字体、云线等参数。
        /// <para>比例选型（设置里的 1:N）＝出图比例分母 N，作用是把"打印到纸上的毫米"换算成图面单位：</para>
        /// <para>• 未勾选自适应：字高(图面) = 打印字高(mm) × N，云线半径(图面) = 打印云线半径(mm) × N；</para>
        /// <para>• 勾选自适应：字高按云线对角线 × 百分比算，云线半径 = 字高 × 0.75，此时 N 不参与（自适应本身就与比例无关）。</para>
        /// <para>设置窗口里"从下拉选比例"会自动取消两个自适应勾选，所以两种算法不会互相打架。</para></summary>
        public static AnnotationSettings ResolveEffectiveSettings(Document doc, AnnotationSettings source, AnnotationData data, double cloudDiagonal = 0, bool captureRenderSettings = false)
        {
            var s=source.Clone();
            var scale=source.ScaleRatio>0?source.ScaleRatio:1.0; // 出图比例分母（1:100 → 100）：打印 mm → 图面单位
            // 图面单位不一定是毫米：总图/场地图常用米（车位间距 2.4、建筑 14 就是 2.4m、14m）。
            // 那种图上"比例选型 1:N"会把尺寸整体放大 1000 倍，字高与云线弧瓣都会离谱。
            // 用图内文字字高识别出的系数顶上（两者相差不足 5 倍时仍尊重用户选的比例）。
            var detected=DrawingScale.UnitsPerMillimeter(doc);
            var unitMismatch=detected>0&&(detected>scale*5.0||detected*5.0<scale);
            var unitScale=unitMismatch?detected:scale;
            var rawText=Math.Max(source.TextHeight,0.1);          // 打印字高（mm）
            var refSize=cloudDiagonal;
            if(refSize<=0){using(var view=doc.Editor.GetCurrentView())refSize=view.Height;}
            var adaptiveText=Math.Max(0.1,refSize*Math.Max(0.1,source.AutoTextViewPercent)/100.0);
            // 按框大小的自适应只在"框能代表图面尺度"时成立。单位对不上时框往往也一并被放大过
            // （总图缩到全图范围框选，框就是几百万单位），这时改按图面换算取尺寸，
            // 否则字高会跟着框一起膨胀，云线弧瓣也大到不成形。
            var text=(source.FontAutoFit&&!unitMismatch)?adaptiveText:rawText*unitScale;
            var factor=text/rawText; // 相对"打印字高"的放大倍数：首行/次行/固定宽度/对勾高度沿用同一倍数
            s.TextHeight=text;s.HeaderHeight=Math.Max(0.1,source.HeaderHeight*factor);s.SecondLineHeight=Math.Max(0.1,source.SecondLineHeight*factor);
            s.FixedWidthValue=Math.Max(text*6,source.FixedWidthValue*factor);
            if(source.CloudAutoFit&&!unitMismatch)
            {
                s.CloudRadius=Math.Max(0.001,adaptiveText*0.75);
                s.LineWidth=Math.Max(0.001,adaptiveText*0.035);
            }
            else
            {
                s.CloudRadius=Math.Max(0.001,source.CloudRadius*unitScale);
                s.LineWidth=Math.Max(0,source.LineWidth*unitScale);
            }
            s.CheckHeight=Math.Max(source.CheckHeight*factor,text*2);if(captureRenderSettings&&data!=null){data.RenderTextHeight=s.TextHeight;data.RenderHeaderHeight=s.HeaderHeight;data.RenderSecondLineHeight=s.SecondLineHeight;data.RenderCloudRadius=s.CloudRadius;data.RenderLineWidth=s.LineWidth;}return s;
        }

        private static AnnotationSettings SettingsForExisting(AnnotationData data)
        {
            var s=SettingsStore.Load();if(data.RenderTextHeight<=0)return s;s.TextHeight=data.RenderTextHeight;s.HeaderHeight=data.RenderHeaderHeight>0?data.RenderHeaderHeight:data.RenderTextHeight;s.SecondLineHeight=data.RenderSecondLineHeight>0?data.RenderSecondLineHeight:data.RenderTextHeight;s.CloudRadius=data.RenderCloudRadius>0?data.RenderCloudRadius:s.CloudRadius;s.LineWidth=data.RenderLineWidth>0?data.RenderLineWidth:s.LineWidth;return s;
        }

        /// <summary>交互式选点：第一角点 → 拖拽云线范围。文字放置由完整预览 Jig 单独完成。
        /// <para>第一角点用 <see cref="FirstCornerPromptJig"/> 而不是 <c>Editor.GetPoint</c>：后者无法被外部中止，
        /// 面板在提示挂起时改选项就会"点了没反应"；换成 Jig 后能让本轮立即退出并按新选项重开。</para>
        /// <para><paramref name="s"/> 建议传"正在跑的那一轮"的设置实例本身（非副本）：拖拽预览每帧从它解析参数，
        /// 面板中改外形/选项能立刻反映到预览上。</para></summary>
        public static bool PromptGeometry(Document doc, AnnotationSettings s, out Point3d firstPoint, out Point3d secondPoint)
        {
            // 进入交互前先识别一次图面单位（要开事务扫文字，不能放在拖拽回调里）。
            DrawingScale.Ensure(doc);
            firstPoint=Point3d.Origin;secondPoint=Point3d.Origin;var ed = doc.Editor;object ortho=null,osmode=null;
            try{
                if(s.AutoCloseOrtho){ortho=CadSystemVariable("ORTHOMODE");SetCadSystemVariable("ORTHOMODE",0);}if(s.AutoCloseSnap){osmode=CadSystemVariable("OSMODE");SetCadSystemVariable("OSMODE",0);}
                var firstJig=new FirstCornerPromptJig("\n指定批注范围第一个角点: ");
                var firstResult=ed.Drag(firstJig);
                if(firstJig.Aborted){ed.WriteMessage("\n绘制选项已更改，本轮按新选项重新开始。");return false;}
                if(firstResult.Status!=PromptStatus.OK)return false;
                var firstWcs=firstJig.Current.TransformBy(GetUcsMatrix(doc));
                var region=new RegionPreviewJig(doc,firstWcs,s);PromptResult regionResult;try{regionResult=ed.Drag(region);}finally{PreviewTransient.Clear();}
                if(region.Aborted){ed.WriteMessage("\n绘制选项已更改，本轮按新选项重新开始。");return false;}
                if(regionResult.Status!=PromptStatus.OK)return false;
                var (_,regionWidth,regionHeight)=UcsAlignedExtents(doc,firstWcs,region.Current);if(regionWidth<=1e-6||regionHeight<=1e-6){ed.WriteMessage("\n批注范围必须同时具有宽度和高度，请重新指定。");return false;}
                firstPoint=firstWcs;secondPoint=region.Current;return true;
            }finally{if(ortho!=null)SetCadSystemVariable("ORTHOMODE",ortho);if(osmode!=null)SetCadSystemVariable("OSMODE",osmode);}
        }

        /// <summary>交互式选点（仅云线）：第一角点 → 拖拽云线范围，不要求文字框位置。</summary>
        public static bool PromptCloudOnly(Document doc, AnnotationSettings s, out Point3d firstPoint, out Point3d secondPoint)
            => PromptCloud(doc, s, false, null, null, out firstPoint, out secondPoint) == CloudPromptResult.Completed;

        /// <summary>交互式取一个"基点"（「基点」外形用：不画云线，引线从它直连到文字框）。
        /// <para>同样用 Jig 而不是 <c>Editor.GetPoint</c>，这样面板改绘制选项时能被
        /// <see cref="InteractionAbort"/> 干净中止（<c>GetPoint</c> 是黑盒，外部中止不了）。</para></summary>
        public static bool PromptBasePoint(Document doc, out Point3d basePoint)
        {
            DrawingScale.Ensure(doc);
            basePoint = Point3d.Origin;
            var ed = doc.Editor;
            var jig = new FirstCornerPromptJig("\n指定批注基点: ");
            var result = ed.Drag(jig);
            if (jig.Aborted)
            {
                ed.WriteMessage("\n绘制选项已更改，本轮按新选项重新开始。");
                return false;
            }

            if (result.Status != PromptStatus.OK) return false;
            basePoint = jig.Current.TransformBy(GetUcsMatrix(doc));
            return true;
        }

        /// <summary>多对一云线选点：回车/空格结束连续绘制，Esc 取消整次操作。</summary>
        public static CloudPromptResult PromptCloudOrFinish(Document doc, AnnotationSettings s, IList<Point3d> historyFirsts, IList<Point3d> historySeconds, out Point3d firstPoint, out Point3d secondPoint)
            => PromptCloud(doc, s, true, historyFirsts, historySeconds, out firstPoint, out secondPoint);

        private static CloudPromptResult PromptCloud(Document doc, AnnotationSettings s, bool allowFinish, IList<Point3d> historyFirsts, IList<Point3d> historySeconds, out Point3d firstPoint, out Point3d secondPoint)
        {
            DrawingScale.Ensure(doc);
            firstPoint=Point3d.Origin;secondPoint=Point3d.Origin;var ed=doc.Editor;object ortho=null,osmode=null;
            try{
                if(s.AutoCloseOrtho){ortho=CadSystemVariable("ORTHOMODE");SetCadSystemVariable("ORTHOMODE",0);}if(s.AutoCloseSnap){osmode=CadSystemVariable("OSMODE");SetCadSystemVariable("OSMODE",0);}
                while(true)
                {
                    Point3d firstWcs;
                    if (allowFinish)
                    {
                        var firstJig = new RegionFirstPointPreviewJig(
                            doc,
                            s,
                            historyFirsts,
                            historySeconds);
                        var firstResult = ed.Drag(firstJig);

                        if (firstJig.FinishRequested)
                        {
                            return CloudPromptResult.Finished;
                        }

                        if (firstJig.Aborted)
                        {
                            ed.WriteMessage("\n绘制选项已更改，本轮按新选项重新开始。");
                            return CloudPromptResult.Cancelled;
                        }

                        if (firstResult.Status != PromptStatus.OK)
                        {
                            return CloudPromptResult.Cancelled;
                        }

                        firstWcs = firstJig.Current;
                    }
                    else
                    {
                        var first = ed.GetPoint("\n指定云线范围第一个角点: ");
                        if (first.Status != PromptStatus.OK)
                        {
                            return CloudPromptResult.Cancelled;
                        }

                        firstWcs = first.Value.TransformBy(GetUcsMatrix(doc));
                    }

                    var region = new RegionPreviewJig(
                        doc,
                        firstWcs,
                        s,
                        historyFirsts,
                        historySeconds);
                    PromptResult regionResult;
                    try { regionResult = ed.Drag(region); }
                    finally { PreviewTransient.Clear(); }
                    if (region.Aborted)
                    {
                        ed.WriteMessage("\n绘制选项已更改，本轮按新选项重新开始。");
                        return CloudPromptResult.Cancelled;
                    }

                    if (regionResult.Status != PromptStatus.OK)
                    {
                        return CloudPromptResult.Cancelled;
                    }
                    var (_,width,height)=UcsAlignedExtents(doc,firstWcs,region.Current);
                    if(width<=1e-6||height<=1e-6)
                    {
                        ed.WriteMessage("\n云线范围必须同时具有宽度和高度，请重新指定。");
                        continue;
                    }
                    firstPoint=firstWcs;secondPoint=region.Current;return CloudPromptResult.Completed;
                }
            }finally{if(ortho!=null)SetCadSystemVariable("ORTHOMODE",ortho);if(osmode!=null)SetCadSystemVariable("OSMODE",osmode);}
        }

        /// <summary>仅创建云线（不含文字、引线、边框），返回实体 ObjectId。</summary>
        public static ObjectId CreateCloudOnly(Document doc, AnnotationSettings settings, Point3d firstPoint, Point3d secondPoint)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var first=firstPoint.TransformBy(wcsToUcs);var second=secondPoint.TransformBy(wcsToUcs);
                var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));
                var cloud=BuildCloud(min,max,settings);cloud.Elevation=first.Z;cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);
                cloud.Layer=EffectiveLayer(settings);cloud.Color=Color.FromColorIndex(ColorMethod.ByAci,settings.CloudColor);
                if(settings.CloudStyle!="渐变"&&settings.LineWidth>0)cloud.ConstantWidth=settings.LineWidth;   // 渐变：线宽走逐段顶点宽度（0→云线线宽），ConstantWidth 必须保持 0
                var id=space.AppendEntity(cloud);tr.AddNewlyCreatedDBObject(cloud,true);
                tr.Commit();return id;
            }
        }

        /// <summary>仅沿用户折点创建闭合 PL 云线，与矩形批注共用多边形云线算法。</summary>
        public static ObjectId CreatePlineCloudOnly(Document doc,AnnotationSettings settings,IList<Point3d> points)
        {
            if(points==null)throw new ArgumentNullException(nameof(points));
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database,tr,settings);
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localPoints=points.Select(point=>point.TransformBy(wcsToUcs)).ToList();
                var cloudPoints=localPoints.Select(point=>new Point2d(point.X,point.Y)).ToList();
                var cloud=BuildPolygonCloud(cloudPoints,settings);cloud.Elevation=localPoints[0].Z;cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);
                cloud.Layer=EffectiveLayer(settings);cloud.Color=Color.FromColorIndex(ColorMethod.ByAci,settings.CloudColor);
                if(settings.CloudStyle!="渐变"&&settings.LineWidth>0)cloud.ConstantWidth=settings.LineWidth;   // 渐变：线宽走逐段顶点宽度（0→云线线宽），ConstantWidth 必须保持 0
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var id=space.AppendEntity(cloud);tr.AddNewlyCreatedDBObject(cloud,true);
                tr.Commit();return id;
            }
        }

        /// <summary>沿折线路径生成云线 + 文字框 + 引出线。</summary>
        public static void CreatePlineCloud(Document doc, AnnotationData data, AnnotationSettings settings, List<Point3d> points, Point3d textLocation)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings,data);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ids=new ObjectIdCollection();
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localPoints=points.Select(p=>p.TransformBy(wcsToUcs)).ToList();var localText=textLocation.TransformBy(wcsToUcs);
                var cloudPts=localPoints.Select(p=>new Point2d(p.X,p.Y)).ToList();
                var cloud=BuildPolygonCloud(cloudPts,settings);cloud.Elevation=localPoints[0].Z;
                // 文字 + 边框
                var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);
                requestedWidth=Math.Max(requestedWidth,MeasureHeaderWidth(doc.Database,tr,data,settings)); // 首行（日期/专业/批注人）不折行
                var margin=TextBoxMargin(settings);
                var text=new MText{Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z),TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};
                ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                MeasureTextBoxFromMText(text,data,settings,out var width,out var height,out margin);
                text.Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z);
                var boxEntity=BuildBox(localText,width,height);boxEntity.Elevation=localText.Z;
                var nearestCloudPoint=cloud.GetClosestPointTo(localText,false);var nearestPt=new Point2d(nearestCloudPoint.X,nearestCloudPoint.Y);
                var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+width,localText.Y),new Point2d(localText.X+width,localText.Y+height),new Point2d(localText.X,localText.Y+height)};
                var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(nearestPt)).First();var leader=new Polyline();leader.AddVertexAt(0,nearestPt,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;
                cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);leader.TransformBy(ucsToWcs);
                Add(space,tr,cloud,ids,settings,data.Id,"cloud",CloudColorFor(settings,data.Status),data);Add(space,tr,text,ids,settings,data.Id,"text",TextColorFor(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",BoxColorFor(settings,data.Status),data);Add(space,tr,leader,ids,settings,data.Id,"leader",LeaderColorFor(settings,data.Status),data);
                // 编组
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForWrite);
                var group=new Group("X-批注 "+data.Number,true);
                groups.SetAt(GroupPrefix+data.Id,group);tr.AddNewlyCreatedDBObject(group,true);group.Append(ids);
                WriteMaster(group,tr,data);
                tr.Commit();
                ArchiveRecord(doc, "创建", data, null);
            }
        }

        /// <summary>多对一批注：清理选点阶段的临时云线，按最终参数创建云线、文字框和引出线并统一编组。</summary>
        public static void CreateMultiCloud(
            Document doc,
            AnnotationData data,
            AnnotationSettings settings,
            AnnotationSettings sourceSettings,
            List<ObjectId> previewCloudIds,
            List<Point3d> firstPoints,
            List<Point3d> secondPoints,
            Point3d textLocation)
        {
            if (firstPoints == null || secondPoints == null || firstPoints.Count == 0 || firstPoints.Count != secondPoints.Count)
                throw new ArgumentException("多对一批注的云线范围数据不完整。");
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,settings,data);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ids=new ObjectIdCollection();
                // 临时云线只负责在连续选点期间提供可见反馈；最终使用统一的自适应参数重建并写入 XData。
                if (previewCloudIds != null)
                {
                    foreach (var previewId in previewCloudIds)
                    {
                        if (!previewId.IsValid || previewId.IsErased) continue;
                        var previewEntity = tr.GetObject(previewId, OpenMode.ForWrite, false);
                        if (previewEntity != null && !previewEntity.IsErased) previewEntity.Erase();
                    }
                }
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var localText=textLocation.TransformBy(wcsToUcs);var localFirsts=firstPoints.Select(p=>p.TransformBy(wcsToUcs)).ToList();var localSeconds=secondPoints.Select(p=>p.TransformBy(wcsToUcs)).ToList();
                var cloudSettings=new List<AnnotationSettings>(firstPoints.Count);
                // 引线锚点必须在云线转到 WCS 之前算：椭圆/菱形外形上"离文字框最近的包围盒角"并不在轮廓上，
                // 只能取云线轮廓本身的最近点，否则线头悬空、与外形脱开（矩形仍取包围盒角）。
                var cloudAnchors=new List<Point2d>(firstPoints.Count);
                for(var k=0;k<localFirsts.Count;k++)
                {
                    var first=localFirsts[k];var second=localSeconds[k];var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));var width=max.X-min.X;var height=max.Y-min.Y;
                    var regionSettings=ResolveEffectiveSettings(doc,sourceSettings??settings,new AnnotationData(),Math.Sqrt(width*width+height*height));cloudSettings.Add(regionSettings);
                    var cloud=BuildCloud(min,max,regionSettings);cloud.Elevation=first.Z;
                    cloudAnchors.Add(ResolveRegionLeaderAnchor(cloud,min,max,regionSettings,localText));
                    cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);Add(space,tr,cloud,ids,regionSettings,data.Id,"cloud",CloudColorFor(regionSettings,data.Status),data);
                }

                // 「仅绘云线」：多对一也只出云线，不生成文字、边框和引线。
                if(!settings.CloudOnly)
                {
                var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);
                requestedWidth=Math.Max(requestedWidth,MeasureHeaderWidth(doc.Database,tr,data,settings)); // 首行（日期/专业/批注人）不折行
                var margin=TextBoxMargin(settings);
                var text=new MText{Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z),TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                MeasureTextBoxFromMText(text,data,settings,out var widthBox,out var heightBox,out margin);
                text.Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z);
                var boxEntity=BuildBox(localText,widthBox,heightBox);boxEntity.Elevation=localText.Z;
                var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+widthBox,localText.Y),new Point2d(localText.X+widthBox,localText.Y+heightBox),new Point2d(localText.X,localText.Y+heightBox)};
                for(var k=0;k<cloudAnchors.Count;k++){var cloudCorner=cloudAnchors[k];var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First();var leader=new Polyline();leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;leader.TransformBy(ucsToWcs);Add(space,tr,leader,ids,cloudSettings[k],data.Id,"leader",LeaderColorFor(cloudSettings[k],data.Status),data);}
                text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);Add(space,tr,text,ids,settings,data.Id,"text",TextColorFor(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",BoxColorFor(settings,data.Status),data);
                }
                // 编组
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForWrite);
                var group=new Group("X-批注 "+data.Number,true);
                groups.SetAt(GroupPrefix+data.Id,group);tr.AddNewlyCreatedDBObject(group,true);group.Append(ids);
                WriteMaster(group,tr,data);
                tr.Commit();
                ArchiveRecord(doc, "创建", data, null);
            }
        }

        /// <summary>增补云线：向既有批注编组追加一条云线和一条连到原文字框的引出线。
        /// <para><b>外形/云线样式/弧瓣半径按当前设置</b>（含比例换算与自适应，经 <see cref="ResolveEffectiveSettings"/>）生成；
        /// <b>图层、颜色、线宽仍照抄原批注</b>（原图层可能带日期/人名后缀，按设置重算会落到今天的图层上）。</para></summary>
        public static bool AppendCloud(Document doc, AnnotationData data, Point3d firstPoint, Point3d secondPoint)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForRead);
                var groupName=GroupPrefix+data.Id;if(!groups.Contains(groupName))return false;
                var group=(Group)tr.GetObject(groups.GetAt(groupName),OpenMode.ForWrite);
                // 原批注样式实测（图层/颜色/线宽 + 引线锚点）——外形与云线几何不再照抄，只有这几样照抄。
                var existing=ProbeExistingStyle(tr,group);
                var baseSettings=SettingsForExisting(data);
                LegacyScaleFallback(baseSettings,data);
                baseSettings.FontAutoFit=false;baseSettings.CloudAutoFit=false;
                EnsureRegApp(doc.Database,tr);EnsureLayer(doc.Database,tr,baseSettings,data);
                // 原图层可能带"日期/人名"后缀（LayerAppendDate），按当前设置重算会落到今天的图层上，故照抄原图层。
                var layerOverride=ExistingLayerName(doc.Database,tr,existing.Layer);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ids=new ObjectIdCollection();
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();
                var first=firstPoint.TransformBy(wcsToUcs);var second=secondPoint.TransformBy(wcsToUcs);
                var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));
                // 云线：外形照原批注（见 BuildAppendCloud），尺寸/弧瓣仍按当前设置（含比例换算/自适应）。
                var diag=Math.Sqrt(Math.Pow(max.X-min.X,2)+Math.Pow(max.Y-min.Y,2));
                var effective=ResolveEffectiveSettings(doc,SettingsStore.Load(),data,diag);
                var cloud=BuildAppendCloud(existing,effective,min,max,out var appendShape);
                baseSettings.Shape=appendShape;
                cloud.Elevation=first.Z;
                // 引线锚点目标：文字框四角（无框时用文字位置），统一转到 UCS 局部坐标比较。
                var hasAnchor=existing.Box!=null||existing.HasText;
                Point3d targetLocal=Point3d.Origin;Point2d[] boxCornersLocal=null;
                if(existing.Box!=null&&existing.Box.NumberOfVertices>=4)
                {
                    var box=existing.Box;
                    boxCornersLocal=Enumerable.Range(0,box.NumberOfVertices).Select(i=>{var p=box.GetPoint2dAt(i);var w=new Point3d(p.X,p.Y,box.Elevation).TransformBy(wcsToUcs);return new Point2d(w.X,w.Y);}).ToArray();
                    targetLocal=new Point3d(boxCornersLocal.Average(c=>c.X),boxCornersLocal.Average(c=>c.Y),first.Z);
                }
                else targetLocal=existing.TextPosition.TransformBy(wcsToUcs);
                // 引线端点必须在云线转到 WCS 之前算好：椭圆外形的锚点用的是云线局部坐标上的最近点。
                var cloudCorner=ResolveRegionLeaderAnchor(cloud,min,max,baseSettings,targetLocal);
                var boxCorner=boxCornersLocal!=null?boxCornersLocal.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First():new Point2d(targetLocal.X,targetLocal.Y);
                cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);
                Add(space,tr,cloud,ids,baseSettings,data.Id,"cloud",(existing.CloudColor??baseSettings.CloudColor),data,layerOverride);
                // 原批注连文字框都没有（异常数据）时不硬拉一条指向原点的引线，只补云线。
                if(hasAnchor)
                {
                    var leader=new Polyline();leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=first.Z;
                    leader.TransformBy(ucsToWcs);
                    Add(space,tr,leader,ids,baseSettings,data.Id,"leader",(existing.LeaderColor??baseSettings.LeaderColor),data,layerOverride);
                    // Add 统一按设置里的线宽写 ConstantWidth，这里按原引线的实测线宽再覆盖一次。
                    if(existing.HasLeader)SafeSetConstantWidth(leader,existing.LeaderWidth);
                }
                // 线宽同理：按原云线的实测值覆盖，保证粗细也一致（0 表示原云线就是不设线宽）。
                if(existing.HasCloud)SafeSetConstantWidth(cloud,existing.CloudWidth);
                group.Append(ids);
                tr.Commit();
                AnnotationHistoryStore.Record(doc,"增补云线",data,null);
                return true;
            }
        }

        /// <summary>增补云线的交互预览设置：当前全局设置（含比例/自适应）叠上<b>原批注的外形</b>，
        /// 与 <see cref="AppendCloud"/> 的落图口径一致——选点阶段看到的云线大小<b>和形状</b>都与最终补出来的相同。
        /// <para>形状必须跟着原批注走：否则从菱形/椭圆批注上增补时，预览是矩形、落图却是菱形，看着像 bug
        /// （这正是"增补云线默认矩形"被报出来的那件事）。</para></summary>
        public static AnnotationSettings AppendCloudStyle(Document doc, AnnotationData data)
        {
            var settings = SettingsStore.Load();
            try
            {
                var shape = ProbeCloudShape(doc, data);
                if (shape == "矩形" || shape == "菱形" || shape == "椭圆") settings.Shape = shape;
                // 留痕：外形探测是本功能最容易"看起来对、其实反推错了"的一环，出问题看这一行就够。
                PluginLog.Info(
                    "AppendCloudStyle",
                    "原批注云线外形探测 = " + (shape ?? "<未识别，退回设置值>") + "，预览按「" + settings.Shape + "」");
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("AppendCloudStyle", ex.Message);
            }

            // 原批注带状态色（已回复/已完成）时，预览也整体用那个颜色 —— 否则选点时是洋红、落图是黄色，
            // 看着像"增补出来的云线颜色不对"。
            var statusColor = StatusColorOverride(settings, data == null ? null : data.Status);
            if (statusColor.HasValue)
            {
                settings.CloudColor = settings.LeaderColor = settings.TextColor = settings.BoxColor = statusColor.Value;
            }

            return settings;
        }

        /// <summary>读原批注编组里的云线实体，反推它的外形（矩形/菱形/椭圆）。读不到返回 null。</summary>
        private static string ProbeCloudShape(Document doc, AnnotationData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Id)) return null;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var name = GroupPrefix + data.Id;
                if (!groups.Contains(name)) return null;
                var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForRead);
                return ProbeExistingStyle(tr, group).Shape;
            }
        }

        /// <summary>老数据（没有 Render* 图面实测值）的兜底：按"打印尺寸 × 比例分母"补一次换算，
        /// 免得把"打印 mm"直接当图面值用。</summary>
        private static void LegacyScaleFallback(AnnotationSettings settings,AnnotationData data)
        {
            if(data.RenderTextHeight>0||data.RenderCloudRadius>0||data.RenderLineWidth>0)return;
            var scale=settings.ScaleRatio>0?settings.ScaleRatio:1.0;
            settings.TextHeight=Math.Max(0.1,settings.TextHeight*scale);
            settings.HeaderHeight=Math.Max(0.1,settings.HeaderHeight*scale);
            settings.SecondLineHeight=Math.Max(0.1,settings.SecondLineHeight*scale);
            settings.CloudRadius=Math.Max(0.001,settings.CloudRadius*scale);
            settings.LineWidth=Math.Max(0,settings.LineWidth*scale);
        }

        /// <summary>把编组里<b>既有实体</b>上量到的尺寸回填成"图面实测值"，并同步修正本次要用的设置。
        /// <para><b>只在 <c>data.RenderTextHeight &lt;= 0</c> 时动手</b>（老批注，或创建时漏记实测值的新批注）。
        /// 兜底的必要性：<see cref="SettingsForExisting"/> 在没有实测值时只能退回设置文件，
        /// 而设置文件里存的是 <b>打印 mm</b>（如字高 3）＋ 出图比例分母（如 100）——两者不是一个量级。
        /// <see cref="FormatText"/> 会把 <c>s.TextHeight</c> 写成内联字高码 <c>\H3;</c>，
        /// 于是"双击编辑一次"就把正文从图面 300 压成 3 —— 2026-10-13 用户实测：面板「基点」模式画的批注，
        /// 双击修改并补了文字之后"文字变得很小"（根因就是这条：<c>DoBasePointAnnotation</c> 当时漏传了
        /// <c>captureRenderSettings</c>）。</para>
        /// <para>推算口径与创建时的 <see cref="ResolveEffectiveSettings"/> 完全一致：正文取实测字高，
        /// 首行/次行/固定宽度/对勾高度按"实测 ÷ 打印字高"的同一倍数放大。
        /// 回填进 <paramref name="data"/> 后由 <c>Update</c> 的 <c>WriteMaster</c> 一起落盘，这条批注被永久修好
        /// ——不必要求用户重画，老图打开编辑一次即自愈。</para></summary>
        private static void AdoptMeasuredTextHeights(Transaction tr, Group group, AnnotationData data, AnnotationSettings settings)
        {
            if (data == null || settings == null || group == null) return;
            if (data.RenderTextHeight > 0) return;   // 已记过实测值：以它为准，别动

            // 实测字高优先取文字实体；文字实体缺失（数据损坏／被手工删）时不猜，保持原样。
            double measured = 0;
            try
            {
                foreach (ObjectId oid in group.GetAllEntityIds())
                {
                    if (!oid.IsValid || oid.IsErased) continue;
                    if (tr.GetObject(oid, OpenMode.ForRead, false) is MText text && text.TextHeight > 1e-9)
                    {
                        measured = text.TextHeight;
                        break;
                    }
                }
            }
            catch (System.Exception ex) { PluginLog.Warning("Update.AdoptMeasuredTextHeights", ex.Message); }

            if (!(measured > 1e-9)) return;

            var raw = Math.Max(settings.TextHeight, 0.1);   // 设置文件里的"打印字高(mm)"
            var factor = measured / raw;                    // 与创建时 text/rawText 同一口径
            settings.TextHeight = measured;
            settings.HeaderHeight = Math.Max(0.1, settings.HeaderHeight * factor);
            settings.SecondLineHeight = Math.Max(0.1, settings.SecondLineHeight * factor);
            settings.CloudRadius = Math.Max(0.001, settings.CloudRadius * factor);
            settings.LineWidth = Math.Max(0, settings.LineWidth * factor);
            settings.FixedWidthValue = Math.Max(measured * 6.0, settings.FixedWidthValue * factor);
            settings.CheckHeight = Math.Max(settings.CheckHeight * factor, measured * 2.0);

            // 回填进批注数据：下次编辑／批注列表／CSV 导出／并入表格就都有正确的图面值了。
            data.RenderTextHeight = settings.TextHeight;
            data.RenderHeaderHeight = settings.HeaderHeight;
            data.RenderSecondLineHeight = settings.SecondLineHeight;
            data.RenderCloudRadius = settings.CloudRadius;
            data.RenderLineWidth = settings.LineWidth;
            PluginLog.Info("Update.AdoptMeasuredTextHeights",
                "批注 " + data.Number + " 缺少图面实测字高，已按编组内文字反测：" + measured.ToString("0.###")
                + "（打印字高 " + raw.ToString("0.###") + " × " + factor.ToString("0.###") + " 倍）。");
        }

        /// <summary>原批注的实测样式：从编组内既有云线/引线/文字框实体上读回来，供"增补云线"照原样补画。
        /// 每一项测不出时留空（颜色用 null、尺寸用 -1），由调用方退回设置值。</summary>
        private sealed class ExistingStyle
        {
            public string Layer;          // 云线所在图层（原批注的图层可能带日期/人名后缀）
            public short? CloudColor;     // 按 ACI 取色时有效
            public short? LeaderColor;
            public double CloudWidth;     // 云线线宽（图面单位，0 = 原云线未设线宽）
            public double LeaderWidth;
            public double ScallopRadius;  // 云线弧瓣间距的一半（= 创建时用的 CloudRadius 图面值）；&lt;=0 表示没测出来
            public string CloudStyle;     // 等宽 / 渐变，null = 没测出来
            public string Shape;          // 矩形 / 菱形 / 椭圆，null = 没测出来
            // 原云线的顶点模板（自身平面内的相对形状 + 逐顶点 bulge）：增补云线直接按模板重建，
            // 外形与弧瓣起伏原样照抄，不再依赖 Shape 反推（反推对接近正方形的矩形会误判成菱形）。
            public double[] TemplateU;
            public double[] TemplateV;
            public double[] TemplateBulges;
            public double TemplateUMin, TemplateUMax, TemplateVMin, TemplateVMax;
            public bool HasTemplate;
            public Polyline Box;          // 文字框：引线的目标
            public Point3d TextPosition;  // 无文字框时的兜底锚点
            public bool HasText;
            public bool HasCloud;
            public bool HasLeader;
        }

        /// <summary>安全读多段线的<b>全局</b>线宽：多段线的逐段顶点宽度不一致时（渐变云线的 0→云线线宽 锥形段、
        /// 其他工具画的锥形多段线），AutoCAD 的 <c>Polyline.ConstantWidth</c> 取值会抛 eInvalidInput，
        /// 这里统一兜底为 0（＝读不到全局线宽），不让一条异常多段线把整个命令打断。</summary>
        private static double SafeConstantWidth(Polyline poly)
        {
            try { return poly.ConstantWidth; } catch { return 0; }
        }

        /// <summary>安全写多段线的全局线宽：逐段宽度不一致时写 ConstantWidth 同样可能抛 eInvalidInput，
        /// 失败就保持原样（调用方若已按逐段宽度重建顶点，本来也不需要再写全局宽度）。</summary>
        private static void SafeSetConstantWidth(Polyline poly, double width)
        {
            try { poly.ConstantWidth = width; } catch { /* 顶点宽度已逐段指定，忽略 */ }
        }

        /// <summary>扫描编组内实体，实测原批注的云线/引线样式。</summary>
        private static ExistingStyle ProbeExistingStyle(Transaction tr,Group group)
        {
            var style=new ExistingStyle();
            foreach(ObjectId member in group.GetAllEntityIds())
            {
                if(!member.IsValid||member.IsErased)continue;
                var entity=tr.GetObject(member,OpenMode.ForRead,false) as Entity;
                if(entity==null)continue;
                var role=TryGetRole(entity,out var r)?r:null;
                if(role=="cloud"&&entity is Polyline cloud)
                {
                    if(style.HasCloud)continue;
                    style.HasCloud=true;style.Layer=cloud.Layer;style.CloudWidth=SafeConstantWidth(cloud);
                    if(cloud.Color!=null&&cloud.Color.ColorMethod==ColorMethod.ByAci)style.CloudColor=cloud.Color.ColorIndex;
                    // 逐个赋值，任一测量失败都只影响它自己，其余照旧生效。
                    try{style.ScallopRadius=MeasureScallopRadius(cloud);}catch{style.ScallopRadius=0;}
                    try{style.CloudStyle=DetectCloudStyle(cloud);}catch{style.CloudStyle=null;}
                    try{style.Shape=DetectCloudShape(cloud);}catch{style.Shape=null;}
                    try{ExtractTemplate(cloud,style);}catch{style.HasTemplate=false;}
                }
                else if(role=="leader"&&entity is Polyline leader)
                {
                    if(style.HasLeader)continue;
                    style.HasLeader=true;style.LeaderWidth=SafeConstantWidth(leader);
                    if(string.IsNullOrEmpty(style.Layer))style.Layer=leader.Layer;
                    if(leader.Color!=null&&leader.Color.ColorMethod==ColorMethod.ByAci)style.LeaderColor=leader.Color.ColorIndex;
                }
                else if(role=="box"&&entity is Polyline box)
                {
                    if(style.Box==null)style.Box=box;
                    if(string.IsNullOrEmpty(style.Layer))style.Layer=box.Layer;
                }
                else if(entity is MText mtext)
                {
                    if(!style.HasText){style.HasText=true;style.TextPosition=mtext.Location;}
                }
            }
            return style;
        }

        /// <summary>原图层名：图层表里存在才返回，否则返回 null（避免赋一个不存在的图层名直接抛异常）。</summary>
        private static string ExistingLayerName(Database db,Transaction tr,string name)
        {
            if(string.IsNullOrWhiteSpace(name))return null;
            try{var table=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);return table.Has(name)?name:null;}
            catch{return null;}
        }

        /// <summary>云线弧瓣间距的一半（即创建时所用的"云线半径"图面值）：取相邻顶点弦长的<b>中位数</b>÷2。
        /// 原批注无论是"自适应"还是"按比例"生成的，用这个值重建都能得到同样的弧瓣密度（中位数可避开 PL 云线短边带来的异常弦长）。</summary>
        private static double MeasureScallopRadius(Polyline cloud)
        {
            var n=cloud.NumberOfVertices;if(n<4)return 0;
            var chords=new List<double>(n);
            for(var i=0;i<n;i++)
            {
                var chord=cloud.GetPoint3dAt(i).DistanceTo(cloud.GetPoint3dAt((i+1)%n));
                if(chord>1e-9)chords.Add(chord);
            }
            if(chords.Count==0)return 0;
            chords.Sort();
            return chords[chords.Count/2]/2.0;
        }

        /// <summary>判断原云线用的是"等宽"还是"渐变"弧瓣：等宽所有 bulge 相同且顶点等距；
        /// 渐变的顶点间距沿周长起伏（弧瓣有大有小，旧版 bulge 波动同样识别为渐变）。测不出返回 null。</summary>
        private static string DetectCloudStyle(Polyline cloud)
        {
            var n=cloud.NumberOfVertices;if(n<8)return null;
            var min=double.MaxValue;var max=double.MinValue;
            var chords=new List<double>();
            for(var i=0;i<n;i++)
            {
                var bulge=Math.Abs(cloud.GetBulgeAt(i));
                if(bulge<min)min=bulge;if(bulge>max)max=bulge;
                var chord=cloud.GetPoint3dAt(i).DistanceTo(cloud.GetPoint3dAt((i+1)%n));
                if(chord>1e-9)chords.Add(chord);
            }
            if(max<=1e-9)return null;
            if(max-min>Math.Max(1e-6,max*0.05))return "渐变";       // bulge 有波动（旧版渐变/手调）
            if(chords.Count<4)return null;
            chords.Sort();
            var cMin=chords[0];var cMax=chords[chords.Count-1];var cMid=chords[chords.Count/2];
            return cMax-cMin>Math.Max(1e-6,cMid*0.12)?"渐变":"等宽";  // 新版渐变：bulge 恒定、弦长起伏
        }

        /// <summary>三种标准外形的"归一化极径剖面"拟合容差（均方根）。
        /// <para>实测：标准外形（含 5% 顶点抖动）RMS ≤ 0.06；PL 任意多边形 ≥ 0.19。
        /// 超过本值说明三种都拟不上 → 判为自定义外形，返回 null 交给顶点模板照抄，不硬套一个形状。</para></summary>
        private const double ShapeFitTolerance = 0.10;

        /// <summary>反推原云线外形（矩形 / 菱形 / 椭圆），三种都拟不上时返回 null。
        /// <para><b>判据：归一化极径剖面拟合。</b>把每个顶点投到"以包围盒中心为原点、半宽半高都为 1"的
        /// 归一化空间，得到极径 ρ 与方向角 φ。三种标准外形的理论 ρ(φ) 都是确定的：</para>
        /// <list type="bullet">
        /// <item>椭圆 ρ ≡ 1</item>
        /// <item>菱形 ρ = 1/(|cosφ|+|sinφ|)（顶点在包围盒边中点，|du|+|dv|=1）</item>
        /// <item>矩形 ρ = 1/max(|cosφ|,|sinφ|)（顶点贴包围盒四边）</item>
        /// </list>
        /// <para>逐顶点算实际 ρ 与三者理论值的偏差，取均方根最小者；RMS 超过
        /// <see cref="ShapeFitTolerance"/> 则返回 null。云线顶点严格落在轮廓上
        /// （<see cref="BuildScallopedVertices"/> 只写 bulge、不移点），所以极径剖面是"精确可判"的。</para>
        /// <para><b>为什么换掉旧算法（v3）</b>：v2 先用"相邻弦方向的 4θ 圆统计集中度"判椭圆，再用
        /// "顶点贴包围盒边 / 落内接菱形"判矩形/菱形。构造三种标准外形做回归后发现：<b>菱形在宽高比 1.5~4 一带
        /// 弦方向集中度会掉到 0.28 左右，被误判成椭圆</b>（<c>touch</c> 又恒为 0.33、永远够不到 0.55 的矩形门限），
        /// 于是"菱形批注增补出来的云线形状不对"。v3 在同一批回归用例（含任意采样点数、含 5% 顶点抖动）
        /// 上 94/94 判对。</para>
        /// <para>主体正确性由顶点模板（<see cref="BuildCloudFromTemplate"/>）保证，此反推只决定
        /// "按标准外形重建"还是"照抄模板"。</para></summary>
        private static string DetectCloudShape(Polyline cloud)
        {
            try
            {
                var n=cloud.NumberOfVertices;if(n<8)return null;
                // 直接用顶点坐标的 X/Y（批注云线建在当前视图平面上，轴向与 UCS 一致）；
                // 斜 UCS 里建的矩形会判不出（返回 null 走顶点模板）。
                var u=new double[n];var v=new double[n];
                for(var i=0;i<n;i++){var p=cloud.GetPoint3dAt(i);u[i]=p.X;v[i]=p.Y;}
                double uMin=double.MaxValue,uMax=double.MinValue,vMin=double.MaxValue,vMax=double.MinValue;
                for(var i=0;i<n;i++){if(u[i]<uMin)uMin=u[i];if(u[i]>uMax)uMax=u[i];if(v[i]<vMin)vMin=v[i];if(v[i]>vMax)vMax=v[i];}
                var halfW=(uMax-uMin)/2.0;var halfH=(vMax-vMin)/2.0;
                if(halfW<1e-9||halfH<1e-9)return null;
                var cx=(uMax+uMin)/2.0;var cy=(vMax+vMin)/2.0;

                double rectSum=0,diamondSum=0,ellipseSum=0;var counted=0;
                for(var i=0;i<n;i++)
                {
                    var du=(u[i]-cx)/halfW;var dv=(v[i]-cy)/halfH;
                    var r=Math.Sqrt(du*du+dv*dv);
                    if(r<1e-9)continue;
                    // 方向角的正/余弦取绝对值：用归一化坐标求方向，与理论式的自变量（φ）同一套。
                    var cosA=Math.Max(Math.Abs(du)/r,1e-12);
                    var sinA=Math.Max(Math.Abs(dv)/r,1e-12);
                    var dRect=r-1.0/Math.Max(cosA,sinA);
                    var dDiamond=r-1.0/(cosA+sinA);
                    var dEllipse=r-1.0;
                    rectSum+=dRect*dRect;diamondSum+=dDiamond*dDiamond;ellipseSum+=dEllipse*dEllipse;
                    counted++;
                }
                if(counted<8)return null;

                var best="矩形";var bestError=Math.Sqrt(rectSum/counted);
                var diamondError=Math.Sqrt(diamondSum/counted);
                if(diamondError<bestError){best="菱形";bestError=diamondError;}
                var ellipseError=Math.Sqrt(ellipseSum/counted);
                if(ellipseError<bestError){best="椭圆";bestError=ellipseError;}

                return bestError>ShapeFitTolerance?null:best;
            }
            catch{return null;}
        }

        /// <summary>把闭合多段线的顶点投影到其自身平面（origin→最远点为 X 轴）。
        /// 返回 U 坐标数组，V 通过 <paramref name="vOut"/> 带出；退化（共线/无法定向）时返回 null。</summary>
        private static double[] ProjectCloudPlane(Polyline cloud,out double[] vOut)
        {
            vOut=null;
            var n=cloud.NumberOfVertices;if(n<4)return null;
            var points=new Point3d[n];for(var i=0;i<n;i++)points[i]=cloud.GetPoint3dAt(i);
            var origin=points[0];var far=origin;var farDistance=-1.0;
            for(var i=0;i<n;i++){var d=points[i].DistanceTo(origin);if(d>farDistance){farDistance=d;far=points[i];}}
            if(farDistance<1e-9)return null;
            var axisX=(far-origin).GetNormal();
            var normal=Vector3d.ZAxis;var widest=-1.0;
            for(var i=0;i<n;i++)
            {
                var cross=axisX.CrossProduct(points[i]-origin);var length=cross.Length;
                if(length>widest){widest=length;normal=cross.GetNormal();}
            }
            if(widest<1e-9)return null;
            var axisY=normal.CrossProduct(axisX);
            var u=new double[n];var v=new double[n];
            for(var i=0;i<n;i++){var rel=points[i]-origin;u[i]=rel.DotProduct(axisX);v[i]=rel.DotProduct(axisY);}
            vOut=v;return u;
        }

        /// <summary>把原云线的顶点布局提取成模板（平面相对坐标 + 逐顶点 bulge），供增补云线照抄重建。</summary>
        private static void ExtractTemplate(Polyline cloud,ExistingStyle style)
        {
            var u=ProjectCloudPlane(cloud,out var v);
            if(u==null)return;
            var n=u.Length;
            var bulges=new double[n];
            for(var i=0;i<n;i++){try{bulges[i]=cloud.GetBulgeAt(i);}catch{bulges[i]=0;}}
            double uMin=double.MaxValue,uMax=double.MinValue,vMin=double.MaxValue,vMax=double.MinValue;
            for(var i=0;i<n;i++){if(u[i]<uMin)uMin=u[i];if(u[i]>uMax)uMax=u[i];if(v[i]<vMin)vMin=v[i];if(v[i]>vMax)vMax=v[i];}
            if(uMax-uMin<1e-9||vMax-vMin<1e-9)return;
            style.TemplateU=u;style.TemplateV=v;style.TemplateBulges=bulges;
            style.TemplateUMin=uMin;style.TemplateUMax=uMax;style.TemplateVMin=vMin;style.TemplateVMax=vMax;
            style.HasTemplate=true;
        }

        /// <summary>按原云线顶点模板在新框上重建云线：外形（矩形/菱形/椭圆/任意折线）与弧瓣起伏（bulge 序列）
        /// 直接线性映射照抄，不经过 Shape 反推——这是"增补云线与原批注形状一致"的根本保证。
        /// 弧瓣的绝对尺寸随新框/原框的比例缩放（密度与形状不变）。失败返回 null，由调用方退回 BuildCloud。</summary>
        private static Polyline BuildCloudFromTemplate(ExistingStyle t,Point2d min,Point2d max)
        {
            if(t==null||!t.HasTemplate||t.TemplateU==null||t.TemplateV==null||t.TemplateBulges==null)return null;
            var n=t.TemplateU.Length;if(n<4||t.TemplateV.Length!=n||t.TemplateBulges.Length!=n)return null;
            var cx=(min.X+max.X)/2.0;var cy=(min.Y+max.Y)/2.0;
            var halfW=Math.Max((max.X-min.X)/2.0,1e-9);var halfH=Math.Max((max.Y-min.Y)/2.0,1e-9);
            var uC=(t.TemplateUMin+t.TemplateUMax)/2.0;var vC=(t.TemplateVMin+t.TemplateVMax)/2.0;
            var halfU=Math.Max((t.TemplateUMax-t.TemplateUMin)/2.0,1e-9);var halfV=Math.Max((t.TemplateVMax-t.TemplateVMin)/2.0,1e-9);
            var p=new Polyline();
            for(var i=0;i<n;i++)
            {
                var nu=cx+(t.TemplateU[i]-uC)/halfU*halfW;
                var nv=cy+(t.TemplateV[i]-vC)/halfV*halfH;
                p.AddVertexAt(i,new Point2d(nu,nv),t.TemplateBulges[i],0,0);
            }
            p.Closed=true;
            return p;
        }

        /// <summary>增补云线的外形来源，按可靠性排序（<paramref name="shapeUsed"/> 带出实际用的外形名，仅供记录）：
        /// <list type="number">
        /// <item>原批注外形可反推（矩形/菱形/椭圆）→ 按<b>同一外形</b>在新框上重建（与框选落图同一算法）；
        /// 这样"原批注是什么形状，增补出来就是什么形状"。</item>
        /// <item>反推不出来（PL 折线围成的自定义外形等）→ 照抄原云线的<b>顶点模板</b>，形状与弧瓣起伏原样复现。</item>
        /// <item>两者都拿不到（如原批注是「基点」批注、本来就没有云线）→ 退回当前设置里的外形。</item>
        /// </list>
        /// <para><b>历史问题</b>：增补云线原先直接用"当前设置"里的外形（默认矩形），于是从菱形/椭圆批注上
        /// 增补出来的云线却是矩形——和原批注对不上。</para></summary>
        private static Polyline BuildAppendCloud(ExistingStyle existing, AnnotationSettings settings, Point2d min, Point2d max, out string shapeUsed)
        {
            var shape = existing?.Shape;
            if (shape == "矩形" || shape == "菱形" || shape == "椭圆")
            {
                var byShape = settings.Clone();
                byShape.Shape = shape;
                shapeUsed = shape;
                return BuildCloud(min, max, byShape);
            }

            var byTemplate = BuildCloudFromTemplate(existing, min, max);
            if (byTemplate != null)
            {
                shapeUsed = string.IsNullOrEmpty(shape) ? settings.Shape : shape;
                PluginLog.Info("BuildAppendCloud", "外形未识别，按原云线顶点模板照抄（形状不变）。");
                return byTemplate;
            }

            // 走到这里说明"既反推不出外形、也拿不到顶点模板"（原批注没有云线，如「基点」批注）。
            // 留一条日志：这是唯一会让增补云线外形与原批注不一致的分支，出问题一眼可见。
            shapeUsed = settings.Shape;
            PluginLog.Warning(
                "BuildAppendCloud",
                "原批注云线不可用（HasCloud=" + (existing != null && existing.HasCloud) +
                "，HasTemplate=" + (existing != null && existing.HasTemplate) +
                "），增补云线退回当前设置外形「" + settings.Shape + "」。");
            return BuildCloud(min, max, settings);
        }

        /// <summary>创建批注实体组：UCS 对齐云线 → 文字 → 边框 → 斜向引出线。
        /// <paramref name="spaceId"/> 为空时写入当前空间；导入按原坐标还原时传入原批注所在布局的空间 Id。</summary>
        public static bool Create(Document doc, AnnotationData data, AnnotationSettings settings, Point3d firstPoint, Point3d secondPoint, Point3d textLocation, ObjectId spaceId = default(ObjectId))
        {
            DrawingScale.Ensure(doc);
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database, tr); EnsureLayer(doc.Database, tr, settings,data);
                var space = (BlockTableRecord)tr.GetObject(spaceId.IsNull ? doc.Database.CurrentSpaceId : spaceId, OpenMode.ForWrite);
                var ids = new ObjectIdCollection();

                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();var first=firstPoint.TransformBy(wcsToUcs);var second=secondPoint.TransformBy(wcsToUcs);var localText=textLocation.TransformBy(wcsToUcs);
                var min=new Point2d(Math.Min(first.X,second.X),Math.Min(first.Y,second.Y));var max=new Point2d(Math.Max(first.X,second.X),Math.Max(first.Y,second.Y));
                var cloud=BuildCloud(min,max,settings);cloud.Elevation=first.Z;
                var requestedWidth=settings.FixedWidth?settings.FixedWidthValue:Math.Max(55.0,settings.TextHeight*18.0);
                requestedWidth=Math.Max(requestedWidth,MeasureHeaderWidth(doc.Database,tr,data,settings)); // 首行（日期/专业/批注人）不折行
                var margin=TextBoxMargin(settings);
                var text=new MText{Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z),TextHeight=settings.TextHeight,Width=requestedWidth,Contents=FormatText(data,settings),Attachment=AttachmentPoint.BottomLeft};
                ApplyTextStyle(doc.Database,tr,text,settings.TextStyleName);
                MeasureTextBoxFromMText(text,data,settings,out var boxW,out var boxH,out margin);
                text.Location=new Point3d(localText.X+margin,localText.Y+margin,localText.Z);
                var boxEntity=BuildBox(localText,boxW,boxH);boxEntity.Elevation=localText.Z;
                var cloudCorner=ResolveRegionLeaderAnchor(cloud,min,max,settings,localText);var boxCorners=new[]{new Point2d(localText.X,localText.Y),new Point2d(localText.X+boxW,localText.Y),new Point2d(localText.X+boxW,localText.Y+boxH),new Point2d(localText.X,localText.Y+boxH)};var boxCorner=boxCorners.OrderBy(c=>c.GetDistanceTo(cloudCorner)).First();
                var leader=new Polyline();leader.AddVertexAt(0,cloudCorner,0,0,0);leader.AddVertexAt(1,boxCorner,0,0,0);leader.Elevation=localText.Z;
                cloud.TransformBy(ucsToWcs);OrientCloudBulgesForView(doc,cloud);text.TransformBy(ucsToWcs);boxEntity.TransformBy(ucsToWcs);leader.TransformBy(ucsToWcs);
                Add(space,tr,cloud,ids,settings,data.Id,"cloud",CloudColorFor(settings,data.Status),data);Add(space,tr,text,ids,settings,data.Id,"text",TextColorFor(settings,data.Status),data);Add(space,tr,boxEntity,ids,settings,data.Id,"box",BoxColorFor(settings,data.Status),data);Add(space,tr,leader,ids,settings,data.Id,"leader",LeaderColorFor(settings,data.Status),data);

                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                var group = new Group("X-批注 " + data.Number, true);
                groups.SetAt(GroupPrefix + data.Id, group); tr.AddNewlyCreatedDBObject(group, true); group.Append(ids);
                WriteMaster(group, tr, data);
                tr.Commit();
                ArchiveRecord(doc, "创建", data, null);
                return true;
            }
        }

        /// <summary>「基点」批注的基点标识：制图惯例里的<b>目标/基准点符号</b>——一个圆环 + 一个穿过圆心的十字，
        /// 十字臂伸出圆环之外，这样即使缩得很小也能一眼看出"精确的那个点"在哪。
        /// <list type="bullet">
        /// <item>AutoCAD 自带的 <c>CENTERMARK</c>（中心标记）就是"十字形标记"，本符号与它同源；</item>
        /// <item>公差标注体系里的 <i>datum target</i>（基准目标，GB/T 1182 / ISO 1101 / ASME Y14.5）
        /// 用"十字指示的点"表示点终止；</item>
        /// <item>机械制图里表示原点/基准点的符号也是"圆 + 十字"这一族。</item>
        /// </list>
        /// <para><b>刻意不用 <c>Hatch</c> 去填充象限</b>（那是更"标准"的原点符号画法）：填充效果依赖
        /// <c>FILLMODE</c>，且图案查找、环有效性、R12 另存丢填充都是额外的失败面。纯多段线在任何设置下
        /// 都能画、能打印、能随编组一起移动与删除。</para>
        /// <para>返回的实体还没写图层/颜色/XData，由调用方统一走 <c>Add(..., "marker", ...)</c>。</para></summary>
        internal static List<Entity> BuildBasePointMarker(Point2d center, double elevation, double radius)
        {
            var r = Math.Max(radius, 1e-6);
            var arm = r * 1.45; // 十字臂从圆心起算的长度（比半径长约 45%，确保十字明显穿出圆环）
            var parts = new List<Entity>(3);

            // 圆环：两段半圆弧（bulge = tan(90°/4) = 1）组成的闭合两顶点多段线，等价于一个整圆。
            var ring = new Polyline();
            ring.AddVertexAt(0, new Point2d(center.X + r, center.Y), 1.0, 0, 0);
            ring.AddVertexAt(1, new Point2d(center.X - r, center.Y), 1.0, 0, 0);
            ring.Closed = true;
            ring.Elevation = elevation;
            parts.Add(ring);

            var horizontal = new Polyline();
            horizontal.AddVertexAt(0, new Point2d(center.X - arm, center.Y), 0, 0, 0);
            horizontal.AddVertexAt(1, new Point2d(center.X + arm, center.Y), 0, 0, 0);
            horizontal.Elevation = elevation;
            parts.Add(horizontal);

            var vertical = new Polyline();
            vertical.AddVertexAt(0, new Point2d(center.X, center.Y - arm), 0, 0, 0);
            vertical.AddVertexAt(1, new Point2d(center.X, center.Y + arm), 0, 0, 0);
            vertical.Elevation = elevation;
            parts.Add(vertical);

            return parts;
        }

        /// <summary>基点标识的半径：跟随批注字高，并夹住一个"框高的一定比例"的下限，
        /// 保证字高被调得极小时标识仍可见、也不会盖住文字框。
        /// <para><c>internal</c>：放置预览 Jig 要用同一口径算出同尺寸的标识，拖放时看到的符号
        /// 就是落图后的符号。</para></summary>
        internal static double BasePointMarkerRadius(AnnotationSettings settings, double boxHeight)
        {
            var byText = Math.Max(settings.TextHeight, 0.1) * 1.0;
            var byBox = Math.Max(boxHeight, 0.1) * 0.18;
            return Math.Max(byText, byBox);
        }

        /// <summary>把「基点标识」临时画到图上：用户点完基点后<b>先看到标识落在哪儿</b>，然后才弹批注内容窗口。
        /// <para>用<b>真实实体</b>而不是 <c>TransientManager</c>：临时图形要按视口逐个登记、视图一变还得重画，
        /// 而且要给 ZWCAD 分支引一整套 GraphicsInterface 别名（本机没法编译验证），收益不大、失败面反而更多。
        /// 真实实体法只用现成的 <see cref="Polyline"/> API，两端宿主都能编过。</para>
        /// <para><b>用完必须调用 <see cref="EraseTemporaryEntities"/> 擦掉</b>：这个标识没有 XData、也不在任何编组里，
        /// 不会被识别成批注实体，留着就是图纸上的垃圾（而且会和 <see cref="CreateBasePoint"/> 生成的正式标识重在一起）。</para></summary>
        internal static List<ObjectId> DrawTemporaryBasePointMarker(Document doc, Point3d basePoint, AnnotationSettings settings)
        {
            var ids = new List<ObjectId>();
            if (doc == null || doc.IsDisposed) return ids;
            try
            {
                using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    EnsureLayer(doc.Database, tr, settings);
                    var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                    var ucsToWcs = GetUcsMatrix(doc); var wcsToUcs = ucsToWcs.Inverse();
                    var local = basePoint.TransformBy(wcsToUcs);
                    // 此刻还不知道文字框多高（内容还没填），先用"正文字高"当参照——与最终标识同量级，
                    // 用户看到的就是"大概这么大、在哪儿"，尺寸随后续步骤精确化。
                    var radius = BasePointMarkerRadius(settings, 0);
                    foreach (var part in BuildBasePointMarker(new Point2d(local.X, local.Y), local.Z, radius))
                    {
                        part.Layer = EffectiveLayer(settings);
                        part.Color = Color.FromColorIndex(ColorMethod.ByAci, settings.LeaderColor);
                        if (part is Polyline partPoly && settings.LineWidth > 0) SafeSetConstantWidth(partPoly, settings.LineWidth);
                        part.TransformBy(ucsToWcs);
                        ids.Add(space.AppendEntity(part));
                        tr.AddNewlyCreatedDBObject(part, true);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("DrawTemporaryBasePointMarker", ex.Message);
            }
            return ids;
        }

        /// <summary>擦掉 <see cref="DrawTemporaryBasePointMarker"/> 画的临时标识。绝不抛异常——
        /// 它多半跑在 <c>finally</c> 里，抛出来会盖掉真正的失败原因。</summary>
        internal static void EraseTemporaryEntities(Document doc, List<ObjectId> ids)
        {
            if (ids == null || ids.Count == 0) return;
            try
            {
                if (doc != null && !doc.IsDisposed)
                {
                    using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in ids)
                        {
                            if (!id.IsValid) continue;
                            if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity e && !e.IsErased) e.Erase();
                        }
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex) { PluginLog.Warning("EraseTemporaryEntities", ex.Message); }
            finally { ids.Clear(); }
        }

        /// <summary>「基点」批注落图：<b>不画云线</b>，只有「基点 → 文字框」一条直线，加文字与文字框。
        /// <para>文字/边框的口径与 <see cref="Create"/> 完全一致（同一个 requestedWidth 公式 + MeasureTextBoxFromMText），
        /// 只是把"云线轮廓锚点"换成用户指定的基点；引线另一端仍取离基点最近的框角。</para>
        /// <para>基点处额外画一个 <see cref="BuildBasePointMarker">目标符号</see>（圆 + 十字），
        /// 让"引线到底接在图上哪个点"可见——它也是编组成员（XData 角色 <c>marker</c>），
        /// 随批注一起移动/删除，复制修复与合并逻辑对未知角色本来就按"随批注处理"。</para>
        /// <para>XData 角色沿用 text / box / leader（标识另加 marker），编组与主数据写入方式不变，
        /// 因此批注列表、移动、删除、双击编辑等既有功能无需特判——只是这类批注编组内没有 "cloud" 实体。</para></summary>
        public static bool CreateBasePoint(
            Document doc,
            AnnotationData data,
            AnnotationSettings settings,
            Point3d basePoint,
            Point3d textLocation,
            ObjectId spaceId = default(ObjectId))
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(doc.Database, tr); EnsureLayer(doc.Database, tr, settings, data);
                var space = (BlockTableRecord)tr.GetObject(spaceId.IsNull ? doc.Database.CurrentSpaceId : spaceId, OpenMode.ForWrite);
                var ids = new ObjectIdCollection();

                var ucsToWcs = GetUcsMatrix(doc); var wcsToUcs = ucsToWcs.Inverse();
                var localAnchor = basePoint.TransformBy(wcsToUcs); var localText = textLocation.TransformBy(wcsToUcs);
                var requestedWidth = settings.FixedWidth ? settings.FixedWidthValue : Math.Max(55.0, settings.TextHeight * 18.0);
                requestedWidth = Math.Max(requestedWidth, MeasureHeaderWidth(doc.Database, tr, data, settings)); // 首行（日期/专业/批注人）不折行
                var margin = TextBoxMargin(settings);
                var text = new MText { Location = new Point3d(localText.X + margin, localText.Y + margin, localText.Z), TextHeight = settings.TextHeight, Width = requestedWidth, Contents = FormatText(data, settings), Attachment = AttachmentPoint.BottomLeft };
                ApplyTextStyle(doc.Database, tr, text, settings.TextStyleName);
                MeasureTextBoxFromMText(text, data, settings, out var boxW, out var boxH, out margin);
                text.Location = new Point3d(localText.X + margin, localText.Y + margin, localText.Z);
                var boxEntity = BuildBox(localText, boxW, boxH); boxEntity.Elevation = localText.Z;

                var anchor = new Point2d(localAnchor.X, localAnchor.Y);
                var boxCorners = new[] { new Point2d(localText.X, localText.Y), new Point2d(localText.X + boxW, localText.Y), new Point2d(localText.X + boxW, localText.Y + boxH), new Point2d(localText.X, localText.Y + boxH) };
                var boxCorner = boxCorners.OrderBy(c => c.GetDistanceTo(anchor)).First();
                var leader = new Polyline();
                leader.AddVertexAt(0, anchor, 0, 0, 0);
                leader.AddVertexAt(1, boxCorner, 0, 0, 0);
                leader.Elevation = localText.Z;

                // 基点标识（圆 + 十字目标符号）：画在用户指定的那个点上，随编组一起移动/删除。
                // 尺寸跟随批注字高，保证在不同出图比例下与文字框观感一致。
                var markerRadius = BasePointMarkerRadius(settings, boxH);
                var markerParts = BuildBasePointMarker(anchor, localText.Z, markerRadius);

                text.TransformBy(ucsToWcs); boxEntity.TransformBy(ucsToWcs); leader.TransformBy(ucsToWcs);
                Add(space, tr, text, ids, settings, data.Id, "text", TextColorFor(settings, data.Status), data);
                Add(space, tr, boxEntity, ids, settings, data.Id, "box", BoxColorFor(settings, data.Status), data);
                Add(space, tr, leader, ids, settings, data.Id, "leader", LeaderColorFor(settings, data.Status), data);
                // 标识走引线同色（都是"指着某个位置"的辅助几何），线宽按设置统一。
                foreach (var markerPart in markerParts)
                {
                    markerPart.TransformBy(ucsToWcs);
                    Add(space, tr, markerPart, ids, settings, data.Id, "marker", LeaderColorFor(settings, data.Status), data);
                }

                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                var group = new Group("X-批注 " + data.Number, true);
                groups.SetAt(GroupPrefix + data.Id, group); tr.AddNewlyCreatedDBObject(group, true); group.Append(ids);
                WriteMaster(group, tr, data);
                tr.Commit();
                ArchiveRecord(doc, "创建", data, null);
                return true;
            }
        }

        /// <summary>从实体反向查找批注数据（通过 XData → 编组 → XRecord 链路）。
        /// 编组链路失败时（批注被 COPY/复制粘贴后编组不跟随实体）回退读取实体 XData 内嵌数据分片。</summary>
        public static bool TryReadFromEntity(Transaction tr, Entity entity, out AnnotationData data)
        {
            data = null;
            if (TryGetId(entity, out var id))
            {
                var db = entity.Database; var groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
                var name = GroupPrefix + id; if (groups.Contains(name))
                {
                    var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForRead);
                    if (Contains(group, entity.ObjectId) && TryReadMaster(group, tr, out data)) return true;
                }
            }
            return TryReadEmbedded(entity, out data);
        }

        /// <summary>从实体自身 XData 中还原批注数据（复制/粘贴后的批注编组不跟随实体，只能靠内嵌数据识别）。</summary>
        private static bool TryReadEmbedded(Entity entity, out AnnotationData data)
        {
            data = null; var rb = entity.GetXDataForApplication(AnnotationCodec.AppName); if (rb == null) return false;
            var chunks = rb.AsArray().Where(v => v.TypeCode == (int)DxfCode.ExtendedDataAsciiString).Select(v => v.Value as string);
            return AnnotationCodec.TryDecodeChunks(chunks, out data);
        }

        /// <summary>判断实体与编组链路是否完整（用于复制后自动修复的触发判断）。</summary>
        internal static bool IsGroupLinked(Transaction tr, Entity entity)
        {
            if (!TryGetId(entity, out var id)) return false;
            var groups = (DBDictionary)tr.GetObject(entity.Database.GroupDictionaryId, OpenMode.ForRead);
            var name = GroupPrefix + id; if (!groups.Contains(name)) return false;
            var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForRead);
            return Contains(group, entity.ObjectId);
        }

        /// <summary>更新批注：刷新文字内容和边框尺寸，保留编组关联。recordHistory=false 时不写改动留痕（供批量套用新版式使用）。</summary>
        public static bool Update(Document doc, ObjectId entityId, AnnotationData data, bool recordHistory = true)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (selected == null || !TryGetId(selected, out var id) || id != data.Id) return false;
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var groupName = GroupPrefix + id; if (!groups.Contains(groupName)) return false;
                var group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForWrite);
                if (!Contains(group, entityId)) return false;
                TryReadMaster(group, tr, out var oldData); // 修改前留底，用于生成变更明细
                var existingSettings=SettingsForExisting(data);
                // 老数据（或旧版「基点」流程画的批注）没记"图面实测字高"，先从编组里的文字实体反测回来，
                // 否则下面的 FormatText 会按设置文件里的"打印 mm"写出内联字高码，编辑一次正文就被压小。
                AdoptMeasuredTextHeights(tr, group, data, existingSettings);
                EnsureLayer(doc.Database,tr,existingSettings,data);
                var targetLayer=EffectiveLayer(existingSettings,data);
                MText changedText=null;Polyline box=null;var leaders=new List<Polyline>();
                // 状态色：整条批注统一换色（「已回复」/「已完成」）。状态退回「待处理」时按角色恢复各元素基础色，
                // 所以每个成员实体都要重算颜色 —— 不能只改文字（那是"状态只让文字变色"的旧行为，
                // 与"改一次状态色就把整条批注所有元素都改掉"的约定不符）。
                var statusColor=StatusColorOverride(existingSettings,data.Status);
                var colorize=existingSettings.StatusColorize;
                foreach (ObjectId oid in group.GetAllEntityIds())
                {
                    if(!oid.IsValid||oid.IsErased)continue;
                    var entity = tr.GetObject(oid, OpenMode.ForWrite, false) as Entity;
                    if(entity==null)continue;
                    entity.Layer=targetLayer;
                    var role = TryGetRole(entity, out var roleName) ? roleName : null;
                    if (entity is MText text)
                    {
                        text.Contents=FormatText(data,existingSettings);
                        // 首行（日期/专业/批注人）不折行：老批注创建时文字宽度不够的，编辑时补足。
                        try{var need=MeasureHeaderWidth(doc.Database,tr,data,existingSettings);if(need>text.Width)text.Width=need;}
                        catch(System.Exception ex){PluginLog.Warning("Update.HeaderWidth",ex.Message);}
                        changedText=text;
                    }
                    else if(entity is Polyline poly)
                    {
                        if(role=="box")box=poly;
                        else if(role=="leader")leaders.Add(poly);
                    }
                    if (role == null && entity is MText) role = "text"; // 老数据兜底：文字实体按 text 角色上色
                    if (statusColor.HasValue)
                    {
                        // 该状态有专属颜色：整条批注的所有元素统一换成它
                        entity.Color=Color.FromColorIndex(ColorMethod.ByAci,statusColor.Value);
                    }
                    else if (!colorize)
                    {
                        // 没开"按状态整条着色"：保持旧行为，只把文字颜色对齐当前设置
                        if (entity is MText) entity.Color=Color.FromColorIndex(ColorMethod.ByAci,TextColorFor(existingSettings,data.Status));
                    }
                    else if (colorize && HasStatusColor(existingSettings, entity)
                             && (role == "cloud" || role == "box" || role == "leader" || role == "marker" || role == "text"))
                    {
                        // 开了整条着色、但当前状态没有专属色（「待处理」）：把上一个状态留下的颜色清掉，恢复基础色。
                        // 只有"当前颜色确实等于某个状态色"的实体才动 —— 否则会把用户手工在特性面板里改过的颜色冲掉。
                        entity.Color=Color.FromColorIndex(ColorMethod.ByAci,BaseColorForRole(existingSettings,role));
                    }
                }
                if(changedText!=null&&box!=null&&box.NumberOfVertices>=4)
                {
                    var origin=box.GetPoint2dAt(0);var xVector=box.GetPoint2dAt(1)-origin;var yVector=box.GetPoint2dAt(3)-origin;
                    var xLength=Math.Max(xVector.Length,1e-9);var yLength=Math.Max(yVector.Length,1e-9);var xAxis=xVector/xLength;var yAxis=yVector/yLength;
                    MeasureTextBoxFromMText(changedText,data,existingSettings,out var width,out var height,out var margin);
                    // 文字框保持「顶边」不动、向下伸缩：批注的惯例是云线在上、文字框在下，
                    // 原来固定左下角向上生长，内容行数一变多（套用新版式 / 追加回复）框顶就会顶进云线里。
                    var newOrigin=origin+yAxis*(yLength-height);
                    box.SetPointAt(0,newOrigin);
                    box.SetPointAt(1,newOrigin+xAxis*width);
                    box.SetPointAt(2,newOrigin+xAxis*width+yAxis*height);
                    box.SetPointAt(3,newOrigin+yAxis*height);
                    // 文字的左下角就是文字框的左下角，框整体下移多少，文字就要跟着下移多少
                    var heightDelta=height-yLength;
                    if(Math.Abs(heightDelta)>1e-9)
                    {
                        var down=box.GetPoint3dAt(0)-box.GetPoint3dAt(3);
                        if(down.Length>1e-9)changedText.TransformBy(Matrix3d.Displacement(down*(heightDelta/down.Length)));
                    }
                    var corners=Enumerable.Range(0,box.NumberOfVertices).Select(box.GetPoint2dAt).ToArray();
                    foreach(var leader in leaders)
                    {
                        if(leader.NumberOfVertices<2)continue;
                        var anchor=leader.GetPoint2dAt(0);leader.SetPointAt(leader.NumberOfVertices-1,corners.OrderBy(c=>c.GetDistanceTo(anchor)).First());
                    }
                }
                group.Description = "X-批注 " + data.Number; WriteMaster(group, tr, data); tr.Commit();
                if (recordHistory) ArchiveRecord(doc, "修改", data, AnnotationHistoryStore.Diff(oldData, data));
                return true;
            }
        }

        /// <summary>按当前设置重写全图批注的文字版式（同时重算文字框与引线端点），供旧图套用新版式；不写留痕。
        /// 返回实际刷新的批注条数。</summary>
        public static int RefreshText(Document doc)
        {
            if (doc == null || doc.IsDisposed) return 0;
            var targets = new List<KeyValuePair<ObjectId, AnnotationData>>();
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                    foreach (var entry in groups)
                    {
                        var name = entry.Key as string;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix, StringComparison.Ordinal)) continue;
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                        if (!TryReadMaster(group, tr, out var data)) continue;
                        ObjectId firstId = ObjectId.Null;
                        foreach (ObjectId oid in group.GetAllEntityIds()) { if (oid.IsValid && !oid.IsErased) { firstId = oid; break; } }
                        if (firstId.IsNull) continue;
                        targets.Add(new KeyValuePair<ObjectId, AnnotationData>(firstId, data));
                    }
                }
            }
            catch (System.Exception ex) { PluginLog.Error("RefreshText.Scan", ex); return 0; }

            var done = 0;
            foreach (var target in targets)
            {
                try { if (Update(doc, target.Key, target.Value, false)) done++; }
                catch (System.Exception ex) { PluginLog.Warning("RefreshText.Update", ex.Message); }
            }
            return done;
        }

        // ==================== 格式刷 ====================

        /// <summary>格式刷的样式值：先从源批注实测填充，对话框里可调整，再连续刷到其他批注。
        /// 内容、编号、日期、批注人等业务数据不参与，只刷"外观"。</summary>
        internal sealed class FormatBrushSpec
        {
            public double HeaderHeight;          // 首行字高（图面实测值）
            public double SecondLineHeight;      // 次行字高（图面实测值）
            public double TextHeight;            // 批注字高（图面实测值）
            public string TextStyleName;         // 文字样式
            public string CloudStyle;            // 等宽 / 渐变
            public double CloudLineWidth;        // 云线线宽（图面实测值；渐变＝锥形最粗端读不出来时回退当前设置的线宽）
            public string LayerName;             // 目标图层（默认照抄源批注的实际图层）
            public short CloudColor;
            public short LeaderColor;
            public short TextColor;
            public List<string> LayerNames = new List<string>();     // 对话框下拉：图中已有图层
            public List<string> TextStyleNames = new List<string>(); // 对话框下拉：图中已有文字样式
        }

        /// <summary>从源批注实测格式（字高 / 文字样式 / 云线样式 / 图层 / 颜色），供格式刷对话框预填。非批注返回 null。</summary>
        internal static FormatBrushSpec BuildFormatSpec(Document doc, ObjectId entityId)
        {
            if (doc == null || entityId.IsNull) return null;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(entityId, OpenMode.ForRead, false) is Entity entity)) return null;
                if (!TryReadFromEntity(tr, entity, out var data)) return null;

                var global = SettingsStore.Load();
                var spec = new FormatBrushSpec
                {
                    HeaderHeight = data.RenderHeaderHeight > 0 ? data.RenderHeaderHeight : global.HeaderHeight,
                    SecondLineHeight = data.RenderSecondLineHeight > 0 ? data.RenderSecondLineHeight : global.SecondLineHeight,
                    TextHeight = data.RenderTextHeight > 0 ? data.RenderTextHeight : global.TextHeight,
                    TextStyleName = global.TextStyleName,
                    CloudStyle = global.CloudStyle,
                    CloudLineWidth = global.LineWidth,
                    LayerName = EffectiveLayer(global, data),
                    CloudColor = global.CloudColor,
                    LeaderColor = global.LeaderColor,
                    TextColor = global.TextColor,
                };
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var groupName = GroupPrefix + data.Id;
                if (groups.Contains(groupName) && tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead) is Group group)
                {
                    var existing = ProbeExistingStyle(tr, group);
                    if (!string.IsNullOrEmpty(existing.CloudStyle)) spec.CloudStyle = existing.CloudStyle;
                    if (existing.CloudColor.HasValue) spec.CloudColor = existing.CloudColor.Value;
                    // 云线线宽也照抄源批注（锥形云线读不出全局宽度时保留设置里的线宽）。
                    if (existing.HasCloud && existing.CloudWidth > 0) spec.CloudLineWidth = existing.CloudWidth;
                    if (existing.LeaderColor.HasValue) spec.LeaderColor = existing.LeaderColor.Value;
                    if (!string.IsNullOrEmpty(existing.Layer)) spec.LayerName = existing.Layer;
                    // 文字颜色从编组内的 MText 实测（可能与全局设置不同——之前被单独改过色）。
                    foreach (ObjectId oid in group.GetAllEntityIds())
                    {
                        if (!oid.IsValid || oid.IsErased) continue;
                        if (tr.GetObject(oid, OpenMode.ForRead, false) is MText mtext && mtext.Color.ColorMethod == ColorMethod.ByAci)
                        {
                            spec.TextColor = mtext.Color.ColorIndex;
                            break;
                        }
                    }
                }
                // 下拉候选：图中已有图层与文字样式
                var layers = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                foreach (var entry in layers)
                {
                    if (tr.GetObject(entry, OpenMode.ForRead) is LayerTableRecord l && l.Name != "0") spec.LayerNames.Add(l.Name);
                }
                spec.LayerNames.Sort(StringComparer.CurrentCulture);
                if (!spec.LayerNames.Contains(spec.LayerName)) spec.LayerNames.Insert(0, spec.LayerName);
                var styles = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (var entry in styles)
                {
                    if (tr.GetObject(entry, OpenMode.ForRead) is TextStyleTableRecord ts) spec.TextStyleNames.Add(ts.Name);
                }
                spec.TextStyleNames.Sort(StringComparer.CurrentCulture);
                if (!spec.TextStyleNames.Contains(spec.TextStyleName)) spec.TextStyleNames.Insert(0, spec.TextStyleName);
                return spec;
            }
        }

        /// <summary>把格式刷对话框的值应用到目标批注：
        /// ① 三项字高写入 Render* 后走 <see cref="Update"/>（重排文字、重算框尺寸与引线端点，首行仍保证不折行）；
        /// ② 图层 / 文字样式 / 云线与引线与文字颜色 / 云线样式（等宽↔渐变，只换弧瓣模式、保留原弧向）统一覆盖。
        /// 内容、编号等业务数据不变。整个操作支持 UNDO。</summary>
        internal static bool ApplyFormatBrush(Document doc, ObjectId entityId, FormatBrushSpec spec)
        {
            if (doc == null || entityId.IsNull || spec == null) return false;
            ObjectId masterEntity;
            AnnotationData data;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(entityId, OpenMode.ForRead, false) is Entity entity)) return false;
                if (!TryReadFromEntity(tr, entity, out data)) return false;
                masterEntity = entity.ObjectId;
            }
            // ① 字高 → Render* → Update 重排（复用编辑批注的整条重排管线）。
            data.RenderTextHeight = Math.Max(0.1, spec.TextHeight);
            data.RenderHeaderHeight = Math.Max(0.1, spec.HeaderHeight);
            data.RenderSecondLineHeight = Math.Max(0.1, spec.SecondLineHeight);
            if (!Update(doc, masterEntity, data, false)) return false;
            // ② 图层 / 颜色 / 样式覆盖。
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(masterEntity, OpenMode.ForRead) as Entity;
                if (selected == null || !TryGetId(selected, out var id)) return false;
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var groupName = GroupPrefix + id;
                if (!groups.Contains(groupName)) return false;
                var group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead);
                // 确保目标图层存在：用"不带日期/人名后缀"的临时设置，保证图层名与对话框里的一致。
                var layerSettings = SettingsStore.Load();
                layerSettings.LayerName = spec.LayerName;
                layerSettings.LayerAppendDate = false;
                layerSettings.LayerAppendName = false;
                EnsureLayer(doc.Database, tr, layerSettings);
                // 状态色（已回复/已完成）优先于格式刷的颜色：格式刷只负责"样子"，不该把状态色抹掉——
                // 否则刷一下"已回复"的批注就从黄色变回洋红了，状态一眼看不出来。
                var statusColor = StatusColorOverride(layerSettings, data.Status);
                foreach (ObjectId oid in group.GetAllEntityIds())
                {
                    if (!oid.IsValid || oid.IsErased) continue;
                    if (!(tr.GetObject(oid, OpenMode.ForWrite, false) is Entity e)) continue;
                    e.Layer = spec.LayerName;
                    if (e is MText text)
                    {
                        ApplyTextStyle(doc.Database, tr, text, spec.TextStyleName);
                        if (!statusColor.HasValue) text.Color = Color.FromColorIndex(ColorMethod.ByAci, spec.TextColor);
                    }
                    else if (e is Polyline poly && TryGetRole(poly, out var role))
                    {
                        if (role == "cloud")
                        {
                            if (!statusColor.HasValue) poly.Color = Color.FromColorIndex(ColorMethod.ByAci, spec.CloudColor);
                            RestyleCloudBulges(poly, spec.CloudStyle, spec.CloudLineWidth);
                        }
                        else if (role == "leader") { if (!statusColor.HasValue) poly.Color = Color.FromColorIndex(ColorMethod.ByAci, spec.LeaderColor); }
                        else if (role == "box") { if (!statusColor.HasValue) poly.Color = Color.FromColorIndex(ColorMethod.ByAci, spec.TextColor); }
                    }
                }
                tr.Commit();
            }
            AnnotationHistoryStore.Record(doc, "格式刷", data, null);
            return true;
        }

        /// <summary>按"等宽/渐变"统一云线弧瓣：两种样式的弧瓣几何完全一致（顶点等距、bulge 全 0.55，只保留原弧向符号）。
        /// 描边随样式重建：渐变＝逐段顶点宽度 <c>0 → lineWidth</c>（并把全局宽度置 0——顶点 0 宽度那端会回退用
        /// 全局宽度，不清零渐变会被抹平）；等宽＝顶点宽度全 0 + 全局宽度 lineWidth。
        /// 多段线没有"改写已有顶点宽度"的接口，用"先追加新顶点、再从头部删旧的"原地重建（顶点顺序与弧向不变）。</summary>
        private static void RestyleCloudBulges(Polyline cloud, string style, double lineWidth)
        {
            var n = cloud.NumberOfVertices; if (n < 4 || string.IsNullOrEmpty(style)) return;
            double sign = 1;
            for (var i = 0; i < n; i++) { var b = cloud.GetBulgeAt(i); if (Math.Abs(b) > 1e-9) { sign = b >= 0 ? 1 : -1; break; } }
            var tapered = style == "渐变" && lineWidth > 0;
            var pts = new List<Point2d>(n); var bulges = new double[n];
            for (var i = 0; i < n; i++) { pts.Add(cloud.GetPoint2dAt(i)); bulges[i] = cloud.GetBulgeAt(i); }
            for (var i = 0; i < n; i++) cloud.AddVertexAt(n + i, pts[i], bulges[i], 0, tapered ? lineWidth : 0);
            for (var i = 0; i < n; i++) cloud.RemoveVertexAt(0);
            if (tapered) SafeSetConstantWidth(cloud, 0);                        // 渐变：全局宽度必须 0
            else if (lineWidth > 0) SafeSetConstantWidth(cloud, lineWidth);     // 等宽：全局宽度＝云线线宽
            // lineWidth<=0（源批注没设线宽）：保留目标云线原有的全局宽度不动
            SetBulgePattern(cloud, style, sign);
        }

        /// <summary>拾取图面上的图号文字：单行文字 / 多行文字 / 块属性（AttributeReference）；
        /// 点到属性块时，块里恰好一个非空属性直接用；有多个属性文字时<b>以鼠标点中的那一个为准</b>
        /// （按拾取点是否落在该属性的图面范围内判断，落在范围外再提示直接点选属性本身）。
        /// 用户取消（Esc/回车）时返回 null；选中非文字对象会提示并让用户重选。</summary>
        public static string PickText(Document doc)
        {
            if (doc == null || doc.IsDisposed) return null;
            while (true)
            {
                var result = doc.Editor.GetEntity("\n拾取图面上的图号文字（单行文字 / 多行文字 / 块属性）: ");
                if (result.Status != PromptStatus.OK) return null;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(result.ObjectId, OpenMode.ForRead, false);
                    if (entity is DBText single) return ExtractDrawingNo(single.TextString);
                    if (entity is MText multi) return ExtractDrawingNo(PlainText(multi.Contents));
                    if (entity is AttributeReference attribute) return ExtractDrawingNo(attribute.TextString);
                    if (entity is BlockReference block)
                    {
                        var values = new List<AttributeReference>();
                        foreach (ObjectId attId in block.AttributeCollection)
                        {
                            if (!attId.IsValid || attId.IsErased) continue;
                            if (!(tr.GetObject(attId, OpenMode.ForRead, false) is AttributeReference att)) continue;
                            if (!string.IsNullOrWhiteSpace(att.TextString)) values.Add(att);
                        }
                        if (values.Count == 1) return ExtractDrawingNo(values[0].TextString);
                        if (values.Count > 1)
                        {
                            var hit = AttributeAt(values, result.PickedPoint);
                            if (hit != null) return ExtractDrawingNo(hit.TextString);
                            doc.Editor.WriteMessage("\n该块包含 " + values.Count + " 个属性文字，请直接点选其中作为图号的那个属性文字。");
                            continue;
                        }
                    }
                }
                doc.Editor.WriteMessage("\n所选对象不是文字、多行文字或块属性，请重新选择。");
            }
        }

        /// <summary>多个属性文字时判断鼠标点中的是哪一个：先看图面范围是否罩住拾取点（留 20% 字高的容差），
        /// 都没罩住则退回"中心离拾取点最近且在一定范围内"的那一个；仍判不出返回 null（由调用处再提示重选）。</summary>
        private static AttributeReference AttributeAt(IList<AttributeReference> attributes, Point3d pickPoint)
        {
            if (pickPoint == Point3d.Origin) return null;
            AttributeReference nearest = null; var nearestCenter = double.MaxValue; var maxHeight = 1e-6;
            foreach (var att in attributes)
            {
                try
                {
                    var ext = att.GeometricExtents;
                    var tol = Math.Max(att.Height, 1e-6) * 0.2;
                    if (att.Height > maxHeight) maxHeight = att.Height;
                    if (pickPoint.X >= ext.MinPoint.X - tol && pickPoint.X <= ext.MaxPoint.X + tol &&
                        pickPoint.Y >= ext.MinPoint.Y - tol && pickPoint.Y <= ext.MaxPoint.Y + tol) return att;
                    var center = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, pickPoint.Z);
                    var distance = center.DistanceTo(pickPoint);
                    if (distance < nearestCenter) { nearestCenter = distance; nearest = att; }
                }
                catch (System.Exception ex) { PluginLog.Warning("PickText.Extents", ex.Message); }
            }
            // 拾取点离任何属性文字都很远（点在了块的其他图元上）时不猜，交给调用处提示重选。
            return nearestCenter <= maxHeight * 3 ? nearest : null;
        }

        /// <summary>从拾取到的文字中取出图号：取首个非空行，并剥掉"图号：/图号:/图号 "这类标签前缀。</summary>
        private static string ExtractDrawingNo(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var first = raw.Replace("\r\n", "\n").Replace('\r', '\n')
                           .Split('\n')
                           .Select(line => line.Trim())
                           .FirstOrDefault(line => line.Length > 0);
            if (string.IsNullOrEmpty(first)) return "";
            foreach (var label in new[] { "图号", "图纸编号" })
            {
                if (!first.StartsWith(label, StringComparison.Ordinal)) continue;
                var rest = first.Substring(label.Length).TrimStart();
                if (rest.Length == 0) return first;
                if (rest[0] == '：' || rest[0] == ':') rest = rest.Substring(1).Trim();
                return rest.Length > 0 ? rest : first;
            }
            return first;
        }

        /// <summary>把多行文字（MText）的内容还原为纯文本：\P 转换行、\~ 转空格、剥掉 \H3;\A1;\C1; 之类的格式码与大括号。</summary>
        private static string PlainText(string contents)
        {
            if (string.IsNullOrEmpty(contents)) return "";
            var sb = new StringBuilder(contents.Length);
            for (var i = 0; i < contents.Length; i++)
            {
                var c = contents[i];
                if (c == '{' || c == '}') continue;
                if (c != '\\') { sb.Append(c); continue; }
                if (i + 1 >= contents.Length) break;
                var next = contents[i + 1];
                if (next == 'P' || next == 'p') { sb.Append('\n'); i++; continue; }
                if (next == '~') { sb.Append(' '); i++; continue; }
                if (next == '\\') { sb.Append('\\'); i++; continue; }
                var end = contents.IndexOf(';', i + 1);
                if (end < 0) break; // 没有分号，视为普通文本结尾
                i = end;
            }
            return sb.ToString();
        }

        /// <summary>移动批注的文字、文字框和引线，云线保持原位。</summary>
        public static bool MoveAnnotation(Document doc, ObjectId entityId)
        {
            var previewEntities = new List<Entity>();
            var previewLeaders = new List<Polyline>();
            Point3d basePoint;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null || !TryGetId(selected, out var id))
                {
                    return false;
                }

                var groups = (DBDictionary)tr.GetObject(
                    doc.Database.GroupDictionaryId,
                    OpenMode.ForRead);
                var groupName = GroupPrefix + id;
                if (!groups.Contains(groupName))
                {
                    return false;
                }

                var group = (Group)tr.GetObject(
                    groups.GetAt(groupName),
                    OpenMode.ForRead);
                Polyline box = null;

                foreach (ObjectId memberId in group.GetAllEntityIds())
                {
                    if (!memberId.IsValid || memberId.IsErased)
                    {
                        continue;
                    }

                    var entity = tr.GetObject(
                        memberId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null || !TryGetRole(entity, out var role))
                    {
                        continue;
                    }

                    if (role == "text" || role == "box")
                    {
                        previewEntities.Add(entity.Clone() as Entity);
                        if (role == "box")
                        {
                            box = entity as Polyline;
                        }
                    }
                    else if (role == "leader" && entity is Polyline leader)
                    {
                        previewLeaders.Add(leader.Clone() as Polyline);
                    }
                }

                if (box == null || box.NumberOfVertices == 0)
                {
                    DisposeEntities(previewEntities);
                    DisposeEntities(previewLeaders);
                    return false;
                }

                basePoint = box.GetPoint3dAt(0);
            }

            try
            {
                var jig = new MoveAnnotationPreviewJig(
                    basePoint,
                    previewEntities.Where(entity => entity != null),
                    previewLeaders.Where(leader => leader != null));
                var result = doc.Editor.Drag(jig);
                if (result.Status != PromptStatus.OK)
                {
                    return true;
                }

                var displacement = jig.Current - basePoint;
                if (displacement.Length <= 1e-8)
                {
                    return true;
                }

                ApplyAnnotationMove(doc, entityId, displacement);
                return true;
            }
            finally
            {
                DisposeEntities(previewEntities);
                DisposeEntities(previewLeaders);
            }
        }

        private static void ApplyAnnotationMove(
            Document doc,
            ObjectId entityId,
            Vector3d displacement)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null || !TryGetId(selected, out var id))
                {
                    return;
                }

                var groups = (DBDictionary)tr.GetObject(
                    doc.Database.GroupDictionaryId,
                    OpenMode.ForRead);
                var groupName = GroupPrefix + id;
                if (!groups.Contains(groupName))
                {
                    return;
                }

                var group = (Group)tr.GetObject(
                    groups.GetAt(groupName),
                    OpenMode.ForRead);
                TryReadMaster(group, tr, out var moveData); // 移动前留底，写入历史留痕
                var transform = Matrix3d.Displacement(displacement);

                foreach (ObjectId memberId in group.GetAllEntityIds())
                {
                    if (!memberId.IsValid || memberId.IsErased)
                    {
                        continue;
                    }

                    var entity = tr.GetObject(
                        memberId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (entity == null || !TryGetRole(entity, out var role))
                    {
                        continue;
                    }

                    if (role == "text" || role == "box")
                    {
                        entity.TransformBy(transform);
                    }
                    else if (role == "leader" && entity is Polyline leader)
                    {
                        if (leader.NumberOfVertices < 2)
                        {
                            continue;
                        }

                        var anchor = leader.GetPoint2dAt(0);
                        leader.TransformBy(transform);
                        leader.SetPointAt(0, anchor);
                    }
                }

                tr.Commit();
                AnnotationHistoryStore.Record(doc, "移动", moveData, null);
            }
        }

        private static void DisposeEntities(IEnumerable<Entity> entities)
        {
            foreach (var entity in entities)
            {
                entity?.Dispose();
            }
        }

        /// <summary>删除整个批注编组（云线+引线+文字+边框全部擦除），支持 UNDO。</summary>
        public static bool Delete(Document doc, ObjectId entityId)
        {
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (selected == null || !TryGetId(selected, out var id)) return false;
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite); var name = GroupPrefix + id;
                if (!groups.Contains(name)) return false;
                var group = (Group)tr.GetObject(groups.GetAt(name), OpenMode.ForWrite);
                if (!Contains(group, entityId)) return false;
                TryReadMaster(group, tr, out var delData); // 删除前留底，写入历史留痕
                foreach (ObjectId oid in group.GetAllEntityIds()) { if(!oid.IsValid||oid.IsErased)continue;var e = tr.GetObject(oid, OpenMode.ForWrite, false); if (e != null && !e.IsErased) e.Erase(); }
                groups.Remove(name);group.Erase();tr.Commit();
                AnnotationHistoryStore.Record(doc, "删除", delData, null);
                return true;
            }
        }

        /// <summary>批注汇总用的轻量条目：业务数据 + 文字高度 + 批注框角点（WCS）。</summary>
        private sealed class SummaryItem
        {
            public AnnotationData Data;
            public double TextHeight;
            public Point2d[] BoxCorners;
            public double BoxElevation;
        }

        /// <summary>批注汇总：框选批注（窗口/窗交均可）后，在用户点击位置绘制"日期+内容"汇总表，并从各批注框引线指向表位。</summary>
        public static void SummarizeAnnotations(Document doc)
        {
            var ed=doc.Editor;
            var options=new PromptSelectionOptions{MessageForAdding="\n框选要汇总的批注（窗口/窗交均可）: "};
            var filter=new SelectionFilter(new[]{new TypedValue((int)DxfCode.ExtendedDataRegAppName,AnnotationCodec.AppName)});
            var selection=ed.GetSelection(options,filter);
            if(selection.Status!=PromptStatus.OK||selection.Value==null||selection.Value.Count==0){ed.WriteMessage("\n未选择任何批注。");return;}

            // 一个批注的多个子实体可能同时被选中，按批注 Id 去重后再读取数据。
            var items=new List<SummaryItem>();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForRead);
                foreach(SelectedObject selected in selection.Value)
                {
                    if(selected==null)continue;
                    var entity=tr.GetObject(selected.ObjectId,OpenMode.ForRead,false) as Entity;
                    if(entity==null)continue;
                    // 编组链路失败时（复制/粘贴后的批注）回退读取实体内嵌数据。
                    if(!TryReadFromEntity(tr,entity,out var data)||!seen.Add(data.Id))continue;
                    var groupName=GroupPrefix+data.Id;Group group=null;
                    if(groups.Contains(groupName))group=(Group)tr.GetObject(groups.GetAt(groupName),OpenMode.ForRead);
                    Polyline box=null;var measuredHeight=0.0;
                    if(group!=null)foreach(ObjectId member in group.GetAllEntityIds())
                    {
                        if(!member.IsValid||member.IsErased)continue;
                        var memberEntity=tr.GetObject(member,OpenMode.ForRead,false) as Entity;
                        if(memberEntity==null)continue;
                        if(box==null&&memberEntity is Polyline poly&&TryGetRole(memberEntity,out var role)&&role=="box")box=poly;
                        else if(measuredHeight<=0&&memberEntity is MText mtext)measuredHeight=mtext.TextHeight;
                    }
                    items.Add(new SummaryItem
                    {
                        Data=data,
                        TextHeight=data.RenderTextHeight>0?data.RenderTextHeight:measuredHeight,
                        BoxCorners=box!=null&&box.NumberOfVertices>=4
                            ?Enumerable.Range(0,box.NumberOfVertices).Select(i=>box.GetPoint2dAt(i)).ToArray()
                            :null,
                        BoxElevation=box!=null?box.Elevation:0
                    });
                }
            }
            if(items.Count==0){ed.WriteMessage("\n所选对象中没有有效的 X-批注。");return;}
            items=items.OrderBy(x=>x.Data.Number,StringComparer.OrdinalIgnoreCase).ToList();

            var placement=ed.GetPoint("\n指定批注汇总表位置: ");
            if(placement.Status!=PromptStatus.OK)return;

            var settings=SettingsStore.Load();
            // 汇总表文字高度 = 全部选中批注文字高度的平均值。
            var textHeight=items.Average(x=>x.TextHeight>0?x.TextHeight:settings.TextHeight);
            CreateSummaryTable(doc,settings,items,placement.Value,textHeight);
            ed.WriteMessage($"\n已生成 {items.Count} 条批注的汇总表。");
        }

        /// <summary>在指定位置绘制批注汇总表（日期+内容两列），并从各批注框绘制引线指向表位左上角。</summary>
        private static void CreateSummaryTable(Document doc,AnnotationSettings settings,List<SummaryItem> items,Point3d ucsPoint,double textHeight)
        {
            using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database,tr,settings);
                var space=(BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId,OpenMode.ForWrite);
                var ucsToWcs=GetUcsMatrix(doc);var wcsToUcs=ucsToWcs.Inverse();
                // GetPoint 返回 UCS 坐标，其数值即局部坐标；几何在局部坐标构建后统一 TransformBy(ucsToWcs)。
                var origin=new Point3d(ucsPoint.X,ucsPoint.Y,ucsPoint.Z);
                var margin=textHeight*0.5;
                var layer=EffectiveLayer(settings);
                var gridColor=Color.FromColorIndex(ColorMethod.ByAci,settings.BoxColor);
                var textColor=Color.FromColorIndex(ColorMethod.ByAci,settings.TextColor);
                var leaderColor=Color.FromColorIndex(ColorMethod.ByAci,settings.LeaderColor);

                // 列宽：日期列取最长日期的实际宽度；内容列取最长单行宽度，超过上限时按上限换行。
                var dateInner=0.0;var contentNatural=0.0;
                foreach(var item in items)
                {
                    using(var dateText=new MText{TextHeight=textHeight,Contents=Escape(item.Data.Date)})
                    {
                        ApplyTextStyle(doc.Database,tr,dateText,settings.TextStyleName);
                        dateInner=Math.Max(dateInner,dateText.ActualWidth);
                    }
                    using(var contentText=new MText{TextHeight=textHeight,Contents=EscapeContent(item.Data.Content)})
                    {
                        ApplyTextStyle(doc.Database,tr,contentText,settings.TextStyleName);
                        contentNatural=Math.Max(contentNatural,contentText.ActualWidth);
                    }
                }
                var contentInner=Math.Min(Math.Max(contentNatural,textHeight*6),textHeight*50);
                var dateWidth=dateInner+margin*2;var contentWidth=contentInner+margin*2;

                // 行高随内容换行后的实际高度自适应。
                var rowHeights=new double[items.Count];var totalHeight=0.0;
                for(var i=0;i<items.Count;i++)
                {
                    using(var contentText=new MText{TextHeight=textHeight,Width=contentInner,Contents=EscapeContent(items[i].Data.Content)})
                    {
                        ApplyTextStyle(doc.Database,tr,contentText,settings.TextStyleName);
                        rowHeights[i]=Math.Max(contentText.ActualHeight,textHeight)+margin*2;
                    }
                    totalHeight+=rowHeights[i];
                }
                var tableWidth=dateWidth+contentWidth;

                void Append(Entity entity,Color color)
                {
                    entity.Layer=layer;entity.Color=color;
                    entity.TransformBy(ucsToWcs);
                    space.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);
                }

                // 表格外框 + 行列分隔线（origin 为表格左上角）。
                Append(BuildBox(new Point3d(origin.X,origin.Y-totalHeight,origin.Z),tableWidth,totalHeight),gridColor);
                Append(new Line(new Point3d(origin.X+dateWidth,origin.Y,origin.Z),new Point3d(origin.X+dateWidth,origin.Y-totalHeight,origin.Z)),gridColor);
                var rowTop=origin.Y;
                for(var i=0;i<items.Count;i++)
                {
                    var rowBottom=rowTop-rowHeights[i];
                    Append(new Line(new Point3d(origin.X,rowBottom,origin.Z),new Point3d(origin.X+tableWidth,rowBottom,origin.Z)),gridColor);
                    var dateCell=new MText{Location=new Point3d(origin.X+margin,rowTop-margin,origin.Z),TextHeight=textHeight,Contents=Escape(items[i].Data.Date),Attachment=AttachmentPoint.TopLeft};
                    ApplyTextStyle(doc.Database,tr,dateCell,settings.TextStyleName);
                    Append(dateCell,textColor);
                    var contentCell=new MText{Location=new Point3d(origin.X+dateWidth+margin,rowTop-margin,origin.Z),TextHeight=textHeight,Width=contentInner,Contents=EscapeContent(items[i].Data.Content),Attachment=AttachmentPoint.TopLeft};
                    ApplyTextStyle(doc.Database,tr,contentCell,settings.TextStyleName);
                    Append(contentCell,textColor);
                    rowTop=rowBottom;
                }

                // 各批注框的引线汇总到用户点击的点位（表格左上角）。
                foreach(var item in items)
                {
                    if(item.BoxCorners==null||item.BoxCorners.Length==0)continue;
                    var localCorners=item.BoxCorners.Select(c=>{var w=new Point3d(c.X,c.Y,item.BoxElevation).TransformBy(wcsToUcs);return new Point2d(w.X,w.Y);}).ToArray();
                    var anchor=localCorners.OrderBy(c=>c.GetDistanceTo(new Point2d(origin.X,origin.Y))).First();
                    var leader=new Polyline();
                    leader.AddVertexAt(0,anchor,0,0,0);
                    leader.AddVertexAt(1,new Point2d(origin.X,origin.Y),0,0,0);
                    leader.Elevation=origin.Z;
                    if(settings.LineWidth>0)leader.ConstantWidth=settings.LineWidth;
                    Append(leader,leaderColor);
                }
                tr.Commit();
            }
        }

        // ============ 批注清单（图上生成表，命令名沿用 X_PZ_LEGEND） ============

        /// <summary>清单表的一列：列标题 + 取值 + 是否按多行文本处理（内容列要把换行转成 \P）。</summary>
        private sealed class LegendColumn
        {
            public string Title;
            public Func<SummaryItem, string> Value;
            public bool MultiLine;
        }

        /// <summary>批注清单表的列定义 —— **要增减列只改这里**：表宽、表头、行高都会自动跟着重算。
        /// 列与「批注列表」窗口保持一致（编号/专业/状态/批注人/角色/日期/内容）；最后一列（内容）按上限宽度自动换行。</summary>
        private static readonly LegendColumn[] LegendColumns =
        {
            new LegendColumn { Title = "编号", Value = item => item.Data.Number },
            new LegendColumn { Title = "专业", Value = item => item.Data.Discipline },
            new LegendColumn { Title = "状态", Value = item => item.Data.Status },
            new LegendColumn { Title = "批注人", Value = item => item.Data.Author },
            new LegendColumn { Title = "角色", Value = item => item.Data.Role },
            new LegendColumn { Title = "日期", Value = item => item.Data.Date },
            new LegendColumn { Title = "内容", Value = item => item.Data.Content, MultiLine = true },
        };

        /// <summary>批注清单：把本图**全部**批注整理成一张"清单表"画到用户指定的位置。
        /// 列与「批注列表」窗口一致；与「批注汇总」的区别：不框选（自动取全图）、**不画引线**、多一行表头。</summary>
        public static void DrawLegend(Document doc)
        {
            var ed = doc.Editor;
            var items = CollectLegendItems(doc);
            if (items.Count == 0) { ed.WriteMessage("\n当前图纸中没有 X-批注，无法生成批注清单。"); return; }

            var placement = ed.GetPoint("\n指定批注清单表位置（表格左上角）: ");
            if (placement.Status != PromptStatus.OK) return;

            var settings = SettingsStore.Load();
            // 与汇总表同一口径：表内文字高度取全部批注文字高度的平均值。
            var textHeight = items.Average(x => x.TextHeight > 0 ? x.TextHeight : settings.TextHeight);
            CreateLegendTable(doc, settings, items, placement.Value, textHeight);
            ed.WriteMessage($"\n已生成 {items.Count} 条批注的清单表。");
        }

        /// <summary>收集清单数据：走 GetAllAnnotations（与批注列表 / CSV / Word 同源，已按编号排序），
        /// 再补上各条的文字高度用于定表内字号。清单表不画引线，所以不需要批注框角点。</summary>
        private static List<SummaryItem> CollectLegendItems(Document doc)
        {
            var items = new List<SummaryItem>();
            var infos = GetAllAnnotations(doc);
            if (infos.Count == 0) return items;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (var info in infos)
                {
                    var groupName = GroupPrefix + info.Id;
                    if (!groups.Contains(groupName)) continue;
                    if (!(tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead) is Group group)) continue;
                    if (!TryReadMaster(group, tr, out var data)) continue;
                    var measuredHeight = 0.0;
                    foreach (ObjectId member in group.GetAllEntityIds())
                    {
                        if (!member.IsValid || member.IsErased) continue;
                        if (!(tr.GetObject(member, OpenMode.ForRead, false) is Entity memberEntity)) continue;
                        if (measuredHeight <= 0 && memberEntity is MText mtext) measuredHeight = mtext.TextHeight;
                    }
                    items.Add(new SummaryItem
                    {
                        Data = data,
                        TextHeight = data.RenderTextHeight > 0 ? data.RenderTextHeight : measuredHeight
                    });
                }
            }
            return items;
        }

        /// <summary>量一段文字在给定字高 / 限宽下的实际尺寸（一次性探针 MText，取值方式与表内单元格一致）。</summary>
        private static void MeasureCell(Document doc, Transaction tr, AnnotationSettings settings, double textHeight, string contents, double wrapWidth, out double width, out double height)
        {
            using (var probe = new MText { TextHeight = textHeight, Contents = contents })
            {
                if (wrapWidth > 0) probe.Width = wrapWidth;
                ApplyTextStyle(doc.Database, tr, probe, settings.TextStyleName);
                width = probe.ActualWidth;
                height = probe.ActualHeight;
            }
        }

        /// <summary>取某一列在该行要显示的文字（多行列把换行转成 \P，其余列走普通转义）。</summary>
        private static string CellText(LegendColumn column, SummaryItem item)
        {
            var raw = column.Value == null ? "" : column.Value(item) ?? "";
            return column.MultiLine ? EscapeContent(raw) : Escape(raw);
        }

        /// <summary>在指定位置绘制批注清单表：表头行 + 各数据行，外框与行列分隔线样式同汇总表。
        /// 列取自 <see cref="LegendColumns"/>；最后一列（内容）按上限宽度自动换行，其余列按实测宽度取宽。
        /// origin 为表格左上角，表格向右下生长。</summary>
        private static void CreateLegendTable(Document doc, AnnotationSettings settings, List<SummaryItem> items, Point3d ucsPoint, double textHeight)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database, tr, settings);
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                var ucsToWcs = GetUcsMatrix(doc);
                // GetPoint 返回 UCS 坐标，其数值即局部坐标；几何在局部坐标构建后统一 TransformBy(ucsToWcs)。
                var origin = new Point3d(ucsPoint.X, ucsPoint.Y, ucsPoint.Z);
                var margin = textHeight * 0.5;
                var layer = EffectiveLayer(settings);
                var gridColor = Color.FromColorIndex(ColorMethod.ByAci, settings.BoxColor);
                var textColor = Color.FromColorIndex(ColorMethod.ByAci, settings.TextColor);

                var count = LegendColumns.Length;
                var lastColumn = count - 1;

                // 列宽：表头与全部数据行里最宽的那个。最后一列（内容）限宽换行，其余列不折行。
                var inner = new double[count];
                for (var c = 0; c < count; c++)
                {
                    double natural, ignored;
                    MeasureCell(doc, tr, settings, textHeight, Escape(LegendColumns[c].Title), 0, out natural, out ignored);
                    foreach (var item in items)
                    {
                        double cellWidth, cellHeight;
                        MeasureCell(doc, tr, settings, textHeight, CellText(LegendColumns[c], item), 0, out cellWidth, out cellHeight);
                        natural = Math.Max(natural, cellWidth);
                    }
                    inner[c] = c == lastColumn ? Math.Min(Math.Max(natural, textHeight * 6), textHeight * 50) : natural;
                }
                var columnWidth = new double[count];
                var tableWidth = 0.0;
                for (var c = 0; c < count; c++) { columnWidth[c] = inner[c] + margin * 2; tableWidth += columnWidth[c]; }

                // 行高：表头行与各数据行都按该行实际（可能换行后）的最高单元格取，再加下内边距。
                var headerTextHeight = 0.0;
                for (var c = 0; c < count; c++)
                {
                    double cellWidth, cellHeight;
                    MeasureCell(doc, tr, settings, textHeight, Escape(LegendColumns[c].Title), 0, out cellWidth, out cellHeight);
                    headerTextHeight = Math.Max(headerTextHeight, cellHeight);
                }
                var headerHeight = headerTextHeight + margin * 2;
                var rowHeights = new double[items.Count];
                var totalHeight = headerHeight;
                for (var i = 0; i < items.Count; i++)
                {
                    var tallest = 0.0;
                    for (var c = 0; c < count; c++)
                    {
                        double cellWidth, cellHeight;
                        MeasureCell(doc, tr, settings, textHeight, CellText(LegendColumns[c], items[i]), c == lastColumn ? inner[c] : 0, out cellWidth, out cellHeight);
                        tallest = Math.Max(tallest, cellHeight);
                    }
                    rowHeights[i] = tallest + margin * 2;
                    totalHeight += rowHeights[i];
                }

                void Append(Entity entity, Color color)
                {
                    entity.Layer = layer; entity.Color = color;
                    entity.TransformBy(ucsToWcs);
                    space.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
                }

                // 单元格：文字从 (x + margin, 行顶 - margin) 起按左上对齐铺开；wrapWidth > 0 时限定宽度换行。
                void AppendCell(string contents, double x, double rowTop, double wrapWidth)
                {
                    var cell = new MText
                    {
                        Location = new Point3d(x + margin, rowTop - margin, origin.Z),
                        TextHeight = textHeight,
                        Contents = contents,
                        Attachment = AttachmentPoint.TopLeft
                    };
                    if (wrapWidth > 0) cell.Width = wrapWidth;
                    ApplyTextStyle(doc.Database, tr, cell, settings.TextStyleName);
                    Append(cell, textColor);
                }

                // 外框 + 表头下边线 + 列分隔线 + 行分隔线。
                Append(BuildBox(new Point3d(origin.X, origin.Y - totalHeight, origin.Z), tableWidth, totalHeight), gridColor);
                var headerBottom = origin.Y - headerHeight;
                Append(new Line(new Point3d(origin.X, headerBottom, origin.Z), new Point3d(origin.X + tableWidth, headerBottom, origin.Z)), gridColor);
                var x = origin.X;
                for (var c = 0; c < lastColumn; c++)
                {
                    x += columnWidth[c];
                    Append(new Line(new Point3d(x, origin.Y, origin.Z), new Point3d(x, origin.Y - totalHeight, origin.Z)), gridColor);
                }

                // 表头行。
                x = origin.X;
                for (var c = 0; c < count; c++) { AppendCell(Escape(LegendColumns[c].Title), x, origin.Y, 0); x += columnWidth[c]; }

                // 数据行。
                var rowTop = headerBottom;
                for (var i = 0; i < items.Count; i++)
                {
                    var rowBottom = rowTop - rowHeights[i];
                    Append(new Line(new Point3d(origin.X, rowBottom, origin.Z), new Point3d(origin.X + tableWidth, rowBottom, origin.Z)), gridColor);
                    x = origin.X;
                    for (var c = 0; c < count; c++)
                    {
                        AppendCell(CellText(LegendColumns[c], items[i]), x, rowTop, c == lastColumn ? inner[c] : 0);
                        x += columnWidth[c];
                    }
                    rowTop = rowBottom;
                }
                tr.Commit();
            }
        }

        private static string EscapeContent(string content)=>Escape(content).Replace("\r\n","\\P").Replace("\n","\\P");

        /// <summary>导出批注到 Word：每条批注输出三行（时间 / 云线范围截图 / 批注文字），支持框选或全部导出。</summary>
        public static void ExportAnnotationsToWord(Document doc)
        {
            var ed=doc.Editor;
            var keywordOptions=new PromptKeywordOptions("\n导出范围 [框选(K)/全部(A)] <A>: ");
            keywordOptions.Keywords.Add("K");
            keywordOptions.Keywords.Add("A");
            keywordOptions.Keywords.Default="A";
            keywordOptions.AllowNone=true;
            var keyword=ed.GetKeywords(keywordOptions);
            if(keyword.Status!=PromptStatus.OK&&keyword.Status!=PromptStatus.None)return;
            var exportAll=keyword.Status==PromptStatus.None||keyword.StringResult=="A";

            HashSet<string> filter=null;
            if(!exportAll)
            {
                var options=new PromptSelectionOptions{MessageForAdding="\n框选要导出的批注（窗口/窗交均可）: "};
                var selectionFilter=new SelectionFilter(new[]{new TypedValue((int)DxfCode.ExtendedDataRegAppName,AnnotationCodec.AppName)});
                var selection=ed.GetSelection(options,selectionFilter);
                if(selection.Status!=PromptStatus.OK||selection.Value==null||selection.Value.Count==0){ed.WriteMessage("\n未选择任何批注。");return;}
                filter=new HashSet<string>(StringComparer.Ordinal);
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    foreach(SelectedObject selected in selection.Value)
                    {
                        if(selected==null)continue;
                        var entity=tr.GetObject(selected.ObjectId,OpenMode.ForRead,false) as Entity;
                        if(entity!=null&&TryGetId(entity,out var id))filter.Add(id);
                    }
                }
                if(filter.Count==0){ed.WriteMessage("\n所选对象中没有有效的 X-批注。");return;}
            }

            var entries=CollectWordEntries(doc,filter);
            if(entries.Count==0){ed.WriteMessage("\n当前图纸中没有可导出的 X-批注。");return;}
            WordExporter.Export(doc,entries);
        }

        /// <summary>收集导出条目：复用 GetAllAnnotations 保证与列表面板内容完全一致，再补充云线 WCS 范围。</summary>
        private static List<AnnotationWordEntry> CollectWordEntries(Document doc,HashSet<string> filter)
        {
            // 直接复用批注列表面板的数据源，确保导出内容与面板完全统一。
            var allAnnotations=GetAllAnnotations(doc);
            var entries=new List<AnnotationWordEntry>();
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForRead);
                foreach(var info in allAnnotations)
                {
                    if(filter!=null&&!filter.Contains(info.Id))continue;
                    var groupName=GroupPrefix+info.Id;
                    if(!groups.Contains(groupName))continue;
                    var group=(Group)tr.GetObject(groups.GetAt(groupName),OpenMode.ForRead);
                    var clouds=new List<Extents3d>();
                    foreach(ObjectId member in group.GetAllEntityIds())
                    {
                        if(!member.IsValid||member.IsErased)continue;
                        var entity=tr.GetObject(member,OpenMode.ForRead,false) as Entity;
                        if(entity==null)continue;
                        if(!TryGetRole(entity,out var role)||role!="cloud")continue;
                        try{clouds.Add(entity.GeometricExtents);}catch{/* 忽略无法计算范围的云线 */}
                    }
                    entries.Add(new AnnotationWordEntry{Number=info.Number,Date=info.Date,Content=info.Content,CloudExtents=clouds});
                }
            }
            return entries; // GetAllAnnotations 已按编号排序
        }

        /// <summary>轻量批注摘要，供列表面板展示。</summary>
        public sealed class AnnotationInfo
        {
            public string Id { get; set; }
            public string Number { get; set; }
            public string Discipline { get; set; }
            public string Author { get; set; }
            public string Role { get; set; }
            public string Date { get; set; }
            public string Status { get; set; }
            public string Content { get; set; }
            public ObjectId GroupId { get; set; }
            public ObjectId FirstEntityId { get; set; }
        }

        /// <summary>扫描当前 DWG 中现有批注编号，返回最大「X-」编号+1（无批注时=1）。</summary>
        public static int GetNextNumber(Document doc)
        {
            if (doc == null || doc.IsDisposed) return 1;
            var max = 0;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (var entry in groups)
                {
                    var name = entry.Key as string;
                    if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix, StringComparison.Ordinal)) continue;
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                    if (TryReadMaster(group, tr, out var data) && !string.IsNullOrWhiteSpace(data.Number))
                    {
                        var number = data.Number.Trim();
                        var numericPart = number.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ? number.Substring(3) : number;
                        if (int.TryParse(numericPart, out var parsed) && parsed > max) max = parsed;
                    }
                }
            }
            return max + 1;
        }

        /// <summary>将本机设置中的下一个编号同步到当前 DWG。</summary>
        public static void SyncNextNumber(Document doc)
        {
            if (doc == null || doc.IsDisposed) return;
            try
            {
                var settings = SettingsStore.Load();
                settings.NextNumber = GetNextNumber(doc);
                SettingsStore.Save(settings);
            }
            catch (System.Exception ex) { PluginLog.Error("Number.Sync", ex); }
        }

        /// <summary>擦除选点阶段创建的临时实体；用于取消或失败时恢复原图状态。</summary>
        public static void EraseTemporaryEntities(Document doc, IEnumerable<ObjectId> entityIds)
        {
            if (doc == null || doc.IsDisposed || entityIds == null) return;
            try
            {
                using(doc.LockDocument())using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    foreach(var id in entityIds)
                    {
                        if(!id.IsValid||id.IsErased)continue;
                        var entity=tr.GetObject(id,OpenMode.ForWrite,false);
                        if(entity!=null&&!entity.IsErased)entity.Erase();
                    }
                    tr.Commit();
                }
            }
            catch(System.Exception ex){PluginLog.Error("TemporaryEntities.Erase",ex);}
        }

        /// <summary>枚举当前 DWG 中所有 X-批注。</summary>
        public static List<AnnotationInfo> GetAllAnnotations(Document doc)
        {
            var result = new List<AnnotationInfo>();
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                    foreach (var entry in groups)
                    {
                        var name = entry.Key as string;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix)) continue;
                        if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                        if (!TryReadMaster(group, tr, out var data)) continue;
                        var ids = group.GetAllEntityIds();
                        ObjectId firstId = ObjectId.Null;
                        foreach (ObjectId oid in ids) { if (oid.IsValid && !oid.IsErased) { firstId = oid; break; } }
                        if(firstId.IsNull)continue;
                        result.Add(new AnnotationInfo
                        {
                            Id = data.Id, Number = data.Number, Discipline = data.Discipline,
                            Author = data.Author, Role = data.Role, Date = data.Date, Status = data.Status,
                            Content = data.Content, GroupId = group.ObjectId, FirstEntityId = firstId
                        });
                    }
                }
            }
            catch { }
            return result.OrderBy(x => x.Number, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 缩放视图到批注实体（及同编组全部实体）范围。
        /// 可从无模式 WPF 面板调用，内部会 LockDocument。
        /// </summary>
        /// <param name="doc">当前文档。</param>
        /// <param name="entityId">批注组内任一实体 ID。</param>
        /// <returns>是否成功定位。</returns>
        public static bool ZoomToAnnotation(Document doc, ObjectId entityId)
        {
            if (doc == null || doc.IsDisposed || entityId.IsNull || !entityId.IsValid || entityId.IsErased)
            {
                return false;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                    if (entity == null)
                    {
                        return false;
                    }

                    var ext = entity.GeometricExtents;
                    var selectIds = new List<ObjectId> { entityId };

                    // 若该实体属于某个编组，扩展到编组中全部实体
                    if (TryGetId(entity, out var id))
                    {
                        var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                        var groupName = GroupPrefix + id;
                        if (groups.Contains(groupName))
                        {
                            var group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead);
                            selectIds.Clear();
                            foreach (ObjectId oid in group.GetAllEntityIds())
                            {
                                if (!oid.IsValid || oid.IsErased)
                                {
                                    continue;
                                }

                                var e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                                if (e == null)
                                {
                                    continue;
                                }

                                selectIds.Add(oid);
                                ext.AddPoint(e.GeometricExtents.MinPoint);
                                ext.AddPoint(e.GeometricExtents.MaxPoint);
                            }
                        }
                    }

                    // 实体范围是 WCS；视图 CenterPoint/Width/Height 使用 DCS，必须先转换。
                    var ed = doc.Editor;
                    using (var view = ed.GetCurrentView())
                    {
                        var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                        wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
                        wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                        wcsToDcs = wcsToDcs.Inverse();
                        var min = ext.MinPoint;
                        var max = ext.MaxPoint;
                        var dcsCorners = new[]
                        {
                            new Point3d(min.X, min.Y, min.Z), new Point3d(max.X, min.Y, min.Z),
                            new Point3d(max.X, max.Y, min.Z), new Point3d(min.X, max.Y, min.Z),
                            new Point3d(min.X, min.Y, max.Z), new Point3d(max.X, min.Y, max.Z),
                            new Point3d(max.X, max.Y, max.Z), new Point3d(min.X, max.Y, max.Z)
                        }.Select(point => point.TransformBy(wcsToDcs)).ToArray();
                        var minX = dcsCorners.Min(point => point.X);
                        var maxX = dcsCorners.Max(point => point.X);
                        var minY = dcsCorners.Min(point => point.Y);
                        var maxY = dcsCorners.Max(point => point.Y);
                        var width = Math.Max(maxX - minX, 1);
                        var height = Math.Max(maxY - minY, 1);
                        const double margin = 1.2;
                        view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
                        view.Width = width * margin;
                        view.Height = height * margin;
                        ed.SetCurrentView(view);
                    }

                    if (selectIds.Count > 0)
                    {
                        ed.SetImpliedSelection(selectIds.ToArray());
                    }

                    ed.UpdateScreen();
                    tr.Commit();
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("ZoomToAnnotation", ex);
                return false;
            }
        }

        private static void WriteMaster(Group group, Transaction tr, AnnotationData data)
        {
            if (group.ExtensionDictionary.IsNull) group.CreateExtensionDictionary();
            var dict = (DBDictionary)tr.GetObject(group.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord record;
            if (dict.Contains(DataKey)) record = (Xrecord)tr.GetObject(dict.GetAt(DataKey), OpenMode.ForWrite);
            else { record = new Xrecord(); dict.SetAt(DataKey, record); tr.AddNewlyCreatedDBObject(record, true); }
            var values = AnnotationCodec.Split(AnnotationCodec.Encode(data)).Select(x => new TypedValue((int)DxfCode.Text, x)).ToArray();
            record.Data = new ResultBuffer(values);
        }

        private static bool TryReadMaster(Group group, Transaction tr, out AnnotationData data)
        {
            data = null; if (group.ExtensionDictionary.IsNull) return false;
            var dict = (DBDictionary)tr.GetObject(group.ExtensionDictionary, OpenMode.ForRead); if (!dict.Contains(DataKey)) return false;
            var record = (Xrecord)tr.GetObject(dict.GetAt(DataKey), OpenMode.ForRead); if (record.Data == null) return false;
            var value = string.Concat(record.Data.AsArray().Where(v => v.TypeCode == (int)DxfCode.Text).Select(v => v.Value as string));
            return AnnotationCodec.TryDecode(value, out data);
        }

        /// <summary>写入留痕：创建/修改受"立即入库"开关控制，其余动作（删除/移动/增补/修复）始终记录。
        /// 勾选"立即入库"时，创建/修改还会同时把该条批注写入<b>知识库</b>（批注条目 + 常用批注语）——
        /// 留痕（history.json）与知识库（knowledge.json）是两套独立数据，各查各的、各清各的。</summary>
        private static void ArchiveRecord(Document doc, string action, AnnotationData data, string changes)
        {
            var creates = action == "创建" || action == "修改";
            if (creates && !SettingsStore.Load().ArchiveOnCreate) return;
            AnnotationHistoryStore.Record(doc, action, data, changes);
            // 知识库入库只对创建/修改生效：删除/移动/增补/修复属于留痕范畴，不往知识库里写条目。
            if (creates) KnowledgeStore.Archive(doc, data);
        }

        // ============ 复制/粘贴批注修复 ============
        // 根因：CAD 的 COPY / 复制粘贴只复制实体和 XData，不复制编组（Group），
        // 而批注主数据存在编组扩展字典的 XRecord 里，导致复制出的批注无法识别。
        // 方案：创建批注时把完整数据分片冗余写入每个子实体的 XData（见 Add），
        // 修复时为"孤儿"实体（有 XData 标识但不在对应编组中）重建编组并分配新 Id。

        /// <summary>批量读写用的批注引用：实体 Id + 业务数据。</summary>
        internal sealed class AnnotationRef
        {
            public ObjectId Id;
            public AnnotationData Data;
        }

        /// <summary>读取批注列表：<paramref name="only"/> 为空时读取全图（按批注 Id 去重），否则只读给定实体。</summary>
        internal static List<AnnotationRef> ReadAnnotations(Document doc, IList<ObjectId> only = null)
        {
            var result = new List<AnnotationRef>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (only != null)
                {
                    foreach (var oid in only)
                    {
                        if (!oid.IsValid || oid.IsErased) continue;
                        var entity = tr.GetObject(oid, OpenMode.ForRead, false) as Entity;
                        if (entity == null || !TryReadFromEntity(tr, entity, out var d) || !seen.Add(d.Id)) continue;
                        result.Add(new AnnotationRef { Id = entity.ObjectId, Data = d });
                    }
                }
                else
                {
                    var map = CollectAnnotatedEntities(tr, doc.Database);
                    foreach (var pair in map)
                    {
                        if (!seen.Add(pair.Key) || pair.Value.Count == 0) continue;
                        if (!TryReadFromEntity(tr, pair.Value[0], out var d)) continue;
                        result.Add(new AnnotationRef { Id = pair.Value[0].ObjectId, Data = d });
                    }
                }
            }
            return result;
        }

        /// <summary>收集全图所有带批注 XData 的实体，按批注 Id 分组。</summary>
        private static Dictionary<string, List<Entity>> CollectAnnotatedEntities(Transaction tr, Database db)
        {
            var map = new Dictionary<string, List<Entity>>(StringComparer.Ordinal);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsAnonymous || btr.IsFromExternalReference || btr.IsDependent) continue;
                foreach (ObjectId entId in btr)
                {
                    var entity = tr.GetObject(entId, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !TryGetId(entity, out var id)) continue;
                    if (!map.TryGetValue(id, out var list)) { list = new List<Entity>(); map[id] = list; }
                    list.Add(entity);
                }
            }
            return map;
        }

        // ============ 批注在图面上的放置信息（导出保留坐标 / 导入按原坐标还原） ============

        /// <summary>批注的放置信息（全部为 WCS）：云线包络范围 + 文字框锚点 + 所在布局名。
        /// 导出写入 CSV，导入到其他 DWG 时据此把批注还原到原图坐标处。</summary>
        internal sealed class AnnotationPlacement
        {
            public string Layout;                        // 所在布局名（模型 / 布局名），取不到为 null
            public bool HasCloud;                        // 是否读到云线范围
            public double MinX, MinY, MaxX, MaxY;        // 云线顶点包络
            public bool HasText;                         // 是否读到文字框锚点（左下角）
            public double TextX, TextY;
            public double Z;                             // 标高（云线/文字所在平面）
        }

        /// <summary>读取全图批注的放置信息，按批注 Id 索引。取法：
        /// <list type="bullet">
        /// <item>文字锚点 = 编组内"框"实体（role=box）的<b>第 0 个顶点</b>——创建时边框正是以 textLocation 为原点、
        /// 沿 +X/+Y 生长，所以第 0 个顶点就是当初传入的 textLocation，导出再导入能精确复位。</item>
        /// <item>云线范围 = "云线"实体（role=cloud）的<b>顶点</b>包络。不用 GeometricExtents：
        /// 弧瓣是向外鼓出的，实测范围会比原范围大一圈，反复导出导入会把云线越还原越大。</item>
        /// </list></summary>
        internal static Dictionary<string, AnnotationPlacement> ReadPlacements(Document doc)
        {
            var map = new Dictionary<string, AnnotationPlacement>(StringComparer.Ordinal);
            if (doc == null || doc.IsDisposed) return map;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layoutCache = new Dictionary<ObjectId, string>();
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (var entry in groups)
                {
                    var name = entry.Key as string;
                    if (string.IsNullOrEmpty(name) || !name.StartsWith(GroupPrefix, StringComparison.Ordinal)) continue;
                    var id = name.Substring(GroupPrefix.Length);
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Group group)) continue;
                    var placement = new AnnotationPlacement();
                    foreach (ObjectId member in group.GetAllEntityIds())
                    {
                        if (!member.IsValid || member.IsErased) continue;
                        var entity = tr.GetObject(member, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        if (placement.Layout == null) placement.Layout = LayoutNameOf(tr, entity, layoutCache);
                        if (!TryGetRole(entity, out var role)) continue;
                        if (role == "box" && entity is Polyline box && box.NumberOfVertices > 0)
                        {
                            var origin = box.GetPoint3dAt(0);
                            placement.HasText = true; placement.TextX = origin.X; placement.TextY = origin.Y; placement.Z = origin.Z;
                        }
                        else if (role == "cloud" && entity is Polyline cloud && cloud.NumberOfVertices > 0)
                        {
                            for (var i = 0; i < cloud.NumberOfVertices; i++)
                            {
                                var v = cloud.GetPoint3dAt(i);
                                if (!placement.HasCloud)
                                {
                                    placement.MinX = placement.MaxX = v.X; placement.MinY = placement.MaxY = v.Y;
                                    placement.Z = v.Z; placement.HasCloud = true; continue;
                                }
                                if (v.X < placement.MinX) placement.MinX = v.X;
                                if (v.X > placement.MaxX) placement.MaxX = v.X;
                                if (v.Y < placement.MinY) placement.MinY = v.Y;
                                if (v.Y > placement.MaxY) placement.MaxY = v.Y;
                            }
                        }
                    }
                    map[id] = placement;
                }
            }
            return map;
        }

        /// <summary>实体所在布局名：由 OwnerId（布局的块表记录）反查 Layout。非布局块表记录返回 null。</summary>
        private static string LayoutNameOf(Transaction tr, Entity entity, Dictionary<ObjectId, string> cache)
        {
            try
            {
                var owner = entity.OwnerId;
                if (cache.TryGetValue(owner, out var cached)) return cached;
                string name = null;
                if (tr.GetObject(owner, OpenMode.ForRead, false) is BlockTableRecord btr && btr.IsLayout)
                {
                    if (tr.GetObject(btr.LayoutId, OpenMode.ForRead, false) is Layout layout) name = layout.LayoutName;
                }
                cache[owner] = name;
                return name;
            }
            catch { return null; }
        }

        /// <summary>按布局名查该布局的空间（BlockTableRecord）Id；本图没有这个布局时返回 ObjectId.Null。
        /// 导入时用来把批注放回它原来所在的布局（模型空间/各布局），而不是一律塞进当前空间。</summary>
        internal static ObjectId FindLayoutSpace(Database db, string layoutName)
        {
            if (db == null || string.IsNullOrWhiteSpace(layoutName)) return ObjectId.Null;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    if (!layouts.Contains(layoutName)) return ObjectId.Null;
                    if (tr.GetObject(layouts.GetAt(layoutName), OpenMode.ForRead, false) is Layout layout) return layout.BlockTableRecordId;
                }
            }
            catch { }
            return ObjectId.Null;
        }

        /// <summary>重写实体 XData 中的批注 Id，保留角色与内嵌数据分片。</summary>
        private static void RewriteAnnotationId(Entity e, string newId)
        {
            var rb = e.GetXDataForApplication(AnnotationCodec.AppName); if (rb == null) return;
            var newRb = new ResultBuffer(); var first = true;
            foreach (var tv in rb.AsArray())
            {
                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName) { newRb.Add(tv); continue; }
                if (first) { newRb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, newId)); first = false; }
                else newRb.Add(tv);
            }
            e.XData = newRb;
        }

        /// <summary>修复同一批注 Id 下的孤儿实体：分配新 Id、重建编组并写入主数据。返回是否发生修复。</summary>
        private static bool RepairBatch(Transaction tr, DBDictionary groups, string id, List<Entity> entities, List<string> warnings, List<AnnotationData> repairedOut = null)
        {
            var groupName = GroupPrefix + id;
            Group group = null; var memberSet = new HashSet<ObjectId>();
            if (groups.Contains(groupName))
            {
                group = (Group)tr.GetObject(groups.GetAt(groupName), OpenMode.ForRead);
                foreach (ObjectId mid in group.GetAllEntityIds()) memberSet.Add(mid);
            }
            var orphans = new List<Entity>();
            foreach (var e in entities) if (!memberSet.Contains(e.ObjectId)) orphans.Add(e);
            if (orphans.Count == 0) return false;
            // 数据来源：优先实体内嵌分片（复制场景），其次原编组主数据（同图复制旧版批注）。
            AnnotationData data = null;
            foreach (var e in orphans) { if (TryReadEmbedded(e, out var d)) { data = d; break; } }
            if (data == null && group != null) TryReadMaster(group, tr, out data);
            if (data == null) { warnings?.Add("批注(Id=" + id + ")缺少内嵌数据且原编组不存在，无法自动恢复，请重新标注。"); return false; }
            var newId = Guid.NewGuid().ToString("N"); data.Id = newId;
            var newIds = new ObjectIdCollection();
            foreach (var e in orphans) { e.UpgradeOpen(); RewriteAnnotationId(e, newId); newIds.Add(e.ObjectId); }
            groups.UpgradeOpen();
            var newGroup = new Group("X-批注 " + data.Number, true);
            groups.SetAt(GroupPrefix + newId, newGroup); tr.AddNewlyCreatedDBObject(newGroup, true); newGroup.Append(newIds);
            WriteMaster(newGroup, tr, data);
            repairedOut?.Add(data);
            return true;
        }

        /// <summary>全图扫描并修复复制/粘贴产生的批注，返回给命令行的报告文本（X_PZ_REPAIR 命令）。</summary>
        public static string RepairOrphans(Document doc)
        {
            var repaired = 0; var warnings = new List<string>(); var repairedData = new List<AnnotationData>();
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var groups = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                var map = CollectAnnotatedEntities(tr, doc.Database);
                foreach (var pair in map) if (RepairBatch(tr, groups, pair.Key, pair.Value, warnings, repairedData)) repaired++;
                tr.Commit();
            }
            foreach (var data in repairedData) AnnotationHistoryStore.Record(doc, "复制修复", data, "编组不随复制迁移，已重建编组");
            var report = repaired > 0 ? "已修复 " + repaired + " 条复制/粘贴产生的批注。" : "未发现需要修复的批注。";
            foreach (var w in warnings) report += "\n" + w;
            return report;
        }

        // ============ 批注显示 / 隐藏 ============
        // 只切换批注实体自身的可见性（Entity.Visible），不改图层、不删数据：
        // 关闭图层会连带隐藏同图层的用户图形，删除实体则会丢数据，两者都不适合作为"隐藏批注"。

        /// <summary>
        /// 批量切换批注可见性：<paramref name="only"/> 为空时处理全图批注，否则只处理所选实体所属的批注
        /// （同一编号的多个副本一并处理）。返回给命令行的报告文本。
        /// </summary>
        public static string SetVisibility(Document doc, bool visible, IList<ObjectId> only = null)
        {
            var annotations = 0; var entities = 0; var changed = 0;
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var selected = new HashSet<string>(StringComparer.Ordinal);
                if (only != null && only.Count > 0)
                {
                    foreach (var oid in only)
                    {
                        if (!oid.IsValid || oid.IsErased) continue;
                        if (tr.GetObject(oid, OpenMode.ForRead, false) is Entity picked && TryGetId(picked, out var pickedId)) selected.Add(pickedId);
                    }
                    if (selected.Count == 0) return "所选对象不是 X-批注。";
                }

                // 走 CollectAnnotatedEntities：既覆盖编组成员，也覆盖复制出来尚未修复的副本。
                var map = CollectAnnotatedEntities(tr, doc.Database);
                foreach (var pair in map)
                {
                    if (selected.Count > 0 && !selected.Contains(pair.Key)) continue;
                    if (pair.Value.Count == 0) continue;
                    annotations++;
                    foreach (var entity in pair.Value)
                    {
                        entities++;
                        if (entity.Visible == visible) continue;
                        entity.UpgradeOpen();
                        entity.Visible = visible;
                        changed++;
                    }
                }
                tr.Commit();
            }
#if !ZWCAD
            doc.Editor.Regen();
#endif
            if (annotations == 0) return visible ? "图中没有可显示的 X-批注。" : "图中没有可隐藏的 X-批注。";
            return (visible ? "已显示 " : "已隐藏 ") + annotations + " 条批注（" + changed + "/" + entities + " 个实体发生改变）。";
        }

        // ============ 合并批注 / 按内容过滤 ============
        // "两条批注相同"的判据 = 批注内容一致（换行归一、去首尾空白后逐字符相等）。
        // 日期/批注人/编号不参与判重：编号本来就各不相同，一旦参与就永远合并不了。

        /// <summary>批注内容的判重键：换行统一成 \n、去掉首尾空白。</summary>
        internal static string ContentKey(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            return content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        /// <summary>批注内容的单行摘要（列表显示用）。</summary>
        internal static string ContentPreview(string content, int maxLength = 60)
        {
            var text = ContentKey(content).Replace("\n", " ");
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Length > maxLength ? text.Substring(0, maxLength) + "…" : text;
        }

        /// <summary>一条"内容分组"：内容一致的全部批注（过滤窗口的数据源）。</summary>
        public sealed class ContentGroup
        {
            public string Key { get; set; }      // 判重键（规范化后的内容）
            public string Content { get; set; }  // 原始内容（换行原样保留）
            public string Preview { get; set; }  // 单行摘要
            public int Count { get; set; }
            public string Numbers { get; set; }  // 涉及的批注编号（、分隔）
        }

        /// <summary>按"批注内容完全相同"给全图批注分组统计（只读，不改图）。</summary>
        public static List<ContentGroup> GetContentGroups(Document doc)
        {
            var result = new List<ContentGroup>();
            if (doc == null || doc.IsDisposed) return result;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var buckets = new Dictionary<string, ContentGroup>(StringComparer.Ordinal);
                    var map = CollectAnnotatedEntities(tr, doc.Database);
                    foreach (var pair in map)
                    {
                        if (pair.Value.Count == 0) continue;
                        if (!TryReadFromEntity(tr, pair.Value[0], out var d)) continue;
                        var key = ContentKey(d.Content);
                        if (!buckets.TryGetValue(key, out var group))
                        {
                            group = new ContentGroup { Key = key, Content = d.Content ?? "", Preview = ContentPreview(d.Content) };
                            buckets[key] = group;
                        }
                        group.Count++;
                        group.Numbers = string.IsNullOrEmpty(group.Numbers) ? d.Number : group.Numbers + "、" + d.Number;
                    }
                    result.AddRange(buckets.Values);
                }
            }
            catch (System.Exception ex) { PluginLog.Error("GetContentGroups", ex); }
            return result.OrderByDescending(g => g.Count).ThenBy(g => g.Preview, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>按内容过滤显示：<paramref name="key"/> 为空 → 显示全部；否则只显示内容与它完全一致的批注，
        /// 其余批注隐藏（只切 <c>Entity.Visible</c>，与 X_PZ_HIDE/SHOW 同一机制，不删数据、不改图层）。
        /// 返回给命令行的报告文本。</summary>
        public static string FilterByContent(Document doc, string key)
        {
            if (doc == null || doc.IsDisposed) return "当前没有可用的图纸。";
            var showAll = string.IsNullOrEmpty(key);
            var total = 0; var shown = 0; var hidden = 0; var changed = 0;
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var map = CollectAnnotatedEntities(tr, doc.Database);
                foreach (var pair in map)
                {
                    if (pair.Value.Count == 0) continue;
                    if (!TryReadFromEntity(tr, pair.Value[0], out var d)) continue;
                    total++;
                    var match = showAll || ContentKey(d.Content) == key;
                    if (match) shown++; else hidden++;
                    foreach (var entity in pair.Value)
                    {
                        if (entity.Visible == match) continue;
                        entity.UpgradeOpen();
                        entity.Visible = match;
                        changed++;
                    }
                }
                tr.Commit();
            }
#if !ZWCAD
            doc.Editor.Regen();
#endif
            if (total == 0) return "图中没有 X-批注。";
            if (showAll) return "已显示全部 " + total + " 条批注。";
            if (shown == 0) return "没有内容与所选一致的批注（内容可能已被修改）。";
            return "已按内容过滤：显示 " + shown + " 条，隐藏 " + hidden + " 条（共 " + total + " 条）。";
        }

        /// <summary>合并用的批注成员：批注 Id + 主数据 + 全部实体 + 所属编组
        /// （复制粘贴产生的副本没有编组，<see cref="Group"/> 可能为 null）。</summary>
        private sealed class MergeMember
        {
            public string AnnotationId;
            public AnnotationData Data;
            public List<Entity> Entities;
            public Group Group;
        }

        /// <summary>合并时统一外观用的主批注样式（颜色 -1 = 主批注不是 ByAci，保持原色不动）。</summary>
        private sealed class MergeStyle
        {
            public bool HasCloud;
            public string CloudLayer;
            public short CloudColor = -1;
            public double CloudWidth;
            public bool HasLeader;
            public string LeaderLayer;
            public short LeaderColor = -1;
            public double LeaderWidth;
        }

        /// <summary>把内容完全相同的批注合并成一条：每组保留编号最小（且有编组）的那条作主批注，
        /// 其余批注的云线与引线整体并入主编组（外观统一照抄主批注、引线接到主批注文字框最近的角点），
        /// 其余批注的文字与文字框删除、编组解散。留痕：主批注记"合并"，被并入的每条记"合并删除"。
        /// 返回给命令行的报告文本。</summary>
        public static string MergeDuplicates(Document doc)
        {
            if (doc == null || doc.IsDisposed) return "当前没有可用的图纸。";
            var mergedBuckets = 0; var absorbed = 0; var movedClouds = 0; var erasedEntities = 0;
            var failed = new List<string>();
            var traces = new List<object[]>(); // 出事务后再写留痕：{action, data, changes}
            try
            {
                using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var map = CollectAnnotatedEntities(tr, doc.Database);
                    var buckets = new Dictionary<string, List<MergeMember>>(StringComparer.Ordinal);
                    foreach (var pair in map)
                    {
                        if (pair.Value.Count == 0) continue;
                        if (!TryReadFromEntity(tr, pair.Value[0], out var d)) continue;
                        var member = new MergeMember { AnnotationId = pair.Key, Data = d, Entities = pair.Value };
                        var groupName = GroupPrefix + pair.Key;
                        var groupDict = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                        if (groupDict.Contains(groupName)) member.Group = tr.GetObject(groupDict.GetAt(groupName), OpenMode.ForRead) as Group;
                        var key = ContentKey(d.Content);
                        // 无内容的批注不参与"按内容合并"：它们是"还没写内容"的占位批注（现已允许空内容生成），
                        // 内容都为空时 ContentKey 全是 ""，会被当成"完全相同"整批并成一条——绝对不能这么干。
                        if (key.Length == 0) continue;
                        if (!buckets.TryGetValue(key, out var list)) { list = new List<MergeMember>(); buckets[key] = list; }
                        list.Add(member);
                    }

                    foreach (var bucket in buckets.Values)
                    {
                        if (bucket.Count < 2) continue;
                        // 主批注：必须自己有编组（否则没地方容纳并进来的云线），在候选里取编号最小者。
                        var master = bucket.Where(m => m.Group != null)
                                           .OrderBy(m => m.Data.Number, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(m => m.Data.Date).FirstOrDefault();
                        if (master == null)
                        {
                            failed.Add("有一组内容相同的批注全是复制产生的副本（没有编组），已跳过；请先执行 X_PZ_REPAIR 修复后再合并。");
                            continue;
                        }
                        var masterGroup = (Group)tr.GetObject(master.Group.ObjectId, OpenMode.ForWrite);
                        var style = ProbeMergeStyle(master);
                        var masterCorners = ReadBoxCorners(master);
                        var absorbedNumbers = new List<string>();
                        var newIds = new ObjectIdCollection();
                        var masterSpace = master.Entities[0].OwnerId; // CAD 的编组是"按布局"的，跨布局不能并进同一编组
                        foreach (var dup in bucket)
                        {
                            if (dup.AnnotationId == master.AnnotationId) continue;
                            if (dup.Entities.Count == 0) continue;
                            if (dup.Entities.Any(x => x.OwnerId != masterSpace))
                            {
                                failed.Add("批注 " + dup.Data.Number + " 与 " + master.Data.Number + " 内容相同但不在同一布局，已跳过。");
                                continue;
                            }
                            var oldBoxCenter = ReadBoxCenter(dup);
                            foreach (var entity in dup.Entities)
                            {
                                if (entity == null || entity.IsErased) continue;
                                var role = TryGetRole(entity, out var r) ? r : null;
                                if (role == "cloud" || role == "leader")
                                {
                                    entity.UpgradeOpen();
                                    if (dup.Group != null && Contains(dup.Group, entity.ObjectId))
                                    {
                                        dup.Group.UpgradeOpen();
                                        dup.Group.Remove(entity.ObjectId);
                                    }
                                    // 引线"指向文字框"的那一端改接到主批注文字框：必须在改 Id 之前做（先看原框位置）。
                                    if (role == "leader" && oldBoxCenter.HasValue && masterCorners != null && entity is Polyline leader)
                                        RetargetLeader(leader, oldBoxCenter.Value, masterCorners);
                                    ApplyMergeStyle(entity, role, style);
                                    RewriteAnnotationXData(entity, master.Data.Id, role, master.Data);
                                    newIds.Add(entity.ObjectId);
                                    if (role == "cloud") movedClouds++;
                                }
                                else
                                {
                                    // 重复批注的文字与文字框没有存在价值（内容与主批注完全相同）。
                                    entity.UpgradeOpen();
                                    entity.Erase();
                                    erasedEntities++;
                                }
                            }
                            if (dup.Group != null)
                            {
                                var name = GroupPrefix + dup.AnnotationId;
                                dup.Group.UpgradeOpen();
                                dup.Group.Erase();
                                var dict = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                                if (dict.Contains(name)) dict.Remove(name);
                            }
                            absorbed++;
                            absorbedNumbers.Add(dup.Data.Number);
                            traces.Add(new object[] { "合并删除", dup.Data, "内容与 " + master.Data.Number + " 完全相同，已并入该批注" });
                        }
                        if (newIds.Count > 0) masterGroup.Append(newIds);
                        if (absorbedNumbers.Count == 0) continue;
                        mergedBuckets++;
                        traces.Add(new object[] { "合并", master.Data, "并入 " + string.Join("、", absorbedNumbers.ToArray()) + "（共 " + absorbedNumbers.Count + " 条）" });
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex) { PluginLog.Error("MergeDuplicates", ex); return "合并失败: " + ex.Message; }
            foreach (var trace in traces)
            {
                try { AnnotationHistoryStore.Record(doc, (string)trace[0], (AnnotationData)trace[1], (string)trace[2]); }
                catch (System.Exception ex) { PluginLog.Warning("MergeDuplicates.Trace", ex.Message); }
            }
#if !ZWCAD
            doc.Editor.Regen();
#endif
            if (absorbed == 0) return "没有内容完全相同的批注，无需合并。";
            var report = "已合并 " + mergedBuckets + " 组相同批注：" + absorbed + " 条并入保留的批注（搬移云线 " + movedClouds +
                         " 条，删除文字/文字框 " + erasedEntities + " 个）。";
            foreach (var warning in failed) report += "\n" + warning;
            return report;
        }

        /// <summary>读主批注的云线/引线外观（合并后整条批注的外观以它为准）。</summary>
        private static MergeStyle ProbeMergeStyle(MergeMember master)
        {
            var style = new MergeStyle();
            foreach (var entity in master.Entities)
            {
                if (entity == null || entity.IsErased) continue;
                if (!TryGetRole(entity, out var role)) continue;
                if (role == "cloud" && !style.HasCloud)
                {
                    style.HasCloud = true; style.CloudLayer = entity.Layer;
                    if (entity.Color != null && entity.Color.ColorMethod == ColorMethod.ByAci) style.CloudColor = entity.Color.ColorIndex;
                    if (entity is Polyline cloud) style.CloudWidth = SafeConstantWidth(cloud);
                }
                else if (role == "leader" && !style.HasLeader)
                {
                    style.HasLeader = true; style.LeaderLayer = entity.Layer;
                    if (entity.Color != null && entity.Color.ColorMethod == ColorMethod.ByAci) style.LeaderColor = entity.Color.ColorIndex;
                    if (entity is Polyline leader) style.LeaderWidth = SafeConstantWidth(leader);
                }
            }
            return style;
        }

        /// <summary>把并入的云线/引线外观统一成主批注的：同一条批注不该横跨两个图层、两种颜色
        /// （图层名带日期/人名后缀，不统一的话按图层隐藏会漏掉一半实体）。</summary>
        private static void ApplyMergeStyle(Entity entity, string role, MergeStyle style)
        {
            var useLeader = role == "leader" && style.HasLeader;
            var layer = useLeader ? style.LeaderLayer : style.CloudLayer;
            var color = useLeader ? style.LeaderColor : style.CloudColor;
            var width = useLeader ? style.LeaderWidth : style.CloudWidth;
            if (!string.IsNullOrEmpty(layer)) entity.Layer = layer;
            if (color >= 0) entity.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
            if (entity is Polyline poly) SafeSetConstantWidth(poly, width);
        }

        /// <summary>读批注文字框的角点（WCS）。没有文字框（数据异常）时返回 null。</summary>
        private static Point3d[] ReadBoxCorners(MergeMember member)
        {
            foreach (var entity in member.Entities)
            {
                if (entity == null || entity.IsErased) continue;
                if (!TryGetRole(entity, out var role) || role != "box") continue;
                if (entity is Polyline box && box.NumberOfVertices >= 4)
                    return Enumerable.Range(0, box.NumberOfVertices).Select(box.GetPoint3dAt).ToArray();
            }
            return null;
        }

        /// <summary>读批注文字框的中心（WCS）。没有文字框时返回 null。</summary>
        private static Point3d? ReadBoxCenter(MergeMember member)
        {
            var corners = ReadBoxCorners(member);
            if (corners == null || corners.Length == 0) return null;
            return new Point3d(corners.Average(p => p.X), corners.Average(p => p.Y), corners.Average(p => p.Z));
        }

        /// <summary>把并入引线"指向文字框"的那一端改接到主批注文字框最近的角点：
        /// 先看两端哪一端离被并入批注的原文字框更近，那一端就是框端。</summary>
        private static void RetargetLeader(Polyline leader, Point3d oldBoxCenter, Point3d[] masterCorners)
        {
            if (leader.NumberOfVertices < 2 || masterCorners == null || masterCorners.Length == 0) return;
            var first = leader.GetPoint3dAt(0);
            var last = leader.GetPoint3dAt(leader.NumberOfVertices - 1);
            var anchor = first.DistanceTo(oldBoxCenter) <= last.DistanceTo(oldBoxCenter) ? 0 : leader.NumberOfVertices - 1;
            var target = masterCorners.OrderBy(c => c.DistanceTo(leader.GetPoint3dAt(anchor))).First();
            leader.SetPointAt(anchor, new Point2d(target.X, target.Y));
        }

        /// <summary>重写实体 XData：新批注 Id + 新角色 + 新的内嵌数据分片。
        /// 三样必须一起换——只换 Id 会把旧批注的数据留在分片里（编组丢失时回退读内嵌数据会读到旧批注）。</summary>
        private static void RewriteAnnotationXData(Entity e, string newId, string role, AnnotationData data)
        {
            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AnnotationCodec.AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, newId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, role));
            if (data != null) foreach (var chunk in AnnotationCodec.BuildChunks(data)) rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, chunk));
            e.XData = rb;
        }

        /// <summary>编辑/移动/删除前的按需自动修复：仅当实体带内嵌数据且脱离编组时触发（复制场景），旧版损坏数据不误处理。</summary>
        public static void AutoRepairIfNeeded(Document doc, ObjectId entityId)
        {
            try
            {
                using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !TryGetId(entity, out var id)) return;
                    if (IsGroupLinked(tr, entity)) return;
                    if (!TryReadEmbedded(entity, out _)) return;
                    var groups = (DBDictionary)tr.GetObject(entity.Database.GroupDictionaryId, OpenMode.ForRead);
                    var map = CollectAnnotatedEntities(tr, entity.Database);
                    var repairedData = new List<AnnotationData>();
                    if (map.TryGetValue(id, out var list)) RepairBatch(tr, groups, id, list, null, repairedData);
                    tr.Commit();
                    foreach (var data in repairedData) AnnotationHistoryStore.Record(doc, "复制修复", data, "编组不随复制迁移，已重建编组");
                }
            }
            catch (System.Exception ex) { PluginLog.Error("AutoRepairIfNeeded", ex); }
        }

        private static bool TryGetId(Entity entity, out string id)
        {
            id = null; var rb = entity.GetXDataForApplication(AnnotationCodec.AppName); if (rb == null) return false;
            var values = rb.AsArray(); if (values.Length < 2) return false; id = values[1].Value as string; return !string.IsNullOrWhiteSpace(id);
        }

        internal static AnnotationSettings SettingsForRegion(Document doc,AnnotationSettings source,Point3d first,Point3d second){var (_,w,h)=UcsAlignedExtents(doc,first,second);return ResolveEffectiveSettings(doc,source,new AnnotationData(),Math.Sqrt(w*w+h*h));}
        // 渐变云线的线宽走逐段顶点宽度，预览时不能再写全局宽度（会抹平渐变；逐段宽度不一致时写全局宽度还可能抛 eInvalidInput）。
        internal static void ApplyPreviewAppearance(Entity entity,AnnotationSettings settings,short color,string role){entity.Color=Color.FromColorIndex(ColorMethod.ByAci,color);if(entity is Polyline poly&&settings.LineWidth>0&&(role=="cloud"||role=="leader")&&!(role=="cloud"&&settings.CloudStyle=="渐变"))SafeSetConstantWidth(poly,settings.LineWidth);}
        internal enum InteractionStatus { Accepted, Cancelled, Failed }
        internal readonly struct InteractionResult
        {
            public InteractionStatus Status { get; }
            public PromptStatus PromptStatus { get; }
            public string Stage { get; }
            public Point3d Point { get; }
            public bool FinishRequested { get; }

            private InteractionResult(
                InteractionStatus status,
                PromptStatus promptStatus,
                string stage,
                Point3d point,
                bool finishRequested = false)
            {
                Status = status;
                PromptStatus = promptStatus;
                Stage = stage;
                Point = point;
                FinishRequested = finishRequested;
            }

            /// <summary>把 <c>Editor.Drag</c> 的返回值翻译成本插件的交互结果。
            /// <para><b>必须判空</b>：<c>PromptResult</c> 是引用类型，而 <c>Editor.Drag(Jig)</c> 在
            /// "Jig 自己从 Sampler 返回 <c>SamplerStatus.Cancel</c>"（用户回车结束、面板改选项中止）时
            /// <b>会返回 null 而不是带 Status 的结果对象</b>。原先直接取 <c>result.Status</c> 就抛
            /// NullReferenceException，日志里表现为「PL线 一进去就报错」。</para>
            /// <para>null 一律按"取消"处理；真正的"回车结束"由 Jig 自己的标志
            /// （<paramref name="finishRequested"/>）表达，调用方据此决定是收尾还是中止。</para></summary>
            public static InteractionResult From(
                PromptResult result,
                string stage,
                Point3d point,
                bool finishRequested = false)
            {
                if (result == null)
                {
                    return finishRequested
                        ? new InteractionResult(InteractionStatus.Accepted, PromptStatus.OK, stage, point, true)
                        : new InteractionResult(InteractionStatus.Cancelled, PromptStatus.Cancel, stage, point, false);
                }

                var status = result.Status == PromptStatus.OK
                    ? InteractionStatus.Accepted
                    : result.Status == PromptStatus.Cancel ||
                      result.Status == PromptStatus.None
                        ? InteractionStatus.Cancelled
                        : InteractionStatus.Failed;
                return new InteractionResult(
                    status,
                    result.Status,
                    stage,
                    point,
                    finishRequested);
            }

            /// <summary>直接构造"取消"结果（拖拽本身抛异常、或要被外部中止时用）。</summary>
            public static InteractionResult Cancelled(string stage, Point3d point)
                => new InteractionResult(InteractionStatus.Cancelled, PromptStatus.Cancel, stage, point, false);

            /// <summary>直接构造"用户已结束输入"结果（回车/空格收尾）。</summary>
            public static InteractionResult Finished(string stage, Point3d point)
                => new InteractionResult(InteractionStatus.Accepted, PromptStatus.OK, stage, point, true);
        }

        /// <summary>放置预览用的批注框：真实内容的 MText + 与落图同一套口径量出的框宽高与文字边距。
        /// <para>Text 的生命周期由 <see cref="PromptPlacement"/> 管理（拖拽结束后统一释放），预览 Jig 不接管。</para></summary>
        internal sealed class PreviewTextBox : IDisposable
        {
            public MText Text;
            public double Width;
            public double Height;
            public double Margin;

            public void Dispose()
            {
                try { Text?.Dispose(); } catch { /* 预览对象，释放失败可忽略 */ }
                Text = null;
            }
        }

        /// <summary>
        /// 构造"定位批注框"阶段的预览文字与框尺寸。
        /// <para><b>口径与落图完全一致</b>：同一个 requestedWidth 公式 → MeasureHeaderWidth 保首行不折行 →
        /// ApplyTextStyle → <see cref="MeasureTextBoxFromMText"/> 量框（对照 <see cref="Create"/> 与
        /// <see cref="CreateMultiCloud"/>）。早先用 MeasureTextBox 另起一套估算，量法不同会让预览框与落图框
        /// 尺寸不符、文字版式也看不出差别；换成同一套口径后，放置时看到的框就是最终得到的框，
        /// 可以按内容的实际大小去找位置。</para>
        /// <para><b>必须在拖拽开始前调用</b>：量文字框要开事务读文字样式表，属于访问数据库，
        /// 放进 WorldDraw（拖拽回调）会让整个预览帧作废。</para>
        /// </summary>
        internal static PreviewTextBox BuildPreviewTextBox(Document doc, AnnotationData data, AnnotationSettings settings)
        {
            var result = new PreviewTextBox();
            var margin = TextBoxMargin(settings);
            try
            {
                // 与 Create/CreateMultiCloud 逐字一致的宽度公式，公式不同会导致落图后外框"撑开"。
                var requestedWidth = settings.FixedWidth
                    ? settings.FixedWidthValue
                    : Math.Max(55.0, settings.TextHeight * 18.0);
                // MText 建在事务外：事务只用来读文字样式表与首行宽度，Abort 不会牵连这个对象。
                var text = new MText
                {
                    Location = Point3d.Origin,
                    TextHeight = settings.TextHeight,
                    Width = requestedWidth,
                    Contents = FormatText(data, settings),
                    Attachment = AttachmentPoint.BottomLeft
                };
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    ApplyTextStyle(doc.Database, tr, text, settings.TextStyleName);
                    requestedWidth = Math.Max(requestedWidth, MeasureHeaderWidth(doc.Database, tr, data, settings));
                    text.Width = requestedWidth;
                    tr.Abort(); // 只读探测，不改数据库
                }

                MeasureTextBoxFromMText(text, data, settings, out var width, out var height, out margin);
                // 文字颜色与落图同一来源（状态色），预览里看到的就是最终颜色。
                ApplyPreviewAppearance(text, settings, TextColorFor(settings, data.Status), "text");
                result.Text = text;
                result.Width = width;
                result.Height = height;
                result.Margin = margin;
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("PreviewTextBox", ex.Message);
            }

            // 兜底：量不到尺寸时给一个看得见的框。BuildBox 遇到 NaN/0 尺寸会抛 eInvalidInput，
            // 异常一旦从 WorldDraw 抛出，整帧预览同样会被丢弃。
            if (double.IsNaN(result.Width) || result.Width <= 1e-6) result.Width = Math.Max(settings.TextHeight * 8, 10);
            if (double.IsNaN(result.Height) || result.Height <= 1e-6) result.Height = Math.Max(settings.TextHeight * 2, 10);
            if (double.IsNaN(result.Margin) || result.Margin <= 1e-6) result.Margin = margin;
            return result;
        }

        /// <summary>未传批注数据时的占位预览框：保留原有"批注内容/图号"占位观感，供兜底调用方使用。</summary>
        internal static PreviewTextBox BuildPlaceholderTextBox(Document doc, AnnotationSettings settings)
        {
            return BuildPreviewTextBox(
                doc,
                new AnnotationData
                {
                    Content = "批注内容",
                    DrawingNo = "图号",
                    Status = "待处理",
                    Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    Discipline = "专业",
                    Author = "批注人"
                },
                settings);
        }

        /// <summary>批注框定位：框内文字与框体随光标移动，云线/引线同时显示，所见即落图效果。
        /// <para>data 非空时按真实批注内容量框并画出真实文字（配合"先填内容再定位"的流程）；
        /// 为空时退回占位文案。</para>
        /// <para><paramref name="fixedAnchor"/> 仅 <see cref="PlacementGeometryKind.BasePoint"/> 用：
        /// 引线起点固定在这个点（WCS）上，而不是从云线轮廓上取。</para></summary>
        internal static InteractionResult PromptPlacement(
            Document doc,
            AnnotationSettings settings,
            AnnotationSettings source,
            IList<Point3d> firsts,
            IList<Point3d> seconds,
            IList<Point3d> polygon,
            Point3d initial,
            PlacementGeometryKind kind,
            AnnotationData data = null,
            Point3d fixedAnchor = default(Point3d))
        {
            // 预览文字与框尺寸在拖拽前备好，WorldDraw 里只做绘制——见 BuildPreviewTextBox 的说明。
            var box = data != null
                ? BuildPreviewTextBox(doc, data, settings)
                : BuildPlaceholderTextBox(doc, settings);
            var jig = new PlacementPreviewJig(
                doc,
                settings,
                source,
                firsts,
                seconds,
                polygon,
                initial,
                kind,
                box,
                fixedAnchor);
            try
            {
                PromptResult result;
                try { result = doc.Editor.Drag(jig); }
                finally { PreviewTransient.Clear(); }
                return InteractionResult.From(                    result,
                    "批注框定位",
                    jig.Current);
            }
            finally
            {
                jig.Dispose();
                box.Dispose();
            }
        }
        /// <summary>取一个 PL 折点（可被 <see cref="InteractionAbort"/> 中止）。
        /// <para><b>顺序不能颠倒</b>：先看 Jig 自己的 <c>FinishRequested</c> / <c>Aborted</c>，
        /// 再看 <c>Editor.Drag</c> 的返回值。原因有两层——
        /// ① "回车结束"和"面板改选项中止"这两种情况只有 Jig 自己知道；
        /// ② Jig 一旦从 Sampler 返回 <c>Cancel</c>，AutoCAD 可能给出 <b>null</b> 而不是 PromptResult
        /// （见 <see cref="InteractionResult.From"/>），此时去读 <c>result.Status</c> 就是 NullReferenceException。
        /// 区域流程（<see cref="PromptCloud"/>）一直是这么写的，PL 流程原先漏了。</para>
        /// <para><b>取点期间与矩形流程一样临时关掉正交/对象捕捉</b>（按 <see cref="AnnotationSettings.AutoCloseOrtho"/> /
        /// <see cref="AnnotationSettings.AutoCloseSnap"/>，两项默认都开）：不关的话 CAD 会把鼠标点下去的位置
        /// 自动吸附到附近已有对象上，用户拿不到"点哪儿就是哪儿"的折点，云线轮廓也跟着走样。
        /// 想沿既有图形的节点描轮廓，把设置里「自动关闭正交/捕捉」取消勾选即可。</para></summary>
        internal static InteractionResult PromptPlinePoint(
            Document doc,
            IList<Point3d> points,
            AnnotationSettings settings)
        {
            var jig = new PlinePointPreviewJig(doc, points, settings);
            PromptResult result;
            object ortho = null, osmode = null;
            try
            {
                // 与 PromptCloud 同口径：临时置 0，finally 里原样恢复（用户自己的设置不受影响）。
                if (settings.AutoCloseOrtho) { ortho = CadSystemVariable("ORTHOMODE"); SetCadSystemVariable("ORTHOMODE", 0); }
                if (settings.AutoCloseSnap) { osmode = CadSystemVariable("OSMODE"); SetCadSystemVariable("OSMODE", 0); }
                result = doc.Editor.Drag(jig);
            }
            catch (System.Exception ex)
            {
                // 拖拽本身抛异常（极少见）：记日志并按取消处理，别让它冒泡成 CAD 致命错误。
                PluginLog.Error("PromptPlinePoint.Drag", ex);
                return InteractionResult.Cancelled("PL 点选择", jig.Current);
            }
            finally
            {
                PreviewTransient.Clear();
                if (ortho != null) SetCadSystemVariable("ORTHOMODE", ortho);
                if (osmode != null) SetCadSystemVariable("OSMODE", osmode);
            }

            // 先认 Jig 的标志（见上面的说明）。
            if (jig.FinishRequested) return InteractionResult.Finished("PL 点选择", jig.Current);
            if (jig.Aborted) return InteractionResult.Cancelled("PL 点选择", jig.Current);

            if (result == null)
            {
                PluginLog.Info(
                    "PromptPlinePoint",
                    "Editor.Drag 返回 null（Jig 自行 Cancel）：Sampled=" + jig.HasSample);
                return InteractionResult.Cancelled("PL 点选择", jig.Current);
            }

            return InteractionResult.From(result, "PL 点选择", jig.Current, false);
        }


        /// <summary>批注框内文字版式（自上而下）：
        /// 日期：2026-09-15    专业：室内    批注人：陈
        /// 批注：……
        /// 图号：……
        /// 状态：……
        /// 首行固定为「日期 / 专业 / 批注人」三项且保持同一行（按设置勾选拼接，全部取消则整行省略）；
        /// "批注"行<b>始终显示</b>（内容为空时就是「批注：」）；"图号""状态"按各自开关出现。
        /// 编号仍在数据里（批注列表、CSV 导出/导入匹配都要用），但不再显示在图内。
        /// <para>内容为空也是允许的（"先落框、后补内容"）：此时"批注："这一行仍然照常显示，
        /// 只是冒号后面没有内容，方便看清"这里还有一条没写内容的批注"。</para></summary>
        internal static string FormatText(AnnotationData d,AnnotationSettings s)
        {
            var lines=new List<string>();
            var header=new List<string>();
            if(s.ShowDate)header.Add("日期："+Escape(d.Date));
            if(s.ShowDiscipline)header.Add("专业："+Escape(d.Discipline));
            if(s.ShowAuthor)header.Add("批注人："+Escape(d.Author)+RoleSuffix(d,s));
            else if(s.ShowRole)header.Add("角色："+Escape(d.Role));
            if(header.Count>0)lines.Add($"\\H{s.HeaderHeight:0.###};"+string.Join("    ",header));
            // 正文：**始终**出这一行。内容为空时就是孤零零一个"批注："——
            // 这样图上一眼能看出"这条批注还没写内容"，而不是让这一行凭空消失。
            var content=(d.Content??"").Trim();
            lines.Add($"\\H{s.TextHeight:0.###};批注："+Escape(content).Replace("\r\n", "\\P").Replace("\n", "\\P"));
            if(s.ShowDrawingNo)lines.Add($"\\H{s.SecondLineHeight:0.###};图号："+Escape(d.DrawingNo));
            if(s.ShowStatus)lines.Add($"\\H{s.SecondLineHeight:0.###};状态："+Escape(d.Status));
            // 兜底：所有可选行都被关掉时至少给一个空白行，免得 MText 零尺寸、文字框退化成一个点。
            if(lines.Count==0)lines.Add($"\\H{s.TextHeight:0.###}; ");
            return string.Join("\\P",lines);
        }
        /// <summary>角色后缀：角色与默认值"批注人"相同时不追加，避免显示成"批注人：陈（批注人）"。</summary>
        private static string RoleSuffix(AnnotationData d,AnnotationSettings s)
        {
            if(!s.ShowRole)return "";
            var role=(d.Role??"").Trim();
            if(role.Length==0||role=="批注人")return "";
            return "（"+Escape(role)+"）";
        }
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        /// <summary>首行（日期/专业/批注人）不折行所需的最小文字宽度：用只含首行的临时 MText 实测。
        /// 默认宽度（字高×18，或固定宽度值）对长首行常常不够，MText 会把"专业：××"折到第二行——
        /// 所有创建/更新路径的文字宽度都必须先与本值取大。</summary>
        internal static double MeasureHeaderWidth(Database db,Transaction tr,AnnotationData d,AnnotationSettings s)
        {
            var header=new List<string>();
            if(s.ShowDate)header.Add("日期："+Escape(d.Date));
            if(s.ShowDiscipline)header.Add("专业："+Escape(d.Discipline));
            if(s.ShowAuthor)header.Add("批注人："+Escape(d.Author)+RoleSuffix(d,s));
            else if(s.ShowRole)header.Add("角色："+Escape(d.Role));
            if(header.Count==0)return 0;
            try
            {
                using(var probe=new MText{Location=Point3d.Origin,TextHeight=s.TextHeight,Contents=$"\\H{s.HeaderHeight:0.###};"+string.Join("    ",header),Attachment=AttachmentPoint.BottomLeft})
                {
                    ApplyTextStyle(db,tr,probe,s.TextStyleName);
                    // 加一个字高的安全余量：CAD 对部分字体的实测宽与最终渲染宽有出入，
                    // 不留余量时首行最后一个字（如"批注人：陈工"的"工"）会被折到第二行。
                    return probe.ActualWidth + s.TextHeight; // 不设 Width → 不换行，量出的就是整行宽
                }
            }
            catch(System.Exception ex){PluginLog.Warning("MeasureHeaderWidth",ex.Message);return 0;}
        }
        /// <summary>状态色覆盖值：状态为「已回复」/「已完成」且开启了「按状态整条着色」时返回该状态的颜色，
        /// 其余情况返回 <c>null</c>（＝"按各元素自己的基础色走"）。
        /// <para><b>这是"整条批注换色"的唯一入口</b>：云线、文字、边框、引线、基点标识在落图、更新、预览时
        /// 都先问它，有值就一律用这个颜色 —— 状态是整条批注的属性，不是某一个元素的属性。
        /// 所以"已回复/已完成"的颜色一改，整条批注的所有元素都跟着变。</para>
        /// <para>与 <see cref="AnnotationSettings.SameColors"/> 无关：统一颜色管"各元素是否同色"，
        /// 本开关管"整条批注要不要按状态换色"。</para></summary>
        internal static short? StatusColorOverride(AnnotationSettings settings, string status)
        {
            if (settings == null || !settings.StatusColorize) return null;
            if (string.Equals(status, "已完成", StringComparison.OrdinalIgnoreCase)) return settings.PassColor;
            if (string.Equals(status, "已回复", StringComparison.OrdinalIgnoreCase)) return settings.ReplyColor;
            return null;
        }

        /// <summary>云线颜色（状态色优先）。</summary>
        internal static short CloudColorFor(AnnotationSettings s, string status)
            => StatusColorOverride(s, status) ?? s.CloudColor;
        /// <summary>边框颜色（状态色优先）。</summary>
        internal static short BoxColorFor(AnnotationSettings s, string status)
            => StatusColorOverride(s, status) ?? (s.SameColors ? s.CloudColor : s.BoxColor);
        /// <summary>引线颜色（状态色优先）。基点标识也走它 —— 两者都是"指着图纸上某个位置"的辅助几何。</summary>
        internal static short LeaderColorFor(AnnotationSettings s, string status)
            => StatusColorOverride(s, status) ?? s.LeaderColor;
        /// <summary>文字颜色（状态色优先）。</summary>
        internal static short TextColorFor(AnnotationSettings s, string status)
            => StatusColorOverride(s, status) ?? (s.SameColors ? s.ColorIndex : s.TextColor);

        /// <summary>按 XData 角色取"基础色"（<b>不含状态覆盖</b>）。
        /// <para>状态从「已回复/已完成」退回「待处理」时用它把各元素颜色恢复回去——
        /// 取值口径必须与各创建路径（<c>Add(..., role, ...)</c>）完全一致，否则一进一出颜色就漂了。</para></summary>
        internal static short BaseColorForRole(AnnotationSettings s, string role)
        {
            if (role == "cloud") return s.CloudColor;
            if (role == "box") return s.SameColors ? s.CloudColor : s.BoxColor;
            if (role == "leader" || role == "marker") return s.LeaderColor;
            return s.SameColors ? s.ColorIndex : s.TextColor; // text / 未知角色
        }
        /// <summary>实体<b>当前</b>颜色是否等于某个状态色（「已回复」或「已完成」的颜色）。
        /// <para>只用在"状态退回待处理、要清掉上一个状态留下的颜色"这一处：
        /// 加这个判断是为了不碰用户手工在特性面板里改过的颜色。</para></summary>
        private static bool HasStatusColor(AnnotationSettings s, Entity e)
        {
            if (e == null || e.Color == null || e.Color.ColorMethod != ColorMethod.ByAci) return false;
            var c = e.Color.ColorIndex;
            return c == s.ReplyColor || c == s.PassColor;
        }
        internal static Polyline BuildBox(Point3d p, double w, double h) { var x=new Polyline();x.AddVertexAt(0,new Point2d(p.X,p.Y),0,0,0);x.AddVertexAt(1,new Point2d(p.X+w,p.Y),0,0,0);x.AddVertexAt(2,new Point2d(p.X+w,p.Y+h),0,0,0);x.AddVertexAt(3,new Point2d(p.X,p.Y+h),0,0,0);x.Closed=true;return x; }
        /// <summary>
        /// 边距：取首行/正文/次行字高最大值的一半，避免字高不一致时上下左右贴边、内容溢出外框。
        /// </summary>
        internal static double TextBoxMargin(AnnotationSettings settings)
        {
            var h = Math.Max(settings.TextHeight, Math.Max(settings.HeaderHeight, settings.SecondLineHeight));
            return Math.Max(0.05, h * 0.5);
        }

        /// <summary>
        /// 量出批注文字框宽高（外框必须包住全部批注信息）。
        /// 取「MText 实测」与「按版式估算」的较大值，再加四周边距；
        /// ActualHeight 在未入库/部分字体下会偏小或为 0，单靠实测会裁切首行或末行。
        /// </summary>
        internal static void MeasureTextBox(Document doc, AnnotationData data, AnnotationSettings settings, double requestedWidth, out double width, out double height)
        {
            var margin = TextBoxMargin(settings);
            var minWidth = settings.TextHeight * 4.0;
            width = Math.Max(requestedWidth, minWidth) + margin * 2.0;
            height = EstimateBoxHeight(data, settings) + margin * 2.0;
            try
            {
                using (var probe = new MText
                {
                    Location = Point3d.Origin,
                    TextHeight = settings.TextHeight,
                    Width = requestedWidth,
                    Contents = FormatText(data, settings),
                    Attachment = AttachmentPoint.BottomLeft
                })
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        ApplyTextStyle(doc.Database, tr, probe, settings.TextStyleName);
                        var headerNeed = MeasureHeaderWidth(doc.Database, tr, data, settings);
                        if (headerNeed > requestedWidth)
                        {
                            requestedWidth = headerNeed;
                            probe.Width = requestedWidth;
                        }
                        tr.Abort();
                    }

                    width = Math.Max(Math.Max(probe.ActualWidth, requestedWidth), minWidth) + margin * 2.0;
                    var measured = probe.ActualHeight;
                    var estimated = EstimateBoxHeight(data, settings);
                    var contentH = measured > 1e-6 ? Math.Max(measured, estimated) : estimated;
                    height = Math.Max(contentH, settings.TextHeight * 2.0) + margin * 2.0;
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("MeasureTextBox", ex.Message);
            }
        }

        /// <summary>
        /// 已有 MText（创建/更新事务内）时计算外框尺寸：实测与版式估算取大，保证包住全部信息。
        /// </summary>
        internal static void MeasureTextBoxFromMText(MText text, AnnotationData data, AnnotationSettings settings, out double width, out double height, out double margin)
        {
            margin = TextBoxMargin(settings);
            var minWidth = Math.Max(text.TextHeight, settings.TextHeight) * 4.0;
            var estimated = EstimateBoxHeight(data, settings);
            var measuredW = 0.0;
            var measuredH = 0.0;
            try { measuredW = text.ActualWidth; } catch { }
            try { measuredH = text.ActualHeight; } catch { }
            width = Math.Max(Math.Max(measuredW, text.Width), minWidth) + margin * 2.0;
            var contentH = measuredH > 1e-6 ? Math.Max(measuredH, estimated) : estimated;
            height = Math.Max(contentH, settings.TextHeight * 2.0) + margin * 2.0;
        }

        /// <summary>
        /// 量不到 MText 实际高度时的兜底估算：按首行/正文/图号/状态各自字高累加，
        /// 再乘 MText 默认行距系数 1.7，避免实测偏小时外框裁切内容。
        /// </summary>
        private static double EstimateBoxHeight(AnnotationData data, AnnotationSettings settings)
        {
            var total = 0.0;
            var lines = 0;
            if (settings.ShowDate || settings.ShowDiscipline || settings.ShowAuthor || settings.ShowRole)
            {
                total += Math.Max(0.1, settings.HeaderHeight);
                lines++;
            }

            var content = (data.Content ?? "").Replace("\r\n", "\n");
            var contentLines = Math.Max(1, content.Split(new[] { '\n' }, StringSplitOptions.None).Length);
            total += Math.Max(0.1, settings.TextHeight) * contentLines;
            lines += contentLines;

            if (settings.ShowDrawingNo)
            {
                total += Math.Max(0.1, settings.SecondLineHeight);
                lines++;
            }

            if (settings.ShowStatus)
            {
                total += Math.Max(0.1, settings.SecondLineHeight);
                lines++;
            }

            // 行距：MText AtLeast 默认约 1.66~1.7；多行时按总高 * 系数更稳
            var spaced = total * (lines <= 1 ? 1.0 : 1.7);
            return Math.Max(settings.TextHeight * 2.0, spaced);
        }
        /// <summary>云线弧瓣弦长。未自适应时 = 2×半径（老算法）；自适应时按周长定瓣数，每瓣仍是 2×半径，
        /// 只让数量随框大小增减。
        /// <para>原来自适应是"按字高的 1.2 倍定瓣数、封顶 160 瓣"：字高一旦相对图面偏大（图形单位不是毫米的
        /// 总图就是这种情况），瓣数被压到下限、每瓣又被拉得很长，画出来是一条大波浪而不是云线——
        /// 这正是总图上"云线比例不对"的来源。改成按半径定，弧瓣永远和设置里的半径一致。</para></summary>
        private static double CloudSpacing(double perimeter, AnnotationSettings settings, double fallback)
        {
            if(!settings.CloudAutoFit) return fallback;
            var radius=Math.Max(settings.CloudRadius,1e-6);
            var desired=(int)Math.Ceiling(perimeter/Math.Max(radius*2.0,1e-6));
            if(desired<20)desired=20;
            if(desired>400)desired=400;
            return perimeter/desired;
        }

        /// <summary>构建云线多段线，支持矩形/菱形/椭圆三种外形，开启 CloudAutoFit 时自适应弧段数。</summary>
        internal static Polyline BuildCloud(Point2d min, Point2d max, AnnotationSettings settings)
        {
            var radius=Math.Max(settings.CloudRadius,0.001);
            var w=Math.Max(0,max.X-min.X);var h=Math.Max(0,max.Y-min.Y);
            var perimeter=ComputePerimeter(w,h,settings.Shape);
            if(perimeter<1e-9){var empty=new Polyline();empty.AddVertexAt(0,min,0,0,0);empty.AddVertexAt(1,max,0,0,0);return empty;}
            var spacing=Math.Max(0.1,radius*2);
            spacing=CloudSpacing(perimeter,settings,spacing);
            // 菱形：使用框选云线专用的四边细分，不进入 PL 多边形校验。
            if(settings.Shape=="菱形"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;return BuildRegionCloudPath(new[]{new Point2d(cx,min.Y),new Point2d(max.X,cy),new Point2d(cx,max.Y),new Point2d(min.X,cy)},spacing,settings.CloudStyle,settings.LineWidth);}
            // 椭圆：自适应采样点数
            if(settings.Shape=="椭圆"){var cx=(min.X+max.X)/2;var cy=(min.Y+max.Y)/2;var rx=w/2;var ry=h/2;var count=Math.Max(20,Math.Min(400,(int)Math.Ceiling(perimeter/spacing)));var points=new List<Point2d>();for(var i=0;i<count;i++){var a=2*Math.PI*i/count;points.Add(new Point2d(cx+rx*Math.Cos(a),cy+ry*Math.Sin(a)));}return BuildScallopedVertices(points,settings.CloudStyle,settings.LineWidth);}
            // 矩形：按宽高分别均匀布点，与任意折点的 PL 云线算法完全分离。
            var nx=Math.Max(4,(int)Math.Ceiling(w/spacing));var ny=Math.Max(4,(int)Math.Ceiling(h/spacing));
            const int maxVertices=400;var total=2*(nx+ny);if(total>maxVertices){var scale=(double)maxVertices/total;nx=Math.Max(4,(int)Math.Floor(nx*scale));ny=Math.Max(4,(int)Math.Floor(ny*scale));}
            var rectanglePoints=new List<Point2d>(2*(nx+ny));
            for(var i=0;i<nx;i++)rectanglePoints.Add(new Point2d(min.X+w*i/nx,min.Y));
            for(var i=0;i<ny;i++)rectanglePoints.Add(new Point2d(max.X,min.Y+h*i/ny));
            for(var i=0;i<nx;i++)rectanglePoints.Add(new Point2d(max.X-w*i/nx,max.Y));
            for(var i=0;i<ny;i++)rectanglePoints.Add(new Point2d(min.X,max.Y-h*i/ny));
            return BuildScallopedVertices(rectanglePoints,settings.CloudStyle,settings.LineWidth);
        }
        /// <summary>计算各外形的近似周长，用于自适应弧段数。</summary>
        private static double ComputePerimeter(double w, double h, string shape)
        {
            if(shape=="菱形") return 2*Math.Sqrt(w*w+h*h);        // 菱形周长 = 2√(w²+h²)
            if(shape=="椭圆") return Math.PI*(w+h)/2*1.05;        // 椭圆近似周长
            return 2*(w+h);                                        // 矩形周长
        }
        /// <summary>按当前 UCS 对齐的四个 WCS 角点构建框选云线。</summary>
        internal static Polyline BuildRegionCloud(Point2d[] corners,AnnotationSettings settings)
        {
            if(settings.Shape=="菱形")
            {
                var diamond=new[]{MidPoint(corners[0],corners[1]),MidPoint(corners[1],corners[2]),MidPoint(corners[2],corners[3]),MidPoint(corners[3],corners[0])};
                return BuildCloudFromUcsCorners(diamond,settings);
            }
            if(settings.Shape=="椭圆")
            {
                var center=new Point2d((corners[0].X+corners[2].X)/2,(corners[0].Y+corners[2].Y)/2);
                var axisX=new Vector2d((corners[1].X-corners[0].X)/2,(corners[1].Y-corners[0].Y)/2);
                var axisY=new Vector2d((corners[3].X-corners[0].X)/2,(corners[3].Y-corners[0].Y)/2);
                var perimeter=Math.PI*(axisX.Length+axisY.Length)*1.05;var spacing=Math.Max(settings.CloudRadius*2,0.1);
                spacing=CloudSpacing(perimeter,settings,spacing);
                var count=Math.Max(20,Math.Min(400,(int)Math.Ceiling(perimeter/spacing)));var points=new List<Point2d>(count);
                for(var i=0;i<count;i++){var angle=2*Math.PI*i/count;points.Add(center+axisX*Math.Cos(angle)+axisY*Math.Sin(angle));}
                return BuildScallopedVertices(points,settings.CloudStyle,settings.LineWidth);
            }
            return BuildCloudFromUcsCorners(corners,settings);
        }

        private static Point2d MidPoint(Point2d a,Point2d b)=>new Point2d((a.X+b.X)/2,(a.Y+b.Y)/2);

        /// <summary>用四个角点（WCS）构建 UCS 对齐云线（仅矩形外形）。</summary>
        internal static Polyline BuildCloudFromUcsCorners(Point2d[] wcsCorners,AnnotationSettings settings)
        {
            var spacing=Math.Max(settings.CloudRadius*2,0.1);
            var perimeter=0.0;for(var i=0;i<wcsCorners.Length;i++)perimeter+=wcsCorners[i].GetDistanceTo(wcsCorners[(i+1)%wcsCorners.Length]);
            spacing=CloudSpacing(perimeter,settings,spacing);
            return BuildRegionCloudPath(wcsCorners,spacing,settings.CloudStyle,settings.LineWidth);
        }

        /// <summary>框选区域专用云线路径细分；矩形和菱形不经过 PL 折点校验。</summary>
        private static Polyline BuildRegionCloudPath(IList<Point2d> corners,double spacing,string style,double lineWidth)
        {
            var points=new List<Point2d>();
            for(var i=0;i<corners.Count;i++)
            {
                var a=corners[i];var b=corners[(i+1)%corners.Count];
                var count=Math.Min(100,Math.Max(4,(int)Math.Ceiling(a.GetDistanceTo(b)/Math.Max(spacing,0.1))));
                for(var j=0;j<count;j++)points.Add(new Point2d(a.X+(b.X-a.X)*j/count,a.Y+(b.Y-a.Y)*j/count));
            }
            return BuildScallopedVertices(points,style,lineWidth);
        }

        internal static Point2d ResolveRegionLeaderAnchor(
            Polyline cloud,
            Point2d min,
            Point2d max,
            AnnotationSettings settings,
            Point3d target)
        {
            // 椭圆/菱形：包围盒四角都不在外形轮廓上（菱形的四个尖角在包围盒边中点），
            // 引线必须接到云线轮廓本身的最近点，否则线头悬空、与外形不相连。
            if (settings.Shape == "椭圆" || settings.Shape == "菱形")
            {
                var closest = cloud.GetClosestPointTo(target, false);
                return new Point2d(closest.X, closest.Y);
            }

            return ClosestCorner(min, max, target);
        }

        internal static Point2d ClosestCorner(Point2d min,Point2d max,Point3d p){var target=new Point2d(p.X,p.Y);var a=new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)};return a.OrderBy(x=>x.GetDistanceTo(target)).First();}
        /// <summary>给定 4 个 WCS 角点，找离目标最近的角。</summary>
        internal static Point2d ClosestCorner4(Point2d[] corners,Point3d p){var target=new Point2d(p.X,p.Y);return corners.OrderBy(c=>c.GetDistanceTo(target)).First();}
        private static bool Contains(Group group,ObjectId id){foreach(ObjectId member in group.GetAllEntityIds())if(member==id)return true;return false;}
        // 渐变云线：线宽走逐段顶点宽度（BuildScallopedVertices 已写入），ConstantWidth 必须保持 0，否则 0 宽度端回退用 ConstantWidth 会抹平渐变。
        private static void Add(BlockTableRecord space,Transaction tr,Entity e,ObjectIdCollection ids,AnnotationSettings s,string id,string role,short color,AnnotationData data=null,string layerOverride=null){e.Layer=string.IsNullOrEmpty(layerOverride)?EffectiveLayer(s,data):layerOverride;e.Color=Color.FromColorIndex(ColorMethod.ByAci,color);if(e is Polyline p&&s.LineWidth>0&&(role=="cloud"||role=="leader")){if(role=="leader"||s.CloudStyle!="渐变")p.ConstantWidth=s.LineWidth;}var rb=new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName,AnnotationCodec.AppName),new TypedValue((int)DxfCode.ExtendedDataAsciiString,id),new TypedValue((int)DxfCode.ExtendedDataAsciiString,role));if(data!=null)foreach(var chunk in AnnotationCodec.BuildChunks(data))rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString,chunk));e.XData=rb;ids.Add(space.AppendEntity(e));tr.AddNewlyCreatedDBObject(e,true);}
        private static bool TryGetRole(Entity e,out string role){role=null;var rb=e.GetXDataForApplication(AnnotationCodec.AppName);if(rb==null)return false;var a=rb.AsArray();if(a.Length<3)return false;role=a[2].Value as string;return role!=null;}
        /// <summary>矩形、菱形、UCS 矩形和 PL 线共用的闭合多边形云线算法。</summary>
        internal static Polyline BuildPolygonCloud(IList<Point2d> sourcePoints,AnnotationSettings settings)
        {
            if(!TryNormalizePolygon(sourcePoints,out var corners,out var error))throw new ArgumentException(error);
            var lengths=new double[corners.Count];var perimeter=0.0;
            for(var i=0;i<corners.Count;i++){lengths[i]=corners[i].GetDistanceTo(corners[(i+1)%corners.Count]);perimeter+=lengths[i];}

            var spacing=Math.Max(settings.CloudRadius*2,0.1);
            spacing=CloudSpacing(perimeter,settings,spacing);

            var minimumPerEdge=corners.Count<=4?4:1;
            var segmentCounts=lengths.Select(length=>Math.Max(minimumPerEdge,(int)Math.Ceiling(length/spacing))).ToArray();
            const int maxVertices=400;
            var total=segmentCounts.Sum();
            if(total>maxVertices)
            {
                var scale=(double)maxVertices/total;
                for(var i=0;i<segmentCounts.Length;i++)segmentCounts[i]=Math.Max(1,(int)Math.Floor(segmentCounts[i]*scale));
                while(segmentCounts.Sum()>maxVertices)
                {
                    var index=Array.IndexOf(segmentCounts,segmentCounts.Max());
                    if(segmentCounts[index]<=1)break;
                    segmentCounts[index]--;
                }
            }

            var sampled=new List<Point2d>(Math.Min(maxVertices,segmentCounts.Sum()));
            for(var i=0;i<corners.Count;i++)
            {
                var a=corners[i];var b=corners[(i+1)%corners.Count];var count=segmentCounts[i];
                for(var j=0;j<count;j++)sampled.Add(new Point2d(a.X+(b.X-a.X)*j/count,a.Y+(b.Y-a.Y)*j/count));
            }
            return BuildScallopedVertices(sampled,settings.CloudStyle,settings.LineWidth);
        }

        /// <summary>去除重复点并统一为逆时针方向，使正 bulge 始终向多边形外侧鼓出。</summary>
        private static bool TryNormalizePolygon(IList<Point2d> sourcePoints,out List<Point2d> points,out string error)
        {
            points=new List<Point2d>();error=null;
            if(sourcePoints!=null)
            {
                foreach(var point in sourcePoints)
                {
                    if(points.Count==0||points[points.Count-1].GetDistanceTo(point)>1e-8)points.Add(point);
                }
            }
            if(points.Count>1&&points[0].GetDistanceTo(points[points.Count-1])<=1e-8)points.RemoveAt(points.Count-1);
            if(points.Count<3){error="PL 云线至少需要三个不同的点。";return false;}
            if(points.Count>400){error="PL 云线原始折点不能超过 400 个。";return false;}
            for(var i=0;i<points.Count;i++)
            {
                for(var j=i+1;j<points.Count;j++)
                {
                    if(points[i].GetDistanceTo(points[j])<=1e-8){error="PL 云线不能重复经过同一个折点。";return false;}
                }
            }
            if(HasSelfIntersection(points)){error="PL 云线边界不能自相交。";return false;}

            var signedArea=0.0;
            for(var i=0;i<points.Count;i++){var a=points[i];var b=points[(i+1)%points.Count];signedArea+=a.X*b.Y-b.X*a.Y;}
            var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
            var areaTolerance=Math.Max(1e-10,(maxX-minX)*(maxY-minY)*1e-10);
            if(Math.Abs(signedArea)*0.5<=areaTolerance){error="PL 云线的点不能全部共线或形成零面积区域。";return false;}
            if(signedArea<0)points.Reverse();
            return true;
        }

        private static bool HasSelfIntersection(IList<Point2d> points)
        {
            for(var i=0;i<points.Count;i++)
            {
                var a1=points[i];var a2=points[(i+1)%points.Count];
                for(var j=i+1;j<points.Count;j++)
                {
                    if(j==i||j==(i+1)%points.Count||(j+1)%points.Count==i)continue;
                    var b1=points[j];var b2=points[(j+1)%points.Count];
                    if(SegmentsIntersect(a1,a2,b1,b2))return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Point2d a,Point2d b,Point2d c,Point2d d)
        {
            var abC=Cross(a,b,c);var abD=Cross(a,b,d);var cdA=Cross(c,d,a);var cdB=Cross(c,d,b);
            const double tolerance=1e-10;
            if(((abC>tolerance&&abD<-tolerance)||(abC<-tolerance&&abD>tolerance))&&
               ((cdA>tolerance&&cdB<-tolerance)||(cdA<-tolerance&&cdB>tolerance)))return true;
            return Math.Abs(abC)<=tolerance&&OnSegment(a,b,c)||Math.Abs(abD)<=tolerance&&OnSegment(a,b,d)||
                   Math.Abs(cdA)<=tolerance&&OnSegment(c,d,a)||Math.Abs(cdB)<=tolerance&&OnSegment(c,d,b);
        }

        private static double Cross(Point2d a,Point2d b,Point2d p)=>(b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X);
        private static bool OnSegment(Point2d a,Point2d b,Point2d p)=>p.X>=Math.Min(a.X,b.X)-1e-10&&p.X<=Math.Max(a.X,b.X)+1e-10&&p.Y>=Math.Min(a.Y,b.Y)-1e-10&&p.Y<=Math.Max(a.Y,b.Y)+1e-10;

        internal static bool ValidateCloudPolygon(IList<Point2d> points,out string error)
        {
            return TryNormalizePolygon(points,out var ignored,out error);
        }
        /// <summary>沿顶点序列生成锯齿云线，始终闭合。顶点按逆时针排列，正 bulge（逆时针弧）向行进方向右侧、即多边形外侧鼓出。
        /// 等宽/渐变的<b>弧瓣几何完全一致</b>：顶点等距、bulge 全 0.55，弧瓣尺寸由云线半径（细分间距）决定，
        /// 每条边都是规整的弧瓣，无大小起伏（2026-09-16 定稿：旧版"弧瓣宽度沿周长递增"观感是上下边变成波浪线，已废弃）。
        /// 渐变与等宽的区别只在<b>描边</b>：渐变每段弧的线宽从 0 渐变到 <paramref name="lineWidth"/>（云线线宽），
        /// 起笔尖、收笔粗，逐瓣重复形成渐变笔画感；等宽整条恒定线宽（由创建方写 ConstantWidth）。
        /// 渐变云线的 ConstantWidth 必须保持 0——顶点宽度为 0 的那端会回退用 ConstantWidth，非 0 会把渐变抹平。</summary>
        internal static Polyline BuildScallopedVertices(IList<Point2d> points,string style,double lineWidth=0)
        {
            var p=new Polyline();
            var tapered=style=="渐变"&&lineWidth>0;
            for(var i=0;i<points.Count;i++)p.AddVertexAt(i,points[i],0,0,tapered?lineWidth:0);   // 每段弧：起始宽度 0 → 结束宽度＝云线线宽
            p.Closed=true;
            SetBulgePattern(p,style,1);return p;
        }

        /// <summary>给整条云线写弧瓣 bulge（不含方向，方向由 <paramref name="sign"/> 统一控制）。
        /// 等宽/渐变的弧瓣 bulge 均为 0.55——两种样式几何一致，区别只在描边宽度（渐变＝逐段 0→云线线宽）。</summary>
        internal static void SetBulgePattern(Polyline cloud,string style,double sign)
        {
            var n=cloud.NumberOfVertices;if(n<4||sign==0)return;
            for(var i=0;i<n;i++)cloud.SetBulgeAt(i,sign*0.55);
        }
        /// <summary>镜像 UCS 或背面视图下，逆时针环在屏幕呈顺时针，弧瓣会鼓向内侧。按当前视图翻转全部 bulge，使弧瓣始终在屏幕中外侧鼓出。</summary>
        internal static void OrientCloudBulgesForView(Document doc,Polyline cloud)
            => OrientCloudBulges(cloud, CloudBulgeSign(doc, cloud?.Normal ?? Vector3d.ZAxis));

        /// <summary>弧瓣朝向的翻转量：<b>+1 不翻、-1 整体翻转</b>。
        /// <para>判据与 <see cref="OrientCloudBulgesForView"/> 完全一致（法向背离相机时要翻），
        /// 只是把"问视图"这一步单独拆出来：<b>Jig 可以在拖拽开始前问一次并把结果存起来</b>，
        /// 拖拽期间只做纯几何运算。</para>
        /// <para><b>为什么必须拆</b>：WorldDraw 是拖拽回调，期间访问编辑器/数据库会让 AutoCAD
        /// 整体丢弃这一帧（见 PlacementPreviewJig 的说明）。原先 <c>OrientCloudBulgesForView</c>
        /// 直接在 WorldDraw 里 <c>Editor.GetCurrentView()</c>——这正是"PL 线拖拽预览不顺滑"、
        /// 以及多对一预览"每帧 × 每条历史云线各问一次视图"的根因。</para></summary>
        internal static double CloudBulgeSign(Document doc,Vector3d normalWcs)
        {
            // 法向指向相机 → 看到正面，无需翻转；指向背离相机 → 看到背面，翻转全部 bulge。
            try
            {
                using(var view=doc.Editor.GetCurrentView())
                    return normalWcs.DotProduct(view.ViewDirection)<0?-1.0:1.0;
            }
            catch{return 1.0;} // 视图不可用时保持原方向
        }

        /// <summary>按预先算好的翻转量改写整条云线的 bulge（不访问视图，可安全用于 WorldDraw）。</summary>
        internal static void OrientCloudBulges(Polyline cloud,double sign)
        {
            if(cloud==null||sign>=0)return;
            try
            {
                for(var i=0;i<cloud.NumberOfVertices;i++)
                    cloud.SetBulgeAt(i,-cloud.GetBulgeAt(i));
            }
            catch{ /* 预览对象，改不动就保持原方向 */ }
        }
        internal static string EffectiveLayer(AnnotationSettings s,AnnotationData data=null)
        {
            var date=DateTime.Today;
            if(data!=null&&!string.IsNullOrWhiteSpace(data.Date))
            {
                if(DateTime.TryParseExact(data.Date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var parsedDate))date=parsedDate;
            }
            var author=data!=null&&!string.IsNullOrWhiteSpace(data.Author)?data.Author:s.DefaultAuthor;
            var parts=new List<string>{s.LayerName};
            if(s.LayerAppendDate&&s.DateBeforeName)parts.Add(date.ToString("yyyyMMdd"));
            if(s.LayerAppendName)parts.Add(author);
            if(s.LayerAppendDate&&!s.DateBeforeName)parts.Add(date.ToString("yyyyMMdd"));
            var connector=s.Connector=="无"?"":s.Connector;
            return string.Join(connector,parts.Where(x=>!string.IsNullOrWhiteSpace(x)));
        }
        /// <summary>应用文字样式；若指定样式不存在则记录警告并回退为默认样式。</summary>
        private static void ApplyTextStyle(Database db,Transaction tr,MText text,string name)
        {
            if(string.IsNullOrWhiteSpace(name))return;
            var table=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
            if(table.Has(name)){text.TextStyleId=table[name];}
            else PluginLog.Warning("TextStyle",$"文字样式「{name}」在图中不存在，已回退为默认样式。");
        }
#if ZWCAD
        private static object CadSystemVariable(string name)=>ZwSoft.ZwCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>ZwSoft.ZwCAD.ApplicationServices.Core.Application.SetSystemVariable(name,value);
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
        private static object CadSystemVariable(string name)=>Gssoft.Gscad.ApplicationServices.Core.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>Gssoft.Gscad.ApplicationServices.Core.Application.SetSystemVariable(name,value);
#else
#if ACAD_PRE2013
        private static object CadSystemVariable(string name)=>Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable(name,value);
#else
        private static object CadSystemVariable(string name)=>Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
        private static void SetCadSystemVariable(string name,object value)=>Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(name,value);
#endif
#endif

        /// <summary>获取当前 UCS → WCS 变换矩阵。</summary>
        internal static Matrix3d GetUcsMatrix(Document doc){try{return doc.Editor.CurrentUserCoordinateSystem;}catch{return Matrix3d.Identity;}}
        /// <summary>WCS 点 → UCS 点。</summary>
        internal static Point3d WcsToUcs(Document doc,Point3d wcsPt){try{return wcsPt.TransformBy(GetUcsMatrix(doc).Inverse());}catch{return wcsPt;}}
        /// <summary>UCS 点 → WCS 点。</summary>
        internal static Point3d UcsToWcs(Document doc,Point3d ucsPt){try{return ucsPt.TransformBy(GetUcsMatrix(doc));}catch{return ucsPt;}}
        /// <summary>将 WCS 两点转为 UCS 对齐矩形（min, width, height）。</summary>
        internal static (Point3d origin,double w,double h) UcsAlignedExtents(Document doc,Point3d a,Point3d b){var ua=WcsToUcs(doc,a);var ub=WcsToUcs(doc,b);return (new Point3d(Math.Min(ua.X,ub.X),Math.Min(ua.Y,ub.Y),ua.Z),Math.Abs(ub.X-ua.X),Math.Abs(ub.Y-ua.Y));}

        private static void EnsureRegApp(Database db,Transaction tr){var t=(RegAppTable)tr.GetObject(db.RegAppTableId,OpenMode.ForRead);if(t.Has(AnnotationCodec.AppName))return;t.UpgradeOpen();var r=new RegAppTableRecord{Name=AnnotationCodec.AppName};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);}
        private static void EnsureLayer(Database db,Transaction tr,AnnotationSettings s,AnnotationData data=null)
        {
            var name=EffectiveLayer(s,data);var t=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            if(t.Has(name))
            {
                var existing=(LayerTableRecord)tr.GetObject(t[name],OpenMode.ForWrite);
                existing.IsPlottable=s.Plottable;
                return;
            }
            t.UpgradeOpen();var r=new LayerTableRecord{Name=name,Color=Color.FromColorIndex(ColorMethod.ByAci,s.CloudColor),IsPlottable=s.Plottable};t.Add(r);tr.AddNewlyCreatedDBObject(r,true);
        }
    }
}
