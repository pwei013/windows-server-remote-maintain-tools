# 登录认证 HTTP 兼容修订验证记录

2026-10-11。按用户要求解除默认仅 HTTPS 登录限制，新增 `UserAuthentication.RequireHttps=false` 配置；BrowserHttps 显式 true/false 可独立选择 Console 入口协议，未配置时保留原 TransportMode 行为。允许 HTTP 时 HTTPS 响应清除旧 HSTS；HTTP Cookie 保留 HttpOnly/SameSiteStrict，HTTPS Cookie 仍带 Secure。生产认证、来源网络、Host、CSRF、Agent 和业务 OpenAPI 验证均保留。

认证专项 21/21 通过，包含 Production HTTP 登录、bootstrap 的 loginRequiresHttps=false、不透明会话、退出/失效、显式 HTTPS-only 策略，以及浏览器 HTTP 与 Agent HTTPS/mTLS 独立配置。传输专项 121/121 通过，使用本轮重新构建的程序完成 HTTP 本机流程；原 6 项仍预期匿名工作台访问的用例已改为验证应用用户认证拦截。传输测试最初缺少所需 dotnet 路径，按测试入口传入已确认路径后完成。测试均为本机合成数据，子进程及临时目录由测试清理，未访问真实平台或写入服务器。

项目现有 publisher 完成 Windows Server x64 / win-x64 / net10.0 / Release / SelfContained=true SDK publish，SDK 10.0.401，零警告、零错误。已有匹配恢复输入未变化，沿用 no-restore。运行时仍为 Microsoft.NETCore.App 与 Microsoft.AspNetCore.App 10.0.12。11 项主项目源码输入与 32 项静态源资源在发布及组包后未漂移；17 项原生/入口 PE 为 x64，Console、Core、Contracts DLL/PDB 配对通过。

[修订包](../outputs/MKY服务器远程管理工具-登录认证-HTTP兼容-20261011-win-x64/README.md)继续以 Console 公钥 Base64 包根为基线，累计提供 50 个 overlay 文件，387 个配套文件要求现场匹配，完整集合 437 个；与上一登录认证包相比，仅 Console DLL/PDB 变化，其余 435 个运行文件一致。SDK 生成的压缩变体均保留，不更新 Agent 或安装器。

说明、HTTP 配置模板、许可证完成后冻结包，最后生成 SHA256；Skill 校验器与标准库独立读回均通过。62 项校验对应全部 63 个包文件（另含校验清单本身），缺失、多余及哈希不符均为 0，无大小写冲突、符号链接、macOS 元数据、校验脚本或真实配置秘密。一次性发布暂存目录清理；以前的本机合成预览保留。

Windows 证书导入和服务私钥读取步骤依据微软官方文档与实际 MachineCertificates.Load 实现编写，包含本机个人存储、私钥、Server Authentication EKU、SAN/信任链、实际服务身份、指纹及程序监听配置。未在 Windows PowerShell 5.1 或目标 Windows 服务执行；未修改现场配置、证书、权限或启停服务，未部署。HTTP 没有浏览器到 Console 的 TLS 加密，场景按用户要求限于既有受控管理入口；HTTPS 可按说明人工启用。
