using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Recognition;
using Xunit;

namespace ShaftChecker.Tests
{
    public class PolygonExtraTests
    {
        [Fact]
        public void 矩形重心在中心()
        {
            Point2D c = PolygonUtils.Centroid(GeometryTests.Rect(1200, 800));
            Assert.Equal(600, c.X, 3);
            Assert.Equal(400, c.Y, 3);
        }

        [Fact]
        public void 點在多邊形內外判斷()
        {
            var rect = GeometryTests.Rect(1200, 800);
            Assert.True(PolygonUtils.Contains(rect, 600, 400));
            Assert.False(PolygonUtils.Contains(rect, 1300, 400));
            Assert.False(PolygonUtils.Contains(rect, 600, -10));
        }
    }

    public class ShaftClassifierTests
    {
        private readonly RuleConfig _cfg = new RuleConfig();

        private static SpaceFeatures Space(string name, double areaM2 = 2.0, double shortMm = 900, string number = "101")
        {
            return new SpaceFeatures
            {
                RoomId = 1, Number = number, Name = name, LevelName = "3F",
                AreaM2 = areaM2, ShortSideMm = shortMm, LongSideMm = areaM2 * 1e6 / shortMm
            };
        }

        [Theory]
        [InlineData("PS-1")]
        [InlineData("EPS")]
        [InlineData("管道間")]
        [InlineData("水管井")]
        [InlineData("Pipe Shaft")]
        public void 名稱符合_確定是管道間(string name)
        {
            var d = ShaftClassifier.Classify(Space(name, number: "1"), _cfg);
            Assert.Equal(DetectionLevel.Confirmed, d.Level);
            Assert.True(d.HasTextEvidence);
        }

        [Fact]
        public void 編號符合也算()
        {
            var d = ShaftClassifier.Classify(Space("房間", number: "PS-3"), _cfg);
            Assert.Equal(DetectionLevel.Confirmed, d.Level);
        }

        [Fact]
        public void 備註提到管道間_加上小尺寸_確定()
        {
            var f = Space("房間", areaM2: 2);
            f.ParameterHit = "備註＝管道間";
            Assert.Equal(DetectionLevel.Confirmed, ShaftClassifier.Classify(f, _cfg).Level);
        }

        [Fact]
        public void 沒標名稱_但有垂直管線且上下樓層疊合_很可能_但不會到確定()
        {
            var f = Space("房間", areaM2: 2.5, shortMm: 1000);
            f.MepLinearCount = 5;
            f.MepVerticalCount = 4;
            f.StackedLevels = 4;
            f.ShaftOpeningCount = 1;

            var d = ShaftClassifier.Classify(f, _cfg);

            Assert.True(d.Score >= _cfg.DetectConfirmedScore);
            Assert.Equal(DetectionLevel.Likely, d.Level); // 沒有文字證據，封頂在「很可能」
            Assert.False(d.HasTextEvidence);
            Assert.True(d.IncludedByDefault);
        }

        [Fact]
        public void 沒標名稱_只有一條管線_疑似_預設不納入()
        {
            var f = Space("房間", areaM2: 2.5, shortMm: 1000);
            f.MepLinearCount = 1;

            var d = ShaftClassifier.Classify(f, _cfg);

            Assert.Equal(DetectionLevel.Candidate, d.Level);
            Assert.False(d.IncludedByDefault);
        }

        [Fact]
        public void 沒標名稱_只有尺寸小_不夠_非管道間()
        {
            var d = ShaftClassifier.Classify(Space("房間", areaM2: 2.5, shortMm: 1000), _cfg);
            Assert.Equal(DetectionLevel.None, d.Level);
        }

        [Fact]
        public void 名稱是其他空間_就算有機電元件也不算()
        {
            var f = Space("廁所", areaM2: 3, shortMm: 1200);
            f.MepLinearCount = 6;
            f.MepVerticalCount = 3;
            f.StackedLevels = 3;
            Assert.Equal(DetectionLevel.None, ShaftClassifier.Classify(f, _cfg).Level);
        }

        [Fact]
        public void 機電室是機房_不是管道間()
        {
            var f = Space("機電室", areaM2: 10, shortMm: 2500);
            f.MepEquipmentCount = 4;
            f.MepLinearCount = 10;
            Assert.Equal(DetectionLevel.None, ShaftClassifier.Classify(f, _cfg).Level);
        }

        [Fact]
        public void 面積太大_沒有名稱就不會是管道間()
        {
            var f = Space("房間", areaM2: 40, shortMm: 5000);
            f.ParameterHit = "備註＝管道間旁";
            f.MepLinearCount = 20;
            f.MepVerticalCount = 8;
            f.ShaftOpeningCount = 2;

            var d = ShaftClassifier.Classify(f, _cfg);

            Assert.Equal(DetectionLevel.None, d.Level);
            Assert.Contains(d.Reasons, r => r.Contains("超過管道間上限"));
        }

        [Fact]
        public void 使用者標是_直接確定_即使面積很大()
        {
            var f = Space("房間", areaM2: 40, shortMm: 5000);
            f.UserConfirm = "是";
            var d = ShaftClassifier.Classify(f, _cfg);
            Assert.Equal(DetectionLevel.Confirmed, d.Level);
            Assert.True(d.ByUser);
        }

        [Fact]
        public void 使用者標否_即使名稱符合也排除()
        {
            var f = Space("PS-9", number: "PS-9");
            f.UserConfirm = "否";
            var d = ShaftClassifier.Classify(f, _cfg);
            Assert.Equal(DetectionLevel.None, d.Level);
            Assert.True(d.ExcludedByUser);
        }

        [Fact]
        public void 理由會說明依據()
        {
            var f = Space("PS-1", number: "PS-1");
            f.MepLinearCount = 3;
            var d = ShaftClassifier.Classify(f, _cfg);
            Assert.NotEmpty(d.Reasons);
            Assert.Contains(d.Reasons, r => r.Contains("命名"));
            Assert.Contains(d.Reasons, r => r.Contains("機電"));
        }

        [Fact]
        public void 門太多_扣分()
        {
            var f = Space("房間", areaM2: 3, shortMm: 1200);
            f.MepLinearCount = 3;
            int without = ShaftClassifier.Classify(f, _cfg).Score;
            f.DoorCount = 3;
            int with = ShaftClassifier.Classify(f, _cfg).Score;
            Assert.Equal(without + ShaftClassifier.ManyDoorsPenalty, with);
        }

        [Fact]
        public void 預設名稱規則可以辨認常見寫法_也不會誤判一般房間()
        {
            Assert.True(RuleConfig.MatchesAny("電氣管道間", _cfg.ShaftNamePatterns));
            Assert.True(RuleConfig.MatchesAny("PS", _cfg.ShaftNamePatterns));
            Assert.False(RuleConfig.MatchesAny("辦公室", _cfg.ShaftNamePatterns));
            Assert.False(RuleConfig.MatchesAny("房間", _cfg.ShaftNamePatterns));
            Assert.True(RuleConfig.MatchesAny("廁所", _cfg.NonShaftNamePatterns));
            Assert.True(RuleConfig.MatchesAny("Meeting Room", _cfg.NonShaftNamePatterns));
            Assert.False(RuleConfig.MatchesAny("管道間", _cfg.NonShaftNamePatterns));
        }

        // ---------- 上下樓層疊合 ----------

        private static SpaceFeatures At(string level, double x, double y, double area = 2.0)
        {
            return new SpaceFeatures { LevelName = level, CentroidXMm = x, CentroidYMm = y, AreaM2 = area };
        }

        [Fact]
        public void 三層相同位置_各自疊到另外兩層()
        {
            var list = new List<SpaceFeatures> { At("1F", 1000, 1000), At("2F", 1050, 980), At("3F", 990, 1020) };
            ShaftClassifier.AssignStacks(list, 12);
            Assert.All(list, f => Assert.Equal(2, f.StackedLevels));
        }

        [Fact]
        public void 位置差太遠或大小差太多_不算疊合()
        {
            var list = new List<SpaceFeatures>
            {
                At("1F", 1000, 1000, 2.0),
                At("2F", 3000, 1000, 2.0),   // 位置差 2 m
                At("3F", 1000, 1000, 6.0)    // 大小差 3 倍
            };
            ShaftClassifier.AssignStacks(list, 12);
            Assert.Equal(0, list[0].StackedLevels);
        }

        [Fact]
        public void 同一層的兩個房間不算疊合()
        {
            var list = new List<SpaceFeatures> { At("1F", 1000, 1000), At("1F", 1010, 1000) };
            ShaftClassifier.AssignStacks(list, 12);
            Assert.All(list, f => Assert.Equal(0, f.StackedLevels));
        }

        [Fact]
        public void 面積超過上限的房間不參與疊合()
        {
            var list = new List<SpaceFeatures> { At("1F", 0, 0, 30), At("2F", 0, 0, 30) };
            ShaftClassifier.AssignStacks(list, 12);
            Assert.All(list, f => Assert.Equal(0, f.StackedLevels));
        }

        // ---------- 連結模型結果的處理 ----------

        [Fact]
        public void HostOnly_只留下主模型的管道間()
        {
            var r = new CheckResult();
            r.Shafts.Add(new ShaftResult { Shaft = new ShaftInfo { RoomId = 1 } });
            r.Shafts.Add(new ShaftResult { Shaft = new ShaftInfo { RoomId = 2, SourceName = "建築.rvt" } });

            Assert.Equal(1, r.HostOnly().Shafts.Count);
            Assert.Equal(1, r.LinkedCount);
        }
    }
}
