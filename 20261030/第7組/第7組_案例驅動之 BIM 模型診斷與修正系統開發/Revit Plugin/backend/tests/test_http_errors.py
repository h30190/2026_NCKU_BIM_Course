import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'CaseBase.extension/lib'))
from casegraph.serialization import error_text


def test_validation_errors_show_reason_without_echoing_the_submitted_model():
    detail = [dict(loc=['body', 'graph'], msg='Value error, Root must be an element with a persistent UniqueId',
                   input={'nodes': ['private model data'] * 10000}, ctx={'error': 'private context'})]
    text = error_text(detail, 422)
    assert '模型資料' in text and 'UniqueId' in text and len(text) < 150
    assert 'private' not in text and 'nodes' not in text


def test_multiple_errors_and_long_server_messages_are_bounded():
    detail = [dict(loc=['body', 'title'], msg='String should have at least 1 character')] * 7
    text = error_text(detail, 422)
    assert text.count('標題') == 1 and '另有 2' in text
    assert len(error_text('x' * 100000, 500)) == 1200
    assert error_text('Stale revision', 409) == 'Stale revision'
    assert 'secret' not in error_text({'input': 'secret'}, 422)
