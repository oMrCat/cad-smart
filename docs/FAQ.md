# FAQ

## 引擎类

### Q：浏览器打开 /health 没反应？
A：引擎没启动。双击 `C:\CadSmart\server\start-server.cmd` 手动启动；或任务计划程序 → `CadSmartServer` → 运行。开机自启任务正常时无需手动。

### Q：需要装 .NET 吗？
A：不需要。Release 安装包里的引擎是自包含单文件（60MB，内置 .NET 6 运行时）。如果你从源码用普通 publish 编译出的引擎才需要目标机装 .NET 6。

### Q：引擎占用什么端口？
A：`127.0.0.1:46200`（仅本机）。可在启动命令行参数改：`CadSmart.Server.exe 46300`。

## 功能类

### Q：为什么我搜"画个圆"没有语义推荐，只有 CIRCLE？
A：这就是推荐对了（CIRCLE=圆）。语义推荐指"把窗户都删掉→ERASE"这类自然语言理解，需要接大模型（见智能模式配置）。纯规则模式已能覆盖 90% 的找命令场景。

### Q：语义推荐很慢（30 秒+）？
A：CPU 跑 4B 大模型的物理极限。三个选择：调小 `CADSMART_LLM_WAITMS`（牺牲语义精度）；换云端 API（1~3 秒）；上 GPU（1~3 秒）。规则匹配不受影响（<10ms）。

### Q：浮窗没弹出来？
A：① 确认引擎活着（/health）；② `NETLOAD` 手动加载 `C:\CadSmart\autocad-net\CadSmart.Adapter.AutoCAD.Real.dll`；③ 再 `CADSMARTON`。如果 NETLOAD 报错，说明 AutoCAD 版本不是 2018（R22.0），需重编插件。

### Q：我的 AutoCAD 是 2021/2024，能用吗？
A：插件按 2018 编译，其他版本需用对应版本的 acmgd/acdbmgd/accoremgd 重新编译（见开发者指南）。引擎部分完全通用。

### Q：中望 CAD / 浩辰 CAD 能用吗？
A：适配骨架已写好，需要按 ZRX/GRX 二次开发（引擎完全通用，只差薄壳）。关注 Releases 等适配发布。

## GPU 类

### Q：GPU 加速怎么开？
A：默认 auto 自动识别并开启，不用管。检测到显存 ≥ 模型体积×1.2 且引擎为 CUDA/Vulkan 构建时自动启用。手动控制见 GPU 加速 Wiki。

### Q：为什么我的显卡没被用于加速？
A：看 `http://127.0.0.1:46200/gpu/status` 的 `Reason` 字段，三种可能：无 N 卡 / 推理引擎是 CPU 构建 / 显存不足。例：GTX 650 1GB 跑 4B 模型 = 显存物理不够。

## 数据类

### Q：使用统计存在哪？会不会丢？
A：`C:\CadSmart\server\usage.json`，30 秒定时保存 + 进程退出保存。强杀进程最多丢 30 秒数据。

### Q：多台电脑能共享个性化数据吗？
A：引擎各自独立存储。要共享需把 usage.json 放共享目录并改引擎启动参数（高级用法，暂未提供开关）。

## 安装类

### Q：install.cmd 报"不是内部或外部命令"？
A：安装包没拷完整。确认 `engine\CadSmart.Server.exe`（60MB）和 `install.cmd` 在同一文件夹。

### Q：怎么彻底卸载？
A：右键 `uninstall.cmd` 管理员运行。清理计划任务、C:\CadSmart、注册表加载项、AutoCAD Support 里的 DLL。
