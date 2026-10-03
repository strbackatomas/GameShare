using System.Security.Cryptography;

namespace GameShare.Storage;

public sealed record ScannedFile(string Path, long Size, string Sha256);

/// <param name="FolderName">Name of the scanned directory. Becomes the torrent name and install folder.</param>
/// <param name="PieceHashes">Concatenated 20-byte SHA-1 of each piece, as BitTorrent v1 needs them.</param>
/// <param name="VolatilePatterns">Patterns that were applied. Files matching them are not in <paramref name="Files"/>.</param>
public sealed record ScanResult(
    string FolderName,
    IReadOnlyList<ScannedFile> Files,
    long TotalSize,
    int PieceLength,
    byte[] PieceHashes,
    IReadOnlyList<string> VolatilePatterns);

/// <summary>
/// Reads a game directory once and produces everything identity and transport need:
/// SHA-256 per file (manifest) and SHA-1 per piece (torrent). One pass means the disk is read only once.
/// </summary>
public static class ContentScanner
{
    /// <summary>Launch metadata file. Excluded from content so editing it never changes game identity.</summary>
    public const string DefinitionFileName = "gameshare.json";

    private const int MinPieceLength = 1 << 20;   // 1 MiB
    private const int MaxPieceLength = 16 << 20;  // 16 MiB
    private const int TargetPieceCount = 2048;
    private const int ReadBufferSize = 4 << 20;

    /// <summary>Chooses a piece length so that large games end up with a few thousand pieces.</summary>
    public static int ChoosePieceLength(long totalSize)
    {
        long wanted = totalSize / TargetPieceCount;
        int len = MinPieceLength;
        while (len < MaxPieceLength && len < wanted) len <<= 1;
        return len;
    }

    /// <summary>Content files below <paramref name="root"/>, ordinal by relative path, forward slashes.</summary>
    internal static List<(string FullPath, string RelativePath, long Size)> EnumerateContent(string root, VolatileMatcher? volatileFiles = null)
    {
        // Reparse points (junctions, symlinks) are skipped so we never follow a loop or leave the game directory.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        return Directory.EnumerateFiles(root, "*", options)
            .Select(p => (FullPath: p, RelativePath: System.IO.Path.GetRelativePath(root, p).Replace('\\', '/')))
            .Where(f => !string.Equals(f.RelativePath, DefinitionFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => volatileFiles is null || !volatileFiles.IsMatch(f.RelativePath))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .Select(f => (f.FullPath, f.RelativePath, new FileInfo(f.FullPath).Length))
            .ToList();
    }

    /// <summary>How many threads hash torrent pieces. Pieces are independent, so they spread well; more than four rarely helps.</summary>
    private static int PieceLanes => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>How many threads hash whole files. One file is one hash and cannot be split, so this helps games with many files.</summary>
    private static int FileLanes => Environment.ProcessorCount >= 4 ? 2 : 1;

    /// <summary>Read buffers in flight. Bounds the memory and how far reading may run ahead of hashing.</summary>
    private const int BuffersInFlight = 12;

    /// <summary>A read buffer, shared by the piece lane and the file lane that hash it, and reused once both are done.</summary>
    private sealed class Chunk(int size)
    {
        public readonly byte[] Data = new byte[size];
        public int Length;
        public int Pending;
    }

    /// <summary>Data to append, or, with no chunk, the end of piece or file <see cref="Finish"/>.</summary>
    private readonly record struct Work(Chunk? Chunk, int Finish);

    /// <summary>
    /// Hashes a game directory. The disk is read once, by one reader, in order, which is what a hard disk wants; the hashing that
    /// follows runs on several threads, since on a fast disk the CPU is what limits. Each piece goes whole to one of the piece lanes
    /// and each file whole to one of the file lanes, so every hash sees its bytes in order and the result is exactly that of a plain
    /// single-threaded pass.
    /// </summary>
    public static async Task<ScanResult> ScanAsync(
        string directory,
        int? pieceLength = null,
        IProgress<long>? bytesHashed = null,
        CancellationToken cancellationToken = default,
        VolatileMatcher? volatileFiles = null)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Cannot scan game, directory does not exist: {directory}");

        var root = System.IO.Path.GetFullPath(directory);
        var entries = EnumerateContent(root, volatileFiles);
        if (entries.Count == 0)
            throw new InvalidOperationException($"Cannot scan game, directory contains no files: {root}");

        long total = entries.Sum(e => e.Size);
        int pieceLen = pieceLength ?? ChoosePieceLength(total);
        if (pieceLen < 16 * 1024 || (pieceLen & (pieceLen - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceLength), pieceLen, "Piece length must be a power of two and at least 16 KiB.");

        int pieceCount = checked((int)((total + pieceLen - 1) / pieceLen));
        var pieceHashes = new byte[pieceCount * 20];
        var fileHashes = new string[entries.Count];

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = System.Threading.Channels.Channel.CreateUnbounded<Chunk>();
        int chunkSize = Math.Min(pieceLen, ReadBufferSize);
        for (int i = 0; i < BuffersInFlight; i++) free.Writer.TryWrite(new Chunk(chunkSize));

        var pieceLanes = Enumerable.Range(0, PieceLanes).Select(_ => System.Threading.Channels.Channel.CreateUnbounded<Work>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true })).ToArray();
        var fileLanes = Enumerable.Range(0, FileLanes).Select(_ => System.Threading.Channels.Channel.CreateUnbounded<Work>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true })).ToArray();

        var workers = pieceLanes.Select(lane => RunLaneAsync(lane.Reader, HashAlgorithmName.SHA1, free.Writer, stop,
                (index, hash) => hash.CopyTo(pieceHashes.AsSpan(index * 20, 20))))
            .Concat(fileLanes.Select(lane => RunLaneAsync(lane.Reader, HashAlgorithmName.SHA256, free.Writer, stop,
                (index, hash) => fileHashes[index] = Convert.ToHexString(hash).ToLowerInvariant())))
            .ToList();

        int pieceIndex = 0;
        try
        {
            int filled = 0;   // bytes already sent into the current piece
            long done = 0;
            for (int f = 0; f < entries.Count; f++)
            {
                var (fullPath, _, size) = entries[f];
                var fileLane = fileLanes[f % fileLanes.Length].Writer;
                await using var fs = new FileStream(fullPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 0,
                });

                long remaining = size;
                while (remaining > 0)
                {
                    // Never read across a piece boundary, so a piece is completed exactly when filled == pieceLen.
                    int want = (int)Math.Min(Math.Min(chunkSize, remaining), pieceLen - filled);
                    var chunk = await free.Reader.ReadAsync(stop.Token).ConfigureAwait(false);
                    int read = await fs.ReadAsync(chunk.Data.AsMemory(0, want), stop.Token).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException($"File shrank while hashing: {fullPath}");

                    chunk.Length = read;
                    chunk.Pending = 2;
                    var pieceLane = pieceLanes[pieceIndex % pieceLanes.Length].Writer;
                    pieceLane.TryWrite(new Work(chunk, 0));
                    fileLane.TryWrite(new Work(chunk, 0));
                    filled += read;
                    remaining -= read;
                    done += read;

                    if (filled == pieceLen)
                    {
                        pieceLane.TryWrite(new Work(null, pieceIndex));
                        pieceIndex++;
                        filled = 0;
                    }
                    bytesHashed?.Report(done);
                }
                fileLane.TryWrite(new Work(null, f)); // also for an empty file, whose hash is that of no bytes
            }

            if (filled > 0)
            {
                pieceLanes[pieceIndex % pieceLanes.Length].Writer.TryWrite(new Work(null, pieceIndex));
                pieceIndex++;
            }
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var lane in pieceLanes.Concat(fileLanes)) lane.Writer.TryComplete();
            // A lane that failed cancelled the reader; its own exception is the one worth seeing, so it is thrown from here.
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { /* the reader's exception is on its way */ }
        }

        if (pieceIndex != pieceCount)
            throw new InvalidOperationException($"Internal error: expected {pieceCount} pieces but hashed {pieceIndex}.");

        var files = entries.Select((e, i) => new ScannedFile(e.RelativePath, e.Size, fileHashes[i])).ToList();
        return new ScanResult(new DirectoryInfo(root).Name, files, total, pieceLen, pieceHashes, volatileFiles?.Patterns ?? []);
    }

    /// <summary>One hashing thread: appends what comes in, stores a hash at each finish, hands buffers back when both lanes are done.</summary>
    private static Task RunLaneAsync(
        System.Threading.Channels.ChannelReader<Work> work, HashAlgorithmName algorithm, System.Threading.Channels.ChannelWriter<Chunk> free,
        CancellationTokenSource stop, Action<int, byte[]> store) =>
        Task.Run(async () =>
        {
            try
            {
                using var hasher = IncrementalHash.CreateHash(algorithm);
                await foreach (var w in work.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    if (w.Chunk is { } chunk)
                    {
                        hasher.AppendData(chunk.Data, 0, chunk.Length);
                        if (Interlocked.Decrement(ref chunk.Pending) == 0) free.TryWrite(chunk);
                    }
                    else store(w.Finish, hasher.GetHashAndReset());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        });
}
