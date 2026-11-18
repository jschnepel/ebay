using Auction.Engine.Hosting;

if (args.Contains(HealthProbe.Argument))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.AddEngine();

var app = builder.Build();
app.MapEngine();
await app.RunAsync();
return 0;
