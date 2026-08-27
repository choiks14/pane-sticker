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

            _wake.WaitOne(_pollIntervalMs);
        }
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

        // 폴더 이름을 알아내려면 TextPattern 으로 화면 텍스트를 읽어야 하므로 Full 모드가 필요하다.
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = resolveFolders ? AutomationElementMode.Full : AutomationElementMode.None
        };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.HelpTextProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        if (resolveFolders) cache.Add(TextPattern.Pattern);

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
                if (string.IsNullOrWhiteSpace(title))
                    title = el.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
                focused = el.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty) is true;
                if (resolveFolders) folder = ResolveFolder(el, title, folderByTitle);
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
            result.Add(new PaneInfo
            {
                Index = i + 1,
                Bounds = c.Rect,
                Title = Shorten(c.Title),
                Folder = c.Folder,
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

    // 프롬프트에서 현재 경로를 뽑는 패턴들.
    //  - PowerShell:  "PS D:\workspace\sticker>"
    //  - cmd:         "D:\workspace\sticker>"
    private static readonly Regex WindowsPrompt = new(
        @"(?m)^\s*(?:PS\s+)?([A-Za-z]:\\[^>\r\n]*?)\s*>", RegexOptions.Compiled);

    //  - Git Bash / MSYS / WSL:  "/d/workspace/sticker$"  "~/proj#"
    private static readonly Regex PosixPrompt = new(
        @"(?m)(~?/[^\s""'<>|:*?$#\r\n]+)\s*[$#]", RegexOptions.Compiled);

    // 텍스트 안에 끼어 있는 절대 경로 (Windows "D:\..." 와 MSYS/WSL "/d/...")
    private static readonly Regex AnyAbsolutePath = new(
        @"[A-Za-z]:\\[^\s""'<>|*?\r\n]+|/[A-Za-z]/[^\s""'<>|*?:\r\n]+", RegexOptions.Compiled);

    /// <summary>
    /// 패인의 작업 폴더 이름을 추정한다. WT 가 패인별 작업 디렉터리를 노출하지 않으므로
    /// 화면에 보이는 프롬프트 -> 제목에 포함된 경로 순으로 찾고, 없으면 빈 문자열을 돌려준다.
    /// </summary>
    private static string ResolveFolder(AutomationElement el, string title,
                                        IReadOnlyDictionary<string, string> folderByTitle)
    {
        // 0) 셸 프로세스에서 직접 얻은 작업 폴더. 콘솔 제목으로 패인과 정확히 매칭된다.
        if (!string.IsNullOrWhiteSpace(title) &&
            folderByTitle.TryGetValue(title, out var exact) &&
            !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        string text = "";
        try
        {
            if (el.GetCachedPattern(TextPattern.Pattern) is TextPattern tp)
            {
                var sb = new StringBuilder();
                foreach (var range in tp.GetVisibleRanges())
                    sb.Append(range.GetText(-1));
                text = sb.ToString();
            }
        }
        catch { /* 패턴 미지원/타이밍 문제는 그냥 다음 단계로 */ }

        if (text.Length > 0)
        {
            // 1) 셸 프롬프트 - 가장 정확하다.
            var m = WindowsPrompt.Matches(text);
            if (m.Count > 0) return Normalize(m[^1].Groups[1].Value);

            var p = PosixPrompt.Matches(text);
            if (p.Count > 0) return Normalize(p[^1].Groups[1].Value);

            // 2) 프롬프트가 안 보이는 경우(대체 화면 버퍼를 쓰는 TUI 등):
            //    화면에 보이는 절대 경로 중 가장 자주 등장하는 디렉터리를 작업 폴더로 추정한다.
            string guess = MostCommonDirectory(text);
            if (guess.Length > 0) return guess;
        }

        // 3) 제목에 경로가 들어 있는 경우 (셸이 타이틀을 cwd 로 설정한 환경)
        if (!string.IsNullOrWhiteSpace(title))
        {
            var m = AnyAbsolutePath.Match(title);
            if (m.Success) return StripFileName(Normalize(m.Value));
        }

        return "";
    }

    /// <summary>화면 텍스트에서 가장 자주 등장하는 절대 디렉터리 경로를 고른다.</summary>
    private static string MostCommonDirectory(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AnyAbsolutePath.Matches(text))
        {
            string p = StripFileName(Normalize(m.Value));
            if (p.Length < 5) continue;
            counts[p] = counts.TryGetValue(p, out int c) ? c + 1 : 1;
        }
        if (counts.Count == 0) return "";

        // 동률이면 더 구체적인(긴) 경로를 택한다.
        return counts.OrderByDescending(kv => kv.Value)
                     .ThenByDescending(kv => kv.Key.Length)
                     .First().Key;
    }

    /// <summary>마지막 구간이 파일 이름처럼 보이면 떼어내 디렉터리만 남긴다.</summary>
    private static string StripFileName(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        if (cut <= 0 || cut >= path.Length - 1) return path;

        string last = path[(cut + 1)..];
        int dot = last.LastIndexOf('.');
        bool looksLikeFile = dot > 0 && dot < last.Length - 1 && last.Length - dot - 1 <= 6;
        return looksLikeFile ? path[..cut] : path;
    }

    /// <summary>경로 문자열 정리. 끝의 구분자와 문장부호만 떼고 경로 전체를 유지한다.</summary>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim().TrimEnd('.', ',', ')', ']', '}', '"', '\'', ';', '`');
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        return path;
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
