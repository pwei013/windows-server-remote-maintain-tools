"use strict";

(() => {
  const stages = Object.freeze({ pending: "等待验证", running: "正在验证", completed: "验证完成", skipped: "已跳过验证", unknown: "验证结果待核对" });
  const results = Object.freeze({ pending: "等待检查", healthy: "检查正常", unhealthy: "检查异常", unavailable: "无法检查", simulation: "模拟检查", unsupported: "需升级 Agent", skipped: "已跳过", unknown: "结果待核对" });
  const scopes = Object.freeze({ site: "所选站点", agentRecord: "本次操作记录的影响范围", topology: "当前应用拓扑确认的范围" });
  const codes = Object.freeze({
    HEALTH_OPERATION_NOT_SUCCEEDED: "操作未成功，未进行业务验证", HEALTH_OPERATION_UNCONFIRMED: "操作结果尚未确认，未进行业务验证",
    HEALTH_OPERATION_WAIT_TIMEOUT: "等待操作完成已超时", HEALTH_OPERATION_STATUS_UNAVAILABLE: "暂时无法核对操作状态", HEALTH_OPERATION_STATUS_INVALID: "操作状态无法可靠识别",
    HEALTH_SCOPE_UNRESOLVED: "无法确认本次操作涉及的业务范围", HEALTH_SCOPE_UNAVAILABLE: "暂时无法读取操作影响范围", HEALTH_CONFIGURATION_UNAVAILABLE: "健康检查配置无法读取",
    HEALTH_NO_MATCHING_ENABLED_CHECKS: "影响范围内没有已启用的健康检查", HEALTH_CHECK_LIMIT_REACHED: "本批次最多检查 20 项，仅检查前 20 项",
    HEALTH_BATCH_TIMEOUT: "本批次已达到 60 秒上限，未完成项需核对", HEALTH_BATCH_INTERRUPTED: "本批次验证被中断，未自动重放",
    HEALTH_CHECK_CHANGED: "检查配置已修改，未沿用旧配置", HEALTH_CHECK_DISABLED: "检查已停用", HEALTH_CHECK_NOT_FOUND: "检查配置已不存在", HEALTH_CHECK_BUSY: "该检查正在执行",
    HEALTH_CHECK_RESULT_CHANGED: "检查结果已发生变化，请核对最新记录", HEALTH_CHECK_UNAVAILABLE: "暂时无法完成检查", HEALTH_CHECK_CANCELLED: "检查已取消",
    HEALTH_APPLICATION_NOT_FOUND: "应用已不存在", HEALTH_APPLICATION_PATH_MISMATCH: "检查路径不属于当前应用", HEALTH_BINDING_AMBIGUOUS: "无法唯一确定目标站点", HEALTH_BINDING_MISMATCH: "检查地址与站点绑定不匹配",
    HEALTH_BUSY: "Agent 健康检查繁忙", HEALTH_CONNECTION_FAILED: "检查地址无法连接", HEALTH_LOCAL_ADDRESS_UNAVAILABLE: "无法核实本机绑定地址", HEALTH_MANAGEMENT_PORT_DENIED: "检查地址使用了管理端口",
    HEALTH_PATH_INVALID: "检查路径格式不合法", HEALTH_REQUEST_INVALID: "检查参数不合法", HEALTH_RESPONSE_INVALID: "检查响应无法识别", HEALTH_SIMULATION: "模拟环境未执行真实业务请求",
    HEALTH_SITE_NOT_FOUND: "站点已不存在", HEALTH_SITE_STOPPED: "站点已停止", HEALTH_STATUS_MISMATCH: "HTTP 状态与预期不符", HEALTH_TARGET_NOT_RUNNING: "目标当前未运行", HEALTH_TIMEOUT: "检查超时",
    HEALTH_TLS_ERROR: "HTTPS 证书验证失败", HEALTH_TOPOLOGY_UNAVAILABLE: "无法读取站点应用拓扑", HEALTH_URL_INVALID: "检查地址不合法", HEALTH_AGENT_UPGRADE_REQUIRED: "请更新 Agent 后检查",
    AGENT_NOT_FOUND: "Agent 未提供所需能力", AGENT_REQUEST_REJECTED: "Agent 拒绝了检查请求", AGENT_ACCESS_DENIED: "Agent 访问被拒绝", AGENT_AUTHENTICATION_REJECTED: "Agent 认证未通过",
    AGENT_HTTP_ERROR: "Agent 返回异常状态", AGENT_TIMEOUT: "Agent 请求超时", AGENT_DNS_FAILED: "Agent 地址解析失败", AGENT_TLS_FAILED: "Agent 证书验证失败", AGENT_UNREACHABLE: "Agent 暂时无法连接", AGENT_RESPONSE_INVALID: "Agent 响应无法识别"
  });
  const own = (object, key) => typeof key === "string" && Object.prototype.hasOwnProperty.call(object, key);
  function code(value) { return value == null ? null : own(codes, value) ? { value, label: codes[value] } : { value: null, label: "存在未识别的结果代码，请核对服务端记录" }; }
  function timestamp(value) {
    if (typeof value !== "string" || value.length > 40 || !/^\d{4}-\d{2}-\d{2}T/.test(value)) return null;
    const parsed = Date.parse(value); return Number.isFinite(parsed) ? new Date(parsed).toISOString() : null;
  }
  function text(value, fallback) { return typeof value === "string" && value.length <= 1024 && !/[\u0000-\u001f\u007f]/.test(value) && !/https?:\/\//i.test(value) ? value : fallback; }
  function project(value) {
    if (value == null) return null;
    if (value.version !== 1 || !own(stages, value.state) || !Array.isArray(value.results))
      return { state: "unknown", stateLabel: stages.unknown, invalid: true, results: [], matchedChecks: null, errorCode: null, scopeLabel: "范围未确认" };
    return { state: value.state, stateLabel: stages[value.state], invalid: false, requestedAt: timestamp(value.requestedAt), startedAt: timestamp(value.startedAt), completedAt: timestamp(value.completedAt),
      scopeLabel: own(scopes, value.scopeSource) ? scopes[value.scopeSource] : "范围未确认", errorCode: code(value.errorCode),
      matchedChecks: Number.isSafeInteger(value.matchedChecks) && value.matchedChecks >= 0 ? Math.min(value.matchedChecks, 200000) : null,
      truncated: value.results.length > 20, results: value.results.slice(0, 20).map(item => {
        if (!item || typeof item !== "object") return { siteName: "站点未记录", applicationPath: "应用未记录", state: "unknown", stateLabel: results.unknown, statusCode: null, elapsedMilliseconds: null, checkedAt: null, errorCode: null };
        const state = own(results, item.state) ? item.state : "unknown";
        return { siteName: text(item.siteName, "站点未记录"), applicationPath: text(item.applicationPath, "应用未记录"), state, stateLabel: results[state],
          statusCode: Number.isSafeInteger(item.statusCode) && item.statusCode >= 100 && item.statusCode <= 599 ? item.statusCode : null,
          elapsedMilliseconds: Number.isSafeInteger(item.elapsedMilliseconds) && item.elapsedMilliseconds >= 0 && item.elapsedMilliseconds <= 3600000 ? item.elapsedMilliseconds : null,
          checkedAt: timestamp(item.checkedAt), errorCode: code(item.errorCode) };
      }) };
  }
  function isPending(value) { return value?.version === 1 && Array.isArray(value.results) && ["pending", "running"].includes(value.state); }
  function render(container, value, { loading = false, readError = false } = {}) {
    if (!container) return;
    const safe = project(value); container.replaceChildren(); container.hidden = !safe && !loading && !readError;
    if (container.hidden) return;
    const node = (tag, content, className) => { const element = document.createElement(tag); if (content !== undefined) element.textContent = content; if (className) element.className = className; return element; };
    const header = node("div", undefined, "operation-health-heading"); header.append(node("h3", "业务健康验证"));
    const bad = safe?.results.some(item => ["unhealthy", "unavailable", "unknown", "unsupported"].includes(item.state));
    header.append(node("span", safe?.stateLabel || "正在读取验证记录…", `badge ${safe?.state === "unknown" ? "bad" : safe?.state === "completed" && !bad && safe.results.length && safe.results.every(item => item.state === "healthy") ? "good" : ""}`));
    container.append(header, node("p", "业务验证与 IIS 操作结果分别记录；检查异常不会改变操作执行状态。", "muted small"));
    if (loading) container.append(node("p", "正在按原操作编号读取验证记录…", "muted small"));
    if (readError) container.append(node("p", "暂时无法读取最新业务验证记录，已保留最近结果；可按原操作编号重新核对。", "operation-health-warning"));
    if (!safe) return;
    if (safe.invalid) { container.append(node("p", "验证记录格式暂不支持，请更新控制台或核对服务端记录。", "operation-health-warning")); return; }
    const formatTime = time => time ? new Date(time).toLocaleString("zh-CN", { hour12: false }) : "—";
    const meta = node("dl", undefined, "operation-health-meta");
    [["验证范围", safe.scopeLabel], ["匹配检查", safe.matchedChecks == null ? "—" : `${safe.matchedChecks} 项`], ["开始时间", formatTime(safe.startedAt)], ["完成时间", formatTime(safe.completedAt)]].forEach(([label, content]) => meta.append(node("dt", label), node("dd", content)));
    container.append(meta);
    if (safe.errorCode) container.append(node("p", `${safe.errorCode.label}${safe.errorCode.value ? `（${safe.errorCode.value}）` : ""}`, "operation-health-warning"));
    if (safe.truncated && safe.errorCode?.value !== "HEALTH_CHECK_LIMIT_REACHED") container.append(node("p", "仅展示前 20 项验证记录，请核对服务端保存结果。", "operation-health-warning"));
    if (!safe.results.length) { container.append(node("p", ["pending", "running"].includes(safe.state) ? "等待后台记录检查结果。" : "本次没有可展示的检查结果。", "muted small")); return; }
    const scroll = node("div", undefined, "table-scroll"), table = node("table", undefined, "operation-health-table"), head = node("thead"), headerRow = node("tr"), body = node("tbody");
    table.append(node("caption", "本次操作关联的业务健康验证结果", "sr-only"));
    ["站点 / 应用", "检查结果", "HTTP / 耗时", "检查时间", "结果代码"].forEach(title => { const cell = node("th", title); cell.setAttribute("scope", "col"); headerRow.append(cell); }); head.append(headerRow);
    safe.results.forEach(item => {
      const row = node("tr"), target = node("td"), status = node("td"), timing = node("td"), error = node("td");
      target.append(node("strong", item.siteName), node("div", item.applicationPath, "muted"));
      status.append(node("span", item.stateLabel, `badge ${item.state === "healthy" ? "good" : ["unhealthy", "unavailable", "unknown", "unsupported"].includes(item.state) ? "bad" : ""}`));
      timing.append(node("div", item.statusCode == null ? "HTTP —" : `HTTP ${item.statusCode}`), node("div", item.elapsedMilliseconds == null ? "耗时 —" : `${item.elapsedMilliseconds} ms`, "muted"));
      error.append(node("div", item.errorCode?.label || "—")); if (item.errorCode?.value) error.append(node("div", item.errorCode.value, "operation-health-code"));
      row.append(target, status, timing, node("td", formatTime(item.checkedAt)), error); body.append(row);
    });
    table.append(head, body); scroll.append(table); container.append(scroll);
  }
  const api = Object.freeze({ project, isPending, render });
  globalThis.IISOperationHealth = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})();
