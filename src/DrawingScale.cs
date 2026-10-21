using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#elif GSCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace XAnnotation
{
    /// <summary>图面单位换算：把设置里的"打印毫米"折算成"图形单位"。
    /// <para>插件所有尺寸设置（字高、云线半径、线宽）都是<b>打印毫米</b>，落到图上必须乘一个
    /// "1 毫米 = 多少图形单位"的系数。原先直接拿「比例选型 1:N」当这个系数，隐含了
    /// "1 图形单位 = 1 毫米"——这对毫米制图纸成立，但总图/场地/测绘图常以<b>米</b>为单位
    /// （车位间距 2.4、建筑 14 就是 2.4m、14m），同一套设置会整体大 1000 倍：字高、
    /// 云线弧瓣全被撑到离谱。</para>
    /// <para>这里用"图内文字的实际字高"反推系数：常规图纸的注释文字打印出来就是 3mm 上下，
    /// 于是 <c>图内文字常见字高 ÷ 3mm</c> 就是 1mm 对应的图形单位数。米制 1:1000 总图得到 1
    /// （3 单位 = 3mm），常见的 1:100 毫米图纸得到 100（300 单位 = 3mm），两者都能算对。</para>
    /// <para><b>只在命令入口探测一次</b>（要开事务扫模型空间）；拖拽预览每帧只读缓存，
    /// 不在 WorldDraw 里碰数据库。</para></summary>
    internal static class DrawingScale
    {
        /// <summary>常规图纸注释文字的打印字高（mm）。</summary>
        private const double PaperTextMillimeters = 3.0;
        private const double MinUnitsPerMm = 0.01;     // 1 图形单位 = 100mm
        private const double MaxUnitsPerMm = 5000.0;   // 1 图形单位 = 0.0002mm
        private const int MinSamples = 5;

        /// <summary>本插件自己生成的批注图层前缀：统计时排除，免得自适应后的大字反过来污染基准。</summary>
        private const string OwnLayerPrefix = "X-批注";

        /// <summary>最多看这么多文字就够定基准了，大图上不必把整个模型空间走完。</summary>
        private const int MaxSamples = 400;

        private static double _unitsPerMm;
        private static int _samples;

        /// <summary>当前缓存是否属于"当前这张图"。
        /// <para><b>不要用 <c>ReferenceEquals(db, _probedDb)</c> 判断</b>：日志里出现过同一张图连续多次
        /// "重新识别"，说明 <c>Document.Database</c> 每次拿到的包装对象不保证是同一个实例，
        /// 引用比较永远不相等——缓存等于失效，识别值永远读不到，1.0.5 的比例修正也就等于没生效。
        /// 改成由文档事件驱动的显式失效（<see cref="Forget"/>）：激活/关闭图纸后清一次，
        /// 下一次 <see cref="Ensure"/> 重新探测。</para></summary>
        private static bool _valid;

        /// <summary>1 毫米对应多少图形单位；未探测或识别失败返回 0（调用方退回比例选型）。</summary>
        internal static double UnitsPerMillimeter(Document doc)
        {
            if (doc == null || !_valid) return 0;
            return _unitsPerMm;
        }

        /// <summary>探测并缓存当前图纸的单位系数。调用点必须是命令/文档事件上下文，不要放在拖拽回调里。</summary>
        internal static void Ensure(Document doc)
        {
            var db = doc == null ? null : doc.Database;
            if (db == null || _valid) return;

            var samples = 0;
            var value = 0.0;
            try { value = Probe(db, out samples); }
            catch (Exception ex)
            {
                PluginLog.Warning("DrawingScale", "识别图面单位失败，按比例选型换算：" + ex.Message);
            }

            _unitsPerMm = value;
            _samples = samples;
            _valid = true;

            if (value > 0)
            {
                PluginLog.Info("DrawingScale",
                    "图面单位识别：文字样本=" + samples + "，1mm≈" + value.ToString("0.###") +
                    " 图形单位（按 3mm 常规字高反推）。");
            }
            else
            {
                PluginLog.Warning("DrawingScale",
                    "图内文字样本不足（" + samples + " 个），本次按比例选型换算。");
            }
        }

        /// <summary>换图/换文档时清缓存（在 DocumentActivated / 关闭文档时调用）。</summary>
        internal static void Forget()
        {
            _unitsPerMm = 0;
            _samples = 0;
            _valid = false;
        }

        private static double Probe(Database db, out int sampleCount)
        {
            sampleCount = 0;
            var heights = new List<double>(MaxSamples);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity entity;
                    try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;

                    double height = 0;
                    try
                    {
                        var layer = entity.Layer;
                        if (!string.IsNullOrEmpty(layer) &&
                            layer.StartsWith(OwnLayerPrefix, StringComparison.Ordinal)) continue;

                        var text = entity as DBText;
                        if (text != null) height = text.Height;
                        else
                        {
                            var mtext = entity as MText;
                            if (mtext != null) height = mtext.Height;
                        }
                    }
                    catch { continue; }

                    if (height > 1e-9 && height < 1e9)
                    {
                        heights.Add(height);
                        // 大图（总图像素级几十万实体）没必要走完：样本够定四分位就收手。
                        if (heights.Count >= MaxSamples) break;
                    }
                }

                tr.Commit();
            }

            sampleCount = heights.Count;
            if (sampleCount < MinSamples) return 0;

            heights.Sort();
            // 取上四分位而不是中位数：图里往往混着一堆更小的字（坐标、井名、尺寸起止符），
            // 中位数会被它们拉低，算出来的图面换算系数偏小，批注字高/云线半径就跟着小一截。
            // 上四分位更接近"这张图的注释文字字高"。
            var index = (int)(sampleCount * 0.75);
            if (index >= sampleCount) index = sampleCount - 1;
            var typical = heights[index];
            var value = typical / PaperTextMillimeters;

            if (value < MinUnitsPerMm) value = MinUnitsPerMm;
            if (value > MaxUnitsPerMm) value = MaxUnitsPerMm;
            return value;
        }
    }
}
