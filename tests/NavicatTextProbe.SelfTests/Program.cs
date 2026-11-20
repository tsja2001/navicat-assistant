using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using NavicatTextProbe;
using NavicatTextProbe.Native;
using NavicatTextProbe.Models;
using NavicatTextProbe.Services;

if (args.Contains("--verify-screen-capture"))
    return ScreenCaptureVerification.Run(args.Contains("--verify-notepad-capture"));

var failures = new List<string>();

void Check(string name, bool condition)
{
    if (!condition) failures.Add(name);
}

var baseDocument = "SELECT * FR";
var state = new SuggestionAcceptanceState(baseDocument, baseDocument.Length, "OM users;");
ProbeResult Probe(string text, int caret) => new() { Text = text, CaretOffset = caret };

Check("document preview compacts line breaks and Word control markers",
    DocumentContextFormatter.Compact("  第一段\r\n\t第二段\a  ") == "第一段 第二段");
var documentPreview = DocumentContextFormatter.BuildTaskbarText(new DocumentContextResult
{
    Status = DocumentContextStatus.Captured,
    ApplicationName = "Microsoft Word",
    ContextBefore = "光标前面的文字",
    ContextAfter = "光标后面的文字",
    CaretAvailable = true,
    SelectedText = "被选中的文字",
    SelectionAvailable = true,
    HasSelection = true
});
Check("document preview contains the three requested labeled values",
    documentPreview.Contains("1.当前软件：Microsoft Word", StringComparison.Ordinal) &&
    documentPreview.Contains("2.光标内容：光标前面的文字▏光标后面的文字", StringComparison.Ordinal) &&
    documentPreview.Contains("3.选中内容：被选中的文字", StringComparison.Ordinal));
Check("document preview reports an unavailable browser selection honestly",
    DocumentContextFormatter.BuildTaskbarText(new DocumentContextResult
    {
        Status = DocumentContextStatus.Captured,
        ApplicationName = "钉钉云文档（Chrome）"
    }).Contains("（未识别到选区）", StringComparison.Ordinal));
Check("DingTalk browser matching requires both a browser and a document title",
    DocumentContextProbeService.IsDingTalkWebWindow("chrome", "测试 · 钉钉文档 - Google Chrome") &&
    !DocumentContextProbeService.IsDingTalkWebWindow("chrome", "Gmail - Google Chrome") &&
    !DocumentContextProbeService.IsDingTalkWebWindow("notepad", "测试 · 钉钉文档"));

Check("read-only SQL validator accepts SELECT and WITH queries",
    SafeMySqlQueryValidator.TryValidate("SELECT id FROM users WHERE status = 'UPDATE' LIMIT 5;", out var safeSelect, out _) &&
    safeSelect.EndsWith("LIMIT 5", StringComparison.Ordinal) &&
    SafeMySqlQueryValidator.TryValidate("WITH recent AS (SELECT id FROM orders) SELECT id FROM recent", out _, out _) &&
    SafeMySqlQueryValidator.TryValidate("SHOW CREATE TABLE sales.orders", out _, out _));
Check("read-only SQL validator rejects writes, multiple statements, files and executable comments",
    !SafeMySqlQueryValidator.TryValidate("UPDATE users SET enabled = 1", out _, out _) &&
    !SafeMySqlQueryValidator.TryValidate("SELECT 1; DELETE FROM users", out _, out _) &&
    !SafeMySqlQueryValidator.TryValidate("SELECT * INTO OUTFILE '/tmp/x' FROM users", out _, out _) &&
    !SafeMySqlQueryValidator.TryValidate("SELECT 1 /*!50000; DELETE FROM users */", out _, out _) &&
    !SafeMySqlQueryValidator.TryValidate("SHOW CREATE USER admin", out _, out _) &&
    !SafeMySqlQueryValidator.TryValidate(new string('x', 20_001), out _, out _));

var databaseCatalog = new DatabaseCatalogSnapshot
{
    Summary = "SCHEMA `sales`: `orders`(rows≈100)\nTABLE `sales`.`orders`: `id` bigint PRI, `amount` decimal(10,2)",
    SchemaCount = 1,
    TableCount = 1,
    ColumnCount = 2
};
var databaseToolSystemPrompt = PromptTemplates.BuildDatabaseToolSystemPrompt(new AppSettings(), databaseCatalog);
Check("database catalog is supplied on every database-enabled model turn without a magic directive",
    databaseToolSystemPrompt.Contains("query_mysql_readonly", StringComparison.Ordinal) &&
    databaseToolSystemPrompt.Contains("`sales`.`orders`", StringComparison.Ordinal) &&
    !databaseToolSystemPrompt.Contains("@assistant-db", StringComparison.Ordinal));

var noDatabaseQueryPayload = JsonSerializer.Serialize(new
{
    mode = "completion",
    display_text = "补全 WHERE 条件",
    insert_text = " WHERE status = 1",
    explanation = "目录已经足够，无需实时数据。",
    issues = Array.Empty<string>(),
    confidence = 0.9
});
var noDatabaseQueryEnvelope = JsonSerializer.Serialize(new
{
    choices = new[] { new { message = new { content = noDatabaseQueryPayload } } },
    usage = new { prompt_tokens = 20, completion_tokens = 10 }
});
var noDatabaseQueryHandler = new SequenceHttpMessageHandler(noDatabaseQueryEnvelope);
var unexpectedDatabaseExecution = false;
DatabaseAssistedCompletionResult noDatabaseQueryResult;
using (var databaseToolClient = new DeepSeekClient(noDatabaseQueryHandler))
{
    noDatabaseQueryResult = await databaseToolClient.CompleteWithDatabaseToolsAsync(
        new AppSettings { ApiBaseUrl = "https://deepseek-self-test.invalid", Model = "self-test" },
        "self-test-key",
        "SELECT * FROM orders<CURSOR>",
        databaseCatalog,
        (_, _, _) =>
        {
            unexpectedDatabaseExecution = true;
            return Task.FromResult(DatabaseContextResult.Failure("不应执行"));
        },
        CancellationToken.None);
}
Check("AI can finish from the cached catalog without querying live data",
    !unexpectedDatabaseExecution &&
    noDatabaseQueryResult.ToolCallCount == 0 &&
    noDatabaseQueryResult.Suggestion.Origin == "database_catalog" &&
    noDatabaseQueryHandler.RequestBodies.Single().Contains("query_mysql_readonly", StringComparison.Ordinal) &&
    noDatabaseQueryHandler.RequestBodies.Single().Contains("tool_choice\":\"auto", StringComparison.Ordinal));

var toolArguments = JsonSerializer.Serialize(new
{
    query = "SELECT COUNT(*) AS total FROM sales.orders",
    purpose = "当前 SQL 需要实时订单总数"
});
var databaseToolCallEnvelope = JsonSerializer.Serialize(new
{
    choices = new[]
    {
        new
        {
            message = new
            {
                content = (string?)null,
                tool_calls = new[]
                {
                    new
                    {
                        id = "call_database_1",
                        type = "function",
                        function = new { name = "query_mysql_readonly", arguments = toolArguments }
                    }
                }
            }
        }
    },
    usage = new { prompt_tokens = 30, completion_tokens = 8 }
});
var databaseFinalPayload = JsonSerializer.Serialize(new
{
    mode = "completion",
    display_text = "使用实时总数",
    insert_text = "SELECT 42 AS total_orders;",
    explanation = "已使用只读工具结果。",
    issues = Array.Empty<string>(),
    confidence = 0.95
});
var databaseFinalEnvelope = JsonSerializer.Serialize(new
{
    choices = new[] { new { message = new { content = databaseFinalPayload } } },
    usage = new { prompt_tokens = 40, completion_tokens = 12 }
});
var databaseToolHandler = new SequenceHttpMessageHandler(databaseToolCallEnvelope, databaseFinalEnvelope);
var executedDatabaseQueries = new List<DatabaseToolRequest>();
DatabaseAssistedCompletionResult databaseToolResult;
using (var databaseToolClient = new DeepSeekClient(databaseToolHandler))
{
    databaseToolResult = await databaseToolClient.CompleteWithDatabaseToolsAsync(
        new AppSettings { ApiBaseUrl = "https://deepseek-self-test.invalid", Model = "self-test" },
        "self-test-key",
        "统计订单<CURSOR>",
        databaseCatalog,
        (request, _, _) =>
        {
            executedDatabaseQueries.Add(request);
            return Task.FromResult(new DatabaseContextResult
            {
                IsSuccess = true,
                Message = "只读查询成功",
                Query = request.Sql,
                RowsSummary = "total\n---\n42",
                RowCount = 1
            });
        },
        CancellationToken.None);
}
Check("DeepSeek tool calls are executed and returned through an official-style tool message loop",
    executedDatabaseQueries.Count == 1 &&
    executedDatabaseQueries[0].Sql.StartsWith("SELECT COUNT", StringComparison.Ordinal) &&
    databaseToolResult.ToolCallCount == 1 &&
    databaseToolResult.SuccessfulQueryCount == 1 &&
    databaseToolResult.Suggestion.Origin == "database_grounded" &&
    databaseToolResult.Suggestion.PromptTokens == 70 &&
    databaseToolHandler.RequestBodies.Count == 2 &&
    databaseToolHandler.RequestBodies[1].Contains("tool_call_id\":\"call_database_1", StringComparison.Ordinal) &&
    databaseToolHandler.RequestBodies[1].Contains("untrusted_database_result", StringComparison.Ordinal));

var threeToolCallsEnvelope = JsonSerializer.Serialize(new
{
    choices = new[]
    {
        new
        {
            message = new
            {
                content = (string?)null,
                tool_calls = Enumerable.Range(1, 3).Select(index => new
                {
                    id = $"call_limit_{index}",
                    type = "function",
                    function = new { name = "query_mysql_readonly", arguments = toolArguments }
                }).ToArray()
            }
        }
    }
});
var toolLimitHandler = new SequenceHttpMessageHandler(threeToolCallsEnvelope, databaseFinalEnvelope);
var toolLimitExecutions = 0;
DatabaseAssistedCompletionResult toolLimitResult;
using (var databaseToolClient = new DeepSeekClient(toolLimitHandler))
{
    toolLimitResult = await databaseToolClient.CompleteWithDatabaseToolsAsync(
        new AppSettings { ApiBaseUrl = "https://deepseek-self-test.invalid", Model = "self-test" },
        "self-test-key",
        "需要多个事实<CURSOR>",
        databaseCatalog,
        (request, _, _) =>
        {
            toolLimitExecutions++;
            return Task.FromResult(new DatabaseContextResult
            {
                IsSuccess = true,
                Query = request.Sql,
                RowsSummary = "value\n---\n1",
                RowCount = 1
            });
        },
        CancellationToken.None);
}
Check("database tool execution is hard-capped at two calls per AI request",
    toolLimitExecutions == 2 &&
    toolLimitResult.ToolCallCount == 2 &&
    toolLimitHandler.RequestBodies[1].Contains("tool_call_id\":\"call_limit_3", StringComparison.Ordinal) &&
    toolLimitHandler.RequestBodies[1].Contains("ok\\u0022:false", StringComparison.Ordinal) &&
    toolLimitHandler.RequestBodies[1].Contains("tool_choice\":\"none", StringComparison.Ordinal));

var fallbackPayload = JsonSerializer.Serialize(new
{
    mode = "completion",
    display_text = "智谱 GLM 补全",
    insert_text = " WHERE status = 1",
    explanation = "冗余接管后的解释。",
    issues = Array.Empty<string>(),
    confidence = 0.85
});
var fallbackEnvelope = JsonSerializer.Serialize(new
{
    choices = new[] { new { message = new { content = fallbackPayload } } },
    usage = new { prompt_tokens = 30, completion_tokens = 12 }
});
var fallbackSettings = new AppSettings
{
    ApiBaseUrl = "https://deepseek-self-test.invalid",
    Model = "self-test-deepseek",
    FallbackEnabled = true,
    FallbackApiBaseUrl = "https://glm-self-test.invalid/api/paas/v4",
    FallbackModel = "glm-5.2"
};

var fallbackTimeoutHandler = new FallbackHttpMessageHandler();
fallbackTimeoutHandler.QueueTimeout();
fallbackTimeoutHandler.QueueResponse(fallbackEnvelope);
SuggestionResult fallbackTimeoutResult;
using (var fallbackTimeoutClient = new DeepSeekClient(fallbackTimeoutHandler))
{
    fallbackTimeoutResult = await fallbackTimeoutClient.CompleteAsync(
        fallbackSettings,
        "deepseek-key",
        "SELECT * FROM orders<CURSOR>",
        CancellationToken.None,
        fallbackApiKey: "zhipu-shared-key");
}
Check("DeepSeek timeout falls back to 智谱 GLM with the shared key",
    fallbackTimeoutHandler.RequestUrls.Count == 2 &&
    fallbackTimeoutHandler.RequestUrls[0].StartsWith("https://deepseek-self-test.invalid", StringComparison.Ordinal) &&
    fallbackTimeoutHandler.RequestUrls[1].StartsWith("https://glm-self-test.invalid/api/paas/v4", StringComparison.Ordinal) &&
    fallbackTimeoutHandler.RequestBodies[1].Contains("\"model\":\"glm-5.2\"", StringComparison.Ordinal) &&
    fallbackTimeoutHandler.AuthorizationValues[1] == "Bearer zhipu-shared-key" &&
    fallbackTimeoutHandler.AuthorizationValues[1] != fallbackTimeoutHandler.AuthorizationValues[0] &&
    fallbackTimeoutResult.Mode == SuggestionMode.Completion &&
    fallbackTimeoutResult.DisplayText == "智谱 GLM 补全" &&
    fallbackTimeoutResult.Explanation.Contains("智谱 glm-5.2", StringComparison.Ordinal));

var fallbackSuccessHandler = new FallbackHttpMessageHandler();
fallbackSuccessHandler.QueueResponse(fallbackEnvelope);
SuggestionResult fallbackSuccessResult;
using (var fallbackSuccessClient = new DeepSeekClient(fallbackSuccessHandler))
{
    fallbackSuccessResult = await fallbackSuccessClient.CompleteAsync(
        fallbackSettings,
        "deepseek-key",
        "SELECT * FROM orders<CURSOR>",
        CancellationToken.None,
        fallbackApiKey: "zhipu-shared-key");
}
Check("DeepSeek success never triggers the GLM fallback",
    fallbackSuccessHandler.RequestUrls.Count == 1 &&
    fallbackSuccessResult.Mode == SuggestionMode.Completion);

var fallbackDisabledHandler = new FallbackHttpMessageHandler();
fallbackDisabledHandler.QueueTimeout();
SuggestionResult fallbackDisabledResult;
using (var fallbackDisabledClient = new DeepSeekClient(fallbackDisabledHandler))
{
    fallbackDisabledResult = await fallbackDisabledClient.CompleteAsync(
        fallbackSettings with { FallbackEnabled = false },
        "deepseek-key",
        "SELECT 1",
        CancellationToken.None,
        fallbackApiKey: "zhipu-shared-key");
}
Check("fallback disabled keeps the DeepSeek timeout error",
    fallbackDisabledHandler.RequestUrls.Count == 1 &&
    fallbackDisabledResult.Mode == SuggestionMode.Error &&
    fallbackDisabledResult.DisplayText == "连接 DeepSeek 失败");

var fallbackNoKeyHandler = new FallbackHttpMessageHandler();
fallbackNoKeyHandler.QueueTimeout();
SuggestionResult fallbackNoKeyResult;
using (var fallbackNoKeyClient = new DeepSeekClient(fallbackNoKeyHandler))
{
    fallbackNoKeyResult = await fallbackNoKeyClient.CompleteAsync(
        fallbackSettings,
        "deepseek-key",
        "SELECT 1",
        CancellationToken.None,
        fallbackApiKey: string.Empty);
}
Check("fallback without a 智谱 key keeps the DeepSeek timeout error",
    fallbackNoKeyHandler.RequestUrls.Count == 1 &&
    fallbackNoKeyResult.Mode == SuggestionMode.Error &&
    fallbackNoKeyResult.DisplayText == "连接 DeepSeek 失败");

var fallbackTransportHandler = new FallbackHttpMessageHandler();
fallbackTransportHandler.QueueTransportFailure();
fallbackTransportHandler.QueueResponse(fallbackEnvelope);
SuggestionResult fallbackTransportResult;
using (var fallbackTransportClient = new DeepSeekClient(fallbackTransportHandler))
{
    fallbackTransportResult = await fallbackTransportClient.CompleteAsync(
        fallbackSettings,
        "deepseek-key",
        "SELECT 1",
        CancellationToken.None,
        fallbackApiKey: "zhipu-shared-key");
}
Check("DeepSeek connection failure also falls back to GLM",
    fallbackTransportHandler.RequestUrls.Count == 2 &&
    fallbackTransportResult.Mode == SuggestionMode.Completion);

var fallbackHttpErrorHandler = new FallbackHttpMessageHandler();
fallbackHttpErrorHandler.QueueStatus(HttpStatusCode.InternalServerError, "upstream boom");
SuggestionResult fallbackHttpErrorResult;
using (var fallbackHttpErrorClient = new DeepSeekClient(fallbackHttpErrorHandler))
{
    fallbackHttpErrorResult = await fallbackHttpErrorClient.CompleteAsync(
        fallbackSettings,
        "deepseek-key",
        "SELECT 1",
        CancellationToken.None,
        fallbackApiKey: "zhipu-shared-key");
}
Check("DeepSeek HTTP error status does not trigger the GLM fallback",
    fallbackHttpErrorHandler.RequestUrls.Count == 1 &&
    fallbackHttpErrorResult.Mode == SuggestionMode.Error &&
    fallbackHttpErrorResult.DisplayText.StartsWith("DeepSeek 请求失败", StringComparison.Ordinal));

Check("guided typing splits Unicode by text element instead of UTF-16 code unit",
    GuidedTypingService.SplitTextElements("A😀中").Count == 3);
Check("SendInput ABI matches the Windows x64 INPUT size",
    Marshal.SizeOf<NativeMethods.NativeInput>() == 40);

bool TestExclusiveHotkeyChord(HotkeyModifiers modifiers, uint virtualKey)
{
    var matcher = new ExclusiveHotkeyMatcher(
        [new ParsedHotkey(HotkeyAction.ToggleGuidedTyping, modifiers, virtualKey, "Ctrl+Alt+D")]);
    var controlDown = matcher.Process(0xA2, keyDown: true, keyUp: false, injected: false);
    var altDown = matcher.Process(0xA4, keyDown: true, keyUp: false, injected: false);
    var firstPress = matcher.Process(virtualKey, keyDown: true, keyUp: false, injected: false);
    var repeatedPress = matcher.Process(virtualKey, keyDown: true, keyUp: false, injected: false);
    var release = matcher.Process(virtualKey, keyDown: false, keyUp: true, injected: false);
    var secondRelease = matcher.Process(virtualKey, keyDown: false, keyUp: true, injected: false);
    return !controlDown.Suppress &&
           !altDown.Suppress &&
           firstPress.Suppress &&
           firstPress.Action == HotkeyAction.ToggleGuidedTyping &&
           repeatedPress.Suppress &&
           repeatedPress.Action is null &&
           release.Suppress &&
           secondRelease is { Suppress: false, Action: null };
}

bool TestExclusiveHotkeySafety()
{
    _ = GlobalHotkeyService.TryParse("Ctrl+Alt+D", out var modifiers, out var virtualKey);
    var matcher = new ExclusiveHotkeyMatcher(
        [new ParsedHotkey(HotkeyAction.ToggleGuidedTyping, modifiers, virtualKey, "Ctrl+Alt+D")]);
    _ = matcher.Process(0xA2, keyDown: true, keyUp: false, injected: false);
    _ = matcher.Process(0xA4, keyDown: true, keyUp: false, injected: false);
    _ = matcher.Process(0xA0, keyDown: true, keyUp: false, injected: false);
    var extraModifier = matcher.Process(virtualKey, keyDown: true, keyUp: false, injected: false);
    _ = matcher.Process(virtualKey, keyDown: false, keyUp: true, injected: false);
    _ = matcher.Process(0xA0, keyDown: false, keyUp: true, injected: false);
    var injected = matcher.Process(virtualKey, keyDown: true, keyUp: false, injected: true);
    var physical = matcher.Process(virtualKey, keyDown: true, keyUp: false, injected: false);
    return !extraModifier.Suppress &&
           extraModifier.Action is null &&
           !injected.Suppress &&
           injected.Action is null &&
           physical.Suppress &&
           physical.Action == HotkeyAction.ToggleGuidedTyping;
}

bool TestDisabledAssistantHotkeySet()
{
    _ = GlobalHotkeyService.TryParse("Ctrl+Alt+S", out var modifiers, out var totalSwitchKey);
    _ = GlobalHotkeyService.TryParse("Ctrl+Alt+D", out _, out var guidedTypingKey);
    var matcher = new ExclusiveHotkeyMatcher(
        [new ParsedHotkey(HotkeyAction.ToggleAssistant, modifiers, totalSwitchKey, "Ctrl+Alt+S")]);
    _ = matcher.Process(0xA2, keyDown: true, keyUp: false, injected: false);
    _ = matcher.Process(0xA4, keyDown: true, keyUp: false, injected: false);
    var guidedTyping = matcher.Process(guidedTypingKey, keyDown: true, keyUp: false, injected: false);
    _ = matcher.Process(guidedTypingKey, keyDown: false, keyUp: true, injected: false);
    var totalSwitch = matcher.Process(totalSwitchKey, keyDown: true, keyUp: false, injected: false);
    return !guidedTyping.Suppress &&
           guidedTyping.Action is null &&
           totalSwitch.Suppress &&
           totalSwitch.Action == HotkeyAction.ToggleAssistant;
}

Check("hotkey validation recognizes aliases and fixed paging conflicts",
    GlobalHotkeyService.TryValidateShortcutSet(
        ["Ctrl+Alt+A", "Ctrl+Alt+C", "Ctrl+Alt+V", "Ctrl+Alt+O", "Ctrl+Alt+P", "Win+PageUp", "Win+PageDown"],
        out _) &&
    !GlobalHotkeyService.TryValidateShortcutSet(["Ctrl+Alt+A", "Control+Alt+A"], out _) &&
    !GlobalHotkeyService.TryValidateShortcutSet(["Ctrl+Alt+A", "not-a-hotkey"], out _));
Check("exclusive hotkey matcher suppresses one physical chord without repeating the action",
    GlobalHotkeyService.TryParse("Ctrl+Alt+D", out var guidedModifiers, out var guidedKey) &&
    TestExclusiveHotkeyChord(guidedModifiers, guidedKey));
Check("exclusive hotkey matcher ignores injected keys and non-exact modifier combinations",
    TestExclusiveHotkeySafety());
Check("disabled Assistant matcher keeps only the total switch exclusive",
    TestDisabledAssistantHotkeySet());
var sqlClipboardAnswer = ClipboardAnswerService.Select(new SuggestionResult
{
    Mode = SuggestionMode.Completion,
    InsertText = "short continuation",
    FullAnswer = "```sql\nSELECT * FROM orders;\n```"
}, sqlAnswer: true);
var fillClipboardAnswer = ClipboardAnswerService.Select(new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    QuestionType = "fill_blank",
    FullAnswer = "SELECT；FROM；WHERE"
}, sqlAnswer: false);
Check("clipboard answer prefers the full answer and removes only an outer Markdown fence",
    sqlClipboardAnswer is { Text: "SELECT * FROM orders;", Kind: "完整 SQL" });
Check("clipboard answer labels ordered fill-blank values as a complete answer",
    fillClipboardAnswer is { Text: "SELECT；FROM；WHERE", Kind: "填空题完整答案" });
Check("clipboard answer rejects errors and enforces a local size ceiling",
    ClipboardAnswerService.Select(SuggestionResult.Error("failure"), sqlAnswer: false) is null &&
    ClipboardAnswerService.Select(new SuggestionResult
    {
        Mode = SuggestionMode.ExerciseAnswer,
        FullAnswer = new string('答', ClipboardAnswerService.MaximumClipboardCharacters + 10)
    }, sqlAnswer: false)?.CharacterCount == ClipboardAnswerService.MaximumClipboardCharacters);
Check("clipboard question validation trims a complete question and rejects empty or oversized input",
    ClipboardAnswerService.TryNormalizeQuestion("  查询最新订单  ", out var normalizedClipboardQuestion, out _) &&
    normalizedClipboardQuestion == "查询最新订单" &&
    !ClipboardAnswerService.TryNormalizeQuestion("  ", out _, out _) &&
    !ClipboardAnswerService.TryNormalizeQuestion(
        new string('题', ClipboardAnswerService.MaximumQuestionCharacters + 1),
        out _,
        out _));
var sqlQuestionClipboardAnswer = ClipboardAnswerService.Select(new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    QuestionType = "sql",
    FullAnswer = "SELECT 1;"
}, sqlAnswer: false);
Check("clipboard SQL questions receive the complete SQL label",
    sqlQuestionClipboardAnswer is { Text: "SELECT 1;", Kind: "完整 SQL" });

DocumentContextResult WordDocumentAt(string text, int caret, string identity = "word-exercise-self-test") => new()
{
    Status = DocumentContextStatus.Captured,
    ApplicationName = "Microsoft Word",
    ProcessName = "WINWORD",
    DocumentName = "SQL练习.docx",
    DocumentIdentity = identity,
    Text = text,
    ContentHash = ContentHasher.Sha256(text),
    CaretOffset = caret,
    CaretAvailable = true,
    SelectionAvailable = true
};

var generalWordPrompt = PromptTemplates.BuildWordDocumentCompletionPrompt(
    WordDocumentAt("项目背景\r需要补充结论\r附件", "项目背景\r需要补充".Length, "general-word"));
Check("general Word writing prompt carries the full document and exact cursor",
    generalWordPrompt.Contains("项目背景", StringComparison.Ordinal) &&
    generalWordPrompt.Contains("需要补充<CURSOR>结论", StringComparison.Ordinal) &&
    generalWordPrompt.Contains("附件", StringComparison.Ordinal) &&
    generalWordPrompt.Split("<CURSOR>", StringSplitOptions.None).Length == 2);
var literalGeneralWordPrompt = PromptTemplates.BuildWordDocumentCompletionPrompt(
    WordDocumentAt("原文写着 <CURSOR> 但这里才是光标", "原文写着 <CURSOR>".Length, "literal-general-word"));
Check("a literal cursor token cannot create a second general Word marker",
    literalGeneralWordPrompt.Split("<CURSOR>", StringSplitOptions.None).Length == 2 &&
    literalGeneralWordPrompt.Contains("文档中的 CURSOR 字样", StringComparison.Ordinal));
Check("Word exercise routing requires exercise cues instead of any numbered SQL document",
    MainWindow.LooksLikeDatabaseExercise("数据库基础\n简答 1. 什么是主键？\n答：唯一标识。\n简答 2. 什么是外键？") &&
    !MainWindow.LooksLikeDatabaseExercise("数据库迁移说明\n1. 准备 SQL\n2. 执行校验\n3. 发布"));

var multiPartExerciseDocument = string.Join("\r",
[
    "简答 3. 请说明第三范式。",
    "答：非主属性不能传递依赖于主键。",
    "简答 4.（10 分）",
    "给定查询：",
    "SELECT * FROM orders",
    "WHERE user_id = 100 AND status = 1",
    "ORDER BY created_at DESC;",
    "(1) 为该查询设计一个联合索引，写出列顺序。（4 分）INDEX(user_id, status, created_at)",
    "(2) 说明你这样排序的理由。（3 分）",
    "(3) 另举两种会导致该索引失效的写法。（3 分）",
    "参考答案与评分要点",
    "简答 4：INDEX(user_id, status, created_at)"
]).Replace("\r(2)", "\v(2)", StringComparison.Ordinal)
  .Replace("\r(3)", "\v(3)", StringComparison.Ordinal);
var multiPartCaret = multiPartExerciseDocument.IndexOf("(2) 说明", StringComparison.Ordinal) + 4;
var multiPartDocument = WordDocumentAt(multiPartExerciseDocument, multiPartCaret);
Check("late Word results are valid only for the same document hash and caret",
    WordExerciseContextBuilder.IsSameObservation(multiPartDocument, multiPartDocument with { }) &&
    !WordExerciseContextBuilder.IsSameObservation(
        multiPartDocument,
        multiPartDocument with { CaretOffset = multiPartCaret + 1 }) &&
    !WordExerciseContextBuilder.IsSameObservation(
        multiPartDocument,
        multiPartDocument with { ContentHash = "new-content" }));
var locateContext = WordExerciseContextBuilder.Build(
    multiPartDocument,
    WordExerciseRequestMode.LocateAndAnswer,
    WordExerciseContextBuilder.FinalExpandedRadius,
    expansionLevel: 2)!;

Check("Word AI locator context carries exactly one real cursor marker",
    locateContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2);
Check("Word soft line breaks become separately addressable source blocks",
    locateContext.Blocks.Any(block => block.Text.StartsWith("(2)", StringComparison.Ordinal)) &&
    locateContext.Blocks.Any(block => block.Text.StartsWith("(3)", StringComparison.Ordinal)));
Check("initial Word context radius is defined as 300 characters per side",
    WordExerciseContextBuilder.InitialRadius == 300 &&
    WordExerciseContextBuilder.FirstExpandedRadius == 1_000 &&
    WordExerciseContextBuilder.FinalExpandedRadius == 3_000);

var literalCursorText = "第1题 文档原文中写着 <CURSOR>\r答：";
var literalCursorContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(literalCursorText, literalCursorText.Length),
    WordExerciseRequestMode.LocateAndAnswer,
    WordExerciseContextBuilder.InitialRadius,
    expansionLevel: 0)!;
Check("a literal cursor token in Word text cannot create a second locator marker",
    literalCursorContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2 &&
    literalCursorContext.BlocksWithCursor.Contains("文档中的 CURSOR 字样", StringComparison.Ordinal));

var questionStartBlock = locateContext.Blocks.Single(block =>
    block.Text.Contains("简答 4.（10 分）", StringComparison.Ordinal));
var questionEndBlock = locateContext.Blocks.Single(block =>
    block.Text.Contains("(3) 另举两种", StringComparison.Ordinal));
var locatedPayload = new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    IsQuestion = true,
    ContextComplete = true,
    NeedsMoreContext = "none",
    SameAsCurrentQuestion = true,
    QuestionNumber = "第4题",
    ActiveSubquestionNumber = "(2)",
    QuestionType = "short_answer",
    QuestionStartBlockId = questionStartBlock.Id,
    QuestionEndBlockId = questionEndBlock.Id,
    QuestionStartAnchor = "简答 4.（10 分）",
    QuestionEndAnchor = "(3) 另举两种会导致该索引失效的写法。（3 分）",
    QuestionText = "这是模型改写过的文本，不能作为原文真值。",
    AnswerState = "partial",
    DetectedUserAnswer = "INDEX(user_id, status, created_at)",
    DisplayText = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前",
    InsertText = "等值查询列在前，排序列放在最后。",
    FullAnswer = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前；(3) 对索引列做函数、隐式类型转换。",
    Explanation = "联合索引先匹配等值条件，再利用 created_at 的有序性。",
    Confidence = 0.96
};
Check("validated AI blocks reconstruct one complete top-level question",
    WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload,
        multiPartExerciseDocument,
        out var locatedQuestion,
        out _));
Check("a multi-part answer keeps the parent number and current child separately",
    locatedQuestion is not null &&
    locatedQuestion.QuestionNumber == "4" &&
    locatedQuestion.ActiveSubquestionNumber == "2" &&
    locatedQuestion.QuestionType == "short_answer");
Check("canonical question text comes from Word and owns the shared SQL plus every child",
    locatedQuestion is not null &&
    locatedQuestion.QuestionText.StartsWith("简答 4.", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("SELECT * FROM orders", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(1) 为该查询", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(2) 说明", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(3) 另举两种", StringComparison.Ordinal) &&
    !locatedQuestion.QuestionText.Contains("模型改写过的文本", StringComparison.Ordinal) &&
    !locatedQuestion.QuestionText.Contains("参考答案与评分要点", StringComparison.Ordinal));

Check("invalid model block IDs are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { QuestionEndBlockId = "B999" },
        multiPartExerciseDocument,
        out _,
        out _));
Check("anchors that are not at the returned Word boundaries are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { QuestionStartAnchor = "ORDER BY created_at DESC;" },
        multiPartExerciseDocument,
        out _,
        out _));
Check("low-confidence question locations are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { Confidence = 0.2 },
        multiPartExerciseDocument,
        out _,
        out _));
Check("a completion payload cannot masquerade as a Word locator result",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { Mode = SuggestionMode.Completion },
        multiPartExerciseDocument,
        out _,
        out _));

var longExerciseDocument = new string('前', 420) + "\r简答 9. 请分析以下查询。\r" +
                           new string('题', 320) + "\r答：\r" + new string('后', 420);
var longExerciseCaret = longExerciseDocument.IndexOf("请分析", StringComparison.Ordinal) + 3;
var narrowContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(longExerciseDocument, longExerciseCaret),
    WordExerciseRequestMode.LocateAndAnswer,
    radius: 80,
    expansionLevel: 0)!;
var edgePayload = locatedPayload with
{
    QuestionStartBlockId = narrowContext.Blocks[0].Id,
    QuestionEndBlockId = narrowContext.Blocks[^1].Id
};
Check("a model range touching a cropped window requests adaptive expansion",
    narrowContext.WindowStart > 0 &&
    narrowContext.WindowEnd < narrowContext.DocumentLength &&
    WordExerciseContextBuilder.NeedsExpandedContext(narrowContext, edgePayload));
Check("an explicit incomplete-context response requests adaptive expansion",
    WordExerciseContextBuilder.NeedsExpandedContext(
        narrowContext,
        edgePayload with { ContextComplete = false, NeedsMoreContext = "both" }));

var editedMultiPartDocument = multiPartExerciseDocument.Replace(
    "(2) 说明你这样排序的理由。（3 分）",
    "(2) 说明你这样排序的理由。（3 分）等值条件列在前，",
    StringComparison.Ordinal);
var editedMultiPartCaret = editedMultiPartDocument.IndexOf("等值条件列在前，", StringComparison.Ordinal) +
                           "等值条件列在前，".Length;
var continuationContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(editedMultiPartDocument, editedMultiPartCaret),
    WordExerciseRequestMode.ContinueCurrentQuestion,
    WordExerciseContextBuilder.InitialRadius,
    expansionLevel: 0,
    locatedQuestion,
    "等值查询列在前，排序列放在最后。")!;
Check("continuation context sends the stored complete parent question",
    continuationContext.CurrentQuestion?.QuestionText.Contains("(3) 另举两种", StringComparison.Ordinal) == true);
Check("continuation context describes writing added since the hotkey baseline",
    continuationContext.BaselineChange.Contains("等值条件列在前", StringComparison.Ordinal));
Check("continuation context still has exactly one cursor in the current local window",
    continuationContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2);

var locatePrompt = PromptTemplates.BuildWordExercisePrompt(locateContext);
var continuationPrompt = PromptTemplates.BuildWordExercisePrompt(continuationContext);
var exerciseSystemPrompt = PromptTemplates.BuildExerciseSystemPrompt(new AppSettings());
Check("Word locator prompt uses the one-call locate-and-answer protocol",
    locatePrompt.Contains("locate_and_answer", StringComparison.Ordinal) &&
    locatePrompt.Contains(questionStartBlock.Id, StringComparison.Ordinal) &&
    locatePrompt.Contains("本次 Word 原文窗口", StringComparison.Ordinal) &&
    locatePrompt.Split("<CURSOR>", StringSplitOptions.None).Length == 2);
Check("continuation prompt includes both the saved parent and baseline change",
    continuationPrompt.Contains("continue_current_question", StringComparison.Ordinal) &&
    continuationPrompt.Contains("完整顶层题目", StringComparison.Ordinal) &&
    continuationPrompt.Contains("等值条件列在前", StringComparison.Ordinal));
Check("exercise system prompt makes top-level multi-part ownership a hard rule",
    exerciseSystemPrompt.Contains("公共说明、表结构、业务背景、SQL、数据样例", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("question_number=\"4\"", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("反例", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("全部子问", StringComparison.Ordinal));
Check("parenthesized numbers are independent only when no shared parent stem exists",
    exerciseSystemPrompt.Contains("不存在共享上级题干", StringComparison.Ordinal));
Check("exercise prompt requires same-question verification before continuation",
    exerciseSystemPrompt.Contains("same_as_current_question=false", StringComparison.Ordinal));
Check("partial-answer prompt forbids echoing text the user already wrote",
    exerciseSystemPrompt.Contains("绝不能复述 detected_user_answer", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("光标后下一步应写什么", StringComparison.Ordinal));
Check("question-number normalization is owned by the program",
    WordExerciseContextBuilder.NormalizeQuestionNumber("第4题") == "4" &&
    WordExerciseContextBuilder.NormalizeQuestionNumber("**第4题。**") == "4" &&
    WordExerciseContextBuilder.NormalizeQuestionNumber("简答 4.") == "4" &&
    WordExerciseContextBuilder.NormalizeSubquestionNumber("（2）") == "2");

var detailAtTopLeft = VisionScreenshotCaptureService.BuildDetailRectangle(2200, 1400, 8, 12);
var detailAtBottomRight = VisionScreenshotCaptureService.BuildDetailRectangle(2200, 1400, 2190, 1390);
Check("vision detail crops stay inside the screenshot at both edges",
    detailAtTopLeft.Left == 0 && detailAtTopLeft.Top == 0 &&
    detailAtTopLeft.Width == 1600 && detailAtTopLeft.Height == 1000 &&
    detailAtBottomRight.Right == 2200 && detailAtBottomRight.Bottom == 1400);

var visionCapture = new VisionCaptureSnapshot
{
    Status = VisionCaptureStatus.Captured,
    ProcessName = "self-test-editor",
    WindowBounds = new PixelRect(0, 0, 1200, 800),
    CaretScreenBounds = new PixelRect(600, 400, 2, 20),
    CaretSource = "self-test caret",
    CaretConfidence = 0.97,
    DetailBounds = new PixelRect(200, 100, 800, 600),
    DetailMarkerX = 400,
    DetailMarkerY = 300,
    FullImageBytes = [1, 2, 3],
    FullImageMediaType = "image/png",
    DetailImageBytes = [4, 5, 6],
    DetailImageMediaType = "image/png",
    CaptureHash = "vision-self-test"
};
var visionPrompt = VisionExercisePromptTemplates.BuildUserPrompt(visionCapture);
var multilineAnswer = "class Solution:\n    def solve(self, nums):\n        return sorted(nums)\n" + new string('#', 150);
foreach (var origin in new[] { "vision_screenshot", "clipboard_screen_ocr" })
{
    var sample = new SuggestionResult { Origin = origin, DisplayText = "先排序", FullAnswer = multilineAnswer, InsertText = "sorted(nums)" };
    Check("the floating strip preserves the full answer and indentation for " + origin,
        TaskbarSuggestionFormatter.BuildText(sample) == sample.FullSuggestionText && sample.FullSuggestionText == multilineAnswer);
}
Check("vision prompt identifies the clean and annotated images plus exact marker coordinates",
    VisionExercisePromptTemplates.DefaultSystemPrompt.Contains("红色空心圆", StringComparison.Ordinal) &&
    visionPrompt.Contains("红圈中心像素：(400, 300)", StringComparison.Ordinal) &&
    visionPrompt.Contains("(0.5000, 0.5000)", StringComparison.Ordinal));

var visionArguments = JsonSerializer.Serialize(new
{
    is_question = true,
    question_type = "short_answer",
    question_number = "第6题",
    active_subquestion_number = "（2）",
    question_text = "第6题 请说明索引最左前缀原则。",
    answer_state = "partial",
    detected_user_answer_before_cursor = "联合索引从左向右匹配",
    display_text = "，中间列缺失后后续列不能用于连续匹配。",
    insert_text = "，中间列缺失后后续列不能用于连续匹配。",
    full_answer = "联合索引应从最左列开始连续匹配。",
    explanation = "这是光标后的剩余答案。",
    confidence = 0.94
});
var visionEnvelope = JsonSerializer.Serialize(new
{
    choices = new[]
    {
        new
        {
            message = new
            {
                content = (string?)null,
                tool_calls = new[]
                {
                    new
                    {
                        type = "function",
                        function = new { name = "return_exercise_answer", arguments = visionArguments }
                    }
                }
            }
        }
    },
    usage = new { prompt_tokens = 456, completion_tokens = 78 }
});
var visionHandler = new RecordingHttpMessageHandler(visionEnvelope);
SuggestionResult visionApiResult;
using (var visionClient = new DeepSeekVisionClient(visionHandler))
{
    visionApiResult = await visionClient.AnswerExerciseAsync(
        new AppSettings(),
        "vision-self-test-key",
        visionCapture,
        CancellationToken.None);
}
Check("DeepSeek vision request sends two images and maps function arguments",
    visionHandler.AuthorizationScheme == "Bearer" &&
    visionHandler.RequestBody.Split("\"type\":\"image_url\"", StringSplitOptions.None).Length == 3 &&
    visionHandler.RequestBody.Contains("deepseek-flash", StringComparison.Ordinal) &&
    visionApiResult.Origin == "vision_screenshot" &&
    visionApiResult.Mode == SuggestionMode.ExerciseAnswer &&
    visionApiResult.QuestionNumber == "6" &&
    visionApiResult.ActiveSubquestionNumber == "2" &&
    visionApiResult.AnswerState == "partial" &&
    visionApiResult.InsertText.StartsWith('，') &&
    visionApiResult.PromptTokens == 456 &&
    visionApiResult.CompletionTokens == 78);

using (var visionClient = new DeepSeekVisionClient())
{
    var generalQuestion = JsonSerializer.Serialize(new
    {
        is_question = true,
        question_type = "short_answer",
        question_text = "一年有几个月？",
        answer_state = "partial",
        display_text = "个月。",
        insert_text = "个月。",
        full_answer = "一年有 12 个月。",
        confidence = 0.95
    });
    var answerWithoutCaret = visionClient.ParsePayload(generalQuestion, hasReliableCaret: false);
    Check("screen OCR shows a general answer without offering a continuation when the caret is unknown",
        answerWithoutCaret.QuestionText == "一年有几个月？" &&
        answerWithoutCaret.AnswerState == "uncertain" &&
        answerWithoutCaret.InsertText.Length == 0 &&
        answerWithoutCaret.DisplayText == "一年有 12 个月。" &&
        TaskbarSuggestionFormatter.BuildText(answerWithoutCaret) == answerWithoutCaret.DisplayText &&
        ClipboardAnswerService.Select(answerWithoutCaret, sqlAnswer: false)?.Text == "一年有 12 个月。");

    var transcription = visionClient.ParsePayload("""
        {"is_question":false,"question_text":"图书馆周一开放。","insert_text":"无效续写","confidence":0.95}
        """, hasReliableCaret: true);
    Check("screen OCR displays transcribed text without supplying an answer for plain text",
        transcription.Mode == SuggestionMode.Information &&
        transcription.QuestionText == "图书馆周一开放。" &&
        transcription.DisplayText == transcription.QuestionText &&
        ClipboardAnswerService.Select(transcription, sqlAnswer: false) is null);
    var ambiguous = visionClient.ParsePayload("""
        {"is_question":true,"target_ambiguous":true,"question_text":"两道独立题目","answer_state":"partial","insert_text":"A","full_answer":"A","confidence":0.99}
        """, hasReliableCaret: true);
    Check("an ambiguous OCR target prompts for a new screenshot and supplies no answer",
        ambiguous.Mode == SuggestionMode.Information && ambiguous.DisplayText.Contains("多道题") &&
        ambiguous.InsertText.Length == 0 && ambiguous.FullAnswer.Length == 0 &&
        ClipboardAnswerService.Select(ambiguous, sqlAnswer: false) is null);
}

var deepSeekListener = new TcpListener(IPAddress.Loopback, 0);
deepSeekListener.Start();
var deepSeekPort = ((IPEndPoint)deepSeekListener.LocalEndpoint).Port;
var fakeDeepSeekServer = Task.Run(async () =>
{
    using var connection = await deepSeekListener.AcceptTcpClientAsync();
    await using var stream = connection.GetStream();
    using var requestBytes = new MemoryStream();
    var buffer = new byte[4096];
    var headerEnd = -1;
    var contentLength = 0;
    var isChunked = false;
    while (headerEnd < 0)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
        var bytes = requestBytes.ToArray();
        headerEnd = Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd >= 0)
        {
            var headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var lengthHeader = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (lengthHeader is not null)
            {
                _ = int.TryParse(lengthHeader["Content-Length:".Length..].Trim(), out contentLength);
            }
            isChunked = headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
            headerEnd += 4;
        }
    }
    while (headerEnd >= 0 && requestBytes.Length - headerEnd < contentLength)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
    }
    while (headerEnd >= 0 && isChunked &&
           requestBytes.ToArray().AsSpan(headerEnd).IndexOf("\r\n0\r\n\r\n"u8) < 0)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
    }

    var inner = JsonSerializer.Serialize(new
    {
        mode = "exercise_answer",
        is_question = true,
        context_complete = true,
        needs_more_context = "none",
        same_as_current_question = true,
        question_number = "4",
        active_subquestion_number = "2",
        question_type = "short_answer",
        question_start_block_id = "B003",
        question_end_block_id = "B010",
        question_start_anchor = "简答 4.（10 分）",
        question_end_anchor = "(3) 另举两种会导致该索引失效的写法。（3 分）",
        question_text = "完整顶层题目原文",
        answer_state = "partial",
        detected_user_answer = "等值条件列在前",
        display_text = "(2) 等值条件列在前，排序列在后",
        insert_text = "排序列 created_at 放在最后。",
        full_answer = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前；(3) 避免函数和隐式转换。",
        explanation = "覆盖三个小问的完整解析。",
        issues = Array.Empty<string>(),
        confidence = 0.96
    });
    var envelope = JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = inner } } },
        usage = new
        {
            prompt_tokens = 321,
            prompt_cache_hit_tokens = 200,
            prompt_cache_miss_tokens = 121,
            completion_tokens = 88
        }
    });
    var responseBody = Encoding.UTF8.GetBytes(envelope);
    var responseHeader = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(responseHeader);
    await stream.WriteAsync(responseBody);
    await stream.FlushAsync();
});
SuggestionResult exerciseApiResult;
using (var fakeDeepSeekClient = new DeepSeekClient())
{
    exerciseApiResult = await fakeDeepSeekClient.CompleteAsync(
        new AppSettings
        {
            ApiBaseUrl = $"http://127.0.0.1:{deepSeekPort}",
            Model = "self-test"
        },
        "self-test-key",
        locatePrompt,
        CancellationToken.None,
        systemPrompt: exerciseSystemPrompt);
}
await fakeDeepSeekServer;
deepSeekListener.Stop();
var exerciseApiMapped =
    exerciseApiResult.Mode == SuggestionMode.ExerciseAnswer &&
    exerciseApiResult.IsQuestion &&
    exerciseApiResult.QuestionNumber == "4" &&
    exerciseApiResult.ActiveSubquestionNumber == "2" &&
    exerciseApiResult.QuestionType == "short_answer" &&
    exerciseApiResult.AnswerState == "partial" &&
    exerciseApiResult.FullAnswer.Contains("(3)", StringComparison.Ordinal) &&
    exerciseApiResult.PromptTokens == 321 &&
    exerciseApiResult.PromptCacheHitTokens == 200 &&
    exerciseApiResult.PromptCacheMissTokens == 121 &&
    exerciseApiResult.CompletionTokens == 88;
if (!exerciseApiMapped)
{
    Console.Error.WriteLine("Exercise API mapping diagnostic: " + JsonSerializer.Serialize(exerciseApiResult));
}
Check("DeepSeek exercise JSON maps locator, answer-state and token-usage fields", exerciseApiMapped);

var realPartialAnswer = new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    QuestionType = "short_answer",
    AnswerState = "partial",
    DisplayText = "1NF:每一列的字段都不能由多个组成 2FN：",
    InsertText = "在满足 1NF 的基础上，非主属性必须完全依赖于整个候选键。",
    FullAnswer = "1NF 要求属性原子性；2NF 要求完全函数依赖；3NF 要求不存在传递依赖。"
};
var realPartialTaskbarAnswer = WordExercisePresentation.SelectTaskbarAnswer(
    realPartialAnswer,
    "short_answer",
    realPartialAnswer.FullAnswer,
    realPartialAnswer.InsertText,
    continuation: false);
Check("partial short answers show the unseen continuation instead of echoing existing writing",
    realPartialTaskbarAnswer == realPartialAnswer.InsertText &&
    !realPartialTaskbarAnswer.Contains("每一列的字段", StringComparison.Ordinal));
Check("choice questions retain the useful option text rather than reducing the answer to one letter",
    WordExercisePresentation.SelectTaskbarAnswer(
        new SuggestionResult
        {
            QuestionType = "choice",
            AnswerState = "empty",
            DisplayText = "B. DDL",
            InsertText = "B"
        },
        "choice",
        "B",
        "B",
        continuation: false) == "B. DDL");
Check("completed short answers without another scoring point receive an explicit status",
    WordExercisePresentation.SelectTaskbarAnswer(
        new SuggestionResult { QuestionType = "short_answer", AnswerState = "complete" },
        "short_answer",
        "完整参考答案",
        string.Empty,
        continuation: true) == "答案已基本完整");

var exerciseDockAnswer = WordExercisePresentation.BuildDisplayText(
    new string('答', 140),
    string.Empty,
    "short_answer",
    "第4题",
    continuation: true);
Check("long Word answers use program-owned type/number formatting and stay taskbar-safe",
    exerciseDockAnswer.StartsWith("简答题｜4.", StringComparison.Ordinal) &&
    exerciseDockAnswer.Length <= 100 &&
    exerciseDockAnswer.EndsWith('…'));
var expandedExerciseDockAnswer = TaskbarSuggestionFormatter.BuildText(new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    IsQuestion = true,
    QuestionType = "short_answer",
    QuestionNumber = "第4题",
    AnswerState = "empty",
    DisplayText = exerciseDockAnswer,
    InsertText = new string('答', 140),
    FullAnswer = new string('答', 140)
});
Check("taskbar paging retains the complete exercise answer beyond the short preview",
    expandedExerciseDockAnswer.StartsWith("简答题｜4.", StringComparison.Ordinal) &&
    expandedExerciseDockAnswer.Length > 100 &&
    !expandedExerciseDockAnswer.EndsWith('…'));
var expandedPartialDockAnswer = TaskbarSuggestionFormatter.BuildText(realPartialAnswer with
{
    IsQuestion = true,
    QuestionNumber = "3"
});
Check("taskbar paging expands only the unseen continuation for partial answers",
    expandedPartialDockAnswer == $"简答题｜3.{realPartialAnswer.InsertText}" &&
    !expandedPartialDockAnswer.Contains("1NF 要求属性原子性", StringComparison.Ordinal));
Check("a model-repeated top-level number is removed before program formatting",
    WordExercisePresentation.BuildDisplayText(
        "第4题：(2) 等值列在前",
        string.Empty,
        "short_answer",
        "4",
        continuation: false) == "简答题｜4.(2) 等值列在前");

var exerciseAcceptance = new SuggestionAcceptanceState(
    multiPartExerciseDocument,
    multiPartCaret,
    "等值查询列在前，排序列放在最后。");
var matchingExerciseInput = multiPartExerciseDocument.Insert(multiPartCaret, "等值查询列在前");
var exerciseProgress = SuggestionAcceptanceMatcher.Match(
        exerciseAcceptance,
        new ProbeResult
        {
            Text = matchingExerciseInput,
            CaretOffset = multiPartCaret + "等值查询列在前".Length
        });
Check("typing the offered Word continuation exposes the untyped suffix locally",
    exerciseProgress.IsFollowing &&
    exerciseProgress.RemainingText == "，排序列放在最后。");
var exerciseQuestionState = new WordExerciseQuestionState
{
    QuestionNumber = "3",
    QuestionType = "short_answer"
};
var advancedExerciseSuggestion = WordExercisePresentation.AdvanceFollowingSuggestion(
    realPartialAnswer,
    exerciseQuestionState,
    exerciseProgress);
Check("the Word taskbar advances locally to the untyped suffix",
    advancedExerciseSuggestion.InsertText == "，排序列放在最后。" &&
    advancedExerciseSuggestion.DisplayText == "简答题｜3.，排序列放在最后。" &&
    !advancedExerciseSuggestion.DisplayText.Contains("每一列的字段", StringComparison.Ordinal));
var completedExerciseSuggestion = WordExercisePresentation.AdvanceFollowingSuggestion(
    realPartialAnswer,
    exerciseQuestionState,
    new SuggestionAcceptanceProgress(true, realPartialAnswer.InsertText.Length, string.Empty));
Check("finishing a Word suggestion leaves a stable completion status while the next point is analyzed",
    completedExerciseSuggestion.InsertText.Length == 0 &&
    completedExerciseSuggestion.DisplayText == "简答题｜3.当前建议已写完");

Check("unchanged document remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, baseDocument.Length)));
Check("matching prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM", baseDocument.Length + 2)));
var sqlProgress = SuggestionAcceptanceMatcher.Match(state, Probe(baseDocument + "OM", baseDocument.Length + 2));
Check("matching SQL input reports consumed and remaining suggestion text",
    sqlProgress.ConsumedSuggestedCharacters == 2 && sqlProgress.RemainingText == " users;");
Check("case-insensitive SQL keyword prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "om users", baseDocument.Length + 8)));
Check("complete suggestion remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM users;", baseDocument.Length + 9)));
Check("a completely typed suggestion is marked complete",
    SuggestionAcceptanceMatcher.Match(state, Probe(baseDocument + "OM users;", baseDocument.Length + 9)).IsComplete);
Check("divergent input requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OX", baseDocument.Length + 2)));
Check("moving the caret requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, 2)));

var middleBase = "SELECT  WHERE active = 1";
var middleState = new SuggestionAcceptanceState(middleBase, 7, "* FROM users");
var middleCurrent = middleBase.Insert(7, "* FROM");
Check("text after the caret is preserved", SuggestionAcceptanceMatcher.IsFollowing(middleState, Probe(middleCurrent, 13)));

var newlineState = new SuggestionAcceptanceState("-- intent", 9, "\nSELECT 1;");
Check("CRLF input matches LF suggestion", SuggestionAcceptanceMatcher.IsFollowing(newlineState, Probe("-- intent\r\nSELECT", 17)));
Check("line-ending normalization still returns the correct raw remaining suffix",
    SuggestionAcceptanceMatcher.Match(newlineState, Probe("-- intent\r\nSELECT", 17)).RemainingText == " 1;");

var commentProbe = new ProbeResult
{
    Text = "-- 查询所有活跃用户\nSELECT ",
    CaretOffset = "-- 查询所有活跃用户\nSELECT ".Length,
    CaretLine = 2,
    CaretColumn = 8
};
var completionPrompt = PromptTemplates.BuildCompletionPrompt(commentProbe);
Check("line comment is labeled as an AI intent hint", completionPrompt.Contains("-- 查询所有活跃用户", StringComparison.Ordinal));

var unfinishedBlock = new ProbeResult { Text = "/* 只查询今天的数据", CaretOffset = 11 };
Check("unfinished block comment is retained", PromptTemplates.BuildCompletionPrompt(unfinishedBlock).Contains("只查询今天的数据", StringComparison.Ordinal));

var diagnosisPrompt = PromptTemplates.BuildDiagnosisPrompt("SELECT", "SELECT");
Check("diagnosis asks for every issue", diagnosisPrompt.Contains("所有能够确认", StringComparison.Ordinal));
Check("diagnosis requests priority order", diagnosisPrompt.Contains("P0 → P1 → P2", StringComparison.Ordinal));
var longDiagnosisDocument = "DIAGNOSIS_DOCUMENT_START\n" + new string('d', 150_000) + "\nDIAGNOSIS_DOCUMENT_END";
var longDiagnosisPrompt = PromptTemplates.BuildDiagnosisPrompt("SELECT 1;", longDiagnosisDocument);
Check("diagnosis also receives a formerly truncated document in full",
    longDiagnosisPrompt.Contains("DIAGNOSIS_DOCUMENT_START", StringComparison.Ordinal) &&
    longDiagnosisPrompt.Contains("DIAGNOSIS_DOCUMENT_END", StringComparison.Ordinal));

var foreignKeyDocument = """
    CREATE TABLE yangzhuoran_exam_addresses(
      id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
      user_id BIGINT UNSIGNED NOT NULL,
      receiver_name VARCHAR(30) NOT NULL,
      receiver_mobile VARCHAR(20) NOT NULL,
      address_text VARCHAR(200) NOT NULL,
      is_default INT UNSIGNED DEFAULT 0,
      created_at DATETIME NOT NULL DEFAULT CURRENT_TIME,
      PRIMARY KEY (id),
      CONSTRAINT fk_user_id 
    """;
var foreignKeyProbe = new ProbeResult
{
    Text = foreignKeyDocument,
    CaretOffset = foreignKeyDocument.Length,
    CaretLine = 10,
    CaretColumn = 25,
    ContentHash = "foreign-key-test"
};
var foreignKeyContext = SqlContextAnalyzer.Analyze(foreignKeyProbe);
Check("fk constraint name enters foreign-key continuation state", foreignKeyContext.ExpectedKind == SqlExpectedContinuationKind.ForeignKeyConstraint);
Check("fk column is inferred from an already defined column", foreignKeyContext.ForeignKeyColumn == "user_id");
Check("defined is_default column is remembered", foreignKeyContext.DefinedColumns.Contains("is_default"));
var immediateForeignKey = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(foreignKeyProbe);
Check("local guard immediately suggests FOREIGN KEY", immediateForeignKey?.InsertText.StartsWith("FOREIGN KEY (user_id) REFERENCES", StringComparison.Ordinal) == true);
Check("repeated is_default suggestion is rejected", !SqlContextAnalyzer.IsCompatible(foreignKeyProbe, new SuggestionResult
{
    Mode = SuggestionMode.Completion,
    InsertText = "is_default TINYINT NOT NULL DEFAULT 0,"
}));

var missingCreateTableParenthesisDocument = """
    CREATE TABLE yangzhuoran_exam_
      id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL,
    )
    """;
var missingCreateTableParenthesisCaret = missingCreateTableParenthesisDocument.IndexOf('\n');
var missingCreateTableParenthesisProbe = new ProbeResult
{
    Text = missingCreateTableParenthesisDocument,
    CaretOffset = missingCreateTableParenthesisCaret,
    CaretLine = 1,
    CaretColumn = 31
};
var missingCreateTableParenthesisContext = SqlContextAnalyzer.Analyze(missingCreateTableParenthesisProbe);
var immediateCreateTableParenthesis = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(missingCreateTableParenthesisProbe);
Check("caret after CREATE TABLE name detects the missing opening parenthesis",
    missingCreateTableParenthesisContext.ExpectedKind == SqlExpectedContinuationKind.CreateTableOpeningParenthesis);
Check("missing CREATE TABLE parenthesis is shown clearly but inserts only SQL",
    immediateCreateTableParenthesis?.DisplayText.Contains("缺少英文左括号", StringComparison.Ordinal) == true &&
    immediateCreateTableParenthesis.InsertText == "(");
Check("opening-parenthesis guard accepts only a parenthesis continuation",
    SqlContextAnalyzer.IsCompatible(missingCreateTableParenthesisProbe, immediateCreateTableParenthesis!) &&
    !SqlContextAnalyzer.IsCompatible(missingCreateTableParenthesisProbe, new SuggestionResult
    {
        Mode = SuggestionMode.Completion,
        InsertText = "id BIGINT"
    }));

var unrankedCandidates = new SuggestionResult
{
    Mode = SuggestionMode.Completion,
    Candidates =
    [
        new SuggestionCandidate { Rank = 1, InsertText = "is_default TINYINT NOT NULL DEFAULT 0,", DisplayText = "is_default ...", Confidence = 0.99 },
        new SuggestionCandidate { Rank = 2, InsertText = "FOREIGN KEY (user_id) REFERENCES yangzhuoran_exam_users (id)", DisplayText = "FOREIGN KEY ...", Confidence = 0.8 }
    ]
};
var rankedCandidates = SqlContextAnalyzer.RankCandidates(foreignKeyProbe, unrankedCandidates);
Check("foreign key candidate outranks repeated field", rankedCandidates.InsertText.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase));
Check("foreign key context always has three fallback candidates", rankedCandidates.Candidates.Count == 3);
Check("all strict foreign-key candidates stay syntax-compatible", rankedCandidates.Candidates.All(candidate => candidate.InsertText.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)));
Check("candidate cycling wraps locally", rankedCandidates.SelectCandidate(3).ActiveCandidateIndex == 0);

var candidatePrompt = PromptTemplates.BuildCandidateContinuationPrompt(foreignKeyProbe);
Check("candidate prompt requests exactly three choices", candidatePrompt.Contains("恰好 3", StringComparison.Ordinal));
Check("candidate prompt carries inferred foreign-key state", candidatePrompt.Contains("ForeignKeyConstraint", StringComparison.Ordinal));
Check("candidate prompt forbids repeating is_default", candidatePrompt.Contains("is_default", StringComparison.Ordinal));
var fullSqlAnswerPrompt = PromptTemplates.BuildFullSqlAnswerPrompt(foreignKeyProbe);
Check("full-answer SQL prompt asks for a complete clipboard answer instead of a short continuation",
    fullSqlAnswerPrompt.Contains("任务模式：full_sql_answer", StringComparison.Ordinal) &&
    fullSqlAnswerPrompt.Contains("完整、可单独人工检查和执行", StringComparison.Ordinal) &&
    fullSqlAnswerPrompt.Contains("full_answer", StringComparison.Ordinal) &&
    fullSqlAnswerPrompt.Contains("<CURSOR>", StringComparison.Ordinal));
var clipboardQuestionPrompt = PromptTemplates.BuildClipboardQuestionAnswerPrompt(
    "查询当前最新月份销售额最高的公司");
var clipboardQuestionSystemPrompt = PromptTemplates.BuildClipboardQuestionSystemPrompt(new AppSettings());
var clipboardDatabaseSystemPrompt = PromptTemplates.BuildDatabaseToolSystemPrompt(
    new AppSettings(),
    databaseCatalog,
    clipboardQuestionSystemPrompt);
Check("clipboard question prompt requests a paste-ready full answer and permits autonomous read-only facts",
    clipboardQuestionPrompt.Contains("任务模式：clipboard_question_answer", StringComparison.Ordinal) &&
    clipboardQuestionPrompt.Contains("查询当前最新月份销售额最高的公司", StringComparison.Ordinal) &&
    clipboardQuestionPrompt.Contains("自主使用只读数据库工具", StringComparison.Ordinal) &&
    clipboardQuestionSystemPrompt.Contains("question_type=sql", StringComparison.Ordinal) &&
    clipboardDatabaseSystemPrompt.Contains("剪贴板题目作答器", StringComparison.Ordinal) &&
    clipboardDatabaseSystemPrompt.Contains("query_mysql_readonly", StringComparison.Ordinal));

var formerlyTruncatedDocument = "FULL_DOCUMENT_START\n" + new string('x', 150_000) + "\nFULL_DOCUMENT_END";
var formerlyTruncatedPrompt = PromptTemplates.BuildCompletionPrompt(new ProbeResult
{
    Text = formerlyTruncatedDocument,
    CaretOffset = formerlyTruncatedDocument.Length / 2
});
Check("documents below the DeepSeek-safe limit are sent in full",
    formerlyTruncatedPrompt.Contains("FULL_DOCUMENT_START", StringComparison.Ordinal) &&
    formerlyTruncatedPrompt.Contains("FULL_DOCUMENT_END", StringComparison.Ordinal) &&
    !formerlyTruncatedPrompt.Contains("模型安全上下文而省略", StringComparison.Ordinal));

var oversizedDocument = "OVERSIZED_START\n" +
                        new string('l', 499_980) +
                        "\nCARET_ZONE\n" +
                        new string('r', 499_980) +
                        "\nOVERSIZED_END";
var oversizedCaret = oversizedDocument.IndexOf("CARET_ZONE", StringComparison.Ordinal) + "CARET_ZONE".Length;
var oversizedPrompt = PromptTemplates.BuildCompletionPrompt(new ProbeResult
{
    Text = oversizedDocument,
    CaretOffset = oversizedCaret
});
Check("oversized completion context keeps the exact cursor", oversizedPrompt.Contains("CARET_ZONE<CURSOR>", StringComparison.Ordinal));
Check("oversized completion context is cropped on both sides",
    !oversizedPrompt.Contains("OVERSIZED_START", StringComparison.Ordinal) &&
    !oversizedPrompt.Contains("OVERSIZED_END", StringComparison.Ordinal) &&
    oversizedPrompt.Contains("/* 前方", StringComparison.Ordinal) &&
    oversizedPrompt.Contains("/* 后方", StringComparison.Ordinal));
Check("completion context uses the 800k-character DeepSeek-safe budget",
    PromptTemplates.CompletionDocumentCharacterLimit == 800_000 && oversizedPrompt.Length < 805_000);

var settingsTestRoot = Path.Combine(Path.GetTempPath(), "Assistant-Settings-SelfTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(settingsTestRoot, "config"));
File.WriteAllText(Path.Combine(settingsTestRoot, "config", "settings.json"), """
    {
      "SettingsVersion": 2,
      "VisionApiBaseUrl": "https://old-provider.invalid",
      "VisionModel": "glm-4.6v",
      "OverlayEnabled": true,
      "TaskbarPanelEnabled": false,
      "TaskbarWidth": 9999,
      "MobileAccessToken": "self-test-access-token-12345"
    }
    """);
try
{
    var migratedSettings = new SettingsService(settingsTestRoot);
    Check("legacy overlay users migrate to the taskbar channel", migratedSettings.Current.TaskbarPanelEnabled);
    Check("taskbar width is clamped to the supported range", migratedSettings.Current.TaskbarWidth == 1400);
    Check("global assistant hotkey defaults to Ctrl+Alt+S", migratedSettings.Current.ToggleAssistantHotkey == "Ctrl+Alt+S");
    Check("core hotkeys and database settings migrate to the unified defaults",
        migratedSettings.Current.SettingsVersion == 12 &&
        migratedSettings.Current.TaskbarFloatingHeight == 160 &&
        migratedSettings.Current.WordExerciseHotkey == "Ctrl+Alt+A" &&
        migratedSettings.Current.SmartActionHotkey == "Ctrl+Alt+A" &&
        migratedSettings.Current.CopyFullAnswerHotkey == "Ctrl+Alt+C" &&
        migratedSettings.Current.AnswerClipboardQuestionHotkey == "Ctrl+Alt+V" &&
        migratedSettings.Current.VisionScreenshotHotkey == "Ctrl+Alt+O" &&
        migratedSettings.Current.GuidedTypingHotkey == "Ctrl+Alt+D" &&
        migratedSettings.Current.ToggleMobileHotkey == "Ctrl+Alt+M" &&
        migratedSettings.Current.ToggleMobilePrivacyHotkey == "Ctrl+Alt+P" &&
        migratedSettings.Current.DatabaseConnection.ProfileId == "default" &&
        migratedSettings.Current.FallbackEnabled &&
        migratedSettings.Current.FallbackModel == "glm-5.2" &&
        migratedSettings.Current.FallbackApiBaseUrl == "https://open.bigmodel.cn/api/paas/v4");
    Check("settings never serialize a database password",
        !File.ReadAllText(migratedSettings.SettingsPath).Contains("DatabasePassword", StringComparison.OrdinalIgnoreCase));
    Check("legacy OCR provider is dropped on migration",
        !File.ReadAllText(migratedSettings.SettingsPath).Contains("old-provider.invalid", StringComparison.Ordinal));
    Check("removed overlay settings are not written back",
        !File.ReadAllText(migratedSettings.SettingsPath).Contains("OverlayEnabled", StringComparison.Ordinal));
}
finally
{
    Directory.Delete(settingsTestRoot, recursive: true);
}

long taskbarExtendedStyle = 0;
uint taskbarCaptureAffinity = 0;
var taskbarPageCount = 1;
var taskbarAdvancedToSecondPage = false;
Exception? taskbarStyleError = null;
var taskbarStyleThread = new Thread(() =>
{
    try
    {
        var taskbarWindow = new TaskbarSuggestionWindow();
        var handle = new WindowInteropHelper(taskbarWindow).EnsureHandle();
        taskbarExtendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        taskbarWindow.Width = 240;
        taskbarWindow.Height = 44;
        taskbarWindow.UpdateSuggestion(
            SuggestionResult.Info(new string('长', 400)),
            new DisplayStyle { FontSize = 11, LineHeight = 15 },
            240);
        taskbarWindow.Show();
        NativeMethods.GetWindowDisplayAffinity(handle, out taskbarCaptureAffinity);
        taskbarWindow.UpdateLayout();
        taskbarAdvancedToSecondPage = taskbarWindow.ChangePage(1);
        taskbarPageCount = taskbarWindow.PageCount;
        taskbarWindow.Close();
    }
    catch (Exception ex)
    {
        taskbarStyleError = ex;
    }
});
taskbarStyleThread.SetApartmentState(ApartmentState.STA);
taskbarStyleThread.Start();
taskbarStyleThread.Join();
Check("taskbar tool-window style can be created without showing UI", taskbarStyleError is null);
Check("visible taskbar answer uses Windows capture exclusion",
    taskbarCaptureAffinity == NativeMethods.WdaExcludeFromCapture);
Check("taskbar suggestion is excluded from app switching",
    (taskbarExtendedStyle & NativeMethods.WsExToolWindow) != 0 &&
    (taskbarExtendedStyle & NativeMethods.WsExNoActivate) != 0 &&
    (taskbarExtendedStyle & NativeMethods.WsExAppWindow) == 0);
Check("taskbar WPF layout exposes a second page for long content",
    taskbarPageCount > 1 &&
    taskbarAdvancedToSecondPage);
Check("taskbar pagination counts complete visual pages and tolerates invalid layout values",
    TaskbarSuggestionWindow.CalculatePageCount(75, 30) == 3 &&
    TaskbarSuggestionWindow.CalculatePageCount(30, 30) == 1 &&
    TaskbarSuggestionWindow.CalculatePageCount(double.NaN, 30) == 1 &&
    TaskbarSuggestionWindow.CalculatePageCount(75, 0) == 1);

var exclusiveHookInstalled = false;
Exception? exclusiveHookError = null;
var exclusiveHookThread = new Thread(() =>
{
    try
    {
        var window = new System.Windows.Window();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        using var service = new GlobalHotkeyService();
        var errors = service.Register(handle, new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.ToggleGuidedTyping] = "Ctrl+Shift+F21",
            [HotkeyAction.CaptureVisionExercise] = "Ctrl+Shift+F22"
        });
        // A second registration for the OCR combination must fail until disposal.
        bool TryReserveOcr() => NativeMethods.RegisterHotKey(handle, 7982, 0x0006, 0x85);
        var reservedWhileRunning = !TryReserveOcr();
        NativeMethods.UnregisterHotKey(handle, 7982);
        exclusiveHookInstalled = service.UsesExclusiveInterception && errors.Count == 0 && reservedWhileRunning;
        service.Dispose();
        exclusiveHookInstalled &= TryReserveOcr();
        NativeMethods.UnregisterHotKey(handle, 7982);
        window.Close();
    }
    catch (Exception ex)
    {
        exclusiveHookError = ex;
    }
});
exclusiveHookThread.SetApartmentState(ApartmentState.STA);
exclusiveHookThread.Start();
exclusiveHookThread.Join();
Check("exclusive global hotkey hook installs and releases on a dedicated Windows message thread",
    exclusiveHookError is null && exclusiveHookInstalled);

var nativeEditorText = new string('w', 12_000);
string nativeReadText = string.Empty;
var nativeReadSucceeded = false;
var nativeReadTruncated = true;
Exception? nativeReadError = null;
var nativeReadThread = new Thread(() =>
{
    try
    {
        using var textBox = new System.Windows.Forms.TextBox
        {
            Multiline = true,
            Text = nativeEditorText
        };
        nativeReadSucceeded = NativeMethods.TryReadControlText(
            textBox.Handle,
            20_000,
            out nativeReadText,
            out nativeReadTruncated);
    }
    catch (Exception ex)
    {
        nativeReadError = ex;
    }
});
nativeReadThread.SetApartmentState(ApartmentState.STA);
nativeReadThread.Start();
nativeReadThread.Join();
Check("native editor text reader handles content beyond the 4096-character UIA cap",
    nativeReadError is null && nativeReadSucceeded && !nativeReadTruncated && nativeReadText == nativeEditorText);
Check("UI Automation Name payloads at 4096 characters are treated as incomplete",
    !NavicatProbeService.IsLikelyUiAutomationNameTruncated(4095) &&
    NavicatProbeService.IsLikelyUiAutomationNameTruncated(4096));

var longUnicodeText = new string('a', 4095) + "😀" + new string('中', 5000);
var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(longUnicodeText)));
Check("chunked content hash matches SHA256 for Unicode text", ContentHasher.Sha256(longUnicodeText) == expectedHash);

var loggerTestDirectory = Path.Combine(Path.GetTempPath(), "NavicatTextProbe-SelfTests", Guid.NewGuid().ToString("N"));
string loggerTestPath;
using (var logger = new ProbeLogger(loggerTestDirectory))
{
    loggerTestPath = logger.LogPath;
    logger.WriteSessionStarted();
    logger.WriteConfiguration(new AppSettings
    {
        DatabaseConnection = new DatabaseConnectionSettings
        {
            Enabled = true,
            ProfileId = "PRIVATE_PROFILE_ID",
            Host = "PRIVATE_DATABASE_HOST",
            Username = "PRIVATE_DATABASE_USER"
        }
    });
    logger.WriteDatabaseContext(false, 0, false, "PRIVATE_DATABASE_EXCEPTION users SELECT secret");
    logger.WriteClipboardAnswer("navicat_sql", "完整 SQL", 321, true);
    logger.WriteState(new ProbeResult
    {
        Status = ProbeStatus.Captured,
        Text = "state records must not repeat SQL"
    });
    logger.WriteCapture(new ProbeResult
    {
        Status = ProbeStatus.Captured,
        Text = "SELECT 1;",
        ContentHash = "logger-test",
        Elements =
        [
            new ElementDiagnostic(0, "Edit", "TEEAutoCompleteMemo", "", "Win32",
                "SELECT 1;\n" + new string('x', 500), [])
        ]
    });
    logger.WriteSuggestion(
        "word_exercise_locate_300",
        locateContext.ContextCharacterCount,
        123,
        locatedPayload with
        {
            PromptTokens = 321,
            PromptCacheHitTokens = 200,
            PromptCacheMissTokens = 121,
            CompletionTokens = 88
        },
        locateContext);
    logger.WriteVisionCapture(visionCapture with
    {
        FullImageBytes = Encoding.UTF8.GetBytes("VISION_SECRET_PIXELS"),
        DetailImageBytes = Encoding.UTF8.GetBytes("VISION_DETAIL_SECRET")
    }, 45);
    for (var index = 0; index < 200; index++)
    {
        logger.WriteDiagnostic("bounded-queue-stress", index.ToString());
    }
}

var loggerLines = File.ReadAllLines(loggerTestPath)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonDocument.Parse(line))
    .ToArray();
try
{
    Check("bounded background logger drains entries on dispose", loggerLines.Any(document => document.RootElement.GetProperty("eventType").GetString() == "session_ended"));
    var stateData = loggerLines.First(document => document.RootElement.GetProperty("eventType").GetString() == "state").RootElement.GetProperty("data");
    Check("state logs omit full SQL text", !stateData.TryGetProperty("Text", out _) && !stateData.TryGetProperty("text", out _));
    var serializedDatabaseLogs = string.Join('\n', loggerLines
        .Where(document => document.RootElement.GetProperty("eventType").GetString() is "configuration" or "database_context")
        .Select(document => document.RootElement.GetRawText()));
    Check("database logs omit connection identity and exception contents",
        !serializedDatabaseLogs.Contains("PRIVATE_PROFILE_ID", StringComparison.Ordinal) &&
        !serializedDatabaseLogs.Contains("PRIVATE_DATABASE_HOST", StringComparison.Ordinal) &&
        !serializedDatabaseLogs.Contains("PRIVATE_DATABASE_USER", StringComparison.Ordinal) &&
        !serializedDatabaseLogs.Contains("PRIVATE_DATABASE_EXCEPTION", StringComparison.Ordinal));
    var serializedClipboardLog = loggerLines.First(document =>
        document.RootElement.GetProperty("eventType").GetString() == "clipboard_answer").RootElement.GetRawText();
    Check("clipboard logs retain only metadata and never the answer body",
        serializedClipboardLog.Contains("characterCount", StringComparison.Ordinal) &&
        serializedClipboardLog.Contains("321", StringComparison.Ordinal) &&
        !serializedClipboardLog.Contains("SELECT * FROM", StringComparison.Ordinal));
    var captureData = loggerLines.First(document => document.RootElement.GetProperty("eventType").GetString() == "capture").RootElement.GetProperty("data");
    Check("debounced capture logs retain full SQL", captureData.GetProperty("Text").GetString() == "SELECT 1;");
    Check("element diagnostics do not duplicate full editor text", captureData.GetProperty("Elements")[0].GetProperty("Name").GetString()!.Length < 240);
    var wordSuggestionData = loggerLines.First(document =>
        document.RootElement.GetProperty("eventType").GetString() == "suggestion" &&
        document.RootElement.GetProperty("data").GetProperty("trigger").GetString() == "word_exercise_locate_300").RootElement.GetProperty("data");
    Check("Word AI logs include adaptive-window and token metadata",
        wordSuggestionData.GetProperty("wordRadius").GetInt32() == WordExerciseContextBuilder.FinalExpandedRadius &&
        wordSuggestionData.GetProperty("wordExpansionLevel").GetInt32() == 2 &&
        wordSuggestionData.GetProperty("PromptTokens").GetInt32() == 321 &&
        wordSuggestionData.GetProperty("PromptCacheHitTokens").GetInt32() == 200);
    Check("Word AI logs omit full canonical questions and detected user answers",
        !wordSuggestionData.TryGetProperty("QuestionText", out _) &&
        !wordSuggestionData.TryGetProperty("DetectedUserAnswer", out _) &&
        wordSuggestionData.GetProperty("questionTextCharacters").GetInt32() > 0 &&
        wordSuggestionData.GetProperty("detectedUserAnswerCharacters").GetInt32() > 0);
    var visionCaptureLine = loggerLines.First(document =>
        document.RootElement.GetProperty("eventType").GetString() == "vision_capture");
    var serializedVisionLog = visionCaptureLine.RootElement.GetRawText();
    Check("vision logs retain only metadata and never image bytes or Base64",
        !serializedVisionLog.Contains("VISION_SECRET_PIXELS", StringComparison.Ordinal) &&
        !serializedVisionLog.Contains("VISION_DETAIL_SECRET", StringComparison.Ordinal) &&
        serializedVisionLog.Contains("fullImageBytes", StringComparison.Ordinal));
}
finally
{
    foreach (var document in loggerLines) document.Dispose();
    Directory.Delete(loggerTestDirectory, recursive: true);
}

await using var mobileServer = new MobileServerService();
var mobilePage = (string)typeof(MobileServerService)
    .GetMethod("BuildPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
    .Invoke(null, new object[] { "self-test-token" })!;
Check("mobile page exposes the full Word question, full answer and current continuation",
    mobilePage.Contains("id=\"question\"", StringComparison.Ordinal) &&
    mobilePage.Contains("id=\"fullAnswer\"", StringComparison.Ordinal) &&
    mobilePage.Contains("当前光标续写", StringComparison.Ordinal) &&
    mobilePage.Contains("智能分析当前内容快捷键", StringComparison.Ordinal) &&
    mobilePage.Contains("屏幕 OCR 快捷键", StringComparison.Ordinal) &&
    mobilePage.Contains("完整答案复制快捷键", StringComparison.Ordinal) &&
    mobilePage.Contains("剪贴板题目回答快捷键", StringComparison.Ordinal) &&
    mobilePage.Contains("mainLight", StringComparison.Ordinal) &&
    mobilePage.Contains("databaseLight", StringComparison.Ordinal) &&
    mobilePage.Contains("typingLight", StringComparison.Ordinal));
Check("mobile page is PWA-installable and supports the privacy black screen",
    mobilePage.Contains(@"rel=""manifest""", StringComparison.Ordinal) &&
    mobilePage.Contains("/manifest.webmanifest?token=self-test-token", StringComparison.Ordinal) &&
    mobilePage.Contains(@"rel=""apple-touch-icon""", StringComparison.Ordinal) &&
    mobilePage.Contains(@"id=""privacyOverlay""", StringComparison.Ordinal) &&
    mobilePage.Contains("$('privacyOverlay').hidden=!x.privacyMode;", StringComparison.Ordinal) &&
    mobilePage.Contains("serviceWorker.register", StringComparison.Ordinal) &&
    mobilePage.Contains("手机页隐私黑屏快捷键", StringComparison.Ordinal) &&
    !mobilePage.Contains("__ASSISTANT_TOKEN__", StringComparison.Ordinal));
var manifestJson = JsonSerializer.Deserialize<JsonElement>(MobileServerService.BuildManifestJson("self-test-token"));
Check("PWA manifest points at the tokenized start URL and both icon sizes",
    manifestJson.GetProperty("display").GetString() == "standalone" &&
    manifestJson.GetProperty("start_url").GetString() == "/?token=self-test-token" &&
    manifestJson.GetProperty("icons").GetArrayLength() == 2);
var iconBytes = MobileServerService.RenderAppIconPng(192);
Check("PWA icon renders a valid PNG at the requested size",
    iconBytes.Length > 100 &&
    iconBytes[0] == 137 && iconBytes[1] == 80 && iconBytes[2] == 78 && iconBytes[3] == 71 &&
    MobileServerService.RenderAppIconPng(512).Length > iconBytes.Length);
Check("PWA service worker activates without caching the tokenized API",
    MobileServerService.BuildServiceWorkerScript().Contains("self.skipWaiting", StringComparison.Ordinal) &&
    MobileServerService.BuildServiceWorkerScript().Contains("clients.claim", StringComparison.Ordinal));
var accessUrls = mobileServer.GetAccessUrls(new AppSettings { MobileAccessToken = "self-test-token", MobilePort = 17832 });
Check("localhost is the first desktop URL", new Uri(accessUrls[0]).Host == "127.0.0.1");
var preferredPhoneHost = accessUrls.Skip(1).Select(url => new Uri(url).Host).FirstOrDefault();
Check("APIPA is never selected as the preferred phone URL", preferredPhoneHost is null || !preferredPhoneHost.StartsWith("169.254.", StringComparison.Ordinal));

var privacyProbe = new TcpListener(IPAddress.Loopback, 0);
privacyProbe.Start();
var privacyPort = ((IPEndPoint)privacyProbe.LocalEndpoint).Port;
privacyProbe.Stop();
var pwaSettings = new AppSettings
{
    MobileAccessToken = "pwa-self-test-token",
    MobilePort = privacyPort,
    MobileServerEnabled = true
};
var pwaServer = new MobileServerService();
var pwaServerStarted = await pwaServer.StartAsync(pwaSettings);
using var http = new HttpClient();
var stateUrl = $"http://127.0.0.1:{privacyPort}/api/state?token=pwa-self-test-token";
var privacyBefore = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(stateUrl))
    .GetProperty("privacyMode")
    .GetBoolean();
pwaServer.PrivacyMode = true;
var privacyAfter = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(stateUrl))
    .GetProperty("privacyMode")
    .GetBoolean();
var pwaPage = await http.GetStringAsync($"http://127.0.0.1:{privacyPort}/?token=pwa-self-test-token");
var pwaManifest = await http.GetStringAsync($"http://127.0.0.1:{privacyPort}/manifest.webmanifest?token=pwa-self-test-token");
var pwaIcon = await http.GetByteArrayAsync($"http://127.0.0.1:{privacyPort}/icon-512.png?token=pwa-self-test-token");
var pwaServiceWorker = await http.GetStringAsync($"http://127.0.0.1:{privacyPort}/sw.js?token=pwa-self-test-token");
Check("mobile state exposes the privacy flag and toggles live",
    pwaServerStarted && !privacyBefore && privacyAfter);
Check("PWA endpoints serve page, manifest, icon and service worker over the phone port",
    pwaPage.Contains("privacyOverlay", StringComparison.Ordinal) &&
    pwaPage.Contains("manifest.webmanifest", StringComparison.Ordinal) &&
    pwaManifest.Contains("standalone", StringComparison.Ordinal) &&
    pwaManifest.Contains("pwa-self-test-token", StringComparison.Ordinal) &&
    pwaIcon.Length > 100 && pwaIcon[0] == 137 &&
    pwaServiceWorker.Contains("skipWaiting", StringComparison.Ordinal));
await pwaServer.DisposeAsync();

if (failures.Count == 0)
{
    Console.WriteLine($"Preferred phone host: {preferredPhoneHost ?? "none"}");
    Console.WriteLine("All self-tests passed.");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine($"FAILED: {failure}");
return 1;

internal sealed class RecordingHttpMessageHandler(string responseBody) : HttpMessageHandler
{
    public string RequestBody { get; private set; } = string.Empty;
    public string? AuthorizationScheme { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class SequenceHttpMessageHandler(params string[] responseBodies) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responseBodies);

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("No queued self-test response", Encoding.UTF8, "text/plain")
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class FallbackHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _behaviors = new();

    public List<string> RequestUrls { get; } = [];

    public List<string?> AuthorizationValues { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public void QueueResponse(string responseBody) =>
        _behaviors.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        }));

    public void QueueStatus(HttpStatusCode status, string responseBody) =>
        _behaviors.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/plain")
        }));

    public void QueueTimeout() =>
        _behaviors.Enqueue((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("self-test timeout")));

    public void QueueTransportFailure() =>
        _behaviors.Enqueue((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("self-test connection refused")));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
        AuthorizationValues.Add(request.Headers.Authorization?.ToString());
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_behaviors.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("No queued self-test behavior", Encoding.UTF8, "text/plain")
            };
        }
        return await _behaviors.Dequeue()(request, cancellationToken);
    }
}
