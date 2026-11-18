using Auction.Web;
using Auction.Web.Client;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same origin as the API, so the browser sends the session cookie with every call.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AuctionApi>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<LiveUpdates>();
builder.Services.AddTransient<ViewRefresher>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
