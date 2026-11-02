using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LebrCarbon.Revit;
using LebrCarbon.UI;
using LebrCarbon;

namespace LebrCarbon
{
    /// <summary>「開始計算」：自動估算主結構 → 計算前檢查 → 計算 → 結果視窗。只會把主結構估算寫回專案資訊。</summary>
    [Transaction(TransactionMode.Manual)]
    public class CalculateCommand : IExternalCommand
    {
        private static LebrDatabase _database;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            LebrDatabase db;
            try
            {
                db = _database ?? (_database = LebrDatabase.Load(Path.Combine(folder, "lebr_2025.json")));
            }
            catch (Exception ex)
            {
                TaskDialog.Show("LEBR", "無法讀取 LEBR 資料庫 lebr_2025.json：\n" + ex.Message);
                return Result.Failed;
            }

            var uiApp = commandData.Application;
            var doc = uiApp.ActiveUIDocument?.Document;
            var calculator = new LebrCalculator(db);
            Func<QuantityInput> readModel = null;
            RefreshResult estimate = null;
            if (doc != null && !doc.IsFamilyDocument)
                readModel = () => RefreshEstimate(doc, folder, new RevitQuantityProvider(doc, db).Read(), out estimate);
            var precheck = new PrecheckWindow(calculator.Calculate, Path.Combine(folder, "samples", "appendix3_quantities.json"),
                                              readModel, doc?.Title);
            new WindowInteropHelper(precheck).Owner = uiApp.MainWindowHandle;
            if (precheck.ShowDialog() != true || precheck.Result == null)
                return Result.Cancelled;

            // 算完就記錄：估算有變（或第一次估算）時寫回專案資訊，專案設定與下次計算都看得到
            if (estimate != null && estimate.Changed)
            {
                try
                {
                    ProjectSettingsStore.WriteEstimate(doc, estimate.CFs, estimate.EstimateJson);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("LEBR", "主結構估算無法寫回專案資訊（這次結果不受影響）：\n" + ex.Message);
                }
            }

            var uiDoc = uiApp.ActiveUIDocument;
            var window = new ResultWindow(precheck.Result, ids => SelectElements(uiDoc, ids));
            new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
            window.ShowDialog();
            return Result.Succeeded;
        }

        /// <summary>
        /// 每次計算都依目前模型的混凝土、鋼筋數量自動估算主結構（BIM 數量法），不必再到專案設定按估算。
        /// 材料沿用上次選的，沒選過的品類用預設材料。模型沒有結構柱、梁、板、牆或鋼筋時改用標準公式。
        /// 估算結果經 estimate 帶出，按「開始」後才寫回模型；重算失敗時沿用已儲存的估算並提醒。
        /// </summary>
        private static QuantityInput RefreshEstimate(Document doc, string folder, QuantityInput input, out RefreshResult estimate)
        {
            estimate = null;
            var p = input.Project;
            if (p == null) return input;
            if (input.Notes == null) input.Notes = new List<string>();
            try
            {
                List<string> quantityNotes;
                var rows = new EstimateQuantityProvider(doc).Read(out quantityNotes).Where(r => r.Quantity > 0).ToList();
                if (rows.Count == 0)
                {
                    if (!(p.StructureCFs > 0))
                        input.Notes.Add("模型裡沒有結構柱、梁、樓板、牆或鋼筋的數量，主結構改用標準公式（式 1～7）。");
                    return input;
                }
                var table = PLccTable.Load(Path.Combine(folder, "data", "estimate", "附錄一_P-LCC_2022.csv"));
                var previous = p.StructureCFs > 0 ? p.StructureCFs : null;
                var refreshed = EstimateRefresh.Recompute(rows, EstimateRefresh.ReadSelections(p.StructureEstimateJson), table, doc.Title, DateTime.Now, previous);
                p.StructureCFs = refreshed.CFs;
                p.StructureEstimateJson = refreshed.EstimateJson;
                input.Notes.Add(EstimateRefresh.Describe(refreshed));
                estimate = refreshed;
            }
            catch (Exception ex)
            {
                input.Notes.Add(p.StructureCFs > 0
                    ? "主結構估算無法依目前模型重算，沿用已儲存的估算值：" + ex.Message
                    : "主結構估算失敗，改用標準公式：" + ex.Message);
            }
            return input;
        }

        /// <summary>在模型中選取明細列對應的元件；範例資料沒有元件 ID 時不動作。</summary>
        private static bool SelectElements(UIDocument uiDoc, IList<long> ids)
        {
            if (uiDoc == null || ids == null || ids.Count == 0) return false;
            var existing = ids.Select(i => new ElementId(i)).Where(id => uiDoc.Document.GetElement(id) != null).ToList();
            if (existing.Count == 0) return false;
            uiDoc.Selection.SetElementIds(existing);
            uiDoc.ShowElements(existing);
            return true;
        }
    }
}
