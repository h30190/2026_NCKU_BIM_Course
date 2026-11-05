using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace CadTerrainKit
{
    public class Building
    {
        public string id { get; set; }
        public List<double[]> points { get; set; }
        public double base_z { get; set; }
        public double height { get; set; }
        public double assumed_height_above_highest_edge_m { get; set; }
        public string height_source { get; set; }
        public double height_above_ground_m { get; set; }
        public bool height_is_assumed { get; set; }
    }
    public class Road { public string id {get;set;} public List<List<double[]>> triangles {get;set;} }
    public class Dataset
    {
        public string name { get; set; }
        public string dataset_id { get; set; }
        public string units { get; set; }
        public bool is_survey_accurate { get; set; }
        public List<double[]> terrain { get; set; }
        public List<Building> buildings { get; set; }
        public Dictionary<string, object> report { get; set; }
        public int schema_version { get; set; }
        public List<Road> roads { get; set; }
    }
    public static class Builder
    {
        public static string Root { get { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Builder).Assembly.Location), "..")); } }
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16000000, RecursionLimit = 100 }; }
        public static Dataset Load()
        {
            string selected=Path.Combine(Bridge.RuntimeDir,"active-selection.json");
            if(!File.Exists(selected))throw new InvalidOperationException("請先按「選擇 CAD」，完成檢查並採用資料。");
            Dictionary<string,object> pick=Serializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(selected,Encoding.UTF8));
            string sid=Convert.ToString(pick["session_id"]);
            if(sid.Length!=32 || !sid.All(c=>"0123456789abcdef".Contains(c)))throw new InvalidDataException("Invalid selection.");
            Dataset d = Serializer().Deserialize<Dataset>(File.ReadAllText(Path.Combine(Bridge.RuntimeDir,"sessions",sid,"prepared.json"), Encoding.UTF8));
            if(d.dataset_id!=Convert.ToString(pick["dataset_id"]))throw new InvalidDataException("資料已變動，請重新檢查並採用。");
            if(d.schema_version!=3 || d.roads==null)throw new InvalidDataException("請用 v3 重新檢查並採用 CAD。");
            if(d.roads.Sum(r=>r.triangles.Count)>50000)throw new InvalidDataException("Too many road triangles.");
            if (d.units != "meters" || d.terrain == null || d.terrain.Count < 3 || d.terrain.Count > 15000 || d.buildings == null || d.buildings.Count > 3000)
                throw new InvalidDataException("Invalid dataset size or units.");
            if (String.IsNullOrEmpty(d.dataset_id) || d.dataset_id.Length > 64 || !d.dataset_id.All(Char.IsLetterOrDigit))
                throw new InvalidDataException("Invalid dataset ID.");
            foreach (double[] p in d.terrain)
                if (p.Length != 3 || p.Any(v => Double.IsNaN(v) || Double.IsInfinity(v) || Math.Abs(v) > 100000)) throw new InvalidDataException("Invalid terrain coordinate.");
            return d;
        }
        static XYZ Pt(double x, double y, double z) { return new XYZ(x / 0.3048, y / 0.3048, z / 0.3048); }
        static void Note(Element e, string text)
        {
            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly) p.Set(text);
        }
        public static object Build(UIApplication app, string expectedId)
        {
            Dataset data = Load();
            if(data.dataset_id!=expectedId)throw new InvalidOperationException("選擇的 CAD 已更換，請重新檢查後建立。");
            if (app.ActiveUIDocument == null) throw new InvalidOperationException("Open a Revit 2024 project first.");
            Document doc = app.ActiveUIDocument.Document;
            if (doc.IsFamilyDocument || doc.IsReadOnly) throw new InvalidOperationException("Use a writable project, not a family.");
            string markerName = "CADTerrainMCP_" + data.dataset_id;
            if (new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Any(e => e.Name == markerName))
                return new { status = "already_built", dataset_id = data.dataset_id, message = "This dataset already exists in this project. No duplicate created." };
            ToposolidType type = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().FirstOrDefault();
            if (type == null) throw new InvalidOperationException("No Toposolid type in project. Start with an Architectural template.");
            ViewFamilyType vf = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vf == null) throw new InvalidOperationException("Project has no 3D view family type.");
            List<long> created = new List<long>();
            List<string> failures = new List<string>();
            List<long> roadIds = new List<long>();
            List<string> roadFailures = new List<string>();
            long terrainId = 0;
            View3D view = null;
            using (Transaction tx = new Transaction(doc, "CAD 基地示意：地形與房屋"))
            {
                tx.Start();
                Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => Math.Abs(l.Elevation) < 1e-6);
                if (level == null) { level = Level.Create(doc, 0); level.Name = "CAD 示意基準 " + data.dataset_id.Substring(0,6); }
                List<XYZ> terrain = data.terrain.Select(p => Pt(p[0],p[1],p[2])).ToList();
                Toposolid topo = Toposolid.Create(doc, terrain, type.Id, level.Id);
                terrainId = topo.Id.Value;
                Note(topo, "CADTerrainMCP v3 " + data.dataset_id + " | "+data.name+" | "+Serializer().Serialize(data.report));
                foreach (Building b in data.buildings)
                {
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            if (b.points == null || b.points.Count < 3 || b.points.Count > 3000 || b.height <= 0 || b.height > 1000)
                                throw new InvalidDataException("Invalid building geometry.");
                            if (b.points.Any(p => p.Length != 2 || p.Any(v => Double.IsNaN(v) || Double.IsInfinity(v) || Math.Abs(v)>100000)))
                                throw new InvalidDataException("Invalid building coordinates.");
                            CurveLoop loop = new CurveLoop();
                            for (int i=0; i<b.points.Count; i++)
                            {
                                double[] a=b.points[i], c=b.points[(i+1)%b.points.Count];
                                XYZ p=Pt(a[0],a[1],b.base_z), q=Pt(c[0],c[1],b.base_z);
                                if (p.DistanceTo(q) <= app.Application.ShortCurveTolerance) throw new InvalidDataException("Edge below Revit short curve tolerance.");
                                loop.Append(Line.CreateBound(p,q));
                            }
                            Solid solid=GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop>{loop},XYZ.BasisZ,b.height/0.3048);
                            DirectShape ds=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                            ds.ApplicationId="CADTerrainMCP"; ds.ApplicationDataId=data.dataset_id+"/"+b.id;
                            ds.Name="房屋 "+b.id+"（"+b.height_source+"）";
                            ds.SetShape(new List<GeometryObject>{solid});
                            Note(ds,"CADTerrainMCP | 高度來源："+b.height_source+"；最高估計地面上方"+b.height_above_ground_m+"m；基座高度依地形估計，需檢查。" );
                            sub.Commit();created.Add(ds.Id.Value);
                        }
                        catch(Exception ex) { if(sub.GetStatus()==TransactionStatus.Started)sub.RollBack();failures.Add(b.id+": "+ex.Message); }
                    }
                }
                foreach(Road road in data.roads)
                {
                    using(SubTransaction sub=new SubTransaction(doc))
                    {
                        sub.Start();
                        try{
                            TessellatedShapeBuilder mesh=new TessellatedShapeBuilder();mesh.OpenConnectedFaceSet(false);
                            foreach(List<double[]> face in road.triangles){
                                if(face.Count!=3 || face.Any(p=>p.Length!=3 || p.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)||Math.Abs(v)>100000)))throw new InvalidDataException("Invalid road face.");
                                mesh.AddFace(new TessellatedFace(face.Select(p=>Pt(p[0],p[1],p[2])).ToList(),ElementId.InvalidElementId));
                            }
                            mesh.CloseConnectedFaceSet();mesh.Target=TessellatedShapeBuilderTarget.Mesh;mesh.Fallback=TessellatedShapeBuilderFallback.Salvage;mesh.Build();
                            IList<GeometryObject> geometry=mesh.GetBuildResult().GetGeometricalObjects();
                            if(geometry.Count==0)throw new InvalidDataException("Empty road geometry.");
                            DirectShape ds=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                            ds.ApplicationId="CADTerrainMCP";ds.ApplicationDataId=data.dataset_id+"/"+road.id;ds.Name="道路示意面 "+road.id;ds.SetShape(geometry);
                            Note(ds,"依選定CAD道路資料與預處理地形建立之網格面；抬高5公分，無路基厚度；請檢查邊界與地形貼合。");
                            sub.Commit();roadIds.Add(ds.Id.Value);
                        }catch(Exception ex){if(sub.GetStatus()==TransactionStatus.Started)sub.RollBack();roadFailures.Add(road.id+": "+ex.Message);}
                    }
                }
                view=View3D.CreateIsometric(doc,vf.Id);
                view.Name="CAD 山坡基地｜示意 "+data.dataset_id.Substring(0,6);
                view.DetailLevel=ViewDetailLevel.Fine;view.DisplayStyle=DisplayStyle.ShadingWithEdges;
                FillPatternElement fill=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f=>f.GetFillPattern().IsSolidFill);
                if(fill!=null)
                {
                    OverrideGraphicSettings ground=new OverrideGraphicSettings();ground.SetSurfaceForegroundPatternId(fill.Id);ground.SetSurfaceForegroundPatternColor(new Color(145,167,120));
                    view.SetElementOverrides(topo.Id,ground);
                    OverrideGraphicSettings house=new OverrideGraphicSettings();house.SetSurfaceForegroundPatternId(fill.Id);house.SetSurfaceForegroundPatternColor(new Color(225,213,194));
                    foreach(long id in created)view.SetElementOverrides(new ElementId(id),house);
                    OverrideGraphicSettings roadStyle=new OverrideGraphicSettings();roadStyle.SetSurfaceForegroundPatternId(fill.Id);roadStyle.SetSurfaceForegroundPatternColor(new Color(80,84,88));
                    foreach(long id in roadIds)view.SetElementOverrides(new ElementId(id),roadStyle);
                }
                // Marker is committed atomically with the geometry. A second build cannot silently duplicate it.
                DataStorage marker=DataStorage.Create(doc);marker.Name=markerName;
                if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit did not commit the model transaction.");
            }
            string displayWarning = null;
            try
            {
                app.ActiveUIDocument.ActiveView=view;
                UIView ui=app.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(v=>v.ViewId==view.Id);
                if(ui!=null)ui.ZoomToFit();
            }
            catch(Exception ex){ displayWarning=ex.Message; }
            object result=new {status=failures.Count==0 && roadFailures.Count==0?"built":"built_with_skips",dataset_id=data.dataset_id,terrain_id=terrainId,
                roads_created=roadIds.Count,road_ids=roadIds,road_failures=roadFailures,
                buildings_created=created.Count,building_ids=created,building_failures=failures,view_id=view.Id.Value,
                view_warning=displayWarning, saved=false, notice="示意地形與假設樓高；請自行另存 .rvt。失敗房屋已略過，請查看 building_failures。"};
            try{File.WriteAllText(Path.Combine(Bridge.RuntimeDir,"last-build.json"),Serializer().Serialize(result),Encoding.UTF8);}catch{}
            return result;
        }
    }
    public class Job
    {
        public string Id; public string ExpectedId; public DateTime Expires; public string Result; public int State;
        public ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }
    public class Handler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Job job=Bridge.Pending;if(job==null)return;
            if(Interlocked.CompareExchange(ref job.State,1,0)!=0)return;
            try
            {
                if(DateTime.UtcNow>job.Expires)throw new TimeoutException("Request expired before Revit processed it. Nothing created.");
                job.Result=Builder.Serializer().Serialize(Builder.Build(app,job.ExpectedId));
            }
            catch(Exception ex){job.Result=Builder.Serializer().Serialize(new {status="error",error=ex.Message});}
            finally{Interlocked.Exchange(ref job.State,2);job.Done.Set();}
        }
        public string GetName(){return "CAD Terrain MCP build handler";}
    }
    public static class Bridge
    {
        public static readonly string RuntimeDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CADTerrainMCP");
        public static volatile Job Pending;
        static TcpListener listener;static volatile bool running;static ExternalEvent ev;static string token;
        public static void Start(ExternalEvent externalEvent)
        {
            Directory.CreateDirectory(RuntimeDir);ev=externalEvent;
            listener=new TcpListener(IPAddress.Loopback,18765);listener.Start();
            token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(RuntimeDir,"bridge-token.txt"),token,Encoding.ASCII);
            running=true;Task.Run((Action)Loop);
        }
        public static void Stop(){running=false;if(listener!=null)listener.Stop();}
        static void Loop()
        {
            while(running)
            {
                try
                {
                    using(TcpClient client=listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout=5000;client.SendTimeout=5000;
                        using(NetworkStream stream=client.GetStream())
                        using(StreamReader reader=new StreamReader(stream,new UTF8Encoding(false)))
                        using(StreamWriter writer=new StreamWriter(stream,new UTF8Encoding(false)){AutoFlush=true})
                        {
                            // Bound request size; no HTTP or remote addresses, and no arbitrary file/code endpoint.
                            StringBuilder sb=new StringBuilder();int c;
                            while((c=reader.Read())!=-1 && c!='\n'){if(sb.Length>=16384)throw new InvalidDataException("Request too large");sb.Append((char)c);}
                            Dictionary<string,object> request=Builder.Serializer().Deserialize<Dictionary<string,object>>(sb.ToString());
                            if(!request.ContainsKey("token") || !String.Equals(Convert.ToString(request["token"]),token,StringComparison.Ordinal))
                            {writer.WriteLine("{\"status\":\"error\",\"error\":\"Unauthorized\"}");continue;}
                            string action=request.ContainsKey("action")?Convert.ToString(request["action"]):"";
                            if(action=="inspect") {try{Dataset d=Builder.Load();writer.WriteLine(Builder.Serializer().Serialize(new {status="ready",dataset_id=d.dataset_id,dataset=d.report,revit_version="2024",kit_version="3.0"}));}catch(Exception ex){writer.WriteLine(Builder.Serializer().Serialize(new {status="error",error=ex.Message,kit_version="3.0"}));}continue;}
                            if(action=="status") {Job j=Pending;writer.WriteLine(j==null?"{\"status\":\"idle\"}":j.Done.IsSet?j.Result:Builder.Serializer().Serialize(new {status="pending",request_id=j.Id}));continue;}
                            if(action!="build" || !request.ContainsKey("accept_inferred") || !(request["accept_inferred"] is bool) || !(bool)request["accept_inferred"])
                            {writer.WriteLine("{\"status\":\"error\",\"error\":\"build requires accept_inferred=true\"}");continue;}
                            Job previous=Pending;
                            if(!request.ContainsKey("expected_dataset_id") || String.IsNullOrEmpty(Convert.ToString(request["expected_dataset_id"])))
                            {writer.WriteLine("{\"status\":\"error\",\"error\":\"Inspect first; expected_dataset_id is required\"}");continue;}
                            if(previous!=null && !previous.Done.IsSet){writer.WriteLine("{\"status\":\"busy\",\"error\":\"Use status; do not retry build\"}");continue;}
                            Job job=new Job{Id=Guid.NewGuid().ToString("N"),ExpectedId=Convert.ToString(request["expected_dataset_id"]),Expires=DateTime.UtcNow.AddSeconds(110)};Pending=job;
                            ExternalEventRequest raised=ev.Raise();
                            if(raised!=ExternalEventRequest.Accepted)
                            {
                                Interlocked.Exchange(ref job.State,3);job.Result="{\"status\":\"error\",\"error\":\"Revit external event not accepted\"}";job.Done.Set();writer.WriteLine(job.Result);continue;
                            }
                            if(job.Done.Wait(120000))writer.WriteLine(job.Result);
                            else
                            {
                                if(Interlocked.CompareExchange(ref job.State,3,0)==0)
                                {job.Result="{\"status\":\"expired\",\"error\":\"Revit was busy; request canceled before execution\"}";job.Done.Set();writer.WriteLine(job.Result);}
                                else writer.WriteLine(Builder.Serializer().Serialize(new {status="running",request_id=job.Id,message="Creation is executing. Query status; do not repeat build."}));
                            }
                        }
                    }
                }
                catch(Exception){if(!running)return;}
            }
        }
    }
    public class App : IExternalApplication
    {
        static void AddButton(RibbonPanel panel,string id,string label,string assembly,string command,string icon,string tip){
            var button=new PushButtonData(id,label,assembly,command);
            button.Image=RibbonIcons.Create(icon);button.LargeImage=RibbonIcons.Create(icon);button.ToolTip=tip;
            panel.AddItem(button);
        }
        ExternalEvent ev;
        public Result OnStartup(UIControlledApplication app)
        {
            string assembly=typeof(App).Assembly.Location;
            RibbonPanel panel=app.CreateRibbonPanel("CAD 基地 MCP v3");
            AddButton(panel,"CadTerrainChoose","選擇 CAD",assembly,"CadTerrainKit.ChooseCommand","choose","選擇 DWG / DXF，設定單位、圖層、房屋高度與道路。");
            AddButton(panel,"CadTerrainInspect","資料檢查",assembly,"CadTerrainKit.InspectCommand","inspect","查看目前採用的 CAD、地形點、房屋與道路資料限制。");
            AddButton(panel,"CadTerrainBuild","建立基地",assembly,"CadTerrainKit.BuildCommand","build","在目前 Revit 專案建立地形、房屋量體與選定道路。");
            ev=ExternalEvent.Create(new Handler());
            try{Bridge.Start(ev);}catch(Exception ex){TaskDialog.Show("CAD 基地 MCP","本機 MCP 連線未啟動，但仍可使用建模按鈕。\n"+ex.Message);}
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app){Bridge.Stop();if(ev!=null)ev.Dispose();return Result.Succeeded;}
    }
    [Transaction(TransactionMode.ReadOnly)]
    public class InspectCommand:IExternalCommand
    {
        public Result Execute(ExternalCommandData cmd,ref string message,ElementSet elements)
        {
            try {Dataset d=Builder.Load();TaskDialog.Show("CAD 基地資料檢查",Summary(d));return Result.Succeeded;}
            catch(Exception ex){message=ex.Message;return Result.Failed;}
        }
        public static string Summary(Dataset d){
            string summary="資料："+d.name+"\n資料編號："+d.dataset_id+"\n地形點："+d.terrain.Count+"\n候選房屋量體："+d.buildings.Count+"\n道路面區塊："+d.roads.Count;
            if(d.report!=null){
                foreach(string key in new[]{"drawing_unit_m","width_m","depth_m","assumed_building_height_m"})
                    if(d.report.ContainsKey(key))summary+="\n"+new Dictionary<string,string>{{"drawing_unit_m","1 圖面單位＝公尺"},{"width_m","寬度（公尺）"},{"depth_m","深度（公尺）"},{"assumed_building_height_m","假設屋高（公尺）"}}[key]+"："+Convert.ToString(d.report[key]);
                if(d.report.ContainsKey("limitations")){
                    var items=d.report["limitations"] as System.Collections.IEnumerable;
                    if(items!=null)foreach(object item in items)summary+="\n\n"+Convert.ToString(item);
                }
            }
            return summary;
        }
    }
    [Transaction(TransactionMode.Manual)]
    public class BuildCommand:IExternalCommand
    {
        public Result Execute(ExternalCommandData cmd,ref string message,ElementSet elements)
        {
            Dataset selected;
            try{selected=Builder.Load();}catch(Exception ex){TaskDialog.Show("請先選擇 CAD",ex.Message);return Result.Cancelled;}
            TaskDialog confirm=new TaskDialog("建立示意基地");
            confirm.MainInstruction="在目前專案新增地形與候選房屋量體";
            confirm.MainContent=InspectCommand.Summary(selected)+"\n\n在目前專案新增，建議先開啟新的建築專案。完成後請另存 .rvt。是否建立？";
            confirm.CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No;confirm.DefaultButton=TaskDialogResult.No;
            if(confirm.Show()!=TaskDialogResult.Yes)return Result.Cancelled;
            try{object result=Builder.Build(cmd.Application,selected.dataset_id);TaskDialog.Show("CAD 基地建立結果",Builder.Serializer().Serialize(result));return Result.Succeeded;}
            catch(Exception ex){message=ex.Message;return Result.Failed;}
        }
    }
    [Transaction(TransactionMode.ReadOnly)]
    public class ChooseCommand:IExternalCommand
    {
        public Result Execute(ExternalCommandData cmd,ref string message,ElementSet elements)
        {
            try {
                string pythonFile=Path.Combine(Builder.Root,"python-path.txt");
                string python=File.Exists(pythonFile)?File.ReadAllText(pythonFile,Encoding.UTF8).Trim():Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe");
                if(!File.Exists(python))throw new FileNotFoundException("找不到 Python，請執行 INSTALL.ps1 設定。");
                var start=new System.Diagnostics.ProcessStartInfo(python,"\""+Path.Combine(Builder.Root,"src","choose_cad.py")+"\"");
                start.UseShellExecute=false;start.CreateNoWindow=true;start.WorkingDirectory=Builder.Root;
                System.Diagnostics.Process.Start(start);return Result.Succeeded;
            }catch(Exception ex){message=ex.Message;return Result.Failed;}
        }
    }
}

