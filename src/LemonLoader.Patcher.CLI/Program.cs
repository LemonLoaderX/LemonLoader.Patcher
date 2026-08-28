using System.Reflection;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await CliApplication.RunAsync(
        args,
        Console.Out,
        Console.Error,
        cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

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
            .Where(argument => !string.Equals(argument, "--verbose", StringComparison.Ordinal))
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
            switch (effectiveArgs[0])
            {
                case "patch":
                    if (effectiveArgs.Length == 2 && effectiveArgs[1] is "--help" or "-h")
                    {
                        await output.WriteLineAsync(PatchHelp);
                        return Success;
                    }
                    var patchRequest = ParsePatchRequest(effectiveArgs[1..]);
                    var patchResult = await new ApkPatchPipeline(patchRequest, progress)
                        .RunAsync(cancellationToken);
                    await output.WriteLineAsync($"output: {patchResult.OutputApkPath}");
                    await output.WriteLineAsync($"sha256: {patchResult.Sha256}");
                    await output.WriteLineAsync($"unity-version: {patchResult.UnityVersion}");
                    return Success;

                case "unity-dependencies":
                    if (effectiveArgs.Length == 2 && effectiveArgs[1] is "--help" or "-h")
                    {
                        await output.WriteLineAsync(UnityDependenciesHelp);
                        return Success;
                    }
                    var dependencyRequest = ParseUnityDependenciesRequest(effectiveArgs[1..]);
                    var dependencyResult = await UnityDependenciesPipeline.RunAsync(
                        dependencyRequest,
                        progress,
                        cancellationToken);
                    await output.WriteLineAsync($"output: {dependencyResult.OutputPath}");
                    await output.WriteLineAsync($"package-version: {dependencyResult.PackageVersion}");
                    await output.WriteLineAsync($"source: {dependencyResult.Source}");
                    await output.WriteLineAsync($"assemblies: {dependencyResult.AssemblyCount}");
                    return Success;

                default:
                    throw new CliUsageException(
                        $"Unknown command '{effectiveArgs[0]}'. Run 'LemonLoader.Patcher.CLI --help'.");
            }
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

    internal static PatchRequest ParsePatchRequest(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            throw new CliUsageException("Missing input APK. Run 'LemonLoader.Patcher.CLI patch --help'.");

        var parsed = CliOptions.Parse(
            args[1..],
            [
                "--output", "--release", "--deployment", "--profile", "--policy",
                "--game-assembly", "--metadata", "--unity-version", "--unity-libraries",
                "--interop-output", "--cpp2il", "--il2cppinterop-cli", "--sdk", "--keystore", "--key-alias"
            ],
            ["--policy"]);
        var keystore = parsed.Optional("--keystore");
        SigningOptions? signing = null;
        if (keystore is not null)
        {
            var alias = parsed.Required("--key-alias");
            var storePassword = Environment.GetEnvironmentVariable(
                "LEMONLOADER_KEYSTORE_PASSWORD");
            if (string.IsNullOrWhiteSpace(storePassword))
            {
                throw new CliUsageException(
                    "Signing requires LEMONLOADER_KEYSTORE_PASSWORD.");
            }
            signing = new(
                keystore,
                storePassword,
                alias,
                Environment.GetEnvironmentVariable("LEMONLOADER_KEY_PASSWORD"));
        }
        else if (parsed.Optional("--key-alias") is not null)
        {
            throw new CliUsageException("--key-alias requires --keystore.");
        }

        try
        {
            return new PatchRequest
            {
                InputApkPath = args[0],
                OutputApkPath = parsed.Required("--output"),
                ReleasePath = parsed.Optional("--release"),
                DeploymentPath = parsed.Optional("--deployment"),
                DeploymentPolicies = DeploymentPolicyOptions.Create(
                    parsed.Optional("--profile"),
                    parsed.Many("--policy")),
                GameAssemblyPath = parsed.Optional("--game-assembly"),
                MetadataPath = parsed.Optional("--metadata"),
                UnityVersion = parsed.Optional("--unity-version"),
                UnityLibrariesPath = parsed.Optional("--unity-libraries"),
                InteropOutputPath = parsed.Optional("--interop-output"),
                Cpp2IlPath = parsed.Optional("--cpp2il"),
                Il2CppInteropCliPath = parsed.Optional("--il2cppinterop-cli"),
                AndroidSdkRoot = parsed.Optional("--sdk"),
                Signing = signing
            }.NormalizeAndValidate();
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException(exception.Message, exception);
        }
    }

    internal static UnityDependenciesRequest ParseUnityDependenciesRequest(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException(
                "Missing Unity version. Run 'LemonLoader.Patcher.CLI unity-dependencies --help'.");
        }
        var parsed = CliOptions.Parse(args[1..], ["--output", "--cache"]);
        return new()
        {
            UnityVersion = args[0],
            OutputPath = parsed.Required("--output"),
            CachePath = parsed.Optional("--cache")
        };
    }

    private static string GetUsefulMessage(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var messages = aggregate.Flatten().InnerExceptions
                .Select(GetUsefulMessage)
                .Distinct(StringComparer.Ordinal);
            return string.Join("; ", messages);
        }
        return exception.Message;
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
          patch                 Build a LemonLoader-enabled Android IL2CPP APK
          unity-dependencies    Restore Unity managed reference assemblies

        Global options:
          -h, --help            Show help
          --version             Show version
          --verbose             Include exception details when a command fails

        Run 'LemonLoader.Patcher.CLI <command> --help' for command options.
        """;

    private const string PatchHelp = """
        Build a LemonLoader-enabled Android IL2CPP APK.

        Usage:
          LemonLoader.Patcher.CLI patch <input.apk> --output <output.apk> [options]

        Required:
          <input.apk>                     Original ARM64 Unity IL2CPP APK
          --output <path>                 Output APK. The input APK is never overwritten.

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

        Android output and signing:
          --sdk <directory>               Android SDK root; otherwise uses ANDROID_SDK_ROOT
          --keystore <path>               Sign the APK with this keystore
          --key-alias <name>              Required when --keystore is supplied

        Signing passwords are read from LEMONLOADER_KEYSTORE_PASSWORD and optional
        LEMONLOADER_KEY_PASSWORD. Without --keystore, output is aligned but unsigned.
        Every output APK is zipaligned for 16 KiB pages.

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

internal sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> values;

    private CliOptions(Dictionary<string, List<string>> values) =>
        this.values = values;

    public static CliOptions Parse(
        string[] args,
        IReadOnlyList<string> supported,
        IReadOnlyList<string>? repeatable = null)
    {
        repeatable ??= [];
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!supported.Contains(option))
                throw new CliUsageException($"Unknown option '{option}'.");
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException($"Missing value for '{option}'.");
            if (!values.TryGetValue(option, out var optionValues))
            {
                optionValues = [];
                values.Add(option, optionValues);
            }
            else if (!repeatable.Contains(option))
            {
                throw new CliUsageException($"Option '{option}' was supplied more than once.");
            }
            optionValues.Add(args[index]);
        }
        return new(values);
    }

    public string Required(string option) =>
        Optional(option) ?? throw new CliUsageException($"Missing required option '{option}'.");

    public string? Optional(string option) =>
        values.TryGetValue(option, out var optionValues) ? optionValues[0] : null;

    public IReadOnlyList<string> Many(string option) =>
        values.TryGetValue(option, out var optionValues) ? optionValues : [];
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
