# Windows 端文档（WPF 主线）

> 平台：Windows 10/11 x64　技术栈：WPF + .NET 8 + WASAPI（NAudio 2.2.1）　开发线：`dev/windows`（优先级最高）
> 跨端共享文档见 [../dev-notes.md](../dev-notes.md)、[../git-workflow.md](../git-workflow.md)。

## 源码地图

| 工程 | 职责 |
|---|---|
| `src/Tuner.App` | WPF 托盘应用（`Tuner.exe`，Windows 主程序）：界面 / 托盘 / 全局快捷键 / 自启 |
| `src/Tuner.Core.Windows` | Windows 平台层：WASAPI 会话监控、默认设备切换、SMTC 媒体状态 |
| `src/Tuner.Core` | 跨端共享：闪避引擎、配置模型、音量曲线、平台接口 |
| `src/Tuner.Console` | 控制台诊断：实时表格 / `--log` / `--selftest` / `--setvol` |
| `src/Tuner.PauseProbe` | SMTC「暂停目标应用」能力探针（实验） |

## 环境

- Windows 10/11 x64，.NET 8 SDK（TFM：`net8.0-windows10.0.19041.0`）
- 纯用户态、免驱动、免管理员权限

## 构建与运行

```bash
dotnet build -c Release
dotnet run --project src/Tuner.App -c Release

# 直接运行构建产物（开机自启指向的也是这个路径）
# src/Tuner.App/bin/Release/net8.0-windows10.0.19041.0/Tuner.exe
```

## 开发参数

| 参数 | 用途 |
|---|---|
| `--minimized` | 静默启动，不弹窗口，驻留托盘（自启用它） |
| `--smoke` | 6 秒后自动退出，最小冒烟 |
| `--probe` | 进程内合成滚轮/拖动，验证推子显示与刷新回写（改动推子交互后必跑） |
| `--animtest` | 动效开关 / 样式时间线自检（改 Theme / 动效字典后必跑） |
| `--screenshot [--shot-dir <目录>] [--shot-size <WxH>]` | 自动遍历页签渲染 PNG，做布局自检 |
| `--lang en-US` | 指定界面语言（调试 i18n） |

控制台诊断：`--log --interval 50` / `--selftest`（自足式双会话自测）/ `--setvol <进程> <音量>`。

## 打包与发布

```bash
# 主程序（自包含单文件）
dotnet publish src/Tuner.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
  -o dev/windows/<版本>/Tuner-<版本>-win-x64

# 控制台诊断：同参数 → dev/windows/<版本>/Tuner-Console-<版本>-win-x64
```

打包后压成同名 zip 放在同一版本目录；确认可发布再把整个版本目录移到 `release/windows/<版本>/`。

## 运行时文件

| 内容 | 位置 |
|---|---|
| 配置 | `%APPDATA%\Tuner\config.json`（原子写入，损坏自动回退 `.bak`） |
| 日志 | `%APPDATA%\Tuner\app.log` |
| 便携模式 | 配置与日志落在程序目录 |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `Tuner.exe --minimized` |
| 全局快捷键 | `Ctrl+Alt+P` 暂停/恢复闪避 · `Ctrl+Alt+M` 显示主界面 |

## 已知限制

- 仅监控**默认播放设备（Multimedia 角色）**；"默认通信设备"独立场景暂不覆盖。
- 浏览器多标签共享一个音频会话（系统限制）；独占模式 / ASIO 检测不到。
- 从未打开过音频流的应用在系统层面不可见——用界面「从运行中的应用添加（预注册）」兜底。
- 闪避期间手动调整被闪避应用音量会被渐变覆盖（同时更新其恢复目标）；恢复后不受影响。

## 排错入口

踩坑细节与常见故障见 [pitfalls.md](pitfalls.md)；完整历史记录见 [../dev-notes.md](../dev-notes.md)。
