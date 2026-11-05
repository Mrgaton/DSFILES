// ZIP32, method STORE: keeps original bytes (including ZIP/7z files) without recompression.
// CRC is calculated incrementally; Blob parts reference the original File objects.
const table = Uint32Array.from({ length: 256 }, (_, n) => {
    for (let i = 0; i < 8; i++) n = n & 1 ? 0xedb88320 ^ (n >>> 1) : n >>> 1;
    return n >>> 0;
});
function header(size) {
    const bytes = new Uint8Array(size);
    return [bytes, new DataView(bytes.buffer)];
}
export async function makeZip(files, signal, report) {
    if (files.length > 65535) throw new Error('ZIP supports up to 65,535 files.');
    const local = [],
        central = [],
        names = new Set();
    let offset = 0,
        processed = 0;
    const total = files.reduce((sum, f) => sum + f.size, 0);
    for (const file of files) {
        signal.throwIfAborted();
        let name = file.name.replaceAll('\\', '/').split('/').pop() || 'file';
        const original = name;
        let suffix = 2;
        while (names.has(name)) {
            const dot = original.lastIndexOf('.');
            name =
                dot > 0
                    ? original.slice(0, dot) + ' (' + suffix++ + ')' + original.slice(dot)
                    : original + ' (' + suffix++ + ')';
        }
        names.add(name);
        const encoded = new TextEncoder().encode(name);
        if (
            encoded.length > 65535 ||
            file.size > 0xffffffff ||
            offset + 30 + encoded.length + file.size > 0xffffffff
        )
            throw new Error('This ZIP exceeds 4 GB. Upload the files separately.');
        let crc = 0xffffffff;
        const reader = file.stream().getReader();
        try {
            while (true) {
                signal.throwIfAborted();
                const { done, value } = await reader.read();
                if (done) break;
                for (const byte of value) crc = table[(crc ^ byte) & 255] ^ (crc >>> 8);
                processed += value.length;
                report(total ? processed / total : 1);
                await new Promise((resolve) => setTimeout(resolve, 0));
            }
        } finally {
            await reader.cancel();
            reader.releaseLock();
        }
        crc = (crc ^ 0xffffffff) >>> 0;
        const d = new Date(file.lastModified || Date.now()),
            year = Math.min(2107, Math.max(1980, d.getFullYear()));
        const date = ((year - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate(),
            time = (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1);
        const [a, v] = header(30);
        v.setUint32(0, 0x04034b50, true);
        v.setUint16(4, 20, true);
        v.setUint16(6, 0x800, true);
        v.setUint16(10, time, true);
        v.setUint16(12, date, true);
        v.setUint32(14, crc, true);
        v.setUint32(18, file.size, true);
        v.setUint32(22, file.size, true);
        v.setUint16(26, encoded.length, true);
        local.push(a, encoded, file);
        const [b, w] = header(46);
        w.setUint32(0, 0x02014b50, true);
        w.setUint16(4, 20, true);
        w.setUint16(6, 20, true);
        w.setUint16(8, 0x800, true);
        w.setUint16(12, time, true);
        w.setUint16(14, date, true);
        w.setUint32(16, crc, true);
        w.setUint32(20, file.size, true);
        w.setUint32(24, file.size, true);
        w.setUint16(28, encoded.length, true);
        w.setUint32(42, offset, true);
        central.push(b, encoded);
        offset += 30 + encoded.length + file.size;
    }
    const size = central.reduce((sum, a) => sum + a.length, 0);
    if (offset + size + 22 > 0xffffffff) throw new Error('The ZIP exceeds 4 GB.');
    const [end, v] = header(22);
    v.setUint32(0, 0x06054b50, true);
    v.setUint16(8, files.length, true);
    v.setUint16(10, files.length, true);
    v.setUint32(12, size, true);
    v.setUint32(16, offset, true);
    return new Blob([...local, ...central, end], { type: 'application/zip' });
}
