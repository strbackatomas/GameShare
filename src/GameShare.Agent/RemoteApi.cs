using GameShare.Protocol;

namespace GameShare.Agent;

/// <summary>
/// Remote management in the control API, so only for this PC's own client, see <see cref="AccessGuard"/>.
/// Turning it on and pairing are done at the PC that is to be managed; the PC that manages enters the code and then reaches the
/// target through /api/remote/targets/{id}/api/..., which takes the same paths as this API.
/// </summary>
public static class RemoteApi
{
    public static void Map(WebApplication app)
    {
        var remote = app.MapGroup("/api/remote");

        remote.MapGet("", (RemoteAccessService service) => service.Status());

        // ---- this PC as a target ----

        remote.MapPut("/enabled", async (RemoteEnableRequest request, RemoteAccessService service, CancellationToken ct) =>
            await service.SetEnabledAsync(request.Enabled, ct));

        remote.MapPost("/pairing", (RemoteAccessService service) => service.StartPairing());

        remote.MapDelete("/pairing", (RemoteAccessService service) =>
        {
            service.CancelPairing();
            return Results.NoContent();
        });

        remote.MapDelete("/controllers/{machineId}", async (string machineId, RemoteAccessService service, CancellationToken ct) =>
        {
            await service.RemoveControllerAsync(machineId, ct);
            return Results.NoContent();
        });

        // ---- this PC as a controller ----

        remote.MapPost("/targets", async (RemotePairRequest request, RemoteAccessService service, CancellationToken ct) =>
            await service.PairAsync(request.MachineId ?? "", request.Code ?? "", ct));

        remote.MapDelete("/targets/{machineId}", async (string machineId, RemoteAccessService service, CancellationToken ct) =>
        {
            await service.RemoveTargetAsync(machineId, ct);
            return Results.NoContent();
        });

        remote.MapMethods("/targets/{machineId}/api/{**rest}", [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete],
            (HttpContext context, string machineId, string? rest, RemoteAccessService service) =>
                service.ForwardToTargetAsync(context, machineId, rest ?? ""));
    }
}
