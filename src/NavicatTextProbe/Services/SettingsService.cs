using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class SettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService(string assistantRoot)
    {
        ConfigDirectory = Path.Combine(assistantRoot, "config");
        SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
        Directory.CreateDirectory(ConfigDirectory);
        Current = Load();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, _jsonOptions));
    }

    public string ConfigDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        EnsureDefaults(settings);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
        Current = settings;
        Changed?.Invoke(this, Current);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    EnsureDefaults(settings, ReadLegacyOverlayEnabled(json));
                    return settings;
                }
            }
        }
        catch
        {
            // A malformed file should not prevent the assistant from starting.
        }

        var defaults = new AppSettings();
        EnsureDefaults(defaults, legacyOverlayEnabled: false);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(defaults, _jsonOptions));
        return defaults;
    }

    private static void EnsureDefaults(AppSettings settings, bool legacyOverlayEnabled = false)
    {
        if (settings.SettingsVersion < 2)
        {
            if (settings.PredictionDebounceMilliseconds == 650)
            {
                settings.PredictionDebounceMilliseconds = 850;
            }

            if (settings.PredictionMinimumIntervalMilliseconds <= 0)
            {
                settings.PredictionMinimumIntervalMilliseconds = 1400;
            }

            if (Math.Abs(settings.TaskbarStyle.FontSize - 14) < 0.01 &&
                Math.Abs(settings.TaskbarStyle.LineHeight - 21) < 0.01 &&
                string.Equals(settings.TaskbarStyle.BackgroundColor, "#17202A", StringComparison.OrdinalIgnoreCase))
            {
                settings.TaskbarStyle.FontSize = 11;
                settings.TaskbarStyle.LineHeight = 15;
                settings.TaskbarStyle.BackgroundColor = "#202020";
                settings.TaskbarStyle.BackgroundOpacity = 0.96;
            }

            settings.SettingsVersion = 2;
        }

        if (settings.SettingsVersion < 3)
        {
            // Version 3 removes the floating overlay. Preserve an existing visible output by
            // moving users who only enabled that overlay onto the taskbar panel.
            if (legacyOverlayEnabled)
            {
                settings.TaskbarPanelEnabled = true;
            }

            settings.SettingsVersion = 3;
        }

        if (settings.SettingsVersion < 4)
        {
            settings.WordExerciseHotkey = string.IsNullOrWhiteSpace(settings.WordExerciseHotkey)
                ? "Ctrl+Alt+A"
                : settings.WordExerciseHotkey;
            settings.SettingsVersion = 4;
        }

        if (settings.SettingsVersion < 5)
        {
            settings.VisionEnabled = true;
            settings.VisionScreenshotHotkey = "Ctrl+Shift+C";
            settings.SettingsVersion = 5;
        }

        if (settings.SettingsVersion < 6)
        {
            settings.SmartActionHotkey = string.IsNullOrWhiteSpace(settings.WordExerciseHotkey)
                ? "Ctrl+Alt+A"
                : settings.WordExerciseHotkey;
            settings.GuidedTypingHotkey = "Ctrl+Alt+D";
            settings.ToggleMobileHotkey = "Ctrl+Alt+M";
            if (string.Equals(settings.VisionScreenshotHotkey, "Ctrl+Shift+C", StringComparison.OrdinalIgnoreCase))
            {
                settings.VisionScreenshotHotkey = "Ctrl+Alt+O";
            }
            settings.GuidedTypingEnabled = true;
            settings.VisionCaptureScope = "CurrentMonitor";
            settings.DatabaseConnection ??= new DatabaseConnectionSettings();
            settings.SettingsVersion = 6;
        }

        if (settings.SettingsVersion < 7)
        {
            settings.CopyFullAnswerHotkey = "Ctrl+Alt+C";
            settings.SettingsVersion = 7;
        }

        if (settings.SettingsVersion < 8)
        {
            settings.AnswerClipboardQuestionHotkey = "Ctrl+Alt+V";
            settings.SettingsVersion = 8;
        }

        if (settings.SettingsVersion < 9)
        {
            settings.FallbackEnabled = true;
            settings.FallbackApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4";
            settings.FallbackModel = "glm-5.2";
            settings.SettingsVersion = 9;
        }

        if (settings.SettingsVersion < 10)
        {
            settings.ToggleMobilePrivacyHotkey = "Ctrl+Alt+P";
            settings.SettingsVersion = 10;
        }

        // Version 11 uses the DeepSeek Flash endpoint for screen OCR. Obsolete
        // vision provider fields are omitted when legacy settings are serialized.
        if (settings.SettingsVersion < 11) settings.SettingsVersion = 11;
        if (settings.SettingsVersion < 12) settings.SettingsVersion = 12;

        settings.ApiBaseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? "https://api.deepseek.com"
            : settings.ApiBaseUrl.TrimEnd('/');
        settings.Model = string.IsNullOrWhiteSpace(settings.Model)
            ? "deepseek-v4-flash"
            : settings.Model.Trim();
        settings.FallbackApiBaseUrl = string.IsNullOrWhiteSpace(settings.FallbackApiBaseUrl)
            ? "https://open.bigmodel.cn/api/paas/v4"
            : settings.FallbackApiBaseUrl.TrimEnd('/');
        settings.FallbackModel = string.IsNullOrWhiteSpace(settings.FallbackModel)
            ? "glm-5.2"
            : settings.FallbackModel.Trim();
        settings.VisionCaptureScope = string.Equals(
            settings.VisionCaptureScope,
            "ForegroundWindow",
            StringComparison.OrdinalIgnoreCase)
            ? "ForegroundWindow"
            : "CurrentMonitor";
        settings.SmartActionHotkey = string.IsNullOrWhiteSpace(settings.SmartActionHotkey)
            ? "Ctrl+Alt+A"
            : settings.SmartActionHotkey.Trim();
        settings.CopyFullAnswerHotkey = string.IsNullOrWhiteSpace(settings.CopyFullAnswerHotkey)
            ? "Ctrl+Alt+C"
            : settings.CopyFullAnswerHotkey.Trim();
        settings.AnswerClipboardQuestionHotkey = string.IsNullOrWhiteSpace(settings.AnswerClipboardQuestionHotkey)
            ? "Ctrl+Alt+V"
            : settings.AnswerClipboardQuestionHotkey.Trim();
        settings.GuidedTypingHotkey = string.IsNullOrWhiteSpace(settings.GuidedTypingHotkey)
            ? "Ctrl+Alt+D"
            : settings.GuidedTypingHotkey.Trim();
        settings.ToggleMobileHotkey = string.IsNullOrWhiteSpace(settings.ToggleMobileHotkey)
            ? "Ctrl+Alt+M"
            : settings.ToggleMobileHotkey.Trim();
        settings.ToggleMobilePrivacyHotkey = string.IsNullOrWhiteSpace(settings.ToggleMobilePrivacyHotkey)
            ? "Ctrl+Alt+P"
            : settings.ToggleMobilePrivacyHotkey.Trim();
        settings.WordExerciseHotkey = string.IsNullOrWhiteSpace(settings.WordExerciseHotkey)
            ? "Ctrl+Alt+A"
            : settings.WordExerciseHotkey.Trim();
        // Word exercise recognition is now one branch of the unified smart action.
        settings.WordExerciseHotkey = settings.SmartActionHotkey;
        settings.VisionScreenshotHotkey = string.IsNullOrWhiteSpace(settings.VisionScreenshotHotkey)
            ? "Ctrl+Alt+O"
            : settings.VisionScreenshotHotkey.Trim();
        settings.DatabaseConnection ??= new DatabaseConnectionSettings();
        settings.DatabaseConnection.ProfileId = string.IsNullOrWhiteSpace(settings.DatabaseConnection.ProfileId)
            ? "default"
            : settings.DatabaseConnection.ProfileId.Trim();
        settings.DatabaseConnection.DisplayName = string.IsNullOrWhiteSpace(settings.DatabaseConnection.DisplayName)
            ? "当前 MySQL"
            : settings.DatabaseConnection.DisplayName.Trim();
        settings.DatabaseConnection.Host = settings.DatabaseConnection.Host?.Trim() ?? string.Empty;
        settings.DatabaseConnection.Username = settings.DatabaseConnection.Username?.Trim() ?? string.Empty;
        settings.DatabaseConnection.Database = settings.DatabaseConnection.Database?.Trim() ?? string.Empty;
        settings.DatabaseConnection.SslMode = NormalizeDatabaseSslMode(settings.DatabaseConnection.SslMode);
        settings.DatabaseConnection.Port = Math.Clamp(settings.DatabaseConnection.Port, 1, 65535);
        settings.DatabaseConnection.ConnectionTimeoutSeconds = Math.Clamp(
            settings.DatabaseConnection.ConnectionTimeoutSeconds,
            2,
            30);
        settings.DatabaseConnection.QueryTimeoutSeconds = Math.Clamp(
            settings.DatabaseConnection.QueryTimeoutSeconds,
            2,
            30);
        settings.DatabaseConnection.MaximumRows = Math.Clamp(settings.DatabaseConnection.MaximumRows, 1, 200);
        settings.DatabaseConnection.MaximumResultBytes = Math.Clamp(
            settings.DatabaseConnection.MaximumResultBytes,
            8_192,
            262_144);
        settings.MobilePort = Math.Clamp(settings.MobilePort, 1024, 65535);
        settings.PredictionDebounceMilliseconds = Math.Clamp(settings.PredictionDebounceMilliseconds, 150, 5000);
        settings.PredictionMinimumIntervalMilliseconds = Math.Clamp(settings.PredictionMinimumIntervalMilliseconds, 500, 5000);
        settings.TaskbarWidth = Math.Clamp(settings.TaskbarWidth, 200, 1400);
        settings.TaskbarFloatingHeight = Math.Clamp(settings.TaskbarFloatingHeight, 30, 900);
        ClampStyle(settings.TaskbarStyle, 10, 36, 12, 54, 0);
        ClampStyle(settings.MobileStyle, 10, 48, 12, 72, 0.05);
        if (string.IsNullOrWhiteSpace(settings.MobileAccessToken) || settings.MobileAccessToken.Length < 20)
        {
            settings.MobileAccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }
    }

    private static bool ReadLegacyOverlayEnabled(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("OverlayEnabled", out var value) &&
                   value.ValueKind is JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static void ClampStyle(DisplayStyle style, double minFont, double maxFont, double minLine, double maxLine, double minOpacity)
    {
        style.FontSize = Math.Clamp(style.FontSize, minFont, maxFont);
        style.LineHeight = Math.Clamp(style.LineHeight, Math.Max(minLine, style.FontSize), maxLine);
        style.BackgroundOpacity = Math.Clamp(style.BackgroundOpacity, minOpacity, 1);
        style.BackgroundColor = string.IsNullOrWhiteSpace(style.BackgroundColor) ? "#17202A" : style.BackgroundColor.Trim();
        style.FontColor = string.IsNullOrWhiteSpace(style.FontColor) ? "#F8F9F9" : style.FontColor.Trim();
    }

    private static string NormalizeDatabaseSslMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "disabled" or "none" => "Disabled",
        "required" => "Required",
        "verifyca" => "VerifyCA",
        "verifyfull" => "VerifyFull",
        _ => "Preferred"
    };
}
