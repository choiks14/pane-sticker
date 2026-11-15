using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using PaneSticker.Interop;
using PaneSticker.Model;

namespace PaneSticker.Services;

/// <summary>
/// Windows Terminal 창을 찾아 UI Automation 트리에서 패인(TermControl) 사각형을 추출한다.
/// 전용 백그라운드 스레드에서 폴링하며, WinEvent 훅이 Nudge()로 즉시 갱신을 유도한다.
/// </summary>
public sealed class PaneTracker : IDisposable
{
    // Windows Terminal 최상위 창 클래스. (WT 1.x ~ 현재)
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private static readonly string[] TerminalProcessNames = { "windowsterminal", "wt" };

    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;


    private readonly ShellProcessScanner _shells = new();

    /// <summary>
    /// 창+패인 번호별로 마지막에 성공한 폴더. 스캔이 순간적으로 실패해도(제목 교체 타이밍 등)
    /// 배지가 비었다 다시 차오르며 깜빡이지 않게 직전 값을 유지한다.
    /// </summary>
    private readonly Dictionary<string, string> _stickyFolder = new(StringComparer.Ordinal);

    private IntPtr _lastTerminal = IntPtr.Zero;
    private volatile int _pollIntervalMs = 350;
    private string _lastSignature = "";

    public event Action<TrackerSnapshot>? SnapshotUpdated;

    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set => _pollIntervalMs = Math.Clamp(value, 80, 3000);
    }

    /// <summary>
    /// 각 패인의 작업 폴더를 알아낼지 여부. 켜면 UIA TextPattern 으로 화면 텍스트를 읽어야 해서
    /// 스캔 비용이 늘어나므로, 배지가 폴더 이름을 쓸 때만 켠다.
    /// </summary>
    public volatile bool ResolveFolders = true;

    public PaneTracker()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "PaneSticker.PaneTracker",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();

    /// <summary>즉시 한 번 다시 스캔하도록 깨운다.</summary>
    public void Nudge() => _wake.Set();

    /// <summary>다음 스캔에서 변화가 없어도 강제로 이벤트를 올린다.</summary>
    public void Invalidate()
    {
        _lastSignature = "";
        _wake.Set();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var snap = Capture();
                var sig = snap.Signature;
                if (sig != _lastSignature)
                {
                    _lastSignature = sig;
                    SnapshotUpdated?.Invoke(snap);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PaneSticker] capture failed: " + ex.Message);
            }

            MaybeTrimMemory();
            _wake.WaitOne(_pollIntervalMs);
        }
    }

    private long _lastTrim = Environment.TickCount64;

    /// <summary>
    /// UIA 요소는 COM RCW 라서 파이널라이저가 돌아야 실제로 해제된다.
    /// 스캔을 쉬지 않고 도는 앱이라 그냥 두면 상주 메모리가 수백 MB 로 불어난다.
    /// 2분마다 한 번 수거하고 워킹셋을 OS 에 돌려준다.
    /// 더 짧게 잡으면 되돌려준 페이지를 곧바로 다시 읽어들이느라 디스크가 분주해진다.
    /// </summary>
    private void MaybeTrimMemory()
    {
        long now = Environment.TickCount64;
        if (now - _lastTrim < 120_000) return;
        _lastTrim = now;

        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            NativeMethods.SetProcessWorkingSetSize(
                NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch { /* 정리 실패는 무시 */ }
    }

    // ------------------------------------------------------------------ scan

    private TrackerSnapshot Capture()
    {
        IntPtr target = ResolveTargetWindow(out bool isForeground);
        if (target == IntPtr.Zero) return TrackerSnapshot.Empty;

        bool minimized = NativeMethods.IsIconic(target);
        if (!NativeMethods.GetWindowRect(target, out RECT r) || r.IsEmpty || minimized)
        {
            return new TrackerSnapshot
            {
                TargetWindow = target,
                IsForeground = isForeground,
                IsMinimized = true
            };
        }

        var windowBounds = new Rect(r.Left, r.Top, r.Width, r.Height);
        var panes = FindPanes(target, windowBounds, ResolveFolders, out string? diag);

        return new TrackerSnapshot
        {
            TargetWindow = target,
            WindowBounds = windowBounds,
            IsForeground = isForeground,
            IsMinimized = false,
            Panes = panes,
            Diagnostic = diag
        };
    }

    private IntPtr ResolveTargetWindow(out bool isForeground)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (IsTerminalWindow(fg))
        {
            _lastTerminal = fg;
            isForeground = true;
            return fg;
        }

        isForeground = false;
        if (_lastTerminal != IntPtr.Zero &&
            NativeMethods.IsWindow(_lastTerminal) &&
            NativeMethods.IsWindowVisible(_lastTerminal))
        {
            return _lastTerminal;
        }

        // 앱 시작 후 터미널을 한 번도 활성화하지 않은 경우를 위한 폴백:
        // 화면에 보이는 WT 창을 Z-순서대로 훑어 가장 위의 것을 대상으로 삼는다.
        IntPtr any = FindVisibleTerminalWindow();
        _lastTerminal = any;
        return any;
    }

    private static IntPtr FindVisibleTerminalWindow()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            if (!string.Equals(NativeMethods.GetWindowClassName(hwnd), TerminalWindowClass, StringComparison.Ordinal))
                return true;
            if (!NativeMethods.GetWindowRect(hwnd, out RECT r) || r.IsEmpty) return true;
            found = hwnd;
            return false;   // 가장 위의 창에서 중단
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsTerminalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        if (string.Equals(NativeMethods.GetWindowClassName(hwnd), TerminalWindowClass, StringComparison.Ordinal))
            return true;

        // 클래스명이 바뀌는 경우를 대비한 프로세스명 폴백
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return TerminalProcessNames.Contains(p.ProcessName.ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// UIA 트리에서 패인 후보를 찾는다.
    /// 1순위: ClassName == "TermControl"
    /// 2순위: Text 컨트롤 + TextPattern 지원 (WT 내부 구현이 바뀌었을 때의 폴백)
    /// </summary>
    private List<PaneInfo> FindPanes(IntPtr hwnd, Rect windowBounds, bool resolveFolders, out string? diagnostic)
    {
        // 셸 프로세스에서 얻은 "콘솔 제목 -> 작업 폴더" 표. 화면 읽기보다 정확하다.
        var shellMap = resolveFolders ? _shells.GetMap() : ShellMap.Empty;

        diagnostic = null;
        var result = new List<PaneInfo>();

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            diagnostic = "FromHandle 실패: " + ex.Message;
            return result;
        }
        if (root == null)
        {
            diagnostic = "루트 UIA 요소 없음";
            return result;
        }

        // 폴더는 프로세스 트리에서 얻으므로 화면 텍스트를 읽을 필요가 없다. 가벼운 캐시 모드로 둔다.
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.None
        };
        // Name 은 캐시하지 않는다. WT 의 TermControl 은 Name 에 화면 버퍼 전체를 담기 때문에
        // 매 스캔마다 패인 수만큼 거대한 문자열을 만들어 메모리를 크게 잡아먹는다.
        // 패인 제목은 HelpText 로 충분하고, ClassName/IsOffscreen 은 조건식에서만 쓰여 캐시가 필요 없다.
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.HelpTextProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        // 패인 고유 식별자. 읽기 순서 번호는 분할/닫기 때마다 바뀌어서 직전 폴더를 엉뚱한 패인에 붙인다.
        cache.Add(AutomationElement.RuntimeIdProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        // 제목이 겹치는 패인을 가릴 때만 커서 줄을 읽는다. 캐시 전용 요소라도 패턴 호출은 된다.
        cache.Add(TextPattern.Pattern);

        var raw = new List<AutomationElement>();
        try
        {
            using (cache.Activate())
            {
                var byClass = new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl");
                raw.AddRange(Enumerate(root.FindAll(TreeScope.Descendants, byClass)));

                if (raw.Count == 0)
                {
                    var byText = new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
                    raw.AddRange(Enumerate(root.FindAll(TreeScope.Descendants, byText)));
                    if (raw.Count > 0) diagnostic = "폴백 탐색(Text+TextPattern) 사용 중";
                }
            }
        }
        catch (Exception ex)
        {
            diagnostic = "UIA 탐색 실패: " + ex.Message;
            return result;
        }

        if (raw.Count == 0)
        {
            diagnostic ??= "패인 요소를 찾지 못함 (WT UIA 트리 변경 가능성)";
            return result;
        }

        // 유효한 후보만 남긴다: 창 안쪽에 있고 최소 크기 이상
        var candidates = new List<(Rect Rect, string Title, string Folder, bool Focused, bool Offscreen, string Id,
            List<ShellCandidate>? Ambig)>();
        foreach (var el in raw)
        {
            Rect b;
            string title;
            string folder = "";
            bool focused;
            bool offscreen;
            string id;
            List<ShellCandidate>? ambig = null;
            try
            {
                var rectObj = el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (rectObj is not Rect rb) continue;
                b = rb;
                // WT 는 패인 제목을 HelpText 로 노출한다. Name 은 컨트롤 종류("Windows PowerShell") 라서 덜 유용하다.
                title = el.GetCachedPropertyValue(AutomationElement.HelpTextProperty) as string ?? "";
                focused = el.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty) is true;
                offscreen = el.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty) is true;
                id = el.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) is int[] rid && rid.Length > 0
                    ? string.Join('.', rid)
                    : "";
                if (resolveFolders) folder = ResolveFolder(title, shellMap, el, out ambig);
            }
            catch
            {
                continue;
            }

            if (double.IsInfinity(b.Width) || double.IsInfinity(b.Height)) continue;
            if (b.Width < 24 || b.Height < 24) continue;

            var clipped = Rect.Intersect(b, windowBounds);
            if (clipped.IsEmpty) continue;
            // 창 밖에 있는(비활성 탭 등) 요소 제외
            if (clipped.Width * clipped.Height < b.Width * b.Height * 0.5) continue;

            candidates.Add((clipped, title, folder, focused, offscreen, id, ambig));
        }

        // 동일 사각형 중복 제거. 화면에 실제로 보이는 요소를 먼저 고른다.
        // 겹친 요소 중 숨은 것(비활성 탭 등)을 집으면 그 패인의 폴더가 보이는 패인에 붙는다.
        var deduped = candidates
            .GroupBy(c => ((int)Math.Round(c.Rect.X), (int)Math.Round(c.Rect.Y),
                           (int)Math.Round(c.Rect.Width), (int)Math.Round(c.Rect.Height)))
            .Select(g => g.OrderBy(x => x.Offscreen).ThenByDescending(x => x.Focused).First())
            .ToList();

        // 읽기 순서 정렬(위 -> 아래, 같은 줄이면 왼쪽 -> 오른쪽). Top 은 8px 버킷으로 반올림.
        deduped.Sort((a, b) =>
        {
            int rowA = (int)Math.Round(a.Rect.Y / 8.0);
            int rowB = (int)Math.Round(b.Rect.Y / 8.0);
            if (rowA != rowB) return rowA.CompareTo(rowB);
            return a.Rect.X.CompareTo(b.Rect.X);
        });

        string keyPrefix = hwnd.ToString() + ":";
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < deduped.Count; i++)
        {
            var c = deduped[i];
            int index = i + 1;

            // 이번 스캔에서 못 찾았으면 이 패인의 직전 값을 쓴다. 표시가 잠깐 비었다 돌아오지 않게.
            // 키는 패인 고유 RuntimeId. 번호로 잡으면 분할/닫기 뒤 다른 패인의 폴더가 넘어온다.
            string key = keyPrefix + (c.Id.Length > 0 ? c.Id : "#" + index);
            seenKeys.Add(key);
            string folder = c.Folder;
            if (folder.Length > 0)
            {
                _stickyFolder[key] = folder;
            }
            else if (_stickyFolder.TryGetValue(key, out var sticky))
            {
                // 제목이 겹쳐 못 가린 패인은, 기억한 폴더가 지금도 후보 중에 있을 때만 쓴다.
                // 그렇지 않으면 cd 이전의 낡은 폴더가 계속 붙어 있게 된다.
                if (c.Ambig == null ||
                    c.Ambig.Any(a => string.Equals(a.Folder, sticky, StringComparison.OrdinalIgnoreCase)))
                    folder = sticky;
            }

            result.Add(new PaneInfo
            {
                Index = index,
                Bounds = c.Rect,
                Title = Shorten(c.Title),
                Folder = folder ?? "",
                Focused = c.Focused
            });
        }

        // 이 창에서 이번에 안 보인 패인은 대개 다른 탭에 숨어 있을 뿐이다. 지우면 탭을 돌아올 때
        // 배지가 잠깐 비므로 남겨 둔다. 닫힌 WT 창의 기억만 버리고, 너무 쌓이면 이 창의 안 보인 패인도 버린다.
        bool overCap = _stickyFolder.Count > StickyCap;
        foreach (var stale in _stickyFolder.Keys
                     .Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)
                                 ? overCap && !seenKeys.Contains(k)
                                 : !IsLiveWindowKey(k))
                     .ToList())
            _stickyFolder.Remove(stale);

        return result;
    }

    /// <summary>"hwnd:패인키" 형식 키의 창이 아직 살아 있는지.</summary>
    private static bool IsLiveWindowKey(string key)
    {
        int colon = key.IndexOf(':');
        return colon > 0
               && long.TryParse(key.AsSpan(0, colon), out long h)
               && NativeMethods.IsWindow(new IntPtr(h));
    }

    private static IEnumerable<AutomationElement> Enumerate(AutomationElementCollection collection)
    {
        for (int i = 0; i < collection.Count; i++)
        {
            AutomationElement? el = null;
            try { el = collection[i]; } catch { }
            if (el != null) yield return el;
        }
    }

    /// <summary>
    /// 패인의 작업 폴더를 돌려준다.
    ///
    /// 셸 프로세스 트리에서 얻은 값만 쓴다. 화면 텍스트에서 경로를 긁는 폴백은 두지 않는다.
    /// 화면에 보이는 경로는 명령 출력에 따라 수시로 달라져서, 작업 중에 표시가 계속 흔들리기 때문이다.
    /// 제목이 방금 바뀌어 표에서 못 찾은 경우에만 한 번 강제로 다시 스캔한다.
    /// ambig = 제목이 겹친 후보들(겹치지 않았으면 null). 기억한 폴더를 써도 되는지 판단할 때 쓴다.
    /// </summary>
    private string ResolveFolder(string title, ShellMap map, AutomationElement el, out List<ShellCandidate>? ambig)
    {
        ambig = null;
        title = ShellProcessScanner.NormalizeTitle(title);
        if (title.Length == 0) return "";

        string found = Lookup(title, map, el, out ambig);
        if (found.Length > 0) return found;

        // 제목이 막 바뀌었거나, 겹치는 제목의 커서 줄이 스캔 뒤에 바뀌었을 수 있다. 한 번 더 찾아본다.
        // 강제 스캔이 실제로 돌았는데도 못 찾은 제목만 잠시 쉰다. 400ms 마다 모든 셸에
        // 콘솔 attach 를 되풀이하지 않으려는 것이다. 간격 제한에 걸려 스캔이 안 돈 경우는 기록하지 않는다.
        long now = Environment.TickCount64;
        if (_forcedMiss.TryGetValue(title, out long missAt) && now - missAt < ForcedMissBackoffMs) return "";

        var fresh = _shells.GetMap(force: true);
        found = Lookup(title, fresh, el, out var freshAmbig);
        if (fresh.Stamp != map.Stamp) ambig = freshAmbig;
        if (found.Length > 0)
        {
            _forcedMiss.Remove(title);
            return found;
        }

        if (fresh.Stamp != map.Stamp)
        {
            _forcedMiss[title] = now;
            if (_forcedMiss.Count > 64)
                foreach (var old in _forcedMiss.Where(kv => now - kv.Value >= ForcedMissBackoffMs)
                             .Select(kv => kv.Key).ToList())
                    _forcedMiss.Remove(old);
        }
        return "";
    }

    /// <summary>
    /// 표에서 제목을 찾는다. 제목이 겹치는 경우면 패인 커서 줄을 셸 커서 줄과 맞춰 본다.
    /// </summary>
    private static string Lookup(string title, ShellMap map, AutomationElement el, out List<ShellCandidate>? ambig)
    {
        ambig = null;
        if (map.ByTitle.TryGetValue(title, out var folder) && folder.Length > 0) return folder;
        if (!map.Ambiguous.TryGetValue(title, out var list)) return "";

        ambig = list;
        string line = ShellProcessScanner.NormalizeLine(ReadCaretLine(el));
        if (line.Length == 0) return "";

        // 1) 커서 줄이 똑같은 후보. 2) 없으면 한쪽이 다른 쪽의 앞부분인 후보
        //    (스캔 뒤에 프롬프트 뒤로 명령을 치는 중이면 줄이 길어져 있다).
        // 맞는 후보들이 모두 같은 폴더일 때만 정한다(예: 둘 다 "❯" 만 보이면 못 가린다).
        string exact = PickFolder(list, c => string.Equals(c.CursorLine, line, StringComparison.Ordinal), out bool any);
        if (any) return exact;
        return PickFolder(list, c => c.CursorLine.Length >= 3 &&
                                     (line.StartsWith(c.CursorLine, StringComparison.Ordinal) ||
                                      c.CursorLine.StartsWith(line, StringComparison.Ordinal)), out _);
    }

    /// <summary>조건에 맞는 후보들의 폴더가 하나로 모이면 그 폴더. any = 맞는 후보가 있었음.</summary>
    private static string PickFolder(List<ShellCandidate> list, Func<ShellCandidate, bool> match, out bool any)
    {
        any = false;
        string hit = "";
        foreach (var c in list)
        {
            if (!match(c)) continue;
            if (c.Folder.Length == 0) { any = true; return ""; }
            if (!any) { any = true; hit = c.Folder; }
            else if (!string.Equals(hit, c.Folder, StringComparison.OrdinalIgnoreCase)) return "";
        }
        return hit;
    }

    /// <summary>패인의 커서가 있는 줄. 선택 영역이 있으면 그 시작 줄이 나와 매칭이 안 될 뿐 해는 없다.</summary>
    private static string ReadCaretLine(AutomationElement el)
    {
        try
        {
            if (el.GetCachedPattern(TextPattern.Pattern) is not TextPattern tp) return "";
            var sel = tp.GetSelection();
            if (sel.Length == 0) return "";
            var range = sel[0].Clone();
            range.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line);
            return range.GetText(2000);
        }
        catch
        {
            return "";
        }
    }

    private const int ForcedMissBackoffMs = 1000;
    private const int StickyCap = 256;

    /// <summary>강제 스캔으로도 못 찾은 제목 -> 그 시각. 트래커 스레드 전용.</summary>
    private readonly Dictionary<string, long> _forcedMiss = new(StringComparer.Ordinal);

    private static string Shorten(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        // UIA Name 이 화면 버퍼 전체를 담는 경우가 있어 앞부분만 사용
        int cut = s.IndexOf("  ", StringComparison.Ordinal);
        if (cut > 0) s = s.Substring(0, cut);
        return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    }

    // -------------------------------------------------------------- 진단 덤프

    /// <summary>현재 WT 창의 UIA 트리를 텍스트로 덤프한다(문제 진단용).</summary>
    public static string DumpUiaTree(IntPtr hwnd, int maxDepth = 6)
    {
        var sb = new StringBuilder();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return "루트 요소 없음";
            Walk(root, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("덤프 실패: " + ex);
        }
        return sb.ToString();

        void Walk(AutomationElement el, int depth)
        {
            if (depth > maxDepth) return;
            string pad = new string(' ', depth * 2);
            try
            {
                var c = el.Current;
                sb.AppendLine(pad + "- [" + c.ControlType.ProgrammaticName.Replace("ControlType.", "") + "] " +
                              "Class='" + c.ClassName + "' Name='" + Shorten(c.Name) + "' Rect=" + c.BoundingRectangle);
            }
            catch
            {
                sb.AppendLine(pad + "- <읽기 실패>");
            }

            AutomationElement? child = null;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
            while (child != null)
            {
                Walk(child, depth + 1);
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
                catch { break; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        try { if (_thread.IsAlive) _thread.Join(500); } catch { }
        _wake.Dispose();
        _cts.Dispose();
    }
}
