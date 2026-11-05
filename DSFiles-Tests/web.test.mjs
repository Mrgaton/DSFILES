import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { Catalog, CACHE_KEY, seal, unseal } from '../DSFiles-Server/Web/catalog.js';
import { GOOGLE_CLIENT_ID } from '../DSFiles-Server/Web/config.js';
import { safeName, apiError } from '../DSFiles-Server/Web/web-utils.js';

const SETTINGS = 'dsfiles.webhook.v1',
    PREFERENCES = 'dsfiles.preferences.v1',
    SESSION = 'dsfiles.google-session.v1';
const password = 'local-test-password-123';
const record = {
    seed: 'test_seed',
    name: 'test.txt',
    size: 10,
    removeToken: 'test:token:value',
    redirect: false,
};
function storage() {
    const values = new Map();
    const api = {
        values,
        fail: null,
        getItem: (key) => values.get(key) ?? null,
        setItem(key, value) {
            if (api.fail?.(key, value)) throw Error('QuotaExceededError');
            values.set(key, String(value));
        },
        removeItem: (key) => values.delete(key),
    };
    globalThis.localStorage = api;
    return api;
}
function catalog() {
    const store = new Catalog();
    store.schedule = () => {};
    return store;
}
Object.defineProperty(globalThis, 'navigator', { value: {}, configurable: true });

test('migrates legacy settings, preserves catalog values and removes duplicate caches', async () => {
    const disk = storage();
    disk.setItem(
        SETTINGS,
        JSON.stringify({ mode: 'custom', webhook: 'test-webhook-secret', encrypt: false }),
    );
    disk.setItem(
        PREFERENCES,
        JSON.stringify({
            'animation-url': 'https://example.test/private-image',
            'library-sort': 'name',
        }),
    );
    const store = catalog();
    await store.unlock();
    assert.equal(store.preferences.webhook.value, 'test-webhook-secret');
    assert.equal(store.preferences['library-sort'].value, 'name');
    assert.equal(disk.getItem(SETTINGS), null);
    assert.equal(disk.getItem(PREFERENCES), null);
    await store.setPreferences({ 'library-sort': 'size' });
    disk.setItem(PREFERENCES, JSON.stringify({ 'library-sort': 'recent' }));
    const reopened = catalog();
    await reopened.unlock();
    assert.equal(reopened.preferences['library-sort'].value, 'size');
});

test('encrypts records, settings and local OAuth session; exports and Drive omit OAuth', async () => {
    const disk = storage(),
        store = catalog();
    await store.unlock();
    await store.add(record);
    await store.setPreferences({ webhook: 'test-webhook-secret' });
    await store.setToken('test-oauth-secret', 3600);
    assert.ok(disk.getItem(SESSION).includes('test-oauth-secret'));
    await store.setEncryption(true, password);
    assert.equal(store.encrypted, true);
    assert.equal(disk.getItem(SESSION), null);
    for (const value of disk.values.values()) {
        assert.equal(value.includes('test-webhook-secret'), false);
        assert.equal(value.includes('test-oauth-secret'), false);
        assert.equal(value.includes('test_seed'), false);
    }
    const local = await unseal(disk.getItem(CACHE_KEY), password);
    assert.equal(local.localSession.token, 'test-oauth-secret');
    assert.equal(local.entries[0].seed, record.seed);
    const backup = await unseal(await store.export(), password);
    assert.equal(backup.localSession, undefined);
    assert.equal(backup.preferences.webhook.value, 'test-webhook-secret');
    const restored = catalog();
    assert.equal(restored.restoreToken(), false);
    assert.equal(restored.token, null);
    assert.equal((await unseal(await restored.export(), password)).localSession, undefined);
    await restored.unlock(password);
    assert.equal(restored.token, 'test-oauth-secret');
    const uploaded = [];
    restored.api = async (url, options = {}) => {
        if (url.includes('uploadType=multipart')) {
            uploaded.push(options.body);
            return { json: async () => ({ id: 'new-snapshot' }) };
        }
        return { json: async () => ({ files: [] }) };
    };
    await restored.sync();
    const encryptedPayload = uploaded[0]
        .split('Content-Type: application/json\r\n\r\n')[1]
        .split('\r\n--')[0];
    assert.equal((await unseal(encryptedPayload, password)).localSession, undefined);
});

test('old encrypted catalogs migrate plaintext leftovers only after a valid unlock', async () => {
    const disk = storage(),
        store = catalog();
    await store.unlock();
    await store.setEncryption(true, password);
    disk.setItem(SETTINGS, JSON.stringify({ webhook: 'legacy-secret', mode: 'custom' }));
    disk.setItem(
        SESSION,
        JSON.stringify({
            clientId: GOOGLE_CLIENT_ID,
            token: 'legacy-token',
            expires: Date.now() + 3600000,
        }),
    );
    const reopened = catalog();
    assert.equal(reopened.restoreToken(), false);
    assert.equal(reopened.token, null);
    await assert.rejects(reopened.unlock(), (e) => e.code === 'PASSWORD_REQUIRED');
    assert.notEqual(disk.getItem(SETTINGS), null);
    await reopened.unlock(password);
    assert.equal(disk.getItem(SETTINGS), null);
    assert.equal(disk.getItem(SESSION), null);
    assert.equal(reopened.preferences.webhook.value, 'legacy-secret');
    assert.equal(reopened.token, 'legacy-token');
});

test('failed encryption writes leave protection, password and saved data unchanged', async () => {
    for (const failingKey of ['dsfiles.catalog-pending.v1', CACHE_KEY]) {
        const disk = storage(),
            store = catalog();
        await store.unlock();
        await store.add(record);
        const before = disk.getItem(CACHE_KEY),
            pending = store.pending;
        disk.fail = (key) => key === failingKey;
        await assert.rejects(store.setEncryption(true, password), /QuotaExceededError/);
        assert.equal(store.encrypted, false);
        assert.equal(store.password, null);
        assert.equal(store.salt, null);
        assert.equal(store.memoryDirty, false);
        assert.equal(store.pending, pending);
        assert.equal(disk.getItem(CACHE_KEY), before);
        disk.fail = null;
        await store.setEncryption(true, password);
        assert.equal(store.encrypted, true);
    }
});

test('failed decryption conversion keeps the encrypted catalog and usable key', async () => {
    const disk = storage(),
        store = catalog();
    await store.unlock();
    await store.add(record);
    await store.setEncryption(true, password);
    const before = disk.getItem(CACHE_KEY),
        key = store.key;
    disk.fail = (k) => k === CACHE_KEY;
    await assert.rejects(store.setEncryption(false), /QuotaExceededError/);
    assert.equal(store.key, key);
    assert.equal(store.encrypted, true);
    assert.equal(store.allowPlainMigration, false);
    assert.equal(disk.getItem(CACHE_KEY), before);
    disk.fail = null;
    await store.setEncryption(false);
    assert.equal(store.encrypted, false);
    assert.equal((await unseal(disk.getItem(CACHE_KEY))).entries.length, 1);
});

test('failed migration retains old settings for recovery', async () => {
    const disk = storage();
    disk.setItem(SETTINGS, JSON.stringify({ webhook: 'legacy-secret' }));
    disk.fail = (k) => k === CACHE_KEY;
    await assert.rejects(catalog().unlock(), /QuotaExceededError/);
    assert.ok(disk.getItem(SETTINGS).includes('legacy-secret'));
});

test('lock clears keys and removes saved Google authorization from the encrypted cache', async () => {
    const disk = storage(),
        store = catalog();
    await store.unlock();
    await store.setEncryption(true, password);
    await store.setToken('test-token', 3600);
    await store.lock();
    assert.equal(store.unlocked, false);
    assert.equal(store.key, null);
    assert.equal(store.password, null);
    assert.equal(store.token, null);
    assert.deepEqual(store.entries, []);
    assert.deepEqual(store.preferences, {});
    assert.equal((await unseal(disk.getItem(CACHE_KEY), password)).localSession, undefined);
    const reopened = catalog();
    await reopened.unlock(password);
    assert.equal(reopened.token, null);
});

test('connecting Google while locked never persists a plaintext token', async () => {
    const disk = storage(),
        store = catalog();
    await store.unlock();
    await store.setEncryption(true, password);
    await store.lock();
    const reopened = catalog();
    await reopened.setToken('new-account-token', 3600);
    assert.equal(disk.getItem(SESSION), null);
    assert.equal(disk.getItem(CACHE_KEY).includes('new-account-token'), false);
    await reopened.unlock(password);
    assert.equal(reopened.token, 'new-account-token');
    assert.equal(
        (await unseal(disk.getItem(CACHE_KEY), password)).localSession.token,
        'new-account-token',
    );
});

test('remote plaintext snapshots are migrated and cleaned up without exporting local secrets', async () => {
    storage();
    const store = catalog();
    await store.unlock();
    await store.setEncryption(true, password);
    const remote = await seal(
        [{ ...record, id: record.seed, createdAt: 1, deleted: false }],
        null,
        null,
        { webhook: { value: 'remote-secret', time: 1 } },
    );
    let written = '',
        deleted = false;
    store.api = async (url, options = {}) => {
        if (options.method === 'DELETE') {
            deleted = true;
            assert.ok(written);
            return {};
        }
        if (options.method === 'POST') {
            written = options.body;
            return { json: async () => ({ id: 'encrypted-copy' }) };
        }
        if (url.includes('alt=media')) return { text: async () => remote };
        return { json: async () => ({ files: [{ id: 'plain-copy', size: remote.length }] }) };
    };
    await store.sync();
    assert.equal(deleted, true);
    assert.equal(written.includes('remote-secret'), false);
    assert.equal(store.entries.length, 1);
    assert.equal(store.preferences.webhook.value, 'remote-secret');
});

test('HTTP failures preserve recovery details and partial deletion counts', () => {
    const token = 'jsp:abc123:' + 'a'.repeat(48);
    const recovery = apiError(
        503,
        JSON.stringify({
            error: 'Could not schedule deletion.',
            key: 'abc123',
            removeToken: token,
        }),
        '/shorten',
    );
    assert.equal(recovery.status, 503);
    assert.equal(recovery.details.removeToken, token);
    assert.match(recovery.message, /schedule deletion/);
    const partial = apiError(
        502,
        JSON.stringify({ error: 'Deletion failed.', removed: 2, total: 5 }),
        '/remove',
    );
    assert.match(partial.message, /2 of 5/);
    assert.match(partial.message, /retry/);
    assert.match(apiError(429, '', '/upload-challenge').message, /Too many requests/);
    assert.equal(
        apiError(500, '<html>private error page</html>', '/shorten').message.includes('<html>'),
        false,
    );
});

test('XHR propagates structured failures instead of losing recovery fields', async () => {
    const app = await readFile(new URL('../DSFiles-Server/Web/app.js', import.meta.url), 'utf8');
    const source = app.slice(app.indexOf('function request('), app.indexOf('function seedLink('));
    class XHR {
        constructor() {
            this.upload = {};
        }
        open() {}
        setRequestHeader() {}
        getResponseHeader() {
            return null;
        }
        send() {
            this.status = 503;
            this.responseText = JSON.stringify({
                error: 'Schedule failed',
                key: 'abc123',
                removeToken: 'recovery-token',
            });
            queueMicrotask(() => this.onload());
        }
    }
    const context = vm.createContext({ XMLHttpRequest: XHR, DOMException, apiError });
    vm.runInContext(source, context);
    await assert.rejects(
        context.request('/shorten', null, {}, new AbortController().signal),
        (error) => error.details.removeToken === 'recovery-token',
    );
});

test('filename normalization preserves extensions within API limits', () => {
    assert.equal(safeName('a'.repeat(100) + '.mp4'), 'a'.repeat(60) + '.mp4');
    assert.equal(safeName('a'.repeat(100) + '.tar.gz'), 'a'.repeat(57) + '.tar.gz');
    assert.equal(safeName('Vídeo 日本語.pdf'), 'Video ___.pdf');
    assert.equal(safeName(''), 'file');
    assert.equal(safeName('.gitignore'), '.gitignore');
    assert.equal(safeName('photo.jpg'), 'photo.jpg');
    assert.equal(safeName('a'.repeat(100)).length, 64);
});

test('worker honors all difficulty bits, including the server 20-bit challenge', async () => {
    const source = await readFile(
        new URL('../DSFiles-Server/Web/proof-worker.js', import.meta.url),
        'utf8',
    );
    for (const difficulty of [18, 20]) {
        const messages = [];
        let attempts = 0;
        const self = { postMessage: (value) => messages.push(value), crypto: { subtle: {} } };
        const crypto = {
            subtle: {
                digest: async () => {
                    attempts++;
                    return Uint8Array.of(0, 0, attempts === 1 ? 16 : 0, ...Array(29).fill(0))
                        .buffer;
                },
            },
        };
        const context = vm.createContext({
            self,
            crypto,
            TextEncoder,
            Uint8Array,
            Date,
            Number,
            Error,
        });
        vm.runInContext(source, context);
        await self.onmessage({
            data: { challenge: 'a'.repeat(48), difficulty, expiresAt: Date.now() + 60000 },
        });
        assert.equal(messages.at(-1).proof, 'a'.repeat(48) + ':' + (difficulty === 20 ? 1 : 0));
    }
});
