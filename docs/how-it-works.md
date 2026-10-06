# How AutoCrop works

Technical background for developers. For using the plugin, see the [README](../README.md).

## Trickplay pre-check

Jellyfin's trickplay images are small thumbnails (320 px wide, one every 10 seconds by default) tiled into sheets of 10×10. Measuring them takes about a second, where the keyframe scan reads the whole file at disk speed (20–60 seconds per episode). Before the keyframe scan, AutoCrop asks Jellyfin (`ITrickplayManager`) for the item's trickplay, takes the largest resolution, and runs one ffmpeg over its sheets, wherever Jellyfin keeps them (the metadata folder, or beside the file with "Save trickplay images next to media"):

```
ffmpeg -f image2 -framerate 1 -start_number 0 -i <trickplay folder>/%d.jpg \
       -vf untile=10x10,cropdetect=limit=0.094:round=2:reset=1:skip=0 -f null -
```

`untile` splits each sheet back into its thumbnails, in order: thumbnail *n* is at *n* × interval. The last sheet is padded with black cells past the thumbnail count; those are ignored.

The thumbnails can only ever conclude **no crop needed**. That takes all of:

- at least 20 thumbnails with picture;
- together they show picture in every row and column of the thumbnail, so there is less than one thumbnail pixel of bar on every side (one pixel is about 6 video pixels at 1080p, under the 1% minimum bar);
- no per-scene matte: no letterbox or pillarbox recurs in at least 5% (and at least 5) of the thumbnails, as in an IMAX film that switches shape.

Such a file is recorded as uncropped with source `trickplay`; its thumbnail measurements are kept like keyframes, marked as thumbnail scale. Anything else goes to the keyframe scan: possible bars, possible mattes, too few thumbnails, no trickplay yet, or an ffmpeg failure. So does stale trickplay, made before the file was replaced: thumbnails older than the file, or thumbnail count × interval more than 10% or 60 seconds away from the runtime.

Turn it off with "Use trickplay images to skip files without bars"; every file then gets the keyframe scan.

## Detection

For every movie and episode with a local video file that the trickplay pre-check didn't settle, the server runs Jellyfin's ffmpeg over the whole file, decoding keyframes only:

```
ffmpeg -hide_banner -nostats -nostdin -skip_frame nokey -i <file> -map 0:V:0 \
       -vf cropdetect=limit=0.094:round=2:reset=1:skip=0 -an -sn -dn -f null -
```

- `reset=1` makes cropdetect report each keyframe on its own: its picture bounds (`x1 x2 y1 y2`) and timestamp (`t`, seconds from the start of the file, the same clock the browser uses).
- `limit=0.094` is given as a fraction, so ffmpeg scales it to the bit depth. With the usual absolute `limit=24`, a 10-bit file's black (64 out of 1023) counts as picture and nothing is ever cropped. That was checked against a generated 10-bit clip.
- `skip=0` keeps the first keyframes, which cropdetect skips by default.
- `-map 0:V:0` picks the main video stream, never embedded cover art.

Some Blu-ray remuxes flag hardly any keyframes (three in a 24-minute episode), and a union of three frames proves nothing. When the keyframe pass gives fewer than one keyframe per minute of a file of at least 2 minutes, the file is measured again with full decoding (on the GPU when enabled) and a frame every 2 seconds:

```
-vf "select='isnan(prev_selected_t)+gte(t-prev_selected_t\,2)',cropdetect=limit=0.094:round=2:reset=1:skip=0"
```

The analysis is the same; the result's source is `frames` instead of `keyframes`.

With Intel QSV on Linux this pass keeps the frames on the GPU until `select` has picked one, so only one frame per 2 seconds is copied to system memory:

```
-init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device qsv=qs@va -hwaccel qsv -hwaccel_output_format qsv ...
-vf "select=...,hwdownload,format=nv12,cropdetect=..."
```

On a Pentium Gold 8505 that measured 5 minutes of an h264 Blu-ray remux in 23 s instead of 52 s, with byte-identical cropdetect output. The download format follows the pixel format the keyframe pass saw: `nv12` for 8-bit, `p010le` for 10-bit 4:2:0; any other format takes the usual path. The same trick on plain VAAPI surfaces (`-hwaccel_output_format vaapi`) silently dropped three quarters of the frames, so the QSV result is only used when ffmpeg succeeds and gives at least 90% of one sample per 2 seconds. Otherwise the file is measured the usual way. Results measured on keyframes before this pass existed (version 1.1), with fewer than one keyframe per minute of their stored duration, are dropped and measured again this way at startup, when the task runs, and on **Re-analyse all**.

From those keyframes the plugin builds:

- **The whole-file crop:** the union of every keyframe's bounds (fully black keyframes are ignored). This is static mode and the fallback.
- **The per-scene timeline:**
  - First the file's real picture shapes, its mattes. A matte is a letterbox (full width, equal bars top and bottom) or a pillarbox (full height, equal bars left and right) that recurs: keyframes within 0.5% of the frame size of each other form a cluster, and a cluster counts when it holds at least 5% of the keyframes and at least 20 of them, with an aspect between 1.30 and 2.80. Its box is the union of the cluster. The whole-file crop is always a matte too, the widest one. An IMAX film has two (2.39:1 and the full frame), almost everything else one.
  - Each keyframe gets the smallest matte that fully contains its bounds. Dark shots, title cards and credits give smaller, scattered bounds inside the real picture, so they land in their scene's matte instead of becoming a shape of their own. A keyframe that pokes out of every other matte gets the whole-file crop.
  - Consecutive keyframes with the same matte form one segment. Fully black keyframes, such as fades, belong to their neighbours.
  - The time between the last keyframe of one shape and the first of the next gets the union of both, so the switch to a larger picture always happens early enough.
  - Segments shorter than the minimum length are merged into the neighbour that loses the least zoom, always taking the larger box. A brief full-frame shot widens the crop instead of being cut.
  - Every segment's box contains every keyframe inside it. The tests check this on keyframes measured from ten real films and episodes.
  - A file with more than 200 segments falls back to the whole-file crop.

Bars thinner than the minimum bar setting are dropped per side, both for the whole-file crop and for every segment.

## Standard aspect ratios

Films and series are made in a handful of shapes: 1.33 (4:3), 1.375, 1.43, 1.66, 1.78 (16:9), 1.85, 1.90, 2.00, 2.20, 2.35, 2.39, 2.40, 2.55 and 2.76. After the measurement, the whole-file crop and every segment are snapped to the nearest of these:

- **Only outward.** The long side keeps its measured extent and the short side grows evenly around the measured box until the ratio matches (shifted inside the frame at an edge). A measured 1920×802 (2.394:1) becomes 1920×804 (2.39:1), one row more on each side. No measured picture is ever cut.
- When the frame is too small to grow that far, the box stays as measured. 1920×961 would need 1922 columns for 2:1, so it stays 1920×961 (1.998:1).
- The ratio is the shape on screen: the stream's sample aspect ratio (SAR) from ffmpeg's output is applied, so an anamorphic DVD is judged by its display shape. Results scanned before this version have no SAR stored and are taken as square pixels until they are rescanned.

A measurement is **suspicious** when no standard ratio lies within 1.5% of it, its ratio is outside 1.25–2.90, or it leaves less than half of the frame. Bogus boxes like that come from files that are almost entirely dark, or from a few unrepresentative frames.

- A suspicious whole-file crop means **no crop** at all: the item plays uncropped, and the dashboard shows it as suspicious with the reason.
- A suspicious segment gets the whole-file crop, which contains every keyframe.
- The full frame (no bars) is never suspicious.

On the ten real fixtures only Interstellar's 2.39:1 scenes change (1920×802 → 1920×804); the other crops already sit on a standard ratio or can't grow without leaving the frame.

Results are stored per item (path, file size and modification time, frame size, sample aspect ratio, crop, segments, scan time, analysis version, source, the suspicious reason, or the failure reason) in `crops.json` in the plugin's data folder. The raw keyframe bounds of each scan (time and `x1 x2 y1 y2`, or nothing for a black keyframe) are kept beside it in `samples/{itemId}.json.gz`, about 12 KB for a three-hour film. Each write goes through a temp file that is then moved into place. A changed file is scanned again.

The keyframes make the analysis cheap to redo:

- **Re-analyse all** recomputes every result from them with the current settings, without ffmpeg. Results settled by trickplay are checked against the same rule again; one that no longer passes is dropped and queued for the keyframe scan.
- When an update changes the analysis, results from the older version are recomputed at startup and when the task runs. Results scanned before keyframes were kept are queued for a scan instead, and so are keyframe results with too few keyframes (see Detection).
- Keyframes are deleted together with the result when an item leaves the library.

## When scans run

- The scheduled task scans everything that has no result yet or whose file changed, and drops results for items that left the library.
- New or updated movies and episodes are queued into one background worker after a 30-second settle delay, so files that are still being copied aren't measured early.
- Only one ffmpeg runs at a time, at idle priority (nice 19 on Linux), and cancelling the task stops it. When the task and the queue pick the same item, the second one finds a current result and skips it, and an item waits in the queue only once.
- Decoding runs on the GPU Jellyfin uses for transcoding (VAAPI for Intel QSV/VAAPI on Linux, CUDA for NVENC, VideoToolbox on macOS; QSV itself for the 2-second pass, see Detection). Decoding is bit-exact, so the result is identical to the CPU; on an Intel N-series/Pentium iGPU HEVC scans about 3× faster. If the GPU can't decode a file, it is measured again on the CPU. Turn it off with "Decode on the GPU" in the settings.
- Virtual items, disc images and folders, `.strm` files and remote paths are skipped.

## Playback

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
| `POST /AutoCrop/Reanalyse` | administrator | Recomputes every result from its stored keyframes with the current settings. Queues items without stored keyframes, and trickplay results that no longer pass, for a scan. |
| `GET /AutoCrop/Stats` | administrator | Counts for the dashboard, including how many results were settled by trickplay and how many are suspicious. |
| `GET /AutoCrop/Results?filter=&search=&startIndex=&limit=` | administrator | Paged results for the dashboard (`filter`: `bars`, `per-scene`, `no-bars`, `suspicious`, `failed`). |
| `GET /AutoCrop/Web/autocrop.js` | anonymous | The player script. |

## Development

No local .NET needed:

```
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test -c Release
node test/crop-math.test.js
```

The end-to-end detection tests generate small clips with ffmpeg and run a real cropdetect pass over them: bars on every keyframe (8-bit and 10-bit), a clip with a full-frame part, a clip that switches between 2.39:1 and 1.90:1, a clip whose only keyframe is the first frame, and an unreadable file. The trickplay tests make sheets from generated clips the way Jellyfin does (`fps=1/10`, 320 px wide, `tile=10x10`) in a folder laid out like Jellyfin's: a clean clip is settled by trickplay, a clip with bars and a shape-switching clip get the keyframe scan, and so do missing and stale trickplay. They run when `ffmpeg` is on the `PATH` (or set `AUTOCROP_FFMPEG`) and are skipped otherwise. CI installs ffmpeg so they always run there.

`Jellyfin.Plugin.AutoCrop.Tests/Fixtures` holds the keyframe output of the plugin's ffmpeg command on ten real films and episodes (IMAX releases that switch shape, a scope-only release, and episodes with dark scenes and credits). The analysis is tested on them: one segment for the episodes, the right mattes for the IMAX films, and no keyframe's picture ever outside its segment.

The plugin targets net9.0 against the Jellyfin 10.11.9 SDK. That one build loads on 10.11 and on 12.x servers. `targetAbi.txt` must equal the SDK package version.

## Releasing

Publish a GitHub release whose tag is the version (`v1.0.0.0`) and whose text is the changelog. `.github/workflows/release.yml` tests and builds the tag, then attaches `autocrop-v1.0.0.0.zip` and an updated `manifest.json` to the release.
