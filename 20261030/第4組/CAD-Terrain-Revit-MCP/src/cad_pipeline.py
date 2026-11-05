"""CAD -> preview -> chosen dataset. No Revit model writes in this module.
Only top-level ModelSpace entities are read. Xrefs/block contents are not expanded.
"""
import argparse, csv, hashlib, json, math, os, re, shutil, subprocess, sys, uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
if (ROOT/'vendor').is_dir():sys.path.insert(0,str(ROOT/'vendor'))
from shapely.geometry import LineString, MultiPoint, Point, Polygon
from shapely.ops import polygonize_full, unary_union

def runtime():
    return Path(os.environ.get('CAD_TERRAIN_RUNTIME',str(Path(os.environ.get('LOCALAPPDATA',str(Path.home())))/'CADTerrainMCP')))
def write_json(path,data):
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_suffix(path.suffix+'.tmp')
    temporary.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    os.replace(temporary,path)
def session_dir(session_id):
    if not re.fullmatch('[a-f0-9]{32}',session_id):raise ValueError('Invalid session ID')
    p=runtime()/'sessions'/session_id
    if not p.is_dir():raise ValueError('找不到選檔紀錄，請重新選擇 CAD。')
    return p
def numeric_text(text):
    text=text.strip()
    # Keep strict: arbitrary room numbers or compound labels must not become heights.
    m=re.fullmatch(r'(?:EL\.?\s*=?\s*)?([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?:m|M)?',text)
    return float(m.group(1)) if m else None
def read_export(path):
    raw=path.read_bytes()
    try:text=raw.decode('utf-8-sig')
    except UnicodeDecodeError:text=raw.decode('mbcs' if os.name=='nt' else 'cp950')
    lines=text.splitlines(); units=int(lines[0].split('\t')[1]);entities=[]
    for line in lines[1:]:
        parts=line.split('\t')
        if len(parts)!=7:raise ValueError('CAD export row is malformed')
        handle,typ,layer,label,closed,coords,status=parts
        points=[list(map(float,p.split(','))) for p in coords.split(';') if p]
        if any(len(p)!=3 or any(not math.isfinite(v) for v in p) for p in points):raise ValueError('Invalid CAD coordinate')
        entities.append({'handle':handle,'type':typ,'layer':layer,'text':label,'closed':closed=='1','points':points,'status':status})
    return units,entities
def layer_summary(entities):
    groups={}
    for e in entities:
        g=groups.setdefault(e['layer'],{'name':e['layer'],'entity_count':0,'curve_count':0,'numeric_text_count':0,'point_count':0,'z_min':None,'z_max':None,'unsupported_count':0})
        g['entity_count']+=1
        if e['type'] in ('LINE','LWPOLYLINE','POLYLINE','ARC','CIRCLE','ELLIPSE','SPLINE') and len(e['points'])>1:g['curve_count']+=1
        elif e['type'] in ('TEXT','MTEXT') and numeric_text(e['text']) is not None:g['numeric_text_count']+=1
        elif e['type']=='POINT':g['point_count']+=1
        elif e['type'] not in ('TEXT','MTEXT'):g['unsupported_count']+=1
        if e['type'] not in ('TEXT','MTEXT','INSERT'):
            for p in e['points']:
                g['z_min']=p[2] if g['z_min'] is None else min(g['z_min'],p[2])
                g['z_max']=p[2] if g['z_max'] is None else max(g['z_max'],p[2])
    return sorted(groups.values(),key=lambda g:g['name'])
def find_autocad():
    override=os.environ.get('CAD_TERRAIN_ACCORE')
    if override and Path(override).is_file():return Path(override)
    base=Path(os.environ.get('ProgramFiles',r'C:\Program Files'))/'Autodesk'
    preferred=base/'AutoCAD 2024/accoreconsole.exe'
    if preferred.is_file():return preferred
    options=sorted(base.glob('AutoCAD 20*/accoreconsole.exe'),reverse=True)
    if options:return options[0]
    raise RuntimeError('找不到 AutoCAD Core Console。這版讀 DWG/DXF 需要已安裝且可啟動的 AutoCAD。')
def scan(source):
    source=Path(source).expanduser().resolve(strict=True)
    if source.suffix.lower() not in ('.dwg','.dxf'):raise ValueError('請選擇 .dwg 或 .dxf。')
    if source.stat().st_size>500_000_000:raise ValueError('檔案超過500MB，請先裁切基地範圍。')
    sid=uuid.uuid4().hex;folder=runtime()/'sessions'/sid;folder.mkdir(parents=True)
    copied=folder/('source'+source.suffix.lower());shutil.copy2(source,copied)
    shutil.copy2(ROOT/'src/export_cad.lsp',folder/'export.lsp')
    # Stable ASCII basenames and a session-specific working directory avoid quoting source paths inside LISP.
    # Inline our own ASCII program: no trusted-folder/security-setting changes.
    (folder/'read.scr').write_text((ROOT/'src/export_cad.lsp').read_text(encoding='utf-8')+'\n(ck-export "entities.tsv")\n_QUIT\n_N\n',encoding='ascii')
    cmd=[str(find_autocad()),'/i',str(copied),'/s',str(folder/'read.scr'),'/l','en-US']
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    try:
        result=subprocess.run(cmd,cwd=folder,capture_output=True,timeout=180,creationflags=flags)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError('CAD 讀取超過180秒；未改原圖，請確認 AutoCAD 授權與檔案大小。') from exc
    (folder/'autocad.log').write_bytes(result.stdout+result.stderr)
    if result.returncode or not (folder/'entities.tsv').exists():raise RuntimeError('AutoCAD 讀圖失敗，請查看 '+str(folder/'autocad.log'))
    units,entities=read_export(folder/'entities.tsv')
    if not entities:raise ValueError('ModelSpace 沒有可讀圖元。圖紙空間與外部參考不會自動展開。')
    summary={'session_id':sid,'source_name':source.name,'source_path':str(source),'source_sha256':hashlib.sha256(copied.read_bytes()).hexdigest(),
             'insunits':units,'suggested_unit_m':{1:.0254,2:.3048,4:.001,5:.01,6:1,7:1000}.get(units),
             'entity_count':len(entities),'layers':layer_summary(entities),
             'unsupported_block_count':sum(e['type']=='INSERT' for e in entities),
             'note':'只讀取 ModelSpace 頂層圖元；圖塊及外部參考未展開。曲線會分段近似，INSUNITS只是建議，請確認實際尺度。'}
    write_json(folder/'scan.json',summary);write_json(folder/'entities.json',entities)
    return summary
def idw(x,y,controls):
    sw=sz=0
    for px,py,pz in controls:
        d=(x-px)**2+(y-py)**2
        if d<1e-12:return pz
        w=1/d;sw+=w;sz+=pz*w
    return sz/sw
def validate_options(cfg):
    tolerance=cfg.get('contour_simplify_m',0)
    if isinstance(tolerance,bool) or not isinstance(tolerance,(int,float)) or not math.isfinite(tolerance) or not 0<=tolerance<=100:raise ValueError('等高線簡化容許偏移須為0至100公尺。')
    for key in ('drawing_unit_m','elevation_unit_m','grid_spacing_m','default_building_height_m'):
        v=cfg.get(key)
        if isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v) or not 0<v<=10000:raise ValueError('參數不正確：'+key)
    if cfg.get('terrain_mode') not in ('cad_z','text_idw'):raise ValueError('請選擇高程來源。')
    if not cfg.get('contour_layer'):raise ValueError('請選擇地形線或測點圖層。')
    if cfg['terrain_mode']=='text_idw' and not cfg.get('label_layer'):raise ValueError('文字插值模式必須選擇標高文字圖層。')
    if cfg['terrain_mode']=='text_idw' and cfg.get('accept_inferred') is not True:raise ValueError('必須明確接受文字標高插值為示意地形。')
def prepare(sid,cfg,activate=False):
    validate_options(cfg)
    folder=session_dir(sid);meta=json.loads((folder/'scan.json').read_text(encoding='utf-8'))
    entities=json.loads((folder/'entities.json').read_text(encoding='utf-8'))
    layer_names={e['layer'] for e in entities}
    for field in ('contour_layer','contour_layer_secondary','label_layer','building_layer'):
        if cfg.get(field) and cfg[field] not in layer_names:raise ValueError('找不到圖層：'+cfg[field])
    scale=cfg['drawing_unit_m']; mode=cfg['terrain_mode']; spacing=cfg['grid_spacing_m']
    contour_layers=list(dict.fromkeys(layer for layer in [cfg['contour_layer'],cfg.get('contour_layer_secondary')] if layer))
    contours=[e for e in entities if e['layer'] in contour_layers and e['type'] in ('LINE','LWPOLYLINE','POLYLINE','ARC','CIRCLE','ELLIPSE','SPLINE','POINT') and e['points']]
    if not contours:raise ValueError('所選地形圖層沒有可用線條或測點。若資料在圖塊內，請先在副本展開圖塊。')
    raw_points=[(p[0]*scale,p[1]*scale,p[2]*scale) for e in contours for p in e['points']]
    hull=MultiPoint([(p[0],p[1]) for p in raw_points]).convex_hull
    if hull.geom_type!='Polygon' or hull.area<1:raise ValueError('地形點共線或範圍小於1平方公尺，請檢查單位與圖層。')
    xmin,ymin,xmax,ymax=hull.bounds;ox,oy=(xmin+xmax)/2,(ymin+ymax)/2
    if max(xmax-xmin,ymax-ymin)>10000:raise ValueError('範圍超過10公里，請核對單位或先裁切基地範圍。')
    warnings=[];label_count=0;outside=0
    if mode=='cad_z':
        original_point_count=len(raw_points)
        tolerance=cfg.get('contour_simplify_m',0)
        if tolerance>0:
            reduced=[];nonflat=0
            for entity in contours:
                pts=[(p[0]*scale,p[1]*scale,p[2]*scale) for p in entity['points']]
                if len(pts)>2 and max(p[2] for p in pts)-min(p[2] for p in pts)<1e-8:
                    line=LineString([(p[0],p[1]) for p in pts])
                    reduced.extend((x,y,pts[0][2]) for x,y in line.simplify(tolerance,preserve_topology=True).coords)
                else:
                    reduced.extend(pts)
                    if len(pts)>2:nonflat+=1
            hull_xy=set(hull.exterior.coords)
            reduced.extend(p for p in raw_points if (p[0],p[1]) in hull_xy)
            raw_points=reduced
            warnings.append('等高線簡化容許平面偏移%s公尺；僅簡化固定高程線，保留其Z值。這是近似，請檢查細節。'%tolerance)
            if nonflat:warnings.append('%d條高程變化線未簡化，以免改變坡度。'%nonflat)
        zvalues=[p[2] for p in raw_points]
        if max(zvalues)-min(zvalues)<.001:
            raise ValueError('所選圖層所有Z高度相同，無法建立有起伏的地形。請選有真實高程的圖層，或明確改用文字標高示意模式。')
        z0=min(zvalues);chosen={}
        # Preserve all input vertices. Do not silently average contradictory elevations at one XY.
        for x,y,z in raw_points:
            key=(round(x-ox,5),round(y-oy,5));val=z-z0
            if key in chosen and abs(chosen[key]-val)>.01:raise ValueError('同一XY位置存在相互衝突的高程，請先修正CAD重疊線。')
            chosen[key]=val
        if len(chosen)>15000:raise ValueError('地形有%s個不重複點，上限15,000。請調整「等高線簡化容許偏移」，或在CAD副本裁切基地；網格間距不影響CAD Z模式。'%format(len(chosen),','))
        terrain=[[x,y,z] for (x,y),z in sorted(chosen.items())]
        controls=terrain
        warnings.append('原始地形點%s，處理後不重複點%s。'%(format(original_point_count,','),format(len(terrain),',')))
        warnings.append('地形使用CAD本身的Z值，但CAD來源精度未經測量驗證。Revit會在點之間三角化，未建立等高線約束。')
        warnings.append('房屋基座仍依附近高程點估計，沒有完成原生地形表面的精確貼合或整地。')
    else:
        labels=[e for e in entities if e['layer']==cfg['label_layer'] and e['type'] in ('TEXT','MTEXT') and e['points'] and numeric_text(e['text']) is not None]
        absolute=[(e['points'][0][0]*scale,e['points'][0][1]*scale,numeric_text(e['text'])*cfg['elevation_unit_m']) for e in labels]
        if len(absolute)<3:raise ValueError('標高文字不足3個；只接受純數字或 EL=數字，請核對圖層。')
        coverage=MultiPoint([(p[0],p[1]) for p in absolute]).convex_hull
        if coverage.geom_type!='Polygon':raise ValueError('標高文字位置共線，無法可靠建立面狀地形。')
        label_count=len(absolute);z0=min(p[2] for p in absolute)
        controls=[];seen={}
        for x,y,z in absolute:
            key=(round(x-ox,5),round(y-oy,5))
            if key in seen and abs(seen[key]-(z-z0))>.01:raise ValueError('同一文字位置存在衝突標高。')
            seen[key]=z-z0
        controls=[[x,y,z] for (x,y),z in seen.items()]
        if max(p[2] for p in controls)-min(p[2] for p in controls)<.001:raise ValueError('所有標高文字相同，無法建立起伏地形。')
        nx,ny=math.ceil((xmax-xmin)/spacing),math.ceil((ymax-ymin)/spacing)
        if (nx+1)*(ny+1)>15000:raise ValueError('網格太密，請加大地形網格間距。')
        xy={(round(x-ox,5),round(y-oy,5)) for x,y in list(hull.exterior.coords)[:-1]}
        for i in range(nx+1):
            for j in range(ny+1):
                x,y=xmin+i*spacing,ymin+j*spacing
                if hull.covers(Point(x,y)):xy.add((round(x-ox,5),round(y-oy,5)))
        xy.update((p[0],p[1]) for p in controls if hull.covers(Point(p[0]+ox,p[1]+oy)))
        terrain=[[x,y,idw(x,y,controls)] for x,y in sorted(xy)]
        outside=sum(not coverage.covers(Point(p[0]+ox,p[1]+oy)) for p in terrain)
        warnings.append('依文字插入位置作IDW插值，未將標高逐條配對等高線；這是示意地形。')
        warnings.append('外推點：%d/%d（%.1f%%）。文字位置不等於測量點。'%(outside,len(terrain),100*outside/len(terrain)))
    if max(p[2] for p in terrain)>10000:raise ValueError('高差異常，請檢查標高單位。')
    house_entities=[e for e in entities if cfg.get('building_layer') and e['layer']==cfg['building_layer']]
    segments=[];unsupported=0
    for e in house_entities:
        if e['type'] not in ('LINE','LWPOLYLINE','POLYLINE','ARC','CIRCLE','ELLIPSE','SPLINE') or len(e['points'])<2:unsupported+=1;continue
        pts=[]
        for p in e['points']:
            q=(round(p[0]*scale,2),round(p[1]*scale,2))
            if not pts or q!=pts[-1]:pts.append(q)
        if len(pts)<2:continue
        if e['closed'] and pts[0]!=pts[-1]:pts.append(pts[0])
        segments.append(LineString(pts))
    polygons,cuts,dangles,invalid=polygonize_full(unary_union(segments))
    buildings=[];skipped=[];accepted=[]
    # Nearby CAD-Z points estimate the foundation; terrain itself retains original Z values.
    def height_at(x,y):
        if mode=='text_idw':return idw(x,y,controls)
        near=sorted(controls,key=lambda p:(p[0]-x)**2+(p[1]-y)**2)[:8]
        return idw(x,y,near)
    for poly in sorted(polygons.geoms,key=lambda p:(p.centroid.x,p.centroid.y)):
        reason=None
        if poly.interiors:reason='inner courtyard / hole'
        elif not 6<=poly.area<=10000:reason='area outside 6..10000 m2'
        elif not hull.covers(poly):reason='outside terrain extent'
        if reason:skipped.append({'reason':reason,'area_m2':poly.area});continue
        simplified=poly.simplify(.03,preserve_topology=True)
        if simplified.difference(poly).area<1e-8:poly=simplified
        pts=[[round(x-ox,5),round(y-oy,5)] for x,y in list(poly.exterior.coords)[:-1]]
        if any(math.dist(pts[i],pts[(i+1)%len(pts)])<.01 for i in range(len(pts))):skipped.append({'reason':'edge below 1cm','area_m2':poly.area});continue
        if not poly.is_valid:skipped.append({'reason':'invalid polygon','area_m2':poly.area});continue
        if any(poly.intersection(p).area>.01 for p in accepted):skipped.append({'reason':'overlapping candidate','area_m2':poly.area});continue
        heights=[]
        for i,a in enumerate(pts):
            b=pts[(i+1)%len(pts)];n=min(200,max(1,math.ceil(math.dist(a,b)/5)))
            heights.extend(height_at(a[0]+(b[0]-a[0])*k/n,a[1]+(b[1]-a[1])*k/n) for k in range(n+1))
        base=min(heights)-.3;top=max(heights)+cfg['default_building_height_m']
        buildings.append({'id':'B%04d'%(len(buildings)+1),'points':pts,'base_z':base,'height':top-base,'area_m2':poly.area,
                          'assumed_height_above_highest_edge_m':cfg['default_building_height_m']});accepted.append(poly)
    if len(buildings)>3000:raise ValueError('候選建築超過3,000，請先裁切基地。')
    if house_entities and not buildings:warnings.append('房屋圖層沒有可用封閉輪廓，本次只會建立地形。')
    from site_features import apply_heights,road_surfaces
    buildings,height_report,height_issues=apply_heights(buildings,entities,cfg,ox,oy,z0)
    roads,road_report,road_warnings=road_surfaces(entities,cfg,terrain,ox,oy)
    warnings+=road_warnings
    warnings+=['房屋為CAD輪廓推導的一般模型實體；高度來源逐棟記錄，樓層換算的層高仍為假設。不保證每個輪廓是一棟獨立建築。',
               '範圍採地形資料凸包，不是地籍邊界。圖塊、外部參考與圖紙空間不會自動展開。']
    report={'source_name':meta['source_name'],'source_path':meta['source_path'],'source_sha256':meta['source_sha256'],
            'terrain_mode':mode,'contour_layer':cfg['contour_layer'],'label_layer':cfg.get('label_layer'),'building_layer':cfg.get('building_layer'),
            'drawing_unit_m':scale,'elevation_unit_m':cfg['elevation_unit_m'],'terrain_points':len(terrain),'elevation_label_count':label_count,
            'terrain_outside_label_hull_points':outside,'building_source_entities':len(house_entities),'polygonized_faces':len(polygons.geoms),
            'buildings_prepared':len(buildings),'building_skipped':len(skipped),'building_unsupported_entities':unsupported,
            'unresolved_line_parts':len(cuts.geoms)+len(dangles.geoms)+len(invalid.geoms),
            'width_m':xmax-xmin,'depth_m':ymax-ymin,'height_range_m':[z0,z0+max(p[2] for p in terrain)],
            'origin_cad_xy':[ox/scale,oy/scale],'local_zero_absolute_elevation_m':z0,'assumed_building_height_m':cfg['default_building_height_m'],
            'limitations':warnings}
    report.update(height_report);report.update(road_report)
    report['contour_layers']=contour_layers
    report['contour_simplify_m']=cfg.get('contour_simplify_m',0) if mode=='cad_z' else 0
    report['terrain_input_points']=original_point_count if mode=='cad_z' else len(raw_points)
    d={'schema_version':3,'name':meta['source_name']+'｜'+('CAD高程' if mode=='cad_z' else '文字插值示意'),'units':'meters','is_survey_accurate':False,
       'terrain_mode':mode,'origin':[ox,oy,z0],'terrain':terrain,'buildings':buildings,'roads':roads,'report':report}
    d['dataset_id']=hashlib.sha256(json.dumps(d,sort_keys=True,ensure_ascii=False).encode()).hexdigest()[:16]
    write_json(folder/'prepared.json',d);write_json(folder/'options.json',cfg);write_json(folder/'report.json',report)
    from preview_site import write_preview
    write_preview(folder,d)
    write_json(folder/'review_required.json',{'height_issues':height_issues,'skipped_polygons':skipped,'unresolved_lines':[list(g.coords) for g in list(cuts.geoms)+list(dangles.geoms)+list(invalid.geoms)]})
    if activate:activate_dataset(sid,d['dataset_id'])
    return {'session_id':sid,'dataset_id':d['dataset_id'],'prepared_path':str(folder/'prepared.json'),'preview_path':str(folder/'preview.html'),'active':activate,'report':report}
def activate_dataset(sid,expected):
    folder=session_dir(sid);d=json.loads((folder/'prepared.json').read_text(encoding='utf-8'))
    if d['dataset_id']!=expected:raise ValueError('檢查後資料已改變，請重新檢查。')
    write_json(runtime()/'active-selection.json',{'session_id':sid,'dataset_id':expected,'prepared_path':str(folder/'prepared.json'),'source_name':d['report']['source_name']})
    return {'status':'selected','dataset_id':expected,'source_name':d['report']['source_name']}
def main():
    parser=argparse.ArgumentParser();parser.add_argument('action',choices=['scan','prepare','activate']);parser.add_argument('--source');parser.add_argument('--session');parser.add_argument('--options');parser.add_argument('--dataset');parser.add_argument('--output',required=True)
    args=parser.parse_args()
    try:
        if args.action=='scan':result=scan(args.source)
        elif args.action=='prepare':result=prepare(args.session,json.loads(Path(args.options).read_text(encoding='utf-8-sig')))
        else:result=activate_dataset(args.session,args.dataset)
        write_json(Path(args.output),{'ok':True,'result':result})
    except Exception as exc:
        write_json(Path(args.output),{'ok':False,'error':str(exc)});sys.exit(1)
if __name__=='__main__':main()
