# 产品命名：CodeOrbit

基座产品名是 **CodeOrbit**。历史称呼「Runtime」仅作兼容保留，**不得**再用于用户可见文案中指代该基座。

## 必须用 CodeOrbit

- 设置页、托盘、关于、连接状态、错误提示
- README / 用户文档中的产品叙述

## 可以保留 Runtime / runtime

- C# 历史类型：`WpfRuntime*`、`IWpfRuntimeClient` 等（不强制全仓 rename）
- settings 键：`runtime_launch_mode` 等
- 磁盘路径：`runtime/`、`runtime-manifest.json`、脚本参数 `-SyncRuntime`
- 技术词：.NET 运行时、CLR；HUD「运行时音效」（功能名，非基座）

## 新代码

用户可见字符串写 CodeOrbit；新类型优先 CodeOrbit 语义命名，勿再引入 Runtime 产品前缀。

更完整的开发说明见本地 `CLAUDE.md`「命名规则」段落（若该文件被 gitignore，以本文与 `.trellis/spec/frontend/component-guidelines.md` 为准）。
