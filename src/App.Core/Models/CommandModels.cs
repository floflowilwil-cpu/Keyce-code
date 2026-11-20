namespace App.Core.Models;

/// <summary>Commande exposée à l'interface (palette de commandes, menus...).</summary>
/// <param name="Id">Identifiant unique, par exemple "hello.sayHello".</param>
/// <param name="Title">Libellé affiché à l'utilisateur.</param>
/// <param name="OwnerId">Id de l'extension qui a enregistré la commande (null = application).</param>
public sealed record CommandInfo(string Id, string Title, string? OwnerId);
