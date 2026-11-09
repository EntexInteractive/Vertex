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
                BaseAddress = new Uri(url.TrimEnd('/') + "/")
            };
            
            GrpcChannel = GrpcChannel.ForAddress(url);
        }

        protected virtual async Task<ServerResult<T>> SendAsync<T>(HttpMethod method, string endpoint, object? body = null, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new(method, endpoint);
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
                return ServerResult<T>.Failure(ex, (int?)ex.StatusCode ?? 0);
            }

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return ServerResult<T>.Failure(new HttpRequestException(string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content, null, response.StatusCode), statusCode);
                }

                try
                {
                    T? data = JsonConvert.DeserializeObject<T>(content);
                    return data is null ? ServerResult<T>.Failure(new JsonSerializationException("Server returned an empty response."), statusCode) : ServerResult<T>.Success(data, statusCode);
                }
                catch (JsonException ex)
                {
                    return ServerResult<T>.Failure(ex, statusCode);
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