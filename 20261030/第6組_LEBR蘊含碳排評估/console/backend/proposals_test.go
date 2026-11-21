package main

import (
	"encoding/json"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const testProposals = `{"updatedAt":"","proposals":[
 {"id":"p1","project":"專案1","title":"內隔間改磚牆","changes":[{"from":"IW-3","to":"IW-1"}],"expected":{"cfr":0.1614,"grade":"1級"}},
 {"id":"p2","project":"專案1","title":"外牆改帷幕","changes":[{"from":"CW-0","to":"CW-2","dropFinish":true}],"status":"proposed"},
 {"id":"x1","project":"SB","title":"別案","changes":[{"from":"IW-3","to":"IW-1"}]}]}`

// newProposalServer：暫存目錄放 B-LCC、對照表與建議檔；路由綁在 s 上，改 s 的欄位即生效。
func newProposalServer(t *testing.T) (*Server, *http.ServeMux, string) {
	t.Helper()
	dir := t.TempDir()
	blcc, mapping, proposals := filepath.Join(dir, "blcc.json"), filepath.Join(dir, "mapping.json"), filepath.Join(dir, "proposals.json")
	_ = os.WriteFile(blcc, []byte(testBlcc), 0o644)
	_ = os.WriteFile(mapping, []byte(testMapping), 0o644)
	_ = os.WriteFile(proposals, []byte(testProposals), 0o644)
	store, err := NewMappingStore(blcc, mapping)
	if err != nil {
		t.Fatal(err)
	}
	s, mux := newTestMux(t)
	s.Mapping = store
	s.Proposals = NewProposalStore(proposals)
	return s, mux, proposals
}

func urlq(s string) string { return url.QueryEscape(s) }

func TestProposalsListByProject(t *testing.T) {
	_, mux, _ := newProposalServer(t)
	var got []Proposal
	_ = json.Unmarshal(do(t, mux, "GET", "/api/proposals?project="+urlq("專案1"), "").Body.Bytes(), &got)
	if len(got) != 2 || got[0].Status != "proposed" {
		t.Fatalf("got %+v", got)
	}
	if got := chosenIDs(t, mux, "專案1"); len(got) != 0 {
		t.Fatalf("no chosen yet, got %v", got)
	}
}

// chosenIDs：外掛取回的選定建議 ID（依檔案順序）
func chosenIDs(t *testing.T, mux *http.ServeMux, project string) []string {
	t.Helper()
	rec := do(t, mux, "GET", "/api/proposals/chosen?project="+urlq(project), "")
	if rec.Code != 200 {
		t.Fatalf("chosen = %d %s", rec.Code, rec.Body)
	}
	var list []Proposal
	_ = json.Unmarshal(rec.Body.Bytes(), &list)
	ids := []string{}
	for _, p := range list {
		ids = append(ids, p.ID)
	}
	return ids
}

// 不衝突的建議可以同時選定，外掛一次取回全部；另一案不受影響，結果寫回檔案
func TestChooseMultipleCompatible(t *testing.T) {
	_, mux, path := newProposalServer(t)
	for _, id := range []string{"p1", "x1", "p2"} {
		if rec := do(t, mux, "PATCH", "/api/proposals/"+id, `{"status":"chosen"}`); rec.Code != 200 {
			t.Fatalf("choose %s: %d %s", id, rec.Code, rec.Body)
		}
	}
	if got := chosenIDs(t, mux, "專案1"); strings.Join(got, ",") != "p1,p2" {
		t.Fatalf("chosen 專案1 = %v", got)
	}
	raw, _ := os.ReadFile(path)
	var f proposalFile
	_ = json.Unmarshal(raw, &f)
	status := map[string]string{}
	for _, p := range f.Proposals {
		status[p.ID] = p.Status
		if len(p.Conflicts) != 0 {
			t.Fatalf("conflicts must not be saved to file: %+v", p)
		}
	}
	if status["p1"] != "chosen" || status["p2"] != "chosen" || status["x1"] != "chosen" {
		t.Fatalf("file statuses = %v", status)
	}
	if rec := do(t, mux, "PATCH", "/api/proposals/p1", `{"status":"applied"}`); rec.Code != 400 {
		t.Fatalf("status applied must go through /applied, code = %d", rec.Code)
	}
}

func TestAppliedUpdatesMappingAndLocks(t *testing.T) {
	_, mux, _ := newProposalServer(t)

	if rec := do(t, mux, "PATCH", "/api/proposals/p2/applied", `{"applied":{"cfr":0.2263,"grade":"1+級"}}`); rec.Code != 409 {
		t.Fatalf("not chosen yet should be 409, got %d", rec.Code)
	}
	do(t, mux, "PATCH", "/api/proposals/p2", `{"status":"chosen"}`)
	rec := do(t, mux, "PATCH", "/api/proposals/p2/applied", `{"applied":{"cfr":0.2263,"grade":"1+級","eec":96080}}`)
	if rec.Code != 200 {
		t.Fatalf("applied: %d %s", rec.Code, rec.Body)
	}
	var resp struct {
		Proposal       Proposal `json:"proposal"`
		MappingUpdated int      `json:"mappingUpdated"`
	}
	_ = json.Unmarshal(rec.Body.Bytes(), &resp)
	if resp.Proposal.Status != "applied" || resp.Proposal.Applied.Grade != "1+級" || resp.MappingUpdated != 1 {
		t.Fatalf("resp = %+v", resp)
	}

	// 專案1 的外牆：CW-0 + OW-1 → CW-2、外裝清掉；SB 同 elementId 的列不動
	var m mappingResp
	_ = json.Unmarshal(do(t, mux, "GET", "/api/mapping", "").Body.Bytes(), &m)
	wall := m.Items[0]
	if wall.BodyID == nil || *wall.BodyID != "CW-2" || wall.FinishID != nil || wall.Confidence != "confirmed" {
		t.Fatalf("wall = %+v", wall.MappingItem)
	}
	if m.Items[1].BodyID != nil {
		t.Fatalf("other project touched: %+v", m.Items[1].MappingItem)
	}

	if got := chosenIDs(t, mux, "專案1"); len(got) != 0 {
		t.Fatalf("applied is no longer chosen, got %v", got)
	}
}

// 套用過的建議可以再選定（重新套用）；取消時回到 applied，紀錄保留。沒套用過的取消回 proposed
func TestReChooseAppliedProposal(t *testing.T) {
	_, mux, _ := newProposalServer(t)
	do(t, mux, "PATCH", "/api/proposals/p2", `{"status":"chosen"}`)
	do(t, mux, "PATCH", "/api/proposals/p2/applied", `{"applied":{"cfr":0.2263,"grade":"1+級"}}`)

	status := func(id string) Proposal {
		var list []Proposal
		_ = json.Unmarshal(do(t, mux, "GET", "/api/proposals?project="+urlq("專案1"), "").Body.Bytes(), &list)
		for _, p := range list {
			if p.ID == id {
				return p
			}
		}
		t.Fatalf("%s not found", id)
		return Proposal{}
	}
	// 對未選定的建議送「取消」不改變狀態
	if rec := do(t, mux, "PATCH", "/api/proposals/p2", `{"status":"proposed"}`); rec.Code != 200 || status("p2").Status != "applied" {
		t.Fatalf("deselect on applied = %d, status %s", rec.Code, status("p2").Status)
	}
	if rec := do(t, mux, "PATCH", "/api/proposals/p2", `{"status":"chosen"}`); rec.Code != 200 || status("p2").Status != "chosen" {
		t.Fatalf("re-choose applied = %d %s", rec.Code, rec.Body)
	}
	// 取消再次選定：回到 applied（保留套用結果），不是 proposed
	do(t, mux, "PATCH", "/api/proposals/p2", `{"status":"proposed"}`)
	if p2 := status("p2"); p2.Status != "applied" || p2.Applied == nil {
		t.Fatalf("p2 after deselect = %+v", p2)
	}
	do(t, mux, "PATCH", "/api/proposals/p1", `{"status":"chosen"}`)
	do(t, mux, "PATCH", "/api/proposals/p1", `{"status":"proposed"}`)
	if p1 := status("p1"); p1.Status != "proposed" || p1.ChosenAt != "" {
		t.Fatalf("p1 after deselect = %+v", p1)
	}
}

// 刪除只移除該筆，寫回檔案；其他案件、其他建議不受影響
func TestDeleteProposal(t *testing.T) {
	_, mux, path := newProposalServer(t)
	if rec := do(t, mux, "DELETE", "/api/proposals/p1", ""); rec.Code != 200 {
		t.Fatalf("delete = %d %s", rec.Code, rec.Body)
	}
	raw, _ := os.ReadFile(path)
	var f proposalFile
	_ = json.Unmarshal(raw, &f)
	if len(f.Proposals) != 2 || f.Proposals[0].ID != "p2" || f.Proposals[1].ID != "x1" {
		t.Fatalf("file after delete = %+v", f.Proposals)
	}
	if rec := do(t, mux, "DELETE", "/api/proposals/p1", ""); rec.Code != 404 {
		t.Fatalf("delete again = %d", rec.Code)
	}
}

func TestProposalsMissingFileIsEmpty(t *testing.T) {
	s, mux := newTestMux(t)
	s.Proposals = NewProposalStore(filepath.Join(t.TempDir(), "none.json"))
	rec := do(t, mux, "GET", "/api/proposals", "")
	if rec.Code != 200 || strings.TrimSpace(rec.Body.String()) != "[]" {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
}
