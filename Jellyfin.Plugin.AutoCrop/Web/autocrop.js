/* AutoCrop for the Jellyfin web player: zooms the video so only the real picture fills the screen. */
(function () {
    'use strict';

    var MODES = ['per-scene', 'static', 'off'];
    var MODE_LABELS = { 'per-scene': 'per scene', 'static': 'whole film', 'off': 'off' };
    var ENABLED_KEY = 'autocrop.enabled';
    var LEGACY_MODE_KEY = 'autocrop.mode';
    var SERIES_KEY = 'autocrop.series.';
    var ASPECT_IDS = ['auto', 'cover', 'fill'];
    var MENU_LABEL = 'Crop black bars';
    // The canvas JavascriptSubtitlesOctopus (@jellyfin/libass-wasm) draws ASS/SSA on, in jellyfin-web
    // 10.11 and 12. PGS and VobSub (libpgs, libbitsub) draw on canvases without a class.
    var ASS_CANVAS_CLASS = 'libassjs-canvas';
    var VIDEO_EVENTS = ['timeupdate', 'loadedmetadata', 'play', 'playing', 'pause', 'ratechange'];
    // Lands just past a boundary rather than just before it, where nothing would change yet.
    var BOUNDARY_MARGIN_MS = 15;
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

    /**
     * Milliseconds of wall-clock time until playback at rate reaches the start of the segment after
     * time (seconds), plus a small margin; null when there is none ahead or playback doesn't advance.
     */
    function nextBoundaryDelay(segments, time, rate) {
        if (!segments || segments.length < 2 || !(rate > 0)) {
            return null;
        }
        var next = segmentAt(segments, time) + 1;
        if (next >= segments.length) {
            return null;
        }
        return Math.max(0, (segments[next].start - time) * 1000 / rate) + BOUNDARY_MARGIN_MS;
    }

    function nextMode(mode) {
        return MODES[(MODES.indexOf(mode) + 1) % MODES.length];
    }

    /**
     * Whether an action sheet's option ids (data-id) are Jellyfin's aspect-ratio choices: exactly
     * auto, cover and fill, in any order. The ids don't depend on the interface language.
     */
    function isAspectSheet(ids) {
        if (!ids || ids.length !== ASPECT_IDS.length) {
            return false;
        }
        for (var i = 0; i < ASPECT_IDS.length; i++) {
            if (ids.indexOf(ASPECT_IDS[i]) < 0) {
                return false;
            }
        }
        return true;
    }

    /**
     * The mode that plays: off when the viewer turned cropping off (Auto, Cover or Fill), else their own
     * choice for this series or movie (the c key), else the server's mode for the item.
     */
    function effectiveMode(enabled, seriesMode, serverMode) {
        if (!enabled) {
            return 'off';
        }
        if (MODES.indexOf(seriesMode) >= 0) {
            return seriesMode;
        }
        return MODES.indexOf(serverMode) >= 0 ? serverMode : 'per-scene';
    }

    /**
     * Older versions kept one global mode in the browser. It becomes the on/off flag: a stored "off"
     * stays off, any other mode means on, which now plays the server's mode for each item.
     */
    function migrateStorage(storage) {
        var legacy = storage.getItem(LEGACY_MODE_KEY);
        if (legacy === null) {
            return;
        }
        if (storage.getItem(ENABLED_KEY) === null) {
            storage.setItem(ENABLED_KEY, legacy === 'off' ? 'off' : 'on');
        }
        storage.removeItem(LEGACY_MODE_KEY);
    }

    /** The mode "Crop black bars" turns on: the current one when it is on, else the server default, else per scene. */
    function enabledMode(mode, defaultMode) {
        if (mode && mode !== 'off') {
            return mode;
        }
        return defaultMode && defaultMode !== 'off' ? defaultMode : 'per-scene';
    }

    function transformCss(transform) {
        return transform
            ? 'translate(' + transform.x.toFixed(2) + 'px, ' + transform.y.toFixed(2) + 'px) scale(' + transform.scale.toFixed(4) + ')'
            : '';
    }

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = {
            computeTransform: computeTransform,
            segmentAt: segmentAt,
            nextBoundaryDelay: nextBoundaryDelay,
            canvasTransform: canvasTransform,
            nextMode: nextMode,
            transformCss: transformCss,
            isAspectSheet: isAspectSheet,
            enabledMode: enabledMode,
            effectiveMode: effectiveMode,
            migrateStorage: migrateStorage
        };
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
        enabled: true,
        seriesModes: {},
        seeking: false,
        boundaryTimer: null,
        css: '',
        range: null,
        choosingCrop: false
    };

    // Storage can be blocked; then the choices made on this page (state) last until it reloads.
    function stored(key) {
        try {
            return window.localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function store(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch (e) {
            // Storage blocked: the state keeps the choice.
        }
    }

    function viewerEnabled() {
        var flag = stored(ENABLED_KEY);
        return flag === 'on' || flag === 'off' ? flag === 'on' : state.enabled;
    }

    function seriesMode() {
        var id = state.data && state.data.seriesId;
        return id ? stored(SERIES_KEY + id) || state.seriesModes[id] : null;
    }

    function currentMode() {
        return effectiveMode(viewerEnabled(), seriesMode(), state.data && state.data.defaultMode);
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
                refresh();
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

    /**
     * The transform for a subtitle canvas: the video's own for ASS/SSA, so positioned signs stay on
     * their spot in the picture; none for anything else. Bitmap subtitles (PGS, VobSub) are often
     * placed inside the black bars and would be pushed off screen, and an unknown canvas is left alone.
     */
    function canvasTransform(className, videoCss, zoomStyled) {
        return zoomStyled && (' ' + (className || '') + ' ').indexOf(' ' + ASS_CANVAS_CLASS + ' ') >= 0 ? videoCss : '';
    }

    // Subtitle renderers draw on canvases beside the video, placed from the video's size, some from its
    // transformed bounds. Keep them over the whole frame as Jellyfin shows it without the zoom. The
    // ASS canvas then gets the video's transform around the same centre, so it zooms exactly along.
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

            // Compared with what was set, not read back: browsers rewrite transform strings.
            var transform = canvasTransform(canvas.className, state.css, state.data && state.data.zoomStyledSubtitles);
            if ((canvas.autocropTransform || '') !== transform) {
                canvas.autocropTransform = transform;
                canvas.style.transition = transform ? video.style.transition : '';
                canvas.style.transformOrigin = transform ? '50% 50%' : '';
                canvas.style.transform = transform;
            }
        }
    }

    // A renderer that adds or moves its canvas (a subtitle track chosen, its own resize) would put it
    // over the zoomed video's bounds; put it back while zoomed.
    function onPlayerMutations(mutations) {
        if (!state.css || !state.video) {
            return;
        }
        for (var i = 0; i < mutations.length; i++) {
            if (mutations[i].addedNodes.length || mutations[i].target.tagName === 'CANVAS') {
                placeCanvases(state.video);
                return;
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

    // One timer for the next segment boundary, from the playback position and rate. Every event that
    // can move either (seeking, pausing, a rate change, timeupdate) sets it again.
    function schedule() {
        clearTimeout(state.boundaryTimer);
        state.boundaryTimer = null;
        var video = state.video;
        var data = state.data;
        if (!video || video.paused || !data || currentMode() !== 'per-scene') {
            return;
        }

        var delay = nextBoundaryDelay(data.segments, video.currentTime, video.playbackRate);
        if (delay !== null) {
            state.boundaryTimer = setTimeout(refresh, delay);
        }
    }

    function refresh() {
        update();
        schedule();
    }

    function onCueChange() {
        if (state.video && state.range) {
            placeCues(state.video, state.range);
        }
    }

    function detach() {
        clearTimeout(state.boundaryTimer);
        if (state.video) {
            apply(state.video, null, 0);
        }
        state.video = null;
        state.source = '';
        state.itemId = null;
        state.data = null;
    }

    function listen(video) {
        video.autocropListening = true;
        for (var i = 0; i < VIDEO_EVENTS.length; i++) {
            video.addEventListener(VIDEO_EVENTS[i], refresh);
        }
        video.addEventListener('seeking', function () {
            state.seeking = true;
            schedule();
        });
        video.addEventListener('seeked', function () {
            refresh();
            state.seeking = false;
        });
        // The player can change size without the window doing so.
        if (window.ResizeObserver) {
            new window.ResizeObserver(refresh).observe(video);
        }
        if (window.MutationObserver && video.parentNode) {
            new window.MutationObserver(onPlayerMutations).observe(video.parentNode,
                { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
        }
    }

    // Every second: find the player, notice a new item (next episode, autoplay) and follow Jellyfin's
    // aspect-ratio setting. Cover and Fill set object-fit on the video; Auto leaves it at contain.
    // While playing it also updates, a safety net for background tabs that throttle timers.
    function tick() {
        var video = document.querySelector('.videoPlayerContainer video');
        if (video !== state.video) {
            detach();
            state.video = video;
            if (video && !video.autocropListening) {
                listen(video);
            }
        }
        if (!video) {
            return;
        }

        var autoAspect = window.getComputedStyle(video).objectFit === 'contain';
        var aspectChanged = autoAspect !== state.autoAspect;
        state.autoAspect = autoAspect;

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
        if (!video.paused || aspectChanged) {
            refresh();
        }
        // Picks up a subtitle track loaded after the zoom was applied.
        onCueChange();
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

    // The aspect-ratio menu turns cropping on or off in this browser, for everything.
    function setEnabled(on) {
        state.enabled = on;
        store(ENABLED_KEY, on ? 'on' : 'off');
    }

    // The c key picks the mode for this series (or movie) only, so a preference for one show sticks.
    function setSeriesMode(mode) {
        var id = state.data && state.data.seriesId;
        if (id) {
            state.seriesModes[id] = mode;
            store(SERIES_KEY + id, mode);
        }
    }

    function showMode() {
        toast('Auto-crop: ' + MODE_LABELS[currentMode()]);
        refresh();
    }

    function onKeyDown(e) {
        var target = e.target || {};
        var editing = target.isContentEditable || /^(input|textarea|select)$/i.test(target.tagName || '');
        if (!state.video || editing || e.ctrlKey || e.altKey || e.metaKey || (e.key !== 'c' && e.key !== 'C')) {
            return;
        }
        if (!state.data) {
            toast('Auto-crop: no black bars to crop');
            return;
        }

        var mode = nextMode(currentMode());
        setSeriesMode(mode);
        if (mode !== 'off') {
            setEnabled(true);
        }
        showMode();
    }

    function menuElement(tag, className) {
        var el = document.createElement(tag);
        el.className = className;
        return el;
    }

    /**
     * Adds "Crop black bars" to Jellyfin's aspect-ratio sheet (Auto, Cover, Fill, recognised by their
     * data-id). Choosing it clicks Jellyfin's own Auto, so the aspect setting becomes Auto and the
     * sheet closes as usual, then turns AutoCrop on. Auto, Cover and Fill turn it off. Any other sheet,
     * or one that looks different, is left alone.
     */
    function extendAspectSheet(sheet) {
        var items = sheet.querySelectorAll('.actionSheetMenuItem[data-id]');
        var ids = [];
        for (var i = 0; i < items.length; i++) {
            ids.push(items[i].getAttribute('data-id'));
        }
        if (!state.video || sheet.autocropExtended || !isAspectSheet(ids)) {
            return;
        }
        sheet.autocropExtended = true;

        var auto = items[ids.indexOf('auto')];
        var last = items[items.length - 1];
        var active = window.getComputedStyle(state.video).objectFit === 'contain' && currentMode() !== 'off';

        var item = menuElement('button', last.className);
        item.type = 'button';
        item.setAttribute('data-id', 'autocrop');

        // Jellyfin marks the selected option with a check icon and keeps a hidden one on the others.
        var nativeIcon = last.querySelector('.actionsheetMenuItemIcon');
        if (nativeIcon) {
            var icon = menuElement('span', 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons check');
            icon.setAttribute('aria-hidden', 'true');
            icon.style.visibility = active ? '' : 'hidden';
            item.appendChild(icon);
            var autoIcon = auto.querySelector('.actionsheetMenuItemIcon');
            if (active && autoIcon) {
                autoIcon.style.visibility = 'hidden';
            }
        }

        var body = menuElement('div', 'listItemBody actionsheetListItemBody');
        var text = menuElement('div', 'listItemBodyText actionSheetItemText');
        text.textContent = MENU_LABEL;
        body.appendChild(text);
        item.appendChild(body);

        for (var j = 0; j < items.length; j++) {
            items[j].addEventListener('click', function () {
                if (!state.choosingCrop && currentMode() !== 'off') {
                    setEnabled(false);
                    showMode();
                }
            });
        }

        item.addEventListener('click', function (e) {
            // Jellyfin's own click handler would store "autocrop" as the aspect ratio.
            e.stopPropagation();
            state.choosingCrop = true;
            auto.click();
            state.choosingCrop = false;
            setEnabled(true);
            if (currentMode() === 'off' && state.data) {
                setSeriesMode(enabledMode('off', state.data.defaultMode));
            }
            showMode();
        });

        last.parentNode.insertBefore(item, last.nextSibling);
    }

    // Jellyfin appends every dialog, the player's action sheets included, to the body.
    function onDialogs(mutations) {
        for (var i = 0; i < mutations.length; i++) {
            var added = mutations[i].addedNodes;
            for (var j = 0; j < added.length; j++) {
                var node = added[j];
                var sheet = node.nodeType === 1
                    && (node.classList.contains('actionSheet') ? node : node.querySelector('.actionSheet'));
                if (sheet) {
                    extendAspectSheet(sheet);
                }
            }
        }
    }

    try {
        migrateStorage(window.localStorage);
    } catch (e) {
        // Storage blocked: nothing stored to migrate.
    }
    document.addEventListener('keydown', onKeyDown, true);
    if (window.MutationObserver && document.body) {
        new window.MutationObserver(onDialogs).observe(document.body, { childList: true });
    }
    window.addEventListener('resize', refresh);
    document.addEventListener('fullscreenchange', refresh);
    document.addEventListener('webkitfullscreenchange', refresh);
    setInterval(tick, 1000);
})();
