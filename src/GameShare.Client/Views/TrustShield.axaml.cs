using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GameShare.Protocol;

namespace GameShare.Client.Views;

/// <summary>
/// The administrator's verdict on a game as a shield, the usual sign for "safe": a green one with a tick for a verified version,
/// an orange one with "!" for a version nobody vouches for, a red one with a cross for a withdrawn one. Nothing when checking is off.
/// </summary>
public partial class TrustShield : UserControl
{
    public static readonly StyledProperty<TrustVerdict> VerdictProperty =
        AvaloniaProperty.Register<TrustShield, TrustVerdict>(nameof(Verdict));

    public TrustVerdict Verdict
    {
        get => GetValue(VerdictProperty);
        set => SetValue(VerdictProperty, value);
    }

    public TrustShield()
    {
        InitializeComponent();
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VerdictProperty) Apply();
    }

    private void Apply()
    {
        var (brush, mark) = Verdict switch
        {
            TrustVerdict.Verified => ("SuccessBrush", "M8,12 L11,15 L16.5,9"),
            TrustVerdict.Unknown => ("WarnBrush", "M12,6.8 V12.6 M12,16.6 V16.7"),
            TrustVerdict.Revoked => ("DangerBrush", "M9,9 L15,15 M15,9 L9,15"),
            _ => ((string?)null, (string?)null),
        };
        IsVisible = brush is not null;
        if (brush is null) return;
        // The app's colours, looked up there: when this is created it is not in the window yet, so it cannot find them through it.
        Shield.Fill = Application.Current?.TryFindResource(brush, out var b) == true ? b as IBrush : Brushes.Gray;
        Mark.Data = Geometry.Parse(mark!);
    }
}
