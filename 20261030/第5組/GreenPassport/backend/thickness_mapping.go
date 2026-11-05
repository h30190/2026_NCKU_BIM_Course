package main

import (
	"math"
	"strconv"
	"strings"
)

// Older captures already contain typed thickness in raw evidence, even when the
// previous mapper missed WINDOW_THICKNESS. Recover only empty, non-manual fields.
func recoverLeafThickness(p *Passport) {
	if p.LeafThicknessMm > 0 || p.FieldSources["leafThickness"].Kind == "manual" {
		return
	}
	type candidate struct {
		parameter RawParameter
		mm        float64
		rank      int
	}
	var best *candidate
	conflict := false
	for _, raw := range p.RawParameters {
		name := strings.ToLower(strings.TrimSpace(raw.Name))
		rank := 0
		switch name {
		case "gp_門扇厚度_mm":
			rank = 400
		case "leaf thickness", "door leaf thickness", "door thickness", "thickness", "門扇厚度", "門扇厚", "扇厚", "門厚", "厚度":
			rank = 200
		}
		if rank < 300 && (raw.Builtin == "DOOR_THICKNESS" || raw.Builtin == "WINDOW_THICKNESS") {
			rank = 300
		}
		if rank == 0 {
			continue
		}
		if raw.Scope == "Instance" {
			rank += 10
		}
		mm := 0.0
		if raw.NumericValueMm != nil {
			mm = *raw.NumericValueMm
		} else if strings.EqualFold(strings.TrimSpace(raw.Unit), "mm") {
			// Raw Double values use Revit feet; only display text with known mm
			// units can be used when converted NumericValueMm is unavailable.
			mm, _ = strconv.ParseFloat(strings.TrimSpace(raw.DisplayValue), 64)
		}
		if mm <= 0 || math.IsNaN(mm) || math.IsInf(mm, 0) {
			continue
		}
		if best == nil || rank > best.rank {
			best = &candidate{raw, mm, rank}
			conflict = false
		} else if rank == best.rank && math.Abs(mm-best.mm) > 0.1 {
			conflict = true
		}
	}
	if best == nil || conflict {
		return
	}
	if p.FieldSources == nil {
		p.FieldSources = map[string]FieldSource{}
	}
	p.LeafThicknessMm = best.mm
	p.FieldSources["leafThickness"] = FieldSource{
		Kind: "bim", Scope: best.parameter.Scope, ParameterName: best.parameter.Name,
		ParameterID: best.parameter.ParameterID, Unit: "mm", Definition: "leaf_thickness",
		Verification: "由保存的 Revit 厚度參數帶入，未經實體量測",
	}
	warnings := make([]string, 0, len(p.MappingWarnings))
	for _, warning := range p.MappingWarnings {
		if !strings.HasPrefix(warning, "leafThickness：") {
			warnings = append(warnings, warning)
		}
	}
	p.MappingWarnings = warnings
}
