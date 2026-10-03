using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Rpc233;
using Xunit;

public sealed class CoreTests
{
    private sealed class FakeTransport : IRpcTransport
    {
        public int Calls;
        public Task<byte[]> InvokeAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken token)
        { Calls++; return Task.FromResult(payload.ToArray()); }
    }
    [Theory]
    [InlineData("")]
    [InlineData("bad\r\nheader")]
    [InlineData("含中文")]
    public async Task InvalidMethodsNeverReachTheTransport(string method)
    {
        var transport = new FakeTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => new RpcClient(transport).CallAsync(method, Array.Empty<byte>(), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, transport.Calls);
    }
    [Fact]
    public async Task AlreadyCanceledCallDoesNotSend()
    {
        var transport = new FakeTransport();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RpcClient(transport).CallAsync("echo", Array.Empty<byte>(), TimeSpan.FromSeconds(1), new CancellationToken(true)));
        Assert.Equal(0, transport.Calls);
    }
    [Fact]
    public async Task DispatcherEnforcesUnknownDuplicateNullAndOversizeResults()
    {
        var dispatcher = new RpcDispatcher(8);
        dispatcher.Register("null", (_, _) => Task.FromResult<byte[]>(null!));
        dispatcher.Register("large", (_, _) => Task.FromResult(new byte[9]));
        Assert.Throws<ArgumentException>(() => dispatcher.Register("large", (_, _) => Task.FromResult(Array.Empty<byte>())));
        Assert.Equal("method_not_found", (await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("missing", Array.Empty<byte>()))).Code);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("null", Array.Empty<byte>()))).Code);
        Assert.Equal("payload_too_large", (await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("large", Array.Empty<byte>()))).Code);
        Assert.Equal("payload_too_large", (await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("large", new byte[9]))).Code);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Disposed;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 233 }) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(Reply(request));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    [Fact]
    public async Task BorrowedHttpClientIsNotDisposedAndHeadersMatchContract()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        handler.Reply = request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("item.get", request.Headers.GetValues(HttpRpcTransport.MethodHeader));
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 233 }) };
        };
        using (var transport = new HttpRpcTransport(new Uri("https://example.invalid/rpc"), http))
            Assert.Equal(new byte[] { 233 }, await transport.InvokeAsync("item.get", Array.Empty<byte>(), CancellationToken.None));
        Assert.False(handler.Disposed);
    }
    [Fact]
    public async Task HttpFailureDoesNotExposeResponseBody()
    {
        var handler = new Handler { Reply = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("private diagnostic content") } };
        using var http = new HttpClient(handler);
        using var transport = new HttpRpcTransport(new Uri("https://example.invalid/rpc"), http);
        var error = await Assert.ThrowsAsync<RpcException>(() => transport.InvokeAsync("item.get", Array.Empty<byte>(), CancellationToken.None));
        Assert.Equal("http_500", error.Code); Assert.DoesNotContain("private diagnostic content", error.Message);
    }
}
