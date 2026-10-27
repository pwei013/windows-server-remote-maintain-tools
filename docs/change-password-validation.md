# 修改本人密码：使用与验证

2026-10-12。登录后点击顶部用户区的“修改密码”，填写原密码、新密码和确认新密码。新密码为12–128个字符，两次新密码须一致；提供显隐开关，取消、关闭或提交结束时清空输入。主界面与操作记录页共用入口和弹窗，不需要新增 appsettings.Local.json 配置，原 HTTP 登录支持保留。

提交调用 `POST /api/v1/auth/change-password`，JSON 字段为 `oldPassword`、`newPassword`、`confirmPassword`。保留 Origin/CSRF 验证和管理 POST 写前本人复核，确认字段仅在 Console 校验；后端以当前用户 Bearer 调用平台 `/open/user-auth-change-password/{现有调用标识}`，沿用分段 RSA、大写十六进制密文及 JSON 字符串正文，明文只含 OldPassword、NewPassword，不添加普通业务 MD5 签名，不允许请求切换用户。

成功需平台 HTTP 成功和 ResultCode=200。本地返回 `{ "passwordChanged": true, "signedOut": true }`，立即清理本控制台同一应用、同一用户的全部会话，其他用户不受影响；平台也注销该用户的全部 Token。页面返回登录并提示使用新密码。密码和上游 Token 不返回前端、不进入 URL/日志/存储或操作账本。

平台用401同时表示原密码错误和会话失效。改密收到401后，仅发送一次只读本人查询：Token有效则提示原密码错误，允许重新填写；会话失效则退出。限流返回429并沿用60秒冷却，不自动重试。网络中断、响应格式异常或平台503不能证明写入未提交，清除本地本人会话并报告结果不确定，提示重新登录核对；不重放改密。浏览器无法读取响应时清空输入并阻止继续提交，提供“退出并重新登录”入口。

本机认证专项28/28、前端专项9/9通过。新增覆盖参数/确认校验、RSA正文及Bearer、旧密码错误、提交期间Token失效、多个本人会话注销、其他用户隔离、写入成功但回包丢失、429、HTTP端点CSRF与写前复核，以及重复点击、前端未知结果和登录跳转。前端测试使用隔离DOM/响应夹具，后端使用合成内存用户与Handler；未修改真实密码或访问真实平台。

Windows Server目标 `win-x64 / net10.0 / Release / SelfContained=true` 构建零警告、零错误；macOS本机测试项目构建通过。实现阶段未publish或打包；2026-10-13按要求另行完成SDK publish和[修改密码部署包](../outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/README.md)，详见[发布验证记录](change-password-package-validation.md)。此前登录/HTTP兼容包不包含此项。未部署，目标Windows、真实平台与SQL Server验收尚未进行。

浏览器实际验证两页入口、弹窗字段和提示、取消清空、密码默认掩码、正常退出，保存[工作台弹窗](ui-evidence/change-password-20261012/workbench-dialog.jpg)和[操作记录页弹窗](ui-evidence/change-password-20261012/history-dialog.jpg)。修改密码的成功及异常流程由上述合成HTTP和前端夹具测试验证，未通过浏览器修改真实账号密码。本轮临时预览以测试项目 `--user-auth-preview` 启动，PID7567、端口52145，结束后合成会话退出、临时浏览器页关闭、进程正常停止，端口独立读回已关闭；以前保留的预览未处理。

复验入口：先按既有恢复资产构建测试项目，再运行 `dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/osx-arm64/IISManager.Transport.SelfTests.dll --user-auth-only <项目根目录>`；前端运行 `node tests/frontend/password-change.test.cjs`，均不依赖真实账号或外部服务。
