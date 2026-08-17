using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string runtimeLocalBuilderName = "System.Reflection.Emit.RuntimeLocalBuilder";

var runtimeMajor = Environment.Version.Major;
var positionalArguments = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--runtime-major" &&
        index + 1 < args.Length &&
        int.TryParse(args[++index], out var parsedRuntimeMajor) &&
        parsedRuntimeMajor >= 1)
    {
        runtimeMajor = parsedRuntimeMajor;
        continue;
    }

    positionalArguments.Add(args[index]);
}

var supportedAssemblies = new Dictionary<string, (string Version, string TypeName)>(StringComparer.Ordinal)
{
    ["MonoMod.Utils"] = ("22.7.31.1", "MonoMod.Utils.Cil.CecilILGenerator"),
    ["0Harmony"] = ("2.10.2.0", "HarmonyLib.Internal.Util.EmitterExtensions")
};

if (positionalArguments.Count == 2 && positionalArguments[0] == "--interop-directory")
    return NormalizeInteropDirectory(positionalArguments[1]);

if (positionalArguments.Count != 1)
{
    Console.Error.WriteLine(
        "Usage: LemonLoader.ManagedCompat [--runtime-major <version>] <managed-assembly.dll> | --interop-directory <directory>");
    return 2;
}

var assemblyPath = Path.GetFullPath(positionalArguments[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"The managed assembly was not found at '{assemblyPath}'.");
    return 2;
}

using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
{
    InMemory = true,
    ReadSymbols = false
});

if (!supportedAssemblies.TryGetValue(assembly.Name.Name, out var target) ||
    assembly.Name.Version?.ToString() != target.Version)
{
    Console.Error.WriteLine(
        $"Unsupported managed assembly identity '{assembly.Name.FullName}'.");
    return 3;
}

var module = assembly.MainModule;
var generatorType = module.GetType(target.TypeName)
    ?? throw new InvalidOperationException($"Type '{target.TypeName}' was not found.");
var staticConstructor = generatorType.Methods.SingleOrDefault(method => method.IsConstructor && method.IsStatic)
    ?? throw new InvalidOperationException($"Static constructor for '{target.TypeName}' was not found.");

var instructions = staticConstructor.Body.Instructions;
var typeGetFromHandle = typeof(Type).GetMethod(
    nameof(Type.GetTypeFromHandle),
    BindingFlags.Public | BindingFlags.Static,
    [typeof(RuntimeTypeHandle)])!;
var typeGetByName = typeof(Type).GetMethod(
    nameof(Type.GetType),
    BindingFlags.Public | BindingFlags.Static,
    [typeof(string)])!;
var importedGetByName = module.ImportReference(typeGetByName);

var replacements = 0;
if (runtimeMajor >= 10)
{
    for (var index = 0; index < instructions.Count - 1; index++)
    {
        var loadType = instructions[index];
        var getType = instructions[index + 1];
        if (loadType.OpCode != OpCodes.Ldtoken ||
            loadType.Operand is not TypeReference typeReference ||
            typeReference.FullName != typeof(System.Reflection.Emit.LocalBuilder).FullName ||
            getType.OpCode != OpCodes.Call ||
            getType.Operand is not MethodReference methodReference ||
            methodReference.FullName != module.ImportReference(typeGetFromHandle).FullName)
        {
            continue;
        }

        loadType.OpCode = OpCodes.Ldstr;
        loadType.Operand = runtimeLocalBuilderName;
        getType.Operand = importedGetByName;
        replacements++;
    }
}

var localBuilderAlreadyPatched = false;
if (runtimeMajor >= 10 && replacements == 0)
{
    var patchedReferences = instructions.Count(instruction =>
        instruction.OpCode == OpCodes.Ldstr &&
        string.Equals(instruction.Operand as string, runtimeLocalBuilderName, StringComparison.Ordinal));
    if (patchedReferences == 3)
    {
        localBuilderAlreadyPatched = true;
    }
}

if (runtimeMajor >= 10 && replacements != 3 && !localBuilderAlreadyPatched)
{
    Console.Error.WriteLine(
        $"Refusing to patch {assembly.Name.Name}: expected 3 LocalBuilder type loads, found {replacements}.");
    return 4;
}

var resolverGuardsAdded = 0;
var resolverGuardsAlreadyPatched = 0;
if (assembly.Name.Name == "0Harmony")
{
    var patchManager = module.GetType("HarmonyLib.Public.Patching.PatchManager")
        ?? throw new InvalidOperationException("Harmony PatchManager was not found.");
    var resolverEventArgs = patchManager.NestedTypes.Single(type => type.Name == "PatcherResolverEventArgs");
    var getMethodPatcher = resolverEventArgs.Methods.Single(method => method.Name == "get_MethodPatcher");

    foreach (var resolverTypeName in new[]
             {
                 "HarmonyLib.Public.Patching.ManagedMethodPatcher",
                 "HarmonyLib.Public.Patching.NativeDetourMethodPatcher"
             })
    {
        var resolverType = module.GetType(resolverTypeName)
            ?? throw new InvalidOperationException($"Harmony resolver '{resolverTypeName}' was not found.");
        var tryResolve = resolverType.Methods.Single(method => method.Name == "TryResolve");
        var body = tryResolve.Body;
        var first = body.Instructions.First();
        if (first.OpCode == OpCodes.Ldarg_1 &&
            first.Next?.OpCode == OpCodes.Callvirt &&
            first.Next.Operand is MethodReference existingGetter &&
            existingGetter.FullName == getMethodPatcher.FullName)
        {
            resolverGuardsAlreadyPatched++;
            continue;
        }

        var processor = body.GetILProcessor();
        processor.InsertBefore(first, processor.Create(OpCodes.Ldarg_1));
        processor.InsertBefore(first, processor.Create(OpCodes.Callvirt, getMethodPatcher));
        processor.InsertBefore(first, processor.Create(OpCodes.Brfalse_S, first));
        processor.InsertBefore(first, processor.Create(OpCodes.Ret));
        resolverGuardsAdded++;
    }
}

if (resolverGuardsAdded != 0 && resolverGuardsAdded != 2)
{
    Console.Error.WriteLine(
        $"Refusing to patch 0Harmony: expected 2 resolver guards, added {resolverGuardsAdded}.");
    return 4;
}

if (assembly.Name.Name == "0Harmony" &&
    resolverGuardsAdded == 0 &&
    resolverGuardsAlreadyPatched != 2)
{
    Console.Error.WriteLine(
        $"Refusing to patch 0Harmony: expected 2 existing resolver guards, found {resolverGuardsAlreadyPatched}.");
    return 4;
}

if (replacements == 0 && resolverGuardsAdded == 0)
{
    Console.WriteLine($"{assembly.Name.Name} is already patched: {assemblyPath}");
    return 0;
}

WriteAssembly(assembly, assemblyPath);

Console.WriteLine($"Patched {assembly.Name.Name} for Android .NET {runtimeMajor}: {assemblyPath}");
return 0;

static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
{
    yield return type;
    foreach (var nestedType in type.NestedTypes.SelectMany(EnumerateTypes))
        yield return nestedType;
}

static void WriteAssembly(AssemblyDefinition assembly, string assemblyPath)
{
    var temporaryPath = assemblyPath + ".patched";
    File.Delete(temporaryPath);
    assembly.Write(temporaryPath, new WriterParameters { WriteSymbols = false });
    assembly.Dispose();
    File.Move(temporaryPath, assemblyPath, true);
}

static int NormalizeInteropDirectory(string directoryPath)
{
    var fullDirectoryPath = Path.GetFullPath(directoryPath);
    if (!Directory.Exists(fullDirectoryPath))
    {
        Console.Error.WriteLine($"The interop assembly directory was not found at '{fullDirectoryPath}'.");
        return 2;
    }

    var assemblyPaths = Directory.GetFiles(fullDirectoryPath, "*.dll")
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
    if (assemblyPaths.Length == 0)
    {
        Console.Error.WriteLine($"No interop assemblies were found under '{fullDirectoryPath}'.");
        return 2;
    }

    var changedAssemblies = 0;
    var normalizedParameters = 0;
    foreach (var path in assemblyPaths)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(fullDirectoryPath);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = false,
            ReadingMode = ReadingMode.Immediate
        });

        var changes = 0;
        foreach (var type in assembly.MainModule.Types.SelectMany(EnumerateTypes))
        foreach (var method in type.Methods)
        foreach (var parameter in method.Parameters)
        {
            if (!parameter.HasDefault || parameter.HasConstant)
                continue;

            parameter.Attributes &= ~Mono.Cecil.ParameterAttributes.HasDefault;
            changes++;
        }

        if (changes == 0)
            continue;

        WriteAssembly(assembly, path);
        changedAssemblies++;
        normalizedParameters += changes;
    }

    Console.WriteLine(
        $"Normalized {normalizedParameters} optional parameter records in {changedAssemblies} of {assemblyPaths.Length} Android interop assemblies: {fullDirectoryPath}");
    return 0;
}
