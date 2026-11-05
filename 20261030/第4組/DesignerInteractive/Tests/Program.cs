using System.Globalization;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Planner.Core;

int passed=0,failed=0;
void Check(bool value,string message="Assertion failed"){if(!value)throw new Exception(message);}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
SettingControl Field(InteractiveDesignerViewModel vm,string key)=>vm.Settings.Single(f=>f.Key==key);
string Export(InteractiveDesignerViewModel vm,double rotation=0)=>ConfigurationCodec.Export(ConfigurationCodec.Create(vm.Design!.SitePlan.Inputs,vm.Design.Inputs,rotation));
void Reject(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Expected rejection");}

Test("Seven groups cover every existing field exactly once",()=>{
 var vm=new InteractiveDesignerViewModel();Check(vm.InputGroups.Select(g=>g.Name).SequenceEqual(InteractiveDesignerViewModel.GroupNames));
 var original=new CanopyViewModel();var keys=original.Site.Fields.Select(f=>f.Key).Concat(original.Fields.Select(f=>f.Key)).Order().ToArray();
 Check(vm.InputGroups.Where(g=>g.Name!="EV Charging Equipment").SelectMany(g=>g.Fields).Select(f=>f.Key).Order().SequenceEqual(keys));Check(vm.Settings.Select(f=>f.Key).Distinct().Count()==keys.Length);
 Check(vm.InputGroups[4].Fields.Count==15&&vm.InputGroups[6].Fields.Count==0);Check(vm.InputGroups[4].Information.Contains("offline",StringComparison.OrdinalIgnoreCase));
});
Test("Defaults identical to the unchanged baseline view model",()=>{
 var original=new CanopyViewModel();original.Calculate();var vm=new InteractiveDesignerViewModel();vm.Calculate();
 Check(vm.Design!.Fingerprint==original.Design!.Fingerprint);Check(vm.Design.Schedule.SequenceEqual(original.Design.Schedule));Check(vm.MaterialTakeoff.SequenceEqual(original.MaterialTakeoff));
});
Test("Edit invalidates immediately, requests preview only, and performs no calculation itself",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();int requested=0;vm.InputEdited+=(_,_)=>requested++;
 Field(vm,"points").Value="11";Check(!vm.IsCurrent&&vm.Design is null&&vm.DesignSummary.Count==0&&vm.MaterialTakeoff.Count==0);Check(vm.PreviewPending&&requested==1&&vm.CalculationCount==1);
 vm.Calculate();Check(vm.IsCurrent&&!vm.PreviewPending&&vm.Design!.SitePlan.Spaces.Count==11&&vm.CalculationCount==2);
});
Test("Empty, fractional capacity and NaN preserve raw edits and hide invalid previews",()=>{
 var vm=new InteractiveDesignerViewModel();foreach(var value in new[]{"","11.5","NaN"}){Field(vm,"points").Value=value;vm.Calculate();Check(!vm.IsCurrent&&vm.Design is null);Check(Field(vm,"points").Value==value&&!Field(vm,"points").SliderEnabled&&Field(vm,"points").InputError.Length>0);}
});
Test("Culture-aware text and slider values",()=>{
 var prior=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");var vm=new InteractiveDesignerViewModel();Field(vm,"RoofDatum").Value="5,8";vm.Calculate();Check(vm.IsCurrent&&vm.Design!.Inputs.RoofDatum==5.8);Field(vm,"RoofDatum").SetFromSlider(6.2);Check(Field(vm,"RoofDatum").Value=="6,2");vm.Calculate();Check(vm.IsCurrent);}finally{CultureInfo.CurrentCulture=prior;}
});
Test("Typed/imported values retain precision and expand the slider without clamping",()=>{
 var vm=new InteractiveDesignerViewModel();Field(vm,"RoofDatum").Value="25.1234567890123";Check(Field(vm,"RoofDatum").SliderMaximum>25&&Field(vm,"RoofDatum").Value=="25.1234567890123");
 vm.Calculate();Check(vm.IsCurrent&&vm.Design!.Inputs.RoofDatum==25.1234567890123);Field(vm,"points").SetFromSlider(17.4);Check(Field(vm,"points").Value=="17");
});
Test("Presentation changes do not dirty, recalculate or change JSON/fingerprint",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();string json=Export(vm);int count=vm.CalculationCount;long revision=vm.InputRevision;vm.Visualization.ShowPv=false;vm.Visualization.ShowRoof=false;vm.Visualization.ShowStructure=false;
 Check(vm.IsCurrent&&vm.CalculationCount==count&&vm.InputRevision==revision&&Export(vm)==json);
});
Test("Butterfly is the existing TowardSpine mode, not a new geometry engine",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();double slope=vm.Design!.Inputs.SlopePercent;vm.SelectedRoof=vm.RoofChoices.Single(r=>r.Key=="Butterfly");Check(!vm.IsCurrent);vm.Calculate();
 Check(vm.IsCurrent&&vm.Design!.Inputs.Drainage==DrainageDirection.TowardSpine&&vm.Design.Inputs.SlopePercent==slope);
 var expected=CanopyEngine.Calculate(vm.Design.SitePlan,vm.Design.Inputs);Check(expected.Fingerprint==vm.Design.Fingerprint&&expected.Schedule.SequenceEqual(vm.Design.Schedule));
 Check(vm.Design.Components.Count(c=>c.Role==ComponentRole.Gutter)==1);Check(vm.RoofChoices.Count(r=>r.Enabled)==2);
});
Test("Outward mode remains available and restores the original roof",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();string before=vm.Design!.Fingerprint;vm.SelectedRoof=vm.RoofChoices.Single(r=>r.Key=="Butterfly");vm.Calculate();vm.SelectedRoof=vm.RoofChoices.Single(r=>r.Key=="Outward");vm.Calculate();Check(vm.Design!.Fingerprint==before);
});
Test("Flat, arch and wave are disabled and programmatic selection is rejected",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();string json=Export(vm);foreach(var choice in vm.RoofChoices.Where(r=>!r.Enabled)){Reject(()=>vm.SelectedRoof=choice);Check(Export(vm)==json);}
 Reject(()=>CanopyEngine.Calculate(C2CapacityPlanner.Calculate(new()),new(){SlopePercent=0}));
});
Test("Engine clearance and out-of-range slope remain blocking",()=>{
 var vm=new InteractiveDesignerViewModel();Field(vm,"RoofDatum").Value="4.6";vm.Calculate();Check(!vm.IsCurrent&&vm.Design is null&&vm.Status.Contains("Clearance conflict"));Field(vm,"RoofDatum").Value="5.4";Field(vm,"SlopePercent").Value="0";vm.Calculate();Check(!vm.IsCurrent);
});
Test("Schema-1 round trip preserves exact full-precision configuration and rotation",()=>{
 foreach(var direction in Enum.GetValues<DrainageDirection>()){
  var c=ConfigurationCodec.Create(new(RequestedPoints:17,Row1Points:10,Row2Points:7,StallWidth:3,StallLength:6),new(){Drainage=direction,RoofDatum=5.8765432109876,ColumnMode=ColumnMode.Manual,ManualColumnOffsets=new[]{1.0,6.0,11.0},PvExclusions=new[]{new RectM(8,8,1,1)}},37.123456789);
  var vm=new InteractiveDesignerViewModel();vm.LoadConfiguration(ConfigurationCodec.Import(ConfigurationCodec.Export(c)));Check(vm.IsCurrent);Check(Export(vm,c.RotationDegrees)==ConfigurationCodec.Export(c));
  Check(vm.SelectedRoof!.Key==(direction==DrainageDirection.TowardSpine?"Butterfly":"Outward"));
 }
});
Test("Import is batched and invalid import preserves the last valid proposal",()=>{
 var vm=new InteractiveDesignerViewModel();int edits=0;vm.InputEdited+=(_,_)=>edits++;var config=ConfigurationCodec.Create(new(),new(),0);vm.LoadConfiguration(config);Check(edits==0&&vm.CalculationCount==1&&vm.IsCurrent);string json=Export(vm);
 Reject(()=>vm.LoadConfiguration(config with{Fingerprint="stale"}));Check(Export(vm)==json&&vm.IsCurrent);
});
Test("Explicit rows and changed capacity continue to use Planner validation",()=>{
 var vm=new InteractiveDesignerViewModel();Field(vm,"points").Value="17";Field(vm,"row1").Value="10";Field(vm,"row2").Value="7";vm.Site.ExplicitRows=true;vm.Calculate();Check(vm.IsCurrent&&vm.Design!.SitePlan.Row1Count==10&&vm.Design.SitePlan.Row2Count==7);
 Field(vm,"row2").Value="6";vm.Calculate();Check(!vm.IsCurrent&&vm.Status.Contains("sum"));
});
Test("Capacity slider retains the original 2–1000 capability and fractional text is not coerced",()=>{
 var vm=new InteractiveDesignerViewModel();Check(Field(vm,"points").SliderMinimum==2&&Field(vm,"points").SliderMaximum==1000);Field(vm,"points").Value="1000";Check(Field(vm,"points").Value=="1000");
 Check(C2CapacityPlanner.Calculate(new(RequestedPoints:1000)).Spaces.Count==1000);Field(vm,"points").Value="2.5";Field(vm,"points").SetFromSlider(3);Check(Field(vm,"points").Value=="2.5");
});
Test("Manual supports, PV exclusions and disabled PV use the existing ledger",()=>{
 var vm=new InteractiveDesignerViewModel();Field(vm,"ColumnMode").Value="Manual";vm.ManualOffsets="1;6;11";vm.Exclusions="7;7;2;2";vm.Calculate();Check(vm.IsCurrent&&vm.Design!.ColumnXs.Count==3);Field(vm,"EnablePv").Value="False";vm.Calculate();Check(vm.IsCurrent&&vm.Design!.PvKilowatts==0&&!vm.MaterialTakeoff.Any(r=>r.Category=="PvPanel"));
});
Test("No Revit or MCP assembly is referenced by the presentation project",()=>{
 Check(typeof(InteractiveDesignerViewModel).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.Contains("Revit",StringComparison.OrdinalIgnoreCase)&&!a.Name.Contains("MCP",StringComparison.OrdinalIgnoreCase)));
});
Test("Default equipment has five physical stations, ten outputs, exact opposite-row assignments",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var e=vm.Equipment!;
 Check(e.PhysicalStations==5&&e.InstalledOutputs==10&&e.AssignedPoints==10&&e.PhysicalConnectors==10&&e.SpareOutputs==0);
 Check(e.Stations.Select(s=>s.Id).SequenceEqual(new[]{"S1","S2","S3","S4","S5"}));
 for(int k=0;k<5;k++){Check(e.Stations[k].Outputs[0].SpaceId==$"R1-S{k+1:0000}");Check(e.Stations[k].Outputs[1].SpaceId==$"R2-S{k+1:0000}");}
 Check(e.Stations.SelectMany(s=>s.Outputs).Select(o=>o.SpaceId).Distinct().Count()==10);
});
Test("Default spine candidacy reports access and service conflicts without moving canopy",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var e=vm.Equipment!;
 Check(!e.Passed&&e.Issues.Any(i=>i.Code=="PedestrianSupport")&&e.Issues.Any(i=>i.Code=="ServiceSolid"));
 Check(!e.Issues.Any(i=>i.Code=="StationSolid"||i.Code=="StationRegion"||i.Code=="CableReach"));
 var baseline=new CanopyViewModel();baseline.Calculate();Check(vm.Design!.Fingerprint==baseline.Design!.Fingerprint&&vm.MaterialTakeoff.SequenceEqual(baseline.MaterialTakeoff));
 Check(e.Stations.All(s=>s.Outputs.All(o=>o.ReachPassed)));
});
Test("Odd balanced counts leave exactly one spare output and never invent a point",()=>{
 foreach(int count in new[]{3,9,11,17}){var vm=new InteractiveDesignerViewModel();Field(vm,"points").Value=count.ToString();vm.Calculate();var e=vm.Equipment!;Check(e.PhysicalStations==(count+1)/2&&e.InstalledOutputs==count+1&&e.AssignedPoints==count&&e.SpareOutputs==1);Check(e.Stations.SelectMany(s=>s.Outputs).Count(o=>o.SpaceId is null)==1);}
});
Test("Explicit asymmetric rows report extra hardware and preserve every assignment",()=>{
 var vm=new InteractiveDesignerViewModel();Field(vm,"points").Value="10";Field(vm,"row1").Value="8";Field(vm,"row2").Value="2";vm.Site.ExplicitRows=true;vm.Calculate();var e=vm.Equipment!;Check(e.PhysicalStations==8&&e.AssignedPoints==10&&e.SpareOutputs==6&&!e.Passed);Check(e.Issues.Any(i=>i.Code=="AsymmetricRows"));
});
Test("Station-body and maintenance collisions are separate blocking checks",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var d=vm.Design!;var e=EquipmentPlanner.Calculate(d,new(){OffsetX=-1});Check(e.Issues.Any(i=>i.Code=="StationSolid"));
 var wide=EquipmentPlanner.Calculate(d,new(){Width=3});Check(wide.Issues.Any(i=>i.Code=="StationStation"));
 var service=EquipmentPlanner.Calculate(d,new(){ServiceMargin=1.2});Check(service.Issues.Any(i=>i.Code=="ServiceService"));
});
Test("Spine, stall, aisle and site departures are rejected geometrically",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var d=vm.Design!;
 var stall=EquipmentPlanner.Calculate(d,new(){OffsetY=1});Check(stall.Issues.Any(i=>i.Code=="StationBoundary")&&stall.Issues.Any(i=>i.Code=="StationRegion"&&i.Detail.Contains("Parking")));
 var aisle=EquipmentPlanner.Calculate(d,new(){OffsetY=7});Check(aisle.Issues.Any(i=>i.Code=="StationRegion"&&i.Detail.Contains("OutsideAisle")));
 var outside=EquipmentPlanner.Calculate(d,new(){OffsetX=50});Check(outside.Issues.Any(i=>i.Code=="StationBoundary"));
});
Test("Cable lengths use 3D inlet/output height plus slack and enforce threshold",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var d=vm.Design!;var e=EquipmentPlanner.Calculate(d,new());var o=e.Stations[0].Outputs[0];
 double expected=Math.Sqrt(1.55*1.55+.2*.2)+.5;Check(Math.Abs(o.RequiredCable-expected)<1e-9);
 Check(!EquipmentPlanner.Calculate(d,new(){CableLength=expected}).Issues.Any(i=>i.Code=="CableReach"));
 Check(EquipmentPlanner.Calculate(d,new(){CableLength=expected-.001}).Issues.Count(i=>i.Code=="CableReach")==10);
 Check(EquipmentPlanner.Calculate(d,new(){InletSetback=8}).Issues.Any(i=>i.Code=="InletOutsideStall"));
 Check(EquipmentPlanner.Calculate(d,new(){InletOffsetX=2}).Issues.Any(i=>i.Code=="InletOutsideStall"));
});
Test("Cables crossing supports and pedestrian strips are reported",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var e=EquipmentPlanner.Calculate(vm.Design!,new(){OffsetX=-1,OffsetY=-.5,InletOffsetX=-1});
 Check(e.Issues.Any(i=>i.Code=="CableObstacle")&&e.Issues.Any(i=>i.Code=="CablePedestrian"));
 Check(EquipmentPlanner.SegmentHits(0,1,3,1,new(1,0,1,2)));Check(!EquipmentPlanner.SegmentHits(0,0,3,0,new(1,1,1,2)));
 Check(!EquipmentPlanner.Overlap(new(0,0,1,1),new(1,0,1,1)));
});
Test("Elevated roof projections do not masquerade as ground-equipment collisions",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var e=vm.Equipment!;Check(!e.Issues.Any(i=>i.Code=="StationSolid"&&i.Detail.Contains("PvPanel")));
 Check(EquipmentPlanner.Calculate(vm.Design!,new(){Height=6,ServiceHeight=7}).Issues.Any(i=>i.Code=="StationSolid"));
});
Test("Roomier spine alone cannot establish cable-free access or certification",()=>{
 var d=CanopyEngine.Calculate(C2CapacityPlanner.Calculate(new(SpineWidth:4)),new());
 var e=EquipmentPlanner.Calculate(d,new(){OffsetY=-1,PedestrianWidth=.5,ServiceMargin=.05,InletSetback=0,CableLength=5});
 // Cables to row B still cross the reserved strip; a cable-free continuous strip cannot be claimed.
 Check(!e.Passed&&e.Issues.Any(i=>i.Code=="CablePedestrian"));Check(e.Issues.Any(i=>!i.Blocking&&i.Code=="Assumptions"));
});
Test("Invalid/nonfinite equipment configuration never produces accepted geometry",()=>{
 foreach(var o in new EquipmentOptions[]{new(){Width=0},new(){Depth=-1},new(){Height=double.NaN},new(){OffsetX=double.PositiveInfinity},new(){ServiceMargin=-.1},new(){ConnectorsPerOutput=0},new(){OutputHeight=2},new(){CableLength=0},new(){PedestrianWidth=0},new(){ServiceHeight=1}})Reject(()=>EquipmentPlanner.Validate(o));
 var vm=new InteractiveDesignerViewModel();vm.Calculate();vm.EquipmentSettings.Single(f=>f.Key=="EquipmentWidth").Value="NaN";Check(vm.Equipment is null&&vm.Design is null);vm.Calculate();Check(vm.IsCurrent&&vm.Equipment is null&&vm.EquipmentValidation.Contains("INVALID"));
});
Test("Independent equipment edits and connector assumptions preserve canopy JSON and quantities",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();string json=Export(vm),fp=vm.Equipment!.Fingerprint;var quantities=vm.MaterialTakeoff.ToArray();
 vm.SetEquipmentOptions(new(){OffsetX=.1234567890123,ConnectorsPerOutput=2});Check(vm.Equipment is null&&vm.PreviewPending);vm.Calculate();Check(Export(vm)==json&&vm.MaterialTakeoff.SequenceEqual(quantities));Check(vm.Equipment!.Fingerprint!=fp&&vm.Equipment.PhysicalConnectors==20&&vm.Equipment.InstalledOutputs==10&&vm.Equipment.AssignedPoints==10);
});
Test("Strict equipment JSON round trip, tamper, schema, missing fields and canopy binding",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();vm.SetEquipmentOptions(new(){OffsetY=.1234567890123});vm.Calculate();string json=vm.ExportEquipment(),canopy=Export(vm),fp=vm.Equipment!.Fingerprint;
 vm.ImportEquipment(json);Check(vm.ExportEquipment()==json&&Export(vm)==canopy&&vm.Equipment!.Fingerprint==fp);
 Reject(()=>vm.ImportEquipment(json.Replace("\"Schema\": 1","\"Schema\": 2")));
 Reject(()=>vm.ImportEquipment(json.Replace("\"Width\": 0.4","\"Width\": 0.5")));
 Reject(()=>vm.ImportEquipment(json.Replace("\"Width\": 0.4,","")));
 Reject(()=>vm.ImportEquipment(json.Replace("\"Units\": \"m\"","\"Units\": \"mm\"")));
 Reject(()=>vm.ImportEquipment(json.Replace("\"Width\": 0.4","\"Width\": 0.4, \"Unknown\": 1")));
 Reject(()=>vm.ImportEquipment(json.Replace("\"Width\": 0.4","\"Width\": 0.4, \"Width\": 0.4")));
 Reject(()=>EquipmentCodec.Import(json,"different canopy"));Check(vm.ExportEquipment()==json);
 Field(vm,"points").Value="11";vm.Calculate();Reject(()=>vm.ImportEquipment(json));Check(vm.Equipment!.AssignedPoints==11);
});
Test("Equipment result collections are read-only and deterministic",()=>{
 var vm=new InteractiveDesignerViewModel();vm.Calculate();var a=vm.Equipment!;var b=EquipmentPlanner.Calculate(vm.Design!,new());Check(a.Fingerprint==b.Fingerprint&&a.Issues.SequenceEqual(b.Issues));
 Check(((IList<StationPlacement>)a.Stations).IsReadOnly&&((IList<OutputAssignment>)a.Stations[0].Outputs).IsReadOnly&&((IList<EquipmentIssue>)a.Issues).IsReadOnly);
});
Console.WriteLine($"{passed} passed; {failed} failed. Offline presentation logic only.");
return failed==0?0:1;
