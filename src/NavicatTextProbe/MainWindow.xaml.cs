using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using NavicatTextProbe.Services;
using Color = System.Windows.Media.Color;

namespace NavicatTextProbe;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _probeTimer;
    private readonly NavicatProbeService _probeService = new();
    private readonly DocumentContextProbeService _documentContextProbeService = new();
    private readonly VisionScreenshotCaptureService _visionScreenshotCaptureService;
    private readonly ProbeLogger _logger;
    private readonly SettingsService _settingsService;
    private readonly DeepSeekClient _deepSeekClient = new();
    private readonly DeepSeekVisionClient _deepSeekVisionClient = new();
    private readonly NavicatSelectionService _selectionService = new();
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly KeyboardFallbackService _keyboardFallbackService = new();
    private readonly GuidedTypingService _guidedTypingService = new();
    private readonly MySqlDatabaseService _databaseService = new();
    private readonly MobileServerService _mobileServer = new();
    private readonly TaskbarSuggestionWindow _taskbarWindow = new();
    private readonly TrayIconService _trayIcon;
    private readonly StringBuilder _fallbackText = new();
    private readonly Dictionary<string, SuggestionResult> _candidateCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _candidateCacheOrder = new();
    private CancellationTokenSource? _predictionCancellation;
    private CancellationTokenSource? _visionCancellation;
    private CancellationTokenSource? _copyAnswerCancellation;
    private CancellationTokenSource? _databaseCatalogCancellation;
    private ProbeResult? _lastCapturedProbe;
    private DocumentContextResult? _lastDocumentContext;
    private SuggestionResult _currentSuggestion = SuggestionResult.Info("等待 Navicat SQL、Word 练习题或主动截图……");
    private bool _captureInProgress;
    private bool _exitRequested;
    private bool _fallbackSessionInitialized;
    private bool _sqlViewDirty;
    private bool _suggestionViewDirty;
    private bool _forceNextExerciseRequest;
    private bool _wordExerciseLocateRequested;
    private int _fallbackCaret;
    private string? _lastContentHash;
    private string? _lastStateKey;
    private string? _lastPredictionContext;
    private string? _predictionInFlightContext;
    private string? _manualCandidateContext;
    private string? _manualCandidateRequestProbeKey;
    private string? _apiKey;
    private string? _screenOcrKey;
    private bool _hasSavedScreenOcrKey;
    private string? _databasePassword;
    private DatabaseCatalogSnapshot? _databaseCatalog;
    private string _databaseStatus = "未使用数据库增强";
    private string _guidedTypingStatus = "代打关闭";
    private string _guidedTypingLevel = "off";
    private SuggestionAcceptanceState? _suggestionAcceptance;
    private SuggestionAcceptanceState? _exerciseSuggestionAcceptance;
    private WordExerciseQuestionState? _wordExerciseQuestion;
    private SuggestionResult? _lastWordExerciseSuggestion;
    private string _wordExerciseBaselineText = string.Empty;
    private string _wordExercisePendingDocumentIdentity = string.Empty;
    private string? _wordExerciseLastProcessedStateKey;
    private bool _wordExerciseCaretOutsideCurrentQuestion;
    private string? _authorizedWordDocumentIdentity;
    private string? _lastWordDocumentCompletionContext;
    private ProbeResult? _pendingCaptureLog;
    private DateTimeOffset _pendingCaptureChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHealthSnapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCompletionRequestStarted = DateTimeOffset.MinValue;
    private string? _lastExerciseRequestContext;
    private string? _exerciseInFlightContext;
    private bool _candidateRequestInProgress;
    private bool _visionRequestInProgress;
    private long _visionRequestSequence;
    private long _copyAnswerRequestSequence;
    private bool _lastVisionHasReliableCaret;

    public MainWindow()
    {
        InitializeComponent();
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Application.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        var assistantRoot = WorkspacePaths.FindAssistantRoot();
        _logger = new ProbeLogger(Path.Combine(assistantRoot, "logs"));
        _settingsService = new SettingsService(assistantRoot);
        _visionScreenshotCaptureService = new VisionScreenshotCaptureService(_documentContextProbeService);
        _trayIcon = new TrayIconService();
        _apiKey = SafeReadApiKey();
        try
        {
            _screenOcrKey = WindowsCredentialService.ReadScreenOcrKey();
            _hasSavedScreenOcrKey = !string.IsNullOrWhiteSpace(_screenOcrKey);
            RememberScreenOcrKeyBox.IsChecked = _hasSavedScreenOcrKey;
            DeleteScreenOcrKeyButton.IsEnabled = _hasSavedScreenOcrKey;
            ScreenOcrKeyStatus.Text = _hasSavedScreenOcrKey ? "已载入保存的密钥" : "请输入密钥后点应用";
        }
        catch (Win32Exception)
        {
            DeleteScreenOcrKeyButton.IsEnabled = true;
            ScreenOcrKeyStatus.Text = "无法读取已保存的密钥，可手动输入";
        }
        _databasePassword = SafeReadDatabasePassword(_settingsService.Current.DatabaseConnection.ProfileId);

        LogPathText.Text = $"日志：{Path.GetFileName(_logger.LogPath)}";
        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _probeTimer.Tick += ProbeTimer_OnTick;

        SourceInitialized += (_, _) =>
        {
            _mainWindowCaptureExcluded = NativeMethods.ExcludeFromScreenCapture(new WindowInteropHelper(this).Handle);
            RegisterHotkeys();
        };
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        _taskbarWindow.SettingsRequested += (_, _) => OpenSettings(showAppearance: true);
        _taskbarWindow.FloatingSizeChanged += SaveFloatingSize;
        _taskbarWindow.HideAssistantRequested += (_, _) => SetAssistantEnabledAsync(false).Forget();

        _hotkeyService.Pressed += HotkeyService_OnPressed;
        _keyboardFallbackService.InputCaptured += KeyboardFallbackService_OnInputCaptured;
        _guidedTypingService.StatusChanged += GuidedTypingService_OnStatusChanged;
        _mobileServer.SettingsChanged += MobileServer_OnSettingsChanged;

        _trayIcon.OpenRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.SettingsRequested += (_, _) => Dispatcher.Invoke(OpenSettings);
        _trayIcon.ToggleAssistantRequested += (_, _) => Dispatcher.Invoke(() =>
            SetAssistantEnabledAsync(!_settingsService.Current.AssistantEnabled).Forget());
        _trayIcon.ToggleTaskbarRequested += (_, _) => Dispatcher.Invoke(() => SetTaskbarEnabled(!_settingsService.Current.TaskbarPanelEnabled));
        _trayIcon.ToggleMobileRequested += (_, _) => Dispatcher.Invoke(() => ToggleMobileAsync().Forget());
        _trayIcon.ExitRequested += (_, _) => Dispatcher.Invoke(() => ExitAsync().Forget());
    }

    private bool _mainWindowCaptureExcluded;

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.WriteSessionStarted();
        _logger.WriteConfiguration(_settingsService.Current);
        if (_settingsService.Current.AssistantEnabled)
        {
            _logger.WriteHealthSnapshot();
        }
        _lastHealthSnapshotAt = DateTimeOffset.UtcNow;
        await ApplySettingsAsync();
        await WarmDatabaseCatalogAsync();
        if (_settingsService.Current.AssistantEnabled && _settingsService.Current.MobileServerEnabled)
        {
            await RefreshFirewallStatusAsync();
        }
        else
        {
            FirewallStatusText.Text = "防火墙：手机服务未运行，暂不检测";
        }
        ShowVisionReadyHint();
    }

    private void ShowVisionReadyHint()
    {
        var settings = _settingsService.Current;
        if (settings.AssistantEnabled && settings.VisionEnabled)
            UpdateSuggestion(SuggestionResult.Info(string.IsNullOrWhiteSpace(_screenOcrKey)
                ? $"截图测试已就绪，按 {settings.VisionScreenshotHotkey}。联网答题请在主窗口输入 DeepSeek OCR 密钥。"
                : $"按 {settings.VisionScreenshotHotkey} 识别当前屏幕，答案显示在此处。"));
    }

    private async void ProbeTimer_OnTick(object? sender, EventArgs e)
        => await CaptureNowAsync(forceRefresh: false);

    private async Task CaptureNowAsync(bool forceRefresh)
    {
        if (!_settingsService.Current.AssistantEnabled || _captureInProgress || _visionRequestInProgress)
        {
            return;
        }

        _captureInProgress = true;
        try
        {
            var documentResult = await _documentContextProbeService.CaptureForegroundAsync();
            if (_settingsService.Current.AssistantEnabled && documentResult.IsTargetApplication)
            {
                ApplyDocumentContextResult(documentResult);
            }
            else if (_settingsService.Current.AssistantEnabled)
            {
                var result = await Task.Run(() => _probeService.CaptureForegroundNavicat(forceRefresh));
                if (_settingsService.Current.AssistantEnabled)
                {
                    ApplyProbeResult(result);
                }
            }
            if (_settingsService.Current.AssistantEnabled &&
                DateTimeOffset.UtcNow - _lastHealthSnapshotAt >= TimeSpan.FromMinutes(1))
            {
                _logger.WriteHealthSnapshot();
                _lastHealthSnapshotAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            ApplyProbeResult(ProbeResult.Failed("探测器内部错误", ex.ToString()));
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void ApplyDocumentContextResult(DocumentContextResult result)
    {
        FlushPendingCaptureLog(force: true);
        _keyboardFallbackService.Stop();
        _fallbackSessionInitialized = false;
        _fallbackText.Clear();

        var selectionHash = ContentHasher.Sha256(result.SelectedText);
        var stateKey = $"document|{result.Status}|{result.ProcessId}|{result.Source}|{result.ContentHash}|" +
                       $"{result.CaretOffset}|{result.CaretAvailable}|{result.SelectionAvailable}|{selectionHash}|{result.Message}";
        var stateChanged = !string.Equals(_lastStateKey, stateKey, StringComparison.Ordinal);
        _lastStateKey = stateKey;

        if (stateChanged)
        {
            var captured = result.Status == DocumentContextStatus.Captured;
            StatusDot.Fill = captured
                ? result.IsPartial ? Brush(243, 156, 18) : Brush(39, 174, 96)
                : Brush(192, 57, 43);
            StatusText.Text = result.Message;
            DetailText.Text =
                $"软件：{result.ApplicationName} | 来源：{result.Source} | " +
                $"正文：{result.Text.Length:N0} 字符 | 光标：{(result.CaretAvailable ? "已识别" : "未识别")} | " +
                $"选区：{(result.SelectionAvailable ? result.HasSelection ? $"{result.SelectedText.Length:N0} 字符" : "无" : "未识别")}";
            _logger.WriteDocumentContext(result);
        }

        if (result.Status == DocumentContextStatus.Captured)
        {
            _lastDocumentContext = result;
            if (_guidedTypingService.IsActive && result.CaretOffset is int documentCaret)
            {
                _guidedTypingService.ValidateObservation(
                    result.WindowHandle,
                    result.Text,
                    documentCaret);
            }
            if (_copyAnswerCancellation is not null)
            {
                return;
            }
            var documentContentKey = "document|" + result.ContentHash;
            if (!string.Equals(_lastContentHash, documentContentKey, StringComparison.Ordinal))
            {
                _lastContentHash = documentContentKey;
                if (IsVisible)
                {
                    SqlTextBox.Text = result.Text;
                    _sqlViewDirty = false;
                }
                else
                {
                    _sqlViewDirty = true;
                }
                LengthText.Text = result.IsPartial
                    ? $"已暴露 {result.Text.Length:N0} 字符（可能不完整）"
                    : $"{result.Text.Length:N0} 字符";
            }
        }

        if (result.Status == DocumentContextStatus.Captured && IsWordDocument(result))
        {
            if (!string.Equals(_authorizedWordDocumentIdentity, result.DocumentIdentity, StringComparison.Ordinal))
            {
                PrepareForWordExerciseMode();
            }
            if (_authorizedWordDocumentIdentity is not null &&
                !string.Equals(_authorizedWordDocumentIdentity, result.DocumentIdentity, StringComparison.Ordinal))
            {
                _authorizedWordDocumentIdentity = null;
                _lastWordDocumentCompletionContext = null;
                UpdateSuggestion(SuggestionResult.Info(
                    $"已切换 Word 文档；按 {_settingsService.Current.SmartActionHotkey} 为当前文档开启 AI 辅助"));
                return;
            }
            if ((_wordExerciseQuestion is not null || _wordExerciseLocateRequested) &&
                !string.Equals(
                    _wordExerciseQuestion?.DocumentIdentity ?? _wordExercisePendingDocumentIdentity,
                    result.DocumentIdentity,
                    StringComparison.Ordinal))
            {
                ClearWordExerciseSession(cancelRequest: true);
                UpdateSuggestion(SuggestionResult.Info(
                    $"已切换 Word 文档，请按 {_settingsService.Current.WordExerciseHotkey} 识别题目"));
                return;
            }

            if (_wordExerciseLocateRequested)
            {
                var immediate = _forceNextExerciseRequest;
                _forceNextExerciseRequest = false;
                ScheduleWordExercise(result, WordExerciseRequestMode.LocateAndAnswer, immediate);
                return;
            }

            if (_wordExerciseQuestion is not null)
            {
                if (!result.CaretAvailable)
                {
                    ResetExerciseRequestState(cancelRequest: true);
                    UpdateSuggestion(SuggestionResult.Info(
                        $"当前仍是 {_wordExerciseQuestion.DisplayLabel}；Word 暂未提供可靠光标"));
                    return;
                }

                if (result.HasSelection)
                {
                    ResetExerciseRequestState(cancelRequest: true, clearAcceptance: false);
                    StatusText.Text = $"当前题目：{_wordExerciseQuestion.DisplayLabel}（已选中文字）";
                    DetailText.Text =
                        $"选择或复制文字期间暂不刷新续写；取消选区后再根据稳定光标更新。{_settingsService.Current.WordExerciseHotkey}：识别/切换光标题目";
                    if (_lastWordExerciseSuggestion is not null &&
                        _currentSuggestion.Mode is not SuggestionMode.ExerciseAnswer and
                            not SuggestionMode.ExerciseContinuation)
                    {
                        UpdateSuggestion(_lastWordExerciseSuggestion);
                    }
                    return;
                }

                var questionType = WordExercisePresentation.ToDisplayType(_wordExerciseQuestion.QuestionType);
                StatusText.Text = $"当前题目：{_wordExerciseQuestion.DisplayLabel}（{questionType}）";
                DetailText.Text =
                    $"当前题目由 AI 识别 | 子问：{(string.IsNullOrWhiteSpace(_wordExerciseQuestion.ActiveSubquestionNumber) ? "无" : _wordExerciseQuestion.ActiveSubquestionNumber)} | " +
                    $"{_settingsService.Current.WordExerciseHotkey}：识别/切换光标题目";

                var observationKey = BuildWordExerciseObservationKey(result);
                var needsContinuation = !string.Equals(
                    observationKey,
                    _wordExerciseLastProcessedStateKey,
                    StringComparison.Ordinal);
                if (needsContinuation)
                {
                    ScheduleWordExercise(result, WordExerciseRequestMode.ContinueCurrentQuestion, immediate: false);
                }
                else if (!_wordExerciseCaretOutsideCurrentQuestion &&
                         _lastWordExerciseSuggestion is not null &&
                         _currentSuggestion.Mode is not SuggestionMode.ExerciseAnswer and
                             not SuggestionMode.ExerciseContinuation)
                {
                    UpdateSuggestion(_lastWordExerciseSuggestion);
                }
                return;
            }

            if (string.Equals(_authorizedWordDocumentIdentity, result.DocumentIdentity, StringComparison.Ordinal))
            {
                if (result.HasSelection)
                {
                    CancelPredictionForDocumentPreview();
                    _lastWordDocumentCompletionContext = null;
                    UpdateSuggestion(SuggestionResult.Info(
                        $"Word 当前有选区；按 {_settingsService.Current.SmartActionHotkey} 分析选区，取消选区后恢复自动续写"));
                    return;
                }
                ScheduleWordDocumentCompletion(result, immediate: false);
                return;
            }
        }

        CancelPredictionForDocumentPreview();

        UpdateSuggestion(new SuggestionResult
        {
            Mode = SuggestionMode.Information,
            DisplayText = IsWordDocument(result) && result.Status == DocumentContextStatus.Captured
                ? DocumentContextFormatter.BuildTaskbarText(result) +
                  $" ｜ {_settingsService.Current.WordExerciseHotkey}识别题目"
                : DocumentContextFormatter.BuildTaskbarText(result),
            Explanation = string.IsNullOrWhiteSpace(result.Diagnostics)
                ? result.Message
                : $"{result.Message} {result.Diagnostics}"
        });
    }

    private void CancelPredictionForDocumentPreview()
    {
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = null;
        _predictionInFlightContext = null;
        _lastPredictionContext = null;
        _manualCandidateContext = null;
        _suggestionAcceptance = null;
        _lastExerciseRequestContext = null;
        _exerciseInFlightContext = null;
        _exerciseSuggestionAcceptance = null;
    }

    private void ScheduleWordDocumentCompletion(DocumentContextResult document, bool immediate)
    {
        if (!document.CaretAvailable || document.CaretOffset is not int caret)
        {
            UpdateSuggestion(SuggestionResult.Info("Word 暂未提供可靠光标，已暂停文档续写。"));
            return;
        }
        if (!_settingsService.Current.AiEnabled || string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Info("请先开启 AI 并保存 DeepSeek API Key。"));
            return;
        }

        var contextKey = $"word_document|{document.DocumentIdentity}|{document.ContentHash}|{caret}";
        if (!immediate && string.Equals(contextKey, _lastWordDocumentCompletionContext, StringComparison.Ordinal))
        {
            return;
        }

        _lastWordDocumentCompletionContext = contextKey;
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        RunWordDocumentCompletionAsync(
            document,
            contextKey,
            immediate,
            _predictionCancellation.Token).Forget();
    }

    private async Task RunWordDocumentCompletionAsync(
        DocumentContextResult document,
        string contextKey,
        bool immediate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(_settingsService.Current.PredictionDebounceMilliseconds, cancellationToken);
            }
            var stopwatch = Stopwatch.StartNew();
            var result = await _deepSeekClient.CompleteAsync(
                _settingsService.Current,
                _apiKey!,
                PromptTemplates.BuildWordDocumentCompletionPrompt(document),
                cancellationToken,
                maxTokens: 900,
                systemPrompt: PromptTemplates.DefaultWordWritingSystemPrompt);
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested ||
                !string.Equals(contextKey, _lastWordDocumentCompletionContext, StringComparison.Ordinal) ||
                !string.Equals(_authorizedWordDocumentIdentity, document.DocumentIdentity, StringComparison.Ordinal) ||
                _lastDocumentContext is not { } current ||
                !WordExerciseContextBuilder.IsSameObservation(document, current))
            {
                return;
            }

            result = result with { Origin = "word_document" };
            _logger.WriteSuggestion("word_document_completion", document.Text.Length, stopwatch.ElapsedMilliseconds, result);
            UpdateSuggestion(result);
        }
        catch (OperationCanceledException)
        {
            // A newer Word observation superseded this request.
        }
    }

    private void ScheduleWordExercise(
        DocumentContextResult document,
        WordExerciseRequestMode mode,
        bool immediate)
    {
        if (!document.CaretAvailable || document.CaretOffset is not int caretOffset)
        {
            if (mode == WordExerciseRequestMode.LocateAndAnswer)
            {
                _wordExerciseLocateRequested = false;
                UpdateSuggestion(SuggestionResult.Error(
                    "无法识别当前题目",
                    "Word 暂未提供可靠光标，请单击题干后重试。"));
            }
            return;
        }

        var questionIdentity = _wordExerciseQuestion?.QuestionIdentity ?? "locating";
        var modeKey = mode == WordExerciseRequestMode.LocateAndAnswer ? "locate" : "continue";
        var contextKey = $"word_exercise|{modeKey}|{document.DocumentIdentity}|{document.ContentHash}|" +
                         $"{caretOffset}|{questionIdentity}";
        var previousSuggestion = _lastWordExerciseSuggestion?.InsertText ?? _currentSuggestion.InsertText;
        if (string.Equals(contextKey, _exerciseInFlightContext, StringComparison.Ordinal) ||
            string.Equals(contextKey, _lastExerciseRequestContext, StringComparison.Ordinal))
        {
            return;
        }

        if (mode == WordExerciseRequestMode.ContinueCurrentQuestion)
        {
            var currentProbe = new ProbeResult
            {
                Text = document.Text,
                CaretOffset = caretOffset
            };
            var progress = SuggestionAcceptanceMatcher.Match(
                _exerciseSuggestionAcceptance,
                currentProbe);
            if (progress.IsFollowing)
            {
                UpdateFollowingWordExerciseSuggestion(progress);
                if (!progress.IsComplete)
                {
                    ResetExerciseRequestState(cancelRequest: true, clearAcceptance: false);
                    _lastExerciseRequestContext = contextKey;
                    _wordExerciseLastProcessedStateKey = BuildWordExerciseObservationKey(document);
                    return;
                }
            }
        }

        if (!_settingsService.Current.AiEnabled)
        {
            if (mode == WordExerciseRequestMode.LocateAndAnswer)
            {
                _wordExerciseLocateRequested = false;
                UpdateSuggestion(SuggestionResult.Info("AI 已关闭，无法识别当前题目"));
            }
            else
            {
                _wordExerciseLastProcessedStateKey = BuildWordExerciseObservationKey(document);
                HandleWordExerciseFailure(mode, "AI 已关闭，暂不更新当前题目的续写。", preserveAnswer: true);
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            if (mode == WordExerciseRequestMode.LocateAndAnswer)
            {
                _wordExerciseLocateRequested = false;
                UpdateSuggestion(SuggestionResult.Info("请先保存 DeepSeek API Key，再识别 Word 题目"));
            }
            else
            {
                _wordExerciseLastProcessedStateKey = BuildWordExerciseObservationKey(document);
                HandleWordExerciseFailure(mode, "DeepSeek API Key 不可用，暂不更新当前题目的续写。", preserveAnswer: true);
            }
            return;
        }

        _exerciseSuggestionAcceptance = null;
        _suggestionAcceptance = null;
        _lastPredictionContext = null;
        _predictionInFlightContext = null;
        _lastExerciseRequestContext = contextKey;
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        _exerciseInFlightContext = contextKey;

        if (mode == WordExerciseRequestMode.LocateAndAnswer)
        {
            UpdateSuggestion(SuggestionResult.Loading("正在识别光标所在题目……"));
        }
        else
        {
            StatusText.Text = $"当前题目：{_wordExerciseQuestion?.DisplayLabel ?? "当前题"}，正在更新续写";
        }

        RunWordExerciseAsync(
            document,
            contextKey,
            mode,
            immediate,
            previousSuggestion,
            _predictionCancellation.Token).Forget();
    }

    private async Task RunWordExerciseAsync(
        DocumentContextResult document,
        string contextKey,
        WordExerciseRequestMode mode,
        bool immediate,
        string previousSuggestion,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = immediate ? 0 : _settingsService.Current.PredictionDebounceMilliseconds;
            if (!immediate)
            {
                var earliest = _lastCompletionRequestStarted.AddMilliseconds(
                    _settingsService.Current.PredictionMinimumIntervalMilliseconds);
                var intervalDelay = (int)Math.Ceiling((earliest - DateTimeOffset.UtcNow).TotalMilliseconds);
                delay = Math.Max(delay, Math.Max(0, intervalDelay));
            }
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            int[] radii =
            [
                WordExerciseContextBuilder.InitialRadius,
                WordExerciseContextBuilder.FirstExpandedRadius,
                WordExerciseContextBuilder.FinalExpandedRadius
            ];
            SuggestionResult? result = null;
            WordExerciseRequestContext? requestContext = null;
            string? locateValidationError = null;
            for (var attempt = 0; attempt < radii.Length; attempt++)
            {
                requestContext = WordExerciseContextBuilder.Build(
                    document,
                    mode,
                    radii[attempt],
                    attempt,
                    mode == WordExerciseRequestMode.ContinueCurrentQuestion ? _wordExerciseQuestion : null,
                    previousSuggestion);
                if (requestContext is null)
                {
                    HandleWordExerciseFailure(mode, "Word 当前没有可靠正文或光标。", preserveAnswer: true);
                    return;
                }

                _lastCompletionRequestStarted = DateTimeOffset.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                result = await _deepSeekClient.CompleteAsync(
                    _settingsService.Current,
                    _apiKey!,
                    PromptTemplates.BuildWordExercisePrompt(requestContext),
                    cancellationToken,
                    maxTokens: 3000,
                    temperature: 0.1,
                    systemPrompt: PromptTemplates.BuildExerciseSystemPrompt(_settingsService.Current));
                stopwatch.Stop();

                if (cancellationToken.IsCancellationRequested ||
                    !IsCurrentWordExerciseRequest(document, contextKey))
                {
                    return;
                }

                _logger.WriteSuggestion(
                    mode == WordExerciseRequestMode.LocateAndAnswer
                        ? $"word_exercise_locate_{radii[attempt]}"
                        : $"word_exercise_continue_{radii[attempt]}",
                    requestContext.ContextCharacterCount,
                    stopwatch.ElapsedMilliseconds,
                    result,
                    requestContext);
                if (result.Mode == SuggestionMode.Error)
                {
                    HandleWordExerciseFailure(mode, result.DisplayText, preserveAnswer: true);
                    return;
                }

                if (mode == WordExerciseRequestMode.LocateAndAnswer &&
                    !result.IsQuestion &&
                    result.ContextComplete)
                {
                    locateValidationError = "当前内容不像 SQL 数据库练习题。";
                    break;
                }

                var needsExpansion = WordExerciseContextBuilder.NeedsExpandedContext(requestContext, result);
                if (mode == WordExerciseRequestMode.LocateAndAnswer && !needsExpansion)
                {
                    if (WordExerciseContextBuilder.TryCreateQuestionState(
                            document,
                            requestContext,
                            result,
                            string.IsNullOrEmpty(_wordExerciseBaselineText)
                                ? document.Text
                                : _wordExerciseBaselineText,
                            out var locatedQuestion,
                            out locateValidationError))
                    {
                        _wordExerciseQuestion = locatedQuestion;
                        break;
                    }
                    needsExpansion = true;
                }

                if (!needsExpansion || attempt == radii.Length - 1)
                {
                    break;
                }

                if (mode == WordExerciseRequestMode.LocateAndAnswer)
                {
                    UpdateSuggestion(SuggestionResult.Loading("题目可能超出当前窗口，正在扩大范围……"));
                }
                else
                {
                    StatusText.Text = $"当前题目：{_wordExerciseQuestion?.DisplayLabel ?? "当前题"}，正在扩大续写上下文";
                }
            }

            if (result is null || requestContext is null)
            {
                HandleWordExerciseFailure(mode, "没有取得有效的题目识别结果。", preserveAnswer: true);
                return;
            }

            if (!IsCurrentWordExerciseRequest(document, contextKey))
            {
                return;
            }

            if (mode == WordExerciseRequestMode.LocateAndAnswer)
            {
                if (_wordExerciseQuestion is null)
                {
                    _wordExerciseLocateRequested = false;
                    var failure = !result.IsQuestion
                        ? "当前内容不像 SQL 数据库练习题。"
                        : locateValidationError ?? "未完整识别题目，请把光标移到题干或选中完整题目后重试。";
                    HandleWordExerciseFailure(mode, failure, preserveAnswer: false);
                    return;
                }
                _wordExerciseLocateRequested = false;
                _wordExerciseCaretOutsideCurrentQuestion = false;
            }
            else
            {
                if (_wordExerciseQuestion is null)
                {
                    return;
                }
                if (!result.SameAsCurrentQuestion)
                {
                    _wordExerciseLastProcessedStateKey = BuildWordExerciseObservationKey(document);
                    _wordExerciseCaretOutsideCurrentQuestion = true;
                    var switchHint =
                        $"当前仍是 {_wordExerciseQuestion.DisplayLabel}；如已进入新题，请按 {_settingsService.Current.WordExerciseHotkey}";
                    UpdateSuggestion(_lastWordExerciseSuggestion is null
                        ? SuggestionResult.Info(switchHint)
                        : _lastWordExerciseSuggestion with
                        {
                            Mode = SuggestionMode.ExerciseContinuation,
                            DisplayText = switchHint,
                            InsertText = string.Empty,
                            Explanation = switchHint,
                            SameAsCurrentQuestion = false
                        });
                    return;
                }

                if (WordExerciseContextBuilder.NeedsExpandedContext(requestContext, result) ||
                    !result.IsQuestion ||
                    result.Mode != SuggestionMode.ExerciseContinuation ||
                    result.Confidence < WordExerciseContextBuilder.MinimumExerciseConfidence)
                {
                    HandleWordExerciseFailure(
                        mode,
                        "当前上下文不足以生成可靠续写，已保留上一条答案。",
                        preserveAnswer: true);
                    return;
                }

                _wordExerciseCaretOutsideCurrentQuestion = false;

                _wordExerciseQuestion = _wordExerciseQuestion with
                {
                    ActiveSubquestionNumber = string.IsNullOrWhiteSpace(result.ActiveSubquestionNumber)
                        ? _wordExerciseQuestion.ActiveSubquestionNumber
                        : WordExerciseContextBuilder.NormalizeSubquestionNumber(result.ActiveSubquestionNumber),
                    QuestionType = result.QuestionType == "unknown" || string.IsNullOrWhiteSpace(result.QuestionType)
                        ? _wordExerciseQuestion.QuestionType
                        : WordExerciseContextBuilder.NormalizeQuestionType(result.QuestionType)
                };
            }

            var question = _wordExerciseQuestion!;
            var expectedMode = mode == WordExerciseRequestMode.LocateAndAnswer
                ? SuggestionMode.ExerciseAnswer
                : SuggestionMode.ExerciseContinuation;
            var questionType = result.QuestionType == "unknown" || string.IsNullOrWhiteSpace(result.QuestionType)
                ? question.QuestionType
                : WordExerciseContextBuilder.NormalizeQuestionType(result.QuestionType);
            var fullAnswer = string.IsNullOrWhiteSpace(result.FullAnswer)
                ? mode == WordExerciseRequestMode.ContinueCurrentQuestion &&
                  !string.IsNullOrWhiteSpace(_lastWordExerciseSuggestion?.FullAnswer)
                    ? _lastWordExerciseSuggestion.FullAnswer
                    : string.IsNullOrWhiteSpace(result.InsertText) ? result.DisplayText : result.InsertText
                : result.FullAnswer;
            var insertText = mode == WordExerciseRequestMode.LocateAndAnswer && string.IsNullOrWhiteSpace(result.InsertText)
                ? fullAnswer
                : result.InsertText;
            var answerForTaskbar = WordExercisePresentation.SelectTaskbarAnswer(
                result,
                questionType,
                fullAnswer,
                insertText,
                mode == WordExerciseRequestMode.ContinueCurrentQuestion);
            var displayText = WordExercisePresentation.BuildDisplayText(
                answerForTaskbar,
                insertText,
                questionType,
                question.QuestionNumber,
                mode == WordExerciseRequestMode.ContinueCurrentQuestion);
            var modeHint =
                $"当前题目：{question.DisplayLabel}；按 {_settingsService.Current.WordExerciseHotkey} 识别或切换到光标所在新题。";
            result = result with
            {
                Mode = expectedMode,
                DisplayText = displayText,
                InsertText = insertText,
                FullAnswer = fullAnswer,
                QuestionType = questionType,
                QuestionNumber = question.QuestionNumber,
                ActiveSubquestionNumber = question.ActiveSubquestionNumber,
                QuestionText = question.QuestionText,
                Explanation = string.IsNullOrWhiteSpace(result.Explanation)
                    ? modeHint
                    : result.Explanation.Trim() + Environment.NewLine + Environment.NewLine + modeHint
            };

            _exerciseSuggestionAcceptance = string.IsNullOrWhiteSpace(insertText)
                ? null
                : new SuggestionAcceptanceState(
                    document.Text,
                    Math.Clamp(document.CaretOffset ?? document.Text.Length, 0, document.Text.Length),
                    insertText);
            _wordExerciseLastProcessedStateKey = BuildWordExerciseObservationKey(document);
            _lastWordExerciseSuggestion = result;
            UpdateSuggestion(result);
        }
        catch (OperationCanceledException)
        {
            // A newer Word edit, caret move or application switch superseded this request.
        }
        catch (Exception ex)
        {
            _logger.WriteDiagnostic("word_exercise", ex.ToString());
            if (IsCurrentWordExerciseRequest(document, contextKey))
            {
                HandleWordExerciseFailure(
                    mode,
                    $"题目处理发生异常：{ex.Message}",
                    preserveAnswer: mode == WordExerciseRequestMode.ContinueCurrentQuestion);
            }
        }
        finally
        {
            if (string.Equals(_exerciseInFlightContext, contextKey, StringComparison.Ordinal))
            {
                _exerciseInFlightContext = null;
            }
        }
    }

    private async Task IdentifyWordExerciseAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info(
                $"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        DocumentContextResult document;
        try
        {
            document = await _documentContextProbeService.CaptureForegroundAsync();
        }
        catch (Exception ex)
        {
            _logger.WriteDiagnostic("word_exercise_hotkey", ex.ToString());
            UpdateSuggestion(SuggestionResult.Error("无法识别当前题目", "读取 Word 正文或光标时发生异常。"));
            return;
        }
        if (document.Status != DocumentContextStatus.Captured || !IsWordDocument(document))
        {
            UpdateSuggestion(SuggestionResult.Error(
                "无法识别当前题目",
                "请先把文本光标放到 Word 的一道 SQL 数据库练习题内，再按识别快捷键。"));
            return;
        }
        if (!document.CaretAvailable)
        {
            UpdateSuggestion(SuggestionResult.Error(
                "无法识别当前题目",
                "Word 暂未提供可靠光标，请单击题干、选项或答案后重试。"));
            return;
        }
        if (!_settingsService.Current.AiEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info("AI 已关闭，无法识别当前题目"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Info("请先保存 DeepSeek API Key，再识别 Word 题目"));
            return;
        }

        ResetExerciseRequestState(cancelRequest: true);
        _wordExerciseQuestion = null;
        _lastWordExerciseSuggestion = null;
        _wordExerciseBaselineText = document.Text;
        _wordExercisePendingDocumentIdentity = document.DocumentIdentity;
        _wordExerciseLastProcessedStateKey = null;
        _wordExerciseCaretOutsideCurrentQuestion = false;
        _wordExerciseLocateRequested = true;
        _forceNextExerciseRequest = true;
        ApplyDocumentContextResult(document);
    }

    private void UpdateFollowingWordExerciseSuggestion(SuggestionAcceptanceProgress progress)
    {
        if (_lastWordExerciseSuggestion is null || _wordExerciseQuestion is null)
        {
            return;
        }

        var updated = WordExercisePresentation.AdvanceFollowingSuggestion(
            _lastWordExerciseSuggestion,
            _wordExerciseQuestion,
            progress);
        _lastWordExerciseSuggestion = updated;
        UpdateSuggestion(updated);
    }

    private void HandleWordExerciseFailure(
        WordExerciseRequestMode mode,
        string message,
        bool preserveAnswer)
    {
        if (mode == WordExerciseRequestMode.LocateAndAnswer)
        {
            _wordExerciseLocateRequested = false;
            if (_wordExerciseQuestion is null)
            {
                _wordExerciseBaselineText = string.Empty;
                _wordExercisePendingDocumentIdentity = string.Empty;
                _wordExerciseLastProcessedStateKey = null;
            }
        }
        if (preserveAnswer && _lastWordExerciseSuggestion is not null)
        {
            _lastWordExerciseSuggestion = _lastWordExerciseSuggestion with
            {
                Explanation = $"本次更新失败，已保留上一条答案：{message}"
            };
            UpdateSuggestion(_lastWordExerciseSuggestion);
            return;
        }
        UpdateSuggestion(SuggestionResult.Error("未能识别或回答当前题目", message));
    }

    private void ResetExerciseRequestState(bool cancelRequest, bool clearAcceptance = true)
    {
        if (cancelRequest)
        {
            _predictionCancellation?.Cancel();
            _predictionCancellation?.Dispose();
            _predictionCancellation = null;
        }
        _lastExerciseRequestContext = null;
        _exerciseInFlightContext = null;
        if (clearAcceptance)
        {
            _exerciseSuggestionAcceptance = null;
        }
    }

    private void LeaveExerciseRequestIfActive()
    {
        if (_lastExerciseRequestContext is not null ||
            _exerciseInFlightContext is not null ||
            _exerciseSuggestionAcceptance is not null)
        {
            ResetExerciseRequestState(cancelRequest: true);
        }
    }

    private void PrepareForWordExerciseMode()
    {
        if (_exerciseInFlightContext is null && _predictionCancellation is not null)
        {
            _predictionCancellation?.Cancel();
            _predictionCancellation?.Dispose();
            _predictionCancellation = null;
        }
        _predictionInFlightContext = null;
        _lastPredictionContext = null;
        _manualCandidateContext = null;
        _manualCandidateRequestProbeKey = null;
        _suggestionAcceptance = null;
    }

    private void ClearWordExerciseSession(bool cancelRequest)
    {
        ResetExerciseRequestState(cancelRequest);
        _wordExerciseQuestion = null;
        _lastWordExerciseSuggestion = null;
        _wordExerciseLocateRequested = false;
        _wordExerciseBaselineText = string.Empty;
        _wordExercisePendingDocumentIdentity = string.Empty;
        _wordExerciseLastProcessedStateKey = null;
        _wordExerciseCaretOutsideCurrentQuestion = false;
        _forceNextExerciseRequest = false;
    }

    private static bool IsWordDocument(DocumentContextResult result)
        => result.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase);

    private static string BuildWordExerciseObservationKey(DocumentContextResult document) =>
        $"{document.DocumentIdentity}|{document.ContentHash}|{document.CaretOffset}";

    private bool IsCurrentWordExerciseRequest(DocumentContextResult requestedDocument, string contextKey)
    {
        var current = _lastDocumentContext;
        return _settingsService.Current.AssistantEnabled &&
               string.Equals(contextKey, _lastExerciseRequestContext, StringComparison.Ordinal) &&
               current is not null &&
               current.Status == DocumentContextStatus.Captured &&
               IsWordDocument(current) &&
               WordExerciseContextBuilder.IsSameObservation(requestedDocument, current);
    }

    private void ApplyProbeResult(ProbeResult result)
    {
        var stateKey = $"{result.Status}|{result.ProcessId}|{result.Source}|{result.Message}|{result.CaretOffset}";
        var stateChanged = !string.Equals(_lastStateKey, stateKey, StringComparison.Ordinal);
        _lastStateKey = stateKey;

        switch (result.Status)
        {
            case ProbeStatus.Captured:
                LeaveExerciseRequestIfActive();
                _keyboardFallbackService.Stop();
                _fallbackSessionInitialized = false;
                _fallbackText.Clear();
                if (stateChanged)
                {
                    StatusDot.Fill = result.IsTruncated ? Brush(243, 156, 18) : Brush(39, 174, 96);
                    StatusText.Text = result.IsTruncated
                        ? "Navicat 当前 SQL 只读取到部分内容"
                        : "已读取 Navicat 当前 SQL 文档全文";
                    DetailText.Text =
                        $"进程：{result.ProcessName}（PID {result.ProcessId}） | {result.Source} | " +
                        $"光标：第 {result.CaretLine?.ToString() ?? "?"} 行，第 {result.CaretColumn?.ToString() ?? "?"} 列";
                }
                _lastCapturedProbe = result;
                if (_guidedTypingService.IsActive && result.CaretOffset is int probeCaret)
                {
                    _guidedTypingService.ValidateObservation(
                        result.WindowHandle,
                        result.Text,
                        probeCaret);
                }

                var contentChanged = !string.Equals(_lastContentHash, result.ContentHash, StringComparison.Ordinal);
                if (contentChanged)
                {
                    _lastContentHash = result.ContentHash;
                    if (IsVisible)
                    {
                        SqlTextBox.Text = result.Text;
                        _sqlViewDirty = false;
                    }
                    else
                    {
                        _sqlViewDirty = true;
                    }
                    LengthText.Text = result.IsTruncated ? $"至少 {result.Text.Length:N0} 字符" : $"{result.Text.Length:N0} 字符";
                    _pendingCaptureLog = result;
                    _pendingCaptureChangedAt = DateTimeOffset.UtcNow;
                }
                else if (stateChanged)
                {
                    _logger.WriteState(result);
                }

                if (_pendingCaptureLog is not null &&
                    string.Equals(_pendingCaptureLog.ContentHash, result.ContentHash, StringComparison.Ordinal))
                {
                    _pendingCaptureLog = result;
                }
                FlushPendingCaptureLog(force: false);

                SchedulePrediction(result);
                break;

            case ProbeStatus.NavicatNotForeground:
                LeaveExerciseRequestIfActive();
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.Stop();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(243, 156, 18);
                    StatusText.Text = _settingsService.Current.VisionEnabled ? "屏幕识别已就绪" : "等待 Navicat 成为前台窗口……";
                    DetailText.Text = _settingsService.Current.VisionEnabled
                        ? $"切到要识别的窗口并按 {_settingsService.Current.VisionScreenshotHotkey}，在可拖动的提示条上查看答案。"
                        : "切回 Navicat 查询编辑器继续输入。";
                }
                if (stateChanged) _logger.WriteState(result);
                break;

            case ProbeStatus.NoReadableText:
                LeaveExerciseRequestIfActive();
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.CaptureEnabled =
                    _settingsService.Current.KeyboardFallbackEnabled && _keyboardFallbackService.Start();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(192, 57, 43);
                    StatusText.Text = "Navicat 全文接口暂时不可读";
                    DetailText.Text = _keyboardFallbackService.CaptureEnabled
                        ? "键盘兜底已临时接管：只在内存恢复输入，不写原始按键日志。"
                        : "键盘兜底默认关闭；可在设置中主动允许。";
                }
                if (_keyboardFallbackService.CaptureEnabled) InitializeFallbackSession();
                if (stateChanged) _logger.WriteState(result);
                break;

            default:
                LeaveExerciseRequestIfActive();
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.Stop();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(192, 57, 43);
                    StatusText.Text = result.Message;
                    DetailText.Text = result.Diagnostics ?? "未知错误";
                }
                if (stateChanged) _logger.WriteState(result);
                break;
        }
    }

    private void FlushPendingCaptureLog(bool force)
    {
        if (_pendingCaptureLog is null ||
            (!force && DateTimeOffset.UtcNow - _pendingCaptureChangedAt < TimeSpan.FromMilliseconds(1400)))
        {
            return;
        }

        _logger.WriteCapture(_pendingCaptureLog);
        _pendingCaptureLog = null;
    }

    private void SchedulePrediction(ProbeResult probe, bool immediate = false)
    {
        if (!_settingsService.Current.AssistantEnabled || _copyAnswerCancellation is not null)
        {
            return;
        }

        if (probe.IsTruncated)
        {
            _suggestionAcceptance = null;
            _lastPredictionContext = null;
            _predictionCancellation?.Cancel();
            _predictionInFlightContext = null;
            UpdateSuggestion(SuggestionResult.Info(
                $"SQL 读取不完整（仅 {probe.Text.Length:N0} 字符），已暂停 AI 预测，避免给出错误续写"));
            return;
        }

        if (string.IsNullOrWhiteSpace(probe.Text))
        {
            return;
        }

        var contextKey = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
        var probeKey = BuildProbeKey(probe);
        if (_candidateRequestInProgress && string.Equals(probeKey, _manualCandidateRequestProbeKey, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(contextKey, _predictionInFlightContext, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(contextKey, _lastPredictionContext, StringComparison.Ordinal))
        {
            return;
        }

        var immediateGuardApplied = false;
        var immediateGuard = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(probe);
        if (immediateGuard is not null && !SqlContextAnalyzer.IsCompatible(probe, _currentSuggestion))
        {
            RememberSuggestionForAcceptance(probe, immediateGuard);
            UpdateSuggestion(immediateGuard);
            immediateGuardApplied = true;
        }

        if (!_settingsService.Current.AiEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            if (!immediateGuardApplied)
            {
                UpdateSuggestion(SuggestionResult.Info("未配置 DeepSeek API Key；全文探测仍在正常工作"));
            }
            return;
        }

        if (!immediateGuardApplied && IsFollowingCurrentSuggestion(probe))
        {
            _lastPredictionContext = contextKey;
            _predictionCancellation?.Cancel();
            return;
        }

        _suggestionAcceptance = null;
        _lastPredictionContext = contextKey;
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        _predictionInFlightContext = contextKey;
        RunCompletionAsync(probe, contextKey, immediate, _predictionCancellation.Token).Forget();
    }

    private async Task RunCompletionAsync(ProbeResult probe, string contextKey, bool immediate, CancellationToken cancellationToken)
    {
        try
        {
            var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
            var newLine = caret > 0 && probe.Text[caret - 1] == '\n';
            var delay = immediate ? 0 : newLine ? 120 : _settingsService.Current.PredictionDebounceMilliseconds;
            if (!newLine && !immediate)
            {
                var earliest = _lastCompletionRequestStarted.AddMilliseconds(
                    _settingsService.Current.PredictionMinimumIntervalMilliseconds);
                var intervalDelay = (int)Math.Ceiling((earliest - DateTimeOffset.UtcNow).TotalMilliseconds);
                delay = Math.Max(delay, Math.Max(0, intervalDelay));
            }
            if (delay > 0) await Task.Delay(delay, cancellationToken);

            _lastCompletionRequestStarted = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var prompt = PromptTemplates.BuildCompletionPrompt(probe);
            var result = await CompleteSqlRequestAsync(
                prompt,
                () => string.Equals(contextKey, _lastPredictionContext, StringComparison.Ordinal),
                cancellationToken);
            result = SqlContextAnalyzer.GuardAutomaticResult(probe, result);
            stopwatch.Stop();
            if (!cancellationToken.IsCancellationRequested && string.Equals(contextKey, _lastPredictionContext, StringComparison.Ordinal))
            {
                _logger.WriteSuggestion(newLine ? "new_line" : "completion", probe.Text.Length, stopwatch.ElapsedMilliseconds, result);
                if (result.Mode == SuggestionMode.Error && _currentSuggestion.Mode == SuggestionMode.Completion)
                {
                    SuggestionExplanationText.Text = $"本次更新失败，已保留上一条建议：{result.DisplayText}";
                    return;
                }

                RememberSuggestionForAcceptance(probe, result);
                _manualCandidateContext = null;
                UpdateSuggestion(result);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this request.
        }
        finally
        {
            if (string.Equals(_predictionInFlightContext, contextKey, StringComparison.Ordinal))
            {
                _predictionInFlightContext = null;
            }
        }
    }

    private async Task<SuggestionResult> CompleteSqlRequestAsync(
        string prompt,
        Func<bool> contextIsCurrent,
        CancellationToken cancellationToken,
        int maxTokens = 500,
        double temperature = 0.2,
        string? systemPrompt = null)
    {
        var databaseSettings = _settingsService.Current.DatabaseConnection;
        if (databaseSettings.Enabled && !string.IsNullOrEmpty(_databasePassword))
        {
            try
            {
                _databaseStatus = "正在准备缓存数据库目录";
                RefreshRuntimeIndicators();
                var catalog = _databaseCatalog;
                if (catalog is null)
                {
                    catalog = await _databaseService.GetCatalogSnapshotAsync(
                        databaseSettings,
                        _databasePassword,
                        cancellationToken);
                    _databaseCatalog = catalog;
                }
                else if (DateTimeOffset.UtcNow - catalog.LoadedAt >= MySqlDatabaseService.CatalogCacheLifetime &&
                         _databaseCatalogCancellation is null)
                {
                    WarmDatabaseCatalogAsync(forceRefresh: true, reportStatus: false).Forget();
                }
                EnsureDatabaseToolContext(contextIsCurrent, cancellationToken);
                var assisted = await _deepSeekClient.CompleteWithDatabaseToolsAsync(
                    _settingsService.Current,
                    _apiKey!,
                    prompt,
                    catalog,
                    (request, requestNumber, token) => ExecuteDatabaseToolAsync(
                        request,
                        requestNumber,
                        catalog,
                        contextIsCurrent,
                        token),
                    cancellationToken,
                    maxTokens,
                    temperature,
                    systemPrompt);
                if (assisted.Suggestion.Mode == SuggestionMode.Error)
                {
                    // 数据库工具会话失败（例如 DeepSeek 超时）时降级普通 AI 补全，
                    // 由 CompleteAsync 内部的智谱 GLM 冗余继续兜底。
                    _databaseStatus = "数据库工具会话失败，已降级普通 AI 补全";
                    RefreshRuntimeIndicators();
                    _logger.WriteDatabaseContext(false, 0, false, assisted.Suggestion.DisplayText);
                }
                else
                {
                    _databaseStatus = assisted.Message;
                    RefreshRuntimeIndicators();
                    return assisted.Suggestion;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _databaseStatus = $"数据库目录不可用，已降级普通 AI：{ex.Message}";
                RefreshRuntimeIndicators();
                _logger.WriteDatabaseContext(false, 0, false, ex.Message);
            }
        }
        else if (databaseSettings.Enabled)
        {
            _databaseStatus = "数据库增强未启用工具：未保存数据库密码";
            RefreshRuntimeIndicators();
        }

        return await _deepSeekClient.CompleteAsync(
            _settingsService.Current,
            _apiKey!,
            prompt,
            cancellationToken,
            maxTokens,
            temperature,
            systemPrompt: systemPrompt);
    }

    private async Task<DatabaseContextResult> ExecuteDatabaseToolAsync(
        DatabaseToolRequest request,
        int requestNumber,
        DatabaseCatalogSnapshot catalog,
        Func<bool> contextIsCurrent,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.Current.DatabaseConnection;
        if (string.IsNullOrEmpty(_databasePassword))
        {
            _databaseStatus = "数据库增强失败：未保存数据库密码";
            RefreshRuntimeIndicators();
            return DatabaseContextResult.Failure("请在设置中保存数据库密码。");
        }

        try
        {
            EnsureDatabaseToolContext(contextIsCurrent, cancellationToken);
            _databaseStatus = $"AI 正在调用只读数据库工具（{requestNumber}/2）";
            StatusText.Text = _databaseStatus;
            RefreshRuntimeIndicators();
            var result = await _databaseService.ExecuteReadOnlyAsync(
                settings,
                _databasePassword,
                new DatabaseQueryPlan(true, request.Sql, request.Purpose, string.Empty),
                catalog.Summary,
                cancellationToken);
            EnsureDatabaseToolContext(contextIsCurrent, cancellationToken);
            _databaseStatus = result.IsSuccess
                ? $"AI 已完成第 {requestNumber} 次只读查询"
                : $"第 {requestNumber} 次只读查询被拒绝或失败";
            RefreshRuntimeIndicators();
            _logger.WriteDatabaseContext(result.IsSuccess, result.RowCount, result.WasTruncated, result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _databaseStatus = $"数据库增强失败：{ex.Message}";
            RefreshRuntimeIndicators();
            _logger.WriteDatabaseContext(false, 0, false, ex.Message);
            return DatabaseContextResult.Failure(ex.Message);
        }
    }

    private static void EnsureDatabaseToolContext(
        Func<bool> contextIsCurrent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextIsCurrent())
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task CycleCandidatesAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info($"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        if (_candidateRequestInProgress)
        {
            SuggestionExplanationText.Text = "正在生成 3 个候选，请稍候；完成后再按一次会切换到下一个。";
            return;
        }

        var probe = await Task.Run(() => _probeService.CaptureForegroundNavicat(forceRefresh: true));
        if (probe.Status != ProbeStatus.Captured || string.IsNullOrWhiteSpace(probe.Text))
        {
            UpdateSuggestion(SuggestionResult.Error(
                "无法按当前光标生成候选",
                "请先让 Navicat 查询编辑器位于前台，再按候选快捷键。"));
            return;
        }

        if (probe.IsTruncated)
        {
            UpdateSuggestion(SuggestionResult.Error(
                "当前 SQL 读取不完整，已停止生成候选",
                "请确认 Navicat 编辑器原生全文读取恢复后再试，避免基于截断内容生成错误续写。"));
            return;
        }

        _lastCapturedProbe = probe;
        var probeKey = BuildProbeKey(probe);
        var candidateContext = probeKey + "|continuation_candidates";
        if (string.Equals(candidateContext, _manualCandidateContext, StringComparison.Ordinal) &&
            _currentSuggestion.HasCandidates)
        {
            var next = _currentSuggestion.SelectCandidate(_currentSuggestion.ActiveCandidateIndex + 1);
            RememberSuggestionForAcceptance(probe, next);
            UpdateSuggestion(next);
            _logger.WriteSuggestion("candidate_cycle", probe.Text.Length, 0, next);
            return;
        }

        if (_candidateCache.TryGetValue(candidateContext, out var cached) && cached.HasCandidates)
        {
            _manualCandidateContext = candidateContext;
            _lastPredictionContext = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
            var first = cached.SelectCandidate(0);
            RememberSuggestionForAcceptance(probe, first);
            UpdateSuggestion(first);
            _logger.WriteSuggestion("candidate_cache", probe.Text.Length, 0, first);
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Error("请先在设置中保存新的 DeepSeek API Key"));
            return;
        }

        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        _predictionInFlightContext = null;
        var token = _predictionCancellation.Token;
        _candidateRequestInProgress = true;
        _manualCandidateRequestProbeKey = probeKey;
        SuggestionExplanationText.Text = "正在按当前精确光标生成 3 个候选；生成期间保留现有任务栏建议，避免闪烁。";

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await CompleteSqlRequestAsync(
                PromptTemplates.BuildCandidateContinuationPrompt(probe),
                () => string.Equals(_manualCandidateRequestProbeKey, probeKey, StringComparison.Ordinal),
                token,
                1800,
                0.35);
            result = SqlContextAnalyzer.RankCandidates(probe, result);
            stopwatch.Stop();
            if (token.IsCancellationRequested)
            {
                return;
            }

            _logger.WriteSuggestion("manual_candidates", probe.Text.Length, stopwatch.ElapsedMilliseconds, result);
            if (result.Mode == SuggestionMode.Error)
            {
                if (_currentSuggestion.Mode == SuggestionMode.Completion)
                {
                    SuggestionExplanationText.Text = $"候选生成失败，已保留上一条建议：{result.DisplayText}";
                }
                else
                {
                    UpdateSuggestion(result);
                }
                return;
            }

            if (!result.HasCandidates)
            {
                UpdateSuggestion(SuggestionResult.Error("模型没有返回可用候选", "请继续输入少量 SQL 后再按一次快捷键。"));
                return;
            }

            AddCandidateCache(candidateContext, result);
            _manualCandidateContext = candidateContext;
            _lastPredictionContext = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
            var first = result.SelectCandidate(0);
            RememberSuggestionForAcceptance(probe, first);
            UpdateSuggestion(first);
        }
        catch (OperationCanceledException)
        {
            // The SQL changed while candidates were being generated.
        }
        finally
        {
            if (string.Equals(_manualCandidateRequestProbeKey, probeKey, StringComparison.Ordinal))
            {
                _candidateRequestInProgress = false;
                _manualCandidateRequestProbeKey = null;
            }
        }
    }

    private void AddCandidateCache(string key, SuggestionResult result)
    {
        if (!_candidateCache.ContainsKey(key))
        {
            _candidateCacheOrder.Enqueue(key);
        }

        _candidateCache[key] = result;
        while (_candidateCacheOrder.Count > 20)
        {
            _candidateCache.Remove(_candidateCacheOrder.Dequeue());
        }
    }

    private static string BuildProbeKey(ProbeResult probe)
    {
        var contentHash = string.IsNullOrWhiteSpace(probe.ContentHash)
            ? ContentHasher.Sha256(probe.Text)
            : probe.ContentHash;
        return $"{contentHash}|{probe.CaretOffset ?? probe.Text.Length}";
    }

    private async Task CaptureVisionExerciseAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info(
                $"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }
        if (!_settingsService.Current.VisionEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info("屏幕 OCR 已在设置中关闭"));
            return;
        }
        CancelVisionRequest();
        CancelCopyAnswerRequest();
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = null;
        _predictionInFlightContext = null;
        _candidateRequestInProgress = false;
        _manualCandidateRequestProbeKey = null;

        var requestSequence = Interlocked.Increment(ref _visionRequestSequence);
        _visionCancellation = new CancellationTokenSource();
        var token = _visionCancellation.Token;
        _visionRequestInProgress = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.WriteDiagnostic("vision_hotkey", "已收到屏幕识别请求");
            UpdateSuggestion(SuggestionResult.Loading("已收到快捷键，正在截图……"));
            var capture = await _visionScreenshotCaptureService.CaptureAsync(
                token,
                _settingsService.Current.VisionCaptureScope);
            if (token.IsCancellationRequested || requestSequence != Volatile.Read(ref _visionRequestSequence))
            {
                return;
            }

            _logger.WriteVisionCapture(capture, stopwatch.ElapsedMilliseconds);
            if (!capture.IsCaptured)
            {
                UpdateSuggestion(SuggestionResult.Error("无法截取前台画面", capture.Message));
                return;
            }
            _lastVisionHasReliableCaret = capture.HasReliableCaret;

            if (string.IsNullOrWhiteSpace(_screenOcrKey))
            {
                UpdateSuggestion(SuggestionResult.Info("截图成功。请在主窗口输入 DeepSeek OCR 密钥并点应用，再按快捷键获取答案。"));
                _logger.WriteDiagnostic("vision_model", "缺少 DeepSeek OCR 密钥");
                return;
            }
            UpdateSuggestion(SuggestionResult.Loading("正在识别截图……"));
            var result = await _deepSeekVisionClient.AnswerExerciseAsync(
                _settingsService.Current,
                _screenOcrKey,
                capture,
                token);
            if (token.IsCancellationRequested || requestSequence != Volatile.Read(ref _visionRequestSequence))
            {
                return;
            }

            _logger.WriteSuggestion(
                "vision_screenshot",
                0,
                stopwatch.ElapsedMilliseconds,
                result);
            UpdateSuggestion(result);
        }
        catch (OperationCanceledException)
        {
            // A newer manual request or the global switch superseded this capture.
        }
        catch (Exception ex)
        {
            _logger.WriteDiagnostic("vision_screenshot", ex.GetType().Name);
            if (requestSequence == Volatile.Read(ref _visionRequestSequence))
            {
                UpdateSuggestion(SuggestionResult.Error("截图识题发生异常", ex.Message));
            }
        }
        finally
        {
            if (requestSequence == Volatile.Read(ref _visionRequestSequence))
            {
                _visionCancellation?.Dispose();
                _visionCancellation = null;
                _visionRequestInProgress = false;
            }
        }
    }

    private void CancelVisionRequest()
    {
        Interlocked.Increment(ref _visionRequestSequence);
        _visionCancellation?.Cancel();
        _visionCancellation?.Dispose();
        _visionCancellation = null;
        _visionRequestInProgress = false;
    }

    private async Task AnalyzeSelectionAsync(bool copyFromNavicat)
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info($"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        string selectedSql;
        if (copyFromNavicat)
        {
            var capture = await _selectionService.CaptureAsync(CancellationToken.None);
            if (!capture.Success)
            {
                UpdateSuggestion(SuggestionResult.Error(capture.Error));
                return;
            }

            selectedSql = capture.Text;
        }
        else
        {
            try
            {
                selectedSql = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            }
            catch (Exception ex)
            {
                UpdateSuggestion(SuggestionResult.Error("读取剪贴板失败", ex.Message));
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedSql))
            {
                UpdateSuggestion(SuggestionResult.Info("请先复制一段 SQL，再点击“分析剪贴板”"));
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Error("请先在设置中保存新的 DeepSeek API Key"));
            return;
        }

        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        var token = _predictionCancellation.Token;
        UpdateSuggestion(SuggestionResult.Loading("正在诊断选中的 SQL……"));
        try
        {
            var fullDocument = _lastCapturedProbe is { IsTruncated: false } completeProbe
                ? completeProbe.Text
                : selectedSql;
            var stopwatch = Stopwatch.StartNew();
            var result = await CompleteSqlRequestAsync(
                PromptTemplates.BuildDiagnosisPrompt(selectedSql, fullDocument),
                () => !token.IsCancellationRequested,
                token,
                2200);
            stopwatch.Stop();
            if (!token.IsCancellationRequested)
            {
                _logger.WriteSuggestion("selection_diagnosis", selectedSql.Length, stopwatch.ElapsedMilliseconds, result);
                UpdateSuggestion(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
    }

    private void UpdateSuggestion(SuggestionResult suggestion)
    {
        if (SameSuggestionPayload(_currentSuggestion, suggestion))
        {
            return;
        }

        if (suggestion.Mode != SuggestionMode.Completion)
        {
            _suggestionAcceptance = null;
        }
        if (suggestion.Mode is not SuggestionMode.ExerciseAnswer and not SuggestionMode.ExerciseContinuation)
        {
            _exerciseSuggestionAcceptance = null;
        }

        _currentSuggestion = suggestion;
        if (IsVisible)
        {
            UpdateMainSuggestionView(suggestion);
            _suggestionViewDirty = false;
        }
        else
        {
            _suggestionViewDirty = true;
        }

        _taskbarWindow.UpdateSuggestion(
            suggestion,
            _settingsService.Current.TaskbarStyle,
            _settingsService.Current.TaskbarWidth,
            _settingsService.Current.TaskbarFloatingHeight);
        _mobileServer.UpdateSuggestion(suggestion, _settingsService.Current);
        RefreshRuntimeIndicators();
    }

    private void UpdateMainSuggestionView(SuggestionResult suggestion)
    {
        var isScreenResult = suggestion.IsScreenResult;
        SuggestionModeText.Text = suggestion.HasCandidates
            ? $"候选 {suggestion.ActiveCandidateIndex + 1}/{suggestion.Candidates.Count}"
            : suggestion.Mode switch
        {
            SuggestionMode.Completion => "补全",
            SuggestionMode.Diagnosis => "诊断",
            SuggestionMode.ExerciseAnswer when isScreenResult => "屏幕回答",
            SuggestionMode.ExerciseAnswer => "题目回答",
            SuggestionMode.ExerciseContinuation => "题目续写",
            SuggestionMode.Error => "错误",
            _ => "状态"
        };
        SuggestionDisplayText.Text = string.IsNullOrWhiteSpace(suggestion.DisplayText) ? "当前没有足够可靠的建议" : suggestion.DisplayText;
        SuggestionExplanationText.Text = string.IsNullOrWhiteSpace(suggestion.Explanation)
            ? suggestion.IsLoading ? "输入变化时，旧请求会自动取消。" : "完整建议也可在悬浮提示条中滚动阅读。"
            : suggestion.Explanation;
        SuggestionInsertLabel.Text = isScreenResult ? "答案或补全内容" : "可插入/替换的 SQL";
        var suggestedText = suggestion.FullSuggestionText;
        SuggestionInsertTextBox.Text = string.IsNullOrWhiteSpace(suggestedText) ? "—" : suggestedText;
        ScreenOcrTextBox.Text = isScreenResult ? suggestion.QuestionText : string.Empty;
        ScreenOcrTab.Visibility = ScreenOcrTextBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssuesText.Text = suggestion.HasCandidates
            ? $"再次按 {_settingsService.Current.CycleCandidatesHotkey} 切换下一个候选；当前为第 {suggestion.ActiveCandidateIndex + 1} 个，共 {suggestion.Candidates.Count} 个。"
            : suggestion.Origin.StartsWith("clipboard_question", StringComparison.Ordinal)
                ? $"剪贴板题目已经回答；按 Ctrl+V 粘贴答案。再次复制一道题后按 {_settingsService.Current.AnswerClipboardQuestionHotkey} 可继续。"
            : string.Equals(suggestion.Origin, "vision_screenshot", StringComparison.Ordinal)
                ? $"切回要识别的窗口，按 {_settingsService.Current.VisionScreenshotHotkey} 可以重新截图。"
            : suggestion.Mode == SuggestionMode.ExerciseAnswer
                ? $"完整题目、答案和解析已同步到手机页；移动到新题后按 {_settingsService.Current.WordExerciseHotkey} 识别并切换，同题再次按下可强制刷新。"
                : suggestion.Mode == SuggestionMode.ExerciseContinuation
                    ? suggestion.SameAsCurrentQuestion
                        ? $"正在跟随当前题目的作答；移动到新题后按 {_settingsService.Current.WordExerciseHotkey} 识别并切换。"
                        : $"当前题目没有自动切换；请在新题内按 {_settingsService.Current.WordExerciseHotkey}。"
                    : suggestion.Issues.Count == 0 ? string.Empty : "问题：" + string.Join("；", suggestion.Issues);
    }

    private static bool SameSuggestionPayload(SuggestionResult left, SuggestionResult right)
    {
        if (left.Mode != right.Mode || left.IsLoading != right.IsLoading ||
            !string.Equals(left.Origin, right.Origin, StringComparison.Ordinal) ||
            left.IsQuestion != right.IsQuestion ||
            left.ContextComplete != right.ContextComplete ||
            left.SameAsCurrentQuestion != right.SameAsCurrentQuestion ||
            left.ActiveCandidateIndex != right.ActiveCandidateIndex ||
            !string.Equals(left.DisplayText, right.DisplayText, StringComparison.Ordinal) ||
            !string.Equals(left.InsertText, right.InsertText, StringComparison.Ordinal) ||
            !string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal) ||
            !string.Equals(left.QuestionType, right.QuestionType, StringComparison.Ordinal) ||
            !string.Equals(left.NeedsMoreContext, right.NeedsMoreContext, StringComparison.Ordinal) ||
            !string.Equals(left.QuestionNumber, right.QuestionNumber, StringComparison.Ordinal) ||
            !string.Equals(left.ActiveSubquestionNumber, right.ActiveSubquestionNumber, StringComparison.Ordinal) ||
            !string.Equals(left.QuestionText, right.QuestionText, StringComparison.Ordinal) ||
            !string.Equals(left.AnswerState, right.AnswerState, StringComparison.Ordinal) ||
            !string.Equals(left.DetectedUserAnswer, right.DetectedUserAnswer, StringComparison.Ordinal) ||
            !string.Equals(left.FullAnswer, right.FullAnswer, StringComparison.Ordinal) ||
            !left.Issues.SequenceEqual(right.Issues, StringComparer.Ordinal) ||
            left.Candidates.Count != right.Candidates.Count)
        {
            return false;
        }

        return left.Candidates.Zip(right.Candidates).All(pair =>
            pair.First.Rank == pair.Second.Rank &&
            string.Equals(pair.First.DisplayText, pair.Second.DisplayText, StringComparison.Ordinal) &&
            string.Equals(pair.First.InsertText, pair.Second.InsertText, StringComparison.Ordinal) &&
            string.Equals(pair.First.Explanation, pair.Second.Explanation, StringComparison.Ordinal));
    }

    private void RememberSuggestionForAcceptance(ProbeResult probe, SuggestionResult result)
    {
        if (result.Mode != SuggestionMode.Completion)
        {
            _suggestionAcceptance = null;
            return;
        }

        var suggestedText = string.IsNullOrWhiteSpace(result.InsertText)
            ? result.DisplayText
            : result.InsertText;
        if (string.IsNullOrEmpty(suggestedText))
        {
            _suggestionAcceptance = null;
            return;
        }

        var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
        _suggestionAcceptance = new SuggestionAcceptanceState(probe.Text, caret, suggestedText);
    }

    private bool IsFollowingCurrentSuggestion(ProbeResult probe)
        => SuggestionAcceptanceMatcher.IsFollowing(_suggestionAcceptance, probe);

    private async Task ApplySettingsAsync()
    {
        var settings = _settingsService.Current;
        _trayIcon.UpdateState(settings);
        ToggleButton.Content = settings.AssistantEnabled ? "暂停全部功能" : "启用 Assistant";
        ScreenShortcutText.Text = !settings.AssistantEnabled
            ? $"已暂停。点「启用 Assistant」或按 {settings.ToggleAssistantHotkey} 恢复屏幕识别。"
            : !settings.VisionEnabled ? "屏幕 OCR 已关闭，可在设置中启用。"
            : $"切到记事本，按 {settings.VisionScreenshotHotkey} 识别。关掉主窗口可收起到托盘。";

        if (!settings.AssistantEnabled)
        {
            StopAssistantActivity();
            _taskbarWindow.Hide();
            await _mobileServer.StopAsync();
            RegisterHotkeys();
            UpdateChannelStatus();
            return;
        }

        _probeTimer.Start();
        var needsKeyboardFallback = settings.KeyboardFallbackEnabled &&
                                    _lastStateKey?.StartsWith(ProbeStatus.NoReadableText.ToString(), StringComparison.Ordinal) == true;
        if (needsKeyboardFallback)
        {
            _keyboardFallbackService.CaptureEnabled = _keyboardFallbackService.Start();
        }
        else
        {
            _keyboardFallbackService.Stop();
        }

        _taskbarWindow.UpdateSuggestion(_currentSuggestion, settings.TaskbarStyle, settings.TaskbarWidth, settings.TaskbarFloatingHeight);
        if (settings.TaskbarPanelEnabled)
        {
            if (!_taskbarWindow.IsVisible) _taskbarWindow.Show();
            _taskbarWindow.Reposition();
        }
        else
        {
            _taskbarWindow.Hide();
        }

        if (settings.MobileServerEnabled)
        {
            if (!await _mobileServer.StartAsync(settings) && !string.IsNullOrWhiteSpace(_mobileServer.LastError))
            {
                _logger.WriteDiagnostic("mobile_server", _mobileServer.LastError);
            }
        }
        else
        {
            await _mobileServer.StopAsync();
        }

        RegisterHotkeys();
        UpdateChannelStatus();
        RefreshRuntimeIndicators();
    }

    private async Task WarmDatabaseCatalogAsync(bool forceRefresh = false, bool reportStatus = true)
    {
        _databaseCatalogCancellation?.Cancel();
        _databaseCatalogCancellation?.Dispose();
        _databaseCatalogCancellation = null;

        var settings = _settingsService.Current;
        if (!settings.AssistantEnabled || !settings.DatabaseConnection.Enabled)
        {
            _databaseCatalog = null;
            _databaseStatus = "数据库增强未开启";
            RefreshRuntimeIndicators();
            return;
        }
        if (string.IsNullOrEmpty(_databasePassword))
        {
            _databaseCatalog = null;
            _databaseStatus = "数据库增强未就绪：未保存数据库密码";
            RefreshRuntimeIndicators();
            return;
        }

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _databaseCatalogCancellation = cancellation;
        try
        {
            if (reportStatus)
            {
                _databaseStatus = "正在预热可访问数据库目录";
                RefreshRuntimeIndicators();
            }
            var catalog = await _databaseService.GetCatalogSnapshotAsync(
                settings.DatabaseConnection,
                _databasePassword,
                cancellationToken,
                forceRefresh);
            cancellationToken.ThrowIfCancellationRequested();
            _databaseCatalog = catalog;
            if (reportStatus)
            {
                _databaseStatus = $"数据库目录已缓存：{catalog.SchemaCount} 库 / {catalog.TableCount} 表" +
                                  (catalog.WasTruncated ? "（已裁剪）" : string.Empty);
                RefreshRuntimeIndicators();
            }
        }
        catch (OperationCanceledException)
        {
            // Settings changed, Assistant stopped, or the application exited.
        }
        catch (Exception ex)
        {
            _logger.WriteDatabaseContext(false, 0, false, ex.Message);
            if (reportStatus)
            {
                _databaseCatalog = null;
                _databaseStatus = $"数据库目录预热失败：{ex.Message}";
                RefreshRuntimeIndicators();
            }
        }
        finally
        {
            if (ReferenceEquals(_databaseCatalogCancellation, cancellation))
            {
                _databaseCatalogCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void StopAssistantActivity()
    {
        _probeTimer.Stop();
        _keyboardFallbackService.Stop();
        _guidedTypingService.Stop("Assistant 已关闭，受控逐字代打已停止。");
        CancelVisionRequest();
        CancelCopyAnswerRequest();
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = null;
        _databaseCatalogCancellation?.Cancel();
        _databaseCatalogCancellation?.Dispose();
        _databaseCatalogCancellation = null;
        _predictionInFlightContext = null;
        ClearWordExerciseSession(cancelRequest: false);
        _authorizedWordDocumentIdentity = null;
        _lastWordDocumentCompletionContext = null;
        _candidateRequestInProgress = false;
        _manualCandidateRequestProbeKey = null;
        FlushPendingCaptureLog(force: true);
        StatusText.Text = "Assistant 已关闭";
        DetailText.Text = $"探测、AI、数据库读取、OCR、代打、任务栏提示和手机服务均已停止；按 {_settingsService.Current.ToggleAssistantHotkey} 重新开启。";
        StatusDot.Fill = Brush(127, 140, 141);
    }

    private void RegisterHotkeys()
    {
        if (new WindowInteropHelper(this).Handle == 0) return;
        var settings = _settingsService.Current;
        var shortcuts = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.ToggleAssistant] = settings.ToggleAssistantHotkey
        };
        if (settings.AssistantEnabled)
        {
            shortcuts[HotkeyAction.SmartAnalyze] = settings.SmartActionHotkey;
            shortcuts[HotkeyAction.CopyFullAnswer] = settings.CopyFullAnswerHotkey;
            shortcuts[HotkeyAction.AnswerClipboardQuestion] = settings.AnswerClipboardQuestionHotkey;
            shortcuts[HotkeyAction.CycleCandidates] = settings.CycleCandidatesHotkey;
            shortcuts[HotkeyAction.ToggleMobile] = settings.ToggleMobileHotkey;
            shortcuts[HotkeyAction.ToggleMobilePrivacy] = settings.ToggleMobilePrivacyHotkey;
            if (settings.GuidedTypingEnabled)
            {
                shortcuts[HotkeyAction.ToggleGuidedTyping] = settings.GuidedTypingHotkey;
            }
            shortcuts[HotkeyAction.PreviousTaskbarPage] = "Win+PageUp";
            shortcuts[HotkeyAction.NextTaskbarPage] = "Win+PageDown";
            if (settings.VisionEnabled)
            {
                shortcuts[HotkeyAction.CaptureVisionExercise] = settings.VisionScreenshotHotkey;
            }
        }

        var errors = _hotkeyService.Register(new WindowInteropHelper(this).Handle, shortcuts);
        HotkeyStatusText.Text = errors.Count == 0
            ? settings.AssistantEnabled
                ? $"全局快捷键 · 总开关 {settings.ToggleAssistantHotkey} · 屏幕 OCR {(settings.VisionEnabled ? settings.VisionScreenshotHotkey : "已关闭")} · 更多快捷键见设置"
                : $"按 {settings.ToggleAssistantHotkey} 启用 Assistant 后，即可使用 OCR。"
            : string.Join("；", errors);
    }

    private void HotkeyService_OnPressed(object? sender, HotkeyAction action)
    {
        if (action is HotkeyAction.CycleCandidates or
            HotkeyAction.SmartAnalyze or
            HotkeyAction.CopyFullAnswer or
            HotkeyAction.AnswerClipboardQuestion)
        {
            CancelVisionRequest();
        }
        if (action is HotkeyAction.CycleCandidates or
            HotkeyAction.SmartAnalyze or
            HotkeyAction.CaptureVisionExercise)
        {
            CancelCopyAnswerRequest();
        }
        switch (action)
        {
            case HotkeyAction.ToggleAssistant:
                SetAssistantEnabledAsync(!_settingsService.Current.AssistantEnabled).Forget();
                break;
            case HotkeyAction.SmartAnalyze: SmartAnalyzeAsync().Forget(); break;
            case HotkeyAction.CopyFullAnswer: CopyFullAnswerAsync().Forget(); break;
            case HotkeyAction.AnswerClipboardQuestion: AnswerClipboardQuestionAsync().Forget(); break;
            case HotkeyAction.ToggleGuidedTyping: ToggleGuidedTyping(); break;
            case HotkeyAction.ToggleMobile: ToggleMobileAsync().Forget(); break;
            case HotkeyAction.ToggleMobilePrivacy: ToggleMobilePrivacy(); break;
            case HotkeyAction.CycleCandidates: CycleCandidatesAsync().Forget(); break;
            case HotkeyAction.AnalyzeSelection: AnalyzeSelectionAsync(true).Forget(); break;
            case HotkeyAction.IdentifyWordExercise: IdentifyWordExerciseAsync().Forget(); break;
            case HotkeyAction.CaptureVisionExercise: CaptureVisionExerciseAsync().Forget(); break;
            case HotkeyAction.PreviousTaskbarPage: _taskbarWindow.ChangePage(-1); break;
            case HotkeyAction.NextTaskbarPage: _taskbarWindow.ChangePage(1); break;
        }
    }

    private void KeyboardFallbackService_OnInputCaptured(object? sender, KeyboardFallbackInput input)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_settingsService.Current.AssistantEnabled)
            {
                return;
            }

            InitializeFallbackSession();
            var text = input.Kind == KeyboardFallbackInputKind.Paste ? SafeClipboardText() : input.Text;
            if (input.Kind == KeyboardFallbackInputKind.Backspace)
            {
                if (_fallbackCaret > 0)
                {
                    _fallbackText.Remove(_fallbackCaret - 1, 1);
                    _fallbackCaret--;
                }
            }
            else if (!string.IsNullOrEmpty(text))
            {
                _fallbackText.Insert(_fallbackCaret, text);
                _fallbackCaret += text.Length;
            }

            if (_fallbackText.Length > 100_000)
            {
                var remove = _fallbackText.Length - 100_000;
                _fallbackText.Remove(0, remove);
                _fallbackCaret = Math.Max(0, _fallbackCaret - remove);
            }

            var snapshot = CreateFallbackProbe();
            SqlTextBox.Text = snapshot.Text;
            LengthText.Text = $"{snapshot.Text.Length:N0} 字符（键盘兜底内存）";
            SchedulePrediction(snapshot, input.Text == "\n");
        });
    }

    private void GuidedTypingService_OnStatusChanged(object? sender, GuidedTypingStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!status.IsActive)
            {
                _guidedTypingService.Stop(status.Message);
            }
            _guidedTypingStatus = status.IsActive
                ? $"代打 {status.ConsumedElements}/{status.TotalElements}"
                : status.IsComplete ? "代打完成" : "代打关闭";
            _guidedTypingLevel = status.IsActive
                ? "ready"
                : status.Message.Contains("无法", StringComparison.Ordinal) ||
                  status.Message.Contains("偏离", StringComparison.Ordinal) ||
                  status.Message.Contains("变化", StringComparison.Ordinal)
                    ? "error"
                    : "off";
            DetailText.Text = status.Message;
            UpdateChannelStatus();
            RefreshRuntimeIndicators();
        });
    }

    private async Task CopyFullAnswerAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info(
                $"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        CancelCopyAnswerRequest();
        CancelVisionRequest();
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = null;
        _predictionInFlightContext = null;
        _guidedTypingService.Stop("正在获取新的完整答案，旧代打会话已停止。");

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var requestSequence = Interlocked.Increment(ref _copyAnswerRequestSequence);
        _copyAnswerCancellation = cancellation;
        UpdateSuggestion(SuggestionResult.Loading("正在获取当前题目的完整答案并准备复制……"));

        try
        {
            SuggestionResult result;
            var source = "screen_ocr";
            var sqlAnswer = false;
            var document = await _documentContextProbeService.CaptureForegroundAsync();
            token.ThrowIfCancellationRequested();
            if (document.Status == DocumentContextStatus.Captured && IsWordDocument(document))
            {
                source = "word_exercise";
                result = await GetWordClipboardAnswerAsync(document, requestSequence, token);
            }
            else
            {
                var probe = await Task.Run(
                    () => _probeService.CaptureForegroundNavicat(forceRefresh: true),
                    token);
                token.ThrowIfCancellationRequested();
                if (probe.Status == ProbeStatus.Captured && !probe.IsTruncated)
                {
                    source = "navicat_sql";
                    sqlAnswer = true;
                    result = await GetNavicatClipboardAnswerAsync(probe, requestSequence, token);
                }
                else
                {
                    result = await GetVisionClipboardAnswerAsync(requestSequence, token);
                }
            }

            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            if (result.Mode == SuggestionMode.Error)
            {
                UpdateSuggestion(result);
                _logger.WriteClipboardAnswer(source, "无可用答案", 0, false);
                return;
            }

            var answer = ClipboardAnswerService.Select(result, sqlAnswer);
            if (answer is null)
            {
                if (source == "screen_ocr" && !string.IsNullOrWhiteSpace(result.QuestionText))
                {
                    UpdateSuggestion(result with { DisplayText = "已识别原文，未生成可复制的答案" });
                    _logger.WriteClipboardAnswer(source, "仅识别原文", 0, false);
                    return;
                }

                UpdateSuggestion(SuggestionResult.Error(
                    "没有可复制的完整答案",
                    string.IsNullOrWhiteSpace(result.Explanation)
                        ? "模型没有返回可靠的完整答案，请把光标放在题目或 SQL TODO 内重试。"
                        : result.Explanation));
                _logger.WriteClipboardAnswer(source, "空答案", 0, false);
                return;
            }

            if (!await TrySetClipboardTextAsync(answer.Text, token))
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "完整答案已生成，但剪贴板正被其他程序占用",
                    "请关闭正在读取剪贴板的程序后再次按复制快捷键。"));
                _logger.WriteClipboardAnswer(source, answer.Kind, answer.CharacterCount, false);
                return;
            }

            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            var copyMessage = $"{answer.Kind}已复制（{answer.CharacterCount:N0} 字符），可按 Ctrl+V 粘贴";
            result = result with
            {
                DisplayText = copyMessage,
                InsertText = answer.Text,
                FullAnswer = answer.Text,
                Explanation = string.IsNullOrWhiteSpace(result.Explanation)
                    ? copyMessage
                    : result.Explanation.Trim() + Environment.NewLine + Environment.NewLine + copyMessage
            };
            UpdateSuggestion(result);
            _logger.WriteClipboardAnswer(source, answer.Kind, answer.CharacterCount, true);
        }
        catch (OperationCanceledException)
        {
            // A newer copy request, context change, global stop, or application exit superseded this request.
        }
        catch (Exception ex)
        {
            if (IsCopyAnswerRequestCurrent(requestSequence))
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "获取完整答案失败",
                    ex.Message));
                _logger.WriteClipboardAnswer("unknown", ex.GetType().Name, 0, false);
            }
        }
        finally
        {
            if (ReferenceEquals(_copyAnswerCancellation, cancellation))
            {
                _copyAnswerCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task AnswerClipboardQuestionAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info(
                $"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        CancelCopyAnswerRequest();
        CancelVisionRequest();
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = null;
        _predictionInFlightContext = null;
        _guidedTypingService.Stop("正在回答剪贴板题目，旧代打会话已停止。");

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var requestSequence = Interlocked.Increment(ref _copyAnswerRequestSequence);
        _copyAnswerCancellation = cancellation;
        UpdateSuggestion(SuggestionResult.Loading("正在识别剪贴板中的题目并准备答案……"));

        try
        {
            var clipboardText = await TryGetClipboardTextAsync(token);
            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            if (clipboardText is null)
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "暂时无法读取剪贴板",
                    "剪贴板正被其他程序占用，请稍后再按剪贴板题目快捷键。"));
                _logger.WriteClipboardAnswer("clipboard_question", "读取失败", 0, false);
                return;
            }
            if (!ClipboardAnswerService.TryNormalizeQuestion(clipboardText, out var question, out var questionError))
            {
                UpdateSuggestion(SuggestionResult.Error("无法识别剪贴板题目", questionError));
                _logger.WriteClipboardAnswer("clipboard_question", "无有效题目", 0, false);
                return;
            }
            if (!_settingsService.Current.AiEnabled || string.IsNullOrWhiteSpace(_apiKey))
            {
                UpdateSuggestion(SuggestionResult.Error("请先开启 AI 并保存 DeepSeek API Key"));
                _logger.WriteClipboardAnswer("clipboard_question", "AI 未配置", 0, false);
                return;
            }

            var result = await CompleteSqlRequestAsync(
                PromptTemplates.BuildClipboardQuestionAnswerPrompt(question),
                () => IsCopyAnswerRequestCurrent(requestSequence),
                token,
                maxTokens: 4000,
                temperature: 0.1,
                systemPrompt: PromptTemplates.BuildClipboardQuestionSystemPrompt(_settingsService.Current));
            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            if (result.Mode == SuggestionMode.Error)
            {
                UpdateSuggestion(result);
                _logger.WriteClipboardAnswer("clipboard_question", "模型失败", 0, false);
                return;
            }
            if (!result.IsQuestion)
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "剪贴板内容不像 SQL 或数据库题目",
                    string.IsNullOrWhiteSpace(result.Explanation)
                        ? "请复制完整题干后重试。"
                        : result.Explanation));
                _logger.WriteClipboardAnswer("clipboard_question", "非数据库题目", 0, false);
                return;
            }

            var answer = ClipboardAnswerService.Select(result, sqlAnswer: false);
            if (answer is null)
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "没有生成可复制的完整答案",
                    string.IsNullOrWhiteSpace(result.Explanation)
                        ? "请确认剪贴板中包含完整题干。"
                        : result.Explanation));
                _logger.WriteClipboardAnswer("clipboard_question", "空答案", 0, false);
                return;
            }

            var currentClipboardText = await TryGetClipboardTextAsync(token);
            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            if (currentClipboardText is null)
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "答案已生成，但暂时无法写入剪贴板",
                    "剪贴板正被其他程序占用，原题没有被覆盖。"));
                _logger.WriteClipboardAnswer("clipboard_question", answer.Kind, answer.CharacterCount, false);
                return;
            }
            if (!string.Equals(currentClipboardText.Trim(), question, StringComparison.Ordinal))
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "剪贴板内容已变化，未覆盖新内容",
                    "回答期间检测到你复制了其他内容；请确认后重新触发。"));
                _logger.WriteClipboardAnswer("clipboard_question", answer.Kind, answer.CharacterCount, false);
                return;
            }
            if (!await TrySetClipboardTextAsync(answer.Text, token))
            {
                UpdateSuggestion(SuggestionResult.Error(
                    "答案已生成，但剪贴板正被其他程序占用",
                    "原题没有被覆盖，请稍后再次按剪贴板题目快捷键。"));
                _logger.WriteClipboardAnswer("clipboard_question", answer.Kind, answer.CharacterCount, false);
                return;
            }

            EnsureCopyAnswerRequestCurrent(requestSequence, token);
            var copyMessage = $"{answer.Kind}已覆盖剪贴板原题（{answer.CharacterCount:N0} 字符），可按 Ctrl+V 粘贴";
            var origin = string.Equals(result.Origin, "database_grounded", StringComparison.Ordinal)
                ? "clipboard_question_database_grounded"
                : "clipboard_question";
            result = result with
            {
                Origin = origin,
                DisplayText = copyMessage,
                InsertText = answer.Text,
                FullAnswer = answer.Text,
                QuestionText = string.Empty,
                DetectedUserAnswer = string.Empty,
                Explanation = string.IsNullOrWhiteSpace(result.Explanation)
                    ? copyMessage
                    : result.Explanation.Trim() + Environment.NewLine + Environment.NewLine + copyMessage
            };
            UpdateSuggestion(result);
            _logger.WriteClipboardAnswer("clipboard_question", answer.Kind, answer.CharacterCount, true);
        }
        catch (OperationCanceledException)
        {
            // A newer answer request, global stop, or application exit superseded this request.
        }
        catch (Exception ex)
        {
            if (IsCopyAnswerRequestCurrent(requestSequence))
            {
                UpdateSuggestion(SuggestionResult.Error("回答剪贴板题目失败", ex.Message));
                _logger.WriteClipboardAnswer("clipboard_question", ex.GetType().Name, 0, false);
            }
        }
        finally
        {
            if (ReferenceEquals(_copyAnswerCancellation, cancellation))
            {
                _copyAnswerCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task<SuggestionResult> GetNavicatClipboardAnswerAsync(
        ProbeResult probe,
        long requestSequence,
        CancellationToken cancellationToken)
    {
        if (!_settingsService.Current.AiEnabled || string.IsNullOrWhiteSpace(_apiKey))
        {
            return SuggestionResult.Error("请先开启 AI 并保存 DeepSeek API Key");
        }

        _lastCapturedProbe = probe;
        var expectedProbeKey = BuildProbeKey(probe);
        var expectedWindow = probe.WindowHandle;
        bool ContextIsCurrent() =>
            IsCopyAnswerRequestCurrent(requestSequence) &&
            IsForegroundRoot(expectedWindow) &&
            _lastCapturedProbe is { } latest &&
            latest.WindowHandle == expectedWindow &&
            string.Equals(BuildProbeKey(latest), expectedProbeKey, StringComparison.Ordinal);

        var result = await CompleteSqlRequestAsync(
            PromptTemplates.BuildFullSqlAnswerPrompt(probe),
            ContextIsCurrent,
            cancellationToken,
            maxTokens: 4000,
            temperature: 0.1);
        EnsureCopyAnswerRequestCurrent(requestSequence, cancellationToken);

        var current = await Task.Run(
            () => _probeService.CaptureForegroundNavicat(forceRefresh: true),
            cancellationToken);
        if (current.Status != ProbeStatus.Captured ||
            current.IsTruncated ||
            current.WindowHandle != expectedWindow ||
            !string.Equals(BuildProbeKey(current), expectedProbeKey, StringComparison.Ordinal))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return result with { Origin = "clipboard_navicat_sql" };
    }

    private async Task<SuggestionResult> GetWordClipboardAnswerAsync(
        DocumentContextResult document,
        long requestSequence,
        CancellationToken cancellationToken)
    {
        if (!document.CaretAvailable || document.CaretOffset is null)
        {
            return SuggestionResult.Error("Word 暂未提供可靠光标", "请把光标放到当前 SQL/数据库题目内重试。");
        }
        if (!_settingsService.Current.AiEnabled || string.IsNullOrWhiteSpace(_apiKey))
        {
            return SuggestionResult.Error("请先开启 AI 并保存 DeepSeek API Key");
        }

        int[] radii =
        [
            WordExerciseContextBuilder.InitialRadius,
            WordExerciseContextBuilder.FirstExpandedRadius,
            WordExerciseContextBuilder.FinalExpandedRadius
        ];
        SuggestionResult? result = null;
        WordExerciseQuestionState? question = null;
        string failure = "未能完整识别当前题目。";
        for (var attempt = 0; attempt < radii.Length; attempt++)
        {
            EnsureCopyAnswerRequestCurrent(requestSequence, cancellationToken);
            var context = WordExerciseContextBuilder.Build(
                document,
                WordExerciseRequestMode.LocateAndAnswer,
                radii[attempt],
                attempt);
            if (context is null)
            {
                return SuggestionResult.Error("Word 当前没有可靠正文或光标");
            }

            result = await _deepSeekClient.CompleteAsync(
                _settingsService.Current,
                _apiKey,
                PromptTemplates.BuildWordExercisePrompt(context),
                cancellationToken,
                maxTokens: 3000,
                temperature: 0.1,
                systemPrompt: PromptTemplates.BuildExerciseSystemPrompt(_settingsService.Current));
            EnsureCopyAnswerRequestCurrent(requestSequence, cancellationToken);
            if (result.Mode == SuggestionMode.Error)
            {
                return result;
            }
            if (!result.IsQuestion && result.ContextComplete)
            {
                return SuggestionResult.Error(
                    "当前光标处不像 SQL/数据库练习题",
                    result.Explanation);
            }

            var needsExpansion = WordExerciseContextBuilder.NeedsExpandedContext(context, result);
            if (!needsExpansion && WordExerciseContextBuilder.TryCreateQuestionState(
                    document,
                    context,
                    result,
                    document.Text,
                    out question,
                    out failure))
            {
                break;
            }
            if (attempt == radii.Length - 1)
            {
                return SuggestionResult.Error("未能定位完整题目", failure);
            }
        }

        if (result is null || question is null)
        {
            return SuggestionResult.Error("未能取得当前题目的完整答案", failure);
        }

        var currentDocument = await _documentContextProbeService.CaptureForegroundAsync();
        if (!WordExerciseContextBuilder.IsSameObservation(document, currentDocument))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var fullAnswer = string.IsNullOrWhiteSpace(result.FullAnswer)
            ? string.IsNullOrWhiteSpace(result.InsertText) ? result.DisplayText : result.InsertText
            : result.FullAnswer;
        result = result with
        {
            Mode = SuggestionMode.ExerciseAnswer,
            Origin = "clipboard_word_exercise",
            FullAnswer = fullAnswer,
            QuestionType = question.QuestionType,
            QuestionNumber = question.QuestionNumber,
            ActiveSubquestionNumber = question.ActiveSubquestionNumber,
            QuestionText = question.QuestionText
        };
        _wordExerciseQuestion = question;
        _lastWordExerciseSuggestion = result;
        _wordExerciseBaselineText = document.Text;
        _wordExercisePendingDocumentIdentity = document.DocumentIdentity;
        return result;
    }

    private async Task<SuggestionResult> GetVisionClipboardAnswerAsync(
        long requestSequence,
        CancellationToken cancellationToken)
    {
        if (!_settingsService.Current.VisionEnabled)
        {
            return SuggestionResult.Error("屏幕 OCR 已在设置中关闭");
        }
        if (string.IsNullOrWhiteSpace(_screenOcrKey))
        {
            return SuggestionResult.Error("请先在主窗口输入 DeepSeek OCR 密钥并点应用");
        }

        var capture = await _visionScreenshotCaptureService.CaptureAsync(
            cancellationToken,
            _settingsService.Current.VisionCaptureScope);
        EnsureCopyAnswerRequestCurrent(requestSequence, cancellationToken);
        _logger.WriteVisionCapture(capture, 0);
        if (!capture.IsCaptured)
        {
            return SuggestionResult.Error("无法截取前台画面", capture.Message);
        }

        var result = await _deepSeekVisionClient.AnswerExerciseAsync(
            _settingsService.Current,
            _screenOcrKey,
            capture,
            cancellationToken);
        EnsureCopyAnswerRequestCurrent(requestSequence, cancellationToken);
        if (!IsForegroundRoot(capture.WindowHandle))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return result.Mode == SuggestionMode.Error ? result : result with { Origin = "clipboard_screen_ocr" };
    }

    private async Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                await Task.Delay(60 * (attempt + 1), cancellationToken);
            }
        }
        return false;
    }

    private static async Task<string?> TryGetClipboardTextAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return System.Windows.Clipboard.ContainsText()
                    ? System.Windows.Clipboard.GetText()
                    : string.Empty;
            }
            catch (ExternalException) when (attempt < 4)
            {
                await Task.Delay(60 * (attempt + 1), cancellationToken);
            }
        }
        return null;
    }

    private bool IsCopyAnswerRequestCurrent(long requestSequence) =>
        _settingsService.Current.AssistantEnabled &&
        requestSequence == Volatile.Read(ref _copyAnswerRequestSequence) &&
        _copyAnswerCancellation is { IsCancellationRequested: false };

    private void EnsureCopyAnswerRequestCurrent(
        long requestSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCopyAnswerRequestCurrent(requestSequence))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void CancelCopyAnswerRequest()
    {
        Interlocked.Increment(ref _copyAnswerRequestSequence);
        _copyAnswerCancellation?.Cancel();
        _copyAnswerCancellation?.Dispose();
        _copyAnswerCancellation = null;
    }

    private static bool IsForegroundRoot(long expectedWindowHandle)
    {
        if (expectedWindowHandle == 0) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero) return false;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        if (root == nint.Zero) root = foreground;
        return root.ToInt64() == expectedWindowHandle;
    }

    private async Task SmartAnalyzeAsync()
    {
        if (!_settingsService.Current.AssistantEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info(
                $"Assistant 已关闭；按 {_settingsService.Current.ToggleAssistantHotkey} 开启"));
            return;
        }

        _guidedTypingService.Stop("开始新的智能分析，旧代打会话已停止。");
        var document = await _documentContextProbeService.CaptureForegroundAsync();
        if (document.Status == DocumentContextStatus.Captured && IsWordDocument(document))
        {
            if (LooksLikeDatabaseExercise(document.Text))
            {
                await IdentifyWordExerciseAsync();
            }
            else
            {
                _authorizedWordDocumentIdentity = document.DocumentIdentity;
                _lastDocumentContext = document;
                _lastWordDocumentCompletionContext = null;
                UpdateSuggestion(SuggestionResult.Loading("已为当前 Word 文档开启 AI 辅助，正在分析光标位置……"));
                ScheduleWordDocumentCompletion(document, immediate: true);
            }
            return;
        }

        var probe = await Task.Run(() => _probeService.CaptureForegroundNavicat(forceRefresh: true));
        if (probe.Status == ProbeStatus.Captured && !probe.IsTruncated)
        {
            _lastCapturedProbe = probe;
            if (probe.SelectionLength is > 0)
            {
                await AnalyzeSelectionAsync(copyFromNavicat: true);
            }
            else
            {
                _lastPredictionContext = null;
                SchedulePrediction(probe, immediate: true);
            }
            return;
        }

        UpdateSuggestion(SuggestionResult.Info(
            $"当前无法取得可靠全文；按 {_settingsService.Current.VisionScreenshotHotkey} 使用屏幕 OCR 保底"));
    }

    private void ToggleGuidedTyping()
    {
        if (_guidedTypingService.IsActive)
        {
            _guidedTypingService.Stop();
            return;
        }

        var settings = _settingsService.Current;
        if (!settings.AssistantEnabled || !settings.GuidedTypingEnabled)
        {
            UpdateSuggestion(SuggestionResult.Info("受控逐字代打已在设置中关闭。"));
            return;
        }
        if (string.IsNullOrEmpty(_currentSuggestion.InsertText))
        {
            UpdateSuggestion(SuggestionResult.Info("当前没有可代打的 InsertText；请先生成答案。"));
            return;
        }
        if (string.Equals(_currentSuggestion.Origin, "vision_screenshot", StringComparison.Ordinal) &&
            !_lastVisionHasReliableCaret)
        {
            UpdateSuggestion(SuggestionResult.Info(
                "本次 OCR 没有可靠文本光标，只能查看答案，不能启动受控逐字代打。"));
            return;
        }

        var foreground = DocumentContextProbeService.ReadForegroundWindow();
        GuidedTypingSession? session = null;
        if (_lastCapturedProbe is { Status: ProbeStatus.Captured, CaretOffset: int navicatCaret } probe &&
            foreground.ProcessName.Contains("navicat", StringComparison.OrdinalIgnoreCase) &&
            foreground.RootWindowHandle.ToInt64() == probe.WindowHandle)
        {
            session = new GuidedTypingSession(
                probe.WindowHandle,
                probe.ProcessName,
                BuildProbeKey(probe),
                _currentSuggestion.InsertText,
                probe.Text,
                navicatCaret);
        }
        else if (_lastDocumentContext is
                 {
                     Status: DocumentContextStatus.Captured,
                     CaretOffset: int wordCaret
                 } document &&
                 IsWordDocument(document) &&
                 foreground.RootWindowHandle.ToInt64() == document.WindowHandle)
        {
            session = new GuidedTypingSession(
                document.WindowHandle,
                document.ProcessName,
                $"{document.DocumentIdentity}|{document.ContentHash}|{wordCaret}",
                _currentSuggestion.InsertText,
                document.Text,
                wordCaret);
        }

        if (session is null)
        {
            UpdateSuggestion(SuggestionResult.Info(
                "代打只支持当前已验证的 Navicat 或 Word 编辑位置；请把光标放回答案位置后重试。"));
            return;
        }

        var status = _guidedTypingService.Start(session);
        if (!status.IsActive)
        {
            UpdateSuggestion(SuggestionResult.Error(status.Message));
        }
    }

    internal static bool LooksLikeDatabaseExercise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sample = text.Length <= 30_000 ? text : text[..30_000];
        var subject = sample.Contains("SQL", StringComparison.OrdinalIgnoreCase) ||
                      sample.Contains("MySQL", StringComparison.OrdinalIgnoreCase) ||
                      sample.Contains("数据库", StringComparison.OrdinalIgnoreCase);
        var explicitExercise = sample.Contains("选择题", StringComparison.OrdinalIgnoreCase) ||
                               sample.Contains("填空题", StringComparison.OrdinalIgnoreCase) ||
                               sample.Contains("简答题", StringComparison.OrdinalIgnoreCase) ||
                               sample.Contains("练习题", StringComparison.OrdinalIgnoreCase) ||
                               sample.Contains("试题", StringComparison.OrdinalIgnoreCase);
        var answerCue = sample.Contains("答：", StringComparison.Ordinal) ||
                        sample.Contains("答案", StringComparison.Ordinal) ||
                        sample.Contains('？') ||
                        sample.Contains('?');
        var numberedQuestions = System.Text.RegularExpressions.Regex.Matches(
            sample,
            @"(?m)^\s*(?:(?:选择|填空|简答)\s*)?(?:第\s*)?\d+[\.、：:]\s*").Count >= 2;
        var titledQuestion = sample.Contains("题目", StringComparison.OrdinalIgnoreCase) && answerCue;
        return subject && (explicitExercise || titledQuestion || numberedQuestions && answerCue);
    }

    private void InitializeFallbackSession()
    {
        if (_fallbackSessionInitialized) return;
        _fallbackSessionInitialized = true;
        _fallbackText.Clear();
        if (_lastCapturedProbe is not null)
        {
            _fallbackText.Append(_lastCapturedProbe.Text);
            _fallbackCaret = Math.Clamp(_lastCapturedProbe.CaretOffset ?? _fallbackText.Length, 0, _fallbackText.Length);
        }
        else
        {
            _fallbackCaret = 0;
        }
    }

    private ProbeResult CreateFallbackProbe()
    {
        var text = _fallbackText.ToString();
        return new ProbeResult
        {
            Status = ProbeStatus.Captured,
            Message = "键盘兜底内存快照",
            Source = "键盘兜底（未写按键日志）",
            Text = text,
            ContentHash = ContentHasher.Sha256(text),
            CaretOffset = _fallbackCaret,
            CaretPrefixLength = _fallbackCaret
        };
    }

    private void MobileServer_OnSettingsChanged(object? sender, MobileSettingsPatch patch)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CancelCopyAnswerRequest();
            var settings = _settingsService.Current;
            if (patch.TaskbarPanelEnabled is bool taskbar) settings.TaskbarPanelEnabled = taskbar;
            if (patch.VisionEnabled is bool visionEnabled) settings.VisionEnabled = visionEnabled;
            if (patch.FallbackEnabled is bool fallbackEnabled) settings.FallbackEnabled = fallbackEnabled;
            if (!string.IsNullOrWhiteSpace(patch.FallbackApiBaseUrl)) settings.FallbackApiBaseUrl = patch.FallbackApiBaseUrl;
            if (!string.IsNullOrWhiteSpace(patch.FallbackModel)) settings.FallbackModel = patch.FallbackModel;
            if (patch.TaskbarWidth is double width) settings.TaskbarWidth = width;
            if (patch.TaskbarFloatingHeight is double height) settings.TaskbarFloatingHeight = height;
            if (!string.IsNullOrWhiteSpace(patch.SmartActionHotkey))
            {
                settings.SmartActionHotkey = patch.SmartActionHotkey;
                settings.WordExerciseHotkey = patch.SmartActionHotkey;
            }
            if (!string.IsNullOrWhiteSpace(patch.CopyFullAnswerHotkey)) settings.CopyFullAnswerHotkey = patch.CopyFullAnswerHotkey;
            if (!string.IsNullOrWhiteSpace(patch.AnswerClipboardQuestionHotkey)) settings.AnswerClipboardQuestionHotkey = patch.AnswerClipboardQuestionHotkey;
            if (!string.IsNullOrWhiteSpace(patch.GuidedTypingHotkey)) settings.GuidedTypingHotkey = patch.GuidedTypingHotkey;
            if (!string.IsNullOrWhiteSpace(patch.ToggleMobileHotkey)) settings.ToggleMobileHotkey = patch.ToggleMobileHotkey;
            if (!string.IsNullOrWhiteSpace(patch.ToggleMobilePrivacyHotkey)) settings.ToggleMobilePrivacyHotkey = patch.ToggleMobilePrivacyHotkey;
            if (!string.IsNullOrWhiteSpace(patch.VisionScreenshotHotkey)) settings.VisionScreenshotHotkey = patch.VisionScreenshotHotkey;
            if (patch.TaskbarFontSize is double tf) settings.TaskbarStyle.FontSize = tf;
            if (patch.TaskbarLineHeight is double tl) settings.TaskbarStyle.LineHeight = tl;
            if (!string.IsNullOrWhiteSpace(patch.TaskbarBackgroundColor)) settings.TaskbarStyle.BackgroundColor = patch.TaskbarBackgroundColor;
            if (!string.IsNullOrWhiteSpace(patch.TaskbarFontColor)) settings.TaskbarStyle.FontColor = patch.TaskbarFontColor;
            if (patch.TaskbarOpacity is double to) settings.TaskbarStyle.BackgroundOpacity = to;
            if (patch.MobileFontSize is double mf) settings.MobileStyle.FontSize = mf;
            if (patch.MobileLineHeight is double ml) settings.MobileStyle.LineHeight = ml;
            if (!string.IsNullOrWhiteSpace(patch.MobileBackgroundColor)) settings.MobileStyle.BackgroundColor = patch.MobileBackgroundColor;
            if (!string.IsNullOrWhiteSpace(patch.MobileFontColor)) settings.MobileStyle.FontColor = patch.MobileFontColor;
            if (patch.MobileOpacity is double mo) settings.MobileStyle.BackgroundOpacity = mo;
            _settingsService.Save(settings);
            ApplySettingsAsync().Forget();
        });
    }

    private async Task SetAssistantEnabledAsync(bool enabled)
    {
        _settingsService.Current.AssistantEnabled = enabled;
        _settingsService.Save(_settingsService.Current);
        if (enabled)
        {
            _lastPredictionContext = null;
            _predictionInFlightContext = null;
            StatusText.Text = "Assistant 正在恢复探测……";
            DetailText.Text = $"Navicat SQL 会恢复自动分析；Word 按 {_settingsService.Current.WordExerciseHotkey} 识题，任意前台应用按 {_settingsService.Current.VisionScreenshotHotkey} 截图识题。";
            StatusDot.Fill = Brush(243, 156, 18);
        }

        await ApplySettingsAsync();
        if (enabled)
        {
            ShowVisionReadyHint();
            await WarmDatabaseCatalogAsync();
            await CaptureNowAsync(forceRefresh: true);
        }
    }

    private void SetTaskbarEnabled(bool enabled)
    {
        _settingsService.Current.TaskbarPanelEnabled = enabled;
        _settingsService.Save(_settingsService.Current);
        ApplySettingsAsync().Forget();
    }

    private async Task ToggleMobileAsync()
    {
        _settingsService.Current.MobileServerEnabled = !_settingsService.Current.MobileServerEnabled;
        _settingsService.Save(_settingsService.Current);
        await ApplySettingsAsync();
    }

    private void ToggleMobilePrivacy()
    {
        var enabled = !_mobileServer.PrivacyMode;
        _mobileServer.PrivacyMode = enabled;
        StatusText.Text = enabled
            ? $"手机页已切换为纯黑屏（按 {_settingsService.Current.ToggleMobilePrivacyHotkey} 恢复正常显示）"
            : "手机页已恢复正常显示";
    }

    private void UpdateChannelStatus()
    {
        var panelStatus = !_taskbarWindow.IsVisible ? "未显示" : _taskbarWindow.CaptureExcluded ? "已排除" : "排除失败";
        CapturePrivacyText.Text = $"Windows 录屏排除 · 主窗口{(_mainWindowCaptureExcluded ? "已排除" : "排除失败")} · 提示条{panelStatus}";
        var settings = _settingsService.Current;
        AssistantStatusText.Text = $"Assistant：{(settings.AssistantEnabled ? "运行中" : "已关闭")}（{settings.ToggleAssistantHotkey}）";
        TaskbarStatusText.Text = settings.AssistantEnabled
            ? $"任务栏内建议：{(settings.TaskbarPanelEnabled ? "开启" : "关闭")}（期望宽度 {settings.TaskbarWidth:0}）"
            : "任务栏内建议：随 Assistant 停止";
        if (_mobileServer.IsRunning)
        {
            var urls = _mobileServer.GetAccessUrls(settings);
            var local = urls.First();
            var lan = urls.FirstOrDefault(url => !url.Contains("127.0.0.1", StringComparison.Ordinal));
            MobileStatusText.Text = lan is null
                ? $"手机页面：已开启，但没有找到可用局域网地址。\n电脑：{local}"
                : $"手机页面：已开启\n手机首选：{lan}\n电脑本机：{local}";
        }
        else
        {
            MobileStatusText.Text = !settings.AssistantEnabled
                ? "手机页面：随 Assistant 停止"
                : settings.MobileServerEnabled
                    ? $"手机页面：启动失败 — {_mobileServer.LastError}"
                    : "手机页面：关闭";
        }
        HotkeyStatusText.ToolTip = $"数据库：{_databaseStatus}；{_guidedTypingStatus}";
    }

    private void RefreshRuntimeIndicators()
    {
        var settings = _settingsService.Current;
        var mainLevel = !settings.AssistantEnabled
            ? "off"
            : _currentSuggestion.IsLoading
                ? "working"
                : _currentSuggestion.Mode == SuggestionMode.Error
                    ? "error"
                    : !string.IsNullOrWhiteSpace(_currentSuggestion.DisplayText)
                        ? "ready"
                        : "standby";
        var databaseLevel = !settings.DatabaseConnection.Enabled
            ? "off"
            : _databaseStatus.StartsWith("正在", StringComparison.Ordinal)
                ? "working"
                : _currentSuggestion.Origin.Contains("database_grounded", StringComparison.Ordinal)
                    ? "ready"
                    : _databaseStatus.Contains("失败", StringComparison.Ordinal) ||
                      _databaseStatus.Contains("未保存", StringComparison.Ordinal)
                        ? "error"
                        : "standby";
        var runtime = new AssistantRuntimeStatus
        {
            MainLevel = mainLevel,
            MainText = !settings.AssistantEnabled
                ? "Assistant 已关闭"
                : _currentSuggestion.IsLoading ? "正在处理" : _currentSuggestion.DisplayText,
            DatabaseLevel = databaseLevel,
            DatabaseText = _databaseStatus,
            GuidedTypingLevel = _guidedTypingLevel,
            GuidedTypingText = _guidedTypingStatus
        };

        StatusDot.Fill = RuntimeBrush(mainLevel);
        StatusDot.ToolTip = $"主流程：{runtime.MainText}";
        DatabaseStatusDot.Fill = RuntimeBrush(databaseLevel);
        DatabaseStatusDot.ToolTip = $"数据库增强：{runtime.DatabaseText}";
        GuidedTypingStatusDot.Fill = RuntimeBrush(_guidedTypingLevel);
        GuidedTypingStatusDot.ToolTip = $"受控代打：{runtime.GuidedTypingText}";
        _taskbarWindow.UpdateRuntimeStatus(runtime);
        _mobileServer.UpdateRuntimeStatus(runtime);
    }

    private static SolidColorBrush RuntimeBrush(string level) => level switch
    {
        "off" => Brush(127, 140, 141),
        "working" => Brush(243, 156, 18),
        "ready" => Brush(39, 174, 96),
        "error" => Brush(192, 57, 43),
        _ => Brush(52, 152, 219)
    };

    private void ApplyScreenOcrKey_OnClick(object sender, RoutedEventArgs e)
    {
        var typed = ScreenOcrKeyBox.Password.Trim();
        var key = typed.Length > 0 ? typed : _screenOcrKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            ScreenOcrKeyStatus.Text = "请先输入 DeepSeek API Key";
            ScreenOcrKeyBox.Focus();
            return;
        }

        bool remember = RememberScreenOcrKeyBox.IsChecked == true;
        try
        {
            if (remember) WindowsCredentialService.SaveScreenOcrKey(key);
            else if (_hasSavedScreenOcrKey) WindowsCredentialService.DeleteScreenOcrKey();
        }
        catch (Win32Exception)
        {
            ScreenOcrKeyStatus.Text = "无法更新 Windows 凭据，请重试";
            return;
        }

        SetScreenOcrKey(key, remember, remember ? "已保存，下次自动载入" : "仅本次运行使用");
    }

    private void DeleteScreenOcrKey_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsCredentialService.DeleteScreenOcrKey();
        }
        catch (Win32Exception)
        {
            ScreenOcrKeyStatus.Text = "删除失败，请重试";
            return;
        }
        SetScreenOcrKey(null, false, "OCR 密钥已删除");
    }

    private void SetScreenOcrKey(string? key, bool remember, string status)
    {
        CancelVisionRequest();
        CancelCopyAnswerRequest();
        _screenOcrKey = key;
        _hasSavedScreenOcrKey = remember;
        RememberScreenOcrKeyBox.IsChecked = remember;
        DeleteScreenOcrKeyButton.IsEnabled = !string.IsNullOrWhiteSpace(key);
        ScreenOcrKeyBox.Clear();
        ScreenOcrKeyStatus.Text = status;
        ShowVisionReadyHint();
    }

    private void SaveFloatingSize(double width, double height)
    {
        var settings = _settingsService.Current with { TaskbarWidth = Math.Round(width), TaskbarFloatingHeight = Math.Round(height) };
        try
        {
            _settingsService.Save(settings);
            _mobileServer.UpdateSuggestion(_currentSuggestion, settings);
            UpdateChannelStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "提示条大小未能保存：" + ex.Message;
        }
    }

    private void OpenSettings() => OpenSettings(showAppearance: false);

    private void OpenSettings(bool showAppearance)
    {
        ShowMainWindow();
        var dialog = new SettingsWindow(
            _settingsService.Current,
            !string.IsNullOrWhiteSpace(_apiKey),
            !string.IsNullOrWhiteSpace(SafeReadZhipuApiKey()),
            !string.IsNullOrEmpty(_databasePassword)) { Owner = this };
        if (showAppearance) dialog.AppearanceTab.IsSelected = true;
        if (dialog.ShowDialog() != true || dialog.UpdatedSettings is null) return;
        try
        {
            if (dialog.ClearApiKey) WindowsCredentialService.DeleteApiKey();
            if (!string.IsNullOrWhiteSpace(dialog.NewApiKey)) WindowsCredentialService.SaveApiKey(dialog.NewApiKey);
            if (dialog.ClearZhipuApiKey) WindowsCredentialService.DeleteZhipuApiKey();
            if (!string.IsNullOrWhiteSpace(dialog.NewZhipuApiKey))
            {
                WindowsCredentialService.SaveZhipuApiKey(dialog.NewZhipuApiKey);
            }
            var databaseProfileId = dialog.UpdatedSettings.DatabaseConnection.ProfileId;
            if (dialog.ClearDatabasePassword)
            {
                WindowsCredentialService.DeleteDatabasePassword(databaseProfileId);
            }
            if (!string.IsNullOrEmpty(dialog.NewDatabasePassword))
            {
                WindowsCredentialService.SaveDatabasePassword(
                    databaseProfileId,
                    dialog.UpdatedSettings.DatabaseConnection.Username,
                    dialog.NewDatabasePassword);
            }
            _apiKey = SafeReadApiKey();
            _databasePassword = SafeReadDatabasePassword(databaseProfileId);
            CancelCopyAnswerRequest();
            _predictionCancellation?.Cancel();
            _lastPredictionContext = null;
            _databaseService.ClearCatalogCache();
            _databaseCatalog = null;
            _settingsService.Save(dialog.UpdatedSettings);
            ApplySettingsAfterSaveAsync().Forget();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "设置保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplySettingsAfterSaveAsync()
    {
        await ApplySettingsAsync();
        if (_settingsService.Current.AssistantEnabled)
        {
            await WarmDatabaseCatalogAsync(forceRefresh: true);
            await CaptureNowAsync(forceRefresh: true);
        }
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        if (_sqlViewDirty)
        {
            if (_lastStateKey?.StartsWith("document|", StringComparison.Ordinal) == true &&
                _lastDocumentContext is not null)
            {
                SqlTextBox.Text = _lastDocumentContext.Text;
                _sqlViewDirty = false;
            }
            else if (_lastCapturedProbe is not null)
            {
                SqlTextBox.Text = _lastCapturedProbe.Text;
                _sqlViewDirty = false;
            }
        }
        if (_suggestionViewDirty)
        {
            UpdateMainSuggestionView(_currentSuggestion);
            _suggestionViewDirty = false;
        }
        Activate();
    }

    private void ToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        SetAssistantEnabledAsync(!_settingsService.Current.AssistantEnabled).Forget();

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void AnalyzeButton_OnClick(object sender, RoutedEventArgs e) => AnalyzeSelectionAsync(false).Forget();

    private void OpenLocalMobilePageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_mobileServer.IsRunning)
        {
            UpdateSuggestion(SuggestionResult.Info("请先开启手机局域网页服务"));
            return;
        }

        var localUrl = _mobileServer.GetAccessUrls(_settingsService.Current).First()
            .Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = localUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateSuggestion(SuggestionResult.Error("无法打开默认浏览器", ex.Message));
        }
    }

    private void CopyPhoneUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_mobileServer.IsRunning)
        {
            UpdateSuggestion(SuggestionResult.Info("请先开启手机局域网页服务"));
            return;
        }

        var url = _mobileServer.GetAccessUrls(_settingsService.Current)
            .FirstOrDefault(candidate => !candidate.Contains("127.0.0.1", StringComparison.Ordinal));
        if (url is null)
        {
            UpdateSuggestion(SuggestionResult.Error("没有找到真实局域网地址"));
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(url);
            _trayIcon.ShowBalloon("手机地址已复制", "请发到手机并确保手机与电脑在同一局域网。若打不开，再点击“修复手机访问”。");
        }
        catch (Exception ex)
        {
            UpdateSuggestion(SuggestionResult.Error("复制手机地址失败", ex.Message));
        }
    }

    private async void FirewallFixButton_OnClick(object sender, RoutedEventArgs e)
    {
        FirewallFixButton.IsEnabled = false;
        FirewallStatusText.Text = "防火墙：等待 Windows 管理员权限确认……";
        var result = await FirewallService.EnsureRuleAsync(_settingsService.Current.MobilePort);
        FirewallStatusText.Text = $"防火墙：{result.Message}";
        FirewallStatusText.Foreground = result.Success ? Brush(39, 174, 96) : Brush(192, 57, 43);
        FirewallFixButton.IsEnabled = true;
    }

    private async Task RefreshFirewallStatusAsync()
    {
        try
        {
            var enabled = await FirewallService.IsRuleEnabledAsync(_settingsService.Current.MobilePort);
            FirewallStatusText.Text = enabled
                ? "防火墙：已允许同一局域网访问"
                : "防火墙：尚未放行；手机打不开时点击“修复手机访问”";
            FirewallStatusText.Foreground = enabled ? Brush(39, 174, 96) : Brush(192, 57, 43);
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = $"防火墙检测失败：{ex.Message}";
        }
    }

    private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logger.LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = _logger.LogDirectory, UseShellExecute = true });
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private async Task ExitAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _probeTimer.Stop();
        _settingsService.Save(_settingsService.Current);
        await _mobileServer.StopAsync();
        Close();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        FlushPendingCaptureLog(force: true);
        CancelVisionRequest();
        CancelCopyAnswerRequest();
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _databaseCatalogCancellation?.Cancel();
        _databaseCatalogCancellation?.Dispose();
        _hotkeyService.Dispose();
        _keyboardFallbackService.Dispose();
        _guidedTypingService.Dispose();
        _documentContextProbeService.Dispose();
        _deepSeekClient.Dispose();
        _deepSeekVisionClient.Dispose();
        _trayIcon.Dispose();
        _taskbarWindow.Close();
        _logger.Dispose();
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));

    private static string? SafeReadApiKey()
    {
        try { return WindowsCredentialService.ReadApiKey(); }
        catch { return null; }
    }

    private static string? SafeReadZhipuApiKey()
    {
        try { return WindowsCredentialService.ReadZhipuApiKey(); }
        catch { return null; }
    }

    private static string SafeClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }

    private static string? SafeReadDatabasePassword(string profileId)
    {
        try { return WindowsCredentialService.ReadDatabasePassword(profileId); }
        catch { return null; }
    }
}

internal static class TaskExtensions
{
    public static async void Forget(this Task task)
    {
        try { await task; }
        catch { /* UI-triggered background work reports its own state. */ }
    }
}
