﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommandLine;

namespace nuget.debugify
{
    [Verb("setup", isDefault:true, HelpText="Replaces *.dlls of a given project in the local nuget .package cache with a debuggable one so that you can actually debug its code")]
    public class DebugCommand
    {
        [Option("verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose{ get; set; }

        [Option('p', "path", Required = false, HelpText = "Path to *.csproj file or a folder that contains it (directly or in subfolder(s))")]
        public string Path { get; set; }

        [Option('v', "version", Required=false,HelpText = "Specify the version you'd like to debugify")]
        public string Version { get; set; }
    }

    internal class Debugifier
    {
        private readonly ILogger _logger;

        public Debugifier(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        public void Debugify(DebugCommand cmd)
        {
            if (!string.IsNullOrWhiteSpace(cmd.Version))
            {
                var versionValidator = new Regex(@"\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?");
                if (!versionValidator.IsMatch(cmd.Version))
                {
                    _logger.Error($"Error: '{cmd.Version}' is not a valid nuget package version");
                    return;
                }
            }

            if (string.IsNullOrEmpty(cmd.Path))
            {
                if(cmd.Verbose) _logger.Debug($"No path provided. Will run in current directory: {Environment.CurrentDirectory}");
                cmd.Path = Environment.CurrentDirectory;
            }
            else 
            {
                if (!Path.IsPathRooted(cmd.Path))
                    cmd.Path = Path.GetFullPath(cmd.Path);
                
                if (cmd.Verbose) _logger.Debug($"Path: {cmd.Path}");

            }

            // find it within the current solution
            var solutionDir = File.Exists(cmd.Path) && string.Equals(Path.GetExtension(cmd.Path), ".sln",
                StringComparison.InvariantCultureIgnoreCase)
                ? Path.GetDirectoryName(cmd.Path)
                : RecursivelyFindSolutionDir(cmd.Path, cmd.Verbose);
            if (solutionDir is null)
            {
                _logger.Warning($"Could not find any solution in '{(File.Exists(cmd.Path) ? Path.GetDirectoryName(cmd.Path) : cmd.Path)}'");
                return;
            }
            if(cmd.Verbose) _logger.Debug($"Found sln directory: {solutionDir}");

            // find all compatible project files
            var projectFiles = EnumerateDebugifiableProjects(cmd.Path).ToList();
            if (!projectFiles.Any())
            {
                _logger.Warning($"Could not find any debugifiable *.csproj files in '{(File.Exists(cmd.Path) ? Path.GetDirectoryName(cmd.Path) : cmd.Path)}'");
                return;
            }

            // replace version in csproj files
            if (!string.IsNullOrWhiteSpace(cmd.Version))
            {
                foreach (var projectFile in projectFiles)
                    projectFile.ActualVersion = cmd.Version;
            }


            // pack the thing up
            var packFailedCount = 0;
            foreach (var projectFile in projectFiles)
            {
                if (!DotnetPack(projectFile, cmd.Version, cmd.Verbose))
                {
                    _logger.Error($"dotnet pack failed for {Path.GetFileName(projectFile.Path)}");
                    packFailedCount++;
                }
            }
            if (packFailedCount != 0)
                _logger.Warning($"WARNING: Failed to create {packFailedCount} of {projectFiles.Count} *.nupkg files");
            

            // resolve nuget package cache
            var pathWithEnv = $@"%USERPROFILE%\.nuget\packages\";
            var packageCachePath = Environment.ExpandEnvironmentVariables(pathWithEnv);

            if(cmd.Verbose) _logger.Info($"nuget package cache fount at '{packageCachePath}'");

            // find each nupkg and extract it to cache
            foreach (var projectFile in projectFiles)
            {
                var packageName = projectFile.NugetPackageName;
                var packagePath = Directory.EnumerateFiles(solutionDir, "*.nupkg", SearchOption.AllDirectories)
                    .FirstOrDefault(p => string.Equals(packageName, Path.GetFileName(p), StringComparison.InvariantCultureIgnoreCase));
                if (packagePath is null)
                {
                    _logger.Error($"Could not find package {packageName}");
                    continue;
                }

                // verify, that there is a nuget package with the same id and version in the cache
                var extractPath = Path.Combine(packageCachePath, projectFile.PackageId, projectFile.Version);
                if (!Directory.Exists(extractPath))
                {
                    _logger.Warning($"Cannot debugify {packageName} as the package cannot be found in the cache: {extractPath}");
                    continue;
                }
                
                // extract nupkg contents into package cache
                ExtractNupkg(cmd, extractPath, packagePath);

                _logger.Success($"Successfully debugified {projectFile.PackageId} version {projectFile.Version}");
            }
            
        }

        private IEnumerable<CsprojInfo> EnumerateDebugifiableProjects(string path)
        {
            var isCsprojFile = File.Exists(path) && string.Equals(Path.GetExtension(path), ".csproj", StringComparison.InvariantCultureIgnoreCase);
            if (isCsprojFile)
            {
                var csproj = path;
                if (TryGetVersion(csproj, out string version))
                {
                    // to be sure that the csproj is nuget-compatible, ensure it is the new csproj format
                    if (!File.ReadLines(csproj).First().Trim().StartsWith("<Project Sdk="))
                        _logger.Warning($"Project file {Path.GetFileName(csproj)} is not supported: Only latest csproj format is supported");
                    else
                    {
                        var packageId = GetPackageId(csproj);

                        yield return new CsprojInfo(packageId, version, csproj);   
                    }
                }
            }
            else
            {
                // ensure path is a directory
                path = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                foreach(var csproj in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
                {
                    if (TryGetVersion(csproj, out string version))
                    {
                        // to be sure that the csproj is nuget-compatible, ensure it is the new csproj format
                        if (!File.ReadLines(csproj).First().Trim().StartsWith("<Project Sdk="))
                            _logger.Warning($"Project file {Path.GetFileName(csproj)} is not supported: Only latest csproj format is supported");
                        else
                        {
                            var packageId = GetPackageId(csproj);

                            yield return new CsprojInfo(packageId, version, csproj);   
                        }
                    }
                }
            }
        }

        private bool TryGetVersion(string filePath, out string version)
        {
            EnsureCsprojFile(filePath);

            var findVersion = new Regex(@"<Version>\s*(?<version>\d+\.\d+\.\d+(\.\d+)?[\d\w_-]*?)\s*</Version>");
            
            var m = findVersion.Match(File.ReadAllText(filePath));
            if (!m.Success)
            {
                version = null;
                return false;
            }

            version = m.Groups["version"].Value;
            return true;
        }

        private string GetPackageId(string filePath)
        {
            EnsureCsprojFile(filePath);

            var findPackageId = new Regex(@"<PackageId>(?<packageId>[\w\.]+)</PackageId>");
            var m = findPackageId.Match(File.ReadAllText(filePath));
            if (m.Success)
                return m.Groups["packageId"].Value;

            return Path.GetFileNameWithoutExtension(filePath);
        }

        private void ExtractNupkg(DebugCommand cmd, string extractPath, string packagePath)
        {
            // add pseudo file to know when do delete such a folder
            if (cmd.Verbose) _logger.Debug($"Writing .debugified.txt to {extractPath}");
            File.WriteAllText(Path.Combine(extractPath, ".debugified.txt"), "this package was debugified");

            using var archive = new ZipArchive(File.OpenRead(packagePath));
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("lib") || entry.FullName.StartsWith("src"))
                {
                    if (cmd.Verbose) _logger.Debug($"Extracting {entry.FullName}");

                    // Gets the full path to ensure that relative segments are removed.
                    string destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));

                    // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that
                    // are case-insensitive.
                    if (destinationPath.StartsWith(extractPath, StringComparison.Ordinal))
                    {
                        // ensure the subdirectories exist
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
                else
                {
                    if (cmd.Verbose) _logger.Debug($"Skipping {entry.FullName} as it does not start with 'src' or 'lib'");
                }
            }
        }

        private string RecursivelyFindSolutionDir(string path, bool verboseLogging)
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

            if(verboseLogging) _logger.Debug("Searching sln directory");

            string sln = null;
            for (int i = 0; i < 10; i++)
            {
                sln = Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                
                // if solution is not found within the current folder, proceed with the next parent folder
                if (sln is null)
                {
                    if(verboseLogging) _logger.Debug($" - No sln in '{dir}'");
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    sln = Path.GetDirectoryName(sln);
                    break;
                }
            }

            if (sln is null)
                _logger.Error("Could not find any *.sln file within {path} or any of its 10 parent folders!");

            return sln;
        }

        private bool DotnetPack(CsprojInfo projectFile, string version, bool verbose)
        {
            var csprojPath = projectFile.Path;
            if (!Path.IsPathRooted(csprojPath))
                throw new ArgumentException($"{nameof(csprojPath)} must be rooted");

            // dotnet pack --include-symbols --include-source -c Debug --force
            // see: https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-pack
            var name = "dotnet";
            //var args = $"pack '{csprojPath}' --include-symbols --include-source -c Debug --force";
            var args = "pack --include-symbols --include-source -c Debug --force";

            if (!string.IsNullOrEmpty(version))
                args += $" -p:PackageVersion={version}";

            var pi = new ProcessStartInfo(name, args);
            pi.RedirectStandardError = true;
            pi.RedirectStandardOutput = true;

            if(verbose)
                _logger.Info($"{name} {args}");
            else
                _logger.Info($"creating {projectFile.NugetPackageName}");
            
            // if the user specified just a folder, set it as working dir
            pi.WorkingDirectory = Path.GetDirectoryName(csprojPath);
            
            var output = new DataReceivedEventHandler((s, e) => 
            {
                if(verbose && !string.IsNullOrEmpty(e.Data))
                    _logger.Verbose(e.Data);
            });
            var error = new DataReceivedEventHandler((s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.Error(e.Data);
            });

            // how to forward process output to console: https://stackoverflow.com/questions/4291912/process-start-how-to-get-the-output
            var process = new Process();
            process.StartInfo = pi;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += output;
            process.ErrorDataReceived += error;
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (verbose)
                    _logger.Debug($"dotnet pack returned {process.ExitCode}");

                // only iif MSBuild returns 0, everything is fine
                return process.ExitCode == 0;
            }
            finally
            {
                process.OutputDataReceived -= output;
                process.ErrorDataReceived -= error;
            }
        }

        private void EnsureCsprojFile(string filePath)
        {
            if (!string.Equals(Path.GetExtension(filePath), ".csproj", StringComparison.InvariantCultureIgnoreCase))
                throw new ArgumentException("filePath must point to *.csproj file");
        }

        [DebuggerDisplay("{" + nameof(NugetPackageName) + "}")]
        private class CsprojInfo
        {
            public string PackageId { get; }
            public string Version { get; private set; }
            public string ActualVersion { get; set; }
            public string Path { get; }

            public CsprojInfo(string packageId, string version, string path)
            {
                PackageId = packageId;
                Version = version;
                Path = path;
                ActualVersion = version;
            }
            public string NugetPackageName => $"{PackageId}.{ActualVersion}.symbols.nupkg";
        }
    }
}
