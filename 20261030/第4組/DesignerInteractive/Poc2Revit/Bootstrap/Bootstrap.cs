using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EVCanopyDesigner.Interactive.Revit.Bootstrap;

namespace EVCanopyDesigner.Poc2.Revit.Bootstrap;

internal static class Loader
{
 internal static readonly string DirectoryPath=Path.GetDirectoryName(typeof(Loader).Assembly.Location)!;
 static Document? disposable;
 static string? disposablePath,projectIdentity;
 static bool generationInvoked;
 internal static bool CanGenerate(UIApplication app)
 {
  var current=app.ActiveUIDocument?.Document;
  return !generationInvoked&&disposable is not null&&disposable.IsValidObject&&ReferenceEquals(current,disposable)&&
   string.Equals(current.PathName,disposablePath,StringComparison.OrdinalIgnoreCase)&&current.ProjectInformation.UniqueId==projectIdentity;
 }
 static readonly Lazy<Assembly> Host=new(()=>{
  var hashes=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(DirectoryPath,"sha256.json")))??throw new InvalidOperationException("Missing package inventory.");
  foreach(var (file,expected) in hashes){if(Path.GetFileName(file)!=file||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(DirectoryPath,file))))!=expected)throw new InvalidOperationException("Candidate package integrity failed: "+file);}
  return new CandidateLoadContext(DirectoryPath,hashes).LoadVerified("EVCanopyDesigner.Poc2.Revit.Host.dll");
 });
 internal static Result Execute(string command,ExternalCommandData data,ref string message,ElementSet elements)
 {
  try
  {
   if(command=="GenerateStaticSiteCommand")
   {
    if(!CanGenerate(data.Application))throw new InvalidOperationException("Activate this candidate's newly created disposable first.");
    generationInvoked=true;
   }
   var result=((IExternalCommand)Activator.CreateInstance(Host.Value.GetType("EVCanopyDesigner.Poc2.Revit."+command,true)!)!).Execute(data,ref message,elements);
   if(command=="NewDisposableCommand"&&result==Result.Succeeded)
   {
    disposable=data.Application.ActiveUIDocument?.Document??throw new InvalidOperationException("Disposable creation returned no active document.");
    disposablePath=disposable.PathName;projectIdentity=disposable.ProjectInformation.UniqueId;
   }
   return result;
  }
  catch(Exception e){message=(e is TargetInvocationException {InnerException:not null} t?t.InnerException!:e).ToString();TaskDialog.Show("POC 2 isolated candidate stopped",message);return Result.Failed;}
 }
}
public sealed class Poc2Application:IExternalApplication
{
 public Result OnStartup(UIControlledApplication app)
 {
  try
  {
   using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(Loader.DirectoryPath,"candidate-settings.json")));string id=settings.RootElement.GetProperty("PackageId").GetString()!;
   if(!typeof(Poc2Application).Assembly.GetName().Name!.EndsWith("."+id,StringComparison.Ordinal))throw new InvalidOperationException("Package/bootstrap identity mismatch.");
   var panel=app.CreateRibbonPanel("EV Hub POC2 "+id[..8]);string assembly=typeof(Poc2Application).Assembly.Location;
   panel.AddItem(new PushButtonData("EVPoc2New_"+id,"1. NEW POC2\ndisposable",assembly,typeof(NewDisposable).FullName){AvailabilityClassName=typeof(NewDisposableAvailability).FullName});
   panel.AddItem(new PushButtonData("EVPoc2Generate_"+id,"2. Generate static\nUNVALIDATED site",assembly,typeof(GenerateStaticSite).FullName){AvailabilityClassName=typeof(GenerateStaticSiteAvailability).FullName});
   return Result.Succeeded;
  }
  catch(Exception e){TaskDialog.Show("POC 2 registration stopped",e.ToString());return Result.Failed;}
 }
 public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
}
public sealed class NewDisposableAvailability:IExternalCommandAvailability
{
 public bool IsCommandAvailable(UIApplication app,CategorySet categories)=>app.ActiveUIDocument is null&&!app.Application.Documents.Cast<Document>().Any();
}
public sealed class GenerateStaticSiteAvailability:IExternalCommandAvailability
{
 public bool IsCommandAvailable(UIApplication app,CategorySet categories)=>Loader.CanGenerate(app);
}
[Transaction(TransactionMode.Manual)] public sealed class NewDisposable:IExternalCommand
{
 public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>Loader.Execute("NewDisposableCommand",data,ref message,elements);
}
[Transaction(TransactionMode.Manual)] public sealed class GenerateStaticSite:IExternalCommand
{
 public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>Loader.Execute("GenerateStaticSiteCommand",data,ref message,elements);
}
