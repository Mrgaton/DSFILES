export function readUploadAuthorization(response) {
    const session = response.header('Session-ID');
    const chunkSize = Number(response.header('ChunkSize'));
    if (
        response.header('Upload-Protocol') !== '2' ||
        !session ||
        !Number.isSafeInteger(chunkSize) ||
        chunkSize <= 0
    )
        throw new Error(
            'The server did not return a compatible upload authorization. Update the server and reload this page.',
        );
    return { session, chunkSize, next: 0, receipts: [], idempotent: false };
}

export function readChunkReceipt(response) {
    try {
        const { receipt } = JSON.parse(response.text);
        if (typeof receipt !== 'string' || receipt.length > 2048 || receipt.split('.').length !== 3)
            throw new Error();
        return receipt;
    } catch {
        throw Object.assign(new Error('The server did not confirm the uploaded fragment.'), {
            processedUnknown: true,
        });
    }
}

export function chunkManifest(receipts) {
    return new Blob([JSON.stringify({ receipts })], { type: 'application/json' });
}

export async function abortChunkUpload(cursor, request) {
    if (!cursor?.receipts?.length) return;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
        return await request(
            '/cua',
            chunkManifest(cursor.receipts),
            { 'Session-ID': cursor.session },
            controller.signal,
        );
    } finally {
        clearTimeout(timer);
    }
}
