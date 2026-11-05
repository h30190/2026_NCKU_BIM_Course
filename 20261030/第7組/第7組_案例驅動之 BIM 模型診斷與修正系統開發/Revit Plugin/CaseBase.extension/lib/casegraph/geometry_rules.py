# -*- coding: utf-8 -*-
"""Project-scoped rule definitions and presentation-only violation overlays."""
from copy import deepcopy
import hashlib
import io
import json
import math
import os
import uuid
from casegraph.serialization import dumps

try:
    string_types = (basestring,)
except NameError:
    string_types = (str,)

SCOPES = [('type', u'同類型元素'), ('category', u'同類別元素'), ('instance', u'僅此元素')]
TARGETS = [('OST_Walls', u'牆（Walls）'), ('OST_Floors', u'樓板（Floors）'),
           ('OST_Ceilings', u'天花（Ceilings）'), ('OST_StructuralColumns', u'結構柱'),
           ('OST_StructuralFraming', u'結構梁'), ('OST_GenericModel', u'一般模型'),
           ('OST_Furniture', u'家具'), ('OST_MechanicalEquipment', u'機械設備')]
DEFAULT_DIRECTORY = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..', 'backend', 'data', 'geometry-rules'))


def validate_rule(rule):
    if rule.get('scope') not in dict(SCOPES) or rule.get('target_category') not in dict(TARGETS):
        raise ValueError(u'規則的元素範圍或目標類別無效。')
    required = ['id', 'name', 'document', 'source_category_id', 'source_key']
    if any(not isinstance(rule.get(key), string_types) or not rule[key] for key in required):
        raise ValueError(u'規則缺少來源元素識別資料。')
    tolerance = rule.get('tolerance_mm3')
    if isinstance(tolerance, bool) or not isinstance(tolerance, (int, float)) or math.isnan(tolerance) or math.isinf(tolerance) or tolerance < 0:
        raise ValueError(u'交集體積容差須為大於或等於 0 的有限數值。')
    if not isinstance(rule.get('exclude_host'), bool):
        raise ValueError(u'排除宿主設定無效。')
    return rule


def make_rule(target, scope, target_category, tolerance_mm3=0., exclude_host=False):
    if not target or target['node_type'] != 'ELEMENT' or target['properties'].get('simulation'):
        raise ValueError(u'此資料沒有可檢查的模型實體；請先讀取實際 Revit 元素。')
    p = target['properties']
    source = {'instance': target.get('source_element_id'), 'type': p.get('type_id'), 'category': p.get('category_id')}.get(scope)
    if not source or source == '-1' or not p.get('category_id'):
        raise ValueError(u'此元素缺少類型或類別資料，請重新讀取，或改用「僅此元素」。')
    parts = [target['source_document'], scope, p['category_id'], source, target_category]
    rule_id = hashlib.sha256(u'|'.join(parts).encode('utf-8')).hexdigest()[:24]
    name = (p.get('type_name') if scope == 'type' else p.get('category') if scope == 'category' else target['name']) or source
    return validate_rule(dict(id=rule_id, document=target['source_document'],
        name=u'{} · {} 不可與{}相交'.format(dict(SCOPES)[scope], name, dict(TARGETS)[target_category]),
        scope=scope, source_category_id=p['category_id'], source_key=source,
        target_category=target_category, tolerance_mm3=float(tolerance_mm3), exclude_host=bool(exclude_host)))


class RuleStore(object):
    def __init__(self, directory=None):
        self.directory = directory or os.environ.get('CASEGRAPH_RULES_DIR') or DEFAULT_DIRECTORY

    def path(self, document):
        return os.path.join(self.directory, hashlib.sha256(document.encode('utf-8')).hexdigest()+'.json')

    def load(self, document):
        path = self.path(document)
        if not os.path.isfile(path):
            return []
        with io.open(path, 'r', encoding='utf-8') as stream:
            data = json.load(stream)
        if data.get('version') != 1 or data.get('document') != document or not isinstance(data.get('rules'), list):
            raise ValueError(u'規則檔格式或來源文件不符；原檔已保留。')
        rules = [validate_rule(rule) for rule in data['rules']]
        if len(rules) > 50 or len(set(r['id'] for r in rules)) != len(rules) or any(r['document'] != document for r in rules):
            raise ValueError(u'規則檔含重複或不屬於此文件的規則；原檔已保留。')
        return rules

    def save(self, document, rules):
        if len(rules) > 50:
            raise ValueError(u'每個文件最多保存 50 條規則。')
        if len(set(rule.get('id') for rule in rules)) != len(rules):
            raise ValueError(u'不可儲存重複規則。')
        for rule in rules:
            validate_rule(rule)
            if rule['document'] != document:
                raise ValueError(u'不可將其他文件的規則寫入目前文件。')
        # Refuse to overwrite a corrupt file, and replace atomically after serialization.
        self.load(document)
        if not os.path.isdir(self.directory):
            os.makedirs(self.directory)
        path = self.path(document)
        temporary = path+'.'+uuid.uuid4().hex+'.tmp'
        try:
            with io.open(temporary, 'w', encoding='utf-8') as stream:
                stream.write(u'{}'.format(dumps(dict(version=1, document=document, rules=rules), indent=2)))
            if hasattr(os, 'replace'):
                os.replace(temporary, path)
            else:  # IronPython 2.7 on Windows.
                from System.IO import File
                if File.Exists(path):
                    # Both files inherit the same directory permissions. Do not
                    # require ACL/metadata merge privileges merely to update JSON.
                    File.Replace(temporary, path, path+'.previous', True)
                else:
                    File.Move(temporary, path)
        finally:
            if os.path.isfile(temporary):
                os.remove(temporary)


def with_rule_overlay(graph, report, limit=240):
    """Never write these RULE_VIOLATION edges into saved model snapshots."""
    if not report or report.get('stale') or report.get('graph_root') != graph['root']:
        return graph
    result = deepcopy(graph)
    nodes = dict((n['node_id'], n) for n in result['nodes'])
    omitted = 0
    for row in report['results']:
        if row['status'] != 'VIOLATION':
            continue
        pair = [row['source'], row['target']]
        missing = [n for n in pair if n['node_id'] not in nodes]
        if len(nodes)+len(missing) > limit:
            omitted += 1
            continue
        for node in pair:
            if node['node_id'] not in nodes:
                nodes[node['node_id']] = deepcopy(node)
                result['nodes'].append(nodes[node['node_id']])
            nodes[node['node_id']]['properties']['_rule_violation'] = True
        result['edges'].append(dict(source=pair[0]['node_id'], target=pair[1]['node_id'],
            relationship_type='RULE_VIOLATION', validation_status='verified',
            evidence=row['message'], properties=dict(rule_name=report['rule']['name'], rule_id=report['rule']['id'],
                overlap_mm3=row['volume_mm3'], view_name=report['view_name'], checked_at=report['checked_at'])))
    if omitted:
        result.setdefault('warnings', []).append(u'圖上省略 {} 組違規以控制節點數，完整結果請看「相交規則」清單。'.format(omitted))
    return result
