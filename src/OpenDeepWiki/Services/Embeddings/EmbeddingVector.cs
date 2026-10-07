using System.Buffers.Binary;

namespace OpenDeepWiki.Services.Embeddings;

/// <summary>
/// Vector helpers: float32 little-endian storage, normalization and dot product.
/// </summary>
internal static class EmbeddingVector
{
    public static byte[] ToBytes(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("Vector byte length must be a multiple of 4.", nameof(bytes));
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>
    /// Scales the vector to unit length in place. A zero vector stays zero.
    /// </summary>
    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 0 || double.IsNaN(norm) || double.IsInfinity(norm))
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    /// <summary>
    /// Dot product of a query vector and a stored little-endian vector of the same length.
    /// Both are unit vectors, so this is the cosine similarity.
    /// </summary>
    public static double Dot(float[] query, byte[] stored)
    {
        double sum = 0;
        var span = stored.AsSpan();
        for (var i = 0; i < query.Length; i++)
        {
            sum += query[i] * BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)));
        }

        return sum;
    }
}
