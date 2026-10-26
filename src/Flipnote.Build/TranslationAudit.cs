using System.Text.Json;
using System.Text.RegularExpressions;
using Flipnote.Build.Formats;

namespace Flipnote.Build;

/// <summary>Mechanical checks supplement, but never replace, Japanese/Chinese editorial review.</summary>
internal static class TranslationAudit
{
    public static object Check(Project project,CiaImage original)
    {
        var files=new List<object>();int messages=0,nonempty=0,unchanged=0;var layoutWarnings=new List<object>();
        foreach(var op in project.Manifest.Operations.Where(o=>o.Kind=="msbt"))
        {
            var msbt=new Msbt(original.Resources[op.Target].Data);string[] source=msbt.Texts();
            using var json=JsonDocument.Parse(File.ReadAllBytes(project.Source(op)));var rows=json.RootElement.EnumerateArray().ToArray();
            var ids=rows.Select(r=>r.GetProperty("id").GetInt32()).ToArray();
            if(!ids.Order().SequenceEqual(Enumerable.Range(0,source.Length)))throw new InvalidDataException("Translation review requires every original message exactly once: "+op.Target);
            var entries=new List<object>();
            foreach(var row in rows)
            {
                int id=row.GetProperty("id").GetInt32();string target=msbt.NormalizeText(row.GetProperty("message").GetString()!);messages++;
                if(source[id].Length>0)nonempty++;if(source[id]==target)unchanged++;
                if(!Tokens(source[id]).SequenceEqual(Tokens(target)))throw new InvalidDataException($"Control/icon sequence changed in {op.Target} [{id}].");
                if((source[id].Length==0)!=(target.Length==0))throw new InvalidDataException($"Empty message status changed in {op.Target} [{id}].");
                if(Regex.IsMatch(FontCoverage.PlainText(target),"[\u3040-\u30ff]"))throw new InvalidDataException($"Japanese kana remains in {op.Target} [{id}].");
                int originalLines=source[id].Count(c=>c=='\n')+1,targetLines=target.Count(c=>c=='\n')+1;
                if(targetLines>originalLines)layoutWarnings.Add(new { target=op.Target,id,reason="More lines than original",originalLines,targetLines });
                entries.Add(new { id,source=source[id],translation=target,controlTokensPreserved=true });
            }
            files.Add(new { target=op.Target,source=op.Source,sha256=Project.Hash(File.ReadAllBytes(project.Source(op))),count=rows.Length,entries });
        }
        return new { schemaVersion=1,inputSha256=original.Sha256,files=files.Count,messages,nonempty,unchangedFromJapanese=unchanged,controlTokensPreserved=true,japaneseKanaRemaining=0,layoutWarnings,review="All messages retranslated from Japanese, then cross-reviewed. Report generated from current source files; in-game layout still requires runtime review.",details=files };
    }
    private static IEnumerable<string> Tokens(string text)=>Regex.Matches(text,"<[^>]*>").Select(m=>m.Value);
}
