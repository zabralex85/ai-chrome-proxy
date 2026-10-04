using Microsoft.Data.Sqlite;

namespace AiChromeProxy.Tests;

/// <summary>Deletes a test folder that holds an <c>aicp.db</c>: a run's last write can keep the file open a moment after the host stopped, so it retries.</summary>
public static class TestFolder
{
	/// <summary>Deletes <paramref name="path"/> with everything in it; gives up after about 5 s.</summary>
	public static async Task DeleteAsync(string path)
	{
		for (var attempt = 0; ; attempt++)
		{
			SqliteConnection.ClearAllPools();
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}

				return;
			}
			catch (IOException) when (attempt < 50)
			{
				await Task.Delay(100);
			}
		}
	}
}
