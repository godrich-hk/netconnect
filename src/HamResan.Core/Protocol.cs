using System.Buffers.Binary;
using System.Text.Json;

namespace HamResan.Core;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxControlBytes = 1024 * 1024;
    public const int MaxFiles = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length is 0 or > MaxControlBytes) throw new InvalidDataException("Control message size is invalid.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaxControlBytes) throw new InvalidDataException("Control message size is invalid.");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty control message."); }
        catch (JsonException e) { throw new InvalidDataException("Malformed control message.", e); }
    }
}

public sealed record FileOffer(string Name, long Size, string? MimeType);
public sealed record Offer(int Version, Guid TransferId, IReadOnlyList<FileOffer> Files);
public sealed record Decision(int Version, bool Accepted);
public sealed record Digest(string Sha256);
public sealed record FileResult(bool Accepted);
public sealed record TransferResult(bool Completed);
public sealed record OutgoingFile(FileOffer Metadata, Func<Stream> OpenRead);
public sealed record TransferProgress(Guid TransferId, int FileIndex, long BytesTransferred, long FileSize, TimeSpan Elapsed);

public static class OfferValidator
{
    public static long Validate(Offer offer, long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (maxTotalBytes < 0 || offer.Version != Protocol.Version || offer.TransferId == Guid.Empty ||
            offer.Files is null || offer.Files.Count is 0 or > Protocol.MaxFiles)
            throw new InvalidDataException("Unsupported or invalid offer.");
        long total = 0;
        foreach (FileOffer file in offer.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 255 ||
                file.Size < 0 || file.MimeType?.Length > 255)
                throw new InvalidDataException("Invalid file metadata.");
            try { total = checked(total + file.Size); }
            catch (OverflowException e) { throw new InvalidDataException("Offer is too large.", e); }
            if (total > maxTotalBytes) throw new InvalidDataException("Offer exceeds receiver limit.");
            SafeFiles.Sanitize(file.Name);
        }
        return total;
    }
}
