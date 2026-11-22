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
| Audio evidence, in shadow | on | Keeps each finished recording's audio fingerprint per channel and, while a recording is written, works out the rows the audio says it would publish, in the log and the status only; see [Audio evidence](#audio-evidence-shadow). |
| Save programmes from the IPTV archive | on | Lets clients save finished programmes from the catchup archive; see [Catchup](#catchup). |
| Seconds asked for before a programme starts | 10 | The archive begins at the first segment after the time asked for, up to a segment late. |
| Hours a download waits for the IPTV connection | 12 | |

Jellyfin's own per-library setting *Disabled media segment providers* turns the plugin off for a library: recordings there are not analysed, and rows stored earlier are read only while the provider is enabled there.

## Channel profiles

Channels differ: where the logo sits, what the provider burns into the picture, whether live analysis is worth its cost. A channel profile changes the settings for the recordings of the channels it lists, by name or by their groups in the tuner's playlist, under *Channel profiles* on the settings page.

A finished Jellyfin recording does not say which channel it came from, so the plugin notes the channel from the recording's timer when it starts recording, and from the request for a catchup download, together with the channel's groups in the M3U tuner's playlist. Every later pass over the file, the final one, the Media Segment Scan's or one asked for, uses the profile of that channel. The first profile that lists the channel or one of its groups applies; a recording that no profile lists, or whose channel the plugin never saw, runs with the settings above. For recordings made before the plugin noted channels, an admin can set the channel through the API below.

| Profile setting | |
| --- | --- |
| Channels | Channel names as Jellyfin shows them, separated by semicolons. Case and accents are ignored, so `RTÉ One` also matches a channel the playlist calls `RTE One`, and `*` matches any text, as in `Sky Sports*`. |
| Playlist groups | Groups from the M3U tuner's playlist, separated by semicolons and matched as channel names are. A channel's groups are its `group-title` split at semicolons, as Kodi's IPTV Simple client reads it, so `group-title="Sport;Sky Sports"` puts a channel in `Sport` and in `Sky Sports`. A channel in any of the groups uses the profile, as does a channel listed by name. Recordings noted before this setting existed, or while the playlist could not be read, go by the name alone; setting the channel through the API below reads the groups again. |
| Comskip ini keys | `key=value` pairs separated by semicolons, laid over the base ini for these channels. The run-mode keys the plugin sets are applied after them and win; *Check Comskip* lists any key a profile sets that is left out for that reason. |
| Analysis height | Overrides the setting above for these channels; empty uses it. |
| Detect while recording | Off: these recordings get only the full pass, once they finish. |
| Audio evidence | Off: these channels' audio is neither kept nor listened to. |
| Publish from audio evidence | Off by default. On: while these channels record, clients see the rows the audio decision gives rather than Comskip's analyses alone. Turn it on for a channel once its shadow rows have looked right; see [Audio evidence](#audio-evidence-shadow). |
| Keep Comskip's per-frame data | After each full pass, keeps Comskip's frame data (`.csv`), its logo file, the ini and the breaks in `comseg/frames` under Jellyfin's data directory, one folder per recording: about 16 MB an hour. Comskip replays the `.csv` without decoding, which makes trying settings on a recording cheap. |

Keys that have mattered:

- **A logo at the bottom of the picture**, as on RTÉ News: the built-in `ticker_tape_percentage=15` hides it, Comskip settles on something else or on no logo, and breaks are missed. `ticker_tape_percentage=0` for those channels.
- **A provider watermark in a bottom corner**, as on some IPTV sports feeds: keep the built-in mask, or Comskip takes the watermark, which stays on through the adverts, for the logo.

## Audio evidence (shadow)

Comskip finds breaks from the picture. While a break is on air it cannot tell where it ends, so a row's end runs on to the end of the file until the next analysis, a minute later, puts it back, and a viewer close to live skips some of the programme. Adverts, promos and sponsor clips repeat, though, and the audio says where they are the moment they air.

With *Audio evidence, in shadow* on (the default), the plugin keeps the audio fingerprint of each finished recording of a channel, taken with Jellyfin's own ffmpeg (Chromaprint), together with its final breaks: the newest 20 recordings per channel, none older than 45 days, about 120 KB an hour. While a recording is written, every poll fingerprints its newest audio and looks for it in those recordings and in the recording's own earlier part. Audio heard inside a stored break is advert audio, audio heard outside one is programme audio, and audio heard at a break's edge, such as a sponsor clip, says where this break starts or ends.

From that and Comskip's analyses the plugin works out the rows it would publish: a break opens on two adverts heard within a minute, or a known break start followed by an advert; a row ends where the audio last heard adverts, or at an edge the audio implies, rather than at the end of the file; a break that opened on a known start and has not yet closed also takes in adverts heard up to three minutes after the last, as adverts first aired since the stored recordings fill a break's middle and end; programme audio cuts a row. On a channel whose breaks nearly always carry audio heard before, a break Comskip reports with none is left for the final pass. Those rows go to the log and the status on the settings page only; what is published does not change. A profile can turn the audio off for its channels. Recordings whose channel the plugin does not know are not listened to.

The status lists, for each recording in progress, how many kept recordings it is matched against and the rows it would publish.

A channel profile with *Publish from audio evidence* on publishes those rows instead, while its recordings are written; the final pass still replaces them. On the 15 November ITV broadcast, matched against three earlier ITV recordings, every break reached clients 4 to 14 s after it started, against a minute or more from Comskip's analyses, and no row covered more than 1.5 s of the programme. A channel whose recordings share little audio, such as Sky's NFL coverage from game to game, gains less, and is better left in shadow until it has more history. With the Comseg build of Comskip, an analysis made without the channel logo has its Comskip-only breaks held for the final pass on channels with audio history.

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
GET  /Comseg/Recording/{itemId}          the recording's channel, its playlist groups, its profile and the settings its passes use
POST /Comseg/Recording/{itemId}/Channel  set the recording's channel: {"ChannelName": "RTE One"}; empty forgets it
```

Clients read segments through Jellyfin's own `GET /MediaSegments/{itemId}?includeSegmentTypes=Commercial`. Rows are replaced rather than edited when a break changes, so re-fetch by item; never cache by segment id.

## Build

```
dotnet test
tools/package.sh            # dist/comseg_<version>.zip
```

Releases are versioned `12.0.0.N` with `targetAbi 12.0.0.0`, built for net10.0 against the oldest Jellyfin 12 package.
