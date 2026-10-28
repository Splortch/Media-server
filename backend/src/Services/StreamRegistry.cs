using System.Collections.Concurrent;

namespace MediaServer.Services;

public record ActiveStream(Guid Id, int UserId, int MediaId, DateTime StartedAt, long BytesRead);

public sealed class StreamRegistry
{
    private readonly ConcurrentDictionary<Guid, CountingStream> streams = new();

    public ActiveStream[] Snapshot() =>
        streams
            .Values.Select(x => new ActiveStream(
                x.Id,
                x.UserId,
                x.MediaId,
                x.StartedAt,
                x.BytesRead
            ))
            .ToArray();

    public Stream Track(Stream source, int user, int media)
    {
        var stream = new CountingStream(source, user, media, this);
        streams[stream.Id] = stream;
        return stream;
    }

    private sealed class CountingStream(Stream source, int user, int media, StreamRegistry owner)
        : Stream
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int UserId => user;
        public int MediaId => media;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        private long bytes;
        public long BytesRead => Interlocked.Read(ref bytes);
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            Interlocked.Add(ref bytes, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default
        )
        {
            var read = await source.ReadAsync(buffer, ct);
            Interlocked.Add(ref bytes, read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct
        )
        {
            var read = await source.ReadAsync(buffer.AsMemory(offset, count), ct);
            Interlocked.Add(ref bytes, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);

        public override void Flush() => source.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            owner.streams.TryRemove(Id, out _);
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            owner.streams.TryRemove(Id, out _);
            await source.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
