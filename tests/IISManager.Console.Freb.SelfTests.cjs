"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const file = join(__dirname, "../src/IISManager.Console/wwwroot/freb.js"), source = readFileSync(file, "utf8"), freb = require(file);
const trace = overrides => ({ traceId: "a".repeat(64), startedAt: "2026-10-27T03:01:00Z", status: 500, subStatus: 30, win32Status: 0,
  timeTakenMilliseconds: 12, method: "GET", failureModule: "AspNetCoreModuleV2", failureNotification: "EXECUTE_REQUEST_HANDLER", truncated: false,
  events: [{ sequence: 1, timeCreated: "2026-10-27T03:01:00Z", offsetMilliseconds: 0, eventName: "MODULE_SET_RESPONSE_ERROR_STATUS", module: "AspNetCoreModuleV2", notification: "EXECUTE_REQUEST_HANDLER", status: 500, subStatus: 30, win32Status: 0 }], ...overrides });
const snapshot = overrides => ({ collectedAt: "2026-10-27T03:02:00Z", mode: "Windows", cacheSeconds: 10, state: "available", errorCode: null,
  sites: [{ siteId: 1, siteName: "Synthetic site", state: "available", errorCode: null, truncated: false, traces: [trace()] }], ...overrides });
test("FREB projection excludes raw request fields and arbitrary event data", () => {
  const value = snapshot({ url: "SYNTHETIC_SECRET" }); value.sites[0].traces[0] = trace({ path: "SYNTHETIC_SECRET", headers: { authorization: "SYNTHETIC_SECRET" }, body: "SYNTHETIC_SECRET", failureModule: "SYNTHETIC_SECRET",
    events: [{ ...trace().events[0], xml: "SYNTHETIC_SECRET", eventName: "SYNTHETIC_SECRET", module: "SYNTHETIC_SECRET", notification: "SYNTHETIC_SECRET" }] });
  const clean = freb.sanitizedSnapshot(value); assert.ok(!JSON.stringify(clean).includes("SYNTHETIC_SECRET"));
  assert.equal(clean.sites[0].traces[0].failureModule, "OTHER");
});
test("FREB keeps actual zero distinct from unknown and rejects invalid shapes", () => {
  const value = snapshot(); value.sites[0].traces = [trace({ timeTakenMilliseconds: null, win32Status: -1, subStatus: null })];
  const clean = freb.sanitizedSnapshot(value).sites[0].traces[0]; assert.equal(clean.timeTakenMilliseconds, null); assert.equal(clean.win32Status, null); assert.equal(clean.events[0].offsetMilliseconds, 0);
  for (const bad of [null, {}, snapshot({ state: "healthy" }), snapshot({ sites: null }), snapshot({ collectedAt: "invalid" })]) assert.throws(() => freb.sanitizedSnapshot(bad));
});
test("FREB bounds request/event projection and marks truncated rows", () => {
  const value = snapshot({ sites: Array.from({ length: 10 }, (_, index) => ({ siteId: index + 1, siteName: "site", state: "available", traces: Array.from({ length: 25 }, () => trace({ events: Array(100).fill(trace().events[0]) })) })) });
  const clean = freb.sanitizedSnapshot(value); assert.equal(clean.sites.reduce((sum, site) => sum + site.traces.length, 0), 64);
  assert.equal(clean.sites[0].traces[0].events.length, 96); assert.equal(clean.sites[0].truncated, true); assert.equal(clean.sites[0].traces[0].truncated, true);
});
test("FREB projection retains the backend's last failure event in a 96-event trace", () => {
  const value = snapshot(); value.sites[0].traces[0] = trace({ events: Array.from({ length: 96 }, (_, index) => ({
    ...trace().events[0], sequence: index === 95 ? 4096 : index + 1,
    eventName: index === 95 ? "MODULE_SET_RESPONSE_ERROR_STATUS" : "NOTIFY_MODULE_START", status: index === 95 ? 500 : null
  })) });
  const events = freb.sanitizedSnapshot(value).sites[0].traces[0].events;
  assert.equal(events.length, 96); assert.equal(events[95].sequence, 4096); assert.equal(events[95].eventName, "MODULE_SET_RESPONSE_ERROR_STATUS"); assert.equal(events[95].status, 500);
});
function browser(handler) {
  const nodes = new Map(), requests = [], exports = [], pageEvents = new Map();
  const element = (tag = "div") => {
    const events = new Map();
    return { tagName: tag, children: [], value: "", textContent: "", hidden: false, disabled: false,
      append(...values) { this.children.push(...values); }, replaceChildren(...values) { this.children = values; },
      addEventListener(name, callback) { events.set(name, callback); }, emit(name) { return events.get(name)?.({ preventDefault() {}, target: this }); }, click() {}, remove() {} };
  };
  const document = { createElement: element, body: element("body"), getElementById(id) { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); } };
  class FakeURL extends URL {}
  FakeURL.createObjectURL = blob => { exports.push(blob); return "blob:synthetic"; }; FakeURL.revokeObjectURL = () => {};
  const harness = { requests, exports, node: id => document.getElementById(id), handler, pageEvents };
  const context = vm.createContext({ document, URL: FakeURL, Blob, AbortController, setTimeout() {}, addEventListener(name, callback) { pageEvents.set(name, callback); },
    IISManagerAuth: { unauthorized() {} }, async fetch(path, options) { requests.push({ path, options }); const data = await harness.handler(path, options); return { ok: !data.httpStatus, status: data.httpStatus || 200, json: async () => data.body || data }; } });
  vm.runInContext(source, context); harness.load = id => vm.runInContext(`IISManagerFreb.load(${JSON.stringify(id)})`, context); return harness;
}
const allText = node => [node.textContent, ...node.children.flatMap(child => allText(child))].join(" ");
test("FREB UI uses server-scoped GET and renders request timeline without changing server state", async () => {
  const page = browser(async () => snapshot()); await page.load("server-a");
  assert.equal(page.requests.length, 1); assert.equal(page.requests[0].path, "/api/v1/servers/server-a/diagnostics/freb"); assert.equal(page.requests[0].options.method, undefined);
  assert.match(allText(page.node("freb-results")), /AspNetCoreModuleV2/); assert.match(allText(page.node("freb-results")), /HTTP 500\.30/);
  assert.equal(page.node("freb-export").disabled, false);
});
test("FREB unsupported/read failures never display an Agent-provided error body", async () => {
  const page = browser(async () => ({ httpStatus: 501, body: { message: "SYNTHETIC_SECRET" } })); await page.load("one");
  assert.match(page.node("freb-error").textContent, /更新 Agent/); assert.equal(page.node("freb-export").disabled, true);
  assert.ok(!allText(page.node("freb-error")).includes("SYNTHETIC_SECRET"));
});
test("FREB disabled-site guidance preserves independent available traces", async () => {
  const value = snapshot(); value.sites.push({ siteId: 2, siteName: "Disabled", state: "disabled", errorCode: "FREB_DISABLED", traces: [] });
  const page = browser(async () => value); await page.load("one");
  assert.equal(page.node("freb-results").children.length, 2); assert.match(allText(page.node("freb-results")), /人工|操作员/); assert.match(page.node("freb-count").textContent, /1 条/);
});
test("late FREB responses cannot mix another server's trace into the current selection", async () => {
  let release; const page = browser(path => path.includes("/one/") ? new Promise(resolve => { release = resolve; }) : snapshot({ mode: "Simulation", state: "simulation", sites: [] }));
  const first = page.load("one"); await page.load("two"); release(snapshot()); await first;
  assert.match(page.node("freb-empty").textContent, /模拟环境/); assert.equal(page.node("freb-results").children.length, 0);
});
test("FREB refresh failure labels cached data as stale and blocks export", async () => {
  const page = browser(async () => snapshot()); await page.load("one"); page.handler = async () => { throw new Error("synthetic network failure"); }; await page.load("one");
  assert.match(page.node("freb-updated").textContent, /上次数据/); assert.equal(page.node("freb-export").disabled, true);
  page.node("freb-export").emit("click"); assert.equal(page.exports.length, 0);
});
test("FREB export is sanitized again and retains only the selected server's snapshot", async () => {
  const page = browser(async () => snapshot({ headers: "SYNTHETIC_SECRET", fileName: "SYNTHETIC_SECRET" })); await page.load("one"); page.node("freb-export").emit("click");
  assert.equal(page.exports.length, 1); const output = await page.exports[0].text(); assert.ok(!output.includes("SYNTHETIC_SECRET")); assert.equal(JSON.parse(output).serverId, "one");
});
test("FREB site and status filters do not issue extra network calls", async () => {
  const value = snapshot(); value.sites[0].traces.push(trace({ traceId: "b".repeat(64), status: 404 }));
  const page = browser(async () => value); await page.load("one"); page.node("freb-state-filter").value = "4xx"; page.node("freb-state-filter").emit("change");
  assert.match(page.node("freb-count").textContent, /1 条/); assert.equal(page.requests.length, 1); assert.match(allText(page.node("freb-results")), /HTTP 404/);
});
test("FREB rendering treats malicious resource names as text", async () => {
  const value = snapshot(); value.sites[0].siteName = "<img src=x onerror=alert(1)>"; const page = browser(async () => value); await page.load("one");
  assert.equal(page.node("freb-results").children[0].children[0].textContent, value.sites[0].siteName + " · 已采集"); assert.equal(page.node("freb-results").children[0].children[0].children.length, 0);
});
