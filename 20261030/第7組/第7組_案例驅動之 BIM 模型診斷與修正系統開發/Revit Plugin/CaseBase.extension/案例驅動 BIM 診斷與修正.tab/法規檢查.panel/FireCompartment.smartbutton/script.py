# -*- coding: utf-8 -*-
"""Disabled ribbon placeholder; intentionally has no executable action."""


def __selfinit__(script_cmp, ui_button_cmp, revit):
    # Disable the actual Revit RibbonItem while keeping the icon visible.
    ui_button_cmp.enabled = False
    return True
