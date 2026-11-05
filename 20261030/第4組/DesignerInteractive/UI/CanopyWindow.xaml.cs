using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;
using System.Windows.Threading;
namespace EVCanopyDesigner.Canopy.UI;
public partial class CanopyWindow : Window
{
 private readonly InteractiveDesignerViewModel vm=new();private readonly string proposedId=Guid.NewGuid().ToString("D");
 private readonly DispatcherTimer previewTimer=new(){Interval=TimeSpan.FromMilliseconds(180)};
 private bool allowGeneration;private Func<GenerationRequest,ValidationReport>? preflight;
 private CanopyIntegrationSession? integrationSession;
 private CanopyReview? preparedReview;
 private CanopyPreflightTicket? canopyTicket;
 public PendingCanopyReview? PendingReview{get;private set;}
 public GenerationRequest? Request{get;private set;}
 public CanopyWindow(){InitializeComponent();DataContext=vm;vm.PropertyChanged+=(_,_)=>{RefreshPreview();InvalidatePreflight();};vm.InputEdited+=(_,_)=>{InvalidatePreflight();previewTimer.Stop();previewTimer.Start();};vm.Visualization.PropertyChanged+=(_,_)=>RefreshPreview();previewTimer.Tick+=(_,_)=>{previewTimer.Stop();vm.Calculate();};Closed+=(_,_)=>{previewTimer.Stop();if(PendingReview is null)integrationSession?.Invalidate();};Loaded+=(_,_)=>{var area=SystemParameters.WorkArea;MinWidth=Math.Min(MinWidth,area.Width);MinHeight=Math.Min(MinHeight,area.Height);Width=Math.Min(Width,area.Width);Height=Math.Min(Height,area.Height);Generate.Visibility=allowGeneration?Visibility.Visible:Visibility.Collapsed;vm.Calculate();};}
 private void RefreshPreview(){foreach(var preview in new[]{PlanPreview,SectionPreview,ChargingPreview}){preview.Design=vm.Design;preview.Equipment=vm.Equipment;preview.EquipmentDetail=ReferenceEquals(preview,ChargingPreview);preview.ShowPv=vm.Visualization.ShowPv;preview.ShowRoof=vm.Visualization.ShowRoof;preview.ShowStructure=vm.Visualization.ShowStructure;preview.InvalidateVisual();}}
 private void SliderChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(sender is Slider {DataContext:SettingControl field} slider&&(slider.IsMouseCaptureWithin||slider.IsKeyboardFocusWithin))field.SetFromSlider(e.NewValue);}
 private void InvalidatePreflight(){integrationSession?.Invalidate();preparedReview=null;canopyTicket=null;PendingReview=null;if(Generate is null)return;Generate.IsEnabled=false;if(PreflightStatus is not null)PreflightStatus.Text="Out of date — Validate & Preview before generation.";}
 private void ModeChanged(object sender,RoutedEventArgs e){InvalidatePreflight();if(Replace is not null&&Replace.IsChecked==true&&DesignChoices is not null)LoadSelected();}
 private void ReviewChanged(object sender,RoutedEventArgs e)=>InvalidatePreflight();
 private void PlacementChanged(object sender,TextChangedEventArgs e){InvalidatePreflight();if(RotationSlider is null)return;bool valid=double.TryParse(Rotation.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double angle)&&double.IsFinite(angle);RotationSlider.IsEnabled=valid;if(valid){RotationSlider.Minimum=Math.Min(-180,angle);RotationSlider.Maximum=Math.Max(180,angle);RotationSlider.Value=angle;}}
 private void RotationSliderChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(sender is Slider slider&&(slider.IsMouseCaptureWithin||slider.IsKeyboardFocusWithin)&&Rotation is not null)Rotation.Text=Math.Round(e.NewValue).ToString(CultureInfo.CurrentCulture);}
 private void SelectedDesignChanged(object sender,SelectionChangedEventArgs e)=>LoadSelected();
 private void LoadSelected(){previewTimer.Stop();InvalidatePreflight();if(DesignChoices.SelectedItem is DesignChoice c&&Replace.IsChecked==true&&c.ConfigurationJson is not null){try{var config=ConfigurationCodec.Import(c.ConfigurationJson);vm.LoadConfiguration(config);Rotation.Text=config.RotationDegrees.ToString(CultureInfo.CurrentCulture);LegacyConfirm.IsChecked=false;PreflightStatus.Text=c.PlacementKnown?"Selected saved inputs loaded. Choose a new placement after validation.":"Legacy inputs loaded; old rotation was not stored. Review rotation and check Legacy rotation reviewed.";}catch(Exception ex){PreflightStatus.Text=ex.Message;}}}
 private GenerationRequest BuildRequest(){if(vm.Design is null)throw new ArgumentException("Calculate a valid design first.");var choice=DesignChoices.SelectedItem as DesignChoice;if(Replace.IsChecked==true&&(choice is null||(!choice.PlacementKnown&&LegacyConfirm.IsChecked!=true)))throw new ArgumentException("Select the owned design and explicitly review legacy rotation if needed.");if(!double.TryParse(Rotation.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double angle)||!double.IsFinite(angle))throw new ArgumentException("Enter a finite rotation angle.");return new(vm.Design,Replace.IsChecked==true?choice!.Id:null,angle,Replace.IsChecked==true?choice!.Id:proposedId);}
 private void Calculate_Click(object sender,RoutedEventArgs e)
 {
  previewTimer.Stop();vm.Calculate();if(!vm.IsCurrent)return;
  try{
   var request=BuildRequest();
   if(integrationSession is not null){
    preparedReview=integrationSession.Prepare(IntegrationScope.CanopyOnly,request,vm.Equipment?.Options,ScopeAck.IsChecked==true);
    canopyTicket=integrationSession.Validate(preparedReview);
    PreflightStatus.Text=CanopyReview.ExclusionNotice+"\n"+canopyTicket.Report.Summary;
    Generate.IsEnabled=allowGeneration&&ScopeAck.IsChecked==true;
   }else{
    var report=preflight?.Invoke(request)??new ValidationReport("Standalone preview",new[]{new ValidationCheck("Revit identifiers, ownership and transaction",CheckStatus.NotVerified,"No Revit context; no generation permitted.")});
    PreflightStatus.Text=report.Summary;Generate.IsEnabled=false;
   }
  }catch(Exception ex){InvalidatePreflight();PreflightStatus.Text=ex.Message;}
 }
 private void Generate_Click(object sender,RoutedEventArgs e)
 {
  if(integrationSession is null||!allowGeneration||!Generate.IsEnabled)return;
  try{
   _=BuildRequest();
   if(ScopeAck.IsChecked!=true||preparedReview is null||canopyTicket is null||canopyTicket.Consumed||canopyTicket.ExpiresUtc<=DateTimeOffset.UtcNow)throw new InvalidOperationException("Fresh canopy-only validation and explicit scope confirmation required.");
   PendingReview=new(preparedReview,canopyTicket,true);DialogResult=true;
  }catch(Exception ex){InvalidatePreflight();PreflightStatus.Text=ex.Message;}
 }
 private void Import_Click(object sender,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Filter="Design configuration (*.json)|*.json"};if(d.ShowDialog(this)!=true)return;try{previewTimer.Stop();var c=ConfigurationCodec.Import(File.ReadAllText(d.FileName));vm.LoadConfiguration(c);Rotation.Text=c.RotationDegrees.ToString(CultureInfo.CurrentCulture);}catch(Exception ex){InvalidatePreflight();PreflightStatus.Text="Import refused: "+ex.Message;}}
 private void Export_Click(object sender,RoutedEventArgs e){try{var r=BuildRequest();var json=ConfigurationCodec.Export(ConfigurationCodec.Create(r.Design.SitePlan.Inputs,r.Design.Inputs,r.RotationDegrees));var d=new Microsoft.Win32.SaveFileDialog{Filter="Design configuration (*.json)|*.json",DefaultExt=".json",AddExtension=true,OverwritePrompt=true};if(d.ShowDialog(this)==true){if(!string.Equals(Path.GetExtension(d.FileName),".json",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only .json export is permitted.");File.WriteAllText(d.FileName,json);}}catch(Exception ex){PreflightStatus.Text="Export refused: "+ex.Message;}}
 private void ImportEquipment_Click(object sender,RoutedEventArgs e){var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Equipment configuration (*.json)|*.json"};if(dialog.ShowDialog(this)!=true)return;try{previewTimer.Stop();vm.ImportEquipment(File.ReadAllText(dialog.FileName));}catch(Exception ex){InvalidatePreflight();PreflightStatus.Text="Equipment import refused: "+ex.Message;}}
 private void ExportEquipment_Click(object sender,RoutedEventArgs e){try{var json=vm.ExportEquipment();var dialog=new Microsoft.Win32.SaveFileDialog{Filter="Equipment configuration (*.json)|*.json",DefaultExt=".json",AddExtension=true,OverwritePrompt=true};if(dialog.ShowDialog(this)==true){if(!string.Equals(Path.GetExtension(dialog.FileName),".json",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only .json export permitted.");File.WriteAllText(dialog.FileName,json);}}catch(Exception ex){PreflightStatus.Text="Equipment export refused: "+ex.Message;}}
 private void Close_Click(object sender,RoutedEventArgs e)=>Close();
 // Retained legacy signature is preview-only; never emit an unscoped generation request.
 public static GenerationRequest? ShowForRevit(long owner,DesignChoice[] choices,Func<GenerationRequest,ValidationReport> validator){var w=new CanopyWindow();w.preflight=validator;w.DesignChoices.ItemsSource=choices;if(owner!=0)new WindowInteropHelper(w).Owner=new IntPtr(owner);w.ShowDialog();return null;}
 public static CanopyWindow CreateForCanopyHost(long owner,DesignChoice[] choices,CanopyIntegrationSession session)
 {
  ArgumentNullException.ThrowIfNull(session);
  var window=new CanopyWindow{integrationSession=session,allowGeneration=true};
  window.DesignChoices.ItemsSource=choices;window.ScopeAck.Visibility=Visibility.Visible;window.Generate.Content="Continue with CANOPY ONLY…";
  window.ScopeModeText.Text=CanopyReview.ExclusionNotice;window.ScopeModeText.TextWrapping=TextWrapping.Wrap;
  window.Title="EV Hub POC — CANOPY ONLY — equipment/site not approved";
  if(owner!=0)new WindowInteropHelper(window).Owner=new IntPtr(owner);
  window.WindowStartupLocation=WindowStartupLocation.CenterOwner;return window;
 }
 public static PendingCanopyReview? ShowCanopyOnlyForRevit(long owner,DesignChoice[] choices,CanopyIntegrationSession session,Action<CanopyWindow>? offlineOrPocDriver=null)
 {
  var window=CreateForCanopyHost(owner,choices,session);
  if(offlineOrPocDriver is not null)window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(()=>offlineOrPocDriver(window));
  bool? result=window.ShowDialog();return result==true?window.PendingReview:null;
 }
}
