using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Rules;
using ShaftChecker.Revit.Collectors;
using ShaftChecker.Revit.Utils;
using ShaftChecker.Revit.Writers;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>
    /// 一鍵檢討：擷取資料 → 執行規則 → 寫回參數 → 目前視圖上色 → 匯出 CSV → 選取違規元件。
    /// 所有模型修改包在同一個 TransactionGroup，可一次復原。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RunCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var cfg = AddinPaths.LoadConfig();
                List<ShaftInfo> shafts = new ShaftCollector(doc, cfg).Collect();

                if (shafts.Count == 0)
                {
                    TaskDialog.Show("管道間檢討",
                        "找不到管道間，請先用「管道間清單」確認 Room 名稱是否符合設定檔規則。");
                    return Result.Succeeded;
                }

                CheckResult result = new RuleEngine().Run(shafts, cfg);
                CheckResult hostResult = result.HostOnly(); // 連結模型是唯讀的，只對主模型寫回與上色

                List<string> missingParams;
                int colored = 0;
                using (var group = new TransactionGroup(doc, "管道間檢討"))
                {
                    group.Start();

                    using (var t = new Transaction(doc, "寫回檢討結果"))
                    {
                        t.Start();
                        missingParams = ParameterWriter.Write(doc, hostResult);
                        t.Commit();
                    }

                    if (GraphicsWriter.CanColor(doc.ActiveView))
                    {
                        using (var t = new Transaction(doc, "違規上色"))
                        {
                            t.Start();
                            colored = GraphicsWriter.Apply(doc, doc.ActiveView, hostResult);
                            t.Commit();
                        }
                    }

                    group.Assimilate();
                }

                string csvPath = CsvExporter.Export(doc.Title, result);
                SelectFailed(uidoc, hostResult);
                ShowSummary(result, csvPath, colored, missingParams);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("管道間檢討", "執行時發生錯誤：\n" + ex);
                return Result.Failed;
            }
        }

        private static void SelectFailed(UIDocument uidoc, CheckResult result)
        {
            var ids = result.Shafts
                .SelectMany(s => s.Issues)
                .Where(i => i.Severity == Severity.Violation)
                .SelectMany(i => i.ElementIds)
                .Distinct()
                .Select(ElementIdUtil.FromLong)
                .Where(id => uidoc.Document.GetElement(id) != null)
                .ToList();

            if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
        }

        private static void ShowSummary(CheckResult result, string csvPath, int colored, List<string> missingParams)
        {
            int total = result.Shafts.Count;
            int pass = result.Shafts.Count(s => s.Overall == Severity.Pass);
            int violation = result.Shafts.Count(s => s.Overall == Severity.Violation);
            int dataMissing = result.Shafts.Count(s => s.Overall == Severity.DataMissing);
            int warning = result.Shafts.Count(s => s.Overall == Severity.Warning);

            var detail = new StringBuilder();
            foreach (var sr in result.Shafts.Where(s => s.Overall != Severity.Pass))
            {
                detail.AppendLine("■ " + sr.Shaft.Label + "：" + Labels.Of(sr.Overall));
                foreach (var i in sr.Issues.Where(x => x.Severity != Severity.Pass))
                {
                    detail.AppendLine("   [" + i.RuleId + "] " + Labels.Of(i.Severity) + "  " + i.Message);
                    if (!string.IsNullOrEmpty(i.Suggestion)) detail.AppendLine("        → " + i.Suggestion);
                }
            }

            string extra = "已上色 " + colored + " 個元件，違規元件已選取。";
            if (result.LinkedCount > 0)
                extra += "\n其中 " + result.LinkedCount + " 個管道間位於連結模型，結果只列在報表中，無法寫回或上色。";
            if (missingParams.Count > 0)
                extra += "\n\n專案缺少參數：" + string.Join("、", missingParams) + "\n請先按「設定參數」，結果才會寫回 Room。";

            var dlg = new TaskDialog("管道間檢討")
            {
                MainInstruction = "檢討 " + total + " 個管道間：合格 " + pass + "、違規 " + violation +
                                  "、資料不足 " + dataMissing + "、警告 " + warning,
                MainContent = extra + "\n\nCSV 報表：" + csvPath,
                ExpandedContent = detail.Length > 0 ? detail.ToString() : "全部合格。"
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "開啟 CSV 報表");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "關閉");

            if (dlg.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(csvPath) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    TaskDialog.Show("管道間檢討", "無法自動開啟，請手動開啟：\n" + csvPath);
                }
            }
        }
    }
}
