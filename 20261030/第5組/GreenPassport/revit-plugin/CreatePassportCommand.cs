using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TemporalBim
{
    /// <summary>
    /// A-model capture creates Pending passports. Existing records retain their
    /// saved review states; all review actions belong to the review-management UI.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class CreatePassportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "請先開啟 A 模型"; return Result.Failed; }
            var doc = uiDoc.Document;
            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            if (doors.Count == 0)
            {
                TaskDialog.Show("Green Passport", "目前模型沒有可建檔的門元件。");
                return Result.Cancelled;
            }

            Dictionary<string, PassportInfo> existing;
            try
            {
                existing = BackendClient.ListPassportsAsync().Result
                    .Where(p => string.Equals(p.SourceModel, doc.Title, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(p => p.SourceUniqueId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Revision).First());
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Green Passport", "無法讀取已建檔資料，請確認後端已啟動：" + ErrorText.RealMessage(ex));
                return Result.Failed;
            }

            string inspector = data.Application.Application.Username;
            var candidates = doors.Select(door =>
            {
                if (existing.TryGetValue(door.UniqueId, out var saved))
                    return new DoorCandidate { Element = door, Passport = saved };
                var passport = DoorDataExtractor.Extract(doc, door);
                passport.InspectedBy = inspector;
                passport.InspectedOn = DateTime.Now.ToString("yyyy-MM-dd");
                return new DoorCandidate { Element = door, Passport = passport };
            }).ToList();

            int created = 0;
            using (var form = new PassportCandidateForm(candidates, doc.Title, passport =>
            {
                passport.ReuseStatus = "Pending";
                var saved = BackendClient.CreatePassportAsync(passport).Result;
                created++;
                return saved;
            }))
            {
                form.ShowDialog();
            }

            return created > 0 ? Result.Succeeded : Result.Cancelled;
        }
    }

    public class DoorCandidate
    {
        public Element Element { get; set; }
        public PassportInfo Passport { get; set; }

        public void MarkManual(string field, string definition = "")
        {
            Passport.FieldSources[field] = new FieldSourceInfo
            {
                Kind = "manual",
                Definition = definition,
                Verification = "由 Revit 外掛使用者人工填寫／確認",
            };
        }
    }
}
