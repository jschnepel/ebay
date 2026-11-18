using Auction.Api.Hosting;

if (args.Contains(HealthProbe.Argument))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.AddApi();

var app = builder.Build();
app.MapApi();
await app.RunAsync();
return 0;
