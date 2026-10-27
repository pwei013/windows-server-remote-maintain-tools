"use strict";

(() => {
  const normalize = value => String(value ?? "").trim().toLocaleLowerCase("zh-CN");
  const date = value => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—";
  const connectionLabels = { online: "连接正常", partial: "部分读取失败", offline: "无法连接", pending: "等待采集" };
  const connectionErrors = {
    AGENT_TIMEOUT: ["读取超时", "核对 Agent 服务、网络延迟和服务器负载，再重试本台。"],
    AGENT_UNREACHABLE: ["无法建立连接", "核对 Agent 实际地址、端口、服务监听和网络规则。"],
    AGENT_DNS_FAILED: ["域名解析失败", "核对 Console 所在服务器的 DNS 和配置的 Agent 主机名。"],
    AGENT_TLS_FAILED: ["TLS 握手或证书验证失败", "核对服务端与客户端证书、主机名、信任链和有效期。"],
    AGENT_ACCESS_DENIED: ["认证或来源限制拒绝", "核对 Agent 的令牌、客户端证书和实际连接来源授权。"],
    AGENT_AUTHENTICATION_REJECTED: ["通信认证未通过", "核对两端配置的通信身份，勿在诊断中粘贴令牌。"],
    AGENT_NOT_FOUND: ["读取接口不存在", "核对 Agent 入口与版本，旧 Agent 可能缺少该模块。"],
    AGENT_RESPONSE_INVALID: ["返回数据无法识别", "核对两端版本与代理响应，保留原有管理记录。"],
    AGENT_PROTOCOL_UNSUPPORTED: ["协议版本不兼容", "确认所用 Agent 与 Console 配套后再管理。"],
    OVERVIEW_READ_FAILED: ["采集未能完成", "核对服务状态和脱敏错误日志，再重试本台。"]
  };
  const healthLabels = { healthy: "检查正常", unhealthy: "检查异常", unavailable: "无法检查", unsupported: "需升级 Agent", simulation: "模拟检查" };
  const certificateLabels = { expired: "已过期", critical: "7 天内到期", warning: "15 天内到期", notice: "30 天内到期", normal: "有效", notYetValid: "尚未生效", missing: "绑定证书缺失" };
  const severityOrder = { missing: 0, expired: 1, critical: 2, notYetValid: 3, warning: 4, notice: 5, normal: 6 };
  const moduleLabels = { info: "服务器信息", snapshot: "IIS 资源", topology: "应用详情", "site-topology": "应用详情", services: "Windows 服务", windowsServices: "Windows 服务", "windows-services": "Windows 服务", certificates: "证书" };
  const probeErrors = { HEALTH_TIMEOUT: "请求超时", HEALTH_REQUEST_FAILED: "连接失败或证书验证未通过", HEALTH_CONNECTION_FAILED: "检查地址暂时无法连接", HEALTH_TLS_ERROR: "HTTPS 证书验证未通过", HEALTH_STATUS_MISMATCH: "HTTP 状态与预期不符", HEALTH_TARGET_INVALID: "检查地址与当前站点配置不匹配", HEALTH_BINDING_MISMATCH: "检查地址与站点绑定不匹配", HEALTH_APPLICATION_PATH_MISMATCH: "检查路径不在所选应用内", HEALTH_SITE_NOT_FOUND: "站点已不存在", HEALTH_APPLICATION_NOT_FOUND: "应用已不存在", HEALTH_SITE_STOPPED: "站点已停止", HEALTH_AGENT_UPGRADE_REQUIRED: "请更新 Agent 后检查", HEALTH_CHECK_UNSUPPORTED: "请更新 Agent 后检查", AGENT_NOT_FOUND: "请更新 Agent 后检查", AGENT_TIMEOUT: "Agent 请求超时", AGENT_UNREACHABLE: "Agent 暂时无法连接" };

  function workbenchUrl(serverId, siteName = "", applicationPath = "/") {
    const query = new URLSearchParams({ server: serverId });
    if (siteName) { query.set("resource", "sites"); query.set("site", siteName); query.set("search", applicationPath === "/" ? siteName : applicationPath); }
    return `/?${query}`;
  }
  function resources(server) {
    const topologySites = server.topology?.sites || [];
    const sites = server.snapshot?.sites || topologySites;
    return sites.flatMap(site => {
      const detailed = topologySites.find(item => item.name === site.name);
      const domains = (site.bindings || detailed?.bindings || []).map(binding => binding.hostName || binding.ipAddress).filter(Boolean);
      const apps = detailed?.applications || [{ path: "/" }];
      return apps.map(app => ({ siteName: site.name, applicationPath: app.path, domains,
        terms: [site.name, app.path, app.physicalPath, ...domains] }));
    });
  }
  function serverMatches(server, search) {
    const query = normalize(search);
    return !query || [server.id, server.name, server.hostName, server.group, server.environment, server.info?.machineName, ...resources(server).flatMap(item => item.terms)]
      .some(value => normalize(value).includes(query));
  }
  function facetOptions(servers, field) {
    const values = [...new Set(servers.map(server => String(server[field] ?? "").trim()))].sort((a, b) => a.localeCompare(b, "zh-CN"));
    return [["", field === "group" ? "全部项目" : "全部环境"], ...values.map(value => [value ? `value:${value}` : "empty", value || (field === "group" ? "未分组" : "未设置")])];
  }
  function serverFilterMatches(server, filters = {}) {
    const matchesFacet = (value, selection) => !selection || (selection === "empty" ? !String(value ?? "").trim() : selection === `value:${String(value ?? "").trim()}`);
    return (!filters.state || server.connectionState === filters.state) && matchesFacet(server.group, filters.group) && matchesFacet(server.environment, filters.environment);
  }
  function metrics(items, runningState) {
    if (!Array.isArray(items)) return null;
    return { total: items.length, running: items.filter(item => item.state === runningState).length,
      stopped: items.filter(item => item.state === "Stopped").length,
      other: items.filter(item => ![runningState, "Stopped"].includes(item.state)).length };
  }
  function certificates(data, search = "", filter = "alerts") {
    const query = normalize(search);
    return data.servers.flatMap(server => (server.certificates || []).map(certificate => ({ ...certificate, serverId: server.id, serverName: server.name, hostName: server.hostName, group: server.group, environment: server.environment })))
      .filter(row => (!filter || (filter === "alerts" ? row.severity !== "normal" : row.severity === filter)) &&
        (!query || [row.serverName, row.serverId, row.hostName, row.group, row.environment, row.subject, row.friendlyName, row.thumbprint, ...(row.bindings || []).flatMap(binding => [binding.siteName, binding.hostName])].some(value => normalize(value).includes(query))))
      .sort((a, b) => (severityOrder[a.severity] ?? 7) - (severityOrder[b.severity] ?? 7) || (a.daysRemaining ?? Infinity) - (b.daysRemaining ?? Infinity) || a.serverName.localeCompare(b.serverName));
  }
  function healthIssue(check) { return check.enabled && !!check.result && ["unhealthy", "unavailable", "unsupported"].includes(check.result.state); }
  function validOverview(data) {
    return !!data && Array.isArray(data.servers) && Array.isArray(data.healthChecks) &&
      data.servers.every(server => !!server && typeof server.id === "string" && typeof server.name === "string" && connectionLabels[server.connectionState] && Array.isArray(server.certificates) && server.certificates.every(certificate => !!certificate && typeof certificate.thumbprint === "string" && Array.isArray(certificate.bindings)) && Array.isArray(server.errors)) &&
      data.healthChecks.every(check => !!check && typeof check.id === "string" && typeof check.serverId === "string" && typeof check.siteName === "string" && typeof check.applicationPath === "string" && typeof check.url === "string");
  }
  function validateForm(value) {
    if (!value.serverId || !value.siteName || !value.applicationPath) return "请选择服务器、站点和应用。";
    let url;
    try { url = new URL(value.url); } catch { return "请填写完整的 HTTP 或 HTTPS 检查地址。"; }
    if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || url.search || url.hash || /[\u0000-\u0020\u007f\\?#]/.test(value.url)) return "检查地址仅支持 HTTP/HTTPS，不能包含账号、查询参数、片段、空白或反斜杠。";
    if (!Number.isInteger(value.expectedStatusCode) || value.expectedStatusCode < 200 || value.expectedStatusCode > 299) return "期望状态码须为 200 至 299。";
    if (!Number.isInteger(value.timeoutSeconds) || value.timeoutSeconds < 1 || value.timeoutSeconds > 15) return "超时须为 1 至 15 秒。";
    if (!Number.isInteger(value.intervalSeconds) || value.intervalSeconds < 30 || value.intervalSeconds > 3600) return "检查间隔须为 30 至 3600 秒。";
    return "";
  }
  const pure = Object.freeze({ workbenchUrl, resources, serverMatches, facetOptions, serverFilterMatches, metrics, certificates, healthIssue, validOverview, validateForm, connectionErrors });
  if (typeof module !== "undefined" && module.exports) module.exports = pure;
  globalThis.IISOverview = pure;
  if (typeof document === "undefined") return;

  const byId = id => document.getElementById(id);
  const state = { data: { servers: [], healthChecks: [] }, csrf: "", loading: null, busy: false, editor: null, deleting: null,
    mutationUnconfirmed: false, timer: null, progressTimer: null, disposed: false, loaded: false,
    favorites: new Set(), reading: new Set(), preferenceKey: "", preferences: null, permissions: null, diagnosticsId: "" };
  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text !== undefined) element.textContent = text;
    if (className) element.className = className;
    return element;
  }
  function message(text = "", error = false) { byId("notice").textContent = text; byId("notice").hidden = !text; byId("notice").className = error ? "notice danger" : "notice"; }
  function badge(text, tone = "neutral") { return node("span", text, `badge ${tone}`); }
  function link(text, href) { const element = node("a", text); element.href = href; return element; }
  function button(text, action, className = "secondary small-button") { const control = node("button", text, className); control.type = "button"; control.addEventListener("click", action); control.dataset.healthAction = "true"; return control; }
  async function api(path, body) {
    const options = { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } };
    if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-IISManager-CSRF"] = state.csrf; options.body = JSON.stringify(body); }
    let response, data;
    try { response = await fetch(path, options); data = await response.json(); }
    catch { const error = new Error(body === undefined ? "总览读取失败，请检查连接后刷新。" : "无法确认本次请求结果，请关闭窗口并刷新总览核对，勿重复提交。"); error.unconfirmed = body !== undefined; throw error; }
    globalThis.IISManagerAuth?.unauthorized(response, data);
    if (!response.ok) { const error = new Error(data.message || "请求未能完成，请刷新后核对。"); error.code = data.code; error.status = response.status; throw error; }
    return data;
  }
  function permitted(id, kind) { return state.permissions?.servers?.some(row => row.serverId === id && row[kind] === true) === true; }
  function updateButtons() {
    const locked = state.busy || !!state.loading || !state.loaded || state.mutationUnconfirmed || !!state.data.healthError;
    byId("overview-refresh").disabled = state.busy || !!state.loading;
    byId("health-add").disabled = locked || !state.data.servers.some(server => permitted(server.id, "configure") && (server.snapshot?.sites?.length || server.topology?.sites?.length));
    byId("health-save").disabled = state.busy || state.mutationUnconfirmed || !permitted(byId("health-server").value, "configure");
    byId("health-delete-confirm").disabled = state.busy || state.mutationUnconfirmed;
    ["health-close", "health-cancel", "health-delete-cancel"].forEach(id => byId(id).disabled = state.busy);
    document.querySelectorAll("[data-health-action]").forEach(control => { control.disabled = locked || control.dataset.unavailable === "true" || (control.dataset.permissionServer && !permitted(control.dataset.permissionServer, control.dataset.permissionKind)); });
    byId("health-form").querySelectorAll("input, select").forEach(control => { control.disabled = state.busy || (control.id === "health-server" && !!state.editor); });
    document.querySelectorAll("[data-server-read]").forEach(control => {
      const item = state.data.servers.find(server => server.id === control.dataset.serverRead);
      control.disabled = state.reading.has(control.dataset.serverRead) || !!item?.refreshing;
    });
  }
  function serverName(id) { return state.data.servers.find(server => server.id === id)?.name || `${id}（已移除）`; }
  function renderSummary() {
    const data = state.data, servers = data.servers;
    const rows = [["管理服务器", servers.length, "已配置的 Agent"], ["连接正常", servers.filter(server => server.connectionState === "online").length, "全部模块读取成功"],
      ["需要关注的服务器", servers.filter(server => ["partial", "offline"].includes(server.connectionState)).length,
        servers.some(server => server.refreshing || server.connectionState === "pending") ? "正在采集，其余节点分批显示" : "离线或部分读取失败"],
      ["业务检查异常", data.healthError ? "—" : data.healthChecks.filter(healthIssue).length, data.healthError ? "检查配置暂时无法读取" : `${data.healthChecks.filter(check => check.enabled).length} 项已启用`],
      ["证书预警", certificates(data).length, "含到期、未生效及缺失证书"]];
    byId("overview-summary").replaceChildren(...rows.map(([title, value, hint], index) => { const card = node("div", undefined, `summary-card${index >= 2 && value !== 0 ? " attention" : ""}`); card.append(node("p", title), node("strong", String(value)), node("small", hint)); return card; }));
  }
  function renderServers() {
    selectOptions("server-group", facetOptions(state.data.servers, "group"), state.preferences?.group ?? byId("server-group").value);
    selectOptions("server-environment", facetOptions(state.data.servers, "environment"), state.preferences?.environment ?? byId("server-environment").value);
    const search = byId("overview-search").value, filters = { state: byId("server-state").value, group: byId("server-group").value, environment: byId("server-environment").value };
    const servers = state.data.servers.filter(server => serverFilterMatches(server, filters) && serverMatches(server, search)
      && (!byId("server-favorites-only").checked || state.favorites.has(server.id)))
      .sort((a, b) => Number(state.favorites.has(b.id)) - Number(state.favorites.has(a.id)));
    byId("servers-count").textContent = `${servers.length} / ${state.data.servers.length} 台服务器`;
    byId("servers-empty").textContent = state.data.servers.length ? "没有匹配的服务器，请调整筛选条件。" : "尚未配置 Agent 服务器，请先由运维人员完成服务器接入。";
    byId("servers-empty").hidden = !!servers.length;
    byId("overview-servers").replaceChildren(...servers.map(server => {
      const card = node("article", undefined, `server-card ${server.connectionState}`), top = node("div", undefined, "server-card-top"), identity = node("div");
      identity.append(link(server.name, workbenchUrl(server.id)), node("span", `${server.info?.machineName || server.hostName || server.id}${server.mode === "Simulation" ? " · 模拟服务器" : ""}`, "server-card-host"));
      top.append(identity, badge(server.refreshing && server.connectionState !== "pending" ? "正在刷新" : connectionLabels[server.connectionState],
        server.refreshing || server.connectionState === "pending" ? "neutral" : server.connectionState === "online" ? "good" : server.connectionState === "partial" ? "warn" : "bad")); card.append(top);
      const tags = node("div", undefined, "server-classifications"); tags.append(node("span", server.group || "未分组"), node("span", server.environment || "未设置")); card.append(tags);
      const grid = node("div", undefined, "server-metrics");
      [["IIS 站点", metrics(server.snapshot?.sites, "Started")], ["应用程序池", metrics(server.snapshot?.applicationPools, "Started")], ["Windows 服务", metrics(server.services?.services?.filter(service => service.managed), "Running")]].forEach(([title, values]) => {
        const metric = node("div", undefined, "server-metric"); metric.append(node("span", title), node("strong", values ? `${values.running} / ${values.total}` : "—"), node("small", values ? `运行 / 总数 · 停止 ${values.stopped}${values.other ? ` · 其他 ${values.other}` : ""}` : "暂未取得数据")); grid.append(metric);
      }); card.append(grid);
      if (server.errors.length) card.append(node("p", server.errors.map(error =>
        `${moduleLabels[error.module] || "数据采集"}：${connectionErrors[error.code]?.[0] || "读取失败"}`).join("；"), "server-issue"));
      if (server.isStale) card.append(node("p", "当前显示上次结果，正在重新核对。", "server-issue"));
      if (normalize(search)) {
        const matches = resources(server).filter(item => item.terms.some(term => normalize(term).includes(normalize(search))));
        if (matches.length) { const list = node("div", undefined, "resource-matches"); matches.slice(0, 5).forEach(item => list.append(link(`${item.siteName}${item.applicationPath === "/" ? " · 根应用" : ` · ${item.applicationPath}`}`, workbenchUrl(server.id, item.siteName, item.applicationPath)))); if (matches.length > 5) list.append(node("span", `另有 ${matches.length - 5} 项匹配，可进入工作台查看`, "muted")); card.append(list); }
      }
      const actions = node("div", undefined, "server-card-actions");
      const favorite = node("button", state.favorites.has(server.id) ? "已收藏" : "收藏", "text-button"); favorite.type = "button";
      favorite.setAttribute("aria-pressed", String(state.favorites.has(server.id)));
      favorite.addEventListener("click", () => { if (state.favorites.has(server.id)) state.favorites.delete(server.id); else state.favorites.add(server.id); savePreferences(); renderServers(); updateButtons(); });
      const diagnostics = node("button", "连接诊断", "secondary small-button"); diagnostics.type = "button"; diagnostics.addEventListener("click", () => openDiagnostics(server.id));
      const retry = node("button", "重试本台", "secondary small-button"); retry.type = "button"; retry.dataset.serverRead = server.id; retry.addEventListener("click", () => retryServer(server.id));
      actions.append(favorite, diagnostics, retry); card.append(actions);
      card.append(node("div", `${server.collectedAt ? `采集于 ${date(server.collectedAt)}` : "尚未取得采集结果"}${server.info?.agentVersion ? ` · Agent ${server.info.agentVersion}` : ""}`, "server-card-bottom")); return card;
    }));
  }
  function renderHealth() {
    const filter = byId("health-filter").value, query = normalize(byId("overview-search").value), unavailable = state.data.healthError;
    byId("health-unavailable").hidden = !unavailable;
    byId("health-unavailable").textContent = unavailable ? "健康检查配置暂时无法读取，已暂停相关操作。请核对控制台存储状态后刷新。" : "";
    const checks = state.data.healthChecks.filter(check => (!filter || filter === "issues" && healthIssue(check) || filter === "enabled" && check.enabled || filter === "disabled" && !check.enabled) &&
      (!query || [serverName(check.serverId), state.data.servers.find(server => server.id === check.serverId)?.group, state.data.servers.find(server => server.id === check.serverId)?.environment, check.serverId, check.siteName, check.applicationPath, check.url].some(value => normalize(value).includes(query))));
    byId("health-count").textContent = unavailable ? "检查配置不可用" : `显示 ${checks.length} / ${state.data.healthChecks.length} 项检查`;
    byId("health-empty").hidden = !!checks.length || !!unavailable;
    byId("health-empty").textContent = state.data.healthChecks.length ? "没有匹配的健康检查，请调整筛选条件。" : "尚未配置健康检查，可为站点或子应用添加检查地址。";
    byId("health-body").replaceChildren(...checks.map(check => {
      const tr = node("tr"), target = node("td"), address = node("td"), result = node("td"), time = node("td", undefined, "health-time"), actions = node("td", undefined, "health-actions");
      target.append(link(check.siteName, workbenchUrl(check.serverId, check.siteName, check.applicationPath)), node("div", `${serverName(check.serverId)} · ${check.applicationPath === "/" ? "根应用" : check.applicationPath}`, "muted"));
      address.append(node("div", check.url, "health-url"), node("div", `期望 ${check.expectedStatusCode} · 每 ${check.intervalSeconds} 秒 · 超时 ${check.timeoutSeconds} 秒`, "muted"));
      const value = check.result, label = !check.enabled ? "已停用" : value ? healthLabels[value.state] || "结果未知" : "等待首次检查";
      result.append(badge(label, !check.enabled || !value || value.state === "simulation" ? "neutral" : value.state === "healthy" ? "good" : "bad"));
      if (value) { const details = [value.statusCode != null ? `HTTP ${value.statusCode}` : null, value.elapsedMilliseconds != null ? `${value.elapsedMilliseconds} ms` : null, value.errorCode ? probeErrors[value.errorCode] || "检查未能确认，请核对检查地址或 Agent 状态" : null].filter(Boolean).join(" · "); if (details) result.append(node("span", details, "health-result-detail")); }
      time.append(node("div", `检查 ${date(value?.checkedAt)}`), node("div", `成功 ${date(check.lastSuccessAt)}`));
      if (check.enabled) time.append(node("div", `下次 ${date(check.nextCheckAt)}`, "muted"));
      const run = button("立即检查", () => runCheck(check)); run.dataset.permissionServer = check.serverId; run.dataset.permissionKind = "operate"; run.dataset.unavailable = String(!check.enabled); run.title = check.enabled ? "执行一次只读业务检查" : "请先启用这项检查";
      const edits = [button("编辑", () => openEditor(check)), button(check.enabled ? "停用" : "启用", () => toggleCheck(check)), button("删除", () => openDelete(check), "text-button")];
      edits.forEach(control => { control.dataset.permissionServer = check.serverId; control.dataset.permissionKind = "configure"; });
      actions.append(run, ...edits);
      tr.append(target, address, result, time, actions); return tr;
    }));
  }
  function renderCertificates() {
    const rows = certificates(state.data, byId("overview-search").value, byId("certificate-filter").value);
    byId("certificates-count").textContent = `显示 ${rows.length} 项 · ${state.data.servers.filter(server => server.errors.some(error => error.module === "certificates")).length} 台服务器未能读取证书`;
    byId("certificates-empty").hidden = !!rows.length;
    byId("certificates-body").replaceChildren(...rows.map(certificate => {
      const tr = node("tr"), identity = node("td"), status = node("td"), expiry = node("td"), targets = node("td"), bindings = node("div", undefined, "certificate-targets");
      identity.append(node("strong", certificate.friendlyName || certificate.subject || "证书信息缺失"), node("div", certificate.serverName, "muted"), node("span", certificate.thumbprint, "certificate-fingerprint"));
      status.append(badge(certificateLabels[certificate.severity] || "待核实", ["expired", "critical", "missing"].includes(certificate.severity) ? "bad" : certificate.severity === "normal" ? "good" : "warn"));
      if (!certificate.hasPrivateKey && certificate.severity !== "missing") status.append(node("div", "未发现私钥", "health-result-detail"));
      expiry.append(node("div", date(certificate.notAfter)), node("div", certificate.daysRemaining == null ? "—" : certificate.severity === "expired" && certificate.daysRemaining === 0 ? "已过期不足 1 天" : certificate.daysRemaining < 0 ? `已过期 ${Math.abs(certificate.daysRemaining)} 天` : `剩余 ${certificate.daysRemaining} 天`, "certificate-days"));
      (certificate.bindings || []).forEach(binding => bindings.append(link(`${binding.siteName} · ${binding.hostName || "未指定域名"}:${binding.port}`, workbenchUrl(certificate.serverId, binding.siteName))));
      if (!bindings.children.length) bindings.append(node("span", "当前未绑定 IIS 站点", "muted")); targets.append(bindings); tr.append(identity, status, expiry, targets); return tr;
    }));
  }
  function render() { renderSummary(); renderServers(); renderHealth(); renderCertificates(); updateButtons(); }
  function loadPreferences(session) {
    if (session.authenticationRequired && !session.user) return;
    state.preferenceKey = `iis-manager-overview-view-v1:${session.user?.appId || 0}:${encodeURIComponent(session.user?.userId || "development")}`;
    try {
      const saved = JSON.parse(localStorage.getItem(state.preferenceKey) || "null");
      if (saved?.version !== 1) return;
      state.favorites = new Set((Array.isArray(saved.favorites) ? saved.favorites : []).filter(id => typeof id === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(id)).slice(0, 2000));
      const filters = saved.filters || {};
      state.preferences = { group: typeof filters.group === "string" ? filters.group.slice(0, 128) : "", environment: typeof filters.environment === "string" ? filters.environment.slice(0, 128) : "" };
      for (const [id, key] of [["server-state", "state"], ["health-filter", "health"], ["certificate-filter", "certificate"]]) {
        if (typeof filters[key] === "string" && [...byId(id).options].some(option => option.value === filters[key])) byId(id).value = filters[key];
      }
      byId("server-favorites-only").checked = saved.favoritesOnly === true;
      byId("overview-auto").checked = saved.autoRefresh === true;
    } catch { /* Optional view preferences must never prevent management. */ }
  }
  function savePreferences() {
    state.preferences = null;
    if (!state.preferenceKey) return;
    try {
      localStorage.setItem(state.preferenceKey, JSON.stringify({ version: 1, favorites: [...state.favorites],
        favoritesOnly: byId("server-favorites-only").checked, autoRefresh: byId("overview-auto").checked,
        filters: { group: byId("server-group").value, environment: byId("server-environment").value,
          state: byId("server-state").value, health: byId("health-filter").value, certificate: byId("certificate-filter").value } }));
    } catch { /* No credentials or drafts are stored in view preferences. */ }
  }
  function renderDiagnostics() {
    const server = state.data.servers.find(item => item.id === state.diagnosticsId);
    if (!server) return;
    byId("server-diagnostics-title").textContent = `${server.name} · 连接诊断`;
    const list = byId("server-diagnostics-details"); list.replaceChildren();
    for (const [label, value] of [["主机", server.hostName], ["连接状态", server.refreshing ? "正在采集" : connectionLabels[server.connectionState]],
      ["采集时间", date(server.collectedAt)], ["耗时", server.elapsedMilliseconds == null ? "—" : `${server.elapsedMilliseconds} ms`], ["Agent 版本", server.info?.agentVersion || "尚未取得"]]) {
      list.append(node("dt", label), node("dd", value || "—"));
    }
    byId("server-diagnostics-errors").replaceChildren(...server.errors.map(error => {
      const item = node("div", undefined, "diagnostic-error");
      const description = connectionErrors[error.code] || ["模块读取失败", "先核对服务连接和版本，再重试本台。"];
      item.append(node("strong", `${moduleLabels[error.module] || "数据采集"} · ${description[0]}`), node("code", error.code || "UNKNOWN"), node("p", description[1])); return item;
    }));
    byId("server-diagnostics-empty").hidden = !!server.errors.length;
    byId("server-diagnostics-empty").textContent = server.connectionState === "pending" ? "正在等待采集，本台结果完成后再核对。" : "本次未记录读取错误；连接正常不等于业务健康。";
    byId("server-diagnostics-retry").disabled = state.reading.has(server.id) || !!server.refreshing;
    byId("server-diagnostics-workbench").href = workbenchUrl(server.id);
    byId("server-diagnostics-resources").href = `/diagnostics.html?server=${encodeURIComponent(server.id)}`;
  }
  function openDiagnostics(id) { state.diagnosticsId = id; renderDiagnostics(); byId("server-diagnostics-dialog").showModal(); }
  async function retryServer(id) {
    if (state.reading.has(id) || state.data.servers.find(server => server.id === id)?.refreshing) return;
    state.reading.add(id); updateButtons(); renderDiagnostics();
    try {
      const result = await api(`/api/v1/servers/${encodeURIComponent(id)}/overview/refresh`);
      if (!validOverview({ servers: [result], healthChecks: [] }) || result.id !== id) throw new Error("本台返回数据不完整，请刷新总览核对。");
      const index = state.data.servers.findIndex(server => server.id === id);
      if (index >= 0) state.data.servers[index] = result;
      render();
    } catch (error) { message(error.message, true); }
    finally { state.reading.delete(id); updateButtons(); renderDiagnostics(); }
  }
  function scheduleProgress() {
    clearTimeout(state.progressTimer);
    if (!state.data.isRefreshing || state.disposed || document.visibilityState === "hidden") return;
    state.progressTimer = setTimeout(async () => {
      if (!state.busy && !byId("health-dialog").open && !byId("health-delete-dialog").open) await refresh();
      else scheduleProgress();
    }, 1000);
  }
  function scheduleRefresh() {
    clearTimeout(state.timer);
    if (!byId("overview-auto").checked || state.disposed || document.visibilityState === "hidden") return;
    state.timer = setTimeout(async () => { if (!state.busy && !byId("health-dialog").open && !byId("health-delete-dialog").open) await refresh(); scheduleRefresh(); }, 30000);
  }
  async function refresh({ reconcile = false } = {}) {
    if (state.loading) return state.loading;
    if (state.busy) return;
    const pending = (async () => {
      try {
        const data = await api("/api/v1/overview/progress");
        if (!validOverview(data)) throw new Error("总览数据不完整，请刷新后核对。");
        state.data = data; state.loaded = true;
        if (reconcile && !data.healthError) state.mutationUnconfirmed = false;
        byId("overview-updated").textContent = data.isRefreshing
          ? `正在采集 ${data.completedServers || 0} / ${data.totalServers ?? data.servers.length} 台，已完成的服务器可先查看`
          : `最近刷新 ${date(data.collectedAt)} · 自动刷新仅更新展示，定时检查按各自配置运行`;
        if (!state.mutationUnconfirmed) message(); render(); state.preferences = null; renderDiagnostics();
      } catch (error) {
        byId("overview-updated").textContent = state.loaded ? "刷新失败，当前展示上次成功读取的数据。" : "总览暂时无法读取。";
        message(error.message, true);
      } finally { state.loading = null; updateButtons(); scheduleRefresh(); scheduleProgress(); }
    })();
    state.loading = pending; updateButtons(); return pending;
  }
  function selectOptions(id, values, selected) {
    const control = byId(id); control.replaceChildren(...values.map(([value, title]) => { const option = node("option", title); option.value = value; return option; }));
    control.value = values.some(([value]) => value === selected) ? selected : values[0]?.[0] || "";
  }
  function currentServer() { return state.data.servers.find(server => server.id === byId("health-server").value); }
  function sitesFor(server) { return server?.snapshot?.sites || server?.topology?.sites || []; }
  function applicationsFor(server, name) { return server?.topology?.sites?.find(site => site.name === name)?.applications || [{ path: "/" }]; }
  function loadSites(siteName = "", applicationPath = "/") {
    const sites = sitesFor(currentServer()), choices = sites.map(site => [site.name, site.name]);
    if (siteName && !choices.some(([value]) => value === siteName)) choices.push([siteName, `${siteName}（当前未读取到）`]);
    selectOptions("health-site", choices, siteName); loadApplications(applicationPath);
  }
  function loadApplications(applicationPath = "/") {
    const apps = applicationsFor(currentServer(), byId("health-site").value), choices = apps.map(app => [app.path, app.path === "/" ? "/（根应用）" : app.path]);
    if (applicationPath && !choices.some(([value]) => value === applicationPath)) choices.push([applicationPath, `${applicationPath}（当前未读取到）`]);
    selectOptions("health-application", choices, applicationPath);
  }
  function suggestUrl() {
    const server = currentServer(), site = sitesFor(server).find(item => item.name === byId("health-site").value), binding = site?.bindings?.find(item => ["http", "https"].includes(item.protocol));
    if (!binding) { byId("health-url").value = ""; return; }
    let host = binding.hostName || (binding.ipAddress && !["*", "0.0.0.0", "::"].includes(binding.ipAddress) ? binding.ipAddress : server.hostName);
    if (!host) { byId("health-url").value = ""; return; }
    if (host.includes(":") && !host.startsWith("[")) host = `[${host}]`;
    const path = byId("health-application").value;
    byId("health-url").value = `${binding.protocol}://${host}:${binding.port}${path === "/" ? "" : path}/health`;
  }
  function formMessage(text) { byId("health-form-error").textContent = text; byId("health-form-error").hidden = !text; }
  function openEditor(check = null) {
    if (!check && !state.data.servers.some(server => permitted(server.id, "configure"))) return;
    if (state.busy || state.loading || state.mutationUnconfirmed || state.data.healthError || (check && !permitted(check.serverId, "configure"))) return;
    state.editor = check; formMessage("");
    byId("health-dialog-title").textContent = check ? "编辑健康检查" : "新增健康检查";
    const servers = state.data.servers.filter(server => permitted(server.id, "configure")).map(server => [server.id, server.name]);
    if (check && !servers.some(([value]) => value === check.serverId)) servers.push([check.serverId, `${check.serverId}（已移除）`]);
    const firstAvailable = state.data.servers.find(server => permitted(server.id, "configure") && sitesFor(server).length)?.id;
    selectOptions("health-server", servers, check?.serverId || firstAvailable);
    byId("health-server").disabled = !!check;
    loadSites(check?.siteName || "", check?.applicationPath || "/");
    byId("health-status").value = check?.expectedStatusCode ?? 200;
    byId("health-timeout").value = check?.timeoutSeconds ?? 5;
    byId("health-interval").value = check?.intervalSeconds ?? 60;
    byId("health-enabled").checked = check?.enabled ?? false;
    if (check) byId("health-url").value = check.url; else suggestUrl();
    byId("health-dialog").showModal(); byId("health-server").focus();
  }
  function readForm() {
    return { id: state.editor?.id ?? null, revision: state.editor?.revision ?? null, serverId: byId("health-server").value, siteName: byId("health-site").value,
      applicationPath: byId("health-application").value, url: byId("health-url").value.trim(), expectedStatusCode: Number(byId("health-status").value),
      timeoutSeconds: Number(byId("health-timeout").value), intervalSeconds: Number(byId("health-interval").value), enabled: byId("health-enabled").checked };
  }
  function updateCheck(check) {
    if (!check || typeof check.id !== "string" || typeof check.revision !== "string") { const error = new Error("返回结果不完整，请刷新总览核对本次请求。"); error.unconfirmed = true; throw error; }
    const index = state.data.healthChecks.findIndex(item => item.id === check.id);
    if (index >= 0) state.data.healthChecks[index] = check; else state.data.healthChecks.push(check);
  }
  async function mutate(action, onError) {
    if (state.busy || state.loading || state.mutationUnconfirmed || state.data.healthError) return;
    state.busy = true; message(); updateButtons();
    try { await action(); render(); }
    catch (error) { if (error.unconfirmed || error.status === 409 || ["HEALTH_STORE_UNVERIFIED", "HEALTH_STORE_UNAVAILABLE", "HEALTH_STORE_CHANGED"].includes(error.code)) state.mutationUnconfirmed = true; (onError || (text => message(text, true)))(error.message); }
    finally { state.busy = false; updateButtons(); }
  }
  async function saveCheck(event) {
    event.preventDefault(); const value = readForm(); if (!permitted(value.serverId, "configure")) return; const error = validateForm(value); if (error) { formMessage(error); return; }
    await mutate(async () => { updateCheck(await api("/api/v1/health-checks/save", value)); byId("health-dialog").close(); message("健康检查配置已保存。"); }, formMessage);
  }
  async function runCheck(check) {
    if (!check.enabled || !permitted(check.serverId, "operate")) return;
    await mutate(async () => { updateCheck(await api(`/api/v1/health-checks/${encodeURIComponent(check.id)}/run`, {})); message("已取得本次检查结果。"); });
  }
  async function toggleCheck(check) {
    if (!permitted(check.serverId, "configure")) return;
    const { id, revision, serverId, siteName, applicationPath, url, expectedStatusCode, timeoutSeconds, intervalSeconds } = check;
    await mutate(async () => { updateCheck(await api("/api/v1/health-checks/save", { id, revision, serverId, siteName, applicationPath, url, expectedStatusCode, timeoutSeconds, intervalSeconds, enabled: !check.enabled })); message(check.enabled ? "已停用检查。" : "已启用检查。"); });
  }
  function openDelete(check) {
    if (state.busy || state.loading || state.mutationUnconfirmed || !permitted(check.serverId, "configure")) return;
    state.deleting = check; byId("health-delete-description").textContent = `确认删除 ${serverName(check.serverId)} / ${check.siteName}${check.applicationPath === "/" ? "" : check.applicationPath} 的健康检查？`;
    byId("health-delete-error").hidden = true; byId("health-delete-dialog").showModal();
  }
  async function deleteCheck() {
    const check = state.deleting; if (!check || !permitted(check.serverId, "configure")) return;
    await mutate(async () => {
      const result = await api(`/api/v1/health-checks/${encodeURIComponent(check.id)}/delete`, { revision: check.revision });
      if (result.deleted !== true) { const error = new Error("无法确认删除结果，请关闭窗口并刷新总览核对。"); error.unconfirmed = true; throw error; }
      state.data.healthChecks = state.data.healthChecks.filter(item => item.id !== check.id); byId("health-delete-dialog").close(); state.deleting = null; message("健康检查已删除。");
    }, text => { byId("health-delete-error").textContent = text; byId("health-delete-error").hidden = false; });
  }
  byId("overview-refresh").addEventListener("click", () => refresh({ reconcile: true }));
  byId("overview-auto").addEventListener("change", () => { savePreferences(); scheduleRefresh(); });
  byId("overview-search").addEventListener("input", render);
  ["server-state", "server-group", "server-environment", "server-favorites-only", "health-filter", "certificate-filter"].forEach(id => byId(id).addEventListener("change", () => { savePreferences(); render(); }));
  byId("overview-reset").addEventListener("click", () => { byId("overview-search").value = ""; byId("server-state").value = ""; byId("server-group").value = ""; byId("server-environment").value = ""; byId("health-filter").value = ""; byId("certificate-filter").value = "alerts"; byId("server-favorites-only").checked = false; savePreferences(); render(); });
  byId("server-diagnostics-close").addEventListener("click", () => byId("server-diagnostics-dialog").close());
  byId("server-diagnostics-retry").addEventListener("click", () => retryServer(state.diagnosticsId));
  byId("health-add").addEventListener("click", () => openEditor());
  byId("health-form").addEventListener("submit", saveCheck);
  ["health-close", "health-cancel"].forEach(id => byId(id).addEventListener("click", () => { if (!state.busy) byId("health-dialog").close(); }));
  byId("health-dialog").addEventListener("cancel", event => { if (state.busy) event.preventDefault(); });
  byId("health-server").addEventListener("change", () => { loadSites(); suggestUrl(); });
  byId("health-site").addEventListener("change", () => { loadApplications(); suggestUrl(); });
  byId("health-application").addEventListener("change", suggestUrl);
  byId("health-delete-cancel").addEventListener("click", () => { if (!state.busy) byId("health-delete-dialog").close(); });
  byId("health-delete-confirm").addEventListener("click", deleteCheck);
  byId("health-delete-dialog").addEventListener("cancel", event => { if (state.busy) event.preventDefault(); });
  document.addEventListener("visibilitychange", () => { scheduleRefresh(); scheduleProgress(); });
  globalThis.addEventListener?.("pagehide", () => { state.disposed = true; clearTimeout(state.timer); clearTimeout(state.progressTimer); });
  render();
  (async () => {
    try { const session = await api("/api/v1/session"); state.csrf = session.csrfToken; globalThis.IISManagerAuth?.render(session); loadPreferences(session); state.permissions = await api("/api/v1/permissions"); await refresh(); }
    catch (error) { message(error.message, true); byId("overview-updated").textContent = "无法读取登录状态，请刷新页面重试。"; }
  })();
})();
