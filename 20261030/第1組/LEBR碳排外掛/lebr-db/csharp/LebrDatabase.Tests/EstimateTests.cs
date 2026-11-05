using System;
using System.Collections.Generic;
using System.Linq;
using LebrCarbon;

namespace LebrDatabaseTests
{
    /// <summary>結構碳排估算測試：附錄一讀取、CFsn 手算、地上與地下分類、迴圈與錯誤處理。</summary>
    internal static partial class Program
    {
        static partial void TestEstimate(LebrDatabase db, string samplePath, string csvPath)
        {
            Section("估算：結構碳排估算值覆寫引擎");
            var baseline = new LebrCalculator(db).Calculate(QuantityInput.Load(samplePath));
            var overridden = QuantityInput.Load(samplePath);
            overridden.Project.StructureCFs = baseline.Design.CFs;
            var r7 = new LebrCalculator(db).Calculate(overridden);
            Equal("覆寫後完整", true, r7.Complete);
            Near("覆寫後設計案 CFs 等於估算值", baseline.Design.CFs, r7.Design.CFs);
            Near("覆寫後 CFR 與原計算相同（不重複套用 Lccr、Rn）", baseline.CFR.Value, r7.CFR.Value);
            Equal("覆寫後等級相同", baseline.Grade, r7.Grade);
            Equal("覆寫有說明訊息", true, r7.Issues.Any(i => i.Message.Contains("LEBR_CFs")));
            var j7 = Newtonsoft.Json.Linq.JObject.Parse(r7.ToJson());
            Equal("結果 JSON 標示主結構採估算值", "bim_quantity", (string)j7["structure"]["method"]);
            Equal("採估算值時結果 JSON 不輸出設計案 C", Newtonsoft.Json.Linq.JTokenType.Null, j7["structure"]["standard"]["C"].Type);
            Near("採估算值時結果 JSON 的 CFs 為估算值", baseline.Design.CFs, (double)j7["structure"]["design_CFs"]);
            Near("採估算值時基準案仍有 Sp", 1.8, (double)j7["structure"]["standard"]["baseline_Sp"]);

            var noSpF = QuantityInput.Load(samplePath);
            noSpF.Building.Sp = null;
            noSpF.Building.F = null;
            noSpF.Project.StructureCFs = baseline.Design.CFs;
            var r8 = new LebrCalculator(db).Calculate(noSpF);
            Equal("估算值存在時不需要 Sp、F", true, r8.Complete);
            Near("估算值存在時不需要 Sp、F，CFs 仍為估算值", baseline.Design.CFs, r8.Design.CFs);

            var zero = QuantityInput.Load(samplePath);
            zero.Project.StructureCFs = 0;
            var r9 = new LebrCalculator(db).Calculate(zero);
            Near("估算值為 0 時視為未估算，CFs 依 Sp、F 計算", baseline.Design.CFs, r9.Design.CFs);

            Section("估算：數量列轉換（ToItems）");
            var rows = new List<EstimateQuantityRow>
            {
                new EstimateQuantityRow { Source = "StructuralColumns", Level = "Level 1", ElevationM = 0, Quantity = 10, Unit = "m3" },
                new EstimateQuantityRow { Source = "Rebar", Level = "Level 1", ElevationM = 0, Quantity = 1000, Unit = "kg", IsRebar = true },
            };
            var map = new Dictionary<string, string> { { "StructuralColumns", "PLCC-040" }, { "Rebar", "PLCC-003" } };
            var items = EstimateQuantities.ToItems(rows, map);
            Equal("轉換後筆數", 2, items.Count);
            Equal("鋼筋列標記為鋼筋", true, items[1].IsRebar);
            Equal("材料編號對應", "PLCC-040", items[0].MaterialId);
            var missingMap = new Dictionary<string, string> { { "StructuralColumns", "PLCC-040" } };
            Throws("缺少鋼筋材料選擇報錯", () => EstimateQuantities.ToItems(rows, missingMap));

            var table = PLccTable.Load(csvPath);
            var viaEstimator = new StructureEstimator(table).Calculate(items);
            Near("轉換後估算 CFs = 10×243.78 + 1000×1.15", 3587.8, viaEstimator.CFs);

            Section("估算：估算紀錄（LEBR_EstimateJson）");
            var json = EstimateRecord.ToJson(viaEstimator, map, "測試模型.rvt", new DateTime(2026, 10, 28, 12, 0, 0));
            double above, below, cfs;
            Equal("紀錄可讀回", true, EstimateRecord.TryReadSummary(json, out above, out below, out cfs));
            Near("讀回地上", 3587.8, above);
            Near("讀回合計", 3587.8, cfs);
            Equal("格式不符時不可讀", false, EstimateRecord.TryReadSummary("not json", out above, out below, out cfs));

            Section("估算：結果 JSON 的 structure.bim（網頁三份文件）");
            Equal("舊版紀錄（沒有 rows）不產生 bim", true, StructureBim.FromEstimateJson(json) == null);
            var fullJson = EstimateRecord.ToJson(viaEstimator, map, "測試模型.rvt", new DateTime(2026, 10, 28, 12, 0, 0), items, table);
            Equal("新版紀錄可讀回", true, EstimateRecord.TryReadSummary(fullJson, out above, out below, out cfs));
            var bim = StructureBim.FromEstimateJson(fullJson);
            Equal("產生 bim", true, bim != null);
            var q = (Newtonsoft.Json.Linq.JArray)bim["quantities"];
            Equal("數量列數", 2, q.Count);
            Equal("柱 → column", "column", (string)q[0]["category"]);
            Equal("鋼筋 → rebar", "rebar", (string)q[1]["category"]);
            Equal("標高 0 為地上", "above", (string)q[0]["zone"]);
            var f = ((Newtonsoft.Json.Linq.JArray)bim["factors"]).ToDictionary(x => (string)x["id"], x => (double)x["total"]);
            Equal("係數只含用到的材料", 2, f.Count);
            Near("數量 × 係數 = 估算 CFs", viaEstimator.CFs, q.Sum(x => (double)x["quantity"] * f[(string)x["material_id"]]));
            Equal("模型說明有假設", true, ((Newtonsoft.Json.Linq.JArray)bim["model_description"]["assumptions"]).Count >= 3);

            var withRecord = QuantityInput.Load(samplePath);
            withRecord.Project.StructureCFs = viaEstimator.CFs;
            withRecord.Project.StructureEstimateJson = fullJson;
            var jr = Newtonsoft.Json.Linq.JObject.Parse(new LebrCalculator(db).Calculate(withRecord).ToJson());
            Equal("採估算值：structure.method = bim_quantity", "bim_quantity", (string)jr["structure"]["method"]);
            Equal("採估算值：structure.bim 有數量", 2, ((Newtonsoft.Json.Linq.JArray)jr["structure"]["bim"]["quantities"]).Count);
            Equal("inputs 不重複輸出估算紀錄", true, jr["inputs"]["project"]["structure_estimate_json"] == null);
            withRecord.Project.StructureEstimateJson = json;
            jr = Newtonsoft.Json.Linq.JObject.Parse(new LebrCalculator(db).Calculate(withRecord).ToJson());
            Equal("舊版估算紀錄：structure.bim 為 null", Newtonsoft.Json.Linq.JTokenType.Null, jr["structure"]["bim"].Type);
            var notEstimated = QuantityInput.Load(samplePath);
            notEstimated.Project.StructureEstimateJson = fullJson;
            jr = Newtonsoft.Json.Linq.JObject.Parse(new LebrCalculator(db).Calculate(notEstimated).ToJson());
            Equal("沒勾使用估算：留著的估算紀錄不輸出 bim", Newtonsoft.Json.Linq.JTokenType.Null, jr["structure"]["bim"].Type);

            Section("開始計算時依目前模型重算估算");
            var sel = EstimateRefresh.ReadSelections(fullJson);
            Equal("從估算紀錄讀回材料選擇", "PLCC-040", sel["StructuralColumns"]);
            Equal("沒有紀錄時讀不到選擇", true, EstimateRefresh.ReadSelections(null) == null);
            var same = EstimateRefresh.Recompute(rows, sel, table, "測試模型.rvt", new DateTime(2026, 11, 1), viaEstimator.CFs);
            Equal("模型沒變：標示沒變動", false, same.Changed);
            Near("模型沒變：CFs 相同", viaEstimator.CFs, same.CFs);
            var moreRows = new List<EstimateQuantityRow>(rows) { new EstimateQuantityRow { Source = "StructuralColumns", Level = "Level 2", ElevationM = 4, Quantity = 5, Unit = "m3" } };
            var changed = EstimateRefresh.Recompute(moreRows, sel, table, "測試模型.rvt", new DateTime(2026, 11, 1), viaEstimator.CFs);
            Equal("模型多了柱：標示有變動", true, changed.Changed);
            Near("模型多了柱：CFs 多 5 m³ × 243.78", viaEstimator.CFs + 5 * 243.78, changed.CFs);
            Equal("重算後的紀錄可轉 structure.bim", true, StructureBim.FromEstimateJson(changed.EstimateJson) != null);
            Equal("說明文字列出前後數字", true, EstimateRefresh.Describe(changed).Contains("→"));
            var newRebarOnly = new Dictionary<string, string> { { "StructuralColumns", "PLCC-040" } };
            var withDefault = EstimateRefresh.Recompute(rows, newRebarOnly, table, "x", DateTime.Now, viaEstimator.CFs);
            Equal("上次沒選的品類用預設材料", true, withDefault.DefaultedSources.Count > 0);
            Equal("說明文字提到預設材料", true, EstimateRefresh.Describe(withDefault).Contains("預設材料"));
            var first = EstimateRefresh.Recompute(rows, null, table, "x", DateTime.Now, null);
            Equal("第一次估算（沒有紀錄）：全部用預設材料也算得出來", true, first.CFs > 0 && first.Changed);
            Equal("第一次估算：說明寫「自動估算」", true, EstimateRefresh.Describe(first).Contains("自動估算"));
            var firstSel = EstimateRefresh.ReadSelections(first.EstimateJson);
            Equal("估算紀錄只列模型有的品類", rows.Select(r => r.Source).Distinct().Count(), firstSel.Count);
            Equal("第一次估算的紀錄可轉 structure.bim", true, StructureBim.FromEstimateJson(first.EstimateJson) != null);

            Section("在圖台開啟：網址 #d=（gzip ＋ base64url）");
            var resultJson = new LebrCalculator(db).Calculate(QuantityInput.Load(samplePath)).ToJson();
            var link = DashboardLink.Build("https://example.netlify.app/dashboard#old", resultJson);
            Equal("網址以圖台網址開頭、舊的 # 會被換掉", true, link.StartsWith("https://example.netlify.app/dashboard#d="));
            var encoded = link.Substring(link.IndexOf("#d=") + 3);
            Equal("只用 base64url 字元", true, encoded.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
            Equal("解碼後等於原本的結果 JSON", resultJson, DashboardLink.Decode(encoded));
            Equal("附錄三範例的網址在長度上限內", true, link.Length < DashboardLink.MaxUrlLength);
            Equal("沒有設定檔時用預設圖台", DashboardLink.DefaultBaseUrl, DashboardLink.ResolveBaseUrl(System.IO.Path.GetTempPath() + "not-exist-folder"));
            var cfgDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lebr-dashlink-test");
            System.IO.Directory.CreateDirectory(cfgDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, DashboardLink.SettingFile), "# 註解\nhttps://my-site.netlify.app/dashboard\n");
            Equal("讀設定檔的網址（略過註解）", "https://my-site.netlify.app/dashboard", DashboardLink.ResolveBaseUrl(cfgDir));
            System.IO.File.WriteAllText(System.IO.Path.Combine(cfgDir, DashboardLink.SettingFile), "not a url");
            Equal("設定檔不是網址時用預設圖台", DashboardLink.DefaultBaseUrl, DashboardLink.ResolveBaseUrl(cfgDir));

            TestEstimateTables(csvPath);
        }

        private static void TestEstimateTables(string csvPath)
        {
            Section("估算：附錄一讀取");
            var table = PLccTable.Load(csvPath);
            Equal("附錄一筆數", 120, table.Materials.Count);
            Near("PLCC-040 預拌混凝土（2000psi）總碳排", 243.78, table.Get("PLCC-040").TotalA1A4.Value);
            Equal("PLCC-040 單位", "m3", table.Get("PLCC-040").Unit);
            Near("PLCC-003 鋼筋及鐵件總碳排", 1.15, table.Get("PLCC-003").TotalA1A4.Value);
            Equal("PLCC-003 單位", "kg", table.Get("PLCC-003").Unit);
            Equal("PLCC-013 有手冊說明", true, table.Get("PLCC-013").ManualIssue != null);
            Throws("不存在的材料編號報錯", () => table.Get("PLCC-999"));

            var est = new StructureEstimator(table);

            Section("估算：單層手算");
            var r1 = est.Calculate(new[]
            {
                Item("StructuralColumns", "Level 1", 0, 10, "m3", "PLCC-040", false),
                Item("Rebar", "Level 1", 0, 1000, "kg", "PLCC-003", true),
            });
            Near("地上 CFs = 10×243.78 + 1000×1.15", 3587.8, r1.Above);
            Near("地下 CFs = 0", 0, r1.Below);
            Near("合計 CFs", 3587.8, r1.CFs);
            Near("Level 1 CFc", 2437.8, r1.Levels[0].CFc);
            Near("Level 1 CFr", 1150, r1.Levels[0].CFr);
            Equal("沒有缺鋼筋註明", 0, r1.Notes.Count);

            Section("估算：地下層與無鋼筋");
            var r2 = est.Calculate(new[]
            {
                Item("StructuralColumns", "B1", -4, 5, "m3", "PLCC-040", false),
            });
            Near("地下 CFs = 5×243.78", 1218.9, r2.Below);
            Near("地上 CFs = 0", 0, r2.Above);
            Equal("無鋼筋樓層有註明", true, r2.Notes.Any(n => n.Contains("B1") && n.Contains("Bn = 0")));

            Section("估算：標高邊界（0 為地上，-0.001 為地下）");
            var r3 = est.Calculate(new[]
            {
                Item("Floors", "Level 0", 0, 1, "m3", "PLCC-040", false),
                Item("Floors", "B0", -0.001, 1, "m3", "PLCC-040", false),
            });
            Near("標高 0 歸地上", 243.78, r3.Above);
            Near("標高 -0.001 歸地下", 243.78, r3.Below);

            Section("估算：迴圈（零次、一次、多次）");
            var r4 = est.Calculate(new List<EstimateItem>());
            Near("零次：CFs = 0", 0, r4.CFs);
            Equal("零次：有註明", true, r4.Notes.Any(n => n.Contains("CFs = 0")));

            var loop = new List<EstimateItem>();
            for (var i = 0; i < 76; i++)
                loop.Add(Item("StructuralColumns", "Level 1", 0, 1.8, "m3", "PLCC-040", false));
            var r5 = est.Calculate(loop);
            Near("76 根柱（每根 1.8 m3）合計", 76 * 1.8 * 243.78, r5.CFs);
            Equal("76 根柱仍為一層", 1, r5.Levels.Count);

            Section("估算：錯誤輸入直接報錯");
            Throws("混凝土數量單位不是 m3 報錯", () => est.Calculate(new[] { Item("StructuralColumns", "Level 1", 0, 10, "kg", "PLCC-040", false) }));
            Throws("混凝土係數用 kg 報錯", () => est.Calculate(new[] { Item("StructuralColumns", "Level 1", 0, 10, "m3", "PLCC-003", false) }));
            Throws("鋼筋係數用 m3 報錯", () => est.Calculate(new[] { Item("Rebar", "Level 1", 0, 10, "kg", "PLCC-040", true) }));
            Throws("負數量報錯", () => est.Calculate(new[] { Item("StructuralColumns", "Level 1", 0, -1, "m3", "PLCC-040", false) }));
            Throws("同名樓層標高不一致報錯", () => est.Calculate(new[]
            {
                Item("StructuralColumns", "Level 1", 0, 1, "m3", "PLCC-040", false),
                Item("Floors", "Level 1", 4, 1, "m3", "PLCC-040", false),
            }));
            Throws("不存在的材料編號報錯", () => est.Calculate(new[] { Item("Floors", "Level 1", 0, 1, "m3", "PLCC-999", false) }));
        }

        private static EstimateItem Item(string source, string level, double elevation, double quantity, string unit, string materialId, bool isRebar)
        {
            return new EstimateItem
            {
                Source = source,
                Level = level,
                ElevationM = elevation,
                Quantity = quantity,
                Unit = unit,
                MaterialId = materialId,
                IsRebar = isRebar,
            };
        }
    }
}
