# -*- coding: utf-8 -*-
"""Compatibility entry point for the CaseGraph dockable workflow."""
__persistentengine__ = True
from casegraph import bootstrap
bootstrap.open_panel('issue')
