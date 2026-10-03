using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rpc233
{
    public sealed class RpcException : Exception
    {
        public string Code { get; }
        public RpcException(string code, string message) : base(message) { Code = code; }
    }

    public interface IRpcTransport
    {
        Task<byte[]> InvokeAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    }

    /// <summary>One invocation per call. Timeouts never automatically retry a business operation.</summary>
    public sealed class RpcClient
    {
        private readonly IRpcTransport _transport;
        public RpcClient(IRpcTransport transport) { _transport = transport ?? throw new ArgumentNullException(nameof(transport)); }

        public async Task<byte[]> CallAsync(string method, ReadOnlyMemory<byte> payload, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            RpcMethods.Validate(method);
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = linked.Token.Register(() => canceled.TrySetResult(true));
            try
            {
                var operation = _transport.InvokeAsync(method, payload, linked.Token);
                if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) != operation)
                {
                    // Observe a late failure even if a custom transport ignores cancellation.
                    _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    linked.Token.ThrowIfCancellationRequested();
                }
                return await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"RPC '{method}' exceeded its deadline. The remote operation may have completed.");
            }
        }
    }

    public sealed class RpcDispatcher
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>>> _handlers
            = new Dictionary<string, Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>>>(StringComparer.Ordinal);
        private readonly int _maxPayloadBytes;

        public RpcDispatcher(int maxPayloadBytes = 4 * 1024 * 1024)
        {
            if (maxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            _maxPayloadBytes = maxPayloadBytes;
        }

        public void Register(string method, Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> handler)
        {
            RpcMethods.Validate(method);
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            lock (_gate) _handlers.Add(method, handler);
        }

        public async Task<byte[]> DispatchAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            RpcMethods.Validate(method);
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length > _maxPayloadBytes) throw new RpcException("payload_too_large", "RPC request exceeds the payload limit.");
            Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> handler;
            lock (_gate)
                if (!_handlers.TryGetValue(method, out handler!)) throw new RpcException("method_not_found", $"Unknown RPC method: {method}.");
            var response = await handler(payload, cancellationToken).ConfigureAwait(false);
            if (response is null) throw new RpcException("invalid_response", "RPC handler returned null.");
            if (response.Length > _maxPayloadBytes) throw new RpcException("payload_too_large", "RPC response exceeds the payload limit.");
            return response;
        }
    }

    internal static class RpcMethods
    {
        internal static void Validate(string method)
        {
            if (string.IsNullOrEmpty(method) || method.Length > 128) throw new ArgumentException("RPC method must contain 1..128 characters.", nameof(method));
            foreach (var ch in method)
                if (!(ch >= 'a' && ch <= 'z') && !(ch >= 'A' && ch <= 'Z') && !(ch >= '0' && ch <= '9')
                    && ch != '_' && ch != '.' && ch != '/' && ch != '-')
                    throw new ArgumentException("RPC method must use ASCII letters, digits, underscore, dot, slash or dash.", nameof(method));
        }
    }
}
