# UnityPlaywright

語言：[English](README.md) | 繁體中文

UnityPlaywright 是一個 Unity Editor HTTP Bridge，用來讓外部自動化工具控制與檢查 Unity Play Mode。它透過 localhost endpoint 提供 Play Mode 控制、UI 操作、Log 查詢、截圖、斷言、Scenario 執行與測試 artifact 收集。

Bridge 主要實作位於 `Assets/Editor/UnityBridgeServer.cs`。

## 需求

- Unity `6000.0.60f1`
- `/keypress` 需要 Windows Editor，因為它使用 `user32.dll`
- Unity UI package (`com.unity.ugui`)
- Input System package (`com.unity.inputsystem`)

## 啟動方式

`UnityBridgeServer` 使用 `[InitializeOnLoad]`，所以 Unity Editor 載入或 script reload 後會自動啟動。

Server 預設綁定：

```text
http://localhost:6544/
```

如果 port 被占用，會一路嘗試到 `6553`。目前使用的 port 會寫入：

```text
Temp/bridge_port.txt
```

檢查 Bridge：

```bash
PORT=$(cat Temp/bridge_port.txt)
curl "http://localhost:${PORT}/status"
```

預期回應：

```text
ok. version=v22 playmode=False
```

## 常用 Endpoints

| Endpoint | 用途 |
| --- | --- |
| `GET /status` | 查看 Bridge 版本與 Play Mode 狀態 |
| `GET /refresh` | 執行 `AssetDatabase.Refresh()` |
| `GET /playmode/enter` | 進入 Play Mode |
| `GET /playmode/exit` | 離開 Play Mode |
| `GET /playmode/check` | 檢查 EventSystem、Canvas、Camera、視窗與 Log 等 runtime 狀態 |
| `GET /log/recent` | 讀取最近 Unity logs |
| `GET /log/search` | 搜尋 Unity logs |
| `GET /log/clear` | 清除 Bridge 記憶體內的 log buffer |
| `GET /hierarchy` | 輸出已載入 scene hierarchy |
| `GET /find` | 依 name、tag、layer 或 component 搜尋 GameObject |
| `GET /exists` | 檢查 selector 是否找到物件 |
| `GET /dump-target` | 檢查單一 GameObject 的 path、狀態、文字與 components |
| `GET /get-text` | 讀取 `Text`、TMP-like 或自訂文字 component 的文字 |
| `GET /click` | 點擊 UI 目標或螢幕座標 |
| `GET /input` | 對目前選取的 input field 設定文字 |
| `GET /keypress` | 送出 `enter` 或 `esc` |
| `GET /wait-for` | 輪詢物件、文字、active 狀態或 log 狀態 |
| `GET /expect` | Playwright 風格的 retrying assertions |
| `GET /diagnose` | 擷取 screenshot、target dump 與最近 logs |
| `GET /screenshot` | 擷取 Game View screenshot |
| `GET /screenshot/get` | 回傳 screenshot PNG |
| `GET /baseline/save` | 儲存 baseline screenshot |
| `GET /baseline/diff` | 比對目前 screenshot 與 baseline |
| `GET /artifacts/list` | 列出 screenshots、baselines、scenario reports 與 logs |
| `POST /batch` | 一次執行多個 Bridge routes |
| `POST /scenario/run` | 執行 JSON scenario 並寫出 report |

## Selectors

多數 UI 與查詢 endpoint 都接受以下 selector 欄位：

| 參數 | 說明 |
| --- | --- |
| `name` | 不分大小寫的 GameObject 名稱片段 |
| `path` | Hierarchy path，例如 `Canvas/LoginPanel/ButtonStart` |
| `parent` | 用來縮小搜尋範圍的 parent path 或名稱 |
| `component` | Component type name，例如 `Button` 或 `TMP_InputField` |
| `index` | 從 0 開始的 match index |
| `inactive` | 設為 `false` 時排除 inactive objects |

範例：

```bash
curl "http://localhost:${PORT}/exists?path=Canvas/LoginPanel/ButtonStart"
curl "http://localhost:${PORT}/dump-target?parent=Canvas/LoginPanel&name=Start&component=Button"
curl "http://localhost:${PORT}/expect?name=Login&condition=toBeVisible&timeoutMs=5000"
```

## UI 自動化

`/click` 與 `/input` 預設會做 Playwright 風格的 actionability checks：

- visible
- stable
- receives events
- enabled
- editable，僅 input 類操作需要

如果你明確想直接操作，可以加上 `force=true` 跳過檢查。

```bash
curl "http://localhost:${PORT}/click?path=Canvas/LoginPanel/ButtonStart"
curl "http://localhost:${PORT}/input?text=test-user"
curl "http://localhost:${PORT}/keypress?key=enter"
```

## Scenario Runner

`POST /scenario/run` 接收一個包含 ordered steps 的 JSON scenario。Bridge 會將 reports 與複製後的 artifacts 寫到：

```text
Temp/AgentReports/scenarios/
```

範例：

```json
{
  "name": "login_smoke",
  "stopOnFailure": true,
  "diagnoseOnFailure": true,
  "defaultTimeoutMs": 5000,
  "steps": [
    {
      "name": "playmode health",
      "action": "checkPlayMode"
    },
    {
      "name": "wait login panel",
      "action": "expect",
      "selector": { "name": "LoginPanel" },
      "condition": "toBeVisible"
    },
    {
      "name": "click start",
      "action": "click",
      "selector": { "path": "Canvas/LoginPanel/ButtonStart" }
    }
  ]
}
```

執行：

```bash
curl -X POST "http://localhost:${PORT}/scenario/run" \
  -H "Content-Type: application/json" \
  --data @scenario.json
```

## Baseline Screenshots

Baseline 檔案會存放在：

```text
Temp/AgentScreenshots/baselines/
```

建立與比對 baseline：

```bash
curl "http://localhost:${PORT}/baseline/save?name=login"
curl "http://localhost:${PORT}/baseline/diff?name=login"
```

Diff 回應會包含 `identical`、`within-threshold` 或 `diff-detected` 等 verdict。

## 本機驗證

這個 repo 包含 Unity 產生的 project files。可以用以下指令做快速語法檢查：

```powershell
dotnet build Assembly-CSharp-Editor.csproj --no-restore
```

完整 runtime 驗證仍需要 Unity Editor，因為 Bridge 行為依賴 Editor、Play Mode、Scene、Game View、EventSystem 與 UI raycasting。

## 注意事項

- Bridge 設計用途是本機 Editor 自動化，並綁定 localhost。
- `Temp/`、`Library/`、`Logs/` 與 `UserSettings/` 預設不進 git。
- Scenario reports、screenshots 與 baselines 都是 runtime artifacts，預設不提交。
