namespace LebrCarbon.Core.Lebr;

/// <summary>B-LCC 資料庫中的一個標準構件（手冊附錄二）。</summary>
public sealed record BlccItem
{
    public required string Id { get; init; }
    public string Table { get; init; } = "";
    /// <summary>類別代碼 ow / w / cw / iw / f / p。</summary>
    public required string Category { get; init; }
    public required string Name { get; init; }
    /// <summary>新建碳排（kgCO2e/m2）。</summary>
    public double New { get; init; }
    public double RenewHigh { get; init; }
    public double RenewMid { get; init; }
    public double RenewLow { get; init; }
    /// <summary>此構件的比較基準構件 ID。</summary>
    public string BaselineId { get; init; } = "";
    public string Note { get; init; } = "";

    public NsCategory CategoryEnum => NsCategoryCodes.Parse(Category);

    public double Renew(WearLevel level) => level switch
    {
        WearLevel.High => RenewHigh,
        WearLevel.Mid => RenewMid,
        _ => RenewLow,
    };
}

/// <summary>
/// 一筆非主結構構件：面積 × 單位碳排（式 11-1 ~ 11-6*）。
/// 單位碳排通常取自 B-LCC，也可自訂（手冊 3-4 節第 4 點）。
/// </summary>
public sealed record NonStructuralItem
{
    public NsCategory Category { get; init; }
    public required string Name { get; init; }
    /// <summary>構件面積（m2）。外窗以外框邊尺寸計算之立面積。</summary>
    public double Area { get; init; }
    /// <summary>新建碳排標準 F（kgCO2e/m2）。</summary>
    public double NewEf { get; init; }
    /// <summary>更新碳排標準 F*（kgCO2e/m2），已依耗損等級取值。</summary>
    public double RenewEf { get; init; }
    /// <summary>來源 B-LCC ID，自訂構件為 null。</summary>
    public string? BlccId { get; init; }
    /// <summary>資料來源（Revit 類型名稱、人工輸入等），供追溯。</summary>
    public string Source { get; init; } = "";

    public double NewEmission => Area * NewEf;
    public double RenewEmission => Area * RenewEf;

    public static NonStructuralItem FromBlcc(BlccItem b, double area, WearLevel level, string source = "") => new()
    {
        Category = b.CategoryEnum,
        Name = b.Name,
        Area = area,
        NewEf = b.New,
        RenewEf = b.Renew(level),
        BlccId = b.Id,
        Source = source,
    };
}

/// <summary>由設計案構件自動產生基準案構件：同面積、改用各構件的基準構件（手冊式 11 參數說明）。</summary>
public static class BaselineBuilder
{
    public static List<NonStructuralItem> Build(
        IEnumerable<NonStructuralItem> design,
        IReadOnlyDictionary<string, BlccItem> blcc,
        WearLevel level)
    {
        var result = new List<NonStructuralItem>();
        foreach (var d in design)
        {
            var baseId = d.BlccId is not null && blcc.TryGetValue(d.BlccId, out var src) ? src.BaselineId : null;
            if (string.IsNullOrEmpty(baseId)) baseId = DefaultBaselineId(d.Category);

            if (baseId is not null && blcc.TryGetValue(baseId, out var b))
                result.Add(NonStructuralItem.FromBlcc(b, d.Area, level, $"基準案（對應 {d.Name}）"));
            else
                result.Add(d with { Source = "基準案（無對應基準，沿用設計案）" });
        }

        // 相同基準構件合併成一列，方便閱讀
        return result
            .GroupBy(x => (x.Category, x.Name, x.NewEf, x.RenewEf, x.BlccId))
            .Select(g => g.First() with { Area = g.Sum(x => x.Area) })
            .ToList();
    }

    /// <summary>自訂構件沒有 BlccId 時，依類別取手冊規定的基準構件。外窗無法判斷玻璃種類，沿用設計案。</summary>
    public static string? DefaultBaselineId(NsCategory c) => c switch
    {
        NsCategory.ExteriorFinish => "OW-1",
        NsCategory.CurtainWall => "CW-0",
        NsCategory.InteriorWall => "IW-1",
        NsCategory.IndoorFloor => "F-G1",
        NsCategory.OutdoorFloor => "P-RC1",
        _ => null,
    };
}
