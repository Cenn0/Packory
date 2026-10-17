using System.Text;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class OciChunkSafetyTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-chunks-" + Guid.NewGuid().ToString("N"));
        public Settings Settings { get; }
        public OciUploadStore Store { get; }
        public string Id { get; }
        public OciUploadStore.Ticket Ticket { get; }
        public string Metadata => Path.Combine(Root, "oci-staging", Id + ".json");
        public Fixture()
        {
            Settings = new Settings { DataDirectory = Root, MaxArtifactBytes = 4096 };
            Store = new OciUploadStore(Settings);
            Id = Store.Begin("team/artifact");
            Assert.True(Store.TryGet("team/artifact", Id, out var ticket));
            Ticket = Assert.IsType<OciUploadStore.Ticket>(ticket);
        }
        public async Task<long> Append(Stream input, string? range = null, long? length = null, long maximum = 4096)
        {
            await Ticket.Gate.WaitAsync();
            try { return await OciChunkWriter.AppendAsync(Store, Ticket, input, maximum, range, length, CancellationToken.None); }
            finally { Ticket.Gate.Release(); }
        }
        public Task<long> Append(string body, string? range = null, long? length = null, long maximum = 4096) =>
            Append(new MemoryStream(Encoding.UTF8.GetBytes(body)), range, length, maximum);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FailingStream(bool cancelled) : MemoryStream(new byte[] { 1, 2, 3, 4 })
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read)
                return ValueTask.FromException<int>(cancelled ? new OperationCanceledException() : new IOException("Synthetic disconnect"));
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], ct);
        }
    }

    [Fact]
    public async Task RangedChunksCommitOnlyCompleteBytesAndResumeAfterRestart()
    {
        using var f = new Fixture();
        Assert.Equal(3, await f.Append("abc", "0-2", 3));
        Assert.Equal(6, await f.Append("def", "3-5", 3));
        var restarted = new OciUploadStore(f.Settings);
        Assert.True(restarted.TryGet("team/artifact", f.Id, out var resumed));
        Assert.NotNull(resumed);
        Assert.Equal(6, resumed.CommittedLength);
        Assert.Equal("abcdef", File.ReadAllText(resumed.Path));
        Assert.False(restarted.TryGet("other/artifact", f.Id, out _));
    }

    [Theory]
    [InlineData("0-2")]
    [InlineData("4-6")]
    [InlineData("3-2")]
    [InlineData("bytes 3-5")]
    [InlineData("3-5/6")]
    [InlineData("3-5,3-5")]
    [InlineData(" 3-5")]
    [InlineData("3-5\n")]
    [InlineData("3-9223372036854775807")]
    [InlineData("3-999999999999999999999")]
    [InlineData("")]
    public async Task InvalidOrReplayedRangesNeverChangeAcknowledgedBytes(string range)
    {
        using var f = new Fixture();
        await f.Append("abc", "0-2", 3);
        await Assert.ThrowsAsync<OciChunkRangeException>(() => f.Append("def", range, 3));
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
        Assert.Equal(3, f.Ticket.CommittedLength);
    }

    [Fact]
    public async Task DeclaredRangeAndContentLengthMustAgreeBeforeWriting()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<OciChunkRangeException>(() => f.Append("abc", "0-2", 2));
        Assert.Equal(0, new FileInfo(f.Ticket.Path).Length);
    }

    [Theory]
    [InlineData("ab", "3-5")]
    [InlineData("abcd", "3-5")]
    public async Task TruncatedAndOverlongBodiesRollbackToCommittedOffset(string body, string range)
    {
        using var f = new Fixture();
        await f.Append("abc");
        await Assert.ThrowsAsync<OciChunkRangeException>(() => f.Append(body, range));
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
        Assert.Equal(3, f.Ticket.CommittedLength);
        Assert.Equal(6, await f.Append("def", "3-5", 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectAndCancellationRollbackWithoutUsingCancelledRequestToken(bool cancelled)
    {
        using var f = new Fixture();
        await f.Append("abc");
        using var input = new FailingStream(cancelled);
        if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => f.Append(input));
        else await Assert.ThrowsAsync<IOException>(() => f.Append(input));
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
        Assert.Equal(3, f.Ticket.CommittedLength);
        Assert.Equal(6, await f.Append("def", "3-5", 3));
    }

    [Fact]
    public async Task CapacityFailureDoesNotPoisonSessionOrChangeBytes()
    {
        using var f = new Fixture();
        await f.Append("abc");
        await Assert.ThrowsAsync<PayloadTooLargeException>(() => f.Append("defg", maximum: 6));
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
        Assert.Equal(6, await f.Append("def", "3-5", 3, 6));
    }

    [Fact]
    public async Task CrashBetweenPayloadFlushAndSnapshotCommitDiscardsOnlyUncommittedTail()
    {
        using var f = new Fixture();
        await f.Append("abc");
        File.AppendAllText(f.Ticket.Path, "unacknowledged");
        var restarted = new OciUploadStore(f.Settings);
        Assert.True(restarted.TryGet("team/artifact", f.Id, out var ticket));
        Assert.NotNull(ticket);
        Assert.Equal("abc", File.ReadAllText(ticket.Path));
        Assert.Equal(3, ticket.CommittedLength);
    }

    [Fact]
    public async Task TruncatedCommittedBytesAreNeverExtendedWithZeros()
    {
        using var f = new Fixture();
        await f.Append("abc");
        File.WriteAllText(f.Ticket.Path, "a");
        Assert.False(new OciUploadStore(f.Settings).TryGet("team/artifact", f.Id, out _));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Append("d"));
        Assert.Equal("a", File.ReadAllText(f.Ticket.Path));
        Assert.False(f.Store.TryGet("team/artifact", f.Id, out _));
    }

    [Fact]
    public async Task SnapshotCommitFailureRollsBackBytesAndPreservesAcknowledgedOffset()
    {
        using var f = new Fixture();
        await f.Append("abc");
        File.Move(f.Metadata, f.Metadata + ".saved");
        Directory.CreateDirectory(f.Metadata);
        var failure = await Record.ExceptionAsync(() => f.Append("def"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
        Assert.Equal(3, f.Ticket.CommittedLength);
        Directory.Delete(f.Metadata);
        File.Move(f.Metadata + ".saved", f.Metadata);
        Assert.Equal(6, await f.Append("def", "3-5", 3));
    }

    [Fact]
    public async Task ConcurrentReplayHasExactlyOneWinner()
    {
        using var f = new Fixture();
        async Task<bool> Attempt()
        {
            try { await f.Append("abc", "0-2", 3); return true; }
            catch (OciChunkRangeException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, result => result);
        Assert.Equal("abc", File.ReadAllText(f.Ticket.Path));
    }

    [Fact]
    public async Task DeletedTicketCannotRecreateStagingFiles()
    {
        using var f = new Fixture();
        await f.Append("abc");
        f.Store.End(f.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Append("def"));
        Assert.False(File.Exists(f.Ticket.Path));
        Assert.False(File.Exists(f.Metadata));
    }

    [Fact]
    public void LegacyAndMalformedSnapshotMetadataRemainUntrustedAndPreserved()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Metadata, JsonSerializer.Serialize(new
        { Repository = "team/artifact", LastTouched = DateTimeOffset.UtcNow }));
        Assert.False(new OciUploadStore(f.Settings).TryGet("team/artifact", f.Id, out _));
        Assert.True(File.Exists(f.Metadata));
        File.WriteAllText(f.Metadata, "{\"Schema\":2,\"CommittedLength\":0,\"Repository\":null}");
        Assert.False(new OciUploadStore(f.Settings).TryGet("team/artifact", f.Id, out _));
        Assert.True(File.Exists(f.Ticket.Path));
    }

    [Theory]
    [InlineData("team/a__b", true)]
    [InlineData("team/a--b", true)]
    [InlineData("team/a___b", false)]
    [InlineData("team/a\n", false)]
    public void RepositoryNamesMatchWholeOciGrammar(string name, bool valid) =>
        Assert.Equal(valid, OciEndpoints.ValidRepository(name));
}
