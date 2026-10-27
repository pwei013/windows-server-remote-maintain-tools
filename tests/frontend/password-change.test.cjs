"use strict";
// Isolated DOM and response fixtures; this suite never changes an actual credential.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(path.resolve(__dirname, "../../src/IISManager.Console/wwwroot/auth-session.js"), "utf8");
const oldPassword = "synthetic-old-password", newPassword = "synthetic-new-password";

function fixture(reply = async () => ({ ok: true, status: 200, json: async () => ({ passwordChanged: true, signedOut: true }) })) {
  const ids = ["session-user", "session-user-name", "session-logout", "session-change-password", "password-dialog", "password-form", "password-old", "password-new", "password-confirm", "password-visible", "password-message", "password-close", "password-cancel", "password-relogin", "password-submit", "notice"];
  const elements = new Map(ids.map(id => [id, {
    id, value: "", type: id.startsWith("password-") ? "password" : "", disabled: false, hidden: true, checked: false, open: false, textContent: "", listeners: {}, attrs: {},
    addEventListener(event, fn) { this.listeners[event] = fn; },
    setAttribute(name, value) { this.attrs[name] = value; }, removeAttribute(name) { delete this.attrs[name]; }, focus() {},
    showModal() { this.open = true; }, close() { this.open = false; this.listeners.close?.({}); },
    querySelectorAll() { return ids.filter(key => ["password-old", "password-new", "password-confirm", "password-visible", "password-close", "password-cancel", "password-relogin", "password-submit"].includes(key)).map(key => elements.get(key)); }
  }]));
  const calls = [], redirects = [];
  const context = vm.createContext({ URL, document: {
    getElementById: id => elements.get(id), querySelectorAll: () => ids.filter(id => /submit|cancel|close|session-logout|session-change-password|relogin/.test(id)).map(id => elements.get(id))
  }, location: { origin: "http://synthetic.invalid", pathname: "/", search: "", hash: "", replace: value => redirects.push(value) },
    fetch: async (url, options) => { calls.push({ url, options }); return reply(url, options); }
  });
  vm.runInContext(source, context);
  context.IISManagerAuth.render({ csrfToken: "synthetic-csrf", authenticationRequired: true, user: { account: "synthetic-user" } });
  const dispatch = (id, event = "click", data = {}) => elements.get(id).listeners[event]({ preventDefault() {}, target: elements.get(id), ...data });
  const fill = (confirmation = newPassword) => {
    elements.get("password-old").value = oldPassword; elements.get("password-new").value = newPassword; elements.get("password-confirm").value = confirmation;
  };
  return { elements, calls, redirects, dispatch, fill };
}

(async () => {
  let count = 0;
  async function check(name, action) { await action(); count++; console.log("PASS " + name); }
  await check("all authentication pages request the current content version of the shared script", async () => {
    const version = require("node:crypto").createHash("sha256").update(source).digest("hex").slice(0, 16);
    for (const page of ["index.html", "operations.html", "login.html"]) {
      const html = fs.readFileSync(path.resolve(__dirname, "../../src/IISManager.Console/wwwroot", page), "utf8");
      assert.ok(html.includes(`src="/auth-session.js?v=${version}" defer`), `${page} must load its matching authentication script`);
    }
  });
  await check("opening and canceling clears password fields and preserves masked inputs", async () => {
    const f = fixture(); await f.dispatch("session-change-password"); f.fill(); await f.dispatch("password-cancel");
    for (const id of ["password-old", "password-new", "password-confirm"]) assert.equal(f.elements.get(id).value, "");
    assert.equal(f.elements.get("password-dialog").open, false);
  });
  await check("opening immediately shows the password dialog without any HTTP request", async () => {
    const f = fixture(); await f.dispatch("session-change-password");
    assert.equal(f.elements.get("password-dialog").open, true); assert.equal(f.calls.length, 0);
  });
  await check("unsupported dialog APIs report a visible error instead of an inert button", async () => {
    const f = fixture(); f.elements.get("password-dialog").showModal = undefined;
    await f.dispatch("session-change-password");
    assert.equal(f.elements.get("notice").hidden, false); assert.match(f.elements.get("notice").textContent, /配套更新/);
    assert.equal(f.calls.length, 0);
  });
  await check("dialog open failures report a visible error without sending credentials", async () => {
    const f = fixture(); f.elements.get("password-dialog").showModal = () => { throw new Error("SYNTHETIC_DIALOG_FAILURE"); };
    await f.dispatch("session-change-password");
    assert.equal(f.elements.get("notice").hidden, false); assert.match(f.elements.get("notice").textContent, /无法打开/);
    assert.equal(f.calls.length, 0);
  });
  await check("confirmation mismatch does not send a request", async () => {
    const f = fixture(); f.fill("synthetic-mismatched-password"); await f.dispatch("password-form", "submit");
    assert.equal(f.calls.length, 0); assert.match(f.elements.get("password-message").textContent, /不一致/);
  });
  await check("short new password is rejected before sending", async () => {
    const f = fixture(); f.fill(); f.elements.get("password-new").value = "short"; await f.dispatch("password-form", "submit");
    assert.equal(f.calls.length, 0); assert.match(f.elements.get("password-message").textContent, /12至128/);
  });
  await check("successful submission carries CSRF and redirects to the new-password login message", async () => {
    const f = fixture(); f.fill(); await f.dispatch("password-form", "submit");
    assert.equal(f.calls[0].url, "/api/v1/auth/change-password"); assert.equal(f.calls[0].options.headers["X-IISManager-CSRF"], "synthetic-csrf");
    assert.deepEqual(JSON.parse(f.calls[0].options.body), { oldPassword, newPassword, confirmPassword: newPassword });
    assert.match(f.redirects[0], /reason=password-changed/); assert.equal(f.elements.get("password-old").value, "");
  });
  await check("wrong old password stays in the dialog and clears all sensitive input", async () => {
    const f = fixture(async () => ({ ok: false, status: 400, json: async () => ({ code: "APP_USER_PASSWORD_REJECTED", message: "原密码不正确" }) }));
    f.fill(); await f.dispatch("password-form", "submit"); assert.equal(f.redirects.length, 0);
    assert.match(f.elements.get("password-message").textContent, /原密码/); assert.equal(f.elements.get("password-submit").disabled, false);
    for (const id of ["password-old", "password-new", "password-confirm"]) assert.equal(f.elements.get(id).value, "");
  });
  await check("expired authentication returns to login", async () => {
    const f = fixture(async () => ({ ok: false, status: 401, json: async () => ({ code: "APP_USER_SESSION_EXPIRED" }) }));
    f.fill(); await f.dispatch("password-form", "submit"); assert.match(f.redirects[0], /reason=expired/);
  });
  await check("unknown provider outcome uses a separate login message and sends only once", async () => {
    const f = fixture(async () => ({ ok: false, status: 503, json: async () => ({ code: "APP_USER_PASSWORD_OUTCOME_UNKNOWN" }) }));
    f.fill(); await f.dispatch("password-form", "submit"); assert.match(f.redirects[0], /reason=password-unconfirmed/); assert.equal(f.calls.length, 1);
  });
  await check("lost browser response blocks duplicate password submissions and offers login verification", async () => {
    const f = fixture(async () => { throw new Error("SYNTHETIC_NETWORK_LOSS"); });
    f.fill(); await f.dispatch("password-form", "submit"); f.fill(); await f.dispatch("password-form", "submit");
    assert.equal(f.calls.length, 1); assert.equal(f.elements.get("password-submit").disabled, true); assert.equal(f.elements.get("password-relogin").hidden, false);
  });
  await check("a pending request blocks duplicate clicks and dialog cancellation", async () => {
    let finish; const f = fixture(() => new Promise(resolve => { finish = resolve; }));
    await f.dispatch("session-change-password"); f.fill(); const first = f.dispatch("password-form", "submit");
    await f.dispatch("password-form", "submit"); await f.dispatch("password-cancel");
    assert.equal(f.calls.length, 1); assert.equal(f.elements.get("password-dialog").open, true);
    finish({ ok: true, status: 200, json: async () => ({ passwordChanged: true, signedOut: true }) }); await first;
  });
  console.log(`${count}/${count} password frontend checks passed`);
})().catch(error => { console.error("FAIL password frontend: " + error.name); process.exitCode = 1; });
