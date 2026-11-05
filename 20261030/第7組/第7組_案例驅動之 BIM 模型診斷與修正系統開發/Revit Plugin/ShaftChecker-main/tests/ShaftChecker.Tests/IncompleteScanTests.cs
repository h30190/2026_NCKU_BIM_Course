using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Rules;
using Xunit;

namespace ShaftChecker.Tests
{
    public class IncompleteScanTests
    {
        [Fact]
        public void MissingMepModelDoesNotPassPipeRules()
        {
            var shaft = new ShaftInfo { MepModelFound = false };
            var result = new RuleEngine().Run(new[] { shaft }, new RuleConfig());
            var issues = result.Shafts.Single().Issues.Where(i => i.RuleId.StartsWith("P")).ToList();
            Assert.Equal(5, issues.Count);
            Assert.All(issues, i => Assert.Equal(Severity.DataMissing, i.Severity));
        }

        [Fact]
        public void FailedScanDoesNotPassMepOrDoorHeadChecks()
        {
            var shaft = new ShaftInfo { ScanComplete = false };
            var result = new RuleEngine().Run(new[] { shaft }, new RuleConfig());
            var issues = result.Shafts.Single().Issues.Where(i => i.RuleId.StartsWith("P") || i.RuleId == "A9").ToList();
            Assert.Equal(6, issues.Count);
            Assert.All(issues, i => Assert.Equal(Severity.DataMissing, i.Severity));
            Assert.Contains(result.Shafts.Single().Issues, i => i.RuleId == "A1");
        }
    }
}
