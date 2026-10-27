"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const { createHash, webcrypto } = require("node:crypto");
const vm = require("node:vm");
const root = join(__dirname, "../src/IISManager.Console/wwwroot");
const shared = readFileSync(join(root, "operation-health.js"), "utf8");
const pure = require(join(root, "operation-health.js"));
const operationId = "00000000-0000-4000-8000-000000000011";
const tick = () => new Promise(resolve => setImmediate(resolve));
const followUp = (change = {}) => ({ version: 1, state: "pending", requestedAt: "2026-10-27T08:00:00Z", startedAt: null, completedAt: null, scopeSource: "agentRecord", errorCode: null, matchedChecks: 1, results: [], ...change });
const checkResult = (change = {}) => ({ checkId: "a9e53562-b781-4968-ab2b-5109249abf8d", checkRevision: "r1", siteName: "Orders", applicationPath: "/api", state: "healthy", statusCode: 200, elapsedMilliseconds: 42, checkedAt: "2026-10-27T08:00:01Z", errorCode: null, ...change });
const task = (health = followUp(), change = {}) => ({ serverId: "104", state: "succeeded", request: { operationId, type: "applicationPool.recycle", target: { applicationPoolName: "Web.Root" }, parameters: {} }, updatedAt: "2026-10-27T08:00:00Z", healthFollowUp: health, ...change });
const row = (id = operationId) => ({ operationId: id, serverId: "104", type: "applicationPool.recycle", target: "Web.Root", state: "succeeded", acceptedAt: "2026-10-27T08:00:00Z", updatedAt: "2026-10-27T08:00:00Z" });
const content = element => [typeof element?.textContent === "string" ? element.textContent : "", ...(element?.children || []).map(content)].join(" ");

async function browser(page = "app") {
  const nodes = new Map(), all = new Set(), storage = new Map(), timers = new Map(), events = new Map(); let timerSequence = 0;
  const harness = { requests: [], handle: null, currentTask: task(), timers, events };
  function node(tag = "div") {
    const listeners = new Map();
    const item = { tagName: tag.toUpperCase(), value: "", checked: false, disabled: false, hidden: false, open: false, dataset: {}, attributes: {}, children: [], parentNode: null,
      get options() { return this.children.filter(child => child.tagName === "OPTION"); }, get firstChild() { return this.children[0]; }, get isConnected() { return this.root || this.parentNode?.isConnected; },
      append(...items) { for (const child of items) { child.parentNode = this; this.children.push(child); } },
      prepend(...items) { this.append(...items); this.children.unshift(...this.children.splice(this.children.length - items.length)); },
      insertBefore(child, reference) { this.append(child); this.children.pop(); this.children.splice(this.children.indexOf(reference), 0, child); },
      replaceChildren(...items) { this.children.forEach(child => { child.parentNode = null; }); this.children = []; this.append(...items); },
      remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; },
      setAttribute(name, value) { this.attributes[name] = value; },
      addEventListener(name, fn) { if (!listeners.has(name)) listeners.set(name, []); listeners.get(name).push(fn); },
      async emit(name, event = {}) { for (const callback of listeners.get(name) || []) await callback({ target: this, preventDefault() {}, ...event }); await tick(); },
      showModal() { this.open = true; }, close() { this.open = false; for (const callback of listeners.get("close") || []) callback(); }, focus() {}
    }; all.add(item); return item;
  }
  const document = { visibilityState: "visible", createElement: node,
    getElementById(id) { const dynamic = [...all].find(item => item.id === id && item.isConnected); if (dynamic) return dynamic; if (!nodes.has(id)) { const item = node(); item.root = true; item.id = id; nodes.set(id, item); } return nodes.get(id); },
    querySelectorAll(selector) { return [...all].filter(item => item.isConnected && (selector === "[data-binding]" ? item.dataset.binding : selector === "[data-operation]" ? item.dataset.operation : selector === "[data-server-id]" ? item.dataset.serverId : selector === "[data-pool-state]" ? item.dataset.poolState !== undefined : selector === "[data-site-annotation]" ? item.dataset.siteAnnotation : selector === "[data-annotation-control]" ? item.dataset.annotationControl : false)); },
    addEventListener(name, fn) { events.set(name, fn); }
  };
  document.body = node("body"); document.body.root = true; document.body.classList = { add() {}, remove() {}, contains() { return false; } };
  const context = vm.createContext({ document, URL, URLSearchParams, crypto: webcrypto, location: { hostname: "console.test", search: "" },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    setTimeout(fn, ms) { const id = ++timerSequence; timers.set(id, { fn, ms }); return id; }, clearTimeout(id) { timers.delete(id); }, addEventListener(name, fn) { events.set(name, fn); },
    async fetch(path, options = {}) {
      const request = { path, method: options.method || "GET", body: options.body }; harness.requests.push(request);
      let data = harness.handle ? await harness.handle(request) : undefined;
      if (data === undefined) {
        if (path === "/api/v1/session") data = { csrfToken: "synthetic-session" };
        else if (path === "/api/v1/servers") data = page === "app" ? [] : [{ id: "104", name: "Read-only Server", hostName: "agent.test" }];
        else if (path === "/api/v1/permissions") data = { administration: false, servers: [{ serverId: "104", read: true, operate: false, configure: false }] };
        else if (/\/operations\/[^/?]+$/.test(path)) data = harness.currentTask;
        else if (path.startsWith("/api/v1/operations?")) { const query = new URLSearchParams(path.split("?")[1]); data = { items: [], total: 0, page: Number(query.get("page")), pageSize: Number(query.get("pageSize")) }; }
        else if (path.endsWith("/snapshot")) data = { collectedAt: "2026-10-27T08:00:00Z", sites: [], applicationPools: [] };
        else if (path.endsWith("/info")) data = { protocolVersion: "1.0", mode: "Simulation" };
        else if (path.endsWith("/certificates")) data = [];
        else if (path.endsWith("/windows-services")) data = { services: [], canCreate: false };
        else if (path.endsWith("/site-topology")) data = { sites: [], applicationPools: [] };
        else if (path.endsWith("/site-annotations")) data = { serverId: "104", entries: [] };
        else throw new Error("UNEXPECTED_TEST_REQUEST");
      }
      const status = data?.httpStatus ?? 200, body = data?.httpStatus ? data.body : data;
      return { ok: status >= 200 && status < 300, status, json: async () => body };
    }
  });
  harness.run = code => vm.runInContext(code, context); harness.node = id => document.getElementById(id); harness.document = document;
  harness.node("records-size").value = "20";
  harness.run(shared); harness.run(readFileSync(join(root, page === "app" ? "app.js" : "operations.js"), "utf8")); await tick(); await tick();
  if (page === "app") harness.run('state.serverId="104"; state.servers=[{id:"104",name:"Read-only Server"}];');
  harness.requests.length = 0;
  harness.setTask = value => harness.run(`state.task=${JSON.stringify(value)};state.taskHealthReadError=false;renderTask();`);
  harness.fireTimer = async () => { const entry = [...timers.entries()][0]; assert.ok(entry, "Expected pending poll"); timers.delete(entry[0]); await entry[1].fn(); await tick(); };
  harness.visibility = async value => { document.visibilityState = value; await events.get("visibilitychange")?.(); await tick(); };
  return harness;
}

test("Follow-up projection only returns approved fields and never exposes URLs, bodies, credentials or unknown error text", () => {
  const input = followUp({ state: "completed", url: "https://private.test/?token=SECRET", token: "SECRET", message: "SECRET", results: [checkResult({ responseBody: "SECRET", url: "https://private.test/SECRET", errorCode: "SECRET" })] });
  const safe = pure.project(input), serialized = JSON.stringify(safe);
  assert.equal(serialized.includes("SECRET"), false); assert.equal(serialized.includes("https://"), false); assert.equal(serialized.includes("checkRevision"), false);
  assert.equal(safe.results[0].errorCode.value, null); assert.equal(input.results[0].errorCode, "SECRET");
  assert.equal(pure.project(null), null); assert.equal(pure.project({ ...input, version: 2 }).state, "unknown");
  assert.equal(pure.project(followUp({ results: [checkResult({ state: "constructor" })] })).results[0].state, "unknown");
});

test("All follow-up stages have fixed labels and 20-check/60-second limits are explicit", () => {
  for (const state of ["pending", "running", "completed", "skipped", "unknown"]) assert.ok(pure.project(followUp({ state })).stateLabel);
  assert.equal(pure.isPending(followUp()), true); assert.equal(pure.isPending(followUp({ state: "running" })), true);
  assert.equal(pure.isPending(followUp({ state: "completed" })), false); assert.equal(pure.isPending(null), false);
  const limited = pure.project(followUp({ matchedChecks: 35, errorCode: "HEALTH_CHECK_LIMIT_REACHED", results: Array.from({ length: 25 }, () => checkResult()) }));
  assert.equal(limited.results.length, 20); assert.match(limited.errorCode.label, /20/); assert.equal(limited.truncated, true);
  assert.match(pure.project(followUp({ errorCode: "HEALTH_BATCH_TIMEOUT" })).errorCode.label, /60 秒/);
});

test("Renderer preserves safe text and simulation stays separate from real healthy outcomes", async () => {
  const harness = await browser(), value = followUp({ state: "completed", results: [checkResult({ siteName: "<img src=x onerror=alert(1)>", state: "simulation", statusCode: null, elapsedMilliseconds: null, errorCode: "HEALTH_SIMULATION" })] });
  harness.setTask(task(value)); const rendered = content(harness.node("task-health-follow-up"));
  assert.match(rendered, /模拟检查/); assert.match(rendered, /<img src=x/); assert.match(rendered, /HEALTH_SIMULATION/); assert.equal(harness.node("task-status").textContent, "执行成功");
  assert.equal(harness.node("task-health-follow-up").children.some(item => item.tagName === "IMG"), false);
  assert.equal(JSON.parse(harness.node("task-json").textContent).healthFollowUp, undefined);
});

test("Read-only workbench continues original-ID GET polling after IIS success until health verification completes", async () => {
  const harness = await browser(); harness.setTask(task()); harness.run("schedulePoll();");
  assert.equal([...harness.timers.values()][0].ms, 5000);
  harness.currentTask = task(followUp({ state: "completed", completedAt: "2026-10-27T08:00:02Z", results: [checkResult({ state: "unhealthy", statusCode: 503, errorCode: "HEALTH_STATUS_MISMATCH" })] }));
  await harness.fireTimer();
  assert.equal(harness.run("state.task.state"), "succeeded"); assert.equal(harness.node("task-status").textContent, "执行成功"); assert.match(content(harness.node("task-health-follow-up")), /检查异常/);
  assert.equal(harness.timers.size, 0); assert.deepEqual(harness.requests.map(request => request.path), [`/api/v1/servers/104/operations/${operationId}`]); assert.ok(harness.requests.every(request => request.method === "GET"));
});

test("A health follow-up read failure preserves known IIS success and never exposes raw response errors", async () => {
  const harness = await browser(); harness.setTask(task());
  harness.handle = () => ({ httpStatus: 503, body: { code: "UNSAFE", message: "SECRET:https://private/?token=value" } });
  await harness.run("queryTask(true)");
  assert.equal(harness.run("state.task.state"), "succeeded"); assert.equal(harness.run("state.task.error"), undefined);
  assert.equal(content(harness.node("task-health-follow-up")).includes("SECRET"), false); assert.match(content(harness.node("task-health-follow-up")), /已保留最近结果/);
  assert.equal([...harness.timers.values()][0].ms, 10000);
});

test("Follow-up queries reject another operation's response while GUID letter casing remains compatible", async () => {
  const harness = await browser(), mixedId = "ABCDEF00-0000-4000-8000-000000000011";
  harness.setTask(task(followUp(), { request: { operationId: mixedId, type: "applicationPool.recycle", target: {} } }));
  harness.currentTask = task(followUp({ state: "completed", results: [checkResult()] }), { request: { operationId: mixedId.toLowerCase(), type: "applicationPool.recycle", target: {} } });
  await harness.run("queryTask(true)"); assert.equal(harness.run("state.task.healthFollowUp.state"), "completed");
  harness.setTask(task()); harness.currentTask = task(followUp({ state: "completed", results: [checkResult({ siteName: "Unrelated" })] }), { request: { operationId: "00000000-0000-4000-8000-000000000099", type: "applicationPool.recycle", target: {} } });
  await harness.run("queryTask(true)"); assert.equal(harness.run("state.task.request.operationId"), operationId); assert.equal(harness.run("state.task.healthFollowUp.state"), "pending"); assert.equal(content(harness.node("task-health-follow-up")).includes("Unrelated"), false);
});

test("Workbench polling pauses while hidden, resumes when visible and keeps legacy tasks compatible", async () => {
  const harness = await browser(); harness.setTask(task()); harness.run("schedulePoll();"); await harness.visibility("hidden");
  assert.equal(harness.timers.size, 0); await harness.run("queryTask(true)"); assert.equal(harness.requests.length, 0);
  await harness.visibility("visible"); assert.equal(harness.timers.size, 1);
  harness.setTask(task(undefined, { healthFollowUp: undefined })); harness.run("schedulePoll();");
  assert.equal(harness.node("task-health-follow-up").hidden, true); assert.equal(harness.timers.size, 0);
  harness.setTask(task(undefined, { state: "queued", healthFollowUp: undefined })); harness.run("schedulePoll();"); assert.equal([...harness.timers.values()][0].ms, 1500);
});

test("History opens persisted follow-up results and polls only the original task GET until terminal", async () => {
  const harness = await browser("history"); harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); await tick();
  assert.equal(harness.node("record-health-section").hidden, false); assert.match(content(harness.node("record-health-follow-up")), /等待验证/);
  harness.currentTask = task(followUp({ state: "completed", results: [checkResult()] })); await harness.fireTimer();
  assert.match(content(harness.node("record-health-follow-up")), /检查正常/); assert.equal(harness.node("record-state").textContent, "执行成功"); assert.equal(harness.timers.size, 0);
  await harness.node("record-close").emit("click"); harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); await tick();
  assert.match(content(harness.node("record-health-follow-up")), /验证完成/); assert.equal(harness.timers.size, 0);
  assert.ok(harness.requests.every(request => request.method === "GET" && request.path === `/api/v1/servers/104/operations/${operationId}`));
});

test("History closes or hides without leaving a poll and rejects stale responses from another detail", async () => {
  const harness = await browser("history"), id2 = "00000000-0000-4000-8000-000000000022"; let release;
  harness.handle = request => request.path.endsWith(operationId) ? new Promise(resolve => { release = resolve; }) : task(followUp({ state: "completed", results: [checkResult({ siteName: "Second" })] }), { request: { operationId: id2, type: "applicationPool.recycle", target: {} } });
  harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); harness.run(`openRecord(${JSON.stringify(row(id2))});`); await tick(); await tick();
  release(task(followUp({ state: "completed", results: [checkResult({ siteName: "Stale" })] }))); await tick(); await tick();
  assert.match(content(harness.node("record-health-follow-up")), /Second/); assert.equal(content(harness.node("record-health-follow-up")).includes("Stale"), false);
  harness.handle = null; harness.currentTask = task(); harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); await tick(); await harness.visibility("hidden");
  assert.equal(harness.timers.size, 0); const count = harness.requests.length; await harness.run("readRecordTask()"); assert.equal(harness.requests.length, count);
  await harness.visibility("visible"); assert.equal(harness.timers.size, 1); await harness.node("record-close").emit("click"); assert.equal(harness.timers.size, 0);
});

test("History read errors preserve last successful operation and old tasks hide the optional area", async () => {
  const harness = await browser("history"); harness.currentTask = task(followUp({ state: "running" })); harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); await tick();
  harness.handle = () => ({ httpStatus: 403, body: { message: "SECRET" } }); await harness.fireTimer();
  assert.equal(harness.node("record-state").textContent, "执行成功"); assert.match(content(harness.node("record-health-follow-up")), /已保留最近结果/); assert.equal(content(harness.node("record-health-follow-up")).includes("SECRET"), false);
  await harness.node("record-close").emit("click"); harness.handle = null; harness.currentTask = task(undefined, { healthFollowUp: undefined }); harness.run(`openRecord(${JSON.stringify(row())});`); await tick(); await tick();
  assert.equal(harness.node("record-health-section").hidden, true); assert.equal(harness.timers.size, 0);
});

test("Both pages request current shared content versions before their page scripts", () => {
  const version = createHash("sha256").update(shared).digest("hex").slice(0, 16);
  for (const [page, script] of [["index.html", "app.js"], ["operations.html", "operations.js"]]) {
    const html = readFileSync(join(root, page), "utf8"), scriptVersion = createHash("sha256").update(readFileSync(join(root, script))).digest("hex").slice(0, 16);
    assert.ok(html.includes(`/operation-health.js?v=${version}`)); assert.ok(html.includes(`/${script}?v=${scriptVersion}`));
    assert.ok(html.indexOf("/operation-health.js") < html.indexOf(`/${script}?`));
  }
});
