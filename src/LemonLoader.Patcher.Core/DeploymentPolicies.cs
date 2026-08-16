public enum DeploymentFilePolicy
{
    Seed,
    Upgrade,
    Refresh,
    Enforce
}

public enum DeploymentProfile
{
    Development,
    Production,
    Locked
}

public sealed record DeploymentPolicyRule(
    string Pattern,
    DeploymentFilePolicy Policy,
    string MatchPath,
    bool Exact)
{
    public static DeploymentPolicyRule Parse(string value)
    {
        var separator = value.LastIndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException(
                "Deployment policy rules must use '<path-or-directory/**>=<policy>'.");

        var pattern = value[..separator].Trim().Replace('\\', '/');
        var policy = DeploymentPolicyOptions.ParsePolicy(value[(separator + 1)..]);
        var exact = !pattern.EndsWith("/**", StringComparison.Ordinal);
        var matchPath = exact ? pattern : pattern[..^3];
        DeploymentPolicyOptions.ValidateRelativePath(matchPath, "deployment policy rule");
        if (matchPath.Contains('*'))
            throw new ArgumentException(
                $"Deployment policy pattern '{pattern}' contains an unsupported wildcard.");
        return new(exact ? matchPath : $"{matchPath}/**", policy, matchPath, exact);
    }

    public bool Matches(string path) => Exact
        ? string.Equals(path, MatchPath, StringComparison.Ordinal)
        : path.StartsWith(MatchPath + '/', StringComparison.Ordinal);
}

public sealed record DeploymentPolicyOptions(
    DeploymentProfile Profile,
    IReadOnlyList<DeploymentPolicyRule> Rules)
{
    public static DeploymentPolicyOptions Create(
        string? profile,
        IEnumerable<string> rules)
    {
        var parsedProfile = (profile ?? "development").Trim().ToLowerInvariant() switch
        {
            "development" => DeploymentProfile.Development,
            "production" => DeploymentProfile.Production,
            "locked" => DeploymentProfile.Locked,
            _ => throw new ArgumentException(
                "Deployment profile must be 'development', 'production', or 'locked'.")
        };
        var parsedRules = rules.Select(DeploymentPolicyRule.Parse).ToArray();
        var duplicate = parsedRules
            .GroupBy(rule => rule.Pattern, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new ArgumentException(
                $"Deployment policy rule '{duplicate.Key}' was supplied more than once.");
        return new(parsedProfile, parsedRules);
    }

    public DeploymentFilePolicy Resolve(string relativePath)
    {
        ValidateRelativePath(relativePath, "deployment file");
        var explicitRule = Rules
            .Where(rule => rule.Matches(relativePath))
            .OrderByDescending(rule => rule.Exact)
            .ThenByDescending(rule => rule.MatchPath.Length)
            .FirstOrDefault();
        if (explicitRule is not null)
            return explicitRule.Policy;

        var topLevel = relativePath.Split('/', 2)[0];
        return Profile switch
        {
            DeploymentProfile.Development => DeploymentFilePolicy.Seed,
            DeploymentProfile.Production when topLevel is "Mods" or "Plugins" or "UserLibs" =>
                DeploymentFilePolicy.Refresh,
            DeploymentProfile.Production when topLevel == "UserData" =>
                DeploymentFilePolicy.Upgrade,
            DeploymentProfile.Locked when topLevel is "Mods" or "Plugins" or "UserLibs" =>
                DeploymentFilePolicy.Enforce,
            DeploymentProfile.Locked when topLevel == "UserData" =>
                DeploymentFilePolicy.Upgrade,
            _ => DeploymentFilePolicy.Seed
        };
    }

    public void ValidateRuleCoverage(IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.ToArray();
        foreach (var rule in Rules)
        {
            if (!paths.Any(rule.Matches))
            {
                throw new InvalidDataException(
                    $"Deployment policy rule '{rule.Pattern}' does not match any packaged file.");
            }
        }
    }

    public static string ToManifestValue(DeploymentFilePolicy policy) => policy switch
    {
        DeploymentFilePolicy.Seed => "seed",
        DeploymentFilePolicy.Upgrade => "upgrade",
        DeploymentFilePolicy.Refresh => "refresh",
        DeploymentFilePolicy.Enforce => "enforce",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    internal static DeploymentFilePolicy ParsePolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "seed" => DeploymentFilePolicy.Seed,
            "upgrade" => DeploymentFilePolicy.Upgrade,
            "refresh" => DeploymentFilePolicy.Refresh,
            "enforce" => DeploymentFilePolicy.Enforce,
            _ => throw new ArgumentException(
                "Deployment policy must be 'seed', 'upgrade', 'refresh', or 'enforce'.")
        };

    internal static void ValidateRelativePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('\\') ||
            path.Any(character => char.IsControl(character) || character == '|'))
        {
            throw new ArgumentException(
                $"The {description} path '{path}' is not a normalized relative path.");
        }
        if (path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException(
                $"The {description} path '{path}' contains an unsafe segment.");

        var topLevel = path.Split('/', 2)[0];
        if (topLevel == "MelonLoader" ||
            topLevel == ".packaged-deployment" ||
            topLevel.StartsWith(".lemonloader-", StringComparison.Ordinal) ||
            topLevel.StartsWith(".lemon-staging-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The {description} path '{path}' uses a bootstrap-reserved directory.");
        }
    }
}
