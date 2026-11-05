import test from 'node:test';
import assert from 'node:assert/strict';
import {
    UploadTiming,
    retryDelay,
    canRetry,
    waitForRetry,
} from '../DSFiles-Server/Web/upload-timing.js';

const kind = 'chunk:encrypted:server';

test('ETA learns transfer rate and fixed server overhead from confirmed history', () => {
    const history = new UploadTiming();
    assert.equal(history.predict(kind, 8000000), null);
    for (const megabytes of [1, 4, 8, 16, 20])
        history.add(kind, megabytes * 1000000, 2000 + megabytes * 500);
    const prediction = history.predict(kind, 8000000);
    assert.ok(Math.abs(prediction.seconds - 6) < 0.01);
    assert.ok(prediction.low < 6 && prediction.high > 6);
});

test('ETA resists a slow outlier and separates compressed uploads', () => {
    const history = new UploadTiming();
    for (const megabytes of [1, 4, 8, 16, 20, 4, 8, 16])
        history.add(kind, megabytes * 1000000, 2000 + megabytes * 500);
    history.add(kind, 8000000, 180000);
    history.add('compressed:encrypted:server', 8000000, 45000);
    assert.ok(history.predict(kind, 8000000).seconds < 10);
    assert.equal(history.predict('compressed:encrypted:server', 8000000).seconds, 45);
});

test('history is bounded, persists numeric observations and ignores damaged input', () => {
    const history = new UploadTiming();
    for (let i = 0; i < 90; i++) history.add(kind, 20971265, 1000 + i);
    assert.equal(history.samples.length, 60);
    assert.ok(history.serialize().length < 8192);
    const restored = new UploadTiming();
    restored.load(history.serialize());
    assert.equal(restored.samples.length, 60);
    assert.ok(restored.predict(kind, 20971265));
    restored.load('invalid-json');
    assert.equal(restored.samples.length, 60);
    restored.load(
        JSON.stringify([
            ['invalid', -1, -3, 0],
            ['chunk', 1, 0, Date.now()],
        ]),
    );
    assert.equal(restored.samples.length, 0);
    restored.load(JSON.stringify([[kind, 1000000, 2000, 1]]));
    assert.ok(Number.isFinite(restored.predict(kind, 1000000).seconds));
});

test('retry requires idempotency for ambiguous failures and obeys explicit blocking', () => {
    assert.equal(canRetry({ networkError: true }), false);
    assert.equal(canRetry({ networkError: true }, true), true);
    assert.equal(canRetry({ status: 502, retryBlocked: true }, true), false);
    assert.equal(canRetry({ status: 429 }), true);
    assert.equal(canRetry({ status: 409 }, true), false);
    assert.equal(canRetry({ status: 409, retryAfter: '1' }, true), true);
    assert.equal(canRetry({ name: 'AbortError', networkError: true }, true), false);
});

test('large transfers retain observations for access and finalization phases', () => {
    const history = new UploadTiming();
    for (const phase of ['proof', 'setup', 'finalize']) history.add(phase, 0, 200);
    for (let i = 0; i < 100; i++) history.add(kind, 20971265, 5000);
    assert.equal(history.samples.length, 60);
    for (const phase of ['proof', 'setup', 'finalize']) assert.ok(history.predict(phase, 0));
});

test('backoff respects Retry-After seconds and dates with bounded exponential growth', () => {
    const now = Date.now();
    assert.equal(
        retryDelay({}, 1, now, () => 0.5),
        1000,
    );
    assert.equal(
        retryDelay({}, 3, now, () => 0.5),
        4000,
    );
    assert.equal(
        retryDelay({ retryAfter: '300' }, 1, now, () => 0.5),
        300000,
    );
    assert.ok(
        retryDelay({ retryAfter: new Date(now + 10000).toUTCString() }, 1, now, () => 0.5) >= 9000,
    );
});

test('retry countdown stops immediately when cancelled', async () => {
    const controller = new AbortController();
    let ticks = 0;
    const waiting = waitForRetry(30000, controller.signal, () => ticks++);
    controller.abort();
    await assert.rejects(waiting, { name: 'AbortError' });
    assert.equal(ticks, 1);
});
