package main

// 多份建議一起套用時的衝突判定。外掛把選定建議的替換合併後依序改 LEBR_BlccId，
// 所以兩份建議不能對同一個類型的同一個代碼有不同處置，也不能一份的結果又被另一份拿去改。
// 網頁（顯示哪些不能同時選）與選定 API（擋下衝突）共用這裡的規則。

import (
	"fmt"
	"strings"
)

// ProposalConflict 是「這份建議」與另一份不能同時選定的原因。
type ProposalConflict struct {
	ID     string `json:"id"`
	Title  string `json:"title"`
	Reason string `json:"reason"`
}

// scopesOverlap：沒指定類型的替換套用到所有類型，與任何替換都可能碰到同一個類型。
func scopesOverlap(a, b ProposalChange) bool {
	return a.TypeName == nil || b.TypeName == nil || *a.TypeName == *b.TypeName
}

func sameCode(a, b string) bool { return strings.EqualFold(strings.TrimSpace(a), strings.TrimSpace(b)) }

func isFinishCode(code string) bool {
	return strings.HasPrefix(strings.ToUpper(strings.TrimSpace(code)), "OW-")
}

// changeConflict 回傳兩個替換為何不能同時套用；可以時回空字串。完全相同的替換不算衝突（合併成一個）。
func changeConflict(a, b ProposalChange) string {
	if !scopesOverlap(a, b) {
		return ""
	}
	switch {
	case sameCode(a.From, b.From) && !sameCode(a.To, b.To):
		return fmt.Sprintf("都要改 %s，但一份換成 %s、另一份換成 %s", a.From, a.To, b.To)
	case sameCode(a.To, b.From) && !sameCode(a.From, b.From):
		return fmt.Sprintf("一份把 %s 換成 %s，另一份又要改 %s", a.From, a.To, a.To)
	case sameCode(b.To, a.From) && !sameCode(a.From, b.From):
		return fmt.Sprintf("一份把 %s 換成 %s，另一份又要改 %s", b.From, b.To, b.To)
	case a.DropFinish && isFinishCode(b.From):
		return fmt.Sprintf("一份換成 %s 並拿掉外裝，另一份又要改外裝 %s", a.To, b.From)
	case b.DropFinish && isFinishCode(a.From):
		return fmt.Sprintf("一份換成 %s 並拿掉外裝，另一份又要改外裝 %s", b.To, a.From)
	}
	return ""
}

// proposalConflict 回傳兩份建議第一個衝突的原因；不同案件或沒有衝突時回空字串。
func proposalConflict(a, b *Proposal) string {
	if a.ID == b.ID || a.Project != b.Project {
		return ""
	}
	for _, ca := range a.Changes {
		for _, cb := range b.Changes {
			if r := changeConflict(ca, cb); r != "" {
				return r
			}
		}
	}
	return ""
}

// conflictsOf 列出 x 與同案件其他建議的衝突（不論狀態，網頁用來標示哪些不能一起選）。
func conflictsOf(x *Proposal, all []Proposal) []ProposalConflict {
	var out []ProposalConflict
	for i := range all {
		if r := proposalConflict(x, &all[i]); r != "" {
			out = append(out, ProposalConflict{ID: all[i].ID, Title: all[i].Title, Reason: r})
		}
	}
	return out
}
