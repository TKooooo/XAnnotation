#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;

namespace XAnnotation
{
    /// <summary>中望暂未启用瞬态预览（先只在 AutoCAD 上解决"拖拽淡显"）。</summary>
    internal static class PreviewTransient
    {
        internal static bool Enabled { get { return false; } }
        internal static void Replace(params Entity[] entities) { }
        internal static void SetHistory(params Entity[] entities) { }
        internal static void Clear() { }
        internal static void ClearHistory() { }
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
        internal static void SetHistory(params Entity[] entities) { }
        internal static void Clear() { }
        internal static void ClearHistory() { }
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
    /// <para><b>两层</b>：<see cref="Replace"/> 管"跟着光标走的那一份"（每帧整体换掉）；
    /// <see cref="SetHistory"/> 管"本轮已经确认过的云线"（只在一轮开始时换一次，不随帧变化）。
    /// 两者分开存放，否则每帧 <see cref="Replace"/> 会把历史云线一起擦掉。</para>
    /// <para><b>保底</b>：Jig 里原有的绘制一律保留（瞬态不可用时才走）。万一某个平台/版本上瞬态层
    /// 不可用，画面退回到改动前的样子，而不是"预览整个消失"。</para>
    /// </summary>
    internal static class PreviewTransient
    {
        private static readonly IntegerCollection Viewports = new IntegerCollection();
        /// <summary>当前帧的预览实体（随光标变）。</summary>
        private static readonly List<Entity> Live = new List<Entity>();
        /// <summary>本轮已确认的历史云线（多对一/连续云线时给用户看进度）。</summary>
        private static readonly List<Entity> HistoryEntities = new List<Entity>();
        private static bool _failed;

        /// <summary>成功初始化过才为 true；一旦失败就永久退出，避免每帧都抛一次异常。</summary>
        internal static bool Enabled { get { return !_failed; } }

        /// <summary>
        /// 用这一批实体替换上一帧的预览图形。<b>所有权转移</b>：传进来的实体由本类负责
        /// 在下一帧或 <see cref="Clear"/> 时擦除并释放，调用方不要再 Dispose。
        /// </summary>
        internal static void Replace(params Entity[] entities)
        {
            Clear();
            if (_failed || entities == null || entities.Length == 0) return;
            Add(entities, Live, TransientDrawingMode.DirectShortTerm);
        }

        /// <summary>
        /// 换上本轮"已确认"的历史云线（不透明）。只在这里调用时才换，<see cref="Replace"/>
        /// 每帧调用不会动它们。<b>所有权转移</b>：同 <see cref="Replace"/>。
        /// </summary>
        internal static void SetHistory(params Entity[] entities)
        {
            ClearHistory();
            if (_failed || entities == null || entities.Length == 0) return;
            // 历史云线用 Main 模式：它跨视图刷新/重生成保留，不会像 DirectShortTerm 那样
            // 在缩放、刷新之后自己消失（拖拽预览每帧重加，所以能用 DirectShortTerm）。
            Add(entities, HistoryEntities, TransientDrawingMode.Main);
        }

        private static void Add(Entity[] entities, List<Entity> target, TransientDrawingMode mode)
        {
            TransientManager manager;
            try { manager = TransientManager.CurrentTransientManager; }
            catch (Exception ex)
            {
                Fail("取瞬态管理器失败，退回 Jig 绘制：" + ex.Message, entities);
                return;
            }

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null) continue;
                try
                {
                    manager.AddTransient(entity, mode, 128, Viewports);
                    target.Add(entity);
                }
                catch (Exception ex)
                {
                    // 只报一次：拖拽期间每帧都会走到这里，重复写日志会把日志刷爆。
                    var rest = new List<Entity>();
                    for (var j = i; j < entities.Length; j++)
                        if (entities[j] != null) rest.Add(entities[j]);
                    Fail("瞬态预览不可用，退回 Jig 绘制：" + ex.Message, rest.ToArray());
                    return;
                }
            }
        }

        /// <summary>瞬态层不可用：永久退出，并把还没挂上去的实体释放掉（挂上的由两个 Clear 统一擦除）。</summary>
        private static void Fail(string message, Entity[] notAdded)
        {
            if (!_failed)
            {
                _failed = true;
                PluginLog.Warning("PreviewTransient", message);
            }

            Clear();
            ClearHistory();
            foreach (var entity in notAdded)
            {
                if (entity == null) continue;
                try { entity.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>擦掉当前这一帧的预览图形并释放实体。拖拽结束（或异常退出）时必须调。
        /// <para>只动预览、不动 <see cref="SetHistory"/> 放进去的历史云线。</para></summary>
        internal static void Clear()
        {
            Erase(Live);
        }

        /// <summary>擦掉历史云线（一轮框选结束、取消、或改选项重开时调）。</summary>
        internal static void ClearHistory()
        {
            Erase(HistoryEntities);
        }

        private static void Erase(List<Entity> list)
        {
            if (list.Count == 0) return;
            try
            {
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in list)
                {
                    try { manager.EraseTransient(entity, Viewports); }
                    catch (Exception) { }
                    try { entity.Dispose(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            list.Clear();
        }
    }
}
#endif
