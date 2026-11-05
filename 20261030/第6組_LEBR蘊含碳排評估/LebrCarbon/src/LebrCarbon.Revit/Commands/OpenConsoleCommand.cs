using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Dashboard;

namespace LebrCarbon.Revit.Commands;

/// <summary>
/// 網頁控制台：前後端都開著就直接開瀏覽器；沒開就執行 console\start.ps1，
/// 等網頁可連線後再自動開瀏覽器。不讀寫模型。
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class OpenConsoleCommand : IExternalCommand
{
    private const string Title = "LEBR 網頁控制台";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var settings = DashboardSettings.Load();
        // 目前開著的模型順便登記成案件，網頁打開時左側就看得到它
        var caseKey = ConsoleCase.RegistrableKey(data.Application.ActiveUIDocument?.Document);

        bool backendUp;
        using (var probe = new DashboardClient(settings.BaseUrl))
            backendUp = Task.Run(() => probe.IsRunningAsync()).GetAwaiter().GetResult();
        if (backendUp && Task.Run(() => IsUpAsync(settings.FrontendUrl, TimeSpan.FromSeconds(2))).GetAwaiter().GetResult())
        {
            if (caseKey is not null)
                Task.Run(() => ConsoleCase.RegisterAsync(settings.BaseUrl, caseKey)).GetAwaiter().GetResult();
            OpenBrowser(settings.FrontendUrl);
            return Result.Succeeded;
        }

        var dir = settings.ConsoleDir ?? BuiltConsoleDir();
        var script = dir is null ? null : Path.Combine(dir, "start.ps1");
        if (script is null || !File.Exists(script))
        {
            TaskDialog.Show(Title,
                $"找不到控制台啟動檔 start.ps1（{script ?? "未設定"}）。\n\n" +
                $"請在 {DashboardSettings.DefaultPath} 加上 \"consoleDir\": \"…\\console\" 指到 console 資料夾。");
            return Result.Cancelled;
        }

        try
        {
            // 啟動檔本身很快結束；後端與前端各開一個視窗，關掉視窗即停止
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show(Title, $"無法執行 start.ps1：{ex.Message}");
            return Result.Failed;
        }

        // 第一次啟動要 npm install，最多等 3 分鐘；背景等待，不卡住 Revit
        var url = settings.FrontendUrl;
        _ = Task.Run(async () =>
        {
            // go run 第一次要編譯，後端可能比前端晚好：兩邊都等到再登記、開瀏覽器
            var ready = await IsUpAsync(settings.BaseUrl.TrimEnd('/') + "/api/health", TimeSpan.FromMinutes(3)).ConfigureAwait(false)
                && await IsUpAsync(url, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            if (!ready) return;
            if (caseKey is not null)
                await ConsoleCase.RegisterAsync(settings.BaseUrl, caseKey).ConfigureAwait(false);
            OpenBrowser(url);
        });

        TaskDialog.Show(Title,
            "正在啟動控制台（會出現後端與前端兩個視窗）。\n" +
            $"網頁準備好後會自動開啟瀏覽器：{url}\n\n" +
            "第一次啟動需要安裝套件，約 1 分鐘。要停止控制台，關掉那兩個視窗即可。");
        return Result.Succeeded;
    }

    /// <summary>在時限內反覆 GET，直到網址有回應為止。</summary>
    private static async Task<bool> IsUpAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var res = await http.GetAsync(url).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    private static string? BuiltConsoleDir() =>
        typeof(OpenConsoleCommand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ConsoleDir")?.Value;

    private static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
