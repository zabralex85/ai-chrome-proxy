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
	public void Decode_NulAfterTheFirstChunk_IsStillAnInvalidFileOnlyWhenNotUtf8()
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
	public void Limit_Is5MiB() => Assert.Equal(5 * 1024 * 1024, FileText.MaxBytes);

	[Theory]
	[InlineData(0, "Binary file — not shown")]
	[InlineData(1, "Too large to show (6.0 MB)")]
	public void Message_SaysWhyTheFileIsNotShown(int reason, string expected)
	{
		var result = new FileTextResult(null, (FileTextReason)(reason + 1), 6 * 1024 * 1024);

		Assert.Equal(expected, result.Message);
	}

	[Fact]
	public void Message_OfText_IsEmpty() => Assert.Equal(string.Empty, FileText.Decode([65]).Message);
}
