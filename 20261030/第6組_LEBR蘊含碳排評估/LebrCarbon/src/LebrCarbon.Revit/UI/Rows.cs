using System.ComponentModel;
using System.Runtime.CompilerServices;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Revit.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>樓層表的一列：勾選是否計入地上（或地下）樓地板面積。</summary>
public sealed class LevelRow : Observable
{
    private bool _include;
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public double ElevationM { get; init; }
    public double AreaM2 { get; init; }
    public bool Include { get => _include; set => Set(ref _include, value); }
}

/// <summary>非主結構構件表的一列。選了 B-LCC 項目時，類別、名稱、碳排由資料庫帶入。</summary>
public sealed class NsRow : Observable
{
    private string _blccId = "";
    private NsCategory _category;
    private string _name = "";
    private double _area;
    private double _newEf;
    private double _renewEf;
    private string _source = "";

    /// <summary>由視窗設定：選擇 B-LCC 項目時用來帶入數值。</summary>
    public Func<string, BlccItem?>? Lookup { get; set; }
    public Func<WearLevel>? Wear { get; set; }

    public string BlccId
    {
        get => _blccId;
        set
        {
            if (!Set(ref _blccId, value ?? "")) return;
            ApplyBlcc();
        }
    }

    public NsCategory Category { get => _category; set { if (Set(ref _category, value)) Raise(nameof(CategoryName)); } }
    public string CategoryName => _category.DisplayName();
    public string Name { get => _name; set => Set(ref _name, value); }
    public double Area { get => _area; set { if (Set(ref _area, value)) RaiseTotals(); } }
    public double NewEf { get => _newEf; set { if (Set(ref _newEf, value)) RaiseTotals(); } }
    public double RenewEf { get => _renewEf; set { if (Set(ref _renewEf, value)) RaiseTotals(); } }
    public string Source { get => _source; set => Set(ref _source, value); }
    public bool IsCustom => string.IsNullOrEmpty(_blccId);
    public double NewEmission => _area * _newEf;
    public double RenewEmission => _area * _renewEf;

    private void RaiseTotals()
    {
        Raise(nameof(NewEmission));
        Raise(nameof(RenewEmission));
    }

    /// <summary>依 B-LCC 與目前耗損等級重新帶入碳排標準。</summary>
    public void ApplyBlcc()
    {
        Raise(nameof(IsCustom));
        var b = Lookup?.Invoke(_blccId);
        if (b is null) return;
        Category = b.CategoryEnum;
        Name = b.Name;
        NewEf = b.New;
        RenewEf = b.Renew(Wear?.Invoke() ?? WearLevel.Low);
    }

    public NonStructuralItem ToItem() => new()
    {
        Category = _category,
        Name = _name,
        Area = _area,
        NewEf = _newEf,
        RenewEf = _renewEf,
        BlccId = IsCustom ? null : _blccId,
        Source = _source,
    };

    public static NsRow From(NonStructuralItem i) => new()
    {
        _blccId = i.BlccId ?? "",
        _category = i.Category,
        _name = i.Name,
        _area = i.Area,
        _newEf = i.NewEf,
        _renewEf = i.RenewEf,
        _source = i.Source,
    };
}

/// <summary>B-LCC 下拉選單的選項。</summary>
public sealed record BlccChoice(string Id, string Display);
