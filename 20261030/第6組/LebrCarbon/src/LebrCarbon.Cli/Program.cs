using System.Text;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Reports;
using LebrCarbon.Reports.Pdf;

// lebr-snapshot：由 .lebr.json 重算 LEBR，輸出控制台快照 JSON，或直接上傳到控制台後端。
// 用途：教室沒有 Revit 2026 時，仍可用事先存好的評估輸入 demo 網頁控制台。

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
用法：lebr-snapshot <專案.lebr.json> [選項]

  --name <名稱>    快照名稱（預設「{模型名} 評估」）
  --key <key>      案子 key（預設為檔名去掉 .lebr.json，與外掛相同）
  --out <路徑>     把快照 JSON 寫到檔案
  --upload [網址]  上傳到控制台後端（預設 http://127.0.0.1:8080）
  --excel          一併附上 Excel 計算書
  --pdf <路徑>     另外輸出一頁式 PDF 摘要（需要 Windows「Microsoft Print to PDF」）

沒有 --out、--upload、--pdf 時，把快照 JSON 印到畫面。
""";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

string input = args[0];
string? name = null, key = null, outPath = null, uploadUrl = null, pdfPath = null;
var upload = false;
var withExcel = false;

for (var i = 1; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
    switch (args[i])
    {
        case "--name": name = Next(); break;
        case "--key": key = Next(); break;
        case "--out": outPath = Next(); break;
        case "--upload": upload = true; uploadUrl = Next(); break;
        case "--excel": withExcel = true; break;
        case "--pdf": pdfPath = Next(); break;
        default:
            Console.Error.WriteLine($"不認得的選項：{args[i]}\n\n{Usage}");
            return 1;
    }
}

var file = ProjectStore.TryLoad(input);
if (file is null)
{
    Console.Error.WriteLine($"找不到評估輸入檔：{input}");
    return 1;
}

var fileName = Path.GetFileName(input);
key ??= fileName.EndsWith(".lebr.json", StringComparison.OrdinalIgnoreCase)
    ? fileName[..^".lebr.json".Length]
    : Path.GetFileNameWithoutExtension(fileName);
var title = string.IsNullOrWhiteSpace(file.ModelTitle) ? key : file.ModelTitle;

var result = ProjectEvaluator.Evaluate(file, BlccLibrary.Load());

byte[]? excel = null;
if (withExcel)
{
    var tmp = Path.Combine(Path.GetTempPath(), $"lebr-{Guid.NewGuid():N}.xlsx");
    try
    {
        LebrExcelWriter.Write(tmp, result, new ShapeValues(file.Shape.PAr, file.Shape.Aspect, file.Shape.Rc), title);
        excel = File.ReadAllBytes(tmp);
    }
    finally
    {
        File.Delete(tmp);
    }
}

var snapshot = SnapshotBuilder.Build(result, key, title, name ?? $"{title} 評估", file.Shape, excel: excel);
Console.Error.WriteLine($"{title}：EEC {result.EEC:N0}、EECc {result.EECc:N0}、CFR {result.CFR:P2}、{result.Grade}");

if (outPath is not null)
{
    File.WriteAllText(outPath, SnapshotBuilder.ToJson(snapshot), new UTF8Encoding(false));
    Console.Error.WriteLine($"已寫入 {outPath}");
}

if (upload)
{
    using var client = new DashboardClient(uploadUrl);
    var r = await client.UploadAsync(snapshot);
    Console.Error.WriteLine(r.Ok ? $"已上傳到 {client.BaseUrl}，快照 ID {r.SnapshotId}" : r.Message);
    if (!r.Ok) return 2;
}

if (pdfPath is not null)
{
    try
    {
        var meta = new SummaryMeta(title, file.SavedAt, SnapshotBuilder.PluginVersion,
            Shape: new ShapeValues(file.Shape.PAr, file.Shape.Aspect, file.Shape.Rc));
        Console.Error.WriteLine($"已寫入 PDF 摘要 {PdfSummaryWriter.Write(pdfPath, LebrSummaryBuilder.Build(result, meta))}");
    }
    catch (Exception ex) when (ex is PdfPrinterUnavailableException or IOException)
    {
        Console.Error.WriteLine($"PDF 摘要匯出失敗：{ex.Message}");
        return 3;
    }
}

if (outPath is null && !upload && pdfPath is null)
    Console.WriteLine(SnapshotBuilder.ToJson(snapshot));

return 0;
