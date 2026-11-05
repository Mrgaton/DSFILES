import { Catalog } from './catalog.js';
import { GOOGLE_CLIENT_ID } from './config.js';
const scope = 'https://www.googleapis.com/auth/drive.appdata';
export function setupCatalog({
    linkFor,
    onDelete,
    onShorten,
    onLock,
    canLock,
    onPreferences = () => {},
    onUnlock = async () => {},
}) {
    const $ = (id) => document.getElementById(id);
    const dialog = $('catalog-dialog');
    const pagination = $('library-pagination');
    const pageSize = 50,
        rows = new Map();
    let page = 0,
        lastEntries = null,
        lastQuery = '',
        lastSort = '',
        filtered = [],
        searchTimer = null;
    let client = null,
        loading = false,
        passwordNeeded = false;
    const store = new Catalog(render);
    const report = (error) => {
        if (error.code === 'PASSWORD_REQUIRED') passwordNeeded = true;
        store.emit(error.message);
    };
    function action(fn) {
        return async () => {
            try {
                await fn();
            } catch (e) {
                report(e);
            }
        };
    }
    function render() {
        $('catalog-status').textContent = store.status;
        $('drive-warning').hidden = !!store.token && store.expires > Date.now();
        const count = store.entries.filter((e) => !e.deleted).length;
        $('library-status').textContent = !store.unlocked
            ? 'Catalog locked · open it in Account and backup'
            : count +
              ' file' +
              (count === 1 ? '' : 's') +
              ' · ' +
              (store.token ? 'Google Drive + local history' : 'Local history') +
              (store.pending && store.token ? ' · changes pending' : '') +
              ' · ' +
              store.status;
        if (store.unlocked) onPreferences(store.preferences);
        $('catalog-unlock').hidden = !passwordNeeded;
        $('catalog-content').hidden = !store.unlocked;
        $('catalog-sync').disabled = !store.token || !!store.active;
        $('catalog-connect').textContent = store.token
            ? 'Switch Google account'
            : 'Connect Google Drive';
        $('catalog-connect').disabled = !client || !!store.active;
        $('catalog-lock').disabled = !!store.active;
        $('catalog-lock').textContent = store.encrypted ? 'Lock' : 'Disconnect Google';
        $('catalog-encryption').disabled = !!store.active;
        $('catalog-encryption').textContent = store.encrypted
            ? 'Disable catalog encryption'
            : 'Enable optional encryption';
        $('catalog-new-password').hidden = store.encrypted;
        $('catalog-new-password-label').hidden = store.encrypted;
        $('catalog-protection').textContent = store.encrypted
            ? 'Password-encrypted: history, settings and the saved local Google session. Google authorization is excluded from backups and Drive sync.'
            : 'Saved locally without extra encryption. Drive access uses your Google account. Backups contain unencrypted data.';
        $('catalog-import').disabled = !!store.active;
        renderLibrary();
    }
    function renderLibrary() {
        const query = $('catalog-search').value.toLocaleLowerCase();
        const sort = $('library-sort').value;
        if (query !== lastQuery || sort !== lastSort) page = 0;
        if (store.entries === lastEntries && query === lastQuery && sort === lastSort) return;
        filtered = store.entries
            .filter((e) => !e.deleted && e.name.toLocaleLowerCase().includes(query))
            .sort((a, b) =>
                sort === 'name'
                    ? a.name.localeCompare(b.name)
                    : sort === 'size'
                      ? b.size - a.size
                      : b.createdAt - a.createdAt,
            );
        lastEntries = store.entries;
        lastQuery = query;
        lastSort = sort;
        renderPage();
    }
    function renderPage() {
        page = Math.max(0, Math.min(page, Math.ceil(filtered.length / pageSize) - 1));
        const entries = filtered.slice(page * pageSize, (page + 1) * pageSize),
            visible = new Set(entries.map((e) => e.id));
        $('catalog-empty').hidden = filtered.length > 0;
        const query = lastQuery;
        $('catalog-empty').textContent = !store.unlocked
            ? 'Open the catalog to view your files.'
            : query
              ? 'No matching files.'
              : 'Your next uploads will appear here. Connect Drive or import a backup from Account and backup.';
        pagination.hidden = filtered.length <= pageSize;
        $('library-previous').disabled = page === 0;
        $('library-next').disabled = (page + 1) * pageSize >= filtered.length;
        $('library-page').textContent = filtered.length
            ? 'Showing ' +
              (page * pageSize + 1) +
              '–' +
              Math.min((page + 1) * pageSize, filtered.length) +
              ' of ' +
              filtered.length
            : 'No files';
        for (const [id, row] of rows)
            if (!visible.has(id)) {
                const focused = row.node.contains(document.activeElement);
                row.node.remove();
                rows.delete(id);
                if (focused) $('catalog-search').focus();
            }
        let previous = null;
        for (const entry of entries) {
            const signature = JSON.stringify(entry),
                existing = rows.get(entry.id);
            if (existing?.signature === signature) {
                if (existing.node.previousElementSibling !== previous)
                    $('catalog-list').insertBefore(
                        existing.node,
                        previous
                            ? previous.nextElementSibling
                            : $('catalog-list').firstElementChild,
                    );
                previous = existing.node;
                continue;
            }
            const li = document.createElement('li');
            li.className = 'catalog-row';
            if (existing) existing.node.replaceWith(li);
            else
                $('catalog-list').insertBefore(
                    li,
                    previous ? previous.nextElementSibling : $('catalog-list').firstElementChild,
                );
            rows.set(entry.id, { node: li, signature });
            previous = li;
            if (entry.receipt) {
                const title = document.createElement('span');
                title.textContent = entry.name + ' · receipt';
                const download = document.createElement('button');
                download.className = 'text-button';
                download.textContent = 'Save receipt';
                download.onclick = () => {
                    const url = URL.createObjectURL(
                        new Blob([entry.receipt], { type: 'text/plain' }),
                    );
                    const a = document.createElement('a');
                    a.href = url;
                    a.download = entry.name + '.receipt.txt';
                    a.click();
                    setTimeout(() => URL.revokeObjectURL(url), 60000);
                };
                li.append(title, download);
                continue;
            }
            const url = entry.shortKey
                ? new URL('/s/' + entry.shortKey, location.origin)
                : new URL(linkFor(entry.seed, entry.name));
            if (entry.redirect && !entry.shortKey) url.searchParams.set('redirect', '');
            const title = document.createElement('a');
            title.textContent = entry.name;
            title.href = url.href;
            title.target = '_blank';
            title.rel = 'noopener noreferrer';
            li.append(title);
            const metadata = document.createElement('small');
            metadata.className = 'helper file-metadata';
            metadata.textContent =
                (entry.shortKey
                    ? 'JSPaste · external service'
                    : entry.redirect
                      ? 'Link'
                      : new Intl.NumberFormat('en', { maximumFractionDigits: 1 }).format(
                            entry.size / 1024,
                        ) + ' KB') +
                ' · ' +
                new Date(entry.createdAt).toLocaleDateString('en');
            if (entry.shortKey)
                metadata.textContent += entry.expiresAt
                    ? ' · ' +
                      (Date.now() >= entry.expiresAt ? 'Expiration reached: ' : 'Expires: ') +
                      new Date(entry.expiresAt).toLocaleString('en')
                    : entry.expiresAt === 0
                      ? ' · No expiration requested'
                      : ' · Expiration not confirmed';
            li.append(metadata);
            const actions = document.createElement('div');
            actions.className = 'catalog-actions';
            const button = (label, fn) => {
                const b = document.createElement('button');
                b.className = 'text-button';
                b.textContent = label;
                b.onclick = action(fn);
                actions.append(b);
            };
            if (!entry.shortKey) button('Shorten link', () => onShorten(entry));
            if (entry.shortKey && entry.targetUrl)
                button('Copy original', () => navigator.clipboard.writeText(entry.targetUrl));
            button('Copy link', async () => {
                await navigator.clipboard.writeText(url.href);
                store.emit('Link copied');
            });
            if (entry.redirect) {
                const open = document.createElement('a');
                open.href = url.href;
                open.target = '_blank';
                open.rel = 'noopener noreferrer';
                open.textContent = 'Open link';
                actions.append(open);
            }
            if (!entry.redirect) {
                const download = document.createElement('a');
                const dl = new URL(url);
                dl.searchParams.set('file', '');
                download.href = dl.href;
                download.textContent = 'Download';
                download.target = '_blank';
                download.rel = 'noopener noreferrer';
                actions.append(download);
            }
            if (entry.removeToken) {
                button('Copy token', () => navigator.clipboard.writeText(entry.removeToken));
                button(entry.shortKey ? 'Delete short link' : 'Delete', () => {
                    if (dialog.open) dialog.close();
                    onDelete(entry.removeToken);
                });
            }
            li.append(actions);
        }
    }
    function prepareGoogle() {
        if (!GOOGLE_CLIENT_ID) {
            store.emit('Saved locally. Set GOOGLE_CLIENT_ID in Web/config.js to connect Drive.');
            return;
        }
        if (client || loading) return;
        loading = true;
        const script = document.createElement('script');
        script.src = 'https://accounts.google.com/gsi/client';
        script.async = true;
        script.onerror = () => {
            loading = false;
            script.remove();
            store.emit('Could not load Google. Reopen the catalog to retry.');
        };
        script.onload = () => {
            loading = false;
            client = google.accounts.oauth2.initTokenClient({
                client_id: GOOGLE_CLIENT_ID,
                scope,
                include_granted_scopes: false,
                callback: async (response) => {
                    if (response.error) {
                        store.emit('Google did not authorize access: ' + response.error);
                        return;
                    }
                    if (!google.accounts.oauth2.hasGrantedAllScopes(response, scope)) {
                        store.emit('Google did not grant catalog access.');
                        return;
                    }
                    try {
                        await store.setToken(response.access_token, Number(response.expires_in));
                        if (!store.unlocked) {
                            store.emit(
                                'Open the existing catalog with its password to keep its data.',
                            );
                            return;
                        }
                        await store.sync();
                    } catch (e) {
                        report(e);
                    }
                },
                error_callback: () => store.emit('Connection cancelled. You can reconnect Drive.'),
            });
            render();
        };
        document.head.append(script);
    }
    const open = () => {
        if (!dialog.open) dialog.showModal();
        render();
        prepareGoogle();
    };
    $('drive-warning-connect').onclick = open;
    $('library-account').onclick = open;
    $('catalog-open').onclick = open;
    $('catalog-close').onclick = () => dialog.close();
    $('catalog-unlock').onsubmit = async (e) => {
        e.preventDefault();
        const password = $('catalog-password').value;
        $('catalog-password').value = '';
        try {
            if (!store.unlocked) {
                await store.unlock(password);
                await onUnlock(store);
            } else store.password = password;
            if (store.token) await store.sync();
            passwordNeeded = false;
            render();
        } catch (error) {
            report(error);
        }
    };
    $('catalog-origin').textContent =
        'If Google shows origin_mismatch, authorize this origin in Google Cloud: ' +
        location.origin;
    $('catalog-connect').onclick = action(() => {
        if (!client) throw Error('Google is still loading.');
        client.requestAccessToken({ prompt: 'select_account' });
    });
    $('catalog-sync').onclick = action(() => store.sync());
    $('catalog-lock').onclick = action(async () => {
        if (!canLock()) throw Error('Wait for the operation to finish.');
        if (store.encrypted) {
            await store.lock();
            passwordNeeded = true;
        } else {
            await store.disconnect();
            store.emit('Google disconnected · local catalog available');
        }
        $('catalog-backup-password').value = '';
        $('catalog-backup').value = '';
        $('catalog-new-password').value = '';
        onLock();
        render();
    });
    $('catalog-search').oninput = () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(renderLibrary, 150);
    };
    $('library-sort').onchange = renderLibrary;
    $('library-previous').onclick = () => {
        page--;
        renderPage();
    };
    $('library-next').onclick = () => {
        page++;
        renderPage();
    };
    $('catalog-export').onclick = action(async () => {
        const text = await store.export();
        const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = 'dsfiles-catalog-' + new Date().toISOString().slice(0, 10) + '.json';
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    });
    $('catalog-import').onclick = action(async () => {
        const file = $('catalog-backup').files[0];
        if (!file) throw Error('Choose a backup.');
        if (file.size > 4 * 1024 * 1024) throw Error('The backup exceeds 4 MiB.');
        const password = $('catalog-backup-password').value;
        $('catalog-backup-password').value = '';
        await store.import(await file.text(), password);
        $('catalog-backup').value = '';
    });
    window.addEventListener('beforeunload', (e) => {
        if (store.active || store.memoryDirty) {
            e.preventDefault();
            e.returnValue = '';
        }
    });
    $('catalog-encryption').onclick = action(async () => {
        if (!canLock()) throw Error('Wait for the operation to finish.');
        const password = $('catalog-new-password').value;
        await store.setEncryption(!store.encrypted, password);
        $('catalog-new-password').value = '';
    });
    setInterval(() => {
        $('drive-warning').hidden = !!store.token && store.expires > Date.now();
    }, 15000);
    store.restoreToken();
    render();
    store
        .unlock()
        .then(async () => {
            await onUnlock(store);
            if (store.token) await store.sync();
        })
        .catch(report);
    return {
        store,
        open,
        requireUnlocked() {
            if (store.unlocked) return true;
            open();
            store.emit('Open the catalog to save your uploads automatically.');
            return false;
        },
    };
}
