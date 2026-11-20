using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class DeepSeekVisionClient : IDisposable
{
    internal const string Model = "deepseek-flash";
    internal const string Endpoint = "https://api.deepseek.com/chat/completions";
    private const double MinimumAnswerConfidence = 0.45;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DeepSeekVisionClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<SuggestionResult> AnswerExerciseAsync(
        AppSettings settings,
        string apiKey,
        VisionCaptureSnapshot capture,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return SuggestionResult.Error("请在主窗口输入 DeepSeek OCR 密钥并点击应用");
        }
        if (!capture.IsCaptured)
        {
            return SuggestionResult.Error(capture.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = VisionExercisePromptTemplates.BuildSystemPrompt(settings)
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        ImageContent(capture.FullImageMediaType, capture.FullImageBytes),
                        ImageContent(capture.DetailImageMediaType, capture.DetailImageBytes),
                        new
                        {
                            type = "text",
                            text = VisionExercisePromptTemplates.BuildUserPrompt(capture)
                        }
                    }
                }
            },
            thinking = new { type = "disabled" },
            max_tokens = 1800,
            stream = false,
            tools = new[] { BuildAnswerTool() },
            tool_choice = new { type = "function", function = new { name = "return_exercise_answer" } }
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return SuggestionResult.Error(
                    $"DeepSeek 截图识别失败（HTTP {(int)response.StatusCode}）",
                    CompactError(responseBody));
            }

            using var envelope = JsonDocument.Parse(responseBody);
            var message = envelope.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            var payloadJson = ReadToolArguments(message) ?? ReadContent(message);
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return SuggestionResult.Error("DeepSeek 没有返回可解析的截图结果");
            }

            var usage = envelope.RootElement.TryGetProperty("usage", out var usageElement)
                ? usageElement
                : default;
            return ParsePayload(payloadJson, capture.HasReliableCaret, usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SuggestionResult.Error("连接 DeepSeek 视觉模型失败", ex.Message);
        }
    }

    internal SuggestionResult ParsePayload(string payloadJson, bool hasReliableCaret, JsonElement usage = default)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<VisionExercisePayload>(
                StripCodeFence(payloadJson),
                _jsonOptions);
            if (payload is null)
            {
                return SuggestionResult.Error("无法解析 DeepSeek 返回的 JSON");
            }

            var recognizedText = payload.QuestionText?.Trim() ?? string.Empty;
            var answerState = hasReliableCaret ? NormalizeAnswerState(payload.AnswerState) : "uncertain";
            var confidence = Math.Clamp(payload.Confidence, 0, 1);
            var fullAnswer = payload.FullAnswer?.Trim() ?? string.Empty;
            var insertText = answerState is "empty" or "partial"
                ? payload.InsertText?.TrimEnd() ?? string.Empty
                : string.Empty;
            var hasAnswer = !payload.TargetAmbiguous && payload.IsQuestion && confidence >= MinimumAnswerConfidence &&
                            (fullAnswer.Length > 0 || insertText.Length > 0 || answerState == "complete");
            var display = hasAnswer
                ? CompactDisplay(answerState switch
                {
                    "uncertain" => fullAnswer,
                    "partial" when !string.IsNullOrWhiteSpace(insertText) => insertText,
                    _ => payload.DisplayText
                })
                : CompactDisplay(recognizedText);
            if (payload.TargetAmbiguous)
            {
                display = "看到多道题，请点进目标题目，或只留一道完整题目再截图。";
            }
            else if (display.Length == 0)
            {
                display = hasAnswer
                    ? fullAnswer.Length > 0 ? CompactDisplay(fullAnswer) : "当前文字已写完整"
                    : "没有识别到清楚的文字，请放大后重试";
            }

            return new SuggestionResult
            {
                Mode = hasAnswer ? SuggestionMode.ExerciseAnswer : SuggestionMode.Information,
                Origin = "vision_screenshot",
                IsQuestion = hasAnswer,
                ContextComplete = hasAnswer && answerState != "uncertain",
                QuestionType = NormalizeQuestionType(payload.QuestionType),
                QuestionNumber = string.IsNullOrWhiteSpace(payload.QuestionNumber)
                    ? string.Empty
                    : WordExerciseContextBuilder.NormalizeQuestionNumber(payload.QuestionNumber),
                ActiveSubquestionNumber = WordExerciseContextBuilder.NormalizeSubquestionNumber(payload.ActiveSubquestionNumber),
                QuestionText = recognizedText,
                AnswerState = answerState,
                DetectedUserAnswer = answerState == "uncertain" ? string.Empty : payload.DetectedUserAnswerBeforeCursor?.Trim() ?? string.Empty,
                DisplayText = display,
                InsertText = hasAnswer ? insertText : string.Empty,
                FullAnswer = hasAnswer ? fullAnswer : string.Empty,
                Explanation = hasAnswer ? payload.Explanation?.Trim() ?? string.Empty : string.Empty,
                Confidence = confidence,
                PromptTokens = ReadUsageInt(usage, "prompt_tokens"),
                CompletionTokens = ReadUsageInt(usage, "completion_tokens")
            };
        }
        catch (Exception ex)
        {
            return SuggestionResult.Error("无法解析 DeepSeek 返回的 JSON", ex.Message);
        }
    }

    private static object ImageContent(string mediaType, byte[] bytes) => new
    {
        type = "image_url",
        image_url = new
        {
            url = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"
        }
    };

    private static object BuildAnswerTool() => new
    {
        type = "function",
        function = new
        {
            name = "return_exercise_answer",
            description = "返回截图原文以及可用的答案或续写。",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["is_question"] = new { type = "boolean" },
                    ["target_ambiguous"] = new { type = "boolean" },
                    ["question_type"] = new { type = "string", @enum = new[] { "choice", "fill_blank", "short_answer", "unknown" } },
                    ["question_number"] = new { type = "string" },
                    ["active_subquestion_number"] = new { type = "string" },
                    ["question_text"] = new { type = "string" },
                    ["answer_state"] = new { type = "string", @enum = new[] { "empty", "partial", "complete", "uncertain" } },
                    ["detected_user_answer_before_cursor"] = new { type = "string" },
                    ["display_text"] = new { type = "string" },
                    ["insert_text"] = new { type = "string" },
                    ["full_answer"] = new { type = "string" },
                    ["explanation"] = new { type = "string" },
                    ["confidence"] = new { type = "number", minimum = 0, maximum = 1 }
                },
                required = new[]
                {
                    "is_question",
                    "target_ambiguous",
                    "question_type",
                    "question_number",
                    "active_subquestion_number",
                    "question_text",
                    "answer_state",
                    "detected_user_answer_before_cursor",
                    "display_text",
                    "insert_text",
                    "full_answer",
                    "explanation",
                    "confidence"
                },
                additionalProperties = false
            }
        }
    };

    private static string? ReadToolArguments(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.TryGetProperty("function", out var function) &&
                function.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.String)
            {
                return arguments.GetString();
            }
        }

        return null;
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join(string.Empty, content.EnumerateArray()
                .Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString()));
        }
        return null;
    }

    private static string StripCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine >= 0)
        {
            text = text[(firstNewLine + 1)..];
        }
        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }
        return text.Trim();
    }

    private static string NormalizeQuestionType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "choice" => "choice",
        "fill_blank" or "fillblank" => "fill_blank",
        "short_answer" or "shortanswer" => "short_answer",
        _ => "unknown"
    };

    private static string NormalizeAnswerState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "empty" => "empty",
        "partial" => "partial",
        "complete" => "complete",
        _ => "uncertain"
    };

    private static string CompactDisplay(string? value)
    {
        var text = DocumentContextFormatter.Compact(value ?? string.Empty);
        return text.Length <= 100 ? text : text[..97] + "…";
    }

    private static string CompactError(string responseBody)
    {
        var text = responseBody.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500] + "…";
    }

    private static int ReadUsageInt(JsonElement usage, string propertyName) =>
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    public void Dispose() => _httpClient.Dispose();

    private sealed record VisionExercisePayload
    {
        [JsonPropertyName("is_question")]
        public bool IsQuestion { get; init; }

        [JsonPropertyName("target_ambiguous")]
        public bool TargetAmbiguous { get; init; }

        [JsonPropertyName("question_type")]
        public string? QuestionType { get; init; }

        [JsonPropertyName("question_number")]
        public string? QuestionNumber { get; init; }

        [JsonPropertyName("active_subquestion_number")]
        public string? ActiveSubquestionNumber { get; init; }

        [JsonPropertyName("question_text")]
        public string? QuestionText { get; init; }

        [JsonPropertyName("answer_state")]
        public string? AnswerState { get; init; }

        [JsonPropertyName("detected_user_answer_before_cursor")]
        public string? DetectedUserAnswerBeforeCursor { get; init; }

        [JsonPropertyName("display_text")]
        public string? DisplayText { get; init; }

        [JsonPropertyName("insert_text")]
        public string? InsertText { get; init; }

        [JsonPropertyName("full_answer")]
        public string? FullAnswer { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }
}
