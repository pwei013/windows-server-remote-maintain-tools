"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const vm = require("node:vm");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const source = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/operations.js"), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));

async function browser() {
  const nodes = new Map(), requests = [];
  function node(tag = "div") {
    const listeners = new Map();
    return { tagName: tag, value: "", children: [], attributes: {}, dataset: {}, hidden: false,
      append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; },
      setAttribute(name, value) { this.attributes[name] = value; },
      addEventListener(name, fn) { listeners.set(name, fn); },
      async emit(name, event = {}) { await listeners.get(name)?.({preventDefault() {}, ...event}); await tick(); },
      showModal() { this.open = true; }, close() { this.open = false; }
    };
  }
  const harness = { requests, handle: null };
  const document = { visibilityState: "visible", addEventListener() {}, createElement: node, getElementById(id) { if (!nodes.has(id)) nodes.set(id, node()); return nodes.get(id); } };
  const context = vm.createContext({ document, URLSearchParams, location: { search: "" }, setTimeout() { return 1; }, clearTimeout() {},
    async fetch(path, options) {
      assert.ok(!options.method || options.method === "GET", "History must never POST operations");
      if (path === "/api/v1/servers") return { ok:true, json:async () => [{id:"a", name:"Agent A", hostName:"server-a"},{id:"b", name:"Agent B", hostName:"server-b"}] };
      const taskPath = /^\/api\/v1\/servers\/([^/]+)\/operations\/([^/?]+)$/.exec(path);
      if (taskPath) return { ok:true, json:async () => ({ serverId:decodeURIComponent(taskPath[1]),request:{operationId:decodeURIComponent(taskPath[2])},state:"succeeded",healthFollowUp:null }) };
      requests.push(path); const parameters = new URLSearchParams(path.split("?")[1]);
      const result = harness.handle ? await harness.handle(parameters) : {items:[],total:0,page:Number(parameters.get("page")),pageSize:Number(parameters.get("pageSize"))};
      return {ok:!result.failure,status:result.failure || 200,json:async () => result};
    }
  });
  harness.run = code => vm.runInContext(code, context); harness.node = id => document.getElementById(id);
  harness.node("records-size").value = "20"; harness.run(source); await tick(); requests.length = 0; return harness;
}
function row(id = "00000000-0000-0000-0000-000000000001") {
  return {operationId:id,serverId:"a",type:"applicationPool.recycle",target:"Orders",state:"succeeded",acceptedAt:"2026-10-06T10:00:00Z",updatedAt:"2026-10-06T10:00:02Z",errorCode:null};
}

test("History and workbench pages load their matching content version and retain the operator field", () => {
  const crypto = require("node:crypto");
  for (const [page, script] of [["operations.html", "operations.js"], ["index.html", "app.js"]]) {
    const html = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot", page), "utf8");
    const bytes = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot", script));
    const version = crypto.createHash("sha256").update(bytes).digest("hex").slice(0, 16);
    assert.ok(html.includes(`src="/${script}?v=${version}" defer`), `${page} must request its current script`);
    assert.ok(html.includes(page === "operations.html" ? 'id="record-operator-column"' : 'id="task-operator"'));
  }
});

test("History row columns align with the seven page headings and opening details retains the recorded operator", async () => {
  const harness = await browser(), item = {...row(),operatorName:" 张工 ",persistenceState:"synced"};
  harness.handle = () => ({items:[item],total:1,page:1,pageSize:20}); await harness.run("queryRecords(1)");
  const html = readFileSync(join(__dirname, "../src/IISManager.Console/wwwroot/operations.html"), "utf8");
  const headings = [...html.match(/<thead>(.*?)<\/thead>/s)[1].matchAll(/<th\b[^>]*>(.*?)<\/th>/g)].map(match => match[1]);
  assert.deepEqual(headings, ["操作 / 目标", "Agent 服务器", "操作人", "执行状态", "平台保存", "提交 / 最近核对", "详情"]);
  const cells = harness.node("records-body").children[0].children;
  assert.equal(cells.length, headings.length); assert.equal(cells[2].textContent, "张工");
  assert.equal(cells[2].attributes.headers, "record-operator-column");
  assert.equal(cells[3].children[0].textContent, "执行成功"); assert.equal(cells[4].textContent, "已保存");
  await cells[6].children[0].emit("click");
  assert.equal(harness.node("record-dialog").open, true);
  assert.equal(harness.node("record-details").children.find(node => node.id === "record-operator").textContent, "张工");
  assert.equal(harness.node("record-operator-note").hidden, true);
});

test("Missing historical operators never reuse another record's name and explain the missing audit value", async () => {
  const harness = await browser();
  for (const name of ["王工", null, "", "  ", undefined]) {
    harness.run(`openRecord(${JSON.stringify({...row(),operatorName:name})});`);
    const actor = harness.node("record-details").children.find(node => node.id === "record-operator");
    assert.equal(actor.textContent, name?.trim() || "未记录");
    assert.equal(harness.node("record-operator-note").hidden, !!name?.trim());
  }
});

test("Site lifecycle history supports each action filter and its label", async () => {
  const harness = await browser();
  for (const [type,title] of [["site.start","启动站点"],["site.stop","停止站点"],["site.restart","重新启动站点"]]) {
    const item={...row(),type,target:"业务门户"}; harness.node("records-type").value=type;
    harness.handle=parameters=>({items:[item],total:1,page:Number(parameters.get("page")),pageSize:20});
    await harness.node("records-form").emit("submit");
    assert.equal(new URLSearchParams(harness.requests.at(-1).split("?")[1]).get("type"),type);
    harness.run(`openRecord(${JSON.stringify(item)});`);
    assert.equal(harness.node("record-dialog-title").textContent,`${title} · 业务门户`);
    assert.equal(harness.node("record-query-link").href,`/?server=a&operation=${item.operationId}#task-section`);
  }
});

test("Combined conditions reset pagination and preserve the criteria on later pages", async () => {
  const harness = await browser();
  harness.node("records-search").value = " Orders "; harness.node("records-type").value = "applicationPool.recycle"; harness.node("records-state").value = "succeeded";
  harness.node("records-from").value = "2026-10-06T08:00"; harness.node("records-to").value = "2026-10-06T20:00";
  harness.run('records.serverId="a";');
  harness.handle = parameters => ({items:[row()],total:45,page:Number(parameters.get("page")),pageSize:20});
  await harness.node("records-form").emit("submit");
  const first = new URLSearchParams(harness.requests[0].split("?")[1]);
  assert.equal(first.get("search"), "Orders"); assert.equal(first.get("serverId"), "a"); assert.equal(first.get("page"), "1");
  assert.equal(first.get("type"), "applicationPool.recycle"); assert.equal(first.get("state"), "succeeded"); assert.match(first.get("from"), /Z$/);
  await harness.node("records-next").emit("click");
  const next = new URLSearchParams(harness.requests.at(-1).split("?")[1]); assert.equal(next.get("page"), "2"); assert.equal(next.get("search"), "Orders");
  assert.equal(harness.node("records-page-info").textContent, "第 2 / 3 页");
});

test("Time range reversal is rejected before any request", async () => {
  const harness = await browser(); harness.node("records-from").value = "2026-10-06T20:00"; harness.node("records-to").value = "2026-10-06T08:00";
  await harness.node("records-form").emit("submit"); assert.equal(harness.requests.length, 0); assert.match(harness.node("records-error").textContent, /不能晚于/);
});

test("Changing Agent during an outstanding query isolates stale results", async () => {
  const harness = await browser(); let release;
  harness.handle = parameters => parameters.get("serverId") === "a" ? new Promise(resolve => {release = resolve;}) : {items:[{...row(),serverId:"b",target:"Reports"}],total:1,page:1,pageSize:20};
  const old = harness.run('queryRecords(1,{serverId:"a"});');
  await harness.run('queryRecords(1,{serverId:"b"});');
  release({items:[row()],total:40,page:1,pageSize:20}); await old;
  assert.equal(harness.node("records-body").children[0].children[0].children[0].textContent, "Reports");
  assert.equal(harness.run("records.query.serverId"), "b"); assert.equal(harness.node("records-next").disabled, true);
});

test("When a page shrinks, query the last available page using the same filters", async () => {
  const harness = await browser();
  harness.handle = parameters => ({items:Number(parameters.get("page")) === 2 ? [row()] : [], total:21,page:Number(parameters.get("page")),pageSize:20});
  await harness.run('queryRecords(3,{serverId:"a",search:"Orders"});');
  assert.equal(harness.run("records.page"), 2); assert.equal(harness.requests.length, 2);
  assert.match(harness.requests[1], /search=Orders/); assert.equal(harness.node("records-next").disabled, true);
});

test("A failed query clears stale rows and retries the attempted criteria", async () => {
  const harness = await browser(); harness.handle = () => ({failure:503,code:"CONSOLE_LEDGER_UNHEALTHY",message:"账本读取失败"});
  await harness.run('queryRecords(1,{serverId:"b",state:"failed"});');
  assert.equal(harness.node("records-body").children.length, 0); assert.equal(harness.node("records-error").hidden, false);
  await harness.node("records-refresh").emit("click"); assert.match(harness.requests.at(-1), /serverId=b/); assert.match(harness.requests.at(-1), /state=failed/);
});

test("History details use safe text and the exact original operation identifier", async () => {
  const harness = await browser(); const item = row(); item.target = "<img src=x onerror=alert(1)>";
  harness.run(`openRecord(${JSON.stringify(item)});`);
  assert.ok(harness.node("record-dialog-title").textContent.includes(item.target));
  assert.equal(harness.node("record-query-link").href, `/?server=a&operation=${item.operationId}#task-section`);
  harness.run(`openRecord(${JSON.stringify({...item,serverId:"removed"})});`); assert.equal(harness.node("record-query-link").hidden, true);
});

test("Reset clears all conditions, restores all Agents, and returns to page one", async () => {
  const harness = await browser(); harness.node("records-search").value = "Orders"; harness.node("records-state").value = "failed"; harness.run('records.serverId="a";records.page=3;');
  await harness.node("records-reset").emit("click"); assert.equal(harness.node("records-search").value, ""); assert.equal(harness.run("records.serverId"), "");
  assert.equal(harness.requests.at(-1), "/api/v1/operations?page=1&pageSize=20");
});

test("Windows service history supports action filters and links to the exact service resource", async () => {
  const harness = await browser(); const item = {...row(),type:"windowsService.updateStartType",target:"Orders #Worker"};
  harness.node("records-type").value = item.type; harness.handle = parameters => ({items:[item],total:1,page:Number(parameters.get("page")),pageSize:20});
  await harness.node("records-form").emit("submit"); assert.equal(new URLSearchParams(harness.requests[0].split("?")[1]).get("type"), item.type);
  harness.run(`openRecord(${JSON.stringify(item)});`);
  assert.equal(harness.node("record-dialog-title").textContent, "修改服务启动类型 · Orders #Worker");
  assert.equal(harness.node("record-target-link").hidden, false);
  assert.equal(harness.node("record-target-link").href, "/?server=a&resource=services&service=Orders%20%23Worker");
  assert.equal(harness.node("record-query-link").href, `/?server=a&operation=${item.operationId}&resource=services&service=Orders%20%23Worker#task-section`);
  harness.run(`openRecord(${JSON.stringify({...item,serverId:"removed"})});`); assert.equal(harness.node("record-target-link").hidden, true);
});

test("Operator names appear as safe text in history rows and details, including legacy records", async () => {
  const harness = await browser(), name = "<img src=x onerror=alert(1)> 王工";
  const items = [{...row(),operatorName:name,persistenceState:"synced"}, {...row("00000000-0000-0000-0000-000000000002"),operatorName:null}, {...row("00000000-0000-0000-0000-000000000003")}];
  harness.handle = () => ({items,total:items.length,page:1,pageSize:20});
  await harness.run("queryRecords(1);");
  const rows = harness.node("records-body").children;
  assert.equal(rows[0].children.length, 7);
  assert.equal(rows[0].children[2].textContent, name);
  assert.equal(rows[0].children[2].children.length, 0, "Names cannot create DOM elements");
  assert.equal(rows[0].children[4].textContent, "已保存");
  assert.equal(rows[1].children[2].textContent, "未记录");
  assert.equal(rows[2].children[2].textContent, "未记录");
  for (const item of items) {
    harness.run(`openRecord(${JSON.stringify(item)});`);
    const details = harness.node("record-details").children, labelIndex = details.findIndex(item => item.textContent === "操作人");
    assert.equal(details[labelIndex + 1].textContent, item.operatorName || "未记录");
    assert.equal(details[labelIndex + 1].children.length, 0);
  }
});

test("History shows platform persistence independently of Agent execution state", async () => {
  const harness = await browser();
  for (const [state, label] of [["synced", "已保存"], ["pending", "待保存"], ["unknown", "待核对"], ["notConfigured", "未启用"], [null, "未记录"], ["constructor", "未记录"]]) {
    const item = {...row(),persistenceState:state}; harness.run(`renderRows([${JSON.stringify(item)}]); openRecord(${JSON.stringify(item)});`);
    const cells = harness.node("records-body").children[0].children;
    assert.equal(cells[3].children[0].textContent, "执行成功");
    assert.equal(cells[4].textContent, label);
    const details = harness.node("record-details").children, index = details.findIndex(item => item.textContent === "平台保存");
    assert.equal(details[index + 1].textContent, label);
  }
});

test("Operator search preserves the server-side search contract and rejects malformed audit fields", async () => {
  const harness = await browser(); harness.node("records-search").value = " 王工 ";
  harness.handle = () => ({items:[{...row(),operatorName:"王工",persistenceState:"synced"}],total:1,page:1,pageSize:20});
  await harness.node("records-form").emit("submit");
  assert.equal(new URLSearchParams(harness.requests[0].split("?")[1]).get("search"), "王工");
  for (const auditField of [{operatorName:{}}, {persistenceState:{}}]) {
    harness.handle = () => ({items:[{...row(),...auditField}],total:1,page:1,pageSize:20});
    await harness.run("queryRecords(1);");
    assert.equal(harness.node("records-body").children.length, 0);
    assert.match(harness.node("records-error").textContent, /响应不完整/);
  }
});
