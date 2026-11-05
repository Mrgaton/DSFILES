import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';

const web = new URL('../DSFiles-Server/Web/', import.meta.url);
const sessions = new Map();
const chunkSize = 1024 * 1024;
const types = {
    html: 'text/html; charset=utf-8',
    js: 'text/javascript',
    css: 'text/css',
    svg: 'image/svg+xml',
};
const assets = new Set([
    'index.html',
    'app.js',
    'catalog.js',
    'catalog-ui.js',
    'web-utils.js',
    'upload-timing.js',
    'chunk-upload.js',
    'proof-worker.js',
    'zip.js',
    'styles.css',
    'icon.svg',
]);
const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
const json = (response, status, data, headers = {}) => {
    response.writeHead(status, {
        'Content-Type': 'application/json',
        'Cache-Control': 'no-store',
        ...headers,
    });
    response.end(JSON.stringify(data));
};

http.createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    if (request.method === 'POST') {
        let length = 0;
        for await (const part of request) length += part.length;
        if (path === '/upload-challenge')
            return json(response, 200, {
                challenge: 'a'.repeat(48),
                difficulty: 1,
                expiresAt: Date.now() + 300000,
            });
        if (path === '/cuh') {
            const id = randomUUID();
            sessions.set(id, {
                name: request.headers.filename,
                chunks: Math.ceil(Number(request.headers.filesize) / chunkSize),
                replies: new Map(),
                attempts: new Map(),
            });
            await delay(200);
            response.writeHead(200, {
                'Session-ID': id,
                ChunkSize: String(chunkSize),
                'Upload-Protocol': '2',
            });
            return response.end('Ready');
        }
        if (path === '/cuc') {
            const session = sessions.get(request.headers['session-id']);
            if (!session) return json(response, 410, { error: 'Session expired.' });
            const index = Number(request.headers.chunk);
            const attempt = (session.attempts.get(index) || 0) + 1;
            session.attempts.set(index, attempt);
            if (session.replies.has(index)) return response.end(session.replies.get(index));
            await delay(1300 + length / 1300);
            if (session.name.includes('attention') && index === 1 && attempt === 1)
                return json(response, 400, {
                    error: 'This file needs a manual retry. Other files can continue.',
                });
            if (session.name.endsWith('.pdf') && index === 2 && attempt === 1)
                return json(
                    response,
                    429,
                    { error: 'The server is busy. Your confirmed chunks are safe.' },
                    { 'Retry-After': '3' },
                );
            const reply = JSON.stringify({ receipt: 'fixture.' + index + '.signature' });
            session.replies.set(index, reply);
            console.log('COMMIT ' + session.name + ' chunk ' + index);
            return response.end(reply);
        }
        if (path === '/cuf') {
            const session = sessions.get(request.headers['session-id']);
            session.finalizations = (session.finalizations || 0) + 1;
            await delay(600);
            if (session.name.endsWith('.zip') && session.finalizations === 1) {
                console.log('DROP final response for ' + session.name);
                return response.destroy();
            }
            console.log('FINALIZE ' + session.name + ' attempt ' + session.finalizations);
            session.finalReply ||= {
                seed: 'fixture_' + randomUUID().replaceAll('-', ''),
                removeToken: 'fixture:token:value',
            };
            return json(response, 200, session.finalReply);
        }
        if (path === '/cua') return json(response, 200, { removed: 1, total: 1 });
        if (path === '/d') {
            await delay(2000);
            return json(response, 200, {
                seed: 'compressed_fixture',
                removeToken: 'fixture:token:value',
            });
        }
        return json(response, 400, { error: 'Unsupported fixture operation.' });
    }
    if (path === '/config.js') {
        response.writeHead(200, { 'Content-Type': 'text/javascript' });
        return response.end("export const GOOGLE_CLIENT_ID = '';");
    }
    const name = path === '/' ? 'index.html' : path.slice(1);
    if (!assets.has(name))
        return json(response, 404, { error: 'No external files in this fixture.' });
    response.writeHead(200, {
        'Content-Type': types[name.split('.').pop()],
        'Cache-Control': 'no-store',
    });
    response.end(await readFile(new URL(name, web)));
}).listen(49273, '127.0.0.1', () => console.log('Upload fixture: http://127.0.0.1:49273'));
