import { setupCatalog } from './catalog-ui.js';
import { makeZip } from './zip.js';
import {
    readUploadAuthorization,
    readChunkReceipt,
    chunkManifest,
    abortChunkUpload,
} from './chunk-upload.js';
import { safeName, apiError } from './web-utils.js';
import { UploadTiming, canRetry, retryDelay, waitForRetry, duration } from './upload-timing.js';

const $ = (id) => document.getElementById(id);
let activeBatch = null,
    activeItem = null;
const timing = new UploadTiming();

let files = [],
    results = [],
    busy = false,
    controller = null,
    settings = { mode: 'server', webhook: '', encrypt: true };

function size(bytes) {
    if (bytes < 1000) return bytes + ' B';
    const unit = Math.min(3, Math.floor(Math.log10(bytes) / 3));
    const value = new Intl.NumberFormat('en', { maximumFractionDigits: 1 }).format(
        bytes / 1000 ** unit,
    );
    return value + ' ' + ['B', 'KB', 'MB', 'GB'][unit];
}

function notice(message, error = false) {
    $('notice').textContent = message;
    $('notice').classList.toggle('error', error);
    $('notice').hidden = false;
}

function element(tag, text, cls) {
    const e = document.createElement(tag);
    if (text !== undefined) e.textContent = text;
    if (cls) e.className = cls;
    return e;
}

function updateDestination() {
    $('destination-label').textContent = 'Settings';
    $('encryption-warning').hidden = settings.encrypt !== false;
}

function jobSize(item) {
    return item.file.size || item.sources?.reduce((sum, file) => sum + file.size, 0) || 0;
}

function newUpload(file, sources = null) {
    return {
        file,
        sources,
        state: 'queued',
        fraction: 0,
        confirmed: 0,
        detail: 'Ready to upload',
        error: '',
        done: false,
        cursor: null,
    };
}

function createUploadRow(item) {
    const node = element('li', undefined, 'file-row upload-row');
    const icon = element(
        'span',
        item.file.name.split('.').pop().slice(0, 4).toUpperCase(),
        'file-icon',
    );
    const card = element('div', undefined, 'upload-card');
    const heading = element('div', undefined, 'upload-card-heading');
    const title = element('strong', item.file.name);
    const state = element('span', '', 'upload-state');
    const remove = element('button', '×', 'icon-button');
    remove.setAttribute('aria-label', 'Remove ' + item.file.name);
    remove.onclick = () => {
        discardUpload(item);
        files = files.filter((file) => file !== item);
        renderQueue();
    };
    heading.append(title, state, remove);
    const metadata = element('small', '', 'upload-metadata');
    const meter = element('progress');
    meter.max = 100;
    meter.setAttribute('aria-label', 'Upload progress for ' + item.file.name);
    const detail = element('div', undefined, 'upload-file-detail');
    const description = element('span');
    const eta = element('span', '', 'upload-eta');
    detail.append(description, eta);
    const error = element('p', '', 'upload-error');
    error.setAttribute('role', 'alert');
    const retry = element('button', 'Retry file', 'secondary');
    retry.onclick = () => startUploads([item]);
    const output = element('div', undefined, 'upload-output');
    card.append(heading, metadata, meter, detail, error, retry, output);
    if (item.sources) {
        const sources = element('details', undefined, 'upload-sources');
        sources.append(element('summary', item.sources.length + ' files in this ZIP'));
        const names = element('ul');
        for (const source of item.sources) names.append(element('li', source.name));
        sources.append(names);
        card.append(sources);
    }
    node.append(icon, card);
    item.view = {
        node,
        title,
        state,
        remove,
        metadata,
        meter,
        description,
        eta,
        error,
        retry,
        output,
    };
    return node;
}

function discardUpload(item) {
    if (!item.done)
        abortChunkUpload(item.cursor, request).catch(() =>
            notice('Some uploaded fragments could not be removed.', true),
        );
    for (const result of results.filter((result) => result.owner === item)) {
        result.node.remove();
        if (result.raw) URL.revokeObjectURL(result.url);
    }
    results = results.filter((result) => result.owner !== item);
    item.view?.node.remove();
    if (activeBatch && !activeBatch.running) {
        activeBatch.items = activeBatch.items.filter((job) => job !== item);
        if (!activeBatch.items.length) activeBatch = null;
    }
}

function updateUploadRow(item) {
    if (!item.view) return;
    const view = item.view;
    const labels = {
        queued: 'Queued',
        zip: 'Bundling',
        proof: 'Checking access',
        setup: 'Preparing',
        sending: 'Uploading',
        confirming: 'Confirming',
        retrying: 'Retry scheduled',
        complete: 'Complete',
        receipt: 'Receipt saved',
        error: 'Needs attention',
        cancelled: 'Cancelled',
        deleted: 'Deleted',
    };
    view.node.dataset.state = item.state;
    view.title.textContent = item.file.name;
    view.state.textContent = labels[item.state] || item.state;
    view.metadata.textContent =
        size(jobSize(item)) +
        (item.done ? ' · ' + duration(item.elapsed / 1000) + ' active' : '') +
        (safeName(item.file.name) !== item.file.name
            ? ' · Upload name: ' + safeName(item.file.name)
            : '');
    view.remove.disabled = busy;
    view.meter.hidden = item.state === 'queued' || item.state === 'deleted';
    view.meter.value = item.done ? 100 : Math.min(99, Math.max(0, item.fraction * 100));
    view.description.textContent = item.detail;
    view.error.hidden = !item.error;
    view.error.textContent = item.error;
    view.retry.hidden = !['error', 'cancelled'].includes(item.state);
    view.retry.disabled = busy;
    view.retry.textContent =
        item.finalizing && item.cursor
            ? 'Recover link'
            : item.cursor && !item.blocked
              ? 'Resume upload'
              : 'Retry file';
    const estimate = estimateItem(item);
    view.eta.textContent =
        !item.done && !item.error && estimate
            ? estimate.overdue
                ? 'Taking longer than recent uploads'
                : '~' + duration(estimate.seconds) + ' left'
            : !item.done && activeItem === item && !item.error
              ? 'Learning from this upload…'
              : '';
}

function setUploadState(item, state, detail, patch = {}) {
    Object.assign(item, patch, { state, detail });
    updateUploadRow(item);
    refreshUploadSummary();
}

function estimateItem(item) {
    if (item.done) return { seconds: 0, low: 0, high: 0 };
    if (item.error || !item.kind) return null;
    let total = 0,
        low = 0,
        high = 0,
        overdue = false;
    const append = (kind, bytes, elapsed = 0, count = 1) => {
        const prediction = timing.predict(kind, bytes);
        if (!prediction) return false;
        const remaining = Math.max(prediction.seconds * 0.15, prediction.seconds - elapsed);
        total += remaining * count;
        low += Math.max(0, prediction.low - elapsed) * count;
        high += Math.max(remaining, prediction.high - elapsed) * count;
        overdue ||= elapsed > prediction.high;
        return true;
    };
    if (
        item.sources &&
        !item.prepared &&
        !append(
            'zip',
            jobSize(item),
            item.state === 'zip' ? (performance.now() - item.phaseStarted) / 1000 : 0,
        )
    )
        return null;
    if (!item.cursor && ['queued', 'zip', 'proof', 'setup'].includes(item.state)) {
        if (
            item.state !== 'setup' &&
            !append(
                'proof',
                0,
                item.state === 'proof' ? (performance.now() - item.phaseStarted) / 1000 : 0,
            )
        )
            return null;
        if (
            !item.compressed &&
            !append(
                'setup',
                0,
                item.state === 'setup' ? (performance.now() - item.phaseStarted) / 1000 : 0,
            )
        )
            return null;
    }
    if (item.compressed) {
        if (
            !append(
                item.kind,
                item.file.size,
                item.transferStarted ? (performance.now() - item.transferStarted) / 1000 : 0,
            )
        )
            return null;
    } else {
        const chunkSize = item.cursor?.chunkSize || 20971265;
        const next = item.cursor?.next || 0;
        const count = Math.ceil(jobSize(item) / chunkSize);
        if (next < count) {
            const currentBytes = Math.min(chunkSize, jobSize(item) - next * chunkSize);
            const elapsed =
                item.transferStarted && item.state !== 'retrying'
                    ? (performance.now() - item.transferStarted) / 1000
                    : 0;
            if (!append(item.kind, currentBytes, elapsed)) return null;
            const following = count - next - 1;
            if (following > 0) {
                if (following > 1 && !append(item.kind, chunkSize, 0, following - 1)) return null;
                const lastBytes = jobSize(item) - (count - 1) * chunkSize;
                if (!append(item.kind, lastBytes)) return null;
            }
        }
    }
    if (
        !item.compressed &&
        !append(
            'finalize',
            0,
            item.finalizing && item.state === 'confirming'
                ? (performance.now() - item.phaseStarted) / 1000
                : 0,
        ) &&
        item.finalizing
    )
        return null;
    if (item.state === 'retrying') {
        const wait = Math.max(0, (item.retryAt - performance.now()) / 1000);
        total += wait;
        low += wait;
        high += wait;
    }
    return { seconds: total, low, high, overdue };
}

function refreshUploadSummary() {
    if (!activeBatch) return;
    const jobs = activeBatch.items;
    const uploading = activeBatch.running;
    const total = jobs.reduce((sum, item) => sum + jobSize(item), 0);
    const sent = jobs.reduce(
        (sum, item) => sum + jobSize(item) * (item.done ? 1 : item.fraction),
        0,
    );
    const completed = jobs.filter((item) => item.done).length;
    const failed = jobs.filter((item) => item.error).length;
    const percent = total
        ? Math.min(completed === jobs.length ? 100 : 99, Math.floor((sent / total) * 100))
        : 0;
    $('upload-summary').hidden = false;
    $('upload-overall-progress').value = percent;
    $('upload-overall-percent').textContent = percent + '%';
    $('upload-overall-label').textContent =
        completed === jobs.length
            ? 'All uploads complete'
            : !uploading
              ? failed
                  ? 'Some files need attention'
                  : 'Upload paused'
              : 'Uploading ' + Math.max(1, jobs.indexOf(activeItem) + 1) + ' of ' + jobs.length;
    $('upload-overall-detail').textContent =
        completed +
        ' of ' +
        jobs.length +
        ' complete · ' +
        size(sent) +
        ' of ' +
        size(total) +
        (failed ? ' · ' + failed + ' need attention' : '');
    const estimates = activeBatch.pending
        .filter((item) => !item.done && !item.error)
        .map(estimateItem);
    const earliest = duration(estimates.reduce((sum, estimate) => sum + (estimate?.low || 0), 0));
    const latest = duration(estimates.reduce((sum, estimate) => sum + (estimate?.high || 0), 0));
    $('upload-overall-eta').textContent = !uploading
        ? completed === jobs.length
            ? 'This run finished in ' +
              duration(((activeBatch.finished || performance.now()) - activeBatch.started) / 1000)
            : 'Completed files keep their links'
        : estimates.some((estimate) => !estimate)
          ? 'Estimating from confirmed transfers…'
          : estimates.some((estimate) => estimate.overdue)
            ? 'Server confirmation is taking longer than usual'
            : 'About ' + earliest + (earliest === latest ? '' : '–' + latest) + ' left';
    $('upload-cancel').hidden = !uploading;
}

function renderQueue() {
    $('upload-panel').classList.toggle('has-files', files.length > 0);
    $('queue-section').hidden = !files.length;
    $('queue-title').textContent = files.length + ' file' + (files.length !== 1 ? 's' : '');
    $('total').textContent = size(files.reduce((sum, item) => sum + jobSize(item), 0)) + ' total';
    const nodes = new Set(files.map((item) => item.view?.node));
    for (const node of [...$('queue').children]) if (!nodes.has(node)) node.remove();
    let previous = null;
    for (const item of files) {
        if (!item.view) createUploadRow(item);
        if (
            item.view.node.previousElementSibling !== previous ||
            item.view.node.parentElement !== $('queue')
        )
            $('queue').insertBefore(
                item.view.node,
                previous ? previous.nextElementSibling : $('queue').firstElementChild,
            );
        previous = item.view.node;
        updateUploadRow(item);
    }
    const pending = files.filter((item) => !item.done);
    $('upload-actions').hidden = files.length > 0 && !pending.length;
    $('upload').disabled = busy || !pending.length;
    $('upload').textContent =
        !pending.length && files.length
            ? 'All uploaded'
            : files.some((item) => item.error)
              ? 'Retry pending files'
              : 'Upload files ↗';
    for (const id of [
        'clear',
        'zip',
        'zip-name',
        'upload-encryption',
        'upload-compression',
        'dropzone',
        'settings-open',
        'redirect-create',
        'delete-submit',
    ])
        $(id).disabled = busy;
    $('operation-cancel').hidden = !busy || !!activeItem;
    $('copy-upload-links').hidden = !results.some((result) => result.owner && !result.raw);
    $('zip-options').hidden = files.length > 0 && files.every((item) => item.done || item.sources);
    if (!files.length || !activeBatch) $('upload-summary').hidden = true;
    refreshUploadSummary();
    if (!busy && typeof preferenceRefresh === 'function') preferenceRefresh();
}

function addFiles(incoming) {
    if (busy) return;
    activeBatch = null;

    let empty = 0;

    for (const file of incoming) {
        if (!file.size) {
            empty++;
            continue;
        }
        files.push(newUpload(file));
    }

    renderQueue();
    if (empty)
        notice('Skipped ' + empty + ' empty files: the API requires at least one byte.', true);
}

$('file-picker').onchange = (e) => {
    addFiles(e.target.files);
    e.target.value = '';
};

$('dropzone').onclick = () => $('file-picker').click();

for (const name of ['dragenter', 'dragover'])
    $('dropzone').addEventListener(name, (e) => {
        e.preventDefault();
        if (!busy) $('dropzone').classList.add('dragover');
    });

for (const name of ['dragleave', 'drop'])
    $('dropzone').addEventListener(name, (e) => {
        e.preventDefault();
        $('dropzone').classList.remove('dragover');
    });

$('dropzone').addEventListener('drop', (e) => {
    const entries = [...e.dataTransfer.items].map((i) => i.webkitGetAsEntry?.());

    if (entries.some((e) => e?.isDirectory))
        notice('To upload a folder, zip it first or select its files.', true);

    addFiles(e.dataTransfer.files);
});

window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

$('clear').onclick = () => {
    files.forEach(discardUpload);
    activeBatch = null;
    files = [];
    renderQueue();
};

$('zip').onchange = () => ($('zip-name-row').hidden = !$('zip').checked);

const sectionHashes = {
    upload: 'upload',
    files: 'files',
    redirect: 'links',
    animation: 'animation',
};
function tab(which, writeHash = true) {
    if (which === 'download' || which === 'delete') {
        const manual = $('manual-' + which);
        which = 'files';
        manual.open = true;
        queueMicrotask(() => manual.scrollIntoView({ block: 'nearest' }));
    }

    if (!Object.hasOwn(sectionHashes, which)) which = 'upload';
    if (writeHash && location.hash !== '#' + sectionHashes[which])
        history.pushState(null, '', '#' + sectionHashes[which]);
    for (const name of ['upload', 'files', 'redirect', 'animation']) {
        const active = name === which;
        $(name + '-tab').setAttribute('aria-selected', String(active));
        $(name + '-tab').tabIndex = active ? 0 : -1;
        $(name + '-panel').hidden = !active;
    }
    $('compression-control').hidden = which !== 'upload';
}

for (const name of ['upload', 'files', 'redirect', 'animation']) {
    $(name + '-tab').onclick = () => tab(name);

    $(name + '-tab').onkeydown = (e) => {
        if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
            e.preventDefault();
            const tabs = ['upload', 'files', 'redirect', 'animation'];
            const next =
                e.key === 'Home'
                    ? tabs[0]
                    : e.key === 'End'
                      ? tabs.at(-1)
                      : tabs[
                            (tabs.indexOf(name) + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) %
                                tabs.length
                        ];
            tab(next);
            $(next + '-tab').focus();
        }
    };
}

function openSettings() {
    if (!vault.requireUnlocked()) return;
    $('upload-encryption').checked = settings.encrypt;
    updateEncryptionHint();

    $('settings-form').elements.destination.value = settings.mode;
    $('webhook').value = settings.webhook;
    $('webhook-field').hidden = settings.mode !== 'custom';
    $('settings-error').textContent = '';
    $('settings').showModal();
}

$('settings-open').onclick = openSettings;
$('settings-close').onclick = () => $('settings').close();

for (const radio of document.querySelectorAll('[name=destination]'))
    radio.onchange = () => ($('webhook-field').hidden = radio.value !== 'custom');

$('forget').onclick = () => {
    settings = { ...settings, mode: 'server', webhook: '' };

    $('webhook').value = '';
    $('settings-form').elements.destination.value = 'server';
    $('webhook-field').hidden = true;
    updateDestination();
    savePreferences({ mode: 'server', webhook: '' });
};

$('settings-form').onsubmit = (e) => {
    e.preventDefault();
    const mode = e.target.elements.destination.value,
        webhook = $('webhook').value.trim();

    if (mode === 'custom') {
        try {
            const u = new URL(webhook);
            if (
                u.protocol !== 'https:' ||
                ![
                    'discord.com',
                    'ptb.discord.com',
                    'canary.discord.com',
                    'discordapp.com',
                ].includes(u.hostname) ||
                !/^\/api\/webhooks\/\d+\/[\w-]+$/.test(u.pathname) ||
                u.search ||
                u.hash ||
                u.username ||
                u.password ||
                u.port ||
                webhook.length > 256
            )
                throw Error();
        } catch {
            $('settings-error').textContent =
                'Enter a valid Discord webhook URL without extra parameters.';
            return;
        }
    }

    settings = {
        mode,
        webhook: mode === 'custom' ? webhook : '',
        encrypt: $('upload-encryption').checked,
    };

    updateDestination();
    $('settings').close();
    savePreferences({ mode: settings.mode, webhook: settings.webhook, encrypt: settings.encrypt });
};

function progress(fraction, label, detail) {
    $('activity').hidden = false;
    $('activity-label').textContent = label;
    $('activity-detail').textContent = detail;
    $('progress').value = Math.max(0, Math.min(100, fraction * 100));
    $('percent').textContent = Math.floor(fraction * 100) + ' %';
}

function request(path, body, headers, signal, onProgress) {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', path);
        xhr.timeout = 10 * 60 * 1000;

        for (const [key, value] of Object.entries(headers)) xhr.setRequestHeader(key, value);

        const abort = () => xhr.abort();
        signal.addEventListener('abort', abort, { once: true });

        const cleanup = () => signal.removeEventListener('abort', abort);

        xhr.upload.onprogress = (e) => {
            if (e.lengthComputable) onProgress?.(e.loaded / e.total);
        };

        xhr.onload = () => {
            cleanup();
            if (xhr.status >= 200 && xhr.status < 300)
                resolve({ text: xhr.responseText, header: (name) => xhr.getResponseHeader(name) });
            else {
                const error = apiError(xhr.status, xhr.responseText, path);
                error.retryAfter = xhr.getResponseHeader('Retry-After');
                error.retryBlocked = xhr.getResponseHeader('Upload-Retry') === 'blocked';
                reject(error);
            }
        };

        xhr.onerror = () => {
            cleanup();
            reject(
                Object.assign(
                    new Error('Connection interrupted. The file may still be processing.'),
                    { networkError: true },
                ),
            );
        };

        xhr.ontimeout = () => {
            cleanup();
            reject(
                Object.assign(
                    new Error('The server timed out. Confirmation may still be processing.'),
                    { networkError: true },
                ),
            );
        };

        xhr.onabort = () => {
            cleanup();
            reject(new DOMException('Cancelled', 'AbortError'));
        };

        if (signal.aborted) {
            cleanup();
            reject(new DOMException('Cancelled', 'AbortError'));
            return;
        }
        xhr.send(body);
    });
}

function seedLink(seed, name) {
    if (typeof seed !== 'string' || !/^[-_A-Za-z0-9]+(?:\$[-_A-Za-z0-9]+)?$/.test(seed))
        throw new Error('The response does not contain a valid seed.');

    return new URL('/d/' + seed + '/' + encodeURIComponent(name), location.origin).href;
}

async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        notice('Copied.');
    } catch {
        notice('Could not copy automatically. Copy the visible link from the list.', true);
    }
}

function addResult(
    name,
    url,
    raw = false,
    removeToken = '',
    redirect = false,
    warning = '',
    owner = null,
) {
    const result = { name, url, raw, removeToken, owner };
    const node = element(owner ? 'div' : 'li', undefined, owner ? 'upload-result' : 'result-row');
    const info = element('div', undefined, 'file-info');
    if (!owner) info.append(element('strong', name));
    if (warning) info.append(element('small', warning, 'error'));
    if (!raw) {
        const link = element('a', url, owner ? 'upload-link' : '');
        link.href = url;
        link.title = url;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        info.append(link);
    } else
        info.append(
            element('small', 'Save this receipt to recover the link through the intermediary.'),
        );
    const actions = element('div', undefined, 'upload-result-actions');
    const downloadUrl = new URL(url);
    if (!raw && !redirect) downloadUrl.searchParams.set('file', '');
    const save = element(
        'a',
        raw ? 'Save receipt' : redirect ? 'Open link' : 'Download',
        'secondary',
    );
    save.href = downloadUrl.href;
    if (raw) save.download = 'dsfiles-receipt.txt';
    else {
        save.target = '_blank';
        save.rel = 'noopener noreferrer';
    }
    actions.append(save);
    if (!raw) {
        const button = element('button', 'Copy link', 'primary');
        button.onclick = () => copy(url);
        actions.append(button);
    }
    if (removeToken) {
        const more = element('details', undefined, 'upload-more');
        more.append(element('summary', 'More actions'));
        const menu = element('div', undefined, 'upload-more-actions');
        const tokenButton = element('button', 'Copy deletion token', 'text-button');
        tokenButton.onclick = () => copy(removeToken);
        const remove = element('button', 'Delete', 'text-button');
        remove.onclick = () => {
            tab('delete');
            $('delete-token').value = removeToken;
            $('delete-confirm').checked = false;
            $('delete-panel').scrollIntoView({ behavior: 'smooth' });
        };
        menu.append(tokenButton, remove);
        more.append(menu);
        actions.append(more);
    }
    if (!owner) node.append(element('span', raw ? 'JWT' : warning ? '!' : '✓', 'file-icon'));
    node.append(info, actions);
    result.node = node;
    results.push(result);
    if (owner) {
        owner.view.output.replaceChildren(node);
    } else {
        $('results-section').hidden = false;
        $('results').append(node);
    }
    $('copy-upload-links').hidden = !results.some((entry) => entry.owner && !entry.raw);
    return result;
}

$('copy-upload-links').onclick = () =>
    copy(
        results
            .filter((result) => result.owner && !result.raw)
            .map((result) => result.url)
            .join('\n'),
    );
$('upload-cancel').onclick = () => controller?.abort();

$('copy-all').onclick = () => {
    const links = results.filter((r) => !r.raw && !r.owner).map((r) => r.url);
    if (links.length) copy(links.join('\n'));
    else notice('The server returned only encrypted receipts.', true);
};

async function solveProof(headers, signal, endpoint = '/upload-challenge', onStatus = null) {
    if (onStatus) onStatus('Preparing upload permission…');
    else progress(0, 'Checking access', 'Preparing upload permission…');

    const response = await request(endpoint, null, headers, signal);

    let challenge;
    try {
        challenge = JSON.parse(response.text);
    } catch {
        throw new Error('Could not obtain upload permission.');
    }

    return new Promise((resolve, reject) => {
        const worker = new Worker(new URL('./proof-worker.js', import.meta.url));

        const stop = () => {
            worker.terminate();
            signal.removeEventListener('abort', abort);
        };

        const abort = () => {
            stop();
            reject(new DOMException('Cancelled', 'AbortError'));
        };

        signal.addEventListener('abort', abort, { once: true });

        worker.onerror = () => {
            stop();
            reject(new Error('Could not start the access check.'));
        };

        worker.onmessage = ({ data }) => {
            if (data.proof) {
                stop();
                resolve(data.proof);
            } else if (data.error) {
                stop();
                reject(new Error(data.error));
            } else {
                const message =
                    'Access check · ' +
                    new Intl.NumberFormat('en').format(data.attempts) +
                    ' attempts';
                if (onStatus) onStatus(message);
                else $('activity-detail').textContent = message + '. You can cancel.';
            }
        };

        if (signal.aborted) {
            abort();
            return;
        }
        worker.postMessage(challenge);
    });
}

async function retryUploadRequest(item, send, signal, idempotent) {
    for (let attempt = 0; ; attempt++) {
        try {
            return await send(attempt);
        } catch (error) {
            if (attempt >= 3 || !canRetry(error, idempotent)) throw error;
            const delay = retryDelay(error, attempt + 1);
            item.retryAt = performance.now() + delay;
            item.transferStarted = null;
            item.fraction = item.file.size ? item.confirmed / item.file.size : 0;
            await waitForRetry(delay, signal, (remaining) => {
                setUploadState(
                    item,
                    'retrying',
                    'Retry ' +
                        (attempt + 1) +
                        ' of 3 in ' +
                        Math.ceil(remaining / 1000) +
                        's · ' +
                        (item.chunkLabel || 'Preparing access'),
                );
            });
        }
    }
}

async function prepareUpload(item, headers, signal) {
    return retryUploadRequest(
        item,
        async () => {
            item.phaseStarted = performance.now();
            setUploadState(item, 'proof', 'Checking upload permission…');
            headers['Upload-Proof'] = await solveProof(
                headers,
                signal,
                '/upload-challenge',
                (detail) => setUploadState(item, 'proof', detail),
            );
            timing.add('proof', 0, performance.now() - item.phaseStarted);
            if (item.compressed) return null;
            item.phaseStarted = performance.now();
            setUploadState(item, 'setup', 'Opening a secure upload session…');
            const response = await request(
                '/cuh',
                null,
                { ...headers, 'Upload-Protocol': '2' },
                signal,
            );
            timing.add('setup', 0, performance.now() - item.phaseStarted);
            return readUploadAuthorization(response);
        },
        signal,
        true,
    );
}

async function uploadFile(item, signal, destination) {
    const file = item.file,
        name = safeName(file.name);
    const headers = {
        filename: name,
        filesize: String(file.size),
        encrypt: String(destination.encrypt !== false),
    };
    if (destination.mode === 'custom') headers.webhook = destination.webhook;
    if (item.compressed && file.size > 100000000)
        throw Error(
            'Automatic compression supports up to 100 MB per file. Turn Compress off before retrying.',
        );
    if (!item.cursor || JSON.stringify(item.destination) !== JSON.stringify(destination)) {
        if (item.cursor)
            abortChunkUpload(item.cursor, request).catch(() =>
                notice('Some uploaded fragments could not be removed.', true),
            );
        item.finalizing = false;
        item.destination = { ...destination };
        item.confirmed = 0;
        item.fraction = 0;
        item.cursor = await prepareUpload(item, headers, signal);
    }
    let final;
    if (item.compressed) {
        headers.compress = 'true';
        item.transferStarted = performance.now();
        setUploadState(item, 'sending', 'Sending file · automatic compression');
        final = await request('/d', file, headers, signal, (fraction) => {
            setUploadState(
                item,
                fraction === 1 ? 'confirming' : 'sending',
                fraction === 1
                    ? 'Sent to the server · waiting for storage confirmation'
                    : size(file.size * fraction) + ' of ' + size(file.size) + ' sent',
                { fraction },
            );
        });
        timing.add(item.kind, file.size, performance.now() - item.transferStarted);
    } else {
        const cursor = item.cursor,
            chunks = Math.ceil(file.size / cursor.chunkSize);
        for (let index = cursor.next; index < chunks; index++) {
            signal.throwIfAborted();
            const start = index * cursor.chunkSize,
                end = Math.min(file.size, start + cursor.chunkSize);
            item.chunkLabel = 'Chunk ' + (index + 1) + ' of ' + chunks;
            final = await retryUploadRequest(
                item,
                async (attempt) => {
                    item.transferStarted = performance.now();
                    setUploadState(item, 'sending', item.chunkLabel + ' · sending');
                    const response = await request(
                        '/cuc',
                        file.slice(start, end),
                        { 'Session-ID': cursor.session, chunk: String(index + 1) },
                        signal,
                        (fraction) => {
                            setUploadState(
                                item,
                                fraction === 1 ? 'confirming' : 'sending',
                                item.chunkLabel +
                                    (fraction === 1
                                        ? ' · sent, waiting for confirmation'
                                        : ' · ' +
                                          size((end - start) * fraction) +
                                          ' of ' +
                                          size(end - start) +
                                          ' sent'),
                                { fraction: (start + (end - start) * fraction) / file.size },
                            );
                        },
                    );
                    if (!attempt)
                        timing.add(
                            item.kind,
                            end - start,
                            performance.now() - item.transferStarted,
                        );
                    return response;
                },
                signal,
                cursor.idempotent,
            );
            cursor.receipts.push(readChunkReceipt(final));
            cursor.next = index + 1;
            item.confirmed = end;
            item.transferStarted = null;
            setUploadState(
                item,
                'confirming',
                index + 1 + ' of ' + chunks + ' chunks safely stored',
                { fraction: end / file.size },
            );
        }
        try {
            item.finalizing = true;
            item.chunkLabel = 'Finalizing file link';
            setUploadState(item, 'confirming', 'All chunks stored · finalizing the file link…');
            final = await retryUploadRequest(
                item,
                async (attempt) => {
                    item.phaseStarted = performance.now();
                    setUploadState(
                        item,
                        'confirming',
                        'All chunks stored · finalizing the file link…',
                    );
                    const response = await request(
                        '/cuf',
                        chunkManifest(cursor.receipts),
                        { 'Session-ID': cursor.session },
                        signal,
                    );
                    if (!attempt) timing.add('finalize', 0, performance.now() - item.phaseStarted);
                    return response;
                },
                signal,
                true,
            );
        } catch (error) {
            error.finalizationSafe = true;
            throw error;
        }
    }
    let response;
    try {
        response = JSON.parse(final.text);
    } catch {
        const encrypted = final.text.trim().split('.').length === 5;
        addResult(
            name,
            URL.createObjectURL(new Blob([final.text], { type: 'text/plain' })),
            true,
            '',
            false,
            encrypted ? '' : 'Unexpected server reply. Keep the receipt before restarting.',
            item,
        );
        try {
            const digest = new Uint8Array(
                await crypto.subtle.digest('SHA-256', new TextEncoder().encode(final.text)),
            );
            await vault.store.add({
                seed:
                    'receipt_' +
                    Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join(''),
                name,
                size: file.size,
                removeToken: '',
                redirect: false,
                receipt: final.text,
            });
        } catch (error) {
            notice('Could not save the receipt: ' + error.message, true);
        }
        if (encrypted) return 'receipt';
        throw Object.assign(
            Error(
                'The server reply was saved as a receipt. Review it before restarting; the file may already be uploaded.',
            ),
            { processedUnknown: true },
        );
    }
    let link;
    try {
        link = new URL(seedLink(response.seed, name));
    } catch {
        addResult(
            name,
            URL.createObjectURL(new Blob([final.text], { type: 'text/plain' })),
            true,
            '',
            false,
            'The reply did not include a valid link. Keep this receipt.',
            item,
        );
        throw Object.assign(
            Error(
                'The server did not provide a valid download link. The file may already be stored.',
            ),
            { processedUnknown: true },
        );
    }
    addResult(name, link.href, false, response.removeToken, false, '', item);
    try {
        await vault.store.add({
            seed: response.seed,
            name,
            size: file.size,
            removeToken: response.removeToken || '',
            redirect: false,
        });
    } catch (error) {
        notice(
            'Upload complete, but history could not be saved: ' +
                error.message +
                ' Keep the link and token on this file.',
            true,
        );
    }
    return 'complete';
}

async function startUploads(selected = null) {
    if (busy || !vault.requireUnlocked()) return;
    let pending = (selected || files).filter((item) => !item.done);
    if (!pending.length) return;
    if ($('zip').checked && !selected) {
        const separate = pending.filter((item) => !item.sources);
        if (separate.length) {
            let name = safeName($('zip-name').value.trim() || 'my-files.zip');
            if (!name.toLowerCase().endsWith('.zip')) name = name.slice(0, 60) + '.zip';
            const bundle = newUpload(
                new File([], name, { type: 'application/zip' }),
                separate.map((item) => item.file),
            );
            separate.forEach(discardUpload);
            files = files.filter((item) => !separate.includes(item));
            files.push(bundle);
            pending = [...pending.filter((item) => item.sources), bundle];
        }
    }
    busy = true;
    controller = new AbortController();
    const signal = controller.signal,
        destination = { ...settings };
    for (const item of pending) {
        item.error = '';
        item.blocked = false;
        item.state = 'queued';
        item.detail = item.cursor ? 'Waiting to resume confirmed chunks' : 'Ready to upload';
        item.compressed = $('upload-compression').checked;
        item.kind =
            (item.compressed ? 'compressed' : 'chunk') +
            ':' +
            (destination.encrypt ? 'encrypted' : 'plain') +
            ':' +
            destination.mode;
    }
    activeBatch = { items: [...files], pending, started: performance.now(), running: true };
    $('activity').hidden = true;
    $('notice').hidden = true;
    renderQueue();
    const uploadTimer = setInterval(() => {
        if (activeItem) updateUploadRow(activeItem);
        refreshUploadSummary();
    }, 500);
    try {
        for (const item of pending) {
            if (signal.aborted) break;
            activeItem = item;
            item.started = performance.now();
            try {
                if (item.sources && !item.prepared) {
                    item.phaseStarted = performance.now();
                    setUploadState(
                        item,
                        'zip',
                        'Bundling ' + item.sources.length + ' files on this device…',
                    );
                    const blob = await makeZip(item.sources, signal, (fraction) =>
                        setUploadState(
                            item,
                            'zip',
                            'Preparing ZIP · ' + Math.floor(fraction * 100) + '%',
                            { fraction: 0 },
                        ),
                    );
                    timing.add('zip', jobSize(item), performance.now() - item.phaseStarted);
                    item.file = new File([blob], item.file.name, { type: 'application/zip' });
                    item.prepared = true;
                    renderQueue();
                }
                const state = await uploadFile(item, signal, destination);
                item.elapsed = (item.elapsed || 0) + performance.now() - item.started;
                item.done = true;
                item.fraction = 1;
                item.cursor = null;
                setUploadState(
                    item,
                    state,
                    state === 'receipt'
                        ? 'Uploaded · save the receipt to recover your link'
                        : 'Stored successfully · your link is ready',
                );
            } catch (error) {
                item.elapsed = (item.elapsed || 0) + performance.now() - item.started;
                item.transferStarted = null;
                item.blocked = !!(
                    error.retryBlocked ||
                    error.processedUnknown ||
                    (!error.finalizationSafe &&
                        (item.compressed || (item.cursor && !item.cursor.idempotent)) &&
                        ['sending', 'confirming'].includes(item.state) &&
                        (error.networkError || error.name === 'AbortError' || error.status >= 500))
                );
                if (
                    error.status === 410 ||
                    item.blocked ||
                    (error.status === 409 && !error.retryAfter)
                ) {
                    abortChunkUpload(item.cursor, request).catch(() =>
                        notice('Some uploaded fragments could not be removed.', true),
                    );
                    item.cursor = null;
                }
                item.fraction = item.cursor && item.file.size ? item.confirmed / item.file.size : 0;
                const message =
                    error.name === 'AbortError'
                        ? item.blocked
                            ? 'Upload cancelled. The current fragment may still finish on the server; confirmed fragments are being cleaned up.'
                            : 'Upload cancelled. Confirmed fragments can be resumed.'
                        : error.message;
                setUploadState(
                    item,
                    error.name === 'AbortError' ? 'cancelled' : 'error',
                    item.blocked
                        ? 'A file link was not confirmed. Review the error before restarting.'
                        : item.confirmed
                          ? size(item.confirmed) + ' safely stored before the interruption'
                          : 'This file needs attention',
                    {
                        error:
                            message +
                            (item.blocked
                                ? ' Restarting may create another copy. Keep any available receipt or link.'
                                : ''),
                    },
                );
            }
        }
        try {
            await vault.store.setPreferences({ 'upload-timing': timing.serialize() });
        } catch (error) {
            notice('Could not save upload timing history: ' + error.message, true);
        }
    } finally {
        activeBatch.running = false;
        activeBatch.finished = performance.now();
        clearInterval(uploadTimer);
        busy = false;
        controller = null;
        activeItem = null;
        renderQueue();
    }
}

$('upload').onclick = () => startUploads();

$('download-form').onsubmit = (e) => {
    e.preventDefault();
    let input = $('download-input').value.trim(),
        url;

    try {
        if (/^https?:\/\//i.test(input)) {
            const source = new URL(input);

            if (!/^\/(d|df|f)\//.test(source.pathname))
                throw new Error('The link must be a DS Files download (/d/…).');

            url = new URL(source.pathname.replace(/^\/(df|f)\//, '/d/'), location.origin);
            url.searchParams.set('file', '');
        } else if (
            /^[-_A-Za-z0-9]+:[-_A-Za-z0-9]*:[-_A-Za-z0-9]+(?:\$[-_A-Za-z0-9]+)?$/.test(input)
        ) {
            url = new URL('/d/' + input, location.origin);
            url.searchParams.set('file', '');
        } else url = new URL(seedLink(input, safeName($('download-name').value.trim() || 'file')));

        url.searchParams.set('file', '');
        const a = element('a');
        a.href = url.href;
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        document.body.append(a);
        a.click();
        a.remove();

        notice(
            'Download requested. If the file is unavailable, the browser will show the API response.',
        );
    } catch (error) {
        notice(error.message, true);
    }
};

window.addEventListener('beforeunload', (e) => {
    if (busy) {
        e.preventDefault();
        e.returnValue = '';
    }
});

$('notice').before($('activity'));
const operationCancel = element('button', 'Cancel operation', 'secondary');
operationCancel.id = 'operation-cancel';
operationCancel.hidden = true;
operationCancel.onclick = () => controller?.abort();
$('activity').append(operationCancel);
updateDestination();
renderQueue();

function httpUrl(value) {
    const url = new URL(value);
    if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password)
        throw new Error('Use an HTTP or HTTPS URL without credentials.');
    return url;
}
function updateAnimation() {
    $('animation-color-value').textContent = $('animation-color').value;
    try {
        if (!$('animation-form').checkValidity())
            throw new Error('Check the URL and parameter values.');
        const source = httpUrl($('animation-url').value.trim());
        const url = new URL('/animate', location.origin);
        const values = {
            url: source.href,
            offset: $('animation-offset').value,
            fps: $('animation-fps').value,
            wd: $('animation-width').value,
            hd: $('animation-height').value,
            mccn: $('animation-color').value,
            i: $('animation-invert').checked,
            hdr: $('animation-hdr').checked,
            pixel: $('animation-pixel').checked,
        };
        for (const [key, value] of Object.entries(values)) url.searchParams.set(key, value);
        const command =
            ($('animation-shell').value === 'powershell' ? 'curl.exe' : 'curl') +
            " --no-buffer --fail --show-error -H 'Accept: text/plain' '" +
            url.href +
            "'";
        $('animation-command').textContent = command;
        $('animation-copy').disabled = false;
    } catch (error) {
        $('animation-command').textContent = error.message;
        $('animation-copy').disabled = true;
    }
}
$('animation-form').oninput = updateAnimation;
$('animation-form').onsubmit = (e) => e.preventDefault();
$('animation-copy').onclick = () => copy($('animation-command').textContent);
$('animation-preview').onclick = () => {
    try {
        const url = httpUrl($('animation-url').value.trim());
        $('animation-image').src = url.href;
        $('animation-image').hidden = false;
    } catch (error) {
        notice(error.message, true);
    }
};
$('animation-image').onerror = () => {
    $('animation-image').hidden = true;
    notice(
        'Could not show the original image. Use a direct image or GIF URL; Tenor page links may work only on the server.',
        true,
    );
};
async function runOperation(action) {
    if (busy) return;
    busy = true;
    controller = new AbortController();
    renderQueue();
    $('notice').hidden = true;
    try {
        await action(controller.signal);
    } catch (error) {
        notice(
            error.name === 'AbortError'
                ? 'Operation cancelled. The server may already have processed part of it.'
                : error.message,
            true,
        );
        $('activity-label').textContent = 'Operation interrupted';
        $('activity-detail').textContent = 'Review the message below before retrying.';
    } finally {
        busy = false;
        controller = null;
        renderQueue();
    }
}
let shortLinkSource = '';
$('redirect-url').addEventListener('input', () => {
    shortLinkSource = '';
});
$('redirect-form').onsubmit = (e) => {
    e.preventDefault();
    if (!vault.requireUnlocked()) return;
    runOperation(async (signal) => {
        const target = httpUrl($('redirect-url').value.trim()).href;
        if (new TextEncoder().encode(target).length > 8192)
            throw new Error('The URL exceeds the 8 KiB limit.');
        const name = safeName($('redirect-name').value.trim() || 'my-link').slice(0, 55) + '.url';
        const lifetime =
            $('redirect-lifetime').value === 'custom'
                ? Number($('redirect-minutes').value) * 60
                : Number($('redirect-lifetime').value);
        if (
            !Number.isSafeInteger(lifetime) ||
            (lifetime !== 0 && (lifetime < 60 || lifetime > 31536000))
        )
            throw Error('Choose an expiration between one minute and one year.');
        const body = new Blob([target], { type: 'text/plain' }),
            headers = { filename: name, filesize: String(body.size), lifetime: String(lifetime) };
        headers['Upload-Proof'] = await solveProof(headers, signal);
        let response;
        try {
            response = JSON.parse((await request('/shorten', body, headers, signal)).text);
        } catch (error) {
            const recovery = error.details;
            if (
                typeof recovery?.key === 'string' &&
                /^[A-Za-z0-9_-]{4,64}$/.test(recovery.key) &&
                typeof recovery.removeToken === 'string' &&
                /^jsp:[A-Za-z0-9_-]{4,64}:[a-f0-9]{48}$/.test(recovery.removeToken) &&
                recovery.removeToken.split(':')[1] === recovery.key
            ) {
                const url = new URL('/s/' + recovery.key, location.origin).href;
                const recoveryName = name + ' (recovery: expiration not confirmed)';
                addResult(
                    recoveryName,
                    url,
                    false,
                    recovery.removeToken,
                    true,
                    'Creation or cleanup was not fully confirmed. Keep the deletion token; expiration is not confirmed.',
                );
                try {
                    await vault.store.add({
                        seed: 'jsp_' + recovery.key,
                        shortKey: recovery.key,
                        name: recoveryName,
                        size: body.size,
                        removeToken: recovery.removeToken,
                        redirect: true,
                        targetUrl: target,
                        sourceSeed: shortLinkSource,
                    });
                } catch (saveError) {
                    error.message += ' Could not save recovery details: ' + saveError.message;
                }
                error.message +=
                    ' Recovery details are in Results. The link may already be absent; deletion was not confirmed. No expiration was confirmed.';
            }
            throw error;
        }
        if (!/^[A-Za-z0-9_-]{4,64}$/.test(response.key) || typeof response.removeToken !== 'string')
            throw Error('JSPaste returned an invalid response.');
        const url = new URL('/s/' + response.key, location.origin).href;
        addResult(name, url, false, response.removeToken, true);
        try {
            await vault.store.add({
                seed: 'jsp_' + response.key,
                shortKey: response.key,
                name,
                size: body.size,
                removeToken: response.removeToken,
                redirect: true,
                expiresAt: response.expiresAt || 0,
                targetUrl: target,
                sourceSeed: shortLinkSource,
            });
        } catch (error) {
            notice(
                'Link created, but history could not be saved: ' +
                    error.message +
                    ' Copy the link and its token.',
                true,
            );
        }
        progress(1, 'Link created', 'Copy the link and deletion token from the results.');
    });
};
$('delete-form').onsubmit = (e) => {
    e.preventDefault();
    if (!vault.requireUnlocked()) return;
    if (!$('delete-confirm').checked) return;
    runOperation(async (signal) => {
        const token = $('delete-token').value.trim();
        if (!/^[-_A-Za-z0-9]+:[-_A-Za-z0-9]+:[-_A-Za-z0-9]+$/.test(token))
            throw new Error('Enter a valid deletion token, not a download seed.');
        const headers = { 'Remove-Token': token };
        headers['Delete-Proof'] = await solveProof(headers, signal, '/delete-challenge');
        progress(0, 'Deleting file', 'Waiting for service confirmation…');
        const response = await request('/remove', null, headers, signal);
        const result = JSON.parse(response.text);
        if (!Number.isInteger(result.removed) || result.removed !== result.total)
            throw new Error('The server did not confirm complete deletion.');
        progress(1, 'File deleted', result.removed + ' messages deleted or already absent.');
        notice('The service confirmed deletion. The link should no longer be used.');
        for (const result of results)
            if (result.removeToken === token) {
                result.node.remove();
                if (result.owner) {
                    setUploadState(
                        result.owner,
                        'deleted',
                        'The file was deleted. This link is no longer available.',
                    );
                }
            }
        results = results.filter((result) => result.removeToken !== token);
        $('results-section').hidden = !results.some((result) => !result.owner);
        $('delete-token').value = '';
        $('delete-confirm').checked = false;
        try {
            await vault.store.deleted(token);
        } catch (error) {
            notice('File deleted, but the catalog could not be updated: ' + error.message);
        }
    });
};

const preferenceIds = [
    'library-sort',
    'upload-compression',
    'zip',
    'zip-name',
    'download-name',
    'redirect-name',
    'redirect-lifetime',
    'redirect-minutes',
    'animation-url',
    'animation-shell',
    'animation-fps',
    'animation-width',
    'animation-height',
    'animation-offset',
    'animation-color',
    'animation-pixel',
    'animation-hdr',
    'animation-invert',
];
const defaultPreferences = Object.fromEntries(
    preferenceIds.map((id) => [id, $(id).type === 'checkbox' ? $(id).checked : $(id).value]),
);
function applyPreferences(values) {
    if (values.mode === 'server' || values.mode === 'custom') settings.mode = values.mode;
    if (typeof values.webhook === 'string') settings.webhook = values.webhook;
    if (typeof values.encrypt === 'boolean') settings.encrypt = values.encrypt;
    for (const id of preferenceIds) {
        const el = $(id),
            value = values[id];
        if (value === undefined) continue;
        if (el.type === 'checkbox') {
            if (typeof value === 'boolean') el.checked = value;
        } else if (typeof value === 'string') el.value = value;
    }
    $('zip-name-row').hidden = !$('zip').checked;
    updateLifetime();
    updateDestination();
    updateAnimation();
}
applyPreferences(defaultPreferences);
function currentPreferences() {
    return {
        mode: settings.mode,
        webhook: settings.webhook,
        encrypt: settings.encrypt,
        ...Object.fromEntries(
            preferenceIds.map((id) => [
                id,
                $(id).type === 'checkbox' ? $(id).checked : $(id).value,
            ]),
        ),
    };
}
function savePreferences(patch) {
    if (vault.store.unlocked)
        vault.store
            .setPreferences(patch)
            .catch((error) => notice('Could not save settings: ' + error.message, true));
    else notice('Open the catalog to save settings.', true);
}
let lastPreferences = '';
const vault = setupCatalog({
    linkFor: seedLink,
    canLock: () => !busy,
    onShorten: (entry) => {
        if (busy) return;
        shortLinkSource = entry.seed;
        const url = new URL(seedLink(entry.seed, entry.name));
        if (entry.redirect) url.searchParams.set('redirect', '');
        $('redirect-url').value = url.href;
        $('redirect-name').value = safeName(entry.name).slice(0, 55);
        savePreferences({ 'redirect-name': $('redirect-name').value });
        tab('redirect');
        $('redirect-lifetime').focus();
    },
    onUnlock: (store) => store.setPreferences(currentPreferences(), true),
    onPreferences: (preferences) => {
        const serialized = JSON.stringify(preferences);
        if (serialized === lastPreferences || busy) return;
        lastPreferences = serialized;
        const values = Object.fromEntries(
            Object.entries(preferences).map(([key, item]) => [key, item.value]),
        );
        applyPreferences(values);
        timing.load(values['upload-timing']);
    },
    onDelete: (token) => {
        $('delete-token').value = token;
        $('delete-confirm').checked = false;
        tab('delete');
    },
    onLock: () => {
        if (vault.store.unlocked) return;
        for (const result of results) {
            result.node.remove();
            if (result.raw) URL.revokeObjectURL(result.url);
        }
        results = [];
        $('results-section').hidden = true;
        $('delete-token').value = '';
        settings = { mode: 'server', webhook: '', encrypt: true };
        lastPreferences = '';
        shortLinkSource = '';
        for (const id of [
            'webhook',
            'download-input',
            'redirect-url',
            'delete-token',
            'catalog-search',
        ])
            $(id).value = '';
        $('delete-confirm').checked = false;
        $('notice').textContent = '';
        $('notice').hidden = true;
        files = [];
        activeBatch = null;
        timing.samples = [];
        timing.saved = '';
        $('file-picker').value = '';
        $('activity').hidden = true;
        $('activity-label').textContent = '';
        $('activity-detail').textContent = '';
        $('animation-image').removeAttribute('src');
        $('animation-image').hidden = true;
        applyPreferences(defaultPreferences);
        $('settings').close();
        renderQueue();
    },
});

function updateEncryptionHint() {
    $('encryption-help').textContent = !$('upload-encryption').checked
        ? 'File content will be stored without encryption. Catalog encryption is configured separately.'
        : 'Protect stored content. The key is included in the link.';
}

$('upload-encryption').onchange = updateEncryptionHint;

for (const id of preferenceIds)
    $(id).addEventListener('change', () =>
        savePreferences({ [id]: $(id).type === 'checkbox' ? $(id).checked : $(id).value }),
    );

var preferenceRefresh = () => vault.store.emit();

function updateLifetime() {
    $('redirect-custom-lifetime').hidden = $('redirect-lifetime').value !== 'custom';
    $('redirect-minutes').disabled = $('redirect-lifetime').value !== 'custom';
}
$('redirect-lifetime').addEventListener('change', updateLifetime);
updateLifetime();

function restoreSection() {
    const value = location.hash.slice(1).toLowerCase();
    const aliases = {
        archivos: 'files',
        enlaces: 'redirect',
        subir: 'upload',
        animacion: 'animation',
    };
    tab(
        Object.keys(sectionHashes).find((key) => sectionHashes[key] === value) ||
            aliases[value] ||
            'upload',
        false,
    );
}
window.addEventListener('hashchange', restoreSection);
window.addEventListener('popstate', restoreSection);
restoreSection();
