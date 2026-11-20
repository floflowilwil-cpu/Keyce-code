using App.Languages;

using var client = new LanguageClient();
var done = new TaskCompletionSource();

client.DiagnosticsReceived += (uri, diagnostics) =>
{
    Console.WriteLine($"Diagnostics pour {uri} : {diagnostics.Length}");
    foreach (var d in diagnostics)
        Console.WriteLine($"  ligne {d.Range.Start.Line + 1} : {d.Message}");
    done.TrySetResult();
};

var root = new Uri(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar);
await client.StartAsync("python", "-m pylsp", root);

// Code Python volontairement faux : import inutilisé + variable non définie
var file = new Uri(Path.Combine(Directory.GetCurrentDirectory(), "test.py"));
await client.OpenDocumentAsync(file, "python", "import os\nprint(valeur_inconnue)\n");

var finished = await Task.WhenAny(done.Task, Task.Delay(15000));
Console.WriteLine(finished == done.Task ? "OK" : "Timeout : aucun diagnostic reçu");

await client.StopAsync();