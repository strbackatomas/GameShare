using System.Text.Json.Serialization;
using GameShare.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GameShare.Client.Services;

/// <summary>Receives the agent's live events over SignalR and keeps the connection alive across agent restarts.</summary>
public sealed class AgentEventStream : IEventStream
{
    /// <summary>Try again every three seconds, forever. The agent is a service that may start after the GUI.</summary>
    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(3);
    }

    private readonly HubConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private Task? _connecting;

    public AgentEventStream(Uri agentBaseAddress)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(agentBaseAddress, GameShareEvents.HubPath))
            .WithAutomaticReconnect(new RetryForever())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        Listen<PeerDto>(GameShareEvents.PeerConnected);
        Listen<PeerDto>(GameShareEvents.PeerDisconnected);
        Listen<GameDto>(GameShareEvents.GameDiscovered);
        Listen<GameDto>(GameShareEvents.GameUpdated);
        Listen<GameDto>(GameShareEvents.GameRemoved);
        Listen<DownloadDto>(GameShareEvents.DownloadStarted);
        Listen<DownloadDto>(GameShareEvents.DownloadProgress);
        Listen<DownloadDto>(GameShareEvents.DownloadPaused);
        Listen<DownloadDto>(GameShareEvents.DownloadCompleted);
        Listen<DownloadDto>(GameShareEvents.DownloadFailed);
        Listen<DownloadDto>(GameShareEvents.DownloadCancelled);
        Listen<SeedDto>(GameShareEvents.SeedStarted);
        Listen<SeedDto>(GameShareEvents.SeedStopped);

        _connection.Reconnecting += _ => { ConnectionChanged?.Invoke(this, false); return Task.CompletedTask; };
        _connection.Reconnected += _ => { ConnectionChanged?.Invoke(this, true); return Task.CompletedTask; };
        _connection.Closed += _ => { ConnectionChanged?.Invoke(this, false); return Task.CompletedTask; };
    }

    public event EventHandler<AgentEvent>? Received;
    public event EventHandler<bool>? ConnectionChanged;

    private void Listen<T>(string name) where T : notnull =>
        _connection.On<T>(name, payload => Received?.Invoke(this, new AgentEvent(name, payload)));

    public Task StartAsync(CancellationToken ct = default)
    {
        // The first connection can fail because the agent is not up yet. Automatic reconnect only covers a connection that
        // existed, so the first attempt is retried here.
        _connecting = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await _connection.StartAsync(linked.Token).ConfigureAwait(false);
                    ConnectionChanged?.Invoke(this, true);
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    ConnectionChanged?.Invoke(this, false);
                    try { await Task.Delay(TimeSpan.FromSeconds(3), linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_connecting is not null) await _connecting.ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
