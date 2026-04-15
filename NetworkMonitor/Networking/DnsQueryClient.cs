using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetworkMonitor;

static class DnsQueryClient
{
    private const int DnsPort = 53;
    private const ushort ClassInternet = 1;
    private const ushort TypeNs = 2;
    private const ushort TypeCName = 5;
    private const ushort TypeMx = 15;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort FlagRecursionDesired = 0x0100;
    private const ushort FlagTruncated = 0x0200;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IPAddress[] FallbackResolvers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8")
    ];

    public static async Task<IReadOnlyList<string>> QueryAsync(string host, string recordType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);

        var queryType = ParseRecordType(recordType);
        var normalizedHost = host.TrimEnd('.');
        var query = BuildQuery(host.TrimEnd('.'), queryType, out var queryId);
        Exception? lastError = null;

        foreach (var resolver in GetResolvers())
        {
            var cacheKey = BuildCacheKey(resolver, normalizedHost, queryType);
            if (TryGetCachedValue(cacheKey, out var cachedValues))
                return cachedValues;

            try
            {
                var response = await QueryUdpAsync(resolver, query, queryId, ct);
                if (IsTruncated(response))
                    response = await QueryTcpAsync(resolver, query, queryId, ct);

                var values = ParseResponse(response, queryId, queryType);
                Cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow.AddSeconds(AppConfigProvider.Current.DnsCacheSeconds), values);
                return values;
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Aucun résolveur DNS n'a pu répondre à la requête.", lastError);
    }

    private static ushort ParseRecordType(string recordType) => recordType.Trim().ToUpperInvariant() switch
    {
        "MX" => TypeMx,
        "TXT" => TypeTxt,
        "CNAME" => TypeCName,
        "NS" => TypeNs,
        "SRV" => TypeSrv,
        _ => throw new NotSupportedException($"Type DNS non supporté : {recordType}")
    };

    private static IReadOnlyList<IPAddress> GetResolvers()
    {
        var configuredResolvers = AppConfigProvider.Current.DnsServers
            .Select(server => IPAddress.TryParse(server, out var parsed) ? parsed : null)
            .OfType<IPAddress>()
            .ToList();

        if (configuredResolvers.Count > 0)
            return configuredResolvers;

        var resolvers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().DnsAddresses)
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(address => !IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.Loopback))
            .Distinct()
            .ToList();

        if (resolvers.Count > 0)
            return resolvers;

        return FallbackResolvers;
    }

    private static byte[] BuildQuery(string host, ushort queryType, out ushort queryId)
    {
        queryId = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        WriteUInt16(writer, queryId);
        WriteUInt16(writer, FlagRecursionDesired);
        WriteUInt16(writer, 1);
        WriteUInt16(writer, 0);
        WriteUInt16(writer, 0);
        WriteUInt16(writer, 0);
        WriteDomainName(writer, host);
        WriteUInt16(writer, queryType);
        WriteUInt16(writer, ClassInternet);

        writer.Flush();
        return stream.ToArray();
    }

    private static async Task<byte[]> QueryUdpAsync(IPAddress resolver, byte[] query, ushort queryId, CancellationToken ct)
    {
        using var udpClient = new UdpClient(resolver.AddressFamily);
        udpClient.Connect(resolver, DnsPort);

        using var timeoutCts = CreateTimeoutTokenSource(ct);
        await udpClient.SendAsync(query, query.Length);
        var result = await udpClient.ReceiveAsync(timeoutCts.Token);

        ValidateResponseHeader(result.Buffer, queryId);
        return result.Buffer;
    }

    private static async Task<byte[]> QueryTcpAsync(IPAddress resolver, byte[] query, ushort queryId, CancellationToken ct)
    {
        using var tcpClient = new TcpClient(resolver.AddressFamily);
        using var timeoutCts = CreateTimeoutTokenSource(ct);
        await tcpClient.ConnectAsync(resolver, DnsPort, timeoutCts.Token);
        await using var stream = tcpClient.GetStream();

        var lengthPrefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, checked((ushort)query.Length));
        await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length, timeoutCts.Token);
        await stream.WriteAsync(query, 0, query.Length, timeoutCts.Token);
        await stream.FlushAsync(timeoutCts.Token);

        var responseLengthBytes = await ReadExactAsync(stream, 2, timeoutCts.Token);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(responseLengthBytes);
        var response = await ReadExactAsync(stream, responseLength, timeoutCts.Token);

        ValidateResponseHeader(response, queryId);
        return response;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer, offset, length - offset, ct);
            if (read == 0)
                throw new IOException("Fin de flux inattendue pendant la lecture de la réponse DNS.");

            offset += read;
        }

        return buffer;
    }

    private static IReadOnlyList<string> ParseResponse(byte[] response, ushort expectedId, ushort queryType)
    {
        ValidateResponseHeader(response, expectedId);

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8, 2));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10, 2));
        var offset = 12;

        offset = SkipQuestions(response, offset);

        var values = new List<string>();
        var totalRecords = answerCount + authorityCount + additionalCount;
        for (var index = 0; index < totalRecords; index++)
        {
            SkipDomainName(response, ref offset);
            var type = ReadUInt16(response, ref offset);
            _ = ReadUInt16(response, ref offset);
            _ = ReadUInt32(response, ref offset);
            var dataLength = ReadUInt16(response, ref offset);
            var dataOffset = offset;

            if (type == queryType)
            {
                var value = type switch
                {
                    TypeMx => ParseMx(response, dataOffset),
                    TypeTxt => ParseTxt(response, dataOffset, dataLength),
                    TypeCName => ReadDomainName(response, dataOffset, out _).TrimEnd('.'),
                    TypeNs => ReadDomainName(response, dataOffset, out _).TrimEnd('.'),
                    TypeSrv => ParseSrv(response, dataOffset),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }

            offset += dataLength;
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SkipQuestions(byte[] response, int offset)
    {
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4, 2));
        for (var index = 0; index < questionCount; index++)
        {
            SkipDomainName(response, ref offset);
            offset += 4;
        }

        return offset;
    }

    private static string ParseMx(byte[] response, int offset)
    {
        offset += 2;
        return ReadDomainName(response, offset, out _).TrimEnd('.');
    }

    private static string ParseTxt(byte[] response, int offset, int dataLength)
    {
        var endOffset = offset + dataLength;
        var builder = new StringBuilder();
        while (offset < endOffset)
        {
            var segmentLength = response[offset++];
            if (offset + segmentLength > endOffset)
                throw new InvalidOperationException("Segment TXT DNS invalide.");

            builder.Append(Encoding.UTF8.GetString(response, offset, segmentLength));
            offset += segmentLength;
        }

        return builder.ToString();
    }

    private static string ParseSrv(byte[] response, int offset)
    {
        var priority = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset, 2));
        var weight = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2, 2));
        var port = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 4, 2));
        var target = ReadDomainName(response, offset + 6, out _).TrimEnd('.');
        return $"{priority} {weight} {port} {target}";
    }

    private static bool IsTruncated(byte[] response)
        => (BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) & FlagTruncated) != 0;

    private static void ValidateResponseHeader(byte[] response, ushort expectedId)
    {
        if (response.Length < 12)
            throw new InvalidOperationException("Réponse DNS trop courte.");

        var actualId = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2));
        if (actualId != expectedId)
            throw new InvalidOperationException("Identifiant de réponse DNS inattendu.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        var responseCode = flags & 0x000F;
        if (responseCode != 0)
            throw new InvalidOperationException($"Réponse DNS en erreur (rcode={responseCode}).");
    }

    private static string ReadDomainName(byte[] buffer, int offset, out int bytesRead)
    {
        var labels = new List<string>();
        var currentOffset = offset;
        var consumed = 0;
        var jumped = false;
        var visitedOffsets = new HashSet<int>();

        while (true)
        {
            if (currentOffset >= buffer.Length)
                throw new InvalidOperationException("Nom de domaine DNS tronqué.");

            if (!visitedOffsets.Add(currentOffset))
                throw new InvalidOperationException("Boucle détectée dans la compression DNS.");

            var length = buffer[currentOffset];
            if (length == 0)
            {
                if (!jumped)
                    consumed++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (currentOffset + 1 >= buffer.Length)
                    throw new InvalidOperationException("Pointeur DNS invalide.");

                var pointer = ((length & 0x3F) << 8) | buffer[currentOffset + 1];
                if (!jumped)
                    consumed += 2;
                currentOffset = pointer;
                jumped = true;
                continue;
            }

            currentOffset++;
            if (currentOffset + length > buffer.Length)
                throw new InvalidOperationException("Label DNS invalide.");

            labels.Add(Encoding.ASCII.GetString(buffer, currentOffset, length));
            currentOffset += length;
            if (!jumped)
                consumed += length + 1;
        }

        bytesRead = consumed;
        return string.Join('.', labels);
    }

    private static void SkipDomainName(byte[] buffer, ref int offset)
        => offset += ReadDomainName(buffer, offset, out var bytesRead) is not null ? bytesRead : 0;

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] buffer, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static void WriteUInt16(BinaryWriter writer, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteDomainName(BinaryWriter writer, string domainName)
    {
        foreach (var label in domainName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (label.Length > 63)
                throw new InvalidOperationException("Label DNS trop long.");

            writer.Write((byte)label.Length);
            writer.Write(Encoding.ASCII.GetBytes(label));
        }

        writer.Write((byte)0);
    }

    private static CancellationTokenSource CreateTimeoutTokenSource(CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        return timeoutCts;
    }

    private static string BuildCacheKey(IPAddress resolver, string host, ushort queryType)
        => $"{resolver}|{host}|{queryType}";

    private static bool TryGetCachedValue(string cacheKey, out IReadOnlyList<string> values)
    {
        values = [];
        if (!Cache.TryGetValue(cacheKey, out var entry))
            return false;

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Cache.TryRemove(cacheKey, out _);
            return false;
        }

        values = entry.Values;
        return true;
    }

    private readonly record struct CacheEntry(DateTimeOffset ExpiresAt, IReadOnlyList<string> Values);
}
