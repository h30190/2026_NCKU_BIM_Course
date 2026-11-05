using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LebrCarbon.Revit
{
    /// <summary>
    /// 代號 JSON：一個模型填好的 LEBR_Code、專案資訊與不計入元件。
    /// 從 A 模型匯出，在 B 模型套用，不用手動一個一個填（例如 Revit 2027 存的模型，Revit 2024 打不開時）。
    /// </summary>
    public class CodeFile
    {
        [JsonProperty("format")] public string Format { get; set; } = "lebr-codes";
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("source_model")] public string SourceModel { get; set; }
        [JsonProperty("exported_at")] public string ExportedAt { get; set; }
        [JsonProperty("project")] public Dictionary<string, JToken> Project { get; set; } = new Dictionary<string, JToken>();
        [JsonProperty("types")] public List<TypeCode> Types { get; set; } = new List<TypeCode>();
        [JsonProperty("excluded")] public List<ExcludedElement> Excluded { get; set; } = new List<ExcludedElement>();
    }

    /// <summary>一個類型的代號。依「品類 + 族群 + 類型名稱」對應；族群名稱不同時，改用品類 + 類型名稱。</summary>
    public class TypeCode
    {
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("family")] public string Family { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("LEBR_Code", NullValueHandling = NullValueHandling.Ignore)] public string Code { get; set; }
        [JsonProperty("LEBR_Code_Body", NullValueHandling = NullValueHandling.Ignore)] public string CodeBody { get; set; }
    }

    /// <summary>勾選 LEBR_Exclude 的元件。先依元件 ID 找；ID 不同時，改找同品類、同類型、同樓層且唯一的元件。</summary>
    public class ExcludedElement
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("level")] public string Level { get; set; }
    }

    public class ApplyReport
    {
        public List<string> Added = new List<string>();
        public int TypesSet, ProjectSet, ExcludedSet;
        public List<string> Missing = new List<string>();
    }

    public static class CodeMapping
    {
        // ------------------------------------------------------------ 匯出

        public static CodeFile Export(Document doc)
        {
            var file = new CodeFile { SourceModel = doc.Title, ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };

            var info = doc.ProjectInformation;
            foreach (var def in LebrParameters.All.Where(d => d.Target == ParamTarget.Project))
            {
                var p = Find(info, def);
                if (p == null || !p.HasValue) continue;
                if (p.StorageType == StorageType.String)
                {
                    var s = p.AsString();
                    if (!string.IsNullOrWhiteSpace(s)) file.Project[def.Name] = s.Trim();
                }
                else if (p.StorageType == StorageType.Double && Math.Abs(p.AsDouble()) > 1e-12)
                {
                    file.Project[def.Name] = p.AsDouble();
                }
            }

            var code = Def(LebrParameters.Code);
            var body = Def(LebrParameters.CodeBody);
            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType()
                         .WherePasses(new ElementMulticategoryFilter(LebrParameters.ComponentCategories))
                         .Cast<ElementType>().OrderBy(t => CategoryKey(t)).ThenBy(t => t.FamilyName).ThenBy(t => t.Name))
            {
                var c = Text(t, code);
                var b = Text(t, body);
                if (c == null && b == null) continue;
                file.Types.Add(new TypeCode { Category = CategoryKey(t), Family = t.FamilyName, Type = t.Name, Code = c, CodeBody = b });
            }

            var exclude = Def(LebrParameters.Exclude);
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                         .WherePasses(new ElementMulticategoryFilter(LebrParameters.ComponentCategories)))
            {
                var p = Find(e, exclude);
                if (p == null || !p.HasValue || p.AsInteger() != 1) continue;
                file.Excluded.Add(new ExcludedElement
                {
                    Id = e.Id.Value, Category = CategoryKey(e), Type = doc.GetElement(e.GetTypeId())?.Name, Level = LevelName(doc, e),
                });
            }
            return file;
        }

        // ------------------------------------------------------------ 套用

        /// <summary>先補建缺少的 LEBR 參數，再在一個交易裡寫入代號。JSON 沒寫到的類型與參數不會被改。</summary>
        public static ApplyReport Apply(Document doc, CodeFile file)
        {
            var report = new ApplyReport { Added = SetupParametersCommand.Ensure(doc) };

            using (var t = new Transaction(doc, "套用 LEBR 代號"))
            {
                t.Start();
                ApplyProject(doc, file, report);
                ApplyTypes(doc, file, report);
                ApplyExcluded(doc, file, report);
                t.Commit();
            }
            return report;
        }

        private static void ApplyProject(Document doc, CodeFile file, ApplyReport report)
        {
            var info = doc.ProjectInformation;
            foreach (var kv in file.Project ?? new Dictionary<string, JToken>())
            {
                var def = LebrParameters.All.FirstOrDefault(d => d.Name == kv.Key && d.Target == ParamTarget.Project);
                var p = def == null ? null : Find(info, def);
                if (p == null || p.IsReadOnly || kv.Value == null || kv.Value.Type == JTokenType.Null)
                {
                    report.Missing.Add("專案資訊 " + kv.Key);
                    continue;
                }
                bool ok;
                if (p.StorageType == StorageType.String) ok = p.Set(kv.Value.ToString());
                else if (p.StorageType == StorageType.Double) ok = p.Set(kv.Value.Value<double>());
                else ok = false;
                if (ok) report.ProjectSet++;
                else report.Missing.Add("專案資訊 " + kv.Key);
            }
        }

        private static void ApplyTypes(Document doc, CodeFile file, ApplyReport report)
        {
            var types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .WherePasses(new ElementMulticategoryFilter(LebrParameters.ComponentCategories))
                .Cast<ElementType>().ToList();
            var code = Def(LebrParameters.Code);
            var body = Def(LebrParameters.CodeBody);

            foreach (var tc in file.Types ?? new List<TypeCode>())
            {
                var sameName = types.Where(t => CategoryKey(t) == tc.Category && t.Name == tc.Type).ToList();
                var exact = sameName.Where(t => t.FamilyName == tc.Family).ToList();
                var match = exact.Count == 1 ? exact[0] : sameName.Count == 1 ? sameName[0] : null;
                if (match == null)
                {
                    report.Missing.Add("類型 " + CategoryLabel(doc, tc.Category) + "：" + tc.Family + " " + tc.Type
                                       + (sameName.Count > 1 ? "（同名類型不只一個）" : "（模型裡沒有）"));
                    continue;
                }
                var ok = true;
                if (tc.Code != null) ok &= SetText(match, code, tc.Code);
                if (tc.CodeBody != null) ok &= SetText(match, body, tc.CodeBody);
                if (ok) report.TypesSet++;
                else report.Missing.Add("類型 " + tc.Type + "（參數無法寫入）");
            }
        }

        private static void ApplyExcluded(Document doc, CodeFile file, ApplyReport report)
        {
            var exclude = Def(LebrParameters.Exclude);
            foreach (var x in file.Excluded ?? new List<ExcludedElement>())
            {
                var e = doc.GetElement(new ElementId(x.Id));
                if (e == null || CategoryKey(e) != x.Category || doc.GetElement(e.GetTypeId())?.Name != x.Type)
                {
                    BuiltInCategory bic;
                    var candidates = Enum.TryParse(x.Category, out bic)
                        ? new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                            .Where(c => doc.GetElement(c.GetTypeId())?.Name == x.Type && LevelName(doc, c) == x.Level).ToList()
                        : new List<Element>();
                    e = candidates.Count == 1 ? candidates[0] : null;
                }
                var p = e == null ? null : Find(e, exclude);
                if (p != null && !p.IsReadOnly && p.Set(1)) report.ExcludedSet++;
                else report.Missing.Add("不計入元件 " + x.Id + "（" + x.Type + "，" + x.Level + "）");
            }
        }

        // ------------------------------------------------------------ 工具

        private static LebrParamDef Def(string name) => LebrParameters.All.First(d => d.Name == name);

        /// <summary>優先用共用參數 GUID 找，找不到再用名稱（例如別人手動建的同名專案參數）。</summary>
        private static Parameter Find(Element e, LebrParamDef def) => e.get_Parameter(def.Guid) ?? e.LookupParameter(def.Name);

        private static string Text(Element e, LebrParamDef def)
        {
            var p = Find(e, def);
            if (p == null || !p.HasValue || p.StorageType != StorageType.String) return null;
            var v = p.AsString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static bool SetText(Element e, LebrParamDef def, string value)
        {
            var p = Find(e, def);
            return p != null && !p.IsReadOnly && p.StorageType == StorageType.String && p.Set(value.Trim());
        }

        /// <summary>品類用 BuiltInCategory 名稱（例如 OST_Walls），中英文版 Revit 都一樣。</summary>
        private static string CategoryKey(Element e) => e.Category == null ? null : e.Category.BuiltInCategory.ToString();

        private static string CategoryLabel(Document doc, string key)
        {
            BuiltInCategory bic;
            return Enum.TryParse(key, out bic) ? Category.GetCategory(doc, bic)?.Name ?? key : key;
        }

        private static string LevelName(Document doc, Element e)
        {
            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
                id = e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId;
            return id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
        }
    }
}
