using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The bootstrap depends only on the SDK. All shells use this same implementation.
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Environment.SetEnvironmentVariable("DOTNET_CLI_FORCE_UTF8_ENCODING", "true");
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root != null && !File.Exists(Path.Combine(root.FullName, "project.json"))) root = root.Parent;
    if (root == null) throw new FileNotFoundException("Cannot find project root.");
    return await new Bootstrap(root.FullName).Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.GetType().Name, message = error.Message }));
    return 1;
}

sealed class Bootstrap(string root)
{
    readonly string tool = Path.Combine(root, "src/Flipnote.Build/bin/Release/net10.0/Flipnote.Build.dll");
    readonly string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    const string Submodule = "external/libkwz";

    public async Task<int> Run(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "note";
        var tail = args.Skip(1).ToList();
        if (mode == "dependencies")
        {
            if (tail.Count != 0) throw new ArgumentException("Unknown dependency arguments.");
            await Restore();
            return 0;
        }
        if (mode == "tool")
        {
            bool force = tail.Remove("--force");
            if (tail.Count != 0) throw new ArgumentException("Unknown tool arguments.");
            await Build(force);
            return 0;
        }
        if (mode == "build")
        {
            string? input = Take(tail, "--input") ?? Take(tail, "-InputCia");
            string? output = Take(tail, "--output") ?? Take(tail, "-Output");
            if (input == null && tail.Count > 0 && !tail[0].StartsWith('-')) { input = tail[0]; tail.RemoveAt(0); }
            if (input == null || tail.Count != 0) throw new ArgumentException("Usage: build <original.cia> [--output artifacts/name.cia]");
            tail = ["build", "--input", Path.GetFullPath(input)];
            if (output != null) tail.AddRange(["--output", output]);
        }
        else if (mode != "note") throw new ArgumentException("Unknown bootstrap command: " + mode);
        await Build(false);
        if (tail.Count == 0) tail.Add("help");
        if (!tail.Contains("--project")) tail.AddRange(["--project", Path.Combine(root, "project.json")]);
        return await Process(dotnet, new[] { tool }.Concat(tail), Environment.CurrentDirectory);
    }

    static string? Take(List<string> args, string name)
    {
        int at = args.IndexOf(name);
        if (at < 0) return null;
        if (at + 1 >= args.Count) throw new ArgumentException("Missing value for " + name);
        string value = args[at + 1]; args.RemoveRange(at, 2); return value;
    }
    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    // The gitlink in the parent index is the only version pin. Never follow a branch tip.
    async Task<string> Restore()
    {
        if (!File.Exists(Path.Combine(root, ".gitmodules")) ||
            !(File.Exists(Path.Combine(root, ".git")) || Directory.Exists(Path.Combine(root, ".git"))))
            throw new InvalidDataException("Use a Git clone with submodules; source ZIPs do not contain the pinned libkwz gitlink.");
        string entry = await Git(root, "ls-files", "--stage", "--", Submodule);
        var match = Regex.Match(entry.Trim(), "^160000 ([a-f0-9]{40}) 0\\texternal/libkwz$");
        if (!match.Success) throw new InvalidDataException("Missing or conflicted libkwz gitlink. Resolve the parent repository index first.");
        string expected = match.Groups[1].Value;
        string destination = Path.Combine(root, Submodule);
        string metadata = Path.Combine(destination, ".git");
        if (!File.Exists(metadata) && !Directory.Exists(metadata))
        {
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidDataException("external/libkwz contains files but is not an initialized submodule; preserve those files before initializing it.");
            if (await Process("git", ["submodule", "update", "--init", "--recursive", "--checkout", "--", Submodule], root, stderrOnly: true) != 0)
                throw new IOException("Cannot initialize libkwz. Check network access, then run: git submodule update --init --recursive");
        }
        string actual = (await Git(destination, "rev-parse", "HEAD")).Trim();
        if (actual != expected)
            throw new InvalidDataException($"libkwz is at {actual}, expected {expected}. Preserve local work, then run: git submodule update --init --recursive");
        if (!string.IsNullOrWhiteSpace(await Git(destination, "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none")))
            throw new InvalidDataException("libkwz has local changes or untracked files. Commit or preserve them before building; the bootstrap will not discard them.");
        if (!File.Exists(Path.Combine(destination, "src/libkwz.csproj")))
            throw new InvalidDataException("The pinned libkwz commit has no library project.");
        return expected;
    }

    static async Task<string> Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Git is required to restore and verify libkwz.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new IOException("Git dependency check failed: " + error.Trim());
        return output;
    }

    async Task Build(bool force)
    {
        string dependency = await Restore(); // Check even when the compiled tool is cached.
        string stamp = Path.Combine(Path.GetDirectoryName(tool)!, "source-stamp.txt");
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Where(p => Path.GetExtension(p) is ".cs" or ".csproj" or ".json")
            .Concat(new[] { Path.Combine(root, "global.json"), Path.Combine(root, ".gitmodules") })
            .OrderBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal);
        string identity = dependency + "\n" + RuntimeInformation.RuntimeIdentifier + "\n" + Environment.Version + "\n" +
            string.Join('\n', sources.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + ":" + Hash(p)));
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (!force && File.Exists(tool) && File.Exists(stamp) && File.ReadAllText(stamp) == expected) return;
        string scratch = Scratch("build");
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(scratch, "dotnet"),
                ["NUGET_PACKAGES"] = Path.Combine(scratch, "packages"),
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(scratch, "http"),
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1"
            };
            // Clean first: MSBuild's up-to-date check must not reuse another host's native assets.
            var csproj = Path.Combine(root, "src/Flipnote.Build/Flipnote.Build.csproj");
            if (await Process(dotnet, ["build", csproj, "-c", "Release", "-t:Rebuild", "--disable-build-servers", "-v", "minimal"], root, environment, true) != 0)
                throw new IOException("Tool compilation failed; no CIA has been generated.");
            File.WriteAllText(stamp, expected, Encoding.ASCII);
        }
        finally { Cleanup(scratch); }
    }

    string Scratch(string label)
    {
        string path = Path.Combine(root, ".work", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    void Cleanup(string path)
    {
        string parent = Path.GetFullPath(Path.Combine(root, ".work")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(parent, StringComparison.Ordinal)) throw new IOException("Invalid scratch cleanup path.");
        Directory.Delete(path, true);
    }
    static async Task<int> Process(string executable, IEnumerable<string> args, string cwd, Dictionary<string, string>? environment = null, bool stderrOnly = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (environment != null) foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start " + executable);
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(stderrOnly ? Console.OpenStandardError() : Console.OpenStandardOutput());
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); return process.ExitCode;
    }
}
