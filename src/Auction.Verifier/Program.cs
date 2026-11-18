using System.Globalization;
using System.Text.Json;
using Auction.Verifier;
using Npgsql;

// auction-verifier [--since <ISO 8601 time>]
// Reads VERIFIER_CONNECTION_STRING (the read-only verifier role). Prints a JSON report; exits 0 when every
// invariant holds, 1 when any is violated, 2 when it can't run.
var connectionString = Environment.GetEnvironmentVariable("VERIFIER_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync("VERIFIER_CONNECTION_STRING is not set.");
    return 2;
}

DateTimeOffset? since = null;
var sinceAt = Array.IndexOf(args, "--since");
if (sinceAt >= 0)
{
    if (sinceAt + 1 >= args.Length
        || !DateTimeOffset.TryParse(args[sinceAt + 1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
    {
        await Console.Error.WriteLineAsync("--since needs an ISO 8601 time.");
        return 2;
    }

    since = parsed;
}

try
{
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    var report = await InvariantVerifier.VerifyAsync(dataSource, new VerifierScope(since), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return report.Passed ? 0 : 1;
}
catch (NpgsqlException e)
{
    await Console.Error.WriteLineAsync($"The database can't be read: {e.Message}");
    return 2;
}
