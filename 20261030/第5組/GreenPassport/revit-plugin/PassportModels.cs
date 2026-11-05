using System.Collections.Generic;
using Newtonsoft.Json;

namespace TemporalBim
{
    public class RawParameterInfo
    {
        [JsonProperty("scope")] public string Scope { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("parameterId")] public string ParameterId { get; set; } = "";
        [JsonProperty("guid")] public string Guid { get; set; } = "";
        [JsonProperty("builtin")] public string Builtin { get; set; } = "";
        [JsonProperty("storage")] public string Storage { get; set; } = "";
        [JsonProperty("displayValue")] public string DisplayValue { get; set; } = "";
        [JsonProperty("rawValue")] public string RawValue { get; set; } = "";
        [JsonProperty("unit")] public string Unit { get; set; } = "";
        [JsonProperty("numericValueMm")] public double? NumericValueMm { get; set; }

        public override string ToString() => $"{Scope}｜{Name}：{DisplayValue}";
    }

    public class FieldSourceInfo
    {
        [JsonProperty("kind")] public string Kind { get; set; } = "unknown";
        [JsonProperty("scope")] public string Scope { get; set; } = "";
        [JsonProperty("parameterName")] public string ParameterName { get; set; } = "";
        [JsonProperty("parameterId")] public string ParameterId { get; set; } = "";
        [JsonProperty("unit")] public string Unit { get; set; } = "";
        [JsonProperty("definition")] public string Definition { get; set; } = "";
        [JsonProperty("verification")] public string Verification { get; set; } = "BIM參數宣告，未經實體檢核／證明驗證";
    }

    /// <summary>Canonical passport plus complete Revit evidence.</summary>
    public class PassportInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("passportId")] public string PassportId { get; set; } = "";
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("sourceModel")] public string SourceModel { get; set; } = "";
        [JsonProperty("sourceUniqueId")] public string SourceUniqueId { get; set; } = "";
        [JsonProperty("sourceElementId")] public string SourceElementId { get; set; } = "";
        [JsonProperty("family")] public string Family { get; set; } = "";
        [JsonProperty("typeName")] public string TypeName { get; set; } = "";
        [JsonProperty("widthMm")] public double WidthMm { get; set; }
        [JsonProperty("heightMm")] public double HeightMm { get; set; }
        [JsonProperty("dimensionDefinition")] public string DimensionDefinition { get; set; } = "nominal";
        [JsonProperty("leafThicknessMm")] public double LeafThicknessMm { get; set; }
        [JsonProperty("frameDimensions")] public string FrameDimensions { get; set; } = "";
        [JsonProperty("operationType")] public string OperationType { get; set; } = "";
        [JsonProperty("swingHanding")] public string SwingHanding { get; set; } = "";
        [JsonProperty("useType")] public string UseType { get; set; } = "";
        [JsonProperty("clearWidthMm")] public double ClearWidthMm { get; set; }
        [JsonProperty("leafCount")] public int LeafCount { get; set; } = 1;
        [JsonProperty("material")] public string Material { get; set; } = "";
        [JsonIgnore] public string FireRating { get; set; } = "";
        [JsonIgnore] public int FireMinutes { get; set; }
        [JsonIgnore] public string FireCertificateReference { get; set; } = "";
        [JsonIgnore] public bool FireCertificateVerified { get; set; }
        [JsonProperty("condition")] public string Condition { get; set; } = "";
        [JsonProperty("conditionGrade")] public string ConditionGrade { get; set; } = "";
        [JsonProperty("reuseStatus")] public string ReuseStatus { get; set; } = "Pending";
        [JsonProperty("reviewNote")] public string ReviewNote { get; set; } = "";
        [JsonProperty("quantity")] public int Quantity { get; set; } = 1;
        [JsonProperty("priceNtd")] public double PriceNtd { get; set; }
        [JsonProperty("placementRequirements")] public string PlacementRequirements { get; set; } = "";
        [JsonProperty("inspectedBy")] public string InspectedBy { get; set; } = "";
        [JsonProperty("inspectedOn")] public string InspectedOn { get; set; } = "";
        [JsonProperty("status")] public string Status { get; set; } = "Unavailable";
        [JsonProperty("targetModel")] public string TargetModel { get; set; } = "";
        [JsonProperty("targetElementId")] public string TargetElementId { get; set; } = "";
        [JsonProperty("photo")] public string Photo { get; set; } = "";
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("createdAt")] public string CreatedAt { get; set; } = "";
        [JsonProperty("updatedAt")] public string UpdatedAt { get; set; } = "";
        [JsonProperty("rawParameters")] public List<RawParameterInfo> RawParameters { get; set; } = new List<RawParameterInfo>();
        [JsonProperty("fieldSources")] public Dictionary<string, FieldSourceInfo> FieldSources { get; set; } = new Dictionary<string, FieldSourceInfo>();
        [JsonProperty("mappingWarnings")] public List<string> MappingWarnings { get; set; } = new List<string>();

        public override string ToString() => $"{PassportId}｜{TypeName}（{ReuseStatus} / {Status}）";
    }

    public class MatchRequestInfo
    {
        [JsonProperty("widthMm")] public double WidthMm { get; set; }
        [JsonProperty("heightMm")] public double HeightMm { get; set; }
        [JsonProperty("dimensionDefinition")] public string DimensionDefinition { get; set; } = "nominal";
        [JsonProperty("toleranceMm")] public double ToleranceMm { get; set; } = 5;
        [JsonProperty("leafThicknessMm")] public double LeafThicknessMm { get; set; }
        [JsonProperty("operationType")] public string OperationType { get; set; } = "";
        [JsonProperty("swingHanding")] public string SwingHanding { get; set; } = "";
        [JsonProperty("useType")] public string UseType { get; set; } = "";
        [JsonProperty("materialPreference")] public string MaterialPreference { get; set; } = "";
        [JsonProperty("clearWidthMinMm")] public double ClearWidthMinMm { get; set; }
        [JsonProperty("leafCount")] public int LeafCount { get; set; }
        [JsonProperty("doorOpeningWidthMm")] public double DoorOpeningWidthMm { get; set; }
        [JsonProperty("hostWallThicknessMm")] public double HostWallThicknessMm { get; set; }
        [JsonIgnore] public bool FireRequirementKnown { get; set; }
        [JsonIgnore] public bool FireRequired { get; set; }
        [JsonIgnore] public int FireMinutes { get; set; }
        [JsonProperty("installationReviewed")] public bool InstallationReviewed { get; set; }
        [JsonProperty("basis")] public string Basis { get; set; } = "";
        [JsonProperty("targetModel")] public string TargetModel { get; set; } = "";
        [JsonProperty("targetUniqueId")] public string TargetUniqueId { get; set; } = "";
    }

    public class MatchCheckInfo
    {
        [JsonProperty("key")] public string Key { get; set; } = "";
        [JsonProperty("state")] public string State { get; set; } = "unknown";
        [JsonProperty("reason")] public string Reason { get; set; } = "";
    }

    public class MatchResultInfo
    {
        [JsonProperty("passport")] public PassportInfo Passport { get; set; }
        [JsonProperty("result")] public string Result { get; set; } = "Pending Review";
        [JsonProperty("checks")] public List<MatchCheckInfo> Checks { get; set; } = new List<MatchCheckInfo>();
        [JsonProperty("excluded")] public bool Excluded { get; set; }
        [JsonProperty("excludeReasons")] public List<string> ExcludeReasons { get; set; } = new List<string>();
        [JsonProperty("openingChangeMm")] public double OpeningChangeMm { get; set; }
    }

    public class ReviewPassportInfo
    {
        [JsonProperty("widthMm")] public double WidthMm { get; set; }
        [JsonProperty("heightMm")] public double HeightMm { get; set; }
        [JsonProperty("dimensionDefinition")] public string DimensionDefinition { get; set; } = "nominal";
        [JsonProperty("leafThicknessMm")] public double LeafThicknessMm { get; set; }
        [JsonProperty("operationType")] public string OperationType { get; set; } = "";
        [JsonProperty("swingHanding")] public string SwingHanding { get; set; } = "";
        [JsonProperty("useType")] public string UseType { get; set; } = "";
        [JsonProperty("material")] public string Material { get; set; } = "";
        [JsonProperty("clearWidthMm")] public double ClearWidthMm { get; set; }
        [JsonIgnore] public string FireRating { get; set; } = "";
        [JsonIgnore] public int FireMinutes { get; set; }
        [JsonProperty("reuseStatus")] public string ReuseStatus { get; set; } = "Pending";
        [JsonProperty("condition")] public string Condition { get; set; } = "";
        [JsonProperty("conditionGrade")] public string ConditionGrade { get; set; } = "";
        [JsonProperty("reviewNote")] public string ReviewNote { get; set; } = "";
        [JsonProperty("inspectedBy")] public string InspectedBy { get; set; } = "";
        [JsonProperty("inspectedOn")] public string InspectedOn { get; set; } = "";
        [JsonProperty("quantity")] public int Quantity { get; set; }
        [JsonProperty("priceNtd")] public double PriceNtd { get; set; }
        [JsonIgnore] public string FireCertificateReference { get; set; } = "";
        [JsonIgnore] public bool FireCertificateVerified { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
    }
}
