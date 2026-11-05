using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Rules;
using Xunit;

namespace ShaftChecker.Tests
{
    /// <summary>
    /// 新增規則的測試。管道間 1200 × 800（x 0–1200、y 0–800），本層樓板 0、上一層樓板 3200；
    /// 維修門在 y = 0 的牆上（牆厚 150，牆中心線 y = −75），門寬 700、高 2100，朝 +y 進入管道間。
    /// </summary>
    public class NewRuleTests
    {
        private readonly RuleConfig _cfg = new RuleConfig();

        private static ShaftInfo Shaft()
        {
            var s = new ShaftInfo
            {
                RoomId = 1, Number = "PS-1", Name = "管道間", LevelName = "3F",
                BoundaryMm = GeometryTests.Rect(1200, 800), AreaM2 = 0.96,
                FloorZ = 0, CeilingZ = 3200
            };
            s.Walls.Add(new WallInfo { Id = 10, TypeName = "RC 15cm", FireRatingRaw = "2 hr" });
            s.Doors.Add(new DoorInfo
            {
                Id = 20, TypeName = "防火門 700x2100", FireRatingRaw = "1 hr", SmokeSeal = true,
                WidthMm = 700, HeightMm = 2100, OtherSideRoomName = "走廊", FrontCheckDone = true,
                HasGeometry = true, CenterX = 600, CenterY = -75, BaseZ = 0,
                AlongX = 1, AlongY = 0, IntoX = 0, IntoY = 1, WallThicknessMm = 150
            });
            return s;
        }

        private static MepInfo Riser(long id, double x, double y, double dia, MepKind kind = MepKind.Pipe,
            MepGroup group = MepGroup.Plumbing, double ins = 0)
        {
            return new MepInfo
            {
                Id = id, Kind = kind, Group = group, X1 = x, Y1 = y, Z1 = 0, X2 = x, Y2 = y, Z2 = 3200,
                OuterDiameterMm = dia, InsulationMm = ins
            };
        }

        private static MepInfo Horizontal(long id, double x1, double y1, double x2, double y2, double z, double dia,
            MepKind kind = MepKind.Pipe, MepGroup group = MepGroup.Plumbing)
        {
            return new MepInfo
            {
                Id = id, Kind = kind, Group = group, X1 = x1, Y1 = y1, Z1 = z, X2 = x2, Y2 = y2, Z2 = z,
                OuterDiameterMm = dia
            };
        }

        /// <summary>沿 y = 700 的梁，寬 400（y 500–900），梁底 2500、梁頂 3200：侵入管道間 300 mm。</summary>
        private static BeamInfo BeamInside()
        {
            return new BeamInfo { Id = 30, Name = "B1 40x70", X1 = -500, Y1 = 700, X2 = 1700, Y2 = 700, WidthMm = 400, BottomZ = 2500, TopZ = 3200 };
        }

        // ------------------------------------------------------------------ 原本的合格案例仍合格

        [Fact]
        public void 沒有機電與結構資料_新規則全部合格()
        {
            var result = new RuleEngine().Run(new[] { Shaft() }, _cfg);
            Assert.Equal(Severity.Pass, result.Shafts[0].Overall);
        }

        // ------------------------------------------------------------------ 管線分類

        [Theory]
        [InlineData("家庭用冷水", MepGroup.Plumbing)]
        [InlineData("Sanitary 污水", MepGroup.Plumbing)]
        [InlineData("瓦斯管", MepGroup.Gas)]
        [InlineData("Natural Gas", MepGroup.Gas)]
        [InlineData("濕式消防系統", MepGroup.FireProtection)]
        [InlineData("Hydronic Supply", MepGroup.HvacWater)]
        [InlineData("", MepGroup.OtherPipe)]
        public void 水管依系統文字分類(string text, MepGroup expected)
        {
            Assert.Equal(expected, MepClassifier.Classify(MepKind.Pipe, text, _cfg));
        }

        [Fact]
        public void 電管與電纜架為電氣類_風管為風管類()
        {
            Assert.Equal(MepGroup.Electrical, MepClassifier.Classify(MepKind.Conduit, "給水", _cfg));
            Assert.Equal(MepGroup.Electrical, MepClassifier.Classify(MepKind.CableTray, null, _cfg));
            Assert.Equal(MepGroup.Air, MepClassifier.Classify(MepKind.Duct, "排煙", _cfg));
        }

        // ------------------------------------------------------------------ 防火區劃

        [Fact]
        public void A4_名稱不像樓梯但空間類型為排煙室_違規()
        {
            var s = Shaft();
            s.Doors[0].OtherSideRoomName = "前室";
            s.Doors[0].OtherSideRoomType = "排煙室";
            var issue = new A4_DoorNotToStair().Check(s, _cfg).Single();
            Assert.Equal(Severity.Violation, issue.Severity);
            Assert.Contains("空間類型", issue.Message);
        }

        [Fact]
        public void A4_走廊_合格()
        {
            Assert.Empty(new A4_DoorNotToStair().Check(Shaft(), _cfg));
        }

        [Fact]
        public void 地下層_A1A2加註地下建築物條文()
        {
            var s = Shaft();
            s.IsUnderground = true;
            s.Walls[0].FireRatingRaw = "0.5 hr";
            var issue = new A1_WallFireRating().Check(s, _cfg).First();
            Assert.Contains("第203條", issue.Reference);

            var result = new RuleEngine().Run(new[] { s }, _cfg);
            var a2Pass = result.Shafts[0].Issues.Single(i => i.RuleId == "A2");
            Assert.Contains("第203條", a2Pass.Reference);
            Assert.DoesNotContain("第203條", result.Shafts[0].Issues.Single(i => i.RuleId == "A6").Reference);
        }

        // ------------------------------------------------------------------ 維修門

        [Fact]
        public void A7_沒有淨寬參數_以寬度扣兩側門框推算()
        {
            var s = Shaft();
            s.Doors[0].FrameWidthMm = 60; // 700 − 2 × 60 = 580
            var issue = new A7_DoorSize().Check(s, _cfg).First();
            Assert.Equal(580, issue.Actual.Value, 3);
            Assert.Contains("門框", issue.Message);
        }

        [Fact]
        public void A7_淨高參數不足_違規()
        {
            var s = Shaft();
            s.Doors[0].ClearHeightMm = 1700;
            var issue = new A7_DoorSize().Check(s, _cfg).Single();
            Assert.Equal(1700, issue.Actual.Value, 3);
        }

        [Fact]
        public void A6_淨深門檻_長邊不足_違規()
        {
            var cfg = _cfg.Clone();
            cfg.MinClearDepthMm = 1500;
            var issue = new A6_ClearSize().Check(Shaft(), cfg).Single();
            Assert.Equal(1200, issue.Actual.Value, 3);
        }

        [Fact]
        public void A9_門頂上方梁底太低_違規()
        {
            var s = Shaft();
            s.Beams.Add(new BeamInfo { Id = 31, Name = "B2", X1 = -500, Y1 = -75, X2 = 1700, Y2 = -75, WidthMm = 400, BottomZ = 2000, TopZ = 2700 });
            var issue = new A9_DoorHeadBeam().Check(s, _cfg).Single();
            Assert.Equal(2000, issue.Actual.Value, 3);
            Assert.Equal(2150, issue.Required.Value, 3);
            Assert.Contains(31L, issue.ElementIds);
        }

        [Fact]
        public void A9_梁底夠高_合格()
        {
            var s = Shaft();
            s.Beams.Add(new BeamInfo { Id = 31, Name = "B2", X1 = -500, Y1 = -75, X2 = 1700, Y2 = -75, WidthMm = 400, BottomZ = 2500, TopZ = 3200 });
            Assert.Empty(new A9_DoorHeadBeam().Check(s, _cfg));
        }

        [Fact]
        public void A9_找不到任何結構模型_警告()
        {
            var s = Shaft();
            s.StructureModelFound = false;
            Assert.Equal(Severity.Warning, new A9_DoorHeadBeam().Check(s, _cfg).Single().Severity);
        }

        // ------------------------------------------------------------------ 管線

        [Fact]
        public void P1_電管與水管同管道間無隔牆_違規_加隔牆後合格()
        {
            var s = Shaft();
            s.Mep.Add(Riser(101, 200, 400, 50, MepKind.Conduit, MepGroup.Electrical));
            s.Mep.Add(Riser(102, 1000, 400, 100));
            Assert.Equal(Severity.Violation, new P1_WaterElectricSeparation().Check(s, _cfg).Single().Severity);

            s.InnerWalls.Add(new InnerWallInfo { Id = 40, X1 = 600, Y1 = 0, X2 = 600, Y2 = 800, ThicknessMm = 100, BottomZ = 0, TopZ = 3200 });
            Assert.Empty(new P1_WaterElectricSeparation().Check(s, _cfg));
        }

        [Fact]
        public void P2_淨距扣除半徑與保溫_不足違規()
        {
            var s = Shaft();
            s.Mep.Add(Riser(101, 300, 400, 100, ins: 25));
            s.Mep.Add(Riser(102, 420, 400, 100, ins: 25)); // 120 − 75 − 75 = −30（重疊）
            s.Mep.Add(Riser(103, 900, 400, 100, ins: 25)); // 與 102 相距 480 − 150 = 330
            var issue = new P2_PipeClearance().Check(s, _cfg).Single();
            Assert.Equal(-30, issue.Actual.Value, 3);
            Assert.Contains("重疊", issue.Message);
        }

        [Fact]
        public void P3_立管在維修門正後方_違規()
        {
            var s = Shaft();
            s.Mep.Add(Riser(101, 600, 300, 100));
            var issue = new P3_DoorBackClearance().Check(s, _cfg).Single();
            Assert.Contains(101L, issue.ElementIds);

            var s2 = Shaft();
            s2.Mep.Add(Riser(102, 100, 700, 100));
            Assert.Empty(new P3_DoorBackClearance().Check(s2, _cfg));
        }

        [Fact]
        public void P4_高層建築瓦斯管與電管同管道間_違規_與給排水同管道間合格()
        {
            var s = Shaft();
            s.IsHighRise = true;
            s.Mep.Add(Riser(101, 300, 400, 50, MepKind.Pipe, MepGroup.Gas));
            s.Mep.Add(Riser(102, 900, 400, 100, MepKind.Pipe, MepGroup.Plumbing));
            Assert.Empty(new P4_GasShaft().Check(s, _cfg));

            s.Mep.Add(Riser(103, 600, 600, 40, MepKind.Conduit, MepGroup.Electrical));
            Assert.Equal(Severity.Violation, new P4_GasShaft().Check(s, _cfg).Single().Severity);

            s.IsHighRise = false;
            Assert.Empty(new P4_GasShaft().Check(s, _cfg));
        }

        [Fact]
        public void P4_設定瓦斯管道間完全單獨_與給排水同管道間也違規()
        {
            var s = Shaft();
            s.IsHighRise = true;
            s.Mep.Add(Riser(101, 300, 400, 50, MepKind.Pipe, MepGroup.Gas));
            s.Mep.Add(Riser(102, 900, 400, 100, MepKind.Pipe, MepGroup.Plumbing));
            var cfg = _cfg.Clone();
            cfg.GasAllowedGroups = new List<string>();
            Assert.Single(new P4_GasShaft().Check(s, cfg));
        }

        [Fact]
        public void P5_風管穿出管道間牆_附近沒有防火閘門_違規_加閘門後合格()
        {
            var s = Shaft();
            s.Mep.Add(new MepInfo
            {
                Id = 201, Kind = MepKind.Duct, Group = MepGroup.Air,
                X1 = 600, Y1 = 400, Z1 = 2800, X2 = 600, Y2 = -1500, Z2 = 2800, WidthMm = 400, HeightMm = 300
            });
            Assert.Equal(Severity.Violation, new P5_FireDamper().Check(s, _cfg).Single().Severity);

            s.Accessories.Add(new AccessoryInfo { Id = 301, Name = "防火閘門 400x300", X = 600, Y = -300, Z = 2800, IsFireDamper = true });
            Assert.Empty(new P5_FireDamper().Check(s, _cfg));
        }

        // ------------------------------------------------------------------ 結構

        [Fact]
        public void S1_梁穿越管道間_計算遮擋面積淨高與移動建議()
        {
            var s = Shaft();
            s.Beams.Add(BeamInside());
            var rep = StructureAnalysis.Analyze(s, _cfg);

            var m = rep.Blocking.Single();
            Assert.Equal(0.36, m.OverlapAreaM2, 3);
            Assert.Equal(300, m.OverlapDepthMm, 1);
            Assert.Equal(2500, m.ClearHeightMm.Value, 3);
            Assert.InRange(rep.BlockedAreaM2, 0.33, 0.39);
            Assert.InRange(rep.FreeWidthMm, 470, 510);

            // 往下（−Y）移 280 mm：侵入只剩 20 mm（容許值）
            var best = rep.ShaftMoves.First();
            Assert.Equal(280, best.DistanceMm, 3);
            Assert.Equal("往下（−Y）", best.Direction);

            var issues = new S1_ShaftStructure().Check(s, _cfg).ToList();
            Assert.Equal(2, issues.Count); // 梁侵入 + 扣除後淨寬不足
            Assert.Contains("往下（−Y） 280 mm", issues[0].Suggestion);
        }

        [Fact]
        public void S1_侵入小於容許值_視為建模誤差不違規()
        {
            var s = Shaft();
            // 梁寬 400，中心 y = 990：侵入 y 790–800 只有 10 mm
            s.Beams.Add(new BeamInfo { Id = 32, Name = "B3", X1 = -500, Y1 = 990, X2 = 1700, Y2 = 990, WidthMm = 400, BottomZ = 2500, TopZ = 3200 });
            Assert.Empty(new S1_ShaftStructure().Check(s, _cfg));
        }

        [Fact]
        public void S1_上一層的梁不算本層()
        {
            var s = Shaft();
            var b = BeamInside();
            b.BottomZ = 5700; b.TopZ = 6400;
            s.Beams.Add(b);
            Assert.Empty(new S1_ShaftStructure().Check(s, _cfg));
        }

        [Fact]
        public void S1_立管穿過梁_建議在管道間內移動()
        {
            var s = Shaft();
            s.Beams.Add(BeamInside());
            s.Mep.Add(Riser(101, 600, 650, 100));
            var rep = StructureAnalysis.Analyze(s, _cfg);
            var r = rep.Risers.Single();
            Assert.Equal(30L, r.MemberId);
            Assert.NotNull(r.Move);
            Assert.Equal("往下（−Y）", r.Move.Direction);
            Assert.Equal(250, r.Move.DistanceMm, 3); // 650 → 400：梁邊 500 − 半徑 50 − 淨距 50
        }

        [Fact]
        public void S1_找不到結構模型_警告()
        {
            var s = Shaft();
            s.StructureModelFound = false;
            Assert.Equal(Severity.Warning, new S1_ShaftStructure().Check(s, _cfg).Single().Severity);
        }

        /// <summary>梁軸 (0, 2000)–(6000, 2000)，寬 300，梁底 2300、梁頂 3000（梁深 700）。</summary>
        private static ShaftInfo ShaftWithLongBeam()
        {
            var s = Shaft();
            s.Beams.Add(new BeamInfo { Id = 33, Name = "G1 30x70", X1 = 0, Y1 = 2000, X2 = 6000, Y2 = 2000, WidthMm = 300, BottomZ = 2300, TopZ = 3000 });
            return s;
        }

        [Fact]
        public void S2_穿梁位置孔徑都符合_不列出()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(401, 3000, 1000, 3000, 3000, 2650, 100));
            Assert.Empty(BeamPenetration.Analyze(s, _cfg));
        }

        [Fact]
        public void S2_孔中心偏離梁深中央_違規並建議移動()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(402, 3000, 1000, 3000, 3000, 2900, 100));
            var h = BeamPenetration.Analyze(s, _cfg).Single();
            Assert.True(h.Through);
            Assert.Contains("中央", h.Problems.Single());
            Assert.Contains("下移", h.Suggestion);
        }

        [Fact]
        public void S2_太靠近梁端_違規()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(403, 500, 1000, 500, 3000, 2650, 100));
            var h = BeamPenetration.Analyze(s, _cfg).Single();
            Assert.Equal(500, h.SupportDistMm, 1);
            Assert.Contains("支承面", h.Problems.Single());
        }

        [Fact]
        public void S2_孔徑超過梁深三分之一_違規()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(404, 3000, 1000, 3000, 3000, 2650, 300)); // 300 > 233
            Assert.Contains(BeamPenetration.Analyze(s, _cfg).Single().Problems, p => p.Contains("孔徑"));
        }

        [Fact]
        public void S2_管線局部撞梁_建議下移()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(405, 3000, 1000, 3000, 3000, 2280, 100)); // 管頂 2330 > 梁底 2300
            var h = BeamPenetration.Analyze(s, _cfg).Single();
            Assert.False(h.Through);
            Assert.Contains("下移至少 80 mm", h.Suggestion);
        }

        [Fact]
        public void S2_管線從梁下通過_不列出()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(406, 3000, 1000, 3000, 3000, 2200, 100)); // 管頂 2250 < 梁底 2300
            Assert.Empty(BeamPenetration.Analyze(s, _cfg));
        }

        [Fact]
        public void S2_相鄰兩孔太近_違規()
        {
            var s = ShaftWithLongBeam();
            s.Mep.Add(Horizontal(407, 3000, 1000, 3000, 3000, 2650, 100));
            s.Mep.Add(Horizontal(408, 3150, 1000, 3150, 3000, 2650, 100)); // 中心距 150 < 3 × 100
            var h = BeamPenetration.Analyze(s, _cfg).Single();
            Assert.Contains("相鄰孔", h.Problems.Single());
        }

        [Fact]
        public void S2_支承面量到柱面()
        {
            var s = ShaftWithLongBeam();
            s.Columns.Add(new ColumnInfo
            {
                Id = 50, Name = "C1",
                FootprintMm = new List<Point2D> { new Point2D(-400, 1600), new Point2D(400, 1600), new Point2D(400, 2400), new Point2D(-400, 2400) },
                BottomZ = 0, TopZ = 3200
            });
            Assert.Equal(1600, BeamPenetration.SupportDistance(s.Beams[0], 2000, s), 3); // 2000 − 柱面 400
        }

        // ------------------------------------------------------------------ 幾何工具

        [Fact]
        public void 凸多邊形裁切_矩形交集面積()
        {
            var a = GeometryTests.Rect(1200, 800);
            var b = PolygonUtils.RectangleFromAxis(new Point2D(-500, 700), new Point2D(1700, 700), 400);
            Assert.Equal(1200 * 300, PolygonUtils.Area(PolygonUtils.ClipByConvex(a, b)), 3);
        }

        [Fact]
        public void 方向文字()
        {
            Assert.Equal("往右（+X）", StructureAnalysis.DirectionText(1, 0));
            Assert.Equal("往下（−Y）", StructureAnalysis.DirectionText(0, -1));
            Assert.Equal("往左上（135°）", StructureAnalysis.DirectionText(-1, 1));
        }

        [Fact]
        public void 新門檻可被假設分析覆寫()
        {
            var cfg = _cfg.Clone();
            Assert.True(cfg.ApplyOverride("minPipeClearMm", 100));
            Assert.True(cfg.ApplyOverride("beamHoleMaxRatio", 0.25));
            Assert.Equal(100, cfg.MinPipeClearMm);
            Assert.Equal(50, _cfg.MinPipeClearMm);
        }

        // ------------------------------------------------------------------ 選取用的元件參照

        [Fact]
        public void 連結模型的管道間_問題元件參照帶連結名稱_不放進上色清單()
        {
            var s = Shaft();
            s.SourceName = "建築.rvt";
            s.Beams.Add(new BeamInfo { Id = 31, Source = "結構.rvt", Name = "B2", X1 = -500, Y1 = -75, X2 = 1700, Y2 = -75, WidthMm = 400, BottomZ = 2000, TopZ = 2700 });
            var issue = new A9_DoorHeadBeam().Check(s, _cfg).Single();
            Assert.Contains(issue.Refs, r => r.Id == 20 && r.Source == "建築.rvt");
            Assert.Contains(issue.Refs, r => r.Id == 31 && r.Source == "結構.rvt");
            Assert.Empty(issue.ElementIds);
        }

        [Fact]
        public void 主模型的管道間_一般規則的元件參照沿用管道間所在模型()
        {
            var s = Shaft();
            s.Doors[0].SmokeSeal = false;
            var issue = new A2_DoorFireSmoke().Check(s, _cfg).First();
            Assert.Contains(issue.Refs, r => r.Id == 20 && r.Source == null);
        }
    }
}
