namespace AiChromeProxy.Client.Navigator;

/// <summary>Lets one re-read of an open file run at a time; triggers that arrive meanwhile ask for exactly one more run, however many there were.</summary>
public sealed class RefreshGate
{
	private bool _running;
	private bool _again;

	/// <summary>Called by a trigger.</summary>
	/// <returns>True when the caller runs the read now (then it loops while <see cref="Finish"/> says so); false when one is running and will run once more.</returns>
	public bool TryStart()
	{
		if (_running)
		{
			_again = true;
			return false;
		}

		_running = true;
		return true;
	}

	/// <summary>Called when a read ended.</summary>
	/// <returns>True when triggers arrived during it: read once more; false when the gate is free again.</returns>
	public bool Finish()
	{
		if (_again)
		{
			_again = false;
			return true;
		}

		_running = false;
		return false;
	}

	/// <summary>Frees the gate after a read that failed.</summary>
	public void Reset() => (_running, _again) = (false, false);
}
