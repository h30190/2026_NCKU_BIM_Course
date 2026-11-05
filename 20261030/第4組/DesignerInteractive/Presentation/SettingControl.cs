using System.ComponentModel;
using System.Globalization;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Planner.UI;
namespace EVCanopyDesigner.Interactive;

// Presentation adapter only. Text remains authoritative; moving a slider is an explicit user edit.
public sealed class SettingControl : INotifyPropertyChanged
{
 private readonly Func<string> read;
 private readonly Action<string> write;
 private readonly bool integer;
 private double lastFinite;
 private readonly double minimum,maximum;
 public string Key{get;}
 public string Label{get;}
 public string Unit{get;}
 public IReadOnlyList<string> Options{get;}
 public bool HasOptions=>Options.Count>0;
 public bool IsNumeric{get;}
 public double Step{get;}
 public string Value{get=>read();set{if(value!=read())write(value);}}
 public double SliderValue=>TryNumber(out double n)?n:lastFinite;
 public double SliderMinimum=>TryNumber(out double n)?Math.Min(minimum,n):minimum;
 public double SliderMaximum=>TryNumber(out double n)?Math.Max(maximum,n):maximum;
 public bool SliderEnabled=>IsNumeric&&TryNumber(out _);
 public string InputError=>IsNumeric&&!TryNumber(out _)?(integer?"Enter a whole number.":"Enter a finite number using your regional decimal separator."):"";
 public event PropertyChangedEventHandler? PropertyChanged;
 public SettingControl(InputField field):this(field.Key,field.Label,field.Key is "points" or "row1" or "row2"?typeof(int):typeof(double),Array.Empty<string>(),()=>field.Value,v=>field.Value=v)
 {field.PropertyChanged+=(_,_)=>Refresh();}
 public SettingControl(CanopyField field):this(field.Key,field.Label,field.Type,field.Options,()=>field.Value,v=>field.Value=v)
 {field.PropertyChanged+=(_,_)=>Refresh();}
 private SettingControl(string key,string label,Type type,IReadOnlyList<string> options,Func<string> read,Action<string> write)
 {
  Key=key;Label=label;Options=options;this.read=read;this.write=write;integer=type==typeof(int);IsNumeric=integer||type==typeof(double);
  Unit=integer?"count":key=="SlopePercent"?"%":key=="PvPowerWatts"?"W":IsNumeric?"m":"";
  (minimum,maximum,Step)=key switch{
   "EquipmentOffsetX" or "EquipmentOffsetY" or "EquipmentInletOffsetX"=>(-3,3,.05),
   "EquipmentConnectorsPerOutput"=>(1,4,1),
   "EquipmentWidth" or "EquipmentDepth" or "EquipmentServiceMargin"=>(0,2,.05),
   "points"=>(2,1000,1),"row1" or "row2"=>(1,999,1),"ColumnCount"=>(1,500,1),
   "SlopePercent"=>(.1,10,.1),"PvPowerWatts"=>(1,2000,10),
   "RoofDatum"=>(3,12,.1),"MinimumClearance"=>(2,8,.1),
   "RoofThickness" or "PvThickness" or "GutterWallThickness"=>(.001,.5,.001),
   "ColumnWidth" or "ColumnDepth" or "ArmWidth" or "ArmDepth" or "PurlinWidth" or "PurlinDepth" or "GutterWidth" or "GutterDepth"=>(.01,1,.01),
   "PvGap" or "PvOffset" or "PvSetback"=>(0,2,.01),_=>(0,12,.1)};
  if(TryNumber(out var n))lastFinite=n;
 }
 private bool TryNumber(out double n)
 {
  if(integer){bool ok=int.TryParse(Value,NumberStyles.Integer,CultureInfo.CurrentCulture,out int i);n=i;return ok;}
  return double.TryParse(Value,NumberStyles.Float,CultureInfo.CurrentCulture,out n)&&double.IsFinite(n);
 }
 private void Refresh()
 {
  if(TryNumber(out var n))lastFinite=n;
  foreach(var key in new[]{nameof(SliderMinimum),nameof(SliderMaximum),nameof(Value),nameof(SliderValue),nameof(SliderEnabled),nameof(InputError)})PropertyChanged?.Invoke(this,new(key));
 }
 public void SetFromSlider(double n)
 {
  if(!SliderEnabled||!double.IsFinite(n))return;
  n=integer?Math.Round(n,MidpointRounding.AwayFromZero):Math.Round(n/Step,MidpointRounding.AwayFromZero)*Step;
  Value=n.ToString(integer?"0":"G15",CultureInfo.CurrentCulture);
 }
}
