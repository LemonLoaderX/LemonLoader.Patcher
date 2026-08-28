using System.Reflection;

public static class CliApplication
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;
    public const int Cancelled = 130;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var verbose = args.Contains("--verbose", StringComparer.Ordinal);
        var effectiveArgs = args
            .Where(argument => argument != "--verbose")
            .ToArray();
        try
        {
            if (effectiveArgs.Length == 0 || effectiveArgs[0] is "--help" or "-h")
            {
                await output.WriteLineAsync(RootHelp);
                return Success;
            }
            if (effectiveArgs[0] == "--version")
            {
                await output.WriteLineAsync(GetVersion());
                return Success;
            }

            var progress = new CliProgress(error);
            return effectiveArgs[0] switch
            {
                "patch" => await RunPatchAsync(
                    effectiveArgs[1..], output, progress, cancellationToken),
                "unity-dependencies" => await RunUnityDependenciesAsync(
                    effectiveArgs[1..], output, progress, cancellationToken),
                _ => throw new CliUsageException(
                    $"Unknown command '{effectiveArgs[0]}'. " +
                    "Run 'LemonLoader.Patcher.CLI --help'.")
            };
        }
        catch (CliUsageException exception)
        {
            await error.WriteLineAsync($"error: {exception.Message}");
            return UsageError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("error: operation cancelled");
            return Cancelled;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"error: {GetUsefulMessage(exception)}");
            if (verbose)
                await error.WriteLineAsync(exception.ToString());
            return Failure;
        }
    }

    private static async Task<int> RunPatchAsync(
        string[] args,
        TextWriter output,
        IProgress<PatcherMessage> progress,
        CancellationToken cancellationToken)
    {
        if (IsHelpRequest(args))
        {
            await output.WriteLineAsync(PatchHelp);
            return Success;
        }
        var result = await new ApkPatchPipeline(
                CliRequestParser.ParsePatchRequest(args),
                progress)
            .RunAsync(cancellationToken);
        await output.WriteLineAsync($"output: {result.OutputPath}");
        if (result.Sha256 is not null)
            await output.WriteLineAsync($"sha256: {result.Sha256}");
        await output.WriteLineAsync($"unity-version: {result.UnityVersion}");
        return Success;
    }

    private static async Task<int> RunUnityDependenciesAsync(
        string[] args,
        TextWriter output,
        IProgress<PatcherMessage> progress,
        CancellationToken cancellationToken)
    {
        if (IsHelpRequest(args))
        {
            await output.WriteLineAsync(UnityDependenciesHelp);
            return Success;
        }
        var result = await UnityDependenciesPipeline.RunAsync(
            CliRequestParser.ParseUnityDependenciesRequest(args),
            progress,
            cancellationToken);
        await output.WriteLineAsync($"output: {result.OutputPath}");
        await output.WriteLineAsync($"package-version: {result.PackageVersion}");
        await output.WriteLineAsync($"source: {result.Source}");
        await output.WriteLineAsync($"assemblies: {result.AssemblyCount}");
        return Success;
    }

    private static bool IsHelpRequest(string[] args) =>
        args.Length == 1 && args[0] is "--help" or "-h";

    private static string GetUsefulMessage(Exception exception)
    {
        if (exception is not AggregateException aggregate)
            return exception.Message;
        var messages = aggregate.Flatten().InnerExceptions
            .Select(GetUsefulMessage)
            .Distinct(StringComparer.Ordinal);
        return string.Join("; ", messages);
    }

    private static string GetVersion() =>
        typeof(CliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    private const string RootHelp = """
        LemonLoader Patcher

        Usage:
          LemonLoader.Patcher.CLI <command> [options]

        Commands:
          patch                 Inject LemonLoader into an Android IL2CPP APK or directory
          unity-dependencies    Restore Unity managed reference assemblies

        Global options:
          -h, --help            Show help
          --version             Show version
          --verbose             Include exception details when a command fails

        Run 'LemonLoader.Patcher.CLI <command> --help' for command options.
        """;

    private const string PatchHelp = """
        Inject LemonLoader into an Android IL2CPP APK or unpacked directory.

        Usage:
          LemonLoader.Patcher.CLI patch <input.apk> --output <output.apk> [options]
          LemonLoader.Patcher.CLI patch <input-directory> [options]

        Required:
          <input>                         Original ARM64 Unity IL2CPP APK or unpacked directory
          --output <path>                 Required only for APK input; directories are patched in place

        Optional payload:
          --release <path>                Local LemonLoader Release; otherwise latest/cache
          --deployment <directory>        MelonLoader mirror containing Mods, Plugins,
                                          UserLibs, UserData, or future top-level folders
          --profile <name>                development (default), production, or locked
          --policy <path=policy>          Deployment policy override; repeatable

        Optional Interop overrides:
          --game-assembly <path>          libil2cpp.so override
          --metadata <path>               global-metadata.dat override
          --unity-version <version>       Unity version override
          --unity-libraries <directory>   Offline Unity managed reference assemblies
          --interop-output <directory>    Publish generated Interop assemblies
          --cpp2il <path>                 Cpp2IL executable override
          --il2cppinterop-cli <path>      Built Il2CppInterop.CLI.dll override

        Optional APK post-processing:
          --align                         Align the output APK for 16 KiB pages
          --zipalign <path>               zipalign override; otherwise resolved from PATH
          --keystore <path>               Sign the APK with this keystore
          --key-alias <name>              Required when --keystore is supplied
          --apksigner <path>              apksigner override; otherwise resolved from PATH

        Signing passwords are read from LEMONLOADER_KEYSTORE_PASSWORD and optional
        LEMONLOADER_KEY_PASSWORD. APK output is unaligned and unsigned unless the
        corresponding post-processing option is requested. Directory input rejects
        --output and all alignment/signing options.

        Global:
          --verbose                       Include exception details on failure
        """;

    private const string UnityDependenciesHelp = """
        Restore Unity managed reference assemblies.

        Usage:
          LemonLoader.Patcher.CLI unity-dependencies <unity-version> --output <directory>
              [--cache <directory>]

        Required:
          <unity-version>                 Full Unity version, for example 6000.3.8f1
          --output <directory>            Published Unity managed reference directory

        Optional:
          --cache <directory>             Download cache; defaults beside the output
          --verbose                       Include exception details on failure
        """;
}

internal sealed class CliProgress(TextWriter error) : IProgress<PatcherMessage>
{
    public void Report(PatcherMessage value)
    {
        var label = value.Kind switch
        {
            PatcherMessageKind.Stage => "stage",
            PatcherMessageKind.ToolOutput => "tool",
            PatcherMessageKind.Warning => "warning",
            _ => "info"
        };
        error.WriteLine($"{label}: {value.Text}");
    }
}
