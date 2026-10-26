using System.Buffers.Binary;
using System.Text;

namespace Flipnote.Build;

internal static class SettingsAboutSelfTest
{
    private const string Root="content/0000/romfs/LayoutArchive.arc!/Config.blz!/";
    private static readonly Dictionary<string,string> Clones=new()
    {
        ["AllClear"]="About", ["TexAllClearWin"]="TexAboutWin", ["TxtAllClear"]="TxtAbout",
        ["TexShaL_12"]="TexAboutShaL", ["TexShaR_13"]="TexAboutShaR",
        ["TexAllClearWinC "]="TexAboutWinC ", ["TexAllClearWinLT"]="TexAboutWinLT"
    };
    private static int U16(byte[] b,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static int U32(byte[] b,int at)=>checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)));
    private static string Name(byte[] b,int at,int n=20)=>Encoding.ASCII.GetString(b,at,n).Split('\0')[0];
    private static List<byte[]> Sections(byte[] b)
    {
        if(b.Length<20 || U32(b,12)!=b.Length)throw new InvalidDataException("Layout length mismatch.");
        var result=new List<byte[]>();int at=20;
        while(at<b.Length)
        {
            int size=U32(b,at+4);
            if(size<8 || size>b.Length-at)throw new InvalidDataException("Layout section bounds.");
            result.Add(b.AsSpan(at,size).ToArray());at+=size;
        }
        if(result.Count!=U16(b,16))throw new InvalidDataException("Layout section count.");
        return result;
    }
    private static byte[][] Table(byte[] b,int countOffset,int tableOffset)
    {
        int n=U16(b,countOffset);var rows=new byte[n][];
        for(int i=0;i<n;i++)
        {
            int at=U32(b,tableOffset+i*4),end=i+1<n?U32(b,tableOffset+(i+1)*4):b.Length;
            if(at<tableOffset+n*4 || end<at || end>b.Length)throw new InvalidDataException("Layout table bounds.");
            rows[i]=b.AsSpan(at,end-at).ToArray();
        }
        return rows;
    }
    public static void Run(Project project,CiaImage original,CiaImage result,Action<bool,string> check,Action<Action,string> reject)
    {
        var old=Sections(original.Resources[Root+"blyt/Config.bclyt"].Data);
        var now=Sections(result.Resources[Root+"blyt/Config.bclyt"].Data);
        string[] types=["pan1","bnd1","pic1","wnd1","txt1"];
        var oldPanes=old.Where(b=>types.Contains(Name(b,0,4))).ToDictionary(b=>Name(b,12));
        var newPanes=now.Where(b=>types.Contains(Name(b,0,4))).ToDictionary(b=>Name(b,12));
        foreach(var (name,bytes) in oldPanes)
        {
            var expected=(byte[])bytes.Clone();
            if(name=="AllClear")BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(40),-368);
            if(!newPanes[name].AsSpan().SequenceEqual(expected))throw new InvalidDataException("Existing setting pane changed: "+name);
        }
        check(newPanes.Count==oldPanes.Count+5 && Clones.Where(p=>oldPanes.ContainsKey(p.Key)).All(p=>newPanes.ContainsKey(p.Value)),
            "Settings adds exactly one independent row and preserves all original controls and text panes");
        var mats=Table(now.Single(b=>Name(b,0,4)=="mat1"),8,12);
        var oldMats=Table(old.Single(b=>Name(b,0,4)=="mat1"),8,12);
        check(mats.Length==oldMats.Length+5 && oldMats.Select((b,i)=>mats[i].AsSpan().SequenceEqual(b)).All(x=>x),
            "Settings clones button materials without changing existing styles");
        int depth=0;
        foreach(var b in now)
        {
            string type=Name(b,0,4);
            if(type=="pas1")depth++;
            if(type=="pae1" && --depth<0)throw new InvalidDataException("Unbalanced pane tree.");
            if(type=="pic1" && U16(b,92)>=mats.Length || type=="txt1" && U16(b,80)>=mats.Length)
                throw new InvalidDataException("Bad pane material.");
            if(type=="wnd1")
            {
                if(U16(b,U32(b,96)+16)>=mats.Length)throw new InvalidDataException("Bad window content material.");
                for(int i=0;i<b[92];i++)if(U16(b,U32(b,U32(b,100)+i*4))>=mats.Length)throw new InvalidDataException("Bad window frame material.");
            }
        }
        var group=now.Single(b=>Name(b,0,4)=="grp1" && Name(b,8,16)=="About");
        check(depth==0 && U16(group,24)==1 && Name(group,28,16)=="About", "Settings pane hierarchy and independent animation group are valid");
        foreach(string state in new[]{"Decide","Disable","KeyPush","Push","Scroll","Wait"})
        {
            string path=Root+"anim/Config_"+state+".bclan";
            var a=Sections(original.Resources[path].Data);var z=Sections(result.Resources[path].Data);
            var rows=Table(a[1],14,20);var newRows=Table(z[1],14,20);
            if(newRows.Length!=rows.Length+rows.Count(b=>Clones.ContainsKey(Name(b,0))))throw new InvalidDataException("Missing Settings animation track.");
            for(int i=0;i<rows.Length;i++)
            {
                var expected=(byte[])rows[i].Clone();
                if(state=="Scroll")
                {
                    string name=Name(expected,0);
                    if(name=="MoveScroll")
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(64),350);
                        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(68),350);
                    }
                    if(name=="TexScrollBar")
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(64),350);
                        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(60),-134f/350);
                        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(72),-134f/350);
                    }
                }
                if(!expected.AsSpan().SequenceEqual(newRows[i]))throw new InvalidDataException("Original animation track changed: "+state);
            }
            foreach(var row in newRows.Skip(rows.Length))
            {
                string source=Clones.Single(p=>p.Value==Name(row,0)).Key;
                if(!rows.Any(b=>Name(b,0)==source && b.AsSpan(20).SequenceEqual(row.AsSpan(20))))throw new InvalidDataException("Cloned animation differs.");
            }
        }
        check(true,"All six Settings animations preserve original tracks and clone only the new row");
        byte[] ex=result.Resources["content/0000/exheader"].Data,oldEx=original.Resources["content/0000/exheader"].Data;
        check((U32(ex,24)+4095)/4096==(U32(oldEx,24)+4095)/4096 && U32(ex,60)==U32(oldEx,60)+24,
            "Settings hook uses existing text pages and only four new state bytes beyond the font loader");
        byte[] input="abcde"u8.ToArray(),wanted="cdeab!"u8.ToArray();
        var recipe=new ResourcePatch.Recipe(Project.Hash(input),Project.Hash(wanted),wanted.Length,
            [new(2,3),new(0,2),new(Data:"21")]);
        check(ResourcePatch.Apply(input,recipe).AsSpan().SequenceEqual(wanted),"Resource recipes can copy, reorder and append without shipping whole original assets");
        reject(()=>ResourcePatch.Apply(input,recipe with {Segments=[new(-1,3)]}),"Resource recipes reject negative copy offsets");
        reject(()=>ResourcePatch.Apply(input,recipe with {Segments=[new(4,3)]}),"Resource recipes reject out-of-bounds copies");
        reject(()=>ResourcePatch.Apply(input,recipe with {Segments=[new(0,1,"21")]}),"Resource recipes reject ambiguous copy/literal segments");
        reject(()=>ResourcePatch.Apply(input,recipe with {ResultLength=2}),"Resource recipes enforce output allocation bounds");
        reject(()=>ResourcePatch.Apply(input,recipe with {ResultSha256=new string('0',64)}),"Resource recipes reject output hash mismatches");
        SettingsAbout.Validate(project,result);
        check(true,"Settings native code agrees with editable text and checked source hashes");
    }
}
