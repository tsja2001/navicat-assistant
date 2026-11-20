using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal enum HotkeyAction
{
    ToggleAssistant,
    SmartAnalyze,
    CopyFullAnswer,
    AnswerClipboardQuestion,
    ToggleGuidedTyping,
    ToggleMobile,
    ToggleMobilePrivacy,
    CycleCandidates,
    AnalyzeSelection,
    IdentifyWordExercise,
    CaptureVisionExercise,
    PreviousTaskbarPage,
    NextTaskbarPage
}

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

internal readonly record struct ParsedHotkey(
    HotkeyAction Action,
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string DisplayText);

internal readonly record struct ExclusiveHotkeyMatch(bool Suppress, HotkeyAction? Action);

internal sealed class ExclusiveHotkeyMatcher
{
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private readonly Dictionary<ulong, HotkeyAction> _shortcuts;
    private readonly HashSet<uint> _pressedModifierKeys = [];
    private readonly HashSet<uint> _suppressedKeys = [];

    public ExclusiveHotkeyMatcher(IEnumerable<ParsedHotkey> shortcuts)
    {
        _shortcuts = shortcuts.ToDictionary(
            shortcut => GlobalHotkeyService.ShortcutSignature(shortcut.Modifiers, shortcut.VirtualKey),
            shortcut => shortcut.Action);
    }

    public ExclusiveHotkeyMatch Process(uint virtualKey, bool keyDown, bool keyUp, bool injected)
    {
        if (injected || (!keyDown && !keyUp))
        {
            return new ExclusiveHotkeyMatch(false, null);
        }

        if (IsModifierVirtualKey(virtualKey))
        {
            if (keyDown) _pressedModifierKeys.Add(virtualKey);
            else _pressedModifierKeys.Remove(virtualKey);
            return new ExclusiveHotkeyMatch(false, null);
        }

        if (keyUp)
        {
            return _suppressedKeys.Remove(virtualKey)
                ? new ExclusiveHotkeyMatch(true, null)
                : new ExclusiveHotkeyMatch(false, null);
        }

        if (_suppressedKeys.Contains(virtualKey))
        {
            return new ExclusiveHotkeyMatch(true, null);
        }

        var signature = GlobalHotkeyService.ShortcutSignature(CurrentModifiers(), virtualKey);
        if (!_shortcuts.TryGetValue(signature, out var action))
        {
            return new ExclusiveHotkeyMatch(false, null);
        }

        _suppressedKeys.Add(virtualKey);
        return new ExclusiveHotkeyMatch(true, action);
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (_pressedModifierKeys.Any(IsControlKey)) modifiers |= HotkeyModifiers.Control;
        if (_pressedModifierKeys.Any(IsAltKey)) modifiers |= HotkeyModifiers.Alt;
        if (_pressedModifierKeys.Any(IsShiftKey)) modifiers |= HotkeyModifiers.Shift;
        if (_pressedModifierKeys.Any(IsWinKey)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    internal static bool IsModifierVirtualKey(uint virtualKey) =>
        IsControlKey(virtualKey) || IsAltKey(virtualKey) || IsShiftKey(virtualKey) || IsWinKey(virtualKey);

    private static bool IsControlKey(uint virtualKey) => virtualKey is VkControl or VkLControl or VkRControl;

    private static bool IsAltKey(uint virtualKey) => virtualKey is VkMenu or VkLMenu or VkRMenu;

    private static bool IsShiftKey(uint virtualKey) => virtualKey is VkShift or VkLShift or VkRShift;

    private static bool IsWinKey(uint virtualKey) => virtualKey is VkLWin or VkRWin;
}

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint LlkhfInjected = 0x00000010;
    private readonly Dictionary<int, HotkeyAction> _fallbackRegistrations = [];
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private Dispatcher? _hookThreadDispatcher;
    private ExclusiveHotkeyMatcher? _matcher;
    private Thread? _hookThread;
    private nint _windowHandle;
    private nint _hookHandle;

    public GlobalHotkeyService()
    {
        _hookCallback = HookCallback;
    }

    public event EventHandler<HotkeyAction>? Pressed;

    public bool UsesExclusiveInterception => _hookHandle != 0;

    public IReadOnlyList<string> Register(nint windowHandle, IReadOnlyDictionary<HotkeyAction, string> shortcuts)
    {
        DisposeRegistrations();
        _windowHandle = windowHandle;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var errors = new List<string>();
        var parsedShortcuts = ParseShortcutSet(shortcuts, errors);
        if (parsedShortcuts.Count == 0)
        {
            return errors;
        }

        // Windows owns the two controls needed while working in other apps.
        // Only the remaining shortcuts should be consumed by the exclusive hook.
        var systemShortcuts = parsedShortcuts.FindAll(shortcut =>
            shortcut.Action is HotkeyAction.CaptureVisionExercise or HotkeyAction.ToggleAssistant);
        var exclusiveShortcuts = parsedShortcuts.Except(systemShortcuts).ToList();
        if (exclusiveShortcuts.Count > 0)
        {
            _matcher = new ExclusiveHotkeyMatcher(exclusiveShortcuts);
            if (!TryStartExclusiveHook(out var hookError))
            {
                _matcher = null;
                errors.Add($"独占快捷键拦截不可用（{hookError}），已降级为普通系统热键。");
                systemShortcuts.AddRange(exclusiveShortcuts);
            }
        }
        RegisterSystemHotkeys(windowHandle, systemShortcuts, errors);
        return errors;
    }

    internal static bool TryValidateShortcutSet(IEnumerable<string> shortcuts, out string error)
    {
        var identities = new HashSet<ulong>();
        foreach (var shortcut in shortcuts)
        {
            if (!TryParse(shortcut, out var modifiers, out var virtualKey))
            {
                error = $"快捷键格式无效：{shortcut}";
                return false;
            }
            if (!identities.Add(ShortcutSignature(modifiers, virtualKey)))
            {
                error = $"快捷键重复：{shortcut}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    internal static bool TryParse(string shortcut, out HotkeyModifiers modifiers, out uint virtualKey)
    {
        modifiers = HotkeyModifiers.None;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        var keyText = parts[^1].Trim();
        if (keyText.Length == 1 && char.IsLetterOrDigit(keyText[0]))
        {
            virtualKey = char.ToUpper(keyText[0], CultureInfo.InvariantCulture);
            return modifiers != HotkeyModifiers.None;
        }

        if (Enum.TryParse<Key>(keyText, true, out var key) && key != Key.None)
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return modifiers != HotkeyModifiers.None &&
                   virtualKey != 0 &&
                   !ExclusiveHotkeyMatcher.IsModifierVirtualKey(virtualKey);
        }

        return false;
    }

    internal static ulong ShortcutSignature(HotkeyModifiers modifiers, uint virtualKey) =>
        ((ulong)modifiers << 32) | virtualKey;

    private static List<ParsedHotkey> ParseShortcutSet(
        IReadOnlyDictionary<HotkeyAction, string> shortcuts,
        List<string> errors)
    {
        var parsed = new List<ParsedHotkey>();
        var registeredShortcuts = new HashSet<ulong>();
        foreach (var pair in shortcuts)
        {
            if (!TryParse(pair.Value, out var modifiers, out var virtualKey))
            {
                errors.Add($"快捷键格式无效：{pair.Value}");
                continue;
            }
            if (!registeredShortcuts.Add(ShortcutSignature(modifiers, virtualKey)))
            {
                errors.Add($"快捷键重复：{pair.Value}");
                continue;
            }

            parsed.Add(new ParsedHotkey(pair.Key, modifiers, virtualKey, pair.Value));
        }
        return parsed;
    }

    private void RegisterSystemHotkeys(
        nint windowHandle,
        IReadOnlyList<ParsedHotkey> shortcuts,
        List<string> errors)
    {
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WindowProc);
        var id = 7300;
        foreach (var shortcut in shortcuts)
        {
            if (!NativeMethods.RegisterHotKey(
                    windowHandle,
                    id,
                    (uint)shortcut.Modifiers | ModNoRepeat,
                    shortcut.VirtualKey))
            {
                errors.Add($"无法注册 {shortcut.DisplayText}（Win32 {Marshal.GetLastWin32Error()}）。请检查是否已启动另一份 Assistant，或在设置中更换组合键。");
                continue;
            }

            _fallbackRegistrations[id] = shortcut.Action;
            id++;
        }
    }

    private bool TryStartExclusiveHook(out string error)
    {
        var startup = new TaskCompletionSource<HookThreadStartup>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var handle = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhKeyboardLl,
                    _hookCallback,
                    0,
                    0);
                var win32Error = handle == 0 ? Marshal.GetLastWin32Error() : 0;
                startup.TrySetResult(new HookThreadStartup(handle, dispatcher, win32Error, string.Empty));
                if (handle != 0)
                {
                    Dispatcher.Run();
                }
            }
            catch (Exception ex)
            {
                startup.TrySetResult(new HookThreadStartup(0, dispatcher, 0, ex.GetType().Name));
            }
        })
        {
            IsBackground = true,
            Name = "Assistant Exclusive Hotkeys"
        };

        try
        {
            _hookThread = thread;
            thread.Start();
            var result = startup.Task.GetAwaiter().GetResult();
            if (result.Handle == 0)
            {
                thread.Join(TimeSpan.FromSeconds(1));
                _hookThread = null;
                error = result.Win32Error != 0
                    ? $"Win32 {result.Win32Error}"
                    : string.IsNullOrWhiteSpace(result.FailureType) ? "未知错误" : result.FailureType;
                return false;
            }

            _hookHandle = result.Handle;
            _hookThreadDispatcher = result.Dispatcher;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _hookThread = null;
            error = ex.GetType().Name;
            return false;
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0 || _matcher is null)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var keyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var keyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        if (!keyDown && !keyUp)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var match = _matcher.Process(
            data.VirtualKey,
            keyDown,
            keyUp,
            (data.Flags & LlkhfInjected) != 0);
        if (match.Action is { } action)
        {
            DispatchPressed(action);
        }
        return match.Suppress
            ? 1
            : NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private void DispatchPressed(HotkeyAction action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => Pressed?.Invoke(this, action)));
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _fallbackRegistrations.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return 0;
    }

    private void DisposeRegistrations()
    {
        _matcher = null;
        var hookHandle = _hookHandle;
        var hookDispatcher = _hookThreadDispatcher;
        var hookThread = _hookThread;
        if (hookDispatcher is not null &&
            !hookDispatcher.HasShutdownStarted &&
            !hookDispatcher.HasShutdownFinished)
        {
            try
            {
                hookDispatcher.Invoke(() =>
                {
                    if (hookHandle != 0)
                    {
                        _ = NativeMethods.UnhookWindowsHookEx(hookHandle);
                    }
                    hookDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                });
                hookHandle = 0;
            }
            catch
            {
                // The current thread performs the final unhook fallback below.
            }
        }
        if (hookHandle != 0)
        {
            _ = NativeMethods.UnhookWindowsHookEx(hookHandle);
        }
        if (hookThread is not null && hookThread != Thread.CurrentThread)
        {
            _ = hookThread.Join(TimeSpan.FromSeconds(2));
        }
        _hookHandle = 0;
        _hookThreadDispatcher = null;
        _hookThread = null;

        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }

        foreach (var id in _fallbackRegistrations.Keys)
        {
            _ = NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _fallbackRegistrations.Clear();
        _dispatcher = null;
    }

    public void Dispose() => DisposeRegistrations();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public uint VirtualKey { get; init; }
        public uint ScanCode { get; init; }
        public uint Flags { get; init; }
        public uint Time { get; init; }
        public nuint ExtraInfo { get; init; }
    }

    private readonly record struct HookThreadStartup(
        nint Handle,
        Dispatcher Dispatcher,
        int Win32Error,
        string FailureType);
}
