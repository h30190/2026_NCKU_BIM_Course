using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Revit.Services;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>建立並綁定檢討用的共用參數（實際邏輯在 Services/ParameterSetup）。</summary>
    [Transaction(TransactionMode.Manual)]
    public class SetupParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            var cfg = AddinPaths.LoadConfig();

            string log;
            try
            {
                log = ParameterSetup.Ensure(data.Application.Application, doc, cfg);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            TaskDialog.Show("設定參數", "已完成參數設定：\n\n" + log +
                "\n下一步：在門的「" + cfg.SmokeSealParam + "」參數勾選遮煙性能，並確認牆、門類型已填「防火時效」。");
            return Result.Succeeded;
        }
    }
}
