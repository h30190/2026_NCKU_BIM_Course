using System.Text.Json;
using System.Text.Json.Serialization;
using LebrCarbon.Core.Mapping;
using LebrCarbon.Core.Models;

namespace LebrCarbon.Data;

/// <summary>factors.LEBR-2025.json 的檔案結構（計畫書 4.1 節）。</summary>
public sealed class FactorLibrary
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Manual { get; set; } = "";
    public List<CarbonFactor> Factors { get; set; } = new();
    public Dictionary<string, DefaultValue> Defaults { get; set; } = new();

    public sealed class DefaultValue
    {
        public string Unit { get; set; } = "";
        public double Value { get; set; }
        public string Source { get; set; } = "";
    }

    public double GetDefault(string key) =>
        Defaults.TryGetValue(key, out var d) ? d.Value : 0;
}

public sealed class MappingRuleFile
{
    public string SchemaVersion { get; set; } = "1.0";
    public List<MappingRule> Rules { get; set; } = new();
}

public static class DataLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>外掛 DLL 旁的 Resources 資料夾。</summary>
    public static string DefaultResourceDir =>
        Path.Combine(Path.GetDirectoryName(typeof(DataLoader).Assembly.Location)!, "Resources");

    public static FactorLibrary LoadFactors(string? path = null)
    {
        path ??= Path.Combine(DefaultResourceDir, "factors.LEBR-2025.json");
        return Load<FactorLibrary>(path);
    }

    public static MappingRuleFile LoadRules(string? path = null)
    {
        path ??= Path.Combine(DefaultResourceDir, "mapping-rules.json");
        return Load<MappingRuleFile>(path);
    }

    private static T Load<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到資料檔：{path}", path);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
               ?? throw new InvalidDataException($"資料檔內容無法解析：{path}");
    }
}
