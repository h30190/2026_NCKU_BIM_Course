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
/// 取回網頁上選定的建議（可多份，後端已確保彼此不衝突）→ 合併替換、列出要改的類型請使用者確認 →
/// 改類型參數 LEBR_BlccId → 同步更新 {模型}.lebr.json 並重算 → 逐份回報控制台已套用。外掛主動連線，後端從不呼叫 Revit。
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

        var key = ConsoleCase.KeyFor(doc);
        using var client = new DashboardClient(DashboardSettings.Load().BaseUrl);
        var fetch = Task.Run(() => client.GetChosenProposalsAsync(key)).GetAwaiter().GetResult();
        var proposals = fetch.Proposals;
        if (proposals.Count == 0)
        {
            TaskDialog.Show(Title, fetch.Ok
                ? $"{fetch.Message}。\n\n請先在網頁控制台「分析與建議」選定一份或多份建議。"
                : fetch.Message);
            return Result.Cancelled;
        }

        var lib = BlccLibrary.Load();
        var changes = ProposalMerge.Changes(proposals);
        var (edits, missing) = BlccIdEditor.Plan(CollectTypes(doc), changes, lib);
        if (edits.Count == 0)
        {
            TaskDialog.Show(Title, $"{Names(proposals)}在模型中找不到可修改的類型：\n\n• " + string.Join("\n• ", missing));
            return Result.Cancelled;
        }

        if (!Confirm(proposals, edits, missing)) return Result.Cancelled;

        // 多份建議放在同一個交易：Ctrl+Z 一次全部撤銷
        using (var t = new Transaction(doc, proposals.Count == 1 ? $"LEBR 套用建議：{proposals[0].Title}" : $"LEBR 套用 {proposals.Count} 份建議"))
        {
            t.Start();
            foreach (var e in edits)
                doc.GetElement(new ElementId(e.TypeId))?.LookupParameter(NonStructuralExtractor.ParamBlccId)?.Set(e.New);
            t.Commit();
        }

        var report = new StringBuilder($"已修改 {edits.Count} 個類型的 LEBR_BlccId（可用復原 Ctrl+Z 撤銷）。\n");
        report.Append(SyncProjectFile(doc, lib, proposals, changes, client));
        TaskDialog.Show(Title, report.ToString());
        return Result.Succeeded;
    }

    private static string Names(List<Proposal> ps) =>
        ps.Count == 1 ? $"建議「{ps[0].Title}」" : $"選定的 {ps.Count} 份建議";

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

    private static bool Confirm(List<Proposal> ps, List<TypeEdit> edits, List<string> missing)
    {
        var sb = new StringBuilder();
        if (ps.Count > 1)
            sb.AppendLine("一起套用：").AppendLine("• " + string.Join("\n• ", ps.Select(p => p.Title))).AppendLine();
        foreach (var e in edits)
            sb.AppendLine($"• {e.CategoryName}「{e.TypeName}」：{e.Old} → {e.New}");
        if (missing.Count > 0)
            sb.AppendLine().AppendLine("以下替換在模型中找不到，會略過：").AppendLine("• " + string.Join("\n• ", missing));
        if (ps.Count == 1 && ps[0].Expected is { } x)
            sb.AppendLine().Append($"控制台試算：減碳率 {x.Cfr:P2}、{x.Grade}");
        else if (ps.Count > 1)
            sb.AppendLine().Append("控制台的試算是各份單獨算的；合併後的減碳率以套用後重算為準。");

        var dlg = new TaskDialog(Title)
        {
            MainInstruction = $"把{Names(ps)}寫回模型？",
            MainContent = sb.ToString(),
            ExpandedContent = string.Join("\n\n", ps.Where(p => !string.IsNullOrWhiteSpace(p.Reason)).Select(p => $"【{p.Title}】{p.Reason}")),
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        return dlg.Show() == TaskDialogResult.Yes;
    }

    /// <summary>
    /// 同步 {模型}.lebr.json 的明細列並重算，再逐份回報已套用（同一個重算結果）。
    /// 評估檔不存在或明細與模型對不上時，只提醒使用者重新「從模型帶入」，不回報。
    /// </summary>
    private static string SyncProjectFile(Document doc, BlccLibrary lib, List<Proposal> ps, List<ScenarioChange> changes, DashboardClient client)
    {
        var path = ProjectStore.PathFor(doc.PathName, doc.Title);
        var file = ProjectStore.TryLoad(path);
        if (file is null)
            return "\n找不到評估檔，請開「LEBR 評估」→「從模型帶入」→「計算」。";

        try
        {
            ScenarioRunner.ApplyTo(file, lib, changes);
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
        if (ps.Count == 1 && ps[0].Expected is { } x && Math.Abs(x.Cfr - r.CFR) > 0.0005)
            sb.Append($"（控制台試算 {x.Cfr:P2}，數字不同，請確認模型數量是否已變動）");

        var result = new ProposalResult(r.CFR, r.Grade, r.EEC);
        var failed = ps
            .Select(p => (p, done: Task.Run(() => client.MarkAppliedAsync(p.Id, result)).GetAwaiter().GetResult()))
            .Where(x => !x.done.Ok)
            .ToList();
        sb.Append(failed.Count == 0
            ? $"\n已回報控制台（{ps.Count} 份）。可再按「上傳到控制台」留一份套用後的快照。"
            : "\n回報控制台失敗：" + string.Join("；", failed.Select(f => $"「{f.p.Title}」{f.done.Message}")));
        return sb.ToString();
    }
}
