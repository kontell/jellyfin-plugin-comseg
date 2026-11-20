# Comseg

Jellyfin server plugin: commercial breaks in DVR recordings, found with [Comskip](https://github.com/erikkaashoek/Comskip) and stored as Jellyfin **Commercial** media segments, so any client that honours media segments (Jellyfin Web, Android TV, Kofin, pvr.kofin) can skip them.

Two paths:

- **While a recording is being written.** The plugin watches Jellyfin's timers for recordings in progress, runs Comskip live against each file and reads the breaks the [Comseg build of Comskip](https://github.com/kontell/Comskip) finds every minute of content, by running its end-of-recording analysis over the frames decoded so far. A break is published once two consecutive analyses agree on where it starts; its end follows the latest analysis while the break is still on air, so a viewer a couple of minutes behind live skips it in a client that re-reads the segments while it plays, such as pvr.kofin.
- **Once a recording finishes.** A full pass replaces the live breaks with Comskip's final result. If it finds no breaks at all where the live analysis found some, it has most likely failed (by losing the channel logo, say), so the live breaks stand and the plugin logs a warning. Jellyfin clears an item's segments when it notices the file has closed, usually while that pass is still running; the plugin puts the breaks straight back. Recordings that the scheduled *Media Segment Scan* finds without breaks, or with only the live ones because the final pass was interrupted, get the same pass.

Requires Jellyfin 12 and a Comskip binary on the server. Finished recordings work with stock Comskip. Detection while a recording is being written needs the [Comseg build of Comskip](https://github.com/kontell/Comskip), branch `comseg`, which makes Comskip's live mode run its end-of-recording analysis every minute over the frames decoded so far; stock live mode only reports breaks that contain black frames. The change is on its way upstream, see the notes in [`comskip/`](comskip/README.md). The same build keeps the channel logo through a long recording's closing filler, where stock Comskip can take the next programme's graphics for the logo and find no breaks at all.

## Settings

Dashboard → Plugins → Comseg.

| Setting | Default | |
| --- | --- | --- |
| Comskip executable | `comskip` | Path or a name on the server's PATH. "Check Comskip" runs it. |
| Comskip ini | built-in | Path of a comskip.ini of your own, anywhere the server can read; blank uses the built-in one, which sets `ticker_tape_percentage=15`, the bottom 15% of the picture, to keep provider watermarks and score bugs out of the logo search at any resolution. If the server finds no file at the path, runs use the built-in one; "Check Comskip" says which one they use. Whichever file is used, the plugin sets `live_tv`, `live_master_interval`, `live_tv_retries`, `output_edl`, `output_default` and `thread_count` itself, from its own settings where it has one. |
| Detect while recording | on | Needs the Comseg build of Comskip. |
| Full pass on finished recordings | on | Also what the Media Segment Scan runs. |
| Live analysis interval | 60 s | `live_master_interval`: how much recording passes between break-finding passes. Frames are decoded once and each pass reuses them, so a pass takes seconds. |
| Analyses before a break is published | 2 | Drops the one-off false breaks sports feeds produce. |
| Shortest break published | 20 s | |
| Recordings analysed live at once | 2 | A live Comskip costs about a third of a core at real-time pace, and holds per-frame data for the whole recording, about 700 MB by the end of a four-hour one. |
| Analysis height | 0 (as recorded) | 720 or 480 has Comskip analyse a copy the GPU scales down through VA-API, made as the recording is written and used again for the final pass. It costs a fraction of a core, where decoding a UHD recording costs Comskip several. An HDR recording's copy is tone mapped to SDR as well, as Jellyfin does on AMD: read as SDR, an HDR picture is too flat for Comskip to find the channel logo. Recordings that are neither taller than this nor HDR are analysed as they are, and so is a recording whose copy fails. |
| GPU device | `/dev/dri/renderD128` | The VA-API render device that makes the copy. The user Jellyfin runs as needs access to it, as it does for hardware transcoding. Tone mapping an HDR copy also needs a Vulkan driver for it (Mesa's RADV on AMD), as Jellyfin's own tone mapping does. |
| Extra recording folders | none | Folders outside Jellyfin's own recording paths whose videos count as recordings, separated by semicolons. |
| Save programmes from the IPTV archive | on | Lets clients save finished programmes from the catchup archive; see [Catchup](#catchup). |
| Seconds asked for before a programme starts | 10 | The archive begins at the first segment after the time asked for, up to a segment late. |
| Hours a download waits for the IPTV connection | 12 | |

Jellyfin's own per-library setting *Disabled media segment providers* turns the plugin off for a library: recordings there are not analysed, and rows stored earlier are read only while the provider is enabled there.

## Catchup

A client can ask the server to save a programme that has already aired from the IPTV provider's catchup archive into the recordings, as if it had been recorded. The Catchup Download add-on for Kodi does this from the TV guide's context menu.

The server finds the channel in its M3U tuner's playlist by name, then by number, and copies the programme from the archive with Jellyfin's ffmpeg. It saves the programme where Jellyfin saves a recording that is not a series, with an nfo and a poster: the programme's image when the client sends one on a public address, otherwise a frame from the programme. It then has the library pick it up and runs the full pass over it.

Downloads run one at a time. IPTV accounts allow few connections, so a download waits while Jellyfin is recording, has a recording due before the download would finish, or is showing live TV, and stops to wait again if one of those starts. It cannot see viewing that does not go through this server.

The provider's archive must be Flussonic-style: a channel URL ending in `NAME.m3u8`, whose archive answers `archive-<start>-<seconds>.m3u8`. The plugin checks the archive's playlist before downloading, because such archives answer a wrong token, a time outside the archive or a start in the future with HTTP 200 and a stand-in video or the live channel. Jobs are kept in memory, so a server restart drops the queue.

```
POST   /Comseg/Catchup        queue a programme (202 with the job; 400, 404, 409 or 503 with the reason)
GET    /Comseg/Catchup        the jobs of the last day
DELETE /Comseg/Catchup/{id}   cancel a job
```

These need permission to manage live TV recordings. The request body is `ChannelName`, `ChannelNumber`, `StartUtc`, `EndUtc`, `Title`, `Plot` and `ImageUrl`.

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
