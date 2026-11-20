namespace NavicatTextProbe.Models;

public enum SuggestionMode
{
    Completion,
    Diagnosis,
    Information,
    Error,
    ExerciseAnswer,
    ExerciseContinuation
}

public sealed record SuggestionCandidate
{
    public int Rank { get; init; }
    public string DisplayText { get; init; } = string.Empty;
    public string InsertText { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public double Confidence { get; init; }
}

public sealed record SuggestionResult
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public SuggestionMode Mode { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
    public string InsertText { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string QuestionType { get; init; } = string.Empty;
    public bool IsQuestion { get; init; }
    public bool ContextComplete { get; init; } = true;
    public string NeedsMoreContext { get; init; } = "none";
    public bool SameAsCurrentQuestion { get; init; } = true;
    public string QuestionNumber { get; init; } = string.Empty;
    public string ActiveSubquestionNumber { get; init; } = string.Empty;
    public string QuestionStartBlockId { get; init; } = string.Empty;
    public string QuestionEndBlockId { get; init; } = string.Empty;
    public string QuestionStartAnchor { get; init; } = string.Empty;
    public string QuestionEndAnchor { get; init; } = string.Empty;
    public string QuestionText { get; init; } = string.Empty;
    public string AnswerState { get; init; } = string.Empty;
    public string DetectedUserAnswer { get; init; } = string.Empty;
    public string FullAnswer { get; init; } = string.Empty;
    public IReadOnlyList<string> Issues { get; init; } = [];
    public double Confidence { get; init; }
    public int PromptTokens { get; init; }
    public int PromptCacheHitTokens { get; init; }
    public int PromptCacheMissTokens { get; init; }
    public int CompletionTokens { get; init; }
    public bool IsLoading { get; init; }
    public IReadOnlyList<SuggestionCandidate> Candidates { get; init; } = [];
    public int ActiveCandidateIndex { get; init; }

    public bool HasCandidates => Candidates.Count > 0;

    internal bool IsScreenResult => Origin is "vision_screenshot" or "clipboard_screen_ocr";
    internal string FullSuggestionText => IsScreenResult && !string.IsNullOrWhiteSpace(FullAnswer) ? FullAnswer : InsertText;

    public SuggestionResult SelectCandidate(int index)
    {
        if (Candidates.Count == 0)
        {
            return this;
        }

        var normalizedIndex = ((index % Candidates.Count) + Candidates.Count) % Candidates.Count;
        var candidate = Candidates[normalizedIndex];
        return this with
        {
            DisplayText = candidate.DisplayText,
            InsertText = candidate.InsertText,
            Explanation = candidate.Explanation,
            Confidence = candidate.Confidence,
            ActiveCandidateIndex = normalizedIndex
        };
    }

    public static SuggestionResult Loading(string message = "正在分析当前 SQL……") => new()
    {
        Mode = SuggestionMode.Information,
        DisplayText = message,
        IsLoading = true
    };

    public static SuggestionResult Info(string message) => new()
    {
        Mode = SuggestionMode.Information,
        DisplayText = message
    };

    public static SuggestionResult Error(string message, string explanation = "") => new()
    {
        Mode = SuggestionMode.Error,
        DisplayText = message,
        Explanation = explanation
    };
}
