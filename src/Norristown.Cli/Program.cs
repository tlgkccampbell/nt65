using Norristown;
using Norristown.Project;

const string Usage = "usage: nt65 build [--cpu 6502|65c02|65816] [-D NAME[=value]]... [<file.nt65>...]";

if (args is not ["build", .. var rest])
{
    Console.Error.WriteLine(Usage);
    return 2;
}

// `--cpu` is what the program is built for when no `.cpu` item and no project file says; one
// that does must agree with it (§5.1). `-D` adds a define or overrides one the project gives
// (§5.3). Named files replace the project's own globs, so one file can be built on its own.
var arguments = new List<Diagnostic>();
Cpu? cpu = null;
var defined = new List<Define>();
var paths = new List<string>();
for (var i = 0; i < rest.Length; i++)
{
    switch (rest[i])
    {
        case "--cpu" when i + 1 < rest.Length && CpuNames.Parse(rest[i + 1]) is { } named:
            cpu = named;
            i++;
            break;
        case "--cpu":
            Console.Error.WriteLine("nt65: --cpu takes 6502, 65c02 or 65816");
            return 2;
        case "-D" when i + 1 < rest.Length:
            if (ProjectFile.Definition(rest[++i], arguments) is { } define)
                defined.Add(define);
            break;
        case "-D":
            Console.Error.WriteLine("nt65: -D takes NAME or NAME=value");
            return 2;
        default:
            paths.Add(rest[i]);
            break;
    }
}

var project = File.Exists(ProjectFile.Name)
    ? ProjectFile.Read(ProjectFile.Name, File.ReadAllText(ProjectFile.Name))
    : ProjectSettings.None;
if (cpu is not null)
    project = project with { Cpu = cpu };
project = project.With(defined) with { Diagnostics = [.. project.Diagnostics, .. arguments] };

if (paths.Count == 0)
{
    paths.AddRange(project.Files.SelectMany(Matching).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    if (paths.Count == 0)
    {
        Console.Error.WriteLine(project.Files.Count == 0
            ? $"nt65: no input files, and no `files` in {ProjectFile.Name}"
            : $"nt65: no file matched the `files` globs in {ProjectFile.Name}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
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
    files.Add(new SourceFile(Logical(path), File.ReadAllText(path)));
}
if (failed)
    return 1;

var compilation = Compiler.Compile(files, project);

foreach (var d in compilation.Diagnostics)
{
    var severity = d.Severity.ToString().ToLowerInvariant();
    Console.Error.WriteLine($"{d.Span.File}:{d.Span.Line}:{d.Span.StartColumn}: {severity}: {d.Message}");
}

var wrong = compilation.Diagnostics.Any(d => d.Severity == Severity.Error);
if (!wrong)
{
    foreach (var output in compilation.Outputs)
    {
        // Rewrite only when the content changes, so build tools see unchanged files as unchanged.
        if (File.Exists(output.Path) && File.ReadAllText(output.Path) == output.Text)
            continue;
        if (Path.GetDirectoryName(output.Path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(output.Path, output.Text);
    }
}

return wrong ? 1 : 0;

// The path a file is known by: relative to where nt65 was run, with `/` separators whatever
// the platform, so that diagnostics and output read the same everywhere.
static string Logical(string path) =>
    Path.GetRelativePath(Environment.CurrentDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

// The files one `files` glob names (§5.3). `**` matches any number of directories and has to
// be a whole segment; anything else is a plain pattern for the directory it sits in.
static IEnumerable<string> Matching(string glob)
{
    var normalized = glob.Replace('\\', '/');
    var at = normalized.IndexOf("**/", StringComparison.Ordinal);
    var (root, pattern, search) = at >= 0
        ? (normalized[..at], normalized[(at + 3)..], SearchOption.AllDirectories)
        : (Directory(normalized), Name(normalized), SearchOption.TopDirectoryOnly);

    var from = root.Length == 0 ? "." : root;
    if (!System.IO.Directory.Exists(from) || pattern.Contains('/'))
        return [];
    return System.IO.Directory.EnumerateFiles(from, pattern, search).Select(Logical);

    static string Directory(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";
    static string Name(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[(i + 1)..] : path;
}
