using System.Runtime.InteropServices;
using System.Text;

namespace NavicatTextProbe.Native;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExAppWindow = 0x00040000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmMouseMove = 0x0200;
    internal const int WmLeftButtonDown = 0x0201;
    internal const int WmLeftButtonUp = 0x0202;
    internal const int WmCaptureChanged = 0x0215;
    internal const uint SemNoGpFaultErrorBox = 0x0002;
    internal const uint WmGetText = 0x000D;
    internal const uint WmGetTextLength = 0x000E;
    internal const int MaNoActivate = 3;
    internal static readonly nint HwndTopmost = new(-1);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SmtoBlock = 0x0001;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int WhKeyboardLl = 13;
    internal const int WmKeyDown = 0x0100;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyUp = 0x0105;
    internal const uint InputKeyboard = 1;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint KeyeventfUnicode = 0x0004;
    internal const uint GaRoot = 2;
    internal const uint GuiCaretBlinking = 0x00000001;
    internal const uint GcsCompStr = 0x0008;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(nint windowHandle, out uint affinity);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        public int Enable;
        public nint BlurRegion;
        public int TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmEnableBlurBehindWindow(nint hwnd, in DwmBlurBehind blur);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);

    internal static bool EnableClientTransparency(nint hwnd)
    {
        // A tiny region outside the client enables DWM alpha composition on the rest of the
        // surface. Keep the ordinary redirected HWND used for Windows capture exclusion.
        var region = CreateRectRgn(-1, -1, 0, 0);
        if (region == 0) return false;
        try
        {
            var blur = new DwmBlurBehind { Flags = 3, Enable = 1, BlurRegion = region };
            return DwmEnableBlurBehindWindow(hwnd, in blur) == 0;
        }
        finally { DeleteObject(region); }
    }

    internal static bool ExcludeFromScreenCapture(nint windowHandle) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
        SetWindowDisplayAffinity(windowHandle, WdaExcludeFromCapture) &&
        GetWindowDisplayAffinity(windowHandle, out var affinity) &&
        affinity == WdaExcludeFromCapture;

    internal delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);

    [DllImport("user32.dll")]
    internal static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint windowHandle,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutText(
        nint windowHandle,
        uint message,
        nuint wParam,
        [Out] StringBuilder lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo guiThreadInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int MapWindowPoints(
        nint fromWindow,
        nint toWindow,
        ref NativeRect points,
        uint pointCount);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string programId, out Guid classId);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid classId,
        nint reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object activeObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    internal static extern int ToUnicode(
        uint virtualKey,
        uint scanCode,
        byte[] keyboardState,
        [Out] StringBuilder buffer,
        int bufferSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetKeyboardState(byte[] keyboardState);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("imm32.dll")]
    internal static extern nint ImmGetContext(nint windowHandle);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmReleaseContext(nint windowHandle, nint inputContext);

    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
    internal static extern int ImmGetCompositionString(
        nint inputContext,
        uint index,
        nint buffer,
        uint bufferLength);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maxCount);

    internal static string ReadWindowTitle(nint windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static object GetActiveComObject(string programId)
    {
        var result = CLSIDFromProgID(programId, out var classId);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        GetActiveObject(ref classId, nint.Zero, out var activeObject);
        return activeObject;
    }

    internal static bool TryReadControlText(
        nint windowHandle,
        int maxCharacters,
        out string text,
        out bool isTruncated)
    {
        text = string.Empty;
        isTruncated = false;
        if (windowHandle == nint.Zero || maxCharacters <= 0)
        {
            return false;
        }

        const uint timeoutMilliseconds = 400;
        const uint flags = SmtoBlock | SmtoAbortIfHung;
        if (SendMessageTimeout(
                windowHandle,
                WmGetTextLength,
                0,
                0,
                flags,
                timeoutMilliseconds,
                out var rawLength) == 0 ||
            rawLength == 0)
        {
            return false;
        }

        var reportedLength = (int)Math.Min(rawLength, (nuint)(int.MaxValue - 1));
        var requestedLength = Math.Min(reportedLength, maxCharacters);
        var buffer = new StringBuilder(requestedLength + 1);
        if (SendMessageTimeoutText(
                windowHandle,
                WmGetText,
                (nuint)(requestedLength + 1),
                buffer,
                flags,
                timeoutMilliseconds,
                out var copiedLength) == 0)
        {
            return false;
        }

        text = buffer.ToString();
        var expectedCopiedLength = Math.Min(reportedLength, maxCharacters);
        isTruncated = reportedLength > maxCharacters ||
                      copiedLength < (nuint)expectedCopiedLength ||
                      text.Length < expectedCopiedLength;
        return text.Length > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    // Assistant only publishes win-x64. Native INPUT uses a 32-byte union on x64,
    // even though KEYBDINPUT itself is 24 bytes; SendInput rejects the smaller ABI.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct NativeInputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    internal static uint ReadLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.TickCount : 0;
    }
}
