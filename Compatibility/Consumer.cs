using System;
using System.Threading;
using System.Threading.Tasks;
using Rpc233;

public class Consumer
{
    public static int Main()
    {
        RpcDispatcher dispatcher = new RpcDispatcher(1024);
        dispatcher.Register("echo", Echo);
        RpcClient client = new RpcClient(new Loopback(dispatcher));
        byte[] response = client.CallAsync("echo", new byte[] { 233 }, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();
        if (response.Length != 1 || response[0] != 233) return 1;
        Console.WriteLine("PASS Rpc233 package consumer");
        return 0;
    }
    private static Task<byte[]> Echo(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        return Task.FromResult<byte[]>(payload.ToArray());
    }
    private class Loopback : IRpcTransport
    {
        private RpcDispatcher dispatcher;
        public Loopback(RpcDispatcher value) { dispatcher = value; }
        public Task<byte[]> InvokeAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            return dispatcher.DispatchAsync(method, payload, token);
        }
    }
}
