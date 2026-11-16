# Evaluator

Runs the plugin's own logic over files, outside Jellyfin, and scores the result against a break list checked by eye. It exists so that a change to detection can be judged on recordings before it is tried on a server, and so that the same judgement can be repeated by a script.

```
tools/eval/run final    --media FILE --out DIR [--comskip PATH] [--ini FILE] [--set KEY=VALUE]... [--keep-frames]
tools/eval/run live-sim --analyses DIR --out FILE [--poll SECONDS] [--confirm N] [--duration SECONDS] [--final EDL] [--final-at SECONDS]
tools/eval/run score    --reference FILE --timeline FILE [--lags 30,120,600,1800] [--json]
```

`final` is the full pass over a finished file: the base ini, then the `--set` keys (what a channel profile would carry), then the plugin's run-mode keys, which win. With `--keep-frames` Comskip leaves its per-frame `.csv` and its logo file in `DIR`; give that `.csv` back as `--media` and Comskip replays it in a fraction of a second without decoding, which is what makes trying many ini values cheap. A replay is close to a decode but not identical, so confirm a result worth keeping with a decode.

`live-sim` replays the analyses a live Comskip wrote through the plugin's decision about what to publish. `DIR/index.tsv` lists, one a line, the second of the recording at which an analysis appeared, a tab, and the EDL file holding it. The result is a timeline: every set of rows clients could have seen, with the time it took effect.

`score` compares a timeline with a reference and prints one number, lower being better. The exit code is 2 when a row reaches into programme the reference marks as free of breaks.

## References

A reference is a text file, one fact a line, times in seconds from the start of the recording:

```
duration 10957.9
break 1246.5 1479.5
unknown 1240.5 1246.5
free 0 1230
```

Everything that is neither a `break` nor `unknown` counts as programme. `unknown` is for stretches nobody checked, or where skipping and not skipping are equally fine (a channel sting next to a break). `free` marks programme that was looked at from end to end: a row reaching more than 5 s into one fails the run, whatever its score.

## The score

A viewer watches a fixed time behind the recording's edge. A second of the recording counts as skipped for that viewer when a row covering it was in force by the time they reached it. For each viewer, and for the final rows:

- a second of programme skipped costs 5;
- a second of a break not skipped costs 1.

A row that is withdrawn, or whose start moves by more than 1.5 s, costs 5 once. The total is the sum over the viewers (30 s, 2 min, 10 min and 30 min behind by default) and the final rows.

## Recording a programme again

`tools/eval/replay-channel FILE DIR` serves a finished recording as a live channel on `127.0.0.1`, at real-time pace, for a Jellyfin test server to record as if it were on air. Add `DIR/replay.m3u` to that server as an M3U tuner and set a timer on the channel once the replay is running. Live detection only shows its faults on a file that is growing while it is read, so this is how a change to it is tested on the same programme twice.
