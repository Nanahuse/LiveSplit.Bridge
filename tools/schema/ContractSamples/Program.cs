using System;
using System.IO;

var outputDirectory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDirectory);
ContractSamples.Write(outputDirectory);
