using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>
/// 控制台上的一份減碳建議（AI 分析計畫書 6 節）。AI 寫入 proposals.json，使用者在網頁選定一份或多份，
/// 外掛「套用控制台建議」取回選定的全部、合併寫回 Revit，再逐份回報已套用。
/// Status：proposed → chosen → applied。
/// </summary>
public sealed record Proposal
{
    public required string Id { get; init; }
    /// <summary>案子 key（模型檔名），與快照相同。</summary>
    public required string Project { get; init; }
    public required string Title { get; init; }
    public string? Reason { get; init; }
    public required List<ScenarioChange> Changes { get; init; }
    /// <summary>AI 用 lebr-snapshot scenario 試算的預期結果；套用後外掛重算比對。</summary>
    public ProposalResult? Expected { get; init; }
    public string Status { get; init; } = "proposed";
    public ProposalResult? Applied { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
}

public sealed record ProposalResult(double Cfr, string Grade, double? Eec = null);

/// <summary>
/// 多份選定建議一起套用：依序合併替換，完全相同的只留一個（否則第二次會找不到已被換掉的代碼）。
/// 後端選定時已擋下會互相衝突的建議，這裡不再判斷衝突。
/// </summary>
public static class ProposalMerge
{
    public static List<ScenarioChange> Changes(IEnumerable<Proposal> proposals) =>
        proposals.SelectMany(p => p.Changes)
            .DistinctBy(c => (c.From.Trim().ToUpperInvariant(), c.To.Trim().ToUpperInvariant(), c.TypeName, c.DropFinish))
            .ToList();
}

/// <summary>Revit 中一個有 LEBR_BlccId 的類型。</summary>
public sealed record TypeBlccIds(long TypeId, string TypeName, string CategoryName, string Value);

/// <summary>一個類型的 LEBR_BlccId 要從 Old 改成 New。</summary>
public sealed record TypeEdit(long TypeId, string TypeName, string CategoryName, string Old, string New);

/// <summary>
/// 把建議的替換換算成類型參數 LEBR_BlccId 的修改。參數可填多個代碼（分號分隔），只換對應的那一個；
/// DropFinish 時一併拿掉同一類型上的外裝（OW）代碼。不碰 Revit API，方便測試。
/// </summary>
public static class BlccIdEditor
{
    private static readonly char[] Separators = { ';', ',', '；' };

    public static List<string> Split(string value) =>
        value.Split(Separators).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>回傳要改的類型，以及找不到對應類型的替換（訊息）。</summary>
    public static (List<TypeEdit> Edits, List<string> Missing) Plan(
        IEnumerable<TypeBlccIds> types, IEnumerable<ScenarioChange> changes, BlccLibrary lib)
    {
        var current = types.ToDictionary(t => t.TypeId, t => (Type: t, Ids: Split(t.Value)));
        var missing = new List<string>();

        foreach (var c in changes)
        {
            if (!lib.ById.TryGetValue(c.To, out var to))
            {
                missing.Add($"B-LCC 代碼 {c.To} 不存在");
                continue;
            }

            var hit = false;
            foreach (var (type, ids) in current.Values)
            {
                if (c.TypeName is not null && type.TypeName != c.TypeName) continue;
                var i = ids.FindIndex(id => string.Equals(id, c.From, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;

                hit = true;
                ids[i] = to.Id;
                if (c.DropFinish && to.CategoryEnum == NsCategory.CurtainWall)
                    ids.RemoveAll(id => lib.ById.TryGetValue(id, out var b) && b.CategoryEnum == NsCategory.ExteriorFinish);
            }

            if (!hit)
                missing.Add(c.TypeName is null
                    ? $"模型中沒有 LEBR_BlccId 含 {c.From} 的類型"
                    : $"模型中沒有類型「{c.TypeName}」的 LEBR_BlccId 含 {c.From}");
        }

        var edits = current.Values
            .Select(x => new TypeEdit(x.Type.TypeId, x.Type.TypeName, x.Type.CategoryName, x.Type.Value, string.Join(";", x.Ids)))
            .Where(e => !string.Equals(string.Join(";", Split(e.Old)), e.New, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (edits, missing);
    }
}
