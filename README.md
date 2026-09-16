# VidLog · 电脑端

电商打包取证系统的**电脑端**：工位录像、集中接收手机/其他电脑的录像、检索与回放。

C# / .NET 9 + WPF。

**闭源商业产品。** 本仓库为私有仓库。

---

## 需求在哪

需求规格书**只有一份**，在母仓 [VidLog0705/VidLog](https://github.com/VidLog0705/VidLog)，本仓不复制（避免多处副本漂移）：

| 文档 | 作用 |
|---|---|
| 母仓 `docs/01-行为规格书.md` | **唯一的需求来源** |
| 母仓 `docs/02-数据模型.md` | 数据概念模型（本仓实现它） |
| 母仓 `docs/03-端间契约.md` | 端间契约（本仓实现接收方与客户端侧） |
| 母仓 `IMPLEMENTATION.md` | 里程碑与开工顺序 |
| 本仓 [`AGENTS.md`](AGENTS.md) | 工程约束、12 条不变量、洁净室规则 |

**在写第一行代码之前，必须确认你理解了 `AGENTS.md` 第 0 节的洁净室规则** ——
那是这个项目唯一一件做错了没法回头的事。

> ⚠️ 这条路意味着**离线或没有母仓权限时无法开工**。这是拆仓换来的取舍，已知并接受。

---

## 仓库结构

```
src/VidLog.Desktop.App/            WPF 外壳（薄，只做呈现与交互）
src/VidLog.Desktop.Core/           领域模型与可测试逻辑（不引用 WPF）
tests/VidLog.Desktop.Core.Tests/   xUnit
scripts/precheck.ps1               推送前的本地预检
```

---

## 开发环境

| 组件 | 最低要求 |
|---|---|
| .NET SDK | 9.0+ |
| FFmpeg | 任意近期版本（M2 起需要） |
| Windows | 10 / 11（WPF 与全局键盘钩子） |

---

## 本地开发流程

```powershell
# 推送前的本地预检
pwsh -NoProfile -File scripts/precheck.ps1

# 全绿后再推送
git push
```

---

## 当前进度

**M0 骨架 + M1 契约** —— 工程能编译、测试能跑、模型与状态机已定。

已落地：

- WPF 外壳（能启动的空窗口）
- `RelativePath` / `WaybillNumber` / `ContentHash` 三个硬约束值对象
- 三个状态机枚举（录制会话 / 上传任务 / 证据生命周期），规格 §4

未做（按里程碑推进）：扫码识别、录像链路、检索回放、归档、证据能力。

CI 见 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)。

---

## 许可

专有软件，保留所有权利。未经授权不得复制、分发或用于衍生作品。
