using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>
/// 上傳到控制台的評估快照（控制台計畫書 5.1 節）。
/// 序列化後欄位為 camelCase；新增欄位只加不改，改格式時調高 SchemaVersion。
/// </summary>
public sealed record SnapshotDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required PluginInfo Plugin { get; init; }
    public required ProjectInfo Project { get; init; }
    public required SnapshotInfo Snapshot { get; init; }
    public required BuildingInputs Inputs { get; init; }
    public ShapeInputs? Shape { get; init; }
    public required SnapshotResult Result { get; init; }
    public required List<SnapshotItem> Items { get; init; }
    /// <summary>（選用）base64 的 .xlsx 計算書。</summary>
    public string? Excel { get; init; }
    /// <summary>（選用）佔比、改善潛力、距下一級；舊快照沒有這欄。</summary>
    public SnapshotAnalysis? Analysis { get; init; }
}

public sealed record PluginInfo(string Version, string Manual);

/// <summary>Key 預設為模型檔名（與 {model}.lebr.json 一致），同一模型的快照歸在同一個案子。</summary>
public sealed record ProjectInfo(string Key, string Title);

public sealed record SnapshotInfo(string Name, DateTimeOffset CreatedAt);

public sealed record SnapshotResult
{
    public double Eec { get; init; }
    public double Eecc { get; init; }
    public double Tec { get; init; }
    public double Tecc { get; init; }
    public double Eci { get; init; }
    public double Ecis { get; init; }
    public double DeltaCf { get; init; }
    public double Cfr { get; init; }
    public required string Grade { get; init; }
    /// <summary>LL 延壽優惠係數與減碳優惠合計：設計案四階段合計 − 優惠 ÷ (1 + LL) = EEC。</summary>
    public double Ll { get; init; }
    public double Credits { get; init; }
    public double CfsUnderground { get; init; }
    public required StageSet Stages { get; init; }
    public required StructureScenario DesignStructure { get; init; }
    public required StructureScenario BaselineStructure { get; init; }
    /// <summary>表 12 八等級刻度的碳排密度 ECI（kgCO2e/m²）。</summary>
    public required List<ScaleTick> Scale { get; init; }
}

public sealed record StageSet(Stages Design, Stages Baseline);

/// <summary>
/// 地上層四階段碳排：製造運輸 = 主結構 + 非主結構新建；更新修繕；施工；拆除廢棄。
/// 施工與拆除廢棄只以設計案計算一次，基準案同（手冊式 12 ~ 19）。
/// </summary>
public sealed record Stages(double Structure, double NonStructural, double Renew, double Construction, double Demolition)
{
    public double Total => Structure + NonStructural + Renew + Construction + Demolition;
}

public sealed record ScaleTick(string Grade, double Eci);

/// <summary>非主結構明細一列。Elements 留給「從控制台定位」使用（P3），目前可為空。</summary>
public sealed record SnapshotItem
{
    /// <summary>design 或 baseline。</summary>
    public required string Side { get; init; }
    public string? BlccId { get; init; }
    /// <summary>B-LCC 類別代碼 ow / w / cw / iw / f / p。</summary>
    public required string Category { get; init; }
    public required string CategoryName { get; init; }
    public required string Name { get; init; }
    public double Area { get; init; }
    public double NewEf { get; init; }
    public double RenewEf { get; init; }
    public double NewCarbon { get; init; }
    public double RenewCarbon { get; init; }
    /// <summary>新建 + 更新碳排。</summary>
    public double Carbon { get; init; }
    public string Source { get; init; } = "";
    public List<SnapshotElement> Elements { get; init; } = new();
}

public sealed record SnapshotElement(string UniqueId, string TypeName);
