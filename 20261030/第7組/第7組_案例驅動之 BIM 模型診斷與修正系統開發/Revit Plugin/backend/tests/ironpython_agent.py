# -*- coding: utf-8 -*-
"""Exercise the real .NET-to-CPython runner without a Claude call or Revit edits."""
import io
import json
import os
import tempfile
from System.Diagnostics import ProcessStartInfo, Stopwatch
from casegraph import agent
from casegraph.suggestion import quote

backend = os.path.dirname(os.path.dirname(__file__))
python = os.path.join(backend, '.venv', 'Scripts', 'python.exe')


def helper(code):
    descriptor, path = tempfile.mkstemp(suffix='.py', dir=os.path.join(backend, 'data'))
    os.close(descriptor)
    with io.open(path, 'w', encoding='utf-8') as stream:
        stream.write(code)
    return path


def test_unicode_and_large_stderr_do_not_block_stdout():
    path = helper(u'''import json, sys
sys.stdin.reconfigure(encoding='utf-8-sig')
sys.stdout.reconfigure(encoding='utf-8')
text = sys.stdin.read()
sys.stderr.write('x' * 200000)
print(json.dumps({'structured_output': {'summary': text}}, ensure_ascii=False))
''')
    try:
        runner = agent.AgentRunner('http://127.0.0.1:8000')
        value = runner.execute(ProcessStartInfo(python, quote(path)), u'門太窄，請加寬。', 'Claude')
        assert value['summary'] == u'門太窄，請加寬。'
        assert runner.process is None
    finally:
        os.remove(path)


def test_timeout_is_reached_while_child_has_no_output():
    path = helper(u'import sys, time\nsys.stdin.read()\ntime.sleep(30)\n')
    previous = agent.TIMEOUT_MS
    agent.TIMEOUT_MS = 1200
    watch = Stopwatch.StartNew()
    try:
        runner = agent.AgentRunner('http://127.0.0.1:8000')
        try:
            runner.execute(ProcessStartInfo(python, quote(path)), '', 'Claude')
            assert False, 'Timeout was ignored'
        except ValueError as error:
            assert u'逾時' in unicode(error)
        assert watch.ElapsedMilliseconds < 15000
        assert runner.process is None
    finally:
        agent.TIMEOUT_MS = previous
        os.remove(path)


def test_real_helper_errors_use_claude_label_and_cancel_prevents_start():
    runner = agent.AgentRunner('http://127.0.0.1:8000')
    try:
        runner.run_claude('invalid')
        assert False, 'Invalid operation should fail'
    except ValueError as error:
        assert u'Claude 執行失敗' in unicode(error) and 'Unknown operation' in unicode(error)
    runner.cancel()
    try:
        runner.run_claude('invalid')
        assert False, 'Cancelled runner should not start'
    except ValueError as error:
        assert u'已停止' in unicode(error)


def test_other_providers_are_rejected_without_starting_them():
    for provider in ('codex', 'unknown', None):
        try:
            agent.AgentRunner('http://127.0.0.1:8000', provider=provider)
            assert False, 'Non-Claude providers must not run'
        except ValueError as error:
            assert u'僅使用 Claude' in unicode(error)


for name, test in sorted((k, v) for k, v in globals().items() if k.startswith('test_')):
    test()
    print('PASS ' + name)
print('4 real IronPython process runner scenarios passed.')
