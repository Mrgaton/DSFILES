export function safeName(name, maxLength = 64) {
    const normalized = name
        .normalize('NFKD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/[^a-zA-Z0-9._ -]/g, '_');
    if (!normalized) return 'file';
    if (normalized.length <= maxLength) return normalized;
    const compound = normalized.match(/\.tar\.(?:gz|bz2|xz|zst)$/i);
    const dot = compound ? normalized.length - compound[0].length : normalized.lastIndexOf('.');
    if (dot > 0 && dot < normalized.length - 1) {
        const extension = normalized.slice(dot);
        if (extension.length < maxLength)
            return normalized.slice(0, maxLength - extension.length) + extension;
    }
    return normalized.slice(0, maxLength);
}

export function apiError(status, text, path) {
    let details = null;
    try {
        const parsed = JSON.parse(text);
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) details = parsed;
    } catch {}
    const fallback =
        status === 410 && path === '/cuc'
            ? 'The upload session expired. Upload the file again.'
            : status === 409
              ? 'The server rejected the chunk order. Try again.'
              : status === 429
                ? 'Too many requests. Wait before trying again.'
                : 'The server returned HTTP ' + status + '.';
    let message = typeof details?.error === 'string' ? details.error.slice(0, 1000) : fallback;
    if (!details && text && text.length <= 1000 && !/[<>]/.test(text)) message += ' ' + text.trim();
    if (
        Number.isSafeInteger(details?.removed) &&
        Number.isSafeInteger(details?.total) &&
        details.removed >= 0 &&
        details.total >= details.removed
    ) {
        message +=
            ' Confirmed deletion: ' +
            details.removed +
            ' of ' +
            details.total +
            '. Keep the token and retry to finish.';
    }
    const error = new Error(message);
    error.status = status;
    error.details = details;
    return error;
}
