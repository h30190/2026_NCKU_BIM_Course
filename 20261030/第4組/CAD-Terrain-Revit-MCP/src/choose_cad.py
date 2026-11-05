"""Simple standalone chooser. Model creation remains inside Revit."""
import json, queue, threading, tkinter as tk
from tkinter import ttk, filedialog, messagebox
import cad_pipeline as pipeline

class Chooser:
    def __init__(self):
        self.root=tk.Tk(); self.root.title('CAD 基地 MCP v3 — 選擇 CAD');self.root.geometry('790x760')
        self.queue=queue.Queue();self.meta=None;self.preview=None;self.busy=False
        frame=ttk.Frame(self.root,padding=16);frame.pack(fill='both',expand=True)
        ttk.Label(frame,text='① 選 CAD → ② 檢查 → ③ 採用 → 回 Revit 建立',font=('',15)).pack(anchor='w')
        self.source=tk.StringVar(value='尚未選檔。直接選電腦裡的 DWG / DXF。')
        ttk.Label(frame,textvariable=self.source,wraplength=740).pack(anchor='w',pady=10)
        self.pick=ttk.Button(frame,text='選擇 CAD 檔案…',command=self.select);self.pick.pack(anchor='w')
        self.fields={};self.mapping={}
        presetbar=ttk.Frame(frame);presetbar.pack(fill='x',pady=5)
        ttk.Label(presetbar,text='圖資預設').pack(side='left',padx=(0,10))
        self.preset=ttk.Combobox(presetbar,state='readonly',values=['國土測繪981編碼（辨識後套用）','自訂'],width=30);self.preset.current(0);self.preset.pack(side='left')
        ttk.Button(presetbar,text='重新套用預設',command=self.apply_preset).pack(side='left',padx=8)
        book=ttk.Notebook(frame);book.pack(fill='x',pady=12)
        base=ttk.Frame(book,padding=8);extra=ttk.Frame(book,padding=8);book.add(base,text='地形與圖面單位');book.add(extra,text='房屋高度與道路')
        grid=ttk.Frame(base);grid.pack(fill='x');grid.columnconfigure(1,weight=1)
        entries=[('unit','CAD 1 單位代表',['請確認實際單位','1 公尺','1 毫米','1 公分','1 英尺']),
                 ('terrain','主要等高線／測點圖層',[]),('terrain2','第二等高線圖層（可不使用）',['不使用']),('mode','高程來源',['CAD 本身的 Z 高度','文字標高插值（示意）']),
                 ('labels','標高文字圖層',['不使用']),('building','房屋輪廓圖層',['不建立房屋']),
                 ('labelunit','標高文字 1 代表',['1 公尺','1 毫米','1 公分','1 英尺'])]
        for row,(key,label,values) in enumerate(entries):
            ttk.Label(grid,text=label).grid(row=row,column=0,sticky='w',padx=(0,12),pady=5)
            box=ttk.Combobox(grid,state='readonly',values=values);box.grid(row=row,column=1,sticky='ew');self.fields[key]=box
            if values:box.current(0)
            box.bind('<<ComboboxSelected>>',lambda e:self.invalidate())
        for row,(key,label,default) in enumerate([('spacing','文字插值網格間距（公尺）','20'),('simplify','等高線簡化容許偏移（公尺；0不簡化）','0'),('height','假設房屋高度（公尺）','9')],len(entries)):
            ttk.Label(grid,text=label).grid(row=row,column=0,sticky='w',pady=5)
            v=tk.StringVar(value=default);v.trace_add('write',lambda *a:self.invalidate());self.fields[key]=v
            ttk.Entry(grid,textvariable=v).grid(row=row,column=1,sticky='ew')
        extra.columnconfigure(1,weight=1)
        for row,(key,label,values) in enumerate([
            ('heightmode','房屋高度來源',['可調假設高度','屋高文字','樓層數 × 層高','屋頂絕對 Z']),
            ('heightlayer','屋高／樓層／屋頂圖層',['不使用']),
            ('heightunit','無單位屋高文字 1 代表',['1 公尺','1 毫米','1 公分','1 英尺']),
            ('missing','無高度或高度衝突時',['採用假設高度並標示','略過該房屋']),
            ('roadmode','道路資料類型',['不建立道路','封閉路面範圍','中心線 + 假設路寬'])]):
            ttk.Label(extra,text=label).grid(row=row,column=0,sticky='w',pady=4)
            box=ttk.Combobox(extra,state='readonly',values=values);box.grid(row=row,column=1,sticky='ew');box.current(0);self.fields[key]=box
            box.bind('<<ComboboxSelected>>',lambda e:self.invalidate())
        for row,key,label,default in [(5,'storey','假設每層高度（公尺）','3'),(6,'roadwidth','假設道路寬度（公尺）','6')]:
            ttk.Label(extra,text=label).grid(row=row,column=0,sticky='w',pady=4)
            var=tk.StringVar(value=default);var.trace_add('write',lambda *a:self.invalidate());self.fields[key]=var
            ttk.Entry(extra,textvariable=var).grid(row=row,column=1,sticky='ew')
        ttk.Label(extra,text='道路圖層（可多選）').grid(row=7,column=0,sticky='nw',pady=4)
        self.roadlist=tk.Listbox(extra,selectmode='multiple',exportselection=False,height=4);self.roadlist.grid(row=7,column=1,sticky='ew');self.roadlist.bind('<<ListboxSelect>>',lambda e:self.invalidate())
        self.roadaccept=tk.BooleanVar()
        ttk.Checkbutton(extra,text='我確認選的是路面範圍或中心線，並會檢查預覽；道路邊線不可直接當中心線。',variable=self.roadaccept,command=self.invalidate).grid(row=8,column=0,columnspan=2,sticky='w',pady=6)
        self.accept=tk.BooleanVar();ttk.Checkbutton(frame,text='我了解文字插值、缺高度補值及樓層換算包含假設。',variable=self.accept,command=self.invalidate).pack(anchor='w')
        buttons=ttk.Frame(frame);buttons.pack(fill='x',pady=12)
        self.check=ttk.Button(buttons,text='檢查這份 CAD',command=self.prepare);self.check.pack(side='left')
        self.use=ttk.Button(buttons,text='採用檢查結果',command=self.activate,state='disabled');self.use.pack(side='left',padx=10)
        self.previewbutton=ttk.Button(buttons,text='開啟平面預覽',command=self.open_preview,state='disabled');self.previewbutton.pack(side='left')
        self.status=tk.StringVar(value='原 CAD 不會被修改。圖塊與外部參考需先在副本展開。')
        ttk.Label(frame,textvariable=self.status,wraplength=740).pack(anchor='w')
        self.text=tk.Text(frame,height=15,wrap='word');self.text.pack(fill='both',expand=True,pady=8)
        self.root.after(100,self.poll);self.root.mainloop()
    def invalidate(self):
        self.preview=None
        if hasattr(self,'use'):self.use.configure(state='disabled')
        if hasattr(self,'previewbutton'):self.previewbutton.configure(state='disabled')
    def show(self,text):self.text.delete('1.0','end');self.text.insert('1.0',text)
    def task(self,fn,callback,label):
        if self.busy:return
        self.busy=True;self.status.set(label);self.pick.configure(state='disabled');self.check.configure(state='disabled');self.use.configure(state='disabled')
        def run():
            try:self.queue.put((callback,fn(),None))
            except Exception as e:self.queue.put((callback,None,str(e)))
        threading.Thread(target=run,daemon=True).start()
    def poll(self):
        try:
            callback,result,error=self.queue.get_nowait();self.busy=False;self.pick.configure(state='normal');self.check.configure(state='normal')
            if error:self.status.set('未完成；原有採用資料沒有更換。');messagebox.showerror('CAD 檢查',error)
            else:callback(result)
        except queue.Empty:pass
        self.root.after(100,self.poll)
    def select(self):
        path=filedialog.askopenfilename(filetypes=[('CAD','*.dwg *.dxf')])
        if not path:return
        self.invalidate();self.meta=None;self.source.set(path)
        self.task(lambda:pipeline.scan(path),self.scanned,'正在讀取 CAD 副本，通常需要數十秒，最長 180 秒…')
    def scanned(self,meta):
        self.meta=meta;self.mapping={}
        curves=[];labels=[]
        for g in meta['layers']:
            label=g['name']+'  [線 %d／數字 %d／Z %s..%s]'%(g['curve_count'],g['numeric_text_count'],g['z_min'],g['z_max'])
            self.mapping[label]=g['name']
            if g['curve_count'] or g['point_count']:curves.append(label)
            if g['numeric_text_count']:labels.append(label)
        for key,values in [('terrain',curves),('terrain2',['不使用']+curves),('labels',['不使用']+labels),('building',['不建立房屋']+curves)]:
            self.fields[key].configure(values=values);self.fields[key].set(values[0] if values else '')
        for key,needle,values in [('terrain','首曲線',curves),('labels','首曲線註記',labels),('building','永久性房屋',curves)]:
            match=next((v for v in values if needle in self.mapping[v]),None)
            if match:self.fields[key].set(match)
        self.fields['unit'].current(0)
        self.fields['heightlayer'].configure(values=['不使用']+list(self.mapping));self.fields['heightlayer'].current(0)
        self.roadlist.delete(0,'end')
        for item in curves:self.roadlist.insert('end',item)
        self.status.set('讀取完成。請確認實際尺度、圖層與高程來源，再按檢查。')
        self.show('來源：'+meta['source_name']+'\n圖元：'+str(meta['entity_count'])+'\n檔案單位提示：'+str(meta['suggested_unit_m'])+' 公尺／單位（可能不正確）\n'+meta['note']+'\n\n'+ '\n'.join(self.mapping))
        if self.preset.current()==0:self.apply_preset()
    def apply_preset(self):
        if not self.meta or self.busy:return
        if self.preset.current()!=0:return
        from cad_presets import nlsc_defaults
        preset=nlsc_defaults(self.meta['layers'])
        if preset is None:
            self.status.set('未找到可唯一辨識的9810101／9810102圖層，請自行選擇；不猜測高程。');return
        reverse={v:k for k,v in self.mapping.items()}
        self.fields['terrain'].set(reverse[preset['contour_layer']])
        self.fields['terrain2'].set(reverse.get(preset['contour_layer_secondary'],'不使用'))
        self.fields['unit'].set('1 公尺');self.fields['mode'].current(0);self.fields['simplify'].set(str(preset['contour_simplify_m']))
        if preset['building_layer']:self.fields['building'].set(reverse[preset['building_layer']])
        self.invalidate();self.status.set(preset['note'])
        self.text.insert('end','\n\n'+preset['note']+'\n主圖層：'+preset['contour_layer']+'；第二層：'+str(preset['contour_layer_secondary']))
    def prepare(self):
        if not self.meta:return messagebox.showinfo('先選檔','請先選擇 CAD。')
        try:
            scales={'1 公尺':1,'1 毫米':.001,'1 公分':.01,'1 英尺':.3048}
            cfg={'drawing_unit_m':scales[self.fields['unit'].get()],'elevation_unit_m':scales[self.fields['labelunit'].get()],
                 'contour_layer':self.mapping.get(self.fields['terrain'].get()),'contour_layer_secondary':self.mapping.get(self.fields['terrain2'].get()),'label_layer':self.mapping.get(self.fields['labels'].get()),
                 'building_layer':self.mapping.get(self.fields['building'].get()),'terrain_mode':'cad_z' if self.fields['mode'].current()==0 else 'text_idw',
                 'grid_spacing_m':float(self.fields['spacing'].get()),'default_building_height_m':float(self.fields['height'].get()),'accept_inferred':self.accept.get()}
            cfg['contour_simplify_m']=float(self.fields['simplify'].get())
            cfg.update(building_height_mode=['assumed','height_text','floors','roof_z'][self.fields['heightmode'].current()],
                building_height_layer=self.mapping.get(self.fields['heightlayer'].get()),building_height_unit_m=scales[self.fields['heightunit'].get()],
                missing_height_policy='assume' if self.fields['missing'].current()==0 else 'skip',storey_height_m=float(self.fields['storey'].get()),
                road_mode=['none','closed_areas','centerlines'][self.fields['roadmode'].current()],road_width_m=float(self.fields['roadwidth'].get()),
                road_layers=[self.mapping[self.roadlist.get(i)] for i in self.roadlist.curselection()],accept_road_interpretation=self.roadaccept.get())
            pipeline.validate_options(cfg)
        except Exception as e:return messagebox.showerror('請確認設定','請確認單位與數值。\n'+str(e))
        self.invalidate();sid=self.meta['session_id']
        self.task(lambda:pipeline.prepare(sid,cfg),self.prepared,'正在檢查地形與封閉房屋輪廓…')
    def prepared(self,result):
        self.preview=result;r=result['report']
        self.previewbutton.configure(state='normal')
        self.show('來源：%s\n1 圖面單位 = %s 公尺\n範圍：%.2f × %.2f 公尺\n標高範圍：%s 公尺\n地形點：%d\n候選房屋：%d；略過：%d\n\n%s'%(r['source_name'],r['drawing_unit_m'],r['width_m'],r['depth_m'],r['height_range_m'],r['terrain_points'],r['buildings_prepared'],r['building_skipped'],'\n'.join(r['limitations'])))
        self.text.insert('end','\n地形圖層：'+'、'.join(r.get('contour_layers',[])))
        self.text.insert('end','\n\n房屋高度：CAD %d／假設 %d／略過 %d\n道路：%d 區塊，%.1f 平方公尺'%(r.get('height_from_cad',0),r.get('height_assumed',0),r.get('height_skipped',0),r.get('road_parts',0),r.get('road_area_m2',0)))
        self.use.configure(state='normal');self.status.set('檢查完成，尚未更換 Revit 使用的資料。確認摘要後按「採用檢查結果」。')
    def open_preview(self):
        if self.preview:
            import webbrowser
            from pathlib import Path
            webbrowser.open(Path(self.preview['preview_path']).as_uri())
    def activate(self):
        if self.busy or not self.preview:return
        try:
            result=pipeline.activate_dataset(self.preview['session_id'],self.preview['dataset_id'])
            messagebox.showinfo('已採用','已選擇：'+result['source_name']+'\n回 Revit → 增益集 → CAD 基地 MCP v3 → 建立地形與房屋。\n建模後請另存成 .rvt。')
            self.status.set('已採用 '+result['source_name']+'。現在可以回 Revit 建立。')
        except Exception as e:messagebox.showerror('無法採用',str(e))
if __name__=='__main__':Chooser()
