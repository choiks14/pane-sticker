using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using PaneSticker.Model;

namespace PaneSticker.Views;

public sealed class ComboItem
{
    public string Text { get; init; } = "";
    public object Value { get; init; } = null!;
}

public sealed class LabelEntry : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private string _value;

    public LabelEntry(AppSettings settings, int index)
    {
        _settings = settings;
        Index = index;
        _value = settings.GetLabel(index);
    }

    public int Index { get; }
    public string Header => $"패인 {Index}";

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value ?? "";
            _settings.SetLabel(Index, _value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    private const int LabelSlots = 8;

    private readonly AppSettings _settings;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        LabelModeCombo.ItemsSource = new List<ComboItem>
        {
            new() { Text = "폴더 경로 (D:\\workspace\\sticker)", Value = BadgeLabelMode.FolderPath },
            new() { Text = "폴더 이름 (sticker)",         Value = BadgeLabelMode.FolderName },
            new() { Text = "패인 제목",                   Value = BadgeLabelMode.Title },
            new() { Text = "번호 (1, 2, 3...)",           Value = BadgeLabelMode.Index },
        };

        AnchorCombo.ItemsSource = new List<ComboItem>
        {
            new() { Text = "좌측 상단",   Value = BadgeAnchor.TopLeft },
            new() { Text = "중앙 상단",   Value = BadgeAnchor.TopCenter },
            new() { Text = "우측 상단",   Value = BadgeAnchor.TopRight },
            new() { Text = "좌측 하단",   Value = BadgeAnchor.BottomLeft },
            new() { Text = "우측 하단",   Value = BadgeAnchor.BottomRight },
        };

        VisibilityCombo.ItemsSource = new List<ComboItem>
        {
            new() { Text = "터미널이 활성일 때만", Value = VisibilityMode.TerminalFocused },
            new() { Text = "터미널이 보이면 항상", Value = VisibilityMode.TerminalVisible },
        };

        var entries = new ObservableCollection<LabelEntry>();
        for (int i = 1; i <= LabelSlots; i++) entries.Add(new LabelEntry(_settings, i));
        LabelList.ItemsSource = entries;

        LoadFromSettings();
        WireEvents();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        OpacitySlider.Value = _settings.Opacity;
        ShowBadgesCheck.IsChecked = _settings.ShowBadges;
        ShowBordersCheck.IsChecked = _settings.ShowBorders;
        ShowTitleCheck.IsChecked = _settings.ShowTitle;
        LabelModeCombo.SelectedValue = _settings.BadgeLabel;
        AnchorCombo.SelectedValue = _settings.BadgeAnchor;
        FontSlider.Value = _settings.BadgeFontSize;
        MarginSlider.Value = _settings.BadgeMargin;
        BorderSlider.Value = _settings.BorderThickness;
        AccentBox.Text = _settings.AccentColor;
        FocusBox.Text = _settings.FocusColor;
        TextColorBox.Text = _settings.TextColor;
        VisibilityCombo.SelectedValue = _settings.Visibility;
        PollSlider.Value = _settings.PollIntervalMs;
        RefreshValueLabels();
    }

    private void WireEvents()
    {
        OpacitySlider.ValueChanged += (_, _) => Apply(() => _settings.Opacity = OpacitySlider.Value);
        FontSlider.ValueChanged += (_, _) => Apply(() => _settings.BadgeFontSize = FontSlider.Value);
        MarginSlider.ValueChanged += (_, _) => Apply(() => _settings.BadgeMargin = MarginSlider.Value);
        BorderSlider.ValueChanged += (_, _) => Apply(() => _settings.BorderThickness = BorderSlider.Value);
        PollSlider.ValueChanged += (_, _) => Apply(() => _settings.PollIntervalMs = (int)PollSlider.Value);

        ShowBadgesCheck.Click += (_, _) => Apply(() => _settings.ShowBadges = ShowBadgesCheck.IsChecked == true);
        ShowBordersCheck.Click += (_, _) => Apply(() => _settings.ShowBorders = ShowBordersCheck.IsChecked == true);
        ShowTitleCheck.Click += (_, _) => Apply(() => _settings.ShowTitle = ShowTitleCheck.IsChecked == true);

        LabelModeCombo.SelectionChanged += (_, _) => Apply(() =>
        {
            if (LabelModeCombo.SelectedValue is BadgeLabelMode lm) _settings.BadgeLabel = lm;
        });
        AnchorCombo.SelectionChanged += (_, _) => Apply(() =>
        {
            if (AnchorCombo.SelectedValue is BadgeAnchor a) _settings.BadgeAnchor = a;
        });
        VisibilityCombo.SelectionChanged += (_, _) => Apply(() =>
        {
            if (VisibilityCombo.SelectedValue is VisibilityMode v) _settings.Visibility = v;
        });

        AccentBox.TextChanged += (_, _) => Apply(() => _settings.AccentColor = AccentBox.Text);
        FocusBox.TextChanged += (_, _) => Apply(() => _settings.FocusColor = FocusBox.Text);
        TextColorBox.TextChanged += (_, _) => Apply(() => _settings.TextColor = TextColorBox.Text);
    }

    private void Apply(Action action)
    {
        if (_loading) return;
        action();
        RefreshValueLabels();
    }

    private void RefreshValueLabels()
    {
        OpacityValue.Text = $"{_settings.Opacity * 100:0}%";
        FontValue.Text = $"{_settings.BadgeFontSize:0}px";
        MarginValue.Text = $"{_settings.BadgeMargin:0}px";
        BorderValue.Text = $"{_settings.BorderThickness:0.0}";
        PollValue.Text = $"{_settings.PollIntervalMs}ms";
    }

    public void UpdateStatus(TrackerSnapshot snap)
    {
        if (!snap.HasTarget)
        {
            StatusText.Text = "Windows Terminal 창을 찾지 못했습니다.";
        }
        else
        {
            string state = snap.IsMinimized ? "최소화됨" : (snap.IsForeground ? "활성" : "비활성");
            StatusText.Text = $"터미널 감지됨 · 패인 {snap.Panes.Count}개 · {state}";
        }

        if (string.IsNullOrEmpty(snap.Diagnostic))
        {
            DiagText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DiagText.Text = snap.Diagnostic;
            DiagText.Visibility = Visibility.Visible;
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        _settings.Opacity = d.Opacity;
        _settings.ShowBadges = d.ShowBadges;
        _settings.ShowBorders = d.ShowBorders;
        _settings.ShowTitle = d.ShowTitle;
        _settings.BadgeLabel = d.BadgeLabel;
        _settings.BadgeAnchor = d.BadgeAnchor;
        _settings.BadgeFontSize = d.BadgeFontSize;
        _settings.BadgeMargin = d.BadgeMargin;
        _settings.BorderThickness = d.BorderThickness;
        _settings.AccentColor = d.AccentColor;
        _settings.FocusColor = d.FocusColor;
        _settings.TextColor = d.TextColor;
        _settings.Visibility = d.Visibility;
        _settings.PollIntervalMs = d.PollIntervalMs;

        _loading = true;
        LoadFromSettings();
        _loading = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
