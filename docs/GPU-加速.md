# GPU 加速

默认 **auto 模式**：自动识别、满足条件自动开启，用户什么都不用管。

## 判定链

引擎启动时执行三步判定：

1. **硬件检测**：`nvidia-smi --query-gpu=name,memory.total` 读 GPU 名称与显存
2. **引擎构建识别**：读 llama-server 进程命令行，从路径与参数识别构建类型（`-cpu` / `-cuda` / `-vulkan` 目录后缀 + `-ngl 0` 参数）
3. **可行性判定**：显存 ≥ 模型体积 × 1.2（预留 KV cache）

| 判定结果 | 行为 |
|---------|------|
| 全部满足 | `ActiveDevice=gpu`，自动启用 offload |
| 无 NVIDIA GPU | CPU 模式，原因："未检测到 NVIDIA GPU" |
| 引擎是 CPU 构建 | CPU 模式，原因："当前推理引擎为 cpu 构建…可升级 LocModel 引擎获取 CUDA 版" |
| 显存不足 | CPU 模式，原因："显存不足：xxx 仅 1024 MiB，模型需约 2700 MiB" |

## 手动开关

```
POST http://127.0.0.1:46200/gpu/mode
Content-Type: application/json

{"mode": "auto"}    ← auto（默认自动识别并开启） / cpu（强制关） / gpu（强制尝试）
```

查询当前状态：

```
GET http://127.0.0.1:46200/gpu/status
```

返回示例：

```json
{
  "GpuName": "NVIDIA GeForce GTX 650",
  "VramTotalMiB": 1024,
  "ModelSizeMiB": 2700,
  "EngineVariant": "cpu",
  "CanOffload": false,
  "Reason": "当前推理引擎为 cpu 构建，不支持 GPU offload",
  "ActiveDevice": "cpu"
}
```

## WPF 界面按钮

浮窗左下角 **⚡ GPU 加速** 按钮，点击循环：自动 → 强制开启 → 关闭。有可用 GPU 时绿色高亮，按钮旁显示 GPU 名称与判定原因。

## 真机实测数据

| 硬件 | 判定 |
|------|------|
| GTX 650（1GB，CUDA 11.4，Kepler 2012）+ Qwen3.5-4B Q4（2.7GB） | 正确判定：显存不足 + 引擎为 CPU 构建 → 保持 CPU 模式 |
| RTX 3060 12GB + CUDA 引擎 | 将自动启用 offload（1~3 秒出语义结果） |

> 显存 1GB 的老卡装不下 4B 模型是物理限制；换卡或用云端 API 才能提升语义速度。规则匹配不受影响（<10ms）。
