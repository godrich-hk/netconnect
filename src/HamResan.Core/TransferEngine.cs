using System.Buffers;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;

namespace HamResan.Core;

/// <summary>One offer per authenticated TLS connection; no file bytes are sent before acceptance.</summary>
public static class TransferEngine
{
    private const int BufferSize = 256 * 1024;

    private static void RequireTls(SslStream tls)
    {
        if (!tls.IsAuthenticated || !tls.IsEncrypted || !tls.IsMutuallyAuthenticated)
            throw new InvalidOperationException("Mutually authenticated TLS is required.");
    }

    public static async Task<bool> SendAsync(PinnedConnection connection, IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        SslStream tls = connection.Stream;
        RequireTls(tls);
        var offer = new Offer(Protocol.Version, Guid.NewGuid(), files.Select(f => f.Metadata).ToArray());
        OfferValidator.Validate(offer, long.MaxValue);
        await Protocol.WriteAsync(tls, offer, ct);
        Decision decision = await Protocol.ReadAsync<Decision>(tls, ct);
        if (decision.Version != Protocol.Version) throw new InvalidDataException("Unsupported protocol version.");
        if (!decision.Accepted) return false;
        for (int i = 0; i < files.Count; i++)
        {
            await using Stream input = await files[i].OpenReadAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyExactlyAsync(input, tls, files[i].Metadata.Size, hash, offer.TransferId, i, progress, ct);
            await Protocol.WriteAsync(tls, new Digest(Convert.ToHexString(hash.GetHashAndReset())), ct);
            if (!(await Protocol.ReadAsync<FileResult>(tls, ct)).Accepted)
                throw new InvalidDataException("Receiver rejected file integrity.");
        }
        if (!(await Protocol.ReadAsync<TransferResult>(tls, ct)).Completed)
            throw new InvalidDataException("Receiver did not confirm transfer.");
        return true;
    }

    /// <summary>Approval must be supplied by the caller, and is invoked before any file is created.</summary>
    public static async Task<bool> ReceiveAsync(PinnedConnection connection, string directory, long maxTotalBytes,
        Func<Offer, CancellationToken, Task<bool>> approve, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        SslStream tls = connection.Stream;
        RequireTls(tls);
        Offer offer = await Protocol.ReadAsync<Offer>(tls, ct);
        OfferValidator.Validate(offer, maxTotalBytes);
        bool accepted = await approve(offer, ct);
        await Protocol.WriteAsync(tls, new Decision(Protocol.Version, accepted), ct);
        if (!accepted) return false;
        Directory.CreateDirectory(directory);
        for (int i = 0; i < offer.Files.Count; i++)
        {
            FileOffer file = offer.Files[i];
            string temporary = Path.Combine(directory, ".hamresan-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    await CopyExactlyAsync(tls, output, file.Size, hash, offer.TransferId, i, progress, ct);
                    await output.FlushAsync(ct);
                }
                Digest digest = await Protocol.ReadAsync<Digest>(tls, ct);
                byte[] expected;
                try { expected = Convert.FromHexString(digest.Sha256); }
                catch (Exception e) when (e is FormatException or ArgumentNullException) { throw new InvalidDataException("Invalid digest.", e); }
                if (expected.Length != 32 || !CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                {
                    await Protocol.WriteAsync(tls, new FileResult(false), ct);
                    throw new InvalidDataException("File integrity check failed.");
                }
                SafeFiles.Commit(temporary, directory, file.Name);
                await Protocol.WriteAsync(tls, new FileResult(true), ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await Protocol.WriteAsync(tls, new TransferResult(true), ct);
        return true;
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long size, IncrementalHash hash,
        Guid transferId, int index, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var clock = Stopwatch.StartNew();
        long done = 0;
        try
        {
            while (done < size)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, size - done)), ct);
                if (read == 0) throw new EndOfStreamException("File ended before its advertised size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(new TransferProgress(transferId, index, done, size, clock.Elapsed));
            }
            if (size == 0) progress?.Report(new TransferProgress(transferId, index, 0, 0, clock.Elapsed));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
