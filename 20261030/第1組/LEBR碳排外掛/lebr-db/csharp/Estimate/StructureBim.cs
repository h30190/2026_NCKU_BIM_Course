using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace LebrCarbon
{
    /// <summary>
    /// 把估算紀錄（LEBR_EstimateJson）轉成結果 JSON 的 structure.bim，給網頁的
    /// 「估算模型說明」「鋼筋、混凝土數量估算報告」「主結構碳排計算報告」三頁使用（手冊 p.24 的三份文件）。
    /// 欄位與網頁 lebr-structure-result 的 model_description、quantities、factors 相同。不依賴 Revit API。
    /// </summary>
    public static class StructureBim
    {
        /// <summary>Revit 來源品類 → 網頁構件類別。</summary>
        public static readonly IDictionary<string, string> Category = new Dictionary<string, string>
        {
            { "StructuralColumns", "column" },
            { "StructuralFraming", "beam" },
            { "Floors", "slab" },
            { "Walls", "wall" },
            { "Rebar", "rebar" },
        };

        /// <summary>
        /// 估算紀錄沒有 rows 或 materials（舊版估算視窗存的）時回傳 null，
        /// 網頁會顯示「尚未匯出明細」；請使用者在估算視窗重新「計算並儲存」。
        /// </summary>
        public static JObject FromEstimateJson(string estimateJson, string software = null)
        {
            if (string.IsNullOrWhiteSpace(estimateJson)) return null;
            JObject rec;
            try { rec = JObject.Parse(estimateJson); }
            catch (Newtonsoft.Json.JsonException) { return null; }

            var rows = rec["rows"] as JArray;
            var mats = rec["materials"] as JArray;
            if (rows == null || rows.Count == 0 || mats == null || mats.Count == 0) return null;

            var quantities = new JArray();
            foreach (var r in rows.OfType<JObject>())
            {
                var source = (string)r["source"] ?? "";
                string cat;
                if (!Category.TryGetValue(source, out cat)) cat = source;
                quantities.Add(new JObject
                {
                    ["category"] = cat,
                    ["source"] = source,
                    ["zone"] = (bool?)r["below_ground"] == true ? "below" : "above",
                    ["level"] = (string)r["level"],
                    ["elevation_m"] = r["elevation_m"],
                    ["material_id"] = (string)r["material_id"],
                    ["unit"] = (string)r["unit"],
                    ["quantity"] = r["quantity"],
                });
            }

            var factors = new JArray();
            foreach (var m in mats.OfType<JObject>())
            {
                factors.Add(new JObject
                {
                    ["id"] = m["id"], ["name"] = m["name"], ["unit"] = m["unit"],
                    ["a1"] = null, ["a2"] = null, ["a3"] = null, ["a4"] = null,
                    ["total"] = m["total"], ["source_page"] = null, ["manual_issue"] = m["manual_issue"],
                });
            }

            var assumptions = new JArray
            {
                "數量依 Revit 品類擷取：結構柱、結構構架（梁）、樓板、結構牆；鋼筋含 Rebar 與樓板的面／路徑鋼筋。",
                "材料由估算視窗選擇，每個品類對應一種附錄一材料；係數取 P-LCC 總碳排量（A1～A4）。",
                "樓層標高小於 0 為地下層（CFs′，只揭露），其餘為地上層。",
            };
            var src = rec["rebar_sources"] as JObject;
            if (src != null && src["mass_method"] != null) assumptions.Add("鋼筋重量：" + (string)src["mass_method"] + "。");
            foreach (var n in (rec["notes"] as JArray ?? new JArray())) assumptions.Add((string)n);

            var result = rec["result"] as JObject ?? new JObject();
            return new JObject
            {
                ["format"] = "lebr-structure-bim",
                ["version"] = 1,
                ["calculated_at"] = rec["calculated_at"],
                ["model_description"] = new JObject
                {
                    ["software"] = software ?? "Autodesk Revit（LEBR 外掛估算視窗）",
                    ["scope"] = "結構柱、結構構架（梁）、樓板、結構牆與鋼筋；地上、地下分開計算",
                    ["assumptions"] = assumptions,
                    ["rebar"] = new JObject { ["mode"] = "modeled" },
                },
                ["selections"] = rec["selections"],
                ["quantities"] = quantities,
                ["factors"] = factors,
                ["levels"] = rec["levels"],
                ["totals"] = new JObject
                {
                    ["above"] = result["above"], ["below"] = result["below"], ["cfs"] = result["cfs"],
                },
            };
        }
    }
}
