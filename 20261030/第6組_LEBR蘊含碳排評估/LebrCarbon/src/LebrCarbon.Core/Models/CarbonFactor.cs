namespace LebrCarbon.Core.Models;

/// <summary>一筆碳排係數（計畫書 4.1 節）。</summary>
public sealed record CarbonFactor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }   // concrete / rebar / steel / ...
    public required string Unit { get; init; }       // m3 / kg / m2 / ea
    /// <summary>每單位碳排 kgCO2e。</summary>
    public double Ef { get; init; }
    /// <summary>EN 15978 階段代號，例如 A1-A3。</summary>
    public string Stage { get; init; } = "A1-A3";
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
}
