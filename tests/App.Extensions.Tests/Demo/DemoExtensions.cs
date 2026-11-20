using App.Core.Contracts;

namespace App.Extensions.Tests.Demo;

/// <summary>
/// Extension de démonstration : ajoute la commande "hello.sayHello"
/// qui écrit hello.txt dans son dossier d'installation.
/// Les tests copient cette .dll dans un dossier d'extension, comme le ferait un vrai auteur.
/// </summary>
public sealed class HelloWorldExtension : IExtension
{
    public const string CommandId = "hello.sayHello";

    public Task ActivateAsync(IExtensionContext context, CancellationToken ct = default)
    {
        context.RegisterCommand(
            CommandId,
            "Hello World",
            token => File.WriteAllTextAsync(
                Path.Combine(context.InstallPath, "hello.txt"),
                $"Bonjour depuis {context.Manifest.Name} !",
                token));

        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Extension volontairement cassée : vérifie que l'hôte reste stable.</summary>
public sealed class CrashingExtension : IExtension
{
    public Task ActivateAsync(IExtensionContext context, CancellationToken ct = default)
    {
        context.RegisterCommand("crash.before", "Avant le crash", _ => Task.CompletedTask);
        throw new InvalidOperationException("Boom");
    }

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
