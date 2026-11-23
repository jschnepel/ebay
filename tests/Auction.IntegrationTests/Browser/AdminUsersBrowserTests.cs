using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The admin Users screen in a browser: the reason popover beside each user.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class AdminUsersBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task Disabling_a_user_asks_for_the_reason_beside_their_row()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            return; // The target account is made through the in-process API.
        }

        var name = $"Target {Guid.NewGuid():N}"[..14];
        await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Popover admin", role: "Admin");

        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        var row = admin.GetByTestId("user-row").Filter(new() { HasText = name });
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByLabel("Reason")).ToHaveCountAsync(0);

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        var popover = admin.GetByRole(AriaRole.Dialog, new() { Name = $"Disable {name}?" });
        await Assertions.Expect(popover).ToBeVisibleAsync();
        await Assertions.Expect(popover.GetByLabel("Reason")).ToBeFocusedAsync();
        await Assertions.Expect(popover.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true })).ToBeDisabledAsync();
        await popover.GetByLabel("Reason").FillAsync("Spam listings");
        await popover.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();

        await Assertions.Expect(popover).ToHaveCountAsync(0);
        await Assertions.Expect(row).ToContainTextAsync("Disabled");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_popover_closes_on_escape_without_changing_anything()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            return;
        }

        var name = $"Kept {Guid.NewGuid():N}"[..12];
        await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Escaping admin", role: "Admin");
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        var row = admin.GetByTestId("user-row").Filter(new() { HasText = name });

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await admin.Keyboard.PressAsync("Escape");

        await Assertions.Expect(admin.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Assertions.Expect(row).ToContainTextAsync("Enabled");
    }
}
