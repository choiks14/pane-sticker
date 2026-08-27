using System;
using System.Collections.Generic;
using System.Text;
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

    // 마지막으로 적용한 배치. 값이 그대로면 창을 다시 건드리지 않아 깜빡임을 막는다.
    private int _lastPx, _lastPy, _lastPw, _lastPh;
    private double _lastSx = 1, _lastSy = 1;
    private bool _placed;

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
            _placed = false;
            return;
        }

        if (!IsVisible) Show();

        var wb = snap.WindowBounds;
        int px = (int)Math.Round(wb.X);
        int py = (int)Math.Round(wb.Y);
        int pw = (int)Math.Round(wb.Width);
        int ph = (int)Math.Round(wb.Height);

        // 창 위치/크기가 그대로면 아무것도 건드리지 않는다.
        // SetWindowPos 나 Width/Height 대입은 매번 하면 레이어드 창이 다시 그려져 깜빡인다.
        if (!_placed || px != _lastPx || py != _lastPy || pw != _lastPw || ph != _lastPh)
        {
            Reposition(px, py, pw, ph);
        }

        if (Math.Abs(Opacity - _settings.Opacity) > 0.001) Opacity = _settings.Opacity;

        // 화면에 실제로 그려질 내용만으로 서명을 만든다.
        // 패인 제목은 폴더를 찾는 데만 쓰이고 자주 바뀌므로, 표시 내용이 같으면 다시 그리지 않는다.
        string sig = RenderSignature(snap);
        if (sig == _renderedSignature) return;
        _renderedSignature = sig;

        Render(snap, _lastSx, _lastSy);
    }

    /// <summary>HWND(물리 픽셀)와 WPF 레이아웃(DIP)을 같은 목표로 맞춘다.</summary>
    private void Reposition(int px, int py, int pw, int ph)
    {
        const uint flags = NativeMethods.SWP_NOACTIVATE
                         | NativeMethods.SWP_NOOWNERZORDER
                         | NativeMethods.SWP_SHOWWINDOW;

        // 1) HWND 를 물리 픽셀로 정확히 배치한다. 이게 유일하게 신뢰할 수 있는 기준이다.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, px, py, pw, ph, flags);

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

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, px, py, pw, ph, flags);

        _lastPx = px; _lastPy = py; _lastPw = pw; _lastPh = ph;
        _lastSx = sx; _lastSy = sy;
        _placed = true;
    }

    private string RenderSignature(TrackerSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append(_lastSx).Append('x').Append(_lastSy).Append('|').Append(SettingsSignature()).Append('|');
        foreach (var p in snap.Panes)
        {
            sb.Append((int)p.Bounds.X).Append(',').Append((int)p.Bounds.Y).Append(',')
              .Append((int)p.Bounds.Width).Append(',').Append((int)p.Bounds.Height).Append(',')
              .Append(p.Focused ? '1' : '0').Append(',').Append(BuildLabel(p)).Append(';');
        }
        return sb.ToString();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _placed = false;          // 배율이 바뀌었으니 다시 배치해야 한다
        InvalidateRender();
    }

    /// <summary>
    /// 배지가 주어진 폭에 들어가도록 텍스트를 단계적으로 줄인다.
    /// 경로는 앞쪽(드라이브)과 뒤쪽(현재 폴더)이 중요하므로 가운데를 생략한다.
    /// </summary>
    private static void FitBadge(Border badge, double maxWidth)
    {
        if (badge.Child is not TextBlock text)
        {
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return;
        }

        foreach (string candidate in ElisionCandidates(text.Text))
        {
            text.Text = candidate;
            badge.InvalidateMeasure();
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (badge.DesiredSize.Width <= maxWidth) return;
        }
        // 어떤 후보도 못 맞추면 마지막(가장 짧은) 상태로 둔다.
    }

    /// <summary>원본부터 시작해 점점 짧아지는 표시 후보들.</summary>
    private static IEnumerable<string> ElisionCandidates(string label)
    {
        yield return label;
        if (string.IsNullOrEmpty(label)) yield break;

        var parts = label.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

        // 1단계: 가운데 구간을 통째로 생략. "D:\a\b\c\d" -> "D:\…\c\d" -> "D:\…\d"
        if (parts.Length >= 3)
        {
            string head = parts[0];
            for (int keep = parts.Length - 2; keep >= 1; keep--)
            {
                yield return head + "\\…\\" + string.Join('\\', parts, parts.Length - keep, keep);
            }
        }

        // 2단계: 그래도 넘치면 마지막 구간 자체를 가운데에서 자른다.
        string tail = parts.Length > 0 ? parts[^1] : label;
        for (int len = tail.Length - 1; len >= 5; len -= 2)
        {
            int front = (len + 1) / 2;
            int back = len - front;
            yield return tail.Substring(0, front) + "…" + tail.Substring(tail.Length - back, back);
        }

        yield return "…";
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

        Color accentColor = ParseColor(_settings.AccentColor, Color.FromRgb(0x4A, 0x55, 0x68));
        Color focusColor = ParseColor(_settings.FocusColor, Color.FromRgb(0xFF, 0x9F, 0x1C));
        Color preferredText = ParseColor(_settings.TextColor, Colors.White);

        var accent = Frozen(accentColor);
        var focus = Frozen(focusColor);

        // 배지 배경이 밝으면(주황 등) 흰 글씨는 대비가 2:1 수준까지 떨어져 읽기 힘들다.
        // 배경마다 대비가 더 좋은 글자색을 고른다.
        var accentText = PickForeground(accentColor, preferredText);
        var focusText = PickForeground(focusColor, preferredText);

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

            var badge = BuildBadge(pane, color, pane.Focused ? focusText : accentText);
            if (badge == null) continue;   // 표기할 폴더가 없으면 테두리만 그린다

            double m = _settings.BadgeMargin;

            // 배지가 패인 폭을 넘으면 옆 구역까지 침범한다. 경로 가운데를 생략해 맞춘다.
            FitBadge(badge, Math.Max(24, w - m * 2));
            var size = badge.DesiredSize;

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

    /// <summary>배지에 쓸 문자열. 표기할 게 없으면 빈 문자열.</summary>
    private string BuildLabel(PaneInfo pane)
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

        if (string.IsNullOrWhiteSpace(label)) return "";

        if (_settings.ShowTitle && !string.IsNullOrEmpty(pane.Title) &&
            !string.Equals(label, pane.Title, StringComparison.Ordinal))
        {
            label += "  ·  " + pane.Title;
        }

        return label;
    }

    private Border? BuildBadge(PaneInfo pane, Brush background, Brush foreground)
    {
        string label = BuildLabel(pane);
        if (label.Length == 0) return null;

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

    /// <summary>설정의 색상 문자열을 파싱한다. 형식이 틀리면 기본값.</summary>
    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color c) return c;
        }
        catch { }
        return fallback;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>배경 위에서 가장 잘 읽히는 글자색을 고른다.</summary>
    private static SolidColorBrush PickForeground(Color background, Color preferred)
    {
        // 사용자가 고른 색이 WCAG AA(4.5:1)를 이미 만족하면 그대로 존중한다.
        double best = Contrast(preferred, background);
        if (best >= 4.5) return Frozen(preferred);

        Color chosen = preferred;
        foreach (Color candidate in new[] { DarkInk, Colors.White })
        {
            double ratio = Contrast(candidate, background);
            if (ratio > best)
            {
                best = ratio;
                chosen = candidate;
            }
        }
        return Frozen(chosen);
    }

    /// <summary>밝은 배경(주황 등)에 쓰는 어두운 글자색.</summary>
    private static readonly Color DarkInk = Color.FromRgb(0x14, 0x16, 0x1B);

    // WCAG 2.x 대비비 계산
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte value)
    {
        double s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
