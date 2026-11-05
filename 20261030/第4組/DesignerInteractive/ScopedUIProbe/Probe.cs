using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;
namespace DesignerInteractive.ScopedUIProbe;
public static class Probe
{
 public static int Run(string output)
 {
  if(Directory.Exists(output))throw new InvalidOperationException("New evidence directory required.");Directory.CreateDirectory(output);
  var checks=new List<string>();var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var host=new FakeHost();var session=new CanopyIntegrationSession(host);Exception? error=null;
  void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);}
  T Named<T>(CanopyWindow window,string name) where T:class=>(T)window.FindName(name);
  void Capture(CanopyWindow window,string name){window.UpdateLayout();var v=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(v.ActualWidth),(int)Math.Ceiling(v.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(v);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new FileStream(Path.Combine(output,name),FileMode.CreateNew);encoder.Save(stream);}
  void Validate(CanopyWindow window)=>Buttons(window).Single(b=>b.Content?.ToString()=="Validate & Preview — No Revit Changes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  try{
   Check(AssemblyLoadContext.GetLoadContext(typeof(CanopyEngine).Assembly)!=AssemblyLoadContext.Default,"Candidate Core loads in private context even with same-identity Core preloaded in default context");
   Check(AssemblyLoadContext.GetLoadContext(typeof(Window).Assembly)==AssemblyLoadContext.Default,"WPF framework stays shared with host");
   var pending=CanopyWindow.ShowCanopyOnlyForRevit(0,Array.Empty<DesignChoice>(),session,w=>{
    try{
     w.Title="Offline private-context UI proof — FAKE host; no Revit";var vm=(InteractiveDesignerViewModel)w.DataContext;var generate=Named<Button>(w,"Generate");
     Check(generate.IsVisible&&!generate.IsEnabled&&host.Calls==0,"Scoped UI opens without automatic host preflight or enabled generation");
     Validate(w);Check(host.Calls==0&&!generate.IsEnabled,"Unchecked equipment-exclusion acknowledgement prevents host preflight");
     Named<CheckBox>(w,"ScopeAck").IsChecked=true;Validate(w);Check(generate.IsEnabled&&host.Calls==1,"Explicit canopy-only validation enables scoped continuation after all critical mock checks pass");
     Check(vm.Equipment!.Issues.Count(i=>i.Blocking)==21,"All 21 equipment-access conflicts remain displayed despite canopy-only readiness");
     int before=host.Calls;vm.Settings.Single(f=>f.Key=="RoofDatum").Value="5.8";Check(!generate.IsEnabled,"Parameter edit immediately invalidates previous preflight ticket");Pump(400);Check(host.Calls==before&&vm.Design!.Inputs.RoofDatum==5.8,"Debounced parameter preview does not call Revit host");
     Validate(w);vm.Visualization.ShowPv=false;Check(generate.IsEnabled,"Presentation-only visibility toggle preserves canopy validation");
     Named<TextBox>(w,"Rotation").Text="37";Check(!generate.IsEnabled,"Rotation edit invalidates continuation");
     host.Bad=true;Validate(w);Check(!generate.IsEnabled,"Missing critical host preflight checks cannot enable continuation");host.Bad=false;Validate(w);Check(generate.IsEnabled,"Corrected explicit preflight restores continuation");
     Named<TabControl>(w,"DesignTabs").SelectedIndex=5;Pump(80);Capture(w,"01-scoped-canopy-only-ui.png");
     Named<TabControl>(w,"DesignTabs").SelectedIndex=3;Pump(80);Capture(w,"02-conflicts-remain-excluded.png");
     generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }catch(Exception e){error=e;w.Close();}
   });
   if(error is not null)throw error;Check(pending is not null,"Modal UI returns a pending scoped review instead of an unscoped GenerationRequest");
   var handoff=session.ConfirmForPlacement(pending!.Review,pending.Ticket,pending.ExplicitCanopyOnlyConfirmation);
   Check(handoff.LegacyRequest.Design.Inputs.RoofDatum==5.8&&handoff.LegacyRequest.RotationDegrees==37,"Changed WPF parameters and rotation reach the canonical canopy handoff");
   Check(!handoff.IncludesChargingEquipment&&!handoff.SiteLayoutApproved&&handoff.Equipment.BlockingConflicts==21,"Handoff excludes charging geometry and preserves conflict context without approving site");
   var cancelSession=new CanopyIntegrationSession(host);var cancelled=CanopyWindow.ShowCanopyOnlyForRevit(0,Array.Empty<DesignChoice>(),cancelSession,w=>w.Close());Check(cancelled is null,"Closing scoped dialog emits no handoff");
   File.WriteAllText(Path.Combine(output,"scoped-ui-validation.json"),JsonSerializer.Serialize(new{Status="PASS",Checks=checks.Count,Results=checks,Scope="Actual WPF in candidate private context, FAKE read-only host, no Revit/model operation",CandidateContext=AssemblyLoadContext.GetLoadContext(typeof(CanopyEngine).Assembly)!.Name},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS: {checks.Count} scoped private-context WPF checks.");return 0;
  }catch(Exception e){File.WriteAllText(Path.Combine(output,"scoped-ui-validation.json"),JsonSerializer.Serialize(new{Status="FAIL",Results=checks,Error=e.ToString()},new JsonSerializerOptions{WriteIndented=true}));Console.Error.WriteLine(e);return 1;}finally{app.Shutdown();}
 }
 static IEnumerable<Button> Buttons(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is Button b)yield return b;foreach(var value in Buttons(child))yield return value;}}
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 sealed class FakeHost : IReadOnlyCanopyHost
 {
  public int Calls;public bool Bad;
  public HostContext CaptureContext()=>new(new Guid("76ce73ab-17ad-4f00-8bcc-bb7e450858f9"),"fake-doc",@"C:\Offline\Fake.rvt","fake-level",0,CheckStatus.Pass,"FAKE host only; not Revit runtime proof");
  public ValidationReport Preflight(GenerationRequest request){Calls++;return Bad?new("Missing guards",Array.Empty<ValidationCheck>()):new("FAKE read-only preflight - no model",new[]{"Approved disposable document","Frozen component ledger and clearance","Design registry identity","Selected replacement ownership","Unique proposed Marks"}.Select(n=>new ValidationCheck(n,CheckStatus.Pass,"MOCK")).Concat(new[]{new ValidationCheck("Actual Revit transaction/reconciliation",CheckStatus.NotVerified,"Not executed")}).ToArray());}
 }
}
