switch (args.FirstOrDefault())
{
    case "csharp": await CSharpTest.RunAsync(); break;
    case "shared": await SharedServerTest.RunAsync(); break;
    case "js": await TypeScriptTest.RunJavaScriptAsync(); break;
    case "ts": await TypeScriptTest.RunTypeScriptAsync(); break;
    case "html": await HtmlCssTest.RunHtmlAsync(); break;
    case "css": await HtmlCssTest.RunCssAsync(); break;
    case "json": await JsonTest.RunAsync(); break;
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