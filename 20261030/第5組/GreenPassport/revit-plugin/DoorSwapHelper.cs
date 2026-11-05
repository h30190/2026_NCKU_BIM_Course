using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace TemporalBim
{
    /// <summary>
    /// 載入來源門族群與材質，再於同一交易替換選取門；失敗時全部回復。
    /// </summary>
    public static class DoorSwapHelper
    {
        private const double FeetToMm = 304.8;

        public static DoorSwapAudit Swap(Document doc, FamilyInstance doorEl, PassportInfo chosen)
        {
            if (doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("模型目前無法開啟替換交易，請結束其他編輯後重試。");
            var source = FindSourceDoor(doc, chosen);
            if (source.Symbol.Family.FamilyPlacementType != doorEl.Symbol.Family.FamilyPlacementType)
                throw new InvalidOperationException("來源門與目標門的宿主方式不同，無法直接替換。");
            string originalUniqueId = doorEl.UniqueId;
            string originalTypeId = doorEl.GetTypeId().Value.ToString();
            ElementId originalHost = doorEl.Host?.Id;
            XYZ location = (doorEl.Location as LocationPoint)?.Point;
            XYZ facing = doorEl.FacingOrientation, hand = doorEl.HandOrientation;
            bool pinned = doorEl.Pinned;
            // Isolate each import so existing families and other doors are not overwritten.
            string familyName = "GP_Source_" + SafeName(chosen.PassportId) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string exportDir = Path.Combine(Path.GetTempPath(), "GreenPassportFamilies", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exportDir);
            string familyFile = Path.Combine(exportDir, familyName + ".rfa");
            ExportFamily(source, familyFile);
            string newTypeId = "";
            using (var tx = new Transaction(doc, "Green Passport：套用 " + chosen.PassportId))
            {
                try
                {
                    tx.Start();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                        .SetClearAfterRollback(true).SetFailuresPreprocessor(new RollbackOnError()));

                    if (!doc.LoadFamily(familyFile, new IsolatedFamilyLoadOptions(), out Family loaded) || loaded == null)
                        throw new InvalidOperationException("來源族群載入失敗，可能含有衝突的共用族群；未變更目標門。");
                    if (loaded.Name != familyName)
                        throw new InvalidOperationException("無法隔離來源族群名稱，已取消以免覆寫其他門。");
                    var newType = loaded.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                        .SingleOrDefault(t => t != null && t.Name == source.Symbol.Name);
                    if (newType == null) throw new InvalidOperationException("載入的族群找不到護照來源門類型。");
                    newType.Name = "GP_" + chosen.PassportId;
                    if (!newType.IsActive) newType.Activate();
                    if (pinned) doorEl.Pinned = false;
                    doorEl.Symbol = newType;
                    doc.Regenerate();
                    SetDimension(newType, doorEl, BuiltInParameter.FAMILY_WIDTH_PARAM, chosen.WidthMm, "門寬");
                    SetDimension(newType, doorEl, BuiltInParameter.FAMILY_HEIGHT_PARAM, chosen.HeightMm, "門高");
                    SetThickness(newType, doorEl, chosen.LeafThicknessMm);
                    CopyInstanceMaterials(source, doorEl);
                    ApplyDeclaredPanelMaterial(doc, newType, doorEl, chosen);
                    doc.Regenerate();
                    if (doorEl.FacingOrientation.DotProduct(facing) < 0 && !doorEl.flipFacing())
                        throw new InvalidOperationException("來源族群無法保留原門面向。");
                    if (doorEl.HandOrientation.DotProduct(hand) < 0 && !doorEl.flipHand())
                        throw new InvalidOperationException("來源族群無法保留原門開啟方向。");
                    doc.Regenerate();
                    if (doorEl.FacingOrientation.DotProduct(facing) < 0.999 || doorEl.HandOrientation.DotProduct(hand) < 0.999)
                        throw new InvalidOperationException("替換後方向與原門不一致，已取消。");
                    if (!Equals(doorEl.Host?.Id, originalHost) ||
                        (location != null && ((doorEl.Location as LocationPoint)?.Point.DistanceTo(location) ?? double.MaxValue) > 0.00001))
                        throw new InvalidOperationException("替換後宿主或位置改變，已取消。");
                    VerifyDimension(newType, doorEl, BuiltInParameter.FAMILY_WIDTH_PARAM, chosen.WidthMm, "門寬");
                    VerifyDimension(newType, doorEl, BuiltInParameter.FAMILY_HEIGHT_PARAM, chosen.HeightMm, "門高");
                    newTypeId = newType.Id.Value.ToString();

                    var comments = doorEl.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    string passportComment = "GreenPassport:" + chosen.PassportId;
                    if (comments == null || (comments.AsString() != passportComment &&
                        (comments.IsReadOnly || !comments.Set(passportComment))))
                        throw new InvalidOperationException("無法寫入護照追蹤備註，已取消。");
                    SetText(doorEl, "GP_室內外用途", chosen.UseType);
                    SetText(doorEl, "GP_開啟方式", chosen.OperationType);
                    SetText(doorEl, "GP_開門方向", chosen.SwingHanding);
                    SetText(doorEl, "材質", chosen.Material);
                    if (pinned) doorEl.Pinned = true;
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit 未完成替換交易；不得標記為已配置。");
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    throw;
                }
            }
            return new DoorSwapAudit
            {
                OriginalUniqueId = originalUniqueId,
                TargetUniqueId = doorEl.UniqueId,
                OriginalTypeId = originalTypeId,
                NewTypeId = newTypeId,
            };
        }

        private static FamilyInstance FindSourceDoor(Document target, PassportInfo chosen)
        {
            if (string.IsNullOrWhiteSpace(chosen.SourceUniqueId) || string.IsNullOrWhiteSpace(chosen.SourceModel))
                throw new InvalidOperationException("護照缺少來源模型或 UniqueId，無法辨識真正的來源門。");
            var matches = new List<FamilyInstance>();
            foreach (Document candidate in target.Application.Documents)
            {
                if (candidate.IsFamilyDocument || !string.Equals(Path.GetFileNameWithoutExtension(candidate.Title),
                    Path.GetFileNameWithoutExtension(chosen.SourceModel), StringComparison.OrdinalIgnoreCase)) continue;
                var instance = candidate.GetElement(chosen.SourceUniqueId) as FamilyInstance;
                if (instance?.Category?.Id.Value == (long)BuiltInCategory.OST_Doors) matches.Add(instance);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException("請先在同一個 Revit 開啟來源模型「" + chosen.SourceModel + "」，並確認來源門仍存在。多份同名來源請只保留正確的一份。");
            var source = matches[0];
            if (source.Symbol.FamilyName != chosen.Family || source.Symbol.Name != chosen.TypeName)
                throw new InvalidOperationException("來源門族群／類型已與護照不同，請先重新確認來源資料。");
            return source;
        }

        private static void ExportFamily(FamilyInstance source, string path)
        {
            if (!source.Symbol.Family.IsEditable || source.Document.IsReadOnly)
                throw new InvalidOperationException("來源族群不可匯出。請開啟原始 A 模型，而非唯讀連結或就地族群。");
            Document familyDoc = null;
            try
            {
                familyDoc = source.Document.EditFamily(source.Symbol.Family);
                familyDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            }
            finally { familyDoc?.Close(false); }
        }

        private static Parameter Dimension(FamilySymbol type, FamilyInstance instance, BuiltInParameter bip) =>
            type.get_Parameter(bip) ?? instance.get_Parameter(bip);

        private static void SetDimension(FamilySymbol type, FamilyInstance instance, BuiltInParameter bip, double mm, string label) =>
            SetLength(Dimension(type, instance, bip), mm, label);

        private static void SetLength(Parameter p, double mm, string label)
        {
            if (mm <= 0 || p == null || p.StorageType != StorageType.Double)
                throw new InvalidOperationException("來源族群缺少可確認的" + label + "參數。");
            if (Math.Abs(p.AsDouble() * FeetToMm - mm) <= 0.1) return;
            if (p.IsReadOnly || !p.Set(mm / FeetToMm))
                throw new InvalidOperationException("來源族群的" + label + "受公式或唯讀限制，無法套用護照尺寸。");
        }

        private static void VerifyDimension(FamilySymbol type, FamilyInstance instance, BuiltInParameter bip, double mm, string label)
        {
            var p = Dimension(type, instance, bip);
            if (p == null || Math.Abs(p.AsDouble() * FeetToMm - mm) > 0.1)
                throw new InvalidOperationException("替換後" + label + "不符合護照，已取消。");
        }

        private static void SetThickness(FamilySymbol type, FamilyInstance instance, double mm)
        {
            var p = type.get_Parameter(BuiltInParameter.WINDOW_THICKNESS) ?? instance.get_Parameter(BuiltInParameter.WINDOW_THICKNESS);
            if (p == null)
                p = new[] { "Leaf Thickness", "Panel Thickness", "Thickness", "門扇厚度", "厚度" }
                    .Select(n => type.LookupParameter(n) ?? instance.LookupParameter(n))
                    .FirstOrDefault(v => v != null && v.StorageType == StorageType.Double);
            SetLength(p, mm, "門扇厚度");
        }

        private static void CopyInstanceMaterials(FamilyInstance source, FamilyInstance target)
        {
            foreach (Parameter p in source.Parameters)
            {
                if (p.Definition.GetDataType() != SpecTypeId.Reference.Material || p.StorageType != StorageType.ElementId) continue;
                if (!(source.Document.GetElement(p.AsElementId()) is Material material)) continue;
                var dest = p.IsShared ? target.get_Parameter(p.GUID) : target.LookupParameter(p.Definition.Name);
                if (dest == null || dest.IsReadOnly) continue;
                var loaded = new FilteredElementCollector(target.Document).OfClass(typeof(Material)).Cast<Material>()
                    .FirstOrDefault(m => m.Name == material.Name);
                if (loaded == null || (dest.AsElementId() != loaded.Id && !dest.Set(loaded.Id)))
                    throw new InvalidOperationException("無法套用來源實體材質：" + p.Definition.Name);
            }
        }

        private static void ApplyDeclaredPanelMaterial(Document doc, FamilySymbol type, FamilyInstance instance, PassportInfo chosen)
        {
            // A sample family can still have Birch despite a declared white PVC panel.
            if (!DoorAppearancePolicy.HasWhiteFinish(chosen.Material)) return;
            var parameters = type.Parameters.Cast<Parameter>().Concat(instance.Parameters.Cast<Parameter>())
                .Where(p => p.StorageType == StorageType.ElementId && p.Definition.GetDataType() == SpecTypeId.Reference.Material &&
                    DoorAppearancePolicy.IsPanelMaterial(p.Definition.Name)).ToList();
            if (parameters.Count == 0)
                throw new InvalidOperationException("護照指定白色門扇，但來源族群未提供門板材質參數，請先編輯來源族群。");
            string name = "GP_" + SafeName(chosen.PassportId) + "_" + SafeName(chosen.Material) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var material = doc.GetElement(Material.Create(doc, name)) as Material;
            material.Color = new Color(245, 245, 245);
            material.Transparency = 0;
            material.Shininess = 35;
            material.Smoothness = 50;
            // Without an appearance asset Revit uses color/transparency for rendering.
            foreach (var p in parameters)
                if (p.IsReadOnly || !p.Set(material.Id))
                    throw new InvalidOperationException("門板材質為唯讀，無法套用護照指定的白色。");
            SetText(type, "COBie.Type.Material", chosen.Material);
        }

        private static void SetText(Element element, string name, string value)
        {
            var p = element.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value ?? "");
        }

        private static string SafeName(string value) => new string((value ?? "").Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) || "{}[]|;<>?`~:".Contains(c) ? '_' : c).ToArray());

        private sealed class IsolatedFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            { overwriteParameterValues = false; return false; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Project; overwriteParameterValues = false; return false; }
        }

        private sealed class RollbackOnError : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor) =>
                failuresAccessor.GetFailureMessages().Any(m => m.GetSeverity() == FailureSeverity.Error)
                    ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    public class DoorSwapAudit
    {
        public string OriginalUniqueId { get; set; }
        public string TargetUniqueId { get; set; }
        public string OriginalTypeId { get; set; }
        public string NewTypeId { get; set; }
    }
}
