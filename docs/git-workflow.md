# Git 双产线操作方法（Tuner 仓库规范）

> 2026-11-05 用户定型。配套全局规则：禁止直接在 main 上更改；一切改动（不论大小）新开分支；
> 逐层合并；合并进 main 必须经过人工检查，AI 不得自行合并/推送 main。

## 分支结构

```
main            稳定线：只接受合并，保持随时可发布；合并前须人工检查
├─ dev/windows  Windows 开发线（WPF 主线，优先级最高：src/Tuner.App + Tuner.Core.Windows）
└─ dev/linux    Linux 开发线（Avalonia：src/Tuner.App.Avalonia + Tuner.Core.Linux）
```

## 日常操作

```bash
# 1. 切到所属平台开发线，派生短命分支（前缀：feat/ fix/ docs/ refactor/）
git switch dev/windows            # Windows 相关改动
git switch -c fix/nav-indicator-retry

# 2. 改动、自检（构建 + --animtest/--selftest 等验证手段）后逐层合并
git switch dev/windows
git merge fix/nav-indicator-retry
git branch -d fix/nav-indicator-retry

# 3. 阶段性稳定后：开发线 → main（须人工检查后进行），并打 tag
git switch main && git merge --no-ff dev/windows && git tag v0.1.2-eta
```

## 双产线约定

| 事项 | 规则 |
|---|---|
| Windows 功能 | 基于 `dev/windows` |
| Linux 功能 | 基于 `dev/linux` |
| 共享核心（`src/Tuner.Core`、配置模型） | 一律基于 `dev/windows`（主线）进行，再 merge / cherry-pick 同步到 `dev/linux` |
| 合并进 `main` | 人工检查确认后才执行；AI 不自行合并/推送 main |
| 版本发布 | main 上打 tag；打包产物按 `dist/windows`、`dist/linux`、`dist/console`、`dist/archive` 归档 |

## 为什么共享核心走 dev/windows

Tuner.Core 由两端共同消费，若允许双向改动会产生合并漂移。Windows 是主线（用户 2026-11-05 决策），
核心演进以 `dev/windows` 为唯一事实来源，`dev/linux` 只做消费者同步。
