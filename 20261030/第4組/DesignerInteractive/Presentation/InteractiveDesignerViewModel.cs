using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Planner.UI;
namespace EVCanopyDesigner.Interactive;

public sealed record RoofChoice(string Key,string Label,bool Enabled,string Explanation);
public sealed record InteractiveGroup(string Name,IReadOnlyList<SettingControl> Fields,string Information,
 bool ShowRows=false,bool ShowManual=false,bool ShowExclusions=false,bool ShowShapes=false,bool ShowVisualization=false,bool ShowEquipment=false);
public sealed class VisualizationSettings : INotifyPropertyChanged
{
 private bool pv=true,roof=true,structure=true;
 public bool ShowPv{get=>pv;set{if(pv==value)return;pv=value;PropertyChanged?.Invoke(this,new(nameof(ShowPv)));}}
 public bool ShowRoof{get=>roof;set{if(roof==value)return;roof=value;PropertyChanged?.Invoke(this,new(nameof(ShowRoof)));}}
 public bool ShowStructure{get=>structure;set{if(structure==value)return;structure=value;PropertyChanged?.Invoke(this,new(nameof(ShowStructure)));}}
 public event PropertyChangedEventHandler? PropertyChanged;
}

// Reuses the unchanged baseline parser, Planner, CanopyEngine, dashboards and configuration codec.
public sealed class InteractiveDesignerViewModel : INotifyPropertyChanged
{
 private readonly CanopyViewModel model=new();
 private bool batch;
 private EquipmentLayout? equipment;
 private string equipmentError="";
 private readonly CanopyField[] equipmentFields;
 public IReadOnlyList<SettingControl> EquipmentSettings{get;}
 public EquipmentLayout? Equipment=>IsCurrent&&equipment?.CanopyFingerprint==model.Design?.Fingerprint?equipment:null;
 public string EquipmentValidation=>PreviewPending?"Equipment awaits current inputs.":Equipment is null?equipmentError:string.Join("\n",Equipment.Issues.Select(i=>$"{(i.Blocking?"CONFLICT":"NOTE")} {i.ObjectId} [{i.Code}]: {i.Detail}"));
 public string EquipmentAssignments=>Equipment is null?"":string.Join("\n",Equipment.Stations.Select(s=>$"{s.Id}: "+string.Join(" | ",s.Outputs.Select(o=>o.SpaceId is null?$"{o.Id} → spare":$"{o.Id} → {o.SpaceId} ({o.RequiredCable:F2} m incl. slack)"))));
 public string ExportEquipment()=>Equipment is not null?EquipmentCodec.Export(Equipment):throw new ArgumentException("Calculate a current equipment layout first.");
 public void ImportEquipment(string json)
 {
  if(!IsCurrent||Design is null)throw new ArgumentException("Load a valid matching canopy first.");
  var options=EquipmentCodec.Import(json,Design.Fingerprint);SetEquipmentOptions(options);Calculate();
 }
 public void SetEquipmentOptions(EquipmentOptions options)
 {
  EquipmentPlanner.Validate(options);batch=true;
  try{foreach(var field in equipmentFields){var value=typeof(EquipmentOptions).GetProperty(field.Key[9..])!.GetValue(options)!;field.Value=((IFormattable)value).ToString(null,CultureInfo.CurrentCulture);}}finally{batch=false;}
  Edited();
 }
 private void CalculateEquipment()
 {
  equipment=null;equipmentError="Equipment awaits a valid canopy.";
  if(model.Design is null)return;
  try{
   var node=JsonSerializer.SerializeToNode(new EquipmentOptions())!.AsObject();
   foreach(var f in equipmentFields){if(f.Type==typeof(int)){if(!int.TryParse(f.Value,out int n))throw new ArgumentException(f.Label+": whole number required.");node[f.Key[9..]]=n;}else{if(!double.TryParse(f.Value,NumberStyles.Float,CultureInfo.CurrentCulture,out double n)||!double.IsFinite(n))throw new ArgumentException(f.Label+": finite number required.");node[f.Key[9..]]=n;}}
   equipment=EquipmentPlanner.Calculate(model.Design,node.Deserialize<EquipmentOptions>()!);equipmentError="";
  }catch(ArgumentException e){equipmentError="INVALID EQUIPMENT: "+e.Message;}
 }
 private void NotifyEquipment(){Notify(nameof(Equipment));Notify(nameof(EquipmentSummary));Notify(nameof(EquipmentValidation));Notify(nameof(EquipmentAssignments));Notify(nameof(DesignSummary));}
 public static readonly string[] GroupNames={"Basic Design","Parking Layout","Canopy & Structure","Solar & Energy","EV Charging Equipment","Site & Circulation","Visualization"};
 public IReadOnlyList<SettingControl> Settings{get;}
 public IReadOnlyList<InteractiveGroup> InputGroups{get;}
 public InteractiveGroup BasicGroup=>InputGroups[0];
 public IEnumerable<InteractiveGroup> AdditionalGroups=>InputGroups.Skip(1);
 public PlannerViewModel Site=>model.Site;
 public CanopyDesign? Design=>PreviewPending?null:model.Design;
 public bool IsCurrent=>model.IsCurrent&&!PreviewPending;
 public bool PreviewPending{get;private set;}
 public long InputRevision{get;private set;}
 public int CalculationCount{get;private set;}
 public string Status=>PreviewPending?"Preview pending — Revit generation remains locked.":model.Status+" Equipment: "+(Equipment is null?equipmentError:Equipment.Passed?"PASS under configured assumptions.":$"CONFLICT ({Equipment.Issues.Count(i=>i.Blocking)} checks). See Equipment tab; candidate is not accepted.");
 public string EquipmentSummary=>Equipment?.Summary??(PreviewPending?"Equipment awaits current inputs.":equipmentError);
 public string Summary=>model.Summary;
 public string Disclaimer=>model.Disclaimer;
 public IReadOnlyList<SummaryGroup> DesignSummary=>PreviewPending?Array.Empty<SummaryGroup>():model.DesignSummary.Select(g=>g.Name=="Charging & Electrical"?new SummaryGroup(g.Name,EquipmentSummary):g).ToArray();
 public IReadOnlyList<TakeoffRow> MaterialTakeoff=>model.MaterialTakeoff;
 public IReadOnlyList<ComponentRow> Rows=>model.Rows;
 public IReadOnlyList<QuantityTotal> Schedule=>model.Schedule;
 public VisualizationSettings Visualization{get;}=new();
 public string ManualOffsets{get=>model.ManualOffsets;set{if(value==model.ManualOffsets)return;model.ManualOffsets=value;Edited();}}
 public string Exclusions{get=>model.Exclusions;set{if(value==model.Exclusions)return;model.Exclusions=value;Edited();}}
 public IReadOnlyList<RoofChoice> RoofChoices{get;}=Array.AsReadOnly(new[]{
  new RoofChoice("Outward","Low-slope timber / outward — positive fall",true,"Visually flat appearance is possible with shallow positive fall (0.1–10%). Two sloping wings, not a horizontal roof; drainage engineering is unverified."),
  new RoofChoice("Butterfly","Butterfly / inward — supported",true,"Existing TowardSpine geometry; centre valley. Uses the current slope and gutter calculation."),
  new RoofChoice("Flat","Flat (0%) — future",false,"Current C2 engine requires 0.1–10% slope. A genuinely flat roof is not implemented."),
  new RoofChoice("Arch","Curved timber arch — future",false,"No curved members or roof generator implemented."),
  new RoofChoice("Wave","Wave / freeform — future",false,"No wave/freeform roof generator implemented.")});
 public RoofChoice? SelectedRoof
 {
  get=>RoofChoices.FirstOrDefault(r=>r.Key==(model.Fields.Single(f=>f.Key=="Drainage").Value switch{"Outward"=>"Outward","TowardSpine"=>"Butterfly",_=>""}));
  set{if(value is null||ReferenceEquals(value,SelectedRoof))return;if(!RoofChoices.Contains(value)||!value.Enabled)throw new ArgumentException("Unsupported roof choice; current engine/schema are preserved.");model.Fields.Single(f=>f.Key=="Drainage").Value=value.Key=="Butterfly"?"TowardSpine":"Outward";}
 }
 public string RoofExplanation=>SelectedRoof?.Explanation??"Select a supported existing drainage mode.";
 public event PropertyChangedEventHandler? PropertyChanged;
 public event EventHandler? InputEdited;
 public InteractiveDesignerViewModel()
 {
  var equipmentDefaults=new EquipmentOptions();
  equipmentFields=typeof(EquipmentOptions).GetProperties().Select(p=>new CanopyField("Equipment"+p.Name,System.Text.RegularExpressions.Regex.Replace(p.Name,"([a-z])([A-Z])","$1 $2")+(p.PropertyType==typeof(int)?" (count)":" (m)"),p.PropertyType,p.GetValue(equipmentDefaults)!)).ToArray();
  EquipmentSettings=Array.AsReadOnly(equipmentFields.Select(f=>new SettingControl(f)).ToArray());
  foreach(var field in equipmentFields)field.PropertyChanged+=(_,_)=>Edited();
  Settings=Array.AsReadOnly(model.Site.Fields.Select(f=>new SettingControl(f)).Concat(model.Fields.Select(f=>new SettingControl(f))).ToArray());
  InputGroups=Array.AsReadOnly(GroupNames.Select((name,i)=>new InteractiveGroup(name,i==4?EquipmentSettings:Settings.Where(f=>GroupFor(f.Key)==i).ToArray(),i switch{
   0=>"C2 supports 2–1000 simultaneous points, one per stall. Identity, replacement and rotation controls remain explicit below.",
   1=>"Calculated two-row layout; explicit row counts must sum to the requested capacity. Precise inputs retain the full existing engine range.",
   2=>"Existing two-wing roof, independent support modes, material and section controls. Flat, curved and wave geometry are unavailable; no shape parameter is added to JSON.",
   3=>"Geometric whole-panel packing and rated DC kWp. Energy yield, AC output, inverters and electrical design are not implemented.",
   4=>"Offline physical candidate: one station pairs opposite-row spaces. Two independent outputs per station; connectors are configurable assumptions. Red geometry indicates unresolved clearance or cable conflicts. Power and power sharing remain unspecified. Equipment JSON is separate from canopy JSON.",
   5=>"Both outside aisles and end/side reservations remain configurable. Drainage dimensions are conceptual; circulation, turning radii and hydraulic sizing remain unverified.",
   _=>"Presentation only. These visibility preferences are not design inputs, are not exported, and never create or replace Revit geometry."},i==1,i==2,i==3,i==2,i==6,i==4)).ToArray());
  model.PropertyChanged+=(_,e)=>Notify(e.PropertyName);
  foreach(var field in model.Fields)field.PropertyChanged+=(_,_)=>{if(field.Key=="Drainage"){Notify(nameof(SelectedRoof));Notify(nameof(RoofExplanation));}Edited();};
  foreach(var field in model.Site.Fields)field.PropertyChanged+=(_,_)=>Edited();
  model.Site.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(PlannerViewModel.ExplicitRows))Edited();};
 }
 public static int GroupFor(string key)=>key=="points"?0:key is "width" or "length" or "spine" or "row1" or "row2"?1:
  key.StartsWith("Pv",StringComparison.Ordinal)||key=="EnablePv"?3:
  key is "aisle1" or "aisle2" or "end0" or "end1" or "side1" or "side2" or "ModelGutters"||key.StartsWith("Gutter",StringComparison.Ordinal)?5:2;
 private void Edited(){if(batch)return;InputRevision++;PreviewPending=true;Notify(nameof(IsCurrent));Notify(nameof(Status));NotifyEquipment();Notify(nameof(Design));InputEdited?.Invoke(this,EventArgs.Empty);}
 public void Calculate(){equipment=null;model.Calculate();CalculationCount++;PreviewPending=false;CalculateEquipment();Notify(nameof(Design));Notify(nameof(IsCurrent));Notify(nameof(Status));NotifyEquipment();}
 public void LoadConfiguration(DesignConfiguration config)
 {
  ConfigurationCodec.Validate(config);batch=true;
  try{model.LoadConfiguration(config);CalculationCount++;}finally{batch=false;}
  InputRevision++;PreviewPending=false;CalculateEquipment();NotifyEquipment();Notify(nameof(SelectedRoof));Notify(nameof(RoofExplanation));Notify(nameof(Design));Notify(nameof(IsCurrent));Notify(nameof(Status));Notify(nameof(EquipmentSummary));
 }
 private void Notify(string? name)=>PropertyChanged?.Invoke(this,new(name));
}
