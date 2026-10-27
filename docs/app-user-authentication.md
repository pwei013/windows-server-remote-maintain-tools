# 应用用户登录与 OpenAPI Token 接入

2026-10-12：源码新增修改本人密码功能，主界面和操作记录页共用弹窗；复用既有应用连接，不新增配置。成功后退出全部本人会话，详细流程与验证见[修改密码说明](change-password-validation.md)。2026-10-13已另行提供[修改密码增量包](../outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/README.md)，18个覆盖文件，历史包未修改，未部署。

2026-10-11：按用户选定的公司 Logo 版方案 1，在现有 Console 中实现登录页、会话保护、本人查询和退出。认证协议依据[客户应用用户认证 OpenAPI 使用说明](/Users/pengwei/MKY/MKYAPI3/MKY.Business.V3/doc/客户应用用户认证OpenAPI使用说明.md)。后续按用户要求默认允许 HTTP 登录，并生成[HTTP 兼容 Windows x64 未压缩增量包](../outputs/MKY服务器远程管理工具-登录认证-HTTP兼容-20261011-win-x64/README.md)；上一登录认证包仍强制 HTTPS，历史包与现场配置未修改或部署。配置合并、可选 HTTPS 证书和人工更新见[新版随包说明](../outputs/MKY服务器远程管理工具-登录认证-HTTP兼容-20261011-win-x64/部署说明.md)。

## 运行配置

生产环境要求应用用户登录。UserAuthentication 缺省启用，不能在 Production 中关闭；显式关闭只允许 Development 且监听回环地址。RequireHttps 缺省 false，允许 HTTP 登录；不需要开启 AllowDevelopmentHttp 或切换 Development。本地原开发配置关闭认证，模拟验证另用隔离夹具启用认证。

Console 复用既有 `Console:SiteAnnotations` 的 `ApplicationId`、`OpenApiBaseUrl`、`OpenApiAppId`、`PublicKeyBase64` 和 `TimeoutSeconds`，无需另配账号或保存密码。当前连接绑定应用 1001；调用标识仍按平台给出的加密值使用。认证可使用这份连接，即使站点标注功能关闭。连接缺失或密钥无效时，登录失败并给出固定配置提示，不开放工作台。

按实际配置在受限 `appsettings.Local.json` 合并以下节点，保留原有字段：

```json
{
  "Console": {
    "UserAuthentication": {
      "Enabled": true,
      "RequireHttps": false,
      "ValidationIntervalSeconds": 60
    },
    "BrowserHttps": false,
    "AllowDevelopmentHttp": false
  }
}
```

`BrowserHttps=false` 明确让 Console 原端口使用 HTTP，即使 Agent 使用 HTTPS/mTLS 也无需改变原 Agent 配置；`true` 使用 HTTPS。该字段缺省时才按原 TransportMode 决定入口协议，兼容旧安装。当前一个端口按一个协议监听，不提供双协议端口。可选 HTTPS 需设置 ServerCertificateThumbprint，证书位于运行主机 LocalMachine/My，包含服务器认证用途及私钥，Console 服务身份具有读取权限，访问域名/IP 与 SAN、AllowedHosts 对应。没有安装证书、改 ACL、重启现场服务或部署。

HTTP 与 HTTPS 下都可以登录并使用受保护工作台；仅显式 RequireHttps=true 时后端拒绝 HTTP 登录，页面才显示 HTTPS 提示。HTTPS 时 Cookie 带 Secure，HTTP 时不带，均保留 HttpOnly/SameSiteStrict；上游 Token 仍仅在服务端内存保存。HTTP 浏览器到 Console 的账号密码及会话没有 TLS 加密，适合受控内网。允许 HTTP 时，HTTPS 响应使用 HSTS max-age=0 清除此前强制升级策略。框架未新增反向代理转发头信任；原开发例外仍限定 Development 与回环，不用于生产 HTTP。

上一登录认证包不识别 RequireHttps，必须覆盖新版程序才能消除其 HTTP 登录拦截；仅修改配置无效。

## 请求与会话

- 后端调用 `POST {现有/open前缀}/user-auth-login/{调用AppID}`，明文对象为 Account、Password；使用既有 UTF-8 分段 RSA PKCS#1 v1.5、大写十六进制密文及 JSON 字符串正文。固定认证路由不添加业务接口的 MD5 签名字段。
- 成功需要 HTTP 成功和 ResultCode=200，并校验 TokenType、AccessToken、UTC 有效期及 User 的应用和用户身份。登录响应不返回上游 Token。
- 每个浏览器使用独立的随机、不透明 HttpOnly/SameSite=Strict Cookie；HTTPS 下带 Secure。服务端内存保存上游会话，不持久化密码或 Token，不放入浏览器存储、日志、URL、操作账本或错误响应。Console 重启后重新登录。
- 工作台、操作记录页及管理 API 统一保护。只读请求的本人复核缓存默认 60 秒，可配 60–300 秒；管理 POST 发送前必须重新复核。缓存不延长 Token 的原有效期。复核收到 429 后该会话冷却 60 秒，轮询不会在冷却期再次请求上游。
- QuerySiteAnnotations、CreateSiteAnnotation、UpdateSiteAnnotation 在每个请求上设置当前用户的 `Authorization: Bearer`；保留原 RSA/MD5 业务协议，不修改共享 HttpClient 默认请求头。上游 HTTP/业务码 401 清理当前浏览器会话并要求重新登录。
- me/logout 的空对象仍加密发送。退出先结束本地会话；上游退出响应丢失时显示“本地退出完成，上游结果未确认”，不自动重放。业务写入结果未知且读回遇到认证失效时，继续保留未知状态，不改称未执行。
- 登录后显示用户名称、修改密码和退出入口；登录跳转只接受工作台、操作记录的同源路径，保留原操作编号用于核对。修改本人密码以当前用户 Token 调用 user-auth-change-password，RSA 加密前只发送 OldPassword、NewPassword，不添加业务签名。新密码12–128字符，两次输入必须一致，成功后立即清除本控制台该用户的全部会话；平台同时注销其全部 Token。没有新增角色、注册或自助重置入口；所有获准登录的控制台用户沿用既有管理能力，实际业务用户由应用维护。

现有 Agent 访问令牌继续用于 Console→Agent 通信；应用用户 Token 用于 Console→Business，二者用途不同。原来源网络、Host、同源/CSRF 限制继续生效，CSRF 引导 Cookie 在多个页面之间复用。

## Console 浏览器接口

| 方法与路径 | 用途 |
| --- | --- |
| GET /api/v1/session | CSRF 引导、认证启用/配置状态、当前用户和到期时间；没有上游 Token |
| POST /api/v1/auth/login | JSON 对象 account、password；需要同源及 CSRF 头 |
| GET /api/v1/auth/me | 受保护本人信息与到期时间 |
| POST /api/v1/auth/logout | 需要同源及 CSRF；返回本地退出状态和上游确认状态 |
| POST /api/v1/auth/change-password | 受保护，JSON 对象 oldPassword、newPassword、confirmPassword；需要同源及 CSRF，写前复核，成功返回 passwordChanged=true、signedOut=true |

所有 POST 沿用 `X-IISManager-CSRF`，Origin 必须匹配 Console 的访问源。接口只输出白名单错误码和固定提示，不透传平台 ResultMsg 或异常正文。

## 本次验证与边界

在 macOS arm64 / .NET SDK 10.0.401 完成：

- 实现阶段及后续 HTTP 修订完成 Windows Server 目标 `net10.0 / win-x64 / Release / SelfContained=true` 构建/发布，零警告、零错误。修订包运行时仍为 10.0.12，50 个覆盖文件与 387 个基线配套文件组成 437 个完整运行文件。原记录见[首版部署包验证](login-auth-package-validation.md)，修订见[HTTP 兼容版验证](login-auth-http-package-validation.md)。
- 认证专项 20 项检查，包括分段加密、HTTP/业务码、无效成功响应、应用绑定、Cookie、安全跳转、过期/撤销、并发用户隔离、读缓存/写前复核、限流退避、退出未知结果和真实本机 HTTP 流程；数据及上游均为合成夹具。
- 新登录前端 12 项、既有工作台与历史页 110 项、站点标注 29 项检查通过。
- 浏览器验证错误登录、密码显隐、成功登录、标注查询/新增/修改与独立读回、历史页身份、退出后访问拦截、390px 登录及工作台退出入口。原图与实现的全图及表单局部同输入对照见[设计验收](../design-qa.md)。
- HTTP 修订认证专项 21 项及传输 121 项通过，覆盖 Production HTTP 登录、页面引导状态、Cookie、CSRF、退出拦截、浏览器 HTTP 与 HTTPS Agent 独立配置、旧配置兼容；初次传输用例仍预期匿名访问，已按新的登录要求修正 6 项，未放宽生产认证。

尚未使用真实应用账号进行外部登录或业务调用，未验证 SQL Server 存储过程、Windows 证书加载、实际服务身份及 Windows/IIS 环境。人工更新时需同时更新 Console 程序和前端资源，合并配置后以一个已授权应用账号验证登录、标注读取及退出；真实保存仅在明确授权的测试记录上执行并独立读回。不要仅凭构建或模拟验收判断生产已完成。

认证专项入口（先构建对应本机测试项目）：
```sh
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/osx-arm64/IISManager.Transport.SelfTests.dll --user-auth-only /Users/pengwei/MKY/IISManagerTool
```

最终代码的本机合成预览保留在 `http://127.0.0.1:63617/login.html`，PID 69354。启动入口是测试项目的 `--user-auth-preview`，仅绑定回环地址，认证和站点标注都在进程内存中，所有上游请求由内存 Handler 处理，不访问真实平台或 IIS。停止该进程会清除合成会话及标注；先前的合成会话及标注、比较服务与失败启动进程已清理。

