using System;
using Microsoft.Win32;

namespace PaneSticker.Services;

/// <summary>
/// Windows 로그인 시 자동 실행. 현재 사용자 Run 키에 등록하므로 관리자 권한이 필요 없다.
/// 상태는 settings.json 이 아니라 레지스트리를 기준으로 읽는다.
/// 작업 관리자의 "시작 프로그램" 탭에서 끈 경우까지 반영하기 위해서다.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // 작업 관리자에서 끄고 켠 상태가 저장되는 곳. 값의 첫 바이트가 홀수면 꺼진 상태다.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PaneSticker";

    private static string? ExePath => Environment.ProcessPath;

    private static string Command => "\"" + ExePath + "\"";

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string s || s.Length == 0) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <returns>바라던 상태가 됐으면 true.</returns>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    if (string.IsNullOrEmpty(ExePath)) return false;
                    run.SetValue(ValueName, Command, RegistryValueKind.String);
                }
                else
                {
                    run.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }

            // 작업 관리자에서 꺼 둔 기록이 남아 있으면 Run 값을 써도 실행되지 않는다. 같이 지운다.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);

            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 자동 실행에 등록된 exe 가 없어졌으면(옮기거나 지운 경우) 지금 실행 중인 exe 로 고친다.
    /// 등록된 exe 가 아직 있으면 건드리지 않는다. 다른 위치의 사본을 잠깐 실행했다고 등록이 바뀌면 안 되기 때문이다.
    /// </summary>
    public static void RepairPathIfMissing()
    {
        try
        {
            if (string.IsNullOrEmpty(ExePath)) return;
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string s || s.Length == 0) return;
            if (!System.IO.File.Exists(s.Trim().Trim('"')))
                run.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        catch
        {
            // 경로 갱신 실패는 치명적이지 않다.
        }
    }
}
