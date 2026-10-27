"use strict";

(() => {
  const normalize = value => String(value ?? "").trim().toLocaleLowerCase("zh-CN");
  function serverMatches(server, query) { return !normalize(query) || [server.id, server.name, server.group, server.environment, server.agentUrl].some(value => normalize(value).includes(normalize(query))); }
  function validateServer(value, existing = null) {
    if (!/^[A-Za-z0-9_-]{1,64}$/.test(value.id || "")) return "编号只能使用 1–64 位字母、数字、下划线或连字符。";
    if (!value.name?.trim() || value.name.length > 100 || /[\u0000-\u001f\u007f]/.test(value.name)) return "请填写有效显示名称，最多 100 个字符。";
    if ([value.group, value.environment].some(value => typeof value !== "string" || value.length > 64 || /[\u0000-\u001f\u007f]/.test(value))) return "项目和环境最多 64 个字符，不能包含控制字符。";
    let uri; try { uri = new URL(value.agentUrl); } catch { return "请填写完整的 HTTP 或 HTTPS Agent 根地址。"; }
    if (!["http:", "https:"].includes(uri.protocol) || uri.username || uri.password || uri.search || uri.hash || uri.pathname !== "/" || /[\s\\?#]/.test(value.agentUrl) || uri.port === "0") return "Agent 地址必须为 HTTP/HTTPS 根地址，不能包含账号、路径、查询参数或片段。";
    if (uri.protocol === "http:") {
      if (value.accessToken && !/^[A-Fa-f0-9]{64}$/.test(value.accessToken)) return "HTTP Token 须为 64 位十六进制值。";
      if (!value.accessToken && !existing?.hasAccessToken) return "请填写目标 Agent 的 HTTP Token。";
      if (!value.accessToken && existing) {
        let previous; try { previous = new URL(existing.agentUrl); } catch { return "原接入地址无法核对，请重新读取。"; }
        if (previous.protocol !== uri.protocol || previous.host.toLowerCase() !== uri.host.toLowerCase()) return "HTTP 接入地址已改变，请重新输入目标的 Token。";
      }
    } else if (!/^[A-Fa-f0-9]{40}$/.test(String(value.serverCertificateThumbprint || "").replaceAll(" ", ""))) return "HTTPS 服务端证书指纹须为 40 位十六进制值。";
    return "";
  }
  function permissionText(grant) { return [grant.read ? "查看" : "", grant.operate ? "启停/回收" : "", grant.configure ? "修改配置" : ""].filter(Boolean).join("、") || "无动作权限"; }
  function validatePolicy(policy, actor) {
    if (actor && !policy.administrators.some(principal => Number(principal.appId) === Number(actor.appId) && principal.userId === actor.userId)) return "保存后必须保留当前操作者管理员身份。";
    if (policy.enabled && !policy.administrators.length) return "严格授权至少需要一位管理员。";
    for (const principal of [...policy.administrators, ...policy.grants]) if (!Number.isInteger(principal.appId) || principal.appId <= 0 || !principal.userId || principal.userId.length > 128 || principal.userId !== principal.userId.trim() || /[\u0000-\u001f\u007f]/.test(principal.userId)) return "请核对每条用户的 AppId 和 UserId。";
    if (policy.grants.some(grant => !grant.serverIds.length || !grant.read && (grant.operate || grant.configure))) return "每条授权需选择服务器；操作和配置权限同时需要查看权限。";
    return "";
  }
  const pure = { serverMatches, validateServer, validatePolicy, permissionText };
  if (typeof module !== "undefined" && module.exports) module.exports = pure;
  if (typeof document === "undefined") return;
  const byId = id => document.getElementById(id);
  const state = { session: null, csrf: "", authorized: false, busy: false, unconfirmed: false, revision: "", servers: [], policy: null,
    canPersist: false, editingId: null, serverPreview: null, serverDraft: null, policyPreview: null, policyDraft: null, administratorRows: [], grantRows: [] };
  function node(tag, text, className) { const element = document.createElement(tag); if (text !== undefined) element.textContent = text; if (className) element.className = className; return element; }
  function message(text = "", error = false) { byId("notice").textContent = text; byId("notice").hidden = !text; byId("notice").className = error ? "notice danger" : "notice"; }
  function formError(kind, text = "") { byId(`${kind}-form-error`).textContent = text; byId(`${kind}-form-error`).hidden = !text; }
  function invalidate(kind) { state[`${kind}Preview`] = null; state[`${kind}Draft`] = null; byId(`${kind}-preview`).hidden = true; updateButtons(); }
  function clearCredentials() { byId("managed-token").value = ""; state.serverDraft = null; state.serverPreview = null; }
  function closeEditors() { byId("server-editor").close(); byId("policy-editor").close(); clearCredentials(); }
  async function api(path, body, mutating = false) {
    let response, data;
    const options = { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } };
    if (body !== undefined) { options.method = "POST"; options.headers["Content-Type"] = "application/json"; options.headers["X-IISManager-CSRF"] = state.csrf; options.body = JSON.stringify(body); }
    try { response = await fetch(path, options); data = await response.json(); }
    catch { const error = new Error(mutating ? "无法确认保存结果，请关闭窗口并重新读取核对，勿重复提交。" : "暂时无法完成读取或验证，请检查连接后重试。"); error.unconfirmed = mutating; throw error; }
    globalThis.IISManagerAuth?.unauthorized(response, data);
    if (!response.ok) { const error = new Error(data.message || "请求未能完成，请重新读取后核对。"); error.code = data.code; error.status = response.status; error.unconfirmed = mutating && (response.status >= 500 || ["ADMINISTRATION_STORE_UNVERIFIED", "ADMINISTRATION_STORE_UNAVAILABLE", "ADMINISTRATION_STORE_CHANGED"].includes(data.code)); throw error; }
    return data;
  }
  function updateButtons() {
    const locked = state.busy || !state.authorized || state.unconfirmed;
    document.querySelectorAll("[data-admin-action]").forEach(button => { button.disabled = state.busy || !state.authorized; });
    byId("admin-refresh").disabled = state.busy;
    ["server-form", "policy-form"].forEach(id => byId(id).querySelectorAll("input, select").forEach(control => { control.disabled = locked; }));
    ["server-test", "server-preview-button", "policy-preview-button", "admin-row-add", "grant-row-add"].forEach(id => { byId(id).disabled = locked; });
    document.querySelectorAll("[data-remove-row]").forEach(control => { control.disabled = locked || control.dataset.self === "true"; });
    byId("server-save").disabled = locked || !state.canPersist || !state.serverPreview;
    byId("policy-save").disabled = locked || !state.canPersist || !state.policyPreview;
    ["server-close", "server-cancel", "policy-close", "policy-cancel"].forEach(id => { byId(id).disabled = state.busy; });
    ["server-reconcile", "policy-reconcile"].forEach(id => { byId(id).hidden = !state.unconfirmed; byId(id).disabled = state.busy; });
  }
  function renderServers() {
    const filtered = state.servers.filter(server => serverMatches(server, byId("server-admin-search").value));
    byId("server-admin-count").textContent = `${filtered.length} / ${state.servers.length} 台`;
    byId("server-admin-empty").hidden = !!filtered.length;
    byId("server-admin-body").replaceChildren(...filtered.map(server => {
      const row = node("tr"), identity = node("td"), group = node("td"), address = node("td", server.agentUrl, "managed-server-address"), status = node("td", undefined, "managed-server-state"), action = node("td");
      identity.append(node("strong", server.name), node("div", server.id, "managed-server-id")); group.append(node("div", server.group || "未分组"), node("div", server.environment || "未设置", "muted small"));
      status.append(node("span", server.hasAccessToken ? "HTTP 凭据已配置" : server.serverCertificateThumbprint ? "HTTPS 证书已配置" : server.simulation ? "开发模拟" : "未配置凭据"), node("span", server.restartRequired ? "重启后生效" : "当前已生效", `badge ${server.restartRequired ? "warn" : "good"}`));
      const edit = node("button", "编辑", "secondary small-button"); edit.type = "button"; edit.dataset.adminAction = "true"; edit.addEventListener("click", () => openServer(server)); action.append(edit); row.append(identity, group, address, status, action); return row;
    }));
  }
  function renderPolicy() {
    const policy = state.policy;
    byId("policy-summary").textContent = policy.enabled ? "严格授权已开启：未授权的服务器和动作默认拒绝" : "当前沿用原有服务器管理权限；接入与授权设置仍仅限管理员";
    const rows = policy.administrators.map(principal => ({ principal, scope: "所有服务器及接入设置", permissions: "管理员" }))
      .concat(policy.grants.map(grant => ({ principal: grant, scope: grant.serverIds.map(id => state.servers.find(server => server.id === id)?.name || id).join("、"), permissions: permissionText(grant) })));
    byId("policy-body").replaceChildren(...rows.map(({ principal, scope, permissions }) => { const row = node("tr"); row.append(node("td", `${principal.appId} / ${principal.userId}`), node("td", scope, "policy-scope"), node("td", permissions)); return row; }));
  }
  async function load() {
    if (state.busy) return;
    state.busy = true; updateButtons(); message();
    try {
      const permissions = await api("/api/v1/permissions"); state.authorized = permissions.administration === true;
      byId("admin-unavailable").hidden = state.authorized; byId("admin-content").hidden = !state.authorized;
      if (!state.authorized) { state.servers = []; state.policy = null; byId("server-admin-body").replaceChildren(); byId("policy-body").replaceChildren(); closeEditors(); return; }
      const [servers, policy] = await Promise.all([api("/api/v1/administration/servers"), api("/api/v1/administration/policy")]);
      if (!Array.isArray(servers.servers) || typeof servers.revision !== "string" || policy.revision !== servers.revision || !Array.isArray(policy.policy?.administrators) || !Array.isArray(policy.policy?.grants)) throw new Error("读取的配置版本不一致或不完整，请重新读取后再编辑。");
      state.servers = servers.servers; state.policy = policy.policy; state.revision = servers.revision; state.canPersist = servers.canPersist === true && policy.canPersist === true; state.unconfirmed = false;
      byId("admin-pending").hidden = !servers.pendingRestart;
      byId("admin-protection").textContent = state.canPersist ? servers.protection === "development-plaintext-v1" ? "当前为显式启用的本机开发明文存储，不可用于生产。" : "接入凭据由当前 Console 服务身份保护；页面不会显示已保存的 Token。" : "当前没有可用的安全配置存储，可以读取和测试，暂不能保存。生产不会回退明文。";
      renderServers(); renderPolicy();
    } catch (error) {
      state.unconfirmed = true;
      if (error.status === 403) { state.authorized = false; byId("admin-content").hidden = true; byId("admin-unavailable").hidden = false; closeEditors(); }
      message(error.message, true);
    } finally { state.busy = false; updateButtons(); }
  }
  async function run(action, kind) {
    if (state.busy || state.unconfirmed || !state.authorized) return;
    state.busy = true; updateButtons(); formError(kind);
    try { await action(); }
    catch (error) {
      if (error.unconfirmed || error.status === 409) { state.unconfirmed = true; state[`${kind}Preview`] = null; }
      if (error.status === 403) { state.authorized = false; closeEditors(); byId("admin-content").hidden = true; byId("admin-unavailable").hidden = false; message(error.message, true); }
      else formError(kind, error.message);
    } finally { state.busy = false; updateButtons(); }
  }
  function renderChanges(kind, preview) {
    if (!preview || typeof preview.fingerprint !== "string" || preview.revision !== state.revision || !Array.isArray(preview.changes)) throw new Error("预览结果不完整，请重新读取配置。");
    byId(`${kind}-changes`).replaceChildren(...(preview.changes.length ? preview.changes.map(change => { const row = node("div", undefined, "administration-change"); row.append(node("strong", change.field), node("p", `原：${change.before || "空"}`, "change-before"), node("p", `新：${change.after || "空"}`, "change-after")); return row; }) : [node("p", "没有检测到字段变化。", "muted")]));
    byId(`${kind}-preview`).hidden = false;
  }
  function openServer(server = null) {
    if (state.busy || state.unconfirmed || !state.authorized) return;
    clearCredentials(); state.editingId = server?.id || null; invalidate("server"); formError("server"); byId("server-test-result").hidden = true;
    byId("server-editor-title").textContent = server ? "编辑服务器接入" : "新增服务器";
    for (const [id, value] of [["managed-id", server?.id || ""], ["managed-name", server?.name || ""], ["managed-group", server?.group || ""], ["managed-environment", server?.environment || ""], ["managed-url", server?.agentUrl || ""], ["managed-certificate", server?.serverCertificateThumbprint || ""]]) byId(id).value = value;
    byId("managed-id").readOnly = !!server; byId("managed-token").placeholder = server?.hasAccessToken ? "已配置；同一地址可留空保留" : "输入目标 Agent 的 Token";
    updateAuthenticationFields(); byId("server-editor").showModal(); byId(server ? "managed-name" : "managed-id").focus();
  }
  function updateAuthenticationFields() {
    let https = false; try { https = new URL(byId("managed-url").value).protocol === "https:"; } catch { }
    byId("managed-token-area").hidden = https; byId("managed-certificate-area").hidden = !https;
  }
  function serverForm() {
    return { revision: state.revision, id: byId("managed-id").value.trim(), name: byId("managed-name").value.trim(), group: byId("managed-group").value.trim(), environment: byId("managed-environment").value.trim(), agentUrl: byId("managed-url").value.trim(), accessToken: byId("managed-token").value, serverCertificateThumbprint: byId("managed-certificate").value.trim() };
  }
  function validatedServer() {
    const value = serverForm();
    if (!state.editingId && state.servers.some(server => server.id === value.id)) throw new Error("该编号已存在，请关闭窗口后编辑已有接入。");
    const error = validateServer(value, state.servers.find(server => server.id === state.editingId)); if (error) throw new Error(error); return value;
  }
  function input(type, value, text, parent) {
    const control = node("input"); control.type = type; if (type === "checkbox") control.checked = value; else control.value = value;
    const label = node("label", text); label.append(control); parent.append(label); return control;
  }
  function removeRowButton(parent, rows, row, onRemove) {
    const remove = node("button", "移除", "secondary small-button"); remove.type = "button"; remove.dataset.removeRow = "true";
    remove.addEventListener("click", () => { if (state.busy) return; rows.splice(rows.indexOf(row), 1); parent.remove(); invalidate("policy"); onRemove?.(); }); parent.append(remove); return remove;
  }
  function addAdministrator(principal = { appId: state.session?.user?.appId || "", userId: "" }) {
    const parent = node("div", undefined, "principal-row"), row = {};
    row.appId = input("number", principal.appId, "AppId", parent); row.appId.min = "1"; row.appId.required = true;
    row.userId = input("text", principal.userId, "UserId", parent); row.userId.maxLength = 128; row.userId.required = true;
    state.administratorRows.push(row); const remove = removeRowButton(parent, state.administratorRows, row);
    if (Number(principal.appId) === Number(state.session?.user?.appId) && principal.userId === state.session?.user?.userId) { remove.dataset.self = "true"; remove.disabled = true; remove.title = "必须保留当前操作者管理员身份"; }
    parent.addEventListener("input", () => invalidate("policy")); byId("admin-editor-rows").append(parent); invalidate("policy");
  }
  function addGrant(grant = { appId: state.session?.user?.appId || "", userId: "", serverIds: [], read: true, operate: false, configure: false }) {
    const parent = node("div", undefined, "grant-row"), row = {};
    row.appId = input("number", grant.appId, "AppId", parent); row.appId.min = "1"; row.appId.required = true;
    row.userId = input("text", grant.userId, "UserId", parent); row.userId.required = true; row.userId.maxLength = 128;
    const label = node("label", "服务器范围"), select = node("select"); select.multiple = true; select.size = 3;
    state.servers.forEach(server => { const option = node("option", `${server.name}（${server.id}）${server.restartRequired ? " · 重启后接入" : ""}`); option.value = server.id; option.selected = grant.serverIds.includes(server.id); select.append(option); }); label.append(select); parent.append(label); row.servers = select;
    const flags = node("div", undefined, "grant-flags"); row.read = input("checkbox", grant.read, "查看", flags); row.operate = input("checkbox", grant.operate, "启停/回收", flags); row.configure = input("checkbox", grant.configure, "修改配置", flags); parent.append(flags);
    row.read.addEventListener("change", () => { if (!row.read.checked) { row.operate.checked = false; row.configure.checked = false; } });
    [row.operate, row.configure].forEach(control => control.addEventListener("change", () => { if (control.checked) row.read.checked = true; }));
    state.grantRows.push(row); removeRowButton(parent, state.grantRows, row); parent.addEventListener("input", () => invalidate("policy")); parent.addEventListener("change", () => invalidate("policy")); byId("grant-editor-rows").append(parent); invalidate("policy");
  }
  function openPolicy() {
    if (state.busy || state.unconfirmed || !state.authorized) return;
    state.administratorRows = []; state.grantRows = []; byId("admin-editor-rows").replaceChildren(); byId("grant-editor-rows").replaceChildren(); byId("policy-enabled").checked = state.policy.enabled;
    state.policy.administrators.forEach(addAdministrator); state.policy.grants.forEach(addGrant); invalidate("policy"); formError("policy"); byId("policy-editor").showModal();
  }
  function policyForm() {
    return { revision: state.revision, policy: { enabled: byId("policy-enabled").checked,
      administrators: state.administratorRows.map(row => ({ appId: Number(row.appId.value), userId: row.userId.value.trim() })),
      grants: state.grantRows.map(row => ({ appId: Number(row.appId.value), userId: row.userId.value.trim(), serverIds: [...row.servers.selectedOptions].map(option => option.value), read: row.read.checked, operate: row.operate.checked, configure: row.configure.checked })) } };
  }
  byId("admin-refresh").addEventListener("click", async () => { closeEditors(); await load(); });
  byId("server-admin-search").addEventListener("input", renderServers);
  byId("server-add").addEventListener("click", () => openServer()); byId("policy-edit").addEventListener("click", openPolicy);
  ["managed-id", "managed-name", "managed-group", "managed-environment", "managed-url", "managed-token", "managed-certificate"].forEach(id => byId(id).addEventListener("input", () => { invalidate("server"); if (id === "managed-url") updateAuthenticationFields(); }));
  byId("server-test").addEventListener("click", () => run(async () => { const result = await api("/api/v1/administration/servers/test", validatedServer()); byId("server-test-result").textContent = `连接成功：${result.machineName} · Agent ${result.agentVersion} · 协议 ${result.protocolVersion}。未保存或切换当前接入。`; byId("server-test-result").hidden = false; }, "server"));
  byId("server-preview-button").addEventListener("click", () => run(async () => { const draft = validatedServer(), preview = await api("/api/v1/administration/servers/preview", draft); renderChanges("server", preview); state.serverDraft = draft; state.serverPreview = preview; }, "server"));
  byId("server-form").addEventListener("submit", async event => { event.preventDefault(); if (!state.serverPreview || !state.canPersist) return; await run(async () => { const result = await api("/api/v1/administration/servers/save", { ...state.serverDraft, expectedFingerprint: state.serverPreview.fingerprint }, true); if (typeof result.revision !== "string") { const error = new Error("保存回执不完整，请重新读取核对。"); error.unconfirmed = true; throw error; } closeEditors(); state.revision = result.revision; message("接入配置已保存并核对，人工重启 Console 后生效。"); }, "server"); if (!state.unconfirmed && !byId("server-editor").open) await load(); });
  byId("policy-enabled").addEventListener("change", () => invalidate("policy"));
  byId("admin-row-add").addEventListener("click", () => addAdministrator()); byId("grant-row-add").addEventListener("click", () => addGrant());
  byId("policy-preview-button").addEventListener("click", () => run(async () => { const draft = policyForm(), error = validatePolicy(draft.policy, state.session?.user); if (error) throw new Error(error); const preview = await api("/api/v1/administration/policy/preview", draft); renderChanges("policy", preview); state.policyDraft = draft; state.policyPreview = preview; }, "policy"));
  byId("policy-form").addEventListener("submit", async event => { event.preventDefault(); if (!state.policyPreview || !state.canPersist) return; await run(async () => { const result = await api("/api/v1/administration/policy/save", { ...state.policyDraft, expectedFingerprint: state.policyPreview.fingerprint }, true); if (typeof result.revision !== "string") { const error = new Error("权限保存回执不完整，请重新读取核对。"); error.unconfirmed = true; throw error; } closeEditors(); message("权限策略已保存并立即生效。"); }, "policy"); if (!state.unconfirmed && !byId("policy-editor").open) await load(); });
  ["server-close", "server-cancel"].forEach(id => byId(id).addEventListener("click", () => { if (!state.busy) { byId("server-editor").close(); clearCredentials(); } }));
  ["policy-close", "policy-cancel"].forEach(id => byId(id).addEventListener("click", () => { if (!state.busy) byId("policy-editor").close(); }));
  ["server-editor", "policy-editor"].forEach(id => byId(id).addEventListener("cancel", event => { if (state.busy) event.preventDefault(); }));
  byId("server-editor").addEventListener("close", clearCredentials);
  ["server-reconcile", "policy-reconcile"].forEach(id => byId(id).addEventListener("click", async () => { closeEditors(); await load(); }));
  (async () => {
    try { state.session = await api("/api/v1/session"); state.csrf = state.session.csrfToken; globalThis.IISManagerAuth?.render(state.session); const user = state.session.user; byId("admin-identity").textContent = user ? `当前用户：${user.displayName || user.account} · AppId ${user.appId} / UserId ${user.userId}` : "本机开发会话"; await load(); }
    catch (error) { message(error.message, true); }
  })();
})();
