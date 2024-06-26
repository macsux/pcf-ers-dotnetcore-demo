﻿using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.CloudFoundry;
using Serilog;
using static Tools.CloudFoundry.CloudFoundryExtensions;
using static Nuke.Common.Tools.CloudFoundry.CloudFoundryTasks;

public partial class Build
{
    [Parameter("Cloud Foundry Username")]
    readonly string CfUsername;
    [Parameter("Cloud Foundry Password")]
    readonly string CfPassword;
    [Parameter("Cloud Foundry Endpoint")]
    readonly string CfApiEndpoint;
    [Parameter("Cloud Foundry Org")]
    string CfOrg;
    [Parameter("Cloud Foundry Space")]
    string CfSpace;
    [Parameter("Type of database plan (default: db-small)")]
    readonly string DbPlan = "db-small";
    [Parameter("Type of SSO plan")]
    string SsoPlan;
    [Parameter("Internal domain for c2c. Optional")]
    string InternalDomain = null;
    [Parameter("Public domain to assign to apps. Optional")]
    string PublicDomain;
    [Parameter("Trigger to use to trigger live sync (Build or Source. Default to Source)")]
    SyncTrigger SyncTrigger = SyncTrigger.Source;
    [Parameter("Should CF push be performed when livesync is started. Disabling is quicker, but only works if app was previously deployed for livesync")]
    bool CfPushInit = true;
    
    Target CfLogin => _ => _
        // .OnlyWhenStatic(() => !CfSkipLogin)
        .Requires(() => CfUsername, () => CfPassword, () => CfApiEndpoint)
        .Unlisted()
        .Executes(() =>
        {
            CloudFoundryApi(c => c.SetUrl(CfApiEndpoint));
            CloudFoundryAuth(c => c
                .SetUsername(CfUsername)
                .SetPassword(CfPassword));
        });

    Target CfTarget => _ => _
        .Requires(() => CfSpace, () => CfOrg)
        .Executes(() =>
        {
            CloudFoundryCreateSpace(c => c
                .SetOrg(CfOrg)
                .SetSpace(CfSpace));
            CloudFoundryTarget(c => c
                .SetSpace(CfSpace)
                .SetOrg(CfOrg));
        });

    Target InnerLoop => _ => _
        .Requires(() => AppName)
        .Executes(async () =>
        {
            var currentEnvVars = Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .ToDictionary(x => (string)x.Key, x => (string)x.Value);
            string os = "";
            if (OperatingSystem.IsWindows())
                os = "win";
            else if (OperatingSystem.IsLinux())
                os = "linux";
            else
                os = "osx";

            var tilt = NuGetToolPathResolver.GetPackageExecutable($"Tilt.CommandLine.{os}-x64", "tilt" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var tiltProcess = ProcessTasks.StartProcess(tilt, "up", 
                workingDirectory: RootDirectory, 
                environmentVariables: new Dictionary<string, string>(currentEnvVars)
                {
                    {"APP_NAME", AppName},
                    {"APP_DIR", "./src/CloudPlatformDemo"},
                    {"SYNC_TRIGGER", SyncTrigger.ToString().ToLower()},
                    {"CF_PUSH_INIT", CfPushInit.ToString().ToLower()},
                    {"MainAssemblyName", AssemblyName},
                    // {"PUSH_PATH", "."},
                    // {"PUSH_COMMAND", $"cd ${{HOME}} && ./watchexec --ignore *.yaml --restart --watch . 'dotnet {AssemblyName}.dll --urls http://0.0.0.0:8080'"},
                    {"TILT_WATCH_WINDOWS_BUFFER_SIZE", "555555"}
                });
            await Task.Delay(3000);
            var tiltPsi = new ProcessStartInfo
            {
                FileName = "http://localhost:10350",
                UseShellExecute = true
            };
            Process.Start(tiltPsi);
            
            tiltProcess.WaitForExit();
        });

    struct AppDeployment
    {
        public string Name { get; set; }
        public string Org { get; set; }
        public string Space { get; set; }
        public string Domain { get; set; }
        public string Hostname => $"{Name}-{Space}-{Org}";
        public bool IsInternal { get; set; }
    }

    Target CfEnsureCurrentTarget => _ => _
        .After(CfTarget, Pack)
        .Executes(() =>
        {
            var userProfileDir = (AbsolutePath)Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cfConfigFile = userProfileDir / ".cf" / "config.json";
            var cfConfig = JObject.Parse(File.ReadAllText(cfConfigFile));

            CfOrg = cfConfig.SelectToken($"OrganizationFields.Name")?.Value<string>(); 
            CfSpace = cfConfig.SelectToken($"SpaceFields.Name")?.Value<string>();
            if (CfOrg is null || CfSpace is null)
            {
                Assert.Fail("CF CLI is not set to an org/space");
            }
            var orgGuid = CloudFoundry($"org {CfOrg} --guid", logOutput: false).StdToText();
            PublicDomain = CloudFoundryCurl(o => o
                    .SetPath($"/v3/organizations/{orgGuid}/domains/default")
                    .DisableProcessLogOutput())
                .StdToJson<dynamic>().name;
            InternalDomain = CloudFoundryCurl(o => o
                    .SetPath($"/v3/domains")
                    .DisableProcessLogOutput())
                .ReadPaged<dynamic>()
                .Where(x => x.@internal == true)
                .Select(x => x.name)
                .First();
            
            Assert.True(PublicDomain != null);
            Assert.True(InternalDomain != null);
        });

    Target CfCreateDeploymentPlan => _ => _
        .Unlisted()
        .DependsOn(CfEnsureCurrentTarget)
        .Executes(() =>
        {
            Green = new AppDeployment
            {
                Name = $"{AppName}-green",
                Org = CfOrg,
                Space = CfSpace,
                Domain = PublicDomain
            };
            Blue = Green;
            Blue.Name = $"{AppName}-blue";
            
            Apps = new[] { Green, Blue };
        });
    Target CfDeploy => _ => _
        .After(CfLogin, CfTarget)
        .DependsOn(Pack, CfEnsureCurrentTarget, CfCreateDeploymentPlan)
        .Description("Deploys {AppsCount} instances to Cloud Foundry /w all dependency services into current target set by cli")
        .Executes(async () =>
        {

            var marketplace = CloudFoundry("marketplace", logOutput: false).StdToText();
            var hasMySql = marketplace.Contains("p.mysql");
            var hasDiscovery = marketplace.Contains("p.service-registry");
            var hasSso = marketplace.Contains("p-identity");
            if (hasSso && SsoPlan == null)
            {
                SsoPlan = Regex.Match(marketplace, @"(?<=^p-identity\s+)[^\s]+", RegexOptions.Multiline).Value;
            }

            if (hasDiscovery)
            {
                CloudFoundryCreateService(c => c
                    .SetService("p.service-registry")
                    .SetPlan("standard")
                    .SetInstanceName("eureka"));
            }
            else
            {
                Log.Warning("Service registry not detected in marketplace. Some demos will not be available");
            }

            if (hasMySql)
            {
                CloudFoundryCreateService(c => c
                    .SetService("p.mysql")
                    .SetPlan(DbPlan)
                    .SetInstanceName("mysql"));
            }
            else
            {
                Log.Warning("MySQL not detected in marketplace. Some demos will not be available");
            }

            if (hasSso)
            {
                CloudFoundryCreateService(c => c
                    .SetService("p-identity")
                    .SetPlan(SsoPlan)
                    .SetInstanceName("sso"));
            }
            else
            {
                Log.Warning("SSO not detected in marketplace. Some demos will not be available");
            }
            
            CloudFoundryPush(c => c
                .EnableNoRoute()
                .EnableNoStart()
                .SetMemory("384M")
                .SetBuildpack("dotnet_core_buildpack")
                .SetPath(ArtifactsDirectory / PackageZipName)
                .CombineWith(Apps,(push,app) =>
                {
                    
                    push = push
                        .SetAppName(app.Name);

                    return push;
                }), degreeOfParallelism: 3);
            
            CloudFoundryMapRoute(c => c
                .CombineWith(Apps, (cf,app) => cf
                    .SetAppName(app.Name)
                    .SetDomain(app.Domain)
                    .SetHostname(app.Hostname))
                , degreeOfParallelism: 3);


            if (hasDiscovery)
                await CloudFoundryEnsureServiceReady("eureka");
            if(hasMySql)
                await CloudFoundryEnsureServiceReady("mysql");
            if(hasSso)
                await CloudFoundryEnsureServiceReady("sso");

            
            CloudFoundryBindService(c => c
                .SetServiceInstance("eureka")
                .CombineWith(Apps, (cf,app) => cf
                    .SetAppName(app.Name)), degreeOfParallelism: 3);

            CloudFoundryBindService(c => c
                .SetServiceInstance("mysql")
                .CombineWith(Apps, (cf, app) => cf
                    .SetAppName(app.Name)), degreeOfParallelism: 3);

            CloudFoundryBindService(c => c
                .SetServiceInstance("sso")
                .SetConfigurationParameters(RootDirectory / "sso-binding.json")
                .CombineWith(Apps, (cf, app) => cf
                    .SetAppName(app.Name)), degreeOfParallelism: 3);
            

            CloudFoundryStart(c => c
                .CombineWith(Apps, (cf, app) => cf
                    .SetAppName(app.Name)), degreeOfParallelism: 3);
        });

    Target CfDeleteApps => _ => _
        .After(CfLogin, CfTarget)
        .DependsOn(CfEnsureCurrentTarget, CfCreateDeploymentPlan)
        .Executes(() =>
        {
            CloudFoundryDeleteApplication(c => c
                .EnableDeleteRoutes()
                .CombineWith(Apps, (cf, app) => cf
                    .SetAppName(app.Name)));
        });
}