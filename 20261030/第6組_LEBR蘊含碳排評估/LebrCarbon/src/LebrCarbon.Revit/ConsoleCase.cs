using System.IO;
using Autodesk.Revit.DB;
using LebrCarbon.Dashboard;

namespace LebrCarbon.Revit;

/// <summary>
/// 控制台「案件」與 Revit 模型的對應：案件 key 一律用模型檔名（不含 .rvt），
/// 與 {模型名}.lebr.json、材料對照表的 project 欄位、減碳建議一致。
/// </summary>
public static class ConsoleCase
{
    /// <summary>模型檔名；未存檔時用視窗標題。</summary>
    public static string KeyFor(Document doc) =>
        string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : Path.GetFileNameWithoutExtension(doc.PathName);

    /// <summary>
    /// 在背景登記案件，讓還沒評估的模型也出現在控制台左側。只登記已存檔的專案模型（略過族群檔、連結檔）；
    /// 控制台沒開時安靜略過，不打擾使用者、不拖慢開檔。必須在 Revit 主執行緒呼叫（會讀 Document）。
    /// </summary>
    public static void RegisterInBackground(Document? doc)
    {
        var key = RegistrableKey(doc);
        if (key is null) return;
        var baseUrl = DashboardSettings.Load().BaseUrl;
        _ = Task.Run(() => RegisterAsync(baseUrl, key));
    }

    /// <summary>可登記的模型回傳 key，否則 null。必須在 Revit 主執行緒呼叫。</summary>
    public static string? RegistrableKey(Document? doc) =>
        doc is null || doc.IsFamilyDocument || doc.IsLinked || string.IsNullOrWhiteSpace(doc.PathName) ? null : KeyFor(doc);

    public static async Task<bool> RegisterAsync(string baseUrl, string key)
    {
        using var client = new DashboardClient(baseUrl);
        return await client.RegisterProjectAsync(key, key).ConfigureAwait(false);
    }
}
