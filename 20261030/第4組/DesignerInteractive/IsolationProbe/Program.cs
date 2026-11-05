using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using EVCanopyDesigner.Interactive.Revit.Bootstrap;
internal static class Program
{
 [STAThread] static int Main(string[] args)
 {
  if(args.Length!=2)throw new ArgumentException("Probe bin directory and NEW evidence directory required.");
  string directory=Path.GetFullPath(args[0]);
  // Simulate production Core already loaded before candidate startup.
  AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory,"EVCanopyDesigner.Canopy.Core.dll"));
  var hashes=Directory.GetFiles(directory,"*.dll").ToDictionary(p=>Path.GetFileName(p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
  var context=new CandidateLoadContext(directory,hashes);var entry=context.LoadVerified("DesignerInteractive.ScopedUIProbe.dll");
  try{return (int)entry.GetType("DesignerInteractive.ScopedUIProbe.Probe",true)!.GetMethod("Run")!.Invoke(null,new object[]{Path.GetFullPath(args[1])})!;}catch(TargetInvocationException e){Console.Error.WriteLine(e.InnerException);return 1;}
 }
}
