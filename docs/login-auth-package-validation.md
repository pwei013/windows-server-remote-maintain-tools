# 登录认证 Windows x64 增量包验证记录

2026-10-11。交付目录：[未压缩增量包](../outputs/MKY服务器远程管理工具-登录认证-20261011-win-x64/README.md)，人工步骤见[部署说明](../outputs/MKY服务器远程管理工具-登录认证-20261011-win-x64/部署说明.md)。仅 Console，未包含 Agent、安装器、配置保护工具或包校验脚本，未执行部署。

使用项目现有 `scripts/publish-installer-component.py` 调用部署包 Skill 的 SDK publisher，目标 `Windows Server / win-x64 / net10.0 / Release / SelfContained=true`，SDK 10.0.401。已有匹配的恢复输入未变，发布使用 `--no-restore`，零警告、零错误。发布继承 Microsoft.NETCore.App 与 Microsoft.AspNetCore.App 10.0.12，deps 目标为 `.NETCoreApp,Version=v10.0/win-x64`。

基线为 `outputs/MKY服务器远程管理工具-Console公钥Base64-20261011-win-x64` 的包根及完整运行清单（408 个文件），由其 2 个覆盖文件与 406 个继承配套文件组成。读取旧基线时按 Skill 的既有行为排除 `.DS_Store`；其余列入清单的包文件及运行指纹已核对，未修改旧包。与此前 1.0.3.0 站点启停修复版 Console 比较，除 Console DLL/PDB 外的配套文件一致。本地基线不能证明服务器实际安装版本。

本次 SDK 发布含 437 个运行文件，全部逐文件对照增量清单：50 个变化文件进入 `overlay/`，387 个未变化文件记录在 `REQUIRED-RUNTIME-FILES.json`，没有依赖删除或运行时升级。覆盖文件包含 Console DLL/PDB、静态端点清单、登录和工作台页面/脚本/样式、Logo/插图/图标，以及 SDK 生成的 `.br`、`.gz` 变体。

17 个原生文件及入口的 PE 架构为 x64；Console、Core、Contracts 的 DLL/PDB 配对通过，Core/Contracts 与基线相同。11 项主项目发布输入及 32 项源静态资源在发布前后、打包后未漂移。完整发布证据保存在随包 `BUILD-VERIFICATION.json`，未携带真实配置、账号、密码、用户 Token、公钥/调用标识或证书私钥。配置模板连接字段与证书指纹均为空，供人工保留或填写有效现场值，不能整文件覆盖。

所有说明、模板及 6 个许可证文件完成后冻结包，最后生成 `SHA256SUMS.txt`。独立运行 Skill 验证器并再次使用标准库读回全部包文件：清单覆盖 62 个文件（另有清单本身，共 63 个文件），共 2,206,286 字节；缺失文件 0，多余文件 0，哈希不符文件 0，无符号链接、路径大小写冲突或 macOS 元数据。包内没有安装器、执行脚本或受保护配置文件。校验后不再修改包内文件；一次性发布暂存目录随后清理。

沿用实现阶段 171 项定向检查和本机合成浏览器验收；打包没有改变业务输入，未重复业务测试。尚未验证真实账号、SQL Server/平台数据、Windows 证书加载、实际服务身份或目标 Windows 运行环境；未安装证书、修改权限、配置服务器或启停任何现场服务。文件传输与覆盖后的正确性由人工操作员保证，现场基线不符时需先准备匹配的完整基线。
