using System.Text.Json;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Interactive;
using EVCanopyDesigner.Interactive.Integration;

int passed=0,failed=0;var results=new List<object>();
void Check(bool ok,string detail="Assertion failed"){if(!ok)throw new Exception(detail);}
void Test(string name,Action action){try{action();passed++;results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}catch(Exception e){failed++;results.Add(new{Name=name,Status="FAIL",Error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Reject(Action action){try{action();}catch(Exception e) when(e is ArgumentException or InvalidOperationException or NotSupportedException){return;}throw new Exception("Expected guarded rejection");}
GenerationRequest Request(){var vm=new InteractiveDesignerViewModel();vm.Calculate();return new(vm.Design!,null,0,Guid.NewGuid().ToString("D"));}
CanopyReview Prepare(CanopyIntegrationSession session,GenerationRequest? r=null)=>session.Prepare(IntegrationScope.CanopyOnly,r??Request(),new(),true);

Test("Read-only delegate adapter is inert until explicit validation and propagates failure",()=>{
 var fake=new FakeHost();var adapter=new ReadOnlyCanopyHostAdapter(fake.CaptureContext,fake.Preflight);Check(fake.Calls==0&&fake.ContextCalls==0);var s=new CanopyIntegrationSession(adapter);var review=Prepare(s);Check(fake.Calls==0&&fake.ContextCalls==0);s.Validate(review);Check(fake.Calls==1);
 var broken=new ReadOnlyCanopyHostAdapter(fake.CaptureContext,_=>throw new InvalidOperationException("host guard refused"));var failSession=new CanopyIntegrationSession(broken);Reject(()=>failSession.Validate(Prepare(failSession)));
});
Test("Canopy-only preparation preserves five stations and all 21 blocking findings",()=>{
 var host=new FakeHost();var session=new CanopyIntegrationSession(host);var request=Request();var review=Prepare(session,request);
 Check(review.Scope==IntegrationScope.CanopyOnly&&!review.SiteLayoutApproved&&review.Equipment.PhysicalStations==5&&review.Equipment.AssignedPoints==10&&review.Equipment.BlockingConflicts==21);
 var e=EquipmentPlanner.Calculate(request.Design,new());Check(review.Equipment.Findings.SequenceEqual(e.Issues));Check(host.Calls==0&&host.ContextCalls==0);
 Check(review.CanopyConfigurationJson==ConfigurationCodec.Export(ConfigurationCodec.Create(request.Design.SitePlan.Inputs,request.Design.Inputs,0)));
});
Test("Equipment and combined-site requests never fall back or reach the host",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);
 foreach(var scope in new[]{IntegrationScope.ChargingEquipment,IntegrationScope.CombinedSite,(IntegrationScope)99})Reject(()=>s.Prepare(scope,Request(),new(),true));
 Check(host.Calls==0&&host.ContextCalls==0);
});
Test("Canopy scope requires acknowledgement and fresh placement confirmation",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);Reject(()=>s.Prepare(IntegrationScope.CanopyOnly,Request(),new(),false));var r=Prepare(s);var ticket=s.Validate(r);Reject(()=>s.ConfirmForPlacement(r,ticket,false));Check(host.Calls==1&&!ticket.Consumed);
});
Test("Scoped handoff contains original canopy ledger, never equipment or site approval",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var request=Request();var r=Prepare(s,request);var ticket=s.Validate(r);var handoff=s.ConfirmForPlacement(r,ticket,true);
 Check(host.Calls==2&&ticket.Consumed&&!handoff.SiteLayoutApproved&&!handoff.IncludesChargingEquipment&&handoff.Scope==IntegrationScope.CanopyOnly);
 Check(handoff.Equipment.BlockingConflicts==21&&handoff.LegacyRequest.Design.Fingerprint==request.Design.Fingerprint&&handoff.LegacyRequest.Design.Components.Count==request.Design.Components.Count);
 Check(handoff.LegacyRequest.Design.Schedule.SequenceEqual(request.Design.Schedule));Check(handoff.ExclusionNotice.Contains("No charging stations"));Reject(()=>s.ConfirmForPlacement(r,ticket,true));
});
Test("Missing, failed, duplicated or unverified safety gates fail closed",()=>{
 foreach(string name in BaselinePreflightAdapter.RequiredChecks){
  var good=FakeHost.GoodReport();
  Reject(()=>BaselinePreflightAdapter.RequireSafetyPasses(good with{Checks=good.Checks.Where(c=>c.Name!=name).ToArray()}));
  Reject(()=>BaselinePreflightAdapter.RequireSafetyPasses(good with{Checks=good.Checks.Select(c=>c.Name==name?c with{Status=CheckStatus.NotVerified}:c).ToArray()}));
  Reject(()=>BaselinePreflightAdapter.RequireSafetyPasses(good with{Checks=good.Checks.Concat(new[]{new ValidationCheck(name,CheckStatus.Pass,"duplicate")}).ToArray()}));
 }
 var host=new FakeHost{Report=new("empty",Array.Empty<ValidationCheck>())};var s=new CanopyIntegrationSession(host);var r=Prepare(s);Reject(()=>s.Validate(r));
 host.Report=FakeHost.GoodReport() with{Checks=FakeHost.GoodReport().Checks.Concat(new[]{new ValidationCheck("Additional guard",CheckStatus.Fail,"blocked")}).ToArray()};Reject(()=>s.Validate(r));
});
Test("Transaction and engineering not-verified findings are retained, not labeled completed",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var ticket=s.Validate(r);Check(ticket.Report.Checks.Any(c=>c.Status==CheckStatus.NotVerified));var handoff=s.ConfirmForPlacement(r,ticket,true);Check(handoff.FinalPreflight.Checks.Any(c=>c.Name=="Actual Revit transaction/reconciliation"&&c.Status==CheckStatus.NotVerified));
});
Test("A changed document, level or command session invalidates the final handoff",()=>{
 foreach(int change in new[]{0,1,2,3,4}){var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var ticket=s.Validate(r);host.Context=change switch{0=>host.Context with{DocumentKey="other"},1=>host.Context with{LevelUniqueId="other"},2=>host.Context with{SessionId=Guid.NewGuid()},3=>host.Context with{DocumentPath=@"C:\OfflineTests\Other.rvt"},_=>host.Context with{LevelElevationFeet=10}};Reject(()=>s.ConfirmForPlacement(r,ticket,true));Check(ticket.Consumed);}
});
Test("Context change during initial or final preflight is rejected",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);host.OnPreflight=()=>host.Context=host.Context with{LevelUniqueId="changed"};Reject(()=>s.Validate(r));
 host.OnPreflight=null;var ticket=s.Validate(r);host.OnPreflight=()=>host.Context=host.Context with{DocumentKey="changed"};Reject(()=>s.ConfirmForPlacement(r,ticket,true));Check(ticket.Consumed);
});
Test("Unverified runtime or incomplete host identity cannot produce a ticket",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var good=host.Context;
 foreach(var bad in new[]{good with{RuntimeCompatibility=CheckStatus.NotVerified},good with{RuntimeEvidence=""},good with{SessionId=Guid.Empty},good with{DocumentKey=""},good with{DocumentPath="relative.rvt"},good with{LevelUniqueId=""},good with{LevelElevationFeet=double.NaN}}){host.Context=bad;Reject(()=>s.Validate(r));}Check(host.Calls==0);
});
Test("Parameter, rotation, equipment and identity edits supersede old review tickets",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var input=Request();var first=Prepare(s,input);var ticket=s.Validate(first);
 var rotated=Prepare(s,input with{RotationDegrees=360});Check(first.ReviewKey!=rotated.ReviewKey);Reject(()=>s.ConfirmForPlacement(first,ticket,true));Reject(()=>s.Validate(first));
 var otherEquipment=s.Prepare(IntegrationScope.CanopyOnly,input,new(){CableLength=1},true);Check(otherEquipment.ReviewKey!=first.ReviewKey&&otherEquipment.Equipment.BlockingConflicts>21);
 var otherIdentity=Prepare(s,input with{ProposedDesignId=Guid.NewGuid().ToString("D")});Check(otherIdentity.ReviewKey!=first.ReviewKey);
 var vm=new InteractiveDesignerViewModel();vm.Settings.Single(f=>f.Key=="RoofDatum").Value="5.8";vm.Calculate();var changed=Prepare(s,input with{Design=vm.Design!});Check(changed.ReviewKey!=first.ReviewKey);
});
Test("Explicit invalidation, cancellation and scope switching revoke old tickets",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var t=s.Validate(r);s.Invalidate();Reject(()=>s.ConfirmForPlacement(r,t,true));
 r=Prepare(s);t=s.Validate(r);s.Cancel(t);Reject(()=>s.ConfirmForPlacement(r,t,true));
 r=Prepare(s);t=s.Validate(r);Reject(()=>s.Prepare(IntegrationScope.CombinedSite,Request(),new(),true));Reject(()=>s.ConfirmForPlacement(r,t,true));
});
Test("New failed validation revokes an earlier successful ticket",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var t=s.Validate(r);host.Report=new("empty",Array.Empty<ValidationCheck>());Reject(()=>s.Validate(r));host.Report=FakeHost.GoodReport();Reject(()=>s.ConfirmForPlacement(r,t,true));
});
Test("Expired and foreign-session tickets are refused",()=>{
 var now=DateTimeOffset.UtcNow;var host=new FakeHost();var s=new CanopyIntegrationSession(host,()=>now);var r=Prepare(s);var t=s.Validate(r);now=now.AddMinutes(2);Reject(()=>s.ConfirmForPlacement(r,t,true));
 var other=new CanopyIntegrationSession(host);Reject(()=>other.ConfirmForPlacement(r,t,true));Reject(()=>other.Cancel(t));
});
Test("Replacement preserves selected identity; invalid GUIDs or identity changes are refused",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var id=Guid.NewGuid().ToString("D");var request=Request() with{ReplaceDesignId=id,ProposedDesignId=id,RotationDegrees=25};var r=Prepare(s,request);var h=s.ConfirmForPlacement(r,s.Validate(r),true);Check(h.LegacyRequest.ReplaceDesignId==id&&h.LegacyRequest.ProposedDesignId==id&&h.LegacyRequest.RotationDegrees==25);
 Reject(()=>Prepare(s,request with{ProposedDesignId=Guid.NewGuid().ToString("D")}));Reject(()=>Prepare(s,request with{ReplaceDesignId="invalid"}));Reject(()=>Prepare(s,request with{ReplaceDesignId=null,ProposedDesignId=Guid.Empty.ToString()}));Reject(()=>Prepare(s,request with{RotationDegrees=double.NaN}));
});
Test("Mutated component ledger or site geometry is rejected despite unchanged fingerprint",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var request=Request();var d=request.Design;
 Reject(()=>Prepare(s,request with{Design=d with{Components=d.Components.Skip(1).ToArray()}}));
 Reject(()=>Prepare(s,request with{Design=d with{SitePlan=d.SitePlan with{Spaces=d.SitePlan.Spaces.Skip(1).ToArray()}}}));Check(host.Calls==0);
});
Test("Unavailable equipment stays not assessed; no zero-conflict site approval is inferred",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=s.Prepare(IntegrationScope.CanopyOnly,Request(),null,true);Check(r.Equipment.BlockingConflicts is null&&r.Equipment.State.StartsWith("NOT ASSESSED"));var h=s.ConfirmForPlacement(r,s.Validate(r),true);Check(!h.SiteLayoutApproved&&!h.IncludesChargingEquipment);
});
Test("Final guard failure burns ticket and emits no handoff",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var t=s.Validate(r);host.Report=new("blocked",new[]{new ValidationCheck("Guard",CheckStatus.Fail,"changed")});Reject(()=>s.ConfirmForPlacement(r,t,true));Check(t.Consumed);host.Report=FakeHost.GoodReport();Reject(()=>s.ConfirmForPlacement(r,t,true));
});
Test("An edit reentered during initial or final preflight revokes the review",()=>{
 var host=new FakeHost();var session=new CanopyIntegrationSession(host);var review=Prepare(session);
 host.OnPreflight=()=>session.Invalidate();Reject(()=>session.Validate(review));
 host.OnPreflight=null;review=Prepare(session);var ticket=session.Validate(review);host.OnPreflight=()=>session.Invalidate();Reject(()=>session.ConfirmForPlacement(review,ticket,true));Check(ticket.Consumed);
});
Test("Report snapshots and equipment findings are immutable; no Revit dependency",()=>{
 var host=new FakeHost();var s=new CanopyIntegrationSession(host);var r=Prepare(s);var t=s.Validate(r);Check(((IList<ValidationCheck>)t.Report.Checks).IsReadOnly&&((IList<EquipmentIssue>)r.Equipment.Findings).IsReadOnly);Check(typeof(CanopyIntegrationSession).Assembly.GetReferencedAssemblies().All(a=>!a.Name!.Contains("Revit",StringComparison.OrdinalIgnoreCase)&&!a.Name.Contains("MCP",StringComparison.OrdinalIgnoreCase)));
});

Test("Unique run directory satisfies unchanged legacy basename guard without adopting existing model",()=>{
 string root=@"C:\PocWorkspace\Revit_Test_Models\InteractiveDesignerPOC";
 string runA=Path.Combine(root,Guid.NewGuid().ToString("N"),DocumentPolicy.FileName),runB=Path.Combine(root,Guid.NewGuid().ToString("N"),DocumentPolicy.FileName);
 Check(runA!=runB);DocumentPolicy.Validate(runA,new(runA));Reject(()=>DocumentPolicy.Validate(runA,new(runB)));
 Reject(()=>DocumentPolicy.Validate(Path.Combine(root,"unique-but-incompatible-name.rvt"),new(Path.Combine(root,"unique-but-incompatible-name.rvt"))));
});
Test("New model marker preserves distinct immutable snapshot requirement",()=>{
 string path=Path.Combine(@"C:\PocWorkspace\Revit_Test_Models",Guid.NewGuid().ToString("N"),DocumentPolicy.FileName);
 var marker=new DisposableMarker(1,path,Path.Combine(Path.GetDirectoryName(path)!,"initial-immutable-snapshot.rvt"),"recorded-actual-snapshot-hash",DateTime.UtcNow.ToString("O"));
 DisposablePolicy.Validate(path,marker);Reject(()=>DisposablePolicy.Validate(path,marker with{SeedPath=path}));Reject(()=>DisposablePolicy.Validate(path,marker with{ApprovedPath=path+".other"}));
});
Console.WriteLine($"{passed} passed; {failed} failed. Offline contracts only; fake host, no Revit/model/deployment operations.");
if(args.Length==1){if(File.Exists(args[0]))throw new InvalidOperationException("Refusing to overwrite evidence.");File.WriteAllText(args[0],JsonSerializer.Serialize(new{Status=failed==0?"PASS":"FAIL",Passed=passed,Failed=failed,Scope="Offline integration contracts and fake read-only host only; no Revit API/runtime acceptance",Results=results},new JsonSerializerOptions{WriteIndented=true}));}
return failed==0?0:1;

sealed class FakeHost : IReadOnlyCanopyHost
{
 public int Calls,ContextCalls;
 public HostContext Context=new(Guid.NewGuid(),"offline-document",@"C:\OfflineTests\Disposable.rvt","offline-level",0,CheckStatus.Pass,"Fake host only, NOT evidence of real Revit compatibility");
 public ValidationReport Report=GoodReport();
 public Action? OnPreflight;
 public HostContext CaptureContext(){ContextCalls++;return Context;}
 public ValidationReport Preflight(GenerationRequest request){Calls++;OnPreflight?.Invoke();return Report;}
 public static ValidationReport GoodReport()=>new("Fake canopy preflight",new[]{"Approved disposable document","Frozen component ledger and clearance","Design registry identity","Selected replacement ownership","Unique proposed Marks"}.Select(n=>new ValidationCheck(n,CheckStatus.Pass,"Mock only")).Concat(new[]{new ValidationCheck("Actual Revit transaction/reconciliation",CheckStatus.NotVerified,"No transaction in preflight")}).Concat(ValidationReport.Boundaries()).ToArray());
}
