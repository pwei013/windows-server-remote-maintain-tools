"use strict";

(() => {
  let csrf = "", redirecting = false, signingOut = false, changingPassword = false, changeUnconfirmed = false;
  const passwordDialog = document.getElementById("password-dialog"), passwordForm = document.getElementById("password-form");
  const passwordInputs = ["password-old", "password-new", "password-confirm"].map(id => document.getElementById(id));
  const safeReturnUrl = value => {
    try {
      const parsed = new URL(value || "/", location.origin);
      if (parsed.origin === location.origin && ["/", "/index.html", "/operations.html", "/overview.html", "/maintenance.html", "/diagnostics.html", "/baselines.html", "/servers.html"].includes(parsed.pathname) && !parsed.username && !parsed.password)
        return parsed.pathname + parsed.search + parsed.hash;
    } catch { /* Invalid or external return destinations fall back to the workbench. */ }
    return "/";
  };
  function loginUrl(reason = "expired") {
    const destination = safeReturnUrl(location.pathname + location.search + location.hash);
    return `/login.html?reason=${encodeURIComponent(reason)}&returnUrl=${encodeURIComponent(destination)}`;
  }
  function unauthorized(response, data) {
    if (response.status !== 401 || !String(data?.code || "").startsWith("APP_USER_")) return;
    if (redirecting) return;
    redirecting = true;
    document.querySelectorAll("button").forEach(button => { button.disabled = true; });
    location.replace(loginUrl());
  }
  function render(session) {
    csrf = session.csrfToken || "";
    const area = document.getElementById("session-user"), name = document.getElementById("session-user-name");
    if (!area || !name) return;
    area.hidden = !session.authenticationRequired || !session.user;
    name.textContent = session.user?.displayName || session.user?.account || "";
    name.title = session.user?.account || "";
  }
  async function logout(reason) {
    if (signingOut || changingPassword) return;
    signingOut = true;
    const button = document.getElementById("session-logout");
    button.disabled = true; button.textContent = "正在退出…";
    try {
      const response = await fetch("/api/v1/auth/logout", { method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { Accept: "application/json", "X-IISManager-CSRF": csrf } });
      const data = await response.json();
      if (!response.ok || data.signedOut !== true) throw new Error(data.message || "无法确认退出结果，请刷新页面核对登录状态。");
      location.replace(loginUrl(reason || (data.remoteConfirmed ? "signed-out" : "logout-unconfirmed")));
    } catch (error) {
      const notice = document.getElementById("notice") || document.getElementById("records-error");
      if (notice) { notice.textContent = error.message || "无法确认退出结果，请刷新页面核对登录状态。"; notice.hidden = false; }
      signingOut = false; button.disabled = false; button.textContent = "退出登录";
    }
  }
  function passwordMessage(text) {
    const message = document.getElementById("password-message");
    message.textContent = text; message.hidden = !text;
  }
  function clearPasswords() {
    passwordInputs.forEach(input => { if (input) { input.value = ""; input.type = "password"; input.removeAttribute("aria-invalid"); } });
    const visible = document.getElementById("password-visible"); if (visible) visible.checked = false;
  }
  function passwordBusy(busy) {
    changingPassword = busy;
    passwordForm?.querySelectorAll("input, button").forEach(control => { control.disabled = busy || redirecting; });
    const submit = document.getElementById("password-submit");
    if (submit) { submit.disabled = busy || changeUnconfirmed || redirecting; submit.textContent = busy ? "正在修改…" : "确认修改"; }
    const relogin = document.getElementById("password-relogin"); if (relogin) relogin.hidden = !changeUnconfirmed;
    ["session-change-password", "session-logout"].forEach(id => { const button = document.getElementById(id); if (button) button.disabled = busy || redirecting || signingOut; });
  }
  document.getElementById("session-change-password")?.addEventListener("click", () => {
    if (changingPassword || signingOut || redirecting) return;
    const showOpenError = message => {
      const notice = document.getElementById("notice") || document.getElementById("records-error");
      if (notice) { notice.textContent = message; notice.hidden = false; }
    };
    if (!passwordDialog || typeof passwordDialog.showModal !== "function") {
      showOpenError("修改密码窗口未能加载，请强制刷新页面；仍未恢复时，请核对控制台页面与认证脚本是否配套更新，并使用支持对话框的新版浏览器。");
      return;
    }
    clearPasswords(); passwordBusy(false);
    passwordMessage(changeUnconfirmed ? "上次密码修改结果尚未确认，请退出并重新登录核对，勿重复提交修改。" : "");
    try { passwordDialog.showModal(); passwordInputs[0]?.focus(); }
    catch { showOpenError("暂时无法打开修改密码窗口，请关闭其他弹窗后重试，或刷新页面核对。"); }
  });
  ["password-close", "password-cancel"].forEach(id => document.getElementById(id)?.addEventListener("click", () => {
    if (!changingPassword) passwordDialog?.close();
  }));
  passwordDialog?.addEventListener("cancel", event => { if (changingPassword) event.preventDefault(); });
  passwordDialog?.addEventListener("close", clearPasswords);
  document.getElementById("password-visible")?.addEventListener("change", event => {
    passwordInputs.forEach(input => { input.type = event.target.checked ? "text" : "password"; });
  });
  document.getElementById("password-relogin")?.addEventListener("click", () => logout("password-unconfirmed"));
  passwordForm?.addEventListener("submit", async event => {
    event.preventDefault(); if (changingPassword || changeUnconfirmed || signingOut || redirecting) return;
    const [oldPassword, newPassword, confirmPassword] = passwordInputs.map(input => input.value);
    if (!oldPassword || oldPassword.length > 128 || newPassword.length < 12 || newPassword.length > 128) {
      passwordMessage("请填写原密码，新密码须为12至128个字符。"); return;
    }
    if (newPassword !== confirmPassword) {
      passwordMessage("两次输入的新密码不一致。"); passwordInputs[2].setAttribute("aria-invalid", "true"); passwordInputs[2].focus(); return;
    }
    passwordMessage(""); passwordBusy(true);
    let confirmedResponse = false;
    try {
      const response = await fetch("/api/v1/auth/change-password", { method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { Accept: "application/json", "Content-Type": "application/json", "X-IISManager-CSRF": csrf },
        body: JSON.stringify({ oldPassword, newPassword, confirmPassword }) });
      const data = await response.json();
      if (data.code === "APP_USER_PASSWORD_OUTCOME_UNKNOWN") {
        redirecting = true; location.replace(loginUrl("password-unconfirmed")); return;
      }
      if (!response.ok) {
        confirmedResponse = true; unauthorized(response, data);
        if (redirecting) return;
        throw new Error(data.message || "暂时无法修改密码，请稍后核对。");
      }
      if (data.passwordChanged !== true || data.signedOut !== true) throw new Error("PASSWORD_RESPONSE_UNCONFIRMED");
      redirecting = true; location.replace(loginUrl("password-changed"));
    } catch (error) {
      if (!confirmedResponse) {
        changeUnconfirmed = true;
        passwordMessage("无法确认密码修改结果。请退出并重新登录核对，勿重复提交修改。");
      } else passwordMessage(error.message || "暂时无法修改密码，请稍后核对。");
    } finally { clearPasswords(); passwordBusy(false); }
  });
  document.getElementById("session-logout")?.addEventListener("click", () => logout());
  globalThis.IISManagerAuth = Object.freeze({ render, unauthorized, safeReturnUrl });
})();
