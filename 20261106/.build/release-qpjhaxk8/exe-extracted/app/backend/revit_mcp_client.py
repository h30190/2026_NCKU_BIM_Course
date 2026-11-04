"""Small stdio MCP client for the panel's exact, approved Revit commands.

All Revit access goes through the existing MCP server, never its raw WebSocket.
"""
import json
import os
import queue
import subprocess
import threading
import time
from collections import deque


CREATE_NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)


def stop_process(process):
    if process.poll() is not None:
        return
    if os.name == 'nt':
        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       creationflags=CREATE_NO_WINDOW, timeout=10)
    else:
        process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


class McpClient:
    def __init__(self, server, timeout=45):
        self.server, self.timeout = server, timeout
        self.process, self.ident = None, 0
        self.messages, self.errors = queue.Queue(), deque(maxlen=20)

    def __enter__(self):
        self.process = subprocess.Popen(
            [self.server['command']] + self.server.get('args', []),
            cwd=self.server.get('cwd'), env=dict(os.environ, **self.server.get('env', {})),
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            encoding='utf-8', errors='replace', creationflags=CREATE_NO_WINDOW)

        def read_messages():
            try:
                for line in self.process.stdout:
                    try:
                        self.messages.put(json.loads(line))
                    except ValueError:
                        self.messages.put(ValueError('MCP server returned invalid JSON.'))
            finally:
                self.messages.put(EOFError('MCP server closed its output.'))

        def read_errors():
            for line in self.process.stderr:
                self.errors.append(line[-500:])

        threading.Thread(target=read_messages, daemon=True).start()
        threading.Thread(target=read_errors, daemon=True).start()
        try:
            self.request('initialize', dict(protocolVersion='2025-06-18', capabilities={},
                                           clientInfo=dict(name='Claude-CaseGraph', version='1.0.0')))
            self.send(dict(jsonrpc='2.0', method='notifications/initialized'))
        except Exception:
            self.__exit__(None, None, None)
            raise
        return self

    def send(self, message):
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + '\n')
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.ident += 1
        ident = self.ident
        self.send(dict(jsonrpc='2.0', id=ident, method=method, params=params or {}))
        deadline = time.monotonic() + self.timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError('MCP request timed out; do not retry a model change automatically.')
            try:
                message = self.messages.get(timeout=remaining)
            except queue.Empty:
                raise TimeoutError('MCP request timed out; its outcome is unknown. Check Revit before retrying.')
            if isinstance(message, Exception):
                raise message
            if 'method' in message:
                if 'id' in message:
                    response = dict(jsonrpc='2.0', id=message['id'])
                    if message['method'] == 'ping':
                        response['result'] = {}
                    else:
                        response['error'] = dict(code=-32601, message='Client method not supported')
                    self.send(response)
                continue
            if message.get('id') != ident:
                continue
            if 'error' in message:
                raise ValueError('MCP: ' + str(message['error'].get('message', message['error'])))
            return message['result']

    def list_tools(self):
        tools, cursor, seen = [], None, set()
        while True:
            page = self.request('tools/list', dict(cursor=cursor) if cursor else {})
            tools.extend(page['tools'])
            cursor = page.get('nextCursor')
            if not cursor:
                return tools
            if cursor in seen:
                raise ValueError('MCP returned a repeated tools cursor.')
            seen.add(cursor)

    def call(self, name, arguments):
        result = self.request('tools/call', dict(name=name, arguments=arguments))
        text = '\n'.join(item.get('text', '') for item in result.get('content', []) if item.get('type') == 'text')
        if result.get('isError'):
            raise ValueError(text or 'MCP tool failed.')
        try:
            value = json.loads(text)
        except ValueError:
            value = None
        if isinstance(value, dict) and (value.get('success') is False or value.get('Success') is False):
            raise ValueError(str(value.get('error') or value.get('Error') or text))
        return text or 'MCP tool completed.'

    def __exit__(self, *args):
        if self.process is not None:
            if self.process.poll() is None:
                try:
                    self.process.stdin.close()
                    self.process.wait(timeout=2)
                except (OSError, subprocess.TimeoutExpired):
                    pass
            stop_process(self.process)
            for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                stream.close()
