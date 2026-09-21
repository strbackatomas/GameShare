# Design notes

## Layers kept apart

| Concept | Lives in | What it says | Changes game identity? |
|---|---|---|---|
| GameDefinition | Protocol | Name, version, executable, arguments, working directory, icon, volatile patterns. Optional `gameshare.json` in the game root. | No |
| GameManifest | Protocol, built by Storage | Exact file list with size and SHA-256. Its `ContentHash` is the identity of a version. | Yes |
| Torrent metadata | Torrent | Transport only. Deterministic from the same scan. Never shown to the user. | No, derived |
| Installation | Core, SQLite | Local: install path, which version, installed or damaged, seeding on or off. | - |
| Download | Core, SQLite | Local: an install, repair or update in progress, with resume data. | - |
| Launch | future launcher | Uses Definition and Installation. | - |

`InstallPath` is deliberately not part of GameDefinition, because it differs on every PC.
`gameshare.json` is excluded from the content scan, so editing launch settings never creates a new version.

## Chunking decision

Fixed-size pieces, 1 to 16 MiB, chosen so a game has about two thousand pieces. Reasons:

- It is what BitTorrent transports and verifies, so parallel multi-peer download, instant re-seeding of finished pieces and per-piece resume come for free.
- File-based chunking was rejected. A single 50 GB archive could not be fetched from several peers or resumed mid-file.
- Content-defined chunking was rejected for now. It only wins when bytes are inserted in the middle of a huge file, which is rare for game archives, and BitTorrent cannot carry it. It would mean writing our own transfer protocol.

Updates need no special mechanism. Attaching the next version's torrent over an installed older version
re-hashes the existing files and keeps every piece that still matches. Measured on a 21.5 MB synthetic game with 1 MiB pieces:

| Change in version 2 | Fetched |
|---|---|
| One byte flipped inside a large file | 1 piece |
| New small file that sorts before all others (shifts the layout) | 1 piece |

These are tests, so they keep guarding the behaviour. Expect real games to fetch pieces of up to 16 MiB per touched region.
An insertion or resize inside a large file changes everything after it in that file, which is the one case CDC would help.

## Data model (SQLite, one file per PC)

| Table | Holds |
|---|---|
| manifests | One row per game version, keyed by ContentHash: the manifest JSON and the torrent bytes. |
| installations | Complete games on this PC: path, installed or invalid, seeding on or off, resume data for the seed. Path is unique, ignoring case. |
| downloads | Installs, repairs and updates: kind, state, error, resume data, bytes done, the installation it belongs to. At most one unfinished download per version. |
| settings | Key and value. |

Not stored, on purpose. Per-file and per-chunk rows would duplicate the manifest and torrent, which are already
hash-checked. Peers live in memory in discovery. There is no separate Games table, a game is the set of manifests
sharing a GameId. The schema is versioned with `user_version`, a database from a newer build is refused with a message.

## Install workflow

`DownloadManager.StartInstallAsync` takes a manifest and torrent that came from another PC and checks, before writing anything:
manifest validity, not already installed, target folder empty or a retry of the same version, enough free disk space,
and that the torrent really belongs to the manifest (info hash, name, size). Then it downloads into the final folder.
On completion a full manifest verification runs off the timer loop. Only a passing result creates an installation
and starts seeding. A failing result marks the download failed, removes the transfer so nothing corrupt is served,
and a retry repairs the folder by re-checking it. A folder that a download is writing into is skipped by library scans.

## Looking after an installed game

A game is either **Installed** (verified, offered to others) or **Invalid** (shown as damaged, not offered).
Nothing changes that state behind the user's back in the direction of "new version". What happens:

| Situation | What GameShare does |
|---|---|
| A scan finds a content file missing or with another size | Marks the game damaged and stops offering it. It does **not** register a new version, or every PC that plays would end up with its own variant. |
| A game rewrote a file and kept its size | A scan cannot see it. **Check** (full verification) reports it, marks the game damaged and suggests volatile patterns. |
| A damaged game looks fine again | A scan runs a full check before putting it back in service. Sizes alone prove nothing. |
| An installed folder is gone, for example an unplugged drive | Marked damaged. Comes back after a full check when the folder returns. |
| The admin patched a game on purpose | **Register** takes the current files as the game's new version and replaces the old record. |
| Files a game writes itself | **Volatile** patterns, below. |
| Files were damaged | **Repair** fetches the damaged pieces from other PCs and puts the game back in service. |
| A newer version is offered on the LAN | **Update** fetches only the pieces that differ, keeps volatile files, and removes files the old version had and the new one does not, but only when they are still exactly what the old manifest says. |

Repair and update take the game out of service first, so nobody is handed files while they are rewritten.
Repair is the only thing that overwrites changed content files, and it is always an explicit request.
Cancelling a repair or update never deletes an installed game, whatever it is asked to do.

### Volatile files

Many games write into their folder: settings, saves, shader caches, logs. Those must not count as game content.

- A list of path patterns, in `gameshare.json` as `volatile`, or added later through the API. `saves/**`, `*.ini`, `shadercache/`.
  `*` stays in one folder, `**` crosses folders, a pattern without a slash matches at any depth, a trailing slash means the whole folder, case is ignored.
- Matching files are left out of the manifest and the torrent, so they are never shared, verified or overwritten.
- A short built-in default list: `*.log`, `*.tmp`, `*.dmp`, `Thumbs.db`. Deliberately short, a wrong entry would hide real content.
- The effective patterns are recorded in the manifest, so a PC that received the game applies the same rules when it looks at its folder again, without any `gameshare.json`.
- Only the resulting file list is identity. Two PCs with different pattern lists agree when the remaining files are the same, and they share one swarm.
- Patterns only accumulate. A file once excluded never re-enters the content, which would flip the game's identity.
- Unsafe patterns are refused with a message: empty, `..`, drive letters, and anything that would match every file.
- A file that must ship with initial values and is rewritten later is a separate case, left for later. The game can normally create its own defaults.

## Speed limits

The library ignores its own global speed limits for peers on the local network, and every peer here is on the local network.
Measured on a 21.5 MB download: no limit 2.1 s, a 2 MB/s global limit 0.5 s, a 2 MB/s limit set on the torrent 12.2 s.
So the total limit is enforced per torrent. The engine divides it every second between the transfers that are actually moving data,
uploads between senders and downloads between unfinished ones, so one busy game gets the whole allowance. A single transfer never
gets less than 16 KiB/s. Tests measure real transfer rates, because storing the setting proved nothing.

## Agent

One process, two HTTP listeners with different trust, plus discovery and the transfer port. See the README for the port table.

- The control API and the event hub are bound to loopback and refuse any request that arrived on another port.
- The peer API answers only private addresses, offers only games that are installed, verified and seeding, and never reveals paths.
- Everything a peer sends is validated before use: offer lists, manifests and torrents have size limits, hashes and paths are checked, and a manifest must match its torrent before a byte is written.
- The GUI may only install into folders that are configured in the settings.
- Events reach the GUI through one ordered queue, so a slow client cannot block downloads or discovery.
- A game that disappears from the LAN is announced with `GameRemoved` and its last known state.
- Seeds store resume data every few minutes and at shutdown, so a restart does not re-hash the whole library. Measured: a seed with resume data goes straight to seeding, without checking files.

## Client

An Avalonia desktop app. It holds no BitTorrent logic, it only calls the agent's local API and listens to its events.

- `AppModel` is the client's picture of the agent: games, downloads, peers. It is loaded once and kept current by events. After every reconnect everything is loaded again, so a missed event cannot linger.
- View models depend on three small interfaces, the agent client, the event stream and a UI dispatcher, so they are tested without a display.
- The agent's own explanation of a failure is what the user sees. An unreachable agent shows a banner and keeps the last known state on screen.
- Tests cover the view models with a fake agent, the HTTP client with a fake server, the whole client against real agents talking to each other, and a headless render of every page to a PNG. The images were looked at, and three defects found that way were fixed.

## Things the transfer library does that the code has to respect

Found by tests that failed or by measuring, kept here so nobody rediscovers them.

- **Removal is asynchronous.** Adding the same torrent right after removing it fails with "already attached". `RemoveAsync` waits for the library's notification and `AddAsync` retries briefly if it is late.
- **Saving resume data can hang.** While a torrent is checking or being removed the library may never answer. Every save has a timeout, a failed save never fails a pause or a shutdown, and the next start simply re-checks the files. A save that is still in flight when a torrent is removed keeps the library holding on to it, so saves and removal are ordered per download.
- **A seed repairs what it sees as damaged.** Left alone, a seed downloads the original of any file a game changed and overwrites it. Seeds are upload-only.
- **Global speed limits do nothing on the LAN.** See Speed limits.
- **Local discovery announces each torrent once per five minutes.** One lost multicast datagram, easy on Wi-Fi, left a new download without peers. The interval is 30 seconds.
- **State names in change notifications are wrong.** The binding puts libtorrent's numbers into its own enum in another order, so checking resume data shows up as "Errored". Notifications are named from the numbers. Polling the status is correct.
- **Several sessions in one process disturb each other.** Tests that share game content find each other's peers through local multicast and add load, which made timing based tests fail. Integration tests run one at a time. A real install has one session per PC.

## Planned: self-update of GameShare itself

The agent and client can be distributed as one more package with its own manifest and torrent, so they spread
over the LAN swarm and update by fetching only changed pieces. Not built yet. Two things differ from games:

- **Files in use.** The package is downloaded to a staging folder and verified with the manifest. A small separate
  updater then stops the service, swaps the files and starts it again. The running agent never overwrites itself.
- **Signature required.** The agent runs as a service with high privileges. An unsigned update channel on the LAN
  would let any machine on it run code everywhere. The manifest of an update is signed with an admin key and the
  agent accepts only packages that verify against a public key built into it.
- **Mixed versions.** During a rollout old and new agents coexist, so the discovery protocol version is checked
  and unknown versions are ignored rather than crashing.

## Known limits and open decisions

- **Integrity strength.** Pieces are SHA-1, which is fine against accidents on a trusted LAN. The manifest adds SHA-256 per file and a full check re-verifies it. A game is only "installed" when a full verification passed.
- **Volatile patterns cannot be removed.** They only accumulate. Deleting the game's record is the way back.
- **Repair needs another PC that has the game.** With no source it waits and can be cancelled. The game stays damaged.
- **Discovery and the transfer engine do not talk to each other.** Each finds peers on its own multicast. The shorter announce interval makes a lost datagram cost seconds, not minutes, but it is not a real nudge.
- **IPv4 only.** Keeps the LAN filter simple.
- **No launcher.** Nothing starts games. The definition file has the fields, the client has no Play button.
- **Seeding while someone plays.** Seeds read the game folder from disk. A "seed while gaming" switch needs to know whether a game runs, which belongs with the launcher.
- **Discovery source address.** A PC with two adapters on the same LAN would be seen from two addresses and reported as changing address. Rare, not handled.
- **No pause state in the binding.** `TorrentTransfer.IsStopped` is tracked by us. Real transfer errors, such as a full disk, are not reported by the status either, a download would just stall.
- **Hashing is single threaded.** One pass computes SHA-256 and SHA-1 together. Fine to start, optimise if scanning a 100 GB game is too slow.
- **The client has no per-download detail beyond the list**, and no way to choose the target folder per install, it uses the first game folder.
