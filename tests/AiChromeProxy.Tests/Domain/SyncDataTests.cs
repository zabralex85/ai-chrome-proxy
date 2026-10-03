using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Tests.Client;

namespace AiChromeProxy.Tests.Domain;

public sealed class SyncDataTests
{
	[Fact]
	public void EncodeDecode_RoundTrip_Base64UrlWithoutPadding()
	{
		byte[] data = [0xFB, 0xEF, 0xBE, 0xFF, 0xFF];

		var encoded = SyncData.Encode(data);

		Assert.Equal("----__8", encoded);
		Assert.Equal(data, SyncData.Decode(encoded));
		Assert.Equal(string.Empty, SyncData.Encode([]));
		Assert.Empty(SyncData.Decode(string.Empty));
	}

	[Theory]
	[InlineData("++++")]
	[InlineData("a/b=")]
	[InlineData("not base64!")]
	public void Decode_NotBase64Url_Throws(string data)
	{
		Assert.Throws<FormatException>(() => SyncData.Decode(data));
	}

	[Fact]
	public void WorstCaseChunk_UnderSignalRLimitWithMargin()
	{
		// FB EF BE is "++++" in base64; the default JSON encoder escapes '+' as \u002B (6 bytes).
		var content = Enumerable.Repeat<byte[]>([0xFB, 0xEF, 0xBE], (SyncLimits.ChunkSize / 3) + 1).SelectMany(b => b).Take(SyncLimits.ChunkSize).ToArray();
		var path = new string('ж', 200) + "/" + new string('ж', 59);
		var repo = new string('r', RepoName.MaxLength);

		int Size(string data) => ManifestPlannerTests.WireSize(
			Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(repo, path, long.MaxValue, data, true, new string('a', 64)), Guid.NewGuid().ToString("N")));

		Assert.True(Size(SyncData.Encode(content)) < ManifestPlannerTests.SignalRLimit - 4096);
		Assert.True(Size(Convert.ToBase64String(content)) > ManifestPlannerTests.SignalRLimit);
	}
}
