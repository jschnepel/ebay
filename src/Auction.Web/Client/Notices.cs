namespace Auction.Web.Client;

/// <summary>
/// The signed-in user's unread count for the header bell. It refreshes when the server says a notification
/// arrived, when the tab reconnects, and after one is read.
/// </summary>
public sealed class Notices(AuctionApi api)
{
    public event Action? Changed;

    public long Unread { get; private set; }

    public async Task RefreshAsync()
    {
        var page = await api.NotificationsAsync(null);
        if (page.Value is { } value)
        {
            Unread = value.Unread;
            Changed?.Invoke();
        }
    }

    public async Task MarkReadAsync(long id)
    {
        await api.MarkNotificationReadAsync(id);
        await RefreshAsync();
    }

    public void Clear()
    {
        Unread = 0;
        Changed?.Invoke();
    }
}

/// <summary>What each notification kind says to the person who receives it.</summary>
public static class NoticeText
{
    public static string For(NotificationView notice)
    {
        var item = notice.AuctionTitle is { } title ? $"\"{title}\"" : "an auction";
        var reason = notice.Reason is { } why ? $" Reason: {why}" : "";
        return notice.Kind switch
        {
            "outbid" => $"You were outbid on {item}.",
            "won" => $"You won {item}.",
            "lost" => $"{item} ended; another bidder won.",
            "ended_sold" => $"Your auction {item} ended and sold.",
            "ended_no_bids" => $"Your auction {item} ended with no bids.",
            "reserve_not_met" => $"{item} ended below its reserve, so there's no sale.",
            "suspended" => $"{item} was suspended by an admin.{reason}",
            "resumed" => $"{item} was resumed by an admin.{reason}",
            "cancelled" => $"{item} was cancelled by an admin.{reason}",
            _ => $"An update on {item}.",
        };
    }
}
