using System;
using System.IO;
using System.Linq;
using System.Text;
using LebrCarbon;

namespace LebrDatabaseTests
{
    /// <summary>
    /// 讀取 dist/lebr_2025.json，檢查筆數、查表結果與邊界值。
    /// 用法：dotnet run -f net8.0 -- [lebr_2025.json 路徑]
    /// </summary>
    internal static partial class Program
    {
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var path = args.Length > 0 ? args[0] : FindDefaultPath();
            Console.WriteLine("讀取 " + path);
            var db = LebrDatabase.Load(path);

            Section("基本資料");
            Equal("schema_version", "1.0", db.Meta.SchemaVersion);
            Equal("P-LCC 筆數", 120, db.PrimaryMaterials.Count);
            Equal("B-LCC 筆數", 187, db.Components.Count);
            Console.WriteLine("  核對狀態：" + db.Meta.ReviewStatus + (db.IsReviewed ? "" : "（尚未人工核對）"));

            Section("B-LCC 構件查詢");
            Near("W-G01-AL 新建碳排", 37.14, db.GetComponent("W-G01-AL").FNew);
            Near("w-g01-al 不分大小寫", 37.14, db.GetComponent("w-g01-al").FNew);
            Equal("IW-L-03 的基準", "IW-L-01", db.GetBaseline(db.GetComponent("IW-L-03")).Code);
            Equal("F-H-W03 的基準", "F-H-W01", db.GetComponent("F-H-W03").BaselineCode);
            BuildingComponent none;
            Equal("查不存在的編號", false, db.TryGetComponent("XX-99", out none));
            Equal("高耗損內隔間筆數", 6, db.GetComponents("IW", "H").Count());
            Near("輕鋼構更新碳排 F-H-G01 ×0.8", (13.58 * 1 + 19.65 * 3) * 0.8, db.GetComponent("F-H-G01").FRenewFor(0.8));
            Near("RC 構造更新碳排 = 手冊值", 72.53, db.GetComponent("F-H-G01").FRenewFor(1.0));

            Section("每個構件的基準都存在，且減碳量可由資料重算");
            var mismatched = 0;
            foreach (var c in db.Components)
            {
                var b = db.GetBaseline(c);
                var d = (c.FNew + c.FRenew) - (b.FNew + b.FRenew);
                if (Math.Abs(d - c.DeltaF) > 0.02)
                {
                    mismatched++;
                    Equal(c.Code + " 減碳量不符時應已標記 CHECK", "CHECK", c.Check.Status);
                }
            }
            Console.WriteLine("  減碳量和重算值不符 " + mismatched + " 筆（都已在資料中標記為手冊不一致）");

            Section("P-LCC 初級資材");
            var concrete = db.FindMaterials("預拌混凝土（3000psi）").Single();
            Near("預拌混凝土 3000psi 總量", 327.75, concrete.Total);
            Equal("單位", "m3", concrete.Unit);
            Near("可樂瓦 A4（小數不可被截斷）", 0.00114, db.FindMaterials("可樂瓦").Single().A4.Value);

            Section("參數查表（含邊界值）");
            Near("f1 正方形 10×10（PAr 1.128）", 1.0, db.ShapeFactorF1(40, 100));
            Near("f1 細長 50×5（PAr 1.96）", 1.08, db.ShapeFactorF1(110, 250));
            Near("f2 b = 4（含）", 1.0, db.AspectFactorF2(4));
            Near("f2 b = 4.01", 1.05, db.AspectFactorF2(4.01));
            Near("f2 b = 7", 1.1, db.AspectFactorF2(7));
            Near("f3 Rc = 0.1（含）", 1.0, db.CantileverFactorF3(0.1));
            Near("f3 Rc = 0.15", 0.98, db.CantileverFactorF3(0.15));
            Throws("f3 出挑 5m 不適用", () => db.CantileverFactorF3(0.15, 5));
            Near("W RC", 1.0, db.StructureFactorW("RC"));
            Near("W SRC", 1.05, db.StructureFactorW("SRC"));
            Near("Wd RC 住宿類", 390, db.WasteDensityWd("RC", "residential"));
            Near("Wd 木構造住宅（以輕鋼構其他類代用）", 270, db.WasteDensityWd("wood", "residential"));
            Near("基準案 W 25 層", 1.0, db.BaselineW(25));
            Near("基準案 W 26 層", 0.9, db.BaselineW(26));
            Near("基準案 F 9 層", 1.15, db.BaselineShapeF(9));
            Near("基準案 Sp 10 層 AFa 600", 1.8, db.BaselineSp(10, 600));
            Near("基準案 Sp 20 層 AFa 500（含）", 1.4, db.BaselineSp(20, 500));
            Near("基準案 Sp 5 層 AFa 150", 1.6, db.BaselineSp(5, 150));
            Near("式4 常數 base", 224, db.Parameters.Constant("structure_C", "base"));
            Near("式4 C 下限", 165, db.Parameters.Constant("structure_C", "min_C"));

            Section("分級（表5-1）");
            Equal("CFR 25%", "1+", db.GradeFor(0.25));
            Equal("CFR 20%（含）", "1", db.GradeFor(0.20));
            Equal("CFR 10%", "3", db.GradeFor(0.10));
            Equal("CFR 3%（含）", "5", db.GradeFor(0.03));
            Equal("CFR -20%（含）", "7", db.GradeFor(-0.20));

            // 外掛包才有計算引擎（EngineTests.cs）；資料庫包沒有時這行不會被編譯
            TestEngine(db, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)), "samples", "appendix3_quantities.json"));

            TestEstimate(db, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)), "samples", "appendix3_quantities.json"),
                Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)), "data", "estimate", "附錄一_P-LCC_2022.csv"));

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "全部通過" : "失敗 " + _failed + " 項");
            return _failed == 0 ? 0 : 1;
        }

        static partial void TestEngine(LebrDatabase db, string samplePath);

        static partial void TestEstimate(LebrDatabase db, string samplePath, string csvPath);

        private static string FindDefaultPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "dist", "lebr_2025.json")))
                dir = dir.Parent;
            if (dir == null)
                throw new FileNotFoundException("找不到 dist/lebr_2025.json，請以參數指定路徑");
            return Path.Combine(dir.FullName, "dist", "lebr_2025.json");
        }

        internal static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("■ " + title);
        }

        internal static void Equal<T>(string label, T expected, T actual)
        {
            Report(label, Equals(expected, actual), expected, actual);
        }

        internal static void Near(string label, double expected, double actual)
        {
            Report(label, Math.Abs(expected - actual) < 1e-6, expected, actual);
        }

        private static void Throws(string label, Action action)
        {
            try
            {
                action();
                Report(label, false, "例外", "沒有例外");
            }
            catch (InvalidOperationException)
            {
                Report(label, true, "例外", "例外");
            }
        }

        internal static void Report(string label, bool ok, object expected, object actual)
        {
            if (!ok) _failed++;
            Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + label + (ok ? "" : "：預期 " + expected + "，實際 " + actual));
        }
    }
}
