using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    [Transaction(TransactionMode.ReadOnly)]
    public class PassportListCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟一個 Revit 文件"; return Result.Failed; }
            bool changed = false;
            while (true)
            {
                List<PassportInfo> passports;
                try { passports = BackendClient.ListPassportsAsync().Result; }
                catch (Exception ex)
                {
                    TaskDialog.Show("Green Passport", "讀取護照失敗，請確認後端已啟動：" + ErrorText.RealMessage(ex));
                    return Result.Failed;
                }
                if (passports.Count == 0) { TaskDialog.Show("Green Passport", "目前沒有護照資料。"); return Result.Cancelled; }

                PassportInfo selected;
                string action;
                using (var form = new PassportListForm(passports))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK) return changed ? Result.Succeeded : Result.Cancelled;
                    selected = form.SelectedPassport;
                    action = form.SelectedAction;
                }
                if (action == "ApproveAll")
                {
                    using (var batch = new PassportBatchApprovalForm(passports, data.Application.Application.Username))
                    {
                        if (batch.ShowDialog() != System.Windows.Forms.DialogResult.OK) continue;
                        int approved = 0;
                        var failures = new List<string>();
                        foreach (var passport in batch.Ready)
                        {
                            try
                            {
                                BackendClient.ReviewPassportAsync(passport.Id, PassportApproval.Request(passport, batch.Inspector, batch.ReviewNote)).Wait();
                                approved++;
                                changed = true;
                            }
                            catch (Exception ex) { failures.Add(passport.PassportId + "：" + ErrorText.RealMessage(ex)); }
                        }
                        string summary = $"{approved} 筆已通過審核並儲存。清單將顯示綠色的『已通過審核』。";
                        if (failures.Count > 0) summary += "\n\n未儲存成功：\n" + string.Join("\n", failures);
                        TaskDialog.Show("Green Passport｜批次審核結果", summary);
                    }
                    continue;
                }
                if (selected == null) continue;

                if (action == "Review")
                {
                    using (var review = new PassportReviewForm(selected))
                    {
                        if (review.ShowDialog() != System.Windows.Forms.DialogResult.OK) continue;
                        try
                        {
                            var updated = BackendClient.ReviewPassportAsync(selected.Id, review.Review).Result;
                            changed = true;
                            TaskDialog.Show("Green Passport", $"{updated.PassportId}：{ReviewStatusDisplay.Label(updated.ReuseStatus)}（已儲存）。\n返回清單即可查看最新審核結果。");
                        }
                        catch (Exception ex) { TaskDialog.Show("Green Passport", "更新失敗：" + ErrorText.RealMessage(ex)); }
                    }
                    continue;
                }

                string locate = Locate(uiDoc, selected);
                TaskDialog.Show("Green Passport｜" + selected.PassportId, locate + "\n\n" + FormatDetail(selected));
                using (var details = new ParameterDetailsForm(selected)) details.ShowDialog();
            }
        }

        private static string Locate(UIDocument uiDoc, PassportInfo p)
        {
            var doc = uiDoc.Document;
            string idText = string.Equals(p.TargetModel, doc.Title, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.TargetElementId)
                ? p.TargetElementId
                : string.Equals(p.SourceModel, doc.Title, StringComparison.OrdinalIgnoreCase) ? p.SourceElementId : "";
            if (!long.TryParse(idText, out long value)) return "這筆護照不屬於目前模型，僅顯示資料。";
            try
            {
                var id = new ElementId(value);
                if (doc.GetElement(id) == null) return "元件已不存在，僅顯示資料。";
                uiDoc.Selection.SetElementIds(new[] { id }); uiDoc.ShowElements(id);
                return "已定位目前模型中的元件。";
            }
            catch (Exception ex) { return "定位失敗：" + ex.Message; }
        }

        private static string FormatDetail(PassportInfo p)
        {
            string Show(double v) => v > 0 ? v.ToString("0.#") : "待確認";
            var sb = new StringBuilder();
            sb.AppendLine($"審核結果：{ReviewStatusDisplay.Label(p.ReuseStatus)}｜供應狀態：{p.Status}｜Revision {p.Revision}");
            sb.AppendLine($"來源：{p.SourceModel} / Element {p.SourceElementId} / {p.SourceUniqueId}");
            sb.AppendLine($"族群／類型：{p.Family} / {p.TypeName}");
            sb.AppendLine($"尺寸：{Show(p.WidthMm)} × {Show(p.HeightMm)} mm（{p.DimensionDefinition}）｜門扇厚 {Show(p.LeafThicknessMm)} mm");
            sb.AppendLine($"開啟／方向／用途：{p.OperationType} / {p.SwingHanding} / {p.UseType}");
            sb.AppendLine($"材質：{p.Material}｜有效淨寬：{Show(p.ClearWidthMm)} mm｜扇數：{p.LeafCount}");
            sb.AppendLine($"狀況：{p.ConditionGrade} {p.Condition}｜審核說明：{p.ReviewNote}");
            sb.AppendLine($"數量：{p.Quantity}｜價格：NT$ {p.PriceNtd:0}｜檢核人：{p.InspectedBy}");
            sb.AppendLine($"完整模型參數：{p.RawParameters?.Count ?? 0} 筆｜映射警告：{p.MappingWarnings?.Count ?? 0} 筆");
            return sb.ToString();
        }
    }
}
