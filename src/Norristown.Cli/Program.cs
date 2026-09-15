using Norristown;

const string Usage = "usage: nt65 build <file.nt65>...";

if (args is not ["build", .. var paths])
{
    Console.Error.WriteLine(Usage);
    return 2;
}
if (paths.Length == 0)
{
    Console.Error.WriteLine("nt65: no input files");
    Console.Error.WriteLine(Usage);
    return 2;
}

var files = new List<SourceFile>();
var failed = false;
foreach (var path in paths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"{path}: error: file not found");
        failed = true;
        continue;
    }
    var logical = Path.GetRelativePath(Environment.CurrentDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
    files.Add(new SourceFile(logical, File.ReadAllText(path)));
}
if (failed)
    return 1;

var compilation = Compiler.Compile(files);

foreach (var d in compilation.Diagnostics)
{
    var severity = d.Severity.ToString().ToLowerInvariant();
    Console.Error.WriteLine($"{d.Span.File}:{d.Span.Line}:{d.Span.StartColumn}: {severity}: {d.Message}");
}

foreach (var output in compilation.Outputs)
{
    // Rewrite only when the content changes, so build tools see unchanged files as unchanged.
    if (File.Exists(output.Path) && File.ReadAllText(output.Path) == output.Text)
        continue;
    File.WriteAllText(output.Path, output.Text);
}

return compilation.Diagnostics.Any(d => d.Severity == Severity.Error) ? 1 : 0;
