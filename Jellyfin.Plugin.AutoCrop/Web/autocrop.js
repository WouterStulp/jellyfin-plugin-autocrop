/* AutoCrop for the Jellyfin web player: zooms the video so only the real picture fills the screen. */
(function () {
    'use strict';

    var MODES = ['per-scene', 'static', 'off'];
    var MODE_LABELS = { 'per-scene': 'per scene', 'static': 'whole film', 'off': 'off' };
    var STORAGE_KEY = 'autocrop.mode';
    var VIDEO_ID = /\/videos\/([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\//i;

    /**
     * The CSS transform that makes the picture area fill the element. Jellyfin shows the whole frame
     * with object-fit: contain, at scale k; the picture area alone would fit at k2. Scaling by k2 / k
     * around the element centre and shifting the area's centre onto it shows exactly the picture.
     * box is in the scanned frame's pixels (frameWidth x frameHeight), which can differ from the
     * decoded size (videoWidth x videoHeight) when the server transcodes to a lower resolution.
     * Returns null when nothing would change, e.g. a 2:1 picture in a 16:9 frame on a 16:9 screen.
     */
    function computeTransform(cw, ch, vw, vh, box, frameWidth, frameHeight) {
        if (!cw || !ch || !vw || !vh || !box || !frameWidth || !frameHeight) {
            return null;
        }

        var ax = box.x * vw / frameWidth;
        var ay = box.y * vh / frameHeight;
        var aw = box.width * vw / frameWidth;
        var ah = box.height * vh / frameHeight;
        if (aw <= 0 || ah <= 0) {
            return null;
        }

        var k = Math.min(cw / vw, ch / vh);
        var k2 = Math.min(cw / aw, ch / ah);
        var scale = k2 / k;
        var dx = (ax + aw / 2 - vw / 2) * k;
        var dy = (ay + ah / 2 - vh / 2) * k;
        var x = -dx * scale;
        var y = -dy * scale;

        if (Math.abs(scale - 1) < 0.001 && Math.abs(x) < 0.5 && Math.abs(y) < 0.5) {
            return null;
        }

        // The part of the untransformed element still on screen, in percent of its height. Native
        // subtitle cues are laid out in the element, so they are moved inside this range.
        var centre = 50 + 100 * dy / ch;
        return {
            scale: scale,
            x: x,
            y: y,
            visibleTop: Math.max(0, centre - 50 / scale),
            visibleBottom: Math.min(100, centre + 50 / scale)
        };
    }

    /** Index of the segment that contains time (seconds), clamped to the first and last. */
    function segmentAt(segments, time) {
        var low = 0;
        var high = segments.length - 1;
        while (low < high) {
            var mid = (low + high + 1) >> 1;
            if (segments[mid].start <= time) {
                low = mid;
            } else {
                high = mid - 1;
            }
        }
        return low;
    }

    function nextMode(mode) {
        return MODES[(MODES.indexOf(mode) + 1) % MODES.length];
    }

    function transformCss(transform) {
        return transform
            ? 'translate(' + transform.x.toFixed(2) + 'px, ' + transform.y.toFixed(2) + 'px) scale(' + transform.scale.toFixed(4) + ')'
            : '';
    }

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = { computeTransform: computeTransform, segmentAt: segmentAt, nextMode: nextMode, transformCss: transformCss };
        return;
    }

    var state = {
        video: null,
        source: '',
        itemId: null,
        data: null,
        loading: false,
        lastSessionCheck: 0,
        autoAspect: true,
        mode: null,
        seeking: false,
        css: '',
        range: null
    };

    function viewerMode() {
        try {
            var stored = window.localStorage.getItem(STORAGE_KEY);
            if (MODES.indexOf(stored) >= 0) {
                return stored;
            }
        } catch (e) {
            // Storage blocked: fall back to the choice made on this page.
        }
        return state.mode;
    }

    function currentMode() {
        return viewerMode() || (state.data && state.data.defaultMode) || 'per-scene';
    }

    function load(itemId, source) {
        var client = window.ApiClient;
        if (!client) {
            return;
        }

        state.loading = true;
        client.getJSON(client.getUrl('AutoCrop/Items/' + itemId)).then(function (data) {
            return data;
        }, function () {
            return null;
        }).then(function (data) {
            state.loading = false;
            if (state.source === source) {
                state.itemId = itemId;
                state.data = data;
            }
        });
    }

    // Transcodes play from a blob: URL that hides the item id; the session knows what is playing.
    function resolveFromSession(source) {
        var client = window.ApiClient;
        if (!client || !client.getSessions || Date.now() - state.lastSessionCheck < 3000) {
            return;
        }

        state.lastSessionCheck = Date.now();
        client.getSessions({ deviceId: client.deviceId() }).then(function (sessions) {
            var playing = null;
            for (var i = 0; i < (sessions || []).length; i++) {
                if (sessions[i].NowPlayingItem) {
                    playing = sessions[i].NowPlayingItem.Id;
                }
            }
            var id = playing ? playing.replace(/-/g, '').toLowerCase() : null;
            if (id && id !== state.itemId && state.source === source && !state.loading) {
                load(id, source);
            }
        }, function () {});
    }

    function showingCues(video) {
        var cues = [];
        var tracks = video.textTracks || [];
        for (var i = 0; i < tracks.length; i++) {
            var list = tracks[i].mode === 'showing' && tracks[i].cues ? tracks[i].cues : [];
            for (var j = 0; j < list.length; j++) {
                cues.push(list[j]);
            }
        }
        return cues;
    }

    // Native cues are laid out inside the video element, so they zoom with it and would end up off
    // screen. Pin them inside the part of the element that is still visible; restore them after.
    function placeCues(video, range) {
        var cues = showingCues(video);
        for (var i = 0; i < cues.length; i++) {
            var cue = cues[i];
            var original = cue.autocropOriginal;
            if (!range) {
                if (original) {
                    cue.snapToLines = original.snapToLines;
                    cue.line = original.line;
                    cue.lineAlign = original.lineAlign;
                    cue.autocropOriginal = null;
                }
                continue;
            }

            if (!original) {
                original = { snapToLines: cue.snapToLines, line: cue.line, lineAlign: cue.lineAlign };
                cue.autocropOriginal = original;
            }
            var fromTop = original.snapToLines && typeof original.line === 'number' && original.line >= 0;
            cue.snapToLines = false;
            cue.lineAlign = fromTop ? 'start' : 'end';
            cue.line = fromTop ? range.visibleTop + 2 : range.visibleBottom - 4;
        }
    }

    // ASS and PGS subtitles draw on canvases beside the video, placed by their renderers from the
    // video's size. Keep them over the whole frame as Jellyfin shows it without the zoom, so they
    // stay on screen and readable (positioned signs won't line up with the zoomed picture).
    function placeCanvases(video) {
        var container = video.parentNode;
        if (!container || !video.videoWidth) {
            return;
        }

        var cw = video.clientWidth;
        var ch = video.clientHeight;
        var k = Math.min(cw / video.videoWidth, ch / video.videoHeight);
        var width = Math.round(video.videoWidth * k) + 'px';
        var height = Math.round(video.videoHeight * k) + 'px';
        var containerRect = container.getBoundingClientRect();
        var canvases = container.querySelectorAll('canvas');
        for (var i = 0; i < canvases.length; i++) {
            var canvas = canvases[i];
            var parentRect = (canvas.offsetParent || container).getBoundingClientRect();
            var left = Math.round(containerRect.left + video.offsetLeft + (cw - video.videoWidth * k) / 2 - parentRect.left) + 'px';
            var top = Math.round(containerRect.top + video.offsetTop + (ch - video.videoHeight * k) / 2 - parentRect.top) + 'px';
            if (canvas.style.left !== left || canvas.style.top !== top || canvas.style.width !== width) {
                canvas.style.left = left;
                canvas.style.top = top;
                canvas.style.width = width;
                canvas.style.height = height;
            }
        }
    }

    function apply(video, transform, transitionMs) {
        var css = transformCss(transform);
        if (css === state.css) {
            if (css) {
                placeCanvases(video);
            }
            return;
        }

        // Animate only between two zoom levels during playback; jump when seeking, starting or stopping.
        var animate = transitionMs > 0 && !state.seeking && state.css && css;
        video.style.transition = animate ? 'transform ' + transitionMs + 'ms ease-in-out' : '';
        video.style.transformOrigin = css ? '50% 50%' : '';
        video.style.transform = css;
        if (video.parentNode) {
            video.parentNode.style.overflow = css ? 'hidden' : '';
        }

        state.css = css;
        state.range = transform ? { visibleTop: transform.visibleTop, visibleBottom: transform.visibleBottom } : null;
        placeCues(video, state.range);
        placeCanvases(video);
    }

    function update() {
        var video = state.video;
        if (!video) {
            return;
        }

        var data = state.data;
        var mode = currentMode();
        if (!data || mode === 'off' || !state.autoAspect || !video.videoWidth) {
            apply(video, null, 0);
            return;
        }

        var box = data.crop;
        if (mode === 'per-scene' && data.segments && data.segments.length) {
            box = data.segments[segmentAt(data.segments, video.currentTime)].box;
        }

        var transform = computeTransform(video.clientWidth, video.clientHeight, video.videoWidth, video.videoHeight,
            box, data.frameWidth, data.frameHeight);
        apply(video, transform, data.transitionMs);
    }

    function onCueChange() {
        if (state.video && state.range) {
            placeCues(state.video, state.range);
        }
    }

    function detach() {
        if (state.video) {
            apply(state.video, null, 0);
        }
        state.video = null;
        state.source = '';
        state.itemId = null;
        state.data = null;
    }

    // Every second: find the player, notice a new item (next episode, autoplay) and follow Jellyfin's
    // aspect-ratio setting. Cover and Fill set object-fit on the video; Auto leaves it at contain.
    function tick() {
        var video = document.querySelector('.videoPlayerContainer video');
        if (video !== state.video) {
            detach();
            state.video = video;
            if (video && !video.autocropListening) {
                video.autocropListening = true;
                video.addEventListener('timeupdate', update);
                video.addEventListener('loadedmetadata', update);
                video.addEventListener('seeking', function () {
                    state.seeking = true;
                });
                video.addEventListener('seeked', function () {
                    update();
                    state.seeking = false;
                });
            }
        }
        if (!video) {
            return;
        }

        state.autoAspect = window.getComputedStyle(video).objectFit === 'contain';

        var tracks = video.textTracks || [];
        for (var i = 0; i < tracks.length; i++) {
            if (!tracks[i].autocropListening) {
                tracks[i].autocropListening = true;
                tracks[i].addEventListener('cuechange', onCueChange);
            }
        }

        var source = video.currentSrc || video.src || '';
        if (source !== state.source) {
            apply(video, null, 0);
            state.source = source;
            state.itemId = null;
            state.data = null;
            state.lastSessionCheck = 0;
        }
        if (!source || state.loading) {
            return;
        }

        var match = VIDEO_ID.exec(source);
        if (match) {
            if (!state.itemId) {
                load(match[1].replace(/-/g, '').toLowerCase(), source);
            }
        } else if (!state.itemId || Date.now() - state.lastSessionCheck > 10000) {
            resolveFromSession(source);
        }
        update();
        // Picks up a subtitle track loaded after the zoom was applied.
        onCueChange();
    }

    // Per frame so a scene change, a resize or fullscreen is picked up at once; the video events and
    // the tick cover browsers that throttle animation frames. Cheap: the DOM is only touched when the
    // transform actually changes.
    function frame() {
        update();
        window.requestAnimationFrame(frame);
    }

    var toastTimer = null;

    function toast(text) {
        var el = document.getElementById('autocropToast');
        if (!el) {
            el = document.createElement('div');
            el.id = 'autocropToast';
            el.style.cssText = 'position:fixed;top:12%;left:50%;transform:translateX(-50%);z-index:2147483647;'
                + 'padding:.5em 1em;border-radius:.4em;background:rgba(0,0,0,.75);color:#fff;font-size:1.3em;'
                + 'pointer-events:none;transition:opacity .3s;';
            document.body.appendChild(el);
        }
        el.textContent = text;
        el.style.opacity = '1';
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () {
            el.style.opacity = '0';
        }, 1500);
    }

    function onKeyDown(e) {
        var target = e.target || {};
        var editing = target.isContentEditable || /^(input|textarea|select)$/i.test(target.tagName || '');
        if (!state.video || editing || e.ctrlKey || e.altKey || e.metaKey || (e.key !== 'c' && e.key !== 'C')) {
            return;
        }

        var mode = nextMode(currentMode());
        state.mode = mode;
        try {
            window.localStorage.setItem(STORAGE_KEY, mode);
        } catch (err) {
            // Storage blocked: state.mode keeps the choice until the page reloads.
        }
        toast('Auto-crop: ' + MODE_LABELS[mode]);
        update();
    }

    document.addEventListener('keydown', onKeyDown, true);
    setInterval(tick, 1000);
    window.requestAnimationFrame(frame);
})();
