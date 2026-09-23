# Changelog

All notable changes to GameShare are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/): MAJOR.MINOR.PATCH, where MAJOR changes mean "not compatible with
older agents on the same LAN", MINOR adds a feature, PATCH fixes a bug. See "Verzování" in README.md for how
this is wired into the build and where a peer's version shows up.

## [Unreleased]

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
