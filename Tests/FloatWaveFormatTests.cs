using SoundShelf.Core;
using Xunit;

namespace SoundShelf.Tests;

public class FloatWaveFormatTests
{
    [Theory]
    [InlineData("RIFF", 1)]
    [InlineData("RIFF", 2)]
    [InlineData("RF64", 2)]
    public void NormalizesExtensibleFloatWithoutMovingOrChangingSamples(string container, int channels)
    {
        using var workspace = new Workspace(); string path = Path.Combine(workspace.Root, "float.wav");
        byte[] samples = new byte[32]; new Random(123).NextBytes(samples);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(container)); writer.Write(container == "RF64" ? uint.MaxValue : 92u); writer.Write("WAVE"u8);
            if (container == "RF64")
            {
                writer.Write("ds64"u8); writer.Write(28u); writer.Write(128L); writer.Write(32L); writer.Write(4L); writer.Write(0u);
            }
            writer.Write("fmt "u8); writer.Write(40u); writer.Write((ushort)0xfffe); writer.Write((ushort)channels);
            writer.Write(48000u); writer.Write((uint)(48000 * channels * 4)); writer.Write((ushort)(channels * 4)); writer.Write((ushort)32);
            writer.Write((ushort)22); writer.Write((ushort)32); writer.Write(channels == 1 ? 4u : 3u);
            writer.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray());
            writer.Write("data"u8); writer.Write((uint)samples.Length); writer.Write(samples);
        }
        byte[] before = File.ReadAllBytes(path); int format = container == "RF64" ? 48 : 12;
        FloatWaveFormat.Normalize(path); byte[] after = File.ReadAllBytes(path);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[..format], after[..format]);
        Assert.Equal(18u, BitConverter.ToUInt32(after, format + 4));
        Assert.Equal((ushort)3, BitConverter.ToUInt16(after, format + 8));
        Assert.Equal("JUNK"u8.ToArray(), after[(format + 26)..(format + 30)]);
        Assert.Equal(before[(format + 48)..], after[(format + 48)..]);
        FloatWaveFormat.Normalize(path); Assert.Equal(after, File.ReadAllBytes(path));
    }

    [Fact]
    public void LeavesOtherFormatsUnchanged()
    {
        using var workspace = new Workspace(); string path = Path.Combine(workspace.Root, "pcm.wav");
        AudioTests.WriteWave(path, .01); byte[] before = File.ReadAllBytes(path);
        FloatWaveFormat.Normalize(path); Assert.Equal(before, File.ReadAllBytes(path));
    }
}
