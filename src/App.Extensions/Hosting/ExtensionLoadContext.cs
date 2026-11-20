using System.Reflection;
using System.Runtime.Loader;

namespace App.Extensions.Hosting;

/// <summary>
/// Contexte de chargement propre à une extension : son code est isolé
/// et peut être déchargé quand elle est désactivée.
/// </summary>
internal sealed class ExtensionLoadContext(string extensionFolder, string extensionId)
    : AssemblyLoadContext("extension:" + extensionId, isCollectible: true)
{
    public Assembly LoadMain(string assemblyPath) => LoadWithoutLock(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // App.Core (l'API) et les bibliothèques de l'application sont partagées :
        // sinon IExtension de l'extension serait un type différent de celui de l'hôte.
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            // Pas dans l'application : on cherche dans le dossier de l'extension.
        }

        string candidate = Path.Combine(extensionFolder, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadWithoutLock(candidate) : null;
    }

    // Chargement depuis la mémoire : le fichier n'est pas verrouillé
    // et le dossier peut être supprimé à la désinstallation.
    private Assembly LoadWithoutLock(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }
}
