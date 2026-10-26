using Flipnote.Build;
using Flipnote.Build.Formats;
using System.Text.Json;

try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    var arguments=args.ToList();string command=arguments.Count>0?arguments[0]:"help";if(arguments.Count>0)arguments.RemoveAt(0);
    string? Option(string name)
    {
        int at=arguments.IndexOf(name);if(at<0)return null;if(at+1>=arguments.Count)throw new ArgumentException("Missing value for "+name);
        string value=arguments[at+1];arguments.RemoveRange(at,2);return value;
    }
    bool Flag(string name)=>arguments.Remove(name);
    string projectPath=Option("--project")??Path.Combine(Environment.CurrentDirectory,"project.json");
    string? input=Option("--input"),output=Option("--output"),filter=Option("--filter");bool raw=Flag("--raw");
    void Print(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,Project.Json));
    if(command is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
        Flipnote-zhCN CLI (.NET 10)
          inventory [--input original.cia] [--filter text] [--output report.json]
          read <virtual-resource-path> --output file [--raw] [--input original.cia]
          texture <virtual-bclim-path> --output image.png [--input original.cia]
          messages <virtual-msbt-path> [--output messages.json] [--input original.cia]
          animation-survey [--output directory] [--input original.cia]
          animation-frame <virtual-path> <zero-based-frame> --output prefix
          animation-check [--output directory]
          font-extend --input font.ttf  Append missing translated glyphs to font sources
          font-unify --input font.ttf   Unify all Han and English glyphs; preserve kana/symbols
          translation-check [--output report.json]
          plan [--input original.cia]   Validate sources and compile every overlay in memory
          build --input original.cia [--output artifacts/name.cia]
          verify --input original.cia [--output artifacts/name.cia]
          selftest                     Exercise format and build invariants
          platform-check               Test native graphics, font, KWZ and I/O without a CIA
        All commands accept --project path/to/project.json. Nested archives use !/.
        Default build reads only the pinned original CIA and the declared overlay sources.
        """);return 0;
    }
    if(command=="platform-check")
    {
        if(arguments.Count!=0 || input!=null || output!=null || raw || filter!=null)throw new ArgumentException("platform-check takes only --project.");
        PlatformCheck.Run(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);return 0;
    }
    var project=File.Exists(projectPath)?new Project(projectPath,input):null;
    if(command is "font-extend" or "font-unify")
    {
        if(project==null || input==null || arguments.Count!=0 || output!=null || raw || filter!=null)throw new ArgumentException("font-extend requires --input font.ttf and a project.");
        Print(command=="font-unify"?FontUnification.Run(project,input):FontCoverage.Extend(project,input));return 0;
    }
    if(command=="translation-check")
    {
        if(project==null || arguments.Count!=0 || raw || filter!=null)throw new ArgumentException("translation-check requires a project.");
        var original=project.OpenInput();var report=TranslationAudit.Check(project,original);
        string destination=output??Path.Combine(project.Root,"artifacts","translation-review.json");ProtectInput(destination,original.SourcePath,project);Project.SaveJson(destination,report);
        Print(new { verified=true,output=destination });return 0;
    }
    if(command=="animation-check")
    {
        if(project==null || arguments.Count!=0 || raw || filter!=null)throw new ArgumentException("animation-check requires a project.");
        var original=project.OpenInput();string destination=output??Path.Combine(project.Root,"artifacts","animation-review");ProtectInput(destination,original.SourcePath,project);
        AnimationLocalization.Check(original,Path.Combine(project.Root,"overlays","zh-CN","animations"),destination);return 0;
    }
    if(command is "plan" or "build" or "verify" or "selftest")
    {
        if(project==null)throw new FileNotFoundException("Project configuration is required.",projectPath);
        if(arguments.Count!=0 || filter!=null || raw || (output!=null && command is not ("build" or "plan" or "verify")))throw new ArgumentException("Unsupported arguments for "+command);
        var image=project.OpenInput();var changes=project.Apply(image);
        if(command=="plan")
        {
            var plan=new { command,inputSha256=image.Sha256,titleId=image.Document.Tmd.TitleId.ToString("X16"),resourceCount=image.Resources.Count,operations=changes,output=project.OutputPath() };
            if(output!=null){ProtectInput(output,image.SourcePath,project);Project.SaveJson(output,plan);}Print(plan);return 0;
        }
        if(command=="verify")
        {
            string path=project.OutputPath(output);var actual=new CiaImage(path);project.Verify(image,actual);Print(new { verified=true,resources=actual.Resources.Count,sha256=actual.Sha256 });return 0;
        }
        if(command=="selftest") { SelfTest.Run(project,image,changes);return 0; }
        byte[] built=image.Build();var reread=new CiaImage(built);project.Verify(image,reread);
        string destination=project.OutputPath(output);Project.Save(destination,built);
        var report=new { schemaVersion=1,input=Path.GetFileName(image.SourcePath),inputSha256=image.Sha256,titleId=image.Document.Tmd.TitleId.ToString("X16"),output=Path.GetRelativePath(project.Root,destination).Replace('\\','/'),outputSha256=Project.Hash(built),outputBytes=built.Length,resourceCount=reread.Resources.Count,verified=true,operations=changes };
        Project.SaveJson(Path.ChangeExtension(destination,"build.json"),report);Print(report);return 0;
    }
    if(command is not ("inventory" or "read" or "texture" or "messages" or "animation-survey" or "animation-frame"))throw new ArgumentException("Unknown command: "+command);
    var source=input!=null?new CiaImage(input):project?.OpenInput()??throw new ArgumentException("Pass --input or --project.");
    if(command=="animation-frame")
    {
        if(arguments.Count!=2 || output==null || raw || filter!=null || !int.TryParse(arguments[1],out int frame))throw new ArgumentException("animation-frame requires virtual path, frame number and --output prefix.");
        ProtectInput(output,source.SourcePath,project);
        AnimationLocalization.Frame(source,arguments[0],frame,output);return 0;
    }
    if(command=="animation-survey")
    {
        if(arguments.Count!=0 || raw || filter!=null)throw new ArgumentException("Unsupported survey arguments.");
        string destination=output??Path.Combine(project?.Root??Environment.CurrentDirectory,".work","animation-survey");ProtectInput(destination,source.SourcePath,project);
        AnimationLocalization.Survey(source,destination);return 0;
    }
    if(command=="inventory")
    {
        if(arguments.Count!=0 || raw)throw new ArgumentException("Unsupported inventory arguments.");
        var report=new { input=source.SourcePath,sha256=source.Sha256,titleId=source.Document.Tmd.TitleId.ToString("X16"),resourceCount=source.Resources.Count,resources=source.Resources.Values.Where(n=>filter==null || n.Path.Contains(filter,StringComparison.OrdinalIgnoreCase)).Select(n=>n.Describe()) };
        if(output!=null){ProtectInput(output,source.SourcePath,project);Project.SaveJson(output,report);}Print(report);return 0;
    }
    if(arguments.Count!=1 || filter!=null || !source.Resources.TryGetValue(arguments[0],out var resource))throw new ArgumentException("Specify one exact virtual resource path from inventory.");
    if(command=="texture")
    {
        if(output==null || raw || !output.EndsWith(".png",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("texture requires --output image.png.");
        ProtectInput(output,source.SourcePath,project);var texture=new Bclim(resource.Data);Project.Save(output,texture.ToPng());
        Print(new { output,width=texture.Width,height=texture.Height,format=texture.FormatName,sha256=Project.Hash(resource.Data) });
    }
    else if(command=="read")
    {
        if(output==null)throw new ArgumentException("read requires --output.");
        byte[] data=raw?resource.Original:resource.Data;ProtectInput(output,source.SourcePath,project);Project.Save(output,data);Print(new { output,size=data.Length,sha256=Project.Hash(data) });
    }
    else
    {
        if(raw)throw new ArgumentException("messages does not accept --raw.");
        var messages=new Msbt(resource.Data).Texts().Select((text,id)=>new { id,message=text }).ToArray();
        if(output!=null){ProtectInput(output,source.SourcePath,project);Project.SaveJson(output,messages);}Print(messages);
    }
    return 0;
}
catch(Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error=error.GetType().Name,message=error.Message },Project.Json));return 1;
}

static void ProtectInput(string output,string input,Project? project)
{
    string target=Path.GetFullPath(output);
    bool Inside(string directory)=>target.Equals(directory,FileSystemPaths.Comparison) || target.StartsWith(directory+Path.DirectorySeparatorChar,FileSystemPaths.Comparison);
    if(target.Equals(input,FileSystemPaths.Comparison))throw new InvalidDataException("Output would overwrite the input CIA.");
    if(project==null)return;
    var roots=project.Manifest.SourceRoots.Values.Select(p=>Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.ManifestPath)!,p))).Append(Path.Combine(project.Root,"rom"));
    if(target.Equals(Path.Combine(project.Root,"project.json"),FileSystemPaths.Comparison) || target.Equals(project.ManifestPath,FileSystemPaths.Comparison) || roots.Any(Inside))throw new InvalidDataException("Output would overwrite a protected source directory.");
}
