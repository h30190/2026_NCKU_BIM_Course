using System.IO;
using LebrCarbon.Core.Lebr;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Reports;
using LebrCarbon.Reports.Pdf;

namespace LebrCarbon.Revit;

/// <summary>
/// 匯出 Excel 計算書與 PDF 摘要（計畫書 F10），檔案放在 {模型名}.lebr.json 旁並加上時間戳。
/// 評估視窗與功能區「匯出報表」共用，檔名規則一致。
/// </summary>
public static class ReportExporter
{
    public static string ExcelPath(string projectPath, DateTime at) =>
        Path.ChangeExtension(projectPath, null) + $".計算書.{at:yyyyMMdd-HHmm}.xlsx";

    public static string PdfPath(string projectPath, DateTime at) =>
        Path.ChangeExtension(projectPath, null) + $".摘要.{at:yyyyMMdd-HHmm}.pdf";

    public static ShapeValues Shape(LebrProjectFile file) => new(file.Shape.PAr, file.Shape.Aspect, file.Shape.Rc);

    public static string WriteExcel(LebrResult r, LebrProjectFile file, string modelTitle, string projectPath, DateTime at)
    {
        var path = ExcelPath(projectPath, at);
        LebrExcelWriter.Write(path, r, Shape(file), modelTitle);
        return path;
    }

    /// <exception cref="PdfPrinterUnavailableException">電腦上沒有「Microsoft Print to PDF」。</exception>
    public static string WritePdf(LebrResult r, LebrProjectFile file, string modelTitle, string projectPath, DateTime at)
    {
        var meta = new SummaryMeta(modelTitle, at, SnapshotBuilder.PluginVersion, file.Manual, Shape(file));
        return PdfSummaryWriter.Write(PdfPath(projectPath, at), LebrSummaryBuilder.Build(r, meta));
    }
}
