// Copyright 2021 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuke.Common.Tooling;

namespace Nuke.Common.Tools.CloudFoundry
{
    public static partial class CloudFoundryTasks
    {
        static CloudFoundryTasks()
        {
            CloudFoundryLogger = (type, output) =>
            {
                if (type == OutputType.Std || output == "Instances starting...")
                    Serilog.Log.Debug(output);
                else
                    Serilog.Log.Error(output);
            };
        }
        public static string GetToolPath()
        {
            return NuGetToolPathResolver.GetPackageExecutable($"CloudFoundry.CommandLine.{CurrentOsRid}", IsWindows ? "cf.exe" : "cf");
        }

        private static bool IsWindows => EnvironmentInfo.Platform == PlatformFamily.Windows;

        private static string CurrentOsRid
            => EnvironmentInfo.Platform switch
            {
                PlatformFamily.Windows => Environment.Is64BitOperatingSystem ? "win-x64" : "win-x32",
                PlatformFamily.Linux => Environment.Is64BitOperatingSystem ? "linux-x64" : "linux-x32",
                PlatformFamily.OSX => "osx-x64",
                _ => throw new PlatformNotSupportedException()
            };

        /// <summary>
        ///   Create task which will complete when creation of an asynchronous service is complete.
        ///   This uses polling to query it repeatedly
        /// </summary>
        public static async Task CloudFoundryEnsureServiceReady(string serviceInstance)
        {
            bool IsCreating()
            {
                var commandResult = CloudFoundryGetServiceInfo(c => c
                        .SetServiceInstance(serviceInstance))
                    .First(x => x.Text.StartsWith("status:"))
                    .Text;
                var result = Regex.Match(commandResult, @"(?<=^status:\s+)[a-z].+", RegexOptions.Multiline).Value;
                return result == "create in progress";
            }

            while (IsCreating())
            {
                await Task.Delay(5000);
            }
        }
    }
}
