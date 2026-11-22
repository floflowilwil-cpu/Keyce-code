switch (args.FirstOrDefault())
{
    case "colors": await ColorTest.RunAsync(); break;
    case "highlight": await HighlightTest.RunAsync(); break;
    case "lsp-extras": await LspExtrasTest.RunAsync(); break;
    case "sync": await SyncTest.RunAsync(); break;
    case "folding": await FoldingTest.RunAsync(); break;
    case "snippets": await SnippetTest.RunAsync(); break;
    default:
        Console.WriteLine("Usage : dotnet run --project tests/App.Languages.Sandbox -- highlight | lsp-extras | sync | folding | snippets");
        break;
}