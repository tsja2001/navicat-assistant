using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal static class TaskbarSuggestionFormatter
{
    public static string BuildText(SuggestionResult suggestion)
    {
        var displayText = string.IsNullOrWhiteSpace(suggestion.DisplayText)
            ? "当前没有可靠建议"
            : suggestion.DisplayText.Trim();
        var body = suggestion.FullSuggestionText;
        if (suggestion.IsScreenResult && !string.IsNullOrWhiteSpace(body))
            return body;

        var expandedText = BuildExpandedExerciseText(suggestion, displayText);
        if (!string.IsNullOrWhiteSpace(body) && !expandedText.Contains(body, StringComparison.Ordinal))
            expandedText += Environment.NewLine + body;
        return suggestion.HasCandidates
            ? $"{suggestion.ActiveCandidateIndex + 1}/{suggestion.Candidates.Count}  {expandedText}"
            : expandedText;
    }

    private static string BuildExpandedExerciseText(SuggestionResult suggestion, string displayText)
    {
        if (suggestion.Origin == "clipboard_screen_ocr" ||
            (suggestion.Origin == "vision_screenshot" && string.IsNullOrWhiteSpace(suggestion.QuestionNumber)))
        {
            return displayText;
        }

        if (suggestion.Mode is not SuggestionMode.ExerciseAnswer and not SuggestionMode.ExerciseContinuation ||
            !suggestion.IsQuestion ||
            !suggestion.SameAsCurrentQuestion)
        {
            return displayText;
        }

        var questionType = WordExerciseContextBuilder.NormalizeQuestionType(suggestion.QuestionType);
        if (questionType == "choice")
        {
            return displayText;
        }

        var continuation = suggestion.Mode == SuggestionMode.ExerciseContinuation ||
                           string.Equals(suggestion.AnswerState, "partial", StringComparison.OrdinalIgnoreCase);
        var answer = WordExercisePresentation.SelectTaskbarAnswer(
            suggestion,
            questionType,
            suggestion.FullAnswer,
            suggestion.InsertText,
            continuation);
        return WordExercisePresentation.BuildExpandedDisplayText(
            answer,
            suggestion.InsertText,
            questionType,
            suggestion.QuestionNumber,
            continuation);
    }
}
