# The Comseg build of Comskip

Comseg runs stock [Comskip](https://github.com/erikkaashoek/Comskip) (GPL-2.0) for finished recordings. For recordings still being written it needs one change, which lives in the [kontell/Comskip](https://github.com/kontell/Comskip) fork:

- Branch `comseg` is what to build: upstream master plus the change. `tools/build-comskip.sh` clones it, builds without the SDL GUI and installs the binary under a prefix of your choosing; point the plugin's "Comskip executable" setting at it.
- Branch `live-master-interval` carries the change as one commit on top of upstream master, and [pull request #1](https://github.com/kontell/Comskip/pull/1) on the fork is where it is being readied for upstream.

The change adds the ini key `live_master_interval`. In `live_tv=1` mode, every `live_master_interval` seconds of content the process forks; the child runs Comskip's full commercial analysis (`BuildMasterCommList`) over the frames decoded so far, on its own copy of the process state, and writes the result atomically to `<base>.master.edl`. The parent keeps decoding, untouched. It is needed because Comskip's own live list only clusters black frames: on a feed whose ad breaks have no black frames it reports almost nothing while the full analysis, which also uses logo and aspect-ratio changes and silence, finds every break.
