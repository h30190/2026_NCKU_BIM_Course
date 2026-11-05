using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>上傳結果：成功時帶回後端給的快照 ID。</summary>
public sealed record UploadResult(bool Ok, string Message, long? SnapshotId = null);

/// <summary>取回選定建議：Ok 但 Proposal 為 null 代表後端有開、只是沒有選定的建議。</summary>
public sealed record ProposalFetch(bool Ok, string Message, Proposal? Proposal = null);

/// <summary>
/// 控制台後端的 HTTP 包裝（控制台計畫書第 7 節）。外掛一律主動連線，後端從不呼叫 Revit。
/// 後端沒開時只回傳失敗訊息，不丟例外，外掛其他功能不受影響。
/// </summary>
public sealed class DashboardClient : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:8080";
    public const string NotRunningMessage = "控制台後端沒有運行，請先執行 console\\start.ps1。";

    private readonly HttpClient _http;

    public DashboardClient(string? baseUrl = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri((baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string BaseUrl => _http.BaseAddress!.ToString().TrimEnd('/');

    /// <summary>GET /api/health，逾時 2 秒視為後端沒開。</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var res = await _http.GetAsync("api/health", cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>先確認後端有開，再 POST /api/snapshots。</summary>
    public async Task<UploadResult> UploadAsync(SnapshotDocument snapshot, CancellationToken ct = default)
    {
        if (!await IsRunningAsync(ct).ConfigureAwait(false))
            return new UploadResult(false, $"{NotRunningMessage}（{BaseUrl}）");

        using var content = new StringContent(SnapshotBuilder.ToJson(snapshot), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        try
        {
            using var res = await _http.PostAsync("api/snapshots", content, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return new UploadResult(false, $"上傳失敗（HTTP {(int)res.StatusCode}）：{ErrorText(body)}");

            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("id", out var p) && p.TryGetInt64(out var v) ? v : (long?)null;
            return new UploadResult(true, "已上傳到控制台", id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UploadResult(false, $"上傳失敗：{ex.Message}");
        }
    }

    /// <summary>GET /api/proposals/chosen?project=：該案子目前選定、尚未套用的建議；沒有時 Proposal 為 null。</summary>
    public async Task<ProposalFetch> GetChosenProposalAsync(string project, CancellationToken ct = default)
    {
        if (!await IsRunningAsync(ct).ConfigureAwait(false))
            return new ProposalFetch(false, $"{NotRunningMessage}（{BaseUrl}）");
        try
        {
            using var res = await _http.GetAsync($"api/proposals/chosen?project={Uri.EscapeDataString(project)}", ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new ProposalFetch(true, $"控制台上「{project}」沒有選定的建議");
            if (!res.IsSuccessStatusCode)
                return new ProposalFetch(false, $"讀取建議失敗（HTTP {(int)res.StatusCode}）：{ErrorText(body)}");
            return new ProposalFetch(true, "", JsonSerializer.Deserialize<Proposal>(body, DataLoader.Options));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ProposalFetch(false, $"讀取建議失敗：{ex.Message}");
        }
    }

    /// <summary>PATCH /api/proposals/{id}/applied：回報已寫回 Revit，附上外掛重算的結果。</summary>
    public async Task<UploadResult> MarkAppliedAsync(string id, ProposalResult applied, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { Applied = applied }, DataLoader.Options), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, $"api/proposals/{Uri.EscapeDataString(id)}/applied") { Content = content };
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode
                ? new UploadResult(true, "已回報控制台")
                : new UploadResult(false, $"回報控制台失敗（HTTP {(int)res.StatusCode}）：{ErrorText(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UploadResult(false, $"回報控制台失敗：{ex.Message}");
        }
    }

    /// <summary>後端錯誤格式為 {"error":"…"}；400 且提到 schemaVersion 時提示版本不符。</summary>
    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { } msg)
                return msg.Contains("schemaVersion") ? $"控制台與外掛的快照格式版本不符（{msg}）" : msg;
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>%AppData%\LebrCarbon\console.json：可改後端網址，沒有檔案時用預設值。</summary>
public sealed class DashboardSettings
{
    public string BaseUrl { get; set; } = DashboardClient.DefaultBaseUrl;

    /// <summary>網頁（Vite 前端）網址，「網頁控制台」按鈕開這個。</summary>
    public string FrontendUrl { get; set; } = "http://localhost:5173";

    /// <summary>console 資料夾（含 start.ps1）；不填時用外掛編譯時記下的 repo 路徑。</summary>
    public string? ConsoleDir { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LebrCarbon", "console.json");

    public static DashboardSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<DashboardSettings>(File.ReadAllText(path), DataLoader.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }
}
