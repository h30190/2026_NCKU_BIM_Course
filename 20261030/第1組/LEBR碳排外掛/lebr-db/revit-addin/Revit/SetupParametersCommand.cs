using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LebrCarbon.Revit
{
    /// <summary>「建立參數」：把 LEBR_* 共用參數綁到牆、窗、帷幕嵌板、樓板與專案資訊。已存在的不重複建立。</summary>
    [Transaction(TransactionMode.Manual)]
    public class SetupParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("LEBR", "請先開啟一個專案（不是族群檔）。");
                return Result.Cancelled;
            }

            List<string> added;
            try
            {
                added = Ensure(doc);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("LEBR", "建立參數失敗：" + ex.Message);
                return Result.Failed;
            }

            var dialog = new TaskDialog("LEBR 參數")
            {
                MainInstruction = added.Count > 0 ? "已建立 " + added.Count + " 個 LEBR 參數" : "LEBR 參數都已存在",
                MainContent =
                    "接下來請填：\n" +
                    "1. 牆、窗、帷幕嵌板、樓板的「類型性質」→ LEBR_Code（外牆本體另填 LEBR_Code_Body）。\n" +
                    "2. 管理 → 專案資訊 → LEBR_WearClass、LEBR_Structure、LEBR_UseType 與結構參數（I、SaD、Fu、L、D0、Rs）。\n" +
                    "3. 不計入的元件（樓梯間牆、分戶牆、停車場地坪等）勾選 LEBR_Exclude。\n\n" +
                    "代號請對照 LEBR 資料庫 README 的編號規則。",
            };
            dialog.Show();
            return Result.Succeeded;
        }

        /// <summary>建立缺少的 LEBR 參數，已存在的不重複建立。不顯示對話框；失敗時拋出例外。回傳本次新增的參數名稱。</summary>
        public static List<string> Ensure(Document doc)
        {
            var app = doc.Application;
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var file = Path.Combine(folder, "LEBR_SharedParams.txt");
            if (!File.Exists(file)) File.WriteAllText(file, string.Empty, new UTF8Encoding(false));

            var existing = new HashSet<string>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key != null) existing.Add(it.Key.Name);

            var original = app.SharedParametersFilename;
            var added = new List<string>();
            try
            {
                app.SharedParametersFilename = file;
                var defFile = app.OpenSharedParameterFile();
                var group = defFile.Groups.get_Item("LEBR") ?? defFile.Groups.Create("LEBR");

                using (var t = new Transaction(doc, "建立 LEBR 參數"))
                {
                    t.Start();
                    foreach (var p in LebrParameters.All)
                    {
                        if (existing.Contains(p.Name)) continue;
                        var def = group.Definitions.get_Item(p.Name) ?? group.Definitions.Create(
                            new ExternalDefinitionCreationOptions(p.Name, p.Spec) { GUID = p.Guid, Description = p.Description });

                        var cats = new CategorySet();
                        foreach (var bic in p.Categories)
                        {
                            var c = Category.GetCategory(doc, bic);
                            if (c != null && c.AllowsBoundParameters) cats.Insert(c);
                        }
                        Binding binding = p.Target == ParamTarget.TypeCode
                            ? (Binding)app.Create.NewTypeBinding(cats)
                            : app.Create.NewInstanceBinding(cats);
                        if (doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data))
                            added.Add(p.Name);
                    }
                    t.Commit();
                }
            }
            finally
            {
                app.SharedParametersFilename = original;
            }
            return added;
        }
    }
}
