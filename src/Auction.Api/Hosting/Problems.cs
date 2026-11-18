namespace Auction.Api.Hosting;

/// <summary>
/// RFC 9457 Problem Details for every refusal. Each carries a stable <c>code</c> for clients; none carries
/// internals such as exception text.
/// </summary>
internal static class Problems
{
    public static IResult Of(int status, string code, string title) =>
        Results.Problem(title: title, statusCode: status, extensions: Code(code));

    /// <summary>422 with field-level errors: <c>errors</c> maps each field to its error codes.</summary>
    public static IResult Validation(IDictionary<string, string[]> errors, string title = "Some fields are invalid.") =>
        Results.ValidationProblem(errors, title: title, statusCode: StatusCodes.Status422UnprocessableEntity, extensions: Code("validation_failed"));

    public static IResult NotFound() => Of(StatusCodes.Status404NotFound, "not_found", "Auction not found.");

    public static IResult BadRequest(string code, string title) => Of(StatusCodes.Status400BadRequest, code, title);

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
