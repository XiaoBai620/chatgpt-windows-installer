# ChatGPT Windows 离线下载与安装助手

[下载脚本 ZIP](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/download/v1.1.0/ChatGPT-Offline-Helper-v1.1.0.zip) · [发布页](https://github.com/XiaoBai620/chatgpt-windows-installer/releases/latest)

**优先用[官方 Windows 安装器](https://get.microsoft.com/installer/download/9PLM9XGG6VKS?cid=website_cta_psi)，或从 [Microsoft Store](https://apps.microsoft.com/detail/9plm9xgg6vks?hl=zh-CN&gl=CN) 安装。** 认准 ChatGPT，发布者 OpenAI；普通用户选正式版。

微软商店下载受网络环境影响；一直不开始或失败时，可以切换网络，或先开启你已有的代理 / 科学上网后重试。仍然下载不了，再用下面的备用脚本。

这是 ACE64 整理的社区辅助工具，不是 OpenAI 官方脚本。只从 OpenAI 官方 HTTPS 地址下载应用包和离线许可证；仓库不托管、修改或重新分发 ChatGPT 应用本体。

## 给普通用户

1. 下载上方脚本 ZIP，**先完整解压**。
2. 只想保存离线包：双击 **`ChatGPT-Download.bat`**。想下载并安装：双击 **`ChatGPT-Install.bat`**，安装时按系统提示批准管理员权限。
3. 下载包约 **1 GB**，需要联网和等待；慢网请耐心等最终结果，不要重复开多个安装窗口。默认存放在当前用户的 `下载\ChatGPT-offline`。
4. 显示“已核验 ChatGPT 预配成功”后，保存工作，注销 Windows 并重新登录，再打开 ChatGPT；若明确提示需要重启，则按提示重启。
5. 打开后从应用或 Microsoft Store 检查更新，商店有“更新”按钮时点击它。更新也需要可用网络。

**仅断开远程桌面连接不等于注销。** 工具不会自动注销、重启或关闭应用。下载完成与安装完成会分别显示；离线安装后使用应用仍然需要联网。

工具运行无需额外安装 Python、Node.js 或 PowerShell；使用 Windows 自带 JScript、curl、tar 和 DISM。Windows 11 优先；Windows 10 至少 19041，最终以实际下载包声明的最低系统版本为准。支持 x64 与 Arm64。

## 已下载的包怎么用

把完整 `ChatGPT-offline` 目录复制到目标电脑，把安装脚本也放到该电脑，运行：

```cmd
ChatGPT-Install.bat --cache "D:\ChatGPT-offline"
```

脚本核对包名、发布者、架构、系统要求与依赖。Windows 在实际部署时校验 MSIX 签名。只看清单不能证明签名有效；不要使用来源不明的安装包，不需要关闭 Windows 签名检查。

## 更新与其他选项

默认复用目录内已有的有效包。需要重新下载当前官方最新版时，使用 `--refresh`；这会重新下载约 1 GB，并保留原有完整包：

```cmd
ChatGPT-Download.bat --refresh
ChatGPT-Install.bat --refresh
```

也可使用 `ChatGPT-Install.bat --download-only` 仅下载。仅下载不要求管理员权限、不修改系统。

```cmd
ChatGPT-Install.bat --check
ChatGPT-Install.bat --self-test
ChatGPT-Install.bat --help
```

`--check` 仅查询本地工具/系统信息，`--self-test` 只做逻辑自测，都不下载或安装。自动化可加 `--no-pause`，避免结束后等待回车。

## 昨天的实际记录

2026-11-12 的单次 Windows 11 x64 记录：下载约 **1008 MiB**，约 **12 分 33 秒**；curl 最终平均约 **1372 KiB/s（1.34 MiB/s）**；预配部署约 **67 秒**，最终重新查询确认版本 `26.1106.6628.0`。当天日志未记录 curl 错误。主包下载约占全程 92%，瓶颈主要在下载等待。

这是一台机器的一次样本，不是速度承诺，也不能证明其他网络更快或更慢；包体积和版本会变化。原始路径与系统日志没有公开，脱敏数据见 [docs/download-analysis.md](docs/download-analysis.md)。原版预配成功不等于本次修改版在其他机器上已完成安装验证。

## 失败时

- 下载失败：确认能访问官方地址；正常包保留，失败的本次临时文件会清理。当前版本不保证中断后的断点续传。
- 缺少 Windows 工具、服务被禁用或系统组件错误：联系系统管理员 / 云电脑平台。工具不自动修复系统、不改权限或管理策略。
- 安装锁还在：先确认旧窗口和 DISM 已结束，再由你手动处理锁文件；不要并发安装。
- 应用占用：保存工作并关闭相关应用后重试。

每次操作日志位于下载目录下的 `logs\运行时间`。分享前检查本机路径、用户名等个人信息；不要上传账户密码、API Key 或整个下载目录。

退出码 `0` 表示检查/下载完成或预配已核验，具体看结果文字；`1` 表示未完成，`2` 表示缺少脚本宿主；`99` 表示已交给新的管理员窗口继续，不代表安装成功。

## 维护与构建

`installer.js` 是源码，`build.py` 生成两个可单独运行的 BAT 和小型 ZIP：

```text
python build.py --output dist
```

发布包不含约 1 GB 的 ChatGPT 本体、下载日志、离线许可证或个人数据。`SHA256SUMS.txt` 可核对脚本包内文件；这不是官方 MSIX 的哈希。构建文件与测试记录见 [docs/validation.md](docs/validation.md)。

官方依据：[OpenAI Windows 安装与离线包说明](https://learn.chatgpt.com/docs/enterprise/windows-deployment)、[Windows 应用指南](https://learn.chatgpt.com/docs/windows/windows-app)。
