using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaneSticker.Model;

public enum BadgeAnchor { TopLeft, TopCenter, TopRight, BottomLeft, BottomRight }

/// <summary>諛곗???臾댁뾿???몄?.</summary>
public enum BadgeLabelMode
{
    /// <summary>?묒뾽 ?대뜑 ?꾩껜 寃쎈줈. ?뚯븘?댁? 紐삵븯硫?諛곗?瑜??쒖떆?섏? ?딅뒗??</summary>
    FolderPath,
    /// <summary>?묒뾽 ?대뜑 ?대쫫(留덉?留?援ш컙)留? ?뚯븘?댁? 紐삵븯硫?諛곗?瑜??쒖떆?섏? ?딅뒗??</summary>
    FolderName,
    /// <summary>?⑥씤 ?쒕ぉ.</summary>
    Title,
    /// <summary>?⑥씤 踰덊샇(1, 2, 3...).</summary>
    Index
}

public enum VisibilityMode
{
    /// <summary>?곕??먯씠 ?쒖꽦 李쎌씪 ?뚮쭔 ?쒖떆 (湲곕낯媛?.</summary>
    TerminalFocused,
    /// <summary>?곕???李쎌씠 蹂댁씠硫???긽 ?쒖떆.</summary>
    TerminalVisible
}

public sealed class AppSettings : INotifyPropertyChanged
{
    // ---- ?쒖떆 -------------------------------------------------------------
    private double _opacity = 0.95;
    private bool _showBadges = true;
    private bool _showBorders = true;
    private bool _showTitle;
    private bool _enabled = true;
    private BadgeAnchor _badgeAnchor = BadgeAnchor.TopLeft;
    private BadgeLabelMode _badgeLabel = BadgeLabelMode.FolderPath;
    private double _badgeFontSize = 13;
    private double _badgeMargin = 6;
    private double _borderThickness = 1.5;

    // ---- ?숈옉 -------------------------------------------------------------
    private VisibilityMode _visibility = VisibilityMode.TerminalFocused;
    private int _pollIntervalMs = 350;
    private bool _hotkeysEnabled = true;

    // ---- ?됱긽 (#AARRGGBB / #RRGGBB) ---------------------------------------
    private string _accentColor = "#FF4A5568";
    private string _focusColor = "#FFFF9F1C";
    private string _textColor = "#FFFFFFFF";

    /// <summary>?⑥씤 踰덊샇(1-based) -> ?ъ슜??吏???쇰꺼.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.05, 1.0)); }
    public bool ShowBadges { get => _showBadges; set => Set(ref _showBadges, value); }
    public bool ShowBorders { get => _showBorders; set => Set(ref _showBorders, value); }
    public bool ShowTitle { get => _showTitle; set => Set(ref _showTitle, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public BadgeAnchor BadgeAnchor { get => _badgeAnchor; set => Set(ref _badgeAnchor, value); }
    public BadgeLabelMode BadgeLabel { get => _badgeLabel; set => Set(ref _badgeLabel, value); }
    public double BadgeFontSize { get => _badgeFontSize; set => Set(ref _badgeFontSize, Math.Clamp(value, 8, 48)); }
    public double BadgeMargin { get => _badgeMargin; set => Set(ref _badgeMargin, Math.Clamp(value, 0, 80)); }
    public double BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, Math.Clamp(value, 0.5, 8)); }
    public VisibilityMode Visibility { get => _visibility; set => Set(ref _visibility, value); }
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Clamp(value, 80, 3000)); }
    public bool HotkeysEnabled { get => _hotkeysEnabled; set => Set(ref _hotkeysEnabled, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string FocusColor { get => _focusColor; set => Set(ref _focusColor, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

    public string GetLabel(int index)
        => Labels.TryGetValue(index.ToString(), out var v) ? v ?? "" : "";

    public void SetLabel(int index, string? value)
    {
        var key = index.ToString();
        if (string.IsNullOrWhiteSpace(value)) Labels.Remove(key);
        else Labels[key] = value.Trim();
        OnPropertyChanged(nameof(Labels));
    }

    // ---- ???濡쒕뱶 ---------------------------------------------------------
    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PaneSticker", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PaneSticker] settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PaneSticker] settings save failed: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

