import http from 'node:http';
import https from 'node:https';
import { readFile } from 'node:fs/promises';
const upstream = new URL(process.argv[2] || 'http://127.0.0.1:8081');
if (!['http:', 'https:'].includes(upstream.protocol) || upstream.username || upstream.password)
    throw Error('Invalid upstream URL');
const files = {
    '/config.js': 'config.js',
    '/catalog.js': 'catalog.js',
    '/catalog-ui.js': 'catalog-ui.js',
    '/': 'index.html',
    '/index.html': 'index.html',
    '/styles.css': 'styles.css',
    '/app.js': 'app.js',
    '/web-utils.js': 'web-utils.js',
    '/upload-timing.js': 'upload-timing.js',
    '/chunk-upload.js': 'chunk-upload.js',
    '/zip.js': 'zip.js',
    '/proof-worker.js': 'proof-worker.js',
    '/icon.svg': 'icon.svg',
};
const types = {
    html: 'text/html; charset=utf-8',
    css: 'text/css',
    js: 'text/javascript',
    svg: 'image/svg+xml',
};
const server = http.createServer(async (req, res) => {
    const path = new URL(req.url, 'http://localhost').pathname;
    if (
        (req.method === 'POST' &&
            [
                '/shorten',
                '/d',
                '/cuh',
                '/cuc',
                '/cuf',
                '/cua',
                '/upload-challenge',
                '/delete-challenge',
                '/remove',
            ].includes(path)) ||
        (req.method === 'GET' &&
            (path.startsWith('/s/') ||
                path.startsWith('/d/') ||
                path.startsWith('/rd/') ||
                path === '/animate'))
    ) {
        const headers = { ...req.headers, host: upstream.host };
        delete headers.origin;
        delete headers.connection;
        const proxy = (upstream.protocol === 'https:' ? https : http).request(
            new URL(req.url, upstream),
            { method: req.method, headers },
            (response) => {
                res.writeHead(response.statusCode, response.headers);
                response.pipe(res);
            },
        );
        proxy.on('error', () => {
            if (!res.headersSent) res.writeHead(502);
            res.end('API no disponible');
        });
        req.on('aborted', () => proxy.destroy());
        res.on('close', () => proxy.destroy());
        req.pipe(proxy);
        return;
    }
    if (req.method !== 'GET' || !files[path]) {
        res.writeHead(404);
        res.end();
        return;
    }
    try {
        const name = files[path];
        res.writeHead(200, {
            'Content-Type': types[name.split('.').pop()],
            'Cache-Control': 'no-store',
            'Referrer-Policy': 'no-referrer',
        });
        res.end(await readFile(new URL(name, import.meta.url)));
    } catch {
        res.writeHead(500);
        res.end();
    }
});
server.listen(4173, '127.0.0.1', () =>
    console.log('DS Files Web: http://127.0.0.1:4173 · API: ' + upstream.origin),
);
