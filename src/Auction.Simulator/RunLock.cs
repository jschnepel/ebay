namespace Auction.Simulator;

/// <summary>
/// A machine-wide lock file held for the whole run: the operating system releases it if the process dies,
/// so a crashed run never blocks the next one.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _file;

    private RunLock(FileStream file) => _file = file;

    public static string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "auction-simulator.lock");

    public static RunLock? TryTake()
    {
        try
        {
            return new RunLock(new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
