"use strict";

(() => {
  const actions = Object.freeze({ "site.update": "编辑站点", "application.create": "创建子应用", "application.update": "编辑子应用", "virtualDirectory.create": "创建虚拟目录", "virtualDirectory.update": "编辑虚拟目录" });
  const finalStates = new Set(["succeeded", "failed", "expired"]);
  const operationStates = new Set(["queued", "running", "succeeded", "failed", "partial", "unknown", "expired"]);
  const labels = { queued: "等待执行", running: "执行中", succeeded: "执行成功，配置已读回", failed: "执行失败，请核对错误", partial: "部分完成，请只读核实现场", unknown: "结果未知，请按原编号核实", expired: "操作已过期" };
  const indexKey = "iis-manager-maintenance-pending-v1";
  function canonicalPath(value, root = false) {
    return typeof value === "string" && value.length > 0 && value.length <= 512 && value.startsWith("/") &&
      (value === "/" ? root : !value.endsWith("/") && value.slice(1).split("/").every(segment => segment && ![".", ".."].includes(segment) && segment === segment.trim() && !segment.endsWith(".") && !/[\u0000-\u001f\u007f%\\?#:;*"<>|]/.test(segment)));
  }
  function permission(data, serverId) {
    const values = Array.isArray(data?.servers) ? data.servers : [];
    const row = values.find(value => (value.serverId ?? value.id) === serverId);
    return { read: row?.read === true, configure: row?.configure === true };
  }
  function buildDraft(value, id, now = Date.now()) {
    if (!actions[value.type] || !/^[A-Za-z0-9_-]{1,64}$/.test(value.serverId || "") || !value.siteName) throw new Error("请选择有效的服务器、站点和维护内容。");
    const target = { siteName: value.siteName }, parameters = {};
    const application = value.type.startsWith("application."), directory = value.type.startsWith("virtualDirectory.");
    if (application || directory) {
      if (!canonicalPath(value.applicationPath, directory)) throw new Error("请填写规范的应用路径，根应用请使用站点编辑。");
      target.applicationPath = value.applicationPath;
    }
    if (directory) {
      if (!canonicalPath(value.virtualDirectoryPath)) throw new Error("请填写相对所属应用的规范虚拟目录路径，不能使用 /。");
      target.virtualDirectoryPath = value.virtualDirectoryPath;
    }
    const creatingApplication = value.type === "application.create", creatingDirectory = value.type === "virtualDirectory.create";
    if (value.changePhysical || creatingApplication || creatingDirectory) {
      if (!value.physicalPath || value.physicalPath !== value.physicalPath.trim() || /[\u0000-\u001f\u007f]/.test(value.physicalPath)) throw new Error("请填写已存在的本机绝对物理目录。");
      parameters.physicalPath = value.physicalPath;
    }
    if (!directory && (value.changePool || creatingApplication)) {
      if (!value.applicationPoolName) throw new Error("请选择已有应用池。");
      parameters.applicationPoolName = value.applicationPoolName;
    }
    if (value.type === "virtualDirectory.update" && value.changePool) {
      if (!value.applicationPoolName) throw new Error("请选择所属应用要使用的已有应用池。");
      parameters.ownerApplicationPoolName = value.applicationPoolName;
    }
    if (value.type === "site.update" && value.changeBindings) {
      if (!Array.isArray(value.bindings) || value.bindings.length < 1 || value.bindings.length > 16) throw new Error("须保留 1–16 个站点绑定。");
      parameters.bindings = value.bindings.map(binding => {
        if (!["http", "https"].includes(binding.protocol) || !Number.isInteger(binding.port) || binding.port < 1 || binding.port > 65535 || !binding.ipAddress) throw new Error("请核对绑定协议、IP 与端口。");
        const item = { protocol: binding.protocol, ipAddress: binding.ipAddress, port: binding.port, hostName: binding.hostName || "", useSni: binding.protocol === "https" && !!binding.useSni };
        if (item.protocol === "https") {
          if (!item.hostName || !/^[a-fA-F0-9]{40}$/.test(binding.certificateThumbprint || "")) throw new Error("HTTPS 绑定需要域名和现有证书。");
          item.certificateThumbprint = binding.certificateThumbprint;
        }
        return item;
      });
    }
    if (!Object.keys(parameters).length) throw new Error("请勾选至少一项需修改的字段；未勾选字段会保留原值。");
    return { operationId: id, type: value.type, target, parameters, expectedFingerprint: null, expiresAt: new Date(now + 5 * 60000).toISOString() };
  }
  function validIndex(value) { return !!value && /^[A-Za-z0-9_-]{1,64}$/.test(value.serverId || "") && /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i.test(value.operationId || ""); }
  function createOperationId(browserCrypto = globalThis.crypto) {
    if (typeof browserCrypto?.randomUUID === "function") return browserCrypto.randomUUID();
    if (typeof browserCrypto?.getRandomValues !== "function") throw new Error("当前浏览器无法安全生成操作编号，请升级浏览器后重试。");
    const bytes = new Uint8Array(16); browserCrypto.getRandomValues(bytes);
    bytes[6] = bytes[6] & 15 | 64; bytes[8] = bytes[8] & 63 | 128;
    const value = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    return `${value.slice(0, 8)}-${value.slice(8, 12)}-${value.slice(12, 16)}-${value.slice(16, 20)}-${value.slice(20)}`;
  }
  function validValidation(value) { return !!value && typeof value.valid === "boolean" && /^[a-fA-F0-9]{64}$/.test(value.fingerprint || "") && ["changes", "warnings", "errors", "affectedApplications"].every(key => Array.isArray(value[key])); }
  function validTask(task, index) { return !!task && task.request?.operationId === index.operationId && task.serverId === index.serverId && operationStates.has(task.state); }
  function linkTarget(search) {
    const query = new URLSearchParams(search), keys = ["server", "site", "action", "application", "directory", "pool"];
    if (keys.some(key => query.getAll(key).length > 1)) throw new Error("维护链接包含重复目标，请从资源列表重新进入。");
    const serverId = query.get("server"), siteName = query.get("site"), type = query.get("action") || "site.update";
    const applicationPath = query.get("application"), virtualDirectoryPath = query.get("directory"), poolOnly = query.get("pool") === "1";
    if (serverId !== null && !/^[A-Za-z0-9_-]{1,64}$/.test(serverId) || siteName !== null && (!siteName || siteName.length > 64 || siteName !== siteName.trim() || siteName.endsWith(".") || /[\u0000-\u001f\u007f/\\:*?"<>|]/.test(siteName)) ||
        !["site.update", "application.update", "virtualDirectory.update"].includes(type) || query.has("pool") && (!poolOnly || !["site.update", "application.update"].includes(type)) ||
        (!siteName && ["action", "application", "directory", "pool"].some(key => query.has(key))) || siteName && !serverId ||
        type === "site.update" && (applicationPath !== null || virtualDirectoryPath !== null) ||
        type.startsWith("application.") && (!canonicalPath(applicationPath) || virtualDirectoryPath !== null) ||
        type.startsWith("virtualDirectory.") && (!canonicalPath(applicationPath, true) || !canonicalPath(virtualDirectoryPath)))
      throw new Error("维护链接的目标不完整或不合法，请从资源列表重新进入。");
    return { serverId, siteName, type, applicationPath, virtualDirectoryPath, poolOnly };
  }
  function resolveLinkTarget(target, topology) {
    if (!target?.siteName) return null;
    const current = topology.sites.find(item => item.name === target.siteName);
    const application = current?.applications?.find(item => item.path === (target.type === "site.update" ? "/" : target.applicationPath));
    const directory = application?.virtualDirectories?.find(item => item.path === target.virtualDirectoryPath);
    if (!current || !application || target.type.startsWith("virtualDirectory.") && !directory)
      throw new Error("链接指定的站点、应用或虚拟目录已不存在，请核对目标后重新选择；未自动改选其它资源。");
    return target;
  }
  const pure = Object.freeze({ actions, canonicalPath, permission, buildDraft, validIndex, createOperationId, validValidation, validTask, linkTarget, resolveLinkTarget });
  if (typeof module !== "undefined" && module.exports) module.exports = pure;
  globalThis.IISMaintenance = pure;
  if (typeof document === "undefined") return;

  const byId = id => document.getElementById(id);
  const state = { servers: [], permissions: null, permissionUnsupported: false, csrf: "", snapshot: null, topology: null, certificates: [],
    loading: false, busy: false, pending: null, draft: null, validation: null, timer: null, disposed: false, bindings: [], linkTarget: null, targetError: false };
  function node(tag, text) { const value = document.createElement(tag); if (text !== undefined) value.textContent = text; return value; }
  function notice(text = "", error = true) { const value = byId("notice"); value.textContent = text; value.hidden = !text; value.className = error ? "notice danger" : "notice"; }
  function serverId() { return byId("maintenance-server").value; }
  function site() { return state.topology?.sites?.find(item => item.name === byId("maintenance-site").value); }
  function type() { return byId("maintenance-action").value; }
  function path(suffix, id = serverId()) { return `/api/v1/servers/${encodeURIComponent(id)}/${suffix}`; }
  async function api(url, body) {
    const options = { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } };
    if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-IISManager-CSRF"] = state.csrf; options.body = JSON.stringify(body); }
    let response, result;
    try { response = await fetch(url, options); result = await response.json(); }
    catch { const error = new Error("无法确认请求结果，请保留原操作编号，只读查询核对，勿重复提交。"); error.transport = true; throw error; }
    globalThis.IISManagerAuth?.unauthorized(response, result);
    if (!response.ok) { const error = new Error(result.message || "请求未完成，请核对服务器和权限。"); error.status = response.status; error.code = result.code; throw error; }
    return result;
  }
  function select(id, rows, selected) {
    const control = byId(id); control.replaceChildren(...rows.map(([value, text]) => { const option = node("option", text); option.value = value; return option; }));
    control.value = rows.some(([value]) => value === selected) ? selected : rows[0]?.[0] || "";
  }
  function updateControls() {
    const allowed = permission(state.permissions, serverId());
    const locked = state.loading || state.busy || !!state.pending, writeLocked = locked || state.targetError;
    byId("maintenance-form").querySelectorAll("input, select, button").forEach(control => { control.disabled = writeLocked || !allowed.configure || !state.topology; });
    // Resource selection remains available to a read-only user; it never sends a mutation.
    ["maintenance-server", "maintenance-site", "maintenance-action", "maintenance-application", "maintenance-directory"].forEach(id => { byId(id).disabled = locked || id !== "maintenance-server" && !state.topology; });
    byId("maintenance-physical").disabled ||= !(byId("maintenance-change-physical").checked || type() === "application.create" || type() === "virtualDirectory.create");
    byId("maintenance-pool").disabled ||= !(byId("maintenance-change-pool").checked || type() === "application.create");
    byId("maintenance-bindings").querySelectorAll("input, select, button").forEach(control => { control.disabled = writeLocked || !allowed.configure || !byId("maintenance-change-bindings").checked; });
    byId("maintenance-add-binding").disabled = writeLocked || !allowed.configure || !byId("maintenance-change-bindings").checked || state.bindings.length >= 16;
    byId("maintenance-refresh").disabled = state.loading || state.busy;
    byId("maintenance-confirm").disabled = writeLocked || !allowed.configure;
    byId("maintenance-submit").disabled = writeLocked || !allowed.configure || !state.validation?.valid || !byId("maintenance-confirm").checked || Date.parse(state.draft?.expiresAt || "") <= Date.now();
    byId("maintenance-query").disabled = state.busy;
    const hint = byId("maintenance-permission"); hint.hidden = allowed.configure;
    hint.textContent = state.permissionUnsupported ? "当前 Console 未提供权限接口，仅可读取资源；请更新后再配置维护。" : "当前账号没有此服务器的配置权限，仅可读取资源。";
  }
  function invalidate() { state.draft = null; state.validation = null; byId("maintenance-preview").hidden = true; byId("maintenance-confirm").checked = false; updateControls(); }
  function setFields(target = null) {
    const current = site(), mode = type(), root = current?.applications?.find(item => item.path === "/");
    const applicationAction = mode.startsWith("application."), directoryAction = mode.startsWith("virtualDirectory.");
    byId("maintenance-application-field").hidden = mode === "site.update" || mode === "application.create";
    byId("maintenance-new-application-field").hidden = mode !== "application.create";
    byId("maintenance-directory-field").hidden = mode !== "virtualDirectory.update";
    byId("maintenance-new-directory-field").hidden = mode !== "virtualDirectory.create";
    byId("maintenance-bindings-field").hidden = mode !== "site.update";
    byId("maintenance-pool-field").hidden = mode === "virtualDirectory.create";
    const choices = (current?.applications || []).filter(item => directoryAction || item.path !== "/");
    select("maintenance-application", choices.map(item => [item.path, item.path === "/" ? "/（根应用）" : item.path]), target?.applicationPath || byId("maintenance-application").value);
    const application = mode === "site.update" ? root : current?.applications?.find(item => item.path === byId("maintenance-application").value);
    select("maintenance-directory", (application?.virtualDirectories || []).filter(item => item.path !== "/").map(item => [item.path, item.path]), target?.virtualDirectoryPath || byId("maintenance-directory").value);
    const selectedDirectory = application?.virtualDirectories?.find(item => item.path === byId("maintenance-directory").value);
    byId("maintenance-physical").value = mode === "application.create" || mode === "virtualDirectory.create" ? "" : directoryAction ? selectedDirectory?.physicalPath || "" : application?.physicalPath || "";
    select("maintenance-pool", (state.snapshot?.applicationPools || []).map(pool => [pool.name, `${pool.name} · ${pool.state}`]), application?.applicationPoolName || root?.applicationPoolName);
    byId("maintenance-change-physical").checked = mode === "application.create" || mode === "virtualDirectory.create";
    byId("maintenance-change-pool").checked = mode === "application.create" || !!target?.poolOnly;
    byId("maintenance-inherited-pool").hidden = !directoryAction;
    byId("maintenance-inherited-pool-name").textContent = application?.applicationPoolName || "未指定";
    byId("maintenance-pool-impact").textContent = mode === "site.update" ? "修改站点根应用的应用池，仅影响根应用及其虚拟目录；独立子应用保留各自的应用池。"
      : directoryAction ? `此处修改所属应用 ${application?.path || "（未选择）"} 的应用池，影响该应用及其全部虚拟目录；虚拟目录本身没有独立应用池。`
      : "修改后，此应用及其全部虚拟目录共同使用新应用池。";
    byId("maintenance-change-bindings").checked = false;
    state.bindings = (current?.bindings || []).map(binding => ({ ...binding })); renderBindings();
    byId("maintenance-current").textContent = current ? `站点状态：${current.state} · 当前 ${current.applications.length} 个应用。${applicationAction && mode === "application.update" && !choices.length ? "尚无可编辑的子应用。" : ""}` : "尚未取得站点与应用拓扑。";
    invalidate();
  }
  function renderBindings() {
    byId("maintenance-bindings").replaceChildren(...state.bindings.map((binding, index) => {
      const row = node("div"); row.className = "maintenance-binding";
      function field(label, key, tag = "input", options = null) {
        const wrapper = node("label", label), control = node(tag); control.setAttribute("aria-label", `绑定 ${index + 1} ${label}`);
        if (options) control.append(...options.map(([value, text]) => { const option = node("option", text); option.value = value; return option; }));
        control.value = String(binding[key] ?? "");
        if (key === "port") { control.type = "number"; control.min = "1"; control.max = "65535"; }
        control.addEventListener("change", () => { binding[key] = key === "port" ? Number(control.value) : control.value;
          if (key === "protocol" && control.value === "http") { binding.useSni = false; binding.certificateThumbprint = null; }
          invalidate(); if (key === "protocol") { renderBindings(); updateControls(); } });
        wrapper.append(control); row.append(wrapper); return wrapper;
      }
      field("协议", "protocol", "select", [["http", "HTTP"], ["https", "HTTPS"]]); field("IP 地址", "ipAddress"); field("端口", "port"); field("域名", "hostName");
      if (binding.protocol === "https") {
        const choices = state.certificates.map(certificate => [certificate.thumbprint, `${certificate.friendlyName || certificate.subject} · ${certificate.thumbprint}`]);
        if (binding.certificateThumbprint && !choices.some(([value]) => value === binding.certificateThumbprint)) choices.unshift([binding.certificateThumbprint, `当前证书（未能读取） ${binding.certificateThumbprint}`]);
        field("现有证书", "certificateThumbprint", "select", [["", "请选择证书"], ...choices]).className = "binding-certificate";
      }
      const actions = node("div"); actions.className = "binding-actions";
      if (binding.protocol === "https") { const label = node("label", "SNI"), sni = node("input"); label.className = "maintenance-checkbox"; sni.type = "checkbox"; sni.checked = !!binding.useSni; sni.addEventListener("change", () => { binding.useSni = sni.checked; invalidate(); }); label.append(sni); actions.append(label); }
      const remove = node("button", "移除绑定"); remove.type = "button"; remove.className = "text-button"; remove.addEventListener("click", () => { state.bindings.splice(index, 1); invalidate(); renderBindings(); updateControls(); }); actions.append(remove); row.append(actions); return row;
    }));
  }
  async function loadResources() {
    if (state.loading || state.busy) return;
    state.loading = true; state.snapshot = null; state.topology = null; invalidate(); notice("");
    try {
      const [snapshot, topology, certificates] = await Promise.all([api(path("snapshot")), api(path("site-topology")), api(path("certificates")).catch(() => [])]);
      if (!Array.isArray(snapshot.applicationPools) || !Array.isArray(snapshot.sites) || !Array.isArray(topology.sites)) throw new Error("站点资源响应不完整，请核对 Agent 版本。");
      state.snapshot = snapshot; state.topology = topology; state.certificates = Array.isArray(certificates) ? certificates : [];
      const target = state.linkTarget;
      select("maintenance-site", topology.sites.map(item => [item.name, `${item.name} · ${item.state}`]), target?.siteName || byId("maintenance-site").value);
      if (target?.siteName) {
        byId("maintenance-action").value = target.type;
        if (!topology.sites.some(item => item.name === target.siteName)) byId("maintenance-site").value = "";
        resolveLinkTarget(target, topology);
      }
      setFields(target); state.linkTarget = null; state.targetError = false;
    } catch (error) { state.targetError = true; notice(error.message); }
    finally { state.loading = false; updateControls(); }
  }
  function readForm() {
    return { serverId: serverId(), siteName: byId("maintenance-site").value, type: type(),
      applicationPath: type() === "application.create" ? byId("maintenance-new-application").value : byId("maintenance-application").value,
      virtualDirectoryPath: type() === "virtualDirectory.create" ? byId("maintenance-new-directory").value : byId("maintenance-directory").value,
      physicalPath: byId("maintenance-physical").value, applicationPoolName: byId("maintenance-pool").value,
      changePhysical: byId("maintenance-change-physical").checked, changePool: type() !== "virtualDirectory.create" && byId("maintenance-change-pool").checked,
      changeBindings: byId("maintenance-change-bindings").checked, bindings: state.bindings };
  }
  function showValidation(result) {
    byId("maintenance-preview").hidden = false; byId("maintenance-validity").textContent = result.valid ? "预检查通过，请核对以下差异及影响范围。" : "预检查未通过，未执行任何更改。";
    byId("maintenance-changes").replaceChildren(...result.changes.map(change => { const row = node("tr"); row.append(node("td", change.field), node("td", change.before), node("td", change.after)); return row; }));
    byId("maintenance-affected").replaceChildren(...result.affectedApplications.map(item => node("li", item)));
    byId("maintenance-warnings").replaceChildren(...result.warnings.map(item => node("li", item)));
    byId("maintenance-errors").replaceChildren(...result.errors.map(item => node("li", `${item.code} · ${item.message}`)));
  }
  async function precheck(event) {
    event.preventDefault(); if (state.busy || state.loading || state.pending || state.targetError || !permission(state.permissions, serverId()).configure) return;
    invalidate();
    try {
      const request = buildDraft(readForm(), createOperationId()); state.busy = true; updateControls();
      const validation = await api(path("operations/validate"), request);
      if (!validValidation(validation)) throw new Error("预检查响应不完整，禁止提交，请刷新核对。");
      state.draft = { ...request, expectedFingerprint: validation.fingerprint }; state.validation = validation; showValidation(validation); notice("");
    } catch (error) { notice(error.message); }
    finally { state.busy = false; updateControls(); }
  }
  function remember(index) {
    sessionStorage.setItem(indexKey, JSON.stringify(index));
    if (!validIndex(JSON.parse(sessionStorage.getItem(indexKey)))) throw new Error("浏览器无法保存操作索引，请先启用会话存储后再提交。");
    try {
      const rows = JSON.parse(localStorage.getItem("iis-manager-task-history-v1") || "[]");
      localStorage.setItem("iis-manager-task-history-v1", JSON.stringify([index, ...(Array.isArray(rows) ? rows.filter(row => row.operationId !== index.operationId) : [])].slice(0, 10)));
    } catch { /* The session index is already independently retained; the task stays visible here. */ }
  }
  function showPending(index, text) {
    byId("maintenance-task").hidden = false; byId("maintenance-operation-id").textContent = index.operationId;
    byId("maintenance-query-server").value = index.serverId; byId("maintenance-query-id").value = index.operationId;
    byId("maintenance-task-status").textContent = text; byId("maintenance-record-link").href = `/operations.html?operation=${encodeURIComponent(index.operationId)}&server=${encodeURIComponent(index.serverId)}`;
  }
  function applyTask(task, index) {
    if (!validTask(task, index)) throw new Error("任务响应与原编号不一致，保留锁定并只读核实。");
    showPending(index, labels[task.state]); byId("maintenance-task-result").textContent = JSON.stringify({ state: task.state, error: task.error || null,
      before: task.agentRecord?.before ?? task.before ?? null, after: task.agentRecord?.after ?? task.after ?? null }, null, 2);
    if (finalStates.has(task.state)) {
      sessionStorage.removeItem(indexKey); state.pending = null;
      const after = task.agentRecord?.after ?? task.after;
      if (task.state === "succeeded" && index.serverId === serverId() && after?.name === site()?.name &&
          Array.isArray(after?.applications) && Array.isArray(after?.bindings) &&
          after.applications.every(item => typeof item.path === "string" && Array.isArray(item.virtualDirectories))) {
        const selection = { applicationPath: byId("maintenance-application").value, virtualDirectoryPath: byId("maintenance-directory").value };
        state.topology = { ...state.topology, sites: state.topology.sites.map(item => item.name === after.name ? after : item) };
        setFields(selection);
      } else invalidate();
    }
    else { state.pending = index; if (["queued", "running"].includes(task.state) && !state.disposed) state.timer = setTimeout(queryPending, 1500); }
  }
  async function submit() {
    if (state.busy || state.loading || state.pending || state.targetError || !state.validation?.valid || !byId("maintenance-confirm").checked || !permission(state.permissions, serverId()).configure) return;
    if (Date.parse(state.draft.expiresAt) <= Date.now()) { invalidate(); notice("预检查已过期，请重新核对差异。"); return; }
    const index = { serverId: serverId(), operationId: state.draft.operationId };
    try { remember(index); } catch { notice("浏览器无法可靠保存操作编号，已阻止提交。请核对浏览器会话存储。"); return; }
    state.pending = index; state.busy = true; updateControls(); showPending(index, "已保留操作编号，正在提交；请勿重复操作。");
    try { applyTask(await api(path("operations", index.serverId), state.draft), index); notice(""); }
    catch (error) { showPending(index, "结果尚未确认，写入已锁定，请按原编号只读核对。"); notice(error.message); }
    finally { state.busy = false; updateControls(); }
  }
  async function queryPending() {
    clearTimeout(state.timer); if (state.busy) return;
    const index = state.pending || { serverId: byId("maintenance-query-server").value, operationId: byId("maintenance-query-id").value };
    if (!validIndex(index)) { notice("请输入有效服务器编号和原操作编号。"); return; }
    state.busy = true; updateControls();
    try { applyTask(await api(path(`operations/${encodeURIComponent(index.operationId)}`, index.serverId)), index); notice(""); }
    catch (error) { if (state.pending) showPending(index, "仍无法确认原任务结果，请保留编号并核实现场。"); notice(error.message); }
    finally { state.busy = false; updateControls(); }
  }
  byId("maintenance-form").addEventListener("submit", precheck);
  byId("maintenance-form").addEventListener("input", event => { if (event.target.id !== "maintenance-server") invalidate(); });
  byId("maintenance-form").addEventListener("change", event => { if (!["maintenance-server", "maintenance-site", "maintenance-action", "maintenance-application", "maintenance-directory"].includes(event.target.id)) invalidate(); });
  byId("maintenance-server").addEventListener("change", () => { state.linkTarget = null; state.targetError = false; return loadResources(); });
  ["maintenance-site", "maintenance-action", "maintenance-application", "maintenance-directory"].forEach(id => byId(id).addEventListener("change", () => { state.linkTarget = null; state.targetError = false; notice(""); setFields(); }));
  byId("maintenance-refresh").addEventListener("click", loadResources);
  byId("maintenance-confirm").addEventListener("change", updateControls);
  byId("maintenance-submit").addEventListener("click", submit);
  byId("maintenance-query").addEventListener("click", queryPending);
  byId("maintenance-add-binding").addEventListener("click", () => { state.bindings.push({ protocol: "http", ipAddress: "*", port: 80, hostName: "", useSni: false }); invalidate(); renderBindings(); updateControls(); });
  globalThis.addEventListener?.("pagehide", () => { state.disposed = true; clearTimeout(state.timer); });
  (async () => {
    state.loading = true; updateControls();
    try {
      const session = await api("/api/v1/session"); state.csrf = session.csrfToken; globalThis.IISManagerAuth?.render(session);
      try { state.permissions = await api("/api/v1/permissions"); }
      catch (error) { if (error.status !== 404) throw error; state.permissionUnsupported = true; }
      const servers = await api("/api/v1/servers"); if (!Array.isArray(servers)) throw new Error("服务器列表格式不完整。");
      state.servers = servers;
      try {
        state.linkTarget = linkTarget(location.search);
        select("maintenance-server", servers.map(server => [server.id, server.name]), state.linkTarget.serverId);
        if (state.linkTarget.serverId && !servers.some(server => server.id === state.linkTarget.serverId)) {
          byId("maintenance-server").value = ""; throw new Error("链接指定的服务器不可用，请重新选择；未自动改选其它服务器。");
        }
      } catch (error) { state.targetError = true; byId("maintenance-server").value = ""; notice(error.message); }
      try { const pending = JSON.parse(sessionStorage.getItem(indexKey) || "null"); if (validIndex(pending)) { state.pending = pending; showPending(pending, "已恢复上次操作编号，请先只读核对结果。"); } } catch { notice("浏览器操作索引无法读取，请在操作记录核对历史任务后再维护。"); state.pending = { serverId: "", operationId: "" }; }
    } catch (error) { notice(error.message); }
    finally { state.loading = false; updateControls(); }
    if (serverId()) await loadResources();
  })();
})();
