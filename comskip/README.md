# The patched Comskip

Comseg runs stock [Comskip](https://github.com/erikkaashoek/Comskip) (GPL-2.0) for
finished recordings. For recordings still being written it needs one patch,
`comskip-live-master.patch`, which adds the ini key `live_master_interval`:

- In `live_tv=1` mode, every `live_master_interval` seconds of content the
  process forks. The child runs Comskip's full commercial analysis
  (`BuildMasterCommList`) over the frames decoded so far, on its own copy of
  the process state, and writes the result atomically to `<base>.master.edl`.
  The parent keeps decoding, untouched.
- Why: Comskip's own live list only clusters black frames. On a feed whose ad
  breaks have no black frames (Sky Sports, for one) it reports almost nothing
  while the full analysis, which also uses logo and aspect-ratio changes and
  silence, finds every break. Measured on a full NFL broadcast: 1 of 27 breaks
  from the stock live list during the recording, 27 of 27 from the patched
  periodic analysis, starts within 0.1 s of the final pass.
- Linux only (`fork`). A full analysis over three hours of frames takes
  seconds and a copy-on-write snapshot of the frame arrays.

`tools/build-comskip.sh` clones upstream, applies the patch and builds. The
patch applies to upstream master as of October 2026 (0.83.001).
