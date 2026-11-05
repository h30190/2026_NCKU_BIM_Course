# -*- coding: utf-8 -*-
"""Load the same dependency-free CASE contract used by the backend."""
import os
_path = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..', 'shared', 'casegraph_contract.py'))
_scope = {}
with open(_path, 'rb') as _stream:
    exec(compile(_stream.read(), _path, 'exec'), _scope)
type_identity, type_match = _scope['type_identity'], _scope['type_match']
classify, draft_root = _scope['classify'], _scope['draft_root']
case_matches, related_matches = _scope['case_matches'], _scope['related_matches']
