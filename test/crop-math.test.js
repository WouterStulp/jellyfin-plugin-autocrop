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

    'segmentAt finds the segment for a time and clamps outside the timeline': function () {
        var segments = [{ start: 0, end: 598 }, { start: 598, end: 900 }, { start: 900, end: 1500 }];
        assert.strictEqual(crop.segmentAt(segments, 0), 0);
        assert.strictEqual(crop.segmentAt(segments, 597.9), 0);
        assert.strictEqual(crop.segmentAt(segments, 598), 1);
        assert.strictEqual(crop.segmentAt(segments, 899.99), 1);
        assert.strictEqual(crop.segmentAt(segments, 900), 2);
        assert.strictEqual(crop.segmentAt(segments, 99999), 2);
        assert.strictEqual(crop.segmentAt(segments, -1), 0);
        assert.strictEqual(crop.segmentAt([{ start: 0, end: 10 }], 5), 0);
    },

    'the c key cycles per-scene, static, off': function () {
        assert.strictEqual(crop.nextMode('per-scene'), 'static');
        assert.strictEqual(crop.nextMode('static'), 'off');
        assert.strictEqual(crop.nextMode('off'), 'per-scene');
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
        assert.strictEqual(crop.enabledMode('static', 'per-scene'), 'static');
        assert.strictEqual(crop.enabledMode('off', 'static'), 'static');
        assert.strictEqual(crop.enabledMode('off', 'off'), 'per-scene');
        assert.strictEqual(crop.enabledMode('off', null), 'per-scene');
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
