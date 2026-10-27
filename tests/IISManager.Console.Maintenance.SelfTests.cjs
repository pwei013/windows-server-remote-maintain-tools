"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const scriptPath = join(__dirname, "../src/IISManager.Console/wwwroot/maintenance.js");
const source = readFileSync(scriptPath, "utf8");
const pure = require(scriptPath);
const id = "7c1d0d31-5613-405c-845e-13fcb80623c2";
const basic = overrides => ({ serverId: "a", siteName: "Site", type: "site.update", physicalPath: "D:\\Sites\\New", applicationPoolName: "Pool", changePhysical: true, ...overrides });
const tick = () => new Promise(resolve => setImmediate(resolve));

test("maintenance patches omit every unchecked field", () => {
  const request = pure.buildDraft(basic(), id, 1000);
  assert.deepEqual(request.target, { siteName: "Site" });
  assert.deepEqual(request.parameters, { physicalPath: "D:\\Sites\\New" });
  assert.equal(request.operationId, id); assert.equal(Date.parse(request.expiresAt), 301000);
  assert.equal(request.expectedFingerprint, null);
  assert.throws(() => pure.buildDraft(basic({ changePhysical: false }), id));
});
test("application create requires an existing pool and a directory and cannot target root", () => {
  const request = pure.buildDraft(basic({ type: "application.create", applicationPath: "/api", changePhysical: false }), id);
  assert.deepEqual(request.target, { siteName: "Site", applicationPath: "/api" });
  assert.equal(request.parameters.applicationPoolName, "Pool");
  assert.throws(() => pure.buildDraft(basic({ type: "application.create", applicationPath: "/" }), id));
  assert.throws(() => pure.buildDraft(basic({ type: "application.create", applicationPath: "/api", applicationPoolName: "" }), id));
});
test("virtual directory creation includes its owner and inherits its pool", () => {
  const request = pure.buildDraft(basic({ type: "virtualDirectory.create", applicationPath: "/api", virtualDirectoryPath: "/assets", changePool: false }), id);
  assert.deepEqual(request.target, { siteName: "Site", applicationPath: "/api", virtualDirectoryPath: "/assets" });
  assert.deepEqual(request.parameters, { physicalPath: "D:\\Sites\\New" });
  assert.throws(() => pure.buildDraft(basic({ type: "virtualDirectory.update", applicationPath: "/api", virtualDirectoryPath: "/" }), id));
});
test("IIS paths reject dot segments encoded separators controls and alias ambiguity", () => {
  for (const value of ["/", "/api/", "/a//b", "/a/../b", "/a%2fb", "/a\\b", "/a;b", "/a?b", "/a#b", "/a.", "/ a", "/a\u0001"]) assert.equal(pure.canonicalPath(value), false);
  for (const value of ["/api", "/api/assets", "/业务"]) assert.equal(pure.canonicalPath(value), true);
  assert.equal(pure.canonicalPath("/", true), true);
});
test("HTTP binding patches strip TLS options while HTTPS needs an existing certificate", () => {
  const binding = { protocol: "http", ipAddress: "*", port: 80, hostName: "app.example.test", certificateThumbprint: "A".repeat(40), useSni: true };
  const request = pure.buildDraft(basic({ changePhysical: false, changeBindings: true, bindings: [binding] }), id);
  assert.equal(request.parameters.bindings[0].useSni, false); assert.ok(!("certificateThumbprint" in request.parameters.bindings[0]));
  assert.throws(() => pure.buildDraft(basic({ changeBindings: true, bindings: [{ ...binding, protocol: "https", certificateThumbprint: "" }] }), id));
});
test("missing or malformed permission data never grants configure", () => {
  for (const data of [null, {}, { servers: [] }, { servers: [{ serverId: "a", read: true, configure: "true" }] }]) assert.equal(pure.permission(data, "a").configure, false);
  assert.deepEqual(pure.permission({ servers: [{ serverId: "a", read: true, configure: true }] }, "a"), { read: true, configure: true });
});
test("operation responses must match the original server and operation ID", () => {
  const index = { serverId: "a", operationId: id };
  assert.equal(pure.validTask({ serverId: "a", request: { operationId: id }, state: "partial" }, index), true);
  assert.equal(pure.validTask({ serverId: "b", request: { operationId: id }, state: "succeeded" }, index), false);
  assert.equal(pure.validTask({ serverId: "a", request: { operationId: "other" }, state: "succeeded" }, index), false);
  assert.equal(pure.validTask({ serverId: "a", request: { operationId: id }, state: "made-up" }, index), false);
});
test("intranet HTTP can generate a cryptographic UUID without the secure-context randomUUID API", () => {
  const value = pure.createOperationId({ getRandomValues(bytes) { bytes.fill(7); return bytes; } });
  assert.match(value, /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/);
  assert.throws(() => pure.createOperationId({}));
});

async function browser(overrides = {}) {
  const nodes = new Map(), created = [], requests = [], timers = new Map(), sessions = new Map(), history = new Map();
  if (overrides.pending) sessions.set("iis-manager-maintenance-pending-v1", JSON.stringify(overrides.pending));
  const formIds = ["maintenance-server", "maintenance-site", "maintenance-action", "maintenance-application", "maintenance-directory", "maintenance-new-application", "maintenance-new-directory", "maintenance-physical", "maintenance-pool", "maintenance-change-physical", "maintenance-change-pool", "maintenance-change-bindings", "maintenance-add-binding", "maintenance-precheck"];
  let timer = 0, uuid = 1;
  function element(tag = "div") {
    const events = new Map();
    const item = { tagName: tag, id: "", value: "", checked: false, hidden: false, disabled: false, textContent: "", children: [], attributes: {}, events,
      append(...values) { this.children.push(...values); }, replaceChildren(...values) { this.children = values; },
      setAttribute(name, value) { this.attributes[name] = value; }, addEventListener(name, fn) { events.set(name, fn); },
      querySelectorAll() { return this.id === "maintenance-form" ? formIds.map(node) : this.id === "maintenance-bindings" ? created.filter(value => ["input", "select", "button"].includes(value.tagName)) : []; },
      async emit(name) {
        const event = { preventDefault() {}, target: this };
        await events.get(name)?.(event);
        if (formIds.includes(this.id) && ["input", "change"].includes(name)) await node("maintenance-form").events.get(name)?.(event);
        await tick();
      }
    };
    created.push(item); return item;
  }
  function node(name) { if (!nodes.has(name)) { const item = element(); item.id = name; nodes.set(name, item); } return nodes.get(name); }
  node("maintenance-action").value = "site.update";
  const document = { createElement: element, getElementById: node };
  const snapshot = overrides.snapshot || { applicationPools: [{ name: "Pool", state: "Stopped" }], sites: [{ name: "Site", state: "Stopped" }] };
  const topology = overrides.topology || { sites: [{ name: "Site", state: "Stopped", bindings: [{ protocol: "http", ipAddress: "*", port: 8080, hostName: "app.example.test" }],
    applications: [{ path: "/", physicalPath: "D:\\Sites\\Root", applicationPoolName: "Pool", virtualDirectories: [{ path: "/", physicalPath: "D:\\Sites\\Root" }, { path: "/assets", physicalPath: "D:\\RootAssets" }] },
      { path: "/api", physicalPath: "D:\\Sites\\Api", applicationPoolName: "Pool", virtualDirectories: [{ path: "/", physicalPath: "D:\\Sites\\Api" }, { path: "/assets", physicalPath: "D:\\ApiAssets" }] }] }] };
  const harness = { requests, timers, sessions, history, node, custom: null, returnedTaskState: "queued" };
  const context = vm.createContext({ document, Date, URLSearchParams, location: { search: overrides.search || "" },
    crypto: { randomUUID() { return "10000000-0000-4000-8000-" + String(uuid++).padStart(12, "0"); } },
    sessionStorage: { getItem: key => sessions.get(key) ?? null, setItem(key, value) { if (overrides.storageFailure) throw new Error("storage"); sessions.set(key, value); }, removeItem: key => sessions.delete(key) },
    localStorage: { getItem: key => history.get(key) ?? null, setItem: (key, value) => history.set(key, value) },
    setTimeout(fn, ms) { const id = ++timer; timers.set(id, { fn, ms }); return id; }, clearTimeout(id) { timers.delete(id); },
    addEventListener() {}, IISManagerAuth: { render() {}, unauthorized() {} },
    async fetch(url, options) {
      requests.push({ url, ...options });
      if (harness.custom) { const custom = await harness.custom(url, options); if (custom) return custom; }
      let status = 200, value;
      if (url === "/api/v1/session") value = { csrfToken: "SYNTHETIC-CSRF", authenticationRequired: false };
      else if (url === "/api/v1/permissions") { status = overrides.permissionStatus || 200; value = status === 200 ? { servers: [{ serverId: "a", read: true, configure: overrides.configure !== false }] } : { code: "PERMISSION_NOT_AVAILABLE" }; }
      else if (url === "/api/v1/servers") value = [{ id: "a", name: "Synthetic server" }];
      else if (url.endsWith("/snapshot")) value = snapshot;
      else if (url.endsWith("/site-topology")) value = topology;
      else if (url.endsWith("/certificates")) value = [];
      else if (url.endsWith("/operations/validate")) value = { valid: true, fingerprint: "F".repeat(64), changes: [{ field: "physicalPath", before: "old", after: "new" }], affectedApplications: ["Site/"], warnings: ["Shared pool impact"], errors: [] };
      else if (url.endsWith("/operations")) { const request = JSON.parse(options.body); value = { serverId: "a", request, state: harness.returnedTaskState }; }
      else if (/\/operations\/[a-f0-9-]+$/i.test(url)) value = { serverId: "a", request: { operationId: url.split("/").at(-1) }, state: harness.returnedTaskState };
      else throw new Error("Unexpected synthetic route");
      return { ok: status >= 200 && status < 300, status, json: async () => value };
    }
  });
  vm.runInContext(source, context); await tick(); await tick(); await tick(); requests.length = 0;
  harness.run = code => vm.runInContext(code, context);
  harness.prepare = async () => {
    node("maintenance-change-physical").checked = true; await node("maintenance-change-physical").emit("change");
    node("maintenance-physical").value = "D:\\Sites\\Updated"; await node("maintenance-physical").emit("input");
    await node("maintenance-form").emit("submit"); node("maintenance-confirm").checked = true; await node("maintenance-confirm").emit("change");
  };
  return harness;
}

test("read-only and legacy permission interfaces permit resource reads but disable mutations", async () => {
  for (const options of [{ configure: false }, { permissionStatus: 404 }]) {
    const harness = await browser(options);
    assert.equal(harness.node("maintenance-precheck").disabled, true);
    assert.equal(harness.node("maintenance-permission").hidden, false);
    await harness.node("maintenance-form").emit("submit");
    assert.equal(harness.requests.filter(request => request.method === "POST").length, 0);
  }
});
test("precheck displays changes and affected applications without submitting a write", async () => {
  const harness = await browser(); await harness.prepare();
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations/validate")).length, 1);
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations")).length, 0);
  assert.equal(harness.node("maintenance-preview").hidden, false); assert.equal(harness.node("maintenance-changes").children.length, 1);
  assert.equal(harness.node("maintenance-affected").children[0].textContent, "Site/");
});
test("editing a field invalidates the reviewed plan and prevents submission", async () => {
  const harness = await browser(); await harness.prepare();
  harness.node("maintenance-physical").value = "D:\\Sites\\Unreviewed"; await harness.node("maintenance-physical").emit("input");
  assert.equal(harness.node("maintenance-preview").hidden, true); assert.equal(harness.node("maintenance-submit").disabled, true);
  await harness.node("maintenance-submit").emit("click");
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations")).length, 0);
});
test("submitting uses the reviewed ID and CSRF then stores only the operation index", async () => {
  const harness = await browser(); await harness.prepare(); await harness.node("maintenance-submit").emit("click");
  const validate = harness.requests.find(request => request.url.endsWith("/operations/validate"));
  const submit = harness.requests.find(request => request.url.endsWith("/operations"));
  assert.equal(JSON.parse(submit.body).operationId, JSON.parse(validate.body).operationId);
  assert.equal(submit.headers["X-IISManager-CSRF"], "SYNTHETIC-CSRF");
  const index = JSON.parse(harness.sessions.get("iis-manager-maintenance-pending-v1"));
  assert.deepEqual(Object.keys(index).sort(), ["operationId", "serverId"]);
  assert.equal(harness.node("maintenance-precheck").disabled, true);
});
test("unknown submission results retain the ID and never automatically re-POST", async () => {
  const harness = await browser(); await harness.prepare();
  harness.custom = async (url) => { if (url.endsWith("/operations")) throw new Error("Lost reply"); };
  await harness.node("maintenance-submit").emit("click"); await harness.node("maintenance-submit").emit("click");
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations")).length, 1);
  assert.ok(harness.sessions.has("iis-manager-maintenance-pending-v1")); assert.equal(harness.node("maintenance-precheck").disabled, true);
  assert.match(harness.node("maintenance-task-status").textContent, /锁定/);
});
test("partial outcomes lock writes while queries stay GET-only", async () => {
  const harness = await browser(); harness.returnedTaskState = "partial"; await harness.prepare();
  await harness.node("maintenance-submit").emit("click"); await harness.node("maintenance-query").emit("click");
  assert.equal(harness.node("maintenance-precheck").disabled, true);
  const queries = harness.requests.filter(request => /\/operations\/[a-f0-9-]+$/i.test(request.url));
  assert.equal(queries.length, 1); assert.equal(queries[0].method, undefined);
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations")).length, 1);
});
test("a durable successful readback unlocks a new maintenance plan", async () => {
  const harness = await browser(); harness.returnedTaskState = "succeeded"; await harness.prepare();
  await harness.node("maintenance-submit").emit("click");
  assert.equal(harness.sessions.has("iis-manager-maintenance-pending-v1"), false);
  assert.equal(harness.node("maintenance-precheck").disabled, false);
});
test("maintenance results display the persisted Agent before and after topology", async () => {
  const before = { applications: [{ path: "/api", applicationPoolName: "OldPool" }] };
  const after = { applications: [{ path: "/api", applicationPoolName: "NewPool" }] };
  for (const fields of [{ agentRecord: { before, after } }, { before, after }]) {
    const harness = await browser(); await harness.prepare();
    harness.custom = async (url, options) => {
      if (!url.endsWith("/operations")) return null;
      const value = { serverId: "a", request: JSON.parse(options.body), state: "succeeded", ...fields };
      return { ok: true, status: 202, json: async () => value };
    };
    await harness.node("maintenance-submit").emit("click");
    const result = JSON.parse(harness.node("maintenance-task-result").textContent);
    assert.deepEqual(result.before, before); assert.deepEqual(result.after, after);
  }
});
test("a restored pending index locks writes until the same original task is queried", async () => {
  const harness = await browser({ pending: { serverId: "a", operationId: id } });
  assert.equal(harness.node("maintenance-precheck").disabled, true);
  harness.returnedTaskState = "succeeded"; await harness.node("maintenance-query").emit("click");
  assert.equal(harness.sessions.has("iis-manager-maintenance-pending-v1"), false);
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 0);
});
test("storage failure blocks submission before any outbound write", async () => {
  const harness = await browser({ storageFailure: true }); await harness.prepare(); await harness.node("maintenance-submit").emit("click");
  assert.equal(harness.requests.filter(request => request.url.endsWith("/operations")).length, 0);
  assert.match(harness.node("notice").textContent, /阻止提交/);
});

test("edit links parse encoded targets and reject ambiguous or incompatible target parameters", () => {
  const query = new URLSearchParams({ server: "a", site: "站点 & one", action: "virtualDirectory.update", application: "/子应用 & api", directory: "/资料 & icons" });
  const target = pure.linkTarget(`?${query}`);
  assert.equal(target.siteName, "站点 & one"); assert.equal(target.applicationPath, "/子应用 & api"); assert.equal(target.virtualDirectoryPath, "/资料 & icons");
  for (const search of ["?server=a&server=b", "?server=a&site=Site&action=other", "?server=a&site=Site&action=application.update&application=/",
    "?server=a&site=Site&action=virtualDirectory.update&application=/api&directory=/assets&pool=1", "?server=a&action=site.update", "?site=Site",
    "?server=a&site=Site&action=site.update&application=/api", "?server=a&site=Site&pool=0"]) assert.throws(() => pure.linkTarget(search));
});

test("application editing opens the exact linked target and pool-only entry preserves unrelated fields", async () => {
  const harness = await browser({ search: "?server=a&site=Site&action=application.update&application=%2Fapi&pool=1" });
  assert.equal(harness.node("maintenance-action").value, "application.update"); assert.equal(harness.node("maintenance-application").value, "/api");
  assert.equal(harness.node("maintenance-physical").value, "D:\\Sites\\Api");
  assert.equal(harness.node("maintenance-change-physical").checked, false); assert.equal(harness.node("maintenance-change-pool").checked, true);
  await harness.node("maintenance-form").emit("submit");
  const request = JSON.parse(harness.requests.find(item => item.url.endsWith("/operations/validate")).body);
  assert.deepEqual(request.target, { siteName: "Site", applicationPath: "/api" }); assert.deepEqual(request.parameters, { applicationPoolName: "Pool" });
  assert.match(harness.node("maintenance-pool-impact").textContent, /全部虚拟目录/);
});

test("ordinary site edit links do not automatically select any field for modification", async () => {
  const harness = await browser({ search: "?server=a&site=Site&action=site.update" });
  assert.equal(harness.node("maintenance-action").value, "site.update");
  assert.equal(harness.node("maintenance-change-physical").checked, false); assert.equal(harness.node("maintenance-change-pool").checked, false);
  assert.equal(harness.node("maintenance-change-bindings").checked, false);
  assert.match(harness.node("maintenance-pool-impact").textContent, /独立子应用保留/);
});

test("directory editing selects the exact owning application and exposes its inherited pool", async () => {
  const harness = await browser({ search: "?server=a&site=Site&action=virtualDirectory.update&application=%2Fapi&directory=%2Fassets" });
  assert.equal(harness.node("maintenance-application").value, "/api"); assert.equal(harness.node("maintenance-directory").value, "/assets");
  assert.equal(harness.node("maintenance-physical").value, "D:\\ApiAssets"); assert.equal(harness.node("maintenance-pool-field").hidden, false);
  assert.equal(harness.node("maintenance-change-pool").checked, false); assert.equal(harness.node("maintenance-change-physical").checked, false);
  assert.equal(harness.node("maintenance-inherited-pool").hidden, false); assert.equal(harness.node("maintenance-inherited-pool-name").textContent, "Pool");
  harness.node("maintenance-change-physical").checked = true; await harness.node("maintenance-change-physical").emit("change");
  await harness.node("maintenance-form").emit("submit");
  const request = JSON.parse(harness.requests.find(item => item.url.endsWith("/operations/validate")).body);
  assert.deepEqual(request.target, { siteName: "Site", applicationPath: "/api", virtualDirectoryPath: "/assets" });
  assert.deepEqual(request.parameters, { physicalPath: "D:\\ApiAssets" });
});

test("directory editing reviews selected path and owner pool together without leaving the editor", async () => {
  for (const application of ["/", "/api"]) {
    for (const changePhysical of [false, true]) {
    const search = new URLSearchParams({ server: "a", site: "Site", action: "virtualDirectory.update", application, directory: "/assets" });
    const harness = await browser({ search: `?${search}` });
    harness.node("maintenance-change-pool").checked = true; await harness.node("maintenance-change-pool").emit("change");
    harness.node("maintenance-change-physical").checked = changePhysical; await harness.node("maintenance-change-physical").emit("change");
    if (changePhysical) harness.node("maintenance-physical").value = "D:\\ChangedAssets";
    assert.equal(harness.node("maintenance-action").value, "virtualDirectory.update");
    assert.match(harness.node("maintenance-pool-impact").textContent, /所属应用.*全部虚拟目录/);
    await harness.node("maintenance-form").emit("submit");
    const validations = harness.requests.filter(item => item.url.endsWith("/operations/validate"));
    assert.equal(validations.length, 1);
    const request = JSON.parse(validations[0].body);
    assert.equal(request.type, "virtualDirectory.update");
    assert.deepEqual(request.target, { siteName: "Site", applicationPath: application, virtualDirectoryPath: "/assets" });
    assert.deepEqual(request.parameters, changePhysical ? { physicalPath: "D:\\ChangedAssets", ownerApplicationPoolName: "Pool" } : { ownerApplicationPoolName: "Pool" });
    harness.node("maintenance-confirm").checked = true; await harness.node("maintenance-confirm").emit("change");
    await harness.node("maintenance-submit").emit("click");
    const writes = harness.requests.filter(item => item.url.endsWith("/operations"));
    assert.equal(writes.length, 1); assert.equal(JSON.parse(writes[0].body).operationId, request.operationId);
    }
  }
});

test("a successful inline edit refreshes the owner pool and selected directory from durable readback", async () => {
  const harness = await browser({ search: "?server=a&site=Site&action=virtualDirectory.update&application=%2Fapi&directory=%2Fassets" });
  harness.node("maintenance-change-pool").checked = true; await harness.node("maintenance-change-pool").emit("change");
  await harness.node("maintenance-form").emit("submit"); harness.node("maintenance-confirm").checked = true;
  const after = { name: "Site", state: "Stopped", bindings: [], applications: [
    { path: "/", physicalPath: "D:\\Sites\\Root", applicationPoolName: "Pool", virtualDirectories: [{ path: "/", physicalPath: "D:\\Sites\\Root" }] },
    { path: "/api", physicalPath: "D:\\Sites\\Api", applicationPoolName: "NewPool", virtualDirectories: [
      { path: "/", physicalPath: "D:\\Sites\\Api" }, { path: "/assets", physicalPath: "D:\\NewAssets" }] }] };
  harness.custom = async (url, options) => url.endsWith("/operations") ? { ok: true, status: 202,
    json: async () => ({ serverId: "a", request: JSON.parse(options.body), state: "succeeded", agentRecord: { after } }) } : null;
  await harness.node("maintenance-submit").emit("click");
  assert.equal(harness.node("maintenance-action").value, "virtualDirectory.update");
  assert.equal(harness.node("maintenance-application").value, "/api"); assert.equal(harness.node("maintenance-directory").value, "/assets");
  assert.equal(harness.node("maintenance-inherited-pool-name").textContent, "NewPool");
  assert.equal(harness.node("maintenance-physical").value, "D:\\NewAssets");
  assert.equal(harness.node("maintenance-change-pool").checked, false); assert.equal(harness.node("maintenance-change-physical").checked, false);
});

test("removed deep-link targets never silently fall back to a different editable resource", async () => {
  for (const search of ["?server=missing&site=Site", "?server=a&site=Missing", "?server=a&site=Site&action=application.update&application=%2Fmissing",
    "?server=a&site=Site&action=virtualDirectory.update&application=%2Fapi&directory=%2Fmissing", "?server=a&site=Site&action=unknown"]) {
    const harness = await browser({ search });
    assert.equal(harness.node("maintenance-precheck").disabled, true); assert.equal(harness.node("notice").hidden, false);
    await harness.prepare(); await harness.node("maintenance-submit").emit("click");
    assert.equal(harness.requests.filter(item => item.method === "POST").length, 0);
  }
});

test("inline owner-pool fields preserve readonly and unresolved operation locks", async () => {
  const search = "?server=a&site=Site&action=virtualDirectory.update&application=%2Fapi&directory=%2Fassets";
  for (const options of [{ configure: false }, { pending: { serverId: "a", operationId: id } }]) {
    const harness = await browser({ search, ...options });
    assert.equal(harness.node("maintenance-change-pool").disabled, true); assert.equal(harness.node("maintenance-pool").disabled, true);
    harness.node("maintenance-change-pool").checked = true;
    await harness.node("maintenance-form").emit("submit"); assert.equal(harness.requests.filter(item => item.method === "POST").length, 0);
  }
});
