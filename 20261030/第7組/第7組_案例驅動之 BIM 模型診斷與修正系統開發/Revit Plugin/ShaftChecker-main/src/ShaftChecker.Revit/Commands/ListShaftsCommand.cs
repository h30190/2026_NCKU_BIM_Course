using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ShaftChecker.Core.Models;
using ShaftChecker.Revit.Collectors;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Commands
{
    /// <summary>只讀：列出辨識到的管道間與其資料，用來在檢討前確認模型資料是否齊全。</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ListShaftsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            var cfg = AddinPaths.LoadConfig();
            var shafts = new ShaftCollector(doc, cfg).Collect();

            if (shafts.Count == 0)
            {
                TaskDialog.Show("管道間清單",
                    "找不到管道間。\n\n辨識方式不只看名稱，也看房間備註、尺寸、內含機電元件、Shaft Opening 與上下樓層位置。\n" +
                    "請開啟「檢討儀表板」查看「疑似管道間」清單並確認；也可以直接把房間命名為：\n" +
                    string.Join("  ", cfg.ShaftNamePatterns));
                return Result.Succeeded;
            }

            var sb = new StringBuilder();
            foreach (ShaftInfo s in shafts)
            {
                s.EnsureMetrics();
                sb.AppendLine("■ " + s.Label + (s.SourceName != null ? "　（連結模型：" + s.SourceName + "）" : ""));
                if (s.Detection != null)
                    sb.AppendLine("   辨識：" + s.Detection.LevelLabel + "（" + s.Detection.Score + " 分）  " +
                                  string.Join("；", s.Detection.Reasons));
                sb.AppendLine("   淨寬 " + s.ClearWidthMm.ToString("F0") + " mm × 淨深 " +
                              s.ClearDepthMm.ToString("F0") + " mm，面積 " + s.AreaM2.ToString("F2") + " m²");

                foreach (var w in s.Walls)
                    sb.AppendLine("   牆  " + w.TypeName + "  防火時效：" + (w.FireRatingRaw ?? "(未填)"));

                if (s.Doors.Count == 0) sb.AppendLine("   門  (找不到維修門)");
                foreach (var d in s.Doors)
                {
                    string smoke = d.SmokeSeal.HasValue ? (d.SmokeSeal.Value ? "有" : "無") : "(未填)";
                    sb.AppendLine("   門  " + d.Label + "  " + d.WidthMm.ToString("F0") + "×" + d.HeightMm.ToString("F0") +
                                  "  防火：" + (d.FireRatingRaw ?? "(未填)") + "  遮煙：" + smoke +
                                  "  通往：" + (d.OtherSideRoomName ?? "(無房間)") +
                                  "  門前障礙：" + d.FrontObstructions.Count);
                }
                sb.AppendLine();
            }

            var dlg = new TaskDialog("管道間清單")
            {
                MainInstruction = "辨識到 " + shafts.Count + " 個管道間",
                MainContent = "共 " + shafts.Sum(s => s.Doors.Count) + " 扇維修門、" +
                              shafts.Sum(s => s.Walls.Count) + " 面邊界牆。展開下方查看明細。",
                ExpandedContent = sb.ToString()
            };
            dlg.Show();
            return Result.Succeeded;
        }
    }
}
