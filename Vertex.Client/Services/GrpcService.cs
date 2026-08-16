// Copyright 2026 Entex Interactive

using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vertex.Server;

namespace Vertex.Client.Services
{
    public sealed class GrpcService : IDisposable
    {
        private readonly ILogger<GrpcService> _logger;
        private readonly GrpcChannel _channel;
        private readonly Greeter.GreeterClient _client;

        public GrpcService(ILogger<GrpcService> logger, IOptions<ClientSettings> settings)
        {
            _logger = logger;

            string address = settings.Value.ServerAddress;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? serverAddress))
            {
                throw new InvalidOperationException($"Client:ServerAddress is not a valid absolute URI: '{address}'.");
            }

            _channel = GrpcChannel.ForAddress(serverAddress);
            _client = new Greeter.GreeterClient(_channel);
        }

        public async Task<string> SayHelloAsync(string name, CancellationToken cancellationToken = default)
        {
            HelloReply reply = await _client.SayHelloAsync(
                new HelloRequest { Name = name },
                cancellationToken: cancellationToken);

            return reply.Message;
        }

        public void Dispose()
        {
            _channel.Dispose();
        }
    }
}
