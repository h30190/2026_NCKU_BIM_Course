using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Interactive;
namespace EVCanopyDesigner.Canopy.UI;
public sealed class CanopyPreview : FrameworkElement
{
 public CanopyDesign? Design{get;set;}
 public EquipmentLayout? Equipment{get;set;}
 public bool EquipmentDetail{get;set;}
 public bool Section{get;set;}
 public bool ShowPv{get;set;}=true;
 public bool ShowRoof{get;set;}=true;
 public bool ShowStructure{get;set;}=true;
 private bool Visible(CanopyComponent c)=>c.Role==ComponentRole.PvPanel?ShowPv:c.Role==ComponentRole.RoofPanel?ShowRoof:c.Role is ComponentRole.SteelColumn or ComponentRole.GlulamArm or ComponentRole.GlulamPurlin?ShowStructure:true;
 public CanopyPreview(){SizeChanged+=(_,_)=>InvalidateVisual();ClipToBounds=true;}
 protected override void OnRender(DrawingContext dc){
  base.OnRender(dc);dc.DrawRectangle(Brushes.WhiteSmoke,null,new Rect(0,0,ActualWidth,ActualHeight));
  void Text(string s,double x,double y,double size=12){var t=new FormattedText(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(1,ActualWidth-x-8)};dc.DrawText(t,new Point(x,y));}
  if(Design is null){Text("No current proposal. Calculate valid inputs.",15,15);return;}
  var d=Design;
  Brush Color(ComponentRole role)=>role switch{ComponentRole.SteelColumn=>Brushes.DarkSlateGray,ComponentRole.GlulamArm or ComponentRole.GlulamPurlin=>Brushes.SandyBrown,ComponentRole.RoofPanel=>Brushes.Beige,ComponentRole.PvPanel=>Brushes.SteelBlue,ComponentRole.Gutter=>Brushes.Gray,_=>Brushes.LightSkyBlue};
  if(!Section){var site=d.SitePlan.Site;var view=EquipmentDetail?new EVCanopyDesigner.Planner.Core.RectM(d.Canopy.X-1,d.Canopy.Y-1,d.Canopy.Width+2,d.Canopy.Height+2):site;double scale=Math.Max(.001,Math.Min(Math.Max(1,ActualWidth-80)/view.Width,Math.Max(1,ActualHeight-130)/view.Height));Point P(double x,double y)=>new((ActualWidth-view.Width*scale)/2+(x-view.X)*scale,55+(view.Top-y)*scale);
   foreach(var r in d.SitePlan.Regions){var a=P(r.Bounds.X,r.Bounds.Top);dc.DrawRectangle(r.Kind==EVCanopyDesigner.Planner.Core.RegionKind.Spine?Brushes.LightGreen:r.Kind==EVCanopyDesigner.Planner.Core.RegionKind.EndAllowance?Brushes.PeachPuff:Brushes.LightGray,new Pen(Brushes.Gray,.5),new Rect(a,new Size(r.Bounds.Width*scale,r.Bounds.Height*scale)));}
   foreach(var c in d.Components.Where(Visible).OrderBy(c=>c.Role==ComponentRole.PvPanel?2:c.Role==ComponentRole.SteelColumn?3:1))foreach(var s in c.Solids){var b=s.Footprint;var a=P(b.X,b.Top);dc.DrawRectangle(c.Role==ComponentRole.RoofPanel?Brushes.Transparent:Color(c.Role),new Pen(Brushes.DimGray,.4),new Rect(a,new Size(b.Width*scale,b.Height*scale)));}
   var ca=P(d.Canopy.X,d.Canopy.Top);dc.DrawRectangle(null,new Pen(Brushes.DarkRed,2),new Rect(ca,new Size(d.Canopy.Width*scale,d.Canopy.Height*scale)));
   if(Equipment is { } equipment){
    var route=equipment.PedestrianRoute;var rp=P(route.X,route.Top);dc.DrawRectangle(new SolidColorBrush(ColorFromArgb(45,80,180,100)),new Pen(Brushes.ForestGreen,1){DashStyle=DashStyles.Dash},new Rect(rp,new Size(route.Width*scale,route.Height*scale)));
    foreach(var station in equipment.Stations){
     bool conflict=equipment.Issues.Any(i=>i.Blocking&&(i.ObjectId==station.Id||station.Outputs.Any(o=>o.Id==i.ObjectId)));var brush=conflict?Brushes.Crimson:Brushes.DarkMagenta;
     foreach(var output in station.Outputs.Where(o=>o.SpaceId is not null)){
      var p0=P(output.StartX,output.StartY);var p1=P(output.EndX,output.EndY);dc.DrawLine(new Pen(output.ReachPassed?Brushes.DarkCyan:Brushes.Red,1.5){DashStyle=DashStyles.Dash},p0,p1);dc.DrawEllipse(Brushes.White,new Pen(Brushes.DarkCyan,1),p1,3,3);
      if(EquipmentDetail){var space=d.SitePlan.Spaces.Single(s=>s.Id==output.SpaceId);var label=P(space.Bounds.X,space.Row==1?space.Bounds.Y+1:space.Bounds.Top-.5);Text(space.Id,label.X,label.Y,10);}
     }
     var zone=station.ServiceZone;dc.DrawRectangle(null,new Pen(brush,1){DashStyle=DashStyles.Dot},new Rect(P(zone.X,zone.Top),new Size(zone.Width*scale,zone.Height*scale)));
     var b=station.Footprint;var point=P(b.X,b.Top);dc.DrawRectangle(brush,new Pen(Brushes.White,1),new Rect(point,new Size(b.Width*scale,b.Height*scale)));Text(station.Id,point.X-2,point.Y-18,12);
    }
   }
   Text($"Site {site.Width:F2} × {site.Height:F2} m | canopy {d.Canopy.Width:F2} × {d.Canopy.Height:F2} m",12,10,14);
   Text("Red/purple: station | dotted: service zone | cyan: assumed cable | green dash: pedestrian strip; red = conflict",12,ActualHeight-55);
  }else{
   var all=d.Components.SelectMany(c=>c.Solids).SelectMany(s=>s.Vertices).ToArray();double minY=all.Min(p=>p.Y),maxY=all.Max(p=>p.Y),maxZ=all.Max(p=>p.Z);
   double scale=Math.Max(.001,Math.Min(Math.Max(1,ActualWidth-90)/(maxY-minY),Math.Max(1,ActualHeight-130)/maxZ));Point P(double y,double z)=>new(45+(y-minY)*scale,ActualHeight-65-z*scale);
   foreach(var c in d.Components.Where(Visible))foreach(var s in c.Solids){var hull=Hull(s.Vertices.Select(p=>P(p.Y,p.Z)).ToArray());if(hull.Length<3)continue;var g=new StreamGeometry();using(var ctx=g.Open()){ctx.BeginFigure(hull[0],true,true);ctx.PolyLineTo(hull.Skip(1).ToArray(),true,false);}dc.DrawGeometry(Color(c.Role),new Pen(Brushes.DimGray,.5),g);}
   dc.DrawLine(new Pen(Brushes.Black,1),P(minY,0),P(maxY,0));dc.DrawLine(new Pen(Brushes.DarkRed,1){DashStyle=DashStyles.Dash},P(minY,d.Inputs.MinimumClearance),P(maxY,d.Inputs.MinimumClearance));
   Text($"Section projected across Y | red: required {d.Inputs.MinimumClearance:F2} m | lowest overhead {d.LowestOverhead:F3} m",12,10,14);
   Text("Base = selected design level. Columns are grade-supported; their footprints must fit the reserved spine. Connections are schematic.",12,ActualHeight-45);
  }
 }
 private static Color ColorFromArgb(byte a,byte r,byte g,byte b)=>System.Windows.Media.Color.FromArgb(a,r,g,b);
 private static Point[] Hull(Point[] points){var p=points.Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToArray();if(p.Length<3)return p;double Cross(Point a,Point b,Point c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);var h=new List<Point>();foreach(var q in p){while(h.Count>=2&&Cross(h[^2],h[^1],q)<=0)h.RemoveAt(h.Count-1);h.Add(q);}int lower=h.Count;for(int k=p.Length-2;k>=0;k--){while(h.Count>lower&&Cross(h[^2],h[^1],p[k])<=0)h.RemoveAt(h.Count-1);h.Add(p[k]);}h.RemoveAt(h.Count-1);return h.ToArray();}
}
