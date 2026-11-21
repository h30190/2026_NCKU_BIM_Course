package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func strp(s string) *string { return &s }

func TestChangeConflictRules(t *testing.T) {
	ch := func(from, to string) ProposalChange { return ProposalChange{From: from, To: to} }
	cases := []struct {
		name string
		a, b ProposalChange
		want string // 原因裡要出現的字；空 = 不衝突
	}{
		{"同代碼換成不同目標", ch("IW-4", "IW-5"), ch("IW-4", "IW-3"), "都要改 IW-4"},
		{"完全相同的替換不衝突", ch("IW-4", "IW-5"), ch("iw-4", "IW-5"), ""},
		{"串接：前一份的結果被另一份改", ch("IW-4", "IW-5"), ch("IW-5", "IW-3"), "又要改 IW-5"},
		{"串接（反方向）", ch("IW-5", "IW-3"), ch("IW-4", "IW-5"), "又要改 IW-5"},
		{"不同代碼互不相干", ch("IW-4", "IW-5"), ch("W-1-AL", "W-1-PL"), ""},
		{"拿掉外裝 vs 改外裝", ProposalChange{From: "CW-0", To: "CW-2", DropFinish: true}, ch("OW-1", "OW-5"), "拿掉外裝"},
		{"不同類型不衝突", ProposalChange{From: "IW-4", To: "IW-5", TypeName: strp("浴室牆")},
			ProposalChange{From: "IW-4", To: "IW-3", TypeName: strp("走廊牆")}, ""},
		{"一份不限類型就會重疊", ch("IW-4", "IW-5"), ProposalChange{From: "IW-4", To: "IW-3", TypeName: strp("浴室牆")}, "都要改 IW-4"},
	}
	for _, c := range cases {
		got := changeConflict(c.a, c.b)
		if (c.want == "") != (got == "") || !strings.Contains(got, c.want) {
			t.Errorf("%s: got %q, want contains %q", c.name, got, c.want)
		}
	}
}

// 列表附上衝突；選定時和已選定的衝突回 409，取消那份後就能選
func TestChooseRejectsConflict(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "proposals.json")
	_ = os.WriteFile(path, []byte(`{"proposals":[
	 {"id":"a","project":"P","title":"隔間改清水磚","changes":[{"from":"IW-4","to":"IW-5"}]},
	 {"id":"b","project":"P","title":"隔間改輕隔間","changes":[{"from":"IW-4","to":"IW-3"}]},
	 {"id":"c","project":"P","title":"窗框改塑鋼","changes":[{"from":"W-1-AL","to":"W-1-PL"}]}]}`), 0o644)
	s, mux := newTestMux(t)
	s.Proposals = NewProposalStore(path)

	var list []Proposal
	_ = json.Unmarshal(do(t, mux, "GET", "/api/proposals?project=P", "").Body.Bytes(), &list)
	if len(list[0].Conflicts) != 1 || list[0].Conflicts[0].ID != "b" || len(list[2].Conflicts) != 0 {
		t.Fatalf("conflicts = %+v / %+v", list[0].Conflicts, list[2].Conflicts)
	}

	do(t, mux, "PATCH", "/api/proposals/a", `{"status":"chosen"}`)
	if rec := do(t, mux, "PATCH", "/api/proposals/c", `{"status":"chosen"}`); rec.Code != 200 {
		t.Fatalf("compatible c = %d %s", rec.Code, rec.Body)
	}
	rec := do(t, mux, "PATCH", "/api/proposals/b", `{"status":"chosen"}`)
	if rec.Code != 409 || !strings.Contains(rec.Body.String(), "隔間改清水磚") {
		t.Fatalf("conflicting b = %d %s", rec.Code, rec.Body)
	}
	if got := strings.Join(chosenIDs(t, mux, "P"), ","); got != "a,c" {
		t.Fatalf("chosen after rejected = %s", got)
	}
	do(t, mux, "PATCH", "/api/proposals/a", `{"status":"proposed"}`)
	if rec := do(t, mux, "PATCH", "/api/proposals/b", `{"status":"chosen"}`); rec.Code != 200 {
		t.Fatalf("b after a deselected = %d %s", rec.Code, rec.Body)
	}
}
