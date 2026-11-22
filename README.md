# CadSmart — CAD 智能命令提示

在 CAD 命令行输入时提供智能提示：中文口语、拼音、缩写、错别字均可命中命令，规则引擎本地毫秒级响应，LLM 语义推荐异步增强。

## 解决方案结构

```
CadSmart.sln
├── src/
│   ├── CadSmart.Core/              核心引擎（平台无关）
│   │   ├── Abstractions/           ICommandSink / ISuggestionUI / IPlatformAdapter / ILlmProvider / ICommandRepository
│   │   ├── Domain/                 CadCommand / CommandCandidate / SuggestionResult
│   │   ├── Matching/               MatchEngine 六路规则匹配（精确/别名/拼音/首字母/包含/模糊）
│   │   ├── Prompting/              PromptTemplates 提示词模板（系统提示词 + JSON 输出约束 + 解析）
│   │   ├── Providers/              Cloud / Local(LocModel·Ollama·LM Studio 自动探测) / Mock / Null
│   │   └── Suggestion/             SuggestionService 双通道编排 + ISuggestionEngine 抽象
│   │       └── Platform/           SuggestionHost 平台接线器（防抖/展示/选中/执行/统计 全自动）
│   ├── CadSmart.Data/              JSON 命令知识库（104 命令）+ 行为存储 + RemoteSuggestionEngine + KnowledgeUpdater
│   ├── CadSmart.Server/            引擎独立服务进程（localhost JSON API：/suggest /accept /health）
│   ├── CadSmart.Adapter.AutoCAD/   AutoCAD 薄壳适配器骨架（真机对接点见文件头注释）
│   ├── CadSmart.Adapter.ZWCAD/     中望适配器骨架（ZRX/ZwSoft .NET）
│   ├── CadSmart.Adapter.GstarCAD/  浩辰适配器骨架（GRX/Gcad）
│   ├── CadSmart.Simulator/         命令行模拟器（支持 --bench）
│   └── CadSmart.Simulator.Wpf/     WPF 候选框原型（阶段0 UI 可行性已验证）
└── tests/CadSmart.Core.Tests/      xUnit 69 项：单元 + HTTP桩 + 服务化 + 平台接线 + E2E + 评估集 + 更新器
```

## 快速开始

```bash
# 使用本地 SDK（无需管理员权限）
export DOTNET=/c/Users/cattery/dotnet-sdk/dotnet

# 构建与测试
$DOTNET build CadSmart.sln
$DOTNET test tests/CadSmart.Core.Tests/CadSmart.Core.Tests.csproj

# 交互式模拟器（中文/拼音/缩写输入，:help 查看指令）
$DOTNET run --project src/CadSmart.Simulator/CadSmart.Simulator.csproj

# 自动化性能与正确性测试（无人值守）
$DOTNET run --project src/CadSmart.Simulator/CadSmart.Simulator.csproj -- --bench
```

## 模拟器指令

| 指令 | 作用 |
|------|------|
| 直接输入 | 中文口语（画个圆）、拼音（yuan/zhi）、缩写（zx/l）、错别字（circls） |
| `:llm mock` | 启用 Mock 语义供应商，体验 LLM 融合 |
| `:llm off` | 纯规则模式 |
| `:stats` | 查看使用统计（个性化数据） |
| `:bench` | 28 用例性能与正确性测试 |
| `:quit` | 退出并保存使用统计 |

## 已验证指标（2026-11-22 最终回归 · Release）

| 指标 | 结果 | 目标 |
|------|------|------|
| 自动化测试 | **97/97 通过** | 全绿 |
| 评估集（115 用例 × 八类输入） | 规则引擎 Top-1 ≥85% 一次达标，Top-3 ≥95% | ✅ |
| 单次匹配延迟 | 平均 0.1ms，最慢 3ms | <50ms |
| 全库扫描（186 命令） | 0.08ms/次 | — |
| NL2Command 评估集（LLM 融合） | 100%（20/20） | ≥85% |
| 引擎服务真实进程冒烟 | /health /suggest /accept 全通 | — |
| Provider HTTP 通路（桩服务器） | 鉴权/解析/超时降级全验证 | — |

## 智能能力全景（产品定位三大能力全部落地）

| 能力 | 实现 | 状态 |
|------|------|------|
| 模糊匹配 | 六路规则 + 前缀覆盖率 + 拼音方言变体（zh/z、ch/c、sh/s、ng/n）+ 全角归一化 + 紧凑空格 | ✅ 115 用例评估集 |
| 上下文感知 | 转移表推荐器（40+ 工作流先验 × 用户行为融合），执行后自动弹"下一步建议" | ✅ 双模拟器接入 |
| 自然语言直达 | LLM 双通道（300ms 融合窗口），提示词模板 + 防编造 + 评估集 | ✅ Mock 验证 / 真实 Key 待配 |

## 当前状态与下一步

- ✅ 阶段 0/1/2/3 核心全部完成（详见路线图勾选项）
- ✅ 引擎已进程化（ISuggestionEngine 抽象 + MiniHttpServer + RemoteSuggestionEngine）
- ✅ 平台适配 SDK（IPlatformAdapter + SuggestionHost），三平台骨架就绪
- ⬜ 真机 POC：安装 AutoCAD/中望/浩辰后按 Adapter 内 TODO 对接（最高风险项：命令行捕获）
- ⬜ 真实云端 LLM：填 LlmOptions 即可（HTTP 通路已用桩服务器验证）
- ⬜ LocModel 实测：装 LocModel 后 LocalLlmProvider 自动探测接入
