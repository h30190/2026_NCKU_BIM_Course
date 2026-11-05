"""NLSC classification-code suggestions; never assigns elevations from lineweight."""
import re
def nlsc_defaults(layers):
    def match(code):
        candidates=[g for g in layers if re.fullmatch(code+r'(?:[_\s].*)?',g['name']) and g.get('curve_count',0)>0]
        return candidates[0]['name'] if len(candidates)==1 else None
    index=match('9810101');intermediate=match('9810102')
    if not index and not intermediate:return None
    return {'contour_layer':index or intermediate,'contour_layer_secondary':intermediate if index else None,
            'drawing_unit_m':1,'terrain_mode':'cad_z','contour_simplify_m':1,
            'building_layer':match('9310100'),
            'note':'已套用國土981編碼建議：公尺、粗細等高線一起讀原始Z。請確認尺度；1m簡化是本工具起始值，不是官方精度。間距隨圖資而異，未重設任何高程。'}
