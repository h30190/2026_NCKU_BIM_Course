# -*- coding: utf-8 -*-
"""Coalesce UI refresh requests and reject results from superseded requests."""
from copy import deepcopy


class RefreshQueue(object):
    def __init__(self):
        self.pending = set()
        self.versions = {}

    def mark(self, *parts):
        for part in parts:
            self.versions[part] = self.versions.get(part, 0) + 1
            self.pending.add(part)

    def take(self):
        ticket = dict((part, self.versions[part]) for part in self.pending)
        self.pending.clear()
        return ticket

    def valid(self, ticket, part):
        return part in ticket and ticket[part] == self.versions.get(part)


def with_case_context(graph, current):
    """Add the open issue to a live preview without changing its saved snapshot."""
    preview = deepcopy(graph)
    if not current or current['draft']['graph']['root'] != graph['root']:
        return preview
    saved = current['draft']['graph']
    issues = [deepcopy(n) for n in saved['nodes'] if n['node_type'] == 'ISSUE']
    issue_ids = set(n['node_id'] for n in issues)
    for issue in issues:
        issue['properties']['status'] = current['status']
    preview['nodes'].extend(issues)
    preview['edges'].extend(deepcopy(e) for e in saved['edges']
                            if e['relationship_type'] == 'HAS_ISSUE'
                            and e['source'] == graph['root'] and e['target'] in issue_ids)
    return preview
