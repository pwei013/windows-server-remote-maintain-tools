"use strict";

const assert = require("node:assert/strict");
const { webcrypto } = require("node:crypto");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

const source = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/app.js"), "utf8");
const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

async function browser(browserCrypto) {
  const nodes = new Map(), allNodes = new Set(), storage = new Map();
  const harness = { requests: [], handle: null, annotationHandle: null, timers: 0 };
  const node = (tag = "div") => {
    const listeners = new Map();
    const item = {
      tagName: tag.toUpperCase(), value: "", hidden: false, checked: false, dataset: {}, children: [], attributes: {}, parentNode: null,
      get options() { return this.children.filter(child => child.tagName === "OPTION"); },
      get firstChild() { return this.children[0]; },
      get isConnected() { return this.root || this.parentNode?.isConnected; },
      append(...children) {
        for (const child of children) {
          if (child.parentNode) child.parentNode.children.splice(child.parentNode.children.indexOf(child), 1);
          child.parentNode = this; this.children.push(child);
        }
      },
      prepend(...children) { this.append(...children); this.children.unshift(...this.children.splice(this.children.length - children.length)); },
      insertBefore(child, reference) { this.append(child); this.children.pop(); this.children.splice(this.children.indexOf(reference), 0, child); },
      replaceChildren(...children) { this.children.forEach(child => { child.parentNode = null; }); this.children = []; this.append(...children); },
      remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; },
      addEventListener(type, callback) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(callback); },
      async emit(type, event = {}) { for (const callback of listeners.get(type) || []) await callback({ target: this, preventDefault() {}, ...event }); },
      setAttribute(key, value) { this.attributes[key] = value; },
      showModal() { this.open = true; }, close() { this.open = false; }, focus() { harness.focused = this; }
    };
    allNodes.add(item); return item;
  };
  const document = {
    getElementById(id) {
      const dynamic = [...allNodes].find(item => item.id === id && item.isConnected);
      if (dynamic) return dynamic;
      if (!nodes.has(id)) { const item = node(); item.root = true; nodes.set(id, item); }
      return nodes.get(id);
    },
    createElement: node,
    querySelectorAll: selector => [...allNodes].filter(item => item.isConnected && (selector === "[data-binding]" ? item.dataset.binding : selector === "[data-operation]" ? item.dataset.operation : selector === "[data-maintenance-edit]" ? item.dataset.maintenanceEdit : selector === "[data-server-id]" ? item.dataset.serverId : selector === "[data-pool-state]" ? item.dataset.poolState !== undefined : selector === "[data-site-annotation]" ? item.dataset.siteAnnotation : selector === "[data-annotation-control]" ? item.dataset.annotationControl : false)),
    addEventListener() {}
  };
  const bodyClasses = new Set(); document.body = node("body"); document.body.root = true;
  document.body.classList = { add: name => bodyClasses.add(name), remove: name => bodyClasses.delete(name), contains: name => bodyClasses.has(name) };
  const context = vm.createContext({
    document, crypto: browserCrypto, URL, URLSearchParams, location: { hostname: "console.example.test", search: "" },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    setTimeout() { harness.timers++; return harness.timers; }, clearTimeout() {},
    async fetch(path, options) {
      let data;
      if (path === "/api/v1/session") data = { csrfToken: "test-session" };
      else if (path === "/api/v1/servers") data = [];
      else if (path === "/api/v1/permissions") data = {administration:false,servers:[{serverId:"104",read:true,operate:true,configure:true}]};
      else if (path.endsWith("/site-annotations") && options.body === undefined && !harness.annotationHandle) data = { serverId: decodeURIComponent(path.split("/")[4]), entries: [] };
      else {
        const request = { path, body: options.body ? JSON.parse(options.body) : undefined, method: options.method || "GET", headers: { ...options.headers } };
        harness.requests.push(request);
        const handler = path.endsWith("/site-annotations") && harness.annotationHandle ? harness.annotationHandle : harness.handle;
        assert.ok(handler, "Unexpected API request");
        data = await handler(request);
      }
      const status = data?.httpStatus || 200;
      if (data?.httpStatus) data = data.body;
      return { ok: status >= 200 && status < 300, status, json: async () => data };
    }
  });
  harness.run = code => vm.runInContext(code, context);
  harness.node = id => document.getElementById(id);
  harness.hasBodyClass = name => bodyClasses.has(name);
  harness.run(source);
  await new Promise(resolve => setImmediate(resolve));
  harness.run('state.serverId = "104"; state.draft = { type: "applicationPool.stop", serverId: "104", pool: { name: ".NET v4.5" } };');
  return harness;
}

function fallbackCrypto(fill) {
  const crypto = {
    getRandomValues(bytes) {
      assert.equal(this, crypto, "Web Crypto methods must retain their receiver");
      if (fill === undefined) webcrypto.getRandomValues(bytes);
      else bytes.fill(fill);
      return bytes;
    }
  };
  return crypto;
}

test("Task details display the server-recorded operator as safe text and preserve legacy compatibility", async () => {
  const harness = await browser(fallbackCrypto()), name = "<img src=x onerror=alert(1)> 王工";
  harness.run(`state.task={serverId:"104",request:{operationId:"00000000-0000-4000-8000-000000000011",type:"site.stop",target:{siteName:"Portal"}},state:"succeeded",operatorName:${JSON.stringify(name)},persistenceState:"synced"}; renderTask();`);
  assert.equal(harness.node("task-operator").textContent, name);
  assert.equal(harness.node("task-operator").children.length, 0);
  assert.equal(JSON.parse(harness.node("task-json").textContent).operatorName, name);
  for (const nameValue of [null, "", "   "]) {
    harness.run(`state.task.operatorName=${JSON.stringify(nameValue)}; renderTask();`);
    assert.equal(harness.node("task-operator").textContent, "未记录");
  }
  harness.run("delete state.task.operatorName; renderTask();");
  assert.equal(harness.node("task-operator").textContent, "未记录");
  assert.equal(JSON.parse(harness.node("task-json").textContent).operatorName, null);
});

test("Task details show platform persistence without changing execution state or request identity", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run('state.task={serverId:"104",request:{operationId:"00000000-0000-4000-8000-000000000011",type:"site.stop",target:{siteName:"Portal"}},state:"succeeded",operatorName:"王工"};');
  for (const [state, label] of [["synced", "已保存"], ["pending", "待保存"], ["unknown", "待核对"], ["notConfigured", "未启用"], [null, "未记录"], ["constructor", "未记录"]]) {
    harness.run(`state.task.persistenceState=${JSON.stringify(state)}; renderTask();`);
    assert.equal(harness.node("task-persistence").textContent, label);
    assert.equal(harness.node("task-status").textContent, "执行成功");
    assert.equal(harness.run("state.task.request.operationId"), "00000000-0000-4000-8000-000000000011");
  }
  const request = JSON.parse(harness.run("JSON.stringify(readDraft())"));
  assert.equal(Object.prototype.hasOwnProperty.call(request, "operatorName"), false, "The browser must not submit an operator identity");
  assert.equal(Object.prototype.hasOwnProperty.call(request, "persistenceState"), false);
});

test("Prefer native randomUUID and retain its Crypto receiver", async () => {
  const id = "01234567-89ab-4cde-8f01-23456789abcd";
  const crypto = {
    randomUUID() { assert.equal(this, crypto); return id; },
    getRandomValues() { assert.fail("Native UUID generation should not use the fallback"); }
  };
  const harness = await browser(crypto);
  assert.equal(harness.run("createOperationId()"), id);
});

for (const [fill, expected] of [
  [0, "00000000-0000-4000-8000-000000000000"],
  [255, "ffffffff-ffff-4fff-bfff-ffffffffffff"]
]) {
  test(`Missing randomUUID: UUID v4 format and variant with byte value ${fill}`, async () => {
    const harness = await browser(fallbackCrypto(fill));
    const id = harness.run("createOperationId()");
    assert.equal(id, expected);
    assert.match(id, uuidV4);
  });
}

test("HTTP-compatible fallback produces distinct operation identifiers", async () => {
  const crypto = fallbackCrypto();
  crypto.randomUUID = "unsupported";
  const harness = await browser(crypto);
  const ids = Array.from({ length: 1000 }, () => harness.run("createOperationId()"));
  ids.forEach(id => assert.match(id, uuidV4));
  assert.equal(new Set(ids).size, ids.length);
});

test("All pool runtime actions use UUID identifiers and preserve the target", async () => {
  const harness = await browser(fallbackCrypto());
  for (const type of ["applicationPool.start", "applicationPool.stop", "applicationPool.recycle"]) {
    harness.run(`state.draft.type = ${JSON.stringify(type)};`);
    const request = JSON.parse(harness.run("JSON.stringify(readDraft())"));
    assert.match(request.operationId, uuidV4);
    assert.equal(request.type, type);
    assert.deepEqual(request.target, { applicationPoolName: ".NET v4.5" });
    assert.deepEqual(request.parameters, {});
    assert.equal(request.expectedFingerprint, null);
    assert.ok(Date.parse(request.expiresAt) > Date.now());
  }
});

test("Stop precheck and submission retain the same identifier and fingerprint", async () => {
  const harness = await browser(fallbackCrypto());
  harness.handle = ({ path, body }) => path.endsWith("/validate")
    ? { valid: true, fingerprint: "test-fingerprint", errors: [], warnings: [], changes: [], affectedApplications: [] }
    : { serverId: "104", request: body, state: "succeeded", updatedAt: new Date().toISOString() };
  await harness.run("validate({ preventDefault() {} })");
  assert.equal(harness.requests.length, 1);
  const precheck = harness.requests[0];
  assert.equal(precheck.path, "/api/v1/servers/104/operations/validate");
  assert.match(precheck.body.operationId, uuidV4);
  assert.equal(harness.node("form-error").hidden, true);
  assert.equal(harness.node("submit-operation-button").hidden, false);
  await harness.run("submitOperation()");
  const submitted = harness.requests[1];
  assert.equal(submitted.path, "/api/v1/servers/104/operations");
  assert.deepEqual(submitted.body, { ...precheck.body, expectedFingerprint: "test-fingerprint" });
  assert.equal(harness.run("state.task.request.operationId"), precheck.body.operationId);
  assert.equal(harness.run("readHistory()[0].operationId"), precheck.body.operationId);
});

test("Lost submission response retains the identifier and prevents a second submission", async () => {
  const harness = await browser(fallbackCrypto());
  harness.handle = ({ path }) => {
    if (path.endsWith("/validate")) return { valid: true, fingerprint: "test-fingerprint", errors: [], warnings: [], changes: [], affectedApplications: [] };
    throw new Error("Simulated response loss");
  };
  await harness.run("validate({ preventDefault() {} })");
  const id = harness.requests[0].body.operationId;
  await harness.run("submitOperation()");
  await harness.run("submitOperation()");
  assert.equal(harness.requests.length, 2);
  assert.equal(harness.run("state.task.state"), "unknown");
  assert.equal(harness.run("state.task.request.operationId"), id);
  assert.equal(harness.run("readHistory()[0].operationId"), id);
  assert.equal(harness.timers, 0);
});

test("Missing secure random APIs gives a useful error before any operation request", async () => {
  for (const crypto of [undefined, null, {}]) {
    const harness = await browser(crypto);
    await harness.run("validate({ preventDefault() {} })");
    assert.equal(harness.requests.length, 0);
    assert.equal(harness.node("form-error").hidden, false);
    assert.match(harness.node("form-error").textContent, /无法安全生成操作编号/);
    assert.equal(harness.run("state.busy"), false);
    assert.equal(harness.run("state.validation"), null);
  }
});

const existingPool = name => ({ name, applications: [], state: "Stopped", managedRuntimeVersion: "", managedPipelineMode: "Integrated", identityType: "ApplicationPoolIdentity", enable32BitAppOnWin64: false, startMode: "OnDemand", autoStart: false, idleTimeoutMinutes: 20, idleTimeoutAction: "Terminate" });
const snapshot = names => ({ collectedAt: new Date().toISOString(), applicationPools: names.map(existingPool), sites: [] });
const validPrecheck = { valid: true, fingerprint: "pool-fingerprint", errors: [], warnings: [], changes: [], affectedApplications: [] };

test("Site runtime actions preserve the site target and use empty parameters", async () => {
  const harness = await browser(fallbackCrypto());
  for (const type of ["site.start", "site.stop", "site.restart"]) {
    harness.run(`state.draft = { type: ${JSON.stringify(type)}, serverId: "104", site: {name:"业务门户"} };`);
    const request = JSON.parse(harness.run("JSON.stringify(readDraft())"));
    assert.equal(request.type, type); assert.match(request.operationId, uuidV4);
    assert.deepEqual(request.target, {siteName:"业务门户"}); assert.deepEqual(request.parameters, {});
    assert.equal(request.expectedFingerprint, null);
  }
});

test("Site rows restrict lifecycle actions to stable compatible states", async () => {
  const harness = await browser(fallbackCrypto());
  const sites = ["Started", "Stopped", "Starting", "Stopping", "Unknown"].map((state, index) => ({id:index + 1,name:`Site ${state}`,state,serverAutoStart:false,physicalPath:"D:\\Sites\\Portal",applicationPoolName:"Web.Root",bindings:[]}));
  harness.run(`state.snapshot = ${JSON.stringify({...snapshot(["Web.Root"]),sites})}; renderSnapshot(); updateButtons();`);
  assert.deepEqual(harness.node("sites-body").children.map(row => row.children[4].children.slice(0,3).map(button => button.disabled)), [
    [true,false,false], [false,true,true], [true,true,true], [true,true,true], [true,true,true]
  ]);
  const running = harness.node("sites-body").children[0];
  await running.children[4].children[2].emit("click");
  assert.equal(harness.node("dialog-title").textContent, "重新启动站点");
  assert.equal(harness.run("state.draft.site.name"), "Site Started");
  harness.run('byId("operation-dialog").close(); state.draft=null; state.snapshot.sites[0].state="Stopped";');
  await running.children[4].children[2].emit("click");
  assert.equal(harness.node("operation-dialog").open, false, "Stale row handlers must not open an incompatible operation");
});

test("Site restart precheck and submission retain one operation identifier and its fingerprint", async () => {
  const harness = await browser(fallbackCrypto());
  const site = {id:11,name:"业务门户",state:"Started",serverAutoStart:false,applicationPoolName:"Web.Root",physicalPath:"D:\\Sites\\Portal",bindings:[]};
  harness.run(`state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; openSiteOperation("site.restart",state.snapshot.sites[0]);`);
  harness.handle = ({path,body}) => path.endsWith("/validate") ? {...validPrecheck,fingerprint:"site-fingerprint",affectedApplications:["业务门户/","业务门户/api"],changes:[{field:"state",before:"Started",after:"Started"}],proposed:{...site}}
    : {serverId:"104",request:body,state:"queued",updatedAt:new Date().toISOString()};
  await harness.run("validate({preventDefault(){}})");
  assert.equal(harness.node("submit-operation-button").hidden, false);
  assert.match(harness.node("validation-proposed").textContent, /"state": "Started"/);
  await harness.run("submitOperation()");
  const [precheck,submitted] = harness.requests;
  assert.deepEqual(submitted.body, {...precheck.body,expectedFingerprint:"site-fingerprint"});
  assert.deepEqual(submitted.body.target,{siteName:"业务门户"});
  assert.equal(harness.node("task-target").textContent,"重新启动站点 · 业务门户");
});

test("Site lifecycle actions remain locked while an earlier result needs verification", async () => {
  const harness = await browser(fallbackCrypto());
  const site = {id:11,name:"Portal",state:"Started",applicationPoolName:"Web.Root",bindings:[]};
  harness.run(`state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; state.task={state:"partial"}; renderSnapshot(); updateButtons(); state.draft=null; openSiteOperation("site.stop",state.snapshot.sites[0]);`);
  assert.ok(harness.node("sites-body").children[0].children[4].children.every(button => button.disabled));
  assert.equal(harness.run("state.draft"),null);
});

for (const phase of ["immediate", "polled"]) for (const status of ["succeeded", "partial", "unknown"]) {
  test(`Site ${status} result ${phase} refreshes actual status while preserving its receipt and lock`, async () => {
    const harness = await browser(fallbackCrypto());
    const site = {id:11,name:"Portal",state:"Stopped",serverAutoStart:true,physicalPath:"D:\\Sites\\Portal",applicationPoolName:"Web.Root",bindings:[]};
    const latest = {...snapshot(["Web.Root"]),sites:[{...site,state:"Started"}]};
    harness.run(`state.serverId="104"; state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; openSiteOperation("site.start",state.snapshot.sites[0]);`);
    let submitted;
    const result = state => ({serverId:"104",request:submitted,state,updatedAt:new Date().toISOString(),agentRecord:{after:{...site,state:"Started"}}});
    harness.handle = ({path,body}) => {
      if (path.endsWith("/validate")) return {...validPrecheck,proposed:{...site,state:"Started"}};
      if (path.endsWith("/operations")) { submitted=body; return result(phase === "immediate" ? status : "running"); }
      if (path.includes("/operations/")) return result(status);
      if (path.endsWith("/info")) return {protocolVersion:"1.0",mode:"Windows"};
      if (path.endsWith("/snapshot")) return latest;
      if (path.endsWith("/certificates")) return [];
      if (path.endsWith("/windows-services")) return {services:[],canCreate:false};
      return {sites:[],applicationPools:[]};
    };
    await harness.run("validate({preventDefault(){}})");
    await harness.run("submitOperation()");
    if (phase === "polled") await harness.run("queryTask()");
    assert.equal(harness.run("state.task.state"), status, "Fresh snapshots cannot rewrite the operation receipt");
    assert.equal(harness.run("state.task.request.operationId"), submitted.operationId);
    assert.equal(harness.run("state.snapshot.sites[0].state"), "Started", "The old stopped row must not survive terminal site readback");
    assert.equal(Boolean(harness.run("hasPendingTask()")), status !== "succeeded");
    assert.equal(harness.node("task-lock-notice").hidden, status === "succeeded");
    assert.ok(harness.requests.some(request => request.path.endsWith("/snapshot")));
    assert.equal(harness.requests.filter(request => request.method === "POST").length, 2, "Refreshing uncertain results must not repeat mutations");
  });
}

test("Pending operations explain the lock on every resource tab and require manual verification", async () => {
  const harness = await browser(fallbackCrypto());
  const site = {id:11,name:"Portal",state:"Stopped",applicationPoolName:"Web.Root",bindings:[]};
  for (const status of ["queued", "running", "partial", "unknown"]) {
    harness.run(`state.serverId="104"; state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; state.task={serverId:"104",request:{operationId:"00000000-0000-4000-8000-000000000011",type:"site.stop",target:{siteName:"Portal"}},state:${JSON.stringify(status)}}; renderSnapshot(); renderTask();`);
    for (const resource of ["sites", "pools", "services"]) {
      harness.run(`activateResourceTab(${JSON.stringify(resource)}); updateButtons();`);
      assert.equal(harness.node("task-lock-notice").hidden, false);
      assert.match(harness.node("task-lock-message").textContent, /停止站点.*00000000-0000-4000-8000-000000000011/);
    }
    assert.equal(harness.node("task-lock-release").hidden, !["partial", "unknown"].includes(status));
    assert.ok(harness.node("pools-body").children[0].children[4].children.every(button => button.disabled));
    assert.ok(harness.node("sites-body").children[0].children[4].children.every(button => button.disabled));
    if (["queued", "running"].includes(status)) {
      await harness.node("task-lock-release").emit("click");
      assert.equal(harness.run("state.task.state"), status);
      assert.equal(harness.requests.length, 0, "Active tasks cannot be released or replayed");
    }
  }
});

for (const status of ["partial", "unknown"]) for (const buttonId of ["release-task-button", "task-lock-release"]) {
  test(`Manual verification of ${status} via ${buttonId} preserves history and refreshes without replay`, async () => {
    const harness = await browser(fallbackCrypto());
    const site = {id:11,name:"Portal",state:"Started",applicationPoolName:"Web.Root",bindings:[]};
    const latest = {...snapshot(["Web.Root"]),applicationPools:[{...existingPool("Web.Root"),state:"Started"}],sites:[{...site,state:"Stopped"}]};
    const operationId = "00000000-0000-4000-8000-000000000011";
    harness.run(`state.serverId="104"; state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; state.task={serverId:"104",request:{operationId:${JSON.stringify(operationId)},type:"site.stop",target:{siteName:"Portal"}},state:${JSON.stringify(status)}}; state.validation={valid:true}; globalThis.originalTask=state.task; globalThis.originalDraft=state.draft; renderSnapshot(); renderTask();`);
    harness.handle = ({path}) => path.endsWith("/info") ? {protocolVersion:"1.0",mode:"Windows",machineName:"TEST",osVersion:"TEST",iisVersion:"TEST",agentVersion:"1.0.0"}
      : path.endsWith("/snapshot") ? latest : path.endsWith("/certificates") ? []
      : path.endsWith("/windows-services") ? {services:[],canCreate:false} : {sites:[],applicationPools:[]};
    await harness.node(buttonId).emit("click");
    assert.equal(harness.run("state.task"), null);
    assert.equal(harness.run("originalTask.state"), status, "Acknowledgement cannot rewrite the original outcome");
    assert.equal(harness.run("state.validation"), null, "A new action must obtain a fresh precheck");
    assert.equal(harness.run("state.draft === originalDraft"), true, "Retain existing form drafts when releasing the result");
    assert.ok(JSON.parse(harness.run("JSON.stringify(readHistory())")).some(item => item.operationId === operationId && item.serverId === "104"));
    assert.equal(harness.node("task-lock-notice").hidden, true);
    assert.equal(harness.run("state.snapshot.sites[0].state"), "Stopped");
    assert.deepEqual(harness.node("sites-body").children[0].children[4].children.slice(0,3).map(button => button.disabled), [false,true,true]);
    assert.equal(harness.node("pools-body").children[0].children[4].children[1].disabled, false);
    assert.ok(harness.requests.some(request => request.path.endsWith("/snapshot")));
    assert.ok(harness.requests.every(request => request.method === "GET"), "Releasing the UI lock must not mutate the server or resubmit the old action");
    assert.match(harness.node("notice").textContent, /已刷新/);
  });
}

test("Manual verification leaves actions disabled when a fresh snapshot cannot be read", async () => {
  const harness = await browser(fallbackCrypto());
  const site = {id:11,name:"Portal",state:"Stopped",applicationPoolName:"Web.Root",bindings:[]};
  harness.run(`state.serverId="104"; state.snapshot=${JSON.stringify({...snapshot(["Web.Root"]),sites:[site]})}; state.task={serverId:"104",request:{operationId:"00000000-0000-4000-8000-000000000011",type:"site.stop",target:{siteName:"Portal"}},state:"partial"}; renderSnapshot(); renderTask();`);
  harness.handle = ({path}) => { if (path.endsWith("/snapshot")) throw new Error("Snapshot unavailable"); return path.endsWith("/certificates") ? [] : {protocolVersion:"1.0",services:[]}; };
  await harness.node("task-lock-release").emit("click");
  assert.equal(harness.run("state.task"), null);
  assert.equal(harness.run("state.snapshot"), null);
  assert.equal(harness.node("create-site-button").disabled, true);
  assert.equal(harness.node("pools-body").children.length, 0);
  assert.match(harness.node("notice").textContent, /快照未能刷新/);
  assert.ok(harness.requests.every(request => request.method === "GET"));
});

async function siteBrowser() {
  const harness = await browser(fallbackCrypto());
  harness.run(`state.snapshot = ${JSON.stringify(snapshot(["DefaultAppPool", "Orders.API", "Orders.Worker"]))}; openOperation("site.create");`);
  harness.node("target-name").value = "MyWebsite";
  harness.node("site-path").value = "D:\\Sites\\MyWebsite";
  harness.node("binding-1-host").value = "example.test";
  return harness;
}

function poolApi(harness, initialState = "queued", names = ["DefaultAppPool", "Orders.API", "Orders.Worker", "NewPool"]) {
  let request;
  harness.handle = ({ path, body }) => {
    if (path.endsWith("/validate")) return validPrecheck;
    if (path.endsWith("/operations")) { request = body; return { serverId: "104", request, state: initialState, updatedAt: new Date().toISOString() }; }
    if (path.includes("/operations/")) return { serverId: "104", request, state: "succeeded", updatedAt: new Date().toISOString() };
    if (path.endsWith("/info")) return { protocolVersion: "1.0", mode: "Simulation" };
    if (path.endsWith("/snapshot")) return snapshot(names);
    if (path.endsWith("/certificates")) return [];
    assert.fail(`Unexpected path ${path}`);
  };
}

async function submitSitePool(harness) {
  harness.run("openSitePoolDialog()");
  harness.node("inline-pool-target-name").value = "NewPool";
  await harness.run("validate({ preventDefault() {} })");
  await harness.run("submitOperation()");
}

test("Certificate selector hides expired, boundary, invalid dates and certificates without private keys", async () => {
  const harness = await browser(fallbackCrypto());
  const now = Date.parse("2026-10-05T00:00:00Z");
  harness.run(`Date.now = () => ${now}; state.certificates = ${JSON.stringify([
    { thumbprint: "expired", notAfter: new Date(now - 1).toISOString(), hasPrivateKey: true },
    { thumbprint: "boundary", notAfter: new Date(now).toISOString(), hasPrivateKey: true },
    { thumbprint: "valid", notAfter: new Date(now + 1).toISOString(), hasPrivateKey: true, friendlyName: "Valid certificate" },
    { thumbprint: "no-key", notAfter: new Date(now + 1000).toISOString(), hasPrivateKey: false },
    { thumbprint: "invalid-date", notAfter: "invalid", hasPrivateKey: true }
  ])}; addBinding();`);
  assert.deepEqual(harness.node("binding-1-cert").options.map(option => option.value), ["", "valid"]);
});

test("Site application pool filter supports case-insensitive substrings and an empty result", async () => {
  const harness = await siteBrowser();
  const search = harness.node("create-site-pool-search"), selector = harness.node("create-site-pool");
  search.value = "  oRdErS  "; await search.emit("input");
  assert.deepEqual(selector.options.map(option => option.value), ["", "Orders.API", "Orders.Worker"]);
  assert.equal(selector.value, "");
  selector.value = "Orders.API"; search.value = "api"; await search.emit("input");
  assert.deepEqual(selector.options.map(option => option.value), ["", "Orders.API"]);
  assert.equal(selector.value, "Orders.API");
  search.value = "missing"; await search.emit("input");
  assert.deepEqual(selector.options.map(option => option.value), [""]);
  assert.match(selector.options[0].textContent, /没有匹配/);
  assert.equal(selector.value, "");
  search.value = ""; await search.emit("input");
  assert.equal(selector.options.length, 4);
});

test("Canceling nested pool dialog preserves the site fields, selection, bindings and filter", async () => {
  const harness = await siteBrowser();
  harness.node("create-site-pool").value = "Orders.API";
  harness.node("create-site-pool-search").value = "Orders";
  harness.run("const savedSiteDraft = state.draft; state.validation = { valid: true }; openSitePoolDialog();");
  assert.equal(harness.node("operation-dialog").open, true);
  assert.equal(harness.node("inline-pool-operation-dialog").open, true);
  assert.equal(harness.run("readDraft().type"), "applicationPool.create");
  harness.run("closeSitePoolDialog()");
  assert.equal(harness.run("state.draft === savedSiteDraft"), true);
  assert.equal(harness.node("inline-pool-operation-dialog").open, false);
  assert.equal(harness.node("operation-dialog").open, true);
  assert.equal(harness.node("target-name").value, "MyWebsite");
  assert.equal(harness.node("site-path").value, "D:\\Sites\\MyWebsite");
  assert.equal(harness.node("binding-1-host").value, "example.test");
  assert.equal(harness.node("create-site-pool").value, "Orders.API");
  assert.equal(harness.node("create-site-pool-search").value, "Orders");
  assert.equal(harness.run("state.validation"), null);
});

test("Queued pool creation selects only after successful task and snapshot readback", async () => {
  const harness = await siteBrowser(); poolApi(harness);
  harness.node("create-site-pool-search").value = "Orders";
  await submitSitePool(harness);
  assert.equal(harness.requests[0].body.type, "applicationPool.create");
  assert.equal(harness.requests[0].body.target.applicationPoolName, "NewPool");
  assert.equal(harness.requests[1].body.operationId, harness.requests[0].body.operationId);
  assert.equal(harness.requests[1].body.expectedFingerprint, "pool-fingerprint");
  assert.equal(harness.node("create-site-pool").value, "DefaultAppPool");
  assert.equal(harness.node("validate-button").disabled, true);
  assert.equal(harness.node("create-site-pool-button").disabled, true);
  assert.equal(harness.node("create-site-pool-query").hidden, false);
  const before = harness.requests.length;
  await harness.run("validate({ preventDefault() {} })");
  assert.equal(harness.requests.length, before, "A site must not precheck while pool creation is pending");
  await harness.run("queryTask()");
  assert.equal(harness.node("create-site-pool").value, "NewPool");
  assert.equal(harness.node("create-site-pool-search").value, "");
  assert.equal(harness.node("target-name").value, "MyWebsite");
  assert.equal(harness.node("site-path").value, "D:\\Sites\\MyWebsite");
  assert.equal(harness.node("binding-1-host").value, "example.test");
  assert.equal(harness.node("validate-button").disabled, false);
  assert.equal(harness.node("create-site-pool-query").hidden, true);
  assert.equal(harness.run("readDraft().parameters.applicationPoolName"), "NewPool");
  assert.equal(harness.run("readDraft().target.siteName"), "MyWebsite");
});

test("Immediately succeeded pool creation also refreshes and selects the new pool", async () => {
  const harness = await siteBrowser(); poolApi(harness, "succeeded");
  await submitSitePool(harness);
  assert.equal(harness.node("create-site-pool").value, "NewPool");
  assert.equal(harness.node("operation-dialog").open, true);
  assert.equal(harness.run("state.siteDraft"), null);
});

test("A failed nested precheck cannot submit or alter the site selection", async () => {
  const harness = await siteBrowser();
  harness.handle = () => ({ ...validPrecheck, valid: false, errors: [{ code: "pool.exists", message: "应用程序池已存在" }] });
  harness.run("openSitePoolDialog()"); harness.node("inline-pool-target-name").value = "DefaultAppPool";
  await harness.run("validate({ preventDefault() {} })"); await harness.run("submitOperation()");
  assert.equal(harness.requests.length, 1);
  assert.equal(harness.node("inline-pool-submit-operation-button").hidden, true);
  assert.equal(harness.node("create-site-pool").value, "DefaultAppPool");
});

for (const result of ["failed", "partial", "unknown", "expired"]) {
  test(`Pool result ${result} never auto-selects and retains site configuration`, async () => {
    const harness = await siteBrowser(); poolApi(harness, result);
    await submitSitePool(harness);
    assert.equal(harness.node("create-site-pool").value, "DefaultAppPool");
    assert.equal(harness.node("target-name").value, "MyWebsite");
    assert.equal(harness.node("validate-button").disabled, ["partial", "unknown"].includes(result));
    assert.equal(harness.requests.filter(request => request.path.endsWith("/snapshot")).length, 0);
  });
}

test("Lost pool response preserves the operation ID and selects only after querying that ID", async () => {
  const harness = await siteBrowser(); poolApi(harness);
  const handle = harness.handle;
  harness.handle = request => {
    const result = handle(request);
    if (request.path.endsWith("/operations")) throw new Error("Simulated response loss");
    return result;
  };
  await submitSitePool(harness);
  const id = harness.requests[0].body.operationId;
  assert.equal(harness.run("state.task.state"), "unknown");
  assert.equal(harness.node("create-site-pool").value, "DefaultAppPool");
  await harness.run("submitOperation()");
  assert.equal(harness.requests.length, 2);
  await harness.run("queryTask()");
  assert.ok(harness.requests.some(request => request.path.endsWith(`/operations/${id}`)));
  assert.equal(harness.node("create-site-pool").value, "NewPool");
});

test("Success without a pool in the refreshed snapshot waits for independent confirmation", async () => {
  const harness = await siteBrowser(); poolApi(harness, "succeeded", ["DefaultAppPool"]);
  await submitSitePool(harness);
  assert.equal(harness.node("create-site-pool").value, "DefaultAppPool");
  assert.match(harness.node("create-site-pool-status").textContent, /尚未从最新快照确认/);
  assert.equal(harness.node("create-site-pool-query").hidden, false);
  const handle = harness.handle;
  harness.handle = request => request.path.endsWith("/snapshot") ? snapshot(["DefaultAppPool", "NewPool"]) : handle(request);
  await harness.run("queryTask()");
  assert.equal(harness.node("create-site-pool").value, "NewPool");
});

test("An old creation result cannot change a newly opened site draft", async () => {
  const harness = await siteBrowser(); poolApi(harness);
  await submitSitePool(harness);
  harness.run("state.task.state = 'succeeded'; openOperation('site.create');");
  harness.node("create-site-pool").value = "Orders.API";
  await harness.run("queryTask()");
  assert.equal(harness.node("create-site-pool").value, "Orders.API");
});

test("An empty application pool list still allows nested creation and successful selection", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run(`state.snapshot = ${JSON.stringify(snapshot([]))}; openOperation("site.create");`);
  assert.equal(harness.node("create-site-pool").value, "");
  assert.match(harness.node("create-site-pool").options[0].textContent, /暂无应用程序池/);
  assert.equal(harness.node("create-site-pool-button").disabled, false);
  poolApi(harness, "succeeded", ["NewPool"]);
  await submitSitePool(harness);
  assert.equal(harness.node("create-site-pool").value, "NewPool");
});

test("Unavailable snapshot cannot open a nested dialog or replace the current site draft", async () => {
  const harness = await siteBrowser();
  harness.run("const savedSiteDraft = state.draft; state.snapshot = null; updateButtons(); openSitePoolDialog();");
  assert.equal(harness.node("create-site-pool-button").disabled, true);
  assert.equal(harness.run("state.siteDraft"), null);
  assert.equal(harness.run("state.draft === savedSiteDraft"), true);
});

const existingSite = (name, applicationPoolName, bindings = []) => ({ name, id: 1, state: "Started", serverAutoStart: true, physicalPath: `D:\\Sites\\${name}`, applicationPoolName, bindings });

test("Pool site navigation includes root and child applications, counts each site once and clears old filters", async () => {
  const harness = await browser(fallbackCrypto());
  const pools = [existingPool("Web.Root"), { ...existingPool("Shared.Worker"), applications: ["Portal/api", "Portal/jobs", "Portal2/task", "Case SITE/API"] }, existingPool("Idle")];
  const sites = [existingSite("Portal", "Web.Root"), existingSite("Portal2", "Web.Root"), existingSite("Case Site", "Web.Root"), existingSite("PortalExtra", "Web.Root"), existingSite("RootOnly", "shared.worker")];
  harness.run(`state.snapshot = ${JSON.stringify({ applicationPools: pools, sites })}; renderSnapshot();`);
  const buttons = harness.node("pools-body").children.map(row => row.children[0].children[2]);
  assert.equal(buttons[1].textContent, "关联站点（4）");
  assert.equal(buttons[2].textContent, "关联站点（0）");
  assert.equal(buttons[2].disabled, true);
  for (const [id, value] of [["site-search", "missing"], ["site-state", "Stopped"], ["site-protocol", "https"], ["site-port", "invalid"]]) harness.node(id).value = value;
  await buttons[1].emit("click");
  assert.equal(harness.node("site-pool").value, "Shared.Worker");
  for (const id of ["site-search", "site-state", "site-protocol", "site-port"]) assert.equal(harness.node(id).value, "");
  assert.equal(harness.node("sites-panel").hidden, false);
  assert.equal(harness.node("pools-panel").hidden, true);
  assert.equal(harness.node("sites-tab").attributes["aria-selected"], "true");
  assert.equal(harness.focused, harness.node("sites-panel"));
  assert.deepEqual(harness.node("sites-body").children.map(row => row.children[0].children[0].textContent), ["Portal", "Portal2", "Case Site", "RootOnly"]);
  assert.equal(harness.node("sites-body").children[0].children[2].children[2].textContent, "子应用使用 Shared.Worker");
  assert.equal(harness.requests.length, 0, "Viewing associations must not submit IIS operations");
  await harness.node("site-reset").emit("click");
  assert.equal(harness.node("sites-body").children.length, sites.length);
});

test("Binding addresses prefer hostnames, use the managed host for wildcard IPs and preserve protocols and ports", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run('state.servers = [{ id: "104", hostName: "10.10.10.5" }]; state.info = { mode: "Windows", machineName: "IIS-SERVER" };');
  const cases = [
    [{ protocol: "http", ipAddress: "10.0.0.9", port: 80, hostName: "site.example.test" }, "http://site.example.test/"],
    [{ protocol: "https", ipAddress: "*", port: 443, hostName: "site.example.test" }, "https://site.example.test/"],
    [{ protocol: "HTTPS", ipAddress: "*", port: 8443, hostName: "site.example.test" }, "https://site.example.test:8443/"],
    [{ protocol: "http", ipAddress: "10.0.0.9", port: 8080, hostName: "" }, "http://10.0.0.9:8080/"],
    [{ protocol: "https", ipAddress: "2001:db8::5", port: 8443, hostName: "" }, "https://[2001:db8::5]:8443/"],
    [{ protocol: "http", ipAddress: "[2001:db8::5]", port: 80, hostName: "" }, "http://[2001:db8::5]/"],
    ...["*", "", "0.0.0.0", "::", "[::]", "0:0:0:0:0:0:0:0"].map(ipAddress => [{ protocol: "http", ipAddress, port: 8080, hostName: "" }, "http://10.10.10.5:8080/"])
  ];
  for (const [binding, expected] of cases) assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), expected);
});

test("Wildcard addresses use the current server and avoid a Console-local Agent address for remote operators", async () => {
  const harness = await browser(fallbackCrypto());
  const binding = { protocol: "http", ipAddress: "*", port: 80, hostName: "" };
  harness.run('state.servers = [{ id: "104", hostName: "127.0.0.1" }, { id: "second", hostName: "second.example.test" }]; state.info = { mode: "Windows", machineName: "IIS-SERVER" };');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), "http://iis-server/");
  for (const hostName of ["localhost.", "[::1]", "127.0.0.2"]) {
    harness.run(`state.servers[0].hostName = ${JSON.stringify(hostName)};`);
    assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), "http://iis-server/");
  }
  harness.run('state.servers[0].hostName = "127.example.test";');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), "http://127.example.test/");
  harness.run('selectServer("second")');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), "http://second.example.test/");
  harness.run('selectServer("missing")');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(binding)})`), "");
});

test("Non-web protocols, invalid ports, wildcard hostnames and URL fragments never become clickable destinations", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run('state.servers = [{ id: "104", hostName: "10.10.10.5" }];');
  const binding = { protocol: "http", ipAddress: "*", port: 80, hostName: "site.example.test" };
  const cases = [
    ...["ftp", "net.tcp", "javascript"].map(protocol => ({ ...binding, protocol })),
    ...[0, -1, 65536, 80.5, "invalid"].map(port => ({ ...binding, port })),
    ...["*", "*.example.test", "https://example.test", "example.test/path", "example.test?x=1", "example.test#fragment", "user@example.test", "example.test:8080", "[::1]:8080", "example.test\\path", "bad host"].map(hostName => ({ ...binding, hostName }))
  ];
  for (const value of cases) assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(value)})`), "", JSON.stringify(value));
});

test("Real loopback bindings are marked local-only and link only when the Console and Agent are accessed locally", async () => {
  const harness = await browser(fallbackCrypto());
  const bindings = [
    { protocol: "http", ipAddress: "127.0.0.1", port: 80, hostName: "" },
    { protocol: "http", ipAddress: "::1", port: 8080, hostName: "" },
    { protocol: "http", ipAddress: "*", port: 80, hostName: "localhost" }
  ];
  harness.run(`state.servers = [{ id: "104", hostName: "10.10.10.5" }]; state.info = { mode: "Windows", machineName: "IIS-SERVER" }; state.snapshot = { applicationPools: [], sites: [${JSON.stringify(existingSite("Portal", "Web.Root", bindings))}] }; renderSnapshot();`);
  const lines = harness.node("sites-body").children[0].children[3].children;
  for (const line of lines) {
    assert.equal(line.children[1].tagName, "SPAN");
    assert.equal(line.children[2].textContent, "仅服务器本机访问");
  }
  harness.run('location.hostname = "127.0.0.1";');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(bindings[0])})`), "", "A local Console must not open loopback bindings on a remote Agent");
  harness.run('state.servers[0].hostName = "127.0.0.1";');
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(bindings[0])})`), "http://127.0.0.1/");
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(bindings[1])})`), "http://[::1]:8080/");
  assert.equal(harness.run(`siteBindingUrl(${JSON.stringify(bindings[2])})`), "http://localhost/");
});

test("Every usable binding renders a named new-tab link while unsupported bindings retain their configuration", async () => {
  const harness = await browser(fallbackCrypto());
  const bindings = [
    { protocol: "https", ipAddress: "*", port: 443, hostName: "site.example.test", certificateThumbprint: "test-certificate", useSni: true },
    { protocol: "http", ipAddress: "10.0.0.9", port: 8080, hostName: "" },
    { protocol: "net.tcp", ipAddress: "*", port: 0, hostName: "" }
  ];
  harness.run(`state.snapshot = { applicationPools: [], sites: [${JSON.stringify(existingSite("Portal", "Web.Root", bindings))}] }; renderSnapshot();`);
  const lines = harness.node("sites-body").children[0].children[3].children;
  assert.equal(lines.length, 3);
  for (const [index, url] of [[0, "https://site.example.test/"], [1, "http://10.0.0.9:8080/"]]) {
    const link = lines[index].children[1];
    assert.equal(link.tagName, "A"); assert.equal(link.href, url); assert.equal(link.textContent, url);
    assert.equal(link.target, "_blank"); assert.equal(link.rel, "noopener noreferrer");
    assert.match(link.attributes["aria-label"], /在新标签页打开 Portal/);
  }
  assert.ok(descendants(lines[0]).some(node => /test-certificate.*SNI/.test(node.textContent || "")), "Complete TLS metadata remains available inside binding details");
  assert.equal(lines[2].children[1].tagName, "SPAN");
  assert.equal(lines[2].children[1].textContent, "*:0 （无主机名）");
  assert.equal(harness.requests.length, 0);
});

const descendants = item => [item, ...item.children.flatMap(descendants)];
const nodesWithTag = (item, tag) => descendants(item).filter(child => child.tagName === tag);
const application = (path, pool, directories = []) => ({ path, applicationPoolName: pool, physicalPath: `D:\\Apps${path.replaceAll("/", "\\")}`, enabledProtocols: "http,net.tcp", virtualDirectories: [{ path: "/", physicalPath: `D:\\Apps${path.replaceAll("/", "\\")}` }, ...directories] });
const topologyFixture = () => ({ collectedAt: "2026-10-06T08:00:00Z", applicationPools: [existingPool("Web.Root"), existingPool("Api.Pool")], sites: [
  { id: 11, name: "Portal", state: "Started", bindings: [{ protocol: "https", ipAddress: "*", port: 443, hostName: "portal.example.test" }], applications: [application("/", "Web.Root"), application("/api", "Api.Pool", [{ path: "/assets", physicalPath: "D:\\Shared\\Assets" }]), application("/api/v2", "Api.Pool")] },
  { id: 12, name: "Portal.Other", state: "Stopped", bindings: [{ protocol: "http", ipAddress: "2001:db8::5", port: 8080, hostName: "" }], applications: [application("/", "Web.Root"), application("/jobs", "Api.Pool", [{ path: "/logs", physicalPath: "D:\\Shared\\Logs" }])] }
] });
async function topologyBrowser() {
  const harness = await browser(fallbackCrypto()), topology = topologyFixture();
  const legacy = { collectedAt: topology.collectedAt, applicationPools: topology.applicationPools.map(pool => ({ ...pool, applications: topology.sites.flatMap(site => site.applications.filter(app => app.applicationPoolName === pool.name).map(app => site.name + app.path)) })), sites: topology.sites.map(site => ({ ...existingSite(site.name, "Web.Root", site.bindings), id: site.id })) };
  harness.run(`state.snapshot = ${JSON.stringify(legacy)}; state.topology = ${JSON.stringify(topology)}; state.topologyStatus = "ready"; renderSnapshot();`);
  return harness;
}

test("Overview navigation selects the requested site and expands its applications without sending an operation", async () => {
  const harness = await topologyBrowser(), name = harness.run("state.snapshot.sites[0].name");
  harness.run(`focusOverviewSite(${JSON.stringify(name)}, "/api")`);
  assert.equal(harness.node("site-search").value, name);
  assert.equal(harness.node("sites-panel").hidden, false);
  assert.equal(harness.run("state.expandedSites.has(siteExpansionKey(state.snapshot.sites[0]))"), true);
  assert.equal(harness.requests.length, 0);
});

test("Overview navigation reports a removed site and never submits an operation", async () => {
  const harness = await topologyBrowser();
  harness.run('focusOverviewSite("Removed.From.IIS", "/api")');
  assert.equal(harness.node("site-search").value, "Removed.From.IIS");
  assert.match(harness.node("notice").textContent, /未出现在当前快照/);
  assert.equal(harness.requests.length, 0);
});

test("Application URLs include application and directory paths, encode each segment and retain HTTPS and IPv6 ports", async () => {
  const harness = await browser(fallbackCrypto()), binding = { protocol: "https", port: 443, hostName: "portal.example.test", ipAddress: "*" };
  for (const [path, directoryPath, expected] of [
    ["/", "/", "https://portal.example.test/"],
    ["/api", "/", "https://portal.example.test/api/"],
    ["/api/v2", "/assets", "https://portal.example.test/api/v2/assets/"],
    ["/应用 一", "/文件#?%", "https://portal.example.test/%E5%BA%94%E7%94%A8%20%E4%B8%80/%E6%96%87%E4%BB%B6%23%3F%25/"]
  ]) assert.equal(harness.run(`applicationUrl(${JSON.stringify(binding)}, ${JSON.stringify(application(path, "Api.Pool"))}, ${JSON.stringify(directoryPath)})`), expected);
  assert.equal(harness.run(`applicationUrl({protocol:"http",port:8080,hostName:"",ipAddress:"2001:db8::5"}, ${JSON.stringify(application("/jobs", "Api.Pool"))})`), "http://[2001:db8::5]:8080/jobs/");
  for (const path of ["//other.example", "/api//jobs", "/../api", "/api/..", "/api\\jobs"]) assert.equal(harness.run(`resourcePath(${JSON.stringify(path)})`), "");
  assert.equal(harness.run(`applicationUrl(${JSON.stringify(binding)}, {path:"/tcp",enabledProtocols:"net.tcp"})`), "");
});

test("Pool association drawer groups exact applications by site and counts directories through their owning application", async () => {
  const harness = await topologyBrowser();
  const view = harness.node("pools-body").children[1].children[0].children[3]; await view.emit("click");
  assert.equal(harness.node("resource-dialog").open, true);
  assert.equal(harness.node("resource-summary").textContent, "2 个站点 · 3 个应用 · 2 个附加虚拟目录");
  assert.deepEqual(nodesWithTag(harness.node("resource-tree"), "H4").map(node => node.textContent), ["api（/api）", "v2（/api/v2）", "jobs（/jobs）"]);
  const links = nodesWithTag(harness.node("resource-tree"), "A");
  assert.deepEqual(links.map(link => link.href), ["https://portal.example.test/api/", "https://portal.example.test/api/assets/", "https://portal.example.test/api/v2/", "http://[2001:db8::5]:8080/jobs/", "http://[2001:db8::5]:8080/jobs/logs/"]);
  links.forEach(link => { assert.equal(link.target, "_blank"); assert.equal(link.rel, "noopener noreferrer"); assert.match(link.attributes["aria-label"], /api|v2|jobs/); });
  assert.ok(descendants(harness.node("resource-tree")).some(node => node.textContent?.includes("所属应用：api（/api） · 随应用使用 Api.Pool")));
  assert.equal(harness.requests.length, 0);
});

test("Site drawer scopes by site ID, includes the root once, supports directory search and links back to the pool", async () => {
  const harness = await topologyBrowser();
  harness.run('openResourceDialog({siteId:11})');
  assert.equal(harness.node("resource-summary").textContent, "1 个站点 · 3 个应用（根应用 1 个） · 1 个附加虚拟目录");
  assert.equal(nodesWithTag(harness.node("resource-tree"), "H4").filter(node => node.textContent === "根应用（/）").length, 1);
  harness.node("resource-search").value = "shared\\assets"; await harness.node("resource-search").emit("input");
  assert.deepEqual(nodesWithTag(harness.node("resource-tree"), "H4").map(node => node.textContent), ["api（/api）"]);
  const poolButton = nodesWithTag(harness.node("resource-tree"), "BUTTON").find(button => button.textContent === "Api.Pool"); await poolButton.emit("click");
  assert.equal(harness.node("resource-dialog").open, false);
  assert.equal(harness.node("pool-search").value, "Api.Pool");
  assert.equal(harness.node("pools-panel").hidden, false);
  assert.equal(harness.focused.dataset.poolName, "Api.Pool");
});

test("Site pool filtering and binding preview use the matching application instead of the site's root application", async () => {
  const harness = await topologyBrowser();
  harness.node("site-pool").value = "Api.Pool"; await harness.node("site-pool").emit("change");
  const row = harness.node("sites-body").children[0], links = nodesWithTag(row.children[3], "A");
  assert.deepEqual(links.map(link => link.href), ["https://portal.example.test/api/"]);
  const inspect = row.children[0].children.find(button => button.id?.startsWith("site-details-toggle-")); await inspect.emit("click");
  assert.equal(harness.run("state.resourceContext"), null, "Site expansion must stay in the table");
  const expanded = harness.node("sites-body").children.find(row => row.dataset.siteDetailsId === "11");
  assert.deepEqual(nodesWithTag(expanded, "H3").map(node => node.textContent), ["应用程序（2）", "附加虚拟目录（1）"]);
  assert.deepEqual(nodesWithTag(expanded, "TBODY")[0].children.map(row => row.dataset.applicationPath), ["/api", "/api/v2"]);
  assert.ok(descendants(expanded).some(node => node.textContent === "仅显示使用 Api.Pool 的应用及其附加虚拟目录。"));
  harness.node("site-search").value = "shared\\logs"; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), ["Portal.Other"]);
  assert.equal(harness.node("site-details-toggle-12").attributes["aria-expanded"], "true");
});

test("Expanding a site omits the root application while preserving directory ownership", async () => {
  const harness = await topologyBrowser();
  await harness.node("site-details-toggle-11").emit("click");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-controls"], "site-details-11");
  assert.equal(harness.node("sites-body").children.length, 3);
  const detail = harness.node("site-details-11"), tables = nodesWithTag(detail, "TABLE"), bodies = nodesWithTag(detail, "TBODY");
  assert.equal(detail.children[0].colSpan, 5); assert.equal(tables.length, 2);
  assert.deepEqual(nodesWithTag(detail, "H3").map(node => node.textContent), ["应用程序（2）", "附加虚拟目录（1）"]);
  assert.deepEqual(bodies[0].children.map(row => row.dataset.applicationPath), ["/api", "/api/v2"]);
  assert.equal(bodies[0].children.filter(row => row.dataset.applicationPath === "/").length, 0);
  assert.equal(harness.run('state.topology.sites[0].applications.length'), 3, "The complete topology still includes the root application.");
  const directory = bodies[1].children[0];
  assert.equal(directory.dataset.applicationPath, "/api"); assert.equal(directory.dataset.directoryPath, "/assets");
  assert.equal(directory.children[1].textContent, "/api"); assert.equal(directory.children[2].children[0].textContent, "Api.Pool");
  assert.equal(directory.children[3].textContent, "D:\\Shared\\Assets");
  assert.equal(nodesWithTag(directory, "A")[0].href, "https://portal.example.test/api/assets/");
  nodesWithTag(detail, "A").forEach(link => { assert.equal(link.target, "_blank"); assert.equal(link.rel, "noopener noreferrer"); });
  assert.ok(!nodesWithTag(detail, "BUTTON").some(button => button.dataset.operation), "Applications and directories do not receive site lifecycle controls");
  await harness.node("site-details-toggle-11").emit("click");
  assert.equal(harness.node("sites-body").children.length, 2); assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "false");
  assert.equal(harness.focused.id, "site-details-toggle-11"); assert.equal(harness.requests.length, 0);
});

test("Searching an application path expands its owning site and filters unrelated child applications", async () => {
  const harness = await topologyBrowser();
  harness.node("site-search").value = "  V2  "; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "TBODY")[0].children.map(row => row.dataset.applicationPath), ["/api/v2"]);
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "H3").map(node => node.textContent), ["应用程序（1）"]);
  assert.equal(harness.requests.length, 0);
});

test("Virtual directory searches include complete application-relative paths and reveal the exact owner", async () => {
  const harness = await topologyBrowser();
  for (const query of ["/api/assets", "  ASSETS  ", "shared\\assets"]) {
    harness.node("site-search").value = query; await harness.node("site-search").emit("input");
    assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
    assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
    const detail = harness.node("site-details-11");
    assert.deepEqual(nodesWithTag(detail, "H3").map(node => node.textContent), ["附加虚拟目录（1）"]);
    const rows = nodesWithTag(detail, "TBODY")[0].children;
    assert.deepEqual(rows.map(row => [row.dataset.applicationPath, row.dataset.directoryPath]), [["/api", "/assets"]]);
    assert.equal(nodesWithTag(rows[0], "A")[0].href, "https://portal.example.test/api/assets/");
  }
});

test("Application physical directories are searchable and scoped to the selected application pool", async () => {
  const harness = await topologyBrowser();
  harness.node("site-search").value = "apps\\api\\v2"; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "TBODY")[0].children.map(row => row.dataset.applicationPath), ["/api/v2"]);
  harness.node("site-pool").value = "Web.Root"; await harness.node("site-pool").emit("change");
  assert.deepEqual(visibleSiteNames(harness), []);
  harness.node("site-pool").value = "Api.Pool"; await harness.node("site-pool").emit("change");
  assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
});

test("Root-application virtual directories can match without showing a root application row", async () => {
  const harness = await topologyBrowser();
  harness.run(`state.topology.sites[0].applications=[${JSON.stringify(application("/", "Web.Root", [{path:"/files",physicalPath:"D:\\Shared\\Files"}]))}];`);
  harness.node("site-search").value = "/files"; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
  const detail = harness.node("site-details-11");
  assert.deepEqual(nodesWithTag(detail, "H3").map(node => node.textContent), ["附加虚拟目录（1）"]);
  assert.equal(nodesWithTag(detail, "TBODY")[0].children[0].dataset.applicationPath, "/");
});

test("A matching topology arriving after search reveals results and clearing search restores all children", async () => {
  const harness = await topologyBrowser(), topology = topologyFixture();
  harness.run('state.topology=null;state.topologyStatus="loading";');
  harness.node("site-search").value = "/api/assets"; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), []);
  harness.handle = () => topology; await harness.run("refreshTopology()");
  assert.deepEqual(visibleSiteNames(harness), ["Portal"]);
  assert.equal(nodesWithTag(harness.node("site-details-11"), "TBODY")[0].children[0].dataset.directoryPath, "/assets");
  harness.node("site-search").value = ""; await harness.node("site-search").emit("input");
  assert.deepEqual(visibleSiteNames(harness), ["Portal", "Portal.Other"]);
  await harness.node("site-details-toggle-11").emit("click");
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "H3").map(node => node.textContent), ["应用程序（2）", "附加虚拟目录（1）"]);
});

test("Search-expanded results can be collapsed and the next search expands matching resources again", async () => {
  const harness = await topologyBrowser();
  harness.node("site-search").value = "/api/assets"; await harness.node("site-search").emit("input");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  await harness.node("site-details-toggle-11").emit("click");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "false");
  assert.equal(harness.node("sites-body").children.length, 1);
  harness.run("renderSnapshot()"); assert.equal(harness.node("sites-body").children.length, 1);
  harness.node("site-search").value = "assets"; await harness.node("site-search").emit("input");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  assert.equal(harness.requests.length, 0);
});

test("A site-name match keeps all child resources available and directory-only results retain exact ownership", async () => {
  const harness = await topologyBrowser();
  harness.run(`state.topology.sites[0].applications[2].virtualDirectories.push({path:"/assets",physicalPath:"D:\\\\OtherAssets"});`);
  harness.node("site-search").value = "Portal"; await harness.node("site-search").emit("input");
  await harness.node("site-details-toggle-11").emit("click");
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "H3").map(node => node.textContent), ["应用程序（2）", "附加虚拟目录（2）"]);
  harness.node("site-search").value = "/api/v2/assets"; await harness.node("site-search").emit("input");
  const rows = nodesWithTag(harness.node("site-details-11"), "TBODY")[0].children;
  assert.deepEqual(rows.map(row => [row.dataset.applicationPath, row.dataset.directoryPath]), [["/api/v2", "/assets"]]);
});

test("Identical virtual paths in different applications remain separate with their full addresses", async () => {
  const harness = await topologyBrowser();
  harness.run('state.topology.sites[0].applications[2].virtualDirectories.push({path:"/assets",physicalPath:"D:\\\\Shared\\\\V2Assets"}); renderSnapshot();');
  await harness.node("site-details-toggle-11").emit("click");
  const rows = nodesWithTag(harness.node("site-details-11"), "TBODY")[1].children;
  assert.deepEqual(rows.map(row => row.dataset.applicationPath), ["/api", "/api/v2"]);
  assert.deepEqual(rows.flatMap(row => nodesWithTag(row, "A").map(link => link.href)), ["https://portal.example.test/api/assets/", "https://portal.example.test/api/v2/assets/"]);
  assert.equal(nodesWithTag(harness.node("site-details-11"), "H3")[1].textContent, "附加虚拟目录（2）");
});

test("Expanded sites follow fresh topology and retain expansion through filters without stale content", async () => {
  const harness = await topologyBrowser(); await harness.node("site-details-toggle-11").emit("click");
  const fresh = topologyFixture(); fresh.sites[0].applications[1].virtualDirectories[1].physicalPath = "D:\\NewAssets";
  harness.handle = () => fresh; await harness.run("refreshTopology()");
  const rows = nodesWithTag(harness.node("site-details-11"), "TBODY")[1].children;
  assert.equal(rows[0].children[3].textContent, "D:\\NewAssets");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  harness.node("site-search").value = "missing"; await harness.node("site-search").emit("input");
  assert.equal(harness.node("sites-body").children.length, 0);
  harness.node("site-search").value = ""; await harness.node("site-search").emit("input");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
});

test("Agent switching clears expansion and rejects callbacks from an earlier Agent", async () => {
  const harness = await topologyBrowser(), oldToggle = harness.node("site-details-toggle-11");
  await oldToggle.emit("click"); harness.run('selectServer("other");');
  assert.equal(harness.run("state.expandedSites.size"), 0);
  const topology = topologyFixture();
  harness.run(`state.snapshot={applicationPools:[],sites:[{id:11,name:"Portal",state:"Started",bindings:[]}]}; state.topology=${JSON.stringify(topology)}; renderSnapshot();`);
  await oldToggle.emit("click");
  assert.equal(harness.run("state.expandedSites.size"), 0); assert.equal(harness.node("sites-body").children.length, 1);
});

test("Unavailable topology expands to its actual status and retry instead of empty child tables", async () => {
  const harness = await topologyBrowser();
  for (const status of ["loading", "unsupported", "failed", "ready"]) {
    harness.run(`state.topology=null; state.topologyStatus=${JSON.stringify(status)}; state.topologyError="read failed"; renderSnapshot();`);
    if (harness.node("site-details-toggle-11").attributes["aria-expanded"] !== "true") await harness.node("site-details-toggle-11").emit("click");
    const detail = harness.node("site-details-11"), text = detail.children[0].children[0].children[0].textContent;
    assert.equal(nodesWithTag(detail, "TABLE").length, 0);
    assert.match(text, status === "loading" ? /正在读取/ : status === "unsupported" ? /升级 Agent/ : status === "failed" ? /读取失败/ : /没有此站点/);
    assert.equal(nodesWithTag(detail, "BUTTON")[0].disabled, status === "loading");
  }
});

test("Empty directory tables are hidden while non-HTTP child applications retain their status", async () => {
  const harness = await topologyBrowser();
  harness.run('state.topology.sites[0].applications.forEach(app=>{app.virtualDirectories=app.virtualDirectories.filter(dir=>dir.path==="/");app.enabledProtocols="net.tcp";}); renderSnapshot();');
  await harness.node("site-details-toggle-11").emit("click");
  const detail = harness.node("site-details-11"), bodies = nodesWithTag(detail, "TBODY");
  assert.deepEqual(nodesWithTag(detail, "H3").map(node => node.textContent), ["应用程序（2）"]);
  assert.equal(bodies.length, 1); assert.equal(bodies[0].children.length, 2);
  assert.equal(nodesWithTag(detail, "A").length, 0);
  assert.ok(descendants(detail).some(node => node.textContent?.includes("未启用 HTTP/HTTPS")));
  const pool = nodesWithTag(detail, "BUTTON").find(button=>button.textContent==="Api.Pool"); await pool.emit("click");
  assert.equal(harness.node("pool-search").value, "Api.Pool"); assert.equal(harness.node("pools-panel").hidden, false);
});

test("A root-only site has no child panel, zero counters or expansion action after refresh", async () => {
  const harness = await topologyBrowser(), oldToggle = harness.node("site-details-toggle-11");
  await oldToggle.emit("click");
  harness.run('state.topology.sites[0].applications = [state.topology.sites[0].applications[0]]; renderSnapshot();');
  const row = harness.node("sites-body").children.find(row => row.dataset.siteName === "Portal");
  assert.equal(nodesWithTag(row.children[0], "BUTTON").filter(button => button.id?.startsWith("site-details-toggle-")).length, 0);
  assert.equal(row.children[0].children.find(child => child.className === "resource-detail muted").textContent, "ID 11");
  assert.ok(!row.className.includes("is-expanded"));
  assert.ok(!harness.node("sites-body").children.some(row => row.dataset.siteDetailsId === "11"));
  assert.equal(row.children[2].children[0].textContent, "Web.Root");
  assert.equal(nodesWithTag(row.children[3], "A")[0].href, "https://portal.example.test/");
  assert.equal(nodesWithTag(row.children[4], "BUTTON").filter(button => button.dataset.operation).length, 3, "Site lifecycle controls remain available.");
  await oldToggle.emit("click");
  assert.equal(harness.run("state.expandedSites.size"), 0);
  assert.equal(harness.requests.length, 0);
});

test("Additional directories on the root application show only the directory table", async () => {
  const harness = await topologyBrowser();
  const root = application("/", "Web.Root", [{path:"/files", physicalPath:"D:\\Shared\\Files"}]);
  harness.run(`state.topology.sites[0].applications = [${JSON.stringify(root)}]; renderSnapshot();`);
  await harness.node("site-details-toggle-11").emit("click");
  const detail = harness.node("site-details-11");
  assert.deepEqual(nodesWithTag(detail, "H3").map(node => node.textContent), ["附加虚拟目录（1）"]);
  const body = nodesWithTag(detail, "TBODY")[0]; assert.equal(body.children.length, 1);
  const directory = body.children[0];
  assert.equal(directory.dataset.applicationPath, "/"); assert.equal(directory.dataset.directoryPath, "/files");
  assert.equal(directory.children[1].textContent, "/"); assert.equal(directory.children[2].children[0].textContent, "Web.Root");
  assert.equal(nodesWithTag(directory, "A")[0].href, "https://portal.example.test/files/");
  assert.equal(harness.requests.length, 0);
});

test("Pool filtering hides empty child groups and restores expansion for matching child applications", async () => {
  const harness = await topologyBrowser(); await harness.node("site-details-toggle-11").emit("click");
  harness.node("site-pool").value = "Web.Root"; await harness.node("site-pool").emit("change");
  assert.deepEqual(harness.node("sites-body").children.map(row => row.dataset.siteName), ["Portal", "Portal.Other"]);
  assert.equal(nodesWithTag(harness.node("sites-body"), "BUTTON").filter(button => button.id?.startsWith("site-details-toggle-")).length, 0);
  assert.equal(harness.run('state.topology.sites[0].applications.length'), 3);
  harness.node("site-pool").value = "Api.Pool"; await harness.node("site-pool").emit("change");
  assert.equal(harness.node("site-details-toggle-11").attributes["aria-expanded"], "true");
  assert.deepEqual(nodesWithTag(harness.node("site-details-11"), "H3").map(node => node.textContent), ["应用程序（2）", "附加虚拟目录（1）"]);
  assert.equal(harness.requests.length, 0);
});

test("A newer topology cannot keep associations for a site already removed from the detailed snapshot", async () => {
  const harness = await topologyBrowser();
  harness.run('state.topology.sites = state.topology.sites.filter(site => site.id !== 11);');
  harness.node("site-pool").value = "Api.Pool";
  await harness.node("site-pool").emit("change");
  assert.deepEqual(harness.node("sites-body").children.map(row => row.children[0].children[0].textContent), ["Portal.Other"]);
  assert.equal(harness.node("pools-body").children[1].children[0].children[2].textContent, "关联站点（1）");
});

for (const [status, code, expected] of [[501, "SITE_TOPOLOGY_UNSUPPORTED", "unsupported"], [403, "AGENT_ACCESS_DENIED", "failed"], [502, "AGENT_UNREACHABLE", "failed"]]) {
  test(`Topology HTTP ${status} stays separate from the valid snapshot and never claims an empty topology`, async () => {
    const harness = await topologyBrowser(); harness.handle = () => ({ httpStatus: status, body: { code, message: "Read unavailable" } });
    await harness.run("refreshTopology()");
    assert.equal(harness.run("state.topologyStatus"), expected);
    assert.equal(harness.run("state.topology"), null);
    assert.equal(harness.node("pools-body").children.length, 2);
    assert.equal(harness.node("create-site-button").disabled, false);
    harness.run('openResourceDialog({siteId:11})');
    assert.equal(harness.node("resource-summary").textContent, "");
    assert.match(harness.node("resource-status").textContent, expected === "unsupported" ? /升级 Agent/ : /读取失败/);
  });
}

test("Malformed topology and stale responses cannot replace valid current-server data", async () => {
  const harness = await topologyBrowser(); harness.handle = () => ({ sites: [{ name: "Broken", applications: null, bindings: [] }], applicationPools: [] });
  await harness.run("refreshTopology()"); assert.equal(harness.run("state.topologyStatus"), "failed");
  let resolveFirst, resolveSecond;
  harness.handle = () => new Promise(resolve => { if (!resolveFirst) resolveFirst = resolve; else resolveSecond = resolve; });
  const first = harness.run("refreshTopology()"), second = harness.run("refreshTopology()");
  resolveSecond(topologyFixture()); await second;
  resolveFirst({ collectedAt: "old", sites: [], applicationPools: [] }); await first;
  assert.equal(harness.run("state.topology.sites.length"), 2);
  let resolveOld; harness.handle = () => new Promise(resolve => { resolveOld = resolve; });
  harness.run('openResourceDialog({siteId:11})'); const oldServerRead = harness.run("refreshTopology()");
  harness.run('selectServer("second")'); resolveOld(topologyFixture()); await oldServerRead;
  assert.equal(harness.run("state.topology"), null); assert.equal(harness.node("resource-dialog").open, false);
});

test("Optional topology failure never turns a successful legacy refresh into a failed connection", async () => {
  const harness = await topologyBrowser();
  harness.handle = ({ path }) => path.endsWith("/info") ? { protocolVersion: "1.0", mode: "Windows" }
    : path.endsWith("/snapshot") ? snapshot(["Web.Root", "Api.Pool"])
    : path.endsWith("/certificates") ? [] : { httpStatus: 501, body: { code: "SITE_TOPOLOGY_UNSUPPORTED", message: "Upgrade Agent" } };
  await harness.run("refresh()"); await new Promise(resolve => setImmediate(resolve));
  assert.equal(harness.run("state.snapshotStatus"), "ready");
  assert.equal(harness.run("state.topologyStatus"), "unsupported");
  assert.equal(harness.node("connection-status").textContent, "连接正常");
  assert.equal(harness.node("notice").hidden, true);
});

test("Pool operation impact summaries include applications across all sites while retaining the Agent's exact list", async () => {
  const harness = await topologyBrowser();
  const affected = ["Portal/api", "Portal.Other/jobs"];
  harness.run('state.draft.request = { target: { applicationPoolName: "Api.Pool" }, parameters: {} };');
  harness.run(`renderValidation(${JSON.stringify({ ...validPrecheck, proposed: {}, affectedApplications: affected })})`);
  assert.equal(harness.node("validation-applications").children[0].textContent, "2 个受影响应用 · 2 个站点");
  assert.deepEqual(nodesWithTag(harness.node("validation-applications"), "LI").map(node => node.textContent), affected);
});

test("Agent rows remain selectable and retain resource tabs after searches and selection", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run('state.servers = [{id:"104",name:"Orders",hostName:"server-a"},{id:"222",name:"Reports",hostName:"server-b"}]; renderServers();');
  const tabs = harness.node("resource-navigation");
  assert.equal(tabs.hidden, false);
  assert.equal(harness.node("server-list").children.length, 2);
  harness.node("server-search").value = " SERVER-B ";
  await harness.node("server-search").emit("input");
  assert.equal(tabs.hidden, true);
  harness.run('selectServer("222");');
  assert.equal(tabs.hidden, false);
  assert.equal(tabs.parentNode.children[0].dataset.serverId, "222");
  harness.node("server-search").value = "does-not-exist";
  await harness.node("server-search").emit("input");
  assert.equal(tabs.hidden, true);
  harness.node("server-search").value = "";
  await harness.node("server-search").emit("input");
  assert.equal(tabs.hidden, false);
  assert.equal(tabs.parentNode.children[0].dataset.serverId, "222");
});

test("A busy operation disables all visible Agent choices", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run('state.servers = [{id:"104",name:"Orders"},{id:"222",name:"Reports"}]; renderServers(); state.busy = true; updateButtons();');
  assert.equal(harness.node("server-list").children[0].children[0].disabled, true);
  assert.equal(harness.node("server-list").children[1].children[0].disabled, true);
  harness.run('state.busy = false; updateButtons();');
  assert.equal(harness.node("server-list").children[0].children[0].disabled, false);
});

test("Vertical resource navigation updates the page heading and creation action", async () => {
  const harness = await browser(fallbackCrypto());
  const html = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/index.html"), "utf8");
  assert.deepEqual([...html.matchAll(/<button id="(sites|pools|services)-tab"/g)].map(match => match[1]), ["sites", "pools", "services"]);
  await harness.node("pools-tab").emit("keydown", {key:"ArrowUp"});
  assert.equal(harness.node("sites-panel").hidden, false);
  assert.equal(harness.node("pools-panel").hidden, true);
  assert.equal(harness.node("resource-heading").textContent, "站点");
  assert.equal(harness.node("create-pool-button").hidden, true);
  assert.equal(harness.node("create-site-button").hidden, false);
  assert.equal(harness.focused, harness.node("sites-tab"));
  await harness.node("sites-tab").emit("keydown", {key:"ArrowDown"});
  assert.equal(harness.node("pools-panel").hidden, false);
  await harness.node("pools-tab").emit("keydown", {key:"End"});
  assert.equal(harness.node("services-panel").hidden, false);
  await harness.node("services-tab").emit("keydown", {key:"Home"});
  assert.equal(harness.node("sites-panel").hidden, false);
  assert.equal(harness.focused, harness.node("sites-tab"));
});

const windowsService = (name = "Orders.Worker", overrides = {}) => ({ name, displayName: "订单处理服务", description: "处理订单", state: "Stopped", startType: "Manual", accountName: `NT SERVICE\\${name}`, executablePath: "D:\\Services\\Orders.exe", processId: 0, dependencies: ["RpcSs"], dependentServices: [], managed: true, canStart: true, canStop: false, canRestart: false, canUpdateStartType: true, unavailableReason: null, ...overrides });
const windowsServiceSnapshot = services => ({ collectedAt: new Date().toISOString(), services, canCreate: true, creationUnavailableReason: null, allowedAccounts: ["VirtualAccount", "NT AUTHORITY\\LocalService"] });
function loadWindowsServices(harness, services = [windowsService()]) { harness.run(`state.services = ${JSON.stringify(windowsServiceSnapshot(services))}; state.servicesStatus = "ready"; renderServices();`); }

test("Service list and counts include managed resources only while retaining temporary action restrictions", async () => {
  const harness = await browser(fallbackCrypto());
  const rows = [
    windowsService("Hidden.System", { managed: false, canStart: false, canUpdateStartType: false, unavailableReason: "服务不在允许管理范围内。" }),
    windowsService("IISManager.Agent", { managed: false, canStart: false, canUpdateStartType: false, unavailableReason: "管理组件及其依赖服务受保护。" }),
    windowsService("Disabled.Worker", { startType: "Disabled", canStart: false }),
    windowsService("Starting.Worker", { state: "StartPending", canStart: false, canUpdateStartType: false, unavailableReason: "服务处于过渡状态。" }),
    windowsService("Restricted.Worker", { canStart: false, canUpdateStartType: false, unavailableReason: "实际权限不足。" })
  ];
  loadWindowsServices(harness, rows);
  assert.deepEqual(harness.node("services-body").children.map(row => row.dataset.serviceName), ["Disabled.Worker", "Starting.Worker", "Restricted.Worker"]);
  assert.equal(harness.node("services-tab-count").textContent, 3);
  assert.equal(harness.node("service-results").textContent, "显示 3 / 3 个可管理 Windows 服务");
  assert.equal(harness.run("state.services.services.length"), 5, "The full snapshot must remain available for dependency selection.");
  harness.node("service-search").value = "Hidden.System"; harness.run("renderServices()");
  assert.equal(harness.node("services-body").children.length, 0);
  assert.equal(harness.node("service-results").textContent, "显示 0 / 3 个可管理 Windows 服务");
  assert.match(harness.node("services-empty").textContent, /查询条件/);
  await harness.node("service-reset").emit("click");
  assert.equal(harness.node("services-body").children.length, 3);
  assert.equal(harness.requests.length, 0);
});

test("An empty management scope shows an actionable empty state and keeps permitted creation independent", async () => {
  const harness = await browser(fallbackCrypto());
  loadWindowsServices(harness, [windowsService("RpcSs", { managed: false, canStart: false, canUpdateStartType: false })]);
  assert.equal(harness.node("services-body").children.length, 0);
  assert.equal(harness.node("services-tab-count").textContent, 0);
  assert.equal(harness.node("services-empty").hidden, false);
  assert.match(harness.node("services-empty").textContent, /配置的服务范围/);
  assert.equal(harness.node("create-service-button").disabled, false);
  harness.run('openServiceOperation("windowsService.create")');
  const picker = harness.run('document.getElementById("service-dependencies").parentNode');
  const choices = picker.children.find(node => node.tagName === "DETAILS");
  assert.equal(choices.children[1].children[0].textContent, "RpcSs", "Hidden resources must remain available as dependency choices.");
  await choices.children[1].children[0].emit("click");
  assert.equal(harness.node("service-dependencies").value, "RpcSs");
  assert.equal(harness.requests.length, 0);
});

test("Windows service navigation supports keyboard traversal, filters and its creation action", async () => {
  const harness = await browser(fallbackCrypto());
  loadWindowsServices(harness, [windowsService(), windowsService("Reporting", { displayName: "报表服务", state: "Running", startType: "Automatic", canStart: false, canStop: true, canRestart: true })]);
  await harness.node("pools-tab").emit("keydown", { key: "ArrowDown" });
  assert.equal(harness.node("services-panel").hidden, false); assert.equal(harness.node("sites-panel").hidden, true);
  assert.equal(harness.node("resource-heading").textContent, "Windows 服务"); assert.equal(harness.node("create-service-button").hidden, false);
  assert.equal(harness.node("create-service-button").disabled, false); assert.equal(harness.node("create-pool-button").hidden, true);
  harness.node("service-search").value = "报表"; harness.node("service-state").value = "Running"; harness.node("service-start-type").value = "Automatic";
  harness.run("renderServices()"); assert.equal(harness.node("services-body").children.length, 1); assert.equal(harness.node("services-body").children[0].dataset.serviceName, "Reporting");
  await harness.node("service-reset").emit("click"); assert.equal(harness.node("services-body").children.length, 2);
  await harness.node("services-tab").emit("keydown", { key: "ArrowDown" }); assert.equal(harness.node("sites-panel").hidden, false);
  assert.equal(harness.requests.length, 0);
});

test("Service capabilities disable unsafe actions, explain disabled startup and keep properties available", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness, [windowsService("Disabled.Worker", { startType: "Disabled", canStart: false })]);
  const actions = harness.node("services-body").children[0].children[4].children;
  assert.equal(actions[0].disabled, true); assert.match(actions[0].title, /属性/);
  assert.equal(actions[1].disabled, true); assert.equal(actions[2].disabled, true); assert.equal(actions[3].disabled, false);
  harness.run('openServiceOperation("windowsService.start",state.services.services[0])'); assert.equal(harness.node("operation-dialog").open, undefined);
  await actions[3].emit("click"); assert.equal(harness.node("operation-dialog").open, true);
  assert.equal(harness.node("service-start-type-input").value, "Disabled");
  harness.run('state.busy=true; updateButtons()'); assert.equal(actions[3].disabled, true);
  harness.run('state.busy=false;state.services.canCreate=false;state.services.creationUnavailableReason="策略禁止创建";renderServices()');
  assert.equal(harness.node("create-service-button").disabled, true); assert.equal(harness.node("create-service-button").title, "策略禁止创建");
});

test("Startup property edit submits only updateStartType, preserves operation ID and never implicitly starts", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness, [windowsService("Disabled.Worker", { startType: "Disabled", canStart: false })]);
  harness.run('openServiceOperation("windowsService.updateStartType",state.services.services[0])');
  assert.throws(() => harness.run("readDraft()"), /未变化/);
  harness.node("service-start-type-input").value = "Automatic";
  harness.handle = ({ path, body }) => path.endsWith("/validate")
    ? { valid: true, fingerprint: "service-fingerprint", errors: [], warnings: [], changes: [{field:"startType",before:"Disabled",after:"Automatic"}], affectedApplications: [], affectedServices: ["Disabled.Worker"] }
    : { serverId: "104", request: body, state: "queued", updatedAt: new Date().toISOString() };
  await harness.run("validate({preventDefault(){}})");
  const request = harness.requests[0].body; assert.equal(request.type, "windowsService.updateStartType");
  assert.deepEqual(request.target, { serviceName: "Disabled.Worker" }); assert.deepEqual(request.parameters, { startType: "Automatic" });
  assert.match(request.operationId, uuidV4); assert.equal(harness.node("validation-services-area").hidden, false);
  assert.equal(harness.node("validation-services").children[0].children[0].textContent, "Disabled.Worker");
  assert.match(harness.node("validation-applications").children[0].textContent, /未列出/);
  await harness.run("submitOperation()"); assert.equal(harness.requests.length, 2);
  assert.deepEqual(harness.requests[1].body, { ...request, expectedFingerprint: "service-fingerprint" });
  assert.match(harness.node("task-target").textContent, /Disabled.Worker/); assert.equal(harness.node("task-service-target").hidden, false);
  assert.ok(harness.requests.every(({ body }) => !body || body.type !== "windowsService.start"));
});

test("Service creation defaults to manual and virtual account, preserving individual argument spaces", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness, [windowsService("RpcSs", { displayName: "RPC", managed: false, canStart: false, canUpdateStartType: false })]);
  harness.run('openServiceOperation("windowsService.create")');
  assert.equal(harness.node("service-name").maxLength, 128);
  assert.equal(harness.node("service-start-type-input").value, "Manual"); assert.equal(harness.node("service-account").value, "VirtualAccount");
  harness.node("service-name").value = " New.Worker "; harness.node("service-display-name").value = " 新服务 "; harness.node("service-description").value = " 业务工作进程 "; harness.node("service-executable").value = " D:\\Services\\New Worker.exe ";
  harness.node("service-arguments").value = '--label=New Worker\n  literal argument  \n\n--mode=production'; harness.node("service-dependencies").value = " RpcSs \n";
  const request = JSON.parse(harness.run("JSON.stringify(readDraft())"));
  assert.deepEqual(request.target, { serviceName: "New.Worker" });
  assert.deepEqual(request.parameters, { displayName:"新服务",description:"业务工作进程",executablePath:"D:\\Services\\New Worker.exe",arguments:["--label=New Worker","  literal argument  ","--mode=production"],startType:"Manual",accountName:"VirtualAccount",dependencies:["RpcSs"] });
  assert.equal(Object.hasOwn(request.parameters, "password"), false); assert.equal(harness.requests.length, 0);
});

test("Creating a service confirms its checked arguments even when the proposed service omits them", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness); harness.run('openServiceOperation("windowsService.create")');
  harness.node("service-name").value = "New.Worker"; harness.node("service-display-name").value = "新服务"; harness.node("service-executable").value = "D:\\Services\\New.exe";
  harness.node("service-arguments").value = "--mode=production\nlabel with spaces";
  harness.handle = () => ({valid:true,fingerprint:"create-fingerprint",errors:[],warnings:[],changes:[],affectedApplications:[],affectedServices:["New.Worker"],proposed:windowsService("New.Worker")});
  await harness.run("validate({preventDefault(){}})");
  const proposed = JSON.parse(harness.node("validation-proposed").textContent);
  assert.deepEqual(proposed.parameters.arguments, ["--mode=production", "label with spaces"]);
  assert.deepEqual(proposed.target, {serviceName:"New.Worker"});
});

test("Existing service property confirmation excludes any original command-line arguments", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness); harness.run('openServiceOperation("windowsService.updateStartType",state.services.services[0])');
  harness.node("service-start-type-input").value = "Automatic";
  harness.handle = () => ({valid:true,fingerprint:"update-fingerprint",errors:[],warnings:[],changes:[],affectedApplications:[],affectedServices:["Orders.Worker"],proposed:{...windowsService(),arguments:["original argument"],commandLine:"original command"}});
  await harness.run("validate({preventDefault(){}})");
  const proposed = JSON.parse(harness.node("validation-proposed").textContent);
  assert.equal(Object.hasOwn(proposed.parameters, "arguments"), false); assert.equal(Object.hasOwn(proposed.parameters, "commandLine"), false);
});

for (const [status, code, expected] of [[501, "WINDOWS_SERVICES_UNSUPPORTED", "unsupported"], [403, "AUTH_DENIED", "failed"], [502, "AGENT_NETWORK_ERROR", "failed"], [403, "WINDOWS_SERVICES_UNSUPPORTED", "failed"]]) {
  test(`Service HTTP ${status}/${code} isolates optional failures and reports their actual category`, async () => {
    const harness = await browser(fallbackCrypto()); harness.run(`state.snapshot=${JSON.stringify(snapshot(["Web.Root"]))};`);
    harness.handle = () => ({ httpStatus: status, body: {code,message:"读取失败"} }); await harness.run("refreshServices()");
    assert.equal(harness.run("state.servicesStatus"), expected); assert.equal(harness.run("state.snapshot.applicationPools[0].name"), "Web.Root");
    assert.equal(harness.node("services-body").children.length, 0); assert.match(harness.node("services-status").textContent, expected === "unsupported" ? /升级 Agent/ : /读取失败/);
    assert.equal(harness.node("create-service-button").disabled, true);
  });
}

test("Service refresh stays independent from a successful legacy IIS refresh", async () => {
  const harness = await browser(fallbackCrypto()); let release;
  harness.handle = ({ path }) => path.endsWith("/windows-services") ? new Promise(resolve => {release=resolve;})
    : path.endsWith("/info") ? {protocolVersion:"1.0",mode:"Simulation",machineName:"Windows",osVersion:"Windows",iisVersion:"10",agentVersion:"1"}
    : path.endsWith("/snapshot") ? snapshot(["Web.Root"]) : path.endsWith("/site-topology") ? {httpStatus:501,body:{code:"SITE_TOPOLOGY_UNSUPPORTED"}} : [];
  await harness.run("refresh()"); assert.equal(harness.run("state.snapshotStatus"), "ready"); assert.equal(harness.run("state.loading"), false);
  assert.equal(harness.run("state.servicesStatus"), "loading");
  release(windowsServiceSnapshot([windowsService()])); await new Promise(resolve => setImmediate(resolve));
  assert.equal(harness.run("state.servicesStatus"), "ready"); assert.equal(harness.node("services-body").children.length, 1);
});

test("A failed IIS refresh still allows an independently valid Windows service snapshot", async () => {
  const harness = await browser(fallbackCrypto());
  harness.handle = ({path}) => path.endsWith("/windows-services") ? windowsServiceSnapshot([windowsService()]) : {httpStatus:503,body:{code:"IIS_UNAVAILABLE",message:"IIS 不可用"}};
  await harness.run("refresh()"); await new Promise(resolve => setImmediate(resolve));
  assert.equal(harness.run("state.snapshotStatus"), "failed"); assert.equal(harness.run("state.servicesStatus"), "ready");
  assert.equal(harness.node("create-service-button").disabled, false);
  harness.run('openServiceOperation("windowsService.start",state.services.services[0])'); assert.equal(harness.node("operation-dialog").open, true);
});

test("Certificate failures preserve a valid IIS snapshot and its non-certificate operations", async () => {
  const harness = await browser(fallbackCrypto());
  harness.handle = ({path}) => path.endsWith("/info") ? {protocolVersion:"1.0",mode:"Windows"}
    : path.endsWith("/snapshot") ? snapshot(["Web.Root"])
    : {httpStatus:503,body:{code:"OPTIONAL_UNAVAILABLE",message:"不可用"}};
  await harness.run("refresh()");
  assert.equal(harness.run("state.snapshotStatus"), "ready");
  assert.equal(harness.run("state.snapshot.applicationPools.length"), 1);
  assert.equal(harness.node("create-pool-button").disabled, false);
  assert.match(harness.node("notice").textContent, /证书读取失败/);
  assert.equal(harness.run("state.certificates.length"), 0);
});

test("Read-only server permissions disable both operational and configuration submission", async () => {
  const harness = await browser(fallbackCrypto());
  harness.run(`state.permissions={servers:[{serverId:"104",read:true,operate:false,configure:false}]};state.snapshot=${JSON.stringify(snapshot(["Web.Root"]))};renderSnapshot();updateButtons();`);
  assert.equal(harness.node("create-pool-button").disabled, true);
  harness.requests.length = 0;
  await harness.run("validate({preventDefault(){}})");
  assert.equal(harness.requests.length, 0);
  assert.equal(harness.run('operationAllowed("applicationPool.stop")'), false);
  assert.equal(harness.run('operationAllowed("site.create")'), false);
});

test("Late service responses cannot overwrite a newer refresh or another Agent's resources", async () => {
  const harness = await browser(fallbackCrypto()); let release;
  harness.handle = () => new Promise(resolve => {release=resolve;}); const first = harness.run("refreshServices()");
  harness.handle = () => windowsServiceSnapshot([windowsService("New.Worker")]); await harness.run("refreshServices()");
  release(windowsServiceSnapshot([windowsService("Old.Worker")])); await first;
  assert.equal(harness.run("state.services.services[0].name"), "New.Worker");
  harness.handle = () => new Promise(resolve => {release=resolve;}); const oldAgent = harness.run("refreshServices()");
  harness.run('selectServer("222")'); assert.equal(harness.node("services-body").children.length, 0);
  release(windowsServiceSnapshot([windowsService("Old.Agent")])); await oldAgent; assert.equal(harness.run("state.services"), null);
});

test("Service details encode the exact name, use safe text and discard closed or cross-Agent responses", async () => {
  const harness = await browser(fallbackCrypto()); const service = windowsService("Orders #Worker", {displayName:"<img src=x>",processId:123,dependentServices:["Consumer"]});
  harness.handle = () => service; await harness.run(`openServiceDetails(${JSON.stringify(service.name)})`);
  assert.equal(harness.requests[0].path, "/api/v1/servers/104/windows-services/Orders%20%23Worker");
  assert.equal(harness.node("service-dialog-title").textContent, "<img src=x>");
  const detailValues = harness.node("service-details").children.filter((_,index) => index % 2).map(node => node.textContent);
  assert.ok(detailValues.includes("123")); assert.ok(detailValues.includes("Consumer")); assert.ok(detailValues.includes("RpcSs")); assert.ok(detailValues.includes(service.executablePath));
  let release; harness.handle = () => new Promise(resolve => {release=resolve;}); const closed = harness.run('openServiceDetails("Closed")');
  harness.run("closeServiceDetails()"); release(windowsService("Closed")); await closed;
  assert.equal(harness.node("service-details").children.length, 0); assert.equal(harness.node("service-dialog").open, false);
  const otherAgent = harness.run('openServiceDetails("Previous.Agent")'); harness.run('selectServer("222")'); release(windowsService("Previous.Agent")); await otherAgent;
  assert.equal(harness.node("service-details").children.length, 0); assert.equal(harness.node("service-dialog").open, false);
});

test("Lost service submissions retain the original operation, prevent replay and preserve manual verification", async () => {
  const harness = await browser(fallbackCrypto()); loadWindowsServices(harness); harness.run('openServiceOperation("windowsService.start",state.services.services[0])');
  harness.handle = ({path}) => { if (path.endsWith("/validate")) return {valid:true,fingerprint:"service-fingerprint",errors:[],warnings:[],changes:[],affectedApplications:[],affectedServices:["Orders.Worker"]}; throw new Error("Lost response"); };
  await harness.run("validate({preventDefault(){}})"); const original = harness.requests[0].body.operationId;
  await harness.run("submitOperation()"); assert.equal(harness.run("state.task.state"), "unknown"); assert.equal(harness.run("state.task.request.operationId"), original);
  await harness.run("submitOperation()"); assert.equal(harness.requests.length, 2); assert.equal(harness.node("release-task-area").hidden, false);
  await harness.node("task-service-target").emit("click"); assert.equal(harness.node("services-panel").hidden, false); assert.equal(harness.node("service-search").value, "Orders.Worker");
});

const annotationSites = () => [
  { ...existingSite("Portal.Web", "Web.Root", [{ protocol: "https", ipAddress: "*", port: 443, hostName: "portal.example.test" }]), id: 11 },
  { ...existingSite("Orders.Api", "Api.Pool", [{ protocol: "http", ipAddress: "*", port: 8080, hostName: "orders.example.test" }]), id: 22 },
  { ...existingSite("Files.Web", "Files.Pool", [{ protocol: "https", ipAddress: "*", port: 443, hostName: "files.example.test" }]), id: 33, state: "Stopped" }
];
const annotationEntries = () => [
  { siteId: 11, siteName: "Portal.Web", alias: "ERP 业务门户", tags: ["生产", "财务", "对外服务"], version: "portal-v1" },
  { siteId: 22, siteName: "Orders.Api", alias: "订单接口", tags: ["生产", "API"], version: "orders-v1" },
  { siteId: 33, siteName: "Files.Web", alias: "", tags: ["测试"], version: "files-v1" }
];
async function annotationBrowser() {
  const harness = await browser(fallbackCrypto());
  harness.run(`state.draft=null; state.servers=[{id:"104",name:"运维服务器",hostName:"agent.example.test"},{id:"222",name:"第二台",hostName:"second.example.test"}]; state.info={mode:"Windows"}; state.snapshot=${JSON.stringify({ ...snapshot(["Web.Root", "Api.Pool", "Files.Pool"]), sites: annotationSites() })}; state.snapshotStatus="ready"; state.siteAnnotations=${JSON.stringify({ serverId: "104", entries: annotationEntries() })}; state.siteAnnotationsStatus="ready"; renderServers(); renderSnapshot(); activateResourceTab("sites"); toggleSiteTagOptions(false); byId("site-annotation-editor").hidden=true; notice("");`);
  return harness;
}
function visibleSiteNames(harness) { return harness.node("sites-body").children.filter(row => row.dataset.siteName).map(row => row.dataset.siteName); }
function annotationEnvelope(entries = annotationEntries(), serverId = "104") { return { serverId, entries }; }
async function editAnnotation(harness, alias = "新的业务门户") {
  harness.run("openSiteAnnotationEditor(state.snapshot.sites[0])"); harness.node("annotation-alias").value = alias; await harness.node("annotation-alias").emit("input");
}

const applicationAnnotationEntries = () => [
  ...annotationEntries(),
  { siteId: 11, siteName: "Portal.Web", applicationPath: "/api", alias: "财务应用", tags: ["应用专用", "API"], version: "app-v1" },
  { siteId: 22, siteName: "Orders.Api", applicationPath: "/api", alias: "订单应用", tags: ["订单"], version: "orders-app-v1" }
];
async function applicationAnnotationBrowser() {
  const harness = await annotationBrowser(), topology = topologyFixture();
  topology.sites = [{ ...topology.sites[0], name: "Portal.Web" }, { ...topology.sites[0], id: 22, name: "Orders.Api" }];
  harness.run(`state.topology=${JSON.stringify(topology)}; state.topologyStatus="ready"; state.siteAnnotations.entries=${JSON.stringify(applicationAnnotationEntries())}; state.expandedSites.add(siteExpansionKey(state.snapshot.sites[0])); renderSnapshot();`);
  return harness;
}

test("Application annotations render safely with original path and open the shared editor for the exact application", async () => {
  const harness = await applicationAnnotationBrowser(), alias = "<img src=x onerror=alert(1)>";
  harness.run(`state.siteAnnotations.entries[3].alias=${JSON.stringify(alias)}; renderSnapshot();`);
  const button = harness.node("application-annotation-edit-11-%2Fapi"), cell = button.parentNode;
  assert.equal(cell.children[0].textContent, alias); assert.equal(cell.children[0].children.length, 0);
  assert.ok(cell.children.some(node => node.textContent === "/api"));
  assert.ok(cell.children.some(node => node.className === "site-row-tags tag-chips"));
  assert.equal(harness.run('siteAnnotation(state.snapshot.sites[0]).alias'), "ERP 业务门户");
  assert.equal(harness.run('siteAnnotation(state.snapshot.sites[1],"/api").alias'), "订单应用");
  await button.emit("click");
  assert.equal(harness.node("annotation-site-name").value, "Portal.Web");
  assert.equal(harness.node("annotation-application-path").value, "/api"); assert.equal(harness.node("annotation-application-field").hidden, false);
  assert.equal(harness.node("annotation-alias-label").textContent, "应用程序别名");
  assert.equal(harness.node("annotation-alias").value, alias); assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), "app-v1");
  await harness.node("annotation-cancel").emit("click"); assert.equal(harness.focused.id, "application-annotation-edit-11-%2Fapi");
  harness.run("openSiteAnnotationEditor(state.snapshot.sites[0])"); assert.equal(harness.node("annotation-application-field").hidden, true);
  assert.equal(harness.node("annotation-alias-label").textContent, "站点别名"); assert.equal(harness.requests.length, 0);
});

test("Application alias search and tag filters reveal the owning site and expand its applications", async () => {
  const harness = await applicationAnnotationBrowser(); harness.run("state.expandedSites.clear()");
  for (const query of ["财务应用", "应用专用"]) {
    harness.node("site-search").value = query; harness.run("renderSnapshot()");
    assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]);
    assert.ok(harness.node("sites-body").children.some(row => row.className === "site-details-row"));
  }
  harness.node("site-search").value = ""; harness.run('setSiteTagFilter("应用专用",true)');
  assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]); assert.equal(harness.run('availableSiteTags().includes("应用专用")'), true);
  harness.node("site-state").value = "Stopped"; harness.run("renderSnapshot()"); assert.deepEqual(visibleSiteNames(harness), []);
});

test("Application saves send path and version once and read back independently without changing the site annotation", async () => {
  const harness = await applicationAnnotationBrowser(); await harness.node("application-annotation-edit-11-%2Fapi").emit("click");
  harness.node("annotation-alias").value = "新接口"; harness.node("annotation-tag-input").value = "新应用标签";
  let persisted;
  harness.annotationHandle = ({ body }) => {
    if (body) { persisted = { siteId: body.siteId, siteName: body.siteName, applicationPath: body.applicationPath, alias: body.alias, tags: body.tags, version: "app-v2" }; return persisted; }
    return annotationEnvelope([...annotationEntries(), persisted, applicationAnnotationEntries()[4]]);
  };
  await harness.node("site-annotation-form").emit("submit");
  assert.deepEqual(harness.requests.map(request => request.method), ["POST", "GET"]);
  assert.deepEqual(harness.requests[0].body, { siteId: 11, siteName: "Portal.Web", applicationPath: "/api", alias: "新接口", tags: ["应用专用", "API", "新应用标签"], expectedVersion: "app-v1" });
  assert.equal(harness.node("site-annotation-editor").hidden, true);
  assert.equal(harness.run('siteAnnotation(state.snapshot.sites[0],"/api").alias'), "新接口");
  assert.equal(harness.run('siteAnnotation(state.snapshot.sites[0]).alias'), "ERP 业务门户");
  assert.equal(harness.run('siteAnnotation(state.snapshot.sites[1],"/api").alias'), "订单应用");
  assert.equal(harness.run('availableSiteTags().includes("新应用标签")'), true);
});

test("Application conflicts retain drafts and reconcile the application version instead of its parent site", async () => {
  const harness = await applicationAnnotationBrowser(); await harness.node("application-annotation-edit-11-%2Fapi").emit("click");
  harness.node("annotation-alias").value = "我的接口"; await harness.node("annotation-alias").emit("input");
  harness.annotationHandle = ({ body }) => body ? { httpStatus: 409, body: { code: "SITE_ANNOTATION_CONFLICT" } }
    : annotationEnvelope(applicationAnnotationEntries().map(entry => entry.version === "app-v1" ? { ...entry, version: "app-v2", alias: "他人的接口" } : entry));
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.node("annotation-save").disabled, true);
  assert.equal(harness.node("annotation-alias").value, "我的接口");
  await harness.run("reconcileSiteAnnotation()"); assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), "app-v2");
  assert.equal(harness.node("annotation-alias").value, "我的接口"); assert.equal(harness.node("annotation-save").disabled, false);
  assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
});

test("Lost application saves retain the draft and cannot be confirmed by a different application or site record", async () => {
  const harness = await applicationAnnotationBrowser(); await harness.node("application-annotation-edit-11-%2Fapi").emit("click");
  harness.node("annotation-alias").value = "待核对";
  // Matching parent or other-application content must not confirm this application's save.
  harness.annotationHandle = ({ body }) => { if (body) throw new Error("Lost response"); return annotationEnvelope(applicationAnnotationEntries().map(entry => entry.applicationPath === "/api" && entry.siteId === 11 ? entry : { ...entry, alias: "待核对" })); };
  await harness.run("saveSiteAnnotation()"); await harness.run("saveSiteAnnotation()");
  assert.equal(harness.node("site-annotation-editor").hidden, false); assert.equal(harness.node("annotation-save").disabled, true);
  assert.equal(harness.node("annotation-alias").value, "待核对"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
});

test("Deleted applications and stale cross-server edit buttons cannot write to a different target", async () => {
  const harness = await applicationAnnotationBrowser(), button = harness.node("application-annotation-edit-11-%2Fapi");
  await button.emit("click"); harness.run('state.topology.sites[0].applications=state.topology.sites[0].applications.filter(application=>application.path!=="/api"); renderSnapshot()');
  assert.equal(harness.node("annotation-save").disabled, true); assert.equal(harness.run("state.siteAnnotationEditor.targetChanged"), true);
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.length, 0);
  harness.run('selectServer("222")'); await button.emit("click"); assert.equal(harness.run("state.siteAnnotationEditor"), null);
});

test("Annotation reads finishing before topology refresh never mark an existing application draft as deleted", async () => {
  const harness = await applicationAnnotationBrowser(); await harness.node("application-annotation-edit-11-%2Fapi").emit("click");
  let finishTopology; const topology = JSON.parse(harness.run("JSON.stringify(state.topology)"));
  harness.handle = () => new Promise(resolve => { finishTopology = resolve; });
  harness.annotationHandle = () => annotationEnvelope(applicationAnnotationEntries());
  const refresh = harness.run("refreshTopology()"); await harness.run("refreshSiteAnnotations()");
  assert.equal(harness.run("state.siteAnnotationEditor.needsReview"), false);
  assert.equal(harness.run("state.siteAnnotationEditor.targetChanged"), false); assert.equal(harness.node("annotation-save").disabled, true);
  finishTopology(topology); await refresh;
  assert.equal(harness.node("annotation-save").disabled, false); assert.equal(harness.node("annotation-alias").value, "财务应用");
});

test("Annotations normalize whitespace, retain first tag casing, allow clearing and enforce all limits", async () => {
  const harness = await annotationBrowser();
  assert.deepEqual(JSON.parse(harness.run('JSON.stringify(normalizeSiteAnnotation("  门户  ",["  API ","api","", "生产", "生产"]))')), { alias: "门户", tags: ["API", "生产"] });
  assert.deepEqual(JSON.parse(harness.run('JSON.stringify(normalizeSiteAnnotation("",[]))')), { alias: "", tags: [] });
  for (const value of ["\t门户", "门\n户", "\u007f", "\u0085"]) assert.throws(() => harness.run(`normalizeSiteAnnotation(${JSON.stringify(value)},[])`), /控制字符/);
  assert.throws(() => harness.run('normalizeSiteAnnotation("a".repeat(101),[])'), /100/);
  assert.throws(() => harness.run('normalizeSiteAnnotation("",["a".repeat(41)])'), /40/);
  assert.throws(() => harness.run('normalizeSiteAnnotation("",Array.from({length:21},(_,i)=>String(i)))'), /20/);
  assert.throws(() => harness.run(`normalizeSiteAnnotation("",[${JSON.stringify("API\n")}])`), /控制字符/);
  assert.equal(harness.run('normalizeSiteAnnotation("a".repeat(100),Array.from({length:20},(_,i)=>String(i))).tags.length'), 20);
});

test("Alias and tags use safe text while retaining the IIS name, hierarchy, addresses and operation target", async () => {
  const harness = await annotationBrowser();
  const row = harness.node("sites-body").children[0];
  assert.equal(row.children[0].children[0].textContent, "ERP 业务门户");
  assert.ok(descendants(row.children[0]).some(node => node.textContent === "Portal.Web"));
  assert.deepEqual(row.children[0].children.find(node => node.className === "site-row-tags tag-chips").children.map(chip => chip.children[0].textContent), ["生产", "财务", "对外服务"]);
  assert.equal(nodesWithTag(row.children[3], "A")[0].href, "https://portal.example.test/");
  assert.equal(row.children.length, 5, "Tags stay below the name and do not add an empty table column");
  await row.children[4].children[1].emit("click");
  assert.deepEqual(JSON.parse(harness.run("JSON.stringify(readDraft().target)")), { siteName: "Portal.Web" });
  harness.run('state.siteAnnotations.entries[0].alias="<img src=x onerror=alert(1)>"; state.siteAnnotations.entries[0].tags=["<script>"]; renderSnapshot();');
  const malicious = harness.node("sites-body").children[0].children[0];
  assert.equal(malicious.children[0].textContent, "<img src=x onerror=alert(1)>");
  assert.ok(descendants(malicious).some(node => node.textContent === "<script>"));
  assert.equal(nodesWithTag(malicious, "SCRIPT").length, 0);
  assert.ok(nodesWithTag(malicious, "IMG").every(image => image.src.startsWith("/assets/fluent/")));
});

test("Multiple annotation tags match either tag and combine with keywords, status, pool, protocol and one binding port", async () => {
  const harness = await annotationBrowser();
  harness.run('state.siteTagFilters=["生产","api"]; renderSnapshot();'); assert.deepEqual(visibleSiteNames(harness), ["Portal.Web", "Orders.Api"]);
  harness.node("site-search").value = "财务"; await harness.node("site-search").emit("input"); assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]);
  harness.node("site-search").value = "erp"; await harness.node("site-search").emit("input"); assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]);
  harness.node("site-search").value = "Portal.Web"; await harness.node("site-search").emit("input"); assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]);
  harness.node("site-state").value = "Stopped"; await harness.node("site-state").emit("change"); assert.deepEqual(visibleSiteNames(harness), []);
  harness.node("site-search").value = ""; harness.node("site-state").value = "Started"; harness.node("site-pool").value = "Api.Pool"; await harness.node("site-pool").emit("change"); assert.deepEqual(visibleSiteNames(harness), ["Orders.Api"]);
  harness.node("site-protocol").value = "http"; harness.node("site-port").value = "443"; await harness.node("site-port").emit("input"); assert.deepEqual(visibleSiteNames(harness), []);
  harness.node("site-port").value = "8080"; await harness.node("site-port").emit("input"); assert.deepEqual(visibleSiteNames(harness), ["Orders.Api"]);
  harness.node("site-search").value = "orders.example.test"; await harness.node("site-search").emit("input"); assert.deepEqual(visibleSiteNames(harness), ["Orders.Api"]);
  await harness.node("site-reset").emit("click"); assert.deepEqual(visibleSiteNames(harness), ["Portal.Web", "Orders.Api", "Files.Web"]);
  assert.equal(harness.run("state.siteTagFilters.length"), 0);
});

test("Checkbox tag filtering, removal and reset retain visible selection semantics", async () => {
  const harness = await annotationBrowser(); await harness.node("site-tags-toggle").emit("click");
  assert.equal(harness.node("site-tag-options").hidden, false); assert.equal(harness.node("site-tags-toggle").attributes["aria-expanded"], "true");
  const production = harness.node("site-tag-option-list").children.find(label => label.children[1]?.children[0]?.textContent === "生产");
  production.children[0].checked = true; await production.children[0].emit("change");
  assert.deepEqual(visibleSiteNames(harness), ["Portal.Web", "Orders.Api"]); assert.equal(harness.node("site-filter-tags").children.length, 1);
  await harness.node("site-filter-tags").children[0].children[1].emit("click"); assert.equal(harness.run("state.siteTagFilters.length"), 0);
  await harness.node("site-tag-options").emit("keydown", { key: "Escape" }); assert.equal(harness.node("site-tag-options").hidden, true);
});

test("Annotation editing adds Enter tags, deduplicates casing, offers existing tags and removes individual chips", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness);
  assert.equal(harness.hasBodyClass("annotation-editor-open"), true); assert.equal(harness.node("annotation-site-name").value, "Portal.Web");
  assert.ok(harness.node("annotation-recommended-tags").children.some(button => button.textContent === "API"));
  harness.node("annotation-tag-input").value = "  自定义  "; await harness.node("annotation-tag-input").emit("input"); await harness.node("annotation-tag-input").emit("keydown", { key: "Enter" });
  assert.equal(harness.run('state.siteAnnotationEditor.tags.includes("自定义")'), true);
  harness.node("annotation-tag-input").value = "生产"; await harness.node("annotation-tag-input").emit("keydown", { key: "Enter" }); assert.equal(harness.run("state.siteAnnotationEditor.tags.length"), 4);
  const api = harness.node("annotation-recommended-tags").children.find(button => button.textContent === "API"); await api.emit("click");
  assert.equal(harness.run('state.siteAnnotationEditor.tags.includes("API")'), true);
  const finance = harness.node("annotation-draft-tags").children.find(chip => chip.children[0].textContent === "财务"); await finance.children[1].emit("click");
  assert.equal(harness.run('state.siteAnnotationEditor.tags.includes("财务")'), false);
  await harness.node("site-annotation-editor").emit("keydown", { key: "Escape" }); assert.equal(harness.node("site-annotation-editor").hidden, true); assert.equal(harness.hasBodyClass("annotation-editor-open"), false);
  assert.equal(harness.requests.length, 0, "Cancel and Escape never persist draft changes");
});

test("Saving annotations posts the captured identity and CSRF once, then reads back before updating filters and closing", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness, "  财务业务入口  ");
  harness.node("annotation-tag-input").value = " 新标签 "; let persisted;
  harness.annotationHandle = ({ body }) => {
    if (body) { persisted = { siteId: body.siteId, siteName: body.siteName, alias: body.alias, tags: body.tags, version: "portal-v2" }; return persisted; }
    return annotationEnvelope([persisted, ...annotationEntries().slice(1)]);
  };
  harness.node("site-search").value = "财务业务入口";
  await harness.node("site-annotation-form").emit("submit");
  assert.equal(harness.requests.length, 2); assert.equal(harness.requests[0].method, "POST"); assert.equal(harness.requests[1].method, "GET");
  assert.equal(harness.requests[0].path, "/api/v1/servers/104/site-annotations"); assert.equal(harness.requests[0].headers["X-IISManager-CSRF"], "test-session");
  assert.deepEqual(harness.requests[0].body, { siteId: 11, siteName: "Portal.Web", alias: "财务业务入口", tags: ["生产", "财务", "对外服务", "新标签"], expectedVersion: "portal-v1" });
  assert.equal(harness.node("site-annotation-editor").hidden, true); assert.equal(harness.run("state.siteAnnotationEditor"), null);
  assert.deepEqual(visibleSiteNames(harness), ["Portal.Web"]); assert.equal(harness.node("sites-body").children[0].children[0].children[0].textContent, "财务业务入口");
  assert.equal(harness.run('availableSiteTags().includes("新标签")'), true); assert.equal(harness.run("state.task"), null); assert.equal(harness.run("readHistory().length"), 0);
  assert.ok(harness.requests.every(request => !request.path.includes("operations")));
});

test("Version conflicts retain the draft and require a latest-version read before an explicit subsequent save", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness, "我的草稿"); const latest = { ...annotationEntries()[0], alias: "其他操作员", version: "portal-v2" }; let posts = 0;
  harness.annotationHandle = ({ body }) => {
    if (body && ++posts === 1) return { httpStatus: 409, body: { code: "SITE_ANNOTATION_CONFLICT" } };
    if (body) { Object.assign(latest, { alias: body.alias, tags: body.tags, version: "portal-v3" }); return { ...latest }; }
    return annotationEnvelope([latest, ...annotationEntries().slice(1)]);
  };
  await harness.run("saveSiteAnnotation()"); assert.equal(posts, 1); assert.equal(harness.node("annotation-alias").value, "我的草稿");
  assert.equal(harness.node("annotation-save").disabled, true); assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), "portal-v1");
  await harness.run("saveSiteAnnotation()"); assert.equal(posts, 1);
  await harness.node("annotation-reconcile").emit("click"); assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), "portal-v2"); assert.equal(harness.node("annotation-alias").value, "我的草稿");
  assert.equal(harness.node("annotation-save").disabled, false); assert.equal(posts, 1);
  await harness.run("saveSiteAnnotation()"); assert.equal(posts, 2); assert.equal(harness.node("site-annotation-editor").hidden, true);
  assert.equal(harness.requests.filter(request => request.method === "POST")[1].body.expectedVersion, "portal-v2");
});

test("A lost annotation POST response can succeed only through matching independent readback without another POST", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness); let persisted;
  harness.annotationHandle = ({ body }) => {
    if (body) { persisted = { siteId: body.siteId, siteName: body.siteName, alias: body.alias, tags: body.tags, version: "portal-v2" }; throw new Error("Lost response"); }
    return annotationEnvelope([persisted, ...annotationEntries().slice(1)]);
  };
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1); assert.equal(harness.requests.filter(request => request.method === "GET").length, 1);
  assert.equal(harness.node("site-annotation-editor").hidden, true); assert.match(harness.node("notice").textContent, /读回确认/);
});

test("HTTP 502 annotation outcome unknown performs only a read and keeps a differing draft blocked", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness, "待核对草稿");
  harness.annotationHandle = ({ body }) => body ? { httpStatus: 502, body: { code: "SITE_ANNOTATION_OUTCOME_UNKNOWN" } } : annotationEnvelope();
  await harness.run("saveSiteAnnotation()"); assert.deepEqual(harness.requests.map(request => request.method), ["POST", "GET"]);
  assert.equal(harness.node("annotation-alias").value, "待核对草稿"); assert.equal(harness.node("annotation-save").disabled, true);
  assert.equal(harness.node("site-annotation-editor").hidden, false); assert.match(harness.node("annotation-editor-error").textContent, /尚未确认/);
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.length, 2); assert.equal(harness.node("notice").hidden, true);
});

test("Unknown annotation results and failed readback preserve the draft, prevent resubmission and allow read-only reconciliation", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness); let persisted, reads = 0;
  harness.annotationHandle = ({ body }) => {
    if (body) { persisted = { siteId: body.siteId, siteName: body.siteName, alias: body.alias, tags: body.tags, version: "portal-v2" }; throw new Error("Lost response"); }
    if (++reads === 1) throw new Error("Read unavailable");
    return annotationEnvelope([persisted, ...annotationEntries().slice(1)]);
  };
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.node("site-annotation-editor").hidden, false); assert.equal(harness.node("annotation-alias").value, "新的业务门户");
  assert.equal(harness.node("annotation-save").disabled, true); assert.match(harness.node("annotation-editor-error").textContent, /尚未确认/);
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
  await harness.node("annotation-reconcile").emit("click"); assert.equal(harness.node("site-annotation-editor").hidden, true); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1); assert.equal(reads, 2);
});

test("A reported annotation save never claims success when the persisted readback is different", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness);
  harness.annotationHandle = ({ body }) => body ? { siteId: body.siteId, siteName: body.siteName, alias: body.alias, tags: body.tags, version: "portal-v2" }
    : annotationEnvelope([{ ...annotationEntries()[0], alias: "后来更新", version: "portal-v3" }, ...annotationEntries().slice(1)]);
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.node("annotation-alias").value, "新的业务门户"); assert.equal(harness.node("annotation-save").disabled, true);
  assert.equal(harness.node("site-annotation-editor").hidden, false); assert.match(harness.node("annotation-editor-error").textContent, /不一致/); assert.equal(harness.node("notice").hidden, true);
  await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.filter(request => request.method === "POST").length, 1);
});

test("Optional annotation configuration and service failures remain separate from valid IIS rows and actions", async () => {
  for (const code of ["SITE_ANNOTATIONS_NOT_CONFIGURED", "SITE_ANNOTATIONS_UNAVAILABLE"]) {
    const harness = await annotationBrowser(); harness.annotationHandle = () => ({ httpStatus: 503, body: { code } });
    await harness.run("refreshSiteAnnotations()"); assert.equal(harness.run("state.snapshotStatus"), "ready"); assert.equal(harness.node("sites-body").children.length, 3);
    assert.equal(harness.node("sites-body").children[0].children[0].children[0].textContent, "Portal.Web");
    assert.equal(harness.node("site-annotation-edit-11").disabled, true); assert.equal(harness.node("sites-body").children[0].children[4].children[1].disabled, false);
    assert.equal(harness.node("site-tags-toggle").disabled, true); assert.equal(harness.node("site-annotation-status-area").hidden, false);
    harness.run("openSiteAnnotationEditor(state.snapshot.sites[0])"); assert.equal(harness.run("state.siteAnnotationEditor"), null);
  }
});

test("Saving locks Agent switches and cancellation, and a forced switch discards all earlier responses and draft state", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness); let release;
  harness.annotationHandle = ({ body }) => new Promise(resolve => { release = () => resolve({ siteId: body.siteId, siteName: body.siteName, alias: body.alias, tags: body.tags, version: "portal-v2" }); });
  const saving = harness.run("saveSiteAnnotation()"); assert.equal(harness.run("state.annotationBusy"), true);
  assert.equal(harness.node("server-row-222").disabled, true); assert.equal(harness.node("annotation-cancel").disabled, true); assert.equal(harness.node("annotation-save").disabled, true);
  await harness.node("server-row-222").emit("click"); assert.equal(harness.run("state.serverId"), "104");
  await harness.node("site-annotation-editor").emit("keydown", { key: "Escape" }); assert.equal(harness.node("site-annotation-editor").hidden, false);
  harness.run('selectServer("222")'); release(); await saving;
  assert.equal(harness.run("state.siteAnnotations"), null); assert.equal(harness.run("state.siteAnnotationEditor"), null); assert.equal(harness.run("state.annotationBusy"), false);
  assert.equal(harness.node("annotation-alias").value, ""); assert.equal(harness.node("site-annotation-editor").hidden, true); assert.equal(harness.requests.length, 1);
  assert.equal(harness.requests[0].path, "/api/v1/servers/104/site-annotations"); assert.equal(harness.node("notice").hidden, true);
});

test("Annotation reads reject malformed payloads, old revisions and responses belonging to another Agent", async () => {
  const harness = await annotationBrowser(); let release;
  harness.annotationHandle = () => new Promise(resolve => { release = resolve; }); const oldRead = harness.run("refreshSiteAnnotations()");
  harness.annotationHandle = () => annotationEnvelope([{ ...annotationEntries()[0], alias: "最新", version: "portal-v2" }]); await harness.run("refreshSiteAnnotations()");
  release(annotationEnvelope([{ ...annotationEntries()[0], alias: "过期" }])); await oldRead; assert.equal(harness.run("state.siteAnnotations.entries[0].alias"), "最新");
  harness.annotationHandle = () => annotationEnvelope(annotationEntries(), "222"); await harness.run("refreshSiteAnnotations()"); assert.equal(harness.run("state.siteAnnotationsStatus"), "failed");
  harness.annotationHandle = () => annotationEnvelope([{ ...annotationEntries()[0], tags: ["API", "api"] }]); await harness.run("refreshSiteAnnotations()"); assert.equal(harness.run("state.siteAnnotations"), null);
  harness.annotationHandle = () => new Promise(resolve => { release = resolve; }); const oldAgent = harness.run("refreshSiteAnnotations()"); harness.run('selectServer("222")'); release(annotationEnvelope()); await oldAgent;
  assert.equal(harness.run("state.siteAnnotations"), null); assert.equal(harness.run("state.siteAnnotationsStatus"), "idle");
});

test("Identical site names and reused IDs resolve annotations by both original identity fields", async () => {
  const harness = await annotationBrowser();
  harness.run('state.snapshot.sites[1].name="Portal.Web"; state.siteAnnotations.entries[1].siteName="Portal.Web"; state.siteAnnotations.entries[1].alias="第二个门户"; renderSnapshot();');
  assert.deepEqual(harness.node("sites-body").children.slice(0, 2).map(row => row.children[0].children[0].textContent), ["ERP 业务门户", "第二个门户"]);
  harness.run('state.snapshot.sites[0].name="Recreated.Web"; renderSnapshot();'); assert.equal(harness.node("sites-body").children[0].children[0].children[0].textContent, "Recreated.Web");
  harness.run("openSiteAnnotationEditor(state.snapshot.sites[0])"); assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), ""); assert.equal(harness.node("annotation-alias").value, "");
  harness.run('state.snapshot.sites[0].name="Changed.Again"'); await harness.run("saveSiteAnnotation()"); assert.equal(harness.requests.length, 0); assert.equal(harness.node("annotation-save").disabled, true);
  assert.match(harness.node("annotation-editor-error").textContent, /IIS 站点/);
});

test("A refreshed annotation version never silently replaces an open alias or tag draft", async () => {
  const harness = await annotationBrowser(); await editAnnotation(harness, "未保存草稿"); harness.run('addAnnotationTag("草稿标签")');
  harness.annotationHandle = () => annotationEnvelope([{ ...annotationEntries()[0], alias: "新持久值", version: "portal-v2", tags: ["外部修改"] }]);
  await harness.run("refreshSiteAnnotations()"); assert.equal(harness.node("annotation-alias").value, "未保存草稿"); assert.equal(harness.run('state.siteAnnotationEditor.tags.includes("草稿标签")'), true);
  assert.equal(harness.run("state.siteAnnotationEditor.expectedVersion"), "portal-v1"); assert.equal(harness.node("annotation-save").disabled, true);
});

test("Resource rows expose exact site application and virtual-directory edit targets", async () => {
  const harness = await topologyBrowser(); await harness.node("site-details-toggle-11").emit("click");
  const siteRow = harness.node("sites-body").children.find(row => row.dataset.siteId === "11");
  const siteEdit = descendants(siteRow).find(node => node.dataset?.maintenanceEdit === "site.update");
  const detail = harness.node("site-details-11"), appRow = nodesWithTag(detail, "TBODY")[0].children[0], directoryRow = nodesWithTag(detail, "TBODY")[1].children[0];
  const appEdit = descendants(appRow).find(node => node.dataset?.maintenanceEdit);
  const directoryEdit = descendants(directoryRow).find(node => node.dataset?.maintenanceEdit === "virtualDirectory.update");
  const ownerEdit = descendants(directoryRow).find(node => node.textContent === "修改所属应用的应用池");
  assert.equal(ownerEdit, undefined, "Pool selection belongs inside the single edit entry.");
  assert.equal(descendants(directoryRow).filter(node => node.dataset?.maintenanceEdit).length, 1);
  for (const [button, action, application, directory, pool] of [[siteEdit, "site.update", null, null, null], [appEdit, "application.update", "/api", null, null],
    [directoryEdit, "virtualDirectory.update", "/api", "/assets", null]]) {
    assert.ok(button, "The resource must expose its own edit action.");
    const query = new URL(button.dataset.maintenanceUrl, "https://console.example.test").searchParams;
    assert.equal(query.get("server"), "104"); assert.equal(query.get("site"), "Portal"); assert.equal(query.get("action"), action);
    assert.equal(query.get("application"), application); assert.equal(query.get("directory"), directory); assert.equal(query.get("pool"), pool);
    assert.equal(button.disabled, false);
  }
  assert.equal(harness.requests.length, 0, "Rendering edit entries sends no operation.");
});

test("Edit navigation encodes names and keeps the selected root-owned directory in its editor", async () => {
  const harness = await topologyBrowser();
  const button = harness.run('maintenanceEditButton({name:"站点 & 1"}, "/子应用 & api", "/资料 & icons")');
  await button.emit("click");
  const query = new URL(harness.run("location.href"), "https://console.example.test").searchParams;
  assert.equal(query.get("site"), "站点 & 1"); assert.equal(query.get("application"), "/子应用 & api"); assert.equal(query.get("directory"), "/资料 & icons");
  const root = harness.run('maintenanceEditButton({name:"Portal"}, "/", "/assets")');
  const target = new URL(root.dataset.maintenanceUrl, "https://console.example.test").searchParams;
  assert.equal(target.get("action"), "virtualDirectory.update"); assert.equal(target.get("application"), "/");
  assert.equal(target.get("directory"), "/assets"); assert.equal(target.get("pool"), null);
  assert.equal(harness.requests.length, 0);
});

test("Resource edit controls honor configure permission and unresolved operation locks", async () => {
  const harness = await topologyBrowser();
  for (const script of ['state.permissions={servers:[{serverId:"104",read:true,configure:false}]}',
    'state.permissions={servers:[{serverId:"104",read:true,configure:true}]};state.task={state:"unknown",serverId:"104",request:{operationId:"10000000-0000-4000-8000-000000000001",type:"site.stop",target:{siteName:"Portal"}}}']) {
    harness.run(`${script};renderSnapshot();`);
    const edits = descendants(harness.node("sites-body")).filter(node => node.dataset?.maintenanceEdit);
    assert.ok(edits.length); assert.ok(edits.every(button => button.disabled));
    await edits[0].emit("click"); assert.equal(harness.run("location.href"), undefined);
  }
});
