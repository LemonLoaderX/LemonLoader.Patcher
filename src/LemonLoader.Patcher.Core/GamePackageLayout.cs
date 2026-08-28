internal static class GamePackageLayout
{
    public const string Arm64LibraryRoot = "lib/arm64-v8a";
    public const string MainLibrary = $"{Arm64LibraryRoot}/libmain.so";
    public const string UnityLibrary = $"{Arm64LibraryRoot}/libunity.so";
    public const string Il2CppLibrary = $"{Arm64LibraryRoot}/libil2cpp.so";
    public const string Metadata = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    public const string GlobalGameManagers = "assets/bin/Data/globalgamemanagers";

    public static string FilePath(string root, string entryName) =>
        Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar));
}
