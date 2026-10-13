namespace XAnnotation
{
    /// <summary>绘制过程中"外部请求中止当前这一轮交互"的协作标志。
    /// <para><b>为什么需要它</b>：在功能选择框里改绘制选项或换外形时，正在跑的那一轮（<c>X_PZ_RUN</c>）
    /// 必须干净退出，才能按新选项重开。早先的做法是 <c>SendStringToExecute("\x1b")</c>：提示挂起时这个
    /// Esc 是"排进命令行"的，不保证能被 CAD 及时消费，于是面板看起来"勾了选项没反应，还得再点一次外形"。
    /// 现在改成本插件自己的 Jig 在 <c>Sampler</c> 里检查本标志（鼠标一移动/一点击就会回调），
    /// 命中即 <c>SamplerStatus.Cancel</c> 让命令立刻退出，完全不依赖命令行注入。</para>
    /// <para><b>生命周期</b>：只在"请求中止"到"被中止的那一轮结束"之间为真。
    /// 每轮交互开始时 <see cref="Clear"/> 一次、结束时再 <see cref="Clear"/> 一次
    /// （见 AnnotationPanel.StartAnnotation），所以不会残留下去误杀后来的交互。</para></summary>
    internal static class InteractionAbort
    {
        private static volatile bool _requested;

        /// <summary>是否已请求中止当前交互。</summary>
        public static bool Requested
        {
            get { return _requested; }
        }

        /// <summary>请求中止当前交互（面板改绘制选项 / 换外形 / 关闭时调用）。</summary>
        public static void Request()
        {
            _requested = true;
        }

        /// <summary>清除中止请求。</summary>
        public static void Clear()
        {
            _requested = false;
        }
    }
}
