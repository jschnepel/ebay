using System.Net;
using Auction.IntegrationTests.Database;
using Auction.Migrator;

namespace Auction.IntegrationTests.Api;

/// <summary>The demo seed: password accounts that sign in through the API, and active auctions anyone can browse.</summary>
public sealed class DemoSeedTests(MigratedDatabase db)
{
    private static readonly DemoSeedOptions Passwords = new("admin-pass-for-tests", "demo-pass-for-tests");

    [Fact]
    public async Task The_seed_adds_24_live_sellers_as_simulator_accounts_once_each_labelled_simulated()
    {
        var connection = db.ConnectionStringFor(MigratedDatabase.Migrator);
        var options = Passwords with { SimulatorPassword = "simulator-pass-for-tests" };
        await DemoSeed.RunAsync(connection, options, TestContext.Current.CancellationToken);

        var again = await DemoSeed.RunAsync(connection, options, TestContext.Current.CancellationToken);

        Assert.Equal(0, again.AccountsAdded);
        var sellers = Enumerable.Range(1, DemoSeedOptions.SimulatorLiveSellers).Select(DemoSeedOptions.SimulatorLiveSellerEmail).ToArray();
        Assert.Equal("sim-seller-01@auction.test", sellers[0]);
        Assert.Equal((24L, 24L, 24L), (
            await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE email = ANY($1) AND kind = 'simulator'", (object)sellers),
            await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(DISTINCT display_name) FROM users WHERE email = ANY($1)", (object)sellers),
            await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE email = ANY($1) AND display_name LIKE '% (simulated)'", (object)sellers)));
    }

    [Fact]
    public async Task Seeding_twice_adds_the_accounts_and_auctions_once_and_keeps_the_first_passwords()
    {
        await DemoSeed.RunAsync(db.ConnectionStringFor(MigratedDatabase.Migrator), Passwords, TestContext.Current.CancellationToken);

        var again = await DemoSeed.RunAsync(
            db.ConnectionStringFor(MigratedDatabase.Migrator), new DemoSeedOptions("changed", "changed"), TestContext.Current.CancellationToken);

        Assert.Equal(new DemoSeedResult(0, 0), again);
        Assert.Equal(3, await db.ScalarAsync<long>(
            MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE email IN ($1, $2, $3)",
            DemoSeedOptions.AdminEmail, DemoSeedOptions.SellerEmail, DemoSeedOptions.BuyerEmail));
        var api = await db.ApiAsync();
        var (_, signIn) = await api.SignInAsync(DemoSeedOptions.AdminEmail, Passwords.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal("admin", (await signIn.JsonAsync()).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Seeded_accounts_need_their_password_and_the_seeded_auctions_are_active()
    {
        await DemoSeed.RunAsync(db.ConnectionStringFor(MigratedDatabase.Migrator), Passwords, TestContext.Current.CancellationToken);
        var api = await db.ApiAsync();

        var (_, emailOnly) = await api.SignInAsync(DemoSeedOptions.SellerEmail);
        var (seller, withPassword) = await api.SignInAsync(DemoSeedOptions.SellerEmail, Passwords.DemoPassword);

        await emailOnly.AssertProblemAsync(HttpStatusCode.Unauthorized, "sign_in_failed");
        Assert.Equal(HttpStatusCode.OK, withPassword.StatusCode);
        var mine = (await seller.GetJsonAsync("/api/v1/me/auctions?pageSize=5")).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, auction => Assert.Equal("active", auction.GetProperty("status").GetString()));
        var first = mine[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await api.Anonymous().StatusOfGetAsync($"/api/v1/auctions/{first}"));
    }
}
