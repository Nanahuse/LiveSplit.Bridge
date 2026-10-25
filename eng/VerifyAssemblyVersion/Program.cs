using System.Diagnostics;
using System.Reflection;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: VerifyAssemblyVersion <DLL> <product-version>");
    return 1;
}

var assemblyVersion = AssemblyName.GetAssemblyName(args[0]).Version?.ToString();
var file = FileVersionInfo.GetVersionInfo(args[0]);
var expected = args[1] + ".0";
if (assemblyVersion != expected || file.FileVersion != expected ||
    file.ProductVersion?.Split('+')[0] != args[1])
{
    Console.Error.WriteLine(
        $"DLL version mismatch: AssemblyVersion={assemblyVersion}, " +
        $"FileVersion={file.FileVersion}, ProductVersion={file.ProductVersion}; expected {args[1]}");
    return 1;
}

Console.WriteLine($"Verified DLL versions: AssemblyVersion={expected}, FileVersion={expected}, ProductVersion={file.ProductVersion}");
return 0;
