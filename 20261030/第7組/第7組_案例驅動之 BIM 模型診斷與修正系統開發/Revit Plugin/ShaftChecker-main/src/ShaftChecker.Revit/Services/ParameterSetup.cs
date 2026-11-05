using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using ShaftChecker.Core.Config;
using ShaftChecker.Revit.Utils;
using ShaftChecker.Revit.Writers;

namespace ShaftChecker.Revit.Services
{
    /// <summary>
    /// 建立外掛專用的共用參數檔，並把參數綁定到 Room（結果欄位）與 Door（遮煙性能）。
    /// 「設定參數」按鈕與「建立測試模型」共用。執行完會還原使用者原本的共用參數檔設定。
    /// </summary>
    public static class ParameterSetup
    {
        private class ParamSpec
        {
            public string Name;
            public ForgeTypeId Spec;
            public BuiltInCategory Category;
        }

        /// <summary>自行開 Transaction；失敗時丟出例外。回傳每個參數的處理紀錄。</summary>
        public static string Ensure(Application app, Document doc, RuleConfig cfg)
        {
            var specs = new List<ParamSpec>
            {
                new ParamSpec { Name = ParameterWriter.PClearWidth, Spec = SpecTypeId.Length, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = ParameterWriter.PClearDepth, Spec = SpecTypeId.Length, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = ParameterWriter.PArea, Spec = SpecTypeId.Area, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = ParameterWriter.PResult, Spec = SpecTypeId.String.Text, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = ParameterWriter.PIssues, Spec = SpecTypeId.String.Text, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = ParameterWriter.PTime, Spec = SpecTypeId.String.Text, Category = BuiltInCategory.OST_Rooms },
                new ParamSpec { Name = cfg.SmokeSealParam, Spec = SpecTypeId.Boolean.YesNo, Category = BuiltInCategory.OST_Doors }
            };

            string original = app.SharedParametersFilename;
            string spFile = Path.Combine(AddinPaths.AddinFolder, "ShaftChecker_SharedParameters.txt");
            var log = new StringBuilder();

            try
            {
                if (!File.Exists(spFile)) File.WriteAllText(spFile, "", Encoding.Unicode);
                app.SharedParametersFilename = spFile;

                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null) throw new InvalidOperationException("無法開啟共用參數檔：" + spFile);

                DefinitionGroup group = file.Groups.get_Item("ShaftChecker") ?? file.Groups.Create("ShaftChecker");

                using (var t = new Transaction(doc, "管道間檢討：設定參數"))
                {
                    t.Start();
                    foreach (ParamSpec spec in specs)
                    {
                        Definition def = group.Definitions.get_Item(spec.Name) ??
                                         group.Definitions.Create(new ExternalDefinitionCreationOptions(spec.Name, spec.Spec));

                        CategorySet cats = app.Create.NewCategorySet();
                        cats.Insert(Category.GetCategory(doc, spec.Category));
                        InstanceBinding binding = app.Create.NewInstanceBinding(cats);

                        bool added = doc.ParameterBindings.Insert(def, binding);
                        if (!added) doc.ParameterBindings.ReInsert(def, binding);

                        log.AppendLine((added ? "新增  " : "已存在  ") + spec.Name +
                                       (spec.Category == BuiltInCategory.OST_Rooms ? "（房間）" : "（門）"));
                    }
                    t.Commit();
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(original))
                {
                    try { app.SharedParametersFilename = original; } catch (Exception) { }
                }
            }

            return log.ToString();
        }
    }
}
