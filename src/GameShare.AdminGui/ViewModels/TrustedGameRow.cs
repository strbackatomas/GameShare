using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameShare.Storage;

namespace GameShare.AdminGui.ViewModels;

/// <summary>One vouched-for game version, with the actions the administrator can take on it.</summary>
public sealed partial class TrustedGameRow : ObservableObject
{
    private readonly TrustedGame _game;
    private readonly Func<TrustedGame, string, Task> _revoke;
    private readonly Func<TrustedGame, Task> _remove;

    public TrustedGameRow(TrustedGame game, Func<TrustedGame, string, Task> revoke, Func<TrustedGame, Task> remove)
    {
        _game = game;
        _revoke = revoke;
        _remove = remove;
    }

    public string Name => _game.Name;
    public string? Version => _game.Version;
    public string ContentHash => _game.ContentHash;
    public string ShortHash => _game.ContentHash[..12];

    /// <summary>Whether the list also vouches for the game's gameshare.json, which says how it starts and what preparing a PC runs.</summary>
    public string DefinitionText => _game.DefinitionHash is { } hash
        ? $"Definice podepsána ({hash[..12]})"
        : "Bez podepsané definice: příprava hry a spuštění jako správce se na PC s povinným ověřením nepovolí";

    /// <summary>The reason field for this row is open.</summary>
    [ObservableProperty] public partial bool IsRevoking { get; set; }

    [ObservableProperty] public partial string Reason { get; set; } = "";

    [RelayCommand]
    private void StartRevoke() => IsRevoking = true;

    [RelayCommand]
    private void CancelRevoke()
    {
        IsRevoking = false;
        Reason = "";
    }

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        var reason = Reason.Trim();
        if (reason.Length == 0) return;
        await _revoke(_game, reason).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveAsync() => await _remove(_game).ConfigureAwait(true);
}

/// <summary>One withdrawn version, kept in the list only as a record of why.</summary>
public sealed partial class RevokedGameRow : ObservableObject
{
    private readonly RevokedGame _revoked;
    private readonly Func<RevokedGame, Task> _forget;

    public RevokedGameRow(RevokedGame revoked, Func<RevokedGame, Task> forget)
    {
        _revoked = revoked;
        _forget = forget;
    }

    public string ContentHash => _revoked.ContentHash;
    public string ShortHash => _revoked.ContentHash[..12];
    public string Reason => _revoked.Reason;

    [RelayCommand]
    private async Task ForgetAsync() => await _forget(_revoked).ConfigureAwait(true);
}
