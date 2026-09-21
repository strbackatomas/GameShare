# GameShare

Shares game files between PCs on one LAN over BitTorrent, without a server. A game that is on one PC can be installed
on every other PC, and each PC that finishes becomes another source. Files are written straight into the target folder,
with no ZIP step.

Status: agent and desktop client work and are tested on one machine. Not yet tried on real hardware, see the end.

## Layout

| Project | Role |
|---|---|
| `GameShare.Protocol` | Shared contracts: game definition, manifest, API and event DTOs |
| `GameShare.Storage` | Scanning, hashing, manifests, volatile patterns, verification |
| `GameShare.Torrent` | The transfer engine over libtorrent, and the `.torrent` builder |
| `GameShare.Discovery` | UDP discovery of other agents on the LAN |
| `GameShare.Core` | SQLite, game library, seeding, install, repair and update |
| `GameShare.Agent` | The Windows service: local API, peer API, SignalR events |
| `GameShare.Client` | The desktop app (Avalonia). Talks only to the local agent |

More detail is in `docs/design-notes.md`. The choice of libtorrent binding is in `docs/binding-evaluation.md`.

## Build and test

Needs the .NET 10 SDK.

```
dotnet build
dotnet test
```

The integration tests start real agents that discover each other over real sockets. They run one at a time on purpose,
so the whole run takes several minutes.

## Run for development

```
dotnet run --project src\GameShare.Agent
dotnet run --project src\GameShare.Client
```

Ports and folders come from `appsettings.json` or environment variables, for example `Agent__DataDir` and `Agent__LocalApiPort`.
Data and logs default to `%ProgramData%\GameShare`. The client finds the agent on `127.0.0.1:47701`, or at `GAMESHARE_AGENT_URL`.

## Install on a PC

```
scripts\publish.ps1                                # builds artifacts\agent and artifacts\client, self-contained
scripts\install-agent.ps1 -GameRoots D:\Games      # elevated PowerShell, run on each PC
```

The published folders include the .NET runtime, nothing has to be installed on the PCs first. The agent is about 120 MB,
the client about 110 MB. The script installs the agent as a Windows service, opens only the ports it needs on the Private and
Domain profiles, and adds a Start menu entry for the client. It has not been run yet, see the end.

## Ports and trust

| Port | Listens on | Purpose | Who may connect |
|---|---|---|---|
| 47701 | 127.0.0.1 | Control API and event hub for the client | This machine only |
| 47702 | all adapters | Read-only API: what this PC offers, manifests, torrents | Private network addresses only |
| 47800 | UDP, LAN | Discovery | LAN |
| 6881 | all adapters | Game data | Private network addresses only |

The two HTTP ports are separate listeners. A request is judged by the socket it arrived on, never by its Host header.
Game data and API calls never go to a public address, DHT and port mapping are off, and nothing is forwarded.

## Local API

`GET /api/status`, `GET|PUT /api/settings`, `GET /api/peers`, `GET /api/games`, `GET /api/games/{contentHash}`,
`POST /api/games/scan`, `POST /api/games/{contentHash}/install`, `.../update`, `.../repair`, `.../check`, `.../register`, `.../volatile`,
`GET /api/downloads`, `GET /api/downloads/{id}`, `POST /api/downloads/{id}/pause`, `.../resume`, `DELETE /api/downloads/{id}?deleteFiles=false`.

Live events arrive on the SignalR hub at `/hub/events`: `PeerConnected`, `PeerDisconnected`, `GameDiscovered`, `GameUpdated`,
`GameRemoved`, `DownloadStarted`, `DownloadProgress`, `DownloadPaused`, `DownloadCompleted`, `DownloadFailed`, `DownloadCancelled`,
`SeedStarted`, `SeedStopped`. Payload types are documented in `GameShare.Protocol/ApiDtos.cs`.

A game is identified by its content hash, not by the name of its folder.

## Games that change their own folder

Saves, settings and caches inside a game folder must not count as game content. List them in `gameshare.json` in the game folder:

```json
{ "gameId": "beamng", "name": "BeamNG.drive", "version": "0.38", "volatile": ["saves/**", "*.ini"] }
```

Or let the client find them: **Zkontrolovat** on a game shows which files changed since install and offers to mark them.
A game whose files changed is shown as damaged and is not offered to others until it is repaired or registered again.

## Not done yet, and not tried

- **The launcher.** Nothing starts games and there is no Play button. The models are prepared.
- **Real hardware.** Everything ran on one machine. Several PCs, real switches and firewalls, multicast on your network
  and speed on 2.5 or 10 GbE are untested.
- **The service installer.** The scripts were syntax checked but never run, they need an elevated session.
- **The client as a Windows app on another PC.** It ran here, headless and as a real window, not on a second machine.
