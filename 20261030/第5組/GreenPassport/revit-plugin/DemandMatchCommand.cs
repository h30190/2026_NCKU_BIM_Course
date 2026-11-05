using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>B 模型一次只處理一扇門；模型資料先帶入，但需求必須由設計者確認。</summary>
    [Transaction(TransactionMode.Manual)]
    public class DemandMatchCommand : IExternalCommand
    {
        private const double FeetToMm = 304.8;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟 B 模型"; return Result.Failed; }
            var doc = uiDoc.Document;
            var ids = uiDoc.Selection.GetElementIds().ToList();
            if (ids.Count != 1)
            {
                TaskDialog.Show("Green Passport", "請先在 B 模型選取一扇待配置的門，再按「B 需求確認與配對」。");
                return Result.Cancelled;
            }

            var door = doc.GetElement(ids[0]) as FamilyInstance;
            if (door?.Category == null || door.Category.Id.Value != (long)BuiltInCategory.OST_Doors)
            {
                TaskDialog.Show("Green Passport", "目前選取的元件不是門。");
                return Result.Cancelled;
            }

            double hostWallThicknessMm = 0;
            string hostFunction = "";
            if (door.Host is Wall wall)
            {
                hostWallThicknessMm = wall.Width * FeetToMm;
                hostFunction = wall.WallType?.Function.ToString() ?? "";
            }

            string roomContext = "";
            try
            {
                var phase = doc.Phases.get_Item(doc.Phases.Size - 1);
                var from = door.get_FromRoom(phase);
                var to = door.get_ToRoom(phase);
                roomContext = $"From：{from?.Name ?? "（無）"}｜To：{to?.Name ?? "（無）"}";
            }
            catch { roomContext = "相鄰空間：模型未提供"; }

            var snapshot = DoorDataExtractor.Extract(doc, door);
            // Material parameters are often ElementId references. Resolve their names
            // for the B snapshot while retaining the original id in RawValue.
            foreach (var parameter in snapshot.RawParameters.Where(p => p.Storage == "ElementId"))
            {
                if (!long.TryParse(parameter.RawValue, out long value) || value <= 0) continue;
                if (doc.GetElement(new ElementId(value)) is Material material)
                    parameter.DisplayValue = material.Name;
            }
            using (var form = new DemandMatchForm(
                uiDoc,
                door,
                snapshot,
                hostWallThicknessMm,
                roomContext,
                data.Application.Application.Username,
                hostFunction))
            {
                form.ShowDialog();
            }
            return Result.Succeeded;
        }
    }
}
