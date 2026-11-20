using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class ProbeLogger : IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Channel<LogEntry> _entries;
    private readonly Task _writerTask;
    private long _sequence;
    private int _droppedEntries;
    private bool _disposed;

    public ProbeLogger(string logDirectory)
    {
        LogDirectory = logDirectory;
        Directory.CreateDirectory(LogDirectory);
        LogPath = Path.Combine(LogDirectory, $"navicat-probe-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());

        _entries = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriterLoopAsync);

        File.WriteAllText(Path.Combine(LogDirectory, "latest-log.txt"), LogPath, new UTF8Encoding(false));
    }

    public string LogDirectory { get; }

    public string LogPath { get; }

    public void WriteSessionStarted() => Write("session_started", new
    {
        app = "Assistant",
        version = typeof(ProbeLogger).Assembly.GetName().Version?.ToString(),
        operatingSystem = Environment.OSVersion.ToString(),
        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        fullSqlLogging = true,
        loggingMode = "输入停顿后写入最新全文；后台有界队列；控件诊断不重复保存编辑器全文",
        note = "验证版会把成功捕获的 SQL 全文写入日志。"
    }, flush: true);

    public void WriteCapture(ProbeResult result) => Write("capture", new
    {
        result.Timestamp,
        result.Status,
        result.Message,
        result.ProcessName,
        result.ProcessId,
        result.WindowHandle,
        result.WindowTitle,
        result.Source,
        result.ElementDescription,
        result.Text,
        result.ContentHash,
        result.CaretLine,
        result.CaretColumn,
        result.CaretOffset,
        result.CaretPrefixLength,
        result.SelectionLength,
        result.IsTruncated,
        result.Diagnostics,
        Elements = result.Elements.Select(element => new
        {
            element.Depth,
            element.ControlType,
            element.ClassName,
            element.AutomationId,
            element.FrameworkId,
            Name = CompactElementName(element.Name),
            element.Patterns
        }).ToArray()
    }, flush: true);

    public void WriteState(ProbeResult result) => Write("state", new
    {
        result.Timestamp,
        result.Status,
        result.Message,
        result.ProcessName,
        result.ProcessId,
        result.WindowHandle,
        result.WindowTitle,
        result.Source,
        result.CaretLine,
        result.CaretColumn,
        result.CaretOffset,
        result.IsTruncated,
        result.Diagnostics
    });

    public void WriteDocumentContext(DocumentContextResult result) => Write("document_context", new
    {
        result.Timestamp,
        result.Status,
        result.ApplicationName,
        result.ProcessName,
        result.ProcessId,
        result.WindowHandle,
        result.WindowTitle,
        result.DocumentName,
        result.DocumentIdentity,
        result.Source,
        textLength = result.Text.Length,
        result.ContentHash,
        result.CaretOffset,
        result.CaretAvailable,
        contextBefore = LimitDocumentLogText(result.ContextBefore),
        contextAfter = LimitDocumentLogText(result.ContextAfter),
        result.SelectionAvailable,
        result.HasSelection,
        selectedText = LimitDocumentLogText(result.SelectedText),
        selectedTextLength = result.SelectedText.Length,
        result.IsPartial,
        result.Message,
        result.Diagnostics
    });

    public void WriteConfiguration(AppSettings settings) => Write("configuration", new
    {
        settings.AssistantEnabled,
        settings.AiEnabled,
        settings.ApiBaseUrl,
        settings.Model,
        settings.VisionEnabled,
        visionModel = DeepSeekVisionClient.Model,
        settings.PredictionDebounceMilliseconds,
        settings.PredictionMinimumIntervalMilliseconds,
        settings.TaskbarPanelEnabled,
        settings.TaskbarWidth,
        settings.MobileServerEnabled,
        settings.KeyboardFallbackEnabled,
        settings.GuidedTypingEnabled,
        databaseEnabled = settings.DatabaseConnection.Enabled,
        databaseProfileIdHash = ContentHasher.Sha256(settings.DatabaseConnection.ProfileId),
        settings.SmartActionHotkey,
        settings.GuidedTypingHotkey,
        settings.ToggleMobileHotkey,
        settings.WordExerciseHotkey,
        settings.VisionScreenshotHotkey,
        note = "API Key、数据库连接地址/用户名/密码、手机访问令牌、自定义全局提示词和截图内容不会写入探测日志。"
    });

    public void WriteVisionCapture(VisionCaptureSnapshot capture, long elapsedMilliseconds) => Write(
        "vision_capture",
        new
        {
            capture.Timestamp,
            capture.Status,
            capture.Message,
            capture.ProcessName,
            capture.ProcessId,
            capture.WindowHandle,
            capture.WindowBounds,
            capture.CaretScreenBounds,
            capture.CaretSource,
            capture.CaretConfidence,
            capture.DetailBounds,
            capture.DetailMarkerX,
            capture.DetailMarkerY,
            fullImageBytes = capture.FullImageBytes.Length,
            detailImageBytes = capture.DetailImageBytes.Length,
            capture.CaptureHash,
            elapsedMilliseconds,
            note = "截图和 Base64 仅保存在内存中，不写入日志。"
        }, flush: true);

    public void WriteSuggestion(
        string trigger,
        int contextCharacters,
        long elapsedMilliseconds,
        SuggestionResult result,
        WordExerciseRequestContext? wordContext = null)
    {
        var isExercise = result.Mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation;
        var isScreenOcr = trigger == "vision_screenshot";
        Write("suggestion", new
        {
            trigger,
            contextCharacters,
            elapsedMilliseconds,
            result.Mode,
            result.Origin,
            displayText = isScreenOcr ? null : result.DisplayText,
            insertText = isExercise || isScreenOcr ? null : result.InsertText,
            explanation = isExercise || isScreenOcr ? null : result.Explanation,
            result.QuestionType,
            result.IsQuestion,
            result.ContextComplete,
            result.NeedsMoreContext,
            result.SameAsCurrentQuestion,
            result.QuestionNumber,
            result.ActiveSubquestionNumber,
            result.AnswerState,
            questionTextCharacters = result.QuestionText.Length,
            detectedUserAnswerCharacters = result.DetectedUserAnswer.Length,
            fullAnswerCharacters = result.FullAnswer.Length,
            insertTextCharacters = result.InsertText.Length,
            result.Issues,
            result.Confidence,
            result.ActiveCandidateIndex,
            result.Candidates,
            result.PromptTokens,
            result.PromptCacheHitTokens,
            result.PromptCacheMissTokens,
            result.CompletionTokens,
            wordRequestMode = wordContext?.Mode.ToString(),
            wordWindowStart = wordContext?.WindowStart,
            wordWindowEnd = wordContext?.WindowEnd,
            wordRadius = wordContext?.Radius,
            wordExpansionLevel = wordContext?.ExpansionLevel
        });
    }

    public void WriteHealthSnapshot()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var gcInfo = GC.GetGCMemoryInfo();
            Write("health", new
            {
                workingSetBytes = process.WorkingSet64,
                privateMemoryBytes = process.PrivateMemorySize64,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                gcHeapSizeBytes = gcInfo.HeapSizeBytes,
                gcFragmentedBytes = gcInfo.FragmentedBytes,
                handles = process.HandleCount,
                threads = process.Threads.Count,
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2),
                queuedLogEntries = _entries.Reader.CanCount ? _entries.Reader.Count : -1,
                droppedLogEntries = Volatile.Read(ref _droppedEntries)
            }, flush: true);
        }
        catch
        {
            // Resource diagnostics are best effort only.
        }
    }

    public void WriteDiagnostic(string component, string message) => Write("diagnostic", new
    {
        component,
        message
    }, flush: component == "vision_hotkey");

    public void WriteDatabaseContext(bool success, int rowCount, bool truncated, string message) => Write(
        "database_context",
        new
        {
            success,
            rowCount,
            truncated,
            outcome = success ? "success" : "failed",
            messageCharacters = message?.Length ?? 0,
            note = "不记录数据库密码、连接字符串、地址、用户名、异常正文、查询文本、Schema 或返回行。"
        });

    public void WriteClipboardAnswer(string source, string kind, int characterCount, bool success) => Write(
        "clipboard_answer",
        new
        {
            source,
            kind,
            characterCount,
            success,
            note = "只记录来源、答案类型、字符数和成功状态，不记录题目、SQL 或剪贴板正文。"
        });

    private void Write(string eventType, object data, bool flush = false)
    {
        if (_disposed)
        {
            return;
        }

        var entry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.Now,
            eventType,
            data,
            flush);
        if (!_entries.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _droppedEntries);
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            using var writer = new StreamWriter(
                new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 16_384, useAsync: true),
                new UTF8Encoding(false),
                16_384);
            var unflushed = 0;
            await foreach (var entry in _entries.Reader.ReadAllAsync())
            {
                var record = new
                {
                    sequence = entry.Sequence,
                    timestamp = entry.Timestamp,
                    eventType = entry.EventType,
                    data = entry.Data
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(record, _jsonOptions));
                unflushed++;
                if (entry.Flush || unflushed >= 10)
                {
                    await writer.FlushAsync();
                    unflushed = 0;
                }
            }

            await writer.FlushAsync();
        }
        catch
        {
            // Logging must never bring down the SQL assistant.
        }
    }

    private static string CompactElementName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        if (name.Length <= 240 && !name.Contains('\r') && !name.Contains('\n'))
        {
            return name;
        }

        var firstLine = name.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (firstLine.Length > 160)
        {
            firstLine = firstLine[..157] + "…";
        }

        return $"[已省略可能重复的编辑器全文，共 {name.Length:N0} 字符] {firstLine}".TrimEnd();
    }

    private static string LimitDocumentLogText(string text)
    {
        const int maximumCharacters = 320;
        if (text.Length <= maximumCharacters)
        {
            return text;
        }

        var half = maximumCharacters / 2;
        return text[..half] + "…" + text[^half..];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var finalEntry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.Now,
            "session_ended",
            new
            {
                droppedLogEntries = Volatile.Read(ref _droppedEntries)
            },
            true);
        if (!_entries.Writer.TryWrite(finalEntry))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                _entries.Writer.WriteAsync(finalEntry, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // The writer may already be unavailable; shutdown must still finish.
            }
        }
        _disposed = true;
        _entries.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Process shutdown should continue even if the log share is unavailable.
        }
    }

    private sealed record LogEntry(
        long Sequence,
        DateTimeOffset Timestamp,
        string EventType,
        object Data,
        bool Flush);
}
