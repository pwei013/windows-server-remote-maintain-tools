"use strict";

(() => {
  const byId = id => document.getElementById(id);
  let csrf = "", submitting = false, ready = false;
  const form = byId("login-form"), account = byId("login-account"), password = byId("login-password"), submit = byId("login-submit");
  const destination = IISManagerAuth.safeReturnUrl(new URLSearchParams(location.search).get("returnUrl"));
  function message(text, error = false) {
    const node = byId("login-message"); node.textContent = text; node.hidden = !text;
    node.className = error ? "login-message error" : "login-message";
  }
  function busy(value) {
    submitting = value; submit.disabled = value || !ready;
    submit.textContent = value ? "正在登录…" : "登录管理工作台";
    form.setAttribute("aria-busy", String(value));
    account.disabled = value; password.disabled = value; byId("login-password-toggle").disabled = value;
  }
  byId("login-password-toggle").addEventListener("click", () => {
    const visible = password.type === "password"; password.type = visible ? "text" : "password";
    byId("login-password-toggle").setAttribute("aria-pressed", String(visible));
    byId("login-password-toggle").setAttribute("aria-label", visible ? "隐藏密码" : "显示密码");
    byId("login-password-eye").src = visible ? "/assets/fluent/eye-off.svg" : "/assets/fluent/eye.svg";
    password.focus();
  });
  form.addEventListener("submit", async event => {
    event.preventDefault(); if (submitting || !ready) return;
    if (!account.value.trim() || !password.value) { message("请填写账号和密码。", true); return; }
    message(""); account.removeAttribute("aria-invalid"); password.removeAttribute("aria-invalid"); busy(true);
    try {
      const response = await fetch("/api/v1/auth/login", { method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { Accept: "application/json", "Content-Type": "application/json", "X-IISManager-CSRF": csrf },
        body: JSON.stringify({ account: account.value.trim(), password: password.value }) });
      const data = await response.json();
      password.value = ""; password.type = "password";
      byId("login-password-toggle").setAttribute("aria-pressed", "false");
      byId("login-password-toggle").setAttribute("aria-label", "显示密码"); byId("login-password-eye").src = "/assets/fluent/eye.svg";
      if (!response.ok) {
        if (response.status === 401) { account.setAttribute("aria-invalid", "true"); password.setAttribute("aria-invalid", "true"); }
        throw new Error(data.message || (response.status === 429 ? "登录过于频繁，请稍后再试。" : "暂时无法登录，请稍后再试。"));
      }
      if (!data.user || !data.expiresAtUtc || !Number.isFinite(Date.parse(data.expiresAtUtc))) throw new Error("登录响应不完整，请联系管理员核对。");
      location.replace(destination);
    } catch (error) {
      password.value = ""; message(error.message || "无法确认登录结果，请稍后核对登录状态。", true); busy(false);
      password.focus();
    }
  });
  (async () => {
    try {
      const response = await fetch("/api/v1/session", { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
      const session = await response.json();
      if (!response.ok || !session.csrfToken) throw new Error("无法读取登录会话，请刷新页面重试。");
      if (session.authenticationRequired === false || session.user) { location.replace(destination); return; }
      if (session.loginRequiresHttps) { message("登录需要通过 HTTPS 访问本控制台，请联系管理员配置访问地址。", true); return; }
      csrf = session.csrfToken; ready = true; busy(false);
      const reason = new URLSearchParams(location.search).get("reason");
      if (session.authenticationConfigured === false) message("登录服务尚未配置，请联系管理员完成应用调用配置。", true);
      else if (reason === "expired") message("登录已失效，请重新登录；原操作记录仍可在登录后核对。");
      else if (reason === "signed-out") message("已退出登录。");
      else if (reason === "password-changed") message("密码已修改，请使用新密码重新登录。");
      else if (reason === "password-unconfirmed") message("密码修改结果尚未确认，已退出本控制台。请尝试用新密码登录；无法登录时联系管理员核实，勿重复提交修改。", true);
      else if (reason === "logout-unconfirmed") message("已退出本控制台；上游退出结果尚未确认，请联系管理员核对。");
    } catch (error) { message(error.message || "暂时无法读取登录状态，请刷新页面重试。", true); }
  })();
})();
