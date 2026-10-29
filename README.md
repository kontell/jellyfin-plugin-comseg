# Comseg

Jellyfin server plugin: commercial breaks in DVR recordings, found with [Comskip](https://github.com/erikkaashoek/Comskip) and stored as Jellyfin **Commercial** media segments, so any client that honours media segments (Jellyfin Web, Android TV, Kofin, pvr.kofin) can skip them.

Two paths:

- **While a recording is being written.** The plugin watches Jellyfin's timers for recordings in progress, runs Comskip live against each file and reads the full analysis the [Comseg build of Comskip](https://github.com/kontell/Comskip) writes every minute of content. A break is published once two consecutive analyses agree on where it starts; its end follows the latest analysis while the break is still on air, so a viewer a couple of minutes behind live skips it.
- **Once a recording finishes.** A full pass replaces the live breaks with Comskip's final result. Jellyfin clears an item's segments when it notices the file has closed, usually while that pass is still running; the plugin puts the breaks straight back. Recordings that the scheduled *Media Segment Scan* finds without breaks, or with only the live ones because the final pass was interrupted, get the same pass.

Requires Jellyfin 12 and a Comskip binary on the server. Finished recordings work with stock Comskip. Detection while a recording is being written needs the [Comseg build of Comskip](https://github.com/kontell/Comskip), branch `comseg`, which adds a periodic full analysis to Comskip's live mode; stock live mode only reports breaks that contain black frames. The change is on its way upstream, see the notes in [`comskip/`](comskip/README.md).

## Settings

Dashboard → Plugins → Comseg.

| Setting | Default | |
| --- | --- | --- |
| Comskip executable | `comskip` | Path or a name on the server's PATH. "Check Comskip" runs it. |
| Comskip ini | built-in | Path of a comskip.ini of your own, anywhere the server can read; blank uses the built-in one, which sets `ticker_tape_percentage=15`, the bottom 15% of the picture, to keep provider watermarks and score bugs out of the logo search at any resolution. If the server finds no file at the path, runs use the built-in one; "Check Comskip" says which one they use. Whichever file is used, the plugin sets `live_tv`, `live_master_interval`, `live_tv_retries`, `output_edl`, `output_default` and `thread_count` itself, from its own settings where it has one. |
| Detect while recording | on | Needs the Comseg build of Comskip. |
| Full pass on finished recordings | on | Also what the Media Segment Scan runs. |
| Live analysis interval | 60 s | `live_master_interval`. |
| Analyses before a break is published | 2 | Drops the one-off false breaks sports feeds produce. |
| Shortest break published | 20 s | |
| Recordings analysed live at once | 2 | A live Comskip costs about a third of a core at real-time pace, and holds per-frame data for the whole recording, about 700 MB by the end of a four-hour one. |
| Extra recording folders | none | Folders outside Jellyfin's own recording paths whose videos count as recordings, separated by semicolons. |

Jellyfin's own per-library setting *Disabled media segment providers* turns the plugin off for a library: recordings there are not analysed, and rows stored earlier are read only while the provider is enabled there.

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
