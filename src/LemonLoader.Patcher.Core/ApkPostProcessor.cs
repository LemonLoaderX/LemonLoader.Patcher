internal sealed record ApkPostProcessingOptions(
    bool Align,
    string? ZipAlignPath,
    SigningOptions? Signing,
    string? ApkSignerPath);

internal sealed record ResolvedApkPostProcessing(
    string? ZipAlignPath,
    SigningOptions? Signing,
    string? ApkSignerPath);

internal static class ApkPostProcessor
{
    public static ResolvedApkPostProcessing Resolve(ApkPostProcessingOptions options) => new(
        options.Align
            ? ExternalToolResolver.Resolve("zipalign", options.ZipAlignPath, "--zipalign")
            : null,
        options.Signing,
        options.Signing is not null
            ? ExternalToolResolver.Resolve("apksigner", options.ApkSignerPath, "--apksigner")
            : null);

    public static async Task PublishAsync(
        string patchedApk,
        string outputPath,
        string workRoot,
        ResolvedApkPostProcessing options,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken)
    {
        var currentApk = patchedApk;
        if (options.ZipAlignPath is not null)
        {
            var aligned = Path.Combine(workRoot, "aligned.apk");
            progress?.Report(new(
                PatcherMessageKind.Stage,
                "Aligning APK for 16 KiB pages"));
            await ProcessRunner.RunAsync(
                options.ZipAlignPath,
                progress,
                cancellationToken,
                "-P", "16", "-f", "4", patchedApk, aligned);
            await ProcessRunner.RunAsync(
                options.ZipAlignPath,
                progress,
                cancellationToken,
                "-P", "16", "-c", "4", aligned);
            currentApk = aligned;
        }

        if (options.Signing is null)
        {
            DirectoryPublisher.ReplaceFile(currentApk, outputPath);
            return;
        }

        var signed = Path.Combine(workRoot, "signed.apk");
        progress?.Report(new(PatcherMessageKind.Stage, "Signing and verifying APK"));
        var invocation = CreateSigningInvocation(options.Signing, signed, currentApk);
        await ProcessRunner.RunAsync(
            options.ApkSignerPath!,
            progress,
            cancellationToken,
            invocation.Environment,
            invocation.Arguments);
        if (options.ZipAlignPath is not null)
        {
            await ProcessRunner.RunAsync(
                options.ZipAlignPath,
                progress,
                cancellationToken,
                "-P", "16", "-c", "4", signed);
        }
        await ProcessRunner.RunAsync(
            options.ApkSignerPath!,
            progress,
            cancellationToken,
            "verify", "--verbose", signed);
        DirectoryPublisher.ReplaceFile(signed, outputPath);
    }

    internal static SigningInvocation CreateSigningInvocation(
        SigningOptions signing,
        string outputPath,
        string inputPath)
    {
        const string storePasswordVariable = "LEMONLOADER_APKSIGNER_STORE_PASSWORD";
        const string keyPasswordVariable = "LEMONLOADER_APKSIGNER_KEY_PASSWORD";
        return new(
            [
                "sign",
                "--ks", signing.KeystorePath,
                "--ks-key-alias", signing.KeyAlias,
                "--ks-pass", $"env:{storePasswordVariable}",
                "--key-pass", $"env:{keyPasswordVariable}",
                "--v4-signing-enabled", "false",
                "--out", outputPath,
                inputPath
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [storePasswordVariable] = signing.StorePassword,
                [keyPasswordVariable] = signing.KeyPassword ?? signing.StorePassword
            });
    }

    internal sealed record SigningInvocation(
        string[] Arguments,
        IReadOnlyDictionary<string, string> Environment);
}

internal static class ExternalToolResolver
{
    public static string Resolve(
        string toolName,
        string? explicitPath,
        string optionName)
    {
        if (explicitPath is not null)
        {
            var resolved = Path.GetFullPath(explicitPath);
            if (!File.Exists(resolved))
            {
                throw new InvalidOperationException(
                    $"Requested tool '{toolName}' was not found at '{resolved}'.");
            }
            return resolved;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directoryValue in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = directoryValue.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            foreach (var candidateName in GetCandidateNames(toolName))
            {
                var candidate = Path.Combine(directory, candidateName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException(
            $"Requested tool '{toolName}' was not found on PATH. " +
            $"Supply {optionName} <path> or add the tool to PATH.");
    }

    private static IEnumerable<string> GetCandidateNames(string toolName)
    {
        yield return toolName;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(toolName))
            yield break;

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
            pathExtensions = ".COM;.EXE;.BAT;.CMD";
        foreach (var extension in pathExtensions.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = extension.Trim();
            if (!normalized.StartsWith('.'))
                normalized = "." + normalized;
            yield return toolName + normalized.ToLowerInvariant();
        }
    }
}
