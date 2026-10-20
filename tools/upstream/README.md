# upstream/ —— 上游源码留档（对照用）

来自 `LaVashikk/Lecoo-Control-Center`（MIT License，https://github.com/LaVashikk/Lecoo-Control-Center ），
2026-10-21 用 `gh api` 拉下来做逐行对照，**不是本项目的一部分，也没有改动**。

| 文件 | 上游路径 | 为什么留 |
|---|---|---|
| `boards.rs` | `daemon/src/ec/profile/boards.rs` | 板子配置表（HRAM 基址候选、rsoc=0x93、风扇/温度寄存器、各板的充电机制） |
| `flexicharger.rs` | `daemon/src/ec/hw/flexicharger.rs` | FlexiCharger / PolicyByte 两套充电上限机制的具体写法（写序、0/0=不限制、freeze=写当前 SoC） |
| `offsets.rs` | `daemon/src/ec/offsets.rs` | 其它 EC 偏移量 |
| `getters.rs` | `daemon/src/ec/hw/getters.rs` | EC 读值辅助函数 |

更新方式：

```bash
gh api repos/LaVashikk/Lecoo-Control-Center/contents/daemon/src/ec/profile/boards.rs --jq '.content' | base64 -d > tools/upstream/boards.rs
```

**注意**：上游的板子表只有 `N155A / N155C / N155D / N161A`，**没有 N175L**（本机）。
所以这里的寄存器语义是「同平台家族的通用写法」，不是厂商对 N175L 的官方支持。
