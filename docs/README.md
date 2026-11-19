# 文档索引

Tuner 的内部说明文档都放在 `docs/`。对外说明以仓库根 [README.md](../README.md) 为准。

## 跨端共享

| 文档 | 内容 |
|---|---|
| [dev-notes.md](dev-notes.md) | 实现细节、踩坑记录、历史决策（按时间追加，最全的一份） |
| [git-workflow.md](git-workflow.md) | 分支模型与 Windows/Linux 双产线操作方法 |
| [todo.md](todo.md) | 迭代任务清单与进度 |
| [ui-iteration-plan.md](ui-iteration-plan.md) | 界面重设计迭代计划（双端同一套 Design Tokens） |
| [ui-validation.md](ui-validation.md) | 界面验证记录（截图对比与验收结论） |
| [screenshots/](screenshots) | README 用的正式截图（v0.1.1，5 张） |
| [ui-preview/](ui-preview) | 原型与配色对比截图（本地保留，不入库） |

## 按平台拆分

平台专属的构建 / 运行 / 打包 / 踩坑文档按系统分目录保存，**每条开发线只带自己那一侧**：

| 目录 | 内容 | 所在开发线 |
|---|---|---|
| `docs/windows/` | Windows 端：环境 / 构建 / 运行 / 打包 / 踩坑排错（WPF + WASAPI） | `dev/windows` |
| `docs/linux/` | Linux 端：环境 / 构建 / 运行 / 打包 / 踩坑排错（Avalonia + PulseAudio） | `dev/linux` |

合并到 `main` 后两侧并存；单看某条开发线时只有本侧目录存在（上表用代码样式标注，不构成跨分支死链）。

排查使用问题：先看根 [README.md](../README.md) 的「已知限制」，再看本侧平台的踩坑文档，最后查 [dev-notes.md](dev-notes.md) 的完整历史记录。
