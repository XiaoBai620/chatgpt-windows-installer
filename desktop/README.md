# ChatGPT Windows 安装助手

双击 `dist/win-x64/ChatGPT-PackagePicker.exe`。这是 C# / WPF 自包含的 x64 单文件测试版，不依赖项目源码、PowerShell 或预装 .NET。真实部署仍待独立 Windows 测试机验收。

## 使用

默认建议 Microsoft Store。商店不可用时，可以直接下载 OpenAI 当前发布的 MSIX 和许可证，不必等待商店报错。完全断网时，在联网电脑准备适合目标架构的包和 XML，复制到目标电脑本地固定磁盘，再添加目录。应用内下载按执行它的电脑架构选择；x64 EXE 已运行测试；`dist/win-arm64/` 为交叉构建的候选件，仅核验了 PE 架构，未做 Arm64 原生运行验收。[官方部署文档](https://learn.chatgpt.com/docs/enterprise/windows-deployment)。

包列表包含下载文件夹下 `ChatGPT-offline` 以及当前会话所加目录第一层的 `.msix`。按清单中的数值版本排序，默认选中架构、系统要求、包身份、依赖与本地签名验证均通过的最高版本。文件无法使用时显示原因。本地最新不等于官方最新；不承诺完整历史档案。

下载不会安装。部署时优先读取包旁同名 XML，也可手动选择许可证。程序检查 XML 格式，许可有效性由 Windows 判定。用户明确确认后才启动部署，必要时请求管理员权限；系统预配清单不可读则停止，已预配同版或更高版则跳过。回查通过仅表示系统预配，不代表当前账户已注册、能启动或已登录。不会自动重启、注销或绕过管理策略。

## 安装助手自身更新（与 ChatGPT 更新无关）

双击先进入账户状态首页，商店为默认路径，离线包列表仅在点击备用入口后打开。启动后在后台自动检查一次助手版本（8 秒超时，不阻塞首页），也可通过「助手更新详情」手动重查。版本来源为 [固定 GitHub 仓库的 Releases](https://github.com/XiaoBai620/chatgpt-windows-installer/releases)。只识别非草稿、非预发布的 `desktop-v主.次.补丁` 标签；历史 `v1.1.0` 是脚本版本，不与助手版本或 OpenAI 的 MSIX 版本比较。联网失败不是“已是最新”。

默认发布件**没有更新公钥，只能检查，不能下载**。有可信公钥且存在当前助手架构的新版本及签名时，用户可选择本地受自己控制的文件夹并再次确认下载。助手先写临时文件，验签成功才保存新的版本化 EXE；不会覆盖已有文件、启动下载内容、自动替换、请求提权或重启。退出旧助手后可手动替换。更新签名是本项目的独立签名，并非 Windows Authenticode 或 OpenAI 的包签名。

预发布候选件支持从 GitHub 手动下载，但不会作为稳定更新推送。`--test` 用内存测试密钥和模拟 HTTP 验证标签筛选、签名、篡改、拒绝无公钥下载与不可覆盖；`--smoke` 还在 WPF 更新窗口中模拟发现新版本，核验打开窗口不联网、点击才检查以及无公钥时下载按钮仍禁用。

维护者发布约定：

- 构建时通过 `-p:DesktopUpdatePublicKey=<Base64-SPKI>` 嵌入离线确认的 ECDSA P-256 公钥。仅提供公钥，不把私钥放入源码、构建参数或安装包；不能从 Release 或运行时配置导入信任。首次启用或信任根失效时，应通过独立可信渠道交付新构建。
- 设置 `desktop/PackagePicker.csproj` 中 `Version`，例如 `1.0.2` 对应 `desktop-v1.0.2`。为每个 RID 上传独立命名的 `ChatGPT-PackagePicker-win-x64.exe` 或 `ChatGPT-PackagePicker-win-arm64.exe` 及同名加 `.sig` 的签名附件。
- 对最终发布 EXE 的字节计算 SHA-256。签名输入依次是四行：`XiaoBai620/chatgpt-windows-installer`、精确标签、精确 EXE 附件名、**大写无分隔符**的 SHA-256 十六进制。每行（含末行）以 LF 结束，UTF-8 无 BOM；以 ECDSA/SHA-256 对这些输入字节签名，`.sig` 为 64 字节原始 IEEE P1363 `r || s`（非 DER、非 Base64）。远端附带的哈希或公钥不能替代此签名。

## 项目结构与规则

- `desktop/PackagePicker.csproj`：.NET 10 WPF 工程。`Program.cs` 负责界面、用户确认和命令入口。
- `desktop/Catalog.cs`、`Trust.cs`：清单筛选、版本推荐和 Windows 本地签名验证。
- `desktop/Acquisition.cs`：限定官方 HTTPS 来源、流式下载、取消、许可证格式和版本缓存。
- `desktop/Deployment.cs`：提权助手、固定的 DISM 操作、预配前后回查。
- `desktop/CatalogTests.cs` 及模块内自测：不依赖历史日志或外部测试脚本。
- `dist/win-x64/`：仅保留发布 EXE。构建生成的 `desktop/bin/`、`desktop/obj/` 不交付。根目录不堆放临时包、截图、旧脚本或日志。
- 修改后先构建并运行发布件自测；测试结论区分模拟、真实只读与真实部署。不在用户工作机上为验收而安装或降级应用。未决权限、发布或系统写入选择用弹窗确认。

## 构建与验收

在项目根目录使用 Windows .NET 10 SDK：`dotnet publish desktop/PackagePicker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist/win-x64`。

对发布 EXE 分别传 `--test`、`--smoke`，等待进程退出，退出码 0 表示通过。`--verify-download <临时目录>` 会实际下载约 1 GB 文件但不部署；`--verify-package <MSIX绝对路径>` 检查该目录里自动选中的文件是否为指定包。测试后仅清理自己创建的临时目录。

2026-11-19：实测下载、检查并选中官方 x64 `26.1111.2228.0`（1,057,802,768 字节，许可证 2,621 字节）。尚未验收新 EXE 在干净虚拟机上的实际 UAC、DISM 写入、重新登录和启动；助手自身更新仅支持用户确认后下载已验签文件并手动替换，Arm64 候选已构建（PE Machine `0xAA64`），尚未通过原生设备验收。
