using CcDirector.Rules.ScreenHarness;

// THE SCREEN HARNESS. Usage:
//   dotnet run --project src/CcDirector.Rules.ScreenHarness -- [--models wingman,wingman-fast] [--corpus <dir>] [--out <dir>] [--case <id>[,<id>...]] [--runs N]
//   dotnet run --project src/CcDirector.Rules.ScreenHarness -- --merge <parent dir of batch runs>
// It calls a live model and is run by hand; it is not part of the local gate.

var modelNames = HarnessRun.DefaultModels.ToList();
string? corpus = null;
string? output = null;
List<string>? onlyCases = null;
string? merge = null;
var runs = HarnessRun.DefaultRuns;
var firstRun = 1;

for (var i = 0; i < args.Length; i++)
{
    string Value(string flag)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("usage error: " + flag + " needs a value");
            Environment.Exit(2);
        }
        return args[++i];
    }

    switch (args[i])
    {
        case "--models":
            modelNames = Value("--models").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            break;
        case "--corpus":
            corpus = Path.GetFullPath(Value("--corpus"));
            break;
        case "--out":
            output = Path.GetFullPath(Value("--out"));
            break;
        case "--case":
            onlyCases = Value("--case").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            break;
        case "--merge":
            merge = Path.GetFullPath(Value("--merge"));
            break;
        case "--first-run":
            if (!int.TryParse(Value("--first-run"), out firstRun) || firstRun < 1)
            {
                Console.Error.WriteLine("usage error: --first-run needs a whole number of at least 1");
                return 2;
            }
            break;
        case "--runs":
            if (!int.TryParse(Value("--runs"), out runs) || runs < 1)
            {
                Console.Error.WriteLine("usage error: --runs needs a whole number of at least 1");
                return 2;
            }
            break;
        case "--help":
        case "-h":
            Console.WriteLine("usage: dotnet run --project src/CcDirector.Rules.ScreenHarness -- " +
                              "[--models wingman,wingman-fast] [--corpus <dir>] [--out <dir>] [--case <id>[,<id>...]] [--runs N] [--first-run K] | --merge <parent dir>");
            return 0;
        default:
            Console.Error.WriteLine("usage error: unknown argument '" + args[i] + "'. Try --help.");
            return 2;
    }
}

if (modelNames.Count == 0)
{
    Console.Error.WriteLine("usage error: --models names no model. The models are wingman and wingman-fast.");
    return 2;
}

var options = new HarnessOptions(
    ModelNames: modelNames,
    CorpusDirectory: corpus ?? RepositoryRoot.DefaultCorpus(),
    OutputDirectory: output ?? RepositoryRoot.DefaultOutput(),
    OnlyCases: onlyCases,
    Runs: runs,
    FirstRun: firstRun);

try
{
    return merge is not null
        ? await HarnessRun.MergeAsync(merge, Console.Out, CancellationToken.None)
        : await HarnessRun.RunAsync(options, Console.Out, CancellationToken.None);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("usage error: " + ex.Message);
    return 2;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException)
{
    Console.Error.WriteLine("the run could not start: " + ex.Message);
    return 3;
}
