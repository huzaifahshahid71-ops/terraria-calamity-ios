// IlDump <assembly> <Type> <method-substring> : prints IL of matching methods (read-only).
using Mono.Cecil;
if (args[0] == "publicize-monomod") { Publicize.Run(args[1], args[2]); return; }
if (args[0] == "access") { Access.Run(args[1], args[2]); return; }
var m = ModuleDefinition.ReadModule(args[0]);
var t = m.GetType(args[1]) ?? throw new Exception("type");
foreach (var meth in t.Methods.Where(x => x.Name == args[2] || (args.Length > 3 && x.Name.Contains(args[2]))))
{ Console.WriteLine($"== {meth.FullName} ({meth.Body?.Instructions.Count})"); if (meth.HasBody) foreach (var i in meth.Body.Instructions) Console.WriteLine(i); }
