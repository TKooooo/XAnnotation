using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#elif GSCAD
// 浩辰 GstarCAD：API 与中望同形，命名空间为 Gssoft.Gscad.*；COM 应用对象属性名是 AcadApplication（非 ZcadApplication）。
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.DatabaseServices;
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace XAnnotation
{
    /// <summary>一条待导出的批注：业务文字 + 每条云线（多对一有多条）的 WCS 范围。</summary>
    internal sealed class AnnotationWordEntry
    {
        public string Number { get; set; }
        public string Date { get; set; }
        public string Content { get; set; }
        public List<Extents3d> CloudExtents { get; set; } = new List<Extents3d>();
    }

    /// <summary>
    /// 批注导出 Word：逐条把视图缩放到云线范围并抓取绘图窗口位图，
    /// 再按"时间 / 云线截图 / 批注文字"三行格式手工打包生成 docx（不依赖 Office）。
    /// </summary>
    internal static class WordExporter
    {
        public static void Export(Document doc, List<AnnotationWordEntry> entries)
        {
            var ed = doc.Editor;
            if (entries == null || entries.Count == 0)
            {
                ed.WriteMessage("\n没有可导出的批注。");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出批注到 Word",
                Filter = "Word 文档 (*.docx)|*.docx",
                FileName = "批注导出.docx",
                AddExtension = true,
                DefaultExt = ".docx"
            };
            // 保存对话框弹出期间先让本插件的置顶窗口（批注列表）挪到后面：
            // 两个 Topmost 窗口互相压不住，列表会把"另存为"挡在下面（现场反馈的问题）。
            bool picked;
            using (CadDialog.SuspendTopmost()) picked = dialog.ShowDialog() == true;
            if (!picked) return;

            // 截图前记住当前视图，导出完成后恢复。
            ViewTableRecord originalView = null;
            using (var view = ed.GetCurrentView()) originalView = (ViewTableRecord)view.Clone();

            // 截图底色（设置 → 导出 Word 回复单 → 截图底色）：0=白底黑字（默认）、1=白底彩色、2=保持原样。
            // 只做像素转换，不改 CAD 显示设置，所以导出中断也不会把用户的界面留成白底。
            var shotStyle = 0;
            try { shotStyle = SettingsStore.Load().ExportShotStyle; }
            catch (System.Exception ex) { PluginLog.Warning("WordExport.Settings", ex.Message); }

            var imagesPerEntry = new List<List<byte[]>>();
            try
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    ed.WriteMessage($"\r正在截图 {i + 1}/{entries.Count}    ");
                    var images = new List<byte[]>();
                    imagesPerEntry.Add(images);
                    foreach (var extents in entries[i].CloudExtents)
                    {
                        try
                        {
                            ZoomToExtents(doc, extents);
                            var png = CaptureWindowPng(doc, shotStyle);
                            if (png != null && png.Length > 0) images.Add(png);
                        }
                        catch (System.Exception ex) { PluginLog.Error("WordExport.Capture", ex); }
                    }
                }
            }
            finally
            {
                try
                {
                    if (originalView != null)
                    {
                        ed.SetCurrentView(originalView);
                        ed.UpdateScreen();
                    }
                    originalView?.Dispose();
                }
                catch { /* 恢复视图失败不影响导出结果 */ }
            }

            DocxBuilder.Build(dialog.FileName, entries, imagesPerEntry);
            ed.WriteMessage($"\n已导出 {entries.Count} 条批注到: {dialog.FileName}");
        }

        /// <summary>缩放当前视图到指定 WCS 范围（与 ZoomToAnnotation 相同的 DCS 变换）。</summary>
        private static void ZoomToExtents(Document doc, Extents3d ext)
        {
            var ed = doc.Editor;
            using (var view = ed.GetCurrentView())
            {
                var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
                wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                wcsToDcs = wcsToDcs.Inverse();
                var min = ext.MinPoint; var max = ext.MaxPoint;
                var dcsCorners = new[]
                {
                    new Point3d(min.X,min.Y,min.Z),new Point3d(max.X,min.Y,min.Z),new Point3d(max.X,max.Y,min.Z),new Point3d(min.X,max.Y,min.Z),
                    new Point3d(min.X,min.Y,max.Z),new Point3d(max.X,min.Y,max.Z),new Point3d(max.X,max.Y,max.Z),new Point3d(min.X,max.Y,max.Z)
                }.Select(point => point.TransformBy(wcsToDcs)).ToArray();
                var minX = dcsCorners.Min(point => point.X); var maxX = dcsCorners.Max(point => point.X);
                var minY = dcsCorners.Min(point => point.Y); var maxY = dcsCorners.Max(point => point.Y);
                var width = Math.Max(maxX - minX, 1); var height = Math.Max(maxY - minY, 1);
                const double margin = 1.15; // 略微外扩，让云线边界本身也进入截图
                view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
                view.Width = width * margin;
                view.Height = height * margin;
                ed.SetCurrentView(view);
            }
            ed.Regen();
        }

        /// <summary>
        /// 抓取当前文档绘图窗口的客户区位图，返回 PNG 字节（宽度超过 1600px 时等比缩小）。
        /// 主路径使用 CAD 自带的 CapturePreviewImage，仅读取当前视图渲染结果；
        /// 后备路径也只请求 CAD 文档窗口自身重绘。两者都不允许抓取桌面像素，防止输入法或其他程序混入 Word。
        /// </summary>
        private static byte[] CaptureWindowPng(Document doc, int shotStyle)
        {
            var hwnd = doc.Window.Handle;
            if (hwnd == IntPtr.Zero) return null;
            byte[] lastCapture = null;
            const int maxAttempts = 3;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                RefreshViewportForCapture(doc, hwnd, attempt);
                if (!NativeMethods.GetClientRect(hwnd, out var rect)) return null;
                var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
                if (width < 10 || height < 10) return null;

                using (var bitmap = CaptureCadView(doc, hwnd, width, height))
                {
                    if (bitmap == null) return null;
                    // 纯色判断用原始截图做（转换后背景一定是白的，会把"没渲染出来"掩盖掉）。
                    var nearlyUniform = LooksNearlyUniform(bitmap);
                    // 先缩到 1600 再换底色：像素转换只在最终尺寸上做一遍，省一半时间；
                    // 顺序反过来（先转换再缩放）观感几乎一样，都是细线被重采样成浅灰。
                    // shotStyle：0=白底黑字、1=白底彩色、2=保持原样
                    using (var scaled = Downscale(bitmap, 1600))
                    using (var styled = shotStyle == 2 ? new Bitmap(scaled) : ToInkOnWhite(scaled, shotStyle == 1))
                    using (var stream = new MemoryStream())
                    {
                        styled.Save(stream, ImageFormat.Png);
                        lastCapture = stream.ToArray();
                    }
                    if (!nearlyUniform) return lastCapture;
                }
                if (attempt < maxAttempts - 1)
                    PluginLog.Warning("WordExport.Capture", $"第 {attempt + 1} 次截图接近纯色，等待 CAD 完成渲染后重试。");
            }
            return lastCapture;
        }

        /// <summary>墨迹亮度目标：把墨色压到这么亮（0=纯黑）。60 在纸上就是"黑字"，又不会糊成一团。</summary>
        private const int InkLuminance = 60;

        /// <summary>把 CAD 视图截图（黑底彩线）转成<b>白底黑字</b>的打印稿观感。
        /// <para><b>为什么用像素转换、而不是临时把 CAD 背景改成白色</b>：
        /// 改背景等于改用户当前会话的显示设置（导出中途出错就会留在白底），而且只有 7 号色会随背景
        /// 自动变黑，其它颜色（黄/青）在白底上反而更看不清；像素转换不动图形、不动设置，
        /// AutoCAD / 中望 / 浩辰走同一套代码，渐变底、深灰底也不会漏。</para>
        /// <para><b>算法</b>：把"与背景色的差异"当作<b>墨量</b> <c>a</c>（0 = 纯背景），背景铺白、
        /// 墨迹按同比例压深：<c>输出 = 白 × (1-a) + 墨色 × a</c>。抗锯齿边缘因为 a 小，自然变成浅灰——
        /// 和真正打印出来的观感一致。</para>
        /// <para><paramref name="keepColor"/>=true（白底彩色）：墨色保留原色相、只压亮度，
        /// 已回复/已完成那种整条着色仍分得出；=false（白底黑字，默认）：墨色统一成黑，
        /// 就是黑白打印稿的效果。</para>
        /// <para>背景本身就是白/浅色（布局、纸质配色的图）时<b>原样返回</b>，不做二次加工，
        /// 免得把本来就是白底的截图又"翻"一遍。</para></summary>
        private static Bitmap ToInkOnWhite(Bitmap source, bool keepColor)
        {
            if (source == null) return null;
            var width = source.Width;
            var height = source.Height;
            if (width <= 0 || height <= 0) return new Bitmap(source);

            var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(target))
            {
                // 统一成 24bpp：CapturePreviewImage 回来可能是 32bpp，直接 LockBits 换格式会失败。
                graphics.DrawImage(source, 0, 0, width, height);
            }

            var rect = new Rectangle(0, 0, width, height);
            BitmapData data = null;
            try
            {
                data = target.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                var stride = data.Stride;
                var bytes = Math.Abs(stride) * height;
                var buffer = new byte[bytes];
                Marshal.Copy(data.Scan0, buffer, 0, bytes);

                // 背景色 = 四条边上出现最多的颜色。CAD 视图底色是整片纯色，按边统计比取角落稳
                // （左上角可能压着坐标轴图标）。
                var background = DetectBackground(buffer, stride, width, height);
                var bgLum = (299 * background.R + 587 * background.G + 114 * background.B) / 1000;
                if (bgLum > 128) return target;   // 已经是浅底，不用转换

                for (var y = 0; y < height; y++)
                {
                    var row = stride > 0 ? y * stride : (height - 1 - y) * (-stride);
                    for (var x = 0; x < width; x++)
                    {
                        var index = row + x * 3;
                        int b = buffer[index], g = buffer[index + 1], r = buffer[index + 2];

                        // 墨量：与背景的最大通道差（黑底时就是这条线有多亮）。
                        var ink = Math.Max(Math.Abs(r - background.R),
                                  Math.Max(Math.Abs(g - background.G), Math.Abs(b - background.B)));
                        if (ink <= 8)
                        {
                            buffer[index] = 255; buffer[index + 1] = 255; buffer[index + 2] = 255;
                            continue;
                        }

                        byte outR, outG, outB;
                        if (!keepColor)
                        {
                            // 白底黑字：墨色统一为黑，墨量直接变灰度 → 白纸上就是从黑到浅灰的黑白打印效果。
                            var level = (byte)(255 - ink);
                            outR = outG = outB = level;
                        }
                        else
                        {
                            // 白底彩色：墨色保留色相，只把亮度压到目标值（已经比目标更暗的不做反相）。
                            var lum = (299 * r + 587 * g + 114 * b) / 1000;
                            var scale = lum > InkLuminance ? (double)InkLuminance / lum : 1.0;
                            var inkR = (int)(r * scale);
                            var inkG = (int)(g * scale);
                            var inkB = (int)(b * scale);
                            // out = 255 × (1 - ink/255) + inkColor × ink/255
                            outR = (byte)(255 - ink * (255 - inkR) / 255);
                            outG = (byte)(255 - ink * (255 - inkG) / 255);
                            outB = (byte)(255 - ink * (255 - inkB) / 255);
                        }

                        buffer[index] = outB;
                        buffer[index + 1] = outG;
                        buffer[index + 2] = outR;
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, bytes);
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("WordExport.InkOnWhite", "白底转换失败，仍按原截图导出：" + ex.Message);
                if (data != null) { try { target.UnlockBits(data); } catch { } data = null; }
                try { target.Dispose(); } catch { }
                return new Bitmap(source);
            }
            finally
            {
                if (data != null)
                {
                    try { target.UnlockBits(data); } catch { }
                }
            }
            return target;
        }

        /// <summary>统计四条边上出现最多的颜色（CAD 视图底色是一片纯色，这样取最稳）。</summary>
        private static Color DetectBackground(byte[] buffer, int stride, int width, int height)
        {
            var counts = new Dictionary<int, int>();
            var step = Math.Max(1, width / 200);
            for (var x = 0; x < width; x += step)
            {
                AddColor(counts, buffer, stride, height, x, 0);
                AddColor(counts, buffer, stride, height, x, height - 1);
            }
            for (var y = 0; y < height; y += step)
            {
                AddColor(counts, buffer, stride, height, 0, y);
                AddColor(counts, buffer, stride, height, width - 1, y);
            }

            var best = 0; var bestCount = -1;
            foreach (var pair in counts)
            {
                if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
            }
            // 一个像素都没统计到（宽高异常）：按黑底处理。
            return bestCount <= 0 ? Color.FromArgb(0, 0, 0)
                 : Color.FromArgb((best >> 16) & 0xFF, (best >> 8) & 0xFF, best & 0xFF);
        }

        private static void AddColor(Dictionary<int, int> counts, byte[] buffer, int stride, int height, int x, int y)
        {
            if (x < 0 || y < 0) return;
            var offset = (stride > 0 ? y * stride : (height - 1 - y) * (-stride)) + x * 3;
            if (offset < 0 || offset + 2 >= buffer.Length) return;
            var key = (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset];
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        private static Bitmap CaptureCadView(Document doc, IntPtr hwnd, int width, int height)
        {
            try
            {
                // CAD 原生预览图由宿主的图形管线产生，不受窗口遮挡、输入法或桌面上其他程序影响。
                var native = doc.CapturePreviewImage((uint)width, (uint)height);
                if (native != null) return native;
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("WordExport.CapturePreviewImage", "CAD 原生视图截图失败，改用文档窗口重绘：" + ex.Message);
            }
            return CaptureCadWindow(hwnd, width, height);
        }

        private static Bitmap CaptureCadWindow(IntPtr hwnd, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var captured = false;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    // PrintWindow 让 CAD 自己绘制客户区，不会把覆盖在上面的其他窗口复制进来。
                    captured = NativeMethods.PrintWindow(
                        hwnd, hdc,
                        NativeMethods.PwClientOnly | NativeMethods.PwRenderFullContent);
                }
                finally { graphics.ReleaseHdc(hdc); }
            }
            if (!captured) { bitmap.Dispose(); return null; }
            return bitmap;
        }

        /// <summary>让缩放后的 CAD 视图完成再生、窗口重绘和 DWM 合成，再读取屏幕像素。</summary>
        private static void RefreshViewportForCapture(Document doc, IntPtr hwnd, int attempt)
        {
            var ed = doc.Editor;
            ed.UpdateScreen();
            NativeMethods.RedrawWindow(
                hwnd, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RdwInvalidate | NativeMethods.RdwAllChildren | NativeMethods.RdwUpdateNow);
            NativeMethods.UpdateWindow(hwnd);
            NativeMethods.TryFlushDwm();

            // Regen 在硬件加速宿主中可能先返回，实际绘图由渲染线程继续完成。
            // 首次只给少量等待；若还是纯色，后续重试逐次延长，避免每张图都无条件慢速。
            Thread.Sleep(120 + attempt * 160);
            ed.UpdateScreen();
            NativeMethods.UpdateWindow(hwnd);
            NativeMethods.TryFlushDwm();
        }

        private static bool LooksNearlyUniform(Bitmap bitmap)
        {
            var stepX = Math.Max(1, bitmap.Width / 160);
            var stepY = Math.Max(1, bitmap.Height / 100);
            var reference = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            var samples = 0;
            var different = 0;
            for (var y = 0; y < bitmap.Height; y += stepY)
            {
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    var color = bitmap.GetPixel(x, y);
                    samples++;
                    if (Math.Abs(color.R - reference.R) + Math.Abs(color.G - reference.G) + Math.Abs(color.B - reference.B) > 24)
                        different++;
                }
            }
            return different < Math.Max(12, samples / 500);
        }

        private static Bitmap Downscale(Bitmap source, int maxWidth)
        {
            // 返回独立位图，避免调用方释放缩放结果时误将原始截图一起释放。
            if (source.Width <= maxWidth) return new Bitmap(source);
            var scale = (double)maxWidth / source.Width;
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var target = new Bitmap(maxWidth, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, maxWidth, height);
            }
            return target;
        }

        private static class NativeMethods
        {
            public const uint RdwInvalidate = 0x0001;
            public const uint RdwAllChildren = 0x0080;
            public const uint RdwUpdateNow = 0x0100;
            public const uint PwClientOnly = 0x00000001;
            public const uint PwRenderFullContent = 0x00000002;

            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
            [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
            [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
            [DllImport("dwmapi.dll")] private static extern int DwmFlush();

            public static void TryFlushDwm()
            {
                try { DwmFlush(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }
        }

        /// <summary>用 System.IO.Packaging 手工打包最小 docx：段落文字 + 内嵌 PNG 图片。</summary>
        private static class DocxBuilder
        {
            private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
            private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
            private const string PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            private const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
            private const string DocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
            private const string DocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

            private const double EmuPerCm = 360000.0;
            // A4 纸 + 左右各 2cm 边距，可用宽度约 17cm，留一点余量按 16.5cm 排图。
            private const double UsableWidthEmu = 16.5 * EmuPerCm;
            private const double MaxImageHeightEmu = 12.0 * EmuPerCm;
            private const double EmuPerPixel = 914400.0 / 96.0; // 按 96dpi 换算像素自然尺寸

            public static void Build(string path, List<AnnotationWordEntry> entries, List<List<byte[]>> imagesPerEntry)
            {
                using (var file = new FileStream(path, FileMode.Create))
                using (var package = Package.Open(file, FileMode.Create))
                {
                    var documentUri = new Uri("/word/document.xml", UriKind.Relative);
                    var documentPart = package.CreatePart(documentUri, DocumentContentType);
                    package.CreateRelationship(documentUri, TargetMode.Internal, DocumentRelationship);

                    // 先写入全部图片部件并记录关系 ID，再生成 document.xml。
                    var relationshipIds = new List<List<string>>();
                    var imageCounter = 0;
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var ids = new List<string>();
                        relationshipIds.Add(ids);
                        foreach (var png in imagesPerEntry[i])
                        {
                            imageCounter++;
                            var imageUri = new Uri("/word/media/image" + imageCounter + ".png", UriKind.Relative);
                            var imagePart = package.CreatePart(imageUri, "image/png");
                            using (var stream = imagePart.GetStream()) stream.Write(png, 0, png.Length);
                            var relationship = documentPart.CreateRelationship(imageUri, TargetMode.Internal, ImageRelationship, "rId" + imageCounter);
                            ids.Add(relationship.Id);
                        }
                    }

                    package.PackageProperties.Title = "X-批注导出";

                    using (var stream = documentPart.GetStream())
                    {
                        var settings = new XmlWriterSettings { Encoding = System.Text.Encoding.UTF8, Indent = false };
                        using (var writer = XmlWriter.Create(stream, settings))
                        {
                            writer.WriteStartDocument();
                            writer.WriteStartElement("w", "document", W);
                            writer.WriteStartElement("w", "body", W);
                            var docPrId = 0;
                            for (var i = 0; i < entries.Count; i++)
                            {
                                // 第一行：时间；第二行：云线范围截图（多对一放多张）；第三行：批注文字。
                                WriteTextParagraph(writer, entries[i].Date);
                                if (relationshipIds[i].Count > 0)
                                    WriteImagesParagraph(writer, imagesPerEntry[i], relationshipIds[i], ref docPrId);
                                WriteTextParagraph(writer, entries[i].Content);
                                WriteTextParagraph(writer, "");
                            }
                            WriteSectionProperties(writer);
                            writer.WriteEndElement(); // body
                            writer.WriteEndElement(); // document
                            writer.WriteEndDocument();
                        }
                    }
                }
            }

            private static void WriteTextParagraph(XmlWriter writer, string text)
            {
                writer.WriteStartElement("w", "p", W);
                writer.WriteStartElement("w", "r", W);
                var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) writer.WriteElementString("w", "br", W, null);
                    writer.WriteStartElement("w", "t", W);
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(lines[i]);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // r
                writer.WriteEndElement(); // p
            }

            private static void WriteImagesParagraph(XmlWriter writer, List<byte[]> images, List<string> relationshipIds, ref int docPrId)
            {
                // 每行最多两张图片，分批写入独立段落。
                const int perRow = 2;
                for (var rowStart = 0; rowStart < images.Count; rowStart += perRow)
                {
                    var rowEnd = Math.Min(rowStart + perRow, images.Count);
                    var rowCount = rowEnd - rowStart;
                    var slotWidth = UsableWidthEmu / rowCount;
                    writer.WriteStartElement("w", "p", W);
                    for (var i = rowStart; i < rowEnd; i++)
                    {
                        Size pixelSize;
                        using (var stream = new MemoryStream(images[i]))
                        using (var bitmap = new Bitmap(stream)) pixelSize = bitmap.Size;
                        var cx = Math.Min(slotWidth, pixelSize.Width * EmuPerPixel);
                        var cy = cx * pixelSize.Height / Math.Max(1, pixelSize.Width);
                        if (cy > MaxImageHeightEmu)
                        {
                            cx *= MaxImageHeightEmu / cy;
                            cy = MaxImageHeightEmu;
                        }
                        docPrId++;
                        WriteImageRun(writer, relationshipIds[i], docPrId, (long)cx, (long)cy, "云线范围" + docPrId);
                    }
                    writer.WriteEndElement(); // p
                }
            }

            private static void WriteImageRun(XmlWriter writer, string relationshipId, int docPrId, long cx, long cy, string name)
            {
                writer.WriteStartElement("w", "r", W);
                writer.WriteStartElement("w", "drawing", W);
                writer.WriteStartElement("wp", "inline", WP);
                writer.WriteAttributeString("distT", "0");
                writer.WriteAttributeString("distB", "0");
                writer.WriteAttributeString("distL", "0");
                writer.WriteAttributeString("distR", "0");

                writer.WriteStartElement("wp", "extent", WP);
                writer.WriteAttributeString("cx", cx.ToString());
                writer.WriteAttributeString("cy", cy.ToString());
                writer.WriteEndElement();

                writer.WriteStartElement("wp", "docPr", WP);
                writer.WriteAttributeString("id", docPrId.ToString());
                writer.WriteAttributeString("name", name);
                writer.WriteEndElement();

                writer.WriteStartElement("a", "graphic", A);
                writer.WriteStartElement("a", "graphicData", A);
                writer.WriteAttributeString("uri", PictureUri);

                writer.WriteStartElement("pic", "pic", PIC);
                writer.WriteStartElement("pic", "nvPicPr", PIC);
                writer.WriteStartElement("pic", "cNvPr", PIC);
                writer.WriteAttributeString("id", "0");
                writer.WriteAttributeString("name", name);
                writer.WriteEndElement();
                writer.WriteStartElement("pic", "cNvPicPr", PIC);
                writer.WriteEndElement();
                writer.WriteEndElement(); // nvPicPr

                writer.WriteStartElement("pic", "blipFill", PIC);
                writer.WriteStartElement("a", "blip", A);
                writer.WriteAttributeString("r", "embed", R, relationshipId);
                writer.WriteEndElement();
                writer.WriteStartElement("a", "stretch", A);
                writer.WriteStartElement("a", "fillRect", A);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement(); // blipFill

                writer.WriteStartElement("pic", "spPr", PIC);
                writer.WriteStartElement("a", "xfrm", A);
                writer.WriteStartElement("a", "off", A);
                writer.WriteAttributeString("x", "0");
                writer.WriteAttributeString("y", "0");
                writer.WriteEndElement();
                writer.WriteStartElement("a", "ext", A);
                writer.WriteAttributeString("cx", cx.ToString());
                writer.WriteAttributeString("cy", cy.ToString());
                writer.WriteEndElement();
                writer.WriteEndElement(); // xfrm
                writer.WriteStartElement("a", "prstGeom", A);
                writer.WriteAttributeString("prst", "rect");
                writer.WriteStartElement("a", "avLst", A);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement(); // spPr

                writer.WriteEndElement(); // pic
                writer.WriteEndElement(); // graphicData
                writer.WriteEndElement(); // graphic
                writer.WriteEndElement(); // inline
                writer.WriteEndElement(); // drawing
                writer.WriteEndElement(); // r
            }

            private static void WriteSectionProperties(XmlWriter writer)
            {
                writer.WriteStartElement("w", "sectPr", W);
                writer.WriteStartElement("w", "pgSz", W);
                writer.WriteAttributeString("w", "w", W, "11906"); // A4
                writer.WriteAttributeString("w", "h", W, "16838");
                writer.WriteEndElement();
                writer.WriteStartElement("w", "pgMar", W);
                writer.WriteAttributeString("w", "top", W, "1134"); // 2cm
                writer.WriteAttributeString("w", "right", W, "1134");
                writer.WriteAttributeString("w", "bottom", W, "1134");
                writer.WriteAttributeString("w", "left", W, "1134");
                writer.WriteAttributeString("w", "header", W, "720");
                writer.WriteAttributeString("w", "footer", W, "720");
                writer.WriteAttributeString("w", "gutter", W, "0");
                writer.WriteEndElement();
                writer.WriteEndElement(); // sectPr
            }
        }
    }
}
