using System;
using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Rules;
using Xunit;

namespace ShaftChecker.Tests
{
    public class GeometryTests
    {
        [Fact]
        public void 矩形管道間_淨寬為短邊()
        {
            var r = PolygonUtils.MinAreaRectangle(Rect(1200, 800));
            Assert.Equal(800, r.ShortSide, 3);
            Assert.Equal(1200, r.LongSide, 3);
        }

        [Fact]
        public void 旋轉30度的矩形_淨寬不變()
        {
            var pts = Rect(1200, 800).Select(p => Rotate(p, 30)).ToList();
            var r = PolygonUtils.MinAreaRectangle(pts);
            Assert.Equal(800, r.ShortSide, 3);
            Assert.Equal(1200, r.LongSide, 3);
        }

        [Fact]
        public void 面積計算()
        {
            Assert.Equal(1200.0 * 800.0, PolygonUtils.Area(Rect(1200, 800)), 3);
        }

        internal static List<Point2D> Rect(double w, double h)
        {
            return new List<Point2D> { new Point2D(0, 0), new Point2D(w, 0), new Point2D(w, h), new Point2D(0, h) };
        }

        private static Point2D Rotate(Point2D p, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point2D(p.X * Math.Cos(a) - p.Y * Math.Sin(a), p.X * Math.Sin(a) + p.Y * Math.Cos(a));
        }
    }

    public class FireRatingParserTests
    {
        [Theory]
        [InlineData("1", 1.0)]
        [InlineData("1 hr", 1.0)]
        [InlineData("1小時", 1.0)]
        [InlineData("2h", 2.0)]
        [InlineData("60 min", 1.0)]
        [InlineData("30分", 0.5)]
        [InlineData("一小時", 1.0)]
        public void 可解析常見寫法(string raw, double expected)
        {
            Assert.Equal(expected, FireRatingParser.ParseHours(raw).Value, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("無")]
        public void 無法解析回傳null(string raw)
        {
            Assert.Null(FireRatingParser.ParseHours(raw));
        }
    }

    public class RuleTests
    {
        private readonly RuleConfig _cfg = new RuleConfig();

        private static ShaftInfo GoodShaft()
        {
            var s = new ShaftInfo
            {
                RoomId = 1, Number = "PS-1", Name = "管道間", LevelName = "1F",
                BoundaryMm = GeometryTests.Rect(1200, 800), AreaM2 = 0.96
            };
            s.Walls.Add(new WallInfo { Id = 10, TypeName = "RC 15cm", FireRatingRaw = "2 hr" });
            s.Doors.Add(new DoorInfo
            {
                Id = 20, TypeName = "防火門 700x2100", FireRatingRaw = "1 hr", SmokeSeal = true,
                WidthMm = 700, HeightMm = 2100, OtherSideRoomName = "走廊", FrontCheckDone = true
            });
            return s;
        }

        [Fact]
        public void 合格管道間_全部規則合格()
        {
            var result = new RuleEngine().Run(new[] { GoodShaft() }, _cfg);
            Assert.Equal(Severity.Pass, result.Shafts[0].Overall);
        }

        [Fact]
        public void T2_牆防火時效不足_A1違規()
        {
            var s = GoodShaft();
            s.Walls[0].FireRatingRaw = "0.5 hr";
            Assert.Contains(new A1_WallFireRating().Check(s, _cfg), i => i.Severity == Severity.Violation);
        }

        [Fact]
        public void 牆未填防火時效_A1資料不足()
        {
            var s = GoodShaft();
            s.Walls[0].FireRatingRaw = null;
            Assert.Contains(new A1_WallFireRating().Check(s, _cfg), i => i.Severity == Severity.DataMissing);
        }

        [Fact]
        public void T3_維修門缺遮煙_A2違規()
        {
            var s = GoodShaft();
            s.Doors[0].SmokeSeal = false;
            Assert.Contains(new A2_DoorFireSmoke().Check(s, _cfg), i => i.Severity == Severity.Violation);
        }

        [Fact]
        public void T4_門開向樓梯間_A4違規()
        {
            var s = GoodShaft();
            s.Doors[0].OtherSideRoomName = "特別安全梯 樓梯間";
            Assert.Contains(new A4_DoorNotToStair().Check(s, _cfg), i => i.Severity == Severity.Violation);
        }

        [Fact]
        public void T5_淨寬不足_A6違規()
        {
            var s = GoodShaft();
            s.BoundaryMm = GeometryTests.Rect(1200, 450);
            var issue = new A6_ClearSize().Check(s, _cfg).Single();
            Assert.Equal(Severity.Violation, issue.Severity);
            Assert.Equal(450, issue.Actual.Value, 3);
        }

        [Fact]
        public void 門太窄_A7違規()
        {
            var s = GoodShaft();
            s.Doors[0].WidthMm = 500;
            Assert.Contains(new A7_DoorSize().Check(s, _cfg), i => i.Severity == Severity.Violation);
        }

        [Fact]
        public void T6_門前有柱_A8違規並標出柱子()
        {
            var s = GoodShaft();
            s.Doors[0].FrontObstructions.Add(new Obstruction { Id = 99, Category = "柱", Name = "C1" });
            var issue = new A8_FrontClearance().Check(s, _cfg).Single();
            Assert.Equal(Severity.Violation, issue.Severity);
            Assert.Contains(99L, issue.ElementIds);
        }

        [Fact]
        public void 假設分析_淨寬門檻提高到1000_原本合格變違規_且不影響原設定()
        {
            var what = _cfg.Clone();
            Assert.True(what.ApplyOverride("minClearWidthMm", 1000));
            Assert.Contains(new A6_ClearSize().Check(GoodShaft(), what), i => i.Severity == Severity.Violation);
            Assert.Equal(600, _cfg.MinClearWidthMm);
            Assert.False(what.ApplyOverride("notAKey", 1));
        }

        [Fact]
        public void 設定檔只啟用A1_只執行A1()
        {
            var cfg = new RuleConfig { EnabledRules = new List<string> { "A1" } };
            var result = new RuleEngine().Run(new[] { GoodShaft() }, cfg);
            Assert.All(result.Shafts[0].Issues, i => Assert.Equal("A1", i.RuleId));
        }
    }
}
