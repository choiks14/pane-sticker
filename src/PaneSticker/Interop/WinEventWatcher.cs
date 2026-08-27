using System;
using System.Collections.Generic;
using PaneSticker.Interop;

namespace PaneSticker.Interop;

/// <summary>
/// 전역 WinEvent 훅. 창 이동/리사이즈/포커스/생성·소멸 이벤트가 오면 콜백을 호출한다.
/// 콜백은 UI 스레드에서 실행되므로 가벼운 작업만 해야 한다.
/// </summary>
public sealed class WinEventWatcher : IDisposable
{
    private readonly List<IntPtr> _hooks = new();
    private readonly NativeMethods.WinEventProc _proc;   // GC 방지용 필드 보관 필수
    private readonly Action<IntPtr, uint> _onEvent;
    private bool _disposed;

    public WinEventWatcher(Action<IntPtr, uint> onEvent)
    {
        _onEvent = onEvent;
        _proc = OnWinEvent;

        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Hook(NativeMethods.EVENT_SYSTEM_MOVESIZEEND, NativeMethods.EVENT_SYSTEM_MOVESIZEEND);
        Hook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        Hook(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_HIDE);
        Hook(NativeMethods.EVENT_OBJECT_REORDER, NativeMethods.EVENT_OBJECT_FOCUS);
        Hook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
    }

    private void Hook(uint min, uint max)
    {
        var h = NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;

        // 마우스 커서(OBJID_CURSOR = -9) 등 노이즈 제거: 창/클라이언트 객체만 통과
        if (idObject != NativeMethods.OBJID_WINDOW && idObject != NativeMethods.OBJID_CLIENT) return;

        try { _onEvent(hwnd, eventType); }
        catch { /* 훅 콜백에서 예외가 새어나가면 안 됨 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in _hooks) NativeMethods.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
