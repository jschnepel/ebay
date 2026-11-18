namespace Auction.Web.Client;

/// <summary>What a view is showing: its content, or one of the states every view has (UI-01).</summary>
public enum ViewState
{
    Content,
    Loading,
    Empty,
    Error,
    Forbidden,
}
