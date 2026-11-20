using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class DeepSeekClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public DeepSeekClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SuggestionResult> CompleteAsync(
        AppSettings settings,
        string apiKey,
        string userPrompt,
        CancellationToken cancellationToken,
        int maxTokens = 500,
        double temperature = 0.2,
        string? systemPrompt = null,
        string? fallbackApiKey = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return SuggestionResult.Error("尚未配置 DeepSeek API Key");
        }

        var resolvedSystemPrompt = systemPrompt ?? PromptTemplates.BuildSystemPrompt(settings);
        var primary = await AttemptProviderAsync(
            "DeepSeek",
            settings.ApiBaseUrl,
            settings.Model,
            apiKey,
            userPrompt,
            resolvedSystemPrompt,
            maxTokens,
            temperature,
            cancellationToken);
        if (!primary.TransportFailure)
        {
            return primary.Result!;
        }

        // DeepSeek 请求超时或网络不可达：用同一个智谱 Key 切换到 GLM 冗余模型重试一次。
        if (!settings.FallbackEnabled ||
            string.IsNullOrWhiteSpace(settings.FallbackModel) ||
            string.IsNullOrWhiteSpace(settings.FallbackApiBaseUrl))
        {
            return SuggestionResult.Error("连接 DeepSeek 失败", primary.TransportError ?? string.Empty);
        }

        var fallbackKey = ResolveFallbackApiKey(fallbackApiKey);
        if (string.IsNullOrWhiteSpace(fallbackKey))
        {
            return SuggestionResult.Error("连接 DeepSeek 失败", primary.TransportError ?? string.Empty);
        }

        var fallback = await AttemptProviderAsync(
            "智谱 GLM",
            settings.FallbackApiBaseUrl,
            settings.FallbackModel,
            fallbackKey,
            userPrompt,
            resolvedSystemPrompt,
            maxTokens,
            temperature,
            cancellationToken);
        if (fallback.TransportFailure)
        {
            return SuggestionResult.Error(
                "DeepSeek 请求失败且智谱 GLM 冗余也失败",
                $"{primary.TransportError}；{fallback.TransportError}");
        }

        var result = fallback.Result!;
        var fallbackNotice = $"DeepSeek 请求超时或不可达，已自动切换智谱 {settings.FallbackModel} 完成本次回答。";
        return result with
        {
            Origin = string.IsNullOrWhiteSpace(result.Origin)
                ? "zhipu_fallback"
                : $"{result.Origin}_zhipu_fallback",
            Explanation = string.IsNullOrWhiteSpace(result.Explanation)
                ? fallbackNotice
                : $"{fallbackNotice}{result.Explanation}"
        };
    }

    private async Task<ProviderAttempt> AttemptProviderAsync(
        string providerLabel,
        string baseUrl,
        string model,
        string apiKey,
        string userPrompt,
        string systemPrompt,
        int maxTokens,
        double temperature,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendCompletionRequestAsync(
                providerLabel,
                baseUrl,
                model,
                apiKey,
                userPrompt,
                systemPrompt,
                maxTokens,
                temperature,
                cancellationToken);
            return new ProviderAttempt(result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout 到期：提供方没有在时限内响应，视为冗余切换条件。
            return new ProviderAttempt(null, $"{providerLabel} 请求超时（{ex.Message}）");
        }
        catch (HttpRequestException ex)
        {
            return new ProviderAttempt(null, $"{providerLabel} 连接失败（{ex.Message}）");
        }
        catch (Exception ex)
        {
            return new ProviderAttempt(SuggestionResult.Error($"连接 {providerLabel} 失败", ex.Message), null);
        }
    }

    private async Task<SuggestionResult> SendCompletionRequestAsync(
        string providerLabel,
        string baseUrl,
        string model,
        string apiKey,
        string userPrompt,
        string systemPrompt,
        int maxTokens,
        double temperature,
        CancellationToken cancellationToken)
    {
        var endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            thinking = new { type = "disabled" },
            temperature = Math.Clamp(temperature, 0, 1),
            max_tokens = Math.Clamp(maxTokens, 100, 4000),
            response_format = new { type = "json_object" },
            stream = false
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return SuggestionResult.Error(
                $"{providerLabel} 请求失败：{(int)response.StatusCode}",
                CompactError(responseBody));
        }

        using var envelope = JsonDocument.Parse(responseBody);
        var content = envelope.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            return SuggestionResult.Error($"{providerLabel} 返回了空内容");
        }

        var usage = envelope.RootElement.TryGetProperty("usage", out var usageElement)
            ? usageElement
            : default;
        return ParseSuggestion(content, usage);
    }

    private static string? ResolveFallbackApiKey(string? fallbackApiKey)
    {
        // 显式传入的 Key 优先；显式空白表示“不启用冗余”，避免误读本机凭据。
        if (fallbackApiKey is not null)
        {
            return string.IsNullOrWhiteSpace(fallbackApiKey) ? null : fallbackApiKey;
        }

        try
        {
            // 未显式传入时读取为 GLM 备用模型保存的凭据。
            return WindowsCredentialService.ReadZhipuApiKey();
        }
        catch
        {
            return null;
        }
    }

    public async Task<DatabaseAssistedCompletionResult> CompleteWithDatabaseToolsAsync(
        AppSettings settings,
        string apiKey,
        string userPrompt,
        DatabaseCatalogSnapshot catalog,
        Func<DatabaseToolRequest, int, CancellationToken, Task<DatabaseContextResult>> executeQueryAsync,
        CancellationToken cancellationToken,
        int maxTokens = 700,
        double temperature = 0.1,
        string? baseSystemPrompt = null)
    {
        const int maximumToolCalls = 2;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new DatabaseAssistedCompletionResult(
                SuggestionResult.Error("尚未配置 DeepSeek API Key"),
                0,
                0,
                "数据库工具未启用：缺少 DeepSeek API Key。");
        }

        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = PromptTemplates.BuildDatabaseToolSystemPrompt(settings, catalog, baseSystemPrompt)
            },
            new { role = "user", content = userPrompt }
        };
        var endpoint = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
        var toolCallCount = 0;
        var successfulQueryCount = 0;
        var promptTokens = 0;
        var promptCacheHitTokens = 0;
        var promptCacheMissTokens = 0;
        var completionTokens = 0;

        try
        {
            for (var round = 0; round <= maximumToolCalls; round++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = JsonContent.Create(new
                {
                    model = settings.Model,
                    messages = messages.ToArray(),
                    thinking = new { type = "disabled" },
                    temperature = Math.Clamp(temperature, 0, 1),
                    max_tokens = Math.Clamp(maxTokens, 100, 4000),
                    response_format = new { type = "json_object" },
                    tools = new[] { BuildReadOnlyDatabaseTool() },
                    tool_choice = toolCallCount >= maximumToolCalls ? "none" : "auto",
                    stream = false
                });

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new DatabaseAssistedCompletionResult(
                        SuggestionResult.Error(
                            $"DeepSeek 数据库工具请求失败：{(int)response.StatusCode}",
                            CompactError(responseBody)),
                        toolCallCount,
                        successfulQueryCount,
                        "DeepSeek 数据库工具请求失败。");
                }

                using var envelope = JsonDocument.Parse(responseBody);
                var root = envelope.RootElement;
                var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
                promptTokens += ReadUsageInt(usage, "prompt_tokens");
                promptCacheHitTokens += ReadUsageInt(usage, "prompt_cache_hit_tokens");
                promptCacheMissTokens += ReadUsageInt(usage, "prompt_cache_miss_tokens");
                completionTokens += ReadUsageInt(usage, "completion_tokens");
                var message = root.GetProperty("choices")[0].GetProperty("message");
                var toolCalls = ReadDatabaseToolCalls(message);
                if (toolCalls.Count == 0)
                {
                    var content = ReadMessageContent(message);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        return new DatabaseAssistedCompletionResult(
                            SuggestionResult.Error("DeepSeek 返回了空内容"),
                            toolCallCount,
                            successfulQueryCount,
                            "数据库工具会话没有返回最终建议。");
                    }

                    var suggestion = ParseSuggestion(content, default) with
                    {
                        Origin = successfulQueryCount > 0 ? "database_grounded" : "database_catalog",
                        PromptTokens = promptTokens,
                        PromptCacheHitTokens = promptCacheHitTokens,
                        PromptCacheMissTokens = promptCacheMissTokens,
                        CompletionTokens = completionTokens
                    };
                    if (successfulQueryCount > 0)
                    {
                        suggestion = suggestion with
                        {
                            Explanation = string.IsNullOrWhiteSpace(suggestion.Explanation)
                                ? $"AI 自主完成 {successfulQueryCount} 次只读数据库查询。"
                                : $"{suggestion.Explanation} AI 自主完成 {successfulQueryCount} 次只读数据库查询。"
                        };
                    }
                    return new DatabaseAssistedCompletionResult(
                        suggestion,
                        toolCallCount,
                        successfulQueryCount,
                        successfulQueryCount > 0
                            ? $"AI 自主执行了 {successfulQueryCount} 次只读查询。"
                            : toolCallCount > 0
                                ? "AI 发起的实时只读查询未成功，已使用缓存目录降级回答。"
                                : "AI 已参考缓存数据库目录，本次无需实时查询。");
                }

                messages.Add(new
                {
                    role = "assistant",
                    content = ReadMessageContent(message),
                    tool_calls = toolCalls.Select(call => new
                    {
                        id = call.Id,
                        type = "function",
                        function = new { name = call.Name, arguments = call.Arguments }
                    }).ToArray()
                });

                foreach (var call in toolCalls)
                {
                    DatabaseContextResult toolResult;
                    if (toolCallCount >= maximumToolCalls)
                    {
                        toolResult = DatabaseContextResult.Failure("已达到每次补全最多 2 次实时只读查询的本地上限。");
                    }
                    else
                    {
                        toolCallCount++;
                        var requestNumber = toolCallCount;
                        var parsed = ParseDatabaseToolRequest(call);
                        toolResult = parsed is null
                            ? DatabaseContextResult.Failure("数据库工具参数无效或工具名称不受支持。")
                            : await executeQueryAsync(parsed, requestNumber, cancellationToken);
                        if (toolResult.IsSuccess) successfulQueryCount++;
                    }

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = call.Id,
                        content = BuildDatabaseToolResult(toolResult)
                    });
                }
            }

            return new DatabaseAssistedCompletionResult(
                SuggestionResult.Error("数据库工具调用次数超过本地上限"),
                toolCallCount,
                successfulQueryCount,
                "数据库工具未在限定轮次内返回最终建议。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DatabaseAssistedCompletionResult(
                SuggestionResult.Error("连接 DeepSeek 数据库工具失败", ex.Message),
                toolCallCount,
                successfulQueryCount,
                "数据库工具会话发生异常。");
        }
    }

    private static object BuildReadOnlyDatabaseTool() => new
    {
        type = "function",
        function = new
        {
            name = "query_mysql_readonly",
            description = "仅当当前 SQL 补全确实需要目录中没有的精确 Schema 细节或实时业务数据时，执行一条 MySQL 只读查询。可查询账号能看到的任意业务库；普通语法补全、表字段已在目录中、或无需实时数据时不要调用。查询结果有超时、行数、字节数和敏感列掩码限制。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        description = "一条最小必要的 SELECT、WITH...SELECT、SHOW、DESCRIBE 或 EXPLAIN。SELECT 应显式限制返回行数。"
                    },
                    purpose = new
                    {
                        type = "string",
                        description = "说明为什么缓存目录不足、这次查询将为当前光标补全取得什么事实。"
                    }
                },
                required = new[] { "query", "purpose" },
                additionalProperties = false
            }
        }
    };

    private DatabaseToolRequest? ParseDatabaseToolRequest(DatabaseToolCall call)
    {
        if (!call.Name.Equals("query_mysql_readonly", StringComparison.Ordinal)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<DatabaseToolArguments>(call.Arguments, _jsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Query)) return null;
            return new DatabaseToolRequest(
                payload.Query.Trim(),
                payload.Purpose?.Trim() ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DatabaseToolCall> ReadDatabaseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<DatabaseToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("id", out var id) ||
                !call.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var name) ||
                !function.TryGetProperty("arguments", out var arguments) ||
                id.ValueKind != JsonValueKind.String ||
                name.ValueKind != JsonValueKind.String ||
                arguments.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            result.Add(new DatabaseToolCall(
                id.GetString() ?? string.Empty,
                name.GetString() ?? string.Empty,
                arguments.GetString() ?? string.Empty));
        }
        return result;
    }

    private static string? ReadMessageContent(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;

    private static string BuildDatabaseToolResult(DatabaseContextResult result) => JsonSerializer.Serialize(new
    {
        untrusted_database_result = true,
        ok = result.IsSuccess,
        message = result.Message,
        executed_query = result.Query,
        rows = result.RowsSummary,
        row_count = result.RowCount,
        truncated = result.WasTruncated
    });

    private SuggestionResult ParseSuggestion(string content, JsonElement usage)
    {
        var payload = JsonSerializer.Deserialize<SuggestionPayload>(StripCodeFence(content), _jsonOptions);
        if (payload is null)
        {
            return SuggestionResult.Error("无法解析 DeepSeek 的 JSON 结果");
        }

        var mode = payload.Mode?.Trim().ToLowerInvariant() switch
        {
            "diagnosis" => SuggestionMode.Diagnosis,
            "exercise_answer" => SuggestionMode.ExerciseAnswer,
            "exercise_continuation" => SuggestionMode.ExerciseContinuation,
            "not_exercise" => SuggestionMode.Information,
            _ => SuggestionMode.Completion
        };
        var issues = (payload.Issues ?? [])
            .OrderBy(IssuePriority)
            .ToArray();
        var displayText = mode == SuggestionMode.Diagnosis && issues.Length > 0
            ? CompactDisplay(issues[0])
            : CompactDisplay(payload.DisplayText);
        if (string.IsNullOrWhiteSpace(displayText) &&
            mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation)
        {
            displayText = CompactDisplay(string.IsNullOrWhiteSpace(payload.FullAnswer)
                ? payload.InsertText
                : payload.FullAnswer);
        }
        var candidates = (payload.Candidates ?? [])
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.InsertText) ||
                !string.IsNullOrWhiteSpace(candidate.DisplayText))
            .OrderBy(candidate => candidate.Rank <= 0 ? int.MaxValue : candidate.Rank)
            .ThenByDescending(candidate => candidate.Confidence)
            .Select((candidate, index) => new SuggestionCandidate
            {
                Rank = index + 1,
                DisplayText = CompactDisplay(candidate.DisplayText),
                InsertText = candidate.InsertText?.TrimEnd() ?? string.Empty,
                Explanation = candidate.Explanation?.Trim() ?? string.Empty,
                Confidence = Math.Clamp(candidate.Confidence, 0, 1)
            })
            .GroupBy(candidate => NormalizeSuggestion(candidate.InsertText.Length > 0
                ? candidate.InsertText
                : candidate.DisplayText), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(3)
            .Select((candidate, index) => candidate with { Rank = index + 1 })
            .ToArray();

        if (candidates.Length > 0)
        {
            displayText = candidates[0].DisplayText;
        }

        var result = new SuggestionResult
        {
            Mode = mode,
            DisplayText = displayText,
            InsertText = candidates.Length > 0 ? candidates[0].InsertText : payload.InsertText?.TrimEnd() ?? string.Empty,
            Explanation = candidates.Length > 0 ? candidates[0].Explanation : payload.Explanation?.Trim() ?? string.Empty,
            QuestionType = mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation
                ? NormalizeQuestionType(payload.QuestionType)
                : string.Empty,
            IsQuestion = payload.IsQuestion ??
                mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation,
            ContextComplete = payload.ContextComplete ?? true,
            NeedsMoreContext = NormalizeNeedsMoreContext(payload.NeedsMoreContext),
            SameAsCurrentQuestion = payload.SameAsCurrentQuestion ?? true,
            QuestionNumber = payload.QuestionNumber?.Trim() ?? string.Empty,
            ActiveSubquestionNumber = payload.ActiveSubquestionNumber?.Trim() ?? string.Empty,
            QuestionStartBlockId = payload.QuestionStartBlockId?.Trim() ?? string.Empty,
            QuestionEndBlockId = payload.QuestionEndBlockId?.Trim() ?? string.Empty,
            QuestionStartAnchor = payload.QuestionStartAnchor?.Trim() ?? string.Empty,
            QuestionEndAnchor = payload.QuestionEndAnchor?.Trim() ?? string.Empty,
            QuestionText = payload.QuestionText?.Trim() ?? string.Empty,
            AnswerState = NormalizeAnswerState(payload.AnswerState),
            DetectedUserAnswer = payload.DetectedUserAnswer?.Trim() ?? string.Empty,
            FullAnswer = payload.FullAnswer?.Trim() ?? string.Empty,
            Issues = issues,
            Confidence = candidates.Length > 0 ? candidates[0].Confidence : Math.Clamp(payload.Confidence, 0, 1),
            Candidates = candidates,
            PromptTokens = ReadUsageInt(usage, "prompt_tokens"),
            PromptCacheHitTokens = ReadUsageInt(usage, "prompt_cache_hit_tokens"),
            PromptCacheMissTokens = ReadUsageInt(usage, "prompt_cache_miss_tokens"),
            CompletionTokens = ReadUsageInt(usage, "completion_tokens")
        };
        return candidates.Length > 0 ? result.SelectCandidate(0) : result;
    }

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine >= 0) text = text[(firstNewLine + 1)..];
        if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        return text.Trim();
    }

    private static string CompactDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var singleLine = string.Join(' ', text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= 100 ? singleLine : singleLine[..97] + "…";
    }

    private static string CompactError(string responseBody)
    {
        var text = responseBody.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500] + "…";
    }

    private static string NormalizeSuggestion(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeQuestionType(string? questionType) => questionType?.Trim().ToLowerInvariant() switch
    {
        "sql" => "sql",
        "choice" => "choice",
        "fill_blank" or "fillblank" => "fill_blank",
        "short_answer" or "shortanswer" => "short_answer",
        _ => "unknown"
    };

    private static string NormalizeNeedsMoreContext(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "before" => "before",
        "after" => "after",
        "both" => "both",
        _ => "none"
    };

    private static string NormalizeAnswerState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "empty" => "empty",
        "partial" => "partial",
        "complete" => "complete",
        _ => "uncertain"
    };

    private static int ReadUsageInt(JsonElement usage, string propertyName) =>
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static int IssuePriority(string issue)
    {
        if (issue.Contains("P0", StringComparison.OrdinalIgnoreCase)) return 0;
        if (issue.Contains("P1", StringComparison.OrdinalIgnoreCase)) return 1;
        if (issue.Contains("P2", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record SuggestionPayload
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("display_text")]
        public string? DisplayText { get; init; }

        [JsonPropertyName("insert_text")]
        public string? InsertText { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("question_type")]
        public string? QuestionType { get; init; }

        [JsonPropertyName("is_question")]
        public bool? IsQuestion { get; init; }

        [JsonPropertyName("context_complete")]
        public bool? ContextComplete { get; init; }

        [JsonPropertyName("needs_more_context")]
        public string? NeedsMoreContext { get; init; }

        [JsonPropertyName("same_as_current_question")]
        public bool? SameAsCurrentQuestion { get; init; }

        [JsonPropertyName("question_number")]
        public string? QuestionNumber { get; init; }

        [JsonPropertyName("active_subquestion_number")]
        public string? ActiveSubquestionNumber { get; init; }

        [JsonPropertyName("question_start_block_id")]
        public string? QuestionStartBlockId { get; init; }

        [JsonPropertyName("question_end_block_id")]
        public string? QuestionEndBlockId { get; init; }

        [JsonPropertyName("question_start_anchor")]
        public string? QuestionStartAnchor { get; init; }

        [JsonPropertyName("question_end_anchor")]
        public string? QuestionEndAnchor { get; init; }

        [JsonPropertyName("question_text")]
        public string? QuestionText { get; init; }

        [JsonPropertyName("answer_state")]
        public string? AnswerState { get; init; }

        [JsonPropertyName("detected_user_answer")]
        public string? DetectedUserAnswer { get; init; }

        [JsonPropertyName("full_answer")]
        public string? FullAnswer { get; init; }

        [JsonPropertyName("issues")]
        public IReadOnlyList<string>? Issues { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("candidates")]
        public IReadOnlyList<CandidatePayload>? Candidates { get; init; }
    }

    private sealed record CandidatePayload
    {
        [JsonPropertyName("rank")]
        public int Rank { get; init; }

        [JsonPropertyName("display_text")]
        public string? DisplayText { get; init; }

        [JsonPropertyName("insert_text")]
        public string? InsertText { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }

    private sealed record DatabaseToolCall(string Id, string Name, string Arguments);

    private sealed record ProviderAttempt(SuggestionResult? Result, string? TransportError)
    {
        public bool TransportFailure => Result is null && !string.IsNullOrWhiteSpace(TransportError);
    }

    private sealed record DatabaseToolArguments
    {
        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("purpose")]
        public string? Purpose { get; init; }
    }
}
