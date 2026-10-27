# 首版部署包验证记录

目标：Windows Server x64，`net10.0`、`win-x64`、`Release`、框架依赖模式。无已核验的服务器基线，因此交付两端完整应用运行文件集合，另附首装安全配置模板。

## 本次发布与静态检查

- 使用 SDK 10.0.401，分别执行匹配目标的 `dotnet publish`，明确 `--self-contained false --no-restore`。两端发布均成功，未出现编译警告或错误。
- Agent 共 16 个运行文件，Console 共 24 个运行文件；Console 含 3 个页面源码文件及 SDK 生成的 6 个 Brotli/Gzip 文件。
- 两个 `.deps.json` 的运行目标均为 `.NETCoreApp,Version=v10.0/win-x64`，对应必需的托管依赖、资源与入口均在包内。
- 两个 apphost 的 PE Machine 均为 `0x8664`（x64）；两组件各自的入口、Contracts、Core 共 6 组 DLL/PDB 的 CodeView 与 Portable PDB 标识配对通过。
- `.runtimeconfig.json` 为框架依赖配置，需要服务器具备 x64 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 的 10.0.x 运行时；最低版本声明为 10.0.0。
- 业务源码、项目、共享 Core/Contracts、锁文件和页面输入的 34 项指纹在发布期间保持一致。前轮已通过的 23 项自测、本机 HTTP 模拟和浏览器验证被复用，没有因打包重跑相同用例；详见 [开发验证记录](validation.md)。
- 仅将运行文件纳入各组件 `overlay/`；生产默认配置、日志规则和明确占位的 Local 配置示例放在 `config-templates/`，供操作员新装或人工合并使用。未包含开发配置、真实 Local 配置、账本、日志、证书、私钥或配置保护工具。
- 全部程序、说明、模板和证据冻结后生成 SHA256 清单，最终独立读回核对精确文件集合、逐文件哈希及组件运行清单。未交付校验脚本；传输和部署后的文件正确性由人工操作员保证。

Console 的静态资源发布过滤首次存在 MSBuild 条件语法错误（MSB4092）；定位并修正临时打包辅助条件后重新发布成功，失败输出未用于交付。

## 未执行

本次在 macOS arm64 交叉发布，没有执行 Windows 服务安装或启动、真实 IIS 操作、目录 ACL/实际服务身份、原生 HTTP.sys/TCP 检查、证书私钥使用、mTLS 或业务访问验收，也没有执行生产复制、部署、证书安装或权限调整。IIS/WAS 原生管理组件由目标操作系统提供，不能通过复制其他机器的 DLL 补齐。

请按 [人工部署说明](deployment-guide.md) 和 [Windows 运行与验收](windows-operations.md) 在测试服务器完成人工验收。
