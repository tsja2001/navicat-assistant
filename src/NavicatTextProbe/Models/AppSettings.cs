namespace NavicatTextProbe.Models;

public sealed record DisplayStyle
{
    public double FontSize { get; set; } = 16;
    public double LineHeight { get; set; } = 24;
    public string FontColor { get; set; } = "#F8F9F9";
    public string BackgroundColor { get; set; } = "#17202A";
    public double BackgroundOpacity { get; set; } = 0.82;
}

public sealed record DatabaseConnectionSettings
{
    public string ProfileId { get; set; } = "default";
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "当前 MySQL";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Preferred";
    public int ConnectionTimeoutSeconds { get; set; } = 5;
    public int QueryTimeoutSeconds { get; set; } = 5;
    public int MaximumRows { get; set; } = 50;
    public int MaximumResultBytes { get; set; } = 65_536;
}

public sealed record AppSettings
{
    public int SettingsVersion { get; set; }
    public bool AssistantEnabled { get; set; } = true;
    public bool AiEnabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public bool VisionEnabled { get; set; } = true;
    public bool FallbackEnabled { get; set; } = true;
    public string FallbackApiBaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
    public string FallbackModel { get; set; } = "glm-5.2";
    public string VisionCaptureScope { get; set; } = "CurrentMonitor";
    public string CustomGlobalPrompt { get; set; } = string.Empty;
    public int PredictionDebounceMilliseconds { get; set; } = 850;
    public int PredictionMinimumIntervalMilliseconds { get; set; } = 1400;
    public bool TaskbarPanelEnabled { get; set; } = true;
    public double TaskbarWidth { get; set; } = 640;
    public double TaskbarFloatingHeight { get; set; } = 160;
    public bool MobileServerEnabled { get; set; }
    public bool KeyboardFallbackEnabled { get; set; }
    public bool GuidedTypingEnabled { get; set; } = true;
    public int MobilePort { get; set; } = 17832;
    public string MobileAccessToken { get; set; } = string.Empty;
    public string ToggleAssistantHotkey { get; set; } = "Ctrl+Alt+S";
    public string SmartActionHotkey { get; set; } = "Ctrl+Alt+A";
    public string CopyFullAnswerHotkey { get; set; } = "Ctrl+Alt+C";
    public string AnswerClipboardQuestionHotkey { get; set; } = "Ctrl+Alt+V";
    public string GuidedTypingHotkey { get; set; } = "Ctrl+Alt+D";
    public string ToggleMobileHotkey { get; set; } = "Ctrl+Alt+M";
    public string ToggleMobilePrivacyHotkey { get; set; } = "Ctrl+Alt+P";
    public string CycleCandidatesHotkey { get; set; } = "Ctrl+Alt+R";
    public string AnalyzeSelectionHotkey { get; set; } = "Ctrl+Alt+E";
    public string WordExerciseHotkey { get; set; } = "Ctrl+Alt+A";
    public string VisionScreenshotHotkey { get; set; } = "Ctrl+Alt+O";
    public DatabaseConnectionSettings DatabaseConnection { get; set; } = new();
    public DisplayStyle TaskbarStyle { get; set; } = new()
    {
        FontSize = 11,
        LineHeight = 15,
        BackgroundColor = "#202020",
        BackgroundOpacity = 0.96
    };
    public DisplayStyle MobileStyle { get; set; } = new()
    {
        FontSize = 18,
        LineHeight = 28,
        FontColor = "#7DF2B1",
        BackgroundColor = "#151D27",
        BackgroundOpacity = 0.96
    };
}
