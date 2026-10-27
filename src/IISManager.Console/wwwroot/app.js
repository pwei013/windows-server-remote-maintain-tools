"use strict";

const byId = id => document.getElementById(id);
const resourceNavigation = byId("resource-navigation");
const state = { servers: [], serverId: "", snapshot: null, snapshotStatus: "idle", topology: null, topologyStatus: "idle", topologyError: "", topologyRevision: 0, resourceContext: null, expandedSites: new Set(), services: null, servicesStatus: "idle", servicesError: "", servicesRevision: 0, serviceContext: null, serviceDetailRevision: 0, info: null, certificates: [], csrf: "", permissions: null, loading: false, busy: false, draft: null, siteDraft: null, sitePoolTask: null, validation: null, task: null, taskHealthReadError: false, timer: null, formRevision: 0 };
Object.assign(state, { siteAnnotations: null, siteAnnotationsStatus: "idle", siteAnnotationsError: "", siteAnnotationsRevision: 0, siteTagFilters: [], siteAnnotationEditor: null, annotationBusy: false, collapsedSiteSearches: new Set() });
const actionLabels = { "applicationPool.start": "启动应用程序池", "applicationPool.stop": "停止应用程序池", "applicationPool.recycle": "回收应用程序池", "applicationPool.create": "新建应用程序池", "applicationPool.update": "局部编辑应用程序池", "site.create": "新建网站", "site.update": "编辑站点配置", "application.create": "新建子应用", "application.update": "编辑子应用", "virtualDirectory.create": "新建虚拟目录", "virtualDirectory.update": "编辑虚拟目录", "site.start": "启动站点", "site.stop": "停止站点", "site.restart": "重新启动站点", "windowsService.start": "启动 Windows 服务", "windowsService.stop": "停止 Windows 服务", "windowsService.restart": "重新启动 Windows 服务", "windowsService.create": "创建 Windows 服务", "windowsService.updateStartType": "修改服务启动类型" };
const statusLabels = { queued: "等待执行", running: "执行中", succeeded: "执行成功", failed: "执行失败", partial: "部分完成，需要人工核对", unknown: "结果未知，请按原编号核对", expired: "操作已过期" };
const persistenceLabels = { synced: "已保存", pending: "待保存", unknown: "待核对", notConfigured: "未启用" };
const iisStateLabels = { Started: "运行中", Stopped: "已停止", Starting: "启动中", Stopping: "停止中", Unknown: "未知" };
const serviceStateLabels = { Running: "运行中", Stopped: "已停止", StartPending: "启动中", StopPending: "停止中", Paused: "已暂停", PausePending: "暂停中", ContinuePending: "恢复中", Unknown: "未知" };
const serviceStartTypeLabels = { Automatic: "自动", AutomaticDelayed: "自动（延迟启动）", Manual: "手动", Disabled: "禁用", Unknown: "未知" };
const serviceStartTypeOptions = Object.entries(serviceStartTypeLabels).filter(([key]) => key !== "Unknown");
const serviceActions = [["windowsService.start", "启动", "canStart"], ["windowsService.stop", "停止", "canStop"], ["windowsService.restart", "重新启动", "canRestart"], ["windowsService.updateStartType", "属性", "canUpdateStartType"]];
const siteActions = [["site.start", "启动"], ["site.stop", "停止"], ["site.restart", "重新启动"]];
const storageKey = "iis-manager-task-history-v1";
const poolFields = [
  ["managedRuntimeVersion", ".NET CLR 版本", [["", "无托管代码"], ["v4.0", "v4.0"], ["v2.0", "v2.0"]], ""],
  ["managedPipelineMode", "托管管道模式", [["Integrated", "集成 Integrated"], ["Classic", "经典 Classic"]], "Integrated"],
  ["enable32BitAppOnWin64", "启用 32 位应用程序", [["false", "否"], ["true", "是"]], false],
  ["autoStart", "自动启动", [["true", "是"], ["false", "否"]], false],
  ["startMode", "启动模式", [["OnDemand", "按需 OnDemand"], ["AlwaysRunning", "始终运行 AlwaysRunning"]], "OnDemand"],
  ["idleTimeoutMinutes", "空闲超时（分钟）", null, 20],
  ["idleTimeoutAction", "空闲超时动作", [["Terminate", "终止 Terminate"], ["Suspend", "挂起 Suspend"]], "Terminate"]
];

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
function notice(message, error = false) {
  byId("notice").textContent = message;
  byId("notice").className = error ? "notice danger" : "notice";
  byId("notice").hidden = !message;
}
function operationNode(id) { return byId(state.siteDraft ? `inline-pool-${id}` : id); }
function formError(message) { operationNode("form-error").textContent = message; operationNode("form-error").hidden = !message; }
function date(value) { return value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—"; }
function serverPath(suffix, id = state.serverId) { return `/api/v1/servers/${encodeURIComponent(id)}/${suffix}`; }
function createOperationId() {
  const browserCrypto = globalThis.crypto;
  if (typeof browserCrypto?.randomUUID === "function") return browserCrypto.randomUUID();
  if (typeof browserCrypto?.getRandomValues !== "function") throw new Error("当前浏览器无法安全生成操作编号，请升级浏览器后重试。");
  // randomUUID requires a secure context; getRandomValues also supports intranet HTTP.
  const bytes = new Uint8Array(16);
  browserCrypto.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
async function api(path, body) {
  const options = { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } };
  if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-IISManager-CSRF"] = state.csrf; options.body = JSON.stringify(body); }
  const response = await fetch(path, options);
  let data;
  try { data = await response.json(); } catch { throw new Error("控制台返回了无法识别的响应，请按原编号核对已有操作。"); }
  globalThis.IISManagerAuth?.unauthorized(response, data);
  if (!response.ok) { const error = new Error(`${data.message || "请求失败"} (${data.code || response.status})`); error.code = data.code; error.status = response.status; throw error; }
  return data;
}
function permitted(kind, id = state.serverId) { return state.permissions?.servers?.some(row => row.serverId === id && row[kind] === true) === true; }
function operationAllowed(type, id = state.serverId) { return permitted(["applicationPool.start", "applicationPool.stop", "applicationPool.recycle", "site.start", "site.stop", "site.restart", "windowsService.start", "windowsService.stop", "windowsService.restart"].includes(type) ? "operate" : "configure", id); }
function hasPendingTask() { return state.task && ["queued", "running", "unknown", "partial"].includes(state.task.state); }
function renderTaskLock() {
  const task = state.task, pending = hasPendingTask(), review = task && ["unknown", "partial"].includes(task.state);
  byId("task-lock-notice").hidden = !pending;
  byId("task-lock-release").hidden = !review;
  byId("task-lock-release").disabled = state.busy || state.annotationBusy || state.loading;
  if (!pending) return;
  const server = state.servers.find(item => item.id === task.serverId)?.name || task.serverId;
  const action = actionLabels[task.request?.type] || "操作", id = task.request?.operationId || "待核对";
  byId("task-lock-message").textContent = review
    ? `${server} 的${action}（${id}）${statusLabels[task.state]}，已暂停站点、应用池和服务操作。完成现场只读核实后，点击下方按钮继续管理；原记录保留，旧操作不会重放。`
    : `${server} 的${action}（${id}）${statusLabels[task.state]}，暂时不能提交其他操作。请等待完成或按原编号核对。`;
}
function updateButtons() {
  const locked = state.busy || state.annotationBusy || state.loading || hasPendingTask();
  byId("refresh-button").disabled = state.loading || state.busy || state.annotationBusy;
  byId("create-pool-button").disabled = locked || !state.snapshot || !permitted("configure");
  byId("create-site-button").disabled = locked || !state.snapshot || !permitted("configure");
  const createService = byId("create-service-button");
  createService.disabled = locked || !state.services?.canCreate || !permitted("configure");
  createService.title = state.services?.creationUnavailableReason || (!state.services ? "等待 Windows 服务快照" : "");
  byId("service-retry").disabled = state.busy || state.annotationBusy || state.servicesStatus === "loading";
  document.querySelectorAll("[data-server-id]").forEach(button => button.disabled = state.busy || state.loading || state.annotationBusy);
  byId("query-task-button").disabled = state.busy || state.annotationBusy;
  byId("release-task-button").disabled = state.busy || state.annotationBusy || state.loading;
  document.querySelectorAll("[data-operation]").forEach(button => {
    button.disabled = locked || button.dataset.unavailable === "true" || !operationAllowed(button.dataset.operation);
    button.title = hasPendingTask() ? "当前操作尚未完成或需要核实，请查看页面顶部的操作提示。" : button.dataset.operationTitle || actionLabels[button.dataset.operation] || "";
  });
  document.querySelectorAll("[data-maintenance-edit]").forEach(button => {
    button.disabled = locked || !state.snapshot || !permitted("configure");
    button.title = hasPendingTask() ? "请先按原编号核对当前操作。" : !permitted("configure") ? "当前账号没有此服务器的配置权限。" : "进入配置维护，预检查后确认修改。";
  });
  operationNode("validate-button").disabled = locked || !operationAllowed(state.draft?.type);
  operationNode("submit-operation-button").disabled = locked || !state.validation?.valid || !operationAllowed(state.draft?.type);
  operationNode("close-dialog").disabled = state.busy;
  if (byId("create-site-pool-button")) byId("create-site-pool-button").disabled = locked || !state.snapshot || !permitted("configure");
  if (byId("create-site-pool-query")) byId("create-site-pool-query").disabled = state.busy;
  updateAnnotationControls();
  renderTaskLock();
}
function readHistory() {
  try {
    const rows = JSON.parse(localStorage.getItem(storageKey) || "[]");
    return Array.isArray(rows) ? rows.filter(row => typeof row.serverId === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(row.serverId) && typeof row.operationId === "string" && /^[0-9a-f-]{36}$/i.test(row.operationId)).slice(0, 10) : [];
  } catch { return []; }
}
function rememberTask(task) {
  const item = { serverId: task.serverId, operationId: task.request.operationId };
  const rows = [item, ...readHistory().filter(row => row.operationId !== item.operationId)].slice(0, 10);
  try { localStorage.setItem(storageKey, JSON.stringify(rows)); } catch { notice("浏览器无法保存操作索引；请保留当前操作编号，刷新后手动核对。"); }
}
function rememberIntent(serverId, operationId) {
  rememberTask({ serverId, request: { operationId } });
}
function renderServers() {
  const list = byId("server-list"), query = normalized(byId("server-search").value);
  const servers = state.servers.filter(server => containsQuery([server.name, server.id, server.hostName], query));
  list.replaceChildren(); byId("server-count").textContent = state.servers.length;
  resourceNavigation.hidden = true;
  servers.forEach(server => {
    const group = element("div", undefined, "server-group"), selected = server.id === state.serverId;
    const button = element("button", undefined, `server-button${selected ? " selected" : ""}`);
    button.type = "button"; button.id = `server-row-${server.id}`; button.dataset.serverId = server.id;
    button.setAttribute("aria-label", `选择 ${server.name}`); button.setAttribute("aria-current", String(selected));
    button.disabled = state.busy || state.loading || state.annotationBusy;
    const icon = element("img", undefined, "ui-icon"); icon.src = "/assets/fluent/server.svg"; icon.alt = "";
    const title = element("span", undefined, "server-label"); title.append(element("strong", server.name), element("small", server.hostName || server.id));
    button.append(icon, title); group.append(button);
    button.addEventListener("click", async () => {
      if (state.busy || state.loading || state.annotationBusy || server.id === state.serverId) return;
      selectServer(server.id); await refresh(); byId(`server-row-${server.id}`)?.focus();
    });
    if (selected) { resourceNavigation.hidden = false; group.append(resourceNavigation); }
    list.append(group);
  });
  if (resourceNavigation.hidden) list.append(resourceNavigation);
  if (!servers.length) list.append(element("p", state.servers.length ? "没有匹配的服务器" : "尚未配置 Agent 服务器", "server-empty"));
}
async function refresh() {
  if (!state.serverId || state.loading || state.annotationBusy) return;
  state.topologyRevision++; state.topology = null; state.topologyStatus = "idle"; state.topologyError = "";
  state.siteAnnotationsRevision++; state.siteAnnotations = null; state.siteAnnotationsStatus = "idle"; state.siteAnnotationsError = "";
  state.loading = true; state.snapshotStatus = "loading"; renderSnapshot(); notice("");
  byId("connection-status").textContent = "连接中"; byId("connection-status").className = "badge";
  const id = state.serverId;
  void refreshServices(id);
  try {
    const [infoResult, snapshotResult, certificateResult] = await Promise.allSettled([api(serverPath("info", id)), api(serverPath("snapshot", id)), api(serverPath("certificates", id))]);
    if (id !== state.serverId) return;
    if (infoResult.status !== "fulfilled") throw infoResult.reason;
    if (snapshotResult.status !== "fulfilled") throw snapshotResult.reason;
    if (certificateResult.status === "rejected" && certificateResult.reason?.status === 401 && String(certificateResult.reason?.code).startsWith("APP_USER_")) throw certificateResult.reason;
    const info = infoResult.value, snapshot = snapshotResult.value;
    if (!Array.isArray(snapshot?.applicationPools) || !Array.isArray(snapshot.sites)) throw new Error("IIS 快照数据无法识别，请刷新核对。");
    const certificatesAvailable = certificateResult.status === "fulfilled" && Array.isArray(certificateResult.value);
    const certificates = certificatesAvailable ? certificateResult.value : [];
    if (info.protocolVersion !== "1.0") throw new Error("Agent 协议版本不兼容，已停止管理操作。");
    state.info = info; state.snapshot = snapshot; state.snapshotStatus = "ready"; state.certificates = certificates;
    byId("connection-status").textContent = info.mode === "Simulation" ? "模拟连接正常" : "连接正常";
    byId("connection-status").className = "badge good";
    byId("machine-info").textContent = `${info.machineName} · ${info.osVersion} · IIS ${info.iisVersion} · Agent ${info.agentVersion}`;
    byId("collected-at").textContent = `采集于 ${date(snapshot.collectedAt)}`;
    byId("snapshot-summary").textContent = `${snapshot.applicationPools.length} 个应用程序池 · ${snapshot.sites.length} 个网站`;
    renderSnapshot();
    void refreshTopology(id);
    void refreshSiteAnnotations(id);
    const warnings = [];
    if (!certificatesAvailable) warnings.push("证书读取失败，已保留可用的 IIS 数据；HTTPS 新建配置需先刷新证书。");
    if (info.sharedConfiguration) warnings.push("此服务器启用了 IIS 共享配置。Agent 将拒绝写入，请在预检查中核对。");
    if (warnings.length) notice(warnings.join(" "));
  } catch (error) {
    if (id !== state.serverId) return;
    state.snapshot = null; state.snapshotStatus = "failed"; state.info = null; state.certificates = [];
    byId("connection-status").textContent = "连接失败"; byId("connection-status").className = "badge bad";
    byId("machine-info").textContent = "无法读取服务器信息，请检查连接后刷新快照。";
    byId("collected-at").textContent = "当前快照不可用于操作";
    byId("snapshot-summary").textContent = ""; renderSnapshot(); notice(error.message, true);
  } finally { if (id === state.serverId) { state.loading = false; updateButtons(); } }
}
function normalized(value) { return String(value ?? "").trim().toLowerCase(); }
function containsQuery(values, query) { return !query || values.some(value => normalized(value).includes(query)); }
function normalizeSiteAnnotation(alias, tags) {
  if (typeof alias !== "string" || !Array.isArray(tags) || tags.some(tag => typeof tag !== "string")) throw new Error("别名和标签格式无法识别。");
  if ([alias, ...tags].some(value => /[\u0000-\u001f\u007f-\u009f]/.test(value))) throw new Error("别名和标签不能包含控制字符。");
  const cleanAlias = alias.trim(), cleanTags = [], seen = new Set();
  if (cleanAlias.length > 100) throw new Error("别名最多 100 个字符。");
  tags.forEach(value => {
    const tag = value.trim(); if (!tag) return;
    if (tag.length > 40) throw new Error("每个标签最多 40 个字符。");
    const key = normalized(tag); if (!seen.has(key)) { seen.add(key); cleanTags.push(tag); }
  });
  if (cleanTags.length > 20) throw new Error("最多 20 个标签。");
  return { alias: cleanAlias, tags: cleanTags };
}
function validSiteAnnotation(entry) {
  if (!entry || !Number.isSafeInteger(entry.siteId) || entry.siteId <= 0 || typeof entry.siteName !== "string" || !entry.siteName || typeof entry.version !== "string") return false;
  if (entry.applicationPath !== undefined && !validAnnotationPath(entry.applicationPath)) return false;
  try {
    const clean = normalizeSiteAnnotation(entry.alias, entry.tags);
    return entry.alias === clean.alias && entry.tags.length === clean.tags.length && entry.tags.every((tag, index) => tag === clean.tags[index]);
  } catch { return false; }
}
function validAnnotationPath(path) {
  return typeof path === "string" && (!path || path.length > 1 && path.length <= 2048 && path.startsWith("/") && path === path.trim()
    && !/[\u0000-\u001f\u007f-\u009f\\?#]/.test(path) && path.split("/").slice(1).every(segment => segment && segment !== "." && segment !== ".."));
}
function siteAnnotation(site, applicationPath = "") {
  return (state.siteAnnotations?.serverId === state.serverId ? state.siteAnnotations.entries : []).find(entry => entry.siteId === site.id && entry.siteName === site.name && (entry.applicationPath || "") === applicationPath)
    || { siteId: site.id, siteName: site.name, applicationPath, alias: "", tags: [], version: "" };
}
function editorAnnotation(editor) { return siteAnnotation({ id: editor.siteId, name: editor.siteName }, editor.applicationPath); }
function annotationTargetExists(editor) {
  return editor?.serverId === state.serverId && !!state.snapshot?.sites.some(site => site.id === editor.siteId && site.name === editor.siteName)
    && (!editor.applicationPath || state.topologyStatus === "ready" && !!state.topology?.sites.some(site => site.id === editor.siteId && site.name === editor.siteName && site.applications.some(application => application.path === editor.applicationPath)));
}
function siteResourceAnnotations(site) {
  return [siteAnnotation(site), ...(topologySite(site)?.applications || []).filter(application => application.path !== "/").map(application => siteAnnotation(site, application.path))];
}
function availableSiteTags() {
  const tags = new Map();
  (state.snapshot?.sites || []).forEach(site => siteResourceAnnotations(site).forEach(entry => entry.tags.forEach(tag => { if (!tags.has(normalized(tag))) tags.set(normalized(tag), tag); })));
  return [...tags.values()].sort((left, right) => left.localeCompare(right, "zh-CN", { numeric: true }));
}
function tagTone(tag) {
  const key = normalized(tag), known = { "生产": "blue", "财务": "purple", "对外服务": "green", api: "orange", "内部系统": "slate", "测试": "purple" };
  if (known[key]) return known[key];
  let hash = 0; for (const character of key) hash = (hash * 31 + character.charCodeAt(0)) >>> 0;
  return ["blue", "purple", "green", "orange", "slate"][hash % 5];
}
function tagChip(tag, remove) {
  const chip = element("span", undefined, `tag-chip tag-${tagTone(tag)}`); chip.append(element("span", tag));
  if (remove) {
    const button = element("button", undefined, "icon-button tag-remove"), icon = element("img", undefined, "ui-icon");
    icon.src = "/assets/fluent/dismiss.svg"; icon.alt = ""; button.type = "button"; button.setAttribute("aria-label", `移除标签 ${tag}`); button.append(icon);
    button.addEventListener("click", remove); chip.append(button);
  }
  return chip;
}
function renderSiteTagFilters() {
  const selected = byId("site-filter-tags"), list = byId("site-tag-option-list"), query = normalized(byId("site-tag-search").value); selected.replaceChildren(); list.replaceChildren();
  state.siteTagFilters.forEach(tag => selected.append(tagChip(tag, () => setSiteTagFilter(tag, false))));
  byId("site-tags-clear").hidden = !state.siteTagFilters.length;
  const tags = new Map([...availableSiteTags(), ...state.siteTagFilters].map(tag => [normalized(tag), tag]));
  [...tags.values()].filter(tag => containsQuery([tag], query)).forEach(tag => {
    const label = element("label", undefined, "tag-filter-option"), checkbox = element("input"); checkbox.type = "checkbox"; checkbox.checked = state.siteTagFilters.some(selected => normalized(selected) === normalized(tag));
    checkbox.addEventListener("change", () => setSiteTagFilter(tag, checkbox.checked)); label.append(checkbox, tagChip(tag)); list.append(label);
  });
  if (!list.children.length) list.append(element("p", state.siteAnnotationsStatus === "ready" ? "没有可选标签" : "读取站点标注后可筛选标签", "muted small"));
}
function setSiteTagFilter(tag, selected) {
  state.siteTagFilters = state.siteTagFilters.filter(value => normalized(value) !== normalized(tag));
  if (selected) state.siteTagFilters.push(tag);
  renderSnapshot();
}
function toggleSiteTagOptions(open = byId("site-tag-options").hidden) {
  byId("site-tag-options").hidden = !open; byId("site-tags-toggle").setAttribute("aria-expanded", String(open));
  if (open) { renderSiteTagFilters(); byId("site-tag-search").focus(); }
}
function siteAnnotationError(error) {
  if (error?.code === "SITE_ANNOTATIONS_NOT_CONFIGURED") return "尚未配置别名与标签存储，请由运维人员完成配置后重试。";
  if (error?.code === "SITE_ANNOTATION_CONFLICT") return "此资源的标注已被更新。草稿已保留，请读取最新版本后再保存。";
  if (error?.code === "SITE_TARGET_CHANGED") return "原 IIS 站点或应用程序已发生变化。草稿已保留，请刷新快照核对。";
  return "别名与标签暂时无法读取，请稍后重试。";
}
async function refreshSiteAnnotations(id = state.serverId, { reviewDraft = true } = {}) {
  if (!id || id !== state.serverId || !state.snapshot) return null;
  const revision = ++state.siteAnnotationsRevision;
  state.siteAnnotations = null; state.siteAnnotationsStatus = "loading"; state.siteAnnotationsError = ""; renderSnapshot();
  try {
    const annotations = await api(serverPath("site-annotations", id));
    if (id !== state.serverId || revision !== state.siteAnnotationsRevision) return null;
    if (annotations?.serverId !== id || !Array.isArray(annotations.entries) || annotations.entries.some(entry => !validSiteAnnotation(entry))
      || new Set(annotations.entries.map(entry => JSON.stringify([entry.siteId, entry.siteName, entry.applicationPath || ""]))).size !== annotations.entries.length) throw new Error("站点标注响应无法识别。");
    state.siteAnnotations = annotations; state.siteAnnotationsStatus = "ready";
    const editor = state.siteAnnotationEditor;
    if (reviewDraft && editor?.serverId === id) {
      const siteExists = state.snapshot?.sites.some(site => site.id === editor.siteId && site.name === editor.siteName);
      // Annotation and topology requests finish independently during a refresh.
      // Wait for topology before treating a temporarily missing application as deleted.
      editor.targetChanged = !siteExists || (!editor.applicationPath || state.topologyStatus === "ready") && !annotationTargetExists(editor);
      if (editor.targetChanged) { editor.needsReview = true; editor.error = siteAnnotationError({ code: "SITE_TARGET_CHANGED" }); }
      else if (editorAnnotation(editor).version !== editor.expectedVersion) {
        editor.needsReview = true; editor.error = siteAnnotationError({ code: "SITE_ANNOTATION_CONFLICT" });
      }
    }
    return annotations;
  } catch (error) {
    if (id !== state.serverId || revision !== state.siteAnnotationsRevision) return null;
    state.siteAnnotationsStatus = error.code === "SITE_ANNOTATIONS_NOT_CONFIGURED" ? "unconfigured" : "failed"; state.siteAnnotationsError = siteAnnotationError(error);
    return null;
  } finally { if (id === state.serverId && revision === state.siteAnnotationsRevision) renderSnapshot(); }
}
function renderSiteAnnotationStatus() {
  const status = state.siteAnnotationsStatus, visible = !!state.snapshot && status !== "ready";
  byId("site-annotation-status-area").hidden = !visible;
  byId("site-annotation-status").textContent = status === "loading" ? "正在读取别名与标签…" : state.siteAnnotationsError || "连接后读取站点别名与标签。";
  byId("site-annotations-retry").hidden = ["idle", "loading", "ready"].includes(status);
}
function annotationEditId(siteId, applicationPath = "") { return applicationPath ? `application-annotation-edit-${siteId}-${encodeURIComponent(applicationPath)}` : `site-annotation-edit-${siteId}`; }
function annotationEditButton(site, applicationPath = "") {
  const button = element("button", undefined, "text-button site-annotation-edit"), icon = element("img", undefined, "ui-icon"), serverId = state.serverId;
  button.type = "button"; button.id = annotationEditId(site.id, applicationPath); button.dataset.siteAnnotation = String(site.id);
  button.setAttribute("aria-label", `编辑 ${site.name}${applicationPath} 的别名与标签`);
  icon.src = "/assets/fluent/edit.svg"; icon.alt = ""; button.append(icon, element("span", "编辑别名与标签"));
  button.addEventListener("click", () => openSiteAnnotationEditor(site, serverId, applicationPath)); return button;
}
function openSiteAnnotationEditor(site, serverId = state.serverId, applicationPath = "") {
  if (state.busy || state.loading || state.annotationBusy || serverId !== state.serverId || state.siteAnnotationsStatus !== "ready"
    || !validAnnotationPath(applicationPath) || !annotationTargetExists({ serverId, siteId: site.id, siteName: site.name, applicationPath })) return;
  const entry = siteAnnotation(site, applicationPath);
  state.siteAnnotationEditor = { serverId, siteId: site.id, siteName: site.name, applicationPath, alias: entry.alias, tags: [...entry.tags], tagInput: "", expectedVersion: entry.version, error: "", status: "", needsReview: false, targetChanged: false, pendingAttempt: null };
  byId("site-annotation-editor").hidden = false; document.body?.classList?.add("annotation-editor-open");
  toggleAnnotationSuggestions(false); renderSnapshot(); byId("annotation-alias").focus();
}
function closeSiteAnnotationEditor(force = false, focus = true) {
  if (state.annotationBusy && !force) return;
  const editor = state.siteAnnotationEditor; state.siteAnnotationEditor = null;
  byId("site-annotation-editor").hidden = true; document.body?.classList?.remove("annotation-editor-open");
  ["annotation-site-name", "annotation-application-path", "annotation-alias", "annotation-tag-input"].forEach(id => { byId(id).value = ""; });
  byId("annotation-draft-tags").replaceChildren(); toggleAnnotationSuggestions(false);
  renderSnapshot(); if (focus && editor?.serverId === state.serverId) byId(annotationEditId(editor.siteId, editor.applicationPath))?.focus();
}
function addAnnotationTag(tag = byId("annotation-tag-input").value) {
  const editor = state.siteAnnotationEditor; if (!editor || state.annotationBusy) return;
  try {
    const clean = normalizeSiteAnnotation(editor.alias, [...editor.tags, tag]); editor.tags = clean.tags; editor.tagInput = ""; editor.error = "";
    renderSiteAnnotationEditor(); byId("annotation-tag-input").focus();
  } catch (error) { editor.error = error.message; renderSiteAnnotationEditor(); }
}
function renderSiteAnnotationEditor() {
  const editor = state.siteAnnotationEditor; if (!editor || editor.serverId !== state.serverId) return;
  byId("annotation-site-name").value = editor.siteName;
  byId("annotation-application-field").hidden = !editor.applicationPath;
  byId("annotation-application-path").value = editor.applicationPath;
  byId("annotation-alias-label").textContent = editor.applicationPath ? "应用程序别名" : "站点别名";
  byId("annotation-alias").placeholder = editor.applicationPath ? "输入便于识别的应用程序别名" : "输入便于识别的站点别名";
  byId("annotation-draft-tags").setAttribute("aria-label", editor.applicationPath ? "当前应用程序标签" : "当前站点标签");
  if (editor.applicationPath && state.topologyStatus === "ready" && !annotationTargetExists(editor)) {
    editor.needsReview = true; editor.targetChanged = true; editor.error = siteAnnotationError({ code: "SITE_TARGET_CHANGED" });
  }
  if (byId("annotation-alias").value !== editor.alias) byId("annotation-alias").value = editor.alias;
  if (byId("annotation-tag-input").value !== editor.tagInput) byId("annotation-tag-input").value = editor.tagInput;
  const draft = byId("annotation-draft-tags"), recommended = byId("annotation-recommended-tags"), suggestions = byId("annotation-tag-suggestions"); draft.replaceChildren(); recommended.replaceChildren(); suggestions.replaceChildren();
  editor.tags.forEach(tag => {
    const chip = tagChip(tag, () => { if (!state.annotationBusy && state.siteAnnotationEditor === editor) { editor.tags = editor.tags.filter(value => normalized(value) !== normalized(tag)); editor.error = ""; renderSiteAnnotationEditor(); } });
    chip.children[1].dataset.annotationControl = "true"; draft.append(chip);
  });
  const tags = availableSiteTags().filter(tag => !editor.tags.some(value => normalized(value) === normalized(tag)));
  const addChoice = (container, tag) => {
    const button = element("button", tag, `tag-chip tag-${tagTone(tag)}`); button.type = "button"; button.dataset.annotationControl = "true"; button.setAttribute("aria-label", `添加标签 ${tag}`);
    button.addEventListener("click", () => { if (state.siteAnnotationEditor === editor) addAnnotationTag(tag); }); container.append(button);
  };
  tags.slice(0, 8).forEach(tag => addChoice(recommended, tag));
  tags.filter(tag => containsQuery([tag], normalized(editor.tagInput))).forEach(tag => addChoice(suggestions, tag));
  if (!suggestions.children.length) suggestions.append(element("p", editor.tagInput ? "可按 Enter 添加新标签" : "暂无其他已有标签", "muted small"));
  byId("annotation-no-recommendations").hidden = !!tags.length;
  byId("annotation-editor-error").textContent = editor.error; byId("annotation-editor-error").hidden = !editor.error;
  byId("annotation-editor-status").textContent = editor.status;
  const reconcile = byId("annotation-reconcile"); reconcile.hidden = !editor.needsReview;
  reconcile.textContent = editor.targetChanged ? "刷新快照并保留草稿" : editor.pendingAttempt ? "核对保存结果" : "读取最新版本并保留草稿";
  updateAnnotationControls();
}
function updateAnnotationControls() {
  const editor = state.siteAnnotationEditor, locked = state.annotationBusy || state.busy || state.loading;
  byId("site-annotation-editor").setAttribute("aria-busy", String(state.annotationBusy));
  byId("annotation-save").disabled = locked || !permitted("configure") || !editor || state.siteAnnotationsStatus !== "ready" || editor.needsReview || !annotationTargetExists(editor);
  byId("annotation-save").textContent = state.annotationBusy ? "正在核对…" : "保存";
  ["annotation-close", "annotation-cancel", "annotation-alias", "annotation-tag-input", "annotation-tag-toggle", "annotation-reconcile"].forEach(id => { byId(id).disabled = locked; });
  document.querySelectorAll("[data-annotation-control]").forEach(button => { button.disabled = locked; });
  document.querySelectorAll("[data-site-annotation]").forEach(button => { button.disabled = locked || !permitted("configure") || state.siteAnnotationsStatus !== "ready"; });
  byId("site-tags-toggle").disabled = state.siteAnnotationsStatus !== "ready";
  byId("site-annotations-retry").disabled = locked || state.siteAnnotationsStatus === "loading";
  ["sites", "pools", "services"].forEach(kind => { byId(`${kind}-tab`).disabled = state.annotationBusy; });
}
function annotationMatches(entry, attempt) {
  return !!entry.version && entry.alias === attempt.alias && entry.tags.length === attempt.tags.length && attempt.tags.every(tag => entry.tags.includes(tag));
}
function annotationEditorCurrent(editor) { return state.siteAnnotationEditor === editor && editor.serverId === state.serverId; }
function finishAnnotationSave(editor) {
  if (!annotationEditorCurrent(editor)) return;
  state.annotationBusy = false; closeSiteAnnotationEditor(true); notice(`已保存 ${editor.siteName}${editor.applicationPath} 的别名与标签，并读回确认。`);
}
async function verifyAnnotationSave(editor, attempt, response = null) {
  if (!annotationEditorCurrent(editor)) return;
  editor.status = "正在读回核对保存结果…"; renderSiteAnnotationEditor();
  const annotations = await refreshSiteAnnotations(editor.serverId, { reviewDraft: false });
  if (!annotationEditorCurrent(editor)) return;
  if (!annotations) {
    editor.needsReview = true; editor.error = "保存结果尚未确认，草稿已保留。请核对保存结果，控制台不会自动重复提交。"; return;
  }
  const latest = editorAnnotation(editor);
  if (annotationMatches(latest, attempt) && (!response || latest.version === response.version)) { finishAnnotationSave(editor); return; }
  editor.pendingAttempt = null; editor.needsReview = true; editor.targetChanged = !annotationTargetExists(editor);
  editor.error = editor.targetChanged ? siteAnnotationError({ code: "SITE_TARGET_CHANGED" }) : "读回的标注与草稿不一致，尚未确认保存结果。草稿已保留，请读取最新版本后再保存。";
  editor.status = "已完成只读核对，未重复提交。";
}
async function saveSiteAnnotation(event) {
  event?.preventDefault(); const editor = state.siteAnnotationEditor;
  if (!editor || state.busy || state.loading || state.annotationBusy || editor.needsReview || state.siteAnnotationsStatus !== "ready") return;
  if (!annotationTargetExists(editor)) { editor.needsReview = true; editor.targetChanged = true; editor.error = siteAnnotationError({ code: "SITE_TARGET_CHANGED" }); renderSiteAnnotationEditor(); return; }
  let clean;
  try { clean = normalizeSiteAnnotation(byId("annotation-alias").value, [...editor.tags, ...(byId("annotation-tag-input").value ? [byId("annotation-tag-input").value] : [])]); }
  catch (error) { editor.error = error.message; renderSiteAnnotationEditor(); return; }
  editor.alias = clean.alias; editor.tags = clean.tags; editor.tagInput = ""; editor.error = ""; editor.status = "正在保存别名与标签…";
  const attempt = { siteId: editor.siteId, siteName: editor.siteName, ...clean, expectedVersion: editor.expectedVersion }; editor.pendingAttempt = attempt;
  if (editor.applicationPath) attempt.applicationPath = editor.applicationPath;
  state.annotationBusy = true; renderSiteAnnotationEditor(); updateButtons();
  try {
    let response;
    try {
      response = await api(serverPath("site-annotations", editor.serverId), attempt);
      if (!annotationEditorCurrent(editor)) return;
      if (!validSiteAnnotation(response) || response.siteId !== editor.siteId || response.siteName !== editor.siteName || (response.applicationPath || "") !== editor.applicationPath || !annotationMatches(response, attempt)) response = null;
    } catch (error) {
      if (!annotationEditorCurrent(editor)) return;
      if (["SITE_ANNOTATION_CONFLICT", "SITE_TARGET_CHANGED"].includes(error.code)) {
        editor.pendingAttempt = null; editor.needsReview = true; editor.targetChanged = error.code === "SITE_TARGET_CHANGED"; editor.error = siteAnnotationError(error); editor.status = "草稿已保留，未重复提交。";
        await refreshSiteAnnotations(editor.serverId, { reviewDraft: false }); return;
      }
      if (error.code === "SITE_ANNOTATIONS_NOT_CONFIGURED" || [400, 401, 403, 404].includes(error.status)) {
        editor.pendingAttempt = null; editor.error = error.code === "SITE_ANNOTATIONS_NOT_CONFIGURED" ? siteAnnotationError(error) : "标注未能保存，草稿已保留。请核对输入或刷新连接后重试。"; editor.status = ""; return;
      }
    }
    await verifyAnnotationSave(editor, attempt, response);
  } finally {
    if (annotationEditorCurrent(editor)) { state.annotationBusy = false; renderSiteAnnotationEditor(); updateButtons(); }
  }
}
async function reconcileSiteAnnotation() {
  const editor = state.siteAnnotationEditor; if (!editor || state.annotationBusy || state.busy || state.loading) return;
  if (editor.targetChanged) { await refresh(); return; }
  state.annotationBusy = true; editor.status = "正在读取最新标注…"; renderSiteAnnotationEditor(); updateButtons();
  try {
    const annotations = await refreshSiteAnnotations(editor.serverId, { reviewDraft: false }); if (!annotationEditorCurrent(editor)) return;
    if (!annotations) { editor.error = "最新标注读取失败，草稿仍然保留。请稍后重试。"; return; }
    const latest = editorAnnotation(editor);
    if (editor.pendingAttempt && annotationMatches(latest, editor.pendingAttempt)) { finishAnnotationSave(editor); return; }
    if (!annotationTargetExists(editor)) { editor.targetChanged = true; editor.error = siteAnnotationError({ code: "SITE_TARGET_CHANGED" }); return; }
    editor.expectedVersion = latest.version; editor.pendingAttempt = null; editor.needsReview = false; editor.error = ""; editor.status = "已读取最新版本，草稿已保留。请核对后保存。";
  } finally { if (annotationEditorCurrent(editor)) { state.annotationBusy = false; renderSiteAnnotationEditor(); updateButtons(); } }
}
function siteUsesPool(site, poolName) {
  const name = normalized(poolName);
  const detail = topologySite(site);
  if (state.topology) return !!detail && detail.applications.some(application => normalized(application.applicationPoolName) === name);
  if (normalized(site.applicationPoolName) === name) return true;
  const pool = state.snapshot?.applicationPools.find(candidate => normalized(candidate.name) === name);
  const prefix = `${normalized(site.name)}/`;
  return (pool?.applications || []).some(application => normalized(application).startsWith(prefix));
}
function showPoolSites(pool) {
  resetResourceFilters("site");
  byId("site-pool").value = pool.name;
  renderSnapshot();
  activateResourceTab("sites");
  byId("sites-panel").focus();
}
function topologySite(site) { return state.topology?.sites.find(candidate => String(candidate.id) === String(site.id) && candidate.name === site.name); }
function applicationLabel(application) { return application.path === "/" ? "根应用（/）" : `${application.path.split("/").filter(Boolean).at(-1)}（${application.path}）`; }
function matchingApplications(site, poolName = "") { return site.applications.filter(application => !poolName || normalized(application.applicationPoolName) === normalized(poolName)); }
function siteSearchValues(site) {
  const annotation = siteAnnotation(site);
  return [site.name, site.id, annotation.alias, ...annotation.tags, site.physicalPath, site.applicationPoolName,
    ...site.bindings.flatMap(binding => [binding.hostName, binding.ipAddress])];
}
function applicationMatchesQuery(site, application, query) {
  const annotation = siteAnnotation(site, application.path);
  return containsQuery([application.path, application.applicationPoolName, application.physicalPath, annotation.alias, ...annotation.tags], query);
}
function directoryMatchesQuery(application, directory, query) {
  const fullPath = (application.path === "/" ? "" : application.path) + directory.path;
  return containsQuery([directory.path, fullPath, directory.physicalPath], query);
}
function siteChildSearchQuery(site, query) { return containsQuery(siteSearchValues(site), query) ? "" : query; }
function siteChildResources(site, poolName = "", query = "") {
  const scoped = matchingApplications(site, poolName).slice().sort((left, right) => left.path.localeCompare(right.path, "zh-CN", { numeric: true }));
  return {
    applications: scoped.filter(application => application.path !== "/" && applicationMatchesQuery(site, application, query)),
    directories: scoped.flatMap(application => application.virtualDirectories.filter(directory => directory.path !== "/" &&
      (applicationMatchesQuery(site, application, query) || directoryMatchesQuery(application, directory, query))).map(directory => ({ application, directory })))
  };
}
function siteMatchesQuery(site, poolName, query) {
  if (containsQuery(siteSearchValues(site), query)) return true;
  const detail = topologySite(site), matches = detail ? siteChildResources(detail, poolName, query) : null;
  return !!matches && (matches.applications.length > 0 || matches.directories.length > 0);
}
function siteExpansionKey(site) { return `${site.id}:${site.name}`; }
function siteSearchExpansionKey(site) { return JSON.stringify([siteExpansionKey(site), normalized(byId("site-search").value), byId("site-pool").value, state.siteTagFilters.map(normalized).sort()]); }
function toggleSiteDetails(site, serverId = state.serverId) {
  if (serverId !== state.serverId || !state.snapshot?.sites.some(current => current.id === site.id && current.name === site.name)) return;
  const key = siteExpansionKey(site);
  const detail = topologySite(site), children = detail ? siteChildResources(detail, byId("site-pool").value) : null;
  if (children && !children.applications.length && !children.directories.length) { state.expandedSites.delete(key); renderSnapshot(); return; }
  const searchKey = siteSearchExpansionKey(site);
  if (byId(`site-details-toggle-${site.id}`)?.dataset.expanded === "true") { state.expandedSites.delete(key); state.collapsedSiteSearches.add(searchKey); }
  else { state.expandedSites.add(key); state.collapsedSiteSearches.delete(searchKey); }
  renderSnapshot(); byId(`site-details-toggle-${site.id}`)?.focus();
}
function siteChildTable(site, heading, headers, columns, kind) {
  const section = element("section", undefined, "site-child-section"), title = element("h3", heading);
  title.id = `site-${kind}-title-${site.id}`; section.setAttribute("aria-labelledby", title.id);
  const scroll = element("div", undefined, "site-child-scroll"), table = element("table", undefined, `site-child-table site-${kind}-table`);
  const caption = element("caption", `${site.name}的${kind === "applications" ? "应用程序" : "附加虚拟目录"}`, "sr-only"), group = element("colgroup");
  columns.forEach(width => { const column = element("col"); column.style = `width:${width}%`; group.append(column); });
  const head = element("thead"), header = element("tr"), body = element("tbody");
  headers.forEach(label => { const cell = element("th", label); cell.scope = "col"; header.append(cell); });
  head.append(header); table.append(caption, group, head, body); scroll.append(table); section.append(title, scroll);
  return { section, body };
}
function siteApplicationPath(application, site) {
  const cell = element("td", undefined, "site-application-path-cell");
  const annotation = site ? siteAnnotation(site, application.path) : null;
  cell.append(element("span", annotation?.alias || application.path, "site-application-path"));
  if (site) cell.append(annotationEditButton(site, application.path));
  if (annotation?.alias) cell.append(element("div", application.path, "resource-original-name muted"));
  if (annotation?.tags.length) { const tags = element("div", undefined, "site-row-tags tag-chips"); annotation.tags.forEach(tag => tags.append(tagChip(tag))); cell.append(tags); }
  if (application.path === "/") cell.append(element("span", "根应用", "site-root-tag"));
  return cell;
}
function sitePoolCell(name, inherited = false) {
  const cell = element("td", undefined, "site-child-pool"); cell.append(poolLink(name));
  if (inherited) cell.setAttribute("aria-label", `继承所属应用的应用池 ${name || "未指定"}`);
  return cell;
}
function maintenanceEditButton(site, applicationPath = "", directoryPath = "") {
  const type = directoryPath ? "virtualDirectory.update" : applicationPath && applicationPath !== "/" ? "application.update" : "site.update";
  const query = new URLSearchParams({ server: state.serverId, site: site.name, action: type });
  if (type !== "site.update") query.set("application", applicationPath);
  if (directoryPath) query.set("directory", directoryPath);
  const label = "编辑";
  const button = element("button", label, "secondary small-button"); button.type = "button";
  button.dataset.maintenanceEdit = type; button.dataset.maintenanceUrl = `/maintenance.html?${query}`;
  button.setAttribute("aria-label", `${label} ${site.name}${applicationPath}${directoryPath}`);
  button.addEventListener("click", () => {
    if (button.disabled || state.busy || state.annotationBusy || state.loading || hasPendingTask() || !permitted("configure")) return;
    location.href = button.dataset.maintenanceUrl;
  });
  return button;
}
function siteAddressCell(site, application, directoryPath = "/") {
  const cell = element("td", undefined, "site-child-address"); cell.append(resourceBindings(site, application, directoryPath)); return cell;
}
function renderSiteDetails(site, detail, poolName, query = "") {
  const row = element("tr", undefined, "site-details-row"), cell = element("td", undefined, "site-details-cell");
  row.id = `site-details-${site.id}`; row.dataset.siteDetailsId = String(site.id); cell.colSpan = 5;
  const panel = element("div", undefined, "site-details-panel"); panel.setAttribute("role", "region"); panel.setAttribute("aria-label", `${site.name}的应用程序和附加虚拟目录`);
  cell.append(panel); row.append(cell);
  if (!detail) {
    panel.append(element("p", state.topologyStatus === "ready" ? "最新应用与目录快照没有此站点，请刷新后核对。" : topologyMessage(), "site-details-status muted"));
    const retry = element("button", "刷新应用与目录", "secondary small-button"); retry.type = "button"; retry.disabled = state.loading || state.topologyStatus === "loading";
    const serverId = state.serverId; retry.addEventListener("click", () => { if (state.serverId === serverId) return refreshTopology(serverId); }); panel.append(retry);
    return row;
  }
  const { applications, directories } = siteChildResources(detail, poolName, query);
  if (poolName) panel.append(element("p", `仅显示使用 ${poolName} 的应用及其附加虚拟目录。`, "site-details-scope muted small"));
  if (query) panel.append(element("p", "仅显示匹配关键词的应用程序、虚拟目录及匹配应用所属的目录。", "site-details-scope muted small"));
  if (applications.length) {
    const apps = siteChildTable(site, `应用程序（${applications.length}）`, ["应用（别名 / 路径）", "应用池", "物理目录", "访问地址", "操作"], [18, 17, 24, 32, 9], "applications");
    applications.forEach(application => {
      const item = element("tr"); item.dataset.applicationPath = application.path;
      if (state.siteAnnotationEditor?.siteId === site.id && state.siteAnnotationEditor.siteName === site.name && state.siteAnnotationEditor.applicationPath === application.path) item.className = "is-annotation-editing";
      item.append(siteApplicationPath(application, site), sitePoolCell(application.applicationPoolName), element("td", application.physicalPath || "未配置根目录映射", "site-child-physical-path"), siteAddressCell(detail, application));
      const actions = element("td", undefined, "site-child-actions"); actions.append(maintenanceEditButton(site, application.path)); item.append(actions);
      apps.body.append(item);
    });
    panel.append(apps.section);
  }
  if (directories.length) {
    const dirs = siteChildTable(site, `附加虚拟目录（${directories.length}）`, ["虚拟路径", "所属应用", "应用池（继承）", "物理目录", "访问地址", "操作"], [12, 12, 16, 20, 27, 13], "directories");
    directories.forEach(({ application, directory }) => {
      const item = element("tr"); item.dataset.applicationPath = application.path; item.dataset.directoryPath = directory.path;
      item.append(element("td", directory.path, "site-virtual-path"), element("td", application.path, "site-directory-owner"), sitePoolCell(application.applicationPoolName, true), element("td", directory.physicalPath || "未配置", "site-child-physical-path"), siteAddressCell(detail, application, directory.path));
      const actions = element("td", undefined, "site-child-actions"); actions.append(maintenanceEditButton(site, application.path, directory.path)); item.append(actions);
      dirs.body.append(item);
    });
    panel.append(dirs.section);
  }
  return row;
}
function topologyMessage() {
  if (state.topologyStatus === "loading") return "正在读取应用程序与虚拟目录…";
  if (state.topologyStatus === "unsupported") return "当前 Agent 尚不支持应用与目录详情，请升级 Agent；现有站点和应用池管理仍可使用。";
  if (state.topologyStatus === "failed") return `应用与目录详情读取失败：${state.topologyError}。现有快照仍可使用。`;
  return state.topology ? `应用与目录采集于 ${date(state.topology.collectedAt)}` : "刷新快照后读取应用程序与虚拟目录。";
}
async function refreshTopology(id = state.serverId) {
  if (!state.snapshot || id !== state.serverId) return;
  const revision = ++state.topologyRevision;
  state.topology = null; state.topologyStatus = "loading"; state.topologyError = ""; renderSnapshot();
  try {
    const topology = await api(serverPath("site-topology", id));
    if (id !== state.serverId || revision !== state.topologyRevision) return;
    if (!Array.isArray(topology?.sites) || !Array.isArray(topology.applicationPools) || topology.sites.some(site =>
      typeof site.name !== "string" || !Array.isArray(site.bindings) || !Array.isArray(site.applications) || site.applications.some(application =>
        typeof application.path !== "string" || typeof application.applicationPoolName !== "string" || !Array.isArray(application.virtualDirectories) || application.virtualDirectories.some(directory => typeof directory.path !== "string"))))
      throw new Error("Agent 返回的应用与目录结构无法识别");
    state.topology = topology; state.topologyStatus = "ready";
  } catch (error) {
    if (id !== state.serverId || revision !== state.topologyRevision) return;
    state.topologyStatus = error.code === "SITE_TOPOLOGY_UNSUPPORTED" ? "unsupported" : "failed"; state.topologyError = error.message;
  }
  if (id === state.serverId && revision === state.topologyRevision) renderSnapshot();
}
function validService(service) {
  return !!service && typeof service.name === "string" && typeof service.displayName === "string" && typeof service.state === "string" && typeof service.startType === "string"
    && typeof service.accountName === "string" && typeof service.executablePath === "string" && Number.isSafeInteger(service.processId) && service.processId >= 0
    && Array.isArray(service.dependencies) && service.dependencies.every(name => typeof name === "string")
    && Array.isArray(service.dependentServices) && service.dependentServices.every(name => typeof name === "string")
    && ["managed", "canStart", "canStop", "canRestart", "canUpdateStartType"].every(key => typeof service[key] === "boolean");
}
function servicesMessage() {
  if (state.servicesStatus === "loading") return "正在读取 Windows 服务…";
  if (state.servicesStatus === "unsupported") return "当前 Agent 尚不支持 Windows 服务管理，请升级 Agent。";
  if (state.servicesStatus === "failed") return `Windows 服务读取失败：${state.servicesError}`;
  return state.services ? `服务采集于 ${date(state.services.collectedAt)}` : "连接服务器后显示 Windows 服务。";
}
async function refreshServices(id = state.serverId) {
  if (!id || id !== state.serverId) return;
  const revision = ++state.servicesRevision;
  state.services = null; state.servicesStatus = "loading"; state.servicesError = ""; renderServices();
  try {
    const snapshot = await api(serverPath("windows-services", id));
    if (id !== state.serverId || revision !== state.servicesRevision) return;
    if (!Array.isArray(snapshot?.services) || snapshot.services.some(service => !validService(service)) || typeof snapshot.canCreate !== "boolean"
      || !Array.isArray(snapshot.allowedAccounts) || snapshot.allowedAccounts.some(account => typeof account !== "string")) throw new Error("Agent 返回的 Windows 服务结构无法识别");
    state.services = snapshot; state.servicesStatus = "ready";
  } catch (error) {
    if (id !== state.serverId || revision !== state.servicesRevision) return;
    state.servicesStatus = error.status === 501 && error.code === "WINDOWS_SERVICES_UNSUPPORTED" ? "unsupported" : "failed";
    state.servicesError = error.message;
  }
  if (id === state.serverId && revision === state.servicesRevision) renderServices();
}
function serviceUnavailableReason(service, capability) {
  if (service[capability]) return "";
  if (service.unavailableReason) return service.unavailableReason;
  if (capability === "canStart" && service.startType === "Disabled") return "服务已禁用，请在属性中改为自动或手动后启动。";
  if (capability === "canStart" && service.state === "Running") return "服务已在运行。";
  if (["canStop", "canRestart"].includes(capability) && service.state === "Stopped") return "服务已停止。";
  return "当前状态、权限或管理策略不允许此操作，请刷新后核对。";
}
function appendServiceActions(container, service) {
  serviceActions.forEach(([type, label, capability]) => {
    const button = element("button", label, "secondary small-button"), reason = serviceUnavailableReason(service, capability);
    button.type = "button"; button.dataset.operation = type; button.dataset.unavailable = String(!service[capability]);
    button.dataset.operationTitle = reason; button.title = reason; button.setAttribute("aria-label", `${label}服务 ${service.name}${reason ? `：${reason}` : ""}`);
    button.addEventListener("click", () => openServiceOperation(type, service)); container.append(button);
  });
}
function renderServices() {
  const body = byId("services-body"), services = (state.services?.services || []).filter(service => service.managed === true); body.replaceChildren();
  byId("services-tab-count").textContent = state.services ? services.length : "—";
  byId("services-status").textContent = servicesMessage(); byId("services-status").className = `service-status small ${state.servicesStatus === "failed" ? "danger-text" : "muted"}`;
  const query = normalized(byId("service-search").value), status = byId("service-state").value, startType = byId("service-start-type").value;
  const visible = services.filter(service => containsQuery([service.name, service.displayName], query) && (!status || service.state === status) && (!startType || service.startType === startType));
  visible.forEach(service => {
    const row = element("tr"), name = element("td"); row.dataset.serviceName = service.name;
    name.append(element("strong", service.displayName || service.name, "resource-name"), element("div", service.name, "resource-detail muted"));
    const inspect = element("button", "查看详情", "text-button pool-sites-button"); inspect.type = "button"; inspect.addEventListener("click", () => openServiceDetails(service.name)); name.append(inspect);
    const status = element("td"); status.append(element("span", serviceStateLabels[service.state] || service.state, `badge ${service.state === "Running" ? "good" : ""}`));
    const start = element("td", serviceStartTypeLabels[service.startType] || service.startType), account = element("td", service.accountName || "—"), actions = element("td", undefined, "row-actions service-actions");
    appendServiceActions(actions, service);
    if (service.unavailableReason) actions.append(element("div", service.unavailableReason, "resource-detail muted"));
    else if (service.startType === "Disabled") actions.append(element("div", "启动前请在属性中解除禁用。", "resource-detail muted"));
    row.append(name, status, start, account, actions); body.append(row);
  });
  byId("service-results").textContent = state.services ? `显示 ${visible.length} / ${services.length} 个可管理 Windows 服务` : "当前服务快照不可用";
  byId("services-empty").hidden = visible.length > 0;
  byId("services-empty").textContent = state.services ? services.length ? "没有匹配的可管理 Windows 服务，请调整查询条件。" : "当前 Agent 没有可管理的 Windows 服务，请核对配置的服务范围、服务是否存在和保护规则。" : servicesMessage();
  byId("service-create-reason").textContent = state.services?.creationUnavailableReason || "";
  byId("service-create-reason").hidden = !state.services?.creationUnavailableReason;
  updateButtons();
}
async function openServiceDetails(name) {
  const id = state.serverId, revision = ++state.serviceDetailRevision;
  state.serviceContext = { name, serverId: id }; byId("service-dialog-title").textContent = name;
  byId("service-details").replaceChildren(); byId("service-detail-actions").replaceChildren();
  byId("service-detail-status").textContent = "正在读取服务详情…"; byId("service-detail-status").className = "muted small"; byId("service-dialog").showModal();
  try {
    const service = await api(serverPath(`windows-services/${encodeURIComponent(name)}`, id));
    if (id !== state.serverId || revision !== state.serviceDetailRevision || !state.serviceContext) return;
    if (!validService(service)) throw new Error("Agent 返回的服务详情无法识别");
    byId("service-dialog-title").textContent = service.displayName || service.name;
    const details = byId("service-details");
    [["服务名称", service.name], ["显示名称", service.displayName], ["描述", service.description], ["运行状态", serviceStateLabels[service.state] || service.state], ["启动类型", serviceStartTypeLabels[service.startType] || service.startType], ["程序路径", service.executablePath], ["进程编号", service.processId ? String(service.processId) : "—"], ["运行账号", service.accountName], ["依赖服务", service.dependencies.join("、") || "无"], ["从属服务", service.dependentServices.join("、") || "无"]].forEach(([label, value]) => details.append(element("dt", label), element("dd", value || "—")));
    byId("service-detail-status").textContent = service.unavailableReason || "修改启动类型保持当前运行状态；需要立即运行时，请单独启动。";
    appendServiceActions(byId("service-detail-actions"), service); updateButtons();
  } catch (error) {
    if (id !== state.serverId || revision !== state.serviceDetailRevision || !state.serviceContext) return;
    byId("service-detail-status").textContent = `服务详情读取失败：${error.message}`; byId("service-detail-status").className = "danger-text small";
  }
}
function closeServiceDetails() { state.serviceDetailRevision++; state.serviceContext = null; byId("service-dialog").close(); }
function showWindowsService(name) {
  resetResourceFilters("service"); byId("service-search").value = name || ""; renderServices(); activateResourceTab("services");
  const row = [...byId("services-body").children].find(candidate => normalized(candidate.dataset.serviceName) === normalized(name));
  if (row) { row.className = "resource-highlight"; row.tabIndex = -1; row.focus(); } else byId("services-panel").focus();
}
function multilineField(label, id, hint) {
  const wrapper = element("div", undefined, "field field-wide"), title = element("label", label, "field-label"), control = element("textarea"); title.htmlFor = id; control.id = id; control.rows = 3;
  wrapper.append(title, control, element("span", hint, "field-hint")); return { wrapper, control };
}
function openServiceOperation(type, service) {
  if (state.busy || state.annotationBusy || state.loading || hasPendingTask() || !state.services) return;
  const capability = serviceActions.find(([action]) => action === type)?.[2];
  if (type === "windowsService.create" ? !state.services.canCreate : !service?.[capability]) return;
  closeServiceDetails(); state.draft = { type, service, serverId: state.serverId }; state.validation = null;
  byId("dialog-title").textContent = actionLabels[type];
  byId("dialog-description").textContent = type === "windowsService.create" ? "程序必须支持 Windows 服务协议，并已由操作员部署到目标服务器。创建后保持停止，运行账号权限由操作员准备。" : type === "windowsService.updateStartType" ? "修改启动类型保持当前运行状态。禁用的服务改为自动或手动后，可单独启动。" : "Agent 将检查权限、状态及服务依赖；停止或重新启动可能中断业务。";
  const container = byId("form-fields"); container.replaceChildren(); formError(""); byId("validation-section").hidden = true; byId("submit-operation-button").hidden = true;
  const name = field("服务名称", "service-name", null, service?.name || "", true); name.control.required = true; name.control.maxLength = 128; name.control.disabled = !!service; container.append(name.wrapper);
  if (type === "windowsService.create") {
    const displayName = field("显示名称", "service-display-name", null, "", true); displayName.control.required = true; displayName.control.maxLength = 256;
    const description = multilineField("描述", "service-description", "仅填写非敏感业务说明。");
    const executable = field("EXE 路径（目标服务器绝对路径）", "service-executable", null, "", true); executable.control.required = true; executable.control.placeholder = "例如 D:\\Services\\Orders\\Orders.exe";
    const argumentsField = multilineField("启动参数", "service-arguments", "每行一个参数，保留参数内部空格。仅填写非敏感值，禁止填写密码、令牌、密钥或连接字符串；参数会保存到操作记录。");
    const accountNames = state.services.allowedAccounts, preferred = accountNames.includes("VirtualAccount") ? "VirtualAccount" : accountNames[0] || "";
    const account = field("运行账号", "service-account", accountNames.map(value => [value, value === "VirtualAccount" ? "专用虚拟账号（VirtualAccount）" : value]), preferred); account.control.required = true;
    const dependencies = multilineField("依赖服务", "service-dependencies", "每行填写一个当前服务器上的精确服务名称；留空表示无依赖。");
    const choices = element("details"), summary = element("summary", "查看可选服务名称"), list = element("div", undefined, "service-dependency-options");
    (state.services.services || []).forEach(item => { const button = element("button", item.name, "secondary small-button"); button.type = "button"; button.addEventListener("click", () => { const names = lines(dependencies.control.value, true); if (!names.some(name => normalized(name) === normalized(item.name))) dependencies.control.value = [...names, item.name].join("\n"); invalidateValidation(); }); list.append(button); });
    choices.append(summary, list); dependencies.wrapper.append(choices);
    container.append(displayName.wrapper, description.wrapper, executable.wrapper, argumentsField.wrapper, account.wrapper, dependencies.wrapper);
  }
  if (["windowsService.create", "windowsService.updateStartType"].includes(type)) container.append(field("启动类型", "service-start-type-input", serviceStartTypeOptions, service?.startType || "Manual").wrapper);
  container.oninput = invalidateValidation; container.onchange = invalidateValidation;
  byId("operation-dialog").showModal(); updateButtons();
}
function lines(value, trim = false) { return String(value || "").split(/\r?\n/).map(line => trim ? line.trim() : line).filter(line => line.trim().length); }
function resourcePath(applicationPath, directoryPath = "/") {
  const parts = [];
  for (const path of [applicationPath, directoryPath]) {
    if (typeof path !== "string" || !path.startsWith("/") || path.includes("//") || /[\\\x00-\x1f\x7f]/.test(path)) return "";
    for (const part of path.split("/").slice(1)) {
      if (part === "." || part === "..") return "";
      if (part) parts.push(encodeURIComponent(part));
    }
  }
  return `/${parts.join("/")}${parts.length ? "/" : ""}`;
}
function applicationUrl(binding, application, directoryPath = "/") {
  if (!String(application.enabledProtocols || "").split(",").some(protocol => normalized(protocol) === "http")) return "";
  const base = siteBindingUrl(binding), path = resourcePath(application.path, directoryPath);
  return base && path ? base.slice(0, -1) + path : "";
}
function openResourceDialog(context) {
  state.resourceContext = { ...context, serverId: state.serverId }; byId("resource-search").value = "";
  renderResourceDialog(); byId("resource-dialog").showModal();
}
function closeResourceDialog() { state.resourceContext = null; byId("resource-dialog").close(); }
function showApplicationPool(name) {
  closeResourceDialog(); resetResourceFilters("pool"); byId("pool-search").value = name; renderSnapshot(); activateResourceTab("pools");
  const row = [...byId("pools-body").children].find(candidate => normalized(candidate.dataset.poolName) === normalized(name));
  if (row) { row.className = "resource-highlight"; row.tabIndex = -1; row.focus(); } else byId("pools-panel").focus();
}
function poolLink(name) {
  const button = element("button", name || "未指定应用池", "text-button resource-pool-link"); button.type = "button"; button.disabled = !name;
  button.setAttribute("aria-label", `查看应用程序池 ${name}`); button.addEventListener("click", () => showApplicationPool(name)); return button;
}
function resourceBindings(site, application, directoryPath = "/") {
  const container = element("div", undefined, "resource-addresses");
  const supportsHttp = String(application.enabledProtocols || "").split(",").some(protocol => normalized(protocol) === "http");
  if (!supportsHttp) { container.append(element("p", `应用启用协议：${application.enabledProtocols || "未启用"}，未启用 HTTP/HTTPS 访问。`, "muted small")); return container; }
  site.bindings.filter(binding => ["http", "https"].includes(normalized(binding.protocol))).forEach(binding => {
    const line = element("div", undefined, "binding-line"), url = applicationUrl(binding, application, directoryPath);
    line.append(element("span", binding.protocol.toUpperCase(), "protocol-tag"));
    if (url) {
      const link = element("a", url, "binding-link"); link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
      link.setAttribute("aria-label", `在新标签页打开 ${site.name} 的 ${applicationLabel(application)}${directoryPath !== "/" ? ` 虚拟目录 ${directoryPath}` : ""}：${url}`); line.append(link);
    } else line.append(element("span", isLoopbackHost(websiteHost(binding.hostName || binding.ipAddress)) ? "仅服务器本机访问" : "此绑定无法生成访问地址", "muted small"));
    container.append(line);
  });
  if (!container.children.length) container.append(element("p", "此站点没有 HTTP/HTTPS 绑定。", "muted small"));
  return container;
}
function renderResourceDialog() {
  const context = state.resourceContext; if (!context || context.serverId !== state.serverId) return;
  byId("resource-dialog-title").textContent = context.poolName && context.siteId === undefined ? `${context.poolName} · 关联应用` : "站点应用与虚拟目录";
  byId("resource-status").textContent = topologyMessage(); byId("resource-retry").disabled = state.topologyStatus === "loading" || state.loading;
  const tree = byId("resource-tree"); tree.replaceChildren(); byId("resource-summary").textContent = "";
  byId("resource-dialog-description").textContent = context.poolName ? `仅显示使用 ${context.poolName} 的应用及其虚拟目录。` : "查看各应用的物理目录、所属应用池和完整访问地址。";
  if (!state.topology) return;
  const query = normalized(byId("resource-search").value);
  const groups = state.topology.sites.filter(site => context.siteId === undefined || String(site.id) === String(context.siteId)).map(site => ({ site, applications: matchingApplications(site, context.poolName).filter(application =>
    containsQuery([site.name, application.path, application.applicationPoolName, application.physicalPath, ...application.virtualDirectories.flatMap(directory => [directory.path, directory.physicalPath])], query)) })).filter(group => group.applications.length);
  const count = groups.reduce((total, group) => total + group.applications.length, 0), directories = groups.reduce((total, group) => total + group.applications.reduce((sum, application) => sum + application.virtualDirectories.filter(directory => directory.path !== "/").length, 0), 0);
  const rootCount = groups.reduce((total, group) => total + group.applications.filter(application => application.path === "/").length, 0);
  byId("resource-summary").textContent = `${groups.length} 个站点 · ${count} 个应用${rootCount ? `（根应用 ${rootCount} 个）` : ""} · ${directories} 个附加虚拟目录`;
  if (!groups.length) tree.append(element("p", "没有匹配的应用；请调整筛选或刷新最新快照。", "empty"));
  groups.forEach(({ site, applications }) => {
    const section = element("section", undefined, "topology-site"), header = element("div", undefined, "topology-site-header");
    header.append(element("h3", `${site.name} · ID ${site.id}`), element("span", `站点：${iisStateLabels[site.state] || site.state}`, "badge")); section.append(header);
    applications.sort((left, right) => left.path.localeCompare(right.path, "zh-CN", { numeric: true })).forEach(application => {
      const card = element("article", undefined, "application-card"), title = element("div", undefined, "application-header");
      title.append(element("h4", applicationLabel(application)), element("span", application.path === "/" ? "根应用" : "应用程序", "resource-kind")); card.append(title);
      const pool = state.topology.applicationPools.find(candidate => normalized(candidate.name) === normalized(application.applicationPoolName));
      const association = element("div", undefined, "application-association"); association.append(element("span", "应用池"), poolLink(application.applicationPoolName), element("span", `应用池状态：${iisStateLabels[pool?.state] || pool?.state || "未找到"}`, "muted small")); card.append(association);
      card.append(element("p", `物理目录：${application.physicalPath || "未配置根目录映射"}`, "resource-physical-path"), resourceBindings(site, application));
      application.virtualDirectories.filter(directory => directory.path !== "/").forEach(directory => {
        const node = element("div", undefined, "virtual-directory");
        node.append(element("strong", `虚拟目录 ${directory.path}`), element("p", `所属应用：${applicationLabel(application)} · 随应用使用 ${application.applicationPoolName}`, "muted small"), element("p", `物理目录：${directory.physicalPath || "未配置"}`, "resource-physical-path"), resourceBindings(site, application, directory.path)); card.append(node);
      });
      section.append(card);
    }); tree.append(section);
  });
}
function websiteHost(value) {
  const host = String(value ?? "").trim();
  const bare = host.startsWith("[") && host.endsWith("]") ? host.slice(1, -1) : host;
  if (!bare || /[\s/\\@?#%\[\]*]/u.test(bare)) return "";
  try { return new URL(`http://${bare.includes(":") ? `[${bare}]` : bare}/`).hostname; }
  catch { return ""; }
}
function isLoopbackHost(host) { return /^(localhost\.?|\[::1\]|127(?:\.[0-9]{1,3}){3})$/.test(host); }
function siteBindingUrl(binding) {
  const protocol = normalized(binding.protocol), port = Number(binding.port);
  if (!["http", "https"].includes(protocol) || !Number.isInteger(port) || port < 1 || port > 65535) return "";
  const hostName = String(binding.hostName ?? "").trim(), ipAddress = String(binding.ipAddress ?? "").trim();
  const serverHost = websiteHost(state.servers.find(server => server.id === state.serverId)?.hostName);
  const localAccess = state.info?.mode === "Simulation" || (isLoopbackHost(serverHost) && isLoopbackHost(websiteHost(globalThis.location?.hostname)));
  let host = websiteHost(hostName || ipAddress);
  if (!hostName && (["", "*"].includes(ipAddress) || ["0.0.0.0", "[::]"].includes(host))) {
    host = serverHost;
    // A Console-local Agent address would otherwise open the operator's computer.
    if (isLoopbackHost(host) && !localAccess) host = websiteHost(state.info?.machineName);
  }
  if (isLoopbackHost(host) && !localAccess) return "";
  return host ? `${protocol}://${host}${port === (protocol === "https" ? 443 : 80) ? "" : `:${port}`}/` : "";
}
function syncSitePoolFilter(applicationPools, sites) {
  const selector = byId("site-pool"), selected = selector.value;
  const names = [...new Set([...applicationPools.map(pool => pool.name), ...sites.map(site => site.applicationPoolName), ...(state.topology?.sites || []).flatMap(site => site.applications.map(application => application.applicationPoolName))].filter(name => typeof name === "string" && name.length))]
    .sort((left, right) => left.localeCompare(right, "zh-CN", { numeric: true }));
  const existing = [...selector.options].slice(1).map(option => option.value);
  if (names.length !== existing.length || names.some((name, index) => name !== existing[index])) {
    const all = element("option", "全部应用程序池"); all.value = "";
    selector.replaceChildren(all);
    names.forEach(name => { const option = element("option", name); option.value = name; selector.append(option); });
  }
  selector.value = names.includes(selected) ? selected : "";
}
function emptyResourceMessage(resourceName, total) {
  if (state.snapshot) return total ? `没有匹配的${resourceName}，请调整查询条件。` : `当前服务器尚无${resourceName}。`;
  if (state.snapshotStatus === "loading") return `正在读取${resourceName}，请稍候…`;
  if (state.snapshotStatus === "failed") return `连接失败，暂时无法获取${resourceName}。请刷新快照或检查服务器连接。`;
  return `连接服务器后显示${resourceName}。`;
}
function renderResourceResult(kind, resourceName, shown, total) {
  const result = byId(`${kind}-results`), empty = byId(`${kind}s-empty`);
  result.textContent = state.snapshot ? `显示 ${shown} / ${total} 个${resourceName}` : state.snapshotStatus === "loading" ? "正在读取最新快照…" : state.snapshotStatus === "failed" ? "当前快照不可用" : "等待连接服务器";
  empty.textContent = emptyResourceMessage(resourceName, total); empty.hidden = shown > 0;
}
function renderSnapshot() {
  const pools = byId("pools-body"), sites = byId("sites-body"); pools.replaceChildren(); sites.replaceChildren();
  const applicationPools = state.snapshot?.applicationPools || [], allSites = state.snapshot?.sites || [];
  byId("topology-status").textContent = state.snapshot ? topologyMessage() : ""; byId("topology-status").hidden = !state.snapshot || state.topologyStatus === "ready" || state.activeResource === "services";
  if (state.snapshot) syncSitePoolFilter(applicationPools, allSites);
  byId("pools-tab-count").textContent = state.snapshot ? applicationPools.length : "—";
  byId("sites-tab-count").textContent = state.snapshot ? allSites.length : "—";
  byId("overview-pools").textContent = state.snapshot ? applicationPools.length : "—";
  byId("overview-sites").textContent = state.snapshot ? allSites.length : "—";
  byId("overview-running").textContent = state.snapshot ? applicationPools.filter(pool => pool.state === "Started").length : "—";
  byId("overview-stopped").textContent = state.snapshot ? applicationPools.filter(pool => pool.state === "Stopped").length : "—";
  const poolQuery = normalized(byId("pool-search").value), poolState = byId("pool-state").value, poolRuntime = byId("pool-runtime").value;
  document.querySelectorAll("[data-pool-state]").forEach(button => {
    const value = button.dataset.poolState, count = value ? applicationPools.filter(pool => pool.state === value).length : applicationPools.length;
    button.setAttribute("aria-pressed", String(value === poolState));
    button.textContent = `${value ? iisStateLabels[value] : "全部"} (${state.snapshot ? count : "—"})`;
  });
  const visiblePools = applicationPools.filter(pool => containsQuery([pool.name, ...(pool.applications || [])], poolQuery)
    && (!poolState || pool.state === poolState)
    && (!poolRuntime || (pool.managedRuntimeVersion || "") === (poolRuntime === "none" ? "" : poolRuntime)));
  visiblePools.forEach(pool => {
    const row = element("tr"), name = element("td"); name.append(element("strong", pool.name, "resource-name"), element("div", pool.applications.length ? pool.applications.join(" · ") : "无关联应用", "resource-detail muted"));
    row.dataset.poolName = pool.name;
    const associatedSites = allSites.filter(site => siteUsesPool(site, pool.name));
    const viewSites = element("button", `关联站点（${associatedSites.length}）`, "text-button pool-sites-button");
    viewSites.type = "button"; viewSites.disabled = !associatedSites.length;
    viewSites.setAttribute("aria-label", `查看 ${pool.name} 关联的 ${associatedSites.length} 个站点`);
    viewSites.addEventListener("click", () => showPoolSites(pool)); name.append(viewSites);
    const related = (state.topology?.sites || []).flatMap(site => matchingApplications(site, pool.name).map(application => ({ site, application })));
    const appCount = state.topology ? related.length : pool.applications.length;
    const viewApplications = element("button", `关联应用（${appCount}）`, "text-button pool-sites-button"); viewApplications.type = "button";
    viewApplications.setAttribute("aria-label", `查看 ${pool.name} 的关联应用`); viewApplications.addEventListener("click", () => openResourceDialog({ poolName: pool.name }));
    name.append(viewApplications);
    const status = element("td"); status.append(element("span", iisStateLabels[pool.state] || pool.state, `badge ${pool.state === "Started" ? "good" : ""}`));
    const config = element("td"); config.append(element("div", `${pool.managedRuntimeVersion || "无托管代码"} / ${pool.managedPipelineMode}`), element("div", `${pool.enable32BitAppOnWin64 ? "32 位" : "64 位"} · ${pool.identityType}`, "muted"));
    const start = element("td"); start.append(element("div", `${pool.startMode} · ${pool.autoStart ? "自动启动" : "手动启动"}`), element("div", `${pool.idleTimeoutMinutes} 分钟 / ${pool.idleTimeoutAction}`, "muted"));
    const actions = element("td", undefined, "row-actions");
    [["applicationPool.start", "启动"], ["applicationPool.stop", "停止"], ["applicationPool.recycle", "回收"], ["applicationPool.update", "编辑"]].forEach(([type, title]) => {
      const button = element("button", title, "secondary small-button"); button.dataset.operation = type;
      button.dataset.unavailable = String((type === "applicationPool.start" && pool.state === "Started") || (["applicationPool.stop", "applicationPool.recycle"].includes(type) && pool.state !== "Started"));
      button.dataset.operationTitle = `${actionLabels[type]} ${pool.name}`;
      button.setAttribute("aria-label", button.dataset.operationTitle);
      button.addEventListener("click", () => openOperation(type, pool)); actions.append(button);
    });
    row.append(name, status, config, start, actions); pools.append(row);
  });
  const siteQuery = normalized(byId("site-search").value), siteState = byId("site-state").value, sitePool = byId("site-pool").value, siteProtocol = byId("site-protocol").value;
  const portInput = byId("site-port"), portText = portInput.value.trim(), portNumber = Number(portText);
  const validPort = !portText || (/^[0-9]+$/.test(portText) && Number.isInteger(portNumber) && portNumber >= 1 && portNumber <= 65535);
  portInput.setAttribute("aria-invalid", String(!validPort));
  byId("site-port-hint").textContent = validPort ? "可留空，或填写 1–65535" : "请输入 1–65535 的整数端口；当前输入无匹配。";
  byId("site-port-hint").hidden = validPort;
  const visibleSites = allSites.filter(site => validPort
    && siteMatchesQuery(site, sitePool, siteQuery)
    && (!state.siteTagFilters.length || siteResourceAnnotations(site).some(entry => entry.tags.some(tag => state.siteTagFilters.some(selected => normalized(tag) === normalized(selected)))))
    && (!siteState || site.state === siteState)
    && (!sitePool || siteUsesPool(site, sitePool))
    && ((!siteProtocol && !portText) || site.bindings.some(binding => (!siteProtocol || normalized(binding.protocol) === siteProtocol) && (!portText || Number(binding.port) === portNumber))));
  visibleSites.forEach(site => {
    const annotation = siteAnnotation(site), row = element("tr"), name = element("td"); name.append(element("strong", annotation.alias || site.name, "resource-name"));
    name.append(annotationEditButton(site));
    if (annotation.alias) name.append(element("div", site.name, "resource-original-name muted"));
    if (annotation.tags.length) { const tags = element("div", undefined, "site-row-tags tag-chips"); annotation.tags.forEach(tag => tags.append(tagChip(tag))); name.append(tags); }
    const detail = topologySite(site), applications = detail ? matchingApplications(detail, sitePool) : [];
    const childQuery = siteChildSearchQuery(site, siteQuery), children = detail ? siteChildResources(detail, sitePool, childQuery) : null;
    const expandable = !children || children.applications.length > 0 || children.directories.length > 0;
    const keywordMatches = siteQuery && detail ? siteChildResources(detail, sitePool, siteQuery) : null;
    const childMatch = !!((keywordMatches && (keywordMatches.applications.length > 0 || keywordMatches.directories.length > 0)) || children?.applications.some(application => {
      const entry = siteAnnotation(site, application.path);
      return state.siteTagFilters.some(selected => entry.tags.some(tag => normalized(tag) === normalized(selected)));
    }));
    const expanded = expandable && (state.expandedSites.has(siteExpansionKey(site)) || childMatch && !state.collapsedSiteSearches.has(siteSearchExpansionKey(site))), serverId = state.serverId;
    row.dataset.siteName = site.name; row.dataset.siteId = String(site.id); row.className = `site-parent-row${expandable ? " is-expandable" : ""}${expanded ? " is-expanded" : ""}${state.siteAnnotationEditor?.siteId === site.id && state.siteAnnotationEditor.siteName === site.name && !state.siteAnnotationEditor.applicationPath ? " is-annotation-editing" : ""}`; name.className = "site-name-cell";
    const counts = children ? [children.applications.length ? `${children.applications.length} 个应用` : "", children.directories.length ? `${children.directories.length} 个附加虚拟目录` : ""].filter(Boolean) : [site.serverAutoStart ? "自动启动" : "手动启动"];
    const caption = element("div", [`ID ${site.id}`, ...counts].join(" · "), "resource-detail muted"); caption.title = `站点${site.serverAutoStart ? "自动启动" : "手动启动"}；子表不重复显示站点根应用及各应用的根目录映射。`; name.append(caption);
    if (expandable) {
      const inspect = element("button", undefined, "secondary site-expand-toggle"); inspect.type = "button"; inspect.id = `site-details-toggle-${site.id}`;
      inspect.dataset.expanded = String(!!expanded);
      const chevron = element("img", undefined, "ui-icon"); chevron.src = `/assets/fluent/chevron-${expanded ? "down" : "right"}.svg`; chevron.alt = ""; inspect.append(chevron);
      inspect.setAttribute("aria-label", `${expanded ? "收起" : "展开"} ${site.name} 的应用与目录`); inspect.setAttribute("aria-expanded", String(expanded)); inspect.setAttribute("aria-controls", `site-details-${site.id}`);
      inspect.addEventListener("click", () => toggleSiteDetails(site, serverId)); name.append(inspect);
    }
    const status = element("td"); status.append(element("span", iisStateLabels[site.state] || site.state, `badge ${site.state === "Started" ? "good" : ""}`));
    const target = element("td"); target.append(poolLink(site.applicationPoolName), element("div", site.physicalPath, "resource-detail resource-path muted"));
    if (sitePool && normalized(site.applicationPoolName) !== normalized(sitePool)) target.append(element("div", `子应用使用 ${sitePool}`, "resource-detail muted"));
    const bindings = element("td");
    site.bindings.forEach(binding => {
      const preview = applications.find(application => application.path === "/") || applications[0];
      const line = element("div", undefined, "binding-line"), url = preview ? applicationUrl(binding, preview) : detail && sitePool ? "" : siteBindingUrl(binding);
      const config = `${binding.ipAddress}:${binding.port} ${binding.hostName || "（无主机名）"}`;
      const localBinding = isLoopbackHost(websiteHost(binding.hostName || binding.ipAddress));
      line.append(element("span", binding.protocol.toUpperCase(), "protocol-tag"));
      const configuration = element("details", undefined, "site-binding-config"), summary = element("summary", "绑定配置"), metadata = element("div");
      configuration.append(summary, metadata);
      if (url) {
        const link = element("a", url, "binding-link"); link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
        link.setAttribute("aria-label", `在新标签页打开 ${site.name}：${url}`);
        line.className += " has-address";
        line.append(link); metadata.append(element("div", config, "resource-detail muted"));
        if (localBinding) metadata.append(element("div", "仅服务器本机访问", "resource-detail muted"));
      } else {
        line.append(element("span", config));
        if (["http", "https"].includes(normalized(binding.protocol))) line.append(element("div", localBinding ? "仅服务器本机访问" : "此绑定无法生成访问地址", "resource-detail muted"));
      }
      if (normalized(binding.protocol) === "https") metadata.append(element("div", `证书 ${binding.certificateThumbprint || "未知"} ${binding.useSni ? "· SNI" : ""}`, "resource-detail muted"));
      metadata.append(element("div", preview ? `${applicationLabel(preview)} · ${preview.applicationPoolName}` : "根应用（/）", "binding-application"));
      line.append(configuration);
      bindings.append(line);
    });
    const actions = element("td", undefined, "row-actions site-actions");
    siteActions.forEach(([type, title]) => {
      const button = element("button", title, "secondary small-button"); button.type = "button"; button.dataset.operation = type;
      button.dataset.unavailable = String(!["Started", "Stopped"].includes(site.state) || (type === "site.start" ? site.state === "Started" : site.state !== "Started"));
      button.dataset.operationTitle = `${title}站点 ${site.name}`;
      button.setAttribute("aria-label", `${title}站点 ${site.name}`);
      button.addEventListener("click", () => openSiteOperation(type, site)); actions.append(button);
    });
    actions.append(maintenanceEditButton(site));
    if (!["Started", "Stopped"].includes(site.state)) actions.append(element("div", "状态未稳定，请刷新快照后核对。", "resource-detail muted"));
    row.append(name, status, target, bindings, actions); sites.append(row);
    if (expanded) sites.append(renderSiteDetails(site, detail, sitePool, childQuery));
  });
  renderResourceResult("pool", "应用程序池", visiblePools.length, applicationPools.length);
  renderResourceResult("site", "站点", visibleSites.length, allSites.length);
  renderSiteTagFilters(); renderSiteAnnotationStatus(); renderSiteAnnotationEditor();
  renderResourceDialog();
  updateButtons();
}
function resetResourceFilters(kind) {
  const ids = kind === "pool" ? ["pool-search", "pool-state", "pool-runtime"] : kind === "site" ? ["site-search", "site-state", "site-pool", "site-protocol", "site-port"] : kind === "service" ? ["service-search", "service-state", "service-start-type"] : ["pool-search", "pool-state", "pool-runtime", "site-search", "site-state", "site-pool", "site-protocol", "site-port", "service-search", "service-state", "service-start-type"];
  ids.forEach(id => { byId(id).value = ""; });
  if (!kind || kind === "site") { state.siteTagFilters = []; byId("site-tag-search").value = ""; toggleSiteTagOptions(false); }
}
function focusOverviewSite(siteName, search = "") {
  activateResourceTab("sites");
  const site = state.snapshot?.sites.find(item => normalized(item.name) === normalized(siteName));
  byId("site-search").value = String(site?.name || siteName || search).slice(0, 512);
  if (site) state.expandedSites.add(siteExpansionKey(site));
  renderSnapshot();
  if (site) byId(`site-details-toggle-${site.id}`)?.scrollIntoView?.({ block: "center" });
  else if (siteName) notice("总览中的站点未出现在当前快照，请刷新总览核对。", true);
}
function activateResourceTab(kind, focus = false) {
  if (state.annotationBusy && kind !== state.activeResource) return;
  if (kind !== "sites" && state.siteAnnotationEditor) closeSiteAnnotationEditor();
  state.activeResource = kind;
  ["pools", "sites", "services"].forEach(name => {
    const tab = byId(`${name}-tab`), selected = name === kind;
    tab.setAttribute("aria-selected", String(selected)); tab.tabIndex = selected ? 0 : -1;
    byId(`${name}-panel`).hidden = !selected;
  });
  const title = kind === "services" ? "Windows 服务" : kind === "sites" ? "站点" : "应用程序池";
  byId("resource-heading").textContent = title; byId("resource-breadcrumb").textContent = title;
  byId("create-pool-button").hidden = kind !== "pools"; byId("create-site-button").hidden = kind !== "sites";
  byId("create-service-button").hidden = kind !== "services";
  byId("resource-overview").hidden = kind !== "pools";
  byId("topology-status").hidden = kind === "services" || !state.snapshot || state.topologyStatus === "ready";
  if (focus) byId(`${kind}-tab`).focus();
}
function selectServer(serverId) {
  const changed = state.serverId !== serverId; state.serverId = serverId;
  if (changed) {
    state.siteAnnotationsRevision++; state.siteAnnotations = null; state.siteAnnotationsStatus = "idle"; state.siteAnnotationsError = ""; state.siteTagFilters = []; state.annotationBusy = false;
    closeSiteAnnotationEditor(true, false); toggleSiteTagOptions(false); byId("site-tag-search").value = "";
    state.expandedSites.clear(); state.collapsedSiteSearches.clear();
    state.servicesRevision++; state.services = null; state.servicesStatus = "idle"; state.servicesError = ""; closeServiceDetails();
    state.topologyRevision++; state.topology = null; state.topologyStatus = "idle"; state.topologyError = "";
    if (state.resourceContext) closeResourceDialog();
    state.snapshot = null; state.snapshotStatus = "idle"; state.info = null; state.certificates = []; resetResourceFilters(); syncSitePoolFilter([], []);
    byId("machine-info").textContent = serverId ? "正在读取服务器清单…" : "尚未选择管理服务器";
    byId("collected-at").textContent = "等待最新快照"; byId("snapshot-summary").textContent = "";
  }
  renderServerTitle(); renderServers(); if (changed) { renderSnapshot(); renderServices(); }
}
function field(label, id, options, value, wide = false) {
  const wrapper = element("div", undefined, `field${wide ? " field-wide" : ""}`), title = element("label", label, "field-label"); title.htmlFor = id;
  const control = element(options ? "select" : "input"); control.id = id; control.name = id;
  if (options) options.forEach(([optionValue, text]) => { const option = element("option", text); option.value = String(optionValue); control.append(option); });
  control.value = value === undefined || value === null ? "" : String(value); wrapper.append(title, control); return { wrapper, control };
}
function sitePoolOptions() {
  return (state.snapshot?.applicationPools || []).map(pool => pool.name)
    .sort((left, right) => left.localeCompare(right, "zh-CN", { numeric: true }));
}
function syncCreateSitePools(selected = byId("create-site-pool").value) {
  const selector = byId("create-site-pool"), query = normalized(byId("create-site-pool-search").value);
  const names = sitePoolOptions().filter(name => normalized(name).includes(query));
  const placeholder = element("option", names.length ? "请选择应用程序池" : query ? "没有匹配的应用程序池" : "暂无应用程序池，请先新建"); placeholder.value = "";
  selector.replaceChildren(placeholder);
  names.forEach(name => { const option = element("option", name); option.value = name; selector.append(option); });
  selector.value = names.includes(selected) ? selected : "";
}
function createSitePoolField() {
  const names = sitePoolOptions();
  const pool = field("应用程序池", "create-site-pool", [["", names.length ? "请选择应用程序池" : "暂无应用程序池，请先新建"], ...names.map(name => [name, name])], names[0]); pool.control.required = true;
  const tools = element("div", undefined, "pool-picker-tools"), create = element("button", "新建", "secondary"); create.type = "button"; create.id = "create-site-pool-button";
  create.addEventListener("click", openSitePoolDialog);
  const search = element("input"); search.type = "search"; search.id = "create-site-pool-search"; search.placeholder = "输入应用程序池名称过滤"; search.autocomplete = "off"; search.setAttribute("aria-label", "按名称过滤应用程序池"); search.setAttribute("aria-controls", "create-site-pool");
  search.addEventListener("input", () => { syncCreateSitePools(); invalidateValidation(); }); tools.append(search, create); pool.wrapper.insertBefore(tools, pool.control);
  const status = element("span", "", "field-hint"); status.id = "create-site-pool-status"; status.hidden = true; status.setAttribute("role", "status"); status.setAttribute("aria-live", "polite");
  const query = element("button", "核对创建结果", "secondary small-button"); query.type = "button"; query.id = "create-site-pool-query"; query.hidden = true; query.addEventListener("click", () => queryTask());
  pool.wrapper.append(status, query); return pool;
}
function openSitePoolDialog() {
  if (state.busy || state.loading || hasPendingTask() || !state.snapshot || state.draft?.type !== "site.create") return;
  state.siteDraft = state.draft;
  openOperation("applicationPool.create");
}
function closeSitePoolDialog() {
  if (!state.siteDraft) return;
  byId("inline-pool-operation-dialog").close();
  state.draft = state.siteDraft; state.siteDraft = null;
  invalidateValidation(); formError(""); updateButtons();
}
function renderSitePoolTask() {
  const saved = state.sitePoolTask, task = state.task;
  if (!saved || !task || task.serverId !== saved.serverId || task.request.operationId !== saved.operationId || state.draft !== saved.draft) return;
  const status = byId("create-site-pool-status"); status.hidden = false;
  status.textContent = `应用程序池 ${saved.name}：${statusLabels[task.state] || task.state}${task.error?.message ? `。${task.error.message}` : ""}`;
  byId("create-site-pool-query").hidden = ["failed", "expired"].includes(task.state);
}
function selectCreatedSitePool() {
  const saved = state.sitePoolTask;
  if (!saved || state.task?.state !== "succeeded" || state.task.serverId !== saved.serverId || state.task.request.operationId !== saved.operationId) return;
  if (state.draft !== saved.draft || state.serverId !== saved.serverId) { state.sitePoolTask = null; return; }
  const status = byId("create-site-pool-status"); status.hidden = false;
  if (!state.snapshot?.applicationPools.some(pool => pool.name === saved.name)) {
    status.textContent = `应用程序池 ${saved.name} 创建成功，尚未从最新快照确认，请核对创建结果。`; return;
  }
  byId("create-site-pool-search").value = ""; syncCreateSitePools(saved.name); invalidateValidation();
  status.textContent = `已创建并选中 ${saved.name}。`; byId("create-site-pool-query").hidden = true; state.sitePoolTask = null;
}
function openSiteOperation(type, site) {
  if (state.busy || state.annotationBusy || state.loading || hasPendingTask() || !state.snapshot || !siteActions.some(([action]) => action === type)) return;
  const current = state.snapshot.sites.find(item => item.id === site.id && item.name === site.name);
  if (!current || !["Started", "Stopped"].includes(current.state) || (type === "site.start" ? current.state !== "Stopped" : current.state !== "Started")) return;
  state.draft = { type, site: current, serverId: state.serverId }; state.validation = null;
  byId("dialog-title").textContent = actionLabels[type];
  byId("dialog-description").textContent = `${state.servers.find(server => server.id === state.serverId)?.name || state.serverId} · ${current.name}。站点操作保留绑定、目录及自动启动配置。`;
  const container = byId("form-fields"); container.replaceChildren(); formError("");
  byId("validation-section").hidden = true; byId("submit-operation-button").hidden = true;
  container.append(element("p", `目标站点：${current.name}（ID ${current.id}）`, "field-wide"));
  container.append(element("p", "站点和应用程序池分别管理。停止站点不会停止应用程序池；如需启停应用池，请进入左侧“应用程序池”页面。", "field-hint field-wide"));
  if (type !== "site.start") container.append(element("p", "这会中断整个站点及其子应用、虚拟目录的访问。请在预检查中确认影响范围。", "field-hint field-wide"));
  if (type === "site.restart") container.append(element("p", "Agent 先停止并独立核对，再启动；此动作不回收应用池。", "field-hint field-wide"));
  container.oninput = invalidateValidation; container.onchange = invalidateValidation;
  byId("operation-dialog").showModal(); updateButtons();
}
function openOperation(type, pool) {
  if (state.busy || state.annotationBusy || state.loading || hasPendingTask() || !state.snapshot) return;
  state.draft = { type, pool, serverId: state.serverId }; state.validation = null;
  operationNode("dialog-title").textContent = actionLabels[type];
  operationNode("dialog-description").textContent = state.siteDraft ? "创建成功后会自动选中此应用程序池，已填写的网站配置会保留。" : `${state.servers.find(server => server.id === state.serverId)?.name || state.serverId} · ${pool?.name || "新建配置"}。操作不会创建目录、上传文件或设置自定义账号密码。`;
  const container = operationNode("form-fields"), prefix = state.siteDraft ? "inline-pool-" : ""; container.replaceChildren(); formError("");
  operationNode("validation-section").hidden = true; operationNode("submit-operation-button").hidden = true;
  if (type === "applicationPool.create" || type === "applicationPool.update") {
    const name = field("应用程序池名称", `${prefix}target-name`, null, pool?.name || "", true); name.control.required = true; name.control.maxLength = 64; name.control.disabled = !!pool; container.append(name.wrapper);
    poolFields.forEach(([key, label, options, defaultValue]) => {
      const item = field(label, `${prefix}pool-${key}`, options, pool ? pool[key] : defaultValue);
      if (key === "idleTimeoutMinutes") { item.control.type = "number"; item.control.min = "0"; item.control.step = "1"; item.control.required = true; }
      if (pool) {
        const toggle = element("input"); toggle.type = "checkbox"; toggle.id = `change-${key}`;
        const toggleLabel = element("label", undefined, "checkbox-label"); toggleLabel.append(toggle, element("span", "修改此项")); item.wrapper.prepend(toggleLabel); item.control.disabled = true;
        toggle.addEventListener("change", () => { item.control.disabled = !toggle.checked; invalidateValidation(); });
      }
      container.append(item.wrapper);
    });
  } else if (type === "site.create") {
    state.sitePoolTask = null;
    const name = field("网站名称", "target-name", null, ""); name.control.required = true; name.control.maxLength = 64;
    const pool = createSitePoolField();
    const physicalPath = field("现有物理目录（服务器绝对路径）", "site-path", null, "", true); physicalPath.control.required = true; physicalPath.control.placeholder = "例如 D:\\Sites\\Example（必须已存在）";
    physicalPath.wrapper.append(element("span", "Agent 会检查目录是否存在；目录内容由运维人员准备。", "field-hint"));
    container.append(name.wrapper, pool.wrapper, physicalPath.wrapper);
    const bindingArea = element("div", undefined, "field-wide"); bindingArea.id = "binding-area"; container.append(bindingArea);
    const add = element("button", "添加绑定", "secondary"); add.type = "button"; add.addEventListener("click", () => { addBinding(); invalidateValidation(); }); container.append(add); addBinding();
  } else {
    container.append(element("p", `目标应用程序池：${pool.name}`, "field-wide"));
    if (type === "applicationPool.stop" || type === "applicationPool.recycle") container.append(element("p", "这可能中断关联应用的请求。请在预检查中确认影响范围。", "field-hint field-wide"));
  }
  container.oninput = invalidateValidation;
  container.onchange = invalidateValidation;
  operationNode("operation-dialog").showModal(); updateButtons();
}
function invalidateValidation() { state.formRevision++; state.validation = null; operationNode("validation-section").hidden = true; operationNode("submit-operation-button").hidden = true; }
let bindingIndex = 0;
function addBinding() {
  const index = ++bindingIndex, card = element("div", undefined, "binding-card"); card.dataset.binding = String(index);
  const title = element("div", undefined, "binding-title"); title.append(element("span", "网站绑定"));
  const remove = element("button", "移除", "secondary small-button"); remove.type = "button"; remove.addEventListener("click", () => { card.remove(); invalidateValidation(); }); title.append(remove); card.append(title);
  const protocol = field("协议", `binding-${index}-protocol`, [["http", "HTTP"], ["https", "HTTPS"]], "http");
  const ip = field("IP 地址", `binding-${index}-ip`, null, "*"); ip.control.required = true;
  const port = field("端口", `binding-${index}-port`, null, 80); port.control.type = "number"; port.control.min = "1"; port.control.max = "65535"; port.control.required = true;
  const host = field("主机名", `binding-${index}-host`, null, "");
  const now = Date.now();
  const certificates = state.certificates.filter(cert => cert.hasPrivateKey && Date.parse(cert.notAfter) > now).map(cert => [cert.thumbprint, `${cert.friendlyName || cert.subject} · 到期 ${date(cert.notAfter)} · ${cert.thumbprint}`]);
  const certificate = field("服务器 LocalMachine\\My 证书", `binding-${index}-cert`, [["", "请选择现有证书"], ...certificates], "", true); certificate.control.disabled = true;
  const sni = element("label", undefined, "checkbox-label field-wide"), sniControl = element("input"); sniControl.type = "checkbox"; sniControl.id = `binding-${index}-sni`; sniControl.disabled = true; sni.append(sniControl, element("span", "启用 SNI（需要主机名）"));
  protocol.control.addEventListener("change", () => { const https = protocol.control.value === "https"; if (port.control.value === "80" || port.control.value === "443") port.control.value = https ? "443" : "80"; certificate.control.disabled = !https; certificate.control.required = https; sniControl.disabled = !https; if (!https) sniControl.checked = false; invalidateValidation(); });
  card.append(protocol.wrapper, ip.wrapper, port.wrapper, host.wrapper, certificate.wrapper, sni); byId("binding-area").append(card);
}
function readDraft() {
  const draft = state.draft, target = {}, parameters = {};
  if (draft.type.startsWith("windowsService.")) {
    target.serviceName = draft.service?.name || byId("service-name").value.trim();
    if (draft.type === "windowsService.create") {
      Object.assign(parameters, { displayName: byId("service-display-name").value.trim(), description: byId("service-description").value.trim(), executablePath: byId("service-executable").value.trim(), arguments: lines(byId("service-arguments").value), startType: byId("service-start-type-input").value, accountName: byId("service-account").value, dependencies: lines(byId("service-dependencies").value, true) });
    } else if (draft.type === "windowsService.updateStartType") {
      parameters.startType = byId("service-start-type-input").value;
      if (parameters.startType === draft.service.startType) throw new Error("启动类型未变化，请选择需要修改的启动类型。");
    }
  }
  if (draft.type.startsWith("applicationPool.")) target.applicationPoolName = draft.pool?.name || operationNode("target-name").value.trim();
  if (siteActions.some(([type]) => type === draft.type)) target.siteName = draft.site.name;
  if (["applicationPool.create", "applicationPool.update"].includes(draft.type)) {
    poolFields.forEach(([key]) => {
      if (draft.pool && !byId(`change-${key}`).checked) return;
      const value = operationNode(`pool-${key}`).value;
      parameters[key] = ["enable32BitAppOnWin64", "autoStart"].includes(key) ? value === "true" : key === "idleTimeoutMinutes" ? Number(value) : value;
    });
    if (draft.pool && !Object.keys(parameters).length) throw new Error("请勾选至少一个需要修改的配置项。");
  }
  if (draft.type === "site.create") {
    target.siteName = byId("target-name").value.trim(); parameters.physicalPath = byId("site-path").value.trim(); parameters.applicationPoolName = byId("create-site-pool").value;
    parameters.bindings = [...document.querySelectorAll("[data-binding]")].map(card => {
      const prefix = `binding-${card.dataset.binding}-`, protocol = byId(prefix + "protocol").value;
      return { protocol, ipAddress: byId(prefix + "ip").value.trim(), port: Number(byId(prefix + "port").value), hostName: byId(prefix + "host").value.trim(), certificateThumbprint: protocol === "https" ? byId(prefix + "cert").value : null, useSni: protocol === "https" && byId(prefix + "sni").checked };
    });
    if (!parameters.bindings.length) throw new Error("网站必须至少有一个绑定。");
    if (parameters.bindings.some(binding => binding.useSni && !binding.hostName)) throw new Error("启用 SNI 的 HTTPS 绑定必须填写主机名。");
  }
  return { operationId: createOperationId(), type: draft.type, target, parameters, expectedFingerprint: null, expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString() };
}
async function validate(event) {
  event.preventDefault(); if (state.busy || state.loading || hasPendingTask() || !operationAllowed(state.draft?.type)) return;
  state.busy = true; state.validation = null; formError(""); updateButtons();
  try {
    const revision = state.formRevision;
    const request = readDraft();
    const validation = await api(serverPath("operations/validate", state.draft.serverId), request);
    if (revision !== state.formRevision) throw new Error("预检查期间配置输入发生变化，请重新执行预检查。");
    state.draft.request = { ...request, expectedFingerprint: validation.fingerprint };
    state.validation = validation; renderValidation(validation);
  } catch (error) { formError(error.message); }
  finally { state.busy = false; updateButtons(); }
}
function renderValidation(validation) {
  operationNode("validation-section").hidden = false;
  operationNode("validation-state").textContent = validation.valid ? "预检查通过。请确认下方差异与影响后提交。" : "预检查未通过，请修正配置。";
  operationNode("validation-state").className = validation.valid ? "good-text" : "danger-text";
  const errors = operationNode("validation-errors"), warnings = operationNode("validation-warnings"), changes = operationNode("validation-changes"), applications = operationNode("validation-applications");
  errors.replaceChildren(); warnings.replaceChildren(); changes.replaceChildren(); applications.replaceChildren();
  validation.errors.forEach(error => errors.append(element("p", `${error.message} (${error.code})`, "danger-text")));
  validation.warnings.forEach(warning => warnings.append(element("p", warning, "notice")));
  validation.changes.forEach(change => { const row = element("div", undefined, "change-row"); row.append(element("strong", change.field), element("span", change.before || "（空）"), element("span", `→ ${change.after || "（空）"}`)); changes.append(row); });
  if (!validation.changes.length) changes.append(element("p", "无配置字段差异；运行状态操作仍可能影响请求。", "muted small"));
  if (validation.affectedApplications.length) {
    const references = state.draft?.serverId === state.serverId ? (state.topology?.sites || []).flatMap(site => site.applications.map(application => ({ site, application, key: normalized(site.name + application.path) }))) : [];
    const matches = validation.affectedApplications.map(name => references.filter(reference => reference.key === normalized(name)));
    const siteCount = matches.every(rows => rows.length === 1) ? new Set(matches.map(rows => String(rows[0].site.id))).size : null;
    applications.append(element("p", `${validation.affectedApplications.length} 个受影响应用${siteCount === null ? "" : ` · ${siteCount} 个站点`}`, "small"));
    const list = element("ul"); validation.affectedApplications.forEach(name => list.append(element("li", name))); applications.append(list);
  }
  else applications.append(element("p", "未列出关联应用。", "muted small"));
  const services = byId("validation-services"); services.replaceChildren();
  byId("validation-applications-area").hidden = state.draft?.type.startsWith("windowsService.");
  const affectedServices = validation.affectedServices || [];
  if (affectedServices.length) { const list = element("ul"); affectedServices.forEach(name => list.append(element("li", name))); services.append(list); }
  else services.append(element("p", "未列出受影响服务。", "muted small"));
  byId("validation-services-area").hidden = !state.draft?.type.startsWith("windowsService.") && !affectedServices.length;
  operationNode("validation-fingerprint").textContent = validation.fingerprint;
  const proposed = validation.proposed || state.draft.request.parameters;
  const safe = {};
  for (const key of ["name", "state", "serverAutoStart", "physicalPath", "applicationPoolName", "managedRuntimeVersion", "managedPipelineMode", "enable32BitAppOnWin64", "autoStart", "startMode", "idleTimeoutMinutes", "idleTimeoutAction", "displayName", "description", "executablePath", "startType", "accountName", "dependencies"])
    if (Object.prototype.hasOwnProperty.call(proposed, key)) safe[key] = proposed[key];
  if (state.draft.type === "windowsService.create") safe.arguments = state.draft.request.parameters.arguments;
  if (Array.isArray(proposed.bindings)) safe.bindings = proposed.bindings.map(binding => ({ protocol: binding.protocol, ipAddress: binding.ipAddress, port: binding.port, hostName: binding.hostName, certificateThumbprint: binding.certificateThumbprint, useSni: binding.useSni }));
  operationNode("validation-proposed").textContent = JSON.stringify({ target: state.draft.request.target, parameters: safe }, null, 2);
  operationNode("submit-operation-button").hidden = !validation.valid;
}
async function submitOperation() {
  if (!operationAllowed(state.draft?.type) || state.busy || state.loading || hasPendingTask() || !state.validation?.valid || !state.draft?.request) return;
  const request = state.draft.request, serverId = state.draft.serverId;
  if (new Date(request.expiresAt).getTime() <= Date.now()) { invalidateValidation(); formError("预检查已过期，请重新执行预检查。"); return; }
  state.busy = true; state.taskHealthReadError = false; updateButtons(); rememberIntent(serverId, request.operationId);
  if (state.siteDraft) state.sitePoolTask = { serverId, operationId: request.operationId, name: request.target.applicationPoolName, draft: state.siteDraft };
  try {
    const task = await api(serverPath("operations", serverId), request);
    state.task = task; state.taskHealthReadError = false; rememberTask(task);
    if (state.siteDraft) closeSitePoolDialog(); else byId("operation-dialog").close();
    renderTask(); schedulePoll();
    await refreshSiteTask(task);
    if (state.sitePoolTask && task.state === "succeeded") { await refresh(); selectCreatedSitePool(); }
    else if (request.type.startsWith("windowsService.") && task.state === "succeeded" && serverId === state.serverId) await refreshServices(serverId);
  } catch (error) {
    // A lost console response is uncertain. Preserve the same ID and never submit it again automatically.
    state.task = { serverId, request, state: "unknown", updatedAt: new Date().toISOString(), error: { code: "CONSOLE_RESPONSE_UNKNOWN", message: `${error.message} 请按原操作编号核对，不要重新提交。` } };
    state.validation = null;
    if (state.siteDraft) closeSitePoolDialog(); else byId("operation-dialog").close();
    renderTask();
    await refreshSiteTask(state.task);
  } finally { state.busy = false; updateButtons(); }
}
function renderTask() {
  if (!state.task) { byId("task-section").hidden = true; return; }
  const task = state.task; byId("task-section").hidden = false;
  byId("task-status").textContent = statusLabels[task.state] || task.state;
  byId("task-status").className = task.state === "succeeded" ? "good-text" : ["unknown", "partial", "failed"].includes(task.state) ? "danger-text" : "muted";
  byId("task-id").textContent = task.request.operationId;
  byId("task-operator").textContent = typeof task.operatorName === "string" && task.operatorName.trim() ? task.operatorName.trim() : "未记录";
  byId("task-persistence").textContent = Object.prototype.hasOwnProperty.call(persistenceLabels, task.persistenceState) ? persistenceLabels[task.persistenceState] : "未记录";
  byId("task-target").textContent = `${actionLabels[task.request.type] || task.request.type} · ${task.request.target.serviceName || (task.request.target.siteName ? task.request.target.siteName + (task.request.target.applicationPath || "") + (task.request.target.virtualDirectoryPath || "") : null) || task.request.target.applicationPoolName || "—"}`;
  const serviceTarget = byId("task-service-target"); serviceTarget.hidden = !task.request.target.serviceName || task.serverId !== state.serverId;
  serviceTarget.textContent = task.request.target.serviceName ? `查看服务 ${task.request.target.serviceName}` : "";
  byId("task-updated").textContent = date(task.updatedAt);
  const message = task.error?.message || (["unknown", "partial"].includes(task.state) ? "需要按原编号及保留参数进行只读核对。控制台不会自动重放此操作。" : "");
  byId("task-message").textContent = message; byId("task-message").hidden = !message;
  byId("release-task-area").hidden = !["unknown", "partial"].includes(task.state);
  byId("task-json").textContent = JSON.stringify({ request: task.request, operatorName: task.operatorName ?? null, state: task.state, persistenceState: task.persistenceState ?? null, before: task.agentRecord?.before, after: task.agentRecord?.after, error: task.error }, null, 2);
  globalThis.IISOperationHealth?.render(byId("task-health-follow-up"), task.healthFollowUp, { readError: !!task.healthFollowUp && state.taskHealthReadError });
  renderSitePoolTask();
  updateButtons();
}
async function releaseTask() {
  if (state.busy || state.annotationBusy || state.loading || !state.task || !["unknown", "partial"].includes(state.task.state)) return;
  const operationId = state.task.request.operationId;
  rememberTask(state.task); clearTimeout(state.timer); state.task = null; state.validation = null;
  renderTask(); updateButtons();
  await refresh();
  const message = `已保留操作 ${operationId} 的原记录与参数，管理锁定已解除。原状态未改变，控制台不会重放。`;
  if (state.snapshotStatus === "ready") notice(`${message} 已刷新当前服务器快照，请按最新状态继续管理。`);
  else notice(`${message} 当前快照未能刷新，请先点击“刷新快照”后继续管理。`, true);
}
function schedulePoll() {
  clearTimeout(state.timer);
  if (document.visibilityState === "hidden") return;
  if (state.task && (["queued", "running"].includes(state.task.state) || pendingTaskHealth()))
    state.timer = setTimeout(() => queryTask(true), pendingTaskHealth() ? state.taskHealthReadError ? 10000 : 5000 : 1500);
}
function pendingTaskHealth(task = state.task) { return task?.state === "succeeded" && globalThis.IISOperationHealth?.isPending(task.healthFollowUp) === true; }
async function refreshSiteTask(task) {
  if (task.serverId !== state.serverId || !siteActions.some(([type]) => type === task.request.type) || !["succeeded", "partial", "unknown"].includes(task.state)) return false;
  // The receipt and management lock stay intact. Read current status independently rather than
  // display an old pre-submit snapshot or assume the receipt's after snapshot is still current.
  await refresh();
  return true;
}
async function queryTask(automatic = false) {
  if (automatic && document.visibilityState === "hidden") return;
  if (!state.task || state.busy) { if (automatic) schedulePoll(); return; }
  const saved = state.task, healthOnly = automatic && pendingTaskHealth(saved); state.busy = true; updateButtons();
  try {
    const task = await api(serverPath(`operations/${encodeURIComponent(saved.request.operationId)}`, saved.serverId));
    if (task?.serverId !== saved.serverId || typeof task?.request?.operationId !== "string" || task.request.operationId.toLowerCase() !== saved.request.operationId.toLowerCase()) throw new Error("返回的操作记录与原编号不一致，请保留原记录核对。");
    state.task = task; state.taskHealthReadError = false;
    rememberTask(state.task); renderTask();
    const refreshedSite = healthOnly ? false : await refreshSiteTask(state.task);
    if (!refreshedSite && state.task.state === "succeeded" && (saved.state !== "succeeded" || state.sitePoolTask?.operationId === state.task.request.operationId) && state.task.serverId === state.serverId) {
      if (state.task.request.type.startsWith("windowsService.")) await refreshServices(); else { await refresh(); selectCreatedSitePool(); }
    }
  } catch (error) {
    if (saved.state === "succeeded" && saved.healthFollowUp != null) {
      state.task = saved; state.taskHealthReadError = true; renderTask();
    } else {
      state.task = { ...saved, state: "unknown", updatedAt: new Date().toISOString(), error: { code: "QUERY_RESULT_UNKNOWN", message: `${error.message} 此结果不能证明 Agent 未执行，请保留原编号。` } }; renderTask();
      await refreshSiteTask(state.task);
    }
  } finally { state.busy = false; updateButtons(); schedulePoll(); }
}
async function restoreTask(row) {
  if (state.busy || state.annotationBusy || state.loading) return;
  clearTimeout(state.timer);
  if (state.servers.some(server => server.id === row.serverId)) {
    selectServer(row.serverId); await refresh();
  }
  state.task = { serverId: row.serverId, request: { operationId: row.operationId, type: "恢复操作", target: {} }, state: "unknown", updatedAt: null };
  state.taskHealthReadError = false;
  renderTask(); await queryTask();
  if (state.task?.request.target.serviceName && state.task.serverId === state.serverId) showWindowsService(state.task.request.target.serviceName);
}
function renderServerTitle() {
  const server = state.servers.find(candidate => candidate.id === state.serverId);
  byId("server-title").textContent = server?.name || "MKY 服务器远程管理工具";
  byId("server-mode").textContent = server?.mode === "Simulation" ? "Development · 模拟数据，不管理真实 IIS" : "Windows Agent";
}
byId("server-search").addEventListener("input", renderServers);
document.querySelectorAll("[data-pool-state]").forEach(button => button.addEventListener("click", () => { byId("pool-state").value = button.dataset.poolState; renderSnapshot(); }));
byId("refresh-button").addEventListener("click", refresh);
byId("resource-close").addEventListener("click", closeResourceDialog);
byId("resource-dialog").addEventListener("close", () => { state.resourceContext = null; });
byId("resource-search").addEventListener("input", renderResourceDialog);
byId("resource-retry").addEventListener("click", () => refreshTopology());
byId("service-close").addEventListener("click", closeServiceDetails);
byId("service-dialog").addEventListener("close", () => { state.serviceDetailRevision++; state.serviceContext = null; });
byId("service-retry").addEventListener("click", () => refreshServices());
byId("service-search").addEventListener("input", renderServices);
["service-state", "service-start-type"].forEach(id => byId(id).addEventListener("change", renderServices));
byId("service-reset").addEventListener("click", () => { resetResourceFilters("service"); renderServices(); });
byId("site-tags-toggle").addEventListener("click", () => toggleSiteTagOptions());
byId("site-tags-clear").addEventListener("click", () => { state.siteTagFilters = []; renderSnapshot(); });
byId("site-tag-search").addEventListener("input", renderSiteTagFilters);
byId("site-tag-options").addEventListener("keydown", event => { if (event.key === "Escape") { event.preventDefault(); toggleSiteTagOptions(false); byId("site-tags-toggle").focus(); } });
byId("site-annotations-retry").addEventListener("click", () => { if (!state.annotationBusy && !state.loading && !state.busy) return refreshSiteAnnotations(); });
byId("site-annotation-form").addEventListener("submit", saveSiteAnnotation);
byId("annotation-close").addEventListener("click", () => closeSiteAnnotationEditor());
byId("annotation-cancel").addEventListener("click", () => closeSiteAnnotationEditor());
byId("annotation-reconcile").addEventListener("click", reconcileSiteAnnotation);
byId("annotation-alias").addEventListener("input", () => { const editor = state.siteAnnotationEditor; if (editor && !state.annotationBusy) { editor.alias = byId("annotation-alias").value; if (!editor.needsReview) editor.error = ""; renderSiteAnnotationEditor(); } });
function toggleAnnotationSuggestions(open) {
  byId("annotation-tag-suggestions").hidden = !open; ["annotation-tag-input", "annotation-tag-toggle"].forEach(id => byId(id).setAttribute("aria-expanded", String(open)));
}
byId("annotation-tag-input").addEventListener("input", () => { const editor = state.siteAnnotationEditor; if (editor && !state.annotationBusy) { editor.tagInput = byId("annotation-tag-input").value; renderSiteAnnotationEditor(); toggleAnnotationSuggestions(true); } });
byId("annotation-tag-input").addEventListener("keydown", event => { if (event.key === "Enter" && !event.isComposing) { event.preventDefault(); addAnnotationTag(); } });
byId("annotation-tag-toggle").addEventListener("click", () => { renderSiteAnnotationEditor(); toggleAnnotationSuggestions(byId("annotation-tag-suggestions").hidden); byId("annotation-tag-input").focus(); });
byId("site-annotation-editor").addEventListener("keydown", event => { if (event.key === "Escape") { event.preventDefault(); if (!state.annotationBusy && !state.busy && !state.loading) closeSiteAnnotationEditor(); } });
document.addEventListener("click", event => { if (!byId("site-tag-options").hidden && !event.target.closest?.(".site-tag-filter")) toggleSiteTagOptions(false); });
byId("create-service-button").addEventListener("click", () => openServiceOperation("windowsService.create"));
byId("task-service-target").addEventListener("click", () => showWindowsService(state.task?.request.target.serviceName));
const resourceTabs = ["sites", "pools", "services"];
resourceTabs.forEach((name, index) => {
  const tab = byId(`${name}-tab`);
  tab.addEventListener("click", () => activateResourceTab(name));
  tab.addEventListener("keydown", event => {
    let target;
    if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) target = resourceTabs[(index + (["ArrowLeft", "ArrowUp"].includes(event.key) ? -1 : 1) + resourceTabs.length) % resourceTabs.length];
    else if (event.key === "Home") target = resourceTabs[0];
    else if (event.key === "End") target = "services";
    if (target) { event.preventDefault(); activateResourceTab(target, true); }
  });
});
["pool-search", "site-search", "site-port"].forEach(id => byId(id).addEventListener("input", () => { if (id === "site-search") state.collapsedSiteSearches.clear(); renderSnapshot(); }));
["pool-state", "pool-runtime", "site-state", "site-pool", "site-protocol"].forEach(id => byId(id).addEventListener("change", renderSnapshot));
["pool", "site"].forEach(kind => byId(`${kind}-reset`).addEventListener("click", () => { resetResourceFilters(kind); renderSnapshot(); }));
byId("create-pool-button").addEventListener("click", () => openOperation("applicationPool.create"));
byId("create-site-button").addEventListener("click", () => openOperation("site.create"));
byId("close-dialog").addEventListener("click", () => { if (!state.busy) byId("operation-dialog").close(); });
byId("operation-dialog").addEventListener("cancel", event => { if (state.busy) event.preventDefault(); });
byId("inline-pool-close-dialog").addEventListener("click", () => { if (!state.busy) closeSitePoolDialog(); });
byId("inline-pool-operation-dialog").addEventListener("cancel", event => { event.preventDefault(); if (!state.busy) closeSitePoolDialog(); });
byId("inline-pool-operation-form").addEventListener("submit", validate);
byId("inline-pool-submit-operation-button").addEventListener("click", submitOperation);
byId("operation-form").addEventListener("submit", validate);
byId("submit-operation-button").addEventListener("click", submitOperation);
byId("query-task-button").addEventListener("click", () => queryTask());
byId("release-task-button").addEventListener("click", releaseTask);
byId("task-lock-release").addEventListener("click", releaseTask);
document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") schedulePoll(); else clearTimeout(state.timer); });
globalThis.addEventListener?.("pagehide", () => clearTimeout(state.timer));
activateResourceTab("pools"); renderSnapshot(); renderServices(); renderServers();
(async () => {
  try {
    const [session, servers, permissions] = await Promise.all([api("/api/v1/session"), api("/api/v1/servers"), api("/api/v1/permissions")]);
    state.csrf = session.csrfToken; state.servers = servers; state.permissions = permissions;
    globalThis.IISManagerAuth?.render(session);
    renderServers();
    if (!servers.length) { byId("machine-info").textContent = "尚未配置管理服务器"; notice("请由运维人员配置服务器清单、来源 IP 范围和对应的通信认证设置后重启控制台。"); updateButtons(); return; }
    const route = new URLSearchParams(globalThis.location?.search || "");
    const routeServer = route.get("server"), routeOperation = route.get("operation");
    selectServer(servers.some(server => server.id === routeServer) ? routeServer : servers[0].id);
    if (route.get("resource") === "services" && servers.some(server => server.id === routeServer)) showWindowsService(route.get("service") || "");
    if (routeOperation && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(routeOperation) && servers.some(server => server.id === routeServer)) {
      await restoreTask({ serverId: routeServer, operationId: routeOperation }); byId("task-section").scrollIntoView?.({ block: "start" }); return;
    }
    if (route.get("resource") === "services" && servers.some(server => server.id === routeServer)) { showWindowsService(route.get("service") || ""); await refresh(); showWindowsService(route.get("service") || ""); return; }
    if (route.get("resource") === "sites" && servers.some(server => server.id === routeServer)) {
      await refresh();
      focusOverviewSite(route.get("site"), route.get("search"));
      return;
    }
    const latest = readHistory()[0];
    if (latest && servers.some(server => server.id === latest.serverId)) await restoreTask(latest); else await refresh();
  } catch (error) { state.snapshotStatus = "failed"; renderSnapshot(); notice(error.message, true); updateButtons(); }
})();
