"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");
const path = require("node:path");
const diagnostics = require("../src/IISManager.Console/wwwroot/diagnostics.js");
const source = fs.readFileSync(path.join(__dirname, "../src/IISManager.Console/wwwroot/diagnostics.js"), "utf8");
function snapshot(extra = {}) {
  return { collectedAt: "2026-10-25T12:00:00Z", mode: "Windows", cacheSeconds: 10,
    resources: { cpuPercent: null, totalMemoryBytes: 1000, availableMemoryBytes: 400, logicalProcessors: 2, disks: [{ name: "C:\\", totalBytes: 2000, availableBytes: 500 }] },
    processes: [{ kind: "applicationPool", name: "DemoPool", state: "Started", processId: 42, cpuPercent: null, workingSetBytes: 100, privateMemoryBytes: 200, startedAt: "2026-10-25T11:00:00Z", uptimeSeconds: 3600 }],
    events: [], iisErrors: [], modules: [{ module: "resources", state: "available" }], ...extra };
}
test("diagnostic projection keeps unknown readings null", () => {
  const value = diagnostics.sanitizedSnapshot(snapshot());
  assert.equal(value.resources.cpuPercent, null); assert.equal(value.processes[0].cpuPercent, null);
  assert.equal(diagnostics.bytes(null), "—"); assert.equal(diagnostics.duration(null), "—");
});
test("diagnostic export drops arbitrary secret and command fields at every level", () => {
  const input = snapshot({ accessToken: "SYNTHETIC_SECRET", rawLog: "SYNTHETIC_SECRET" });
  input.resources.environment = "SYNTHETIC_SECRET"; input.processes[0].commandLine = "SYNTHETIC_SECRET";
  const output = JSON.stringify(diagnostics.sanitizedSnapshot(input)); assert.ok(!output.includes("SYNTHETIC_SECRET"));
});
test("diagnostic events use provider whitelist and regenerate fixed summaries", () => {
  const event = { timeCreated: "2026-10-25T11:00:00Z", logName: "Application", provider: ".NET Runtime", eventId: 1026, level: 2, recordId: 12, summary: "SYNTHETIC_SECRET", userName: "SYNTHETIC_SECRET" };
  const value = diagnostics.sanitizedSnapshot(snapshot({ events: [event, { ...event, provider: "Unknown" }] }));
  assert.equal(value.events.length, 1); assert.ok(!JSON.stringify(value.events).includes("SYNTHETIC_SECRET"));
});
test("diagnostic numeric values cannot become NaN or negative usage", () => {
  const input = snapshot(); input.resources.cpuPercent = 1000; input.resources.availableMemoryBytes = -1; input.processes[0].uptimeSeconds = Infinity;
  const value = diagnostics.sanitizedSnapshot(input);
  assert.equal(value.resources.cpuPercent, null); assert.equal(value.resources.availableMemoryBytes, null); assert.equal(value.processes[0].uptimeSeconds, null);
});
test("diagnostic IIS export contains only aggregate numbers and site identity", () => {
  const value = diagnostics.sanitizedSnapshot(snapshot({ iisErrors: [{ siteName: "Demo", state: "partial", windowStart: "2026-10-24T12:00:00Z", windowEnd: "2026-10-25T12:00:00Z", sampledFiles: 2, sampledLines: 10, truncated: true, rawLog: "SYNTHETIC_SECRET", errors: [{ status: 503, subStatus: 0, win32Status: 0, count: 3, averageMilliseconds: 123, maximumMilliseconds: 150, query: "SYNTHETIC_SECRET" }] }] }));
  assert.equal(value.iisErrors[0].errors[0].count, 3); assert.ok(value.iisErrors[0].truncated); assert.ok(!JSON.stringify(value).includes("SYNTHETIC_SECRET"));
});
test("diagnostic row limits bound export size", () => {
  const value = diagnostics.sanitizedSnapshot(snapshot({ processes: Array(250).fill(snapshot().processes[0]) })); assert.equal(value.processes.length, 200);
});
test("diagnostic resource formatting distinguishes unknown from real zero", () => {
  assert.equal(diagnostics.bytes(0), "0 B"); assert.equal(diagnostics.bytes(1024), "1.0 KB"); assert.equal(diagnostics.duration(3600), "1.0 小时");
});
function browser(handler) {
  const nodes = new Map(), requests = [], exports = [];
  function element(tag = "div") {
    const listeners = new Map();
    return { tagName: tag, children: [], value: "", checked: false, hidden: false, disabled: false, textContent: "",
      append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; }, remove() {}, click() {},
      addEventListener(event, callback) { listeners.set(event, callback); }, emit(event) { return listeners.get(event)?.({ target: this }); } };
  }
  const document = { hidden: false, body: element("body"), createElement: element, addEventListener() {}, getElementById(id) { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); } };
  class FakeURL extends URL {}
  FakeURL.createObjectURL = blob => { exports.push(blob); return "blob:synthetic"; }; FakeURL.revokeObjectURL = () => {};
  const context = vm.createContext({ document, location: { search: "?server=one" }, URL: FakeURL, URLSearchParams, AbortController, Blob,
    setTimeout() { return 1; }, clearTimeout() {}, addEventListener() {}, IISManagerAuth: { render() {}, unauthorized() {} },
    async fetch(route, options) { requests.push({ route, method: options.method || "GET" }); const result = route === "/api/v1/session" ? {} : route === "/api/v1/servers" ? [{ id: "one", name: "第一台" }, { id: "two", name: "第二台" }] : await handler(route); return { ok: !result.httpStatus, status: result.httpStatus || 200, json: async () => result.body || result }; }
  });
  vm.runInContext(source, context);
  return { node: id => document.getElementById(id), requests, exports, flush: async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); } };
}
test("diagnostic UI issues only reads and shows partial modules", async () => {
  const page = browser(async () => snapshot({ resources: null, modules: [{ module: "resources", state: "unavailable", errorCode: "DIAGNOSTICS_ACCESS_DENIED" }] })); await page.flush();
  assert.ok(page.requests.every(request => request.method === "GET")); assert.match(page.node("diagnostics-modules").children[0].textContent, /不可用/); assert.equal(page.node("diagnostics-export").disabled, false);
});
test("diagnostic UI cannot mix an old server response into the new selection", async () => {
  let resolveFirst;
  const page = browser(route => route.includes("/one/") ? new Promise(resolve => { resolveFirst = resolve; }) : snapshot({ mode: "Simulation" })); await page.flush();
  page.node("diagnostics-server").value = "two"; page.node("diagnostics-server").emit("change"); await page.flush(); resolveFirst(snapshot()); await page.flush();
  assert.match(page.node("diagnostics-updated").textContent, /模拟环境/);
});
test("diagnostic UI explains old Agent support and disables export on failure", async () => {
  const page = browser(async () => ({ httpStatus: 501, body: { code: "DIAGNOSTICS_UNSUPPORTED" } })); await page.flush();
  assert.match(page.node("notice").textContent, /更新 Agent/); assert.equal(page.node("diagnostics-export").disabled, true);
});
test("diagnostic UI exports a projected JSON snapshot", async () => {
  const page = browser(async () => snapshot({ token: "SYNTHETIC_SECRET" })); await page.flush(); page.node("diagnostics-export").emit("click");
  assert.equal(page.exports.length, 1); const output = JSON.parse(await page.exports[0].text()); assert.equal(output.serverId, "one"); assert.ok(!JSON.stringify(output).includes("SYNTHETIC_SECRET"));
});
