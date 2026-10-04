using System.Text;
using AiChromeProxy.Client.Navigator;

namespace AiChromeProxy.Tests.Client;

public sealed class FileTextTests
{
	public static TheoryData<byte[], string> TextCases => new()
	{
		{ [], string.Empty },
		{ Encoding.UTF8.GetBytes("héllo\nworld"), "héllo\nworld" },
		{ [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("bom ü")], "bom ü" },
		{ [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("le ü\n")], "le ü\n" },
		{ [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("be ü\n")], "be ü\n" },
	};

	[Theory]
	[MemberData(nameof(TextCases))]
	public void Decode_Text_IsDecodedWithoutBom(byte[] bytes, string expected)
	{
		var result = FileText.Decode(bytes);

		Assert.Equal(expected, result.Text);
		Assert.Equal(FileTextReason.None, result.Reason);
		Assert.Equal(bytes.Length, result.Size);
	}

	[Fact]
	public void Decode_NulInTheFirstChunk_IsBinary()
	{
		var result = FileText.Decode([65, 0, 66]);

		Assert.Null(result.Text);
		Assert.Equal(FileTextReason.Binary, result.Reason);
	}

	[Fact]
	public void Decode_NulAfterTheProbedBytes_IsStillText()
	{
		var bytes = new byte[FileText.BinaryProbeBytes + 10];
		Array.Fill(bytes, (byte)'a');
		bytes[FileText.BinaryProbeBytes] = 0;

		Assert.Equal(FileTextReason.None, FileText.Decode(bytes).Reason);
	}

	[Theory]
	[InlineData(new byte[] { 0xC3, 0x28 })]
	[InlineData(new byte[] { 0xFF, 0xFF, 0x41 })]
	[InlineData(new byte[] { 0xE2, 0x82 })]
	public void Decode_InvalidUtf8_IsBinary(byte[] bytes) => Assert.Equal(FileTextReason.Binary, FileText.Decode(bytes).Reason);

	[Fact]
	public void Decode_Utf8WithBomAndNul_IsBinary() => Assert.Equal(FileTextReason.Binary, FileText.Decode([0xEF, 0xBB, 0xBF, 65, 0, 66]).Reason);

	[Fact]
	public void Decode_Utf32LeBom_IsBinary() => Assert.Equal(FileTextReason.Binary, FileText.Decode([0xFF, 0xFE, 0, 0, 65, 0, 0, 0]).Reason);

	[Theory]
	[InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0xD8 })]
	[InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x42 })]
	[InlineData(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 })]
	[InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x00 })]
	public void Decode_LoneSurrogateOrOddLengthUtf16_IsBinary(byte[] bytes) => Assert.Equal(FileTextReason.Binary, FileText.Decode(bytes).Reason);

	[Fact]
	public void Decode_MultiByteCharAcrossTheProbeBoundary_IsText()
	{
		var bytes = new byte[FileText.BinaryProbeBytes + 4];
		Array.Fill(bytes, (byte)'a');
		bytes[FileText.BinaryProbeBytes - 1] = 0xC3;
		bytes[FileText.BinaryProbeBytes] = 0xA9;

		var result = FileText.Decode(bytes);

		Assert.Equal(FileTextReason.None, result.Reason);
		Assert.Equal('é', result.Text![FileText.BinaryProbeBytes - 1]);
	}

	[Fact]
	public void Decode_ExactlyTheLimit_IsShown()
	{
		var bytes = new byte[FileText.MaxBytes];
		Array.Fill(bytes, (byte)'a');

		Assert.Equal(FileTextReason.None, FileText.Decode(bytes).Reason);
	}

	[Fact]
	public void Decode_OneByteOverTheLimit_IsTooLarge()
	{
		var result = FileText.Decode(new byte[FileText.MaxBytes + 1]);

		Assert.Null(result.Text);
		Assert.Equal(FileTextReason.TooLarge, result.Reason);
		Assert.Equal(FileText.MaxBytes + 1, result.Size);
	}

	[Fact]
	public void Decode_CappedBytesOfAHugeFile_ReportTheRealSize()
	{
		var result = FileText.Decode(new byte[FileText.MaxBytes + 1], 12L * 1024 * 1024);

		Assert.Equal(FileTextReason.TooLarge, result.Reason);
		Assert.Equal("Too large to show (12.0 MB)", result.Message);
	}

	[Fact]
	public async Task FakeFolder_ReadFile_AgreesWithDecode()
	{
		var folder = new FakeFolder();
		folder.Files["a.txt"] = "hi"u8.ToArray();
		folder.Files["big.bin"] = new byte[FileText.MaxBytes + 10];
		folder.SizeOnly["huge.bin"] = 50L * 1024 * 1024;
		folder.HashFailures.Add("locked.txt");

		var small = (await folder.ReadFileAsync("a.txt"))!;
		var big = (await folder.ReadFileAsync("big.bin"))!;
		var huge = (await folder.ReadFileAsync("huge.bin"))!;

		Assert.Equal("hi", FileText.Decode(small.Bytes, small.Size).Text);
		Assert.Equal(FileText.MaxBytes + 1, big.Bytes.Length);
		Assert.Equal(FileText.MaxBytes + 10, FileText.Decode(big.Bytes, big.Size).Size);
		Assert.Equal(FileTextReason.TooLarge, FileText.Decode(huge.Bytes, huge.Size).Reason);
		Assert.Equal(50L * 1024 * 1024, FileText.Decode(huge.Bytes, huge.Size).Size);
		Assert.Null(await folder.ReadFileAsync("missing.txt"));
		await Assert.ThrowsAsync<Microsoft.JSInterop.JSException>(() => folder.ReadFileAsync("locked.txt"));
		await Assert.ThrowsAsync<Microsoft.JSInterop.JSException>(() => folder.ReadFileAsync("../a.txt"));
	}

	[Fact]
	public void Limit_Is5MiB() => Assert.Equal(5 * 1024 * 1024, FileText.MaxBytes);

	[Theory]
	[InlineData(FileTextReason.Binary, "Binary file — not shown")]
	[InlineData(FileTextReason.TooLarge, "Too large to show (6.0 MB)")]
	public void Message_SaysWhyTheFileIsNotShown(FileTextReason reason, string expected)
	{
		var result = new FileTextResult(null, reason, 6 * 1024 * 1024);

		Assert.Equal(expected, result.Message);
	}

	[Fact]
	public void Message_OfText_IsEmpty() => Assert.Equal(string.Empty, FileText.Decode([65]).Message);
}
