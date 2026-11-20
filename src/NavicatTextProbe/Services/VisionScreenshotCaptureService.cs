using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed class VisionScreenshotCaptureService
{
    private const int DetailWidth = 1600;
    private const int DetailHeight = 1000;
    private const int MaximumFullDimension = 3200;
    private const int MaximumImageBytes = 4_800_000;
    private readonly DocumentContextProbeService _documentContextProbeService;

    public VisionScreenshotCaptureService(DocumentContextProbeService documentContextProbeService)
    {
        _documentContextProbeService = documentContextProbeService;
    }

    public async Task<VisionCaptureSnapshot> CaptureAsync(
        CancellationToken cancellationToken,
        string captureScope = "ForegroundWindow")
    {
        var foreground = DocumentContextProbeService.ReadForegroundWindow();
        if (foreground.WindowHandle == nint.Zero)
        {
            return VisionCaptureSnapshot.Failure(
                VisionCaptureStatus.NoForegroundWindow,
                "没有可截图的前台窗口。");
        }
        if (foreground.ProcessId == Environment.ProcessId)
        {
            return Failure(
                VisionCaptureStatus.AssistantForeground,
                "请先切回需要识别的窗口，再按截图快捷键。",
                foreground);
        }
        if (NativeMethods.IsIconic(foreground.RootWindowHandle))
        {
            return Failure(
                VisionCaptureStatus.CaptureFailed,
                "前台窗口已最小化，无法取得可见题目截图。",
                foreground);
        }

        CaretLocation? caret = null;
        if (foreground.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
        {
            var document = await _documentContextProbeService.CaptureForegroundAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Status == DocumentContextStatus.Captured &&
                document.ProcessId == foreground.ProcessId &&
                document.CaretScreenBounds is PixelRect wordCaret)
            {
                caret = new CaretLocation(
                    wordCaret,
                    document.CaretScreenSource,
                    0.98);
            }
        }

        caret ??= await Task.Run(() => ForegroundCaretLocator.LocateAsync(foreground, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => CaptureWindow(foreground, caret, captureScope),
            cancellationToken);
    }

    private static VisionCaptureSnapshot CaptureWindow(
        ForegroundWindowContext foreground,
        CaretLocation? caret,
        string captureScope)
    {
        try
        {
            var foregroundBounds = ReadWindowBounds(foreground.RootWindowHandle);
            if (caret is not null && !foregroundBounds.Contains(
                    caret.Bounds.Left + Math.Max(1, caret.Bounds.Width / 2),
                    caret.Bounds.Top + Math.Max(1, caret.Bounds.Height / 2)))
            {
                caret = null;
            }
            var windowBounds = ReadCaptureBounds(foreground.RootWindowHandle, foregroundBounds, captureScope, caret);
            if (windowBounds.IsEmpty)
            {
                return Failure(
                    VisionCaptureStatus.CaptureFailed,
                    "无法读取前台窗口的屏幕范围。",
                    foreground);
            }

            var caretCenterX = caret is null
                ? windowBounds.Left + windowBounds.Width / 2
                : caret.Bounds.Left + Math.Max(1, caret.Bounds.Width / 2);
            var caretCenterY = caret is null
                ? windowBounds.Top + windowBounds.Height / 2
                : caret.Bounds.Top + Math.Max(1, caret.Bounds.Height / 2);
            using var fullBitmap = new Bitmap(
                windowBounds.Width,
                windowBounds.Height,
                PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(fullBitmap))
            {
                if (DocumentContextProbeService.ReadForegroundWindow().RootWindowHandle != foreground.RootWindowHandle)
                {
                    return Failure(VisionCaptureStatus.CaptureFailed, "截图前切换了窗口，请重试。", foreground);
                }
                graphics.CopyFromScreen(
                    windowBounds.Left,
                    windowBounds.Top,
                    0,
                    0,
                    fullBitmap.Size,
                    CopyPixelOperation.SourceCopy);
            }
            if (DocumentContextProbeService.ReadForegroundWindow().RootWindowHandle != foreground.RootWindowHandle)
            {
                return Failure(VisionCaptureStatus.CaptureFailed, "截图时切换了窗口，请重试。", foreground);
            }

            var relativeCaretX = caretCenterX - windowBounds.Left;
            var relativeCaretY = caretCenterY - windowBounds.Top;
            var detailRectangle = BuildDetailRectangle(
                fullBitmap.Width,
                fullBitmap.Height,
                relativeCaretX,
                relativeCaretY);
            using var detailBitmap = fullBitmap.Clone(detailRectangle, PixelFormat.Format32bppArgb);
            var detailMarkerX = relativeCaretX - detailRectangle.Left;
            var detailMarkerY = relativeCaretY - detailRectangle.Top;
            if (caret is not null)
            {
                DrawCaretMarker(detailBitmap, detailMarkerX, detailMarkerY);
            }

            using var scaledFullBitmap = ScaleDown(fullBitmap, MaximumFullDimension);
            var fullBytes = EncodeJpegToLimit(scaledFullBitmap, MaximumImageBytes);
            var detailBytes = EncodePng(detailBitmap);
            var detailMediaType = "image/png";
            if (detailBytes.Length > MaximumImageBytes)
            {
                detailBytes = EncodeJpegToLimit(detailBitmap, MaximumImageBytes);
                detailMediaType = "image/jpeg";
            }

            return new VisionCaptureSnapshot
            {
                Status = VisionCaptureStatus.Captured,
                Message = caret is null
                    ? string.Equals(captureScope, "CurrentMonitor", StringComparison.OrdinalIgnoreCase)
                        ? "已截取当前显示器；未取得可靠文本光标，将只回答可见内容。"
                        : "已截取前台窗口；未取得可靠文本光标，将只回答可见内容。"
                    : string.Equals(captureScope, "CurrentMonitor", StringComparison.OrdinalIgnoreCase)
                        ? "已截取当前显示器并标记文本光标。"
                        : "已截取前台窗口并标记文本光标。",
                ProcessId = foreground.ProcessId,
                ProcessName = foreground.ProcessName,
                WindowHandle = foreground.RootWindowHandle.ToInt64(),
                WindowBounds = windowBounds,
                CaretScreenBounds = caret?.Bounds ?? default,
                CaretSource = caret?.Source ?? "未取得可靠文本光标",
                CaretConfidence = caret?.Confidence ?? 0,
                DetailBounds = new PixelRect(
                    windowBounds.Left + detailRectangle.Left,
                    windowBounds.Top + detailRectangle.Top,
                    detailRectangle.Width,
                    detailRectangle.Height),
                DetailMarkerX = detailMarkerX,
                DetailMarkerY = detailMarkerY,
                FullImageBytes = fullBytes,
                DetailImageBytes = detailBytes,
                DetailImageMediaType = detailMediaType,
                CaptureHash = Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant()
            };
        }
        catch (Exception ex)
        {
            return Failure(
                VisionCaptureStatus.CaptureFailed,
                $"截图失败：{ex.Message}",
                foreground);
        }
    }

    private static PixelRect ReadWindowBounds(nint windowHandle)
    {
        NativeMethods.NativeRect nativeBounds;
        if (NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                NativeMethods.DwmwaExtendedFrameBounds,
                out nativeBounds,
                Marshal.SizeOf<NativeMethods.NativeRect>()) != 0 &&
            !NativeMethods.GetWindowRect(windowHandle, out nativeBounds))
        {
            return default;
        }

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var left = Math.Max(nativeBounds.Left, virtualScreen.Left);
        var top = Math.Max(nativeBounds.Top, virtualScreen.Top);
        var right = Math.Min(nativeBounds.Right, virtualScreen.Right);
        var bottom = Math.Min(nativeBounds.Bottom, virtualScreen.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static PixelRect ReadCaptureBounds(
        nint windowHandle,
        PixelRect foregroundBounds,
        string captureScope,
        CaretLocation? caret)
    {
        if (!string.Equals(captureScope, "CurrentMonitor", StringComparison.OrdinalIgnoreCase))
        {
            return foregroundBounds;
        }

        var screen = caret is null
            ? System.Windows.Forms.Screen.FromHandle(windowHandle)
            : System.Windows.Forms.Screen.FromPoint(new Point(
                caret.Bounds.Left + Math.Max(1, caret.Bounds.Width / 2),
                caret.Bounds.Top + Math.Max(1, caret.Bounds.Height / 2)));
        var bounds = screen.Bounds;
        return new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    internal static Rectangle BuildDetailRectangle(
        int imageWidth,
        int imageHeight,
        int markerX,
        int markerY)
    {
        var width = Math.Min(DetailWidth, imageWidth);
        var height = Math.Min(DetailHeight, imageHeight);
        var left = Math.Clamp(markerX - width / 2, 0, Math.Max(0, imageWidth - width));
        var top = Math.Clamp(markerY - height / 2, 0, Math.Max(0, imageHeight - height));
        return new Rectangle(left, top, width, height);
    }

    private static void DrawCaretMarker(Bitmap bitmap, int x, int y)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var halo = new Pen(Color.FromArgb(235, 255, 255, 255), 8);
        using var marker = new Pen(Color.FromArgb(255, 230, 30, 55), 4);
        const int radius = 17;
        var circle = new Rectangle(x - radius, y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(halo, circle);
        graphics.DrawEllipse(marker, circle);

        var arrowStartX = Math.Clamp(x + 58, 0, Math.Max(0, bitmap.Width - 1));
        var arrowStartY = Math.Clamp(y - 48, 0, Math.Max(0, bitmap.Height - 1));
        var arrowEndX = Math.Clamp(x + 16, 0, Math.Max(0, bitmap.Width - 1));
        var arrowEndY = Math.Clamp(y - 10, 0, Math.Max(0, bitmap.Height - 1));
        marker.CustomEndCap = new AdjustableArrowCap(5, 6, true);
        graphics.DrawLine(marker, arrowStartX, arrowStartY, arrowEndX, arrowEndY);
    }

    private static Bitmap ScaleDown(Bitmap source, int maximumDimension)
    {
        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static byte[] EncodePng(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] EncodeJpegToLimit(Image image, int maximumBytes)
    {
        var codec = ImageCodecInfo.GetImageEncoders()
            .First(value => value.FormatID == ImageFormat.Jpeg.Guid);
        using var initial = new Bitmap(image);
        Bitmap current = initial;
        byte[] bytes = [];
        for (var scaleAttempt = 0; scaleAttempt < 6; scaleAttempt++)
        {
            foreach (var quality in new long[] { 88, 76, 64, 52, 40 })
            {
                using var stream = new MemoryStream();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                current.Save(stream, codec, parameters);
                bytes = stream.ToArray();
                if (bytes.Length <= maximumBytes)
                {
                    if (!ReferenceEquals(current, initial)) current.Dispose();
                    return bytes;
                }
            }

            var nextWidth = Math.Max(1, (int)Math.Round(current.Width * 0.8));
            var nextHeight = Math.Max(1, (int)Math.Round(current.Height * 0.8));
            var next = new Bitmap(nextWidth, nextHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(next))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(current, 0, 0, nextWidth, nextHeight);
            }
            if (!ReferenceEquals(current, initial)) current.Dispose();
            current = next;
        }

        if (!ReferenceEquals(current, initial)) current.Dispose();
        return bytes;
    }

    private static VisionCaptureSnapshot Failure(
        VisionCaptureStatus status,
        string message,
        ForegroundWindowContext foreground) => VisionCaptureSnapshot.Failure(
            status,
            message,
            foreground.ProcessId,
            foreground.ProcessName,
            foreground.RootWindowHandle.ToInt64());
}
