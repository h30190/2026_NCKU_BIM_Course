package main

import "strings"

// Legacy columns stay in SQLite for compatibility. Fire data is no longer part
// of the public passport, review, matching, or parameter-view contract.
func removedPassportField(name string) bool {
	name = strings.ToLower(name)
	for _, part := range []string{"fire", "防火", "耐火", "遮煙", "阻熱", "codeperformance"} {
		if strings.Contains(name, part) {
			return true
		}
	}
	return false
}

func omitRetiredFields(p *Passport) {
	parameters := make([]RawParameter, 0, len(p.RawParameters))
	for _, parameter := range p.RawParameters {
		if !removedPassportField(parameter.Name + " " + parameter.Builtin) {
			parameters = append(parameters, parameter)
		}
	}
	p.RawParameters = parameters
	for key, source := range p.FieldSources {
		if removedPassportField(key + " " + source.ParameterName) {
			delete(p.FieldSources, key)
		}
	}
	warnings := make([]string, 0, len(p.MappingWarnings))
	for _, warning := range p.MappingWarnings {
		if !removedPassportField(warning) {
			warnings = append(warnings, warning)
		}
	}
	p.MappingWarnings = warnings
}
