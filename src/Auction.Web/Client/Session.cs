namespace Auction.Web.Client;

/// <summary>
/// Who is signed in, as the API last reported it. The session itself is the API's HttpOnly cookie; this
/// only remembers the account to show it.
/// </summary>
public sealed class Session(AuctionApi api)
{
    private Task? _loading;

    public event Action? Changed;

    public Account? Account { get; private set; }

    /// <summary>Asks the API once per page load; later calls wait for the same answer.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public void SignedIn(Account account)
    {
        Account = account;
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        await api.SignOutAsync();
        Account = null;
        Changed?.Invoke();
    }

    private async Task LoadCoreAsync()
    {
        Account = await api.MeAsync();
        Changed?.Invoke();
    }
}
