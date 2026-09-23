<p align="center"><img src="src/GameShare.Client/Assets/logo.png" alt="LANka.seru.cz" width="200"></p>

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
| `GameShare.Standalone` | Agent and client bundled into one portable exe, no service install, for a LAN-party guest |
| `GameShare.Admin` | `gameshare-admin`, the command-line administrator's tool for the signed list of verified games. Not installed on the players' PCs |
| `GameShare.AdminGui` | The same tool with a window instead of a command line. Also administrator-only |

More detail is in `docs/design-notes.md`. The choice of libtorrent binding is in `docs/binding-evaluation.md`.

## Build and test

Needs the .NET 10 SDK.

```
dotnet build
dotnet test -m:1
```

The integration tests start real agents that discover each other over real sockets. They run one at a time on purpose,
so the whole run takes several minutes. `-m:1` matters: without it `dotnet test` runs test projects side by side,
and two projects hosting libtorrent sessions at once can crash each other's test host. Each project passes when run alone.

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

`publish.ps1` builds each app twice: `artifacts\agent` and `artifacts\client` are self-contained (about 120 MB and 110 MB),
nothing has to be installed on the PC first, just copy the folder. `artifacts\agent-net10` and `artifacts\client-net10` are the
same apps without the runtime bundled in (about 13 MB and 31 MB), for a PC that already has the ASP.NET Core Runtime 10.0 (x64)
installed (https://dotnet.microsoft.com/download/dotnet/10.0, one installer covers both). `install-agent.ps1` uses the
self-contained folders by default; pass `-SourceDir artifacts\agent-net10 -ClientSourceDir artifacts\client-net10` to use the
smaller ones instead. The script installs the agent as a Windows service, opens only the ports it needs on the Private and
Domain profiles, and adds a Start menu entry for the client. It has not been run yet, see the end.

## Verzování

One version number for everything: agent, client, both admin tools and the standalone build. It is set once,
in `<Version>` in `src\Directory.Build.props`, and follows [Semantic Versioning](https://semver.org/). Every
build picks it up automatically (assembly version, file version, the `.exe` properties in Windows Explorer).

- `GET /api/status` and the client's Settings page show the agent's and the client's own version, so a PC where
  only one of the two was updated is easy to notice.
- Each agent puts its version in its discovery hellos. It is informational only, shown next to a PC in the
  Network view (with a small warning badge on a mismatch) — it plays no part in deciding whether a peer is
  understood, that is still the separate discovery protocol version (`DiscoveryMessage.Version`), which is
  checked strictly and always has (see "Mixed versions" in `docs/design-notes.md`).
- `gameshare-admin --version` prints the CLI's build; `gameshare-admin-gui` shows it next to its title.
- `scripts\publish.ps1` prints the version it is building at the start of the run.

Bump the version and add an entry to `CHANGELOG.md` in the same change, then tag the commit once it is released:

```
git tag v0.1.0
git push origin v0.1.0
```

## Just visiting? `GameShare-LanParty.exe`

`publish.ps1` also builds `artifacts\standalone\GameShare-LanParty.exe`: agent and client in one file, self-contained,
no service, no admin rights. Hand it to a LAN-party guest and they double-click it. It reuses the same ports as the
installed product (below) and, if this PC already runs the real agent, quietly becomes a thin client of it instead of
starting its own. See "Standalone (LAN party) build" in `docs/design-notes.md`.

## Ports and trust

| Port | Listens on | Purpose | Who may connect |
|---|---|---|---|
| 47701 | 127.0.0.1 | Control API and event hub for the client | This machine only |
| 47702 | all adapters | Read-only API: what this PC offers, manifests, torrents | Private network addresses only |
| 47800 | UDP, LAN | Discovery | LAN |
| 6881 | all adapters | Game data | Private network addresses only |

The two HTTP ports are separate listeners. A request is judged by the socket it arrived on, never by its Host header.
Game data and API calls never go to a public address, DHT and port mapping are off, and nothing is forwarded.
The one request that may leave the LAN is the optional download of the administrator's signed list (below). It sends nothing about the PC.

## Local API

`GET /api/status`, `GET|PUT /api/settings`, `GET /api/peers`, `GET /api/games`, `GET /api/games/{contentHash}`,
`GET /api/trust`, `POST /api/trust/refresh`, `POST /api/games/{contentHash}/launch`, `GET .../executables`, `PUT .../launcher`, `POST /api/games/scan`, `POST /api/games/{contentHash}/install`, `.../update`, `.../repair`, `.../check`, `.../register`, `.../volatile`,
`GET /api/downloads`, `GET /api/downloads/{id}`, `POST /api/downloads/{id}/pause`, `.../resume`, `DELETE /api/downloads/{id}?deleteFiles=false`.

Live events arrive on the SignalR hub at `/hub/events`: `PeerConnected`, `PeerDisconnected`, `GameDiscovered`, `GameUpdated`,
`GameRemoved`, `DownloadStarted`, `DownloadProgress`, `DownloadPaused`, `DownloadCompleted`, `DownloadFailed`, `DownloadCancelled`,
`SeedStarted`, `SeedStopped`. Payload types are documented in `GameShare.Protocol/ApiDtos.cs`.

A game is identified by its content hash, not by the name of its folder.

## Games that change their own folder

Saves, settings and caches inside a game folder must not count as game content. List them in `gameshare.json` in the game folder:

```json
{ "gameId": "beamng", "name": "BeamNG.drive", "version": "0.38", "executable": "Bin64/BeamNG.drive.x64.exe", "volatile": ["saves/**", "*.ini"] }
```

Or let GameShare find them. While a game is played the agent notices which of its files are rewritten and the game shows as changed,
with suggested patterns and a button that marks them. **Zkontrolovat** does the same on demand for every file. Nothing is marked without a click.
A game whose files changed is shown as damaged until it is repaired or registered again. It still offers the parts that are unchanged,
so other PCs can use it as a source, and PCs that were played on differently can complete each other.

## Playing

A game that says which program starts it (`executable` in `gameshare.json`, a `.exe` that is one of the game's own files) has a **Hrát** button.
For a game that does not say, the client lists the programs of the game and the player picks one, which is remembered on that PC.

- The agent is a Windows service without a desktop, so it cannot start a game the player can see. It checks, and the client starts.
  The agent refuses when the program is not one of the game's files, when it is not the file that was verified (a game changing its settings is
  fine, its program changing is not), when the administrator withdrew that version, or when the game is already running.
- A game is recognised as running when a program from its folder runs, however it was started. A game that starts its real program from
  somewhere else, such as a store client, is not recognised.
- While a game runs, a repair, an update and registering wait, and its seed stops sending. Files the seed holds open would keep the game from saving,
  and the disk and network are the game's. Other PCs cannot install that game from this PC until it is closed (`Agent:PauseSeedWhilePlaying`).

## Verified games (optional)

Content is identified by a hash of every file, so a PC that hands out a modified game hands out a game with a different hash.
What the hash cannot say is which version is the one you meant. The administrator can publish a **signed list** of the content hashes
they vouch for, and every PC checks games against it. Nobody has to trust the PC a game came from.

On the administrator's PC, either the command line or the graphical tool, whichever is easier. Both call the same code
(`GameShare.Storage/TrustWorkflow.cs`), so they behave the same and either can carry on where the other left off on the
same key and list files.

Command line (`scripts\publish.ps1` builds `artifacts\admin\gameshare-admin.exe`):

```
gameshare-admin keygen --out C:\keys                       # once. Keep trust-private.key safe, and off the players' PCs
gameshare-admin add D:\Games\BeamNG --key C:\keys\trust-private.key --list trust.json   # a clean install, as it is on the reference PC
gameshare-admin revoke <content hash> --reason "modified executable" --key ... --list trust.json
gameshare-admin show --list trust.json --pub C:\keys\trust-public.key
```

Graphical (`artifacts\admin-gui\gameshare-admin-gui.exe`): a window with a key section, a list section, a folder picker
to scan and add a game, and a card per verified and per revoked version with Zrušit/Odebrat buttons. Every change signs
and writes the list at once, there is no separate save step. It remembers the last key and list file used (not the
password) in the administrator's own profile, never on a share.

Put `trust.json` on an https address or a share, and install the agents with the public key:

```
scripts\install-agent.ps1 -GameRoots D:\Games -TrustMode Warn -TrustListSource https://example.org/trust.json -TrustPublicKey <key>
```

- `Off` ignores the list. `Warn` marks games as verified or not and installs anything but a revoked version. `Require` installs only verified games,
  and nothing when no list can be loaded, because "cannot check" must not mean "allowed".
- The settings live in `appsettings.json` (`Agent:TrustMode`, `Agent:TrustListSource`, `Agent:TrustPublicKey`), not in the client, so a player cannot switch the check off from the app.
- The client shows a badge on each game, and the settings page shows which list is in use and why a refresh failed.
- A list is used only when its signature verifies against the public key. The last good list is kept on disk, so a server that is down never turns checking off.
  A list older than the one a PC already has is refused, so an old list cannot be replayed to hide a revocation. `--valid-days` makes a list expire.

## Not done yet, and not tried

- **Starting a real game.** The launcher was tested by starting a program that lives in a game folder, not with a real game. A game that
  starts its real program from a store client, or one that needs the administrator prompt, is untested.
- **Real hardware.** Everything ran on one machine. Several PCs, real switches and firewalls, multicast on your network
  and speed on 2.5 or 10 GbE are untested.
- **The service installer.** The scripts were syntax checked but never run, they need an elevated session.
- **The signed list on real infrastructure.** It was tested with files and real agents, not with an https server, a share, or a PC that is offline for days.
- **`gameshare-admin-gui`.** Its logic is tested (17 tests, with mutation checks), and the published exe was run and screenshotted once. Not tried on a second machine,
  and dialog cancel/error paths were exercised through fakes, not by actually clicking Cancel in the real Windows dialogs.
- **The client as a Windows app on another PC.** It ran here, headless and as a real window, not on a second machine.
