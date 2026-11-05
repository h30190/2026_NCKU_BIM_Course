using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
namespace EVCanopyDesigner.Interactive.Revit.Bootstrap;

internal static class CandidateLoader
{
 internal static readonly string DirectoryPath=Path.GetDirectoryName(typeof(CandidateLoader).Assembly.Location)!;
 static readonly Lazy<Assembly> host=new(()=>{
  var hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(DirectoryPath,"sha256.json")))??throw new InvalidOperationException("Missing package hashes.");
  foreach(var pair in hashes){if(Path.GetFileName(pair.Key)!=pair.Key||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(DirectoryPath,pair.Key))))!=pair.Value)throw new InvalidOperationException("Package integrity/isolation failed: "+pair.Key);}
  return new CandidateLoadContext(DirectoryPath,hashes).LoadVerified("EVCanopyDesigner.Interactive.Revit.Host.dll");
 });
 internal static Result Execute(string type,ExternalCommandData data,ref string message,ElementSet elements)
 {
  try{return ((IExternalCommand)Activator.CreateInstance(host.Value.GetType("EVCanopyDesigner.Interactive.Revit."+type,true)!)!).Execute(data,ref message,elements);}catch(Exception e){message=(e is TargetInvocationException {InnerException:not null} t?t.InnerException!:e).ToString();TaskDialog.Show("EV Hub POC isolation blocked",message);return Result.Failed;}
 }
 internal static string Autorun(UIApplication app)=>(string)host.Value.GetType("EVCanopyDesigner.Interactive.Revit.PocOperations",true)!.GetMethod("RunAutorun")!.Invoke(null,new object[]{app})!;
}
public sealed class PocApplication : IExternalApplication
{
 public Result OnStartup(UIControlledApplication app)
 {
  try{
   string settings=Path.Combine(CandidateLoader.DirectoryPath,"candidate-settings.json");using var json=JsonDocument.Parse(File.ReadAllText(settings));string id=json.RootElement.GetProperty("PackageId").GetString()!;
   string assembly=typeof(PocApplication).Assembly.Location;var panel=app.CreateRibbonPanel("EV Hub POC "+id[..8]);
   foreach(var item in new[]{("NewDisposable","1. NEW disposable","CreateDisposable"),("CanopyOnly","2. Canopy ONLY","InteractiveCanopy"),("Validate","Validate canopy","ValidateCanopy"),("Acceptance","Run POC acceptance","RunAcceptance")})panel.AddItem(new PushButtonData("EVHubPOC_"+item.Item1,item.Item2,assembly,"EVCanopyDesigner.Interactive.Revit.Bootstrap."+item.Item3));
   if(Environment.GetEnvironmentVariable("EV_INTERACTIVE_POC_AUTORUN")==id)app.Idling+=OnFirstIdle;
   return Result.Succeeded;
  }catch(Exception e){TaskDialog.Show("EV Hub POC registration stopped",e.ToString());return Result.Failed;}
 }
 void OnFirstIdle(object? sender,IdlingEventArgs args)
 {
  if(sender is not UIApplication app)return;app.Idling-=OnFirstIdle;
  string file=Path.Combine(CandidateLoader.DirectoryPath,"autorun-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".json");
  try{string result=CandidateLoader.Autorun(app);File.WriteAllText(file,JsonSerializer.Serialize(new{Status="DISPOSABLE_CREATED_AWAITING_ACCEPTANCE",Result=result},new JsonSerializerOptions{WriteIndented=true}));}
  catch(Exception e){var cause=e is TargetInvocationException {InnerException:not null} t?t.InnerException!:e;File.WriteAllText(file,JsonSerializer.Serialize(new{Status="BLOCKED_OR_FAILED",Error=cause.ToString()},new JsonSerializerOptions{WriteIndented=true}));TaskDialog.Show("EV Hub POC stopped safely",cause.Message);}
 }
 public Result OnShutdown(UIControlledApplication app){app.Idling-=OnFirstIdle;return Result.Succeeded;}
}
[Transaction(TransactionMode.Manual)] public sealed class CreateDisposable : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CandidateLoader.Execute("CreateDisposableCommand",data,ref message,elements);}
[Transaction(TransactionMode.Manual)] public sealed class InteractiveCanopy : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CandidateLoader.Execute("InteractiveCanopyCommand",data,ref message,elements);}
[Transaction(TransactionMode.Manual)] public sealed class ValidateCanopy : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CandidateLoader.Execute("ValidateCanopyCommand",data,ref message,elements);}
[Transaction(TransactionMode.Manual)] public sealed class RunAcceptance : IExternalCommand {public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>CandidateLoader.Execute("RunAcceptanceCommand",data,ref message,elements);}
