<div align="center">

# CadSmart 智能命令提示

**在 CAD 命令行里用中文、拼音、缩写找命令 —— 不用背英文命令名**

`画个圆` → CIRCLE · `zx` → LINE · `把窗户都删掉` → ERASE

[下载安装包](../../releases/latest) · [安装手册](安装使用说明书.md) · [Wiki](../../wiki)

</div>

---

## 这是什么

CadSmart 是一个**平台无关的 CAD 命令智能推荐引擎 + AutoCAD 插件**：

```
你输入:  画个圆  /  zhixian  /  zx  /  标尺寸  /  把窗户都删掉
           ↓ 引擎（规则匹配 + 本地大模型语义理解，自动降级）
返回:    CIRCLE · LINE · DIMLINEAR · ERASE  （带评分、参数提示）
           ↓ 一键执行
AutoCAD 就开始执行该命令（和手输 CIRCLE 一样）
```

### 三档智能模式（自动降级，开箱即用）

| 模式 | 条件 | 能力 |
|------|------|------|
| 纯规则 | 默认，零配置 | 拼音（含方言变体 zh→z）/ 首字母 / 别名 / 关键词 / 模糊匹配，**离线可用** |
| 本地语义 | 局域网内有 llama-server（LocModel 等） | + 自然语言理解（"把这两条线倒个圆角"→ FILLET） |
| 云端语义 | 配置 DeepSeek/通义 API | + 同上，云端算力 |

**规则匹配 <10ms，186 条内置命令库，使用越多的命令排越前（个性化）。**

## 核心特性

- 🚀 **免依赖部署**：60MB 单文件引擎，内置 .NET 运行时，拷到目标电脑双击安装脚本即完成
- 🧠 **规则 + LLM 双通道融合**：本地 CPU 模型 7~9 秒出语义结果，超时自动降级纯规则（永不出空结果）
- ⚡ **GPU 加速自动识别**：检测 NVIDIA 显卡显存与引擎构建，满足条件自动启用 offload（auto/cpu/gpu 三态开关）
- 📊 **个性化排序**：accept 的命令使用次数持久化，越用越懂你
- 🔗 **上下文推荐**：刚画完圆 → 自动推荐 OFFSET（偏移），来自真实命令序列学习
- 🖥️ **AutoCAD 浮窗**：WPF 浮动候选窗，双击/回车执行，注册表自动加载
- 🔒 **安全**：仅绑定 127.0.0.1，XSS/注入/超长输入全部无害化

## 30 秒上手

1. 下载 [Latest Release](../../releases/latest) 里的 `CadSmart-离线安装包.zip`，解压
2. 右键 `install.cmd` → **以管理员身份运行**（10 秒装完，自动检测 AutoCAD）
3. 打开 AutoCAD → 输入 `CADSMARTON` → 浮窗弹出 → 输入 `画个圆` → 双击 CIRCLE

详细步骤、语义模式配置、排查手册见 **[安装使用说明书](安装使用说明书.md)**。

## 架构

```
┌─────────────────────────────────────────────┐
│  CAD 平台薄壳（AutoCAD 2018 已实现）          │
│  CADSMARTON/CS 命令 · WPF 浮窗 · HTTP 调用   │
└──────────────────┬──────────────────────────┘
                   │ localhost HTTP (127.0.0.1:46200)
┌──────────────────▼──────────────────────────┐
│  CadSmart.Server（引擎服务，单文件免安装）      │
│  ├─ 规则引擎: 拼音/别名/关键词 <10ms          │
│  ├─ LLM 融合: 粗筛 Top30 → 大模型精排         │
│  ├─ GPU 检测: nvidia-smi + 引擎构建识别       │
│  └─ 行为持久化: usage.json 定时落盘           │
└──────────────────┬──────────────────────────┘
                   │ OpenAI 兼容 API（可配）
┌──────────────────▼──────────────────────────┐
│  大模型: LocModel / llama-server / 云端 API   │
└─────────────────────────────────────────────┘
```

**平台无关设计**：核心逻辑全在 `CadSmart.Core`（平台无关类库），AutoCAD/中望/浩辰只是薄壳适配器（中望 ZRX、浩辰 GRX 骨架已就绪）。

## 项目结构

```
src/
├── CadSmart.Core/           核心引擎（匹配/LLM 融合/GPU 检测/知识库，平台无关）
├── CadSmart.Data/           数据层（186 条命令知识库 + 行为统计）
├── CadSmart.Server/         独立服务进程（localhost JSON API）
├── CadSmart.Adapter.AutoCAD/  AutoCAD 2018 薄壳（LSP + .NET 真机插件）
├── CadSmart.Simulator.Wpf/  WPF 浮窗 UI（验证过的交互形态）
tests/
└── CadSmart.Core.Tests/     105 项单测（匹配引擎/融合/上下文/持久化）
```

## 构建

```bash
# 引擎（net8.0 + net6.0 双目标）
dotnet publish src/CadSmart.Server -c Release -f net8.0 -o dist/server

# 自包含单文件（目标机免装 .NET）
dotnet publish src/CadSmart.Server -c Release -f net6.0 -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/server-selfcontained

# AutoCAD 真机插件（需把 acmgd/acdbmgd/accoremgd.dll 放入 lib/autocad2018/，见 Wiki）
dotnet build src/CadSmart.Adapter.AutoCAD/CadSmart.Adapter.AutoCAD.Real.csproj

# 测试
dotnet test tests/CadSmart.Core.Tests
```

## 真机验证数据

| 指标 | 数值 |
|------|------|
| 引擎规则匹配延迟 | **平均 0.1ms / 最大 2ms**（105 项测试 + 34 项专业测试全绿） |
| 语义推荐（CPU 4B 模型） | 7~9 秒（GPU 可到 1~3 秒） |
| 匹配正确率回归 | 20/20（精确/别名/拼音/首字母/方言变体） |
| 强杀进程恢复 | <12 秒（计划任务自动拉起） |
| 数据落盘 | 30 秒定时持久化，强杀不丢 |

## 路线图

- [x] 引擎服务 + 规则匹配 + LLM 融合
- [x] WPF 浮窗 UI
- [x] AutoCAD 2018 真机插件（NETLOAD + 注册表自动加载）
- [x] GPU 自动加速开关
- [x] 离线安装包（一键安装/卸载脚本）
- [ ] 命令行逐字实时候选（WH_KEYBOARD 钩子方案）
- [ ] 中望 CAD 适配（ZRX）
- [ ] 浩辰 CAD 适配（GRX）
- [ ] 知识库在线更新（组件已内置）

## 许可

MIT
