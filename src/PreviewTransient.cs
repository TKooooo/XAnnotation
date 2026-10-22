#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;

namespace XAnnotation
{
    /// <summary>中望暂未启用瞬态预览（先只在 AutoCAD 上解决"拖拽淡显"）。</summary>
    internal static class PreviewTransient
    {
        internal static bool Enabled { get { return false; } }
        internal static void Replace(params Entity[] entities) { }
        internal static void Clear() { }
    }
}
#elif GSCAD
using Gssoft.Gscad.DatabaseServices;

namespace XAnnotation
{
    /// <summary>浩辰暂未启用瞬态预览（先只在 AutoCAD 上解决"拖拽淡显"）。</summary>
    internal static class PreviewTransient
    {
        internal static bool Enabled { get { return false; } }
        internal static void Replace(params Entity[] entities) { }
        internal static void Clear() { }
    }
}
#else
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace XAnnotation
{
    /// <summary>
    /// 用<b>瞬态图形</b>（<see cref="TransientManager"/>）给拖拽预览补一份"不透明"的副本。
    /// <para><b>为什么需要它</b>：AutoCAD 的 Jig 拖拽管线会把 WorldDraw 画出来的每个图元
    /// 按 50% 透明度与背景合成（抓屏实测：落图 (255,0,255)、预览 (128,0,128)，重叠处 192）。
    /// 这个透明度加在拖拽合成层，<c>SubEntityTraits.Transparency</c> 在 2014 上读会抛
    /// <c>eInvalidKey</c>、写则被静默忽略；.NET 也没有封装 <c>AcEdDragStyle</c>（可以给拖动对象
    /// 指定 kNone），所以从 traits 侧关不掉。</para>
    /// <para><b>为什么瞬态层可以</b>：瞬态图形不经过拖拽合成，实测同样的 Polyline 在
    /// <c>AddTransient</c> 之后是纯实色（255/255）。于是每帧把同一份几何再交给瞬态层画一遍，
    /// 不透明的那一份会盖住被淡化的那一份。</para>
    /// <para><b>保底</b>：Jig 里原有的绘制一律保留。万一某个平台/版本上瞬态层不可用，
    /// 画面退回到改动前的样子，而不是"预览整个消失"。</para>
    /// </summary>
    internal static class PreviewTransient
    {
        private static readonly IntegerCollection Viewports = new IntegerCollection();
        private static readonly List<Entity> Live = new List<Entity>();
        private static bool _failed;

        /// <summary>成功初始化过才为 true；一旦失败就永久退出，避免每帧都抛一次异常。</summary>
        internal static bool Enabled { get { return !_failed; } }

        /// <summary>
        /// 用这一批实体替换上一帧的瞬态图形。<b>所有权转移</b>：传进来的实体由本类负责
        /// 在下一帧或 <see cref="Clear"/> 时擦除并释放，调用方不要再 Dispose。
        /// </summary>
        internal static void Replace(params Entity[] entities)
        {
            Clear();
            if (_failed || entities == null || entities.Length == 0) return;

            try
            {
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in entities)
                {
                    if (entity == null) continue;
                    manager.AddTransient(entity, TransientDrawingMode.DirectShortTerm, 128, Viewports);
                    Live.Add(entity);
                }
            }
            catch (Exception ex)
            {
                // 只报一次：拖拽期间每帧都会走到这里，重复写日志会把日志刷爆。
                _failed = true;
                PluginLog.Warning("PreviewTransient", "瞬态预览不可用，退回 Jig 绘制：" + ex.Message);
                Clear();
            }
        }

        /// <summary>擦掉当前这一批瞬态图形并释放实体。拖拽结束（或异常退出）时必须调。</summary>
        internal static void Clear()
        {
            if (Live.Count == 0) return;
            try
            {
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in Live)
                {
                    try { manager.EraseTransient(entity, Viewports); }
                    catch (Exception) { }
                    try { entity.Dispose(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            Live.Clear();
        }
    }
}
#endif
