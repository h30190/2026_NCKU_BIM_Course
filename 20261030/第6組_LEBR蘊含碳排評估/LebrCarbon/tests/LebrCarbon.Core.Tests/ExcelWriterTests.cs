using System.IO.Compression;
using System.Xml.Linq;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Reports;

namespace LebrCarbon.Core.Tests;

/// <summary>
/// Excel 計算書結構檢查：每個 XML 都要能解析、公式中的 {key} 都要換成儲存格位址。
/// 公式數值的正確性已另以 LibreOffice 重算同一份規格驗證（測試模型與附錄三實例）。
/// </summary>
public class ExcelWriterTests
{
    private static LebrResult Example()
    {
        var p = new BuildingInputs
        {
            AFu = 336, AFb = 0, S = 3, Sb = 0, BH = 3.5, I = 1, SaD = 0.3, Fu = 3, Rs = 1, L = 200, D0 = 300,
            Structure = StructureType.RC, Sp = 1.106, F = 1.0, Wear = WearLevel.Low,
        };
        var design = new List<NonStructuralItem>
        {
            new() { Category = NsCategory.CurtainWall, Name = "RC 外牆 & 測試 <跳脫>", Area = 436.08, NewEf = 73.67, BlccId = "CW-0" },
            new() { Category = NsCategory.InteriorWall, Name = "輕隔間", Area = 82.43, NewEf = 22.33, BlccId = "IW-3" },
        };
        var baseline = new List<NonStructuralItem>
        {
            new() { Category = NsCategory.CurtainWall, Name = "RC 外牆", Area = 436.08, NewEf = 73.67, BlccId = "CW-0" },
            new() { Category = NsCategory.InteriorWall, Name = "磚牆", Area = 82.43, NewEf = 56.28, BlccId = "IW-1" },
        };
        return LebrCalculator.Evaluate(p, design, baseline);
    }

    [Fact]
    public void 計算書為有效的xlsx且公式都已解析()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lebr-test-{Guid.NewGuid():N}.xlsx");
        try
        {
            LebrExcelWriter.Write(path, Example(), new ShapeValues(1.172, 1.75, 0), "測試模型");

            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("[Content_Types].xml", names);
            Assert.Contains("xl/workbook.xml", names);
            Assert.Contains("xl/styles.xml", names);
            Assert.Contains("xl/worksheets/sheet1.xml", names);
            Assert.Contains("xl/worksheets/sheet2.xml", names);

            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                XDocument.Load(s); // 每個 XML 都要能解析
            }

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var formulas = XDocument.Load(sheet).Descendants(ns + "f").Select(f => f.Value).ToList();
            Assert.True(formulas.Count > 60, $"公式數量 {formulas.Count} 偏少");
            Assert.DoesNotContain(formulas, f => f.Contains('{') || f.Contains('}'));
            Assert.Contains(formulas, f => f.StartsWith("MAX(165,"));
            Assert.Contains(formulas, f => f.Contains("SUMIFS('非主結構明細'!"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
