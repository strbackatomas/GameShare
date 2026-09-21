# Design notes

## Layers kept apart

| Concept | Lives in | What it says | Changes game identity? |
|---|---|---|---|
| GameDefinition | Protocol | Name, version, executable, arguments, working directory, icon, volatile patterns. Optional `gameshare.json` in the game root. | No |
| GameManifest | Protocol, built by Storage | Exact file list with size and SHA-256. Its `ContentHash` is the identity of a version. | Yes |
| Torrent metadata | Torrent | Transport only. Deterministic from the same scan. Never shown to the user. | No, derived |
| Installation | Core, SQLite | Local: install path, which version, installed or damaged, seeding on or off. | - |
| Download | Core, SQLite | Local: an install, repair or update in progress, with resume data. | - |
| Launch | Agent, Client | The agent checks what may be started, the client starts it. Uses Definition, Manifest and Installation. | Local choice of program, per game id |

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

A game is either **Installed** (verified) or **Invalid** (shown as damaged). Both are offered to others: a damaged game still
serves every piece that matches its recorded hash, see "Damaged games keep sharing" below.
Nothing changes that state behind the user's back in the direction of "new version". What happens:

| Situation | What GameShare does |
|---|---|
| A scan finds a content file missing or with another size | Marks the game damaged. It keeps offering the pieces that still match. It does **not** register a new version, or every PC that plays would end up with its own variant. |
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

### Damaged games keep sharing

A game that was played on may have a few changed files, yet most of it is still exactly the original. Hiding it would waste that.
So a damaged game stays seeded, upload-only, and serves only the pieces whose hash still matches (libtorrent checks each piece
before it is served). What that means on the network:

- `/peer/games` marks each offer with `IsComplete` and `PercentIntact`. Offers with nothing intact are left out.
- `/peer/games/{hash}/pieces` returns the piece map, one bit per piece (`PieceMapCodec`). A complete copy answers "everything".
- Every PC keeps the maps of its peers. A game is **fully available** when some PC has a complete copy, or when the union of the
  partial maps covers every piece. Otherwise the client shows "Zatím nekompletní" and how much is covered, and install and update
  are refused with 409 until the missing parts appear. Peers with the same hole cannot complete each other, so nothing starts and stalls.
- Two PCs damaged in different places complete a third one together. Repair works the same way and can use damaged copies as sources.
- A running repair or download of the same folder is never taken over by the seed (`SeedManager.HasActiveDownloadAsync`).

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
- **Suggested automatically.** `GameChangeTracker` watches every installed game folder. Once the folder has been quiet for a moment
  (10 s, then only the touched files are looked at, a size check first and a hash only when the size is the same), a content file
  that really differs marks the game damaged and the game card lists the suggested patterns, without anyone running a check.
  New files the game created are recorded too. Nothing is applied by itself: the user accepts the suggestion, which registers the patterns.
- Suggestions are deliberately narrow. Folders that games are known to write to (`saves`, `cache`, `logs`, `config`, `profile`, ...) become a folder pattern
  at the level where they were found, four or more changed files in one other folder become that folder, anything else stays the one file.
  A folder pattern for `content/` because one file in it changed would hide the game itself.
- Files that are being written by a repair or an update are not the game changing and are ignored. Too many events, or a lost watcher,
  fall back to one full check. A damaged game that keeps changing has its seed re-checked every 5 minutes at most.
- A file that must ship with initial values and is rewritten later is a separate case, left for later. The game can normally create its own defaults.

## Verified games

Identity is the content hash. A manifest is validated and its hash is recomputed from its file list, the torrent must match the manifest, and a finished
download is checked against the manifest's SHA-256 of every file. So "this content hash" means exactly these files, whichever PC delivered them.
The signed list adds the missing piece: *which* content hashes the administrator vouches for.

- **Format.** JSON with a base64 payload, an ECDSA P-256 / SHA-256 signature over the payload bytes as they were serialised (nothing to canonicalise), and a key id.
  The payload has a sequence number, the issue date, an optional `validUntil`, the vouched games (hash, id, name, version) and the revoked hashes with a reason.
  The public key is one line of base64 in the agent settings, the private key stays with the administrator (optionally password protected).
- **A list that does not verify counts as no list.** Wrong key (the message names both key ids), changed payload, garbage, a size over 4 MB, a hash that is not a hash: refused, and the list in use stays.
  Tested by tampering with the payload, by signing with another key under the administrator's key id, and by turning the signature check off to see the tests fail.
- **Rollback.** A validly signed list with a lower sequence than the one held is refused. Equal is ignored. A list past `validUntil` is not used, but it stays held so an older one cannot replace it.
- **Fail closed, but not stuck.** In `Require` mode without a usable list nothing is installed, and the message says why the list did not load. The last good list is stored in the data folder and used after a restart while the source is unreachable.
- **What a verdict means.** Verified: the hash is in the list. Unknown: a list is loaded and it is not there. Revoked wins over everything and is refused in `Warn` as well.
  A game whose files changed after install is not shown as verified, the list vouches for the files of that version and those are no longer on disk. A revocation is still shown.
- **Only installs and updates are checked.** Games that are already installed are not removed, and a PC keeps offering what it has. `Require` decides what a PC installs, not what it serves.
- **The one outbound request.** The list is fetched with a plain GET from a web address or read from a file. It carries nothing about the PC, and the download is capped in size even if the server never says how large it is.

What it does not protect against, so nobody assumes it does: the administrator adding a game from a PC that was already infected, a stolen private key,
a first start of a PC with no stored list where someone serves an old list that was once valid and has no `validUntil`, and a game that a PC modifies after the install (that is the damaged state, not this).

## Launcher

- **The client starts, the agent decides.** The agent is a service in session 0 and cannot show a window on the player's desktop. So `POST /games/{hash}/launch` checks
  everything and returns the program, arguments and working directory, and the client starts that with the shell (so an administrator prompt works).
- **The definition is untrusted.** It arrives from another PC in the manifest. The program must be a `.exe` that is one of the manifest's own files (matched case-insensitively, the manifest's spelling is used),
  the path must stay inside the game folder (a UNC path, a drive letter, `..` and alternate streams are refused), the working directory must be a folder of the game, and the arguments are limited in length with no control characters.
  A file dropped into the game folder later is not in the manifest and is not started.
- **The program that runs is the program that was verified.** Its SHA-256 is compared with the manifest just before it is handed out. Other files may have changed, that is what playing does,
  and a damaged game can be played. A withdrawn version (see Verified games) is never started, and a game that already runs is not started twice.
- **Choosing a program.** A game without `executable` shows the programs of the game (nearest the root first) and the player picks one. It is stored per game id on this PC and checked again against every version.
  A choice that is no longer valid, for example after an update removed the file, falls back to asking.
- **What counts as running.** `RunningGames` looks every 5 s (and on demand before a decision) for a process whose program lives inside a game folder, with a trailing separator so
  `TestGameTwo` is not `TestGame`. A protected system process that cannot be inspected is ignored.
- **While it runs.** Repair, update, register and marking volatile are refused with "The game is running", because they rewrite or re-hash every file. The seed of that game is suspended:
  it does not send, does not re-check (a check is remembered and done when the game closes) and, because a paused seed closes its files, the game can save into them.
  That closes the limit noted under the transfer library for games started on this PC. Tested by mutation: without the re-check guard the test that watches the piece map fails.
- **Commands and their buttons.** The toolkit does not re-evaluate a command when the property behind its CanExecute changes, so the card names the commands to re-evaluate. Before, buttons stayed enabled while a card was busy.

## Speed limits

The library ignores its own global speed limits for peers on the local network, and every peer here is on the local network.
Measured on a 21.5 MB download: no limit 2.1 s, a 2 MB/s global limit 0.5 s, a 2 MB/s limit set on the torrent 12.2 s.
So the total limit is enforced per torrent. The engine divides it every second between the transfers that are actually moving data,
uploads between senders and downloads between unfinished ones, so one busy game gets the whole allowance. A single transfer never
gets less than 16 KiB/s. Tests measure real transfer rates, because storing the setting proved nothing.

## Agent

One process, two HTTP listeners with different trust, plus discovery and the transfer port. See the README for the port table.

- The control API and the event hub are bound to loopback and refuse any request that arrived on another port.
- The peer API answers only private addresses, offers only games that are installed here and seeding (a damaged one with the pieces that are still intact), and never reveals paths.
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

- **Served files cannot be rewritten by the game.** On Windows the library memory-maps the files it reads. A program that replaces such a file by
  truncating it, which is how most games save their settings (`File.WriteAllText`, `fopen("w")`), gets "the requested operation cannot be performed on a file
  with a user-mapped section open". Measured: while a seed checked its files, while it uploaded, and afterwards, every served file refused it.
  Disk I/O modes and the mapping cutoff changed nothing. What helps: the file pool is limited to 8 files (a transfer of 3000 small files was not slower
  with 2, 4 or 16 files than with the default), and a seed that uploaded nothing for 20 s is paused and resumed one tick later, which closes its files.
  Resuming does not re-check it and it keeps seeding, tested by installing from it afterwards. While a peer is downloading a game nobody plays here, its files being served are
  still open; a game that is played is different, see Launcher: its seed is stopped. A game started outside GameShare and not recognised as running has the old limit. Agent settings: `OpenFileLimit`, `SeedIdleRelease`.
  The upload rate the library reports is a moving average that stays above zero long after the last byte, so idleness is judged by bytes uploaded.
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
- **Repair needs PCs that have the missing parts.** With no source it waits and can be cancelled. The game stays damaged.
- **Discovery and the transfer engine do not talk to each other.** Each finds peers on its own multicast. The shorter announce interval makes a lost datagram cost seconds, not minutes, but it is not a real nudge.
- **IPv4 only.** Keeps the LAN filter simple.
- **Games that start through another program.** A game is running when a program from its folder runs. One that hands over to a store client is not seen, so its seed keeps sending and a repair is not held back.
- **Discovery source address.** A PC with two adapters on the same LAN would be seen from two addresses and reported as changing address. Rare, not handled.
- **No pause state in the binding.** `TorrentTransfer.IsStopped` is tracked by us. Real transfer errors, such as a full disk, are not reported by the status either, a download would just stall.
- **Hashing is single threaded.** One pass computes SHA-256 and SHA-1 together. Fine to start, optimise if scanning a 100 GB game is too slow.
- **The client has no per-download detail beyond the list**, and no way to choose the target folder per install, it uses the first game folder.
