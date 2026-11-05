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
    /// <summary>「專案設定」：開啟表單，儲存時寫回專案資訊的 LEBR_* 參數。</summary>
    [Transaction(TransactionMode.Manual)]
    public class ProjectSettingsCommand : IExternalCommand
    {
        private static LebrDatabase _database;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("LEBR", "請先開啟一個專案（不是族群檔）。");
                return Result.Cancelled;
            }

            try
            {
                var added = SetupParametersCommand.Ensure(doc);
                var missing = ProjectSettingsStore.MissingParameters(doc);
                if (missing.Count > 0)
                {
                    TaskDialog.Show("LEBR", "無法建立下列 LEBR 參數：\n" + string.Join("、", missing));
                    return Result.Failed;
                }
                if (added.Count > 0)
                    TaskDialog.Show("LEBR", "已自動建立 " + added.Count + " 個 LEBR 參數。");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("LEBR", "建立 LEBR 參數失敗：" + ex.Message);
                return Result.Failed;
            }

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

            var table = db.Parameters.LifeExtensionLL;
            var window = new ProjectSettingsWindow(
                ProjectSettingsStore.Read(doc, table),
                table,
                () => ProjectSettingsStore.Read(doc, table),
                doc.Title,
                EstimateOpener(uiApp, doc, folder));
            new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
            if (window.ShowDialog() != true || window.Result == null)
                return Result.Cancelled;

            var m = window.Result;
            var ll = ProjectSettingsRules.ComputeLl(table, m.Structure, m.DurableItemId, m.QualityItemId, m.ProofAttached);
            try
            {
                ProjectSettingsStore.Write(doc, m, ll);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("LEBR", "儲存專案設定時發生錯誤：\n" + ex.Message);
                return Result.Failed;
            }

            TaskDialog.Show("LEBR", "已儲存專案設定。\nLEBR_LL = " + ll.ToString("0.00") + "（上限 " + table.MaxTotal.ToString("0.00") + "）");
            return Result.Succeeded;
        }

        /// <summary>「開啟估算」：讀取附錄一與模型數量，計算後把 CFs 與估算紀錄寫入專案。</summary>
        private static Func<EstimateOutcome> EstimateOpener(UIApplication uiApp, Document doc, string folder)
        {
            return () =>
            {
                PLccTable plcc;
                try
                {
                    plcc = PLccTable.Load(Path.Combine(folder, "data", "estimate", "附錄一_P-LCC_2022.csv"));
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("LEBR", "無法讀取附錄一 P-LCC：\n" + ex.Message);
                    return null;
                }

                var window = new EstimateWindow(
                    () =>
                    {
                        List<string> notes;
                        var rows = new EstimateQuantityProvider(doc).Read(out notes);
                        return Tuple.Create(rows, notes);
                    },
                    plcc,
                    doc.Title,
                    (cfs, json) => ProjectSettingsStore.WriteEstimate(doc, cfs, json));
                new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
                return window.ShowDialog() == true ? window.Outcome : null;
            };
        }
    }
}
