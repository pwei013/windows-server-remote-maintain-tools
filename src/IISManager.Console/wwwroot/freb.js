"use strict";
(() => {
  const states = new Set(["available", "partial", "unavailable", "disabled", "simulation"]);
  const modules = new Set(["OTHER", "UNKNOWN", "IIS Web Core", "AspNetCoreModule", "AspNetCoreModuleV2", "ManagedPipelineHandler", "ManagedEngine", "StaticFileModule", "DefaultDocumentModule", "DirectoryListingModule", "RequestFilteringModule", "RewriteModule", "AnonymousAuthenticationModule", "WindowsAuthenticationModule", "BasicAuthenticationModule", "UrlAuthorizationModule", "HttpCacheModule", "ProtocolSupportModule", "CustomErrorModule", "IsapiModule", "IsapiFilterModule", "WebSocketModule", "ConfigurationValidationModule", "FormsAuthentication", "UrlAuthorization", "Session", "OutputCache"]);
  const notifications = new Set(["OTHER", "UNKNOWN", "BEGIN_REQUEST", "AUTHENTICATE_REQUEST", "AUTHORIZE_REQUEST", "RESOLVE_REQUEST_CACHE", "MAP_REQUEST_HANDLER", "ACQUIRE_REQUEST_STATE", "PRE_EXECUTE_REQUEST_HANDLER", "EXECUTE_REQUEST_HANDLER", "RELEASE_REQUEST_STATE", "UPDATE_REQUEST_CACHE", "LOG_REQUEST", "END_REQUEST", "SEND_RESPONSE"]);
  const events = new Set(["OTHER", "GENERAL_REQUEST_START", "GENERAL_REQUEST_END", "GENERAL_REQUEST_ENTITY", "MODULE_SET_RESPONSE_ERROR_STATUS", "NOTIFY_MODULE_START", "NOTIFY_MODULE_END", "HANDLER_CHANGED", "GENERAL_RESPONSE_HEADERS", "GENERAL_FLUSH_RESPONSE_END", "GENERAL_SEND_RESPONSE"]);
  const methods = new Set(["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE", "CONNECT", "OTHER"]);
  ["NONE", "DigestAuthenticationModule", "FileAuthorizationModule", "HttpRedirectionModule", "ApplicationRequestRouting", "ApplicationRequestRoutingModule", "FastCgiModule", "CgiModule", "FailedRequestsTracingModule", "RequestMonitorModule", "HttpLoggingModule", "CustomLoggingModule", "FileAuthorization", "SessionStateModule"].forEach(value => modules.add(value));
  notifications.add("NONE");
  ["PRE_BEGIN_REQUEST_START", "PRE_BEGIN_REQUEST_END", "GENERAL_SET_RESPONSE_HEADER", "GENERAL_FLUSH_RESPONSE_START", "GENERAL_SEND_CUSTOM_ERROR", "GENERAL_REQUEST_DISCONNECT", "URL_REWRITE_START", "URL_REWRITE_END", "RULE_EVALUATION_START", "RULE_EVALUATION_END", "PATTERN_MATCH", "REWRITE_ACTION", "CONDITIONS_EVALUATION_START", "CONDITIONS_EVALUATION_END", "CONDITION_EVALUATION", "LOG_FILE_MAX_SIZE_TRUNCATE"].forEach(value => events.add(value));
  const errors = {
    FREB_DISABLED: "此站点尚未开启失败请求跟踪。", FREB_NOT_ENABLED: "此站点尚未开启失败请求跟踪。",
    FREB_WINDOWS_REQUIRED: "需要 Windows IIS 环境才能读取跟踪。", FREB_SIMULATION: "模拟环境不读取真实跟踪。",
    FREB_ACCESS_DENIED: "当前 Agent 身份无法读取跟踪，请由操作员核对权限。", FREB_TIMEOUT: "跟踪采集等待超时，请稍后重试。",
    FREB_PATH_REJECTED: "跟踪目录未通过本机路径安全检查。", FREB_READ_UNAVAILABLE: "无法可靠读取跟踪，请在服务器核对配置。",
    FREB_LIMIT_REACHED: "跟踪样本达到上限，仅展示部分记录。", FREB_XML_INVALID: "部分跟踪文件格式不受支持或不完整。",
    FREB_CONFIGURATION_UNAVAILABLE: "无法读取 IIS 失败请求跟踪配置。", FREB_UNSUPPORTED: "当前 Agent 不支持请求级跟踪，请更新 Agent。"
  };
  Object.assign(errors, {
    FREB_ROLE_MISSING: "服务器尚未安装 IIS 跟踪角色服务，请由操作员核对。", FREB_SITE_LIMIT: "站点数量达到采样上限。",
    FREB_LOG_DIRECTORY_NOT_FOUND: "尚未找到本站点的 FREB 跟踪目录。", FREB_FILE_REJECTED: "跟踪文件未通过安全检查。",
    FREB_FILE_CHANGED: "跟踪文件在读取期间发生变化，请稍后刷新。", FREB_FILE_TOO_LARGE: "跟踪文件超过读取上限。",
    FREB_SCAN_LIMIT: "目录枚举达到采样上限。", FREB_TRACE_LIMIT: "请求跟踪条数达到采样上限。", FREB_EVENT_LIMIT: "管线事件达到展示上限。",
    FREB_BYTE_BUDGET_REACHED: "跟踪读取达到总字节预算。", FREB_NO_RECENT_TRACES: "采样范围内没有近期跟踪记录。",
    FREB_XML_LIMIT_REACHED: "XML 跟踪内容超过解析上限。", FREB_SCOPE_MISMATCH: "跟踪记录与当前站点归属不一致，已拒绝显示。",
    FREB_LOG_UNAVAILABLE: "无法可靠读取跟踪日志。", FREB_TRACE_TRUNCATED: "跟踪内容不完整，仅展示可确认的事件。"
  });
  const text = (value, limit = 256) => typeof value === "string" && value.length <= limit && !/[\u0000-\u001f\u007f]/.test(value) ? value : "";
  const numeric = value => typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : null;
  const integer = value => Number.isSafeInteger(value) && value >= 0 ? value : null;
  const date = value => typeof value === "string" && Number.isFinite(Date.parse(value)) ? new Date(value).toISOString() : null;
  const token = (value, allowed) => allowed.has(value) ? value : "OTHER";
  const code = value => Object.hasOwn(errors, value) ? value : value ? "FREB_READ_UNAVAILABLE" : null;
  function sanitizedSnapshot(value) {
    if (!value || !date(value.collectedAt) || !["Windows", "Simulation"].includes(value.mode) || !states.has(value.state) || !Array.isArray(value.sites)) throw new Error("请求跟踪响应不完整，请刷新核对。");
    let remaining = 64;
    return { collectedAt: date(value.collectedAt), mode: value.mode, cacheSeconds: integer(value.cacheSeconds), state: value.state, errorCode: code(value.errorCode),
      sites: value.sites.slice(0, 32).map(site => {
        const source = Array.isArray(site?.traces) ? site.traces : [], count = Math.min(source.length, remaining, 20); remaining -= count;
        return { siteId: integer(site?.siteId), siteName: text(site?.siteName), state: states.has(site?.state) ? site.state : "unavailable", errorCode: code(site?.errorCode),
          truncated: site?.truncated === true || source.length > count, traces: source.slice(0, count).map(trace => ({
            traceId: typeof trace?.traceId === "string" && /^[a-f0-9]{24,64}$/i.test(trace.traceId) ? trace.traceId : "",
            startedAt: date(trace?.startedAt), status: integer(trace?.status), subStatus: integer(trace?.subStatus), win32Status: integer(trace?.win32Status),
            timeTakenMilliseconds: numeric(trace?.timeTakenMilliseconds), method: token(trace?.method, methods),
            failureModule: token(trace?.failureModule, modules), failureNotification: token(trace?.failureNotification, notifications),
            truncated: trace?.truncated === true || Array.isArray(trace?.events) && trace.events.length > 96,
            events: (Array.isArray(trace?.events) ? trace.events : []).slice(0, 96).map(event => ({
              sequence: integer(event?.sequence), timeCreated: date(event?.timeCreated), offsetMilliseconds: numeric(event?.offsetMilliseconds),
              eventName: token(event?.eventName, events), module: token(event?.module, modules), notification: token(event?.notification, notifications),
              status: integer(event?.status), subStatus: integer(event?.subStatus), win32Status: integer(event?.win32Status)
            }))
          })).filter(trace => trace.traceId) };
      }) };
  }
  const pure = Object.freeze({ sanitizedSnapshot });
  if (typeof module !== "undefined" && module.exports) module.exports = pure;
  if (typeof document === "undefined") return;
  const byId = id => document.getElementById(id);
  const model = { serverId: "", snapshot: null, stale: false, busy: false, revision: 0, controller: null, error: "" };
  const stateNames = { available: "已采集", partial: "部分可用", unavailable: "不可用", disabled: "未开启", simulation: "模拟环境" };
  const node = (tag, value, className) => { const item = document.createElement(tag); if (value !== undefined) item.textContent = value; if (className) item.className = className; return item; };
  const time = value => value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "未知时间";
  const number = value => value === null ? "—" : String(value);
  const status = value => value.status === null ? "未知状态" : `HTTP ${value.status}${value.subStatus === null ? "" : "." + value.subStatus}`;
  function renderTrace(trace) {
    const detail = node("details", undefined, "freb-trace"), summary = node("summary");
    summary.append(node("strong", `${time(trace.startedAt)} · ${trace.method} · ${status(trace)}`), node("span", `${number(trace.timeTakenMilliseconds)} ms`, "muted")); detail.append(summary);
    detail.append(node("p", `跟踪编号 ${trace.traceId}`, "freb-id small"));
    detail.append(node("p", `失败模块 ${trace.failureModule === "NONE" ? "未记录" : trace.failureModule} · 通知 ${trace.failureNotification === "NONE" ? "未记录" : trace.failureNotification} · Win32 ${number(trace.win32Status)}`, "small"));
    if (trace.truncated) detail.append(node("p", "本条事件已截取，完整记录请在服务器核对。", "notice"));
    if (!trace.events.length) { detail.append(node("p", "该样本没有可展示的管线事件。", "muted small")); return detail; }
    const wrapper = node("div", undefined, "freb-table-wrap"), table = node("table"), head = node("thead"), titles = node("tr"), body = node("tbody");
    ["序号", "相对时间 ms", "事件", "模块", "通知", "状态", "Win32"].forEach(label => titles.append(node("th", label)));
    head.append(titles); table.append(head);
    trace.events.forEach(event => { const row = node("tr"); [number(event.sequence), number(event.offsetMilliseconds), event.eventName, event.module, event.notification, status(event), number(event.win32Status)].forEach(value => row.append(node("td", value))); body.append(row); });
    table.append(body); wrapper.append(table); detail.append(wrapper); return detail;
  }
  function render() {
    const value = model.snapshot, selected = byId("freb-site").value;
    byId("freb-refresh").disabled = model.busy || !model.serverId;
    byId("freb-export").disabled = model.busy || model.stale || !value;
    byId("freb-updated").textContent = model.busy ? "正在读取请求级跟踪…" : value ? `${model.stale ? "上次数据 · " : ""}采集于 ${time(value.collectedAt)} · ${stateNames[value.state]}` : "尚未取得请求级跟踪。";
    byId("freb-error").textContent = model.error; byId("freb-error").hidden = !model.error;
    byId("freb-site").replaceChildren(node("option", "全部站点")); byId("freb-site").children[0].value = "";
    (value?.sites || []).forEach(site => { const option = node("option", site.siteName || `站点 ${site.siteId}`); option.value = String(site.siteId); byId("freb-site").append(option); });
    byId("freb-site").value = value?.sites.some(site => String(site.siteId) === selected) ? selected : "";
    const range = byId("freb-state-filter").value, shownSites = (value?.sites || []).filter(site => !byId("freb-site").value || String(site.siteId) === byId("freb-site").value);
    let count = 0;
    byId("freb-results").replaceChildren(...shownSites.map(site => {
      const group = node("section", undefined, "freb-site"); group.append(node("h3", `${site.siteName || "未命名站点"} · ${stateNames[site.state]}`));
      if (site.errorCode) group.append(node("p", errors[site.errorCode] || errors.FREB_READ_UNAVAILABLE, "notice"));
      if (site.state === "disabled") group.append(node("p", "由操作员在 IIS 管理器为本站点开启失败请求跟踪并添加规则，再刷新此页。", "muted small"));
      if (site.truncated) group.append(node("p", "仅展示有界采样中的最近跟踪，未覆盖全部文件。", "muted small"));
      const traces = site.traces.filter(trace => !range || range === "5xx" && trace.status >= 500 && trace.status < 600 || range === "4xx" && trace.status >= 400 && trace.status < 500);
      count += traces.length; traces.forEach(trace => group.append(renderTrace(trace)));
      if (!traces.length && ["available", "partial"].includes(site.state)) group.append(node("p", "采样范围内没有匹配的跟踪记录，不代表没有故障。", "muted"));
      return group;
    }));
    byId("freb-count").textContent = `显示 ${count} 条请求跟踪`;
    byId("freb-empty").hidden = !!shownSites.length;
    byId("freb-empty").textContent = value?.mode === "Simulation" ? "模拟环境不读取真实 FREB 文件。" : value ? "本次没有可展示的站点跟踪配置。" : "请选择服务器并读取诊断。";
  }
  async function load(serverId) {
    if (typeof serverId !== "string" || !/^[A-Za-z0-9_-]{1,64}$/.test(serverId)) return;
    model.controller?.abort(); const revision = ++model.revision; model.controller = new AbortController();
    if (model.serverId !== serverId) { model.snapshot = null; model.stale = false; }
    model.serverId = serverId; model.busy = true; model.error = ""; render();
    try {
      const response = await fetch(`/api/v1/servers/${encodeURIComponent(serverId)}/diagnostics/freb`, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" }, signal: model.controller.signal });
      let data; try { data = await response.json(); } catch { throw new Error(errors.FREB_READ_UNAVAILABLE); }
      globalThis.IISManagerAuth?.unauthorized(response, data);
      if (!response.ok) throw new Error(response.status === 501 ? errors.FREB_UNSUPPORTED : response.status === 403 ? "当前账号没有该服务器的查看权限。" : errors.FREB_READ_UNAVAILABLE);
      const result = sanitizedSnapshot(data); if (revision !== model.revision) return;
      model.snapshot = result; model.stale = false;
      if (result.errorCode) model.error = errors[result.errorCode] || errors.FREB_READ_UNAVAILABLE;
    } catch (error) { if (revision !== model.revision || error.name === "AbortError") return; model.stale = !!model.snapshot; model.error = error.message === errors.FREB_UNSUPPORTED || error.message === "当前账号没有该服务器的查看权限。" ? error.message : errors.FREB_READ_UNAVAILABLE; }
    finally { if (revision === model.revision) { model.busy = false; render(); } }
  }
  byId("freb-refresh").addEventListener("click", () => load(model.serverId));
  ["freb-site", "freb-state-filter"].forEach(id => byId(id).addEventListener("change", render));
  byId("freb-export").addEventListener("click", () => {
    if (!model.snapshot || model.stale || model.busy) return;
    const output = { serverId: model.serverId, freb: sanitizedSnapshot(model.snapshot) }, url = URL.createObjectURL(new Blob([JSON.stringify(output, null, 2)], { type: "application/json;charset=utf-8" }));
    const link = node("a"); link.href = url; link.download = `freb-${model.serverId}.json`; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  globalThis.addEventListener?.("pagehide", () => { model.revision++; model.controller?.abort(); });
  globalThis.IISManagerFreb = Object.freeze({ load }); render();
})();
