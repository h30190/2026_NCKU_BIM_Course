package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
)

func TestRetiredFireCannotAffectReviewOrMatch(t *testing.T) {
	for _, rating := range []string{"", "unknown", "0", "120 min"} {
		p := approvedDoor()
		p.FireRating = rating
		p.FireMinutes = 0
		if err := validateRequiredCore(p); err != nil {
			t.Fatal(err)
		}
		req := confirmedDemand()
		req.FireRequirementKnown = false
		req.FireRequired = true
		req.FireMinutes = 999
		result := evaluateMatch(p, req)
		if result.Result != "Matched" || len(result.Checks) != 8 {
			t.Fatalf("fire affected eight-field match: %#v", result)
		}
	}
}

func TestRetiredFireIsAbsentFromAPIAndLegacyDataIsPreserved(t *testing.T) {
	db, err := OpenDB(filepath.Join(t.TempDir(), "removed-fire.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	mux := http.NewServeMux()
	NewServer(db).routes(mux)
	p := approvedDoor()
	p.SourceModel = "A"
	p.SourceUniqueID = "test"
	p.ReviewNote = "Checked"
	p.InspectedBy = "Reviewer"
	p.FireRating = ""
	p.FireMinutes = 0
	p.RawParameters = []RawParameter{{Name: "Width", DisplayValue: "900"}, {Name: "Fire Rating", DisplayValue: "120 min"}}
	data, _ := json.Marshal(p)
	request := httptest.NewRequest("POST", "/passports", bytes.NewReader(data))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatal(response.Body.String())
	}
	if _, err = db.Exec("UPDATE passports SET fire_rating='120 min',fire_minutes=120 WHERE id=1"); err != nil {
		t.Fatal(err)
	}
	request = httptest.NewRequest("GET", "/passports", nil)
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	for _, forbidden := range []string{"fireRating", "fireMinutes", "fireCertificate", "Fire Rating"} {
		if bytes.Contains(response.Body.Bytes(), []byte(forbidden)) {
			t.Fatalf("retired field leaked: %s", response.Body.String())
		}
	}
	var current Passport
	rows := []Passport{}
	if err = json.Unmarshal(response.Body.Bytes(), &rows); err != nil {
		t.Fatal(err)
	}
	current = rows[0]
	var review ReviewPassportRequest
	data, _ = json.Marshal(current)
	if err = json.Unmarshal(data, &review); err != nil {
		t.Fatal(err)
	}
	review.ReuseStatus = "Approved"
	data, _ = json.Marshal(review)
	request = httptest.NewRequest("PATCH", "/passports/1/review", bytes.NewReader(data))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatal(response.Body.String())
	}
	var minutes int
	if err = db.QueryRow("SELECT fire_minutes FROM passports WHERE id=1").Scan(&minutes); err != nil {
		t.Fatal(err)
	}
	if minutes != 120 {
		t.Fatal("unrelated review erased legacy archive")
	}
}
