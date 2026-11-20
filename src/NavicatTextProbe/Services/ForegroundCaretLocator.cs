using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Automation.Text;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed record CaretLocation(PixelRect Bounds, string Source, double Confidence);

internal static class ForegroundCaretLocator
{
    internal const string HelperArgument = "--probe-ocr-caret";

    public static async Task<CaretLocation?> LocateAsync(ForegroundWindowContext foreground, CancellationToken cancellationToken)
    {
        if (foreground.WindowHandle == nint.Zero || foreground.ProcessId <= 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var native = TryLocateWithGuiThreadInfo(foreground);
        if (native is not null) return native;

        // UIA providers can fault in native code. Keep those calls outside the desktop process.
        var assembly = typeof(App).Assembly.Location;
        var useDotnet = string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(useDotnet ? Environment.ProcessPath! : Path.ChangeExtension(assembly, ".exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        if (useDotnet) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(foreground.WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(foreground.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var child = Process.Start(start);
            if (child is null) return null;
            try
            {
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (child.ExitCode != 0) return null;
                var json = await child.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize<CaretLocation>(json);
            }
            finally
            {
                if (!child.HasExited) child.Kill();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException)
        {
            return null;
        }
    }

    internal static int RunHelper(string[] args)
    {
        if (args.Length != 3 || !long.TryParse(args[1], out var handle) || !int.TryParse(args[2], out var pid)) return 1;
        NativeMethods.SetErrorMode(NativeMethods.SemNoGpFaultErrorBox);
        var foreground = DocumentContextProbeService.ReadForegroundWindow();
        if (foreground.WindowHandle.ToInt64() != handle || foreground.ProcessId != pid) return 1;
        var location = TryLocateWithTextPattern(foreground);
        if (location is null) return 1;
        Console.Write(JsonSerializer.Serialize(location));
        return 0;
    }

    private static CaretLocation? TryLocateWithTextPattern(ForegroundWindowContext foreground)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            for (var depth = 0; element is not null && depth < 12; depth++)
            {
                if (element.Current.ProcessId != foreground.ProcessId)
                {
                    return null;
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern) &&
                    rawPattern is TextPattern pattern)
                {
                    var ranges = pattern.GetSelection();
                    if (ranges.Length == 1 && ranges[0].CompareEndpoints(
                            TextPatternRangeEndpoint.Start, ranges[0], TextPatternRangeEndpoint.End) == 0)
                    {
                        var location = LocateDegenerateRange(ranges[0]);
                        if (location is not null)
                        {
                            return location;
                        }
                    }
                }

                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch
        {
            // UI Automation providers may disappear or reject cross-process calls.
        }

        return null;
    }

    private static CaretLocation? LocateDegenerateRange(TextPatternRange caretRange)
    {
        var forward = caretRange.Clone();
        try
        {
            if (forward.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End,
                    TextUnit.Character,
                    1) != 0)
            {
                var rectangles = forward.GetBoundingRectangles();
                var rectangle = rectangles.FirstOrDefault(value =>
                    value.Width > 0 && value.Height > 0 && IsFinite(value));
                if (rectangle.Width > 0 && rectangle.Height > 0)
                {
                    return new CaretLocation(
                        ToCaretRect(rectangle.Left, rectangle.Top, rectangle.Height),
                        "UI Automation TextPattern.GetSelection",
                        0.82);
                }
            }
        }
        catch
        {
            // Try expanding backwards when the caret is at the document end.
        }

        var backward = caretRange.Clone();
        try
        {
            if (backward.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.Start,
                    TextUnit.Character,
                    -1) != 0)
            {
                var rectangles = backward.GetBoundingRectangles();
                var rectangle = rectangles.LastOrDefault(value =>
                    value.Width > 0 && value.Height > 0 && IsFinite(value));
                if (rectangle.Width > 0 && rectangle.Height > 0)
                {
                    return new CaretLocation(
                        ToCaretRect(rectangle.Right, rectangle.Top, rectangle.Height),
                        "UI Automation TextPattern.GetSelection",
                        0.8);
                }
            }
        }
        catch
        {
            // The provider does not expose a usable range rectangle.
        }

        return null;
    }

    private static CaretLocation? TryLocateWithGuiThreadInfo(ForegroundWindowContext foreground)
    {
        try
        {
            var threadId = NativeMethods.GetWindowThreadProcessId(foreground.WindowHandle, out _);
            if (threadId == 0)
            {
                return null;
            }

            var info = new NativeMethods.GuiThreadInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
            };
            if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.CaretWindow == nint.Zero)
            {
                return null;
            }

            var caretRoot = NativeMethods.GetAncestor(info.CaretWindow, NativeMethods.GaRoot);
            if (caretRoot != nint.Zero && caretRoot != foreground.RootWindowHandle)
            {
                return null;
            }

            var rectangle = info.CaretRectangle;
            _ = NativeMethods.MapWindowPoints(info.CaretWindow, nint.Zero, ref rectangle, 2);
            var width = Math.Max(2, rectangle.Width);
            var height = rectangle.Height;
            if (height <= 0 || height > 300)
            {
                return null;
            }

            return new CaretLocation(
                new PixelRect(rectangle.Left, rectangle.Top, width, height),
                "Win32 GetGUIThreadInfo.rcCaret",
                0.9);
        }
        catch
        {
            return null;
        }
    }

    private static PixelRect ToCaretRect(double x, double y, double height) => new(
        (int)Math.Round(x),
        (int)Math.Round(y),
        2,
        Math.Max(1, (int)Math.Ceiling(height)));

    private static bool IsFinite(System.Windows.Rect rectangle) =>
        double.IsFinite(rectangle.Left) &&
        double.IsFinite(rectangle.Top) &&
        double.IsFinite(rectangle.Width) &&
        double.IsFinite(rectangle.Height);
}
