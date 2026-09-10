using System.Diagnostics;

internal static class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static Task RunAsync(
        string fileName,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        RunAsync(fileName, progress, cancellationToken, null, arguments);

    public static async Task RunAsync(
        string fileName,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environment is not null)
        {
            foreach (var pair in environment)
                info.Environment[pair.Key] = pair.Value;
        }
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        progress?.Report(new(PatcherMessageKind.Stage, $"Running {Path.GetFileName(fileName)}"));
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var standardOutput = ForwardOutputAsync(process.StandardOutput, progress, linked.Token);
        var standardError = ForwardOutputAsync(process.StandardError, progress, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(standardOutput, standardError);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(standardOutput, standardError); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"'{fileName}' did not finish within {DefaultTimeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");
    }

    private static async Task ForwardOutputAsync(
        StreamReader reader,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(new(PatcherMessageKind.ToolOutput, line));
        }
    }
}
