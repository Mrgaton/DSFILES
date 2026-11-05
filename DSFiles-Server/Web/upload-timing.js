const MAX_SAMPLES = 60;
const DAY = 86400000;
const median = (values) => {
    const sorted = [...values].sort((a, b) => a - b);
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
};

export class UploadTiming {
    constructor() {
        this.samples = [];
        this.started = Date.now();
        this.saved = '';
    }

    load(text) {
        if (typeof text !== 'string' || text === this.saved) return;
        try {
            const samples = JSON.parse(text);
            if (!Array.isArray(samples)) return;
            this.samples = samples
                .filter(
                    (sample) =>
                        Array.isArray(sample) &&
                        sample.length === 4 &&
                        typeof sample[0] === 'string' &&
                        sample[0].length <= 50 &&
                        Number.isSafeInteger(sample[1]) &&
                        sample[1] >= 0 &&
                        Number.isFinite(sample[2]) &&
                        sample[2] > 0 &&
                        sample[2] <= 3600000 &&
                        Number.isSafeInteger(sample[3]) &&
                        sample[3] > 0 &&
                        sample[3] <= Date.now() + DAY,
                )
                .slice(-MAX_SAMPLES);
            this.saved = text;
        } catch {}
    }

    add(kind, bytes, milliseconds) {
        if (
            typeof kind !== 'string' ||
            kind.length > 50 ||
            !Number.isSafeInteger(bytes) ||
            bytes < 0
        )
            return;
        if (!Number.isFinite(milliseconds) || milliseconds <= 0 || milliseconds > 3600000) return;
        this.samples.push([kind, bytes, Math.max(1, Math.round(milliseconds)), Date.now()]);
        if (this.samples.length > MAX_SAMPLES) {
            const counts = new Map();
            for (const sample of this.samples)
                counts.set(sample[0], (counts.get(sample[0]) || 0) + 1);
            const oldest = this.samples.findIndex((sample) => counts.get(sample[0]) > 1);
            this.samples.splice(Math.max(0, oldest), 1);
        }
    }

    serialize() {
        return JSON.stringify(this.samples);
    }

    predict(kind, bytes) {
        const samples = this.samples.filter((sample) => sample[0] === kind);
        if (!samples.length) return null;
        const now = Date.now();
        const points = samples.map((sample) => ({
            x: sample[1] / 1000000,
            y: sample[2] / 1000,
            weight:
                Math.max(0.001, Math.exp(-Math.max(0, now - sample[3]) / (7 * DAY))) *
                (sample[3] >= this.started ? 4 : 1),
        }));
        const fit = (weights) => {
            const total = weights.reduce((sum, weight) => sum + weight, 0);
            const meanX = points.reduce((sum, point, i) => sum + point.x * weights[i], 0) / total;
            const meanY = points.reduce((sum, point, i) => sum + point.y * weights[i], 0) / total;
            const variance = points.reduce(
                (sum, point, i) => sum + weights[i] * (point.x - meanX) ** 2,
                0,
            );
            let slope =
                variance > total * 0.01
                    ? points.reduce(
                          (sum, point, i) =>
                              sum + weights[i] * (point.x - meanX) * (point.y - meanY),
                          0,
                      ) / variance
                    : meanX > 0
                      ? meanY / meanX
                      : 0;
            slope = Math.max(0, slope);
            let overhead = Math.max(0, meanY - slope * meanX);
            if (overhead === 0 && meanX > 0) slope = meanY / meanX;
            return (x) => Math.max(0.05, overhead + slope * x);
        };
        let weights = points.map((point) => point.weight);
        let model = fit(weights);
        // Reweight residuals so one unusually slow request cannot dominate the estimate.
        for (let pass = 0; pass < 3 && points.length >= 4; pass++) {
            const residuals = points.map((point) => point.y - model(point.x));
            const center = median(residuals);
            const scale = Math.max(
                0.25,
                1.4826 * median(residuals.map((value) => Math.abs(value - center))),
            );
            weights = points.map(
                (point, i) =>
                    point.weight *
                    Math.min(1, (1.5 * scale) / Math.max(0.001, Math.abs(residuals[i] - center))),
            );
            model = fit(weights);
        }
        const seconds = model(bytes / 1000000);
        const residual = median(points.map((point) => Math.abs(point.y - model(point.x))));
        const uncertainty = Math.max(residual * 1.4826, seconds * (points.length < 3 ? 0.5 : 0.2));
        return {
            seconds,
            low: Math.max(0.05, seconds - uncertainty),
            high: seconds + uncertainty,
            samples: points.length,
        };
    }
}

export function retryDelay(error, attempt, now = Date.now(), random = Math.random) {
    const value = error.retryAfter;
    const parsed =
        value && /^\d+(?:\.\d+)?$/.test(value.trim())
            ? Number(value) * 1000
            : Date.parse(value) - now;
    const backoff = Math.min(30000, 1000 * 2 ** (attempt - 1)) * (0.85 + random() * 0.3);
    return Math.max(backoff, Number.isFinite(parsed) ? parsed : 0);
}

export function canRetry(error, idempotent = false) {
    if (error.name === 'AbortError' || error.retryBlocked) return false;
    if (error.status === 429) return true;
    if (error.status === 409 && !error.retryAfter) return false;
    return (
        idempotent && (error.networkError || [408, 409, 500, 502, 503, 504].includes(error.status))
    );
}

export function waitForRetry(milliseconds, signal, onTick = () => {}) {
    return new Promise((resolve, reject) => {
        const deadline = performance.now() + milliseconds;
        let timer;
        const finish = () => {
            clearTimeout(timer);
            signal.removeEventListener('abort', abort);
        };
        const abort = () => {
            finish();
            reject(new DOMException('Cancelled', 'AbortError'));
        };
        const tick = () => {
            if (signal.aborted) return abort();
            const remaining = Math.max(0, deadline - performance.now());
            onTick(remaining);
            if (!remaining) {
                finish();
                resolve();
            } else timer = setTimeout(tick, Math.min(250, remaining));
        };
        signal.addEventListener('abort', abort, { once: true });
        tick();
    });
}

export function duration(seconds) {
    const rounded = Math.max(1, Math.ceil(seconds));
    if (rounded < 60) return rounded + 's';
    if (rounded < 3600) return Math.ceil(rounded / 60) + ' min';
    return Math.floor(rounded / 3600) + 'h ' + Math.ceil((rounded % 3600) / 60) + ' min';
}
