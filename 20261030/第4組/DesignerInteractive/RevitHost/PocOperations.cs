using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.Revit;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;
namespace EVCanopyDesigner.Interactive.Revit;

public sealed record CandidateSettings(string PackageId,string WorkspaceRoot,string TestRoot);
public sealed record CreatedModel(string RunId,string ModelPath,string ProjectUniqueId,string SnapshotPath,string SnapshotSha256,string CreatedUtc);
internal sealed class ProcessSetting : IDisposable
{
 readonly string name;readonly string? old;
 internal ProcessSetting(string name,string? value){this.name=name;old=Environment.GetEnvironmentVariable(name);Environment.SetEnvironmentVariable(name,value);}
 public void Dispose()=>Environment.SetEnvironmentVariable(name,old);
}

public static class PocOperations
{
 private static CreatedModel? created;
 internal static RunDiagnostics? LastRun {get;private set;}
 public static CandidateSettings Settings=>JsonSerializer.Deserialize<CandidateSettings>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(PocOperations).Assembly.Location)!,"candidate-settings.json")))??throw new InvalidOperationException("Missing candidate settings.");
 private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
 public static string EvidenceDirectory=>created is not null?Path.GetDirectoryName(created.ModelPath)!:throw new InvalidOperationException("Create the new POC disposable first.");
 static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
 static void NewJson(string file,object value){using var stream=new FileStream(file,FileMode.CreateNew);JsonSerializer.Serialize(stream,value,Json);}
 static void RequireHost(UIApplication app)
 {
  if(app.Application.VersionNumber!="2026"||Environment.Version.Major!=10||typeof(Document).Assembly.GetName().Version?.Major!=26)throw new InvalidOperationException("This POC requires actual Revit 2026 / API 26 / .NET 10. No compatibility bypass.");
 }
 static void NoAliases(string path,string stop)
 {
  for(var d=new DirectoryInfo(path);d is not null;d=d.Parent){if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Reparse-point paths are not allowed for POC models.");if(string.Equals(d.FullName,stop,StringComparison.OrdinalIgnoreCase))return;}
  throw new InvalidOperationException("POC directory is outside its approved root.");
 }
 public static CreatedModel CreateDisposable(UIApplication app)
 {
  RequireHost(app);var settings=Settings;
  var workspace=Path.GetFullPath(settings.WorkspaceRoot);var root=Path.GetFullPath(settings.TestRoot);
  if(!Directory.Exists(Path.Combine(workspace,".git"))||!root.StartsWith(workspace+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!root.Contains("Revit_Test_Models",StringComparison.Ordinal))throw new InvalidOperationException("Dedicated workspace test root is not verified.");
  string id=Guid.NewGuid().ToString("N"),directory=Path.Combine(root,id),path=Path.Combine(directory,DocumentPolicy.FileName);
  if(Directory.Exists(directory)||File.Exists(path))throw new InvalidOperationException("New model target already exists. Never reuse or overwrite a model.");
  Directory.CreateDirectory(directory);NoAliases(directory,workspace);
  NewJson(Path.Combine(directory,"creation-intent.json"),new{Package=settings.PackageId,RunId=id,ExactNewModelPath=path,ExistingTarget=false,CreatedUtc=DateTime.UtcNow});
  // No template/existing RVT is opened. This creates a brand-new in-memory project only.
  var doc=app.Application.NewProjectDocument(UnitSystem.Metric)??throw new InvalidOperationException("New project creation failed.");
  try{
   if(!string.IsNullOrEmpty(doc.PathName)||File.Exists(path))throw new InvalidOperationException("Fresh-document/new-path identity failed before initial SaveAs.");
   string project=doc.ProjectInformation.UniqueId;
   using(var save=new SaveAsOptions{OverwriteExistingFile=false})doc.SaveAs(path,save);
   string snapshot=Path.Combine(directory,"initial-immutable-snapshot.rvt");
   // Archive the just-created model; this new snapshot is never opened for modification.
   File.Copy(path,snapshot,false);string hash=Hash(snapshot);
   var record=new CreatedModel(id,path,project,snapshot,hash,DateTime.UtcNow.ToString("O"));
   NewJson(path+".evcd-disposable.json",new DisposableMarker(1,path,snapshot,hash,record.CreatedUtc));
   NewJson(Path.Combine(directory,"created-model.json"),record);doc.Close(false);created=record;
  }catch{if(doc.IsValidObject)doc.Close(false);throw;}
  app.OpenAndActivateDocument(path);var active=RequireDocument(app);
  // Plan setup is allowed only after exact saved path/project/snapshot identity verification.
  var plan=active.ActiveView as ViewPlan;
  if(plan is null||plan.IsTemplate||plan.GenLevel is null)plan=new FilteredElementCollector(active).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v=>!v.IsTemplate&&v.GenLevel is not null);
  if(plan is null){RequireDocument(app);using var tx=new Transaction(active,"POC: plan in newly created disposable only");tx.Start();var level=new FilteredElementCollector(active).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault()??Level.Create(active,10);
   var type=new FilteredElementCollector(active).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.FloorPlan);plan=ViewPlan.Create(active,type.Id,level.Id);plan.Name="EV Interactive POC - Canopy Only";if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("POC plan setup did not commit.");}
  if(active.ActiveView.Id!=plan.Id)app.ActiveUIDocument.RequestViewChange(plan);RequireDocument(app);active.Save();return created!;
 }
 public static Document RequireDocument(UIApplication app)
 {
  RequireHost(app);var record=created??throw new InvalidOperationException("No model was created by this candidate in this Revit session. Create a NEW disposable; existing RVTs cannot be adopted.");
  var doc=app.ActiveUIDocument?.Document??throw new InvalidOperationException("No active disposable model.");
  if(!string.Equals(Path.GetFullPath(doc.PathName),record.ModelPath,StringComparison.OrdinalIgnoreCase)||doc.ProjectInformation.UniqueId!=record.ProjectUniqueId||doc.IsFamilyDocument||doc.IsLinked||doc.IsWorkshared||doc.IsModelInCloud||doc.IsReadOnly)throw new InvalidOperationException("Exact disposable model identity/path or writable-local-project check failed. No operation is permitted.");
  NoAliases(Path.GetDirectoryName(record.ModelPath)!,Path.GetFullPath(Settings.WorkspaceRoot));
  if(!File.Exists(record.ModelPath)||(File.GetAttributes(record.ModelPath)&FileAttributes.ReparsePoint)!=0||Hash(record.SnapshotPath)!=record.SnapshotSha256)throw new InvalidOperationException("Disposable file or immutable snapshot identity failed.");
  var saved=JsonSerializer.Deserialize<CreatedModel>(File.ReadAllText(Path.Combine(EvidenceDirectory,"created-model.json")));
  if(saved!=record)throw new InvalidOperationException("Creation certificate changed.");
  using var approved=new ProcessSetting("EV_CANOPY_11B_TEST_MODEL",record.ModelPath);Planner11BDocumentGuard.Check(doc);return doc;
 }
 static T WithLegacyGuard<T>(UIApplication app,Func<Document,T> operation,string? fault=null)
 {
  var doc=RequireDocument(app);using var approved=new ProcessSetting("EV_CANOPY_11B_TEST_MODEL",created!.ModelPath);using var controlledFault=new ProcessSetting("EV_CANOPY_11B_TEST_FAULT",fault);return operation(doc);
 }
 public static CanopyIntegrationSession NewSession(UIApplication app)
 {
  var doc=RequireDocument(app);var level=doc.ActiveView.GenLevel??throw new InvalidOperationException("Select a floor plan with a level.");Guid command=Guid.NewGuid();
  string nativeProject=doc.ProjectInformation.UniqueId,documentPath=doc.PathName,levelKey=level.UniqueId;
  HostContext Context(){var current=RequireDocument(app);var actual=current.ActiveView.GenLevel??throw new InvalidOperationException("Active view is no longer a plan.");if(current.ProjectInformation.UniqueId!=nativeProject||!string.Equals(current.PathName,documentPath,StringComparison.OrdinalIgnoreCase)||actual.UniqueId!=levelKey)throw new InvalidOperationException("Active document/level changed.");return new(command,created!.RunId+"|"+nativeProject,current.PathName,actual.UniqueId,actual.Elevation,CheckStatus.Pass,$"Revit {app.Application.VersionNumber}/{app.Application.VersionBuild}; CLR {Environment.Version}; API {typeof(Document).Assembly.GetName().Version}");}
  return new(new ReadOnlyCanopyHostAdapter(Context,r=>WithLegacyGuard(app,d=>DesignValidation.Preflight(d,r))));
 }
 public static string Interactive(UIApplication app)
 {
  var doc=RequireDocument(app);var session=NewSession(app);var choices=OwnedDesignRepository.List(doc);
  var pending=CanopyWindow.ShowCanopyOnlyForRevit(app.MainWindowHandle.ToInt64(),choices,session);
  if(pending is null)return "Cancelled: no generation transaction started.";
  XYZ point;try{point=app.ActiveUIDocument.Selection.PickPoint(ObjectSnapTypes.Endpoints|ObjectSnapTypes.Intersections,"CANOPY ONLY: choose SITE origin; Escape cancels without generation.");}catch(Autodesk.Revit.Exceptions.OperationCanceledException){session.Cancel(pending.Ticket);return "Origin selection cancelled: no generation transaction started.";}
  return Generate(app,session,pending,point).Id;
 }
 public static (string Id,RunDiagnostics Run) Generate(UIApplication app,CanopyIntegrationSession session,PendingCanopyReview pending,XYZ origin,string? fault=null)
 {
  RequireDocument(app);var handoff=session.ConfirmForPlacement(pending.Review,pending.Ticket,pending.ExplicitCanopyOnlyConfirmation);
  if(handoff.Scope!=IntegrationScope.CanopyOnly||handoff.IncludesChargingEquipment||handoff.SiteLayoutApproved)throw new InvalidOperationException("Unsupported generation scope.");
  var run=new RunDiagnostics();LastRun=run;run.Event("CANOPY_ONLY; EQUIPMENT_EXCLUDED; SITE_NOT_APPROVED");
  string evidence=Path.Combine(EvidenceDirectory,"generation-"+run.RunId+".json");
  try{string id=WithLegacyGuard(app,d=>GenerationService.Generate(d,handoff.LegacyRequest,new XYZ(origin.X,origin.Y,handoff.Context.LevelElevationFeet),run),fault);
   NewJson(evidence,new{Status="Committed",Scope="CanopyOnly",SiteLayoutApproved=false,Equipment=handoff.Equipment,Run=run.RunId,run.Created,run.TransactionOutcome,Checks=run.Checks,Events=run.Events});return(id,run);
  }catch(Exception e){NewJson(evidence,new{Status="Failed",Scope="CanopyOnly",SiteLayoutApproved=false,Equipment=handoff.Equipment,run.TransactionOutcome,Checks=run.Checks,Events=run.Events,Error=e.ToString()});throw;}
 }
 public static ValidationReport Validate(UIApplication app)=>WithLegacyGuard(app,DesignValidation.Existing);
 internal static void Capture(CanopyWindow window,string name)
 {
  window.UpdateLayout();var visual=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new FileStream(Path.Combine(EvidenceDirectory,name),FileMode.CreateNew);encoder.Save(stream);
 }
 public static string RunAutorun(UIApplication app)
 {
  if(app.Application.Documents.Cast<Document>().Any())throw new InvalidOperationException("Autorun stopped: another model is open. No existing model will be opened, closed or modified.");
  var model=CreateDisposable(app);return "DISPOSABLE_CREATED: "+model.ModelPath+". Run the isolated acceptance command in normal external-command context.";
 }
 public static string SelfTest(UIApplication app)=>PocAcceptance.Run(app);
}

[Transaction(TransactionMode.Manual)] public sealed class CreateDisposableCommand : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CommandHelper.Run(()=>JsonSerializer.Serialize(PocOperations.CreateDisposable(data.Application)),ref message);}
[Transaction(TransactionMode.Manual)] public sealed class InteractiveCanopyCommand : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CommandHelper.Run(()=>PocOperations.Interactive(data.Application),ref message);}
[Transaction(TransactionMode.Manual)] public sealed class ValidateCanopyCommand : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CommandHelper.Run(()=>PocOperations.Validate(data.Application).Summary,ref message);}
[Transaction(TransactionMode.Manual)] public sealed class RunAcceptanceCommand : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CommandHelper.Run(()=>PocOperations.SelfTest(data.Application),ref message);}
internal static class CommandHelper {internal static Result Run(Func<string> action,ref string message){try{TaskDialog.Show("EV Hub isolated POC",action());return Result.Succeeded;}catch(Exception e){message=e.ToString();TaskDialog.Show("EV Hub POC stopped safely",message);return Result.Failed;}}}
