# Changelog

All notable changes to GameShare are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/): MAJOR.MINOR.PATCH, where MAJOR changes mean "not compatible with
older agents on the same LAN", MINOR adds a feature, PATCH fixes a bug. See "Verzování" in README.md for how
this is wired into the build and where a peer's version shows up.

## [Unreleased]

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
