"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const script = join(__dirname, "../src/IISManager.Console/wwwroot/servers.js");
const source = readFileSync(script, "utf8"), pure = require(script);
const tick = () => new Promise(resolve => setImmediate(resolve));
const identity = { appId: 1001, userId: "synthetic-admin", account: "synthetic", displayName: "Synthetic admin" };
const item = { id: "alpha", name: "Alpha", group: "项目", environment: "测试", agentUrl: "http://synthetic.invalid:7444/", hasAccessToken: true, serverCertificateThumbprint: "", restartRequired: false };
const policy = { enabled: false, administrators: [{ appId: identity.appId, userId: identity.userId }], grants: [] };
const draft = (extra = {}) => ({ id: "alpha", name: "Alpha", group: "", environment: "", agentUrl: item.agentUrl, accessToken: "", serverCertificateThumbprint: "", ...extra });
const preview = { revision: "r1", fingerprint: "synthetic-preview", changes: [{ field: "名称", before: "Alpha", after: "New name" }], requiresRestart: true, credentialsChanged: false };

async function browser(administration = true) {
  const nodes = new Map(), all = [], requests = [];
  function element(tag = "div") {
    const events = new Map();
    const result = { tagName: tag.toUpperCase(), value: "", checked: false, selected: false, textContent: "", hidden: false, disabled: false, readOnly: false, open: false, children: [], dataset: {},
      append(...children) { children.forEach(child => { child.parent = this; }); this.children.push(...children); },
      replaceChildren(...children) { this.children = []; this.append(...children); },
      remove() { if (this.parent) this.parent.children = this.parent.children.filter(item => item !== this); },
      get selectedOptions() { return this.children.filter(option => option.selected); },
      addEventListener(name, fn) { events.set(name, fn); },
      async emit(name) { await events.get(name)?.({ preventDefault() {}, target: this }); await tick(); },
      showModal() { this.open = true; }, close() { this.open = false; events.get("close")?.(); }, focus() {},
      querySelectorAll(selector) { return descendants(this).filter(node => selector === "input, select" && ["INPUT", "SELECT"].includes(node.tagName)); }
    }; all.push(result); return result;
  }
  function descendants(root) { return root.children.flatMap(child => [child, ...descendants(child)]); }
  const document = { createElement: element,
    getElementById(id) { if (!nodes.has(id)) { const node = element(); node.id = id; nodes.set(id, node); } return nodes.get(id); },
    querySelectorAll(selector) { const key = selector === "[data-admin-action]" ? "adminAction" : selector === "[data-remove-row]" ? "removeRow" : null; return key ? all.filter(node => node.dataset[key]) : []; }
  };
  const h = { nodes, all, requests, custom: null, administration, revision: "r1", node: id => document.getElementById(id) };
  const context = vm.createContext({ document, URL, Date, IISManagerAuth: { unauthorized() {}, render() {} },
    async fetch(path, options) {
      requests.push({ path, ...options });
      if (h.custom) { const result = await h.custom(path, options); if (result !== undefined) return result; }
      const data = path === "/api/v1/session" ? { csrfToken: "synthetic-csrf", user: identity } : path === "/api/v1/permissions" ? { administration: h.administration, servers: [] } :
        path === "/api/v1/administration/servers" ? { revision: h.revision, servers: [item], pendingRestart: false, canPersist: true, protection: "windows-dpapi-current-user-v1" } :
        path === "/api/v1/administration/policy" ? { revision: h.revision, policy, canPersist: true } :
        path.endsWith("/preview") ? preview : path.endsWith("/test") ? { machineName: "Synthetic", agentVersion: "1.0", protocolVersion: "1.0" } : (() => { h.revision = "r2"; return { revision: h.revision }; })();
      return { ok: true, status: 200, json: async () => data };
    }
  });
  vm.runInContext(source, context); await tick(); await tick(); return h;
}
async function edit(h) {
  const button = h.node("server-admin-body").children[0].children[4].children[0]; await button.emit("click");
  h.node("managed-name").value = "New name"; await h.node("managed-name").emit("input");
}

test("server search includes grouping and URLs without requiring secret metadata", () => {
  for (const query of ["ALPHA", "项目", "测试", "synthetic.invalid"]) assert.equal(pure.serverMatches(item, query), true);
  assert.equal(pure.serverMatches(item, "missing"), false);
});
test("root URL validation rejects credential query fragment path scheme and zero port", () => {
  for (const agentUrl of ["file:///tmp/", "http://user:password@synthetic.invalid/", "http://synthetic.invalid/?secret=x", "http://synthetic.invalid/#x", "http://synthetic.invalid/api", "http://synthetic.invalid:0/", "http://synthetic.invalid\\x/"])
    assert.notEqual(pure.validateServer(draft({ agentUrl, accessToken: "A".repeat(64) }), item), "");
});
test("blank saved HTTP token is preserved only for the same destination", () => {
  assert.equal(pure.validateServer(draft(), item), "");
  assert.match(pure.validateServer(draft({ agentUrl: "http://other.invalid:7444/" }), item), /重新输入/);
  assert.match(pure.validateServer(draft({ agentUrl: "http://synthetic.invalid:7445/" }), item), /重新输入/);
  assert.equal(pure.validateServer(draft({ agentUrl: "http://other.invalid:7444/", accessToken: "B".repeat(64) }), item), "");
});
test("new HTTP and HTTPS credentials must meet their distinct contracts", () => {
  assert.notEqual(pure.validateServer(draft(), null), "");
  assert.notEqual(pure.validateServer(draft({ accessToken: "bad" }), item), "");
  assert.notEqual(pure.validateServer(draft({ agentUrl: "https://synthetic.invalid/" }), item), "");
  assert.equal(pure.validateServer(draft({ agentUrl: "https://synthetic.invalid/", serverCertificateThumbprint: "C".repeat(40) }), item), "");
});
test("policy validation retains current administrator and separates action grants", () => {
  assert.equal(pure.validatePolicy(policy, identity), "");
  assert.match(pure.validatePolicy({ ...policy, administrators: [] }, identity), /保留当前/);
  assert.match(pure.validatePolicy({ ...policy, grants: [{ appId: 1001, userId: "viewer", serverIds: ["alpha"], read: false, configure: true }] }, identity), /查看权限/);
  assert.equal(pure.permissionText({ read: true, operate: false, configure: true }), "查看、修改配置");
});
test("non administrators never request protected connection or policy metadata", async () => {
  const h = await browser(false);
  assert.equal(h.node("admin-content").hidden, true); assert.equal(h.node("admin-unavailable").hidden, false);
  assert.equal(h.requests.some(request => request.path.includes("/administration/")), false);
});
test("editing starts with a blank secret and an immutable existing identifier", async () => {
  const h = await browser(); await edit(h);
  assert.equal(h.node("managed-id").readOnly, true); assert.equal(h.node("managed-token").value, "");
  assert.equal(h.node("server-save").disabled, true);
  await h.node("server-form").emit("submit"); assert.equal(h.requests.some(request => request.path.endsWith("/save")), false);
});
test("preview uses session CSRF and only an explicit confirmation writes once", async () => {
  const h = await browser(); await edit(h); await h.node("server-preview-button").emit("click");
  assert.equal(h.node("server-save").disabled, false); assert.equal(h.requests.some(request => request.path.endsWith("/save")), false);
  const request = h.requests.find(request => request.path.endsWith("/preview")); assert.equal(request.headers["X-IISManager-CSRF"], "synthetic-csrf");
  assert.equal(JSON.parse(request.body).accessToken, "");
  await h.node("server-form").emit("submit");
  const writes = h.requests.filter(request => request.path.endsWith("/save")); assert.equal(writes.length, 1);
  assert.equal(JSON.parse(writes[0].body).expectedFingerprint, preview.fingerprint); assert.equal(h.node("managed-token").value, "");
});
test("editing after preview invalidates confirmation", async () => {
  const h = await browser(); await edit(h); await h.node("server-preview-button").emit("click");
  h.node("managed-name").value = "Another name"; await h.node("managed-name").emit("input");
  assert.equal(h.node("server-save").disabled, true); await h.node("server-form").emit("submit");
  assert.equal(h.requests.some(request => request.path.endsWith("/save")), false);
});
test("a lost save receipt cannot replay and requires a fresh independent read", async () => {
  const h = await browser(); await edit(h); await h.node("server-preview-button").emit("click");
  h.node("managed-token").value = "A".repeat(64);
  h.custom = path => { if (path.endsWith("/save")) throw new Error("synthetic transport loss"); };
  await h.node("server-form").emit("submit"); await h.node("server-form").emit("submit");
  assert.equal(h.requests.filter(request => request.path.endsWith("/save")).length, 1);
  assert.equal(h.node("server-save").disabled, true); assert.equal(h.node("server-reconcile").hidden, false);
  h.custom = null; await h.node("server-reconcile").emit("click");
  assert.equal(h.node("server-editor").open, false); assert.equal(h.node("managed-token").value, "");
  assert.equal(h.requests.filter(request => request.path === "/api/v1/administration/servers").length, 2);
});
test("a generic server 500 during save also locks further writes", async () => {
  const h = await browser(); await edit(h); await h.node("server-preview-button").emit("click");
  h.custom = path => path.endsWith("/save") ? { ok: false, status: 500, json: async () => ({ code: "INTERNAL_ERROR", message: "保存状态需核对" }) } : undefined;
  await h.node("server-form").emit("submit"); await h.node("server-form").emit("submit");
  assert.equal(h.requests.filter(request => request.path.endsWith("/save")).length, 1); assert.equal(h.node("server-reconcile").hidden, false);
});
test("connection tests call only the explicit info-test endpoint and never save", async () => {
  const h = await browser(); await edit(h); await h.node("server-test").emit("click");
  assert.equal(h.requests.filter(request => request.path.endsWith("/test")).length, 1);
  assert.equal(h.requests.some(request => request.path.endsWith("/save") || request.path.includes("operations")), false);
});
test("permission revocation hides previously loaded administrator data", async () => {
  const h = await browser(); h.administration = false; await h.node("admin-refresh").emit("click");
  assert.equal(h.node("admin-content").hidden, true); assert.equal(h.node("server-admin-body").children.length, 0); assert.equal(h.node("policy-body").children.length, 0);
});
test("server supplied labels and previews remain text and no secrets enter browser storage", async () => {
  const h = await browser(); await edit(h);
  h.custom = path => path.endsWith("/preview") ? { ok: true, status: 200, json: async () => ({ ...preview, changes: [{ field: "<img>", before: "<script>", after: "<img onerror=alert(1)>" }] }) } : undefined;
  await h.node("server-preview-button").emit("click");
  assert.equal(h.node("server-changes").children[0].children[2].textContent, "新：<img onerror=alert(1)>");
  assert.equal(/innerHTML|localStorage|sessionStorage|console\.log/.test(source), false);
});
