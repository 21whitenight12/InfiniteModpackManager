namespace Imm.Core.IO;

/// <summary>
/// Storage locations per IMM.md Data Contracts / Infrastructure Rules:
/// portable mode wins when library.json sits next to the exe, otherwise %LOCALAPPDATA%\IMM.
/// Explicit arguments keep this testable without touching the real machine state.
/// </summary>
public sealed record ImmPaths(string LibraryFilePath, string LogsDirectory, bool Portable, string DocumentsDirectory)
{
    public const string LibraryFileName = "library.json";

    public static ImmPaths Resolve(
        string? exeBaseDirectory = null,
        string? localAppDataDirectory = null,
        string? documentsDirectory = null)
    {
        var baseDirectory = exeBaseDirectory ?? AppContext.BaseDirectory;
        var appData = localAppDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // MyDocuments already honours the OneDrive redirect (IMM.md «Бэкапы»).
        var documents = documentsDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var portableLibrary = Path.Combine(baseDirectory, LibraryFileName);
        if (File.Exists(portableLibrary))
        {
            return new ImmPaths(portableLibrary, Path.Combine(baseDirectory, "logs"), Portable: true, documents);
        }

        var immRoot = Path.Combine(appData, "IMM");
        return new ImmPaths(
            Path.Combine(immRoot, LibraryFileName),
            Path.Combine(immRoot, "logs"),
            Portable: false,
            documents);
    }
}
