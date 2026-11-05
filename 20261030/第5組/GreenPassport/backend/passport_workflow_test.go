package main

import (
	"bytes"
	"database/sql"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strconv"
	"sync"
	"testing"
)

func workflowFixture(t *testing.T) (*sql.DB, *http.ServeMux, Passport) {
	t.Helper()
	db, err := OpenDB(filepath.Join(t.TempDir(), "workflow.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	mux := http.NewServeMux()
	NewServer(db).routes(mux)
	p := approvedDoor()
	p.SourceModel = "Source-A"
	p.SourceUniqueID = "source-unique"
	p.ReviewNote = "verified"
	p.InspectedBy = "tester"
	encoded, _ := json.Marshal(p)
	req := httptest.NewRequest(http.MethodPost, "/passports", bytes.NewReader(encoded))
	req.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("fixture: %d %s", rec.Code, rec.Body.String())
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &p); err != nil {
		t.Fatal(err)
	}
	return db, mux, p
}

func workflowBody(p Passport, status, actor string) WorkflowRequest {
	revision := p.Revision
	return WorkflowRequest{Status: status, Actor: actor, ExpectedRevision: &revision, TargetModel: "Target-B", TargetElementID: "1001", TargetUniqueID: "target-unique-1001"}
}

func workflowCall(mux *http.ServeMux, id int64, body WorkflowRequest) *httptest.ResponseRecorder {
	encoded, _ := json.Marshal(body)
	req := httptest.NewRequest(http.MethodPatch, "/passports/"+strconv.FormatInt(id, 10), bytes.NewReader(encoded))
	req.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	return rec
}

func workflowOK(t *testing.T, mux *http.ServeMux, p Passport, body WorkflowRequest) Passport {
	t.Helper()
	rec := workflowCall(mux, p.ID, body)
	if rec.Code != http.StatusOK {
		t.Fatalf("workflow: %d %s", rec.Code, rec.Body.String())
	}
	var updated Passport
	if err := json.Unmarshal(rec.Body.Bytes(), &updated); err != nil {
		t.Fatal(err)
	}
	return updated
}

func TestWorkflowRejectsMissingIdentityAndRevision(t *testing.T) {
	_, mux, p := workflowFixture(t)
	for _, field := range []string{"revision", "actor", "model", "element", "unique"} {
		t.Run(field, func(t *testing.T) {
			body := workflowBody(p, "Reserved", "owner")
			switch field {
			case "revision":
				body.ExpectedRevision = nil
			case "actor":
				body.Actor = ""
			case "model":
				body.TargetModel = ""
			case "element":
				body.TargetElementID = ""
			case "unique":
				body.TargetUniqueID = ""
			}
			if rec := workflowCall(mux, p.ID, body); rec.Code != http.StatusBadRequest {
				t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
			}
		})
	}
}

func TestWorkflowRejectsSecondOwnerTargetAndStaleVersion(t *testing.T) {
	db, mux, p := workflowFixture(t)
	original := p
	p = workflowOK(t, mux, p, workflowBody(p, "Reserved", "owner"))
	if p.ReservedBy != "owner" || p.TargetUniqueID != "target-unique-1001" {
		t.Fatalf("binding not saved: %+v", p)
	}
	cases := map[string]WorkflowRequest{}
	cases["stale"] = workflowBody(original, "Reserved", "owner")
	cases["other actor"] = workflowBody(p, "Reserved", "other")
	other := workflowBody(p, "Reserved", "owner")
	other.TargetElementID = "2002"
	other.TargetUniqueID = "target-2002"
	cases["other target"] = other
	other = workflowBody(p, "Allocated", "other")
	cases["other actor allocates"] = other
	for name, body := range cases {
		t.Run(name, func(t *testing.T) {
			if rec := workflowCall(mux, p.ID, body); rec.Code != http.StatusConflict {
				t.Fatalf("got %d %s", rec.Code, rec.Body.String())
			}
		})
	}
	var events int
	if err := db.QueryRow("SELECT COUNT(*) FROM passport_events WHERE passport_id=?", p.ID).Scan(&events); err != nil {
		t.Fatal(err)
	}
	if events != 2 {
		t.Fatalf("rejected requests must not create events: %d", events)
	}
	p = workflowOK(t, mux, p, workflowBody(p, "Reserved", "owner")) // preflight check
	p = workflowOK(t, mux, p, workflowBody(p, "Allocated", "owner"))
	other = workflowBody(p, "Allocated", "other")
	other.TargetModel = "Different-Model"
	if rec := workflowCall(mux, p.ID, other); rec.Code != http.StatusConflict {
		t.Fatalf("allocated passport rebound: %d", rec.Code)
	}
	p = workflowOK(t, mux, p, workflowBody(p, "Allocated", "reviewer")) // repair same target remains valid
	if p.TargetModel != "Target-B" || p.TargetElementID != "1001" {
		t.Fatal("repair changed target")
	}
	p = workflowOK(t, mux, p, workflowBody(p, "Installed", "reviewer"))
	if p.Status != "Installed" {
		t.Fatal("installation transition failed")
	}
}

func TestWorkflowConcurrentReservationsHaveOneWinner(t *testing.T) {
	db, mux, p := workflowFixture(t)
	gate := make(chan struct{})
	results := make(chan int, 2)
	var wg sync.WaitGroup
	for _, actor := range []string{"owner-A", "owner-B"} {
		wg.Add(1)
		go func(actor string) {
			defer wg.Done()
			<-gate
			results <- workflowCall(mux, p.ID, workflowBody(p, "Reserved", actor)).Code
		}(actor)
	}
	close(gate)
	wg.Wait()
	close(results)
	success, conflict := 0, 0
	for code := range results {
		switch code {
		case 200:
			success++
		case 409:
			conflict++
		default:
			t.Fatalf("unexpected response %d", code)
		}
	}
	if success != 1 || conflict != 1 {
		t.Fatalf("success=%d conflict=%d", success, conflict)
	}
	updated, err := getPassportByID(db, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	if updated.Revision != p.Revision+1 {
		t.Fatalf("revision=%d", updated.Revision)
	}
}

func TestWorkflowReleaseClearsBindingAndAllowsNewOwner(t *testing.T) {
	_, mux, p := workflowFixture(t)
	p = workflowOK(t, mux, p, workflowBody(p, "Reserved", "A"))
	p = workflowOK(t, mux, p, workflowBody(p, "Available", "A"))
	if p.ReservedBy != "" || p.TargetModel != "" || p.TargetElementID != "" || p.TargetUniqueID != "" {
		t.Fatal("release did not clear binding")
	}
	p = workflowOK(t, mux, p, workflowBody(p, "Reserved", "B"))
	if p.ReservedBy != "B" {
		t.Fatal("new owner not saved")
	}
}

func TestWorkflowCannotReserveZeroStoredStock(t *testing.T) {
	db, mux, p := workflowFixture(t)
	if _, err := db.Exec("UPDATE passports SET quantity=0 WHERE id=?", p.ID); err != nil {
		t.Fatal(err)
	}
	if rec := workflowCall(mux, p.ID, workflowBody(p, "Reserved", "owner")); rec.Code != http.StatusConflict {
		t.Fatalf("zero stock reserved: %d %s", rec.Code, rec.Body.String())
	}
}

func TestWorkflowLegacyAllocationMigrationPreservesTarget(t *testing.T) {
	db, _, p := workflowFixture(t)
	_, err := db.Exec("UPDATE passports SET status='Allocated',target_model='Target-B',target_element_id='1001' WHERE id=?", p.ID)
	if err != nil {
		t.Fatal(err)
	}
	for _, column := range []string{"target_unique_id", "reserved_by"} {
		if _, err := db.Exec("ALTER TABLE passports DROP COLUMN " + column); err != nil {
			t.Fatal(err)
		}
	}
	if err := migrate(db); err != nil {
		t.Fatal(err)
	}
	p, err = getPassportByID(db, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	if p.Status != "Allocated" || p.TargetModel != "Target-B" || p.TargetElementID != "1001" {
		t.Fatal("migration changed allocation")
	}
	mux := http.NewServeMux()
	NewServer(db).routes(mux)
	bad := workflowBody(p, "Allocated", "reviewer")
	bad.TargetElementID = "2002"
	if rec := workflowCall(mux, p.ID, bad); rec.Code != 409 {
		t.Fatal("legacy allocation rebound")
	}
	p = workflowOK(t, mux, p, workflowBody(p, "Allocated", "reviewer"))
	if p.TargetUniqueID != "target-unique-1001" {
		t.Fatal("legacy same-target repair not bound")
	}
}

func TestWorkflowAuditFailureRollsBackReservation(t *testing.T) {
	db, mux, p := workflowFixture(t)
	if _, err := db.Exec(`CREATE TRIGGER reject_workflow BEFORE INSERT ON passport_events WHEN NEW.action LIKE 'workflow:%' BEGIN SELECT RAISE(ABORT, 'audit failure fixture'); END;`); err != nil {
		t.Fatal(err)
	}
	if rec := workflowCall(mux, p.ID, workflowBody(p, "Reserved", "owner")); rec.Code != http.StatusInternalServerError {
		t.Fatalf("want 500, got %d", rec.Code)
	}
	unchanged, err := getPassportByID(db, p.ID)
	if err != nil {
		t.Fatal(err)
	}
	if unchanged.Status != "Available" || unchanged.Revision != p.Revision || unchanged.ReservedBy != "" || unchanged.TargetModel != "" {
		t.Fatal("audit failure left a partial reservation")
	}
}
