# macOS 适配方案（todo 15 第 ③ 步：立项评估结论与实施蓝图）

> 状态：**骨架已建（src/Tuner.Core.macOS，仅 Windows 上编译通过），运行验证与完整实现必须由 macOS 真机接手。**
> 结论先行：**macOS 的 per-app 会话音量/峰值控制不存在公开 API**，完整 Tuner 体验需要一条"虚拟音频驱动"路线；
> 公开 API 范围内只能做到设备级（骨架 `CoreAudioSessionMonitor` 即此范围）。

## 一、公开 API 能做什么（骨架已覆盖）

| 能力 | API | 状态 |
| --- | --- | --- |
| 默认输出设备枚举/名称 | `AudioObjectGetPropertyData(kAudioHardwarePropertyDevices / 'dnam')` | 骨架实现 |
| 主音量/静音读写 | `kAudioDevicePropertyVolumeScalar / Mute`（按声道） | 骨架实现 |
| per-app 会话枚举/音量/峰值 | **无公开 API** | 不可行 |
| 媒体播放状态（SMTC 等价） | MediaRemote 私有框架；`MPNowPlayingInfoCenter` 仅自报告 | 不可行（公开范围） |

## 二、完整路线：虚拟音频驱动（BackgroundMusic 模式）

参考开源 BackgroundMusic（MIT，github.com/kyleneideck/BackgroundMusic）验证过的架构：

1. **AudioDriverKit 驱动**（C++/DriverKit，Xcode 构建）：注册一个虚拟输出设备；系统把全部应用音频路由到它，驱动再把混音结果写入真实设备。驱动内部按 **audio process ID**（macOS 10.13+ 的 `kAudioProcessObject` 属性可拿每个流的归属进程）维护 per-app 增益——这就是 Tuner 的"会话音量"。
2. **峰值**：驱动在混音路径上天然可见每应用 PCM，per-app 峰值免费获得（比 Windows 的 AudioMeterInformation 还直接）。
3. **宿主通信**：驱动 ⇄ .NET 宿主（Tuner.Cross/macOS 目标）经 XPC 或 Unix domain socket：枚举会话、读写 per-app 音量、订阅事件。宿主侧实现 `IAudioSessionMonitor`（接口已在 Tuner.Core 就位），规则引擎/配置/UI（Avalonia）全部复用。
4. **分发硬约束**：DriverKit 驱动需要 Apple Developer 账号（$99/年）做签名 + 公证，否则用户要手动降系统安全策略——这是分发成本，不是技术障碍。

## 三、工程量与排期评估（需在 Mac 上进行）

| 模块 | 规模 | 备注 |
| --- | --- | --- |
| AudioDriverKit 驱动 | 大（3-6 周熟练 C++/DriverKit） | 可大量借鉴 BackgroundMusic（MIT 许可兼容） |
| 宿主 IPC + IAudioSessionMonitor 实现 | 中（1-2 周） | .NET 侧 |
| 媒体状态（暂停联动） | 小 | MediaRemote 私有框架，风险自担或放弃 |
| Avalonia UI macOS 打磨 | 小 | Tuner.Cross 直接复用，字体/托盘适配 |
| 签名/公证/打包 .dmg | 小但必做 | Apple 账号依赖 |

## 四、下一步（当有 Mac 时）

1. 在 Mac 上复核 `CoreAudioSessionMonitor` 的 P/Invoke 常量与调用约定（`'nm  '`/声道数读取两处标了"需复核"）并跑通冒烟。
2. 决策驱动路线：fork BackgroundMusic 驱动改造 vs 自研；确定 IPC 协议（建议 JSON over Unix socket，与配置格式一致）。
3. Tuner.App.Avalonia 增加 `net8.0-macos` 目标接入（装配处 #if 分支已预留模式）。
