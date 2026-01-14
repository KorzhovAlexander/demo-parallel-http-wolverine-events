// repro_parallel.mjs
// Node 18+ (Node 20/22/24 ok)
// Run examples:
//   node repro_parallel.mjs
//   RUNS=100 BASE=http://localhost:5198 node repro_parallel.mjs
//   RUNS=200 PARALLEL=5 node repro_parallel.mjs

// ---------- config ----------
const BASE = process.env.BASE ?? "http://localhost:5198";
const RUNS = Number(process.env.RUNS ?? "1");
const PAUSE_MS = Number(process.env.PAUSE_MS ?? "50"); // small pause between runs
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? "15000");
const MARK_READY_ROUTE = "api/v1/mark-ready"; //or mark-ready -> mark-ready-controller

function nowIso() {
    return new Date().toISOString();
}

function newGuid() {
    // Node 18+ has crypto.randomUUID()
    return crypto.randomUUID();
}

function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
}

function withTimeout(promise, ms, label) {
    const ac = new AbortController();
    const t = setTimeout(() => ac.abort(), ms);
    return Promise.race([
        promise(ac.signal).finally(() => clearTimeout(t)),
        new Promise((_, reject) =>
            setTimeout(() => reject(new Error(`${label} timed out after ${ms}ms`)), ms + 50)
        ),
    ]);
}

async function postJson(path, body, label) {
    const url = `${BASE}${path}`;
    const startedAt = Date.now();

    const result = await withTimeout(
        async (signal) => {
            const res = await fetch(url, {
                method: "POST",
                headers: {
                    Accept: "application/json",
                    "Content-Type": "application/json",
                },
                body: JSON.stringify(body),
                signal,
            });

            const text = await res.text().catch(() => "");
            return {
                ok: res.ok,
                status: res.status,
                statusText: res.statusText,
                body: text,
            };
        },
        TIMEOUT_MS,
        label
    );

    const elapsedMs = Date.now() - startedAt;

    return {url, elapsedMs, ...result};
}

function shortBody(body, max = 500) {
    if (!body) return "";
    if (body.length <= max) return body;
    return body.slice(0, max) + ` ... (trimmed, len=${body.length})`;
}

async function runOnce(runIndex) {
    const streamId = newGuid();
    const payload = {id: streamId, itemName: "string"};

    console.log(`\n[${nowIso()}] RUN #${runIndex} streamId=${streamId}`);
    console.log(`[${nowIso()}] -> create-stream`);

    const create = await postJson("/api/v1/create-stream", payload, `create-stream#${runIndex}`);
    console.log(
        `[${nowIso()}] <- create-stream status=${create.status} elapsed=${create.elapsedMs}ms url=${create.url}`
    );

    if (create.status >= 500) {
        console.log(`[${nowIso()}] !! create-stream 5xx body:\n${shortBody(create.body)}\n`);
        return {streamId, failed: true, reason: "create-5xx", create, m1: null, m2: null};
    }

    // send 2 parallel mark-ready calls
    console.log(`[${nowIso()}] -> mark-ready (2 parallel requests)`);

    const [m1, m2] = await Promise.all([
        postJson("/" + MARK_READY_ROUTE, payload, `mark-ready#${runIndex}.1`).catch((e) => ({
            ok: false,
            status: 0,
            statusText: "ERR",
            body: String(e),
            elapsedMs: -1,
            url: `${BASE}/${MARK_READY_ROUTE}`,
        })),
        postJson("/" + MARK_READY_ROUTE, payload, `mark-ready#${runIndex}.2`).catch((e) => ({
            ok: false,
            status: 0,
            statusText: "ERR",
            body: String(e),
            elapsedMs: -1,
            url: `${BASE}/${MARK_READY_ROUTE}`,
        })),
    ]);

    console.log(
        `[${nowIso()}] <- mark-ready#1 status=${m1.status} elapsed=${m1.elapsedMs}ms`
    );
    if (m1.status === 500 || m1.status === 0) {
        console.log(`[${nowIso()}] !! mark-ready#1 body:\n${shortBody(m1.body)}\n`);
    }

    console.log(
        `[${nowIso()}] <- mark-ready#2 status=${m2.status} elapsed=${m2.elapsedMs}ms`
    );
    if (m2.status === 500 || m2.status === 0) {
        console.log(`[${nowIso()}] !! mark-ready#2 body:\n${shortBody(m2.body)}\n`);
    }

    const has500 = create.status === 500 || m1.status === 500 || m2.status === 500;
    const hasErr = m1.status === 0 || m2.status === 0;


    if (has500) {
        console.log(`[${nowIso()}] RESULT: FAIL (500 detected)`);
    } else if (hasErr) {
        console.log(`[${nowIso()}] RESULT: FAIL (client/network error)`);
    } else {
        console.log(`[${nowIso()}] RESULT: OK`);
    }

    return {
        streamId,
        failed: has500 || hasErr,
        reason: has500 ? "500" : hasErr ? "client-error" : "ok",
        create,
        m1,
        m2,
    };
}

async function main() {
    console.log(`[${nowIso()}] Starting repro...`);
    console.log(`BASE=${BASE}`);
    console.log(`RUNS=${RUNS}`);
    console.log(`TIMEOUT_MS=${TIMEOUT_MS}`);
    console.log(`PAUSE_MS=${PAUSE_MS}`);

    let fails = 0;
    let fails500 = 0;
    let failsClient = 0;

    for (let i = 1; i <= RUNS; i++) {
        const r = await runOnce(i);

        if (r.failed) {
            fails++;
            if (r.reason === "500") fails500++;
            if (r.reason === "client-error") failsClient++;
        }

        await sleep(PAUSE_MS);
    }

    console.log("\n---- SUMMARY ----");
    console.log(`runs=${RUNS}`);
    console.log(`fails=${fails}`);
    console.log(`fails_500=${fails500}`);
    console.log(`fails_client=${failsClient}`);
    console.log("exitCode =", fails > 0 ? 2 : 0);

    process.exit(fails > 0 ? 2 : 0);
}

main().catch((e) => {
    console.error(`[${nowIso()}] FATAL:`, e);
    process.exit(99);
});