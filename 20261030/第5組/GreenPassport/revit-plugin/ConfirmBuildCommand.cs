using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;

namespace TemporalBim
{
    /// <summary>
    /// ③執行替換：唯一會寫入模型的指令。
    /// 直接在目前這個 B 檔案上操作，不另存新檔——換一扇門不用開一個新模型，
    /// 全部門都換完之後，使用者自己用 Revit 的「另存新檔」存一份最終版就好
    /// （這樣也不用管哪個檔案是「B-1」，B 檔案本身就是最新狀態）。
    /// 一個 Transaction 內：載入 A 模型來源族群，套用護照尺寸與明確的材質外觀，
    /// 換 Symbol（保留宿主牆、位置與方向），把護照編號寫進 Comments。
    /// 成功回報後端 Allocated；失敗 RollBack，護照狀態維持 Reserved。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ConfirmBuildCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟 B 模型"; return Result.Failed; }
            var doc = uiDoc.Document;

            var selIds = uiDoc.Selection.GetElementIds().ToList();
            if (selIds.Count != 1)
            {
                TaskDialog.Show("Green Passport", "請先選取「一個」已經配對、審核通過的門，再按「執行替換」。");
                return Result.Cancelled;
            }
            var doorEl = doc.GetElement(selIds[0]) as FamilyInstance;
            if (doorEl == null)
            {
                TaskDialog.Show("Green Passport", "選取的不是門元件。");
                return Result.Cancelled;
            }

            List<PassportInfo> reserved;
            try
            {
                reserved = BackendClient.ListPassportsAsync("Reserved").Result;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Green Passport", "抓不到護照清單（後端有跑嗎？go run ./backend）：" + ErrorText.RealMessage(ex));
                return Result.Failed;
            }
            if (reserved.Count == 0)
            {
                TaskDialog.Show("Green Passport", "目前沒有狀態是 Reserved 的護照，請先用「需求配對」審核通過一筆。");
                return Result.Cancelled;
            }

            PassportInfo chosen = reserved.Count == 1 ? reserved[0] : PickOne(reserved);
            if (chosen == null) return Result.Cancelled;

            // 換門邏輯跟②需求配對視窗的「執行替換」按鈕共用，見 DoorSwapHelper.cs
            try
            {
                DoorSwapHelper.Swap(doc, doorEl, chosen);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Green Passport", "寫入模型失敗，已復原（RollBack）：" + ErrorText.RealMessage(ex));
                return Result.Failed;
            }

            // 3. 成功，回報後端
            try
            {
                BackendClient.PatchPassportAsync(chosen.Id, status: "Allocated",
                    targetModel: doc.Title, targetElementId: doorEl.Id.Value.ToString()).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Green Passport",
                    $"模型已經寫入成功，但回報後端失敗（護照狀態可能沒更新）：{ErrorText.RealMessage(ex)}\n可以之後手動用 PATCH /passports/{chosen.Id} 補更新。");
                return Result.Succeeded;
            }

            TaskDialog.Show("Green Passport", $"完成！{chosen.PassportId} 已裝進目前模型（{doc.Title}），護照狀態改為 Allocated。\n全部門都換完後，記得自己用「另存新檔」存一份最終版。");
            return Result.Succeeded;
        }

        private PassportInfo PickOne(List<PassportInfo> options)
        {
            using (var form = new Form())
            {
                form.Text = "選擇要裝入的護照";
                form.Size = new System.Drawing.Size(420, 360);
                form.StartPosition = FormStartPosition.CenterParent;
                var list = new ListBox { Dock = DockStyle.Fill };
                list.Items.AddRange(options.Cast<object>().ToArray());
                var ok = new Button { Text = "選這筆", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 36 };
                form.Controls.Add(list);
                form.Controls.Add(ok);
                form.AcceptButton = ok;
                if (form.ShowDialog() != DialogResult.OK || list.SelectedItem == null) return null;
                return (PassportInfo)list.SelectedItem;
            }
        }
    }
}
