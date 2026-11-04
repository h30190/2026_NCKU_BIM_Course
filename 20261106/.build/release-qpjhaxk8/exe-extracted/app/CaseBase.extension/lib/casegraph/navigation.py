# -*- coding: utf-8 -*-
"""Pages shared by ribbon commands, grouped into the panel's three workflows."""
# (feature, TabItem, title, workflow, sub-page button)
FEATURES = [
    ('checks', 'ChecksTab', u'模型檢查', 'checks', None),
    ('similar', 'SimilarTab', u'相似元素', 'elements', 'SubSimilar'),
    ('compare', 'CompareTab', u'元素比較', 'elements', 'SubCompare'),
    ('graph', 'GraphTab', u'關係圖', 'elements', 'SubGraph'),
    ('library', 'LibraryTab', u'案例庫', 'cases', 'SubLibrary'),
    ('related', 'HistoryTab', u'相關案例', 'cases', 'SubRelated'),
    ('issue', 'IssueTab', u'問題紀錄', 'cases', 'SubIssue'),
    ('review', 'ReviewTab', u'解法審查', 'cases', 'SubReview'),
    ('suggest', 'SuggestTab', u'修改建議', 'cases', 'SubSuggest'),
]
# (workflow, tab button, sub-page bar)
WORKFLOWS = [('checks', 'NavChecks', None), ('elements', 'NavElements', 'ElementNav'), ('cases', 'NavCases', 'CaseNav')]
BY_KEY = dict((row[0], row) for row in FEATURES)
PANEL_INSTANCE_KEY = 'CASEGRAPH_WORKSPACE_INSTANCE'
