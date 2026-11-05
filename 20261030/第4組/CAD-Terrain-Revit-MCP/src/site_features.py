"""Explicit building-height provenance and road surface preparation."""
import math,re
from collections import Counter
from shapely.geometry import Point,Polygon,LineString,MultiPoint
from shapely.ops import unary_union,polygonize_full,triangulate
from shapely.strtree import STRtree

def height_label(text,mode,unit,storey):
    text=text.strip()
    if mode=='floors':
        match=re.fullmatch(r'(\d{1,2})\s*(?:F|層|层|樓|楼)?',text,re.I)
        return (int(match[1])*storey,'樓層數 × 假設層高') if match and 1<=int(match[1])<=99 else None
    match=re.fullmatch(r'(?:(?:H|HEIGHT|高度|屋高)\s*[:=：]?\s*)?([+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(m|公尺|米|mm|毫米|cm|公分)?',text,re.I)
    if not match:return None
    suffix=(match[2] or '').lower();factor={'m':1,'米':1,'公尺':1,'mm':.001,'毫米':.001,'cm':.01,'公分':.01}.get(suffix,unit)
    val=float(match[1])*factor
    return (val,'CAD 屋高文字') if .5<=val<=500 else None

def apply_heights(buildings,entities,cfg,ox,oy,z0):
    mode=cfg.get('building_height_mode','assumed');missing=cfg.get('missing_height_policy','assume')
    if mode not in ('assumed','height_text','floors','roof_z') or missing not in ('assume','skip'):raise ValueError('房屋高度模式不正確。')
    scale=cfg['drawing_unit_m'];unit=cfg.get('building_height_unit_m',1);storey=cfg.get('storey_height_m',3)
    if not all(isinstance(x,(int,float)) and not isinstance(x,bool) and math.isfinite(x) and 0<x<=100 for x in (unit,storey)):raise ValueError('房屋高度單位或層高不正確。')
    layer=cfg.get('building_height_layer');labels=[]
    if layer and layer not in {e['layer'] for e in entities}:raise ValueError('找不到房屋高度圖層。')
    if mode in ('height_text','floors'):
        if not layer:raise ValueError('請選房屋高度／樓層文字圖層。')
        for e in entities:
            if e['layer']==layer and e['type'] in ('TEXT','MTEXT') and e['points']:
                parsed=height_label(e['text'],mode,unit,storey)
                if parsed:labels.append((Point(e['points'][0][0]*scale-ox,e['points'][0][1]*scale-oy),parsed,e['handle'],e['text']))
    polygons=[Polygon(b['points']) for b in buildings]
    # A label touching multiple building boundaries is not automatically assigned.
    matches={i:[] for i in range(len(buildings))};ambiguous_labels=0
    for pt,value,handle,text in labels:
        covered=[i for i,poly in enumerate(polygons) if poly.covers(pt)]
        if len(covered)==1:matches[covered[0]].append((value,handle,text))
        elif len(covered)>1:ambiguous_labels+=1
    kept=[];issues=[];counts=Counter()
    for i,b in enumerate(buildings):
        top_ground=b['base_z']+b['height']-b['assumed_height_above_highest_edge_m']
        value=None;source=None;reason='沒有可唯一配對的高度';evidence=[]
        if mode in ('height_text','floors'):
            candidates=matches[i];unique={round(x[0][0],4) for x in candidates}
            if len(unique)==1:
                value=candidates[0][0][0];source=candidates[0][0][1];evidence=[{'handle':x[1],'text':x[2]} for x in candidates]
            elif len(unique)>1:reason='同一輪廓內高度文字互相衝突'
        elif mode=='roof_z':
            # Only an explicitly selected roof-elevation layer is interpreted as roof Z.
            roof_layer=layer or cfg.get('building_layer')
            values=[p[2]*scale-z0 for e in entities if e['layer']==roof_layer and e['type'] in ('LWPOLYLINE','POLYLINE','LINE','POINT') for p in e['points'] if polygons[i].buffer(.015).covers(Point(p[0]*scale-ox,p[1]*scale-oy))]
            if values and max(values)-min(values)<.01 and .5<=min(values)-top_ground<=500:
                value=min(values)-top_ground;source='CAD 屋頂絕對 Z（扣除估計地面）';evidence=[{'roof_elevation_m':min(values)+z0}]
            else:reason='屋頂Z不足、衝突，或不在估計地面上方'
        if value is None:
            if mode!='assumed':issues.append({'building_id':b['id'],'reason':reason})
            if missing=='skip' and mode!='assumed':counts['skipped']+=1;continue
            value=cfg['default_building_height_m'];source='假設高度';counts['assumed']+=1
        else:counts['from_cad']+=1
        b['height']=top_ground+value-b['base_z'];b['height_above_ground_m']=value
        b['height_source']=source;b['height_evidence']=evidence;b['height_is_assumed']=source=='假設高度'
        # Deprecated field retained for backward-compatible readers; new readers use provenance above.
        b['assumed_height_above_highest_edge_m']=value
        kept.append(b)
    return kept,{'height_from_cad':counts['from_cad'],'height_assumed':counts['assumed'],'height_skipped':counts['skipped'],'ambiguous_height_labels':ambiguous_labels,'building_height_mode':mode},issues

def road_surfaces(entities,cfg,terrain,ox,oy):
    layers=cfg.get('road_layers') or [];mode=cfg.get('road_mode','none')
    if mode not in ('none','closed_areas','centerlines'):raise ValueError('道路模式不正確。')
    if mode=='none':return [],{'road_mode':'none','road_parts':0,'road_area_m2':0,'road_triangles':0},[]
    if not isinstance(layers,list) or not layers or any(not isinstance(s,str) for s in layers):raise ValueError('請選擇道路圖層。')
    available={e['layer'] for e in entities}
    if any(s not in available for s in layers):raise ValueError('找不到道路圖層。')
    if cfg.get('accept_road_interpretation') is not True:raise ValueError('請確認道路資料是封閉路面範圍或中心線；不得把道路邊線直接當成中心線。')
    scale=cfg['drawing_unit_m'];width=cfg.get('road_width_m',6)
    if not isinstance(width,(int,float)) or not math.isfinite(width) or not .5<=width<=100:raise ValueError('道路假設寬度須在0.5到100公尺之間。')
    lines=[]
    for e in entities:
        if e['layer'] not in layers or e['type'] not in ('LINE','LWPOLYLINE','POLYLINE','ARC','CIRCLE','SPLINE','ELLIPSE') or len(e['points'])<2:continue
        pts=[(round(p[0]*scale-ox,5),round(p[1]*scale-oy,5)) for p in e['points']]
        if e['closed'] and pts[0]!=pts[-1]:pts.append(pts[0])
        line=LineString(pts)
        if line.length>.01:lines.append(line)
    if not lines:raise ValueError('道路圖層沒有可用線條。')
    union=unary_union(lines);warnings=[];unclosed=0
    if mode=='centerlines':
        footprint=union.buffer(width/2,cap_style=2,join_style=2)
        warnings.append('道路由指定中心線加假設寬度產生；若原圖是兩側邊線，結果會錯誤，請先整理中心線。')
    else:
        faces,cuts,dangles,invalid=polygonize_full(union);unclosed=len(cuts.geoms)+len(dangles.geoms)+len(invalid.geoms)
        footprint=unary_union(list(faces.geoms))
        warnings.append('封閉輪廓均視為路面；需確認沒有把街廓、分隔島或建築範圍當路面。未封閉線段不會自動猜測補齊。')
    if footprint.is_empty:raise ValueError('沒有封閉路面範圍。請在CAD副本補齊道路邊界，或提供中心線並指定路寬。')
    if unclosed:warnings.append('道路只有部分封閉：%d段未封閉或無法組面，本次略過；結果不是完整路網。'%unclosed)
    hull=MultiPoint([(p[0],p[1]) for p in terrain]).convex_hull
    footprint=footprint.intersection(hull)
    if footprint.is_empty:raise ValueError('道路不在地形範圍內。')
    parts=list(footprint.geoms) if hasattr(footprint,'geoms') else [footprint]
    parts=[p for p in parts if p.geom_type=='Polygon' and p.area>.05]
    terrain_triangles=triangulate(MultiPoint([(p[0],p[1]) for p in terrain]));tree=STRtree(terrain_triangles)
    heights={(p[0],p[1]):p[2] for p in terrain};roads=[];n=0
    for part in parts:
        mesh=[]
        for index in tree.query(part,predicate='intersects'):
            triangle=terrain_triangles[int(index)];xy=list(triangle.exterior.coords)[:3];z=[heights[q] for q in xy]
            (ax,ay),(bx,by),(cx,cy)=xy;den=(by-cy)*(ax-cx)+(cx-bx)*(ay-cy)
            if abs(den)<1e-12:continue
            clipped=triangle.intersection(part)
            polys=list(clipped.geoms) if hasattr(clipped,'geoms') else [clipped]
            for poly in polys:
                if poly.geom_type!='Polygon' or poly.area<1e-6:continue
                for sub in triangulate(poly):
                    if not poly.covers(sub) or sub.area<1e-6:continue
                    vertices=[]
                    for x,y in list(sub.exterior.coords)[:3]:
                        u=((by-cy)*(x-cx)+(cx-bx)*(y-cy))/den;v=((cy-ay)*(x-cx)+(ax-cx)*(y-cy))/den
                        vertices.append([x,y,u*z[0]+v*z[1]+(1-u-v)*z[2]+.05])
                    mesh.append(vertices);n+=1
                    if n>50000:raise ValueError('道路三角面超過50,000，請裁切範圍或簡化地形。')
        if mesh:roads.append({'id':'R%04d'%(len(roads)+1),'triangles':mesh,'area_m2':part.area,'source_layers':layers,'mode':mode,'assumed_width_m':width if mode=='centerlines' else None})
    warnings.append('道路為一般模型網格面，依預處理地形三角網抬高5公分；Revit可能採不同三角化，需檢查貼合。未設計路基厚度、縱坡或排水。')
    return roads,{'road_mode':mode,'road_layers':layers,'road_parts':len(roads),'road_area_m2':sum(p['area_m2'] for p in roads),'road_triangles':n,'road_unclosed_line_parts':unclosed},warnings
