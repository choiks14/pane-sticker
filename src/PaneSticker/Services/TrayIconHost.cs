using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PaneSticker.Model;

namespace PaneSticker.Services;

/// <summary>트레이 아이콘 + 우클릭 메뉴. 오버레이가 클릭 통과라 유일한 조작 지점이다.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly AppSettings _settings;
    private Icon? _generatedIcon;

    public event Action? OpenSettingsRequested;
    public event Action? DumpTreeRequested;
    public event Action? ExitRequested;
    public event Action? EnabledToggled;

    public TrayIconHost(AppSettings settings)
    {
        _settings = settings;

        _enabledItem = new ToolStripMenuItem("오버레이 표시 (Ctrl+Alt+P)")
        {
            CheckOnClick = true,
            Checked = settings.Enabled
        };
        _enabledItem.Click += (_, _) =>
        {
            _settings.Enabled = _enabledItem.Checked;
            EnabledToggled?.Invoke();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("설정...", null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add("UIA 트리 덤프 저장", null, (_, _) => DumpTreeRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "PaneSticker — Windows Terminal 패인 오버레이",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();
    }

    public void SyncEnabled() => _enabledItem.Checked = _settings.Enabled;

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(4000);
    }

    private Icon CreateIcon()
    {
        // 실행 파일에 임베드된 앱 아이콘을 그대로 쓴다(트레이와 작업표시줄 아이콘 일치).
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var embedded = Icon.ExtractAssociatedIcon(exe);
                if (embedded != null) return _generatedIcon = embedded;
            }
        }
        catch { /* 추출 실패 시 아래에서 직접 그린다 */ }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(230, 24, 26, 32));
            g.FillRectangle(bg, 2, 2, 28, 28);
            using var pen = new Pen(Color.FromArgb(255, 45, 111, 247), 2f);
            g.DrawRectangle(pen, 3, 3, 26, 26);
            g.DrawLine(pen, 16, 4, 16, 28);              // 세로 분할선
            using var accent = new SolidBrush(Color.FromArgb(255, 255, 159, 28));
            g.FillRectangle(accent, 5, 5, 8, 5);          // 좌측 배지
            using var accent2 = new SolidBrush(Color.FromArgb(255, 45, 111, 247));
            g.FillRectangle(accent2, 19, 5, 8, 5);        // 우측 배지
        }
        _generatedIcon = Icon.FromHandle(bmp.GetHicon());
        return _generatedIcon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon?.Dispose();
    }
}
