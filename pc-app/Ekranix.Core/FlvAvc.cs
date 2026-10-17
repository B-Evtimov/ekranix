namespace Ekranix.Core;

internal static class FlvAvc
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>AVCDecoderConfigurationRecord → SPS и PPS с start codes.</summary>
    public static byte[] ConfigToAnnexB(ReadOnlySpan<byte> rec)
    {
        if (rec.Length < 7) throw new InvalidDataException("Твърде кратък AVC config.");
        if ((rec[4] & 0x03) != 3) throw new InvalidDataException("Очаквам 4-байтови NAL дължини.");

        using var ms = new MemoryStream();
        int pos = 5;

        int numSps = rec[pos++] & 0x1F;
        for (int i = 0; i < numSps; i++) pos = CopyNal(rec, pos, ms);

        int numPps = rec[pos++];
        for (int i = 0; i < numPps; i++) pos = CopyNal(rec, pos, ms);

        return ms.ToArray();
    }

    private static int CopyNal(ReadOnlySpan<byte> rec, int pos, MemoryStream ms)
    {
        int len = (rec[pos] << 8) | rec[pos + 1];
        pos += 2;
        ms.Write(StartCode);
        ms.Write(rec.Slice(pos, len));
        return pos + len;
    }

    /// <summary>
    /// [4 байта дължина][NAL]... → [00 00 00 01][NAL]...
    /// Размерът не се променя, затова се прави на място, без копиране.
    /// </summary>
    public static void AvccToAnnexBInPlace(Span<byte> buf)
    {
        int pos = 0;
        while (pos + 4 <= buf.Length)
        {
            int len = (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
            if (len < 0 || pos + 4 + len > buf.Length)
                throw new InvalidDataException("Повредена NAL дължина.");

            buf[pos] = 0; buf[pos + 1] = 0; buf[pos + 2] = 0; buf[pos + 3] = 1;
            pos += 4 + len;
        }
    }
}