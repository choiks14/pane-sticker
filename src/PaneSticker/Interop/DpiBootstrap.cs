using System.Runtime.CompilerServices;

namespace PaneSticker.Interop;

/// <summary>
/// 프로세스를 Per-Monitor DPI Aware V2 로 승격시킨다.
///
/// 반드시 창이 하나라도 만들어지기 전에 실행돼야 하므로 [ModuleInitializer] 를 쓴다.
/// app.manifest 에도 같은 선언이 있지만 단일 파일(PublishSingleFile) 배포에서는
/// 매니페스트가 적용되지 않는 경우가 있어, 코드로 한 번 더 보장한다.
///
/// 이게 없으면 GetWindowRect 는 가상화된 좌표를, UI Automation 은 물리 좌표를 돌려주기 때문에
/// 100% 가 아닌 배율의 모니터에서 오버레이 위치와 크기가 어긋난다.
/// </summary>
internal static class DpiBootstrap
{
    /// <summary>PerMonitorV2 승격 성공 여부(진단용).</summary>
    internal static bool Applied { get; private set; }

    /// <summary>실패 시 Win32 오류 코드(진단용). 5 = ERROR_ACCESS_DENIED (이미 잠김).</summary>
    internal static int LastError { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            Applied = NativeMethods.SetProcessDpiAwarenessContext(
                NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (!Applied) LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        catch
        {
            // Windows 10 1703 미만에는 이 API 가 없다.
            LastError = -1;
        }
    }
}
