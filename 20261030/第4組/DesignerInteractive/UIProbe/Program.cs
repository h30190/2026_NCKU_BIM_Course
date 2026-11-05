using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Canopy.UI;
using EVCanopyDesigner.Interactive;

internal static class Program
{
 [STAThread] public static int Main(string[] args)
 {
  if(args.Length!=1||Directory.Exists(args[0])){Console.Error.WriteLine("Supply a NEW evidence directory; verified artifacts are never overwritten.");return 1;}
  string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
  var results=new List<string>();var trace=new StringWriter();var listener=new TextWriterTraceListener(trace);
  PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new CanopyWindow();
  int preflightCalls=0;
  typeof(CanopyWindow).GetField("preflight",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,(Func<GenerationRequest,ValidationReport>)(_=>{preflightCalls++;return new("Offline fake preflight",new[]{new ValidationCheck("Revit",CheckStatus.NotVerified,"No Revit connection")});}));
  void Check(bool ok,string description){if(!ok)throw new Exception(description);results.Add(description);}
  T Named<T>(string name) where T:class=>window.FindName(name) as T??throw new Exception("Missing "+name);
  void Capture(string name){window.UpdateLayout();var visual=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);}
  GenerationRequest Request()=> (GenerationRequest)typeof(CanopyWindow).GetMethod("BuildRequest",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!;
  void Rejected(Action action){try{action();}catch(TargetInvocationException e) when(e.InnerException is ArgumentException){return;}throw new Exception("Expected guarded request rejection");}
  try{
   window.Show();window.Activate();Pump(300);
   var vm=(InteractiveDesignerViewModel)window.DataContext;var generate=Named<Button>("Generate");
   Check(vm.IsCurrent&&generate.Visibility==Visibility.Collapsed&&!generate.IsEnabled&&window.Request is null,"Standalone starts with current preview and generation hidden/disabled");
   Check(preflightCalls==0,"Initial automatic calculation never invokes model preflight");
   Check(Descendants<Expander>(window).Select(e=>e.Header?.ToString()).Where(n=>n is not null).Order().SequenceEqual(InteractiveDesignerViewModel.GroupNames.Order()),"All seven collapsible group headers rendered");
   Capture("01-overview.png");
   var equipment=Descendants<Expander>(window).Single(e=>e.Header?.ToString()=="EV Charging Equipment");equipment.IsExpanded=true;equipment.BringIntoView();Pump(100);
   Check(Descendants<TextBlock>(equipment).Any(t=>t.IsVisible&&t.Text.Contains("5 physical stations; 10 installed outputs; 10 assigned")),"Equipment group renders five physical stations and ten assigned points");
   Check(vm.Equipment is {PhysicalStations:5,AssignedPoints:10,Passed:false}&&Named<CanopyPreview>("PlanPreview").Equipment==vm.Equipment,"Physical station candidate is bound to the plan and flags actual spine conflicts");
   var defaultEquipment=vm.Equipment!;var defaultCanopy=vm.Design!;
   File.WriteAllText(Path.Combine(output,"equipment-layout.json"),JsonSerializer.Serialize(defaultEquipment,new JsonSerializerOptions{WriteIndented=true}));
   File.WriteAllText(Path.Combine(output,"equipment-config.json"),vm.ExportEquipment());
   File.WriteAllText(Path.Combine(output,"geometry-context.json"),JsonSerializer.Serialize(new{Site=defaultCanopy.SitePlan.Site,Spaces=defaultCanopy.SitePlan.Spaces,Spine=defaultCanopy.SitePlan.Regions.Single(r=>r.Kind==EVCanopyDesigner.Planner.Core.RegionKind.Spine).Bounds,Columns=defaultCanopy.Components.Where(c=>c.Role==ComponentRole.SteelColumn).Select(c=>new{c.Id,Solids=c.Solids.Select(s=>s.Footprint)})},new JsonSerializerOptions{WriteIndented=true}));
   File.WriteAllText(Path.Combine(output,"canopy-config.json"),ConfigurationCodec.Export(ConfigurationCodec.Create(defaultCanopy.SitePlan.Inputs,defaultCanopy.Inputs,0)));
   var widthSetting=vm.EquipmentSettings.Single(f=>f.Key=="EquipmentWidth");var widthBox=Descendants<TextBox>(equipment).Single(b=>ReferenceEquals(b.DataContext,widthSetting)&&b.GetBindingExpression(TextBox.TextProperty) is not null);
   string canopyFingerprint=vm.Design!.Fingerprint;string equipmentJson=vm.ExportEquipment();
   widthBox.Text="0.55";widthBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();Check(vm.Equipment is null&&!generate.IsEnabled,"Equipment edits immediately hide stale equipment and keep generation locked");Pump(500);
   Check(vm.Equipment!.Options.Width==.55&&vm.Design!.Fingerprint==canopyFingerprint,"Actual WPF equipment field updates station footprint without changing canopy fingerprint");
   widthBox.Text="NaN";widthBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();Pump(400);Check(vm.IsCurrent&&vm.Equipment is null&&vm.EquipmentValidation.Contains("INVALID"),"Invalid equipment input clears equipment without losing valid canopy calculations");
   vm.ImportEquipment(equipmentJson);Pump(100);Check(vm.Equipment!.Options.Width==.4,"Equipment JSON restores exact controls and physical footprints");
   Named<TabControl>("DesignTabs").SelectedIndex=5;vm.Visualization.ShowPv=false;Pump(100);Capture("07-five-station-plan.png");
   Check(Named<CanopyPreview>("ChargingPreview").Equipment!.Stations.Select(s=>s.Id).SequenceEqual(new[]{"S1","S2","S3","S4","S5"}),"Charging plan renders S1–S5 with exact opposite-row space IDs");
   Named<TabControl>("DesignTabs").SelectedIndex=3;Pump(100);Capture("08-assignments-validation.png");
   Check(Descendants<TextBlock>(window).Any(t=>t.IsVisible&&t.Text.Contains("S5-O2 → R2-S0005")),"Equipment tab displays output-to-space assignment diagram");
   vm.EquipmentSettings.Single(f=>f.Key=="EquipmentCableLength").Value="1";Pump(400);Check(vm.Equipment!.Issues.Count(i=>i.Code=="CableReach")==10,"Actual live refresh reports ten insufficient-cable conflicts");Capture("09-short-cable-conflict.png");
   vm.ImportEquipment(equipmentJson);Pump(100);vm.Visualization.ShowPv=true;Named<TabControl>("DesignTabs").SelectedIndex=0;
   Capture("06-equipment-planning.png");equipment.IsExpanded=false;Named<Expander>("BasicExpander").BringIntoView();Pump(100);
   var setting=vm.Settings.Single(f=>f.Key=="points");
   var slider=Descendants<Slider>(window).Single(s=>ReferenceEquals(s.DataContext,setting));slider.Focus();Keyboard.Focus(slider);Check(slider.IsKeyboardFocusWithin,"Actual WPF numeric slider can receive keyboard focus");
   int before=vm.CalculationCount;slider.SetCurrentValue(Slider.ValueProperty,12.0);slider.SetCurrentValue(Slider.ValueProperty,13.0);slider.SetCurrentValue(Slider.ValueProperty,15.0);
   Check(!vm.IsCurrent&&vm.Design is null&&vm.CalculationCount==before&&vm.PreviewPending,"Slider edits hide stale geometry immediately without synchronous calculation");
   Pump(500);Check(vm.IsCurrent&&vm.Design!.SitePlan.Spaces.Count==15&&vm.CalculationCount==before+1,"Rapid slider changes coalesce into one preview refresh");Check(preflightCalls==0&&!generate.IsEnabled,"Automatic refresh cannot invoke preflight or enable generation");
   var box=Descendants<TextBox>(window).Single(b=>ReferenceEquals(b.DataContext,setting)&&b.GetBindingExpression(TextBox.TextProperty) is not null);
   box.Focus();box.Text="15.5";box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();Pump(400);
   Check(!vm.IsCurrent&&vm.Design is null&&setting.Value=="15.5"&&setting.InputError.Length>0&&!generate.IsEnabled,"Invalid precise input stays visible and blocks preview/generation");
   Capture("02-invalid-input.png");box.Text="11";box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();Pump(400);Check(vm.IsCurrent&&vm.Design!.SitePlan.Spaces.Count==11,"Valid numeric correction recovers the preview");
   setting.Value="10";Pump(400);Check(vm.IsCurrent&&vm.Design!.SitePlan.Spaces.Count==10,"Roof comparison screenshots use the same ten-stall capacity");
   Named<Expander>("BasicExpander").IsExpanded=false;var canopy=Descendants<Expander>(window).Single(e=>e.Header?.ToString()=="Canopy & Structure");canopy.IsExpanded=true;Pump(100);
   var choices=Descendants<ComboBox>(canopy).Single(c=>ReferenceEquals(c.ItemsSource,vm.RoofChoices));choices.IsDropDownOpen=true;Pump(120);
   Check(vm.RoofChoices.All(r=>choices.ItemContainerGenerator.ContainerFromItem(r) is ComboBoxItem item&&item.IsEnabled==r.Enabled),"Actual roof selector disables flat, arch and wave while retaining both supported modes");
   choices.IsDropDownOpen=false;choices.SelectedItem=vm.RoofChoices.Single(r=>r.Key=="Butterfly");Pump(400);
   Check(vm.IsCurrent&&vm.Design!.Inputs.Drainage==DrainageDirection.TowardSpine,"Butterfly UI selection uses existing inward-drainage geometry");
   Named<TabControl>("DesignTabs").SelectedIndex=1;Pump(100);Capture("03-butterfly-section.png");
   int calculations=vm.CalculationCount;string fingerprint=vm.Design!.Fingerprint;vm.Visualization.ShowPv=false;Pump(80);
   Check(vm.CalculationCount==calculations&&vm.Design.Fingerprint==fingerprint&&!Named<CanopyPreview>("PlanPreview").ShowPv,"Visualization toggle redraws without changing calculated design");vm.Visualization.ShowPv=true;
   var id=Guid.NewGuid().ToString("D");var config=ConfigurationCodec.Create(new(RequestedPoints:11),new(){Drainage=DrainageDirection.TowardSpine,ColumnMode=ColumnMode.Manual,ManualColumnOffsets=new[]{1.0,6.0,11.0}},25);
   var choice=new DesignChoice(id,"Offline saved design",ConfigurationCodec.Export(config),true);
   var designs=Named<ComboBox>("DesignChoices");designs.ItemsSource=new[]{choice};Named<RadioButton>("Replace").IsChecked=true;designs.SelectedItem=choice;Pump(100);
   var request=Request();Check(request.ReplaceDesignId==id&&request.ProposedDesignId==id&&request.RotationDegrees==25&&vm.Design!.ColumnXs.Count==3,"Existing-design selection restores saved inputs, identity and rotation");
   var legacy=choice with{Id=Guid.NewGuid().ToString("D"),PlacementKnown=false};designs.ItemsSource=new[]{legacy};designs.SelectedItem=legacy;Rejected(()=>Request());Named<CheckBox>("LegacyConfirm").IsChecked=true;Check(Request().ReplaceDesignId==legacy.Id,"Legacy replacement still requires explicit rotation review");
   Named<TextBox>("Rotation").Text="NaN";Rejected(()=>Request());Check(!Named<Slider>("RotationSlider").IsEnabled,"Invalid rotation disables its slider without coercing text");Named<TextBox>("Rotation").Text="721.123456789";Check(Request().RotationDegrees==721.123456789&&Named<Slider>("RotationSlider").Maximum>=721.123456789,"Precise imported/typed rotation is retained beyond the navigation range");Named<TextBox>("Rotation").Text="25";Check(!generate.IsEnabled,"Rotation edits invalidate generation authorization");
   var validate=Descendants<Button>(window).Single(b=>b.Content?.ToString()=="Validate & Preview — No Revit Changes");validate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(preflightCalls==1&&!generate.IsEnabled&&window.Request is null,"Only explicit validation invokes fake preflight; standalone generation remains blocked");
   Named<TabControl>("DesignTabs").SelectedIndex=4;Pump(100);Capture("04-material-takeoff.png");
   window.Width=1100;window.Height=720;Pump(100);var pos=validate.TranslatePoint(new Point(0,0),window);Check(pos.Y>=0&&pos.Y+validate.ActualHeight<=window.ActualHeight,"Action controls remain within the compact window");Capture("05-compact-layout.png");
   Check(vm.RoofChoices.Single(r=>r.Key=="Outward").Label.Contains("positive fall")&&!vm.RoofChoices.Single(r=>r.Key=="Flat").Enabled,"Low-slope terminology retains positive fall and disables true flat");
   Check(window.Request is null,"No generation request was accepted during the probe");
   listener.Flush();Check(!trace.ToString().Contains("Error:",StringComparison.Ordinal),"No WPF binding errors");
   File.WriteAllText(Path.Combine(output,"ui-validation.json"),JsonSerializer.Serialize(new{Status="PASS",Checks=results.Count,Results=results,Scope="Standalone WPF only; fake preflight callback, no Revit/MCP, no deployment",BindingTrace=trace.ToString()},new JsonSerializerOptions{WriteIndented=true}));
   Console.WriteLine($"PASS: {results.Count} actual WPF checks; screenshots: {output}");return 0;
  }catch(Exception error){File.WriteAllText(Path.Combine(output,"ui-validation.json"),JsonSerializer.Serialize(new{Status="FAIL",Results=results,Error=error.ToString(),BindingTrace=trace.ToString()},new JsonSerializerOptions{WriteIndented=true}));Console.Error.WriteLine(error);return 1;}
  finally{window.Close();app.Shutdown();PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);listener.Dispose();}
 }
 private static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var descendant in Descendants<T>(child))yield return descendant;}}
}
