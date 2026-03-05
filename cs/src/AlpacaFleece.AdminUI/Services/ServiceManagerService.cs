using System.Net.Sockets;
using System.Text.Json;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Manages the bot Docker container via the Docker Engine API over a Unix domain socket.
/// Gracefully degrades when the socket is unavailable (e.g., dev environment).
/// </summary>
public sealed class ServiceManagerService(
    IOptions<AdminOptions> options,
    ILogger<ServiceManagerService> logger)
{
    private const string DockerApiVersion = "v1.47";
    private readonly string _containerName = options.Value.BotContainerName;

    private HttpClient CreateDockerClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint("/var/run/docker.sock");
                await sock.ConnectAsync(endpoint, ct);
                return new NetworkStream(sock, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    public async ValueTask<ContainerStatus?> GetContainerStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateDockerClient();
            var url = $"/{DockerApiVersion}/containers/{_containerName}/json";
            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var stateEl = root.GetProperty("State");
            var state = stateEl.GetProperty("Status").GetString() ?? "unknown";
            var running = stateEl.GetProperty("Running").GetBoolean();

            DateTimeOffset? startedAt = null;
            if (stateEl.TryGetProperty("StartedAt", out var startedEl))
            {
                var startedStr = startedEl.GetString();
                if (!string.IsNullOrEmpty(startedStr) && DateTimeOffset.TryParse(startedStr, out var dt))
                    startedAt = dt;
            }

            // Stats require a separate call
            double cpuPct = 0;
            long memUsage = 0, memLimit = 0;
            try
            {
                var statsResp = await client.GetAsync(
                    $"/{DockerApiVersion}/containers/{_containerName}/stats?stream=false", ct);
                if (statsResp.IsSuccessStatusCode)
                {
                    var statsJson = await statsResp.Content.ReadAsStringAsync(ct);
                    using var statsDoc = JsonDocument.Parse(statsJson);
                    var sr = statsDoc.RootElement;
                    if (sr.TryGetProperty("memory_stats", out var mem))
                    {
                        memUsage = mem.TryGetProperty("usage", out var u) ? u.GetInt64() : 0;
                        memLimit = mem.TryGetProperty("limit", out var l) ? l.GetInt64() : 0;
                    }
                    if (sr.TryGetProperty("cpu_stats", out var cpu) &&
                        sr.TryGetProperty("precpu_stats", out var preCpu))
                    {
                        var cpuDelta = cpu.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble() -
                                       preCpu.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble();
                        var sysDelta = cpu.GetProperty("system_cpu_usage").GetDouble() -
                                       preCpu.GetProperty("system_cpu_usage").GetDouble();
                        var numCpu = cpu.GetProperty("online_cpus").GetInt32();
                        if (sysDelta > 0) cpuPct = cpuDelta / sysDelta * numCpu * 100.0;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not fetch container stats");
            }

            var statusStr = running
                ? $"Up since {startedAt?.LocalDateTime:g}"
                : $"Exited ({state})";

            return new ContainerStatus(
                State: state,
                Status: statusStr,
                IsRunning: running,
                StartedAt: startedAt,
                CpuPercent: Math.Round(cpuPct, 2),
                MemoryUsageBytes: memUsage,
                MemoryLimitBytes: memLimit);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Docker socket unavailable, returning null status");
            return null;
        }
    }

    public async ValueTask StartContainerAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateDockerClient();
            var resp = await client.PostAsync(
                $"/{DockerApiVersion}/containers/{_containerName}/start",
                content: null, ct);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("Container {Name} start requested", _containerName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start container {Name}", _containerName);
            throw;
        }
    }

    public async ValueTask StopContainerAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateDockerClient();
            var resp = await client.PostAsync(
                $"/{DockerApiVersion}/containers/{_containerName}/stop",
                content: null, ct);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("Container {Name} stop requested", _containerName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop container {Name}", _containerName);
            throw;
        }
    }

    public async ValueTask RestartContainerAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateDockerClient();
            var resp = await client.PostAsync(
                $"/{DockerApiVersion}/containers/{_containerName}/restart",
                content: null, ct);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("Container {Name} restart requested", _containerName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restart container {Name}", _containerName);
            throw;
        }
    }
}
