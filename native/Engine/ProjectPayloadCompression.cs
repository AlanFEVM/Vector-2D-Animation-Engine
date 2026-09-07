using System.Buffers;
using System.IO.Compression;

namespace VectorAnimationEngine;

internal static class ProjectPayloadCompression
{
    private const int OutputChunkSize = 64 * 1024;

    internal static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(payload);
        }

        return output.ToArray();
    }

    internal static byte[] Decompress(ReadOnlySpan<byte> compressed, int maximumDecodedBytes)
    {
        if (maximumDecodedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
        }
        if (compressed.IsEmpty)
        {
            throw new InvalidDataException("The Brotli payload is empty.");
        }

        var chunks = new List<(byte[] Buffer, int Length)>();
        var decodedLength = 0;
        var inputOffset = 0;
        var overflowOutput = new byte[1];
        var decoder = new BrotliDecoder();
        try
        {
            while (true)
            {
                var remainingInput = compressed[inputOffset..];
                var remainingOutput = maximumDecodedBytes - decodedLength;
                byte[]? outputBuffer;
                Span<byte> output;
                if (remainingOutput > 0)
                {
                    outputBuffer = new byte[Math.Min(OutputChunkSize, remainingOutput)];
                    output = outputBuffer;
                }
                else
                {
                    outputBuffer = null;
                    output = overflowOutput;
                }

                var status = decoder.Decompress(
                    remainingInput,
                    output,
                    out var bytesConsumed,
                    out var bytesWritten);
                if (bytesConsumed < 0
                    || bytesConsumed > remainingInput.Length
                    || bytesWritten < 0
                    || bytesWritten > output.Length)
                {
                    throw new InvalidDataException("The Brotli decoder returned invalid progress.");
                }

                inputOffset += bytesConsumed;
                if (bytesWritten > 0)
                {
                    if (outputBuffer is null
                        || decodedLength > maximumDecodedBytes - bytesWritten)
                    {
                        throw new InvalidDataException("The Brotli payload exceeds the decoded size limit.");
                    }

                    chunks.Add((outputBuffer, bytesWritten));
                    decodedLength += bytesWritten;
                }

                switch (status)
                {
                    case OperationStatus.Done:
                        if (inputOffset != compressed.Length)
                        {
                            throw new InvalidDataException("The Brotli payload contains trailing data.");
                        }

                        return Combine(chunks, decodedLength);

                    case OperationStatus.DestinationTooSmall:
                        if (bytesConsumed == 0 && bytesWritten == 0)
                        {
                            throw new InvalidDataException(
                                decodedLength >= maximumDecodedBytes
                                    ? "The Brotli payload exceeds the decoded size limit."
                                    : "The Brotli decoder made no progress.");
                        }
                        continue;

                    case OperationStatus.NeedMoreData:
                        throw new InvalidDataException("The Brotli payload is truncated.");

                    case OperationStatus.InvalidData:
                    default:
                        throw new InvalidDataException("The Brotli payload is invalid.");
                }
            }
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private static byte[] Combine(List<(byte[] Buffer, int Length)> chunks, int decodedLength)
    {
        if (decodedLength == 0) return Array.Empty<byte>();

        var result = new byte[decodedLength];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Buffer.AsSpan(0, chunk.Length).CopyTo(result.AsSpan(offset));
            offset += chunk.Length;
        }

        return result;
    }
}
