package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strconv"
	"testing"
)

func approvedDoor() Passport {
	return Passport{
		ID: 1, PassportID: "GP-D-001", WidthMm: 900, HeightMm: 2100,
		DimensionDefinition: "nominal", LeafThicknessMm: 45, OperationType: "單扇平開",
		SwingHanding: "右開", UseType: "Interior", Material: "鋼製", ClearWidthMm: 820,
		LeafCount: 1, FireRating: "60 分鐘", FireMinutes: 60, FireCertificateReference: "CERT-60",
		FireCertificateVerified: true, ReuseStatus: "Approved", Status: "Available", Quantity: 1,
	}
}

func confirmedDemand() MatchRequest {
	return MatchRequest{
		WidthMm: 900, HeightMm: 2100, DimensionDefinition: "nominal", ToleranceMm: 5,
		LeafThicknessMm: 45, OperationType: "單扇平開", SwingHanding: "右開", UseType: "Interior",
		MaterialPreference: "鋼製", ClearWidthMinMm: 800, LeafCount: 1,
		FireRequirementKnown: true, FireRequired: true, FireMinutes: 60,
		InstallationReviewed: true, Basis: "設計圖說 D-01",
	}
}

func TestEvaluateMatchAllConfirmed(t *testing.T) {
	result := evaluateMatch(approvedDoor(), confirmedDemand())
	if result.Result != "Matched" {
		t.Fatalf("want Matched, got %s: %#v", result.Result, result.Checks)
	}
}

func TestEvaluateMatchExteriorUseAliases(t *testing.T) {
	aliases := []string{"外部出入口", "外部", "室外", "Exterior", " exterior ", "EXTERIOR"}
	for _, supply := range aliases {
		for _, demand := range aliases {
			t.Run(supply+"/"+demand, func(t *testing.T) {
				p, req := approvedDoor(), confirmedDemand()
				p.UseType, req.UseType = supply, demand
				result := evaluateMatch(p, req)
				if result.Result != "Matched" {
					t.Fatalf("equivalent exterior uses must match: %#v", result.Checks)
				}
				if result.Passport.UseType != supply {
					t.Fatalf("matching must preserve the passport's original use: %q", result.Passport.UseType)
				}
			})
		}
	}
}

func TestEvaluateMatchExteriorUseDoesNotRelaxOtherChecks(t *testing.T) {
	for _, tc := range []struct {
		name, supply, demand, want string
	}{
		{"different uses", "外部出入口", "Interior", "Not Matched"},
		{"different uses reversed", "Interior", "室外", "Not Matched"},
		{"unknown use", "外部出入口", "待確認", "Not Matched"},
		{"ambiguous use", "室內或室外", "Exterior", "Not Matched"},
		{"missing supply", "", "Exterior", "Pending Review"},
		{"missing demand", "外部出入口", " ", "Pending Review"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p, req := approvedDoor(), confirmedDemand()
			p.UseType, req.UseType = tc.supply, tc.demand
			if got := evaluateMatch(p, req).Result; got != tc.want {
				t.Fatalf("got %s, want %s", got, tc.want)
			}
		})
	}
	p, req := approvedDoor(), confirmedDemand()
	p.UseType, req.UseType = "外部出入口", "Exterior"
	req.HeightMm = p.HeightMm + 5.01
	if got := evaluateMatch(p, req).Result; got != "Not Matched" {
		t.Fatalf("exterior aliases must not relax the ±5 mm dimension rule, got %s", got)
	}
}

func TestEvaluateMatchDimensionFailure(t *testing.T) {
	req := confirmedDemand()
	req.WidthMm = 1000
	if got := evaluateMatch(approvedDoor(), req).Result; got != "Not Matched" {
		t.Fatalf("want Not Matched, got %s", got)
	}
}

func TestEvaluateMatchDimensionToleranceIsFixedAtFiveMillimetres(t *testing.T) {
	req := confirmedDemand()
	req.ToleranceMm = 100 // The API/client may not relax the business rule.
	req.WidthMm = 905
	req.HeightMm = 2095
	if got := evaluateMatch(approvedDoor(), req).Result; got != "Matched" {
		t.Fatalf("exactly ±5 mm should match, got %s", got)
	}
	req.WidthMm = 905.01
	if got := evaluateMatch(approvedDoor(), req).Result; got != "Not Matched" {
		t.Fatalf("more than 5 mm must not match even when client asks for a larger tolerance, got %s", got)
	}
}

func TestReferenceFieldsDoNotAffectMatchResult(t *testing.T) {
	p := approvedDoor()
	p.Condition = "待整理"
	p.ConditionGrade = "C"
	req := confirmedDemand()
	req.DimensionDefinition = "frame_outer"
	req.LeafCount = 2
	req.InstallationReviewed = false
	req.Basis = ""
	req.HostWallThicknessMm = 999
	result := evaluateMatch(p, req)
	if result.Result != "Matched" {
		t.Fatalf("uncoloured reference fields must not block matching, got %s: %#v", result.Result, result.Checks)
	}
	if len(result.Checks) != 8 {
		t.Fatalf("matching should contain exactly the eight coloured criteria, got %d: %#v", len(result.Checks), result.Checks)
	}
	for _, check := range result.Checks {
		switch check.Key {
		case "供應資格", "門扇數", "安裝條件", "需求依據", "保存狀況":
			t.Fatalf("reference-only field leaked into matching checks: %#v", check)
		}
	}
}

func TestEvaluateMatchUnknownDoesNotBecomePass(t *testing.T) {
	p := approvedDoor()
	p.ClearWidthMm = 0
	result := evaluateMatch(p, confirmedDemand())
	if result.Result != "Pending Review" {
		t.Fatalf("missing clear width must remain Pending Review, got %s", result.Result)
	}
}

func TestEvaluateMatchRequiredDemandCannotBeOmitted(t *testing.T) {
	req := confirmedDemand()
	req.LeafThicknessMm = 0
	req.MaterialPreference = ""
	req.ClearWidthMinMm = 0
	if got := evaluateMatch(approvedDoor(), req).Result; got != "Pending Review" {
		t.Fatalf("omitted required demand fields must remain Pending Review, got %s", got)
	}
}

func TestEvaluateMatchCertificateMetadataDoesNotAffectMatching(t *testing.T) {
	p := approvedDoor()
	p.FireCertificateVerified = false
	p.FireCertificateReference = ""
	if got := evaluateMatch(p, confirmedDemand()).Result; got != "Matched" {
		t.Fatalf("certificate metadata must not block a eight matching fields, got %s", got)
	}
}

func TestMinimumClearWidthThresholds(t *testing.T) {
	for _, tc := range []struct {
		name        string
		clearWidth  float64
		fireMinutes int
		want        string
	}{
		{"equal minimums", 800, 60, "Matched"},
		{"greater minimums", 850, 120, "Matched"},
		{"clear width below minimum", 799.99, 120, "Not Matched"},
		{"legacy fire has no effect", 850, 59, "Matched"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := approvedDoor()
			p.ClearWidthMm = tc.clearWidth
			p.FireMinutes = tc.fireMinutes
			p.FireCertificateVerified = false
			p.FireCertificateReference = ""
			if got := evaluateMatch(p, confirmedDemand()).Result; got != tc.want {
				t.Fatalf("got %s, want %s", got, tc.want)
			}
		})
	}
}

func TestEvaluateMatchConfirmedNoFireRequirement(t *testing.T) {
	p := approvedDoor()
	p.FireMinutes = 0
	p.FireCertificateReference = ""
	p.FireCertificateVerified = false
	req := confirmedDemand()
	req.FireRequired = false
	req.FireMinutes = 0
	if got := evaluateMatch(p, req).Result; got != "Matched" {
		t.Fatalf("designer-confirmed no-fire requirement should not require certificate, got %s", got)
	}
}

func TestApprovedReviewValidation(t *testing.T) {
	if err := validateReview("Approved", "", "reviewer", "nominal", 900, 2100); err == nil {
		t.Fatal("Approved without review note must fail")
	}
	if err := validateReview("Approved", "實體檢核完成", "reviewer", "nominal", 900, 2100); err != nil {
		t.Fatalf("complete Approved review should pass: %v", err)
	}
}

func TestApprovedRequiresEightCoreFields(t *testing.T) {
	p := approvedDoor()
	p.ReuseStatus = "Approved"
	p.Material = ""
	if err := validateRequiredCore(p); err == nil {
		t.Fatal("Approved without material must fail")
	}
	p.Material = "鋼製"
	p.FireRating = "無防火需求"
	p.FireMinutes = 0
	if err := validateRequiredCore(p); err != nil {
		t.Fatalf("explicit no-fire requirement must count as completed zero: %v", err)
	}
	p.FireRating = "無防火性能"
	if err := validateRequiredCore(p); err != nil {
		t.Fatalf("explicit no-fire-performance classification should be complete: %v", err)
	}
}

func TestDashboardCannotWriteButRevitClientCan(t *testing.T) {
	db, err := OpenDB(filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	mux := http.NewServeMux()
	NewServer(db).routes(mux)

	p := approvedDoor()
	p.SourceModel = "A.rvt"
	p.SourceUniqueID = "unique-1"
	p.SourceElementID = "1001"
	p.ReviewNote = "實體檢核完成"
	p.InspectedBy = "tester"
	body, _ := json.Marshal(p)

	request := httptest.NewRequest(http.MethodPost, "/passports", bytes.NewReader(body))
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("browser-like write must be forbidden, got %d", response.Code)
	}

	request = httptest.NewRequest(http.MethodPost, "/passports", bytes.NewReader(body))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("Revit client write should succeed, got %d: %s", response.Code, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/passports", nil)
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusOK || !bytes.Contains(response.Body.Bytes(), []byte("GP-D-001")) {
		t.Fatalf("read-only list failed: %d %s", response.Code, response.Body.String())
	}
}

func TestReviewCanCompleteCoreFieldsAndRecordsManualSource(t *testing.T) {
	db, err := OpenDB(filepath.Join(t.TempDir(), "review.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	mux := http.NewServeMux()
	NewServer(db).routes(mux)

	p := approvedDoor()
	p.ReuseStatus = "Pending"
	p.Status = "Unavailable"
	p.Material = ""
	p.FireRating = ""
	p.FireMinutes = 0
	p.FireCertificateReference = ""
	p.FireCertificateVerified = false
	p.SourceModel = "A-review.rvt"
	p.SourceUniqueID = "unique-review"
	p.SourceElementID = "2001"
	body, _ := json.Marshal(p)
	request := httptest.NewRequest(http.MethodPost, "/passports", bytes.NewReader(body))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("create Pending passport failed: %d %s", response.Code, response.Body.String())
	}
	var created Passport
	if err := json.Unmarshal(response.Body.Bytes(), &created); err != nil {
		t.Fatal(err)
	}

	review := ReviewPassportRequest{
		WidthMm: created.WidthMm, HeightMm: created.HeightMm, DimensionDefinition: created.DimensionDefinition,
		LeafThicknessMm: created.LeafThicknessMm, OperationType: created.OperationType, SwingHanding: created.SwingHanding,
		UseType: created.UseType, Material: "不鏽鋼", ClearWidthMm: created.ClearWidthMm,
		FireRating: "60 分鐘", FireMinutes: 60, ReuseStatus: "Approved",
		Condition: "良好", ConditionGrade: "A", ReviewNote: "已完成實體檢核",
		InspectedBy: "reviewer", InspectedOn: "2026-11-02", Quantity: 1,
		FireCertificateReference: "CERT-60", FireCertificateVerified: true, Revision: created.Revision,
	}
	body, _ = json.Marshal(review)
	request = httptest.NewRequest(http.MethodPatch, "/passports/"+strconv.FormatInt(created.ID, 10)+"/review", bytes.NewReader(body))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("review update failed: %d %s", response.Code, response.Body.String())
	}
	var reviewed Passport
	if err := json.Unmarshal(response.Body.Bytes(), &reviewed); err != nil {
		t.Fatal(err)
	}
	if reviewed.Status != "Available" || reviewed.Material != "不鏽鋼" {
		t.Fatalf("reviewed core fields were not persisted: %#v", reviewed)
	}
	if source, ok := reviewed.FieldSources["material"]; !ok || source.Kind != "manual" {
		t.Fatalf("manual material source was not recorded: %#v", reviewed.FieldSources)
	}
	if _, ok := reviewed.FieldSources["fireRating"]; ok {
		t.Fatalf("retired fire source was exposed: %#v", reviewed.FieldSources)
	}
}
