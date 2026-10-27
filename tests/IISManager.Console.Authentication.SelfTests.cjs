"use strict";

const assert = require("node:assert/strict");
const { test } = require("node:test");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const source = name => readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot", name), "utf8");
const sessionSource = source("auth-session.js"), loginSource = source("auth-login.js");

async function browser({ session = {}, search = "", login = true, handler } = {}) {
  const nodes = new Map(), requests = [], redirects = [];
  for (const id of ["session-user", "session-user-name", "session-logout", "notice", "records-error",
    ...(login ? ["login-form", "login-account", "login-password", "login-submit", "login-message", "login-password-toggle", "login-password-eye"] : [])]) {
    const listeners = new Map();
    nodes.set(id, { id, value: "", textContent: "", hidden: false, disabled: id === "login-submit", type: id === "login-password" ? "password" : "text", attributes: {},
      addEventListener(name, callback) { listeners.set(name, callback); },
      async emit(name) { return listeners.get(name)?.({ preventDefault() {} }); },
      setAttribute(name, value) { this.attributes[name] = value; }, removeAttribute(name) { delete this.attributes[name]; },
      focus() { this.focused = true; } });
  }
  const location = { origin: "http://synthetic.example.test", pathname: login ? "/login.html" : "/operations.html", search, hash: "", replace(value) { redirects.push(value); } };
  const context = vm.createContext({ URL, URLSearchParams, location, Date,
    document: { getElementById: id => nodes.get(id), querySelectorAll: () => [nodes.get("session-logout"), nodes.get("login-submit")].filter(Boolean) },
    localStorage: { setItem() { throw new Error("TEST_SECRET_STORAGE_FORBIDDEN"); } },
    sessionStorage: { setItem() { throw new Error("TEST_SECRET_STORAGE_FORBIDDEN"); } },
    async fetch(path, options) {
      const request = { path, options }; requests.push(request);
      const result = path === "/api/v1/session" ? { csrfToken: "synthetic-csrf", authenticationRequired: true, authenticationConfigured: true, ...session } :
        await handler?.(request) || { user: { account: "synthetic-admin" }, expiresAtUtc: "2099-01-01T00:00:00Z" };
      return { status: result.status || 200, ok: !result.status || result.status < 400, json: async () => result.body || result };
    }
  });
  vm.runInContext(sessionSource, context); if (login) vm.runInContext(loginSource, context);
  await new Promise(resolve => setImmediate(resolve));
  return { nodes, requests, redirects, run: code => vm.runInContext(code, context) };
}
const submit = async harness => {
  harness.nodes.get("login-account").value = " synthetic-admin ";
  harness.nodes.get("login-password").value = "SYNTHETIC-only-password";
  await harness.nodes.get("login-form").emit("submit");
};

test("Login submits once with CSRF and returns to the requested protected page", async () => {
  const harness = await browser({ search: "?returnUrl=%2Foperations.html%3Fpage%3D2" }); await submit(harness);
  const request = harness.requests.find(item => item.path.endsWith("/login"));
  assert.deepEqual(JSON.parse(request.options.body), { account: "synthetic-admin", password: "SYNTHETIC-only-password" });
  assert.equal(request.options.headers["X-IISManager-CSRF"], "synthetic-csrf");
  assert.equal(request.options.credentials, "same-origin"); assert.deepEqual(harness.redirects, ["/operations.html?page=2"]);
  assert.equal(harness.nodes.get("login-password").value, "");
});
test("Return URL rejects external protocol-relative credential-bearing and unexpected paths", async () => {
  const harness = await browser();
  for (const value of ["https://other.example.test/", "//other.example.test/", "javascript:alert(1)", "/api/v1/servers", "/login.html", "http://user:password@synthetic.example.test/"])
    assert.equal(harness.run(`IISManagerAuth.safeReturnUrl(${JSON.stringify(value)})`), "/");
  assert.equal(harness.run('IISManagerAuth.safeReturnUrl("/?server=demo&operation=synthetic#task-section")'), "/?server=demo&operation=synthetic#task-section");
  assert.equal(harness.run('IISManagerAuth.safeReturnUrl("/overview.html#health-section")'), "/overview.html#health-section");
});
test("Password visibility stays accessible and errors erase the submitted secret", async () => {
  const harness = await browser({ handler: () => ({ status: 401, body: { code: "APP_USER_LOGIN_REJECTED", message: "账号或密码不正确。" } }) });
  await harness.nodes.get("login-password-toggle").emit("click");
  assert.equal(harness.nodes.get("login-password").type, "text"); assert.equal(harness.nodes.get("login-password-toggle").attributes["aria-label"], "隐藏密码");
  await submit(harness);
  assert.equal(harness.nodes.get("login-password").value, ""); assert.equal(harness.nodes.get("login-password").type, "password");
  assert.equal(harness.nodes.get("login-password-toggle").attributes["aria-pressed"], "false");
  assert.equal(harness.nodes.get("login-password").attributes["aria-invalid"], "true");
  assert.equal(harness.nodes.get("login-submit").disabled, false);
  assert.equal(harness.nodes.get("login-message").textContent, "账号或密码不正确。");
});
test("Concurrent clicks retain one login attempt and visible busy state", async () => {
  let finish;
  const harness = await browser({ handler: () => new Promise(resolve => { finish = resolve; }) });
  const pending = submit(harness); await harness.nodes.get("login-form").emit("submit");
  assert.equal(harness.requests.filter(item => item.path.endsWith("/login")).length, 1);
  assert.equal(harness.nodes.get("login-submit").textContent, "正在登录…"); assert.equal(harness.nodes.get("login-account").disabled, true);
  finish({ user: { account: "synthetic-admin" }, expiresAtUtc: "2099-01-01T00:00:00Z" }); await pending;
});
test("An unconfirmed login does not silently replay or retain the password", async () => {
  const harness = await browser({ handler: () => { throw new Error("合成网络中断"); } }); await submit(harness);
  assert.equal(harness.requests.filter(item => item.path.endsWith("/login")).length, 1);
  assert.equal(harness.nodes.get("login-password").value, ""); assert.equal(harness.redirects.length, 0);
  assert.equal(harness.nodes.get("login-submit").disabled, false);
});
test("HTTPS requirement stops login before sending a password", async () => {
  const harness = await browser({ session: { loginRequiresHttps: true } }); await submit(harness);
  assert.equal(harness.requests.length, 1); assert.equal(harness.nodes.get("login-submit").disabled, true);
  assert.match(harness.nodes.get("login-message").textContent, /HTTPS/);
});
test("Missing session bootstrap keeps login disabled", async () => {
  const harness = await browser({ session: { csrfToken: "" } }); await submit(harness);
  assert.equal(harness.requests.length, 1); assert.equal(harness.nodes.get("login-submit").disabled, true);
});
test("HTTP 429 is a visible non-replayed login failure", async () => {
  const harness = await browser({ handler: () => ({ status: 429, body: { message: "登录验证过于频繁，请稍后再试。" } }) }); await submit(harness);
  assert.match(harness.nodes.get("login-message").textContent, /过于频繁/); assert.equal(harness.requests.length, 2);
});
test("Session rejection redirects once and retains the operation reference", async () => {
  const harness = await browser({ login: false, search: "?operation=synthetic" });
  harness.run('IISManagerAuth.unauthorized({status:401}, {code:"APP_USER_SESSION_EXPIRED"}); IISManagerAuth.unauthorized({status:401}, {code:"APP_USER_SESSION_EXPIRED"});');
  assert.equal(harness.redirects.length, 1);
  assert.match(harness.redirects[0], /reason=expired/); assert.match(harness.redirects[0], /operation%3Dsynthetic/);
  assert.equal(harness.nodes.get("session-logout").disabled, true);
});
test("Agent failures do not masquerade as expired application sessions", async () => {
  const harness = await browser({ login: false }); harness.run('IISManagerAuth.unauthorized({status:401}, {code:"AGENT_CALL_REJECTED"})');
  assert.equal(harness.redirects.length, 0);
});
test("Current user is rendered with safe text and logout ends the local session", async () => {
  const harness = await browser({ login: false, handler: () => ({ signedOut: true, remoteConfirmed: true }) });
  harness.run('IISManagerAuth.render({csrfToken:"synthetic-csrf", authenticationRequired:true, user:{account:"synthetic-admin",displayName:"<script>synthetic</script>"}})');
  assert.equal(harness.nodes.get("session-user-name").textContent, "<script>synthetic</script>");
  assert.equal(harness.nodes.get("session-user").hidden, false); await harness.nodes.get("session-logout").emit("click");
  assert.equal(harness.requests[0].options.headers["X-IISManager-CSRF"], "synthetic-csrf");
  assert.deepEqual(harness.redirects, ["/login.html?reason=signed-out&returnUrl=%2Foperations.html"]);
});
test("Unconfirmed upstream logout reports that local logout completed", async () => {
  const harness = await browser({ login: false, handler: () => ({ signedOut: true, remoteConfirmed: false }) });
  harness.run('IISManagerAuth.render({csrfToken:"synthetic-csrf", authenticationRequired:true,user:{account:"synthetic-admin"}})');
  await harness.nodes.get("session-logout").emit("click"); assert.deepEqual(harness.redirects, ["/login.html?reason=logout-unconfirmed&returnUrl=%2Foperations.html"]);
});
