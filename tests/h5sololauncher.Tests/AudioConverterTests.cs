using System.Buffers.Binary;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Content;
using H5SoloLauncher.Core.Conversion;
using H5SoloLauncher.Core.Forge;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class AudioConverterTests
{
    [Theory]
    [InlineData(0xffff, 2, 3)]
    [InlineData(0xfffe, 1, 4)]
    [InlineData(2, 6, 63)]
    public void ConvertsChannelLayoutWithoutChangingCompressedMedia(int codec, int channels, uint mask)
    {
        var source = Wem(codec, channels, (mask << 12) | 256u | (uint)channels); var original = (byte[])source.Clone();
        var result = AudioConverter.Wem(source);
        Assert.Equal(original, source); Assert.Equal(mask, BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(40)));
        Assert.Equal(source[..40], result.Bytes[..40]); Assert.Equal(source[44..], result.Bytes[44..]);
    }
    [Fact]
    public void RejectsUnknownChannelLayoutsAndTruncationAndRecordsXma()
    {
        Assert.Throws<CacheException>(() => AudioConverter.Wem(Wem(0xffff, 2, 0x1102)));
        Assert.Throws<CacheException>(() => AudioConverter.Wem(Wem(0xffff, 2, 0x3102)[..35]));
        Assert.Throws<CacheException>(() => AudioConverter.Wem(Wem(0x1234, 2, 0x3102)));
        var xma = Wem(0x166, 2, 0); var result = AudioConverter.Wem(xma);
        Assert.Equal(xma, result.Bytes); Assert.Equal(1, result.PreservedXma);
    }
    [Fact]
    public void AudioReaderAllowsOnlyKnownPackageNames()
    {
        Assert.True(ForgePaths.IsAudio("sound/win/SFX/soundbank.pck")); Assert.True(ForgePaths.IsAudio("sound/win/English(US)/soundvoice.pck"));
        foreach (var path in new[] { "sound/win/../soundvoice.pck", "sound/win/SFX/secret.pck", "sound/win/English(US)/soundvoice.pck:other", "C:/sound/win/SFX/soundbank.pck" }) Assert.False(ForgePaths.IsAudio(path));
        Assert.Throws<CacheException>(() => FileMetadata.FromAudioTables(new byte[28], 100, 0));
    }
    private static byte[] Wem(int codec, int channels, uint configuration)
    {
        var bytes = new byte[64]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 56); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 24); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)codec);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), (ushort)channels); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), configuration);
        "data"u8.CopyTo(bytes.AsSpan(44)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 12);
        for (var i = 52; i < bytes.Length; i++) bytes[i] = (byte)(i * 3); return bytes;
    }
}
