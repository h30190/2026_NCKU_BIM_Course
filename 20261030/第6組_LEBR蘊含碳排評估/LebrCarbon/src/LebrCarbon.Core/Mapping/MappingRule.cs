namespace LebrCarbon.Core.Mapping;

/// <summary>材質名稱 → 係數 ID 的規則（計畫書 4.2 節第 3 步）。</summary>
public sealed record MappingRule
{
    /// <summary>比對材質名稱的正規表示式，例如 "混凝土.*280"。</summary>
    public required string Pattern { get; init; }
    public required string FactorId { get; init; }
    public string Note { get; init; } = "";
}
