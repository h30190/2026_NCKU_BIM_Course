using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Revit.Extraction;

namespace LebrCarbon.Revit.Commands;

/// <summary>
/// 套用控制台建議（AI 分析計畫書 6 節，資料流回 Revit 的那一段）：
/// 取回網頁上選定的建議 → 列出要改的類型請使用者確認 → 改類型參數 LEBR_BlccId →
/// 同步更新 {模型}.lebr.json 並重算 → 回報控制台已套用。外掛主動連線，後端從不呼叫 Revit。
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ApplyProposalCommand : IExternalCommand
{
    private const string Title = "套用控制台建議";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show(Title, "請先開啟專案模型（非族群檔）。");
            return Result.Cancelled;
        }

        // 案子 key 與「上傳到控制台」相同：模型檔名，未存檔時用標題
        var key = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : Path.GetFileNameWithoutExtension(doc.PathName);
        using var client = new DashboardClient(DashboardSettings.Load().BaseUrl);
        var fetch = Task.Run(() => client.GetChosenProposalAsync(key)).GetAwaiter().GetResult();
        if (fetch.Proposal is not { } proposal)
        {
            TaskDialog.Show(Title, fetch.Ok
                ? $"{fetch.Message}。\n\n請先在網頁控制台「分析與建議」選一份建議。"
                : fetch.Message);
            return Result.Cancelled;
        }

        var lib = BlccLibrary.Load();
        var (edits, missing) = BlccIdEditor.Plan(CollectTypes(doc), proposal.Changes, lib);
        if (edits.Count == 0)
        {
            TaskDialog.Show(Title, $"建議「{proposal.Title}」在模型中找不到可修改的類型：\n\n• " + string.Join("\n• ", missing));
            return Result.Cancelled;
        }

        if (!Confirm(proposal, edits, missing)) return Result.Cancelled;

        using (var t = new Transaction(doc, $"LEBR 套用建議：{proposal.Title}"))
        {
            t.Start();
            foreach (var e in edits)
                doc.GetElement(new ElementId(e.TypeId))?.LookupParameter(NonStructuralExtractor.ParamBlccId)?.Set(e.New);
            t.Commit();
        }

        var report = new StringBuilder($"已修改 {edits.Count} 個類型的 LEBR_BlccId（可用復原 Ctrl+Z 撤銷）。\n");
        report.Append(SyncProjectFile(doc, lib, proposal, client));
        TaskDialog.Show(Title, report.ToString());
        return Result.Succeeded;
    }

    /// <summary>模型中實際用到、且類型上有 LEBR_BlccId 的類型。</summary>
    private static List<TypeBlccIds> CollectTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(NonStructuralExtractor.Categories))
            .WhereElementIsNotElementType()
            .Select(e => e.GetTypeId())
            .Distinct()
            .Select(id => doc.GetElement(id))
            .Select(t => (Type: t, Value: t?.LookupParameter(NonStructuralExtractor.ParamBlccId)?.AsString()))
            .Where(x => x.Type is not null && !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new TypeBlccIds(x.Type!.Id.Value, x.Type.Name, x.Type.Category?.Name ?? "", x.Value!))
            .ToList();

    private static bool Confirm(Proposal p, List<TypeEdit> edits, List<string> missing)
    {
        var sb = new StringBuilder();
        foreach (var e in edits)
            sb.AppendLine($"• {e.CategoryName}「{e.TypeName}」：{e.Old} → {e.New}");
        if (missing.Count > 0)
            sb.AppendLine().AppendLine("以下替換在模型中找不到，會略過：").AppendLine("• " + string.Join("\n• ", missing));
        if (p.Expected is { } x)
            sb.AppendLine().Append($"控制台試算：減碳率 {x.Cfr:P2}、{x.Grade}");

        var dlg = new TaskDialog(Title)
        {
            MainInstruction = $"把建議「{p.Title}」寫回模型？",
            MainContent = sb.ToString(),
            ExpandedContent = p.Reason ?? "",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        return dlg.Show() == TaskDialogResult.Yes;
    }

    /// <summary>
    /// 同步 {模型}.lebr.json 的明細列並重算，與控制台試算比對後回報已套用。
    /// 評估檔不存在或明細與模型對不上時，只提醒使用者重新「從模型帶入」，不回報。
    /// </summary>
    private static string SyncProjectFile(Document doc, BlccLibrary lib, Proposal p, DashboardClient client)
    {
        var path = ProjectStore.PathFor(doc.PathName, doc.Title);
        var file = ProjectStore.TryLoad(path);
        if (file is null)
            return "\n找不到評估檔，請開「LEBR 評估」→「從模型帶入」→「計算」。";

        try
        {
            ScenarioRunner.ApplyTo(file, lib, p.Changes);
        }
        catch (ScenarioException ex)
        {
            return $"\n評估檔的明細和建議對不上（{ex.Message}），請開「LEBR 評估」→「從模型帶入」→「計算」。";
        }

        var r = ProjectEvaluator.Evaluate(file, lib);
        try
        {
            ProjectStore.Save(path, file);
        }
        catch (IOException ex)
        {
            return $"\n評估檔儲存失敗：{ex.Message}";
        }

        var sb = new StringBuilder($"\n重算結果：減碳率 {r.CFR:P2}、{r.Grade}");
        if (p.Expected is { } x && Math.Abs(x.Cfr - r.CFR) > 0.0005)
            sb.Append($"（控制台試算 {x.Cfr:P2}，數字不同，請確認模型數量是否已變動）");

        var done = Task.Run(() => client.MarkAppliedAsync(p.Id, new ProposalResult(r.CFR, r.Grade, r.EEC))).GetAwaiter().GetResult();
        sb.Append($"\n{done.Message}。可再按「上傳到控制台」留一份套用後的快照。");
        return sb.ToString();
    }
}
