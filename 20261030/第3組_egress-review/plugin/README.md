# A 組員：Revit 逃生資料擷取外掛

依樓層、階段輸出房間、門、元件式樓梯、指定走廊中心線及缺漏清單，並提供門位置與樓梯終點的圖面點選設定。使用第三組既有 v0.3 資料契約；不做法規判定、不需 RFI 服務或帳號。擷取是唯讀；點選時暫時建立本層工作平面，完成或取消後回滾，不留下模型元件。

本次開發從 `main` 的 `e15b8e4` 開出 **`codex/revit-egress-export`**。後續修改提交此分支，經 PR 審閱後才能合併；不直接提交或推送 main。

## 給其他電腦用

2024 用 .NET Framework 4.8，2025、2026 用 .NET 8，2027 用 .NET 10。用 NuGet 參考套件建置只驗證編譯，實際載入仍要在該版 Revit 測。

`.\package.ps1` 會一次建置 Revit 2024–2027，產生 `dist\EgressExporter-Revit2024-2027.zip`（每個版本一個資料夾，加上 `install.bat`、`使用說明.txt`）。對方解壓縮後雙擊 `install.bat`，會自動裝到那台電腦有的每一版 Revit，重開 Revit 即可，不需要 Node、.NET SDK、網路或 MCP。只要一個版本時用 `-RevitVersions 2027`。建置機沒有裝的 Revit 版本，會自動從 NuGet 下載 API 參考套件（Nice3point.Revit.Api）來編譯，所以建置機需要網路。檢討網頁內嵌在 DLL 裡；每次擷取都在快照旁產生 `*-檢討網頁.html` 並自動開啟；使用類組、第幾層、防火構造在擷取視窗填寫（類組和樓層必填），隨網頁帶入後直接算出結果，並存在本機設定供下次帶入。建置機需要 Node（產生網頁）、.NET SDK 與對應版本的 Revit；各版本的 DLL 不能互換，安裝腳本會依版本挑對應的。

## 建置與安裝

預設 Revit 2027、.NET 10 SDK。Revit 2024 設定保留為 .NET Framework 4.8，需另外安裝該版 Revit API 與 .NET Framework 4.8 Developer Pack。兩版 DLL 不可互換。

```powershell
cd plugin
.\build.ps1 -RevitVersion 2027
.\install.ps1 -RevitVersion 2027

# 指定 SDK 或 Revit 安裝位置
.\build.ps1 -RevitVersion 2027 -Dotnet 'C:\path\to\dotnet.exe' -RevitDirectory 'C:\Program Files\Autodesk\Revit 2027'

# 2024（在具備 2024 API 的機器上建置）
.\build.ps1 -RevitVersion 2024
.\install.ps1 -RevitVersion 2024
```

安裝位置為 `%APPDATA%\Autodesk\Revit\Addins\2027\Group3.EgressExporter.addin` 及同名子目錄。不需系統管理員；不覆蓋 RFI 外掛。安裝程式把 DLL 放入 SHA-256 命名的版本子目錄，核對雜湊後才更新 manifest，並備份原 manifest。可在 Revit 開啟時準備新版，但已開啟的 Revit 仍使用舊版；自行儲存模型並重新啟動後才會載入新版。

## 第一次使用

1. 開啟模型，在「增益集」頁籤找到「第三組｜逃生資料」。
2. 按「擷取模型與缺漏清單」，選樓層及階段。第一次設定檔可留空。
3. 選擇 JSON 輸出路徑。會產生快照和 `settings-template-樓層ID-階段ID.json`（範本已存在就保留）。切換另一模型請使用另一個輸出資料夾。
4. 查看缺漏清單，可選取一或多列，按「在 Revit 選取所選元件」。沒有 Element ID 的列是整體範圍提醒。
5. 複製範本為 `settings.local.json`，逐項確認分類、樓梯資格、終點與走廊中心線。再次擷取時載入此設定。
6. 將已覆核的快照交給 B／C／D。來源或幾何尚有缺漏時，不應把檢討結果視為正式合規結論。

2026-11-02 起功能區只保留「擷取模型與缺漏清單」「選擇直通梯」。安全梯防火門終點、偵測不到第一階時的點選，都併進「選擇直通梯」；「手動指定樓梯終點」不再放在功能區。「查看所選元件識別碼」「設定門位置」不再放在功能區；沒有插入點的門（例如帷幕牆上的門）改用外框中心當位置，提醒代碼 `DOOR_POSITION_APPROX`。下方「在平面圖設定門與樓梯位置」中門的部分是舊流程的紀錄。

## 選擇直通梯

一般流程只需要這個按鈕：在樓板平面圖按「選擇直通梯」，點一下樓梯，外掛列出該位置重疊的樓梯、方向與偵測到的第一階，勾選要用的即可。設定存 `is_direct_stair`，不存座標，第一階在每次匯出時從梯段路徑重新偵測，所以模型改了會跟著更新；`endpoint_source` 為 `user_selected_direct:<偵測方式>`。偵測規則：從本層往上取最低梯段路徑起點；到本層為止取最高梯段路徑終點；穿過本層取本層高度的梯段，地上層優先往下、地下層優先往上。選過之後，本層取消勾選和沒選到的樓梯都移到 `unresolved_stairs`（`is_direct_stair: false`）。非元件式樓梯（舊式草圖樓梯、樓梯族群，品類是樓梯但不是 `Stairs`）讀不到梯段：按「選擇直通梯」點這座樓梯後，外掛請使用者點第一階，之後才列入計算，外框用包圍框，只供畫面顯示；沒指定且跨越本層時回報 `STAIR_ENDPOINT_UNCONFIRMED`，其他樓層的不再提醒。

## 在平面圖設定門與樓梯位置

先開啟要檢討的「樓板平面圖」，確認視圖的樓層與階段。兩個新按鈕使用此範圍；3D、天花板與其他視圖不提供點選，以免誤用另一層高程。

### 設定門位置

1. 選取門，按「第三組｜逃生資料 → 設定門位置」。也可以先按鈕、再選門。
2. 選「在平面圖點選門口位置」，點實際可通行門口。若已核對畫面與顯示座標，也可明確確認採用候選位置。
3. 核對樓層、階段、門編號與點座標，按確定儲存。Esc 或取消不會儲存這次修改。
4. 匯出時「設定」欄留空，外掛自動載入相同模型、樓層及階段的本機設定。

一般門仍優先讀 `LocationPoint`。沒有此資料的門以完整模型 BoundingBox 經 Transform 換算後產生 XY 中心候選，Z 使用本層高程；這是待人工核對的近似點，不代表實際門口。候選門完整保留在 `unresolved_doors`，不連進房間的 `door_ids` 或計算用 `doors`；確認後才轉入 `doors`。沒有幾何的門也保留識別碼與缺漏，不默默消失。門位置設定不會替使用者填入淨寬、房門關聯或防火性能。

### 設定樓梯終點

1. 選整座樓梯，按「設定樓梯終點」。若選到梯段，可按 Tab 切換到整座樓梯。
2. 選「第一階」或「防火門」。直通樓梯資格預設「待確認」，依 C 的規則由使用者確認。
3. 第一階：在本樓層平面點選終點。防火門：先選實際參考門，再點選其通行位置。
4. 核對摘要後儲存，再匯出同一樓層與階段。未知資格維持 null，即使已設定終點，B 也不會把它當成已確認的直通樓梯。

### 儲存、重設與幾何變更

- 本機設定在 `%LOCALAPPDATA%\Group3.EgressExporter\settings\模型識別與檔案路徑雜湊\樓層ID-階段ID.json`。另存新模型路徑後需重新設定，或明確載入已核對的設定檔。
- 兩個按鈕只更新選定元件，保留本機設定中的其他門、樓梯、房間與走廊。門設定可在同一按鈕清除；樓梯再次設定即取代該樓層原設定。
- 明確選擇外部 JSON 時，以該檔為準，不暗中混入本機設定。每次匯出另附此次使用設定的副本；檔名記於 `project.settings_file`。重複匯出會使用新副檔名，避免覆蓋先前設定副本。
- 人工點記錄 Internal Origin 的 XYZ 公尺座標與元件幾何指紋。指紋包括世界座標幾何、包圍框、族群 Transform、型別、樓層和階段；不只依賴存檔間才更新的 `Element.VersionGuid`。幾何或樓層變更時標記為過期，要求重新設定，不會退回採用另一個猜測點。
- 人工樓梯終點失效時保留在 `unresolved_stairs`，原本的 true 資格回到 null；不會改用自動第一階並沿用舊確認。重新設定人工點，或由使用者重新執行「選擇直通梯」，才能更新該設定。
- 防火門終點同時核對樓梯與參考門的幾何。點選中的工作平面會回滾，確認設定才寫入本機 JSON。
- 點位核對容差：距本層平面 1 mm，門外框周邊 0.5 m、樓梯外框周邊 3 m；指紋座標取至 0.000001 m。這些只用於攔截選錯元件／樓層，均非法規門檻，也不代表已驗證點位的法規適用性。

所有座標為 Revit Internal Origin 的 XY 公尺座標，不翻轉 Y；樓層高程採 `ProjectElevation`。長度、面積透過 `UnitUtils` 換算；ElementId 為文字；JSON 為 UTF-8 無 BOM。

## 擷取內容與判斷來源

| 資料 | 已實作 | 缺漏處理 |
|---|---|---|
| 房間 | 指定階段／樓層、名稱編號、面積、Finish 邊界、關聯門 | 未封閉、零面積、未放置、沒有門、分類未定皆列 warnings |
| 門 | 指定階段 FromRoom／ToRoom、插入點、有效淨寬 | 缺關聯、插入點需覆核、淨寬缺漏；不拿一般門寬當淨寬 |
| 樓梯 | 實際梯段、平台輪廓、梯段路徑、人工確認終點 | 不用包圍矩形代替樓梯；未確認樓梯保留於 unresolved_stairs |
| 走廊 | 指定房間與有順序的模型／詳圖曲線、淨寬、兩側居室設定 | 不由房間名稱猜測；拒絕斷線、錯樓層、超出房間的中心線 |
| 柱子 | 與樓層相交的建築／結構柱保守包圍框 | geometry_source 與 warnings 說明近似，避免誤認為實際輪廓 |

## 設定檔

先以實際匯出的範本為基礎。`model_guid`、`level_id`、`phase_id` 需完全相符，避免把另一樓層的確認結果套進來。模型識別採 `ProjectInformation.UniqueId`，另存副本可能相同；換模型後仍須人工核對。

```json
{
  "model_guid": "從快照 project 複製",
  "level_id": "從快照 project 複製",
  "phase_id": "從快照 project 複製",
  "clear_width_parameter": "Egress_ClearWidth",
  "rooms": [
    { "unique_id": "房間 UniqueId", "is_habitable": true, "is_circulation": false },
    { "unique_id": "走廊房間 UniqueId", "is_habitable": false, "is_circulation": true }
  ],
  "stairs": [
    {
      "unique_id": "樓梯 UniqueId",
      "is_direct_stair": true,
      "endpoint_type": "fire_door",
      "endpoint_unique_id": "已確認安全梯防火門的 UniqueId",
      "curve_end": 0
    }
  ],
  "corridors": [
    {
      "room_unique_id": "走廊房間 UniqueId",
      "curve_unique_ids": ["曲線一 UniqueId", "曲線二 UniqueId"],
      "rooms_both_sides": null
    }
  ]
}
```

- **識別碼取得**：選取 Revit 元件，按「查看所選元件識別碼」，選取文字複製。房間／門／樓梯也可直接在快照查 UniqueId。
- **淨寬參數**：`Egress_ClearWidth` 是使用者明確指定的有效淨寬參數名稱，不會自動建立。必須是 Revit「長度」型別且大於零。優先讀實例，沒有才讀型別；重名或無效值回傳 null。若已有可信的有效淨寬參數，改此名稱即可。
- **第一階終點**：將 `endpoint_type` 設成 `first_step`，參考由使用者確認位在第一階的 ModelCurve／DetailCurve，`curve_end` 為 0 或 1。程式不以樓梯中心或路徑第一點猜測法規終點。
- **防火門終點**：參考門的插入點；指定 `fire_door` 代表使用者確認其資格與位置，程式不自行認證門的防火性能。
- **走廊中心線**：曲線依路徑順序排列，允許個別反向。內部接點容差 1 mm（幾何處理設定，非法規值）。樓層高度差檢查容差 0.5 m（避免誤用別層，不代表法規）。分支走廊第一版需分成不同房間；每房間只接受一條有序中心線。
- 未確認的布林與數值保留 `null`；不要為了讓計算成功而填 `true` 或 `0`。

## 給 B／D 的欄位補充

既有接口沒有改名。新增欄位均為補充資訊：

- `unresolved_stairs[]`：終點未定的樓梯，`entry_point: null`。既有 check.js 要求 `stairs[]` 終點為座標，因此未定項目移至此陣列並留 warnings；不是漏抓樓梯。B 不可把這些當可達終點。
- `unresolved_doors[]`：已讀到但尚未確認位置的門；`position` 為 null，`position_candidate` 可供 D 顯示待確認標記，不能放入計算路網。`position_source`、`position_confirmed` 區別自動插入點、候選與人工確認。
- `footprint_loops`：每個實際梯段／平台投影的獨立輪廓，D 應逐圈畫。多圈時舊 `footprint` 留空，避免把不相連的邊界串成錯誤樓梯。這些是整座樓梯投影，尚未套用 Revit 平面視圖的剖切／隱線規則。
- `run_paths`：各梯段路徑，僅作幾何參考；尚未輸出踏階線。
- `boundary_loops`：原始房間所有邊界圈。內圈（柱子、孔洞）寫進 `obstacles` 由引擎繞開；不相連的多區域無法安全表示，所以 `boundary=[]`、`boundary_closed=false`，阻止錯誤穿越。
- `warnings[].severity`：error 代表缺資料或未涵蓋範圍，warning 代表待確認／近似。既有 B/C **不會自動以所有 warnings 阻止判定**，使用前需覆核清單。
- `project.model_version` 與 `has_unsaved_changes`：記錄匯出時模型版本；設定與快照仍應一起保存。

目前只處理主文件、主模型、單樓層。連結模型、設計選項、傳統樓梯及多層樓梯重複實例會明確提示未完整支援。曲線使用 Revit Tessellate 近似。柱以包圍框近似；設備、家具、矮牆及連結障礙物尚未完整涵蓋。門插入點可能與可通行門口不同，需在實際模型覆核。

## 驗證

```powershell
dotnet run --project tests/CoreTests.csproj -c Release
cd ..
node check.js
```

獨立測試驗證：封閉輪廓、反向接線、斷線拒絕、凹形房間越界、UTF-8、中文、64 位 ID、null、可重現序列化，以及定位點範圍、幾何變動、樓層／階段核對、舊設定相容與單一元件設定更新。這些測試不需要 Revit，也不代表已驗證真實模型 API 行為。

首次 Revit 驗收請使用模型副本：

1. 一間 10 m × 4 m 房間，確認匯出約 40 m²、座標與模型相符。
2. 門兩側各放房間，核對 phase 切換後 room_ids 正確，拆除門不進未對應階段。
3. 刪除／留空淨寬參數，確認輸出 null。
4. 一座雙跑梯，核對 footprint_loops 包含梯段與平台；未確認終點時只進 unresolved_stairs。
5. 走廊中心線故意斷開或切過 L 形房間外側，確認不送入 corridors 且有修正提示。
6. 有孔洞房間、設計選項、連結與未放置房間，確認缺漏提示完整。
7. 匯出前後模型沒有交易／變更，重複匯出除時間外一致。

2026-10-26：Revit 2027 API 編譯成功（0 警告／0 錯誤）；核心測試與專案 check.js 通過。此機器沒有 Revit 2024 API，2024 建置及真實模型操作尚待驗收；Revit MCP 當時回傳錯誤，未把編譯成功當作已完成模型實測。

同日首次模型實測：外掛成功載入、擷取表單可開啟，但匯出因 Revit 不支援 `OfClass(typeof(Room))` 而中止，尚未產生模型快照。已改為篩選 `SpatialElement` 後取 `Room`，並以 ElementId 比對目前視圖的預設樓層。修正版編譯成功（0 警告／0 錯誤），專案 check.js 通過。重跑核心測試時，JSON 暫存檔寫入遭執行環境拒絕，該次測試未完整通過。使用者已要求暫停驗證；修正版尚未重新安裝至 Revit，模型重測待續。

2026-10-27 更新：修正版已重新安裝；核心測試 10 項及專案 check.js 通過。使用者指定改用背景方式測試。MCP 現已成功讀到 project1-validation 與 FL1，但全模型 Rooms／Doors／Stairs 查詢皆為 0，尚無對應元件可驗證外掛匯出。詳細結果見 [背景驗證紀錄](validation/2026-10-27.md)。

同日後續：使用者另開 `NCKU_9hours_intro_course` 並授權操作匯出視窗，已成功匯出 Level 1／New Construction 快照。3 間房的邊界與面積、1 座樓梯的分件幾何及缺漏輸出已核對；18 項快照檢查通過。門座標仍僅輸出 1 扇，2 扇帷幕牆門列缺漏；房門關聯、走廊與樓梯終點尚待補齊，因此完整驗收仍為部分通過。見 [NCKU 實測紀錄](validation/2026-10-27-NCKU.md)。

2026-10-30：同步 PR #4 的 C 規則後，以真實快照產生隔離測試頁並實測卡片、缺漏清單及建立新批次。三間房正確保留模型資料不足，未出現瀏覽器錯誤。前端的快照匯入、樓層文字、模型取景、空值設定及樓梯分件呈現仍待整合，見 [A、C 與前端整合紀錄](validation/2026-10-30-integration.md)。

同日定位功能實測：新版已安裝並重新啟動 Revit 2027.3。在 NCKU 模型副本的 Level 3／New Construction，成功點選並重新匯出一扇門及一座樓梯的第一階測試終點；另外三扇門保留待定位。Esc 取消後設定檔雜湊不變，兩次匯出的 `has_unsaved_changes` 均為 false。核心測試 23 項、實際快照核對 9 組及專案檢查通過。樓梯直通資格維持 null，完整距離計算仍待補資料。實測中 `model_version` 的 GUID 有變，不能單憑版本 GUID 差異判斷幾何是否變動，見 [定位與樓梯終點實測紀錄](validation/2026-10-30-points.md)。

2026-11-02：缺漏清單改成同一種問題一列，附件數與「要處理／提醒」等級，選一列可在 Revit 選取該類所有元件。以下情況不再逐筆提醒：族群插入點落在主體牆上的門（`DOOR_INSERTION_POINT` 只在對不上時出現，門口偏差最多約半個門寬）、外牆上只連一間房的室外門、其他樓層留下的未放置房間、`STAIR_COMPOSITE_FOOTPRINT`。門淨寬缺漏和障礙物範圍的固定說明不影響計算，不列入 warnings；門的 `clear_width_m` 仍輸出，沒有就是 null。同一樓梯間的上、下元件在平面上外框重疊，「設定樓梯終點」視窗會列出本層所有重疊的樓梯與方向，由使用者選擇；上、下元件都列入計算候選，各自設定終點與資格，外掛不判斷方向；點的位置離該樓梯梯段路徑的第一階超過 0.5 m 時，確認視窗會提醒，選單也會標「位置待確認」並預設停在那座（操作提醒，非法規值）。房間的內圈（柱子、孔洞）改成寫進 `obstacles`（`category: room_inner_boundary`，`element_id` 是房間），`boundary` 取面積最大的外框，提醒代碼 `ROOM_INNER_LOOP`；不相連的區域仍回報 `ROOM_MULTIPLE_LOOPS` 並留空 `boundary`。此功能只有核心測試與引擎模擬，尚未用實際有獨立柱的 Revit 模型驗收。

同日 Revit 2027.3 實測：已建置、安裝並在 NCKU 副本與使用者新開的 apartment 模型匯出。重現並修正過期人工樓梯端點仍被自動端點取代、沿用舊 true 資格的問題；同一失效設定重測通過。apartment／Level 4 讀到 31 間房、39 扇門、2 座樓梯，20 間居室均有計算路徑；未確認的樓梯與防火構造保留人工確認狀態。詳見 [Revit 2027 合併版與新模型測試](validation/2026-11-02-Revit2027.md)。

重跑實際匯出資料核對（不需開啟 Revit；輸入檔留在本機）：

```powershell
node plugin/tests/verify-merged-export.cjs <自動終點快照.json> <失效人工點修正後快照.json>
node plugin/tests/verify-apartment-export.cjs <apartment-Level4.json>
```

## 程式位置

- `src/App.cs`：功能區按鈕、匯出與缺漏介面、所選元件識別碼。
- `src/Extractor.cs`：Revit 唯讀資料擷取與缺漏檢查。
- `src/Contract.cs`：v0.3 DTO、設定及 JSON 寫入。
- `src/Geometry.cs`：輪廓、中心線接續及越界檢查。
- `src/PointCommands.cs`：門位置與樓梯終點操作、可取消的點選及工作平面回滾。
- `src/RevitPositions.cs`：候選點、元件幾何指紋、本機設定儲存。
- `src/PointRules.cs`：不依賴 Revit 的點位、範圍及設定驗證。
- `EgressExporter.csproj`、`Directory.Build.props`：2027／2024 建置隔離。
- `build.ps1`、`install.ps1`：建置及每位使用者安裝。
- `tests/`：不依賴 Revit 的核心測試。

API 參考：[Autodesk Revit 2027 .NET 10 說明](https://blog.autodesk.io/revit-2027-sdk-net-10-api-changes-and-additions/)、本機安裝附帶 `RevitAPI.xml` 的 `StairsRun.GetFootprintBoundary`、`GetStairsPath`、`SpatialElement.GetBoundarySegments` 說明。
