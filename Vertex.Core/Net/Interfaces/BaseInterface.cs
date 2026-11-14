// Copyright 2026 Entex Interactive

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
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

        /// <summary>
        /// The server address this interface talks to, without a trailing slash.
        /// </summary>
        public string ServerAddress { get; }

        /// <param name="url">The server address.</param>
        /// <param name="token">The access token from <c>vx login</c>, sent as a bearer token on every REST and gRPC
        /// call. Null calls the server anonymously.</param>
        protected BaseInterface(string url, string? token = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            Uri baseAddress = new(url.TrimEnd('/') + "/");
            ServerAddress = url.TrimEnd('/');
            _versionPolicy = VersionPolicyFor(baseAddress);
            HttpClient = new HttpClient(new AuthorizationHandler(token, new SocketsHttpHandler()))
            {
                BaseAddress = baseAddress,
                DefaultVersionPolicy = _versionPolicy,
                DefaultRequestVersion = HttpVersion.Version20
            };

            SocketsHttpHandler transport = new()
            {
                // Lets concurrent transfers open extra connections instead of sharing one connection's window.
                EnableMultipleHttp2Connections = true,
                // Start with a large window rather than waiting for dynamic window scaling to ramp up.
                InitialHttp2StreamWindowSize = FileTransferLimits.StreamWindowSize,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
            };

            // gRPC-Web carries each call's status in the response body rather than in HTTP/2 trailers, which proxies
            // such as Cloudflare Tunnel drop. The server accepts both, and the cost over native gRPC is negligible.
            GrpcChannel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new VersionHandler(_versionPolicy, new AuthorizationHandler(token, transport))),
                MaxSendMessageSize = FileTransferLimits.MaximumMessageSize,
                MaxReceiveMessageSize = FileTransferLimits.MaximumMessageSize
            });
        }

        private readonly HttpVersionPolicy _versionPolicy;

        /// <summary>
        /// Over https, prefer HTTP/2 but accept HTTP/1.1 from proxies that only speak that (TLS negotiates it).
        /// Over plain http there is nothing to negotiate with, and the server only speaks HTTP/2, so require it.
        /// </summary>
        private static HttpVersionPolicy VersionPolicyFor(Uri address)
        {
            return address.Scheme == Uri.UriSchemeHttps ? HttpVersionPolicy.RequestVersionOrLower : HttpVersionPolicy.RequestVersionExact;
        }

        /// <summary>
        /// Applies the HTTP version policy to gRPC-Web requests, which otherwise default to HTTP/1.1.
        /// </summary>
        private sealed class VersionHandler(HttpVersionPolicy policy, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = policy;
                return base.SendAsync(request, cancellationToken);
            }
        }

        /// <summary>
        /// Adds the bearer token to every request, REST and gRPC alike.
        /// </summary>
        private sealed class AuthorizationHandler(string? token, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            private readonly AuthenticationHeaderValue? _header = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_header is not null) request.Headers.Authorization = _header;
                return base.SendAsync(request, cancellationToken);
            }
        }

        /// <summary>
        /// Sends a REST request and deserializes the response.
        /// </summary>
        /// <exception cref="NotLoggedInException">The server answered 401: there is no token, or it was revoked or
        /// expired. Thrown rather than returned so every command reports it the same way.</exception>
        protected virtual Task<ServerResult<TResult>> SendAsync<TResult>(HttpMethod method, string endpoint, object? body = null, CancellationToken cancellationToken = default)
        {
            return SendAsync<TResult>(method, endpoint, body, true, cancellationToken);
        }

        /// <param name="throwWhenUnauthorized">False returns a 401 as a failed result instead, for calls such as
        /// login where 401 means wrong credentials rather than a missing token.</param>
        protected async Task<ServerResult<TResult>> SendAsync<TResult>(HttpMethod method, string endpoint, object? body, bool throwWhenUnauthorized, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(method, endpoint);
            request.VersionPolicy = _versionPolicy;
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
                if (statusCode == 401 && throwWhenUnauthorized) throw new NotLoggedInException(ServerAddress);
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