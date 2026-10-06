# AutoCrop for Jellyfin

Removes black bars that are burned into the video itself, so the picture fills your screen in Jellyfin's web player.

Some files store their black bars inside the video: a widescreen film saved as a 16:9 frame with black rows above and below. On a 16:9 TV you won't notice, but on an **ultrawide (21:9) monitor** you get black bars on every side. Jellyfin's own Zoom and Stretch options can't fix that, because they don't know where the picture is. AutoCrop measures every file once and zooms the player so only the real picture fills the screen.

## Features

- **Exact.** Every file is measured, so nothing is guessed and no picture is ever cut off.
- **Follows IMAX scenes.** Films that switch between widescreen and taller IMAX scenes are followed scene by scene.
- **Only zooms when it helps.** On a normal 16:9 screen nothing changes.
- **Works in the background.** New films and episodes are measured automatically, usually in about a second.
- **Dashboard** with everything it found, plus settings.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```
   https://github.com/WouterStulp/jellyfin-plugin-autocrop/releases/latest/download/manifest.json
   ```
2. Install **AutoCrop** from the catalog and restart Jellyfin.
3. Reload Jellyfin in your browser.

The first scan of your library runs in the background. Most files are done in a second; files with black bars take up to a minute or two each. Works with Jellyfin 10.11.9 and newer, including 12.x.

## Using it

While watching in the browser, open the **⚙ settings → Aspect Ratio** and choose **Crop black bars**. Choose **Auto**, **Zoom** or **Stretch** to turn it off again. This is remembered in that browser, for everything you watch.

While it's on, each film or episode plays in the mode the server sets for it (normally per scene). Press **C** during playback to switch between:

| Mode | What you see |
| --- | --- |
| **Per scene** | The crop follows the picture shape, e.g. zooms out for IMAX scenes. |
| **Static** | One crop for the whole file. |
| **Off** | Normal playback. |

This choice is remembered in that browser for the whole series (or that film).

## Dashboard and settings

Go to **Dashboard → Plugins → AutoCrop**.

- **Overview:** every measured file with its picture size and shape. Search, filters, **Rescan** per file, and **Scan library now**.
- **Settings:**
  - default mode, and a mode per library
  - minimum bar size (1%)
  - minimum scene length (30 s)
  - zoom transition (300 ms)
  - zoom styled (ASS) subtitles with the picture (on)
  - GPU decoding (on)
  - use trickplay images to speed up scanning (on)

  After changing a setting, **Re-analyse all** applies it to everything in seconds.
- **Mode per series, film or episode:** the **Mode** column in the overview, and the **Series** filter to set a whole series at once. Useful for anime that use black bars as an effect in fight scenes (Black Clover, Jujutsu Kaisen): set them to **Static** so the picture doesn't zoom in during those scenes.

  A file uses its own mode, else its series', else its library's, else the default mode.

Files marked **suspicious** gave a result that doesn't look like any real film shape, so AutoCrop leaves them uncropped.

## Limitations

- **Browser only.** It works in Jellyfin's web player (also inside apps that use it, like Jellyfin Media Player). Android TV, Swiftfin, Infuse and Kodi aren't supported.
- **Blu-ray (PGS) and DVD subtitles** stay on the whole, unzoomed frame, because they are often placed in the black bars. Styled ASS/SSA subtitles zoom with the picture, so signs stay on their spot (turn that off in the settings). Normal subtitles are fine.

## More

- [How it works](docs/how-it-works.md): detection, trickplay pre-check, per-scene logic, API and development.
- [MIT license](LICENSE)
