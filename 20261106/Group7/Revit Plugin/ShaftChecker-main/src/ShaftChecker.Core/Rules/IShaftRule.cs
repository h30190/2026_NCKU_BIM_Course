using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// 一條檢討規則。新增檢討項目時，只要新增一個實作此介面的類別，
    /// 再加進 RuleEngine.AllRules 即可。
    /// </summary>
    public interface IShaftRule
    {
        string Id { get; }
        string Name { get; }
        RuleNature Nature { get; }
        string Reference { get; }
        /// <summary>規則分組：fire 防火區劃、maint 維修門與空間、mep 管線分隔與間距、struct 結構柱梁。</summary>
        string Group { get; }

        /// <summary>這個管道間適用的法規依據（地下層會加註地下建築物條文）。</summary>
        string ReferenceFor(ShaftInfo shaft);

        /// <summary>只回傳「不合格」的項目；沒有回傳任何項目代表合格。</summary>
        IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg);
    }

    /// <summary>提供建立 Issue 的共用方法。</summary>
    public abstract class RuleBase : IShaftRule
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public abstract RuleNature Nature { get; }
        public abstract string Reference { get; }
        public abstract IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg);

        public virtual string Group { get { return RuleGroups.Of(Id); } }

        /// <summary>地下層要加註的條文；null 代表地下層與一般樓層相同。</summary>
        protected virtual string UndergroundReference { get { return null; } }

        public string ReferenceFor(ShaftInfo shaft)
        {
            if (shaft != null && shaft.IsUnderground && !string.IsNullOrEmpty(UndergroundReference))
                return Reference + "；" + UndergroundReference;
            return Reference;
        }

        protected Issue Make(ShaftInfo shaft, Severity severity, string message,
                             double? actual = null, double? required = null, string unit = null,
                             params long[] elementIds)
        {
            var issue = new Issue
            {
                RuleId = Id,
                RuleName = Name,
                Nature = Nature,
                Reference = ReferenceFor(shaft),
                ShaftRoomId = shaft.RoomId,
                Severity = severity,
                Message = message,
                Actual = actual,
                Required = required,
                Unit = unit
            };
            if (elementIds != null)
            {
                issue.ElementIds.AddRange(elementIds);
                // 牆、門、房間都屬於管道間所在的模型
                issue.Refs.AddRange(elementIds.Distinct().Select(id => new ElementRef(id, shaft.SourceName)));
            }
            return issue;
        }

        /// <summary>
        /// 以元件物件（MepInfo、BeamInfo、ColumnInfo、DoorInfo…）建立結果：
        /// Refs 記錄每個元件所在的模型（可選取連結模型中的元件），ElementIds 只放主模型元件（上色用）。
        /// </summary>
        protected Issue MakeFor(ShaftInfo shaft, Severity severity, string message, string suggestion,
                                double? actual, double? required, string unit, params object[] items)
        {
            List<ElementRef> refs = ShaftSpace.Refs(shaft, items);
            Issue issue = Make(shaft, severity, message, actual, required, unit);
            issue.Suggestion = suggestion;
            issue.Refs = refs;
            issue.ElementIds = refs.Where(r => r.Source == null).Select(r => r.Id).Distinct().ToList();
            return issue;
        }

        protected Issue Make(ShaftInfo shaft, Severity severity, string message, string suggestion,
                             double? actual, double? required, string unit, params long[] elementIds)
        {
            Issue issue = Make(shaft, severity, message, actual, required, unit, elementIds);
            issue.Suggestion = suggestion;
            return issue;
        }
    }
}
