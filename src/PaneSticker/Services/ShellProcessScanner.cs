using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PaneSticker.Services;

/// <summary>
/// Windows Terminal 각 패인의 셸 프로세스를 찾아 "콘솔 제목 -> 작업 폴더" 표를 만든다.
///
/// WT 는 패인별 작업 디렉터리를 노출하지 않지만, 다음 두 사실을 이용하면 정확히 이어붙일 수 있다.
///  1) 패인의 셸(powershell 등)은 WindowsTerminal.exe 의 직계 자식이고,
///     AttachConsole + GetConsoleTitle 로 그 패인의 콘솔 제목을 읽을 수 있다.
///     이 제목은 UIA 가 TermControl.HelpText 로 노출하는 패인 제목과 같다.
///  2) 실제 작업 폴더는 셸 자신이 아니라 셸에서 가장 가까운 자손 프로세스(claude -> node 등)의 CWD 에 있다.
///     PowerShell 은 Set-Location 을 해도 프로세스 CWD 를 바꾸지 않기 때문이다.
/// </summary>
public sealed class ShellProcessScanner
{
    private const int RefreshMs = 3000;

    /// <summary>강제 갱신 최소 간격. 제목이 바뀐 직후 즉시 다시 찾되 과도한 스캔은 막는다.</summary>
    private const int ForcedMinIntervalMs = 400;

    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "wsl.exe", "nu.exe", "zsh.exe", "fish.exe"
    };

    /// <summary>
    /// 셸 자신의 CWD 가 실제 위치를 반영하지 않는 셸. Set-Location 은 프로세스 CWD 를 바꾸지 않고,
    /// 자식을 띄울 때만 그 시점의 위치를 자식 CWD 로 넘겨준다.
    /// </summary>
    private static readonly HashSet<string> StaleCwdShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe"
    };

    private readonly object _lock = new();
    private Dictionary<string, string> _byTitle = new(StringComparer.Ordinal);
    private long _lastRefresh = -RefreshMs * 2L;

    /// <summary>
    /// 셸 PID 별로 마지막에 자손에서 읽은 폴더. 명령이 끝나 자손이 사라져도
    /// 셸 시작 폴더(낡은 값)로 되돌아가지 않게 한다.
    /// </summary>
    private readonly Dictionary<int, string> _lastChildFolder = new();

    /// <summary>
    /// 콘솔 제목 -> 작업 폴더. 평소에는 RefreshMs 주기로만 스캔한다.
    /// force 는 제목이 방금 바뀌어 표에서 못 찾았을 때 쓰며, 이때도 ForcedMinIntervalMs 로 제한한다.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetMap(bool force = false)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            long interval = force ? ForcedMinIntervalMs : RefreshMs;
            if (now - _lastRefresh >= interval)
            {
                _lastRefresh = now;
                try { _byTitle = Scan(); }
                catch (Exception ex) { Debug.WriteLine("[PaneSticker] shell scan failed: " + ex.Message); }
            }
            return _byTitle;
        }
    }

    /// <summary>
    /// 제목 비교용 정규화. Claude Code 등은 제목 앞에 스피너 글자(✳, ⠂, ⠐ …)를 돌린다.
    /// UIA 제목과 콘솔 제목을 읽는 시점이 달라 프레임이 어긋나면 매칭이 깨지거나,
    /// 다른 패인의 제목과 우연히 같아져 엉뚱한 폴더가 붙는다. 앞쪽 비문자 기호를 걷어 낸다.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        int i = 0;
        while (i < title.Length && !char.IsLetterOrDigit(title[i])) i++;
        return title[i..].Trim();
    }

    private Dictionary<string, string> Scan()
    {
        var (nameOf, childrenOf) = SnapshotProcesses();

        // WindowsTerminal.exe 들의 직계 자식 중 셸만 고른다.
        var shells = new List<int>();
        foreach (var kv in nameOf)
        {
            if (!string.Equals(kv.Value, "WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!childrenOf.TryGetValue(kv.Key, out var kids)) continue;
            foreach (int kid in kids)
                if (nameOf.TryGetValue(kid, out var kn) && ShellNames.Contains(kn))
                    shells.Add(kid);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        // 사라진 셸의 기억은 버린다(PID 재사용 시 엉뚱한 값이 붙지 않게).
        // 스냅샷이 실패해 셸이 하나도 안 잡힌 경우에는 기억을 지우지 않는다.
        if (shells.Count > 0)
        {
            var alive = new HashSet<int>(shells);
            foreach (int dead in _lastChildFolder.Keys.Where(k => !alive.Contains(k)).ToList())
                _lastChildFolder.Remove(dead);
        }

        foreach (int shell in shells)
        {
            bool stale = StaleCwdShells.Contains(nameOf[shell]);

            // 콘솔에 붙어 있는 구간은 최소로 유지한다. 경로 존재 확인 등은 떼어낸 뒤에 한다.
            var (rawTitle, rawPrompt, idle) = ReadConsole(shell, readPrompt: stale);
            string title = NormalizeTitle(rawTitle);
            if (title.Length == 0) continue;

            string prompt = ValidatePromptPath(rawPrompt);
            string folder = stale
                ? ResolvePowerShellFolder(shell, childrenOf, prompt, idle)
                : ResolvePlainShellFolder(shell, childrenOf);
            if (folder.Length == 0) continue;

            if (result.TryGetValue(title, out var existing))
            {
                // 제목이 겹치면(예: 둘 다 "Windows PowerShell") 어느 패인인지 특정할 수 없다.
                // 잘못된 폴더를 보여주느니 비워 둔다.
                if (!string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase))
                    ambiguous.Add(title);
            }
            else
            {
                result[title] = folder;
            }
        }

        foreach (var t in ambiguous) result.Remove(t);
        return result;
    }

    /// <summary>cmd 등: cd 가 프로세스 CWD 를 바꾸므로 셸 자신이 최신 값이다.</summary>
    private static string ResolvePlainShellFolder(int shellPid, Dictionary<int, List<int>> childrenOf)
    {
        string own = ReadMeaningfulCwd(shellPid);
        return own.Length > 0 ? own : BestDescendantFolder(shellPid, childrenOf);
    }

    /// <summary>
    /// PowerShell: 셸 CWD 는 시작 폴더에 멈춰 있다(Set-Location 이 프로세스 CWD 를 안 바꾼다).
    ///  1) 커서 줄이 곧 프롬프트(=입력 대기)면 그 경로가 지금 위치다.
    ///     백그라운드 job·watch 같은 오래 사는 자식이 있어도 이쪽이 더 최신이다.
    ///  2) 명령 실행 중이면 가장 가까운 자손 CWD(실행 시점의 $PWD).
    ///  3) 줄바꿈된 프롬프트에서 읽은 경로 -> 마지막으로 본 폴더 -> 셸 자신 순.
    /// </summary>
    private string ResolvePowerShellFolder(int shellPid, Dictionary<int, List<int>> childrenOf,
        string prompt, bool idle)
    {
        if (idle && prompt.Length > 0) return Remember(shellPid, prompt);

        string child = BestDescendantFolder(shellPid, childrenOf);
        if (child.Length > 0) return Remember(shellPid, child);

        if (prompt.Length > 0) return Remember(shellPid, prompt);
        if (_lastChildFolder.TryGetValue(shellPid, out var remembered)) return remembered;
        return ReadMeaningfulCwd(shellPid);
    }

    private string Remember(int shellPid, string folder)
    {
        _lastChildFolder[shellPid] = folder;
        return folder;
    }

    /// <summary>
    /// 프롬프트에서 읽은 경로를 확인한다. 콘솔에서 떼어낸 뒤에 호출한다.
    /// 네트워크 드라이브는 끊겨 있으면 Exists 가 수 초씩 막히므로 확인을 건너뛴다.
    /// </summary>
    private static string ValidatePromptPath(string path)
    {
        if (path.Length == 0) return "";
        try
        {
            var drive = new System.IO.DriveInfo(path.Substring(0, 1));
            if (drive.DriveType == System.IO.DriveType.Network) return path;
            return System.IO.Directory.Exists(path) ? path : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadMeaningfulCwd(int pid)
    {
        string cwd = ProcessCwd.Read(pid);
        return IsMeaningful(cwd) ? cwd.TrimEnd('\\', '/') : "";
    }

    /// <summary>
    /// 셸에서 가장 가까운(=얕은) 자손의 CWD 를 작업 폴더로 삼는다. 셸 자신은 제외한다.
    ///
    /// 최빈값을 쓰면 안 된다. Claude Code 같은 도구가 하위 디렉터리에서 잠깐씩 띄우는
    /// bash/cmd 프로세스 때문에 표시 경로가 작업 중에 계속 흔들리기 때문이다.
    /// 얕은 깊이를 우선하고, 같은 깊이면 더 짧은(=상위) 경로를 택하면
    /// 세션이 시작된 프로젝트 루트로 고정된다.
    /// </summary>
    private static string BestDescendantFolder(int shellPid, Dictionary<int, List<int>> childrenOf)
    {
        var queue = new Queue<(int Pid, int Depth)>();
        if (childrenOf.TryGetValue(shellPid, out var first))
            foreach (int kid in first) queue.Enqueue((kid, 1));

        int bestDepth = int.MaxValue;
        string best = "";
        int visited = 0;

        while (queue.Count > 0 && visited < 400)
        {
            var (pid, depth) = queue.Dequeue();
            visited++;

            // BFS 라 깊이가 단조 증가한다. 이미 더 얕은 곳에서 찾았으면 여기서 멈춘다.
            if (depth > bestDepth) continue;

            string cwd = ProcessCwd.Read(pid);
            if (IsMeaningful(cwd))
            {
                cwd = cwd.TrimEnd('\\', '/');
                if (depth < bestDepth || (depth == bestDepth && cwd.Length < best.Length))
                {
                    bestDepth = depth;
                    best = cwd;
                }
            }

            if (childrenOf.TryGetValue(pid, out var kids))
                foreach (int kid in kids) queue.Enqueue((kid, depth + 1));
        }

        return best;
    }

    /// <summary>시스템 디렉터리나 사용자 홈 루트는 작업 폴더로 보지 않는다.</summary>
    private static bool IsMeaningful(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Length < 4) return false;
        string p = cwd.TrimEnd('\\', '/');
        if (p.Length <= 3) return false;   // "C:\"

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && p.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (profile.Length > 0 && string.Equals(p, profile, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    // ------------------------------------------------------------ 콘솔 제목

    /// <summary>
    /// 셸 콘솔에 잠깐 붙어 제목과 "커서 줄의 프롬프트 경로"를 함께 읽는다.
    /// 프롬프트 경로는 화면 전체가 아니라 커서가 있는 줄(과 줄바꿈된 직전 줄)만 본다.
    /// 그 줄이 "PS D:\path>" 형태일 때만 쓰므로 명령 출력 속 경로에 흔들리지 않는다.
    /// </summary>
    private static (string Title, string PromptPath, bool Idle) ReadConsole(int pid, bool readPrompt)
    {
        EnsureCtrlHandler();

        // GUI 앱이라 자체 콘솔이 없다. 잠깐 붙었다가 반드시 떼어낸다.
        if (!AttachConsole(pid))
        {
            // 이미 다른 콘솔이 붙어 있으면 ACCESS_DENIED. 떼고 한 번만 다시 시도한다.
            if (Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED) return ("", "", false);
            FreeConsole();
            if (!AttachConsole(pid))
            {
                Debug.WriteLine("[PaneSticker] AttachConsole failed: " + Marshal.GetLastWin32Error());
                return ("", "", false);
            }
        }
        try
        {
            var sb = new StringBuilder(1024);
            int n = GetConsoleTitleW(sb, sb.Capacity);
            string title = n > 0 ? sb.ToString() : "";
            if (!readPrompt) return (title, "", false);
            var (path, idle) = ReadPromptPath();
            return (title, path, idle);
        }
        catch
        {
            return ("", "", false);
        }
        finally
        {
            FreeConsole();
        }
    }

    private const int ERROR_ACCESS_DENIED = 5;

    // 콘솔에 붙어 있는 동안 그 패인에서 Ctrl+C/Break 가 오면 이 프로세스에도 전달된다.
    // 기본 처리기는 ExitProcess 를 부르므로, 처리됨(TRUE)으로 삼켜 앱이 죽지 않게 한다.
    // 델리게이트는 GC 되지 않게 정적으로 붙잡아 둔다.
    private static ConsoleCtrlDelegate? _ctrlHandler;

    private static void EnsureCtrlHandler()
    {
        if (_ctrlHandler != null) return;
        _ctrlHandler = type => type is CTRL_C_EVENT or CTRL_BREAK_EVENT;
        SetConsoleCtrlHandler(_ctrlHandler, true);
    }

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    // "PS D:\a\b> " 또는 "D:\a\b>" 로 시작하는 줄. 사용자가 입력 중인 명령은 뒤에 붙어도 된다.
    private static readonly Regex PromptPath =
        new(@"^\s*(?:PS\s+)?([A-Za-z]:\\[^<>|""*?\r\n]*?)>", RegexOptions.Compiled);

    /// <summary>
    /// 커서 줄(과 줄바꿈된 직전 두 줄)에서 프롬프트 경로를 읽는다. 존재 확인은 하지 않는다(호출부가 detach 후에).
    /// Idle = 커서 줄 하나만으로 프롬프트가 매칭됨 = 셸이 입력을 기다리는 중.
    /// </summary>
    private static (string Path, bool Idle) ReadPromptPath()
    {
        IntPtr h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return ("", false);
        try
        {
            if (!GetConsoleScreenBufferInfo(h, out var info)) return ("", false);
            int width = info.dwSize.X;
            int cy = info.dwCursorPosition.Y;
            if (width <= 0 || width > 2000 || cy < 0) return ("", false);

            // 직전 줄은 줄바꿈 이어붙이기용이라 다듬지 않고 원문 그대로 붙인다.
            string line = "";
            for (int back = 0; back <= 2 && cy - back >= 0; back++)
            {
                line = ReadLine(h, cy - back, width) + line;
                var m = PromptPath.Match(line);
                if (!m.Success) continue;
                string path = m.Groups[1].Value.TrimEnd('\\', ' ');
                if (path.Length == 2) path += "\\";   // "C:" -> "C:\"
                return (path, back == 0);
            }
            return ("", false);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string ReadLine(IntPtr h, int y, int width)
    {
        var buf = new char[width];
        if (!ReadConsoleOutputCharacterW(h, buf, (uint)width, new COORD { X = 0, Y = (short)y }, out uint read))
            return "";
        return new string(buf, 0, (int)Math.Min(read, (uint)buf.Length));
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr h, [Out] char[] chars, uint length,
        COORD coord, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetConsoleTitleW(StringBuilder lpConsoleTitle, int nSize);

    // -------------------------------------------------------- 프로세스 스냅샷

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static (Dictionary<int, string> NameOf, Dictionary<int, List<int>> ChildrenOf) SnapshotProcesses()
    {
        var nameOf = new Dictionary<int, string>();
        var childrenOf = new Dictionary<int, List<int>>();

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return (nameOf, childrenOf);

        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref pe)) return (nameOf, childrenOf);

            do
            {
                int pid = (int)pe.th32ProcessID;
                int parent = (int)pe.th32ParentProcessID;
                nameOf[pid] = pe.szExeFile;

                if (!childrenOf.TryGetValue(parent, out var list))
                    childrenOf[parent] = list = new List<int>();
                list.Add(pid);
            }
            while (Process32NextW(snap, ref pe));
        }
        finally
        {
            CloseHandle(snap);
        }

        return (nameOf, childrenOf);
    }
}

/// <summary>다른 프로세스의 PEB 를 읽어 현재 작업 디렉터리를 얻는다. (x64 전용 오프셋)</summary>
internal static class ProcessCwd
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PROCESS_VM_READ = 0x0010;

    // x64 레이아웃
    private const int PebProcessParametersOffset = 0x20;
    private const int UppCurrentDirectoryOffset = 0x38;   // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass,
        ref PROCESS_BASIC_INFORMATION info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address,
        byte[] buffer, int size, out IntPtr read);

    public static string Read(int pid)
    {
        if (!Environment.Is64BitProcess) return "";

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return "";

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return "";
            if (pbi.PebBaseAddress == IntPtr.Zero) return "";

            var buf8 = new byte[8];
            if (!ReadProcessMemory(h, pbi.PebBaseAddress + PebProcessParametersOffset, buf8, 8, out _)) return "";
            long parameters = BitConverter.ToInt64(buf8, 0);
            if (parameters == 0) return "";

            // UNICODE_STRING { ushort Length; ushort MaximumLength; (4바이트 패딩) IntPtr Buffer; }
            var unicodeString = new byte[16];
            if (!ReadProcessMemory(h, new IntPtr(parameters + UppCurrentDirectoryOffset), unicodeString, 16, out _))
                return "";

            ushort length = BitConverter.ToUInt16(unicodeString, 0);
            long buffer = BitConverter.ToInt64(unicodeString, 8);
            if (length == 0 || length > 4096 || buffer == 0) return "";

            var chars = new byte[length];
            if (!ReadProcessMemory(h, new IntPtr(buffer), chars, length, out _)) return "";
            return Encoding.Unicode.GetString(chars);
        }
        catch
        {
            return "";
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
