using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZulipQQ.Zulip;

/// <summary>上传时报告已发送字节数的 <see cref="HttpContent"/>。</summary>
public sealed class ProgressStreamContent(Stream source, IProgress<long>? progress) : HttpContent
{
    private const int BufferSize = 81920;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long sent = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;
            progress?.Report(sent);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (source.CanSeek)
        {
            length = source.Length - source.Position;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            source.Dispose();
        base.Dispose(disposing);
    }
}
