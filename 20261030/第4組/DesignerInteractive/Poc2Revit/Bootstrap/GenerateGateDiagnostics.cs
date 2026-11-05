using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace EVCanopyDesigner.Poc2.Revit.Bootstrap;

// Offline diagnostic-only addition. Not registered by Poc2Application.OnStartup.
// A separately authorized Command manifest may point at a newly built copy of this
// class. Inspect the ORIGINAL loaded bootstrap, never a new Loader's empty state.
[Transaction(TransactionMode.ReadOnly)]
public sealed class DiagnoseGenerateAvailability:IExternalCommand
{
 const string TargetAssembly="EVCanopyDesigner.Poc2.Revit.Bootstrap.c4072dc3c4e34a2db7464899d06372cd";
 const string TargetRun="e46bd3719d664d33af991aba3428bf19";
 sealed record Check(string Condition,string State,string Detail);
 public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
 {
  try
  {
   var rows=new List<Check>();var observations=new Dictionary<string,string?>();var errors=new Dictionary<string,string>();
   var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name==TargetAssembly).ToArray();
   var target=assemblies.Length==1?assemblies[0]:null;
   var loader=target?.GetType("EVCanopyDesigner.Poc2.Revit.Bootstrap.Loader",false);
   observations["TargetAssembly"]=TargetAssembly;observations["MatchingLoadedAssemblies"]=assemblies.Length.ToString();
   observations["LoadedContexts"]=string.Join(" | ",assemblies.Select(a=>a.Location+"; ALC="+System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a)?.Name));
   object? Field(string name)
   {
    try
    {
     if(loader is null)throw new InvalidOperationException("Original Loader not uniquely available; do not infer or reconstruct its state.");
     var field=loader.GetField(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingFieldException(name);
     return field.GetValue(null); // No SetValue, private method invocation, Host.Value or assembly loading.
    }
    catch(Exception e){errors[name]=e.Message;return null;}
   }
   var invoked=Field("generationInvoked");var cachedObject=Field("disposable");var cached=cachedObject as Document;
   var cachedPath=Field("disposablePath") as string;var cachedIdentity=Field("projectIdentity") as string;
   var current=data.Application.ActiveUIDocument?.Document;
   string? Observe(string key,Func<string?> read)
   {
    try{var value=read();observations[key]=value;return value;}
    catch(Exception e){errors[key]=e.Message;return null;}
   }
   var currentPath=Observe("ActivePath",()=>current?.PathName);
   var currentIdentity=Observe("ActiveProjectUniqueId",()=>current?.ProjectInformation.UniqueId);
   observations["StoredPath"]=cachedPath;observations["StoredProjectUniqueId"]=cachedIdentity;
   observations["StoredReferenceRuntimeType"]=cachedObject?.GetType().FullName;
   void Probe(string name,Func<bool> check,string? unknown=null)
   {
    if(unknown is not null){rows.Add(new(name,"UNKNOWN",unknown));return;}
    try{rows.Add(new(name,check()?"PASS":"FAIL","Read-only observation; no authorization state changed."));}
    catch(Exception e){rows.Add(new(name,"ERROR",e.Message));}
   }
   string? Missing(bool condition,string explanation)=>condition?explanation:null;
   Probe("Prerequisite: active document exists",()=>current is not null);
   Probe("Gate 1: !generationInvoked",()=>invoked is false,Missing(invoked is not bool,"Stored Boolean unavailable."));
   Probe("Gate 2: disposable is not null",()=>cachedObject is not null,Missing(errors.ContainsKey("disposable"),"Stored field unavailable."));
   Probe("Gate 3: disposable.IsValidObject",()=>cached!.IsValidObject,Missing(cached is null,"No readable cached Document."));
   Probe("Gate 4: ReferenceEquals(current, disposable)",()=>ReferenceEquals(current,cachedObject),Missing(errors.ContainsKey("disposable"),"Stored field unavailable."));
   Probe("Gate 5: active path equals stored path (OrdinalIgnoreCase)",()=>string.Equals(currentPath,cachedPath,StringComparison.OrdinalIgnoreCase),Missing(currentPath is null||cachedPath is null,"One path is unavailable."));
   Probe("Gate 6: active ProjectInformation.UniqueId equals stored ID",()=>currentIdentity==cachedIdentity,Missing(currentIdentity is null||cachedIdentity is null,"One project identity is unavailable."));
   Probe("Diagnostic only: cached Document.Equals(current)",()=>cached!.IsValidObject&&cached.Equals(current),Missing(cached is null||current is null,"One Document is unavailable."));
   Probe("Diagnostic only: repeated ActiveUIDocument.Document getter returns same reference",()=>ReferenceEquals(current,data.Application.ActiveUIDocument?.Document),Missing(current is null,"No active Document."));
   Observe("CachedDocumentPath",()=>cached is not null&&cached.IsValidObject?cached.PathName:null);
   Observe("CachedDocumentProjectUniqueId",()=>cached is not null&&cached.IsValidObject?cached.ProjectInformation.UniqueId:null);
   Observe("ActiveIsModified",()=>current?.IsModified.ToString());
   Observe("ActiveHasDesignLevel",()=>current is null?null:(current.ActiveView.GenLevel is not null).ToString());
   try
   {
    if(target is null)throw new InvalidOperationException("Original candidate unavailable.");
    var package=Path.GetDirectoryName(target.Location)!;
    using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"candidate-settings.json")));
    var root=settings.RootElement.GetProperty("WorkspaceRoot").GetString()!;
    var certificate=Path.Combine(root,"Revit_Test_Models","POC2StaticSite",TargetRun,"created-model.json");
    using var record=JsonDocument.Parse(File.ReadAllText(certificate));
    var path=record.RootElement.GetProperty("ModelPath").GetString();var id=record.RootElement.GetProperty("ProjectUniqueId").GetString();
    observations["ReadOnlyCertificate"]=certificate;observations["ExpectedPath"]=path;observations["ExpectedProjectUniqueId"]=id;
    Probe("Certificate: correct run",()=>record.RootElement.GetProperty("RunId").GetString()==TargetRun);
    Probe("Certificate: live path matches recorded path",()=>string.Equals(currentPath,path,StringComparison.OrdinalIgnoreCase),Missing(currentPath is null,"Live path unavailable."));
    Probe("Certificate: live project ID matches recorded ID",()=>currentIdentity==id,Missing(currentIdentity is null,"Live ID unavailable."));
    Probe("Certificate: stored path/ID match recorded identity",()=>string.Equals(cachedPath,path,StringComparison.OrdinalIgnoreCase)&&cachedIdentity==id,Missing(cachedPath is null||cachedIdentity is null,"Stored identity unavailable."));
   }
   catch(Exception e){rows.Add(new("Certificate comparison","UNKNOWN",e.Message));}
   var report=JsonSerializer.Serialize(new{Scope="DIAGNOSTIC ONLY — does not enable Generate or replace host checks",TargetRun,Observations=observations,Checks=rows,ReadErrors=errors,GateModified=false,ModelModified=false},new JsonSerializerOptions{WriteIndented=true});
   // Display only: no transaction, model save, file output, command posting or static-field writes.
   TaskDialog.Show("POC 2 — Generate gate READ ONLY",report);
   return Result.Succeeded;
  }
  catch(Exception e){message=e.ToString();return Result.Failed;}
 }
}
