# -*- coding: utf-8 -*-
"""Coalesce UI refresh requests and reject results from superseded requests."""


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
