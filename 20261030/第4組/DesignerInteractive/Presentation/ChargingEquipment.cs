using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Planner.Core;
namespace EVCanopyDesigner.Interactive;

// Offline companion only. These assumptions never enter the approved canopy codec or component ledger.
public sealed record EquipmentOptions
{
 public double Width {get;init;}=.4;
 public double Depth {get;init;}=.4;
 public double Height {get;init;}=1.5;
 public double OffsetX {get;init;}=0;
 public double OffsetY {get;init;}=0;
 public double ServiceMargin {get;init;}=.3;
 public double ServiceHeight {get;init;}=2;
 public double PedestrianWidth {get;init;}=.9;
 public double CableLength {get;init;}=4;
 public double CableSlack {get;init;}=.5;
 public double OutputHeight {get;init;}=.9;
 public double InletHeight {get;init;}=.7;
 public double InletSetback {get;init;}=1;
 public double InletOffsetX {get;init;}=0;
 public int ConnectorsPerOutput {get;init;}=1;
}
public sealed record EquipmentIssue(string Code,string ObjectId,string Detail,bool Blocking=true);
public sealed record OutputAssignment(string Id,string? SpaceId,double StartX,double StartY,double EndX,double EndY,double RequiredCable,bool ReachPassed);
public sealed record StationPlacement(string Id,RectM Footprint,RectM ServiceZone,double Height,IReadOnlyList<OutputAssignment> Outputs);
public sealed record EquipmentLayout(string CanopyFingerprint,string Fingerprint,EquipmentOptions Options,RectM PedestrianRoute,
 IReadOnlyList<StationPlacement> Stations,IReadOnlyList<EquipmentIssue> Issues)
{
 public int PhysicalStations=>Stations.Count;
 public int InstalledOutputs=>Stations.Count*2;
 public int PhysicalConnectors=>InstalledOutputs*Options.ConnectorsPerOutput;
 public int AssignedPoints=>Stations.Sum(s=>s.Outputs.Count(o=>o.SpaceId is not null));
 public int SpareOutputs=>InstalledOutputs-AssignedPoints;
 public bool Passed=>!Issues.Any(i=>i.Blocking);
 public string Summary=>$"{PhysicalStations} physical stations; {InstalledOutputs} installed outputs; {AssignedPoints} assigned points; {PhysicalConnectors} assumed connectors; {SpareOutputs} spare outputs. Target: {AssignedPoints} simultaneous cars; electrical capacity unverified. Geometry: {(Passed?"PASS under assumptions":"CONFLICT — candidate only")}.";
}
public static class EquipmentPlanner
{
 public static bool Overlap(RectM a,RectM b)=>a.X<b.Right-1e-9&&a.Right>b.X+1e-9&&a.Y<b.Top-1e-9&&a.Top>b.Y+1e-9;
 public static bool Contains(RectM a,RectM b)=>b.X>=a.X-1e-9&&b.Y>=a.Y-1e-9&&b.Right<=a.Right+1e-9&&b.Top<=a.Top+1e-9;
 public static void Validate(EquipmentOptions o)
 {
  ArgumentNullException.ThrowIfNull(o);
  foreach(var p in typeof(EquipmentOptions).GetProperties().Where(p=>p.PropertyType==typeof(double)))
  {double n=(double)p.GetValue(o)!;if(!double.IsFinite(n)||Math.Abs(n)>100)throw new ArgumentException(p.Name+": finite value within ±100 m required.");}
  foreach(double n in new[]{o.Width,o.Depth,o.Height,o.ServiceHeight,o.PedestrianWidth,o.CableLength})if(n<=0)throw new ArgumentException("Equipment dimensions, service height, pedestrian width and cable length must be positive.");
  if(o.ServiceMargin<0||o.CableSlack<0||o.OutputHeight<0||o.OutputHeight>o.Height||o.InletHeight<0||o.InletSetback<0)throw new ArgumentException("Margins, slack and heights cannot be negative; output height must fit station.");
  if(o.ServiceHeight<o.Height)throw new ArgumentException("Service height must be at least station height.");
  if(o.ConnectorsPerOutput is <1 or >4)throw new ArgumentException("Connectors per output must be 1–4; alternatives do not add simultaneous outputs.");
 }
 public static EquipmentLayout Calculate(CanopyDesign d,EquipmentOptions o)
 {
  ArgumentNullException.ThrowIfNull(d);Validate(o);
  var plan=d.SitePlan;var spine=plan.Regions.Single(r=>r.Kind==RegionKind.Spine).Bounds;
  var a=plan.Spaces.Where(s=>s.Row==1).OrderBy(s=>s.Bounds.X).ToArray();var b=plan.Spaces.Where(s=>s.Row==2).OrderBy(s=>s.Bounds.X).ToArray();
  var issues=new List<EquipmentIssue>();var stations=new List<StationPlacement>();
  void Issue(string code,string id,string detail,bool blocking=true)=>issues.Add(new(code,id,detail,blocking));
  if(Math.Abs(a.Length-b.Length)>1)Issue("AsymmetricRows","Layout","Opposite-row pairing requires more than ceil(spaces/2) stations. Rebalance rows or review a different placement strategy; no spaces moved.");
  var route=new RectM(spine.X,spine.Top-o.PedestrianWidth,spine.Width,o.PedestrianWidth);
  if(!Contains(spine,route))Issue("PedestrianBoundary","Route","Configured continuous +Y-side pedestrian strip does not fit the spine.");
  var solids=d.Components.SelectMany(c=>c.Solids.Select(s=>new{c.Id,c.Role,Bounds=s.Footprint,MinZ=s.Vertices.Min(v=>v.Z),MaxZ=s.Vertices.Max(v=>v.Z)})).ToArray();
  foreach(var solid in solids.Where(s=>s.MaxZ>0&&s.MinZ<o.ServiceHeight&&Overlap(s.Bounds,route)))Issue("PedestrianSupport","Route",$"Continuous pedestrian strip intersects {solid.Id}. End-to-end pedestrian/service access is not established.");
  for(int k=0;k<Math.Max(a.Length,b.Length);k++)
  {
   ParkingSpace? sa=k<a.Length?a[k]:null,sb=k<b.Length?b[k]:null;var reference=sa??sb!;
   double x=reference.Bounds.X+reference.Bounds.Width/2+o.OffsetX,y=spine.Y+spine.Height/2+o.OffsetY;
   var body=new RectM(x-o.Width/2,y-o.Depth/2,o.Width,o.Depth);
   var service=new RectM(body.X-o.ServiceMargin,body.Y-o.ServiceMargin,body.Width+2*o.ServiceMargin,body.Height+2*o.ServiceMargin);
   string id=$"S{k+1}";var outputs=new List<OutputAssignment>();
   if(!Contains(spine,body))Issue("StationBoundary",id,"Physical station leaves reserved spine. Consider offset adjustment or a separately reviewed end/side equipment bay.");
   if(!Contains(spine,service))Issue("ServiceBoundary",id,"Maintenance envelope leaves spine; no stall or aisle has been resized.");
   foreach(var r in plan.Regions.Where(r=>r.Kind is RegionKind.Parking or RegionKind.OutsideAisle))
   {
    if(Overlap(body,r.Bounds))Issue("StationRegion",id,$"Physical footprint overlaps {r.Id} ({r.Kind}).");
    if(Overlap(service,r.Bounds))Issue("ServiceRegion",id,$"Maintenance envelope overlaps {r.Id} ({r.Kind}).");
   }
   if(Overlap(service,route))Issue("ServicePedestrian",id,"Maintenance zone occupies configured pedestrian strip; uninterrupted access is not established.");
   if(Overlap(body,route))Issue("PedestrianStation",id,"Station obstructs configured continuous pedestrian strip.");
   foreach(var solid in solids)
   {
    if(solid.MaxZ>0&&solid.MinZ<o.Height&&Overlap(body,solid.Bounds))Issue("StationSolid",id,$"Physical station intersects conservative bounds of {solid.Id} ({solid.Role}).");
    if(solid.MaxZ>0&&solid.MinZ<o.ServiceHeight&&Overlap(service,solid.Bounds))Issue("ServiceSolid",id,$"Maintenance envelope intersects {solid.Id}; adjust equipment offsets or review a separate equipment bay.");
   }
   for(int row=1;row<=2;row++)
   {
    var space=row==1?sa:sb;double startY=row==1?body.Y:body.Top;
    double endX=space is null?x:space.Bounds.X+space.Bounds.Width/2+o.InletOffsetX;
    double endY=space is null?startY:row==1?space.Bounds.Top-o.InletSetback:space.Bounds.Y+o.InletSetback;
    double required=space is null?0:Math.Sqrt(Math.Pow(endX-x,2)+Math.Pow(endY-startY,2)+Math.Pow(o.InletHeight-o.OutputHeight,2))+o.CableSlack;
    bool reach=required<=o.CableLength+1e-9;string output=$"{id}-O{row}";
    outputs.Add(new(output,space?.Id,x,startY,endX,endY,required,reach));
    if(space is null)continue;
    if(endX<space.Bounds.X||endX>space.Bounds.Right||endY<space.Bounds.Y||endY>space.Bounds.Top)Issue("InletOutsideStall",output,"Assumed vehicle inlet is outside its assigned stall; revise setback/offset assumptions.");
    if(!reach)Issue("CableReach",output,$"{space.Id}: needs {required:F3} m including slack; available {o.CableLength:F3} m.");
    foreach(var solid in solids.Where(s=>s.MaxZ>Math.Min(o.OutputHeight,o.InletHeight)&&s.MinZ<Math.Max(o.OutputHeight,o.InletHeight)+.001))
     if(SegmentHits(x,startY,endX,endY,solid.Bounds))Issue("CableObstacle",output,$"Straight cable path intersects bounds of {solid.Id}; routed cable design required.");
    if(SegmentHits(x,startY,endX,endY,route))Issue("CablePedestrian",output,"Cable crosses pedestrian strip: trip protection or alternative routing is required.");
   }
   stations.Add(new(id,body,service,o.Height,Array.AsReadOnly(outputs.ToArray())));
  }
  for(int k=0;k<stations.Count;k++)for(int j=k+1;j<stations.Count;j++)
  {
   var s=stations[k];var t=stations[j];
   if(Overlap(s.Footprint,t.Footprint))Issue("StationStation",s.Id,$"Physical overlap with {t.Id}.");
   if(Overlap(s.ServiceZone,t.ServiceZone))Issue("ServiceService",s.Id,$"Maintenance zones overlap {t.Id}; independent servicing not demonstrated.");
  }
  foreach(var s in stations)foreach(var output in s.Outputs.Where(o=>o.SpaceId is not null))foreach(var other in stations.Where(t=>t.Id!=s.Id))
   if(Math.Min(o.OutputHeight,o.InletHeight)<other.Height&&SegmentHits(output.StartX,output.StartY,output.EndX,output.EndY,other.Footprint))Issue("CableStation",output.Id,$"Cable crosses {other.Id}.");
  Issue("Assumptions","Layout","Rectangular station/maintenance bounds, a continuous +Y-side strip connected to end reservations, and hypothetical nose-in vehicle inlets only. End reservations are not verified site entrances. No accessible-route, hydraulic, electrical, foundation, swept-path or manufacturer approval.",false);
  return new(d.Fingerprint,EquipmentCodec.Fingerprint(d.Fingerprint,o),o,route,Array.AsReadOnly(stations.ToArray()),Array.AsReadOnly(issues.ToArray()));
 }
 // Segment versus open rectangle interior. Conservative plan obstacle test at cable-height range.
 public static bool SegmentHits(double x0,double y0,double x1,double y1,RectM r)
 {
  double lo=0,hi=1;
  bool Clip(double start,double delta,double min,double max){if(Math.Abs(delta)<1e-12)return start>min+1e-9&&start<max-1e-9;double a=(min-start)/delta,b=(max-start)/delta;if(a>b)(a,b)=(b,a);lo=Math.Max(lo,a);hi=Math.Min(hi,b);return lo<hi-1e-9;}
  return Clip(x0,x1-x0,r.X,r.Right)&&Clip(y0,y1-y0,r.Y,r.Top)&&hi>0&&lo<1;
 }
}
public sealed record EquipmentConfiguration(int Schema,string Units,string CanopyFingerprint,EquipmentOptions Options,string Fingerprint);
public static class EquipmentCodec
{
 private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 public static string Fingerprint(string canopy,EquipmentOptions options)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Version=1,CanopyFingerprint=canopy,Options=options}))));
 public static string Export(EquipmentLayout layout)=>JsonSerializer.Serialize(new EquipmentConfiguration(1,"m",layout.CanopyFingerprint,layout.Options,layout.Fingerprint),Json);
 public static EquipmentOptions Import(string json,string expectedCanopy)
 {
  if(json.Length>262144)throw new ArgumentException("Equipment JSON exceeds size limit.");
  try{
   using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
   Require(root,new[]{"Schema","Units","CanopyFingerprint","Options","Fingerprint"});Require(root.GetProperty("Options"),typeof(EquipmentOptions).GetProperties().Select(p=>p.Name));
   var c=JsonSerializer.Deserialize<EquipmentConfiguration>(json,Json)??throw new ArgumentException("Equipment configuration is null.");
   if(c.Schema!=1||c.Units!="m"||c.CanopyFingerprint!=expectedCanopy)throw new ArgumentException("Equipment schema, units or canopy binding mismatch; load its matching canopy first.");
   EquipmentPlanner.Validate(c.Options);
   if(c.Fingerprint!=Fingerprint(c.CanopyFingerprint,c.Options))throw new ArgumentException("Equipment fingerprint mismatch.");
   return c.Options;
  }catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException){throw new ArgumentException("Invalid equipment JSON: "+e.Message,e);}
 }
 private static void Require(JsonElement element,IEnumerable<string> keys)
 {
  if(element.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Expected equipment JSON object.");
  var actual=element.EnumerateObject().Select(p=>p.Name).Order().ToArray();if(!actual.SequenceEqual(keys.Order()))throw new ArgumentException("Equipment JSON has missing, duplicate or unknown fields.");
 }
}
