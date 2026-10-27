"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const source = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/baselines.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));
const revision = "1".repeat(32), nextRevision = "2".repeat(32), fingerprint = "A".repeat(64);
function comparison(id = "a", existing = true) {
  const configuration = { pools: { Pool: { managedRuntimeVersion: "", identityType: "ApplicationPoolIdentity" } }, sites: {} };
  return { baseline: existing ? { serverId: id, revision, label: "Synthetic confirmed baseline", savedAt: "2026-10-25T00:00:00Z", savedBy: "synthetic-user", fingerprint, configuration } : null,
    currentFingerprint: id === "a" ? fingerprint : "B".repeat(64), currentConfiguration: configuration,
    differences: [{ path: "/sites/Orders", before: "old", after: "new" }], differenceCount: 1, matches: false };
}
function response(status, body) { return { ok: status >= 200 && status < 300, status, json: async () => body }; }

async function browser(options = {}) {
  const nodes = new Map(), requests = [], blobs = [], downloads = [], revoked = [];
  function element(tag = "div") {
    const events = new Map();
    return { tagName: tag, value: "", checked: false, disabled: false, hidden: false, textContent: "", open: false, children: [],
      append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; },
      addEventListener(name, handler) { events.set(name, handler); },
      async emit(name) { await events.get(name)?.({ target: this, preventDefault() {} }); await tick(); },
      showModal() { this.open = true; }, close() { this.open = false; }, click() { downloads.push({ href: this.href, download: this.download }); }
    };
  }
  function node(id) { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); }
  const harness = { node, requests, blobs, downloads, revoked, custom: null, current: { a: comparison("a", options.existing !== false), b: comparison("b", options.existing !== false) } };
  const context = vm.createContext({ document: { getElementById: node, createElement: element },
    location: { search: "" }, Date, URLSearchParams, Blob,
    URL: { createObjectURL(blob) { blobs.push(blob); return "blob:synthetic-" + blobs.length; }, revokeObjectURL(url) { revoked.push(url); } },
    IISManagerAuth: { unauthorized() {}, render() {} },
    async fetch(url, request) {
      requests.push({ url, ...request });
      if (harness.custom) { const result = await harness.custom(url, request); if (result !== undefined) return result; }
      if (url === "/api/v1/session") return response(200, { csrfToken: "SYNTHETIC-CSRF", authenticationRequired: false });
      if (url === "/api/v1/servers") return response(200, [{ id: "a", name: "Synthetic A" }, { id: "b", name: "Synthetic B" }]);
      if (url === "/api/v1/permissions") return response(200, { servers: [{ serverId: "a", read: true, configure: options.configure !== false }, { serverId: "b", read: true, configure: options.configure !== false }] });
      const match = url.match(/^\/api\/v1\/servers\/(a|b)\/configuration-baseline$/);
      assert.ok(match, "Only local synthetic baseline routes are expected");
      const id = match[1];
      if (request.method === "GET") return response(200, harness.current[id]);
      const submitted = JSON.parse(request.body);
      const baseline = { serverId: id, revision: nextRevision, label: submitted.label, savedAt: "2026-10-25T00:01:00Z", savedBy: "synthetic-user", fingerprint: submitted.currentFingerprint, configuration: harness.current[id].currentConfiguration };
      harness.current[id] = { ...harness.current[id], baseline, differences: [], differenceCount: 0, matches: true };
      return response(200, baseline);
    }
  });
  vm.runInContext(source, context); await tick(); await tick(); await tick(); requests.length = 0;
  harness.open = async () => { await node("baseline-save-open").emit("click"); node("baseline-label").value = "  Synthetic approval  "; };
  harness.confirm = async () => { node("baseline-confirm").checked = true; await node("baseline-confirm").emit("change"); };
  return harness;
}

test("opening baseline save requires explicit confirmation before sending POST", async () => {
  const harness = await browser(); await harness.open();
  assert.equal(harness.node("baseline-save-dialog").open, true);
  assert.equal(harness.node("baseline-confirm").checked, false);
  assert.equal(harness.node("baseline-save-confirm").disabled, true);
  await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.requests.filter(item => item.method === "POST").length, 0);
});
test("baseline confirmation is cleared whenever a new save dialog opens", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  await harness.node("baseline-save-cancel").emit("click"); await harness.open();
  assert.equal(harness.node("baseline-confirm").checked, false);
  assert.equal(harness.node("baseline-save-confirm").disabled, true);
});
test("save transmits the displayed revision fingerprint trimmed label and CSRF", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  await harness.node("baseline-save-form").emit("submit");
  const writes = harness.requests.filter(item => item.method === "POST"); assert.equal(writes.length, 1);
  assert.deepEqual(JSON.parse(writes[0].body), { label: "Synthetic approval", revision, currentFingerprint: fingerprint });
  assert.equal(writes[0].headers["X-IISManager-CSRF"], "SYNTHETIC-CSRF");
  assert.equal(writes[0].credentials, "same-origin");
});
test("first baseline save explicitly sends a null previous revision", async () => {
  const harness = await browser({ existing: false }); await harness.open(); await harness.confirm();
  await harness.node("baseline-save-form").emit("submit");
  const request = harness.requests.find(item => item.method === "POST");
  assert.equal(JSON.parse(request.body).revision, null);
});
test("a successful save independently re-reads the baseline before reporting verified success", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  await harness.node("baseline-save-form").emit("submit");
  assert.deepEqual(harness.requests.map(item => item.method), ["POST", "GET"]);
  assert.match(harness.node("notice").textContent, /独立读取核对/);
  assert.equal(harness.node("baseline-save-dialog").open, false);
  assert.equal(harness.current.a.baseline.revision, nextRevision);
});
test("a transport-unknown save blocks resubmission until a fresh read proves the stored state", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  harness.custom = (url, request) => {
    if (request.method === "POST") {
      harness.current.a = { ...harness.current.a, baseline: { ...harness.current.a.baseline, revision: nextRevision } };
      throw new Error("Synthetic lost reply");
    }
  };
  await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.node("baseline-save-confirm").disabled, true);
  await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.requests.filter(item => item.method === "POST").length, 1);
  await harness.node("baseline-save-close").emit("click"); await harness.node("baseline-read").emit("click");
  assert.deepEqual(harness.requests.map(item => item.method), ["POST", "GET"]);
  assert.equal(harness.node("baseline-save-open").disabled, false);
  await harness.open();
  assert.match(harness.node("baseline-save-target").textContent, /Synthetic A/);
});
test("an unverifiable store response retains the uncertainty lock and never retries POST", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  harness.custom = (url, request) => request.method === "POST" ? response(503, { code: "BASELINE_STORE_UNVERIFIED", message: "Synthetic store result unknown" }) : undefined;
  await harness.node("baseline-save-form").emit("submit"); await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.node("baseline-save-confirm").disabled, true);
  assert.equal(harness.requests.filter(item => item.method === "POST").length, 1);
});
test("a failed read after unknown save does not unlock further writes", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  harness.custom = (url, request) => { if (request.method === "POST") throw new Error("Synthetic transport failure"); return response(503, { code: "BASELINE_STORE_UNAVAILABLE", message: "Synthetic unavailable" }); };
  await harness.node("baseline-save-form").emit("submit"); await harness.node("baseline-save-close").emit("click");
  await harness.node("baseline-read").emit("click");
  assert.equal(harness.node("baseline-save-open").disabled, true);
  assert.equal(harness.requests.filter(item => item.method === "POST").length, 1);
});
test("read-only permissions disable baseline writes even if submit is triggered directly", async () => {
  const harness = await browser({ configure: false });
  assert.equal(harness.node("baseline-save-open").disabled, true);
  assert.equal(harness.node("baseline-permission").hidden, false);
  await harness.open(); await harness.confirm(); await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.requests.filter(item => item.method === "POST").length, 0);
  assert.equal(harness.node("baseline-export").disabled, false);
});
test("malformed successful save receipts remain unknown instead of becoming verified success", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  harness.custom = (url, request) => request.method === "POST" ? response(200, { serverId: "b", revision: nextRevision, fingerprint }) : undefined;
  await harness.node("baseline-save-form").emit("submit");
  assert.equal(harness.node("baseline-save-confirm").disabled, true);
  assert.match(harness.node("baseline-save-error").textContent, /回执不完整/);
  assert.equal(harness.requests.filter(item => item.method === "GET").length, 0);
});
test("revision and fingerprint conflict errors never announce save success or auto-retry", async () => {
  for (const code of ["BASELINE_REVISION_CONFLICT", "BASELINE_CONFIGURATION_CHANGED"]) {
    const harness = await browser(); await harness.open(); await harness.confirm();
    harness.custom = (url, request) => request.method === "POST" ? response(409, { code, message: "Synthetic conflict" }) : undefined;
    await harness.node("baseline-save-form").emit("submit");
    assert.match(harness.node("baseline-save-error").textContent, /conflict/);
    assert.equal(harness.requests.filter(item => item.method === "POST").length, 1);
    assert.equal(harness.requests.filter(item => item.method === "GET").length, 0);
    assert.doesNotMatch(harness.node("notice").textContent, /已保存/);
  }
});
test("changing server discards the old confirmation and uses the new server fingerprint", async () => {
  const harness = await browser(); await harness.open(); await harness.confirm();
  harness.node("baseline-server").value = "b"; await harness.node("baseline-server").emit("change"); await tick();
  assert.equal(harness.node("baseline-save-dialog").open, false);
  await harness.open(); await harness.confirm(); await harness.node("baseline-save-form").emit("submit");
  const write = harness.requests.find(item => item.method === "POST");
  assert.match(write.url, /\/servers\/b\//); assert.equal(JSON.parse(write.body).currentFingerprint, "B".repeat(64));
});
test("JSON export contains the viewed baseline configuration and differences without a POST", async () => {
  const harness = await browser(); await harness.node("baseline-export").emit("click");
  assert.equal(harness.requests.length, 0); assert.equal(harness.blobs.length, 1);
  assert.equal(harness.blobs[0].type, "application/json;charset=utf-8");
  assert.deepEqual(JSON.parse(await harness.blobs[0].text()), harness.current.a);
  assert.equal(harness.downloads[0].download, "configuration-baseline-a.json");
  assert.deepEqual(harness.revoked, [harness.downloads[0].href]);
});
test("all untrusted baseline differences are rendered as text rather than HTML", async () => {
  const harness = await browser();
  harness.current.a.differences = [{ path: "/sites/<script>", before: "<img src=x onerror=evil()>", after: "<script>evil()</script>" }];
  await harness.node("baseline-read").emit("click");
  const row = harness.node("baseline-changes").children[0];
  assert.deepEqual(row.children.map(cell => cell.textContent), ["/sites/<script>", "<img src=x onerror=evil()>", "<script>evil()</script>"]);
  assert.ok(row.children.every(cell => !("innerHTML" in cell)));
});
