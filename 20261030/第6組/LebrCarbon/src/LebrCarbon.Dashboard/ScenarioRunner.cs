using System.Text.Json;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>
/// 一筆替換：把代碼 From 的構件換成 To（同一 B-LCC 類別）。
/// TypeName 有值時只換來源是該 Revit 類型的那一列；DropFinish = 換成帷幕系統時一併拿掉同一類型的外裝（OW）。
/// </summary>
public sealed record ScenarioChange(string From, string To, string? TypeName = null, bool DropFinish = false);

/// <summary>替換不合法（代碼不存在、找不到構件、跨類別），訊息直接給使用者看。</summary>
public sealed class ScenarioException(string message) : Exception(message);

/// <summary>
/// 情境試算（AI 分析計畫書 4、5 節）：複製評估輸入、套用替換，再用同一個計算核心重算。
/// 不改原本的 .lebr.json，也不另寫公式。
/// </summary>
public static class ScenarioRunner
{
    public static LebrResult Run(LebrProjectFile file, BlccLibrary lib, IEnumerable<ScenarioChange> changes)
    {
        var copy = Clone(file);
        ApplyTo(copy, lib, changes);
        return ProjectEvaluator.Evaluate(copy, lib);
    }

    /// <summary>直接改評估輸入（套用控制台建議寫回 Revit 後，同步更新 .lebr.json 用）。</summary>
    public static void ApplyTo(LebrProjectFile file, BlccLibrary lib, IEnumerable<ScenarioChange> changes)
    {
        foreach (var c in changes)
            Apply(file.DesignItems, lib, c);
    }

    /// <summary>直接改設計案構件清單後重算，供分析引擎逐項試算。</summary>
    internal static LebrResult RunEdited(LebrProjectFile file, BlccLibrary lib, Action<List<NonStructuralItem>> edit)
    {
        var copy = Clone(file);
        edit(copy.DesignItems);
        return ProjectEvaluator.Evaluate(copy, lib);
    }

    internal static LebrProjectFile Clone(LebrProjectFile file) =>
        JsonSerializer.Deserialize<LebrProjectFile>(JsonSerializer.Serialize(file, DataLoader.Options), DataLoader.Options)!;

    private static void Apply(List<NonStructuralItem> items, BlccLibrary lib, ScenarioChange c)
    {
        if (!lib.ById.TryGetValue(c.To, out var to))
            throw new ScenarioException($"B-LCC 代碼 {c.To} 不存在");

        var hits = items
            .Select((item, index) => (item, index))
            .Where(x => string.Equals(x.item.BlccId, c.From, StringComparison.OrdinalIgnoreCase)
                        && (c.TypeName is null || x.item.Source.Contains($"「{c.TypeName}」")))
            .ToList();
        if (hits.Count == 0)
            throw new ScenarioException(c.TypeName is null
                ? $"找不到代碼 {c.From} 的構件"
                : $"找不到類型「{c.TypeName}」代碼 {c.From} 的構件");

        foreach (var (item, index) in hits)
        {
            var fromCategory = lib.ById.TryGetValue(c.From, out var from) ? from.CategoryEnum : item.Category;
            if (fromCategory != to.CategoryEnum)
                throw new ScenarioException(
                    $"{c.From}（{fromCategory.DisplayName()}）不能換成 {c.To}（{to.CategoryEnum.DisplayName()}），只能同類別替換");
            items[index] = item with { BlccId = to.Id };
        }

        if (c.DropFinish && to.CategoryEnum == NsCategory.CurtainWall)
        {
            // 外牆類型填 CW;OW 時，兩列的來源相同；拿掉同來源的外裝列
            var sources = hits.Select(h => h.item.Source).Where(s => s.Length > 0).ToHashSet();
            items.RemoveAll(i => i.Category == NsCategory.ExteriorFinish && sources.Contains(i.Source));
        }
    }
}
