using System.Net;
using System.Text;
using Grpc.Net.Client;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    /// <summary>
    /// Base class for Vertex server API endpoints.
    /// </summary>
    public abstract class BaseInterface : IDisposable
    {
        public HttpClient HttpClient { get; }
        public GrpcChannel GrpcChannel { get; }

        protected BaseInterface(string url)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            HttpClient = new HttpClient
            {
                BaseAddress = new Uri(url.TrimEnd('/') + "/"),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                DefaultRequestVersion = HttpVersion.Version20
            };

            GrpcChannel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    // Lets concurrent transfers open extra connections instead of sharing one connection's window.
                    EnableMultipleHttp2Connections = true,
                    // Start with a large window rather than waiting for dynamic window scaling to ramp up.
                    InitialHttp2StreamWindowSize = FileTransferLimits.StreamWindowSize,
                    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
                },
                MaxSendMessageSize = FileTransferLimits.MaximumMessageSize,
                MaxReceiveMessageSize = FileTransferLimits.MaximumMessageSize
            });
        }

        protected virtual async Task<ServerResult<TResult>> SendAsync<TResult>(HttpMethod method, string endpoint, object? body = null, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(method, endpoint);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Version = HttpVersion.Version20;

            if (body is not null)
            {
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;

            try
            {
                response = await HttpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return ServerResult<TResult>.Failure(ex, (int?)ex.StatusCode ?? 0);
            }

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return ServerResult<TResult>.Failure(new HttpRequestException(string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content, null, response.StatusCode), statusCode);
                }

                try
                {
                    TResult? data = JsonConvert.DeserializeObject<TResult>(content);
                    return data is null ? ServerResult<TResult>.Failure(new JsonSerializationException("Server returned an empty response."), statusCode) : ServerResult<TResult>.Success(data, statusCode);
                }
                catch (JsonException ex)
                {
                    return ServerResult<TResult>.Failure(ex, statusCode);
                }
            }
        }

        public void Dispose()
        {
            HttpClient.Dispose();
            GrpcChannel.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}