// Copyright 2026 Entex Interactive

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Vertex.Gui.Hosting;

namespace Vertex.Gui.Controllers
{
    [ApiController]
    [Route("api/events")]
    public sealed class EventsController : ControllerBase
    {
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        private readonly HostEvents _events;
        private readonly JsonSerializerOptions _json;

        public EventsController(HostEvents events, IOptions<JsonOptions> json)
        {
            _events = events;
            _json = json.Value.JsonSerializerOptions;
        }

        /// <summary>
        /// Streams host events as Server-Sent Events (<c>event: &lt;type&gt;</c>, <c>data: &lt;json&gt;</c>) until the
        /// page disconnects. A comment line every 15 seconds keeps the connection alive and lets either side notice a
        /// dead one.
        /// </summary>
        [HttpGet]
        [Produces("text/event-stream")]
        public async Task Stream(CancellationToken cancellationToken)
        {
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";

            await WriteAsync(": connected\n\n", cancellationToken);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IAsyncEnumerator<HostEvent> events = _events.SubscribeAsync(stop.Token).GetAsyncEnumerator(stop.Token);
            Task<bool> next = events.MoveNextAsync().AsTask();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using CancellationTokenSource heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task delay = Task.Delay(HeartbeatInterval, heartbeat.Token);
                    if (await Task.WhenAny(next, delay) != next)
                    {
                        await WriteAsync(": heartbeat\n\n", cancellationToken);
                        continue;
                    }

                    await heartbeat.CancelAsync();
                    if (!await next) break;

                    HostEvent hostEvent = events.Current;
                    string data = JsonSerializer.Serialize(hostEvent.Data, _json);
                    await WriteAsync($"event: {hostEvent.Type}\ndata: {data}\n\n", cancellationToken);
                    next = events.MoveNextAsync().AsTask();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The page went away.
            }
            finally
            {
                // An iterator can't be disposed while a MoveNext is pending, so end it first.
                await stop.CancelAsync();
                try
                {
                    await next;
                }
                catch (OperationCanceledException)
                {
                }

                await events.DisposeAsync();
            }
        }

        private async Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
