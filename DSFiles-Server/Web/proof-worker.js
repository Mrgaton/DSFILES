self.onmessage = async ({ data }) => {
    try {
        if (!self.crypto?.subtle) throw new Error('The check requires HTTPS or localhost.');
        const { challenge, difficulty, expiresAt } = data;
        if (
            !/^[a-f0-9]{48}$/.test(challenge) ||
            !Number.isInteger(difficulty) ||
            difficulty < 1 ||
            difficulty > 24 ||
            !Number.isFinite(expiresAt)
        )
            throw new Error('Invalid server challenge.');
        const encoder = new TextEncoder();
        for (let nonce = 0; nonce < Number.MAX_SAFE_INTEGER; nonce++) {
            if (nonce % 1024 === 0) {
                if (Date.now() >= expiresAt) throw new Error('The challenge expired. Try again.');
                self.postMessage({ attempts: nonce });
            }
            const proof = challenge + ':' + nonce;
            const hash = new Uint8Array(
                await crypto.subtle.digest('SHA-256', encoder.encode(proof)),
            );
            let valid = true;
            for (let bit = 0; bit < difficulty; bit++)
                if (hash[bit >> 3] & (1 << (7 - (bit & 7)))) {
                    valid = false;
                    break;
                }
            if (valid) {
                self.postMessage({ proof });
                return;
            }
        }
    } catch (error) {
        self.postMessage({ error: error.message });
    }
};
