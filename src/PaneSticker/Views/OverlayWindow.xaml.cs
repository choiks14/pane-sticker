using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PaneSticker.Interop;
using PaneSticker.Model;

namespace PaneSticker.Views;

/// <summary>
/// 클릭 통과(WS_EX_TRANSPARENT) + 최상위(HWND_TOPMOST) 반투명 오버레이.
/// Windows Terminal 창 영역을 그대로 덮고, 패인마다 테두리와 번호 배지를 그린다.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private string _renderedSignature = "";

    /// <summary>WM_HOTKEY 수신 시 hotkey id 전달.</summary>
    public event Action<int>? HotKeyPressed;

    public IntPtr Handle => _hwnd;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            HotKeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>강제 재렌더(설정 변경 후 호출).</summary>
    public void InvalidateRender() => _renderedSignature = "";

    public void ApplySnapshot(TrackerSnapshot snap)
    {
        bool visible = _settings.Enabled
                       && snap.HasTarget
                       && !snap.IsMinimized
                       && snap.Panes.Count > 0
                       && (_settings.Visibility == VisibilityMode.TerminalVisible || snap.IsForeground);

        if (!visible)
        {
            if (IsVisible) Hide();
            _renderedSignature = "";
            return;
        }

        if (!IsVisible) Show();

        var wb = snap.WindowBounds;
        int px = (int)Math.Round(wb.X);
        int py = (int)Math.Round(wb.Y);
        int pw = (int)Math.Round(wb.Width);
        int ph = (int)Math.Round(wb.Height);

        // 1) HWND 를 물리 픽셀로 정확히 배치한다. 이게 유일하게 신뢰할 수 있는 기준이다.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, px, py, pw, ph,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);

        // 2) WPF 가 DIP -> 디바이스 픽셀로 합성할 때 쓰는 실제 변환을 사용한다.
        //    AllowsTransparency 창은 고배율 모니터에서 레이아웃 DPI(VisualTreeHelper.GetDpi)와
        //    합성 배율이 어긋나는 경우가 있어, 합성 변환(TransformToDevice)만이 신뢰할 수 있다.
        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            var m = src.CompositionTarget.TransformToDevice;
            if (m.M11 is > 0.2 and < 8) sx = m.M11;
            if (m.M22 is > 0.2 and < 8) sy = m.M22;
        }

        // 3) WPF 모델(창 + 캔버스)에도 같은 크기를 알려 두어 잘림/되돌림을 막는다.
        Left = px / sx;
        Top = py / sy;
        Width = pw / sx;
        Height = ph / sy;
        Root.Width = pw / sx;
        Root.Height = ph / sy;

        DumpDpiOnce(pw, ph, sx, sy);

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, px, py, pw, ph,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);

        Opacity = _settings.Opacity;

        string sig = snap.Signature + "|" + SettingsSignature() + "|" + sx + "x" + sy;
        if (sig == _renderedSignature) return;
        _renderedSignature = sig;

        Render(snap, sx, sy);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateRender();
    }

    /// <summary>경로에서 마지막 구간(폴더 이름)만 뽑는다. 드라이브 루트면 "D:" 형태.</summary>
    private static string LastSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.TrimEnd('\\', '/');
        if (path.Length == 0) return "";
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    private string SettingsSignature() =>
        string.Join(',', _settings.ShowBadges, _settings.ShowBorders, _settings.ShowTitle,
            _settings.BadgeLabel, _settings.BadgeAnchor, _settings.BadgeFontSize, _settings.BadgeMargin,
            _settings.BorderThickness, _settings.AccentColor, _settings.FocusColor,
            _settings.TextColor, _settings.Labels.Count,
            string.Join('/', _settings.Labels.Values));

    private void Render(TrackerSnapshot snap, double sx, double sy)
    {
        Root.Children.Clear();
        var origin = snap.WindowBounds;

        var accent = MakeBrush(_settings.AccentColor, Color.FromRgb(0x2D, 0x6F, 0xF7));
        var focus = MakeBrush(_settings.FocusColor, Color.FromRgb(0xFF, 0x9F, 0x1C));
        var textBrush = MakeBrush(_settings.TextColor, Colors.White);

        foreach (var pane in snap.Panes)
        {
            double x = (pane.Bounds.X - origin.X) / sx;
            double y = (pane.Bounds.Y - origin.Y) / sy;
            double w = pane.Bounds.Width / sx;
            double h = pane.Bounds.Height / sy;
            if (w <= 2 || h <= 2) continue;

            var color = pane.Focused ? focus : accent;

            if (_settings.ShowBorders)
            {
                double t = _settings.BorderThickness * (pane.Focused ? 1.6 : 1.0);
                var box = new Rectangle
                {
                    Width = Math.Max(0, w - t),
                    Height = Math.Max(0, h - t),
                    Stroke = color,
                    StrokeThickness = t,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(box, x + t / 2);
                Canvas.SetTop(box, y + t / 2);
                Root.Children.Add(box);
            }

            if (!_settings.ShowBadges) continue;

            var badge = BuildBadge(pane, color, textBrush);
            if (badge == null) continue;   // 표기할 폴더가 없으면 테두리만 그린다
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = badge.DesiredSize;

            double m = _settings.BadgeMargin;
            double bx = _settings.BadgeAnchor switch
            {
                BadgeAnchor.TopCenter => x + (w - size.Width) / 2,
                BadgeAnchor.TopRight => x + w - size.Width - m,
                BadgeAnchor.BottomRight => x + w - size.Width - m,
                _ => x + m
            };
            double by = _settings.BadgeAnchor switch
            {
                BadgeAnchor.BottomLeft => y + h - size.Height - m,
                BadgeAnchor.BottomRight => y + h - size.Height - m,
                _ => y + m
            };

            Canvas.SetLeft(badge, Math.Max(x, bx));
            Canvas.SetTop(badge, Math.Max(y, by));
            Root.Children.Add(badge);
        }
    }

    private Border? BuildBadge(PaneInfo pane, Brush background, Brush foreground)
    {
        // 수동 지정 이름이 있으면 항상 그게 우선.
        string label = _settings.GetLabel(pane.Index);
        if (string.IsNullOrEmpty(label))
        {
            label = _settings.BadgeLabel switch
            {
                // 폴더 모드에서는 폴더만 표기한다. 알아내지 못하면 제목/번호로 대체하지 않고 비운다.
                BadgeLabelMode.FolderPath => pane.Folder,
                BadgeLabelMode.FolderName => LastSegment(pane.Folder),
                BadgeLabelMode.Title => pane.Title,
                _ => pane.Index.ToString()
            };
        }

        if (string.IsNullOrWhiteSpace(label)) return null;

        if (_settings.ShowTitle && !string.IsNullOrEmpty(pane.Title) &&
            !string.Equals(label, pane.Title, StringComparison.Ordinal))
        {
            label += "  ·  " + pane.Title;
        }

        var text = new TextBlock
        {
            Text = label,
            Foreground = foreground,
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
            FontSize = _settings.BadgeFontSize,
            FontWeight = FontWeights.SemiBold
        };

        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 3),
            Child = text,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black
            }
        };
    }

    private bool _dpiDumped;

    /// <summary>배율 관련 값을 한 번만 파일로 남긴다(고배율 모니터 정합 문제 진단용).</summary>
    private void DumpDpiOnce(int pw, int ph, double sx, double sy)
    {
        if (_dpiDumped) return;
        _dpiDumped = true;
        try
        {
            var layout = VisualTreeHelper.GetDpi(this);
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            uint winDpi = NativeMethods.GetDpiForWindow(_hwnd);
            string line =
                $"physical={pw}x{ph} usedScale={sx:0.###}/{sy:0.###} " +
                $"layoutDpi={layout.DpiScaleX:0.###}/{layout.DpiScaleY:0.###} " +
                $"transformToDevice={m.M11:0.###}/{m.M22:0.###} " +
                $"GetDpiForWindow={winDpi} " +
                $"pmv2Applied={Interop.DpiBootstrap.Applied}/err={Interop.DpiBootstrap.LastError} " +
                $"canvasActual={Root.ActualWidth:0.#}x{Root.ActualHeight:0.#}";
            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Model.AppSettings.FilePath)!, "dpi-debug.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, line);
        }
        catch { }
    }

    private static SolidColorBrush MakeBrush(string value, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch { }
        var fb = new SolidColorBrush(fallback);
        fb.Freeze();
        return fb;
    }
}
