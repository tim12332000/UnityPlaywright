---
name: skill-tool-unity-bridge(Unity Editor HTTP)
description: |-
  Skill功能: Unity Editor HTTP 操作手冊與端點快速參考
  Use when:
  - Editor 生命週期：status、refresh、playmode、menu-item、凍幀
  - Log / Diagnostics：recent、search、wait-for log、一站式 diagnose
  - Screenshot / 視覺檢查 / baseline pixel-diff
  - 場景查詢與 selector 定位：hierarchy、find、exists、dump-target、rect-transforms、get-active-window
  - UI 操作模擬：click、input、keypress、click-and-wait、input-and-wait（含 Playwright actionability）
  - 狀態斷言與等待：expect（toBeVisible/toHaveText/toHaveCount...）、wait-for、is-visible、is-interactable
  - Component Reflection 遠端 Inspector（runtime field/property 讀寫）
  - Batch / Scenario Runner 自動化流程、artifacts
  - 需要只靠 skill 文件操作 UnityBridge、少讀 UnityBridgeServer.cs。完整能力邊界見 body「能力邊界總覽」。
user-invocable: true
---

# Unity Bridge 工具

Documented Bridge version: `v22`

## 能力邊界總覽

> [!TIP]
> 先掃這段決定能不能用 Bridge 解題，再跳到「端點一覽」查具體參數。

**能做**（八大類）：

1. **Editor 生命週期**：`/status`、`/refresh`（AssetDatabase + recompile）、`/playmode/enter|exit|check`、`/menu-item`（執行任意 MenuItem，含 `Edit/Pause` 凍幀）
2. **Log / Diagnostics**：`/log/recent`、`/log/search`、`/log/clear`、`/wait-for?state=log`、`/diagnose`（截圖 + dump + log 一次打包）
3. **Screenshot / 視覺檢查**：`/screenshot`、`/screenshot/get`、`/baseline/save|list|diff|delete`（pixel-level diff，需要 PlayMode）
4. **場景查詢與 selector 定位**：`/hierarchy`、`/find`（name/tag/layer/component）、`/exists`、`/dump-target`、`/get-text`、`/find-by-text`、`/rect-transforms`、`/get-active-window`（GUIWindow 子類）、`/get-selected`（EventSystem）
5. **UI 操作模擬**：`/click`、`/input`、`/keypress`、`/click-and-wait`、`/input-and-wait`；內建 Playwright actionability（Visible + Stable + ReceivesEvents + Enabled / Visible + Enabled + Editable）
6. **狀態斷言與等待**：`/expect`（Playwright 風格 `toBeVisible`/`toHaveText`/`toHaveCount`...）、`/wait-for`、`/is-visible`、`/is-interactable`
7. **Component Reflection（遠端 Inspector）**：`/component/list|get|set`，透過 Reflection 讀寫 runtime 即時值（public field + `[SerializeField]` private field + public property，不依賴 SerializedProperty）
8. **Batch / 自動化流程**：`/batch`（POST 循序打多個 route）、`/scenario/run`（POST JSON DSL 跑流程 + 自動 diagnose + 產 report/artifacts）、`/artifacts/list`

**不能做 / 限制**：

- 不能直接讀寫 Prefab/Asset 檔（專案走 AssetBundle，本地無 `.prefab`）
- 需要 PlayMode 的端點：`/click`、`/input`、`/keypress`、`/click-and-wait`、`/input-and-wait`、`/screenshot`、`/baseline/*`、`/get-selected`、`/get-active-window`
- `/keypress` 只支援 `enter` / `esc`
- `/component/set` 只改 runtime 記憶體，不會寫回 scene/prefab serialized data
- `/batch` 循序執行（非平行）；同名 GO / 同型別 Component 預設回第一個匹配
- Domain reload、`/refresh`、PlayMode 切換會讓 port 改變（重讀 `Temp/bridge_port.txt`）

> [!IMPORTANT]
> **Direct-first 原則**：正常使用 UnityBridge 時，優先依本文件直接執行 endpoint。若參數不明、回傳與文件不符、需要修改 Bridge、或需要排查 Bridge 本身，再讀 `Assets/Editor/UnityBridgeServer.cs`。

> [!IMPORTANT]
> **所見即所得操作流程**：
> 1. 讀 `Temp/bridge_port.txt`。
> 2. 打 `/status`，確認 `version=v22` 或至少確認 Bridge 可連線。
> 3. 依本文件的端點表與範例操作。
> 4. 操作失敗先用 `/diagnose`、`/log/recent?compact=true`、`/dump-target` 查現場。
> 5. 必要時再讀 code，不把讀 code 當第一步。

> [!WARNING]
> `UnityBridge_TODO.md` 是歷史規劃與完成紀錄，不是主要操作入口。查可用 endpoint、參數、回傳格式時，優先看本 `SKILL.md`。

> [!TIP]
> - Port 從 `Temp/bridge_port.txt` 讀取（預設 6544，若被佔用會自動遞增到 6553）
> - 無需 token，先讀 port 再 `curl http://localhost:$PORT/...`
> - Domain reload 後自動重啟，port 衝突時自動換 port

## 取得 Port（每次操作前必做）

```bash
BRIDGE_PORT=$(cat Temp/bridge_port.txt)
curl -s "http://localhost:${BRIDGE_PORT}/status"
```

`/status` 應回傳類似 `ok. version=v22 playmode=True/False`。若版本不是本文件標示的 `v22`，先告知使用者「Bridge 版本與 skill 文件不一致」，再決定是否需要檢查 code 或更新 skill。

> [!WARNING]
> - If `http://127.0.0.1:$PORT/...` fails but `http://localhost:$PORT/...` works, the bridge listener is likely bound only to IPv6 loopback `::1`.
> - Prefer `http://localhost:$PORT/...` in scripts and examples. Do not hardcode `127.0.0.1`.
> - If `Temp/bridge_port.txt` has a value but the endpoint is unreachable, verify whether the port is stale after domain reload or Play Mode, then retry with the latest port.

> [!IMPORTANT]
> **不要硬編碼 port 6544**，一律從 `Temp/bridge_port.txt` 讀取。Domain reload 或 Play Mode 進出後 port 可能改變。

## 核心概念

`UnityBridgeServer`（`Assets/Editor/UnityBridgeServer.cs`）是 `[InitializeOnLoad]` 的 HTTP Server，Unity Editor 啟動時自動跑。預設嘗試 port 6544，若被佔用則依序嘗試 6545–6553，實際 port 寫入 `Temp/bridge_port.txt`。多數操作透過 HTTP GET 完成；`/batch` 與 `/scenario/run` 為 POST。

> [!CAUTION]
> 一般 UI 查詢、點擊、輸入、截圖、log、scenario runner 優先直接照「端點一覽」和對應範例執行。只有在文件不足以決策或現場回傳異常時，再讀 `UnityBridgeServer.cs`，避免一開始就把 Bridge 實作塞進 context。

## Play Mode 測試 SOP

> [!IMPORTANT]
> 編譯後要讓遊戲 runtime code/state 乾淨生效時，重置的是 **Play Mode**，不是 Unity Editor。

- Unity Editor 是工具本體；Play Mode 才是遊戲執行狀態。
- 編譯或 `/refresh` 後做驗證，標準流程是 `/playmode/exit` → 等 Bridge/domain reload 回來 → 重新讀 `Temp/bridge_port.txt` → `/playmode/enter` → 再讀 port/status。
- 不要因為「重開」「關閉重開」「編譯後重開」這類說法就關閉 Unity Editor 主程式；在 Unity 測試語境中預設指 Play Mode exit/enter。
- 只有在使用者明確說「重開 Unity Editor / 關閉 Editor」，或 Editor/Bridge 明確壞死且 Play Mode reset、重新讀 port、等待 domain reload 都無效時，才考慮關閉 Editor。
- 若 Bridge 暫時連不上，先等待 domain reload、重新讀 `Temp/bridge_port.txt`、查 `/status`；不要直接 kill/close Unity process。
- 修改 `UnityBridgeServer.cs` 後必須推進 Bridge 版本號，並讓啟動 log 與 `/status` 同步回傳版本；驗證時先確認 `/status` 的 version 是新版本，避免誤判仍在執行舊 assembly。

## 暫停 / 繼續 PlayMode（凍幀）

透過 `Edit/Pause` MenuItem 來 toggle PlayMode 的暫停狀態：

```bash
# 切換暫停 / 繼續（同一指令，toggle）
curl -s "http://localhost:${BRIDGE_PORT}/menu-item?path=Edit%2FPause"
```

> [!IMPORTANT]
> **什麼時候需要暫停**：
> - `baseline screenshot` 比對要讓 `/baseline/diff` 的 hash short-circuit 命中 `verdict=identical`——活躍場景連續兩幀永遠有差異（粒子、動畫 icon、時間 shader），暫停才能讓連續 capture byte-identical。
> - 截圖前想凍結動畫、讓 UI 定位清楚可讀。
> - 除錯時不想有新 event / network packet 干擾現場快照。
>
> **注意**：
> - 這是 toggle；連打兩次就恢復。如果不確定狀態，先看 `/status` 或 GameView 畫面。
> - 暫停**不會**解除 PlayMode，Bridge、MessagePacket、所有現場 state 都保留，純粹是停 frame update。
> - Domain reload / `/refresh` 會失去暫停狀態。

## 端點一覽（快速參考）

> [!TIP]
> 這張表是低 token 操作入口。先用表格選 endpoint；需要 copy-paste 時再跳到下方「常用範例」對應段落。

| 端點 | 說明 | 參數 |
|------|------|------|
| `/status` | 查詢狀態（含 Bridge version 與 Play Mode） | — |
| `/refresh` | 刷新 AssetDatabase + recompile | — |
| `/playmode/enter` | 進入 Play Mode | — |
| `/playmode/exit` | 離開 Play Mode | — |
| `/playmode/check` | Play Mode runtime health（Unity prereqs + active GUIWindow + 近期 log 活動量） | `logWindowSec`(預設5) |
| `/log/recent` | 取得最近 log | `count`(預設30), `type`(Log/Warning/Error), `compact`(true=省略stackTrace), `offset`(跳過前N筆) |
| `/log/search` | 搜尋 log (message+stackTrace) | `q`(必填), `count`(預設50), `type`, `compact`(true=省略stackTrace), `offset`(跳過前N筆) |
| `/log/clear` | 清空 log buffer | — |
| `/rect-transforms` | 查詢場景 RectTransform | `q`(搜尋關鍵字，空=全部) |
| `/screenshot` | 截取 Game View 畫面 | — |
| `/screenshot/get` | 取得截圖 PNG 二進位 | `path`(選填，預設最新截圖) |
| `/click` | 模擬 UI 點擊（selector 模式內建 Playwright actionability 檢查） | `name`/`path`/`parent`/`component`/`index` 或 `x`+`y`(螢幕座標), `force`(true=跳過 actionability), `timeoutMs`(預設5000), `pollMs`(預設100) |
| `/input` | 模擬輸入框文字輸入（內建 Visible+Enabled+Editable 檢查） | `text`(欲輸入文字), `force`(true=跳過 actionability), `timeoutMs`(預設5000), `pollMs`(預設100) |
| `/keypress`| 模擬按下特定按鍵 | `key`(支援 `enter`, `esc`) |
| `/menu-item` | 執行 Unity MenuItem | `path`(必填，如 `Tools/Something`) |
| `/component/list` | 列出 GO 上所有 Component | `name`(GO 名稱) |
| `/component/get` | 讀取 Component 的 field/property | `name`, `component`(型別名), `property`(選填，省略列全部) |
| `/component/set` | 寫入 Component 的 field/property | `name`, `component`, `property`, `value`(JSON 或純值) |
| `/hierarchy` | 場景 Hierarchy 樹狀結構 | `depth`(預設10), `limit`(預設1000), `root`(篩選根物件名) |
| `/find` | 搜尋 GameObject | `search`(必填), `by`(name/tag/layer/component，預設name), `inactive`(預設true) |
| `/exists` | 檢查 GameObject 是否存在 | `name` 或 `path`(hierarchy path), `parent`, `component`, `index`, `inactive`(預設true) |
| `/get-text` | 取得 GameObject 上的顯示文字 | `name` 或 `path`, `parent`, `component`, `index`, `inactive`(預設true) |
| `/dump-target` | 傾印 GameObject 完整資訊 | `name` 或 `path`, `parent`, `component`, `index`, `inactive`(預設true) |
| `/wait-for` | 等待 GameObject 達到指定狀態 | `name` 或 `path`, `parent`, `component`, `index`, `state`(exists/missing/active/inactive/text/text-contains/log/log-contains), `value`(比對用), `type`(Log/Warning/Error/Exception/Assert，僅 log state 使用), `timeoutMs`(預設5000), `pollMs`(預設100) |
| `/is-visible` | 檢查 GO 是否可見（active + CanvasGroup alpha） | `name` 或 `path`, `parent`, `component`, `index` |
| `/is-interactable` | 檢查 GO 是否可互動（Selectable + CanvasGroup） | `name` 或 `path`, `parent`, `component`, `index` |
| `/get-selected` | 取得當前 EventSystem 選取的 GO | —（需 PlayMode） |
| `/get-active-window` | 列出所有 active 的 GUIWindow 子類 | —（需 PlayMode） |
| `/click-and-wait` | 點擊前跑 actionability 檢查，點擊後等待目標狀態 | `name`/`path`/`parent`/`component`/`index`(點擊目標), `waitTarget`(等待目標), `waitState`(預設active), `waitValue`, `force`(true=跳過 actionability), `timeoutMs`(預設5000), `pollMs`(預設100) |
| `/input-and-wait` | 輸入前跑 actionability 檢查，輸入後等待目標狀態 | `text`(輸入內容), `waitTarget`, `waitState`(預設active), `waitValue`, `force`(true=跳過 actionability), `timeoutMs`(預設5000), `pollMs`(預設100) |
| `/find-by-text` | 依顯示文字搜尋 GO | `text`(必填), `exact`(預設false, contains匹配) |
| `/expect` | Web-first assertion（Playwright 風格，自動 retry 至 timeout） | `name`/`path`/`parent`/`component`/`index` + `condition`(必填) + `value`(部分 condition 必填) + `timeoutMs`(預設5000) + `pollMs`(預設100) |
| `/diagnose` | 失敗診斷：一次回傳截圖 + 目標 dump + visible/enabled/editable + 最近 N 筆 log | `name`/`path`/... (選填), `logs`(預設20), `screenshot`(預設true, false 跳過截圖) |
| `/baseline/save` | 截當前 Game View 存為 baseline（需 PlayMode） | `name`(必填，[A-Za-z0-9_-]) |
| `/baseline/list` | 列出所有已存的 baseline + metadata | — |
| `/baseline/delete` | 刪除 baseline（含 png/json/compare_diff） | `name` |
| `/baseline/diff` | 截當前 + 與 baseline pixel-diff，回 verdict/regions（需 PlayMode） | `name`, `identicalThreshold`(預設0.001), `gridSize`(預設32), `perPixelThreshold`(預設16) |
| `/batch` | **POST** 批次執行多個路由 | Body: `{"paths":["/route1?p=v","/route2"]}` |
| `/scenario/run` | **POST** 通用 scenario runner，依 JSON DSL 循序執行 step 並輸出 report | Body: 見「Scenario Runner」 |
| `/artifacts/list` | 列出 Bridge 測試 artifact（scenario report、log、screenshot、baseline、run-local copies） | `kind`(all/screenshots/baselines/reports/logs/run-artifacts), `limit`(預設50, 最大500) |

## 常用範例

> [!TIP]
> 建議閱讀順序：先跑「基本操作」確認 Bridge/PlayMode，再用「搜尋 GameObject」「查詢與等待」定位 UI；要操作時用「UI 點擊模擬」「UI 文字輸入模擬」，失敗才看「失敗診斷」。`Scenario Runner`、baseline、Component Reflection 屬於進階工具，需要流程化測試或查 runtime 值時再看。

### 基本操作

```bash
# 查詢狀態
curl -s "http://localhost:${BRIDGE_PORT}/status"

# 進入 Play Mode（domain reload 後 bridge 自動重啟）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/playmode/enter"
sleep 8
curl -s "http://localhost:${BRIDGE_PORT}/status"

# 離開 Play Mode
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/playmode/exit"

# 刷新 Assets
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/refresh"

# Play Mode runtime health：Unity prereqs + 真實遊戲狀態
curl -s "http://localhost:${BRIDGE_PORT}/playmode/check"
# 首行 "ready: yes" 或 "ready: no (<reasons>)"；reasons 包含 "no active GUIWindow (likely login/loading)" 或 "no recent log activity (game loop may be idle/stalled)"
# 門檻：Unity-level（EventSystem/Canvas/Camera） + game-level（至少 1 個 active GUIWindow + 近 5 秒內有 log 活動）
# 調整 log 觀察窗：?logWindowSec=10
```

### Log 查詢

```bash
# 最近 10 筆錯誤（完整含 stackTrace）
curl -s "http://localhost:${BRIDGE_PORT}/log/recent?count=10&type=Error"

# compact 模式：只顯示 message 單行，省 token（推薦先用這個掃一遍）
curl -s "http://localhost:${BRIDGE_PORT}/log/recent?count=20&type=Error&compact=true"

# 用 offset 展開指定單筆的完整 stackTrace（index 從 compact 結果取得）
curl -s "http://localhost:${BRIDGE_PORT}/log/recent?count=1&offset=3"

# 搜尋關鍵字（同時搜 message 與 stackTrace）
curl -s "http://localhost:${BRIDGE_PORT}/log/search?q=NullReference&count=20&compact=true"

# 清空 log buffer（回傳被清除的筆數）
curl -s "http://localhost:${BRIDGE_PORT}/log/clear"

# wait for log keyword in message or stackTrace
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?state=log&value=NullReference&timeoutMs=5000&pollMs=200"

# wait for a specific log type only
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?state=log&value=NullReference&type=Exception&timeoutMs=5000&pollMs=200"
```

### 場景 Hierarchy

```bash
# 查看場景樹（深度 3，最多 50 個物件）
curl -s "http://localhost:${BRIDGE_PORT}/hierarchy?depth=3&limit=50"

# 只看特定根物件
curl -s "http://localhost:${BRIDGE_PORT}/hierarchy?root=Canvas&depth=5"
```

### 搜尋 GameObject

```bash
# 依名稱搜尋（模糊匹配）
curl -s "http://localhost:${BRIDGE_PORT}/find?search=Button&by=name"

# 依 Component 類型搜尋
curl -s "http://localhost:${BRIDGE_PORT}/find?search=Camera&by=component"

# 依 Tag 搜尋
curl -s "http://localhost:${BRIDGE_PORT}/find?search=Player&by=tag"

# 依 Layer 搜尋
curl -s "http://localhost:${BRIDGE_PORT}/find?search=UI&by=layer"

# 排除 inactive 物件
curl -s "http://localhost:${BRIDGE_PORT}/find?search=Panel&by=name&inactive=false"
```

> [!IMPORTANT]
> - 搜尋範圍：所有已載入 Scene + DontDestroyOnLoad（含 inactive）
> - name 搜尋為模糊匹配（contains, case-insensitive）
> - tag/layer/component 搜尋為精確匹配

### 查詢與等待

```bash
# 檢查物件是否存在（回傳 true/false）
curl -s "http://localhost:${BRIDGE_PORT}/exists?name=ButtonClose"

# 用 hierarchy path 精確定位
curl -s "http://localhost:${BRIDGE_PORT}/exists?path=Canvas/Panel/ButtonClose"

# 用 parent + name + component 縮小同名物件範圍
curl -s "http://localhost:${BRIDGE_PORT}/dump-target?parent=Canvas/Panel&name=ButtonClose&component=UIButton"

# 同名物件指定第 N 筆（0-based）
curl -s "http://localhost:${BRIDGE_PORT}/dump-target?name=UIButton&component=UIButton&index=1"

# 取得物件上的顯示文字（支援 Text / UIText / TMP）
curl -s "http://localhost:${BRIDGE_PORT}/get-text?name=LabelTitle"

# 傾印物件完整資訊（path、active、layer、components 等）
curl -s "http://localhost:${BRIDGE_PORT}/dump-target?name=ButtonClose"

# 等待物件出現（預設 timeout 5s）
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?name=Panel&state=exists"

# 等待物件變 active
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?name=Panel&state=active&timeoutMs=10000"

# 等待物件消失
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?name=LoadingMask&state=missing"

# 等待文字變成指定值
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?name=LabelLevel&state=text&value=Lv.10"

# 等待文字包含關鍵字
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?name=LabelStatus&state=text-contains&value=Complete"

# 等待 log 出現關鍵字（不需要 name/path）
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?state=log&value=Connected&timeoutMs=10000"

# wait for Exception / Error / Warning only
curl -s "http://localhost:${BRIDGE_PORT}/wait-for?state=log&value=NullReference&type=Exception&timeoutMs=10000"
```

> [!IMPORTANT]
> - `name` 為模糊匹配（同 `/find`），`path` 為精確 hierarchy path 匹配（case-insensitive）
> - `/wait-for` 會阻塞直到條件滿足或 timeout，適合在操作後等 UI 穩定
> - `/dump-target` 回傳純文字，包含 name、path、active、layer、tag、rectTransform、text、components

### 可見性與互動檢查

```bash
# 檢查物件是否可見（activeInHierarchy + CanvasGroup alpha > 0）
curl -s "http://localhost:${BRIDGE_PORT}/is-visible?name=Panel"

# 檢查物件是否可互動（Selectable.interactable + CanvasGroup.interactable + blocksRaycasts）
curl -s "http://localhost:${BRIDGE_PORT}/is-interactable?name=ButtonConfirm"

# 取得當前 EventSystem 選取的 GO（需 PlayMode）
curl -s "http://localhost:${BRIDGE_PORT}/get-selected"

# 列出所有 active 的 GUIWindow 子類（需 PlayMode）
curl -s "http://localhost:${BRIDGE_PORT}/get-active-window"
```

> [!IMPORTANT]
> - `/is-visible` 和 `/is-interactable` 回傳帶原因的 `true` / `false (原因)`，方便 debug
> - `/get-active-window` 透過 reflection 偵測 GUIWindow 子類，不依賴硬編碼

### UI 點擊模擬

```bash
# 依名稱點擊（內建 Playwright actionability：Visible + Stable + ReceivesEvents + Enabled）
curl -s "http://localhost:${BRIDGE_PORT}/click?name=ButtonClose"

# 使用 hierarchy path 定位點擊目標
curl -s "http://localhost:${BRIDGE_PORT}/click?path=Canvas/Panel/ButtonClose"

# 使用 parent + component + index 定位點擊目標
curl -s "http://localhost:${BRIDGE_PORT}/click?parent=Canvas/Panel&component=UIButton&index=1"

# 跳過 actionability（類似 Playwright force=true），風險自負
curl -s "http://localhost:${BRIDGE_PORT}/click?name=ButtonClose&force=true"

# 依座標點擊（座標模式不跑 actionability，沒有明確目標可檢查）
curl -s "http://localhost:${BRIDGE_PORT}/click?x=500&y=300"
```

> [!IMPORTANT]
> - `/click` **需要 Play Mode**，Edit Mode 下會回傳錯誤
> - 名稱模式搜尋範圍：所有已載入 Scene + DontDestroyOnLoad
> - 支援 UI 元素（RectTransform）與 3D 物件
> - 會自動往上找 `IPointerClickHandler`，找不到則嘗試 `ISubmitHandler`
> - **Actionability 檢查**（對齊 <https://playwright.dev/docs/actionability>）：
>   - `Visible`：`activeInHierarchy` + `CanvasGroup.alpha>0` + 非空 bbox
>   - `Stable`：連續兩次 poll 的 RectTransform world-corner bbox 相同
>   - `ReceivesEvents`：`EventSystem.RaycastAll` 第一個命中是目標或其子孫（沒被遮）
>   - `Enabled`：`Selectable.interactable` + `CanvasGroup.interactable` + `blocksRaycasts`
> - Actionability 在 `timeoutMs` 內自動 polling，通過才執行 click；失敗回傳 `error: actionability timeout (...)`
> - 失敗訊息會標明哪一項沒過（例：`error: actionability timeout (5000ms): occluded by LoadingMask`）

### UI 文字輸入模擬

```bash
# 先點擊輸入框，使其成為當前焦點 (currentSelectedGameObject)
curl -s "http://localhost:${BRIDGE_PORT}/click?name=AccountInput"

# 發送文字輸入指令（內建 Visible + Enabled + Editable 檢查）
curl -s "http://localhost:${BRIDGE_PORT}/input?text=MyTestAccount"

# 跳過 actionability
curl -s "http://localhost:${BRIDGE_PORT}/input?text=MyTestAccount&force=true"
```

> [!IMPORTANT]
> - `/input` **需要 Play Mode**
> - 輸入前畫面中**必須有獲得焦點的 UI 元件**，且必須包含 `UnityEngine.UI.InputField` 或 `TMP_InputField`
> - 自動觸發 `onValueChanged` 事件以更新 UI 顯示
> - **Actionability 檢查**（對應 Playwright `fill()`）：`Visible` + `Enabled` + `Editable`（`InputField.readOnly==false`）。檢查目標為 `EventSystem.current.currentSelectedGameObject`，在 `timeoutMs` 內自動 polling

### 鍵盤按鍵模擬

```bash
# 確保當前有 UI 焦點後，模擬按下 Enter 鍵送出
curl -s "http://localhost:${BRIDGE_PORT}/keypress?key=enter"

# 模擬按下 Esc 鍵取消
curl -s "http://localhost:${BRIDGE_PORT}/keypress?key=esc"
```

> [!IMPORTANT]
> - `/keypress` **需要 Play Mode**
> - 當前實作會**同時發送兩種事件**以確保最大相容性：
>   1. 向 `currentSelectedGameObject` 派送 `ISubmitHandler` (enter) 或 `ICancelHandler` (esc) 介面事件。
>   2. 透過 Windows 原生 API (`user32.dll` 的 `keybd_event`) 向作業系統發送實體的按鍵訊號（會自動將焦點設回 GameView）。
> - **注意**：實體按鍵模擬會真的發送鍵盤訊號，測試期間請勿隨便切換作業系統焦點，否則按鍵可能送到其他視窗。

### 點擊後等待

```bash
# 點擊按鈕後等待目標面板出現
curl -s "http://localhost:${BRIDGE_PORT}/click-and-wait?name=ButtonOpen&waitTarget=PanelDetail&waitState=active"

# 點擊關閉後等待面板消失
curl -s "http://localhost:${BRIDGE_PORT}/click-and-wait?name=ButtonClose&waitTarget=PanelDetail&waitState=missing&timeoutMs=3000"
```

> [!IMPORTANT]
> - `/click-and-wait` **需要 PlayMode**
> - 如果不帶 `waitTarget`，行為等同 `/click`
> - **點擊前自動跑 actionability 5 檢查**（詳見上方「UI 點擊模擬」段落），全通過才點擊，可大幅降低「點到一半被 mask 擋住」、「動畫還沒到定位就點」的誤觸
> - 回傳格式：`clicked: ButtonName → ok: state 'active' satisfied after 120 ms`
> - Actionability 失敗格式：`error: actionability timeout (5000ms): occluded by LoadingMask`

### 輸入後等待

```bash
# 輸入文字後等待目標狀態
curl -s "http://localhost:${BRIDGE_PORT}/input-and-wait?text=MyAccount&waitTarget=LabelStatus&waitState=text-contains&waitValue=ok"
```

> [!IMPORTANT]
> - `/input-and-wait` **需要 PlayMode**，且需先用 `/click` 聚焦輸入框
> - 如果不帶 `waitTarget`，行為等同 `/input`
> - **輸入前自動跑 Visible + Enabled + Editable 檢查**（對應 Playwright `fill()` actionability）

### 文字搜尋

```bash
# 搜尋顯示文字包含「確認」的 GO（模糊匹配）
curl -s "http://localhost:${BRIDGE_PORT}/find-by-text?text=確認"

# 精確匹配
curl -s "http://localhost:${BRIDGE_PORT}/find-by-text?text=OK&exact=true"
```

> [!IMPORTANT]
> - 搜尋範圍：所有已載入 Scene + DontDestroyOnLoad 的 Text/UIText/TMP 元件
> - 預設模糊匹配（contains, case-insensitive），`exact=true` 為精確匹配

### Web-first Assertion（Playwright 風格）

`/expect` 自動 polling 至 timeout。通過回 `ok: <condition> satisfied after <ms>`，逾時回 `fail: <condition> not met after <timeoutMs> ms; last=<snapshot>`。

```bash
# 等面板可見（active + CanvasGroup alpha>0 + 非空 bbox）
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=Panel&condition=toBeVisible"

# 等物件隱藏（hidden = 偵測不到或不可見；對齊 Playwright `toBeHidden`）
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=LoadingMask&condition=toBeHidden"

# 等按鈕可互動
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=ConfirmButton&condition=toBeEnabled"

# 等 InputField 可編輯（非 readOnly）
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=AccountInput&condition=toBeEditable"

# 等 EventSystem 目前選取到此物件
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=AccountInput&condition=toBeFocused"

# 等 UIText 顯示文字 == "Lv.10"
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=LabelLevel&condition=toHaveText&value=Lv.10"

# 等 UIText 包含關鍵字
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=LabelStatus&condition=toContainText&value=Complete"

# 等 InputField 內容 == "hello"
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=AccountInput&condition=toHaveValue&value=hello"

# 等 selector 匹配數量 == 3（使用 component + parent 等 selector 收斂）
curl -s "http://localhost:${BRIDGE_PORT}/expect?parent=List&component=UIItem&condition=toHaveCount&value=3"

# 等 RectTransform 至少有一個角落落在螢幕內
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=FloatingTip&condition=toBeInViewport"

# 等 GameObject 存在（含 inactive）
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=Panel&condition=toBeAttached"

# 縮短/拉長 timeout
curl -s "http://localhost:${BRIDGE_PORT}/expect?name=Panel&condition=toBeVisible&timeoutMs=1500&pollMs=100"
```

> [!IMPORTANT]
> - Condition 一律 camelCase（`toBeVisible`，大小寫不敏感）
> - 預設 timeout 5000ms、poll 100ms（對齊 Playwright 預設）
> - 支援全套 selector：`name` / `path` / `parent` / `component` / `index`
> - 和 `/wait-for` 的差異：`/expect` 命名、snapshot 格式、多項 condition 都對齊 Playwright，不需要背 `state=active/missing/text-contains` 這套舊命名；`/wait-for` 保留給現有 caller 相容
> - 支援條件：`toBeAttached` / `toBeVisible` / `toBeHidden` / `toBeEnabled` / `toBeDisabled` / `toBeEditable` / `toBeFocused` / `toHaveText` / `toContainText` / `toHaveValue` / `toHaveCount` / `toBeInViewport`

### 失敗診斷（一站式）

`/diagnose` 把「失敗後要看的東西」一次打包回傳：Game View 截圖路徑、目標節點 dump + visible/enabled/editable 判定、最近 N 筆 log。取代「失敗後連打 `/screenshot` `/dump-target` `/log/recent` 三次」。

```bash
# 針對失敗的 selector 做完整診斷（預設 20 筆 log）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/diagnose?name=ButtonConfirm"

# 指定 hierarchy path
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/diagnose?path=Canvas/Panel/ButtonConfirm&logs=50"

# 純 log + 截圖（不指定 selector 時跳過 target dump）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/diagnose?logs=30"

# 跳過截圖（只要文字資訊，快）
curl -s --max-time 5 "http://localhost:${BRIDGE_PORT}/diagnose?name=ButtonConfirm&screenshot=false"
```

> [!IMPORTANT]
> - 截圖需 PlayMode，Edit Mode 下自動跳過並註記 `skipped (requires PlayMode)`
> - 典型流程：`/click-and-wait` / `/expect` 失敗 → `/diagnose?name=<失敗目標>` 取得完整現場
> - 回傳為純文字，方便直接貼進對話 / log

### Baseline 截圖比對（v17）

Session-level baseline：改 UI 前拍 baseline，改完後 diff。走 pixel-level 快速判定，差異明顯才動 Vision。詳細規範見 `skill-tool-vision-compare`。

```bash
# 存 baseline（需 PlayMode）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/baseline/save?name=DragonWarMain"

# 列出所有 baseline
curl -s "http://localhost:${BRIDGE_PORT}/baseline/list"

# 改 UI 後 diff（截當前 + pixel-compare baseline）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/baseline/diff?name=DragonWarMain"
# 回傳: verdict=identical / within-threshold / diff-detected，含 diffPercent/regions/vizPath

# 清掉 baseline（png + json + 差異視覺化一起刪）
curl -s "http://localhost:${BRIDGE_PORT}/baseline/delete?name=DragonWarMain"
```

> [!IMPORTANT]
> - baseline 存於 `Temp/AgentScreenshots/baselines/{name}.png`，**gitignored**，session-level，不進 repo。
> - `verdict=identical` / `within-threshold` 直接 PASS，不需動 Vision。
> - `verdict=diff-detected` 才 Read baseline/current/diffVisualization 三張 PNG 進 Vision 判語意。
> - diffVisualization 是紅色半透明覆蓋差異像素的 PNG，便於視覺定位。
> - `regions` 按 density 降序，最多 20 筆，每 cell 預設 32×32，density > 10% 才列入。

### 批次執行

```bash
# 一次送多個請求（POST）
curl -s -X POST "http://localhost:${BRIDGE_PORT}/batch" \
  -d '{"paths":["/status","/find?search=Canvas&by=name","/hierarchy?depth=1&limit=5"]}'
# 回傳 JSON: {"results": [{path, result}, ...]}

# 批次斷言：多條 /expect 組在一次 /batch 裡
curl -s -X POST "http://localhost:${BRIDGE_PORT}/batch" \
  -d '{"paths":["/expect?name=Panel&condition=toBeVisible","/expect?name=OKButton&condition=toBeEnabled","/expect?name=Label&condition=toContainText&value=完成"]}'
```

> [!IMPORTANT]
> - `/batch` 是循序執行（非平行）；每條 sub-request 獨立 timeout
> - 要做「一次 query 多個 selector」、「一次跑多條斷言」都組在 /batch 裡

### Scenario Runner

`/scenario/run` 是通用流程 runner。Bridge 只負責 DSL、step 執行、失敗診斷與 report；不要把特定業務流程（例如 DragonWarZone）寫成 Bridge route。

```bash
curl -s -X POST "http://localhost:${BRIDGE_PORT}/scenario/run" \
  -d '{
    "name":"schedule_smoke",
    "stopOnFailure":true,
    "diagnoseOnFailure":true,
    "defaultTimeoutMs":5000,
    "steps":[
      {"name":"health","action":"checkPlayMode"},
      {"name":"wait panel","action":"wait","selector":{"name":"UIDragonWarZoneCupSchedule"},"state":"active"},
      {"name":"title visible","action":"expect","selector":{"name":"LabelTitle"},"condition":"toBeVisible"},
      {"name":"close","action":"click","selector":{"name":"ButtonClose"}}
    ]
  }'
```

支援 action：

- `route`：直接執行既有 route，需提供 `path`，例如 `"/status"`。
- `checkPlayMode`：執行 `/playmode/check`，只有 `ready: yes` 算成功。
- `wait`：組 `/wait-for`，支援 `selector`、`state`、`value`、`type`。
- `expect`：組 `/expect`，支援 `selector`、`condition`、`value`。
- `click`：組 `/click`，支援完整 `selector` 或 `x` + `y`。
- `input`：組 `/input`，支援 `text`。
- `clickAndWait` / `inputAndWait`：組 `/click-and-wait` / `/input-and-wait`，支援 `waitTarget`、`waitState`、`waitValue`。
- `keypress`：組 `/keypress`，支援 `key`。
- `diagnose`：組 `/diagnose`。
- `baselineSave` / `baselineDiff`：組 `/baseline/save` / `/baseline/diff`，使用 `baseline` 或 step `name` 作為 baseline name。

回傳 JSON 包含 `ok`、`name`、`runId`、`runDir`、`logsPath`、`durationMs`、`failedStep`、`steps[]`、`diagnose`、`reportPath`、`artifacts[]`。每次 run 會建立 `Temp/AgentReports/scenarios/<runId>/`，report 寫成 `report.json`，最近 log 寫成 `logs.txt`，截圖與 baseline/current/diff 圖會複製到該 run 的 `artifacts/`。run-local artifact 檔名會加上 step index/name 前綴，例如 `001_shot_20260421_162718.png`。每個 step 也會回 step-level `artifacts[]`。

```bash
# 列出最近 Bridge 測試產物
curl -s "http://localhost:${BRIDGE_PORT}/artifacts/list?kind=all&limit=20"

# 只看 scenario reports
curl -s "http://localhost:${BRIDGE_PORT}/artifacts/list?kind=reports"

# 只看 scenario logs
curl -s "http://localhost:${BRIDGE_PORT}/artifacts/list?kind=logs"

# 只看 scenario run-local artifact copies
curl -s "http://localhost:${BRIDGE_PORT}/artifacts/list?kind=run-artifacts"
```

### RectTransform 查詢

回傳 JSON，包含 anchoredPosition、sizeDelta、anchor、pivot、hierarchy path、active 狀態等。

```bash
# 搜尋名稱含 "ButtonClose" 的物件
curl -s "http://localhost:${BRIDGE_PORT}/rect-transforms?q=ButtonClose"

# 列出所有 RectTransform（不帶 q 或 q 為空）
curl -s "http://localhost:${BRIDGE_PORT}/rect-transforms"
```

> [!IMPORTANT]
> - 搜尋範圍：所有已載入 Scene + DontDestroyOnLoad（含 inactive 物件）
> - 不需要 Play Mode，Edit Mode 也可查詢
> - 結果量大時建議帶 `q` 縮小範圍
> - `active` = `activeInHierarchy`，`selfActive` = `activeSelf`
> - 結果同時寫入 `Temp/AgentRectTransforms.json`

### 截圖

```bash
# 截取 Game View（需要 Play Mode）
curl -s --max-time 15 "http://localhost:${BRIDGE_PORT}/screenshot"
# 回傳: screenshot saved: C:\...\Temp\AgentScreenshots\20260312_111234.png

# 抓最新截圖到本地檔案
curl -s "http://localhost:${BRIDGE_PORT}/screenshot/get" --output game_view.png

# 抓指定路徑的截圖
curl -s "http://localhost:${BRIDGE_PORT}/screenshot/get?path=C:/path/to/screenshot.png" --output game_view.png
```

> [!TIP]
> 常見流程：先 `/screenshot` 截圖 → 再 `/screenshot/get` 抓圖到本地查看

### 執行 MenuItem

```bash
curl -s "http://localhost:${BRIDGE_PORT}/menu-item?path=Tools/Agent/Some%20Action"
```

### Component Reflection（遠端 Inspector）

透過 Reflection 直接讀寫 runtime 記憶體中的值，**不依賴 SerializedProperty**。

```bash
# 列出 GO 上所有 Component
curl -s "http://localhost:${BRIDGE_PORT}/component/list?name=Main%20Camera"

# 列出 Component 所有 field + property（含 rw/r 標示）
curl -s "http://localhost:${BRIDGE_PORT}/component/get?name=Main%20Camera&component=Camera"

# 讀取單一屬性
curl -s "http://localhost:${BRIDGE_PORT}/component/get?name=Main%20Camera&component=Transform&property=localPosition"

# 寫入屬性（複雜型別用 JSON，簡單型別直接傳值）
curl -s "http://localhost:${BRIDGE_PORT}/component/set?name=ObjName&component=Camera&property=backgroundColor&value=%7B%22r%22%3A0.2%2C%22g%22%3A0.3%2C%22b%22%3A0.8%2C%22a%22%3A1%7D"

# 簡單型別直接傳
curl -s "http://localhost:${BRIDGE_PORT}/component/set?name=ObjName&component=Camera&property=fieldOfView&value=75"
```

> [!IMPORTANT]
> - 名稱搜尋皆 **case-insensitive**（GO 名、Component 型別名、property 名）
> - 可見範圍：public field + `[SerializeField]` private field + public property
> - 繼承鏈遍歷到 `MonoBehaviour`/`Component` 前停止
> - **讀到的是 runtime 即時值**，不是 serialized data
> - 寫入後會 readback 確認，回傳實際設定後的值
> - 複雜型別（Vector2/3/4、Color、Quaternion 等）的 `value` 用 JSON 格式，需 URL encode
> - 同名 GO 回傳第一個匹配；同型別多個 Component 回傳第一個

## 注意事項

> [!WARNING]
> - Play Mode 進出觸發 domain reload，Server 中斷數秒後自動重啟（port 可能改變，需重新讀 `Temp/bridge_port.txt`）
> - `/refresh` 觸發 recompile，同樣導致短暫中斷，port 可能改變
> - Listener 意外死亡會自動偵測並重啟

> [!TIP]
> - `--max-time 5` 用於快速查詢，`--max-time 15` 用於耗時操作
> - 打不存在的端點會回傳所有端點列表

> [!CAUTION]
> **本專案資源全部走 AssetBundle，Prefab 不在本地專案目錄內。**
> - 不能用 Glob/Read 直接讀 `.prefab` 檔來查 RectTransform 或 UI 層級
> - 查 UI 佈局必須透過 Bridge 的 `/rect-transforms` 端點（需要 Play Mode 或 Scene 已載入）
> - 若 Bridge 無法連線，只能從 C# 程式碼的 enum 和 `GetChild` 索引推斷層級關係

## 相關檔案

- `Assets/Editor/UnityBridgeServer.cs` — HTTP Server 實作（所有功能統一在此）
