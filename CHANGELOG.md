# Changelog

All notable changes to GameShare are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/): MAJOR.MINOR.PATCH, where MAJOR changes mean "not compatible with
older agents on the same LAN", MINOR adds a feature, PATCH fixes a bug. See "Verzování" in README.md for how
this is wired into the build and where a peer's version shows up.

## [Unreleased]

### Added
- The Síť page shows what is being played on each PC ("▶ hraje Half-Life 2"). Agents announce it in their discovery hello, at
  once when a game starts or stops; a PC running an older version simply shows nothing.

### Changed
- "Kontrola sítě" folds to its one-line summary when everything is fine, and unfolds by itself when something is wrong.
  A click on the summary opens or folds it.

### Fixed
- A game that was already on the PC and was found by a scan, without its gameshare.json, now takes its definition (what starts it,
  its icon, how it is prepared) from a PC on the LAN with the same files, right after the scan. Before, it stayed without one: no
  program to start and no icon. When the administrator signed a definition for that version, only the signed one is taken.

## [0.10.0] - 2026-10-04

### Added
- Backup of the pairings on the "Vzdálená správa" page: "Uložit zálohu…" saves this PC's identity, certificate and pairings to a
  file encrypted with a password; after Windows is reinstalled, "Obnovit ze zálohy…" puts them back and the agent restarts, so
  neither this PC nor the ones paired with it need pairing again. Refused while the original PC is still on the network.

## [0.9.0] - 2026-10-04

### Added
- "Kontrola sítě" on the Síť page: the networks this PC is on and whether Windows has them as private, domain or public (GameShare's
  firewall rules only cover private and domain); for each of GameShare's ports whether the agent listens there and whether the
  firewall lets it in on those networks; and whether each other PC answers. What is wrong comes with what to do about it, such as
  how to make a network private. Runs when the page is first opened, and on "Zkontrolovat". In the window of a managed PC it
  checks that PC. Reads only, through the firewall's own interface, and needs no administrator.

## [0.8.0] - 2026-10-03

### Added
- A game can be installed on several managed PCs with one click: "Nainstalovat na další PC…" in its menu, or "Na další PC…" next
  to Instalovat. Each paired PC says whether it already has the game, is downloading it, has an older version (then it is
  updated) or does not see it on the network; the ones that can take it are ticked, and each says how it went.
- The managed PCs on the "Vzdálená správa" page are an overview: for each PC on the network its GameShare version (marked when
  it differs from this one), what it is downloading with the progress and speed, and the free space in its game folders (marked
  when every folder has less than 20 GB). Asked every five seconds while the page is open, not at all otherwise.
- Wake-on-LAN for managed PCs: one that is off has "Zapnout" instead of "Spravovat". A PC with remote management on tells the
  PCs paired with it its MAC addresses, so this works after it was on once with this version. Wake-on-LAN has to be enabled in
  its BIOS and for its network adapter.

### Changed
- Scanning a game is several times faster on a fast disk: it still reads the disk once and in order, but hashes on several
  threads. Measured on 1 GB from cache: 0.5 s instead of 1.85 s. The hashes are exactly the same as before.

### Fixed
- A download whose disk filled up stalled without a word. It now pauses itself when less than 512 MB is left on its drive and
  says so on the download ("the disk is full: only … MB free on D:\"); Pokračovat continues it once there is room.
- An install, update or repair could start although it would not fit with the downloads already running on that drive, or only
  just fit and then pause near its end. It is now refused up front unless the drive has room for it, for what the running
  downloads there still have to write, and for the 512 MB that are kept free; the message says how much of each.

## [0.7.0] - 2026-10-03

### Added
- Remote management, agent side: one PC can start, pause and cancel downloads on others. Off until turned on at each PC, which
  then shows a pairing code for the managing PC to enter. Paired PCs only talk over TLS with each other's pinned certificate,
  on the new port 47703 (`install-agent.ps1` opens it on the Private and Domain profiles), and may only do what is on a fixed
  list: no settings, no starting games or setup steps, no deleting games. Each action is logged on the managed PC and announced
  to its client. The portable LAN party build never takes it. See "Remote management" in `docs/design-notes.md`.
- Page "Vzdálená správa" in the client: turn remote management of this PC on, show the pairing code, see and remove who may
  manage it and what they did; pair with another PC by its code and open its window with "Spravovat". That window shows the
  other PC's library, transfers and network, without playing, preparing or uninstalling. The tray tooltip says when a paired PC
  did something here.
- `install-agent.ps1` puts a GameShare shortcut on the desktop of all users next to the Start menu entry, `-NoDesktopShortcut`
  leaves it out. `uninstall-agent.ps1` removes it.

### Upgrading
- The remote management port 47703 is opened in the firewall by `install-agent.ps1`. A PC that updates itself from the app keeps
  its old firewall rules, so on a PC that is to be managed run `install-agent.ps1` again, or allow TCP 47703 for the Private and
  Domain profiles by hand. A PC that only manages others needs no new rule.

## [0.6.8] - 2026-10-01

### Added
- Settings has a folded section "Pokročilé – ladění přenosů" for chasing a slow transfer: open files, how much is read ahead for
  each connection that sends, how much received data may wait for the disk, to how many PCs is sent at once, blocks asked for at
  once and disk threads. Empty means the default. Saved values apply at once, also to running transfers, and the log says which
  were used ("Transfer tuning").

### Fixed
- Saving on the Settings page switched off debug logging of the transfer engine that was switched on on the Protokol page.

## [0.6.7] - 2026-10-01

### Security
- A web page open in a browser on the PC could use the agent's control API. It is local only, but a page can send requests to
  127.0.0.1 too: those without a body (start a scan, clear the log, install a game it knows the hash of) went through, and with
  DNS rebinding, its domain turned into 127.0.0.1 after it loaded, everything did, uninstalling a game with its files too. The
  control API and the event hub now refuse a request with an Origin header, and one whose Host is not 127.0.0.1, localhost or
  ::1. GameShare's own programs send neither.

## [0.6.6] - 2026-09-30

### Changed
- A fixed and signed `gameshare.json` reaches every PC by itself. When a new signed list arrives, or a PC on the LAN starts
  offering something, the agent fetches the signed definition of every game and redistributables package it has whose own
  definition is not the signed one. Until now that happened only when somebody prepared a game on that PC, and the package's
  card never offered anything, since its files, and so its version, stayed the same. PCs that did not have it are asked again
  after 10 minutes, or at once when the list or the PCs change: a manifest can be megabytes.

### Fixed
- Looking for updates showed the version that was ready already (0.6.4) at 0 % while a newer one (0.6.5) was on its way. It
  now shows the one coming in.
- A version found on the internet waits up to 10 minutes for a PC on the LAN to get it first, so a LAN party fetches it once.
  That wait stood at 0 % without a word. The settings now say until when it waits, and "Zkontrolovat aktualizace" fetches it
  from the internet at once: someone who asked is not thirty PCs finding it at the same time.

## [0.6.5] - 2026-09-30

### Fixed
- A PC that had the shared redistributables package kept its old definition when the administrator fixed and signed a new
  one, so every game there went on running the old installers (and with Require refused to prepare at all). As for a game,
  the agent now fetches the signed definition of the package from another PC on the LAN when its own is not the signed one.
- The card of the redistributables package offered to pick a program to start it. It is installed by the games that need
  it, so the card no longer offers to play it.

## [0.6.4] - 2026-09-30

### Fixed
- The check for a stuck download went by verified data, which grows only once a whole piece is in. A game with big pieces
  arriving slowly (Wreckfest: 16 MB, so 8 s each at 2 MB/s) looked stuck while data was flowing, got "Obnovuji spojení" and a
  reconnect it did not need, and so came in bursts. It now goes by the data received, and steps in only when nothing arrives.
- Preparing a game counted a redistributable as installed as soon as its installer ended with 0, although one that hands over
  to another process ends before the installation does, or fails without saying so. Before noting the preparation as done, the
  agent now looks for every redistributable its package says how to find ("installedIf"), and when one is missing it says so
  and Play asks for the preparation again.
- The other way round: an installer that ended with an error although what it installs is there (VC++ 2010 ends with 5100
  when a newer version is installed) failed the whole preparation. The client now asks the agent to look, counts such a step
  as done and runs the player's own steps that waited for it, without a second prompt for administrator rights.

## [0.6.3] - 2026-09-30

### Fixed
- A game made of many small files with big pieces came in bursts, a few pieces and then nothing, over and over. The sending PC
  kept only 40 files open, and a piece of Wreckfest spans up to 2,989 of them. It now keeps up to 500 open: Wreckfest went from
  about 4 MB/s on average to 14.5 MB/s. The limit is written to the log when the agent starts.

## [0.6.2] - 2026-09-30

### Changed
- A game's card in the library says what its download does and which phase it is in, like the Downloads page:
  "Instalace · Stahuje se · 42 % · 12 MB/s", "Aktualizace · Pozastaveno", "Instalace · Ověřuji soubory". It said
  "Stahuje se" throughout, and a paused download kept showing its last speed. While the files are checked, the progress
  bar runs without a percentage instead of standing at 100 %.

## [0.6.1] - 2026-09-30

### Fixed
- The portable exe started twice in quick succession ran two transfer engines on the same games, which could break off
  downloads and seeding. Started again, it now only brings up the window of the copy that is already running. A built-in
  agent that fails to start is stopped instead of left running without its ports.

## [0.6.0] - 2026-09-30

### Added
- A graph of the download speed over the last 5 minutes on the Přenosy page, with the current, average and highest speed, like
  Steam's. Each download has a small one of its own, so it shows which game gets the speed when several download at once.

### Changed
- The transfer engine keeps up to 40 game files open at once instead of 8, for all games together. With several games going out
  at once they had to share 8. `Agent:OpenFileLimit` still sets it.

### Fixed
- A download could stop dead for a minute or longer, most often near its end, with the source still connected. Measured with three
  games at once between two engines, about one run in three had such a stall. The agent now notices a download that gets nothing
  from a connected source: after 5 s it asks again for the missing pieces, after 10 s more it reconnects. Reconnecting to a peer
  waits 2 s instead of 60 s, and each game announces itself on the LAN every 10 s instead of 30 s. The longest stall measured since
  was 17 s.
- Meanwhile the download says "Obnovuji spojení" with what is being done, in the list and on the game's card, instead of just
  standing at zero.
- Sending several games at once to the same PC kept breaking off. A game that waited its turn for 20 seconds counted as idle, its
  seed let go of its files and so disconnected that PC, which dropped out of the list of where the game goes and stalled until it
  found the seed again. A seed now keeps its files while a PC that lacks the game is connected to it.
- A download connected to no PC said "Stahuje se" although nothing came in. It now says "Čeká na zdroj", in the list of downloads
  and on the game's card, and goes on by itself once a PC with the game shows up.

## [0.5.0] - 2026-09-29

### Added
- **GameShare updates itself.** The agent looks for a newer release on GitHub every 6 hours (or at `Agent:UpdateSource`, for example
  a share) and asks the other PCs on the LAN which versions they hold. It downloads the new version in the background, from the other
  PCs when they have it, otherwise from the internet after a random wait so the LAN party's connection carries it once, and copies
  the files that did not change. The client shows a strip "Je připravená nová verze" with its notes and an **Aktualizovat** button,
  also in Settings and in the tray menu. Nothing is installed without the click.
- The installed service puts the new version in place through a helper: stop, swap the files, start, and back to the previous
  version when the new one does not answer within 90 seconds. `appsettings.json` is kept. The client restarts into the new version.
  `GameShare-LanParty.exe` replaces itself and starts again.
- Only packages signed with the release key built into the programs are taken, only for the same build and only newer. Every file
  is checked against its SHA-256 after the download and again right before it is put in place.
- `gameshare-admin release-keygen`, `release-sign` and `release-show`. Releases carry `update-<build>.json` and
  `GameShare-Update-<build>.zip` for each build.

### Changed
- The release workflow needs `scripts/release-public.key` and the secret `GAMESHARE_RELEASE_KEY`.

### Fixed
- Adding a game folder and pressing Uložit right after could save the older settings last and undo what Uložit changed.
  Settings are now saved one after the other.

### Upgrading
- 0.5.0 is the first version with the updater, so it has to be installed by hand once. Later versions arrive by themselves.

## [0.4.3] - 2026-09-25

### Fixed
- The agent and `GameShare-LanParty.exe` failed to start on a PC without the Visual C++ runtime ("Unable to load DLL 'tsw'").
  Its three DLLs now ship next to the torrent library, so nothing has to be installed.

## [0.4.2] - 2026-09-25

### Added
- `install-agent.bat` at the top of `GameShare-Agent.zip`: double-click it, it asks for administrator rights (UAC) itself, then
  for the game folders. Any arguments go on to `install-agent.ps1` unchanged.

## [0.4.1] - 2026-09-25

### Fixed
- `scripts\install-agent.ps1` from `GameShare-Agent.zip` failed with a `Join-Path` error when run with Windows PowerShell 5.1
  (`powershell -File`), which leaves `$PSScriptRoot` empty in parameter defaults. `publish.ps1` and `package-release.ps1` had the same flaw.

## [0.4.0] - 2026-09-24

### Added
- Releases on GitHub: pushing a `v*` tag builds everything and publishes `GameShare-LanParty.zip`, `GameShare-Agent.zip`,
  `GameShare-Agent-net10.zip` and `GameShare-Admin.zip` under the same names every time (`.github/workflows/release.yml`,
  `scripts/package-release.ps1`).
- A product page for the web (`docs/web/`), as WordPress blocks and as one Custom HTML block.

### Changed
- The game card shows states and the administrator's verdict as small symbols with the words in a tooltip, keeps only the main
  action (Hrát, or choosing the program) as a button, and puts everything else into one menu.
- The administrator's verdict is a shield: green with a tick for a vouched-for game, orange with "!" for one nobody vouches for,
  red with a cross for a withdrawn one. A game whose files changed shows a pencil instead.

### Fixed
- A game that writes into its own folder the moment it starts (UT2004 and its `UT2004.ini`) no longer quits on its first starts:
  the seed lets go of the game's files as soon as the client asks to start it, not up to five seconds later. A game that stops
  right after it was started says so on its card, and "Hra se spouští…" no longer stays.

## [0.3.0] - 2026-09-24

### Added
- **Game preparation** ("Příprava hry"), what the old LAN party installer did after copying a game: shared redistributables
  (DirectX, Visual C++, .NET) from a redistributables package that is shared like a game, installers shipped with the game,
  `.reg` import with the old install path rewritten to the real folder, a registry key cleaned first, a Windows compatibility
  mode, and a profile folder copied to Documents. The agent plans and checks it, the player sees every step (with the registry
  keys and values it writes) and confirms, the machine's steps run in one elevated process after one UAC prompt, the player's own
  steps (HKCU, compatibility mode, Documents) run as the player. Play asks for it once per setup and folder.
- Several programs per game (`launch` in `gameshare.json`): the first is the play button, the others (an editor, a server,
  launcher variants) are in a menu next to it. An entry can ask to start with administrator rights.
- Game icons, read out of the game's program on each PC.
- The administrator's signed list can vouch for a game's `gameshare.json` too (`definitionHash`). With `Require`, preparation and
  starting as administrator only happen with the signed definition; a PC fetches the signed one from the LAN when it has another.
- A definition editor in the admin GUI: programs, redistributables, registry, compatibility and profile, picked from what is in the
  game folder and checked the way agents check it before `gameshare.json` is written.
- `scripts/import-lan-installer.ps1`: one-time conversion of the old installer (`hry_install_v2\*.7z`, shortcuts, `-meta.json`,
  `_redist`) into a game root with a `gameshare.json` per game and a `_Redist` package.

- The trust list can be published in several places (`Agent:TrustListSource` separated by `;`, e.g. the web and a share). Every place
  is asked and the newest valid list wins. The installer uses `https://lanka.seru.cz/trust.json` by default, takes several with
  `-TrustListSource a,b`, and picks up a `trust-public.key` next to it (then defaulting to `Warn`).
- The portable `GameShare-LanParty.exe` does the same with a `trust-public.key` next to it, and `publish.ps1` puts
  `scripts\trust-public.key` next to the portable exe and into the agent folders.

### Changed
- An installed game's folder gets its `gameshare.json` written, and a scan picks up an edited one, without the game becoming a new version.
- Registry paths are rewritten regardless of case (the old installer missed `C:\GAMES\STARCRAFT`), and a `.reg` file that writes
  outside a game's own keys (autostart, file associations, anything of Microsoft's) is refused as a whole.

## [0.2.0] - 2026-09-23

### Added
- Uploads panel on the renamed "Přenosy" (Transfers) page: which peers are pulling which game from this PC right now, and how fast.
- Network adapters that look virtual (VirtualBox, Hyper-V, VMware, WSL, Docker, …) are skipped for LAN discovery by default, since their
  address flapping against the real adapter is what stalls transfers; Settings lists them with a one-click re-enable.
- "Zahodit" (discard) action on a failed or cancelled download: deletes its partial files instead of leaving the row forever.
- The log view can copy its lines to the clipboard.
- A persisted, GUI-toggleable setting for detailed libtorrent debug logging, and a "Restartovat agenta" button on the Protokol page that
  safely restarts the agent (Windows service, standalone build, or plain console) so the setting takes effect.

### Fixed
- A crash (`TimeSpan` overflow) in the download ETA estimate after a long stall.
- Deleting an uninstalled game's folder now retries a few times instead of giving up on one briefly locked file.
- A scan no longer re-hashes and misregisters the partial files of a failed download as a new game version.

## [0.1.0] - 2026-09-22

First versioned build. Agent, desktop client, admin tools (CLI and GUI), verified-game trust list and the
standalone LAN-party build all carry this number from here on.
