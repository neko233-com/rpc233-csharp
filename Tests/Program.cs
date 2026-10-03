using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rpc233;

var passed = 0;
async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

var dispatcher = new RpcDispatcher(1024);
dispatcher.Register("game.echo", (data, _) => Task.FromResult(data.ToArray()));
dispatcher.Register("game.slow", async (_, token) => { await Task.Delay(5000, token); return Array.Empty<byte>(); });
dispatcher.Register("game.large", (_, _) => Task.FromResult(new byte[1025]));
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var app = builder.Build();
app.MapPost("/rpc", async context =>
{
    try
    {
        // Real server adapters must enforce limits while reading, before invoking the dispatcher.
        using var body = new MemoryStream();
        var buffer = new byte[1024]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (body.Length + count > 1024) throw new RpcException("payload_too_large", "Request too large.");
            body.Write(buffer, 0, count);
        }
        var result = await dispatcher.DispatchAsync(context.Request.Headers[HttpRpcTransport.MethodHeader].ToString(), body.ToArray(), context.RequestAborted);
        context.Response.ContentType = "application/octet-stream";
        await context.Response.Body.WriteAsync(result, context.RequestAborted);
    }
    catch (RpcException e)
    {
        context.Response.StatusCode = e.Code == "method_not_found" ? 404 : 400;
        context.Response.Headers[HttpRpcTransport.ErrorHeader] = e.Code;
    }
    catch (ArgumentException) { context.Response.StatusCode = 400; }
});
app.MapPost("/oversized", async context => { await context.Response.Body.WriteAsync(new byte[1025]); });
app.MapPost("/redirect", context => { context.Response.Redirect("/rpc", true, true); return Task.CompletedTask; });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var transport = new HttpRpcTransport(new Uri(address + "/rpc"), maxPayloadBytes: 1024);
var client = new RpcClient(transport);

await Check("real HTTP binary roundtrip and concurrent calls", async () =>
{
    await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
    {
        var payload = Encoding.UTF8.GetBytes("金币-" + i);
        var response = await client.CallAsync("game.echo", payload, TimeSpan.FromSeconds(5));
        Assert(payload.SequenceEqual(response));
    }));
});
await Check("remote method error code", async () =>
{
    try { await client.CallAsync("missing", Array.Empty<byte>(), TimeSpan.FromSeconds(2)); }
    catch (RpcException e) { Assert(e.Code == "method_not_found"); return; }
    throw new Exception("Missing method succeeded.");
});
await Check("HTTP deadline", () => Throws<TimeoutException>(() => client.CallAsync("game.slow", Array.Empty<byte>(), TimeSpan.FromMilliseconds(80))));
await Check("caller cancellation", async () =>
{
    using var cancel = new CancellationTokenSource(80);
    await Throws<OperationCanceledException>(() => client.CallAsync("game.slow", Array.Empty<byte>(), TimeSpan.FromSeconds(2), cancel.Token));
});
await Check("request, dispatcher response and streamed response limits", async () =>
{
    await Throws<RpcException>(() => client.CallAsync("game.echo", new byte[1025], TimeSpan.FromSeconds(2)));
    await Throws<RpcException>(() => client.CallAsync("game.large", Array.Empty<byte>(), TimeSpan.FromSeconds(2)));
    using var oversized = new HttpRpcTransport(new Uri(address + "/oversized"), maxPayloadBytes: 1024);
    await Throws<RpcException>(() => new RpcClient(oversized).CallAsync("game.echo", Array.Empty<byte>(), TimeSpan.FromSeconds(2)));
});
await Check("redirects are not followed", async () =>
{
    using var redirect = new HttpRpcTransport(new Uri(address + "/redirect"));
    await Throws<RpcException>(() => new RpcClient(redirect).CallAsync("game.echo", Array.Empty<byte>(), TimeSpan.FromSeconds(2)));
});
await Check("ignored transport cancellation still times out with one send", async () =>
{
    var stubborn = new StubbornTransport();
    await Throws<TimeoutException>(() => new RpcClient(stubborn).CallAsync("game.echo", Array.Empty<byte>(), TimeSpan.FromMilliseconds(30)));
    Assert(stubborn.Calls == 1);
    stubborn.Pending.SetException(new IOException("late failure"));
});
await Check("invalid method rejected before transport", async () =>
{
    var fake = new StubbornTransport();
    await Throws<ArgumentException>(() => new RpcClient(fake).CallAsync("bad\r\nheader", Array.Empty<byte>(), TimeSpan.FromSeconds(1)));
    Assert(fake.Calls == 0);
});
await app.StopAsync();
Console.WriteLine($"Rpc233: {passed} checks passed.");

public sealed class StubbornTransport : IRpcTransport
{
    public int Calls;
    public TaskCompletionSource<byte[]> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<byte[]> InvokeAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    { Calls++; return Pending.Task; }
}
