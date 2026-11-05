using System.Collections.Generic;
using Newtonsoft.Json;

namespace TemporalBim
{
    /// <summary>單一元件在某個版本的狀態。欄位要跟後端 models.go 的 ElementSnapshot 對齊。</summary>
    public class ElementSnapshotInfo
    {
        [JsonProperty("uniqueId")] public string UniqueId { get; set; }
        [JsonProperty("elementId")] public string ElementId { get; set; }
        [JsonProperty("typeName")] public string TypeName { get; set; }
        [JsonProperty("material")] public string Material { get; set; }
        [JsonProperty("quantity")] public double Quantity { get; set; }
        [JsonProperty("positionX")] public double PositionX { get; set; }
        [JsonProperty("positionY")] public double PositionY { get; set; }
        [JsonProperty("positionZ")] public double PositionZ { get; set; }
        [JsonProperty("levelName")] public string LevelName { get; set; }
    }

    /// <summary>POST /snapshot 的 request body。</summary>
    public class SnapshotRequest
    {
        [JsonProperty("label")] public string Label { get; set; } = "";
        [JsonProperty("modelName")] public string ModelName { get; set; } = "";
        [JsonProperty("elements")] public List<ElementSnapshotInfo> Elements { get; set; } = new List<ElementSnapshotInfo>();
    }

    /// <summary>GET /versions 其中一列。</summary>
    public class VersionInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("modelName")] public string ModelName { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; }
        [JsonProperty("elementCount")] public int ElementCount { get; set; }

        public override string ToString() => $"{Label}（{CreatedAt}，{ElementCount} 個元件）";
    }

    /// <summary>Diff 結果裡，一筆「修改」類型元件的單一欄位新舊值。</summary>
    public class ChangedFieldInfo
    {
        [JsonProperty("field")] public string Field { get; set; }
        [JsonProperty("oldValue")] public string OldValue { get; set; }
        [JsonProperty("newValue")] public string NewValue { get; set; }
    }

    /// <summary>Diff 結果裡的一筆元件異動。ChangeType 是 "added" / "removed" / "modified"。</summary>
    public class ChangeEventInfo
    {
        [JsonProperty("uniqueId")] public string UniqueId { get; set; }
        [JsonProperty("changeType")] public string ChangeType { get; set; }
        [JsonProperty("changedFields")] public List<ChangedFieldInfo> ChangedFields { get; set; }
        [JsonProperty("lastSnapshot")] public ElementSnapshotInfo LastSnapshot { get; set; }

        public string ChangeTypeLabel
        {
            get
            {
                switch (ChangeType)
                {
                    case "added": return "新增";
                    case "removed": return "移除";
                    case "modified": return "修改";
                    default: return ChangeType;
                }
            }
        }
    }

    /// <summary>GET /diff 的回傳格式。</summary>
    public class DiffResult
    {
        [JsonProperty("fromVersionId")] public long FromVersionId { get; set; }
        [JsonProperty("toVersionId")] public long ToVersionId { get; set; }
        [JsonProperty("changes")] public List<ChangeEventInfo> Changes { get; set; } = new List<ChangeEventInfo>();
    }
}
