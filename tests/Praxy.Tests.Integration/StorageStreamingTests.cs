using System.Net.Http.Headers;
using System.Security.Cryptography;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The landmine the design doc calls out by name: it is very easy to write code that *looks*
/// streaming but buffers. These tests move a file far larger than any incidental buffer through
/// the real pipeline — generated on the fly and hashed on the way back, so the test itself never
/// holds it either — and watch the managed heap while it happens. A <c>ReadToEndAsync</c> anywhere
/// on the path, or a buffered response, shows up here as a heap that grows with the file.
/// </summary>
public class StorageStreamingTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const long FileBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Generously loose on purpose, and well below <see cref="FileBytes"/> — that is the whole
    /// assertion: an implementation holding the file cannot come in under a third of its size. A
    /// streaming one holds a chunk (512 KiB) plus transport buffers; the rest of the headroom is
    /// uncollected garbage from moving 128 MB, which <c>GC.GetTotalMemory(false)</c> counts.
    /// </summary>
    private const long AcceptableHeapGrowthBytes = 48L * 1024 * 1024;

    protected override IDictionary<string, string?>? ExtraSettings =>
        new Dictionary<string, string?>(base.ExtraSettings!)
        {
            ["Praxy:Quotas:MaxFileSizeBytes"] = "268435456",
            ["Praxy:Quotas:MaxStorageBytesPerProject"] = "1073741824",
            ["Praxy:RateLimits:DataPlane:PermitLimit"] = "100000",
        };

    [Fact]
    public async Task A_128MB_file_round_trips_exactly_without_ever_being_held_in_memory()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId);

        var expectedHash = HashOfGeneratedBytes(FileBytes);
        var baseline = SettledHeapBytes();
        using var peak = new HeapPeakSampler();

        // ---- upload: a generated stream, never a byte[] ----
        var upload = Authed(
            HttpMethod.Post,
            $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files?name=large.bin",
            operatorToken);
        upload.Content = new StreamContent(new GeneratedStream(FileBytes));
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Content.Headers.ContentLength = FileBytes;

        var uploaded = await ReadJson(await Client.SendAsync(upload));
        var fileId = uploaded.GetProperty("id").GetString()!;
        Assert.Equal(FileBytes, uploaded.GetProperty("sizeBytes").GetInt64());
        // The server's own checksum, computed while streaming, matches one taken independently.
        Assert.Equal(expectedHash, uploaded.GetProperty("checksum").GetString());
        Assert.Equal(256, uploaded.GetProperty("chunkCount").GetInt32()); // 128 MB / 512 KiB

        // ---- download: read the body incrementally and hash it as it arrives ----
        var download = await Client.SendAsync(
            Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files/{fileId}/download",
                operatorToken),
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(200, (int)download.StatusCode);
        Assert.Equal(FileBytes, download.Content.Headers.ContentLength);

        await using var body = await download.Content.ReadAsStreamAsync();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
            total += read;
        }

        Assert.Equal(FileBytes, total);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(hasher.GetHashAndReset()));

        var growth = peak.PeakBytes - baseline;
        Assert.True(growth < AcceptableHeapGrowthBytes,
            $"Managed heap grew by {growth / 1024 / 1024} MB moving a {FileBytes / 1024 / 1024} MB file — "
            + "something on the upload or download path is buffering it rather than streaming.");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<string> CreateBucketAsync(string operatorToken, string projectId)
    {
        var response = await Client.SendAsync(Authed(
            HttpMethod.Post, $"/v1/console/projects/{projectId}/storage/buckets", operatorToken,
            new { key = "big", name = "Big", maxFileSizeBytes = 268_435_456L }));
        Assert.Equal(201, (int)response.StatusCode);
        return (await ReadJson(response)).GetProperty("id").GetString()!;
    }

    private static string HashOfGeneratedBytes(long length)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536];
        long produced = 0;
        while (produced < length)
        {
            var take = (int)Math.Min(buffer.Length, length - produced);
            GeneratedStream.Fill(buffer.AsSpan(0, take), produced);
            hasher.AppendData(buffer.AsSpan(0, take));
            produced += take;
        }
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static long SettledHeapBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>Polls the managed heap on a background loop so the *peak* during the transfer is seen, not just the size once it is over.</summary>
    private sealed class HeapPeakSampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private long _peak;

        public HeapPeakSampler()
        {
            _peak = GC.GetTotalMemory(forceFullCollection: false);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var now = GC.GetTotalMemory(forceFullCollection: false);
                    if (now > _peak) _peak = now;
                    try { await Task.Delay(10, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            });
        }

        public long PeakBytes => _peak;

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* cancellation */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// A read-only stream that synthesizes deterministic bytes on demand. Nothing of the "file"
    /// exists anywhere — so if the heap grows by its size, the growth is the server's, not the
    /// test's.
    /// </summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;

        public static void Fill(Span<byte> destination, long offset)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var index = offset + i;
                destination[i] = (byte)((index * 31 + (index >> 8) * 17) & 0xFF);
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            var take = (int)Math.Min(destination.Length, length - _position);
            if (take <= 0) return 0;
            Fill(destination[..take], _position);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
