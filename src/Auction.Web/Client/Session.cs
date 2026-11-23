namespace Auction.Web.Client;

/// <summary>
/// Who is signed in, as the API last reported it. The session itself is the API's HttpOnly cookie; this
/// only remembers the account to show it.
/// </summary>
public sealed class Session
{
    private readonly AuctionApi api;
    private Task? _loading;

    public Session(AuctionApi api)
    {
        this.api = api;
        api.SessionEnded += OnSessionEnded;
    }

    public event Action? Changed;

    public Account? Account { get; private set; }

    /// <summary>Set when the server ended this session because the account signed in elsewhere.</summary>
    public bool EndedElsewhere { get; private set; }

    /// <summary>Set when the API stopped accepting this session without saying why, as after a demo reset.</summary>
    public bool Ended { get; private set; }

    /// <summary>Asks the API once per page load; later calls wait for the same answer.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public void SignedIn(Account account)
    {
        (Account, EndedElsewhere, Ended) = (account, false, false);
        Changed?.Invoke();
    }

    /// <summary>The account signed in on another browser or device, which ended this session.</summary>
    public void SignedOutElsewhere()
    {
        (Account, EndedElsewhere) = (null, true);
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        await api.SignOutAsync();
        Account = null;
        Changed?.Invoke();
    }

    private void OnSessionEnded()
    {
        if (Account is not null)
        {
            (Account, Ended) = (null, true);
            Changed?.Invoke();
        }
    }

    private async Task LoadCoreAsync()
    {
        Account = await api.MeAsync();
        Changed?.Invoke();
    }
}
