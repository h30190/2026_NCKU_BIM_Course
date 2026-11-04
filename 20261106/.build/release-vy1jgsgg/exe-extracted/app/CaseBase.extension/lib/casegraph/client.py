# -*- coding: utf-8 -*-
"""HTTP transport with no WPF or Revit objects; safe for background workers."""
import json
from System import Uri
from System.Net import HttpWebRequest, WebException
from System.IO import StreamReader
from System.Text import Encoding
from casegraph.serialization import dumps


def request(base, path, payload=None, method=None):
    uri = Uri(base)
    if uri.Scheme != 'http' or uri.Host not in ('127.0.0.1','localhost'):
        raise ValueError(u'請使用本機服務位址，例如 http://127.0.0.1:8000。')
    req = HttpWebRequest.Create(base.rstrip('/')+'/graph'+path)
    req.Method = method or ('POST' if payload is not None else 'GET')
    req.Timeout = 65000 if path.endswith('/analyze') else 15000
    req.ReadWriteTimeout = req.Timeout
    if payload is not None:
        data = Encoding.UTF8.GetBytes(dumps(payload))
        req.ContentType = 'application/json; charset=utf-8'
        req.ContentLength = data.Length
        stream = req.GetRequestStream()
        try:
            stream.Write(data,0,data.Length)
        finally:
            stream.Close()
    try:
        response = req.GetResponse()
    except WebException as error:
        # Catch the .NET type: a generic `except Exception` wraps it without .Response,
        # which made every HTTP error (404/409/422) look like a lost connection.
        response = error.Response
        if response is not None:
            status = int(response.StatusCode)
            reader = StreamReader(response.GetResponseStream())
            try:
                body = reader.ReadToEnd()
                try:
                    detail = json.loads(body).get('detail',body)
                except Exception:
                    detail = body
            finally:
                reader.Close()
                response.Close()
            if status == 404 and detail == 'Not Found':
                raise ValueError(u'本機服務版本過舊，缺少此功能。請重新開啟「案例驅動 BIM 診斷與修正」面板，或執行 Start-CaseGraph.ps1 重新啟動服務。')
            raise ValueError(unicode(detail))
        raise ValueError(u'無法連線到本機服務。請稍後重試，或執行工作資料夾中的 Start-CaseGraph.ps1。')
    reader = StreamReader(response.GetResponseStream())
    try:
        return json.loads(reader.ReadToEnd())
    finally:
        reader.Close()
        response.Close()
