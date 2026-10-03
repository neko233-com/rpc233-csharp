using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Rpc233
{
    /// <summary>POST binary payloads using the X-Rpc233-Method header. The caller supplies authentication on HttpClient.</summary>
    public sealed class HttpRpcTransport : IRpcTransport, IDisposable
    {
        public const string MethodHeader = "X-Rpc233-Method";
        public const string ErrorHeader = "X-Rpc233-Error";
        private readonly Uri _endpoint;
        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        private readonly int _maxPayloadBytes;

        public HttpRpcTransport(Uri endpoint, HttpClient? httpClient = null, int maxPayloadBytes = 4 * 1024 * 1024)
        {
            if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
            if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "https" && endpoint.Scheme != "http"))
                throw new ArgumentException("An absolute HTTP(S) endpoint is required.", nameof(endpoint));
            if (maxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            _endpoint = endpoint;
            _ownsClient = httpClient is null;
            _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _maxPayloadBytes = maxPayloadBytes;
        }

        public async Task<byte[]> InvokeAsync(string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            RpcMethods.Validate(method);
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length > _maxPayloadBytes) throw new RpcException("payload_too_large", "RPC request exceeds the payload limit.");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add(MethodHeader, method);
            request.Content = new ByteArrayContent(payload.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var code = "http_" + (int)response.StatusCode;
                if (response.Headers.TryGetValues(ErrorHeader, out var codes))
                    foreach (var candidate in codes) { code = candidate; break; }
                throw new RpcException(code, $"RPC '{method}' failed with HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > _maxPayloadBytes)
                throw new RpcException("payload_too_large", "RPC response exceeds the payload limit.");
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[Math.Min(8192, _maxPayloadBytes)];
            int count;
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > _maxPayloadBytes) throw new RpcException("payload_too_large", "RPC response exceeds the payload limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }

        public void Dispose() { if (_ownsClient) _http.Dispose(); }
    }
}
