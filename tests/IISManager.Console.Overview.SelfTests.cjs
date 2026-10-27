"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const { createHash } = require("node:crypto");
const scriptPath = join(__dirname, "../src/IISManager.Console/wwwroot/overview.js");
const source = readFileSync(scriptPath, "utf8");
const pure = require(scriptPath);
const tick = () => new Promise(resolve => setImmediate(resolve));
const healthCheck = (overrides = {}) => ({ id: "a9e53562-b781-4968-ab2b-5109249abf8d", revision: "r1", serverId: "a", siteName: "Orders", applicationPath: "/api", url: "https://orders.example.com/api/health", expectedStatusCode: 200, timeoutSeconds: 5, intervalSeconds: 60, enabled: true, result: null, nextCheckAt: null, lastSuccessAt: null, ...overrides });
const server = (overrides = {}) => ({ id: "a", name: "主服务器", hostName: "server-a", mode: "Windows", connectionState: "online", collectedAt: "2026-10-25T08:00:00Z", info: { machineName: "SERVER-A", agentVersion: "1.0" },
  snapshot: { sites: [{ name: "Orders", state: "Started", bindings: [{ protocol: "https", ipAddress: "*", port: 443, hostName: "orders.example.com" }] }], applicationPools: [{ name: "OrdersPool", state: "Stopped" }] },
  topology: { sites: [{ name: "Orders", applications: [{ path: "/", physicalPath: "C:\\Sites\\Orders" }, { path: "/api", physicalPath: "C:\\Sites\\OrdersApi" }] }] },
  services: { services: [{ managed: true, state: "Running" }, { managed: false, state: "Stopped" }] }, certificates: [], errors: [], ...overrides });
const overview = (overrides = {}) => ({ collectedAt: "2026-10-25T08:00:00Z", servers: [server()], healthChecks: [], healthError: null, ...overrides });

async function browser(data = overview(), options = {}) {
  const nodes = new Map(), all = [], requests = [], timers = new Map(), listeners = new Map();
  const storage = options.storage || new Map();
  let timerId = 0;
  function element(tag = "div") {
    const events = new Map();
    const result = { tagName: tag, value: "", checked: false, textContent: "", hidden: false, disabled: false, open: false, children: [], dataset: {}, attributes: {},
      get options() { return this.children.filter(item => item.tagName === "option"); },
      append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; },
      setAttribute(name, value) { this.attributes[name] = value; }, addEventListener(name, fn) { events.set(name, fn); },
      async emit(name, extra = {}) { await events.get(name)?.({ preventDefault() {}, target: this, ...extra }); await tick(); },
      showModal() { this.open = true; }, close() { this.open = false; }, focus() {}, querySelectorAll() { return []; }
    };
    all.push(result); return result;
  }
  const document = { visibilityState: "visible", createElement: element,
    getElementById(id) { if (!nodes.has(id)) { const item = element(); item.id = id; nodes.set(id, item); } return nodes.get(id); },
    querySelectorAll(selector) { return selector === "[data-health-action]" ? all.filter(item => item.dataset.healthAction) : selector === "[data-server-read]" ? all.filter(item => item.dataset.serverRead) : []; },
    addEventListener(name, fn) { listeners.set(name, fn); }
  };
  const html = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/overview.html"), "utf8");
  for (const id of ["server-state", "health-filter", "certificate-filter"]) {
    const markup = html.match(new RegExp(`<select[^>]*id="${id}"[^>]*>([\\s\\S]*?)<\\/select>`))?.[1] || "";
    for (const match of markup.matchAll(/<option value="([^"]*)"[^>]*>([^<]*)<\/option>/g)) {
      const option = element("option"); option.value = match[1]; option.textContent = match[2]; document.getElementById(id).append(option);
    }
  }
  document.getElementById("certificate-filter").value = "alerts";
  const harness = { requests, timers, data, custom: options.custom || null, node: id => document.getElementById(id), all, document, storage,
    session: options.session || { csrfToken: "test-csrf", authenticationRequired: false },
    permissions: Object.hasOwn(options, "permissions") ? options.permissions : { administration: true,
      servers: data.servers.map(item => ({ serverId: item.id, read: true, operate: true, configure: true })) } };
  const context = vm.createContext({ document, URL, URLSearchParams, Date,
    setTimeout(fn, ms) { const id = ++timerId; timers.set(id, { fn, ms }); return id; }, clearTimeout(id) { timers.delete(id); },
    addEventListener() {}, IISManagerAuth: { unauthorized() {}, render() {} },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    async fetch(path, options) {
      requests.push({ path, ...options });
      if (harness.custom) { const result = await harness.custom(path, options); if (result !== undefined) return result; }
      const value = path === "/api/v1/session" ? harness.session : path === "/api/v1/permissions" ? harness.permissions : harness.data;
      return { ok: true, status: 200, json: async () => value };
    }
  });
  harness.run = code => vm.runInContext(code, context);
  harness.fireTimer = async milliseconds => {
    const entry = [...timers.entries()].find(([, timer]) => timer.ms === milliseconds);
    assert.ok(entry, `Expected a ${milliseconds} ms display timer`);
    timers.delete(entry[0]); await entry[1].fn(); await tick();
  };
  harness.run(source); await tick(); await tick(); harness.initialRequests = [...requests]; requests.length = 0; return harness;
}

const descendantText = node => [node.textContent, ...node.children.map(descendantText)].join(" ");
const childWithClass = (node, className) => node.children.find(item => item.className === className);
const cardActions = card => childWithClass(card, "server-card-actions").children;
const pendingServer = (overrides = {}) => server({ connectionState: "pending", refreshing: true, collectedAt: null,
  info: null, snapshot: null, topology: null, services: null, certificates: [], errors: [], ...overrides });

test("Global search matches servers, bound domains, child applications and physical paths", () => {
  const item = server();
  for (const query of ["主服务器", "SERVER-A", "orders.example.com", "/API", "OrdersApi"]) assert.equal(pure.serverMatches(item, query), true);
  assert.equal(pure.serverMatches(item, "missing"), false);
  const url = new URL(pure.workbenchUrl("a&b", "订单 & 生产", "/api/v2"), "http://localhost");
  assert.equal(url.pathname, "/"); assert.equal(url.searchParams.get("server"), "a&b"); assert.equal(url.searchParams.get("site"), "订单 & 生产"); assert.equal(url.searchParams.get("search"), "/api/v2"); assert.equal(url.searchParams.get("resource"), "sites");
});

test("Unavailable resource counts remain unknown and transient states are not counted as stopped", () => {
  assert.equal(pure.metrics(null, "Started"), null);
  assert.deepEqual(pure.metrics([{ state: "Started" }, { state: "Stopped" }, { state: "Unknown" }, { state: "Starting" }], "Started"), { total: 4, running: 1, stopped: 1, other: 2 });
});

test("Project and environment facets combine with status and preserve omitted legacy fields", () => {
  const items = [server({ group: "车队", environment: "生产" }), server({ id: "b", group: "车队", environment: "测试", connectionState: "offline" }), server({ id: "c", group: "empty", environment: "生产" }), server({ id: "legacy" })];
  const options = pure.facetOptions(items, "group"); assert.deepEqual(options[0], ["", "全部项目"]);
  assert.deepEqual(options.slice(1).sort((a, b) => a[0].localeCompare(b[0])), [["empty", "未分组"], ["value:车队", "车队"], ["value:empty", "empty"]].sort((a, b) => a[0].localeCompare(b[0])));
  assert.equal(items.filter(item => pure.serverFilterMatches(item, { group: "value:车队", environment: "value:生产", state: "online" })).length, 1);
  assert.equal(items.filter(item => pure.serverFilterMatches(item, { group: "empty", environment: "empty" }))[0].id, "legacy");
  assert.equal(pure.serverFilterMatches(items[3]), true); assert.equal(pure.serverMatches(items[0], "车队"), true); assert.equal(pure.serverMatches(items[0], "生产"), true);
  assert.equal(pure.validOverview(overview({ servers: [items[3]] })), true);
});

test("Dynamic project and environment selections combine and reset without changing legacy server visibility", async () => {
  const harness = await browser(overview({ servers: [server({ group: "车队", environment: "生产" }), server({ id: "b", group: "车队", environment: "测试", connectionState: "offline" }), server({ id: "legacy", name: "旧服务器" })] }));
  assert.equal(harness.node("overview-servers").children.length, 3);
  harness.node("server-group").value = "value:车队"; await harness.node("server-group").emit("change");
  harness.node("server-environment").value = "value:生产"; await harness.node("server-environment").emit("change");
  harness.node("server-state").value = "online"; await harness.node("server-state").emit("change");
  assert.equal(harness.node("overview-servers").children.length, 1); assert.equal(harness.node("servers-count").textContent, "1 / 3 台服务器");
  await harness.node("overview-reset").emit("click"); assert.equal(harness.node("overview-servers").children.length, 3);
  harness.node("server-group").value = "empty"; harness.node("server-environment").value = "empty"; await harness.node("server-group").emit("change");
  const cards = harness.node("overview-servers").children; assert.equal(cards.length, 1);
  assert.deepEqual(cards[0].children.find(item => item.className === "server-classifications").children.map(item => item.textContent), ["未分组", "未设置"]);
});

test("Certificate warnings sort missing and expired first and preserve null validity dates", () => {
  const data = overview({ servers: [server({ certificates: [
    { thumbprint: "normal", severity: "normal", daysRemaining: 80, bindings: [] },
    { thumbprint: "notice", severity: "notice", daysRemaining: 25, bindings: [{ siteName: "Orders", hostName: "orders.example.com" }] },
    { thumbprint: "expired", severity: "expired", daysRemaining: -2, bindings: [] },
    { thumbprint: "missing", severity: "missing", daysRemaining: null, notAfter: null, bindings: [] }
  ] })] });
  assert.deepEqual(pure.certificates(data).map(row => row.thumbprint), ["missing", "expired", "notice"]);
  assert.deepEqual(pure.certificates(data, "ORDERS.EXAMPLE.COM").map(row => row.thumbprint), ["notice"]);
  assert.equal(pure.certificates(data, "", "").length, 4);
});

test("Only enabled failed checks count as business health issues; simulation is separate", () => {
  assert.equal(pure.healthIssue(healthCheck({ result: { state: "unhealthy" } })), true);
  assert.equal(pure.healthIssue(healthCheck({ enabled: false, result: { state: "unhealthy" } })), false);
  assert.equal(pure.healthIssue(healthCheck({ result: { state: "simulation" } })), false);
  assert.equal(pure.healthIssue(healthCheck()), false);
});

test("Health editor rejects credential, query, fragment, invalid schemes and invalid numeric ranges", () => {
  const item = healthCheck(); assert.equal(pure.validateForm(item), "");
  for (const url of ["https://name:secret@orders.example.com/api/health", "https://orders.example.com/api/health?token=value", "https://orders.example.com/api/health#fragment", "file:///health", "http://orders.example.com/a\\b", "http://orders.example.com/a b"]) assert.notEqual(pure.validateForm({ ...item, url }), "");
  for (const change of [{ timeoutSeconds: 0 }, { timeoutSeconds: 16 }, { intervalSeconds: 29 }, { intervalSeconds: 3601 }, { expectedStatusCode: 302 }, { expectedStatusCode: 200.5 }]) assert.notEqual(pure.validateForm({ ...item, ...change }), "");
});

test("Overview loads through read-only routes and health storage errors do not conceal server data", async () => {
  const harness = await browser(overview({ healthError: { code: "HEALTH_STORE_UNAVAILABLE", message: "safe message" } }));
  assert.equal(harness.node("overview-servers").children.length, 1);
  assert.equal(harness.node("health-unavailable").hidden, false); assert.equal(harness.node("health-empty").hidden, true);
  assert.equal(harness.node("health-add").disabled, true);
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 0);
});

test("New checks remain disabled until explicitly enabled and save with CSRF and no operational mutation", async () => {
  const harness = await browser(); await harness.node("health-add").emit("click");
  assert.equal(harness.node("health-enabled").checked, false);
  assert.equal(harness.node("health-dialog").open, true);
  harness.node("health-url").value = "https://orders.example.com/health";
  harness.custom = (path, options) => {
    if (path !== "/api/v1/health-checks/save") return;
    const body = JSON.parse(options.body); assert.equal(body.enabled, false); assert.equal(body.id, null); assert.equal(body.revision, null);
    assert.equal(options.headers["X-IISManager-CSRF"], "test-csrf");
    return { ok: true, status: 200, json: async () => healthCheck({ ...body, id: "new", revision: "r-new" }) };
  };
  await harness.node("health-form").emit("submit");
  assert.equal(harness.node("health-dialog").open, false);
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
  assert.equal(harness.requests.some(request => request.path.includes("/operations")), false);
});

test("Disabled checks cannot run manually and no false healthy badge is shown for simulation", async () => {
  const harness = await browser(overview({ healthChecks: [healthCheck({ enabled: false }), healthCheck({ id: "simulation", result: { state: "simulation", checkedAt: "2026-10-25T08:00:00Z" } })] }));
  const rows = harness.node("health-body").children, run = rows[0].children[4].children[0];
  assert.equal(run.disabled, true); assert.match(run.title, /启用/); await run.emit("click"); assert.equal(harness.requests.length, 0);
  assert.equal(rows[1].children[2].children[0].textContent, "模拟检查");
});

test("Concurrent refresh clicks share one request and a refresh failure labels retained data as old", async () => {
  const harness = await browser(); let release;
  harness.custom = path => path === "/api/v1/overview/progress" ? new Promise(resolve => { release = resolve; }) : undefined;
  const first = harness.node("overview-refresh").emit("click"), second = harness.node("overview-refresh").emit("click");
  await tick(); assert.equal(harness.requests.length, 1);
  release({ ok: false, status: 503, json: async () => ({ code: "UNAVAILABLE", message: "temporary" }) }); await Promise.all([first, second]);
  assert.equal(harness.node("overview-servers").children.length, 1); assert.match(harness.node("overview-updated").textContent, /上次成功/);
});

test("An unconfirmed save preserves the draft and blocks repeated mutations until a fresh read", async () => {
  const harness = await browser(); await harness.node("health-add").emit("click"); harness.node("health-url").value = "https://orders.example.com/health";
  harness.custom = path => { if (path === "/api/v1/health-checks/save") throw new Error("network failure"); };
  await harness.node("health-form").emit("submit");
  assert.equal(harness.node("health-dialog").open, true); assert.equal(harness.node("health-url").value, "https://orders.example.com/health");
  assert.equal(harness.node("health-save").disabled, true); assert.match(harness.node("health-form-error").textContent, /勿重复提交/);
  await harness.node("health-form").emit("submit"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
  await harness.node("health-cancel").emit("click"); harness.custom = null; await harness.node("overview-refresh").emit("click");
  assert.equal(harness.node("health-add").disabled, false);
});

test("Delete requires the explicit dialog and submits the original revision once", async () => {
  const item = healthCheck(), harness = await browser(overview({ healthChecks: [item] }));
  const row = harness.node("health-body").children[0]; await row.children[4].children[3].emit("click");
  assert.equal(harness.node("health-delete-dialog").open, true); assert.equal(harness.requests.length, 0);
  harness.custom = (path, options) => {
    if (!path.endsWith("/delete")) return;
    assert.deepEqual(JSON.parse(options.body), { revision: "r1" });
    return { ok: true, status: 200, json: async () => ({ deleted: true }) };
  };
  await harness.node("health-delete-confirm").emit("click"); assert.equal(harness.node("health-body").children.length, 0); assert.equal(harness.node("health-delete-dialog").open, false);
});

test("An unverifiable persisted write blocks retries and certificate expiry under one day is explicit", async () => {
  const harness = await browser(overview({ servers: [server({ certificates: [{ thumbprint: "expired", subject: "Orders", notAfter: "2026-10-25T07:00:00Z", hasPrivateKey: true, severity: "expired", daysRemaining: 0, bindings: [] }] })] }));
  assert.equal(harness.node("certificates-body").children[0].children[2].children[1].textContent, "已过期不足 1 天");
  await harness.node("health-add").emit("click"); harness.node("health-url").value = "https://orders.example.com/health";
  harness.custom = path => path === "/api/v1/health-checks/save" ? { ok: false, status: 503, json: async () => ({ code: "HEALTH_STORE_UNVERIFIED", message: "请勿重复提交" }) } : undefined;
  await harness.node("health-form").emit("submit"); assert.equal(harness.node("health-save").disabled, true);
  await harness.node("health-form").emit("submit"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
});

test("Automatic display refresh never runs a health probe and pauses while the editor is open", async () => {
  const harness = await browser(); assert.equal(harness.timers.size, 0);
  harness.node("overview-auto").checked = true; await harness.node("overview-auto").emit("change");
  assert.equal(harness.timers.size, 1); let timer = [...harness.timers.values()][0]; assert.equal(timer.ms, 30000);
  await timer.fn(); assert.equal(harness.requests.at(-1).path, "/api/v1/overview/progress"); assert.equal(harness.requests.some(request => request.method === "POST"), false);
  await harness.node("health-add").emit("click"); harness.requests.length = 0; timer = [...harness.timers.values()].at(-1); await timer.fn(); assert.equal(harness.requests.length, 0);
});

test("Total failure is not presented as an empty healthy fleet and incomplete response is rejected", () => {
  assert.equal(pure.validOverview({ servers: [], healthChecks: [] }), true);
  assert.equal(pure.validOverview({ servers: [{ id: "a", name: "a", connectionState: "online" }], healthChecks: [] }), false);
  assert.equal(pure.validOverview({ servers: [], healthChecks: [null] }), false);
});

test("Pending progressive nodes retain unknown resource counts and are not called offline or stopped", async () => {
  const harness = await browser(overview({ servers: [pendingServer()], isRefreshing: true, completedServers: 0, totalServers: 1 }));
  const card = harness.node("overview-servers").children[0];
  assert.equal(childWithClass(card, "server-card-top").children[1].textContent, "等待采集");
  const metrics = childWithClass(card, "server-metrics").children;
  assert.deepEqual(metrics.map(item => item.children[1].textContent), ["—", "—", "—"]);
  assert.ok(metrics.every(item => item.children[2].textContent === "暂未取得数据"));
  assert.doesNotMatch(descendantText(card), /0 \/ 0|停止 0|连接正常|无法连接/);
  const attention = harness.node("overview-summary").children[2];
  assert.equal(attention.children[1].textContent, "0"); assert.match(attention.children[2].textContent, /正在采集/);
  assert.match(harness.node("overview-updated").textContent, /0 \/ 1/);
});

test("Progressive collection renders a fast node while a slow node remains pending, then merges display progress", async () => {
  const slow = pendingServer({ id: "b", name: "慢服务器", hostName: "server-b" });
  const harness = await browser(overview({ servers: [server(), slow], isRefreshing: true, completedServers: 1, totalServers: 2 }));
  let cards = harness.node("overview-servers").children;
  assert.equal(cards.length, 2);
  assert.equal(childWithClass(cards[0], "server-metrics").children[0].children[1].textContent, "1 / 1");
  assert.equal(childWithClass(cards[1], "server-metrics").children[0].children[1].textContent, "—");
  assert.match(harness.node("overview-updated").textContent, /1 \/ 2/);
  harness.data = overview({ servers: [server(), server({ id: "b", name: "慢服务器", hostName: "server-b" })], isRefreshing: false, completedServers: 2, totalServers: 2 });
  await harness.fireTimer(1000);
  cards = harness.node("overview-servers").children;
  assert.equal(childWithClass(cards[1], "server-metrics").children[0].children[1].textContent, "1 / 1");
  assert.equal(harness.requests.length, 1); assert.equal(harness.requests[0].path, "/api/v1/overview/progress");
  assert.ok(harness.requests.every(request => !request.method || request.method === "GET"));
});

test("Retry one node makes one GET and leaves the other node unchanged even after repeated clicks", async () => {
  const harness = await browser(overview({ servers: [server(), server({ id: "b", name: "保持原结果" })] }));
  const retry = cardActions(harness.node("overview-servers").children[0]).find(item => item.textContent === "重试本台");
  let release;
  harness.custom = path => path === "/api/v1/servers/a/overview/refresh" ? new Promise(resolve => { release = resolve; }) : undefined;
  const first = retry.emit("click"), duplicate = retry.emit("click"); await tick();
  assert.equal(harness.requests.length, 1); assert.equal(harness.requests[0].path, "/api/v1/servers/a/overview/refresh");
  assert.equal(harness.requests[0].method || "GET", "GET"); assert.equal(retry.disabled, true);
  release({ ok: true, status: 200, json: async () => server({ name: "本台新结果" }) }); await Promise.all([first, duplicate]);
  const cards = harness.node("overview-servers").children;
  assert.match(descendantText(cards[0]), /本台新结果/); assert.match(descendantText(cards[1]), /保持原结果/);
  assert.equal(harness.requests.some(request => request.path.includes("/health-checks") || request.path === "/api/v1/overview/progress"), false);
});

test("Connection diagnostics explain DNS TLS and authorization failures without exposing remote error bodies", async () => {
  const item = server({ connectionState: "partial", elapsedMilliseconds: 1200, errors: [
    { module: "info", code: "AGENT_DNS_FAILED", message: "SYNTHETIC_SECRET_DNS_BODY" },
    { module: "certificates", code: "AGENT_TLS_FAILED", message: "SYNTHETIC_SECRET_TLS_BODY" },
    { module: "windows-services", code: "AGENT_ACCESS_DENIED", message: "SYNTHETIC_SECRET_AUTH_BODY" }
  ] });
  const harness = await browser(overview({ servers: [item] }));
  const open = cardActions(harness.node("overview-servers").children[0]).find(control => control.textContent === "连接诊断"); await open.emit("click");
  assert.equal(harness.node("server-diagnostics-dialog").open, true); assert.equal(harness.requests.length, 0);
  const details = descendantText(harness.node("server-diagnostics-details")), errors = descendantText(harness.node("server-diagnostics-errors"));
  assert.match(details, /server-a/); assert.match(details, /1200 ms/); assert.match(details, /1\.0/);
  assert.match(errors, /域名解析失败/); assert.match(errors, /TLS 握手或证书验证失败/); assert.match(errors, /认证或来源限制拒绝/);
  assert.doesNotMatch(errors, /SYNTHETIC_SECRET/);
  assert.equal(new URL(harness.node("server-diagnostics-resources").href, "https://console.example.test").searchParams.get("server"), "a");
  await harness.node("server-diagnostics-close").emit("click"); assert.equal(harness.node("server-diagnostics-dialog").open, false);
});

test("Progress polling cannot clear an unconfirmed health mutation or permit another write", async () => {
  const harness = await browser(overview({ isRefreshing: true, completedServers: 1, totalServers: 2 }));
  await harness.node("health-add").emit("click"); harness.node("health-url").value = "https://orders.example.com/health";
  harness.custom = path => { if (path === "/api/v1/health-checks/save") throw new Error("synthetic transport failure"); };
  await harness.node("health-form").emit("submit"); assert.equal(harness.node("health-save").disabled, true);
  await harness.node("health-cancel").emit("click"); harness.custom = null;
  harness.data = overview({ healthChecks: [healthCheck({ enabled: false })], isRefreshing: false, completedServers: 2, totalServers: 2 });
  await harness.fireTimer(1000);
  assert.equal(harness.node("health-add").disabled, true); assert.equal(harness.node("health-save").disabled, true);
  await harness.node("health-form").emit("submit");
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
  assert.ok(harness.requests.some(request => request.path === "/api/v1/overview/progress"));
  harness.node("overview-auto").checked = true; await harness.node("overview-auto").emit("change");
  await harness.fireTimer(30000);
  assert.equal(harness.node("health-add").disabled, true); assert.equal(harness.node("health-save").disabled, true);
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
  await harness.node("overview-refresh").emit("click"); assert.equal(harness.node("health-add").disabled, false);
});

test("Favorites store only IDs and view preferences exclude search credentials URLs and health drafts", async () => {
  const data = overview({ servers: [server({ group: "车队", environment: "生产" }), server({ id: "b", name: "测试服务器", group: "车队", environment: "测试" })] });
  const session = { csrfToken: "SYNTHETIC_SECRET_CSRF", authenticationRequired: true, user: { appId: 1001, userId: "alice", account: "SYNTHETIC_SECRET_ACCOUNT", token: "SYNTHETIC_SECRET_TOKEN" } };
  const harness = await browser(data, { session });
  const favorite = cardActions(harness.node("overview-servers").children[1]).find(item => item.textContent === "收藏"); await favorite.emit("click");
  assert.match(descendantText(harness.node("overview-servers").children[0]), /测试服务器/);
  harness.node("overview-search").value = "SYNTHETIC_SECRET_SEARCH"; await harness.node("overview-search").emit("input");
  harness.node("health-url").value = "https://orders.example.com/SYNTHETIC_SECRET_DRAFT";
  for (const [id, value] of [["server-group", "value:车队"], ["server-environment", "value:测试"], ["server-state", "online"], ["health-filter", "disabled"], ["certificate-filter", ""]]) {
    harness.node(id).value = value; await harness.node(id).emit("change");
  }
  harness.node("server-favorites-only").checked = true; await harness.node("server-favorites-only").emit("change");
  harness.node("overview-auto").checked = true; await harness.node("overview-auto").emit("change");
  assert.equal(harness.storage.size, 1);
  const savedText = [...harness.storage.values()][0], saved = JSON.parse(savedText);
  assert.deepEqual(saved.favorites, ["b"]); assert.deepEqual(Object.keys(saved).sort(), ["autoRefresh", "favorites", "favoritesOnly", "filters", "version"]);
  assert.deepEqual(Object.keys(saved.filters).sort(), ["certificate", "environment", "group", "health", "state"]);
  assert.doesNotMatch(savedText, /SYNTHETIC_SECRET|https?:\/\/|csrf|token|account|url|search/i);
  const restored = await browser(data, { session, storage: harness.storage });
  assert.equal(restored.node("overview-search").value, ""); assert.equal(restored.node("server-favorites-only").checked, true); assert.equal(restored.node("overview-auto").checked, true);
  assert.equal(restored.node("server-group").value, "value:车队"); assert.equal(restored.node("server-environment").value, "value:测试");
  assert.equal(restored.node("server-state").value, "online"); assert.equal(restored.node("health-filter").value, "disabled"); assert.equal(restored.node("certificate-filter").value, "");
  assert.equal(restored.node("overview-servers").children.length, 1); assert.match(descendantText(restored.node("overview-servers").children[0]), /测试服务器/);
  const other = await browser(data, { session: { ...session, user: { ...session.user, userId: "bob" } }, storage: harness.storage });
  assert.equal(other.node("server-favorites-only").checked, false); assert.equal(other.node("overview-servers").children.length, 2);
});

test("Read-only overview users can inspect resources but cannot invoke any health mutation even through synthetic clicks", async () => {
  const permissions = { administration: false, servers: [{ serverId: "a", read: true, operate: false, configure: false }] };
  const harness = await browser(overview({ healthChecks: [healthCheck()] }), { permissions });
  assert.ok(harness.initialRequests.some(request => request.path === "/api/v1/permissions"));
  assert.equal(harness.node("overview-servers").children.length, 1); assert.equal(harness.node("health-add").disabled, true);
  await harness.node("health-add").emit("click"); assert.equal(harness.node("health-dialog").open, false);
  const actions = harness.node("health-body").children[0].children[4].children;
  assert.ok(actions.every(control => control.disabled));
  for (const control of actions) await control.emit("click");
  harness.node("health-server").value = "a"; harness.node("health-url").value = "https://orders.example.com/health";
  await harness.node("health-form").emit("submit"); await harness.node("health-delete-confirm").emit("click");
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 0);
});

test("Operate permission permits one health run while configuration actions remain disabled", async () => {
  const item = healthCheck(), permissions = { administration: false, servers: [{ serverId: "a", read: true, operate: true, configure: false }] };
  const harness = await browser(overview({ healthChecks: [item] }), { permissions });
  const actions = harness.node("health-body").children[0].children[4].children;
  assert.equal(actions[0].disabled, false); assert.ok(actions.slice(1).every(control => control.disabled));
  harness.custom = path => path.endsWith("/run") ? { ok: true, status: 200, json: async () => healthCheck({ result: { state: "healthy", checkedAt: "2026-10-25T08:00:10Z" } }) } : undefined;
  await actions[0].emit("click");
  assert.equal(harness.requests.length, 1); assert.equal(harness.requests[0].path, `/api/v1/health-checks/${item.id}/run`); assert.equal(harness.requests[0].method, "POST");
  assert.equal(harness.requests[0].headers["X-IISManager-CSRF"], "test-csrf");
});

test("Missing or foreign-server permissions fail closed instead of granting health writes", async () => {
  for (const permissions of [null, { administration: true, servers: [{ serverId: "other", read: true, operate: true, configure: true }] }]) {
    const harness = await browser(overview({ healthChecks: [healthCheck()] }), { permissions });
    assert.equal(harness.node("health-add").disabled, true);
    const actions = harness.node("health-body").children[0].children[4].children;
    assert.ok(actions.every(control => control.disabled));
    for (const control of actions) await control.emit("click");
    assert.equal(harness.requests.filter(request => request.method === "POST").length, 0);
  }
});

test("Permissions lookup failure leaves health controls disabled and never starts progressive collection", async () => {
  const harness = await browser(overview(), { custom: path => path === "/api/v1/permissions" ? { ok: false, status: 503, json: async () => ({ code: "ACCESS_UNAVAILABLE", message: "权限暂不可用" }) } : undefined });
  assert.equal(harness.node("health-add").disabled, true); assert.equal(harness.node("health-save").disabled, true);
  assert.equal(harness.initialRequests.some(request => request.path === "/api/v1/overview/progress"), false);
  assert.match(harness.node("notice").textContent, /权限暂不可用/);
});

test("Overview has matching script content versions, safe DOM rendering and the shared password dialog", () => {
  const html = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/overview.html"), "utf8");
  const hash = createHash("sha256").update(source).digest("hex").slice(0, 16);
  assert.ok(html.includes(`src="/overview.js?v=${hash}" defer`));
  for (const id of ["password-dialog", "password-form", "password-old", "password-new", "password-confirm", "password-submit", "session-change-password", "session-logout"]) assert.ok(html.includes(`id="${id}"`));
  assert.equal(source.includes("innerHTML"), false); assert.ok(html.includes('aria-current="page">服务器总览'));
});
