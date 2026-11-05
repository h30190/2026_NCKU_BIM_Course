package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const testBlcc = `{"items":[
 {"id":"CW-0","category":"cw","name":"傳統 15cm RC 外牆（基準）","new":73.67,"baselineId":"CW-0"},
 {"id":"OW-1","category":"ow","name":"RC 外牆貼磁磚（基準）","new":28.7,"baselineId":"OW-1"},
 {"id":"IW-3","category":"iw","name":"輕隔間牆（矽酸鈣板）","new":22.33,"baselineId":"IW-1"}]}`

// 兩個專案剛好有相同 elementId（同一份範本帶出的類型），PATCH 必須連 project 一起比對
const testMapping = `{"updatedAt":"","items":[
 {"elementId":1740,"project":"專案1","typeName":"通用 - 200mm","kind":"wall","bodyId":"CW-0","finishId":"OW-1","areaM2":10,"confidence":"guess","note":""},
 {"elementId":1740,"project":"SB","typeName":"同 ID 的別案類型","kind":"interior","bodyId":null,"finishId":null,"areaM2":5,"confidence":"guess","note":""},
 {"elementId":9,"project":"專案1","typeName":"門","kind":"excluded","bodyId":null,"finishId":null,"areaM2":3,"confidence":"confirmed","note":""}]}`

func newMappingMux(t *testing.T) (*http.ServeMux, string) {
	t.Helper()
	dir := t.TempDir()
	blcc := filepath.Join(dir, "blcc.json")
	mapping := filepath.Join(dir, "data", "mapping.json")
	_ = os.WriteFile(blcc, []byte(testBlcc), 0o644)
	_ = os.MkdirAll(filepath.Dir(mapping), 0o755)
	_ = os.WriteFile(mapping, []byte(testMapping), 0o644)
	store, err := NewMappingStore(blcc, mapping)
	if err != nil {
		t.Fatal(err)
	}
	s, mux := newTestMux(t)
	s.Mapping = store
	return mux, mapping
}

type mappingResp struct {
	Items []ResolvedMapping `json:"items"`
	Total float64           `json:"total"`
}

func TestMappingListAndSubtotal(t *testing.T) {
	mux, _ := newMappingMux(t)
	var got mappingResp
	_ = json.Unmarshal(do(t, mux, "GET", "/api/mapping", "").Body.Bytes(), &got)
	if len(got.Items) != 3 {
		t.Fatalf("items = %d", len(got.Items))
	}
	// (73.67 + 28.7) × 10；排除項不計
	if w := got.Items[0]; w.UnitEC != 102.37 || w.Subtotal < 1023.69 || w.Subtotal > 1023.71 || w.BodyName == "" {
		t.Fatalf("wall row = %+v", w)
	}
	if got.Items[2].Subtotal != 0 {
		t.Fatalf("excluded row should not count: %+v", got.Items[2])
	}

	var opts []BlccOption
	_ = json.Unmarshal(do(t, mux, "GET", "/api/blcc?category=iw", "").Body.Bytes(), &opts)
	if len(opts) != 1 || opts[0].ID != "IW-3" {
		t.Fatalf("iw options = %+v", opts)
	}
}

func TestMappingPatchMatchesProjectAndConfidence(t *testing.T) {
	mux, path := newMappingMux(t)

	// 只改面積：不應標為已確認
	rec := do(t, mux, "PATCH", "/api/mapping/1740", `{"project":"SB","areaM2":8}`)
	var r ResolvedMapping
	_ = json.Unmarshal(rec.Body.Bytes(), &r)
	if rec.Code != 200 || r.AreaM2 != 8 || r.Confidence != "guess" || r.Project != "SB" {
		t.Fatalf("area-only patch = %d %+v", rec.Code, r)
	}
	// 改代碼：標為已確認，且只改到 SB，不動專案1 的同 ID 類型
	rec = do(t, mux, "PATCH", "/api/mapping/1740", `{"project":"SB","bodyId":"IW-3"}`)
	_ = json.Unmarshal(rec.Body.Bytes(), &r)
	if r.Confidence != "confirmed" || r.BodyID == nil || *r.BodyID != "IW-3" {
		t.Fatalf("code patch = %+v", r)
	}
	raw, _ := os.ReadFile(path)
	var f mappingFile
	_ = json.Unmarshal(raw, &f)
	if *f.Items[0].BodyID != "CW-0" || f.Items[0].Confidence != "guess" || f.UpdatedAt == "" {
		t.Fatalf("other project's row changed or file not saved: %+v", f.Items[0])
	}
	// 清除外裝
	rec = do(t, mux, "PATCH", "/api/mapping/1740", `{"project":"專案1","finishId":""}`)
	_ = json.Unmarshal(rec.Body.Bytes(), &r)
	if r.FinishID != nil {
		t.Fatalf("finish not cleared: %+v", r)
	}

	for _, c := range []struct {
		path, body string
		code       int
	}{
		{"/api/mapping/1740", `{"project":"SB","bodyId":"XX-9"}`, http.StatusBadRequest},
		{"/api/mapping/1740", `{"project":"SB","areaM2":-1}`, http.StatusBadRequest},
		{"/api/mapping/1740", `{"project":"沒有這個專案","areaM2":1}`, http.StatusNotFound},
		{"/api/mapping/abc", `{}`, http.StatusBadRequest},
	} {
		if rec := do(t, mux, "PATCH", c.path, c.body); rec.Code != c.code {
			t.Fatalf("%s %s = %d, want %d", c.path, c.body, rec.Code, c.code)
		}
	}
}

func TestMappingReloadsWhenAgentEditsFile(t *testing.T) {
	mux, path := newMappingMux(t)
	edited := strings.Replace(testMapping, `"typeName":"門"`, `"typeName":"門（agent 改過）"`, 1)
	_ = os.WriteFile(path, []byte(edited), 0o644)
	future := time.Now().Add(2 * time.Second) // 確保修改時間與載入時不同
	_ = os.Chtimes(path, future, future)

	var got mappingResp
	_ = json.Unmarshal(do(t, mux, "GET", "/api/mapping", "").Body.Bytes(), &got)
	if got.Items[2].TypeName != "門（agent 改過）" {
		t.Fatalf("not reloaded: %+v", got.Items[2])
	}
}

func TestMappingDisabledWithoutBlcc(t *testing.T) {
	_, mux := newTestMux(t)
	if rec := do(t, mux, "GET", "/api/mapping", ""); rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("no store = %d", rec.Code)
	}
}
