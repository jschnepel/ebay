using System.Text.Json;

namespace Auction.StackTests;

/// <summary>The containers themselves: all up and healthy, none running as root, nothing published beyond loopback.</summary>
[Collection(StackRuns.Name)]
public sealed class ContainerTests(StackUnderTest stack)
{
    private static readonly string[] LongRunning = ["postgres", "redis", "engine", "api", "simulator-control"];

    [Fact]
    public async Task Every_service_is_running_and_healthy_and_the_migrator_finished_cleanly()
    {
        var services = await ServicesAsync();

        foreach (var name in LongRunning)
        {
            var service = Assert.Contains(name, services);
            Assert.Equal(("running", "healthy"), (service.GetProperty("State").GetString(), service.GetProperty("Health").GetString()));
        }

        var migrator = Assert.Contains("migrator", services);
        Assert.Equal(("exited", 0), (migrator.GetProperty("State").GetString(), migrator.GetProperty("ExitCode").GetInt32()));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("engine")]
    [InlineData("migrator")]
    [InlineData("simulator-control")]
    public async Task An_application_container_runs_as_a_user_other_than_root(string service)
    {
        var user = (await StackUnderTest.DockerAsync("inspect", "--format", "{{.Config.User}}", await stack.ContainerOfAsync(service))).Trim();

        Assert.False(user is "" or "root" or "0" || user.StartsWith("0:", StringComparison.Ordinal), $"{service} runs as '{user}'.");
    }

    [Fact]
    public async Task Every_long_running_service_comes_back_by_itself_after_docker_or_the_machine_restarts()
    {
        foreach (var service in LongRunning)
        {
            var policy = (await StackUnderTest.DockerAsync("inspect", "--format", "{{.HostConfig.RestartPolicy.Name}}", await stack.ContainerOfAsync(service))).Trim();

            Assert.True(policy is "unless-stopped" or "always", $"{service} has restart policy '{policy}'.");
        }
    }

    [Fact]
    public async Task Only_the_api_and_the_data_stores_publish_a_port_and_only_on_the_loopback_address()
    {
        foreach (var service in LongRunning)
        {
            var bindings = await HostBindingsAsync(service);
            if (service is "engine" or "simulator-control")
            {
                Assert.Empty(bindings);
            }
            else
            {
                Assert.NotEmpty(bindings);
                Assert.All(bindings, address => Assert.Equal("127.0.0.1", address));
            }
        }
    }

    private async Task<Dictionary<string, JsonElement>> ServicesAsync()
    {
        var output = (await stack.ComposeAsync("ps", "-a", "--format", "json")).Trim();
        // Compose prints one JSON object per line (older versions print one array).
        var rows = output.StartsWith('[')
            ? JsonDocument.Parse(output).RootElement.EnumerateArray().Select(row => row.Clone())
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone());
        return rows.ToDictionary(row => row.GetProperty("Service").GetString()!);
    }

    private async Task<List<string>> HostBindingsAsync(string service)
    {
        var ports = JsonDocument.Parse(await StackUnderTest.DockerAsync("inspect", "--format", "{{json .NetworkSettings.Ports}}", await stack.ContainerOfAsync(service))).RootElement;
        return ports.ValueKind != JsonValueKind.Object
            ? []
            : [.. ports.EnumerateObject()
                .Where(port => port.Value.ValueKind == JsonValueKind.Array)
                .SelectMany(port => port.Value.EnumerateArray())
                .Select(binding => binding.GetProperty("HostIp").GetString() ?? "")];
    }
}
