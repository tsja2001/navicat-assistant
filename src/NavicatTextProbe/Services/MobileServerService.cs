using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class MobileServerService : IAsyncDisposable
{
    private readonly object _stateLock = new();
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private int _nextClientTaskId;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverLoop;
    private SuggestionResult _suggestion = SuggestionResult.Info("等待 Navicat SQL、Word 练习题或主动截图…");
    private AppSettings _settings = new();
    private AssistantRuntimeStatus _runtimeStatus = new();
    private bool _privacyMode;

    public bool IsRunning => _listener is not null;

    /// <summary>手机页隐私黑屏开关：开启后页面只显示纯黑屏，不泄露任何建议内容。</summary>
    public bool PrivacyMode
    {
        get
        {
            lock (_stateLock)
            {
                return _privacyMode;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _privacyMode = value;
            }
        }
    }

    public int Port { get; private set; }

    public string? LastError { get; private set; }

    public event EventHandler<MobileSettingsPatch>? SettingsChanged;

    public async Task<bool> StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_listener is not null && Port == settings.MobilePort)
        {
            lock (_stateLock)
            {
                _settings = settings;
            }

            return true;
        }

        await StopAsync();
        LastError = null;
        Port = settings.MobilePort;
        lock (_stateLock)
        {
            _settings = settings;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Any, settings.MobilePort);
            _listener.Start();
            _serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _serverLoop = AcceptLoopAsync(_listener, _serverCancellation.Token);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            _listener = null;
            return false;
        }
    }

    public void UpdateSuggestion(SuggestionResult suggestion, AppSettings settings)
    {
        lock (_stateLock)
        {
            _suggestion = suggestion;
            _settings = settings;
        }
    }

    public void UpdateRuntimeStatus(AssistantRuntimeStatus status)
    {
        lock (_stateLock)
        {
            _runtimeStatus = status;
        }
    }

    public IReadOnlyList<string> GetAccessUrls(AppSettings settings)
    {
        var token = WebUtility.UrlEncode(settings.MobileAccessToken);
        var candidates = new List<(int Score, IPAddress Address)>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
            var adapterText = $"{adapter.Name} {adapter.Description}";
            var likelyVirtual = IsLikelyVirtualAdapter(adapterText);
            var physicalType = adapter.NetworkInterfaceType is
                NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet;

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address) &&
                    !IsAutomaticPrivateAddress(address.Address))
                {
                    var score = 0;
                    if (physicalType) score += 100;
                    if (hasGateway) score += 80;
                    if (IsPrivateLanAddress(address.Address)) score += 40;
                    if (likelyVirtual) score -= 200;
                    candidates.Add((score, address.Address));
                }
            }
        }

        var urls = new List<string>
        {
            $"http://127.0.0.1:{settings.MobilePort}/?token={token}"
        };
        urls.AddRange(candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => $"http://{candidate.Address}:{settings.MobilePort}/?token={token}")
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return urls;
    }

    private static bool IsLikelyVirtualAdapter(string text)
    {
        string[] markers = ["tailscale", "wintun", "tunnel", "mihomo", "meta", "vgate", "hyper-v", "virtual", "vmware", "wsl", "bluetooth"];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAutomaticPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var taskId = Interlocked.Increment(ref _nextClientTaskId);
                var clientTask = HandleClientAsync(client, cancellationToken);
                _clientTasks[taskId] = clientTask;
                _ = clientTask.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _clientTasks.TryRemove(taskId, out _);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(150, cancellationToken);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", "请求格式不正确", cancellationToken);
                    return;
                }

                var contentLength = 0;
                while (true)
                {
                    var header = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(header)) break;
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);
                    }
                }

                var body = string.Empty;
                if (contentLength > 0)
                {
                    var buffer = new char[Math.Min(contentLength, 64_000)];
                    var total = 0;
                    while (total < buffer.Length)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                        if (count == 0) break;
                        total += count;
                    }

                    body = new string(buffer, 0, total);
                }

                var method = requestParts[0].ToUpperInvariant();
                var uri = new Uri("http://localhost" + requestParts[1]);
                var token = ReadQueryValue(uri.Query, "token");
                if (!IsAuthorized(token))
                {
                    await WriteResponseAsync(stream, 401, "text/plain; charset=utf-8", "访问令牌不正确。请从电脑端复制完整手机地址。", cancellationToken);
                    return;
                }

                if (method == "GET" && uri.AbsolutePath == "/")
                {
                    await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", BuildPage(token!), cancellationToken);
                    return;
                }

                if (method == "GET" && uri.AbsolutePath == "/api/state")
                {
                    string json;
                    lock (_stateLock)
                    {
                        json = JsonSerializer.Serialize(new
                        {
                            suggestion = _suggestion,
                            runtime = _runtimeStatus,
                            privacyMode = _privacyMode,
                            settings = new
                            {
                                _settings.TaskbarPanelEnabled,
                                _settings.TaskbarWidth,
                                _settings.TaskbarFloatingHeight,
                                _settings.MobileServerEnabled,
                                _settings.VisionEnabled,
                                _settings.FallbackEnabled,
                                _settings.FallbackApiBaseUrl,
                                _settings.FallbackModel,
                                _settings.SmartActionHotkey,
                                _settings.CopyFullAnswerHotkey,
                                _settings.AnswerClipboardQuestionHotkey,
                                _settings.GuidedTypingHotkey,
                                _settings.ToggleMobileHotkey,
                                _settings.ToggleMobilePrivacyHotkey,
                                _settings.VisionScreenshotHotkey,
                                taskbarStyle = _settings.TaskbarStyle,
                                mobileStyle = _settings.MobileStyle
                            }
                        }, WebJsonOptions);
                    }

                    await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", json, cancellationToken);
                    return;
                }

                if (method == "GET" && uri.AbsolutePath == "/manifest.webmanifest")
                {
                    await WriteResponseAsync(
                        stream,
                        200,
                        "application/manifest+json; charset=utf-8",
                        BuildManifestJson(token!),
                        cancellationToken);
                    return;
                }

                if (method == "GET" && uri.AbsolutePath is "/icon-192.png" or "/icon-512.png" or "/apple-touch-icon.png")
                {
                    var iconBytes = uri.AbsolutePath switch
                    {
                        "/icon-192.png" => Icon192Png.Value,
                        "/apple-touch-icon.png" => Icon180Png.Value,
                        _ => Icon512Png.Value
                    };
                    await WriteRawResponseAsync(stream, 200, "image/png", iconBytes, cancellationToken);
                    return;
                }

                if (method == "GET" && uri.AbsolutePath == "/sw.js")
                {
                    await WriteResponseAsync(
                        stream,
                        200,
                        "application/javascript; charset=utf-8",
                        BuildServiceWorkerScript(),
                        cancellationToken);
                    return;
                }

                if (method == "POST" && uri.AbsolutePath == "/api/settings")
                {
                    var patch = JsonSerializer.Deserialize<MobileSettingsPatch>(body, WebJsonOptions);
                    if (patch is null)
                    {
                        await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", "设置内容为空", cancellationToken);
                        return;
                    }
                    if (!TryValidateSettingsPatch(patch, out var validationError))
                    {
                        await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", validationError, cancellationToken);
                        return;
                    }

                    SettingsChanged?.Invoke(this, patch);
                    await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", "{\"saved\":true}", cancellationToken);
                    return;
                }

                await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.ToString();
            }
        }
    }

    private bool TryValidateSettingsPatch(MobileSettingsPatch patch, out string error)
    {
        string[] hotkeys;
        lock (_stateLock)
        {
            hotkeys =
            [
                _settings.ToggleAssistantHotkey,
                string.IsNullOrWhiteSpace(patch.SmartActionHotkey)
                    ? _settings.SmartActionHotkey
                    : patch.SmartActionHotkey,
                string.IsNullOrWhiteSpace(patch.CopyFullAnswerHotkey)
                    ? _settings.CopyFullAnswerHotkey
                    : patch.CopyFullAnswerHotkey,
                string.IsNullOrWhiteSpace(patch.AnswerClipboardQuestionHotkey)
                    ? _settings.AnswerClipboardQuestionHotkey
                    : patch.AnswerClipboardQuestionHotkey,
                string.IsNullOrWhiteSpace(patch.VisionScreenshotHotkey)
                    ? _settings.VisionScreenshotHotkey
                    : patch.VisionScreenshotHotkey,
                string.IsNullOrWhiteSpace(patch.GuidedTypingHotkey)
                    ? _settings.GuidedTypingHotkey
                    : patch.GuidedTypingHotkey,
                string.IsNullOrWhiteSpace(patch.ToggleMobileHotkey)
                    ? _settings.ToggleMobileHotkey
                    : patch.ToggleMobileHotkey,
                string.IsNullOrWhiteSpace(patch.ToggleMobilePrivacyHotkey)
                    ? _settings.ToggleMobilePrivacyHotkey
                    : patch.ToggleMobilePrivacyHotkey,
                _settings.CycleCandidatesHotkey,
                "Win+PageUp",
                "Win+PageDown"
            ];
        }
        return GlobalHotkeyService.TryValidateShortcutSet(hotkeys, out error);
    }

    private bool IsAuthorized(string? requestToken)
    {
        string token;
        lock (_stateLock)
        {
            token = _settings.MobileAccessToken;
        }

        return !string.IsNullOrWhiteSpace(token) && string.Equals(requestToken, token, StringComparison.Ordinal);
    }

    private static string? ReadQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length > 0 && string.Equals(WebUtility.UrlDecode(pair[0]), key, StringComparison.Ordinal))
            {
                return pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : string.Empty;
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        string content,
        CancellationToken cancellationToken) =>
        await WriteRawResponseAsync(stream, statusCode, contentType, Encoding.UTF8.GetBytes(content), cancellationToken);

    private static async Task WriteRawResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Error"
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        var listener = _listener;
        var cancellation = _serverCancellation;
        var loop = _serverLoop;
        _listener = null;
        _serverCancellation = null;
        _serverLoop = null;
        cancellation?.Cancel();
        listener.Stop();
        try
        {
            if (loop is not null)
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }

            var clients = _clientTasks.Values.ToArray();
            if (clients.Length > 0)
            {
                await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException)
        {
            // Stopping the listener interrupts AcceptTcpClientAsync by design.
        }
        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    internal static string BuildManifestJson(string token)
    {
        var encodedToken = WebUtility.UrlEncode(token);
        return JsonSerializer.Serialize(new
        {
            name = "Assistant",
            short_name = "Assistant",
            description = "Navicat SQL / Word 练习题的手机端辅助页面",
            start_url = $"/?token={encodedToken}",
            scope = "/",
            display = "standalone",
            background_color = "#0C1117",
            theme_color = "#151D27",
            icons = new[]
            {
                new { src = $"/icon-192.png?token={encodedToken}", sizes = "192x192", type = "image/png" },
                new { src = $"/icon-512.png?token={encodedToken}", sizes = "512x512", type = "image/png" }
            }
        }, WebJsonOptions);
    }

    internal static byte[] RenderAppIconPng(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var backgroundPath = RoundedRectanglePath(size);
        using var background = new SolidBrush(Color.FromArgb(23, 32, 42));
        graphics.FillPath(background, backgroundPath);
        using var font = new Font("Segoe UI", Math.Max(12, size * 0.52f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(125, 242, 177));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString("A", font, brush, new RectangleF(0, 0, size, size), format);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static readonly Lazy<byte[]> Icon192Png = new(() => RenderAppIconPng(192));
    private static readonly Lazy<byte[]> Icon180Png = new(() => RenderAppIconPng(180));
    private static readonly Lazy<byte[]> Icon512Png = new(() => RenderAppIconPng(512));

    private static GraphicsPath RoundedRectanglePath(int size)
    {
        var radius = size * 0.22f;
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static string BuildServiceWorkerScript() => """
        self.addEventListener('install', () => self.skipWaiting());
        self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
        self.addEventListener('fetch', (event) => event.respondWith(fetch(event.request)));
        """;

    private static string BuildPage(string token) => """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
          <meta name="theme-color" content="#151D27">
          <meta name="mobile-web-app-capable" content="yes">
          <meta name="apple-mobile-web-app-capable" content="yes">
          <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
          <meta name="apple-mobile-web-app-title" content="Assistant">
          <link rel="manifest" href="/manifest.webmanifest?token=__ASSISTANT_TOKEN__">
          <link rel="icon" type="image/png" href="/icon-192.png?token=__ASSISTANT_TOKEN__">
          <link rel="apple-touch-icon" href="/apple-touch-icon.png?token=__ASSISTANT_TOKEN__">
          <title>Assistant</title>
          <style>
            :root{color-scheme:dark;font-family:system-ui,-apple-system,"Segoe UI",sans-serif;background:#0c1117;color:#f2f5f7}
            body{margin:0;padding:18px;max-width:920px;margin:auto}.card{background:#151d27;border:1px solid #293746;border-radius:14px;padding:16px;margin-bottom:14px;box-shadow:0 8px 28px #0005}
            .badge{display:inline-block;padding:3px 9px;border-radius:99px;background:#1e6f50;color:#c9ffe8;font-size:12px}.lights{display:inline-flex;gap:4px;margin-left:8px}.light{width:7px;height:7px;border-radius:50%;background:#7f8c8d}.light.off{background:#7f8c8d}.light.standby{background:#3498db}.light.working{background:#f39c12}.light.ready{background:#27ae60}.light.error{background:#c0392b}h1{font-size:21px;margin:4px 0 16px}h2{font-size:15px;color:#9fb1c3;margin:0 0 9px}
            #display{font:600 18px/1.55 ui-monospace,Consolas,monospace;color:#7df2b1;white-space:pre-wrap}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#0d131a;padding:12px;border-radius:9px;color:#dce7ef}#privacyOverlay{position:fixed;inset:0;background:#000;z-index:99999}
            #explanation,#issues{line-height:1.65;color:#c7d2dc}#candidates{padding-left:22px;line-height:1.5}#candidates li{padding:7px 9px;margin:6px 0;border-left:3px solid #405365;background:#0d131a;border-radius:5px;font-family:ui-monospace,Consolas,monospace}#candidates li.active{border-color:#58d68d;color:#9af5c2}.grid{display:grid;grid-template-columns:1fr 1fr;gap:11px}.field{display:flex;flex-direction:column;gap:5px}label{font-size:13px;color:#aab8c5}input{background:#0d131a;color:#fff;border:1px solid #3a4d60;border-radius:7px;padding:9px}button{background:#2b8a62;color:white;border:0;border-radius:8px;padding:11px 15px;font-weight:650}.muted{color:#7f91a3;font-size:12px}
            @media(max-width:600px){.grid{grid-template-columns:1fr}body{padding:11px}.card{padding:13px}}
          </style>
        </head>
        <body>
          <div id="privacyOverlay" hidden></div>
          <h1>Assistant <span id="mode" class="badge">等待</span><span class="lights"><span id="mainLight" class="light standby"></span><span id="databaseLight" class="light off"></span><span id="typingLight" class="light off"></span></span></h1>
          <section class="card"><h2>当前最有用的建议</h2><div id="display">等待 Assistant…</div></section>
          <section id="exerciseCard" class="card" hidden><h2 id="questionTitle">完整题目</h2><pre id="question">—</pre><h2>完整参考答案</h2><pre id="fullAnswer">—</pre></section>
          <section class="card"><h2 id="insertTitle">完整建议</h2><pre id="insert">—</pre><div id="candidateBlock" hidden><h2>本次 3 个候选（电脑按 Ctrl+Alt+R 切换）</h2><ol id="candidates"></ol></div><h2>答案解析 / 原因</h2><div id="explanation">—</div><ul id="issues"></ul></section>
          <details class="card"><summary>展示配置</summary><p class="muted">保存后会同步到电脑任务栏和当前手机页面。Assistant 默认独占业务快捷键，命中后不再传给钉钉等普通应用；Windows 系统保留键和权限更高的程序除外。</p>
            <div class="grid">
              <label><input id="taskbarPanelEnabled" type="checkbox"> 开启任务栏内建议</label>
              <label><input id="visionEnabled" type="checkbox"> 开启主动截图识题</label>
              <label><input id="fallbackEnabled" type="checkbox"> DeepSeek 超时自动切换智谱 GLM</label>
              <div class="field"><label>任务栏期望宽度</label><input id="taskbarWidth" type="number" min="200" max="1400" step="20"></div>
              <div class="field"><label>提示条悬浮高度</label><input id="taskbarFloatingHeight" type="number" min="30" max="900" step="1"></div>
              <div class="field"><label>智能分析当前内容快捷键</label><input id="smartActionHotkey" type="text" placeholder="Ctrl+Alt+A"></div>
              <div class="field"><label>完整答案复制快捷键</label><input id="copyFullAnswerHotkey" type="text" placeholder="Ctrl+Alt+C"></div>
              <div class="field"><label>剪贴板题目回答快捷键</label><input id="answerClipboardQuestionHotkey" type="text" placeholder="Ctrl+Alt+V"></div>
              <div class="field"><label>屏幕 OCR 快捷键</label><input id="visionScreenshotHotkey" type="text" placeholder="Ctrl+Alt+O"></div>
              <div class="field"><label>受控逐字代打快捷键</label><input id="guidedTypingHotkey" type="text" placeholder="Ctrl+Alt+D"></div>
              <div class="field"><label>手机答案开关快捷键</label><input id="toggleMobileHotkey" type="text" placeholder="Ctrl+Alt+M"></div>
              <div class="field"><label>手机页隐私黑屏快捷键</label><input id="toggleMobilePrivacyHotkey" type="text" placeholder="Ctrl+Alt+P"></div>
              <div class="field"><label>GLM 冗余 API 地址</label><input id="fallbackApiBaseUrl" type="text"></div>
              <div class="field"><label>GLM 冗余模型</label><input id="fallbackModel" type="text"></div>
              <div class="field"><label>任务栏字号</label><input id="taskbarFont" type="number" min="10" max="36" step="1"></div>
              <div class="field"><label>任务栏行高</label><input id="taskbarLine" type="number" min="12" max="54" step="1"></div>
              <div class="field"><label>任务栏背景色</label><input id="taskbarBg" type="color"></div>
              <div class="field"><label>任务栏字体色</label><input id="taskbarFg" type="color"></div>
              <div class="field"><label>提示条背景不透明度（0 全透明）</label><input id="taskbarOpacity" type="number" min="0" max="1" step="0.01"></div>
              <div class="field"><label>手机建议字号</label><input id="mobileFont" type="number" min="10" max="48" step="1"></div>
              <div class="field"><label>手机建议行高</label><input id="mobileLine" type="number" min="12" max="72" step="1"></div>
              <div class="field"><label>手机卡片背景色</label><input id="mobileBg" type="color"></div>
              <div class="field"><label>手机建议字体色</label><input id="mobileFg" type="color"></div>
              <div class="field"><label>手机卡片透明度</label><input id="mobileOpacity" type="number" min="0.05" max="1" step="0.05"></div>
            </div><p><button id="save">保存并同步</button> <span id="saved" class="muted"></span></p>
          </details>
          <script>
            const token=new URLSearchParams(location.search).get('token')||'';let populated=false;
            const $=id=>document.getElementById(id); const api=p=>p+'?token='+encodeURIComponent(token);
            async function refresh(){try{const r=await fetch(api('/api/state'));if(!r.ok)throw Error(r.status);const x=await r.json(),s=x.suggestion||{},c=x.settings||{},rt=x.runtime||{};
              const cs=s.candidates||[],active=s.activeCandidateIndex||0,modes=['补全','诊断','状态','错误','题目回答','题目续写'],types={sql:'SQL题',choice:'选择题',fill_blank:'填空题',short_answer:'简答题',unknown:'题目'},exercise=s.mode===4||s.mode===5;
              let mode=cs.length?`候选 ${active+1}/${cs.length}`:(modes[s.mode]||'建议');if(s.questionType)mode+=` · ${types[s.questionType]||s.questionType}`;if(exercise&&s.questionNumber)mode+=s.questionNumber==='当前题'?' · 当前题':` · 第 ${s.questionNumber} 题`;if(exercise&&s.activeSubquestionNumber)mode+=` · 子问 ${s.activeSubquestionNumber}`;$('mode').textContent=mode;$('display').textContent=s.displayText||'当前没有足够可靠的建议';$('insert').textContent=s.insertText||'—';$('insertTitle').textContent=s.mode===4?'本次答案文本':s.mode===5?'当前光标续写':'完整建议代码';$('explanation').textContent=s.explanation||'—';
              const light=(id,level,title)=>{const e=$(id);e.className='light '+(level||'off');e.title=title||''};light('mainLight',rt.mainLevel,rt.mainText);light('databaseLight',rt.databaseLevel,rt.databaseText);light('typingLight',rt.guidedTypingLevel,rt.guidedTypingText);$('privacyOverlay').hidden=!x.privacyMode;
              $('exerciseCard').hidden=!exercise;$('questionTitle').textContent=exercise?(s.questionNumber&&s.questionNumber!=='当前题'?`第 ${s.questionNumber} 题 · 完整题目`:'当前完整题目'):'完整题目';$('question').textContent=s.questionText||'—';$('fullAnswer').textContent=s.fullAnswer||'—';
              $('candidateBlock').hidden=!cs.length;$('candidates').replaceChildren(...cs.map((c,i)=>{const li=document.createElement('li');li.textContent=`${i+1}. ${c.displayText||c.insertText||'—'}`;if(i===active)li.className='active';return li}));
              $('issues').replaceChildren(...(s.issues||[]).map(i=>{const li=document.createElement('li');li.textContent=i;return li}));
              const m=c.mobileStyle||{};$('display').style.fontSize=(m.fontSize||18)+'px';$('display').style.lineHeight=(m.lineHeight||28)+'px';$('display').style.color=m.fontColor||'#7df2b1';
              const rgba=(hex,a)=>{const v=(hex||'#151d27').replace('#','');const n=parseInt(v.length===3?v.split('').map(x=>x+x).join(''):v,16);return `rgba(${n>>16},${n>>8&255},${n&255},${a??.96})`};document.querySelectorAll('.card').forEach(x=>x.style.backgroundColor=rgba(m.backgroundColor,m.backgroundOpacity));
              if(!populated){populated=true;$('taskbarPanelEnabled').checked=!!c.taskbarPanelEnabled;const t=c.taskbarStyle||{};
                $('visionEnabled').checked=!!c.visionEnabled;
                $('fallbackEnabled').checked=!!c.fallbackEnabled;
                $('taskbarWidth').value=c.taskbarWidth||640;
                $('taskbarFloatingHeight').value=c.taskbarFloatingHeight??160;
                $('smartActionHotkey').value=c.smartActionHotkey||'Ctrl+Alt+A';
                $('copyFullAnswerHotkey').value=c.copyFullAnswerHotkey||'Ctrl+Alt+C';
                $('answerClipboardQuestionHotkey').value=c.answerClipboardQuestionHotkey||'Ctrl+Alt+V';
                $('visionScreenshotHotkey').value=c.visionScreenshotHotkey||'Ctrl+Alt+O';
                $('guidedTypingHotkey').value=c.guidedTypingHotkey||'Ctrl+Alt+D';
                $('toggleMobileHotkey').value=c.toggleMobileHotkey||'Ctrl+Alt+M';$('toggleMobilePrivacyHotkey').value=c.toggleMobilePrivacyHotkey||'Ctrl+Alt+P';
                $('fallbackApiBaseUrl').value=c.fallbackApiBaseUrl||'https://open.bigmodel.cn/api/paas/v4';$('fallbackModel').value=c.fallbackModel||'glm-5.2';
                $('taskbarFont').value=t.fontSize||14;$('taskbarLine').value=t.lineHeight||21;$('taskbarBg').value=t.backgroundColor||'#17202a';$('taskbarFg').value=t.fontColor||'#f8f9f9';$('taskbarOpacity').value=t.backgroundOpacity??.92;
                $('mobileFont').value=m.fontSize||18;$('mobileLine').value=m.lineHeight||28;$('mobileBg').value=m.backgroundColor||'#151d27';$('mobileFg').value=m.fontColor||'#7df2b1';$('mobileOpacity').value=m.backgroundOpacity||.96;}}
              catch(e){$('display').textContent='无法连接电脑端：'+e.message}}
            $('save').onclick=async()=>{const body={taskbarPanelEnabled:$('taskbarPanelEnabled').checked,visionEnabled:$('visionEnabled').checked,fallbackEnabled:$('fallbackEnabled').checked,fallbackApiBaseUrl:$('fallbackApiBaseUrl').value.trim(),fallbackModel:$('fallbackModel').value.trim(),taskbarWidth:+$('taskbarWidth').value,taskbarFloatingHeight:+$('taskbarFloatingHeight').value,smartActionHotkey:$('smartActionHotkey').value.trim(),copyFullAnswerHotkey:$('copyFullAnswerHotkey').value.trim(),answerClipboardQuestionHotkey:$('answerClipboardQuestionHotkey').value.trim(),visionScreenshotHotkey:$('visionScreenshotHotkey').value.trim(),guidedTypingHotkey:$('guidedTypingHotkey').value.trim(),toggleMobileHotkey:$('toggleMobileHotkey').value.trim(),toggleMobilePrivacyHotkey:$('toggleMobilePrivacyHotkey').value.trim(),taskbarFontSize:+$('taskbarFont').value,taskbarLineHeight:+$('taskbarLine').value,taskbarBackgroundColor:$('taskbarBg').value,taskbarFontColor:$('taskbarFg').value,taskbarOpacity:+$('taskbarOpacity').value,mobileFontSize:+$('mobileFont').value,mobileLineHeight:+$('mobileLine').value,mobileBackgroundColor:$('mobileBg').value,mobileFontColor:$('mobileFg').value,mobileOpacity:+$('mobileOpacity').value};
              const r=await fetch(api('/api/settings'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});$('saved').textContent=r.ok?'已保存':'保存失败';setTimeout(()=>$('saved').textContent='',1800)};
            refresh();setInterval(refresh,1200);
            if('serviceWorker' in navigator){try{navigator.serviceWorker.register('/sw.js?token='+encodeURIComponent(token)).catch(()=>{})}catch(e){}}
          </script>
        </body></html>
        """.Replace("__ASSISTANT_TOKEN__", WebUtility.UrlEncode(token));
}

internal sealed record MobileSettingsPatch
{
    public bool? TaskbarPanelEnabled { get; init; }
    public bool? VisionEnabled { get; init; }
    public bool? FallbackEnabled { get; init; }
    public string? FallbackApiBaseUrl { get; init; }
    public string? FallbackModel { get; init; }
    public double? TaskbarWidth { get; init; }
    public double? TaskbarFloatingHeight { get; init; }
    public string? SmartActionHotkey { get; init; }
    public string? CopyFullAnswerHotkey { get; init; }
    public string? AnswerClipboardQuestionHotkey { get; init; }
    public string? GuidedTypingHotkey { get; init; }
    public string? ToggleMobileHotkey { get; init; }
    public string? ToggleMobilePrivacyHotkey { get; init; }
    public string? VisionScreenshotHotkey { get; init; }
    public double? TaskbarFontSize { get; init; }
    public double? TaskbarLineHeight { get; init; }
    public string? TaskbarBackgroundColor { get; init; }
    public string? TaskbarFontColor { get; init; }
    public double? TaskbarOpacity { get; init; }
    public double? MobileFontSize { get; init; }
    public double? MobileLineHeight { get; init; }
    public string? MobileBackgroundColor { get; init; }
    public string? MobileFontColor { get; init; }
    public double? MobileOpacity { get; init; }
}
