using AiChromeProxy.Application.Chat;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary><see cref="IAttachmentStore"/> on the file system under <see cref="AttachmentsOptions.Root"/>.</summary>
public sealed class FileSystemAttachmentStore(IOptions<AttachmentsOptions> options) : IAttachmentStore
{
	private const string PartSuffix = ".part";

	private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root));

	public string RepoFolder(string repo) => Child(_root, repo);

	public void Begin(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		var part = Child(folder, name + PartSuffix);
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}

		Directory.CreateDirectory(folder);
		File.WriteAllBytes(part, []);
	}

	public long Append(string repo, string id, string name, byte[] data)
	{
		using (var stream = new FileStream(Child(Folder(repo, id), name + PartSuffix), FileMode.Append, FileAccess.Write, FileShare.None))
		{
			stream.Write(data);
			return stream.Length;
		}
	}

	public void Commit(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		File.Move(Child(folder, name + PartSuffix), Child(folder, name));
	}

	public StoredAttachment? Find(string repo, string id)
	{
		var folder = Folder(repo, id);
		var file = Directory.Exists(folder)
			? Directory.EnumerateFiles(folder).FirstOrDefault(f => !f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
			: null;
		return file is null ? null : new StoredAttachment(file, Path.GetFileName(file), new FileInfo(file).Length);
	}

	public void Delete(string repo, string id)
	{
		var folder = Folder(repo, id);
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	public int Sweep(DateTimeOffset olderThan, DateTimeOffset partsOlderThan)
	{
		if (!Directory.Exists(_root))
		{
			return 0;
		}

		var deleted = 0;
		foreach (var repoFolder in Directory.EnumerateDirectories(_root).ToList())
		{
			try
			{
				deleted += SweepRepo(repoFolder, olderThan, partsOlderThan);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A repo folder that cannot be listed is skipped; the next sweep tries again.
			}
		}

		return deleted;
	}

	private static int SweepRepo(string repoFolder, DateTimeOffset olderThan, DateTimeOffset partsOlderThan)
	{
		var deleted = 0;
		foreach (var folder in Directory.EnumerateDirectories(repoFolder).ToList())
		{
			try
			{
				var files = Directory.GetFiles(folder);
				var written = files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
				var limit = files.Any(f => f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)) ? partsOlderThan : olderThan;
				if (written < limit.UtcDateTime)
				{
					Directory.Delete(folder, recursive: true);
					deleted++;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A file still open (an upload in progress, a run reading it) is tried again on the next sweep.
			}
		}

		return deleted;
	}

	private string Folder(string repo, string id) => Child(Child(_root, repo), id);

	// One path segment directly inside parent: rejects "", ".", "..", and anything with separators.
	private string Child(string parent, string segment)
	{
		var full = Path.GetFullPath(Path.Combine(parent, segment));
		if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path is not directly inside its parent folder.", nameof(segment));
		}

		return full;
	}
}
