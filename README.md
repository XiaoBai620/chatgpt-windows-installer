# ChatGPT Windows 安装助手

## 下载最新桌面版

当前正式版：**1.0.6**。点击下面的链接直接下载一个 EXE，无需先进入 GitHub 发布页：

- **[Windows x64 下载（常见 Intel / AMD 电脑）](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest/download/ChatGPT-PackagePicker-win-x64.exe)**
- **[Windows Arm64 下载](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest/download/ChatGPT-PackagePicker-win-arm64.exe)**
- [发布说明](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest)

下载的是本项目的**安装助手**，ChatGPT 官方离线包约 1 GB，需要在助手内另外下载。助手需要对应架构的 **.NET 10 Desktop Runtime**；完整运行条件和安装方式见下文。下载链接始终指向标记为 Latest 的正式版，发布新版后文档链接保持不变。

在使用文档中可以直接复制：

```markdown
[下载 ChatGPT Windows 安装助手（x64）](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest/download/ChatGPT-PackagePicker-win-x64.exe)
[下载 ChatGPT Windows 安装助手（Arm64）](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest/download/ChatGPT-PackagePicker-win-arm64.exe)
```

## 运行条件

双击 `dist/win-x64/ChatGPT-PackagePicker.exe`；Arm64 使用 `dist/win-arm64/` 中的同名文件。这是轻量单文件 EXE，不携带 .NET。首次使用需要与 EXE 架构一致的 **.NET 10 Desktop Runtime（Windows 桌面运行时）**，不是 SDK 或 ASP.NET Core Runtime。缺少运行时时，Windows 图形宿主会提示下载。也可从 [Microsoft 官方下载页](https://dotnet.microsoft.com/download/dotnet/10.0) 选择 Desktop Runtime 的 Windows x64 或 Arm64，安装后重新打开助手。完全离线的电脑请提前准备对应的运行时安装程序。

## 使用

首页默认建议 Microsoft Store。商店不可用时，可从备用入口直接下载 OpenAI 当前发布的 MSIX 和许可证。完全断网时，在联网电脑准备适合目标架构的包和 XML，复制到目标电脑本地固定磁盘，再添加目录。[官方部署文档](https://learn.chatgpt.com/docs/enterprise/windows-deployment)。

包列表扫描下载文件夹下 `ChatGPT-offline` 和当前会话添加目录的第一层 `.msix`，按清单中的数值版本排序；默认选中架构、系统要求、包身份、依赖和本地签名验证均通过的最高版本。本地最新不等于官方最新。

### 下载速度与网络状态

下载页面显示进度、实时速度、预计剩余时间和网络等待状态；即使暂时收不到数据，也会持续更新状态。默认开启「多连接加速」，官方源支持时最多使用 4 个连接；不支持时改用单连接，也可以手动关闭。多连接可改善部分单连接限速，无法突破云电脑出口或下载源的总带宽限制。

下载停滞时会在超时后自动重连；只有官方源提供可验证的相同文件标识与分段响应时，才接着本次已收到的数据下载，避免把两个版本拼接。取消下载会清理未完成的数据；关闭助手后再次下载会重新开始。文件完整下载后仍须通过包身份、兼容性和 Windows 签名检查。

### 历史版本

OpenAI 的[官方部署文档](https://learn.chatgpt.com/docs/enterprise/windows-deployment)明确说明，公开 MSIX 链接下载每个架构的当前版本，不能用来指定历史版本。本助手没有官方在线历史下载列表。

「本地与历史版本」会保留并列出已下载的不同版本；已有旧版时，点击「导入历史版本…」添加其所在目录，保留对应 XML 许可证。助手不会自动覆盖其他版本，部署已预配的更高版本时仍遵守 Windows 的版本与管理策略。

下载不会安装。部署时优先读取包旁同名 XML，也可选择许可证。程序检查 XML 格式，许可有效性由 Windows 判定。用户确认部署后，必要时请求管理员权限；系统预配清单不可读则停止，已预配同版或更高版则跳过。回查通过仅表示系统预配，不代表当前账户已注册、可启动或已登录。不会自动重启系统、注销或绕过管理策略。

## 更新安装助手（不是更新 ChatGPT）

首页在后台检查一次（8 秒超时），不会自动下载。进入「更新详情…」并检查版本后，可选择：

- **更新并重启**：确认版本和当前 EXE 路径后，从[固定 GitHub 仓库](https://github.com/XiaoBai620/chatgpt-windows-installer/releases)下载并核对 SHA-256。旧助手退出后，由同一普通用户运行的本地辅助进程在同卷用 Windows `File.Replace` 替换文件并保留原件；新版首页显示后才确认成功。新版未能启动或未在 60 秒内显示首页时，尝试原子恢复并重新打开旧版。仅重启助手，不影响 ChatGPT 或系统。
- **仅保存新版**：选择空目录并确认，校验通过才保存，不覆盖、不启动。关闭旧助手后手动替换。管理员启动、改名、非单文件、网络磁盘、符号链接或无写权限等情况下请使用此路径。不要为了更新而提权。直接打开发布页并由自己核实文件也可；这不等于助手已验证该下载。

只在自己控制的本地目录内使用自动更新。辅助进程不接受目标路径、下载 URL 或命令；它检查启动进程、同字节副本、固定文件名、同目录随机事务、版本与架构，并复核文件摘要。标准名为 `ChatGPT-PackagePicker.exe` 或本架构的发行附件名。若断电、文件锁或系统策略导致无法自动恢复，先关闭相关进程，检查 EXE 同级 `.picker-update-<事务ID>/previous.exe`。此文件是该事务替换前的备份；确认无误后可手动恢复。不要运行来源不明的 helper 或备份。

**信任边界**：不使用项目密钥、`.sig` 或独立发布者签名。信任 HTTPS、GitHub Release API、该仓库的发布权限与其返回的附件 `digest`。只接受精确附件名、固定仓库/标签的下载地址、有效大小、`sha256:` 加 64 位十六进制摘要，以及 GitHub 发行附件 CDN 的 HTTPS 重定向。缺失、重复、不匹配或内容被篡改一律拒绝，不能静默回退到低版本。摘要不能防止发布账号/GitHub 同时修改附件及摘要，也不防御可任意写本地助手目录或控制当前账户的攻击者。助手的更新规则不改变 OpenAI MSIX 的 Windows 签名校验。

### 渠道与发布约定

- 正式桌面版发布时显式设为 GitHub 的 Latest（`gh release create ... --latest` 或发布页的「Set as latest release」），并保留下面的固定附件名，以保证文档中的 `releases/latest/download/` 直链始终可用。不要依赖历史脚本标签与桌面标签之间的版本自动排序。
- 稳定构建只选非草稿、非预发布的 `desktop-v主.次.补丁`。候选构建还选标为预发布的 `desktop-v主.次.补丁-rcN`（如 `desktop-v1.0.2-rc1`）。先按三段数值版本，再按候选号排序，同基版稳定版高于所有 RC。只向前更新；`v1.1.0` 等历史脚本标签不参与比较。
- 在 `desktop/PackagePicker.csproj` 指定 `Version`（例如 `1.0.4` 或 `1.0.5-rc1`），匹配 Release 标签及预发布标记。上传两个不含运行时的单文件 EXE，分别命名 `ChatGPT-PackagePicker-win-x64.exe` 与 `ChatGPT-PackagePicker-win-arm64.exe`。由 GitHub 为每个附件生成摘要；不上传签名、密钥或自制校验清单。未来构建请保留启动确认协议，否则旧助手会回滚。
- 已经发布的旧版不会因为新代码而获得新能力。不能自更新的旧版，首次升级需手动下载。

## 项目与验收

`desktop/Program.cs` 是 Picker 界面与入口；`Home.cs` 是首页。`Catalog.cs` / `Trust.cs` 检查本地包。`Acquisition.cs` 限定官方来源并管理下载。`Deployment.cs` 实现固定的 DISM 部署流程（和不提权的助手自更新相互独立）。`AssistantUpdates.cs`、`AssistantUpdateInstall.cs` 与 `AssistantUpdateWindow.cs` 分别负责发布记录/下载、替换恢复、用户确认。

使用 Windows .NET 10 SDK：`dotnet publish desktop/PackagePicker.csproj -c Release -r win-x64 --self-contained false -o dist/win-x64`。Arm64 将命令中的两个 `win-x64` 改为 `win-arm64`。只交付 `dist/` 的两个 EXE，不交付 `desktop/bin`、`desktop/obj` 或测试夹具。

- `--test` 运行包目录、下载、部署、自更新及下载页面回归；网络测试使用模拟 HTTP 和断流，不下载真实大包。下载页面测试在后台测量控件，可通过 `PICKER_TEST_SCREENSHOTS` 指定截图目录，不显示窗口。更新测试验证渠道、精确附件绑定、摘要、篡改、重定向和不覆盖。`--smoke` 与 `--home-smoke` 检查 WPF 控件与页面入口。
- `--verify-download <临时目录>` 从官方来源下载约 1 GB 的 MSIX/许可证而不部署；`--verify-package <MSIX绝对路径>` 检查目录扫描和自动选择。
- 在普通权限 x64 PowerShell 中运行 `./tests/AssistantUpdate-E2E.ps1` 可重现跨进程替换和回滚：测试在新建的本地临时目录使用同项目构建的旧版夹具与新版 EXE，核对新进程窗口和文件哈希，不从网上获取可执行文件；只有测试构建带本地夹具入口。真实 UAC/DISM 写入、干净 Windows 环境及 Arm64 原生运行仍需独立验收。

2026-11-19 本机 x64 实测：临时目录的 `1.0.3-rc1` 退出后原位替换为 `1.0.4` 并显示新版首页；替换后的 SHA-256 与交付的 x64 EXE 一致。另一次故意令新版在显示首页前退出，已恢复旧文件的原始 SHA-256 并重新打开旧版首页。事务以中完整性、非管理员令牌运行，未替换用户安装。四组自测及两个窗口 smoke 通过；另实际下载并选中官方 x64 MSIX `26.1111.2228.0`（1,057,802,768 字节），未执行部署。
