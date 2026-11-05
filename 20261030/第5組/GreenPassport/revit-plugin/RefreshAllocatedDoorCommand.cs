using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>Upgrade a door previously allocated by the dimension-only implementation.</summary>
    [Transaction(TransactionMode.Manual)]
    public class RefreshAllocatedDoorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟 B 模型。"; return Result.Failed; }
            var doc = uiDoc.Document;
            var ids = uiDoc.Selection.GetElementIds();
            var door = ids.Count == 1 ? doc.GetElement(ids.First()) as FamilyInstance : null;
            if (door?.Category?.Id.Value != (long)BuiltInCategory.OST_Doors)
            {
                TaskDialog.Show("更新已配置門", "請先選取一扇已套用 Green Passport 的門。");
                return Result.Cancelled;
            }
            string comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            const string prefix = "GreenPassport:";
            if (!comments.StartsWith(prefix, StringComparison.Ordinal))
            {
                TaskDialog.Show("更新已配置門", "所選門沒有護照備註，請先使用「B｜確認需求、配對與替換」。");
                return Result.Cancelled;
            }
            PassportInfo passport;
            try
            {
                passport = BackendClient.ListPassportsAsync().GetAwaiter().GetResult()
                    .SingleOrDefault(p => p.PassportId == comments.Substring(prefix.Length).Trim());
            }
            catch (Exception ex) { message = "無法讀取護照：" + ErrorText.RealMessage(ex); return Result.Failed; }
            if (passport == null || passport.Status != "Allocated" || passport.TargetModel != doc.Title ||
                passport.TargetElementId != door.Id.Value.ToString(CultureInfo.InvariantCulture))
            {
                TaskDialog.Show("更新已配置門", "護照必須為已配置（Allocated），且目標模型與 Element ID 必須對應目前選取門；不會重新配置其他門。");
                return Result.Cancelled;
            }
            if (TaskDialog.Show("更新已配置門", "將以 " + passport.PassportId + " 的來源族群更新此門外觀與材質。\n請確認 A 模型已在同一 Revit 開啟。此動作不會重新占用護照。",
                TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel) != TaskDialogResult.Ok) return Result.Cancelled;

            DoorSwapAudit audit;
            try
            {
                passport = BackendClient.PatchPassportAsync(passport.Id, status: "Allocated", targetModel: doc.Title,
                    targetElementId: door.Id.Value.ToString(CultureInfo.InvariantCulture), targetUniqueId: door.UniqueId,
                    actor: data.Application.Application.Username, reason: "更新前確認既有配置與版本", expectedRevision: passport.Revision)
                    .GetAwaiter().GetResult();
                audit = DoorSwapHelper.Swap(doc, door, passport);
            }
            catch (Exception ex) { message = "來源族群與材質更新未完成：" + ErrorText.RealMessage(ex); return Result.Failed; }
            try
            {
                BackendClient.PatchPassportAsync(passport.Id, status: "Allocated", targetModel: doc.Title,
                    targetElementId: door.Id.Value.ToString(CultureInfo.InvariantCulture), targetUniqueId: audit.TargetUniqueId,
                    originalUniqueId: audit.OriginalUniqueId, reason: "更新既有配置：套用來源族群與護照材質",
                    actor: data.Application.Application.Username, originalTypeId: audit.OriginalTypeId, newTypeId: audit.NewTypeId,
                    expectedRevision: passport.Revision)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("更新已配置門", "Revit 外觀已更新，但後端稽核補登失敗；請勿重複執行：" + ErrorText.RealMessage(ex));
                return Result.Succeeded;
            }
            uiDoc.Selection.SetElementIds(new[] { door.Id });
            uiDoc.RefreshActiveView();
            TaskDialog.Show("更新已配置門", "完成：" + passport.PassportId + " 已套用來源族群與材質。\n請切換到 3D 視圖，使用「著色」或「擬真」檢查門板顏色。\n請儲存模型；平面圖的黑白線條不一定會改變。");
            return Result.Succeeded;
        }
    }
}
