using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class UnityBridgeServer
{
    static HttpListener listener;
    static Thread listenerThread;
    static readonly List<(Func<string> action, ManualResetEvent done, string[] result)> pendingActions
        = new List<(Func<string>, ManualResetEvent, string[])>();
    const int BasePort = 6544;
    const int MaxPortRetry = 10;
    const string BridgeVersion = "v22";
    static int _activePort = BasePort;
    const int LogCapacity = 10000;
    const string ScreenshotDir = "Temp/AgentScreenshots";
    const string BaselineDir = ScreenshotDir + "/baselines";
    const string AgentReportDir = "Temp/AgentReports";
    const string ScenarioReportDir = AgentReportDir + "/scenarios";
    static bool _stopping;
    static bool _listenerDied;
    static int _retryCount;
    static double _nextRetryTime;


    static readonly Dictionary<string, Func<HttpListenerRequest, string>> routes
        = new Dictionary<string, Func<HttpListenerRequest, string>>();

    // Binary routes: return (byte[] data, string contentType) instead of string
    static readonly Dictionary<string, Func<HttpListenerRequest, (byte[] data, string contentType)>> binaryRoutes
        = new Dictionary<string, Func<HttpListenerRequest, (byte[] data, string contentType)>>();

    struct LogEntry
    {
        public string message;
        public string stackTrace;
        public LogType type;
        public DateTime time;
    }

    struct ArtifactInfo
    {
        public string kind;
        public string path;
        public long sizeBytes;
        public DateTime modifiedUtc;
    }

    class ScenarioRunContext
    {
        public string runId;
        public string runDir;
        public string artifactDir;
        public string reportPath;
        public string logPath;
        public readonly Dictionary<string, string> copiedArtifacts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static readonly LinkedList<LogEntry> logBuffer = new LinkedList<LogEntry>();
    static readonly object logLock = new object();

    struct SelectorQuery
    {
        public string name;
        public string path;
        public string parent;
        public string component;
        public int index;
        public bool includeInactive;

        public bool HasSelector
        {
            get
            {
                return !string.IsNullOrEmpty(name) ||
                       !string.IsNullOrEmpty(path) ||
                       !string.IsNullOrEmpty(parent) ||
                       !string.IsNullOrEmpty(component);
            }
        }
    }

    [Serializable]
    class ScenarioRequest
    {
        public string name;
        public bool stopOnFailure;
        public bool diagnoseOnFailure;
        public int defaultTimeoutMs;
        public int defaultPollMs;
        public ScenarioStep[] steps;
    }

    [Serializable]
    class ScenarioStep
    {
        public string name;
        public string action;
        public string path;
        public ScenarioSelector selector;
        public string state;
        public string value;
        public string type;
        public string condition;
        public string text;
        public string key;
        public string baseline;
        public string waitTarget;
        public string waitState;
        public string waitValue;
        public string x;
        public string y;
        public bool force;
        public bool screenshot;
        public int timeoutMs;
        public int pollMs;
        public int logs;
        public int logWindowSec;
        public int gridSize;
        public int perPixelThreshold;
        public float identicalThreshold;
    }

    [Serializable]
    class ScenarioSelector
    {
        public string name;
        public string path;
        public string parent;
        public string component;
        public int index;
        public bool inactive;
    }

    // Playwright-style actionability checks.
    // Ref: https://playwright.dev/docs/actionability
    [Flags]
    enum Actionability
    {
        None = 0,
        Visible = 1 << 0,         // non-empty bbox, not hidden
        Stable = 1 << 1,          // same bbox for 2 consecutive polls
        ReceivesEvents = 1 << 2,  // hit target of pointer event at action point
        Enabled = 1 << 3,         // not disabled
        Editable = 1 << 4,        // enabled and not readonly

        // Per-action bundles matching Playwright's table.
        Click = Visible | Stable | ReceivesEvents | Enabled,
        Fill = Visible | Enabled | Editable,
    }

    static UnityBridgeServer()
    {
        Application.logMessageReceived += OnLogReceived;
        RegisterBuiltInRoutes();
        // Defer start 0.1s to let old socket fully release after domain reload
        _nextRetryTime = EditorApplication.timeSinceStartup + 0.1;
        EditorApplication.update += RetryTick;
        EditorApplication.update += PumpMainThread;
        EditorApplication.quitting += Stop;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
    }

    static void OnLogReceived(string message, string stackTrace, LogType type)
    {
        lock (logLock)
        {
            if (logBuffer.Count >= LogCapacity)
                logBuffer.RemoveFirst();
            logBuffer.AddLast(new LogEntry
            {
                message = message,
                stackTrace = stackTrace,
                type = type,
                time = DateTime.Now
            });
        }
    }

    // --- Public API ---

    public static void RegisterRoute(string path, Func<HttpListenerRequest, string> handler)
    {
        routes[path.ToLower()] = handler;
    }

    public static string RunOnMainThread(Func<string> action)
    {
        var done = new ManualResetEvent(false);
        var result = new string[] { "" };

        lock (pendingActions)
        {
            pendingActions.Add((action, done, result));
        }

        done.WaitOne(60000);
        return result[0];
    }

    // --- Built-in general routes ---

    static void RegisterBuiltInRoutes()
    {
        RegisterRoute("/status", req =>
            RunOnMainThread(() => "ok. version=" + BridgeVersion + " playmode=" + EditorApplication.isPlaying)
        );

        RegisterRoute("/refresh", req =>
            RunOnMainThread(() =>
            {
                AssetDatabase.Refresh();
                return "refresh triggered, recompiling...";
            })
        );

        RegisterRoute("/playmode/enter", req =>
            RunOnMainThread(() =>
            {
                if (EditorApplication.isPlaying) return "already in PlayMode";
                EditorApplication.isPlaying = true;
                return "entering PlayMode";
            })
        );

        RegisterRoute("/playmode/exit", req =>
            RunOnMainThread(() =>
            {
                if (!EditorApplication.isPlaying) return "not in PlayMode";
                EditorApplication.isPlaying = false;
                return "exiting PlayMode";
            })
        ); 

        RegisterRoute("/log/search", req =>
        {
            string q = req.QueryString["q"];
            if (string.IsNullOrEmpty(q)) return "error: missing q param. usage: /log/search?q=keyword&type=Error&count=50&compact=true";
            string typeFilter = req.QueryString["type"];
            int.TryParse(req.QueryString["count"], out int count);
            if (count <= 0) count = 50;
            bool compact = string.Equals(req.QueryString["compact"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["offset"], out int offset);

            lock (logLock)
            {
                var matches = logBuffer.AsEnumerable().Reverse()
                    .Where(e =>
                        e.message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (!string.IsNullOrEmpty(e.stackTrace) &&
                         e.stackTrace.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
                if (!string.IsNullOrEmpty(typeFilter) && Enum.TryParse<LogType>(typeFilter, true, out var lt))
                    matches = matches.Where(e => e.type == lt);
                var list = matches.Skip(offset).Take(count).ToList();
                if (list.Count == 0) return "no matches";
                return string.Join("\n\n", list.Select((e, i) => FormatLogEntry(e, offset + i, compact)));
            }
        });

        RegisterRoute("/log/recent", req =>
        {
            int.TryParse(req.QueryString["count"], out int count);
            if (count <= 0) count = 30;
            string typeFilter = req.QueryString["type"];
            bool compact = string.Equals(req.QueryString["compact"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["offset"], out int offset);

            lock (logLock)
            {
                var entries = logBuffer.AsEnumerable().Reverse().AsEnumerable();
                if (!string.IsNullOrEmpty(typeFilter) && Enum.TryParse<LogType>(typeFilter, true, out var lt))
                    entries = entries.Where(e => e.type == lt);
                var list = entries.Skip(offset).Take(count).ToList();
                if (list.Count == 0) return "no logs";
                return string.Join("\n\n", list.Select((e, i) => FormatLogEntry(e, offset + i, compact)));
            }
        });

        RegisterRoute("/log/clear", req =>
        {
            lock (logLock)
            {
                int count = logBuffer.Count;
                logBuffer.Clear();
                return "cleared " + count + " log entries";
            }
        });

        // --- Routes migrated from AgentUnityBridge ---

        RegisterRoute("/menu-item", req =>
        {
            string path = req.QueryString["path"];
            if (string.IsNullOrEmpty(path)) return "error: missing path param. usage: /menu-item?path=Tools/Something";
            return RunOnMainThread(() =>
            {
                bool executed = EditorApplication.ExecuteMenuItem(path);
                return executed ? "executed: " + path : "error: menu item not found: " + path;
            });
        });

        RegisterRoute("/rect-transforms", req =>
        {
            string q = req.QueryString["q"] ?? "";
            return RunOnMainThread(() => FindRectTransforms(q));
        });

        RegisterRoute("/screenshot", req =>
        {
            string outputPath = CaptureScreenshotAndWait();
            if (outputPath.StartsWith("error:")) return outputPath;
            return "screenshot saved: " + outputPath;
        });

        // --- Screenshot get (binary PNG) ---

        binaryRoutes["/screenshot/get"] = req =>
        {
            string filePath = req.QueryString["path"];
            if (string.IsNullOrEmpty(filePath))
            {
                // Find the latest screenshot in Temp/AgentScreenshots/
                string dir = Path.GetFullPath(ScreenshotDir);
                if (!Directory.Exists(dir))
                    return (Encoding.UTF8.GetBytes("error: no screenshot directory found"), "text/plain; charset=utf-8");
                var files = Directory.GetFiles(dir, "*.png").OrderByDescending(f => File.GetLastWriteTime(f)).ToArray();
                if (files.Length == 0)
                    return (Encoding.UTF8.GetBytes("error: no screenshot found"), "text/plain; charset=utf-8");
                filePath = files[0];
            }

            if (!File.Exists(filePath))
                return (Encoding.UTF8.GetBytes("error: file not found: " + filePath), "text/plain; charset=utf-8");

            byte[] data = File.ReadAllBytes(filePath);
            return (data, "image/png");
        };

        // --- Click simulation ---

        RegisterRoute("/click", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            string xStr = req.QueryString["x"];
            string yStr = req.QueryString["y"];
            bool force = string.Equals(req.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["timeoutMs"], out int acTimeout);
            if (acTimeout <= 0) acTimeout = 5000;
            int.TryParse(req.QueryString["pollMs"], out int acPoll);
            if (acPoll <= 0) acPoll = 100;

            bool hasX = !string.IsNullOrEmpty(xStr);
            bool hasY = !string.IsNullOrEmpty(yStr);
            bool hasCoordinates = hasX && hasY;
            if (!selector.HasSelector && !hasCoordinates)
                return "error: missing params. usage: /click?name=ObjName[&force=true] OR /click?path=Canvas/Panel/Button OR /click?parent=Canvas/Panel&name=Button OR /click?component=UIButton&index=0 OR /click?x=500&y=300";
            if (hasX != hasY)
                return "error: coordinate click requires both x and y";

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            // Actionability polling only applies when a selector target is given; coordinate
            // clicks skip checks (mirrors Playwright: x/y clicks bypass actionability).
            if (!force && selector.HasSelector)
            {
                string failReason = CheckActionability(
                    () => FindGameObject(selector),
                    Actionability.Click, acTimeout, acPoll);
                if (failReason != null) return "error: " + failReason;
            }

            return RunOnMainThread(() => hasCoordinates
                ? ExecuteClick(null, "coordinates", xStr, yStr)
                : ExecuteClick(FindGameObject(selector), DescribeSelector(selector), null, null));
        });

        // --- Text Input simulation ---

        RegisterRoute("/input", req =>
        {
            string text = req.QueryString["text"];
            if (string.IsNullOrEmpty(text))
                return "error: missing text param. usage: /input?text=myAccount[&force=true]";
            bool force = string.Equals(req.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["timeoutMs"], out int acTimeout);
            if (acTimeout <= 0) acTimeout = 5000;
            int.TryParse(req.QueryString["pollMs"], out int acPoll);
            if (acPoll <= 0) acPoll = 100;

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            if (!force)
            {
                string failReason = CheckActionability(
                    () => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null,
                    Actionability.Fill, acTimeout, acPoll);
                if (failReason != null) return "error: " + failReason;
            }

            return RunOnMainThread(() => ExecuteInput(text));
        });

        // --- Keypress simulation (UI) ---

        RegisterRoute("/keypress", req =>
        {
            string keyParam = req.QueryString["key"]?.ToLower();
            if (string.IsNullOrEmpty(keyParam))
                return "error: missing key param. usage: /keypress?key=enter  (supports: enter, submit, esc, cancel)";

            return RunOnMainThread(() =>
            {
                if (!EditorApplication.isPlaying)
                    return "error: requires PlayMode";
                return ExecuteKeypress(keyParam);
            });
        });

        // --- Component Reflection ---

        RegisterRoute("/component/list", req =>
        {
            string name = req.QueryString["name"];
            if (string.IsNullOrEmpty(name))
                return "error: missing name param. usage: /component/list?name=ObjName";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObjectByName(name);
                if (go == null) return "error: GameObject not found: " + name;
                var comps = go.GetComponents<Component>();
                var sb = new StringBuilder();
                sb.AppendLine(go.name + " (" + GetHierarchyPath(go.transform) + ")");
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) { sb.AppendLine("  [" + i + "] (missing script)"); continue; }
                    sb.AppendLine("  [" + i + "] " + comps[i].GetType().Name);
                }
                return sb.ToString().TrimEnd();
            });
        });

        RegisterRoute("/component/get", req =>
        {
            string name = req.QueryString["name"];
            string compName = req.QueryString["component"];
            string propName = req.QueryString["property"];
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(compName))
                return "error: missing params. usage: /component/get?name=ObjName&component=Image[&property=color]";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObjectByName(name);
                if (go == null) return "error: GameObject not found: " + name;
                Component comp = FindComponentByTypeName(go, compName);
                if (comp == null) return "error: component not found: " + compName + " on " + name;
                if (!string.IsNullOrEmpty(propName))
                    return GetComponentValue(comp, propName);
                return InspectComponent(comp);
            });
        });

        RegisterRoute("/component/set", req =>
        {
            string name = req.QueryString["name"];
            string compName = req.QueryString["component"];
            string propName = req.QueryString["property"];
            string valueStr = req.QueryString["value"];
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(compName)
                || string.IsNullOrEmpty(propName) || string.IsNullOrEmpty(valueStr))
                return "error: missing params. usage: /component/set?name=ObjName&component=Image&property=color&value={...}";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObjectByName(name);
                if (go == null) return "error: GameObject not found: " + name;
                Component comp = FindComponentByTypeName(go, compName);
                if (comp == null) return "error: component not found: " + compName + " on " + name;
                return SetComponentValue(comp, propName, valueStr);
            });
        });

        // --- Scene hierarchy ---

        RegisterRoute("/hierarchy", req =>
        {
            int.TryParse(req.QueryString["depth"], out int depth);
            if (depth <= 0) depth = 10;
            int.TryParse(req.QueryString["limit"], out int limit);
            if (limit <= 0) limit = 1000;
            string root = req.QueryString["root"];
            return RunOnMainThread(() => GetSceneHierarchy(depth, limit, root));
        });

        // --- Find GameObjects ---

        RegisterRoute("/find", req =>
        {
            string search = req.QueryString["search"];
            string by = req.QueryString["by"] ?? "name";
            string inactiveStr = req.QueryString["inactive"];
            bool includeInactive = string.IsNullOrEmpty(inactiveStr) || inactiveStr != "false";
            if (string.IsNullOrEmpty(search))
                return "error: missing search param. usage: /find?search=Name[&by=name|tag|layer|component][&inactive=true]";
            return RunOnMainThread(() => FindGameObjectsBySearch(search, by, includeInactive));
        });

        RegisterRoute("/exists", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /exists?name=ObjName OR /exists?path=Canvas/Panel/Button [&parent=Panel][&component=Button][&index=1]";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObject(selector);
                return go != null ? "true" : "false";
            });
        });

        RegisterRoute("/get-text", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /get-text?name=ObjName OR /get-text?path=Canvas/Label [&parent=Panel][&component=UIText][&index=1]";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObject(selector);
                if (go == null) return "error: GameObject not found";
                if (TryGetDisplayedText(go, out string text, out string source))
                    return text ?? string.Empty;
                return "error: no supported text component found on " + go.name + " (looked for Text/UIText/TMP text-like components)";
            });
        });

        RegisterRoute("/dump-target", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /dump-target?name=ObjName OR /dump-target?path=Canvas/Panel/Button [&parent=Panel][&component=Button][&index=1]";
            return RunOnMainThread(() =>
            {
                GameObject go = FindGameObject(selector);
                if (go == null) return "error: GameObject not found";
                return DumpTarget(go);
            });
        });

        RegisterRoute("/wait-for", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            string state = (req.QueryString["state"] ?? "exists").ToLowerInvariant();
            string value = req.QueryString["value"];
            string logTypeFilter = req.QueryString["type"];
            // log state doesn't need name/path selector
            if (state != "log" && state != "log-contains" && !selector.HasSelector)
                return "error: missing selector. usage: /wait-for?name=ObjName&state=exists OR /wait-for?state=log&value=keyword";

            int.TryParse(req.QueryString["timeoutMs"], out int timeoutMs);
            if (timeoutMs <= 0) timeoutMs = 5000;
            int.TryParse(req.QueryString["pollMs"], out int pollMs);
            if (pollMs <= 0) pollMs = 100;
            if (pollMs > timeoutMs) pollMs = timeoutMs;

            return WaitForState(selector, state, value, timeoutMs, pollMs, logTypeFilter);
        });

        // --- Query APIs ---

        RegisterRoute("/is-visible", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /is-visible?name=ObjName OR /is-visible?path=Canvas/Panel/Button [&parent=Panel][&component=Button][&index=1]";
            return RunOnMainThread(() =>
            {
                selector.includeInactive = true;
                GameObject go = FindGameObject(selector);
                if (go == null) return "false (not found)";
                if (!go.activeInHierarchy) return "false (inactive)";
                // Check CanvasGroup alpha chain
                var cg = go.GetComponentInParent<CanvasGroup>();
                if (cg != null && cg.alpha <= 0f) return "false (CanvasGroup alpha=0)";
                return "true";
            });
        });

        RegisterRoute("/is-interactable", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /is-interactable?name=ObjName OR /is-interactable?path=Canvas/Panel/Button [&parent=Panel][&component=Button][&index=1]";
            return RunOnMainThread(() =>
            {
                selector.includeInactive = true;
                GameObject go = FindGameObject(selector);
                if (go == null) return "false (not found)";
                if (!go.activeInHierarchy) return "false (inactive)";
                // Check Selectable (Button, Toggle, etc.)
                var selectable = go.GetComponent<UnityEngine.UI.Selectable>();
                if (selectable != null && !selectable.interactable)
                    return "false (Selectable.interactable=false)";
                // Check CanvasGroup chain
                var cg = go.GetComponentInParent<CanvasGroup>();
                if (cg != null && !cg.interactable)
                    return "false (CanvasGroup.interactable=false)";
                if (cg != null && !cg.blocksRaycasts)
                    return "false (CanvasGroup.blocksRaycasts=false)";
                return "true";
            });
        });

        RegisterRoute("/get-selected", req =>
        {
            return RunOnMainThread(() =>
            {
                if (!EditorApplication.isPlaying)
                    return "error: requires PlayMode";
                EventSystem es = EventSystem.current;
                if (es == null) return "none (no EventSystem)";
                GameObject sel = es.currentSelectedGameObject;
                if (sel == null) return "none";
                return sel.name + " (" + GetHierarchyPath(sel.transform) + ")";
            });
        });

        RegisterRoute("/get-active-window", req =>
        {
            return RunOnMainThread(() =>
            {
                if (!EditorApplication.isPlaying)
                    return "error: requires PlayMode";
                // Find topmost active GUIWindow by searching known patterns
                var results = new List<string>();
                foreach (Transform root in EnumerateAllRootTransforms())
                    CollectActiveWindows(root, results);
                if (results.Count == 0) return "none";
                return "active windows (" + results.Count + "):\n" + string.Join("\n", results);
            });
        });

        // --- Settle mechanism ---

        RegisterRoute("/click-and-wait", req =>
        {
            SelectorQuery selector = ReadSelector(req);
            if (!selector.HasSelector)
                return "error: missing selector. usage: /click-and-wait?name=ButtonName&waitTarget=PanelName&waitState=active OR /click-and-wait?path=Canvas/Panel/Button&waitTarget=PanelName";
            string waitTarget = req.QueryString["waitTarget"];
            string waitState = (req.QueryString["waitState"] ?? "active").ToLowerInvariant();
            string waitValue = req.QueryString["waitValue"];
            bool force = string.Equals(req.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["timeoutMs"], out int timeoutMs);
            if (timeoutMs <= 0) timeoutMs = 5000;
            int.TryParse(req.QueryString["pollMs"], out int pollMs);
            if (pollMs <= 0) pollMs = 100;
            if (pollMs > timeoutMs) pollMs = timeoutMs;

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            // Step 0: Playwright-style actionability polling on the click target.
            if (!force)
            {
                string failReason = CheckActionability(
                    () => FindGameObject(selector),
                    Actionability.Click, timeoutMs, pollMs);
                if (failReason != null) return "error: " + failReason;
            }

            // Step 1: Click
            string clickResult = RunOnMainThread(() => ExecuteClick(FindGameObject(selector), DescribeSelector(selector), null, null));
            if (clickResult.StartsWith("error:")) return clickResult;

            // Step 2: Wait (if waitTarget specified)
            if (!string.IsNullOrEmpty(waitTarget))
            {
                string waitResult = WaitForState(waitTarget, null, waitState, waitValue, true, timeoutMs, pollMs);
                return clickResult + " → " + waitResult;
            }

            return clickResult;
        });

        RegisterRoute("/input-and-wait", req =>
        {
            string text = req.QueryString["text"];
            if (string.IsNullOrEmpty(text))
                return "error: missing text param. usage: /input-and-wait?text=value&waitTarget=Label&waitState=text-contains&waitValue=ok";
            string waitTarget = req.QueryString["waitTarget"];
            string waitState = (req.QueryString["waitState"] ?? "active").ToLowerInvariant();
            string waitValue = req.QueryString["waitValue"];
            bool force = string.Equals(req.QueryString["force"], "true", StringComparison.OrdinalIgnoreCase);
            int.TryParse(req.QueryString["timeoutMs"], out int timeoutMs);
            if (timeoutMs <= 0) timeoutMs = 5000;
            int.TryParse(req.QueryString["pollMs"], out int pollMs);
            if (pollMs <= 0) pollMs = 100;
            if (pollMs > timeoutMs) pollMs = timeoutMs;

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            // Step 0: Actionability on the currently-focused input (Visible + Enabled + Editable).
            if (!force)
            {
                string failReason = CheckActionability(
                    () => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null,
                    Actionability.Fill, timeoutMs, pollMs);
                if (failReason != null) return "error: " + failReason;
            }

            // Step 1: Input
            string inputResult = RunOnMainThread(() => ExecuteInput(text));
            if (inputResult.StartsWith("error:")) return inputResult;

            // Step 2: Wait (if waitTarget specified)
            if (!string.IsNullOrEmpty(waitTarget))
            {
                string waitResult = WaitForState(waitTarget, null, waitState, waitValue, true, timeoutMs, pollMs);
                return inputResult + " → " + waitResult;
            }

            return inputResult;
        });

        // --- Text-based selector ---

        RegisterRoute("/find-by-text", req =>
        {
            string text = req.QueryString["text"];
            if (string.IsNullOrEmpty(text))
                return "error: missing text param. usage: /find-by-text?text=確認&exact=false";
            bool exact = string.Equals(req.QueryString["exact"], "true", StringComparison.OrdinalIgnoreCase);
            return RunOnMainThread(() => FindGameObjectsByText(text, exact));
        });

        // --- Web-first assertions (Playwright-style auto-retry) ---
        // Ref: https://playwright.dev/docs/test-assertions

        RegisterRoute("/expect", req =>
        {
            SelectorQuery sel = ReadSelector(req);
            string cond = (req.QueryString["condition"] ?? "").ToLowerInvariant();
            string value = req.QueryString["value"];
            int.TryParse(req.QueryString["timeoutMs"], out int timeoutMs);
            if (timeoutMs <= 0) timeoutMs = 5000;
            int.TryParse(req.QueryString["pollMs"], out int pollMs);
            if (pollMs <= 0) pollMs = 100;
            if (pollMs > timeoutMs) pollMs = timeoutMs;

            if (string.IsNullOrEmpty(cond))
                return "error: missing condition. usage: /expect?name=X&condition=toBeVisible[&value=...][&timeoutMs=5000]";
            if (!sel.HasSelector)
                return "error: missing selector. usage: /expect?name=X&condition=toBeVisible";
            sel.includeInactive = true;

            return RunExpect(sel, cond, value, timeoutMs, pollMs);
        });

        // --- Play Mode sanity check ---

        RegisterRoute("/playmode/check", req =>
        {
            int.TryParse(req.QueryString["logWindowSec"], out int logWindowSec);
            if (logWindowSec <= 0) logWindowSec = 5;

            // Snapshot log liveness off the main thread (uses its own lock, cheap).
            int recentLogs;
            string lastLogMsg;
            DateTime now = DateTime.Now;
            lock (logLock)
            {
                recentLogs = logBuffer.Count(e => (now - e.time).TotalSeconds <= logWindowSec);
                var last = logBuffer.LastOrDefault();
                lastLogMsg = string.IsNullOrEmpty(last.message) ? "(none)" :
                    last.message.Substring(0, Math.Min(last.message.Length, 80)) +
                    " (" + Math.Max(0, (int)(now - last.time).TotalSeconds) + "s ago)";
            }

            return RunOnMainThread(() =>
            {
                var sb = new StringBuilder();
                var reasons = new List<string>();

                sb.AppendLine("playmode: " + EditorApplication.isPlaying);
                if (!EditorApplication.isPlaying) reasons.Add("not in PlayMode");

                var eventSystems = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None);
                sb.AppendLine("EventSystem: " + eventSystems.Length + (eventSystems.Length == 0 ? " (MISSING)" : eventSystems.Length > 1 ? " (multiple — may conflict)" : ""));
                if (eventSystems.Length == 0) reasons.Add("no EventSystem");

                var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
                sb.AppendLine("Canvas: " + canvases.Length + (canvases.Length == 0 ? " (MISSING)" : ""));
                if (canvases.Length == 0) reasons.Add("no Canvas");

                var mainCam = Camera.main;
                sb.AppendLine("Camera.main: " + (mainCam != null ? mainCam.name : "null (MISSING)"));
                if (mainCam == null) reasons.Add("no Camera.main");

                int raycasters = 0;
                foreach (var c in canvases)
                    if (c.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null) raycasters++;
                sb.AppendLine("GraphicRaycaster: " + raycasters + "/" + canvases.Length + " canvases");

                EventSystem es = EventSystem.current;
                GameObject focused = es != null ? es.currentSelectedGameObject : null;
                sb.AppendLine("EventSystem.current: " + (es != null ? es.name : "null"));
                sb.AppendLine("currentSelected: " + (focused != null ? focused.name : "none"));

                // Active GUIWindow list — distinguishes real gameplay from login/loading.
                var windows = new List<string>();
                if (EditorApplication.isPlaying)
                    foreach (Transform root in EnumerateAllRootTransforms())
                        CollectActiveWindows(root, windows);
                sb.AppendLine("activeWindows: " + windows.Count);
                foreach (var w in windows) sb.AppendLine("  - " + w);
                if (EditorApplication.isPlaying && windows.Count == 0)
                    reasons.Add("no active GUIWindow (likely login/loading)");

                // Log liveness — silent buffer means game loop may be stalled.
                sb.AppendLine("logsInLast" + logWindowSec + "s: " + recentLogs);
                sb.AppendLine("lastLog: " + lastLogMsg);
                if (EditorApplication.isPlaying && recentLogs == 0)
                    reasons.Add("no recent log activity (game loop may be idle/stalled)");

                string firstLine = reasons.Count == 0
                    ? "ready: yes"
                    : "ready: no (" + string.Join("; ", reasons) + ")";
                sb.Insert(0, firstLine + "\n");
                return sb.ToString().TrimEnd();
            });
        });

        // --- Failure diagnostics (one-shot snapshot for debugging a failed action) ---

        RegisterRoute("/diagnose", req =>
        {
            SelectorQuery sel = ReadSelector(req);
            sel.includeInactive = true;
            int.TryParse(req.QueryString["logs"], out int logCount);
            if (logCount <= 0) logCount = 20;
            bool wantShot = !string.Equals(req.QueryString["screenshot"], "false", StringComparison.OrdinalIgnoreCase);

            string shotLine;
            if (wantShot)
            {
                string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "skipped (requires PlayMode)");
                if (playCheck != null)
                {
                    shotLine = "screenshot: " + playCheck;
                }
                else
                {
                    string shot = CaptureScreenshotAndWait();
                    shotLine = "screenshot: " + shot;
                }
            }
            else
            {
                shotLine = "screenshot: disabled";
            }

            return RunOnMainThread(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== DIAGNOSE @ " + DateTime.Now.ToString("HH:mm:ss.fff") + " ===");
                sb.AppendLine("playmode: " + EditorApplication.isPlaying);
                sb.AppendLine(shotLine);

                if (sel.HasSelector)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- target ---");
                    GameObject go = FindGameObject(sel);
                    if (go == null)
                    {
                        sb.AppendLine("NOT FOUND for selector: " + DescribeSelector(sel));
                    }
                    else
                    {
                        sb.AppendLine(DumpTarget(go).TrimEnd());
                        string vr = VisibleReason(go);
                        sb.AppendLine("visible: " + (vr == null ? "yes" : "no (" + vr + ")"));
                        string er = EnabledReason(go);
                        sb.AppendLine("enabled: " + (er == null ? "yes" : "no (" + er + ")"));
                        string edr = EditableReason(go);
                        sb.AppendLine("editable: " + (edr == null ? "yes" : "no (" + edr + ")"));
                    }
                }

                sb.AppendLine();
                sb.AppendLine("--- last " + logCount + " logs ---");
                lock (logLock)
                {
                    var recent = logBuffer.AsEnumerable().Reverse().Take(logCount).ToList();
                    if (recent.Count == 0)
                    {
                        sb.AppendLine("(none)");
                    }
                    else
                    {
                        for (int i = 0; i < recent.Count; i++)
                            sb.AppendLine(FormatLogEntry(recent[i], i, true));
                    }
                }

                return sb.ToString().TrimEnd();
            });
        });

        // --- Baseline screenshot workflow (v17) ---

        RegisterRoute("/baseline/save", req =>
        {
            string name = req.QueryString["name"];
            if (string.IsNullOrEmpty(name))
                return "error: missing name param. usage: /baseline/save?name=PanelName";
            string safe = SanitizeBaselineName(name);
            if (safe == null) return "error: invalid name (allowed: letters, digits, _ -)";

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            string shot = CaptureScreenshotAndWait();
            if (shot.StartsWith("error:")) return shot;

            string dir = Path.GetFullPath(BaselineDir);
            Directory.CreateDirectory(dir);
            string pngPath = Path.Combine(dir, safe + ".png");
            string jsonPath = Path.Combine(dir, safe + ".json");
            File.Copy(shot, pngPath, true);

            int width = 0, height = 0;
            string sceneName = RunOnMainThread(() =>
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    tex.LoadImage(File.ReadAllBytes(pngPath));
                    width = tex.width;
                    height = tex.height;
                }
                finally { UnityEngine.Object.DestroyImmediate(tex); }
                return SceneManager.GetActiveScene().name ?? "";
            });

            string capturedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            File.WriteAllText(jsonPath,
                "{\"name\":\"" + safe + "\"," +
                "\"width\":" + width + "," +
                "\"height\":" + height + "," +
                "\"capturedAt\":\"" + capturedAt + "\"," +
                "\"sceneName\":\"" + sceneName.Replace("\"", "\\\"") + "\"}");

            var sb = new StringBuilder();
            sb.AppendLine("ok: saved");
            sb.AppendLine("name: " + safe);
            sb.AppendLine("path: " + pngPath);
            sb.AppendLine("width: " + width);
            sb.AppendLine("height: " + height);
            sb.AppendLine("capturedAt: " + capturedAt);
            sb.AppendLine("sceneName: " + sceneName);
            return sb.ToString().TrimEnd();
        });

        RegisterRoute("/baseline/list", req =>
        {
            string dir = Path.GetFullPath(BaselineDir);
            if (!Directory.Exists(dir)) return "count: 0";
            var pngs = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToArray();
            var sb = new StringBuilder();
            sb.AppendLine("count: " + pngs.Length);
            foreach (var png in pngs)
            {
                string n = Path.GetFileNameWithoutExtension(png);
                string jsonPath = Path.ChangeExtension(png, ".json");
                string meta = File.Exists(jsonPath) ? File.ReadAllText(jsonPath).Trim() : "(no metadata)";
                sb.AppendLine("- " + n + " | " + png + " | " + meta);
            }
            return sb.ToString().TrimEnd();
        });

        RegisterRoute("/baseline/delete", req =>
        {
            string name = req.QueryString["name"];
            if (string.IsNullOrEmpty(name))
                return "error: missing name param. usage: /baseline/delete?name=PanelName";
            string safe = SanitizeBaselineName(name);
            if (safe == null) return "error: invalid name";
            string dir = Path.GetFullPath(BaselineDir);
            var deleted = new List<string>();
            foreach (var ext in new[] { ".png", ".json" })
            {
                string p = Path.Combine(dir, safe + ext);
                if (File.Exists(p)) { File.Delete(p); deleted.Add(p); }
            }
            string diffViz = Path.GetFullPath(ScreenshotDir + "/compare_diff_" + safe + ".png");
            if (File.Exists(diffViz)) { File.Delete(diffViz); deleted.Add(diffViz); }
            if (deleted.Count == 0) return "error: baseline not found: " + safe;
            var sb = new StringBuilder();
            sb.AppendLine("ok: deleted " + deleted.Count + " file(s)");
            foreach (var p in deleted) sb.AppendLine("- " + p);
            return sb.ToString().TrimEnd();
        });

        RegisterRoute("/baseline/diff", req =>
        {
            string name = req.QueryString["name"];
            if (string.IsNullOrEmpty(name))
                return "error: missing name param. usage: /baseline/diff?name=PanelName[&identicalThreshold=0.001][&gridSize=32][&perPixelThreshold=16]";
            string safe = SanitizeBaselineName(name);
            if (safe == null) return "error: invalid name";

            string dir = Path.GetFullPath(BaselineDir);
            string baselinePath = Path.Combine(dir, safe + ".png");
            if (!File.Exists(baselinePath))
                return "error: baseline not found: " + baselinePath;

            float identicalThreshold = 0.001f;
            if (float.TryParse(req.QueryString["identicalThreshold"], out float id) && id > 0)
                identicalThreshold = id;
            int gridSize = 32;
            if (int.TryParse(req.QueryString["gridSize"], out int gs) && gs > 0)
                gridSize = gs;
            int perPixelThreshold = 16;
            if (int.TryParse(req.QueryString["perPixelThreshold"], out int pp) && pp > 0)
                perPixelThreshold = pp;

            string playCheck = RunOnMainThread(() => EditorApplication.isPlaying ? null : "error: requires PlayMode");
            if (playCheck != null) return playCheck;

            string shot = CaptureScreenshotAndWait();
            if (shot.StartsWith("error:")) return shot;

            string currentPath = Path.GetFullPath(ScreenshotDir + "/compare_current.png");
            Directory.CreateDirectory(Path.GetDirectoryName(currentPath));
            File.Copy(shot, currentPath, true);

            return BaselineDiffer.Diff(safe, baselinePath, currentPath,
                identicalThreshold, gridSize, perPixelThreshold);
        });

        RegisterRoute("/artifacts/list", req =>
        {
            string kind = req.QueryString["kind"] ?? "all";
            int.TryParse(req.QueryString["limit"], out int limit);
            return BuildArtifactListJson(kind, limit);
        });
    }

    static string SanitizeBaselineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (char c in name)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return null;
        return name;
    }

    static string BuildArtifactListJson(string requestedKind, int limit)
    {
        string kind = string.IsNullOrWhiteSpace(requestedKind)
            ? "all"
            : requestedKind.Trim().ToLowerInvariant();
        if (limit <= 0) limit = 50;
        if (limit > 500) limit = 500;

        var artifacts = CollectArtifacts(kind)
            .OrderByDescending(a => a.modifiedUtc)
            .Take(limit)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"kind\": \"" + EscapeJsonString(kind) + "\",");
        sb.AppendLine("  \"limit\": " + limit + ",");
        sb.AppendLine("  \"count\": " + artifacts.Count + ",");
        sb.AppendLine("  \"roots\": {");
        sb.AppendLine("    \"screenshots\": \"" + EscapeJsonString(Path.GetFullPath(ScreenshotDir)) + "\",");
        sb.AppendLine("    \"baselines\": \"" + EscapeJsonString(Path.GetFullPath(BaselineDir)) + "\",");
        sb.AppendLine("    \"scenarioReports\": \"" + EscapeJsonString(Path.GetFullPath(ScenarioReportDir)) + "\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"artifacts\": [");
        for (int i = 0; i < artifacts.Count; i++)
        {
            ArtifactInfo a = artifacts[i];
            sb.Append("    {");
            sb.Append("\"kind\":\"" + EscapeJsonString(a.kind) + "\",");
            sb.Append("\"path\":\"" + EscapeJsonString(a.path) + "\",");
            sb.Append("\"sizeBytes\":" + a.sizeBytes + ",");
            sb.Append("\"modifiedAt\":\"" + a.modifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"");
            sb.Append("}");
            if (i < artifacts.Count - 1) sb.Append(",");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.Append("}");
        return sb.ToString();
    }

    static List<ArtifactInfo> CollectArtifacts(string kind)
    {
        var artifacts = new List<ArtifactInfo>();
        bool all = string.IsNullOrEmpty(kind) || kind == "all";

        if (all || kind == "screenshot" || kind == "screenshots")
            AddArtifactFiles(artifacts, "screenshot", ScreenshotDir, "*.png");

        if (all || kind == "baseline" || kind == "baselines")
        {
            AddArtifactFiles(artifacts, "baseline", BaselineDir, "*.png");
            AddArtifactFiles(artifacts, "baseline-metadata", BaselineDir, "*.json");
        }

        if (all || kind == "log" || kind == "logs")
        {
            AddArtifactFiles(artifacts, "scenario-log", ScenarioReportDir, "logs.txt", SearchOption.AllDirectories);
        }

        if (all || kind == "report" || kind == "reports" ||
            kind == "scenario" || kind == "scenario-report" || kind == "scenario-reports")
        {
            AddArtifactFiles(artifacts, "scenario-report", ScenarioReportDir, "*.json", SearchOption.AllDirectories);
        }

        if (all || kind == "run-artifact" || kind == "run-artifacts" ||
            kind == "scenario-artifact" || kind == "scenario-artifacts")
        {
            AddScenarioRunArtifactFiles(artifacts);
        }

        return artifacts;
    }

    static void AddArtifactFiles(List<ArtifactInfo> artifacts, string kind, string dir, string pattern)
    {
        AddArtifactFiles(artifacts, kind, dir, pattern, SearchOption.TopDirectoryOnly);
    }

    static void AddArtifactFiles(List<ArtifactInfo> artifacts, string kind, string dir, string pattern, SearchOption searchOption)
    {
        string fullDir = Path.GetFullPath(dir);
        if (!Directory.Exists(fullDir)) return;

        foreach (string path in Directory.GetFiles(fullDir, pattern, searchOption))
        {
            var file = new FileInfo(path);
            artifacts.Add(new ArtifactInfo
            {
                kind = kind,
                path = file.FullName,
                sizeBytes = file.Length,
                modifiedUtc = file.LastWriteTimeUtc
            });
        }
    }

    static void AddScenarioRunArtifactFiles(List<ArtifactInfo> artifacts)
    {
        string root = Path.GetFullPath(ScenarioReportDir);
        if (!Directory.Exists(root)) return;

        foreach (string artifactDir in Directory.GetDirectories(root, "artifacts", SearchOption.AllDirectories))
            AddArtifactFiles(artifacts, "scenario-artifact", artifactDir, "*", SearchOption.TopDirectoryOnly);
    }

    // Pixel-level diff helper for /baseline/diff.
    // Hash short-circuits identical PNGs; pixel compare runs on main thread (Texture2D required).
    static class BaselineDiffer
    {
        public struct Region
        {
            public int gridX, gridY, pixelX, pixelY, w, h;
            public double density;
        }

        public static string Diff(string name, string baselinePath, string currentPath,
            float identicalThreshold, int gridSize, int perPixelThreshold)
        {
            byte[] baselineBytes = File.ReadAllBytes(baselinePath);
            byte[] currentBytes = File.ReadAllBytes(currentPath);

            if (BytesEqual(baselineBytes, currentBytes))
            {
                return FormatResult(name, "identical", 0, 0, 0, true,
                    baselinePath, currentPath, null, new List<Region>());
            }

            return RunOnMainThread(() =>
            {
                var baseTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var curTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    baseTex.LoadImage(baselineBytes);
                    curTex.LoadImage(currentBytes);

                    if (baseTex.width != curTex.width || baseTex.height != curTex.height)
                    {
                        return FormatResult(name, "diff-detected", 1.0, 0, 0, false,
                            baselinePath, currentPath, null, new List<Region>());
                    }

                    int w = baseTex.width, h = baseTex.height;
                    Color32[] a = baseTex.GetPixels32();
                    Color32[] b = curTex.GetPixels32();

                    int cellCols = (w + gridSize - 1) / gridSize;
                    int cellRows = (h + gridSize - 1) / gridSize;
                    int[] cellCounts = new int[cellCols * cellRows];
                    bool[] diffMask = new bool[a.Length];
                    int diffPixels = 0;

                    for (int y = 0; y < h; y++)
                    {
                        int row = y * w;
                        int cy = y / gridSize;
                        for (int x = 0; x < w; x++)
                        {
                            int idx = row + x;
                            var pa = a[idx]; var pb = b[idx];
                            int maxD = Math.Max(Math.Abs(pa.r - pb.r),
                                        Math.Max(Math.Abs(pa.g - pb.g), Math.Abs(pa.b - pb.b)));
                            if (maxD > perPixelThreshold)
                            {
                                diffPixels++;
                                diffMask[idx] = true;
                                cellCounts[cy * cellCols + (x / gridSize)]++;
                            }
                        }
                    }

                    int total = w * h;
                    double percent = (double)diffPixels / total;

                    var regions = new List<Region>();
                    for (int cy = 0; cy < cellRows; cy++)
                    {
                        for (int cx = 0; cx < cellCols; cx++)
                        {
                            int cellW = Math.Min(gridSize, w - cx * gridSize);
                            int cellH = Math.Min(gridSize, h - cy * gridSize);
                            int cellPixels = cellW * cellH;
                            int c = cellCounts[cy * cellCols + cx];
                            double density = cellPixels > 0 ? (double)c / cellPixels : 0;
                            if (density > 0.10)
                            {
                                regions.Add(new Region
                                {
                                    gridX = cx,
                                    gridY = cy,
                                    pixelX = cx * gridSize,
                                    pixelY = cy * gridSize,
                                    w = cellW,
                                    h = cellH,
                                    density = density
                                });
                            }
                        }
                    }
                    regions = regions.OrderByDescending(r => r.density).Take(20).ToList();

                    string vizPath = Path.GetFullPath(ScreenshotDir + "/compare_diff_" + name + ".png");
                    WriteDiffVisualization(a, diffMask, w, h, vizPath);

                    string verdict = percent < identicalThreshold ? "within-threshold" : "diff-detected";

                    return FormatResult(name, verdict, percent, diffPixels, total, true,
                        baselinePath, currentPath, vizPath, regions);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baseTex);
                    UnityEngine.Object.DestroyImmediate(curTex);
                }
            });
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void WriteDiffVisualization(Color32[] src, bool[] mask, int w, int h, string outPath)
        {
            var overlay = new Color32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                if (mask[i])
                {
                    overlay[i] = new Color32(255, (byte)(src[i].g / 3), (byte)(src[i].b / 3), 255);
                }
                else
                {
                    overlay[i] = new Color32(
                        (byte)(src[i].r / 2 + 64),
                        (byte)(src[i].g / 2 + 64),
                        (byte)(src[i].b / 2 + 64), 255);
                }
            }
            var vizTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                vizTex.SetPixels32(overlay);
                vizTex.Apply();
                byte[] png = vizTex.EncodeToPNG();
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, png);
            }
            finally { UnityEngine.Object.DestroyImmediate(vizTex); }
        }

        static string FormatResult(string name, string verdict, double percent,
            int diffPixels, int total, bool resolutionMatch,
            string baselinePath, string currentPath, string vizPath, List<Region> regions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("verdict: " + verdict);
            sb.AppendLine("name: " + name);
            sb.AppendLine("diffPercent: " + percent.ToString("F6"));
            sb.AppendLine("diffPixelCount: " + diffPixels);
            sb.AppendLine("totalPixelCount: " + total);
            sb.AppendLine("resolutionMatch: " + (resolutionMatch ? "true" : "false"));
            sb.AppendLine("baselinePath: " + baselinePath);
            sb.AppendLine("currentPath: " + currentPath);
            sb.AppendLine("diffVisualizationPath: " + (vizPath ?? "(none)"));
            sb.AppendLine("regions: " + regions.Count);
            foreach (var r in regions)
                sb.AppendLine("  - gridX=" + r.gridX + " gridY=" + r.gridY +
                    " x=" + r.pixelX + " y=" + r.pixelY +
                    " w=" + r.w + " h=" + r.h +
                    " density=" + r.density.ToString("F3"));
            return sb.ToString().TrimEnd();
        }
    }

    static string DescribeSelector(SelectorQuery sel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(sel.name)) parts.Add("name=" + sel.name);
        if (!string.IsNullOrEmpty(sel.path)) parts.Add("path=" + sel.path);
        if (!string.IsNullOrEmpty(sel.parent)) parts.Add("parent=" + sel.parent);
        if (!string.IsNullOrEmpty(sel.component)) parts.Add("component=" + sel.component);
        if (sel.index > 0) parts.Add("index=" + sel.index);
        return parts.Count == 0 ? "(empty)" : string.Join(", ", parts);
    }

    // --- Log formatting ---

    static string FormatLogEntry(LogEntry e, int index, bool compact)
    {
        if (compact)
            return $"[{index}][{e.time:HH:mm:ss}][{e.type}] {e.message}";
        if (string.IsNullOrEmpty(e.stackTrace))
            return $"[{index}][{e.time:HH:mm:ss}][{e.type}] {e.message}";
        return $"[{index}][{e.time:HH:mm:ss}][{e.type}] {e.message}\n{e.stackTrace}";
    }

    // --- RectTransform search ---

    static string FindRectTransforms(string query)
    {
        query = query.Trim();
        var results = new List<RectTransformInfo>();

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                CollectRectTransforms(root.transform, query, results);
        }

        // DontDestroyOnLoad
        GameObject temp = null;
        try
        {
            temp = new GameObject("__BridgeProbe__");
            UnityEngine.Object.DontDestroyOnLoad(temp);
            Scene ddolScene = temp.scene;
            UnityEngine.Object.DestroyImmediate(temp);
            temp = null;
            foreach (GameObject root in ddolScene.GetRootGameObjects())
                CollectRectTransforms(root.transform, query, results);
        }
        catch { if (temp != null) UnityEngine.Object.DestroyImmediate(temp); }

        var wrapper = new RectTransformResultWrapper { query = query, count = results.Count, results = results.ToArray() };
        string json = JsonUtility.ToJson(wrapper, true);

        // Also write to file for compatibility
        File.WriteAllText("Temp/AgentRectTransforms.json", json);

        return json;
    }

    static void CollectRectTransforms(Transform current, string query, List<RectTransformInfo> results)
    {
        RectTransform rt = current as RectTransform;
        if (rt != null)
        {
            bool match = string.IsNullOrEmpty(query)
                || current.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            if (match)
                results.Add(BuildRectTransformInfo(rt));
        }
        for (int i = 0; i < current.childCount; i++)
            CollectRectTransforms(current.GetChild(i), query, results);
    }

    static RectTransformInfo BuildRectTransformInfo(RectTransform rt)
    {
        var info = new RectTransformInfo();
        info.name = rt.name;
        info.path = GetHierarchyPath(rt);
        info.active = rt.gameObject.activeInHierarchy;
        info.selfActive = rt.gameObject.activeSelf;
        info.anchoredPosition = $"({rt.anchoredPosition.x:F1}, {rt.anchoredPosition.y:F1})";
        info.sizeDelta = $"({rt.sizeDelta.x:F1}, {rt.sizeDelta.y:F1})";
        info.anchorMin = $"({rt.anchorMin.x:F1}, {rt.anchorMin.y:F1})";
        info.anchorMax = $"({rt.anchorMax.x:F1}, {rt.anchorMax.y:F1})";
        info.pivot = $"({rt.pivot.x:F1}, {rt.pivot.y:F1})";
        info.localPosition = $"({rt.localPosition.x:F1}, {rt.localPosition.y:F1}, {rt.localPosition.z:F1})";
        info.localScale = $"({rt.localScale.x:F1}, {rt.localScale.y:F1}, {rt.localScale.z:F1})";
        info.localRotation = $"({rt.localEulerAngles.x:F1}, {rt.localEulerAngles.y:F1}, {rt.localEulerAngles.z:F1})";
        info.rect = $"x:{rt.rect.x:F1} y:{rt.rect.y:F1} w:{rt.rect.width:F1} h:{rt.rect.height:F1}";
        info.siblingIndex = rt.GetSiblingIndex();
        return info;
    }

    static string GetHierarchyPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        Transform parent = t.parent;
        while (parent != null)
        {
            sb.Insert(0, "/");
            sb.Insert(0, parent.name);
            parent = parent.parent;
        }
        return sb.ToString();
    }

    [Serializable]
    class RectTransformInfo
    {
        public string name;
        public string path;
        public bool active;
        public bool selfActive;
        public string anchoredPosition;
        public string sizeDelta;
        public string anchorMin;
        public string anchorMax;
        public string pivot;
        public string localPosition;
        public string localScale;
        public string localRotation;
        public string rect;
        public int siblingIndex;
    }

    [Serializable]
    class RectTransformResultWrapper
    {
        public string query;
        public int count;
        public RectTransformInfo[] results;
    }

    // --- Click helpers ---

    static string ExecuteClick(string nameParam, string xStr, string yStr)
    {
        EventSystem es = EventSystem.current;
        if (es == null)
            return "error: no EventSystem found in scene";

        Vector2 screenPos;

        if (!string.IsNullOrEmpty(nameParam))
        {
            // Find by name → get screen position
            GameObject target = FindGameObjectByName(nameParam);
            if (target == null)
                return "error: GameObject not found: " + nameParam;

            RectTransform rt = target.GetComponent<RectTransform>();
            if (rt != null)
            {
                // UI element → use RectTransform center in screen space
                Canvas canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null)
                    return "error: no parent Canvas found for: " + nameParam;

                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : (canvas.worldCamera ?? Camera.main);
                Vector3[] corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                Vector3 worldCenter = (corners[0] + corners[2]) * 0.5f;

                if (cam != null)
                    screenPos = cam.WorldToScreenPoint(worldCenter);
                else
                    screenPos = worldCenter; // Overlay: world coords == screen coords
            }
            else
            {
                // 3D object → use main camera
                Camera cam = Camera.main;
                if (cam == null)
                    return "error: no main camera for 3D click";
                screenPos = cam.WorldToScreenPoint(target.transform.position);
            }
        }
        else
        {
            float.TryParse(xStr, out float x);
            float.TryParse(yStr, out float y);
            screenPos = new Vector2(x, y);
        }

        // Raycast through EventSystem
        PointerEventData ped = new PointerEventData(es)
        {
            position = screenPos
        };

        List<RaycastResult> results = new List<RaycastResult>();
        es.RaycastAll(ped, results);

        if (results.Count == 0)
            return $"click at ({screenPos.x:F0},{screenPos.y:F0}): no UI hit";

        GameObject hitObj = results[0].gameObject;

        // Walk up to find the actual clickable handler
        GameObject clickable = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObj);
        if (clickable != null)
        {
            ExecuteEvents.Execute(clickable, ped, ExecuteEvents.pointerClickHandler);
            return $"clicked: {clickable.name} (at {screenPos.x:F0},{screenPos.y:F0}, hit: {hitObj.name})";
        }

        // Fallback: try submit handler
        GameObject submittable = ExecuteEvents.GetEventHandler<ISubmitHandler>(hitObj);
        if (submittable != null)
        {
            ExecuteEvents.Execute(submittable, ped, ExecuteEvents.submitHandler);
            return $"submitted: {submittable.name} (at {screenPos.x:F0},{screenPos.y:F0}, hit: {hitObj.name})";
        }

        return $"hit: {hitObj.name} at ({screenPos.x:F0},{screenPos.y:F0}) but no click/submit handler found";
    }

    static string ExecuteClick(GameObject target, string targetDescription, string xStr, string yStr)
    {
        EventSystem es = EventSystem.current;
        if (es == null)
            return "error: no EventSystem found in scene";

        Vector2 screenPos;

        if (!string.IsNullOrEmpty(xStr) || !string.IsNullOrEmpty(yStr))
        {
            float.TryParse(xStr, out float x);
            float.TryParse(yStr, out float y);
            screenPos = new Vector2(x, y);
        }
        else
        {
            if (target == null)
                return "error: GameObject not found: " + targetDescription;

            RectTransform rt = target.GetComponent<RectTransform>();
            if (rt != null)
            {
                Canvas canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null)
                    return "error: no parent Canvas found for: " + targetDescription;

                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : (canvas.worldCamera ?? Camera.main);
                Vector3[] corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                Vector3 worldCenter = (corners[0] + corners[2]) * 0.5f;
                screenPos = cam != null ? cam.WorldToScreenPoint(worldCenter) : worldCenter;
            }
            else
            {
                Camera cam = Camera.main;
                if (cam == null)
                    return "error: no main camera for 3D click";
                screenPos = cam.WorldToScreenPoint(target.transform.position);
            }
        }

        PointerEventData ped = new PointerEventData(es)
        {
            position = screenPos
        };

        List<RaycastResult> results = new List<RaycastResult>();
        es.RaycastAll(ped, results);

        if (results.Count == 0)
            return $"click at ({screenPos.x:F0},{screenPos.y:F0}): no UI hit";

        GameObject hitObj = results[0].gameObject;
        GameObject clickable = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObj);
        if (clickable != null)
        {
            ExecuteEvents.Execute(clickable, ped, ExecuteEvents.pointerClickHandler);
            return $"clicked: {clickable.name} (at {screenPos.x:F0},{screenPos.y:F0}, hit: {hitObj.name})";
        }

        GameObject submittable = ExecuteEvents.GetEventHandler<ISubmitHandler>(hitObj);
        if (submittable != null)
        {
            ExecuteEvents.Execute(submittable, ped, ExecuteEvents.submitHandler);
            return $"submitted: {submittable.name} (at {screenPos.x:F0},{screenPos.y:F0}, hit: {hitObj.name})";
        }

        return $"hit: {hitObj.name} at ({screenPos.x:F0},{screenPos.y:F0}) but no click/submit handler found";
    }

    static string ExecuteInput(string text)
    {
        EventSystem es = EventSystem.current;
        if (es == null) return "error: no EventSystem found in scene";

        GameObject currentSelected = es.currentSelectedGameObject;
        if (currentSelected == null) return "error: no UI element is currently selected. Use /click first to focus an input field.";

        // Try standard UI InputField
        var inputField = currentSelected.GetComponent<InputField>();
        if (inputField != null)
        {
            inputField.text = text;
            inputField.onValueChanged?.Invoke(text);
            return $"input set on {currentSelected.name} (UnityEngine.UI.InputField)";
        }

        // Try TextMeshPro InputField via Reflection to avoid hard dependency
        var tmpComp = currentSelected.GetComponent("TMP_InputField");
        if (tmpComp != null)
        {
            var textProp = tmpComp.GetType().GetProperty("text");
            if (textProp != null)
            {
                textProp.SetValue(tmpComp, text, null);
                
                // Try invoke onValueChanged event
                var evField = tmpComp.GetType().GetField("onValueChanged");
                if (evField != null)
                {
                    var evObj = evField.GetValue(tmpComp);
                    if (evObj != null)
                    {
                        var invokeMethod = evObj.GetType().GetMethod("Invoke");
                        if (invokeMethod != null)
                            invokeMethod.Invoke(evObj, new object[] { text });
                    }
                }
                return $"input set on {currentSelected.name} (TMP_InputField)";
            }
        }

        // Try UIEmojiInput (project custom InputField) via Reflection
        var emojiInput = currentSelected.GetComponent("UIEmojiInput");
        if (emojiInput != null)
        {
            var textProp = emojiInput.GetType().GetProperty("text");
            if (textProp != null)
            {
                textProp.SetValue(emojiInput, text, null);
                return $"input set on {currentSelected.name} (UIEmojiInput)";
            }
        }

        return $"error: selected object {currentSelected.name} does not have an InputField, TMP_InputField, or UIEmojiInput component";
    }

    static string ExecuteKeypress(string keyParam)
    {
        EventSystem es = EventSystem.current;
        if (es == null) return "error: no EventSystem found in scene";

        bool uiHandled = false;
        GameObject currentSelected = es.currentSelectedGameObject;
        if (currentSelected != null)
        {
            PointerEventData ped = new PointerEventData(es);
            if (keyParam == "enter" || keyParam == "submit")
            {
                GameObject submittable = ExecuteEvents.GetEventHandler<ISubmitHandler>(currentSelected);
                if (submittable != null)
                {
                    ExecuteEvents.Execute(submittable, ped, ExecuteEvents.submitHandler);
                    uiHandled = true;
                }
            }
            else if (keyParam == "esc" || keyParam == "cancel")
            {
                GameObject cancellable = ExecuteEvents.GetEventHandler<ICancelHandler>(currentSelected);
                if (cancellable != null)
                {
                    ExecuteEvents.Execute(cancellable, ped, ExecuteEvents.cancelHandler);
                    uiHandled = true;
                }
            }
        }

        // Send actual OS keystroke (requires Editor GameView focus to work)
        bool osHandled = false;
        if (keyParam == "enter" || keyParam == "submit")
        {
            SimulateKeystroke(VK_RETURN);
            osHandled = true;
        }
        else if (keyParam == "esc" || keyParam == "cancel")
        {
            SimulateKeystroke(VK_ESCAPE);
            osHandled = true;
        }

        if (uiHandled && osHandled)
            return $"keypress '{keyParam}' sent (UI Handler invoked & OS keystroke simulated)";
        else if (osHandled)
            return $"keypress '{keyParam}' sent (OS keystroke simulated)";
        else
            return $"error: unsupported key '{keyParam}'. Supports: enter, submit, esc, cancel.";
    }

    // --- OS Keystroke Simulation ---
    
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
    
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    const byte VK_RETURN = 0x0D;
    const byte VK_ESCAPE = 0x1B;
    const uint KEYEVENTF_KEYUP = 0x0002;

    static void SimulateKeystroke(byte vkCode)
    {
        // Force OS to bring Unity to foreground, otherwise keybd_event hits the terminal/browser
        var process = System.Diagnostics.Process.GetCurrentProcess();
        SetForegroundWindow(process.MainWindowHandle);

        // Require GameView to be focused for Unity's Input System to catch it
        Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        if (gameViewType != null)
        {
            var gv = EditorWindow.GetWindow(gameViewType);
            if (gv != null) gv.Focus();
        }

        Thread.Sleep(100); // Give OS time to switch focus

        keybd_event(vkCode, 0, 0, 0); // Key down
        Thread.Sleep(50); // Small delay to ensure Unity registers the down state
        keybd_event(vkCode, 0, KEYEVENTF_KEYUP, 0); // Key up
    }

    static GameObject FindGameObjectByName(string name)
    {
        // Search all loaded scenes
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                GameObject found = FindInChildren(root.transform, name);
                if (found != null) return found;
            }
        }

        // Search DontDestroyOnLoad
        GameObject temp = null;
        try
        {
            temp = new GameObject("__BridgeClickProbe__");
            UnityEngine.Object.DontDestroyOnLoad(temp);
            Scene ddolScene = temp.scene;
            UnityEngine.Object.DestroyImmediate(temp);
            temp = null;
            foreach (GameObject root in ddolScene.GetRootGameObjects())
            {
                GameObject found = FindInChildren(root.transform, name);
                if (found != null) return found;
            }
        }
        catch { if (temp != null) UnityEngine.Object.DestroyImmediate(temp); }

        return null;
    }

    static SelectorQuery ReadSelector(HttpListenerRequest req)
    {
        string inactiveStr = req.QueryString["inactive"];
        int.TryParse(req.QueryString["index"], out int index);
        if (index < 0)
            index = 0;

        return new SelectorQuery
        {
            name = req.QueryString["name"],
            path = req.QueryString["path"],
            parent = req.QueryString["parent"],
            component = req.QueryString["component"],
            index = index,
            includeInactive = string.IsNullOrEmpty(inactiveStr) || inactiveStr != "false"
        };
    }

    static GameObject FindGameObject(SelectorQuery selector)
    {
        GameObject go = !string.IsNullOrEmpty(selector.path)
            ? FindGameObjectByPath(selector.path, selector.includeInactive)
            : FindGameObjectBySelector(selector);
        if (go == null) return null;
        if (!selector.includeInactive && !go.activeInHierarchy) return null;
        if (!MatchesComponent(go, selector.component)) return null;
        return go;
    }

    static GameObject FindGameObject(string name, string path, bool includeInactive)
    {
        GameObject go = !string.IsNullOrEmpty(path)
            ? FindGameObjectByPath(path, includeInactive)
            : FindGameObjectByName(name);
        if (go == null) return null;
        if (!includeInactive && !go.activeInHierarchy) return null;
        return go;
    }

    static GameObject FindGameObjectByPath(string path, bool includeInactive)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string normalizedPath = NormalizeHierarchyPath(path);
        foreach (Transform root in EnumerateAllRootTransforms())
        {
            GameObject found = FindByPathRecursive(root, normalizedPath, includeInactive);
            if (found != null)
                return found;
        }

        return null;
    }

    static GameObject FindGameObjectBySelector(SelectorQuery selector)
    {
        var results = new List<GameObject>();
        if (!string.IsNullOrEmpty(selector.parent))
        {
            GameObject parent = FindGameObjectByPath(selector.parent, selector.includeInactive);
            if (parent == null)
                parent = FindGameObjectByName(selector.parent);
            if (parent == null || (!selector.includeInactive && !parent.activeInHierarchy))
                return null;

            for (int i = 0; i < parent.transform.childCount; i++)
                CollectSelectorMatches(parent.transform.GetChild(i), selector, results);
        }
        else
        {
            foreach (Transform root in EnumerateAllRootTransforms())
                CollectSelectorMatches(root, selector, results);
        }

        return selector.index < results.Count ? results[selector.index] : null;
    }

    static void CollectSelectorMatches(Transform current, SelectorQuery selector, List<GameObject> results)
    {
        if (!selector.includeInactive && !current.gameObject.activeInHierarchy)
            return;

        bool nameMatches = string.IsNullOrEmpty(selector.name) ||
            current.name.IndexOf(selector.name, StringComparison.OrdinalIgnoreCase) >= 0;
        if (nameMatches && MatchesComponent(current.gameObject, selector.component))
            results.Add(current.gameObject);

        for (int i = 0; i < current.childCount; i++)
            CollectSelectorMatches(current.GetChild(i), selector, results);
    }

    static bool MatchesComponent(GameObject go, string componentName)
    {
        if (string.IsNullOrEmpty(componentName))
            return true;

        return FindComponentByTypeName(go, componentName) != null;
    }

    static string NormalizeHierarchyPath(string path)
    {
        return path.Trim().Trim('/');
    }

    static IEnumerable<Transform> EnumerateAllRootTransforms()
    {
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                yield return root.transform;
        }

        // DontDestroyOnLoad is PlayMode-only; there is no DDOL scene in Edit Mode.
        if (!EditorApplication.isPlaying) yield break;

        GameObject temp = null;
        try
        {
            temp = new GameObject("__BridgeRootProbe__");
            UnityEngine.Object.DontDestroyOnLoad(temp);
            Scene ddolScene = temp.scene;
            UnityEngine.Object.DestroyImmediate(temp);
            temp = null;
            foreach (GameObject root in ddolScene.GetRootGameObjects())
                yield return root.transform;
        }
        finally
        {
            if (temp != null)
                UnityEngine.Object.DestroyImmediate(temp);
        }
    }

    static GameObject FindByPathRecursive(Transform current, string normalizedPath, bool includeInactive)
    {
        if (!includeInactive && !current.gameObject.activeInHierarchy)
            return null;

        if (GetHierarchyPath(current).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            return current.gameObject;

        for (int i = 0; i < current.childCount; i++)
        {
            GameObject found = FindByPathRecursive(current.GetChild(i), normalizedPath, includeInactive);
            if (found != null)
                return found;
        }

        return null;
    }

    static GameObject FindInChildren(Transform current, string name)
    {
        if (current.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            return current.gameObject;
        for (int i = 0; i < current.childCount; i++)
        {
            GameObject found = FindInChildren(current.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    static bool TryGetDisplayedText(GameObject go, out string text, out string source)
    {
        text = null;
        source = null;

        var unityText = go.GetComponent<Text>();
        if (unityText != null)
        {
            text = unityText.text;
            source = unityText.GetType().Name;
            return true;
        }

        foreach (var comp in go.GetComponents<Component>())
        {
            if (comp == null) continue;
            var type = comp.GetType();
            if (type.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var prop = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead || prop.PropertyType != typeof(string))
                continue;

            try
            {
                text = prop.GetValue(comp, null) as string;
                source = type.Name;
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    static string DumpTarget(GameObject go)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name: " + go.name);
        sb.AppendLine("path: " + GetHierarchyPath(go.transform));
        sb.AppendLine("activeSelf: " + go.activeSelf);
        sb.AppendLine("activeInHierarchy: " + go.activeInHierarchy);
        sb.AppendLine("layer: " + LayerMask.LayerToName(go.layer) + " (" + go.layer + ")");
        sb.AppendLine("tag: " + go.tag);
        sb.AppendLine("scene: " + go.scene.name);

        var rect = go.GetComponent<RectTransform>();
        if (rect != null)
        {
            sb.AppendLine("rectTransform.anchoredPosition: (" + rect.anchoredPosition.x.ToString("F1") + ", " + rect.anchoredPosition.y.ToString("F1") + ")");
            sb.AppendLine("rectTransform.sizeDelta: (" + rect.sizeDelta.x.ToString("F1") + ", " + rect.sizeDelta.y.ToString("F1") + ")");
            sb.AppendLine("rectTransform.siblingIndex: " + rect.GetSiblingIndex());
        }

        if (TryGetDisplayedText(go, out string text, out string source))
            sb.AppendLine("text[" + source + "]: " + (text ?? string.Empty));

        sb.AppendLine("components:");
        foreach (var comp in go.GetComponents<Component>())
        {
            if (comp == null)
                sb.AppendLine("  - (missing script)");
            else
                sb.AppendLine("  - " + comp.GetType().Name);
        }

        return sb.ToString().TrimEnd();
    }

    static void CollectActiveWindows(Transform current, List<string> results)
    {
        if (!current.gameObject.activeInHierarchy) return;
        // Detect GUIWindow subclasses via reflection (avoid hard dependency)
        foreach (var comp in current.GetComponents<Component>())
        {
            if (comp == null) continue;
            Type t = comp.GetType();
            // Walk inheritance chain to find GUIWindow-derived classes
            Type check = t;
            while (check != null && check != typeof(MonoBehaviour))
            {
                if (check.Name == "GUIWindow")
                {
                    results.Add(t.Name + " (" + GetHierarchyPath(current) + ")");
                    return; // One entry per GO
                }
                check = check.BaseType;
            }
        }
        for (int i = 0; i < current.childCount; i++)
            CollectActiveWindows(current.GetChild(i), results);
    }

    static string WaitForState(SelectorQuery selector, string state, string value, int timeoutMs, int pollMs, string logTypeFilter = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string lastSnapshot = "state not evaluated";

        while (sw.ElapsedMilliseconds <= timeoutMs)
        {
            string result = RunOnMainThread(() =>
            {
                // log state doesn't need a GameObject
                if (state == "log" || state == "log-contains")
                {
                    if (string.IsNullOrEmpty(value))
                        return "error: missing value param for state=" + state;

                    LogType parsedLogType = default;
                    bool hasLogTypeFilter = !string.IsNullOrEmpty(logTypeFilter);
                    if (hasLogTypeFilter && !Enum.TryParse(logTypeFilter, true, out parsedLogType))
                        return "error: unsupported type '" + logTypeFilter + "'. supports: Log, Warning, Error, Exception, Assert";

                    lock (logLock)
                    {
                        var match = logBuffer.AsEnumerable().Reverse()
                            .Where(e => !hasLogTypeFilter || e.type == parsedLogType)
                            .FirstOrDefault(e =>
                                (!string.IsNullOrEmpty(e.message) &&
                                 e.message.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (!string.IsNullOrEmpty(e.stackTrace) &&
                                 e.stackTrace.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0));

                        if (!string.IsNullOrEmpty(match.message) || !string.IsNullOrEmpty(match.stackTrace))
                        {
                            string matchedText = !string.IsNullOrEmpty(match.message) ? match.message : match.stackTrace;
                            lastSnapshot = "found[" + match.type + "]: " + matchedText.Substring(0, Math.Min(matchedText.Length, 80));
                            return null;
                        }
                        lastSnapshot = hasLogTypeFilter
                            ? "no " + parsedLogType + " log match for '" + value + "'"
                            : "no log match for '" + value + "'";
                        return "waiting";
                    }
                }

                GameObject go = FindGameObject(selector);
                switch (state)
                {
                    case "exists":
                        lastSnapshot = go != null ? "exists" : "missing";
                        return go != null ? null : "waiting";
                    case "missing":
                    case "gone":
                        lastSnapshot = go != null ? "exists" : "missing";
                        return go == null ? null : "waiting";
                    case "active":
                        lastSnapshot = go == null ? "missing" : "activeInHierarchy=" + go.activeInHierarchy;
                        return go != null && go.activeInHierarchy ? null : "waiting";
                    case "inactive":
                        lastSnapshot = go == null ? "missing" : "activeInHierarchy=" + go.activeInHierarchy;
                        return go != null && !go.activeInHierarchy ? null : "waiting";
                    case "text":
                    case "text-equals":
                        if (string.IsNullOrEmpty(value))
                            return "error: missing value param for state=" + state;
                        if (go == null)
                        {
                            lastSnapshot = "missing";
                            return "waiting";
                        }
                        if (!TryGetDisplayedText(go, out string text, out _))
                        {
                            lastSnapshot = "no text component";
                            return "waiting";
                        }
                        lastSnapshot = "text=" + (text ?? string.Empty);
                        return string.Equals(text, value, StringComparison.Ordinal) ? null : "waiting";
                    case "text-contains":
                        if (string.IsNullOrEmpty(value))
                            return "error: missing value param for state=text-contains";
                        if (go == null)
                        {
                            lastSnapshot = "missing";
                            return "waiting";
                        }
                        if (!TryGetDisplayedText(go, out string containsText, out _))
                        {
                            lastSnapshot = "no text component";
                            return "waiting";
                        }
                        lastSnapshot = "text=" + (containsText ?? string.Empty);
                        return !string.IsNullOrEmpty(containsText) && containsText.IndexOf(value, StringComparison.Ordinal) >= 0 ? null : "waiting";
                    default:
                        return "error: unsupported state '" + state + "'. supports: exists, missing, active, inactive, text, text-equals, text-contains, log, log-contains";
                }
            });

            if (result == null)
                return "ok: state '" + state + "' satisfied after " + sw.ElapsedMilliseconds + " ms";
            if (result.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return result;

            Thread.Sleep(pollMs);
        }

        return "timeout: state '" + state + "' not satisfied after " + timeoutMs + " ms; last=" + lastSnapshot;
    }

    static string WaitForState(string name, string path, string state, string value, bool includeInactive, int timeoutMs, int pollMs, string logTypeFilter = null)
    {
        return WaitForState(new SelectorQuery
        {
            name = name,
            path = path,
            index = 0,
            includeInactive = includeInactive
        }, state, value, timeoutMs, pollMs, logTypeFilter);
    }

    // Playwright-style actionability polling. Resolves the target each tick and re-checks
    // all requested conditions until they pass or timeout. Returns null on pass, or a
    // human-readable failure reason on timeout. Stable requires bbox equality across 2
    // consecutive polls, mirroring Playwright's "two consecutive animation frames" rule.
    static string CheckActionability(Func<GameObject> resolveTarget, Actionability req, int timeoutMs, int pollMs)
    {
        if (req == Actionability.None) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Rect prevBbox = default;
        bool hasPrevBbox = false;
        string lastFail = "not evaluated";

        while (true)
        {
            Rect newBbox = default;
            bool sawBbox = false;
            Rect capturedPrev = prevBbox;
            bool capturedHas = hasPrevBbox;

            string result = RunOnMainThread(() =>
            {
                GameObject go = resolveTarget();
                if (go == null) return "target not found";

                if ((req & Actionability.Visible) != 0)
                {
                    if (!go.activeInHierarchy) return "inactive";
                    var cg = go.GetComponentInParent<CanvasGroup>();
                    if (cg != null && cg.alpha <= 0f) return "CanvasGroup alpha=0";
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        Vector3[] corners = new Vector3[4];
                        rt.GetWorldCorners(corners);
                        float w = Vector3.Distance(corners[0], corners[3]);
                        float h = Vector3.Distance(corners[0], corners[1]);
                        if (w <= 0f || h <= 0f) return "empty bbox";
                    }
                }

                if ((req & Actionability.Stable) != 0)
                {
                    var rt = go.GetComponent<RectTransform>();
                    Rect bbox = default;
                    if (rt != null)
                    {
                        Vector3[] corners = new Vector3[4];
                        rt.GetWorldCorners(corners);
                        Vector3 min = corners[0]; Vector3 max = corners[2];
                        bbox = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
                    }
                    newBbox = bbox;
                    sawBbox = true;
                    if (!capturedHas) return "stable: baseline";
                    if (!RectApproxEqual(capturedPrev, bbox)) return "stable: bbox moving";
                }

                if ((req & Actionability.ReceivesEvents) != 0)
                {
                    EventSystem es = EventSystem.current;
                    if (es == null) return "no EventSystem";
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) return "no RectTransform";
                    Canvas canvas = rt.GetComponentInParent<Canvas>();
                    if (canvas == null) return "no parent Canvas";
                    Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : (canvas.worldCamera ?? Camera.main);
                    Vector3[] corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    Vector3 worldCenter = (corners[0] + corners[2]) * 0.5f;
                    Vector2 screen = cam != null ? (Vector2)cam.WorldToScreenPoint(worldCenter) : (Vector2)worldCenter;
                    var ped = new PointerEventData(es) { position = screen };
                    var results = new List<RaycastResult>();
                    es.RaycastAll(ped, results);
                    if (results.Count == 0) return "no raycast hit";
                    GameObject hit = results[0].gameObject;
                    Transform walk = hit.transform;
                    while (walk != null && walk != go.transform) walk = walk.parent;
                    if (walk == null) return "occluded by " + hit.name;
                }

                if ((req & Actionability.Enabled) != 0)
                {
                    var sel = go.GetComponent<UnityEngine.UI.Selectable>();
                    if (sel != null && !sel.interactable) return "Selectable.interactable=false";
                    var cg = go.GetComponentInParent<CanvasGroup>();
                    if (cg != null && !cg.interactable) return "CanvasGroup.interactable=false";
                    if (cg != null && !cg.blocksRaycasts) return "CanvasGroup.blocksRaycasts=false";
                }

                if ((req & Actionability.Editable) != 0)
                {
                    var input = go.GetComponent<InputField>();
                    if (input != null && input.readOnly) return "InputField.readOnly=true";
                    var tmp = go.GetComponent("TMP_InputField");
                    if (tmp != null)
                    {
                        var prop = tmp.GetType().GetProperty("readOnly");
                        if (prop != null)
                        {
                            object v = prop.GetValue(tmp, null);
                            if (v is bool b && b) return "TMP_InputField.readOnly=true";
                        }
                    }
                }

                return null;
            });

            if ((req & Actionability.Stable) != 0 && sawBbox)
            {
                prevBbox = newBbox;
                hasPrevBbox = true;
            }

            if (result == null) return null;
            lastFail = result;

            if (sw.ElapsedMilliseconds > timeoutMs)
                return "actionability timeout (" + timeoutMs + "ms): " + lastFail;

            Thread.Sleep(pollMs);
        }
    }

    static bool RectApproxEqual(Rect a, Rect b)
    {
        const float eps = 0.5f;
        return Mathf.Abs(a.x - b.x) < eps
            && Mathf.Abs(a.y - b.y) < eps
            && Mathf.Abs(a.width - b.width) < eps
            && Mathf.Abs(a.height - b.height) < eps;
    }

    // Playwright-style web-first assertion. Polls EvaluateExpect until pass or timeout.
    static string RunExpect(SelectorQuery sel, string cond, string value, int timeoutMs, int pollMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string[] snapBuf = { "not evaluated" };

        while (true)
        {
            string result = RunOnMainThread(() => EvaluateExpect(sel, cond, value, snapBuf));
            if (result == null)
                return "ok: " + cond + " satisfied after " + sw.ElapsedMilliseconds + " ms";
            if (result.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return result;
            if (sw.ElapsedMilliseconds > timeoutMs)
                return "fail: " + cond + " not met after " + timeoutMs + " ms; last=" + snapBuf[0];
            Thread.Sleep(pollMs);
        }
    }

    // Returns null=pass, "waiting"=retry, "error:..."=permanent. snapBuf[0] carries
    // the latest observed state for timeout diagnostics.
    static string EvaluateExpect(SelectorQuery sel, string cond, string value, string[] snapBuf)
    {
        switch (cond)
        {
            case "tobeattached":
            {
                GameObject go = FindGameObject(sel);
                snapBuf[0] = go != null ? "attached" : "detached";
                return go != null ? null : "waiting";
            }
            case "tobevisible":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                string r = VisibleReason(go);
                snapBuf[0] = r ?? "visible";
                return r == null ? null : "waiting";
            }
            case "tobehidden":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "detached"; return null; }
                string r = VisibleReason(go);
                snapBuf[0] = r ?? "visible";
                return r != null ? null : "waiting";
            }
            case "tobeenabled":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                string r = EnabledReason(go);
                snapBuf[0] = r ?? "enabled";
                return r == null ? null : "waiting";
            }
            case "tobedisabled":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                string r = EnabledReason(go);
                snapBuf[0] = r ?? "enabled";
                return r != null ? null : "waiting";
            }
            case "tobeeditable":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                string er = EnabledReason(go);
                if (er != null) { snapBuf[0] = er; return "waiting"; }
                string edr = EditableReason(go);
                snapBuf[0] = edr ?? "editable";
                return edr == null ? null : "waiting";
            }
            case "tobefocused":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                EventSystem es = EventSystem.current;
                if (es == null) { snapBuf[0] = "no EventSystem"; return "waiting"; }
                GameObject selObj = es.currentSelectedGameObject;
                snapBuf[0] = "selected=" + (selObj != null ? selObj.name : "none");
                return selObj == go ? null : "waiting";
            }
            case "tohavetext":
            {
                if (value == null) return "error: missing value for toHaveText";
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                if (!TryGetDisplayedText(go, out string text, out _)) { snapBuf[0] = "no text component"; return "waiting"; }
                snapBuf[0] = "text=" + (text ?? "");
                return string.Equals(text, value, StringComparison.Ordinal) ? null : "waiting";
            }
            case "tocontaintext":
            {
                if (value == null) return "error: missing value for toContainText";
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                if (!TryGetDisplayedText(go, out string text, out _)) { snapBuf[0] = "no text component"; return "waiting"; }
                snapBuf[0] = "text=" + (text ?? "");
                return !string.IsNullOrEmpty(text) && text.IndexOf(value, StringComparison.Ordinal) >= 0 ? null : "waiting";
            }
            case "tohavevalue":
            {
                if (value == null) return "error: missing value for toHaveValue";
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                string cur = null;
                var input = go.GetComponent<InputField>();
                if (input != null) cur = input.text;
                else
                {
                    var tmp = go.GetComponent("TMP_InputField");
                    if (tmp != null)
                    {
                        var prop = tmp.GetType().GetProperty("text");
                        if (prop != null) cur = prop.GetValue(tmp, null) as string;
                    }
                }
                if (cur == null) { snapBuf[0] = "no InputField/TMP_InputField"; return "waiting"; }
                snapBuf[0] = "value=" + cur;
                return string.Equals(cur, value, StringComparison.Ordinal) ? null : "waiting";
            }
            case "tohavecount":
            {
                if (value == null) return "error: missing value for toHaveCount";
                if (!int.TryParse(value, out int expected)) return "error: toHaveCount value must be integer";
                var matches = new List<GameObject>();
                foreach (Transform root in EnumerateAllRootTransforms())
                    CollectSelectorMatches(root, sel, matches);
                snapBuf[0] = "count=" + matches.Count;
                return matches.Count == expected ? null : "waiting";
            }
            case "tobeinviewport":
            {
                GameObject go = FindGameObject(sel);
                if (go == null) { snapBuf[0] = "not found"; return "waiting"; }
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) { snapBuf[0] = "no RectTransform"; return "waiting"; }
                Canvas canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null) { snapBuf[0] = "no Canvas"; return "waiting"; }
                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : (canvas.worldCamera ?? Camera.main);
                Vector3[] corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float sw = Screen.width, sh = Screen.height;
                bool anyIn = false;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 sp = cam != null ? (Vector2)cam.WorldToScreenPoint(corners[i]) : (Vector2)corners[i];
                    if (sp.x >= 0 && sp.x <= sw && sp.y >= 0 && sp.y <= sh) { anyIn = true; break; }
                }
                snapBuf[0] = anyIn ? "in viewport" : "off screen";
                return anyIn ? null : "waiting";
            }
        }
        return "error: unsupported condition '" + cond + "'. supports: toBeAttached, toBeVisible, toBeHidden, toBeEnabled, toBeDisabled, toBeEditable, toBeFocused, toHaveText, toContainText, toHaveValue, toHaveCount, toBeInViewport";
    }

    static string VisibleReason(GameObject go)
    {
        if (!go.activeInHierarchy) return "inactive";
        var cg = go.GetComponentInParent<CanvasGroup>();
        if (cg != null && cg.alpha <= 0f) return "CanvasGroup alpha=0";
        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            float w = Vector3.Distance(corners[0], corners[3]);
            float h = Vector3.Distance(corners[0], corners[1]);
            if (w <= 0f || h <= 0f) return "empty bbox";
        }
        return null;
    }

    static string EnabledReason(GameObject go)
    {
        var sel = go.GetComponent<UnityEngine.UI.Selectable>();
        if (sel != null && !sel.interactable) return "Selectable.interactable=false";
        var cg = go.GetComponentInParent<CanvasGroup>();
        if (cg != null && !cg.interactable) return "CanvasGroup.interactable=false";
        if (cg != null && !cg.blocksRaycasts) return "CanvasGroup.blocksRaycasts=false";
        return null;
    }

    // Captures a Game View screenshot and blocks until the file exists on disk.
    // Returns the full file path on success, or "error: ..." on failure.
    static string CaptureScreenshotAndWait()
    {
        string outputPath = RunOnMainThread(() =>
        {
            string dir = Path.GetFullPath(ScreenshotDir);
            Directory.CreateDirectory(dir);
            string filePath = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            if (File.Exists(filePath)) File.Delete(filePath);

            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType != null)
            {
                var gv = EditorWindow.GetWindow(gameViewType);
                if (gv != null) gv.Focus();
            }

            ScreenCapture.CaptureScreenshot(filePath);
            return filePath;
        });

        if (outputPath.StartsWith("error:")) return outputPath;

        for (int i = 0; i < 50; i++) // max 10 seconds
        {
            Thread.Sleep(200);
            if (File.Exists(outputPath)) return outputPath;
        }
        return "error: screenshot timed out: " + outputPath;
    }

    static string EditableReason(GameObject go)
    {
        var input = go.GetComponent<InputField>();
        if (input != null && input.readOnly) return "InputField.readOnly=true";
        var tmp = go.GetComponent("TMP_InputField");
        if (tmp != null)
        {
            var prop = tmp.GetType().GetProperty("readOnly");
            if (prop != null)
            {
                object v = prop.GetValue(tmp, null);
                if (v is bool b && b) return "TMP_InputField.readOnly=true";
            }
        }
        return null;
    }

    // --- Component Reflection helpers ---

    static readonly HashSet<string> ReflectExcludedTypes = new HashSet<string>
        { "MonoBehaviour", "Behaviour", "Component", "Object" };

    static Component FindComponentByTypeName(GameObject go, string typeName)
    {
        foreach (var comp in go.GetComponents<Component>())
        {
            if (comp == null) continue;
            if (comp.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
        return null;
    }

    static string InspectComponent(Component comp)
    {
        var sb = new StringBuilder();
        Type type = comp.GetType();
        sb.AppendLine("[" + type.Name + "] on " + comp.gameObject.name);

        sb.AppendLine("--- Fields ---");
        Type t = type;
        while (t != null && !ReflectExcludedTypes.Contains(t.Name))
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                try { sb.AppendLine("  " + f.FieldType.Name + " " + f.Name + " = " + SerializeReflectedValue(f.GetValue(comp))); }
                catch (Exception e) { sb.AppendLine("  " + f.FieldType.Name + " " + f.Name + " = (error: " + e.Message + ")"); }
            }
            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!f.IsDefined(typeof(SerializeField), false)) continue;
                try { sb.AppendLine("  [S] " + f.FieldType.Name + " " + f.Name + " = " + SerializeReflectedValue(f.GetValue(comp))); }
                catch (Exception e) { sb.AppendLine("  [S] " + f.FieldType.Name + " " + f.Name + " = (error: " + e.Message + ")"); }
            }
            t = t.BaseType;
        }

        sb.AppendLine("--- Properties ---");
        t = type;
        while (t != null && !ReflectExcludedTypes.Contains(t.Name))
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                string rw = p.CanWrite ? "rw" : "r";
                try { sb.AppendLine("  " + p.PropertyType.Name + " " + p.Name + " (" + rw + ") = " + SerializeReflectedValue(p.GetValue(comp))); }
                catch (Exception e) { sb.AppendLine("  " + p.PropertyType.Name + " " + p.Name + " (" + rw + ") = (error: " + e.Message + ")"); }
            }
            t = t.BaseType;
        }

        return sb.ToString().TrimEnd();
    }

    static string GetComponentValue(Component comp, string propName)
    {
        Type type = comp.GetType();
        var field = FindReflectField(type, propName);
        if (field != null)
        {
            try { return SerializeReflectedValue(field.GetValue(comp)); }
            catch (Exception e) { return "error: " + e.Message; }
        }
        var prop = FindReflectProperty(type, propName);
        if (prop != null && prop.CanRead)
        {
            try { return SerializeReflectedValue(prop.GetValue(comp)); }
            catch (Exception e) { return "error: " + e.Message; }
        }
        return "error: field/property not found: " + propName;
    }

    static string SetComponentValue(Component comp, string propName, string valueStr)
    {
        Type type = comp.GetType();
        var field = FindReflectField(type, propName);
        if (field != null)
        {
            try
            {
                object val = DeserializeReflectedValue(valueStr, field.FieldType);
                field.SetValue(comp, val);
                return "set " + propName + " = " + SerializeReflectedValue(field.GetValue(comp));
            }
            catch (Exception e) { return "error: " + e.Message; }
        }
        var prop = FindReflectProperty(type, propName);
        if (prop != null)
        {
            if (!prop.CanWrite) return "error: property is read-only: " + propName;
            try
            {
                object val = DeserializeReflectedValue(valueStr, prop.PropertyType);
                prop.SetValue(comp, val);
                return "set " + propName + " = " + SerializeReflectedValue(prop.GetValue(comp));
            }
            catch (Exception e) { return "error: " + e.Message; }
        }
        return "error: field/property not found: " + propName;
    }

    static FieldInfo FindReflectField(Type type, string name)
    {
        Type t = type;
        while (t != null && !ReflectExcludedTypes.Contains(t.Name))
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.IsPublic || f.IsDefined(typeof(SerializeField), false)) return f;
            }
            t = t.BaseType;
        }
        return null;
    }

    static PropertyInfo FindReflectProperty(Type type, string name)
    {
        Type t = type;
        while (t != null && !ReflectExcludedTypes.Contains(t.Name))
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            t = t.BaseType;
        }
        return null;
    }

    static string SerializeReflectedValue(object val)
    {
        if (val == null) return "null";
        if (val is UnityEngine.Object uobj && !uobj) return "null (destroyed)";
        Type t = val.GetType();
        if (t.IsPrimitive || val is string || t.IsEnum) return val.ToString();
        if (val is Vector2 v2) return $"({v2.x}, {v2.y})";
        if (val is Vector3 v3) return $"({v3.x}, {v3.y}, {v3.z})";
        if (val is Vector4 v4) return $"({v4.x}, {v4.y}, {v4.z}, {v4.w})";
        if (val is Quaternion q) return $"({q.eulerAngles.x}, {q.eulerAngles.y}, {q.eulerAngles.z})";
        if (val is Color c) return $"({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";
        if (val is Color32 c32) return $"({c32.r}, {c32.g}, {c32.b}, {c32.a})";
        if (val is Rect r) return $"(x:{r.x}, y:{r.y}, w:{r.width}, h:{r.height})";
        if (val is Bounds b) return $"(center:{b.center}, size:{b.size})";
        if (val is UnityEngine.Object obj) return obj.name + " [id=" + obj.GetInstanceID() + "]";
        if (val is System.Collections.IList list) return t.Name + "[" + list.Count + "]";
        if (val is Array arr) return t.Name + "[" + arr.Length + "]";
        try { return JsonUtility.ToJson(val); }
        catch { return val.ToString(); }
    }

    static object DeserializeReflectedValue(string str, Type targetType)
    {
        if (targetType == typeof(int)) return int.Parse(str);
        if (targetType == typeof(float)) return float.Parse(str);
        if (targetType == typeof(double)) return double.Parse(str);
        if (targetType == typeof(long)) return long.Parse(str);
        if (targetType == typeof(bool))
        {
            if (str == "1") return true;
            if (str == "0") return false;
            return bool.Parse(str);
        }
        if (targetType == typeof(string)) return str;
        if (targetType.IsEnum) return Enum.Parse(targetType, str, true);
        // Complex types (Vector2/3/4, Color, Quaternion, custom [Serializable] structs)
        return JsonUtility.FromJson(str, targetType);
    }

    // --- Hierarchy helpers ---

    static string GetSceneHierarchy(int depth, int limit, string root)
    {
        var sb = new StringBuilder();
        int count = 0;

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            sb.AppendLine("[" + scene.name + "] " + scene.path);

            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (!string.IsNullOrEmpty(root) &&
                    go.name.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                BuildHierarchyTree(go.transform, sb, 0, depth, ref count, limit);
                if (count >= limit) break;
            }
            if (count >= limit) break;
        }

        if (count >= limit)
            sb.AppendLine("... truncated at " + limit + " objects");

        sb.Insert(0, "total: " + count + " objects\n");
        return sb.ToString().TrimEnd();
    }

    static void BuildHierarchyTree(Transform t, StringBuilder sb, int indent, int maxDepth, ref int count, int limit)
    {
        if (count >= limit) return;
        count++;

        sb.Append(' ', indent * 2);
        sb.Append(t.name);
        if (!t.gameObject.activeSelf) sb.Append(" [off]");
        sb.AppendLine();

        if (indent >= maxDepth) return;
        for (int i = 0; i < t.childCount; i++)
            BuildHierarchyTree(t.GetChild(i), sb, indent + 1, maxDepth, ref count, limit);
    }

    // --- Find helpers ---

    static string FindGameObjectsBySearch(string search, string by, bool includeInactive)
    {
        var results = new List<string>();
        string byLower = by.ToLower();

        // Collect root transforms from all scenes + DDOL
        var stack = new Stack<Transform>();

        // DontDestroyOnLoad (collect first, push last so scenes are processed first)
        var ddolRoots = new List<Transform>();
        GameObject temp = null;
        try
        {
            temp = new GameObject("__BridgeFindProbe__");
            UnityEngine.Object.DontDestroyOnLoad(temp);
            Scene ddolScene = temp.scene;
            UnityEngine.Object.DestroyImmediate(temp);
            temp = null;
            foreach (GameObject root in ddolScene.GetRootGameObjects())
                ddolRoots.Add(root.transform);
        }
        catch { if (temp != null) UnityEngine.Object.DestroyImmediate(temp); }

        // Push DDOL roots (processed after scene roots due to stack LIFO)
        for (int i = ddolRoots.Count - 1; i >= 0; i--)
            stack.Push(ddolRoots[i]);

        // Push scene roots
        for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            var roots = scene.GetRootGameObjects();
            for (int i = roots.Length - 1; i >= 0; i--)
                stack.Push(roots[i].transform);
        }

        // Iterative DFS
        while (stack.Count > 0)
        {
            Transform t = stack.Pop();
            GameObject go = t.gameObject;

            if (!includeInactive && !go.activeInHierarchy) continue;

            bool match = false;
            switch (byLower)
            {
                case "name":
                    match = go.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                    break;
                case "tag":
                    try { match = go.CompareTag(search); } catch { }
                    break;
                case "layer":
                    match = LayerMask.LayerToName(go.layer).Equals(search, StringComparison.OrdinalIgnoreCase);
                    break;
                case "component":
                    match = go.GetComponents<Component>().Any(c => c != null &&
                        c.GetType().Name.Equals(search, StringComparison.OrdinalIgnoreCase));
                    break;
            }

            if (match)
                results.Add(GetHierarchyPath(t) + (go.activeInHierarchy ? "" : " [inactive]"));

            // Push children in reverse order for correct traversal order
            for (int i = t.childCount - 1; i >= 0; i--)
                stack.Push(t.GetChild(i));
        }

        if (results.Count == 0) return "no matches for '" + search + "' (by " + by + ")";
        return "found " + results.Count + ":\n" + string.Join("\n", results);
    }

    static string FindGameObjectsByText(string searchText, bool exact)
    {
        var results = new List<string>();
        var stack = new Stack<Transform>();

        if (EditorApplication.isPlaying)
        {
            foreach (Transform root in EnumerateAllRootTransforms())
                stack.Push(root);
        }
        else
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    stack.Push(root.transform);
            }
        }

        while (stack.Count > 0)
        {
            Transform t = stack.Pop();
            if (TryGetDisplayedText(t.gameObject, out string text, out string source))
            {
                bool match = exact
                    ? string.Equals(text, searchText, StringComparison.Ordinal)
                    : (!string.IsNullOrEmpty(text) && text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match)
                    results.Add(GetHierarchyPath(t) + " [" + source + ": " + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "]");
            }
            for (int i = t.childCount - 1; i >= 0; i--)
                stack.Push(t.GetChild(i));
        }

        if (results.Count == 0) return "no matches for text '" + searchText + "'";
        return "found " + results.Count + ":\n" + string.Join("\n", results);
    }

    // --- Scenario runner helpers ---

    static string HandleScenarioRun(HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = reader.ReadToEnd();

        if (string.IsNullOrWhiteSpace(body))
            return "{\"ok\":false,\"error\":\"invalid body. expected scenario JSON\"}";

        ScenarioRequest scenario;
        try
        {
            scenario = JsonUtility.FromJson<ScenarioRequest>(body);
        }
        catch (Exception e)
        {
            return "{\"ok\":false,\"error\":\"invalid JSON: " + EscapeJsonString(e.Message) + "\"}";
        }

        if (scenario == null || scenario.steps == null || scenario.steps.Length == 0)
            return "{\"ok\":false,\"error\":\"scenario requires at least one step\"}";

        string scenarioName = string.IsNullOrWhiteSpace(scenario.name) ? "scenario" : scenario.name.Trim();
        ScenarioRunContext runContext;
        try
        {
            runContext = CreateScenarioRunContext(scenarioName);
        }
        catch (Exception e)
        {
            return "{\"ok\":false,\"error\":\"failed to create run directory: " + EscapeJsonString(e.Message) + "\"}";
        }

        bool stopOnFailure = !ContainsJsonProperty(body, "stopOnFailure") || scenario.stopOnFailure;
        bool diagnoseOnFailure = !ContainsJsonProperty(body, "diagnoseOnFailure") || scenario.diagnoseOnFailure;
        int defaultTimeoutMs = scenario.defaultTimeoutMs > 0 ? scenario.defaultTimeoutMs : 5000;
        int defaultPollMs = scenario.defaultPollMs > 0 ? scenario.defaultPollMs : 100;

        var runSw = System.Diagnostics.Stopwatch.StartNew();
        var stepReports = new List<string>();
        var artifactPaths = new List<string>();
        bool ok = true;
        int failedStep = -1;
        string diagnoseResult = null;

        for (int i = 0; i < scenario.steps.Length; i++)
        {
            ScenarioStep step = scenario.steps[i] ?? new ScenarioStep();
            string action = NormalizeScenarioAction(step.action);
            string stepName = string.IsNullOrWhiteSpace(step.name) ? action : step.name.Trim();
            string path;
            string buildError;

            if (!TryBuildScenarioPath(step, action, defaultTimeoutMs, defaultPollMs, out path, out buildError))
            {
                ok = false;
                failedStep = i;
                stepReports.Add(BuildScenarioStepReport(i, stepName, action, false, 0, null, buildError, null));
                if (diagnoseOnFailure)
                {
                    diagnoseResult = ExecuteScenarioDiagnose(step, defaultTimeoutMs, defaultPollMs);
                    AddUniqueArtifacts(artifactPaths, CopyArtifactsToRunDir(ExtractArtifactPaths(diagnoseResult), runContext, i, stepName));
                }
                if (stopOnFailure)
                    break;
                continue;
            }

            var stepSw = System.Diagnostics.Stopwatch.StartNew();
            string result = ExecuteLocalRoutePath(path, Math.Max(30000, StepTimeoutMs(step, defaultTimeoutMs) + 5000));
            stepSw.Stop();

            bool stepOk = ScenarioResultSucceeded(action, result);
            if (!stepOk && ok)
            {
                ok = false;
                failedStep = i;
                if (diagnoseOnFailure)
                {
                    diagnoseResult = ExecuteScenarioDiagnose(step, defaultTimeoutMs, defaultPollMs);
                    AddUniqueArtifacts(artifactPaths, CopyArtifactsToRunDir(ExtractArtifactPaths(diagnoseResult), runContext, i, stepName));
                }
            }

            List<string> stepArtifacts = CopyArtifactsToRunDir(ExtractArtifactPaths(result), runContext, i, stepName);
            AddUniqueArtifacts(artifactPaths, stepArtifacts);
            stepReports.Add(BuildScenarioStepReport(i, stepName, action, stepOk, stepSw.ElapsedMilliseconds, path, result, stepArtifacts));
            if (!stepOk && stopOnFailure)
                break;
        }

        runSw.Stop();
        string logPath = WriteScenarioLogs(runContext, 200);
        AddUniqueArtifacts(artifactPaths, new[] { logPath });
        AddUniqueArtifacts(artifactPaths, new[] { runContext.reportPath });
        string report = BuildScenarioReport(ok, scenarioName, runContext, runSw.ElapsedMilliseconds, failedStep,
            runContext.reportPath, stepReports, diagnoseResult, artifactPaths);
        WriteScenarioReport(runContext, report);

        return report;
    }

    static bool TryBuildScenarioPath(ScenarioStep step, string action, int defaultTimeoutMs, int defaultPollMs,
        out string path, out string error)
    {
        path = null;
        error = null;
        int timeoutMs = StepTimeoutMs(step, defaultTimeoutMs);
        int pollMs = StepPollMs(step, defaultPollMs, timeoutMs);

        switch (action)
        {
            case "route":
                if (string.IsNullOrWhiteSpace(step.path) || !step.path.StartsWith("/"))
                {
                    error = "route action requires path starting with /";
                    return false;
                }
                path = step.path;
                return true;

            case "checkplaymode":
            {
                var p = new List<string>();
                if (step.logWindowSec > 0) AddParam(p, "logWindowSec", step.logWindowSec.ToString());
                path = "/playmode/check" + ToQueryString(p);
                return true;
            }

            case "wait":
            {
                var p = BuildSelectorParams(step.selector);
                AddParam(p, "state", string.IsNullOrEmpty(step.state) ? "exists" : step.state);
                AddParam(p, "value", step.value);
                AddParam(p, "type", step.type);
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/wait-for" + ToQueryString(p);
                return true;
            }

            case "expect":
            {
                if (string.IsNullOrWhiteSpace(step.condition))
                {
                    error = "expect action requires condition";
                    return false;
                }
                var p = BuildSelectorParams(step.selector);
                AddParam(p, "condition", step.condition);
                AddParam(p, "value", step.value);
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/expect" + ToQueryString(p);
                return true;
            }

            case "click":
            {
                var p = new List<string>();
                if (!string.IsNullOrEmpty(step.x) && !string.IsNullOrEmpty(step.y))
                {
                    AddParam(p, "x", step.x);
                    AddParam(p, "y", step.y);
                }
                else if (!string.IsNullOrEmpty(step.x) || !string.IsNullOrEmpty(step.y))
                {
                    error = "click coordinate action requires both x and y";
                    return false;
                }
                else if (HasScenarioSelector(step.selector))
                {
                    p = BuildSelectorParams(step.selector);
                }
                else
                {
                    error = "click action requires selector or x/y";
                    return false;
                }
                if (step.force) AddParam(p, "force", "true");
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/click" + ToQueryString(p);
                return true;
            }

            case "input":
            {
                if (step.text == null)
                {
                    error = "input action requires text";
                    return false;
                }
                var p = new List<string>();
                AddParam(p, "text", step.text);
                if (step.force) AddParam(p, "force", "true");
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/input" + ToQueryString(p);
                return true;
            }

            case "clickandwait":
            {
                if (!HasScenarioSelector(step.selector))
                {
                    error = "clickAndWait action requires selector";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(step.waitTarget))
                {
                    error = "clickAndWait action requires waitTarget";
                    return false;
                }
                var p = BuildSelectorParams(step.selector);
                AddParam(p, "waitTarget", step.waitTarget);
                AddParam(p, "waitState", string.IsNullOrEmpty(step.waitState) ? "active" : step.waitState);
                AddParam(p, "waitValue", step.waitValue);
                if (step.force) AddParam(p, "force", "true");
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/click-and-wait" + ToQueryString(p);
                return true;
            }

            case "inputandwait":
            {
                if (step.text == null)
                {
                    error = "inputAndWait action requires text";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(step.waitTarget))
                {
                    error = "inputAndWait action requires waitTarget";
                    return false;
                }
                var p = new List<string>();
                AddParam(p, "text", step.text);
                AddParam(p, "waitTarget", step.waitTarget);
                AddParam(p, "waitState", string.IsNullOrEmpty(step.waitState) ? "active" : step.waitState);
                AddParam(p, "waitValue", step.waitValue);
                if (step.force) AddParam(p, "force", "true");
                AddParam(p, "timeoutMs", timeoutMs.ToString());
                AddParam(p, "pollMs", pollMs.ToString());
                path = "/input-and-wait" + ToQueryString(p);
                return true;
            }

            case "keypress":
            {
                if (string.IsNullOrWhiteSpace(step.key))
                {
                    error = "keypress action requires key";
                    return false;
                }
                var p = new List<string>();
                AddParam(p, "key", step.key);
                path = "/keypress" + ToQueryString(p);
                return true;
            }

            case "diagnose":
            {
                var p = BuildSelectorParams(step.selector);
                if (step.logs > 0) AddParam(p, "logs", step.logs.ToString());
                path = "/diagnose" + ToQueryString(p);
                return true;
            }

            case "baselinesave":
            case "baseline.save":
            {
                string name = ScenarioBaselineName(step);
                if (string.IsNullOrEmpty(name))
                {
                    error = "baselineSave action requires baseline or name";
                    return false;
                }
                var p = new List<string>();
                AddParam(p, "name", name);
                path = "/baseline/save" + ToQueryString(p);
                return true;
            }

            case "baselinediff":
            case "baseline.diff":
            {
                string name = ScenarioBaselineName(step);
                if (string.IsNullOrEmpty(name))
                {
                    error = "baselineDiff action requires baseline or name";
                    return false;
                }
                var p = new List<string>();
                AddParam(p, "name", name);
                if (step.identicalThreshold > 0f) AddParam(p, "identicalThreshold", step.identicalThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (step.gridSize > 0) AddParam(p, "gridSize", step.gridSize.ToString());
                if (step.perPixelThreshold > 0) AddParam(p, "perPixelThreshold", step.perPixelThreshold.ToString());
                path = "/baseline/diff" + ToQueryString(p);
                return true;
            }
        }

        error = "unsupported action '" + action + "'";
        return false;
    }

    static string ExecuteLocalRoutePath(string subPath, int timeoutMs)
    {
        if (string.IsNullOrEmpty(subPath) || !subPath.StartsWith("/"))
            return "error: route path must start with /";

        string url = "http://localhost:" + _activePort + subPath;
        try
        {
            var httpReq = (HttpWebRequest)WebRequest.Create(url);
            httpReq.Timeout = timeoutMs > 0 ? timeoutMs : 30000;
            using (var resp = (HttpWebResponse)httpReq.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
        catch (WebException we)
        {
            if (we.Response != null)
            {
                using (var reader = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            return "error: " + we.Message;
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    static string ExecuteScenarioDiagnose(ScenarioStep step, int defaultTimeoutMs, int defaultPollMs)
    {
        var diag = new ScenarioStep
        {
            action = "diagnose",
            selector = step != null ? step.selector : null,
            logs = step != null && step.logs > 0 ? step.logs : 20,
            timeoutMs = defaultTimeoutMs,
            pollMs = defaultPollMs
        };

        string path;
        string error;
        if (!TryBuildScenarioPath(diag, "diagnose", defaultTimeoutMs, defaultPollMs, out path, out error))
            return error;
        return ExecuteLocalRoutePath(path, Math.Max(30000, defaultTimeoutMs + 5000));
    }

    static bool ScenarioResultSucceeded(string action, string result)
    {
        if (string.IsNullOrEmpty(result)) return false;
        string trimmed = result.TrimStart();
        if (trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("fail:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("timeout:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("actionability timeout", StringComparison.OrdinalIgnoreCase))
            return false;

        if (action == "checkplaymode")
            return trimmed.StartsWith("ready: yes", StringComparison.OrdinalIgnoreCase);

        if (action == "baselinediff" || action == "baseline.diff")
            return trimmed.IndexOf("verdict: identical", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.IndexOf("verdict: within-threshold", StringComparison.OrdinalIgnoreCase) >= 0;

        return true;
    }

    static string BuildScenarioReport(bool ok, string name, ScenarioRunContext runContext, long durationMs,
        int failedStep, string reportPath, List<string> stepReports, string diagnoseResult, List<string> artifacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"ok\": " + (ok ? "true" : "false") + ",");
        sb.AppendLine("  \"name\": \"" + EscapeJsonString(name) + "\",");
        sb.AppendLine("  \"runId\": \"" + EscapeJsonString(runContext != null ? runContext.runId : "") + "\",");
        sb.AppendLine("  \"runDir\": " + (runContext == null ? "null" : "\"" + EscapeJsonString(runContext.runDir) + "\"") + ",");
        sb.AppendLine("  \"logsPath\": " + (runContext == null ? "null" : "\"" + EscapeJsonString(runContext.logPath) + "\"") + ",");
        sb.AppendLine("  \"durationMs\": " + durationMs + ",");
        sb.AppendLine("  \"failedStep\": " + failedStep + ",");
        sb.AppendLine("  \"reportPath\": " + (string.IsNullOrEmpty(reportPath) ? "null" : "\"" + EscapeJsonString(reportPath) + "\"") + ",");
        sb.AppendLine("  \"steps\": [");
        for (int i = 0; i < stepReports.Count; i++)
        {
            sb.Append(stepReports[i]);
            if (i < stepReports.Count - 1) sb.Append(",");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");
        sb.AppendLine("  \"diagnose\": " + (string.IsNullOrEmpty(diagnoseResult) ? "null" : "\"" + EscapeJsonString(diagnoseResult) + "\"") + ",");
        sb.AppendLine("  \"artifacts\": " + FormatJsonStringArray(artifacts));
        sb.Append("}");
        return sb.ToString();
    }

    static string BuildScenarioStepReport(int index, string name, string action, bool ok, long durationMs,
        string path, string result, List<string> artifacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("    {");
        sb.AppendLine("      \"index\": " + index + ",");
        sb.AppendLine("      \"name\": \"" + EscapeJsonString(name) + "\",");
        sb.AppendLine("      \"action\": \"" + EscapeJsonString(action) + "\",");
        sb.AppendLine("      \"ok\": " + (ok ? "true" : "false") + ",");
        sb.AppendLine("      \"durationMs\": " + durationMs + ",");
        sb.AppendLine("      \"path\": " + (string.IsNullOrEmpty(path) ? "null" : "\"" + EscapeJsonString(path) + "\"") + ",");
        sb.AppendLine("      \"result\": \"" + EscapeJsonString(result ?? "") + "\",");
        sb.AppendLine("      \"artifacts\": " + FormatJsonStringArray(artifacts));
        sb.Append("    }");
        return sb.ToString();
    }

    static string FormatJsonStringArray(IEnumerable<string> values)
    {
        if (values == null) return "[]";
        var items = values.Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (items.Count == 0) return "[]";
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("\"");
            sb.Append(EscapeJsonString(items[i]));
            sb.Append("\"");
        }
        sb.Append("]");
        return sb.ToString();
    }

    static List<string> ExtractArtifactPaths(string result)
    {
        var artifacts = new List<string>();
        if (string.IsNullOrEmpty(result)) return artifacts;

        string[] prefixes =
        {
            "screenshot saved:",
            "screenshot:",
            "path:",
            "baselinePath:",
            "currentPath:",
            "diffVisualizationPath:"
        };

        string[] lines = result.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            foreach (string prefix in prefixes)
            {
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string path = NormalizeArtifactPath(line.Substring(prefix.Length).Trim());
                if (!string.IsNullOrEmpty(path) && !artifacts.Contains(path, StringComparer.OrdinalIgnoreCase))
                    artifacts.Add(path);
                break;
            }
        }

        return artifacts;
    }

    static string NormalizeArtifactPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        string path = rawPath.Trim().Trim('"');
        if (path.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("skipped", StringComparison.OrdinalIgnoreCase) ||
            path == "(none)")
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!IsPathUnder(fullPath, ScreenshotDir) && !IsPathUnder(fullPath, AgentReportDir))
                return null;
            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    static bool IsPathUnder(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    static void AddUniqueArtifacts(List<string> target, IEnumerable<string> artifacts)
    {
        if (target == null || artifacts == null) return;
        foreach (string path in artifacts)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (!target.Contains(path, StringComparer.OrdinalIgnoreCase))
                target.Add(path);
        }
    }

    static ScenarioRunContext CreateScenarioRunContext(string scenarioName)
    {
        string safeName = SafeScenarioFileName(scenarioName);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string runId = safeName + "_" + timestamp;
        string runDir = Path.Combine(Path.GetFullPath(ScenarioReportDir), runId);
        string artifactDir = Path.Combine(runDir, "artifacts");
        Directory.CreateDirectory(artifactDir);

        return new ScenarioRunContext
        {
            runId = runId,
            runDir = runDir,
            artifactDir = artifactDir,
            reportPath = Path.Combine(runDir, "report.json"),
            logPath = Path.Combine(runDir, "logs.txt")
        };
    }

    static List<string> CopyArtifactsToRunDir(IEnumerable<string> sourcePaths, ScenarioRunContext context, int stepIndex, string stepName)
    {
        var copied = new List<string>();
        if (sourcePaths == null) return copied;
        if (context == null) return sourcePaths.Where(p => !string.IsNullOrEmpty(p)).ToList();

        Directory.CreateDirectory(context.artifactDir);
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrEmpty(sourcePath)) continue;

            string fullSource;
            try { fullSource = Path.GetFullPath(sourcePath); }
            catch { continue; }

            string copyKey = BuildArtifactCopyKey(fullSource);
            if (context.copiedArtifacts.TryGetValue(copyKey, out string existingCopy))
            {
                AddUniqueArtifacts(copied, new[] { existingCopy });
                continue;
            }

            if (!File.Exists(fullSource))
            {
                AddUniqueArtifacts(copied, new[] { fullSource });
                continue;
            }

            if (IsPathUnder(fullSource, context.runDir))
            {
                context.copiedArtifacts[copyKey] = fullSource;
                AddUniqueArtifacts(copied, new[] { fullSource });
                continue;
            }

            string destPath = BuildUniqueArtifactCopyPath(context.artifactDir, fullSource, stepIndex, stepName);
            try
            {
                File.Copy(fullSource, destPath, false);
                context.copiedArtifacts[copyKey] = destPath;
                AddUniqueArtifacts(copied, new[] { destPath });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Bridge] Failed to copy scenario artifact: " + e.Message);
                AddUniqueArtifacts(copied, new[] { fullSource });
            }
        }

        return copied;
    }

    static string BuildArtifactCopyKey(string sourcePath)
    {
        try
        {
            var file = new FileInfo(sourcePath);
            if (file.Exists)
                return file.FullName + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length;
        }
        catch { }
        return sourcePath;
    }

    static string BuildUniqueArtifactCopyPath(string artifactDir, string sourcePath, int stepIndex, string stepName)
    {
        string fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "artifact";

        string stepPrefix = (stepIndex + 1).ToString("000") + "_" + SafeArtifactNameSegment(stepName) + "_";
        string name = stepPrefix + Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string destPath = Path.Combine(artifactDir, name + ext);
        int suffix = 2;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(artifactDir, name + "_" + suffix + ext);
            suffix++;
        }
        return destPath;
    }

    static string SafeArtifactNameSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "step";

        var sb = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_' || c == '-')
            {
                sb.Append(c);
            }
            else if (sb.Length == 0 || sb[sb.Length - 1] != '_')
            {
                sb.Append('_');
            }

            if (sb.Length >= 48) break;
        }

        string safe = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(safe) ? "step" : safe;
    }

    static string WriteScenarioLogs(ScenarioRunContext runContext, int count)
    {
        try
        {
            if (runContext == null || string.IsNullOrEmpty(runContext.logPath))
                return null;

            Directory.CreateDirectory(runContext.runDir);
            var sb = new StringBuilder();
            sb.AppendLine("=== Unity Bridge Logs ===");
            sb.AppendLine("runId: " + runContext.runId);
            sb.AppendLine("capturedAt: " + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
            sb.AppendLine("count: " + count);
            sb.AppendLine();

            lock (logLock)
            {
                var recent = logBuffer.AsEnumerable().Reverse().Take(count).ToList();
                if (recent.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    for (int i = 0; i < recent.Count; i++)
                        sb.AppendLine(FormatLogEntry(recent[i], i, false));
                }
            }

            File.WriteAllText(runContext.logPath, sb.ToString(), Encoding.UTF8);
            return runContext.logPath;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Bridge] Failed to write scenario logs: " + e.Message);
            return null;
        }
    }

    static string WriteScenarioReport(ScenarioRunContext runContext, string report)
    {
        try
        {
            if (runContext == null || string.IsNullOrEmpty(runContext.reportPath))
                return null;

            Directory.CreateDirectory(runContext.runDir);
            File.WriteAllText(runContext.reportPath, report, Encoding.UTF8);
            return runContext.reportPath;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Bridge] Failed to write scenario report: " + e.Message);
            return null;
        }
    }

    static string NormalizeScenarioAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "";
        return action.Replace("-", "").Replace("_", "").Trim().ToLowerInvariant();
    }

    static int StepTimeoutMs(ScenarioStep step, int defaultTimeoutMs)
    {
        return step != null && step.timeoutMs > 0 ? step.timeoutMs : defaultTimeoutMs;
    }

    static int StepPollMs(ScenarioStep step, int defaultPollMs, int timeoutMs)
    {
        int pollMs = step != null && step.pollMs > 0 ? step.pollMs : defaultPollMs;
        return pollMs > timeoutMs ? timeoutMs : pollMs;
    }

    static List<string> BuildSelectorParams(ScenarioSelector selector)
    {
        var p = new List<string>();
        if (selector == null) return p;
        AddParam(p, "name", selector.name);
        AddParam(p, "path", selector.path);
        AddParam(p, "parent", selector.parent);
        AddParam(p, "component", selector.component);
        if (selector.index > 0) AddParam(p, "index", selector.index.ToString());
        if (selector.inactive) AddParam(p, "inactive", "true");
        return p;
    }

    static bool HasScenarioSelector(ScenarioSelector selector)
    {
        return selector != null &&
               (!string.IsNullOrEmpty(selector.name) ||
                !string.IsNullOrEmpty(selector.path) ||
                !string.IsNullOrEmpty(selector.parent) ||
                !string.IsNullOrEmpty(selector.component));
    }

    static string ToQueryString(List<string> parameters)
    {
        return parameters.Count == 0 ? "" : "?" + string.Join("&", parameters.ToArray());
    }

    static void AddParam(List<string> parameters, string name, string value)
    {
        if (value == null) return;
        parameters.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
    }

    static string ScenarioBaselineName(ScenarioStep step)
    {
        if (step == null) return null;
        return !string.IsNullOrWhiteSpace(step.baseline) ? step.baseline : step.name;
    }

    static bool ContainsJsonProperty(string json, string property)
    {
        return !string.IsNullOrEmpty(json) &&
               json.IndexOf("\"" + property + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string SafeScenarioFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "scenario";
        var sb = new StringBuilder();
        foreach (char c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        string safe = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(safe) ? "scenario" : safe;
    }

    // --- Batch helpers ---

    static string HandleBatch(HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = reader.ReadToEnd();

        string[] paths = ParseBatchPaths(body);
        if (paths.Length == 0)
            return "{\"error\": \"invalid body. expected: {\\\"paths\\\": [\\\"/route?p=v\\\", ...]}\"}";

        var sb = new StringBuilder();
        sb.AppendLine("{\"results\": [");

        for (int i = 0; i < paths.Length; i++)
        {
            string subPath = paths[i];
            string url = "http://localhost:" + _activePort + subPath;

            string result;
            try
            {
                var httpReq = (HttpWebRequest)WebRequest.Create(url);
                httpReq.Timeout = 30000;
                using (var resp = (HttpWebResponse)httpReq.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    result = reader.ReadToEnd();
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    using (var reader = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                        result = reader.ReadToEnd();
                }
                else
                    result = "error: " + we.Message;
            }
            catch (Exception e)
            {
                result = "error: " + e.Message;
            }

            sb.Append("  {\"path\": \"");
            sb.Append(EscapeJsonString(subPath));
            sb.Append("\", \"result\": \"");
            sb.Append(EscapeJsonString(result));
            sb.Append("\"}");
            if (i < paths.Length - 1) sb.Append(",");
            sb.AppendLine();
        }

        sb.Append("]}");
        return sb.ToString();
    }

    static string[] ParseBatchPaths(string json)
    {
        json = json.Trim();
        int arrStart = json.IndexOf('[');
        int arrEnd = json.LastIndexOf(']');
        if (arrStart < 0 || arrEnd <= arrStart) return new string[0];

        string content = json.Substring(arrStart + 1, arrEnd - arrStart - 1).Trim();
        if (string.IsNullOrEmpty(content)) return new string[0];

        var results = new List<string>();
        bool inQuote = false;
        var current = new StringBuilder();

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '"' && (i == 0 || content[i - 1] != '\\'))
            {
                inQuote = !inQuote;
                continue;
            }
            if (c == ',' && !inQuote)
            {
                string val = current.ToString().Trim();
                if (val.Length > 0) results.Add(val);
                current.Clear();
                continue;
            }
            if (inQuote) current.Append(c);
        }
        string last = current.ToString().Trim();
        if (last.Length > 0) results.Add(last);

        return results.ToArray();
    }

    static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    // --- Server core ---

    static void StartWithRetry()
    {
        if (listener != null || _stopping) return;
        try
        {
            Start();
            _retryCount = 0;
            EditorApplication.update -= RetryTick;
        }
        catch (Exception ex)
        {
            _retryCount++;
            double delay = Math.Min(0.5 * Math.Pow(2, _retryCount - 1), 5.0);
            _nextRetryTime = EditorApplication.timeSinceStartup + delay;

            if (_retryCount <= 5)
                Debug.LogWarning($"[Bridge] Start failed, retry #{_retryCount} in {delay:F1}s — {ex.Message}");
            else if (_retryCount % 10 == 0)
                Debug.LogWarning($"[Bridge] Still retrying (attempt #{_retryCount})...");

            EditorApplication.update -= RetryTick;
            EditorApplication.update += RetryTick;
        }
    }

    static void RetryTick()
    {
        if (listener != null || _stopping || EditorApplication.timeSinceStartup < _nextRetryTime)
            return;
        EditorApplication.update -= RetryTick;
        StartWithRetry();
    }

    static void Start()
    {
        if (listener != null) return;
        _stopping = false;
        _listenerDied = false;

        HttpListener newListener = null;
        int port = BasePort;
        for (int i = 0; i < MaxPortRetry; i++)
        {
            port = BasePort + i;
            newListener = new HttpListener();
            try
            {
                newListener.Prefixes.Add($"http://localhost:{port}/");
                newListener.Start();
                break; // success
            }
            catch
            {
                try { newListener.Close(); } catch { }
                newListener = null;
                if (i < MaxPortRetry - 1 && i < 3)
                    Debug.LogWarning($"[Bridge] Port {port} in use, trying {port + 1}...");
            }
        }

        if (newListener == null)
            throw new Exception($"[Bridge] Could not bind any port in range {BasePort}-{BasePort + MaxPortRetry - 1}");

        _activePort = port;
        listener = newListener;
        listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "UnityBridge" };
        listenerThread.Start();

        File.WriteAllText("Temp/bridge_port.txt", _activePort.ToString());
        Debug.Log($"[Bridge] Server {BridgeVersion} started on http://localhost:{_activePort}");
    }

    static void Stop()
    {
        _stopping = true;
        EditorApplication.update -= RetryTick;
        _retryCount = 0;

        if (listener != null)
        {
            var oldListener = listener;
            var oldThread = listenerThread;
            listener = null;
            listenerThread = null;

            // Abort() forces pending async operations to throw
            try { oldListener.Abort(); } catch { }

            // _stopping is already true, so ListenLoop will exit within 500ms
            try { oldThread?.Join(2000); } catch { }

            // Last resort: Thread.Abort to forcibly kill the thread and release socket
            if (oldThread != null && oldThread.IsAlive)
            {
                try { oldThread.Abort(); } catch { }
                try { oldThread.Join(1000); } catch { }
            }
        }

        // Cancel pending commands so waiters don't hang
        lock (pendingActions)
        {
            foreach (var (_, done, result) in pendingActions)
            {
                result[0] = "error: bridge shutting down";
                done.Set();
            }
            pendingActions.Clear();
        }
    }

    static void PumpMainThread()
    {
        // Auto-restart on unexpected listener death
        if (_listenerDied)
        {
            _listenerDied = false;
            if (!_stopping)
            {
                Debug.LogWarning("[Bridge] Listener died unexpectedly — restarting...");
                StartWithRetry();
            }
        }

        lock (pendingActions)
        {
            for (int i = pendingActions.Count - 1; i >= 0; i--)
            {
                var (action, done, result) = pendingActions[i];
                try { result[0] = action(); }
                catch (Exception e) { result[0] = "error: " + e.Message; }
                finally
                {
                    done.Set();
                    pendingActions.RemoveAt(i);
                }
            }
        }
    }

    static void ListenLoop()
    {
        while (!_stopping && listener != null && listener.IsListening)
        {
            try
            {
                // Use async + WaitOne so the thread can check _stopping periodically
                // instead of blocking forever in GetContext()
                var ar = listener.BeginGetContext(null, null);
                while (!_stopping && !ar.AsyncWaitHandle.WaitOne(500)) { }
                if (_stopping) break;
                var context = listener.EndGetContext(ar);
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch
            {
                if (!_stopping && listener != null)
                    _listenerDied = true;
                break;
            }
        }
    }

    static void HandleRequest(HttpListenerContext context)
    {
        byte[] buffer;
        string contentType = "text/plain; charset=utf-8";
        try
        {
            var req = context.Request;
            string path = req.Url.AbsolutePath.ToLower();

            if (path == "/batch")
            {
                string batchResult = HandleBatch(req);
                buffer = Encoding.UTF8.GetBytes(batchResult);
                contentType = "application/json; charset=utf-8";
            }
            else if (path == "/scenario/run")
            {
                string scenarioResult = HandleScenarioRun(req);
                buffer = Encoding.UTF8.GetBytes(scenarioResult);
                contentType = "application/json; charset=utf-8";
            }
            else if (binaryRoutes.TryGetValue(path, out var binaryHandler))
            {
                // Binary route (e.g. /screenshot/get)
                var (data, ct) = binaryHandler(req);
                buffer = data;
                contentType = ct;
            }
            else if (routes.TryGetValue(path, out var handler))
            {
                string result = handler(req);
                // Return JSON content-type for JSON-producing helper routes.
                if ((path == "/rect-transforms" || path == "/artifacts/list") && result.TrimStart().StartsWith("{"))
                    contentType = "application/json; charset=utf-8";
                buffer = Encoding.UTF8.GetBytes(result);
            }
            else
            {
                var allKeys = routes.Keys.Concat(binaryRoutes.Keys).OrderBy(k => k);
                string result = "endpoints:\n"
                    + string.Join("\n", allKeys.Select(k => "  GET  " + k))
                    + "\n  POST /batch"
                    + "\n  POST /scenario/run";
                buffer = Encoding.UTF8.GetBytes(result);
            }
        }
        catch (Exception e)
        {
            buffer = Encoding.UTF8.GetBytes("error: " + e.Message);
        }

        try
        {
            context.Response.ContentLength64 = buffer.Length;
            context.Response.ContentType = contentType;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }
        catch { }
    }
}
