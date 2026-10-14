// Run with: node test/crop-math.test.js
'use strict';

var assert = require('assert');
var crop = require('../Jellyfin.Plugin.AutoCrop/Web/autocrop.js');

var ULTRAWIDE = { w: 3440, h: 1440 };
var FULL_HD = { w: 1920, h: 1080 };
var TABLET = { w: 1024, h: 768 };

function transform(screen, box, video) {
    video = video || { w: 1920, h: 1080 };
    return crop.computeTransform(screen.w, screen.h, video.w, video.h, box, 1920, 1080);
}

function near(actual, expected, message) {
    assert.ok(Math.abs(actual - expected) < 0.01, message + ': expected ' + expected + ', got ' + actual);
}

// Where the picture area ends up on screen after the transform (origin at the element centre).
function onScreen(screen, box, t) {
    var k = Math.min(screen.w / 1920, screen.h / 1080);
    var s = t ? t.scale : 1;
    var x = t ? t.x : 0;
    var y = t ? t.y : 0;
    function map(px, py) {
        var ex = screen.w / 2 + (px - 960) * k;
        var ey = screen.h / 2 + (py - 540) * k;
        return { x: screen.w / 2 + x + s * (ex - screen.w / 2), y: screen.h / 2 + y + s * (ey - screen.h / 2) };
    }
    return { topLeft: map(box.x, box.y), bottomRight: map(box.x + box.width, box.y + box.height) };
}

var tests = {
    '2:1 in 16:9 on a 21:9 screen zooms until the picture fills the height': function () {
        var box = { x: 0, y: 60, width: 1920, height: 960 };
        var t = transform(ULTRAWIDE, box);
        near(t.scale, 1.125, 'scale');
        near(t.x, 0, 'x');
        near(t.y, 0, 'y');
        var area = onScreen(ULTRAWIDE, box, t);
        near(area.topLeft.y, 0, 'picture top');
        near(area.bottomRight.y, 1440, 'picture bottom');
        near(t.visibleBottom, 50 + 50 / 1.125, 'visible bottom');
    },

    'the same file on a 16:9 screen is left alone (s = 1)': function () {
        assert.strictEqual(transform(FULL_HD, { x: 0, y: 60, width: 1920, height: 960 }), null);
    },

    '2.39:1 in 16:9 on a 21:9 screen fills the width': function () {
        var box = { x: 0, y: 139, width: 1920, height: 802 };
        var t = transform(ULTRAWIDE, box);
        near(t.scale, (3440 / 1920) / (1440 / 1080), 'scale');
        var area = onScreen(ULTRAWIDE, box, t);
        near(area.topLeft.x, 0, 'picture left');
        near(area.bottomRight.x, 3440, 'picture right');
        assert.ok(area.topLeft.y >= 0 && area.bottomRight.y <= 1440, 'whole picture visible');
    },

    '4:3 pillarbox on a 4:3 screen fills the screen': function () {
        var box = { x: 240, y: 0, width: 1440, height: 1080 };
        var t = transform(TABLET, box);
        near(t.scale, 4 / 3, 'scale');
        var area = onScreen(TABLET, box, t);
        near(area.topLeft.x, 0, 'picture left');
        near(area.bottomRight.x, 1024, 'picture right');
    },

    '4:3 pillarbox on a wider screen needs no zoom': function () {
        assert.strictEqual(transform(ULTRAWIDE, { x: 240, y: 0, width: 1440, height: 1080 }), null);
    },

    'off-centre picture is shifted to the centre': function () {
        var box = { x: 0, y: 0, width: 1920, height: 960 };
        var t = transform(ULTRAWIDE, box);
        var area = onScreen(ULTRAWIDE, box, t);
        near(area.topLeft.y, 0, 'picture top');
        near(area.bottomRight.y, 1440, 'picture bottom');
        assert.ok(t.y > 0, 'moved down');
    },

    'no crop gives no transform': function () {
        assert.strictEqual(transform(ULTRAWIDE, { x: 0, y: 0, width: 1920, height: 1080 }), null);
        assert.strictEqual(crop.transformCss(null), '');
    },

    'a transcode to a lower resolution scales the box along': function () {
        var full = transform(ULTRAWIDE, { x: 0, y: 138, width: 1920, height: 804 });
        var scaled = transform(ULTRAWIDE, { x: 0, y: 138, width: 1920, height: 804 }, { w: 1280, h: 720 });
        near(scaled.scale, full.scale, 'same zoom at 720p');
    },

    'ASS/SSA canvases zoom with the video, bitmap and unknown canvases never do': function () {
        var css = 'translate(0.00px, 0.00px) scale(1.3333)';
        assert.strictEqual(crop.canvasTransform('libassjs-canvas', css, true), css, 'JavascriptSubtitlesOctopus');
        assert.strictEqual(crop.canvasTransform('foo libassjs-canvas', css, true), css, 'with another class');
        assert.strictEqual(crop.canvasTransform('libassjs-canvas', css, false), '', 'setting off');
        assert.strictEqual(crop.canvasTransform('libassjs-canvas', '', true), '', 'no zoom');
        assert.strictEqual(crop.canvasTransform('', css, true), '', 'libpgs and libbitsub canvases have no class');
        assert.strictEqual(crop.canvasTransform(undefined, css, true), '');
        assert.strictEqual(crop.canvasTransform('libassjs-canvas-parent', css, true), '', 'only the canvas itself');
    },

    'the c key cycles theatre, whole, off': function () {
        assert.strictEqual(crop.nextMode('theatre'), 'whole');
        assert.strictEqual(crop.nextMode('whole'), 'off');
        assert.strictEqual(crop.nextMode('off'), 'theatre');
    },

    'modes stored by earlier versions map onto the new ones': function () {
        assert.strictEqual(crop.normalizeMode('per-scene'), 'theatre', 'followed the scenes: theatre is the closest');
        assert.strictEqual(crop.normalizeMode('static'), 'whole', 'one crop that never cut');
        assert.strictEqual(crop.normalizeMode('theatre'), 'theatre');
        assert.strictEqual(crop.normalizeMode('whole'), 'whole');
        assert.strictEqual(crop.normalizeMode('off'), 'off');
        assert.strictEqual(crop.normalizeMode('zoom'), null);
        assert.strictEqual(crop.normalizeMode(null), null);
    },

    'the aspect-ratio sheet is recognised by its option ids, in any order and language': function () {
        assert.ok(crop.isAspectSheet(['auto', 'cover', 'fill']));
        assert.ok(crop.isAspectSheet(['fill', 'auto', 'cover']));
        assert.ok(!crop.isAspectSheet(['auto', 'cover']), 'missing fill');
        assert.ok(!crop.isAspectSheet(['auto', 'cover', 'fill', 'autocrop']), 'already extended');
        assert.ok(!crop.isAspectSheet(['0.5', '1', '1.25']), 'playback speed');
        assert.ok(!crop.isAspectSheet(['Auto', 'Cover', 'Fill']), 'labels are not ids');
        assert.ok(!crop.isAspectSheet([]));
        assert.ok(!crop.isAspectSheet(null));
    },

    'Crop black bars keeps the chosen mode, else the server default, never off': function () {
        assert.strictEqual(crop.enabledMode('whole', 'theatre'), 'whole');
        assert.strictEqual(crop.enabledMode('off', 'whole'), 'whole');
        assert.strictEqual(crop.enabledMode('off', 'static'), 'whole', 'a server default from an earlier version');
        assert.strictEqual(crop.enabledMode('off', 'off'), 'theatre');
        assert.strictEqual(crop.enabledMode('off', null), 'theatre');
    },

    'the viewer turning cropping off beats everything': function () {
        assert.strictEqual(crop.effectiveMode(false, 'whole', 'theatre'), 'off');
        assert.strictEqual(crop.effectiveMode(false, null, 'theatre'), 'off');
    },

    'while on, the series choice beats the server mode': function () {
        assert.strictEqual(crop.effectiveMode(true, 'whole', 'theatre'), 'whole');
        assert.strictEqual(crop.effectiveMode(true, 'theatre', 'off'), 'theatre', 'the viewer turned an off series on');
        assert.strictEqual(crop.effectiveMode(true, 'off', 'theatre'), 'off');
        assert.strictEqual(crop.effectiveMode(true, 'static', 'theatre'), 'whole', 'a choice stored by an earlier version');
    },

    'without a series choice the server mode plays, and off from the server does nothing': function () {
        assert.strictEqual(crop.effectiveMode(true, null, 'whole'), 'whole');
        assert.strictEqual(crop.effectiveMode(true, undefined, 'off'), 'off');
        assert.strictEqual(crop.effectiveMode(true, 'zoom', undefined), 'theatre');
        assert.strictEqual(crop.effectiveMode(true, null, 'per-scene'), 'theatre', 'a server default from an earlier version');
    }
};

var failed = 0;
Object.keys(tests).forEach(function (name) {
    try {
        tests[name]();
        console.log('ok   ' + name);
    } catch (e) {
        failed++;
        console.log('FAIL ' + name + '\n     ' + e.message);
    }
});
console.log(Object.keys(tests).length - failed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
