using System.Buffers.Binary;

namespace CrushGheChoi.Core.Animation;

/// <summary>Đọc kích thước PNG từ phần đầu tệp (chunk IHDR) mà không cần giải mã ảnh.</summary>
public static class PngInfo
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = height = 0;
        Span<byte> head = stackalloc byte[24];
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!head[..8].SequenceEqual(Signature) || !head[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(head[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(head[20..24]);
        return width > 0 && height > 0;
    }
}

/// <summary>So sánh tên tệp theo kiểu tự nhiên: frame2 đứng trước frame10.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = a.SequenceCompareTo(b);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0)
            {
                return c;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
