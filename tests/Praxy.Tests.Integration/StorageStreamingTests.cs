using System.Net.Http.Headers;
using System.Security.Cryptography;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The landmine the design doc calls out by name: it is very easy to write code that *looks*
/// streaming but buffers. These tests move files far larger than any incidental buffer through the
/// real pipeline — generated on the fly and hashed on the way back, so the test itself never holds
/// one either — and watch the managed heap while it happens.
///
/// The assertion is deliberately about **growth with respect to file size**, not an absolute
/// number of megabytes. An absolute bound is not a stable gate: the same round trip measured 19 MB
/// in isolation and 65 MB when run after 285 other integration tests, because
/// <c>GC.GetTotalMemory(false)</c> counts whatever garbage the rest of the suite left uncollected.
/// What *is* stable — and is the actual property being claimed — is that quadrupling the file does
/// not quadruple the memory. A <c>ReadToEndAsync</c> anywhere on the path, or a buffered response,
/// fails that immediately; noise cannot.
/// </summary>
public class StorageStreamingTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    private const long SmallBytes = 32L * 1024 * 1024;
    private const long LargeBytes = 128L * 1024 * 1024;

    /// <summary>
    /// How much extra retained heap the 96 MB of *additional* file is allowed to cost. A buffering
    /// implementation holds the whole file, so it would spend the full extra 96 MB (more, with a
    /// doubling MemoryStream); a streaming one spends about nothing, because its working set is a
    /// chunk plus transport buffers either way.
    ///
    /// <para>Was 48 MB — half the difference — when <see cref="HeapPeakSampler"/> measured total
    /// heap and therefore had to leave room for however much garbage happened to be pending. Now
    /// that it samples live bytes (see its own remarks, and the CI flake that prompted it), the
    /// measurement is not noisy in that way: the same round trip measured under 1 MB of extra
    /// growth on three consecutive runs. 8 MB keeps eight times that headroom while being twelve
    /// times tighter than the old budget — enough to catch a path that buffers only *part* of a
    /// file, which 48 MB would have let through.</para>
    /// </summary>
    private const long AcceptableExtraGrowthBytes = 8L * 1024 * 1024;

    protected override IDictionary<string, string?>? ExtraSettings =>
        new Dictionary<string, string?>(base.ExtraSettings!)
        {
            ["Praxy:Quotas:MaxFileSizeBytes"] = "268435456",
            ["Praxy:Quotas:MaxStorageBytesPerProject"] = "1073741824",
            ["Praxy:RateLimits:DataPlane:PermitLimit"] = "100000",
        };

    [Fact]
    public async Task Files_round_trip_exactly_and_memory_does_not_grow_with_their_size()
    {
        var (operatorToken, projectId) = await SetupProjectAsync();
        var bucketId = await CreateBucketAsync(operatorToken, projectId);

        var small = await RoundTripAsync(operatorToken, projectId, bucketId, "small.bin", SmallBytes);
        var large = await RoundTripAsync(operatorToken, projectId, bucketId, "large.bin", LargeBytes);

        // Correctness first: both files came back byte-for-byte, with the server's own streamed
        // checksum matching one taken independently, and the expected number of 512 KiB chunks.
        Assert.Equal(64, small.ChunkCount);
        Assert.Equal(256, large.ChunkCount);

        var extra = large.PeakGrowthBytes - small.PeakGrowthBytes;
        Assert.True(extra < AcceptableExtraGrowthBytes,
            $"A {(LargeBytes - SmallBytes) / 1024 / 1024} MB larger file cost {extra / 1024 / 1024} MB more "
            + $"peak heap ({small.PeakGrowthBytes / 1024 / 1024} MB -> {large.PeakGrowthBytes / 1024 / 1024} MB). "
            + "Memory is tracking file size, so something on the upload or download path is buffering "
            + "rather than streaming.");
    }

    // ---- one measured round trip ---------------------------------------------------------------

    private sealed record RoundTrip(int ChunkCount, long PeakGrowthBytes);

    /// <summary>
    /// Uploads a generated file, downloads it back hashing as it arrives, asserts it is identical,
    /// and reports the peak managed-heap growth over a freshly settled baseline.
    /// </summary>
    private async Task<RoundTrip> RoundTripAsync(
        string operatorToken, string projectId, string bucketId, string name, long length)
    {
        var expectedHash = HashOfGeneratedBytes(length);
        var baseline = SettledHeapBytes();
        using var peak = new HeapPeakSampler();

        var upload = Authed(
            HttpMethod.Post,
            $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files?name={name}",
            operatorToken);
        upload.Content = new StreamContent(new GeneratedStream(length));
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Content.Headers.ContentLength = length;

        var uploadResponse = await Client.SendAsync(upload);
        Assert.Equal(201, (int)uploadResponse.StatusCode);
        var uploaded = await ReadJson(uploadResponse);
        var fileId = uploaded.GetProperty("id").GetString()!;
        Assert.Equal(length, uploaded.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(expectedHash, uploaded.GetProperty("checksum").GetString());

        var download = await Client.SendAsync(
            Authed(HttpMethod.Get,
                $"/v1/console/projects/{projectId}/storage/buckets/{bucketId}/files/{fileId}/download",
                operatorToken),
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(200, (int)download.StatusCode);
        Assert.Equal(length, download.Content.Headers.ContentLength);

        await using (var body = await download.Content.ReadAsStreamAsync())
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65_536];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                hasher.AppendData(buffer.AsSpan(0, read));
                total += read;
            }
            Assert.Equal(length, total);
            Assert.Equal(expectedHash, Convert.ToHexStringLower(hasher.GetHashAndReset()));
        }

        return new RoundTrip(uploaded.GetProperty("chunkCount").GetInt32(), peak.PeakBytes - baseline);
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

        /// <summary>
        /// Samples **live** bytes (<c>forceFullCollection: true</c>), not total heap, and every
        /// 100ms rather than every 10ms. Both matter, and the first is why this test used to flake.
        ///
        /// <para><c>GC.GetTotalMemory(false)</c> counts uncollected garbage, while the baseline it
        /// is subtracted from comes from <see cref="SettledHeapBytes"/>, which collects first — so
        /// the reported "growth" silently included whatever garbage happened to be pending. That
        /// amount scales with how long the operation ran and how the runtime is tuned, not with
        /// what the code retains: the 128 MB round trip runs longer than the 32 MB one, gets
        /// sampled more, and is likelier to catch a garbage high-water mark, biasing precisely the
        /// difference this test asserts on. It failed in CI at 54 MB against a 48 MB budget while
        /// passing locally on identical code, and had flaked once before that.</para>
        ///
        /// <para>Collecting before each sample makes both ends of the subtraction mean the same
        /// thing — retained bytes — which is the property actually being claimed. The coarser
        /// interval keeps the cost of those collections reasonable, and loses nothing: a buffering
        /// implementation holds the whole file for the *entire* transfer, so it is caught by any
        /// sampling rate. 10ms resolution was only ever needed to catch garbage spikes, which are
        /// no longer what is being measured.</para>
        /// </summary>
        public HeapPeakSampler()
        {
            _peak = GC.GetTotalMemory(forceFullCollection: true);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var now = GC.GetTotalMemory(forceFullCollection: true);
                    if (now > _peak) _peak = now;
                    try { await Task.Delay(100, _cts.Token); }
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
    /// exists anywhere — so if the heap grows with its size, the growth is the server's, not the
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
