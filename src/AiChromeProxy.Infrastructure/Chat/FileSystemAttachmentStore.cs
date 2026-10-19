using AiChromeProxy.Application.Chat;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary><see cref="IAttachmentStore"/> on the file system under <see cref="AttachmentsOptions.Root"/>.</summary>
public sealed class FileSystemAttachmentStore(IOptions<AttachmentsOptions> options) : IAttachmentStore
{
	private const string PartSuffix = ".part";

	private readonly string _root = Path.GetFullPath(options.Value.Root);

	public string RepoFolder(string repo) => Under(Path.Combine(_root, repo));

	public void Begin(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		var part = Under(Path.Combine(folder, name + PartSuffix));
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}

		Directory.CreateDirectory(folder);
		File.WriteAllBytes(part, []);
	}

	public long Append(string repo, string id, string name, byte[] data)
	{
		using (var stream = new FileStream(Under(Path.Combine(Folder(repo, id), name + PartSuffix)), FileMode.Append, FileAccess.Write, FileShare.None))
		{
			stream.Write(data);
			return stream.Length;
		}
	}

	public void Commit(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		File.Move(Under(Path.Combine(folder, name + PartSuffix)), Under(Path.Combine(folder, name)));
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
		foreach (var folder in Directory.EnumerateDirectories(_root).SelectMany(Directory.EnumerateDirectories).ToList())
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

	private string Folder(string repo, string id) => Under(Path.Combine(_root, repo, id));

	private string Under(string path)
	{
		var full = Path.GetFullPath(path);
		if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path leaves the attachments root.", nameof(path));
		}

		return full;
	}
}
