const {readFileSync} = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = readFileSync(__dirname + '/codex-meter-subscription.user.js', 'utf8');
const key = 'a'.repeat(64);
function harness({storedKey = key, hash = '', path = '/settings/billing', failTransport = false} = {}) {
    const sent = [], timers = [], storage = {meterConnectionKey: storedKey}, elements = [];
    let body;
    const location = {origin: 'https://chatgpt.com', pathname: path, search: '', hash};
    const original = {status: 200, clone: () => ({text: async () => JSON.stringify(body)})};
    class XHR {
        addEventListener(name, fn) { this[name] = fn; }
        open() {}
    }
    const page = {fetch: async () => original, XMLHttpRequest: XHR};
    const context = {URL, URLSearchParams, Date, WeakMap, unsafeWindow: page, location,
        history: {state: {}, replaceState(_, __, url) { location.hash = new URL(url, location.origin).hash; }},
        document: {body: {appendChild(node) { elements.push(node); }}, createElement() { return {style: {}, setAttribute() {}, addEventListener() {}, remove() {}, textContent: ''}; }},
        setInterval(fn) { timers.push(fn); },
        GM_getValue: (name, fallback) => storage[name] ?? fallback,
        GM_setValue: (name, value) => { storage[name] = value; },
        GM_xmlhttpRequest: request => { sent.push(request); if (failTransport) throw new Error('fixture denial'); request.onload({status: 200}); }
    };
    vm.runInNewContext(source, context);
    return {sent, storage, location, elements, page,
        async fetch(url, response) { body = response; const result = await page.fetch(url); await new Promise(resolve => setImmediate(resolve)); return result; },
        tick() { for (const fn of timers) fn(); }, original};
}
const fixture = {accounts: {'account-test': {account: {account_id: 'account-test', plan_type: 'plus', email: 'PRIVATE_EMAIL'},
    entitlement: {renews_at: '2026-11-02T11:01:48Z', scheduled_plan_change: {changes_at: '2026-11-02T11:01:48Z', plan_type: 'pro'},
        secret: 'PRIVATE_ENTITLEMENT'}, last_active_subscription: {will_renew: true, payment_method: 'PRIVATE_PAYMENT'},
    access_token: 'PRIVATE_TOKEN', billing_address: 'PRIVATE_ADDRESS'}}, unrelated: 'PRIVATE_EXTRA'};
(async () => {
    const a = harness({storedKey: '', hash: '#codex-meter-connect=' + key});
    assert.equal(a.storage.meterConnectionKey, key); assert.equal(a.location.hash, '');
    assert.equal(await a.fetch('https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27', fixture), a.original);
    assert.equal(a.sent.length, 1); assert.equal(a.sent[0].url, 'http://127.0.0.1:43129/subscription');
    assert.equal(a.sent[0].headers['X-Codex-Meter-Key'], key);
    const payload = JSON.parse(a.sent[0].data);
    assert.equal(payload.accounts['account-test'].entitlement.scheduled_plan_change.plan_type, 'pro');
    assert.ok(!a.sent[0].data.includes('PRIVATE')); assert.ok(!a.sent[0].data.includes(key));
    a.tick(); assert.equal(a.sent.length, 1);
    console.log('PASS initial pairing, private storage, fragment removal, response preservation, billing whitelist and deduplication');
    const b = harness({path: '/'});
    await b.fetch('https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27', fixture);
    assert.equal(b.sent.length, 0); b.location.pathname = '/settings/billing'; b.tick(); assert.equal(b.sent.length, 1);
    const c = harness();
    await c.fetch('https://chatgpt.com.evil.invalid/backend-api/accounts/check/v4-2023-04-27', fixture);
    await c.fetch('https://chatgpt.com/backend-api/conversation', fixture);
    await c.fetch('https://chatgpt.com:8443/backend-api/accounts/check/v4-2023-04-27', fixture);
    assert.equal(c.sent.length, 0);
    const d = harness({storedKey: ''}); await d.fetch('/backend-api/accounts/check/v4-2023-04-27', fixture); assert.equal(d.sent.length, 0);
    console.log('PASS billing-only navigation, exact HTTPS account endpoint, and unpaired silence');
    const e = harness();
    const xhr = new e.page.XMLHttpRequest(); xhr.open('GET', '/backend-api/accounts/check/v4-2023-04-27');
    xhr.status = 200; xhr.responseType = 'json'; xhr.response = fixture; xhr.load();
    assert.equal(e.sent.length, 1);
    xhr.open('GET', '/backend-api/conversation'); xhr.load(); assert.equal(e.sent.length, 1);
    console.log('PASS XHR response observation and reused request isolation');
    const f = harness({failTransport: true}); await f.fetch('/backend-api/accounts/check/v4-2023-04-27', fixture);
    assert.ok(f.elements.some(node => node.textContent.includes('권한')));
    await f.fetch('/backend-api/accounts/check/v4-2023-04-27', fixture); assert.equal(f.sent.length, 2);
    console.log('PASS synchronous bridge failure does not permanently lock delivery');
})().catch(error => { console.error(error); process.exitCode = 1; });
