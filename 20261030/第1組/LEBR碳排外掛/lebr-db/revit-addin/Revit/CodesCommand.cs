using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace LebrCarbon.Revit
{
    /// <summary>「代號 JSON」：把這個模型的 LEBR 代號匯出成 JSON，或把別人匯出的 JSON 套用到這個模型。</summary>
    [Transaction(TransactionMode.Manual)]
    public class CodesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("LEBR", "請先開啟一個專案（不是族群檔）。");
                return Result.Cancelled;
            }

            var choose = new TaskDialog("LEBR 代號 JSON")
            {
                MainInstruction = "要匯出還是套用代號？",
                MainContent = "代號 JSON 記錄類型上的 LEBR_Code、LEBR_Code_Body，專案資訊的 LEBR 參數，以及勾選 LEBR_Exclude 的元件。",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            choose.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "套用代號（從 JSON 檔）",
                "自動建立缺少的參數，再依類型名稱填入代號。JSON 沒寫到的類型不會被改。");
            choose.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "匯出代號（存成 JSON 檔）",
                "把這個模型已填好的代號存起來，給組員套用到他們的模型。");
            var picked = choose.Show();
            if (picked == TaskDialogResult.CommandLink1) return Apply(doc, ref message);
            if (picked == TaskDialogResult.CommandLink2) return Export(doc);
            return Result.Cancelled;
        }

        private static Result Export(Document doc)
        {
            var file = CodeMapping.Export(doc);
            if (file.Types.Count == 0 && file.Project.Count == 0)
            {
                TaskDialog.Show("LEBR", "這個模型還沒有填任何 LEBR 代號或專案參數，沒有東西可以匯出。");
                return Result.Cancelled;
            }
            var dialog = new SaveFileDialog
            {
                Title = "匯出 LEBR 代號",
                Filter = "JSON 檔 (*.json)|*.json",
                FileName = doc.Title + "_LEBR代號.json",
            };
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
            TaskDialog.Show("LEBR", "已匯出：" + file.Types.Count + " 個類型、" + file.Project.Count + " 個專案參數、"
                                    + file.Excluded.Count + " 個不計入元件。\n\n" + dialog.FileName);
            return Result.Succeeded;
        }

        private static Result Apply(Document doc, ref string message)
        {
            var dialog = new OpenFileDialog { Title = "選擇 LEBR 代號 JSON", Filter = "JSON 檔 (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            CodeFile file;
            try
            {
                file = JsonConvert.DeserializeObject<CodeFile>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                if (file == null || file.Format != "lebr-codes") throw new InvalidDataException("不是 LEBR 代號 JSON（format 應為 lebr-codes）。");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("LEBR", "無法讀取這個檔案：\n" + ex.Message);
                return Result.Cancelled;
            }

            ApplyReport report;
            try
            {
                report = CodeMapping.Apply(doc, file);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("LEBR", "套用代號失敗：" + ex.Message);
                return Result.Failed;
            }

            var text = new StringBuilder();
            if (report.Added.Count > 0) text.AppendLine("先建立了 " + report.Added.Count + " 個 LEBR 參數。");
            text.AppendLine("類型代號：" + report.TypesSet + " / " + file.Types.Count);
            text.AppendLine("專案資訊：" + report.ProjectSet + " / " + file.Project.Count);
            text.AppendLine("不計入元件：" + report.ExcludedSet + " / " + file.Excluded.Count);
            if (report.Missing.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("下面這些沒套到，請手動確認：");
                foreach (var m in report.Missing.Take(15)) text.AppendLine("・" + m);
                if (report.Missing.Count > 15) text.AppendLine("…另有 " + (report.Missing.Count - 15) + " 項");
            }
            text.AppendLine();
            text.Append("可以按「開始計算」了。要復原請按 Ctrl+Z。");
            new TaskDialog("LEBR 代號已套用")
            {
                MainInstruction = report.Missing.Count == 0 ? "全部套用完成" : "部分套用完成",
                MainContent = text.ToString(),
            }.Show();
            return Result.Succeeded;
        }
    }
}
