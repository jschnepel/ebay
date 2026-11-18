namespace Auction.Web.Client;

/// <summary>Either the API's answer or the problem it reported; never both.</summary>
public sealed record ApiResult<T>(T? Value, Problem? Problem)
{
    public bool Succeeded => Problem is null;

    /// <summary>The problem's title with any field errors, for an error message.</summary>
    public string Message => Problem switch
    {
        null => "",
        { Errors: { Count: > 0 } errors } => $"{Problem.Title} {string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"))}",
        _ => Problem.Title ?? "The request failed.",
    };
}
