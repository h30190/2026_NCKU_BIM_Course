# -*- coding: utf-8 -*-
"""Run the Claude panel worker through stdio MCP. Call off the UI thread."""
import os
from System.Diagnostics import Process, ProcessStartInfo
from System.Text import Encoding
from casegraph.serialization import dumps
from casegraph.suggestion import parse_output, quote
from casegraph.runtime import python_executable

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
TIMEOUT_MS = 8*60*1000  # Planning may retry once when the first answer has no executable steps.


class AgentRunner(object):
    def __init__(self, base_url, provider='claude'):
        if provider != 'claude':
            raise ValueError(u'此面板僅使用 Claude；請重新載入新版面板。')
        self.base_url, self.process, self.provider = base_url, None, provider
        self.cancelled = False

    def run_claude(self, operation, **payload):
        backend = os.path.join(ROOT, 'backend')
        python = python_executable(ROOT)
        info = ProcessStartInfo(python, quote(os.path.join(backend, 'claude_agent.py')))
        return self.execute(info, dumps(dict(payload, operation=operation, base_url=self.base_url)), 'Claude')

    def execute(self, info, prompt, label):
        if self.cancelled:
            raise ValueError(u'已停止。')
        info.UseShellExecute, info.CreateNoWindow = False, True
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = True
        info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8
        info.WorkingDirectory = ROOT
        process = self.process = Process.Start(info)
        try:
            if self.cancelled:
                self.kill_tree(process)
                raise ValueError(u'已停止。')
            # .NET Framework cannot set the stdin encoding; write UTF-8 bytes directly.
            data = Encoding.UTF8.GetBytes(prompt)
            process.StandardInput.BaseStream.Write(data, 0, data.Length)
            process.StandardInput.Close()
            errors = process.StandardError.ReadToEndAsync()
            output = process.StandardOutput.ReadToEndAsync()
            if not process.WaitForExit(TIMEOUT_MS):
                self.kill_tree(process)
                raise ValueError(label + u' 執行逾時，已停止。')
            if self.cancelled:
                raise ValueError(u'已停止。')
            if not output.Result.strip():
                raise ValueError(label + u' 沒有輸出：' + (errors.Result or u'')[-400:])
            if process.ExitCode != 0:
                # Prefer the helper's structured error when available.
                parse_output(output.Result, label)
                raise ValueError(label + u' 執行失敗：' + (errors.Result or u'')[-400:])
            return parse_output(output.Result, label)
        finally:
            self.process = None
            process.Dispose()

    @staticmethod
    def kill_tree(process):
        if process.HasExited:
            return
        info = ProcessStartInfo(os.path.join(os.environ.get('WINDIR', 'C:\\Windows'), 'System32', 'taskkill.exe'),
                                '/PID {} /T /F'.format(process.Id))
        info.UseShellExecute, info.CreateNoWindow = False, True
        info.RedirectStandardOutput = info.RedirectStandardError = True
        killer = Process.Start(info)
        try:
            if not killer.WaitForExit(10000):
                killer.Kill()
        finally:
            killer.Dispose()

    def cancel(self):
        self.cancelled = True
        process, self.process = self.process, None
        if process is not None and not process.HasExited:
            self.kill_tree(process)

    def plan(self, target, solutions, instruction, mode='cases', context=None, members=None):
        return self.run_claude('plan', target=target, solutions=solutions, instruction=instruction,
                              mode=mode, context=context, members=members or [])

    def apply(self, steps, scope=None):
        return self.run_claude('apply', steps=steps, scope=scope)
