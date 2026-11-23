namespace Auction.Web.Client;

/// <summary>
/// The words a person sees for a refused request. The API's titles already read well, except for field
/// errors, which it reports as a field name and a code; a 401, which means the session ended; a busy engine,
/// whose title is written for API clients; and a gateway error, which during a deploy means the site is restarting.
/// </summary>
internal static class ProblemText
{
    private const string AmountRange = "between $0.01 and $10,000,000";

    private static readonly Dictionary<(string Field, string Code), string> Sentences = new()
    {
        [("displayName", "required")] = "Enter a display name.",
        [("displayName", "too_long")] = "The display name can be at most 50 characters.",
        [("email", "invalid")] = "Enter a valid email address.",
        [("role", "invalid")] = "Choose User or Admin.",
        [("title", "required")] = "Enter a title.",
        [("title", "too_short")] = "The title needs at least 3 characters.",
        [("title", "too_long")] = "The title can be at most 120 characters.",
        [("description", "too_long")] = "The description can be at most 4,000 characters.",
        [("openingPriceCents", "required")] = "Enter an opening price.",
        [("openingPriceCents", "out_of_range")] = $"The opening price must be {AmountRange}.",
        [("incrementCents", "out_of_range")] = $"The bid increment must be {AmountRange}.",
        [("reserveCents", "required")] = "Enter a reserve, or choose another price floor.",
        [("reserveCents", "out_of_range")] = $"The reserve must be {AmountRange}.",
        [("reserveCents", "below_opening_price")] = "The reserve can't be below the opening price.",
        [("reserveCents", "not_allowed")] = "Only a listing with a hidden reserve has a reserve amount.",
        [("startsAt", "required")] = "Enter when the auction starts.",
        [("endsAt", "required")] = "Enter when the auction ends.",
        [("endsAt", "not_after_start")] = "The auction must end after it starts.",
        [("endsAt", "in_past")] = "That end time has already passed.",
        [("duration", "too_short")] = "An auction must run for at least a minute.",
        [("duration", "too_long")] = "An auction can run for at most 14 days.",
        [("amountCents", "required")] = "Enter an amount.",
        [("reason", "required")] = "Enter a reason; it's sent to the people affected and kept in the audit log.",
        [("reason", "too_long")] = "The reason can be at most 500 characters.",
        [("contentType", "unsupported_or_mismatched")] = "Choose a JPEG, PNG or WebP image.",
        [("dataBase64", "invalid")] = "That file couldn't be read as an image.",
        [("dataBase64", "too_large")] = "Images may be at most 5 MiB.",
        [("images", "count_out_of_range")] = "A listing has at most four images.",
        [("images", "must_list_each_image_once")] = "List each image once.",
    };

    // The API's field names in the words the forms use, for a code the client has no sentence for.
    private static readonly Dictionary<string, string> FieldWords = new()
    {
        ["displayName"] = "display name",
        ["openingPriceCents"] = "opening price",
        ["incrementCents"] = "bid increment",
        ["reserveCents"] = "reserve",
        ["floorType"] = "price floor",
        ["bidEntry"] = "bidding",
        ["startsAt"] = "start",
        ["endsAt"] = "end",
        ["amountCents"] = "amount",
        ["dataBase64"] = "image",
        ["contentType"] = "image type",
    };

    public static string For(Problem problem) => problem switch
    {
        // One line per error; MessageText shows several as a list.
        { Errors: { Count: > 0 } errors } => string.Join('\n', errors.SelectMany(e => e.Value.Select(code => Sentence(e.Key, code)))),
        { Status: 401, Code: not "sign_in_failed" } => "You're signed out. Sign in again to continue.",
        { Code: "busy" } => "The auction service is busy. Try again in a moment.",
        { Code: "maximum_lowered" } => "You already have a higher maximum on this auction. A maximum can't be lowered.",
        { Status: 502 or 504 } or { Status: 503, Code: null } => "The site is updating. Try again in a moment.",
        _ => problem.Title ?? "The request failed.",
    };

    // A few errors carry their rule after the code ("required, 1 to 500 characters") or instead of one
    // ("one of smoke, realistic"); the first keeps its sentence, the second the API's words.
    private static string Sentence(string field, string code)
    {
        if (Sentences.TryGetValue((field, code), out var sentence) || Sentences.TryGetValue((field, code.Split(',')[0]), out sentence))
        {
            return sentence;
        }

        var words = FieldWords.GetValueOrDefault(field, field);
        return code.Contains(' ', StringComparison.Ordinal) ? $"The {words} must be {code}." : $"Check the {words} ({code}).";
    }
}
