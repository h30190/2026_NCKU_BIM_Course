using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVCanopyDesigner.Canopy.Core;
using EVCanopyDesigner.Planner.Core;
using EVCanopyDesigner.Interactive;

namespace EVCanopyDesigner.Interactive.Integration;

public enum IntegrationScope { CanopyOnly, ChargingEquipment, CombinedSite }

// A future Revit implementation must capture these from the command's actual document/level.
// No document is opened or trusted solely because its path appears here.
public sealed record HostContext(Guid SessionId,string DocumentKey,string DocumentPath,string LevelUniqueId,double LevelElevationFeet,
 CheckStatus RuntimeCompatibility=CheckStatus.NotVerified,string RuntimeEvidence="");

public interface IReadOnlyCanopyHost
{
 HostContext CaptureContext();
 ValidationReport Preflight(GenerationRequest canopyRequest);
}

// Delegate seam for a future command to bind its existing read-only DesignValidation.Preflight.
// Construction does not call Revit or invoke either delegate.
public sealed class ReadOnlyCanopyHostAdapter : IReadOnlyCanopyHost
{
 private readonly Func<HostContext> context;
 private readonly Func<GenerationRequest,ValidationReport> preflight;
 public ReadOnlyCanopyHostAdapter(Func<HostContext> context,Func<GenerationRequest,ValidationReport> preflight)
 {this.context=context??throw new ArgumentNullException(nameof(context));this.preflight=preflight??throw new ArgumentNullException(nameof(preflight));}
 public HostContext CaptureContext()=>context();
 public ValidationReport Preflight(GenerationRequest canopyRequest)=>preflight(canopyRequest);
}

public sealed class EquipmentReviewSnapshot
{
 public string State {get;}
 public string? Fingerprint {get;}
 public int? PhysicalStations {get;}
 public int? AssignedPoints {get;}
 public int? BlockingConflicts {get;}
 public IReadOnlyList<EquipmentIssue> Findings {get;}
 internal EquipmentReviewSnapshot(CanopyDesign canopy,EquipmentOptions? options)
 {
  if(options is null){State="NOT ASSESSED - equipment is excluded, not approved";Findings=Array.Empty<EquipmentIssue>();return;}
  // Recompute: never accept a caller-supplied Passed flag or filtered list of conflicts.
  var layout=EquipmentPlanner.Calculate(canopy,options);
  State=layout.Passed?"CLEAR UNDER OFFLINE ASSUMPTIONS ONLY":"CONFLICT - equipment is excluded, not approved";
  Fingerprint=layout.Fingerprint;PhysicalStations=layout.PhysicalStations;AssignedPoints=layout.AssignedPoints;
  BlockingConflicts=layout.Issues.Count(i=>i.Blocking);Findings=Array.AsReadOnly(layout.Issues.ToArray());
 }
}

public sealed class CanopyReview
{
 public const string ExclusionNotice="CANOPY ONLY: parking markings, canopy structure, roof, gutters and PV from the existing canopy ledger. No charging stations, outputs, cables or equipment maintenance zones will be generated. Equipment conflicts remain unresolved. This is not approval of a complete site layout.";
 public IntegrationScope Scope=>IntegrationScope.CanopyOnly;
 public bool SiteLayoutApproved=>false;
 public string ReviewKey {get;}
 public string CanopyConfigurationJson {get;}
 public EquipmentReviewSnapshot Equipment {get;}
 internal GenerationRequest Request {get;}
 internal CanopyReview(GenerationRequest request,EquipmentOptions? equipment)
 {
  Request=request;Equipment=new(request.Design,equipment);
  CanopyConfigurationJson=ConfigurationCodec.Export(ConfigurationCodec.Create(request.Design.SitePlan.Inputs,request.Design.Inputs,request.RotationDegrees));
  ReviewKey=Digest(JsonSerializer.Serialize(new{Version=1,Scope,request.ReplaceDesignId,request.ProposedDesignId,request.RotationDegrees,CanopyConfigurationJson,Ledger=Digest(JsonSerializer.Serialize(request.Design)),Equipment.Fingerprint,Equipment.State,ExclusionNotice}));
 }
 internal static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed class CanopyPreflightTicket
{
 public string ReviewKey=>Review.ReviewKey;
 public HostContext Context {get;}
 public ValidationReport Report {get;}
 public DateTimeOffset ExpiresUtc {get;}
 public bool Consumed {get;internal set;}
 internal CanopyIntegrationSession Owner {get;}
 internal CanopyReview Review {get;}
 internal long Epoch {get;}
 internal CanopyPreflightTicket(CanopyIntegrationSession owner,CanopyReview review,HostContext context,ValidationReport report,DateTimeOffset expiry,long epoch)
 {Epoch=epoch;Owner=owner;Review=review;Context=context;Report=new(report.Scope,Array.AsReadOnly(report.Checks.ToArray()));ExpiresUtc=expiry;}
}

// This is a scoped payload, NOT an executor and NOT a site-layout approval.
public sealed record PendingCanopyReview(CanopyReview Review,CanopyPreflightTicket Ticket,bool ExplicitCanopyOnlyConfirmation);

public sealed class CanopyOnlyHandoff
{
 public IntegrationScope Scope=>IntegrationScope.CanopyOnly;
 public bool IncludesChargingEquipment=>false;
 public bool SiteLayoutApproved=>false;
 public GenerationRequest LegacyRequest {get;}
 public string ExclusionNotice=>CanopyReview.ExclusionNotice;
 public EquipmentReviewSnapshot Equipment {get;}
 public HostContext Context {get;}
 public ValidationReport FinalPreflight {get;}
 internal CanopyOnlyHandoff(CanopyReview review,HostContext context,ValidationReport report)
 {LegacyRequest=review.Request;Equipment=review.Equipment;Context=context;FinalPreflight=new(report.Scope,Array.AsReadOnly(report.Checks.ToArray()));}
}

public static class BaselinePreflightAdapter
{
 // Exact names are the existing DesignValidation.Preflight contract. A future host must map
 // an intentional contract revision explicitly; missing or duplicated gates fail closed.
 public static IReadOnlyList<string> RequiredChecks {get;}=Array.AsReadOnly(new[]{
  "Approved disposable document","Frozen component ledger and clearance","Design registry identity",
  "Selected replacement ownership","Unique proposed Marks"});
 public static void RequireSafetyPasses(ValidationReport report)
 {
  ArgumentNullException.ThrowIfNull(report);
  if(report.Checks is null||report.Checks.Any(c=>c.Status==CheckStatus.Fail))throw new InvalidOperationException("Canopy preflight failed; no handoff is permitted.");
  foreach(string name in RequiredChecks){var gates=report.Checks.Where(c=>c.Name==name).ToArray();if(gates.Length!=1||gates[0].Status!=CheckStatus.Pass)throw new InvalidOperationException("Required canopy safety gate must pass exactly once: "+name);}
  // Transaction/reconciliation and engineering boundaries may remain NotVerified here.
  // The unchanged GenerationService must still verify/commit or roll back them in Revit.
 }
}

public sealed class CanopyIntegrationSession
{
 private readonly IReadOnlyCanopyHost host;
 private readonly Func<DateTimeOffset> utcNow;
 private CanopyReview? activeReview;
 private long epoch;
 // Future UI must call this on every parameter, import, identity, mode or rotation edit.
 public void Invalidate(){epoch++;activeReview=null;}
 public CanopyIntegrationSession(IReadOnlyCanopyHost host,Func<DateTimeOffset>? utcNow=null)
 {this.host=host??throw new ArgumentNullException(nameof(host));this.utcNow=utcNow??(()=>DateTimeOffset.UtcNow);}

 public CanopyReview Prepare(IntegrationScope scope,GenerationRequest request,EquipmentOptions? equipment,bool acknowledgeEquipmentExclusion)
 {
  Invalidate();
  if(scope!=IntegrationScope.CanopyOnly)throw new NotSupportedException("Charging-equipment and combined-site generation are unavailable. No fallback to canopy generation is permitted.");
  if(!acknowledgeEquipmentExclusion)throw new InvalidOperationException("Explicit acknowledgement of canopy-only scope and unresolved equipment is required.");
  ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(request.Design);
  if(!double.IsFinite(request.RotationDegrees))throw new ArgumentException("Finite rotation required.");
  string id=request.ReplaceDesignId??request.ProposedDesignId??Guid.NewGuid().ToString("D");
  if(!Guid.TryParse(id,out var parsed)||parsed==Guid.Empty)throw new ArgumentException("Valid nonempty design GUID required.");
  if(request.ReplaceDesignId is not null&&request.ProposedDesignId is not null&&(!Guid.TryParse(request.ProposedDesignId,out var proposed)||proposed!=parsed))throw new ArgumentException("Replacement must preserve the selected design identity.");
  var original=request.Design;
  var frozen=CanopyEngine.Calculate(C2CapacityPlanner.Calculate(original.SitePlan.Inputs),original.Inputs);
  if(CanopyReview.Digest(JsonSerializer.Serialize(original))!=CanopyReview.Digest(JsonSerializer.Serialize(frozen)))throw new ArgumentException("Stale or altered canopy ledger/site geometry; recalculate the preview.");
  activeReview=new(request with{Design=frozen,ProposedDesignId=id},equipment);return activeReview;
 }

 // Call only from an explicit Validate action; no leaf edit or debounce should call the host.
 public CanopyPreflightTicket Validate(CanopyReview review)
 {
  ArgumentNullException.ThrowIfNull(review);
  long validationEpoch=++epoch; // A new validation attempt supersedes previous tickets even if it fails.
  if(!ReferenceEquals(activeReview,review))throw new InvalidOperationException("Review was superseded or invalidated by an edit.");
  var context=RequireContext();var report=host.Preflight(review.Request);
  if(RequireContext()!=context)throw new InvalidOperationException("Document, level, runtime or command session changed during preflight.");
  if(epoch!=validationEpoch||!ReferenceEquals(activeReview,review))throw new InvalidOperationException("Review changed during validation.");
  BaselinePreflightAdapter.RequireSafetyPasses(report);
  return new(this,review,context,report,utcNow().AddMinutes(2),validationEpoch);
 }

 // Future command calls this AFTER the user confirms canopy-only scope and picks an origin.
 // It returns data only. It never starts a transaction, places anything, saves or syncs.
 public CanopyOnlyHandoff ConfirmForPlacement(CanopyReview currentReview,CanopyPreflightTicket ticket,bool explicitCanopyOnlyConfirmation)
 {
  ArgumentNullException.ThrowIfNull(currentReview);ArgumentNullException.ThrowIfNull(ticket);
  if(!explicitCanopyOnlyConfirmation)throw new InvalidOperationException("Explicit canopy-only placement confirmation required.");
  lock(ticket){
   if(ticket.Owner!=this||ticket.Consumed||ticket.Epoch!=epoch||!ReferenceEquals(activeReview,currentReview)||!ReferenceEquals(ticket.Review,currentReview)||ticket.ExpiresUtc<=utcNow())throw new InvalidOperationException("Preflight ticket is stale, consumed, expired or belongs to another review/session.");
   ticket.Consumed=true; // Failed final checks also require a fresh explicit validation.
  }
  if(RequireContext()!=ticket.Context)throw new InvalidOperationException("Document or level changed after validation.");
  var report=host.Preflight(currentReview.Request);BaselinePreflightAdapter.RequireSafetyPasses(report);
  if(RequireContext()!=ticket.Context)throw new InvalidOperationException("Document or level changed during final preflight.");
  if(epoch!=ticket.Epoch||!ReferenceEquals(activeReview,currentReview))throw new InvalidOperationException("Review changed during final preflight.");
  return new(currentReview,ticket.Context,report);
 }
 public void Cancel(CanopyPreflightTicket ticket){ArgumentNullException.ThrowIfNull(ticket);if(ticket.Owner!=this)throw new InvalidOperationException("Foreign ticket.");lock(ticket){ticket.Consumed=true;if(ticket.Epoch==epoch)epoch++;}}
 private HostContext RequireContext()
 {
  var c=host.CaptureContext()??throw new InvalidOperationException("No host context.");
  if(c.SessionId==Guid.Empty||string.IsNullOrWhiteSpace(c.DocumentKey)||!Path.IsPathFullyQualified(c.DocumentPath)||string.IsNullOrWhiteSpace(c.LevelUniqueId)||!double.IsFinite(c.LevelElevationFeet)||c.RuntimeCompatibility!=CheckStatus.Pass||string.IsNullOrWhiteSpace(c.RuntimeEvidence))throw new InvalidOperationException("Verified host/runtime, document and level context required. No host assumptions are inferred from offline tests.");
  return c;
 }
}
