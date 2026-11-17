// Copyright 2026 Entex Interactive

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Newtonsoft.Json;
using Vertex.Server.Telemetry;

namespace Vertex.Server.Database
{
    /// <summary>
    /// Serializes cached values with Newtonsoft.Json, the serializer the models (<c>VProject</c>, <c>VBranch</c>, ...)
    /// are annotated for and the REST API uses, so a value read from Redis is built through the same constructor as
    /// one read from the API. HybridCache's own serializer (System.Text.Json) ignores those attributes.
    /// </summary>
    public sealed class NewtonsoftCacheSerializerFactory : IHybridCacheSerializerFactory
    {
        public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
        {
            // HybridCache stores strings and byte arrays as they are.
            if (typeof(T) == typeof(string) || typeof(T) == typeof(byte[]))
            {
                serializer = null;
                return false;
            }

            serializer = new Serializer<T>();
            return true;
        }

        private sealed class Serializer<T> : IHybridCacheSerializer<T>
        {
            private static readonly JsonSerializer Json = JsonSerializer.CreateDefault();
            private static readonly KeyValuePair<string, object?> TypeTag = VertexMetrics.Tag("type", typeof(T).Name);

            public T Deserialize(ReadOnlySequence<byte> source)
            {
                using StreamReader reader = new(new MemoryStream(source.ToArray()), Encoding.UTF8);
                using JsonTextReader json = new(reader);
                return Json.Deserialize<T>(json)!;
            }

            public void Serialize(T value, IBufferWriter<byte> target)
            {
                using StringWriter writer = new();
                Json.Serialize(writer, value);
                byte[] bytes = Encoding.UTF8.GetBytes(writer.ToString());
                VertexMetrics.CacheValueSize.Record(bytes.Length, TypeTag);
                target.Write(bytes);
            }
        }
    }
}
