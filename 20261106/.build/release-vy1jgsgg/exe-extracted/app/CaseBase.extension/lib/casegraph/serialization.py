# -*- coding: utf-8 -*-
"""Keep .NET text as Unicode until the HTTP transport encodes it as UTF-8.

IronPython 2.7.12's accelerated ASCII JSON encoder interprets characters such as
U+00B3 (the exponent in m3) as UTF-8 bytes and raises UnicodeDecodeError. The
Unicode encoder avoids that path and also preserves Chinese parameter names.
"""
import json


def dumps(value, **options):
    options['ensure_ascii'] = False
    return json.dumps(value, **options)
