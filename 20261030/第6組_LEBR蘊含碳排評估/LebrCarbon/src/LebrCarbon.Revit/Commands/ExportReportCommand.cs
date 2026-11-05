using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Reports.Pdf;

namespace LebrCarbon.Revit.Commands;

/// <summary>
/// 匯出報表（F10）：依最近一次在「LEBR 評估」按「計算」時存下的評估輸入（{模型名}.lebr.json），
/// 一次匯出 Excel 計算書與 PDF 摘要。不開評估視窗、不讀寫模型。
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ExportReportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            TaskDialog.Show("LEBR 匯出報表", "請先開啟專案模型（非族群檔）。");
            return Result.Cancelled;
        }

        var projectPath = ProjectStore.PathFor(doc.PathName, doc.Title);
        var file = ProjectStore.TryLoad(projectPath);
        if (file is null)
        {
            TaskDialog.Show("LEBR 匯出報表",
                $"還沒有這個模型的評估資料（{Path.GetFileName(projectPath)}）。\n\n請先按「LEBR 評估」，補齊參數後按「計算」，再回來匯出。");
            return Result.Cancelled;
        }

        try
        {
            var result = ProjectEvaluator.Evaluate(file, BlccLibrary.Load());
            var at = DateTime.Now;
            var lines = new List<string>();
            string? folder = null;

            var excel = ReportExporter.WriteExcel(result, file, doc.Title, projectPath, at);
            lines.Add($"Excel 計算書：{excel}");
            folder = Path.GetDirectoryName(excel);

            try
            {
                lines.Add($"PDF 摘要：{ReportExporter.WritePdf(result, file, doc.Title, projectPath, at)}");
            }
            catch (PdfPrinterUnavailableException ex)
            {
                lines.Add($"PDF 摘要：未匯出。{ex.Message}");
            }

            var td = new TaskDialog("LEBR 匯出報表")
            {
                MainInstruction = $"{result.Grade}，碳排減碳率 {result.CFR * 100:0.00}%",
                MainContent = string.Join("\n\n", lines),
                FooterText = $"依 {file.SavedAt:yyyy-MM-dd HH:mm} 儲存的評估輸入計算。模型改過後，請先到「LEBR 評估」重新按「計算」。",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "開啟檔案所在資料夾");
            if (td.Show() == TaskDialogResult.CommandLink1 && folder is not null)
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return Result.Succeeded;
        }
        catch (IOException ex)
        {
            TaskDialog.Show("LEBR 匯出報表", $"無法寫入檔案，若同名檔案正在 Excel 或 PDF 檢視器中開啟，請先關閉。\n\n{ex.Message}");
            return Result.Failed;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 匯出報表失敗", ex.ToString());
            return Result.Failed;
        }
    }
}
