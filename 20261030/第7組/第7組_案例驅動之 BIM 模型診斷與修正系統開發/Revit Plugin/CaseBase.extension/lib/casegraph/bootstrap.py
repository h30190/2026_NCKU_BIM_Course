# -*- coding: utf-8 -*-
"""Open the updated workspace from the existing ribbon without closing documents."""
from System import Guid
from Autodesk.Revit.UI import DockablePane, DockablePaneId
from pyrevit import forms
from pyrevit.coreutils import envvars
from casegraph import workspace_panel
from casegraph.service import ensure_started
from casegraph.navigation import PANEL_INSTANCE_KEY


def register():
    pane_id = DockablePaneId(Guid(workspace_panel.PANEL_ID))
    if not DockablePane.PaneIsRegistered(pane_id):
        workspace_panel.INSTANCE = forms.register_dockable_panel(workspace_panel.WorkspacePanel,default_visible=False)
        # Ribbon commands use separate IronPython engines. Keep the actual registered
        # WPF object in pyRevit's process-wide store instead of a module singleton.
        envvars.set_pyrevit_env_var(PANEL_INSTANCE_KEY, workspace_panel.INSTANCE)
    else:
        workspace_panel.INSTANCE = envvars.get_pyrevit_env_var(PANEL_INSTANCE_KEY)
    return pane_id


def open_panel(feature='home'):
    ensure_started()
    register()
    forms.open_dockable_panel(workspace_panel.PANEL_ID)
    instance = envvars.get_pyrevit_env_var(PANEL_INSTANCE_KEY)
    if instance is not None and hasattr(instance, 'open_feature'):
        instance.open_feature(feature)
    else:
        forms.alert(u'新版功能按鈕已安裝。請先儲存模型並重新啟動 Revit，讓已註冊的舊面板載入新版介面。', title=u'案例驅動 BIM 診斷與修正')
