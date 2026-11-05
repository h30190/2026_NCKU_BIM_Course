using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    /// <summary>
    /// 避難檢核圖紙自動排版服務 (Egress Sheet &amp; Note Service)
    /// </summary>
    public class EgressSheetNoteService
    {
        public const string DefaultScheduleName = "避難步行距離檢討表";

        /// <summary>
        /// 圖紙平面 2D 矩形結構（單位：Revit 內部英尺 Feet）
        /// </summary>
        private struct Rect2D
        {
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;

            public Rect2D(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double Width => Math.Max(0.0, MaxX - MinX);
            public double Height => Math.Max(0.0, MaxY - MinY);
            public bool IsValid => MaxX > MinX + 1e-6 && MaxY > MinY + 1e-6;

            public bool Contains(Rect2D inner)
            {
                return inner.MinX >= MinX - 1e-5 &&
                       inner.MaxX <= MaxX + 1e-5 &&
                       inner.MinY >= MinY - 1e-5 &&
                       inner.MaxY <= MaxY + 1e-5;
            }

            public bool Intersects(Rect2D other, double paddingFeet = 0.0)
            {
                return other.MaxX + paddingFeet > MinX + 1e-5 &&
                       other.MinX - paddingFeet < MaxX - 1e-5 &&
                       other.MaxY + paddingFeet > MinY + 1e-5 &&
                       other.MinY - paddingFeet < MaxY - 1e-5;
            }

            public double OverlapArea(Rect2D other, double paddingFeet = 0.0)
            {
                double ox = Math.Min(MaxX, other.MaxX + paddingFeet) - Math.Max(MinX, other.MinX - paddingFeet);
                double oy = Math.Min(MaxY, other.MaxY + paddingFeet) - Math.Max(MinY, other.MinY - paddingFeet);
                if (ox <= 0.0 || oy <= 0.0)
                    return 0.0;
                return ox * oy;
            }

            public object ToMmObject()
            {
                return new
                {
                    MinX = Math.Round(UnitUtils.ConvertFromInternalUnits(MinX, UnitTypeId.Millimeters), 2),
                    MinY = Math.Round(UnitUtils.ConvertFromInternalUnits(MinY, UnitTypeId.Millimeters), 2),
                    MaxX = Math.Round(UnitUtils.ConvertFromInternalUnits(MaxX, UnitTypeId.Millimeters), 2),
                    MaxY = Math.Round(UnitUtils.ConvertFromInternalUnits(MaxY, UnitTypeId.Millimeters), 2),
                    Width = Math.Round(UnitUtils.ConvertFromInternalUnits(Width, UnitTypeId.Millimeters), 2),
                    Height = Math.Round(UnitUtils.ConvertFromInternalUnits(Height, UnitTypeId.Millimeters), 2)
                };
            }
        }

        private struct LineSegment2D
        {
            public double X1;
            public double Y1;
            public double X2;
            public double Y2;

            public double MinX => Math.Min(X1, X2);
            public double MaxX => Math.Max(X1, X2);
            public double MinY => Math.Min(Y1, Y2);
            public double MaxY => Math.Max(Y1, Y2);
            public double MidX => 0.5 * (X1 + X2);
            public double MidY => 0.5 * (Y1 + Y2);
            public double Dx => Math.Abs(X2 - X1);
            public double Dy => Math.Abs(Y2 - Y1);
            public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
            public bool IsVertical => Dx <= UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters) && Dy > Dx * 3.0;
            public bool IsHorizontal => Dy <= UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Millimeters) && Dx > Dy * 3.0;
        }

        /// <summary>
        /// 從 JSON 參數解析並執行避難檢核圖紙自動排版（供 CommandExecutor 呼叫）
        /// </summary>
        public static object GenerateEgressSheetNote(Document doc, JObject parameters)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            string sheetNumber = parameters["sheetNumber"]?.Value<string>()?.Trim() ?? string.Empty;
            string regulationNoteText = parameters["regulationNoteText"]?.Value<string>() ?? string.Empty;
            bool placeScheduleOnSheet = parameters["placeScheduleOnSheet"]?.Value<bool>() ?? false;

            return GenerateEgressSheetNote(doc, sheetNumber, regulationNoteText, placeScheduleOnSheet);
        }

        /// <summary>
        /// 確保專案中已建立「避難步行距離檢討表」明細表（ViewSchedule），並包含完整 6 個檢討欄位。
        /// 預設不將明細表排版放置於圖紙上，若 removeSheetInstances 為 true 則會移除圖紙上的明細表實例（保留專案明細表本身）。
        /// </summary>
        public static ViewSchedule EnsureOrUpdateEgressSchedule(
            Document doc,
            out List<string> addedFieldNames,
            bool removeSheetInstances = true)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            addedFieldNames = new List<string>();

            // 先移除舊有同名明細表並重新建立，以確保欄位順序與名稱一致為「避難步行距離檢討表」
            ViewSchedule? existingSchedule = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(vs =>
                    !vs.IsTemplate &&
                    string.Equals(vs.Name, DefaultScheduleName, StringComparison.OrdinalIgnoreCase));

            if (existingSchedule != null)
            {
                if (removeSheetInstances)
                {
                    var existingInstances = new FilteredElementCollector(doc)
                        .OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .Where(ssi => ssi.ScheduleId == existingSchedule.Id)
                        .Select(ssi => ssi.Id)
                        .ToList();

                    foreach (ElementId ssiId in existingInstances)
                    {
                        try
                        {
                            doc.Delete(ssiId);
                        }
                        catch
                        {
                            // 忽略單一圖紙明細表實例刪除失敗
                        }
                    }
                }

                try
                {
                    doc.Delete(existingSchedule.Id);
                }
                catch
                {
                    // 若舊明細表因故無法刪除，後續命名會自動附加序號避免衝突
                }
            }

            ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Rooms));
            SetScheduleNameSafely(doc, schedule, DefaultScheduleName);

            // 加入欄位（ScheduleField）：
            // 包含「房間編號(Number)」、「房間名稱(Name)」，以及 4 個共用參數 GUID
            // (Egress_Actual_Dist, Egress_Max_Allowed, Egress_Target_Exit, Egress_Is_Compliant)
            IList<SchedulableField> schedulableFields = schedule.Definition.GetSchedulableFields();

            AddBuiltInField(doc, schedule, schedulableFields, BuiltInParameter.ROOM_NUMBER, new[] { "Number", "編號" }, addedFieldNames);
            AddBuiltInField(doc, schedule, schedulableFields, BuiltInParameter.ROOM_NAME, new[] { "Name", "名稱" }, addedFieldNames);

            AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidActualDist, EgressAuditService.ParamActualDist, addedFieldNames);
            AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidMaxAllowed, EgressAuditService.ParamMaxAllowed, addedFieldNames);
            AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidTargetExit, EgressAuditService.ParamTargetExit, addedFieldNames);
            AddSharedParameterField(doc, schedule, schedulableFields, EgressAuditService.GuidIsCompliant, EgressAuditService.ParamIsCompliant, addedFieldNames);

            return schedule;
        }

        /// <summary>
        /// 建立或更新專案中的「避難步行距離檢討表」明細表（預設僅存於 Revit 明細表清單、不放置於圖紙上），
        /// 並自動識別目標圖紙的圖框內有效範圍與既有圖元障礙物，將法規檢討說明文字無重疊放置於圖框內空白處 (generate_egress_sheet_note)
        /// </summary>
        public static object GenerateEgressSheetNote(
            Document doc,
            string sheetNumber,
            string regulationNoteText,
            bool placeScheduleOnSheet = false)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(sheetNumber))
                throw new ArgumentException("請提供目標圖紙圖號 (sheetNumber)", nameof(sheetNumber));

            if (string.IsNullOrWhiteSpace(regulationNoteText))
            {
                regulationNoteText = "【避難步行距離法規檢討說明】\n" +
                                     "1. 依據建築技術規則建築設計施工編第 93 條規定檢討各居室至直通樓梯／安全梯之步行距離。\n" +
                                     "2. 檢討方式採各居室最遠角落頂點內縮 0.3m 起算，取各頂點至最近出口之最不利逃生路徑長度。";
            }

            using (TransactionGroup tg = new TransactionGroup(doc, "避難檢核圖紙自動排版"))
            {
                tg.Start();

                try
                {
                    // 尋找目標圖紙：透過 FilteredElementCollector 尋找 ViewSheet，比對 SheetNumber == sheetNumber
                    ViewSheet? sheet = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .FirstOrDefault(s =>
                            !s.IsTemplate &&
                            string.Equals(s.SheetNumber?.Trim(), sheetNumber.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (sheet == null)
                    {
                        throw new InvalidOperationException($"找不到圖號為 '{sheetNumber}' 的圖紙 (ViewSheet)");
                    }

                    // 確保房間的 4 個避難檢核共用參數已綁定至 OST_Rooms
                    EgressAuditService.EnsureRoomSharedParameters(doc);

                    ViewSchedule schedule;
                    ScheduleSheetInstance? scheduleInstance = null;
                    TextNote textNote;
                    XYZ schedulePoint = XYZ.Zero;
                    XYZ textLocationXyz = XYZ.Zero;
                    Rect2D titleBlockOuterBounds;
                    Rect2D safeAreaBounds;
                    Rect2D finalTextBounds;
                    List<Rect2D> obstacles;
                    bool isCollisionFree = true;
                    string placementZone = "圖框內右下/右側空白區";
                    double adoptedWidthMm = 150.0;
                    List<string> addedFieldNames;

                    using (Transaction trans = new Transaction(doc, "建立避難步行距離檢討表與圖紙智慧尋空說明"))
                    {
                        trans.Start();

                        // 1. 於專案中建立或更新「避難步行距離檢討表」明細表（若不排版至圖紙則自動清除圖紙上的舊明細表實例）
                        schedule = EnsureOrUpdateEgressSchedule(
                            doc,
                            out addedFieldNames,
                            removeSheetInstances: !placeScheduleOnSheet);

                        // 2. 若圖紙上已有先前產生的避難步行距離法規檢討文字備註，先移除避免被誤判為障礙物或重疊
                        var existingNotes = new FilteredElementCollector(doc, sheet.Id)
                            .OfClass(typeof(TextNote))
                            .Cast<TextNote>()
                            .Where(tn =>
                            {
                                string txt = tn.Text ?? string.Empty;
                                return txt.Contains("避難步行距離") || txt.Contains("建築技術規則第93條");
                            })
                            .Select(tn => tn.Id)
                            .ToList();

                        foreach (ElementId oldNoteId in existingNotes)
                        {
                            try
                            {
                                doc.Delete(oldNoteId);
                            }
                            catch
                            {
                                // 忽略舊文字刪除失敗
                            }
                        }

                        // 3. 步驟 1：自動識別圖框外框、內框有效區 (SafeArea) 與圖框內圖籤／標題欄區域
                        safeAreaBounds = ResolveTitleBlockAndSafeArea(
                            doc,
                            sheet,
                            out titleBlockOuterBounds,
                            out List<Rect2D> titleBlockObstacles);

                        // 4. 建立初始 TextNote（與選配的明細表），以便透過 Regenerate 量測真實 BoundingBox 尺寸
                        ElementId textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                        if (textTypeId == ElementId.InvalidElementId)
                        {
                            textTypeId = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .FirstElementId();
                        }

                        if (textTypeId == ElementId.InvalidElementId)
                        {
                            throw new InvalidOperationException("專案中找不到可用的文字類型 (TextNoteType)");
                        }

                        TextNoteOptions textNoteOptions = new TextNoteOptions(textTypeId)
                        {
                            HorizontalAlignment = HorizontalTextAlignment.Left
                        };

                        XYZ initialSeedPoint = new XYZ(safeAreaBounds.MinX, safeAreaBounds.MaxY, 0.0);
                        textNote = TextNote.Create(doc, sheet.Id, initialSeedPoint, regulationNoteText, textNoteOptions);
                        if (textNote == null)
                        {
                            throw new InvalidOperationException("無法於圖紙上建立法規檢討說明文字 (TextNote)");
                        }

                        var createdIds = new HashSet<ElementId> { textNote.Id };
                        if (placeScheduleOnSheet)
                        {
                            scheduleInstance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, initialSeedPoint);
                            if (scheduleInstance != null)
                            {
                                createdIds.Add(scheduleInstance.Id);
                            }
                        }

                        // 5. 步驟 2：收集圖紙上所有既有圖面資料（Viewport 視埠、視埠標題、既有明細表、文字註記、圖框標題欄）作為避讓障礙物
                        obstacles = CollectSheetObstacles(doc, sheet, safeAreaBounds, createdIds, titleBlockObstacles);

                        // 6. 步驟 3 & 4：動態量測輸出文字真實尺寸，並於圖框內有效區搜尋無重疊空白位置（支援寬度自適應調整）
                        PlaceElementsInBlankSpace(
                            doc,
                            sheet,
                            textNote,
                            scheduleInstance,
                            safeAreaBounds,
                            obstacles,
                            regulationNoteText,
                            out schedulePoint,
                            out textLocationXyz,
                            out finalTextBounds,
                            out adoptedWidthMm,
                            out isCollisionFree,
                            out placementZone);

                        trans.Commit();
                    }

                    tg.Assimilate();

                    return new
                    {
                        Success = true,
                        SheetId = sheet.Id.GetIdValue(),
                        SheetNumber = sheet.SheetNumber,
                        SheetName = sheet.Name,
                        ScheduleId = schedule.Id.GetIdValue(),
                        ScheduleName = schedule.Name,
                        PlacedScheduleOnSheet = placeScheduleOnSheet,
                        ScheduleSheetInstanceId = scheduleInstance?.Id.GetIdValue(),
                        AddedFields = addedFieldNames,
                        TextNoteId = textNote?.Id.GetIdValue(),
                        TextWidthMm = adoptedWidthMm,
                        IsCollisionFree = isCollisionFree,
                        PlacementZone = placementZone,
                        ObstacleCount = obstacles.Count,
                        TitleBlockBoundsMm = titleBlockOuterBounds.ToMmObject(),
                        SafeAreaBoundsMm = safeAreaBounds.ToMmObject(),
                        TextBoundsMm = finalTextBounds.ToMmObject(),
                        ScheduleLocationMm = placeScheduleOnSheet
                            ? new
                            {
                                X = Math.Round(UnitUtils.ConvertFromInternalUnits(schedulePoint.X, UnitTypeId.Millimeters), 2),
                                Y = Math.Round(UnitUtils.ConvertFromInternalUnits(schedulePoint.Y, UnitTypeId.Millimeters), 2)
                            }
                            : null,
                        TextNoteLocationMm = new
                        {
                            X = Math.Round(UnitUtils.ConvertFromInternalUnits(textLocationXyz.X, UnitTypeId.Millimeters), 2),
                            Y = Math.Round(UnitUtils.ConvertFromInternalUnits(textLocationXyz.Y, UnitTypeId.Millimeters), 2)
                        },
                        Message = placeScheduleOnSheet
                            ? $"已於圖紙 {sheet.SheetNumber} ({sheet.Name}) 識別圖框空白處（{placementZone}），無重疊放置「{schedule.Name}」與法規檢討說明文字"
                            : $"已於 Revit 專案建立「{schedule.Name}」明細表（不直接排版至圖紙），並於圖紙 {sheet.SheetNumber} ({sheet.Name}) 識別圖框內空白處（{placementZone}）放置法規檢討說明文字"
                    };
                }
                catch
                {
                    if (tg.HasStarted())
                    {
                        tg.RollBack();
                    }
                    throw;
                }
            }
        }

        /// <summary>
        /// 步驟 1：自動識別圖紙上的圖框 (OST_TitleBlocks) 外框、內框有效區 (SafeArea) 以及圖框內標題欄／圖籤區域
        /// </summary>
        private static Rect2D ResolveTitleBlockAndSafeArea(
            Document doc,
            ViewSheet sheet,
            out Rect2D titleBlockOuter,
            out List<Rect2D> titleBlockObstacles)
        {
            titleBlockObstacles = new List<Rect2D>();

            double mmToFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            double defaultLeftMargin = 20.0 * mmToFeet;   // 裝訂側邊距 20mm
            double defaultEdgeMargin = 15.0 * mmToFeet;   // 上／下／右側內框邊距 15mm
            double innerClearance = 5.0 * mmToFeet;       // 內框線安全淨距 5mm

            Element? titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            BoundingBoxXYZ? tbBox = titleBlock?.get_BoundingBox(sheet);
            if (tbBox != null && tbBox.Max.X > tbBox.Min.X && tbBox.Max.Y > tbBox.Min.Y)
            {
                titleBlockOuter = new Rect2D(tbBox.Min.X, tbBox.Min.Y, tbBox.Max.X, tbBox.Max.Y);
            }
            else
            {
                BoundingBoxUV? outline = sheet.Outline;
                if (outline != null && outline.Max.U > outline.Min.U && outline.Max.V > outline.Min.V)
                {
                    titleBlockOuter = new Rect2D(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);
                }
                else
                {
                    // 標準 A1 橫式圖框經驗範圍 (841mm x 594mm)
                    titleBlockOuter = new Rect2D(0.0, 0.0, 841.0 * mmToFeet, 594.0 * mmToFeet);
                }
            }

            Rect2D outer = titleBlockOuter;
            double safeMinX = outer.MinX + defaultLeftMargin;
            double safeMinY = outer.MinY + defaultEdgeMargin;
            double safeMaxX = outer.MaxX - defaultEdgeMargin;
            double safeMaxY = outer.MaxY - defaultEdgeMargin;

            if (titleBlock != null)
            {
                List<LineSegment2D> segments = ExtractTitleBlockLineSegments(titleBlock, sheet, outer);
                double tbWidth = outer.Width;
                double tbHeight = outer.Height;

                if (segments.Count > 0 && tbWidth > 100.0 * mmToFeet && tbHeight > 100.0 * mmToFeet)
                {
                    // (A) 識別四周長內框線（長度 >= 55% 圖框寬或高）
                    var leftBorderLines = segments
                        .Where(s => s.IsVertical &&
                                    s.Length >= tbHeight * 0.55 &&
                                    s.MidX >= outer.MinX + 4.0 * mmToFeet &&
                                    s.MidX <= outer.MinX + 40.0 * mmToFeet)
                        .ToList();
                    if (leftBorderLines.Count > 0)
                    {
                        double innerLeft = leftBorderLines.Max(s => s.MaxX) + innerClearance;
                        safeMinX = Math.Max(safeMinX, innerLeft);
                    }

                    var rightBorderLines = segments
                        .Where(s => s.IsVertical &&
                                    s.Length >= tbHeight * 0.55 &&
                                    s.MidX >= outer.MaxX - 40.0 * mmToFeet &&
                                    s.MidX <= outer.MaxX - 4.0 * mmToFeet)
                        .ToList();
                    if (rightBorderLines.Count > 0)
                    {
                        double innerRight = rightBorderLines.Min(s => s.MinX) - innerClearance;
                        safeMaxX = Math.Min(safeMaxX, innerRight);
                    }

                    var bottomBorderLines = segments
                        .Where(s => s.IsHorizontal &&
                                    s.Length >= tbWidth * 0.55 &&
                                    s.MidY >= outer.MinY + 4.0 * mmToFeet &&
                                    s.MidY <= outer.MinY + 35.0 * mmToFeet)
                        .ToList();
                    if (bottomBorderLines.Count > 0)
                    {
                        double innerBottom = bottomBorderLines.Max(s => s.MaxY) + innerClearance;
                        safeMinY = Math.Max(safeMinY, innerBottom);
                    }

                    var topBorderLines = segments
                        .Where(s => s.IsHorizontal &&
                                    s.Length >= tbWidth * 0.55 &&
                                    s.MidY >= outer.MaxY - 35.0 * mmToFeet &&
                                    s.MidY <= outer.MaxY - 4.0 * mmToFeet)
                        .ToList();
                    if (topBorderLines.Count > 0)
                    {
                        double innerTop = topBorderLines.Min(s => s.MinY) - innerClearance;
                        safeMaxY = Math.Min(safeMaxY, innerTop);
                    }

                    // (B) 識別右側直式圖籤欄（常見於台灣建築執照圖框右側 40mm ~ 210mm 處的貫穿分隔線）
                    var rightColumnDividers = segments
                        .Where(s => s.IsVertical &&
                                    s.Length >= tbHeight * 0.45 &&
                                    s.MidX >= outer.MaxX - 210.0 * mmToFeet &&
                                    s.MidX <= outer.MaxX - 38.0 * mmToFeet)
                        .OrderBy(s => s.MidX)
                        .ToList();

                    bool foundRightStrip = false;
                    foreach (var div in rightColumnDividers)
                    {
                        // 確認該垂直分隔線右側有足夠的水平分格線（代表圖籤表格）
                        int rightCells = segments.Count(s =>
                            s.IsHorizontal &&
                            s.Length >= 15.0 * mmToFeet &&
                            s.MidX > div.MidX &&
                            s.MaxX <= outer.MaxX + 2.0 * mmToFeet);

                        if (rightCells >= 3)
                        {
                            safeMaxX = Math.Min(safeMaxX, div.MinX - innerClearance);
                            titleBlockObstacles.Add(new Rect2D(div.MinX, outer.MinY, outer.MaxX, outer.MaxY));
                            foundRightStrip = true;
                            break;
                        }
                    }

                    // (C) 識別下方橫式圖籤欄（位於圖框底部 25mm ~ 115mm 處的長水平分隔線）
                    var bottomRowDividers = segments
                        .Where(s => s.IsHorizontal &&
                                    s.Length >= tbWidth * 0.45 &&
                                    s.MidY >= outer.MinY + 22.0 * mmToFeet &&
                                    s.MidY <= outer.MinY + 115.0 * mmToFeet)
                        .OrderByDescending(s => s.MidY)
                        .ToList();

                    bool foundBottomStrip = false;
                    foreach (var div in bottomRowDividers)
                    {
                        int bottomCells = segments.Count(s =>
                            s.IsVertical &&
                            s.Length >= 10.0 * mmToFeet &&
                            s.MidY < div.MidY &&
                            s.MinY >= outer.MinY - 2.0 * mmToFeet);

                        if (bottomCells >= 3)
                        {
                            safeMinY = Math.Max(safeMinY, div.MaxY + innerClearance);
                            titleBlockObstacles.Add(new Rect2D(outer.MinX, outer.MinY, outer.MaxX, div.MaxY));
                            foundBottomStrip = true;
                            break;
                        }
                    }

                    // (D) 識別右下角局部圖籤塊（若非整條貫穿欄，而是右下角局部表格框）
                    if (!foundRightStrip && !foundBottomStrip)
                    {
                        double cornerRegionMinX = outer.MaxX - 260.0 * mmToFeet;
                        double cornerRegionMaxY = outer.MinY + 180.0 * mmToFeet;

                        var cornerTableLines = segments
                            .Where(s => s.Length >= 10.0 * mmToFeet &&
                                        s.Length <= Math.Max(tbWidth, tbHeight) * 0.45 &&
                                        s.MinX >= cornerRegionMinX &&
                                        s.MaxY <= cornerRegionMaxY &&
                                        s.MaxX <= safeMaxX + 2.0 * mmToFeet &&
                                        s.MinY >= safeMinY - 2.0 * mmToFeet)
                            .ToList();

                        if (cornerTableLines.Count >= 4)
                        {
                            Rect2D stampRect = new Rect2D(
                                cornerTableLines.Min(s => s.MinX),
                                cornerTableLines.Min(s => s.MinY),
                                cornerTableLines.Max(s => s.MaxX),
                                cornerTableLines.Max(s => s.MaxY));
                            if (stampRect.IsValid)
                            {
                                titleBlockObstacles.Add(stampRect);
                            }
                        }
                    }
                }
            }

            // 防呆：若圖框幾何異常導致安全區過窄，退回基本邊距安全區
            if (safeMaxX - safeMinX < 180.0 * mmToFeet || safeMaxY - safeMinY < 120.0 * mmToFeet)
            {
                safeMinX = titleBlockOuter.MinX + defaultLeftMargin;
                safeMinY = titleBlockOuter.MinY + defaultEdgeMargin;
                safeMaxX = titleBlockOuter.MaxX - defaultEdgeMargin;
                safeMaxY = titleBlockOuter.MaxY - defaultEdgeMargin;
            }

            return new Rect2D(safeMinX, safeMinY, safeMaxX, safeMaxY);
        }

        /// <summary>
        /// 擷取圖框 (TitleBlock) 幾何中的水平與垂直線段，供內框與標題欄辨識使用
        /// </summary>
        private static List<LineSegment2D> ExtractTitleBlockLineSegments(
            Element titleBlock,
            ViewSheet sheet,
            Rect2D outerBounds)
        {
            var list = new List<LineSegment2D>();
            try
            {
                Options opts = new Options
                {
                    View = sheet,
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false
                };

                GeometryElement? geom = titleBlock.get_Geometry(opts) ?? titleBlock.get_Geometry(new Options());
                if (geom != null)
                {
                    CollectGeometryLinesRecursive(geom, Transform.Identity, outerBounds, list);
                }
            }
            catch
            {
                // 忽略個別圖框幾何讀取例外
            }
            return list;
        }

        private static void CollectGeometryLinesRecursive(
            GeometryElement geomElem,
            Transform currentTransform,
            Rect2D outerBounds,
            List<LineSegment2D> output)
        {
            foreach (GeometryObject obj in geomElem)
            {
                if (obj is Line line && line.IsBound)
                {
                    XYZ p1 = currentTransform.OfPoint(line.GetEndPoint(0));
                    XYZ p2 = currentTransform.OfPoint(line.GetEndPoint(1));
                    var seg = new LineSegment2D
                    {
                        X1 = p1.X,
                        Y1 = p1.Y,
                        X2 = p2.X,
                        Y2 = p2.Y
                    };
                    if ((seg.IsHorizontal || seg.IsVertical) &&
                        seg.MinX >= outerBounds.MinX - 0.1 &&
                        seg.MaxX <= outerBounds.MaxX + 0.1 &&
                        seg.MinY >= outerBounds.MinY - 0.1 &&
                        seg.MaxY <= outerBounds.MaxY + 0.1)
                    {
                        output.Add(seg);
                    }
                }
                else if (obj is PolyLine poly)
                {
                    IList<XYZ> coords = poly.GetCoordinates();
                    for (int i = 0; i + 1 < coords.Count; i++)
                    {
                        XYZ p1 = currentTransform.OfPoint(coords[i]);
                        XYZ p2 = currentTransform.OfPoint(coords[i + 1]);
                        var seg = new LineSegment2D
                        {
                            X1 = p1.X,
                            Y1 = p1.Y,
                            X2 = p2.X,
                            Y2 = p2.Y
                        };
                        if (seg.IsHorizontal || seg.IsVertical)
                        {
                            output.Add(seg);
                        }
                    }
                }
                else if (obj is GeometryInstance gi)
                {
                    GeometryElement? symbolGeom = gi.GetSymbolGeometry();
                    if (symbolGeom != null)
                    {
                        CollectGeometryLinesRecursive(symbolGeom, currentTransform.Multiply(gi.Transform), outerBounds, output);
                    }
                }
            }
        }

        /// <summary>
        /// 步驟 2：收集圖紙上所有不可覆蓋的既有圖元邊界（視埠、視埠標題、既有明細表、文字註記、圖例與圖框標題欄）
        /// </summary>
        private static List<Rect2D> CollectSheetObstacles(
            Document doc,
            ViewSheet sheet,
            Rect2D safeArea,
            HashSet<ElementId> ignoreElementIds,
            List<Rect2D> titleBlockObstacles)
        {
            var obstacles = new List<Rect2D>();
            if (titleBlockObstacles != null)
            {
                obstacles.AddRange(titleBlockObstacles.Where(r => r.IsValid));
            }

            // 1. 收集所有 Viewport（含視埠框 BoxOutline 與視埠標題 LabelOutline）
            ICollection<ElementId> viewportIds = sheet.GetAllViewports();
            foreach (ElementId vpId in viewportIds)
            {
                if (ignoreElementIds.Contains(vpId))
                    continue;

                Viewport? vp = doc.GetElement(vpId) as Viewport;
                if (vp == null)
                    continue;

                try
                {
                    Outline boxOutline = vp.GetBoxOutline();
                    if (boxOutline != null)
                    {
                        var rect = new Rect2D(
                            boxOutline.MinimumPoint.X,
                            boxOutline.MinimumPoint.Y,
                            boxOutline.MaximumPoint.X,
                            boxOutline.MaximumPoint.Y);
                        if (rect.IsValid)
                        {
                            obstacles.Add(rect);
                        }
                    }
                }
                catch
                {
                    // 忽略個別視埠 BoxOutline 讀取失敗
                }

                try
                {
                    Outline labelOutline = vp.GetLabelOutline();
                    if (labelOutline != null)
                    {
                        var labelRect = new Rect2D(
                            labelOutline.MinimumPoint.X,
                            labelOutline.MinimumPoint.Y,
                            labelOutline.MaximumPoint.X,
                            labelOutline.MaximumPoint.Y);
                        if (labelRect.IsValid)
                        {
                            obstacles.Add(labelRect);
                        }
                    }
                }
                catch
                {
                    // 忽略無標題視埠之 LabelOutline 例外
                }
            }

            // 2. 收集圖紙上的明細表實例 (ScheduleSheetInstance)
            var scheduleInstances = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>();

            foreach (ScheduleSheetInstance ssi in scheduleInstances)
            {
                if (ignoreElementIds.Contains(ssi.Id) || ssi.IsTitleblockRevisionSchedule)
                    continue;

                BoundingBoxXYZ? ssiBox = ssi.get_BoundingBox(sheet);
                if (ssiBox != null)
                {
                    var rect = new Rect2D(ssiBox.Min.X, ssiBox.Min.Y, ssiBox.Max.X, ssiBox.Max.Y);
                    if (rect.IsValid)
                    {
                        obstacles.Add(rect);
                    }
                }
            }

            // 3. 收集圖紙上既有的其他註記／文字／符號／詳圖線元素
            long titleBlockCatId = (long)BuiltInCategory.OST_TitleBlocks;
            var sheetElements = new FilteredElementCollector(doc, sheet.Id)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in sheetElements)
            {
                if (elem == null ||
                    elem.Id == sheet.Id ||
                    ignoreElementIds.Contains(elem.Id) ||
                    elem is Viewport ||
                    elem is ScheduleSheetInstance)
                {
                    continue;
                }

                if (elem.Category == null || elem.Category.Id.GetIdValue() == titleBlockCatId)
                {
                    continue;
                }

                BoundingBoxXYZ? bb = elem.get_BoundingBox(sheet);
                if (bb == null)
                    continue;

                var rect = new Rect2D(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
                if (!rect.IsValid)
                    continue;

                // 排除涵蓋整張圖紙的系統參考框
                if (rect.Width >= safeArea.Width * 0.92 && rect.Height >= safeArea.Height * 0.92)
                    continue;

                obstacles.Add(rect);
            }

            return obstacles;
        }

        /// <summary>
        /// 步驟 3 &amp; 4：量測輸出元素的真實寬高，並在圖框內有效安全區搜尋不與任何障礙物重疊的空白處定位
        /// </summary>
        private static void PlaceElementsInBlankSpace(
            Document doc,
            ViewSheet sheet,
            TextNote textNote,
            ScheduleSheetInstance? scheduleInstance,
            Rect2D safeArea,
            List<Rect2D> obstacles,
            string regulationNoteText,
            out XYZ finalSchedulePoint,
            out XYZ finalTextPoint,
            out Rect2D finalTextBounds,
            out double adoptedWidthMm,
            out bool isCollisionFree,
            out string placementZone)
        {
            double mmToFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            double verticalGapFeet = 8.0 * mmToFeet;

            // 候選文字寬度（優先以標準 150mm 排版，若圖面空間緊湊則自動嘗試窄欄 120mm、寬扁欄 185mm 或緊湊 105mm）
            double[] candidateWidthsMm = new[] { 150.0, 120.0, 185.0, 105.0 };
            double[] candidateClearancesMm = new[] { 8.0, 4.0 };

            adoptedWidthMm = 150.0;
            isCollisionFree = false;
            placementZone = "圖框內右下/右側空白區";

            Rect2D chosenTargetRect = default;
            Rect2D measuredTextBox = default;
            Rect2D measuredSchedBox = default;
            bool hasScheduleBox = false;

            foreach (double clearanceMm in candidateClearancesMm)
            {
                double clearanceFeet = clearanceMm * mmToFeet;

                foreach (double widthMm in candidateWidthsMm)
                {
                    double targetWidthFeet = widthMm * mmToFeet;
                    if (targetWidthFeet > safeArea.Width)
                        continue;

                    textNote.Width = targetWidthFeet;
                    doc.Regenerate();

                    measuredTextBox = GetElementSheetRectOrEstimate(textNote, sheet, targetWidthFeet, regulationNoteText, mmToFeet);

                    double combinedWidth = measuredTextBox.Width;
                    double combinedHeight = measuredTextBox.Height;

                    if (scheduleInstance != null)
                    {
                        BoundingBoxXYZ? sb = scheduleInstance.get_BoundingBox(sheet);
                        if (sb != null && sb.Max.X > sb.Min.X && sb.Max.Y > sb.Min.Y)
                        {
                            measuredSchedBox = new Rect2D(sb.Min.X, sb.Min.Y, sb.Max.X, sb.Max.Y);
                            hasScheduleBox = true;
                            combinedWidth = Math.Max(measuredSchedBox.Width, measuredTextBox.Width);
                            combinedHeight = measuredSchedBox.Height + verticalGapFeet + measuredTextBox.Height;
                        }
                    }

                    if (TryFindBestFreeRect(
                        safeArea,
                        obstacles,
                        combinedWidth,
                        combinedHeight,
                        clearanceFeet,
                        requireZeroOverlap: true,
                        out Rect2D candidateRect,
                        out string zoneName))
                    {
                        adoptedWidthMm = widthMm;
                        chosenTargetRect = candidateRect;
                        isCollisionFree = true;
                        placementZone = zoneName;
                        break;
                    }
                }

                if (isCollisionFree)
                    break;
            }

            // 若圖紙極度擁擠、所有候選寬度皆無法找到 100% 零碰撞區，則以 150mm 寬度選取重疊面積最小的圖框內位置
            if (!isCollisionFree)
            {
                adoptedWidthMm = 150.0;
                double fallbackWidthFeet = adoptedWidthMm * mmToFeet;
                textNote.Width = fallbackWidthFeet;
                doc.Regenerate();

                measuredTextBox = GetElementSheetRectOrEstimate(textNote, sheet, fallbackWidthFeet, regulationNoteText, mmToFeet);
                double combinedWidth = measuredTextBox.Width;
                double combinedHeight = measuredTextBox.Height;

                if (scheduleInstance != null)
                {
                    BoundingBoxXYZ? sb = scheduleInstance.get_BoundingBox(sheet);
                    if (sb != null && sb.Max.X > sb.Min.X && sb.Max.Y > sb.Min.Y)
                    {
                        measuredSchedBox = new Rect2D(sb.Min.X, sb.Min.Y, sb.Max.X, sb.Max.Y);
                        hasScheduleBox = true;
                        combinedWidth = Math.Max(measuredSchedBox.Width, measuredTextBox.Width);
                        combinedHeight = measuredSchedBox.Height + verticalGapFeet + measuredTextBox.Height;
                    }
                }

                TryFindBestFreeRect(
                    safeArea,
                    obstacles,
                    combinedWidth,
                    combinedHeight,
                    2.0 * mmToFeet,
                    requireZeroOverlap: false,
                    out chosenTargetRect,
                    out placementZone);
            }

            // 將明細表與文字精準平移至 chosenTargetRect 內（確保實際 BoundingBox 完全落在目標空白矩形中）
            if (scheduleInstance != null && hasScheduleBox)
            {
                // 明細表置於 chosenTargetRect 上半部（左對齊 chosenTargetRect.MinX，上緣對齊 chosenTargetRect.MaxY）
                XYZ schedDelta = new XYZ(
                    chosenTargetRect.MinX - measuredSchedBox.MinX,
                    chosenTargetRect.MaxY - measuredSchedBox.MaxY,
                    0.0);
                ElementTransformUtils.MoveElement(doc, scheduleInstance.Id, schedDelta);

                // 文字置於 chosenTargetRect 下半部（左對齊 chosenTargetRect.MinX，下緣對齊 chosenTargetRect.MinY）
                XYZ textDelta = new XYZ(
                    chosenTargetRect.MinX - measuredTextBox.MinX,
                    chosenTargetRect.MinY - measuredTextBox.MinY,
                    0.0);
                ElementTransformUtils.MoveElement(doc, textNote.Id, textDelta);
            }
            else
            {
                XYZ textDelta = new XYZ(
                    chosenTargetRect.MinX - measuredTextBox.MinX,
                    chosenTargetRect.MinY - measuredTextBox.MinY,
                    0.0);
                ElementTransformUtils.MoveElement(doc, textNote.Id, textDelta);
            }

            doc.Regenerate();

            finalTextBounds = GetElementSheetRectOrEstimate(textNote, sheet, adoptedWidthMm * mmToFeet, regulationNoteText, mmToFeet);
            finalTextPoint = textNote.Coord;
            finalSchedulePoint = scheduleInstance != null
                ? new XYZ(chosenTargetRect.MinX, chosenTargetRect.MaxY, 0.0)
                : XYZ.Zero;
        }

        /// <summary>
        /// 讀取 TextNote 在圖紙上的實際 BoundingBox，若視圖尚未產生 BoundingBox 則依字數與行數進行保守估算
        /// </summary>
        private static Rect2D GetElementSheetRectOrEstimate(
            TextNote textNote,
            ViewSheet sheet,
            double targetWidthFeet,
            string text,
            double mmToFeet)
        {
            BoundingBoxXYZ? bb = textNote.get_BoundingBox(sheet);
            if (bb != null && bb.Max.X > bb.Min.X + 1e-4 && bb.Max.Y > bb.Min.Y + 1e-4)
            {
                return new Rect2D(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
            }

            // 保守估算：計算換行後總行數（每行約 22 個全形字／150mm 寬，每行高約 5.5mm）
            string[] rawLines = (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int charsPerLine = Math.Max(10, (int)Math.Floor((targetWidthFeet / mmToFeet) / 6.5));
            int estimatedLines = 0;
            foreach (string line in rawLines)
            {
                estimatedLines += Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, line.Length) / charsPerLine));
            }

            double estHeightFeet = Math.Max(45.0 * mmToFeet, estimatedLines * 5.8 * mmToFeet);
            XYZ coord = textNote.Coord;
            return new Rect2D(coord.X, coord.Y - estHeightFeet, coord.X + targetWidthFeet, coord.Y);
        }

        /// <summary>
        /// 在圖框內有效安全區 (SafeArea) 搜尋最佳空白矩形位置。
        /// 優先順序：
        ///   Tier 1: 右下／右側空白區（靠近有效區右緣）
        ///   Tier 2: 下方空白帶（靠近有效區下緣，由右往左）
        ///   Tier 3: 左側／上方或其他圖框內空白區
        /// </summary>
        private static bool TryFindBestFreeRect(
            Rect2D safeArea,
            List<Rect2D> obstacles,
            double reqWidth,
            double reqHeight,
            double clearanceFeet,
            bool requireZeroOverlap,
            out Rect2D bestRect,
            out string placementZone)
        {
            double mmToFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            double step = 10.0 * mmToFeet; // 10mm 搜尋網格步進

            double maxAllowedMinX = safeArea.MaxX - reqWidth;
            double maxAllowedMinY = safeArea.MaxY - reqHeight;

            if (maxAllowedMinX < safeArea.MinX - 1e-5 || maxAllowedMinY < safeArea.MinY - 1e-5)
            {
                bestRect = new Rect2D(
                    Math.Max(safeArea.MinX, safeArea.MaxX - reqWidth),
                    safeArea.MinY,
                    safeArea.MaxX,
                    Math.Min(safeArea.MaxY, safeArea.MinY + reqHeight));
                placementZone = "圖框內預設基準區";
                return false;
            }

            // 建立候選 X 與 Y 座標集合（包含 10mm 網格點、安全區邊界點、以及各障礙物外擴邊界對齊點）
            var candXs = new List<double> { safeArea.MinX, maxAllowedMinX };
            for (double x = maxAllowedMinX; x >= safeArea.MinX; x -= step)
            {
                candXs.Add(x);
            }
            foreach (Rect2D obs in obstacles)
            {
                double leftOfObs = obs.MinX - clearanceFeet - reqWidth;
                double rightOfObs = obs.MaxX + clearanceFeet;
                if (leftOfObs >= safeArea.MinX && leftOfObs <= maxAllowedMinX)
                    candXs.Add(leftOfObs);
                if (rightOfObs >= safeArea.MinX && rightOfObs <= maxAllowedMinX)
                    candXs.Add(rightOfObs);
            }

            var candYs = new List<double> { safeArea.MinY, maxAllowedMinY };
            for (double y = safeArea.MinY; y <= maxAllowedMinY; y += step)
            {
                candYs.Add(y);
            }
            foreach (Rect2D obs in obstacles)
            {
                double belowObs = obs.MinY - clearanceFeet - reqHeight;
                double aboveObs = obs.MaxY + clearanceFeet;
                if (belowObs >= safeArea.MinY && belowObs <= maxAllowedMinY)
                    candYs.Add(belowObs);
                if (aboveObs >= safeArea.MinY && aboveObs <= maxAllowedMinY)
                    candYs.Add(aboveObs);
            }

            // 去重並排序
            List<double> uniqueXs = candXs
                .Select(v => Math.Round(v, 4))
                .Distinct()
                .Where(v => v >= safeArea.MinX - 1e-5 && v <= maxAllowedMinX + 1e-5)
                .ToList();

            List<double> uniqueYs = candYs
                .Select(v => Math.Round(v, 4))
                .Distinct()
                .Where(v => v >= safeArea.MinY - 1e-5 && v <= maxAllowedMinY + 1e-5)
                .ToList();

            double rightBandThreshold = 180.0 * mmToFeet;
            double bottomBandThreshold = 140.0 * mmToFeet;

            bool foundAny = false;
            int bestTier = int.MaxValue;
            double bestOverlap = double.MaxValue;
            double bestScore = double.MaxValue;
            bestRect = new Rect2D(maxAllowedMinX, safeArea.MinY, maxAllowedMinX + reqWidth, safeArea.MinY + reqHeight);
            placementZone = "圖框內右下/右側空白區";

            foreach (double cx in uniqueXs)
            {
                foreach (double cy in uniqueYs)
                {
                    var cand = new Rect2D(cx, cy, cx + reqWidth, cy + reqHeight);
                    if (!safeArea.Contains(cand))
                        continue;

                    double totalOverlap = 0.0;
                    bool hasCollision = false;

                    for (int i = 0; i < obstacles.Count; i++)
                    {
                        if (cand.Intersects(obstacles[i], clearanceFeet))
                        {
                            hasCollision = true;
                            if (requireZeroOverlap)
                                break;
                            totalOverlap += cand.OverlapArea(obstacles[i], clearanceFeet);
                        }
                    }

                    if (requireZeroOverlap && hasCollision)
                        continue;

                    double distFromRight = safeArea.MaxX - cand.MaxX;
                    double distFromBottom = cand.MinY - safeArea.MinY;
                    double distFromLeft = cand.MinX - safeArea.MinX;
                    double distFromTop = safeArea.MaxY - cand.MaxY;

                    int tier;
                    double score;
                    string zone;

                    if (distFromRight <= rightBandThreshold)
                    {
                        // 第一優先：圖框內右下／右側空白區（愈靠右、愈靠下愈優先）
                        tier = 1;
                        score = distFromRight * 2.5 + distFromBottom;
                        zone = "圖框內右下/右側空白區";
                    }
                    else if (distFromBottom <= bottomBandThreshold)
                    {
                        // 第二優先：圖框內下方空白帶（愈靠下、愈靠右愈優先）
                        tier = 2;
                        score = distFromBottom * 2.5 + distFromRight;
                        zone = "圖框內下方空白帶";
                    }
                    else
                    {
                        // 第三優先：圖框內左側／上方空白區（優先貼齊左緣或上緣整齊排列）
                        tier = 3;
                        double edgeSnapDist = Math.Min(distFromLeft, distFromTop);
                        score = edgeSnapDist * 2.0 + distFromBottom * 0.5 + distFromLeft * 0.5;
                        zone = "圖框內左側/上方空白區";
                    }

                    bool isBetter = false;
                    if (!foundAny)
                    {
                        isBetter = true;
                    }
                    else if (!requireZeroOverlap && Math.Abs(totalOverlap - bestOverlap) > 1e-5)
                    {
                        isBetter = totalOverlap < bestOverlap;
                    }
                    else if (tier != bestTier)
                    {
                        isBetter = tier < bestTier;
                    }
                    else if (score < bestScore - 1e-5)
                    {
                        isBetter = true;
                    }

                    if (isBetter)
                    {
                        foundAny = true;
                        bestOverlap = totalOverlap;
                        bestTier = tier;
                        bestScore = score;
                        bestRect = cand;
                        placementZone = requireZeroOverlap ? zone : $"{zone}（最小重疊備援）";
                    }
                }
            }

            return foundAny && (requireZeroOverlap || bestOverlap <= 1e-5);
        }

        private static void SetScheduleNameSafely(Document doc, ViewSchedule schedule, string baseName)
        {
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.Id != schedule.Id)
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existingNames.Contains(baseName))
            {
                schedule.Name = baseName;
                return;
            }

            int suffix = 2;
            while (existingNames.Contains($"{baseName} ({suffix})"))
            {
                suffix++;
            }
            schedule.Name = $"{baseName} ({suffix})";
        }

        private static void AddBuiltInField(
            Document doc,
            ViewSchedule schedule,
            IList<SchedulableField> schedulableFields,
            BuiltInParameter bip,
            string[] fallbackNames,
            List<string> addedFieldNames)
        {
            ElementId targetParamId = new ElementId(bip);
            SchedulableField? matchedField = schedulableFields
                .FirstOrDefault(sf => sf.ParameterId == targetParamId);

            if (matchedField == null && fallbackNames != null && fallbackNames.Length > 0)
            {
                matchedField = schedulableFields.FirstOrDefault(sf =>
                {
                    string fieldName = sf.GetName(doc) ?? string.Empty;
                    return fallbackNames.Any(n => string.Equals(fieldName, n, StringComparison.OrdinalIgnoreCase));
                });
            }

            if (matchedField != null)
            {
                ScheduleField field = schedule.Definition.AddField(matchedField);
                addedFieldNames.Add(field.GetName());
            }
        }

        private static void AddSharedParameterField(
            Document doc,
            ViewSchedule schedule,
            IList<SchedulableField> schedulableFields,
            Guid paramGuid,
            string paramName,
            List<string> addedFieldNames)
        {
            SharedParameterElement? sharedParamElem = SharedParameterElement.Lookup(doc, paramGuid);
            SchedulableField? matchedField = null;

            if (sharedParamElem != null)
            {
                matchedField = schedulableFields
                    .FirstOrDefault(sf => sf.ParameterId == sharedParamElem.Id);
            }

            if (matchedField == null)
            {
                matchedField = schedulableFields.FirstOrDefault(sf =>
                    string.Equals(sf.GetName(doc), paramName, StringComparison.OrdinalIgnoreCase));
            }

            if (matchedField == null)
            {
                throw new InvalidOperationException($"在房間明細表可排程欄位中找不到共用參數: {paramName} ({paramGuid})");
            }

            ScheduleField field = schedule.Definition.AddField(matchedField);
            addedFieldNames.Add(field.GetName());
        }
    }
}
