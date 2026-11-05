package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
)

func thicknessParameter(name, builtin string, mm float64) RawParameter {
	return RawParameter{Scope: "Type", Name: name, Builtin: builtin, ParameterID: "-1001302", Unit: "mm", NumericValueMm: &mm}
}

func TestRecoverThicknessFromDoorFamilyWindowBuiltin(t *testing.T) {
	p := Passport{RawParameters: []RawParameter{
		thicknessParameter("厚度", "WINDOW_THICKNESS", 50),
		thicknessParameter("Masonry Frame Depth", "", 146.05),
	}, MappingWarnings: []string{"leafThickness：未找到具明確單位及語意的參數", "其他欄位缺漏"}}
	recoverLeafThickness(&p)
	if p.LeafThicknessMm != 50 || p.FieldSources["leafThickness"].Kind != "bim" || p.FieldSources["leafThickness"].ParameterName != "厚度" {
		t.Fatalf("expected BIM thickness 50 mm, got %#v", p)
	}
	if len(p.MappingWarnings) != 1 || p.MappingWarnings[0] != "其他欄位缺漏" {
		t.Fatal("stale thickness warning not removed correctly")
	}
}

func TestRecoverThicknessRespectsManualAndRejectsUncertainEvidence(t *testing.T) {
	for _, tc := range []struct {
		name     string
		passport Passport
		want     float64
	}{
		{"manual value", Passport{LeafThicknessMm: 42, RawParameters: []RawParameter{thicknessParameter("厚度", "WINDOW_THICKNESS", 50)}}, 42},
		{"manual empty", Passport{FieldSources: map[string]FieldSource{"leafThickness": {Kind: "manual"}}, RawParameters: []RawParameter{thicknessParameter("厚度", "WINDOW_THICKNESS", 50)}}, 0},
		{"frame only", Passport{RawParameters: []RawParameter{thicknessParameter("Masonry Frame Depth", "", 146.05)}}, 0},
		{"conflicting type data", Passport{RawParameters: []RawParameter{thicknessParameter("厚度", "WINDOW_THICKNESS", 50), thicknessParameter("Thickness", "WINDOW_THICKNESS", 60)}}, 0},
		{"unknown unit", Passport{RawParameters: []RawParameter{{Name: "厚度", RawValue: "0.16404199475065617", DisplayValue: "50"}}}, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			recoverLeafThickness(&tc.passport)
			if tc.passport.LeafThicknessMm != tc.want {
				t.Fatalf("got %v, want %v", tc.passport.LeafThicknessMm, tc.want)
			}
		})
	}
}

func TestSavedLegacyPassportReadFillsThicknessWithoutChangingReview(t *testing.T) {
	db, err := OpenDB(filepath.Join(t.TempDir(), "thickness.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	mux := http.NewServeMux()
	NewServer(db).routes(mux)
	request := httptest.NewRequest(http.MethodPost, "/passports", strings.NewReader(`{"sourceModel":"A","sourceUniqueId":"door","reuseStatus":"Pending"}`))
	request.Header.Set("X-Green-Passport-Client", "RevitPlugin")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatal(response.Body.String())
	}
	legacy, _ := json.Marshal([]RawParameter{thicknessParameter("厚度", "WINDOW_THICKNESS", 50)})
	_, err = db.Exec(`UPDATE passports SET leaf_thickness_mm=0,raw_parameters_json=?,field_sources_json='{}'`, string(legacy))
	if err != nil {
		t.Fatal(err)
	}
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/passports", nil))
	var results []Passport
	if err = json.Unmarshal(response.Body.Bytes(), &results); err != nil {
		t.Fatal(err)
	}
	if len(results) != 1 || results[0].LeafThicknessMm != 50 || results[0].ReuseStatus != "Pending" || results[0].Revision != 1 {
		t.Fatalf("incorrect legacy recovery: %#v", results)
	}
}
