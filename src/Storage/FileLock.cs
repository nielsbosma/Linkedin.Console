namespace Linkedin.Console.Storage;

/// <summary>
/// Cross-process exclusive lock, held while an export folder is written.
///
/// A scheduled <c>me sync</c> that overlaps the previous run would otherwise read the same position
/// and append the same events twice.
/// </summary>
public sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static async Task<FileLock> AcquireAsync(string path, CancellationToken ct, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var delay = 25;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
                return new FileLock(stream);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 > deadline)
                    throw new IOException($"Timed out waiting for the lock at {path}. Another linkedin process may be stuck.");

                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 400);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
