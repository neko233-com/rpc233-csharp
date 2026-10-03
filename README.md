# rpc233-csharp

C# 游戏框架 RPC 基础库，提供 .NET Standard 2.0 / 2.1、.NET Framework 4.6.2 和 .NET 8 / 9 / 10 目标。包含方法分发、HTTP 二进制传输、截止时间、取消和载荷限制。netstandard2.0 / net462 通过 System.Memory 提供内存视图。

## 使用

通过 NuGet 安装：`dotnet add package Rpc233 --version 0.2.0`。语言版本、运行时矩阵及验证边界见 [COMPATIBILITY.md](https://github.com/neko233-com/rpc233-csharp/blob/main/COMPATIBILITY.md)。

```csharp
using System;
using System.Text;
using Rpc233;

using var transport = new HttpRpcTransport(new Uri("https://example.invalid/rpc"));
var client = new RpcClient(transport);
var response = await client.CallAsync(
    "game.echo", Encoding.UTF8.GetBytes("hello"), TimeSpan.FromSeconds(3));
```

载荷是不透明字节，可传 ByteMsg233 运行库编码结果。服务端注册：

```csharp
var dispatcher = new RpcDispatcher();
dispatcher.Register("game.echo", (payload, cancellationToken) =>
    System.Threading.Tasks.Task.FromResult(payload.ToArray()));
var result = await dispatcher.DispatchAsync("game.echo", new byte[] { 1, 2, 3 });
```

## HTTP 协议

| 项目 | 约定 |
| --- | --- |
| 请求 | POST 到宿主指定的 `/rpc` 地址 |
| 方法 | `X-Rpc233-Method` 请求头，区分大小写，1..128 个 ASCII 字母 / 数字 / `_ . / -` |
| 内容 | `application/octet-stream`，请求体和响应体均为原始字节 |
| 成功 | HTTP 200 |
| 失败 | 非 200；`X-Rpc233-Error` 可携带机器可读错误码 |
| 限制 | 默认请求和响应最大 4 MiB；双方可配置 |

宿主负责实现 HTTP 入口、鉴权和业务错误映射。入口应在读取请求体时限制大小，然后调用 `DispatchAsync`。`Tests/LegacySmoke.Tests.cs` 包含使用 ASP.NET Core 的可运行本机 HTTP 适配示例。

自定义传输实现 `IRpcTransport`。默认 `HttpRpcTransport` 自建 HttpClient，关闭重定向并由 transport 释放。传入现有 HttpClient 时由调用方管理其生命周期、鉴权、重定向和超时设置。

`RpcClient` 每次调用只发一次请求。调用方取消抛出 `OperationCanceledException`；超过调用截止时间抛出 `TimeoutException`。超时或断线不表示服务端未执行，消耗型业务应使用自己的幂等键和结果查询。自定义传输若忽略取消，调用方仍可超时返回，但该传输的底层工作可能继续执行。

本版定义自己的 HTTP 适配约定，不宣称兼容现有 Go 网关 RPC 包格式。它不包含服务发现、连接级登录、自动重连、请求重放或 WebSocket 适配。Unity WebGL 需另接可用传输，本版未在 Unity / 真机上验证。

## 验证

需要 .NET 10 SDK（测试宿主使用 SDK 自带 ASP.NET Core）：

```sh
dotnet build Rpc233.csproj -c Release
dotnet test Tests/Rpc233.Tests.csproj -c Release -f net10.0
dotnet pack Rpc233.csproj -c Release --no-build -o artifacts
```

测试启动随机本机端口，覆盖真实 HTTP 往返、并发调用、未知方法、超时、取消、大小限制、重定向拒绝和忽略取消的传输。

MIT License.

完整自动化入口：`./eng/verify.ps1 -Frameworks net10.0,net462 -Legacy`（Windows）；CI 覆盖 Windows / Linux / macOS 的 .NET 8 / 9 / 10、包内两种 .NET Standard DLL 回退以及实际 NuGet 消费。
