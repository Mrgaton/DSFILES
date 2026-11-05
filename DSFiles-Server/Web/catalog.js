import { GOOGLE_CLIENT_ID } from './config.js';
const PLAIN_FORMAT = 'dsfiles-catalog-plain-v1';
const FORMAT = 'dsfiles-catalog-v1',
    ITERATIONS = 600000,
    MAX_BYTES = 4 * 1024 * 1024;
const encoder = new TextEncoder(),
    decoder = new TextDecoder('utf-8', { fatal: true });
const aad = encoder.encode(FORMAT);
const sessionAad = encoder.encode(FORMAT + ':local-session');
export const CACHE_KEY = 'dsfiles.encrypted-catalog.v1';
const DIRTY_KEY = 'dsfiles.catalog-pending.v1';
const SESSION_KEY = 'dsfiles.google-session.v1';
const LEGACY_SETTINGS_KEY = 'dsfiles.webhook.v1',
    LEGACY_PREFERENCES_KEY = 'dsfiles.preferences.v1';
function migratePreferences(preferences) {
    const patch = Object.create(null);
    for (const cacheKey of [LEGACY_SETTINGS_KEY, LEGACY_PREFERENCES_KEY]) {
        const text = localStorage.getItem(cacheKey);
        if (!text) continue;
        const saved = JSON.parse(text);
        if (!saved || typeof saved !== 'object' || Array.isArray(saved)) {
            throw Error(
                'Invalid saved settings. Export your catalog before clearing browser data.',
            );
        }
        for (const [key, value] of Object.entries(saved)) {
            if (!Object.hasOwn(preferences, key)) patch[key] = { value, time: 0 };
        }
    }
    return mergePreferences(preferences, patch);
}
const to64 = (bytes) => {
    let s = '';
    for (let i = 0; i < bytes.length; i += 8192)
        s += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(s);
};
const from64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
function validateEntry(e) {
    if (
        e?.expiresAt !== undefined &&
        (!Number.isSafeInteger(e.expiresAt) || e.expiresAt < 0 || e.expiresAt > 8640000000000000)
    )
        throw Error('Invalid expiration.');
    if (
        e?.sourceSeed !== undefined &&
        (typeof e.sourceSeed !== 'string' ||
            e.sourceSeed.length > 60000 ||
            !/^[-_$A-Za-z0-9]*$/.test(e.sourceSeed))
    )
        throw Error('Invalid source file.');
    if (e?.targetUrl !== undefined) {
        let url;
        try {
            url = new URL(e.targetUrl);
        } catch {
            throw Error('Invalid original URL.');
        }
        if (
            typeof e.targetUrl !== 'string' ||
            e.targetUrl.length > 8192 ||
            !['https:', 'http:'].includes(url.protocol) ||
            url.username ||
            url.password
        )
            throw Error('Invalid original URL.');
    }

    if (
        !e ||
        typeof e.id !== 'string' ||
        e.id.length > 60000 ||
        typeof e.seed !== 'string' ||
        !/^[-_A-Za-z0-9]+(?:\$[-_A-Za-z0-9]+)?$/.test(e.seed) ||
        (e.shortKey !== undefined &&
            (typeof e.shortKey !== 'string' ||
                !/^[-_A-Za-z0-9]{4,64}$/.test(e.shortKey) ||
                e.seed !== 'jsp_' + e.shortKey ||
                e.redirect !== true)) ||
        (e.receipt !== undefined &&
            (typeof e.receipt !== 'string' || e.receipt.length > 1000000)) ||
        e.id !== e.seed ||
        typeof e.name !== 'string' ||
        e.name.length > 256 ||
        typeof e.removeToken !== 'string' ||
        e.removeToken.length > 60000 ||
        !Number.isSafeInteger(e.size) ||
        e.size < 0 ||
        !Number.isFinite(e.createdAt) ||
        typeof e.deleted !== 'boolean' ||
        typeof e.redirect !== 'boolean'
    )
        throw Error('Invalid catalog entry.');
    return {
        id: e.id,
        seed: e.seed,
        name: e.name,
        removeToken: e.removeToken,
        size: e.size,
        createdAt: e.createdAt,
        deleted: e.deleted,
        redirect: e.redirect,
        ...(e.receipt ? { receipt: e.receipt } : {}),
        ...(e.shortKey ? { shortKey: e.shortKey } : {}),
        ...(e.expiresAt !== undefined ? { expiresAt: e.expiresAt } : {}),
        ...(e.targetUrl ? { targetUrl: e.targetUrl } : {}),
        ...(e.sourceSeed ? { sourceSeed: e.sourceSeed } : {}),
    };
}
export function mergeEntries(...groups) {
    const entries = new Map();
    for (const group of groups) {
        if (!Array.isArray(group) || group.length > 20000)
            throw Error('The catalog exceeds 20,000 entries.');
        for (const raw of group) {
            const e = validateEntry(raw),
                old = entries.get(e.id);
            // Deletion is permanent for a seed, independent of clocks on different devices.
            entries.set(e.id, old ? { ...old, deleted: old.deleted || e.deleted } : e);
        }
    }
    if (entries.size > 20000) throw Error('The catalog exceeds 20,000 entries.');
    return [...entries.values()];
}
export function mergePreferences(...groups) {
    const merged = Object.create(null);
    for (const group of groups) {
        if (!group) continue;
        if (typeof group !== 'object' || Array.isArray(group) || Object.keys(group).length > 100)
            throw Error('Invalid preferences.');
        for (const [key, item] of Object.entries(group)) {
            if (
                !/^[a-z][a-z0-9-]{0,63}$/.test(key) ||
                !item ||
                !Number.isSafeInteger(item.time) ||
                item.time < 0 ||
                !['string', 'boolean', 'number'].includes(typeof item.value) ||
                String(item.value).length > 8192
            )
                throw Error('Invalid preference.');
            const old = merged[key];
            if (
                !old ||
                item.time > old.time ||
                (item.time === old.time && JSON.stringify(item.value) > JSON.stringify(old.value))
            )
                merged[key] = { value: item.value, time: item.time };
        }
    }
    return merged;
}
export async function deriveKey(password, salt) {
    const material = await crypto.subtle.importKey(
        'raw',
        encoder.encode(password),
        'PBKDF2',
        false,
        ['deriveKey'],
    );
    return crypto.subtle.deriveKey(
        { name: 'PBKDF2', hash: 'SHA-256', iterations: ITERATIONS, salt },
        material,
        { name: 'AES-GCM', length: 256 },
        false,
        ['encrypt', 'decrypt'],
    );
}
export async function seal(entries, key, salt, preferences = {}, localSession) {
    if (!key) {
        const text = JSON.stringify({
            format: PLAIN_FORMAT,
            entries: mergeEntries(entries),
            preferences: mergePreferences(preferences),
        });
        if (encoder.encode(text).length > MAX_BYTES) throw Error('The catalog exceeds 4 MiB.');
        return text;
    }
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const plain = encoder.encode(
        JSON.stringify({
            entries: mergeEntries(entries),
            preferences: mergePreferences(preferences),
        }),
    );
    const ciphertext = new Uint8Array(
        await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: aad }, key, plain),
    );
    // Separate authenticated ciphertext lets locked exports omit OAuth without a password.
    let authorization;
    if (localSession) {
        const sessionIv = crypto.getRandomValues(new Uint8Array(12));
        const data = new Uint8Array(
            await crypto.subtle.encrypt(
                { name: 'AES-GCM', iv: sessionIv, additionalData: sessionAad },
                key,
                encoder.encode(JSON.stringify(localSession)),
            ),
        );
        authorization = { iv: to64(sessionIv), data: to64(data) };
    }
    const result = JSON.stringify({
        format: FORMAT,
        kdf: 'PBKDF2-SHA256',
        iterations: ITERATIONS,
        salt: to64(salt),
        iv: to64(iv),
        data: to64(ciphertext),
        ...(authorization ? { localSession: authorization } : {}),
    });
    if (encoder.encode(result).length > MAX_BYTES)
        throw Error('The catalog exceeds 4 MiB. Export a backup.');
    return result;
}
export async function unseal(text, password) {
    if (typeof text !== 'string' || text.length > MAX_BYTES) throw Error('Backup is too large.');
    let box, salt, iv, data;
    try {
        box = JSON.parse(text);
        if (box.format === PLAIN_FORMAT)
            return {
                entries: mergeEntries(box.entries),
                preferences: mergePreferences(box.preferences),
                key: null,
                salt: null,
            };
        if (box.format !== FORMAT || box.kdf !== 'PBKDF2-SHA256' || box.iterations !== ITERATIONS)
            throw Error();
        salt = from64(box.salt);
        iv = from64(box.iv);
        data = from64(box.data);
        if (salt.length !== 16 || iv.length !== 12 || data.length < 16) throw Error();
    } catch {
        throw Error('Invalid catalog format.');
    }
    if (!password) {
        const error = Error(
            'Optional encryption is enabled for this catalog. Enter its password to open it or disable encryption.',
        );
        error.code = 'PASSWORD_REQUIRED';
        throw error;
    }
    const key = await deriveKey(password, salt);
    try {
        const plain = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv, additionalData: aad },
            key,
            data,
        );
        const payload = JSON.parse(decoder.decode(plain));
        let localSession;
        if (box.localSession) {
            try {
                const session = await crypto.subtle.decrypt(
                    {
                        name: 'AES-GCM',
                        iv: from64(box.localSession.iv),
                        additionalData: sessionAad,
                    },
                    key,
                    from64(box.localSession.data),
                );
                localSession = JSON.parse(decoder.decode(session));
            } catch {
                /* A damaged local authorization must not prevent catalog recovery. */
            }
        }
        return {
            entries: mergeEntries(payload.entries),
            preferences: mergePreferences(payload.preferences),
            key,
            salt,
            localSession,
        };
    } catch {
        throw Error('Incorrect password or damaged catalog. No data was overwritten.');
    }
}

export class Catalog {
    constructor(onChange = () => {}) {
        this.onChange = onChange;
        this.preferences = {};
        this.entries = [];
        this.password = null;
        this.key = null;
        this.salt = null;
        this.token = null;
        this.expires = 0;
        this.pending = false;
        this.memoryDirty = false;
        this.chain = Promise.resolve();
        this.ready = false;
        this.allowPlainMigration = false;
        this.status = 'Catalog locked';
        this.active = 0;
        this.timer = null;
    }
    get unlocked() {
        return this.ready;
    }
    get encrypted() {
        return !!this.key;
    }
    emit(status) {
        if (status) this.status = status;
        this.onChange(this);
    }
    enqueue(fn) {
        this.active++;
        this.emit();
        const run = () => (navigator.locks ? navigator.locks.request(CACHE_KEY, fn) : fn());
        const task = this.chain.then(run);
        this.chain = task.catch(() => {});
        return task.finally(() => {
            this.active--;
            this.emit();
        });
    }
    async unlock(password) {
        return this.enqueue(async () => {
            const text = localStorage.getItem(CACHE_KEY);
            const opened = text
                ? await unseal(text, password)
                : { entries: [], preferences: {}, key: null, salt: null };
            this.password = password || null;
            this.key = opened.key;
            this.salt = opened.salt;
            this.entries = opened.entries;
            this.preferences = opened.preferences;
            this.ready = true;
            this.pending = localStorage.getItem(DIRTY_KEY) === '1';
            this.preferences = migratePreferences(this.preferences);
            if (!this.token || this.expires <= Date.now()) this.restoreToken(opened.localSession);
            await this.persist(
                !text ||
                    this.pending ||
                    localStorage.getItem(LEGACY_SETTINGS_KEY) !== null ||
                    localStorage.getItem(LEGACY_PREFERENCES_KEY) !== null,
            );
            this.emit(this.pending ? 'Saved locally · pending Drive sync' : 'Catalog open');
        });
    }
    async setEncryption(enabled, password) {
        if (!this.unlocked) throw Error('Open the existing catalog first.');
        await this.enqueue(async () => {
            await this.mergeLocal();
            const protection = {
                key: null,
                salt: null,
                password: this.password,
                allowPlainMigration: true,
            };
            if (enabled) {
                if (!password || password.length < 12) throw Error('Use at least 12 characters.');
                protection.salt = crypto.getRandomValues(new Uint8Array(16));
                protection.key = await deriveKey(password, protection.salt);
                protection.password = password;
                protection.allowPlainMigration = false;
            }
            const dirty = this.memoryDirty,
                pending = this.pending;
            try {
                await this.persist(true, protection);
            } catch (error) {
                if (this.key !== protection.key) {
                    this.memoryDirty = dirty;
                    this.pending = pending;
                }
                throw error;
            }
            this.emit(
                enabled
                    ? 'Optional encryption enabled · settings and local Google session protected'
                    : 'Catalog encryption disabled · saved locally',
            );
        });
        this.schedule();
    }
    async mergeLocal() {
        const text = localStorage.getItem(CACHE_KEY);
        if (text) {
            const opened = await unseal(text, this.password);
            this.entries = mergeEntries(this.entries, opened.entries);
            this.preferences = mergePreferences(this.preferences, opened.preferences);
        }
    }
    localSession() {
        return this.token && this.expires > Date.now()
            ? { clientId: GOOGLE_CLIENT_ID, token: this.token, expires: this.expires }
            : null;
    }
    async persist(pending = true, protection = { key: this.key, salt: this.salt }) {
        if (this.memoryDirty) pending = true;
        this.memoryDirty = true;
        const encrypted = await seal(
            this.entries,
            protection.key,
            protection.salt,
            this.preferences,
            this.localSession(),
        );
        // Set pending before the cache so an interrupted write never claims sync success.
        if (pending) localStorage.setItem(DIRTY_KEY, '1');
        localStorage.setItem(CACHE_KEY, encrypted);
        Object.assign(this, protection);
        this.pending = pending;
        if (this.key) localStorage.removeItem(SESSION_KEY);
        else if (this.localSession())
            localStorage.setItem(SESSION_KEY, JSON.stringify(this.localSession()));
        localStorage.removeItem(LEGACY_SETTINGS_KEY);
        localStorage.removeItem(LEGACY_PREFERENCES_KEY);
        if (!pending) localStorage.removeItem(DIRTY_KEY);
        this.memoryDirty = false;
    }
    async add(record) {
        if (!this.unlocked) throw Error('Unlock the catalog before uploading.');
        await this.enqueue(async () => {
            await this.mergeLocal();
            this.entries = mergeEntries(this.entries, [
                { ...record, id: record.seed, deleted: false, createdAt: Date.now() },
            ]);
            this.memoryDirty = true;
            await this.persist();
            this.emit('Saved locally · pending Drive sync');
        });
        this.schedule();
    }
    async setPreferences(values, defaults = false) {
        if (!this.unlocked) return;
        await this.enqueue(async () => {
            await this.mergeLocal();
            const patch = {};
            for (const [key, value] of Object.entries(values)) {
                const old = this.preferences[key];
                if (defaults && old) continue;
                if (old?.value === value) continue;
                patch[key] = {
                    value,
                    time: defaults ? 0 : Math.max(Date.now(), (old?.time || 0) + 1),
                };
            }
            if (!Object.keys(patch).length) return;
            this.preferences = mergePreferences(this.preferences, patch);
            await this.persist();
            this.emit('Settings saved · pending Drive sync');
        });
        this.schedule();
    }
    async deleted(token) {
        if (!this.unlocked) return;
        await this.enqueue(async () => {
            await this.mergeLocal();
            this.entries = this.entries.map((e) =>
                e.removeToken === token ? { ...e, deleted: true } : e,
            );
            await this.persist();
            this.emit('Deletion saved · pending Drive sync');
        });
        this.schedule();
    }
    schedule() {
        clearTimeout(this.timer);
        if (this.token)
            this.timer = setTimeout(
                () => this.sync().catch((error) => this.emit(error.message)),
                500,
            );
    }
    async setToken(token, seconds) {
        if (typeof token !== 'string' || !token || !Number.isFinite(seconds) || seconds <= 30)
            throw Error('Google returned invalid authorization.');
        this.token = token;
        this.expires = Date.now() + seconds * 1000 - 30000;
        try {
            if (this.encrypted) await this.enqueue(() => this.persist(this.pending));
            else if (
                this.unlocked ||
                JSON.parse(localStorage.getItem(CACHE_KEY))?.format !== FORMAT
            )
                localStorage.setItem(SESSION_KEY, JSON.stringify(this.localSession()));
        } catch {
            this.emit('Connected, but this browser cannot remember the session.');
        }
    }
    restoreToken(localSession) {
        try {
            // Do not load an old plaintext token before an encrypted catalog is unlocked.
            if (!this.unlocked && JSON.parse(localStorage.getItem(CACHE_KEY))?.format === FORMAT)
                return false;
            const saved = localSession || JSON.parse(localStorage.getItem(SESSION_KEY));
            if (
                saved?.clientId === GOOGLE_CLIENT_ID &&
                typeof saved.token === 'string' &&
                saved.token.length > 0 &&
                saved.token.length < 20000 &&
                Number.isFinite(saved.expires) &&
                saved.expires > Date.now()
            ) {
                this.token = saved.token;
                this.expires = saved.expires;
                return true;
            }
        } catch {}
        this.token = null;
        this.expires = 0;
        localStorage.removeItem(SESSION_KEY);
        return false;
    }
    async disconnect() {
        this.token = null;
        this.expires = 0;
        clearTimeout(this.timer);
        try {
            localStorage.removeItem(SESSION_KEY);
        } catch {}
        if (this.unlocked && this.encrypted) await this.enqueue(() => this.persist(this.pending));
    }
    async api(url, options = {}) {
        if (!this.token || Date.now() >= this.expires) {
            this.token = null;
            this.expires = 0;
            localStorage.removeItem(SESSION_KEY);
            if (this.encrypted) await this.persist(this.pending);
            throw Error('Reconnect Google Drive. Changes are still saved locally.');
        }
        const response = await fetch(url, {
            ...options,
            headers: { ...options.headers, Authorization: 'Bearer ' + this.token },
            signal: AbortSignal.timeout(45000),
        });
        if (response.status === 401) {
            this.token = null;
            this.expires = 0;
            localStorage.removeItem(SESSION_KEY);
            if (this.encrypted) await this.persist(this.pending);
            throw Error('Your Google session expired. Reconnect Drive.');
        }
        if (!response.ok) {
            let detail = '',
                reasons = [];
            try {
                const { error } = await response.json();
                if (typeof error?.message === 'string') detail = error.message.slice(0, 1000);
                reasons = [
                    ...(error?.errors || []).map((item) => item.reason),
                    ...(error?.details || []).map((item) => item.reason),
                ].filter((value) => typeof value === 'string');
            } catch {}
            const disabled = reasons.some(
                (reason) => reason === 'accessNotConfigured' || reason === 'SERVICE_DISABLED',
            );
            const hint = disabled
                ? ' Enable Google Drive API in the Google Cloud project that owns this OAuth client.'
                : '';
            throw Error(
                'Drive returned HTTP ' +
                    response.status +
                    (detail ? ': ' + detail : '.') +
                    hint +
                    ' Changes are still pending.',
            );
        }
        return response;
    }
    async listSnapshots() {
        let token = '',
            files = [];
        do {
            const query = new URLSearchParams({
                spaces: 'appDataFolder',
                q: "name = 'dsfiles-catalog-v1.json' and trashed = false",
                fields: 'nextPageToken,files(id,size)',
                pageSize: '100',
            });
            if (token) query.set('pageToken', token);
            const page = await (
                await this.api('https://www.googleapis.com/drive/v3/files?' + query)
            ).json();
            files.push(...(page.files || []));
            token = page.nextPageToken || '';
            if (files.length > 1000)
                throw Error('Too many remote copies. Export a backup before continuing.');
        } while (token);
        return files;
    }
    async sync() {
        if (!this.unlocked) throw Error('Unlock the catalog.');
        return this.enqueue(async () => {
            this.emit('Syncing with Drive…');
            await this.mergeLocal();
            const snapshots = await this.listSnapshots();
            let merged = this.entries,
                preferences = this.preferences,
                protection = { key: this.key, salt: this.salt };
            for (const snapshot of snapshots) {
                if (Number(snapshot.size) > MAX_BYTES) throw Error('Remote copy is too large.');
                const response = await this.api(
                    'https://www.googleapis.com/drive/v3/files/' +
                        encodeURIComponent(snapshot.id) +
                        '?alt=media',
                );
                const text = await response.text();
                const opened = await unseal(text, this.password);
                if (opened.key && !protection.key && !this.allowPlainMigration)
                    protection = { key: opened.key, salt: opened.salt };
                merged = mergeEntries(merged, opened.entries);
                preferences = mergePreferences(preferences, opened.preferences);
            }
            this.entries = merged;
            this.preferences = preferences;
            await this.persist(true, protection);
            const encrypted = await seal(merged, this.key, this.salt, this.preferences);
            const boundary = 'dsfiles_' + crypto.randomUUID();
            const metadata = JSON.stringify({
                name: 'dsfiles-catalog-v1.json',
                parents: ['appDataFolder'],
                mimeType: 'application/json',
            });
            const body =
                '--' +
                boundary +
                '\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n' +
                metadata +
                '\r\n--' +
                boundary +
                '\r\nContent-Type: application/json\r\n\r\n' +
                encrypted +
                '\r\n--' +
                boundary +
                '--';
            const created = await (
                await this.api(
                    'https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id',
                    {
                        method: 'POST',
                        headers: { 'Content-Type': 'multipart/related; boundary=' + boundary },
                        body,
                    },
                )
            ).json();
            if (!created.id) throw Error('Drive did not confirm the save. Changes are pending.');
            await this.persist(false);
            // Remove only immutable snapshots read before creating this merged copy.
            // Concurrent writers' new snapshots remain and are merged on the next sync.
            let cleanupFailed = false;
            for (const snapshot of snapshots) {
                try {
                    await this.api(
                        'https://www.googleapis.com/drive/v3/files/' +
                            encodeURIComponent(snapshot.id),
                        { method: 'DELETE' },
                    );
                } catch {
                    cleanupFailed = true;
                }
            }
            this.emit(
                cleanupFailed
                    ? 'Synced · old copies still need cleanup'
                    : 'Synced with Google Drive',
            );
        });
    }
    async export() {
        return this.enqueue(async () => {
            if (this.unlocked) {
                await this.mergeLocal();
                return seal(this.entries, this.key, this.salt, this.preferences);
            }
            const text = localStorage.getItem(CACHE_KEY);
            if (!text) throw Error('There is no catalog to export.');
            const backup = JSON.parse(text);
            delete backup.localSession;
            return JSON.stringify(backup);
        });
    }
    async import(text, password) {
        await this.enqueue(async () => {
            const incoming = await unseal(text, password);
            if (!this.unlocked) throw Error('Unlock or create the destination catalog first.');
            await this.mergeLocal();
            this.entries = mergeEntries(this.entries, incoming.entries);
            this.preferences = mergePreferences(this.preferences, incoming.preferences);
            await this.persist();
            this.emit('Backup imported · pending Drive sync');
        });
        this.schedule();
    }
    async lock() {
        if (this.active || this.memoryDirty)
            throw Error(
                'An operation is pending or changes are unsaved. Export a backup before closing this page.',
            );
        await this.disconnect();
        this.ready = false;
        this.allowPlainMigration = false;
        this.preferences = {};
        this.entries = [];
        this.password = null;
        this.key = null;
        this.salt = null;
        this.emit('Catalog locked');
    }
}
