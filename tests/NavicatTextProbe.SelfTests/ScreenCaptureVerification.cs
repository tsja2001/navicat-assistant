using System.Drawing;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NavicatTextProbe;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using NavicatTextProbe.Services;

internal static class ScreenCaptureVerification
{
    // This opt-in test briefly shows a synthetic answer on the interactive desktop.
    // Sample only its rectangle. Never store desktop frames or user text.
    internal static int Run(bool verifyNotepad)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var window = new TaskbarSuggestionWindow();
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    var style = new DisplayStyle { BackgroundColor = "#E51FC7", BackgroundOpacity = 1 };
                    window.UpdateSuggestion(SuggestionResult.Info("测试答案 12"), style, 360);
                    var foreground = NativeMethods.GetForegroundWindow();
                    var before = ReadRect(handle);
                    var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
                    var dy = bounds.Top + bounds.Height / 3 - before.Top;
                    SendMouse(handle, NativeMethods.WmLeftButtonDown, 1, 35, 12);
                    SendMouse(handle, NativeMethods.WmMouseMove, 1, 35, 12 + dy);
                    SendMouse(handle, NativeMethods.WmLeftButtonUp, 0, 35, 12);
                    window.UpdateSuggestion(SuggestionResult.Info("更新后的答案 12"), style, 360);
                    await Task.Delay(1200);
                    if (Math.Abs(ReadRect(handle).Top - before.Top - dy) > 2 ||
                        NativeMethods.GetForegroundWindow() != foreground)
                        throw new InvalidOperationException("拖动或刷新改变了输入焦点或丢失了拖动位置。");
                    Console.WriteLine("Answer strip moved, retained its new position after refresh, and preserved focus.");
                    if (!window.CaptureExcluded || !NativeMethods.SetWindowDisplayAffinity(handle, 0))
                        throw new InvalidOperationException("无法建立录屏对照。");
                    await Task.Delay(500);
                    var visiblePixels = CountMarkerPixels(handle);
                    if (visiblePixels < 100) throw new InvalidOperationException("对照画面没有捕获测试色块。");
                    if (!NativeMethods.ExcludeFromScreenCapture(handle)) throw new InvalidOperationException("录屏排除失败。");
                    await Task.Delay(500);
                    for (int frame = 0; frame < 10; frame++)
                    {
                        if (!window.IsVisible || CountMarkerPixels(handle) > 0)
                            throw new InvalidOperationException("提示条已隐藏，或测试色块仍出现在捕获帧中。");
                        await Task.Delay(100);
                    }
                    Console.WriteLine($"GDI capture: {visiblePixels} marker pixels before exclusion, zero in 10 protected frames.");
                    var dockItem = window.SuggestionBorder.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                        .Single(item => Equals(item.Header, "回到任务栏"));
                    dockItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    if (Math.Abs(ReadRect(handle).Top - before.Top) > 2)
                        throw new InvalidOperationException("提示条无法回到任务栏。");
                    if (verifyNotepad) await VerifyNotepadHotkeyAsync(handle);
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    window.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is null) return 0;
        Console.Error.WriteLine(error.Message);
        return 1;
    }

    private static NativeMethods.NativeRect ReadRect(nint hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect) ? rect : throw new InvalidOperationException("无法读取窗口位置。");

    private static void SendMouse(nint hwnd, int message, nuint buttons, int x, int y)
    {
        var coords = new nint((y << 16) | (x & 0xffff));
        if (NativeMethods.SendMessageTimeout(hwnd, (uint)message, buttons, coords, NativeMethods.SmtoAbortIfHung, 1000, out _) == 0)
            throw new InvalidOperationException("无法向测试提示条发送拖动消息。");
    }

    private static async Task VerifyNotepadHotkeyAsync(nint hwnd)
    {
        using var hotkeys = new GlobalHotkeyService();
        var errors = hotkeys.Register(hwnd, new Dictionary<HotkeyAction, string> { [HotkeyAction.CaptureVisionExercise] = "Ctrl+Alt+O" });
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        using var document = new DocumentContextProbeService();
        var screenshots = new VisionScreenshotCaptureService(document);
        for (var i = 0; i < 3; i++)
        {
            var foreground = DocumentContextProbeService.ReadForegroundWindow();
            if (!foreground.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase) ||
                !foreground.WindowTitle.Contains("OCR记事本测试", StringComparison.Ordinal))
                throw new InvalidOperationException("请先把专用的 OCR记事本测试.txt 测试文档切到前台。");
            var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHotkey(object? sender, HotkeyAction action)
            {
                if (action == HotkeyAction.CaptureVisionExercise) pressed.TrySetResult();
            }
            hotkeys.Pressed += OnHotkey;
            try
            {
                NativeMethods.NativeInput[] keys = [Key(0x11), Key(0x12), Key(0x4F), Key(0x4F, true), Key(0x12, true), Key(0x11, true)];
                if (NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.NativeInput>()) != keys.Length)
                    throw new InvalidOperationException("无法发送测试快捷键。");
                await pressed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (NativeMethods.GetForegroundWindow() != foreground.WindowHandle) throw new InvalidOperationException("前台已切换。");
                var capture = await screenshots.CaptureAsync(CancellationToken.None, "ForegroundWindow");
                if (!capture.IsCaptured || capture.WindowHandle != foreground.RootWindowHandle || capture.FullImageBytes.Length == 0 || capture.DetailImageBytes.Length == 0)
                    throw new InvalidOperationException("记事本截图失败。" + capture.Message);
                Console.WriteLine($"Notepad hotkey {i + 1}: full and detail captured; caret={capture.CaretSource}.");
            }
            finally { hotkeys.Pressed -= OnHotkey; }
        }
    }

    private static NativeMethods.NativeInput Key(ushort vk, bool up = false) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.NativeInputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput { VirtualKey = vk, Flags = up ? NativeMethods.KeyeventfKeyup : 0 }
        }
    };

    private static int CountMarkerPixels(nint handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("无法读取提示条位置。");
        using var frame = new Bitmap(rect.Width, rect.Height);
        using var graphics = Graphics.FromImage(frame);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, frame.Size);
        int count = 0;
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            var pixel = frame.GetPixel(x, y);
            if (Math.Abs(pixel.R - 229) < 8 && Math.Abs(pixel.G - 31) < 8 && Math.Abs(pixel.B - 199) < 8)
                count++;
        }
        return count;
    }
}
