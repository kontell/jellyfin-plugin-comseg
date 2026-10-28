# Comseg

Jellyfin server plugin: commercial breaks in DVR recordings, found with [Comskip](https://github.com/erikkaashoek/Comskip) and stored as Jellyfin **Commercial** media segments, so any client that honours media segments (Jellyfin Web, Android TV, Kofin, pvr.kofin) can skip them.

Two paths:

- **While a recording is being written.** The plugin watches Jellyfin's timers for recordings in progress, runs Comskip live against each file and reads the full analysis that Comskip, with the plugin's [live-analysis patch](comskip/README.md), writes every minute of content. A break is published once two consecutive analyses agree on where it starts; its end follows the latest analysis while the break is still on air, so a viewer a couple of minutes behind live skips it.
- **Once a recording finishes.** A full pass replaces the live breaks with Comskip's final result. Recordings that the scheduled *Media Segment Scan* finds without breaks get the same pass.

Requires Jellyfin 12 and a Comskip binary on the server. Finished recordings work with stock Comskip. Detection while a recording is being written needs Comskip built with the [live-analysis patch](comskip/README.md), because Comskip's own live mode only reports breaks that contain black frames.

## Settings

Dashboard → Plugins → Comseg.

| Setting | Default | |
| --- | --- | --- |
| Comskip executable | `comskip` | Path or a name on the server's PATH. "Check Comskip" runs it. |
| Comskip ini | built-in | The built-in ini sets `ticker_tape=160`, which keeps provider watermarks and score bugs out of the logo search; without it Comskip found nothing on a Sky Sports feed. |
| Detect while recording | on | Needs the patched Comskip. |
| Full pass on finished recordings | on | Also what the Media Segment Scan runs. |
| Live analysis interval | 60 s | `live_master_interval`. |
| Analyses before a break is published | 2 | Drops the one-off false breaks sports feeds produce. |
| Shortest break published | 20 s | |
| Recordings analysed live at once | 2 | A live Comskip costs about a core at real-time pace. |

Jellyfin's own per-library setting *Disabled media segment providers* can turn the provider off for a library; the plugin's rows are read only while the provider is enabled there.

## API (admin)

```
GET  /Comseg/Status          live jobs and the configured binary
POST /Comseg/Check           run the configured Comskip with --help
POST /Comseg/Detect/{itemId} queue a full pass over a finished recording (202)
```

Clients read segments through Jellyfin's own `GET /MediaSegments/{itemId}?includeSegmentTypes=Commercial`. Rows are replaced rather than edited when a break changes, so re-fetch by item; never cache by segment id.

## Build

```
dotnet test
tools/package.sh            # dist/comseg_<version>.zip
```

Releases are versioned `12.0.0.N` with `targetAbi 12.0.0.0`, built for net10.0 against the oldest Jellyfin 12 package.

The feasibility study that shaped this is in [`docs/feasibility-comskip-2026-10-27.md`](docs/feasibility-comskip-2026-10-27.md).
