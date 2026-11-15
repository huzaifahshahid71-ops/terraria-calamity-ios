// Exports Texture2D XNB (Color format) to raw RGBA files using FNA's internal LZX decoder. Build-time helper; originals are only read.
using System; using System.IO; using System.Reflection;
static class P {
    static int Read7(BinaryReader r){int v=0,s=0;byte b;do{b=r.ReadByte();v|=(b&0x7f)<<s;s+=7;}while((b&0x80)!=0);return v;}
    static void Main(string[] a){
        var fna=typeof(Microsoft.Xna.Framework.Vector2).Assembly;
        if(a[0]=="types"){foreach(var t in fna.GetTypes())if(t.FullName.Contains("Lz")||t.FullName.Contains("ompress"))Console.WriteLine(t.FullName);return;}
        for(int i=1;i+1<a.Length;i+=2){ string src=a[i], dst=a[i+1];
            using var f=File.OpenRead(src); var br=new BinaryReader(f);
            br.ReadBytes(3); br.ReadByte(); br.ReadByte(); byte flags=br.ReadByte(); int total=br.ReadInt32();
            Stream body=f;
            if((flags&0x80)!=0){int dec=br.ReadInt32(); var dt=fna.GetType("Microsoft.Xna.Framework.Content.LzxDecoder",true);
                object d=Activator.CreateInstance(dt,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{16},null);
                var m=dt.GetMethod("Decompress",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                var ms=new MemoryStream(dec); long start=f.Position, pos=start, csize=total-14;
                while(pos-start<csize){int hi=f.ReadByte(),lo=f.ReadByte(),block=(hi<<8)|lo,frame=0x8000;
                    if(hi==0xFF){hi=lo;lo=f.ReadByte();frame=(hi<<8)|lo;hi=f.ReadByte();lo=f.ReadByte();block=(hi<<8)|lo;pos+=5;}else pos+=2;
                    if(block==0||frame==0)break; m.Invoke(d,new object[]{f,block,ms,frame}); pos+=block; f.Position=pos;}
                ms.Position=0; body=ms;}
            var r=new BinaryReader(body);
            int nr=Read7(r); for(int k=0;k<nr;k++){r.ReadString();r.ReadInt32();}
            Read7(r); Read7(r);
            int fmt=r.ReadInt32(),w=r.ReadInt32(),h=r.ReadInt32(),levels=r.ReadInt32(),size=r.ReadInt32();
            if(fmt!=0) throw new Exception(src+": surface format "+fmt);
            var px=r.ReadBytes(size);
            using var o=File.Create(dst); var bw=new BinaryWriter(o); bw.Write(w);bw.Write(h);bw.Write(px);
            Console.WriteLine($"{Path.GetFileName(src)} {w}x{h}");
        }
    }
}
