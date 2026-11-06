namespace Screenbox.Core.Tests.Helpers;

/// <summary>
/// A read-only seekable stream wrapper that counts the bytes actually read through it, so a test
/// can prove that a reader skipped data by seeking rather than by reading it.
/// </summary>
internal sealed partial class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
