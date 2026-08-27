using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PaneSticker.Interop;
using PaneSticker.Model;
using PaneSticker.Services;
using PaneSticker.Views;

namespace PaneSticker;

public partial class App : Application
{
    private const int HotkeyToggle = 9001;
    private const int HotkeyOpacityDown = 9002;
    private const int HotkeyOpacityUp = 9003;

    private const uint VK_P = 0x50;
    private const uint VK_OEM_MINUS = 0xBD;
    private const uint VK_OEM_PLUS = 0xBB;

    private Mutex? _singleInstance;
    private AppSettings _settings = null!;
    private OverlayWindow _overlay = null!;
    private PaneTracker _tracker = null!;
    private WinEventWatcher? _watcher;
    private TrayIconHost _tray = null!;
    private SettingsWindow? _settingsWindow;

    private volatile uint _terminalPid;
    private TrackerSnapshot _lastSnapshot = TrackerSnapshot.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\PaneSticker.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("PaneSticker 가 이미 실행 중입니다. (트레이 아이콘 확인)",
                "PaneSticker", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("오류가 발생했습니다.\n\n" + args.Exception, "PaneSticker",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _settings = AppSettings.Load();
        _settings.PropertyChanged += OnSettingsChanged;

        _overlay = new OverlayWindow(_settings);
        _overlay.HotKeyPressed += OnHotKey;
        _overlay.Show();     // ShowActivated=False + WS_EX_NOACTIVATE 이므로 포커스를 뺏지 않음
        _overlay.Hide();     // 대상이 잡히기 전까지는 숨김

        RegisterHotkeys();

        _tray = new TrayIconHost(_settings);
        _tray.OpenSettingsRequested += ShowSettings;
        _tray.DumpTreeRequested += DumpTree;
        _tray.ExitRequested += () => Shutdown();
        _tray.EnabledToggled += () => { _settings.Save(); ForceRefresh(); };

        _tracker = new PaneTracker
        {
            PollIntervalMs = _settings.PollIntervalMs,
            ResolveFolders = NeedsFolders(_settings.BadgeLabel)
        };
        _tracker.SnapshotUpdated += OnSnapshot;
        _tracker.Start();

        _watcher = new WinEventWatcher(OnWinEvent);
    }

    // ------------------------------------------------------------- 이벤트 처리

    private void OnSnapshot(TrackerSnapshot snap)
    {
        _lastSnapshot = snap;
        if (snap.TargetWindow != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(snap.TargetWindow, out uint pid);
            _terminalPid = pid;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _overlay.ApplySnapshot(snap);
            _settingsWindow?.UpdateStatus(snap);
        }));
    }

    private void OnWinEvent(IntPtr hwnd, uint eventType)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            _tracker.Nudge();
            return;
        }

        // 대상 창(또는 그 자식)에서 온 이벤트만 반영해 불필요한 스캔을 막는다.
        if (string.Equals(NativeMethods.GetWindowClassName(hwnd), "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.Ordinal))
        {
            _tracker.Nudge();
            return;
        }

        uint pid = _terminalPid;
        if (pid == 0) return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint evPid);
        if (evPid == pid) _tracker.Nudge();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.PollIntervalMs))
            _tracker.PollIntervalMs = _settings.PollIntervalMs;

        if (e.PropertyName == nameof(AppSettings.BadgeLabel))
            _tracker.ResolveFolders = NeedsFolders(_settings.BadgeLabel);

        _tray?.SyncEnabled();
        ForceRefresh();
    }

    private static bool NeedsFolders(BadgeLabelMode mode)
        => mode is BadgeLabelMode.FolderPath or BadgeLabelMode.FolderName;

    private void ForceRefresh()
    {
        _overlay.InvalidateRender();
        _overlay.ApplySnapshot(_lastSnapshot);
        _tracker?.Invalidate();
    }

    // ------------------------------------------------------------------ 단축키

    private void RegisterHotkeys()
    {
        if (!_settings.HotkeysEnabled) return;
        uint mod = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        NativeMethods.RegisterHotKey(_overlay.Handle, HotkeyToggle, mod, VK_P);
        NativeMethods.RegisterHotKey(_overlay.Handle, HotkeyOpacityDown, mod, VK_OEM_MINUS);
        NativeMethods.RegisterHotKey(_overlay.Handle, HotkeyOpacityUp, mod, VK_OEM_PLUS);
    }

    private void OnHotKey(int id)
    {
        switch (id)
        {
            case HotkeyToggle:
                _settings.Enabled = !_settings.Enabled;
                break;
            case HotkeyOpacityDown:
                _settings.Opacity = Math.Round(_settings.Opacity - 0.05, 2);
                break;
            case HotkeyOpacityUp:
                _settings.Opacity = Math.Round(_settings.Opacity + 0.05, 2);
                break;
        }
        _settings.Save();
    }

    // ------------------------------------------------------------------- 트레이

    private void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => { _settings.Save(); _settingsWindow = null; };
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
        _settingsWindow.UpdateStatus(_lastSnapshot);
    }

    private void DumpTree()
    {
        var snap = _lastSnapshot;
        if (!snap.HasTarget)
        {
            _tray.Notify("PaneSticker", "Windows Terminal 창을 찾지 못했습니다. 터미널을 활성화한 뒤 다시 시도하세요.");
            return;
        }

        string path = Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "uia-tree.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PaneTracker.DumpUiaTree(snap.TargetWindow));
            _tray.Notify("PaneSticker", "UIA 트리를 저장했습니다:\n" + path);
        }
        catch (Exception ex)
        {
            _tray.Notify("PaneSticker", "덤프 실패: " + ex.Message);
        }
    }

    // -------------------------------------------------------------------- 종료

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_overlay != null)
            {
                NativeMethods.UnregisterHotKey(_overlay.Handle, HotkeyToggle);
                NativeMethods.UnregisterHotKey(_overlay.Handle, HotkeyOpacityDown);
                NativeMethods.UnregisterHotKey(_overlay.Handle, HotkeyOpacityUp);
            }
            _watcher?.Dispose();
            _tracker?.Dispose();
            _tray?.Dispose();
            _settings?.Save();
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
