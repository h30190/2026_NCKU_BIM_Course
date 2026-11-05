# -*- coding: utf-8 -*-
"""Import and construct ShaftChecker in a fresh runtime without preloading WPF.

Only the pyRevit framework import loads the real engine-specific WPF assembly,
matching the installed pyRevit framework's initialization order. Revit is stubbed.
"""
import os
import sys
import types
import clr
from System import EventArgs


def module(name, **values):
    result = types.ModuleType(name)
    result.__dict__.update(values)
    sys.modules[name] = result
    return result


class Object(object):
    def __init__(self, **values):
        self.__dict__.update(values)


class Signal(object):
    def __iadd__(self, handler):
        return self

    def __isub__(self, handler):
        return self


# This reproduces the user's failure before loading any pyRevit UI framework.
try:
    import wpf
except ImportError as error:
    assert '_wpf' in str(error), str(error)
    print('PASS: reproduced No module named _wpf in a fresh runtime')
else:
    raise AssertionError('WPF was preloaded; this would mask the startup regression')

module('pyrevit', __path__=[], HOST_APP=Object(app=Object(DocumentChanged=Signal())), forms=Object())
module('pyrevit.coreutils', envvars=Object(get_pyrevit_env_var=lambda key: None))
db = module('Autodesk.Revit.DB', Events=Object(DocumentChangedEventArgs=EventArgs))
module('Autodesk')
module('Autodesk.Revit', DB=db)
module('casegraph.shaft_check', run_check=lambda uidoc: None,
       validate_report=lambda *args: None, validate_result=lambda *args: None)


class FrameworkLoader(object):
    """Defer the real WPF DLL load until production code requests the framework."""
    loaded = False

    def find_module(self, fullname, path=None):
        return self if fullname == 'pyrevit.framework' else None

    def load_module(self, fullname):
        for assembly in ('WindowsBase', 'PresentationCore', 'PresentationFramework', 'System.Xaml'):
            clr.AddReference(assembly)
        engine_dir = r'C:\Program Files\pyRevit-Master\bin\netfx\engines\IPY2712PR'
        clr.AddReferenceToFileAndPath(os.path.join(engine_dir, 'pyRevitLabs.IronPython.Wpf.dll'))
        import wpf
        self.loaded = True
        return module(fullname, wpf=wpf)


loader = FrameworkLoader()
sys.meta_path.insert(0, loader)
try:
    from casegraph.shaft_window import ShaftWindow
    assert loader.loaded
    window = ShaftWindow(None)
    assert window.Findings.Items.Count == 0 and not window.Record.IsEnabled
    assert window.Title == u'案例驅動 BIM 診斷與修正 · 管道間檢查'
    window.Close()
    print('PASS: production ShaftChecker imports and constructs its real XAML without preloaded WPF')
finally:
    sys.meta_path.remove(loader)
