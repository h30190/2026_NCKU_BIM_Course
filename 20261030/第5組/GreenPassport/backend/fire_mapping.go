package main

import (
	"regexp"
	"strings"
)

var explicitZeroMinutes = regexp.MustCompile(`(?i)^0+(?:\.0+)?\s*(?:分鐘|分钟|分|minutes?|mins?|min|小時|小时|hours?|hrs?|hr|h)?$`)

func explicitZeroFireRating(rating string) bool {
	text := strings.ToLower(strings.TrimSpace(rating))
	if text == "" {
		return false
	}
	for _, label := range []string{"無防火需求", "不要求防火", "無防火性能", "不具防火性能", "無防火時效", "non-rated", "no fire"} {
		if strings.Contains(text, label) {
			return true
		}
	}
	return text == "none" || explicitZeroMinutes.MatchString(text)
}

// The course convention treats an explicit no-fire declaration as a completed
// numeric zero. Raw Revit parameters and approval/certificate states are retained.
func normalizeFirePerformance(p *Passport) {
	if !explicitZeroFireRating(p.FireRating) {
		return
	}
	original := p.FireRating
	p.FireRating = "0"
	p.FireMinutes = 0
	if p.FieldSources == nil {
		p.FieldSources = map[string]FieldSource{}
	}
	if p.FieldSources["fireMinutes"].Definition != "zero_fire_minutes" {
		source := p.FieldSources["fireRating"]
		if source.Kind == "" {
			source.Kind = "unknown"
		}
		source.Unit = "min"
		source.Definition = "zero_fire_minutes"
		source.Verification = "原防火欄位：" + original + "；依本專案規則記為 0 分鐘，不表示具有正值防火時效"
		p.FieldSources["fireMinutes"] = source
	}
}
