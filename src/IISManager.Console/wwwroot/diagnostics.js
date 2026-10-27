"use strict";

(() => {
  const providers = new Set(["Microsoft-Windows-WAS", "Microsoft-Windows-IIS-W3SVC", "Microsoft-Windows-IIS-W3SVC-WP", "Service Control Manager", "Application Error", ".NET Runtime", "Windows Error Reporting"]);
  const states = new Set(["available", "partial", "unavailable", "simulation"]);
  const text = (value, maximum = 256) => typeof value === "string" && value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value) ? value : "";
  const number = value => typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : null;
  const integer = value => Number.isSafeInteger(value) && value >= 0 ? value : null;
  const timestamp = value => typeof value === "string" && Number.isFinite(Date.parse(value)) ? new Date(value).toISOString() : null;
  const code = value => typeof value === "string" && /^[A-Z][A-Z0-9_]{0,80}$/.test(value) ? value : null;
  const percent = value => number(value) !== null && value <= 100 ? value : null;
  function eventSummary(provider) {
    return provider === "Service Control Manager" ? "Windows 服务控制事件，请按时间与编号在服务器核对。" : ["Application Error", ".NET Runtime", "Windows Error Reporting"].includes(provider) ? "应用或运行时关键事件，请在服务器核对。" : "IIS/WAS 关键事件，请在服务器核对。";
  }
  function sanitizedSnapshot(value) {
    if (!value || !timestamp(value.collectedAt) || !["Windows", "Simulation"].includes(value.mode) || !Array.isArray(value.modules)) throw new Error("诊断响应不完整，请刷新后重试。");
    const array = (rows, limit, project) => rows === null ? null : Array.isArray(rows) ? rows.slice(0, limit).map(project).filter(Boolean) : null;
    const resources = value.resources && typeof value.resources === "object" ? {
      cpuPercent: percent(value.resources.cpuPercent), totalMemoryBytes: number(value.resources.totalMemoryBytes), availableMemoryBytes: number(value.resources.availableMemoryBytes), logicalProcessors: integer(value.resources.logicalProcessors),
      disks: array(value.resources.disks, 32, disk => ({ name: text(disk?.name, 64), totalBytes: number(disk?.totalBytes), availableBytes: number(disk?.availableBytes), errorCode: code(disk?.errorCode) })) || []
    } : null;
    return { collectedAt: value.collectedAt, mode: value.mode, cacheSeconds: integer(value.cacheSeconds), resources,
      processes: array(value.processes, 200, row => row && ["applicationPool", "windowsService"].includes(row.kind) ? {
        kind: row.kind, name: text(row.name), state: text(row.state, 32), processId: integer(row.processId), cpuPercent: percent(row.cpuPercent), workingSetBytes: number(row.workingSetBytes), privateMemoryBytes: number(row.privateMemoryBytes), startedAt: timestamp(row.startedAt), uptimeSeconds: number(row.uptimeSeconds), errorCode: code(row.errorCode)
      } : null),
      events: array(value.events, 50, row => row && providers.has(row.provider) && ["System", "Application"].includes(row.logName) && integer(row.eventId) !== null ? {
        timeCreated: timestamp(row.timeCreated), logName: row.logName, provider: row.provider, eventId: row.eventId, level: integer(row.level), recordId: integer(row.recordId), summary: eventSummary(row.provider)
      } : null),
      iisErrors: array(value.iisErrors, 32, row => row && states.has(row.state) ? {
        siteName: text(row.siteName), state: row.state, windowStart: timestamp(row.windowStart), windowEnd: timestamp(row.windowEnd), sampledFiles: integer(row.sampledFiles), sampledLines: integer(row.sampledLines), truncated: row.truncated === true, errorCode: code(row.errorCode),
        errors: array(row.errors, 100, error => error && integer(error.status) >= 400 && integer(error.status) <= 599 ? { status: error.status, subStatus: integer(error.subStatus), win32Status: integer(error.win32Status), count: integer(error.count), averageMilliseconds: number(error.averageMilliseconds), maximumMilliseconds: number(error.maximumMilliseconds) } : null) || []
      } : null),
      modules: value.modules.slice(0, 16).filter(module => module && states.has(module.state)).map(module => ({ module: text(module.module, 64), state: module.state, errorCode: code(module.errorCode), truncated: module.truncated === true }))
    };
  }
  function bytes(value) { if (number(value) === null) return "—"; const units = ["B", "KB", "MB", "GB", "TB"]; let index = 0; while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; } return `${value.toFixed(index ? 1 : 0)} ${units[index]}`; }
  function duration(value) { if (number(value) === null) return "—"; if (value >= 86400) return `${(value / 86400).toFixed(1)} 天`; if (value >= 3600) return `${(value / 3600).toFixed(1)} 小时`; return `${Math.floor(value / 60)} 分钟`; }
  const pure = Object.freeze({ sanitizedSnapshot, bytes, duration, eventSummary });
  if (typeof module !== "undefined" && module.exports) module.exports = pure;
  globalThis.IISDiagnostics = pure;
  if (typeof document === "undefined") return;

  const byId = id => document.getElementById(id);
  const model = { servers: [], serverId: "", snapshot: null, revision: 0, controller: null, busy: false, timer: null, stale: false };
  const labels = { resources: "服务器资源", "resources.cpu": "服务器 CPU", "resources.disks": "部分磁盘", processes: "应用池与服务进程", events: "Windows 关键事件", iisLogs: "IIS 错误摘要", "processes.limit": "进程条数", "processes.items": "部分进程", "events.limit": "事件条数", "iisLogs.limit": "站点条数", "iisLogs.items": "部分 IIS 日志" };
  const date = value => value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—";
  const cpu = value => value === null ? "待二次采样 / 未知" : `${value.toFixed(1)}%`;
  function node(tag, content, className) { const element = document.createElement(tag); if (content !== undefined) element.textContent = content; if (className) element.className = className; return element; }
  function notice(message = "") { byId("notice").textContent = message; byId("notice").hidden = !message; }
  function unavailable(target, message = "该模块暂不可用，请查看采集状态。") { target.replaceChildren(node("p", message, "empty")); }
  function cell(row, value) { row.append(node("td", value)); }
  function table(headers) { const element = node("table"), head = node("thead"), row = node("tr"), body = node("tbody"); headers.forEach(header => { const th = node("th", header); th.scope = "col"; row.append(th); }); head.append(row); element.append(head, body); return { element, body }; }
  function render() {
    const data = model.snapshot;
    byId("diagnostics-export").disabled = !data || model.busy || model.stale;
    byId("diagnostics-refresh").disabled = model.busy || !model.serverId;
    byId("diagnostics-updated").textContent = data ? `${model.stale ? "上次快照，当前读取失败 · " : "采集于 "}${date(data.collectedAt)} · ${data.mode === "Simulation" ? "模拟环境" : "Windows 本机只读采集"} · 服务端缓存 ${data.cacheSeconds ?? 10} 秒` : "选择服务器读取诊断快照。";
    const modules = byId("diagnostics-modules"); modules.replaceChildren();
    (data?.modules || []).forEach(module => { const item = node("li", `${labels[module.module] || module.module}：${({ available: "已采集", partial: "部分可用", unavailable: "不可用", simulation: "模拟，不采集真实数据" })[module.state]}${module.errorCode ? ` · ${module.errorCode}` : ""}${module.truncated ? " · 有采样上限" : ""}`); item.className = module.state === "available" ? "diagnostic-ok" : "diagnostic-warning"; modules.append(item); });
    const resource = byId("diagnostics-resources"); resource.replaceChildren();
    if (!data?.resources) unavailable(resource);
    else {
      const r = data.resources, stats = node("div", undefined, "diagnostic-stats");
      [["服务器 CPU", cpu(r.cpuPercent)], ["内存使用 / 总量", r.totalMemoryBytes === null || r.availableMemoryBytes === null ? "—" : `${bytes(Math.max(0, r.totalMemoryBytes - r.availableMemoryBytes))} / ${bytes(r.totalMemoryBytes)}`], ["逻辑处理器", r.logicalProcessors === null ? "—" : String(r.logicalProcessors)]].forEach(([title, value]) => { const box = node("div"); box.append(node("span", title), node("strong", value)); stats.append(box); });
      resource.append(stats); const disks = table(["本机固定磁盘", "可用 / 总量", "采集状态"]);
      r.disks.forEach(disk => { const row = node("tr"); cell(row, disk.name); cell(row, `${bytes(disk.availableBytes)} / ${bytes(disk.totalBytes)}`); cell(row, disk.errorCode || "已采集"); disks.body.append(row); }); resource.append(disks.element);
    }
    renderProcesses();
    const eventTarget = byId("diagnostics-events"); eventTarget.replaceChildren();
    if (data?.events === null || !data) unavailable(eventTarget);
    else if (!data.events.length) unavailable(eventTarget, "最近 24 小时的限定事件采样中没有匹配项。");
    else { const events = table(["时间 / 日志", "提供程序 / 事件编号", "安全摘要"]); data.events.forEach(event => { const row = node("tr"); cell(row, `${date(event.timeCreated)} · ${event.logName}`); cell(row, `${event.provider} · ${event.eventId}（级别 ${event.level ?? "—"}）`); cell(row, event.summary); events.body.append(row); }); eventTarget.append(events.element); }
    const logs = byId("diagnostics-iis"); logs.replaceChildren();
    if (data?.iisErrors === null || !data) unavailable(logs);
    else if (!data.iisErrors.length) unavailable(logs, "没有可采样的 IIS 站点日志。");
    else data.iisErrors.forEach(site => { const section = node("section", undefined, "diagnostic-log"); section.append(node("h3", site.siteName), node("p", `${date(site.windowStart)} 至 ${date(site.windowEnd)} 范围内尾部采样 · ${site.sampledFiles ?? "—"} 个文件 / ${site.sampledLines ?? "—"} 行${site.truncated ? " · 已截取，非全量" : ""}`, "muted small")); if (site.errorCode) section.append(node("p", site.errorCode, "diagnostic-warning")); if (site.state === "unavailable") unavailable(section, `${site.siteName}：日志不可用 · ${site.errorCode || "读取失败"}`); else if (!site.errors.length) section.append(node("p", "样本内未发现 4xx/5xx；这不是全量业务健康结论。", "muted")); else { const grid = table(["HTTP / 子状态 / Win32", "次数", "平均耗时", "最大耗时"]); site.errors.forEach(error => { const row = node("tr"); cell(row, `${error.status} / ${error.subStatus ?? "—"} / ${error.win32Status ?? "—"}`); cell(row, String(error.count ?? "—")); cell(row, error.averageMilliseconds === null ? "—" : `${error.averageMilliseconds} ms`); cell(row, error.maximumMilliseconds === null ? "—" : `${error.maximumMilliseconds} ms`); grid.body.append(row); }); section.append(grid.element); } logs.append(section); });
  }
  function renderProcesses() {
    const target = byId("diagnostics-processes"); target.replaceChildren();
    const rows = model.snapshot?.processes;
    if (rows === null || !model.snapshot) return unavailable(target);
    const query = byId("diagnostics-search").value.trim().toLocaleLowerCase("zh-CN");
    const visible = rows.filter(row => !query || `${row.kind} ${row.name} ${row.processId ?? ""}`.toLocaleLowerCase("zh-CN").includes(query));
    if (!visible.length) return unavailable(target, rows.length ? "没有匹配的进程项。" : "当前没有可显示的应用池或可管理服务进程。");
    const grid = table(["类型 / 名称", "状态 / PID", "CPU", "工作集 / 专用内存", "运行时长", "采集状态"]);
    visible.forEach(process => { const row = node("tr"); cell(row, `${process.kind === "applicationPool" ? "应用池" : "服务"} · ${process.name}`); cell(row, `${process.state} / ${process.processId ?? "—"}`); cell(row, process.processId === null ? "—" : cpu(process.cpuPercent)); cell(row, `${bytes(process.workingSetBytes)} / ${bytes(process.privateMemoryBytes)}`); cell(row, duration(process.uptimeSeconds)); cell(row, process.errorCode || (process.processId === null ? "当前无工作进程" : "已采集")); grid.body.append(row); }); target.append(grid.element);
  }
  async function api(path, signal) {
    let response, data;
    try { response = await fetch(path, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" }, signal }); }
    catch (error) { if (error.name === "AbortError") throw error; throw new Error("诊断读取失败，请核对连接后刷新。"); }
    try { data = await response.json(); } catch { throw new Error("诊断响应格式无效，请核对连接后刷新。"); }
    globalThis.IISManagerAuth?.unauthorized(response, data);
    if (!response.ok) { const error = new Error(response.status === 501 || data?.code === "DIAGNOSTICS_UNSUPPORTED" ? "当前 Agent 不支持诊断，请统一更新 Agent。" : "无法读取诊断，请核对连接、权限和服务器状态。"); error.code = code(data?.code); throw error; }
    return data;
  }
  async function refresh() {
    if (!model.serverId) return;
    void globalThis.IISManagerFreb?.load(model.serverId);
    model.controller?.abort(); const revision = ++model.revision; const controller = new AbortController(); model.controller = controller; model.busy = true; notice(); render();
    try { const data = sanitizedSnapshot(await api(`/api/v1/servers/${encodeURIComponent(model.serverId)}/diagnostics`, controller.signal)); if (revision !== model.revision) return; model.snapshot = data; model.stale = false; }
    catch (error) { if (revision !== model.revision || error.name === "AbortError") return; model.stale = true; notice(error.message || "诊断读取失败，请刷新后重试。"); }
    finally { if (revision === model.revision) { model.busy = false; render(); schedule(); } }
  }
  function schedule() { clearTimeout(model.timer); if (byId("diagnostics-auto").checked && !document.hidden && !model.busy) model.timer = setTimeout(refresh, 15000); }
  byId("diagnostics-refresh").addEventListener("click", refresh);
  byId("diagnostics-server").addEventListener("change", () => { model.serverId = byId("diagnostics-server").value; model.snapshot = null; model.stale = false; void refresh(); });
  byId("diagnostics-search").addEventListener("input", renderProcesses);
  byId("diagnostics-auto").addEventListener("change", schedule);
  document.addEventListener("visibilitychange", schedule);
  globalThis.addEventListener?.("pagehide", () => { clearTimeout(model.timer); model.controller?.abort(); });
  byId("diagnostics-export").addEventListener("click", () => {
    if (!model.snapshot || model.stale || model.busy) return;
    const output = JSON.stringify({ serverId: model.serverId, diagnostics: sanitizedSnapshot(model.snapshot) }, null, 2);
    const url = URL.createObjectURL(new Blob([output], { type: "application/json;charset=utf-8" })); const link = node("a"); link.href = url; link.download = `diagnostics-${model.serverId}-${new Date().toISOString().replace(/[:.]/g, "-")}.json`; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  render();
  (async () => { try { const [session, servers] = await Promise.all([api("/api/v1/session"), api("/api/v1/servers")]); globalThis.IISManagerAuth?.render(session); if (!Array.isArray(servers)) throw new Error("服务器列表无效。"); model.servers = servers; const selected = new URLSearchParams(location.search).get("server"); model.serverId = servers.find(server => server.id === selected)?.id || servers[0]?.id || ""; byId("diagnostics-server").replaceChildren(...servers.map(server => { const option = node("option", server.name); option.value = server.id; return option; })); byId("diagnostics-server").value = model.serverId; if (model.serverId) await refresh(); else notice("尚未配置服务器。"); } catch (error) { notice(error.message || "无法读取登录状态或服务器列表。"); } })();
})();
