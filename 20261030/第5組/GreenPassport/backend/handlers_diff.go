package main

import (
	"fmt"
	"math"
	"net/http"
	"strconv"
)

// 位置座標的誤差容許值（mm）。小於這個差距不算「修改」，避免浮點數誤差被誤判。
const positionToleranceMm = 1.0

// GET /diff?from=ID&to=ID
// 規則（跟技術規格文件一致）：
//  1. 用 uniqueId 當比對鍵值，把兩個版本的元件兩兩配對
//  2. 只在新版本出現 → added；只在舊版本出現 → removed
//  3. 兩邊都有，但 typeName/material/quantity/position 任一不同 → modified
//  4. 兩邊都有，且全部相同 → 不算異動，不放進結果
func (s *Server) handleDiff(w http.ResponseWriter, r *http.Request) {
	fromID, err1 := strconv.ParseInt(r.URL.Query().Get("from"), 10, 64)
	toID, err2 := strconv.ParseInt(r.URL.Query().Get("to"), 10, 64)
	if err1 != nil || err2 != nil {
		writeErr(w, http.StatusBadRequest, "from、to 都要是版本 id(數字)")
		return
	}

	fromMap, err := loadSnapshotMap(s.DB, fromID)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	toMap, err := loadSnapshotMap(s.DB, toID)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if len(fromMap) == 0 && len(toMap) == 0 {
		writeErr(w, http.StatusNotFound, "兩個版本都查不到任何元件，id 是不是打錯了？")
		return
	}

	var changes []ChangeEvent

	// 只在 to 出現 → added；兩邊都有 → 比欄位
	for uid, toEl := range toMap {
		fromEl, inFrom := fromMap[uid]
		if !inFrom {
			changes = append(changes, ChangeEvent{
				UniqueID: uid, ChangeType: "added", LastSnapshot: toEl,
			})
			continue
		}
		if fields := diffFields(fromEl, toEl); len(fields) > 0 {
			changes = append(changes, ChangeEvent{
				UniqueID: uid, ChangeType: "modified",
				ChangedFields: fields, LastSnapshot: toEl,
			})
		}
		// 欄位全部相同 → unchanged，不放進結果
	}

	// 只在 from 出現 → removed（最後已知狀態用 from 那筆）
	for uid, fromEl := range fromMap {
		if _, inTo := toMap[uid]; !inTo {
			changes = append(changes, ChangeEvent{
				UniqueID: uid, ChangeType: "removed", LastSnapshot: fromEl,
			})
		}
	}

	writeJSON(w, http.StatusOK, DiffResult{
		FromVersionID: fromID, ToVersionID: toID, Changes: changes,
	})
}

// diffFields 比對同一個元件在兩個版本的欄位，回傳有變的欄位清單。
// 數字欄位本來就是 float64（JSON 解析自動轉型，不會有字串誤判的問題）；
// 座標另外套 1mm 的誤差容許值。
func diffFields(oldEl, newEl ElementSnapshot) []ChangedField {
	var out []ChangedField
	add := func(field, oldV, newV string) {
		out = append(out, ChangedField{Field: field, OldValue: oldV, NewValue: newV})
	}

	if oldEl.TypeName != newEl.TypeName {
		add("typeName", oldEl.TypeName, newEl.TypeName)
	}
	if oldEl.Material != newEl.Material {
		add("material", oldEl.Material, newEl.Material)
	}
	if oldEl.Quantity != newEl.Quantity {
		add("quantity", fmt.Sprintf("%g", oldEl.Quantity), fmt.Sprintf("%g", newEl.Quantity))
	}
	if !withinTolerance(oldEl.PositionX, newEl.PositionX) ||
		!withinTolerance(oldEl.PositionY, newEl.PositionY) ||
		!withinTolerance(oldEl.PositionZ, newEl.PositionZ) {
		add("position",
			fmt.Sprintf("(%.1f, %.1f, %.1f)", oldEl.PositionX, oldEl.PositionY, oldEl.PositionZ),
			fmt.Sprintf("(%.1f, %.1f, %.1f)", newEl.PositionX, newEl.PositionY, newEl.PositionZ))
	}
	return out
}

func withinTolerance(a, b float64) bool {
	return math.Abs(a-b) < positionToleranceMm
}
