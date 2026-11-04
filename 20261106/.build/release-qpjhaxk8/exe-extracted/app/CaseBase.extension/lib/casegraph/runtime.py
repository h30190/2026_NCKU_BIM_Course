# -*- coding: utf-8 -*-
"""Locate the packaged CPython runtime or a developer's virtual environment."""
import os


def python_executable(root, windowless=False):
    name = 'pythonw.exe' if windowless else 'python.exe'
    candidates = (os.path.join(root, 'runtime', 'python', name),
                  os.path.join(root, 'backend', '.venv', 'Scripts', name))
    for path in candidates:
        if os.path.isfile(path):
            return path
    raise ValueError(u'找不到 Python 執行環境；請重新執行安裝程式或參閱 README.md。')
