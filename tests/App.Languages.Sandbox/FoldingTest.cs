using App.Languages;

static class FoldingTest
{
    public static Task RunAsync()
    {
        const string code =
            "class Chat:\n" +
            "    def miauler(self):\n" +
            "        print('miaou')\n" +
            "        print('miaou')\n" +
            "\n" +
            "    def dormir(self):\n" +
            "        pass\n" +
            "\n" +
            "x = 1\n";

        foreach (var r in IndentationFolding.Compute(code))
            Console.WriteLine($"plier lignes {r.StartLine + 1} à {r.EndLine + 1}");

        return Task.CompletedTask;
    }
}