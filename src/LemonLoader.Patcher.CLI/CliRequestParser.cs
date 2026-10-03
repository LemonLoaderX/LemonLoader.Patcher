internal static class CliRequestParser
{
    private static readonly string[] PatchValueOptions =
    [
        "--output", "--release", "--runtime", "--deployment", "--profile", "--policy",
        "--game-assembly", "--metadata", "--unity-version", "--unity-libraries",
        "--interop-output", "--cpp2il", "--il2cppinterop-cli", "--zipalign",
        "--keystore", "--key-alias", "--apksigner", "--interop"
    ];

    public static PatchRequest ParsePatchRequest(string[] args, bool injectionOnly = false)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            throw new CliUsageException(
                "Missing input APK or directory. " +
                "Run 'LemonLoader.Patcher.CLI patch --help'.");
        }
        var parsed = CliOptions.Parse(
            args[1..],
            injectionOnly ? PatchValueOptions.Where(option => option is not
                ("--game-assembly" or "--metadata" or "--unity-version" or "--unity-libraries" or
                 "--interop-output" or "--cpp2il" or "--il2cppinterop-cli")).ToArray() : PatchValueOptions,
            ["--policy"],
            ["--align"]);
        try
        {
            return new PatchRequest
            {
                InputPath = args[0],
                OutputPath = parsed.Optional("--output"),
                ReleasePath = parsed.Optional("--release"),
                RuntimeVariant = parsed.Optional("--runtime"),
                DeploymentPath = parsed.Optional("--deployment"),
                DeploymentPolicies = DeploymentPolicyOptions.Create(
                    parsed.Optional("--profile"),
                    parsed.Many("--policy")),
                GameAssemblyPath = parsed.Optional("--game-assembly"),
                MetadataPath = parsed.Optional("--metadata"),
                UnityVersion = parsed.Optional("--unity-version"),
                UnityLibrariesPath = parsed.Optional("--unity-libraries"),
                InteropOutputPath = parsed.Optional("--interop-output"),
                InteropInputPath = injectionOnly ? parsed.Required("--interop") : parsed.Optional("--interop"),
                Cpp2IlPath = parsed.Optional("--cpp2il"),
                Il2CppInteropCliPath = parsed.Optional("--il2cppinterop-cli"),
                AlignApk = parsed.Has("--align"),
                ZipAlignPath = parsed.Optional("--zipalign"),
                ApkSignerPath = parsed.Optional("--apksigner"),
                Signing = ParseSigning(parsed)
            }.NormalizeAndValidate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new CliUsageException(exception.Message, exception);
        }
    }

    public static InteropRequest ParseInteropRequest(string[] args)
    {
        var hasInput = args.Length > 0 && !args[0].StartsWith('-');
        var parsed = CliOptions.Parse(hasInput ? args[1..] : args,
            ["--output", "--game-assembly", "--metadata", "--unity-version", "--unity-libraries", "--cpp2il", "--il2cppinterop-cli"]);
        try
        {
            return new InteropRequest
            {
                InputPath = hasInput ? args[0] : null, OutputPath = parsed.Required("--output"),
                GameAssemblyPath = parsed.Optional("--game-assembly"), MetadataPath = parsed.Optional("--metadata"),
                UnityVersion = parsed.Optional("--unity-version"), UnityLibrariesPath = parsed.Optional("--unity-libraries"),
                Cpp2IlPath = parsed.Optional("--cpp2il"), Il2CppInteropCliPath = parsed.Optional("--il2cppinterop-cli")
            }.NormalizeAndValidate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        { throw new CliUsageException(exception.Message, exception); }
    }

    public static ApkProcessingRequest ParseProcessingRequest(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new CliUsageException("Missing input APK. Run 'LemonLoader.Patcher.CLI process-apk --help'.");
        var parsed = CliOptions.Parse(args[1..],
            ["--output", "--zipalign", "--keystore", "--key-alias", "--apksigner"], switches: ["--align"]);
        try
        {
            var request = new ApkProcessingRequest
            {
                InputPath = args[0], OutputPath = parsed.Required("--output"),
                AlignApk = parsed.Has("--align"), ZipAlignPath = parsed.Optional("--zipalign"),
                ApkSignerPath = parsed.Optional("--apksigner"), Signing = ParseSigning(parsed)
            };
            _ = new ApkProcessingPipeline(request);
            return request;
        }
        catch (ArgumentException exception) { throw new CliUsageException(exception.Message, exception); }
    }

    public static UnityDependenciesRequest ParseUnityDependenciesRequest(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            throw new CliUsageException(
                "Missing Unity version. " +
                "Run 'LemonLoader.Patcher.CLI unity-dependencies --help'.");
        }
        var parsed = CliOptions.Parse(args[1..], ["--output", "--cache"]);
        return new()
        {
            UnityVersion = args[0],
            OutputPath = parsed.Required("--output"),
            CachePath = parsed.Optional("--cache")
        };
    }

    private static SigningOptions? ParseSigning(CliOptions parsed)
    {
        var keystore = parsed.Optional("--keystore");
        if (keystore is null)
        {
            if (parsed.Optional("--key-alias") is not null)
                throw new CliUsageException("--key-alias requires --keystore.");
            return null;
        }

        var storePassword = Environment.GetEnvironmentVariable(
            "LEMONLOADER_KEYSTORE_PASSWORD");
        if (string.IsNullOrWhiteSpace(storePassword))
        {
            throw new CliUsageException(
                "Signing requires LEMONLOADER_KEYSTORE_PASSWORD.");
        }
        return new(
            keystore,
            storePassword,
            parsed.Required("--key-alias"),
            Environment.GetEnvironmentVariable("LEMONLOADER_KEY_PASSWORD"));
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
        IReadOnlyList<string>? repeatable = null,
        IReadOnlyList<string>? switches = null)
    {
        repeatable ??= [];
        switches ??= [];
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!supported.Contains(option) && !switches.Contains(option))
                throw new CliUsageException($"Unknown option '{option}'.");
            if (switches.Contains(option))
            {
                if (!values.TryAdd(option, []))
                {
                    throw new CliUsageException(
                        $"Option '{option}' was supplied more than once.");
                }
                continue;
            }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException($"Missing value for '{option}'.");
            if (!values.TryGetValue(option, out var optionValues))
            {
                optionValues = [];
                values.Add(option, optionValues);
            }
            else if (!repeatable.Contains(option))
            {
                throw new CliUsageException(
                    $"Option '{option}' was supplied more than once.");
            }
            optionValues.Add(args[index]);
        }
        return new(values);
    }

    public string Required(string option) =>
        Optional(option) ?? throw new CliUsageException(
            $"Missing required option '{option}'.");

    public string? Optional(string option) =>
        values.TryGetValue(option, out var optionValues) ? optionValues[0] : null;

    public IReadOnlyList<string> Many(string option) =>
        values.TryGetValue(option, out var optionValues) ? optionValues : [];

    public bool Has(string option) => values.ContainsKey(option);
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
