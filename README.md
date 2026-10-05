# AutoCrop for Jellyfin

Removes black bars that are burned into the video itself, so the real picture fills your screen in Jellyfin's web player.

Some files carry their letterbox inside the frame: a 2:1 film stored as 1920×1080 has 60 rows of black above and below the picture. On a 16:9 screen you don't notice, but on a 21:9 ultrawide you get bars on all four sides. Jellyfin's own aspect options (Auto, Cover, Fill) can't fix that: they don't know where the picture is. AutoCrop measures it per file with ffmpeg and zooms the player so exactly the picture area fills the screen, nothing more.

## What it does

- **Measures the picture, it doesn't guess.** Every keyframe of the whole file is checked. A row or column that is black in every keyframe is a bar; anything that ever shows picture is kept. A film with a single full-frame shot is never cropped in that shot.
- **Per scene, for films that change shape.** IMAX releases switch between 2.39:1 and 1.90:1. In per-scene mode the player follows those changes and zooms as far as each scene allows, without ever cutting off picture.
- **Only zooms when it helps.** A 2:1 picture in a 16:9 frame on a 16:9 screen already fills the width, so nothing changes. On a 21:9 screen it zooms in until the bars are gone.
- **Letterbox and pillarbox.** Bars at the top and bottom, the sides, or both.
- **Scans in the background.** New movies and episodes are scanned after they're added, and a nightly task catches anything missed or changed. One file at a time, at the lowest CPU priority.
- **Dashboard.** See what every scan found, rescan single items, and change the settings.

## Modes

| Mode | What you see |
| --- | --- |
| **Per scene** (default) | The crop follows the picture shape scene by scene, with a short zoom transition. |
| **Static** | One crop for the whole file: the union of every scene's picture. Full-frame scenes keep the whole file uncropped. |
| **Off** | Jellyfin's normal playback. |

The server default is set on the plugin's settings page. Each viewer can override it in their own browser: press **c** during playback to cycle per scene → static → off. A short message shows the new mode, and the choice is remembered in that browser.

AutoCrop only acts when Jellyfin's own aspect ratio setting is **Auto**. Choose Cover or Fill in the player and AutoCrop stays out of the way.

## Limitations

- **Web player only.** The crop is applied by a script in Jellyfin's web client (a browser, or apps that wrap it such as Jellyfin Media Player's web view). Native apps such as Android TV, Swiftfin, Infuse or Kodi don't run it.
- **Subtitles:**
  - Text subtitles drawn by Jellyfin as an overlay (subtitle styling "Custom") aren't affected.
  - Native browser subtitles (styling "Native", the default in most desktop browsers) would zoom along with the video, so AutoCrop moves them into the part of the picture that is still on screen.
  - ASS/SSA and PGS/VobSub subtitles are drawn on canvases beside the video. AutoCrop keeps them over the whole frame as Jellyfin would show it without zoom. They stay readable, but positioned signs and karaoke won't line up exactly with the zoomed picture.
- **Keyframes only.** Detection decodes keyframes, not every frame (a 50-minute 1080p episode takes about 45 seconds on a NAS). A shape change that starts and ends between two keyframes isn't seen. Changes at a scene cut usually start a new keyframe, and the stretch between two keyframes around a change always gets the larger picture.
- **Very dark scenes** can look like smaller pictures to ffmpeg. In static mode the union over the whole file makes this harmless. In per-scene mode a long, very dark stretch could be zoomed slightly too far; segments shorter than the minimum segment length are merged away.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   `https://github.com/WouterStulp/jellyfin-plugin-autocrop/releases/latest/download/manifest.json`
2. Install **AutoCrop** from the catalog and restart Jellyfin.
3. Reload the web client (the player script is added to its page on load).

Requires Jellyfin 10.11.9 or later, including 12.x. ffmpeg is the one Jellyfin already uses.

## Dashboard and settings

**Dashboard → Plugins → AutoCrop** has two views.

**Overview**
- Counts: scanned, with bars, per scene (more than one segment), pending (not scanned yet), failed.
- A table of every scanned item: title, frame size, picture size and aspect ratio (e.g. 1920×960 · 2.00:1), number of segments with a small timeline of where the shape changes, scan date (dd-mm-yy) and status.
- Search, filters (all / with bars / per scene / no bars / failed), paging, and a **Rescan** button per row.
- Failed scans show ffmpeg's reason. They aren't retried every night; use Rescan, or they're retried automatically once the file changes.
- **Scan library now** runs the scheduled task and shows its progress.

**Settings**
- Enable or disable the plugin.
- Default mode.
- Minimum bar: bars thinner than this share of the width or height (default 1%) are ignored, so edge noise doesn't cause 2-pixel crops.
- Minimum segment length (default 2 s).
- Transition time (default 300 ms, 0 for instant).

The library scan, the scheduled task "Detect black bars", runs daily at 02:00. Change its schedule under **Dashboard → Scheduled tasks**. Changes to the minimum bar or segment length apply to items scanned after the change; use Rescan to apply them to existing items.

## How it works

### Detection

For every movie and episode with a local video file, the server runs Jellyfin's ffmpeg over the whole file, decoding keyframes only:

```
ffmpeg -hide_banner -nostats -nostdin -skip_frame nokey -i <file> -map 0:V:0 \
       -vf cropdetect=limit=0.094:round=2:reset=1:skip=0 -an -sn -dn -f null -
```

- `reset=1` makes cropdetect report each keyframe on its own: its picture bounds (`x1 x2 y1 y2`) and timestamp (`t`, seconds from the start of the file, the same clock the browser uses).
- `limit=0.094` is given as a fraction, so ffmpeg scales it to the bit depth. With the usual absolute `limit=24`, a 10-bit file's black (64 out of 1023) counts as picture and nothing is ever cropped. That was checked against a generated 10-bit clip.
- `skip=0` keeps the first keyframes, which cropdetect skips by default.
- `-map 0:V:0` picks the main video stream, never embedded cover art.

From those keyframes the plugin builds:

- **The whole-file crop:** the union of every keyframe's bounds (fully black keyframes are ignored). This is static mode and the fallback.
- **The per-scene timeline:**
  - Consecutive keyframes with the same bounds, within a few pixels, form one segment.
  - Fully black keyframes, such as fades, belong to their neighbours.
  - The time between the last keyframe of one shape and the first of the next gets the union of both, so the switch to a larger picture always happens early enough.
  - Segments shorter than the minimum length are merged into the neighbour that loses the least zoom, always taking the larger box. A brief full-frame flash widens the crop instead of being cut.
  - Every segment's box contains every keyframe inside it.
  - A file with more than 200 segments falls back to the whole-file crop.

Bars thinner than the minimum bar setting are dropped per side, both for the whole-file crop and for every segment.

Results are stored per item (path, file size and modification time, frame size, crop, segments, scan time, or the failure reason) in `crops.json` in the plugin's data folder. Each write goes through a temp file that is then moved into place. A changed file is scanned again.

### When scans run

- The scheduled task scans everything that has no result yet or whose file changed, and drops results for items that left the library.
- New or updated movies and episodes are queued into one background worker after a 30-second settle delay, so files that are still being copied aren't measured early.
- Only one ffmpeg runs at a time, at idle priority (nice 19 on Linux), and cancelling the task stops it.
- Decoding runs on the GPU Jellyfin uses for transcoding (VAAPI for Intel QSV/VAAPI on Linux, CUDA for NVENC, VideoToolbox on macOS). Decoding is bit-exact, so the result is identical to the CPU; on an Intel N-series/Pentium iGPU HEVC scans about 3× faster. If the GPU can't decode a file, it is measured again on the CPU. Turn it off with "Decode on the GPU" in the settings.
- Virtual items, disc images and folders, `.strm` files and remote paths are skipped.

### Playback

The plugin adds a small script to the web client's `index.html`. It does this at request time without changing any files, and also registers with the File Transformation plugin if that is installed. The script:

1. Notices when the player starts or the item changes, such as the next episode or autoplay. With direct play the item id comes from the video URL. Transcoded (HLS) playback uses a `blob:` URL, so it asks Jellyfin's sessions API what this device is playing.
2. Fetches `GET /AutoCrop/Items/{id}` through Jellyfin's `ApiClient`, which is authenticated and respects a base URL. The server only answers for items the signed-in user may see.
3. Computes the zoom. Jellyfin shows the whole frame with `object-fit: contain` at scale `k = min(cw/fw, ch/fh)`. The picture area alone would fit at `k2 = min(cw/aw, ch/ah)`. The video is scaled by `s = k2 / k` around its centre and shifted so the centre of the picture area lands in the centre of the screen. Overflow is hidden by the player container.
4. In per-scene mode, follows `currentTime` and applies each segment's box with a short CSS transition. After a seek it jumps instantly.
5. Recalculates on resize and fullscreen, and removes the zoom when playback stops, the item has no crop, the mode is off, or Jellyfin's aspect setting isn't Auto.

## API

| Endpoint | Access | Purpose |
| --- | --- | --- |
| `GET /AutoCrop/Items/{itemId}` | signed-in user who can see the item | Crop, segments, default mode and transition for the player. 404 when there is no result, no crop is needed, or the file changed since the scan. |
| `POST /AutoCrop/Items/{itemId}/Rescan` | administrator | Drops the result and queues the item for a scan. |
| `GET /AutoCrop/Stats` | administrator | Counts for the dashboard. |
| `GET /AutoCrop/Results?filter=&search=&startIndex=&limit=` | administrator | Paged results for the dashboard (`filter`: `bars`, `per-scene`, `no-bars`, `failed`). |
| `GET /AutoCrop/Web/autocrop.js` | anonymous | The player script. |

## Development

No local .NET needed:

```
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test -c Release
node test/crop-math.test.js
```

The end-to-end detection tests generate small clips with ffmpeg and run a real cropdetect pass over them: bars on every keyframe (8-bit and 10-bit), a clip with a full-frame part, a clip that switches between 2.39:1 and 1.90:1, and an unreadable file. They run when `ffmpeg` is on the `PATH` (or set `AUTOCROP_FFMPEG`) and are skipped otherwise. CI installs ffmpeg so they always run there.

The plugin targets net9.0 against the Jellyfin 10.11.9 SDK. That one build loads on 10.11 and on 12.x servers. `targetAbi.txt` must equal the SDK package version.

### Releasing

Publish a GitHub release whose tag is the version (`v1.0.0.0`) and whose text is the changelog. `.github/workflows/release.yml` tests and builds the tag, then attaches `autocrop-v1.0.0.0.zip` and an updated `manifest.json` to the release.

## License

[MIT](LICENSE)

## NOTICE

Parts of this plugin are adapted from [Jellyscribe](https://github.com/WouterStulp/Jellyscribe):

- The web client script injection (`ScriptInjectionStartupFilter.cs`)
- The File Transformation registration (`FileTransformationTask.cs`)
- The release workflow
- The dashboard styling

Jellyscribe is licensed under the MIT License:

```
Copyright (c) 2026 Lachlan Young

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
