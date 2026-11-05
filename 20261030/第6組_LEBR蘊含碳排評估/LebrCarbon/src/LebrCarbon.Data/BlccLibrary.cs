using System.Text.Json;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Data;

/// <summary>blcc.LEBR-2025.json：手冊附錄二建築構件碳排資料庫。</summary>
public sealed class BlccLibrary
{
    public string Manual { get; set; } = "";
    public string Source { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "";
    public List<BlccItem> Items { get; set; } = new();

    private Dictionary<string, BlccItem>? _byId;

    public IReadOnlyDictionary<string, BlccItem> ById =>
        _byId ??= Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<BlccItem> InCategory(NsCategory c) => Items.Where(i => i.CategoryEnum == c);

    public static BlccLibrary Load(string? path = null)
    {
        path ??= Path.Combine(DataLoader.DefaultResourceDir, "blcc.LEBR-2025.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 B-LCC 資料檔：{path}", path);
        return JsonSerializer.Deserialize<BlccLibrary>(File.ReadAllText(path), DataLoader.Options)
               ?? throw new InvalidDataException($"B-LCC 資料檔無法解析：{path}");
    }
}
