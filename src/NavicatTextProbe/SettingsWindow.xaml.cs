using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using NavicatTextProbe.Services;

namespace NavicatTextProbe;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _workingCopy;
    private readonly MySqlDatabaseService _databaseService = new();

    public SettingsWindow(
        AppSettings settings,
        bool hasSavedApiKey,
        bool hasSavedZhipuApiKey,
        bool hasSavedDatabasePassword)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (!NativeMethods.ExcludeFromScreenCapture(new WindowInteropHelper(this).Handle))
                Title = "Assistant 设置（录屏排除未生效）";
        };
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Application.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        _workingCopy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        ApiKeyStatusText.Text = hasSavedApiKey
            ? "Windows 凭据管理器中已经保存了一个 API Key。"
            : "尚未保存 API Key。请先撤销聊天中暴露的旧 Key，再在这里填写新 Key。";
        ZhipuApiKeyStatusText.Text = hasSavedZhipuApiKey
            ? "Windows 凭据管理器中已经保存了智谱 API Key。"
            : "尚未保存智谱备用模型密钥。";
        DatabasePasswordStatusText.Text = hasSavedDatabasePassword
            ? "Windows 凭据管理器中已保存此连接的密码；新密码留空会保留现有密码。"
            : "尚未保存数据库密码。密码不会写入 settings.json。";
        PopulateControls();
    }

    public AppSettings? UpdatedSettings { get; private set; }

    public string NewApiKey => ApiKeyPasswordBox.Password.Trim();

    public bool ClearApiKey => ClearApiKeyCheckBox.IsChecked == true;

    public string NewZhipuApiKey => ZhipuApiKeyPasswordBox.Password.Trim();

    public bool ClearZhipuApiKey => ClearZhipuApiKeyCheckBox.IsChecked == true;

    public string NewDatabasePassword => DatabasePasswordBox.Password;

    public bool ClearDatabasePassword => ClearDatabasePasswordCheckBox.IsChecked == true;

    private void PopulateControls()
    {
        AiEnabledCheckBox.IsChecked = _workingCopy.AiEnabled;
        ApiBaseUrlTextBox.Text = _workingCopy.ApiBaseUrl;
        ModelTextBox.Text = _workingCopy.Model;
        VisionEnabledCheckBox.IsChecked = _workingCopy.VisionEnabled;
        VisionCaptureScopeComboBox.SelectedValue = _workingCopy.VisionCaptureScope;
        FallbackEnabledCheckBox.IsChecked = _workingCopy.FallbackEnabled;
        FallbackApiBaseUrlTextBox.Text = _workingCopy.FallbackApiBaseUrl;
        FallbackModelTextBox.Text = _workingCopy.FallbackModel;
        GlobalPromptTextBox.Text = _workingCopy.CustomGlobalPrompt;
        DebounceTextBox.Text = _workingCopy.PredictionDebounceMilliseconds.ToString();
        MinimumIntervalTextBox.Text = _workingCopy.PredictionMinimumIntervalMilliseconds.ToString();

        AssistantEnabledCheckBox.IsChecked = _workingCopy.AssistantEnabled;
        TaskbarEnabledCheckBox.IsChecked = _workingCopy.TaskbarPanelEnabled;
        TaskbarWidthTextBox.Text = _workingCopy.TaskbarWidth.ToString("0.##");
        TaskbarHeightTextBox.Text = _workingCopy.TaskbarFloatingHeight.ToString("0.##");
        TaskbarFontSizeTextBox.Text = _workingCopy.TaskbarStyle.FontSize.ToString("0.##");
        TaskbarLineHeightTextBox.Text = _workingCopy.TaskbarStyle.LineHeight.ToString("0.##");
        TaskbarOpacitySlider.Value = _workingCopy.TaskbarStyle.BackgroundOpacity;
        TaskbarBackgroundTextBox.Text = _workingCopy.TaskbarStyle.BackgroundColor;
        TaskbarForegroundTextBox.Text = _workingCopy.TaskbarStyle.FontColor;

        MobileEnabledCheckBox.IsChecked = _workingCopy.MobileServerEnabled;
        MobilePortTextBox.Text = _workingCopy.MobilePort.ToString();
        MobileTokenTextBox.Text = _workingCopy.MobileAccessToken;
        MobileFontSizeTextBox.Text = _workingCopy.MobileStyle.FontSize.ToString("0.##");
        MobileLineHeightTextBox.Text = _workingCopy.MobileStyle.LineHeight.ToString("0.##");
        MobileOpacityTextBox.Text = _workingCopy.MobileStyle.BackgroundOpacity.ToString("0.##");
        MobileBackgroundTextBox.Text = _workingCopy.MobileStyle.BackgroundColor;
        MobileForegroundTextBox.Text = _workingCopy.MobileStyle.FontColor;
        KeyboardFallbackCheckBox.IsChecked = _workingCopy.KeyboardFallbackEnabled;
        GuidedTypingEnabledCheckBox.IsChecked = _workingCopy.GuidedTypingEnabled;
        AssistantHotkeyTextBox.Text = _workingCopy.ToggleAssistantHotkey;
        SmartActionHotkeyTextBox.Text = _workingCopy.SmartActionHotkey;
        CopyFullAnswerHotkeyTextBox.Text = _workingCopy.CopyFullAnswerHotkey;
        AnswerClipboardQuestionHotkeyTextBox.Text = _workingCopy.AnswerClipboardQuestionHotkey;
        GuidedTypingHotkeyTextBox.Text = _workingCopy.GuidedTypingHotkey;
        ToggleMobileHotkeyTextBox.Text = _workingCopy.ToggleMobileHotkey;
        ToggleMobilePrivacyHotkeyTextBox.Text = _workingCopy.ToggleMobilePrivacyHotkey;
        CandidateHotkeyTextBox.Text = _workingCopy.CycleCandidatesHotkey;
        VisionScreenshotHotkeyTextBox.Text = _workingCopy.VisionScreenshotHotkey;

        var database = _workingCopy.DatabaseConnection;
        DatabaseEnabledCheckBox.IsChecked = database.Enabled;
        DatabaseDisplayNameTextBox.Text = database.DisplayName;
        DatabaseHostTextBox.Text = database.Host;
        DatabasePortTextBox.Text = database.Port.ToString();
        DatabaseUsernameTextBox.Text = database.Username;
        DatabaseNameTextBox.Text = database.Database;
        DatabaseSslModeComboBox.SelectedValue = database.SslMode;
        DatabaseConnectTimeoutTextBox.Text = database.ConnectionTimeoutSeconds.ToString();
        DatabaseQueryTimeoutTextBox.Text = database.QueryTimeoutSeconds.ToString();
        DatabaseMaximumRowsTextBox.Text = database.MaximumRows.ToString();
        DatabaseMaximumKilobytesTextBox.Text = (database.MaximumResultBytes / 1024).ToString();
    }

    private sealed record DialogOwner(nint Handle) : System.Windows.Forms.IWin32Window;

    private void ChooseBackground_OnClick(object sender, RoutedEventArgs e)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true };
        var hex = TaskbarBackgroundTextBox.Text.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            picker.Color = System.Drawing.Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        if (picker.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;
        TaskbarBackgroundTextBox.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        if (TaskbarOpacitySlider.Value == 0) TaskbarOpacitySlider.Value = 1;
    }

    private void TransparentBackground_OnClick(object sender, RoutedEventArgs e) => TaskbarOpacitySlider.Value = 0;

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DebounceTextBox.Text, out var debounce) ||
            !int.TryParse(MinimumIntervalTextBox.Text, out var minimumInterval) ||
            !int.TryParse(MobilePortTextBox.Text, out var port) ||
            !double.TryParse(TaskbarWidthTextBox.Text, out var taskbarWidth) || !double.IsFinite(taskbarWidth) ||
            !double.TryParse(TaskbarHeightTextBox.Text, out var taskbarHeight) || !double.IsFinite(taskbarHeight) ||
            !double.TryParse(TaskbarFontSizeTextBox.Text, out var taskbarFontSize) ||
            !double.TryParse(TaskbarLineHeightTextBox.Text, out var taskbarLineHeight) ||
            !double.TryParse(MobileFontSizeTextBox.Text, out var mobileFontSize) ||
            !double.TryParse(MobileLineHeightTextBox.Text, out var mobileLineHeight) ||
            !double.TryParse(MobileOpacityTextBox.Text, out var mobileOpacity))
        {
            System.Windows.MessageBox.Show(this, "宽度、字号、行高、透明度、端口和等待时间必须填写有效数字。", "设置无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryApplyDatabaseControls(showMessage: true))
        {
            return;
        }

        _workingCopy.AiEnabled = AiEnabledCheckBox.IsChecked == true;
        _workingCopy.ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim();
        _workingCopy.Model = ModelTextBox.Text.Trim();
        _workingCopy.VisionEnabled = VisionEnabledCheckBox.IsChecked == true;
        _workingCopy.VisionCaptureScope = VisionCaptureScopeComboBox.SelectedValue?.ToString() ?? "CurrentMonitor";
        _workingCopy.FallbackEnabled = FallbackEnabledCheckBox.IsChecked == true;
        _workingCopy.FallbackApiBaseUrl = FallbackApiBaseUrlTextBox.Text.Trim();
        _workingCopy.FallbackModel = FallbackModelTextBox.Text.Trim();
        _workingCopy.CustomGlobalPrompt = GlobalPromptTextBox.Text;
        _workingCopy.PredictionDebounceMilliseconds = debounce;
        _workingCopy.PredictionMinimumIntervalMilliseconds = minimumInterval;
        _workingCopy.AssistantEnabled = AssistantEnabledCheckBox.IsChecked == true;
        _workingCopy.TaskbarPanelEnabled = TaskbarEnabledCheckBox.IsChecked == true;
        _workingCopy.TaskbarWidth = taskbarWidth;
        _workingCopy.TaskbarFloatingHeight = taskbarHeight;
        _workingCopy.TaskbarStyle.FontSize = taskbarFontSize;
        _workingCopy.TaskbarStyle.LineHeight = taskbarLineHeight;
        _workingCopy.TaskbarStyle.BackgroundOpacity = TaskbarOpacitySlider.Value;
        _workingCopy.TaskbarStyle.BackgroundColor = TaskbarBackgroundTextBox.Text.Trim();
        _workingCopy.TaskbarStyle.FontColor = TaskbarForegroundTextBox.Text.Trim();
        _workingCopy.MobileServerEnabled = MobileEnabledCheckBox.IsChecked == true;
        _workingCopy.MobilePort = port;
        _workingCopy.MobileAccessToken = MobileTokenTextBox.Text.Trim();
        _workingCopy.MobileStyle.FontSize = mobileFontSize;
        _workingCopy.MobileStyle.LineHeight = mobileLineHeight;
        _workingCopy.MobileStyle.BackgroundOpacity = mobileOpacity;
        _workingCopy.MobileStyle.BackgroundColor = MobileBackgroundTextBox.Text.Trim();
        _workingCopy.MobileStyle.FontColor = MobileForegroundTextBox.Text.Trim();
        _workingCopy.KeyboardFallbackEnabled = KeyboardFallbackCheckBox.IsChecked == true;
        _workingCopy.GuidedTypingEnabled = GuidedTypingEnabledCheckBox.IsChecked == true;
        _workingCopy.ToggleAssistantHotkey = AssistantHotkeyTextBox.Text.Trim();
        _workingCopy.SmartActionHotkey = SmartActionHotkeyTextBox.Text.Trim();
        _workingCopy.CopyFullAnswerHotkey = CopyFullAnswerHotkeyTextBox.Text.Trim();
        _workingCopy.AnswerClipboardQuestionHotkey = AnswerClipboardQuestionHotkeyTextBox.Text.Trim();
        _workingCopy.GuidedTypingHotkey = GuidedTypingHotkeyTextBox.Text.Trim();
        _workingCopy.ToggleMobileHotkey = ToggleMobileHotkeyTextBox.Text.Trim();
        _workingCopy.ToggleMobilePrivacyHotkey = ToggleMobilePrivacyHotkeyTextBox.Text.Trim();
        _workingCopy.CycleCandidatesHotkey = CandidateHotkeyTextBox.Text.Trim();
        _workingCopy.WordExerciseHotkey = _workingCopy.SmartActionHotkey;
        _workingCopy.VisionScreenshotHotkey = VisionScreenshotHotkeyTextBox.Text.Trim();

        var hotkeys = new[]
        {
            _workingCopy.ToggleAssistantHotkey,
            _workingCopy.SmartActionHotkey,
            _workingCopy.CopyFullAnswerHotkey,
            _workingCopy.AnswerClipboardQuestionHotkey,
            _workingCopy.VisionScreenshotHotkey,
            _workingCopy.GuidedTypingHotkey,
            _workingCopy.ToggleMobileHotkey,
            _workingCopy.ToggleMobilePrivacyHotkey,
            _workingCopy.CycleCandidatesHotkey,
            "Win+PageUp",
            "Win+PageDown"
        };
        if (!GlobalHotkeyService.TryValidateShortcutSet(hotkeys, out var hotkeyError))
        {
            System.Windows.MessageBox.Show(
                this,
                hotkeyError,
                "设置无法保存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        UpdatedSettings = _workingCopy;
        DialogResult = true;
    }

    private bool TryApplyDatabaseControls(bool showMessage)
    {
        if (!int.TryParse(DatabasePortTextBox.Text, out var port) ||
            !int.TryParse(DatabaseConnectTimeoutTextBox.Text, out var connectTimeout) ||
            !int.TryParse(DatabaseQueryTimeoutTextBox.Text, out var queryTimeout) ||
            !int.TryParse(DatabaseMaximumRowsTextBox.Text, out var maximumRows) ||
            !int.TryParse(DatabaseMaximumKilobytesTextBox.Text, out var maximumKilobytes))
        {
            if (showMessage)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "数据库端口、超时、最大行数和最大结果必须填写有效整数。",
                    "数据库设置无效",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }

        var database = _workingCopy.DatabaseConnection;
        database.Enabled = DatabaseEnabledCheckBox.IsChecked == true;
        database.DisplayName = DatabaseDisplayNameTextBox.Text.Trim();
        database.Host = DatabaseHostTextBox.Text.Trim();
        database.Port = Math.Clamp(port, 1, 65535);
        database.Username = DatabaseUsernameTextBox.Text.Trim();
        database.Database = DatabaseNameTextBox.Text.Trim();
        database.SslMode = DatabaseSslModeComboBox.SelectedValue?.ToString() ?? "Preferred";
        database.ConnectionTimeoutSeconds = Math.Clamp(connectTimeout, 2, 30);
        database.QueryTimeoutSeconds = Math.Clamp(queryTimeout, 2, 30);
        database.MaximumRows = Math.Clamp(maximumRows, 1, 200);
        database.MaximumResultBytes = (int)Math.Clamp((long)maximumKilobytes * 1024, 8_192L, 262_144L);
        if (database.Enabled &&
            (string.IsNullOrWhiteSpace(database.Host) ||
             string.IsNullOrWhiteSpace(database.Username)))
        {
            if (showMessage)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "启用数据库增强时，必须填写主机和用户名；优先数据库可以留空。",
                    "数据库设置无效",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
        return true;
    }

    private async void TestDatabaseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryApplyDatabaseControls(showMessage: true))
        {
            return;
        }

        var database = _workingCopy.DatabaseConnection;
        var password = DatabasePasswordBox.Password;
        if (string.IsNullOrEmpty(password) && ClearDatabasePasswordCheckBox.IsChecked != true)
        {
            try
            {
                password = WindowsCredentialService.ReadDatabasePassword(database.ProfileId) ?? string.Empty;
            }
            catch (Exception ex)
            {
                DatabaseTestStatusText.Text = $"读取凭据失败：{ex.Message}";
                return;
            }
        }
        if (string.IsNullOrEmpty(password))
        {
            DatabaseTestStatusText.Text = "请填写新密码，或先保存一个数据库密码。";
            return;
        }

        TestDatabaseButton.IsEnabled = false;
        DatabaseTestStatusText.Text = "正在测试连接……";
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(database.ConnectionTimeoutSeconds + 2));
            var result = await _databaseService.TestConnectionAsync(database, password, timeout.Token);
            DatabaseTestStatusText.Text = result.IsSuccess
                ? $"{result.Message} MySQL {result.ServerVersion}"
                : result.Message;
        }
        catch (OperationCanceledException)
        {
            DatabaseTestStatusText.Text = "连接测试已超时。";
        }
        finally
        {
            TestDatabaseButton.IsEnabled = true;
        }
    }
}
