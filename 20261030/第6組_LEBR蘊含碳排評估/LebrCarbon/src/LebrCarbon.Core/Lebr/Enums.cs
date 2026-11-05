namespace LebrCarbon.Core.Lebr;

/// <summary>地上結構構造類別（手冊表 3）。</summary>
public enum StructureType { Masonry, RC, SRC, Steel, LightSteel, Wood }

/// <summary>建築耗損等級（手冊表 1），決定內隔間、地坪的更新次數。</summary>
public enum WearLevel
{
    /// <summary>高耗損：商店商場、旅館、餐廳、運動、醫療、娛樂、交通旅運設施。</summary>
    High,
    /// <summary>中耗損：出租辦公、工廠、公共廳舍、教育文化設施。</summary>
    Mid,
    /// <summary>低耗損：自用辦公、倉庫、住宅、住宿類。</summary>
    Low,
}

/// <summary>廢棄物量密度的用途分類（手冊表 9）。</summary>
public enum WasteUsage
{
    /// <summary>住宿類與旅館、醫院類。</summary>
    Residential,
    /// <summary>廠房類。</summary>
    Factory,
    /// <summary>其他非住宿類。</summary>
    Other,
}

/// <summary>非主結構六類構件（手冊 3-1 節、式 11）。代碼與 B-LCC 資料庫 category 欄位一致。</summary>
public enum NsCategory
{
    /// <summary>ow：一般外牆外裝（附表 2-1）。</summary>
    ExteriorFinish,
    /// <summary>w：外窗與透光帷幕外窗（附表 2-2、2-3）。</summary>
    Window,
    /// <summary>cw：不透光帷幕外牆及一般外牆（附表 2-4）。</summary>
    CurtainWall,
    /// <summary>iw：內隔間（附表 2-5）。</summary>
    InteriorWall,
    /// <summary>f：室內地坪（附表 2-6）。</summary>
    IndoorFloor,
    /// <summary>p：戶外地坪（附表 2-7）。</summary>
    OutdoorFloor,
}

public static class NsCategoryCodes
{
    public static NsCategory Parse(string code) => code.Trim().ToLowerInvariant() switch
    {
        "ow" => NsCategory.ExteriorFinish,
        "w" => NsCategory.Window,
        "cw" => NsCategory.CurtainWall,
        "iw" => NsCategory.InteriorWall,
        "f" => NsCategory.IndoorFloor,
        "p" => NsCategory.OutdoorFloor,
        _ => throw new ArgumentException($"未知的構件類別代碼：{code}"),
    };

    public static string Code(this NsCategory c) => c switch
    {
        NsCategory.ExteriorFinish => "ow",
        NsCategory.Window => "w",
        NsCategory.CurtainWall => "cw",
        NsCategory.InteriorWall => "iw",
        NsCategory.IndoorFloor => "f",
        _ => "p",
    };

    public static string DisplayName(this NsCategory c) => c switch
    {
        NsCategory.ExteriorFinish => "一般外牆外裝",
        NsCategory.Window => "外窗及透光帷幕外窗",
        NsCategory.CurtainWall => "不透光帷幕外牆及一般外牆",
        NsCategory.InteriorWall => "內隔間",
        NsCategory.IndoorFloor => "室內地坪",
        _ => "戶外地坪",
    };
}
