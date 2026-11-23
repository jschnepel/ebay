namespace Auction.Web.Client;

/// <summary>Either the API's answer or the problem it reported; never both.</summary>
public sealed record ApiResult<T>(T? Value, Problem? Problem)
{
    public bool Succeeded => Problem is null;

    /// <summary>What a person sees for the problem: the API's title, or plain sentences for field errors.</summary>
    public string Message => Problem is null ? "" : ProblemText.For(Problem);
}
