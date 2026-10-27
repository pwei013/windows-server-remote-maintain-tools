"use strict";
(() => {
  const byId = id => document.getElementById(id);
  const state = { csrf: "", servers: [], permissions: [], serverId: "", comparison: null, pending: null, busy: false, unconfirmed: false, revision: 0 };
  const node = (tag, text) => { const item = document.createElement(tag); if (text !== undefined) item.textContent = text; return item; };
  const date = value => value ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—";
  function notice(text = "", error = false) { const area = byId("notice"); area.textContent = text; area.hidden = !text; area.className = error ? "notice danger" : "notice"; }
  async function api(path, body) {
    let response, data;
    try {
      response = await fetch(path, { method: body === undefined ? "GET" : "POST", cache: "no-store", credentials: "same-origin",
        headers: { Accept: "application/json", ...(body === undefined ? {} : { "Content-Type": "application/json", "X-IISManager-CSRF": state.csrf }) },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
      data = await response.json();
    } catch { const error = new Error(body === undefined ? "配置巡检读取失败，请重试。" : "基线保存结果无法确认，请关闭弹窗并重新巡检，勿重复提交。"); error.unconfirmed = body !== undefined; throw error; }
    globalThis.IISManagerAuth?.unauthorized(response, data);
    if (!response.ok) { const error = new Error(data.message || "无法完成请求。"); error.code = data.code; error.unconfirmed = data.code === "BASELINE_STORE_UNVERIFIED"; throw error; }
    return data;
  }
  function configureAllowed() { return !!state.permissions.find(item => (item.serverId || item.id) === state.serverId)?.configure; }
  function buttons() {
    byId("baseline-server").disabled = state.busy;
    byId("baseline-read").disabled = state.busy || !state.serverId;
    byId("baseline-save-open").disabled = state.busy || state.unconfirmed || !state.comparison || !configureAllowed();
    byId("baseline-export").disabled = state.busy || !state.comparison;
    byId("baseline-save-confirm").disabled = state.busy || state.unconfirmed || !byId("baseline-confirm").checked;
    ["baseline-save-cancel", "baseline-save-close"].forEach(id => byId(id).disabled = state.busy);
  }
  function render() {
    const result = state.comparison, baseline = result?.baseline;
    byId("baseline-status").textContent = !result ? "请选择服务器并执行只读巡检。" : !baseline ? "尚未设置配置基线，可核对当前配置后保存。" : result.matches ? "当前配置与基线一致。" : `发现 ${result.differenceCount} 项配置变化。`;
    byId("baseline-details").replaceChildren();
    if (result) for (const [label, value] of [["基线说明", baseline?.label || "尚未建立"], ["保存时间", date(baseline?.savedAt)], ["保存操作人", baseline?.savedBy || "—"], ["基线版本", baseline?.revision || "—"], ["当前指纹", result.currentFingerprint]]) byId("baseline-details").append(node("dt", label), node("dd", value));
    byId("baseline-changes").replaceChildren(...(result?.differences || []).map(change => { const tr = node("tr"); tr.append(node("td", change.path), node("td", change.before ?? "不存在"), node("td", change.after ?? "不存在")); return tr; }));
    byId("baseline-truncated").hidden = !result || result.differenceCount <= result.differences.length;
    byId("baseline-permission").hidden = configureAllowed(); buttons();
  }
  async function read() {
    if (state.busy || !state.serverId) return;
    const id = state.serverId, revision = ++state.revision; state.busy = true; notice(); buttons();
    try {
      const result = await api(`/api/v1/servers/${encodeURIComponent(id)}/configuration-baseline`);
      if (typeof result.currentFingerprint !== "string" || !Array.isArray(result.differences) || !Number.isSafeInteger(result.differenceCount)) throw new Error("配置巡检数据不完整。");
      if (revision !== state.revision || id !== state.serverId) return;
      state.comparison = result; state.unconfirmed = false; render();
    } catch (error) { state.comparison = null; notice(error.message, true); render(); }
    finally { state.busy = false; buttons(); }
  }
  byId("baseline-read").addEventListener("click", read);
  byId("baseline-server").addEventListener("change", () => { state.serverId = byId("baseline-server").value; state.comparison = null; state.pending = null; byId("baseline-save-dialog").close(); render(); void read(); });
  byId("baseline-save-open").addEventListener("click", () => {
    if (!state.comparison || !configureAllowed() || state.busy || state.unconfirmed) return;
    state.pending = { id: state.serverId, fingerprint: state.comparison.currentFingerprint, revision: state.comparison.baseline?.revision || null };
    byId("baseline-confirm").checked = false; byId("baseline-save-error").hidden = true;
    byId("baseline-save-target").textContent = `${state.servers.find(server => server.id === state.serverId)?.name || state.serverId} · 当前配置 ${state.pending.fingerprint}`;
    byId("baseline-save-dialog").showModal(); buttons();
  });
  byId("baseline-confirm").addEventListener("change", buttons);
  ["baseline-save-cancel", "baseline-save-close"].forEach(id => byId(id).addEventListener("click", () => { if (!state.busy) { byId("baseline-save-dialog").close(); state.pending = null; } }));
  byId("baseline-save-dialog").addEventListener("cancel", event => { if (state.busy) event.preventDefault(); });
  byId("baseline-save-form").addEventListener("submit", async event => {
    event.preventDefault(); const pending = state.pending;
    if (!pending || pending.id !== state.serverId || state.busy || state.unconfirmed || !byId("baseline-confirm").checked || !configureAllowed()) return;
    state.busy = true; buttons();
    try {
      const saved = await api(`/api/v1/servers/${encodeURIComponent(pending.id)}/configuration-baseline`, { label: byId("baseline-label").value.trim(), revision: pending.revision, currentFingerprint: pending.fingerprint });
      if (saved.serverId !== pending.id || saved.fingerprint !== pending.fingerprint || typeof saved.revision !== "string") { const error = new Error("基线保存回执不完整，请重新巡检核对。"); error.unconfirmed = true; throw error; }
      byId("baseline-save-dialog").close(); state.pending = null; state.busy = false; await read();
      if (state.comparison?.baseline?.revision === saved.revision) notice("基线已保存并独立读取核对。");
    } catch (error) {
      if (error.unconfirmed) state.unconfirmed = true;
      byId("baseline-save-error").textContent = error.message; byId("baseline-save-error").hidden = false;
    } finally { state.busy = false; buttons(); }
  });
  byId("baseline-export").addEventListener("click", () => {
    if (!state.comparison) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(state.comparison, null, 2) + "\n"], { type: "application/json;charset=utf-8" }));
    const link = node("a"); link.href = url; link.download = `configuration-baseline-${state.serverId}.json`; link.click(); URL.revokeObjectURL(url);
  });
  (async () => {
    try {
      const session = await api("/api/v1/session"); state.csrf = session.csrfToken; globalThis.IISManagerAuth?.render(session);
      const [servers, permissions] = await Promise.all([api("/api/v1/servers"), api("/api/v1/permissions")]);
      state.servers = servers; state.permissions = permissions.servers || [];
      byId("baseline-server").replaceChildren(...servers.map(server => { const option = node("option", server.name); option.value = server.id; return option; }));
      const requested = new URLSearchParams(location.search).get("server"); state.serverId = servers.some(server => server.id === requested) ? requested : servers[0]?.id || "";
      byId("baseline-server").value = state.serverId; render(); await read();
    } catch (error) { notice(error.message, true); buttons(); }
  })();
})();
