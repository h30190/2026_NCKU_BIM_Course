using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LebrCarbon.Revit.Commands;

/// <summary>一鍵把計畫書 4.3 節的共用參數加入專案。GUID 固定，跨專案一致。</summary>
[Transaction(TransactionMode.Manual)]
public sealed class BindParametersCommand : IExternalCommand
{
    private sealed record ParamSpec(string Name, Guid Guid, ForgeTypeId Spec, bool IsType, BuiltInCategory[] Categories);

    private static readonly BuiltInCategory[] Structural =
    {
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation,
    };

    private static readonly ParamSpec[] Specs =
    {
        new("LEBR_FactorId", new("6f1d2a10-5b3e-4c8a-9d01-1e0a0c0f0001"), SpecTypeId.String.Text, false,
            Structural.Append(BuiltInCategory.OST_Materials).ToArray()),
        new("LEBR_Scope", new("6f1d2a10-5b3e-4c8a-9d01-1e0a0c0f0002"), SpecTypeId.String.Text, false, Structural),
        new("LEBR_RebarRatio", new("6f1d2a10-5b3e-4c8a-9d01-1e0a0c0f0003"), SpecTypeId.Number, true, Structural),
        new("LEBR_EC", new("6f1d2a10-5b3e-4c8a-9d01-1e0a0c0f0004"), SpecTypeId.Number, false, Structural),
        // 非主結構：類型上填 B-LCC ID（可多個，用分號分隔）
        new("LEBR_BlccId", new("6f1d2a10-5b3e-4c8a-9d01-1e0a0c0f0005"), SpecTypeId.String.Text, true, new[]
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_CurtainWallPanels,
        }),
    };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var app = data.Application.Application;
        var doc = data.Application.ActiveUIDocument.Document;
        var original = app.SharedParametersFilename;
        var tempFile = Path.Combine(Path.GetTempPath(), "LebrCarbon_SharedParameters.txt");

        try
        {
            if (!File.Exists(tempFile)) File.WriteAllText(tempFile, "");
            app.SharedParametersFilename = tempFile;
            var defFile = app.OpenSharedParameterFile()
                          ?? throw new InvalidOperationException("無法開啟暫存共用參數檔");
            var group = defFile.Groups.get_Item("LEBR") ?? defFile.Groups.Create("LEBR");

            var added = new List<string>();
            using var t = new Transaction(doc, "LEBR 綁定共用參數");
            t.Start();

            foreach (var spec in Specs)
            {
                var def = group.Definitions.get_Item(spec.Name) as ExternalDefinition
                          ?? (ExternalDefinition)group.Definitions.Create(
                              new ExternalDefinitionCreationOptions(spec.Name, spec.Spec) { GUID = spec.Guid });

                var cats = app.Create.NewCategorySet();
                foreach (var bic in spec.Categories)
                {
                    var c = Category.GetCategory(doc, bic);
                    if (c is not null && c.AllowsBoundParameters) cats.Insert(c);
                }

                Binding binding = spec.IsType ? app.Create.NewTypeBinding(cats) : app.Create.NewInstanceBinding(cats);

                var ok = doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data)
                         || doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data);
                if (ok) added.Add(spec.Name);
            }

            t.Commit();
            TaskDialog.Show("LEBR 綁定參數", $"已綁定 {added.Count} 個共用參數：\n{string.Join("\n", added)}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("LEBR 綁定參數失敗", ex.ToString());
            return Result.Failed;
        }
        finally
        {
            // 還原使用者原本的共用參數檔設定
            try { app.SharedParametersFilename = original; } catch { /* 原設定可能為空 */ }
        }
    }
}
