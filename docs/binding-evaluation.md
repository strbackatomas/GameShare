# libtorrent .NET binding evaluation

Checked 2026-09-21 against NuGet and GitHub. Native libtorrent version was read out of the shipped DLLs.

| Candidate | libtorrent | Windows x64 | Linux | Last release | Notes |
|---|---|---|---|---|---|
| **TorrentSharp.Wrap 1.0.1** + Native 0.2.0 | 2.0.11 | yes | x64, arm64 | 2026-09-21 | Resume data, force recheck, peer list, piece map, per-file priority. Own native layer. No project URL, anonymous author, first published 2026-09-07. |
| csdl 2.0.0-beta + Native 2.0.0-pre | 2.0.11 | yes | x64, arm64 | 2026-05-10 | Named author, GitHub repo. TorrentSharp appears to be an extension of it. No resume data, no recheck, no peer list. |
| LibtorrentDotNet 1.1.0 | old | C++/CLI only | no | stale | Windows only, does not fit the Linux agent goal. |
| Ragnar, LibtorrentRTWrapper, LibtorrentSharp | - | - | - | 2014 / stale / 0.1.0-alpha | Not viable. |
| MonoTorrent 3.0.2 (3.9.0-alpha in progress) | pure .NET | yes | yes | 2026-06 (alpha) | Not libtorrent. Can create torrents and add peers. Kept as fallback if native binding fails. |

## Gaps in every libtorrent binding

None of the wrappers can create a `.torrent`. `GameShare.Torrent.TorrentBuilder` does it instead
(bencode + SHA-1 pieces, about 150 lines). Its output is deterministic, so the same directory content
gives the same info hash on every PC. That is what makes content, not directory name, the game identity.

None of the wrappers exposes "add peer by IP". Peers are therefore found by libtorrent Local Service
Discovery (multicast on the LAN), which works without tracker or DHT. If LSD turns out to be blocked on the
real network, the fallback is to run our own UDP discovery and inject peers, which needs a small native
or wrapper change.

## Decision

Use **TorrentSharp.Wrap 1.0.1** for the MVP because it has the resume and recheck API the requirements need.

Risk: anonymous, very young package that ships native binaries into a service. Mitigations:

- All use is behind `GameShare.Torrent`, so swapping to csdl or MonoTorrent touches one project.
- Package version is pinned exactly.
- Before any real deployment, decide whether to vendor a build from source (its readme documents a CMake and vcpkg build).

## Proven so far (one machine)

- Native library loads on Windows x64 under .NET 10.
- libtorrent parses our generated torrent and reports the same info hash.
- Several engines in one process find each other via LSD and the leecher writes the game directly into the
  target directory, byte-identical to the source, with no stray files.
- Resume works after a clean restart with saved resume data and after a crash without it.
- Force recheck repairs a corrupted file by re-fetching only the bad piece.
- A third client downloads from both the original seeder and the second client.
- An engine with a LAN-only filter refuses peers outside the allowed ranges.
- Updating over an installed older version fetches only the changed pieces.

All of this ran on one machine, so LSD traffic never crossed a real network.

## Not yet proven

- Two or three physical PCs, and whether the real switches pass LSD multicast.
- Resume after a Windows restart, the same mechanism but not exercised.
- Throughput on 2.5 GbE and 10 GbE.
- That the LAN filter also blocks outgoing connections to public addresses. Only incoming and discovered peers were tested.
