"use strict";

const byId = id => document.getElementById(id);
const records = { servers: [], serverId: "", query: {}, page: 1, pageSize: 20, total: 0, revision: 0, loading: false };
const recordDetail = { revision: 0, row: null, task: null, loading: false, timer: null, readError: false };
const actionLabels = { "applicationPool.start": "启动应用程序池", "applicationPool.stop": "停止应用程序池", "applicationPool.recycle": "回收应用程序池", "applicationPool.create": "新建应用程序池", "applicationPool.update": "编辑应用程序池", "site.create": "新建网站", "site.update": "编辑站点配置", "application.create": "新建子应用", "application.update": "编辑子应用", "virtualDirectory.create": "新建虚拟目录", "virtualDirectory.update": "编辑虚拟目录", "site.start": "启动站点", "site.stop": "停止站点", "site.restart": "重新启动站点", "windowsService.start": "启动 Windows 服务", "windowsService.stop": "停止 Windows 服务", "windowsService.restart": "重新启动 Windows 服务", "windowsService.create": "创建 Windows 服务", "windowsService.updateStartType": "修改服务启动类型" };
const statusLabels = { queued: "等待执行", running: "执行中", succeeded: "执行成功", failed: "执行失败", partial: "部分完成", unknown: "结果未知", expired: "已过期" };
const persistenceLabels = { synced: "已保存", pending: "待保存", unknown: "待核对", notConfigured: "未启用" };
function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
function date(value) { return value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—"; }
function operatorName(value) { return typeof value === "string" && value.trim() ? value.trim() : "未记录"; }
function persistenceLabel(value) { return Object.prototype.hasOwnProperty.call(persistenceLabels, value) ? persistenceLabels[value] : "未记录"; }
function normalized(value) { return String(value || "").trim().toLowerCase(); }
function showError(message) { byId("records-error").textContent = message; byId("records-error").hidden = !message; }
async function api(path) {
  const response = await fetch(path, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
  let data;
  try { data = await response.json(); } catch { throw new Error("无法识别操作记录响应，请稍后重新查询。"); }
  globalThis.IISManagerAuth?.unauthorized(response, data);
  if (!response.ok) throw new Error(`${data.message || "操作记录查询失败"} (${data.code || response.status})`);
  return data;
}
function serverName(id) { return records.servers.find(server => server.id === id)?.name || `${id}（未配置）`; }
function renderServers() {
  const list = byId("server-list"), query = normalized(byId("server-search").value); list.replaceChildren();
  byId("server-count").textContent = records.servers.length;
  const choices = [{ id: "", name: "全部服务器", hostName: "包含已移除服务器的历史" }, ...records.servers];
  if (records.serverId && !records.servers.some(server => server.id === records.serverId)) choices.push({ id: records.serverId, name: serverName(records.serverId), hostName: "保留的历史服务器" });
  choices.filter(server => !server.id || [server.id, server.name, server.hostName].some(value => normalized(value).includes(query))).forEach(server => {
    const button = element("button", undefined, `server-button${server.id === records.serverId ? " selected" : ""}`); button.type = "button";
    button.setAttribute("aria-current", String(server.id === records.serverId)); button.dataset.recordsServer = server.id;
    const icon = element("img", undefined, "ui-icon"); icon.src = "/assets/fluent/server.svg"; icon.alt = "";
    const label = element("span", undefined, "server-label"); label.append(element("strong", server.name), element("small", server.hostName || server.id)); button.append(icon, label);
    button.addEventListener("click", () => {
      records.serverId = server.id; renderServers();
      void queryRecords(1, { ...records.query, serverId: server.id });
    }); list.append(button);
  });
}
function readCriteria() {
  const fromValue = byId("records-from").value, toValue = byId("records-to").value;
  const from = fromValue ? new Date(fromValue) : null, to = toValue ? new Date(toValue) : null;
  if ((from && !Number.isFinite(from.getTime())) || (to && !Number.isFinite(to.getTime()))) throw new Error("请填写有效的提交时间。");
  if (from && to && from > to) throw new Error("提交时间起不能晚于提交时间止。");
  return { serverId: records.serverId, search: byId("records-search").value.trim(), type: byId("records-type").value, state: byId("records-state").value, from: from?.toISOString() || "", to: to?.toISOString() || "" };
}
function queryParameters(query, page, pageSize) {
  const parameters = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) if (value) parameters.set(key, value);
  parameters.set("page", String(page)); parameters.set("pageSize", String(pageSize)); return parameters;
}
function renderPagination() {
  const last = Math.max(1, Math.ceil(records.total / records.pageSize));
  byId("records-prev").disabled = records.loading || records.page <= 1;
  byId("records-next").disabled = records.loading || records.page >= last;
  byId("records-page-info").textContent = `第 ${records.page} / ${last} 页`;
  const pages = byId("records-pages"); pages.replaceChildren();
  const start = Math.max(1, Math.min(records.page - 2, last - 4));
  for (let number = start; number <= Math.min(last, start + 4); number++) {
    const button = element("button", String(number), number === records.page ? "" : "secondary"); button.type = "button"; button.disabled = records.loading;
    button.setAttribute("aria-label", `第 ${number} 页`); if (number === records.page) button.setAttribute("aria-current", "page");
    button.addEventListener("click", () => queryRecords(number)); pages.append(button);
  }
}
function openRecord(row) {
  clearTimeout(recordDetail.timer); recordDetail.revision++; recordDetail.row = row; recordDetail.task = null; recordDetail.loading = false; recordDetail.readError = false;
  byId("record-dialog-title").textContent = `${actionLabels[row.type] || row.type} · ${row.target || "—"}`;
  const detail = byId("record-details"); detail.replaceChildren();
  [["操作编号", row.operationId], ["服务器", serverName(row.serverId)], ["操作人", operatorName(row.operatorName), "record-operator"], ["目标", row.target], ["执行状态", statusLabels[row.state] || row.state, "record-state"], ["平台保存", persistenceLabel(row.persistenceState)], ["提交时间", date(row.acceptedAt)], ["最近核对", date(row.updatedAt), "record-updated"], ["结果代码", row.errorCode || "—"]].forEach(([label, value, id]) => {
    const content = element("dd", value || "—"); if (id) content.id = id;
    detail.append(element("dt", label), content);
  });
  const operatorNote = byId("record-operator-note");
  if (operatorNote) operatorNote.hidden = typeof row.operatorName === "string" && !!row.operatorName.trim();
  const link = byId("record-query-link"), available = records.servers.some(server => server.id === row.serverId);
  link.hidden = !available; link.href = `/?server=${encodeURIComponent(row.serverId)}&operation=${encodeURIComponent(row.operationId)}#task-section`;
  const service = row.type.startsWith("windowsService."), targetLink = byId("record-target-link");
  targetLink.hidden = !available || !service;
  targetLink.href = `/?server=${encodeURIComponent(row.serverId)}&resource=services&service=${encodeURIComponent(row.target)}`;
  targetLink.textContent = `查看服务 ${row.target}`;
  if (service) link.href = `/?server=${encodeURIComponent(row.serverId)}&operation=${encodeURIComponent(row.operationId)}&resource=services&service=${encodeURIComponent(row.target)}#task-section`;
  if (!available) detail.append(element("dt", "核对提示"), element("dd", "该服务器已不在当前配置中，保留的历史仍可查询。"));
  byId("record-dialog").showModal();
  renderRecordHealth(); void readRecordTask();
}
function renderRecordHealth() {
  const followUp = recordDetail.task?.healthFollowUp;
  byId("record-health-section").hidden = !followUp && !recordDetail.loading && !recordDetail.readError;
  globalThis.IISOperationHealth?.render(byId("record-health-follow-up"), followUp, { loading: recordDetail.loading, readError: recordDetail.readError });
  byId("record-health-refresh").disabled = recordDetail.loading;
}
function closeRecordDetail() {
  clearTimeout(recordDetail.timer); recordDetail.revision++; recordDetail.row = null; recordDetail.loading = false;
}
function scheduleRecordHealthPoll() {
  clearTimeout(recordDetail.timer);
  if (!byId("record-dialog").open || document.visibilityState === "hidden" || !recordDetail.row || !globalThis.IISOperationHealth?.isPending(recordDetail.task?.healthFollowUp)) return;
  recordDetail.timer = setTimeout(() => readRecordTask(), recordDetail.readError ? 10000 : 5000);
}
async function readRecordTask() {
  if (!recordDetail.row || recordDetail.loading || !byId("record-dialog").open || document.visibilityState === "hidden") return;
  const row = recordDetail.row, revision = recordDetail.revision; recordDetail.loading = true; renderRecordHealth();
  try {
    const task = await api(`/api/v1/servers/${encodeURIComponent(row.serverId)}/operations/${encodeURIComponent(row.operationId)}`);
    if (revision !== recordDetail.revision || !byId("record-dialog").open) return;
    if (task?.serverId !== row.serverId || typeof task?.request?.operationId !== "string" || task.request.operationId.toLowerCase() !== row.operationId.toLowerCase()) throw new Error("RECORD_IDENTITY_MISMATCH");
    recordDetail.task = task; recordDetail.readError = false;
    byId("record-state").textContent = Object.prototype.hasOwnProperty.call(statusLabels, task.state) ? statusLabels[task.state] : "结果未知";
    byId("record-updated").textContent = date(task.updatedAt);
  } catch {
    if (revision === recordDetail.revision) recordDetail.readError = true;
  } finally {
    if (revision === recordDetail.revision) { recordDetail.loading = false; renderRecordHealth(); scheduleRecordHealthPoll(); }
  }
}
function renderRows(items) {
  const body = byId("records-body"); body.replaceChildren();
  items.forEach(row => {
    const tr = element("tr"), target = element("td");
    target.append(element("strong", row.target || "—"), element("div", actionLabels[row.type] || row.type, "muted"), element("span", row.operationId, "record-id"));
    const server = element("td", serverName(row.serverId)), status = element("td");
    status.append(element("span", statusLabels[row.state] || row.state, `badge ${row.state === "succeeded" ? "good" : ["failed", "partial", "unknown"].includes(row.state) ? "bad" : ""}`));
    const time = element("td"); time.append(element("div", date(row.acceptedAt)), element("div", date(row.updatedAt), "muted"));
    const actions = element("td"), button = element("button", "查看详情", "secondary small-button"); button.type = "button"; button.setAttribute("aria-label", `查看 ${row.target || row.operationId} 的操作详情`); button.addEventListener("click", () => openRecord(row)); actions.append(button);
    const operator = element("td", operatorName(row.operatorName)); operator.setAttribute("headers", "record-operator-column");
    tr.append(target, server, operator, status, element("td", persistenceLabel(row.persistenceState)), time, actions); body.append(tr);
  });
  byId("records-empty").textContent = "没有匹配的操作记录，请调整查询条件。"; byId("records-empty").hidden = items.length > 0;
}
async function queryRecords(page = 1, criteria = records.query) {
  const revision = ++records.revision, query = { ...criteria }, pageSize = Number(byId("records-size").value) || 20;
  records.query = query;
  records.loading = true; showError(""); renderPagination();
  byId("records-query").disabled = true; byId("records-refresh").disabled = true;
  byId("records-body").replaceChildren(); byId("records-empty").hidden = true;
  byId("records-results").textContent = "正在读取操作记录…";
  try {
    const result = await api(`/api/v1/operations?${queryParameters(query, page, pageSize)}`);
    if (revision !== records.revision) return;
    if (!Array.isArray(result?.items) || !Number.isSafeInteger(result.total) || result.total < 0 || result.page !== page || result.pageSize !== pageSize || result.items.some(row => typeof row.operationId !== "string" || typeof row.serverId !== "string" || typeof row.target !== "string" || typeof row.type !== "string" || typeof row.state !== "string" || (row.operatorName != null && typeof row.operatorName !== "string") || (row.persistenceState != null && typeof row.persistenceState !== "string"))) throw new Error("操作记录响应不完整，请重新查询。");
    const last = Math.max(1, Math.ceil(result.total / pageSize));
    if (page > last) { await queryRecords(last, query); return; }
    records.query = query; records.page = page; records.pageSize = pageSize; records.total = result.total;
    renderRows(result.items); byId("records-results").textContent = `共 ${result.total} 条记录 · 本页显示 ${result.items.length} 条`;
    globalThis.history?.replaceState(null, "", `/operations.html?${queryParameters(query, page, pageSize)}`);
  } catch (error) {
    if (revision !== records.revision) return;
    records.total = 0; records.page = 1; records.pageSize = pageSize; byId("records-body").replaceChildren();
    byId("records-results").textContent = "查询失败，当前没有可展示的结果"; byId("records-empty").hidden = true; showError(error.message);
  } finally {
    if (revision === records.revision) { records.loading = false; renderPagination(); byId("records-query").disabled = false; byId("records-refresh").disabled = false; }
  }
}
function submitQuery() {
  try { void queryRecords(1, readCriteria()); } catch (error) { showError(error.message); }
}
byId("server-search").addEventListener("input", renderServers);
byId("records-form").addEventListener("submit", event => { event.preventDefault(); submitQuery(); });
byId("records-refresh").addEventListener("click", () => queryRecords(records.page));
byId("records-export").addEventListener("click", async () => {
  const button = byId("records-export"); if (button.disabled) return;
  let query; try { query = readCriteria(); } catch (error) { showError(error.message); return; }
  button.disabled = true; showError("");
  try {
    const response = await fetch(`/api/v1/operations/export?${queryParameters(query, 1, 50)}`, { cache: "no-store", credentials: "same-origin", headers: { Accept: "text/csv, application/json" } });
    if (!response.ok) { const data = await response.json(); globalThis.IISManagerAuth?.unauthorized(response, data); throw new Error(data.message || "导出失败，请缩小查询范围或重新登录。"); }
    const blob = await response.blob(), url = URL.createObjectURL(blob);
    const link = document.createElement("a"); link.href = url; link.download = "operations.csv"; link.click(); URL.revokeObjectURL(url);
  } catch (error) { showError(error.message || "导出失败，请重试。"); }
  finally { button.disabled = false; }
});
byId("records-prev").addEventListener("click", () => { if (!records.loading && records.page > 1) void queryRecords(records.page - 1); });
byId("records-next").addEventListener("click", () => { if (!records.loading && records.page < Math.ceil(records.total / records.pageSize)) void queryRecords(records.page + 1); });
byId("records-size").addEventListener("change", () => queryRecords(1));
byId("records-reset").addEventListener("click", () => {
  ["records-search", "records-type", "records-state", "records-from", "records-to"].forEach(id => byId(id).value = "");
  records.serverId = ""; renderServers(); submitQuery();
});
byId("record-close").addEventListener("click", () => { closeRecordDetail(); byId("record-dialog").close(); });
byId("record-dialog").addEventListener("close", closeRecordDetail);
byId("record-dialog").addEventListener("cancel", closeRecordDetail);
byId("record-health-refresh").addEventListener("click", readRecordTask);
document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") clearTimeout(recordDetail.timer); else if (byId("record-dialog").open) void readRecordTask(); });
globalThis.addEventListener?.("pagehide", closeRecordDetail);
(async () => {
  try {
    records.servers = await api("/api/v1/servers");
    if (globalThis.IISManagerAuth) globalThis.IISManagerAuth.render(await api("/api/v1/session"));
    const route = new URLSearchParams(globalThis.location?.search || "");
    records.serverId = route.get("serverId") || "";
    [["search", "records-search"], ["type", "records-type"], ["state", "records-state"]].forEach(([key, id]) => byId(id).value = route.get(key) || "");
    for (const key of ["from", "to"]) {
      const value = route.get(key); if (!value) continue;
      const parsed = new Date(value); if (!Number.isFinite(parsed.getTime())) throw new Error("链接中的时间条件不合法。");
      const local = new Date(parsed.getTime() - parsed.getTimezoneOffset() * 60000).toISOString().slice(0, 16); byId(`records-${key}`).value = local;
    }
    const size = Number(route.get("pageSize")); if ([10, 20, 50].includes(size)) byId("records-size").value = String(size);
    const page = Number(route.get("page")); renderServers();
    await queryRecords(Number.isSafeInteger(page) && page > 0 ? page : 1, readCriteria());
  } catch (error) { showError(error.message); byId("records-results").textContent = "无法读取操作记录"; }
})();
