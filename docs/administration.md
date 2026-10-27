# Console 接入与权限管理

`/servers.html` 提供服务器新增、编辑、连接测试，以及本地用户授权。只有 Console 管理员可读取接入清单、授权名单或提交变更；普通用户可通过 `/api/v1/permissions` 查看自己的有效权限。仍使用现有登录会话、来源限制和 POST 的 `X-IISManager-CSRF` 校验。

## 首次启用

未启用严格授权时，已登录用户继续沿用原有服务器查看、操作与配置权限；新增的接入和授权设置只开放给管理员。首次管理员由操作员在 **Console 自己的配置** 中登记已知身份：

```json
"Administration": {
  "BootstrapAdministrators": [
    { "AppId": 1234, "UserId": "KNOWN_USER_ID" }
  ],
  "AllowDevelopmentPlaintext": false
}
```

上面是 `Console` 节点内需要合并的片段，`1234` 和 `KNOWN_USER_ID` 都是占位值，应填写当前登录用户实际的 AppId、UserId。页面会显示当前用户身份，也可由已认证的 `/api/v1/session` 读取。不需要联查或修改零代码平台用户目录。配置来源继续遵循现有 .NET 启动配置顺序；不要整份替换现场 appsettings。

启动时先读取原有配置，再加载受保护的独立管理状态。独立状态中相同 ID 的接入覆盖启动配置中的接入，新 ID 追加，其他原有接入保留。管理状态首次保存后，其中的管理员名单成为权威来源，之后更改 `BootstrapAdministrators` 不会覆盖已保存名单。新增接入不改写 appsettings，也不改变原连接配置只在启动时生效的策略。

## 保护与存储

Windows 使用 DPAPI **CurrentUser** 保护完整管理状态（包含连接 Token 和授权策略），由实际 Console 服务身份进行保护和读取；不使用机器范围的共享解密权限。另以 Console 内容根目录的实例摘要绑定保护用途。默认目录位于站点外：

```text
%ProgramData%\MKY\IISManagerTool\Console\<实例摘要>\administration\administration-state.json
```

可通过 `Console:Administration:DataDirectory` 指定其他站点外绝对路径。生产拒绝位于 Console 内容根目录内的路径，以及经过符号链接或重解析目录的路径。新建管理目录会限制给当前服务 SID、SYSTEM 和本机 Administrators；已有目录只检查所有者和 DACL，不自动修改。原文件替换前保存并应用 Owner、Group、DACL，替换后独立核对实际内容和权限；未检查 SACL，不声明恢复审计权限。

实际服务身份需能访问该目录，并具有可工作的 DPAPI 用户配置文件。操作员如需预建目录，应按实际身份设置受限权限；不要授权 Everyone/Users/Authenticated Users，也不要因保存失败关闭安全检查。迁移服务账号、机器或 Console 根路径前保留受限备份并制定迁移方式，原加密文件不能视为可直接跨身份或跨实例复制的配置。程序不会自动迁移密钥、重置权限或重置授权。

非 Windows 环境没有默认生产保护器，页面仍可只读查看和测试，但拒绝保存。仅同时满足 `Development`、监听地址为 loopback、显式 `AllowDevelopmentPlaintext=true` 时允许开发明文；生产或非 loopback 即使误配该开关也拒绝启动。开发文件中的 Base64 只用于封装，不构成加密。

状态文件、锁文件、受限备份及凭据均属于运行配置，不加入部署包或站点覆盖目录。没有安全存储、无法解密、权限不合格或读取损坏状态时，不回退生产明文。已存在状态无法核对会停止初始化，避免严格授权被静默重置为兼容模式。

## 接入编辑

1. 在页面新增或编辑接入；已有 ID 不可修改，也没有删除接入操作，以保留任务与历史归属。
2. 地址只允许 HTTP/HTTPS 根地址，不含账号、密码、子路径、查询参数或片段。HTTP Token 和 HTTPS 双向证书继续服从当前 Console 传输策略；客户端证书仍沿用启动配置。
3. 编辑同一 HTTP 目标时 Token 留空保留原值，页面从不回填或响应实际 Token。HTTP 目标的协议、主机或端口改变时，必须显式重新输入目标 Token，测试和保存都不会自动向新地址转发旧凭据。
4. “测试连接”仅通过现有 AgentClient 请求该草稿目标的 `/info`，不会发业务查询或写请求，也不保存草稿。保留正常 TLS 验证和证书检查，不跟随重定向，最多两路测试、单次至多十秒。
5. 先预览非秘密字段差异，再明确确认保存。预览指纹绑定当前 revision、操作者和整份草稿；修改任意字段后必须重做预览。保存会保护、写入并独立读回核对。
6. 保存后提示待重启，当前 Agent 连接继续使用启动时配置。人工核对待处理任务并重启 **Console** 后生效；此操作不要求重启 Agent。

## 用户与动作授权

策略按已认证用户的 **AppId + UserId** 精确匹配，服务器 ID 保持精确匹配。管理员拥有所有服务器权限，并能管理接入和授权。每次保存策略都必须保留当前操作者的管理员身份，严格模式至少保留一位管理员。

| 权限 | 作用 |
| --- | --- |
| `read` | 查看授权服务器及其相关总览、健康和历史数据 |
| `operate` | 对授权服务器执行启停、重启、回收及主动健康检查 |
| `configure` | 对授权服务器执行配置与维护变更 |

`operate` 与 `configure` 分开授权，均要求同时具有 `read`。严格授权关闭时维持旧用户的服务器操作能力；开启时未匹配的服务器和动作默认拒绝。策略保存也需要 revision、预览和明确确认，持久化核对成功后立即生效，无需重启；不会向远程用户目录写授权。授权名单允许手填已知 UserId，不创建身份。

页面隐藏按钮只是使用提示，实际由后台鉴权。服务器读取、操作 validate/submit、配置维护、健康检查均按目标服务器和动作校验；全局清单、总览、健康与历史需过滤实际返回数据及计数。历史中旧服务器对受限用户不可见，管理员保留跨历史服务器查询能力。

## HTTP 契约

所有接口继续受原认证中间件保护，匿名请求返回原有 401，已认证但无管理权限返回 403。管理 POST 使用现有会话 CSRF。服务端只输出白名单错误码和安全提示，不输出异常正文或秘密。

| 方法与路径 | 行为 |
| --- | --- |
| `GET /api/v1/permissions` | 仅当前用户的 `administration` 和 `servers[{serverId,read,operate,configure}]`，附 `strictMode`；不含其他用户 |
| `GET /api/v1/administration/servers` | 安全接入视图、revision、待重启和保护状态；Token 仅有 `hasAccessToken` |
| `POST /api/v1/administration/servers/preview` | 校验接入草稿，返回安全差异和 fingerprint，无持久化或网络请求 |
| `POST /api/v1/administration/servers/test` | 校验草稿并只读请求 `/info`，不持久化 |
| `POST /api/v1/administration/servers/save` | 要求 revision、expectedFingerprint 及相同草稿；保护保存，待人工重启生效 |
| `GET /api/v1/administration/policy` | 管理员读取独立策略及 revision |
| `POST /api/v1/administration/policy/preview` | 接收 `{revision,policy}`，预览身份、范围和动作差异 |
| `POST /api/v1/administration/policy/save` | 接收相同草稿和 expectedFingerprint，核对后立即生效 |

接入草稿字段：`revision,id,name,group,environment,agentUrl,accessToken,serverCertificateThumbprint`。保存额外包含 `expectedFingerprint`。授权策略字段：`enabled,administrators[{appId,userId}],grants[{appId,userId,serverIds,read,operate,configure}]`。返回的预览不能当作新 revision 使用。

`ADMINISTRATION_CONFLICT` / `ADMINISTRATION_PREVIEW_REQUIRED` 为 409，需要重新读取并预览。权限判断所需 JSON 字段遵循 Web 大小写规则；重复字段（含大小写变体）拒绝为 `AUTHORIZATION_BODY_INVALID` 400，不能用重复字段使权限判断和业务绑定产生分歧。JSON 语法或字段类型无效同为安全 400，超 64KiB 为 `AUTHORIZATION_BODY_TOO_LARGE` 413。

`SERVER_CREDENTIAL_REENTRY_REQUIRED` 为 400；`ADMIN_CONNECTION_BUSY` 为 429。安全存储不可用、漂移或写入结果不能确认均返回安全 503。文件出现外部漂移后停止相关写入和后续权限使用，不覆盖外部文件。保存回执丢失、5xx、版本冲突或读回失败时，页面锁定提交，不自动重放；先重新读取核对。存储损坏或未知结果需操作员只读核对实际文件、权限、服务身份，再决定恢复或重启，不能通过删文件重置权限来宣告成功。

## 集成与验证边界

`AdministrationBootstrap.Load` 在启动配置校验及 AgentClient 构造前加载；`Register` 注册 store、权限服务、测试器与管理服务；`AdministrationEndpoints.MapEndpoints` 接管理路由。原认证和 CSRF 成功后调用 `ConsoleAccessService.AuthorizeRequestAsync`，统一将 `AdministrationException` 映射其安全 Code/SafeMessage/StatusCode。全局列表可使用 `AllowedServerIds`，返回 null 表示不限制；显式目标读写可调用 `Require`。新操作类型默认按 configure 处理，已有启停类型按 operate 处理。

专项 C# 入口为 Transport.SelfTests 的 `--administration-only`；Node 页面测试命令为：

```text
node --test tests/IISManager.Console.Administration.SelfTests.cjs
```

专项测试只使用合成身份、合成凭据、注入的跨平台保护器和临时状态，不连接真实平台或服务器。Node 覆盖预览/确认、CSRF、秘密不回填、无权限不取管理信息、回执未知不重放、撤权和安全文本展示。Windows DPAPI、实际服务身份配置文件、目录 ACL 读写和目标 Windows 环境仍需现场验证；跨平台测试通过不代表 Windows 实机验收通过。
