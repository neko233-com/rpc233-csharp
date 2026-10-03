# 兼容性与验证边界

语言语法、库的目标框架、实际运行环境分别验证，不能互相替代。

| 层级 | 支持或验证方式 |
| --- | --- |
| Runtime 源码 | 固定 C# 7.3，六个目标框架分别构建 |
| NuGet DLL | netstandard2.0、netstandard2.1、net462、net8.0、net9.0、net10.0 |
| 消费者语言模式 | C# 2–14，包含 7.1 / 7.2 / 7.3；用当前 Roslyn 编译真实包消费者并执行 |
| 现代运行时 | .NET 8 / 9 / 10，Windows / Linux / macOS CI 运行测试与包消费者 |
| .NET Framework | net462 目标，在 Windows 已安装的 .NET Framework 4.x 上执行 |
| .NET Standard 回退 | 在各现代运行时分别强制引用包内 netstandard2.0 / 2.1 DLL 并执行消费者 |
| Unity | 可由宿主集成兼容 DLL 或源码；Editor、IL2CPP、WebGL、真机尚未实测 |

“C# 2–14”表示现代编译器的语言模式兼容，不表示已在历史 Visual Studio、旧编译器或 .NET 1.x / 2.x CLR 上运行。本库的类型化 API 使用泛型，最低消费语法为 C# 2。
直接编译 Runtime 源码需要 C# 7.3+；低语言版本项目使用已编译 NuGet DLL。netstandard2.1 不能用于 .NET Framework，NuGet 应选择 net462 资产。
测试工程需要 .NET 10 SDK，同时安装待测版本运行时。非 Windows 不执行 net462。

## 可重复验证

```powershell
# 构建六种 DLL，运行指定运行时测试，打包，再从隔离缓存安装并执行消费者
./eng/verify.ps1 -Frameworks net8.0,net9.0,net10.0
# Windows：增加 Framework 目标和全部语言模式
./eng/verify.ps1 -Frameworks net10.0,net462 -Legacy
```

每次验证读取 TRX 的实际执行数，测试未被发现或失败均退出非零。包检查覆盖六种 DLL、README、MIT 元数据及测试二进制泄漏。
CI 在三个操作系统运行；测试报告和本次 NuGet 包保留为 Actions artifacts。语言模式测试不是将 Runtime 源码降级到 C# 1 / 2。
