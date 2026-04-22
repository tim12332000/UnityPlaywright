# UnityPlaywright

Languages: English | [繁體中文](README.zh-TW.md)

UnityPlaywright is a Unity Editor HTTP bridge for driving and inspecting Play Mode from external automation tools. It exposes localhost endpoints for Play Mode control, UI actions, logs, screenshots, assertions, scenario runs, and test artifacts.

The bridge implementation lives in `Assets/Editor/UnityBridgeServer.cs`.

## Requirements

- Unity `6000.0.60f1`
- Windows Editor for `/keypress`, because it uses `user32.dll`
- Unity UI package (`com.unity.ugui`)
- Input System package (`com.unity.inputsystem`)

## How It Starts

`UnityBridgeServer` is marked with `[InitializeOnLoad]`, so Unity starts it automatically after the Editor loads or scripts reload.

The server binds to `http://localhost:6544/` by default. If the port is busy, it retries up to `6553`. The active port is written to:

```text
Temp/bridge_port.txt
```

Check the bridge:

```bash
PORT=$(cat Temp/bridge_port.txt)
curl "http://localhost:${PORT}/status"
```

Expected response:

```text
ok. version=v22 playmode=False
```

## Common Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /status` | Bridge version and Play Mode status |
| `GET /refresh` | Run `AssetDatabase.Refresh()` |
| `GET /playmode/enter` | Enter Play Mode |
| `GET /playmode/exit` | Exit Play Mode |
| `GET /playmode/check` | Runtime health check for EventSystem, Canvas, Camera, windows, and logs |
| `GET /log/recent` | Read recent Unity logs |
| `GET /log/search` | Search Unity logs |
| `GET /log/clear` | Clear the in-memory bridge log buffer |
| `GET /hierarchy` | Dump loaded scene hierarchy |
| `GET /find` | Find GameObjects by name, tag, layer, or component |
| `GET /exists` | Check if a selector resolves |
| `GET /dump-target` | Inspect one GameObject, including path, state, text, and components |
| `GET /get-text` | Read text from `Text`, TMP-like, or custom text components |
| `GET /click` | Click a UI target or screen coordinate |
| `GET /input` | Set text on the currently selected input field |
| `GET /keypress` | Send `enter` or `esc` |
| `GET /wait-for` | Poll for object, text, active state, or log state |
| `GET /expect` | Playwright-style retrying assertions |
| `GET /diagnose` | Capture screenshot, target dump, and recent logs |
| `GET /screenshot` | Capture Game View screenshot |
| `GET /screenshot/get` | Return a screenshot PNG |
| `GET /baseline/save` | Save a baseline screenshot |
| `GET /baseline/diff` | Compare current screenshot against a baseline |
| `GET /artifacts/list` | List screenshots, baselines, scenario reports, and logs |
| `POST /batch` | Run multiple bridge routes |
| `POST /scenario/run` | Run a JSON scenario and write a report |

## Selectors

Most UI and query endpoints accept these selector fields:

| Parameter | Description |
| --- | --- |
| `name` | Case-insensitive partial GameObject name |
| `path` | Hierarchy path, such as `Canvas/LoginPanel/ButtonStart` |
| `parent` | Parent path or name used to narrow search |
| `component` | Component type name, such as `Button` or `TMP_InputField` |
| `index` | Zero-based match index |
| `inactive` | Set `false` to exclude inactive objects |

Examples:

```bash
curl "http://localhost:${PORT}/exists?path=Canvas/LoginPanel/ButtonStart"
curl "http://localhost:${PORT}/dump-target?parent=Canvas/LoginPanel&name=Start&component=Button"
curl "http://localhost:${PORT}/expect?name=Login&condition=toBeVisible&timeoutMs=5000"
```

## UI Automation

`/click` and `/input` perform Playwright-style actionability checks by default:

- visible
- stable
- receives events
- enabled
- editable, for input actions

Use `force=true` to bypass those checks when you intentionally want a direct action.

```bash
curl "http://localhost:${PORT}/click?path=Canvas/LoginPanel/ButtonStart"
curl "http://localhost:${PORT}/input?text=test-user"
curl "http://localhost:${PORT}/keypress?key=enter"
```

## Scenario Runner

`POST /scenario/run` accepts a JSON scenario with ordered steps. The bridge writes reports and copied artifacts under:

```text
Temp/AgentReports/scenarios/
```

Example:

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

Run it:

```bash
curl -X POST "http://localhost:${PORT}/scenario/run" \
  -H "Content-Type: application/json" \
  --data @scenario.json
```

## Baseline Screenshots

Baseline files are stored under:

```text
Temp/AgentScreenshots/baselines/
```

Create and compare a baseline:

```bash
curl "http://localhost:${PORT}/baseline/save?name=login"
curl "http://localhost:${PORT}/baseline/diff?name=login"
```

Diff responses include a verdict such as `identical`, `within-threshold`, or `diff-detected`.

## Local Validation

This repository includes Unity-generated project files. A quick syntax check can be run with:

```powershell
dotnet build Assembly-CSharp-Editor.csproj --no-restore
```

Unity may still be required for full runtime validation because bridge behavior depends on the Editor, Play Mode, scenes, Game View, EventSystem, and UI raycasting.

## Notes

- The bridge is intended for local Editor automation and binds to localhost.
- `Temp/`, `Library/`, `Logs/`, and `UserSettings/` are ignored by git.
- Scenario reports, screenshots, and baselines are runtime artifacts and are not committed by default.
