// ==UserScript==
// @name         Codex 미터기 · ChatGPT 구독 연결
// @namespace    https://github.com/6Zman8/codex-usage-meter
// @version      1.3.0
// @description  평소 쓰는 Chrome에서 구독 페이지를 열면 Codex 미터기에 구독 날짜를 전달합니다.
// @match        https://chatgpt.com/*
// @run-at       document-start
// @grant        unsafeWindow
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// @updateURL    https://raw.githubusercontent.com/6Zman8/codex-usage-meter/main/browser/codex-meter-subscription.user.js
// @downloadURL  https://raw.githubusercontent.com/6Zman8/codex-usage-meter/main/browser/codex-meter-subscription.user.js
// ==/UserScript==

(() => {
    'use strict';
    const endpoint = '/backend-api/accounts/check/v4-2023-04-27';
    const bridge = 'http://127.0.0.1:43129/subscription';
    const page = unsafeWindow;
    let latest = null, sending = false, sent = '', retryAt = 0, notice = null;
    let key = GM_getValue('meterConnectionKey', '');
    const hash = new URLSearchParams(location.hash.slice(1));
    const incomingKey = hash.get('codex-meter-connect');
    if (incomingKey && /^[a-f0-9]{64}$/.test(incomingKey)) {
        key = incomingKey;
        GM_setValue('meterConnectionKey', key);
        hash.delete('codex-meter-connect');
        history.replaceState(history.state, '', location.pathname + location.search + (hash.size ? '#' + hash.toString() : ''));
    }

    function billingPage() { return location.pathname === '/settings/billing' || location.pathname.startsWith('/settings/billing/'); }
    function show(text) {
        if (!billingPage() || !document.body) return;
        if (!notice) {
            notice = document.createElement('div');
            notice.setAttribute('role', 'status');
            Object.assign(notice.style, { position: 'fixed', right: '20px', bottom: '20px', zIndex: '2147483647',
                padding: '12px 16px', maxWidth: '360px', borderRadius: '12px', background: '#202026', color: '#f4f4f5',
                border: '1px solid #57515f', font: '13px/1.6 system-ui', boxShadow: '0 4px 20px #0005' });
            notice.title = '클릭하면 안내를 닫습니다.';
            notice.addEventListener('click', () => { notice.remove(); notice = null; });
            document.body.appendChild(notice);
        }
        notice.textContent = text;
    }
    function accountResponse(url) {
        try { const parsed = new URL(String(url), location.origin); return parsed.origin === 'https://chatgpt.com' && parsed.pathname === endpoint; }
        catch { return false; }
    }
    function safeString(value, maximum = 80) { return typeof value === 'string' && value.length <= maximum ? value : null; }
    function compact(raw) {
        if (!raw || !raw.accounts || typeof raw.accounts !== 'object' || Array.isArray(raw.accounts)) return null;
        const accounts = Object.create(null);
        for (const [id, details] of Object.entries(raw.accounts).slice(0, 32)) {
            if (!details || !details.account || details.account.account_id !== id || !safeString(id, 128)) continue;
            const account = details.account, entitlement = details.entitlement || {}, subscription = details.last_active_subscription || {};
            const change = entitlement.scheduled_plan_change || {};
            accounts[id] = {
                account: { account_id: id, plan_type: safeString(account.plan_type) },
                entitlement: {
                    is_delinquent: typeof entitlement.is_delinquent === 'boolean' ? entitlement.is_delinquent : null,
                    renews_at: safeString(entitlement.renews_at), cancels_at: safeString(entitlement.cancels_at), expires_at: safeString(entitlement.expires_at),
                    scheduled_plan_change: { changes_at: safeString(change.changes_at), plan_type: safeString(change.plan_type) }
                },
                last_active_subscription: { will_renew: typeof subscription.will_renew === 'boolean' ? subscription.will_renew : null,
                    active_until: safeString(subscription.active_until) }
            };
        }
        return Object.keys(accounts).length ? { version: 1, observedAt: new Date().toISOString(), accounts } : null;
    }
    function observed(raw) {
        try { const value = compact(raw); if (value) { latest = value; sent = ''; retryAt = 0; flush(); } } catch { /* Do not affect ChatGPT. */ }
    }
    function flush() {
        if (!billingPage() || !latest || !/^[a-f0-9]{64}$/.test(key) || sending || Date.now() < retryAt) return;
        if (Date.now() - Date.parse(latest.observedAt) > 9 * 60 * 1000) {
            show('Codex 미터기: 최신 구독 정보를 받으려면 이 페이지를 새로고침해 주세요.'); return;
        }
        const payload = JSON.stringify(latest);
        if (payload === sent || payload.length > 65536) return;
        sending = true;
        const done = () => { sending = false; retryAt = Date.now() + 30000; };
        try { GM_xmlhttpRequest({ method: 'POST', url: bridge, timeout: 5000, anonymous: true,
            headers: { 'Content-Type': 'application/json', 'X-Codex-Meter-Key': key }, data: payload,
            onload: response => {
                done();
                if (response.status === 200) { sent = payload; show('Codex 미터기: 구독 정보를 반영했습니다.'); }
                else if (response.status === 403) show('Codex 미터기: 미터기의 구독 날짜를 눌러 크롬을 다시 연결해 주세요.');
                else if (response.status === 409) show('Codex 미터기: 웹 계정과 미터기의 연결 계정·플랜이 일치하는지 확인해 주세요.');
                else show('Codex 미터기: 연결을 확인하지 못했습니다. 잠시 후 다시 시도합니다.');
            },
            onerror: () => { done(); show('Codex 미터기를 실행해 주세요. 실행 후 자동으로 다시 연결합니다.'); },
            ontimeout: () => { done(); show('Codex 미터기 연결이 지연되고 있습니다. 잠시 후 다시 시도합니다.'); }
        }); } catch { done(); show('Codex 미터기: 탬퍼몽키의 로컬 연결 권한을 확인해 주세요.'); }
    }

    // Observe only the response body of the normal ChatGPT account request.
    // Never inspect request headers, cookies, passwords, chats or billing addresses.
    const originalFetch = page.fetch;
    page.fetch = function (...args) {
        const result = originalFetch.apply(this, args);
        const requestUrl = typeof args[0] === 'string' || args[0] instanceof URL ? args[0] : args[0] && args[0].url;
        if (accountResponse(requestUrl)) result.then(response => {
            if (response.status === 200) response.clone().text().then(text => {
                if (text.length <= 524288) { try { observed(JSON.parse(text)); } catch { /* Non-JSON response. */ } }
            }).catch(() => {});
        }).catch(() => {});
        return result;
    };
    const originalOpen = page.XMLHttpRequest.prototype.open;
    const requests = new WeakMap();
    page.XMLHttpRequest.prototype.open = function (method, url, ...rest) {
        if (!requests.has(this)) this.addEventListener('load', () => {
            if (!requests.get(this) || this.status !== 200) return;
            try {
                if (this.responseType === 'json') observed(this.response);
                else if ((!this.responseType || this.responseType === 'text') && this.responseText.length <= 524288) observed(JSON.parse(this.responseText));
            } catch { /* An unsupported response type is unrelated to the meter. */ }
        });
        requests.set(this, accountResponse(url));
        return originalOpen.call(this, method, url, ...rest);
    };
    setInterval(() => {
        if (!billingPage()) { if (notice) { notice.remove(); notice = null; } return; }
        if (!key) show('Codex 미터기에서 구독 날짜 → 크롬에서 확인을 한 번 눌러 연결해 주세요.');
        else if (!latest) show('Codex 미터기: 구독 정보를 기다리고 있습니다. 계속 표시되면 이 페이지를 새로고침해 주세요.');
        flush();
    }, 1500);
})();
