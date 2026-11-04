"""Backend adapter for the shared CASE contract; no Revit dependencies."""
from pathlib import Path
from importlib.util import module_from_spec, spec_from_file_location

_spec = spec_from_file_location('_casegraph_contract', Path(__file__).resolve().parents[2] / 'shared' / 'casegraph_contract.py')
_contract = module_from_spec(_spec)
_spec.loader.exec_module(_contract)
type_identity, type_match = _contract.type_identity, _contract.type_match
classify, draft_root = _contract.classify, _contract.draft_root
case_matches, related_matches = _contract.case_matches, _contract.related_matches
