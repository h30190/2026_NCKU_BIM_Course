# -*- coding: utf-8 -*-
"""Status colours for list rows; frozen brushes are safe to share."""
from System.Windows.Media import BrushConverter

CHECK_ACCENTS = dict(OVERLAP='#C0392B', SUSPECT='#B76E00', WARNING='#B76E00', UNKNOWN='#7A8A99')
# Case lifecycle: (background, foreground) for the status chip.
CASE_CHIPS = dict(DRAFT=('#EEF1F4', '#52677A'), REPORTED=('#E3EEFA', '#1F5F99'), ANALYZED=('#E3EEFA', '#1F5F99'),
                  UNDER_REVIEW=('#FFF3DC', '#8A5A00'), RESOLVED=('#E2F2F1', '#176A7A'),
                  VERIFIED=('#E3F4E6', '#22703A'), ARCHIVED=('#EEF1F4', '#687888'),
                  UNSOLVED=('#FDECEA', '#B03A2E'))
_converter, _cache = BrushConverter(), {}


def brush(color):
    if color not in _cache:
        value = _converter.ConvertFromString(color)
        value.Freeze()
        _cache[color] = value
    return _cache[color]


def case_chip(status):
    back, fore = CASE_CHIPS.get(status, CASE_CHIPS['DRAFT'])
    return brush(back), brush(fore)
