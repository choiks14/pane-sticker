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

    private static readonly Dictionary<string, string> EmptyFolderMap = new();

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
        var folderByTitle = resolveFolders
            ? _shells.GetMap()
            : (IReadOnlyDictionary<string, string>)EmptyFolderMap;

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
        var candidates = new List<(Rect Rect, string Title, string Folder, bool Focused)>();
        foreach (var el in raw)
        {
            Rect b;
            string title;
            string folder = "";
            bool focused;
            try
            {
                var rectObj = el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (rectObj is not Rect rb) continue;
                b = rb;
                // WT 는 패인 제목을 HelpText 로 노출한다. Name 은 컨트롤 종류("Windows PowerShell") 라서 덜 유용하다.
                title = el.GetCachedPropertyValue(AutomationElement.HelpTextProperty) as string ?? "";
                focused = el.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty) is true;
                if (resolveFolders) folder = ResolveFolder(title, folderByTitle);
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

            candidates.Add((clipped, title, folder, focused));
        }

        // 동일 사각형 중복 제거
        var deduped = candidates
            .GroupBy(c => ((int)Math.Round(c.Rect.X), (int)Math.Round(c.Rect.Y),
                           (int)Math.Round(c.Rect.Width), (int)Math.Round(c.Rect.Height)))
            .Select(g => g.OrderByDescending(x => x.Focused).First())
            .ToList();

        // 읽기 순서 정렬(위 -> 아래, 같은 줄이면 왼쪽 -> 오른쪽). Top 은 8px 버킷으로 반올림.
        deduped.Sort((a, b) =>
        {
            int rowA = (int)Math.Round(a.Rect.Y / 8.0);
            int rowB = (int)Math.Round(b.Rect.Y / 8.0);
            if (rowA != rowB) return rowA.CompareTo(rowB);
            return a.Rect.X.CompareTo(b.Rect.X);
        });

        for (int i = 0; i < deduped.Count; i++)
        {
            var c = deduped[i];
            int index = i + 1;

            // 이번 스캔에서 못 찾았으면 직전 값을 그대로 쓴다. 표시가 잠깐 비었다 돌아오지 않게.
            string key = hwnd.ToString() + ":" + index;
            string folder = c.Folder;
            if (folder.Length == 0) _stickyFolder.TryGetValue(key, out folder!);
            else _stickyFolder[key] = folder;

            result.Add(new PaneInfo
            {
                Index = index,
                Bounds = c.Rect,
                Title = Shorten(c.Title),
                Folder = folder ?? "",
                Focused = c.Focused
            });
        }

        return result;
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
    /// </summary>
    private string ResolveFolder(string title, IReadOnlyDictionary<string, string> folderByTitle)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        if (folderByTitle.TryGetValue(title, out var found) && !string.IsNullOrWhiteSpace(found))
            return found;

        // 제목이 막 바뀌었을 수 있다. 캐시를 무시하고 한 번 더 찾아본다.
        var fresh = _shells.GetMap(force: true);
        if (fresh.TryGetValue(title, out var refreshed) && !string.IsNullOrWhiteSpace(refreshed))
            return refreshed;

        return "";
    }

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
