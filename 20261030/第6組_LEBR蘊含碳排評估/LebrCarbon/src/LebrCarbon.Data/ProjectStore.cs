using System.Text.Json;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Data;

/// <summary>
/// 一個專案的 LEBR 輸入（建築參數 + 非主結構構件），存成 JSON 跟著模型走（計畫書 4.4 節）。
/// </summary>
public sealed class LebrProjectFile
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Manual { get; set; } = "LEBR-2025";
    public string ModelTitle { get; set; } = "";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public BuildingInputs Inputs { get; set; } = new();
    public List<NonStructuralItem> DesignItems { get; set; } = new();
    /// <summary>人工調整過的基準案構件；空清單代表由 B-LCC 自動產生。</summary>
    public List<NonStructuralItem> BaselineItems { get; set; } = new();
    public ShapeInputs Shape { get; set; } = new();
    /// <summary>一樓（地面層）樓層名稱，用來區分地上與地下。</summary>
    public string? GroundLevelName { get; set; }
    /// <summary>樓層表中取消勾選的樓層名稱（例如屋頂層、屋突）。</summary>
    public List<string> ExcludedLevels { get; set; } = new();
    /// <summary>樓層表中額外勾選的樓層名稱。</summary>
    public List<string> IncludedLevels { get; set; } = new();
}

/// <summary>形狀係數 F 的三個輸入（表 5 ~ 7）。</summary>
public sealed class ShapeInputs
{
    /// <summary>周長面積比係數 PAr。</summary>
    public double PAr { get; set; } = 1.0;
    /// <summary>平面長寬比 b。</summary>
    public double Aspect { get; set; } = 1.0;
    /// <summary>出挑係數 Rc（模型無法自動判斷，需人工輸入）。</summary>
    public double Rc { get; set; }
    public string LevelName { get; set; } = "";

    public double F1 => LebrCarbon.Core.Lebr.LebrTables.F1(PAr);
    public double F2 => LebrCarbon.Core.Lebr.LebrTables.F2(Aspect);
    public double F3 => LebrCarbon.Core.Lebr.LebrTables.F3(Rc);
    public double F => F1 * F2 * F3;
}

public static class ProjectStore
{
    /// <summary>
    /// 模型已存檔時，存在模型旁的 {模型名}.lebr.json；
    /// 未存檔時，存在「文件\LebrCarbon\{標題}.lebr.json」。
    /// </summary>
    public static string PathFor(string? modelPath, string title)
    {
        if (!string.IsNullOrWhiteSpace(modelPath) && Path.IsPathRooted(modelPath))
            return Path.ChangeExtension(modelPath, ".lebr.json");

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LebrCarbon");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(dir, safe + ".lebr.json");
    }

    public static LebrProjectFile? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<LebrProjectFile>(File.ReadAllText(path), DataLoader.Options);
    }

    public static void Save(string path, LebrProjectFile file)
    {
        file.SavedAt = DateTime.Now;
        File.WriteAllText(path, JsonSerializer.Serialize(file, DataLoader.Options));
    }
}
