using System.Text.Json;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>把 LebrResult 轉成控制台快照 JSON（控制台計畫書 5.1 節）。只搬數字，不另外計算碳排。</summary>
public static class SnapshotBuilder
{
    public const string Manual = "LEBR-2025";

    public static SnapshotDocument Build(
        LebrResult r,
        string projectKey,
        string projectTitle,
        string snapshotName,
        ShapeInputs? shape = null,
        DateTimeOffset? createdAt = null,
        byte[]? excel = null,
        SnapshotAnalysis? analysis = null)
    {
        var credits = r.Inputs.ReuseCredit + r.Inputs.RecycledMaterialCredit + r.Inputs.LowCarbonMethodCredit;
        return new SnapshotDocument
        {
            Plugin = new PluginInfo(PluginVersion, Manual),
            Project = new ProjectInfo(projectKey, projectTitle),
            Snapshot = new SnapshotInfo(snapshotName, createdAt ?? DateTimeOffset.Now),
            Inputs = r.Inputs,
            Shape = shape,
            Result = new SnapshotResult
            {
                Eec = r.EEC,
                Eecc = r.EECc,
                Tec = r.TEC,
                Tecc = r.TECc,
                Eci = r.ECI,
                Ecis = r.ECIs,
                DeltaCf = r.DeltaCF,
                Cfr = r.CFR,
                Grade = r.Grade,
                Ll = r.Inputs.LL,
                Credits = credits,
                CfsUnderground = r.CFsUnderground,
                Stages = new StageSet(
                    new Stages(r.Design.CFs, r.CFns, r.CFrm, r.CFc, r.CFdw),
                    new Stages(r.Baseline.CFs, r.CFnsBaseline, r.CFrmBaseline, r.CFc, r.CFdw)),
                DesignStructure = r.Design,
                BaselineStructure = r.Baseline,
                Scale = r.Scale.Select(t => new ScaleTick(t.Grade, t.Eci)).ToList(),
            },
            Items = r.DesignItems.Select(i => Item("design", i))
                .Concat(r.BaselineItems.Select(i => Item("baseline", i)))
                .ToList(),
            Excel = excel is null ? null : Convert.ToBase64String(excel),
            Analysis = analysis,
        };
    }

    public static string ToJson(SnapshotDocument s) => JsonSerializer.Serialize(s, DataLoader.Options);

    public static SnapshotDocument? FromJson(string json) => JsonSerializer.Deserialize<SnapshotDocument>(json, DataLoader.Options);

    /// <summary>外掛組件版本（例如 0.5.0），快照用來追溯是哪一版算的。</summary>
    public static string PluginVersion =>
        typeof(SnapshotBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static SnapshotItem Item(string side, NonStructuralItem i) => new()
    {
        Side = side,
        BlccId = i.BlccId,
        Category = i.Category.Code(),
        CategoryName = i.Category.DisplayName(),
        Name = i.Name,
        Area = i.Area,
        NewEf = i.NewEf,
        RenewEf = i.RenewEf,
        NewCarbon = i.NewEmission,
        RenewCarbon = i.RenewEmission,
        Carbon = i.NewEmission + i.RenewEmission,
        Source = i.Source,
    };
}
