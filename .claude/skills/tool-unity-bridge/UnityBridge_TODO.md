# Unity Bridge TODO

目的：參考 Playwright 類工具的實用能力，補強 Unity Bridge 在「AI 可穩定操作 UI / 做自動化測試」這件事上的缺口。

## 原則

- 不把 Unity Bridge 做成業務邏輯入口。
- 優先補「穩定性」而不是再加更多單純 click API。
- Bridge 提供通用能力；場景流程放在測試 helper 或 scenario runner。
- **以 Playwright 為藍本**：Playwright 已是成熟的測試框架，本 TODO 的設計盡量貼近它，不憑空發明新概念。凡是 Playwright 有的命名、檢查項、行為，優先照搬。

## Playwright 哲學（本 TODO 的設計依據）

以下內容摘自 Playwright 官方文件 <https://playwright.dev/docs/actionability> / `/locators` / `/test-assertions`，Unity 對應為我方翻譯。

### 1. Actionability checks（5 項，action 執行前自動跑）

Playwright 官方定義（verbatim）：

- **Visible**：「Element is considered visible when it has non-empty bounding box and does not have `visibility:hidden` computed style.」
- **Stable**：「Element is considered stable when it has maintained the same bounding box for at least two consecutive animation frames.」
- **Receives Events**：「Element is considered receiving pointer events when it is the hit target of the pointer event at the action point.」
- **Enabled**：「Element is considered enabled when it is not disabled.」
- **Editable**：「Element is considered editable when it is enabled and is not readonly.」

### 2. 每個 action 需要的 checks（照 Playwright 規格）

| Action | Visible | Stable | Receives Events | Enabled | Editable |
|---|---|---|---|---|---|
| click / dblclick / check / uncheck / setChecked / tap | ✓ | ✓ | ✓ | ✓ | ✓ |
| hover / dragTo | ✓ | ✓ | ✓ | ✓ | — |
| fill / clear | ✓ | — | — | ✓ | ✓ |
| screenshot | ✓ | ✓ | — | — | — |
| focus / press / dispatchEvent / setInputFiles | — | — | — | — | — |

### 3. Auto-waiting = actionability + timeout

Playwright 的 auto-waiting 不是「sleep 一下」而是「在 timeout 內不停重試 actionability」。條件滿足即動手，不滿足就 `TimeoutError`。使用者永遠不呼叫 `waitForStable`，是內建在 action 裡。

### 4. `force` 選項

「disables non-essential actionability checks」。只有明確知道風險時才用。

### 5. Locator = lazy + strict

- Lazy：「every time a locator is used for an action, an up-to-date DOM element is located in the page.」→ 永遠重新 query，不 cache。
- Strict：匹配 >1 個元素時丟例外。要拿多個需明確呼叫 `first()` / `last()` / `nth()`。

### 6. Web-first assertions（自動 retry，預設 5s timeout）

「re-testing the element ... until the condition is met or until the timeout is reached.」

常見：`toBeVisible` / `toBeHidden` / `toBeEnabled` / `toBeDisabled` / `toBeChecked` / `toBeEditable` / `toBeAttached` / `toBeFocused` / `toBeInViewport` / `toHaveText` / `toContainText` / `toHaveValue` / `toHaveCount` / `toHaveAttribute` / `toHaveClass`。

### 7. 使用者面向的 locator（優先於 CSS/XPath）

`getByRole` / `getByText` / `getByLabel` / `getByPlaceholder` / `getByAltText` / `getByTitle` / `getByTestId`，再配 `filter` / `nth` / `first` / `last` / `and` / `or`。

### Unity 對應（我方翻譯）

| Playwright | Unity Bridge 對應 |
|---|---|
| Visible | `activeInHierarchy` + `CanvasGroup.alpha>0` + RectTransform 有非零 rect |
| Stable（連續 2 frame bbox 不變）| 連續 2 frame `RectTransform.rect` + `position` 不變；AERunner/Tween 未在播 |
| Receives Events | GraphicRaycaster 射線命中目標、前面無遮擋、`Button.IsInteractable()==true` |
| Enabled | `Selectable.interactable==true` 且祖鏈 CanvasGroup 未 block |
| Editable | `InputField.readOnly==false` 且 enabled |
| Lazy locator | 每次 action 前重找 GameObject，不存 InstanceID |
| Strict mode | 同名多個時拒絕操作，強制指定 index / path |
| networkidle | 連續 N ms 無 MessagePacket 收發 |

## P0

- [x] 補 `waitFor` 類能力
  - [x] 等 GameObject 出現
  - [x] 等 GameObject active / inactive
  - [x] 等指定文字變化
  - [x] 等 loading mask 消失（用 state=missing）
  - [x] 等 log 出現關鍵字

- [x] 補查詢型 API，降低 screenshot 猜測
  - [x] `exists`
  - [x] `isVisible`
  - [x] `isInteractable`
  - [x] `getText`
  - [x] `getSelectedObject`
  - [x] `getActiveWindow`

- [x] 補更穩定的 selector
  - [x] 支援 hierarchy path
  - [x] 支援 parent + child 條件
  - [x] 支援 component type + object name
  - [x] 支援 text-based selector（/find-by-text）
  - [x] 同名物件時可指定 index 或完整 path

- [x] 把 Playwright 的 actionability 內建進現有 action（取代自寫 `waitForStableUI`）（Bridge v11）
  - [x] `clickAndWait`
  - [x] `inputAndWait`
  - [x] 在 `click` / `clickAndWait` 前置 4 項檢查：Visible → Stable → Receives Events → Enabled（click 不需 Editable，依上表）
  - [x] 在 `input` / `inputAndWait` 前置 3 項：Visible + Enabled + Editable
  - [x] 所有前置檢查跑在 timeout polling 內；timeout 才丟錯（對齊 Playwright auto-waiting）
  - [x] 提供 `force=true` 旗標跳過 non-essential checks（對齊 Playwright `force`）
  - 設計準則：使用者不該需要呼叫 `waitForStableUI`，穩定性內建在 action 裡

## P1

- [x] 補 web-first assertion（命名對齊 Playwright，自動 retry 至 timeout，預設 5s）— Bridge v12
  - [x] `toBeAttached` — GameObject 存在於場景
  - [x] `toBeVisible` / `toBeHidden` — 對應 Visible 檢查
  - [x] `toBeEnabled` / `toBeDisabled` — 對應 Enabled 檢查
  - [x] `toBeEditable` — 對應 Editable 檢查
  - [x] `toBeFocused` — EventSystem 當前選中
  - [x] `toHaveText` / `toContainText` — UIText 內容
  - [x] `toHaveValue` — InputField 內容
  - [x] `toHaveCount` — selector 匹配數
  - [x] `toBeInViewport` — 在 Canvas 可視範圍內
  - [ ] 自訂補充：`logNotContains`（Playwright 無對應；語意與 log buffer append-only 有衝突，先擱置）
  - 統一端點：`/expect?condition=toBeX&value=...&timeoutMs=5000`
  - 設計準則：全部 assertion 自動 polling，不需使用者手動 `waitFor`

- [x] 補 failure diagnostics（Bridge v14，端點 `/diagnose`）
  - [x] 截圖（PlayMode 才拍，Edit Mode 自動標註跳過）
  - [x] 附最近 N 筆 log（預設 20，可調 `logs=N`）
  - [x] 附目標節點 hierarchy path
  - [x] 附 target component dump
  - [x] 附 visible / enabled / editable 判定原因
  - 設計準則：不綁進每個 action 失敗路徑，維持 action 輕量；使用者失敗後手動打 `/diagnose?name=<target>` 取得完整現場

- [x] 補批次查詢與批次斷言（沿用既有 `/batch`，不新增端點）
  - [x] 一次查多個 selector：`/batch` + `/exists` / `/dump-target`
  - [x] 一次回傳多個 query 結果：JSON `{"results":[...]}`
  - [x] 批次斷言：`/batch` + 多條 `/expect`
  - 設計決策：與其另開端點，直接用 `/batch` 組 `/expect` 即可；Playwright 本身也沒有專屬 batchAssert API

- [x] 補 Play Mode 測試前置檢查（Bridge v16，端點 `/playmode/check`）
  - [x] EventSystem 是否存在（並警告多個 instance）
  - [x] Canvas 數量
  - [x] Camera.main 是否存在
  - [x] GraphicRaycaster 覆蓋率
  - [x] 當前 focused GameObject
  - [x] **Active GUIWindow 列表**（區分「真實 gameplay」vs「login/loading 畫面」——只有 Unity prereqs 齊全不夠）
  - [x] **近期 log 活動量**（偵測遊戲 loop 是否 stall；預設觀察窗 5 秒）
  - [x] `ready: no` 時在首行列出失敗原因（可直接 grep）
  - 教訓：只驗 Unity-level 會把 login 畫面誤判為 ready；必須加上 game-level 訊號

## P2

- [x] 補 scenario runner（Bridge v18，端點 `POST /scenario/run`）
  - [x] 用 JSON DSL 描述步驟
  - [x] click / input / wait / assert / route / diagnose / baseline 可串成流程
  - [x] 失敗時輸出完整 step report，並可自動附 `/diagnose`
  - [x] report 寫入 `Temp/AgentReports/scenarios/`
  - [x] Bridge v19：`click` / `clickAndWait` 底層 route 對齊完整 selector（`name` / `path` / `parent` / `component` / `index`）

- [x] 補 baseline screenshot workflow（Bridge v17，端點 `/baseline/save` `/list` `/diff` `/delete`）
  - [x] 建 baseline — `/baseline/save?name=X`，PNG + JSON metadata 存 `Temp/AgentScreenshots/baselines/`
  - [x] 比對新截圖 — `/baseline/diff?name=X` 走 hash short-circuit + pixel-delta + grid 聚合，三段 verdict：`identical` / `within-threshold` / `diff-detected`
  - [x] 回報差異區域 — `regions[]` 以 32×32 grid 聚合 density>10% 的格子，並產出紅色覆蓋的 `compare_diff_X.png`
  - 設計準則：baseline 為 session-level（Temp/ gitignored，不進 repo、不綁 CI）；`identical` / `within-threshold` 直接 PASS 不動 Vision，只有 `diff-detected` 才送三張 PNG 進 Vision 判語意

- [ ] 補 test artifact 管理
  - [x] scenario report 回傳頂層 `artifacts[]`
  - [x] 每個 scenario step 回傳 step-level `artifacts[]`
  - [x] `/artifacts/list` 可列出 screenshots / baselines / scenario reports
  - [x] 每次 scenario run 建立獨立結果資料夾
  - [x] scenario artifact 複製到 run-local `artifacts/`
  - [x] run-local artifact 檔名包含 step index/name
  - [x] log 輸出到 run-local `logs.txt`
  - [x] `/artifacts/list?kind=logs` 可列出 scenario logs
  - [x] 每次測試 run 的結果資料夾
  - [x] scenario report 已落地於 `Temp/AgentReports/scenarios/<runId>/report.json`

## 不建議直接加進 Bridge

- [ ] 不做大量業務語意 route
  - 例如 `/openDragonWarZoneMain`
  - 這種應放在專案測試 helper，不應污染通用 bridge

- [ ] 不把所有流程都做成 screenshot 驅動
  - screenshot 適合驗證畫面
  - 不適合取代 query / assert / wait

- [ ] 不只靠 object name 當 selector
  - UI 同名物件與動態 clone 很常見，長期不穩
  - Playwright 對應：優先 `getByRole` / `getByText` / `getByLabel` 這類使用者面向的定位，CSS/XPath 是 fallback。我方對應：優先 `find-by-text`、component type + name，hierarchy path 當 fallback

## 建議實作順序

1. `waitFor`
2. query API
3. selector 強化
4. **把 actionability 5 檢查內建進 click / input**（對齊 Playwright auto-waiting，取代原本的 `waitForStableUI` 規劃）
5. **web-first assertion（`toBe*` / `toHave*`，自動 retry）**
6. diagnostics
7. scenario runner

## 可先做的最小版本

- [ ] `waitFor?name=...&state=active`
- [x] `waitFor?name=...&state=active`
- [x] `getText?name=...`
- [x] `exists?name=...`
- [x] `clickAndWait?name=...&wait=active&target=...`
- [x] `dumpTarget?name=...`

## 結論

- 第一輪：`exists`、`get-text`、`dump-target`、`wait-for`。
- 第二輪：`is-visible`、`is-interactable`、`get-selected`、`get-active-window`、`click-and-wait`、`log/clear`。
- 第三輪：`wait-for state=log`、`wait-for type=Log|Warning|Error|Exception|Assert`、`input-and-wait`、`find-by-text`、版本號 v9、`/input` 支援 UIEmojiInput。
- 第四輪：selector 強化（parent+child、component+name、index）、版本號 v10、`/status` 回傳 version。
- 下一步：**把 Playwright actionability 5 檢查內建進 click/input**（取代原本規劃的 `waitForStableUI`）、web-first assertion、diagnostics。
