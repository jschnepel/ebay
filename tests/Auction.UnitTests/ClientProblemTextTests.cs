using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>The error text a person sees when the API refuses a request.</summary>
public sealed class ClientProblemTextTests
{
    private static string Shown(Problem problem) => new ApiResult<object>(null, problem).Message;

    private static Problem Invalid(params (string Field, string Code)[] errors) => new()
    {
        Status = 422,
        Code = "validation_failed",
        Title = "Some fields are invalid.",
        Errors = errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray()),
    };

    [Fact]
    public void A_refusal_with_no_field_errors_shows_the_api_title()
    {
        Assert.Equal("You can't bid on your own auction.", Shown(new() { Status = 409, Code = "self_bid", Title = "You can't bid on your own auction." }));
    }

    [Theory]
    [InlineData("displayName", "required", "Enter a display name.")]
    [InlineData("displayName", "too_long", "The display name can be at most 50 characters.")]
    [InlineData("email", "invalid", "Enter a valid email address.")]
    [InlineData("role", "invalid", "Choose User or Admin.")]
    [InlineData("title", "required", "Enter a title.")]
    [InlineData("title", "too_short", "The title needs at least 3 characters.")]
    [InlineData("title", "too_long", "The title can be at most 120 characters.")]
    [InlineData("description", "too_long", "The description can be at most 4,000 characters.")]
    [InlineData("openingPriceCents", "required", "Enter an opening price.")]
    [InlineData("openingPriceCents", "out_of_range", "The opening price must be between $0.01 and $10,000,000.")]
    [InlineData("incrementCents", "out_of_range", "The bid increment must be between $0.01 and $10,000,000.")]
    [InlineData("reserveCents", "required", "Enter a reserve, or choose another price floor.")]
    [InlineData("reserveCents", "below_opening_price", "The reserve can't be below the opening price.")]
    [InlineData("reserveCents", "not_allowed", "Only a listing with a hidden reserve has a reserve amount.")]
    [InlineData("startsAt", "required", "Enter when the auction starts.")]
    [InlineData("endsAt", "required", "Enter when the auction ends.")]
    [InlineData("endsAt", "not_after_start", "The auction must end after it starts.")]
    [InlineData("endsAt", "in_past", "That end time has already passed.")]
    [InlineData("duration", "too_short", "An auction must run for at least a minute.")]
    [InlineData("duration", "too_long", "An auction can run for at most 14 days.")]
    [InlineData("amountCents", "required", "Enter an amount.")]
    [InlineData("reason", "required", "Enter a reason; it's sent to the people affected and kept in the audit log.")]
    [InlineData("contentType", "unsupported_or_mismatched", "Choose a JPEG, PNG or WebP image.")]
    [InlineData("dataBase64", "invalid", "That file couldn't be read as an image.")]
    [InlineData("dataBase64", "too_large", "Images may be at most 5 MiB.")]
    [InlineData("images", "count_out_of_range", "A listing has at most four images.")]
    public void A_field_error_reads_as_a_sentence_without_the_api_field_name_or_code(string field, string code, string expected)
    {
        Assert.Equal(expected, Shown(Invalid((field, code))));
    }

    [Fact]
    public void Several_field_errors_are_one_line_each()
    {
        Assert.Equal(
            "Enter a display name.\nEnter a valid email address.",
            Shown(Invalid(("displayName", "required"), ("email", "invalid"))));
    }

    [Theory]
    [InlineData("reason", "required, 1 to 500 characters", "Enter a reason; it's sent to the people affected and kept in the audit log.")]
    [InlineData("profile", "one of smoke, realistic, hot-burst", "The profile must be one of smoke, realistic, hot-burst.")]
    public void A_field_error_that_states_its_rule_reads_as_a_sentence(string field, string code, string expected)
    {
        Assert.Equal(expected, Shown(Invalid((field, code))));
    }

    [Fact]
    public void A_field_error_the_client_has_no_sentence_for_still_names_the_field_in_words()
    {
        Assert.Equal("Check the reserve (something_new).", Shown(Invalid(("reserveCents", "something_new"))));
    }

    [Fact]
    public void A_request_refused_because_the_session_ended_says_to_sign_in_again()
    {
        Assert.Equal("You're signed out. Sign in again to continue.", Shown(new() { Status = 401, Title = "Unauthorized" }));
    }

    [Fact]
    public void A_busy_engine_says_to_try_again_without_naming_a_header()
    {
        Assert.Equal(
            "The auction service is busy. Try again in a moment.",
            Shown(new() { Status = 503, Code = "busy", Title = "The auction service is busy; retry with the same Idempotency-Key." }));
    }
}
