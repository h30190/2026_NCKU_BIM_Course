# -*- coding: utf-8 -*-
"""Run with pyRevit's IronPython 2.7 runtime, not CPython.

With CASEGRAPH_TEST_URL set, the real plugin HTTP client also creates, reads and
revises a draft against an isolated test backend. Never target production data.
"""
import json
import os
from System.Text import Encoding
from casegraph.serialization import dumps


values = [u'0.00m\u00b3', u'12.50 m\u00b2', u'90\u00b0', u'\u00b1 3 mm',
          u'\u5ba2\u5ef3_\u6c99\u767c', u'\u4e2d\u6587 0.00m\u00b3', u'quote " slash \\ newline\n']
parameters = {u'\u9ad4\u7a4d': {'value': values[0]}, u'\u9762\u7a4d': {'value': values[1]}}
draft = dict(title=u'\u9023\u63a5\u554f\u984c m\u00b3', description=u'\u4e2d\u6587\u53c3\u6578 ' + u' / '.join(values),
             issue_type='CONNECTION_ERROR', notes=u'\u50b3\u8f38\u5f8c\u4e0d\u53ef\u907a\u5931\u7b26\u865f',
             graph=dict(root='test:door', nodes=[dict(node_id='test:door', name=values[4], node_type='ELEMENT',
                 source_document='encoding-test',source_element_id='door',properties=dict(category='Doors',parameters=parameters))]))

try:
    json.dumps({'value': values[0]})
except UnicodeError:
    print('Reproduced original superscript-unit JSON encoding failure')
else:
    raise AssertionError('Expected the original IronPython encoder to fail')

for value in values + [parameters, draft]:
    text = dumps(value)
    wire = Encoding.UTF8.GetBytes(text)
    restored = json.loads(Encoding.UTF8.GetString(wire))
    assert restored == value, repr(value)
    assert json.loads(dumps(value,indent=2)) == value
print('PASS: units, Unicode keys, Chinese text, escapes and nested draft UTF-8 round trips')


def check_http_errors():
    """HTTP errors keep the server's message; only a refused connection reads as offline."""
    import threading
    from System import Array, Byte
    from System.Net import IPAddress
    from System.Net.Sockets import TcpListener
    from casegraph.client import request
    replies = {'/graph/missing': (404, '{"detail": "Not Found"}'),
               '/graph/stale': (409, u'{"detail": "Only the current draft revision can be edited"}'),
               '/graph/broken': (500, 'Internal Server Error'),
               '/graph/invalid': (422, dumps(dict(detail=[dict(loc=['body', 'graph'],
                   msg='Value error, Root must be an element with a persistent UniqueId',
                   input=dict(nodes=[u'private-model-data'] * 1000))])))}
    listener = TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    port = listener.LocalEndpoint.Port

    def serve():
        for _ in range(len(replies)):
            client = listener.AcceptTcpClient()
            stream = client.GetStream()
            buffer = Array.CreateInstance(Byte, 4096)
            data = Encoding.ASCII.GetString(buffer, 0, stream.Read(buffer, 0, 4096))
            status, body = replies[data.split(' ')[1]]
            payload = Encoding.UTF8.GetBytes(body)
            head = Encoding.ASCII.GetBytes('HTTP/1.1 {} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n'.format(status, payload.Length))
            stream.Write(head, 0, head.Length)
            stream.Write(payload, 0, payload.Length)
            client.Close()
    thread = threading.Thread(target=serve)
    thread.daemon = True
    thread.start()
    base = 'http://127.0.0.1:{}'.format(port)
    messages = []
    for path in ('/missing', '/stale', '/broken', '/invalid'):
        try:
            request(base, path)
            raise AssertionError('HTTP error not raised for ' + path)
        except ValueError as error:
            messages.append(unicode(error))
    listener.Stop()
    assert u'版本過舊' in messages[0], messages[0]
    assert messages[1] == u'Only the current draft revision can be edited', messages[1]
    assert messages[2] == u'Internal Server Error', messages[2]
    assert u'模型資料' in messages[3] and 'UniqueId' in messages[3], messages[3]
    assert 'private-model-data' not in messages[3] and len(messages[3]) < 150
    try:
        request('http://127.0.0.1:{}'.format(port), '/missing')
        raise AssertionError('Closed port did not fail')
    except ValueError as error:
        assert u'無法連線' in unicode(error), unicode(error)
    print('PASS: HTTP 404/409/500 retain messages; 422 omits model input; a closed port reads as offline')


check_http_errors()


def check_revit_adapter_generators():
    """Run the real Revit-side generators under pyRevit's engine options.

    With Tracing + FullFrames, a list comprehension inside a generator raised
    "Unable to cast ... FieldExpression to ... BlockExpression" in Revit.
    """
    from System.Collections.Generic import List
    from casegraph.model_checks import RevitCheckAdapter

    class Obj(object):
        def __init__(self, **values):
            self.__dict__.update(values)

    class Collector(object):
        def __init__(self, doc, view=None):
            self.items = list(doc.items)
        def WhereElementIsNotElementType(self):
            return self
        def WherePasses(self, test):
            self.items = [e for e in self.items if test(e)]
            return self
        def OfCategory(self, category):
            return self
        def __iter__(self):
            return iter(self.items)
        def Dispose(self):
            pass

    def element(key):
        return Obj(Id=key, UniqueId=key, Category=Obj(CategoryType=1))
    items = [element('a'), element('b'), element('c')]
    point = lambda x, y, z: Obj(X=x, Y=y, Z=z)
    box = Obj(Min=point(0, 0, 0), Max=point(1, 1, 1), Transform=Obj(OfPoint=lambda p: p))
    warning = Obj(GetFailingElements=lambda: ['a'], GetAdditionalElements=lambda: ['gone'], GetDescriptionText=lambda: u'重疊', Dispose=lambda: None)
    doc = Obj(items=items, GetElement=lambda key: next((e for e in items if e.UniqueId == key), None), GetWarnings=lambda: [warning])
    adapter = object.__new__(RevitCheckAdapter)
    adapter.DB = Obj(FilteredElementCollector=Collector, CategoryType=Obj(Model=1), RevitLinkInstance=type('Link', (object,), {}),
                     ElementId=object, ElementIdSetFilter=lambda ids: (lambda e: e.Id in list(ids)), XYZ=point,
                     Outline=lambda low, high: (low, high), BoundingBoxIntersectsFilter=lambda outline: (lambda e: True),
                     BuiltInCategory=Obj(OST_Walls=1))
    adapter.doc, adapter.view, adapter.owners = doc, Obj(Id=7), []
    assert [e.Id for e in adapter.sources(dict(scope='selection', keys=['a', 'c', 'gone']))] == ['a', 'c']
    assert list(adapter.sources(dict(scope='selection', keys=['gone']))) == []
    assert list(adapter.warnings()) == [dict(keys=['a'], message=u'重疊')]
    source = Obj(get_BoundingBox=lambda view: box)
    assert len(list(adapter.candidates(source, dict(target_category='OST_Walls')))) == 3
    print('PASS: Revit adapter generators (selection sources, warnings, rule candidates) under pyRevit engine options')


check_revit_adapter_generators()

url = os.getenv('CASEGRAPH_TEST_URL')
if url:
    from casegraph.client import request
    created = request(url,'/cases',draft)
    assert created['status'] == 'DRAFT'
    case_id = created['id']
    restored = request(url,'/cases/{}'.format(case_id))['draft']
    assert restored['title'] == draft['title']
    assert restored['description'] == draft['description']
    assert restored['notes'] == draft['notes']
    assert restored['graph']['nodes'][0]['properties']['parameters'] == parameters
    draft['description'] = values[-1] + u' 3m\u00b3'
    saved = request(url,'/cases/{}?revision=1'.format(case_id),draft,'PUT')
    assert saved['revision'] == 2
    assert saved['draft']['description'] == draft['description']
    assert request(url,'/cases/{}/revisions/1'.format(case_id))['description'] != draft['description']
    print('PASS: actual IronPython HTTP client draft save, readback and revision history')
