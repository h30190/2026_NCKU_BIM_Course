import csv,html
def write_preview(folder,data):
    points=data['terrain'];xmin=min(p[0] for p in points);xmax=max(p[0] for p in points);ymin=min(p[1] for p in points);ymax=max(p[1] for p in points)
    w=xmax-xmin;h=ymax-ymin
    def coords(pts):return ' '.join('%.4f,%.4f'%(p[0],-p[1]) for p in pts)
    svg=['<svg viewBox="%s %s %s %s" xmlns="http://www.w3.org/2000/svg" style="width:100%%;max-height:75vh;background:#e5eddf">'%(xmin,-ymax,w,h)]
    for road in data['roads']:
        for tri in road['triangles']:svg.append('<polygon points="'+coords(tri)+'" fill="#555e65"/>')
    for b in data['buildings']:
        color='#c28b48' if b['height_is_assumed'] else '#417e74'
        title=html.escape(b['id']+' '+b['height_source']+' '+str(b['height_above_ground_m'])+'m')
        svg.append('<polygon points="'+coords(b['points'])+'" fill="'+color+'"><title>'+title+'</title></polygon>')
    svg.append('</svg>');r=data['report']
    text='<!doctype html><meta charset="utf-8"><title>CAD 基地檢查預覽</title><style>body{font-family:Microsoft JhengHei,sans-serif;margin:24px;background:#f6f7f4;color:#263a36}p{line-height:1.7}</style><h1>'+html.escape(data['name'])+'</h1>'
    text+='<p>平面預覽；灰色：道路，棕色：假設屋高，綠色：CAD高度資料。滑鼠停在房屋上可看高度來源。這不是Revit完成畫面。</p>'
    text+='<p>道路 %d 區塊／%.1f m²；房屋 CAD高度 %d／假設高度 %d。請檢查道路是否誤填街廓、跨入房屋，確認後再採用。</p>'%(r['road_parts'],r['road_area_m2'],r['height_from_cad'],r['height_assumed'])
    text+=''.join(svg)+'<p>'+html.escape('；'.join(r['limitations']))+'</p>'
    (folder/'preview.html').write_text(text,encoding='utf-8')
    with (folder/'building-heights.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.writer(f);writer.writerow(['building_id','height_above_estimated_ground_m','height_source','is_assumed'])
        for b in data['buildings']:writer.writerow([b['id'],b['height_above_ground_m'],b['height_source'],b['height_is_assumed']])
