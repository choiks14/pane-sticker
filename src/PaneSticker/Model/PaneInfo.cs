using System;
using System.Collections.Generic;
using System.Windows;

namespace PaneSticker.Model;

/// <summary>Windows Terminal 한 개 패인(분할 구역)의 물리 픽셀 기준 정보.</summary>
public sealed class PaneInfo
{
    public int Index { get; init; }            // 1-based, 좌상단 -> 우하단 읽기 순서
    public Rect Bounds { get; init; }          // 물리 픽셀(스크린 좌표)
    public string Title { get; init; } = "";   // 패인 제목 (UIA HelpText)
    public string Folder { get; init; } = "";  // 추정한 작업 폴더 경로 (알 수 없으면 빈 문자열)
    public bool Focused { get; init; }

    public string Signature =>
        $"{Index}:{(int)Bounds.X},{(int)Bounds.Y},{(int)Bounds.Width},{(int)Bounds.Height}:" +
        $"{(Focused ? 1 : 0)}:{Title}:{Folder}";
}

/// <summary>추적기가 한 번 스캔한 결과.</summary>
public sealed class TrackerSnapshot
{
    public static readonly TrackerSnapshot Empty = new();

    public IntPtr TargetWindow { get; init; }
    public Rect WindowBounds { get; init; }        // 물리 픽셀
    public bool IsForeground { get; init; }
    public bool IsMinimized { get; init; }
    public IReadOnlyList<PaneInfo> Panes { get; init; } = Array.Empty<PaneInfo>();
    public string? Diagnostic { get; init; }

    public bool HasTarget => TargetWindow != IntPtr.Zero && !WindowBounds.IsEmpty;

    public string Signature
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(TargetWindow).Append('|')
              .Append((int)WindowBounds.X).Append(',').Append((int)WindowBounds.Y).Append(',')
              .Append((int)WindowBounds.Width).Append(',').Append((int)WindowBounds.Height).Append('|')
              .Append(IsForeground ? 1 : 0).Append(IsMinimized ? 1 : 0).Append('|');
            foreach (var p in Panes) sb.Append(p.Signature).Append(';');
            return sb.ToString();
        }
    }
}
