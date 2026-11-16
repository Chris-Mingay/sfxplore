namespace SoundShelf.Core;

public static class FloatWaveFormat
{
    private static readonly byte[] FloatSubtype = new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray();

    // Older FFmpeg versions wrap even mono/stereo floats in WAVEFORMATEXTENSIBLE.
    // Godot requires format tag 3. Replace only the extension with a JUNK chunk,
    // retaining all audio bytes, chunk offsets, and RIFF/RF64 sizes unchanged.
    public static void Normalize(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        byte[] container = reader.ReadBytes(4);
        if (!container.AsSpan().SequenceEqual("RIFF"u8) && !container.AsSpan().SequenceEqual("RF64"u8)) return;
        stream.Position = 8;
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("WAVE"u8)) return;
        while (stream.Position + 8 <= stream.Length)
        {
            byte[] chunk = reader.ReadBytes(4); uint size = reader.ReadUInt32(); long start = stream.Position;
            if (start + size > stream.Length) return;
            if (chunk.AsSpan().SequenceEqual("fmt "u8))
            {
                if (size != 40 || reader.ReadUInt16() != 0xfffe) return;
                stream.Position = start + 14;
                if (reader.ReadUInt16() != 32 || reader.ReadUInt16() != 22) return;
                stream.Position = start + 24;
                if (!reader.ReadBytes(16).AsSpan().SequenceEqual(FloatSubtype)) return;
                stream.Position = start - 4; writer.Write(18u);
                stream.Position = start; writer.Write((ushort)3);
                stream.Position = start + 16; writer.Write((ushort)0);
                writer.Write("JUNK"u8); writer.Write(14u); writer.Write(new byte[14]);
                return;
            }
            stream.Position = start + size + (size & 1);
        }
    }
}
