using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;
using EVCanopyDesigner.Interactive.Revit;

namespace EVCanopyDesigner.Poc2.Revit;

public static class StaticSite
{
 public const string Owner="EVChargingHub.Poc2.StaticSite/1";
 public const string Label="POC 2 — Conceptual site layout / circulation not validated.";
 public const string Findings="Neighboring parked-car clearance conflict; pedestrian clearance/crossing concerns; road-alignment turns incomplete; full ten-space maneuvers unverified; 21 equipment-access conflicts unresolved; additional land assumed available. Schematic routes, NOT swept paths. Physical chargers DISABLED.";
 const string ConfigFingerprint="B6759D0DCF5EA3CEA989EF2B824F7890BB80F4E04FC94A6D9E46BC972F162B2F";
 const string EquipmentFingerprint="D890EF40E8DADA8000868C0A903E00ABA1B8A2F6B384D6888B8D50DAC0914D0E";
 static readonly Guid SchemaId=new("e61498fa-dc6e-4aa2-ac62-42419b5c582d");
 static CreatedModel? created;
 static HashSet<long>? initialElements;
 static bool attempted;
 static string Package=>Path.GetDirectoryName(typeof(StaticSite).Assembly.Location)!;
 static string VerifiedText(string file)
 {
  var hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Package,"sha256.json")))!;
  var bytes=File.ReadAllBytes(Path.Combine(Package,file));
  if(!hashes.TryGetValue(file,out var expected)||Convert.ToHexString(SHA256.HashData(bytes))!=expected)throw new InvalidOperationException("Package input changed: "+file);
  return System.Text.Encoding.UTF8.GetString(bytes);
 }
 static (DesignConfiguration Config,CanopyDesign Canopy,JsonDocument Scene) ReadInputs()
 {
  var config=ConfigurationCodec.Import(VerifiedText("selected-canopy.json"));
  if(config.Fingerprint!=ConfigFingerprint||config.RotationDegrees!=0)throw new InvalidOperationException("Only the verified selected canopy configuration is supported.");
  var canopy=CanopyEngine.Calculate(EVCanopyDesigner.Planner.Core.C2CapacityPlanner.Calculate(config.Site),config.Canopy);
  var equipment=EquipmentPlanner.Calculate(canopy,new());
  if(canopy.Components.Count!=144||canopy.SitePlan.Spaces.Count!=10||canopy.ColumnXs.Count!=4||equipment.Fingerprint!=EquipmentFingerprint||equipment.Issues.Count(i=>i.Blocking)!=21)throw new InvalidOperationException("Verified canopy/equipment reference changed.");
  var scene=JsonDocument.Parse(VerifiedText("proposal.json"));var s=scene.RootElement;
  if(s.GetProperty("BaseFingerprint").GetString()!=config.Fingerprint||s.GetProperty("CanopyFingerprint").GetString()!=canopy.Fingerprint||s.GetProperty("SiteLayoutApproved").GetBoolean()||s.GetProperty("HistoricalUnresolvedEquipmentAccessConflicts").GetInt32()!=21)throw new InvalidOperationException("Scene binding or limitations changed.");
  var site=s.GetProperty("Site");var road=s.GetProperty("Road");
  if(N(site,"X")!=-5||N(site,"Y")!=0||N(site,"Width")!=34.5||N(site,"Height")!=24.5||N(road,"X")!=-5||N(road,"Y")!=-7||N(road,"Width")!=34.5||N(road,"Height")!=7||s.GetProperty("Gateways").GetArrayLength()!=2||s.GetProperty("Gateways").EnumerateArray().Any(g=>N(g.GetProperty("Bounds"),"Width")!=6)||s.GetProperty("Spaces").GetArrayLength()!=10||s.GetProperty("Equipment").GetProperty("Stations").GetArrayLength()!=5)throw new InvalidOperationException("Accepted static site dimensions/counts changed.");
  using var review=JsonDocument.Parse(VerifiedText("maneuver-review.json"));
  if(review.RootElement.GetProperty("SiteLayoutApproved").GetBoolean()||review.RootElement.GetProperty("ExistingEquipmentAccessConflicts").GetInt32()!=21||!review.RootElement.GetProperty("AdditionalLandAssumedAvailable").GetBoolean())throw new InvalidOperationException("Final review limitations missing.");
  return(config,canopy,scene);
 }
 static double N(JsonElement e,string key){double n=e.GetProperty(key).GetDouble();if(!double.IsFinite(n))throw new InvalidOperationException("Non-finite scene coordinate.");return n;}
 static double Ft(double m)=>UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters);
 static XYZ Local(JsonElement e)=>new(N(e,"X"),N(e,"Y"),e.TryGetProperty("Z",out var z)?z.GetDouble():0);
 static void CheckRoot()
 {
  var settings=PocOperations.Settings;
  if(!string.Equals(Path.GetFullPath(settings.TestRoot),Path.Combine(Path.GetFullPath(settings.WorkspaceRoot),"Revit_Test_Models","POC2StaticSite"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Candidate must use its separate POC2StaticSite test root.");
 }
 public static string Create(UIApplication app)
 {
  using var scene=ReadInputs().Scene;CheckRoot();
  if(app.Application.Documents.Cast<Document>().Any())throw new InvalidOperationException("Close all open models manually before creating this isolated disposable. No existing document will be closed or adopted.");
  if(created is not null)throw new InvalidOperationException("This candidate already created a disposable in this session. Restart before another candidate run.");
  created=PocOperations.CreateDisposable(app);
  initialElements=new FilteredElementCollector(PocOperations.RequireDocument(app)).WhereElementIsNotElementType().Select(e=>e.Id.Value).ToHashSet();
  return "NEW POC 2 disposable only: "+created.ModelPath+"\nConfirm this exact path and activate its floor plan before Generate. No canopy/site generated yet.";
 }
 static Document RequireTarget(UIApplication app)
 {
  CheckRoot();var expected=created??throw new InvalidOperationException("Run this candidate's NEW disposable command first; existing projects cannot be adopted.");
  var doc=PocOperations.RequireDocument(app);
  if(!string.Equals(doc.PathName,expected.ModelPath,StringComparison.OrdinalIgnoreCase)||doc.ProjectInformation.UniqueId!=expected.ProjectUniqueId)throw new InvalidOperationException("Target changed.");
  return doc;
 }
 public static string Generate(UIApplication app)
 {
  var (config,canopy,scene)=ReadInputs();using var input=scene;
  var doc=RequireTarget(app);
  var currentElements=new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e=>e.Id.Value).ToHashSet();
  if(attempted||doc.IsModified||initialElements is null||!initialElements.SetEquals(currentElements)||new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Any()||new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Any())throw new InvalidOperationException("Only the unchanged fresh disposable is accepted; no edits, retry, replacement or cleanup operation is provided.");
  var planLevel=doc.ActiveView.GenLevel??throw new InvalidOperationException("Activate the new disposable's floor plan first.");
  var s=scene.RootElement;var origin=Local(s.GetProperty("Options").GetProperty("VerifiedGenerationOriginMetres"));
  // Origin Z is tied to the actual new model level, not copied from an old model's elevation.
  var basePoint=new XYZ(Ft(origin.X),Ft(origin.Y),planLevel.Elevation);
  var session=PocOperations.NewSession(app);
  var review=session.Prepare(IntegrationScope.CanopyOnly,new GenerationRequest(canopy,null,config.RotationDegrees,Guid.NewGuid().ToString("D")),new EquipmentOptions(),true);
  var ticket=session.Validate(review);
  if(review.Equipment.BlockingConflicts!=21)throw new InvalidOperationException("Equipment conflict evidence changed.");
  // Consumes the existing single-use preflight through PocOperations.Generate; site writes
  // are a distinct stage, never a CombinedSite handoff through the canopy-only adapter.
  attempted=true;string run=Guid.NewGuid().ToString("N"),receipt=Path.Combine(PocOperations.EvidenceDirectory,"poc2-site-result.json");
  if(File.Exists(receipt))throw new InvalidOperationException("Receipt already exists.");
  var made=new List<Element>();string? designId=null;bool committed=false;
  using var group=new TransactionGroup(doc,"POC 2: one canopy and static site — unvalidated");
  if(group.Start()!=TransactionStatus.Started)throw new InvalidOperationException("Could not start site transaction group.");
  try
  {
   RequireTarget(app);
   designId=PocOperations.Generate(app,session,new PendingCanopyReview(review,ticket,true),basePoint).Id;
   RequireTarget(app);
   using(var tx=new Transaction(doc,"POC 2 static site, reservations and views"))
   {
    if(tx.Start()!=TransactionStatus.Started)throw new InvalidOperationException("Site transaction refused.");
    var failures=tx.GetFailureHandlingOptions();failures.SetFailuresPreprocessor(new StopOnFailure());failures.SetClearAfterRollback(true);tx.SetFailureHandlingOptions(failures);
    var schema=Schema.Lookup(SchemaId);
    if(schema is null){var builder=new SchemaBuilder(SchemaId);builder.SetSchemaName("EVHubPoc2StaticSiteV1");builder.SetReadAccessLevel(AccessLevel.Public);builder.SetWriteAccessLevel(AccessLevel.Public);builder.AddSimpleField("Receipt",typeof(string));schema=builder.Finish();}
    void Tag(Element e,string role,string key)
    {
     var entity=new Entity(schema);entity.Set("Receipt",JsonSerializer.Serialize(new{Owner,Run=run,Role=role,Key=key,Configuration=config.Fingerprint,SiteLayoutApproved=false}));e.SetEntity(entity);
     e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("EVPOC2-"+run+"-"+key);
     e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Label+" "+Findings);made.Add(e);
    }
    var materials=new Dictionary<string,ElementId>();
    ElementId MaterialFor(string role,Color color)
    {
     if(materials.TryGetValue(role,out var existing))return existing;
     var id=Material.Create(doc,"EVPOC2-"+run+"-"+role);var material=(Material)doc.GetElement(id);material.Color=color;material.Transparency=role=="service-reservation"?45:0;Tag(material,"Material",role);materials[role]=id;return id;
    }
    XYZ Point(double x,double y,double z)=>basePoint+new XYZ(Ft(x),Ft(y),Ft(z));
    DirectShape Shape(string role,string key,IEnumerable<(double X,double Y)> corners,double z,double thickness,Color color)
    {
     var ps=corners.Select(p=>Point(p.X,p.Y,z)).ToArray();var loop=new CurveLoop();for(int i=0;i<ps.Length;i++)loop.Append(Line.CreateBound(ps[i],ps[(i+1)%ps.Length]));
     var solid=GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,Ft(thickness),new SolidOptions(MaterialFor(role,color),ElementId.InvalidElementId));
     var ds=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));ds.ApplicationId=Owner;ds.ApplicationDataId=run+":"+key;ds.Name=role+" — "+key;ds.SetShape(new GeometryObject[]{solid});Tag(ds,role,key);return ds;
    }
    void Box(JsonElement b,string role,string key,double z,double depth,Color color)=>Shape(role,key,new[]{(N(b,"X"),N(b,"Y")),(N(b,"Right"),N(b,"Y")),(N(b,"Right"),N(b,"Top")),(N(b,"X"),N(b,"Top"))},z,depth,color);
    void Segment(XYZ a,XYZ b,string role,string key,double width,double z,Color color)
    {
     var v=b-a;double length=Math.Sqrt(v.X*v.X+v.Y*v.Y);if(length<.001)return;double dx=-v.Y/length*width/2,dy=v.X/length*width/2;
     Shape(role,key,new[]{(a.X+dx,a.Y+dy),(b.X+dx,b.Y+dy),(b.X-dx,b.Y-dy),(a.X-dx,a.Y-dy)},z,.015,color);
    }
    var roadColor=new Color(75,85,100);var pavement=new Color(150,160,170);var green=new Color(60,170,110);var amber=new Color(235,150,45);
    // Surface tops remain below the unchanged canopy's parking marking geometry.
    Box(s.GetProperty("Site"),"site-base","property",-.14,.06,new Color(190,205,180));
    Box(s.GetProperty("Road"),"public-road","road",-.08,.05,roadColor);
    foreach(var surface in s.GetProperty("Surfaces").EnumerateArray())Box(surface.GetProperty("Bounds"),"circulation-surface",surface.GetProperty("Id").GetString()!,-.07,.05,pavement);
    var boundary=s.GetProperty("Boundary").EnumerateArray().Select(Local).ToArray();for(int i=1;i<boundary.Length;i++)Segment(boundary[i-1],boundary[i],"boundary","boundary-"+i,.08,.02,new Color(40,120,60));
    foreach(var gate in s.GetProperty("Gateways").EnumerateArray())Box(gate.GetProperty("Bounds"),"gateway",gate.GetProperty("Id").GetString()!,.025,.02,new Color(40,120,210));
    int routeIndex=0;
    foreach(var space in s.GetProperty("Spaces").EnumerateArray())foreach(string phase in new[]{"Inbound","ReverseOut","Outbound"})
    {
     var ps=space.GetProperty(phase).EnumerateArray().Select(Local).ToArray();var color=phase=="ReverseOut"?amber:phase=="Inbound"?new Color(35,110,210):new Color(160,65,190);
     for(int i=1;i<ps.Length;i++)Segment(ps[i-1],ps[i],"schematic-route","route-"+(routeIndex++)+"-"+i,.075,.07,color);
     var delta=ps[^1]-ps[^2];var tangent=delta.Normalize();var p=ps[^1];var back=p-tangent*.45;var side=new XYZ(-tangent.Y,tangent.X,0)*.22;
     Shape("schematic-arrow","arrow-"+routeIndex,new[]{(p.X,p.Y),(back.X+side.X,back.Y+side.Y),(back.X-side.X,back.Y-side.Y)},.09,.015,color);
    }
    int ped=0;foreach(var path in s.GetProperty("PedestrianRoutes").EnumerateArray())
    {
     var ps=path.GetProperty("Points").EnumerateArray().Select(Local).ToArray();for(int i=1;i<ps.Length;i++)Segment(ps[i-1],ps[i],"pedestrian-reservation","ped-"+(ped++)+"-"+i,.9,.035,green);
    }
    int cross=0;foreach(var crossing in s.GetProperty("Crossings").EnumerateArray())Box(crossing,"marked-crossing","crossing-"+cross++,.055,.015,new Color(240,215,90));
    foreach(var station in s.GetProperty("Equipment").GetProperty("Stations").EnumerateArray())Box(station.GetProperty("ServiceZone"),"service-reservation","reservation-"+station.GetProperty("Id").GetString(),.04,.015,amber);
    var plan=ViewPlan.Create(doc,doc.GetElement(doc.ActiveView.GetTypeId()).Id,planLevel.Id);plan.Name="POC 2 - Site Plan";plan.Scale=100;plan.DetailLevel=ViewDetailLevel.Fine;Tag(plan,"View","plan");
    var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().Single(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
    var view=View3D.CreateIsometric(doc,type.Id);view.Name="POC 2 - Conceptual Site 3D";view.DisplayStyle=DisplayStyle.FlatColors;view.DetailLevel=ViewDetailLevel.Fine;Tag(view,"View","3d");
    var viewBox=new BoundingBoxXYZ{Min=Point(-7,-9,-1),Max=Point(32,27,9)};view.SetSectionBox(viewBox);
    var forward=new XYZ(-1,1,-1).Normalize();var right=forward.CrossProduct(XYZ.BasisZ).Normalize();view.SetOrientation(new ViewOrientation3D(Point(38,-30,25),right.CrossProduct(forward).Normalize(),forward));
    view.SaveOrientationAndLock();
    // View-only covering hide: never alter or delete canopy components.
    var hide=new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().Where(e=>e.ApplicationId==OwnershipPolicy.Owner&&(e.Name.Contains("RoofPanel",StringComparison.Ordinal)||e.Name.Contains("PvPanel",StringComparison.Ordinal)||e.Name.Contains("GlulamArm",StringComparison.Ordinal)||e.Name.Contains("GlulamPurlin",StringComparison.Ordinal)||e.Name.Contains("Gutter",StringComparison.Ordinal))).Select(e=>e.Id).ToList();plan.HideElements(hide);
    var footprint=s.GetProperty("Canopy");double fx=N(footprint,"X"),fy=N(footprint,"Y"),fr=N(footprint,"Right"),ft=N(footprint,"Top");
    var cp=new[]{new XYZ(fx,fy,0),new XYZ(fr,fy,0),new XYZ(fr,ft,0),new XYZ(fx,ft,0),new XYZ(fx,fy,0)};
    for(int i=1;i<cp.Length;i++){var curve=doc.Create.NewDetailCurve(plan,Line.CreateBound(Point(cp[i-1].X,cp[i-1].Y,0),Point(cp[i].X,cp[i].Y,0)));Tag(curve,"PlanFootprint","footprint-"+i);}
    var textType=new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();if(textType==ElementId.InvalidElementId)throw new InvalidOperationException("New document has no text type; generation rolls back.");
    foreach(var target in new View[]{plan,view})
    {
     target.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.Set(Label);
     var note=TextNote.Create(doc,target.Id,Point(-5,-8,0),.8,Label+"\r"+Findings,textType);Tag(note,"Warning",target.Id.Value.ToString());
     foreach(var gate in s.GetProperty("Gateways").EnumerateArray()){var b=gate.GetProperty("Bounds");var text=TextNote.Create(doc,target.Id,Point(N(b,"X"),-1,0),gate.GetProperty("Id").GetString()!.ToUpperInvariant()+" — 6 m (schematic)",textType);Tag(text,"GatewayLabel",target.Id.Value+"-"+gate.GetProperty("Id").GetString());}
    }
    doc.ProjectInformation.Name=Label;doc.ProjectInformation.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Findings);
    var registry=DataStorage.Create(doc);registry.Name="POC2 static site receipt "+run;Tag(registry,"Registry","site-registry");
    doc.Regenerate();
    if(new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().Count(e=>e.ApplicationId==OwnershipPolicy.Owner)!=144)throw new InvalidOperationException("Exactly one 144-component canopy required.");
    foreach(var element in made)if(!element.GetEntity(schema).IsValid())throw new InvalidOperationException("Missing site ownership tag.");
    if(!PocOperations.Validate(app).CanProceed)throw new InvalidOperationException("Existing canopy validation failed after site creation.");
    if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Site transaction rolled back.");
   }
   RequireTarget(app);
   if(group.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("Site group did not commit.");committed=true;
   // Save only after exact disposable revalidation. A failed save leaves a clearly reported
   // committed-but-unsaved disposable; do not blindly regenerate or adopt it in another session.
   RequireTarget(app).Save();
   WriteReceipt("CommittedAndSaved",null);
   // A UI request failure must not rewrite the already-finalized receipt or suggest retrying generation.
   try{var targetView=new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Single(v=>v.Name=="POC 2 - Conceptual Site 3D");app.ActiveUIDocument.RequestViewChange(targetView);}catch(InvalidOperationException){/* Model is saved; activate the generated view manually. */}
   return "STATIC POC 2 SAVED: "+doc.PathName+"\n"+Label+"\n"+Findings+"\nReceipt: "+receipt;
  }
  catch(Exception e)
  {
   if(group.GetStatus()==TransactionStatus.Started)group.RollBack();
   WriteReceipt(committed?"Committed_SaveOrEvidenceFailed":"RolledBack",e.ToString());throw;
  }
  void WriteReceipt(string status,string? error)
  {
   var data=new{Status=status,ModelPath=doc.PathName,ProjectUniqueId=doc.ProjectInformation.UniqueId,Owner,Run=run,DesignId=designId,Configuration=config.Fingerprint,Label,Findings,EquipmentGenerated=false,SiteLayoutApproved=false,ConflictCount=21,Elements=committed?made.Select(e=>new{Id=e.Id.Value,e.UniqueId,e.Name}).ToArray():null,Error=error,Note="Canopy generation JSON describes an inner transaction only; this receipt is the final group/save outcome. No automatic retry."};
   using var stream=new FileStream(receipt,FileMode.CreateNew);JsonSerializer.Serialize(stream,data,new JsonSerializerOptions{WriteIndented=true});
  }
 }
 sealed class StopOnFailure:IFailuresPreprocessor
 {
  public FailureProcessingResult PreprocessFailures(FailuresAccessor f)=>f.GetFailureMessages().Count>0?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
 }
}

[Transaction(TransactionMode.Manual)] public sealed class NewDisposableCommand:IExternalCommand
{
 public Result Execute(ExternalCommandData data,ref string message,ElementSet elements){try{TaskDialog.Show("POC 2 — NEW disposable",StaticSite.Create(data.Application));return Result.Succeeded;}catch(Exception e){message=e.ToString();TaskDialog.Show("POC 2 stopped safely",message);return Result.Failed;}}
}
[Transaction(TransactionMode.Manual)] public sealed class GenerateStaticSiteCommand:IExternalCommand
{
 public Result Execute(ExternalCommandData data,ref string message,ElementSet elements){try{TaskDialog.Show("POC 2 — static demonstration",StaticSite.Generate(data.Application));return Result.Succeeded;}catch(Exception e){message=e.ToString();TaskDialog.Show("POC 2 stopped safely",message);return Result.Failed;}}
}
