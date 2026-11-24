using Amazon.S3;
using Amazon.S3.Model;

namespace Ytp.Cache;

/// <summary>
/// Read-only, seekable view of an object. Each read after a seek opens a ranged GET from the
/// current position, so HTTP range requests to ytp map onto range requests to the bucket.
/// </summary>
public sealed class S3RangeStream(IAmazonS3 client, string bucket, string key, long length) : Stream
{
    private GetObjectResponse? response;
    private long position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        if (target != position)
        {
            CloseResponse();
            position = target;
        }
        return position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0 || position >= length)
        {
            return 0;
        }

        response ??= await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(position, length - 1)
        }, ct);

        var read = await response.ResponseStream.ReadAsync(buffer, ct);
        if (read == 0)
        {
            throw new IOException($"Object {key} ended at {position} of {length} bytes.");
        }
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseResponse();
        }
        base.Dispose(disposing);
    }

    private void CloseResponse()
    {
        response?.Dispose();
        response = null;
    }
}
