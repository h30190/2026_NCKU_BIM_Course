using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
namespace EVCanopyDesigner.Interactive.Revit.Bootstrap;
// The installed production Core/UI assemblies never resolve candidate dependencies.
internal sealed class CandidateLoadContext : AssemblyLoadContext
{
 readonly string directory;readonly IReadOnlyDictionary<string,string> hashes;
 internal CandidateLoadContext(string directory,IReadOnlyDictionary<string,string> hashes):base("EVHub-Interactive-"+Guid.NewGuid().ToString("N"),false){this.directory=directory;this.hashes=hashes;}
 protected override Assembly? Load(AssemblyName name)
 {
  if(name.Name is "RevitAPI" or "RevitAPIUI")return Default.Assemblies.Single(a=>a.GetName().Name==name.Name);
  if(name.Name is not null&&(name.Name.StartsWith("EVCanopyDesigner",StringComparison.Ordinal)||name.Name.StartsWith("DesignerInteractive",StringComparison.Ordinal)))return LoadVerified(name.Name+".dll");
  return null; // System/WPF remain shared with the actual Revit runtime.
 }
 internal Assembly LoadVerified(string file)
 {
  if(!hashes.TryGetValue(file,out string? expected))throw new InvalidOperationException("Unlisted candidate dependency: "+file);
  string path=Path.Combine(directory,file);if(!File.Exists(path)||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=expected)throw new InvalidOperationException("Candidate package changed: "+file);
  return LoadFromAssemblyPath(path);
 }
}
