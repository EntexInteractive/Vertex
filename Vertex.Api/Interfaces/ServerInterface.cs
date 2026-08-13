// Copyright 2026 Entex Interactive

namespace Vertex.Api.Interfaces
{
    public sealed class ServerInterface(string url)
    {
        private readonly HttpClient _client = new();
        private readonly string _url = url;

        public async Task<string> GetVersionAsync()
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{_url}/api/v1/server/version");
            using HttpResponseMessage response = await _client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
    }
}