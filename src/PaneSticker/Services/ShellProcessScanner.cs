using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    private readonly object _lock = new();
    private Dictionary<string, string> _byTitle = new(StringComparer.Ordinal);
    private long _lastRefresh = -RefreshMs * 2L;

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

    private static Dictionary<string, string> Scan()
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

        foreach (int shell in shells)
        {
            string title = ReadConsoleTitle(shell);
            if (string.IsNullOrWhiteSpace(title)) continue;

            string folder = BestFolder(shell, childrenOf);
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

    /// <summary>
    /// 셸에서 가장 가까운(=얕은) 자손의 CWD 를 작업 폴더로 삼는다.
    ///
    /// 최빈값을 쓰면 안 된다. Claude Code 같은 도구가 하위 디렉터리에서 잠깐씩 띄우는
    /// bash/cmd 프로세스 때문에 표시 경로가 작업 중에 계속 흔들리기 때문이다.
    /// 얕은 깊이를 우선하고, 같은 깊이면 더 짧은(=상위) 경로를 택하면
    /// 세션이 시작된 프로젝트 루트로 고정된다.
    /// </summary>
    private static string BestFolder(int shellPid, Dictionary<int, List<int>> childrenOf)
    {
        var queue = new Queue<(int Pid, int Depth)>();
        queue.Enqueue((shellPid, 0));

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

    private static string ReadConsoleTitle(int pid)
    {
        // GUI 앱이라 자체 콘솔이 없다. 잠깐 붙었다가 반드시 떼어낸다.
        if (!AttachConsole(pid)) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int n = GetConsoleTitleW(sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }
        finally
        {
            FreeConsole();
        }
    }

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
