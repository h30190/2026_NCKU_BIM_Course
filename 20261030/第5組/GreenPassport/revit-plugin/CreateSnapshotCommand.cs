using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// 「建立版本快照」指令：
    /// 1. 掃目前模型的門窗元件（OST_Doors + OST_Windows）
    /// 2. 每個元件擷取技術規格文件第 1 步列的 7 個欄位
    /// 3. 包成 JSON，POST 給後端 /snapshot
    /// 只讀取資料，不改模型，所以用 ReadOnly（不是 Manual）。
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class CreateSnapshotCommand : IExternalCommand
    {
        // Revit 內部單位是英呎（feet），座標要乘這個係數換算成公釐（mm）。
        private const double FeetToMm = 304.8;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟一個專案文件"; return Result.Failed; }
            var doc = uiDoc.Document;

            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(categories.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList()));

            var snapshotElements = collector.Select(el => ExtractSnapshot(doc, el)).ToList();

            if (snapshotElements.Count == 0)
            {
                TaskDialog.Show("Temporal BIM", "目前模型裡沒有找到門窗元件，沒有東西可以存成快照。");
                return Result.Cancelled;
            }

            var req = new SnapshotRequest
            {
                ModelName = doc.Title,
                Elements = snapshotElements,
            };

            try
            {
                var version = BackendClient.CreateSnapshotAsync(req).Result;
                TaskDialog.Show("Temporal BIM",
                    $"快照已建立：{version.Label}\n擷取了 {version.ElementCount} 個門窗元件。");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Temporal BIM", "送出失敗（後端有跑嗎？go run ./backend）：" + ex.Message);
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        private ElementSnapshotInfo ExtractSnapshot(Document doc, Element el)
        {
            string typeName = "";
            var typeElement = doc.GetElement(el.GetTypeId()) as ElementType;
            if (typeElement != null) typeName = typeElement.Name;
            else typeName = el.Name;

            string material = LookupMaterial(el);

            string levelName = "";
            try
            {
                var level = doc.GetElement(el.LevelId) as Level;
                if (level != null) levelName = level.Name;
            }
            catch { /* 有些元件沒有 LevelId，留空就好 */ }

            double px = 0, py = 0, pz = 0;
            var bbox = el.get_BoundingBox(null);
            if (bbox != null)
            {
                px = (bbox.Min.X + bbox.Max.X) / 2.0 * FeetToMm;
                py = (bbox.Min.Y + bbox.Max.Y) / 2.0 * FeetToMm;
                pz = (bbox.Min.Z + bbox.Max.Z) / 2.0 * FeetToMm;
            }

            return new ElementSnapshotInfo
            {
                UniqueId = el.UniqueId,
                ElementId = el.Id.Value.ToString(),
                TypeName = typeName,
                Material = material,
                Quantity = 1, // 門窗每個 instance 算 1
                PositionX = px,
                PositionY = py,
                PositionZ = pz,
                LevelName = levelName,
            };
        }

        /// <summary>
        /// 材質沒有統一的標準參數名稱，先試常見的「材質」「Material」，
        /// 兩個都找不到就留空字串（不要讓整個快照失敗）。
        /// </summary>
        private string LookupMaterial(Element el)
        {
            var param = el.LookupParameter("材質") ?? el.LookupParameter("Material");
            if (param == null) return "";
            if (param.StorageType == StorageType.String) return param.AsString() ?? "";
            return param.AsValueString() ?? "";
        }
    }
}
