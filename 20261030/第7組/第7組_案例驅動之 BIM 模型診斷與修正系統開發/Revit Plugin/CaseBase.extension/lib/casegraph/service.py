# -*- coding: utf-8 -*-
"""Start the separately installed CPython service when the panel is opened.

An already-running service from an older version is replaced: otherwise new pages
call endpoints it does not have, and new fields are silently dropped.
"""
import json
import os
from System.Diagnostics import Process, ProcessStartInfo
from System.IO import StreamReader
from System.Net import HttpWebRequest
from System.Threading import Thread
from casegraph.runtime import python_executable

REQUIRED_API = 7
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))


def health():
    """The service's health payload, or None when no CaseGraph service answers."""
    request = HttpWebRequest.Create('http://127.0.0.1:8000/graph/health')
    request.Timeout = 1000
    response = None
    try:
        response = request.GetResponse()
        reader = StreamReader(response.GetResponseStream())
        try:
            status = json.loads(reader.ReadToEnd())
            return status if status.get('service') == 'casegraph' else None
        finally:
            reader.Close()
    except Exception:
        return None
    finally:
        if response:
            response.Close()


def is_running():
    status = health()
    return bool(status and status.get('api_version', 0) >= REQUIRED_API)


def stop_outdated(status):
    pid = status.get('pid')
    if pid is None:  # Services before API 4 do not report their process ID.
        path = os.path.join(ROOT, 'backend', 'data', 'server.pid')
        if not os.path.isfile(path):
            raise ValueError(u'本機診斷與修正服務版本過舊，且找不到其程序編號；請結束舊服務後重新開啟面板。')
        with open(path) as stream:
            pid = int(stream.read().strip())
    try:
        process = Process.GetProcessById(int(pid))
    except Exception:
        return  # Already gone.
    if process.ProcessName.lower() not in ('python', 'pythonw'):
        raise ValueError(u'本機診斷與修正服務版本過舊，但程序 {} 不是 Python；請手動結束舊服務。'.format(pid))
    process.Kill()
    process.WaitForExit(5000)


def ensure_started():
    status = health()
    if status and status.get('api_version', 0) >= REQUIRED_API:
        return
    python = python_executable(ROOT, windowless=True)
    runner = os.path.join(ROOT, 'backend', 'run_backend.py')
    if status:
        stop_outdated(status)
    info = ProcessStartInfo()
    info.FileName = python
    info.Arguments = '"' + runner + '"'
    info.WorkingDirectory = ROOT
    info.UseShellExecute = False
    info.CreateNoWindow = True
    Process.Start(info)
    # Wait briefly so the panel's first requests reach the new service.
    for _ in range(40):
        if is_running():
            return
        Thread.Sleep(250)
