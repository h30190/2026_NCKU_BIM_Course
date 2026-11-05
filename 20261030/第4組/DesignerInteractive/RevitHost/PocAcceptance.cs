using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.Revit;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;
namespace EVCanopyDesigner.Interactive.Revit;

internal static class PocAcceptance
{
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr key,IntPtr data);
 internal static string Run(UIApplication app)
 {
  var doc=PocOperations.RequireDocument(app);if(OwnedDesignRepository.Registries(doc).Count!=0)throw new InvalidOperationException("Acceptance requires this candidate's newly created blank disposable; never reuse a populated RVT.");
  var checks=new List<string>();string report=Path.Combine(PocOperations.EvidenceDirectory,"revit-acceptance.json");if(File.Exists(report))throw new InvalidOperationException("This disposable already has acceptance evidence; create a new one.");
  void Check(bool ok,string detail){if(!ok)throw new InvalidOperationException(detail);checks.Add(detail);}
  PendingCanopyReview Review(CanopyIntegrationSession session,Action<InteractiveDesignerViewModel>? edit=null,string? replace=null,string? capture=null)
  {
   Exception? failure=null;
   var pending=CanopyWindow.ShowCanopyOnlyForRevit(app.MainWindowHandle.ToInt64(),OwnedDesignRepository.List(doc),session,window=>{
    try{
     var vm=(InteractiveDesignerViewModel)window.DataContext;
     if(replace is not null){((RadioButton)window.FindName("Replace")).IsChecked=true;((System.Windows.Controls.ComboBox)window.FindName("DesignChoices")).SelectedItem=OwnedDesignRepository.List(doc).Single(c=>c.Id==replace);}
     edit?.Invoke(vm);((CheckBox)window.FindName("ScopeAck")).IsChecked=true;
     var validate=FindButtons(window).Single(b=>b.Content?.ToString()=="Validate & Preview — No Revit Changes");validate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
     var generate=(Button)window.FindName("Generate");if(!generate.IsEnabled)throw new InvalidOperationException(((System.Windows.Controls.TextBox)window.FindName("PreflightStatus")).Text);
     if(capture is not null)PocOperations.Capture(window,capture);generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }catch(Exception e){failure=e;window.Close();}
   });
   if(failure is not null)throw new InvalidOperationException("Actual Revit-hosted WPF workflow failed.",failure);return pending??throw new InvalidOperationException("No scoped UI handoff.");
  }
  try{
   string actualPath=doc.PathName;Check(PocOperations.RequireDocument(app).PathName==actualPath,"Exact newly created disposable path/project/snapshot identity verified before model changes");
   int initial=new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
   var cancelSession=PocOperations.NewSession(app);var cancel=CanopyWindow.ShowCanopyOnlyForRevit(app.MainWindowHandle.ToInt64(),Array.Empty<DesignChoice>(),cancelSession,w=>w.Close());Check(cancel is null&&new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()==initial,"Actual WPF cancellation starts no generation transaction and changes no elements");
   var session=PocOperations.NewSession(app);var first=Review(session,vm=>vm.Settings.Single(f=>f.Key=="RoofDatum").Value="5.8",capture:"01-revit-hosted-designer.png");
   Check(first.Review.Equipment.BlockingConflicts==21,"All 21 equipment-access conflicts retained; equipment excluded from canopy-only handoff");
   var made=PocOperations.Generate(app,session,first,new XYZ(10,20,0));Check(made.Run.TransactionOutcome=="Committed","Actual canopy generation committed in disposable");string designA=made.Id;
   Check(PocOperations.Validate(app).CanProceed,"Actual generated canopy quantities/ownership/clearance validate");
   var initialA=OwnedDesignRepository.Registries(doc).Single(x=>x.Record.Id==designA).Record;
   var sessionB=PocOperations.NewSession(app);var second=Review(sessionB,vm=>vm.Settings.Single(f=>f.Key=="RoofDatum").Value="6.1");var madeB=PocOperations.Generate(app,sessionB,second,new XYZ(150,20,0));string designB=madeB.Id;
   var unrelated=DesignValidation.SnapshotOthers(doc,designA);
   var updateSession=PocOperations.NewSession(app);var update=Review(updateSession,vm=>vm.Settings.Single(f=>f.Key=="RoofDatum").Value="6.0",designA,"02-revit-hosted-replacement.png");var updated=PocOperations.Generate(app,updateSession,update,new XYZ(10,20,0));
   var replaced=OwnedDesignRepository.Registries(doc).Single(x=>x.Record.Id==designA).Record;
   Check(updated.Id==designA&&replaced.Revision==initialA.Revision+1,"Replacement retains selected design ID and advances revision");DesignValidation.CompareOthers(doc,designA,unrelated);Check(true,"Unrelated second canopy receipts preserved by replacement");
   foreach(string fault in new[]{"AfterFirstCreate","AfterDelete"}){
    var before=DesignValidation.SnapshotOthers(doc,"");var faultSession=PocOperations.NewSession(app);var pending=Review(faultSession,vm=>vm.Settings.Single(f=>f.Key=="RoofDatum").Value="6.2",designA);
    bool rejected=false;try{PocOperations.Generate(app,faultSession,pending,new XYZ(10,20,0),fault);}catch(InvalidOperationException e) when(e.Message.Contains("Controlled test fault",StringComparison.Ordinal)){rejected=true;}
    Check(rejected&&PocOperations.LastRun?.TransactionOutcome=="RolledBack","Controlled "+fault+" fault actually rolled back the Revit transaction");DesignValidation.CompareOthers(doc,"",before);Check(PocOperations.Validate(app).CanProceed,"Rollback after "+fault+" preserves all owned receipts and valid geometry");
   }
   var staleSession=PocOperations.NewSession(app);var stale=Review(staleSession);var staleBefore=DesignValidation.SnapshotOthers(doc,"");staleSession.Invalidate();bool staleRejected=false;try{PocOperations.Generate(app,staleSession,stale,new XYZ(10,20,0));}catch(InvalidOperationException){staleRejected=true;}Check(staleRejected,"Stale session rejected before generation transaction");DesignValidation.CompareOthers(doc,"",staleBefore);
   var boundSession=PocOperations.NewSession(app);var bound=Review(boundSession);var boundBefore=DesignValidation.SnapshotOthers(doc,"");var originalPlan=app.ActiveUIDocument.ActiveView;
   PocOperations.RequireDocument(app);ViewPlan alternate;
   using(var tx=new Transaction(doc,"POC: alternate level for actual stale-context test")){tx.Start();var level=Level.Create(doc,originalPlan.GenLevel.Elevation+20);var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.FloorPlan);alternate=ViewPlan.Create(doc,type.Id,level.Id);if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Alternate-level setup failed.");}
   app.ActiveUIDocument.ActiveView=alternate;bool contextRejected=false;try{PocOperations.Generate(app,boundSession,bound,new XYZ(10,20,0));}catch(InvalidOperationException){contextRejected=true;}app.ActiveUIDocument.ActiveView=originalPlan;
   Check(contextRejected,"Actual active-level change rejects previously validated handoff before generation");DesignValidation.CompareOthers(doc,"",boundBefore);
   PocOperations.RequireDocument(app);bool originCancelled=false;int beforePick=new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
   // Windows input goes only to this Revit main window; the timer never calls the Revit API.
   var handle=app.MainWindowHandle;using(var timer=new System.Threading.Timer(_=>{PostMessage(handle,0x0100,new IntPtr(27),IntPtr.Zero);PostMessage(handle,0x0101,new IntPtr(27),IntPtr.Zero);},null,700,System.Threading.Timeout.Infinite)){
    try{app.ActiveUIDocument.Selection.PickPoint("POC cancellation test: automatic Escape; no model generation.");}catch(Autodesk.Revit.Exceptions.OperationCanceledException){originCancelled=true;}}
   Check(originCancelled&&new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()==beforePick,"Actual PickPoint Escape cancels without model changes");
   Check(OwnedDesignRepository.Registries(doc).Count==2,"Exactly two independent canopy registries; no equipment registry created");
   var plan=app.ActiveUIDocument.ActiveView;PocOperations.RequireDocument(app);using(var tx=new Transaction(doc,"POC: evidence view in verified disposable")){tx.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);var view=View3D.CreateIsometric(doc,type.Id);view.Name="EV Interactive POC - Generated Canopies";if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Evidence view failed.");app.ActiveUIDocument.ActiveView=view;}
   app.ActiveUIDocument.RefreshActiveView();PocOperations.RequireDocument(app);
   var export=new ImageExportOptions{FilePath=Path.Combine(PocOperations.EvidenceDirectory,"03-actual-revit-canopies"),ExportRange=ExportRange.SetOfViews,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,ZoomType=ZoomFitType.FitToPage,PixelSize=1800};export.SetViewsAndSheets(new List<ElementId>{app.ActiveUIDocument.ActiveView.Id});doc.ExportImage(export);
   PocOperations.RequireDocument(app);doc.Save();Check(true,"Only newly created disposable saved; actual Revit view exported");
   using(var stream=new FileStream(report,FileMode.CreateNew))JsonSerializer.Serialize(stream,new{Status="PASS",Scope="CanopyOnly",ModelPath=doc.PathName,Runtime=Environment.Version.ToString(),Revit=app.Application.VersionBuild,EquipmentAccessConflicts=21,SiteLayoutApproved=false,Checks=checks,Designs=new[]{designA,designB}},new JsonSerializerOptions{WriteIndented=true});
   return "PASS: "+checks.Count+" actual Revit checks. Model: "+doc.PathName+"; evidence: "+PocOperations.EvidenceDirectory;
  }catch(Exception e){using(var stream=new FileStream(report,FileMode.CreateNew))JsonSerializer.Serialize(stream,new{Status="FAIL",Scope="CanopyOnly",ModelPath=doc.PathName,Checks=checks,Error=e.ToString()},new JsonSerializerOptions{WriteIndented=true});throw;}
 }
 static IEnumerable<Button> FindButtons(DependencyObject root){for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is Button b)yield return b;foreach(var nested in FindButtons(child))yield return nested;}}
}
