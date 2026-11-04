# -*- coding: utf-8 -*-
__persistentengine__ = True
from casegraph.bootstrap import register
from casegraph.workspace_panel import BUILD
import json
import os
from datetime import datetime
from pyrevit import HOST_APP
from pyrevit.coreutils import envvars
from casegraph.navigation import PANEL_INSTANCE_KEY

status = dict(revit_version=str(HOST_APP.version), time=datetime.utcnow().isoformat())
try:
    register()
    status['panel_registered'] = True
    instance = envvars.get_pyrevit_env_var(PANEL_INSTANCE_KEY)
    status['installed_build'] = BUILD
    status['build'] = getattr(instance, 'build', None)
    status['requires_restart'] = status['build'] != BUILD
except Exception as error:
    status.update(panel_registered=False, error=str(error))
    raise
finally:
    folder = os.path.join(os.path.dirname(__file__), '..', 'backend', 'data')
    if not os.path.isdir(folder):
        os.makedirs(folder)
    with open(os.path.join(folder, 'revit-startup.json'), 'w') as output:
        json.dump(status, output)
