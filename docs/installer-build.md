# 安装器开发机复建

此文仅针对源码构建。服务器操作员使用已交付的 EXE，步骤见 [安装说明](installer-guide.md)。实际安装不由开发机脚本执行。

安装器使用 `IISManager.Setup.slnx`，Windows 向导目标为 `net10.0-windows`；共享校验组件为 `net10.0`。内置服务为 `net10.0` / `win-x64` / Release / 自包含，EXE 为 Windows x64 自包含单文件；不裁剪 WinForms 或反射所需代码。

使用 `global.json` 对应 SDK。本机产物和 Windows 发布输出分开；不得把 macOS 本地验证输出交给 Windows 服务器。构建脚本复用本机 `create-dotnet-deployment-package` Skill 的 SDK publisher/依赖核验模块，要求该 Skill 已存在。项目内没有额外复制一套发布器。

1. 确认 NuGet 源及网络环境，先为 Agent、Console 恢复匹配的 `win-x64` / `SelfContained=true` 输入。本版使用独立目录 `work/installer-payload-20261004-intranet/`；其中 `Agent.zip` 和 `Console.zip` 必须不存在，脚本拒绝覆盖已有载荷。旧版 `work/installer-payload/`、`work/installer-payload-20261004/` 和已交付包保留。
2. 使用下面命令生成载荷，`--stage` 是尚未存在的本任务暂存目录，`--legacy-package` 是保留原结构和清单的首次手工部署包。历史包只有运行文件作为升级基线，现场配置不会被嵌入。`--component Agent` 或 `--component Console` 可单独补建尚不存在的组件。

```sh
python3 scripts/build-installer-payloads.py --dotnet dotnet --stage work/installer-stage-20261004-intranet-new --payload-directory work/installer-payload-20261004-intranet --legacy-package outputs/IISManagerTool-20261003-win-x64
```

两个 ZIP 的 `payload-manifest.json` 覆盖完整自包含文件集合、字节数、SHA256 和已知旧版运行文件。运行文件使用 SDK publish 输出；Console `wwwroot` 由 SDK 发布。新安装默认 Console 和 Agent 的数组基线置空，由 Local 配置定义实际 Host 及自动内网范围，避免 JSON provider 按索引合并后保留开发模板尾项。新安装计划默认 `TransportMode=HttpToken`、`SourceAccessMode=PrivateNetwork`，无需用户填写来源 IP/CIDR；默认固定范围由同一程序规则生成。令牌由服务器安装向导运行时生成，不把真实令牌、现场配置或证书嵌入载荷。更新保留现场配置、认证模式、来源模式与原令牌；旧配置缺少字段时分别按 `Https`、`Allowlist` 兼容，不自动放宽已安装服务。

3. 恢复、构建并运行安装边界自测，覆盖 HTTP/HTTPS 配置分支、自动内网/精确白名单来源模式、令牌规则和更新兼容。可选载荷参数还会使用安装器的真实 `PayloadArchive` 对两个包完整解压及逐文件核验，结束清理测试目录。

```sh
dotnet restore tests/IISManager.Setup.SelfTests/IISManager.Setup.SelfTests.csproj
dotnet build tests/IISManager.Setup.SelfTests/IISManager.Setup.SelfTests.csproj -c Release --no-restore
dotnet run --project tests/IISManager.Setup.SelfTests/IISManager.Setup.SelfTests.csproj -c Release --no-build --no-restore -- --payload-directory work/installer-payload-20261004-intranet
```

4. 为图形安装器恢复和发布相同目标输入，发布目录位于本任务暂存区。`EnableWindowsTargeting` 已在项目中启用，允许开发机交叉编译；无法在 macOS 执行 WinForms 或 Windows 安装后端。

```sh
dotnet restore src/IISManager.Setup/IISManager.Setup.csproj -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:InstallerPayloadDirectory=/Users/pengwei/MKY/IISManagerTool/work/installer-payload-20261004-intranet
dotnet publish src/IISManager.Setup/IISManager.Setup.csproj -c Release -r win-x64 --self-contained true --no-restore -p:InstallerPayloadDirectory=/Users/pengwei/MKY/IISManagerTool/work/installer-payload-20261004-intranet -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o work/installer-exe-stage-20261004-intranet-new
```

5. 使用项目的只读检查脚本确认 EXE 为 x64 PE、UAC 要求管理员、bundle 包含托管入口、共享组件、WindowsDesktop/NETCore 运行时及两个内嵌 ZIP；脚本不执行 Windows 程序。输出须使用尚不存在的任务文件。Windows `singlefilehost` 静态链接原生运行时，不能要求它像服务文件夹发布一样另列 `coreclr.dll`。本机已恢复的 host 路径如下，其他开发机须按实际 NuGet 缓存位置填写：

```sh
python3 scripts/verify-installer-bundle.py --installer work/installer-exe-stage-new/IISManager.Setup.exe --assembly-output work/installer-published-assembly.dll --evidence-output work/installer-artifact.json --host-path work/nuget-packages/microsoft.netcore.app.host.win-x64/10.0.12/runtimes/win-x64/native/singlefilehost.exe
```

6. 通过 PE 元数据独立核对提取出来的真实安装程序集与两个已验证载荷：

```sh
dotnet run --project tests/IISManager.Setup.SelfTests/IISManager.Setup.SelfTests.csproj -c Release --no-build --no-restore -- --payload-directory work/installer-payload-20261004-intranet --installer-assembly work/installer-published-assembly.dll
```

以上 `InstallerPayloadDirectory` 是本次项目绝对路径，其他开发机需替换为其真实项目根目录，不能把它当作服务器安装路径。直接执行安装器 `dotnet build` 时也须传相同属性。交付 EXE、操作员说明、验证记录及依赖许可。全部交付内容冻结后生成 SHA256 清单并独立读回验证。检查用证据保存在交付目录之外，包内不交付校验脚本。

自包含运行时也需要维护，后续安全更新须用匹配的新 SDK/运行包重新发布，并在 Windows 测试环境验收。复建、编译和安全自测不替代 Windows 新装、更新、SCM、ACL、防火墙及真实 IIS 验收；自动内网模式须验收真实私有/VPN 来源放行、公网来源拒绝和令牌，旧精确来源模式须保持其原限制；HTTPS 模式另验收 mTLS。
