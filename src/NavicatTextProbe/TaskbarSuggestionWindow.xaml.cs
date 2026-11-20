using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using NavicatTextProbe.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using PixelPoint = System.Drawing.Point;

namespace NavicatTextProbe;

public partial class TaskbarSuggestionWindow : Window
{
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private HwndSource? _windowSource;
    private nint _windowHandle;
    private double _preferredWidth = 640;
    private double _floatingHeight = 160;
    private double _pageHeight = 30;
    private int _currentPageIndex;
    private int _pageCount = 1;
    private bool _paginationRefreshQueued;
    private PixelPoint? _dragOffset;
    private PixelPoint? _floatingPosition;
    private NativeMethods.NativeRect? _resizeStart;
    private bool _resizeTopLeft;
    private bool _sizeChangedByPointer;

    public TaskbarSuggestionWindow()
    {
        InitializeComponent();
        SuggestionScroller.ScrollChanged += (_, _) => QueuePaginationRefresh();
        SourceInitialized += (_, _) => ConfigureToolWindow();
        _dragTimer.Tick += (_, _) =>
        {
            // A non-activating window can miss mouse messages when a fast drag leaves its bounds.
            if (NativeMethods.GetAsyncKeyState(1) >= 0) EndDrag();
            else MoveWithPointer(System.Windows.Forms.Cursor.Position);
        };
        _positionTimer.Tick += (_, _) =>
        {
            if (IsVisible) Reposition();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Reposition();
                QueuePaginationRefresh();
                _positionTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
                if (_dragOffset.HasValue) EndDrag();
            }
        };
        StateChanged += (_, _) =>
        {
            if (IsVisible && WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Reposition();
            }
        };
        Closed += (_, _) =>
        {
            _positionTimer.Stop();
            _dragTimer.Stop();
            _windowSource?.RemoveHook(WindowProc);
            _windowSource = null;
        };
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideAssistantRequested;
    internal event Action<double, double>? FloatingSizeChanged;

    internal bool CaptureExcluded { get; private set; }

    public void UpdateSuggestion(SuggestionResult suggestion, DisplayStyle style, double preferredWidth, double floatingHeight = 160)
    {
        if (_resizeStart is null)
        {
            _preferredWidth = Math.Clamp(preferredWidth, 200, 1400);
            _floatingHeight = Math.Clamp(floatingHeight, 30, 900);
        }
        var text = TaskbarSuggestionFormatter.BuildText(suggestion);
        var changed = SuggestionText.Text != text;
        if (changed) SuggestionText.Text = text;
        SuggestionText.FontSize = style.FontSize;
        SuggestionText.LineHeight = style.LineHeight;
        SuggestionText.Foreground = ParseBrush(style.FontColor, Colors.White);
        PageIndicator.FontSize = Math.Max(9, style.FontSize - 2);
        PageIndicator.Foreground = ParseBrush(style.FontColor, Colors.White);
        SuggestionBorder.Background = ParseBrush(
            style.BackgroundColor,
            Color.FromRgb(32, 32, 32),
            style.BackgroundOpacity);
        if (changed) ResetPagination();
        else QueuePaginationRefresh();
        if (IsVisible) Reposition();
    }

    internal void UpdateRuntimeStatus(AssistantRuntimeStatus status)
    {
        MainStatusDot.Fill = StatusBrush(status.MainLevel);
        MainStatusDot.ToolTip = $"主流程：{status.MainText}";
        DatabaseStatusDot.Fill = StatusBrush(status.DatabaseLevel);
        DatabaseStatusDot.ToolTip = $"数据库增强：{status.DatabaseText}";
        GuidedTypingStatusDot.Fill = StatusBrush(status.GuidedTypingLevel);
        GuidedTypingStatusDot.ToolTip = $"受控代打：{status.GuidedTypingText}";
    }

    public bool ChangePage(int direction)
    {
        if (direction == 0)
        {
            return false;
        }

        RefreshPagination();
        var offset = SuggestionScroller.VerticalOffset;
        var target = Math.Clamp(offset + Math.Sign(direction) * _pageHeight, 0, SuggestionScroller.ScrollableHeight);
        if (Math.Abs(offset - target) < 0.01) return false;
        SuggestionScroller.ScrollToVerticalOffset(target);
        return true;
    }

    internal int CurrentPageIndex => _currentPageIndex;

    internal int PageCount => _pageCount;

    internal static int CalculatePageCount(double extentHeight, double pageHeight)
    {
        if (!double.IsFinite(extentHeight) || !double.IsFinite(pageHeight) || pageHeight <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, extentHeight - 0.5) / pageHeight));
    }

    public void Reposition()
    {
        if (_resizeStart.HasValue) return;
        if (_floatingPosition is PixelPoint position)
        {
            PositionFloatingWindow(position, System.Windows.Forms.Screen.FromPoint(position));
            return;
        }
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0 || !NativeMethods.GetWindowRect(taskbar, out var taskbarRect) || taskbarRect.Width <= taskbarRect.Height)
        {
            RepositionFallback();
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var tray = NativeMethods.FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        var rightPixels = taskbarRect.Right - (int)(190 * scale);
        if (tray != 0 && NativeMethods.GetWindowRect(tray, out var trayRect) && trayRect.Left > taskbarRect.Left)
        {
            rightPixels = trayRect.Left;
        }

        var paddingPixels = (int)(6 * scale);
        var minimumLeftPixels = taskbarRect.Left + (int)(180 * scale);
        var rebar = NativeMethods.FindWindowEx(taskbar, 0, "ReBarWindow32", null);
        var taskSwitch = rebar == 0 ? 0 : NativeMethods.FindWindowEx(rebar, 0, "MSTaskSwWClass", null);
        var taskList = taskSwitch == 0 ? 0 : NativeMethods.FindWindowEx(taskSwitch, 0, "MSTaskListWClass", null);
        if (taskList != 0 && NativeMethods.GetWindowRect(taskList, out var taskListRect))
        {
            minimumLeftPixels = Math.Max(minimumLeftPixels, taskListRect.Right + paddingPixels);
        }

        var availableWidthPixels = rightPixels - minimumLeftPixels - paddingPixels;
        int actualWidthPixels;
        int leftPixels;
        if (availableWidthPixels >= (int)(200 * scale))
        {
            actualWidthPixels = Math.Min((int)(_preferredWidth * scale), availableWidthPixels);
            leftPixels = rightPixels - actualWidthPixels - paddingPixels;
        }
        else
        {
            actualWidthPixels = (int)(200 * scale);
            leftPixels = rightPixels - actualWidthPixels - paddingPixels;
        }

        SetGeometry(
            leftPixels / scale,
            (taskbarRect.Top + (int)(2 * scale)) / scale,
            actualWidthPixels / scale,
            Math.Max(30, (taskbarRect.Height - (int)(4 * scale)) / scale));
    }

    private void RepositionFallback()
    {
        var workArea = SystemParameters.WorkArea;
        var width = Math.Min(_preferredWidth, Math.Max(200, workArea.Width - 12));
        SetGeometry(workArea.Right - width - 6, workArea.Bottom - 46, width, 42);
    }

    private void SetGeometry(double left, double top, double width, double height)
    {
        var geometryChanged = false;
        if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.5) Left = left;
        if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.5) Top = top;
        if (Math.Abs(Width - width) > 0.5)
        {
            Width = width;
            geometryChanged = true;
        }
        if (Math.Abs(Height - height) > 0.5)
        {
            Height = height;
            geometryChanged = true;
        }
        if (geometryChanged)
        {
            QueuePaginationRefresh();
        }
        KeepAboveWindowsWithoutActivation();
    }

    private void ResetPagination()
    {
        _currentPageIndex = 0;
        _pageCount = 1;
        PageIndicator.Visibility = Visibility.Collapsed;
        PageIndicatorColumn.Width = new GridLength(0);
        SuggestionScroller.ScrollToTop();
        QueuePaginationRefresh();
    }

    private void QueuePaginationRefresh()
    {
        if (_paginationRefreshQueued)
        {
            return;
        }

        _paginationRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _paginationRefreshQueued = false;
            RefreshPagination();
        });
    }

    private void RefreshPagination()
    {
        UpdateLayout();
        _pageHeight = Math.Max(1, SuggestionScroller.ViewportHeight);
        var pageCountWithoutIndicator = CalculatePageCount(SuggestionScroller.ExtentHeight, _pageHeight);
        var needsIndicator = pageCountWithoutIndicator > 1;
        PageIndicator.Text = $"{_currentPageIndex + 1}/{pageCountWithoutIndicator}";
        PageIndicator.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var indicatorWidth = needsIndicator ? Math.Max(30, PageIndicator.DesiredSize.Width + 6) : 0;
        if ((PageIndicator.Visibility == Visibility.Visible) != needsIndicator ||
            Math.Abs(PageIndicatorColumn.Width.Value - indicatorWidth) > 0.5)
        {
            PageIndicator.Visibility = needsIndicator ? Visibility.Visible : Visibility.Collapsed;
            PageIndicatorColumn.Width = new GridLength(indicatorWidth);
            UpdateLayout();
            _pageHeight = Math.Max(1, SuggestionScroller.ViewportHeight);
        }

        _pageCount = CalculatePageCount(SuggestionScroller.ExtentHeight, _pageHeight);
        _currentPageIndex = SuggestionScroller.VerticalOffset >= SuggestionScroller.ScrollableHeight - 0.5
            ? _pageCount - 1
            : Math.Clamp((int)(SuggestionScroller.VerticalOffset / _pageHeight), 0, _pageCount - 1);
        if (_pageCount <= 1)
        {
            PageIndicator.Visibility = Visibility.Collapsed;
            PageIndicatorColumn.Width = new GridLength(0);
        }
        PageIndicator.Text = $"{_currentPageIndex + 1}/{_pageCount}";
    }

    private void ConfigureToolWindow()
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        if (_windowHandle == 0)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        extendedStyle &= ~NativeMethods.WsExAppWindow;
        _ = NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle, new nint(extendedStyle));
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        ConfigureComposition();
        _windowSource?.AddHook(WindowProc);
        KeepAboveWindowsWithoutActivation();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x031E) ConfigureComposition(); // WM_DWMCOMPOSITIONCHANGED
        if (message == 0x020A) // WM_MOUSEWHEEL
        {
            var delta = unchecked((short)(wParam.ToInt64() >> 16));
            var lines = SystemParameters.WheelScrollLines;
            var step = lines < 0 ? SuggestionScroller.ViewportHeight : lines * SuggestionText.LineHeight;
            SuggestionScroller.ScrollToVerticalOffset(SuggestionScroller.VerticalOffset - step * delta / 120d);
            handled = true;
            return 0;
        }
        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return NativeMethods.MaNoActivate;
        }

        if (message == NativeMethods.WmLeftButtonDown)
        {
            var point = MousePoint(lParam);
            var scale = VisualTreeHelper.GetDpi(this);
            var hit = InputHitTest(new System.Windows.Point(point.X / scale.DpiScaleX, point.Y / scale.DpiScaleY)) as DependencyObject;
            while (hit != null && hit != TopLeftGrip && hit != BottomRightGrip)
            {
                if (hit is System.Windows.Controls.Primitives.ScrollBar) return 0;
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            }
            if (hit != null && NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                _resizeStart = rect;
                _resizeTopLeft = hit == TopLeftGrip;
            }
            _dragOffset = point;
            NativeMethods.SetCapture(hwnd);
            _dragTimer.Start();
            handled = true;
        }
        else if (message == NativeMethods.WmMouseMove && _dragOffset.HasValue)
        {
            if ((wParam.ToInt64() & 1) == 0)
            {
                EndDrag();
            }
            else if (NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                var relative = MousePoint(lParam);
                MoveWithPointer(new PixelPoint(rect.Left + relative.X, rect.Top + relative.Y));
            }
            handled = true;
        }
        else if (message == NativeMethods.WmLeftButtonUp && _dragOffset.HasValue)
        {
            EndDrag();
            handled = true;
        }
        else if (message == NativeMethods.WmCaptureChanged)
        {
            FinishPointerGesture();
        }
        return 0;
    }

    private static PixelPoint MousePoint(nint lParam) =>
        new(unchecked((short)lParam.ToInt64()), unchecked((short)(lParam.ToInt64() >> 16)));

    private void EndDrag()
    {
        FinishPointerGesture();
        NativeMethods.ReleaseCapture();
    }

    private void FinishPointerGesture()
    {
        _dragTimer.Stop();
        _dragOffset = null;
        _resizeStart = null;
        if (_sizeChangedByPointer)
        {
            _sizeChangedByPointer = false;
            FloatingSizeChanged?.Invoke(_preferredWidth, _floatingHeight);
        }
    }

    private void MoveWithPointer(PixelPoint pointer)
    {
        if (_dragOffset is not PixelPoint offset || !NativeMethods.GetWindowRect(_windowHandle, out var rect)) return;
        if (_resizeStart is { } start)
        {
            var scale = VisualTreeHelper.GetDpi(this);
            int dx = pointer.X - start.Left - offset.X, dy = pointer.Y - start.Top - offset.Y;
            if (dx == 0 && dy == 0 && !_sizeChangedByPointer) return;
            var sign = _resizeTopLeft ? -1 : 1;
            _preferredWidth = Math.Clamp((start.Width + (double)sign * dx) / scale.DpiScaleX, 200, 1400);
            _floatingHeight = Math.Clamp((start.Height + (double)sign * dy) / scale.DpiScaleY, 30, 900);
            var anchor = _resizeTopLeft
                ? new PixelPoint(start.Right - (int)Math.Round(_preferredWidth * scale.DpiScaleX),
                    start.Bottom - (int)Math.Round(_floatingHeight * scale.DpiScaleY))
                : new PixelPoint(start.Left, start.Top);
            _sizeChangedByPointer = true;
            PositionFloatingWindow(anchor, System.Windows.Forms.Screen.FromPoint(pointer));
            return;
        }
        var position = new PixelPoint(pointer.X - offset.X, pointer.Y - offset.Y);
        if (position.X != rect.Left || position.Y != rect.Top)
            PositionFloatingWindow(position, System.Windows.Forms.Screen.FromPoint(pointer));
    }

    private void PositionFloatingWindow(PixelPoint position, System.Windows.Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var dpi = NativeMethods.GetDpiForWindow(_windowHandle);
        var scale = dpi == 0 ? 1 : dpi / 96d;
        var width = Math.Min(bounds.Width, (int)Math.Round(_preferredWidth * scale));
        var height = Math.Min(bounds.Height, (int)Math.Round(_floatingHeight * scale));
        var left = Math.Clamp(position.X, bounds.Left, bounds.Right - width);
        var top = Math.Clamp(position.Y, bounds.Top, bounds.Bottom - height);
        _floatingPosition = new PixelPoint(left, top);
        NativeMethods.SetWindowPos(_windowHandle, NativeMethods.HwndTopmost,
            left, top, width, height, NativeMethods.SwpNoActivate);
    }

    private void DockMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _floatingPosition = null;
        Reposition();
    }

    private void ConfigureComposition()
    {
        if (_windowSource?.CompositionTarget is { } target)
        {
            target.BackgroundColor = NativeMethods.EnableClientTransparency(_windowHandle)
                ? Colors.Transparent : Color.FromRgb(32, 32, 32);
        }
        CaptureExcluded = NativeMethods.ExcludeFromScreenCapture(_windowHandle);
    }

    private void KeepAboveWindowsWithoutActivation()
    {
        if (_windowHandle == 0 || !IsVisible)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);
    }

    private static SolidColorBrush ParseBrush(string value, Color fallback, double opacity = 1)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Opacity = Math.Clamp(opacity, 0, 1);
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(fallback);
            brush.Opacity = Math.Clamp(opacity, 0, 1);
            return brush;
        }
    }

    private static SolidColorBrush StatusBrush(string level) => level switch
    {
        "off" => new SolidColorBrush(Color.FromRgb(127, 140, 141)),
        "working" => new SolidColorBrush(Color.FromRgb(243, 156, 18)),
        "ready" => new SolidColorBrush(Color.FromRgb(39, 174, 96)),
        "error" => new SolidColorBrush(Color.FromRgb(192, 57, 43)),
        _ => new SolidColorBrush(Color.FromRgb(52, 152, 219))
    };

    private void OpenSettingsMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HideMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        HideAssistantRequested?.Invoke(this, EventArgs.Empty);
}
