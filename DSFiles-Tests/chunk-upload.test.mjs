import test from 'node:test';
import assert from 'node:assert/strict';
import { File } from 'node:buffer';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import {
    readUploadAuthorization,
    readChunkReceipt,
    chunkManifest,
    abortChunkUpload,
} from '../DSFiles-Server/Web/chunk-upload.js';
import { canRetry, retryDelay } from '../DSFiles-Server/Web/upload-timing.js';

const handshake = (version = '2') => ({
    header: (name) =>
        ({ 'Session-ID': 'immutable-session', ChunkSize: '3', 'Upload-Protocol': version })[name],
});
const fragment = (index) => ({
    text: JSON.stringify({ receipt: `header.payload.signature${index}` }),
});
const file = () => new File([new Uint8Array([1, 2, 3, 4, 5, 6])], 'test.bin');
const final = () => ({
    text: JSON.stringify({ seed: 'test_seed', removeToken: 'srv:protected:v2' }),
});
const destination = { mode: 'server', encrypt: true };

async function client(request) {
    const app = await readFile(new URL('../DSFiles-Server/Web/app.js', import.meta.url), 'utf8');
    const source = app.slice(
        app.indexOf('async function retryUploadRequest('),
        app.indexOf('async function startUploads('),
    );
    const saved = [],
        states = [];
    const context = vm.createContext({
        request,
        readUploadAuthorization,
        readChunkReceipt,
        chunkManifest,
        abortChunkUpload,
        performance,
        URL,
        Blob,
        DOMException,
        AbortController,
        JSON,
        canRetry,
        retryDelay,
        waitForRetry: async () => {},
        safeName: (name) => name,
        size: (bytes) => String(bytes),
        timing: { add() {} },
        solveProof: async () => 'challenge:0',
        notice() {},
        addResult() {},
        seedLink: (seed, name) => 'https://example.test/d/' + seed + '/' + name,
        vault: { store: { add: async (value) => saved.push(value) } },
        setUploadState: (item, state, message, fields) => {
            states.push([state, message]);
            Object.assign(item, fields);
        },
    });
    vm.runInContext(source, context);
    return {
        upload: (item, signal) => context.uploadFile(item, signal, destination),
        saved,
        states,
    };
}

test('real UI sends protocol 2 and finalizes all signed fragment receipts', async () => {
    const calls = [];
    const flow = await client(async (path, body, headers, signal, onProgress) => {
        calls.push({ path, body, headers });
        if (path === '/cuh') {
            assert.equal(headers['Upload-Protocol'], '2');
            return handshake();
        }
        assert.equal(headers['Session-ID'], 'immutable-session');
        if (path === '/cuc') {
            onProgress?.(0.5);
            return fragment(headers.chunk);
        }
        assert.equal(path, '/cuf');
        assert.deepEqual(JSON.parse(await body.text()), {
            receipts: ['header.payload.signature1', 'header.payload.signature2'],
        });
        return final();
    });
    const item = { file: file(), confirmed: 0 };
    assert.equal(await flow.upload(item, new AbortController().signal), 'complete');
    assert.deepEqual(
        calls.map((c) => c.path),
        ['/cuh', '/cuc', '/cuc', '/cuf'],
    );
    assert.deepEqual([...new Uint8Array(await calls[1].body.arrayBuffer())], [1, 2, 3]);
    assert.deepEqual([...new Uint8Array(await calls[2].body.arrayBuffer())], [4, 5, 6]);
    assert.equal(flow.saved[0].seed, 'test_seed');
    assert.equal(item.confirmed, 6);
});

test('UI retains confirmed receipts and resumes on a new request without another handshake', async () => {
    const calls = [];
    let cancelled = false;
    const flow = await client(async (path, body, headers) => {
        calls.push(path + (headers.chunk ? ':' + headers.chunk : ''));
        if (path === '/cuh') return handshake();
        if (path === '/cuc' && headers.chunk === '2' && !cancelled) {
            cancelled = true;
            throw new DOMException('Cancelled', 'AbortError');
        }
        if (path === '/cuc') return fragment(headers.chunk);
        return final();
    });
    const item = { file: file(), confirmed: 0 };
    await assert.rejects(flow.upload(item, new AbortController().signal), { name: 'AbortError' });
    assert.equal(item.cursor.next, 1);
    assert.equal(item.cursor.receipts.length, 1);
    assert.equal(await flow.upload(item, new AbortController().signal), 'complete');
    assert.deepEqual(calls, ['/cuh', '/cuc:1', '/cuc:2', '/cuc:2', '/cuf']);
});

test('UI retries finalization without uploading the fragments again', async () => {
    const calls = [];
    let failed = false;
    const flow = await client(async (path, body, headers) => {
        calls.push(path);
        if (path === '/cuh') return handshake();
        if (path === '/cuc') return fragment(headers.chunk);
        if (!failed) {
            failed = true;
            throw Object.assign(new Error('lost final response'), { networkError: true });
        }
        return final();
    });
    assert.equal(
        await flow.upload({ file: file(), confirmed: 0 }, new AbortController().signal),
        'complete',
    );
    assert.deepEqual(calls, ['/cuh', '/cuc', '/cuc', '/cuf', '/cuf']);
});

test('discard cleanup sends only the acknowledged receipts with an independent signal', async () => {
    const cursor = { session: 'authorization', receipts: ['h.p.s'] };
    const signal = new AbortController();
    signal.abort();
    await abortChunkUpload(cursor, async (path, body, headers, actualSignal) => {
        assert.equal(path, '/cua');
        assert.equal(headers['Session-ID'], cursor.session);
        assert.equal(actualSignal.aborted, false);
        assert.deepEqual(JSON.parse(await body.text()), { receipts: cursor.receipts });
    });
});

test('invalid receipts are marked ambiguous and incompatible servers fail before uploading', () => {
    assert.throws(
        () => readChunkReceipt({ text: '{"seed":"not-a-fragment-receipt"}' }),
        (error) => error.processedUnknown === true,
    );
    assert.throws(() => readUploadAuthorization(handshake('1')), /compatible upload authorization/);
    assert.equal(readUploadAuthorization(handshake()).idempotent, false);
    assert.deepEqual(readUploadAuthorization(handshake()).receipts, []);
});
