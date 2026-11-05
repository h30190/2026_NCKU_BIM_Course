using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using ShaftChecker.Core.Config;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Services
{
    /// <summary>
    /// 一鍵建立 T1–T6 測試案例：一條走廊、六個管道間、一個樓梯間、一根柱。
    /// 所有建立的元件在「註解」參數標記 ShaftChecker_TestModel，重新建立時會先刪除舊的。
    ///
    ///  平面配置（單位 mm，牆中心線；W×D 為管道間淨尺寸）
    ///
    ///                 ┌──樓梯間──┐
    ///   ┌──┐  ┌──┐  ┌──┐  ┌──┐  ┌──┐  ┌──┐
    ///   │T1│  │T2│  │T3│  │T4│  │T5│  │T6│        y = 0 以上：管道間
    ///   ══D════D═════D═════════D═════D═══         y = 0：走廊北牆（維修門在此）
    ///                                     ■       T6 門外的柱
    ///   ────────────── 走廊 ──────────────         y = -2200：走廊南牆
    /// </summary>
    public static class TestModelBuilder
    {
        public const string Marker = "ShaftChecker_TestModel";

        private const string WallType1Hr = "SC_防火牆 1hr";
        private const string WallTypeBad = "SC_隔間牆 0.5hr";
        private const string DoorTypeName = "SC_維修門 800x2000 1hr";
        private const string ColumnName = "SC_測試柱 400x400";

        private const double Pitch = 3000;      // 各管道間的間距
        private const double ClearW = 1200;     // 管道間淨寬（東西向）
        private const double ClearD = 800;      // 管道間淨深（南北向）
        private const double NarrowD = 450;     // T5 的淨深
        private const double CorridorD = 2200;  // 走廊南牆中心線位置
        private const double StairD = 2500;     // T4 北側樓梯間深度
        private const double WallH = 3000;

        private class Case
        {
            public string Id;
            public string Title;
            public string Expect;
            public double Depth = ClearD;
            public bool BadTopWall;
            public bool NoSmokeSeal;
            public bool DoorToStair;
            public bool ColumnInFront;
        }

        private static readonly Case[] Cases =
        {
            new Case { Id = "T1", Title = "合格", Expect = "全部合格" },
            new Case { Id = "T2", Title = "牆防火不足", Expect = "A1 違規", BadTopWall = true },
            new Case { Id = "T3", Title = "門無遮煙", Expect = "A2 違規", NoSmokeSeal = true },
            new Case { Id = "T4", Title = "門開向樓梯間", Expect = "A4 違規", DoorToStair = true },
            new Case { Id = "T5", Title = "淨寬不足", Expect = "A6 違規", Depth = NarrowD },
            new Case { Id = "T6", Title = "門前有柱", Expect = "A8 違規", ColumnInFront = true }
        };

        public static Dictionary<string, object> Build(UIApplication uiapp)
        {
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null) throw new InvalidOperationException("Revit 沒有開啟中的專案。");
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument) throw new InvalidOperationException("請在專案（不是族群）中執行。");

            RuleConfig cfg = AddinPaths.LoadConfig();

            // 1. 確保門有「遮煙性能」參數、Room 有結果欄位（自己開 Transaction）
            ParameterSetup.Ensure(uiapp.Application, doc, cfg);

            Level level = PickLevel(doc);
            if (level == null) throw new InvalidOperationException("專案中沒有樓層。");

            var created = new List<ElementId>();
            var caseRows = new List<object>();
            var notes = new List<string>();
            int deleted;

            using (var t = new Transaction(doc, "管道間檢討：建立 T1–T6 測試模型"))
            {
                t.Start();
                var fh = t.GetFailureHandlingOptions();
                fh.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fh);

                deleted = DeleteOld(doc);

                // 2. 類型
                WallType baseWall = PickBaseWallType(doc);
                if (baseWall == null) throw new InvalidOperationException("找不到基本牆類型。");
                WallType wall1Hr = GetOrDuplicate(baseWall, WallType1Hr);
                WallType wallBad = GetOrDuplicate(baseWall, WallTypeBad);
                SetText(wall1Hr, BuiltInParameter.FIRE_RATING, cfg.FireRatingParam, "1小時");
                SetText(wallBad, BuiltInParameter.FIRE_RATING, cfg.FireRatingParam, "0.5小時");

                FamilySymbol baseDoor = PickDoorSymbol(doc);
                if (baseDoor == null) throw new InvalidOperationException("專案中沒有載入任何門族群，請先載入一個單扇門。");
                var doorType = (FamilySymbol)GetOrDuplicate(baseDoor, DoorTypeName);
                SetLength(doorType, 800, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
                SetLength(doorType, 2000, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                SetText(doorType, BuiltInParameter.FIRE_RATING, cfg.FireRatingParam, "1小時");
                if (!doorType.IsActive) doorType.Activate();

                double tw = UnitConv.ToMm(wall1Hr.Width);
                double outerW = ClearW + tw;
                double xStart = -1500;
                double xEnd = (Cases.Length - 1) * Pitch + outerW + 1500;
                double yCorr = -CorridorD;

                // 3. 走廊：北牆（共用，掛維修門）、南牆、兩端牆
                Wall north = MakeWall(doc, level, wall1Hr, xStart, 0, xEnd, 0, created);
                MakeWall(doc, level, wall1Hr, xStart, yCorr, xEnd, yCorr, created);
                MakeWall(doc, level, wall1Hr, xStart, yCorr, xStart, 0, created);
                MakeWall(doc, level, wall1Hr, xEnd, yCorr, xEnd, 0, created);

                // 4. 管道間的牆
                var topWalls = new Dictionary<string, Wall>();
                for (int i = 0; i < Cases.Length; i++)
                {
                    Case c = Cases[i];
                    double x0 = i * Pitch, x1 = x0 + outerW;
                    double yTop = c.Depth + tw;
                    double sideTop = c.DoorToStair ? yTop + StairD : yTop;

                    MakeWall(doc, level, wall1Hr, x0, 0, x0, sideTop, created);
                    MakeWall(doc, level, wall1Hr, x1, 0, x1, sideTop, created);
                    topWalls[c.Id] = MakeWall(doc, level, c.BadTopWall ? wallBad : wall1Hr, x0, yTop, x1, yTop, created);
                    if (c.DoorToStair)
                        MakeWall(doc, level, wall1Hr, x0, sideTop, x1, sideTop, created);
                }

                doc.Regenerate();

                // 5. 維修門、柱
                var doors = new Dictionary<string, FamilyInstance>();
                for (int i = 0; i < Cases.Length; i++)
                {
                    Case c = Cases[i];
                    double xm = i * Pitch + outerW / 2;
                    double yTop = c.Depth + tw;

                    Wall host = c.DoorToStair ? topWalls[c.Id] : north;
                    double yDoor = c.DoorToStair ? yTop : 0;
                    FamilyInstance door = doc.Create.NewFamilyInstance(
                        Pt(xm, yDoor, level), doorType, host, level, StructuralType.NonStructural);
                    Mark(door);
                    SetText(door, BuiltInParameter.ALL_MODEL_MARK, null, "MD-" + c.Id);
                    created.Add(door.Id);
                    doors[c.Id] = door;

                    if (c.ColumnInFront)
                    {
                        // 柱心距走廊北牆牆面 500 mm，400×400，落在門前 750 mm 作業區內
                        double yCol = -tw / 2 - 500;
                        Element col = MakeColumn(doc, level, xm, yCol, 400, WallH, notes);
                        if (col != null) created.Add(col.Id);
                    }
                }

                doc.Regenerate();

                // 6. 遮煙性能（門的實體參數）
                foreach (Case c in Cases)
                {
                    Parameter p = doors[c.Id].LookupParameter(cfg.SmokeSealParam);
                    if (p != null && !p.IsReadOnly) p.Set(c.NoSmokeSeal ? 0 : 1);
                    else notes.Add("門 MD-" + c.Id + " 找不到「" + cfg.SmokeSealParam + "」參數");
                }

                // 7. Room
                var shaftRooms = new Dictionary<string, Room>();
                created.Add(MakeRoom(doc, level, (xStart + 600), yCorr / 2, "C-01", "走廊").Id);
                for (int i = 0; i < Cases.Length; i++)
                {
                    Case c = Cases[i];
                    double xm = i * Pitch + outerW / 2;
                    double yTop = c.Depth + tw;

                    Room room = MakeRoom(doc, level, xm, yTop / 2, "PS-" + c.Id, "PS-" + c.Id + " " + c.Title);
                    created.Add(room.Id);
                    shaftRooms[c.Id] = room;

                    if (c.DoorToStair)
                        created.Add(MakeRoom(doc, level, xm, yTop + StairD / 2, "ST-01", "樓梯間").Id);
                }

                doc.Regenerate();

                foreach (Case c in Cases)
                {
                    Room room = shaftRooms[c.Id];
                    if (room.Area <= 0) notes.Add("PS-" + c.Id + " 的房間沒有封閉（面積為 0），請檢查牆是否接合");
                    caseRows.Add(new Dictionary<string, object>
                    {
                        { "case", c.Id },
                        { "room", room.Name },
                        { "roomId", room.Id.GetIdValue() },
                        { "areaM2", Math.Round(UnitConv.ToM2(room.Area), 2) },
                        { "expect", c.Expect }
                    });
                }

                t.Commit();
            }

            try { uidoc.ShowElements(created); } catch (Exception) { }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "level", level.Name },
                { "deletedOld", deleted },
                { "createdCount", created.Count },
                { "cases", caseRows },
                { "notes", notes },
                { "next", "測試模型已建立。可直接呼叫 check_shafts（或在儀表板按「執行檢討」）驗證 T1–T6 是否符合預期。" }
            };
        }

        // ------------------------------------------------------------------ 建立元件

        private static XYZ Pt(double xMm, double yMm, Level level)
        {
            return new XYZ(UnitConv.FromMm(xMm), UnitConv.FromMm(yMm), level.Elevation);
        }

        private static Wall MakeWall(Document doc, Level level, WallType type,
            double x0, double y0, double x1, double y1, List<ElementId> created)
        {
            Line line = Line.CreateBound(Pt(x0, y0, level), Pt(x1, y1, level));
            Wall w = Wall.Create(doc, line, type.Id, level.Id, UnitConv.FromMm(WallH), 0, false, false);
            Mark(w);
            created.Add(w.Id);
            return w;
        }

        private static Room MakeRoom(Document doc, Level level, double xMm, double yMm, string number, string name)
        {
            Room room = doc.Create.NewRoom(level, new UV(UnitConv.FromMm(xMm), UnitConv.FromMm(yMm)));
            room.Number = number;
            room.Name = name;
            Parameter upper = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
            if (upper != null && !upper.IsReadOnly) upper.Set(UnitConv.FromMm(WallH));
            Mark(room);
            return room;
        }

        /// <summary>
        /// 柱用 DirectShape 建立（類別為「柱」），不依賴專案是否載入柱族群，也不受頂部樓層影響。
        /// </summary>
        private static Element MakeColumn(Document doc, Level level, double xMm, double yMm, double size, double height, List<string> notes)
        {
            double h = size / 2;
            XYZ a = Pt(xMm - h, yMm - h, level), b = Pt(xMm + h, yMm - h, level);
            XYZ c = Pt(xMm + h, yMm + h, level), d = Pt(xMm - h, yMm + h, level);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(a, b));
            loop.Append(Line.CreateBound(b, c));
            loop.Append(Line.CreateBound(c, d));
            loop.Append(Line.CreateBound(d, a));
            Solid box = GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop }, XYZ.BasisZ, UnitConv.FromMm(height));

            var catId = new ElementId(BuiltInCategory.OST_Columns);
            if (!DirectShape.IsValidCategoryId(catId, doc))
            {
                catId = new ElementId(BuiltInCategory.OST_GenericModel);
                notes.Add("柱改用「一般模型」類別建立");
            }

            DirectShape ds = DirectShape.CreateElement(doc, catId);
            ds.SetShape(new List<GeometryObject> { box });
            ds.SetName(ColumnName);
            Mark(ds);
            return ds;
        }

        private static void Mark(Element e)
        {
            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly) p.Set(Marker);
        }

        private static int DeleteOld(Document doc)
        {
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Doors, BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Columns, BuiltInCategory.OST_GenericModel
            };

            var ids = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType()
                .Where(e =>
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (e is DirectShape && e.Name == ColumnName) return true;
                    return p != null && p.AsString() == Marker;
                })
                .Select(e => e.Id)
                .ToList();

            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }

        // ------------------------------------------------------------------ 選類型、樓層

        private static Level PickLevel(Document doc)
        {
            var plan = doc.ActiveView as ViewPlan;
            if (plan != null && plan.GenLevel != null) return plan.GenLevel;
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).FirstOrDefault();
        }

        private static WallType PickBaseWallType(Document doc)
        {
            var basics = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(w => w.Kind == WallKind.Basic && !w.Name.StartsWith("SC_"))
                .ToList();
            if (basics.Count == 0) return null;

            // 優先選厚度 150–250 mm 的牆，比較像管道間隔間牆
            return basics
                .OrderBy(w => Math.Abs(UnitConv.ToMm(w.Width) - 200))
                .First();
        }

        private static FamilySymbol PickDoorSymbol(Document doc)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => !s.Name.StartsWith("SC_"))
                .ToList();
            if (symbols.Count == 0) return null;

            string[] avoid = { "雙", "double", "garage", "車庫", "幕牆", "curtain", "滑", "slid", "折", "fold", "旋轉", "revolv" };
            string[] prefer = { "單", "single" };

            Func<FamilySymbol, string> key = s => ((s.FamilyName ?? "") + " " + s.Name).ToLowerInvariant();

            return symbols.FirstOrDefault(s => prefer.Any(k => key(s).Contains(k)) && !avoid.Any(k => key(s).Contains(k)))
                ?? symbols.FirstOrDefault(s => !avoid.Any(k => key(s).Contains(k)))
                ?? symbols.First();
        }

        private static ElementType GetOrDuplicate(ElementType source, string name)
        {
            Document doc = source.Document;
            ElementType existing = new FilteredElementCollector(doc)
                .OfClass(source.GetType())
                .Cast<ElementType>()
                .FirstOrDefault(t => t.Name == name && t.Category != null && source.Category != null &&
                                     t.Category.Id.Equals(source.Category.Id));
            return existing ?? source.Duplicate(name);
        }

        private static WallType GetOrDuplicate(WallType source, string name)
        {
            return (WallType)GetOrDuplicate((ElementType)source, name);
        }

        // ------------------------------------------------------------------ 參數

        private static void SetText(Element e, BuiltInParameter bip, string altName, string value)
        {
            Parameter p = e.get_Parameter(bip);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);

            if (!string.IsNullOrEmpty(altName))
            {
                Parameter alt = e.LookupParameter(altName);
                if (alt != null && !alt.IsReadOnly && alt.StorageType == StorageType.String) alt.Set(value);
            }
        }

        private static void SetLength(Element e, double mm, params BuiltInParameter[] bips)
        {
            foreach (var bip in bips)
            {
                Parameter p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                {
                    p.Set(UnitConv.FromMm(mm));
                    return;
                }
            }
        }

        /// <summary>吞掉建立過程中的警告（例如牆重疊、房間未封閉），避免跳出對話框卡住 HTTP 請求。</summary>
        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (FailureMessageAccessor f in fa.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
